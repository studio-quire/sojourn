using System.Numerics;
using LibreMetaverse.StructuredData;
using UUID = LibreMetaverse.UUID;

namespace Sojourn.Render.Environment;

/// <summary>One layer of an advanced-atmospherics density profile (rayleigh/mie/absorption config).</summary>
public readonly record struct DensityLayer(float Width, float ExpTerm, float ExpScale, float LinearTerm, float ConstantTerm, float Anisotropy);

/// <summary>
/// An EEP sky, ported from LLSettingsSky (llinventory/llsettingssky.cpp) + LLSettingsVOSky.
/// Values are exactly what the viewer holds after validation (clamped, defaults applied).
///
/// Haze note: the viewer's sky validation list has no top-level ambient/blue_*/haze_*/*_multiplier
/// keys, so validation strips them and only the "legacy_haze" sub-map (or the hard-coded defaults)
/// survive. Every validated sky therefore has its seven haze values flagged "legacy" and blends
/// them with plain lerps. This port relies on that and drops the flag machinery.
/// </summary>
public sealed class SkySettings
{
    // llsettingssky.cpp DOME_OFFSET / DOME_RADIUS (getDomeOffset/getDomeRadius ignore the asset)
    public const float DomeOffset = 0.96f;
    public const float DomeRadius = 15000f;

    // Hard-coded defaults for absent haze values (loadValuesFromLLSD, getParameterMap).
    public static readonly Vector3 DefaultAmbient = new(0.25f, 0.25f, 0.25f);
    public static readonly Vector3 DefaultBlueHorizon = new(0.4954f, 0.4954f, 0.6399f);
    public static readonly Vector3 DefaultBlueDensity = new(0.2447f, 0.4487f, 0.7599f);
    public const float DefaultHazeHorizon = 0.19f;
    public const float DefaultHazeDensity = 0.7f;
    public const float DefaultDistanceMultiplier = 0.8f;
    public const float DefaultDensityMultiplier = 0.0001f;

    public static readonly UUID DefaultAssetId = new("651510b8-5f4d-8991-1592-e7eeab2a5a06");
    public static readonly UUID DefaultMoonTextureId = new("d07f6eed-b96a-47cd-b51d-400ad4a1c428");
    public static readonly UUID DefaultCloudTextureId = new("1dc1368f-e8fe-f02d-a08d-9d9f11c1af6b");
    public static readonly UUID DefaultBloomTextureId = new("3c59f7fe-9dc8-47f9-8aaf-a9dd1fbc3bef");
    public static readonly UUID DefaultRainbowTextureId = new("11b4c57c-56b3-04ed-1f82-2004363882e4");
    public static readonly UUID DefaultHaloTextureId = new("12149143-f599-91a7-77ac-b52a3c0f59cd");
    /// <summary>The "blank" sun disc (GetBlankSunTextureId); EEP's default sun texture is null.</summary>
    public static readonly UUID BlankSunTextureId = new("32bfbcea-24b1-fb9d-1ef9-48a28a63730f");

    public string Name { get; set; } = "";
    public UUID AssetId { get; set; }

    public UUID SunTextureId { get; set; }
    public UUID MoonTextureId { get; set; }
    public UUID CloudTextureId { get; set; }
    public UUID BloomTextureId { get; set; }
    public UUID RainbowTextureId { get; set; }
    public UUID HaloTextureId { get; set; }

    /// <summary>Set by blending: the end keyframe's textures (mNext*TextureId). Null when not blending.</summary>
    public UUID NextSunTextureId { get; set; }
    public UUID NextMoonTextureId { get; set; }
    public UUID NextCloudTextureId { get; set; }
    public UUID NextBloomTextureId { get; set; }
    public UUID NextRainbowTextureId { get; set; }
    public UUID NextHaloTextureId { get; set; }

    /// <summary>getBlendFactor: how far a keyframe blend went (0 for an unblended sky).</summary>
    public float BlendFactor { get; set; }

    /// <summary>True when the asset has no reflection_probe_ambiance key: a "classic" (pre-PBR) sky.</summary>
    public bool CanAutoAdjust { get; set; }
    /// <summary>mReflectionProbeAmbiance (0 when CanAutoAdjust).</summary>
    public float ReflectionProbeAmbiance { get; set; }

