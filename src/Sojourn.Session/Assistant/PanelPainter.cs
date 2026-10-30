using SkiaSharp;

namespace Sojourn.Session.Assistant;

public sealed partial class Panel
{
    /// <summary>A look's colours and faces, parsed once.</summary>
    internal sealed class Ink
    {
        public readonly PanelLook Look;
        public readonly SKColor Tint, Text, Dim, Faint, Accent, OnAccent, Key;
        public readonly SKColor[] Sheen;
        public readonly SKTypeface Regular, Semi;
        public readonly bool Glass, Pearl, Metal;
        /// <summary>The body is light (dark ink): wells and shadows go darker, not lighter.</summary>
        public readonly bool Light;

        public Ink(PanelLook l)
        {
            Look = l;
            Tint = SKColor.Parse(l.Tint);
            Text = SKColor.Parse(l.Ink);
            Dim = SKColor.Parse(l.Dim);
            Faint = SKColor.Parse(l.Faint);
            Accent = SKColor.Parse(l.Accent);
            OnAccent = SKColor.Parse(l.OnAccent);
            Key = SKColor.Parse(l.Key);
            Sheen = l.Sheen.Select(g => SKColor.Parse(g)).ToArray();
            (Regular, Semi) = Faces(l.Font);
            Pearl = l.Material == "pearl";
            Metal = l.Material == "metal";
            Glass = !Pearl && !Metal;
            Light = Text.Red + Text.Green + Text.Blue < 3 * 128;
        }

        // one parse per look: the panel redraws often, the look rarely changes
        static Ink? _last;
        public static Ink For(PanelLook l)
        {
            var last = _last;
            if (last != null && ReferenceEquals(last.Look, l)) return last;
            return _last = new Ink(l);
        }
    }

    static readonly Dictionary<string, (SKTypeface, SKTypeface)> FaceCache = new();

