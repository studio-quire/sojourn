using System.Collections.Concurrent;
using System.Numerics;
using LibreMetaverse;
using LibreMetaverse.Rendering;
using Sojourn.Render.Gpu;
using LVector3 = LibreMetaverse.Vector3;

namespace Sojourn.Render.World;

public sealed class FaceGeometry
{
    public int TeIndex;
    public MeshVertex[] Vertices = Array.Empty<MeshVertex>();
    public uint[] Indices = Array.Empty<uint>();
    public MeshRange Range;
    /// <summary>The face's bounding box in the shape's own (unscaled) frame, centre and half size: where a blended
    /// face sorts (LLDrawable::updateDistance takes each alpha face's centre and extents).</summary>
    public System.Numerics.Vector3 Center, HalfSize;
    /// <summary>Rigged mesh faces: per-vertex joints (indices into the skin's joint names) and weights, uploaded
    /// for the skinning pre-pass.</summary>
    public SkinWeight[]? SkinWeights;
    public StorageRange Weights;
    /// <summary>Positions and triangles kept on the CPU for pointing at things (unrigged prims, sculpts and meshes).</summary>
    public System.Numerics.Vector3[]? PickPositions;
    public uint[]? PickIndices;
    /// <summary>For touches: the vertices' UVs, and on planar-mapped faces the volume's own texture coordinates as
    /// well (the viewer's surface coordinates; the UVs hold the planar projection there).</summary>
    public System.Numerics.Vector2[]? PickUvs, PickSt;
}

/// <summary>Geometry for one shape at one LOD, in unit-scale object space (scaled by the prim's scale at draw time).
/// Shared by every object with the same key.</summary>
public sealed class ShapeGeometry
{
    public required GeometryKey Key;
    public FaceGeometry[] Faces = Array.Empty<FaceGeometry>();
    public Vector3 Min, Max;
    public volatile bool Ready;      // uploaded
    public volatile bool Failed;
    /// <summary>Dropped from the cache (idle eviction or release): its ranges are freed, so holders must re-request.</summary>
    public volatile bool Evicted;
    public int RefCount;
    public long LastUsed;
    /// <summary>The largest screen area (pixels) an object drawn with it covered lately: mesh fetches go largest first.</summary>
    public float Area;
    public ulong Bytes;
    /// <summary>Rigged mesh (ShapeKind.RiggedMesh with a skin section): its skin; vertices already carry the bind
    /// shape matrix, as the viewer bakes it into rigged vertex buffers.</summary>
    public MeshSkinData? Skin;
    public Matrix4x4[]? InverseBind, AltInverseBind;
    /// <summary>Its skin's joints as skeleton indices and their inverse binds, shared with every shape whose are the
    /// same (resolved by the avatar renderer: Avatars.ShareOf).</summary>
    public SkinShare? Share;
}

/// <summary>What a rigged mesh's joint palette is made of: skeleton joint indices (unknown names give mPelvis) and
/// their inverse bind matrices. The pieces of one body, its clothes' and a mesh's LODs mostly have the same, so on
/// an avatar one palette a frame serves all of them.</summary>
public sealed class SkinShare
{
    public required int[] Map;
    public required Matrix4x4[] InverseBind;
}

/// <summary>RiggedMesh: a mesh drawn on an avatar (an attachment); decoded like Mesh, plus its skin when it has one.
/// Flexi: a flexible prim's faces at rest for one number of path points (Lod = detail level, Position.X = render
/// sections' power of two), whose indices its simulated vertices are drawn with (World/Flexi.cs).</summary>
public enum ShapeKind : byte { Prim, Sculpt, Mesh, Tree, Grass, RiggedMesh, Flexi }

/// <summary>Cache key. Trees: ShapeHash = species, Lod = trunk LOD. Grass (one mesh per object, built on the terrain):
/// Asset = object id, ShapeHash = species, Lod = terrain revision, PlanarScale = object scale, Position = its region
/// position.</summary>
public readonly record struct GeometryKey(ShapeKind Kind, UUID Asset, int ShapeHash, int Lod, Vector3 PlanarScale, Vector3 Position = default);

/// <summary>
/// Builds shape geometry on worker threads and uploads it into the shared geometry arenas on the render thread.
/// Prims and sculpts use the viewer's volume code (Geometry/ port) when present, LibreMetaverse's MeshFoundry
/// otherwise; meshes decode through MeshFoundry (LibreMetaverse's port of the viewer's mesh asset decoder).
/// </summary>
public sealed class GeometryCache
{
    readonly ConcurrentDictionary<GeometryKey, ShapeGeometry> _shapes = new();
    readonly ConcurrentQueue<ShapeGeometry> _built = new();
    readonly SemaphoreSlim _gate;
    readonly MeshFoundry _mesher = new();
    public Action<string>? Log { get; set; }
    public int Count => _shapes.Count;
    public int Building => _building;
    int _building;

    public GeometryCache(int threads = 4)
    {
        _gate = new SemaphoreSlim(threads);
        _ = Task.Run(PumpMeshes);
    }

