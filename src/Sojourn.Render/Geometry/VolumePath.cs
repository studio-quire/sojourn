// Ported from the Second Life viewer: indra/llmath/llvolume.cpp (LLPath::genNGon, LLPath::generate).
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
using System.Numerics;

namespace Sojourn.Render.Geometry;

internal struct PathPoint
{
    public Vector3 Pos;
    // Rows of LLMatrix3(rotation); positions use row vectors (v * M).
    public Vector3 Rot0, Rot1, Rot2;
    public float ScaleX, ScaleY;
    public float TexT;
}

/// <summary>One point of a flexible prim's path (LLPath::PathPt as doFlexibleUpdate writes it): position in the prim's
/// frame without its scale (metres), rotation (System.Numerics, the viewer's quaternion), the section's size in metres.</summary>
public struct FlexiPathPoint
{
    public Vector3 Pos;
    public Quaternion Rot;
    public float ScaleX, ScaleY;
    public float TexT;
}

// LLPath
internal sealed class VolumePath
{
    public readonly List<PathPoint> Points = new();
    public bool Open;
    public float Step;

    private static readonly float[] TableScale = { 1, 1, 1, 0.5f, 0.707107f, 0.53f, 0.525f, 0.5f };

    private static void SetRot(ref PathPoint pt, Quaternion q) => LLMath.RotationRows(q, out pt.Rot0, out pt.Rot1, out pt.Rot2);

    // Circular path, starting at (1, 0, 0), counterclockwise along the xz plane.
    private void GenNGon(VolumeParams p, int sides, float startOff = 0f, float endScale = 1f, float twistScale = 1f)
    {
        float revolutions = p.Revolutions;
        float skew = p.Skew;
        float skewMag = MathF.Abs(skew);
        float holeX = p.ScaleX * (1.0f - skewMag);
        float holeY = p.ScaleY;

        // Taper begin/end (negative means taper the beginning)
        float taperXBegin = 1.0f, taperXEnd = 1.0f - p.TaperX;
        float taperYBegin = 1.0f, taperYEnd = 1.0f - p.TaperY;
        if (taperXEnd > 1.0f)
        {
            taperXBegin = 2.0f - taperXEnd;
            taperXEnd = 1.0f;
        }
        if (taperYEnd > 1.0f)
        {
            taperYBegin = 2.0f - taperYEnd;
            taperYEnd = 1.0f;
        }

        float radiusStart = 0.5f;
        if (sides < 8)
            radiusStart = TableScale[sides];
        radiusStart *= 1.0f - holeY;

        float radiusEnd = radiusStart;
        float radiusOffset = p.RadiusOffset;
        if (radiusOffset < 0f)
            radiusStart *= 1f + radiusOffset;
        else
            radiusEnd *= 1f - radiusOffset;

        Open = (p.PathEnd * endScale - p.PathBegin < 1.0f) ||
               (skewMag > 0.001f) ||
               (MathF.Abs(taperXEnd - taperXBegin) > 0.001f) ||
               (MathF.Abs(taperYEnd - taperYBegin) > 0.001f) ||
               (MathF.Abs(radiusEnd - radiusStart) > 0.001f);

        float twistBegin = p.TwistBegin * twistScale;
        float twistEnd = p.TwistEnd * twistScale;

        void Append(float t)
        {
            float ang = 2.0f * LLMath.Pi * revolutions * t;
            float radius = LLMath.Lerp(radiusStart, radiusEnd, t);
            float c = MathF.Cos(ang) * radius;
            float s = MathF.Sin(ang) * radius;
            var pt = new PathPoint
            {
                Pos = new Vector3(0 + LLMath.Lerp(0f, p.ShearX, s) + LLMath.Lerp(-skew, skew, t) * 0.5f,
                                  c + LLMath.Lerp(0f, p.ShearY, s),
                                  s),
                ScaleX = holeX * LLMath.Lerp(taperXBegin, taperXEnd, t),
                ScaleY = holeY * LLMath.Lerp(taperYBegin, taperYEnd, t),
                TexT = t,
            };
            // Twist rotates the path along the x,y plane; then rotate around the circle's centre.
            Quaternion twist = LLMath.AxisAngle(LLMath.Lerp(twistBegin, twistEnd, t) * 2f * LLMath.Pi - LLMath.Pi, 0, 0, 1);
            Quaternion qang = LLMath.AxisAngle(ang, 1, 0, 0);
            SetRot(ref pt, LLMath.Mul(twist, qang));
            Points.Add(pt);
        }

        // Once before the main loop so the path begins at the correct cut.
        float step = 1.0f / sides;
        float t = p.PathBegin;
        Append(t);
        t += step;

        // Snap to a quantized parameter, so that cut does not affect most sample points.
        t = ((int)(t * sides)) * step; // the viewer is built with /fp:fast, which turns "/ (F32)sides" into "* step"

        while (t < p.PathEnd)
        {
            Append(t);
            t += step;
        }

        // Final pass for the end cut.
        Append(p.PathEnd);
    }

