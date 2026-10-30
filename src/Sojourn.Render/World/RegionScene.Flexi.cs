using System.Diagnostics;
using LibreMetaverse;
using Sojourn.Render.Frame;
using Sojourn.Render.Geometry;
using Sojourn.Render.Gpu;

namespace Sojourn.Render.World;

/// <summary>A flexible prim's shape from one simulation step: its faces' vertices in the prim's frame without its scale
/// (metres), for the detail level and render sections it was built at, and how far its vertices reach from the prim's
/// centre (its cull sphere).</summary>
public sealed class FlexiResult
{
    public required int Lod, RenderRes;
    public required FlexiFace[] Faces;
    public float Radius;
    /// <summary>The rest shape (ShapeKind.Flexi) whose indices it was checked against, and whether every face matched.</summary>
    internal ShapeGeometry? CheckedWith;
    internal bool Matches;

    public FlexiFace? Face(int te)
    {
        foreach (var f in Faces) if (f.Geo.TeIndex == te) return f;
        return null;
    }
}

/// <summary>One face of a flexible prim's shape: its box (for sorting when blended) and its vertices.</summary>
public sealed record FlexiFace(FaceGeometry Geo, MeshVertex[] Vertices);

/// <summary>A flexible prim's simulation and when it was last stepped (llflexibleobject.cpp doIdleUpdate's state).</summary>
internal sealed class FlexiState(uint id)
{
    public readonly FlexiSim Sim = new();
    public readonly uint Id = id;
    public int RenderRes = -1;
    public ulong LastFrame;
    public volatile FlexiResult? Result;
    public int Building;
    public object? ShapeRef;
    public VolumeParams? Params;
    public Vector3 LastAnchor = new(float.NaN);
    /// <summary>What its steps build into (the profile, mesh and faces), and its vertices' three sets used in turn.</summary>
    public readonly FlexiBuffers Buffers = new();
    public readonly List<MeshVertex[]>?[] Ring = new List<MeshVertex[]>?[3];
    public int Gen;
    public long DebugAt;
}

public sealed partial class RegionScene
{
    // Flexible prims, as the viewer simulates them (llflexibleobject.cpp): only in the main view, only while in view
    // and over 256 pixels; a step every 1-32 sixtieths of a second by how much of the screen the prim covers, the
    // steps of one linkset's prims in the same sixtieth, its sections fewer when it's small on screen. A step (the
    // chain, then the volume swept along it) runs on the thread pool; the prim is drawn with the latest finished one,
    // its vertices copied each frame through the skinning pre-pass (DrawList.AddFlexi) and its indices those of its
    // rest shape at that detail (ShapeKind.Flexi). Until the first step is in, and with flexible prims off, it's drawn
    // straight, as before.

    static readonly bool FlexiDebug = System.Environment.GetEnvironmentVariable("SOJOURN_FLEXI_DEBUG") == "1";
    static long _flexiIds;
    static int _flexiJobs;
    static readonly Stopwatch FlexiClock = Stopwatch.StartNew();
    /// <summary>Steps running at once, across every scene: beyond it a step waits for the next frame.</summary>
    static readonly int MaxFlexiJobs = Math.Max(2, System.Environment.ProcessorCount);
    const float SecPerFlexiFrame = 1f / 60f;   // SEC_PER_FLEXI_FRAME
    const float FlexiUpdateFactor = 1f;        // LLVolumeImplFlexible::sUpdateFactor (RenderFlexiTimeFactor)

    /// <summary>A flexible prim: the flexible path (0x80) with flexible data, as the viewer's setVolume makes one.</summary>
    public static bool IsFlexible(Primitive p) =>
        p.Flexible != null && ((byte)p.PrimData.PathCurve & 0xF0) == VolumeConsts.PathFlexible;

    bool FlexiApplies(DrawCtx ctx, SceneObject obj) =>
        obj.Kind == ShapeKind.Prim && ctx.HudPx == 0 && ctx.V.LodView == null && (_world.Quality?.Flexi ?? true) && IsFlexible(obj.Prim);

