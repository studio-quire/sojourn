using System.Collections.Concurrent;
using System.Diagnostics;
using LibreMetaverse;
using Sojourn.Render.Environment;
using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;
using Sojourn.Render.Shadows;
using Sojourn.Render.World;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Sojourn.Render.Realtime;

public sealed record FrameRendererOptions
{
    /// <summary>Folder holding the GLSL shaders (defaults to Shaders/ beside the assembly).</summary>
    public string? ShaderDirectory { get; init; }
    /// <summary>Folder for caches (compiled SPIR-V).</summary>
    public required string CacheDirectory { get; init; }
    public VulkanSetup Vulkan { get; init; } = new();
    public int WorkerThreads { get; init; } = 3;
    /// <summary>The geometry arenas' size as a share of the usual 4M vertices (Gpu/GeometryPool.cs): less for a renderer
    /// that holds little (out of sight); more arenas come as needed either way.</summary>
    public double GeometryArenaScale { get; init; } = 1;
    /// <summary>Mesh assets' bytes kept to build each LOD from (World/Geometry.cs), oldest out; past it they're read from
    /// the disk cache again.</summary>
    public long MeshBytesBudget { get; init; } = 256L << 20;
    public ViewOptions View { get; init; } = new();
    /// <summary>Sun/moon shadows (the viewer's RenderShadowDetail >= 1).</summary>
    public bool Shadows { get; init; } = true;
    /// <summary>Longest side of the sun cascade and projector shadow maps. The viewer sizes them to the screen
    /// (RenderShadowResolutionScale 1), which at a headset eye's ~2100x2200 makes shadows the costliest pass; 0 keeps
    /// the viewer's rule.</summary>
    public int ShadowResolutionCap { get; init; }
    /// <summary>Projector shadows, two maps (RenderShadowDetail 2).</summary>
    public bool ProjectorShadows { get; init; } = true;
    public bool Probes { get; init; } = true;
    /// <summary>RenderReflectionProbeLevel: 0 default probe only, 1 + manual, 2 + terrain/water, 3 + objects.</summary>
    public int ProbeLevel { get; init; } = 3;
    public int ProbeCacheCount { get; init; } = 512;
    /// <summary>Probe captures a frame may take while probes in view are missing (each is a dozen small deferred
    /// renders, ~5 ms of GPU); refreshes of stale probes take one at most every <see cref="ProbeRefreshInterval"/>.</summary>
    public int ProbeCapturesPerFrame { get; init; } = 1;
    /// <summary>Spread each capture over frames, this many of its twelve face renders a frame (one capture at a time;
    /// the default probe is still taken whole). 0 takes captures whole, <see cref="ProbeCapturesPerFrame"/> a frame.</summary>
    public int ProbeFacesPerFrame { get; init; } = 2;
    public TimeSpan ProbeRefreshInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public ViewerRenderSettings Graphics { get; init; } = ViewerRenderSettings.Default;
    public Particles.ParticleSettings Particles { get; init; } = new();
    public Action<string>? Log { get; init; }
}

/// <summary>The quality settings that change while running (the settings menu's view section, the agent API's
/// SojournSettings); the renderer reads them every frame. They start from <see cref="FrameRendererOptions"/>.</summary>
public sealed class RenderQuality
{
    /// <summary>Sun, moon and projector shadows.</summary>
    public volatile bool Shadows = true;
    /// <summary>Longest side of the shadow maps; 0: the viewer's rule (the screen's size).</summary>
    public volatile int ShadowCap;
    public volatile bool ProjectorShadows = true;
    /// <summary>Reflection probes (environment reflections and ambient light from captures); off: the sky's alone.</summary>
    public volatile bool Reflections = true;
    /// <summary>Mirrors (the viewer's RenderMirrors, which its presets leave off; on here, since only a mirror in view costs
    /// anything): each eye's reflection is rendered at this fraction of the eye's size (0.25-1).</summary>
    public volatile bool Mirrors = true;
    public float MirrorScale = 0.5f;
    /// <summary>RenderVolumeLODFactor: how far objects keep their detail (the viewer's High-Ultra 1.75).</summary>
    public float LodFactor = 1.75f;
    /// <summary>The load governor's frame side: loading slowed and the draw distance brought in while frames run long
    /// (Realtime/LoadGovernor.cs). Video memory is governed either way.</summary>
    public volatile bool LoadGovernor = true;
    /// <summary>See-through surfaces (Frame/Renderer.Layers.cs): at most this many blended layers shaded a pixel, fewer
    /// where the nearer ones hide what's behind (0: every layer); and their lighting made simpler (ambient, haze and
    /// local lights per vertex), a small change in look.</summary>
    public volatile int AlphaLayers;
    public volatile bool SimpleAlpha;
    /// <summary>Flexible prims simulated as the viewer does (its Rendering > Features > Flexible Objects): hair, skirts,
    /// flags and tails of old-style prims sway and hang; off, they're drawn straight.</summary>
    public volatile bool Flexi = true;
    /// <summary>Avatar physics (the viewer's AvatarPhysics): breast, belly and butt movement from physics wearables.</summary>
    public volatile bool AvatarPhysics = true;
    /// <summary>Textures (World/TextureStore.DataOnly when off): off, only those whose pixels shape something load
    /// (sculpt maps) and faces are drawn without the rest, for a view nobody sees (Sojourn Access with nothing to look
    /// at its pictures: Sojourn.Session/Offscreen/OffscreenRenderer.cs). Back on, what's in view loads as it's drawn.</summary>
    public volatile bool Textures = true;
    /// <summary>Particles simulated and drawn (off: none, for a view nobody sees).</summary>
    public volatile bool Particles = true;
    // GPU options to judge (docs/rendering.md > GPU options; all off by default, each a small change in look):
    /// <summary>The sun-light map (sun shadow, SSAO, projector shadows) and its blur at half the eye's size, brought
    /// back up depth-aware (Frame/Renderer.HalfLight.cs).</summary>
    public volatile bool HalfSsao;
    /// <summary>The far sun cascades fitted this much larger (0: as the viewer), so one of them can be kept from the
    /// frame before while the view moves too (Frame/Renderer.Cascades.cs).</summary>
    public volatile float FarCascadeMargin;
    /// <summary>Objects smaller than this many texels (bounding radius) in a sun cascade's map aren't drawn into it
    /// (0: every caster, as the viewer).</summary>
    public volatile float MinCasterTexels;
    /// <summary>The coarse far sun cascade's map size (0: none; shadows end as the viewer's do): shadows carried on past
    /// the fourth cascade, and past the governor's trim, out to a quarter beyond the draw distance, fading there
    /// (docs/rendering.md > Where shadows end).</summary>
    public volatile int FarShadowSize = 1024;
}

/// <summary>One eye relative to the frame's camera, in the camera's eye space (x right, y up, z back, metres): where
/// it sits and how it's turned, and for headsets an asymmetric field of view as tangents of its half-angles, left and
/// down negative (all zero: symmetric, from the camera's vertical FOV).</summary>
public readonly record struct EyeSpec(Vector3 Offset, Quaternion Rotation, float TanLeft = 0, float TanRight = 0, float TanUp = 0, float TanDown = 0)
{
    /// <summary>The lenses' hidden area for this eye (XR_KHR_visibility_mask: triangles on the eye's z = -1 plane), or
    /// null; the renderer skips shading what's well inside it (Frame/Renderer.Lens.cs).</summary>
    public Vector2[]? Hidden { get; init; }

    /// <summary>A desktop stereo eye: shifted along camera right, same orientation and projection as the camera.</summary>
    public EyeSpec(float offsetRight) : this(new Vector3(offsetRight, 0, 0), Quaternion.Identity) { }

    public bool Asymmetric => TanLeft != 0 || TanRight != 0 || TanUp != 0 || TanDown != 0;
    public bool IsCamera => Offset == Vector3.Zero && Rotation.IsIdentity && !Asymmetric;
}

/// <summary>What one frame shows: a camera in a region's coordinates, the per-eye image size, and the eyes (one for
/// the desktop; two for stereo, culled, shadowed and probed once and drawn twice).</summary>
public sealed record FrameRequest
{
    public required ulong RegionHandle { get; init; }
    public required Vector3 Position { get; init; }
    public required Vector3 Forward { get; init; }
    public Vector3 Up { get; init; } = Vector3.UnitZ;
    /// <summary>Vertical field of view in radians (the viewer's CameraAngle, default 60 degrees).</summary>
    public float VerticalFov { get; init; } = 1.047197551f;
    public required int Width { get; init; }
    public required int Height { get; init; }
    public float DrawDistance { get; init; } = 128f;
    public IReadOnlyList<EyeSpec> Eyes { get; init; } = Mono;
    public IReadOnlyCollection<UUID>? HideAvatars { get; init; }
    /// <summary>No avatar drawn at all, nor what they wear (a view nobody sees: their bakes, worn meshes and poses aren't
    /// loaded or made for it).</summary>
    public bool NoAvatars { get; init; }
    /// <summary>The person's own avatar as seen from the headset (<see cref="Sojourn.Render.SelfBody"/>).</summary>
    public SelfBody? Self { get; init; }
    public bool ShowNames { get; init; }
    /// <summary>Environment time (UTC); null = now.</summary>
    public DateTime? Time { get; init; }
    public UUID? LocalSky { get; init; }
    /// <summary>Also save the first eye's image as a PNG here (written once the GPU is done with the frame).</summary>
    public string? CapturePath { get; init; }
    /// <summary>Also hand the first eye's image (RGBA8, gamma-encoded) to this once the GPU is done with the frame:
    /// what the person sees, without the overlay.</summary>
    public Action<byte[], int, int>? CaptureTo { get; init; }
    /// <summary>Hand the first eye's image as it goes to the display (RGBA8, gamma-encoded) to this, from a worker,
    /// once the GPU is done with the frame: the overlay and HUDs in it (on the desktop the panel and feed too; the
    /// headset's quad layers are the compositor's, not in it). The frame never waits for it (FrameRenderer.Seen.cs).</summary>
    public Action<byte[], int, int>? SeenTo { get; init; }
    /// <summary>A frame of a recording (FrameRenderer.Video.cs): the first eye, scaled down on the GPU, without the overlay.</summary>
    public VideoCapture? VideoTo { get; init; }
    /// <summary>Drawn over every eye after post-processing (the assistant's panel and pointer).</summary>
    public OverlayScene? Overlay { get; init; }
    /// <summary>The person's HUD attachments (Realtime/Hud.cs): drawn after the captures, under the overlay.</summary>
    public HudRequest? Hud { get; init; }
    /// <summary>Planned, not drawn: what's in view is asked for and counted (FrameStats.Pending) and what has arrived
    /// goes to the GPU, as a still's settling does; nothing is recorded or presented. For a picture's settling where a
    /// drawn frame is dear (the renderer out of sight on the processor).</summary>
    public bool PlanOnly { get; init; }

    public static readonly IReadOnlyList<EyeSpec> Mono = new[] { new EyeSpec(0) };
    public static IReadOnlyList<EyeSpec> Stereo(float ipd) => new[] { new EyeSpec(-ipd / 2), new EyeSpec(ipd / 2) };
}

