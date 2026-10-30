namespace Sojourn.Render.Shadows;

/// <summary>
/// A projector (spot light) shadow, RenderShadowDetail 2: the spot half of generateSunShadow.
/// The viewer keeps two of these (mSpotShadow[0..1]) for the two highest-priority projectors
/// that deferred lighting drew last frame, fading them in and out (see Priority).
/// </summary>
public sealed class SpotShadow
{
    /// <summary>Region -> light view. view[4+i].</summary>
    public Matrix4x4 LightView { get; private set; }

    /// <summary>glm::perspective(fov, scale.x/scale.y, near, far), GL clip. proj[4+i].</summary>
    public Matrix4x4 LightProjection { get; private set; }

    /// <summary>Region -> Vulkan clip for rendering casters (no depth clamp for spots).</summary>
    public Matrix4x4 LightViewProj { get; private set; }

    /// <summary>GL eye space -> shadow texture coords. shadow_matrix[4+i].</summary>
    public Matrix4x4 ShadowMatrix { get; private set; }

    /// <summary>Caster cull volume: the projector frustum, unmodified.</summary>
    public ShadowFrustum CullFrustum { get; private set; } = null!;

    /// <summary>Projection origin (behind the light's near face) in region space.</summary>
    public Vector3 Origin { get; private set; }

    public float NearClip { get; private set; }
    public float FarClip { get; private set; }

    /// <summary>Square map: (int)(screenWidth * RenderShadowResolutionScale) on a side. proj_shadow_res.</summary>
    public int MapSize { get; private set; }

    /// <param name="cameraView">Main camera region -> eye view (for ShadowMatrix).</param>
    /// <param name="lightPosition">Light prim position, region space (getPositionAgent).</param>
    /// <param name="rotation">Light prim render rotation (LL quaternion; same components as System.Numerics).</param>
    /// <param name="scale">Light prim scale.</param>
    /// <param name="fov">Projector FOV, radians (getSpotLightParams().x).</param>
    /// <param name="radius">Light radius (getLightRadius).</param>
    public static SpotShadow Compute(in Matrix4x4 cameraView, Vector3 lightPosition, Quaternion rotation,
        Vector3 scale, float fov, float radius, int screenWidth, ShadowSettings? settings = null)
    {
        var s = settings ?? ShadowSettings.Default;
        var r = new SpotShadow();

        Vector3 atAxis = Vector3.Transform(new Vector3(0, 0, -scale.Z * 0.5f), rotation);
        Vector3 np = lightPosition + atAxis;
        atAxis = ShadowMath.Norm(atAxis);

        float dist = (scale.Y * 0.5f) / MathF.Tan(fov * 0.5f);
        Vector3 origin = np - atAxis * dist;

        Matrix4x4 lightToRegion = Matrix4x4.CreateFromQuaternion(rotation);
        lightToRegion.Translation = origin;
        Matrix4x4.Invert(lightToRegion, out var view);

        float nearClip = dist + 0.01f;
        float farClip = dist + radius * 1.5f;
        Matrix4x4 proj = ShadowMath.GlPerspective(fov, scale.X / scale.Y, nearClip, farClip);

        Matrix4x4.Invert(cameraView, out var invView);
        Matrix4x4 viewProj = view * proj;

        r.Origin = origin;
        r.NearClip = nearClip;
        r.FarClip = farClip;
        r.LightView = view;
        r.LightProjection = proj;
        r.LightViewProj = viewProj * ShadowMath.VulkanDepthRemap;
        r.ShadowMatrix = invView * (view * (proj * ShadowMath.TexBias));
        r.CullFrustum = ShadowFrustum.FromCorners(ShadowMath.UnprojectCube(viewProj));
        r.MapSize = (int)(uint)(screenWidth * (s.CubeSnapshot ? 1.0f : MathF.Max(0f, s.ResolutionScale)));
        return r;
    }

    /// <summary>
    /// LLVOVolume::updateSpotLightPriority: the projected pixel area of a radius-r sphere pushed
    /// r along the light's -z and r back toward the camera. The two highest-priority projectors
    /// among those deferred lighting draws get the shadow maps (setupSpotLight's insertion sort).
    /// </summary>
    public static float Priority(Vector3 lightPosition, Quaternion rotation, float radius,
        Vector3 cameraOrigin, Vector3 cameraAt, float cameraFovY, int screenHeight)
    {
        Vector3 pos = lightPosition + Vector3.Transform(new Vector3(0, 0, -1), rotation) * radius;
        pos -= cameraAt * radius;

        float d = (pos - cameraOrigin).Length();
        if (d < 16f)
        {
            d /= 16f;
            d *= d;
            d *= 16f;
        }
        float appAngle = MathF.Atan(new Vector3(radius).Length() / d);
        float pixelAngle = screenHeight / cameraFovY;
        float rad = appAngle * pixelAngle;
        return rad * rad * MathF.PI;
    }
}
