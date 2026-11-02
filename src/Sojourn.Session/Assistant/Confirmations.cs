using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Assistant;

/// <summary>
/// The confirmation gate (LeapDispatcher.Gate): spending L$, deleting, giving things away, changing permissions and
/// putting new scripts into objects wait until the person presses confirm on the panel with their own hand (a
/// controller press or a click). The client describes the action from the op's own arguments, resolved the way the op
/// resolves them, so what the person confirms is what will happen, whatever the caller said about it. Nothing on the
/// agent API can answer; only <see cref="Answer"/>, called from input. While a confirmation waits, the dispatcher holds
/// every other op. One confirmation at a time; others queue behind it. Unanswered after two minutes, it's refused.
/// Touching someone else's object, or answering its menu, waits too while the person's setting asks for it (on by
/// default): vendors, rental boxes and tip jars once granted debit permission can take L$ that way, and the client
/// can't tell which hold it. Only ops on the agent API are gated; the person's own hand doesn't go through here.
/// </summary>
public sealed partial class Confirmations
{
    readonly AgentSession _s;
    readonly Panel _panel;
    readonly bool _uploadsCost;
    readonly SemaphoreSlim _one = new(1, 1);
    // names things had before an op renamed them this session, so a confirmation can't pass off one thing as another
    readonly ConcurrentDictionary<UUID, string> _wasNamed = new();
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    // the lists a confirmation being described names only the start of (Listed), gathered for its list key
    static readonly AsyncLocal<List<ConfirmList>?> Gathered = new();

    public Confirmations(AgentSession session, Panel panel, bool uploadsCost)
    {
        _s = session;
        _panel = panel;
        _uploadsCost = uploadsCost;
    }

    public bool Pending => _panel.Read(s => s.Confirm != null);

    /// <summary>The person's setting: touches and menu answers on objects they don't own wait for them.</summary>
    public Func<bool> ConfirmTouches { get; set; } = () => true;

    /// <summary>More of the touch rule from elsewhere (Media/: the assistant's presses and typing in pages that aren't the
    /// person's own): what to ask, or null to let the op run. Asked only while the person's setting asks for touches.</summary>
    public Func<string, string, JsonObject, (string Title, string Detail)?>? OtherTouches { get; set; }

    /// <summary>An abuse report's words as the person is shown them before it's sent (Control/ReportApi.cs), or null
    /// for a draft that doesn't exist. Every send waits for the person, whatever their settings.</summary>
    public Func<JsonObject, (string Title, string Detail)?>? Report { get; set; }

    /// <summary>The person's answer to a confirmation they were shown (input thread). Ignored unless it's still the
    /// one showing.</summary>
    public void Answer(PendingConfirm which, bool yes)
    {
        if (_panel.Read(s => s.Confirm) == which) which.Answer.TrySetResult(yes);
    }

    /// <summary>The gate: null to run at once, else the ask (the wait for the person).</summary>
    public Func<Task<string?>>? Check(string pump, string op, JsonObject data)
    {
        Canonicalise(pump, op, data);
        // what an op would refuse anyway is refused before the person is asked (Inventory/InventoryWiring.cs: protected folders)
        foreach (var refuse in Refusals)
            if (refuse(pump, op, data) is { } no) return () => Task.FromResult<string?>(no);
        if (pump == "TesselAgent" && op == "pay" && PayCheck is null && Unaffordable(data) is { } why)
            return () => Task.FromResult<string?>(why);
        if (pump == "SojournReport" && op == "send" && Report?.Invoke(data) is null)
            return () => Task.FromResult<string?>("no such draft; nothing was sent");
        var kind = Classify(pump, op, data);
        if (kind is null)
            foreach (var c in Cases)
                if (c.Kind(pump, op, data) is { } named && Enum.TryParse<Kind>(named, true, out var ck))
                    return () => AskAsync(ck, pump, op, data, c.Describe);
        if (kind is Kind.Spend && pump == "TesselAgent" && op == "pay" && PayCheck is { } payCheck)
            return async () =>
            {
                var (no, note) = await payCheck(data);
                // an amount the object wouldn't take says so first; then whether it's affordable
                return no ?? Unaffordable(data) ?? await AskAsync(Kind.Spend, pump, op, data, note: note);
            };
        // (an upload's fee worked out and pinned first: Confirmations.Uploads.cs)
        if (kind is Kind.Spend && pump == "TesselAgent" && op is "upload" or "uploadMesh" or "uploadClips")
            return async () => await UploadFeeAsync(op, data) ?? await AskAsync(Kind.Spend, pump, op, data);
        return kind is { } k ? () => AskAsync(k, pump, op, data) : null;
    }

