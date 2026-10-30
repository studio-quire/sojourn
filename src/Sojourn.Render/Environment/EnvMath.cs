using System.Numerics;

namespace Sojourn.Render.Environment;

/// <summary>
/// Small math helpers that reproduce the viewer's llmath behaviour exactly where it differs from
/// System.Numerics (quaternion slerp, normalisation fallbacks, the light-norm axis permutation).
/// Coordinates are SL agent/region space throughout: +X east, +Y north, +Z up.
/// </summary>
public static class EnvMath
{
    // llmath.h FP_MAG_THRESHOLD
    internal const float FpMagThreshold = 0.0000001f;

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t));

    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t), Lerp(a.Z, b.Z, t));

    /// <summary>llquaternion.cpp slerp(u, a, b): flips to the short arc, lerps when nearly equal, no renormalise.</summary>
    public static Quaternion LLSlerp(float u, Quaternion a, Quaternion b)
    {
        float cosT = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        bool flip = false;
        if (cosT < 0f)
        {
            cosT = -cosT;
            flip = true;
        }

        float alpha, beta;
        if (1f - cosT < 0.00001f)
        {
            beta = 1f - u;
            alpha = u;
        }
        else
        {
            float theta = MathF.Acos(cosT);
            float sinT = MathF.Sin(theta);
            beta = MathF.Sin(theta - u * theta) / sinT;
            alpha = MathF.Sin(u * theta) / sinT;
        }

        if (flip)
            beta = -beta;

        return new Quaternion(
            beta * a.X + alpha * b.X,
            beta * a.Y + alpha * b.Y,
            beta * a.Z + alpha * b.Z,
            beta * a.W + alpha * b.W);
    }

    /// <summary>LLQuaternion::setAngleAxis (identity for a degenerate axis).</summary>
    public static Quaternion AngleAxis(float angle, Vector3 axis)
    {
        float mag = axis.Length();
        if (mag <= FpMagThreshold)
            return Quaternion.Identity;
        angle *= 0.5f;
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle) / mag;
        return new Quaternion(axis.X * s, axis.Y * s, axis.Z * s, c);
    }

    /// <summary>LLVector3::normalize: zero vector when the magnitude is below threshold.</summary>
    public static Vector3 LLNormalize(Vector3 v)
    {
        float mag = v.Length();
        return mag > FpMagThreshold ? v / mag : Vector3.Zero;
    }

    /// <summary>
    /// llsettingssky.cpp convert_azimuth_and_altitude_to_quat: the rotation taking +X to the direction
    /// (cos az cos alt, sin az cos alt, sin alt). Azimuth is counter-clockwise from east.
    /// </summary>
    public static Quaternion AzimuthAltitudeToQuat(float azimuth, float altitude)
    {
        float sinTheta = MathF.Sin(azimuth), cosTheta = MathF.Cos(azimuth);
        float sinPhi = MathF.Sin(altitude), cosPhi = MathF.Cos(altitude);
        var dir = new Vector3(cosTheta * cosPhi, sinTheta * cosPhi, sinPhi);
        var axis = LLNormalize(Vector3.Cross(Vector3.UnitX, dir));
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.UnitX, dir), -1f, 1f));
        return AngleAxis(angle, axis);
    }

    /// <summary>LLVector3 * LLQuaternion (rotates v by q; same as Vector3.Transform).</summary>
    public static Vector3 Rotate(Vector3 v, Quaternion q) => Vector3.Transform(v, q);

    /// <summary>
    /// LLEnvironment::toLightNorm: (y, z, x). The "lightnorm" uniform lives in this permuted frame,
    /// where .y is up; the sky dome and cloud shaders work in the same frame.
    /// </summary>
    public static Vector3 ToLightNorm(Vector3 v) => new(v.Y, v.Z, v.X);

    /// <summary>LLEnvironment::toCFR: (y, x, z).</summary>
    public static Vector3 ToCfr(Vector3 v) => new(v.Y, v.X, v.Z);

    /// <summary>llmath sRGBtoLinear, per component.</summary>
    public static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static Vector3 SrgbToLinear(Vector3 c) => new(SrgbToLinear(c.X), SrgbToLinear(c.Y), SrgbToLinear(c.Z));

    /// <summary>LLColor3::calcHSL luminance: (max + min) / 2.</summary>
    public static float HslLightness(Vector3 c)
    {
        float min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        return (max + min) * 0.5f;
    }

    public static float MaxComponent(Vector3 c) => MathF.Max(c.X, MathF.Max(c.Y, c.Z));

    public static Vector3 Exp(Vector3 v) => new(MathF.Exp(v.X), MathF.Exp(v.Y), MathF.Exp(v.Z));

    /// <summary>
    /// Plane of the water surface (world z = height, normal +Z) in eye space, as
    /// LLSettingsVOWater::applySpecial builds the "waterPlane" uniform. <paramref name="worldToView"/>
    /// is the System.Numerics row-vector view matrix (v_eye = v_world * M), in the same frame as the
    /// positions the shaders see. Returns (n.x, n.y, n.z, -dot(p_eye, n)).
    /// </summary>
    public static Vector4 EyeSpacePlane(Matrix4x4 worldToView, Vector3 worldNormal, Vector3 worldPoint)
    {
        Matrix4x4.Invert(worldToView, out var inv);
        var invTrans = Matrix4x4.Transpose(inv);
        var n = Vector3.Normalize(Vector3.TransformNormal(worldNormal, invTrans));
        var p = Vector3.Transform(worldPoint, worldToView);
        return new Vector4(n, -Vector3.Dot(p, n));
    }
}
