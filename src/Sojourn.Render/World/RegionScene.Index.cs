using LibreMetaverse;
using Sojourn.Render.Geometry;
using LVector3 = LibreMetaverse.Vector3;
using LQuaternion = LibreMetaverse.Quaternion;

namespace Sojourn.Render.World;

/// <summary>A 16 m column of the region's objects (or the list of big ones): who's in it, and two boxes over them, what
/// the shadow fit takes (their extents) and what culling takes (the spheres each one is culled with).</summary>
internal sealed class SceneCell
{
    public readonly List<SceneObject> Objects = new();
    public Vector3 BoxMin, BoxMax, ReachMin, ReachMax;
    public bool Stale;
    long _keptAt;

    /// <summary>Culled whole while within draw distance (out of view): its objects' textures count as near
    /// (FaceCache.KeepNear), every 10 s at most. Render thread (a view's plan).</summary>
    public void KeepNear()
    {
        long now = System.Environment.TickCount64;
        if (now - _keptAt < 10_000) return;
        _keptAt = now;
        foreach (var o in Objects) o.Faces?.KeepNear(now);
    }

    public void Grow(SceneObject o)
    {
        if (Objects.Count == 1) { BoxMin = o.BoxMin; BoxMax = o.BoxMax; ReachMin = o.CullCenter - new Vector3(o.CullRadius); ReachMax = o.CullCenter + new Vector3(o.CullRadius); return; }
        BoxMin = Vector3.Min(BoxMin, o.BoxMin);
        BoxMax = Vector3.Max(BoxMax, o.BoxMax);
        ReachMin = Vector3.Min(ReachMin, o.CullCenter - new Vector3(o.CullRadius));
        ReachMax = Vector3.Max(ReachMax, o.CullCenter + new Vector3(o.CullRadius));
    }

    public void Refit()
    {
        Stale = false;
        var objs = Objects;
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            var rmin = o.CullCenter - new Vector3(o.CullRadius);
            var rmax = o.CullCenter + new Vector3(o.CullRadius);
            if (i == 0) { BoxMin = o.BoxMin; BoxMax = o.BoxMax; ReachMin = rmin; ReachMax = rmax; continue; }
            BoxMin = Vector3.Min(BoxMin, o.BoxMin);
            BoxMax = Vector3.Max(BoxMax, o.BoxMax);
            ReachMin = Vector3.Min(ReachMin, rmin);
            ReachMax = Vector3.Max(ReachMax, rmax);
        }
    }
}

/// <summary>
/// The object index (persistent scene state, render thread). Every object's world placement (position, rotation,
/// extents, the sphere it's culled with) is worked out when the region says it changed, not every frame: the region
/// source queues the local ids of objects added, updated, moved or removed (RegionSource.Changes), and the scene
/// applies them at the start of a frame, re-placing a linkset's children with their root. World objects sit in a grid
/// of 16 m columns (big ones in a list of their own), so a view culls columns before objects, the shadow fit takes a
/// column's extents at once, and lights and projectors are lists of their own. Attachments follow their wearer's pose
/// every frame, listed by wearer. As a safety net a few objects a frame are checked against the region's dictionary
/// (all of them every few seconds), and objects whose parent isn't known yet are retried twice a second. Without a
/// change queue (a bare GridClient source) every frame rescans everything, as before.
/// </summary>
public sealed partial class RegionScene
{
    public const float CellSize = 16f;
    /// <summary>Objects reaching further than this go in the big list rather than a column.</summary>
    const float BigRadius = 24f;

    SceneCell[] _cells = Array.Empty<SceneCell>();
    int _gridW, _gridH;
    readonly SceneCell _big = new();
    readonly List<SceneObject> _all = new();
    readonly Dictionary<uint, List<SceneObject>> _children = new();
    readonly List<SceneObject> _unplaced = new();
    readonly HashSet<SceneObject> _lights = new();
    readonly List<(Vector3 Min, Vector3 Max)> _probeChanges = new();
    bool _probeDirty = true;
    bool _indexed;
    /// <summary>A frame has drawn it (synced it) at least once: its objects are indexed.</summary>
    public bool Synced => _frame > 0;
    int _roundRobin;
    long _nextUnplaced, _nextReconcile;

