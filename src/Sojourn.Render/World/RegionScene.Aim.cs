namespace Sojourn.Render.World;

/// <summary>One face of a prim as drawn (RegionScene.FaceList): a point to aim at and its normal, the face's mean
/// normal, area (m²), flatness (0-1) and whether it faces inward.</summary>
public readonly record struct FaceShape(int Face, Vector3 Point, Vector3 Normal, Vector3 Mean, float Area, float Flat, bool Inner);

public sealed partial class RegionScene
{
    /// <summary>
    /// A point on one face of a prim and its outward normal (this region's frame), for touching that face without a
    /// pointer (Sojourn Access touches parts and faces by name and number): the middle of the face's triangle nearest its
    /// middle (the triangles' centre by area), of the LOD drawn last. Null while the prim isn't drawn yet (no triangles
    /// kept), for rigged meshes, or when the prim has no such face. Render thread, between frames.
    /// </summary>
    public (Vector3 Point, Vector3 Normal)? FaceAim(uint localId, int face)
    {
        if (!_objects.TryGetValue(localId, out var obj) || obj.Skip || !Transform(obj, out var pos, out var rot)) return null;
        var shape = obj.Faces?.Shape ?? obj.Lods.FirstOrDefault(l => l is { Ready: true, Evicted: false });
        if (shape == null || shape.Skin != null) return null;
        var prim = obj.Prim;
        var scale = new Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z);
        if (scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) return null;
        var tris = new List<(Vector3 Mid, Vector3 Cross)>();
        Vector3 sum = Vector3.Zero;
        float total = 0;
        foreach (var f in shape.Faces)
        {
            if (f.TeIndex != face || f.PickPositions is not { } p || f.PickIndices is not { } idx) continue;
            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                Vector3 a = p[idx[i]] * scale, b = p[idx[i + 1]] * scale, c = p[idx[i + 2]] * scale;
                var cross = Vector3.Cross(b - a, c - a);
                float area = cross.Length();
                if (area < 1e-10f) continue;
                var mid = (a + b + c) / 3;
                tris.Add((mid, cross));
                sum += mid * area;
                total += area;
            }
        }
        if (tris.Count == 0) return null;
        var centre = sum / total;
        var (point, n) = tris.MinBy(t => Vector3.DistanceSquared(t.Mid, centre));
        // faces wind counter-clockwise seen from outside (RegionScene.Collide.cs)
        return (pos + Vector3.Transform(point, rot), Vector3.Normalize(Vector3.Transform(n, rot)));
    }

    /// <summary>
    /// Every face a prim has as drawn (Sojourn Access names them for the person: "the top", "the side facing you"): for
    /// each texture entry index, a point on it to aim at (as <see cref="FaceAim"/>), the outward normal there, its
    /// area-weighted mean normal (this region's frame), its area, how flat it is (1 a plane, toward 0 for a face wrapping
    /// round, as a cylinder's side) and whether it faces into the prim (a hollow's inside). Null as FaceAim's is.
    /// </summary>
    public List<FaceShape>? FaceList(uint localId)
    {
        if (!_objects.TryGetValue(localId, out var obj) || obj.Skip || !Transform(obj, out var pos, out var rot)) return null;
        var shape = obj.Faces?.Shape ?? obj.Lods.FirstOrDefault(l => l is { Ready: true, Evicted: false });
        if (shape == null || shape.Skin != null) return null;
        var prim = obj.Prim;
        var scale = new Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z);
        if (scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) return null;
        var byFace = new SortedDictionary<int, List<(Vector3 Mid, Vector3 Cross)>>();
        foreach (var f in shape.Faces)
        {
            if (f.PickPositions is not { } p || f.PickIndices is not { } idx) continue;
            if (!byFace.TryGetValue(f.TeIndex, out var tris)) byFace[f.TeIndex] = tris = new();
            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                Vector3 a = p[idx[i]] * scale, b = p[idx[i + 1]] * scale, c = p[idx[i + 2]] * scale;
                var cross = Vector3.Cross(b - a, c - a);
                if (cross.LengthSquared() < 1e-20f) continue;
                tris.Add(((a + b + c) / 3, cross));
            }
        }
        var list = new List<FaceShape>();
        foreach (var (face, tris) in byFace)
        {
            if (tris.Count == 0) continue;
            Vector3 sum = Vector3.Zero, crosses = Vector3.Zero;
            float total = 0;
            foreach (var (mid, cross) in tris)
            {
                float area = cross.Length();
                sum += mid * area;
                crosses += cross;
                total += area;
            }
            var centre = sum / total;
            var (point, n) = tris.MinBy(t => Vector3.DistanceSquared(t.Mid, centre));
            float flat = crosses.Length() / total;
            var mean = crosses.LengthSquared() > 1e-20f ? Vector3.Normalize(crosses) : Vector3.Normalize(n);
            list.Add(new FaceShape(face, pos + Vector3.Transform(point, rot), Vector3.Normalize(Vector3.Transform(n, rot)),
                Vector3.Normalize(Vector3.Transform(mean, rot)), total / 2, flat, Vector3.Dot(mean, centre) < 0));
        }
        return list;
    }

    /// <summary>Whether a prim is drawn with its triangles kept (picks and touches land on its surface, not its box).</summary>
    public bool HasTriangles(uint localId) =>
        _objects.TryGetValue(localId, out var obj)
        && (obj.Faces?.Shape ?? obj.Lods.FirstOrDefault(l => l is { Ready: true, Evicted: false })) is { } s
        && s.Faces.Any(f => f.PickPositions != null);
}
