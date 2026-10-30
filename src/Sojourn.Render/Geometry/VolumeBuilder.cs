// Ported from the Second Life viewer: indra/llmath/llvolume.cpp (LLVolume::generate, LLVolume::sculpt,
// sculptGenerateMapVertices, sculpt_calc_mesh_resolution, validate_sculpt_geometry, placeholders) and
// indra/llmath/llvolumemgr.cpp (LOD detail scales). Copyright (C) 2010, Linden Research, Inc.
// LGPL 2.1 only; see the viewer's licence header.
using System.Numerics;
using Primitive = LibreMetaverse.Primitive;

namespace Sojourn.Render.Geometry;

/// <summary>What one flexible prim's steps build into again and again (VolumeBuilder.BuildFlexi): its profile, path,
/// mesh and faces. The faces returned are these buffers: read them before the next step.</summary>
public sealed class FlexiBuffers
{
    internal VolumeProfile? Profile;
    internal VolumeParams? ProfileFor;
    internal float ProfileDetail;
    internal readonly VolumePath Path = new();
    internal Vector3[]? Mesh;
    internal VolumeFace[]? Faces;
}

public static class VolumeBuilder
{
    // LLVolumeLODGroup::mDetailScales: volume detail for LOD 0..3.
    private static readonly float[] DetailScales = { 1f, 1.5f, 2.5f, 4f };

    public static float DetailForLod(int lod) => DetailScales[Math.Clamp(lod, 0, 3)];

    // LLVolumeLODGroup::getVolumeDetailFromScale
    public static int LodForDetail(float detail)
    {
        for (int i = 1; i < 4; i++)
            if (DetailScales[i] > detail)
                return i - 1;
        return 3;
    }

    public static VolumeFace[] BuildPrim(Primitive.ConstructionData shape, float detail) =>
        BuildPrim(VolumeParams.FromConstructionData(shape), detail);

    // A plain prim (no sculpt). Flexible paths (0x80) are built here as their straight, unsimulated line (drawn while
    // the simulation's first shape is on its way, and with flexible prims off); BuildFlexi is the simulated one.
    public static VolumeFace[] BuildPrim(VolumeParams p, float detail)
    {
        if ((p.SculptType & VolumeConsts.SculptTypeMask) != VolumeConsts.SculptTypeNone)
        {
            p = p.Clone();
            p.SculptType = VolumeConsts.SculptTypeNone;
        }
        VolumeMesh? v = Generate(p, detail);
        return v == null ? Array.Empty<VolumeFace>() : VolumeFaceBuilder.CreateVolumeFaces(v);
    }

    public static VolumeFace[] BuildSculpt(Primitive.ConstructionData shape, Primitive.SculptData sculpt,
        ReadOnlySpan<byte> rgba, int width, int height, float detail, int components = 4, bool rowsTopDown = true,
        bool missingAsset = false)
    {
        byte type = (byte)((byte)sculpt.Type | (sculpt.Invert ? VolumeConsts.SculptFlagInvert : 0) |
                           (sculpt.Mirror ? VolumeConsts.SculptFlagMirror : 0));
        return BuildSculpt(VolumeParams.FromConstructionData(shape, type), rgba, width, height, detail, components, rowsTopDown, missingAsset);
    }

