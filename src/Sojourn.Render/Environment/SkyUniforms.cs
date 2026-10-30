using System.Numerics;
using UUID = LibreMetaverse.UUID;

namespace Sojourn.Render.Environment;

/// <summary>Which pass the uniforms are for; reflection-probe irradiance passes get altered values.</summary>
public enum SkyPass
{
    /// <summary>The ordinary camera view.</summary>
    Normal,
    /// <summary>A reflection probe radiance capture (gCubeSnapshot, radiance pass): same as Normal here.</summary>
    ProbeRadiance,
    /// <summary>
    /// A reflection probe irradiance capture (gCubeSnapshot and not radiance): ambient_color is zeroed
    /// and blue_horizon/blue_density are desaturated so the probes don't tint the world blue.
    /// </summary>
    ProbeIrradiance,
}

/// <summary>A sun or moon billboard (LLVOSky::updateHeavenlyBodyGeometry), camera-relative.</summary>
public readonly record struct HeavenlyBodyQuad(Vector3 Corner0, Vector3 Corner1, Vector3 Corner2, Vector3 Corner3)
{
    /// <summary>Texture coordinates of Corner0..3 (TEX01, TEX00, TEX11, TEX10); triangles (0,2,1) and (1,2,3).</summary>
    public static readonly Vector2[] TexCoords = [new(0, 1), new(0, 0), new(1, 1), new(1, 0)];
    public static readonly ushort[] Indices = [0, 2, 1, 1, 2, 3];
}

/// <summary>
/// Every shader-facing value the viewer derives from the current sky, as plain data.
/// Each field names the GLSL uniform it feeds and where the viewer sets it.
///
/// Groups (LLGLSLShader shader groups): "ANY" values go to every shader; "SKY" ones to the sky dome,
/// clouds, sun, moon and stars programs (deferred/skyF, cloudsF, sunDiscF, moonF, starsF);
/// "DEFAULT" ones to everything else (softenLight, alpha, materials, haze...); "deferred" ones are
/// set by LLPipeline::bindDeferredShader on deferred programs and override the ANY values there.
///
/// Spaces: directions are SL agent space (+X east, +Y north, +Z up) unless named LightNorm
/// (permuted (y,z,x), y up: the frame atmosphericsFuncs/skyF/cloudsF use). Colours are WindLight's
/// gamma-space ("sRGB-ish") values unless named Linear; see the report for where shaders convert.
/// </summary>
public struct SkyUniforms
{
    // ---------------------------------------------------------------- directions

    /// <summary>Sun direction, unit, agent space (LLSettingsSky::getSunDirection = +X rotated by sun_rotation).</summary>
    public Vector3 SunDirection;
    /// <summary>Moon direction, unit, agent space. Also uniform "moon_dir" of deferred/moonF.glsl (raw, not view space).</summary>
    public Vector3 MoonDirection;
    /// <summary>getLightDirection: sun if up, else moon if up, else (0,0,-1).</summary>
    public Vector3 LightDirection;
    /// <summary>
    /// "lightnorm" (vec3): LLEnvironment::getClampedLightNorm, i.e. LightDirection with z clamped to
    /// >= -0.1 (not renormalised), permuted to (y,z,x). Groups DEFAULT and SKY (applySpecial), also
    /// set explicitly for softenLight (pipeline.cpp) and the water/haze programs.
    /// Used by windlight/atmosphericsFuncs.glsl, deferred/skyF.glsl, cloudsV/F.glsl, waterF.glsl.
    /// </summary>
    public Vector3 LightNorm;
    /// <summary>
    /// World direction behind deferred "sun_dir" (softenLightF etc.): the sun direction, or (0,1,0)
    /// when neither body is up (setupHWLights). The viewer multiplies it by the view matrix (w = 0).
    /// </summary>
    public Vector3 DeferredSunDirection;
    /// <summary>World direction behind deferred "moon_dir"; same rules as <see cref="DeferredSunDirection"/>.</summary>
    public Vector3 DeferredMoonDirection;
    public Quaternion SunRotation;
    public Quaternion MoonRotation;
    public bool IsSunUp;
    public bool IsMoonUp;
    /// <summary>"sun_up_factor" (int 0/1), group ANY; also set per pass by the pipeline and draw pools.</summary>
    public int SunUpFactor;
    /// <summary>"sun_moon_glow_factor", group ANY and the sky/cloud draw pool: 1 sun up, moon_brightness/4 moon only, else 0.</summary>
    public float SunMoonGlowFactor;