    /// <summary>Regular and semibold faces for a look's font; Archivo (bundled) when the system lacks one. No light
    /// weights: thin strokes shimmer on a headset's lenses.</summary>
    static (SKTypeface Regular, SKTypeface Semi) Faces(string font)
    {
        lock (FaceCache)
        {
            if (FaceCache.TryGetValue(font, out var got)) return got;
            var archivo = (Bundled("Archivo-Regular.ttf"), Bundled("Archivo-SemiBold.ttf"));
            string? family = font switch
            {
                "segoe" => "Segoe UI", "bahnschrift" => "Bahnschrift", "georgia" => "Georgia", "cascadia" => "Cascadia Code", _ => null,
            };
            var faces = archivo;
            if (family != null && SKTypeface.FromFamilyName(family) is { } probe && probe.FamilyName == family)
            {
                SKTypeface W(SKFontStyleWeight w) => SKTypeface.FromFamilyName(family, w, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? probe;
                faces = (W(SKFontStyleWeight.Normal), W(SKFontStyleWeight.SemiBold));
            }
            FaceCache[font] = faces;
            return faces;
        }
    }

    static SKTypeface Bundled(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fonts", file);
        return File.Exists(path) ? SKTypeface.FromFile(path) : SKTypeface.Default;
    }

    internal static SKFont Font(SKTypeface face, float size) => new(face, size) { Edging = SKFontEdging.Antialias, Subpixel = true, Hinting = SKFontHinting.Slight };

    internal static float Measure(SKFont font, string s) => Painter.FontFor(font, s).MeasureText(s);

    /// <summary>Greedy word wrap into at most <paramref name="maxLines"/> lines, the last ellipsised if cut.</summary>
    internal static List<string> Wrap(string text, SKFont font, float width, int maxLines)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var words = para.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cur = "";
            foreach (var word in words)
            {
                var next = cur.Length == 0 ? word : cur + " " + word;
                if (Measure(font, next) <= width) { cur = next; continue; }
                if (cur.Length > 0) lines.Add(cur);
                cur = word;
                while (Measure(font, cur) > width && cur.Length > 1)
                {
                    int n = cur.Length - 1;
                    while (n > 1 && Measure(font, cur[..n]) > width) n--;
                    lines.Add(cur[..n]);
                    cur = cur[n..];
                }
            }
            lines.Add(cur);
        }
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            lines[^1] = Ellipsize(lines[^1] + " ...", font, width, force: true);
        }
        return lines;
    }

    internal static string Ellipsize(string text, SKFont font, float width, bool force = false)
    {
        if (!force && Measure(font, text) <= width) return text;
        var t = text.TrimEnd('.', ' ');
        while (t.Length > 0 && Measure(font, t + "...") > width) t = t[..^1];
        return t.TrimEnd() + "...";
    }

    static SKColor White(float a) => new(255, 255, 255, (byte)Math.Clamp(a * 255, 0, 255));
    static SKColor Black(float a) => new(0, 0, 0, (byte)Math.Clamp(a * 255, 0, 255));
    internal static SKColor A(SKColor c, float a) => c.WithAlpha((byte)Math.Clamp(a * 255, 0, 255));
    internal static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t), (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    /// <summary>
    /// The drawing moves, in the look's material; a null canvas only measures. Everything on the body is a lighter or
    /// darker layer of it; the accent is the one solid colour.
    /// </summary>
    internal readonly struct Painter(SKCanvas? c, Ink k, float width)
    {
        public Ink K => k;
        public float W => width;
        public bool Drawing => c != null;
        public SKCanvas? Canvas => c;

        /// <summary>Archivo and the system faces cover Latin; other scripts (names, chat) fall back to a face that has them.</summary>
        public static SKFont FontFor(SKFont font, string text)
        {
            if (font.ContainsGlyphs(text)) return font;
            foreach (var rune in text.EnumerateRunes())
            {
                if (font.ContainsGlyph(rune.Value)) continue;
                var fb = SKFontManager.Default.MatchCharacter(rune.Value);
                if (fb != null) return new SKFont(fb, font.Size) { Edging = font.Edging, Subpixel = font.Subpixel, Hinting = font.Hinting };
                break;
            }
            return font;
        }

        float Radius(float h) => Math.Min(k.Look.Radius, h / 2);

        /// <summary>The body. Light lies on it evenly: nothing drawn here starts or stops partway down, so it reads as
        /// one pane whatever the panel holds.</summary>
        public void Body(float h)
        {
            if (c == null) return;
            var l = k.Look;
            var rect = new SKRect(0.5f, 0.5f, width - 0.5f, h - 0.5f);
            float rad = Radius(h);
            var r = new SKRoundRect(rect, rad);

            // a faint dark edge outside keeps it apart from a bright world
            using (var edge = new SKPaint { Color = Black(k.Light ? 0.12f : 0.22f), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
                c.DrawRoundRect(new SKRoundRect(new SKRect(rect.Left - 0.5f, rect.Top - 0.5f, rect.Right + 0.5f, rect.Bottom + 0.5f), rad + 0.5f), edge);

            // the body: lighter toward the top, as light falls on it
            using (var fill = new SKPaint
                   {
                       IsAntialias = true,
                       Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h),
                           new[] { A(Mix(k.Tint, SKColors.White, k.Metal ? 0 : 0.07f), l.TintOpacity), A(k.Tint, l.TintOpacity) }, SKShaderTileMode.Clamp),
                   })
                c.DrawRoundRect(r, fill);

            c.Save();
            c.ClipRoundRect(r, antialias: true);
            if (k.Sheen.Length > 0 && l.SheenOpacity > 0)
            {
                float s = l.SheenOpacity;
                if (k.Metal)
                {
                    // studio light on polished metal: a bright band at the top, a darker one through the middle, a
                    // second bright one low down
                    var hi = k.Sheen[0];
                    var lo = k.Sheen.Length > 1 ? k.Sheen[1] : k.Tint;
                    var stops = new[] { A(hi, s), A(hi, s * 0.15f), A(lo, s * 0.85f), A(lo, s * 0.2f), A(hi, s * 0.7f), A(lo, s * 0.4f) };
                    using var p = new SKPaint
                    {
                        IsAntialias = true,
                        Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width * 0.08f, h), stops,
                            new[] { 0f, 0.22f, 0.46f, 0.62f, 0.86f, 1f }, SKShaderTileMode.Clamp),
                    };
                    c.DrawRect(rect, p);
                }
                else if (k.Pearl)
                {
                    // orient: the sheen colours lying under the surface in broad diagonal bands
                    var cols = k.Sheen.Concat(k.Sheen.Take(1)).Select(g => A(g, s)).ToArray();
                    using (var p = new SKPaint
                           {
                               IsAntialias = true,
                               Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, h * 1.6f), cols, SKShaderTileMode.Mirror),
                           })
                        c.DrawRect(rect, p);
                    // and a broad satin highlight over the top
                    using var sat = new SKPaint
                    {
                        IsAntialias = true,
                        Shader = SKShader.CreateRadialGradient(new SKPoint(width * 0.35f, -h * 0.15f), Math.Max(width * 0.7f, h),
                            new[] { White(0.35f * l.Rim), White(0.1f * l.Rim), White(0) }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp),
                    };
                    c.DrawRect(rect, sat);
                }
                else
                {
                    // glass: light caught in the corners, top left and bottom right
                    var at = new[] { new SKPoint(0, 0), new SKPoint(width, h) };
                    float reach = Math.Max(width * 0.55f, h * 0.9f);
                    for (int i = 0; i < 2; i++)
                    {
                        var g = k.Sheen[Math.Min(i, k.Sheen.Length - 1)];
                        using var p = new SKPaint
                        {
                            IsAntialias = true,
                            Shader = SKShader.CreateRadialGradient(at[i], reach,
                                new[] { A(g, s), A(g, s * 0.4f), A(g, 0) }, new[] { 0f, 0.4f, 1f }, SKShaderTileMode.Clamp),
                        };
                        c.DrawRect(rect, p);
                    }
                }
            }
            // thickness: light gathered just inside the edge
            if (!k.Metal)
                using (var band = new SKPaint
                       {
                           IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7,
                           Color = White((k.Light ? 0.22f : 0.06f) * l.Rim), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3),
                       })
                    c.DrawRoundRect(r, band);
            c.Restore();

            // the rim
            if (k.Metal)
            {
                // a bevel: lit along the top, shaded along the bottom
                using var bevel = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f,
                    Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h),
                        new[] { White(0.95f * l.Rim), White(0.3f * l.Rim), Black(0.1f), Black(0.3f) }, new[] { 0f, 0.2f, 0.75f, 1f }, SKShaderTileMode.Clamp),
                };
                c.DrawRoundRect(new SKRoundRect(new SKRect(rect.Left + 0.8f, rect.Top + 0.8f, rect.Right - 0.8f, rect.Bottom - 0.8f), rad - 0.8f), bevel);
            }
            else
            {
                // a specular ring, brightest top left and bottom right; in pearl it carries the orient
                var top = k.Pearl && k.Sheen.Length > 0 ? Mix(SKColors.White, k.Sheen[0], 0.35f) : SKColors.White;
                var bottom = k.Pearl && k.Sheen.Length > 1 ? Mix(SKColors.White, k.Sheen[^1], 0.5f) : SKColors.White;
                using var rim = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f,
                    Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width * 0.35f, h),
                        new[] { A(top, 0.95f * l.Rim), A(top, 0.22f * l.Rim), A(bottom, 0.1f * l.Rim), A(bottom, 0.5f * l.Rim) },
                        new[] { 0f, 0.3f, 0.7f, 1f }, SKShaderTileMode.Clamp),
                };
                c.DrawRoundRect(r, rim);
            }
        }

        /// <summary>A pane raised over what's already drawn (the menu's tip): a soft shadow round it, what's under it put
        /// back to the bare body (of height bodyH), then a key's face, so it reads as a piece of the same glass.</summary>
        public void Pane(SKRect r, float rad, float bodyH)
        {
            if (c == null) return;
            using (var shadow = new SKPaint { Color = Black(k.Light ? 0.12f : 0.3f), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5) })
                c.DrawRoundRect(new SKRect(r.Left, r.Top + 2, r.Right, r.Bottom + 4), rad, rad, shadow);
            c.Save();
            c.ClipRoundRect(new SKRoundRect(r, rad), antialias: true);
            c.Clear(SKColors.Transparent);
            Body(bodyH);
            c.Restore();
            KeyFace(r, rad, false, 1.5f);
        }

        /// <summary>A plain fill of the key colour (a lit row, a group's pane).</summary>
        public void Fill(SKRect r, float radius, float alpha)
        {
            if (c == null) return;
            using var p = new SKPaint { Color = A(k.Key, alpha), IsAntialias = true };
            c.DrawRoundRect(r, radius, radius, p);
        }

        /// <summary>A recess cut into the body (segmented controls, switch grooves, help).</summary>
        public void Recess(SKRect r, float radius)
        {
            if (c == null) return;
            using var p = new SKPaint { Color = Black(k.Light ? 0.07f : 0.2f), IsAntialias = true };
            c.DrawRoundRect(r, radius, radius, p);
            using var shade = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), new[] { Black(k.Light ? 0.14f : 0.25f), White(k.Light ? 0.5f : 0.08f) }, SKShaderTileMode.Clamp),
            };
            c.DrawRoundRect(new SKRect(r.Left + 0.5f, r.Top + 0.5f, r.Right - 0.5f, r.Bottom - 0.5f), radius, radius, shade);
        }

        /// <summary>A raised key's face (no label): lighter glass, satin pearl, or bevelled metal; lit when pointed at.</summary>
        public void KeyFace(SKRect r, float rad, bool lit, float strength = 1)
        {
            if (c == null) return;
            var l = k.Look;
            // lit: a little lighter and ringed (PanelLook.Readability measures text on the lightest of these)
            float a = l.KeyOpacity * strength * (lit ? 1.5f : 1) + (lit ? 0.02f : 0);
            if (k.Metal)
            {
                using (var face = new SKPaint
                       {
                           IsAntialias = true,
                           Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                               new[] { A(k.Key, Math.Min(1, a * 1.5f)), A(k.Key, a * 0.4f), A(k.Key, a * 0.7f) }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp),
                       })
                    c.DrawRoundRect(r, rad, rad, face);
                using var bevel = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1,
                    Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), new[] { White(0.9f), White(0.1f), Black(0.28f) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp),
                };
                c.DrawRoundRect(new SKRect(r.Left + 0.5f, r.Top + 0.5f, r.Right - 0.5f, r.Bottom - 0.5f), rad, rad, bevel);
            }
            else
            {
                using (var face = new SKPaint
                       {
                           IsAntialias = true,
                           Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                               new[] { A(k.Key, a * 1.35f), A(k.Key, a * 0.8f) }, SKShaderTileMode.Clamp),
                       })
                    c.DrawRoundRect(r, rad, rad, face);
                using var hi = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1,
                    Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                        new[] { White((k.Light ? 0.9f : 0.42f) * (0.4f + 0.6f * l.Rim)), White(k.Light ? 0.25f : 0.03f) }, SKShaderTileMode.Clamp),
                };
                c.DrawRoundRect(new SKRect(r.Left + 0.5f, r.Top + 0.5f, r.Right - 0.5f, r.Bottom - 0.5f), rad, rad, hi);
                if (k.Light)
                    using (var shadow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Black(0.08f) })
                        c.DrawLine(r.Left + rad, r.Bottom + 0.5f, r.Right - rad, r.Bottom + 0.5f, shadow);
            }
            if (lit)
                using (var ring = new SKPaint { Color = A(k.Text, 0.5f), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f })
                    c.DrawRoundRect(new SKRect(r.Left + 1.5f, r.Top + 1.5f, r.Right - 1.5f, r.Bottom - 1.5f), rad - 1.5f, rad - 1.5f, ring);
        }

        /// <summary>A labelled key, a capsule unless it's tall.</summary>
        public void Key(SKRect r, string label, bool lit, float size = 16)
        {
            KeyFace(r, Math.Min(r.Height / 2, k.Look.Radius * 0.8f), lit);
            var f = Font(k.Regular, size);
            Text(label, r.MidX - Measure(f, label) / 2, r.MidY + size * 0.36f, f, k.Text);
        }

        /// <summary>The accent key (confirm). Before it's armed it's an outline of the accent and takes no press; armed,
        /// it's solid, a little glossy.</summary>
        public void AccentKey(SKRect r, string label, bool lit, bool armed, float size = 16.5f)
        {
            if (c == null) return;
            float rad = Math.Min(r.Height / 2, k.Look.Radius * 0.8f);
            var f = Font(k.Semi, size);
            float tx = r.MidX - Measure(f, label) / 2, ty = r.MidY + size * 0.36f;
            if (!armed)
            {
                KeyFace(r, rad, false, 0.6f);
                using var ring = new SKPaint { Color = A(k.Accent, 0.75f), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
                c.DrawRoundRect(new SKRect(r.Left + 0.75f, r.Top + 0.75f, r.Right - 0.75f, r.Bottom - 0.75f), rad, rad, ring);
                Text(label, tx, ty, f, k.Dim);
                return;
            }
            var face = lit ? Mix(k.Accent, SKColors.White, 0.14f) : k.Accent;
            using (var halo = new SKPaint { Color = A(k.Accent, 0.45f), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) })
                c.DrawRoundRect(new SKRect(r.Left + 3, r.Top + 4, r.Right - 3, r.Bottom + 2), rad, rad, halo);
            using (var p = new SKPaint
                   {
                       IsAntialias = true,
                       Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                           new[] { Mix(face, SKColors.White, 0.12f), face, Mix(face, SKColors.Black, 0.06f) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp),
                   })
                c.DrawRoundRect(r, rad, rad, p);
            // gloss over the top half
            using (var gloss = new SKPaint
                   {
                       IsAntialias = true,
                       Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.MidY), new[] { White(0.4f), White(0.06f) }, SKShaderTileMode.Clamp),
                   })
                c.DrawRoundRect(new SKRect(r.Left + 3, r.Top + 2, r.Right - 3, r.MidY), rad - 2, rad - 2, gloss);
            if (lit)
                using (var ring = new SKPaint { Color = A(k.OnAccent, 0.55f), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f })
                    c.DrawRoundRect(new SKRect(r.Left + 1.5f, r.Top + 1.5f, r.Right - 1.5f, r.Bottom - 1.5f), rad - 1.5f, rad - 1.5f, ring);
            Text(label, tx, ty, f, k.OnAccent);
        }

        public enum Glyph { Close, Help, Mic, Back, Forward, Reload, Play, Pause, Keyboard, ClearAll, Stack }

        /// <summary>A small round key with a glyph: close, help, the microphone. Quiet until pointed at.</summary>
        public void GlyphKey(SKRect r, Glyph g, bool lit, bool on = false)
        {
            if (c == null) return;
            if (lit || on) KeyFace(r, r.Height / 2, lit, on && !lit ? 1.4f : 1);
            var ink = lit || on ? k.Text : k.Dim;
            using var p = new SKPaint { Color = ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.7f, StrokeCap = SKStrokeCap.Round };
            float cx = r.MidX, cy = r.MidY, s = r.Height * 0.18f;
            switch (g)
            {
                case Glyph.Close:
                    c.DrawLine(cx - s, cy - s, cx + s, cy + s, p);
                    c.DrawLine(cx - s, cy + s, cx + s, cy - s, p);
                    break;
                case Glyph.Help:
                {
                    var f = Font(k.Semi, r.Height * 0.52f);
                    Text("?", cx - Measure(f, "?") / 2, cy + f.Size * 0.36f, f, ink);
                    break;
                }
                case Glyph.Mic:
                    Mic(cx, cy, r.Height * 0.62f, ink);
                    break;
                case Glyph.Back or Glyph.Forward:
                {
                    // a chevron (media's page views)
                    float d = g == Glyph.Back ? 1 : -1, w = s * 0.75f;
                    c.DrawLine(cx + d * w * 0.5f, cy - s, cx - d * w * 0.5f, cy, p);
                    c.DrawLine(cx - d * w * 0.5f, cy, cx + d * w * 0.5f, cy + s, p);
                    break;
                }
                case Glyph.Reload:
                {
                    using var arc = new SKPath();
                    arc.AddArc(new SKRect(cx - s, cy - s, cx + s, cy + s), -60, 290);
                    c.DrawPath(arc, p);
                    float ax = cx + s * MathF.Cos(-60 * MathF.PI / 180), ay = cy + s * MathF.Sin(-60 * MathF.PI / 180);
                    c.DrawLine(ax, ay, ax - s * 0.55f, ay - s * 0.05f, p);
                    c.DrawLine(ax, ay, ax + s * 0.1f, ay + s * 0.55f, p);
                    break;
                }
                case Glyph.Play:
                {
                    using var tri = new SKPath();
                    tri.MoveTo(cx - s * 0.6f, cy - s);
                    tri.LineTo(cx + s * 0.9f, cy);
                    tri.LineTo(cx - s * 0.6f, cy + s);
                    tri.Close();
                    using var fill = new SKPaint { Color = ink, IsAntialias = true };
                    c.DrawPath(tri, fill);
                    break;
                }
                case Glyph.Pause:
                    c.DrawLine(cx - s * 0.45f, cy - s, cx - s * 0.45f, cy + s, p);
                    c.DrawLine(cx + s * 0.45f, cy - s, cx + s * 0.45f, cy + s, p);
                    break;
                case Glyph.Keyboard:
                {
                    // a keyboard: its outline, two rows of keys as dots, a space bar
                    float w = s * 1.9f, h = s * 1.25f;
                    using (var thin = new SKPaint { Color = ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f })
                        c.DrawRoundRect(new SKRect(cx - w, cy - h, cx + w, cy + h), s * 0.3f, s * 0.3f, thin);
                    using var dot = new SKPaint { Color = ink, IsAntialias = true };
                    for (int row = 0; row < 2; row++)
                        for (int i = 0; i < 5; i++)
                            c.DrawCircle(cx - w * 0.64f + i * w * 0.32f, cy - h * 0.48f + row * h * 0.5f, s * 0.11f, dot);
                    c.DrawLine(cx - w * 0.42f, cy + h * 0.5f, cx + w * 0.42f, cy + h * 0.5f, p);
                    break;
                }
                case Glyph.ClearAll:
                {
                    // a short stack of lines, the last with a small × beside it: clear them all
                    float w = s * 1.15f;
                    c.DrawLine(cx - w, cy - s * 0.8f, cx + w, cy - s * 0.8f, p);
                    c.DrawLine(cx - w, cy, cx + w * 0.2f, cy, p);
                    c.DrawLine(cx - w, cy + s * 0.8f, cx - w * 0.1f, cy + s * 0.8f, p);
                    float xx = cx + w * 0.62f, xy = cy + s * 0.55f, xs = s * 0.38f;
                    c.DrawLine(xx - xs, xy - xs, xx + xs, xy + xs, p);
                    c.DrawLine(xx - xs, xy + xs, xx + xs, xy - xs, p);
                    break;
                }
                case Glyph.Stack:
                {
                    // cards stacked: the replies so far
                    float w = s * 1.05f, h = s * 0.62f;
                    using var thin = new SKPaint { Color = ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeJoin = SKStrokeJoin.Round };
                    c.DrawRoundRect(new SKRect(cx - w, cy - h + s * 0.35f, cx + w, cy + h + s * 0.35f), s * 0.22f, s * 0.22f, thin);
                    c.DrawLine(cx - w * 0.78f, cy - h - s * 0.05f, cx + w * 0.78f, cy - h - s * 0.05f, p);
                    c.DrawLine(cx - w * 0.56f, cy - h - s * 0.45f, cx + w * 0.56f, cy - h - s * 0.45f, p);
                    break;
                }
            }
        }

        /// <summary>A microphone, about <paramref name="h"/> tall, centred.</summary>
        public void Mic(float cx, float cy, float h, SKColor ink)
        {
            if (c == null) return;
            using var p = new SKPaint { Color = ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, h * 0.09f), StrokeCap = SKStrokeCap.Round };
            float w = h * 0.34f, top = cy - h * 0.5f, capB = cy + h * 0.1f;
            using (var fill = new SKPaint { Color = ink, IsAntialias = true })
                c.DrawRoundRect(new SKRect(cx - w / 2, top, cx + w / 2, capB), w / 2, w / 2, fill);
            using var arc = new SKPath();
            float ar = w * 0.95f;
            arc.AddArc(new SKRect(cx - ar, capB - ar * 1.55f, cx + ar, capB + ar * 0.45f), 15, 150);
            c.DrawPath(arc, p);
            c.DrawLine(cx, capB + ar * 0.45f, cx, cy + h * 0.5f, p);
        }

        /// <summary>Listening: an orb lit in the accent (the same light as an "on" switch's bead) that breathes (phase 0-15).</summary>
        public void Orb(float cx, float cy, int phase)
        {
            if (c == null) return;
            float t = 0.5f - 0.5f * MathF.Cos(phase / 16f * MathF.Tau);
            using (var halo = new SKPaint
                   {
                       IsAntialias = true,
                       Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), 15 + 5 * t, new[] { A(k.Accent, 0.5f), A(k.Accent, 0.16f + 0.12f * t), A(k.Accent, 0) },
                           new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp),
                   })
                c.DrawCircle(cx, cy, 20 + 5 * t, halo);
            using var core = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(new SKPoint(cx - 2, cy - 3), 10, new[] { Mix(k.Accent, SKColors.White, 0.45f), k.Accent }, SKShaderTileMode.Clamp),
            };
            c.DrawCircle(cx, cy, 7.5f + 1.2f * t, core);
        }

        /// <summary>Working: three dots brightening in turn (phase 0-15).</summary>
        public void Dots(float x, float cy, int phase)
        {
            if (c == null) return;
            using var p = new SKPaint { IsAntialias = true };
            for (int i = 0; i < 3; i++)
            {
                float t = 0.5f + 0.5f * MathF.Cos((phase / 16f - i / 6f) * MathF.Tau);
                p.Color = A(k.Text, 0.25f + 0.75f * t);
                c.DrawCircle(x + i * 9, cy, 3.1f, p);
            }
        }

        /// <summary>Which page of a long answer this is: a dot a page, the current one in full ink (past nine, its number
        /// and the count).</summary>
        public void PageDots(float cx, float cy, int pages, int page)
        {
            if (c == null) return;
            if (pages > 9)
            {
                var f = Font(k.Regular, 13.5f);
                var label = $"{page + 1} / {pages}";
                Text(label, cx - Measure(f, label) / 2, cy + 5, f, k.Dim);
                return;
            }
            using var p = new SKPaint { IsAntialias = true };
            for (int i = 0; i < pages; i++)
            {
                p.Color = i == page ? k.Text : A(k.Text, 0.25f);
                c.DrawCircle(cx + (i - (pages - 1) / 2f) * 14, cy, i == page ? 3.6f : 3f, p);
            }
        }

        /// <summary>The pointer's mark in the world, small: so "that" on the panel reads as the dot out there.</summary>
        public void TargetDot(float cx, float cy)
        {
            if (c == null) return;
            using var p = new SKPaint { IsAntialias = true, Color = new SKColor(243, 194, 47) };
            c.DrawCircle(cx, cy, 4, p);
            using var ring = new SKPaint { IsAntialias = true, Color = new SKColor(243, 194, 47, 90), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f };
            c.DrawCircle(cx, cy, 7, ring);
        }

        /// <summary>An on/off switch made of the panel's own stuff: a bead in a groove cut into the body. Off, the bead
        /// rests at the left, the same glass as the keys; on, it sits at the right and is lit in the accent, and the
        /// groove holds a little of its light. Read by where the bead is first (no colour needed), by the light second.</summary>
        public void Switch(SKRect r, bool on, bool lit, bool enabled)
        {
            if (c == null) return;
            float rad = r.Height / 2;
            float fade = enabled ? 1 : 0.4f;
            Recess(r, rad);
            float br = rad - 4;
            float bx = on ? r.Right - rad : r.Left + rad;
            if (on)
            {
                // the groove takes the bead's light, strongest near it
                using var glow = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateLinearGradient(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0),
                        new[] { A(k.Accent, 0.1f * fade), A(k.Accent, 0.38f * fade) }, SKShaderTileMode.Clamp),
                };
                c.DrawRoundRect(r, rad, rad, glow);
                using (var halo = new SKPaint { Color = A(k.Accent, 0.4f * fade), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3.5f) })
                    c.DrawCircle(bx, r.MidY, br + 1.5f, halo);
                using (var bead = new SKPaint
                       {
                           IsAntialias = true,
                           Shader = SKShader.CreateRadialGradient(new SKPoint(bx - br * 0.35f, r.MidY - br * 0.4f), br * 1.5f,
                               new[] { A(Mix(k.Accent, SKColors.White, 0.35f), fade), A(k.Accent, fade) }, SKShaderTileMode.Clamp),
                       })
                    c.DrawCircle(bx, r.MidY, br, bead);
            }
            else
            {
                using (var shadow = new SKPaint { Color = Black((k.Light ? 0.14f : 0.3f) * fade), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f) })
                    c.DrawCircle(bx, r.MidY + 0.8f, br, shadow);
                var face = k.Light ? White(0.92f * fade) : A(Mix(k.Tint, SKColors.White, 0.42f), fade);
                using (var bead = new SKPaint
                       {
                           IsAntialias = true,
                           Shader = SKShader.CreateRadialGradient(new SKPoint(bx - br * 0.35f, r.MidY - br * 0.4f), br * 1.5f,
                               new[] { face, Mix(face, Black(face.Alpha / 255f), 0.1f) }, SKShaderTileMode.Clamp),
                       })
                    c.DrawCircle(bx, r.MidY, br, bead);
            }
            if (lit)
                using (var ring = new SKPaint { Color = A(k.Text, 0.5f), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f })
                    c.DrawRoundRect(new SKRect(r.Left - 2.5f, r.Top - 2.5f, r.Right + 2.5f, r.Bottom + 2.5f), rad + 2.5f, rad + 2.5f, ring);
        }

        /// <summary>One segment of a segmented control (the control's recess drawn first).</summary>
        public void Segment(SKRect r, string label, bool selected, bool lit, bool enabled, float size = 14.5f)
        {
            if (c == null) return;
            if (selected) KeyFace(r, r.Height / 2, false, 1.5f);
            else if (lit) KeyFace(r, r.Height / 2, false, 0.9f);
            if (lit)
                using (var ring = new SKPaint { Color = A(k.Text, 0.5f), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f })
                    c.DrawRoundRect(new SKRect(r.Left + 1.5f, r.Top + 1.5f, r.Right - 1.5f, r.Bottom - 1.5f), r.Height / 2 - 1.5f, r.Height / 2 - 1.5f, ring);
            var f = Font(selected ? k.Semi : k.Regular, size);
            Text(label, r.MidX - Measure(f, label) / 2, r.MidY + size * 0.36f, f, !enabled ? k.Faint : selected || lit ? k.Text : k.Dim);
        }

        /// <summary>A look as a small round sample of its material, ringed when it's the one in use.</summary>
        public void Swatch(float cx, float cy, float rad, PanelLook look, bool selected, bool lit)
        {
            if (c == null) return;
            var s = new Ink(look);
            var circle = new SKRect(cx - rad, cy - rad, cx + rad, cy + rad);
            c.Save();
            using (var clip = new SKPath()) { clip.AddOval(circle); c.ClipPath(clip, antialias: true); }
            // a neutral world behind it, so glass shows as glass
            using (var world = new SKPaint
                   {
                       IsAntialias = true,
                       Shader = SKShader.CreateLinearGradient(new SKPoint(cx, cy - rad), new SKPoint(cx, cy + rad),
                           new[] { new SKColor(150, 170, 196), new SKColor(96, 110, 92) }, SKShaderTileMode.Clamp),
                   })
                c.DrawRect(circle, world);
            using (var body = new SKPaint { Color = A(s.Tint, look.TintOpacity), IsAntialias = true })
                c.DrawRect(circle, body);
            if (look.Glow > look.TintOpacity)
                using (var glow = new SKPaint { Color = A(s.Tint, look.Glow - look.TintOpacity), IsAntialias = true, BlendMode = SKBlendMode.Plus })
                    c.DrawRect(circle, glow);
            if (s.Sheen.Length > 0)
            {
                var cols = s.Metal
                    ? new[] { A(s.Sheen[0], look.SheenOpacity), A(s.Sheen[^1], look.SheenOpacity * 0.8f), A(s.Sheen[0], look.SheenOpacity * 0.6f) }
                    : s.Sheen.Concat(s.Sheen.Take(1)).Select(g => A(g, look.SheenOpacity * (s.Pearl ? 0.8f : 1.4f))).ToArray();
                using var sheen = new SKPaint
                {
                    IsAntialias = true,
                    Shader = s.Metal
                        ? SKShader.CreateLinearGradient(new SKPoint(cx, cy - rad), new SKPoint(cx, cy + rad), cols, SKShaderTileMode.Clamp)
                        : SKShader.CreateLinearGradient(new SKPoint(cx - rad, cy - rad), new SKPoint(cx + rad, cy + rad), cols, SKShaderTileMode.Clamp),
                };
                c.DrawRect(circle, sheen);
            }
            // a dome's highlight
            using (var dome = new SKPaint
                   {
                       IsAntialias = true,
                       Shader = SKShader.CreateRadialGradient(new SKPoint(cx - rad * 0.3f, cy - rad * 0.45f), rad * 0.9f, new[] { White(0.55f), White(0) }, SKShaderTileMode.Clamp),
                   })
                c.DrawRect(circle, dome);
            // a dot of its accent
            using (var acc = new SKPaint { Color = s.Accent, IsAntialias = true })
                c.DrawCircle(cx + rad * 0.32f, cy + rad * 0.32f, rad * 0.2f, acc);
            c.Restore();
            using (var rim = new SKPaint { Color = White(0.6f), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
                c.DrawOval(circle, rim);
            if (selected || lit)
                using (var ring = new SKPaint { Color = A(k.Text, selected ? 0.95f : 0.45f), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
                    c.DrawCircle(cx, cy, rad + 4, ring);
        }

        public void Text(string text, float x, float y, SKFont font, SKColor color)
        {
            if (c == null || text.Length == 0) return;
            using var p = new SKPaint { Color = color, IsAntialias = true };
            c.DrawText(text, x, y, SKTextAlign.Left, FontFor(font, text), p);
        }

        public void TextRight(string text, float right, float y, SKFont font, SKColor color) => Text(text, right - Measure(font, text), y, font, color);

        /// <summary>A hairline between rows.</summary>
        public void Hairline(float x0, float x1, float y)
        {
            if (c == null) return;
            using var p = new SKPaint { Color = A(k.Text, k.Light ? 0.12f : 0.1f), StrokeWidth = 1 };
            c.DrawLine(x0, y + 0.5f, x1, y + 0.5f, p);
        }

        /// <summary>The assistant's spark: a four-pointed star marking what it made (its cards, its text in the reader).</summary>
        internal static void Spark(Painter draw, float cx, float cy, float r, SKColor colour)
        {
            if (!draw.Drawing) return;
            using var path = new SKPath();
            path.MoveTo(cx, cy - r);
            path.QuadTo(cx + r * 0.16f, cy - r * 0.16f, cx + r, cy);
            path.QuadTo(cx + r * 0.16f, cy + r * 0.16f, cx, cy + r);
            path.QuadTo(cx - r * 0.16f, cy + r * 0.16f, cx - r, cy);
            path.QuadTo(cx - r * 0.16f, cy - r * 0.16f, cx, cy - r);
            path.Close();
            using var p = new SKPaint { IsAntialias = true, Color = colour };
            draw.Canvas!.DrawPath(path, p);
        }
    }
}
