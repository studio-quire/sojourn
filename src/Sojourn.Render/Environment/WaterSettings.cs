using System.Numerics;
using LibreMetaverse.StructuredData;
using UUID = LibreMetaverse.UUID;

namespace Sojourn.Render.Environment;

/// <summary>An EEP water, ported from LLSettingsWater (llinventory/llsettingswater.cpp).</summary>
public sealed class WaterSettings
{
    public static readonly UUID DefaultAssetId = new("59d1a851-47e7-0e5f-1ed7-6b715154f41a");
    public static readonly UUID DefaultNormalMapId = new("822ded49-9a6c-f61c-cb89-6df54f42cdf4");
    public static readonly UUID DefaultTransparentTextureId = new("2bfd3884-7e27-69b9-ba3a-3e673f680004");
    public static readonly UUID DefaultOpaqueTextureId = new("43c32285-d658-1793-c123-bf86315de055");

    public string Name { get; set; } = "";
    public UUID AssetId { get; set; }

    public float BlurMultiplier { get; set; }
    /// <summary>water_fog_color, sRGB.</summary>
    public Vector3 FogColor { get; set; }
    public float FogDensity { get; set; }
    /// <summary>underwater_fog_mod.</summary>
    public float FogMod { get; set; }
    public float FresnelOffset { get; set; }
    public float FresnelScale { get; set; }
    public Vector3 NormalScale { get; set; }
    public float ScaleAbove { get; set; }
    public float ScaleBelow { get; set; }
    public Vector2 Wave1Direction { get; set; }
    public Vector2 Wave2Direction { get; set; }
    public UUID NormalMapId { get; set; }
    /// <summary>
    /// Always null after the viewer's validation: "transparent_texture" is missing from the water
    /// validation list, so it is stripped. Kept for completeness.
    /// </summary>
    public UUID TransparentTextureId { get; set; }

    public UUID NextNormalMapId { get; set; }
    public UUID NextTransparentTextureId { get; set; }
    public float BlendFactor { get; set; }

    /// <summary>getModifiedWaterFogDensity: under water the density is raised to underwater_fog_mod.</summary>
    public float GetModifiedFogDensity(bool underwater)
    {
        float fogDensity = FogDensity;
        float mod = FogMod;
        if (underwater && mod > 0f)
        {
            mod = Math.Clamp(mod, 0f, 10f);
            if (fogDensity < 0f && mod != MathF.Round(mod, MidpointRounding.AwayFromZero))
                fogDensity = 1f;
            fogDensity = MathF.Pow(fogDensity, mod);
        }
        return fogDensity;
    }

    public WaterSettings Clone()
    {
        var c = (WaterSettings)MemberwiseClone();
        c.NextNormalMapId = c.NextTransparentTextureId = UUID.Zero;
        c.BlendFactor = 0f;
        return c;
    }

    public static WaterSettings Blend(WaterSettings start, WaterSettings end, float blendf)
    {
        var r = start.Clone();
        r.BlendToward(end, blendf);
        return r;
    }

    /// <summary>LLSettingsWater::blend, in place.</summary>
    public void BlendToward(WaterSettings other, float t)
    {
        BlurMultiplier = EnvMath.Lerp(BlurMultiplier, other.BlurMultiplier, t);
        FogColor = EnvMath.Lerp(FogColor, other.FogColor, t);
        FogDensity = EnvMath.Lerp(FogDensity, other.FogDensity, t);
        FogMod = EnvMath.Lerp(FogMod, other.FogMod, t);
        FresnelOffset = EnvMath.Lerp(FresnelOffset, other.FresnelOffset, t);
        FresnelScale = EnvMath.Lerp(FresnelScale, other.FresnelScale, t);
        NormalScale = EnvMath.Lerp(NormalScale, other.NormalScale, t);
        ScaleAbove = EnvMath.Lerp(ScaleAbove, other.ScaleAbove, t);
        ScaleBelow = EnvMath.Lerp(ScaleBelow, other.ScaleBelow, t);
        Wave1Direction = EnvMath.Lerp(Wave1Direction, other.Wave1Direction, t);
        Wave2Direction = EnvMath.Lerp(Wave2Direction, other.Wave2Direction, t);
        NextNormalMapId = other.NormalMapId;
        NextTransparentTextureId = other.TransparentTextureId;
        BlendFactor = t;
    }

