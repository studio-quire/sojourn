using System.Collections.Concurrent;
using System.Diagnostics;
using LibreMetaverse;
using Sojourn.Core.Agents;
using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Sojourn.Session.Assistant;
using Sojourn.Session.Control;
using Silk.NET.Vulkan;
using LVector3 = LibreMetaverse.Vector3;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Session.Offscreen;

/// <summary>A prim's faces as drawn (OffscreenRenderer.FacesAsync).</summary>
public readonly record struct FaceSet(FaceShape[] Faces);

/// <summary>
/// The renderer out of sight (docs/access.md > M0b): the VR client's real-time renderer with no window, swapchain or
/// headset, for an app that doesn't show the world (Sojourn Access) but needs what only a renderer knows: pictures for
/// the assistant's look (LiveSnapshots), where touches land on a prim's faces, and the local collision model.
///
/// One thread of its own, below normal priority, both simulates and renders (no pipe: at a few frames a second nothing
/// is gained by overlapping), so the world is this thread's between frames; picks, touches and collision rays from
/// other threads are queued to it (<see cref="OnWorld"/>) and run between frames. The camera is the avatar's eyes
/// (no pilot: it never sends movement or a camera of its own).
///
/// Frames, each read back nowhere (<see cref="NoTarget"/>):
/// - idle, <see cref="IdleHz"/> a second (0: none), small (<see cref="IdleSize"/>), turning a quarter at a time
///   around the avatar and a little down, so everything near is drawn now and then: its triangles kept for picks and the
///   collision model (prims count only once drawn), its textures and meshes asked for;
/// - while the assistant works, faster (<see cref="BusyHz"/>), every other frame looking ahead at a picture's size;
/// - a picture asked for (the snapshot ops): twenty frames a second at that size looking ahead until what's in view has
///   arrived (at most <see cref="SettleSeconds"/>), then one taken;
/// - a touch on something not drawn yet: frames looking at it until it is (a glance).
/// Each frame waits for its GPU work before the next (nothing in flight between frames).
///
/// A lost GPU (or any Vulkan failure) drops this renderer and starts a fresh one in the same process a few seconds
/// later, every region fed to it again (the VR client restarts its process instead: Sojourn.Client/DeviceLoss.cs; here
/// the app and the session go on, only the view starts again). Three losses in ten minutes and it stops for the
/// session. Claude Opus 5.5, 2026-10-29.
///
/// Unseen (2026-10-30): while nothing can look at its pictures (no assistant up, no picture asked for in the last
/// <see cref="PicturesLinger"/>: a packaged build always, a repo run without its assistant), it draws only what the
/// picks, faces and collision model need, which is geometry: no textures fetched, decoded or held but sculpt maps, no
/// avatars or what they wear (the collision model counts standing avatars as boxes from the world model), no shadows,
/// reflection probes, particles, flexible prims' sway or avatar physics (the draw distance stays: <see cref="UnseenDrawDistance"/>).
/// The first picture asked for, or the assistant coming up, draws it whole again at once (what's in view loading from
/// then on; a picture waits its usual settling time for it).
/// </summary>
public sealed partial class OffscreenRenderer : IDisposable
{
    readonly AgentSession _s;
    readonly LiveSnapshots _snapshots;
    readonly Func<bool> _busy, _assistantUp;
    readonly string _cacheDir;
    readonly Action<string> _log;
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly ConcurrentQueue<WorldJob> _jobs = new();
    readonly List<WorldJob> _waiting = new();   // jobs waiting on a glance (render thread)
    volatile bool _stop;
    FrameRenderer? _r;
    RegionFeed? _feed;
    PhysicsShapes? _physics;
    readonly List<long> _losses = new();
    long _retryAt;
    bool _gaveUp;

