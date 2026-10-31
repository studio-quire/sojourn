namespace Sojourn.Client.Control;

/// <summary>How big the pointer's dot is where it lands in the world, as the eye sees it: full size up to 2 m (about how a
/// 10 m dot looked on a square-root rule tried before), then shrinking evenly per doubling of distance to half that by
/// 30 m and beyond (the user, 2026-10-31: too large far away; then the same size everywhere; then that size as the most,
/// about two times smaller far off).</summary>
public static class PointerDot
{
    const float Full = 2, Half = 30;

    /// <summary>perMetre: the dot's old radius per metre of distance (each dot's own weight).</summary>
    public static float Radius(float distance, float perMetre)
    {
        float d = MathF.Max(distance, 0);
        float far = Math.Clamp(MathF.Log(MathF.Max(d, Full) / Full) / MathF.Log(Half / Full), 0, 1);
        return MathF.Max(perMetre * 0.45f * d * (1 - 0.5f * far), 0.0015f);
    }
}
