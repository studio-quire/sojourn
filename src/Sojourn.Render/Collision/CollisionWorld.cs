using System.Collections.Concurrent;
using LibreMetaverse;
using Sojourn.Core.World;
using Sojourn.Render.Integration;
using Sojourn.Render.World;
using LQuaternion = LibreMetaverse.Quaternion;
using LVector3 = LibreMetaverse.Vector3;

namespace Sojourn.Render.Collision;

/// <summary>
/// The local collision model without a GPU (docs/access-app.md > Walk mode > The collision model): what's solid around
/// the person, from the session's world model and the same prim volumes, sculpt maps and mesh physics the renderer's
/// model uses (RegionScene.CollideRay), by the same rule (<see cref="CollisionRule"/>), so the two answer alike. For an
/// app that may have no usable GPU (Sojourn Access on an old card or a VM); the VR client keeps the renderer's.
///
/// Every region the session's world knows is indexed: each prim's place in the region (a linkset's children with their
/// root) as the region reports changes (RegionWorld.Changed), in 16 m columns, with a few hundred checked against the
/// world model each query as a safety net. Shapes come from <see cref="CollisionShapes"/>: unlike the renderer's model,
/// which counts only what it has drawn, this one knows every prim in the region once its shape is had (prims at once;
/// sculpts and meshes once downloaded, nearest the person first: <see cref="Around"/>). Prims count by their physics
/// shape type (the caller's <c>solid</c>), front faces only; trees, grass, attachments and HUDs don't count; standing
/// avatars are boxes (<see cref="CollideMath.Avatar"/>). Positions are the region's last word (no smoothing between
/// updates). Any thread; one query at a time. Claude Opus 5.5, 2026-10-30.
/// </summary>
public sealed class CollisionWorld : IDisposable
{
    readonly WorldHub _hub;
    readonly CollisionShapes _shapes;
    readonly Dictionary<ulong, Region> _regions = new();
    readonly object _gate = new();

    public CollisionWorld(WorldHub hub, Action<string> log, int threads = 2)
    {
        _hub = hub;
        _shapes = new CollisionShapes(log, threads);
        hub.RegionChanged += OnRegion;
        foreach (var r in hub.Regions) OnRegion(r, true);
    }

    void OnRegion(RegionWorld w, bool created)
    {
        if (w.Handle == 0) return;
        lock (_gate)
        {
            if (created)
            {
                if (_regions.TryGetValue(w.Handle, out var have) && ReferenceEquals(have.World, w)) return;
                have?.Dispose();
                _regions[w.Handle] = new Region(w, new HubAssetSource(_hub, w.GridKey));
            }
            else if (_regions.TryGetValue(w.Handle, out var r) && ReferenceEquals(r.World, w))
            {
                _regions.Remove(w.Handle);
                r.Dispose();
            }
        }
    }

    /// <summary>The nearest solid surface along a ray (in the frame of the region <paramref name="region"/>), met from
    /// outside, within <paramref name="maxT"/>; the own avatar (<paramref name="self"/>, a local id there) left out.</summary>
    public CollideHit? CollideRay(ulong region, Vector3 origin, Vector3 dir, float maxT, uint self, Func<Primitive, CollideAs> solid)
    {
        var hits = CollideMany(region, [(origin, dir, maxT)], self, solid);
        return hits[0];
    }

    /// <summary>Many rays at once (one pass over the region's changes), each as <see cref="CollideRay"/>; a direction of
    /// zero length meets nothing.</summary>
    public CollideHit?[] CollideMany(ulong region, IReadOnlyList<(Vector3 From, Vector3 Dir, float Max)> rays, uint self, Func<Primitive, CollideAs> solid)
    {
        var hits = new CollideHit?[rays.Count];
        lock (_gate)
        {
            foreach (var r in _regions.Values) r.Update();
            for (int i = 0; i < rays.Count; i++)
            {
                var (origin, dir, max) = rays[i];
                if (dir.LengthSquared() < 1e-12f) continue;
                dir = Vector3.Normalize(dir);
                float best = max;
                CollideHit? hit = null;
                foreach (var r in _regions.Values)
                {
                    var off = Offset(region, r.World.Handle);
                    var o = origin + off;
                    if (!r.Near(o, best)) continue;
                    if (r.Ray(_shapes, o, dir, ref best, r.World.Handle == region ? self : 0, solid) is { } h) hit = h;
                }
                hits[i] = hit;
            }
        }
        return hits;
    }