    /// <summary>Idle frames a second (0: only when a picture, touch or ray needs one; the world is still kept current).</summary>
    public double IdleHz { get; set; } = 2;
    /// <summary>The idle frames' longer side, pixels (4:3).</summary>
    public int IdleSize { get; set; } = 640;
    /// <summary>Frames a second while the assistant works on a request.</summary>
    public double BusyHz { get; set; } = 5;
    /// <summary>The longest side a picture is drawn at (a bigger one asked for is scaled up by its reader's own fit).</summary>
    public int LookMax { get; set; } = 1280;
    /// <summary>How long a picture waits for what's in view to load before it's taken.</summary>
    public double SettleSeconds { get; set; } = 3;
    /// <summary>Frames a second while the person moves (Sojourn Access's walk mode sets <see cref="Moving"/>): what comes
    /// near gets drawn sooner, so the collision model has it before it's reached.</summary>
    public double MovingHz { get; set; } = 4;
    /// <summary>The person is moving (walking, flying, driving): at least <see cref="MovingHz"/> frames a second.</summary>
    public volatile bool Moving;
    /// <summary>How long it stays drawn whole after anything last could look at its pictures (the assistant up, a
    /// picture asked for), before it's unseen again.</summary>
    public TimeSpan PicturesLinger { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>The draw distance while unseen, at most the person's (default: theirs). Kept: in the perf grid's forest
    /// and crowd unseen at 64 m instead of 128 saved only ~50 MB of memory and no video memory, while paths for walks
    /// (their area plus 12 m, up to 200 m: Sojourn.Access/Walk/PathPlanner.cs) know only what's been drawn.
    /// SOJOURN_OFFSCREEN_UNSEEN_DISTANCE=M for comparing.</summary>
    public float UnseenDrawDistance { get; set; } = float.TryParse(Environment.GetEnvironmentVariable("SOJOURN_OFFSCREEN_UNSEEN_DISTANCE"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : float.PositiveInfinity;
    /// <summary>Nothing can look at its pictures now: only what picks, faces and the collision model need is drawn.</summary>
    public bool Unseen => _unseen;
    volatile bool _unseen;
    FrameRenderer? _appliedTo;
    long _seenAt;   // when anything last could look at its pictures (0: never yet)

    /// <summary>Whether a renderer is up (false while starting, after a loss, or once given up).</summary>
    public bool Running => _r != null;
    public string State { get; private set; } = "starting";
    /// <summary>Given up without ever having come up: most likely a PC whose graphics can't run it (no Vulkan 1.3
    /// driver: a VM, Microsoft's basic display adapter, an old card).</summary>
    public bool NeverRan => _gaveUp && !_everUp;
    volatile bool _everUp;

    // what the status op reports (render thread writes, any thread reads)
    long _frames, _idleFrames, _lookFrames, _glanceFrames, _pictures;
    double _gpuMs = double.NaN, _cpuMs = double.NaN;
    int _gpuFrames;
    double _lastFrameCpu;
    long _windowAt;
    string _device = "";

    /// <param name="assistantUp">an assistant is running (or starting) that may ask for pictures</param>
    public OffscreenRenderer(AgentSession session, LiveSnapshots snapshots, Func<bool> assistantBusy, Func<bool> assistantUp, string cacheDir, Action<string> log)
    {
        _s = session;
        _snapshots = snapshots;
        _busy = assistantBusy;
        _assistantUp = assistantUp;
        _cacheDir = cacheDir;
        _log = log;
        _thread = new Thread(Run) { Name = "offscreen render", IsBackground = true, Priority = ThreadPriority.BelowNormal };
    }

    public void Start() => _thread.Start();

    // --- the world, from other threads -------------------------------------------------------------------------------

    sealed class WorldJob
    {
        /// <summary>Run between frames; true when done, false to wait for a glance at <see cref="Glance"/> first.</summary>
        public required Func<FrameRenderer, bool> Step;
        public required Action Fail;
        public long Deadline;
        public (ulong Region, Vector3 At)? Glance;
    }

    /// <summary>Run <paramref name="f"/> on the render thread between frames with the renderer (picks, rays); null if no
    /// renderer is up or it takes longer than <paramref name="timeout"/>.</summary>
    public Task<T?> OnWorld<T>(Func<FrameRenderer, T?> f, TimeSpan timeout, CancellationToken ct = default) where T : struct =>
        Queue<T>((r, done) => { done(f(r)); return true; }, timeout, ct);

    Task<T?> Queue<T>(Func<FrameRenderer, Action<T?>, bool> step, TimeSpan timeout, CancellationToken ct) where T : struct
    {
        if (_stop || _gaveUp) return Task.FromResult<T?>(null);
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Enqueue(new WorldJob
        {
            Step = r => step(r, v => tcs.TrySetResult(v)),
            Fail = () => tcs.TrySetResult(null),
            Deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency),
        });
        _wake.Set();
        return tcs.Task.WaitAsync(timeout + TimeSpan.FromSeconds(1), ct).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null, CancellationToken.None);
    }

    /// <summary>Where a touch on an object lands (AgentSession.FindTouchSurface), as the VR client's DirectTouch finds it
    /// without a pointer: a ray from the eyes to the point given, else to the prim's middle as drawn, counting only its
    /// linkset; if it isn't drawn yet, the view glances at it first (up to a second and a half). A child prim given is
    /// the part meant (Sojourn Access touches parts by number): the ray counts that part alone first, so a part behind
    /// another of its linkset (a lid under a trim) still gets the touch, then the linkset.</summary>
    public Task<TouchPoint?> FindTouchAsync(UUID prim, LVector3? at, CancellationToken ct) =>
        Queue<TouchPoint>((r, done) =>
        {
            if (_s.Client is not { Network.CurrentSim: { } sim } || _s.Region is not { } w || w.PrimById(prim) is not { } p) { done(null); return true; }
            _s.Objects.IsAttachment(w, p, out _, out var root);
            var middle = r.DrawnPosition(sim.Handle, p.LocalID) ?? V(w.RegionPosition(p));
            if (!r.HasTriangles(sim.Handle, p.LocalID) && !Due(_current!)) { _current!.Glance = (sim.Handle, middle); return false; }
            var set = new HashSet<UUID> { root.ID };
            foreach (var q in w.Prims.Values) if (q.ParentID == root.LocalID) set.Add(q.ID);
            var eye = Eyes(r, sim)?.Eye ?? V(sim.Client.Self.SimPosition) + new Vector3(0, 0, 0.7f);
            var sets = p.ID != root.ID ? new[] { new HashSet<UUID> { p.ID }, set } : new[] { set };
            foreach (var only in sets)
                foreach (var target in at is { } a ? new[] { V(a), middle } : new[] { middle })
                {
                    var dir = target - eye;
                    if (dir.LengthSquared() < 1e-6f) continue;
                    if (r.PickPrims(sim.Handle, eye, dir, dir.Length() + 4, q => only.Contains(q.ID)) is { } hit) { done(Point(hit)); return true; }
                }
            done(null);
            return true;
        }, TimeSpan.FromSeconds(3), ct);

    /// <summary>A prim's faces as drawn (RegionScene.FaceList), for naming them to the person; if it isn't drawn yet, the
    /// view glances at it first. Null when it can't be told (no renderer, not drawn, a rigged mesh).</summary>
    public Task<FaceSet?> FacesAsync(UUID prim, CancellationToken ct = default) =>
        Queue<FaceSet>((r, done) =>
        {
            if (_s.Client is not { Network.CurrentSim: { } sim } || _s.Region is not { } w || w.PrimById(prim) is not { } p) { done(null); return true; }
            if (!r.HasTriangles(sim.Handle, p.LocalID) && !Due(_current!))
            {
                _current!.Glance = (sim.Handle, r.DrawnPosition(sim.Handle, p.LocalID) ?? V(w.RegionPosition(p)));
                return false;
            }
            done(r.FaceList(sim.Handle, p.LocalID) is { Count: > 0 } list ? new FaceSet(list.ToArray()) : null);
            return true;
        }, TimeSpan.FromSeconds(3), ct);

    /// <summary>Where a touch on one face of a prim lands (AgentSession.FindFaceSurface): a ray straight at the middle of
    /// the face's largest triangle from just outside it, counting only that prim; its surface data is the face's own.
    /// Null when the prim has no such face or something of its own covers the spot (then the touch goes by
    /// <see cref="FindTouchAsync"/> with the face's number).</summary>
    public Task<TouchPoint?> FindFaceAsync(UUID prim, int face, CancellationToken ct) =>
        Queue<TouchPoint>((r, done) =>
        {
            if (_s.Client is not { Network.CurrentSim: { } sim } || _s.Region is not { } w || w.PrimById(prim) is not { } p) { done(null); return true; }
            if (r.FaceAim(sim.Handle, p.LocalID, face) is not var (at, normal))
            {
                if (!r.HasTriangles(sim.Handle, p.LocalID) && !Due(_current!))
                {
                    _current!.Glance = (sim.Handle, r.DrawnPosition(sim.Handle, p.LocalID) ?? V(w.RegionPosition(p)));
                    return false;
                }
                done(null);
                return true;
            }
            var from = at + normal * 0.5f;
            done(r.PickPrims(sim.Handle, from, -normal, 1f, q => q.ID == prim) is { Surface.Face: var f } hit && f == face ? Point(hit) : null);
            return true;
        }, TimeSpan.FromSeconds(3), ct);

    /// <summary>The local collision model (FrameRenderer.CollideRay, as the VR client's walk uses it): the nearest solid
    /// surface along a ray in the agent's region frame, met from outside; prims by their physics shapes where the region
    /// has said them (asked ahead around the avatar), phantom and flexible ones passable, the own avatar left out.</summary>
    /// <returns>Null when it couldn't be asked (no renderer up, not in a region), else what the ray met if anything.</returns>
    public Task<(CollideHit? Hit, bool Asked)?> CollideAsync(Vector3 from, Vector3 dir, float max, CancellationToken ct = default) =>
        Queue<(CollideHit? Hit, bool Asked)>((r, done) =>
        {
            if (_s.Client is not { Network.CurrentSim: { } sim } c) { done(null); return true; }
            done((r.CollideRay(sim.Handle, from, dir, max, c.Self.LocalID, Solid), true));
            return true;
        }, TimeSpan.FromSeconds(3), ct);

    /// <summary>Many collision rays at once (one trip to the render thread), each like <see cref="CollideAsync"/>;
    /// prims whose local ids are in <paramref name="passable"/> don't count (the vehicle one sits on). Null when it couldn't
    /// be asked.</summary>
    public Task<RayBatch?> CollideManyAsync(IReadOnlyList<(Vector3 From, Vector3 Dir, float Max)> rays, IReadOnlySet<uint>? passable = null,
        CancellationToken ct = default) =>
        Queue<RayBatch>((r, done) =>
        {
            if (_s.Client is not { Network.CurrentSim: { } sim } c) { done(null); return true; }
            Func<Primitive, CollideAs> solid = passable is { Count: > 0 } ? p => passable.Contains(p.LocalID) || passable.Contains(p.ParentID) ? CollideAs.Passable : Solid(p) : Solid;
            var hits = new CollideHit?[rays.Count];
            for (int i = 0; i < rays.Count; i++)
            {
                var (from, dir, max) = rays[i];
                if (dir.LengthSquared() > 1e-8f) hits[i] = r.CollideRay(sim.Handle, from, Vector3.Normalize(dir), max, c.Self.LocalID, solid);
            }
            done(new RayBatch(hits, sim.Handle));
            return true;
        }, TimeSpan.FromSeconds(3), ct);

    /// <summary>The collision model's solid triangles in a box (FrameRenderer.SolidTriangles), for planning walks: the
    /// agent's region frame; prims in <paramref name="passable"/> left out. Null when it couldn't be asked.</summary>
    public Task<Solids?> SolidsAsync(Vector3 min, Vector3 max, IReadOnlySet<uint>? passable = null, CancellationToken ct = default) =>
        Queue<Solids>((r, done) =>
        {
            if (_s.Client is not { Network.CurrentSim: { } sim }) { done(null); return true; }
            Func<Primitive, CollideAs> solid = passable is { Count: > 0 } ? p => passable.Contains(p.LocalID) || passable.Contains(p.ParentID) ? CollideAs.Passable : Solid(p) : Solid;
            var tris = new List<Vector3>();
            var ids = new List<uint>();
            r.SolidTriangles(sim.Handle, min, max, solid, tris, ids);
            done(new Solids(tris, ids, sim.Handle));
            return true;
        }, TimeSpan.FromSeconds(5), ct);

    CollideAs Solid(Primitive p)
    {
        if (((byte)p.PrimData.PathCurve & 0x80) != 0) return CollideAs.Passable;   // flexible prims are phantom
        var flags = _s.State.PrimFlagsOf(p);
        if (p.ParentID != 0 && _s.Region is { } w && w.Prims.TryGetValue(p.ParentID, out var root)) flags |= _s.State.PrimFlagsOf(root);
        if ((flags & PrimFlags.Phantom) != 0) return CollideAs.Passable;
        return _physics?.Of(p) is { } t ? (CollideAs)(byte)t : CollideAs.Unknown;
    }

    WorldJob? _current;
    bool Due(WorldJob j) => Stopwatch.GetTimestamp() >= j.Deadline - Stopwatch.Frequency * 3 / 2;

    static TouchPoint Point(PickHit h) => new(h.Region, h.LocalId, h.Id, h.Surface is { } s
        ? new TouchSurface(s.Face, L(s.ST), L(s.UV), L(h.Point), L(s.Normal), L(s.Binormal))
        : TouchSurface.None with { Position = L(h.Point) });

    static LVector3 L(Vector3 v) => new(v.X, v.Y, v.Z);
    static LibreMetaverse.Vector2 L(Vector2 v) => new(v.X, v.Y);
    static Vector3 V(LVector3 v) => new(v.X, v.Y, v.Z);

    // --- the loop ----------------------------------------------------------------------------------------------------

    void Run()
    {
        while (!_stop)
        {
            try
            {
                if (_r == null)
                {
                    if (_gaveUp || Stopwatch.GetTimestamp() < _retryAt || !Create()) { FailJobs(); _wake.WaitOne(250); continue; }
                }
                Step();
            }
            catch (Exception e) when (!_stop)
            {
                Lost(e);
            }
        }
    }

    bool Create()
    {
        State = "starting";
        try
        {
            var r = new FrameRenderer(new FrameRendererOptions
            {
                CacheDirectory = _cacheDir,
                WorkerThreads = 2,
                ShadowResolutionCap = 1024,
                ProbeCacheCount = 128,
                // a quarter of the client's (1M vertices, 72 MB): unseen, a busy place's geometry near fits in one
                GeometryArenaScale = 0.25,
                Log = m => _log(m),
            });
            r.Quality.Mirrors = false;   // a second render of the scene per mirror in view, for nobody to see
            r.OwnsWorld = () => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;
            PassProfiler.Enabled = true;
            _device = r.DeviceName;
            if (_feed == null) _feed = new RegionFeed(_s.World, r);
            else _feed.Switch(r);
            _r = r;
            _everUp = true;
            _windowAt = Stopwatch.GetTimestamp();
            State = "running";
            _log($"the view out of sight is up on {r.DeviceName}: {IdleHz:0.#} frames a second at {IdleSize} px between pictures");
            return true;
        }
        catch (Exception e)
        {
            _log($"the view out of sight couldn't start: {e.Message}");
            Lost(e);
            return false;
        }
    }

    void Lost(Exception e)
    {
        var r = _r;
        _r = null;
        FailJobs();
        long now = Stopwatch.GetTimestamp();
        _losses.RemoveAll(t => Stopwatch.GetElapsedTime(t, now).TotalMinutes > 10);
        _losses.Add(now);
        bool lost = IsDeviceLost(e);
        if (r != null)
        {
            // a lost device may refuse every call: what can be freed is, the rest goes with the process
            try { r.Dispose(); } catch (Exception de) { _log($"the view out of sight: letting go of the old renderer: {de.Message}"); }
        }
        if (_losses.Count >= 3)
        {
            _gaveUp = true;
            State = "stopped";
            _log($"the view out of sight stopped: {(lost ? "the GPU was lost" : "it failed")} three times in ten minutes ({e.Message}); " +
                 "no pictures, touches by surface or collision rays this session");
            return;
        }
        State = "restarting";
        _retryAt = now + Stopwatch.Frequency * 3;
        _log($"the view out of sight: {(lost ? "the GPU was lost" : "it failed")} ({e.GetType().Name}: {e.Message}); starting again in 3 s" +
             (lost ? "" : Environment.NewLine + e));
    }

    static bool IsDeviceLost(Exception? e)
    {
        for (; e != null; e = e.InnerException)
        {
            if (e is VulkanException { Result: Result.ErrorDeviceLost }) return true;
            if (e is AggregateException a && a.InnerExceptions.Any(IsDeviceLost)) return true;
        }
        return false;
    }

    void FailJobs()
    {
        foreach (var j in _waiting) j.Fail();
        _waiting.Clear();
        while (_jobs.TryDequeue(out var j)) j.Fail();
    }

    long _nextIdle, _nextTick;
    int _turn;
    bool _aheadNext;
    // SOJOURN_OFFSCREEN_LOSE_AFTER=S: the GPU "lost" once, S seconds after the first frame drawn (for checking the
    // recovery without resetting the PC's display driver; once per process, unlike the client's SOJOURN_FAKE_DEVICE_LOST)
    static readonly double LoseAfter = double.TryParse(Environment.GetEnvironmentVariable("SOJOURN_OFFSCREEN_LOSE_AFTER"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : -1;
    static bool _lostOnce;
    long _firstFrame;

    void Step()
    {
        var r = _r!;
        long now = Stopwatch.GetTimestamp();
        RunJobs(r);
        var me = _s.Client;
        var sim = me?.Network.Connected == true ? me.Network.CurrentSim : null;
        if (me != null) r.AvatarLimits.Self = me.Self.AgentID;   // never limited or an impostor
        var look = _snapshots.WaitingSize ?? (_snapshots.SeenWaiting ? (1024, 576) : null);
        var glance = _waiting.Select(j => j.Glance).FirstOrDefault(g => g != null);
        bool busy = _busy();
        if (look != null || busy || _assistantUp()) _seenAt = now;
        bool unseen = _seenAt == 0 || Stopwatch.GetElapsedTime(_seenAt, now) > PicturesLinger;
        if (unseen != _unseen || _appliedTo != r) Apply(r, unseen);
        double hz = busy ? Math.Max(BusyHz, IdleHz) : IdleHz;
        if (Moving) hz = Math.Max(hz, MovingHz);
        bool idleDue = hz > 0 && now >= _nextIdle;
        if (sim == null || (look == null && glance == null && !idleDue))
        {
            // nothing to draw now: the world kept current (region changes, finished uploads) four times a second
            if (now >= _nextTick) { r.Motion.BeginFrame(); r.Tick(); _nextTick = now + Stopwatch.Frequency / 4; }
            long until = hz > 0 ? _nextIdle : now + Stopwatch.Frequency;
            int ms = (int)Math.Clamp((until - now) * 1000 / Stopwatch.Frequency, 1, 250);
            _wake.WaitOne(ms);
            return;
        }
        if (LoseAfter >= 0 && !_lostOnce && _firstFrame != 0 && Stopwatch.GetElapsedTime(_firstFrame).TotalSeconds >= LoseAfter)
        {
            _lostOnce = true;
            throw new VulkanException("vkWaitForFences (simulated by SOJOURN_OFFSCREEN_LOSE_AFTER)", Result.ErrorDeviceLost);
        }

        r.Motion.BeginFrame();
        if (Eyes(r, sim) is not { } eyes) { r.Tick(); _wake.WaitOne(100); return; }
        _physics ??= new PhysicsShapes(_s) { Say = _log };
        _physics.Around(sim.Handle, eyes.Avatar);
        var hide = _s.HiddenInView(hideSelf: true);
        float fov = _s.Camera.Fov;
        FrameRequest req;
        if (look is var (lw, lh))
        {
            req = Ahead(sim.Handle, eyes, LookSize(lw, lh), fov, hide);
            Picture(r, req);
            _nextIdle = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency / Math.Max(hz, 0.5));
            return;
        }
        if (glance is var (gr, at) && gr == sim.Handle)
        {
            var dir = at - eyes.Eye;
            if (dir.LengthSquared() < 1e-4f) dir = eyes.Forward;
            req = new FrameRequest
            {
                RegionHandle = sim.Handle, Position = eyes.Eye, Forward = Vector3.Normalize(dir), Width = 512, Height = 512,
                VerticalFov = 0.9f, DrawDistance = Reach, HideAvatars = hide, NoAvatars = _unseen,
            };
            _glanceFrames++;
        }
        else if (busy && (_aheadNext = !_aheadNext))
        {
            req = Ahead(sim.Handle, eyes, LookSize(1024, 1024), fov, hide);
            _lookFrames++;
        }
        else
        {
            // idle: a quarter turn at a time, a little down, 75 degrees high and about 90 across
            float yaw = MathF.Atan2(eyes.Forward.Y, eyes.Forward.X) + _turn++ % 4 * MathF.PI / 2;
            const float pitch = -0.26f;
            int w = Math.Clamp(IdleSize, 128, 2048), h = w * 3 / 4;
            req = new FrameRequest
            {
                RegionHandle = sim.Handle, Position = eyes.Eye, Forward = new Vector3(MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch)),
                Width = w, Height = h, VerticalFov = 1.31f, DrawDistance = Reach, HideAvatars = hide, NoAvatars = _unseen,
            };
            _idleFrames++;
        }
        long t0 = Stopwatch.GetTimestamp();
        Render(r, req);
        if (Stopwatch.GetElapsedTime(t0).TotalMilliseconds is var took and > 500) _log($"the view out of sight: a slow frame, {took:0} ms");
        if (glance == null) _nextIdle = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency / Math.Max(hz, 0.05));
    }

