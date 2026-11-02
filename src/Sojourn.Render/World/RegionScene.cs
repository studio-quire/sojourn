using System.Runtime.CompilerServices;
using LibreMetaverse;
using LibreMetaverse.Assets;
using Sojourn.Render.Frame;
using Sojourn.Render.Geometry;
using Sojourn.Render.Gpu;
using Sojourn.Render.Shadows;
using LVector3 = LibreMetaverse.Vector3;
using LQuaternion = LibreMetaverse.Quaternion;

namespace Sojourn.Render.World;

/// <summary>Render-side state for one prim, refreshed from LibreMetaverse's Primitive by reference/value checks.</summary>
public sealed class SceneObject
{
    public uint LocalId;
    public Primitive Prim = null!;
    public long SeenFrame;
    // change detection: LibreMetaverse replaces these objects on full updates
    public object? PrimDataRef, SculptRef, TexturesRef;
    public LVector3 ScaleSeen;
    public ShapeKind Kind;
    public UUID Asset;
    public int ShapeHash;
    public bool AnyPlanar;
    public bool Skip;              // particle-only and other non-geometry pcodes
    public GeometryKey? GrassKey;  // this grass object's current mesh, released when its inputs change or it leaves
    public ShapeGeometry? Grass;
    public int GrassBlades;        // how many of its blades were drawn last (the pick's, as the viewer's)
    public readonly ShapeGeometry?[] Lods = new ShapeGeometry?[4];
    public Primitive? Snapshot;    // detached copy for geometry builds
    /// <summary>The resolved faces of the shape drawn last (materials and texture slots), reused while nothing they
    /// depend on changes; null until every face's assets have arrived.</summary>
    public FaceCache? Faces;
    /// <summary>The model and normal matrices for the transform drawn last.</summary>
    public Vector3 InstPos, InstScale;
    public Quaternion InstRot;
    public GpuInstance Inst;
    public bool HasInst;
    /// <summary>Whether the scene budget last left it undrawn, or without a shadow (RegionScene.Budgeted).</summary>
    public bool BudgetHidden, BudgetNoShadow;
    /// <summary>Within the main view's draw distance when last tested, in view or not: it stays until it's
    /// ViewSetup.EdgeBand past it (past the governor's cap, ViewSetup.NewDistance, too), while something new comes in
    /// only within the draw distance and the cap.</summary>
    public bool InReach;
    /// <summary>Has had a shape drawn (the load governor holds back only objects that never have).</summary>
    public bool Drawn;
    /// <summary>Let in by the load governor while it held new objects: never held again, though its shape may still
    /// be on its way.</summary>
    public bool Admitted;

    // placement, kept by the scene's index (RegionScene.Index.cs) as the region reports changes
    /// <summary>A world object whose parent is known: <see cref="WPos"/>/<see cref="WRot"/> are its region-frame
    /// transform, the boxes and sphere its extents and cull bounds.</summary>
    public bool Placed;
    /// <summary>The avatar (local id) it's attached to, or 0; attachments follow their wearer's pose every frame.</summary>
    public uint Owner;
    /// <summary>The animated object (animesh) it's part of: its linkset root's local id when the root has the Animated
    /// Mesh flag, else 0. Its rigged faces skin to that object's own skeleton (Avatars.Animesh.cs), worn or not.</summary>
    public uint Animesh;
    public Vector3 WPos;
    public Quaternion WRot = Quaternion.Identity;
    public Vector3 BoxMin, BoxMax, CullCenter;
    public float CullRadius;
    public ProbeVolume? Probe;
    /// <summary>calcLOD's radius (from the shape and scale), kept with the placement.</summary>
    public float LodRadius = -1;
    internal uint ParentSeen;
    internal bool Parented, Unplaced;
    internal LVector3 PosSeen;
    internal LQuaternion RotSeen;
    internal SceneCell? Cell;
    internal int CellSlot = -1, AllSlot = -1;
    /// <summary>Its faces being resolved off the frame (World/FaceResolver.cs), until the next draw takes them.</summary>
    internal ResolveJob? Resolving;
    /// <summary>A flexible prim's simulation (World/RegionScene.Flexi.cs), while it's drawn as one.</summary>
    internal FlexiState? Flexi;
}

/// <summary>
/// One object's faces as resolved for drawing (persistent scene state): per face its material, pass and glow, reused
/// every frame after; only culling, depth and draw lists are per frame. It stays valid while the prim's data, the
/// shape (LOD) and the PBR ids/overrides are the same objects and every texture it used is in the state and slot it
/// was in when resolved, so an object still waiting for textures keeps its faces too, until one of them arrives (or is
/// evicted), and one waiting for a material until any material arrives.
/// </summary>
public sealed class FaceCache
{
    public required ShapeGeometry Shape;
    public required object? RenderMaterialsRef, OverridesRef;
    public required CachedFace[] Faces;
    public required SceneTexture[] Textures;
    public required int[] Slots;
    public required TexState[] States;
    /// <summary>Each texture's share of the object's screen area (its repeats): what it's wanted at.</summary>
    public required float[] Factors;
    /// <summary>How many of its assets were still loading when resolved (what the full path would count).</summary>
    public int Pending;
    /// <summary>The material store's version when a material it uses was still on its way; -1 when none was.</summary>
    public long MaterialsVersion = -1;
    /// <summary>Worn: the wearer's bakes when resolved (bakes on mesh show them), as <see cref="RegionScene"/>'s BakeKey.</summary>
    public int BakeKey;
    /// <summary>The frame (plus one) it was first found out of date but drawn as it was, the frame's resolve budget being
    /// spent; 0 while current.</summary>
    public long StaleSince;
    /// <summary>The last frame it was found current (every slot it names live then): drawn out of date only within two
    /// frames of that, since a retired texture's slot and image outlive their retirement by a few frames only
    /// (RenderWorld.RetireTexture).</summary>
    public long ValidFrame;
    /// <summary>The prim's texture entry or other data changed since (RegionScene.Refresh): resolve again, drawn as it was
    /// meanwhile if the frame's resolving is spent.</summary>
    public bool Outdated;
    /// <summary>The media surfaces its faces look up (docs/media.md), told when it's drawn from the cache too.</summary>
    public Sojourn.Render.Media.MediaSurface[]? Media;
    /// <summary>Its faces' instances and materials in the renderer's persistent tables (a world object drawn in its own
    /// region's frame: RegionScene.EmitPersistent), or null; and the placement the instances were written for.</summary>
    public int[]? InstSlots, MatSlots;
    public Vector3 SlotPos, SlotScale;
    public Quaternion SlotRot;

    long _keptAt;

    /// <summary>Not drawn, but within a view's draw distance (behind the person, too small, worn between impostor
    /// captures): its textures count as near, so the store's idle and low-slot reclaiming leave them for last
    /// (TextureStore.Reclaim). At most every 10 s; any thread.</summary>
    public void KeepNear(long now)
    {
        if (now - _keptAt < 10_000) return;
        _keptAt = now;
        foreach (var t in Textures) t.KeepNear(now);
    }

    public bool Valid(ShapeGeometry shape, object? renderMaterials, object? overrides, long materialsVersion, float area)
    {
        if (Outdated) return false;
        if (!ReferenceEquals(shape, Shape) || !ReferenceEquals(renderMaterials, RenderMaterialsRef) || !ReferenceEquals(overrides, OverridesRef)) return false;
        if (MaterialsVersion >= 0 && MaterialsVersion != materialsVersion) return false;
        long now = System.Environment.TickCount64;
        for (int i = 0; i < Textures.Length; i++)
        {
            var t = Textures[i];
            if (t.State != States[i] || t.Slot != Slots[i]) return false;
            t.Wanted(now, area * Factors[i]);   // what TextureStore.Request does: kept resident, and fetched as far as it's shown
        }
        return true;
    }
}

public readonly record struct CachedFace(FaceGeometry Face, GpuMaterial Material, FaceClass Class, float Glow);

/// <summary>
/// One region's renderable world: prims (with geometry by LOD), terrain and water, built from a RegionSource on
/// demand. Sync is cheap enough to run every frame (reference checks over the region's primitive dictionary).
/// </summary>
public sealed partial class RegionScene
{
    public RegionSource Source { get; }
    readonly RenderWorld _world;
    readonly Dictionary<uint, SceneObject> _objects = new();
    long _frame;
    public Terrain Terrain { get; }
    public int ObjectCount => _objects.Count;
    public long LastUsed;
    /// <summary>Particle sources and texture animations, timestamped as they change.</summary>
    public Particles.EffectTracker Effects { get; } = new();

    public RegionScene(RenderWorld world, RegionSource source)
    {
        _world = world;
        Source = source;
        Terrain = new Terrain(world, source);
    }

    readonly Dictionary<uint, List<SceneObject>> _attachments = new();

    /// <summary>The avatar (local id) a prim is attached to, or 0.</summary>
    public uint AttachmentOwner(Primitive prim)
    {
        uint parent = prim.ParentID;
        if (parent == 0) return 0;
        if (Source.Avatars.ContainsKey(parent)) return parent;
        if (_objects.TryGetValue(parent, out var root) && root.Prim.ParentID != 0 && Source.Avatars.ContainsKey(root.Prim.ParentID))
            return root.Prim.ParentID;
        return 0;
    }

    static readonly IReadOnlyList<SceneObject> NoAttachments = Array.Empty<SceneObject>();

    /// <summary>The prims attached to an avatar (as of the last Sync).</summary>
    public IReadOnlyList<SceneObject> AttachmentsOf(uint avatarLocalId) =>
        _attachments.TryGetValue(avatarLocalId, out var l) ? l : NoAttachments;

    /// <summary>The rigged mesh shapes worn by an avatar that are loaded (any LOD), with their mesh asset ids. A worn
    /// animated object's are its own skeleton's, not the wearer's (addAttachmentOverridesForObject skips them).</summary>
    public IEnumerable<(UUID Mesh, ShapeGeometry Shape)> RiggedShapesOf(uint avatarLocalId)
    {
        foreach (var obj in AttachmentsOf(avatarLocalId))
        {
            if (obj.Kind != ShapeKind.Mesh || obj.Animesh != 0) continue;
            foreach (var l in obj.Lods)
                if (l is { Ready: true, Evicted: false, Skin: not null } && l.Key.Kind == ShapeKind.RiggedMesh) { yield return (obj.Asset, l); break; }
        }
    }

    /// <summary>Give back this region's own GPU geometry (it left the renderer). Shared shapes age out of the cache.</summary>
    public void Release()
    {
        foreach (var obj in _objects.Values) { ReleaseGrass(obj); SetFaces(obj, null); }
        Terrain.Release();
        // and its objects (their shapes' copies for picks, the prims' snapshots): a left scene can still be reachable
        // from the last frame's lists until the next frame, which out of sight on the processor can be an hour away
        _objects.Clear();
        _all.Clear();
        _children.Clear();
        _unplaced.Clear();
        _lights.Clear();
        _animesh.Clear();
        _moving.Clear();
        _attachments.Clear();
        _mirrors.Clear();
        _probeVolumes = null;
        _byId = null;
        _cells = Array.Empty<SceneCell>();
        _big.Objects.Clear();
    }

    void ReleaseGrass(SceneObject obj)
    {
        if (obj.GrassKey is not { } key) return;
        _world.Geometry.Release(key, _world.RetireRange);
        obj.GrassKey = null;
        obj.Grass = null;
    }

