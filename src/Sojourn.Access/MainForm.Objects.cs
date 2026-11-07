using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Access;

namespace Sojourn.Access;

/// <summary>
/// Objects (Alt+7) beyond its list and menu (docs/access-app.md > Objects nearby): the words filter, past the nearest 60,
/// what people nearby wear, a thing's details, its contents, Touch landing where //touch does, and who may take, delete or
/// return a thing, by the viewer's own rules (llviewermenu's enable_take, LLSelectMgr's getFirstDeleteableObject,
/// LLObjectEnableReturn). Claude Opus 5.5, 2026-11-07.
/// </summary>
public sealed partial class MainForm
{
    const string MoreRow = "more";

    LineBox _objectsFilterBox = null!;
    readonly System.Windows.Forms.Timer _objectsFilterTimer = new() { Interval = 400 };
    int _objectsPages;   // pages past the first asked for by the More row (back to none in another region)
    string? _objectsRegion;
    int? _objectsLand;   // the row to arrive on once a More has filled

    string? ObjectsFilter => _objectsFilterBox.Text.Trim() is { Length: > 0 } f ? f : null;

    int ObjectsPage => _set.ObjectsRange > 30 ? 200 : 60;

    int ObjectsLimit
    {
        get
        {
            if (_ops.Me()?.Region is { } here && here != _objectsRegion) { _objectsRegion = here; _objectsPages = 0; }
            return ObjectsPage * (1 + _objectsPages);
        }
    }

    /// <summary>What the list is named: what it shows, and that a filter is on, so arriving in it says why it's short.</summary>
    string ObjectsName => (ShowingWorn ? "What you wear" : ShowingOthers ? "What people nearby wear" : "Objects nearby") + (ObjectsFilter is { } f ? $", with {f}" : "");

