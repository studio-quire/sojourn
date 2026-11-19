using System.Globalization;
using System.Runtime.CompilerServices;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;
using Sojourn.Core.World;
using Sojourn.Render.Collision;
using Sojourn.Render.World;
using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;

namespace Sojourn.Access.Walk;

/// <summary>
/// Why a walk went as it did, in the session log a tester sends (Help > Send a log) as well as the walk log: the plan
/// and the planner's reasons (Commands' walks and the assistant's), and, where a walk sticks, what the collision model has
/// just ahead of her, what it hasn't got yet, the parcels there and the region's own word. Until 2026-11-19 these went
/// only to SOJOURN_WALK_LOG, a test switch, so the blind tester's walks at a place on Second Life that kept sticking
/// and ending in "no way" (her log of 2026-11-18) couldn't say whether what stopped her was in the model at all.
/// Positions and object names, never people's names or words. Claude Opus 5.5, 2026-11-19.
/// </summary>
public sealed partial class WalkMode
{
    /// <summary>A walk's line for the session log (and the walk log).</summary>
    public void Note(string line)
    {
        Log(line);
        S.Log.LogInformation("{Line}", line);
    }

    /// <summary>The planner's reasons (PathPlanner.Diagnosis) as a line after <paramref name="head"/>, once the names of
    /// what it found are in (asked for, up to 2 s; the walk goes on meanwhile).</summary>
    public void NoteWhy(string head, PathPlanner.Diagnosis d) => _ = Task.Run(async () =>
    {
        try
        {
            await NamesAsync(d.AtStart.Concat(d.AtGoal).Select(s => (s.Region, s.Prim)));
            Note($"{head}{PathPlanner.Say(d, Thing)}");
        }
        catch (Exception x) { Note($"{head}{d.What} ({x.GetType().Name} naming what's there)"); }
    });

    /// <summary>A walk that stuck: <paramref name="head"/> (what stuck and where, the caller's), then what's there
    /// (StuckAsync), written once the names are in; the walk goes on meanwhile. Positions in her region's frame.</summary>
    public void NoteStuck(string head, Vector3 at, float feet, Vector3 toward) => _ = Task.Run(async () =>
    {
        try { Note($"{head}; {await StuckAsync(at, feet, toward)}"); }
        catch (Exception x) { Note($"{head}; ({x.GetType().Name} looking at what's there: {x.Message})"); }
    });

    /// <summary>How far the stuck line's rays look ahead and to the sides; how far on the floor ahead is measured and
    /// the second pair of side rays cast.</summary>
    const float StuckLook = 3f, AcrossLook = 1.5f, FloorOn = 0.6f, AcrossOn = 0.4f;