    (int W, int H) LookSize(int w, int h)
    {
        // the view's own shape (16:9), as big as the picture asked for within LookMax
        int width = Math.Clamp(Math.Max(w, h * 16 / 9), 256, Math.Max(256, LookMax));
        return (width, width * 9 / 16);
    }

    /// <summary>The draw distance its frames use: the person's, or less while unseen.</summary>
    public float Reach => _unseen ? MathF.Min(_s.Camera.FarClip, UnseenDrawDistance) : _s.Camera.FarClip;

    /// <summary>Something is about to look at a picture: drawn whole from now on (render thread).</summary>
    void Seen(FrameRenderer r)
    {
        _seenAt = Stopwatch.GetTimestamp();
        if (_unseen || _appliedTo != r) Apply(r, unseen: false);
    }

    /// <summary>Unseen, only what picks, faces and the collision model need; else everything (the renderer's own
    /// defaults, mirrors aside).</summary>
    void Apply(FrameRenderer r, bool unseen)
    {
        bool whole = !unseen;
        var q = r.Quality;
        q.Textures = q.Particles = q.Flexi = q.AvatarPhysics = whole;
        q.Shadows = q.ProjectorShadows = q.Reflections = whole;
        bool first = _appliedTo == null, changed = first || _unseen != unseen;
        (_unseen, _appliedTo) = (unseen, r);
        if (!changed) return;
        if (unseen) _log($"the view out of sight is unseen{(first ? "" : " again")} (nothing can look at its pictures): only geometry drawn, " +
                         "no textures but sculpt maps, no avatars, shadows, reflections or particles");
        else if (!first) _log("the view out of sight is drawn whole: something can look at its pictures");
    }