    // ---------------------------------------------------------------- light colours

    /// <summary>
    /// "sunlight_color", group SKY: the raw sky sunlight_color (0..3, not attenuated; the shaders
    /// attenuate it). With RenderSkyAutoAdjustLegacy on a classic sky, group ANY gets it scaled.
    /// In classic mode syncLightState replaces it with light 0's normalised colour (see Compute).
    /// </summary>
    public Vector3 SunlightColor;
    /// <summary>"moonlight_color", group SKY: same as the sunlight colour (EEP shares it).</summary>
    public Vector3 MoonlightColor;
    /// <summary>
    /// "sunlight_color" on deferred programs (bindDeferredShader): the sky sunlight colour divided by
    /// its max component when that exceeds 1, clamped to [0,1], zero when neither body is up, times
    /// RenderSkyAutoAdjustSunColorScale for auto-adjusted classic skies. This is the one
    /// softenLightF/alpha/water lighting sees.
    /// </summary>
    public Vector3 DeferredSunlightColor;
    /// <summary>"moonlight_color" on deferred programs; normalised like the sun, zero when neither is up.</summary>
    public Vector3 DeferredMoonlightColor;
    /// <summary>
    /// "ambient_color", group ANY: the sky ambient (legacy_haze ambient). Zero in probe irradiance
    /// passes; times RenderSkyAutoAdjustAmbientScale for auto-adjusted classic skies.
    /// </summary>
    public Vector3 AmbientColor;
    /// <summary>"blue_horizon", group ANY (desaturated to HSL lightness in irradiance passes).</summary>
    public Vector3 BlueHorizon;
    /// <summary>"blue_density", group ANY (desaturated to HSL lightness in irradiance passes).</summary>
    public Vector3 BlueDensity;
    /// <summary>"haze_horizon", group ANY.</summary>
    public float HazeHorizon;
    /// <summary>"haze_density", group ANY.</summary>
    public float HazeDensity;
    /// <summary>"density_multiplier", group ANY.</summary>
    public float DensityMultiplier;
    /// <summary>"distance_multiplier", group ANY.</summary>
    public float DistanceMultiplier;
    /// <summary>"max_y", group ANY.</summary>
    public float MaxY;
    /// <summary>"glow" (vec3: x size, y unused, z falloff exponent), group ANY.</summary>
    public Vector3 Glow;
    /// <summary>"cloud_shadow", group ANY.</summary>
    public float CloudShadow;
    /// <summary>"scene_light_strength", group ANY: 2*(0.75 + RenderSunDynamicRange*max(sun.z,0)); divisor for local lights.</summary>
    public float SceneLightStrength;
    /// <summary>"gamma", group ANY: the sky gamma. Read by the legacy gamma post shaders (postDeferredTonemap/GammaCorrect legacyGamma).</summary>
    public float Gamma;

    // ---------------------------------------------------------------- clouds

    /// <summary>"cloud_color", group SKY (cloudsF).</summary>
    public Vector3 CloudColor;
    /// <summary>
    /// "cloud_pos_density1", group SKY: cloud_pos_density1 + (-scroll.x, scroll.y, 0) (the x flip is
    /// SL-13084). xy are noise texture offsets, z the density.
    /// </summary>
    public Vector3 CloudPosDensity1;
    /// <summary>"cloud_pos_density2", group ANY.</summary>
    public Vector3 CloudPosDensity2;
    /// <summary>"cloud_scale", group ANY.</summary>
    public float CloudScale;
    /// <summary>"cloud_variance", group ANY and the cloud draw pool.</summary>
    public float CloudVariance;
    /// <summary>The cloud scroll offset already folded into <see cref="CloudPosDensity1"/>, [0,1).</summary>
    public Vector2 CloudScrollDelta;
    /// <summary>cloud_noise_texture (A) / cloud_noise_texture_next (B); null means none.</summary>
    public UUID CloudNoiseTexture;
    public UUID CloudNoiseTextureNext;
    /// <summary>"blend_factor" for cloudsF: 0 when only one texture is bound or the scroll rate is exactly zero.</summary>
    public float CloudBlendFactor;
    /// <summary>Clouds are drawn only when a cloud noise texture exists (and cloud_scale >= 0.001 in the shader).</summary>
    public bool CloudsVisible;

