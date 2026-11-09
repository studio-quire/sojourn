using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Core.World;

namespace Sojourn.Access;

/// <summary>
/// The person's own actions, through the same ops the assistant and the agent API use. An action that began with the
/// person's input (<see cref="RealInput.Acting"/>: a hardware key or click, or a key the screen reader passed on, and
/// what it awaits) runs the op itself, so the assistant's confirmations (its touches on others' things, say) and
/// incognito's limits on callers don't stand between them and what they asked for. Anything else (a timer, a callback,
/// a UI Automation invoke, a key a program sent) goes <see cref="GatedAsync"/>, through the op gate, as the assistant's
/// would. Spending, giving and deleting always go through the gate, so the person's confirmation holds for their own
/// keys too. Lookups that change nothing go <see cref="ReadAsync"/>. Errors come back as {error}.
/// </summary>
public sealed class PersonOps(AgentSession s)
{
    public AgentSession Session => s;

    /// <summary>An action: the op itself if it began with the person's input, else through the gate.</summary>
    public Task<JsonObject> DoAsync(string pump, string op, JsonObject? args = null, CancellationToken ct = default) =>
        RealInput.Acting ? RunAsync(pump, op, args, ct) : GatedAsync(pump, op, args, ct);

    /// <summary>A lookup that changes nothing in the world or the account: the op itself, whoever asked.</summary>
    public Task<JsonObject> ReadAsync(string pump, string op, JsonObject? args = null, CancellationToken ct = default) =>
        RunAsync(pump, op, args, ct);

    async Task<JsonObject> RunAsync(string pump, string op, JsonObject? args, CancellationToken ct)
    {
        args ??= new JsonObject();
        args["op"] = op;
        if (!s.Api.ApiMap.TryGetValue(pump, out var api) || !api.Ops.TryGetValue(op, out var handler))
        {
            s.Log.LogWarning("person op {Pump}.{Op} isn't here", pump, op);
            return Json.Error(Fault);
        }
        if (api.NeedsSession && !s.Online) return Json.Error(NotIn);
        try { return Logged(pump, op, await handler.Run(args, ct) as JsonObject ?? new JsonObject()); }
        catch (LeapError e) { return Logged(pump, op, Json.Error(e.Message)); }
        catch (NotConnected) { return Json.Error(NotIn); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            s.Log.LogWarning(e, "person op {Pump}.{Op} threw", pump, op);
            return Json.Error(Fault);
        }
    }

    /// <summary>Through the dispatcher and its gate (the person's confirmation for spending, giving, deleting).</summary>
    public async Task<JsonObject> GatedAsync(string pump, string op, JsonObject? args = null, CancellationToken ct = default)
    {
        args ??= new JsonObject();
        args["op"] = op;
        try { return Logged(pump, op, await s.Api.CallAsync(pump, args, reply: true, TimeSpan.FromSeconds(150), ct) as JsonObject ?? new JsonObject()); }
        catch (NotConnected) { return Json.Error(NotIn); }
    }

    /// <summary>An op's error as it came, in the log: what the person hears is <see cref="Words"/>' version of it.</summary>
    JsonObject Logged(string pump, string op, JsonObject r)
    {
        if (r["error"] is JsonValue v && v.TryGetValue<string>(out var e)) s.Log.LogInformation("person op {Pump}.{Op}: {Error}", pump, op, e);
        return r;
    }

    /// <summary>Why an op can't run out of world: logging in again after a drop is said as that.</summary>
    string NotIn => s.WantsOnline ? "not in world right now; logging in again" : "not logged in";

    /// <summary>Said for a fault of ours (an op missing, an exception, arguments the op refused): the detail is in the log.</summary>
    const string Fault = "something went wrong inside Sojourn Access (the log has the details)";

    /// <summary>An op's error in the person's words (<see cref="Words"/>), or null if it went through.</summary>
    public static string? Error(JsonObject r) => r["error"] is JsonValue v && v.TryGetValue<string>(out var e) ? Words(e) : null;