    // --- mesh fetches: prioritised, bounded -----------------------------------------------------------------------
    // Mesh assets download through a queue of their own: at most MaxMeshFetches at once (the viewer's mesh fetch, 8
    // connections), the asset whose objects cover the most screen first, and only assets something asked for in the
    // last few seconds (a teleport leaves the old region's queue behind). Shapes waiting on an asset's data are
    // started once it arrives; a failed download is tried again later rather than given up on. The grid's source
    // fetches them over connections of their own (IRangedAssetSource.MeshAsync), from disk when they're there. Wants
    // nobody has asked about for a minute are dropped, their shapes with them (holders ask again if they come back).
    // Mesh bytes are kept only for a while (MeshBytesBudget) to build each LOD from; after that, from disk again.

    sealed class MeshWant
    {
        public readonly List<(ShapeGeometry Shape, Primitive Prim, IAssetSource Assets, TextureStore Textures)> Shapes = new();
        /// <summary>Where to fetch it from when only its data is wanted (<see cref="MeshCost"/>), no shape yet.</summary>
        public IAssetSource? Assets;
        public float Area;
        public long AreaAt, LastWanted, RetryAt;
        public int Failures;
        public bool Fetching, Done, Dropped;
    }

    readonly ConcurrentDictionary<UUID, MeshWant> _meshWants = new();
    int _meshFetching;
    long _lastWantSweep;
    /// <summary>Mesh downloads at once (the viewer's: 8 connections, Mesh2MaxConcurrentRequests).</summary>
    public int MaxMeshFetches { get; set; } = 8;
    public int MeshesFetching => Volatile.Read(ref _meshFetching);
    /// <summary>Meshes something asked for in the last few seconds that haven't arrived.</summary>
    public int MeshesWaiting
    {
        get
        {
            long now = System.Environment.TickCount64;
            int n = 0;
            foreach (var kv in _meshWants) if (now - kv.Value.LastWanted < 3000) n++;
            return n;
        }
    }

    // mesh bytes lately fetched, to build each LOD from: bounded, oldest out first (they were kept for the whole
    // session, an hour of Second Life is gigabytes of them)
    readonly ConcurrentDictionary<UUID, byte[]> _meshBytes = new();
    readonly ConcurrentQueue<UUID> _meshBytesOrder = new();
    long _meshBytesHeld;
    public long MeshBytesBudget { get; set; } = 256L << 20;

    void KeepMeshBytes(UUID id, byte[] data)
    {
        if (!_meshBytes.TryAdd(id, data)) return;
        Interlocked.Add(ref _meshBytesHeld, data.Length);
        _meshBytesOrder.Enqueue(id);
        while (Interlocked.Read(ref _meshBytesHeld) > MeshBytesBudget && _meshBytesOrder.TryDequeue(out var old))
            if (_meshBytes.TryRemove(old, out var gone)) Interlocked.Add(ref _meshBytesHeld, -gone.Length);
    }

    /// <summary>For the console: what the mesh queue holds and what it costs.</summary>
    public string Describe() =>
        $", {_meshWants.Count} asked for, {Interlocked.Read(ref _meshBytesHeld) >> 20} MB of mesh data held" + (DescribeExtra?.Invoke() ?? "");

    /// <summary>More for the console line from whoever fetches (the grid's source: downloads, LibreMetaverse's queue).</summary>
    public static Func<string?>? DescribeExtra { get; set; }

    /// <summary>A view drew (or wanted to draw) an object with this shape over about <paramref name="area"/> pixels.</summary>
    public void Wanted(ShapeGeometry shape, float area)
    {
        long now = System.Environment.TickCount64;
        if (area >= shape.Area || now - shape.LastUsed > 1000) shape.Area = area;
        if (!shape.Ready && shape.Key.Kind is ShapeKind.Mesh or ShapeKind.RiggedMesh && _meshWants.TryGetValue(shape.Key.Asset, out var w))
            lock (w)
            {
                w.LastWanted = now;
                if (area >= w.Area || now - w.AreaAt > 1000) { w.Area = area; w.AreaAt = now; }
            }
    }

    void QueueMesh(ShapeGeometry shape, Primitive prim, IAssetSource assets, TextureStore textures)
    {
        // the data here lately (another LOD's): decode as before
        if (_meshBytes.TryGetValue(shape.Key.Asset, out var data)) { Start(shape, prim, assets, textures, data); return; }
        Want(shape, prim, assets, textures);
    }

    void Want(ShapeGeometry shape, Primitive prim, IAssetSource assets, TextureStore textures)
    {
        while (true)
        {
            var w = _meshWants.GetOrAdd(shape.Key.Asset, _ => new MeshWant());
            byte[]? arrived;
            lock (w)
            {
                // swept away between the look and the lock: a new one
                if (w.Dropped) continue;
                if (!w.Done)
                {
                    w.Shapes.Add((shape, prim, assets, textures));
                    w.LastWanted = System.Environment.TickCount64;
                    w.Area = MathF.Max(w.Area, shape.Area);
                    return;
                }
                // it arrived meanwhile (and the want left the map with it)
                _meshBytes.TryGetValue(shape.Key.Asset, out arrived);
            }
            if (arrived == null) continue;   // and was already let go of: ask again
            Start(shape, prim, assets, textures, arrived);
            return;
        }
    }

