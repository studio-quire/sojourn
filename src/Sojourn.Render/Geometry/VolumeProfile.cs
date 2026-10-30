// Ported from the Second Life viewer: indra/llmath/llvolume.cpp (LLProfile::genNGon, addHole, addCap,
// addFace, generate). Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
using System.Numerics;

namespace Sojourn.Render.Geometry;

internal sealed class ProfileFace
{
    public int Index;
    public int Count;
    public float ScaleU;
    public bool Cap;
    public bool Flat;
    public ushort FaceId;
}

// LLProfile. Points are (x, y, s) where s is the texture coordinate along the profile.
internal sealed class VolumeProfile
{
    public readonly List<Vector3> Points = new();
    public readonly List<ProfileFace> Faces = new();
    public bool Open;
    public bool Concave;
    public int Total;
    public int TotalOut;

    private static readonly float[] TableScale = { 1, 1, 1, 0.5f, 0.707107f, 0.53f, 0.525f, 0.5f };

    private ProfileFace AddCap(ushort faceId)
    {
        var f = new ProfileFace { Index = 0, Count = Total, ScaleU = 1.0f, Cap = true, FaceId = faceId };
        Faces.Add(f);
        return f;
    }

    private ProfileFace AddFace(int i, int count, float scaleU, ushort faceId, bool flat)
    {
        var f = new ProfileFace { Index = i, Count = count, ScaleU = scaleU, Flat = flat, Cap = false, FaceId = faceId };
        Faces.Add(f);
        return f;
    }

    private void GenNGon(VolumeParams p, int sides, float offset = 0f, float bevel = 0f, float angScale = 1f, int split = 0)
    {
        float scale = 0.5f;
        float begin = p.ProfileBegin;
        float end = p.ProfileEnd;

        float tStep = 1.0f / sides;
        float angStep = 2.0f * LLMath.Pi * tStep * angScale;

        int totalSides = LLMath.Round(sides / angScale);
        if (totalSides < 8)
            scale = TableScale[totalSides];

        float tFirst = MathF.Floor(begin * sides) * tStep; // "/ (F32)sides" under the viewer's /fp:fast

        float t = tFirst;
        float ang = 2.0f * LLMath.Pi * (t * angScale + offset);
        var pt1 = new Vector3(MathF.Cos(ang) * scale, MathF.Sin(ang) * scale, t);

        t += tStep;
        ang += angStep;
        var pt2 = new Vector3(MathF.Cos(ang) * scale, MathF.Sin(ang) * scale, t);

        float tFraction = (begin - tFirst) * sides;
        if (tFraction < 0.9999f)
            Points.Add(LLMath.LerpV(pt1, pt2, tFraction));

        while (t < end)
        {
            pt1 = new Vector3(MathF.Cos(ang) * scale, MathF.Sin(ang) * scale, t);
            if (Points.Count > 0)
            {
                Vector3 last = Points[^1];
                for (int i = 0; i < split && Points.Count > 0; i++)
                    Points.Add((pt1 - last) * (1.0f / (split + 1) * (i + 1)) + last);
            }
            Points.Add(pt1);
            t += tStep;
            ang += angStep;
        }

        pt2 = new Vector3(MathF.Cos(ang) * scale, MathF.Sin(ang) * scale, t);
        tFraction = (end - (t - tStep)) * sides;
        if (tFraction > 0.0001f)
        {
            Vector3 newPt = LLMath.LerpV(pt1, pt2, tFraction);
            if (Points.Count > 0)
            {
                Vector3 last = Points[^1];
                for (int i = 0; i < split && Points.Count > 0; i++)
                    Points.Add((newPt - last) * (1.0f / (split + 1) * (i + 1)) + last);
            }
            Points.Add(newPt);
        }

        if ((end - begin) * angScale < 0.99f)
        {
            Concave = (end - begin) * angScale > 0.5f;
            Open = true;
            if (p.ProfileHollow <= 0)
                Points.Add(Vector3.Zero); // centre point if not hollow
        }
        else
        {
            Open = false;
            Concave = false;
        }

        Total = Points.Count;
    }