    public Quaternion SunRotation { get; set; } = Quaternion.Identity;
    public Quaternion MoonRotation { get; set; } = Quaternion.Identity;
    public Vector3 SunlightColor { get; set; }
    public Vector3 Glow { get; set; }
    public float SunScale { get; set; } = 1f;
    public float StarBrightness { get; set; }
    public float MoonBrightness { get; set; }
    public float MoonScale { get; set; } = 1f;
    public float MaxY { get; set; }
    public float Gamma { get; set; } = 1f;
    public float CloudVariance { get; set; }
    public float CloudShadow { get; set; }
    public float CloudScale { get; set; }
    public Vector2 CloudScrollRate { get; set; }
    public Vector3 CloudPosDensity1 { get; set; }
    public Vector3 CloudPosDensity2 { get; set; }
    public Vector3 CloudColor { get; set; }

    public DensityLayer[] RayleighConfig { get; set; } = [];
    public DensityLayer[] MieConfig { get; set; } = [];
    public DensityLayer[] AbsorptionConfig { get; set; } = [];
    public float SunArcRadians { get; set; }
    public float SkyTopRadius { get; set; }
    public float SkyBottomRadius { get; set; }
    public float PlanetRadius { get; set; }
    public float MoistureLevel { get; set; }
    public float DropletRadius { get; set; }
    public float IceLevel { get; set; }

    public float HazeHorizon { get; set; } = DefaultHazeHorizon;
    public float HazeDensity { get; set; } = DefaultHazeDensity;
    public float DistanceMultiplier { get; set; } = DefaultDistanceMultiplier;
    public float DensityMultiplier { get; set; } = DefaultDensityMultiplier;
    public Vector3 BlueHorizon { get; set; } = DefaultBlueHorizon;
    public Vector3 BlueDensity { get; set; } = DefaultBlueDensity;
    public Vector3 AmbientColor { get; set; } = DefaultAmbient;

    // HDR/tonemap fields, constant per loadValuesFromLLSD; TonemapMix is overwritten by applySpecial.
    public float HdrMax { get; set; } = 2f;
    public float HdrMin { get; set; } = 0.5f;
    public float HdrOffset { get; set; } = 1f;
    public float TonemapMixValue { get; set; } = 1f;

    public float MieAnisotropy => MieConfig.Length > 0 ? MieConfig[0].Anisotropy : 0f;

    // ---------------------------------------------------------------- getters with viewer semantics

    public float GetReflectionProbeAmbiance(bool autoAdjust, float autoAdjustProbeAmbiance) =>
        autoAdjust && CanAutoAdjust ? autoAdjustProbeAmbiance : ReflectionProbeAmbiance;

    public float GetHdrMin(bool autoAdjust) => CanAutoAdjust && !autoAdjust ? 0f : HdrMin;
    public float GetHdrMax(bool autoAdjust) => CanAutoAdjust && !autoAdjust ? 0f : HdrMax;
    public float GetHdrOffset(bool autoAdjust) => CanAutoAdjust && !autoAdjust ? 1f : HdrOffset;
    public float GetTonemapMix(bool autoAdjust) => CanAutoAdjust && !autoAdjust ? 0f : TonemapMixValue;

    public Vector3 SunDirection => EnvMath.LLNormalize(EnvMath.Rotate(Vector3.UnitX, SunRotation));
    public Vector3 MoonDirection => EnvMath.LLNormalize(EnvMath.Rotate(Vector3.UnitX, MoonRotation));
    public bool IsSunUp => SunDirection.Z >= 0f;
    public bool IsMoonUp => MoonDirection.Z >= 0f;

    /// <summary>getLightDirection: the sun if up, else the moon if up, else straight down.</summary>
    public Vector3 LightDirection => IsSunUp ? SunDirection : IsMoonUp ? MoonDirection : -Vector3.UnitZ;

    /// <summary>getSunMoonGlowFactor.</summary>
    public float SunMoonGlowFactor => IsSunUp ? 1f : IsMoonUp ? MoonBrightness * 0.25f : 0f;

    /// <summary>The moon shares the sun's light colour (getMoonlightColor).</summary>
    public Vector3 MoonlightColor => SunlightColor;

    public Vector3 TotalDensity => BlueDensity + new Vector3(HazeDensity);

    public Vector3 GetLightAttenuation(float distance) =>
        (BlueDensity * 1.0f + new Vector3(HazeDensity * 0.25f)) * DensityMultiplier * distance;

    public Vector3 GetLightTransmittance(float distance) =>
        EnvMath.Exp(TotalDensity * -(DensityMultiplier * distance));