/// <summary>Where a frame goes: a swapchain image (or an XR swapchain later). Called on the render thread.</summary>
public interface IFrameTarget
{
    /// <summary>Called once the frame slot is free: an image to draw into, the semaphore that signals it's ready and
    /// one to signal when drawing is done. False skips the frame (minimised, swapchain being rebuilt).</summary>
    bool Acquire(int slot, out Semaphore ready, out Semaphore done);
    /// <summary>Record copying one eye's finished image (TransferSrcOptimal) into the acquired image.</summary>
    void RecordPresent(CommandBuffer cmd, int eye, int eyeCount, GpuImage output);
    /// <summary>After submission: hand the image to the display.</summary>
    void Present();
    /// <summary>Record copying one eye's depth (reverse-Z, infinite far, the view's near plane, 0.1 m, at 1) where the
    /// display wants it; called just before <see cref="RecordPresent"/>. Headsets use it to reproject a late frame by
    /// position as well as rotation.</summary>
    void RecordDepth(CommandBuffer cmd, int eye, GpuImage depth) { }
    /// <summary>Where this eye's hands go over the display's menus (Overlay.Hands.cs), in COLOR_ATTACHMENT_OPTIMAL, called
    /// after <see cref="RecordPresent"/> while the frame's overlay has a cut; null for a display without menus of its own.</summary>
    HandsTarget? Hands(CommandBuffer cmd, int eye) => null;
    /// <summary>The depth this eye just handed the display (<see cref="RecordDepth"/>), for the menus' footprints to be
    /// carved out of it with menus over walls (Overlay.Walls.cs); null where there's none this frame.</summary>
    DepthTarget? DepthOut(int eye) => null;
    /// <summary>After every eye was presented (and its hands drawn), before the frame is submitted: whatever the
    /// target adds over all the eyes (the headset's mirror window, its menus), and its images made ready to show.</summary>
    void RecordFinish(CommandBuffer cmd) { }

    // A split frame (the headset's overlap, docs/vr.md > Overlap): the scene is recorded before the target is ready for
    // it (its last frame may still be on the GPU), with RecordDepth, DepthOut, RecordPresent and Hands going into the
    // target's own images; the scene's commands are submitted once the target says so (SubmitScene); then Acquire, and a
    // second command buffer gets what writes the target's images (RecordOutput, RecordFinish) before Present.

    /// <summary>Take this frame split (render thread, before anything is recorded; the renderer asks only when each eye
    /// finishes into an image of its own). False: the frame goes the usual way.</summary>
    bool TrySplit() => false;
    /// <summary>A split frame's scene commands are recorded: run <paramref name="submit"/> (which submits them) once the
    /// target's last frame is done with the queue, on this thread or the target's own; returns once it has run.</summary>
    void SubmitScene(Action submit) => submit();
    /// <summary>A split frame, after <see cref="Acquire"/>: copy what the scene left in the target's own images into the
    /// acquired ones (<see cref="RecordFinish"/> follows).</summary>
    void RecordOutput(CommandBuffer cmd) { }
}

/// <summary>CPU milliseconds of one frame's phases, and what it drew.</summary>
public readonly record struct FrameStats(double WaitMs, double SceneMs, double AvatarMs, double ProbeMs, double RecordMs, double TotalCpuMs,
    int Instances, int Draws, int Pending, int Captures, bool Presented);

/// <summary>
/// The real-time renderer: one camera, every frame, into a presentable target. Owns the GPU and the scenes of every
/// region it's given. Not thread-safe: call from one render thread, except AddRegion / RemoveRegion, which queue.
/// Each frame waits for its slot (two in flight), builds the view and its draw list, takes a budgeted share of
/// reflection probe captures, records shadows once and the scene and post-processing once per eye, then presents.
/// </summary>
public sealed partial class FrameRenderer : IDisposable
{
    readonly FrameRendererOptions _opt;
    readonly GpuContext _gpu;
    readonly RenderWorld _world;
    readonly Particles.ParticleBuilder _particles = new();
    readonly ProbeManager? _probes;
    readonly Overlay _overlay;
    LayerMirror? _layerMirror;
    LayerCut? _layerCut;
    readonly DrawList[] _drawLists;
    readonly ConcurrentQueue<RegionSource> _addRegions = new();
    readonly ConcurrentQueue<ulong> _removeRegions = new();
    readonly ConcurrentDictionary<ulong, RegionSource> _sources = new();
    int _slot;
    float _centerAspect = 1;
    // the draw lists' parts and the threads that build them with this one (docs/performance.md > Parallel draw lists);
    // SOJOURN_SCENE_THREADS=N helpers (0: this thread alone)
    readonly DrawPlan _plan = new();
    readonly SceneWorkers? _workers = int.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_SCENE_THREADS"), out int st)
        ? (st > 0 ? new SceneWorkers(st) : null) : new SceneWorkers(3);
    long _lastEffects, _lastHousekeeping, _nextRefreshCapture, _lastAssetReport, _reportedBytes;
    string? _lastAssetLine;
    int _lastObjects, _lastCulled, _lastDraws;
    double _captureMs = 6;
    readonly AllocProfile? _allocs;

    public string DeviceName => _gpu.Vk.DeviceName;
    /// <summary>Drawn on the processor (Gpu/SoftwareVulkan.cs).</summary>
    public bool Software => _gpu.Vk.Software;
    /// <summary>Quality settings that apply from the next frame.</summary>
    public RenderQuality Quality { get; }
    ViewOptions View => Math.Abs(_view.LodFactor - Quality.LodFactor) < 1e-4f && _view.MinPixels == Budget.Pixels && _view.MinShadowPixels == Budget.ShadowPixels
        ? _view : _view = _view with { LodFactor = Quality.LodFactor, MinPixels = Budget.Pixels, MinShadowPixels = Budget.ShadowPixels };
    /// <summary>Probe captures are 256-pixel faces: what's under a pixel there neither shows nor shades.</summary>
    ViewOptions CaptureView
    {
        get
        {
            var v = View;
            if (!ReferenceEquals(v, _captureFor)) (_captureFor, _captureView) = (v, v with { MinPixels = Math.Max(1f, v.MinPixels), MinShadowPixels = Math.Max(2f, v.MinShadowPixels) });
            return _captureView!;
        }
    }
    ViewOptions? _captureFor, _captureView;
    /// <summary>The crowd settings: how many avatars are drawn whole, the complexity limit, the person's choices
    /// (World/Avatars.Crowd.cs).</summary>
    public AvatarLimits AvatarLimits => _world.Avatars.Limits;
    /// <summary>Keeps the scene phase near its budget by dropping what's tiny on screen (Realtime/SceneBudget.cs).</summary>
    public SceneBudget Budget { get; } = new();
    /// <summary>Loading held to the frame's time and video memory (Realtime/LoadGovernor.cs).</summary>
    public LoadGovernor Governor => _world.Governor;
    long _memAt;
    ViewOptions _view;
    public VulkanContext Vk => _gpu.Vk;
    public PassProfiler Profiler => _gpu.Renderer.Profiler;
    /// <summary>The headset's menus drawn into its mirror window (Realtime/LayerMirror.cs), made on first use. Render
    /// thread.</summary>
    public LayerMirror LayerMirror => _layerMirror ??= new LayerMirror(_gpu.Vk, _gpu.Shaders)
    {
        Mark = name => _gpu.Renderer.Profiler.Mark(name), Log = _opt.Log,
    };
    /// <summary>The headset menus' parts cleared (Realtime/LayerCut.cs), made on first use. Render thread.</summary>
    public LayerCut LayerCut => _layerCut ??= new LayerCut(_gpu.Vk, _gpu.Shaders);
    public int FramesInFlight => _gpu.Frames.Length;
    /// <summary>Where avatars are drawn between updates. Begin a frame on it before anything (the camera) asks.</summary>
    public MotionSmoother Motion => _world.Motion;
    /// <summary>The own avatar's eye height above its feet at its own size, standing (from the last frame that drew a <see cref="SelfBody"/>).</summary>
    public float? SelfEyeHeight { get { CheckWorld(); return _world.Avatars.SelfEyeHeight; } }

    /// <summary>The avatars whose name tags the last frame placed (FrameRequest.ShowNames), in its region's frame: where
    /// the client's tags go (Realtime/Overlay.cs OverlayTag).</summary>
    public (ulong Region, NameTag[] Tags) Heads { get { CheckWorld(); return _heads; } private set => _heads = value; }
    (ulong Region, NameTag[] Tags) _heads = (0, Array.Empty<NameTag>());

    /// <summary>The latest small sample of the world behind the overlay (OverlayScene.SampleBackdrop; Overlay.Backdrop.cs),
    /// from any thread: published whole once its frame is done.</summary>
    public BackdropSample? Backdrop => _overlay.Sampled;

    /// <summary>Called on the render thread once a frame is done with the world (its scene, probe plans and capture
    /// lists built), before it's recorded and presented: the client's FramePipe hands the world to the main thread then
    /// (Sojourn.Client/FramePipe.cs).</summary>
    public Action? WorldReleased { get; set; }
    /// <summary>Whether the calling thread owns the world now (the client's FramePipe). What reads the world from outside
    /// (picks, where things are drawn) checks it and logs each offender once, with its stack.</summary>
    public Func<bool>? OwnsWorld { get; set; }
    readonly ConcurrentDictionary<string, bool> _offences = new();

    void CheckWorld([System.Runtime.CompilerServices.CallerMemberName] string who = "")
    {
        if (OwnsWorld is { } owns && !owns() && _offences.TryAdd(who, true))
            _opt.Log?.Invoke($"the world read off its owner's turn: {who} on thread {System.Environment.CurrentManagedThreadId} " +
                             $"({Thread.CurrentThread.Name ?? "unnamed"}){System.Environment.NewLine}{System.Environment.StackTrace}");
    }

    public FrameRenderer(FrameRendererOptions options)
    {
        _opt = options;
        _allocs = AllocProfile.Start(options.Log);
        Quality = new RenderQuality
        {
            Shadows = options.Shadows, ShadowCap = options.ShadowResolutionCap, ProjectorShadows = options.ProjectorShadows,
            Reflections = options.Probes, LodFactor = options.View.LodFactor,
        };
        _view = options.View;
        var shaderDir = _opt.ShaderDirectory ?? Path.Combine(AppContext.BaseDirectory, "Shaders");
        _gpu = new GpuContext(shaderDir, _opt.CacheDirectory, _opt.Vulkan with { Log = _opt.Vulkan.Log ?? _opt.Log }, framesInFlight: 2, _opt.GeometryArenaScale);
        _drawLists = _gpu.Frames.Select(_ => new DrawList()).ToArray();
        _world = new RenderWorld(_gpu, _opt.WorkerThreads) { Log = _opt.Log, ResolveThreads = _workers?.Workers ?? 1, Quality = Quality };
        _world.Media.Log = _opt.Log;
        _particles.Settings = _opt.Particles;
        _overlay = new Overlay(_gpu.Vk, _gpu.Shaders, _gpu.Frames.Length);
        _overlay.Mark = name => _gpu.Renderer.Profiler.Mark(name);
        _gpu.Renderer.Log = _opt.Log;
        _world.Textures.Log = _opt.Log;
        // on the processor: textures at three sizes, none past 512 (TextureStore.FewSizes); SOJOURN_SOFTWARE_TEXTURES=as-is
        // leaves them as on a GPU, for comparing
        if (_gpu.Vk.Software && System.Environment.GetEnvironmentVariable("SOJOURN_SOFTWARE_TEXTURES") != "as-is")
            (_world.Textures.FewSizes, _world.Textures.MaxDimension) = (true, 512);
        _gpu.Renderer.Profiler.FrameDone = ms => _world.Governor.Gpu(ms);
        var (vramUsed, vramBudget) = _gpu.Vk.DeviceLocalBudget();
        _opt.Log?.Invoke($"video memory: {_gpu.Vk.DeviceLocalHeapBytes >> 20} MB, this process budgeted {vramBudget >> 20} MB" +
                         (_gpu.Vk.Software ? " (drawn on the processor: the PC's own memory, our allocations counted)"
                          : _gpu.Vk.HasMemoryBudget ? "" : " (no VK_EXT_memory_budget: 85% of the heap assumed)") +
                         $", using {vramUsed >> 20} MB" + (LoadGovernor.TestBudget > 0 ? $"; testing with a budget of {LoadGovernor.TestBudget >> 20} MB" : "") +
                         $"; {_gpu.Bindless.MaxTextures} texture slots, {Bindless.MaxCubes} cube slots");
        _world.Geometry.Log = _opt.Log;
        _world.Geometry.MeshBytesBudget = _opt.MeshBytesBudget;
        _world.Materials.Log = _opt.Log;
        if (_opt.Probes)
        {
            _probes = new ProbeManager(new ProbeSystem(_gpu.Vk, _gpu.Bindless, _gpu.Shaders, _gpu.Renderer), _world)
            {
                Level = _opt.ProbeLevel, MaxResident = Math.Clamp(_opt.ProbeCacheCount, 32, (int)(Bindless.MaxCubes - 16) / 2),
                ProbeParams = h => _sources.TryGetValue(h, out var src) ? src.ReflectionProbes : null,
            };
            _probes.RetireMaps = maps => _world.RetireLater(maps.Dispose);
        }
    }