    /// <summary>Changes applied per frame at most (a region arriving queues thousands; the rest wait a frame), and the
    /// time they may take (a few thousand new objects took 5-10 ms on arrival: a batch a frame instead).</summary>
    const int MaxChangesPerFrame = 6000;
    static readonly long MaxChangeTicks = (long)(0.0015 * System.Diagnostics.Stopwatch.Frequency);

    /// <param name="terrain">its land rebuilt too if new patches came (false: a tick between frames, the land left for
    /// the next frame that draws the region)</param>
    public void Sync(bool terrain = true)
    {
        _frame++;
        EnsureGrid();
        var queue = Source.Changes;
        long now = System.Environment.TickCount64;
        if (queue == null || !_indexed)
        {
            if (queue != null) queue.Clear();   // everything below is current as of now
            FullSync();
            _indexed = queue != null;
            Effects.Incremental = _indexed;
            _nextReconcile = now + 10_000;
        }
        else
        {
            int n = 0;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            while (n < MaxChangesPerFrame && queue.TryDequeue(out var change))
            {
                n++;
                if ((n & 63) == 0 && System.Diagnostics.Stopwatch.GetTimestamp() - start > MaxChangeTicks) n = MaxChangesPerFrame;
                if (change.Kind == RegionSource.ChangeRemoved) { if (_objects.TryGetValue(change.LocalId, out var gone)) Remove(gone); }
                else Touch(change.LocalId);
            }
            if (n > 0) _world.Motion.Heard(Source.Handle);
            Verify(now);
            if (now >= _nextUnplaced && _unplaced.Count > 0)
            {
                _nextUnplaced = now + 500;
                foreach (var o in _unplaced.ToArray()) Place(o);
            }
            // a missed add or kill shows as a count that disagrees once the queue is empty: rescan then
            if (now >= _nextReconcile && queue.IsEmpty)
            {
                _nextReconcile = now + 10_000;
                if (Source.Prims.Count != _objects.Count) FullSync();
            }
        }
        MoveMoving();
        if (terrain) Terrain.Sync();
    }

    void EnsureGrid()
    {
        if (_cells.Length > 0 || !Source.HasSimulator) return;
        var sim = Source.Simulator;
        _gridW = Math.Max(1, (int)MathF.Ceiling((sim.SizeX == 0 ? 256 : sim.SizeX) / CellSize));
        _gridH = Math.Max(1, (int)MathF.Ceiling((sim.SizeY == 0 ? 256 : sim.SizeY) / CellSize));
        _cells = new SceneCell[_gridW * _gridH];
        for (int i = 0; i < _cells.Length; i++) _cells[i] = new SceneCell();
        // anything placed before the grid existed goes in now
        foreach (var o in _all) if (o.Placed && !o.Skip) Reindex(o);
    }

    /// <summary>Every object against the region's dictionary: new ones added, changed ones refreshed, gone ones removed.</summary>
    void FullSync()
    {
        foreach (var (_, prim) in Source.Prims)
        {
            if (!_objects.TryGetValue(prim.LocalID, out var obj)) Add(prim);
            else if (Changed(obj, prim)) { Refresh(obj, prim); Place(obj); }
            else if (Moved(obj, prim)) Place(obj);
            if (_objects.TryGetValue(prim.LocalID, out obj)) obj.SeenFrame = _frame;
        }
        if (_objects.Count > Source.Prims.Count || _objects.Values.Any(o => o.SeenFrame != _frame))
            foreach (var gone in _objects.Values.Where(o => o.SeenFrame != _frame).ToList()) Remove(gone);
    }

    static bool Changed(SceneObject obj, Primitive prim) =>
        !ReferenceEquals(obj.Prim, prim) || !ReferenceEquals(obj.PrimDataRef, prim.PrimData) || !ReferenceEquals(obj.SculptRef, prim.Sculpt) ||
        !ReferenceEquals(obj.TexturesRef, prim.Textures) || obj.ScaleSeen != prim.Scale;

    static bool Moved(SceneObject obj, Primitive prim) =>
        prim.ParentID != obj.ParentSeen || prim.Position != obj.PosSeen || prim.Rotation != obj.RotSeen;

