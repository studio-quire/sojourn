// Ported from the Second Life viewer: indra/llmath/llvolume.cpp (LLVolume::createVolumeFaces,
// LLVolumeFace::create/createSide/createCap/createUnCutCubeCap/createTangents, LLCalculateTangentArray).
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
using System.Numerics;

namespace Sojourn.Render.Geometry;

// The generated volume before faces are cut out of it (LLVolume's mPathp/mProfilep/mMesh).
internal sealed class VolumeMesh
{
    public required VolumeParams Params;
    public required VolumePath Path;
    public required VolumeProfile Profile;
    public Vector3[] Mesh = Array.Empty<Vector3>(); // sizeS (path) rows of sizeT (profile) points
}

internal static class VolumeFaceBuilder
{
    private struct FaceSetup
    {
        public int BeginS, NumS, BeginT, NumT;
        public VolumeFaceType Type;
        public ushort FaceId;
    }

    // LLVolume::createVolumeFaces + LLVolumeFace::create. reuse: faces made from a volume of the same shape and size
    // before (a flexible prim's last step), whose arrays are filled again rather than made anew.
    public static VolumeFace[] CreateVolumeFaces(VolumeMesh v, VolumeFace[]? reuse = null)
    {
        if (reuse != null && reuse.Length != v.Profile.Faces.Count) reuse = null;
        var faces = reuse ?? new VolumeFace[v.Profile.Faces.Count];
        for (int i = 0; i < faces.Length; i++)
        {
            ProfileFace face = v.Profile.Faces[i];
            var fs = new FaceSetup
            {
                BeginS = face.Index,
                NumS = face.Count,
                BeginT = 0,
                NumT = v.Path.Points.Count,
                FaceId = face.FaceId,
            };
            if (fs.NumS < 0)
                throw new InvalidOperationException("Volume face corruption detected.");

            if (v.Params.ProfileHollow > 0)
                fs.Type |= VolumeFaceType.Hollow;
            if (v.Profile.Open)
                fs.Type |= VolumeFaceType.Open;
            if (face.Cap)
            {
                fs.Type |= VolumeFaceType.Cap;
                fs.Type |= face.FaceId == VolumeConsts.FacePathBegin ? VolumeFaceType.Top : VolumeFaceType.Bottom;
            }
            else if ((face.FaceId & (VolumeConsts.FaceProfileBegin | VolumeConsts.FaceProfileEnd)) != 0)
            {
                fs.Type |= VolumeFaceType.Flat | VolumeFaceType.End;
            }
            else
            {
                fs.Type |= VolumeFaceType.Side;
                if (face.Flat)
                    fs.Type |= VolumeFaceType.Flat;
                if ((face.FaceId & VolumeConsts.FaceInnerSide) != 0)
                {
                    fs.Type |= VolumeFaceType.Inner;
                    if (face.Flat && fs.NumS > 2)
                        fs.NumS *= 2; // flat inner faces have to copy vert normals
                }
                else
                {
                    fs.Type |= VolumeFaceType.Outer;
                }
            }

            var vf = reuse?[i] ?? new VolumeFace();
            (vf.TeIndex, vf.FaceId, vf.Type) = (i, face.FaceId, fs.Type);
            if ((fs.Type & VolumeFaceType.Cap) != 0)
                CreateCap(v, ref fs, vf);
            else
                CreateSide(v, ref fs, vf);
            CreateTangents(vf);
            faces[i] = vf;
        }
        return faces;
    }

    private static void LerpPlanarVertex(Vector3 p0, Vector2 t0, Vector3 p1, Vector2 t1, Vector3 p2, Vector2 t2,
        float coef01, float coef02, out Vector3 pos, out Vector2 tc)
    {
        Vector3 lhs = (p1 - p0) * coef01;
        Vector3 rhs = (p2 - p0) * coef02;
        pos = rhs + lhs + p0;
        tc = t0 + ((t1 - t0) * coef01) + ((t2 - t0) * coef02);
    }

