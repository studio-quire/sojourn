using LibreMetaverse;
using Sojourn.Core.Agents;
using Sojourn.Render.Collision;
using Sojourn.Render.World;
using Sojourn.Session.Control;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Session.Collision;

/// <summary>What a batch of collision rays met, in order (null: nothing within its reach), in the frame of the region
/// they were cast in.</summary>
public readonly record struct RayBatch(CollideHit?[] Hits, ulong Region);

/// <summary>Solid triangles (three points each) and each one's prim, in the frame of the region asked about.</summary>
public readonly record struct Solids(List<Vector3> Triangles, List<uint> Prims, ulong Region);

/// <summary>
/// The local collision model for an app that may have no usable GPU (Sojourn Access: walk mode's rays, paths, placing
/// by words; docs/access-app.md > Walk mode > The collision model): Sojourn.Render's <see cref="CollisionWorld"/> over
/// the session's world, with the region's physics shape types (<see cref="PhysicsShapes"/>, shared with the renderer
/// out of sight when that runs too) and phantom and flexible prims passable. A background loop keeps the shapes around
/// the person ready (within their draw distance, at least 64 m) and the types asked for. Rays run off the caller's
/// thread. Needs nothing from the GPU, so it answers the same on any PC. Claude Opus 5.5, 2026-10-30.
/// </summary>
public sealed class LocalCollision : IDisposable
{
    readonly AgentSession _s;
    readonly CollisionWorld _world;
    readonly Action<string> _log;
    volatile bool _stop;

    /// <summary>The region's physics shape types for the prims around the person.</summary>
    public PhysicsShapes Physics { get; }

    public LocalCollision(AgentSession session, Action<string> log)
    {
        _s = session;
        _log = log;
        Physics = new PhysicsShapes(session) { Say = log };
        // a quarter of the cores for building and decoding shapes, one or two (a 4-core PC keeps three for the rest)
        _world = new CollisionWorld(session.World, log, threads: Math.Clamp(Environment.ProcessorCount / 4, 1, 2));
        _ = Task.Run(Loop);
        log("the collision model is up (no GPU needed): rays, paths and placing answer from the world's prims and their physics shapes");
    }

    /// <summary>Rays can be asked: the session is in a region.</summary>
    public bool Ready => _s.Client is { Network.CurrentSim: not null };

    /// <summary>How far around the person shapes are kept ready: their draw distance, 64 to 256 m.</summary>
    public float Reach => Math.Clamp(_s.Camera.FarClip, 64f, 256f);

    Func<Primitive, CollideAs> Solid(IReadOnlySet<uint>? passable) => passable is { Count: > 0 }
        ? p => passable.Contains(p.LocalID) || passable.Contains(p.ParentID) ? CollideAs.Passable : Physics.Collide(p)
        : Physics.Collide;

    /// <summary>Many rays at once, each the nearest solid surface along it in the agent's region frame, met from outside;
    /// prims whose local ids are in <paramref name="passable"/> (or whose root's are) don't count (the vehicle one sits
    /// on), nor does the own avatar. Null when it can't be asked (not in a region).</summary>
    public Task<RayBatch?> CollideManyAsync(IReadOnlyList<(Vector3 From, Vector3 Dir, float Max)> rays, IReadOnlySet<uint>? passable = null,
        CancellationToken ct = default) => Task.Run(() =>
        {
            if (_s.Client is not { Network.CurrentSim: { } sim } c) return (RayBatch?)null;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var hits = _world.CollideMany(sim.Handle, rays, c.Self.LocalID, Solid(passable));
            Took(rays.Count, System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            return new RayBatch(hits, sim.Handle);
        }, ct);

    /// <summary>The last batch of rays: how many and how long it took (ms), and the slowest lately.</summary>
    public (int Rays, double Ms, double SlowestMs) LastBatch => (_lastRays, _lastMs, _slowestMs);
    volatile int _lastRays;
    double _lastMs, _slowestMs;
    long _slowestAt;

    void Took(int rays, double ms)
    {
        (_lastRays, _lastMs) = (rays, ms);
        long now = Environment.TickCount64;
        if (ms >= _slowestMs || now - _slowestAt > 60_000) (_slowestMs, _slowestAt) = (ms, now);
        if (ms > 100) _log($"collision: a slow batch, {rays} rays in {ms:0} ms");
    }

    /// <summary>One ray (<see cref="CollideManyAsync"/>); Asked false when it couldn't be asked.</summary>
    public async Task<(CollideHit? Hit, bool Asked)> CollideAsync(Vector3 from, Vector3 dir, float max, CancellationToken ct = default) =>
        await CollideManyAsync([(from, dir, max)], null, ct) is { } b ? (b.Hits[0], true) : (null, false);

    /// <summary>The solid triangles in a box (the agent's region frame), for planning walks; prims in
    /// <paramref name="passable"/> left out. Null when it can't be asked.</summary>
    public Task<Solids?> SolidsAsync(Vector3 min, Vector3 max, IReadOnlySet<uint>? passable = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (_s.Client is not { Network.CurrentSim: { } sim }) return (Solids?)null;
            var tris = new List<Vector3>();
            var ids = new List<uint>();
            _world.SolidTriangles(sim.Handle, min, max, Solid(passable), tris, ids);
            return new Solids(tris, ids, sim.Handle);
        }, ct);

    public (int Regions, int Prims, int Placed, int Shapes, int Ready, int Waiting, int Fetching, int Failed, long Bytes) Status() => _world.Status();

    async Task Loop()
    {
        long nextAround = 0, nextReport = Environment.TickCount64 + 60_000;
        string? reported = null;
        while (!_stop)
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);
                if (_s.Client is not { Network: { Connected: true, CurrentSim: { } sim } } c) continue;
                var me = c.Self.SimPosition;
                var at = new Vector3(me.X, me.Y, me.Z);
                Physics.Around(sim.Handle, at);
                long now = Environment.TickCount64;
                if (now < nextAround) continue;
                nextAround = now + 1000;
                _world.Around(sim.Handle, at, Reach);
                if (now >= nextReport)
                {
                    // what it holds, for a tester's log (a long ride's memory): every five minutes if it changed
                    nextReport = now + 300_000;
                    var st = _world.Status();
                    var line = $"collision: {st.Regions} regions, {st.Prims} prims, {st.Shapes} shapes ({st.Bytes >> 20} MB), {Physics.Known} physics types known";
                    if (line != reported) _log(reported = line);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _log($"collision: {e.GetType().Name}: {e.Message}");
                await Task.Delay(2000).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _world.Dispose();
    }
}