    /// <summary>
    /// The one place an op's error becomes what the person hears. The ops' errors are written for the assistant and the
    /// agent API: from the avatar's side ("I'm not in that group", "isn't mine"), naming arguments and other ops ("give
    /// group_id", "(TesselInventory copy)") and ids. Here they turn to the person's side, ids and API hints left out (the
    /// log keeps the error as it came: <see cref="Logged"/>), and an error that's only about arguments says it's ours.
    /// Idempotent, so text already in their words passes through.
    /// </summary>
    public static string Words(string e)
    {
        var w = e;
        // ids: "no item <id> in my inventory" says no such item; elsewhere the id just goes
        w = Regex.Replace(w, $@"\bno (inventory )?(item or folder|item|folder|object) {Id}", "no such $2");
        w = Regex.Replace(w, $@"\bhas id {Id}", "has that id");
        w = Regex.Replace(w, $@"\bcalled {Id}", "called that");
        w = Regex.Replace(w, $@"(:| for| of)? ?\(?""?{Id}""?\)?", "");
        // an op's name leading ("requestSit could not find the sit target"), an argument's naming the value refused
        // ("agent_limit: at most 40 here")
        w = Regex.Replace(w, $@"^{Api}\s+(?=could not)", "");
        w = Regex.Replace(w, $@"^{Arg}: (?!.*{Arg})", "");
        w = Regex.Replace(w, @"\s*\(HTTP \d+\)", "");
        // a clause or bracket naming an API or another op ("(TesselInventory copy)", ": find their id with TesselAgent
        // findAvatar") goes; one only about arguments ("; give group_id") too
        w = Regex.Replace(w, $@"\s*\([^()]*{Api}[^()]*\)", "");
        w = Regex.Replace(w, $@"[:;,] [^:;,()]*{Api}[^:;,()]*", "");
        w = Regex.Replace(w, $@"; give {Arg}( or {Arg})*", "");
        // the avatar's side to the person's: only the phrases the ops use, never a bare I, me or my, which would turn
        // the names in the error too ("Read me" isn't a landmark, "I Love SL" isn't worn)
        w = Regex.Replace(w, @"\bI'm (not|in)\b", "you're $1");
        w = Regex.Replace(w, @"\bI (can't|don't)\b", "you $1");
        w = Regex.Replace(w, @"\bgroup's I have\b", "group's you have");
        w = Regex.Replace(w, @"\bmy (inventory|groups|friends|roles|block list|land's)\b", "your $1");
        w = Regex.Replace(w, @"\bof mine\b", "of yours");
        w = Regex.Replace(w, @"\b(isn't|all be|to be) mine\b", "$1 yours");
        w = Regex.Replace(w, @"\b(rights for|seat|below) me\b", "$1 you");
        w = Regex.Replace(w, @"^that's me$", "that's you");
        w = Regex.Replace(w, @"\s{2,}", " ").Trim();
        // what's left still names an op, or is about an argument ("need item_id", "folder_id isn't a folder"): nothing
        // the person can act on, so it's said as ours (an object's name with an underscore elsewhere stays)
        if (w.Length == 0 || Regex.IsMatch(w, Api) || Regex.IsMatch(w, $@"^{Arg}\b|\b(need|give) (the )?(worn )?{Arg}")) return Fault;
        return w;
    }

    const string Id = @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
    // an API's name (TesselInventory, SojournBuild) or an op's (getActiveGestures, findAvatar)
    const string Api = @"(?:\b(?:Tessel|Sojourn|Multitude)[A-Z]\w*|\b(?:get|set|find|request|list|write|read)[A-Z]\w*\b)";
    // an argument's name (group_id, item_ids, next_owner)
    const string Arg = @"\b[a-z]+_[a-z_]+\b";

    // --- where things are -------------------------------------------------------------------------------------------

    public GridClient? Client => s.Client;

    /// <summary>The avatar's position in its region and its compass heading (the seat's, sitting).</summary>
    public (Vector3 Pos, double Heading, string Region)? Me()
    {
        if (s.Client is not { } c || c.Network.CurrentSim is not { } sim) return null;
        var q = c.Self.SittingOn != 0 ? c.Self.SimRotation : c.Self.Movement.BodyRotation;
        return (s.Motion.Position(c), Motion.Heading(q), sim.Name);
    }

    /// <summary>Someone around. Name: as names are said (Alt+0's People's names); Written: their display name as written.</summary>
    public sealed record Person(UUID Id, string Name, string Username, Vector3 Pos, double Dist, string Written = "")
    {
        /// <summary>Every name she might type for them: the one said, the username, the display name as written and in
        /// plain letters, so a name she hears is a name she can type back whatever the style.</summary>
        public IEnumerable<string> Names => new[] { Name, Username, Written, PlainName.Of(Written) }.Where(n => n.Length > 0).Distinct();
    }

    /// <summary>A person's name from an op's result (TesselAgent's people, friends, names, findAvatar; search's people) as
    /// names are said: its said, else its display name and username worded so.</summary>
    public static string SaidName(JsonObject o, string fallback = "someone")
    {
        if (o.Str("said") is { Length: > 0 } said) return said;
        var user = o.Str("username") is { Length: > 0 } u ? u : o.Str("legacy_name") is { Length: > 0 } l ? NameCache.UserName(l) : "";
        return PlainName.Said(o.Str("display_name"), user, NameCache.Style) is { Length: > 0 } shown ? shown : fallback;
    }