    // LLVolume::sculpt. The image is the sculpt map at the resolution the viewer holds (see SculptDiscardLevel),
    // `components` bytes per pixel (fewer than 3 means "no data"). rowsTopDown: row 0 is the top of the picture,
    // as JPEG2000 decoders (CoreJ2K) produce it. The viewer's own decoders flip rows so its raw row 0 is the
    // bottom; pass false for data already in that order. With no data the viewer makes an empty placeholder (all
    // vertices at the origin), or a sphere placeholder when the asset is missing; a map whose surface area is out
    // of range gets the sphere too, and one that fails validation the empty one. Stitching type none is a sculpt too
    // (the viewer sculpts any prim with the sculpt parameter): no pinch or wrap, edges clamped, normals as a plain prim's.
    public static VolumeFace[] BuildSculpt(VolumeParams p, ReadOnlySpan<byte> image, int width, int height, float detail,
        int components = 4, bool rowsTopDown = true, bool missingAsset = false)
    {
        int stitching = p.SculptType & VolumeConsts.SculptTypeMask;
        if (stitching >= VolumeConsts.SculptTypeMesh)
            throw new ArgumentException("Not a sculpt type: " + p.SculptType);

        bool dataIsEmpty = false;
        bool visiblePlaceholder = missingAsset;
        if (width <= 0 || height <= 0 || components < 3 || image.Length < width * height * components)
        {
            dataIsEmpty = true;
            width = Math.Max(width, 0);
            height = Math.Max(height, 0);
        }
        // The viewer passes U16 sizes.
        width = Math.Min(width, ushort.MaxValue);
        height = Math.Min(height, ushort.MaxValue);

        SculptCalcMeshResolution(width, height, detail, out int requestedS, out int requestedT);

        var path = new VolumePath();
        var profile = new VolumeProfile();
        path.Generate(p, detail, 0, true, requestedS);
        profile.Generate(p, path.Open, detail, 0, true, requestedT);

        int sizeS = path.Points.Count;
        int sizeT = profile.Points.Count;
        var v = new VolumeMesh { Params = p, Path = path, Profile = profile, Mesh = new Vector3[sizeS * sizeT] };

        if (!dataIsEmpty)
        {
            SculptGenerateMapVertices(v, width, height, components, rowsTopDown, image, p.SculptType);

            // Don't test the lowest LOD, to support legacy content (DEV-33670).
            if (detail > 1) // SCULPT_MIN_AREA_DETAIL
            {
                float area = SculptGetSurfaceArea(v);
                if (area < 0.002f || area > 384f) // SCULPT_MIN_AREA, SCULPT_MAX_AREA
                {
                    dataIsEmpty = true;
                    visiblePlaceholder = true;
                }
            }
        }

        if (dataIsEmpty)
        {
            if (visiblePlaceholder)
                SculptGenerateSpherePlaceholder(v);
            else
                Array.Clear(v.Mesh);
        }

        VolumeFace[] faces = VolumeFaceBuilder.CreateVolumeFaces(v);

        if (!dataIsEmpty && !ValidateSculptGeometry(faces))
        {
            // "Sculpt failed geometry validation - either invalid image or malicious."
            Array.Clear(v.Mesh);
            faces = VolumeFaceBuilder.CreateVolumeFaces(v);
        }
        return faces;
    }

    // LLVolume::generate: path + profile, then sweep the profile along the path.
    internal static VolumeMesh? Generate(VolumeParams p, float detail)
    {
        // Split tessellates profile edges to prevent lighting/texture errors on triangles stretched by twist or scale.
        int split = (int)(detail * 0.66f);
        if (p.PathCurve == VolumeConsts.PathLine &&
            (p.ScaleX != 1.0f || p.ScaleY != 1.0f) &&
            (p.ProfileCurve == VolumeConsts.ProfileSquare || p.ProfileCurve == VolumeConsts.ProfileIsoTri ||
             p.ProfileCurve == VolumeConsts.ProfileEqualTri || p.ProfileCurve == VolumeConsts.ProfileRightTri))
        {
            split = 0;
        }

        var path = new VolumePath();
        var profile = new VolumeProfile();
        path.Generate(p, detail, split);
        if (!profile.Generate(p, path.Open, detail, split))
            return null;
        return Sweep(p, path, profile);
    }

    // A flexible prim's split: as LLVolume::generate's (its path isn't a line, so never zeroed).
    static int FlexiSplit(float detail) => (int)(detail * 0.66f);

    /// <summary>A flexible prim at one moment (LLVolume::generate over LLDynamicPath): the profile swept along the path
    /// points its simulation gives, in the prim's frame in metres (llflexibleobject.cpp doFlexibleUpdate). The faces'
    /// vertices and indices line up with <see cref="BuildFlexiRest"/> for the same number of points.</summary>
    public static VolumeFace[] BuildFlexi(VolumeParams p, float detail, ReadOnlySpan<FlexiPathPoint> points, FlexiBuffers? buffers = null)
    {
        if (points.Length < 2) return Array.Empty<VolumeFace>();
        // a prim stepped again keeps its profile (the same for every step at a detail), its mesh and its faces' arrays
        var b = buffers ?? new FlexiBuffers();
        if (!ReferenceEquals(b.ProfileFor, p) || b.ProfileDetail != detail || b.Profile == null)
        {
            var profile = new VolumeProfile();
            if (!profile.Generate(p, true, detail, FlexiSplit(detail)))
                return Array.Empty<VolumeFace>();
            (b.Profile, b.ProfileFor, b.ProfileDetail, b.Faces) = (profile, p, detail, null);
        }
        b.Path.SetPoints(points);
        var mesh = Sweep(p, b.Path, b.Profile, b.Mesh);
        b.Mesh = mesh.Mesh;
        return b.Faces = VolumeFaceBuilder.CreateVolumeFaces(mesh, b.Faces);
    }

