using System.Globalization;
using System.Numerics;

namespace Sojourn.Client;

/// <summary>SOJOURN_TEST_LENS_MASK=R: a round lens of radius R (tangents of the angle off the eye's axis) on the desktop's
/// Quest-sized eyes (--quest-eyes), its outside as the hidden area, so the lens mask (Render/Frame/Renderer.Lens.cs)
/// can be tested and timed without a headset. Meta's runtime gives the real mesh (XR_KHR_visibility_mask).</summary>
static class TestLens
{
    public static readonly Vector2[]? Mesh = Make();

    static Vector2[]? Make()
    {
        if (!float.TryParse(Environment.GetEnvironmentVariable("SOJOURN_TEST_LENS_MASK"), NumberStyles.Float, CultureInfo.InvariantCulture, out float r) || r <= 0)
            return null;
        const int n = 96;
        const float outer = 8;   // past any eye's edge
        var v = new List<Vector2>();
        for (int j = 0; j < n; j++)
        {
            float a0 = MathF.Tau * j / n, a1 = MathF.Tau * (j + 1) / n;
            Vector2 d0 = new(MathF.Cos(a0), MathF.Sin(a0)), d1 = new(MathF.Cos(a1), MathF.Sin(a1));
            v.AddRange(new[] { d0 * r, d1 * r, d1 * outer, d0 * r, d1 * outer, d0 * outer });
        }
        return v.ToArray();
    }
}