    /// <summary>The solid triangles (as rays count them; avatars left out, they move) of every prim reaching into the box,
    /// in the frame of the region <paramref name="region"/>, three corners each, with the prim's local id once a triangle.</summary>
    public void SolidTriangles(ulong region, Vector3 min, Vector3 max, Func<Primitive, CollideAs> solid, List<Vector3> into, List<uint> ids)
    {
        lock (_gate)
        {
            foreach (var r in _regions.Values)
            {
                var off = Offset(region, r.World.Handle);
                if (!r.Overlaps(min + off, max + off)) continue;
                r.Update();
                int from = into.Count;
                r.Solids(_shapes, min + off, max + off, solid, into, ids);
                if (off != Vector3.Zero) for (int i = from; i < into.Count; i++) into[i] -= off;
            }
        }
    }

    /// <summary>Gets ready what's within <paramref name="reach"/> of the person (region frame): prims' shapes built,
    /// sculpt maps and meshes downloaded, nearest first, so they count before they're reached. Built here, on the
    /// caller's thread (a background loop's); downloads go on in the background.</summary>
    public void Around(ulong region, Vector3 at, float reach)
    {
        // (one caller, a loop once a second: the list is kept, and holds only what isn't ready, since a busy place
        // within the draw distance is tens of thousands of prims)
        var wanted = _wanted;
        wanted.Clear();
        lock (_gate)
        {
            foreach (var r in _regions.Values)
            {
                var off = Offset(region, r.World.Handle);
                if (!r.Near(at + off, reach)) continue;
                r.Update();
                r.Within(_shapes, at + off, reach, wanted);
            }
        }
        wanted.Sort((a, b) => a.D.CompareTo(b.D));
        foreach (var (d, key, prim, assets) in wanted)
        {
            if (key.Kind == ShapeKind.Prim) _shapes.Prebuild(key, prim.PrimData);
            else _shapes.Get(key, prim, assets, d, buildNow: false);
        }
        wanted.Clear();
    }

    readonly List<(float D, ShapeKey Key, Primitive Prim, IAssetSource Assets)> _wanted = new();

    /// <summary>For the status op: regions, prims indexed and placed, shapes (ready, waiting, downloading, failed) and
    /// roughly what they hold.</summary>
    public (int Regions, int Prims, int Placed, int Shapes, int Ready, int Waiting, int Fetching, int Failed, long Bytes) Status()
    {
        int regions, prims = 0, placed = 0;
        lock (_gate)
        {
            regions = _regions.Count;
            foreach (var r in _regions.Values) { prims += r.Count; placed += r.PlacedCount; }
        }
        var (ready, failed, bytes) = _shapes.Totals();
        return (regions, prims, placed, _shapes.Count, ready, _shapes.Waiting, _shapes.Fetching, failed, bytes);
    }

    static Vector3 Offset(ulong from, ulong to)
    {
        if (from == to) return Vector3.Zero;
        Utils.LongToUInts(from, out uint fx, out uint fy);
        Utils.LongToUInts(to, out uint tx, out uint ty);
        return new Vector3((float)((long)fx - tx), (float)((long)fy - ty), 0);
    }

    public void Dispose()
    {
        _hub.RegionChanged -= OnRegion;
        lock (_gate)
        {
            foreach (var r in _regions.Values) r.Dispose();
            _regions.Clear();
        }
        _shapes.Stop();
    }

    // --- one region's index ------------------------------------------------------------------------------------------

