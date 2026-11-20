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
        try
        {
            var (line, atFeet) = await StuckAsync(at, feet, toward);
            Note($"{head}; {line}");
            // (beside it, under the same name: "go", "follow", "assistant's walk", "keep in place's walk back")
            if (atFeet != null) Note($"{head[..Math.Max(0, head.IndexOf(':'))]}: {atFeet}");
        }
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
    /// near a border the model's rays meet a neighbour's prims.
    /// AtFeet: where the model has nothing under her, her feet are well over its floor, or it has nothing ahead at any
    /// height (the region had something solid there that the model hasn't, at its place: her log of 2026-11-19 at
    /// 20:08, rising 3.4 m up steps the model lacked while it said it had every shape), a second line naming the parts
    /// whose boxes come within a metre of her feet (AtFeet); every time with the walk log on (a test switch).</summary>
    public async Task<(string Line, string? AtFeet)> StuckAsync(Vector3 at, float feet, Vector3 toward)
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
        // the model's side of what the region had there: nothing under her, her feet well over its floor (the ground
        // counting), or nothing ahead at any height
        float? modelUnder = hits[underfoot] is { } uh ? at.Z - uh.Distance : null;
        float? groundUnder = GroundAt(at.X, at.Y) is { } gu && gu <= at.Z ? gu : null;
        bool blindAhead = Enumerable.Range(0, heights.Length).All(i => hits[i] == null);
        string? lacking = modelUnder is { } mu && mu >= (groundUnder ?? float.NegativeInfinity)
                ? (feet - mu > AtFeetOver ? Inv($"the model's floor {feet - mu:0.00} m under them") : null)
            : groundUnder is { } g0 ? (feet - g0 > AtFeetOver ? Inv($"nothing of the model's under them, the ground {feet - g0:0.00} m under them") : null)
            : "nothing under them in the model, nor the ground";
        lacking ??= blindAhead ? "nothing ahead in the model at any height" : Logging ? "the walk log on" : null;
        var near = lacking != null ? _rays.Near(new Vector3(at.X, at.Y, feet), AtFeetReach) : default;
        var listed = near.Parts?.Take(AtFeetListed + AtFeetNamed).ToList() ?? [];
        await NamesAsync(hits.Where(h => h is { Avatar: false }).Select(h => (h!.Value.Region, h.Value.LocalId))
            .Concat(missing.Take(4).Select(m => (m.Region, m.Prim.LocalID)))
            .Concat(listed.Select(p => (p.Region, p.Prim.LocalID))));

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
        return (string.Join("; ", parts), lacking != null ? AtFeet(new Vector3(at.X, at.Y, feet), lacking, near, listed) : null);
    }

    /// <summary>Whether a walk stuck where the region holds her on something the collision model hasn't got (the blind
    /// tester's walks to her partner, 2026-11-19: carried 3.4 m up steps the model lacked while each stuck stretch was
    /// marked and gone round, till "no way"): her feet at least Lifted over what the model has under her (its floor or
    /// the ground, whichever is higher; the planner too carries her floor on level from her feet then), no further over
    /// it than a fall that lands near (LiftedFall: a skybox over open ground isn't trusted), and nothing of the model's
    /// within LiftedAhead toward where she'd be sent at any height from her shin to her head (people aside: a walk up
    /// against someone ends as before), and no drop the model has on the straight way there: under a point each metre
    /// or so on it, the model's floor or the ground no more than LiftedDrop under what's under her now (the 12 m was
    /// measured only under her). What's under her is the highest the model has under her middle and four points at her
    /// body's edge, as at a deck's edge her middle can be past it while she stands on it. Lifted: the words for the log
    /// (how far over what), or null; Not: lifted, but why the region isn't trusted (for the stuck line).</summary>
    public async Task<(string? Lifted, string? Not)> LiftedAsync(Vector3 at, float feet, Vector3 toward)
    {
        var flat = new Vector2(toward.X - at.X, toward.Y - at.Y);
        float dist = flat.Length();
        var dir = dist > 0.1f ? Vector3.Normalize(new Vector3(flat, 0))
            : Dir(S.Client is { } me ? YawOf(me.Self.Movement.BodyRotation) : 0);
        float tall = Math.Clamp(2 * (at.Z - feet), 1.2f, 3f);
        var rays = new[] { 0.2f, 0.5f, 0.9f, 1.3f, tall - 0.1f }.Select(h => (new Vector3(at.X, at.Y, feet + h), dir, LiftedAhead)).ToList();
        var side = new Vector3(-dir.Y, dir.X, 0);
        foreach (var o in new[] { Vector3.Zero, dir, -dir, side, -side })
            rays.Add((new Vector3(at.X + o.X * LiftedEdge, at.Y + o.Y * LiftedEdge, feet + 0.3f), -Vector3.UnitZ, LiftedFall + 0.3f));
        int n = Math.Min((int)MathF.Ceiling(dist / LiftedStep), LiftedPoints);
        var line = Enumerable.Range(1, n).Select(i => at + dir * (dist * i / n)).ToList();
        foreach (var p in line) rays.Add((new Vector3(p.X, p.Y, feet + 0.3f), -Vector3.UnitZ, LiftedFall + LiftedDrop + 0.3f));
        if (await _rays.CollideManyAsync(rays) is not { } got) return (null, null);
        float? model = null;
        for (int k = 5; k < 10; k++)
            if (got.Hits[k] is { Avatar: false } d && (model is not { } was || feet + 0.3f - d.Distance > was)) model = feet + 0.3f - d.Distance;
        float? ground = GroundAt(at.X, at.Y) is { } g && g <= feet + 0.3f ? g : null;
        if (model == null && ground == null) return (null, null);
        bool byModel = model is { } m && (ground is not { } g2 || m >= g2);
        float under = byModel ? model!.Value : ground!.Value, over = feet - under;
        if (over < Lifted || over > LiftedFall) return (null, null);
        var said = byModel ? Inv($"her feet {over:0.00} m over the model's floor under them ({model:0.00})")
            : Inv($"her feet {over:0.00} m over the ground ({ground:0.00}), nothing of the model's under them");
        if (got.Hits.Take(5).Any(h => h is { Avatar: false })) return (null, $"{said}, not trusting the region: something of the model's ahead");
        for (int i = 0; i < n; i++)
        {
            var p = line[i];
            float? below = got.Hits[10 + i] is { Avatar: false } h ? feet + 0.3f - h.Distance : null;
            if (GroundAt(p.X, p.Y) is { } gp && gp <= feet + 0.3f && (below is not { } b0 || gp > b0)) below = gp;
            if (below is not { } b || b < under - LiftedDrop)
                return (null, Inv($"{said}, not trusting the region: a drop the model has on the way, at {p.X:0.0},{p.Y:0.0}, ") + (below is { } b1 ? Inv($"its floor {b1:0.00}") : "nothing under it within reach"));
        }
        return (said, null);
    }

    /// <summary>LiftedAsync: how far over the model's floor is on something it hasn't got (the planner's own: a floor under
    /// her feet any further down isn't hers); how far down a fall may be; how far ahead the model must have nothing; how
    /// far out her body's edge is; a down ray each LiftedStep on the way there, at most LiftedPoints; how far under what's
    /// under her the floor on the way may be (a drop the planner wouldn't take, and some).</summary>
    const float Lifted = 0.5f, LiftedFall = 12f, LiftedAhead = 1.5f, LiftedEdge = 0.3f, LiftedStep = 1f, LiftedDrop = 1.5f;
    const int LiftedPoints = 16;

    /// <summary>How far over the model's floor her feet may be before a stuck line looks at what's at them; how far from
    /// her feet a part's box may be to be listed; how many are told in full, how many more by name.</summary>
    const float AtFeetOver = 0.3f, AtFeetReach = 1f;
    const int AtFeetListed = 6, AtFeetNamed = 6;

    /// <summary>The parts whose boxes come within a metre of her feet, as the model holds them, for the line beside a
    /// stuck one: each one's name (part of object), kind, the region's physics shape type, how the model counts it and
    /// why not where it doesn't (phantom, flexible, none), the flags that bear on it (phantom, volume detect, temporary,
    /// physical), its shape (ready with how many triangles and which, still to come, given up on) and how near its
    /// triangles come to her feet (a part the model has elsewhere, or empty, against one at her feet), and its box; then
    /// how many of the region's parts the model can't place at all (their object's root not had). What isn't listed
    /// the model hasn't got: the region never sent it, or it was dropped on the way.</summary>
    string AtFeet(Vector3 feet, string lacking, (List<PartNear> Parts, int Unplaced, int UnplacedObjects) near, List<PartNear> listed)
    {
        var all = near.Parts ?? [];
        var head = Inv($"at her feet when stuck ({feet.X:0.00},{feet.Y:0.00},{feet.Z:0.00}; {lacking}): ");
        var said = new List<string>();
        foreach (var p in listed.Take(AtFeetListed)) said.Add(PartSaid(p));
        var body = all.Count == 0 ? Inv($"no part's box within {AtFeetReach:0} m of them")
            : (all.Count == 1 ? Inv($"1 part whose box comes within {AtFeetReach:0} m of them: ") : Inv($"{all.Count} parts whose boxes come within {AtFeetReach:0} m of them: ")) + string.Join("; ", said)
              + (all.Count > AtFeetListed
                  ? $"; and {all.Count - AtFeetListed} more: " + string.Join(", ", listed.Skip(AtFeetListed).Select(p => Name(p.Region, p.Prim.LocalID)))
                    + (all.Count > AtFeetListed + AtFeetNamed ? ", ..." : "")
                  : "");
        var unplaced = near.Unplaced == 0 ? "none of the region's parts the model can't place"
            : Inv($"{near.Unplaced} of the region's parts, of {near.UnplacedObjects} object{(near.UnplacedObjects == 1 ? "" : "s")}, the model can't place (their object's root not had)");
        return $"{head}{body}; {unplaced}";
    }

    /// <summary>One part for the line at her feet.</summary>
    string PartSaid(PartNear p)
    {
        var prim = p.Prim;
        var kind = p.Kind switch
        {
            ShapeKind.Mesh => "mesh", ShapeKind.Sculpt => "sculpt", ShapeKind.Prim => "prim",
            _ => prim.PrimData.PCode switch { PCode.Tree or PCode.NewTree => "a Linden tree", PCode.Grass => "Linden grass", var c => $"not a volume ({c})" },
        };
        var type = _rays.Physics.Of(prim) switch
        {
            PhysicsShapeType.Prim => "prim", PhysicsShapeType.ConvexHull => "convex hull", PhysicsShapeType.None => "none", { } t => t.ToString(), null => "not known yet",
        };
        var flags = S.State.PrimFlagsOf(prim);
        var rootFlags = prim.ParentID != 0 && S.World.Region(S.GridKey, p.Region) is { } w && w.Prims.TryGetValue(prim.ParentID, out var root)
            ? S.State.PrimFlagsOf(root) : PrimFlags.None;
        var marks = new List<string>();
        if ((flags & PrimFlags.Phantom) != 0) marks.Add("phantom");
        else if ((rootFlags & PrimFlags.Phantom) != 0) marks.Add("phantom by its object's root");
        if (((flags | rootFlags) & VolumeDetect) != 0) marks.Add("volume detect");
        if (((flags | rootFlags) & (PrimFlags.TemporaryOnRez | PrimFlags.Temporary)) != 0) marks.Add("temporary");
        if (((flags | rootFlags) & PrimFlags.Physics) != 0) marks.Add("physical");
        if (((byte)prim.PrimData.PathCurve & 0x80) != 0) marks.Add("flexible");
        var counts = p.How switch
        {
            CollideAs.Prim => "counted as prim", CollideAs.ConvexHull => "counted as a convex hull", CollideAs.Unknown => "counted as solid",
            CollideAs.None => "not counted (physics none)", _ => "not counted (phantom or flexible)",
        };
        var shape = p.Shape switch
        {
            ShapeState.None => "no shape: never counted",
            ShapeState.ToCome => "its shape still to come",
            ShapeState.GivenUp => "its shape given up on",
            _ when p.Triangles == 0 => "its shape ready but holding no triangles",
            _ => Inv($"{p.Triangles} triangles by {p.By}, the nearest {p.Nearest:0.00} m from her feet"),
        };
        // (a mesh's flat hulls have no triangles here; the region may count them as thin plates)
        if (p.Hulls is { } h && (h.Hulls > 0 || h.FlatBase))
            shape += (h.Hulls > 0 ? Inv($", {h.Hulls} hulls in its decomposition, {h.Flat} of them flat and left out") : "")
                     + (h.FlatBase ? ", its own hull flat and left out" : "");
        var box = p.BoxOff < 0.005f ? "her feet in its box" : Inv($"its box {p.BoxOff:0.00} m off");
        return Inv($"{Name(p.Region, prim.LocalID)} ({kind}; the region's physics {type}, {counts}")
               + (marks.Count > 0 ? $"; {string.Join(", ", marks)}" : "")
               + Inv($"; {shape}; {box}: middle {p.Middle.X:0.00},{p.Middle.Y:0.00},{p.Middle.Z:0.00}, size {p.Size.X:0.00}x{p.Size.Y:0.00}x{p.Size.Z:0.00}, ")
               + Turned(p.Rot) + Inv($", from {p.Low:0.00} to {p.High:0.00} high)");
    }

    /// <summary>Second Life's volume detect flag (FLAGS_VOLUME_DETECT, 1 &lt;&lt; 14; LibreMetaverse names that bit for a
    /// joint).</summary>
    const PrimFlags VolumeDetect = (PrimFlags)0x4000;

    /// <summary>A part's turn as the build floater gives it (degrees about X, Y, Z), or "not turned".</summary>
    static string Turned(System.Numerics.Quaternion q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float rx = MathF.Atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y));
        float ry = MathF.Asin(Math.Clamp(2 * (w * y - z * x), -1f, 1f));
        float rz = MathF.Atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z));
        static float D(float r) { float d = MathF.Round(r * 180 / MathF.PI); return d == 0 ? 0 : d; }
        float dx = D(rx), dy = D(ry), dz = D(rz);
        return dx == 0 && dy == 0 && dz == 0 ? "not turned" : Inv($"turned {dx:0},{dy:0},{dz:0}");
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