    void Refresh(SceneObject obj, Primitive prim)
    {
        var oldKind = obj.Kind; var oldAsset = obj.Asset; var oldHash = obj.ShapeHash; var oldPlanar = obj.AnyPlanar; var oldScale = obj.ScaleSeen;
        obj.Prim = prim;
        obj.PrimDataRef = prim.PrimData;
        obj.SculptRef = prim.Sculpt;
        obj.TexturesRef = prim.Textures;
        obj.ScaleSeen = prim.Scale;
        var pcode = prim.PrimData.PCode;
        if (pcode != PCode.Grass) ReleaseGrass(obj);
        if (pcode is PCode.Tree or PCode.NewTree or PCode.Grass)
        {
            // Linden plants (LLVOTree / LLVOGrass): meshes from Foliage/, keyed per draw (see AddTree / AddGrass)
            obj.Skip = false;
            obj.Kind = pcode == PCode.Grass ? ShapeKind.Grass : ShapeKind.Tree;
            obj.Asset = UUID.Zero;
            obj.ShapeHash = 0;
            obj.AnyPlanar = false;
            Array.Clear(obj.Lods);
            obj.Snapshot = null;
            return;
        }
        obj.Skip = pcode != PCode.Prim;
        // a sculpt parameter with a map is a sculpt whatever its type (LLVOVolume::isSculpted is the parameter's presence):
        // type none is sculpted with no stitching, edges clamped. Taking it for the plain prim drew old sculpts with a type
        // byte of 0 (Calas Galadhon's TUFF grass) as their underlying torus, huge and streaked (2026-10-29)
        if (prim.Sculpt is { } sc && sc.SculptTexture != UUID.Zero && (int)sc.Type <= (int)SculptType.Mesh)
        {
            obj.Kind = sc.Type == SculptType.Mesh ? ShapeKind.Mesh : ShapeKind.Sculpt;
            obj.Asset = sc.SculptTexture;
        }
        else
        {
            obj.Kind = ShapeKind.Prim;
            obj.Asset = UUID.Zero;
        }
        obj.ShapeHash = obj.Kind == ShapeKind.Mesh ? 0 : GeometryCache.HashShape(prim.PrimData, pcode) ^ (prim.Sculpt?.Type.GetHashCode() ?? 0) * 31
            ^ ((prim.Sculpt?.Invert ?? false) ? 0x1000 : 0) ^ ((prim.Sculpt?.Mirror ?? false) ? 0x2000 : 0);
        obj.AnyPlanar = AnyPlanarFace(prim);
        bool shapeChanged = obj.Kind != oldKind || obj.Asset != oldAsset || obj.ShapeHash != oldHash || obj.AnyPlanar != oldPlanar
                            || (obj.AnyPlanar && oldScale != prim.Scale);
        if (shapeChanged) Array.Clear(obj.Lods);
        // a full update (hover text, a colour, a sound: LibreMetaverse makes the texture entry anew) only outdates the
        // faces: with the frame's resolving spent they're drawn as they were for up to two frames, as an out-of-date cache
        // is, instead of the object blinking out until it's resolved again (it counted as new)
        if (shapeChanged) SetFaces(obj, null);
        else if (obj.Faces != null) obj.Faces.Outdated = true;
        obj.Resolving = null;   // one in flight read the old texture entry
        obj.Snapshot = new Primitive
        {
            LocalID = prim.LocalID,
            PrimData = prim.PrimData,
            Sculpt = prim.Sculpt,
            Scale = prim.Scale,
            Textures = prim.Textures,
        };
    }

    static bool AnyPlanarFace(Primitive prim)
    {
        var t = prim.Textures;
        if (t == null) return false;
        if (t.DefaultTexture?.TexMapType == MappingType.Planar) return true;
        if (t.FaceTextures != null)
            foreach (var f in t.FaceTextures)
                if (f?.TexMapType == MappingType.Planar) return true;
        return false;
    }

    // --- per view ----------------------------------------------------------------------------------

    static Vector3 V(LVector3 v) => new(v.X, v.Y, v.Z);
    static Quaternion Q(LQuaternion q) => new(q.X, q.Y, q.Z, q.W);

    /// <summary>The main view's hooks, set by the renderer before it draws this region: what places attachments
    /// outside a build (picks, TryWorldTransform, look-at), and what probe captures' and the HUD's builds start from. A
    /// build reads its own context's.</summary>
    public SceneHooks Hooks { get; set; } = SceneHooks.None;

    /// <summary>A prim's position and rotation in this region's frame (attachments follow their avatar's pose,
    /// as last set per view); false if it isn't placed.</summary>
    public bool TryWorldTransform(uint localId, out Vector3 pos, out Quaternion rot)
    {
        pos = default; rot = Quaternion.Identity;
        return _objects.TryGetValue(localId, out var obj) && Transform(obj, out pos, out rot);
    }

    Dictionary<UUID, uint>? _byId;
    long _byIdFrame = -1;

    /// <summary>Where an object or avatar is (region frame), by id: particle targets.</summary>
    public Vector3? FindPosition(UUID id)
    {
        foreach (var av in Source.Avatars.Values)
            if (av.ID == id)
            {
                var p = V(av.Position);
                if (av.ParentID != 0 && _objects.TryGetValue(av.ParentID, out var seat) && WorldTransform(seat.Prim, out var sp, out var sr))
                    p = sp + Vector3.Transform(p, sr);
                return p;
            }
        if (_byIdFrame != _frame || _byId == null)
        {
            _byId ??= new Dictionary<UUID, uint>();
            _byId.Clear();
            foreach (var o in _objects.Values) _byId[o.Prim.ID] = o.LocalId;
            _byIdFrame = _frame;
        }
        return _byId.TryGetValue(id, out uint lid) && TryWorldTransform(lid, out var pos, out _) ? pos : null;
    }

    long _byIdBuiltAt = long.MinValue;

    /// <summary>An object's position and rotation (region frame) by id: look-at targets, asked every frame, so the id
    /// map is rebuilt only when an id isn't in it, at most once a second.</summary>
    public bool TryFindObject(UUID id, out Vector3 pos, out Quaternion rot)
    {
        pos = default; rot = Quaternion.Identity;
        if (_byId != null && _byId.TryGetValue(id, out uint lid) && _objects.TryGetValue(lid, out var o) && o.Prim.ID == id)
            return Transform(o, out pos, out rot);
        long tick = System.Environment.TickCount64;
        if (tick - _byIdBuiltAt < 1000) return false;
        _byIdBuiltAt = tick;
        _byId ??= new Dictionary<UUID, uint>();
        _byId.Clear();
        foreach (var x in _objects.Values) _byId[x.Prim.ID] = x.LocalId;
        _byIdFrame = _frame;
        return _byId.TryGetValue(id, out lid) && TryWorldTransform(lid, out pos, out rot);
    }

    bool WorldTransform(Primitive prim, out Vector3 pos, out Quaternion rot)
    {
        if (_objects.TryGetValue(prim.LocalID, out var obj)) return Transform(obj, out pos, out rot);
        pos = default; rot = Quaternion.Identity;
        return false;
    }

    /// <summary>An attachment's root prim: its position and rotation are relative to the attachment point
    /// (LLViewerJointAttachment); HUD points are never drawn in the world.</summary>
    bool AttachmentRoot(Primitive root, Func<uint, int, (Vector3 Pos, Quaternion Rot)?>? attachmentFrame, out Vector3 pos, out Quaternion rot)
    {
        pos = default; rot = Quaternion.Identity;
        if (attachmentFrame == null || !Source.Avatars.ContainsKey(root.ParentID)) return false;
        int point = (int)root.PrimData.AttachmentPoint;
        if (attachmentFrame(root.ParentID, point) is not { } frame) return false;
        pos = frame.Pos + Vector3.Transform(V(root.Position), frame.Rot);
        rot = Quaternion.Normalize(Quaternion.Concatenate(Local(root).Rot, frame.Rot));   // (spinning: llTargetOmega)
        return true;
    }

    /// <summary>Bounding boxes for the sun shadow fit (LLPipeline::getVisibleExtents inputs): objects within draw
    /// distance, terrain patches (16x16 m, at least 1 m tall) and the region's water plane. A column wholly within
    /// draw distance gives its box at once (the union of its objects' boxes, which is what they'd add one by one).</summary>
    public void AddExtents(AabbSceneExtents ext, ViewSetup v, Vector3 offset)
    {
        foreach (var cell in Columns())
        {
            if (cell.Stale) cell.Refit();
            var rmin = cell.ReachMin + offset;
            var rmax = cell.ReachMax + offset;
            if (Vector3.Distance(Vector3.Clamp(v.Position, rmin, rmax), v.Position) > v.DrawDistance) continue;
            if (FarthestCorner(rmin, rmax, v.Position) <= v.DrawDistance) { ext.Add(cell.BoxMin + offset, cell.BoxMax + offset); continue; }
            foreach (var obj in cell.Objects)
            {
                if (Vector3.Distance(obj.CullCenter + offset, v.Position) - obj.CullRadius > v.DrawDistance) continue;
                ext.Add(obj.BoxMin + offset, obj.BoxMax + offset);
            }
        }
        Terrain.AddExtents(ext, offset);
        var sim = Source.Simulator;
        ext.Add(offset + new Vector3(0, 0, sim.WaterHeight), offset + new Vector3(sim.SizeX, sim.SizeY, sim.WaterHeight));
    }

    IEnumerable<SceneCell> Columns()
    {
        foreach (var c in _cells) if (c.Objects.Count > 0) yield return c;
        if (_big.Objects.Count > 0) yield return _big;
    }

    static float FarthestCorner(Vector3 min, Vector3 max, Vector3 p) => Vector3.Max(Vector3.Abs(min - p), Vector3.Abs(max - p)).Length();

    /// <summary>True when every sphere inside the box is beyond the view's draw distance and <paramref name="band"/>
    /// (Beyond's test, for a box).</summary>
    static bool BoxBeyond(ViewSetup v, Vector3 min, Vector3 max, float band = 0)
    {
        var near = Vector3.Clamp(v.Position, min, max);
        if (!v.CubeCull) return Vector3.Distance(near, v.Position) > v.DrawDistance + band;
        var d = Vector3.Abs(near - v.Position);
        return MathF.Max(d.X, MathF.Max(d.Y, d.Z)) > v.DrawDistance + band;
    }

    /// <summary>Append everything this region contributes to a view. offset: this region's origin relative to the
    /// view's region (for neighbours). Columns beyond the draw distance, or neither in view nor casting into a shadow
    /// map, are skipped whole; the rest have their objects tested one by one as the viewer would. hooks: the view's
    /// (SceneHooks), carried by the build's own context.</summary>
    public RegionDrawStats BuildDrawList(SceneHooks hooks, ViewSetup v, DrawList dl, Vector3 offset, ViewOptions opt, Frustum frustum,
        ShadowFrustum?[]? casters = null, bool objects = true, bool water = true)
    {
        LastUsed = System.Environment.TickCount64;
        var axis = Vector3.Normalize(new Vector3(-v.View.M13, -v.View.M23, -v.View.M33));
        var ctx = new DrawCtx(hooks, v, dl, offset, opt, frustum, casters, axis, Source.Assets, Source.Simulator);
        if (objects)
        {
            // lights don't depend on their prim being drawn: the viewer keeps every light within the far clip
            // (calcNearbyLights, distance less radius) and draws those whose light box is in the frustum
            foreach (var obj in _lights) AddLightOf(ctx, obj, obj.WPos, obj.WRot);
            foreach (var cell in Columns())
            {
                if (cell.Stale) cell.Refit();
                var rmin = cell.ReachMin + offset;
                var rmax = cell.ReachMax + offset;
                if (BoxBeyond(v, rmin, rmax, ViewSetup.EdgeBand)) { ctx.Stats.Culled += cell.Objects.Count; continue; }
                var rc = (rmin + rmax) * 0.5f;
                float rr = Vector3.Distance(rmin, rmax) * 0.5f;
                bool seen = frustum.Sphere(rc, rr);
                int cellCasters = CasterMask(casters, rmin, rmax);
                if (!seen && cellCasters == 0) { ctx.Stats.Culled += cell.Objects.Count; cell.KeepNear(); continue; }
                ctx.CellCasters = cellCasters;
                ctx.CellInView = seen && frustum.Contains(rc, rr);
                var list = cell.Objects;
                for (int i = 0; i < list.Count; i++)
                    if (!list[i].Skip) DrawObject(ctx, list[i], list[i].WPos, list[i].WRot);
                ctx.CellCasters = -1;
                ctx.CellInView = false;
            }
            // attachments, where their wearers are posed this frame: drawn, into the wearer's impostor capture, only
            // their lights (an impostor between captures), or not at all (a jelly doll)
            if (hooks.AttachmentFrame != null)
                foreach (var (owner, list) in _attachments) DrawWorn(ctx, owner, list);
        }
        Terrain.BuildDrawList(v, dl, offset, frustum, ref ctx.Stats, casters);
        if (offset == Vector3.Zero && v.HasWater && water) AddWater(v, dl, ref ctx.Stats);
        if (offset == Vector3.Zero) SkyTextures(v, ref ctx.Stats);
        ctx.Finish(_world);
        return ctx.Stats;
    }

