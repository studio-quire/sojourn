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
            if (av.LocalID == skipAvatar) continue;
            if (CollideMath.Avatar(av, origin, dir, best, out float t, out var n)) { best = t; hit = new CollideHit(t, n, av.LocalID, true); }
        }
        return hit;
    }

    /// <summary>What the region collides with for a prim, where that isn't its drawn triangles (null: they are):
    /// <see cref="CollisionRule.Triangles"/>, the hull of the drawn shape from the shape drawn.</summary>
    Vector3[]? CollisionTriangles(SceneObject obj, ShapeGeometry shape, CollideAs how, Vector3 scale) =>
        CollisionRule.Triangles(obj.Kind == ShapeKind.Mesh, _world.Geometry.Physics(obj.Asset), how, scale, () => ConvexHull.Of(shape));
}
