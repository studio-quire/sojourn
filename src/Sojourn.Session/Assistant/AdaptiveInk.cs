using System.Numerics;
using SkiaSharp;
using Sojourn.Render.Realtime;

namespace Sojourn.Session.Assistant;

/// <summary>The sheets drawn in the look whose ink adaptive ink chooses, each by what's behind it.</summary>
public enum InkSheet { Panel, Feed, Cards, Reader }

/// <summary>
/// Adaptive ink (VrSettings.AdaptiveInk, off by default; the user's idea, 2026-10-28): for clear glass, the ink turns
/// light over dark places and dark over bright ones, so a nearly clear panel stays readable without the glow's milky
/// light behind it. Where the body is solid enough that the world behind barely moves it, it doesn't change.
///
/// The headset's compositor does the final blend, so the ink can't follow the world per pixel: each sheet (the panel and
/// the settings menu on its layer, the feed, the cards with the person, the reader) takes one ink, chosen from a small
/// sample of the world behind it (the overlay's frost mips, read back every third frame without waiting:
/// Render's Overlay.Backdrop.cs). From the patch under a sheet it keeps its darker and brighter quarter (so a sheet over
/// both sky and ground is judged by both), smoothed over a tenth of a second, and takes whichever ink reads better over
/// both, by the legibility measure's contrast of the ink on the bare body (PanelLook.InkContrast). It turns only when its
/// ink reads less than well (4.5) and the other would read a quarter better, at most every 0.35 s; and it fades over
/// 0.15 s in a few steps (each a redraw of the sheet), not a flash. A sheet that comes up after a while away takes its
/// ink at once.
///
/// The other ink is the look's own turned over (<see cref="Turned"/>): each colour's lightness mirrored, its hue kept,
/// and dim, faint and the accent as far from the ink as they were, so a look gets light ink of its own colours, not a
/// preset's. With either ink there's no glow: the ink turning is what keeps the text readable, and the panel stays as
/// clear as its tint_opacity says.
/// </summary>
public sealed class AdaptiveInk
{
    readonly Func<bool> _on;

    public AdaptiveInk(Func<bool> on) => _on = on;

    public bool On => _on();

    // the turned-over ink sits at about smoke's light ink (L* 90) or a little below the presets' dark inks (24-28)
    const float LightInk = 90, DarkInk = 24;
    // quicker after the user found it slow in the headset (2026-10-28; was hold 1 s, smoothing 0.25 s, fade 0.3 s)
    const double Good = 4.5, Margin = 1.25, Hold = 0.35, Smooth = 0.1, Fade = 0.15, Away = 1.0;
    const int Steps = 4;

    sealed class Sheet
    {
        public float Lo = -1, Hi = -1;   // the world behind, smoothed: its darker and brighter quarter (linear)
        public bool Turned;              // the other ink is chosen
        public double ChangedAt = double.NegativeInfinity, SeenAt = double.NegativeInfinity;
        public float T;                  // easing from 0 (its own ink) to 1 (the other)
        public volatile int Step;        // T in steps: the look it's drawn in
    }

    readonly Sheet[] _sheets = Enum.GetValues<InkSheet>().Select(_ => new Sheet()).ToArray();

    sealed class Cache(PanelLook look)
    {
        public readonly PanelLook Base = look;
        public readonly PanelLook[] Steps = Build(look);

        static PanelLook[] Build(PanelLook look)
        {
            var own = look with { Glow = 0 };
            var other = AdaptiveInk.Turned(look);
            var steps = new PanelLook[AdaptiveInk.Steps + 1];
            for (int i = 0; i <= AdaptiveInk.Steps; i++)
                steps[i] = i == 0 ? own : i == AdaptiveInk.Steps ? other : Blend(own, other, (float)i / AdaptiveInk.Steps);
            return steps;
        }
    }

    volatile Cache? _cache;

    Cache For(PanelLook look)
    {
        var c = _cache;
        if (c == null || !ReferenceEquals(c.Base, look)) _cache = c = new Cache(look);
        return c;
    }

    /// <summary>The look a sheet is drawn in now: the person's own while adaptive ink is off; otherwise theirs without
    /// the glow, or with its ink turned over (or on the way). The same instance for the same choice, so the sheets'
    /// redraw-on-change keys see a change only when the choice changes.</summary>
    public PanelLook Look(InkSheet sheet, PanelLook look) => On ? For(look).Steps[_sheets[(int)sheet].Step] : look;