    FrameRequest Ahead(ulong region, (Vector3 Eye, Vector3 Forward, Vector3 Avatar) eyes, (int W, int H) size, float fov, IReadOnlyCollection<UUID>? hide) => new()
    {
        RegionHandle = region, Position = eyes.Eye, Forward = Vector3.Normalize(eyes.Forward + new Vector3(0, 0, -0.08f)),
        Width = size.W, Height = size.H, VerticalFov = fov, DrawDistance = Reach, HideAvatars = hide, NoAvatars = _unseen,
    };

    /// <summary>A picture: frames at its size until what's in view has arrived (or the settling time is up), then one
    /// with the snapshots' callbacks.</summary>
    void Picture(FrameRenderer r, FrameRequest req)
    {
        long t0 = Stopwatch.GetTimestamp();
        int best = int.MaxValue;
        long improved = t0;
        while (!_stop)
        {
            var st = Render(r, req);
            _lookFrames++;
            if (st.Pending < best) (best, improved) = (st.Pending, Stopwatch.GetTimestamp());
            double waited = Stopwatch.GetElapsedTime(t0).TotalSeconds;
            if (st.Pending == 0 || waited >= SettleSeconds || Stopwatch.GetElapsedTime(improved).TotalSeconds > 1) break;
            RunJobs(r);
            // what's waited for is downloads and decoding, not frames: twenty a second ask for it all the same
            _wake.WaitOne(Math.Max(0, 50 - (int)st.TotalCpuMs));
            r.Motion.BeginFrame();
        }
        var plain = _snapshots.Take();
        var seen = _snapshots.TakeSeen(NoMenus.Instance);
        if (plain == null && seen == null) return;
        Render(r, req with { CaptureTo = (rgba, w, h) => { plain?.Invoke(rgba, w, h); seen?.Invoke(rgba, w, h); } });
        _pictures++;
    }