    /// <summary>One wearer's attachments, where it's posed this frame: drawn, into its impostor capture, only their
    /// lights (an impostor between captures), or not at all (a jelly doll).</summary>
    void DrawWorn(DrawCtx ctx, uint owner, List<SceneObject> list)
    {
        var hooks = ctx.Hooks;
        var (mode, cap) = hooks.WornOf?.Invoke(owner) ?? (Avatars.Worn.Full, null);
        if (mode == Avatars.Worn.None) return;
        ctx.Capture = mode == Avatars.Worn.Capture ? cap : null;
        // what goes into the capture names this build's instances: noted, so they're moved along when the segment is
        // appended to the view's list (DrawList.Append)
        if (ctx.Capture is { } c) ctx.Dl.CaptureMarks.Add((c, c.Opaque.Count, c.TwoSided.Count));
        ctx.InstFlags = (owner == hooks.MirrorSelf ? 0 : hooks.AvatarFlags) | (owner == hooks.SelfOwner && ctx.Capture == null ? InstanceFlags.Self : 0);
        long now = System.Environment.TickCount64;
        foreach (var obj in list)
        {
            if (obj.Skip) continue;
            // what isn't drawn now (between impostor captures, on the own head) is still near: its textures stay
            if (mode == Avatars.Worn.Lights && obj.Prim.Light is not { Intensity: > 0 }) { obj.Faces?.KeepNear(now); continue; }
            if (!AttachmentTransform(obj.Prim, hooks.AttachmentFrame, out var pos, out var rot))
            {
                // worn on the own head, which the eyes don't draw: the mirror does
                if (owner == hooks.MirrorSelf && hooks.MirrorAttachmentFrame != null && mode == Avatars.Worn.Full) DrawInMirror(ctx, obj);
                else obj.Faces?.KeepNear(now);
                continue;
            }
            if (ctx.Capture == null) AddLightOf(ctx, obj, pos, rot);
            if (mode == Avatars.Worn.Lights) { obj.Faces?.KeepNear(now); continue; }
            DrawObject(ctx, obj, pos, rot);
        }
        ctx.Capture = null;
        ctx.InstFlags = 0;
    }

    static int CasterMask(ShadowFrustum?[]? casters, Vector3 min, Vector3 max)
    {
        if (casters == null) return 0;
        int mask = 0;
        for (int c = 0; c < casters.Length; c++)
            if (casters[c] is { } cf && cf.IntersectsAabb(min, max)) mask |= 1 << c;
        return mask;
    }

    /// <summary>Whether the scene budget leaves something of this on-screen radius out. Something left out comes back
    /// only once it's a quarter over the limit, so a thing about that size doesn't pop in and out as the head moves
    /// (leaves flickering in a forest at the budget's top step). The state is the main view's (<paramref name="track"/>);
    /// probe captures test the plain limit.</summary>
    static bool Budgeted(ref bool wasOut, float px, float limit, bool track)
    {
        bool outNow = px < limit * (track && wasOut ? 1.25f : 1f);
        if (track) wasOut = outNow;
        return outNow;
    }

    /// <summary>An attachment of the own head, for the mirror alone: placed on the mirror's whole pose, no shadow.</summary>
    void DrawInMirror(DrawCtx ctx, SceneObject obj)
    {
        // placed by the mirror's frame (nothing else of the draw reads the attachment frame), without shadows
        if (!AttachmentTransform(obj.Prim, ctx.Hooks.MirrorAttachmentFrame, out var pos, out var rot)) return;
        var savedFlags = ctx.InstFlags;
        ctx.InstFlags = InstanceFlags.MirrorOnly;
        try { DrawObject(ctx, obj, pos, rot, noCasters: true); }
        finally { ctx.InstFlags = savedFlags; }
    }

    void AddLightOf(DrawCtx ctx, SceneObject obj, Vector3 pos, Quaternion rot)
    {
        var prim = obj.Prim;
        if (prim.Light is not { } light || light.Intensity <= 0 || light.Radius <= 0) return;
        var v = ctx.V;
        var worldPos = pos + ctx.Offset;
        if (Vector3.Distance(worldPos, v.Position) - light.Radius >= v.DrawDistance) return;
        if (ctx.Frustum.Sphere(worldPos, light.Radius * 1.5f))
            ctx.Stats.Pending += AddLight(ctx.Dl, prim, light, worldPos, rot, V(prim.Scale), ctx.Assets, v.ShadowProjectors);
        else if (prim.LightMap is { } map && _world.Textures.TryGet(map.LightTexture, out var projected))
            projected.KeepNear(System.Environment.TickCount64);   // a projector out of view: its picture is near
    }