    /// <summary>Wants nobody has asked about for a minute go, and the shapes waiting on them (marked evicted, so whoever
    /// holds one asks again if it's wanted after all).</summary>
    void SweepWants(long now)
    {
        if (now - _lastWantSweep < 5000) return;
        _lastWantSweep = now;
        foreach (var (id, w) in _meshWants)
        {
            if (w.Fetching || now - w.LastWanted < 60_000) continue;
            lock (w)
            {
                if (w.Fetching || w.Done) continue;
                w.Dropped = true;
                foreach (var (s, _, _, _) in w.Shapes)
                {
                    _shapes.TryRemove(KeyValuePair.Create(s.Key, s));
                    s.Evicted = true;
                }
                w.Shapes.Clear();
            }
            _meshWants.TryRemove(KeyValuePair.Create(id, w));
        }
    }

    async Task PumpMeshes()
    {
        var picks = new List<(UUID Id, MeshWant W)>();
        while (true)
        {
            await Task.Delay(20).ConfigureAwait(false);
            try
            {
                long now = System.Environment.TickCount64;
                SweepWants(now);
                int free = MaxMeshFetches - Volatile.Read(ref _meshFetching);
                if (free <= 0 || _meshWants.IsEmpty) continue;
                picks.Clear();
                foreach (var (id, w) in _meshWants)
                    if (!w.Fetching && now - w.LastWanted < 3000 && now >= w.RetryAt) picks.Add((id, w));
                picks.Sort((a, b) => b.W.Area.CompareTo(a.W.Area));
                foreach (var (id, w) in picks)
                {
                    if (free-- <= 0) break;
                    w.Fetching = true;
                    Interlocked.Increment(ref _meshFetching);
                    _ = FetchMesh(id, w);
                }
            }
            catch (Exception e) { Log?.Invoke($"mesh queue: {e.Message}"); }
        }
    }

    async Task FetchMesh(UUID id, MeshWant w)
    {
        IAssetSource assets;
        lock (w) assets = w.Shapes.Count > 0 ? w.Shapes[0].Assets : w.Assets!;
        byte[]? data = null;
        var status = FetchStatus.Retry;
        var retryAfter = TimeSpan.Zero;
        try
        {
            if (assets is IRangedAssetSource ranged)
            {
                var r = await ranged.MeshAsync(id, CancellationToken.None).ConfigureAwait(false);
                (status, data, retryAfter) = (r.Status, r.Data, r.RetryAfter);
            }
            else
            {
                data = await assets.GetMeshAsync(id, CancellationToken.None).ConfigureAwait(false);
                status = data != null ? FetchStatus.Ok : FetchStatus.Retry;
            }
        }
        catch (Exception e) { Log?.Invoke($"mesh {id}: {e.Message}"); }
        finally { Interlocked.Decrement(ref _meshFetching); }
        if (status != FetchStatus.Ok || data is not { Length: > 0 })
        {
            lock (w)
            {
                w.Fetching = false;
                long now = System.Environment.TickCount64;
                if (status == FetchStatus.Unavailable) { w.RetryAt = now + 1000; return; }   // no region to ask yet
                w.Failures++;
                w.RetryAt = now + Math.Max((long)retryAfter.TotalMilliseconds, Math.Min(60_000, 1000L << Math.Min(w.Failures, 6)));
                if (status == FetchStatus.NotFound || w.Failures > 6)
                {
                    foreach (var s in w.Shapes) s.Shape.Failed = true;
                    _meshFailed[id] = 0;
                    w.Done = true;
                    _meshWants.TryRemove(KeyValuePair.Create(id, w));
                    Log?.Invoke(status == FetchStatus.NotFound ? $"mesh {id}: not found" : $"mesh {id}: gave up after {w.Failures} failed downloads");
                }
            }
            return;
        }
        KeepMeshBytes(id, data);
        if (!_meshTris.ContainsKey(id))
        {
            float[] tris = Array.Empty<float>();
            try { tris = MeshHeaderTris(data, out bool rigged); if (rigged) _meshRigged[id] = 0; } catch { }
            _meshTris[id] = tris;
        }
        if (!_meshPhysics.ContainsKey(id))
        {
            MeshPhysics? physics = null;
            try { physics = MeshPhysics.FromAsset(data); } catch { }
            _meshPhysics[id] = physics;
        }
        List<(ShapeGeometry, Primitive, IAssetSource, TextureStore)> waiting;
        lock (w)
        {
            waiting = w.Shapes.ToList();
            w.Done = true;
            _meshWants.TryRemove(KeyValuePair.Create(id, w));
        }
        foreach (var (shape, prim, a, t) in waiting)
            if (!shape.Evicted) Start(shape, prim, a, t, data);
    }

    // --- what a mesh costs, from its header alone (the viewer's LLMeshCostData) -------------------------------------

    readonly ConcurrentDictionary<UUID, float[]?> _meshTris = new();
    readonly ConcurrentDictionary<UUID, byte> _meshFailed = new();

