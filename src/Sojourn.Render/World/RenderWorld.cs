using System.Collections.Concurrent;
using LibreMetaverse;
using Sojourn.Render.Environment;
using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;

namespace Sojourn.Render.World;

/// <summary>
/// Everything the render thread owns across regions and views: the GPU context, the shared texture/geometry/material
/// caches, one RegionScene per known region and each region's environment. Render thread only, except AddRegion /
/// RemoveRegion which queue.
/// </summary>
public sealed class RenderWorld : IDisposable
{
    public GpuContext Gpu { get; }
    public TextureStore Textures { get; }
    public GeometryCache Geometry { get; }
    public MaterialStore Materials { get; }
    /// <summary>Web pages and video on faces (docs/media.md).</summary>
    public Media.MediaSurfaces Media { get; } = new();
    /// <summary>Dead reckoning for avatars drawn between updates (the caller begins each frame).</summary>
    public MotionSmoother Motion { get; } = new();
    /// <summary>Loading and unloading held to the frame's time and video memory (Realtime/LoadGovernor.cs).</summary>
    public Realtime.LoadGovernor Governor { get; } = new();
    /// <summary>Property lines and ban lines (World/ParcelLines.cs).</summary>
    public ParcelLines ParcelLines { get; } = new();
    /// <summary>Beacons, highlights and the tracking beacon (World/Beacons.cs).</summary>
    public Beacons Beacons { get; } = new();
    /// <summary>World objects' faces' instances and materials kept on the GPU across frames (Frame/PersistentTable.cs);
    /// null with SOJOURN_PERSISTENT=0 (every face's written into each frame's draw list, as before).</summary>
    public PersistentTable<GpuInstance>? StaticInstances { get; }
    public PersistentTable<GpuMaterial>? StaticMaterials { get; }
    /// <summary>Objects' faces resolved off the frame (World/FaceResolver.cs); null with SOJOURN_FACE_THREADS=0.</summary>
    public FaceResolver? Resolver { get; }
    /// <summary>The settings the renderer reads each frame (the settings menu's view section); here for what the world
    /// itself reads of them: flexible prims (RegionScene.Flexi.cs) and avatar physics (Avatars.Physics.cs).</summary>
    public Realtime.RenderQuality? Quality { get; set; }
    /// <summary>Avatar bodies (created on first use: loads the character data).</summary>
    public Avatars Avatars => _avatars ??= new Avatars(this);
    Avatars? _avatars;
    readonly Dictionary<ulong, RegionScene> _regions = new();
    readonly ConcurrentQueue<Action> _pending = new();
    readonly Dictionary<ulong, RegionEnvironment> _env = new();
    public Action<string>? Log { get; set; }
    long _frameNumber;
    readonly List<(long Frame, Action Free)> _retire = new();

    public RenderWorld(GpuContext gpu, int workerThreads = 4)
    {
        Gpu = gpu;
        Textures = new TextureStore(workerThreads);
        Geometry = new GeometryCache(workerThreads);
        Materials = new MaterialStore();
        Textures.Retire = (img, slot) => RetireTexture(() =>
        {
            img.Dispose();
            if (slot < 0) return;
            Gpu.Bindless.FreeTexture(slot);
            Textures.SlotFreed();
        });
        int faceThreads = int.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_FACE_THREADS"), out int ft) ? ft : 2;
        if (faceThreads > 0) Resolver = new FaceResolver(faceThreads);
        if (System.Environment.GetEnvironmentVariable("SOJOURN_PERSISTENT") != "0")
        {
            StaticInstances = new PersistentTable<GpuInstance>(gpu.Vk, 32768, RetireLater);
            StaticMaterials = new PersistentTable<GpuMaterial>(gpu.Vk, 32768, RetireLater);
            gpu.Renderer.Persistent = (StaticInstances, StaticMaterials);
        }
    }

    public IReadOnlyCollection<RegionScene> Regions => _regions.Values;

    public void AddRegion(RegionSource src) => _pending.Enqueue(() =>
    {
        if (_regions.TryGetValue(src.Handle, out var old)) old.Release();
        _regions[src.Handle] = new RegionScene(this, src);
        _env[src.Handle] = new RegionEnvironment(src, Log);
    });

    public void RemoveRegion(ulong handle) => _pending.Enqueue(() =>
    {
        if (_regions.Remove(handle, out var old)) old.Release();
        _env.Remove(handle);
        ParcelLines.Forget(handle);
    });

    public RegionScene? Region(ulong handle) => _regions.TryGetValue(handle, out var r) ? r : null;
    public RegionEnvironment? Environment(ulong handle) => _env.TryGetValue(handle, out var e) ? e : null;
    public IReadOnlyCollection<RegionEnvironment> Environments => _env.Values;

    /// <summary>Frames begun so far.</summary>
    public long FrameNumber => _frameNumber;