    /// <summary>A confirmation another part of the client adds for its own ops (People/PeopleWiring.cs): the kind of
    /// thing an op does (spend, delete, give, permissions, words; null: it runs at once) and what the person is asked.</summary>
    public sealed record Case(Func<string, string, JsonObject, string?> Kind, Func<string, string, JsonObject, Task<(string Title, string Detail)>> Describe);
    public List<Case> Cases { get; } = new();

    /// <summary>Refusals other parts of the client add for ops that would fail once run (a protected folder deleted):
    /// the reason, or null. Asked before anything waits for the person.</summary>
    public List<Func<string, string, JsonObject, string?>> Refusals { get; } = new();

    /// <summary>Before a pay is asked: a refusal (an object's pay window wouldn't offer that amount), or a note for the
    /// confirmation (its pay buttons). People/PeopleWiring.cs.</summary>
    public Func<JsonObject, Task<(string? Refusal, string Note)>>? PayCheck { get; set; }

    enum Kind { Spend, Delete, Give, Permissions, Script, Touch, Open, Report, Words }

    static readonly HashSet<string> PermKeys = new() { "next_perms", "everyone_perms", "group_perms", "sale" };

    /// <summary>Before anything runs: pin what the op will act on, and remember names an op is about to change.</summary>
    void Canonicalise(string pump, string op, JsonObject a)
    {
        switch (pump, op)
        {
            case ("LLNotifications", "respond" or "cancel"):
                // a short prefix could match a different notification by the time the op runs
                if (_s.Notify.Resolve(a.Str("uuid")) is { } nid) a["uuid"] = nid.ToString();
                break;
            case ("LLAgent", "requestTouch") when !a.Has("obj_uuid") && a.Vec3("position") is { } pos && _s.Region is { } w:
            {
                // the prim the op would pick, pinned, so what's checked and confirmed is what gets touched
                var near = w.Prims.Values
                    .Where(p => p.PrimData.PCode == PCode.Prim && !_s.Objects.IsAttachment(w, p, out _, out _))
                    .OrderBy(p => Vector3.Distance(w.RegionPosition(p), pos)).FirstOrDefault();
                if (near != null) a["obj_uuid"] = near.ID.ToString();
                break;
            }
            case ("TesselInventory", "update") when a.Has("name"):
                if (InventoryTarget(a) is { } iid && _s.Inventory.Node(iid) is { } node) _wasNamed.TryAdd(iid, node.Name);
                break;
            case ("TesselBuild", "modify") when a.Has("name"):
                foreach (var id in Ids(a))
                    if (_s.Region?.PrimById(id)?.Properties?.Name is { Length: > 0 } pn) _wasNamed.TryAdd(id, pn);
                break;
        }
    }

    string? Unaffordable(JsonObject a)
    {
        int amount = a.Int("amount", 0);
        return _s.BalanceKnown && _s.Client is { } c && c.Self.Balance < amount
            ? $"can't afford L${amount} (balance L${c.Self.Balance}); nothing was done" : null;
    }

    Kind? Classify(string pump, string op, JsonObject a) => (pump, op) switch
    {
        ("TesselAgent", "pay") => Kind.Spend,
        ("TesselObjects", "buy") or ("TesselGroups", "create") => Kind.Spend,
        ("TesselAgent", "upload" or "uploadMesh" or "uploadClips") => UploadCosts(op, a) ? Kind.Spend : null,
        ("TesselBuild", "delete" or "returnObjects" or "removeFromContents") => Kind.Delete,
        ("TesselBuild", "take") => a.Has("folder_id") && InTrash(a.Uuid("folder_id")) ? Kind.Delete : null,
        ("TesselInventory", "move") => InTrash(a.Uuid("folder_id")) ? Kind.Delete : null,
        ("TesselInventory", "delete" or "emptyTrash") => Kind.Delete,
        ("TesselAgent", "parcelReturn" or "deletePick") or ("TesselGroups", "deleteRole") => Kind.Delete,
        // temporary objects are deleted by the region within a minute
        ("TesselBuild", "modify") when a.Bool("temp", false) => Kind.Delete,
        ("TesselBuild", "rez") when a.Bool("temp", false) && !Copyable(a.Uuid("item_id")) => Kind.Delete,
        ("TesselAgent", "give") => Kind.Give,
        ("TesselBuild", "addToContents") => !OwnedByMe(ContentsOps.PrimId(a)) ? Kind.Give : AddsScript(a) ? Kind.Script : null,
        ("TesselGroups", "notice") => a.Uuid("item_id") is not null ? Kind.Give : null,
        ("TesselBuild", "modify" or "create" or "duplicate" or "rez") => a.Select(kv => kv.Key).Any(PermKeys.Contains) ? Kind.Permissions : null,
        ("TesselInventory", "update") => a.Has("next_perms") || a.Has("everyone_copy") || a.Has("group_perms") ? Kind.Permissions : null,
        ("TesselAgent", "parcelSet" or "parcelEject") => Kind.Permissions,
        ("TesselGroups", "update" or "createRole" or "updateRole" or "assignRole" or "unassignRole" or "eject") => Kind.Permissions,
        ("TesselGroups", "invite") => InviteRoles(a).Any(r => r != UUID.Zero) ? Kind.Permissions : null,
        ("LLNotifications", "respond") => NotificationKind(a) ?? (ConfirmTouches() && OthersMenu(a) ? Kind.Touch : null),
        ("SojournReport", "send") => Kind.Report,
        ("LLAgent", "requestTouch") => ConfirmTouches() && !OwnedByMe(a.Uuid("obj_uuid")) ? Kind.Touch : null,
        // scripts act as the person in-world (they can give away or delete what's in their object, or the object)
        ("ScriptEditor", "object.item.create") => (a.Str("type") ?? "script") == "script" ? Kind.Script : null,
        ("ScriptEditor", "object.content.save") => Kind.Script,
        _ => ConfirmTouches() && OtherTouches?.Invoke(pump, op, a) != null ? Kind.Touch : null,
    };