    // ---------------------------------------------------------------- sky dome extras

    /// <summary>"camPosLocal" for the dome draws: (0, dome_offset*dome_radius, 0) = (0, 14400, 0), in the dome's y-up frame.</summary>
    public Vector3 SkyDomeCameraPosition;
    /// <summary>"moon_brightness", group ANY and moonF.</summary>
    public float MoonBrightness;
    /// <summary>"moisture_level" (rainbow), skyF; forced to 0 when neither body is up (draw pool).</summary>
    public float MoistureLevel;
    /// <summary>"droplet_radius", skyF.</summary>
    public float DropletRadius;
    /// <summary>"ice_level" (22 degree halo), skyF; 0 when neither body is up.</summary>
    public float IceLevel;
    /// <summary>rainbow_map / halo_map textures (the viewer latches these when LLVOSky initialises).</summary>
    public UUID RainbowTexture;
    public UUID HaloTexture;

    // ---------------------------------------------------------------- sun, moon, stars

    /// <summary>Sun disc diffuseMap / altDiffuseMap. EEP's default sun texture is null: no disc drawn.</summary>
    public UUID SunTexture;
    public UUID SunTextureNext;
    /// <summary>"blend_factor" for sunDiscF.</summary>
    public float SunDiscBlendFactor;
    /// <summary>Sun disc drawn: sun up and a texture present.</summary>
    public bool SunDiscVisible;
    /// <summary>Sun disc "color" (DIFFUSE_COLOR): the attenuated sun diffuse (sunDiscF ignores it today).</summary>
    public Vector3 SunDiscColor;
    public float SunScale;
    public UUID MoonTexture;
    public UUID MoonTextureNext;
    public bool MoonDiscVisible;
    /// <summary>Moon "color" uniform and moonF "moonlight_color": both white.</summary>
    public Vector3 MoonDiscColor;
    public float MoonScale;
    /// <summary>Stars "custom_alpha": star_brightness/500 (1 in reflection renders).</summary>
    public float StarAlpha;
    /// <summary>Stars are skipped when <see cref="StarAlpha"/> &lt; 0.001.</summary>
    public bool StarsVisible;
    /// <summary>Star bloom textures (null bloom ids fall back to the default bloom texture).</summary>
    public UUID StarTexture;
    public UUID StarTextureNext;
    public float StarBlendFactor;
    /// <summary>Stars "time" uniform: viewer uptime*0.5; here animation-clock seconds*0.5.</summary>
    public float StarTime;
    /// <summary>Stars dome rotation about +Z in degrees (gFrameTimeSeconds*0.01), applied around the camera.</summary>
    public float StarRotationDegrees;
    /// <summary>The sky's keyframe blend factor (getBlendFactor) used for the A/B texture blends.</summary>
    public float SkyBlendFactor;

    // ---------------------------------------------------------------- HDR, ambient split, tonemap

