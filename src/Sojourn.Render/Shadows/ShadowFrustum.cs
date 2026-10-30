namespace Sojourn.Render.Shadows;

/// <summary>
/// The plane/corner part of LLCamera that the shadow code uses: 8 corners, 6 planes in the
/// viewer's order, and its AABB/sphere tests. Planes are (unit normal, d) with the normal
/// pointing out of the frustum: dist(p) = n.p + d &gt; 0 means outside that plane. Region space.
/// </summary>
public sealed class ShadowFrustum
{
    public const int Left = 0, Right = 1, Near = 2, Bottom = 3, Top = 4, Far = 5;

    public Vector4[] Planes { get; } = new Vector4[6];

    /// <summary>LLCamera::mPlaneMask: which normal components are &gt;= 0 (bit 0 x, 1 y, 2 z).</summary>
    public byte[] Masks { get; } = new byte[6];

    /// <summary>mAgentFrustum: near face (-,-) (+,-) (+,+) (-,+), then the far face.</summary>
    public Vector3[] Corners { get; } = new Vector3[8];

    static readonly Vector3[] Scaler =
    [
        new(-1, -1, -1), new(1, -1, -1), new(-1, 1, -1), new(1, 1, -1),
        new(-1, -1, 1), new(1, -1, 1), new(-1, 1, 1), new(1, 1, 1),
    ];

    /// <summary>LLCamera::calcAgentFrustumPlanes.</summary>
    public static ShadowFrustum FromCorners(ReadOnlySpan<Vector3> frust)
    {
        var f = new ShadowFrustum();
        frust[..8].CopyTo(f.Corners);
        f.Planes[Near] = PlaneFromPoints(frust[0], frust[1], frust[2]);
        f.Planes[Far] = PlaneFromPoints(frust[5], frust[4], frust[6]);
        f.Planes[Left] = PlaneFromPoints(frust[4], frust[0], frust[7]);
        f.Planes[Right] = PlaneFromPoints(frust[1], frust[5], frust[6]);
        f.Planes[Top] = PlaneFromPoints(frust[3], frust[2], frust[6]);
        f.Planes[Bottom] = PlaneFromPoints(frust[1], frust[0], frust[4]);
        for (int i = 0; i < 6; i++)
            f.Masks[i] = CalcMask(f.Planes[i]);
        return f;
    }

    static Vector4 PlaneFromPoints(Vector3 p1, Vector3 p2, Vector3 p3)
    {
        Vector3 n = ShadowMath.Norm(Vector3.Cross(p2 - p1, p3 - p1));
        return new Vector4(n, -Vector3.Dot(p1, n));
    }

    static byte CalcMask(Vector4 p) =>
        (byte)((p.X >= 0 ? 1 : 0) | (p.Y >= 0 ? 2 : 0) | (p.Z >= 0 ? 4 : 0));

    /// <summary>
    /// Overwrite a plane without recomputing its mask, as generateSunShadow does when it swaps in
    /// the shared near plane (getAgentPlane(NEAR).set(...)).
    /// </summary>
    internal void SetPlaneKeepMask(int index, Vector4 plane) => Planes[index] = plane;

    public float Distance(int plane, Vector3 p)
    {
        var pl = Planes[plane];
        return pl.X * p.X + pl.Y * p.Y + pl.Z * p.Z + pl.W;
    }

    /// <summary>LLCamera::AABBInFrustum: 0 outside, 1 partially inside, 2 fully inside.</summary>
    public int AabbInFrustum(Vector3 center, Vector3 radius) => AabbTest(center, radius, true);

    /// <summary>LLCamera::AABBInFrustumNoFarClip.</summary>
    public int AabbInFrustumNoFarClip(Vector3 center, Vector3 radius) => AabbTest(center, radius, false);

    /// <summary>AabbInFrustum on a min/max box, as the viewer's shadow cull does on drawable extents.</summary>
    public bool IntersectsAabb(Vector3 min, Vector3 max) =>
        AabbInFrustum((min + max) * 0.5f, (max - min) * 0.5f) > 0;

    int AabbTest(Vector3 center, Vector3 radius, bool farClip)
    {
        bool result = false;
        for (int i = 0; i < 6; i++)
        {
            if (!farClip && i == Far)
                continue;
            var p = Planes[i];
            var n = new Vector3(p.X, p.Y, p.Z);
            Vector3 rscale = radius * Scaler[Masks[i]];
            float d = -p.W;
            if (Vector3.Dot(n, center - rscale) > d)
                return 0;
            if (!result)
                result = Vector3.Dot(n, center + rscale) > d;
        }
        return result ? 1 : 2;
    }

    /// <summary>LLCamera::sphereInFrustum: 0 outside, 1 partially inside, 2 fully inside.</summary>
    public int SphereInFrustum(Vector3 center, float radius)
    {
        bool res = false;
        for (int i = 0; i < 6; i++)
        {
            float d = Distance(i, center);
            if (d > radius)
                return 0;
            res = res || d > -radius;
        }
        return res ? 1 : 2;
    }
}