    /// <summary>Answers that grant something: joining a group with a fee, a script's permission request (debit above
    /// all). Classified by the button the response would actually press (Notifications.Choose).</summary>
    Kind? NotificationKind(JsonObject a)
    {
        if (_s.Notify.Resolve(a.Str("uuid")) is not { } id || _s.Notify.Get(id) is not { } n) return null;
        return (n.Str("name"), _s.Notify.Choose(id, a["response"])) switch
        {
            ("JoinGroupCanAfford", "OK_okcancelbuttons") => Kind.Spend,
            ("ScriptQuestion" or "ScriptQuestionCaution" or "ScriptQuestionExperience", "Yes") => Kind.Permissions,
            // an object's web page (llLoadURL) opens only by the person's own press: loading it tells its server where they are
            ("LoadWebPage", "Gotopage") => Kind.Open,
            _ => null,
        };
    }

    /// <summary>A button pressed on a script's menu from an object someone else owns (not Block or Ignore).</summary>
    bool OthersMenu(JsonObject a)
    {
        if (_s.Notify.Resolve(a.Str("uuid")) is not { } id || _s.Notify.Get(id) is not { } n) return false;
        if (n.Str("name") is not ("ScriptDialog" or "ScriptDialogGroup")) return false;
        if (_s.Notify.Choose(id, a["response"]) is null or "Client_Side_Mute" or "Client_Side_Ignore") return false;
        var owner = (n["payload"] as JsonObject)?.Uuid("owner_id");
        return owner is null || _s.Client is not { } c || owner != c.Self.AgentID;
    }

    static List<UUID> Ids(JsonObject a)
    {
        var ids = a.UuidList("ids");
        if (a.Uuid("id") is { } one && !ids.Contains(one)) ids.Add(one);
        return ids;
    }

    static UUID? InventoryTarget(JsonObject a) => a.Uuid("id") ?? a.Uuid("item_id") ?? a.Uuid("folder_id");

    static List<UUID> InviteRoles(JsonObject a)
    {
        var roles = a.UuidList("role_ids");
        if (a.Uuid("role_id") is { } r) roles.Add(r);
        return roles;
    }

    /// <summary>A folder in Trash (or one we can't place, to be safe).</summary>
    bool InTrash(UUID? folder)
    {
        if (folder is not { } cur) return false;
        var trash = _s.Inventory.FolderFor(FolderType.Trash);
        for (int depth = 0; depth < 64 && cur != UUID.Zero; depth++)
        {
            if (cur == trash) return true;
            if (_s.Inventory.Node(cur) is not { } n) return true;
            cur = n.ParentUUID;
        }
        return false;
    }

    bool Copyable(UUID? itemId) =>
        itemId is { } id && _s.Inventory.Node(id) is InventoryItem it && (it.Permissions.OwnerMask & PermissionMask.Copy) != 0;

    List<UUID> ItemIds(JsonObject a)
    {
        var ids = a.UuidList("item_ids");
        if (a.Uuid("item_id") is { } one && !ids.Contains(one)) ids.Add(one);
        return ids;
    }

    InventoryItem? Resolved(UUID id) =>
        _s.Inventory.Node(id) is InventoryItem it && it.AssetType == AssetType.Link && _s.Inventory.Node(it.AssetUUID) is InventoryItem t ? t : _s.Inventory.Node(id) as InventoryItem;

