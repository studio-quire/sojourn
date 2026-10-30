using System.Numerics;
using UUID = LibreMetaverse.UUID;

namespace Sojourn.Render.Environment;

/// <summary>
/// Shader-facing water values: LLSettingsVOWater::applySpecial (group ANY, so every program sees
/// the fog terms, used for underwater fog in deferred passes) and LLDrawPoolWater::renderPostDeferred
/// (the water programs: class3/environment/waterF.glsl, class1/environment/waterV.glsl,
/// underWaterF.glsl, fog in class1/environment/waterFogF.glsl).
/// Camera-dependent uniforms are left to the caller: "waterPlane" (EnvMath.EyeSpacePlane with
/// normal +Z at the region water height), "waterHeight" (camera z - water height), "eyeVec" and
/// "camPosLocal" (camera position), "clipPlane" (mirror plane).
/// </summary>
public struct WaterUniforms
{
    /// <summary>"waterFogColor" (vec4), group ANY: water_fog_color in sRGB, alpha 1. waterFogF converts with srgb_to_linear itself.</summary>
    public Vector4 WaterFogColor;
    /// <summary>"waterFogColorLinear" (vec3), group ANY: linearColor3(water_fog_color).</summary>
    public Vector3 WaterFogColorLinear;
    /// <summary>
    /// "waterFogDensity", group ANY and the water pool: water_fog_density, raised to
    /// underwater_fog_mod when the camera is under water (getModifiedWaterFogDensity).
    /// </summary>
    public float WaterFogDensity;
    /// <summary>
    /// "waterFogKS", group ANY: 1/max(lightnorm[2], 0.3). lightnorm is the permuted (y,z,x) light
    /// norm, so index 2 is the world X (east) component, not up; that is the viewer's behaviour.
    /// </summary>
    public float WaterFogKS;
    /// <summary>"lightnorm" for the water programs: the clamped, permuted light norm (as SkyUniforms.LightNorm).</summary>
    public Vector3 LightNorm;
    /// <summary>"lightDir" (waterV/waterF): getLightDirection, normalised, raw agent space (not permuted).</summary>
    public Vector3 LightDir;
    /// <summary>
    /// "specular" (waterF): the sun (or, with only the moon up, moon) colour normalised to unit
    /// length, times 1.5 + 6*(lightDir.x^2 + lightDir.y^2). Zero when neither is up.
    /// </summary>
    public Vector3 Specular;
    /// <summary>"waveDir1" / "waveDir2" (waterV): wave1_direction / wave2_direction.</summary>
    public Vector2 WaveDir1;
    public Vector2 WaveDir2;
    /// <summary>"normScale" (waterF): normal_scale.</summary>
    public Vector3 NormScale;
    /// <summary>"fresnelScale" / "fresnelOffset" (waterF).</summary>
    public float FresnelScale;
    public float FresnelOffset;
    /// <summary>"blurMultiplier" (waterF): max(0, blur_multiplier) * 2.</summary>
    public float BlurMultiplier;
    /// <summary>"refScale" (waterF): scale_below under water, scale_above otherwise.</summary>
    public float RefScale;
    /// <summary>"time" (waterV): viewer uptime*0.5; here animation-clock seconds*0.5.</summary>
    public float Time;
    /// <summary>bumpMap / bumpMap2 normal maps and "blend_factor" between them (0 with one texture).</summary>
    public UUID NormalMap;
    public UUID NormalMapNext;
    public float NormalMapBlendFactor;
    /// <summary>"blend_factor" the water settings push to group ANY (the raw water keyframe blend).</summary>
    public float WaterBlendFactor;
    /// <summary>"sun_up_factor" the water pool sets.</summary>
    public int SunUpFactor;
    /// <summary>"exposure", "tonemap_type", "tonemap_mix" the water pool sets (same as the sky's).</summary>
    public float Exposure;
    public int TonemapType;
    public float TonemapMix;
    public bool CameraUnderWater;

    /// <param name="cameraHeightAboveWater">camera z minus the region water height; &lt;= 0 counts as under water.</param>
    public static WaterUniforms Compute(EnvironmentSnapshot snapshot, float cameraHeightAboveWater,
        ViewerRenderSettings? settings = null, double? animationSeconds = null)
    {
        settings ??= ViewerRenderSettings.Default;
        var water = snapshot.Water;
        var sky = snapshot.Sky;
        var u = new WaterUniforms();
        bool underwater = cameraHeightAboveWater <= 0f;
        u.CameraUnderWater = underwater;

        // LLSettingsVOWater::applySpecial
        var clamped = sky.LightDirection;
        if (clamped.Z < -0.1f) clamped.Z = -0.1f;
        u.LightNorm = EnvMath.ToLightNorm(clamped);
        u.WaterFogKS = 1f / MathF.Max(u.LightNorm.Z, 0.3f);
        u.WaterFogDensity = water.GetModifiedFogDensity(underwater);
        u.WaterFogColor = new Vector4(water.FogColor, 1f);
        u.WaterFogColorLinear = EnvMath.SrgbToLinear(water.FogColor);
        u.WaterBlendFactor = water.BlendFactor;

        // LLDrawPoolWater::renderPostDeferred
        bool sunUp = sky.IsSunUp, moonUp = sky.IsMoonUp;
        var lightDir = EnvMath.LLNormalize(sky.LightDirection);
        Vector3 lightDiffuse = Vector3.Zero;
        if (sunUp) lightDiffuse += sky.SunlightColor;
        else if (moonUp) lightDiffuse += sky.MoonlightColor;
        float groundProjSq = lightDir.X * lightDir.X + lightDir.Y * lightDir.Y;
        float len = lightDiffuse.Length();
        if (len > EnvMath.FpMagThreshold)
            lightDiffuse = lightDiffuse / len * (1.5f + 6f * groundProjSq);
        else
            lightDiffuse = Vector3.Zero;
        u.Specular = lightDiffuse;
        u.LightDir = lightDir;
        u.WaveDir1 = water.Wave1Direction;
        u.WaveDir2 = water.Wave2Direction;
        u.NormScale = water.NormalScale;
        u.FresnelScale = water.FresnelScale;
        u.FresnelOffset = water.FresnelOffset;
        u.BlurMultiplier = MathF.Max(0f, water.BlurMultiplier) * 2f;
        u.RefScale = underwater ? water.ScaleBelow : water.ScaleAbove;
        double clock = animationSeconds ?? SkyUniforms.AnimationClock(snapshot.UnixSeconds);
        u.Time = (float)(clock * 0.5);
        // LLDrawPoolWater::setNormalMaps fallbacks
        u.NormalMap = water.NormalMapId != UUID.Zero ? water.NormalMapId : WaterSettings.DefaultNormalMapId;
        u.NormalMapNext = water.NextNormalMapId != UUID.Zero ? water.NextNormalMapId : u.NormalMap;
        u.NormalMapBlendFactor = SkyUniforms.PairBlend(u.NormalMap, u.NormalMapNext, water.BlendFactor);
        u.SunUpFactor = sunUp ? 1 : 0;
        u.Exposure = Math.Clamp(settings.Exposure, 0.5f, 4f);
        u.TonemapType = settings.TonemapType;
        u.TonemapMix = sky.CanAutoAdjust && !settings.AutoAdjustLegacySky ? 0f : settings.TonemapMix;
        return u;
    }
}