    /// <summary>"classic_mode" (int), group ANY: the sky has no reflection_probe_ambiance and auto-adjust is off.</summary>
    public int ClassicMode;
    /// <summary>"classic_mode" the pipeline gives local light programs: 1 whenever the sky can auto-adjust.</summary>
    public int LocalLightClassicMode;
    /// <summary>"sky_hdr_scale", group ANY: sqrt(gamma)*2 for PBR skies (probe ambiance != 0), else 1 (auto-adjust: its scale).</summary>
    public float SkyHdrScale;
    /// <summary>"sky_sunlight_scale", group ANY.</summary>
    public float SkySunlightScale;
    /// <summary>"sky_ambient_scale", group ANY.</summary>
    public float SkyAmbientScale;
    /// <summary>
    /// "reflection_probe_ambiance", group ANY, and the floor of every probe's refParams.x (ambiance
    /// scale) in the probe UBO (LLReflectionMapManager::updateUniforms).
    /// </summary>
    public float ReflectionProbeAmbiance;
    /// <summary>
    /// The post chain the viewer picks: probe ambiance == 0 means legacy gamma (legacyGamma with
    /// "gamma") and no tonemap (NO_POST: clamp only). Otherwise tonemap + plain sRGB encode.
    /// </summary>
    public bool LegacyGammaNoPost;
    /// <summary>
    /// NO_POST tonemap variant: clamp only, no exposure or tonemapping. The viewer picks it for legacy skies and for
    /// snapshots taken with no_post (<see cref="ViewerRenderSettings.SnapshotNoPost"/>).
    /// </summary>
    public bool NoPost;
    /// <summary>"tonemap_mix": 0 for classic skies, RenderTonemapMix otherwise.</summary>
    public float TonemapMix;
    /// <summary>"tonemap_type" (RenderTonemapType).</summary>
    public int TonemapType;
    /// <summary>"exposure": RenderExposure clamped to [0.5, 4].</summary>
    public float Exposure;
    /// <summary>"dynamic_exposure_params" (coefficient, min, max, speed error) of the exposure pass.</summary>
    public Vector4 DynamicExposureParams;
    /// <summary>"diffuse_luminance_scale" of the luminance pass (RenderDiffuseLuminanceScale): non-sky pixels count this much toward exposure.</summary>
    public float DiffuseLuminanceScale;
    /// <summary>RenderCASSharpness; 0 = no sharpening pass (the tonemap pass gamma-corrects instead).</summary>
    public float CasSharpness;
    /// <summary>SMAA preset (RenderFSAASamples clamped to 0-3) when RenderFSAAType is SMAA, else -1.</summary>
    public int SmaaQuality;
    /// <summary>"dynamic_exposure_params2" (hdr offset, min, max, speed target).</summary>
    public Vector4 DynamicExposureParams2;

    // ---------------------------------------------------------------- CPU-side light state

    /// <summary>LLSettingsSky lighting (getSunDiffuse etc.); sun disc colour, fixed-function light, legacy paths.</summary>
    public SkyLighting Lighting;

