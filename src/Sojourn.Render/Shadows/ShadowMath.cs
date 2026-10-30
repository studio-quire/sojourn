namespace Sojourn.Render.Shadows;

// Matrix note: a glm (column-vector) matrix's column-major memory, read in order, is exactly the
// argument list of the row-vector System.Numerics Matrix4x4 constructor. So glm::mat4(16 scalars)
// and glm::make_mat4(ptr) port literally, and glm A*B becomes B*A here.
internal static class ShadowMath
{
    const float FpMagThreshold = 0.0000001f;

    /// <summary>LLVector3::normVec.</summary>
    public static Vector3 Norm(Vector3 v)
    {
        float mag = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (mag > FpMagThreshold)
        {
            float t = 1f / mag;
            return new Vector3(v.X * t, v.Y * t, v.Z * t);
        }
        return Vector3.Zero;
    }

    /// <summary>LLVector3 / F32 (multiplies by the reciprocal).</summary>
    public static Vector3 Div(Vector3 v, float k)
    {
        float t = 1f / k;
        return new Vector3(v.X * t, v.Y * t, v.Z * t);
    }

    /// <summary>pipeline.cpp look(): a gluLookAt along dir.</summary>
    public static Matrix4x4 Look(Vector3 pos, Vector3 dir, Vector3 up)
    {
        Vector3 lftN = Norm(Vector3.Cross(dir, up));
        Vector3 upN = Norm(Vector3.Cross(lftN, dir));
        Vector3 dirN = Norm(dir);
        return new Matrix4x4(
            lftN.X, upN.X, -dirN.X, 0f,
            lftN.Y, upN.Y, -dirN.Y, 0f,
            lftN.Z, upN.Z, -dirN.Z, 0f,
            -Vector3.Dot(lftN, pos), -Vector3.Dot(upN, pos), Vector3.Dot(dirN, pos), 1f);
    }

    /// <summary>glm::ortho (GL, clip z in [-w, w]).</summary>
    public static Matrix4x4 GlOrtho(float l, float r, float b, float t, float n, float f)
    {
        var m = Matrix4x4.Identity;
        m.M11 = 2f / (r - l);
        m.M22 = 2f / (t - b);
        m.M33 = -2f / (f - n);
        m.M41 = -(r + l) / (r - l);
        m.M42 = -(t + b) / (t - b);
        m.M43 = -(f + n) / (f - n);
        return m;
    }

    /// <summary>glm::perspective (GL, right-handed, clip z in [-w, w]).</summary>
    public static Matrix4x4 GlPerspective(float fovy, float aspect, float n, float f)
    {
        float tanHalf = MathF.Tan(fovy / 2f);
        var m = new Matrix4x4();
        m.M11 = 1f / (aspect * tanHalf);
        m.M22 = 1f / tanHalf;
        m.M33 = -(f + n) / (f - n);
        m.M34 = -1f;
        m.M43 = -(2f * f * n) / (f - n);
        return m;
    }

    /// <summary>The viewer's "trans" matrix: [-1,1] -> [0,1] on all three axes.</summary>
    public static readonly Matrix4x4 TexBias = new(
        0.5f, 0f, 0f, 0f,
        0f, 0.5f, 0f, 0f,
        0f, 0f, 0.5f, 0f,
        0.5f, 0.5f, 0.5f, 1f);

    /// <summary>
    /// GL clip -> Vulkan clip: z' = (z + w) / 2, so Vulkan depth equals GL window depth.
    /// x and y are untouched: with a positive-height viewport, NDC y = -1 lands on framebuffer
    /// row 0 in both APIs, and texture v = 0 is row 0 in both.
    /// </summary>
    public static readonly Matrix4x4 VulkanDepthRemap = new(
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 0.5f, 0f,
        0f, 0f, 0.5f, 1f);

    /// <summary>mul_mat4_vec3: transform a point with w divide.</summary>
    public static Vector3 MulPoint(in Matrix4x4 m, Vector3 v)
    {
        float w = v.X * m.M14 + v.Y * m.M24 + v.Z * m.M34 + m.M44;
        return new Vector3(
            (v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + m.M41) / w,
            (v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + m.M42) / w,
            (v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + m.M43) / w);
    }

    public static void UpdateMinMax(ref Vector3 min, ref Vector3 max, Vector3 p)
    {
        if (min.X > p.X) min.X = p.X;
        if (max.X < p.X) max.X = p.X;
        if (min.Y > p.Y) min.Y = p.Y;
        if (max.Y < p.Y) max.Y = p.Y;
        if (min.Z > p.Z) min.Z = p.Z;
        if (max.Z < p.Z) max.Z = p.Z;
    }

    /// <summary>
    /// LLViewerCamera::updateFrustumPlanes(camera, false, false, true): unproject the corners of
    /// the GL NDC cube through view*proj (region -> GL clip). Order: near face (-,-) (+,-) (+,+) (-,+),
    /// then the far face in the same order.
    /// </summary>
    public static Vector3[] UnprojectCube(in Matrix4x4 viewProj)
    {
        Matrix4x4.Invert(viewProj, out var inv);
        var c = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            float x = (i & 3) is 1 or 2 ? 1f : -1f;
            float y = (i & 3) >= 2 ? 1f : -1f;
            float z = i < 4 ? -1f : 1f;
            var p = Vector4.Transform(new Vector4(x, y, z, 1f), inv);
            c[i] = new Vector3(p.X, p.Y, p.Z) / p.W;
        }
        return c;
    }

    /// <summary>BlurHappySize: (x * scale + 16) rounded down to a multiple of 16.</summary>
    public static int BlurHappySize(int x, float scale) => (int)((uint)(x * scale + 16.0f) & ~0xFu);
}
