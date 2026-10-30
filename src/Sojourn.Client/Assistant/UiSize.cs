using System.Text.Json.Nodes;
using Sojourn.Client.Control;

namespace Sojourn.Client.Assistant;

/// <summary>
/// How big the headset's sheets are (VrSettings.UiSize; user, 2026-10-27: they were very large, and people comfortable
/// with smaller text should fit more on screen): one factor on the angular size of the assistant's panel, the settings
/// menu, the chat feed, the assistant's cards, the reader and the HUD board. 1 is the size they were designed at (body
/// text ~1.25 degrees). Not the desktop's, which sizes them by the window, nor name tags and labels out in the world.
///
/// Sharpness: the sheets are rasterised at <see cref="Raster"/>, which follows the size, so a degree always holds about
/// 34 raster pixels (2.4 per logical pixel at 0.07 degrees), ~1.4 times the Quest 3's ~25 display pixels a degree. A
/// smaller sheet drawn at the old 2.4 would be more pixels a degree than the display has, and its layer has no mips,
/// so fine strokes would shimmer; at the smallest size body text is still ~22 display pixels to the em.
///
/// Fitting more: the sheets that hold long text keep their angular budget, so smaller text means more of it (an answer's
/// lines a page, the menu's rows a page, the reader's view); see <see cref="Fit"/>.
/// </summary>
public static class UiSize
{
    /// <summary>The menu's sizes (its segments), smallest first; the agent API takes anything between the ends.</summary>
    public static readonly float[] Steps = { 0.7f, 0.85f, 1f, 1.2f };
    public const float Min = 0.7f, Max = 1.2f;
    public const float BaseDegPerPx = 0.07f, BaseRaster = 2.4f;

    static float _scale = 1;
    public static float Scale
    {
        get => Volatile.Read(ref _scale);
        set => Volatile.Write(ref _scale, Math.Clamp(float.IsFinite(value) ? value : 1, Min, Max));
    }

    /// <summary>Degrees a logical pixel spans.</summary>
    public static float DegPerPx => BaseDegPerPx * Scale;
    /// <summary>Raster pixels per logical pixel for a headset layer: the same pixels a degree at any size.</summary>
    public static float Raster => BaseRaster * Scale;
    /// <summary>A logical pixel's size in metres at a distance.</summary>
    public static float MetresPerPx(float distance) => distance * MathF.Tan(DegPerPx * MathF.PI / 180);
    /// <summary>How many logical pixels a sheet's angular budget holds now, against size 1 (1/Scale): more lines a page
    /// when smaller.</summary>
    public static float Fit => 1 / Scale;

    /// <summary>How much wider in logical px a sheet is for a fit (<see cref="Panel.WideFor"/>: Fit^0.7).</summary>
    public static float WideFor(float fit) => Panel.WideFor(fit);
    public static float Wide => WideFor(Fit);
    // --- sheets beside the panel (the chat feed on the left, the assistant's view cards on the right) ----------------
    // Placed edge to edge with the panel at every size (the user, 2026-10-27: at 0.7 the feed overlapped the panel). The
    // panel's width is taken as its widest regular sheet (the settings menu; an answer card is a little narrower), so the
    // feed doesn't shift whenever the panel changes between capsule, card and menu. Half-widths are yaw angles where the
    // sheets meet lowest, 30 degrees below the eyes (the feed's and cards' bottom): a flat sheet low in view spans more
    // yaw than its width says at eye level (1/cos 30, ~15% more).

    public const float BesideGapDeg = 3;
    const float LowCos = 0.866f;   // cos 30 degrees

    /// <summary>Half the yaw (radians) a sheet this wide (metres) spans at the panel's distance, where it sits lowest.</summary>
    public static float HalfYaw(float widthM) => MathF.Atan(widthM / 2 / (PanelPlacement.Distance * LowCos));
    /// <summary>Half the yaw the panel's widest sheet spans (radians).</summary>
    public static float PanelHalfYaw => HalfYaw(Panel.MenuWidth * Wide * MetresPerPx(PanelPlacement.Distance));
    /// <summary>From the panel's middle to the middle of a sheet this wide beside it (radians), edge to edge plus the gap.</summary>
    public static float BesideYaw(float widthM) => PanelHalfYaw + BesideGapDeg * MathF.PI / 180 + HalfYaw(widthM);

    /// <summary>The menu's row (view section, headset): four segments, each an A at its size.</summary>
    public static MenuRow Row(VrSettings st)
    {
        int at = 0;
        for (int i = 1; i < Steps.Length; i++)
            if (MathF.Abs(Steps[i] - st.UiSize) < MathF.Abs(Steps[at] - st.UiSize)) at = i;
        return MenuRow.Choice("size", Steps.Select(_ => "A").ToArray(), at) with { OptionSizes = new[] { 12f, 14.5f, 17f, 20f } };
    }

    public static void Set(VrSettings st, float size)
    {
        st.UiSize = Math.Clamp(size, Min, Max);
        Scale = st.UiSize;
    }

    /// <summary>At start: the saved size, and the setting on the agent API (SojournSettings ui_size).</summary>
    public static void Start(VrSettings st, SettingsApi api)
    {
        Scale = st.UiSize;
        api.Add("ui_size", new SettingsApi.Setting("view",
            "in the headset, how big the assistant's panel, the settings menu, the chat feed, the assistant's cards, the reader and the HUD board are "
            + "(1: the default; smaller fits more lines and rows on each; the menu's sizes are 0.7, 0.85, 1 and 1.2)",
            $"{Min:0.##}-{Max:0.##}", () => JsonValue.Create(MathF.Round(st.UiSize, 2)),
            SettingsApi.Number("ui_size", Min, Max, "", f => Set(st, f))));
    }
}