    /// <summary>What a sheet's look is now, for its redraw key (-1: off).</summary>
    public int StepOf(InkSheet sheet) => On ? _sheets[(int)sheet].Step : -1;

    long _sampleSeen = -1;
    double _sampledAt, _frameAt;
    readonly List<float> _cells = new();

    /// <summary>Each frame on the main thread: the renderer's latest sample of the world behind, the person's look, and
    /// the reader's corners in the frame's region (null: closed, or on the desktop, where it's in the panel's place).</summary>
    public void Update(BackdropSample? sample, PanelLook look, Vector3[]? reader, double now)
    {
        float dt = (float)Math.Clamp(now - _frameAt, 0, 0.1);
        _frameAt = now;
        if (!On)
        {
            foreach (var s in _sheets) { s.Lo = s.Hi = -1; s.Turned = false; s.T = 0; s.Step = 0; s.SeenAt = double.NegativeInfinity; }
            return;
        }
        if (sample != null && sample.Number != _sampleSeen)
        {
            _sampleSeen = sample.Number;
            float since = (float)Math.Clamp(now - _sampledAt, 0, 1);
            _sampledAt = now;
            var cache = For(look);
            var scene = sample.Scene;
            bool panel = Measure(sample, PanelQuad(sample, scene));
            if (panel) Judge(InkSheet.Panel, cache, since, now);
            if (Measure(sample, scene.Feed is { } f ? SheetQuad(sample, f.Image, f.At, f.Corners) : null)) Judge(InkSheet.Feed, cache, since, now);
            if (MeasureCards(sample, scene)) Judge(InkSheet.Cards, cache, since, now);
            // the reader: its own place in the headset; on the desktop it's drawn in the panel's
            if (reader != null ? Measure(sample, Quad(sample, reader)) : panel && Measure(sample, PanelQuad(sample, scene)))
                Judge(InkSheet.Reader, cache, since, now);
        }
        foreach (var s in _sheets)
        {
            float to = s.Turned ? 1 : 0;
            s.T = s.T < to ? Math.Min(to, s.T + dt / (float)Fade) : Math.Max(to, s.T - dt / (float)Fade);
            s.Step = (int)MathF.Round(s.T * Steps);
        }
    }

    /// <summary>The darker and brighter quarter of the patch just gathered (in _cells) into a sheet, and its ink chosen.</summary>
    void Judge(InkSheet which, Cache cache, float since, double now)
    {
        var s = _sheets[(int)which];
        _cells.Sort();
        int n = _cells.Count;
        float lo = _cells[(n - 1) / 4], hi = _cells[(n - 1) - (n - 1) / 4];
        bool fresh = s.Lo < 0 || now - s.SeenAt > Away;
        s.SeenAt = now;
        if (fresh) (s.Lo, s.Hi) = (lo, hi);
        else
        {
            float k = 1 - MathF.Exp(-since / (float)Smooth);
            s.Lo += (lo - s.Lo) * k;
            s.Hi += (hi - s.Hi) * k;
        }
        var (own, other) = (cache.Steps[0], cache.Steps[Steps]);
        if ((fresh || now - s.ChangedAt >= Hold) && Better(s.Turned ? own : other, s.Turned ? other : own, s.Lo, s.Hi))
        {
            s.Turned = !s.Turned;
            s.ChangedAt = now;
        }
        if (fresh) { s.T = s.Turned ? 1 : 0; s.Step = s.Turned ? Steps : 0; }
    }

    /// <summary>The other ink is worth turning to, over a world whose darker and brighter quarter are lo and hi: the ink
    /// in use reads less than well over one of them, and the other would read a quarter better over both.</summary>
    static bool Better(PanelLook other, PanelLook inUse, float lo, float hi)
    {
        double Reads(PanelLook l) => Math.Min(l.InkContrast(lo), l.InkContrast(hi));
        double here = Reads(inUse);
        return here < Good && Reads(other) > here * Margin;
    }

    /// <summary>What a sheet in this look takes when it comes up over such a world (--panel-shots' adaptive sheet): the
    /// look without the glow, or turned over.</summary>
    internal static PanelLook Fresh(PanelLook look, float lo, float hi)
    {
        var own = look with { Glow = 0 };
        var other = Turned(look);
        return Better(other, own, lo, hi) ? other : own;
    }

    // --- where a sheet is in the sample ---------------------------------------------------------------------------------

