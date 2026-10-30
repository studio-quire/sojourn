// Avatars as the Second Life viewer draws them: the system body (head, upper and lower body, eyes, eyelashes, hair,
// skirt) shaped by the avatar's visual params, posed by its animations, skinned on the GPU and textured with its
// bakes; a loading cloud until its appearance and bakes have arrived. Rules from newview/llvoavatar.cpp
// (renderSkinned / renderTransparent / renderRigid, isTextureVisible, updateRootPositionAndRotation, updateJointLODs,
// idleCalcNameTagPosition), newview/lldrawpoolavatar.cpp (passes, cutoffs, shadows), newview/llviewerjointmesh.cpp
// (uploadJointMatrices), llappearance/llavatarappearancedefines.cpp (mesh -> bake). LGPL 2.1, Linden Research, Inc.
using System.Diagnostics;
using LibreMetaverse;
using Sojourn.Render.Bodies;
using Sojourn.Render.Frame;
using Sojourn.Render.Geometry;
using Sojourn.Render.Gpu;
using Sojourn.Render.Shadows;

namespace Sojourn.Render.World;

/// <summary>Where avatar bakes come from when they aren't ordinary assets: Second Life's server bakes are served by
/// the region's appearance service by avatar, bake and texture id (AssetManager.RequestServerBakedImageAsync). An
/// asset source that also implements this is asked for bakes this way.</summary>
public interface IBakeSource
{
    /// <param name="textureEntry">The avatar texture entry of the bake (8 head, 9 upper, 10 lower, 11 eyes, 19 skirt,
    /// 20 hair, 40-44 extended).</param>
    Task<byte[]?> GetBakeAsync(UUID avatar, int textureEntry, UUID texture, CancellationToken ct);
}

/// <summary>An asset source view that fetches one avatar bake through <see cref="IBakeSource"/>.</summary>
sealed class BakeAssets(IBakeSource bakes, UUID avatar, int te) : IAssetSource
{
    readonly IAssetSource _inner = (IAssetSource)bakes;
    public Task<byte[]?> GetTextureAsync(UUID id, CancellationToken ct) => bakes.GetBakeAsync(avatar, te, id, ct);
    public Task<byte[]?> GetMeshAsync(UUID id, CancellationToken ct) => _inner.GetMeshAsync(id, ct);
    public Task<byte[]?> GetAssetAsync(UUID id, AssetType type, CancellationToken ct) => _inner.GetAssetAsync(id, type, ct);
}

/// <summary>Every avatar the renderer draws; one per RenderWorld (render thread only).</summary>
public sealed partial class Avatars
{
    public static readonly UUID ImgDefaultAvatar = new("c228d1cf-4b5d-4ba8-84f4-899a0796aa97");
    public static readonly UUID ImgDefault = new("d2114404-dd59-4a4d-8e6c-49359e91bbf0");
    public static readonly UUID ImgInvisible = new("3a367d1c-bef1-6d43-7595-e88c1e3aadb3");

    const int TeHead = 8, TeUpper = 9, TeLower = 10, TeEyes = 11, TeSkirt = 19, TeHair = 20;

    readonly RenderWorld _world;
    readonly CharacterData _cd;
    readonly MotionLibrary _motions;
    readonly Vector3[] _skinOffset;      // totalSkinOffset per joint
    readonly Dictionary<UUID, Body> _bodies = new();
    readonly Dictionary<MeshDef, MeshGpu> _meshGpu = new();
    StorageRange _rigidWeights;
    MeshRange _puff;
    bool _shared;
    long _lastSweep;

    sealed class MeshGpu
    {
        public MeshRange Indices;      // shared by every avatar (vertices come from the skinning pre-pass)
        public ulong Weights;
        public uint VertexCount;
    }

    sealed class Body
    {
        public required UUID Id;
        public long LastUsed;
        public required AvatarAnimator Anim;
        public required SkeletonPose Pose;
        public BodyShape? Shape;
        public int ShapeKey, LookKey;      // the whole shape's (with animated morphs), and without them
        public long ShapeBuiltAt;
        public Vector3[]? RestPos, RestScale;     // the shape's joints with rigged mesh overrides
        public float PelvisToFoot, BodyHeight, PelvisFixup;
        public int Hidden;                         // system meshes hidden by bakes on mesh (bit per MeshOrder entry)
        public int WornKey;                        // what WornState last saw (its attachments, their rigged shapes) and on which shape
        public BodyShape? WornShape;
        public Vector3 Center;                     // animated extents this frame (view frame) and their diagonal
        public float Diagonal;
        public StorageRange Verts;
        public readonly Dictionary<MeshDef, uint> Offsets = new();
        // the person's own body in the headset (PoseSelf): smoothed facing, eye height and scale; joints not shown
        public float SelfYaw, SelfEye, SelfScale;
        public long SelfAt;
        public bool[]? HiddenJoints;
        public SkeletonPose? MirrorPose;            // the own body whole, for a mirror (Avatars.Mirror.cs)
        public BodyPhysics? Physics;                 // avatar physics (Avatars.Physics.cs)
        public LipState? Lip;                        // lip sync while it speaks (Avatars.LipSync.cs)
        // its impostor (Avatars.Crowd.cs): the atlas tile, whether it holds a capture, when and from which direction and
        // distance it was taken, the box it covers (centre from the root, half axes), and whether it was one last frame
        public ImpostorTile Tile;
        public bool Captured, WasImpostor;
        public long CapturedAt;
        public Vector3 CapDir, BoxFromRoot, BoxX, BoxY, BoxZ;
        public float CapDist;
        // what its attachments cost (the viewer's avatar rendering complexity), for the worn set it was judged on
        public int CostKey = int.MinValue, Cost;
        public bool CostKnown;
        public float CostArea;
        public long CostAt;
    }

    /// <summary>Drawn meshes in the viewer's order: skinned opaque, rigid eyes, then the blended ones
    /// (renderTransparent: skirt, eyelashes, hair).</summary>
    static readonly (string Type, int Te, int Kind)[] MeshOrder =
    {
        ("headMesh", TeHead, 0), ("upperBodyMesh", TeUpper, 0), ("lowerBodyMesh", TeLower, 0),
        ("eyeBallLeftMesh", TeEyes, 1), ("eyeBallRightMesh", TeEyes, 1),
        ("skirtMesh", TeSkirt, 2), ("eyelashMesh", TeHead, 2), ("hairMesh", TeHair, 2),
    };

    public Avatars(RenderWorld world)
    {
        _world = world;
        var sw = Stopwatch.StartNew();
        _cd = CharacterData.Shared;
        world.Log?.Invoke($"avatar character data loaded in {sw.ElapsedMilliseconds} ms");
        _motions = new MotionLibrary(_cd.Skeleton, s => world.Log?.Invoke(s));
        _skinOffset = Enumerable.Range(0, _cd.Skeleton.Joints.Count).Select(j => CharacterData.TotalSkinOffset(_cd.Skeleton, j)).ToArray();
    }

