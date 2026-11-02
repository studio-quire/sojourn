using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Session.Assistant;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Render.Realtime;

namespace Sojourn.Session.Land;

/// <summary>
/// Batch B5's client half (land and region), kept out of Program.cs: the confirmations for SojournLand's ops (what
/// spends, gives land away, ejects or bans, deletes, or changes who may do what), each describing the parcel from the
/// region's own answer and pinning it (its local id, region, price and area) so the op acts on what the person was
/// shown; the land outlined on the ground while they're asked and while a land op runs (World/ParcelLines.cs), the
/// terraform brush and the terrain hurried while a stroke goes; and the hook that remembers which parcel just turned
/// the person away, for a pass bought without saying where. Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class LandWiring : IDisposable
{
    readonly AgentSession _s;
    readonly AssistantHost _a;
    readonly FrameRenderer? _r;
    readonly ILogger _log;
    readonly Timer _timer;
    volatile bool _shownForAsk;

    /// <param name="renderer">Outlines the land and the terraform brush in the world (null: an app without a renderer).</param>
    public static LandWiring Start(AgentSession s, AssistantHost assistant, FrameRenderer? renderer, ILogger log) => new(s, assistant, renderer, log);

    LandWiring(AgentSession s, AssistantHost assistant, FrameRenderer? renderer, ILogger log)
    {
        _s = s;
        _a = assistant;
        _r = renderer;
        _log = log;
        assistant.Confirmations.Cases.Add(new Confirmations.Case(Kind, DescribeAsync));
        var lines = renderer?.ParcelLines;
        s.Parcels.Showing = (region, bitmap, rect, seconds) =>
        {
            if (bitmap is { Length: > 0 }) lines?.Select(region, bitmap, seconds);
            else if (rect is { } r) lines?.Select(region, r.W, r.S, r.E, r.N, seconds);
        };
        s.Parcels.Brushing = (centre, half, area, seconds) =>
        {
            if (s.Client?.Network.CurrentSim is not { } sim) return;
            lines?.Brush(sim.Handle, new System.Numerics.Vector2(centre.X, centre.Y), half, area, seconds);
            Interlocked.Exchange(ref Sojourn.Render.World.Terrain.HurryUntil, System.Environment.TickCount64 + (long)(seconds * 1000) + 1500);
        };
        var hooked = new System.Runtime.CompilerServices.ConditionalWeakTable<GridClient, object>();
        void Hook(GridClient c)
        {
            if (hooked.TryGetValue(c, out _)) return;
            hooked.Add(c, new object());
            c.Parcels.ParcelProperties += (_, e) =>
            {
                try { s.Parcels.Observe(e); }
                catch (Exception x) { log.LogDebug(x, "land"); }
            };
        }
        lock (AgentSession.ClientHooks) AgentSession.ClientHooks.Add(Hook);
        if (s.Client is { } now) Hook(now);
        // the land a confirmation showed stays a moment after it's answered (the op runs), then goes
        _timer = new Timer(_ =>
        {
            if (_shownForAsk && !_a.Confirmations.Pending)
            {
                _shownForAsk = false;
                lines?.KeepSelection(8);
            }
        }, null, 500, 250);
    }

    public void Dispose() => _timer.Dispose();

    // --- what waits for the person ----------------------------------------------------------------------------------

    static readonly HashSet<string> Permissions = new()
    {
        "set", "allow", "unallow", "ban", "unban", "reclaim", "divide", "estateAdd", "estateRemove", "kick", "sendHome", "setRegion", "regionDebug",
    };

    string? Kind(string pump, string op, JsonObject a)
    {
        if (pump != "SojournLand") return null;
        // a buy the region has already shown can't go fails without asking; the op checks afresh and, marked so,
        // refuses even if it could go now (it was never confirmed)
        if (op is "buy" or "buyPass")
        {
            a.Remove("unasked");
            try
            {
                if (_s.Parcels.Known(a) is { } k && (op == "buy"
                        ? _s.Parcels.BuyRefusal(k.Parcel, _s.Parcels.GroupArg(a) ?? UUID.Zero)
                        : (a.Has("at") ? _s.Parcels.PassRefusal(k.Parcel) : null)) is not null)
                {
                    a["unasked"] = true;
                    return null;
                }
            }
            catch (Exception e) when (e is LeapError or NotConnected) { }
        }
        if (Permissions.Contains(op)) return "permissions";
        return op switch
        {
            "freeze" when a.Bool("freeze", true) => "permissions",
            "restart" when !a.Bool("cancel", false) => "permissions",
            "sell" or "abandon" or "deed" => "give",
            "buy" or "buyPass" or "contribute" => "spend",
            "join" or "returnObjects" or "bakeTerrain" => "delete",
            "terraform" when ParcelOps.AreaTerraform(a) => "delete",
            "message" => "words",
            _ => null,
        };
    }

    async Task<(string Title, string Detail)> DescribeAsync(string pump, string op, JsonObject a)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;
        try { return await DescribeLandAsync(op, a, ct); }
        catch (Exception e) when (e is LeapError or OperationCanceledException or NotConnected)
        {
            return (Lang.F("{0} (the region didn't say which land)", Title(op)), Lang.F("{0}: this will likely fail.", e.Message));
        }
    }

    static string Title(string op) => op switch
    {
        "buy" => Lang.T("Buy land"), "buyPass" => Lang.T("Buy a pass"), "sell" => Lang.T("Sell land"), "abandon" => Lang.T("Abandon land"),
        "deed" => Lang.T("Deed land to a group"),
        _ => Lang.F("Land: {0}", op),
    };

    string Balance() => _s.BalanceKnown && _s.Client is { } c ? Lang.F("Your balance: L${0}.", c.Self.Balance) : "";

    static string Join(params string[] parts) => string.Join(" ", parts.Where(p => p.Length > 0));

    static string Num(double v, string format = "0") => v.ToString(format, CultureInfo.InvariantCulture);

    string Land(Simulator sim, Parcel p) =>
        p.Name is { Length: > 0 } n ? Lang.F("\"{0}\" ({1} m² in {2})", n, p.Area, sim.Name) : Lang.F("a parcel ({0} m² in {1})", p.Area, sim.Name);

    string RegionName() => _s.Client?.Network.CurrentSim?.Name ?? Lang.T("this region");

    async Task<string> NamesAsync(JsonObject a, CancellationToken ct)
    {
        var ids = await _s.Parcels.PeopleAsync(a, ct);
        if (_s.Client is { } c) await _s.Names.ResolveAsync(c, ids, TimeSpan.FromSeconds(4), ct);
        return Confirmations.Listed(ids.Select(id => _s.Names.Complete(id, Lang.F("someone ({0})", id.ToString()[..8]))).ToList(), 6, "people");
    }

    /// <summary>The parcel the op will act on, asked afresh, pinned into its arguments and outlined on the ground.</summary>
    async Task<(Simulator Sim, Parcel P)> PinAsync(JsonObject a, CancellationToken ct, bool pass = false)
    {
        var (sim, p) = pass ? await _s.Parcels.PassParcelAsync(a, ct) : await _s.Parcels.ParcelAsync(a, ct);
        ParcelOps.Pin(a, sim, p);
        Outline(sim, p.Bitmap, null);
        return (sim, p);
    }

    void Outline(Simulator sim, byte[]? bitmap, (float W, float S, float E, float N)? rect)
    {
        var seconds = Confirmations.Timeout.TotalSeconds + 5;
        if (bitmap is { Length: > 0 }) _r?.ParcelLines.Select(sim.Handle, bitmap, seconds);
        else if (rect is { } r) _r?.ParcelLines.Select(sim.Handle, r.W, r.S, r.E, r.N, seconds);
        _shownForAsk = true;
    }

    /// <summary>A title for a while (hours given) or for good: its three templates, {0} who, {1} the land, {2} hours.</summary>
    static string Timed(JsonObject a, string always, string hour, string hours, string who, string land) =>
        a.Num("hours") is { } h && h > 0 ? Lang.F(h == 1 ? hour : hours, who, land, Num(h, "0.#")) : Lang.F(always, who, land);

    async Task<(string Title, string Detail)> DescribeLandAsync(string op, JsonObject a, CancellationToken ct)
    {
        var parcels = _s.Parcels;
        switch (op)
        {
            case "set":
            {
                var (sim, p) = await PinAsync(a, ct);
                var changes = a.Where(kv => kv.Key is not ("op" or "at" or "local_id" or "region"))
                    .Select(kv => kv.Value is JsonObject o ? $"{kv.Key}: " + string.Join(", ", o.Select(x => $"{x.Key} {Short(x.Value)}")) : $"{kv.Key}: {Short(kv.Value)}");
                return (Lang.F("Change the settings of {0}", Land(sim, p)), string.Join("; ", changes));
            }
            case "allow" or "unallow" or "ban" or "unban":
            {
                var (sim, p) = await PinAsync(a, ct);
                var who = await NamesAsync(a, ct);
                bool closed = (p.Flags & ParcelFlags.UseAccessList) != 0;
                return op switch
                {
                    "ban" => (Timed(a, "Ban {0} from {1}", "Ban {0} from {1} for {2} hour", "Ban {0} from {1} for {2} hours", who, Land(sim, p)),
                        Lang.T("They can't enter it; anyone of them on it now is sent off it.")),
                    "unban" => (Lang.F("Lift the ban on {0} from {1}", who, Land(sim, p)), ""),
                    "allow" => (Timed(a, "Let {0} into {1}", "Let {0} into {1} for {2} hour", "Let {0} into {1} for {2} hours", who, Land(sim, p)),
                        Lang.T(closed ? "It's closed to all but its allowed list, and they'll be on it." : "On its allowed list (it's open to everyone now, so that matters once it's closed).")),
                    _ => (Lang.F("Take {0} off the allowed list of {1}", who, Land(sim, p)), closed ? Lang.T("It's closed to all but that list: they can't enter it then.") : ""),
                };
            }
            case "freeze":
            {
                var who = await NamesAsync(new JsonObject { ["to"] = a.Str("to") }, ct);
                return (Lang.F("Freeze {0} where they stand", who), Lang.T("As the land's owner: they can't move on it until you free them or the region lets them go."));
            }
            case "sell":
            {
                var (sim, p) = await PinAsync(a, ct);
                int price = a.Int("price", 0);
                string to = a.Has("to") ? Lang.F("Only {0} can buy it.", await NamesAsync(a, ct)) : _s.Parcels.GroupArg(a, "to_group") is { } g
                    ? (_s.State.Groups.TryGetValue(g, out var grp) ? Lang.F("Only the group {0} can buy it.", grp.Name) : Lang.T("Only the group named can buy it."))
                    : Lang.T("Anyone can buy it.");
                string objects = a.Bool("objects") == true ? Lang.T("Your objects on it go with it.") : "";
                return (price == 0 ? Lang.F("Offer {0} for nothing", Land(sim, p)) : Lang.F("Put {0} up for sale for L${1}", Land(sim, p), price),
                    Join(to, objects, price == 0 ? Lang.T("They get it for L$0: the land is given away when they take it.") : ""));
            }
            case "buy":
            {
                var (sim, p) = await PinAsync(a, ct);
                a["price"] = p.SalePrice;
                a["area"] = p.Area;
                var group = parcels.GroupArg(a) ?? UUID.Zero;
                if (parcels.BuyRefusal(p, group) is { } no) return (Lang.F("Buy {0}", Land(sim, p)), Lang.F("It can't be bought now: {0}.", no));
                if (_s.Client is { } c) await _s.Names.ResolveAsync(c, new[] { p.OwnerID }, TimeSpan.FromSeconds(3), ct);
                var covenant = await CovenantLineAsync(ct);
                string title = group == UUID.Zero ? Lang.F("Buy {0} for L${1}", Land(sim, p), p.SalePrice)
                    : _s.State.Groups.TryGetValue(group, out var g) ? Lang.F("Buy {0} for the group {1} for L${2}", Land(sim, p), g.Name, p.SalePrice)
                    : Lang.F("Buy {0} for the group named for L${1}", Land(sim, p), p.SalePrice);
                string from = (p.Flags & ParcelFlags.SellParcelObjects) != 0 ? Lang.F("From {0}, with the objects on it.", parcels.NameOf(p.OwnerID)) : Lang.F("From {0}.", parcels.NameOf(p.OwnerID));
                return (title, Join(from, Balance(), covenant,
                    Lang.T("On the mainland, owning land needs Premium and can raise your monthly land use fee, which Linden Lab bills.")));
            }
            case "buyPass":
            {
                var (sim, p) = await PinAsync(a, ct, pass: true);
                a["price"] = p.PassPrice;
                if (parcels.PassRefusal(p) is { } no) return (Lang.F("Buy a pass to {0}", Land(sim, p)), Lang.F("It can't be bought: {0}.", no));
                return (Lang.F("Buy a pass to {0} for L${1}", Land(sim, p), p.PassPrice), Join(Lang.F("{0} hours of access.", Num(p.PassHours, "0.#")), Balance()));
            }
            case "abandon":
            {
                var (sim, p) = await PinAsync(a, ct);
                return (Lang.F("Abandon {0}", Land(sim, p)),
                    Lang.T("It stops being yours for good: the estate's owner (Governor Linden on the mainland) gets it, and you'd have to buy it back."));
            }
            case "deed":
            {
                var (sim, p) = await PinAsync(a, ct);
                var g = parcels.GroupArg(a) ?? p.GroupID;
                return (_s.State.Groups.TryGetValue(g, out var grp) ? Lang.F("Deed {0} to {1}", Land(sim, p), grp.Name) : Lang.F("Deed {0} to its group", Land(sim, p)),
                    Lang.T("The group owns it from then on; only someone with its powers can give it back."));
            }
            case "reclaim":
            {
                var (sim, p) = await PinAsync(a, ct);
                if (_s.Client is { } c) await _s.Names.ResolveAsync(c, new[] { p.OwnerID }, TimeSpan.FromSeconds(3), ct);
                return (Lang.F("Reclaim {0} from {1}", Land(sim, p), parcels.NameOf(p.OwnerID)), Lang.T("As the region's owner it becomes yours; they lose it."));
            }
            case "divide":
            {
                var sim = _s.Client?.Network.CurrentSim ?? throw new LeapError("no region");
                var (_, p) = await parcels.ParcelAsync(a.Has("half") ? new JsonObject() : CentreOf(a), ct);
                ParcelOps.Pin(a, sim, p);
                var r = ParcelOps.Rect(a, p, sim.SizeX) ?? throw new LeapError("no rectangle");
                Outline(sim, null, r);
                int area = (int)((r.E - r.W) * (r.N - r.S));
                return (Lang.F("Divide {0}", Land(sim, p)),
                    Lang.F("About {0} m² from {1},{2} to {3},{4} becomes a parcel of its own, with the same settings.", area, Num(r.W), Num(r.S), Num(r.E), Num(r.N)));
            }
            case "join":
            {
                var sim = _s.Client?.Network.CurrentSim ?? throw new LeapError("no region");
                var r = ParcelOps.Rect(a, null, sim.SizeX) ?? throw new LeapError("no rectangle");
                Outline(sim, null, r);
                var (_, p0) = await parcels.ParcelAsync(new JsonObject { ["at"] = new JsonArray((double)r.W + 1, (double)r.S + 1) }, ct);
                var (_, p1) = await parcels.ParcelAsync(new JsonObject { ["at"] = new JsonArray((double)r.E - 1, (double)r.N - 1) }, ct);
                return (Lang.F("Join {0} and {1}", Land(sim, p0), Land(sim, p1)),
                    Lang.T("They become one parcel (with any others the outline touches). The largest keeps its settings; the others' names, settings and lists are lost."));
            }
            case "terraform":
            {
                var sim = _s.Client?.Network.CurrentSim ?? throw new LeapError("no region");
                string action = (a.Str("action") ?? "reshape").Trim().ToLowerInvariant();
                string what;
                if (a.Bool("parcel", false))
                {
                    var (_, p) = await PinAsync(a, ct);
                    what = Lang.F("the land of {0}", Land(sim, p));
                }
                else
                {
                    var r = ParcelOps.Rect(a, null, sim.SizeX) ?? throw new LeapError("no area");
                    Outline(sim, null, r);
                    what = Lang.F("the land from {0},{1} to {2},{3}", Num(r.W), Num(r.S), Num(r.E), Num(r.N));
                }
                // one template an action (Flatten {0}, Raise {0}, ... ParcelOps.Actions, and Reshape {0})
                return (Lang.F($"{char.ToUpperInvariant(action[0])}{action[1..]} {{0}}", what),
                    Lang.T("Everyone sees the ground change. Revert goes back only to the region's last bake, not to how it is now."));
            }
            case "estateAdd" or "estateRemove":
            {
                bool add = op == "estateAdd";
                var list = (a.Str("list") ?? "").Trim().ToLowerInvariant();
                string who = list == "allowed_groups"
                    ? (parcels.GroupArg(a) ?? parcels.GroupArg(a, "to")) is { } g && _s.State.Groups.TryGetValue(g, out var grp) ? Lang.F("the group {0}", grp.Name) : Lang.T("a group")
                    : await NamesAsync(a, ct);
                string where = Lang.T(a.Bool("all_estates", false) ? "every estate you manage for this owner" : "this estate");
                string title = (list, add) switch
                {
                    ("managers", true) => Lang.F("Add {0} to the managers of {1}", who, where),
                    ("managers", false) => Lang.F("Remove {0} from the managers of {1}", who, where),
                    ("allowed", true) => Lang.F("Add {0} to the allowed residents of {1}", who, where),
                    ("allowed", false) => Lang.F("Remove {0} from the allowed residents of {1}", who, where),
                    ("allowed_groups", true) => Lang.F("Add {0} to the allowed groups of {1}", who, where),
                    ("allowed_groups", false) => Lang.F("Remove {0} from the allowed groups of {1}", who, where),
                    ("banned", true) => Lang.F("Add {0} to the banned residents of {1}", who, where),
                    ("banned", false) => Lang.F("Remove {0} from the banned residents of {1}", who, where),
                    (_, true) => Lang.F("Add {0} to the {1} list of {2}", who, list, where),
                    _ => Lang.F("Remove {0} from the {1} list of {2}", who, list, where),
                };
                string detail = (list, add) switch
                {
                    ("managers", true) => Lang.T("Estate managers can change nearly everything about the estate and its regions, its lists and its land."),
                    ("banned", true) => Lang.T("They can't enter any region of it."),
                    ("allowed", false) or ("allowed_groups", false) => Lang.T("If the estate isn't public, they can't enter it then."),
                    _ => "",
                };
                return (title, detail);
            }
            case "contribute":
            {
                var g = parcels.GroupArg(a);
                var name = g is { } gid && _s.State.Groups.TryGetValue(gid, out var grp) ? grp.Name : Lang.T("a group");
                int was = g is { } id && _s.State.Groups.TryGetValue(id, out var gr) ? gr.Contribution : 0;
                return (Lang.F("Contribute {0} m² of your land allowance to {1}", a.Int("area", 0), name),
                    Lang.F("Now {0} m². The group can hold that much land on your account; on Second Life it counts toward your monthly land use fee.", was));
            }
            case "kick":
                return (Lang.F("Kick {0} out of the estate", await NamesAsync(a, ct)), Lang.T("They're sent home."));
            case "sendHome":
                return a.Bool("everyone", false)
                    ? (Lang.F("Send everyone in {0} home", RegionName()), Lang.T("All but you are teleported to their homes."))
                    : (Lang.F("Send {0} home", await NamesAsync(a, ct)), Lang.T("They're teleported to their home."));
            case "message":
            {
                var text = (a.Str("text") ?? "").Trim();
                return (a.Bool("estate", false) ? Lang.T("Send this to everyone in the estate:")
                        : _s.Client?.Network.CurrentSim?.Name is { } region ? Lang.F("Send this to everyone in {0}:", region) : Lang.T("Send this to everyone in the region:"),
                    $"“{text}”");
            }
            case "setRegion":
            {
                var changes = a.Where(kv => kv.Key != "op").Select(kv => $"{kv.Key}: {Short(kv.Value)}");
                return (Lang.F("Change the settings of {0}", RegionName()), string.Join("; ", changes));
            }
            case "regionDebug":
            {
                var parts = new List<string>();
                void Part(string key, string off, string on)
                {
                    if (a.Bool(key) is { } v) parts.Add(Lang.T(v ? off : on));
                }
                Part("scripts_disabled", "scripts off", "scripts on");
                Part("collisions_disabled", "collisions off", "collisions on");
                Part("physics_disabled", "physics off", "physics on");
                return (Lang.F("Turn {0} in {1}", string.Join(", ", parts), RegionName()),
                    Lang.T("For everyone and everything in the region until switched back."));
            }
            case "restart":
            {
                int delay = Math.Clamp(a.Int("delay", 120), 30, 240);
                return (Lang.F("Restart {0} in {1} seconds", RegionName(), delay),
                    Lang.T("Everyone in it is warned; whoever is still there when it goes is logged out, you too."));
            }
            case "returnObjects":
            {
                var ids = a.UuidList("ids");
                if (a.Uuid("id") is { } one) ids.Add(one);
                var named = ids.Distinct().Select(id => _s.Estate.LastTop.TryGetValue(id, out var t) ? $"\"{t.Name}\" ({t.Owner})" : Lang.T("an object")).ToList();
                return (Lang.F(named.Count == 1 ? "Return {0} object to their owners" : "Return {0} objects to their owners", named.Count),
                    Lang.F("{0}. Taken out of the region, into their owners' inventories.", Confirmations.Listed(named, 5, "objects")));
            }
            case "bakeTerrain":
                return (Lang.F("Bake the terrain of {0}", RegionName()), Lang.T("The land as it is now becomes what reverting returns to; the old baked land is gone."));
        }
        return ($"SojournLand.{op}", Short(a));
    }

    static JsonObject CentreOf(JsonObject a) =>
        a.Num("west") is { } w && a.Num("south") is { } s && a.Num("east") is { } e && a.Num("north") is { } n
            ? new JsonObject { ["at"] = new JsonArray((w + e) / 2, (s + n) / 2) }
            : a["at"] is JsonArray xy && xy.Count >= 2 && xy[0] is not JsonArray ? new JsonObject { ["at"] = xy.DeepClone() } : new JsonObject();

    async Task<string> CovenantLineAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var cov = await _s.Estate.CovenantAsync(cts.Token);
            var text = (cov.Str("covenant") ?? "").Trim();
            if (text.StartsWith("none:")) return Lang.T("The estate has no covenant.");
            var flat = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var quoted = flat.Length > 180 ? flat[..180] + "..." : flat;
            return cov.Str("estate") is { } estate ? Lang.F("Buying agrees to {0}'s covenant: “{1}”", estate, quoted) : Lang.F("Buying agrees to the estate's covenant: “{0}”", quoted);
        }
        catch (Exception e) when (e is LeapError or OperationCanceledException)
        {
            return Lang.T("Buying agrees to the estate's covenant (it didn't come to show here).");
        }
    }

    static string Short(JsonNode? n)
    {
        var s = n is JsonValue v && v.TryGetValue<string>(out var str) ? str : n?.ToJsonString() ?? "";
        return s.Length > 80 ? s[..80] + "..." : s;
    }
}
