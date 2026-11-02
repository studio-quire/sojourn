using System.Collections.Concurrent;
using CoreJ2K;
using CoreJ2K.Configuration;
using LibreMetaverse;
using LibreMetaverse.Rendering;
using Sojourn.Render.Geometry;
using Sojourn.Render.World;

namespace Sojourn.Render.Collision;

/// <summary>Which shape: a prim's or sculpt's by its construction (the renderer's shape hash, RegionScene.Refresh), a
/// mesh's by its asset.</summary>
public readonly record struct ShapeKey(ShapeKind Kind, UUID Asset, int Hash);

/// <summary>
/// One shape as the collision model without a GPU knows it, in unit-scale object space: its drawn surface as a flat
/// triangle list (three corners each, wound counter-clockwise seen from outside) and, for a mesh, the asset's physics
/// (<see cref="MeshPhysics"/>). A mesh whose asset has a hull of its own keeps no drawn triangles: the rule never asks
/// for them (<see cref="CollisionRule.Triangles"/>), and its box is the mesh's unit domain.
/// </summary>
public sealed class CollisionShape
{
    public required ShapeKey Key { get; init; }
    public Vector3[]? Drawn { get; private set; }
    public MeshPhysics? Physics { get; private set; }
    public Vector3 Min { get; private set; } = new(-0.5f);
    public Vector3 Max { get; private set; } = new(0.5f);
    volatile bool _ready;
    /// <summary>Built, or its data arrived and decoded: it counts from now on.</summary>
    public bool Ready => _ready;
    public volatile bool Failed;
    internal long LastUsed, LastWanted, RetryAt;
    internal float Priority = float.MaxValue;
    internal int Failures;
    internal volatile bool Busy;
    internal Primitive? Snapshot;   // a detached copy of what a build reads (sculpts and meshes, off the query's thread)
    Vector3[]? _hull;

    /// <summary>The convex hull of the drawn surface (worked out once, when the rule asks).</summary>
    public Vector3[] HullOfDrawn() => _hull ??= Drawn is { } d ? ConvexHull.Triangles(d) : Array.Empty<Vector3>();

    /// <summary>Roughly what it holds, bytes.</summary>
    public long Bytes => (Drawn?.Length ?? 0) * 12L + (_hull?.Length ?? 0) * 12L + (Physics != null ? 4096 : 0);

    internal void Set(Vector3[]? drawn, MeshPhysics? physics)
    {
        Drawn = drawn is { Length: > 0 } ? drawn : null;
        Physics = physics;
        if (Drawn != null)
        {
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (var p in Drawn) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            (Min, Max) = (lo, hi);
        }
        _ready = true;
    }
}

/// <summary>
/// The collision model's shapes (CollisionWorld): prims built by the viewer's volume code (Geometry/), sculpts from
/// their maps, meshes' physics from their assets, through the same caches the renderer uses (the grid's asset cache on
/// disk, so a renderer running beside it doesn't download anything twice). Prims and sculpts at the viewer's LOD 2 (a
/// metre-wide sphere's surface within a centimetre of the true one, at about 40% of LOD 3's triangles), meshes by their
/// physics, else their highest LOD. Prims build on the spot (a box is microseconds, and a place's prims share a few dozen shapes);
/// sculpt maps and meshes download nearest first, a few at once, and count once they've arrived. Shapes nobody has
/// asked for in three minutes are let go. Claude Opus 5.5, 2026-10-30.
/// </summary>
public sealed class CollisionShapes
{
    readonly ConcurrentDictionary<ShapeKey, CollisionShape> _shapes = new();
    readonly ConcurrentDictionary<ShapeKey, (CollisionShape Shape, IAssetSource Assets)> _waiting = new();
    readonly SemaphoreSlim _cpu;
    readonly MeshFoundry _mesher = new();
    readonly Action<string> _log;
    int _fetching;
    long _lastSweep;
    volatile bool _stop;

    /// <summary>Downloads at once (the renderer's mesh queue's eight).</summary>
    public int MaxFetches { get; set; } = 8;
    /// <summary>How long a shape nobody asks for is kept. What's within reach is asked for every second
    /// (CollisionWorld.Around), so this is only what's been left behind: on a train ten minutes of track either side
    /// was kept (2026-11-01), three is enough to turn back.</summary>
    public long IdleMs { get; set; } = 180_000;
    const int Lod = 2;

    public CollisionShapes(Action<string> log, int threads)
    {
        _log = log;
        _cpu = new SemaphoreSlim(Math.Max(1, threads));
        _ = Task.Run(Pump);
    }