    void BuildObjectsFilter()
    {
        _objectsFilterBox = new LineBox { Name = "objectsFilter", AccessibleName = "Filter: words in its name, description or floating text", Dock = DockStyle.Fill };
        _objectsFilterBox.TextChanged += (_, _) => { _objectsFilterTimer.Stop(); _objectsFilterTimer.Start(); };
        _objectsFilterTimer.Tick += (_, _) =>
        {
            _objectsFilterTimer.Stop();
            _objectsPages = 0;
            _objects.AccessibleName = ObjectsName;
            _ = RefreshObjects(_objectsFilterBox.Focused);
        };
        _objectsFilterBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; _ = FilterEnterAsync(); }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = e.SuppressKeyPress = true;
                _objectsFilterBox.Clear();
                _ = FilterEnterAsync();
            }
        };
        // Control+F from anywhere in the area, as in the inventory
        void ToFilter(object? _, KeyEventArgs e)
        {
            if (e.KeyData != (Keys.Control | Keys.F)) return;
            e.Handled = e.SuppressKeyPress = true;
            _objectsFilterBox.Focus();
            _objectsFilterBox.SelectAll();
        }
        _objects.KeyDown += ToFilter;
        _parts.KeyDown += ToFilter;
        _faces.KeyDown += ToFilter;
        _objectsShow.KeyDown += ToFilter;
    }

    /// <summary>Enter in the filter (or Escape, once it's cleared): the list as it now is, and the focus in it.</summary>
    async Task FilterEnterAsync()
    {
        _objectsFilterTimer.Stop();
        _objectsPages = 0;
        await RefreshObjects(asked: true);
        _objects.Focus();
    }

    /// <summary>Enter on the list's last row: the next page, arriving on the first thing it adds.</summary>
    void MoreObjects()
    {
        _objectsLand = _objects.FocusedItem?.Index;
        _objectsPages++;
        _ = RefreshObjects(asked: false);
    }

    void LandAfterMore()
    {
        if (_objectsLand is not { } at) return;
        _objectsLand = null;
        if (at >= _objects.Items.Count) return;
        var item = _objects.Items[at];
        item.Selected = true;
        ReaderList.MakeCurrent(item);
        item.EnsureVisible();
    }

    string ObjectLine(PersonOps.Thing t)
    {
        if (t.Hud) return $"{t.Name}, {t.Text}";
        var touch = t.Sit ? ", can sit" : t.Touch || t.PartTouch ? ", touchable" : "";
        return t.WornBy != UUID.Zero
            ? $"{t.Name}, worn by {t.WornByName} on {t.Point}, {_ops.Where(t.Pos)}{(t.Money ? ", takes payment" : "")}{touch}"
            : $"{t.Name}, {_ops.Where(t.Pos)}{DealWords(t)}{touch}";
    }

    string ObjectsEmpty(string? filter) => filter != null ? $"Nothing here has {filter} in its name, description or floating text. Control+F, then Escape, clears the filter."
        : ShowingWorn ? "Nothing worn here yet." : ShowingOthers ? "Nobody nearby is wearing anything that's in view." : "Nothing nearby.";

    string ObjectsCount(List<PersonOps.Thing> things, PersonOps.Nearby page, string? filter)
    {
        if (things.Count == 0) return ObjectsEmpty(filter);
        if (ShowingWorn && filter == null) return WornCount(things);
        var more = page.More > 0 ? $", {page.More} more further on" : "";
        var unnamed = filter != null && page.Unnamed > 0 ? $"; {page.Unnamed} in range have no name from Second Life yet, F5 looks again" : "";
        return (filter != null ? $"{things.Count} found" : $"{things.Count} objects") + more + unnamed + ".";
    }

    static PersonOps.Nearby WornPage(List<PersonOps.Thing> worn, string? filter) =>
        new(filter == null ? worn : worn.Where(t => t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList(), 0, 0);

    // --- who may ------------------------------------------------------------------------------------------------------

    /// <summary>The viewer's Take: yours, or you may change it and pass it on.</summary>
    static bool MayTake(PersonOps.Thing t) => t.Mine || t.Modify && t.Transfer;

    /// <summary>The viewer's Delete: yours, or you may change it.</summary>
    static bool MayDelete(PersonOps.Thing t) => t.Mine || t.Modify;

    /// <summary>The viewer's Return, on someone else's thing: you may change it, or it's on land that's yours or your group's,
    /// or you manage the estate. The region checks again; what it refuses is said.</summary>
    bool MayReturn(PersonOps.Thing t) => !t.Mine && (t.Modify || LandIsMine(t.Pos));

    bool LandIsMine(LibreMetaverse.Vector3 pos)
    {
        if (_ops.Client is not { } c || c.Network.CurrentSim is not { } sim) return false;
        if (sim.IsEstateManager || sim.SimOwner == c.Self.AgentID) return true;
        int x = Math.Clamp((int)pos.X / 4, 0, 63), y = Math.Clamp((int)pos.Y / 4, 0, 63);
        // the region's map of its land, each 4 m square's low bits who owns it as seen from here (2: my group, 3: me)
        return (sim.ParcelOverlay[y * 64 + x] & 0x07) is 2 or 3;
    }

    async Task ReturnThingAsync(PersonOps.Thing t)
    {
        if (await ConfirmedAsync("TesselBuild", "returnObjects", new JsonObject { ["ids"] = new JsonArray(t.Id.ToString()) }) is not { } r) return;
        Answer(PersonOps.Error(r) is { } e ? $"Not returned: {e}" : $"Returned {t.Name} to its owner.");
        await Task.Delay(500);
        _ = RefreshObjects(false);
    }

    // --- touch --------------------------------------------------------------------------------------------------------

    /// <summary>The menu's Touch, landing where //touch does: on the object, unless only a part takes touches (a treasure
    /// chest's lid, the reason parts exist). Several parts taking them can't be chosen between here, so that's said; nothing
    /// taking them at all is touched anyway, as a click would, and said.</summary>
    async Task TouchThingAsync(PersonOps.Thing t)
    {
        if (t.Touch || !t.PartTouch)
        {
            await ThingOpAsync("requestTouch", t, t.Touch ? $"Touched {t.Name}." : $"Touched {t.Name}. Nothing in it says it takes touches.");
            return;
        }
        bool worn = t.Hud || t.WornBy != UUID.Zero;
        var takers = (await PartsOfAsync(t.Id, worn, placed: !worn || OnHud(t.Id)))?.Where(p => p.Touch).ToList();
        if (takers == null) { Answer($"{t.Name} isn't here any more."); return; }
        if (takers.Count == 1)
        {
            await TouchPartAsync(takers[0], null);
            if (!worn) _route?.Touched(t.Name);   // (//touch NAME finds the same part again)
            return;
        }
        Answer($"{t.Name} itself takes no touches. {takers.Count} of its parts do: {AndList(takers.Select(PartName).ToList())}. Tab reaches its parts.");
    }

    // --- details ------------------------------------------------------------------------------------------------------

    /// <summary>What a thing is (Alt+Enter, or Details on its menu): whose, who made it, what it says about itself, what the
    /// person may do with it, without going through the build editor. Said whole, for the review keys.</summary>
    async Task ThingDetailsAsync(UUID id, string name)
    {
        var got = await _ops.ReadAsync("TesselObjects", "get", new JsonObject { ["ids"] = new JsonArray(id.ToString()), ["full"] = true, ["timeout"] = 4 });
        if (PersonOps.Error(got) is { } err) { Answer($"Couldn't read {name}: {err}"); return; }
        if ((got["objects"] as JsonArray)?.FirstOrDefault() is not JsonObject o) { Answer($"{name} isn't here any more."); return; }
        var flags = (o["flags"] as JsonArray ?? new()).Select(f => f?.ToString()).ToHashSet();
        var words = new List<string> { $"{(o.Str("name") is { Length: > 0 } own ? own : name)}." };
        var ownerId = o.Str("owner_id");
        var groupId = o.Str("group_id");
        bool groupOwned = groupId != null && groupId == ownerId;
        var creatorId = o.Str("creator_id");
        var names = await PeopleNamesAsync(new[] { groupOwned ? null : ownerId, creatorId });
        bool mine = _ops.IsMine(o, flags);
        if (mine) words.Add("Yours.");
        else if (groupOwned) words.Add($"Owned by the group {await GroupNameAsync(groupId!)}.");
        else if (ownerId != null && names.TryGetValue(ownerId, out var owner)) words.Add($"Owned by {owner}.");
        var made = creatorId != null && names.TryGetValue(creatorId, out var creator) ? $"Made by {creator}" : null;
        var when = Json.AsDouble(o["created"]) is { } secs and > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)secs).LocalDateTime : (DateTime?)null;
        if (made != null) words.Add(when is { } w ? $"{made}, {w.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}." : $"{made}.");
        if (o.Str("desc") is { Length: > 0 } d && d != "(No Description)") words.Add($"Description: {d.Trim()}{(".!?".Contains(d.Trim()[^1]) ? "" : ".")}");
        if (o.Str("text") is { Length: > 0 } sign) words.Add($"Its sign says: {sign.ReplaceLineEndings(", ").Trim()}.");
        if (o.Str("attach_point") is { } point) words.Add($"Worn on {point}.");
        if (Json.AsDouble(o["prims"]) is { } prims and > 1) words.Add($"{prims:0} parts.");
        if (o["root_id"] != null) words.Add("A part of a linked object.");
        else if (o.Str("attach_point") == null && await LandImpactAsync(id) is { } impact) words.Add($"Land impact {impact}.");
        if (o["sale"] is JsonObject sale) words.Add(sale["price"]?.GetValue<int>() is { } price and > 0 ? $"For sale, L${price}." : "For sale, free.");
        words.Add(RightsWords(mine, flags, o["perms"] as JsonObject));
        var does = new List<string>();
        if (flags.Contains("scripted")) does.Add("scripted");
        if (flags.Contains("touch")) does.Add("takes touches");
        if (flags.Contains("pay")) does.Add("takes payment");
        if (flags.Contains("temp")) does.Add("temporary");
        if (flags.Contains("physics")) does.Add("physical");
        if (flags.Contains("phantom")) does.Add("phantom");
        if (does.Count > 0) words.Add(char.ToUpperInvariant(does[0][0]) + string.Join(", ", does)[1..] + ".");
        Answer(string.Join(" ", words), LineFrom.People);
    }

    static string RightsWords(bool mine, HashSet<string?> flags, JsonObject? perms)
    {
        static string Letters(string? m)
        {
            var rights = new[] { ('C', "copy"), ('M', "modify"), ('T', "transfer") }.Where(x => m?.Contains(x.Item1) == true).Select(x => x.Item2).ToList();
            return rights.Count == 0 ? "do nothing with" : AndList(rights);
        }
        if (mine)
            return $"Yours to {Letters(perms?.Str("owner"))}; the next owner may {Letters(perms?.Str("next"))}.";
        var may = new List<string>();
        if (flags.Contains("copy")) may.Add("copy");
        if (flags.Contains("mod")) may.Add("modify");
        if (flags.Contains("move")) may.Add("move");
        return may.Count == 0 ? "You may not change or copy it." : $"You may {AndList(may)} it.";
    }

    async Task<Dictionary<string, string>> PeopleNamesAsync(IEnumerable<string?> ids)
    {
        var known = new Dictionary<string, string>();
        var want = ids.Where(i => i != null && i != UUID.Zero.ToString()).Distinct().Select(i => (JsonNode)i!).ToArray();
        if (want.Length == 0) return known;
        var r = await _ops.ReadAsync("TesselAgent", "names", new JsonObject { ["ids"] = new JsonArray(want), ["timeout"] = 3 });
        if (r["names"] is JsonObject nm)
            foreach (var (id, n) in nm)
                if ((n is JsonObject no ? no.Str("display_name") ?? no.Str("username") : n?.ToString()) is { Length: > 0 } name) known[id] = name;
        return known;
    }

    async Task<string> GroupNameAsync(string id)
    {
        if (UUID.TryParse(id, out var gid) && Host?.Session.State.Groups.TryGetValue(gid, out var mine) == true) return mine.Name;
        var r = await _ops.ReadAsync("TesselGroups", "profile", new JsonObject { ["group_id"] = id });
        return r.Str("name") ?? "not known";
    }

    async Task<int?> LandImpactAsync(UUID id)
    {
        var r = await _ops.ReadAsync("SojournBuild", "landImpact", new JsonObject { ["id"] = id.ToString() });
        return Json.AsDouble(r["land_impact"]) is { } li and > 0 ? (int)Math.Ceiling(li - 1e-6) : null;
    }

    /// <summary>//details NAME: the nearest thing with that name within 30 metres, else a named part within 10.</summary>
    async Task DetailsWordsAsync(string words)
    {
        var w = words.Trim();
        if (w.Length == 0) { Answer("//details NAME: whose something within 30 metres is, who made it, what it says about itself."); return; }
        if (Named(await _ops.ObjectsAsync(30, 60, w), w) is { } t) await ThingDetailsAsync(t.Id, t.Name);
        else if (await FindPartAsync(w) is { } part) await ThingDetailsAsync(part.Id, PartName(part));
        else Answer($"Nothing called {w} within 30 metres.");
    }

    // --- contents -----------------------------------------------------------------------------------------------------

    /// <summary>What's inside a thing or a part: the build editor's Content tab with the focus in its list, without having to
    /// know that's where it lives; Escape comes back here.</summary>
    async Task ContentsOfAsync(UUID id, bool onePart)
    {
        if (!_buildPage.ContainsFocus) { _beforeBuild = ActiveControl; _beforeBuildPage = _areas.SelectedTab; }
        SetBuildTarget(id, null, onePart);
        int tab = Array.IndexOf(BuildRows.Tabs, "Content");
        if (_buildTab.SelectedIndex != tab) _buildTab.SelectedIndex = tab;
        GoArea(_buildPage);
        await GuardedAsync(async () =>
        {
            await ReadPartsAsync();
            await ShowBuildRowsAsync();
            _buildRows.Focus();
        }, "its contents");
    }
}