    sealed class Entry
    {
        public uint Id;
        public Primitive Prim = null!;
        public ShapeKey? Key;           // null: no shape (not a volume prim: trees, grass, particle sources)
        public uint ParentSeen;
        public bool Parented, Attached, Placed, Unplaced;
        public LVector3 PosSeen, ScaleSeen;
        public LQuaternion RotSeen;
        public Vector3 Pos, Scale;
        public Quaternion Rot = Quaternion.Identity;
        public float Radius;
        public Cell? Cell;
        public int Slot = -1, AllSlot = -1;
    }

    sealed class Cell
    {
        public readonly List<Entry> Items = new();
        public Vector3 Min, Max;
        public bool Stale;

        public void Refit()
        {
            Stale = false;
            for (int i = 0; i < Items.Count; i++)
            {
                var e = Items[i];
                var lo = e.Pos - new Vector3(e.Radius);
                var hi = e.Pos + new Vector3(e.Radius);
                if (i == 0) (Min, Max) = (lo, hi);
                else (Min, Max) = (Vector3.Min(Min, lo), Vector3.Max(Max, hi));
            }
        }
    }

    sealed class Region : IDisposable
    {
        const float CellSize = 16f, BigRadius = 24f;
        const int VerifyPerUpdate = 256, MaxQueued = 50_000;
        int _queued;
        volatile bool _overflow;
        public readonly RegionWorld World;
        public readonly IAssetSource Assets;
        readonly ConcurrentQueue<(bool Removed, uint Id)> _changes = new();
        readonly Dictionary<uint, Entry> _entries = new();
        readonly List<Entry> _all = new();
        readonly Dictionary<uint, List<Entry>> _children = new();
        readonly HashSet<Entry> _unplaced = new();
        Cell[] _cells = Array.Empty<Cell>();
        readonly Cell _big = new();
        int _gridW, _gridH, _verifyAt;
        bool _synced;
        long _nextUnplaced, _nextReconcile;
        public int PlacedCount;

        public Region(RegionWorld world, IAssetSource assets)
        {
            World = world;
            Assets = assets;
            world.Changed += OnChanged;
        }

        public int Count => _entries.Count;
        float SizeX => World.SizeX > 0 ? World.SizeX : 256f;
        float SizeY => World.SizeY > 0 ? World.SizeY : 256f;

        void OnChanged(RegionWorld w, WorldChange change, uint localId)
        {
            if (change is not (WorldChange.Added or WorldChange.Updated or WorldChange.Moved or WorldChange.Removed)) return;
            // a region nobody asks about for long (far from the person) would pile up its moving things' reports: past a
            // limit they're dropped and it's rescanned when next asked
            if (_overflow) return;
            if (Interlocked.Increment(ref _queued) > MaxQueued) { _overflow = true; return; }
            _changes.Enqueue((change == WorldChange.Removed, localId));
        }

        public bool Near(Vector3 at, float reach)
        {
            float pad = reach + BigRadius * 2;
            return at.X > -pad && at.Y > -pad && at.X < SizeX + pad && at.Y < SizeY + pad;
        }

        public bool Overlaps(Vector3 min, Vector3 max)
        {
            float pad = BigRadius * 2;
            return max.X > -pad && max.Y > -pad && min.X < SizeX + pad && min.Y < SizeY + pad;
        }

        public void Update()
        {
            EnsureGrid();
            long now = System.Environment.TickCount64;
            if (!_synced || _overflow)
            {
                _changes.Clear();   // everything below is current as of now
                Interlocked.Exchange(ref _queued, 0);
                _overflow = false;
                FullSync();
                _synced = true;
                _nextReconcile = now + 10_000;
                return;
            }
            while (_changes.TryDequeue(out var c))
            {
                Interlocked.Decrement(ref _queued);
                if (c.Removed) Remove(c.Id);
                else Touch(c.Id);
            }
            Verify();
            if (now >= _nextUnplaced && _unplaced.Count > 0)
            {
                _nextUnplaced = now + 500;
                foreach (var e in _unplaced.ToArray()) Place(e);
            }
            // a missed add or kill shows as a count that disagrees once the queue is empty
            if (now >= _nextReconcile)
            {
                _nextReconcile = now + 10_000;
                if (World.Prims.Count != _entries.Count) FullSync();
            }
        }