    /// <summary>What's where a walk stuck, for its line, every ray cast at once:
    /// - toward where the stretch was heading, from her shin (a kerb or a step the planner's 0.4 m step takes and the
    ///   knee's ray passes over) to her knee, waist, chest and the top of her head (taller avatars meet what's over the
    ///   planner's 1.8 m), each what it met (named, its kind and physics shape type, how far) or nothing within 3 m;
    /// - the floor under her and 0.6 m on toward it (the model's or the ground, whichever is higher), against her feet:
    ///   a step up or down the region may not take as the planner does;
    /// - across at her waist, where she stands and 0.4 m on, what each side has within 1.5 m and the gap between (a
    ///   doorway down whose middle the rays ahead see nothing; the planner passes 0.6 m);
    /// - sculpts and meshes within 3 m whose shapes the model hasn't got (still to come, or given up on), which the
    ///   region counts all the same (prims the region never sent can't be among them);
    /// - the parcel she's on and the one 2 m ahead, with who may enter; a parcel's line the region sent her lately;
    /// - and the region's word that its autopilot stopped, only where none of our cancels came just before it (the
    ///   region answers each of ours with it: 1f50470).
    /// Names and unknown parcels are asked for, up to 2 s. Things are named by the region whose local id they have, as
    /// near a border the model's rays meet a neighbour's prims.</summary>
    public async Task<string> StuckAsync(Vector3 at, float feet, Vector3 toward)
    {
        long now = Environment.TickCount64;
        var flat = new Vector2(toward.X - at.X, toward.Y - at.Y);
        var dir = flat.LengthSquared() > 0.01f ? Vector3.Normalize(new Vector3(flat, 0))
            : Dir(S.Client is { } me ? YawOf(me.Self.Movement.BodyRotation) : 0);
        var left = new Vector3(-dir.Y, dir.X, 0);
        float tall = Math.Clamp(2 * (at.Z - feet), 1.2f, 3f);
        var heights = new (string Where, float Z)[] { ("shin", 0.2f), ("knee", 0.5f), ("waist", 0.9f), ("chest", 1.3f), ("head", tall - 0.1f) };
        var rays = heights.Select(h => (new Vector3(at.X, at.Y, feet + h.Z), dir, StuckLook)).ToList();
        int underfoot = rays.Count;
        rays.Add((at, -Vector3.UnitZ, 4f));
        int floorOn = rays.Count;
        var onward = new Vector3(at.X, at.Y, feet + 1f) + dir * FloorOn;
        rays.Add((onward, -Vector3.UnitZ, 3f));
        int across = rays.Count;
        foreach (float by in new[] { 0f, AcrossOn })
        {
            var waist = new Vector3(at.X, at.Y, feet + 0.9f) + dir * by;
            rays.Add((waist, left, AcrossLook));
            rays.Add((waist, -left, AcrossLook));
        }
        var got = await _rays.CollideManyAsync(rays);
        // (what doesn't count anyway, phantom or of no physics, isn't missing)
        var missing = _rays.Missing(at, StuckLook).Where(m => _rays.Physics.Collide(m.Prim) is not (CollideAs.Passable or CollideAs.None)).ToList();
        long stopped = Interlocked.Read(ref _autopilotStoppedAt), stoppedAgo = stopped > 0 ? now - stopped : -1;
        var turned = _parcelLine;

        // the parcels here and ahead, asked of the region where it hasn't said
        var ahead = at + dir * 2f;
        var points = new[] { at, ahead };
        if (S.Client is { Network.CurrentSim: { } sim } c)
            foreach (var p in points)
                if (ParcelAt(c, sim, p) == null)
                    try { c.Parcels.RequestParcelProperties(sim, p.Y + 1, p.X + 1, p.Y - 1, p.X - 1, 0, false); } catch (Exception) { }

        var hits = got?.Hits ?? new CollideHit?[rays.Count];
        await NamesAsync(hits.Where(h => h is { Avatar: false }).Select(h => (h!.Value.Region, h.Value.LocalId))
            .Concat(missing.Take(4).Select(m => (m.Region, m.Prim.LocalID))));

        // each thing named in full once a line, by its name alone after that
        var named = new HashSet<(ulong, uint)>();
        string What(CollideHit x) => x.Avatar ? "someone" : named.Add((x.Region, x.LocalId)) ? Thing(x.Region, x.LocalId) : Name(x.Region, x.LocalId);
        string Met(CollideHit? h, float look) => h is { } x ? Inv($"{What(x)}, {x.Distance:0.00} m") : Inv($"nothing within {look:0.#} m");
        static bool Same(CollideHit? a, CollideHit? b) => a is not { } x ? b is null
            : b is { } y && x.LocalId == y.LocalId && x.Region == y.Region && x.Avatar == y.Avatar && MathF.Abs(x.Distance - y.Distance) < 0.005f;
        string Rel(float z)
        {
            float d = z - feet;
            return MathF.Abs(d) < 0.005f ? "level with her feet" : Inv($"{MathF.Abs(d):0.00} m {(d > 0 ? "over" : "under")} her feet");
        }
        // the floor a ray cast down from `from` came to: what it met, or the ground where that's higher (the model has
        // no ground)
        string? Floor(CollideHit? h, Vector3 from, float max)
        {
            float? ground = GroundAt(from.X, from.Y) is { } g && g <= from.Z && g >= from.Z - max ? g : null;
            if (h is { } x && (ground is not { } g1 || from.Z - x.Distance >= g1 - 0.01f)) return $"{What(x)}, {Rel(from.Z - x.Distance)}";
            return ground is { } g2 ? $"the ground, {Rel(g2)}" : null;
        }

        var parts = new List<string>();
        var said = new List<string>();
        for (int i = 0; i < heights.Length; i++)
            said.Add($"at the {heights[i].Where} {(i > 0 && Same(hits[i], hits[i - 1]) ? "the same" : Met(hits[i], StuckLook))}");
        parts.Add($"toward it the model has {string.Join(", ", said)}");
        parts.Add(Floor(hits[underfoot], at, 4f) is { } under ? $"underfoot: {under}" : Inv($"nothing underfoot within {4f - (at.Z - feet):0.#} m of her feet"));
        // (a point past what the knee's or the waist's ray met is behind it or inside it: rays meet faces from outside,
        // so one cast from inside a wall finds what's beyond or under it)
        bool Past(float on, bool knee) => knee && hits[1] is { } k && k.Distance < on || hits[2] is { } w && w.Distance < on;
        var beyond = Past(FloorOn, true) ? ", past what's ahead" : "";
        parts.Add(Floor(hits[floorOn], onward, 3f) is { } on ? Inv($"the floor {FloorOn:0.0} m on{beyond}: {on}") : Inv($"no floor {FloorOn:0.0} m on{beyond} within 2 m under her feet"));
        string Across(int i)
        {
            CollideHit? l = hits[i], r = hits[i + 1];
            if (l == null && r == null) return Inv($"nothing within {AcrossLook:0.#} m either side");
            string Side(CollideHit? h) => h is { } x ? Inv($"{What(x)} at {x.Distance:0.00} m") : Inv($"nothing within {AcrossLook:0.#} m");
            return $"left {Side(l)}, right {Side(r)}" + (l is { } a && r is { } b ? Inv($" ({a.Distance + b.Distance:0.00} m between)") : "");
        }
        parts.Add(Inv($"across at the waist here, {Across(across)}, and {AcrossOn:0.0} m on, ")
                  + (Past(AcrossOn, false) ? "past what's ahead at the waist" : Across(across + 2)));
        parts.Add(missing.Count == 0 ? $"every shape within {StuckLook:0} m in the model"
            : $"not in the model within {StuckLook:0} m: " + string.Join(", ", missing.Take(4).Select(m =>
                $"{Thing(m.Region, m.Prim.LocalID)}, a {(m.Kind == ShapeKind.Mesh ? "mesh" : "sculpt")} {(m.Failed ? "given up on" : "still to come")}, "
                + (m.D < 0.05f ? "her middle in its box" : Inv($"{m.D:0.0} m from its box")))
              ) + (missing.Count > 4 ? $" and {missing.Count - 4} more" : ""));
        if (S.Client is { Network.CurrentSim: { } sim2 } c2)
        {
            var here = ParcelAt(c2, sim2, at);
            var there = ParcelAt(c2, sim2, ahead);
            parts.Add($"the parcel here {Parcel(here)}; 2 m ahead {(here != null && there?.LocalID == here.LocalID ? "the same" : Parcel(there))}");
        }
        parts.Add(turned is { } pl && now - pl.At < 60_000
            ? Inv($"the region sent a parcel's line she may not cross {(now - pl.At) / 1000.0:0.0} s before: {pl.Name} ({pl.Id}), {pl.Why}")
            : "no parcel's line sent her in the last minute");
        if (stoppedAgo is >= 0 and < 15_000)
            parts.Add(Inv($"the region said its autopilot stopped {stoppedAgo / 1000.0:0.0} s before, with no cancel of ours just before it"));
        return string.Join("; ", parts);
    }