    void Touch(uint localId)
    {
        if (!Source.Prims.TryGetValue(localId, out var prim))
        {
            if (_objects.TryGetValue(localId, out var gone)) Remove(gone);
            return;
        }
        if (!_objects.TryGetValue(localId, out var obj)) { Add(prim); return; }
        if (Changed(obj, prim)) Refresh(obj, prim);
        Place(obj);
    }

    void Add(Primitive prim)
    {
        var obj = new SceneObject { LocalId = prim.LocalID, Prim = prim, SeenFrame = _frame, AllSlot = _all.Count };
        _objects[prim.LocalID] = obj;
        _all.Add(obj);
        Refresh(obj, prim);
        Place(obj);
    }

    void Remove(SceneObject obj)
    {
        ReleaseGrass(obj);
        SetFaces(obj, null);
        if (_indexed) Effects.Forget(obj.LocalId, Particles.EffectClock.Now);
        if (obj.Placed) NoteProbeChange(obj.BoxMin, obj.BoxMax);
        Unindex(obj);
        SetOwner(obj, 0);
        _lights.Remove(obj);
        _animesh.Remove(obj);
        if (obj.Unplaced) { _unplaced.Remove(obj); obj.Unplaced = false; }
        Unparent(obj);
        if (obj.AllSlot >= 0)
        {
            var last = _all[^1];
            _all[obj.AllSlot] = last;
            last.AllSlot = obj.AllSlot;
            _all.RemoveAt(_all.Count - 1);
            obj.AllSlot = -1;
        }
        _objects.Remove(obj.LocalId);
        obj.Placed = false;
        // its children have nothing to hang from now: they wait for a parent again
        if (_children.TryGetValue(obj.LocalId, out var kids))
            foreach (var k in kids.ToArray()) Place(k);
    }

    /// <summary>A few objects a frame (all of them every ~4 s) against the dictionary, for changes no event told of.</summary>
    void Verify(long now)
    {
        int count = _all.Count;
        if (count == 0) return;
        int k = Math.Min(count, Math.Max(64, count / 240));
        for (int i = 0; i < k && _all.Count > 0; i++)
        {
            _roundRobin = (_roundRobin + 1) % _all.Count;
            var obj = _all[_roundRobin];
            if (!Source.Prims.TryGetValue(obj.LocalId, out var prim)) { Remove(obj); continue; }
            if (Changed(obj, prim)) { Refresh(obj, prim); Place(obj); }
            else if (Moved(obj, prim)) Place(obj);
        }
    }

    void Unparent(SceneObject obj)
    {
        if (obj.ParentSeen == 0 || !_children.TryGetValue(obj.ParentSeen, out var siblings)) return;
        siblings.Remove(obj);
        if (siblings.Count == 0) _children.Remove(obj.ParentSeen);
    }

    void SetOwner(SceneObject obj, uint owner)
    {
        if (obj.Owner == owner) return;
        if (obj.Owner != 0 && _attachments.TryGetValue(obj.Owner, out var was))
        {
            was.Remove(obj);
            if (was.Count == 0) _attachments.Remove(obj.Owner);
        }
        obj.Owner = owner;
        if (owner != 0)
        {
            if (!_attachments.TryGetValue(owner, out var list)) _attachments[owner] = list = new List<SceneObject>();
            list.Add(obj);
        }
    }