    /// <summary>
    /// calculateLightSettings: the CPU-side light colours. These do not feed the WindLight/deferred
    /// shaders directly (the shaders redo the attenuation from the raw sunlight colour), but they
    /// feed the sun disc colour, the fixed-function light and legacy paths.
    /// </summary>
    public SkyLighting ComputeLighting()
    {
        Vector3 sunlight = SunlightColor;
        Vector3 ambient = AmbientColor;
        float cloudShadow = CloudShadow;
        Vector3 lightnorm = LightDirection;

        float maxY = MaxY;
        Vector3 lightAtten = GetLightAttenuation(maxY);
        Vector3 lightTransmittance = GetLightTransmittance(maxY);

        const float limit = 1.1920929E-07f * 8.0f; // FLT_EPSILON * 8
        float lighty = MathF.Abs(lightnorm.Z);
        if (lighty >= limit)
            lighty = 1f / lighty;
        lighty = MathF.Max(limit, lighty);
        sunlight *= EnvMath.Exp(lightAtten * -1f * lighty);
        sunlight *= lightTransmittance;

        Vector3 tmpAmbient = ambient + (Vector3.One - ambient) * cloudShadow * 0.5f;

        Vector3 sunDiffuse = sunlight;
        Vector3 sunAmbient = tmpAmbient;

        float hazeHorizon = HazeHorizon;
        sunlight *= 1f - cloudShadow;
        sunlight += tmpAmbient;

        Vector3 hazeColor = BlueHorizon * BlueDensity * sunlight;
        hazeColor += new Vector3(hazeHorizon) * HazeDensity * sunlight;

        float moonBrightness = IsMoonUp ? MoonBrightness : 0.001f;
        Vector3 moonlight = MoonlightColor * EnvMath.Exp(lightAtten * -1f * lighty);
        Vector3 moonDiffuse = moonlight * lightTransmittance * moonBrightness;
        Vector3 moonAmbient = new Vector3(0.66f, 0.66f, 1.2f) * 0.0125f;

        return new SkyLighting(sunDiffuse, sunAmbient, moonDiffuse, moonAmbient, ambient, hazeColor);
    }

    // ---------------------------------------------------------------- cloning and blending

    /// <summary>replaceSettings(other): copies every value; Next* textures cleared, blend factor reset.</summary>
    public SkySettings Clone()
    {
        var c = (SkySettings)MemberwiseClone();
        c.NextSunTextureId = c.NextMoonTextureId = c.NextCloudTextureId = UUID.Zero;
        c.NextBloomTextureId = c.NextRainbowTextureId = c.NextHaloTextureId = UUID.Zero;
        c.BlendFactor = 0f;
        return c;
    }

    /// <summary>A new sky: <paramref name="start"/> blended toward <paramref name="end"/> (LLSettingsSky::blend).</summary>
    public static SkySettings Blend(SkySettings start, SkySettings end, float blendf)
    {
        var r = start.Clone();
        r.BlendToward(end, blendf);
        return r;
    }