        void EnsureGrid()
        {
            if (_cells.Length > 0) return;
            _gridW = Math.Max(1, (int)MathF.Ceiling(SizeX / CellSize));
            _gridH = Math.Max(1, (int)MathF.Ceiling(SizeY / CellSize));
            _cells = new Cell[_gridW * _gridH];
            for (int i = 0; i < _cells.Length; i++) _cells[i] = new Cell();
        }

        void FullSync()
        {
            foreach (var id in _entries.Keys.ToArray())
                if (!World.Prims.ContainsKey(id)) Remove(id);
            foreach (var id in World.Prims.Keys) Touch(id);
        }

        /// <summary>A few hundred entries a query checked against the world model (round robin), as a safety net for a
        /// change the region's reports missed.</summary>
        void Verify()
        {
            for (int n = 0; n < VerifyPerUpdate && _all.Count > 0; n++)
            {
                if (_verifyAt >= _all.Count) _verifyAt = 0;
                var e = _all[_verifyAt++];
                if (!World.Prims.TryGetValue(e.Id, out var p)) { Remove(e.Id); continue; }
                if (!ReferenceEquals(p, e.Prim) || p.Position != e.PosSeen || p.Rotation != e.RotSeen || p.Scale != e.ScaleSeen
                    || p.ParentID != e.ParentSeen || CollisionShapes.KeyOf(p) != e.Key)
                    Touch(e.Id);
            }
        }

        void Touch(uint id)
        {
            if (!World.Prims.TryGetValue(id, out var p)) { Remove(id); return; }
            if (!_entries.TryGetValue(id, out var e))
            {
                e = new Entry { Id = id };
                _entries[id] = e;
                e.AllSlot = _all.Count;
                _all.Add(e);
            }
            e.Prim = p;
            e.Key = CollisionShapes.KeyOf(p);
            Place(e);
        }

        void Remove(uint id)
        {
            if (!_entries.Remove(id, out var e)) return;
            Unindex(e);
            Unparent(e);
            _unplaced.Remove(e);
            if (e.Placed) PlacedCount--;
            int last = _all.Count - 1;
            if (e.AllSlot != last) { _all[e.AllSlot] = _all[last]; _all[e.AllSlot].AllSlot = e.AllSlot; }
            _all.RemoveAt(last);
            // its children stay, unplaced, until they go too or find a parent
            if (_children.TryGetValue(id, out var kids)) foreach (var k in kids.ToArray()) Place(k);
        }

        void Unparent(Entry e)
        {
            if (!e.Parented || e.ParentSeen == 0) return;
            if (_children.TryGetValue(e.ParentSeen, out var kids))
            {
                kids.Remove(e);
                if (kids.Count == 0) _children.Remove(e.ParentSeen);
            }
        }

        void Place(Entry e)
        {
            var p = e.Prim;
            if (p.ParentID != e.ParentSeen || !e.Parented)
            {
                Unparent(e);
                e.ParentSeen = p.ParentID;
                e.Parented = true;
                if (p.ParentID != 0)
                {
                    if (!_children.TryGetValue(p.ParentID, out var kids)) _children[p.ParentID] = kids = new List<Entry>();
                    kids.Add(e);
                }
            }
            (e.PosSeen, e.RotSeen, e.ScaleSeen) = (p.Position, p.Rotation, p.Scale);
            bool wasPlaced = e.Placed;
            e.Attached = false;
            bool placed = false;
            var pos = V(p.Position);
            var rot = Q(p.Rotation);
            if (p.ParentID == 0) placed = true;
            else if (World.Avatars.ContainsKey(p.ParentID)) e.Attached = true;
            else if (World.Prims.TryGetValue(p.ParentID, out var root))
            {
                if (root.ParentID != 0)
                {
                    // the root's on an avatar: worn; else not placed yet
                    e.Attached = World.Avatars.ContainsKey(root.ParentID);
                }
                else
                {
                    var rrot = Q(root.Rotation);
                    pos = V(root.Position) + Vector3.Transform(pos, rrot);
                    rot = Quaternion.Normalize(Quaternion.Concatenate(rot, rrot));
                    placed = true;
                }
            }
            bool unplaced = !placed && !e.Attached;
            if (unplaced != e.Unplaced)
            {
                if (unplaced) _unplaced.Add(e); else _unplaced.Remove(e);
                e.Unplaced = unplaced;
            }
            e.Placed = placed;
            if (placed != wasPlaced) PlacedCount += placed ? 1 : -1;
            if (placed && e.Key != null)
            {
                e.Pos = pos;
                e.Rot = rot;
                e.Scale = V(p.Scale);
                // the prim's volume fits its scaled unit box; a mesh's hull may reach a little past it
                e.Radius = 0.5f * e.Scale.Length() * 1.05f + 0.02f;
                Reindex(e);
            }
            else Unindex(e);
            if (_children.TryGetValue(e.Id, out var children))
                foreach (var c in children.ToArray()) Place(c);
        }

