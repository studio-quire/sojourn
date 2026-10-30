namespace Sojourn.Render.Environment;

/// <summary>
/// The viewer graphics preferences that change how environment settings become uniforms.
/// Defaults are the stock values in newview/app_settings/settings.xml (not the LLCachedControl
/// fallbacks in code, which differ for several of these).
/// </summary>
public sealed record ViewerRenderSettings
{
    /// <summary>RenderHDREnabled.</summary>
    public bool HdrEnabled { get; init; } = true;
    /// <summary>RenderSkySunlightScale (used when HDR is off).</summary>
    public float SkySunlightScale { get; init; } = 1.0f;
    /// <summary>RenderHDRSkySunlightScale (used when HDR is on).</summary>
    public float HdrSkySunlightScale { get; init; } = 1.0f;
    /// <summary>RenderSkyAmbientScale.</summary>
    public float SkyAmbientScale { get; init; } = 1.0f;
    /// <summary>RenderSunDynamicRange (feeds scene_light_strength).</summary>
    public float SunDynamicRange { get; init; } = 1.0f;

    /// <summary>RenderSkyAutoAdjustLegacy: treat classic skies as PBR with probe ambiance (off by default).</summary>
    public bool AutoAdjustLegacySky { get; init; } = false;
    public float AutoAdjustAmbientScale { get; init; } = 1.0f;
    public float AutoAdjustHdrScale { get; init; } = 1.0f;
    public float AutoAdjustBlueHorizonScale { get; init; } = 1.0f;
    public float AutoAdjustBlueDensityScale { get; init; } = 1.0f;
    public float AutoAdjustSunColorScale { get; init; } = 1.0f;
    /// <summary>RenderSkyAutoAdjustProbeAmbiance (LLSettingsSky::sAutoAdjustProbeAmbiance).</summary>
    public float AutoAdjustProbeAmbiance { get; init; } = 0.01f;

    /// <summary>LLPipeline::sReflectionProbesEnabled; when off a PBR sky's probe ambiance is forced to 1.</summary>
    public bool ReflectionProbesEnabled { get; init; } = true;
    /// <summary>RenderDesaturateIrradiance (irradiance probe passes take grey blue_horizon/blue_density).</summary>
    public bool DesaturateIrradiance { get; init; } = true;

    /// <summary>RenderTonemapMix (applied to non-classic skies).</summary>
    public float TonemapMix { get; init; } = 0.7f;
    /// <summary>RenderTonemapType: 0 Khronos PBR Neutral, 1 ACES (Hill).</summary>
    public int TonemapType { get; init; } = 1;
    /// <summary>
    /// gSnapshotNoPost: render the way LEAP's LLViewerWindow saveSnapshot does, which is how Tessel-style agents
    /// see (sl.py look/shot, refshot). LLViewerWindow::saveSnapshot passes its show_balance argument (always true
    /// from LLViewerWindowListener) into rawSnapshot's no_post slot, so those snapshots skip exposure and
    /// tonemapping for every sky (the NO_POST tonemap variant: clamp, then sRGB). False gives what the viewer's
    /// window shows: dynamic exposure plus ACES (RenderTonemapMix) for non-legacy skies. Legacy skies (probe
    /// ambiance 0) are NO_POST either way.
    /// </summary>
    public bool SnapshotNoPost { get; init; } = true;
    /// <summary>RenderCASSharpness (High and above: 0.4). Contrast adaptive sharpening after tonemapping.</summary>
    public float CasSharpness { get; init; } = 0.4f;
    /// <summary>RenderFSAAType: 0 none, 1 FXAA (not ported; treated as none), 2 SMAA. settings.xml says 0; the
    /// High-Ultra preset the reference viewer runs uses 2.</summary>
    public int FsaaType { get; init; } = 2;
    /// <summary>RenderFSAASamples, which for SMAA is the quality: 0 low, 1 medium, 2 high, 3 ultra (High-Ultra: 2).</summary>
    public int FsaaQuality { get; init; } = 2;
    /// <summary>RenderExposure (clamped to [0.5, 4] by the viewer).</summary>
    public float Exposure { get; init; } = 1.0f;

    /// <summary>RenderDynamicExposureEnabled (absent from settings.xml; code default true).</summary>
    public bool DynamicExposureEnabled { get; init; } = true;
    /// <summary>RenderUseExposureSkySettings.</summary>
    public bool UseExposureSkySettings { get; init; } = false;
    /// <summary>RenderDynamicExposureCoefficient.</summary>
    public float DynamicExposureCoefficient { get; init; } = 0.5f;
    /// <summary>RenderDiffuseLuminanceScale: weight of lit surfaces (not sky) in the exposure luminance.</summary>
    public float DiffuseLuminanceScale { get; init; } = 0.5f;
    /// <summary>RenderDynamicExposureSpeedError / SpeedTarget (absent from settings.xml; code defaults).</summary>
    public float DynamicExposureSpeedError { get; init; } = 0.1f;
    public float DynamicExposureSpeedTarget { get; init; } = 2.0f;

    public static ViewerRenderSettings Default { get; } = new();
}