    /// <summary>LLSettingsSky::blend, in place, quirks included.</summary>
    public void BlendToward(SkySettings other, float t)
    {
        UUID cloudNoise = CloudTextureId;
        UUID cloudNoiseNext = other.CloudTextureId;
        if (cloudNoise != UUID.Zero && cloudNoiseNext == UUID.Zero)
        {
            // destination has no cloud texture: fade coverage out, keep the current texture
            CloudShadow = EnvMath.Lerp(CloudShadow, 0f, t);
            cloudNoiseNext = cloudNoise;
        }
        else if (cloudNoise == UUID.Zero && cloudNoiseNext != UUID.Zero)
        {
            CloudShadow = EnvMath.Lerp(0f, CloudShadow, t);
            CloudTextureId = cloudNoiseNext;
        }
        else
        {
            CloudShadow = EnvMath.Lerp(CloudShadow, other.CloudShadow, t);
        }

        CanAutoAdjust = other.CanAutoAdjust;

        SunRotation = EnvMath.LLSlerp(t, SunRotation, other.SunRotation);
        MoonRotation = EnvMath.LLSlerp(t, MoonRotation, other.MoonRotation);
        SunlightColor = EnvMath.Lerp(SunlightColor, other.SunlightColor, t);
        Glow = EnvMath.Lerp(Glow, other.Glow, t);
        ReflectionProbeAmbiance = EnvMath.Lerp(ReflectionProbeAmbiance, other.ReflectionProbeAmbiance, t);
        SunScale = EnvMath.Lerp(SunScale, other.SunScale, t);
        StarBrightness = EnvMath.Lerp(StarBrightness, other.StarBrightness, t);
        MoonBrightness = EnvMath.Lerp(MoonBrightness, other.MoonBrightness, t);
        MoonScale = EnvMath.Lerp(MoonScale, other.MoonScale, t);
        MaxY = EnvMath.Lerp(MaxY, other.MaxY, t);
        Gamma = EnvMath.Lerp(Gamma, other.Gamma, t);
        CloudVariance = EnvMath.Lerp(CloudVariance, other.CloudVariance, t);
        // The viewer lerps cloud shadow a second time here (llsettingssky.cpp blend()); kept on purpose.
        CloudShadow = EnvMath.Lerp(CloudShadow, other.CloudShadow, t);
        CloudScale = EnvMath.Lerp(CloudScale, other.CloudScale, t);
        CloudScrollRate = EnvMath.Lerp(CloudScrollRate, other.CloudScrollRate, t);
        CloudPosDensity1 = EnvMath.Lerp(CloudPosDensity1, other.CloudPosDensity1, t);
        CloudPosDensity2 = EnvMath.Lerp(CloudPosDensity2, other.CloudPosDensity2, t);
        CloudColor = EnvMath.Lerp(CloudColor, other.CloudColor, t);

        SunArcRadians = EnvMath.Lerp(SunArcRadians, other.SunArcRadians, t);
        SkyTopRadius = EnvMath.Lerp(SkyTopRadius, other.SkyTopRadius, t);
        SkyBottomRadius = EnvMath.Lerp(SkyBottomRadius, other.SkyBottomRadius, t);
        MoistureLevel = EnvMath.Lerp(MoistureLevel, other.MoistureLevel, t);
        DropletRadius = EnvMath.Lerp(DropletRadius, other.DropletRadius, t);
        IceLevel = EnvMath.Lerp(IceLevel, other.IceLevel, t);
        PlanetRadius = EnvMath.Lerp(PlanetRadius, other.PlanetRadius, t);

        // Legacy haze: both sides always carry all seven as legacy values (see class remarks), so
        // lerp_legacy_float/lerp_legacy_color reduce to plain lerps.
        HazeHorizon = EnvMath.Lerp(HazeHorizon, other.HazeHorizon, t);
        HazeDensity = EnvMath.Lerp(HazeDensity, other.HazeDensity, t);
        DistanceMultiplier = EnvMath.Lerp(DistanceMultiplier, other.DistanceMultiplier, t);
        DensityMultiplier = EnvMath.Lerp(DensityMultiplier, other.DensityMultiplier, t);
        AmbientColor = EnvMath.Lerp(AmbientColor, other.AmbientColor, t);
        BlueHorizon = EnvMath.Lerp(BlueHorizon, other.BlueHorizon, t);
        BlueDensity = EnvMath.Lerp(BlueDensity, other.BlueDensity, t);

        AbsorptionConfig = LerpLayers(AbsorptionConfig, other.AbsorptionConfig, t);
        MieConfig = LerpLayers(MieConfig, other.MieConfig, t);
        RayleighConfig = LerpLayers(RayleighConfig, other.RayleighConfig, t);

        NextSunTextureId = other.SunTextureId;
        NextMoonTextureId = other.MoonTextureId;
        NextCloudTextureId = cloudNoiseNext;
        NextBloomTextureId = other.BloomTextureId;
        NextRainbowTextureId = other.RainbowTextureId;
        NextHaloTextureId = other.HaloTextureId;

        BlendFactor = t;
    }

    // interpolateSDValue over the layer arrays; simplified to index-wise lerp (unused by any shader).
    private static DensityLayer[] LerpLayers(DensityLayer[] a, DensityLayer[] b, float t)
    {
        int n = Math.Max(a.Length, b.Length);
        var r = new DensityLayer[n];
        for (int i = 0; i < n; i++)
        {
            if (i >= a.Length) { r[i] = b[i]; continue; }
            if (i >= b.Length) { r[i] = a[i]; continue; }
            r[i] = new DensityLayer(
                EnvMath.Lerp(a[i].Width, b[i].Width, t), EnvMath.Lerp(a[i].ExpTerm, b[i].ExpTerm, t),
                EnvMath.Lerp(a[i].ExpScale, b[i].ExpScale, t), EnvMath.Lerp(a[i].LinearTerm, b[i].LinearTerm, t),
                EnvMath.Lerp(a[i].ConstantTerm, b[i].ConstantTerm, t), EnvMath.Lerp(a[i].Anisotropy, b[i].Anisotropy, t));
        }
        return r;
    }

    // ---------------------------------------------------------------- parsing