    /// <summary>LLDynamicPath: a flexible prim's path, its points set by the simulation (LLVolumeImplFlexible::
    /// doFlexibleUpdate) rather than generated; always open (end caps drawn).</summary>
    public void SetPoints(ReadOnlySpan<FlexiPathPoint> points)
    {
        Points.Clear();
        Open = true;
        foreach (var fp in points)
        {
            var pt = new PathPoint { Pos = fp.Pos, ScaleX = fp.ScaleX, ScaleY = fp.ScaleY, TexT = fp.TexT };
            SetRot(ref pt, fp.Rot);
            Points.Add(pt);
        }
    }

    public bool Generate(VolumeParams p, float detail, int split, bool isSculpted = false, int sculptSize = 0)
    {
        if (detail < 0) detail = 0; // MIN_LOD

        Points.Clear();
        Open = true;

        switch (p.PathCurve & 0xf0)
        {
            default:
            case VolumeConsts.PathLine:
            {
                // Take the begin/end twist into account for detail.
                int np = LLMath.Floor(MathF.Abs(p.TwistBegin - p.TwistEnd) * 3.5f * (detail - 0.5f)) + 2;
                if (np < split + 2)
                    np = split + 2;

                Step = 1.0f / (np - 1);

                var startScale = p.BeginScale;
                var endScale = p.EndScale;

                for (int i = 0; i < np; i++)
                {
                    float t = LLMath.Lerp(p.PathBegin, p.PathEnd, i * Step);
                    var pt = new PathPoint
                    {
                        Pos = new Vector3(LLMath.Lerp(0f, p.ShearX, t), LLMath.Lerp(0f, p.ShearY, t), t - 0.5f),
                        ScaleX = LLMath.Lerp(startScale.X, endScale.X, t),
                        ScaleY = LLMath.Lerp(startScale.Y, endScale.Y, t),
                        TexT = t,
                    };
                    SetRot(ref pt, LLMath.AxisAngle(LLMath.Lerp(LLMath.Pi * p.TwistBegin, LLMath.Pi * p.TwistEnd, t), 0, 0, 1));
                    Points.Add(pt);
                }
                break;
            }
            case VolumeConsts.PathCircle:
            {
                // Increase the detail as the revolutions and twist increase.
                float twistMag = MathF.Abs(p.TwistBegin - p.TwistEnd);
                int sides = LLMath.Floor(LLMath.Floor(VolumeConsts.MinDetailFaces * detail + twistMag * 3.5f * (detail - 0.5f)) * p.Revolutions);
                if (isSculpted)
                    sides = Math.Max(sculptSize, 1);
                if (0 < sides)
                    GenNGon(p, sides);
                break;
            }
            case VolumeConsts.PathCircle2:
            {
                if (p.PathEnd - p.PathBegin >= 0.99f && p.ScaleX >= .99f)
                    Open = false;

                GenNGon(p, LLMath.Floor(VolumeConsts.MinDetailFaces * detail));

                float toggle = 0.5f;
                for (int i = 0; i < Points.Count; i++)
                {
                    var pt = Points[i];
                    pt.Pos.X = toggle;
                    Points[i] = pt;
                    toggle = toggle == 0.5f ? -0.5f : 0.5f;
                }
                break;
            }
            case VolumeConsts.PathTest:
            {
                const int np = 5;
                Step = 1.0f / (np - 1);
                for (int i = 0; i < np; i++)
                {
                    float t = i * Step;
                    var pt = new PathPoint
                    {
                        Pos = new Vector3(0,
                                          LLMath.Lerp(0f, -MathF.Sin(LLMath.Pi * p.TwistEnd * t) * 0.5f, t),
                                          LLMath.Lerp(-0.5f, MathF.Cos(LLMath.Pi * p.TwistEnd * t) * 0.5f, t)),
                        ScaleX = LLMath.Lerp(1f, p.ScaleX, t),
                        ScaleY = LLMath.Lerp(1f, p.ScaleY, t),
                        TexT = t,
                    };
                    SetRot(ref pt, LLMath.AxisAngle(LLMath.Pi * p.TwistEnd * t, 1, 0, 0));
                    Points.Add(pt);
                }
                break;
            }
        }

        if (p.TwistEnd != p.TwistBegin)
            Open = true;

        return true;
    }
}