    /// <summary>One object's faces into the draw list: culled, its LOD chosen, its faces resolved or reused.</summary>
    /// <param name="noCasters">Into no shadow map, whatever the build's casters (the mirror's copy of the own head's).</param>
    void DrawObject(DrawCtx ctx, SceneObject obj, Vector3 pos, Quaternion rot, bool noCasters = false)
    {
        var prim = obj.Prim;
        var v = ctx.V;
        var dl = ctx.Dl;
        var offset = ctx.Offset;
        var opt = ctx.Opt;
        var axis = ctx.Axis;
        var casters = noCasters ? null : ctx.Casters;
        var hooks = ctx.Hooks;
        if (obj.Kind == ShapeKind.Tree) { AddTree(ctx, obj, pos, rot, casters); return; }
        if (obj.Kind == ShapeKind.Grass) { AddGrass(ctx, obj, pos, casters); return; }
        var scale = V(prim.Scale);
        float radius = 0.5f * scale.Length();
        if (obj.Flexi?.Result is { } flexed) radius = MathF.Max(radius, flexed.Radius);   // bent, it reaches further
        var worldPos = pos + offset;
        float dist = Vector3.Distance(worldPos, v.Position);
        // an animated object's rigged parts are where its skeleton is: culled by that (AddRigged), not by the prim
        bool animesh = obj.Animesh != 0 && obj.Kind == ShapeKind.Mesh && ctx.HudPx == 0 && hooks.AvatarExtents?.Invoke(obj.Animesh) != null;
        if (ctx.Capture == null && !animesh)
        {
            // the draw distance from the head's centre, with a band at its edge for the main view (leaning doesn't blink
            // what sits on it, nor does the governor's cap take back what's drawn), tested in view or not, so turning
            // the head changes nothing (docs/rendering.md > Distances from the head)
            bool main = v.LodView == null && ctx.HudPx == 0;
            bool beyond;
            if (main)
            {
                float reach = obj.InReach ? v.DrawDistance + ViewSetup.EdgeBand : MathF.Min(v.DrawDistance, v.NewDistance);
                beyond = dist - radius > reach;
                obj.InReach = !beyond;
            }
            else beyond = v.Beyond(worldPos, radius) || dist - radius > v.NewDistance;
            if (beyond) { ctx.Stats.Culled++; return; }
        }
        bool visible = ctx.Capture != null || ctx.CellInView || animesh || ctx.Frustum.Sphere(worldPos, radius);
        int casterMask = 0;
        if (casters != null && ctx.CellCasters != 0 && ctx.Capture == null)
        {
            var bmin = worldPos - new Vector3(radius);
            var bmax = worldPos + new Vector3(radius);
            for (int c = 0; c < casters.Length; c++)
                if ((ctx.CellCasters & (1 << c)) != 0 && casters[c] is { } cf && cf.IntersectsAabb(bmin, bmax)) casterMask |= 1 << c;
            // a projector's own prim stays out of its shadow map (renderShadow skips RenderSpotLight)
            for (int i = 0; i < 2; i++)
                if (v.ShadowProjectors[i] == (Source.Handle, prim.LocalID)) casterMask &= ~(1 << (4 + i));
            // smaller than a few texels in a sun cascade's map: left out of it (RenderQuality.MinCasterTexels; the far
            // cascade's own floor always)
            int sun = (v.MinCasterTexels > 0 ? 0xF : 0) | (v.FarCasterTexels > 0 ? 1 << ViewSetup.FarCascade : 0);
            if ((casterMask & sun) != 0 && !animesh)
                casterMask &= ~v.TinyCasters(worldPos, radius, casterMask & sun);
        }
        if (opt.MinShadowPixels > 0 && dist > radius && (obj.Kind != ShapeKind.Mesh || obj.Owner == 0) && !animesh && ctx.Capture == null)
        {
            // over budget: what's tiny on screen goes (its shadow first); worn meshes go by their wearer (AddRigged)
            float px = radius / dist * (v.Height * 0.5f) / MathF.Tan(v.VFov * 0.5f);
            bool track = v.LodView == null;
            if (Budgeted(ref obj.BudgetNoShadow, px, opt.MinShadowPixels, track)) casterMask = 0;
            if (Budgeted(ref obj.BudgetHidden, px, opt.MinPixels, track)) visible = false;
        }
        else if (v.LodView == null) obj.BudgetHidden = obj.BudgetNoShadow = false;
        if (!visible && casterMask == 0)
        {
            ctx.Stats.Culled++;
            obj.Faces?.KeepNear(System.Environment.TickCount64);   // within draw distance, out of view: near
            return;
        }
        // a HUD's textures go by its size on the HUD screen
        ctx.ObjArea = ctx.HudPx > 0 ? MathF.PI * radius * radius * ctx.HudPx * ctx.HudPx : ScreenArea(v, radius, dist, visible);
        // the frame lagging too much (Realtime/LoadGovernor.cs): what has never been drawn and isn't near waits, before
        // anything of it is fetched, built or resolved, and comes in at the governor's rate, largest on screen first;
        // what's worn is its wearer's
        if (!obj.Drawn && !obj.Admitted && dist - radius > _world.NewObjectRadius && ctx.Capture == null && ctx.HudPx == 0 && v.LodView == null && obj.Owner == 0 && !animesh)
        {
            if (_world.Admit(ctx.ObjArea)) obj.Admitted = true;
            else
            {
                ctx.Stats.Pending++;
                ctx.HeldObjects++;
                return;
            }
        }

        // LOD as the viewer picks it (llvovolume.cpp calcLOD), from the main camera: cube snapshots don't
        // update geometry, so probe captures draw the LODs the main view chose
        if (obj.LodRadius < 0) obj.LodRadius = VolumeLod.LodRadius(scale, prim.PrimData, obj.Kind == ShapeKind.Mesh);
        float lodRadius = obj.LodRadius;
        var lodView = v.LodView ?? v;
        float lodDist = lodView == v ? dist : Vector3.Distance(worldPos, lodView.Position);
        int lod = ctx.HudPx > 0 ? 3 : VolumeLod.CalcLod(lodDist, lodRadius, opt.LodFactor, lodView.LodFov ?? lodView.VFov);   // HUDs: always their best
        if (lod < 0) lod = 3;
        // meshes worn by an avatar may be rigged: their shape keeps the skin (not on a HUD, which has no skeleton); an
        // animated object's skin to its own skeleton, worn or not
        uint owner = animesh ? obj.Animesh : obj.Kind == ShapeKind.Mesh && ctx.HudPx == 0 ? obj.Owner : 0;
        ctx.Wearer = obj.Owner;
        var kind = owner != 0 ? ShapeKind.RiggedMesh : obj.Kind;
        // rigged LOD (calcLOD): the avatar's distance and the full diagonal of its animated extents
        int RiggedLod((Vector3 Center, float Diagonal) ext)
        {
            int rlod = VolumeLod.CalcLod(Vector3.Distance(ext.Center + offset, lodView.Position), ext.Diagonal, opt.LodFactor, lodView.LodFov ?? lodView.VFov);
            return rlod < 0 ? 3 : rlod;
        }
        // a worn mesh whose header says it's rigged goes straight to the wearer's LOD: asking for the prim's own first
        // built a second copy at a LOD it doesn't show (SL heads' lower LODs are often a placeholder or a coarse face),
        // drawn until the right one was in
        if (owner != 0 && _world.Geometry.MeshRigged(obj.Asset) && hooks.AvatarExtents?.Invoke(owner) is { } known) lod = RiggedLod(known);
        var shape = GetShape(ctx, obj, lod, out bool waiting, kind);
        if (shape?.Skin != null && hooks.AvatarExtents?.Invoke(owner) is { } ext && RiggedLod(ext) is var rlod && rlod != lod)
            shape = GetShape(ctx, obj, rlod, out waiting, kind) ?? shape;
        if (waiting) ctx.Stats.Pending++;
        if (shape == null) return;
        obj.Drawn = true;
        if (shape.Skin != null)
        {
            ctx.Stats.Pending += AddRigged(ctx, obj, prim, shape, owner, casters);
            return;
        }
        // a flexible prim: stepped when due, drawn from its latest shape (World/RegionScene.Flexi.cs)
        var flexi = FlexiFor(ctx, obj, pos, rot, scale, shape.Key.Lod, visible, dist);

        if (!obj.HasInst || obj.InstPos != worldPos || obj.InstRot != rot || obj.InstScale != scale)
        {
            var model = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(worldPos);
            Matrix4x4.Invert(model, out var inv);
            var nm = Matrix4x4.Transpose(inv);
            obj.Inst = new GpuInstance
            {
                Model = model,
                N0 = new Vector4(nm.M11, nm.M12, nm.M13, 0),
                N1 = new Vector4(nm.M21, nm.M22, nm.M23, 0),
                N2 = new Vector4(nm.M31, nm.M32, nm.M33, 0),
                ObjectId = prim.LocalID,
            };
            (obj.InstPos, obj.InstRot, obj.InstScale, obj.HasInst) = (worldPos, rot, scale, true);
        }
        var baseInst = obj.Inst;
        baseInst.Flags = ctx.InstFlags;
        ctx.AlphaGroup = GroupDepth(shape, obj.Inst.Model, v.Position);
        var gltfOverrides = ctx.Sim.GLTFMaterialOverrides.TryGetValue(prim.LocalID, out var ov) ? ov : null;
        var renderMaterials = Source.RenderMaterialIds(prim.LocalID);
        var bounds = new Vector4(worldPos, radius);
        ctx.Stats.Objects++;
        SetAnim(ctx, prim, shape);
        // persistent faces, worn ones too while the wearer's bakes stay the same (bakes on mesh show them); texture
        // animations stay per frame
        bool cacheable = ctx.Anim == null;
        int bakeKey = owner != 0 ? BakeKey(ctx, owner) : 0;
        var resolver = _world.Resolver;
        // faces resolved off the frame (World/FaceResolver.cs): taken when they're for what's drawn now
        if (cacheable && obj.Resolving is { Done: true } done)
        {
            // only when it's current now (a texture may have changed state since): a stale result is never drawn
            obj.Resolving = null;
            if (done.Result is { } r && done.Fits(shape, bakeKey, renderMaterials, gltfOverrides)
                && r.Valid(shape, renderMaterials, gltfOverrides, _world.Materials.Version, ctx.ObjArea)
                && !(obj.Faces is { } have && have.BakeKey == bakeKey && have.Valid(shape, renderMaterials, gltfOverrides, _world.Materials.Version, ctx.ObjArea)))
            {
                r.ValidFrame = _world.FrameNumber;
                SetFaces(obj, r);
            }
        }
        if (cacheable && obj.Faces is { } cached)
        {
            bool current = cached.BakeKey == bakeKey && cached.Valid(shape, renderMaterials, gltfOverrides, _world.Materials.Version, ctx.ObjArea);
            if (current) { cached.StaleSince = 0; cached.ValidFrame = _world.FrameNumber; }
            else if (resolver != null && !cached.Shape.Evicted)
            {
                // out of date: resolved off the frame, drawn as it was meanwhile for up to two frames after it was last
                // current (a retired texture's slot and image last longer: RenderWorld.RetireTexture), then here, below
                obj.Resolving ??= Resolve(resolver, ctx, obj, shape, owner, bakeKey, renderMaterials, gltfOverrides, visible ? dist : float.PositiveInfinity);
                current = _world.FrameNumber - cached.ValidFrame <= 2;
            }
            else if (ctx.ResolveSpent(_world) && !cached.Shape.Evicted)
            {
                // this frame's resolving is spent (a burst of textures arriving touches many objects at once): draw it
                // as it was for up to two frames after it was last current
                current = _world.FrameNumber - cached.ValidFrame <= 2;
            }
            if (current)
            {
                if (cached.Media != null && visible)
                    foreach (var m in cached.Media) Sojourn.Render.Media.MediaSurfaces.Seen(m, ctx.ObjArea, dist);
                if (flexi is { } fx)
                {
                    var finst = FlexiInstance(prim, worldPos, rot, ctx.InstFlags);
                    foreach (var f in cached.Faces)
                        if (!EmitFlexiFace(ctx, fx, f.Face.TeIndex, finst, f.Material, f.Class, f.Glow, worldPos, visible, casterMask, bounds))
                            EmitFace(dl, v, f.Face, f.Face.Range, baseInst, f.Material, f.Class, f.Glow, worldPos, axis, visible, casterMask, bounds, into: ctx.Capture,
                                group: ctx.AlphaGroup);
                }
                else if (Persistable(ctx, obj)) EmitPersistent(obj, cached, dl, v, pos, rot, scale, axis, visible, casterMask, bounds, ctx.AlphaGroup);
                else
                    foreach (var f in cached.Faces)
                        EmitFace(dl, v, f.Face, f.Face.Range, baseInst, f.Material, f.Class, f.Glow, worldPos, axis, visible, casterMask, bounds, into: ctx.Capture,
                            group: ctx.AlphaGroup);
                ctx.Stats.Pending += cached.Pending;
                return;
            }
        }
        else if (cacheable && resolver != null && (dist > 16f || ctx.ResolveSpent(_world)) && ctx.Capture == null)
        {
            // new: resolved off the frame (but here at once when near, while the frame's resolving lasts) and drawn once
            // that's done
            obj.Resolving ??= Resolve(resolver, ctx, obj, shape, owner, bakeKey, renderMaterials, gltfOverrides, visible ? dist : float.PositiveInfinity);
            ctx.Stats.Pending++;
            return;
        }
        else if (cacheable && ctx.ResolveSpent(_world) && dist > 16f && ctx.Capture == null)
        {
            // new and not near, with the frame's resolving spent: it's drawn from the next frame on, the near first
            ctx.Stats.Pending++;
            return;
        }
        long resolveStart = System.Diagnostics.Stopwatch.GetTimestamp();
        long materialsVersion = _world.Materials.Version;   // before: one arriving while this resolves must count
        ctx.Deps = cacheable ? new List<SceneTexture>() : null;
        ctx.DepFactors.Clear();
        ctx.MaterialsWaiting = false;
        var faces = cacheable ? new List<CachedFace>(shape.Faces.Length) : null;
        int pendingBefore = ctx.Stats.Pending;
        (ctx.MediaPrim, ctx.MediaDist, ctx.MediaUsed) = (prim.LocalID, visible ? dist : float.PositiveInfinity, null);
        foreach (var face in shape.Faces)
        {
            if (face.Range.IsEmpty) continue;
            var te = prim.Textures?.GetFace((uint)face.TeIndex);
            if (te == null) continue;
            if (owner != 0) te = BakeOnMesh(te, owner);
            GpuMaterial mat;
            FaceClass fc;
            if (Invisiprim(te)) ClassifyInvisiprim(obj, te, face, renderMaterials, out mat, out fc);
            else ctx.Stats.Pending += ResolveFace(ctx, te, face, renderMaterials, gltfOverrides?.FaceOverrides, out mat, out fc, owner);
            if (fc.Pass == DrawPass.None) continue;
            float glow = MathF.Floor(Math.Clamp(te.Glow, 0f, 1f) * 255f) / 255f;   // the viewer's emissive vertex alpha: (U8)(glow * 255)
            faces?.Add(new CachedFace(face, mat, fc, glow));
            if (flexi is not { } fx || !EmitFlexiFace(ctx, fx, face.TeIndex, FlexiInstance(prim, worldPos, rot, ctx.InstFlags), mat, fc, glow, worldPos, visible, casterMask, bounds))
                EmitFace(dl, v, face, face.Range, baseInst, mat, fc, glow, worldPos, axis, visible, casterMask, bounds, into: ctx.Capture,
                    group: ctx.AlphaGroup);
        }
        // kept while some of its textures or materials are still on their way too: it holds what each one was when
        // resolved, and stops being valid as soon as any of them changes state (or any material arrives)
        if (faces != null)
            SetFaces(obj, new FaceCache
            {
                Shape = shape, RenderMaterialsRef = renderMaterials, OverridesRef = gltfOverrides, Faces = faces.ToArray(),
                Textures = ctx.Deps!.ToArray(), Slots = ctx.Deps!.Select(t => t.Slot).ToArray(), States = ctx.Deps!.Select(t => t.State).ToArray(),
                Factors = ctx.DepFactors.ToArray(),
                Pending = ctx.Stats.Pending - pendingBefore,
                MaterialsVersion = ctx.MaterialsWaiting ? materialsVersion : -1,
                ValidFrame = _world.FrameNumber,
                BakeKey = bakeKey,
                Media = ctx.MediaUsed?.ToArray(),
            });
        ctx.MediaPrim = 0;
        ctx.MediaUsed = null;
        ctx.Deps = null;
        ctx.Anim = null;
        _world.AddResolve(System.Diagnostics.Stopwatch.GetTimestamp() - resolveStart);
    }

    ResolveJob Resolve(FaceResolver resolver, DrawCtx ctx, SceneObject obj, ShapeGeometry shape, uint owner, int bakeKey, UUID[]? renderMaterials,
        object? overrides, float dist)
    {
        var job = new ResolveJob(this, obj, shape, owner, bakeKey, renderMaterials, overrides, ctx.ObjArea, dist, ctx.Assets, ctx.Sim);
        resolver.Enqueue(job);
        return job;
    }

