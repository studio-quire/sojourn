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
/// the assistant's look (LiveSnapshots), where touches land on a prim's faces, and its collision model (Sojourn Access
/// walks by the one without a GPU, Session's Collision/LocalCollision.cs; this one's rays are SojournView's, for comparing).
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
///
/// On the processor (2026-10-31): where no GPU can run it, Mesa's lavapipe beside the program draws instead
/// (Render's Gpu/SoftwareVulkan.cs; docs/access-app.md > How it fits > On the processor): no frames between pictures,
/// glances and settling only planned, what's around drawn once ahead of the first picture (<see cref="WarmUp"/>).
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
    // drawing on the processor (Render's Gpu/SoftwareVulkan.cs: Mesa's lavapipe beside the program), once no GPU here
    // could run it or one failed three times; SOJOURN_OFFSCREEN_SOFTWARE=1 from the start (testing on a PC with a GPU)
    string? _softwareManifest = Environment.GetEnvironmentVariable("SOJOURN_OFFSCREEN_SOFTWARE") == "1" ? SoftwareVulkan.Find() : null;
    long _threadsLoweredAt, _upAt;
    static readonly bool SoftwareShadows = Environment.GetEnvironmentVariable("SOJOURN_SOFTWARE_SHADOWS") == "1";
    // on the processor, per renderer (WarmUp): 0 nothing drawn yet, 1 a first frame drawn at once, 2 what's around drawn
    volatile int _warmStage;
    const double WarmFirstAfter = 2, WarmAfter = 20;   // seconds after it's up: at once, then the session's arrival settled
    /// <summary>Drawing on the processor: a software Vulkan driver, since this PC's graphics couldn't run it.</summary>
    public bool Software => _r?.Software ?? _softwareManifest != null;
    /// <summary>On the processor and not yet through its warm-up (or not yet up): a picture now pays for compiling the
    /// driver's shaders, a minute or more on a small PC, so callers wait longer and say why.</summary>
    public bool Preparing => Software && (_r == null || _warmStage < 2);

    /// <summary>How long a batch of pictures is waited for: on a GPU twice the settling, the wait for its subjects' looks
    /// and 20 s; on the processor five minutes while it's still preparing (a first picture on the blind tester's 2400G
    /// took minutes), else two and the looks' wait.</summary>
    TimeSpan PictureWait(double settle) =>
        !Software ? TimeSpan.FromSeconds(settle * 2 + LookWait(false) + 20) : Preparing ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(settle * 2 + LookWait(true) + 120);

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
    /// driver: a VM, Microsoft's basic display adapter, an old card) in a build without the software driver, or one
    /// where that failed too.</summary>
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
    /// <param name="physics">the region's physics shape types, shared with the collision model without a GPU when the app
    /// has one (else its own, asked for around the avatar)</param>
    public OffscreenRenderer(AgentSession session, LiveSnapshots snapshots, Func<bool> assistantBusy, Func<bool> assistantUp, string cacheDir, Action<string> log,
        PhysicsShapes? physics = null)
    {
        _physics = physics;
        _s = session;
        _snapshots = snapshots;
        _busy = assistantBusy;
        // SOJOURN_OFFSCREEN_SEEN=1: as if an assistant were up from the start (benches without one: tools/firstpicture.py)
        _assistantUp = Environment.GetEnvironmentVariable("SOJOURN_OFFSCREEN_SEEN") == "1" ? () => true : assistantUp;
        // the assistant's look waits as a description's pictures do (on a GPU its usual 8 s)
        snapshots.Patience = () => Software ? PictureWait(SettleSeconds) : (TimeSpan?)null;
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

    CollideAs Solid(Primitive p) => _physics?.Collide(p) ?? CollideAs.Unknown;

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
            var options = new FrameRendererOptions
            {
                CacheDirectory = _cacheDir,
                WorkerThreads = 2,
                ShadowResolutionCap = 1024,
                ProbeCacheCount = 128,
                // a quarter of the client's (1M vertices, 72 MB): unseen, a busy place's geometry near fits in one
                GeometryArenaScale = 0.25,
                // a quarter of the client's mesh bytes held too: it builds a LOD now and then, not every frame, and the
                // bytes are on disk (a three-hour ride held 200 MB of them, on an 8 GB PC)
                MeshBytesBudget = 64L << 20,
                Log = m => _log(m),
            };
            FrameRenderer r;
            if (_softwareManifest is { } manifest)
            {
                // what a description can do without, each dear on the processor: reflection probes but the default one
                // (each capture is a dozen renders; without even that one, shiny floors took the sky's blue and lost
                // their colours), SMAA and sharpening
                options = options with
                {
                    ProbeLevel = 0,
                    Graphics = options.Graphics with { FsaaType = 0, CasSharpness = 0 },
                };
                using (SoftwareVulkan.Use(manifest)) r = new FrameRenderer(options);
                LowerSoftwareThreads(force: true);
            }
            else r = new FrameRenderer(options);
            r.Quality.Mirrors = false;   // a second render of the scene per mirror in view, for nobody to see
            // each region's sky and water asked again once a minute rather than every 10 s (the process's only renderer:
            // a picture now and then needs them current, not each region's settings fetched and parsed six times a minute)
            RegionEnvironment.RefreshInterval = TimeSpan.FromMinutes(1);
            // nobody watches its frame rate: loading never held back for slow frames (a processor's are seconds), only
            // video memory governed
            r.Quality.LoadGovernor = false;
            r.OwnsWorld = () => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;
            PassProfiler.Enabled = true;
            _device = r.DeviceName;
            if (_feed == null) _feed = new RegionFeed(_s.World, r);
            else _feed.Switch(r);
            _r = r;
            (_upAt, _warmStage) = (Stopwatch.GetTimestamp(), 0);
            _everUp = true;
            _windowAt = Stopwatch.GetTimestamp();
            State = "running";
            _log(r.Software
                ? $"the view out of sight is up on {r.DeviceName}, drawing on the processor: pictures only when asked for, no frames between them"
                : $"the view out of sight is up on {r.DeviceName}: {IdleHz:0.#} frames a second at {IdleSize} px between pictures");
            return true;
        }
        catch (NoUsableGpuException e) when (_softwareManifest == null && SoftwareVulkan.Find() is { } manifest)
        {
            // no GPU here can run it (an old driver's Vulkan 1.1, a VM): the processor draws instead, at once
            _log($"the view out of sight can't run on this PC's graphics ({e.Message}); drawing on the processor instead (Mesa's lavapipe)");
            _softwareManifest = manifest;
            return false;
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
        if (_losses.Count >= 3 && _softwareManifest == null && SoftwareVulkan.Find() is { } manifest)
        {
            // a GPU that keeps failing (a driver that resets under load): the processor draws instead from now on
            _softwareManifest = manifest;
            _losses.Clear();
            State = "restarting";
            _retryAt = now + Stopwatch.Frequency * 3;
            _log($"the view out of sight: {(lost ? "the GPU was lost" : "it failed")} three times in ten minutes ({e.Message}); " +
                 "drawing on the processor from now on (Mesa's lavapipe), starting in 3 s");
            return;
        }
        if (_losses.Count >= 3)
        {
            _gaveUp = true;
            State = "stopped";
            _log($"the view out of sight stopped: {(lost ? "the GPU was lost" : "it failed")} three times in ten minutes ({e.Message}); " +
                 "no pictures, faces or touches by surface this session (walk mode's rays need no GPU)");
            return;
        }
        State = "restarting";
        _retryAt = now + Stopwatch.Frequency * 3;
        _log($"the view out of sight: {(lost ? "the GPU was lost" : "it failed")} ({e.GetType().Name}: {e.Message}); starting again in 3 s" +
             (lost ? "" : Environment.NewLine + e));
    }

    /// <summary>The software driver's threads below normal priority, as this thread is: on starting, and after frames at
    /// most every 10 s (it may start more as it goes).</summary>
    void LowerSoftwareThreads(bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && (now - _threadsLoweredAt) < Stopwatch.Frequency * 10) return;
        _threadsLoweredAt = now;
        try
        {
            if (SoftwareVulkan.LowerThreads() is var n and > 0) _log($"the view out of sight: {n} of the software driver's threads put below normal priority");
        }
        catch (Exception e) { _log($"the view out of sight: the software driver's threads kept their priority ({e.Message})"); }
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
        // on the processor a frame is a second or more of every core: none between pictures (picks, faces and touches
        // glance at what isn't drawn yet; walk mode's rays need no renderer), nor while the assistant works (a
        // description's pictures are drawn from cameras of their own as it's asked)
        double hz = r.Software ? 0 : busy ? Math.Max(BusyHz, IdleHz) : IdleHz;
        // but a first frame at once and what's around once it's settled in (WarmUp): the driver compiles its shaders as
        // it first draws them
        bool warm = r.Software && _warmStage < 2 && !_unseen
                    && Stopwatch.GetElapsedTime(_upAt, now).TotalSeconds >= (_warmStage == 0 ? WarmFirstAfter : WarmAfter);
        bool idleDue = (hz > 0 || warm) && now >= _nextIdle;
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
        if (warm && glance == null)
        {
            if (_warmStage == 0) WarmFirst(r, sim.Handle, eyes, fov);
            else WarmUp(r, sim.Handle, eyes, fov);
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
                // a prim's triangles are kept as it's built for drawing, so on the processor planning is enough
                PlanOnly = r.Software,
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

    /// <summary>On the processor, two seconds after it's up: one frame ahead from the eyes drawn at once with whatever has
    /// arrived, so the driver compiles what every picture draws whatever is in it (the sky, the ground, water, the
    /// reflection probe, the lighting passes) while the session is still arriving rather than in the first picture.</summary>
    void WarmFirst(FrameRenderer r, ulong region, (Vector3 Eye, Vector3 Forward, Vector3 Avatar) eyes, float fov)
    {
        _warmStage = 1;
        long t0 = Stopwatch.GetTimestamp();
        var share = ProcessorShare.Now();
        r.Motion.BeginFrame();
        Render(r, new FrameRequest
        {
            RegionHandle = region, Position = eyes.Eye, Forward = Vector3.Normalize(eyes.Forward + new Vector3(0, 0, -0.3f)),
            Width = 768, Height = 432, VerticalFov = fov, DrawDistance = Reach,
        });
        RunJobs(r);
        _log($"the view out of sight on the processor: a first frame drawn at once, {Stopwatch.GetElapsedTime(t0).TotalSeconds:0.0} s " +
             $"(what every picture needs compiled; {share.Since()})");
    }

    /// <summary>On the processor, once a renderer has settled in: what's around drawn whole as pictures are (settled,
    /// then drawn), the four ways round from the person's eyes as a description of the place has it, their own body in
    /// view below (nothing hidden: what avatars wear is drawn by shaders of its own), then the person from their front
    /// as a description of them has it, so the driver compiles its shaders for what's here now, at below normal
    /// priority, rather than in the first description's wait (minutes of every core on a small PC; the driver keeps
    /// nothing between sessions). Each at a picture's size, so its targets' sampling is compiled too.</summary>
    void WarmUp(FrameRenderer r, ulong region, (Vector3 Eye, Vector3 Forward, Vector3 Avatar) eyes, float fov)
    {
        long t0 = Stopwatch.GetTimestamp();
        var share = ProcessorShare.Now();
        double drawn = 0;
        var right = Vector3.Cross(eyes.Forward, Vector3.UnitZ);
        var down = new Vector3(0, 0, -0.3f);
        var front = eyes.Avatar + eyes.Forward * 2.8f + new Vector3(0, 0, 0.35f);
        var views = new (Vector3 At, Vector3 Dir, int W, int H, float Fov)[]
        {
            (eyes.Eye, eyes.Forward + down, 768, 432, fov), (eyes.Eye, right + down, 768, 432, fov),
            (eyes.Eye, -eyes.Forward + down, 768, 432, fov), (eyes.Eye, -right + down, 768, 432, fov),
            (front, eyes.Avatar + new Vector3(0, 0, 0.1f) - front, 640, 960, 40 * MathF.PI / 180),
        };
        foreach (var (at, dir, w, h, vfov) in views)
        {
            var req = new FrameRequest
            {
                RegionHandle = region, Position = at, Forward = Vector3.Normalize(dir),
                Width = w, Height = h, VerticalFov = vfov, DrawDistance = Reach,
            };
            long settle = Stopwatch.GetTimestamp(), improved = settle;
            int best = int.MaxValue;
            while (!_stop && Stopwatch.GetElapsedTime(settle).TotalSeconds < SettleSeconds)
            {
                r.Motion.BeginFrame();
                var st = Render(r, Settling(r, req));
                if (st.Pending < best) (best, improved) = (st.Pending, Stopwatch.GetTimestamp());
                if (st.Pending == 0 || Stopwatch.GetElapsedTime(improved).TotalSeconds > 1) break;
                RunJobs(r);
                _wake.WaitOne(50);
            }
            r.Motion.BeginFrame();
            long d0 = Stopwatch.GetTimestamp();
            Render(r, req);
            drawn += Stopwatch.GetElapsedTime(d0).TotalMilliseconds;
            RunJobs(r);
        }
        _warmStage = 2;
        _log($"the view out of sight on the processor: what's around drawn once ahead of any picture, {Stopwatch.GetElapsedTime(t0).TotalSeconds:0.0} s " +
             $"({drawn:0} ms of it drawing, its shaders compiled; {share.Since()})");
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
        q.Reflections = whole;
        // no shadows on the processor: drawing every caster into four sun cascades and a far map was half of each picture
        // there (a tester's busy place: 3 million triangles, 0.5 s of a 1 s frame) and a share of the first one's compiling,
        // and a description wants colours, shapes, text and looks, which sunlight without shade shows as well
        // (SOJOURN_SOFTWARE_SHADOWS=1 keeps them, for comparing)
        q.Shadows = q.ProjectorShadows = whole && (!r.Software || SoftwareShadows);
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
    /// with the snapshots' callbacks. On the processor the frames before it are only planned (<see cref="Settling"/>).</summary>
    void Picture(FrameRenderer r, FrameRequest req)
    {
        long t0 = Stopwatch.GetTimestamp();
        int best = int.MaxValue;
        long improved = t0;
        while (!_stop)
        {
            var st = Render(r, Settling(r, req));
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

    /// <summary>A frame while a picture settles: on the processor, where a drawn frame is a second or more of every core,
    /// only planned (what's in view asked for, counted and uploaded as it arrives), so the picture is the one frame
    /// drawn; on a GPU drawn, as the picture will be.</summary>
    static FrameRequest Settling(FrameRenderer r, FrameRequest req) => r.Software ? req with { PlanOnly = true } : req;

    FrameStats Render(FrameRenderer r, FrameRequest req)
    {
        var st = r.RenderFrame(req, NoTarget.Instance);
        r.WaitIdle();   // nothing in flight between frames: a picture's callback runs now, and the GPU is idle until the next
        _frames++;
        if (_firstFrame == 0) _firstFrame = Stopwatch.GetTimestamp();
        if (r.Software) LowerSoftwareThreads();
        _lastFrameCpu = st.TotalCpuMs;
        if (Stopwatch.GetElapsedTime(_windowAt).TotalSeconds >= 10)
        {
            var (gpu, frames, passes) = r.Profiler.TakeWindow();
            (_gpuMs, _gpuFrames, _cpuMs) = (gpu, frames, st.TotalCpuMs);
            if (r.Software && frames > 0)
                _log($"the view out of sight on the processor: {frames} frames lately, {gpu:0} ms each; dearest passes " +
                     string.Join(", ", passes.OrderByDescending(p => p.Ms).Take(8).Select(p => $"{p.Pass} {p.Ms:0}")));
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
