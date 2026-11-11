using System.Numerics;
using Sojourn.Core.Agents;
using Sojourn.Session.Collision;

namespace Sojourn.Access.Walk;

/// <summary>
/// Paths around what's in the way (M3), for `//go`, walk to, follow and the assistant's walks: the local collision
/// model's solid triangles (the renderer out of sight: prims by their physics shapes, only what's been drawn) laid on
/// a grid of 0.25 m cells over the walk's area plus a margin, what stands between knee and head height as obstacles;
/// nearer than the body's half width to them is blocked, and A* across it (eight ways) pays more near walls so it keeps
/// to the middle of gaps; then pulled straight wherever a line keeps that room, leaving the corners as waypoints.
/// The floor under each cell is followed out from where the avatar stands (2026-10-31, after a blind tester's walk in
/// a jazz club found no way): from a cell to its neighbour, the highest surface no more than a step up (Step) or a
/// short drop down (Drop) from its floor, the ground among them, so stairs, a stage's steps, a ramp and a building's
/// floor over sloping ground are walked, while a surface out of reach (a stage with no steps, a table) is an obstacle;
/// what the floor can't reach is blocked. A goal inside something, or out of reach, moves to the nearest spot that can
/// be reached within GoalReach. Written by Claude Opus 5.5, 2026-10-29.
/// People on their feet, when the caller gives them (a `//go`, walk to), are gone round too (Claude Fable 5.2,
/// 2026-11-09): each a disc the path keeps off as it does a wall. Unlike a wall, a person never makes for no way:
/// where the only way is through someone (a doorway they stand in, a ring of people round the goal), or the way round
/// is much longer (ThroughPerson), the path stops short of them and says who (Plan.InWay).
/// Floors over one another (Claude Opus 5.5, 2026-11-11): the walk is planned over nodes, a floor over a cell, so a
/// cell can hold the ground floor and an upstairs over it, a balcony and the path under it, a bridge and the road
/// below (Floors). The floor followed across from a node to the next cell joins the node already there at that height
/// or starts one, and a step goes only where the floor goes; walls, room kept, people and stuck spots count per node,
/// the goal's floor is the one under its height, and a line pulled straight follows the floor as the walk will, so it
/// stays on the floors the path took and its corners lead up the stairs in order. With one floor a cell, as until then,
/// an upstairs over where the walk began was taken for the ground floor under it, and the stairs led nowhere.
/// </summary>
public sealed class PathPlanner
{
    readonly AgentSession _s;
    readonly LocalCollision _rays;

    public const float Cell = 0.25f;
    const float Margin = 12f, MaxSpan = 200f;
    const float Knee = 0.45f, Head = 1.8f;
    /// <summary>The body's half width: nearer than this to anything solid is blocked (a 0.8 m doorway still passes).</summary>
    const float Body = 0.3f;
    /// <summary>The room a path keeps from walls where there's that much (a corner closer than this was bumped).</summary>
    const float Room = 0.55f;
    /// <summary>Two floors over a cell nearer than this are one (what a body stands on is the same within a knee's height;
    /// a floor over another with room for the body between is always further).</summary>
    const float Same = Knee;

    public PathPlanner(AgentSession s, LocalCollision rays)
    {
        _s = s;
        _rays = rays;
    }

    /// <summary>A plan: the waypoints after the start (the last is the goal, or the nearest free spot to it), whether the
    /// straight line was clear anyway, its length.</summary>
    public sealed record Plan(List<Vector2> Waypoints, bool Direct, float Length, bool GoalMoved)
    {
        /// <summary>The floor's height at the start and at the last waypoint; FloorKnown: the model has what the start
        /// stands on (else the floor was carried on level from the feet given, and both heights are only that).</summary>
        public float StartFloor { get; init; }
        public float GoalFloor { get; init; }
        public bool FloorKnown { get; init; }
        /// <summary>The people (indices into those given) the way goes round, when they alone keep it from being
        /// straight; empty when things are in the way too, or nobody is.</summary>
        public IReadOnlyList<int> Around { get; init; } = Array.Empty<int>();
        /// <summary>The person the way is cut short at (its last waypoint StandOff from them; no waypoints when that's
        /// where the walk starts): the only way on is through them, or the way round is much longer.</summary>
        public int? InWay { get; init; }
        /// <summary>The way ends on the goal's own floor: the one under its height, or with none under it within a
        /// body's height (someone hovering, a floor the model hasn't got), one it could be standing on. False: that
        /// floor is out of reach from here, and the way ends as near as it goes on another (meaningful where FloorKnown).</summary>
        public bool OnGoalFloor { get; init; } = true;
    }

    /// <summary>Whether a plan can be asked for now (the view is up): a null plan then means there's no way.</summary>
    public bool CanAsk => _rays.Ready;

    /// <summary>Spots to treat as walls (where a walk got stuck), region frame, until they expire; with a height (the
    /// avatar's middle where it stuck), only on the floor it was walking.</summary>
    readonly List<(Vector2 At, float R, float? Z, long Until)> _blocked = new();

    public void Block(Vector2 at, float radius, TimeSpan forHowLong, float? z = null)
    {
        lock (_blocked) _blocked.Add((at, radius, z, Environment.TickCount64 + (long)forHowLong.TotalMilliseconds));
    }