    /// <summary>A flexible prim drawn in the main view: its simulation stepped when it's due, and its latest shape with
    /// the rest shape whose indices it's drawn with, when both are here; null: draw it straight.</summary>
    (FlexiResult Result, ShapeGeometry RestShape)? FlexiFor(DrawCtx ctx, SceneObject obj, Vector3 pos, Quaternion rot, Vector3 scale, int lod,
        bool visible, float dist)
    {
        if (!FlexiApplies(ctx, obj))
        {
            if (ctx.V.LodView == null && ctx.HudPx == 0) obj.Flexi = null;   // off, or no longer flexible: straight from now on
            return null;
        }
        var prim = obj.Prim;
        var fs = obj.Flexi ??= new FlexiState((uint)Interlocked.Increment(ref _flexiIds));
        if (!ReferenceEquals(fs.ShapeRef, prim.PrimData) || fs.Params == null)
        {
            fs.ShapeRef = prim.PrimData;
            fs.Params = VolumeParams.FromConstructionData(prim.PrimData);
        }
        ScheduleFlexi(ctx, obj, fs, pos, rot, scale, lod, visible, dist);
        if (FlexiDebug && System.Environment.TickCount64 - fs.DebugAt > 2000)
        {
            fs.DebugAt = System.Environment.TickCount64;
            var r = fs.Result;
            _world.Log?.Invoke($"flexi {prim.LocalID}: pos {pos} rot {rot} visible {visible} area {ctx.ObjArea:0} res {fs.RenderRes} result {(r == null ? "none" : $"lod {r.Lod} res {r.RenderRes} faces {r.Faces.Length} matches {r.Matches} tip {(r.Faces.Length > 0 ? r.Faces[0].Vertices[^1].Position : default)}")}");
        }

        if (fs.Result is not { } res) return null;
        var rest = _world.Geometry.Request(new GeometryKey(ShapeKind.Flexi, UUID.Zero, obj.ShapeHash, res.Lod, Vector3.Zero, new Vector3(res.RenderRes, 0, 0)),
            obj.Snapshot ?? prim, ctx.Assets, _world.Textures);
        if (FlexiDebug && fs.DebugAt == System.Environment.TickCount64) _world.Log?.Invoke($"  rest ready {rest.Ready} evicted {rest.Evicted} failed {rest.Failed} capture {ctx.Capture != null} flags {ctx.InstFlags}");
        if (!rest.Ready || rest.Evicted) return null;
        if (!ReferenceEquals(res.CheckedWith, rest))
        {
            // its faces line up with the rest shape's, vertex for vertex (they're built by the same code from the same
            // profile and point count): checked once, drawn straight if not
            bool ok = rest.Faces.Length == res.Faces.Length;
            foreach (var f in rest.Faces)
                ok &= res.Face(f.TeIndex) is { } rf && rf.Vertices.Length == f.Range.VertexCount && rf.Vertices.Length <= FlexiMaxVertices;
            (res.CheckedWith, res.Matches) = (rest, ok);
            if (!ok) _world.Log?.Invoke($"flexible prim {prim.LocalID}: its simulated faces don't match its rest shape's; drawn straight");
        }
        return res.Matches ? (res, rest) : null;
    }

    /// <summary>A face's vertex limit for the copy (the weights the pre-pass reads for them are one shared block).</summary>
    public const int FlexiMaxVertices = 8192;