    /// <summary>Time this frame spent resolving objects' faces (materials and textures, first draws and after a texture
    /// arrived), Stopwatch ticks, and how much a frame may spend before the rest wait (RegionScene.DrawObject).</summary>
    public long ResolveTicks;
    long _resolveBudget = (long)(0.002 * System.Diagnostics.Stopwatch.Frequency);
    public bool ResolveSpent => Interlocked.Read(ref ResolveTicks) > _resolveBudget * ResolveThreads;
    /// <summary>How many threads build the frame's draw lists at once (the scene workers and the render thread): the
    /// budget is for the frame's time, and they spend it together.</summary>
    public int ResolveThreads = 1;
    /// <summary>Time a build spent resolving (any of the threads building at once).</summary>
    public void AddResolve(long ticks) => Interlocked.Add(ref ResolveTicks, ticks);
    /// <summary>Objects never drawn yet that are further than this (to their edge) are held while the governor holds
    /// them, and let in through <see cref="Admit"/> (RegionScene.DrawObject); <see cref="HeldObjects"/> counts those
    /// held this frame.</summary>
    public float NewObjectRadius = float.PositiveInfinity;
    public int HeldObjects;

    // admission of held objects: this frame's allowance (taken by Admit on any building thread), the least size class it
    // goes to (from the last frame's held objects, so the largest on screen go first), and the held objects' size
    // classes this frame (log2 of their screen area in pixels)
    readonly int[] _heldSizes = new int[32];
    int _admitLeft, _admitGiven, _admitClass;
    double _admitTokens;
    long _admitAt;

    /// <summary>Whether a held object, this big on screen (pixels), is let in now. Thread-safe.</summary>
    public bool Admit(float screenArea)
    {
        int size = screenArea >= 2 ? Math.Min(31, (int)MathF.Log2(MathF.Min(screenArea, 1e9f))) : 0;
        Interlocked.Increment(ref _heldSizes[size]);
        return size >= _admitClass && Interlocked.Decrement(ref _admitLeft) >= 0;
    }