    public int Count => _shapes.Count;
    public int Waiting => _waiting.Count;
    public int Fetching => Volatile.Read(ref _fetching);
    public (int Ready, int Failed, long Bytes) Totals()
    {
        int ready = 0, failed = 0;
        long bytes = 0;
        foreach (var s in _shapes.Values)
        {
            if (s.Ready) { ready++; bytes += s.Bytes; }
            else if (s.Failed) failed++;
        }
        return (ready, failed, bytes);
    }

    /// <summary>The key for a prim's shape (null: it has none: not a volume prim).</summary>
    public static ShapeKey? KeyOf(Primitive prim)
    {
        var pcode = prim.PrimData.PCode;
        if (pcode != PCode.Prim) return null;
        // a sculpt parameter with a map is a sculpt whatever its type, as the renderer takes it (RegionScene.Refresh)
        if (prim.Sculpt is { } sc && sc.SculptTexture != UUID.Zero && (int)sc.Type <= (int)SculptType.Mesh)
        {
            if (sc.Type == SculptType.Mesh) return new ShapeKey(ShapeKind.Mesh, sc.SculptTexture, 0);
            return new ShapeKey(ShapeKind.Sculpt, sc.SculptTexture, Hash(prim, pcode));
        }
        return new ShapeKey(ShapeKind.Prim, UUID.Zero, Hash(prim, pcode));
    }

    static int Hash(Primitive prim, PCode pcode) => GeometryCache.HashShape(prim.PrimData, pcode) ^ (prim.Sculpt?.Type.GetHashCode() ?? 0) * 31
        ^ ((prim.Sculpt?.Invert ?? false) ? 0x1000 : 0) ^ ((prim.Sculpt?.Mirror ?? false) ? 0x2000 : 0);

    /// <summary>The shape for a prim, if it's ready; else null, its build or download started (a prim's built now when
    /// <paramref name="buildNow"/>). <paramref name="priority"/>: lower downloads first (the distance).</summary>
    public CollisionShape? Get(ShapeKey key, Primitive prim, IAssetSource assets, float priority, bool buildNow)
    {
        var shape = _shapes.GetOrAdd(key, k => new CollisionShape { Key = k });
        long now = System.Environment.TickCount64;
        shape.LastUsed = now;
        if (shape.Ready) return shape;
        if (shape.Failed) return null;
        if (key.Kind == ShapeKind.Prim)
        {
            if (!buildNow) return null;
            lock (shape)
            {
                if (!shape.Ready) BuildPrim(shape, prim.PrimData);
            }
            return shape.Ready ? shape : null;
        }
        if (priority < shape.Priority || now - shape.LastWanted > 2000) shape.Priority = priority;
        shape.LastWanted = now;
        if (!shape.Busy && shape.Snapshot == null) shape.Snapshot = Detach(prim);
        _waiting.TryAdd(key, (shape, assets));
        return null;
    }

    /// <summary>Whether a shape is had (ready, or given up on), counting it as used now; false when it's still to come.</summary>
    public bool Settled(ShapeKey key)
    {
        if (!_shapes.TryGetValue(key, out var shape)) return false;
        shape.LastUsed = System.Environment.TickCount64;
        return shape.Ready || shape.Failed;
    }

    /// <summary>Builds a prim's shape off the query's path (what's around, ahead of the walk).</summary>
    public void Prebuild(ShapeKey key, Primitive.ConstructionData data)
    {
        if (key.Kind != ShapeKind.Prim) return;
        var shape = _shapes.GetOrAdd(key, k => new CollisionShape { Key = k });
        shape.LastUsed = System.Environment.TickCount64;
        if (shape.Ready || shape.Failed) return;
        lock (shape)
        {
            if (!shape.Ready) BuildPrim(shape, data);
        }
    }

    void BuildPrim(CollisionShape shape, Primitive.ConstructionData data)
    {
        try { shape.Set(Flatten(VolumeBuilder.BuildPrim(data, VolumeBuilder.DetailForLod(Lod))), null); }
        catch (Exception e)
        {
            shape.Failed = true;
            _log($"collision: prim shape {shape.Key.Hash:x8}: {e.GetType().Name}: {e.Message}");
        }
    }

    static Primitive Detach(Primitive p) => new()
    {
        LocalID = p.LocalID, ID = p.ID, PrimData = p.PrimData, Scale = p.Scale, Sculpt = p.Sculpt,
    };

    static Vector3[] Flatten(VolumeFace[] faces)
    {
        int n = 0;
        foreach (var f in faces) n += f.Indices.Length / 3 * 3;
        var tris = new Vector3[n];
        int at = 0;
        foreach (var f in faces)
            for (int i = 0; i + 2 < f.Indices.Length; i += 3)
            {
                tris[at++] = f.Positions[f.Indices[i]];
                tris[at++] = f.Positions[f.Indices[i + 1]];
                tris[at++] = f.Positions[f.Indices[i + 2]];
            }
        return tris;
    }