    FrameStats Render(FrameRenderer r, FrameRequest req)
    {
        var st = r.RenderFrame(req, NoTarget.Instance);
        r.WaitIdle();   // nothing in flight between frames: a picture's callback runs now, and the GPU is idle until the next
        _frames++;
        if (_firstFrame == 0) _firstFrame = Stopwatch.GetTimestamp();
        _lastFrameCpu = st.TotalCpuMs;
        if (Stopwatch.GetElapsedTime(_windowAt).TotalSeconds >= 10)
        {
            var (gpu, frames, _) = r.Profiler.TakeWindow();
            (_gpuMs, _gpuFrames, _cpuMs) = (gpu, frames, st.TotalCpuMs);
            _windowAt = Stopwatch.GetTimestamp();
        }
        return st;
    }

    void RunJobs(FrameRenderer r)
    {
        // jobs that waited on a glance get another go after each frame; new ones run now
        var batch = new List<WorldJob>(_waiting);
        _waiting.Clear();
        while (_jobs.TryDequeue(out var j)) batch.Add(j);
        foreach (var j in batch)
        {
            _current = j;
            j.Glance = null;
            try
            {
                if (!j.Step(r)) _waiting.Add(j);
            }
            catch (VulkanException) { j.Fail(); throw; }
            catch (Exception e) { _log($"the view out of sight: {e.Message}"); j.Fail(); }
        }
        _current = null;
    }