    bool AddsScript(JsonObject a) => ItemIds(a).Any(id => Resolved(id) is not { } it || it.AssetType == AssetType.LSLText);

    bool OwnedByMe(UUID? id)
    {
        if (id is not { } oid || _s.Region?.PrimById(oid) is not { } p || _s.Client is not { } c) return false;
        return p.Properties?.OwnerID == c.Self.AgentID || (_s.State.PrimFlagsOf(p) & PrimFlags.ObjectYouOwner) != 0;
    }

    async Task<string?> AskAsync(Kind kind, string pump, string op, JsonObject a,
        Func<string, string, JsonObject, Task<(string Title, string Detail)>>? describe = null, string? note = null)
    {
        await _one.WaitAsync();
        try
        {
            var lists = new List<ConfirmList>();
            Gathered.Value = lists;
            var (title, detail) = describe != null ? await describe(pump, op, a) : await DescribeAsync(kind, pump, op, a);
            if (note is { Length: > 0 }) detail = Join(detail, note);
            var pending = new PendingConfirm { Title = title, Detail = detail, For = a, Lists = lists };
            _panel.Update(s => s.Confirm = pending);
            bool yes;
            // two minutes from when it came up, or from the person last opening or closing its list (reading it)
            while (true)
            {
                var left = Timeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - pending.KeptAt);
                if (left <= TimeSpan.Zero) { yes = false; break; }
                try { yes = await pending.Answer.Task.WaitAsync(left); break; }
                catch (TimeoutException) { }
            }
            pending.Answer.TrySetResult(false);   // refused by the clock: its list, if open, closes with it
            _panel.Update(s =>
            {
                if (s.Confirm == pending) s.Confirm = null;
                s.Status = yes ? Lang.F("confirmed: {0}", title) : Lang.F("not done: {0}", title);
            });
            return yes ? null : $"the person didn't confirm ({kind.ToString().ToLowerInvariant()}: {title}); nothing was done";
        }
        finally { _one.Release(); }
    }

    // --- what the person is asked -----------------------------------------------------------------------------------

    const string TouchRisk = "Not yours: if you've ever let it take L$ (a vendor, a rental box, a tip jar), touching it or answering its menu can.";

    const string ScriptRisk = "A script acts as you in-world: it can give away or delete what's in that object, the object itself, and anything linked to it later.";

    async Task<(string Title, string Detail)> DescribeAsync(Kind kind, string pump, string op, JsonObject a)
    {
        switch (pump, op)
        {
            case ("TesselAgent", "pay"):
            {
                var to = a.Uuid("to");
                string who = to is { } id ? await NameAsync(id) : Lang.T("nobody");
                var desc = a.Str("desc") is { Length: > 0 } d ? Lang.F("Note: {0}", d) : "";
                return (Lang.F("Pay L${0} to {1}", a.Int("amount", 0), who), Join(desc, Balance()));
            }
            case ("TesselObjects", "buy"):
            {
                var id = a.Uuid("id");
                var props = id is { } oid ? _s.Region?.PrimById(oid)?.Properties : null;
                // the op checks the price asked against the object's own and buys at the object's
                var price = props?.SalePrice ?? a.Int("price", 0);
                var name = await NameAsync(id);
                return (props?.SaleType switch
                {
                    SaleType.Copy => Lang.F("Buy a copy of {0} for L${1}", name, price),
                    SaleType.Contents => Lang.F("Buy the contents of {0} for L${1}", name, price),
                    _ => Lang.F("Buy {0} for L${1}", name, price),
                }, Balance());
            }
            case ("TesselGroups", "create"):
                return (Lang.F("Create the group \"{0}\"", a.Str("name")), Join(Lang.T("On Second Life creating a group costs L$100."), Balance()));
            case ("TesselAgent", "upload" or "uploadMesh" or "uploadClips"):
                return UploadAsk(a);
            case ("TesselBuild", "delete"):
                return (Lang.F("Delete {0}", await ObjectsAsync(a)), Lang.T("They go to your Trash."));
            case ("TesselBuild", "take"):
                return (Lang.F("Delete {0}", await ObjectsAsync(a)), Lang.T("Taken into your Trash."));
            case ("TesselBuild", "returnObjects"):
                return (Lang.F("Return {0} to their owners", await ObjectsAsync(a)), "");
            case ("TesselBuild", "removeFromContents"):
            {
                var (obj, item) = await TaskItemAsync(a);
                return (Lang.F("Remove {0} from inside {1}", item, obj), Lang.T("It's deleted from the object."));
            }
            case ("TesselInventory", "move"):
                return (Lang.F("Move to Trash: {0}", ItemNames(Ids(a))), "");
            case ("TesselInventory", "delete"):
            {
                var names = ItemNames(Ids(a));
                var worn = Lang.Tn(_s.InventoryOps.WornNote(a)) ?? "";
                return a.Bool("purge", false) ? (Lang.F("Delete for good: {0}", names), Join(Lang.T("Purged items can't be recovered."), worn)) : (Lang.F("Move to Trash: {0}", names), worn);
            }
            case ("TesselInventory", "emptyTrash"):
                return (Lang.T("Empty your Trash"), Lang.T("Everything in it is deleted for good."));
            case ("TesselAgent", "parcelReturn"):
                return (Lang.F("Return objects on {0} to their owners", await ParcelAsync()), a.Bool("others", false) ? Lang.T("Everyone's but yours.") : await OwnersAsync(a));
            case ("TesselAgent", "deletePick"):
                return (Lang.T("Delete one of your profile picks"), "");
            case ("TesselGroups", "deleteRole"):
                return (Lang.T("Delete a group role"), "");
            case ("TesselBuild", "modify") when kind == Kind.Delete:
                return (Lang.F("Make {0} temporary", await ObjectsAsync(a)), Join(Lang.T("The region deletes temporary objects within a minute, for good (not to Trash)."), PermDetail(a)));
            case ("TesselBuild", "rez") when kind == Kind.Delete:
                return (Lang.F("Rez {0} as a temporary object", ItemName(a.Uuid("item_id"))),
                    Join(Lang.T("It's no-copy: it leaves your inventory, and the region deletes it within a minute, for good."), PermDetail(a)));
            case ("TesselAgent", "give"):
            {
                var who = await NameAsync(a.Uuid("to"));
                var note = a.Uuid("folder_id") is { } fid ? FolderNote(fid) : NoCopyNote(a.Uuid("item_id"));
                return (a.Uuid("folder_id") is { } f ? Lang.F("Give the folder {0} to {1}", ItemName(f), who) : Lang.F("Give {0} to {1}", ItemName(a.Uuid("item_id")), who), note);
            }
            case ("TesselBuild", "addToContents") when kind == Kind.Script:
                return (Lang.F("Put {0} into {1} and run it", ItemNames(ItemIds(a)), await NameAsync(ContentsOps.PrimId(a))), Lang.T(ScriptRisk));
            case ("TesselBuild", "addToContents"):
                return (Lang.F("Put {0} into {1}", ItemNames(ItemIds(a)), await NameAsync(ContentsOps.PrimId(a))), Lang.T("That object isn't yours: the items become its owner's."));
            case ("TesselGroups", "notice"):
                return (Lang.F("Send a group notice with {0} attached", ItemName(a.Uuid("item_id"))), Lang.T("Every member gets a copy."));
            case ("TesselGroups", "invite"):
                return (Lang.F("Invite {0} to {1} in {2}", await NameAsync(a.Uuid("to")), GroupName(a.Uuid("group_id")), await RoleNamesAsync(a)),
                    Lang.T("A role can carry powers in the group, up to an owner's."));
            case ("TesselBuild", _):
                return (Lang.F("Change permissions of {0}", await ObjectsAsync(a)), PermDetail(a));
            case ("TesselInventory", "update"):
                return (Lang.F("Change permissions of {0}", ItemName(InventoryTarget(a))), PermDetail(a));
            case ("TesselAgent", "parcelSet"):
                return (Lang.F("Change the settings of {0}", await ParcelAsync()), string.Join(", ", a.Select(kv => $"{kv.Key}: {Short(kv.Value)}").Where(s => !s.StartsWith("op:"))));
            case ("TesselAgent", "parcelEject"):
            {
                var who = await NameAsync(a.Uuid("to"));
                return (a.Bool("ban", false) ? Lang.F("Eject and ban {0} from your parcel", who) : Lang.F("Eject {0} from your parcel", who), "");
            }
            case ("TesselGroups", _):
                return (Lang.F("Change group roles or settings ({0}) in {1}", op, GroupName(a.Uuid("group_id"))), string.Join(", ", a.Where(kv => kv.Key != "op").Select(kv => $"{kv.Key}: {Short(kv.Value)}")));
            case ("LLAgent", "requestTouch"):
                return (Lang.F("Touch {0}", await NameAsync(a.Uuid("obj_uuid"))), Lang.T(TouchRisk));
            case ("LLNotifications", "respond") when kind == Kind.Open:
            {
                var n = _s.Notify.Resolve(a.Str("uuid")) is { } id ? _s.Notify.Get(id) : null;
                var p = n?["payload"] as JsonObject;
                var url = p?.Str("url") ?? "";
                string owner = p?.Uuid("owner_id") is { } oid ? await NameAsync(oid) : Lang.T("someone");
                return (Lang.F("Open the web page {0}", url.Length > 90 ? url[..90] + "..." : url),
                    Lang.F("Offered by {0}, owned by {1}. The site sees your address.", p?.Str("object_name") ?? Lang.T("an object"), owner));
            }
            case ("LLNotifications", "respond") when kind == Kind.Touch:
            {
                var id = _s.Notify.Resolve(a.Str("uuid"));
                var n = id is { } nid ? _s.Notify.Get(nid) : null;
                var subs = n?["substitutions"] as JsonObject;
                var button = id is { } bid ? _s.Notify.Choose(bid, a["response"]) : null;
                var menu = subs?.Str("TITLE") ?? Lang.T("an object");
                return (button == "!!llTextBox!!" ? Lang.F("Send text to {0}'s menu", menu) : Lang.F("Press \"{0}\" on {1}'s menu", button, menu),
                    Join(Lang.F("Owned by {0}.", subs?.Str("NAME") ?? subs?.Str("GROUPNAME") ?? Lang.T("someone else")), Lang.T(TouchRisk)));
            }
            case ("LLNotifications", "respond"):
            {
                var n = _s.Notify.Resolve(a.Str("uuid")) is { } id ? _s.Notify.Get(id) : null;
                var name = n?.Str("name");
                var subs = n?["substitutions"] as JsonObject;
                if (name == "JoinGroupCanAfford")
                    return (Lang.T("Join the group and pay its fee"), Join(n?.Str("message") ?? "", Balance()));
                string obj = subs?.Str("OBJECTNAME") ?? Lang.T("an object"), owner = subs?.Str("NAME") ?? Lang.T("someone");
                if (name == "ScriptQuestionExperience")
                    return ((n?["payload"] as JsonObject)?.Str("experience_name") is { Length: > 0 } en ? Lang.F("Join the experience \"{0}\"", en) : Lang.T("Join an experience"),
                        Lang.F("Asked by {0} (owned by {1}). Its scripts can then, wherever it runs, without asking again: {2}", obj, owner,
                            (subs?.Str("QUESTIONS") ?? "").Replace("\n", "; ").Trim(' ', ';')));
                return (Lang.F("Give {0} (owned by {1}) permission to:", obj, owner),
                    subs?.Str("QUESTIONS") ?? n?.Str("message") ?? "");
            }
            case ("SojournReport", "send") when Report?.Invoke(a) is { } report:
                return report;
            case ("ScriptEditor", "object.item.create"):
                return (Lang.F("Put a new script \"{0}\" into {1} and run it", a.Str("name"), await NameAsync(a.Uuid("prim_id"))), Lang.T(ScriptRisk));
            case ("ScriptEditor", "object.content.save"):
            {
                var (obj, item) = await TaskItemAsync(new JsonObject { ["id"] = a.Str("prim_id"), ["item_id"] = a.Str("item_id") });
                return (Lang.F("Replace what's in {0} inside {1}", item, obj), Lang.T(ScriptRisk));
            }
        }
        if (OtherTouches?.Invoke(pump, op, a) is { } other) return other;
        return ($"{pump}.{op}", Short(a));
    }

    static string Join(params string[] parts) => string.Join(" ", parts.Where(p => p.Length > 0));

    string Balance() => _s.BalanceKnown && _s.Client is { } c ? Lang.F("Your balance: L${0}.", c.Self.Balance) : "";

    static string Short(JsonNode? n)
    {
        var s = n?.ToJsonString() ?? "";
        return s.Length > 80 ? s[..80] + "..." : s;
    }

    static string PermDetail(JsonObject a)
    {
        var parts = new List<string>();
        if (a.Str("next_perms") is { } np) parts.Add(Lang.F("next owner can: {0}", Letters(np)));
        if (a.Str("everyone_perms") is { } ep) parts.Add(Lang.F("everyone can: {0}", Letters(ep)));
        if (a.Str("group_perms") is { } gp) parts.Add(Lang.F("group can: {0}", Letters(gp)));
        if (a.Has("everyone_copy")) parts.Add(Lang.F("everyone can copy: {0}", Lang.T(a.Bool("everyone_copy", false) ? "yes" : "no")));
        if (a["sale"] is JsonObject so) parts.Add(Lang.F("for sale: L${0} ({1})", so.Int("price", 0), Lang.T(so.Str("type") ?? "original", "sale")));
        else if (a["sale"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && !b) parts.Add(Lang.T("not for sale"));
        return string.Join("; ", parts);
    }

    static string Letters(string mct)
    {
        var words = new List<string>();
        if (mct.Contains('M', StringComparison.OrdinalIgnoreCase)) words.Add(Lang.T("modify"));
        if (mct.Contains('C', StringComparison.OrdinalIgnoreCase)) words.Add(Lang.T("copy"));
        if (mct.Contains('T', StringComparison.OrdinalIgnoreCase)) words.Add(Lang.T("transfer"));
        return words.Count == 0 ? Lang.T("nothing") : string.Join(", ", words);
    }

    string NoCopyNote(UUID? itemId) =>
        itemId is { } id && _s.Inventory.Node(id) is InventoryItem it && (it.Permissions.OwnerMask & PermissionMask.Copy) == 0
            ? Lang.T("It's no-copy: it leaves your inventory.") : "";

    string FolderNote(UUID folderId)
    {
        if (_s.Client?.Inventory.Store is not { } store || !store.Contains(folderId)) return "";
        var items = store.GetContents(folderId).OfType<InventoryItem>().ToList();
        int noCopy = items.Count(i => (i.Permissions.OwnerMask & PermissionMask.Copy) == 0);
        bool one = items.Count == 1, folders = store.GetContents(folderId).OfType<InventoryFolder>().Any();
        return (noCopy > 0, folders) switch
        {
            (false, false) => Lang.F(one ? "{0} item in it." : "{0} items in it.", items.Count),
            (false, true) => Lang.F(one ? "{0} item in it, and the folders inside it." : "{0} items in it, and the folders inside it.", items.Count),
            (true, false) => Lang.F(one ? "{0} item in it, {1} no-copy (they leave your inventory)." : "{0} items in it, {1} no-copy (they leave your inventory).", items.Count, noCopy),
            (true, true) => Lang.F(one ? "{0} item in it, {1} no-copy (they leave your inventory), and the folders inside it."
                : "{0} items in it, {1} no-copy (they leave your inventory), and the folders inside it.", items.Count, noCopy),
        };
    }

    /// <summary>A thing's name in quotes, and what it was called before if an op renamed it this session.</summary>
    string Quoted(UUID id, string now) =>
        _wasNamed.TryGetValue(id, out var was) && was != now ? Lang.F("\"{0}\" (renamed from \"{1}\" this session)", now, was) : $"\"{now}\"";

    /// <summary>A quoted object's name, with its parts when it's a linkset.</summary>
    static string WithParts(string quoted, int parts) => parts > 1 ? Lang.F("{0} ({1} parts)", quoted, parts) : quoted;

    string ItemName(UUID? id) => id is { } i && _s.Inventory.Node(i) is { } n ? Quoted(i, n.Name) : Lang.T("an item");

    string ItemNames(List<UUID> ids) => Listed(ids.Select(i => ItemName(i)).ToList(), 6, "items");

    /// <summary>Names as a confirmation shows them: the first few, then "and N more". A list cut short is kept whole
    /// for the confirmation's list key (the reader) and screen readers, as "N {what}" (each what a catalog template,
    /// "{0} items"). For the descriptions other parts of the client add (<see cref="Cases"/>) too.</summary>
    public static string Listed(IReadOnlyList<string> names, int shown, string what)
    {
        if (names.Count == 0) return Lang.T("nothing");
        if (names.Count <= shown) return string.Join(", ", names);
        Gathered.Value?.Add(new ConfirmList(Lang.F("{0} " + what, names.Count), names.ToList()));
        return Lang.F("{0} and {1} more", string.Join(", ", names.Take(shown)), names.Count - shown);
    }

    string GroupName(UUID? id) => id is { } g && _s.State.Groups.TryGetValue(g, out var grp) ? Lang.F("the group \"{0}\"", grp.Name) : Lang.T("a group");

    async Task<string> RoleNamesAsync(JsonObject a)
    {
        var want = InviteRoles(a).Where(r => r != UUID.Zero).Select(r => r.ToString()).ToHashSet();
        try
        {
            var got = await _s.Api.CallAsync("TesselGroups", new JsonObject { ["op"] = "roles", ["group_id"] = a.Str("group_id") }, true, TimeSpan.FromSeconds(8), default);
            var names = ((got as JsonObject)?["roles"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Where(r => want.Contains(r.Str("role_id") ?? "")).Select(r => $"\"{r.Str("name")}\"").ToList();
            if (names.Count == want.Count) return Lang.F(names.Count == 1 ? "the role {0}" : "the roles {0}", string.Join(", ", names));
        }
        catch { }
        return want.Count == 1 ? Lang.T("a role") : Lang.F("{0} roles", want.Count);
    }

    async Task<(string Object, string Item)> TaskItemAsync(JsonObject a)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var (prim, item) = await _s.Contents.ResolveAsync(a, cts.Token);
            return (await NameAsync(prim.ID), $"\"{item.Name}\" ({Lang.T(AgentInventory.AssetTypeLabel(item.AssetType), "type")})");
        }
        catch { return (await NameAsync(ContentsOps.PrimId(a)), Lang.T("an item (not found: this will likely fail)")); }
    }

    async Task<string> ParcelAsync()
    {
        try
        {
            var got = await _s.Api.CallAsync("TesselAgent", new JsonObject { ["op"] = "parcel" }, true, TimeSpan.FromSeconds(6), default);
            if ((got as JsonObject)?["parcel"] is JsonObject p && p.Str("name") is { Length: > 0 } n) return Lang.F("the parcel \"{0}\"", n);
        }
        catch { }
        return Lang.T("this parcel");
    }

    async Task<string> ObjectsAsync(JsonObject a)
    {
        var ids = Ids(a);
        if (ids.Count == 0) return Lang.T("a new object");
        var names = new List<string>();
        foreach (var id in ids.Take(5)) names.Add(await NameAsync(id));
        if (ids.Count > 5) names.AddRange(await RestNamesAsync(ids.Skip(5).ToList()));
        return Listed(names, 5, "objects");
    }

    /// <summary>The names past the first few of a list of objects, for its list key: asked afresh in one go rather than
    /// one by one, then as NameAsync gives them.</summary>
    async Task<List<string>> RestNamesAsync(List<UUID> ids)
    {
        var region = _s.Region;
        var here = ids.Where(id => region?.PrimById(id) != null).ToList();
        if (here.Count > 0)
            try
            {
                await _s.Api.CallAsync("TesselObjects", new JsonObject { ["op"] = "get", ["ids"] = new JsonArray(here.Select(i => (JsonNode)i.ToString()).ToArray()), ["fresh"] = true, ["timeout"] = 4 },
                    true, TimeSpan.FromSeconds(6), default);
            }
            catch { }
        var names = new List<string>();
        foreach (var id in ids)
        {
            if (region?.PrimById(id) is { } prim)
            {
                string name = prim.Properties?.Name is { Length: > 0 } pn ? pn : Lang.T("an object");
                int parts = 1 + region.Prims.Values.Count(p => p.ParentID == prim.LocalID);
                names.Add(WithParts(Quoted(id, name), parts));
            }
            else names.Add(await NameAsync(id));
        }
        return names;
    }

    async Task<string> OwnersAsync(JsonObject a)
    {
        var ids = a.UuidList("owner_ids");
        if (a.Uuid("owner_id") is { } one) ids.Add(one);
        var names = new List<string>();
        foreach (var id in ids) names.Add(await NameAsync(id));
        return names.Count > 0 ? Lang.F("Objects of: {0}", string.Join(", ", names)) : "";
    }

    /// <summary>An avatar's or object's name for the person: what's in view, the name cache, or a lookup. Avatars
    /// by username (display names are anyone's choice).</summary>
    async Task<string> NameAsync(UUID? id)
    {
        if (id is not { } uid) return Lang.T("nobody");
        var region = _s.Region;
        if (region?.Avatars.Values.FirstOrDefault(av => av.ID == uid) is { } av) return av.Name;
        if (region?.PrimById(uid) is { } prim)
        {
            // asked afresh: names change (scripts, the build tools), and the person should confirm what's there now
            string name = prim.Properties?.Name is { Length: > 0 } pn ? pn : Lang.T("an object");
            try
            {
                var got = await _s.Api.CallAsync("TesselObjects", new JsonObject { ["op"] = "get", ["ids"] = new JsonArray(uid.ToString()), ["fresh"] = true, ["timeout"] = 3 },
                    true, TimeSpan.FromSeconds(4), default);
                if ((got as JsonObject)?["objects"] is JsonArray arr && arr.FirstOrDefault() is JsonObject o && o.Str("name") is { Length: > 0 } n) name = n;
            }
            catch { }
            int parts = 1 + region.Prims.Values.Count(p => p.ParentID == prim.LocalID);
            return WithParts(Quoted(uid, name), parts);
        }
        if (_s.Names.Legacy(uid) is { Length: > 0 } legacy) return legacy;
        try
        {
            var got = await _s.Api.CallAsync("TesselAgent", new JsonObject { ["op"] = "names", ["ids"] = new JsonArray(uid.ToString()) }, true, TimeSpan.FromSeconds(6), default);
            if ((got as JsonObject)?["names"] is JsonObject names && names[uid.ToString()] is JsonObject entry)
            {
                var user = entry.Str("username");
                var display = entry.Str("display_name");
                if (user is { Length: > 0 })
                    return display is { Length: > 0 } && !string.Equals(display, user, StringComparison.OrdinalIgnoreCase) ? $"{display} ({user})" : user;
                if (display is { Length: > 0 }) return display;
            }
        }
        catch { }
        return Lang.F("someone ({0})", uid.ToString()[..8]);
    }
}