    /// <summary>An object's faces resolved on a resolver thread (FaceResolver): what DrawObject's resolving makes for its
    /// cache, without drawing. Reads the prim and the stores as the frame does; the object itself isn't touched.</summary>
    internal FaceCache? ResolveOff(ResolveJob job)
    {
        var obj = job.Obj;
        var prim = obj.Prim;
        var shape = job.Shape;
        if (shape.Evicted || !shape.Ready) return null;
        long materialsVersion = _world.Materials.Version;   // before: one arriving while this resolves must count
        var ctx = new DrawCtx(job.Assets, job.Sim) { ObjArea = job.Area, Deps = new List<SceneTexture>(), Wearer = obj.Owner };
        (ctx.MediaPrim, ctx.MediaDist, ctx.MediaUsed) = (prim.LocalID, job.Distance, null);
        var gltfOverrides = job.Overrides as LibreMetaverse.GLTFMaterialOverrideCacheEntry;
        var faces = new List<CachedFace>(shape.Faces.Length);
        int pending = 0;
        foreach (var face in shape.Faces)
        {
            if (face.Range.IsEmpty) continue;
            var te = prim.Textures?.GetFace((uint)face.TeIndex);
            if (te == null) continue;
            if (job.Owner != 0) te = BakeOnMesh(te, job.Owner);
            GpuMaterial mat;
            FaceClass fc;
            if (Invisiprim(te)) ClassifyInvisiprim(obj, te, face, job.RenderMaterials, out mat, out fc);
            else pending += ResolveFace(ctx, te, face, job.RenderMaterials, gltfOverrides?.FaceOverrides, out mat, out fc, job.Owner);
            if (fc.Pass == DrawPass.None) continue;
            float glow = MathF.Floor(Math.Clamp(te.Glow, 0f, 1f) * 255f) / 255f;
            faces.Add(new CachedFace(face, mat, fc, glow));
        }
        return new FaceCache
        {
            Shape = shape, RenderMaterialsRef = job.RenderMaterials, OverridesRef = job.Overrides, Faces = faces.ToArray(),
            Textures = ctx.Deps.ToArray(), Slots = ctx.Deps.Select(t => t.Slot).ToArray(), States = ctx.Deps.Select(t => t.State).ToArray(),
            Factors = ctx.DepFactors.ToArray(),
            Pending = pending,
            MaterialsVersion = ctx.MaterialsWaiting ? materialsVersion : -1,
            BakeKey = job.BakeKey,
            Media = ctx.MediaUsed?.ToArray(),
        };
    }

    void SkyTextures(ViewSetup v, ref RegionDrawStats stats)
    {
        var assets = Source.Assets;
        int Slot(UUID id, ref RegionDrawStats st)
        {
            if (id == UUID.Zero) return -1;
            var t = _world.Textures.Request(id, assets);
            if (t.State == TexState.Loading) st.Pending++;
            return t.State == TexState.Ready ? t.Slot : -1;
        }
        v.CloudNoiseSlotA = Slot(v.CloudNoiseA, ref stats);
        v.CloudNoiseSlotB = Slot(v.CloudNoiseB, ref stats);
        if (v.CloudNoiseSlotB < 0) v.CloudNoiseSlotB = v.CloudNoiseSlotA;
        // sun and moon discs, star sprite, rainbow and halo maps: only what will be drawn
        var s = v.SkyU;
        int flags = v.Sky.heavenly_flags;
        if ((flags & GpuSky.SunDiscFlag) != 0)
        {
            v.Sky.sun_tex_a = Slot(s.SunTexture, ref stats);
            v.Sky.sun_tex_b = Slot(s.SunTextureNext, ref stats);
            if (v.Sky.sun_tex_a < 0) v.Sky.sun_tex_a = v.Sky.sun_tex_b;
            if (v.Sky.sun_tex_b < 0) v.Sky.sun_tex_b = v.Sky.sun_tex_a;
        }
        if ((flags & GpuSky.MoonDiscFlag) != 0) v.Sky.moon_tex = Slot(s.MoonTexture, ref stats);
        if ((flags & GpuSky.StarsFlag) != 0) v.Sky.star_tex = Slot(s.StarTexture, ref stats);
        if (v.Sky.moisture_level > 0f) v.Sky.rainbow_tex = Slot(s.RainbowTexture, ref stats);
        if (v.Sky.ice_level > 0f) v.Sky.halo_tex = Slot(s.HaloTexture, ref stats);
    }

    void AddWater(ViewSetup v, DrawList dl, ref RegionDrawStats stats)
    {
        var assets = Source.Assets;
        if (v.WaterNormalA != UUID.Zero)
        {
            var a = _world.Textures.Request(v.WaterNormalA, assets);
            v.WaterNormalSlotA = a.State == TexState.Ready ? a.Slot : -1;
            if (a.State == TexState.Loading) stats.Pending++;
        }
        if (v.WaterNormalB != UUID.Zero)
        {
            var b = _world.Textures.Request(v.WaterNormalB, assets);
            v.WaterNormalSlotB = b.State == TexState.Ready ? b.Slot : -1;
            if (b.State == TexState.Loading) stats.Pending++;
        }
        if (v.WaterNormalSlotB < 0) v.WaterNormalSlotB = v.WaterNormalSlotA;
        uint inst = dl.AddInstance(new GpuInstance
        {
            Model = Matrix4x4.CreateTranslation(0, 0, v.WaterHeight),
            N0 = new Vector4(1, 0, 0, 0), N1 = new Vector4(0, 1, 0, 0), N2 = new Vector4(0, 0, 1, 0),
            Material = dl.AddMaterial(new GpuMaterial { Diffuse = GpuTexRef.None, Normal = GpuTexRef.None, Specular = GpuTexRef.None, Emissive = GpuTexRef.None, Flags = MaterialFlags.Water }),
        });
        dl.Water.Add(new DrawCmd { Range = _world.WaterMesh, Instance = inst });
    }

    static readonly bool AvatarDebug = System.Environment.GetEnvironmentVariable("SOJOURN_AVATAR_DEBUG") == "1";
    static readonly UUID InvisiprimA = new("38b86f85-2575-52a9-a531-23108d8da837");
    static readonly UUID InvisiprimB = new("e97cf410-8e61-7005-ec06-629eba4cd1fb");

    /// <summary>A rigged mesh worn by an avatar: skinned to its posed skeleton (the attachment point and the prim's
    /// own transform don't matter: llvovolume "rigged meshes ignore their model matrix"), culled and depth-sorted
    /// with the avatar.</summary>
    int AddRigged(DrawCtx ctx, SceneObject obj, Primitive prim, ShapeGeometry shape, uint owner, ShadowFrustum?[]? casters)
    {
        var (hooks, v, dl, offset, frustum, axis, opt) = (ctx.Hooks, ctx.V, ctx.Dl, ctx.Offset, ctx.Frustum, ctx.Axis, ctx.Opt);
        if (hooks.RiggedPalette == null || hooks.AvatarExtents?.Invoke(owner) is not { } ext) return 0;
        var center = ext.Center + offset;
        float radius = ext.Diagonal * 0.5f + 0.5f;
        bool visible = ctx.Capture != null || frustum.Sphere(center, radius);
        int casterMask = 0;
        if (casters != null && ctx.Capture == null)
            for (int c = 0; c < casters.Length; c++)
                if (casters[c] is { } cf && cf.IntersectsAabb(center - new Vector3(radius), center + new Vector3(radius))) casterMask |= 1 << c;
        float avDist = Vector3.Distance(center, v.Position);
        if (opt.MinShadowPixels > 0 && avDist > radius && ctx.Capture == null)
        {
            // a worn mesh is many faces: over budget, a far avatar's shadow goes well before the avatar does
            float px = radius / avDist * (v.Height * 0.5f) / MathF.Tan(v.VFov * 0.5f);
            bool track = v.LodView == null;
            if (Budgeted(ref obj.BudgetNoShadow, px, opt.MinShadowPixels * 4, track)) casterMask = 0;
            if (Budgeted(ref obj.BudgetHidden, px, opt.MinPixels, track)) visible = false;
        }
        else if (v.LodView == null) obj.BudgetHidden = obj.BudgetNoShadow = false;
        if (!visible && casterMask == 0) return 0;
        int pal = hooks.RiggedPalette(owner, shape, dl);
        if (AvatarDebug)
            _world.Log?.Invoke($"rigged {prim.LocalID}: pal {pal} faces {shape.Faces.Length} weights {string.Join(",", shape.Faces.Select(f => f.Weights.Count))} " +
                $"bounds {shape.Min}..{shape.Max} joints {string.Join(",", shape.Skin!.JointNames)} ibm0 {shape.InverseBind?[0]} bsm {string.Join(",", shape.Skin.BindShapeMatrix)} ext {ext.Center} visible {visible}");
        if (pal < 0) return 0;
        var inst = new GpuInstance
        {
            Model = Matrix4x4.Identity,
            N0 = new Vector4(1, 0, 0, 0), N1 = new Vector4(0, 1, 0, 0), N2 = new Vector4(0, 0, 1, 0),
            ObjectId = prim.LocalID,
        };
        var gltfOverrides = Source.Simulator.GLTFMaterialOverrides.TryGetValue(prim.LocalID, out var ov) ? ov : null;
        var renderMaterials = Source.RenderMaterialIds(prim.LocalID);
        var arenas = _world.Gpu.GeometryPool.Arenas;
        var bounds = new Vector4(center, radius);
        SetAnim(ctx, prim, shape);
        // the own body in a mirror that shows it: headless for the eyes, whole (skinned again) for the mirror
        int mirrorPal = owner == hooks.MirrorSelf && ctx.Capture == null && hooks.MirrorPalette != null ? hooks.MirrorPalette(owner, shape, dl) : -1;
        if (ctx.InstFlags == InstanceFlags.MirrorOnly)
        {
            // worn on the hidden head (DrawInMirror): the mirror's copy alone
            if (mirrorPal < 0) return 0;
            (pal, mirrorPal, casterMask, visible) = (mirrorPal, -1, 0, true);
        }
        inst.Flags = mirrorPal >= 0 ? InstanceFlags.MainOnly | (ctx.InstFlags & InstanceFlags.Self) : ctx.InstFlags;
        var mirrorInst = inst with { Flags = InstanceFlags.MirrorOnly };
        void Emit(FaceGeometry face, in GpuMaterial mat, FaceClass fc, float glow)
        {
            ulong src = arenas[face.Range.Arena].Vertices.Address + (ulong)face.Range.FirstVertex * PipelineBuilder.VertexStride;
            var skinned = dl.AddSkinned(face.Range, src, face.Weights.Address, pal);
            EmitFace(dl, v, face, skinned, inst, mat, fc, glow, center, axis, visible, casterMask, bounds, rigged: true, into: ctx.Capture);
            if (mirrorPal < 0) return;
            var mirrored = dl.AddSkinned(face.Range, src, face.Weights.Address, mirrorPal);
            EmitFace(dl, v, face, mirrored, mirrorInst, mat, fc, glow, center, axis, true, 0, bounds, rigged: true);
        }
        // the faces' materials are kept as for other objects (skinning still runs every frame); the area is the avatar's
        bool cacheable = ctx.Anim == null;
        int bakeKey = BakeKey(ctx, owner);
        ctx.ObjArea = ScreenArea(v, radius, Vector3.Distance(center, v.Position), visible);
        if (cacheable && obj.Faces is { } cached && cached.BakeKey == bakeKey && cached.Valid(shape, renderMaterials, gltfOverrides, _world.Materials.Version, ctx.ObjArea))
        {
            foreach (var f in cached.Faces) Emit(f.Face, f.Material, f.Class, f.Glow);
            return cached.Pending;
        }
        ctx.Deps = cacheable ? new List<SceneTexture>() : null;
        ctx.DepFactors.Clear();
        ctx.MaterialsWaiting = false;
        var faces = cacheable ? new List<CachedFace>(shape.Faces.Length) : null;
        long materialsVersion = _world.Materials.Version;
        int pending = 0;
        foreach (var face in shape.Faces)
        {
            if (face.Range.IsEmpty || face.Weights.Count == 0) continue;
            var te = prim.Textures?.GetFace((uint)face.TeIndex);
            if (te == null || te.TextureID == InvisiprimA || te.TextureID == InvisiprimB) continue;
            te = BakeOnMesh(te, owner);
            pending += ResolveFace(ctx, te, face, renderMaterials, gltfOverrides?.FaceOverrides, out var mat, out var fc, owner);
            if (fc.Pass == DrawPass.None) continue;
            float glow = MathF.Floor(Math.Clamp(te.Glow, 0f, 1f) * 255f) / 255f;
            faces?.Add(new CachedFace(face, mat, fc, glow));
            Emit(face, mat, fc, glow);
        }
        if (faces != null)
            SetFaces(obj, new FaceCache
            {
                Shape = shape, RenderMaterialsRef = renderMaterials, OverridesRef = gltfOverrides, Faces = faces.ToArray(),
                Textures = ctx.Deps!.ToArray(), Slots = ctx.Deps!.Select(t => t.Slot).ToArray(), States = ctx.Deps!.Select(t => t.State).ToArray(),
                Factors = ctx.DepFactors.ToArray(), Pending = pending,
                MaterialsVersion = ctx.MaterialsWaiting ? materialsVersion : -1,
                BakeKey = bakeKey,
            });
        ctx.Deps = null;
        ctx.Anim = null;
        return pending;
    }