    /// <summary>The avatar's eyes as the renderer draws it (the shared dead reckoning; seated, on its seat as drawn), the
    /// way its body faces, and the avatar's middle.</summary>
    (Vector3 Eye, Vector3 Forward, Vector3 Avatar)? Eyes(FrameRenderer r, Simulator sim)
    {
        var c = sim.Client;
        var raw = V(c.Self.SimPosition);
        Vector3 at;
        Quaternion rot;
        var w = _s.World.Region(_s.GridKey, sim.Handle);
        if (c.Self.SittingOn != 0 && w != null && sim.ObjectsAvatars.TryGetValue(c.Self.LocalID, out var av)
            && r.Motion.Seated(id => w.Prims.TryGetValue(id, out var prim) ? prim : null, av, sim.Handle,
                sim.Stats.Dilation is > 0 and var d ? d : 1f, sim.SizeX > 0 ? sim.SizeX : 256f) is { } seated)
            (at, rot) = (seated.Pos, seated.Rot);
        else
        {
            var vel = c.Self.Velocity;
            at = r.Motion.Position(c.Self.AgentID, sim.Handle, raw, V(vel));
            var body = c.Self.Movement.BodyRotation;
            if (body == LibreMetaverse.Quaternion.Identity) body = c.Self.SimRotation;
            rot = new Quaternion(body.X, body.Y, body.Z, body.W);
        }
        var f = Vector3.Transform(Vector3.UnitX, rot);
        f.Z = 0;
        f = f.LengthSquared() > 1e-6f ? Vector3.Normalize(f) : Vector3.UnitX;
        return (at + new Vector3(0, 0, 0.72f) + f * 0.12f, f, at);
    }