    /// <summary>Work out where an object is (it, or its parent, changed), file it in the index, and do the same for
    /// its children.</summary>
    void Place(SceneObject obj)
    {
        var prim = obj.Prim;
        if (prim.ParentID != obj.ParentSeen || !obj.Parented)
        {
            Unparent(obj);
            obj.ParentSeen = prim.ParentID;
            obj.Parented = true;
            if (prim.ParentID != 0)
            {
                if (!_children.TryGetValue(prim.ParentID, out var kids)) _children[prim.ParentID] = kids = new List<SceneObject>();
                kids.Add(obj);
            }
        }
        obj.PosSeen = prim.Position;
        obj.RotSeen = prim.Rotation;
        if (obj.Placed) NoteProbeChange(obj.BoxMin, obj.BoxMax);
        Effects.ObserveOne(Source.Handle, prim, Particles.EffectClock.Now);

        uint owner = AttachmentOwner(prim);
        SetOwner(obj, owner);
        obj.Animesh = AnimeshRoot(prim);
        if (obj.Animesh == obj.LocalId) _animesh.Add(obj); else _animesh.Remove(obj);
        var pos = Vector3.Zero;
        var rot = Quaternion.Identity;
        bool placed = owner == 0 && WorldTransformFresh(prim, out pos, out rot);
        bool unplaced = owner == 0 && !placed;
        if (unplaced != obj.Unplaced)
        {
            if (unplaced) _unplaced.Add(obj); else _unplaced.Remove(obj);
            obj.Unplaced = unplaced;
        }
        if (!placed)
        {
            obj.Placed = false;
            Unindex(obj);
            _lights.Remove(obj);
        }
        else
        {
            obj.WPos = pos;
            obj.WRot = rot;
            Bounds(obj);
            obj.Placed = true;
            if (obj.Skip) Unindex(obj); else Reindex(obj);
            if (!obj.Skip && prim.Light is { Intensity: > 0, Radius: > 0 }) _lights.Add(obj); else _lights.Remove(obj);
            NoteProbeChange(obj.BoxMin, obj.BoxMax);
            NoteMotion(obj);
        }
        if (_children.TryGetValue(obj.LocalId, out var children))
            foreach (var c in children.ToArray()) Place(c);
    }

    /// <summary>Extents (the shadow fit's box) and the cull sphere, as the draw list tests them; the probe scan's volume.</summary>
    void Bounds(SceneObject obj)
    {
        var prim = obj.Prim;
        var pos = obj.WPos;
        var rot = obj.WRot;
        obj.Probe = null;
        obj.LodRadius = -1;   // worked out again at its next draw (the shape or scale may have changed)
        if (obj.Kind is ShapeKind.Tree or ShapeKind.Grass)
        {
            var (fmin, fmax) = FoliageExtents(obj, pos, rot);
            obj.BoxMin = fmin;
            obj.BoxMax = fmax;
            obj.CullCenter = (fmin + fmax) * 0.5f;
            obj.CullRadius = Vector3.Distance(fmin, fmax) * 0.5f;
            return;
        }
        var scale = V(prim.Scale);
        float r = 0.5f * scale.Length();
        obj.BoxMin = pos - new Vector3(r);
        obj.BoxMax = pos + new Vector3(r);
        obj.CullCenter = pos;
        obj.CullRadius = r;
        if (obj.Skip) return;
        // the probe scan's volume (LLVOVolume::getBinRadius for static prims: max(drawable radius, 3))
        var half = 0.5f * scale;
        var m = Matrix4x4.CreateFromQuaternion(rot);
        var ext = new Vector3(
            MathF.Abs(m.M11) * half.X + MathF.Abs(m.M21) * half.Y + MathF.Abs(m.M31) * half.Z,
            MathF.Abs(m.M12) * half.X + MathF.Abs(m.M22) * half.Y + MathF.Abs(m.M32) * half.Z,
            MathF.Abs(m.M13) * half.X + MathF.Abs(m.M23) * half.Y + MathF.Abs(m.M33) * half.Z);
        float binRadius = Math.Clamp(MathF.Max(r, 3f), 0.5f, 256f);
        var model = Matrix4x4.CreateScale(scale) * m * Matrix4x4.CreateTranslation(pos);
        Matrix4x4.Invert(model, out var toUnit);
        obj.Probe = new ProbeVolume(pos, pos - ext, pos + ext, binRadius, toUnit, Pickable(prim));
    }

    void NoteProbeChange(Vector3 min, Vector3 max)
    {
        _probeChanges.Add((min, max));
        _probeDirty = true;
        ProbeVersion++;
    }

    /// <summary>Moves whenever an object the probe scan sees changes (so an unchanged region needs no scan).</summary>
    public long ProbeVersion { get; private set; }