    public static SkyUniforms Compute(EnvironmentSnapshot snapshot, ViewerRenderSettings? settings = null,
        SkyPass pass = SkyPass.Normal, double? animationSeconds = null)
    {
        settings ??= ViewerRenderSettings.Default;
        var sky = snapshot.Sky;
        var u = new SkyUniforms();
        var lighting = sky.ComputeLighting();
        u.Lighting = lighting;

        bool sunUp = sky.IsSunUp, moonUp = sky.IsMoonUp;
        bool irradiance = pass == SkyPass.ProbeIrradiance;
        bool desaturate = irradiance && settings.DesaturateIrradiance;

        u.SunDirection = sky.SunDirection;
        u.MoonDirection = sky.MoonDirection;
        u.LightDirection = sky.LightDirection;
        u.SunRotation = sky.SunRotation;
        u.MoonRotation = sky.MoonRotation;
        u.IsSunUp = sunUp;
        u.IsMoonUp = moonUp;

        // LLSettingsVOSky::applyToUniforms (group ANY)
        u.AmbientColor = sky.AmbientColor;
        u.BlueDensity = desaturate ? new Vector3(EnvMath.HslLightness(sky.BlueDensity)) : sky.BlueDensity;
        u.BlueHorizon = desaturate ? new Vector3(EnvMath.HslLightness(sky.BlueHorizon)) : sky.BlueHorizon;
        u.HazeDensity = sky.HazeDensity;
        u.HazeHorizon = sky.HazeHorizon;
        u.DensityMultiplier = sky.DensityMultiplier;
        u.DistanceMultiplier = sky.DistanceMultiplier;
        u.CloudPosDensity2 = sky.CloudPosDensity2;
        u.CloudScale = sky.CloudScale;
        u.CloudShadow = sky.CloudShadow;
        u.CloudVariance = sky.CloudVariance;
        u.Glow = sky.Glow;
        u.MaxY = sky.MaxY;
        u.MoonBrightness = sky.MoonBrightness;
        u.MoistureLevel = sky.MoistureLevel;
        u.DropletRadius = sky.DropletRadius;
        u.IceLevel = sky.IceLevel;

        // LLSettingsVOSky::applySpecial
        var clamped = u.LightDirection;
        if (clamped.Z < -0.1f) clamped.Z = -0.1f;
        u.LightNorm = EnvMath.ToLightNorm(clamped);

        u.CloudScrollDelta = snapshot.CloudScrollDelta;
        u.CloudPosDensity1 = sky.CloudPosDensity1 + new Vector3(-snapshot.CloudScrollDelta.X, snapshot.CloudScrollDelta.Y, 0f);
        u.SunlightColor = sky.SunlightColor;
        u.MoonlightColor = sky.MoonlightColor;
        u.CloudColor = sky.CloudColor;

        // LLSettingsVOSky::updateSettings
        float sdr = MathF.Max(settings.SunDynamicRange, 0.0001f);
        u.SceneLightStrength = 2.0f * (0.75f + sdr * MathF.Max(u.SunDirection.Z, 0f));

        Vector3 ambient = lighting.TotalAmbient;
        float g = sky.Gamma;
        bool classic = sky.CanAutoAdjust && !settings.AutoAdjustLegacySky;

        u.SkySunlightScale = settings.HdrEnabled ? settings.HdrSkySunlightScale : settings.SkySunlightScale;
        u.SkyAmbientScale = settings.SkyAmbientScale;
        u.ClassicMode = classic ? 1 : 0;

        float probeAmbiance = sky.ReflectionProbeAmbiance;
        // sky_hdr_scale is not pushed during irradiance passes, so the program keeps the value of
        // the last normal pass: compute that value either way.
        float hdrScale = sky.ReflectionProbeAmbiance != 0f ? MathF.Sqrt(g) * 2.0f
            : sky.CanAutoAdjust && settings.AutoAdjustLegacySky ? settings.AutoAdjustHdrScale
            : 1f;
        if (irradiance)
        {
            // direct light only; ambient comes back through the probes
            u.AmbientColor = Vector3.Zero;
        }
        else if (sky.ReflectionProbeAmbiance != 0f)
        {
            u.AmbientColor = ambient;
            if (!settings.ReflectionProbesEnabled)
                probeAmbiance = 1f; // DEFAULT_AUTO_ADJUST_PROBE_AMBIANCE
        }
        else if (sky.CanAutoAdjust && settings.AutoAdjustLegacySky)
        {
            u.AmbientColor = ambient * settings.AutoAdjustAmbientScale;
            u.BlueHorizon = sky.BlueHorizon * settings.AutoAdjustBlueHorizonScale;
            u.BlueDensity = sky.BlueDensity * settings.AutoAdjustBlueDensityScale;
            // the ANY-group sunlight_color is scaled here too; SKY-group programs keep the raw one
            probeAmbiance = settings.AutoAdjustProbeAmbiance;
        }
        else
        {
            u.AmbientColor = ambient;
        }
        u.SkyHdrScale = hdrScale;
        u.ReflectionProbeAmbiance = probeAmbiance;
        u.SunUpFactor = sunUp ? 1 : 0;
        u.SunMoonGlowFactor = sky.SunMoonGlowFactor;
        u.Gamma = g;
        u.LocalLightClassicMode = sky.CanAutoAdjust ? 1 : 0;

        // LLPipeline::setupHWLights + bindDeferredShader
        static Vector3 NormaliseLight(Vector3 c)
        {
            float max = EnvMath.MaxComponent(c);
            if (max > 1f) c *= 1f / max;
            return Vector3.Clamp(c, Vector3.Zero, Vector3.One);
        }
        var sunDiffuse = NormaliseLight(sky.SunlightColor);
        var moonDiffuse = NormaliseLight(sky.MoonlightColor);
        u.DeferredSunDirection = u.SunDirection;
        u.DeferredMoonDirection = u.MoonDirection;
        if (!sunUp && !moonUp)
        {
            sunDiffuse = moonDiffuse = Vector3.Zero;
            u.DeferredSunDirection = u.DeferredMoonDirection = Vector3.UnitY;
        }
        if (settings.AutoAdjustLegacySky && sky.CanAutoAdjust)
            sunDiffuse *= settings.AutoAdjustSunColorScale;
        u.DeferredSunlightColor = sunDiffuse;
        u.DeferredMoonlightColor = moonDiffuse;

        // LLRender::syncLightState: in classic mode every program with lighting or atmospherics features (the sky,
        // clouds, sun and moon programs among them) gets light 0's colours at draw time, overriding the raw sky
        // colours applySpecial gave the SKY group. Matters when sunlight_color exceeds 1 (Sunset's 2.84, Sunrise's 2.37).
        if (classic)
        {
            var light0 = sunUp ? NormaliseLight(sky.SunlightColor) : moonDiffuse;
            if (!sunUp && !moonUp) light0 = Vector3.Zero;
            u.SunlightColor = light0;
            u.MoonlightColor = moonDiffuse;
        }

        // LLDrawPoolWLSky
        u.SkyDomeCameraPosition = new Vector3(0f, SkySettings.DomeOffset * SkySettings.DomeRadius, 0f);
        if (!sunUp && !moonUp)
        {
            u.MoistureLevel = 0f;
            u.IceLevel = 0f;
        }
        u.RainbowTexture = sky.RainbowTextureId == UUID.Zero ? SkySettings.DefaultRainbowTextureId : sky.RainbowTextureId;
        u.HaloTexture = sky.HaloTextureId == UUID.Zero ? SkySettings.DefaultHaloTextureId : sky.HaloTextureId;
        u.SkyBlendFactor = sky.BlendFactor;

        u.CloudNoiseTexture = sky.CloudTextureId;
        u.CloudNoiseTextureNext = sky.NextCloudTextureId;
        u.CloudBlendFactor = PairBlend(sky.CloudTextureId, sky.NextCloudTextureId, sky.BlendFactor);
        if (sky.CloudScrollRate == Vector2.Zero) u.CloudBlendFactor = 0f;
        // LLDrawPoolWLSky::renderDeferred: "don't draw clouds in irradiance maps to avoid popping"
        u.CloudsVisible = !irradiance && (sky.CloudTextureId != UUID.Zero || sky.NextCloudTextureId != UUID.Zero);

        u.SunTexture = sky.SunTextureId;
        u.SunTextureNext = sky.NextSunTextureId;
        u.SunDiscBlendFactor = PairBlend(sky.SunTextureId, sky.NextSunTextureId, sky.BlendFactor);
        u.SunDiscVisible = sunUp && (sky.SunTextureId != UUID.Zero || sky.NextSunTextureId != UUID.Zero);
        u.SunDiscColor = lighting.SunDiffuse;
        u.SunScale = sky.SunScale;
        u.MoonTexture = sky.MoonTextureId;
        u.MoonTextureNext = sky.NextMoonTextureId;
        u.MoonDiscVisible = moonUp && sky.MoonTextureId != UUID.Zero;
        u.MoonDiscColor = Vector3.One;
        u.MoonScale = sky.MoonScale;

        u.StarAlpha = sky.StarBrightness / 500.0f;
        u.StarsVisible = u.StarAlpha >= 0.001f;
        // LLVOSky::setBloomTextures fallbacks
        var bloom = sky.BloomTextureId == UUID.Zero ? SkySettings.DefaultBloomTextureId : sky.BloomTextureId;
        var bloomNext = sky.NextBloomTextureId == UUID.Zero
            ? (sky.BloomTextureId == UUID.Zero ? SkySettings.DefaultBloomTextureId : sky.BloomTextureId)
            : sky.NextBloomTextureId;
        u.StarTexture = bloom;
        u.StarTextureNext = bloomNext;
        u.StarBlendFactor = PairBlend(bloom, bloomNext, sky.BlendFactor);
        double clock = animationSeconds ?? AnimationClock(snapshot.UnixSeconds);
        u.StarTime = (float)(clock * 0.5);
        u.StarRotationDegrees = (float)(clock * 0.01 % 360.0);

        // LLPipeline::tonemap / generateExposure
        bool aa = settings.AutoAdjustLegacySky;
        float probeAA = sky.GetReflectionProbeAmbiance(aa, settings.AutoAdjustProbeAmbiance);
        u.LegacyGammaNoPost = probeAA == 0f;
        u.NoPost = u.LegacyGammaNoPost || settings.SnapshotNoPost;
        // applySpecial sets mTonemapMix = RenderTonemapMix on non-classic skies; getTonemapMix gives
        // 0 for classic ones.
        u.TonemapMix = classic ? 0f : settings.TonemapMix;
        u.TonemapType = settings.TonemapType;
        u.Exposure = Math.Clamp(settings.Exposure, 0.5f, 4f);

        float expMin = 1f, expMax = 1f;
        if (settings.UseExposureSkySettings)
        {
            if (settings.DynamicExposureEnabled)
            {
                expMin = sky.GetHdrOffset(aa) - sky.GetHdrMin(aa);
                expMax = sky.GetHdrOffset(aa) + sky.GetHdrMax(aa);
            }
            else
            {
                expMin = expMax = sky.GetHdrOffset(aa);
            }
        }
        else if (settings.DynamicExposureEnabled && probeAA > 0f)
        {
            float s = MathF.Sqrt(g) * 2f;
            if (s > 1f)
            {
                expMin = 1f / s;
                expMax = s;
            }
        }
        u.DiffuseLuminanceScale = settings.DiffuseLuminanceScale;
        u.CasSharpness = settings.HdrEnabled ? settings.CasSharpness : 0f;
        u.SmaaQuality = settings.FsaaType == 2 ? Math.Clamp(settings.FsaaQuality, 0, 3) : -1;
        u.DynamicExposureParams = new Vector4(settings.DynamicExposureCoefficient, expMin, expMax, settings.DynamicExposureSpeedError);
        u.DynamicExposureParams2 = new Vector4(sky.GetHdrOffset(aa), expMin, expMax, settings.DynamicExposureSpeedTarget);
        return u;
    }