    /// <summary>A path from <paramref name="from"/> (feet at <paramref name="feet"/>) to <paramref name="to"/>, both in the
    /// agent's region frame; null when there's no way (or the view can't be asked). <paramref name="toZ"/>: the goal's
    /// height (a person's middle, a point given, for a thing where a body at its foot would have its middle), which picks
    /// the floor it's on where floors lie over one another: the floor under it within a body's height, else the highest
    /// at or under it, else the lowest (none: the floor nearest the start's); and tells a goal in a closed room of its own
    /// (no way) from one up on a stage beside this floor (the nearest spot below it). <paramref name="people"/>: where
    /// people stand (their middles), for the way to go round them. <paramref name="thing"/>: the goal is a thing's, whose
    /// inside, closed all round as a room with no door is, is never a space of its own: the nearest spot outside it.</summary>
    public async Task<Plan?> PlanAsync(Vector2 from, float feet, Vector2 to, IReadOnlySet<uint>? passable = null, CancellationToken ct = default,
        float? toZ = null, IReadOnlyList<Vector3>? people = null, bool thing = false)
    {
        if (!_rays.Ready) return null;
        var lo = Vector2.Min(from, to) - new Vector2(Margin);
        var hi = Vector2.Max(from, to) + new Vector2(Margin);
        if (hi.X - lo.X > MaxSpan || hi.Y - lo.Y > MaxSpan)
        {
            // too far to plan in one go: the first stretch toward it
            var dir = Vector2.Normalize(to - from);
            return await PlanAsync(from, feet, from + dir * (MaxSpan / 2 - Margin), passable, ct, null, people);
        }
        var w = _s.Region;
        float? Ground(float x, float y) => w?.TerrainHeight(x, y);
        // near the ground the floor may follow it up and down hills; in a skybox, only a few storeys either way
        // (the ground not known yet there counts as near: Solve takes the floor as level where it isn't known)
        bool nearGround = Ground(from.X, from.Y) is not { } g0 || feet - g0 < 10f;
        float zMin = feet - 3, zMax = feet + 12;
        if (nearGround)
            for (float y = lo.Y; y <= hi.Y + 4; y += 4)
                for (float x = lo.X; x <= hi.X + 4; x += 4)
                    if (Ground(Math.Min(x, hi.X), Math.Min(y, hi.Y)) is { } g) { zMin = MathF.Min(zMin, g - 2); zMax = MathF.Max(zMax, g + 12); }
        var got = await _rays.SolidsAsync(new Vector3(lo, zMin), new Vector3(hi, zMax), passable, ct);
        if (got is not { } solids) return null;
        return await Task.Run(() => Solve(solids.Triangles, from, feet, to, toZ, lo, hi, nearGround ? Ground : null, people, thing), ct);
    }

    /// <summary>A step up the floor may take from one cell to the next (stairs, a kerb, a stage's steps), and how far
    /// down it may drop (off a step, a low stage); further down is an edge.</summary>
    const float Step = 0.4f, Drop = 1.2f;
    /// <summary>How far from a goal out of reach (inside a bar, on a stage with no steps) its stand-in may be.</summary>
    public const float GoalReach = 6f;
    /// <summary>A person as the path sees them: a disc this wide round their middle, so with the body's own half width
    /// the path's line stays 0.75 m from it (two avatars touch at about 0.6 to 0.75), and a metre where there's room.</summary>
    public const float PersonR = 0.45f;
    /// <summary>How far short of someone in the way a path cut at them ends (as a walk to a person stops).</summary>
    public const float StandOff = 1.5f;
    /// <summary>What going through someone costs the search, in cells: paid once, on stepping from clear of people to
    /// too near one (so once for a person, or for a knot of people standing together, however the way would cross
    /// them). With what being that near anything costs, a way round is walked up to 10 to 12 m longer than the way
    /// through, and past that the path ends short of them (asked directly, a wall across the way: someone in the
    /// middle of its metre-wide doorway, round the wall's end is walked at 11.3 m further and not at 12.2; someone
    /// to one side of a doorway 1.5 m wide, whom the way would only brush, at 10.4 and not at 11.3). Until the
    /// evening of 2026-11-09 it was 30 for each cell too near someone: some 45 m for a way through a person's
    /// middle, a long way to send her round for one person who'll likely have moved on, and 8 m for a way that
    /// brushed a single cell of theirs.</summary>
    const float ThroughPerson = 38f;

    /// <summary>Whether a body standing on a floor at <paramref name="floor"/> has its middle at <paramref name="z"/>
    /// (give or take: someone on that floor, not on one over or under it).</summary>
    static bool StandsOn(float z, float floor) => z >= floor - 0.3f && z <= floor + 2.6f;