    /// <summary>People around, nearest first (in view, then the map's coarse positions), as TesselAgent people.</summary>
    public async Task<List<Person>> PeopleAsync(double within = 96)
    {
        var r = await ReadAsync("TesselAgent", "people", new JsonObject { ["dist"] = within });
        var list = new List<Person>();
        foreach (var p in (r["people"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            if (!UUID.TryParse(p.Str("id"), out var id) || p["pos"] is not JsonArray pos || pos.Count < 3) continue;
            var user = p.Str("username") ?? "";
            list.Add(new Person(id, SaidName(p), user, new Vector3((float)pos[0]!.GetValue<double>(), (float)pos[1]!.GetValue<double>(), (float)pos[2]!.GetValue<double>()),
                p["dist"]?.GetValue<double>() ?? 0, p.Str("display_name") ?? ""));
        }
        return list;
    }

    /// <summary>The nearest person with a name (<see cref="Person.Names"/>) starting with the words, else containing them.</summary>
    public async Task<Person?> FindPersonAsync(string words) => (await FindPeopleAsync(words)).FirstOrDefault();

    /// <summary>Everyone with a name (said, user, or the display name as written or in plain letters) starting with the
    /// words, else everyone whose has them, nearest first. Words pasted in decorative letters are matched in plain ones.</summary>
    public async Task<List<Person>> FindPeopleAsync(string words)
    {
        var ws = new[] { words.Trim(), PlainName.Of(words) }.Where(x => x.Length > 0).Select(x => x.ToLowerInvariant()).Distinct().ToList();
        if (ws.Count == 0) return new();
        var all = await PeopleAsync(512);
        bool Starts(string n) => ws.Any(w => n.ToLowerInvariant().StartsWith(w) || n.ToLowerInvariant().Split(' ', '.').Any(part => part.StartsWith(w)));
        bool Has(string n) => ws.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase));
        var starting = all.Where(p => p.Names.Any(Starts)).ToList();
        return starting.Count > 0 ? starting : all.Where(p => p.Names.Any(Has)).ToList();
    }

    public sealed record Thing(UUID Id, string Name, Vector3 Pos, double Dist, bool Sit, bool Touch, string? Text)
    {
        /// <summary>Something the person wears (Objects' Show: what you wear; a HUD or on the body): Text says where.</summary>
        public bool Hud { get; init; }
        /// <summary>The person's own, in the world: theirs to take or delete.</summary>
        public bool Mine { get; init; }
        /// <summary>It or one of its parts has a money event; PayTo: the root if it does, else that part.</summary>
        public bool Money { get; init; }
        public UUID PayTo { get; init; }
        /// <summary>For sale, as its properties say ("original", "copy", "contents"), and the price; null: not, or not known yet.</summary>
        public string? Sale { get; init; }
        public int Price { get; init; }
        public bool MayCopy { get; init; }
        /// <summary>What the viewer's left click would do: "pay", "buy", "sit"...; null: touch.</summary>
        public string? Click { get; init; }
        /// <summary>The region says the person may change it (a friend's with edit rights, a group's), and pass it on.</summary>
        public bool Modify { get; init; }
        public bool Transfer { get; init; }
        /// <summary>One of its parts takes touches, though the root itself may not.</summary>
        public bool PartTouch { get; init; }
        /// <summary>Someone else wears it: who, and on what.</summary>
        public UUID WornBy { get; init; }
        public string? WornByName { get; init; }
        public string? Point { get; init; }
    }

    /// <summary>A page of Objects nearby: what's listed, how many more past it, how many in range have no name yet.</summary>
    public sealed record Nearby(List<Thing> Things, int More, int Unnamed);

    /// <summary>Objects around (root objects, not attachments), nearest first; query: words in the name, description
    /// or floating text. renew: scripted ones' names older than this many seconds asked again (0: all of them), as a
    /// script may have renamed its object (a self-naming sofa).</summary>
    public async Task<List<Thing>> ObjectsAsync(double within = 30, int limit = 60, string? query = null, double? renew = null) =>
        (await NearbyAsync(within, limit, query, renew)).Things;

    /// <summary>As ObjectsAsync, for the list itself: scan looks through everything in range for a query, not the nearest
    /// 200; others: only what other people wear; wornBy: only what that person wears.</summary>
    public async Task<Nearby> NearbyAsync(double within, int limit, string? query = null, double? renew = null, bool scan = false, bool others = false,
        UUID? wornBy = null)
    {
        var args = new JsonObject { ["dist"] = within, ["limit"] = limit, ["timeout"] = 3 };
        if (!string.IsNullOrWhiteSpace(query)) args["query"] = query.Trim();
        if (renew is { } secs) args["renew"] = secs;
        if (scan) args["scan"] = true;
        if (others) args["worn_only"] = true;
        if (wornBy is { } by) args["worn_by"] = by.ToString();
        var r = await ReadAsync("TesselObjects", "nearby", args);
        var list = Things(r["objects"] as JsonArray);
        return new Nearby(list.OrderBy(t => t.Dist).ToList(), r.Int("more", 0), r.Int("unnamed", 0));
    }