    /// <summary>This frame's allowance of held objects: the governor's rate over the time since the last frame (a
    /// quarter second's worth at most), going to the largest size classes the last frame held.</summary>
    void BeginAdmission(Realtime.LoadGovernor g)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = _admitAt == 0 ? 0 : (now - _admitAt) / (double)System.Diagnostics.Stopwatch.Frequency;
        _admitAt = now;
        int used = _admitGiven - Math.Max(0, _admitLeft);
        g.AdmittedObjects += used;
        double rate = g.AdmitPerSecond;
        if (double.IsPositiveInfinity(rate))
        {
            _admitTokens = 0;
            _admitGiven = _admitLeft = 0;
            Array.Clear(_heldSizes);
            return;
        }
        _admitTokens = Math.Min(_admitTokens - used + rate * Math.Min(dt, 0.25), Math.Max(1, rate * 0.25));
        int give = (int)Math.Max(0, _admitTokens);
        int cls = 0, sum = 0;
        for (int c = _heldSizes.Length - 1; c >= 0; c--)
        {
            sum += _heldSizes[c];
            if (sum >= give) { cls = c; break; }
        }
        Array.Clear(_heldSizes);
        (_admitClass, _admitGiven, _admitLeft) = (cls, give, give);
    }

    /// <summary>Apply queued region changes and pending uploads. Call at the start of each frame.</summary>
    public void BeginFrame()
    {
        var g = Governor;
        _resolveBudget = (long)(g.ResolveMs * 0.001 * System.Diagnostics.Stopwatch.Frequency);
        NewObjectRadius = g.NewObjectRadius;
        if (HeldObjects > 0) { g.HeldObjects += HeldObjects; HeldObjects = 0; }
        BeginAdmission(g);
        Textures.MaxFetches = g.Fetches;
        Textures.MaxDecodes = g.Decodes;
        Textures.HoldGrowth = g.HoldGrowth;
        Textures.Pressure = g.Pressure;
        // uploads go in the GPU's spare time (LoadGovernor.BeginLoading): what they stage is spent from it
        g.BeginLoading();
        var up = Gpu.Uploader;
        bool force = _frameNumber % g.ForcedUploadEvery == 0 && g.MayForce;
        bool forceBake = _frameNumber % Math.Min(g.ForcedUploadEvery, 8) == 0;   // a cloud costs more than a late frame
        ResolveTicks = 0;
        while (_pending.TryDequeue(out var a)) a();
        // texture slots and states change here: not while a face resolver reads them (World/FaceResolver.cs)
        Resolver?.Slots.EnterWriteLock();
        try
        {
            Textures.ProcessShrinks(Gpu.Vk, Gpu.Bindless, up);   // (with nothing to shed, it only empties its queue)
            ulong staged = up.PendingBytes;
            Textures.ProcessUploads(Gpu.Vk, Gpu.Bindless, up, g.UploadBudget(g.TextureUploadBytes), g.TextureUploadMs, force, forceBake,
                streamBudget: g.TextureUploadBytes);
            g.Uploaded(Since(up.PendingBytes, staged));
            Textures.Reclaim(Gpu.Bindless, idle: false);   // (only if the slot table is low)
            Media.BeginFrame(this);
        }
        finally { Resolver?.Slots.ExitWriteLock(); }
        while (Media.TakeInvalid(out (ulong Region, uint LocalId) mo)) Region(mo.Region)?.InvalidateMedia(mo.LocalId);
        while (Media.TakeInvalid(out UUID mt)) foreach (var r in _regions.Values) r.InvalidateMedia(mt);
        ulong before = up.PendingBytes;
        Geometry.ProcessUploads(Gpu.GeometryPool, g.UploadBudget(g.GeometryUploadBytes), Gpu.SkinWeights, g.GeometryUploadMs, force && g.MayForce);
        g.Uploaded(Since(up.PendingBytes, before));
        foreach (var w in Geometry.DrainFreedWeights()) { var r = w; RetireLater(() => Gpu.SkinWeights.Free(r)); }
    }

    /// <summary>Bytes staged since a reading of Uploader.PendingBytes (a full ring submits and starts it again).</summary>
    static ulong Since(ulong now, ulong before) => now >= before ? now - before : now;

    /// <summary>Called after a frame is submitted; frees resources once every in-flight frame is past them.</summary>
    public void EndFrame()
    {
        // (retirements can come from the render thread's record phase while the main thread has the world: locked)
        lock (_retire)
        {
            _frameNumber++;
            long safe = _frameNumber - Gpu.Frames.Length - 1;
            for (int i = _retire.Count - 1; i >= 0; i--)
                if (_retire[i].Frame <= safe)
                {
                    _retire[i].Free();
                    _retire.RemoveAt(i);
                }
        }
    }

    MeshRange _water;

    /// <summary>The water surface: one big grid around the region at z = 0 (the viewer's region water plus void
    /// water out to the horizon; waterV pulls far vertices in to 2560 m).</summary>
    public MeshRange WaterMesh
    {
        get
        {
            if (!_water.IsEmpty) return _water;
            const int n = 72; const float min = -2176, step = 64;
            var verts = new MeshVertex[(n + 1) * (n + 1)];
            for (int y = 0; y <= n; y++)
                for (int x = 0; x <= n; x++)
                    verts[y * (n + 1) + x] = new MeshVertex
                    {
                        Position = new Vector3(min + x * step, min + y * step, 0),
                        Normal = Vector3.UnitZ, Tangent = new Vector4(1, 0, 0, 1),
                    };
            var idx = new List<uint>();
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    uint a = (uint)(y * (n + 1) + x), b = a + 1, c = (uint)((y + 1) * (n + 1) + x), d = c + 1;
                    idx.AddRange(new[] { a, b, d, a, d, c });
                }
            _water = Gpu.GeometryPool.Add(verts, idx.ToArray());
            return _water;
        }
    }

    public void RetireLater(Action free) { lock (_retire) _retire.Add((_frameNumber, free)); }
    /// <summary>A texture's image and slot, kept two frames longer than other retirements: a face cache found out of date
    /// is drawn as it was for up to two frames after it was last current (RegionScene.DrawObject), naming them.</summary>
    public void RetireTexture(Action free) { lock (_retire) _retire.Add((_frameNumber + 2, free)); }
    public void RetireRange(MeshRange r) => RetireLater(() => Gpu.GeometryPool.Free(r));

    /// <summary>Periodic housekeeping: idle geometry, render targets and textures (unseen for minutes: left behind or
    /// out of draw distance), and while video memory is short (the governor), what's been out of view longest: textures
    /// over the target, geometry and targets sooner.</summary>
    public void Housekeeping()
    {
        var g = Governor;
        Resolver?.Slots.EnterWriteLock();
        try
        {
            Textures.Reclaim(Gpu.Bindless, idle: true);
            if (g.OverTarget) Textures.Evict(5_000);
        }
        finally { Resolver?.Slots.ExitWriteLock(); }
        foreach (var r in Geometry.Evict(g.OverTarget ? 10_000 : g.HoldGrowth ? 30_000 : 120_000)) RetireRange(r);
        Gpu.TrimTargets(g.HoldGrowth ? 2_000 : 60_000);
    }

    public void Dispose()
    {
        Resolver?.Dispose();
        Media.Dispose();
        Gpu.Vk.WaitIdle();
        foreach (var r in _retire) r.Free();
        StaticInstances?.Dispose();
        StaticMaterials?.Dispose();
        Terrain.DisposeShared(Gpu.Bindless);
        Textures.Dispose();
    }
}