        void Reindex(Entry e)
        {
            Cell cell;
            if (e.Radius > BigRadius) cell = _big;
            else
            {
                int cx = Math.Clamp((int)MathF.Floor(e.Pos.X / CellSize), 0, _gridW - 1);
                int cy = Math.Clamp((int)MathF.Floor(e.Pos.Y / CellSize), 0, _gridH - 1);
                cell = _cells[cy * _gridW + cx];
            }
            if (e.Cell != cell)
            {
                Unindex(e);
                e.Cell = cell;
                e.Slot = cell.Items.Count;
                cell.Items.Add(e);
            }
            cell.Stale = true;
        }

        void Unindex(Entry e)
        {
            if (e.Cell is not { } cell) return;
            int last = cell.Items.Count - 1;
            if (e.Slot != last) { cell.Items[e.Slot] = cell.Items[last]; cell.Items[e.Slot].Slot = e.Slot; }
            cell.Items.RemoveAt(last);
            cell.Stale = true;
            e.Cell = null;
            e.Slot = -1;
        }

        IEnumerable<Cell> Cells()
        {
            foreach (var c in _cells) yield return c;
            yield return _big;
        }

        public CollideHit? Ray(CollisionShapes shapes, Vector3 o, Vector3 d, ref float best, uint self, Func<Primitive, CollideAs> solid)
        {
            CollideHit? hit = null;
            foreach (var cell in Cells())
            {
                if (cell.Items.Count == 0) continue;
                if (cell.Stale) cell.Refit();
                if (!RegionScene.Slab(o, d, cell.Min, cell.Max, best, out _)) continue;
                foreach (var e in cell.Items)
                {
                    var oc = e.Pos - o;
                    float tc = Vector3.Dot(oc, d), r = e.Radius;
                    if (tc < -r || tc - r > best || (oc - d * tc).LengthSquared() > r * r) continue;
                    var how = solid(e.Prim);
                    if (how is CollideAs.Passable or CollideAs.None) continue;
                    if (shapes.Get(e.Key!.Value, e.Prim, Assets, oc.Length(), buildNow: true) is not { } shape) continue;
                    if (Hit(e, shape, how, o, d, ref best) is { } h) hit = h;
                }
            }
            foreach (var av in World.Avatars.Values)
            {
                if (self != 0 && av.LocalID == self) continue;
                if (CollideMath.Avatar(av, o, d, best, out float t, out var n)) { best = t; hit = new CollideHit(t, n, av.LocalID, true); }
            }
            return hit;
        }