    void EnsureShared()
    {
        if (_shared) return;
        _shared = true;
        var gpu = _world.Gpu;
        var shape = ObjectManager.BuildBasicShape(PrimType.Sphere);
        var faces = VolumeBuilder.BuildPrim(shape, VolumeBuilder.DetailForLod(2));
        var pv = new List<MeshVertex>();
        var pi = new List<uint>();
        foreach (var f in faces)
        {
            uint b = (uint)pv.Count;
            for (int i = 0; i < f.Positions.Length; i++)
                pv.Add(new MeshVertex { Position = f.Positions[i], Normal = f.Normals[i], Tangent = new Vector4(1, 0, 0, 1), Uv = f.TexCoords[i] });
            foreach (var i in f.Indices) pi.Add(b + i);
        }
        _puff = gpu.GeometryPool.Add(pv.ToArray(), pi.ToArray());

        var weightsByOwner = new Dictionary<MeshDef, ulong>();
        int maxRigid = 0;
        foreach (var lods in _cd.Meshes.Values)
            foreach (var md in lods)
            {
                var m = md.Mesh;
                int nv = Math.Max(1, m.NumVertices);
                var verts = new MeshVertex[nv];
                for (int i = 0; i < nv && i < m.Coords.Length; i++)
                    verts[i] = new MeshVertex { Position = m.Coords[i], Normal = m.Normals[i], Tangent = new Vector4(1, 0, 0, 1), Uv = m.TexCoords[i] };
                var g = new MeshGpu { Indices = gpu.GeometryPool.Add(verts, m.Indices), VertexCount = (uint)nv };
                _meshGpu[md] = g;
                if (!m.HasWeights) { maxRigid = Math.Max(maxRigid, nv); continue; }
                if (!weightsByOwner.TryGetValue(md.Owner, out var w))
                {
                    // the viewer's weight: integer part = palette slot, fraction blends toward the next slot
                    var src = md.Owner.Mesh.Weights;
                    int slots = md.PaletteJoints.Length;
                    var sw = new SkinWeight[src.Length];
                    Span<int> j = stackalloc int[4];
                    Span<float> wt = stackalloc float[4];
                    for (int i = 0; i < src.Length; i++)
                    {
                        int a = Math.Min((int)MathF.Floor(src[i]), 14);
                        int b = Math.Min(a + 1, 14);
                        float x = src[i] - MathF.Floor(src[i]);
                        j[0] = Math.Clamp(a, 0, slots - 1); j[1] = Math.Clamp(b, 0, slots - 1); j[2] = 0; j[3] = 0;
                        wt[0] = 1 - x; wt[1] = x; wt[2] = 0; wt[3] = 0;
                        sw[i] = SkinWeight.From(j, wt);
                    }
                    w = gpu.SkinWeights.Add<SkinWeight>(sw).Address;
                    weightsByOwner[md.Owner] = w;
                }
                g.Weights = w;
            }
        var rigid = new SkinWeight[Math.Max(1, maxRigid)];
        Span<int> rj = stackalloc int[4];
        Span<float> rw = stackalloc float[] { 1, 0, 0, 0 };
        for (int i = 0; i < rigid.Length; i++) rigid[i] = SkinWeight.From(rj, rw);
        _rigidWeights = gpu.SkinWeights.Add<SkinWeight>(rigid);
        foreach (var (md, g) in _meshGpu)
            if (!md.Mesh.HasWeights) g.Weights = _rigidWeights.Address;
    }

    static readonly int Clones = int.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_AVATAR_CLONES"), out var c) ? c : 0;
    static readonly bool Debug = System.Environment.GetEnvironmentVariable("SOJOURN_AVATAR_DEBUG") == "1";

    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    static UUID BakeId(Avatar av, int te)
    {
        var t = av.Textures;
        if (t == null) return UUID.Zero;
        var f = t.FaceTextures != null && te < t.FaceTextures.Length ? t.FaceTextures[te] : null;
        return f?.TextureID ?? t.DefaultTexture?.TextureID ?? UUID.Zero;
    }

    static readonly int[] BakeFaces = { TeHead, TeUpper, TeLower, TeEyes, TeSkirt, TeHair };

    /// <summary>An avatar within draw distance but not drawn this frame (behind the person, an impostor out of view):
    /// its bakes' and attachments' textures count as near, so the texture store's idle and low-slot reclaiming leave
    /// them for last (TextureStore.Reclaim) and turning round doesn't find it a cloud again.</summary>
    void KeepNear(RegionScene scene, Avatar av, long tick)
    {
        foreach (int te in BakeFaces)
            if (_world.Textures.TryGet(BakeId(av, te), out var t)) t.KeepNear(tick);
        foreach (var obj in scene.AttachmentsOf(av.LocalID)) obj.Faces?.KeepNear(tick);
    }

    static bool Defined(UUID id) => id != UUID.Zero && id != ImgDefaultAvatar && id != ImgDefault;
    static bool Visible(UUID id) => Defined(id) && id != ImgInvisible;

    static Vector3 V(LibreMetaverse.Vector3 v) => new(v.X, v.Y, v.Z);
    static Quaternion Q(LibreMetaverse.Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    /// <summary>Where the avatar's root (mRoot, the pelvis) is, in the region frame, before the shape's foot
    /// offset; false if its seat isn't known. Seated, it rides its seat where the scene draws it (moving seats are
    /// carried on between the region's updates: RegionScene.Motion.cs).</summary>
    static bool Placement(RegionScene scene, Avatar av, out Vector3 pos, out Quaternion rot, out bool seated)
    {
        pos = V(av.Position);
        rot = Q(av.Rotation);
        seated = av.ParentID != 0;
        if (!seated) return true;
        // sitting: the position and rotation are relative to the seat prim (its root's frame for a child prim)
        if (!scene.TryWorldTransform(av.ParentID, out var sp, out var sr))
        {
            var src = scene.Source;
            if (!src.Prims.TryGetValue(av.ParentID, out var seat)) return false;
            sr = Q(seat.Rotation);
            sp = V(seat.Position);
            if (seat.ParentID != 0)
            {
                if (!src.Prims.TryGetValue(seat.ParentID, out var root)) return false;
                var rr = Q(root.Rotation);
                sp = V(root.Position) + Vector3.Transform(sp, rr);
                sr = Quaternion.Concatenate(sr, rr);
            }
        }
        (pos, rot) = SeatedOn(sp, sr, av);
        return true;
    }

    /// <summary>A seated avatar's root (pelvis) from its seat's region pose: its position and rotation relative to the
    /// seat, with its hover. The headset's seated view works it out the same way (VrPilot).</summary>
    public static (Vector3 Pos, Quaternion Rot) SeatedOn(Vector3 seatPos, Quaternion seatRot, Avatar av)
    {
        var rot = Q(av.Rotation);
        var hover = V(av.HoverHeight);
        var pos = seatPos + Vector3.Transform(V(av.Position) + Vector3.Transform(hover, rot), seatRot);
        return (pos, Quaternion.Normalize(Quaternion.Concatenate(rot, seatRot)));
    }

    static Quaternion YawOnly(Quaternion q)
    {
        var f = Vector3.Transform(Vector3.UnitX, q);
        if (f.X * f.X + f.Y * f.Y < 1e-8f) return Quaternion.Identity;
        return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan2(f.Y, f.X));
    }

    /// <summary>Boxes for the sun shadow fit (avatars are visible drawables in getVisibleExtents).</summary>
    public void AddExtents(IReadOnlyList<(RegionScene Scene, Vector3 Offset)> scenes, AabbSceneExtents ext, ViewSetup v)
    {
        foreach (var (scene, offset) in scenes)
            foreach (var av in scene.Source.Avatars.Values)
            {
                if (!Placement(scene, av, out var p, out _, out _)) continue;
                p += offset;
                if (Vector3.Distance(p, v.Position) > v.DrawDistance + 2) continue;
                ext.Add(p - new Vector3(0.6f, 0.6f, 1.2f), p + new Vector3(0.6f, 0.6f, 1.2f));
            }
    }