    /// <summary>
    /// Builds a sky from EEP LLSD (a settings asset, or a frame of a day cycle) with the viewer's
    /// validation. Returns null where the viewer's validate() fails (it then drops the frame).
    /// </summary>
    public static SkySettings? FromLlsd(OSDMap map, ICollection<string>? problems = null)
    {
        var r = new SettingsReader(map, $"sky '{map["name"].AsString()}'", problems);
        var s = new SkySettings
        {
            Name = map["name"].AsString(),
            AssetId = map["asset_id"].AsUUID(),
        };

        float?[] z3 = [0f, 0f, 0f, null], one3 = [1f, 1f, 1f, null];

        s.BloomTextureId = r.Uuid("bloom_id", true);
        s.RainbowTextureId = r.Uuid("rainbow_id", false);
        s.HaloTextureId = r.Uuid("halo_id", false);
        s.CloudColor = SettingsReader.ToVec3(r.Vector("cloud_color", true, z3, one3));
        s.CloudPosDensity1 = SettingsReader.ToVec3(r.Vector("cloud_pos_density1", true, z3, [1f, 1f, 3f, null]));
        s.CloudPosDensity2 = SettingsReader.ToVec3(r.Vector("cloud_pos_density2", true, z3, one3));
        s.CloudScale = r.Real("cloud_scale", true, 0.001f, 3f);
        s.CloudScrollRate = SettingsReader.ToVec2(r.Vector("cloud_scroll_rate", true, [-50f, -50f], [50f, 50f]));
        s.CloudShadow = r.Real("cloud_shadow", true, 0f, 1f);
        s.CloudTextureId = r.Uuid("cloud_id", false);
        s.CloudVariance = r.Real("cloud_variance", false, 0f, 1f);
        r.Real("dome_offset", false, 0f, 1f);
        r.Real("dome_radius", false, 1000f, 2000f);
        s.Gamma = r.Real("gamma", true, 0f, 20f);
        s.Glow = SettingsReader.ToVec3(r.Vector("glow", true, [0.2f, null, -10f, null], [40f, null, 10f, null]));
        s.MaxY = r.Real("max_y", true, 0f, 10000f);
        s.MoonRotation = r.Quat("moon_rotation", true) ?? default;
        s.MoonScale = r.Real("moon_scale", false, 0.25f, 20f, 1f);
        s.MoonTextureId = r.Uuid("moon_id", false);
        s.MoonBrightness = r.Real("moon_brightness", false, 0f, 1f);
        s.StarBrightness = r.Real("star_brightness", true, 0f, 500f);
        s.SunlightColor = SettingsReader.ToVec3(r.Vector("sunlight_color", true, z3, [3f, 3f, 3f, null]));
        s.SunRotation = r.Quat("sun_rotation", true) ?? default;
        s.SunScale = r.Real("sun_scale", false, 0.25f, 20f, 1f);
        s.SunTextureId = r.Uuid("sun_id", false);
        s.PlanetRadius = r.Real("planet_radius", true, 1000f, 32768f);
        s.SkyBottomRadius = r.Real("sky_bottom_radius", true, 1000f, 32768f);
        s.SkyTopRadius = r.Real("sky_top_radius", true, 1000f, 32768f);
        s.SunArcRadians = r.Real("sun_arc_radians", true, 0f, 0.1f);
        s.MoistureLevel = r.Real("moisture_level", false, 0f, 1f);
        s.DropletRadius = r.Real("droplet_radius", false, 5f, 1000f);
        s.IceLevel = r.Real("ice_level", false, 0f, 1f);

        s.CanAutoAdjust = !r.Has("reflection_probe_ambiance");
        s.ReflectionProbeAmbiance = s.CanAutoAdjust ? 0f : r.Real("reflection_probe_ambiance", false, 0f, 10f);

        s.RayleighConfig = ReadLayers(r, "rayleigh_config", false);
        s.AbsorptionConfig = ReadLayers(r, "absorption_config", false);
        s.MieConfig = ReadLayers(r, "mie_config", true);

        var haze = r.SubMap("legacy_haze", false);
        if (haze is not null)
            ReadLegacyHaze(r, haze, s);

        return r.Valid ? s : null;
    }

    private static readonly HashSet<string> CommonKeys = ["name", "id", "hash", "asset_id", "type", "flags"];