    /// <summary>The A/B texture rule the viewer's draw pools apply: blend only when both differ.</summary>
    internal static float PairBlend(UUID a, UUID b, float blend)
    {
        if (a != UUID.Zero && (b == UUID.Zero || a == b)) return 0f;
        if (b != UUID.Zero && a == UUID.Zero) return 0f;
        return a != b ? blend : 0f;
    }

    /// <summary>
    /// Stand-in for the viewer's uptime clocks (water time, stars): seconds since the start of the
    /// current UTC hour, so values stay small enough for float precision and a still at time T is
    /// reproducible.
    /// </summary>
    public static double AnimationClock(double unixSeconds) => unixSeconds - Math.Floor(unixSeconds / 3600.0) * 3600.0;

    /// <summary>
    /// The sun or moon billboard corners relative to the camera (add the camera position), from
    /// the body's rotation and scale (LLVOSky::updateHeavenlyBodyGeometry). Moon disc radius is 0.9x the sun's.
    /// </summary>
    public static HeavenlyBodyQuad HeavenlyBody(Quaternion rotation, float scale, bool moon)
    {
        const float horizonDist = 1024f;
        const float bodyDist = horizonDist - 20f;
        const float bodyFactor = 0.1f;
        float diskRadius = moon ? 0.5f * 0.9f : 0.5f;

        var toDir = EnvMath.Rotate(Vector3.UnitX, rotation);
        var right = Vector3.Cross(toDir, Vector3.UnitZ);
        var up = Vector3.Cross(right, toDir);
        if (Vector3.Dot(toDir, Vector3.UnitZ) > 0.99f)
        {
            right = EnvMath.Rotate(-Vector3.UnitY, rotation);
            up = EnvMath.Rotate(Vector3.UnitZ, rotation);
        }
        var drawPos = toDir * bodyDist;
        right = EnvMath.LLNormalize(right);
        up = EnvMath.LLNormalize(up);

        float enlarge = 1f - toDir.Z;
        float horiz = 1f + enlarge * 0.3f;
        float vert = 1f + enlarge * 0.2f;
        var sr = horiz * scale * bodyDist * bodyFactor * diskRadius * right;
        var su = vert * scale * bodyDist * bodyFactor * diskRadius * up;
        return new HeavenlyBodyQuad(drawPos - sr + su, drawPos - sr - su, drawPos + sr + su, drawPos + sr - su);
    }
}