    // --- downloads: nearest first, a few at once ------------------------------------------------------------------

    async Task Pump()
    {
        var picks = new List<(ShapeKey Key, CollisionShape Shape, IAssetSource Assets)>();
        while (!_stop)
        {
            await Task.Delay(30).ConfigureAwait(false);
            try
            {
                long now = System.Environment.TickCount64;
                Sweep(now);
                int free = MaxFetches - Volatile.Read(ref _fetching);
                if (free <= 0 || _waiting.IsEmpty) continue;
                picks.Clear();
                foreach (var (key, (shape, assets)) in _waiting)
                {
                    if (shape.Ready || shape.Failed || now - shape.LastWanted > 10_000) { _waiting.TryRemove(key, out _); continue; }
                    if (!shape.Busy && now >= shape.RetryAt) picks.Add((key, shape, assets));
                }
                picks.Sort((a, b) => a.Shape.Priority.CompareTo(b.Shape.Priority));
                foreach (var (key, shape, assets) in picks)
                {
                    if (free-- <= 0) break;
                    shape.Busy = true;
                    Interlocked.Increment(ref _fetching);
                    _ = Fetch(key, shape, assets);
                }
            }
            catch (Exception e) { _log($"collision: shape queue: {e.Message}"); }
        }
    }

    async Task Fetch(ShapeKey key, CollisionShape shape, IAssetSource assets)
    {
        var status = FetchStatus.Retry;
        byte[]? data = null;
        var retryAfter = TimeSpan.Zero;
        try
        {
            (status, data, retryAfter) = key.Kind == ShapeKind.Mesh ? await MeshData(key.Asset, assets).ConfigureAwait(false)
                : await TextureData(key.Asset, assets).ConfigureAwait(false);
        }
        catch (Exception e) { _log($"collision: {key.Kind} {key.Asset}: {e.GetType().Name}: {e.Message}"); }
        finally { Interlocked.Decrement(ref _fetching); }
        try
        {
            var prim = shape.Snapshot;
            if (prim == null) return;
            if (status == FetchStatus.NotFound && key.Kind == ShapeKind.Sculpt)
            {
                // a missing map: the viewer's sphere placeholder, as the renderer draws it
                await Decoded(() => shape.Set(Flatten(VolumeBuilder.BuildSculpt(prim.PrimData, prim.Sculpt!, ReadOnlySpan<byte>.Empty, 0, 0,
                    VolumeBuilder.DetailForLod(Lod), components: 0, rowsTopDown: false, missingAsset: true)), null)).ConfigureAwait(false);
                return;
            }
            if (status != FetchStatus.Ok || data is not { Length: > 0 })
            {
                long now = System.Environment.TickCount64;
                if (status == FetchStatus.Unavailable) { shape.RetryAt = now + 1000; return; }   // no region to ask yet
                shape.Failures++;
                shape.RetryAt = now + Math.Max((long)retryAfter.TotalMilliseconds, Math.Min(60_000, 1000L << Math.Min(shape.Failures, 6)));
                if (status == FetchStatus.NotFound || shape.Failures > 6)
                {
                    shape.Failed = true;
                    _log($"collision: {key.Kind} {key.Asset}: {(status == FetchStatus.NotFound ? "not found" : $"gave up after {shape.Failures} failed downloads")}");
                }
                return;
            }
            await Decoded(() =>
            {
                if (key.Kind == ShapeKind.Mesh) DecodeMesh(shape, prim, data);
                else DecodeSculpt(shape, prim, data);
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            shape.Failed = true;
            _log($"collision: {key.Kind} {key.Asset}: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            shape.Busy = false;
            if (shape.Ready || shape.Failed) { _waiting.TryRemove(key, out _); shape.Snapshot = null; }
        }
    }

    async Task Decoded(Action decode)
    {
        await _cpu.WaitAsync().ConfigureAwait(false);
        try { decode(); }
        finally { _cpu.Release(); }
    }

    static async Task<(FetchStatus, byte[]?, TimeSpan)> MeshData(UUID id, IAssetSource assets)
    {
        if (assets is IRangedAssetSource ranged)
        {
            var r = await ranged.MeshAsync(id, CancellationToken.None).ConfigureAwait(false);
            return (r.Status, r.Data, r.RetryAfter);
        }
        var d = await assets.GetMeshAsync(id, CancellationToken.None).ConfigureAwait(false);
        return (d is { Length: > 0 } ? FetchStatus.Ok : FetchStatus.Retry, d, TimeSpan.Zero);
    }

    static async Task<(FetchStatus, byte[]?, TimeSpan)> TextureData(UUID id, IAssetSource assets)
    {
        if (assets is IRangedAssetSource ranged)
        {
            var (cached, complete) = ranged.CachedTexture(id);
            if (cached is { Length: > 0 } && complete) return (FetchStatus.Ok, cached, TimeSpan.Zero);
            var r = await ranged.TextureRangeAsync(id, 0, -1, CancellationToken.None).ConfigureAwait(false);
            if (r.Status == FetchStatus.Ok && r.Data is { Length: > 0 } d && r.ToEnd) ranged.StoreTexture(id, d, complete: true);
            return (r.Status, r.Data, r.RetryAfter);
        }
        var whole = await assets.GetTextureAsync(id, CancellationToken.None).ConfigureAwait(false);
        return (whole is { Length: > 0 } ? FetchStatus.Ok : FetchStatus.Retry, whole, TimeSpan.Zero);
    }

    /// <summary>A mesh's physics from its header; its drawn triangles too (the highest LOD) only when the rule could
    /// ask for them: no physics blocks, or no hull of its own.</summary>
    void DecodeMesh(CollisionShape shape, Primitive prim, byte[] data)
    {
        MeshPhysics? phys = null;
        try { phys = MeshPhysics.FromAsset(data); } catch { }
        Vector3[]? drawn = null;
        if (phys?.Hull == null)
        {
            var fm = _mesher.GenerateFacetedMeshMesh(prim, data, DetailLevel.Highest);
            if (fm?.Faces is { Count: > 0 } faces)
            {
                var tris = new List<Vector3>();
                foreach (var f in faces)
                {
                    if (f.Vertices == null || f.Indices == null) continue;
                    for (int i = 0; i + 2 < f.Indices.Count; i += 3)
                    {
                        int a = f.Indices[i], b = f.Indices[i + 1], c = f.Indices[i + 2], n = f.Vertices.Count;
                        if (a >= n || b >= n || c >= n) continue;
                        tris.Add(V(f.Vertices[a].Position)); tris.Add(V(f.Vertices[b].Position)); tris.Add(V(f.Vertices[c].Position));
                    }
                }
                drawn = tris.ToArray();
            }
        }
        shape.Set(drawn, phys);
    }

    static Vector3 V(LibreMetaverse.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>A sculpt from its map, at the reduction the viewer holds it at (VolumeBuilder.SculptDiscardLevel).</summary>
    static void DecodeSculpt(CollisionShape shape, Primitive prim, byte[] j2c)
    {
        byte[][] comps;
        int w, h;
        bool pooled = false;
        if (OpenJpeg.Decode(j2c, 0, pooled: true) is { } native) { (comps, w, h) = native; pooled = true; }
        else
        {
            using var img = new J2KDecoderConfiguration().WithMaxDegreeOfParallelism(1).Decode(j2c).Image;
            (w, h) = (img.Width, img.Height);
            comps = new byte[img.NumberOfComponents][];
            for (int c = 0; c < comps.Length; c++) { comps[c] = new byte[w * h]; img.ToComponentBytes(c, comps[c]); }
        }
        int nc = comps.Length;
        // as the renderer's texture store keeps it: RGBA, row 0 the picture's bottom
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int dst = ((h - 1 - i / w) * w + i % w) * 4;
            byte r, g, b, a = 255;
            switch (nc)
            {
                case 1: r = g = b = comps[0][i]; break;
                case 2: r = g = b = comps[0][i]; a = comps[1][i]; break;
                case 3: r = comps[0][i]; g = comps[1][i]; b = comps[2][i]; break;
                default: r = comps[0][i]; g = comps[1][i]; b = comps[2][i]; a = comps[3][i]; break;
            }
            rgba[dst] = r; rgba[dst + 1] = g; rgba[dst + 2] = b; rgba[dst + 3] = a;
        }
        if (pooled) OpenJpeg.Return(comps);
        int discard = VolumeBuilder.SculptDiscardLevel(w, h);
        for (int i = 0; i < discard && w > 1 && h > 1; i++)
        {
            rgba = TextureStore.HalveRgba(rgba, w, h);
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        var faces = VolumeBuilder.BuildSculpt(prim.PrimData, prim.Sculpt!, rgba, w, h, VolumeBuilder.DetailForLod(Lod),
            components: nc < 3 ? nc : 4, rowsTopDown: false);
        shape.Set(Flatten(faces), null);
    }

    void Sweep(long now)
    {
        if (now - _lastSweep < 30_000) return;
        _lastSweep = now;
        foreach (var (key, s) in _shapes)
            if (now - s.LastUsed > IdleMs && !s.Busy) _shapes.TryRemove(KeyValuePair.Create(key, s));
    }

    public void Stop() => _stop = true;
}