    // the texture animation of the prim whose faces are being added, if any, into the build's context
    void SetAnim(DrawCtx ctx, Primitive prim, ShapeGeometry shape)
    {
        ctx.Anim = Effects.Anim(prim.LocalID);
        if (ctx.Anim == null) return;
        ctx.AnimFaces = 0;
        foreach (var f in shape.Faces) ctx.AnimFaces = Math.Max(ctx.AnimFaces, f.TeIndex + 1);
        ctx.AnimElapsed = Particles.EffectClock.Now - ctx.Anim.Start;
    }

    /// <summary>A texture animation replaces the face's texture transform in every channel (LLFace::getGeometryVolume
    /// with mTextureMatrix); GLTF faces keep their own.</summary>
    static void ApplyAnim(DrawCtx ctx, ref GpuMaterial mat, Primitive.TextureEntryFace te, int teIndex)
    {
        if (ctx.Anim is not { } st || !Particles.TextureAnimation.Covers(st.Anim, teIndex, ctx.AnimFaces)) return;
        float offS = te.OffsetU, offT = te.OffsetV, scaleS = te.RepeatU, scaleT = te.RepeatV, rot = te.Rotation;
        Particles.TextureAnimation.Apply(st.Anim, ctx.AnimElapsed, ref offS, ref offT, ref scaleS, ref scaleT, ref rot);
        static void Set(ref GpuTexRef r, float os, float ot, float ss, float st, float ro)
        {
            r.Offset = new Vector2(os, ot);
            r.Scale = new Vector2(ss, st);
            r.Rotation = ro;
        }
        Set(ref mat.Diffuse, offS, offT, scaleS, scaleT, rot);
        if (mat.Normal.Index >= 0) Set(ref mat.Normal, offS, offT, scaleS, scaleT, rot);
        if (mat.Specular.Index >= 0) Set(ref mat.Specular, offS, offT, scaleS, scaleT, rot);
    }

    /// <summary>The bakes-on-mesh texture ids and the avatar texture entry each stands for (llcommon
    /// indra_constants IMG_USE_BAKED_*; LLViewerObject::getBakedTextureForMagicId).</summary>
    public static readonly Dictionary<UUID, int> BakeOnMeshIds = new (string Id, int Te)[]
    {
        ("5a9f4a74-30f2-821c-b88d-70499d3e7183", 8), ("ae2de45c-d252-50b8-5c6e-19f39ce79317", 9),
        ("24daea5f-0539-cfcf-047f-fbc40b2786ba", 10), ("52cc6bb6-2ee5-e632-d3ad-50197b1dcb8a", 11),
        ("43529ce8-7faa-ad92-165a-bc4078371687", 19), ("09aac1fb-6bce-0bee-7d44-caac6dbb6c63", 20),
        ("ff62763f-d60a-9855-890b-0c96f8f8cd98", 40), ("8e915e25-31d1-cc95-ae08-d58a47488251", 41),
        ("9742065b-19b5-297c-858a-29711d539043", 42), ("03642e83-2bd1-4eb9-34b4-4c47ed586d2d", 43),
        ("edd51b77-fc10-ce7a-4b3d-011dfc349e4f", 44),
    }.ToDictionary(e => new UUID(e.Id), e => e.Te);

    static readonly UUID ImgDefault = new("d2114404-dd59-4a4d-8e6c-49359e91bbf0");

    /// <summary>The wearer's bakes as one number: a worn object's cached faces hold while it stays the same.</summary>
    int BakeKey(DrawCtx ctx, uint owner)
    {
        // asked for every worn mesh: once a build per wearer
        var keys = ctx.BakeKeys;
        if (keys.TryGetValue(owner, out int known)) return known;
        return keys[owner] = ComputeBakeKey(owner);
    }

    int ComputeBakeKey(uint owner)
    {
        if (!Source.Avatars.TryGetValue(owner, out var av) || av.Textures is not { } t) return 0;
        var h = new HashCode();
        h.Add(RuntimeHelpers.GetHashCode(t));
        if (t.FaceTextures is { } ft)
            foreach (int te in BakeOnMeshIds.Values)
                if (te < ft.Length) h.Add(ft[te]?.TextureID ?? UUID.Zero);
        return h.ToHashCode() | 1;
    }

    /// <summary>An attachment face using a bakes-on-mesh id shows the wearer's bake (IMG_DEFAULT if it has none).</summary>
    Primitive.TextureEntryFace BakeOnMesh(Primitive.TextureEntryFace te, uint owner)
    {
        if (!BakeOnMeshIds.TryGetValue(te.TextureID, out int bakeTe)) return te;
        var copy = (Primitive.TextureEntryFace)te.Clone();
        copy.TextureID = WearerBake(owner, bakeTe);
        return copy;
    }

    /// <summary>LLViewerObject::getBakedTextureForMagicId for something worn: the wearer's bake for a bakes-on-mesh id,
    /// IMG_DEFAULT while it has none; any other id as it is.</summary>
    UUID BakeOnMesh(UUID id, uint owner) => owner != 0 && BakeOnMeshIds.TryGetValue(id, out int bakeTe) ? WearerBake(owner, bakeTe) : id;

    UUID WearerBake(uint owner, int bakeTe)
    {
        var bake = UUID.Zero;
        if (Source.Avatars.TryGetValue(owner, out var av) && av.Textures is { } t)
            bake = (t.FaceTextures != null && bakeTe < t.FaceTextures.Length ? t.FaceTextures[bakeTe]?.TextureID : null) ?? UUID.Zero;
        return bake != UUID.Zero ? bake : ImgDefault;
    }

    ShapeGeometry? GetShape(DrawCtx ctx, SceneObject obj, int lod, out bool waiting, ShapeKind? kindOverride = null)
    {
        var assets = ctx.Assets;
        waiting = false;
        var kind = kindOverride ?? obj.Kind;
        var want = obj.Lods[lod];
        if (want != null && want.Key.Kind != kind) { Array.Clear(obj.Lods); want = null; }
        if (want == null || want.Evicted)
        {
            var key = new GeometryKey(kind, obj.Asset, obj.ShapeHash, lod, obj.AnyPlanar ? V(obj.Prim.Scale) : Vector3.Zero);
            want = obj.Lods[lod] = _world.Geometry.Request(key, obj.Snapshot!, assets, _world.Textures);
        }
        if (!want.Ready) _world.Geometry.Wanted(want, ctx.ObjArea);
        want.LastUsed = System.Environment.TickCount64;
        if (want.Ready) return want;
        if (!want.Failed) waiting = true;
        // fall back to the nearest LOD that's ready (the viewer keeps drawing the old LOD while a new one builds)
        for (int d = 1; d < 4; d++)
            foreach (var l in new[] { lod - d, lod + d })
                if (l is >= 0 and < 4 && obj.Lods[l] is { Ready: true, Evicted: false } s) { s.LastUsed = want.LastUsed; return s; }
        return null;
    }

    /// <summary>About how many pixels an object covers (its bounding sphere's disc): what its textures are fetched for
    /// (LLViewerTextureList::updateImageDecodePriority's virtual size). Only casting a shadow, much less.</summary>
    static float ScreenArea(ViewSetup v, float radius, float dist, bool visible)
    {
        if (dist <= radius) return float.PositiveInfinity;
        float r = radius / dist * (v.Height * 0.5f) / MathF.Tan(v.VFov * 0.5f);
        return MathF.PI * r * r * (visible ? 1f : 0.1f);
    }

    /// <summary>A texture repeated n times across a face needs 1/n^2 of the texels (the viewer's TE scale factor,
    /// clamped to TextureScaleMin/MaxAreaFactor).</summary>
    static float RepeatFactor(Primitive.TextureEntryFace te)
    {
        float s = MathF.Min(MathF.Abs(te.RepeatU), MathF.Abs(te.RepeatV));
        return 1f / Math.Clamp(s * s, 0.0095f, 25f);
    }

    static SceneTexture Tex(DrawCtx ctx, TextureStore textures, UUID id)
    {
        if (ctx.HoldTextures > 0) LookHold.Hold(textures, id, ctx.HoldTextures);
        var t = textures.Request(id, ctx.Assets, ctx.ObjArea * ctx.TexFactor);
        if (ctx.Deps != null) { ctx.Deps.Add(t); ctx.DepFactors.Add(ctx.TexFactor); }
        return t;
    }

    /// <summary>A face's material and pass from its texture entry, GLTF material and override, legacy material and
    /// textures (requesting what's missing); returns how many of those are still loading.</summary>
    /// <param name="wearer">The avatar wearing the face (0: not worn): bakes-on-mesh ids in its GLTF material show that
    /// avatar's bakes, as its legacy texture's do (<see cref="BakeOnMesh(Primitive.TextureEntryFace, uint)"/>).</param>
    int ResolveFace(DrawCtx ctx, Primitive.TextureEntryFace te, FaceGeometry face, UUID[]? renderMaterials, IReadOnlyDictionary<int, AssetMaterial>? overrides,
        out GpuMaterial mat, out FaceClass fc, uint wearer = 0)
    {
        int pending = 0;
        var assets = ctx.Assets;
        var textures = _world.Textures;
        ctx.HoldTextures = LookHold.Active && (wearer != 0 ? wearer : ctx.Wearer) is var by and not 0 && Source.Avatars.TryGetValue(by, out var heldBy)
            ? LookHold.WornBy(heldBy) : 0;
        // GLTF material (base asset + per-face override)
        AssetMaterial? gltf = null;
        var renderMaterialId = te.RenderMaterialID;
        if (renderMaterialId == UUID.Zero && renderMaterials != null && face.TeIndex < renderMaterials.Length)
            renderMaterialId = renderMaterials[face.TeIndex];
        if (renderMaterialId != UUID.Zero)
        {
            var baseMat = _world.Materials.Gltf(renderMaterialId, assets, out bool gltfWaiting);
            if (gltfWaiting) { pending++; ctx.MaterialsWaiting = true; }
            if (baseMat == null) { }
            else
            {
                gltf = baseMat;
                if (overrides != null && overrides.TryGetValue(face.TeIndex, out var o))
                {
                    gltf = CloneMaterial(baseMat);
                    gltf.ApplyOverride(o);
                }
            }
        }
        bool legacyWaiting = false;
        var legacy = gltf == null ? _world.Materials.Legacy(te.MaterialID, Source, out legacyWaiting) : null;
        if (legacyWaiting) { pending++; ctx.MaterialsWaiting = true; }

        ctx.TexFactor = gltf != null ? 1f : RepeatFactor(te);   // GLTF textures carry their own transforms
        if (gltf != null)
        {
            var ids = gltf.TextureIds ?? Array.Empty<UUID>();
            var tex = new SceneTexture?[4];
            for (int i = 0; i < 4 && i < ids.Length; i++)
                if (ids[i] != UUID.Zero)
                {
                    // PBR bakes on mesh (updateTEMaterialTextures): a worn face's material, or its override, naming a
                    // bake's magic id shows the wearer's bake; fetched as itself it was LL's placeholder, or nothing
                    tex[i] = Tex(ctx, textures, BakeOnMesh(ids[i], wearer));
                    if (tex[i]!.State == TexState.Loading) pending++;
                }
            if (MediaTexture(ctx, te.TextureID, face.TeIndex) is { } media) tex[0] = media;   // media replaces the base colour
            fc = FaceMaterials.Classify(te, tex[0], null, gltf);
            mat = FaceMaterials.Pbr(te, fc, gltf, tex);
        }
        else
        {
            var diffuse = MediaTexture(ctx, te.TextureID, face.TeIndex) ?? (te.TextureID != UUID.Zero ? Tex(ctx, textures, te.TextureID) : null);
            if (diffuse?.State == TexState.Loading) pending++;
            SceneTexture? normal = null, spec = null;
            if (legacy != null)
            {
                if (legacy.NormalMap != UUID.Zero) { normal = Tex(ctx, textures, legacy.NormalMap); if (normal.State == TexState.Loading) pending++; }
                if (legacy.SpecularMap != UUID.Zero) { spec = Tex(ctx, textures, legacy.SpecularMap); if (spec.State == TexState.Loading) pending++; }
            }
            fc = FaceMaterials.Classify(te, diffuse, legacy, null, rigged: face.Weights.Count > 0);
            SceneTexture? bump = null;
            if (FaceMaterials.LegacyBump(te, fc, legacy) is var code and > 0 && BumpMaps.Source(code, te.TextureID) is { } bumpSource
                && bumpSource != UUID.Zero)
            {
                if (ctx.HoldTextures > 0) LookHold.Hold(textures, bumpSource, ctx.HoldTextures);
                bump = textures.RequestBump(bumpSource, code, assets);
                if (ctx.Deps != null) { ctx.Deps.Add(bump); ctx.DepFactors.Add(1f); }
                if (bump.State == TexState.Loading) pending++;
            }
            mat = FaceMaterials.Legacy(te, fc, diffuse, legacy, normal, spec, bump);
            ApplyAnim(ctx, ref mat, te, face.TeIndex);
        }
        return pending;
    }

