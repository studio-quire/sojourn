using System.Runtime.InteropServices;
using Sojourn.Render.Environment;

namespace Sojourn.Render.Frame;

/// <summary>
/// Environment uniforms for one view, named after the viewer's GLSL uniforms (ported shaders read SKY.name).
/// Filled from the environment port's SkyUniforms/WaterUniforms by <see cref="Build"/>. Where the viewer feeds
/// different values to different programs (raw vs normalised sunlight, classic mode for local lights), both are here.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuSky
{
    // atmospherics, deferred-program values (softenLight, haze, alpha, water)
    public Vector4 lightnorm;
    public Vector3 sunlight_color;
    public float sun_moon_glow_factor;
    public Vector3 moonlight_color;
    public int sun_up_factor;
    public Vector3 ambient_color;
    public float haze_horizon;
    public Vector3 blue_horizon;
    public float haze_density;
    public Vector3 blue_density;
    public float cloud_shadow;
    public float density_multiplier;
    public float distance_multiplier;
    public float max_y;
    public float scene_light_strength;
    public Vector3 glow;
    public float sky_sunlight_scale;
    public float sky_ambient_scale;
    public int classic_mode;
    public float sky_hdr_scale;
    public float gamma;

    // the sky dome programs get the raw sunlight colour (LLSettingsVOSky applySpecial group SKY)
    public Vector3 sky_sunlight_color;
    public int local_light_classic_mode;
    public Vector3 sky_moonlight_color;
    public float cam_height;           // camPosLocal.y of the dome draws (dome_offset * dome_radius)

    // eye-space light directions
    public Vector3 sun_dir;
    public float moisture_level;
    public Vector3 moon_dir;
    public float droplet_radius;

    // clouds
    public Vector3 cloud_pos_density1;
    public float cloud_scale;
    public Vector3 cloud_pos_density2;
    public float cloud_variance;
    public Vector3 cloud_color;
    public float ice_level;
    public float moon_brightness;
    public float star_brightness;
    public float dome_radius;
    public float clouds_visible;
    public float cloud_blend_factor;
    public Vector3 cloud_pad;

    // sun and moon discs, stars, rainbow and halo (LLDrawPoolWLSky, LLVOSky::updateHeavenlyBodyGeometry): quads as
    // camera-relative centre and half-extent vectors, region frame; texture slots -1 = none (set by the scene)
    public Vector3 sun_disc_center;
    public int sun_tex_a;
    public Vector3 sun_disc_right;
    public int sun_tex_b;
    public Vector3 sun_disc_up;
    public float sun_disc_blend;
    public Vector3 moon_disc_center;
    public int moon_tex;
    public Vector3 moon_disc_right;
    public float moon_dir_z;           // moonF's fade near the horizon
    public Vector3 moon_disc_up;
    public int heavenly_flags;         // 1 sun disc, 2 moon disc, 4 stars
    public int star_tex;
    public float star_time;            // starsV "time"
    public float star_rotation;        // radians about +Z
    public int rainbow_tex;
    public int halo_tex;
    public Vector3 heavenly_pad;

    // water fog and surface
    public Vector4 waterPlane;          // eye space
    public Vector4 waterFogColor;       // sRGB rgb
    public float waterFogDensity;
    public float waterFogKS;
    public float waterSign;
    public float fresnelScale;
    public float fresnelOffset;
    public float blurMultiplier;
    public float refScale;
    public float water_time;
    public Vector3 normScale;
    public float water_height;
    public Vector2 waveDir1;
    public Vector2 waveDir2;
    public Vector3 water_specular;
    public int camera_under_water;
    public Vector3 water_light_dir;    // "lightDir" of waterV/waterF (agent space)
    public float water_blend_factor;   // blend between the two water normal maps
    public Vector3 eye_vec;            // "eyeVec": camera position, region coords
    public float water_cam_height;     // "waterHeight" uniform: camera height above water

    // sun shadows (shadowUtil.glsl) and SSAO (aoUtil.glsl, softenLightF HAS_SSAO)
    public Matrix4x4 shadow_matrix0, shadow_matrix1, shadow_matrix2, shadow_matrix3;
    public Matrix4x4 shadow_viewproj0, shadow_viewproj1, shadow_viewproj2, shadow_viewproj3;   // region -> cascade clip (caster rendering)
    public Vector4 shadow_clip;
    public Vector2 shadow_res;
    public float shadow_bias;
    public float shadow_offset;
    public int shadows_on;
    // the coarse far sun cascade (ours; shadows.glsl, SunShadowSetup.Far): eye -> its map, region -> its clip, and
    // where it takes over, starts fading, ends (radii), w 1 when on; its map's size
    public Matrix4x4 shadow_matrix6, shadow_viewproj6;
    public Vector4 shadow_far;
    public Vector2 shadow_far_res;
    // projector shadows (RenderShadowDetail 2): two maps for the two highest-priority projectors
    public Matrix4x4 shadow_matrix4, shadow_matrix5;       // eye -> projector shadow texture coords
    public Matrix4x4 shadow_viewproj4, shadow_viewproj5;   // region -> projector clip (caster rendering)
    public Vector2 proj_shadow_res;
    public float spot_shadow_bias, spot_shadow_offset;
    public int spot_shadows;                                // bit i: projector shadow map i is live
    public int ssao_on;
    public float ssao_radius;
    public float ssao_max_radius;
    public float ssao_factor;
    public float ssao_factor_inv;
    public float ssao_irradiance_scale;
    public float ssao_irradiance_max;
    public Vector3 ssao_effect_row0;
    public float blur_dist_factor;
    public Vector3 ssao_effect_row1;
    public float blur_kern_scale;
    public Vector3 ssao_effect_row2;
    public float blur_pad;
    public Vector4 blur_kern0, blur_kern1, blur_kern2, blur_kern3;   // xyz = (x weight, y weight, offset)

    // reflection probes (probes.glsl): the view's probe list (GpuProbe[]) and a region-xy grid of probe indices
    // (uint[]: grid_w*grid_h+1 cell starts, then indices), both in the frame arena (see ProbeManager.WriteList)
    public int probes_on;
    public int probe_default;              // 1: list entry 0 is the default probe
    public uint probe_list_lo, probe_list_hi;
    public uint probe_cells_lo, probe_cells_hi;
    public Vector2 probe_grid_origin;
    public float probe_grid_cell;
    public int probe_grid_w, probe_grid_h;
    public float max_probe_lod;
    public float probe_capture_far;        // > 0 in probe captures: terrain clipped beyond this eye depth
    public float probe_pad;

    // a mirror (the viewer's hero probe; Realtime/FrameRenderer.Mirror.cs): this eye's reflection, rendered from the
    // eye mirrored in the mirror's plane into an image the eye's shape (a pixel at u finds its reflection at 1 - u)
    public Matrix4x4 hero_box;             // region -> the mirror probe's unit box
    public Vector4 hero_plane;             // region space, n and d: in front where dot(n, p) + d > 0
    public int hero_on;
    public int hero_tex;                   // bindless slot of the reflection image
    public float hero_mips;                // its mip levels (the viewer's heroMipCount)
    public float hero_pad;

    // post
    public float probe_ambiance;
    public float exposure;
    public float tonemap_mix;
    public int tonemap_type;
    public int legacy_gamma_no_post;   // LEGACY_GAMMA variant (probe ambiance 0)
    public int no_post;                // NO_POST variant
    public float dynamic_exposure_min, dynamic_exposure_max, dynamic_exposure_coefficient;
    public float diffuse_luminance_scale;
    public float cas_sharpness;
    public int smaa_quality;           // SMAA preset 0-3, -1 off

    /// <summary>SSAO and light-map blur uniforms at the viewer's settings.xml defaults (bindDeferredShader,
    /// renderDeferredLighting's soften-shadow pass).</summary>
    public void SetSsaoDefaults()
    {
        ssao_on = 1;                       // RenderDeferredSSAO
        ssao_radius = 500f;                // RenderSSAOScale
        ssao_max_radius = 200f;            // RenderSSAOMaxScale
        ssao_factor = 0.30f;               // RenderSSAOFactor
        ssao_factor_inv = 1f / ssao_factor;
        ssao_irradiance_scale = 0.6f;      // RenderSSAOIrradianceScale
        ssao_irradiance_max = 0.18f;       // RenderSSAOIrradianceMax
        // RenderSSAOEffect (0.8, 1.0, 0): matrix_diag = (e0 + 2 e1) / 3, non-diagonal (e0 - e1) / 3
        float diag = (0.8f + 2f * 1.0f) / 3f, nondiag = (0.8f - 1.0f) / 3f;
        ssao_effect_row0 = new Vector3(diag, nondiag, nondiag);
        ssao_effect_row1 = new Vector3(nondiag, diag, nondiag);
        ssao_effect_row2 = new Vector3(nondiag, nondiag, diag);
        // RenderShadowGaussian (3, 2), RenderShadowBlurSize 1.4, RenderShadowBlurDistFactor 0, kern_length 4
        static float Gauss(float x, float o) => 1f / MathF.Sqrt(2f * MathF.PI * o * o) * MathF.Exp(-(x * x) / (2f * o * o));
        blur_kern0 = new Vector4(Gauss(0, 3), Gauss(0, 2), 0, 0);
        blur_kern1 = new Vector4(Gauss(1, 3), Gauss(1, 2), 1, 0);
        blur_kern2 = new Vector4(Gauss(2, 3), Gauss(2, 2), 2, 0);
        blur_kern3 = new Vector4(Gauss(3, 3), Gauss(3, 2), 3, 0);
        blur_kern_scale = 1.4f * (4 / 2f - 0.5f);
        blur_dist_factor = 0f;
        shadow_bias = -0.002f;             // RenderShadowBias (the setup may override)
        shadow_offset = 0.01f;             // RenderShadowOffset
    }

    public const int SunDiscFlag = 1, MoonDiscFlag = 2, StarsFlag = 4;

    // LLVOSky: HEAVENLY_BODY_DIST (HORIZON_DIST - 20), HEAVENLY_BODY_FACTOR, SUN_DISK_RADIUS, MOON_DISK_RADIUS
    const float BodyDist = 1024f - 20f, BodyFactor = 0.1f, SunDiskRadius = 0.5f, MoonDiskRadius = SunDiskRadius * 0.9f;

    void SetHeavenlyBodies(in SkyUniforms s)
    {
        (sun_disc_center, sun_disc_right, sun_disc_up) = BodyQuad(s.SunRotation, s.SunScale, SunDiskRadius);
        sun_disc_center -= new Vector3(0, 0, 50);   // sunDiscV lowers the disc 50 m
        (moon_disc_center, moon_disc_right, moon_disc_up) = BodyQuad(s.MoonRotation, s.MoonScale, MoonDiskRadius);
        moon_dir_z = s.MoonDirection.Z;
        sun_disc_blend = s.SunDiscBlendFactor;
        heavenly_flags = (s.SunDiscVisible ? SunDiscFlag : 0) | (s.MoonDiscVisible ? MoonDiscFlag : 0) | (s.StarsVisible ? StarsFlag : 0);
        star_time = s.StarTime;
        star_rotation = s.StarRotationDegrees * MathF.PI / 180f;
        sun_tex_a = sun_tex_b = moon_tex = star_tex = rainbow_tex = halo_tex = -1;
    }

    /// <summary>LLVOSky::updateHeavenlyBodyGeometry: the disc quad's centre and half extents.</summary>
    static (Vector3 Center, Vector3 Right, Vector3 Up) BodyQuad(Quaternion rot, float scale, float diskRadius)
    {
        var toDir = EnvMath.Rotate(Vector3.UnitX, rot);
        var right = Vector3.Cross(toDir, Vector3.UnitZ);
        var up = Vector3.Cross(right, toDir);
        if (toDir.Z > 0.99f)
        {
            right = EnvMath.Rotate(-Vector3.UnitY, rot);
            up = EnvMath.Rotate(Vector3.UnitZ, rot);
        }
        right = Vector3.Normalize(right);
        up = Vector3.Normalize(up);
        float enlarge = 1f - toDir.Z;
        float size = scale * BodyDist * BodyFactor * diskRadius;
        return (toDir * BodyDist, right * (1f + enlarge * 0.3f) * size, up * (1f + enlarge * 0.2f) * size);
    }

    /// <summary>Viewer uniforms for a view from the evaluated environment.</summary>
    public static GpuSky Build(in SkyUniforms s, in WaterUniforms w, Matrix4x4 view, float waterHeight, Vector3 cameraPos)
    {
        var g = new GpuSky
        {
            lightnorm = new Vector4(s.LightNorm, 0),
            sunlight_color = s.DeferredSunlightColor,
            moonlight_color = s.DeferredMoonlightColor,
            sky_sunlight_color = s.SunlightColor,
            sky_moonlight_color = s.MoonlightColor,
            sun_moon_glow_factor = s.SunMoonGlowFactor,
            sun_up_factor = s.SunUpFactor,
            ambient_color = s.AmbientColor,
            haze_horizon = s.HazeHorizon,
            haze_density = s.HazeDensity,
            blue_horizon = s.BlueHorizon,
            blue_density = s.BlueDensity,
            cloud_shadow = s.CloudShadow,
            density_multiplier = s.DensityMultiplier,
            distance_multiplier = s.DistanceMultiplier,
            max_y = s.MaxY,
            scene_light_strength = s.SceneLightStrength,
            glow = s.Glow,
            sky_sunlight_scale = s.SkySunlightScale,
            sky_ambient_scale = s.SkyAmbientScale,
            classic_mode = s.ClassicMode,
            local_light_classic_mode = s.LocalLightClassicMode,
            sky_hdr_scale = s.SkyHdrScale,
            gamma = s.Gamma,
            cam_height = s.SkyDomeCameraPosition.Y,
            dome_radius = SkySettings.DomeRadius,
            sun_dir = Vector3.Normalize(Vector3.TransformNormal(s.DeferredSunDirection, view)),
            moon_dir = Vector3.Normalize(Vector3.TransformNormal(s.DeferredMoonDirection, view)),
            moisture_level = s.MoistureLevel,
            droplet_radius = s.DropletRadius,
            ice_level = s.IceLevel,
            cloud_pos_density1 = s.CloudPosDensity1,
            cloud_pos_density2 = s.CloudPosDensity2,
            cloud_scale = s.CloudScale,
            cloud_variance = s.CloudVariance,
            cloud_color = s.CloudColor,
            clouds_visible = s.CloudsVisible ? 1 : 0,
            cloud_blend_factor = s.CloudBlendFactor,
            moon_brightness = s.MoonBrightness,
            star_brightness = s.StarAlpha,
            probe_ambiance = s.ReflectionProbeAmbiance,
            exposure = s.Exposure,
            tonemap_mix = s.TonemapMix,
            tonemap_type = s.TonemapType,
            legacy_gamma_no_post = s.LegacyGammaNoPost ? 1 : 0,
            no_post = s.NoPost ? 1 : 0,
            dynamic_exposure_coefficient = s.DynamicExposureParams.X,
            dynamic_exposure_min = s.DynamicExposureParams.Y,
            dynamic_exposure_max = s.DynamicExposureParams.Z,
            diffuse_luminance_scale = s.DiffuseLuminanceScale,
            cas_sharpness = s.CasSharpness,
            smaa_quality = s.SmaaQuality,

            waterFogColor = w.WaterFogColor,
            waterFogDensity = w.WaterFogDensity,
            waterFogKS = w.WaterFogKS,
            fresnelScale = w.FresnelScale,
            fresnelOffset = w.FresnelOffset,
            blurMultiplier = w.BlurMultiplier,
            refScale = w.RefScale,
            water_time = w.Time,
            normScale = w.NormScale,
            waveDir1 = w.WaveDir1,
            waveDir2 = w.WaveDir2,
            water_specular = w.Specular,
            camera_under_water = w.CameraUnderWater ? 1 : 0,
            water_light_dir = w.LightDir,
            water_blend_factor = w.NormalMapBlendFactor,
            water_height = waterHeight,
        };
        g.SetHeavenlyBodies(s);
        g.waterPlane = EnvMath.EyeSpacePlane(view, Vector3.UnitZ, new Vector3(0, 0, waterHeight));
        g.SetSsaoDefaults();
        g.waterSign = w.CameraUnderWater ? -1f : 1f;
        g.eye_vec = cameraPos;
        g.water_cam_height = cameraPos.Z - waterHeight;
        return g;
    }
}