    /// <summary>Add every avatar in the scenes to the view's draw list; returns the number still loading.</summary>
    public int AddAvatars(IReadOnlyList<(RegionScene Scene, Vector3 Offset)> scenes, ViewSetup v, DrawList dl, RenderRequest req,
        Frustum frustum, ShadowFrustum?[]? casters, ViewOptions opt)
    {
        EnsureShared();
        _frameTick++;
        (_lastRigged, _lastPalettes, _rigged, _palettesMade) = (_rigged, _palettesMade, 0, 0);
        _posed.Clear();
        dl.MainView = true;
        int pending = 0;
        double now = Now;
        long tick = System.Environment.TickCount64;
        var hide = req.HideAvatars;
        LookScenes(scenes, tick);
        ApplyFixes(scenes);   // reset skeleton, refresh, resync (Avatars.Reset.cs)

        _worn.Clear();
        _captures.Clear();
        _entryCount = 0;
        NoteViewRegion(req.RegionHandle);
        var taken = _taken;
        taken.Clear();
        // gather candidates first: the avatar LOD depends on how many are drawn (sNumVisibleAvatars). Every avatar within
        // the draw distance is a candidate, in view or not, so which are whole and at what detail goes by distance from
        // the head alone and turning it changes nothing (docs/rendering.md > Distances from the head)
        foreach (var (scene, offset) in scenes)
        {
            var src = scene.Source;
            foreach (var av in src.Avatars.Values)
            {
                if (!OneCopy(src.Handle, av, taken)) continue;
                if (hide != null && hide.Contains(av.ID)) continue;
                if (!Placement(scene, av, out var pos, out var rot, out bool seated)) continue;
                if (!seated) pos = _world.Motion.Position(av.ID, src.Handle, pos, V(av.Velocity), scene.RegionSize);
                pos += offset;
                // the own body in the headset is drawn under the head, wherever the region has it
                bool self = av.ID == Limits.Self || req.Self?.Id == av.ID;
                if (req.Self is { } me && me.Id == av.ID && !seated) pos = new Vector3(me.Head.X, me.Head.Y, pos.Z);
                float reach = 2.5f;
                float dist = Vector3.Distance(pos, v.Position);
                if (dist - reach > v.DrawDistance) continue;
                bool vis = frustum.Sphere(pos, reach);
                int mask = 0;
                if (casters != null)
                    for (int c = 0; c < casters.Length; c++)
                        if (casters[c] is { } cf && cf.IntersectsAabb(pos - new Vector3(reach), pos + new Vector3(reach))) mask |= 1 << c;
                var e = NextEntry();
                (e.Scene, e.Offset, e.Av, e.Pos, e.Rot, e.Seated, e.Visible, e.Casters, e.Dist, e.Self) = (scene, offset, av, pos, rot, seated, vis, mask, dist, self);
                // bench aid: SOJOURN_AVATAR_CLONES=N draws each avatar N more times on a grid behind it
                for (int c = 1; c <= Clones; c++)
                {
                    var cp = pos + new Vector3((c % 7 - 3) * 1.0f, -(c / 7 + 1) * 1.2f, 0);
                    var k = NextEntry();
                    (k.Scene, k.Offset, k.Av, k.Pos, k.Rot, k.Seated, k.Visible, k.Casters, k.Dist, k.Clone) =
                        (scene, offset, av, cp, rot, seated, frustum.Sphere(cp, reach), mask, Vector3.Distance(cp, v.Position), true);
                }
            }
        }
        // pooled entries past this frame's let go of their avatar, body and scene (one held a left region's whole scene)
        for (int i = _entryCount; i < _entries.Count && _entries[i].Av != null; i++)
            (_entries[i].Scene, _entries[i].Av, _entries[i].Body) = (null!, null!, null);
        Plan(v, dl, tick);
        // the viewer counts the avatars in its view; here those within the draw distance drawn whole (or as clouds or
        // jelly dolls), wherever the head points
        int numVisible = 0;
        for (int i = 0; i < _entryCount; i++) if (_entries[i].Drawn != "impostor") numVisible++;

        for (int i = 0; i < _entryCount; i++)
        {
            var e = _entries[i];
            if (e.How == Draw.Cloud)
            {
                if (e.Visible) AddCloud(e.Av, e.Pos, v, dl, req);
                continue;
            }
            // neither in view nor casting a shadow into it: ranked and counted, not drawn
            if (!e.Visible && e.Casters == 0) { KeepNear(e.Scene, e.Av, tick); continue; }
            // impostors cast no shadow: one out of view isn't drawn at all
            if (e.How is Draw.Stale or Draw.Capture && !e.Visible) { KeepNear(e.Scene, e.Av, tick); continue; }
            pending += AddAvatar(e.Scene, e.Offset, e.Av, e.Pos, e.Rot, e.Seated, e.Visible, e.How is Draw.Stale or Draw.Capture ? 0 : e.Casters,
                v, dl, req, opt, numVisible, now, tick, e.How, _captures.GetValueOrDefault(e));
        }
        pending += PoseAnimesh(scenes, v, now, tick);   // animated objects' own skeletons (Avatars.Animesh.cs)
        PublishStatus(tick);

        if (tick - _lastSweep > 10_000)
        {
            _lastSweep = tick;
            foreach (var id in _bodies.Where(kv => tick - kv.Value.LastUsed > 60_000).Select(kv => kv.Key).ToList())
            {
                Release(_bodies[id]);
                if (!_bodies[id].Tile.IsEmpty) _world.Gpu.Renderer.Atlas.Free(_bodies[id].Tile);
                _bodies.Remove(id);
            }
        }
        return pending;
    }

    readonly HashSet<UUID> _taken = new(), _pollTaken = new();
    ulong _viewRegion;

    /// <summary>Whether this copy of an avatar is the one to draw. Everything here is per avatar id (its body, pose,
    /// worn state, dead reckoning), and a region can still hold a copy of an avatar that has crossed out of it (until
    /// its kill or the coarse lists clear it; for the person's own avatar Sojourn.Core's WorldHub.SelfIn drops it as
    /// soon as the new region has one): two copies drawn shared one pose, and their attachments were placed at the
    /// other copy's. So one copy per id: the view region's if it has one, else the first. A terse update for an
    /// avatar no longer known makes a shell with no id in LibreMetaverse; that's never drawn.</summary>
    bool OneCopy(ulong region, Avatar av, HashSet<UUID> taken)
    {
        if (av.ID == UUID.Zero) return false;
        if (region != _viewRegion && _inView.Contains(av.ID)) return false;
        return taken.Add(av.ID);
    }

    readonly HashSet<UUID> _inView = new();

    /// <summary>The ids the view region holds avatars for (their copies there are the ones drawn).</summary>
    void NoteViewRegion(ulong region)
    {
        _viewRegion = region;
        _inView.Clear();
        if (_world.Region(region) is not { } view) return;
        foreach (var (_, av) in view.Source.Avatars) _inView.Add(av.ID);
    }

    readonly Dictionary<UUID, (AvatarAnimator Anim, long Seen)> _animators = new();
    long _lastPoll, _lastAnimatorSweep;

    AvatarAnimator Animator(UUID id, double now)
    {
        if (!_animators.TryGetValue(id, out var a)) a = (new AvatarAnimator(now), 0);
        _animators[id] = (a.Anim, System.Environment.TickCount64);
        return a.Anim;
    }

    /// <summary>Note animation changes of every avatar in every region (render thread, called each loop): poses
    /// need to know when an animation started, and a render may come long after.</summary>
    public void Poll()
    {
        long tick = System.Environment.TickCount64;
        if (tick - _lastPoll < 100) return;
        _lastPoll = tick;
        double now = Now;
        // (one copy per avatar, as drawn: a stale copy's animations would stop and restart the other's)
        NoteViewRegion(_viewRegion);
        var taken = _pollTaken;
        taken.Clear();
        foreach (var scene in _world.Regions)
            foreach (var av in scene.Source.Avatars.Values)
                if (OneCopy(scene.Source.Handle, av, taken)) Animator(av.ID, now).Sync(av.Animations, now);
        if (tick - _lastAnimatorSweep > 30_000)
        {
            _lastAnimatorSweep = tick;
            foreach (var id in _animators.Where(kv => tick - kv.Value.Seen > 120_000).Select(kv => kv.Key).ToList())
                _animators.Remove(id);
        }
    }

    readonly Dictionary<(ulong Region, uint LocalId), (Body Body, Vector3 Offset)> _posed = new();

    /// <summary>A rigged mesh's joint palette on an avatar posed this frame (LLSkinningUtil::initSkinningMatrixPalette:
    /// inverse bind times the joint's world matrix; the bind shape matrix is already in the vertices), or -1. Only for
    /// the view's own draw list: reflection probe captures leave avatars out. One a frame per avatar and skin: the
    /// meshes an avatar wears mostly share theirs (SL's often list ~110 joints, so a palette per mesh per frame was
    /// most of what a crowd allocated).</summary>
    public int RiggedPalette(ulong region, uint avatarLocalId, ShapeGeometry shape, DrawList dl)
    {
        if (!dl.MainView || shape.InverseBind == null || shape.Skin == null) return -1;
        if (!_posed.TryGetValue((region, avatarLocalId), out var p)) return -1;
        var share = ShareOf(shape);
        Interlocked.Increment(ref _rigged);
        // per draw list: the view's is built in segments on several threads (World/DrawPlan.cs), each with its own
        if (dl.PaletteStarts.TryGetValue((p.Body, share, false), out int start)) return start;
        Interlocked.Increment(ref _palettesMade);
        start = WritePalette(dl, share, p.Body.Pose.World);
        dl.PaletteStarts[(p.Body, share, false)] = start;
        return start;
    }