    /// <summary>For the status op: how it's doing.</summary>
    public (string State, string Device, long Frames, long Idle, long Looks, long Glances, long Pictures, double GpuMs, int GpuFrames, double CpuMs,
        ulong VramUsed, ulong TextureBytes, int Losses) Status()
    {
        var r = _r;
        (ulong used, _) = r?.VramBudget() ?? (0, 0);
        return (State, _device, _frames, _idleFrames, _lookFrames, _glanceFrames, _pictures, _gpuMs, _gpuFrames,
            double.IsNaN(_cpuMs) ? _lastFrameCpu : _cpuMs, used, r?.TextureBytes ?? 0, _losses.Count);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(10_000);
        FailJobs();
        try { _r?.Dispose(); } catch (Exception e) { _log($"the view out of sight: closing: {e.Message}"); }
        _r = null;
        _feed?.Dispose();
    }

    /// <summary>Where frames go: nowhere (pictures are read back by the snapshots' callbacks, FrameRequest.CaptureTo).</summary>
    sealed class NoTarget : IFrameTarget
    {
        public static readonly NoTarget Instance = new();

        public bool Acquire(int slot, out Silk.NET.Vulkan.Semaphore ready, out Silk.NET.Vulkan.Semaphore done)
        {
            (ready, done) = (default, default);
            return true;
        }

        public void RecordPresent(CommandBuffer cmd, int eye, int eyeCount, GpuImage output) { }
        public void Present() { }
    }

    /// <summary>A picture "with the menus" when there are none.</summary>
    sealed class NoMenus : ISeenView
    {
        public static readonly NoMenus Instance = new();
        public IReadOnlyList<string> LayerNames => [];
        public IReadOnlyList<string> LeftOut => [];
        public byte[] Compose(byte[] rgba, int w, int h) => rgba;

        public (byte[] Rgba, int W, int H)? CloseUp(byte[] rgba, int w, int h, string name, out string? error)
        {
            error = "Sojourn Access draws no menus";
            return null;
        }
    }
}

/// <summary>What a batch of collision rays met, in order (null: nothing within its reach), in the frame of the region
/// they were cast in.</summary>
public readonly record struct RayBatch(CollideHit?[] Hits, ulong Region);

/// <summary>Solid triangles (three points each) and each one's prim, in the frame of the region asked about.</summary>
public readonly record struct Solids(List<Vector3> Triangles, List<uint> Prims, ulong Region);
