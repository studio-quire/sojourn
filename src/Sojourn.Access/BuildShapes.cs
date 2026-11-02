using System.Numerics;
using LibreMetaverse;
using Sojourn.Render.Geometry;
using Sojourn.Render.World;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access;

/// <summary>
/// What building needs to know of a prim's shape without a picture (docs/access.md > M6): its faces from its own volume,
/// built on the processor as the viewer builds it (VolumeBuilder, the port of llvolume.cpp), so the editor names faces
/// by direction on a PC that can't run the renderer out of sight; and how two prims' boxes meet (touching, apart, sunk
/// into each other). Sculpted prims are one face; a mesh's faces need its model (the renderer's FaceList, else numbers
/// only).
/// </summary>
public static class BuildShapes
{
    /// <summary>A plain prim's faces (region frame, as <see cref="RegionScene.FaceList"/> gives them), or null for a
    /// mesh, a sculpt or anything the volume can't be built for.</summary>
    public static List<FaceShape>? Faces(Primitive p, Vector3 pos, Quaternion rot)
    {
        if (p.PrimData.PCode != PCode.Prim || p.Sculpt is { } sc && sc.SculptTexture != UUID.Zero) return null;
        VolumeFace[] faces;
        try { faces = VolumeBuilder.BuildPrim(p.PrimData, 1f); }
        catch (Exception) { return null; }
        var scale = new Vector3(p.Scale.X, p.Scale.Y, p.Scale.Z);
        if (scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) return null;
        var byFace = new SortedDictionary<int, List<(Vector3 Mid, Vector3 Cross)>>();
        foreach (var f in faces)
        {
            if (!byFace.TryGetValue(f.TeIndex, out var tris)) byFace[f.TeIndex] = tris = new();
            for (int i = 0; i + 2 < f.Indices.Length; i += 3)
            {
                Vector3 a = f.Positions[f.Indices[i]] * scale, b = f.Positions[f.Indices[i + 1]] * scale, c = f.Positions[f.Indices[i + 2]] * scale;
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

    /// <summary>A face by the prim's own axes, the 2009 design's naming ("plusx", "minus z", "+y"), or by the compass
    /// ("north", "the east side"): the outside face whose mean normal points most that way.</summary>
    public static FaceShape? ByAxis(IReadOnlyList<FaceShape> faces, string words, Quaternion rot)
    {
        var w = words.Trim().ToLowerInvariant().Replace(" ", "").Replace("side", "").Replace("the", "");
        Vector3? local = w switch
        {
            "plusx" or "+x" or "posx" => Vector3.UnitX, "minusx" or "-x" or "negx" => -Vector3.UnitX,
            "plusy" or "+y" or "posy" => Vector3.UnitY, "minusy" or "-y" or "negy" => -Vector3.UnitY,
            "plusz" or "+z" or "posz" => Vector3.UnitZ, "minusz" or "-z" or "negz" => -Vector3.UnitZ,
            _ => null,
        };
        Vector3? world = local is { } l ? Vector3.Transform(l, rot) : w switch
        {
            "north" or "n" => Vector3.UnitY, "south" or "s" => -Vector3.UnitY, "east" or "e" => Vector3.UnitX, "west" or "w" => -Vector3.UnitX,
            _ => null,
        };
        if (world is not { } dir) return null;
        return faces.Where(f => !f.Inner).Select(f => (f, d: Vector3.Dot(f.Mean, dir))).Where(x => x.d > 0.5f)
            .OrderByDescending(x => x.d).ThenByDescending(x => x.f.Area).Select(x => (FaceShape?)x.f).FirstOrDefault();
    }

    /// <summary>A prim's box (its scale is its bounding box, turned with it).</summary>
    public readonly record struct Box(Vector3 Centre, Quaternion Rot, Vector3 Half)
    {
        public Vector3 Axis(int i) => Vector3.Transform(i == 0 ? Vector3.UnitX : i == 1 ? Vector3.UnitY : Vector3.UnitZ, Rot);
        public float HalfOf(int i) => i == 0 ? Half.X : i == 1 ? Half.Y : Half.Z;
        public float Radius => Half.Length();

        /// <summary>How far the box reaches along a direction from its centre.</summary>
        public float Reach(Vector3 dir) =>
            MathF.Abs(Vector3.Dot(Axis(0), dir)) * Half.X + MathF.Abs(Vector3.Dot(Axis(1), dir)) * Half.Y + MathF.Abs(Vector3.Dot(Axis(2), dir)) * Half.Z;

        /// <summary>Its lowest point's height and the middle of its underside, as turned.</summary>
        public float Bottom => Centre.Z - Reach(Vector3.UnitZ);
    }

    /// <summary>The gap between two boxes along the axis that separates them most (separating axis test over the 15
    /// axes): positive, how far apart; negative, how deep they overlap (the least push that would part them). A box's
    /// own shape may be smaller than its box (a sphere, a hollow), so "touching" for those is the boxes'.</summary>
    public static float Gap(Box a, Box b)
    {
        var axes = new List<Vector3>(15);
        for (int i = 0; i < 3; i++) { axes.Add(a.Axis(i)); axes.Add(b.Axis(i)); }
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                var c = Vector3.Cross(a.Axis(i), b.Axis(j));
                if (c.LengthSquared() > 1e-6f) axes.Add(Vector3.Normalize(c));
            }
        var d = b.Centre - a.Centre;
        float best = float.NegativeInfinity;
        foreach (var ax in axes)
        {
            float gap = MathF.Abs(Vector3.Dot(d, ax)) - a.Reach(ax) - b.Reach(ax);
            if (gap > best) best = gap;
        }
        return best;
    }

    /// <summary>A plain box (no cut, hollow, twist, taper or shear): its box is its shape.</summary>
    public static bool IsPlainBox(Primitive p)
    {
        var d = p.PrimData;
        return p.PrimData.PCode == PCode.Prim && (p.Sculpt == null || p.Sculpt.SculptTexture == UUID.Zero)
               && (d.profileCurve & 0x0f) == 1 && ((byte)d.PathCurve & 0xf0) == 0x10
               && d.ProfileBegin == 0 && d.ProfileEnd == 1 && d.PathBegin == 0 && d.PathEnd == 1 && d.ProfileHollow == 0
               && d.PathTwist == 0 && d.PathTwistBegin == 0 && d.PathShearX == 0 && d.PathShearY == 0
               && d.PathScaleX == 1 && d.PathScaleY == 1;
    }
}
