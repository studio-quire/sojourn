using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

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
            return Json.Error($"{pump}.{op} isn't here");
        if (api.NeedsSession && !s.Online) return Json.Error(NotIn);
        try { return await handler.Run(args, ct) as JsonObject ?? new JsonObject(); }
        catch (LeapError e) { return Json.Error(e.Message); }
        catch (NotConnected) { return Json.Error(NotIn); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return Json.Error($"{e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>Through the dispatcher and its gate (the person's confirmation for spending, giving, deleting).</summary>
    public async Task<JsonObject> GatedAsync(string pump, string op, JsonObject? args = null, CancellationToken ct = default)
    {
        args ??= new JsonObject();
        args["op"] = op;
        try { return await s.Api.CallAsync(pump, args, reply: true, TimeSpan.FromSeconds(150), ct) as JsonObject ?? new JsonObject(); }
        catch (NotConnected) { return Json.Error(NotIn); }
    }

    /// <summary>Why an op can't run out of world: logging in again after a drop is said as that.</summary>
    string NotIn => s.WantsOnline ? "not in world right now; logging in again" : "not logged in";

    public static string? Error(JsonObject r) => r["error"] is JsonValue v && v.TryGetValue<string>(out var e) ? e : null;

    // --- where things are -------------------------------------------------------------------------------------------

    public GridClient? Client => s.Client;

    /// <summary>The avatar's position in its region and its compass heading (the seat's, sitting).</summary>
    public (Vector3 Pos, double Heading, string Region)? Me()
    {
        if (s.Client is not { } c || c.Network.CurrentSim is not { } sim) return null;
        var q = c.Self.SittingOn != 0 ? c.Self.SimRotation : c.Self.Movement.BodyRotation;
        return (s.Motion.Position(c), Motion.Heading(q), sim.Name);
    }

    public sealed record Person(UUID Id, string Name, string Username, Vector3 Pos, double Dist);

    /// <summary>People around, nearest first (in view, then the map's coarse positions), as TesselAgent people.</summary>
    public async Task<List<Person>> PeopleAsync(double within = 96)
    {
        var r = await ReadAsync("TesselAgent", "people", new JsonObject { ["dist"] = within });
        var list = new List<Person>();
        foreach (var p in (r["people"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            if (!UUID.TryParse(p.Str("id"), out var id) || p["pos"] is not JsonArray pos || pos.Count < 3) continue;
            var user = p.Str("username") ?? "";
            var name = p.Str("display_name") is { Length: > 0 } dn ? dn : user.Length > 0 ? user : "someone";
            list.Add(new Person(id, name, user, new Vector3((float)pos[0]!.GetValue<double>(), (float)pos[1]!.GetValue<double>(), (float)pos[2]!.GetValue<double>()),
                p["dist"]?.GetValue<double>() ?? 0));
        }
        return list;
    }

    /// <summary>The nearest person whose name (display or user) starts with the words, else contains them.</summary>
    public async Task<Person?> FindPersonAsync(string words)
    {
        var w = words.Trim().ToLowerInvariant();
        if (w.Length == 0) return null;
        var all = await PeopleAsync(512);
        bool Starts(string n) => n.ToLowerInvariant().StartsWith(w) || n.ToLowerInvariant().Split(' ', '.').Any(part => part.StartsWith(w));
        return all.FirstOrDefault(p => Starts(p.Name) || Starts(p.Username))
               ?? all.FirstOrDefault(p => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || p.Username.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    public sealed record Thing(UUID Id, string Name, Vector3 Pos, double Dist, bool Sit, bool Touch, string? Text)
    {
        /// <summary>Something the person wears (Objects' Show: what you wear; a HUD or on the body): Text says where.</summary>
        public bool Hud { get; init; }
        /// <summary>The person's own, in the world: theirs to take or delete.</summary>
        public bool Mine { get; init; }
    }

    /// <summary>Objects around (root objects, not attachments), nearest first; query: words in the name, description
    /// or floating text. renew: scripted ones' names older than this many seconds asked again (0: all of them), as a
    /// script may have renamed its object (a self-naming sofa).</summary>
    public async Task<List<Thing>> ObjectsAsync(double within = 30, int limit = 60, string? query = null, double? renew = null)
    {
        var args = new JsonObject { ["dist"] = within, ["limit"] = limit, ["timeout"] = 3 };
        if (!string.IsNullOrWhiteSpace(query)) args["query"] = query.Trim();
        if (renew is { } secs) args["renew"] = secs;
        var r = await ReadAsync("TesselObjects", "nearby", args);
        var list = new List<Thing>();
        foreach (var o in (r["objects"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            if (!UUID.TryParse(o.Str("id"), out var id) || o["pos"] is not JsonArray pos || pos.Count < 3) continue;
            var flags = (o["flags"] as JsonArray ?? new()).Select(f => f?.ToString()).ToHashSet();
            var name = o.Str("name") is { Length: > 0 } n ? n : "object";
            list.Add(new Thing(id, name, new Vector3((float)pos[0]!.GetValue<double>(), (float)pos[1]!.GetValue<double>(), (float)pos[2]!.GetValue<double>()),
                o["dist"]?.GetValue<double>() ?? 0, o.Str("click_action") == "sit", flags.Contains("touch"), o.Str("text")) { Mine = flags.Contains("mine") });
        }
        return list.OrderBy(t => t.Dist).ToList();
    }

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
