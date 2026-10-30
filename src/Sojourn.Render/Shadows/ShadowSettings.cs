namespace Sojourn.Render.Shadows;

/// <summary>
/// The viewer settings the sun/spot shadow setup reads, with app_settings/settings.xml defaults.
/// RenderShadowDetail is overridden by the GPU class featuretable: High = 1 (sun only),
/// HighUltra and Ultra = 2 (sun + two projector shadows), lower presets = 0 (no shadows).
/// RenderShadowNearDist / ClipPlanes / OrthoClipPlanes no longer exist in this viewer.
/// </summary>
public sealed record ShadowSettings
{
    /// <summary>RenderShadowDetail: 0 none, 1 sun/moon, 2 sun/moon + 2 projector shadows.</summary>
    public int ShadowDetail { get; init; } = 2;

    /// <summary>RenderShadowSplitExponent. x and y are equal by default, so the split curve is plain x^3.</summary>
    public Vector3 SplitExponent { get; init; } = new(3f, 3f, 2f);

    /// <summary>RenderShadowFOVCutoff (radians, capped at 1.4 in code).</summary>
    public float FovCutoff { get; init; } = 0.8f;

    /// <summary>RenderShadowErrorCutoff.</summary>
    public float ErrorCutoff { get; init; } = 5.0f;

    /// <summary>RenderShadowBias.</summary>
    public float Bias { get; init; } = -0.002f;

    /// <summary>RenderShadowBiasError (scaled by camera altitude / 3000).</summary>
    public float BiasError { get; init; } = -0.007f;

    /// <summary>RenderShadowOffset.</summary>
    public float Offset { get; init; } = 0.01f;

    /// <summary>RenderShadowResolutionScale.</summary>
    public float ResolutionScale { get; init; } = 1.0f;

    /// <summary>RenderShadowSplits (cascade j is skipped when j &gt; this; 3 = all four).</summary>
    public int Splits { get; init; } = 3;

    /// <summary>RenderSpotShadowBias.</summary>
    public float SpotShadowBias { get; init; } = -0.0002f;

    /// <summary>RenderSpotShadowOffset.</summary>
    public float SpotShadowOffset { get; init; } = 0.04f;

    /// <summary>RenderShadowNoise (bound as shadow_noise alongside the shadow uniforms).</summary>
    public float ShadowNoise { get; init; } = -0.0001f;

    /// <summary>RenderShadowBlurSize (bound as blur_size).</summary>
    public float BlurSize { get; init; } = 1.4f;

    /// <summary>
    /// RenderFarClip (draw distance setting), used only to place the casters' near cull plane
    /// 2x this far toward the light. Null = use the camera's far distance.
    /// </summary>
    public float? FarClip { get; init; }

    /// <summary>
    /// gCubeSnapshot: reflection probe render. Two cascades only, stretched splits, unscaled map size.
    /// </summary>
    public bool CubeSnapshot { get; init; }

    /// <summary>Ours, not the viewer's: the last cascade's far end brought in to this share of where it would be (the
    /// load governor's trim, Realtime/LoadGovernor.cs), never nearer than just past the third cascade, so the near
    /// cascades and the maps' sizes stay as they are and shadows only end sooner.</summary>
    public float FarReach { get; init; } = 1f;

    /// <summary>Ours: cascades 2 and 3 fitted to their receivers grown by this share about their middle (0: as the
    /// viewer), so the map drawn one frame still holds what the next needs while the view moves a little, and one of
    /// them can be kept for a frame (Frame/Renderer.Cascades.cs). Their texels cover that much more ground: slightly
    /// softer far shadows. <see cref="ShadowCascade.ReceiverPoints"/> stay the receivers as fitted without it.</summary>
    public float FarMargin { get; init; }

    /// <summary>Ours: a fifth, coarse sun cascade (<see cref="SunShadowSetup.Far"/>) carrying shadows on from where the
    /// fourth ends (its full reach, or the governor's trim) to <see cref="FarEnd"/> times the full reach, fading out over
    /// that last stretch, instead of the viewer's fade over the fourth cascade's far half. 0: none, as the viewer.</summary>
    public int FarMapSize { get; init; }

    /// <summary>Where the coarse cascade ends, as a share of the fourth cascade's full reach (it fades out from 1 to this).</summary>
    public float FarEnd { get; init; } = 1.25f;

    /// <summary>The coarse cascade fitted to its receivers grown by this share about their middle, so its map can be kept
    /// while the head turns and walks a little (FrameRenderer.FarShadow).</summary>
    public float FarCascadeMargin { get; init; } = 0.25f;

    public static ShadowSettings Default { get; } = new();
}

/// <summary>
/// The main camera as the shadow code sees it. View is region -> GL eye space, row-vector
/// (System.Numerics CreateLookAt). Far is the camera's far (draw distance, LLViewerCamera::getFar()),
/// not the projection's far plane. Near is the viewer's 0.1 (MIN_NEAR_PLANE) unless changed.
/// </summary>
public readonly record struct ShadowCamera(Matrix4x4 View, float FovY, float Aspect, float Far, float Near = 0.1f);