    /// <summary>One object by its id (its root's, for a part), as the list has it; null when it isn't in view.</summary>
    public async Task<Thing?> ThingAsync(UUID id)
    {
        var r = await ReadAsync("TesselObjects", "get", new JsonObject { ["ids"] = new JsonArray(id.ToString()), ["timeout"] = 3 });
        if (Things(r["objects"] as JsonArray).FirstOrDefault() is not { } t) return null;
        // (get gives no distance: the list's words and order need one)
        return Me() is { } me ? t with { Dist = Vector3.Distance(t.Pos, me.Pos) } : t;
    }

    /// <summary>Objects as the ops describe them (TesselObjects nearby and get), as the lists' things.</summary>
    List<Thing> Things(JsonArray? objects)
    {
        var list = new List<Thing>();
        // a part's money event or touch counts for its object: the region says it on that part alone
        var childMoney = new Dictionary<UUID, UUID>();
        var childTouch = new HashSet<UUID>();
        if (s.Region is { } region)
            foreach (var p in region.Prims.Values)
            {
                if (p.ParentID == 0 || !region.Prims.TryGetValue(p.ParentID, out var parent)) continue;
                var pf = s.State.PrimFlagsOf(p);
                if ((pf & PrimFlags.Money) != 0) childMoney.TryAdd(parent.ID, p.ID);
                if ((pf & PrimFlags.Touch) != 0) childTouch.Add(parent.ID);
            }
        foreach (var o in (objects ?? new()).OfType<JsonObject>())
        {
            if (!UUID.TryParse(o.Str("id"), out var id) || o["pos"] is not JsonArray pos || pos.Count < 3) continue;
            var flags = (o["flags"] as JsonArray ?? new()).Select(f => f?.ToString()).ToHashSet();
            var name = o.Str("name") is { Length: > 0 } n ? n : "object";
            bool rootMoney = flags.Contains("pay");
            bool kidMoney = childMoney.TryGetValue(id, out var payPart);
            list.Add(new Thing(id, name, new Vector3((float)pos[0]!.GetValue<double>(), (float)pos[1]!.GetValue<double>(), (float)pos[2]!.GetValue<double>()),
                o["dist"]?.GetValue<double>() ?? 0, o.Str("click_action") == "sit", flags.Contains("touch"), o.Str("text"))
            {
                Mine = IsMine(o, flags), MayCopy = flags.Contains("copy"), Click = o.Str("click_action"),
                Sale = (o["sale"] as JsonObject)?.Str("type"), Price = (o["sale"] as JsonObject)?["price"]?.GetValue<int>() ?? 0,
                Money = rootMoney || kidMoney, PayTo = rootMoney || !kidMoney ? id : payPart,
                Modify = flags.Contains("mod"), Transfer = flags.Contains("transfer"), PartTouch = childTouch.Contains(id),
                WornBy = UUID.TryParse(o.Str("attached_to"), out var by) ? by : UUID.Zero,
                WornByName = UUID.TryParse(o.Str("attached_to"), out var who) ? PersonName(who) : null,
                Point = o.Str("attach_point"),
            });
        }
        return list;
    }

    /// <summary>The person's own by the region's flag and its owner: OpenSim sets the flag on a friend's that the friend
    /// lets them edit too, and those aren't theirs to take as their own.</summary>
    public bool IsMine(JsonObject o, HashSet<string?> flags) => flags.Contains("mine")
        && (!UUID.TryParse(o.Str("owner_id"), out var owner) || owner == s.AgentId);

    string PersonName(UUID id) => s.Names.Complete(id, s.Region?.AvatarById(id)?.Name ?? "someone");

    /// <summary>"4 m, two o'clock" from the avatar, or null when it doesn't know where it is.</summary>
    public string? Where(Vector3 pos, bool full = false)
    {
        if (Me() is not { } me) return null;
        var d = pos - me.Pos;
        return full ? Bearings.Full(d.X, d.Y, d.Z, me.Heading) : Bearings.Relative(d.X, d.Y, d.Z, me.Heading);
    }

    /// <summary>A point in the avatar's region as a global position (for the autopilot).</summary>
    public Vector3d? Global(Vector3 regionPos)
    {
        if (s.Client?.Network.CurrentSim is not { } sim) return null;
        Utils.LongToUInts(sim.Handle, out var gx, out var gy);
        return new Vector3d(gx + regionPos.X, gy + regionPos.Y, regionPos.Z);
    }
}