    /// <summary>A mesh's estimated triangles by LOD (lowest to high), from the sizes its header gives each LOD as the
    /// viewer estimates them (LLMeshCostData::init: bytes less 384, at least 16, over 16 bytes a triangle). Null while
    /// its data is on its way: asking fetches it (largest wanted area first, as shapes are) without decoding anything,
    /// so an avatar's attachments can be judged before any of them is built. An empty array if it never came.</summary>
    public float[]? MeshCost(UUID asset, IAssetSource assets, float area)
    {
        if (_meshTris.TryGetValue(asset, out var known)) return known;
        if (_meshFailed.ContainsKey(asset)) return Array.Empty<float>();
        long now = System.Environment.TickCount64;
        for (int tries = 0; tries < 3; tries++)
        {
            var w = _meshWants.GetOrAdd(asset, _ => new MeshWant());
            lock (w)
            {
                if (w.Dropped || w.Done) continue;   // swept away, or just arrived (its cost is known next time)
                w.Assets ??= assets;
                w.LastWanted = now;
                if (area >= w.Area || now - w.AreaAt > 1000) { w.Area = area; w.AreaAt = now; }
                return null;
            }
        }
        return _meshTris.TryGetValue(asset, out known) ? known : null;
    }

    readonly ConcurrentDictionary<UUID, byte> _meshRigged = new();
    readonly ConcurrentDictionary<UUID, MeshPhysics?> _meshPhysics = new();

    /// <summary>A mesh's physics blocks (hull, decomposition, physics model) once its data has arrived; null before, or
    /// if it has none (the local collision model, RegionScene.CollideRay).</summary>
    public MeshPhysics? Physics(UUID asset) => _meshPhysics.TryGetValue(asset, out var p) ? p : null;

    /// <summary>Whether a mesh whose cost is known has a skin (LLVOVolume::isRiggedMeshFast).</summary>
    public bool MeshRigged(UUID asset) => _meshRigged.ContainsKey(asset);

    static float[] MeshHeaderTris(byte[] data, out bool rigged)
    {
        rigged = false;
        using var ms = new MemoryStream(data);
        if (LibreMetaverse.StructuredData.OSDParser.DeserializeLLSDBinary(ms) is not LibreMetaverse.StructuredData.OSDMap header) return Array.Empty<float>();
        int Size(string lod) => header[lod] is LibreMetaverse.StructuredData.OSDMap m ? m["size"].AsInteger() : 0;
        rigged = Size("skin") > 0;
        int high = Size("high_lod"), med = Size("medium_lod"), low = Size("low_lod"), lowest = Size("lowest_lod");
        if (med == 0) med = high;
        if (low == 0) low = med;
        if (lowest == 0) lowest = low;
        return new[] { lowest, low, med, high }.Select(b => MathF.Max(b - 384f, 16f) / 16f).ToArray();
    }

    public static int HashShape(Primitive.ConstructionData d, PCode pcode) => HashCode.Combine(
        HashCode.Combine(d.profileCurve, d.PathCurve, d.PathBegin, d.PathEnd, d.PathScaleX, d.PathScaleY, d.PathShearX, d.PathShearY),
        HashCode.Combine(d.PathTwist, d.PathTwistBegin, d.PathRadiusOffset, d.PathTaperX, d.PathTaperY, d.PathRevolutions, d.PathSkew, d.ProfileBegin),
        HashCode.Combine(d.ProfileEnd, d.ProfileHollow, pcode));

    /// <summary>Get or start building a shape. snapshot is a detached Primitive copy safe to read off-thread.</summary>
    public ShapeGeometry Request(GeometryKey key, Primitive snapshot, IAssetSource assets, TextureStore textures)
    {
        var shape = _shapes.GetOrAdd(key, k => new ShapeGeometry { Key = k });
        shape.LastUsed = System.Environment.TickCount64;
        if (shape.Faces.Length == 0 && !shape.Failed && Interlocked.CompareExchange(ref shape.RefCount, 1, 0) == 0)
        {
            if (key.Kind is ShapeKind.Mesh or ShapeKind.RiggedMesh) QueueMesh(shape, snapshot, assets, textures);
            else Start(shape, snapshot, assets, textures);
        }
        return shape;
    }

    /// <summary>Get or start building a Linden tree or grass mesh (Foliage/ port); build runs on a worker thread.</summary>
    public ShapeGeometry RequestFoliage(GeometryKey key, Func<Sojourn.Render.Foliage.FoliageMesh> build)
    {
        var shape = _shapes.GetOrAdd(key, k => new ShapeGeometry { Key = k });
        shape.LastUsed = System.Environment.TickCount64;
        if (shape.Faces.Length == 0 && !shape.Failed && Interlocked.CompareExchange(ref shape.RefCount, 1, 0) == 0)
        {
            Interlocked.Increment(ref _building);
            _ = Task.Run(() =>
            {
                try
                {
                    FromFoliage(shape, build());
                    Built(shape);
                }
                catch (Exception e)
                {
                    Log?.Invoke($"geometry {shape.Key}: {e.GetType().Name}: {e.Message}");
                    shape.Failed = true;
                }
                finally { Interlocked.Decrement(ref _building); }
            });
        }
        return shape;
    }

