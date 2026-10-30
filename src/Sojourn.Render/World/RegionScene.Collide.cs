using LibreMetaverse;
using Sojourn.Render.Geometry;

namespace Sojourn.Render.World;

/// <summary>What a collision ray met first: a solid surface's distance, its outward normal (region frame), and the prim's
/// local id (0 for an avatar).</summary>
public readonly record struct CollideHit(float Distance, Vector3 Normal, uint LocalId, bool Avatar);

public sealed partial class RegionScene
{
    readonly List<SceneObject> _collideFound = new();

    /// <summary>
    /// The own avatar's local collision model (the headset's walk, Sojourn.Client VrPilot.Collide.cs): the nearest solid
    /// surface along a ray (dir normalised), met from its outside. Prims count by their physics shape as the region
    /// collides with them (<see cref="CollisionTriangles"/>; <paramref name="solid"/> gives the type), front faces only,
    /// so a ray starting inside a prim walks out of it freely; prims still loading, trees and grass, attachments and
    /// HUDs don't count, nor does a prim with physics shape none or anything else <paramref name="solid"/> calls
    /// passable (phantom ones, ones the region let the avatar through). Other standing avatars count as a box around
    /// the body. Render thread, between frames.
    /// </summary>
    public CollideHit? CollideRay(Vector3 origin, Vector3 dir, float maxT, uint skipAvatar, Func<Primitive, CollideAs> solid)
    {
        float best = maxT;
        CollideHit? hit = null;
        foreach (var obj in RayCandidates(origin, dir, best, _collideFound))
        {
            if (obj.Skip || obj.Owner != 0 || obj.Kind is ShapeKind.Tree or ShapeKind.Grass or ShapeKind.RiggedMesh) continue;
            var prim = obj.Prim;
            var how = solid(prim);
            if (how is CollideAs.Passable or CollideAs.None || !Transform(obj, out var pos, out var rot)) continue;
            var scale = new Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z);
            if (scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) continue;
            var shape = obj.Faces?.Shape ?? obj.Lods.FirstOrDefault(l => l is { Ready: true, Evicted: false });
            if (shape == null || shape.Min.X > shape.Max.X) continue;
            var inv = Quaternion.Conjugate(rot);
            var lo = Vector3.Transform(origin - pos, inv) / scale;
            var ld = Vector3.Transform(dir, inv) / scale;
            var tris = CollisionTriangles(obj, shape, how, scale);
            // (a mesh's hull is the asset's, quantised over its own domain: it can reach a little past the drawn box)
            var pad = tris != null ? new Vector3(0.02f) : Vector3.Zero;
            if (!Slab(lo, ld, shape.Min - pad, shape.Max + pad, best, out _)) continue;
            if (tris != null)
            {
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vector3 a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    var nu = Vector3.Cross(b - a, c - a);
                    if (Vector3.Dot(nu, ld) >= 0) continue;
                    if (!Triangle(lo, ld, a, b, c, out float t, out _, out _) || t >= best) continue;
                    var wn = Vector3.Transform(nu / scale, rot);
                    if (wn.LengthSquared() < 1e-20f) continue;
                    best = t;
                    hit = new CollideHit(t, Vector3.Normalize(wn), prim.LocalID, false);
                }
                continue;
            }
            foreach (var f in shape.Faces)
            {
                if (f.PickPositions is not { } p || f.PickIndices is not { } idx) continue;
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    Vector3 a = p[idx[i]], b = p[idx[i + 1]], c = p[idx[i + 2]];
                    // faces wind counter-clockwise seen from outside (checked on the local grid's boxes): met from behind,
                    // they don't count (the sign holds through the scale)
                    var nu = Vector3.Cross(b - a, c - a);
                    if (Vector3.Dot(nu, ld) >= 0) continue;
                    if (!Triangle(lo, ld, a, b, c, out float t, out _, out _) || t >= best) continue;
                    var wn = Vector3.Transform(nu / scale, rot);
                    if (wn.LengthSquared() < 1e-20f) continue;
                    best = t;
                    hit = new CollideHit(t, Vector3.Normalize(wn), prim.LocalID, false);
                }
            }
        }
        foreach (var av in Source.Avatars.Values)
        {
            if (av.LocalID == skipAvatar || av.ParentID != 0) continue;   // seated avatars are their seat's
            var p = new Vector3(av.Position.X, av.Position.Y, av.Position.Z);
            float h = av.Scale.Z is > 0.5f and < 4f ? av.Scale.Z : 1.9f;
            var half = new Vector3(0.25f, 0.25f, h / 2);
            var o = origin - p;
            if (MathF.Abs(o.X) < half.X && MathF.Abs(o.Y) < half.Y && MathF.Abs(o.Z) < half.Z) continue;   // inside: walk out
            if (Slab(origin, dir, p - half, p + half, best, out float t) && t > 0)
            {
                var at = origin + dir * t - p;
                var n = MathF.Abs(at.X / half.X) > MathF.Abs(at.Y / half.Y) ? new Vector3(MathF.Sign(at.X), 0, 0) : new Vector3(0, MathF.Sign(at.Y), 0);
                if (MathF.Abs(at.Z) > half.Z - 1e-3f) n = new Vector3(0, 0, MathF.Sign(at.Z));
                best = t;
                hit = new CollideHit(t, n, av.LocalID, true);
            }
        }
        return hit;
    }

    /// <summary>
    /// The collision model's solid triangles (as <see cref="CollideRay"/> counts them: physics shapes, nothing passable,
    /// no trees, grass or attachments, only what's been drawn) of every prim whose reach overlaps the box
    /// [<paramref name="min"/>, <paramref name="max"/>], in this region's frame, three points each, added to
    /// <paramref name="into"/> with the prim's local id once per triangle. For planning walks around things (Sojourn
    /// Access); avatars aren't included (they move). Render thread, between frames.
    /// </summary>
    public void SolidTriangles(Vector3 min, Vector3 max, Func<Primitive, CollideAs> solid, List<Vector3> into, List<uint> ids)
    {
        for (int i = 0; i <= _cells.Length; i++)
        {
            var cell = i < _cells.Length ? _cells[i] : _big;
            if (cell.Objects.Count == 0) continue;
            if (cell.Stale) cell.Refit();
            if (cell.ReachMax.X < min.X || cell.ReachMin.X > max.X || cell.ReachMax.Y < min.Y || cell.ReachMin.Y > max.Y
                || cell.ReachMax.Z < min.Z || cell.ReachMin.Z > max.Z) continue;
            foreach (var obj in cell.Objects)
            {
                if (obj.Skip || obj.Owner != 0 || obj.Kind is ShapeKind.Tree or ShapeKind.Grass or ShapeKind.RiggedMesh) continue;
                var c = obj.CullCenter;
                float r = obj.CullRadius;
                if (c.X + r < min.X || c.X - r > max.X || c.Y + r < min.Y || c.Y - r > max.Y || c.Z + r < min.Z || c.Z - r > max.Z) continue;
                var prim = obj.Prim;
                var how = solid(prim);
                if (how is CollideAs.Passable or CollideAs.None || !Transform(obj, out var pos, out var rot)) continue;
                var scale = new Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z);
                if (scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) continue;
                var shape = obj.Faces?.Shape ?? obj.Lods.FirstOrDefault(l => l is { Ready: true, Evicted: false });
                if (shape == null || shape.Min.X > shape.Max.X) continue;
                Vector3 W(Vector3 local) => pos + Vector3.Transform(local * scale, rot);
                if (CollisionTriangles(obj, shape, how, scale) is { } tris)
                {
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        into.Add(W(tris[t])); into.Add(W(tris[t + 1])); into.Add(W(tris[t + 2]));
                        ids.Add(prim.LocalID);
                    }
                    continue;
                }
                foreach (var f in shape.Faces)
                {
                    if (f.PickPositions is not { } p || f.PickIndices is not { } idx) continue;
                    for (int t = 0; t + 2 < idx.Length; t += 3)
                    {
                        into.Add(W(p[idx[t]])); into.Add(W(p[idx[t + 1]])); into.Add(W(p[idx[t + 2]]));
                        ids.Add(prim.LocalID);
                    }
                }
            }
        }
    }

    /// <summary>
    /// What the region collides with for a prim, where that isn't its drawn triangles (null: they are), after the
    /// viewer's LLPhysicsShapeBuilderUtil::determinePhysicsShape and renderPhysicsShape, in the shape's unit space:
    /// - A mesh with a convex hull shape: the asset's hull (or the drawn mesh's, without one).
    /// - A mesh with a prim shape: its decomposition's hulls if it has them; else its physics model, unless it's under
    ///   0.5 m in some dimension (the region's thin-triangle rule makes that the hull); else its hull. While its type
    ///   isn't known, as uploaded: a prim shape if the asset has a physics model, a hull if not.
    /// - A prim or sculpt with a convex hull shape: the hull of its drawn shape. With a prim shape, its drawn triangles
    ///   (the region's own simplifications, boxes, spheres and cylinders, are the same surfaces).
    /// Before the mesh's data has arrived, its drawn triangles.
    /// </summary>
    Vector3[]? CollisionTriangles(SceneObject obj, ShapeGeometry shape, CollideAs how, Vector3 scale)
    {
        if (obj.Kind == ShapeKind.Mesh)
        {
            if (_world.Geometry.Physics(obj.Asset) is not { } phys) return how == CollideAs.ConvexHull ? NonEmpty(ConvexHull.Of(shape)) : null;
            bool prim = how == CollideAs.Prim || how == CollideAs.Unknown && phys.UploadedAsPrim;
            if (prim)
            {
                if (phys.Decomposition is { } d) return d;
                bool thin = scale.X < 0.5f || scale.Y < 0.5f || scale.Z < 0.5f;
                if (!thin && phys.Triangles is { } t) return t;
            }
            return phys.Hull ?? NonEmpty(ConvexHull.Of(shape));
        }
        return how == CollideAs.ConvexHull ? NonEmpty(ConvexHull.Of(shape)) : null;
    }

    static Vector3[]? NonEmpty(Vector3[] tris) => tris.Length > 0 ? tris : null;
}