    Plan? Solve(List<Vector3> tris, Vector2 from, float feet, Vector2 to, float? toZ, Vector2 lo, Vector2 hi, Func<float, float, float?>? ground,
        IReadOnlyList<Vector3>? people = null, bool thing = false)
    {
        int nx = (int)MathF.Ceiling((hi.X - lo.X) / Cell), ny = (int)MathF.Ceiling((hi.Y - lo.Y) / Cell);
        int cells = nx * ny;
        // what stands over each cell, a list per cell: each triangle's heights over it, whether it's flat enough to
        // stand on (its top a floor the walk may step onto), and whether it faces up (a floor's top, not a ceiling)
        var head = new int[cells];
        Array.Fill(head, -1);
        var links = new List<int>();
        var los = new List<float>();
        var his = new List<float>();
        var flats = new List<bool>();
        var ups = new List<bool>();
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            Vector3 a = tris[t], b = tris[t + 1], c = tris[t + 2];
            var n = Vector3.Cross(b - a, c - a);
            float len = n.Length();
            if (len < 1e-9f) continue;
            float up = MathF.Abs(n.Z / len);
            float tzLo = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), tzHi = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
            // where it lies across the grid: nudged 2 mm back from the way it faces and over a cell only if it reaches
            // half a millimetre into it, so a wall's side lying on a cell's edge counts in the cell the wall stands in
            // and not the free one beside it (2026-11-11: builds and walks on round numbers put a stairwell's walls on
            // cell edges, and a stair a metre wide between them came out with no cell a body fits in)
            var back = new Vector2(n.X, n.Y) * (-0.002f / len);
            Vector2 a2 = new Vector2(a.X, a.Y) + back, b2 = new Vector2(b.X, b.Y) + back, c2 = new Vector2(c.X, c.Y) + back;
            int i0 = Math.Max(0, (int)((MathF.Min(a2.X, MathF.Min(b2.X, c2.X)) - lo.X) / Cell)), i1 = Math.Min(nx - 1, (int)((MathF.Max(a2.X, MathF.Max(b2.X, c2.X)) - lo.X) / Cell));
            int j0 = Math.Max(0, (int)((MathF.Min(a2.Y, MathF.Min(b2.Y, c2.Y)) - lo.Y) / Cell)), j1 = Math.Min(ny - 1, (int)((MathF.Max(a2.Y, MathF.Max(b2.Y, c2.Y)) - lo.Y) / Cell));
            if (i0 > i1 || j0 > j1) continue;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var c0 = new Vector2(lo.X + i * Cell, lo.Y + j * Cell);
                    if (!Overlaps(a2, b2, c2, c0 + new Vector2(0.0005f), c0 + new Vector2(Cell - 0.0005f))) continue;
                    // its heights over this cell: its plane at the cell's corners, within the triangle's own (a long
                    // sloping roof or ramp spans metres, but little over one cell); an upright one, all of it
                    float zl = tzLo, zh = tzHi;
                    if (up > 0.05f)
                    {
                        float Z(float x, float y) => a.Z - (n.X * (x - a.X) + n.Y * (y - a.Y)) / n.Z;
                        float z00 = Z(c0.X, c0.Y), z10 = Z(c0.X + Cell, c0.Y), z01 = Z(c0.X, c0.Y + Cell), z11 = Z(c0.X + Cell, c0.Y + Cell);
                        zl = Math.Clamp(MathF.Min(MathF.Min(z00, z10), MathF.Min(z01, z11)), tzLo, tzHi);
                        zh = Math.Clamp(MathF.Max(MathF.Max(z00, z10), MathF.Max(z01, z11)), tzLo, tzHi);
                    }
                    int k = j * nx + i;
                    links.Add(head[k]);
                    head[k] = los.Count;
                    los.Add(zl);
                    his.Add(zh);
                    flats.Add(up >= 0.7f);
                    ups.Add(n.Z > 0);
                }
        }
        // (as arrays: every step of the floor-following reads them)
        var link = links.ToArray();
        var spanLo = los.ToArray();
        var spanHi = his.ToArray();
        var flat = flats.ToArray();
        var faceUp = ups.ToArray();
        // the ground's height under each cell, asked once (+infinity: not known there)
        var groundZ = new float[ground != null ? cells : 0];
        Array.Fill(groundZ, float.NaN);
        float? GroundAt(int k)
        {
            if (ground == null) return null;
            float v = groundZ[k];
            if (float.IsNaN(v)) groundZ[k] = v = ground(lo.X + (k % nx + 0.5f) * Cell, lo.Y + (k / nx + 0.5f) * Cell) ?? float.PositiveInfinity;
            return float.IsPositiveInfinity(v) ? null : v;
        }
        // a cell's floor coming from a neighbour's at f: the highest surface (or the ground) within a step up and a drop
        // down; none: out of reach from there. Level: nothing known under the avatar (flying, hovering, on what the model
        // hasn't got), so the floor goes on at its height wherever nothing else is in reach, as a walk in the air
        bool level = false;
        float? FloorFrom(int k, float f)
        {
            float best = level ? f : float.NegativeInfinity;
            // near the ground: where its height isn't known yet (a patch not sent), the floor goes on level
            if (ground != null) { var g = GroundAt(k) ?? f; if (g <= f + Step && g >= f - Drop) best = g; }
            for (int e = head[k]; e >= 0; e = link[e])
                if (flat[e] && spanHi[e] <= f + Step && spanHi[e] >= f - Drop && spanHi[e] > best) best = spanHi[e];
            return best > float.NegativeInfinity ? best : null;
        }
        // something in the body's height band over a floor at f: a wall crossing it, a table top within it
        bool Obstructed(int k, float f)
        {
            float sLo = f + Knee, sHi = f + Head;
            for (int e = head[k]; e >= 0; e = link[e])
                if (spanHi[e] >= sLo && spanLo[e] <= sHi) return true;
            return false;
        }
        // the floor under a point at height z, whether or not the walk reaches it: the highest surface facing up (or the
        // ground) a little above it to 2.5 m below (a person's middle, a thing on it, a point on the ground). A ceiling's
        // underside is no floor: a light hung just under it is in the room below, not on the floor upstairs
        float? SurfaceUnder(int k, float z)
        {
            float gf = float.NegativeInfinity;
            if (GroundAt(k) is { } gg && gg <= z + 0.2f && gg >= z - 2.5f) gf = gg;
            for (int e = head[k]; e >= 0; e = link[e])
                if (flat[e] && faceUp[e] && spanHi[e] <= z + 0.2f && spanHi[e] >= z - 2.5f && spanHi[e] > gf) gf = spanHi[e];
            return gf > float.NegativeInfinity ? gf : null;
        }

        (int, int) CellOf(Vector2 p) => (Math.Clamp((int)((p.X - lo.X) / Cell), 0, nx - 1), Math.Clamp((int)((p.Y - lo.Y) / Cell), 0, ny - 1));
        Vector2 Centre(int i, int j) => new(lo.X + (i + 0.5f) * Cell, lo.Y + (j + 0.5f) * Cell);
        var (si, sj) = CellOf(from);
        var (gi, gj) = CellOf(to);

        // the floor followed out from where the avatar stands (four ways), a node for each floor reached over a cell: one
        // that can't be reached is as good as solid, and so is one with something in the way over its floor (the floor
        // goes no further from there)
        int count = 0, cap = cells + cells / 8 + 16;
        var nodeCell = new int[cap];
        var nodeFloor = new float[cap];
        var nodeNext = new int[cap * 4];
        var nodeSolid = new bool[cap];
        var firstAt = new int[cells];
        var sameCell = new int[cap];   // the cell's node made before this one (each cell's list, newest first)
        Array.Fill(firstAt, -1);
        int Add(int k, float f)
        {
            if (count == nodeCell.Length)
            {
                int more = count + count / 2;
                Array.Resize(ref nodeCell, more);
                Array.Resize(ref nodeFloor, more);
                Array.Resize(ref nodeNext, more * 4);
                Array.Resize(ref nodeSolid, more);
                Array.Resize(ref sameCell, more);
            }
            nodeCell[count] = k;
            nodeFloor[count] = f;
            sameCell[count] = firstAt[k];
            firstAt[k] = count;
            nodeNext.AsSpan(count * 4, 4).Fill(Floors.None);
            return count++;
        }
        var queue = new Queue<int>();
        int sc = sj * nx + si;
        // (the feet as walk mode reckons them can be a little off either way: the floor under them within reach of it)
        int start;
        if (FloorFrom(sc, feet + 0.4f) is { } f0 && f0 >= feet - 0.5f) start = Add(sc, f0);
        else { start = Add(sc, feet); level = true; }
        queue.Enqueue(start);
        while (queue.TryDequeue(out int n))
        {
            int k = nodeCell[n];
            float f = nodeFloor[n];
            if (n != start && Obstructed(k, f)) { nodeSolid[n] = true; continue; }
            int i = k % nx, j = k / nx;
            for (int d = 0; d < 4; d++)
            {
                int ii = i + (d == 0 ? 1 : d == 1 ? -1 : 0), jj = j + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (ii < 0 || jj < 0 || ii >= nx || jj >= ny) { nodeNext[n * 4 + d] = Floors.OffGrid; continue; }
                int kk = jj * nx + ii;
                if (FloorFrom(kk, f) is not { } fk) continue;
                int m = firstAt[kk];
                while (m >= 0 && MathF.Abs(nodeFloor[m] - fk) >= Same) m = sameCell[m];
                if (m < 0) { m = Add(kk, fk); queue.Enqueue(m); }
                nodeNext[n * 4 + d] = m;
            }
        }
        var fl = new Floors(nx, ny, count, nodeCell, nodeFloor, nodeNext, out var renumbered);
        int nodes = fl.Count;
        var floor = fl.FloorOf;
        var solid = new bool[nodes];
        for (int n = 0; n < count; n++) solid[renumbered[n]] = nodeSolid[n];
        start = renumbered[start];
        float startFloor = floor[start];
        long now = Environment.TickCount64;
        lock (_blocked)
        {
            _blocked.RemoveAll(x => x.Until < now);
            foreach (var (spot, r, z, _) in _blocked)
            {
                int ci = (int)((spot.X - lo.X) / Cell), cj = (int)((spot.Y - lo.Y) / Cell), cr = (int)MathF.Ceiling(r / Cell);
                for (int j = cj - cr; j <= cj + cr; j++)
                    for (int i = ci - cr; i <= ci + cr; i++)
                        if (i >= 0 && j >= 0 && i < nx && j < ny && (i - ci) * (i - ci) + (j - cj) * (j - cj) <= cr * cr)
                            for (int m = fl.First[j * nx + i]; m < fl.First[j * nx + i + 1]; m++)
                                if (z is not { } zz || StandsOn(zz, floor[m])) solid[m] = true;
            }
        }

        // people standing on a floor (their middle within a body's height of it): that floor's nodes under each
        bool[]? stood = null;
        if (people != null)
            for (int p = 0; p < people.Count; p++)
            {
                var stands = people[p];
                int ci = (int)MathF.Floor((stands.X - lo.X) / Cell), cj = (int)MathF.Floor((stands.Y - lo.Y) / Cell), cr = (int)MathF.Ceiling(PersonR / Cell);
                for (int j = cj - cr; j <= cj + cr; j++)
                    for (int i = ci - cr; i <= ci + cr; i++)
                    {
                        if (i < 0 || j < 0 || i >= nx || j >= ny || Vector2.Distance(Centre(i, j), new Vector2(stands.X, stands.Y)) > PersonR) continue;
                        for (int m = fl.First[j * nx + i]; m < fl.First[j * nx + i + 1]; m++)
                            if (!solid[m] && StandsOn(stands.Z, floor[m])) (stood ??= new bool[nodes])[m] = true;
                    }
            }
        bool StoodAt(Vector3 at)
        {
            if (stood == null) return false;
            var (i, j) = CellOf(new Vector2(at.X, at.Y));
            for (int m = fl.First[j * nx + i]; m < fl.First[j * nx + i + 1]; m++)
                if (stood[m] && StandsOn(at.Z, floor[m])) return true;
            return false;
        }

        // how far each node is from anything solid on its floor; too close for the avatar's body is blocked, and the path
        // keeps to the middle of what it passes through where there's room (a corner shaved too close is bumped). People
        // count for the room kept (clear) and for where the path goes (blocked), but not for what can be got to at all
        // (hard: things only), so someone standing in the only doorway doesn't make the room behind them a closed one
        var clearOfThings = fl.Clearance(solid);
        var clear = clearOfThings;
        if (stood != null)
        {
            var both = (bool[])solid.Clone();
            for (int m = 0; m < nodes; m++) if (stood[m]) both[m] = true;
            clear = fl.Clearance(both);
        }
        var hard = new bool[nodes];
        var blocked = new bool[nodes];
        for (int m = 0; m < nodes; m++) { hard[m] = clearOfThings[m] < Body; blocked[m] = clear[m] < Body; }
        // standing against a wall: the floor around the start is free (else nothing could leave it). Standing right by
        // someone, those cells that take her no nearer them than she is: she leaves away from them, not brushing past
        bool TowardSomeone(Vector2 cell)
        {
            foreach (var p in people!)
            {
                var them = new Vector2(p.X, p.Y);
                float gap = Vector2.Distance(cell, them);
                if (gap < PersonR + Body + Cell && gap < Vector2.Distance(from, them) - 0.01f && StoodAt(p)) return true;
            }
            return false;
        }
        int near = (int)MathF.Ceiling(Body / Cell) + 1;
        for (int dj = -near; dj <= near; dj++)
            for (int di = -near; di <= near; di++)
            {
                int i = si + di, j = sj + dj;
                if (i < 0 || j < 0 || i >= nx || j >= ny) continue;
                for (int m = fl.First[j * nx + i]; m < fl.First[j * nx + i + 1]; m++)
                {
                    if (solid[m] || MathF.Abs(floor[m] - startFloor) > Drop) continue;
                    hard[m] = false;
                    blocked[m] = stood != null && clear[m] < Body && TowardSomeone(Centre(i, j));
                }
            }
        hard[start] = blocked[start] = false;
        // (too near someone, as against too near a thing: what the search pays ThroughPerson for. A spot against a thing
        // is entered only off an edge, landing beside a stage: someone standing there nearer than the stage's side is
        // stepped in among too)
        bool[]? byPeople = null;
        if (stood != null)
        {
            byPeople = new bool[nodes];
            for (int m = 0; m < nodes; m++) byPeople[m] = blocked[m] && (!hard[m] || clear[m] < clearOfThings[m]);
        }
        // where a body can get to from here (as A* moves; four ways reach what its eight do, since a diagonal is only
        // taken past two corners it could step into)
        var open = new bool[nodes];
        open[start] = true;
        queue.Enqueue(start);
        while (queue.TryDequeue(out int n))
            for (int d = 0; d < 4; d++)
            {
                int m = fl.Next[n * 4 + d];
                if (m < 0 || open[m] || !fl.Enters(n, m, false, hard, solid)) continue;
                open[m] = true;
                queue.Enqueue(m);
            }

        int CellNode(int m) => fl.CellOf[m];
        // the goal's floor: where it has a height, the floor under that (the goal upstairs or on the ground floor under
        // it), and none there if the walk doesn't reach that floor over this cell. With nothing under the height within a
        // body's (a thing's middle high in a tower, a light hung from a ceiling, someone hovering), the highest floor
        // reached there at or under it, else the lowest; with no height, the floor nearest the start's
        int gCell = gj * nx + gi;
        float? under = toZ is { } tz0 ? SurfaceUnder(gCell, tz0) : null;
        int goal = -1;
        if (under is { } u)
        {
            for (int m = fl.First[gCell]; m < fl.First[gCell + 1]; m++)
                if (goal < 0 || MathF.Abs(floor[m] - u) < MathF.Abs(floor[goal] - u)) goal = m;
            if (goal >= 0 && !level && MathF.Abs(floor[goal] - u) >= Same) goal = -1;
        }
        else if (toZ is { } tz1)
        {
            for (int m = fl.First[gCell]; m < fl.First[gCell + 1]; m++)
                if (floor[m] <= tz1 + 0.2f && (goal < 0 || floor[m] > floor[goal])) goal = m;
            if (goal < 0)
                for (int m = fl.First[gCell]; m < fl.First[gCell + 1]; m++)
                    if (goal < 0 || floor[m] < floor[goal]) goal = m;
        }
        else
            for (int m = fl.First[gCell]; m < fl.First[gCell + 1]; m++)
                if (goal < 0 || MathF.Abs(floor[m] - startFloor) < MathF.Abs(floor[goal] - startFloor)) goal = m;
        // the floor a stand-in is sought on, if one is needed: the goal's own; none known (a height with nothing under it
        // over a cell the floor never reached), any
        float? want = under ?? (goal >= 0 ? floor[goal] : toZ == null ? startFloor : null);

        // the goal out of reach in a space of its own (a room behind a closed door, a pen, a room upstairs whose door is
        // shut): no way. A space that meets this one at an edge (a stage with no steps, a balcony) or the inside of a
        // thing (a bar, a chair) isn't one
        bool Enclosed(int g, float z)
        {
            if (SurfaceUnder(g, z) is not { } gf || Obstructed(g, gf)) return false;
            var seen = new Dictionary<int, float> { [g] = gf };
            var q = new Queue<int>();
            q.Enqueue(g);
            int r2 = (int)(4 / Cell) * (int)(4 / Cell), gi0 = g % nx, gj0 = g / nx;
            while (q.TryDequeue(out int k))
            {
                int i = k % nx, j = k / nx;
                for (int d = 0; d < 4; d++)
                {
                    int ii = i + (d == 0 ? 1 : d == 1 ? -1 : 0), jj = j + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (ii < 0 || jj < 0 || ii >= nx || jj >= ny || (ii - gi0) * (ii - gi0) + (jj - gj0) * (jj - gj0) > r2) continue;
                    int kk = jj * nx + ii;
                    if (seen.ContainsKey(kk)) continue;
                    float f = seen[k];
                    float best = float.NegativeInfinity;
                    if (GroundAt(kk) is { } g2 && g2 <= f + Step) best = g2;
                    for (int e = head[kk]; e >= 0; e = link[e])
                        if (flat[e] && spanHi[e] <= f + Step && spanHi[e] > best) best = spanHi[e];
                    // stepping across or down into this space: they meet at an edge (a floor the walk reaches here, and not
                    // one under this space's own, as the ground floor is under a room upstairs)
                    for (int m = fl.First[kk]; m < fl.First[kk + 1]; m++)
                        if (!solid[m] && floor[m] <= f + Step && floor[m] >= best - Same) return false;
                    if (best == float.NegativeInfinity || f - best > 10 || Obstructed(kk, best)) continue;
                    seen[kk] = best;
                    q.Enqueue(kk);
                }
            }
            return seen.Count >= 16;   // (smaller: the inside of something, not a place to stand)
        }
        IEnumerable<string> Spans(int k)
        {
            for (int e = head[k]; e >= 0; e = link[e]) yield return $"{(flat[e] ? "flat" : "upright")} {spanLo[e]:0.00}-{spanHi[e]:0.00}";
        }
        IEnumerable<string> Nodes(int k)
        {
            for (int m = fl.First[k]; m < fl.First[k + 1]; m++)
                yield return $"{floor[m]:0.00}{(solid[m] ? " in the way" : open[m] ? "" : " not got to")}";
        }
        // (why there's no way, for the walk log)
        string Why(string what) =>
            $"{what}: feet {feet:0.00}, start floor {startFloor:0.00} (ground {GroundAt(sc)?.ToString("0.00") ?? "unknown"}, surfaces "
            + $"{string.Join(" ", Spans(sc))}), {nodes} floor nodes over {fl.Cells} of {cells} cells, {open.Count(x => x)} open; the goal cell "
            + (fl.First[gCell] < fl.First[gCell + 1] ? $"reached at {string.Join(", ", Nodes(gCell))}" : "unreached")
            + (under is { } uu ? $", the floor under the goal {uu:0.00}" : "");
        bool goalMoved = false;
        if (goal < 0 || !open[goal])
        {
            if (toZ is { } tz && !thing && Enclosed(gCell, tz)) { WalkMode.Log(Why("the goal is in a space of its own")); return null; }
            // the goal inside something (a bar, a chair, a block) or out of reach where this space meets it (up on a
            // stage with no steps, a balcony): the nearest spot that can be got to, within GoalReach, a floor more than
            // 1.5 m over or under the goal's counting that height as distance too. Spots about as near as the nearest
            // (within half a metre) are one: the one nearest her of those, so a //go to a block or a bar ends on her side
            // of it, not round the back for the grid's quarter-metre
            var cand = new List<(int M, float D, float Flat)>();
            float bestD = float.MaxValue, bestFlat = 0;
            bool anyFree = false;
            int r = (int)(GoalReach / Cell);
            for (int dj = -r; dj <= r; dj++)
                for (int di = -r; di <= r; di++)
                {
                    int ii = gi + di, jj = gj + dj;
                    if (ii < 0 || jj < 0 || ii >= nx || jj >= ny) continue;
                    for (int m = fl.First[jj * nx + ii]; m < fl.First[jj * nx + ii + 1]; m++)
                    {
                        if (!open[m]) continue;
                        // (a spot with room round it before one against the edge, where the region's walk rides up it;
                        // and one nobody stands at before any where someone does)
                        float rise = want is { } wf ? MathF.Abs(floor[m] - wf) : 0, dz = level || rise <= 1.5f ? 0 : rise / Cell;
                        float d = di * di + dj * dj + dz * dz + (clear[m] < Room ? 16 : 0);
                        if (d > r * r) continue;
                        cand.Add((m, MathF.Sqrt(d), MathF.Sqrt(di * di + dj * dj)));
                        anyFree |= !blocked[m];
                    }
                }
            foreach (var (m, d, flatD) in cand)
                if (blocked[m] != anyFree && d < bestD) (bestD, bestFlat) = (d, flatD);
            int best = -1;
            float bestFrom = float.MaxValue;
            const float Tie = 0.5f / Cell;
            foreach (var (m, d, flatD) in cand)
            {
                if (blocked[m] == anyFree || d > bestD + Tie || flatD > bestFlat + Tie) continue;
                float apart = Vector2.DistanceSquared(Centre(CellNode(m) % nx, CellNode(m) / nx), from);
                if (apart < bestFrom) { bestFrom = apart; best = m; }
            }
            if (best < 0) { WalkMode.Log(Why($"nothing open within {GoalReach} m of the goal")); return null; }
            goal = best;
            (gi, gj) = (CellNode(best) % nx, CellNode(best) / nx);
            goalMoved = true;
        }
        var goalAt = goalMoved ? Centre(gi, gj) : to;
        int? inWay = null;
        IReadOnlyList<int> around = Array.Empty<int>();
        Plan Made(List<Vector2> pts, bool direct, float length) =>
            new(pts, direct, length, goalMoved)
            {
                StartFloor = startFloor, GoalFloor = floor[goal], FloorKnown = !level, Around = around, InWay = inWay,
                OnGoalFloor = level || (under is { } uf ? MathF.Abs(floor[goal] - uf) < Same : toZ is not { } tzf || StandsOn(tzf, floor[goal])),
            };
        bool Roomy(int m, float need) => !blocked[m] && (clear[m] >= need || NearEnd(m));
        bool NearEnd(int m)
        {
            int i = CellNode(m) % nx, j = CellNode(m) / nx;
            return Math.Abs(i - si) <= near && Math.Abs(j - sj) <= near || Math.Abs(i - gi) <= near && Math.Abs(j - gj) <= near;
        }
        // (off an edge, onto the floor beside what it stepped off: as Enters, but not in among people)
        bool Lands(int m) => !solid[m] && byPeople?[m] != true;
        if (fl.Clear(start, goal, m => Roomy(m, Room), Lands)) return Made(new List<Vector2> { goalAt }, true, Vector2.Distance(from, goalAt));

        // straight but for people: they're what the way goes round (for the caller's words)
        bool peopleOnly = stood != null && fl.Clear(start, goal, m => !hard[m] && (clearOfThings[m] >= Room || NearEnd(m)), m => !solid[m]);

        // (so many cells searched with no end to it, people among them: the way as if nobody were there, cut at the first)
        var path = fl.AStar(hard, solid, byPeople, clear, start, goal)
            ?? (stood != null ? fl.AStar(hard, solid, null, clearOfThings, start, goal) : null);
        if (path == null) return null;
        Vector2 At(int m) => Centre(CellNode(m) % nx, CellNode(m) / nx);
        // through someone: the only way, or the way round is much longer. Cut StandOff short of them
        int first = byPeople != null ? path.FindIndex(1, m => byPeople[m]) : -1;
        if (first > 0 && people != null)
        {
            var hit = At(path[first]);
            // (whose cells these are: the nearest person, one standing on this floor before one over or under it)
            float Far(int p) => Vector2.DistanceSquared(new(people[p].X, people[p].Y), hit) + (StoodAt(people[p]) ? 0 : 100);
            int who = 0;
            for (int p = 1; p < people.Count; p++) if (Far(p) < Far(who)) who = p;
            var them = new Vector2(people[who].X, people[who].Y);
            int end = first - 1;
            while (end > 0 && Vector2.Distance(At(path[end]), them) < StandOff) end--;
            inWay = who;
            goalMoved = false;   // (not the goal's stand-in any more: where the way was cut)
            goal = path[end];
            (gi, gj) = (CellNode(goal) % nx, CellNode(goal) / nx);
            goalAt = At(goal);
            if (end == 0) return Made(new List<Vector2>(), false, 0);
            path.RemoveRange(end + 1, path.Count - end - 1);
            if (fl.Clear(start, goal, m => Roomy(m, Room), Lands)) return Made(new List<Vector2> { goalAt }, true, Vector2.Distance(from, goalAt));
        }
        else if (peopleOnly && people != null)
        {
            // whoever stands too near the straight line for it to pass
            var line = to - from;
            var list = new List<int>();
            for (int p = 0; p < people.Count; p++)
            {
                var rel = new Vector2(people[p].X, people[p].Y) - from;
                float t = Math.Clamp(Vector2.Dot(rel, line) / MathF.Max(line.LengthSquared(), 1e-6f), 0, 1);
                if (Vector2.Distance(rel, line * t) < PersonR + Room + Cell && StoodAt(people[p])) list.Add(p);
            }
            around = list;
        }
        // pulled straight: from each corner, the furthest node along the path still in a clear line that keeps the
        // room the path itself had between the two (all of Room where there's that much, else as much as its tightest
        // spot there: a squeeze, or a stage's edge stepped off beside its side, lets only the stretch through it come
        // as close, and not the whole walk), the line followed over the floor as the walk will be, so it ends on that
        // node and not on a floor over or under it
        var pts = new List<Vector2>();
        var tight = new float[path.Count];
        int at = 0;
        while (at < path.Count - 1)
        {
            float t = Room;
            for (int q = at; q < path.Count; q++) tight[q] = t = NearEnd(path[q]) ? t : MathF.Min(t, clear[path[q]]);
            int next = at + 1;
            for (int k = path.Count - 1; k > at + 1; k--)
            {
                float need = tight[k] - 0.01f;
                if (fl.Clear(path[at], path[k], m => Roomy(m, need), Lands)) { next = k; break; }
            }
            at = next;
            pts.Add(at == path.Count - 1 ? goalAt : At(path[at]));
        }
        float length = 0;
        var prev = from;
        foreach (var p in pts) { length += Vector2.Distance(prev, p); prev = p; }
        return Made(pts, false, length);
    }

    /// <summary>
    /// The floors a walk reaches, as nodes: a floor over a cell, as many to a cell as there are floors over one another
    /// there. Each node's neighbour across each side of its cell (Next: 4 n + 0 east, 1 west, 2 north, 3 south) is the
    /// node the floor goes on to there: None where it doesn't (an edge, a step up too high, an obstructed node's own
    /// sides), OffGrid past the grid. Numbered by cell, so a pass over the nodes in order is a pass over the grid's rows
    /// (First[k] to First[k + 1] are cell k's). What was a cell's in the planner before (room from walls, a line of sight,
    /// A*) is a node's here, the eight ways out of a node found through its sides: a diagonal only where both ways round
    /// its corner come to the same node.
    /// </summary>
    sealed class Floors
    {
        public const int None = -1, OffGrid = -2;
        public readonly int Nx, Ny, Count;
        public readonly int[] First, CellOf, Next;
        public readonly float[] FloorOf;
        /// <summary>The cells with a node at all.</summary>
        public readonly int Cells;

        /// <summary>From the nodes as the floor-following made them (in the order it reached them): renumbered by cell,
        /// <paramref name="renumbered"/> saying where each went.</summary>
        public Floors(int nx, int ny, int count, int[] cellOf, float[] floorOf, int[] next, out int[] renumbered)
        {
            Nx = nx;
            Ny = ny;
            Count = count;
            First = new int[nx * ny + 1];
            for (int n = 0; n < count; n++) First[cellOf[n] + 1]++;
            for (int k = 0; k < nx * ny; k++)
            {
                if (First[k + 1] > 0) Cells++;
                First[k + 1] += First[k];
            }
            var fill = (int[])First.Clone();
            renumbered = new int[count];
            for (int n = 0; n < count; n++) renumbered[n] = fill[cellOf[n]]++;
            CellOf = new int[count];
            FloorOf = new float[count];
            Next = new int[count * 4];
            for (int n = 0; n < count; n++)
            {
                int to = renumbered[n];
                CellOf[to] = cellOf[n];
                FloorOf[to] = floorOf[n];
                for (int d = 0; d < 4; d++) Next[to * 4 + d] = next[n * 4 + d] is var m && m >= 0 ? renumbered[m] : m;
            }
        }

        static int Side(int di, int dj) => di > 0 ? 0 : di < 0 ? 1 : dj > 0 ? 2 : 3;

        /// <summary>The node one step (eight ways) from <paramref name="n"/>, or below 0; a diagonal not past a
        /// <paramref name="hard"/> corner.</summary>
        public int Neighbour(int n, int di, int dj, bool[] hard)
        {
            if (dj == 0 || di == 0) return Next[n * 4 + Side(di, dj)];
            int sx = Side(di, 0), sy = Side(0, dj);
            int a = Next[n * 4 + sx], b = Next[n * 4 + sy];
            if (a < 0 || b < 0 || hard[a] || hard[b]) return None;
            int m = Next[a * 4 + sy];
            return m >= 0 && m == Next[b * 4 + sx] ? m : None;
        }

        /// <summary>Whether a walk may step from <paramref name="n"/> into <paramref name="m"/>, its neighbour: not into
        /// a hard node, except off an edge (straight across a side, more than a step down). A stage's or a platform's edge
        /// drops onto the floor right beside it, which from that floor is against a wall, the stage's side.</summary>
        public bool Enters(int n, int m, bool diagonal, bool[] hard, bool[] solid) =>
            !hard[m] || !diagonal && !solid[m] && FloorOf[m] < FloorOf[n] - PathPlanner.Step;

        /// <summary>Metres from each node's middle to the nearest solid thing on its floor, a solid node or a side the
        /// floor doesn't go on from (a two-pass chamfer distance, the passes in row order as on a plain grid).</summary>
        public float[] Clearance(bool[] solid)
        {
            const float D = 1.4142f;
            var d = new float[Count];
            for (int n = 0; n < Count; n++) d[n] = solid[n] ? 0 : 1e6f;
            float Of(int m) => m >= 0 ? d[m] : m == None ? 0 : 1e6f;
            // the node diagonally across a corner, by way of either side: where the two ways come to different floors
            // (a corner where the floor changes level, a stage's edge beside the top of its steps), the clearer, as
            // the other is a floor over or under this one's
            float Corner(int n, int a, int b)
            {
                int x = Next[n * 4 + a], y = Next[n * 4 + b];
                float v = -1;
                if (x >= 0) v = Of(Next[x * 4 + b]);
                if (y >= 0) v = MathF.Max(v, Of(Next[y * 4 + a]));
                return v >= 0 ? v : x == None || y == None ? 0 : 1e6f;
            }
            for (int n = 0; n < Count; n++)
            {
                if (solid[n]) continue;
                float v = d[n];
                v = MathF.Min(v, Of(Next[n * 4 + 1]) + 1);
                v = MathF.Min(v, Of(Next[n * 4 + 3]) + 1);
                v = MathF.Min(v, Corner(n, 1, 3) + D);
                v = MathF.Min(v, Corner(n, 0, 3) + D);
                d[n] = v;
            }
            for (int n = Count - 1; n >= 0; n--)
            {
                if (solid[n]) continue;
                float v = d[n];
                v = MathF.Min(v, Of(Next[n * 4 + 0]) + 1);
                v = MathF.Min(v, Of(Next[n * 4 + 2]) + 1);
                v = MathF.Min(v, Corner(n, 0, 2) + D);
                v = MathF.Min(v, Corner(n, 1, 2) + D);
                d[n] = v;
            }
            for (int n = 0; n < Count; n++) d[n] = d[n] >= 1e5f ? 1e3f : MathF.Max(0, (d[n] - 0.5f) * Cell);
            return d;
        }

        /// <summary>Whether every node the straight line between two nodes' cells passes through is <paramref name="ok"/>,
        /// following the floor across each cell side it crosses, and it comes to <paramref name="n1"/> itself. A node it
        /// steps down into off an edge needs only to be <paramref name="lands"/> (as Enters: it lands beside what it
        /// stepped off), and so does the first (a path's corner where it landed).</summary>
        public bool Clear(int n0, int n1, Func<int, bool> ok, Func<int, bool>? lands = null)
        {
            // a supercover walk: steps along whichever axis' next cell boundary comes first
            int x0 = CellOf[n0] % Nx, y0 = CellOf[n0] / Nx, x1 = CellOf[n1] % Nx, y1 = CellOf[n1] / Nx;
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0), sx = Math.Sign(x1 - x0), sy = Math.Sign(y1 - y0);
            int x = x0, y = y0, cur = n0, prev = -1, ex = Side(sx, 0), ey = Side(0, sy);
            double tx = dx == 0 ? double.PositiveInfinity : 0.5 / dx, ty = dy == 0 ? double.PositiveInfinity : 0.5 / dy;
            double stepX = dx == 0 ? double.PositiveInfinity : 1.0 / dx, stepY = dy == 0 ? double.PositiveInfinity : 1.0 / dy;
            for (int n = 0; n <= dx + dy; n++)
            {
                if (!ok(cur) && !(lands != null && (prev < 0 || FloorOf[cur] < FloorOf[prev] - PathPlanner.Step) && lands(cur))) return false;
                if (x == x1 && y == y1) return cur == n1;
                prev = cur;
                if (tx < ty) { cur = Next[cur * 4 + ex]; x += sx; tx += stepX; }
                else if (ty < tx) { cur = Next[cur * 4 + ey]; y += sy; ty += stepY; }
                else
                {
                    // through a corner: both neighbours count too, and both ways round it come to the same node
                    int a = Next[cur * 4 + ex], b = Next[cur * 4 + ey];
                    if (a < 0 || b < 0 || !ok(a) || !ok(b)) return false;
                    cur = Next[a * 4 + ey];
                    if (cur != Next[b * 4 + ex]) return false;
                    x += sx; y += sy; tx += stepX; ty += stepY; n++;
                }
                if (cur < 0) return false;
            }
            return cur == n1;
        }

        /// <summary><paramref name="hard"/>: nodes no path enters (but off an edge: Enters); <paramref name="people"/>:
        /// the nodes too near a person, which a path steps in among only at ThroughPerson's price.</summary>
        public List<int>? AStar(bool[] hard, bool[] solid, bool[]? people, float[] clear, int start, int goal)
        {
            int gi = CellOf[goal] % Nx, gj = CellOf[goal] / Nx;
            var g = new float[Count];
            Array.Fill(g, float.MaxValue);
            var came = new int[Count];
            var closed = new bool[Count];
            var open = new PriorityQueue<int, float>();
            float H(int n)
            {
                int dx = Math.Abs(CellOf[n] % Nx - gi), dy = Math.Abs(CellOf[n] / Nx - gj);
                return Math.Max(dx, dy) + 0.4142f * Math.Min(dx, dy);
            }
            g[start] = 0;
            open.Enqueue(start, H(start));
            int expanded = 0;
            while (open.TryDequeue(out int cur, out _))
            {
                if (cur == goal)
                {
                    var path = new List<int> { cur };
                    while (cur != start) { cur = came[cur]; path.Add(cur); }
                    path.Reverse();
                    return path;
                }
                if (closed[cur]) continue;
                closed[cur] = true;
                if (++expanded > 400_000) return null;
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        if (di == 0 && dj == 0) continue;
                        // (no cutting a blocked corner)
                        int k = Neighbour(cur, di, dj, hard);
                        if (k < 0 || closed[k] || !Enters(cur, k, di != 0 && dj != 0, hard, solid)) continue;
                        // dearer close to walls, so it keeps to the middle of a gap or a corridor
                        float near = MathF.Max(0, (Room + 0.2f - clear[k]) / (Room + 0.2f));
                        float ng = g[cur] + (di != 0 && dj != 0 ? 1.4142f : 1f) * (1 + 3 * near) + (people != null && people[k] && !people[cur] ? ThroughPerson : 0);
                        if (ng >= g[k]) continue;
                        g[k] = ng;
                        came[k] = cur;
                        open.Enqueue(k, ng + H(k));
                    }
            }
            return null;
        }
    }

    /// <summary>Whether a triangle (2D) overlaps an axis-aligned square: separating axes (the square's two, the
    /// triangle's three edges' normals).</summary>
    static bool Overlaps(Vector2 a, Vector2 b, Vector2 c, Vector2 min, Vector2 max)
    {
        if (MathF.Max(a.X, MathF.Max(b.X, c.X)) < min.X || MathF.Min(a.X, MathF.Min(b.X, c.X)) > max.X) return false;
        if (MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) < min.Y || MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) > max.Y) return false;
        Span<Vector2> sq = stackalloc Vector2[4] { min, new(max.X, min.Y), max, new(min.X, max.Y) };
        Span<Vector2> tri = stackalloc Vector2[3] { a, b, c };
        for (int e = 0; e < 3; e++)
        {
            var p = tri[e];
            var q = tri[(e + 1) % 3];
            var axis = new Vector2(q.Y - p.Y, p.X - q.X);
            if (axis.LengthSquared() < 1e-12f) continue;
            float tMin = float.MaxValue, tMax = float.MinValue, sMin = float.MaxValue, sMax = float.MinValue;
            foreach (var v in tri) { float d = Vector2.Dot(v, axis); tMin = MathF.Min(tMin, d); tMax = MathF.Max(tMax, d); }
            foreach (var v in sq) { float d = Vector2.Dot(v, axis); sMin = MathF.Min(sMin, d); sMax = MathF.Max(sMax, d); }
            if (tMax < sMin || sMax < tMin) return false;
        }
        return true;
    }
}