    /// <summary>Drop one shape now (its owner changed or left), handing its uploaded ranges to retire.</summary>
    public void Release(GeometryKey key, Action<MeshRange> retire)
    {
        if (!_shapes.TryRemove(key, out var s)) return;
        s.Evicted = true;   // a build still in flight is dropped at upload
        if (s.Ready)
            foreach (var f in s.Faces) { retire(f.Range); if (f.Weights.Count > 0) _freedWeights.Enqueue(f.Weights); }
    }

    static void FromFoliage(ShapeGeometry shape, Sojourn.Render.Foliage.FoliageMesh m)
    {
        var verts = new MeshVertex[m.Positions.Length];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < verts.Length; i++)
        {
            var p = m.Positions[i];
            verts[i] = new MeshVertex { Position = p, Normal = m.Normals[i], Uv = m.TexCoords[i] };
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        var idx = (uint[])m.Indices.Clone();
        Tangents.Generate(verts, idx);
        shape.Faces = new[] { new FaceGeometry { TeIndex = 0, Vertices = verts, Indices = idx } };
        // grass is picked on its blades (RegionScene.GrassHit), trees by the viewer's tetrahedron, which needs no triangles
        if (shape.Key.Kind == ShapeKind.Grass) (shape.Faces[0].PickPositions, shape.Faces[0].PickIndices) = (m.Positions, idx);
        shape.Min = min;
        shape.Max = max;
    }