    /// <summary>A flexible prim at rest with 2^renderRes + 1 path points (straight, unit scale, twisted and tapered as
    /// the simulation's sections are): the same faces, vertex counts and indices as <see cref="BuildFlexi"/> with that
    /// many points.</summary>
    public static VolumeFace[] BuildFlexiRest(VolumeParams p, float detail, int renderRes)
    {
        int n = (1 << Math.Clamp(renderRes, 0, 3)) + 1;
        var (bx, by) = p.BeginScale;
        var (ex, ey) = p.EndScale;
        Span<FlexiPathPoint> pts = stackalloc FlexiPathPoint[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / (n - 1);
            pts[i] = new FlexiPathPoint
            {
                Pos = new Vector3(0, 0, t - 0.5f),
                Rot = LLMath.AxisAngle(LLMath.Lerp(LLMath.Pi * p.TwistBegin, LLMath.Pi * p.TwistEnd, t), 0, 0, 1),
                ScaleX = LLMath.Lerp(bx, ex, t), ScaleY = LLMath.Lerp(by, ey, t), TexT = t,
            };
        }
        return BuildFlexi(p, detail, pts);
    }

    static VolumeMesh Sweep(VolumeParams p, VolumePath path, VolumeProfile profile, Vector3[]? reuse = null)
    {
        int sizeS = path.Points.Count;
        int sizeT = profile.Points.Count;
        var mesh = reuse?.Length == sizeS * sizeT ? reuse : new Vector3[sizeS * sizeT];
        int dst = 0;
        for (int s = 0; s < sizeS; s++)
        {
            PathPoint pp = path.Points[s];
            // rows of scale * rot (scale z is 0, so the profile's s coordinate drops out)
            Vector3 r0 = pp.Rot0 * pp.ScaleX;
            Vector3 r1 = pp.Rot1 * pp.ScaleY;
            Vector3 offset = pp.Pos;
            if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || !float.IsFinite(offset.Z))
                offset = Vector3.Zero; // MAINT-5660
            for (int t = 0; t < sizeT; t++)
            {
                Vector3 prof = profile.Points[t];
                mesh[dst++] = prof.X * r0 + prof.Y * r1 + offset;
            }
        }
        return new VolumeMesh { Params = p, Path = path, Profile = profile, Mesh = mesh };
    }

    // ---- sculpts ----

    // sculpt_sides: detail is one of 1, 1.5, 2.5, 4.
    private static int SculptSides(float detail)
    {
        if (detail <= 1.0) return 6; // 6 looks round whereas 4 looks square
        if (detail <= 2.0) return 8;
        if (detail <= 3.0) return 16;
        return 32;
    }

    // sculpt_calc_mesh_resolution: keep the map's aspect ratio, capped by LOD and by the map (one vertex per 4 px).
    private static void SculptCalcMeshResolution(int width, int height, float detail, out int s, out int t)
    {
        int maxVerticesLod = (int)Math.Pow(SculptSides(detail), 2.0);
        int maxVerticesMap = width * height / 4;
        int vertices = maxVerticesMap > 0 ? Math.Min(maxVerticesLod, maxVerticesMap) : maxVerticesLod;
        float ratio = (width == 0 || height == 0) ? 1f : (float)width / height;
        s = (int)MathF.Sqrt(vertices / ratio);
        s = Math.Max(s, 4);
        t = vertices / s;
        t = Math.Max(t, 4);
        s = vertices / t;
    }

    private static void SculptGenerateMapVertices(VolumeMesh v, int width, int height, int components, bool rowsTopDown,
        ReadOnlySpan<byte> data, byte sculptType)
    {
        int stitching = sculptType & VolumeConsts.SculptTypeMask;
        bool invert = (sculptType & VolumeConsts.SculptFlagInvert) != 0;
        bool mirror = (sculptType & VolumeConsts.SculptFlagMirror) != 0;
        bool reverseHorizontal = invert ? !mirror : mirror;

        int sizeS = v.Path.Points.Count;
        int sizeT = v.Profile.Points.Count;
        // The viewer's /fp:fast build turns the inner loop's division into a multiply by the reciprocal (the outer
        // one stays a division); that moves some samples by a pixel, e.g. whether the last column reaches `width`
        // and wraps. Checked against the viewer's compiled llmath over 40 random map sizes x 4 LODs.
        float invT = 1f / (sizeT - 1);
        int line = 0;
        for (int s = 0; s < sizeS; s++)
        {
            for (int t = 0; t < sizeT; t++)
            {
                int reversedT = reverseHorizontal ? sizeT - t - 1 : t;
                uint x = (uint)(reversedT * invT * width);
                uint y = (uint)((float)s / (sizeS - 1) * height);

                if (y == 0 && stitching == VolumeConsts.SculptTypeSphere)
                    x = (uint)(width / 2); // top row: pinch

                if (y == height)
                {
                    y = stitching == VolumeConsts.SculptTypeTorus ? 0u : (uint)(height - 1); // bottom row: wrap or clamp
                    if (stitching == VolumeConsts.SculptTypeSphere)
                        x = (uint)(width / 2);
                }

                if (x == width)
                {
                    // side stitching: wrap or clamp
                    x = stitching is VolumeConsts.SculptTypeSphere or VolumeConsts.SculptTypeTorus or VolumeConsts.SculptTypeCylinder
                        ? 0u : (uint)(width - 1);
                }

                uint row = rowsTopDown ? (uint)(height - 1) - y : y; // viewer raw images are bottom-up
                int index = (int)((x + row * (uint)width) * (uint)components);
                var pt = new Vector3(data[index], data[index + 1], data[index + 2]) * (1f / 255f) - new Vector3(0.5f);
                if (mirror)
                    pt.X = -pt.X;
                v.Mesh[t + line] = pt;
            }
            line += sizeT;
        }
    }

    private static float SculptGetSurfaceArea(VolumeMesh v)
    {
        float area = 0;
        int sizeS = v.Path.Points.Count;
        int sizeT = v.Profile.Points.Count;
        Vector3[] m = v.Mesh;
        for (int s = 0; s < sizeS - 1; s++)
        {
            for (int t = 0; t < sizeT - 1; t++)
            {
                Vector3 p1 = m[s * sizeT + t];
                Vector3 p2 = m[(s + 1) * sizeT + t];
                Vector3 p3 = m[s * sizeT + t + 1];
                Vector3 p4 = m[(s + 1) * sizeT + t + 1];
                Vector3 cross1 = Vector3.Cross(p1 - p2, p1 - p3);
                Vector3 cross2 = Vector3.Cross(p4 - p2, p4 - p3);
                area += (cross1.Length() + cross2.Length()) / 2f;
            }
        }
        return area;
    }

    private static void SculptGenerateSpherePlaceholder(VolumeMesh v)
    {
        int sizeS = v.Path.Points.Count;
        int sizeT = v.Profile.Points.Count;
        const double RADIUS = 0.3f;
        int line = 0;
        for (int s = 0; s < sizeS; s++)
        {
            for (int t = 0; t < sizeT; t++)
            {
                float u = (float)s / (sizeS - 1);
                float w = (float)t / (sizeT - 1);
                v.Mesh[t + line] = new Vector3(
                    (float)(Math.Sin(Math.PI * w) * Math.Cos(2.0 * Math.PI * u) * RADIUS),
                    (float)(Math.Sin(Math.PI * w) * Math.Sin(2.0 * Math.PI * u) * RADIUS),
                    (float)(Math.Cos(Math.PI * w) * RADIUS));
            }
            line += sizeT;
        }
    }

    // validate_sculpt_geometry: reject maps whose triangle area dwarfs their bounding surface.
    private static bool ValidateSculptGeometry(VolumeFace[] faces)
    {
        foreach (VolumeFace face in faces)
        {
            if (face.Positions.Length < 3 || face.Indices.Length < 3)
                continue;
            Vector3 extent = face.Max - face.Min;
            float boundingSurface = 2 * (extent.X * extent.Y + extent.X * extent.Z + extent.Y * extent.Z);
            if (!float.IsFinite(boundingSurface) || boundingSurface <= LLMath.FltEpsilon)
                return false;

            float total = 0f;
            for (int i = 0; i + 2 < face.Indices.Length; i += 3)
            {
                Vector3 v0 = face.Positions[face.Indices[i]];
                Vector3 v1 = face.Positions[face.Indices[i + 1]];
                Vector3 v2 = face.Positions[face.Indices[i + 2]];
                total += Vector3.Cross(v1 - v0, v2 - v0).Length() * 0.5f;
            }
            if (!float.IsFinite(total))
                return false;
            if (total / boundingSurface > 16.0f) // MAX_AREA_TO_AREA_RATIO
                return false;
        }
        return true;
    }

    // Which discard level of the sculpt map's JPEG2000 stream the viewer ends up holding: it asks for a
    // 256x256 "known draw size" (LLVOVolume::updateSculptTexture), so maps over 256 px on *both* sides are
    // fetched reduced until one side reaches 256; others at full resolution. Decode at this reduction level.
    public static int SculptDiscardLevel(int fullWidth, int fullHeight)
    {
        const int known = 256;
        if (fullWidth <= known || fullHeight <= known)
            return 0;
        int d = (int)Math.Min(Math.Log((float)fullWidth / known) / Math.Log(2), Math.Log((float)fullHeight / known) / Math.Log(2));
        return Math.Max(d, 0);
    }
}