    static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    float? GroundAt(float x, float y) =>
        S.Region is { } w && x >= 0 && y >= 0 && x < w.SizeX && y < w.SizeY ? w.TerrainHeight(x, y) : null;

    Parcel? ParcelAt(GridClient c, Simulator sim, Vector3 at)
    {
        try
        {
            int id = c.Parcels.GetParcelLocalID(sim, new LibreMetaverse.Vector3(Math.Clamp(at.X, 0, 255.9f), Math.Clamp(at.Y, 0, 255.9f), at.Z));
            return id != 0 && sim.Parcels.TryGetValue(id, out var p) ? p : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>A parcel for the log: its name and local id, and who may enter, as far as she can know it (an allowed list
    /// or a ban list is the owner's to see; only that one's in use).</summary>
    string Parcel(Parcel? p)
    {
        if (p == null) return "not known";
        var f = p.Flags;
        var who = new List<string>();
        if ((f & ParcelFlags.UseAccessGroup) != 0)
            who.Add(S.State.Groups.ContainsKey(p.GroupID) ? "its group (hers)" : "its group (not hers)");
        if ((f & ParcelFlags.UseAccessList) != 0) who.Add("its allowed list");
        if ((f & ParcelFlags.UsePassList) != 0) who.Add("passes sold");
        var access = who.Count > 0 ? $"only {string.Join(" or ", who)}" : "open to all";
        if ((f & ParcelFlags.UseBanList) != 0) access += ", a ban list";
        if ((f & ParcelFlags.DenyAnonymous) != 0) access += ", none without payment info";
        if ((f & ParcelFlags.DenyAgeUnverified) != 0) access += ", age-verified only";
        if (S.Client is { } c && p.OwnerID == c.Self.AgentID) access += ", hers";
        return $"{(string.IsNullOrWhiteSpace(p.Name) ? "unnamed" : p.Name.Trim())} ({p.LocalID}): {access}";
    }

    /// <summary>A prim by the region whose local id it has (0: the one she's in), with that region's world.</summary>
    (Primitive Prim, RegionWorld World)? Find(ulong region, uint id)
    {
        var w = region == 0 ? S.Region : S.World.Region(S.GridKey, region);
        return w != null && w.Prims.TryGetValue(id, out var p) ? (p, w) : null;
    }

    /// <summary>A prim's name as the log says it: the part's and its object's ("Step of Grand Stairs"), else "unnamed".</summary>
    string Name(ulong region, uint id)
    {
        if (Find(region, id) is not { } f) return Inv($"something gone ({id})");
        var root = S.Objects.RootOf(f.World, f.Prim);
        static string? Named(Primitive x) => x.Properties?.Name is { } n && !Unnamed.Contains(n.Trim()) ? n.Trim() : null;
        string? part = Named(f.Prim), whole = root.LocalID == f.Prim.LocalID ? null : Named(root);
        return part != null && whole != null && part != whole ? $"{part} of {whole}" : part ?? whole ?? "unnamed";
    }

    /// <summary>A prim as the log says it the first time: its name, its kind and the physics shape type it counts by.</summary>
    string Thing(ulong region, uint id)
    {
        if (Find(region, id) is not { } f) return Inv($"something gone ({id})");
        var kind = CollisionShapes.KeyOf(f.Prim)?.Kind switch { ShapeKind.Mesh => "mesh", ShapeKind.Sculpt => "sculpt", _ => "prim" };
        var physics = _rays.Physics.Collide(f.Prim) switch
        {
            CollideAs.Prim => "prim", CollideAs.ConvexHull => "convex hull", CollideAs.None => "none", CollideAs.Passable => "phantom",
            _ => "not known yet",
        };
        return $"{Name(region, id)} ({kind}, physics {physics})";
    }

    /// <summary>Names asked for the prims (and their objects) that lack them, of each region they're in, waited for up
    /// to 2 s.</summary>
    async Task NamesAsync(IEnumerable<(ulong Region, uint Id)> ids)
    {
        var want = new Dictionary<ulong, (RegionWorld World, Dictionary<uint, Primitive> Prims)>();
        foreach (var (region, id) in ids)
        {
            if (id == 0 || Find(region, id) is not { } f) continue;
            if (!want.TryGetValue(f.World.Handle, out var of)) want[f.World.Handle] = of = (f.World, new());
            of.Prims.TryAdd(f.Prim.LocalID, f.Prim);
            var root = S.Objects.RootOf(f.World, f.Prim);
            of.Prims.TryAdd(root.LocalID, root);
        }
        if (want.Count == 0 || S.Client is not { } c) return;
        await Task.WhenAll(want.Values.Select(async of =>
        {
            Simulator? sim;
            lock (c.Network.Simulators) sim = c.Network.Simulators.FirstOrDefault(s => s.Handle == of.World.Handle);
            if (sim == null) return;   // (a region with no circuit now: named as far as they are)
            try { await S.Objects.FetchAsync(of.World, of.Prims.Values.ToList(), TimeSpan.FromSeconds(2), CancellationToken.None, sim: sim); }
            catch (Exception) { /* named as far as they came */ }
        }));
    }

    // --- the region's word: parcels' lines, its autopilot -------------------------------------------------------------

    sealed record ParcelLine(long At, string Name, int Id, string Why);
    volatile ParcelLine? _parcelLine;
    long _autopilotStoppedAt, _ownCancelAt;
    readonly ConditionalWeakTable<GridClient, object> _newsHooked = new();
    readonly Dictionary<(string, int, int), long> _linesLogged = new();

    /// <summary>The region's word that its autopilot stopped within this long of a cancel of ours is its answer to that.</summary>
    const long OwnCancelAnswer = 3000;

    /// <summary>A walk of ours let go of the region's autopilot (Commands.CancelAutoPilot): its answer, AutopilotCanceled,
    /// isn't the region's own word.</summary>
    public void OwnCancel() => Interlocked.Exchange(ref _ownCancelAt, Environment.TickCount64);

    void HookWalkNews()
    {
        lock (AgentSession.ClientHooks) AgentSession.ClientHooks.Add(HookWalkNews);
        if (S.Client is { } c) HookWalkNews(c);
    }

    /// <summary>A parcel's line the region sends her (ParcelProperties with a collision sequence id, from which the viewer
    /// draws its ban lines: llparcel.h; sent as she nears a parcel she may not enter, not only when it pushes her back)
    /// goes in the session log as it comes, once in 30 s a parcel; and its word that its autopilot stopped
    /// (AutopilotCanceled), where it doesn't answer a cancel of ours, is timed for a stuck walk's line.</summary>
    void HookWalkNews(GridClient c)
    {
        lock (_newsHooked)
        {
            if (_newsHooked.TryGetValue(c, out _)) return;
            _newsHooked.Add(c, new object());
        }
        c.Parcels.ParcelProperties += (_, e) =>
        {
            var why = e.SequenceID switch
            {
                ParcelLines.CollisionBanned => "banned from it",
                ParcelLines.CollisionNotOnList => "not on its allowed list",
                ParcelLines.CollisionNotInGroup => "not in its group",
                _ => null,
            };
            if (why == null || e.Parcel is not { } p) return;
            long now = Environment.TickCount64;
            var name = string.IsNullOrWhiteSpace(p.Name) ? "unnamed" : p.Name.Trim();
            _parcelLine = new ParcelLine(now, name, p.LocalID, why);
            var key = (e.Simulator?.Name ?? "", p.LocalID, e.SequenceID);
            lock (_linesLogged)
            {
                if (_linesLogged.TryGetValue(key, out var was) && now - was < 30_000) return;
                _linesLogged[key] = now;
            }
            var me = c.Self.SimPosition;
            Note(Inv($"parcel line: the region says she may not enter {name} ({p.LocalID}) in {e.Simulator?.Name}: {why}{(p.Flags.HasFlag(ParcelFlags.UsePassList) ? ", passes sold" : "")}; she was at {me.X:0.0},{me.Y:0.0},{me.Z:0.0}"));
        };
        c.Self.AlertMessage += (_, e) =>
        {
            if (e.NotificationId != "AutopilotCanceled" && e.Message != "AUTOPILOT_CANCELED") return;
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _ownCancelAt) < OwnCancelAnswer) return;
            Interlocked.Exchange(ref _autopilotStoppedAt, now);
        };
    }
}