        /// <summary>One prim against a ray, as RegionScene.CollideRay meets it: in the shape's unit space, the rule's
        /// triangles (or the drawn ones), front faces only.</summary>
        static CollideHit? Hit(Entry e, CollisionShape shape, CollideAs how, Vector3 o, Vector3 d, ref float best)
        {
            var scale = e.Scale;
            if (scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) return null;
            var tris = Triangles(e, shape, how, out var pad);
            if (tris == null) return null;
            var inv = Quaternion.Conjugate(e.Rot);
            var lo = Vector3.Transform(o - e.Pos, inv) / scale;
            var ld = Vector3.Transform(d, inv) / scale;
            if (!RegionScene.Slab(lo, ld, shape.Min - pad, shape.Max + pad, best, out _)) return null;
            CollideHit? hit = null;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = tris[i], b = tris[i + 1], c = tris[i + 2];
                var nu = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(nu, ld) >= 0) continue;
                if (!RegionScene.Triangle(lo, ld, a, b, c, out float t, out _, out _) || t >= best) continue;
                var wn = Vector3.Transform(nu / scale, e.Rot);
                if (wn.LengthSquared() < 1e-20f) continue;
                best = t;
                hit = new CollideHit(t, Vector3.Normalize(wn), e.Id, false);
            }
            return hit;
        }

        static Vector3[]? Triangles(Entry e, CollisionShape shape, CollideAs how, out Vector3 pad)
        {
            var rule = CollisionRule.Triangles(e.Key!.Value.Kind == ShapeKind.Mesh, shape.Physics, how, e.Scale, shape.HullOfDrawn);
            // (a mesh's hull is the asset's, quantised over its own domain: it can reach a little past the drawn box)
            pad = rule != null ? new Vector3(0.02f) : Vector3.Zero;
            return rule ?? shape.Drawn;
        }

        public void Solids(CollisionShapes shapes, Vector3 min, Vector3 max, Func<Primitive, CollideAs> solid, List<Vector3> into, List<uint> ids)
        {
            foreach (var cell in Cells())
            {
                if (cell.Items.Count == 0) continue;
                if (cell.Stale) cell.Refit();
                if (cell.Max.X < min.X || cell.Min.X > max.X || cell.Max.Y < min.Y || cell.Min.Y > max.Y || cell.Max.Z < min.Z || cell.Min.Z > max.Z) continue;
                foreach (var e in cell.Items)
                {
                    var c = e.Pos;
                    float r = e.Radius;
                    if (c.X + r < min.X || c.X - r > max.X || c.Y + r < min.Y || c.Y - r > max.Y || c.Z + r < min.Z || c.Z - r > max.Z) continue;
                    var how = solid(e.Prim);
                    if (how is CollideAs.Passable or CollideAs.None) continue;
                    var scale = e.Scale;
                    if (scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) continue;
                    if (shapes.Get(e.Key!.Value, e.Prim, Assets, 0, buildNow: true) is not { } shape) continue;
                    if (Triangles(e, shape, how, out _) is not { } tris) continue;
                    foreach (var t in tris) into.Add(e.Pos + Vector3.Transform(t * scale, e.Rot));
                    for (int i = 0; i + 2 < tris.Length; i += 3) ids.Add(e.Id);
                }
            }
        }

        /// <summary>What's within reach of a point whose shape isn't ready, with its distance (the ready ones kept, as
        /// still wanted).</summary>
        public void Within(CollisionShapes shapes, Vector3 at, float reach, List<(float D, ShapeKey Key, Primitive Prim, IAssetSource Assets)> into)
        {
            foreach (var cell in Cells())
            {
                if (cell.Items.Count == 0) continue;
                if (cell.Stale) cell.Refit();
                if (cell.Max.X < at.X - reach || cell.Min.X > at.X + reach || cell.Max.Y < at.Y - reach || cell.Min.Y > at.Y + reach) continue;
                foreach (var e in cell.Items)
                {
                    float d = MathF.Max(0, Vector3.Distance(e.Pos, at) - e.Radius);
                    if (d <= reach && !shapes.Settled(e.Key!.Value)) into.Add((d, e.Key!.Value, e.Prim, Assets));
                }
            }
        }

        static Vector3 V(LVector3 v) => new(v.X, v.Y, v.Z);
        static Quaternion Q(LQuaternion q) => new(q.X, q.Y, q.Z, q.W);

        public void Dispose() => World.Changed -= OnChanged;
    }
}
