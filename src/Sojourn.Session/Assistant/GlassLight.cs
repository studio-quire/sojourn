using SkiaSharp;

namespace Sojourn.Session.Assistant;

/// <summary>
/// A sheet in the look (the panel, the settings menu, the feed, the reader, the assistant's cards with the person)
/// finished for the compositor that lays it over the world, with the light the glass gives of its own.
///
/// The glow (PanelLook.Glow): a clear glass with dark ink needs light behind its text, and over a dark world a light
/// tint at low opacity is only a dark grey. So the body never reads dimmer over a dark world than Glow x its tint when
/// solid: as tint_opacity drops below Glow the body makes up the difference with light of its own, in the tint's colour,
/// and at or above Glow it gives none. It's laid as a light behind the glass, passing through it where it's clear, so
/// text and keys (opaque or nearly) keep their own colour and the body's antialiased edge takes its share: per pixel,
/// the light is (coverage of the body - alpha), coverage being alpha / the body's opacity. Premultiplied, that's colour
/// above alpha: light added to whatever is behind, the world's own light still passing through at (1 - alpha).
///
/// The headset's layers (<see cref="Layers"/>): the swapchains are sRGB, so the compositor decodes each channel before it
/// blends, in linear light. Skia premultiplies in gamma, and decode(a x c) is far darker than a x decode(c) at low
/// alpha (a light tint at 0.3 came out a quarter as bright as it should). So for a layer the sheet is re-premultiplied
/// in linear light, glow and all, and encoded again. The desktop's overlay blends gamma bytes, as Skia does, and isn't
/// corrected; there the glow is added in gamma, as the same step in encoded light that the headset's makes.
/// </summary>
public static class GlassLight
{
    /// <summary>The sheets go to the headset's quad layers on sRGB swapchains (XrSession sets it while it runs).</summary>
    public static volatile bool Layers;

    /// <summary>The least body opacity the glow can find the body by (PanelLook's tint_opacity floor).</summary>
    public const float MinBody = 0.05f;

    /// <summary>The glow's light (linear, premultiplied: what the body adds over black where it's clear), per channel,
    /// for a look: (Glow - tint_opacity) x the tint, none at or above Glow.</summary>
    public static (float R, float G, float B) Light(PanelLook look)
    {
        float body = Math.Clamp(look.TintOpacity, MinBody, 1), glow = Math.Clamp(look.Glow, 0, 1);
        if (glow <= body) return (0, 0, 0);
        var t = SKColor.Parse(look.Tint);
        float k = glow - body;
        return (k * Decode(t.Red / 255f), k * Decode(t.Green / 255f), k * Decode(t.Blue / 255f));
    }

    /// <summary>Finish a sheet drawn by Skia (premultiplied RGBA8, gamma) in a look, in place: for a headset layer
    /// (layer) re-premultiplied in linear light with the glow; otherwise only the glow, in gamma. Alpha is unchanged
    /// (the frost goes by it).</summary>
    public static void Finish(byte[] px, PanelLook look, bool layer)
    {
        float body = Math.Clamp(look.TintOpacity, MinBody, 1);
        var (lr, lg, lb) = Light(look);
        bool lit = lr + lg + lb > 0;
        if (!layer && !lit) return;

        // the light per unit of the body's transmission, by alpha: (coverage - alpha) / (1 - body), 1 inside the bare body
        Span<float> through = stackalloc float[256];
        for (int a = 0; a < 256; a++)
        {
            float al = a / 255f;
            through[a] = lit ? Math.Max(0, Math.Min(1, al / body) - al) / (1 - body) : 0;
        }

        if (!layer)
        {
            // gamma: the step the headset's glow makes in the encoded body over black
            var t = SKColor.Parse(look.Tint);
            float G(byte c, float l) => 255 * Math.Max(0, Encode(body * Decode(c / 255f) + l) - Encode(body * Decode(c / 255f)));
            float gr = G(t.Red, lr), gg = G(t.Green, lg), gb = G(t.Blue, lb);
            for (int i = 0; i < px.Length; i += 4)
            {
                float k = through[px[i + 3]];
                if (k <= 0) continue;
                px[i] = (byte)Math.Min(255, px[i] + k * gr + 0.5f);
                px[i + 1] = (byte)Math.Min(255, px[i + 1] + k * gg + 0.5f);
                px[i + 2] = (byte)Math.Min(255, px[i + 2] + k * gb + 0.5f);
            }
            return;
        }

        var own = Own;
        var enc = Enc;
        const float n = EncSteps;
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 0 || a == 255) continue;   // nothing, or solid: the bytes are right as they are
            float k = through[a];
            int row = a << 8;
            px[i] = enc[(int)(Math.Min(1, own[row + px[i]] + k * lr) * n + 0.5f)];
            px[i + 1] = enc[(int)(Math.Min(1, own[row + px[i + 1]] + k * lg) * n + 0.5f)];
            px[i + 2] = enc[(int)(Math.Min(1, own[row + px[i + 2]] + k * lb) * n + 0.5f)];
        }
    }

    public static float Decode(float s) => s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
    public static float Encode(float l) => l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1 / 2.4f) - 0.055f;

    // [alpha * 256 + byte]: a gamma-premultiplied channel as linear premultiplied light (alpha x decode(byte / alpha))
    static readonly float[] Own = BuildOwn();
    // linear light in EncSteps steps -> its sRGB byte (fine enough that the darkest bytes come back as they went)
    const int EncSteps = 16384;
    static readonly byte[] Enc = BuildEnc();

    static float[] BuildOwn()
    {
        var t = new float[256 * 256];
        for (int a = 1; a < 256; a++)
            for (int p = 0; p < 256; p++)
                t[(a << 8) + p] = a / 255f * Decode(Math.Min(1, (float)p / a));
        return t;
    }

    static byte[] BuildEnc()
    {
        var t = new byte[EncSteps + 1];
        for (int i = 0; i <= EncSteps; i++) t[i] = (byte)Math.Round(255 * Encode((float)i / EncSteps));
        return t;
    }
}
