// Ported from the Second Life viewer: indra/llmath (llquaternion.cpp, m4math.h/.cpp, v3math.h, llmath.h).
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
using System.Runtime.CompilerServices;

namespace Sojourn.Render.Foliage;

/// <summary>
/// The LL math the foliage code leans on, written with the viewer's operation order so results match its float
/// rounding (System.Numerics may use FMA and different sum orders). LL matrices are row-vector (v' = v * M), the
/// same layout as <see cref="Matrix4x4"/>: LL mMatrix[r][c] is M(r+1)(c+1).
/// </summary>
internal static class FoliageMath
{
    public const float Pi = 3.1415926535897932384626433832795f;     // F_PI
    public const float DegToRad = 0.017453292519943295769236907684886f;
    public const float RadToDeg = 57.295779513082320876798154814105f;
    public const float AlmostZero = 0.0001f;                         // F_ALMOST_ZERO
    public const float MagThreshold = 0.0000001f;                    // FP_MAG_THRESHOLD

    // LLQuaternion(angle, LLVector4 vec)
    public static Quaternion AxisAngle(float angle, float x, float y, float z)
    {
        float mag = MathF.Sqrt(x * x + y * y + z * z);
        if (mag <= MagThreshold)
            return Quaternion.Identity;
        angle *= 0.5f;
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle) / mag;
        return new Quaternion(x * s, y * s, z * s, c);
    }

    // LLQuaternion operator*(a, b): rotate by a, then by b (Hamilton b*a).
    public static Quaternion Mul(Quaternion a, Quaternion b) => new(
        b.W * a.X + b.X * a.W + b.Y * a.Z - b.Z * a.Y,
        b.W * a.Y + b.Y * a.W + b.Z * a.X - b.X * a.Z,
        b.W * a.Z + b.Z * a.W + b.X * a.Y - b.Y * a.X,
        b.W * a.W - b.X * a.X - b.Y * a.Y - b.Z * a.Z);

    // LLMatrix4(LLQuaternion): LLQuaternion::getMatrix3 + initMatrix
    public static Matrix4x4 Rotation(Quaternion q)
    {
        float xx = q.X * q.X, xy = q.X * q.Y, xz = q.X * q.Z, xw = q.X * q.W;
        float yy = q.Y * q.Y, yz = q.Y * q.Z, yw = q.Y * q.W;
        float zz = q.Z * q.Z, zw = q.Z * q.W;
        return new Matrix4x4(
            1f - 2f * (yy + zz), 2f * (xy + zw), 2f * (xz - yw), 0f,
            2f * (xy - zw), 1f - 2f * (xx + zz), 2f * (yz + xw), 0f,
            2f * (xz + yw), 2f * (yz - xw), 1f - 2f * (xx + yy), 0f,
            0f, 0f, 0f, 1f);
    }

    public static Matrix4x4 Translation(float x, float y, float z)
    {
        var m = Matrix4x4.Identity;
        m.M41 = x; m.M42 = y; m.M43 = z;
        return m;
    }

    public static Matrix4x4 Scale(float x, float y, float z)
    {
        var m = Matrix4x4.Identity;
        m.M11 = x; m.M22 = y; m.M33 = z;
        return m;
    }

    // operator*=(LLMatrix4& a, const LLMatrix4& b): a = a * b, summed left to right.
    public static Matrix4x4 Mul(in Matrix4x4 a, in Matrix4x4 b)
    {
        Matrix4x4 r;
        r.M11 = a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41;
        r.M12 = a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42;
        r.M13 = a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43;
        r.M14 = a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44;
        r.M21 = a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41;
        r.M22 = a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42;
        r.M23 = a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43;
        r.M24 = a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44;
        r.M31 = a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41;
        r.M32 = a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42;
        r.M33 = a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43;
        r.M34 = a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44;
        r.M41 = a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41;
        r.M42 = a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42;
        r.M43 = a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43;
        r.M44 = a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44;
        return r;
    }

    // const LLVector3 operator*(const LLVector3 &a, const LLMatrix4 &b): full transform incl. translation.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Transform(Vector3 a, in Matrix4x4 b) => new(
        a.X * b.M11 + a.Y * b.M21 + a.Z * b.M31 + b.M41,
        a.X * b.M12 + a.Y * b.M22 + a.Z * b.M32 + b.M42,
        a.X * b.M13 + a.Y * b.M23 + a.Z * b.M33 + b.M43);

    // LLVector3 * LLMatrix3 (rotation part only)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Rotate(Vector3 a, Quaternion q) => TransformNoTranslation(a, Rotation(q));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 TransformNoTranslation(Vector3 a, in Matrix4x4 b) => new(
        a.X * b.M11 + a.Y * b.M21 + a.Z * b.M31,
        a.X * b.M12 + a.Y * b.M22 + a.Z * b.M32,
        a.X * b.M13 + a.Y * b.M23 + a.Z * b.M33);

    /// <summary>
    /// The normal matrix the viewer builds with glm::transpose(glm::inverse(m)) and applies as v * N; for the
    /// affine matrices here that is the inverse-transpose of the upper 3x3 (translation row zero).
    /// </summary>
    public static Matrix4x4 NormalMatrix(in Matrix4x4 m)
    {
        // cofactors of the 3x3; (M^-1)^T = cof(M) / det
        float c11 = m.M22 * m.M33 - m.M23 * m.M32;
        float c12 = m.M23 * m.M31 - m.M21 * m.M33;
        float c13 = m.M21 * m.M32 - m.M22 * m.M31;
        float c21 = m.M13 * m.M32 - m.M12 * m.M33;
        float c22 = m.M11 * m.M33 - m.M13 * m.M31;
        float c23 = m.M12 * m.M31 - m.M11 * m.M32;
        float c31 = m.M12 * m.M23 - m.M13 * m.M22;
        float c32 = m.M13 * m.M21 - m.M11 * m.M23;
        float c33 = m.M11 * m.M22 - m.M12 * m.M21;
        float det = m.M11 * c11 + m.M12 * c12 + m.M13 * c13;
        float inv = det != 0f ? 1f / det : 0f;
        return new Matrix4x4(
            c11 * inv, c12 * inv, c13 * inv, 0f,
            c21 * inv, c22 * inv, c23 * inv, 0f,
            c31 * inv, c32 * inv, c33 * inv, 0f,
            0f, 0f, 0f, 1f);
    }

    // LLVector3::normalize
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Normalize(Vector3 v)
    {
        float mag = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (mag > MagThreshold)
        {
            float oomag = 1f / mag;
            return new Vector3(v.X * oomag, v.Y * oomag, v.Z * oomag);
        }
        return Vector3.Zero;
    }

    // LLVector3 cross product operator%
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Cross(Vector3 a, Vector3 b) => new(
        a.Y * b.Z - b.Y * a.Z,
        a.Z * b.X - b.Z * a.X,
        a.X * b.Y - b.X * a.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Length(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    // ll_round(val, nearest)
    public static float Round(float val, float nearest) => MathF.Floor(val * (1f / nearest) + 0.5f) * nearest;
}