    /// <summary>The cells of the sample under a quad (the eye's normalised device coordinates, corners in order) into
    /// _cells; false if it isn't in view.</summary>
    bool Measure(BackdropSample s, Vector2[]? q, bool keep = false)
    {
        if (!keep) _cells.Clear();
        if (q == null) return _cells.Count > 0;
        float minX = q.Min(p => p.X), maxX = q.Max(p => p.X), minY = q.Min(p => p.Y), maxY = q.Max(p => p.Y);
        if (maxX < -1 || minX > 1 || maxY < -1 || minY > 1) return _cells.Count > 0;
        int w = s.Width, h = s.Height, before = _cells.Count;
        int i0 = Math.Max(0, (int)MathF.Floor((minX + 1) / 2 * w - 0.5f)), i1 = Math.Min(w - 1, (int)MathF.Ceiling((maxX + 1) / 2 * w - 0.5f));
        int j0 = Math.Max(0, (int)MathF.Floor((minY + 1) / 2 * h - 0.5f)), j1 = Math.Min(h - 1, (int)MathF.Ceiling((maxY + 1) / 2 * h - 0.5f));
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
                if (Inside(q, new Vector2((i + 0.5f) / w * 2 - 1, (j + 0.5f) / h * 2 - 1)))
                    _cells.Add(s.Luminance[j * w + i]);
        if (_cells.Count == before)
        {
            // smaller than a cell: the one under its middle
            var c = (q[0] + q[1] + q[2] + q[3]) / 4;
            int i = Math.Clamp((int)((c.X + 1) / 2 * w), 0, w - 1), j = Math.Clamp((int)((c.Y + 1) / 2 * h), 0, h - 1);
            _cells.Add(s.Luminance[j * w + i]);
        }
        return true;
    }

    bool MeasureCards(BackdropSample s, OverlayScene scene)
    {
        _cells.Clear();
        foreach (var card in scene.Cards)
        {
            var q = card.Corners is { Length: 4 } c ? Quad(s, c)
                : card.Screen is { } at ? Flat(s, at, new Vector2(card.Uv.Z, card.Uv.W)) : null;
            Measure(s, q, keep: true);
        }
        return _cells.Count > 0;
    }

    static Vector2[]? PanelQuad(BackdropSample s, OverlayScene scene) =>
        scene.Panel is { } p ? SheetQuad(s, p, scene.PanelAt, scene.PanelCorners) : null;

    static Vector2[]? SheetQuad(BackdropSample s, OverlayImage img, Vector2 at, Vector3[]? corners) =>
        corners is { Length: 4 } ? Quad(s, corners) : Flat(s, at, new Vector2(img.Width, img.Height));

    static Vector2[] Flat(BackdropSample s, Vector2 at, Vector2 size)
    {
        Vector2 a = at / s.Target * 2 - Vector2.One, b = (at + size) / s.Target * 2 - Vector2.One;
        return new[] { a, new Vector2(b.X, a.Y), b, new Vector2(a.X, b.Y) };
    }