    /// <summary>The palette written straight into the draw list (no array of its own).</summary>
    static int WritePalette(DrawList dl, SkinShare share, Matrix4x4[] world)
    {
        var map = share.Map;
        var ibm = share.InverseBind;
        int start = dl.Palettes.Count;
        System.Runtime.InteropServices.CollectionsMarshal.SetCount(dl.Palettes, start + map.Length);
        var pal = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(dl.Palettes).Slice(start, map.Length);
        for (int j = 0; j < map.Length; j++) pal[j] = ibm[j] * world[map[j]];
        return start;
    }

    readonly Dictionary<SkinShare, SkinShare> _shares = new(SkinShareComparer.Instance);

    /// <summary>The shape's skin as shared with every other whose joints and inverse binds are the same.</summary>
    SkinShare ShareOf(ShapeGeometry shape)
    {
        if (shape.Share is { } known) return known;
        lock (_shares) return ShareOfLocked(shape);
    }

    SkinShare ShareOfLocked(ShapeGeometry shape)
    {
        if (shape.Share is { } known) return known;
        var ibm = shape.InverseBind!;
        var map = shape.Skin!.JointNames.Take(ibm.Length).Select(n => Math.Max(_cd.Skeleton[n], 0)).ToArray();
        var share = new SkinShare { Map = map, InverseBind = ibm.Length == map.Length ? ibm : ibm[..map.Length] };
        if (_shares.TryGetValue(share, out var same)) return shape.Share = same;
        // shapes keep the share they were given; forgetting the table only stops new ones finding it
        if (_shares.Count > 8192) _shares.Clear();
        _shares[share] = share;
        return shape.Share = share;
    }

    sealed class SkinShareComparer : IEqualityComparer<SkinShare>
    {
        public static readonly SkinShareComparer Instance = new();
        public bool Equals(SkinShare? a, SkinShare? b) =>
            ReferenceEquals(a, b) || a != null && b != null && a.Map.AsSpan().SequenceEqual(b.Map) && a.InverseBind.AsSpan().SequenceEqual(b.InverseBind);
        public int GetHashCode(SkinShare s)
        {
            var h = new HashCode();
            h.Add(s.Map.Length);
            for (int j = 0; j < s.Map.Length; j++)
            {
                h.Add(s.Map[j]);
                h.Add(s.InverseBind[j].M41); h.Add(s.InverseBind[j].M42); h.Add(s.InverseBind[j].M43);
            }
            return h.ToHashCode();
        }
    }

    /// <summary>An avatar's animated extents this frame in its region's frame (centre, full diagonal).</summary>
    public (Vector3 Center, float Diagonal)? Extents(ulong region, uint avatarLocalId) =>
        _posed.TryGetValue((region, avatarLocalId), out var p) ? (p.Body.Center - p.Offset, p.Body.Diagonal) : null;

    /// <summary>What the avatar's attachments change: system meshes hidden by bakes-on-mesh faces
    /// (updateMeshVisibility), joint position/scale overrides and the pelvis fixup from rigged meshes
    /// (addAttachmentOverridesForObject: alt inverse bind translations off the default by more than 0.1 mm; the
    /// largest mesh id wins), and the body size those joints give.</summary>
    void WornState(RegionScene scene, Avatar av, Body body, BodyShape shape)
    {
        // it only changes with what's worn: skip the work (and its allocations) every frame it's the same
        int worn = WornKey(scene, av);
        if (body.RestPos != null && worn == body.WornKey && ReferenceEquals(shape, body.WornShape)) return;
        body.WornKey = worn;
        body.WornShape = shape;
        var sk = _cd.Skeleton;
        int n = sk.Joints.Count;
        int hidden = 0;
        foreach (var obj in scene.AttachmentsOf(av.LocalID))
        {
            var t = obj.Prim.Textures;
            if (t == null) continue;
            void Check(UUID id)
            {
                if (!RegionScene.BakeOnMeshIds.TryGetValue(id, out int te)) return;
                hidden |= te switch
                {
                    TeHead => Bit("headMesh") | Bit("eyelashMesh"),
                    TeUpper => Bit("upperBodyMesh"),
                    TeLower => Bit("lowerBodyMesh"),
                    TeEyes => Bit("eyeBallLeftMesh") | Bit("eyeBallRightMesh"),
                    TeHair => Bit("hairMesh"),
                    TeSkirt => Bit("skirtMesh"),
                    _ => 0,
                };
            }
            if (t.DefaultTexture != null) Check(t.DefaultTexture.TextureID);
            if (t.FaceTextures != null)
                foreach (var f in t.FaceTextures) if (f != null) Check(f.TextureID);
        }
        body.Hidden = hidden;
        if (Debug)
            _world.Log?.Invoke($"worn {av.Name}: " + string.Join(", ", scene.AttachmentsOf(av.LocalID).Select(o =>
                $"{o.Prim.LocalID} {o.Prim.ID} {o.Kind} pt{(int)o.Prim.PrimData.AttachmentPoint} lods[{string.Join("", o.Lods.Select(l => l == null ? "-" : l.Ready ? (l.Skin != null ? "S" : "R") : l.Failed ? "F" : "."))}]")));

        body.RestPos ??= new Vector3[n];
        body.RestScale ??= new Vector3[n];
        Array.Copy(shape.JointPos, body.RestPos, n);
        Array.Copy(shape.JointScale, body.RestScale, n);
        body.PelvisFixup = 0;
        bool any = false;
        foreach (var (mesh, geo) in scene.RiggedShapesOf(av.LocalID).OrderBy(x => x.Mesh.ToString()))
        {
            var skin = geo.Skin!;
            if (skin.PelvisOffset != 0) body.PelvisFixup = skin.PelvisOffset;
            if (geo.AltInverseBind is not { } alt) continue;
            for (int j = 0; j < alt.Length && j < skin.JointNames.Length; j++)
            {
                int joint = sk[skin.JointNames[j]];
                if (joint < 0) continue;
                var p = alt[j].Translation;
                if (Vector3.Distance(p, sk.Joints[joint].Pos) <= 0.0001f) continue;
                body.RestPos[joint] = p;
                if (skin.LockScaleIfJointPosition) body.RestScale[joint] = sk.Joints[joint].Scale;
                any = true;
            }
        }
        if (any) (body.PelvisToFoot, body.BodyHeight) = BodyShape.BodySize(sk, body.RestPos, body.RestScale);
        else (body.PelvisToFoot, body.BodyHeight) = (shape.PelvisToFoot, shape.BodyHeight);
    }