    public void AddRegion(RegionSource source) { _sources[source.Handle] = source; _addRegions.Enqueue(source); }
    public void RemoveRegion(ulong handle) { _sources.TryRemove(handle, out _); _removeRegions.Enqueue(handle); }
    public bool HasRegion(ulong handle) => _sources.ContainsKey(handle);

    /// <summary>Region changes and finished uploads, without drawing (e.g. while minimised).</summary>
    public void Tick()
    {
        CheckWorld();
        _world.Textures.DataOnly = !Quality.Textures;
        ApplyRegionChanges();
        // between frames (the renderer out of sight on the processor draws only pictures, an hour apart at times) a
        // region drawn before is kept current as a frame keeps it, else every update it sent waited in its change
        // queue until the next picture (a ride through a busy place: without bound); one never drawn isn't indexed
        // (its first frame reads it whole), so what it reports is let go
        foreach (var r in _world.Regions)
        {
            if (!r.Synced) { if (r.Source.Changes is { IsEmpty: false } q) q.Clear(); }   // (Clear makes a new segment each time)
            else if (r.Source.Changes != null) r.Sync(terrain: false);
        }
        _world.BeginFrame();
        Periodic();
        // what BeginFrame staged (uploads, a shrunk texture's copy) goes to the GPU now: EndFrame counts this as a frame
        // and frees what was retired three frames ago, so a copy left waiting for the next drawn frame could name an
        // image already destroyed (lavapipe crashed on it, 2026-10-31; between the out-of-sight renderer's pictures
        // only ticks run, for minutes)
        if (_gpu.Uploader.HasPending) _gpu.Uploader.FlushNow();
        _world.EndFrame();
    }

    void ApplyRegionChanges()
    {
        // removes first: a region dropped and re-joined between two frames must end up present
        while (_removeRegions.TryDequeue(out var h)) { _world.RemoveRegion(h); _probes?.RemoveRegion(h); }
        while (_addRegions.TryDequeue(out var r)) _world.AddRegion(r);
    }

    void Periodic()
    {
        long now = Stopwatch.GetTimestamp();
        // particle systems and texture animations are timestamped as they change
        if (Stopwatch.GetElapsedTime(_lastEffects, now).TotalMilliseconds > 200)
        {
            _lastEffects = now;
            double t = Particles.EffectClock.Now;
            // (a region not drawn yet isn't followed: its first frame starts it, and until then each pass was a scan of
            // all its objects)
            foreach (var r in _world.Regions) if (r.Synced) r.Effects.Observe(r.Source.Handle, r.Source.Prims, t);
            foreach (var e in _world.Environments) e.RefreshIfStale();
        }
        if (Stopwatch.GetElapsedTime(_lastHousekeeping, now).TotalSeconds > 2)
        {
            _lastHousekeeping = now;
            _world.Housekeeping();
        }
        if (Stopwatch.GetElapsedTime(_lastAssetReport, now).TotalSeconds > 10)
        {
            _lastAssetReport = now;
            var t = _world.Textures;
            var g = _world.Geometry;
            long bytes = Interlocked.Read(ref t.FetchedBytes);
            string line = $"view: {_lastObjects} objects drawn, {_lastCulled} culled, {_lastDraws} draws{_lastContent}{_world.Avatars.Describe()}{Budget.Describe()}{_phases.Line}; assets: textures {t.Fetching} fetching, {t.Waiting} waiting, {t.Count} known, {Interlocked.Read(ref t.Fetches)} fetches " +
                          $"({(bytes - _reportedBytes) / 10.0 / 1024:F0} KB/s), {Interlocked.Read(ref t.Failures)} failed, {Interlocked.Read(ref t.Decodes)} decodes, {t.ResidentBytes >> 20} MB resident, {t.PendingBytes >> 20} MB decoded waiting; " +
                          $"meshes {g.MeshesFetching} fetching, {g.MeshesWaiting} waiting, {g.Building} building{g.Describe()}; " +
                          $"gc {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, paused {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms, {GC.GetTotalAllocatedBytes() >> 20} MB allocated" +
                          (_gpu.Renderer.TakeExposureLine() is { } exposure ? "; " + exposure : "");
            _reportedBytes = bytes;
            _phases = default;
            if (line != _lastAssetLine) _opt.Log?.Invoke(line);
            _lastAssetLine = line;
            static string Gb(ulong b) => $"{b / (double)(1UL << 30):F1}";
            var alloc = _gpu.Vk.Allocator;
            _opt.Log?.Invoke(Governor.TakeLine($"textures {Gb(t.ResidentBytes)}, geometry {Gb(_gpu.GeometryPool.Bytes)}, targets {Gb(_gpu.TargetBytes)}, " +
                $"probes {Gb(_probes?.Bytes ?? 0)}, ours in all {Gb(alloc.TotalUsed)} of {Gb(alloc.TotalAllocated)} allocated"));
        }
    }

