namespace Sojourn.Client.Xr;

/// <summary>
/// Dynamic resolution for the headset: the eyes' render size follows the GPU so the frame rate holds. Once a second it
/// gets the GPU's mean frame time; two seconds in a row above 85% of the display's period shrink the eyes toward 75%
/// of it (GPU time goes with the pixel count, so the scale moves by the square root), six calm seconds under 60% grow
/// them a step toward 70%. Changes are at least 3 s apart and at least 2% (each new size allocates render targets).
/// </summary>
public sealed class EyeScale(float start, float min, float max)
{
    public float Scale { get; private set; } = Math.Clamp(start, min, max);
    double _lastChange = double.MinValue;
    int _hot, _calm;

    /// <summary>The starting scale for a runtime's recommended eye size: about 4.2 million pixels an eye, the size
    /// measured to hold 90 Hz in a dense scene on this machine's RTX 5070 (Quest 3 over Link at 90 Hz recommends
    /// 2448x2672, so 0.8; smaller recommendations start at 1).</summary>
    public static float StartFor(int recommendedW, int recommendedH, float min, float max) =>
        Math.Clamp(MathF.Sqrt(4.2e6f / Math.Max(1, recommendedW * recommendedH)), min, max);

    /// <returns>The new scale, or null to keep the current one.</returns>
    public float? Update(double now, double gpuMs, double periodMs)
    {
        if (gpuMs <= 0 || periodMs <= 0) return null;
        _hot = gpuMs > 0.85 * periodMs ? _hot + 1 : 0;
        _calm = gpuMs < 0.6 * periodMs ? _calm + 1 : 0;
        if (now - _lastChange < 3) return null;
        float next = Scale;
        if (_hot >= 2) next = Scale * (float)Math.Clamp(Math.Sqrt(0.75 * periodMs / gpuMs), 0.8, 0.97);
        else if (_calm >= 6) next = Scale * (float)Math.Clamp(Math.Sqrt(0.7 * periodMs / gpuMs), 1.0, 1.06);
        next = Math.Clamp(next, min, max);
        if (MathF.Abs(next - Scale) < 0.02f) return null;
        Scale = next;
        _lastChange = now;
        _hot = _calm = 0;
        return next;
    }
}