    /// <summary>EEP water LLSD with the viewer's validation; null where validate() fails.</summary>
    public static WaterSettings? FromLlsd(OSDMap map, ICollection<string>? problems = null)
    {
        var r = new SettingsReader(map, $"water '{map["name"].AsString()}'", problems);
        var w = new WaterSettings
        {
            Name = map["name"].AsString(),
            AssetId = map["asset_id"].AsUUID(),
            BlurMultiplier = r.Real("blur_multiplier", true, -0.5f, 0.5f),
            FogColor = SettingsReader.ToVec3(r.Vector("water_fog_color", true, [0f, 0f, 0f, 1f], [1f, 1f, 1f, 1f])),
            FogDensity = r.Real("water_fog_density", true, 0.001f, 100f),
            FogMod = r.Real("underwater_fog_mod", true, 0f, 20f),
            FresnelOffset = r.Real("fresnel_offset", true, 0f, 1f),
            FresnelScale = r.Real("fresnel_scale", true, 0f, 1f),
            NormalMapId = r.Uuid("normal_map", true),
            NormalScale = SettingsReader.ToVec3(r.Vector("normal_scale", true, [0f, 0f, 0f], [10f, 10f, 10f])),
            ScaleAbove = r.Real("scale_above", true, 0f, 3f),
            ScaleBelow = r.Real("scale_below", true, 0f, 3f),
            Wave1Direction = SettingsReader.ToVec2(r.Vector("wave1_direction", true, [-20f, -20f], [20f, 20f])),
            Wave2Direction = SettingsReader.ToVec2(r.Vector("wave2_direction", true, [-20f, -20f], [20f, 20f])),
            TransparentTextureId = UUID.Zero,
        };
        return r.Valid ? w : null;
    }

    /// <summary>LLSettingsWater::defaults(position); the viewer caches position 0.</summary>
    public static OSDMap DefaultsLlsd(float position = 0f)
    {
        float nso = position * 0.5f - 0.25f;
        return new OSDMap
        {
            ["blur_multiplier"] = OSD.FromReal(0.04000f),
            ["water_fog_color"] = SettingsReader.Arr(0.0156f, 0.1490f, 0.2509f),
            ["water_fog_density"] = OSD.FromReal(2.0f),
            ["underwater_fog_mod"] = OSD.FromReal(0.25f),
            ["fresnel_offset"] = OSD.FromReal(0.5f),
            ["fresnel_scale"] = OSD.FromReal(0.3999),
            ["transparent_texture"] = OSD.FromUUID(DefaultTransparentTextureId),
            ["normal_map"] = OSD.FromUUID(DefaultNormalMapId),
            ["normal_scale"] = SettingsReader.Arr(2.0f + nso, 2.0f + nso, 2.0f + nso),
            ["scale_above"] = OSD.FromReal(0.0299f),
            ["scale_below"] = OSD.FromReal(0.2000f),
            ["wave1_direction"] = SettingsReader.Arr(1.04999f, -0.42000f),
            ["wave2_direction"] = SettingsReader.Arr(1.10999f, -1.16000f),
            ["type"] = OSD.FromString("water"),
        };
    }

    /// <summary>LLSettingsVOWater::buildDefaultWater.</summary>
    public static WaterSettings CreateDefault()
    {
        var m = DefaultsLlsd();
        m["name"] = OSD.FromString("_default_");
        return FromLlsd(m) ?? throw new InvalidOperationException("built-in default water failed validation");
    }

    /// <summary>LLSettingsWater::translateLegacySettings (WindLight water preset keys). Null if nothing matched.</summary>
    public static OSDMap? TranslateLegacyLlsd(OSDMap legacy)
    {
        bool converted = false;
        var n = DefaultsLlsd();
        void Real(string from, string to)
        {
            if (SettingsReader.Present(legacy, from)) { n[to] = OSD.FromReal(legacy[from].AsReal()); converted = true; }
        }
        Real("blurMultiplier", "blur_multiplier");
        if (SettingsReader.Present(legacy, "waterFogColor")) { n["water_fog_color"] = SettingsReader.Arr(SettingsReader.LegacyVec3(legacy["waterFogColor"])); converted = true; }
        Real("waterFogDensity", "water_fog_density");
        Real("underWaterFogMod", "underwater_fog_mod");
        Real("fresnelOffset", "fresnel_offset");
        Real("fresnelScale", "fresnel_scale");
        if (SettingsReader.Present(legacy, "normalMap")) { n["normal_map"] = OSD.FromUUID(legacy["normalMap"].AsUUID()); converted = true; }
        if (SettingsReader.Present(legacy, "normScale")) { n["normal_scale"] = SettingsReader.Arr(SettingsReader.LegacyVec3(legacy["normScale"])); converted = true; }
        Real("scaleAbove", "scale_above");
        Real("scaleBelow", "scale_below");
        if (SettingsReader.Present(legacy, "wave1Dir")) { n["wave1_direction"] = SettingsReader.Arr(SettingsReader.LegacyVec2(legacy["wave1Dir"])); converted = true; }
        if (SettingsReader.Present(legacy, "wave2Dir")) { n["wave2_direction"] = SettingsReader.Arr(SettingsReader.LegacyVec2(legacy["wave2Dir"])); converted = true; }
        return converted ? n : null;
    }

    public static WaterSettings? FromLegacy(OSDMap legacy, string name = "", ICollection<string>? problems = null)
    {
        var n = TranslateLegacyLlsd(legacy);
        if (n is null)
        {
            problems?.Add($"legacy water '{name}': nothing to translate");
            return null;
        }
        n["name"] = OSD.FromString(name);
        return FromLlsd(n, problems);
    }
}