    /// <summary>Add a resolved face to the frame's draw list: its material and instance, then its draw in each pass
    /// it's visible in and each shadow map it casts into.</summary>
    static void EmitFace(DrawList dl, ViewSetup v, FaceGeometry face, MeshRange range, GpuInstance inst, in GpuMaterial mat, FaceClass fc, float glow,
        Vector3 objPos, Vector3 axis, bool visible, int casterMask, Vector4 bounds, bool rigged = false, ImpostorCapture? into = null,
        float group = float.NaN)
    {
        if (into != null)
        {
            // an impostor capture is a G-buffer: every face goes in once, blended ones masked at half
            if (fc.Pass is DrawPass.None or DrawPass.WaterExclusion) return;
            var m = mat;
            if ((m.Flags & MaterialFlags.AlphaModeMask) == MaterialFlags.AlphaBlend)
            {
                m.Flags = (m.Flags & ~MaterialFlags.AlphaModeMask) | MaterialFlags.AlphaMask;
                m.AlphaCutoff = 0.5f;
            }
            inst.Material = dl.AddMaterial(m);
            uint ci = dl.AddInstance(inst);
            var cc = new DrawCmd { Range = range, Instance = ci, Bounds = bounds };
            ((m.Flags & MaterialFlags.DoubleSided) != 0 ? into.TwoSided : into.Opaque).Add(cc);
            return;
        }
        inst.Material = dl.AddMaterial(mat);
        inst.Glow = glow;
        uint id = dl.AddInstance(inst);
        EmitDraws(dl, v, face, range, id, inst.Model, fc, glow, (mat.Flags & MaterialFlags.DoubleSided) != 0, objPos, axis, visible, casterMask, bounds, rigged,
            group);
    }

    /// <summary>A face's draws, its instance (and material) already in place: one in each pass it's visible in and each
    /// shadow map it casts into. A blended face sorts by <paramref name="group"/>, its prim's key (<see cref="GroupDepth"/>),
    /// then by its own; by its own alone when that's NaN.</summary>
    static void EmitDraws(DrawList dl, ViewSetup v, FaceGeometry face, MeshRange range, uint id, in Matrix4x4 model, FaceClass fc, float glow,
        bool twoSided, Vector3 objPos, Vector3 axis, bool visible, int casterMask, Vector4 bounds, bool rigged = false, float group = float.NaN)
    {
        float depth, sub = 0, halfHeight = 0;
        if (fc.Pass == DrawPass.Alpha && !rigged)
        {
            // the face's own place; Bounds holds its centre for sorting again from a mirror
            sub = AlphaDepth(face, model, v.Position, v.PlanarSort ? axis : null, out var fcenter);
            depth = float.IsNaN(group) || v.PlanarSort ? sub : group;
            bounds = new Vector4(fcenter, bounds.W);
            // its box's reach up and down (the world box of the local one, as BoxDepth), for the water's split
            var h = face.HalfSize;
            halfHeight = MathF.Abs(model.M13) * h.X + MathF.Abs(model.M23) * h.Y + MathF.Abs(model.M33) * h.Z + 0.01f;
        }
        else depth = Vector3.Dot(objPos - v.Position, axis);
        var cmd = new DrawCmd { Range = range, Instance = id, Depth = depth, SubDepth = sub, Bounds = bounds, HalfHeight = halfHeight };
        if (fc.Pass == DrawPass.WaterExclusion)
        {
            // the water exclusion mask's alone: never lit, glowing or casting (RegionScene.WaterExclusion.cs)
            if (visible) dl.WaterExclusion.Add(cmd);
            return;
        }
        if (visible)
        {
            switch (fc.Pass)
            {
                case DrawPass.Opaque: (twoSided ? dl.OpaqueTwoSided : dl.Opaque).Add(cmd); break;
                case DrawPass.Fullbright: dl.Fullbright.Add(cmd); break;
                case DrawPass.Alpha: (rigged ? dl.RiggedAlpha : dl.Alpha).Add(cmd); break;
            }
            if (glow > 0)
            {
                if (fc.Pass == DrawPass.Alpha) dl.AlphaGlow.Add(cmd);
                else if (fc.Glow) dl.Glow.Add(cmd);
            }
        }
        // shadow casters: everything but water; blended faces cast through the shadow shader's alpha rules
        if (casterMask != 0)
            for (int c = 0; c < dl.ShadowCasters.Length; c++)
                if ((casterMask & (1 << c)) != 0) dl.ShadowCasters[c].Add(cmd);
    }

    /// <summary>A world object's cached faces can keep their instances and materials in the renderer's persistent tables
    /// when drawn in their own region's frame (a neighbour's objects are drawn moved by the regions' offset, which
    /// differs by view: the main view's and a probe's in another region), as themselves (not into an impostor, a HUD or
    /// for the mirror alone).</summary>
    bool Persistable(DrawCtx ctx, SceneObject obj) =>
        ctx.Offset == Vector3.Zero && obj.Owner == 0 && ctx.Capture == null && ctx.InstFlags == 0 && ctx.HudPx == 0 && _world.StaticInstances != null;

    /// <summary>A world object's cached faces by their persistent instances: written when they're first drawn so and when
    /// the object moves, then only their draws each frame (docs/performance.md > Persistent instances and materials).</summary>
    void EmitPersistent(SceneObject obj, FaceCache c, DrawList dl, ViewSetup v, Vector3 pos, Quaternion rot, Vector3 scale, Vector3 axis,
        bool visible, int casterMask, Vector4 bounds, float group)
    {
        var insts = _world.StaticInstances!;
        var mats = _world.StaticMaterials!;
        var faces = c.Faces;
        bool place;
        if (c.InstSlots == null)
        {
            c.InstSlots = new int[faces.Length];
            c.MatSlots = new int[faces.Length];
            for (int i = 0; i < faces.Length; i++)
            {
                c.MatSlots[i] = mats.Alloc();
                mats.Write(c.MatSlots[i], faces[i].Material);
                c.InstSlots[i] = insts.Alloc();
            }
            place = true;
        }
        else place = c.SlotPos != pos || c.SlotRot != rot || c.SlotScale != scale;
        if (place)
        {
            // obj.Inst is this placement's: DrawObject made it for this draw's position (the offset is zero here)
            var inst = obj.Inst;
            inst.Flags = 0;
            for (int i = 0; i < faces.Length; i++)
            {
                inst.Material = (uint)c.MatSlots![i] | PersistentTable<GpuInstance>.Bit;
                inst.Glow = faces[i].Glow;
                insts.Write(c.InstSlots[i], inst);
            }
            (c.SlotPos, c.SlotRot, c.SlotScale) = (pos, rot, scale);
        }
        ref readonly var model = ref obj.Inst.Model;
        for (int i = 0; i < faces.Length; i++)
        {
            var f = faces[i];
            EmitDraws(dl, v, f.Face, f.Face.Range, (uint)c.InstSlots[i] | PersistentTable<GpuInstance>.Bit, model, f.Class, f.Glow,
                (f.Material.Flags & MaterialFlags.DoubleSided) != 0, pos, axis, visible, casterMask, bounds, group: group);
        }
    }

    /// <summary>An object's resolved faces from now on (null: none); the old ones' persistent slots go back.</summary>
    void SetFaces(SceneObject obj, FaceCache? faces)
    {
        if (obj.Faces is { } old && !ReferenceEquals(old, faces) && old.InstSlots is { } inst)
        {
            for (int i = 0; i < inst.Length; i++)
            {
                _world.StaticInstances!.Release(inst[i]);
                _world.StaticMaterials!.Release(old.MatSlots![i]);
            }
            old.InstSlots = old.MatSlots = null;
        }
        obj.Faces = faces;
        obj.Resolving = null;   // a resolve in flight is older than this
    }

    /// <summary>Where a blended face sorts among its prim's faces: LLDrawable::updateDistance's key (its own centre,
    /// pulled toward the camera by a quarter of its world box), but measured from the head's centre along the line to the
    /// face instead of along the view axis, so turning the head never reorders anything (docs/rendering.md > Distances
    /// from the head). Along <paramref name="axis"/> when given, as the viewer does: HUDs, whose pieces stack by their
    /// offset along it, and sort by this alone. Per face within the prim: its faces all sorting at its centre tied, and
    /// the sort's order among ties changed whenever the draw list did (leaves flickering in a forest).</summary>
    static float AlphaDepth(FaceGeometry face, in Matrix4x4 model, Vector3 cam, Vector3? axis, out Vector3 c) =>
        BoxDepth(face.Center, face.HalfSize, model, cam, axis, 0.5f, out c);

    /// <summary>Where a prim's blended faces sort among everything else's, before their own keys order them: the
    /// viewer's alpha pool draws spatial groups far to near (LLSpatialPartition::calcDistance's mDepth: the group's
    /// centre pulled toward the camera by a quarter of its half size) and only the faces within a group by their own
    /// keys. Here each prim is its own group, its whole box measured from the head's centre as faces are. Faces alone
    /// let one big face pass everything near it: a 62 m blended ground's face under the person, pulled 15 m nearer by
    /// its own size, sorted after the mist planes lying on it and covered them (House of Legends, 2026-10-28).
    /// NaN for a prim with no geometry.</summary>
    static float GroupDepth(ShapeGeometry shape, in Matrix4x4 model, Vector3 cam)
    {
        if (!(shape.Min.X <= shape.Max.X)) return float.NaN;
        return BoxDepth((shape.Min + shape.Max) * 0.5f, (shape.Max - shape.Min) * 0.5f, model, cam, null, 0.25f, out _);
    }

