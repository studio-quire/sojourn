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
/// be reached within GoalReach. People aren't obstacles (they move). Written by Claude Opus 5.5, 2026-10-29.
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

    public PathPlanner(AgentSession s, LocalCollision rays)
    {
        _s = s;
        _rays = rays;
    }

    /// <summary>A plan: the waypoints after the start (the last is the goal, or the nearest free spot to it), whether the
    /// straight line was clear anyway, its length.</summary>
    public sealed record Plan(List<Vector2> Waypoints, bool Direct, float Length, bool GoalMoved);

    /// <summary>Whether a plan can be asked for now (the view is up): a null plan then means there's no way.</summary>
    public bool CanAsk => _rays.Ready;

    /// <summary>Spots to treat as walls (where a walk got stuck), region frame, until they expire.</summary>
    readonly List<(Vector2 At, float R, long Until)> _blocked = new();

    public void Block(Vector2 at, float radius, TimeSpan forHowLong)
    {
        lock (_blocked) _blocked.Add((at, radius, Environment.TickCount64 + (long)forHowLong.TotalMilliseconds));
    }

    /// <summary>A path from <paramref name="from"/> (feet at <paramref name="feet"/>) to <paramref name="to"/>, both in the
    /// agent's region frame; null when there's no way (or the view can't be asked). <paramref name="toZ"/>: the goal's
    /// height (a person's middle, a thing's, a point on the ground), which tells a goal in a closed room of its own (no
    /// way) from one up on a stage beside this floor (the nearest spot below it).</summary>
    public async Task<Plan?> PlanAsync(Vector2 from, float feet, Vector2 to, IReadOnlySet<uint>? passable = null, CancellationToken ct = default,
        float? toZ = null)
    {
        if (!_rays.Ready) return null;
        var lo = Vector2.Min(from, to) - new Vector2(Margin);
        var hi = Vector2.Max(from, to) + new Vector2(Margin);
        if (hi.X - lo.X > MaxSpan || hi.Y - lo.Y > MaxSpan)
        {
            // too far to plan in one go: the first stretch toward it
            var dir = Vector2.Normalize(to - from);
            return await PlanAsync(from, feet, from + dir * (MaxSpan / 2 - Margin), passable, ct, null);
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
        return await Task.Run(() => Solve(solids.Triangles, from, feet, to, toZ, lo, hi, nearGround ? Ground : null), ct);
    }

    /// <summary>A step up the floor may take from one cell to the next (stairs, a kerb, a stage's steps), and how far
    /// down it may drop (off a step, a low stage); further down is an edge.</summary>
    const float Step = 0.4f, Drop = 1.2f;
    /// <summary>How far from a goal out of reach (inside a bar, on a stage with no steps) its stand-in may be.</summary>
    public const float GoalReach = 6f;

    Plan? Solve(List<Vector3> tris, Vector2 from, float feet, Vector2 to, float? toZ, Vector2 lo, Vector2 hi, Func<float, float, float?>? ground)
    {
        int nx = (int)MathF.Ceiling((hi.X - lo.X) / Cell), ny = (int)MathF.Ceiling((hi.Y - lo.Y) / Cell);
        int cells = nx * ny;
        // what stands over each cell, a list per cell: each triangle's heights over it, and whether it's flat enough to
        // stand on (its top a floor the walk may step onto)
        var head = new int[cells];
        Array.Fill(head, -1);
        var link = new List<int>();
        var spanLo = new List<float>();
        var spanHi = new List<float>();
        var flat = new List<bool>();
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            Vector3 a = tris[t], b = tris[t + 1], c = tris[t + 2];
            var n = Vector3.Cross(b - a, c - a);
            float len = n.Length();
            if (len < 1e-9f) continue;
            float up = MathF.Abs(n.Z / len);
            float tzLo = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), tzHi = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
            int i0 = Math.Max(0, (int)((MathF.Min(a.X, MathF.Min(b.X, c.X)) - lo.X) / Cell)), i1 = Math.Min(nx - 1, (int)((MathF.Max(a.X, MathF.Max(b.X, c.X)) - lo.X) / Cell));
            int j0 = Math.Max(0, (int)((MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - lo.Y) / Cell)), j1 = Math.Min(ny - 1, (int)((MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - lo.Y) / Cell));
            if (i0 > i1 || j0 > j1) continue;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var c0 = new Vector2(lo.X + i * Cell, lo.Y + j * Cell);
                    if (!Overlaps(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), new Vector2(c.X, c.Y), c0, c0 + new Vector2(Cell))) continue;
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
                    link.Add(head[k]);
                    head[k] = spanLo.Count;
                    spanLo.Add(zl);
                    spanHi.Add(zh);
                    flat.Add(up >= 0.7f);
                }
        }
        float? GroundAt(int k) => ground?.Invoke(lo.X + (k % nx + 0.5f) * Cell, lo.Y + (k / nx + 0.5f) * Cell);
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

        (int, int) CellOf(Vector2 p) => (Math.Clamp((int)((p.X - lo.X) / Cell), 0, nx - 1), Math.Clamp((int)((p.Y - lo.Y) / Cell), 0, ny - 1));
        Vector2 Centre(int i, int j) => new(lo.X + (i + 0.5f) * Cell, lo.Y + (j + 0.5f) * Cell);
        var (si, sj) = CellOf(from);
        var (gi, gj) = CellOf(to);

        // the floor followed out from where the avatar stands (four ways): a cell it can't reach is as good as solid, and
        // so is one with something in the way over its floor
        var floor = new float[cells];
        var reached = new bool[cells];
        var solid = new bool[cells];
        var queue = new Queue<int>();
        int start = sj * nx + si;
        // (the feet as walk mode reckons them can be a little off either way: the floor under them within reach of it)
        if (FloorFrom(start, feet + 0.4f) is { } f0 && f0 >= feet - 0.5f) floor[start] = f0;
        else { floor[start] = feet; level = true; }
        reached[start] = true;
        queue.Enqueue(start);
        while (queue.TryDequeue(out int k))
        {
            if (k != start && Obstructed(k, floor[k])) { solid[k] = true; continue; }
            int i = k % nx, j = k / nx;
            for (int d = 0; d < 4; d++)
            {
                int ii = i + (d == 0 ? 1 : d == 1 ? -1 : 0), jj = j + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (ii < 0 || jj < 0 || ii >= nx || jj >= ny) continue;
                int kk = jj * nx + ii;
                if (reached[kk] || FloorFrom(kk, floor[k]) is not { } fk) continue;
                floor[kk] = fk;
                reached[kk] = true;
                queue.Enqueue(kk);
            }
        }
        for (int k = 0; k < cells; k++) if (!reached[k]) solid[k] = true;
        long now = Environment.TickCount64;
        lock (_blocked)
        {
            _blocked.RemoveAll(x => x.Until < now);
            foreach (var (spot, r, _) in _blocked) Disc(solid, nx, ny, lo, spot, r);
        }

        // how far each cell is from anything solid; too close for the avatar's body is blocked, and the path keeps to
        // the middle of what it passes through where there's room (a corner shaved too close is bumped)
        var clear = Clearance(solid, nx, ny);
        var blocked = new bool[cells];
        for (int k = 0; k < blocked.Length; k++) blocked[k] = clear[k] < Body;
        // standing against a wall: the cells around the start are free (else nothing could leave them)
        int near = (int)MathF.Ceiling(Body / Cell) + 1;
        for (int dj = -near; dj <= near; dj++)
            for (int di = -near; di <= near; di++)
                if (si + di >= 0 && sj + dj >= 0 && si + di < nx && sj + dj < ny && !solid[(sj + dj) * nx + si + di]) blocked[(sj + dj) * nx + si + di] = false;
        blocked[start] = false;

        // where a body can get to from here (as A* moves: eight ways, no cutting a blocked corner)
        var open = new bool[cells];
        open[start] = true;
        queue.Enqueue(start);
        while (queue.TryDequeue(out int k))
        {
            int i = k % nx, j = k / nx;
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    int ii = i + di, jj = j + dj;
                    if (ii < 0 || jj < 0 || ii >= nx || jj >= ny) continue;
                    int kk = jj * nx + ii;
                    if (open[kk] || blocked[kk] || di != 0 && dj != 0 && (blocked[j * nx + ii] || blocked[jj * nx + i])) continue;
                    open[kk] = true;
                    queue.Enqueue(kk);
                }
        }
        // the goal out of reach in a space of its own (a room behind a closed door, a pen): no way. A space that meets
        // this one at an edge (a stage with no steps, a balcony) or the inside of a thing (a bar, a chair) isn't one
        bool Enclosed(int g, float z)
        {
            float gf = float.NegativeInfinity;
            if (GroundAt(g) is { } gg && gg <= z + 0.2f && gg >= z - 2.5f) gf = gg;
            for (int e = head[g]; e >= 0; e = link[e])
                if (flat[e] && spanHi[e] <= z + 0.2f && spanHi[e] >= z - 2.5f && spanHi[e] > gf) gf = spanHi[e];
            if (gf == float.NegativeInfinity || Obstructed(g, gf)) return false;
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
                    // stepping across or down into this space: they meet at an edge
                    if (reached[kk] && !solid[kk] && floor[kk] <= f + Step) return false;
                    float best = float.NegativeInfinity;
                    if (GroundAt(kk) is { } g2 && g2 <= f + Step) best = g2;
                    for (int e = head[kk]; e >= 0; e = link[e])
                        if (flat[e] && spanHi[e] <= f + Step && spanHi[e] > best) best = spanHi[e];
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
        // (why there's no way, for the walk log)
        string Why(string what) =>
            $"{what}: feet {feet:0.00}, start floor {floor[start]:0.00} (ground {GroundAt(start)?.ToString("0.00") ?? "unknown"}, surfaces "
            + $"{string.Join(" ", Spans(start))}), {reached.Count(x => x)} of {cells} cells reached, {open.Count(x => x)} open; "
            + $"the goal cell {(reached[gj * nx + gi] ? $"reached, floor {floor[gj * nx + gi]:0.00}{(solid[gj * nx + gi] ? ", in the way" : "")}" : "unreached")}";
        bool goalMoved = false;
        if (!open[gj * nx + gi])
        {
            if (toZ is { } tz && Enclosed(gj * nx + gi, tz)) { WalkMode.Log(Why("the goal is in a space of its own")); return null; }
            // the goal inside something (a bar, a chair) or out of reach where this space meets it (up on a stage with
            // no steps, a balcony): the nearest spot that can be got to, within GoalReach
            int best = -1;
            float bestD = float.MaxValue;
            int r = (int)(GoalReach / Cell);
            for (int dj = -r; dj <= r; dj++)
                for (int di = -r; di <= r; di++)
                {
                    int ii = gi + di, jj = gj + dj;
                    if (ii < 0 || jj < 0 || ii >= nx || jj >= ny || !open[jj * nx + ii]) continue;
                    // (a spot with room round it before one against the edge, where the region's walk rides up it)
                    float d = di * di + dj * dj + (clear[jj * nx + ii] < Room ? 16 : 0);
                    if (d < bestD && d <= r * r) { bestD = d; best = jj * nx + ii; }
                }
            if (best < 0) { WalkMode.Log(Why($"nothing open within {GoalReach} m of the goal")); return null; }
            (gi, gj) = (best % nx, best / nx);
            goalMoved = true;
        }
        var goal = goalMoved ? Centre(gi, gj) : to;
        bool Roomy(int k, float need) => !blocked[k] && (clear[k] >= need || NearEnd(k));
        bool NearEnd(int k)
        {
            int i = k % nx, j = k / nx;
            return Math.Abs(i - si) <= near && Math.Abs(j - sj) <= near || Math.Abs(i - gi) <= near && Math.Abs(j - gj) <= near;
        }
        if (Clear(nx, ny, si, sj, gi, gj, k => Roomy(k, Room))) return new Plan(new List<Vector2> { goal }, true, Vector2.Distance(from, goal), goalMoved);

        var path = AStar(blocked, clear, nx, ny, si, sj, gi, gj);
        if (path == null) return null;
        // pulled straight: from each corner, the furthest cell along the path still in a clear line that keeps the
        // room the path itself had (all of Room where there's that much, else as much as its tightest spot)
        float tight = Room;
        foreach (var k in path) if (!NearEnd(k)) tight = MathF.Min(tight, clear[k]);
        var pts = new List<Vector2>();
        int at = 0;
        while (at < path.Count - 1)
        {
            int next = at + 1;
            for (int k = path.Count - 1; k > at + 1; k--)
                if (Clear(nx, ny, path[at] % nx, path[at] / nx, path[k] % nx, path[k] / nx, c => Roomy(c, tight - 0.01f))) { next = k; break; }
            at = next;
            pts.Add(at == path.Count - 1 ? goal : Centre(path[at] % nx, path[at] / nx));
        }
        float length = 0;
        var prev = from;
        foreach (var p in pts) { length += Vector2.Distance(prev, p); prev = p; }
        return new Plan(pts, false, length, goalMoved);
    }

    /// <summary>Metres from each cell's middle to the nearest solid cell's edge (a two-pass chamfer distance).</summary>
    static float[] Clearance(bool[] solid, int nx, int ny)
    {
        const float D = 1.4142f;
        var d = new float[nx * ny];
        for (int k = 0; k < d.Length; k++) d[k] = solid[k] ? 0 : 1e6f;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int k = j * nx + i;
                float v = d[k];
                if (i > 0) v = MathF.Min(v, d[k - 1] + 1);
                if (j > 0)
                {
                    v = MathF.Min(v, d[k - nx] + 1);
                    if (i > 0) v = MathF.Min(v, d[k - nx - 1] + D);
                    if (i < nx - 1) v = MathF.Min(v, d[k - nx + 1] + D);
                }
                d[k] = v;
            }
        for (int j = ny - 1; j >= 0; j--)
            for (int i = nx - 1; i >= 0; i--)
            {
                int k = j * nx + i;
                float v = d[k];
                if (i < nx - 1) v = MathF.Min(v, d[k + 1] + 1);
                if (j < ny - 1)
                {
                    v = MathF.Min(v, d[k + nx] + 1);
                    if (i < nx - 1) v = MathF.Min(v, d[k + nx + 1] + D);
                    if (i > 0) v = MathF.Min(v, d[k + nx - 1] + D);
                }
                d[k] = v;
            }
        for (int k = 0; k < d.Length; k++) d[k] = d[k] >= 1e5f ? 1e3f : MathF.Max(0, (d[k] - 0.5f) * Cell);
        return d;
    }

    static void Disc(bool[] solid, int nx, int ny, Vector2 lo, Vector2 at, float r)
    {
        int ci = (int)((at.X - lo.X) / Cell), cj = (int)((at.Y - lo.Y) / Cell), cr = (int)MathF.Ceiling(r / Cell);
        for (int j = cj - cr; j <= cj + cr; j++)
            for (int i = ci - cr; i <= ci + cr; i++)
                if (i >= 0 && j >= 0 && i < nx && j < ny && (i - ci) * (i - ci) + (j - cj) * (j - cj) <= cr * cr) solid[j * nx + i] = true;
    }

    /// <summary>Whether every cell the straight line between two cells passes through is <paramref name="ok"/>.</summary>
    static bool Clear(int nx, int ny, int x0, int y0, int x1, int y1, Func<int, bool> ok)
    {
        // a supercover walk: steps along whichever axis' next cell boundary comes first
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0), sx = Math.Sign(x1 - x0), sy = Math.Sign(y1 - y0);
        int x = x0, y = y0;
        double tx = dx == 0 ? double.PositiveInfinity : 0.5 / dx, ty = dy == 0 ? double.PositiveInfinity : 0.5 / dy;
        double stepX = dx == 0 ? double.PositiveInfinity : 1.0 / dx, stepY = dy == 0 ? double.PositiveInfinity : 1.0 / dy;
        for (int n = 0; n <= dx + dy; n++)
        {
            if (!ok(y * nx + x)) return false;
            if (x == x1 && y == y1) return true;
            if (tx < ty) { x += sx; tx += stepX; }
            else if (ty < tx) { y += sy; ty += stepY; }
            else
            {
                // through a corner: both neighbours count too
                if (!ok(y * nx + x + sx) || !ok((y + sy) * nx + x)) return false;
                x += sx; y += sy; tx += stepX; ty += stepY; n++;
            }
            if (x < 0 || y < 0 || x >= nx || y >= ny) return false;
        }
        return true;
    }

    static List<int>? AStar(bool[] blocked, float[] clear, int nx, int ny, int si, int sj, int gi, int gj)
    {
        int start = sj * nx + si, goal = gj * nx + gi;
        var g = new float[nx * ny];
        Array.Fill(g, float.MaxValue);
        var came = new int[nx * ny];
        var closed = new bool[nx * ny];
        var open = new PriorityQueue<int, float>();
        g[start] = 0;
        open.Enqueue(start, H(si, sj));
        float H(int i, int j)
        {
            int dx = Math.Abs(i - gi), dy = Math.Abs(j - gj);
            return Math.Max(dx, dy) + 0.4142f * Math.Min(dx, dy);
        }
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
            int ci = cur % nx, cj = cur / nx;
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0) continue;
                    int i = ci + di, j = cj + dj;
                    if (i < 0 || j < 0 || i >= nx || j >= ny) continue;
                    int k = j * nx + i;
                    if (blocked[k] || closed[k]) continue;
                    // no cutting a blocked corner
                    if (di != 0 && dj != 0 && (blocked[cj * nx + i] || blocked[j * nx + ci])) continue;
                    // dearer close to walls, so it keeps to the middle of a gap or a corridor
                    float near = MathF.Max(0, (Room + 0.2f - clear[k]) / (Room + 0.2f));
                    float ng = g[cur] + (di != 0 && dj != 0 ? 1.4142f : 1f) * (1 + 3 * near);
                    if (ng >= g[k]) continue;
                    g[k] = ng;
                    came[k] = cur;
                    open.Enqueue(k, ng + H(i, j));
                }
        }
        return null;
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