    static int WornKey(RegionScene scene, Avatar av)
    {
        var h = new HashCode();
        foreach (var obj in scene.AttachmentsOf(av.LocalID))
        {
            h.Add(obj.LocalId);
            h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Prim.Textures));
            if (obj.Kind != ShapeKind.Mesh) continue;
            foreach (var l in obj.Lods)
                if (l is { Ready: true, Evicted: false, Skin: not null } && l.Key.Kind == ShapeKind.RiggedMesh)
                {
                    h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(l));
                    break;
                }
        }
        return h.ToHashCode();
    }

    static int Bit(string type) => 1 << Array.FindIndex(MeshOrder, m => m.Type == type);

    /// <summary>An attachment point's frame on an avatar posed this frame, in its region's frame
    /// (LLViewerJointAttachment: a child of its bone, offset by the point's position and rotation).</summary>
    public (Vector3 Pos, Quaternion Rot)? AttachmentFrame(ulong region, uint avatarLocalId, int point)
    {
        if (!_posed.TryGetValue((region, avatarLocalId), out var p)) return null;
        // an unknown point falls back to the chest (LLVOAvatar::attachObject)
        if (!_cd.AttachmentPoints.TryGetValue(point, out var ap) && !_cd.AttachmentPoints.TryGetValue(1, out ap)) return null;
        if (ap.Hud) return null;
        var pose = p.Body.Pose;
        int j = ap.Joint;
        if (p.Body.HiddenJoints is { } hidden && hidden[j]) return null;   // worn on the own head in the headset
        var wr = Quaternion.Normalize(Quaternion.Concatenate(ap.Rotation, pose.WorldRot[j]));
        var wp = pose.WorldPos[j] + Vector3.Transform(pose.Scale[j] * ap.Position, pose.WorldRot[j]);
        return (wp - p.Offset, wr);
    }

    // --- the person's own body in the headset ------------------------------------------------------------------------

    bool[]? _headJoints;
    /// <summary>How far behind the camera the own body's eyes are put (m, at its own size): a little, so looking down
    /// shows the chest rather than the inside of the collar.</summary>
    static readonly float BodyBack = float.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_BODY_BACK"), System.Globalization.CultureInfo.InvariantCulture, out var bb) ? bb : 0.08f;
    (int Shoulder, int Elbow, int Wrist)? _leftArm, _rightArm;
    int _eyeL = -1, _eyeR = -1;

    /// <summary>mNeck and every joint under it (head, face, eyes, skull, their collision volumes): they collapse to the
    /// base of the neck, so looking down from the eyes there's no neck to see into.</summary>
    bool[] HeadJoints()
    {
        if (_headJoints != null) return _headJoints;
        var sk = _cd.Skeleton;
        var hidden = new bool[sk.Joints.Count];
        int head = sk["mNeck"];
        for (int i = 0; i < sk.Joints.Count; i++)
            for (int j = i; j >= 0; j = sk.Joints[j].Parent)
                if (j == head) { hidden[i] = true; break; }
        _eyeL = sk["mEyeLeft"];
        _eyeR = sk["mEyeRight"];
        _leftArm = (sk["mShoulderLeft"], sk["mElbowLeft"], sk["mWristLeft"]);
        _rightArm = (sk["mShoulderRight"], sk["mElbowRight"], sk["mWristRight"]);
        return _headJoints = hidden;
    }

    /// <summary>The own avatar in the headset: standing, it faces the given way, is scaled about its feet so its eyes
    /// are at the person's eye height, and stands with its eyes under the camera (a few cm behind, so looking down shows
    /// the chest, not the inside of the neck); seated, it stays on its seat. Its head and everything under it shrink to
    /// a point, and the arms reach for the hands.</summary>
    void PoseSelf(Body body, SelfBody self, Vector3 pos, Quaternion rootRot, float ground, bool seated, long tick)
    {
        var pose = body.Pose;
        var hidden = HeadJoints();
        int n = pose.Scale.Length;
        float dt = body.SelfAt == 0 ? 1 : Math.Clamp((tick - body.SelfAt) / 1000f, 0, 1);
        bool first = body.SelfAt == 0 || tick - body.SelfAt > 2000;
        body.SelfAt = tick;
        body.HiddenJoints = hidden;
        float k = 1;
        if (!seated)
        {
            // facing: the body turns after the head (as the region's avatar does), smoothly
            float d = self.Yaw - body.SelfYaw;
            d = MathF.IEEERemainder(d, 2 * MathF.PI);
            body.SelfYaw = first ? self.Yaw : body.SelfYaw + d * (1 - MathF.Exp(-dt * 8));
            rootRot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, body.SelfYaw);

            // its own eye height, standing upright (the rest pose: animations breathe, and flying lies it down), then the
            // scale that puts its eyes at the person's
            pose.Compose(pos, rootRot);
            var eyes = (pose.WorldPos[_eyeL] + pose.WorldPos[_eyeR]) * 0.5f;
            float eye = (pos.Z - ground) + RestHeight(body, _eyeL);
            if (eye > 0.3f) body.SelfEye = eye;
            SelfEyeHeight = body.SelfEye;
            float want = self.EyeHeight > 0 && body.SelfEye > 0.3f ? Math.Clamp(self.EyeHeight / body.SelfEye, 0.5f, 1.6f) : 1;
            body.SelfScale = first || body.SelfScale <= 0 ? want : body.SelfScale + (want - body.SelfScale) * (1 - MathF.Exp(-dt / 0.5f));
            k = body.SelfScale;

            // about the feet; eyes over the camera, set back a little along the facing
            var fwd = Vector3.Transform(Vector3.UnitX, rootRot);
            var eyeRel = (eyes - pos) * k;
            var root = new Vector3(pos.X, pos.Y, ground + (pos.Z - ground) * k);
            var at = new Vector3(self.Head.X, self.Head.Y, 0) - fwd * BodyBack * k;
            root.X += at.X - (root.X + eyeRel.X);
            root.Y += at.Y - (root.Y + eyeRel.Y);
            pos = root;
            SelfSeatedEyes = null;
            SelfSeatedEyesLocal = null;
        }
        else
        {
            // where the sit animation puts the eyes (before the head is shrunk away): the headset's seated view
            pose.Compose(pos, rootRot);
            var seatedEyes = (pose.WorldPos[_eyeL] + pose.WorldPos[_eyeR]) * 0.5f;
            SelfSeatedEyes = (seatedEyes, Vector3.Transform(Vector3.UnitX, rootRot));
            SelfSeatedEyesLocal = Vector3.Transform(seatedEyes - pos, Quaternion.Conjugate(rootRot));
        }
        for (int i = 0; i < n; i++)
        {
            pose.Scale[i] *= k;
            if (hidden[i]) pose.Scale[i] *= 0.001f;
        }
        pose.Compose(pos, rootRot, k);

        if (self.Left is { } l && _leftArm is { } la) Reach(pose, la, l, +1, rootRot);
        if (self.Right is { } r && _rightArm is { } ra) Reach(pose, ra, r, -1, rootRot);
        if (self.Left != null || self.Right != null) pose.Compose(pos, rootRot, k);
        PoseMirrorSelf(body, pos, rootRot, k);
    }

    /// <summary>How far a joint is above mRoot in the rest pose of this body's shape (rotations all identity: each
    /// joint's offset times its parent's scale, summed up the chain).</summary>
    float RestHeight(Body body, int joint)
    {
        var sk = _cd.Skeleton;
        var pos = body.RestPos ?? body.Shape?.JointPos;
        var scale = body.RestScale ?? body.Shape?.JointScale;
        if (pos == null || scale == null || joint < 0) return 0;
        float z = 0;
        for (int j = joint; j >= 0; j = sk.Joints[j].Parent)
        {
            int p = sk.Joints[j].Parent;
            z += pos[j].Z * (p >= 0 ? scale[p].Z : 1);
        }
        return z;
    }

    const float MaxStretch = 1.4f;

    /// <summary>Two-bone IK: the shoulder and elbow turned so the wrist reaches the target (the arm stretching along its
    /// bones when it's out of reach, up to MaxStretch, then pointing at it), the elbow hanging down, out and back, as a relaxed arm's does; then the wrist turned as asked. In
    /// the rest pose (a T) every bone's frame is its parent's: the arm runs along +Y (left) or -Y (right) and the elbow
    /// hinges about Z, so each bone's world frame is built from where it points and the bend's axis.</summary>
    static void Reach(SkeletonPose pose, (int Shoulder, int Elbow, int Wrist) arm, HandTarget target, float side, Quaternion rootRot)
    {
        var (sj, ej, wj) = arm;
        if (sj < 0 || ej < 0 || wj < 0) return;
        var s = pose.WorldPos[sj];
        float a = Vector3.Distance(s, pose.WorldPos[ej]), b = Vector3.Distance(pose.WorldPos[ej], pose.WorldPos[wj]);
        if (a < 1e-3f || b < 1e-3f) return;
        var to = target.Wrist - s;
        float d = to.Length();
        if (d < 1e-4f) return;
        var dir = to / d;
        // out of reach (an avatar's arms are often shorter than its wearer's, and its shoulders sit behind the eyes):
        // the arm stretches along its bones, up to MaxStretch, so the hand still reaches the controller
        float stretch = Math.Clamp(d / ((a + b) * 0.97f), 1, MaxStretch);   // a little short of straight
        if (stretch > 1)
        {
            pose.Scale[sj].Y *= stretch;
            pose.Scale[ej].Y *= stretch;
            a *= stretch;
            b *= stretch;
        }
        d = Math.Clamp(d, MathF.Abs(a - b) + 1e-3f, a + b - 1e-4f);

        var bodyLeft = Vector3.Transform(Vector3.UnitY, rootRot);
        var bodyFwd = Vector3.Transform(Vector3.UnitX, rootRot);
        var pole = -Vector3.UnitZ + bodyLeft * (side * 0.6f) - bodyFwd * 0.4f;
        var m = pole - dir * Vector3.Dot(pole, dir);
        if (m.LengthSquared() < 1e-6f) m = Vector3.Cross(dir, bodyFwd);
        if (m.LengthSquared() < 1e-6f) m = Vector3.Cross(dir, Vector3.UnitZ);
        m = Vector3.Normalize(m);

        float cosA = Math.Clamp((a * a + d * d - b * b) / (2 * a * d), -1, 1);
        float sinA = MathF.Sqrt(1 - cosA * cosA);
        var elbow = s + dir * (cosA * a) + m * (sinA * a);
        var wrist = s + dir * d;
        var upper = Vector3.Normalize(elbow - s);
        var fore = Vector3.Normalize(wrist - elbow);
        var hinge = Vector3.Normalize(Vector3.Cross(dir, m)) * side;   // the bones' Z

        var shoulderRot = Frame(upper * side, hinge);
        var elbowRot = Frame(fore * side, hinge);
        int parent = pose.Def.Joints[sj].Parent;
        var parentRot = parent >= 0 ? pose.WorldRot[parent] : rootRot;
        pose.Rot[sj] = Quaternion.Normalize(Quaternion.Concatenate(shoulderRot, Quaternion.Inverse(parentRot)));
        pose.Rot[ej] = Quaternion.Normalize(Quaternion.Concatenate(elbowRot, Quaternion.Inverse(shoulderRot)));
        pose.Rot[wj] = Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Normalize(target.Rotation), Quaternion.Inverse(elbowRot)));
    }

    /// <summary>The rotation taking a bone's local axes to world ones, given where its Y and Z go.</summary>
    static Quaternion Frame(Vector3 y, Vector3 z)
    {
        y = Vector3.Normalize(y);
        z = Vector3.Normalize(z - y * Vector3.Dot(z, y));
        var x = Vector3.Cross(y, z);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>The own avatar's eye height above its feet at its own size (last drawn as a <see cref="SelfBody"/>).</summary>
    public float? SelfEyeHeight { get; private set; }

    /// <summary>Where the own avatar's eyes were last drawn while it sat (a <see cref="SelfBody"/>, the view frame), and
    /// which way its body faced.</summary>
    public (Vector3 Eyes, Vector3 Facing)? SelfSeatedEyes { get; private set; }
    /// <summary>The same eyes in the seated avatar's own frame (its root's position and rotation, Placement): where its
    /// sit animation holds them, so the headset can put them on this frame's seat pose (a moving vehicle) rather than
    /// last frame's.</summary>
    public Vector3? SelfSeatedEyesLocal { get; private set; }

    void Release(Body b)
    {
        var r = b.Verts;
        if (r.Count > 0) _world.RetireLater(() => _world.Gpu.SkinVertices.Free(r));
        b.Verts = default;
    }

    /// <summary>How an avatar is drawn this frame (Avatars.Crowd.cs): whole; captured into its impostor tile (and drawn
    /// as its impostor); as its impostor from an earlier capture, its pose only carried to where it is now; or as a jelly
    /// doll, the system body in one colour without its attachments.</summary>
    enum Draw : byte { Full, Capture, Stale, Jelly, Cloud }

    // each avatar's bake sources, kept (one a bake an avatar a frame was made before)
    readonly Dictionary<(IBakeSource, UUID, int), BakeAssets> _bakeSources = new();

    BakeAssets BakeSource(IBakeSource bs, UUID avatar, int te)
    {
        if (_bakeSources.TryGetValue((bs, avatar, te), out var known)) return known;
        if (_bakeSources.Count > 4096) _bakeSources.Clear();
        return _bakeSources[(bs, avatar, te)] = new BakeAssets(bs, avatar, te);
    }

    int AddAvatar(RegionScene scene, Vector3 offset, Avatar av, Vector3 pos, Quaternion rot, bool seated, bool visible, int casterMask, ViewSetup v,
        DrawList dl, RenderRequest req, ViewOptions opt, int numVisible, double now, long tick, Draw how = Draw.Full, ImpostorCapture? cap = null)
    {
        var src = scene.Source;
        var assets = src.Assets;
        var textures = _world.Textures;
        int pending = 0;

        // appearance: visual params and at least one body bake, then every visible bake loaded (isFullyLoaded)
        var vp = av.VisualParameters;
        bool appearance = vp is { Length: > 1 } && (Defined(BakeId(av, TeHead)) || Defined(BakeId(av, TeUpper)) || Defined(BakeId(av, TeLower)));
        var bakes = _bakes;
        bakes.Clear();
        bool jelly = how == Draw.Jelly;
        // a jelly doll needs only its shape: its bakes aren't shown
        bool loaded = how != Draw.Cloud && (jelly ? vp is { Length: > 1 } : appearance);
        if (appearance && !jelly)
            foreach (int te in new[] { TeHead, TeUpper, TeLower, TeEyes, TeSkirt, TeHair })
            {
                var id = BakeId(av, te);
                if (!Visible(id)) continue;
                // head/upper/lower carry the morph mask in a fifth component
                var bakeSrc = assets is IBakeSource bs ? BakeSource(bs, av.ID, te) : assets;
                var t = textures.Request(id, bakeSrc, keepAux: te is TeHead or TeUpper or TeLower);
                if (t.State == TexState.Loading) { loaded = false; pending++; }
                // a body bake that can't be had keeps the avatar a cloud (the viewer never finishes loading it)
                if (t.State == TexState.Missing && te is TeHead or TeUpper or TeLower) loaded = false;
                bakes[te] = t;
            }
        if (!loaded)
        {
            if (Debug && tick % 5000 < 20)
                _world.Log?.Invoke($"cloud {av.Name}: vp {vp?.Length ?? 0}, bakes {string.Join(" ", new[] { TeHead, TeUpper, TeLower, TeEyes, TeSkirt, TeHair }.Select(te => $"{te}:{BakeId(av, te).ToString()[..8]}:{(bakes.TryGetValue(te, out var bt) ? bt.State.ToString() : "-")}"))}");
            if (visible) AddCloud(av, pos, v, dl, req);
            return pending;
        }

        if (!_bodies.TryGetValue(av.ID, out var body))
        {
            body = new Body { Id = av.ID, Anim = Animator(av.ID, now), Pose = new SkeletonPose(_cd.Skeleton) };
            _bodies[av.ID] = body;
        }
        body.LastUsed = tick;
        // an impostor between captures: its last pose, carried to where the avatar is now
        if (how == Draw.Stale && body.Shape != null && body.Captured)
        {
            PlaceRoot(body, body.Shape, av, ref pos, rot, seated, out var staleRot);
            body.HiddenJoints = null;
            body.Pose.Compose(pos, staleRot);
            _posed[(src.Handle, av.LocalID)] = (body, offset);
            _worn[(src.Handle, av.LocalID)] = (Worn.Lights, null);
            if (visible) AddImpostor(av, body, pos, v, dl);
            NameTag(av, body, body.Shape, v, dl, req, visible);
            return pending;
        }

        // shape (with last pose's animated morphs; the pose needs the shape's skeleton first)
        body.Anim = Animator(av.ID, now);
        body.Anim.LookAt = LookAtFor(scene, offset, av, body, tick);
        body.Anim.PointAt = PointAtFor(scene, offset, av, body, tick);   // a received PointAt's arm (Avatars.Effects.cs)
        if (body.Anim.Sync(av.Animations, now) && Debug)
            _world.Log?.Invoke($"avatar {av.Name} at {pos} rot {rot}: animations {string.Join(", ", (av.Animations ?? new()).Select(x => x.AnimationID.ToString()[..8] + "#" + x.AnimationSequence))}");
        var masks = new Dictionary<string, MorphMask>();
        foreach (var (te, region) in new[] { (TeHead, "head"), (TeUpper, "upper_body"), (TeLower, "lower_body") })
            if (bakes.TryGetValue(te, out var bt) && MaskFor(bt, region) is { } mask) masks[region] = mask;
        var shape = body.Shape ?? new BodyShape(_cd, vp, null, masks);
        WornState(scene, av, body, shape);
        // the default stands' feet on the ground under this frame's root (the own body in the headset is put down by the rig)
        SetFootGround(scene, offset, av, body, shape, pos, rot, seated, req.Self is { } fs && fs.Id == av.ID);
        var animated = body.Anim.Evaluate(shape, body.Pose, _motions, assets, now, ref pending, body.RestPos, body.RestScale);
        int lookKey = ShapeKey(vp!, null, masks);
        int key = ShapeKey(vp!, animated, masks);
        // a new shape morphs and uploads the whole body (milliseconds); an expression or hand pose fading in changes
        // the animated morphs nearly every frame, so those changes alone are taken at most ten times a second an
        // avatar and once a frame overall, a new appearance at once
        bool lookChanged = body.Shape == null || lookKey != body.LookKey;
        if (lookChanged || key != body.ShapeKey && tick - body.ShapeBuiltAt >= 100 && _animatedRebuildFrame != _frameTick)
        {
            if (!lookChanged) _animatedRebuildFrame = _frameTick;
            body.ShapeBuiltAt = tick;
            body.LookKey = lookKey;
            long t0 = Stopwatch.GetTimestamp();
            shape = new BodyShape(_cd, vp, animated, masks);
            _shapesBuilt++;
            if (Debug) _world.Log?.Invoke($"shape {av.Name}: {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F2} ms");
            // re-pose on the new skeleton (the shape's joint positions and scales are the pose's rest)
            WornState(scene, av, body, shape);
            body.Anim.Evaluate(shape, body.Pose, _motions, assets, now, ref pending, body.RestPos, body.RestScale);
            body.Shape = shape;
            body.ShapeKey = key;
            Upload(body);
        }

        if (Debug && _cd.Skeleton.TryGet("mHipLeft", out var dh) && _cd.Skeleton.TryGet("mKneeLeft", out var dk))
            _world.Log?.Invoke($"pose {av.Name}: seated={seated} hipL {body.Pose.Rot[dh]} kneeL {body.Pose.Rot[dk]} pelvis {body.Pose.Pos[0]} {body.Anim.Describe(_motions, assets)}");
        // root: standing avatars put their feet on the ground (pelvis-to-foot below the agent's centre), seated
        // ones sit exactly where the region puts them
        float ground = pos.Z - 0.5f * body.BodyHeight;
        PlaceRoot(body, shape, av, ref pos, rot, seated, out var rootRot);
        if (req.Self is { } self && self.Id == av.ID) PoseSelf(body, self, pos, rootRot, ground, seated, tick);
        else
        {
            body.HiddenJoints = null;
            body.Pose.Compose(pos, rootRot);
        }
        ApplyPhysics(body, shape, av, now);   // breast, belly and butt from a physics wearable (Avatars.Physics.cs)
        _posed[(src.Handle, av.LocalID)] = (body, offset);
        _worn[(src.Handle, av.LocalID)] = jelly ? (Worn.None, null) : cap != null ? (Worn.Capture, cap) : (Worn.Full, null);
        var rootM = Matrix4x4.CreateFromQuaternion(rootRot) * Matrix4x4.CreateTranslation(pos);

        // pixel area (LLPipeline::calcPixelArea over the joint extents, padded 0.25 m) and the avatar LOD
        var pose = body.Pose;
        var bmin = pos; var bmax = pos;
        for (int j = 0; j < _cd.Skeleton.NumBones; j++)
            if (_cd.Skeleton.Joints[j].IsBase) { bmin = Vector3.Min(bmin, pose.WorldPos[j]); bmax = Vector3.Max(bmax, pose.WorldPos[j]); }
        bmin -= new Vector3(0.25f); bmax += new Vector3(0.25f);
        var center = (bmin + bmax) * 0.5f;
        var half = (bmax - bmin) * 0.5f;
        body.Center = center;
        body.Diagonal = (bmax - bmin).Length();
        var lodView = v.LodView ?? v;
        float d = Vector3.Distance(center, lodView.Position);
        if (d < 16) d = 16 * (d / 16) * (d / 16);
        float pixelAngle = lodView.Height / lodView.VFov;
        float area = MathF.PI * MathF.Pow(MathF.Atan(half.Length() / MathF.Max(d, 1e-3f)) * pixelAngle, 2);
        float lf = Math.Clamp(opt.AvatarLodFactor, 0, 1);
        float lodFactor = lf * 0.7f + 0.3f;
        float numFactor = ClampRescale(numVisible, 8, 25, 1, Rescale(lf, 0, 1, 0.25f, 0.6f));
        float adjusted = area * 0.16f * lodFactor * lodFactor * numFactor * numFactor;
        body.Anim.PixelArea = area;
        var bounds = new Vector4(center, half.Length());
        if (cap != null) BeginCapture(body, pos, center, half, v, cap);

        var palette = _palette;
        var jellyColor = jelly ? JellyColor(av.ID) : Vector4.One;
        // the head with its lips moving, made this frame (not into impostor captures, nor while a mesh head hides it:
        // headMesh is MeshOrder's first)
        var lip = cap == null && (jelly || (body.Hidden & 1) == 0) ? LipVertices(av, body, now) : null;
        for (int mi = 0; mi < MeshOrder.Length; mi++)
        {
            var (type, te, kind) = MeshOrder[mi];
            SceneTexture? tex = null;
            if (jelly)
            {
                // the viewer's visually muted avatar: the body in a flat colour, whatever its attachments hid
                if (kind != 0) continue;
            }
            else
            {
                if ((body.Hidden & (1 << mi)) != 0) continue;
                if (!bakes.TryGetValue(te, out tex)) continue;
            }
            if (!_cd.Meshes.TryGetValue(type, out var lods)) continue;
            // LLViewerJoint::render: the first LOD whose minimum area the avatar reaches (area 0 always passes)
            MeshDef? md = null;
            foreach (var l in lods)
                if (adjusted >= l.MinPixelArea || l.MinPixelArea == 0) { md = l; break; }
            if (md == null || !_meshGpu.TryGetValue(md, out var g) || !body.Offsets.TryGetValue(md.Owner, out var vOff)) continue;
            if (tex != null && tex.State != TexState.Ready) continue;

            // a capture is a G-buffer: blended meshes go in masked
            bool blend = kind == 2 && cap == null;
            uint mat = dl.AddMaterial(new GpuMaterial
            {
                Color = jellyColor,
                Diffuse = tex != null ? new GpuTexRef { Index = tex.Slot, Scale = Vector2.One } : GpuTexRef.None,
                Normal = GpuTexRef.None, Specular = GpuTexRef.None, Emissive = GpuTexRef.None,
                AlphaCutoff = kind == 2 && cap != null ? 0.5f : 0.2f,   // LLDrawPoolAvatar::sMinimumAlpha
                Flags = (blend ? MaterialFlags.AlphaBlend : MaterialFlags.AlphaMask) | MaterialFlags.Avatar,
            });
            // the own body in a mirror that shows it: headless for the eyes, then whole for the mirror alone
            bool mirrorSelf = cap == null && body.MirrorPose != null && req.Self is { } me2 && me2.Id == av.ID;
            for (int pass = 0; pass < (mirrorSelf ? 2 : 1); pass++)
            {
            var ppose = pass == 0 ? pose : body.MirrorPose!;
            uint iflags = pass == 1 ? InstanceFlags.MirrorOnly : mirrorSelf ? InstanceFlags.MainOnly : AvatarInstanceFlags;
            if (pass == 0 && cap == null && req.Self is { } own && own.Id == av.ID) iflags |= InstanceFlags.Self;
            // joint palette: uploadJointMatrices (weighted meshes: pivot offset then joint world matrix) or the
            // mesh joint's world matrix (eyeballs, on their eye joints)
            int palStart;
            palette.Clear();
            if (md.Mesh.HasWeights)
            {
                foreach (int j in md.PaletteJoints)
                {
                    var w = j < 0 ? rootM : ppose.World[j];
                    var off = j < 0 ? Vector3.Zero : _skinOffset[j];
                    palette.Add(Matrix4x4.CreateTranslation(-off) * w);
                }
            }
            else
            {
                string eyeJoint = type == "eyeBallLeftMesh" ? "mEyeLeft" : "mEyeRight";
                if (!_cd.Skeleton.TryGet(eyeJoint, out var ej)) continue;
                var m = md.Mesh;
                var wr = Quaternion.Concatenate(m.Rotation, ppose.WorldRot[ej]);
                var wp = ppose.WorldPos[ej] + Vector3.Transform(ppose.Scale[ej] * m.Position, ppose.WorldRot[ej]);
                palette.Add(Matrix4x4.CreateScale(m.Scale) * Matrix4x4.CreateFromQuaternion(wr) * Matrix4x4.CreateTranslation(wp));
            }
            palStart = dl.AddPalette(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(palette));
            ulong srcAddr = body.Verts.Address + (ulong)vOff * PipelineBuilder.VertexStride;
            var range = lip != null && md.Owner == _headOwner && md.Mesh.HasWeights
                ? dl.AddSkinnedCopy(lip, g.Indices with { VertexCount = g.VertexCount }, g.Weights, palStart)
                : dl.AddSkinned(g.Indices with { VertexCount = g.VertexCount }, srcAddr, g.Weights, palStart);

            uint inst = dl.AddInstance(new GpuInstance
            {
                Model = Matrix4x4.Identity,
                N0 = new Vector4(1, 0, 0, 0), N1 = new Vector4(0, 1, 0, 0), N2 = new Vector4(0, 0, 1, 0),
                Material = mat, ObjectId = av.LocalID, Flags = iflags,
            });
            var cmd = new DrawCmd { Range = range, Instance = inst, Depth = Vector3.Distance(center, v.Position), Bounds = bounds };
            if (cap != null)
            {
                (kind == 2 ? cap.TwoSided : cap.Opaque).Add(cmd);
                continue;
            }
            if (visible || pass == 1)
            {
                if (blend) dl.AvatarAlpha.Add(new AvatarAlphaCmd(cmd, area > 64 * 64));
                else dl.Opaque.Add(cmd);
            }
            // eyeballs don't cast (renderShadow draws renderSkinned only)
            if (pass == 0 && kind != 1 && casterMask != 0)
                for (int c = 0; c < dl.ShadowCasters.Length; c++)
                    if ((casterMask & (1 << c)) != 0) dl.ShadowCasters[c].Add(cmd);
            }
        }

        if (cap != null && visible) AddImpostor(av, body, pos, v, dl);
        NameTag(av, body, shape, v, dl, req, visible);
        return pending;
    }

    readonly Dictionary<int, SceneTexture> _bakes = new();
    readonly List<Matrix4x4> _palette = new(64);

    /// <summary>Where the root goes: standing avatars put their feet on the ground (pelvis-to-foot below the agent's
    /// centre), seated ones sit exactly where the region puts them.</summary>
    static void PlaceRoot(Body body, BodyShape shape, Avatar av, ref Vector3 pos, Quaternion rot, bool seated, out Quaternion rootRot)
    {
        if (seated) { rootRot = rot; return; }
        rootRot = YawOnly(rot);
        // getRenderPosition adds a rigged mesh's pelvis fixup for avatars that aren't seated
        pos.Z += body.PelvisFixup + shape.Hover - (0.5f * body.BodyHeight - body.PelvisToFoot);
        pos += V(av.HoverHeight);
    }

    void NameTag(Avatar av, Body body, BodyShape shape, ViewSetup v, DrawList dl, RenderRequest req, bool visible)
    {
        if (!visible || !req.ShowUi || string.IsNullOrEmpty(av.Name) || !_cd.Skeleton.TryGet("mHead", out var head)) return;
        // idleCalcNameTagPosition: above the head by 0.17 of the body height, plus 25 pixels
        var pose = body.Pose;
        float dist = Vector3.Distance(pose.WorldPos[head], v.Position);
        float pixel = dist * 2 * MathF.Tan(v.VFov / 2) / Math.Max(1, v.Height);
        dl.NameTags.Add(new NameTag(pose.WorldPos[head] + new Vector3(0, 0, shape.BodyHeight * 0.17f + 25 * pixel), av.Name, 0xFFFFFFFF, av.ID, pose.WorldPos[head]));
    }

    /// <summary>A body bake's morph mask: sampled from its fifth component once, then kept with the texture in place of
    /// that component (a megabyte or four a bake), both let go with the texture (TextureStore.Drop). Null while the bake
    /// has neither.</summary>
    MorphMask? MaskFor(SceneTexture t, string region)
    {
        if (t.Mask is { } mask)
        {
            if (t.Aux != null) t.Aux = null;   // decoded again meanwhile: the same asset, the same mask
            return mask;
        }
        if (t.Aux is not { } aux) return null;
        t.Mask = mask = new MorphMask(_cd, region, aux, t.AuxWidth, t.AuxHeight);
        t.Aux = null;
        return mask;
    }

    long _animatedRebuildFrame = -1, _frameTick;
    int _rigged, _palettesMade, _lastRigged, _lastPalettes, _shapesBuilt;

    static int ShapeKey(byte[] vp, Dictionary<int, float>? animated, Dictionary<string, MorphMask> masks)
    {
        var h = new HashCode();
        h.AddBytes(vp);
        if (animated != null)
            foreach (var (id, w) in animated.OrderBy(kv => kv.Key)) { h.Add(id); h.Add((int)MathF.Round(w * 64)); }
        foreach (var (r, m) in masks.OrderBy(kv => kv.Key)) { h.Add(r); h.Add(m.GetHashCode()); }
        return h.ToHashCode();
    }

    /// <summary>Upload the shape's morphed vertices (every LOD0 section, back to back) for the skinning pre-pass.</summary>
    void Upload(Body body)
    {
        Release(body);
        body.Offsets.Clear();
        var all = new List<MeshVertex>(6000);
        foreach (var (md, mm) in body.Shape!.Morphed)
        {
            body.Offsets[md] = (uint)all.Count;
            for (int i = 0; i < mm.Coords.Length; i++)
                all.Add(new MeshVertex { Position = mm.Coords[i], Normal = mm.Normals[i], Tangent = new Vector4(1, 0, 0, 1), Uv = mm.TexCoords[i] });
        }
        body.Verts = _world.Gpu.SkinVertices.Add<MeshVertex>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(all));
    }

    /// <summary>The loading cloud (the viewer's particle cloud while an avatar isn't fully loaded): soft grey puffs
    /// filling the body's volume, deterministic per avatar.</summary>
    void AddCloud(Avatar av, Vector3 pos, ViewSetup v, DrawList dl, RenderRequest req)
    {
        float height = av.Scale.Z > 0.5f ? av.Scale.Z : 1.9f;
        var rng = new Random(av.ID.GetHashCode());
        uint mat = dl.AddMaterial(new GpuMaterial
        {
            Color = new Vector4(0.82f, 0.83f, 0.86f, 0.55f),
            Diffuse = GpuTexRef.None, Normal = GpuTexRef.None, Specular = GpuTexRef.None, Emissive = GpuTexRef.None,
            Flags = MaterialFlags.AlphaBlend,
        });
        for (int i = 0; i < 14; i++)
        {
            float t = (float)rng.NextDouble();
            var c = pos + new Vector3((float)(rng.NextDouble() - 0.5) * 0.45f, (float)(rng.NextDouble() - 0.5) * 0.45f, (t - 0.5f) * height * 0.85f);
            float s = 0.35f + (float)rng.NextDouble() * 0.3f;
            var model = Matrix4x4.CreateScale(s) * Matrix4x4.CreateTranslation(c);
            uint inst = dl.AddInstance(new GpuInstance
            {
                Model = model, N0 = new Vector4(1 / s, 0, 0, 0), N1 = new Vector4(0, 1 / s, 0, 0), N2 = new Vector4(0, 0, 1 / s, 0),
                Material = mat, ObjectId = av.LocalID, Flags = AvatarInstanceFlags,
            });
            dl.Alpha.Add(new DrawCmd { Range = _puff, Instance = inst, Depth = Vector3.Distance(c, v.Position) });
        }
        if (req.ShowUi && !string.IsNullOrEmpty(av.Name))
            dl.NameTags.Add(new NameTag(pos + new Vector3(0, 0, height * 0.5f + 0.35f), av.Name, 0xFFFFFFFF, av.ID, pos + new Vector3(0, 0, height * 0.5f - 0.2f)));
    }

    static float Rescale(float x, float x1, float x2, float y1, float y2) => y1 + (x - x1) * (y2 - y1) / (x2 - x1);
    static float ClampRescale(float x, float x1, float x2, float y1, float y2) =>
        x <= x1 ? y1 : x >= x2 ? y2 : Rescale(x, x1, x2, y1, y2);

    public int BodyCount => _bodies.Count;
}