    /// <summary>doIdleUpdate: whether a step is due this frame, and if so, started.</summary>
    void ScheduleFlexi(DrawCtx ctx, SceneObject obj, FlexiState fs, Vector3 pos, Quaternion rot, Vector3 scale, int lod, bool visible, float dist)
    {
        var v = ctx.V;
        double now = FlexiClock.Elapsed.TotalSeconds;
        ulong frame = (ulong)(now / SecPerFlexiFrame);
        bool due = false;
        if (fs.RenderRes == -1) due = true;
        else
        {
            float area = ctx.ObjArea;
            float screen = (float)v.Width * v.Height;
            // far flexies are stepped rarely, near ones often (at most every 32 sixtieths: MAINT-1890)
            float p = area > 0 ? screen * 0.01f / (area * (FlexiUpdateFactor + 1f)) : 32f;
            uint period = (uint)Math.Clamp(MathF.Min(MathF.Max(p, 0f), 64f) + 1f, 1f, 32f);
            if (visible)
            {
                if (Volatile.Read(ref fs.Building) == 0 && area > 256f)
                {
                    // a linkset's prims are stepped in the same sixtieth, others spread over the period
                    ulong delay = (frame + FlexiThrottleId(obj)) % period;
                    if ((delay == 0 && fs.LastFrame < frame) || fs.LastFrame + period < frame || fs.LastFrame > frame)
                    {
                        fs.LastFrame = frame - delay;
                        due = true;
                    }
                }
            }
            else fs.LastFrame = frame;
        }
        if (!due || Volatile.Read(ref fs.Building) != 0 || Volatile.Read(ref _flexiJobs) >= MaxFlexiJobs) return;

        var attrs = FlexiParams.From(obj.Prim.Flexible!);
        var (renderRes, simRes) = FlexiSim.Resolution(attrs.Softness, scale.Z, dist);
        fs.RenderRes = renderRes;
        var job = new FlexiJob(fs, fs.Params!, attrs, obj.Snapshot ?? obj.Prim, pos, rot, scale, lod, renderRes, simRes, dist, now, ctx.Sim);
        Volatile.Write(ref fs.Building, 1);
        Interlocked.Increment(ref _flexiJobs);
        ThreadPool.UnsafeQueueUserWorkItem(static j =>
        {
            long t0 = Stopwatch.GetTimestamp();
            try { j.Fs.Result = j.Run(); }
            catch (Exception e) { j.Log?.Invoke($"flexible prim: {e.GetType().Name}: {e.Message}"); }
            finally
            {
                Interlocked.Decrement(ref _flexiJobs);
                Volatile.Write(ref j.Fs.Building, 0);
                CountFlexiStep(Stopwatch.GetTimestamp() - t0, j.Log);
            }
        }, job with { Log = _world.Log }, preferLocal: false);
    }

    static long _flexiSteps, _flexiTicks, _flexiReportAt;

    /// <summary>What the steps cost, said every 10 s while any run: how many, and the worker time they took.</summary>
    static void CountFlexiStep(long ticks, Action<string>? log)
    {
        Interlocked.Increment(ref _flexiSteps);
        Interlocked.Add(ref _flexiTicks, ticks);
        long now = System.Environment.TickCount64, at = Volatile.Read(ref _flexiReportAt);
        if (at == 0) { Interlocked.CompareExchange(ref _flexiReportAt, now, 0); return; }
        if (now - at < 10_000 || Interlocked.CompareExchange(ref _flexiReportAt, now, at) != at) return;
        long n = Interlocked.Exchange(ref _flexiSteps, 0), t = Interlocked.Exchange(ref _flexiTicks, 0);
        double ms = t * 1000.0 / Stopwatch.Frequency;
        log?.Invoke($"flexible prims: {n / ((now - at) / 1000.0):0} steps a second, {ms / ((now - at) / 1000.0):0.0} ms of worker time a second ({(n > 0 ? ms * 1000 / n : 0):0} us each)");
    }

    /// <summary>The viewer's throttling id: a root's own, a child's its parent's (0 when the parent isn't flexible).</summary>
    uint FlexiThrottleId(SceneObject obj)
    {
        var prim = obj.Prim;
        if (prim.ParentID == 0 || obj.Owner == prim.ParentID) return obj.Flexi!.Id;
        return _objects.TryGetValue(prim.ParentID, out var parent) && parent.Flexi is { } pf ? pf.Id : 0;
    }