    SceneCell CellFor(SceneObject obj)
    {
        if (obj.CullRadius > BigRadius || _cells.Length == 0) return _big;
        int x = Math.Clamp((int)MathF.Floor(obj.WPos.X / CellSize), 0, _gridW - 1);
        int y = Math.Clamp((int)MathF.Floor(obj.WPos.Y / CellSize), 0, _gridH - 1);
        return _cells[y * _gridW + x];
    }

    void Reindex(SceneObject obj)
    {
        var cell = CellFor(obj);
        if (ReferenceEquals(obj.Cell, cell))
        {
            cell.Stale = true;   // it moved within the column: the boxes may shrink
            return;
        }
        Unindex(obj);
        obj.Cell = cell;
        obj.CellSlot = cell.Objects.Count;
        cell.Objects.Add(obj);
        if (!cell.Stale) cell.Grow(obj);
    }

    void Unindex(SceneObject obj)
    {
        if (obj.Cell is not { } cell) return;
        var list = cell.Objects;
        var last = list[^1];
        list[obj.CellSlot] = last;
        last.CellSlot = obj.CellSlot;
        list.RemoveAt(list.Count - 1);
        cell.Stale = true;
        obj.Cell = null;
        obj.CellSlot = -1;
    }

    readonly HashSet<SceneObject> _animesh = new();

    /// <summary>The animated object a prim belongs to (LLVOVolume::isAnimatedObject: its linkset root's Animated Mesh
    /// flag), as the root's local id, or 0. A worn one's root is the attached prim.</summary>
    uint AnimeshRoot(Primitive prim)
    {
        var root = prim;
        if (prim.ParentID != 0 && !Source.Avatars.ContainsKey(prim.ParentID))
            root = _objects.TryGetValue(prim.ParentID, out var r) ? r.Prim : null;
        return root is { IsAnimatedObject: true, PrimData.PCode: PCode.Prim } ? root.LocalID : 0;
    }

    /// <summary>The roots of this region's animated objects (in the world or worn), each with the avatar it's worn by
    /// (local id) or 0.</summary>
    public IEnumerable<(SceneObject Root, uint Wearer)> AnimeshRoots()
    {
        foreach (var o in _animesh) yield return (o, o.Owner);
    }

    /// <summary>The rigged mesh shapes of an animated object's linkset that are loaded (any LOD), with their mesh ids:
    /// what its skeleton takes joint positions from, as an avatar's does from what it wears.</summary>
    public IEnumerable<(UUID Mesh, ShapeGeometry Shape)> AnimeshShapesOf(SceneObject root)
    {
        IEnumerable<SceneObject> parts = _children.TryGetValue(root.LocalId, out var kids) ? kids.Prepend(root) : new[] { root };
        foreach (var obj in parts)
        {
            if (obj.Kind != ShapeKind.Mesh || obj.Animesh != root.LocalId) continue;
            foreach (var l in obj.Lods)
                if (l is { Ready: true, Evicted: false, Skin: not null } && l.Key.Kind == ShapeKind.RiggedMesh) { yield return (obj.Asset, l); break; }
        }
    }

    /// <summary>The ids of an animated object's prims (root first), whose signalled animations it plays together.</summary>
    public IEnumerable<UUID> AnimeshPartIds(SceneObject root)
    {
        yield return root.Prim.ID;
        if (_children.TryGetValue(root.LocalId, out var kids))
            foreach (var k in kids) if (k.Animesh == root.LocalId) yield return k.Prim.ID;
    }

    /// <summary>A world object's placement from its prim and its parent's (attachments aside), fresh.</summary>
    bool WorldTransformFresh(Primitive prim, out Vector3 pos, out Quaternion rot)
    {
        // (as moved between the region's updates: RegionScene.Motion.cs)
        (pos, rot) = Local(prim);
        if (prim.ParentID == 0) return true;
        if (!_objects.TryGetValue(prim.ParentID, out var parent) || parent.Prim.ParentID != 0) return false;
        var (ppos, prot) = Local(parent.Prim);
        pos = ppos + Vector3.Transform(pos, prot);
        rot = Quaternion.Normalize(Quaternion.Concatenate(rot, prot));
        return true;
    }