    /// <summary>A local box's sort key: its world centre's distance from <paramref name="cam"/> less <paramref name="pull"/>
    /// times its world half size along the line to it (or along <paramref name="axis"/>).</summary>
    static float BoxDepth(Vector3 center, Vector3 h, in Matrix4x4 model, Vector3 cam, Vector3? axis, float pull, out Vector3 c)
    {
        c = Vector3.Transform(center, model);
        // the world box of the local box: |M| times the half size (row vectors)
        var ext = new Vector3(
            MathF.Abs(model.M11) * h.X + MathF.Abs(model.M21) * h.Y + MathF.Abs(model.M31) * h.Z,
            MathF.Abs(model.M12) * h.X + MathF.Abs(model.M22) * h.Y + MathF.Abs(model.M32) * h.Z,
            MathF.Abs(model.M13) * h.X + MathF.Abs(model.M23) * h.Y + MathF.Abs(model.M33) * h.Z);
        if (axis is { } at) return Vector3.Dot(c - cam - ext * pull * at, at);
        var d = c - cam;
        float r = d.Length();
        if (r < 1e-4f) return 0;
        var u = d / r;
        return r - Vector3.Dot(ext * pull, u * u);   // a face's (max - min) * 0.25 = half size * 0.5; a group's 0.25 of its half size
    }

    static AssetMaterial CloneMaterial(AssetMaterial m) => new()
    {
        TextureIds = (UUID[])(m.TextureIds ?? new UUID[AssetMaterial.TEXTURE_COUNT]).Clone(),
        TextureTransforms = (GltfTextureTransform[])(m.TextureTransforms ?? new GltfTextureTransform[AssetMaterial.TEXTURE_COUNT]).Clone(),
        BaseColorFactor = m.BaseColorFactor,
        EmissiveFactor = m.EmissiveFactor,
        MetallicFactor = m.MetallicFactor,
        RoughnessFactor = m.RoughnessFactor,
        AlphaCutoff = m.AlphaCutoff,
        AlphaMode = m.AlphaMode,
        DoubleSided = m.DoubleSided,
    };

    /// <summary>Adds the light (and its projector, for a spot light with a texture); returns 1 if the projector
    /// texture is still loading.</summary>
    int AddLight(DrawList dl, Primitive prim, Primitive.LightData light, Vector3 pos, Quaternion rot, Vector3 scale, IAssetSource assets,
        (ulong Region, uint LocalId)[] shadowProjectors)
    {
        // LLVOVolume::getLightLinearColor: colour times intensity. The wire bytes are already linear
        // (LLLightParams::unpack -> setLinearColor; the build floater converts its sRGB swatch before sending)
        var c = light.Color;
        var lin = new Vector3(c.R, c.G, c.B) * light.Intensity;
        if (lin.LengthSquared() < 0.001f) return 0;
        float adjusted = light.Radius * 1.5f;
        bool spot = prim.LightMap is { } lm && lm.LightTexture != UUID.Zero;
        // forward (alpha) light terms as LLPipeline::setupHWLights hands them to the GL light state
        float linatten = 3f * (1f + light.Falloff * 2f) / adjusted;
        float quadratic = light.Falloff * 0.5f + 1f;
        var dir = Vector3.Transform(-Vector3.UnitZ, rot);
        dl.Lights.Add(new GpuLight
        {
            PositionRadius = new Vector4(pos, adjusted),
            ColorFalloff = new Vector4(lin, light.Falloff * 0.5f),
            DirectionCutoff = new Vector4(dir, spot ? light.Cutoff : -2f),
            Params = new Vector4(linatten, quadratic, spot ? 0f : 1f, 0f),
        });
        if (!spot) return 0;

        int pending = 0, slot = -1;
        float lod = 0f;
        var tex = _world.Textures.Request(prim.LightMap!.LightTexture, assets);
        if (tex.State == TexState.Loading) pending = 1;
        if (tex.State == TexState.Ready) { slot = tex.Slot; lod = MathF.Log2(Math.Max(tex.Width, 1)); }
        var proj = Projector(pos, rot, scale, V(prim.LightMap.Params), adjusted, lin, light.Falloff * 0.5f, slot, lod);
        for (int i = 0; i < 2; i++)
            if (shadowProjectors[i] == (Source.Handle, prim.LocalID))
            {
                proj.ShadowIndex = i;
                proj.ShadowFade = 0f;   // 1 - mSpotLightFade, faded in
            }
        dl.Projectors.Add(proj);
        return pending;
    }

    /// <summary>A projector the view could give a shadow map to (setupSpotLight's candidates: projectors deferred
    /// lighting draws).</summary>
    public readonly record struct ProjectorCandidate(ulong Region, uint LocalId, Vector3 Position, Quaternion Rotation,
        Vector3 Scale, float Fov, float Radius);

    /// <summary>The projectors this view lights with (same tests as <see cref="BuildDrawList"/>).</summary>
    public void CollectProjectors(ViewSetup v, Vector3 offset, Frustum frustum, List<ProjectorCandidate> into)
    {
        foreach (var obj in _lights.Concat(_attachments.Values.SelectMany(l => l)))
        {
            if (obj.Skip) continue;
            var prim = obj.Prim;
            if (prim.Light is not { } light || light.Intensity <= 0 || light.Radius <= 0) continue;
            if (prim.LightMap is not { } lm || lm.LightTexture == UUID.Zero) continue;
            if (!Transform(obj, out var pos, out var rot)) continue;
            var worldPos = pos + offset;
            if (Vector3.Distance(worldPos, v.Position) - light.Radius >= v.DrawDistance) continue;
            if (!frustum.Sphere(worldPos, light.Radius * 1.5f)) continue;
            var c = light.Color;
            if ((new Vector3(c.R, c.G, c.B) * light.Intensity).LengthSquared() < 0.001f) continue;
            into.Add(new ProjectorCandidate(Source.Handle, prim.LocalID, worldPos, rot, V(prim.Scale), lm.Params.X, light.Radius));
        }
    }

    /// <summary>LLPipeline::setupSpotLight in region space: the projector frustum's apex sits behind the prim's -Z
    /// face so that face spans the field of view; it reaches the light's range past the prim.</summary>
    internal static GpuProjector Projector(Vector3 pos, Quaternion rot, Vector3 scale, Vector3 spotParams, float size,
        Vector3 color, float falloff, int texture, float lod)
    {
        float fov = spotParams.X, focus = spotParams.Y, ambiance = spotParams.Z;
        var at = Vector3.Transform(new Vector3(0, 0, -scale.Z * 0.5f), rot);
        var np = pos + at;
        at = Vector3.Normalize(at);
        float dist = scale.Y * 0.5f / MathF.Tan(fov * 0.5f);
        var origin = np - at * dist;

        var lightToRegion = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(origin);
        Matrix4x4.Invert(lightToRegion, out var regionToLight);
        float near = dist, far = size + dist - scale.Z;
        float aspect = scale.X / scale.Y;
        // glm::perspective (GL clip, z in [-w, w]) and the [0,1] bias, transposed for row vectors
        float f = 1f / MathF.Tan(fov * 0.5f);
        var proj = new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, 2 * far * near / (near - far), 0);
        var bias = new Matrix4x4(
            0.5f, 0, 0, 0,
            0, 0.5f, 0, 0,
            0, 0, 0.5f, 0,
            0.5f, 0.5f, 0.5f, 1);
        var p1 = Vector3.Transform(new Vector3(0, 0, -(near + 0.01f)), lightToRegion);
        var p2 = Vector3.Transform(new Vector3(0, 0, -(near + 1f)), lightToRegion);
        return new GpuProjector
        {
            RegionToTc = regionToLight * proj * bias,
            CenterSize = new Vector4(pos, size),
            ColorFalloff = new Vector4(color, falloff),
            OriginRange = new Vector4(origin, far - near),
            NormalFocus = new Vector4(Vector3.Normalize(p2 - p1), focus),
            PlaneAmbiance = new Vector4(p1, ambiance),
            Texture = texture,
            Lod = lod,
            ShadowIndex = -1,
            ShadowFade = 1f,
        };
    }

}

public struct RegionDrawStats
{
    public int Objects, Culled, Pending;
}

public sealed record ViewOptions
{
    // Defaults are the reference viewer's: the stock High-Ultra preset (featuretable.txt "HighUltra", graphics
    // quality 5), the level the viewer picks for this machine and the one the refviewer runs.

    /// <summary>RenderVolumeLODFactor (High-Ultra: 1.75).</summary>
    public float LodFactor { get; init; } = VolumeLod.LodFactorHighUltra;

    /// <summary>RenderTreeLODFactor (0.5 on the stock LowMid to High-Ultra presets, 1.0 on Ultra, 0 on Low: no trees or grass).</summary>
    public float TreeLodFactor { get; init; } = 0.5f;

    /// <summary>RenderAvatarLODFactor (featuretable 1.0 from Mid up; the reference profile sets 1).</summary>
    public float AvatarLodFactor { get; init; } = 1.0f;

    /// <summary>Objects whose bounding sphere is smaller on screen than this radius in pixels aren't drawn, and below
    /// <see cref="MinShadowPixels"/> they cast no shadow; 0 draws everything, as the viewer does. Set by the renderer's
    /// scene budget (Realtime/SceneBudget.cs) when the scene's CPU time runs over, so far small things go first.</summary>
    public float MinPixels { get; init; }
    public float MinShadowPixels { get; init; }
}

/// <summary>Six-plane frustum from a view-projection matrix (row-vector convention, Vulkan depth, reverse-Z infinite).</summary>
public readonly struct Frustum
{
    readonly Vector4[] _planes;
    // more volumes, any of which counts as in view (a mirror's reflection, the handheld camera's preview, beside the
    // eyes' view)
    readonly Vector4[][]? _or;

    /// <summary>Accepts everything (probe captures look in all directions).</summary>
    public static Frustum All => new(Array.Empty<Vector4>());
    Frustum(Vector4[] planes, Vector4[][]? or = null) => (_planes, _or) = (planes, or);

    /// <summary>A convex volume from inward planes (xyz normal, w: dot(n, p) + w >= 0 inside).</summary>
    public static Frustum FromPlanes(Vector4[] planes) => new(planes);

    /// <summary>What's in either of two volumes (each with its own others).</summary>
    public static Frustum Either(Frustum a, Frustum b) => new(a._planes, [.. a._or ?? [], b._planes, .. b._or ?? []]);

    public Frustum(Matrix4x4 viewProj)
    {
        var m = viewProj;
        // clip = p * M; columns of M give clip components
        var c0 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c1 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c2 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c3 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        _planes = new[]
        {
            c3 + c0, c3 - c0,   // left, right
            c3 + c1, c3 - c1,   // y
            c3 - c2,            // near (reverse-Z: z <= w)
        };
        for (int i = 0; i < _planes.Length; i++)
        {
            var p = _planes[i];
            float len = new Vector3(p.X, p.Y, p.Z).Length();
            _planes[i] = p / len;
        }
    }

    /// <summary>What either of two side-by-side eyes sees (eyes offset along their shared x axis, same orientation):
    /// the left eye's left plane, the right eye's right plane, and their shared top, bottom and near planes.</summary>
    public static Frustum Union(Frustum left, Frustum right)
    {
        var p = (Vector4[])left._planes.Clone();
        p[1] = right._planes[1];
        return new Frustum(p, left._or);
    }

    public bool Sphere(Vector3 c, float r)
    {
        if (Touches(_planes, c, r)) return true;
        if (_or != null) foreach (var o in _or) if (Touches(o, c, r)) return true;
        return false;
    }

    /// <summary>The whole sphere is inside (one of the volumes).</summary>
    public bool Contains(Vector3 c, float r)
    {
        if (Holds(_planes, c, r)) return true;
        if (_or != null) foreach (var o in _or) if (Holds(o, c, r)) return true;
        return false;
    }

    static bool Touches(Vector4[] planes, Vector3 c, float r)
    {
        foreach (var p in planes)
            if (p.X * c.X + p.Y * c.Y + p.Z * c.Z + p.W < -r) return false;
        return true;
    }

    static bool Holds(Vector4[] planes, Vector3 c, float r)
    {
        foreach (var p in planes)
            if (p.X * c.X + p.Y * c.Y + p.Z * c.Z + p.W < r) return false;
        return true;
    }
}