    void Start(ShapeGeometry shape, Primitive prim, IAssetSource assets, TextureStore textures, byte[]? meshData = null)
    {
        Interlocked.Increment(ref _building);
        _ = Task.Run(async () =>
        {
            try
            {
                FacetedMesh? fm = null;
                var lod = (DetailLevel)Math.Clamp(shape.Key.Lod, 0, 3);
                switch (shape.Key.Kind)
                {
                    case ShapeKind.Prim:
                    {
                        await _gate.WaitAsync().ConfigureAwait(false);
                        try { FromVolume(shape, Sojourn.Render.Geometry.VolumeBuilder.BuildPrim(prim.PrimData, Sojourn.Render.Geometry.VolumeBuilder.DetailForLod(shape.Key.Lod)), prim); }
                        finally { _gate.Release(); }
                        Built(shape);
                        return;
                    }
                    case ShapeKind.Flexi:
                    {
                        await _gate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            var vp = Sojourn.Render.Geometry.VolumeParams.FromConstructionData(prim.PrimData);
                            FromVolume(shape, Sojourn.Render.Geometry.VolumeBuilder.BuildFlexiRest(vp, Sojourn.Render.Geometry.VolumeBuilder.DetailForLod(shape.Key.Lod), (int)shape.Key.Position.X), prim);
                        }
                        finally { _gate.Release(); }
                        Built(shape);
                        return;
                    }
                    case ShapeKind.Sculpt:
                    {
                        while (true)
                        {
                            var tex = textures.Request(shape.Key.Asset, assets, keepCpu: true);
                            // the map is streamed only while something wants it (and an evicted one reloads only then):
                            // asked for again while waiting, so a busy queue can't leave the build waiting for good
                            for (int i = 1; tex.State == TexState.Loading || (tex.State == TexState.Ready && tex.Rgba == null); i++)
                            {
                                if (shape.Evicted) return;
                                await Task.Delay(50).ConfigureAwait(false);
                                if (i % 20 == 0) tex = textures.Request(shape.Key.Asset, assets, keepCpu: true);
                            }
                            await _gate.WaitAsync().ConfigureAwait(false);
                            Sojourn.Render.Geometry.VolumeFace[]? faces;
                            try
                            {
                                faces = SculptFaces(prim, tex, Sojourn.Render.Geometry.VolumeBuilder.DetailForLod(shape.Key.Lod));
                                if (faces != null) FromVolume(shape, faces, prim);
                            }
                            finally { _gate.Release(); }
                            if (faces != null) { Built(shape); return; }
                            if (shape.Evicted) return;
                            await Task.Delay(50).ConfigureAwait(false);
                        }
                    }
                    case ShapeKind.Mesh:
                    case ShapeKind.RiggedMesh:
                    {
                        if ((meshData ?? (_meshBytes.TryGetValue(shape.Key.Asset, out var held) ? held : null)) is not { } data)
                        {
                            // let go of meanwhile: back in the queue (from disk, usually)
                            Want(shape, prim, assets, textures);
                            return;
                        }
                        await _gate.WaitAsync().ConfigureAwait(false);
                        if (shape.Evicted) { _gate.Release(); return; }   // let go of while it waited for a thread
                        try { fm = _mesher.GenerateFacetedMeshMesh(prim, data, lod); }
                        finally { _gate.Release(); }
                        break;
                    }
                    default:
                        shape.Failed = true;
                        return;
                }
                if (fm == null || fm.Faces.Count == 0) { shape.Failed = true; return; }
                Convert(shape, fm, shape.Key.Kind);
                if (shape.Key.Kind == ShapeKind.RiggedMesh && fm.SkinData is { JointNames.Length: > 0 } skin) Rig(shape, fm, skin);
                Built(shape);
            }
            catch (Exception e)
            {
                Log?.Invoke($"geometry {shape.Key}: {e.GetType().Name}: {e.Message}");
                shape.Failed = true;
            }
            finally { Interlocked.Decrement(ref _building); }
        });
    }

    // The viewer fetches big sculpt maps at a reduced J2K level (SculptDiscardLevel); we halve the decoded image.
    // Null when the map's pixels aren't there after all (let go, or decoded again, while the build waited for a thread):
    // the caller waits for them again. Only a missing asset gets the viewer's visible sphere placeholder; it used to be
    // given for pixels merely let go too, and kept as that detail level's shape.
    static Sojourn.Render.Geometry.VolumeFace[]? SculptFaces(Primitive prim, SceneTexture tex, float detail)
    {
        if (tex.State == TexState.Missing)
            return Sojourn.Render.Geometry.VolumeBuilder.BuildSculpt(prim.PrimData, prim.Sculpt!, ReadOnlySpan<byte>.Empty, 0, 0, detail,
                components: 0, rowsTopDown: false, missingAsset: true);
        var rgba = tex.Rgba;
        int w = tex.Width, h = tex.Height;
        // (a decode sets the size before the pixels: a size that doesn't fit the pixels is one caught in between)
        if (rgba == null || (long)w * h * 4 != rgba.Length || !ReferenceEquals(rgba, tex.Rgba)) return null;
        int discard = Sojourn.Render.Geometry.VolumeBuilder.SculptDiscardLevel(w, h);
        for (int i = 0; i < discard && w > 1 && h > 1; i++)
        {
            rgba = TextureStore.HalveRgba(rgba, w, h);
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        return Sojourn.Render.Geometry.VolumeBuilder.BuildSculpt(prim.PrimData, prim.Sculpt!, rgba, w, h, detail,
            // the pixels are RGBA whatever the map had; under 3 channels the viewer treats the map as empty
            components: tex.Components < 3 ? tex.Components : 4, rowsTopDown: false);
    }

    /// <summary>Viewer volume faces to our vertices. UVs: the volume's own, or planar (needs the object scale,
    /// which is then part of the geometry key); the TE repeat/offset/rotation runs in the shader.</summary>
    static void FromVolume(ShapeGeometry shape, Sojourn.Render.Geometry.VolumeFace[] vfaces, Primitive prim)
    {
        var faces = new List<FaceGeometry>(vfaces.Length);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var scale = shape.Key.PlanarScale;
        for (int fi = 0; fi < vfaces.Length; fi++)
        {
            var vf = vfaces[fi];
            if (vf.Indices.Length < 3) continue;
            bool planar = scale != Vector3.Zero && IsPlanar(prim, vf.TeIndex);
            var verts = new MeshVertex[vf.Positions.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var p = vf.Positions[i];
                verts[i] = new MeshVertex
                {
                    Position = p,
                    Normal = vf.Normals[i],
                    Tangent = i < vf.Tangents.Length ? vf.Tangents[i] : new Vector4(1, 0, 0, 1),
                    Uv = planar ? Sojourn.Render.Geometry.TexGen.Planar(vf.Normals[i], p * scale) : vf.TexCoords[i],
                };
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            faces.Add(new FaceGeometry { TeIndex = vf.TeIndex, Vertices = verts, Indices = vf.Indices, PickSt = planar ? vf.TexCoords : null });
        }
        shape.Faces = faces.ToArray();
        shape.Min = faces.Count > 0 ? min : Vector3.Zero;
        shape.Max = faces.Count > 0 ? max : Vector3.Zero;
    }

    public static bool IsPlanar(Primitive prim, int te)
    {
        var f = prim.Textures?.GetFace((uint)Math.Max(0, te));
        return f != null && f.TexMapType == MappingType.Planar;
    }

    static void Convert(ShapeGeometry shape, FacetedMesh fm, ShapeKind kind)
    {
        var faces = new List<FaceGeometry>(fm.Faces.Count);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int fi = 0; fi < fm.Faces.Count; fi++)
        {
            var f = fm.Faces[fi];
            if (f.Vertices == null || f.Indices == null || f.Indices.Count < 3) continue;
            var verts = new MeshVertex[f.Vertices.Count];
            for (int i = 0; i < verts.Length; i++)
            {
                var v = f.Vertices[i];
                var p = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                verts[i] = new MeshVertex
                {
                    Position = p,
                    Normal = new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z),
                    Uv = new Vector2(v.TexCoord.X, v.TexCoord.Y),
                };
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var idx = new uint[f.Indices.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = f.Indices[i];
            Tangents.Generate(verts, idx);
            faces.Add(new FaceGeometry { TeIndex = kind == ShapeKind.Mesh ? fi : (f.ID >= 0 && kind == ShapeKind.Prim ? fi : fi), Vertices = verts, Indices = idx });
        }
        shape.Faces = faces.ToArray();
        shape.Min = min;
        shape.Max = max;
    }

    static Matrix4x4 Mat(float[] m, int offset) => new(
        m[offset + 0], m[offset + 1], m[offset + 2], m[offset + 3], m[offset + 4], m[offset + 5], m[offset + 6], m[offset + 7],
        m[offset + 8], m[offset + 9], m[offset + 10], m[offset + 11], m[offset + 12], m[offset + 13], m[offset + 14], m[offset + 15]);

    /// <summary>A rigged mesh: positions times the bind shape matrix, normals times its inverse transpose
    /// (LLFace::getGeometryVolume for rigged faces), per-vertex weights kept (LLVolumeFace weights: up to four,
    /// normalized like objectSkinV), the skin's matrices as row-vector Matrix4x4 (LLSD rows, translation last).</summary>
    static void Rig(ShapeGeometry shape, FacetedMesh fm, MeshSkinData skin)
    {
        var bsm = Mat(skin.BindShapeMatrix, 0);
        Matrix4x4.Invert(bsm, out var inv);
        var nrm = Matrix4x4.Transpose(inv);
        int joints = Math.Min(skin.JointNames.Length, 110);   // LL_MAX_JOINTS_PER_MESH_OBJECT
        shape.InverseBind = Enumerable.Range(0, joints).Select(j => j * 16 + 15 < skin.InverseBindMatrices.Length ? Mat(skin.InverseBindMatrices, j * 16) : Matrix4x4.Identity).ToArray();
        if (skin.AltInverseBindMatrices is { Length: > 0 } alt && alt.Length >= skin.JointNames.Length * 16)
            shape.AltInverseBind = Enumerable.Range(0, joints).Select(j => Mat(alt, j * 16)).ToArray();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int fi = 0;
        foreach (var f in shape.Faces)
        {
            // faces are converted one per FacetedMesh face with geometry, in order
            while (fi < fm.Faces.Count && (fm.Faces[fi].Vertices == null || fm.Faces[fi].Indices == null || fm.Faces[fi].Indices.Count < 3)) fi++;
            Face? src = fi < fm.Faces.Count ? fm.Faces[fi++] : null;
            for (int i = 0; i < f.Vertices.Length; i++)
            {
                ref var v = ref f.Vertices[i];
                v.Position = Vector3.Transform(v.Position, bsm);
                v.Normal = Vector3.Normalize(Vector3.TransformNormal(v.Normal, nrm));
                var t = Vector3.TransformNormal(new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z), bsm);
                if (t.LengthSquared() > 0) v.Tangent = new Vector4(Vector3.Normalize(t), v.Tangent.W);
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
            if (src?.Weights is not { } w || w.Count != f.Vertices.Length) continue;
            var sw = new SkinWeight[w.Count];
            Span<int> jj = stackalloc int[4];
            Span<float> ww = stackalloc float[4];
            for (int i = 0; i < sw.Length; i++)
            {
                var x = w[i];
                jj[0] = Math.Min(x.Joint0, joints - 1); jj[1] = Math.Min(x.Joint1, joints - 1);
                jj[2] = Math.Min(x.Joint2, joints - 1); jj[3] = Math.Min(x.Joint3, joints - 1);
                float sum = x.Weight0 + x.Weight1 + x.Weight2 + x.Weight3;
                if (sum <= 0) { ww[0] = 1; ww[1] = ww[2] = ww[3] = 0; }
                else { ww[0] = x.Weight0 / sum; ww[1] = x.Weight1 / sum; ww[2] = x.Weight2 / sum; ww[3] = x.Weight3 / sum; }
                sw[i] = SkinWeight.From(jj, ww);
            }
            f.SkinWeights = sw;
        }
        shape.Skin = skin;
        if (min.X <= max.X) { shape.Min = min; shape.Max = max; }
    }

    readonly ConcurrentQueue<StorageRange> _freedWeights = new();

    /// <summary>Skin weight blocks of shapes dropped since the last call (free them once no frame uses them).</summary>
    public IEnumerable<StorageRange> DrainFreedWeights()
    {
        while (_freedWeights.TryDequeue(out var r)) yield return r;
    }

    /// <summary>Build thread: what's left of a shape before its upload, done here rather than on the render thread (which
    /// only copies it into the pool): each face's box, its size, and the copies kept for pointing at it.</summary>
    void Built(ShapeGeometry shape)
    {
        foreach (var f in shape.Faces)
        {
            shape.Bytes += (ulong)(f.Vertices.Length * PipelineBuilder.VertexStride + f.Indices.Length * 4);
            if (f.Vertices.Length > 0)
            {
                var lo = f.Vertices[0].Position;
                var hi = lo;
                foreach (ref readonly var vx in f.Vertices.AsSpan())
                {
                    lo = System.Numerics.Vector3.Min(lo, vx.Position);
                    hi = System.Numerics.Vector3.Max(hi, vx.Position);
                }
                (f.Center, f.HalfSize) = ((lo + hi) * 0.5f, (hi - lo) * 0.5f);
            }
            if (shape.Key.Kind is ShapeKind.Prim or ShapeKind.Sculpt or ShapeKind.Mesh)
            {
                var pos = new System.Numerics.Vector3[f.Vertices.Length];
                var uvs = new System.Numerics.Vector2[f.Vertices.Length];
                for (int i = 0; i < pos.Length; i++) { pos[i] = f.Vertices[i].Position; uvs[i] = f.Vertices[i].Uv; }
                f.PickPositions = pos;
                f.PickUvs = uvs;
                f.PickIndices = f.Indices;
            }
        }
        _built.Enqueue(shape);
    }

    /// <summary>Render thread: queue uploads for finished builds (bounded by bytes per call).</summary>
    public int ProcessUploads(GeometryPool pool, ulong maxBytes = 8UL << 20, StoragePool? weights = null, double maxMs = 1.0, bool forceOne = true)
    {
        int n = 0;
        ulong bytes = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // a byte and a time budget a frame (the first one over them too when forceOne: loading never stops)
        int skipped = 0;
        while ((n == 0 && forceOne || bytes < maxBytes && System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds < maxMs)
               && !_built.IsEmpty)
        {
            if (!_built.TryDequeue(out var shape)) break;
            if (shape.Evicted) continue;   // released while building or waiting (its vertices go with it)
            // too big for what's left of the budget: to the back, so smaller ones still go
            if (!(n == 0 && forceOne) && bytes + shape.Bytes > maxBytes)
            {
                _built.Enqueue(shape);
                if (++skipped >= 16) break;
                continue;
            }
            foreach (var f in shape.Faces)
            {
                f.Range = pool.Add(f.Vertices, f.Indices);
                if (f.SkinWeights != null && weights != null)
                {
                    f.Weights = weights.Add<SkinWeight>(f.SkinWeights);
                    f.SkinWeights = null;
                }
                f.Vertices = Array.Empty<MeshVertex>();   // the full CPU copy is no longer needed
                f.Indices = Array.Empty<uint>();
            }
            bytes += shape.Bytes;
            shape.Ready = true;
            n++;
        }
        return n;
    }

    public ulong ResidentBytes => _shapes.Values.Where(s => s.Ready).Aggregate(0UL, (a, s) => a + s.Bytes);

    /// <summary>Drop shapes not requested for idleMs, returning their ranges for deferred freeing. Shapes not uploaded yet
    /// go too: built ones waited in the upload queue with all their vertices (under lag, uploads at a quarter or less,
    /// a left place's would wait there for minutes and then be uploaded for nobody; 2026-10-28, ~250 MB of the queue
    /// in a gcdump), and their upload is skipped; one still building or fetching is dropped when it gets there. Whoever
    /// asks for it again starts it again.</summary>
    public List<MeshRange> Evict(long idleMs)
    {
        var freed = new List<MeshRange>();
        long now = System.Environment.TickCount64;
        foreach (var kv in _shapes)
        {
            var s = kv.Value;
            if (now - s.LastUsed < idleMs || !s.Ready && s.Failed) continue;
            if (!_shapes.TryRemove(KeyValuePair.Create(kv.Key, s))) continue;
            s.Evicted = true;
            // (Ready is set on this thread, the render thread, so a shape not ready here has no ranges yet)
            if (s.Ready)
                foreach (var f in s.Faces) { freed.Add(f.Range); if (f.Weights.Count > 0) _freedWeights.Enqueue(f.Weights); }
        }
        return freed;
    }
}

/// <summary>Per-vertex tangents by accumulating per-triangle UV derivatives (w = bitangent handedness).</summary>
public static class Tangents
{
    public static void Generate(MeshVertex[] v, uint[] idx)
    {
        var tan = new Vector3[v.Length];
        var bit = new Vector3[v.Length];
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            uint a = idx[i], b = idx[i + 1], c = idx[i + 2];
            var p0 = v[a].Position; var p1 = v[b].Position; var p2 = v[c].Position;
            var w0 = v[a].Uv; var w1 = v[b].Uv; var w2 = v[c].Uv;
            var e1 = p1 - p0; var e2 = p2 - p0;
            float x1 = w1.X - w0.X, x2 = w2.X - w0.X, y1 = w1.Y - w0.Y, y2 = w2.Y - w0.Y;
            float r = x1 * y2 - x2 * y1;
            if (MathF.Abs(r) < 1e-12f) continue;
            r = 1f / r;
            var sdir = (e1 * y2 - e2 * y1) * r;
            var tdir = (e2 * x1 - e1 * x2) * r;
            tan[a] += sdir; tan[b] += sdir; tan[c] += sdir;
            bit[a] += tdir; bit[b] += tdir; bit[c] += tdir;
        }
        for (int i = 0; i < v.Length; i++)
        {
            var n = v[i].Normal;
            var t = tan[i];
            t = t - n * Vector3.Dot(n, t);
            if (t.LengthSquared() < 1e-12f)
            {
                var axis = MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
                t = Vector3.Cross(n, axis);
            }
            t = Vector3.Normalize(t);
            float w = Vector3.Dot(Vector3.Cross(n, t), bit[i]) < 0 ? -1f : 1f;
            v[i].Tangent = new Vector4(t, w);
        }
    }
}