    /// <summary>Where an object is this frame: world objects from the index, attachments from their wearer's pose (as
    /// <paramref name="attachmentFrame"/> gives it; by default the main view's, <see cref="Hooks"/>).</summary>
    bool Transform(SceneObject obj, out Vector3 pos, out Quaternion rot, Func<uint, int, (Vector3 Pos, Quaternion Rot)?>? attachmentFrame = null)
    {
        if (obj.Owner == 0)
        {
            pos = obj.WPos;
            rot = obj.WRot;
            return obj.Placed;
        }
        return AttachmentTransform(obj.Prim, attachmentFrame ?? Hooks.AttachmentFrame, out pos, out rot);
    }

    bool AttachmentTransform(Primitive prim, Func<uint, int, (Vector3 Pos, Quaternion Rot)?>? attachmentFrame, out Vector3 pos, out Quaternion rot)
    {
        if (prim.ParentID == 0 || !_objects.TryGetValue(prim.ParentID, out var root))
            return AttachmentRoot(prim, attachmentFrame, out pos, out rot);
        if (!AttachmentRoot(root.Prim, attachmentFrame, out var rpos, out var rrot)) { pos = default; rot = Quaternion.Identity; return false; }
        pos = rpos + Vector3.Transform(V(prim.Position), rrot);
        rot = Quaternion.Normalize(Quaternion.Concatenate(Local(prim).Rot, rrot));   // (spinning: llTargetOmega)
        return true;
    }

    /// <summary>The probe scan's view of the region: every volume prim's box (cached with its placement), the manual
    /// probes among them, and the boxes that changed since the last scan.</summary>
    public void ScanForProbes(List<ProbeVolume> volumes, List<ManualProbeObject> manual, Action<Vector3, Vector3> changed,
        Func<uint, ReflectionProbeParams?>? probeParams)
    {
        foreach (var (min, max) in _probeChanges) changed(min, max);
        _probeChanges.Clear();
        if (_probeDirty || _probeVolumes == null)
        {
            _probeDirty = false;
            _probeVolumes ??= new List<(ProbeVolume, SceneObject)>();
            _probeVolumes.Clear();
            foreach (var obj in _all)
                if (obj.Placed && obj.Probe is { } pv) _probeVolumes.Add((pv, obj));
        }
        _mirrors.Clear();
        foreach (var (pv, obj) in _probeVolumes)
        {
            volumes.Add(pv);
            if (probeParams?.Invoke(obj.LocalId) is not { } pp) continue;
            // mirrors are the hero probe's (LLVOVolume::updateReflectionProbePtr), not reflection probes
            if ((pp.Flags & ReflectionProbeParams.FlagMirror) == 0) manual.Add(new ManualProbeObject(obj.LocalId, obj.WPos, obj.WRot, V(obj.Prim.Scale), pp));
            else _mirrors.Add((obj, pp));
        }
    }

    readonly List<(SceneObject Obj, ReflectionProbeParams Params)> _mirrors = new();

    /// <summary>The region's mirrors as the last probe scan found them, where they are now (placed ones only).</summary>
    public IEnumerable<ManualProbeObject> Mirrors()
    {
        foreach (var (obj, pp) in _mirrors)
            if (obj.Placed && !obj.Skip) yield return new ManualProbeObject(obj.LocalId, obj.WPos, obj.WRot, V(obj.Prim.Scale), pp);
    }

    List<(ProbeVolume, SceneObject)>? _probeVolumes;

    /// <summary>Columns (and the big list) a ray could touch before <paramref name="maxT"/>.</summary>
    List<SceneObject> RayCandidates(Vector3 origin, Vector3 dir, float maxT, List<SceneObject> found)
    {
        // into the caller's list, kept for its next ray (the pointer casts one or two a frame): the caller walks it
        // before anything can change the index
        found.Clear();
        for (int i = 0; i <= _cells.Length; i++)
        {
            var cell = i < _cells.Length ? _cells[i] : _big;
            if (cell.Objects.Count == 0) continue;
            if (cell.Stale) cell.Refit();
            if (!Slab(origin, dir, cell.ReachMin, cell.ReachMax, maxT, out _)) continue;
            found.AddRange(cell.Objects);
        }
        foreach (var list in _attachments.Values) found.AddRange(list);
        return found;
    }
}
