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
            return ($"{Title(op)} (the region didn't say which land)", $"{e.Message}: this will likely fail.");
        }
    }

    static string Title(string op) => op switch
    {
        "buy" => "Buy land", "buyPass" => "Buy a pass", "sell" => "Sell land", "abandon" => "Abandon land", "deed" => "Deed land to a group",
        _ => $"Land: {op}",
    };

    string Balance() => _s.BalanceKnown && _s.Client is { } c ? $"Your balance: L${c.Self.Balance}." : "";

    static string Join(params string[] parts) => string.Join(" ", parts.Where(p => p.Length > 0));

    string Land(Simulator sim, Parcel p) =>
        $"{(p.Name is { Length: > 0 } n ? $"\"{n}\"" : "a parcel")} ({p.Area} m² in {sim.Name})";

    async Task<string> NamesAsync(JsonObject a, CancellationToken ct)
    {
        var ids = await _s.Parcels.PeopleAsync(a, ct);
        if (_s.Client is { } c) await _s.Names.ResolveAsync(c, ids, TimeSpan.FromSeconds(4), ct);
        var names = ids.Take(6).Select(id => _s.Names.Complete(id, $"someone ({id.ToString()[..8]})")).ToList();
        return string.Join(", ", names) + (ids.Count > 6 ? $" and {ids.Count - 6} more" : "");
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

    static string Hours(JsonObject a) => a.Num("hours") is { } h && h > 0 ? $" for {h:0.#} hour{(h == 1 ? "" : "s")}" : "";

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
                return ($"Change the settings of {Land(sim, p)}", string.Join("; ", changes));
            }
            case "allow" or "unallow" or "ban" or "unban":
            {
                var (sim, p) = await PinAsync(a, ct);
                var who = await NamesAsync(a, ct);
                return op switch
                {
                    "ban" => ($"Ban {who} from {Land(sim, p)}{Hours(a)}", "They can't enter it; anyone of them on it now is sent off it."),
                    "unban" => ($"Lift the ban on {who} from {Land(sim, p)}", ""),
                    "allow" => ($"Let {who} into {Land(sim, p)}{Hours(a)}", (p.Flags & ParcelFlags.UseAccessList) != 0
                        ? "It's closed to all but its allowed list, and they'll be on it." : "On its allowed list (it's open to everyone now, so that matters once it's closed)."),
                    _ => ($"Take {who} off the allowed list of {Land(sim, p)}", (p.Flags & ParcelFlags.UseAccessList) != 0 ? "It's closed to all but that list: they can't enter it then." : ""),
                };
            }
            case "freeze":
            {
                var who = await NamesAsync(new JsonObject { ["to"] = a.Str("to") }, ct);
                return ($"Freeze {who} where they stand", "As the land's owner: they can't move on it until you free them or the region lets them go.");
            }
            case "sell":
            {
                var (sim, p) = await PinAsync(a, ct);
                int price = a.Int("price", 0);
                string to = a.Has("to") ? $"Only {await NamesAsync(a, ct)} can buy it." : _s.Parcels.GroupArg(a, "to_group") is { } g
                    ? $"Only the group {(_s.State.Groups.TryGetValue(g, out var grp) ? grp.Name : "named")} can buy it." : "Anyone can buy it.";
                string objects = a.Bool("objects") == true ? "Your objects on it go with it." : "";
                return (price == 0 ? $"Offer {Land(sim, p)} for nothing" : $"Put {Land(sim, p)} up for sale for L${price}",
                    Join(to, objects, price == 0 ? "They get it for L$0: the land is given away when they take it." : ""));
            }
            case "buy":
            {
                var (sim, p) = await PinAsync(a, ct);
                a["price"] = p.SalePrice;
                a["area"] = p.Area;
                var group = parcels.GroupArg(a) ?? UUID.Zero;
                if (parcels.BuyRefusal(p, group) is { } no) return ($"Buy {Land(sim, p)}", $"It can't be bought now: {no}.");
                if (_s.Client is { } c) await _s.Names.ResolveAsync(c, new[] { p.OwnerID }, TimeSpan.FromSeconds(3), ct);
                var covenant = await CovenantLineAsync(ct);
                var forWhom = group != UUID.Zero ? $" for the group {(_s.State.Groups.TryGetValue(group, out var g) ? g.Name : "named")}" : "";
                return ($"Buy {Land(sim, p)}{forWhom} for L${p.SalePrice}",
                    Join($"From {parcels.NameOf(p.OwnerID)}{((p.Flags & ParcelFlags.SellParcelObjects) != 0 ? ", with the objects on it" : "")}.", Balance(), covenant,
                        "On the mainland, owning land needs Premium and can raise your monthly land use fee, which Linden Lab bills."));
            }
            case "buyPass":
            {
                var (sim, p) = await PinAsync(a, ct, pass: true);
                a["price"] = p.PassPrice;
                if (parcels.PassRefusal(p) is { } no) return ($"Buy a pass to {Land(sim, p)}", $"It can't be bought: {no}.");
                return ($"Buy a pass to {Land(sim, p)} for L${p.PassPrice}", Join($"{p.PassHours:0.#} hours of access.", Balance()));
            }
            case "abandon":
            {
                var (sim, p) = await PinAsync(a, ct);
                return ($"Abandon {Land(sim, p)}", "It stops being yours for good: the estate's owner (Governor Linden on the mainland) gets it, and you'd have to buy it back.");
            }
            case "deed":
            {
                var (sim, p) = await PinAsync(a, ct);
                var g = parcels.GroupArg(a) ?? p.GroupID;
                var name = _s.State.Groups.TryGetValue(g, out var grp) ? grp.Name : "its group";
                return ($"Deed {Land(sim, p)} to {name}", "The group owns it from then on; only someone with its powers can give it back.");
            }
            case "reclaim":
            {
                var (sim, p) = await PinAsync(a, ct);
                if (_s.Client is { } c) await _s.Names.ResolveAsync(c, new[] { p.OwnerID }, TimeSpan.FromSeconds(3), ct);
                return ($"Reclaim {Land(sim, p)} from {parcels.NameOf(p.OwnerID)}", "As the region's owner it becomes yours; they lose it.");
            }
            case "divide":
            {
                var sim = _s.Client?.Network.CurrentSim ?? throw new LeapError("no region");
                var (_, p) = await parcels.ParcelAsync(a.Has("half") ? new JsonObject() : CentreOf(a), ct);
                ParcelOps.Pin(a, sim, p);
                var r = ParcelOps.Rect(a, p, sim.SizeX) ?? throw new LeapError("no rectangle");
                Outline(sim, null, r);
                int area = (int)((r.E - r.W) * (r.N - r.S));
                return ($"Divide {Land(sim, p)}", $"About {area} m² from {r.W:0},{r.S:0} to {r.E:0},{r.N:0} becomes a parcel of its own, with the same settings.");
            }
            case "join":
            {
                var sim = _s.Client?.Network.CurrentSim ?? throw new LeapError("no region");
                var r = ParcelOps.Rect(a, null, sim.SizeX) ?? throw new LeapError("no rectangle");
                Outline(sim, null, r);
                var (_, p0) = await parcels.ParcelAsync(new JsonObject { ["at"] = new JsonArray((double)r.W + 1, (double)r.S + 1) }, ct);
                var (_, p1) = await parcels.ParcelAsync(new JsonObject { ["at"] = new JsonArray((double)r.E - 1, (double)r.N - 1) }, ct);
                return ($"Join {Land(sim, p0)} and {Land(sim, p1)}",
                    "They become one parcel (with any others the outline touches). The largest keeps its settings; the others' names, settings and lists are lost.");
            }
            case "terraform":
            {
                var sim = _s.Client?.Network.CurrentSim ?? throw new LeapError("no region");
                string action = (a.Str("action") ?? "reshape").Trim().ToLowerInvariant();
                string what;
                if (a.Bool("parcel", false))
                {
                    var (_, p) = await PinAsync(a, ct);
                    what = $"the land of {Land(sim, p)}";
                }
                else
                {
                    var r = ParcelOps.Rect(a, null, sim.SizeX) ?? throw new LeapError("no area");
                    Outline(sim, null, r);
                    what = $"the land from {r.W:0},{r.S:0} to {r.E:0},{r.N:0}";
                }
                return ($"{char.ToUpperInvariant(action[0])}{action[1..]} {what}",
                    "Everyone sees the ground change. Revert goes back only to the region's last bake, not to how it is now.");
            }
            case "estateAdd" or "estateRemove":
            {
                bool add = op == "estateAdd";
                var list = (a.Str("list") ?? "").Trim().ToLowerInvariant();
                string who = list == "allowed_groups"
                    ? (parcels.GroupArg(a) ?? parcels.GroupArg(a, "to")) is { } g && _s.State.Groups.TryGetValue(g, out var grp) ? $"the group {grp.Name}" : "a group"
                    : await NamesAsync(a, ct);
                string where = a.Bool("all_estates", false) ? "every estate you manage for this owner" : "this estate";
                string listName = list switch { "managers" => "managers", "allowed" => "allowed residents", "allowed_groups" => "allowed groups", "banned" => "banned residents", _ => list };
                string detail = (list, add) switch
                {
                    ("managers", true) => "Estate managers can change nearly everything about the estate and its regions, its lists and its land.",
                    ("banned", true) => "They can't enter any region of it.",
                    ("allowed", false) or ("allowed_groups", false) => "If the estate isn't public, they can't enter it then.",
                    _ => "",
                };
                return ($"{(add ? "Add" : "Remove")} {who} {(add ? "to" : "from")} the {listName} of {where}", detail);
            }
            case "contribute":
            {
                var g = parcels.GroupArg(a);
                var name = g is { } gid && _s.State.Groups.TryGetValue(gid, out var grp) ? grp.Name : "a group";
                int was = g is { } id && _s.State.Groups.TryGetValue(id, out var gr) ? gr.Contribution : 0;
                return ($"Contribute {a.Int("area", 0)} m² of your land allowance to {name}",
                    $"Now {was} m². The group can hold that much land on your account; on Second Life it counts toward your monthly land use fee.");
            }
            case "kick":
                return ($"Kick {await NamesAsync(a, ct)} out of the estate", "They're sent home.");
            case "sendHome":
                return a.Bool("everyone", false)
                    ? ($"Send everyone in {_s.Client?.Network.CurrentSim?.Name ?? "this region"} home", "All but you are teleported to their homes.")
                    : ($"Send {await NamesAsync(a, ct)} home", "They're teleported to their home.");
            case "message":
            {
                var text = (a.Str("text") ?? "").Trim();
                return ($"Send this to everyone in {(a.Bool("estate", false) ? "the estate" : _s.Client?.Network.CurrentSim?.Name ?? "the region")}:", $"“{text}”");
            }
            case "setRegion":
            {
                var changes = a.Where(kv => kv.Key != "op").Select(kv => $"{kv.Key}: {Short(kv.Value)}");
                return ($"Change the settings of {_s.Client?.Network.CurrentSim?.Name ?? "this region"}", string.Join("; ", changes));
            }
            case "regionDebug":
            {
                var parts = new List<string>();
                void Part(string key, string what)
                {
                    if (a.Bool(key) is { } v) parts.Add($"{what} {(v ? "off" : "on")}");
                }
                Part("scripts_disabled", "scripts");
                Part("collisions_disabled", "collisions");
                Part("physics_disabled", "physics");
                return ($"Turn {string.Join(", ", parts)} in {_s.Client?.Network.CurrentSim?.Name ?? "this region"}",
                    "For everyone and everything in the region until switched back.");
            }
            case "restart":
            {
                int delay = Math.Clamp(a.Int("delay", 120), 30, 240);
                return ($"Restart {_s.Client?.Network.CurrentSim?.Name ?? "this region"} in {delay} seconds",
                    "Everyone in it is warned; whoever is still there when it goes is logged out, you too.");
            }
            case "returnObjects":
            {
                var ids = a.UuidList("ids");
                if (a.Uuid("id") is { } one) ids.Add(one);
                var named = ids.Distinct().Take(5).Select(id => _s.Estate.LastTop.TryGetValue(id, out var t) ? $"\"{t.Name}\" ({t.Owner})" : "an object").ToList();
                return ($"Return {ids.Distinct().Count()} object{(ids.Distinct().Count() == 1 ? "" : "s")} to their owners",
                    string.Join(", ", named) + (ids.Distinct().Count() > 5 ? " and more" : "") + ". Taken out of the region, into their owners' inventories.");
            }
            case "bakeTerrain":
                return ($"Bake the terrain of {_s.Client?.Network.CurrentSim?.Name ?? "this region"}", "The land as it is now becomes what reverting returns to; the old baked land is gone.");
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
            if (text.StartsWith("none:")) return "The estate has no covenant.";
            var flat = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return $"Buying agrees to {cov.Str("estate") ?? "the estate"}'s covenant: “{(flat.Length > 180 ? flat[..180] + "..." : flat)}”";
        }
        catch (Exception e) when (e is LeapError or OperationCanceledException)
        {
            return "Buying agrees to the estate's covenant (it didn't come to show here).";
        }
    }

    static string Short(JsonNode? n)
    {
        var s = n is JsonValue v && v.TryGetValue<string>(out var str) ? str : n?.ToJsonString() ?? "";
        return s.Length > 80 ? s[..80] + "..." : s;
    }
}