    static Vector2[]? Quad(BackdropSample s, Vector3[] corners)
    {
        var q = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            var c = Vector4.Transform(new Vector4(corners[i], 1), s.ViewProj);
            if (c.W < 0.02f) return null;
            q[i] = new Vector2(c.X / c.W, c.Y / c.W);
        }
        return q;
    }

    static bool Inside(Vector2[] q, Vector2 p)
    {
        int sign = 0;
        for (int k = 0; k < 4; k++)
        {
            Vector2 a = q[k], e = q[(k + 1) % 4] - a, d = p - a;
            int side = Math.Sign(e.X * d.Y - e.Y * d.X);
            if (side == 0) continue;
            if (sign == 0) sign = side;
            else if (side != sign) return false;
        }
        return true;
    }

    // --- the other ink ----------------------------------------------------------------------------------------------

    /// <summary>A look with its ink turned over: light where it was dark, dark where it was light. Each of ink, dim,
    /// faint and the accent keeps its hue (a little paler going light) and its distance in lightness from the ink, the
    /// ink itself landing at L* 90 or 24; the accent's label white or a deep tone of the accent, whichever reads; keys,
    /// sheen and rim (light laid on the body) fainter under light ink, keys stronger under dark; no glow.</summary>
    public static PanelLook Turned(PanelLook l)
    {
        var ink = Lab.Of(l.Ink);
        bool toLight = ink.L < 50;
        float chroma = toLight ? 0.8f : 1;
        string Turn(string hex)
        {
            var c = Lab.Of(hex);
            float lit = toLight ? Math.Clamp(LightInk - (c.L - ink.L), 50, 98) : Math.Clamp(DarkInk + (ink.L - c.L), 4, 50);
            return Lab.Hex(lit, c.A * chroma, c.B * chroma);
        }
        var accent = Turn(l.Accent);
        double la = PanelLook.Lum(accent);
        var onAccent = l.OnAccent;
        if (PanelLook.Contrast(PanelLook.Lum(onAccent), la) < 4.5)
        {
            var a = Lab.Of(accent);
            var deep = Lab.Hex(12, a.A * 0.5f, a.B * 0.5f);
            onAccent = PanelLook.Contrast(1, la) >= PanelLook.Contrast(PanelLook.Lum(deep), la) ? "#ffffff" : deep;
        }
        return l with
        {
            Ink = Turn(l.Ink), Dim = Turn(l.Dim), Faint = Turn(l.Faint), Accent = accent, OnAccent = onAccent,
            KeyOpacity = toLight ? l.KeyOpacity * 0.25f : Math.Min(0.6f, l.KeyOpacity * 4),
            SheenOpacity = toLight ? l.SheenOpacity * 0.4f : l.SheenOpacity,
            // the rim carries pearl's satin highlight across the top, where the asked line sits in light ink
            Rim = toLight ? l.Rim * 0.4f : l.Rim,
            Glow = 0,
        };
    }

    /// <summary>Part of the way from one look's ink to another's (a step of the fade).</summary>
    static PanelLook Blend(PanelLook a, PanelLook b, float t) => a with
    {
        Ink = Mix(a.Ink, b.Ink, t), Dim = Mix(a.Dim, b.Dim, t), Faint = Mix(a.Faint, b.Faint, t),
        Accent = Mix(a.Accent, b.Accent, t), OnAccent = t < 0.5f ? a.OnAccent : b.OnAccent,
        KeyOpacity = a.KeyOpacity + (b.KeyOpacity - a.KeyOpacity) * t,
        SheenOpacity = a.SheenOpacity + (b.SheenOpacity - a.SheenOpacity) * t,
        Rim = a.Rim + (b.Rim - a.Rim) * t,
    };

    static string Mix(string x, string y, float t)
    {
        SKColor p = SKColor.Parse(x), q = SKColor.Parse(y);
        int M(byte u, byte v) => (int)MathF.Round(u + (v - u) * t);
        return $"#{M(p.Red, q.Red):x2}{M(p.Green, q.Green):x2}{M(p.Blue, q.Blue):x2}";
    }

    /// <summary>CIELAB (D65) to and from sRGB hex, enough to turn a colour's lightness over.</summary>
    static class Lab
    {
        const float D = 6 / 29f;
        static float F(float t) => t > D * D * D ? MathF.Cbrt(t) : t / (3 * D * D) + 4 / 29f;
        static float Finv(float t) => t > D ? t * t * t : 3 * D * D * (t - 4 / 29f);

        public static (float L, float A, float B) Of(string hex)
        {
            var c = SKColor.Parse(hex);
            float r = GlassLight.Decode(c.Red / 255f), g = GlassLight.Decode(c.Green / 255f), b = GlassLight.Decode(c.Blue / 255f);
            float fx = F((0.4124f * r + 0.3576f * g + 0.1805f * b) / 0.95047f);
            float fy = F(0.2126f * r + 0.7152f * g + 0.0722f * b);
            float fz = F((0.0193f * r + 0.1192f * g + 0.9505f * b) / 1.08883f);
            return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
        }

        /// <summary>The colour at that lightness, its chroma eased down until it fits in sRGB (so the lightness holds).</summary>
        public static string Hex(float l, float a, float b)
        {
            float r = 0, g = 0, bl = 0;
            for (int i = 0; i < 12; i++, a *= 0.85f, b *= 0.85f)
            {
                float fy = (l + 16) / 116, x = Finv(fy + a / 500) * 0.95047f, y = Finv(fy), z = Finv(fy - b / 200) * 1.08883f;
                r = 3.2406f * x - 1.5372f * y - 0.4986f * z;
                g = -0.9689f * x + 1.8758f * y + 0.0415f * z;
                bl = 0.0557f * x - 0.2040f * y + 1.0570f * z;
                if (Math.Min(r, Math.Min(g, bl)) >= -0.002f && Math.Max(r, Math.Max(g, bl)) <= 1.002f) break;
            }
            int E(float v) => (int)MathF.Round(255 * GlassLight.Encode(Math.Clamp(v, 0, 1)));
            return $"#{E(r):x2}{E(g):x2}{E(bl):x2}";
        }
    }
}
