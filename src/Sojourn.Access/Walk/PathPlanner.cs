using System.Numerics;
using Sojourn.Core.Agents;
using Sojourn.Session.Collision;

namespace Sojourn.Access.Walk;

/// <summary>
/// Paths around what's in the way (M3), for `//go`, walk to, follow and the assistant's walks: the local collision
/// model's solid triangles (the renderer out of sight: prims by their physics shapes, only what's been drawn) laid on
/// a grid of 0.25 m cells over the walk's area plus a margin, what stands between knee and head height as obstacles;
/// nearer than the body's half width to them is blocked, and A* across it (eight ways) pays more near walls so it keeps
/// to the middle of gaps; then pulled straight wherever a line keeps that room, leaving the corners as waypoints. Heights are taken from the ground under each cell when the walk starts on the
/// ground, else from the floor the avatar stands on, so a path stays on one level: stairs up count as walls. People
/// aren't obstacles (they move). Written by Claude Opus 5.5, 2026-10-29.
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
    /// agent's region frame; null when there's no way (or the view can't be asked).</summary>
    public async Task<Plan?> PlanAsync(Vector2 from, float feet, Vector2 to, IReadOnlySet<uint>? passable = null, CancellationToken ct = default)
    {
        if (!_rays.Ready) return null;
        var lo = Vector2.Min(from, to) - new Vector2(Margin);
        var hi = Vector2.Max(from, to) + new Vector2(Margin);
        if (hi.X - lo.X > MaxSpan || hi.Y - lo.Y > MaxSpan)
        {
            // too far to plan in one go: the first stretch toward it
            var dir = Vector2.Normalize(to - from);
            return await PlanAsync(from, feet, from + dir * (MaxSpan / 2 - Margin), passable, ct);
        }
        var w = _s.Region;
        float? Ground(float x, float y) => w?.TerrainHeight(x, y);
        bool onGround = Ground(from.X, from.Y) is { } g0 && feet - g0 < 0.4f;
        float zMin = onGround ? Math.Min(feet, Ground(from.X, from.Y) ?? feet) - 2 : feet - 0.5f;
        float zMax = feet + 30;   // the ground can rise along the way: filtered per cell below
        var got = await _rays.SolidsAsync(new Vector3(lo, zMin), new Vector3(hi, zMax), passable, ct);
        if (got is not { } solids) return null;
        return await Task.Run(() => Solve(solids.Triangles, from, feet, to, lo, hi, onGround ? Ground : null), ct);
    }

    Plan? Solve(List<Vector3> tris, Vector2 from, float feet, Vector2 to, Vector2 lo, Vector2 hi, Func<float, float, float?>? ground)
    {
        int nx = (int)MathF.Ceiling((hi.X - lo.X) / Cell), ny = (int)MathF.Ceiling((hi.Y - lo.Y) / Cell);
        var solid = new bool[nx * ny];
        // the floor under each cell: the ground's, or the floor stood on
        var floor = new float[nx * ny];
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
                floor[j * nx + i] = ground?.Invoke(lo.X + (i + 0.5f) * Cell, lo.Y + (j + 0.5f) * Cell) ?? feet;

        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            Vector3 a = tris[t], b = tris[t + 1], c = tris[t + 2];
            var n = Vector3.Cross(b - a, c - a);
            float len = n.Length();
            if (len < 1e-9f) continue;
            bool steep = MathF.Abs(n.Z / len) < 0.7f;
            float tzLo = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), tzHi = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
            int i0 = Math.Max(0, (int)((MathF.Min(a.X, MathF.Min(b.X, c.X)) - lo.X) / Cell)), i1 = Math.Min(nx - 1, (int)((MathF.Max(a.X, MathF.Max(b.X, c.X)) - lo.X) / Cell));
            int j0 = Math.Max(0, (int)((MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - lo.Y) / Cell)), j1 = Math.Min(ny - 1, (int)((MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - lo.Y) / Cell));
            if (i0 > i1 || j0 > j1) continue;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int k = j * nx + i;
                    if (solid[k]) continue;
                    float f = floor[k], sLo = f + Knee, sHi = f + Head;
                    // in the body's height band: a wall crossing it, or anything flat-ish wholly within it (a table)
                    if (tzHi < sLo || tzLo > sHi) continue;
                    if (!steep && (tzLo < sLo || tzHi > sHi)) continue;
                    var c0 = new Vector2(lo.X + i * Cell, lo.Y + j * Cell);
                    if (Overlaps(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), new Vector2(c.X, c.Y), c0, c0 + new Vector2(Cell))) solid[k] = true;
                }
        }
        long now = Environment.TickCount64;
        lock (_blocked)
        {
            _blocked.RemoveAll(x => x.Until < now);
            foreach (var (spot, r, _) in _blocked) Disc(solid, nx, ny, lo, spot, r);
        }

        // how far each cell is from anything solid; too close for the avatar's body is blocked, and the path keeps to
        // the middle of what it passes through where there's room (a corner shaved too close is bumped)
        var clear = Clearance(solid, nx, ny);
        var blocked = new bool[nx * ny];
        for (int k = 0; k < blocked.Length; k++) blocked[k] = clear[k] < Body;

        (int, int) CellOf(Vector2 p) => (Math.Clamp((int)((p.X - lo.X) / Cell), 0, nx - 1), Math.Clamp((int)((p.Y - lo.Y) / Cell), 0, ny - 1));
        Vector2 Centre(int i, int j) => new(lo.X + (i + 0.5f) * Cell, lo.Y + (j + 0.5f) * Cell);
        var (si, sj) = CellOf(from);
        var (gi, gj) = CellOf(to);
        // standing against a wall: the cells around the start are free (else nothing could leave them)
        int near = (int)MathF.Ceiling(Body / Cell) + 1;
        for (int dj = -near; dj <= near; dj++)
            for (int di = -near; di <= near; di++)
                if (si + di >= 0 && sj + dj >= 0 && si + di < nx && sj + dj < ny && !solid[(sj + dj) * nx + si + di]) blocked[(sj + dj) * nx + si + di] = false;
        bool goalMoved = false;
        if (blocked[gj * nx + gi])
        {
            // the goal inside something (a bar, a chair): the nearest free cell within 3 m
            int best = -1;
            float bestD = float.MaxValue;
            int r = (int)(3 / Cell);
            for (int dj = -r; dj <= r; dj++)
                for (int di = -r; di <= r; di++)
                {
                    int ii = gi + di, jj = gj + dj;
                    if (ii < 0 || jj < 0 || ii >= nx || jj >= ny || blocked[jj * nx + ii]) continue;
                    float d = di * di + dj * dj;
                    if (d < bestD) { bestD = d; best = jj * nx + ii; }
                }
            if (best < 0) return null;
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
