// Ported from the Second Life viewer (indra/llmath: llmath.h, llquaternion.cpp, llvector4a.inl).
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sojourn.Render.Geometry;

// Small helpers that reproduce the viewer's float behaviour where it can change results
// (std::lerp, llfloor, ll_round, LL quaternion order and matrix layout).
internal static class LLMath
{
    public const float Pi = 3.1415926535f; // F_PI
    public const float TwoPi = 2f * Pi;
    public const float ApproximatelyZero = 0.00001f; // F_APPROXIMATELY_ZERO
    public const float FltEpsilon = 1.192092896e-07F;

    // std::lerp as implemented by the C++ standard libraries (exact at both ends, monotonic).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t)
    {
        if ((a <= 0f && b >= 0f) || (a >= 0f && b <= 0f))
            return t * b + (1f - t) * a;
        if (t == 1f)
            return b;
        float x = a + t * (b - a);
        if ((t > 1f) == (b > a))
            return b < x ? x : b;
        return x < b ? x : b;
    }

    // LLVector4a::setLerp: lhs + (rhs - lhs) * c
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 LerpV(Vector3 a, Vector3 b, float c) => a + (b - a) * c;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Floor(float f) => (int)MathF.Floor(f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Ceil(float f) => (int)MathF.Ceiling(f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Round(float f) => Floor(f + 0.5f);

    // ll_round(val, nearest)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Round(float val, float nearest) => (float)Math.Floor(val * (1f / nearest) + 0.5f) * nearest;

    // LLQuaternion::setQuat(angle, x, y, z)
    public static Quaternion AxisAngle(float angle, float x, float y, float z)
    {
        float mag = MathF.Sqrt(x * x + y * y + z * z);
        if (mag <= 0.0000001f)
            return Quaternion.Identity;
        angle *= 0.5f;
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle) / mag;
        return new Quaternion(x * s, y * s, z * s, c);
    }

    // LLQuaternion operator*(a, b): "rotate by a, then by b" (Hamilton b*a).
    public static Quaternion Mul(Quaternion a, Quaternion b) => new(
        b.W * a.X + b.X * a.W + b.Y * a.Z - b.Z * a.Y,
        b.W * a.Y + b.Y * a.W + b.Z * a.X - b.X * a.Z,
        b.W * a.Z + b.Z * a.W + b.X * a.Y - b.Y * a.X,
        b.W * a.W - b.X * a.X - b.Y * a.Y - b.Z * a.Z);

    // LLQuaternion::getMatrix3 rows; the viewer uses row vectors (v' = v * M).
    public static void RotationRows(Quaternion q, out Vector3 r0, out Vector3 r1, out Vector3 r2)
    {
        float xx = q.X * q.X, xy = q.X * q.Y, xz = q.X * q.Z, xw = q.X * q.W;
        float yy = q.Y * q.Y, yz = q.Y * q.Z, yw = q.Y * q.W;
        float zz = q.Z * q.Z, zw = q.Z * q.W;
        r0 = new Vector3(1f - 2f * (yy + zz), 2f * (xy + zw), 2f * (xz - yw));
        r1 = new Vector3(2f * (xy - zw), 1f - 2f * (xx + zz), 2f * (yz + xw));
        r2 = new Vector3(2f * (xz + yw), 2f * (yz - xw), 1f - 2f * (xx + yy));
    }

    // The viewer uses normalize3fast (rsqrt estimate, ~12 bits); exact normalisation differs by ~1e-4.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Normalize(Vector3 v)
    {
        float l = v.Length();
        return l > 0f ? v / l : v;
    }
}