    sealed record FlexiJob(FlexiState Fs, VolumeParams Params, FlexiParams Attrs, Primitive Prim, Vector3 Pos, Quaternion Rot, Vector3 Scale,
        int Lod, int RenderRes, int SimRes, float Dist, double Now, Simulator Sim)
    {
        public Action<string>? Log { get; init; }

        public FlexiResult Run()
        {
            var sim = Fs.Sim;
            // moved a long way at once (a teleport, a crossing, sitting down far off): straight again from the new place
            var anchor = sim.AnchorOf(Pos, Rot, Scale);
            if (!(Vector3.Distance(anchor, Fs.LastAnchor) < 8f)) sim.Reset();
            Fs.LastAnchor = anchor;
            Span<FlexiPathPoint> path = stackalloc FlexiPathPoint[(1 << FlexiSim.MaxSections) + 1];
            var region = Sim;
            int n = sim.Step(Now, Pos, Rot, Scale, Params, Attrs, SimRes, RenderRes, Dist, Attrs.Wind > 0.001f ? p => Particles.RegionWind.At(region, p) : null, path);
            var vfaces = VolumeBuilder.BuildFlexi(Params, VolumeBuilder.DetailForLod(Lod), path[..n], Fs.Buffers);
            var faces = new List<FlexiFace>(vfaces.Length);
            // the vertices go into one of three sets kept in turn: a frame being recorded may still be reading the last
            // result's (DrawList.AddFlexi), never the one before it
            var ring = Fs.Ring[Fs.Gen++ % Fs.Ring.Length] ??= new List<MeshVertex[]>();
            int slot = 0;
            float r2 = 0;
            foreach (var vf in vfaces)
            {
                if (vf.Indices.Length < 3) continue;   // as GeometryCache.FromVolume leaves them out
                // planar texture coordinates from the positions as they are (the viewer takes a flexible volume's scale
                // as 1: isVolumeGlobal)
                bool planar = GeometryCache.IsPlanar(Prim, vf.TeIndex);
                if (ring.Count <= slot) ring.Add(Array.Empty<MeshVertex>());
                var verts = ring[slot].Length == vf.Positions.Length ? ring[slot] : ring[slot] = new MeshVertex[vf.Positions.Length];
                slot++;
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                for (int i = 0; i < verts.Length; i++)
                {
                    var p = vf.Positions[i];
                    verts[i] = new MeshVertex
                    {
                        Position = p, Normal = vf.Normals[i],
                        Tangent = i < vf.Tangents.Length ? vf.Tangents[i] : new Vector4(1, 0, 0, 1),
                        Uv = planar ? TexGen.Planar(vf.Normals[i], p) : vf.TexCoords[i],
                    };
                    lo = Vector3.Min(lo, p);
                    hi = Vector3.Max(hi, p);
                    r2 = MathF.Max(r2, p.LengthSquared());
                }
                var geo = new FaceGeometry { TeIndex = vf.TeIndex, Center = (lo + hi) * 0.5f, HalfSize = (hi - lo) * 0.5f };
                faces.Add(new FlexiFace(geo, verts));
            }
            return new FlexiResult { Lod = Lod, RenderRes = RenderRes, Faces = faces.ToArray(), Radius = MathF.Sqrt(r2) };
        }
    }

    /// <summary>A flexible prim's instance this frame: its place and turn without its scale (the vertices are in metres).</summary>
    static GpuInstance FlexiInstance(Primitive prim, Vector3 worldPos, Quaternion rot, uint flags)
    {
        var model = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(worldPos);
        return new GpuInstance
        {
            Model = model,
            N0 = new Vector4(model.M11, model.M12, model.M13, 0),
            N1 = new Vector4(model.M21, model.M22, model.M23, 0),
            N2 = new Vector4(model.M31, model.M32, model.M33, 0),
            ObjectId = prim.LocalID,
            Flags = flags,
        };
    }

    /// <summary>One face of a flexible prim drawn from its simulated shape; false if it has none for this face.</summary>
    static bool EmitFlexiFace(DrawCtx ctx, (FlexiResult Result, ShapeGeometry RestShape) flexi, int te, in GpuInstance inst, in GpuMaterial mat,
        FaceClass fc, float glow, Vector3 worldPos, bool visible, int casterMask, Vector4 bounds)
    {
        if (flexi.Result.Face(te) is not { } f) return false;
        FaceGeometry? rest = null;
        foreach (var r in flexi.RestShape.Faces) if (r.TeIndex == te) { rest = r; break; }
        if (rest == null || rest.Range.IsEmpty) return false;
        var range = ctx.Dl.AddFlexi(f.Vertices, rest.Range);
        EmitFace(ctx.Dl, ctx.V, f.Geo, range, inst, mat, fc, glow, worldPos, ctx.Axis, visible, casterMask, bounds, into: ctx.Capture,
            group: ctx.AlphaGroup);
        return true;
    }
}