    private static void ReadLegacyHaze(SettingsReader outer, OSDMap haze, SkySettings s)
    {
        // validateLegacyHaze: errors or stripped (unknown) keys fail the whole sky.
        var problems = new List<string>();
        var r = new SettingsReader(haze, "legacy_haze", problems);
        float?[] z3 = [0f, 0f, 0f, null], three3 = [3f, 3f, 3f, null];
        var amb = r.Vector("ambient", false, z3, three3);
        var bd = r.Vector("blue_density", false, z3, three3);
        var bh = r.Vector("blue_horizon", false, z3, three3);
        bool hasHd = r.Has("haze_density"), hasHh = r.Has("haze_horizon");
        bool hasDen = r.Has("density_multiplier"), hasDist = r.Has("distance_multiplier");
        float hd = r.Real("haze_density", false, 0f, 5f);
        float hh = r.Real("haze_horizon", false, 0f, 5f);
        float den = r.Real("density_multiplier", false, 0.0001f, 2f);
        float dist = r.Real("distance_multiplier", false, 0.0001f, 1000f);
        foreach (var key in haze.Keys)
        {
            if (!CommonKeys.Contains(key) && key is not ("ambient" or "blue_density" or "blue_horizon" or "haze_density"
                    or "haze_horizon" or "density_multiplier" or "distance_multiplier"))
                r.Invalidate($"unknown key '{key}'");
        }
        if (!r.Valid)
        {
            outer.Invalidate("legacy_haze invalid: " + string.Join("; ", problems));
            return;
        }
        if (amb is not null) s.AmbientColor = SettingsReader.ToVec3(amb);
        if (bd is not null) s.BlueDensity = SettingsReader.ToVec3(bd);
        if (bh is not null) s.BlueHorizon = SettingsReader.ToVec3(bh);
        if (hasHd) s.HazeDensity = hd;
        if (hasHh) s.HazeHorizon = hh;
        if (hasDen) s.DensityMultiplier = den;
        if (hasDist) s.DistanceMultiplier = dist;
    }

    private static DensityLayer[] ReadLayers(SettingsReader outer, string key, bool mie)
    {
        var arr = outer.Array(key, true);
        if (arr is null)
            return [];
        var layers = new List<DensityLayer>();
        foreach (var item in arr)
        {
            if (item is not OSDMap m)
            {
                outer.Invalidate($"{key}: layer is not a map");
                continue;
            }
            var problems = new List<string>();
            var r = new SettingsReader(m, key, problems);
            var layer = new DensityLayer(
                r.Real("width", false, 0f, 32768f),
                r.Real("exp_term", false, 0f, 2f),
                r.Real("exp_scale", false, -1f, 1f),
                r.Real("linear_term", false, 0f, 2f),
                r.Real("constant_term", false, 0f, 1f),
                mie ? r.Real("anisotropy", false, 0f, 1f) : 0f);
            foreach (var k in m.Keys)
            {
                if (!CommonKeys.Contains(k) && k is not ("width" or "exp_term" or "exp_scale" or "linear_term" or "constant_term")
                    && !(mie && k == "anisotropy"))
                    r.Invalidate($"unknown key '{k}'");
            }
            if (!r.Valid)
                outer.Invalidate($"{key} invalid: " + string.Join("; ", problems));
            layers.Add(layer);
        }
        return layers.ToArray();
    }

    /// <summary>
    /// LLSettingsSky::defaults(position) as LLSD. The viewer caches the first call, which is made
    /// with position 0, so every caller gets the position-0 sky; that is reproduced by default.
    /// </summary>
    public static OSDMap DefaultsLlsd(float position = 0f)
    {
        float azimuth = MathF.PI * position + 80f * (MathF.PI / 180f);
        float altitude = MathF.PI * position;
        // The viewer passes (altitude, azimuth) swapped, so this sun sits 80 degrees up, toward east.
        var sunquat = EnvMath.AzimuthAltitudeToQuat(altitude, azimuth);
        var moonquat = EnvMath.AzimuthAltitudeToQuat(altitude + MathF.PI * 0.125f, azimuth + MathF.PI * 0.125f);

        var m = new OSDMap
        {
            ["cloud_color"] = SettingsReader.Arr(0.4099f, 0.4099f, 0.4099f, 0f),
            ["cloud_pos_density1"] = SettingsReader.Arr(1.0f, 0.5260f, 1.0f, 0f),
            ["cloud_pos_density2"] = SettingsReader.Arr(1.0f, 0.5260f, 1.0f, 0f),
            ["cloud_scale"] = OSD.FromReal(0.4199),
            ["cloud_scroll_rate"] = SettingsReader.Arr(0.2f, 0.01f),
            ["cloud_shadow"] = OSD.FromReal(0.2699),
            ["cloud_variance"] = OSD.FromReal(0.0),
            ["dome_offset"] = OSD.FromReal(0.96f),
            ["dome_radius"] = OSD.FromReal(15000f),
            ["gamma"] = OSD.FromReal(1.0),
            ["glow"] = SettingsReader.Arr(5.000f, 0.0010f, -0.4799f, 1.0f),
            ["max_y"] = OSD.FromReal(1605),
            ["moon_rotation"] = SettingsReader.Arr(moonquat),
            ["moon_brightness"] = OSD.FromReal(0.5f),
            ["star_brightness"] = OSD.FromReal(250.0),
            ["sunlight_color"] = SettingsReader.Arr(0.7342f, 0.7815f, 0.8999f, 0f),
            ["sun_rotation"] = SettingsReader.Arr(sunquat),
            ["bloom_id"] = OSD.FromUUID(DefaultBloomTextureId),
            ["cloud_id"] = OSD.FromUUID(DefaultCloudTextureId),
            ["moon_id"] = OSD.FromUUID(DefaultMoonTextureId),
            ["sun_id"] = OSD.FromUUID(UUID.Zero),
            ["rainbow_id"] = OSD.FromUUID(DefaultRainbowTextureId),
            ["halo_id"] = OSD.FromUUID(DefaultHaloTextureId),
            ["type"] = OSD.FromString("sky"),
            ["planet_radius"] = OSD.FromReal(6360.0f),
            ["sky_bottom_radius"] = OSD.FromReal(6360.0f),
            ["sky_top_radius"] = OSD.FromReal(6420.0f),
            ["sun_arc_radians"] = OSD.FromReal(0.00045f),
            ["moisture_level"] = OSD.FromReal(0.0f),
            ["droplet_radius"] = OSD.FromReal(800.0f),
            ["ice_level"] = OSD.FromReal(0.0f),
            // Present (as 0) in the defaults, so the built-in sky is NOT a classic/auto-adjust sky.
            ["reflection_probe_ambiance"] = OSD.FromReal(0.0f),
            ["rayleigh_config"] = LayerArray(new DensityLayer(0f, 1f, -1f / 8000f, 0f, 0f, 0f)),
            ["mie_config"] = LayerArray(new DensityLayer(0f, 1f, -1f / 1200f, 0f, 0f, 0.8f)),
            ["absorption_config"] = LayerArray(
                new DensityLayer(25000f, 0f, 0f, -1f / 25000f, -2f / 3f, 0f),
                new DensityLayer(0f, 0f, 0f, -1f / 15000f, 8f / 3f, 0f)),
        };
        return m;
    }