    /// <summary>Feed the load governor a frame: its render-thread CPU, and four times a second video memory.</summary>
    void Govern(FrameRequest req, double cpuMs)
    {
        var g = Governor;
        if (g.Enabled != Quality.LoadGovernor) _opt.Log?.Invoke($"load governor {(Quality.LoadGovernor ? "on" : "off (video memory is still governed)")}");
        g.Enabled = Quality.LoadGovernor;
        var t = _world.Textures;
        // what the store did since the last frame (render thread: shrinks, uploads dropped, evictions)
        g.Shrunk += t.Shrunk; g.ShrunkBytes += t.ShrunkBytes; g.Evicted += t.Evictions; g.HeldUploads += t.DroppedUploads;
        t.Shrunk = t.ShrunkBytes = t.Evictions = t.DroppedUploads = 0;
        g.IdleEvicted += t.IdleEvictions; g.IdleBytes += t.IdleBytes; g.SlotEvicted += t.SlotEvictions; g.SlotWaits += t.SlotWaits;
        t.IdleEvictions = t.IdleBytes = t.SlotEvictions = t.SlotWaits = 0;
        g.Slots(_gpu.Bindless.TexturesUsed, (int)_gpu.Bindless.MaxTextures);
        double period = double.IsFinite(Profiler.OverMs) ? Profiler.OverMs : 1000.0 / 60;
        g.Frame(cpuMs, period, req.DrawDistance);
        var lim = _world.Avatars.Limits;
        (lim.CapturesPerFrame, lim.RefreshScale, lim.ImpostorBeyond) = (g.ImpostorCaptures, g.ImpostorRefresh, g.ImpostorBeyond);
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_memAt, now).TotalMilliseconds < 250) return;
        _memAt = now;
        var (used, budget) = _gpu.Vk.DeviceLocalBudget();
        // on the processor our allocations are the PC's own memory, budgeted at 1 GB, and what's meant is textures shed
        // from 512 MB: so they get half of it beyond everything else allocated (geometry arenas, targets, probes). Judged
        // against the whole, the rest alone (0.4-0.5 GB at the blind tester's club) kept every texture held and shrunk at
        // the top pressure with almost none resident, pictures blurred, sharper copies dropped (her log, 2026-11-01)
        if (Software) budget = Math.Max(budget, used - Math.Min(used, TextureBytes) + budget / 2 + (1UL << 30));
        var alloc = _gpu.Vk.Allocator;
        g.Memory(used, budget, _gpu.Vk.DeviceLocalHeapBytes, alloc.TotalAllocated > alloc.TotalUsed ? alloc.TotalAllocated - alloc.TotalUsed : 0);
    }

    /// <summary>Render and present one frame. Returns CPU timings; GPU pass times are in <see cref="Profiler"/>.</summary>
    public FrameStats RenderFrame(FrameRequest req, IFrameTarget target)
    {
        long t0 = Stopwatch.GetTimestamp();
        int slot = _slot;
        var fr = _gpu.Frames[slot];
        fr.Wait();   // the frame two back is done: its arena, draw list and staging are free again
        double waitMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        CheckWorld();
        _world.Textures.DataOnly = !Quality.Textures;
        ApplyRegionChanges();
        _world.BeginFrame();
        _world.Avatars.Poll();
        Periodic();
        double stillMs = 0;
        try
        {
            var region = _world.Region(req.RegionHandle);
            if (region == null || !region.Source.HasSimulator)
            {
                // nothing to draw yet (logging in, crossing): still present something so the window stays alive, with
                // what's over it if anything is (the login sheet, the pointer: FrameRenderer.Void.cs)
                return req.Overlay != null ? Void(req, fr, slot, target, waitMs, t0) : Clear(fr, slot, target, waitMs, t0);
            }
            stillMs = StepStills(req, region);   // pictures from cameras of their own, first (FrameRenderer.Still.cs)

            FakeDeviceLoss();
            Renderer.AbStep();
            long tScene = Stopwatch.GetTimestamp();
            var center = BuildView(req, region);
            var eyes = BuildEyes(req, center);
            var cull = CullFrustum(req, center, eyes);
            // a mirror in view: culled for what it can show as well (FrameRenderer.Mirror.cs)
            _mirror = PlanMirror(center, region, cull, ref _hero, person: true);
            if (_mirror != null) cull = Frustum.Either(cull, _mirror.Portal);
            // the handheld camera's preview: culled for what it sees as well (FrameRenderer.Preview.cs), and a mirror in
            // its view for what that shows
            var preview = PlanPreview(req, center, fr);
            MirrorFrame? previewMirror = null;
            if (preview != null)
            {
                var seen = new Frustum(preview.View.View * preview.View.Proj);
                cull = Frustum.Either(cull, seen);
                previewMirror = PlanMirror(preview.View, region, seen, ref _previewHero, person: false);
                if (previewMirror != null) cull = Frustum.Either(cull, previewMirror.Portal);
            }
            else _previewHero = default;
            // avatars in mirrors as the eyes' has them (or the preview's, when the eyes have none)
            var avatarsBy = _mirror ?? previewMirror;
            _world.Avatars.Mirror = avatarsBy == null ? MirrorAvatars.None : avatarsBy.Dynamic ? MirrorAvatars.Shown : MirrorAvatars.Hidden;
            var dl = _drawLists[slot];
            dl.Clear();
            int pending = BuildDrawList(center, region, dl, req, cull, out double avatarMs);
            dl.PropertyLineVertices = _world.ParcelLines.Emit(_world, req.RegionHandle, center.Position, dl.ParcelLines, RegionOffset);   // World/ParcelLines.cs
            _world.Beacons.Emit(_world, req.RegionHandle, center, cull, dl, RegionOffset);   // World/Beacons.cs
            if (req.ShowNames) Heads = (req.RegionHandle, dl.NameTags.ToArray());
            var hud = PlanHud(req, slot);
            ShareShadows(center, eyes);
            if (_mirror != null) FinishMirror(_mirror, center, eyes);
            var moreViews = _mirror?.Views.Where(m => m != null).Select(m => m!.View).ToArray() ?? Array.Empty<ViewSetup>();
            if (preview != null)
            {
                ShareShadows(center, [preview.View]);
                if (previewMirror != null)
                {
                    FinishMirror(previewMirror, center, [preview.View], Renderer.PreviewMirror, preview.Targets);
                    preview.Mirror = previewMirror.Views[0];
                }
                moreViews = [.. moreViews, preview.View];   // (the views beside the eyes: their probe lists)
                if (preview.Mirror is { } pm) moreViews = [.. moreViews, pm.View];
            }
            double sceneMs = Stopwatch.GetElapsedTime(tScene).TotalMilliseconds;
            Budget.Frame(sceneMs);
            if (req.PlanOnly)
            {
                WorldReleased?.Invoke();
                _gpu.Uploader.FlushNow();   // what this frame's loading staged, uploaded now (no frame records it)
                return new FrameStats(waitMs, sceneMs, avatarMs, 0, 0, Stopwatch.GetElapsedTime(t0).TotalMilliseconds,
                    dl.Instances.Count, dl.TotalDraws, pending, 0, false);
            }

            // reflection probes: plan for the centre view, capture inline before the scene: whole captures (the
            // default probe, or every capture when slicing is off), or a few faces of the one capture in progress
            long tProbe = Stopwatch.GetTimestamp();
            ProbeList? probes = null;
            var captures = new List<ProbeCapture>();
            bool slicing = _opt.ProbeFacesPerFrame is > 0 and < 12;
            if (_probes != null && Quality.Reflections)
            {
                probes = _probes.Plan(center, region, SkyKey(center), blocking: false);
                if (probes.Default == null && probes.DefaultEntry != null && _slice == null && _probes.TakeNow(probes.DefaultEntry, center) is { } dc)
                {
                    captures.Add(dc);
                    probes.Default = dc.Entry;
                }
                else if (_slice == null && (probes.Missing > 0 || Stopwatch.GetTimestamp() >= _nextRefreshCapture))
                {
                    int max = slicing ? 1 : probes.Missing > 0 ? _opt.ProbeCapturesPerFrame : 1;
                    if (probes.Missing == 0) _nextRefreshCapture = Stopwatch.GetTimestamp() + (long)(_opt.ProbeRefreshInterval.TotalSeconds * Governor.ProbeRefreshScale * Stopwatch.Frequency);
                    if (_probes.HasWork(false)) captures.AddRange(_probes.TakeBatch(max, blockingOnly: false));
                }
            }
            // after the plan, whose probe scan kills the sites of objects that went away and releases their maps: the
            // capture in progress is dropped here if its probe died, before anything of it is recorded this frame
            if (_slice != null && !SliceAlive(_slice.Capture)) { _probes!.Abandon(_slice.Capture); _slice = null; }
            if (captures.Count > 0 || _slice != null) EnsureCaptureShadowMaps(center);
            var lists = BuildCaptures(captures, c => c != null && c.Entry != probes?.DefaultEntry && _probes!.Defer(c, false));
            if (slicing && captures.Count == 1 && captures[0].Entry != probes?.DefaultEntry)
            {
                _slice = new SlicedCapture(captures[0], lists[0]);   // starts this frame
                captures.Clear();
                lists.Clear();
            }
            // fresh draws halfway through (a texture slot retired at the start is only reused three frames on), rather
            // than every frame: a capture's draw list covers everything around it and cost several ms a time
            else if (_slice != null && _slice.Step == 6) _slice.Lists = BuildCaptures(new List<ProbeCapture> { _slice.Capture })[0];
            var slice = _slice;
            int sliceTo = slice == null ? 0 : Math.Min(12, slice.Step + Governor.ProbeFaces(_opt.ProbeFacesPerFrame));
            var arenaCaptures = slice == null ? captures : captures.Append(slice.Capture).ToList();
            var arenaLists = slice == null ? lists : lists.Append(slice.Lists).ToList();
            double probeMs = Stopwatch.GetElapsedTime(tProbe).TotalMilliseconds;

            // from here on only this frame's own draw lists and the GPU's resources: the world goes back to the main thread
            WorldReleased?.Invoke();
            // two eyes of one size: a stereo set, both drawn at once where the passes draw geometry (multiview)
            var targets = eyes.Length == 2 && eyes[0].Width == eyes[1].Width && eyes[0].Height == eyes[1].Height && Renderer.Multiview
                ? _gpu.Targets(eyes[0].Width, eyes[0].Height, 2).Eyes.Select((t, i) => new Renderer.Eye(eyes[i], t)).ToArray()
                : eyes.Select(e => new Renderer.Eye(e, _gpu.Targets(e.Width, e.Height))).ToArray();
            // split (the headset's overlap): the scene recorded and submitted while the target's last frame may still be
            // on the GPU, its images written after; only where each eye's output survives the next eye's turn
            bool split = targets.Length == 2 && targets[0].Targets.Output.Handle.Handle != targets[1].Targets.Output.Handle.Handle && target.TrySplit();
            Semaphore ready = default, done = default;
            if (!split && !target.Acquire(slot, out ready, out done))
            {
                foreach (var c in captures) _probes!.Defer(c, false);
                return new FrameStats(waitMs, sceneMs, avatarMs, probeMs, 0, Stopwatch.GetElapsedTime(t0).TotalMilliseconds, dl.Instances.Count, dl.TotalDraws, pending, 0, false);
            }

            long tRecord = Stopwatch.GetTimestamp();
            fr.BeginCommands();
            if (center.Sky.shadows_on != 0) _gpu.Renderer.EnsureShadowMaps(center.ShadowMapWidth, center.ShadowMapHeight);
            if (center.Sky.shadow_far.W > 0) _gpu.Renderer.EnsureFarShadowMap((uint)center.Sky.shadow_far_res.X);
            if (center.Sky.spot_shadows != 0) _gpu.Renderer.EnsureSpotShadowMaps(center.SpotShadowMapSize);
            var mainList = probes != null ? ProbeManager.Build(probes, center.SkyU.ReflectionProbeAmbiance, false, center.Position + new Vector3(0, 0, ProbeManager.DefaultHeight),
                into: _mainProbes ??= new BuiltProbeList { Probes = new() }) : null;
            if (probes == null) foreach (var e in eyes.Concat(moreViews)) e.Sky.probes_on = 0;
            _gpu.Renderer.BeginFrame(fr);
            (_gpu.Renderer.AlphaLayers, _gpu.Renderer.SimpleAlpha, _gpu.Renderer.HalfLight) = (Quality.AlphaLayers, Quality.SimpleAlpha, Quality.HalfSsao);
            ulong mark = _gpu.Renderer.Record(fr, targets, dl, _gpu.Uploader,
                prepareViews: arena =>
                {
                    PrepareCaptures(arena, arenaCaptures, arenaLists);
                    if (mainList != null) foreach (var e in eyes.Concat(moreViews)) mainList.Write(arena, ref e.Sky);
                    if (hud != null) PrepareHud(arena, hud, eyes);
                    return 0;
                },
                beforeScene: c =>
                {
                    RecordCaptures(c, captures);
                    if (slice != null) RecordSlice(c, slice, sliceTo);
                },
                extraArenaBytes: CaptureArenaBytes(arenaCaptures, arenaLists) + (mainList?.Bytes ?? 0) * (ulong)(eyes.Length + moreViews.Length) + (hud?.Bytes ?? 0) + 128 * (ulong)eyes.Length,
                present: (cmd, eye, img) =>
                {
                    // the assistant's snapshots see the world without the overlay; screenshots show the screen
                    if (eye == 0 && req.CaptureTo is { } to)
                        RecordCapture(fr, cmd, img, 0, eyes.Length, null, to);
                    if (eye == 0 && req.VideoTo is { } video) RecordVideo(fr, cmd, img, video);
                    _gpu.Renderer.RecordParcelLines(cmd, img, targets[eye].Targets.Depth);   // under the HUDs and menus, out of pictures
                    _gpu.Renderer.RecordBeacons(cmd, img, targets[eye].Targets.Depth);   // (so are beacons and highlights)
                    if (hud != null) RecordHud(fr, cmd, img, eye, hud);
                    if (req.Overlay is { } overlay) _overlay.Record(fr, slot, cmd, img, eye, eyes[eye], overlay, dl.NameTags, targets[eye].Targets.Depth, center.Position, targets[eye].Targets.Normal,
                        preview?.Targets.Output);
                    target.RecordDepth(cmd, eye, targets[eye].Targets.Depth);
                    if (req.Overlay != null && target.DepthOut(eye) is { } walls) _overlay.RecordWalls(cmd, slot, eye, walls);
                    if (eye == 0 && req.SeenTo is { } seen) RecordSeen(fr, cmd, img, seen);
                    if (req.CapturePath is { } path)
                        RecordCapture(fr, cmd, img, eye, eyes.Length, eye == 0 ? path : Path.ChangeExtension(path, null) + $"-eye{eye}.png");
                    target.RecordPresent(cmd, eye, eyes.Length, img);
                    // the hands over the headset's menus (Overlay.Hands.cs): a layer of their own above them
                    if (req.Overlay?.Cut != null && _overlay.HasHands && target.Hands(cmd, eye) is { } hands) _overlay.RecordHands(cmd, slot, eye, img, hands);
                },
                mirrors: _mirror?.Views, preview: preview);
            if (!split) target.RecordFinish(fr.Cmd);
            _probes?.System.RetireViews(fr);
            foreach (var c in captures) _probes!.Completed(c);
            if (slice != null)
            {
                slice.Step = sliceTo;
                if (sliceTo == 12) { _probes!.Completed(slice.Capture); _slice = null; }
            }
            bool shown = true;
            if (split)
            {
                // the scene goes to the queue once the target's last frame is done with it (its images back, the frame
                // ended); then the target's images, in a second buffer that carries the slot's fence
                Profiler.Pause();
                long tSubmit = Stopwatch.GetTimestamp();
                target.SubmitScene(() => Submit(fr, fr.Cmd, default, default, fence: false));
                waitMs += Stopwatch.GetElapsedTime(tSubmit).TotalMilliseconds;   // (waiting on the GPU, as the slot's fence is)
                var output = fr.BeginOutput();
                shown = target.Acquire(slot, out ready, out done);
                Profiler.Resume(output);
                if (shown)
                {
                    target.RecordOutput(output);
                    target.RecordFinish(output);
                }
                Profiler.Mark("output");
                Submit(fr, output, ready, done);
            }
            else Submit(fr, fr.Cmd, ready, done);
            var up = _gpu.Uploader;
            fr.Retire.Add(() => up.Release(mark));
            double recordMs = Stopwatch.GetElapsedTime(tRecord).TotalMilliseconds;
            if (shown) target.Present();
            _slot = (slot + 1) % _gpu.Frames.Length;
            return new FrameStats(waitMs, sceneMs, avatarMs, probeMs, recordMs, Stopwatch.GetElapsedTime(t0).TotalMilliseconds,
                dl.Instances.Count, dl.TotalDraws, pending, captures.Count + (slice != null ? 1 : 0), shown);
        }
        finally
        {
            _world.EndFrame();
            Govern(req, Stopwatch.GetElapsedTime(t0).TotalMilliseconds - waitMs - stillMs);
        }
    }

    // SOJOURN_FAKE_DEVICE_LOST=S: the GPU "lost" S seconds into the first region drawn, the exception a real loss
    // throws (vkWaitForFences), for checking the client's recovery (Sojourn.Client/DeviceLoss.cs) without resetting
    // the PC's display driver
    static readonly double FakeLossAfter = double.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_FAKE_DEVICE_LOST"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : -1;
    long _firstDrawn;

    void FakeDeviceLoss()
    {
        if (FakeLossAfter < 0) return;
        if (_firstDrawn == 0) _firstDrawn = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_firstDrawn).TotalSeconds >= FakeLossAfter)
            throw new VulkanException("vkWaitForFences (simulated by SOJOURN_FAKE_DEVICE_LOST)", Result.ErrorDeviceLost);
    }

    FrameStats Clear(FrameResources fr, int slot, IFrameTarget target, double waitMs, long t0)
    {
        WorldReleased?.Invoke();
        if (!target.Acquire(slot, out var ready, out var done)) return new FrameStats(waitMs, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);
        fr.BeginCommands();
        var rt = _gpu.Targets(64, 64);
        rt.Output.Transition(fr.Cmd, ImageLayout.TransferDstOptimal);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        var color = new ClearColorValue(0.05f, 0.06f, 0.08f, 1f);
        _gpu.Vk.Api.CmdClearColorImage(fr.Cmd, rt.Output.Handle, ImageLayout.TransferDstOptimal, in color, 1, in range);
        rt.Output.Transition(fr.Cmd, ImageLayout.TransferSrcOptimal);
        target.RecordPresent(fr.Cmd, 0, 1, rt.Output);
        target.RecordFinish(fr.Cmd);
        ulong mark = _gpu.Uploader.Record(fr.Cmd);
        Submit(fr, fr.Cmd, ready, done);
        var up = _gpu.Uploader;
        fr.Retire.Add(() => up.Release(mark));
        target.Present();
        _slot = (slot + 1) % _gpu.Frames.Length;
        return new FrameStats(waitMs, 0, 0, 0, 0, Stopwatch.GetElapsedTime(t0).TotalMilliseconds, 0, 0, 0, 0, true);
    }

    /// <summary>Copy an eye's finished image (TransferSrcOptimal) to the frame's readback buffer; once the frame is
    /// done, encode it to a PNG off the render thread.</summary>
    unsafe void RecordCapture(FrameResources fr, CommandBuffer cmd, GpuImage img, int eye, int eyeCount, string? path, Action<byte[], int, int>? to = null)
    {
        var api = _gpu.Vk.Api;
        ulong bytes = (ulong)img.Width * img.Height * 4;
        // a handed-over capture takes the slot after the eyes' file captures, so both can happen in one frame
        ulong offset = bytes * (ulong)(to != null ? eyeCount : eye);
        var rb = fr.EnsureReadback(bytes * (ulong)(eyeCount + 1));
        var copy = new BufferImageCopy
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(img.Width, img.Height, 1),
        };
        api.CmdCopyImageToBuffer(cmd, img.Handle, ImageLayout.TransferSrcOptimal, rb.Handle, 1, in copy);
        var hb = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit, DstAccessMask = AccessFlags2.HostReadBit,
        };
        var di = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &hb };
        api.CmdPipelineBarrier2(cmd, in di);
        int w = (int)img.Width, h = (int)img.Height;
        fr.Retire.Add(() =>
        {
            var pixels = new byte[w * h * 4];
            new ReadOnlySpan<byte>((void*)(rb.Alloc.Mapped + (nint)offset), pixels.Length).CopyTo(pixels);
            if (to != null)
            {
                _ = Task.Run(() => to(pixels, w, h));
                return;
            }
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path!))!);
                    File.WriteAllBytes(path!, ImageCodec.EncodePng(pixels, w, h));
                }
                catch (Exception e) { _opt.Log?.Invoke($"capture {path}: {e.Message}"); }
            });
        });
    }

    /// <summary>End and submit one of the frame's buffers; with the fence (the frame's last submission), the slot is in
    /// flight until it signals, which covers everything submitted before it too.</summary>
    unsafe void Submit(FrameResources fr, CommandBuffer cmd, Semaphore ready, Semaphore done, bool fence = true)
    {
        var api = _gpu.Vk.Api;
        VulkanContext.Check(api.EndCommandBuffer(cmd), "vkEndCommandBuffer");
        var waitStage = PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit;
        var si = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1, PCommandBuffers = &cmd,
            WaitSemaphoreCount = ready.Handle != 0 ? 1u : 0u, PWaitSemaphores = &ready, PWaitDstStageMask = &waitStage,
            SignalSemaphoreCount = done.Handle != 0 ? 1u : 0u, PSignalSemaphores = &done,
        };
        var f = fence ? fr.Fence : default;
        if (fence) VulkanContext.Check(api.ResetFences(_gpu.Vk.Device, 1, in f), "vkResetFences");
        lock (_gpu.Vk.QueueLock) VulkanContext.Check(api.QueueSubmit(_gpu.Vk.Queue, 1, in si, f), "vkQueueSubmit");
        if (!fence) return;
        fr.InFlight = true;
        fr.SubmittedAt = Stopwatch.GetTimestamp();
    }

    /// <summary>Wait for the GPU (before tearing down a swapchain).</summary>
    public void WaitIdle()
    {
        foreach (var f in _gpu.Frames) f.Wait();
        _gpu.Vk.WaitIdle();
    }

    // --- views ------------------------------------------------------------------------------------

    // the person's fixed skies, parsed once (their LLSD was parsed every frame)
    readonly Dictionary<UUID, Sojourn.Render.Environment.SkySettings?> _fixedSkies = new();
    Sojourn.Render.Environment.SkySettings? FixedSky(UUID id) =>
        _fixedSkies.TryGetValue(id, out var sky) ? sky : _fixedSkies[id] = EnvironmentState.KnownSky(id);

    ViewSetup BuildView(FrameRequest req, RegionScene region)
    {
        var v = new ViewSetup
        {
            Width = (uint)Math.Clamp(req.Width, 16, 8192),
            Height = (uint)Math.Clamp(req.Height, 16, 8192),
            Position = req.Position,
            RegionHandle = req.RegionHandle,
            VFov = req.VerticalFov,
        };
        // the person's, or less while the governor brings it in from the far end: new objects within its cap, those
        // already drawn out to 6 m past it (no blinking at the edge)
        float cap = Governor.DrawDistance(req.DrawDistance);
        if (cap < req.DrawDistance) (v.DrawDistance, v.NewDistance) = (MathF.Min(req.DrawDistance, cap + 6f), cap);
        else v.DrawDistance = req.DrawDistance;
        var dir = req.Forward.LengthSquared() > 1e-8f ? Vector3.Normalize(req.Forward) : Vector3.UnitX;
        var up = req.Up;
        if (MathF.Abs(Vector3.Dot(dir, Vector3.Normalize(up))) > 0.999f) up = Vector3.UnitY;   // looking straight up/down
        v.View = ViewSetup.LookAt(req.Position, req.Position + dir, up);
        _centerAspect = (float)v.Width / v.Height;
        if (req.Eyes.Any(e => e.Asymmetric))
        {
            // headset eyes: a symmetric centre view wide and tall enough for both (shadow fitting, LOD, probes)
            var (h, vt) = (req.Eyes.Max(e => MathF.Max(-e.TanLeft, e.TanRight)), req.Eyes.Max(e => MathF.Max(e.TanUp, -e.TanDown)));
            v.VFov = 2 * MathF.Atan(vt);
            _centerAspect = h / vt;
            v.Near = ViewSetup.HeadsetNear;
            v.LodFov = Sojourn.Render.Geometry.VolumeLod.DefaultFov;
        }
        v.Proj = ViewSetup.ReverseZInfinite(v.VFov, _centerAspect, v.Near);
        v.WaterHeight = region.Source.Simulator.WaterHeight;

        var env = _world.Environment(region.Source.Handle);
        var time = req.Time ?? DateTime.UtcNow;
        v.EnvTime = time;
        v.LocalSky = req.LocalSky;
        var snap = EnvironmentFor(req, env, time, out v.LocalKey);   // FrameRenderer.Environment.cs
        var sky = SkyUniforms.Compute(snap, _opt.Graphics);
        var water = WaterUniforms.Compute(snap, req.Position.Z - v.WaterHeight, _opt.Graphics);
        v.SkyU = sky;
        v.SkyIrradianceU = SkyUniforms.Compute(snap, _opt.Graphics, SkyPass.ProbeIrradiance);
        LastEnvironment = new EnvironmentSeen(snap, sky, req.Position, req.RegionHandle, env?.Parcel ?? 0);
        v.WaterU = water;
        v.Sky = GpuSky.Build(sky, water, v.View, v.WaterHeight, req.Position);
        v.WaterNormalA = water.NormalMap;
        v.WaterNormalB = water.NormalMapNext;
        v.CloudNoiseA = sky.CloudNoiseTexture;
        v.CloudNoiseB = sky.CloudNoiseTextureNext;
        v.SunDirRegion = sky.DeferredSunDirection;
        v.MoonDirRegion = sky.DeferredMoonDirection;
        v.SunUp = sky.SunUpFactor == 1;
        v.Time = (float)SkyUniforms.AnimationClock((time - DateTime.UnixEpoch).TotalSeconds);
        return v;
    }

    /// <summary>Per-eye views: the centre view moved and turned by each eye's pose in its eye space, with the eye's
    /// own projection. One eye that is the camera is the centre view itself.</summary>
    static ViewSetup[] BuildEyes(FrameRequest req, ViewSetup center)
    {
        if (req.Eyes.Count == 1 && req.Eyes[0].IsCamera) return new[] { center };
        Matrix4x4.Invert(center.View, out var invView);
        var eyes = new ViewSetup[req.Eyes.Count];
        for (int i = 0; i < eyes.Length; i++)
        {
            var spec = req.Eyes[i];
            // eye space -> centre eye space (row vectors: rotate, then translate); the eye's view is the inverse
            var eyeToCenter = Matrix4x4.CreateFromQuaternion(spec.Rotation) * Matrix4x4.CreateTranslation(spec.Offset);
            Matrix4x4.Invert(eyeToCenter, out var centerToEye);
            var pos = Vector3.Transform(spec.Offset, invView);
            var e = center.CloneForEye();
            e.Position = pos;
            e.View = center.View * centerToEye;
            e.Proj = spec.Asymmetric
                ? ViewSetup.ReverseZInfinite(spec.TanLeft, spec.TanRight, spec.TanUp, spec.TanDown, e.Near)
                : center.Proj;
            e.Sky = GpuSky.Build(center.SkyU, center.WaterU, e.View, center.WaterHeight, pos);
            // the lens mask, except on frames that keep the eye's whole image for someone (pictures, videos); developers'
            // screenshots (CapturePath) show what the headset renders
            e.LensHidden = req.CaptureTo == null && req.VideoTo == null && req.SeenTo == null ? spec.Hidden : null;
            eyes[i] = e;
        }
        return eyes;
    }

    /// <summary>One frustum holding every eye's. Side-by-side desktop eyes: the outer eyes' side planes. Headset eyes:
    /// the centre camera with the eyes' widest tangents, its apex pulled back until it contains every eye's apex (exact
    /// enough for parallel eyes, as on the Quest; canted displays would need their tangents turned into the centre
    /// frame first).</summary>
    Frustum CullFrustum(FrameRequest req, ViewSetup center, ViewSetup[] eyes)
    {
        if (eyes.Length == 1 && !req.Eyes[0].Asymmetric) return new Frustum(eyes[0].View * eyes[0].Proj);
        if (!req.Eyes.Any(e => e.Asymmetric))
            return Frustum.Union(new Frustum(eyes[0].View * eyes[0].Proj), new Frustum(eyes[^1].View * eyes[^1].Proj));
        float l = req.Eyes.Min(e => e.TanLeft), r = req.Eyes.Max(e => e.TanRight);
        float u = req.Eyes.Max(e => e.TanUp), d = req.Eyes.Min(e => e.TanDown);
        float reach = MathF.Min(MathF.Min(-l, r), MathF.Min(u, -d));
        float back = reach > 0.05f ? req.Eyes.Max(e => e.Offset.Length()) / reach : 0;
        var view = center.View * Matrix4x4.CreateTranslation(0, 0, -back);   // eye space z is backward
        return new Frustum(view * ViewSetup.ReverseZInfinite(l, r, u, d, center.Near));
    }

    /// <summary>Shadow maps are fitted and rendered once, for the centre view; each eye samples them through its own
    /// eye-to-shadow matrices (M_eye = inv(View_eye) * View_centre * M_centre in row-vector order).</summary>
    static void ShareShadows(ViewSetup center, ViewSetup[] eyes)
    {
        foreach (var e in eyes)
        {
            if (ReferenceEquals(e, center)) continue;
            Matrix4x4.Invert(e.View, out var invEye);
            var toCenter = invEye * center.View;
            (e.FarReceivers2, e.FarReceivers3) = (center.FarReceivers2, center.FarReceivers3);
            ref var s = ref e.Sky;
            ref readonly var c = ref center.Sky;
            s.shadows_on = c.shadows_on;
            s.shadow_matrix0 = toCenter * c.shadow_matrix0;
            s.shadow_matrix1 = toCenter * c.shadow_matrix1;
            s.shadow_matrix2 = toCenter * c.shadow_matrix2;
            s.shadow_matrix3 = toCenter * c.shadow_matrix3;
            s.shadow_matrix4 = toCenter * c.shadow_matrix4;
            s.shadow_matrix5 = toCenter * c.shadow_matrix5;
            s.shadow_viewproj0 = c.shadow_viewproj0; s.shadow_viewproj1 = c.shadow_viewproj1;
            s.shadow_viewproj2 = c.shadow_viewproj2; s.shadow_viewproj3 = c.shadow_viewproj3;
            s.shadow_viewproj4 = c.shadow_viewproj4; s.shadow_viewproj5 = c.shadow_viewproj5;
            s.shadow_clip = c.shadow_clip;
            s.shadow_res = c.shadow_res;
            s.shadow_matrix6 = toCenter * c.shadow_matrix6;
            s.shadow_viewproj6 = c.shadow_viewproj6;
            s.shadow_far = c.shadow_far;
            s.shadow_far_res = c.shadow_far_res;
            (e.FarShadowKept, e.FarCasterTexels) = (center.FarShadowKept, center.FarCasterTexels);
            s.shadow_bias = c.shadow_bias;
            s.shadow_offset = c.shadow_offset;
            s.spot_shadows = c.spot_shadows;
            s.proj_shadow_res = c.proj_shadow_res;
            s.spot_shadow_bias = c.spot_shadow_bias;
            s.spot_shadow_offset = c.spot_shadow_offset;
            s.sun_tex_a = c.sun_tex_a; s.sun_tex_b = c.sun_tex_b; s.moon_tex = c.moon_tex; s.star_tex = c.star_tex;
            s.rainbow_tex = c.rainbow_tex; s.halo_tex = c.halo_tex;
            e.ShadowMapWidth = center.ShadowMapWidth; e.ShadowMapHeight = center.ShadowMapHeight; e.SpotShadowMapSize = center.SpotShadowMapSize;
            e.CloudNoiseSlotA = center.CloudNoiseSlotA; e.CloudNoiseSlotB = center.CloudNoiseSlotB;
            e.WaterNormalSlotA = center.WaterNormalSlotA; e.WaterNormalSlotB = center.WaterNormalSlotB;
        }
    }

    // --- draw list (one for every eye) --------------------------------------------------------------

    ShadowSettings _shadowSettings = ShadowSettings.Default;
    /// <summary>SOJOURN_FAR_MARGIN=F: the far cascades' margin over the setting (tests).</summary>
    static readonly float? EnvFarMargin = float.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_FAR_MARGIN"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : null;

    int BuildDrawList(ViewSetup v, RegionScene region, DrawList dl, FrameRequest req, Frustum frustum, out double avatarMs)
    {
        int pending = 0, objects = 0, culled = 0;
        long tPhase = Stopwatch.GetTimestamp();
        region.Sync();

        // neighbours in draw distance, offset by the difference of region origins; connected regions beyond it draw
        // terrain only (the viewer culls terrain and water by the frustum's sides alone)
        var scenes = new List<(RegionScene Scene, Vector3 Offset)> { (region, Vector3.Zero) };
        var farScenes = new List<(RegionScene Scene, Vector3 Offset)>();
        Utils.LongToUInts(region.Source.Handle, out uint rx, out uint ry);
        foreach (var other in _world.Regions)
        {
            if (other == region || !other.Source.HasSimulator) continue;
            Utils.LongToUInts(other.Source.Handle, out uint ox, out uint oy);
            var offset = new Vector3((float)((long)ox - rx), (float)((long)oy - ry), 0);
            var size = new Vector3(other.Source.Simulator.SizeX, other.Source.Simulator.SizeY, 0);
            var c = Vector3.Clamp(new Vector3(v.Position.X, v.Position.Y, 0), offset, offset + size);
            other.Sync();
            if (Vector2.Distance(new Vector2(c.X, c.Y), new Vector2(v.Position.X, v.Position.Y)) > v.DrawDistance) farScenes.Add((other, offset));
            else scenes.Add((other, offset));
        }

        _phases.Sync += Lap(ref tPhase);
        // sun (or moon) shadows: fit the cascades to what's in view, then cull casters per cascade
        ShadowFrustum?[]? casters = null;
        v.Sky.shadows_on = 0;
        v.Sky.shadow_far = default;
        bool lightUp = v.Sky.sun_up_factor == 1 ? v.Sky.sunlight_color.LengthSquared() > 0 : v.Sky.moonlight_color.LengthSquared() > 0;
        if (Quality.Shadows && lightUp)
        {
            var ext = _extents;
            ext.Clear();
            foreach (var (scene, offset) in scenes) scene.AddExtents(ext, v, offset);
            _world.Avatars.AddExtents(scenes, ext, v);
            var lightDir = v.SunUp ? v.SunDirRegion : v.MoonDirRegion;
            var (sw, sh) = ShadowScreen(v);
            // the cascades fit the person's draw distance, not the governor's draw distance: shadows look the same either
            // way; under lag the governor may end the last cascade sooner (its shadow reach trim)
            float reach = EnvShadowReach ?? Governor.ShadowReach;
            float margin = EnvFarMargin ?? Quality.FarCascadeMargin;
            int farSize = FarShadowSize;
            if (_shadowSettings.FarReach != reach || _shadowSettings.FarMargin != margin || _shadowSettings.FarMapSize != farSize)
                _shadowSettings = ShadowSettings.Default with { FarReach = reach, FarMargin = margin, FarMapSize = farSize };
            var setup = SunShadowSetup.Compute(new ShadowCamera(v.View, v.VFov, _centerAspect, MathF.Max(v.DrawDistance, req.DrawDistance), MathF.Max(v.Near, 0.1f)),   // the viewer's cascades
                lightDir, ext, sw, sh, _shadowSettings);
            if (setup.Valid)
            {
                (Governor.ShadowFar, Governor.ShadowFullFar, Governor.ShadowCoarse) = (setup.ShadowClip.W, setup.FullFar, setup.FarClip.W > 0);
                casters = new ShadowFrustum?[7];
                for (int c = 0; c < 4; c++) casters[c] = setup.Cascades[c].Active ? setup.Cascades[c].CullFrustum : null;
                v.Sky.shadows_on = 1;
                v.Sky.shadow_matrix0 = setup.Cascades[0].ShadowMatrix;
                v.Sky.shadow_matrix1 = setup.Cascades[1].ShadowMatrix;
                v.Sky.shadow_matrix2 = setup.Cascades[2].ShadowMatrix;
                v.Sky.shadow_matrix3 = setup.Cascades[3].ShadowMatrix;
                v.Sky.shadow_viewproj0 = setup.Cascades[0].LightViewProj;
                v.Sky.shadow_viewproj1 = setup.Cascades[1].LightViewProj;
                v.Sky.shadow_viewproj2 = setup.Cascades[2].LightViewProj;
                v.Sky.shadow_viewproj3 = setup.Cascades[3].LightViewProj;
                v.Sky.shadow_clip = setup.ShadowClip;
                v.Sky.shadow_res = setup.Resolution;
                v.Sky.shadow_bias = setup.Bias;
                v.Sky.shadow_offset = setup.Offset;
                v.ShadowMapWidth = (uint)setup.MapWidth;
                v.ShadowMapHeight = (uint)setup.MapHeight;
                v.MinCasterTexels = Renderer.AbCasterTexels ?? Quality.MinCasterTexels;
                FarShadow(v, setup, casters);
                if (v.MinCasterTexels > 0 || v.FarCasterTexels > 0)
                {
                    var across = Vector3.Cross(lightDir, Vector3.UnitZ);
                    across = across.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(across);
                    (v.CasterAcross1, v.CasterAcross2) = (across, Vector3.Normalize(Vector3.Cross(lightDir, across)));
                }
                (v.FarReceivers2, v.FarReceivers3) = _shadowSettings.FarMargin > 0
                    ? (setup.Cascades[2].Active ? setup.Cascades[2].ReceiverPoints : null, setup.Cascades[3].Active ? setup.Cascades[3].ReceiverPoints : null)
                    : (null, null);
            }
        }

        // projector shadows (RenderShadowDetail 2): the two projectors in view with the largest pixel area
        v.ShadowProjectors[0] = v.ShadowProjectors[1] = default;
        v.Sky.spot_shadows = 0;
        if (Quality.Shadows && Quality.ProjectorShadows)
        {
            var candidates = new List<RegionScene.ProjectorCandidate>();
            foreach (var (scene, offset) in scenes) scene.CollectProjectors(v, offset, frustum, candidates);
            if (candidates.Count > 0)
            {
                var at = Vector3.Normalize(new Vector3(-v.View.M13, -v.View.M23, -v.View.M33));
                var chosen = candidates
                    .OrderByDescending(c => SpotShadow.Priority(c.Position, c.Rotation, c.Radius, v.Position, at, v.VFov, (int)v.Height))
                    .Take(2).ToList();
                casters ??= new ShadowFrustum?[7];
                var settings = ShadowSettings.Default;
                for (int i = 0; i < chosen.Count; i++)
                {
                    var c = chosen[i];
                    var ss = SpotShadow.Compute(v.View, c.Position, c.Rotation, c.Scale, c.Fov, c.Radius, ShadowScreen(v).W, settings);
                    casters[4 + i] = ss.CullFrustum;
                    if (i == 0) { v.Sky.shadow_matrix4 = ss.ShadowMatrix; v.Sky.shadow_viewproj4 = ss.LightViewProj; }
                    else { v.Sky.shadow_matrix5 = ss.ShadowMatrix; v.Sky.shadow_viewproj5 = ss.LightViewProj; }
                    v.Sky.spot_shadows |= 1 << i;
                    v.ShadowProjectors[i] = (c.Region, c.LocalId);
                    v.SpotShadowMapSize = (uint)ss.MapSize;
                }
                v.Sky.proj_shadow_res = new Vector2(v.SpotShadowMapSize, v.SpotShadowMapSize);
                v.Sky.spot_shadow_bias = settings.SpotShadowBias;
                v.Sky.spot_shadow_offset = settings.SpotShadowOffset;
            }
        }

        _phases.Shadows += Lap(ref tPhase);
        // avatars first: their poses place their attachments, which the scenes draw with their other objects
        long avStart = Stopwatch.GetTimestamp();
        var hidden = req.NoAvatars ? scenes.SelectMany(s => s.Scene.Source.Avatars.Values.Select(a => a.ID)).ToHashSet() : req.HideAvatars;
        var avReq = new RenderRequest { RegionHandle = req.RegionHandle, CameraPosition = req.Position, ShowUi = req.ShowNames, HideAvatars = hidden, Self = req.Self };
        pending += _world.Avatars.AddAvatars(scenes, v, dl, avReq, frustum, casters, View);
        avatarMs = Stopwatch.GetElapsedTime(avStart).TotalMilliseconds;
        tPhase = Stopwatch.GetTimestamp();
        foreach (var (scene, offset) in scenes)
        {
            var handle = scene.Source.Handle;
            // the main view's hooks, kept by the scene for what places attachments between frames (picks, particles)
            scene.Hooks = MirrorHooks(new SceneHooks
            {
                AttachmentFrame = (av, point) => _world.Avatars.AttachmentFrame(handle, av, point),
                RiggedPalette = (av, shape, list) => _world.Avatars.RiggedPalette(handle, av, shape, list),
                AvatarExtents = av => _world.Avatars.Extents(handle, av),
                WornOf = av => _world.Avatars.WornOf(handle, av),
                SelfOwner = SelfIn(scene, req.Self),
            }, handle);
            scene.Plan(_plan, scene.Hooks, v, dl, offset, View, frustum, casters);
        }
        foreach (var (scene, offset) in farScenes)
            scene.Plan(_plan, scene.Hooks, v, dl, offset, View, frustum, casters, objects: false);
        // the scenes' parts on the scene workers, appended in order (World/DrawPlan.cs)
        var rs = _plan.Run(_workers, dl, _world);
        pending += rs.Pending;
        objects += rs.Objects;
        culled += rs.Culled;
        _phases.Objects += Lap(ref tPhase);
        _particles.Extra ??= _world.Avatars.BeamParticles;   // others' beams (World/Avatars.Effects.cs)
        var particles = Quality.Particles ? _opt.Particles : _noParticles ??= _opt.Particles with { MaxParticles = 0 };
        if (!ReferenceEquals(_particles.Settings, particles)) _particles.Settings = particles;
        pending += _particles.Build(v, _world.Regions, scene =>
        {
            Utils.LongToUInts(scene.Source.Handle, out uint sx, out uint sy);
            return new Vector3((float)((long)sx - rx), (float)((long)sy - ry), 0);
        }, dl, frustum, _world.Textures);
        _phases.Particles += Lap(ref tPhase);
        if (_world.Environment(region.Source.Handle) is { Loaded: false }) pending++;
        SortLights(dl, v.Position);
        _phases.Avatars += avatarMs;
        _phases.Frames++;
        _lastObjects = objects;
        _lastCulled = culled;
        _lastDraws = dl.TotalDraws;
        // what's drawn, for the view line only: walking every draw (and a string) each frame was waste
        if (Stopwatch.GetElapsedTime(_lastAssetReport).TotalSeconds > 9.5) _lastContent = Content(dl);
        return pending;
    }

    /// <summary>What the GPU has to draw, for the view line: lights, blended faces and particles, triangles for the eyes
    /// (each eye draws them) and for the shadow maps (all maps together). Which of these grows in a heavy place tells
    /// per-pixel cost from geometry.</summary>
    static string Content(DrawList dl)
    {
        static long Tris(List<DrawCmd> l) { long n = 0; foreach (var d in l) n += d.Range.IndexCount; return n / 3; }
        long eye = Tris(dl.Opaque) + Tris(dl.OpaqueTwoSided) + Tris(dl.Fullbright) + Tris(dl.Alpha) + Tris(dl.RiggedAlpha) + Tris(dl.Water);
        long shadow = 0;
        foreach (var c in dl.ShadowCasters) shadow += Tris(c);
        return $"; content: {dl.Lights.Count} lights, {dl.Projectors.Count} projectors, {dl.Alpha.Count + dl.RiggedAlpha.Count} blended, " +
               $"{dl.ParticleQuads.Count} particles, {eye / 1e6:F1}M triangles an eye, {shadow / 1e6:F1}M in shadows";
    }
    string _lastContent = "";
    Particles.ParticleSettings? _noParticles;

    static double Lap(ref long since)
    {
        long now = Stopwatch.GetTimestamp();
        double ms = (now - since) * 1000.0 / Stopwatch.Frequency;
        since = now;
        return ms;
    }

    /// <summary>Where the scene phase goes, summed over the frames since the last view line.</summary>
    struct ScenePhases
    {
        public double Sync, Shadows, Avatars, Objects, Particles;
        public int Frames;
        public readonly string Line => Frames == 0 ? "" :
            $"; scene ms a frame: sync {Sync / Frames:F1}, shadow fit {Shadows / Frames:F1}, avatars {Avatars / Frames:F1}, objects {Objects / Frames:F1}, particles {Particles / Frames:F1}";
    }
    ScenePhases _phases;
    readonly AabbSceneExtents _extents = new();
    // the main view's probe list, filled again each frame (it's in the frame's arena by then)
    BuiltProbeList? _mainProbes;

    /// <summary>The screen size shadow maps are sized from: the view's, scaled down to the cap.</summary>
    (int W, int H) ShadowScreen(ViewSetup v)
    {
        if (_stillShadowScreen is { } still) return still;   // a still's maps are the person's (FrameRenderer.Still.cs)
        int w = (int)v.Width, h = (int)v.Height, cap = Quality.ShadowCap;
        if (cap <= 0 || Math.Max(w, h) <= cap) return _lastShadowScreen = (w, h);
        float k = (float)cap / Math.Max(w, h);
        return _lastShadowScreen = ((int)(w * k), (int)(h * k));
    }

    static void SortLights(DrawList dl, Vector3 cam)
    {
        // the viewer feeds the nearest lights to forward shaders; keep the list nearest first
        dl.Lights.Sort((a, b) => Vector3.DistanceSquared(new Vector3(a.PositionRadius.X, a.PositionRadius.Y, a.PositionRadius.Z), cam)
            .CompareTo(Vector3.DistanceSquared(new Vector3(b.PositionRadius.X, b.PositionRadius.Y, b.PositionRadius.Z), cam)));
        if (dl.Lights.Count > 256) dl.Lights.RemoveRange(256, dl.Lights.Count - 256);
    }

    // --- reflection probes --------------------------------------------------------------------------

    static long SkyKey(ViewSetup v) => v.LocalKey != 0 ? HashCode.Combine(1, v.LocalKey) : HashCode.Combine(2, v.RegionHandle);

    void EnsureCaptureShadowMaps(ViewSetup v)
    {
        if (Quality.Shadows && _gpu.Renderer.ShadowMaps[0] == null)
        {
            if (v.ShadowMapWidth > 0) _gpu.Renderer.EnsureShadowMaps(v.ShadowMapWidth, v.ShadowMapHeight);
            else _gpu.Renderer.EnsureShadowMaps((uint)ShadowMath.BlurHappySize(1280, 1f), (uint)ShadowMath.BlurHappySize(720, 1f));
        }
    }

    sealed record CaptureLists(BuiltProbeList Irradiance, BuiltProbeList Radiance);

    static ViewSetup LodCamera(ViewSetup env, ulong probeRegion) => new()
    {
        Position = env.Position + RegionOffset(env.RegionHandle, probeRegion), View = env.View, VFov = env.VFov, Width = env.Width, Height = env.Height,
    };

    /// <summary>CPU side of a batch of captures: each one's draw list, its sun shadow, and the probe lists its two
    /// passes sample. Captures still waiting for assets go back to the queue (<paramref name="defer"/>).</summary>
    List<CaptureLists> BuildCaptures(List<ProbeCapture> batch, Func<ProbeCapture, bool>? defer = null)
    {
        foreach (var c in batch)
        {
            var site = c.Entry.Site;
            var env = c.Env;
            ulong handle = c.Scene.Source.Handle;
            var lodView = LodCamera(env, handle);
            float water = c.Scene.Source.Simulator.WaterHeight;
            var pv = ProbeSystem.FaceView(env, handle, water, c.Center, site.NearClip, 0, lodView);
            c.Draws.Clear();

            ShadowFrustum?[]? casters = null;
            bool lightUp = env.SunUp ? env.Sky.sunlight_color.LengthSquared() > 0 : env.Sky.moonlight_color.LengthSquared() > 0;
            c.Shadows = Quality.Shadows && lightUp && site.Kind != ProbeKind.Default && _gpu.Renderer.ShadowMaps[0] != null;
            if (c.Shadows)
            {
                const float r = ProbeManager.CaptureFar * 1.7321f;
                var casterDir = Vector3.Normalize(env.SunUp ? env.SunDirRegion : env.MoonDirRegion);
                var lightDir = -casterDir;
                var up = MathF.Abs(lightDir.Z) > 0.75f ? Vector3.UnitX : Vector3.UnitZ;
                var lview = ShadowMath.Look(c.Center, lightDir, -up);
                var proj = ShadowMath.GlOrtho(-r, r, -r, r, -(r + 256f), r);
                var viewProj = lview * proj;
                casters = new ShadowFrustum?[] { ShadowFrustum.FromCorners(ShadowMath.UnprojectCube(viewProj)) };
                c.ShadowLightViewProj = viewProj * ShadowMath.VulkanDepthRemap;
                c.ShadowTex = viewProj * ShadowMath.TexBias;
            }
            int pending = 0;
            foreach (var (scene, offset) in _probes!.ScenesNear(c.Scene, c.Center, ProbeManager.CaptureFar * 1.8f))
            {
                // the viewer leaves avatars, and so their attachments and rigged meshes, out of probe captures (the
                // capture's own hooks: the scene keeps the main view's for picks and particle sources)
                scene.Plan(_plan, scene.Hooks with { AttachmentFrame = null, RiggedPalette = null }, pv, c.Draws, offset, CaptureView, Frustum.All, casters,
                    objects: site.Kind != ProbeKind.Default);
            }
            c.Pending = pending + _plan.Run(_workers, c.Draws, _world).Pending;
        }
        if (defer != null) batch.RemoveAll(c => defer(c));

        var lists = new List<CaptureLists>(batch.Count);
        foreach (var c in batch)
        {
            var env = c.Env;
            float amb = env.SkyU.ReflectionProbeAmbiance;
            var defOrigin = LodCamera(env, c.Scene.Source.Handle).Position + new Vector3(0, 0, ProbeManager.DefaultHeight);
            (ProbeEntry, bool) self = (c.Entry, c.WasComplete);
            lists.Add(new CaptureLists(
                ProbeManager.Build(_probes!.Around(c, batch, false), amb, true, defOrigin, self),
                ProbeManager.Build(_probes.Around(c, batch, true), amb, false, defOrigin, self)));
        }
        return lists;
    }

    static Vector3 RegionOffset(ulong from, ulong to)
    {
        Utils.LongToUInts(from, out uint fx, out uint fy);
        Utils.LongToUInts(to, out uint tx, out uint ty);
        return new Vector3((float)((long)fx - tx), (float)((long)fy - ty), 0);
    }

    static ulong CaptureArenaBytes(List<ProbeCapture> batch, List<CaptureLists> lists) =>
        (ulong)batch.Sum(c => (long)Renderer.ArenaBytes(c.Draws, 12)) + (ulong)lists.Sum(l => (long)(l.Irradiance.Bytes + l.Radiance.Bytes));

    void PrepareCaptures(FrameArena arena, List<ProbeCapture> batch, List<CaptureLists> lists)
    {
        var r = _gpu.Renderer;
        var sys = _probes?.System;
        if (sys == null) return;
        var shadowMap = r.ShadowMaps[0];
        for (int i = 0; i < batch.Count; i++)
        {
            var c = batch[i];
            var env = c.Env;
            ulong handle = c.Scene.Source.Handle;
            float water = c.Scene.Source.Simulator.WaterHeight;
            var lodView = LodCamera(env, handle);
            c.Uploaded = r.Upload(arena, c.Draws);
            GpuSky irrProbes = default, radProbes = default;
            lists[i].Irradiance.Write(arena, ref irrProbes);
            lists[i].Radiance.Write(arena, ref radProbes);
            for (int face = 0; face < 6; face++)
            {
                var iv = ProbeSystem.FaceView(env, handle, water, c.Center, c.Entry.Site.NearClip, face, lodView);
                iv.Sky = GpuSky.Build(env.SkyIrradianceU, env.WaterU, iv.View, water, c.Center);
                CaptureSky(ref iv.Sky, in irrProbes, c, iv, shadowMap);
                c.IrradianceSetups[face] = iv;
                c.IrradianceViews[face] = r.WriteView(arena, iv, sys.Capture.Width, sys.Capture.Height);

                var rv = ProbeSystem.FaceView(env, handle, water, c.Center, c.Entry.Site.NearClip, face, lodView);
                rv.Sky = GpuSky.Build(env.SkyU, env.WaterU, rv.View, water, c.Center);
                CaptureSky(ref rv.Sky, in radProbes, c, rv, shadowMap);
                c.RadianceSetups[face] = rv;
                c.RadianceViews[face] = r.WriteView(arena, rv, sys.Capture.Width, sys.Capture.Height);
            }
        }
    }

    static void CaptureSky(ref GpuSky sky, in GpuSky probes, ProbeCapture c, ViewSetup face, GpuImage? shadowMap)
    {
        sky.sun_tex_a = c.Env.Sky.sun_tex_a; sky.sun_tex_b = c.Env.Sky.sun_tex_b; sky.moon_tex = c.Env.Sky.moon_tex;
        sky.rainbow_tex = c.Env.Sky.rainbow_tex; sky.halo_tex = c.Env.Sky.halo_tex;
        sky.ssao_on = 0;
        sky.spot_shadows = 0;
        sky.shadows_on = 0;
        sky.shadow_far = default;
        sky.probes_on = probes.probes_on;
        sky.probe_default = probes.probe_default;
        sky.probe_list_lo = probes.probe_list_lo;
        sky.probe_list_hi = probes.probe_list_hi;
        sky.probe_cells_lo = probes.probe_cells_lo;
        sky.probe_cells_hi = probes.probe_cells_hi;
        sky.probe_grid_origin = probes.probe_grid_origin;
        sky.probe_grid_cell = probes.probe_grid_cell;
        sky.probe_grid_w = probes.probe_grid_w;
        sky.probe_grid_h = probes.probe_grid_h;
        sky.max_probe_lod = probes.max_probe_lod;
        sky.probe_capture_far = ProbeManager.CaptureFar;
        if (c.Shadows && shadowMap != null)
        {
            var s = ShadowSettings.Default;
            Matrix4x4.Invert(face.View, out var invView);
            sky.shadows_on = 1;
            sky.shadow_matrix0 = invView * c.ShadowTex;
            sky.shadow_viewproj0 = c.ShadowLightViewProj;
            sky.shadow_clip = new Vector4(300, 400, 500, 600);   // every capture depth falls in the first split
            sky.shadow_res = new Vector2(shadowMap.Width, shadowMap.Height);
            sky.shadow_bias = s.Bias + s.BiasError * MathF.Abs(c.Center.Z) / 3000f;
            sky.shadow_offset = s.Offset;
        }
    }

    /// <summary>Every probe's irradiance pass, then every radiance pass (each radiance pass sees its neighbours'
    /// fresh irradiance). LLReflectionMapManager::updateProbeFace per face.</summary>
    void RecordCaptures(CommandBuffer cmd, List<ProbeCapture> batch)
    {
        var r = _gpu.Renderer;
        var sys = _probes?.System;
        if (sys == null || batch.Count == 0) return;
        r.Profiler.ProbeFaces(12 * batch.Count);   // (the load governor learns a face's GPU cost)
        foreach (var radiance in new[] { false, true })
            foreach (var c in batch)
            {
                var views = radiance ? c.RadianceViews : c.IrradianceViews;
                var setups = radiance ? c.RadianceSetups : c.IrradianceSetups;
                CaptureShadow(cmd, c, radiance, views[0], keep: false);   // the whole pass this frame
                sys.RecordPass(cmd, c.Entry.Maps!, radiance,
                    face => r.RecordScene(cmd, c.Uploaded!, views[face], setups[face], sys.Capture, shadows: false), views[0]);
            }
    }

    // whose shadow the renderer keeps: a capture's pass (its draws are planned again for the radiance pass), as of the
    // shadow maps' version
    (ProbeCapture? Capture, bool Radiance, int Version) _captureShadowKept;

    /// <summary>A capture pass's sun shadow into cascade 0, where its faces read it: drawn on the pass's first frame and,
    /// when the pass goes on in later frames (<paramref name="keep"/>), kept and copied back there in each of them
    /// rather than drawn again (the frame's own cascade 0 overwrites it in between).</summary>
    void CaptureShadow(CommandBuffer cmd, ProbeCapture c, bool radiance, Renderer.ViewData view, bool keep)
    {
        if (!c.Shadows) return;
        var r = _gpu.Renderer;
        if (Renderer.CaptureShadowEveryFrame) { r.RecordShadowMap(cmd, c.Uploaded!, view, 0); return; }   // A/B: as before
        var pass = (c, radiance, r.CaptureShadowVersion);
        if (_captureShadowKept == pass && r.RestoreCaptureShadow(cmd)) return;
        r.RecordShadowMap(cmd, c.Uploaded!, view, 0);
        _captureShadowKept = keep && r.KeepCaptureShadow(cmd) ? pass : default;
    }

    /// <summary>A capture spread over frames: faces 0-5 are its irradiance pass, 6-11 its radiance pass.</summary>
    sealed class SlicedCapture(ProbeCapture capture, CaptureLists lists)
    {
        public readonly ProbeCapture Capture = capture;
        public CaptureLists Lists = lists;
        public int Step;
    }

    SlicedCapture? _slice;

    /// <summary>Still worth finishing: its probe's site and region exist and it still holds its maps. Checked each
    /// frame after the probe plan, the last thing before recording that can kill sites or release maps.</summary>
    bool SliceAlive(ProbeCapture c) => c.Entry.Site.Alive && c.Entry.Maps != null && _world.Region(c.Scene.Source.Handle) == c.Scene;

    /// <summary>This frame's faces of the capture in progress, from its step up to <paramref name="to"/>, filtering
    /// each pass once its sixth face is in.</summary>
    void RecordSlice(CommandBuffer cmd, SlicedCapture s, int to)
    {
        var r = _gpu.Renderer;
        var sys = _probes!.System;
        var c = s.Capture;
        r.Profiler.ProbeFaces(Math.Max(0, to - s.Step));
        for (int step = s.Step; step < to;)
        {
            bool radiance = step >= 6;
            int passEnd = radiance ? 12 : 6, end = Math.Min(to, passEnd);
            var views = radiance ? c.RadianceViews : c.IrradianceViews;
            var setups = radiance ? c.RadianceSetups : c.IrradianceSetups;
            CaptureShadow(cmd, c, radiance, views[0], keep: end < passEnd);
            int offset = radiance ? 6 : 0;
            sys.RecordFaces(cmd, step - offset, end - offset,
                face => r.RecordScene(cmd, c.Uploaded!, views[face], setups[face], sys.Capture, shadows: false), views[0]);
            if (end == passEnd) sys.Filter(cmd, c.Entry.Maps!, radiance, views[0]);
            step = end;
        }
    }

    /// <summary>
    /// What a ray from <paramref name="origin"/> (the region <paramref name="regionHandle"/>'s frame) hits first within
    /// <paramref name="maxDistance"/>: a prim, an avatar or the ground, in any region loaded. The hit point comes back
    /// in the ray's region frame. Render thread, between frames (uses the last frame's poses). The person's own avatar
    /// and what it wears never count, known by its agent id (AvatarLimits.Self) in every region as well as by the local
    /// id given: a local id is per region and lags a teleport or crossing.
    /// </summary>
    public PickHit? Pick(ulong regionHandle, Vector3 origin, Vector3 dir, float maxDistance, uint skipAvatarLocalId = 0, bool ground = true)
    {
        CheckWorld();
        if (dir.LengthSquared() < 1e-12f) return null;
        dir = Vector3.Normalize(dir);
        float best = maxDistance;
        PickHit? hit = null;
        var self = AvatarLimits.Self;
        foreach (var scene in _world.Regions)
        {
            if (!scene.Source.HasSimulator) continue;
            var offset = RegionOffset(regionHandle, scene.Source.Handle);   // the ray's frame to the scene's
            if (scene.Raycast(origin + offset, dir, ref best, skipAvatarLocalId, ground, self) is { } h)
                hit = h with { Point = h.Point - offset };
        }
        return hit;
    }

    /// <summary>As <see cref="Pick"/>, but only the prims <paramref name="only"/> accepts count, and nothing else blocks
    /// the ray: where on a known object a touch lands. Render thread, between frames.</summary>
    public PickHit? PickPrims(ulong regionHandle, Vector3 origin, Vector3 dir, float maxDistance, Func<Primitive, bool> only)
    {
        CheckWorld();
        if (dir.LengthSquared() < 1e-12f) return null;
        dir = Vector3.Normalize(dir);
        float best = maxDistance;
        PickHit? hit = null;
        foreach (var scene in _world.Regions)
        {
            if (!scene.Source.HasSimulator) continue;
            var offset = RegionOffset(regionHandle, scene.Source.Handle);
            if (scene.RaycastPrims(origin + offset, dir, ref best, only) is { } h)
                hit = h with { Point = h.Point - offset };
        }
        return hit;
    }

    /// <summary>The own avatar's local collision model (RegionScene.CollideRay) in every region loaded: the nearest solid
    /// surface along a ray from the region <paramref name="regionHandle"/>'s frame. Render thread, between frames.</summary>
    public CollideHit? CollideRay(ulong regionHandle, Vector3 origin, Vector3 dir, float maxDistance, uint selfLocalId, Func<Primitive, CollideAs> solid)
    {
        CheckWorld();
        if (dir.LengthSquared() < 1e-12f) return null;
        dir = Vector3.Normalize(dir);
        CollideHit? hit = null;
        foreach (var scene in _world.Regions)
        {
            if (!scene.Source.HasSimulator) continue;
            var offset = RegionOffset(regionHandle, scene.Source.Handle);
            if (scene.CollideRay(origin + offset, dir, hit?.Distance ?? maxDistance, selfLocalId, solid) is { } h) hit = h;
        }
        return hit;
    }

    /// <summary>Where the own avatar's eyes were drawn last frame while it sat (the sit animation's, in the frame of the
    /// view's region) and which way its body faced, or null.</summary>
    public (Vector3 Eyes, Vector3 Facing)? SelfSeatedEyes { get { CheckWorld(); return _world.Avatars.SelfSeatedEyes; } }
    public Vector3? SelfSeatedEyesLocal { get { CheckWorld(); return _world.Avatars.SelfSeatedEyesLocal; } }

    /// <summary>Where a prim of a region is drawn this frame (attachments on their wearer), in that region's frame.</summary>
    public Vector3? DrawnPosition(ulong regionHandle, uint localId)
    {
        CheckWorld();
        return _world.Regions.FirstOrDefault(r => r.Source.Handle == regionHandle)?.DrawnPosition(localId);
    }

    public string DescribeProbes() { try { return _probes?.Describe() ?? "probes off"; } catch (Exception e) { return e.Message; } }

    public (ulong Used, ulong Budget) VramBudget() => _gpu.Vk.DeviceLocalBudget();
    public ulong TextureBytes => _world.Textures.ResidentBytes;
    public int TextureCount => _world.Textures.Count;

    public void Dispose()
    {
        WaitIdle();
        _probes?.Dispose();
        _overlay.Dispose();
        _layerMirror?.Dispose();
        _layerCut?.Dispose();
        DisposeHud();
        DisposeVideo();
        DisposeSeen();
        DisposePreview();
        _workers?.Dispose();
        _world.Dispose();
        _gpu.Dispose();
    }
}