    // Hollow is relative to the bounding box, not to this profile's geometry.
    private ProfileFace AddHole(VolumeParams p, bool flat, float sides, float offset, float boxHollow, float angScale, int split = 0)
    {
        TotalOut = Total;
        GenNGon(p, LLMath.Floor(sides), offset, -1, angScale, split);
        var face = AddFace(TotalOut, Total - TotalOut, 0, VolumeConsts.FaceInnerSide, flat);

        // Scale (all components, the s coordinate too) and reverse the inner points.
        var pt = new Vector3[Total];
        for (int i = TotalOut; i < Total; i++)
            pt[i] = Points[i] * boxHollow;
        int j = Total - 1;
        for (int i = TotalOut; i < Total; i++)
            Points[i] = pt[j--];

        foreach (var f in Faces)
            if (f.Cap)
                f.Count *= 2;

        return face;
    }

    public bool Generate(VolumeParams p, bool pathOpen, float detail, int split, bool isSculpted = false, int sculptSize = 0)
    {
        if (detail < 0) detail = 0; // MIN_LOD

        Points.Clear();
        Faces.Clear();
        Open = false;
        Concave = false;
        Total = 0;
        TotalOut = 0;

        float begin = p.ProfileBegin;
        float end = p.ProfileEnd;
        float hollow = p.ProfileHollow;

        if (begin > end - 0.01f)
            return false; // "assertion failed (begin >= end)"

        int faceNum = 0;
        int profileType = p.ProfileCurve & VolumeConsts.ProfileMask;
        int holeType = p.ProfileCurve & VolumeConsts.HoleMask;

        switch (profileType)
        {
            case VolumeConsts.ProfileSquare:
            {
                GenNGon(p, 4, -0.375f, 0, 1, split);
                if (pathOpen)
                    AddCap(VolumeConsts.FacePathBegin);

                for (int i = LLMath.Floor(begin * 4f); i < LLMath.Floor(end * 4f + .999f); i++)
                    AddFace(faceNum++ * (split + 1), split + 2, 1, (ushort)(VolumeConsts.FaceOuterSide0 << i), true);

                for (int i = 0; i < Points.Count; i++)
                    Points[i] = Points[i] * new Vector3(1, 1, 4); // scale s by 4 for tex coords

                if (hollow != 0)
                {
                    switch (holeType)
                    {
                        case VolumeConsts.HoleTriangle:
                            AddHole(p, true, 3, -0.375f, hollow, 1f, split);
                            break;
                        case VolumeConsts.HoleCircle:
                            AddHole(p, false, VolumeConsts.MinDetailFaces * detail, -0.375f, hollow, 1f);
                            break;
                        default: // same, square
                            AddHole(p, true, 4, -0.375f, hollow, 1f, split);
                            break;
                    }
                }

                if (pathOpen)
                    Faces[0].Count = Total;
                break;
            }
            case VolumeConsts.ProfileIsoTri:
            case VolumeConsts.ProfileRightTri:
            case VolumeConsts.ProfileEqualTri:
            {
                GenNGon(p, 3, 0, 0, 1, split);
                for (int i = 0; i < Points.Count; i++)
                    Points[i] = Points[i] * new Vector3(1, 1, 3);

                if (pathOpen)
                    AddCap(VolumeConsts.FacePathBegin);

                for (int i = LLMath.Floor(begin * 3f); i < LLMath.Floor(end * 3f + .999f); i++)
                    AddFace(faceNum++ * (split + 1), split + 2, 1, (ushort)(VolumeConsts.FaceOuterSide0 << i), true);

                if (hollow != 0)
                {
                    float triangleHollow = hollow / 2f;
                    switch (holeType)
                    {
                        case VolumeConsts.HoleCircle:
                            AddHole(p, false, VolumeConsts.MinDetailFaces * detail, 0, triangleHollow, 1f);
                            break;
                        case VolumeConsts.HoleSquare:
                            AddHole(p, true, 4, 0, triangleHollow, 1f, split);
                            break;
                        default: // same, triangle
                            AddHole(p, true, 3, 0, triangleHollow, 1f, split);
                            break;
                    }
                }
                break;
            }
            case VolumeConsts.ProfileCircle:
            {
                float circleDetail = VolumeConsts.MinDetailFaces * detail;
                int hole = 0;
                if (hollow != 0)
                {
                    hole = holeType;
                    if (hole == VolumeConsts.HoleSquare)
                        circleDetail = LLMath.Ceil(circleDetail / 4.0f) * 4.0f; // corners line up
                }

                int sides = (int)circleDetail;
                if (isSculpted)
                    sides = sculptSize;

                GenNGon(p, sides);

                if (pathOpen)
                    AddCap(VolumeConsts.FacePathBegin);

                if (Open && hollow == 0)
                    AddFace(0, Total - 1, 0, VolumeConsts.FaceOuterSide0, false);
                else
                    AddFace(0, Total, 0, VolumeConsts.FaceOuterSide0, false);

                if (hollow != 0)
                {
                    switch (hole)
                    {
                        case VolumeConsts.HoleSquare:
                            AddHole(p, true, 4, 0, hollow, 1f, split);
                            break;
                        case VolumeConsts.HoleTriangle:
                            AddHole(p, true, 3, 0, hollow, 1f, split);
                            break;
                        default:
                            AddHole(p, false, circleDetail, 0, hollow, 1f);
                            break;
                    }
                }
                break;
            }
            case VolumeConsts.ProfileCircleHalf:
            {
                float circleDetail = VolumeConsts.MinDetailFaces * detail * 0.5f;
                int hole = 0;
                if (hollow != 0)
                {
                    hole = holeType;
                    if (hole == VolumeConsts.HoleSquare)
                        circleDetail = LLMath.Ceil(circleDetail / 2.0f) * 2.0f;
                }
                GenNGon(p, LLMath.Floor(circleDetail), 0.5f, 0f, 0.5f);
                if (pathOpen)
                    AddCap(VolumeConsts.FacePathBegin);
                if (Open && p.ProfileHollow == 0)
                    AddFace(0, Total - 1, 0, VolumeConsts.FaceOuterSide0, false);
                else
                    AddFace(0, Total, 0, VolumeConsts.FaceOuterSide0, false);

                if (hollow != 0)
                {
                    switch (hole)
                    {
                        case VolumeConsts.HoleSquare:
                            AddHole(p, true, 2, 0.5f, hollow, 0.5f, split);
                            break;
                        case VolumeConsts.HoleTriangle:
                            AddHole(p, true, 3, 0.5f, hollow, 0.5f, split);
                            break;
                        default:
                            AddHole(p, false, circleDetail, 0.5f, hollow, 0.5f);
                            break;
                    }
                }

                // Special case for openness of sphere
                if ((p.ProfileEnd - p.ProfileBegin) < 1f)
                {
                    Open = true;
                }
                else if (hollow == 0)
                {
                    Open = false;
                    Points.Add(Points[0]);
                    Total++;
                }
                break;
            }
            default:
                throw new ArgumentException($"Unknown profile curve 0x{p.ProfileCurve:x2}");
        }

        if (pathOpen)
            AddCap(VolumeConsts.FacePathEnd); // bottom

        if (Open) // interior edge caps
        {
            AddFace(Total - 1, 2, 0.5f, VolumeConsts.FaceProfileBegin, true);
            if (hollow != 0)
                AddFace(TotalOut - 1, 2, 0.5f, VolumeConsts.FaceProfileEnd, true);
            else
                AddFace(Total - 2, 2, 0.5f, VolumeConsts.FaceProfileEnd, true);
        }

        return true;
    }
}