    private static void CreateUnCutCubeCap(VolumeMesh v, ref FaceSetup fs, VolumeFace vf)
    {
        Vector3[] mesh = v.Mesh;
        List<Vector3> profile = v.Profile.Points;
        int maxS = v.Profile.Total;
        int maxT = v.Path.Points.Count;
        int gridSize = (profile.Count - 1) / 4;
        bool top = (fs.Type & VolumeFaceType.Top) != 0;

        int offset = top ? (maxT - 1) * maxS : fs.BeginS;

        Span<Vector3> cornerPos = stackalloc Vector3[4];
        Span<Vector2> cornerTc = stackalloc Vector2[4];
        for (int t = 0; t < 4; t++)
        {
            cornerPos[t] = mesh[offset + gridSize * t];
            cornerTc[t] = new Vector2(profile[gridSize * t].X + 0.5f, 0.5f - profile[gridSize * t].Y);
        }

        Vector3 baseNormal = LLMath.Normalize(Vector3.Cross(cornerPos[1] - cornerPos[0], cornerPos[2] - cornerPos[1]));
        if (!top)
        {
            baseNormal *= -1.0f;
        }
        else
        {
            // Swap the UVs on the U(X) axis for top face
            (cornerTc[0], cornerTc[3]) = (cornerTc[3], cornerTc[0]);
            (cornerTc[1], cornerTc[2]) = (cornerTc[2], cornerTc[1]);
        }

        int size = (gridSize + 1) * (gridSize + 1);
        var pos = Fresh(vf.Positions, size);
        var norm = Fresh(vf.Normals, size);
        var tc = Fresh(vf.TexCoords, size);
        Vector3 min = default, max = default;
        int k = 0;
        for (int gx = 0; gx < gridSize + 1; gx++)
        {
            for (int gy = 0; gy < gridSize + 1; gy++)
            {
                LerpPlanarVertex(cornerPos[0], cornerTc[0], cornerPos[1], cornerTc[1], cornerPos[3], cornerTc[3],
                    (float)gx / gridSize, (float)gy / gridSize, out Vector3 p, out Vector2 uv);
                pos[k] = p;
                norm[k] = baseNormal;
                tc[k] = uv;
                k++;
                if (gx == 0 && gy == 0)
                {
                    min = max = p;
                }
                else
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
        }

        var indices = Fresh(vf.Indices, gridSize * gridSize * 6);
        int o = 0;
        Span<int> idxs = stackalloc int[] { 0, 1, (gridSize + 1) + 1, (gridSize + 1) + 1, gridSize + 1, 0 };
        for (int gx = 0; gx < gridSize; gx++)
        {
            for (int gy = 0; gy < gridSize; gy++)
            {
                if (top)
                {
                    for (int i = 5; i >= 0; i--)
                        indices[o++] = (uint)((gy * (gridSize + 1)) + gx + idxs[i]);
                }
                else
                {
                    for (int i = 0; i < 6; i++)
                        indices[o++] = (uint)((gy * (gridSize + 1)) + gx + idxs[i]);
                }
            }
        }

        vf.Positions = pos;
        vf.Normals = norm;
        vf.TexCoords = tc;
        vf.Indices = indices;
        vf.Min = min;
        vf.Max = max;
        vf.Center = (min + max) * 0.5f;
    }

    // Signed double area of triangle (a, b, c) in the profile plane, as the cap triangulation computes it.
    private static float Area(Vector3 a, Vector3 b, Vector3 c) =>
        (a.X * b.Y - b.X * a.Y) + (b.X * c.Y - c.X * b.Y) + (c.X * a.Y - a.X * c.Y);

    private static void CreateCap(VolumeMesh v, ref FaceSetup fs, VolumeFace vf)
    {
        VolumeParams p = v.Params;
        bool hollow = (fs.Type & VolumeFaceType.Hollow) != 0;
        bool open = (fs.Type & VolumeFaceType.Open) != 0;
        bool top = (fs.Type & VolumeFaceType.Top) != 0;

        if (!hollow && !open && p.PathBegin == 0.0f && p.PathEnd == 1.0f &&
            p.ProfileCurve == VolumeConsts.ProfileSquare && p.PathCurve == VolumeConsts.PathLine)
        {
            CreateUnCutCubeCap(v, ref fs, vf);
            return;
        }

        Vector3[] mesh = v.Mesh;
        List<Vector3> profile = v.Profile.Points;

        // All types of caps have the same number of vertices and indices
        int numVertices = profile.Count;
        int numIndices = (profile.Count - 2) * 3;
        bool addCenter = !hollow && !open;

        var pos = Fresh(vf.Positions, addCenter ? numVertices + 1 : numVertices);
        var tc = Fresh(vf.TexCoords, pos.Length);
        var norm = Fresh(vf.Normals, pos.Length);
        var indices = Fresh(vf.Indices, addCenter ? numIndices + 3 : numIndices);

        int maxS = v.Profile.Total;
        int maxT = v.Path.Points.Count;
        int offset = top ? (maxT - 1) * maxS : fs.BeginS;

        Vector3 min = mesh[offset], max = min;
        Vector2 minUv, maxUv;
        if (top)
        {
            minUv = maxUv = new Vector2(profile[0].X + 0.5f, profile[0].Y + 0.5f);
            for (int i = 0; i < numVertices; i++)
            {
                tc[i] = new Vector2(profile[i].X + 0.5f, profile[i].Y + 0.5f);
                Vector3 src = mesh[offset + i];
                min = Vector3.Min(min, src);
                max = Vector3.Max(max, src);
                minUv = Vector2.Min(minUv, tc[i]);
                maxUv = Vector2.Max(maxUv, tc[i]);
                pos[i] = src;
            }
        }
        else
        {
            minUv = maxUv = new Vector2(profile[0].X + 0.5f, 0.5f - profile[0].Y);
            for (int i = 0; i < numVertices; i++)
            {
                // Mirror for underside.
                tc[i] = new Vector2(profile[i].X + 0.5f, 0.5f - profile[i].Y);
                Vector3 src = mesh[offset + i];
                min = Vector3.Min(min, src);
                max = Vector3.Max(max, src);
                minUv = Vector2.Min(minUv, tc[i]);
                maxUv = Vector2.Max(maxUv, tc[i]);
                pos[i] = src;
            }
        }

        Vector3 center = (min + max) * 0.5f;
        Vector2 cuv = (minUv + maxUv) * 0.5f;

        if (addCenter)
        {
            pos[numVertices] = center;
            tc[numVertices] = cuv;
            numVertices++;
        }

        if (hollow)
        {
            // Walk the outer and (reversed) inner rings towards each other, picking the better triangle each step.
            // Uses the un-transformed profile points.
            int pt1 = 0, pt2 = numVertices - 1;
            int i = 0;
            while (pt2 - pt1 > 1)
            {
                Vector3 p1 = profile[pt1], p2 = profile[pt2], pa = profile[pt1 + 1], pb = profile[pt2 - 1];

                float area1a2 = Area(p1, pa, p2);
                float area1ba = Area(p1, pb, pa);
                float area21b = Area(p2, p1, pb);
                float area2ab = Area(p2, pa, pb);

                bool tri1a2 = true, tri21b = true;
                if (area1a2 < 0) tri1a2 = false;
                if (area2ab < 0) tri1a2 = false; // contains point b
                if (area21b < 0) tri21b = false;
                if (area1ba < 0) tri21b = false; // contains point b

                bool useTri1a2;
                if (!tri1a2)
                    useTri1a2 = false;
                else if (!tri21b)
                    useTri1a2 = true;
                else
                {
                    Vector3 d1 = p1 - pa;
                    Vector3 d2 = p2 - pb;
                    // LLVector4a::dot3 includes the s coordinate (z) of the profile points.
                    useTri1a2 = Vector3.Dot(d1, d1) < Vector3.Dot(d2, d2);
                }

                if (top)
                {
                    if (useTri1a2)
                    {
                        indices[i++] = (uint)pt1;
                        indices[i++] = (uint)(pt1 + 1);
                        indices[i++] = (uint)pt2;
                        pt1++;
                    }
                    else
                    {
                        indices[i++] = (uint)pt1;
                        indices[i++] = (uint)(pt2 - 1);
                        indices[i++] = (uint)pt2;
                        pt2--;
                    }
                }
                else
                {
                    // Flipped backfacing from top
                    if (useTri1a2)
                    {
                        indices[i++] = (uint)pt1;
                        indices[i++] = (uint)pt2;
                        indices[i++] = (uint)(pt1 + 1);
                        pt1++;
                    }
                    else
                    {
                        indices[i++] = (uint)pt1;
                        indices[i++] = (uint)pt2;
                        indices[i++] = (uint)(pt2 - 1);
                        pt2--;
                    }
                }
            }
        }
        else
        {
            // Not hollow, triangle fan around the last vertex (added centre, or the open profile's centre point).
            int v1 = 2, v2 = 1;
            if (top)
            {
                v1 = 1;
                v2 = 2;
            }
            for (int i = 0; i < numVertices - 2; i++)
            {
                indices[3 * i] = (uint)(numVertices - 1);
                indices[3 * i + v1] = (uint)i;
                indices[3 * i + v2] = (uint)(i + 1);
            }
        }

        // Flat face: one normal from the first triangle.
        Vector3 d0 = pos[indices[1]] - pos[indices[0]];
        Vector3 dd1 = pos[indices[2]] - pos[indices[0]];
        Vector3 normal = Vector3.Cross(d0, dd1);
        if (Vector3.Dot(normal, normal) > LLMath.ApproximatelyZero)
            normal = LLMath.Normalize(normal);
        else
            normal = normal.Z >= 0 ? new Vector3(0, 0, 1) : new Vector3(0, 0, -1); // degenerate

        for (int i = 0; i < numVertices; i++)
            norm[i] = normal;

        vf.Positions = pos;
        vf.Normals = norm;
        vf.TexCoords = tc;
        vf.Indices = indices;
        vf.Min = min;
        vf.Max = max;
        vf.Center = center;
    }

    private static void CreateSide(VolumeMesh v, ref FaceSetup fs, VolumeFace vf)
    {
        bool flat = (fs.Type & VolumeFaceType.Flat) != 0;

        byte sculptType = v.Params.SculptType;
        int sculptStitching = sculptType & VolumeConsts.SculptTypeMask;
        bool sculptInvert = (sculptType & VolumeConsts.SculptFlagInvert) != 0;
        bool sculptMirror = (sculptType & VolumeConsts.SculptFlagMirror) != 0;
        bool sculptReverseHorizontal = sculptInvert ? !sculptMirror : sculptMirror; // XOR

        Vector3[] mesh = v.Mesh;
        List<Vector3> profile = v.Profile.Points;
        List<PathPoint> path = v.Path.Points;
        int maxS = v.Profile.Total;
        int numS = fs.NumS, numT = fs.NumT;

        int numVertices = numS * numT;
        int numIndices = (numS - 1) * (numT - 1) * 6;

        var pos = Fresh(vf.Positions, numVertices);
        var tc = Fresh(vf.TexCoords, numVertices);
        float beginStex = MathF.Floor(profile[fs.BeginS].Z);
        bool innerFlatDup = (fs.Type & VolumeFaceType.Inner) != 0 && (fs.Type & VolumeFaceType.Flat) != 0 && numS > 2;
        int sCount = innerFlatDup ? numS / 2 : numS;
        bool endFace = (fs.Type & VolumeFaceType.End) != 0;

        int cur = 0;
        int endT = fs.BeginT + numT;
        for (int t = fs.BeginT; t < endT; t++)
        {
            float tt = path[t].TexT;
            for (int s = 0; s < sCount; s++)
            {
                float ss;
                if (endFace)
                {
                    ss = s != 0 ? 1f : 0f;
                }
                else
                {
                    int index = fs.BeginS + s;
                    if (index >= profile.Count)
                        ss = flat ? 1f - beginStex : 1f; // edge
                    else if (!flat)
                        ss = profile[index].Z;
                    else
                        ss = profile[index].Z - beginStex;
                }

                if (sculptReverseHorizontal)
                    ss = 1f - ss;

                // Wrap around the profile ring?
                int i = fs.BeginS + s >= maxS ? fs.BeginS + s + maxS * (t - 1) : fs.BeginS + s + maxS * t;

                pos[cur] = mesh[i];
                tc[cur] = new Vector2(ss, tt);
                cur++;

                if (innerFlatDup && s > 0)
                {
                    pos[cur] = mesh[i];
                    tc[cur] = new Vector2(ss, tt);
                    cur++;
                }
            }

            if (innerFlatDup)
            {
                int s = (fs.Type & VolumeFaceType.Open) != 0 ? sCount - 1 : 0;
                int i = fs.BeginS + s + maxS * t;
                float ss = profile[fs.BeginS + s].Z - beginStex;
                pos[cur] = mesh[i];
                tc[cur] = new Vector2(ss, tt);
                cur++;
            }
        }

        Vector3 faceMin = pos[0], faceMax = pos[0];
        for (int k = 1; k < numVertices; k++)
        {
            faceMin = Vector3.Min(faceMin, pos[k]);
            faceMax = Vector3.Max(faceMax, pos[k]);
        }

        var indices = Fresh(vf.Indices, numIndices);
        int ci = 0;
        for (int t = 0; t < numT - 1; t++)
        {
            for (int s = 0; s < numS - 1; s++)
            {
                indices[ci++] = (uint)(s + numS * t);           // bottom left
                indices[ci++] = (uint)(s + 1 + numS * (t + 1)); // top right
                indices[ci++] = (uint)(s + numS * (t + 1));     // top left
                indices[ci++] = (uint)(s + numS * t);           // bottom left
                indices[ci++] = (uint)(s + 1 + numS * t);       // bottom right
                indices[ci++] = (uint)(s + 1 + numS * (t + 1)); // top right
            }
        }

        // Smooth normals: sum of unnormalised triangle normals, with every other triangle's second/third
        // vertex counted twice to even out quad contributions. Left unnormalised here like the viewer.
        var norm = Fresh(vf.Normals, numVertices);
        int triCount = numIndices / 3;
        for (int k = 0; k < triCount; k++)
        {
            uint i0 = indices[k * 3], i1 = indices[k * 3 + 1], i2 = indices[k * 3 + 2];
            Vector3 b = pos[i0];
            Vector3 c = Vector3.Cross(b - pos[i1], b - pos[i2]);
            norm[i0] += c;
            norm[i1] += c;
            norm[i2] += c;
            if (k % 2 == 0)
                norm[i1] += c;
            else
                norm[i2] += c;
        }

        // Adjust normals based on wrapping and stitching.
        // (Row numT-2, not numT-1: with a two-row path both tests compare a point with itself.)
        Vector3 top = pos[0] - pos[numS * (numT - 2)];
        bool sBottomConverges = Vector3.Dot(top, top) < 0.000001f;
        top = pos[numS - 1] - pos[numS * (numT - 2) + numS - 1];
        bool sTopConverges = Vector3.Dot(top, top) < 0.000001f;

        if (sculptStitching == VolumeConsts.SculptTypeNone)
        {
            if (!v.Path.Open)
            {
                // wrap normals on T
                for (int i = 0; i < numS; i++)
                {
                    Vector3 n = norm[i] + norm[numS * (numT - 1) + i];
                    norm[i] = n;
                    norm[numS * (numT - 1) + i] = n;
                }
            }

            if (!v.Profile.Open && !sBottomConverges)
            {
                // wrap normals on S
                for (int i = 0; i < numT; i++)
                {
                    Vector3 n = norm[numS * i] + norm[numS * i + numS - 1];
                    norm[numS * i] = n;
                    norm[numS * i + numS - 1] = n;
                }
            }

            if (v.Params.PathCurve == VolumeConsts.PathCircle &&
                (v.Params.ProfileCurve & VolumeConsts.ProfileMask) == VolumeConsts.ProfileCircleHalf)
            {
                if (sBottomConverges)
                    for (int i = 0; i < numT; i++)
                        norm[numS * i] = new Vector3(1, 0, 0);
                if (sTopConverges)
                    for (int i = 0; i < numT; i++)
                        norm[numS * i + numS - 1] = new Vector3(-1, 0, 0);
            }
        }
        else
        {
            bool averagePoles = sculptStitching == VolumeConsts.SculptTypeSphere;
            bool wrapS = sculptStitching == VolumeConsts.SculptTypeSphere || sculptStitching == VolumeConsts.SculptTypeTorus ||
                         sculptStitching == VolumeConsts.SculptTypeCylinder;
            bool wrapT = sculptStitching == VolumeConsts.SculptTypeTorus;

            if (averagePoles)
            {
                Vector3 average = Vector3.Zero;
                for (int i = 0; i < numS; i++) average += norm[i];
                for (int i = 0; i < numS; i++) norm[i] = average;

                average = Vector3.Zero;
                for (int i = 0; i < numS; i++) average += norm[i + numS * (numT - 1)];
                for (int i = 0; i < numS; i++) norm[i + numS * (numT - 1)] = average;
            }

            if (wrapS)
            {
                for (int i = 0; i < numT; i++)
                {
                    Vector3 n = norm[numS * i] + norm[numS * i + numS - 1];
                    norm[numS * i] = n;
                    norm[numS * i + numS - 1] = n;
                }
            }

            if (wrapT)
            {
                for (int i = 0; i < numS; i++)
                {
                    Vector3 n = norm[i] + norm[numS * (numT - 1) + i];
                    norm[i] = n;
                    norm[numS * (numT - 1) + i] = n;
                }
            }
        }

        vf.Positions = pos;
        vf.Normals = norm;
        vf.TexCoords = tc;
        vf.Indices = indices;
        vf.Min = faceMin;
        vf.Max = faceMax;
        vf.Center = (faceMin + faceMax) * 0.5f;
    }

    // LLVolumeFace::createTangents: tangents from the (still unnormalised, for sides) normals, then normalise
    // the normals. Faithful to the viewer, so side-face tangents aren't exactly orthogonal to the normal.
    public static void CreateTangents(VolumeFace vf)
    {
        int n = vf.Positions.Length;
        var tangents = Fresh(vf.Tangents, n);
        CalculateTangentArray(vf.Positions, vf.Normals, vf.TexCoords, vf.Indices, tangents);
        for (int i = 0; i < n; i++)
            vf.Normals[i] = LLMath.Normalize(vf.Normals[i]);
        vf.Tangents = tangents;
    }

    [ThreadStatic] static Vector3[]? _tan1, _tan2;

    /// <summary>An array of n: the one given, cleared, when it's that size (a face built again), else a new one.</summary>
    static T[] Fresh<T>(T[] old, int n)
    {
        if (old.Length != n || n == 0) return new T[n];
        Array.Clear(old);
        return old;
    }

    // Lengyel, "Computing Tangent Space Basis Vectors for an Arbitrary Mesh" (LLCalculateTangentArray).
    private static void CalculateTangentArray(Vector3[] vertex, Vector3[] normal, Vector2[] texcoord, uint[] indices, Vector4[] tangent)
    {
        int vertexCount = vertex.Length;
        if (_tan1 == null || _tan1.Length < vertexCount) (_tan1, _tan2) = (new Vector3[vertexCount], new Vector3[vertexCount]);
        var (tan1, tan2) = (_tan1, _tan2!);
        Array.Clear(tan1, 0, vertexCount);
        Array.Clear(tan2, 0, vertexCount);

        for (int a = 0; a + 2 < indices.Length; a += 3)
        {
            uint i1 = indices[a], i2 = indices[a + 1], i3 = indices[a + 2];
            Vector3 v1 = vertex[i1], v2 = vertex[i2], v3 = vertex[i3];
            Vector2 w1 = texcoord[i1], w2 = texcoord[i2], w3 = texcoord[i3];

            float x1 = v2.X - v1.X, x2 = v3.X - v1.X;
            float y1 = v2.Y - v1.Y, y2 = v3.Y - v1.Y;
            float z1 = v2.Z - v1.Z, z2 = v3.Z - v1.Z;
            float s1 = w2.X - w1.X, s2 = w3.X - w1.X;
            float t1 = w2.Y - w1.Y, t2 = w3.Y - w1.Y;

            float rd = s1 * t2 - s2 * t1;
            float r = (rd * rd) > LLMath.FltEpsilon ? 1.0f / rd : (rd > 0.0f ? 1024f : -1024f);

            var sdir = new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r);
            var tdir = new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r);

            tan1[i1] += sdir; tan1[i2] += sdir; tan1[i3] += sdir;
            tan2[i1] += tdir; tan2[i2] += tdir; tan2[i3] += tdir;
        }

        for (int a = 0; a < vertexCount; a++)
        {
            Vector3 nrm = normal[a];
            Vector3 t = tan1[a];
            Vector3 ncrosst = Vector3.Cross(nrm, t);
            // Gram-Schmidt orthogonalize
            Vector3 tsubn = t - nrm * Vector3.Dot(nrm, t);
            if (Vector3.Dot(tsubn, tsubn) > LLMath.ApproximatelyZero)
            {
                tsubn = LLMath.Normalize(tsubn);
                float handedness = Vector3.Dot(ncrosst, tan2[a]) < 0f ? -1f : 1f;
                tangent[a] = new Vector4(tsubn, handedness);
            }
            else
            {
                tangent[a] = new Vector4(0, 0, 1, 1); // degenerate, make up a value
            }
        }
    }
}