    private static OSDArray LayerArray(params DensityLayer[] layers)
    {
        var a = new OSDArray();
        foreach (var l in layers)
        {
            var m = new OSDMap
            {
                ["width"] = OSD.FromReal(l.Width),
                ["exp_term"] = OSD.FromReal(l.ExpTerm),
                ["exp_scale"] = OSD.FromReal(l.ExpScale),
                ["linear_term"] = OSD.FromReal(l.LinearTerm),
                ["constant_term"] = OSD.FromReal(l.ConstantTerm),
            };
            if (l.Anisotropy != 0f)
                m["anisotropy"] = OSD.FromReal(l.Anisotropy);
            a.Add(m);
        }
        return a;
    }

    /// <summary>LLSettingsVOSky::buildDefaultSky: the viewer's built-in sky (sun 80 degrees up).</summary>
    public static SkySettings CreateDefault()
    {
        var m = DefaultsLlsd();
        m["name"] = OSD.FromString("_default_");
        return FromLlsd(m) ?? throw new InvalidOperationException("built-in default sky failed validation");
    }

    // ---------------------------------------------------------------- legacy WindLight

    /// <summary>
    /// LLSettingsSky::translateLegacySettings: a pre-EEP WindLight sky preset (as in the legacy
    /// EnvironmentSettings message or app_settings/windlight/skies/*.xml) to EEP LLSD.
    /// Returns null when nothing was recognised.
    /// </summary>
    public static OSDMap? TranslateLegacyLlsd(OSDMap legacy)
    {
        bool converted = false;
        var n = DefaultsLlsd();

        var haze = new OSDMap();
        if (Has(legacy, "ambient")) haze["ambient"] = SettingsReader.Arr(SettingsReader.LegacyVec3(legacy["ambient"]));
        if (Has(legacy, "blue_density")) haze["blue_density"] = SettingsReader.Arr(SettingsReader.LegacyVec3(legacy["blue_density"]));
        if (Has(legacy, "blue_horizon")) haze["blue_horizon"] = SettingsReader.Arr(SettingsReader.LegacyVec3(legacy["blue_horizon"]));
        foreach (var key in new[] { "density_multiplier", "distance_multiplier", "haze_density", "haze_horizon" })
            if (Has(legacy, key)) haze[key] = OSD.FromReal(SettingsReader.LegacyReal(legacy[key], 0));
        if (haze.Count > 0)
        {
            n["legacy_haze"] = haze;
            converted = true;
        }

        if (Has(legacy, "cloud_color")) { n["cloud_color"] = SettingsReader.Arr(SettingsReader.LegacyVec3(legacy["cloud_color"])); converted = true; }
        if (Has(legacy, "cloud_pos_density1")) { n["cloud_pos_density1"] = SettingsReader.Arr(SettingsReader.LegacyVec3(legacy["cloud_pos_density1"])); converted = true; }
        if (Has(legacy, "cloud_pos_density2")) { n["cloud_pos_density2"] = SettingsReader.Arr(SettingsReader.LegacyVec3(legacy["cloud_pos_density2"])); converted = true; }
        if (Has(legacy, "cloud_scale")) { n["cloud_scale"] = OSD.FromReal(SettingsReader.LegacyReal(legacy["cloud_scale"], 0)); converted = true; }
        if (Has(legacy, "cloud_scroll_rate"))
        {
            var scroll = SettingsReader.LegacyVec2(legacy["cloud_scroll_rate"]) - new Vector2(10f, 10f);
            if (Has(legacy, "enable_cloud_scroll") && legacy["enable_cloud_scroll"] is OSDArray en)
            {
                if (!(en.Count > 0 && en[0].AsBoolean())) scroll.X = 0f;
                if (!(en.Count > 1 && en[1].AsBoolean())) scroll.Y = 0f;
            }
            n["cloud_scroll_rate"] = SettingsReader.Arr(scroll);
            converted = true;
        }
        if (Has(legacy, "cloud_shadow")) { n["cloud_shadow"] = OSD.FromReal(SettingsReader.LegacyReal(legacy["cloud_shadow"], 0)); converted = true; }
        if (Has(legacy, "gamma")) { n["gamma"] = OSD.FromReal(SettingsReader.LegacyReal(legacy["gamma"], 0)); converted = true; }
        if (Has(legacy, "glow")) { n["glow"] = SettingsReader.Arr(SettingsReader.LegacyVec3(legacy["glow"])); converted = true; }
        if (Has(legacy, "max_y")) { n["max_y"] = OSD.FromReal(SettingsReader.LegacyReal(legacy["max_y"], 0)); converted = true; }
        if (Has(legacy, "star_brightness")) { n["star_brightness"] = OSD.FromReal(SettingsReader.LegacyReal(legacy["star_brightness"]) * 250f); converted = true; }
        if (Has(legacy, "sunlight_color"))
        {
            // LLColor4(legacy).getValue(): four components (missing alpha reads as 1)
            var a = legacy["sunlight_color"] as OSDArray;
            float alpha = a is not null && a.Count > 3 ? (float)a[3].AsReal() : 1f;
            var c = SettingsReader.LegacyVec3(legacy["sunlight_color"]);
            n["sunlight_color"] = SettingsReader.Arr(c.X, c.Y, c.Z, alpha);
            converted = true;
        }
        foreach (var key in new[] { "planet_radius", "sky_bottom_radius", "sky_top_radius", "sun_arc_radians" })
            if (Has(legacy, key)) { n[key] = OSD.FromReal(SettingsReader.LegacyReal(legacy[key])); converted = true; }

        if (Has(legacy, "east_angle") && Has(legacy, "sun_angle"))
        {
            // counter-clockwise radians from WindLight's clockwise east angle
            float azimuth = -SettingsReader.LegacyReal(legacy["east_angle"]);
            float altitude = SettingsReader.LegacyReal(legacy["sun_angle"]);
            var sunquat = EnvMath.AzimuthAltitudeToQuat(azimuth, altitude);
            // WindLight's moon was diametrically opposed to the sun
            var moonquat = EnvMath.AzimuthAltitudeToQuat(azimuth + MathF.PI, -altitude);
            n["sun_rotation"] = SettingsReader.Arr(sunquat);
            n["moon_rotation"] = SettingsReader.Arr(moonquat);
            converted = true;
        }

        return converted ? n : null;
    }

    /// <summary>Legacy WindLight sky preset to a validated sky (buildFromLegacyPreset).</summary>
    public static SkySettings? FromLegacy(OSDMap legacy, string name = "", ICollection<string>? problems = null)
    {
        var n = TranslateLegacyLlsd(legacy);
        if (n is null)
        {
            problems?.Add($"legacy sky '{name}': nothing to translate");
            return null;
        }
        n["name"] = OSD.FromString(name);
        return FromLlsd(n, problems);
    }

    private static bool Has(OSDMap m, string key) => SettingsReader.Present(m, key);
}

/// <summary>Output of LLSettingsSky::calculateLightSettings (all in WindLight's sRGB-ish space).</summary>
public readonly record struct SkyLighting(
    Vector3 SunDiffuse,
    Vector3 SunAmbient,
    Vector3 MoonDiffuse,
    Vector3 MoonAmbient,
    Vector3 TotalAmbient,
    Vector3 HazeColor);
