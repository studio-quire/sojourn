using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Sojourn.Session.Assistant;

/// <summary>
/// Text in a look's face with fallback per character, for every sheet drawn with Skia (the panel, the feed, name tags,
/// bubbles, floating text, the reader, cards, the map). Residents write in every script and decorate with emoji,
/// symbols and "fancy" letters (𝓗𝓮𝓵𝓵𝓸, ღ, ✿), which Archivo and the system faces mostly lack. The text is cut into
/// grapheme clusters (a ZWJ family, a skin-toned thumb, a keycap and a letter with its accents are one each), each
/// cluster drawn in the first face that has it: Segoe UI Emoji for emoji (its COLR glyphs draw in colour), the look's
/// face, Segoe UI Symbol, then whatever face the system's fallback offers; neighbours in one face make one run. A run
/// with a cluster of several code points is shaped with HarfBuzz, so sequences become their single glyphs. Measure and
/// Draw read the same runs, so wrapping matches what's drawn. Pure ASCII (most text) takes the look's face directly;
/// the rest is laid out once per (face, text) and cached. Claude Opus 5.5, 2026-10-31.
/// </summary>
internal static class FallbackText
{
    /// <summary>One run: its text (in its face, with invisible selectors and joiners dropped unless shaped) and, when
    /// it needs shaping, its glyphs at RefSize.</summary>
    sealed record Run(string Text, SKTypeface Face, Shaped? Shape);

    sealed record Shaped(ushort[] Glyphs, SKPoint[] Points, float Width);

    const float RefSize = 64;
    const int MaxCached = 4096;

    static readonly ConcurrentDictionary<(SKTypeface, string), Run[]> Layouts = new();
    static readonly ConcurrentDictionary<(SKTypeface, int), bool> Covered = new();
    // by the language in use too: Chinese and Japanese share characters drawn differently (Yu Gothic for Japanese,
    // Microsoft JhengHei for the viewers' Traditional Chinese), and the system's fallback picks by the language's tag
    static readonly ConcurrentDictionary<(SKTypeface, int, string), SKTypeface?> Matched = new();

    static FallbackText() => Lang.Changed += () => Layouts.Clear();
    static readonly Dictionary<SKTypeface, SKShaper> Shapers = new();

    static readonly SKTypeface? Emoji = System("Segoe UI Emoji");
    static readonly SKTypeface? Symbol = System("Segoe UI Symbol");

    static SKTypeface? System(string family) =>
        SKTypeface.FromFamilyName(family) is { } f && f.FamilyName == family ? f : null;

    public static float Measure(SKFont font, string text)
    {
        if (Ascii(text)) return font.MeasureText(text);
        float w = 0;
        foreach (var run in Lay(font.Typeface ?? SKTypeface.Default, text))
        {
            if (run.Shape is { } sh) w += sh.Width * font.Size / RefSize;
            else if (run.Face == font.Typeface) w += font.MeasureText(run.Text);
            else
            {
                using var f = In(font, run.Face);
                w += f.MeasureText(run.Text);
            }
        }
        return w;
    }

    /// <summary>Draws text from x on the baseline y.</summary>
    public static void Draw(SKCanvas c, string text, float x, float y, SKFont font, SKPaint paint)
    {
        if (text.Length == 0) return;
        if (Ascii(text))
        {
            c.DrawText(text, x, y, SKTextAlign.Left, font, paint);
            return;
        }
        foreach (var run in Lay(font.Typeface ?? SKTypeface.Default, text))
        {
            if (run.Shape == null && run.Face == font.Typeface)
            {
                c.DrawText(run.Text, x, y, SKTextAlign.Left, font, paint);
                x += font.MeasureText(run.Text);
                continue;
            }
            using var f = In(font, run.Face);
            if (run.Shape is { } sh)
            {
                float k = font.Size / RefSize;
                var points = new SKPoint[sh.Points.Length];
                for (int i = 0; i < points.Length; i++) points[i] = new SKPoint(sh.Points[i].X * k, sh.Points[i].Y * k);
                using var builder = new SKTextBlobBuilder();
                builder.AddPositionedRun(sh.Glyphs, f, points);
                using var blob = builder.Build();
                if (blob != null) c.DrawText(blob, x, y, paint);
                x += sh.Width * k;
            }
            else
            {
                c.DrawText(run.Text, x, y, SKTextAlign.Left, f, paint);
                x += f.MeasureText(run.Text);
            }
        }
    }

    /// <summary>The last place before n where the text can be cut without splitting a character (a surrogate pair, an
    /// emoji sequence, a letter and its accents); 0 when there's none.</summary>
    public static int Before(string s, int n)
    {
        if (n <= 1) return 0;
        if (Ascii(s)) return n - 1;
        int at = 0, best = 0;
        while (at < s.Length)
        {
            int next = at + StringInfo.GetNextTextElementLength(s.AsSpan(at));
            if (next >= n) break;
            best = at = next;
        }
        return best;
    }

    static bool Ascii(string s)
    {
        foreach (var ch in s) if (ch >= 0x80) return false;
        return true;
    }

    static SKFont In(SKFont like, SKTypeface face) =>
        new(face, like.Size) { Edging = like.Edging, Subpixel = like.Subpixel, Hinting = like.Hinting, ScaleX = like.ScaleX, Embolden = like.Embolden };

    static Run[] Lay(SKTypeface primary, string text)
    {
        if (Layouts.TryGetValue((primary, text), out var got)) return got;
        if (Layouts.Count > MaxCached) Layouts.Clear();
        return Layouts[(primary, text)] = Split(primary, text);
    }

    static Run[] Split(SKTypeface primary, string text)
    {
        string s = text;
        try { if (!s.IsNormalized()) s = s.Normalize(); }   // "e" + U+0301 as "é", which the face is likelier to have
        catch (ArgumentException) { }                        // a lone surrogate: left as it is
        var runs = new List<Run>();
        SKTypeface? face = null;
        int start = 0;
        bool shape = false;
        for (int i = 0; i < s.Length;)
        {
            int len = StringInfo.GetNextTextElementLength(s.AsSpan(i));
            var cluster = s.AsSpan(i, len);
            var f = FaceFor(primary, cluster);
            if (f != face)
            {
                if (face != null) runs.Add(Make(s[start..i], face, shape));
                (face, start, shape) = (f, i, false);
            }
            // several visible code points in one cluster (a sequence, a mark the face didn't compose) want shaping;
            // in the look's own face they're drawn as they come, as before
            if (f != primary && Visible(cluster) > 1) shape = true;
            i += len;
        }
        if (face != null) runs.Add(Make(s[start..], face, shape));
        return runs.ToArray();
    }

    static Run Make(string text, SKTypeface face, bool shape)
    {
        if (!shape) return new Run(Strip(text), face, null);
        var shaper = ShaperFor(face);
        SKShaper.Result r;
        lock (shaper)
        {
            using var f = new SKFont(face, RefSize);
            r = shaper.Shape(text, f);
        }
        var glyphs = new ushort[r.Codepoints.Length];
        for (int i = 0; i < glyphs.Length; i++) glyphs[i] = (ushort)r.Codepoints[i];
        return new Run(text, face, new Shaped(glyphs, r.Points, r.Width));
    }

    static SKShaper ShaperFor(SKTypeface face)
    {
        lock (Shapers)
        {
            if (!Shapers.TryGetValue(face, out var s)) Shapers[face] = s = new SKShaper(face);
            return s;
        }
    }

    /// <summary>The face for one cluster.</summary>
    static SKTypeface FaceFor(SKTypeface primary, ReadOnlySpan<char> cluster)
    {
        Rune.DecodeFromUtf16(cluster, out var first, out _);
        int cp = first.Value;
        if (Emoji != null && WantsEmoji(cluster, cp) && Has(Emoji, cp)) return Emoji;
        if (Covers(primary, cluster)) return primary;
        // symbols look for Segoe UI Symbol first; letters for the face the system pairs with the look's
        var match = Matched.GetOrAdd((primary, cp, Lang.Current.Tag), k =>
            SKFontManager.Default.MatchCharacter(k.Item1.FamilyName, k.Item1.FontStyle, [k.Item3], k.Item2));
        var order = Rune.GetUnicodeCategory(first) is UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol
            or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherPunctuation ? (Symbol, match) : (match, Symbol);
        if (order.Item1 != null && Covers(order.Item1, cluster)) return order.Item1;
        if (order.Item2 != null && Covers(order.Item2, cluster)) return order.Item2;
        if (order.Item1 != null && Has(order.Item1, cp)) return order.Item1;
        if (order.Item2 != null && Has(order.Item2, cp)) return order.Item2;
        return primary;
    }

    static bool Has(SKTypeface face, int cp) => Covered.GetOrAdd((face, cp), k => k.Item1.GetGlyph(k.Item2) != 0);

    static bool Covers(SKTypeface face, ReadOnlySpan<char> cluster)
    {
        foreach (var r in cluster.EnumerateRunes())
            if (!Invisible(r.Value) && !Has(face, r.Value)) return false;
        return true;
    }

    static int Visible(ReadOnlySpan<char> cluster)
    {
        int n = 0;
        foreach (var r in cluster.EnumerateRunes()) if (!Invisible(r.Value)) n++;
        return n;
    }

    /// <summary>Joiners and selectors: they change how their neighbours look and have no glyph of their own (drawn
    /// unshaped they'd show as boxes).</summary>
    static bool Invisible(int cp) => cp is 0x200C or 0x200D or >= 0xFE00 and <= 0xFE0F or >= 0xE0000 and <= 0xE007F or >= 0xE0100 and <= 0xE01EF;

    static string Strip(string s)
    {
        bool any = false;
        foreach (var r in s.EnumerateRunes()) if (Invisible(r.Value)) { any = true; break; }
        if (!any) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes()) if (!Invisible(r.Value)) sb.Append(r.ToString());
        return sb.ToString();
    }

    /// <summary>Whether a cluster shows as an emoji: its first character is one by default (Emoji_Presentation), or a
    /// pictograph asked to be one (U+FE0F, a skin tone, a ZWJ sequence), or a keycap; U+FE0E asks for text.</summary>
    static bool WantsEmoji(ReadOnlySpan<char> cluster, int first)
    {
        bool vs16 = false, joined = false, tone = false, keycap = false;
        foreach (var r in cluster.EnumerateRunes())
            switch (r.Value)
            {
                case 0xFE0E: return false;
                case 0xFE0F: vs16 = true; break;
                case 0x200D: joined = true; break;
                case 0x20E3: keycap = true; break;
                case >= 0x1F3FB and <= 0x1F3FF: tone = true; break;
            }
        if (keycap && first is '#' or '*' or >= '0' and <= '9') return true;
        if (EmojiByDefault(first)) return true;
        bool pictograph = first is >= 0x2190 and <= 0x2BFF or 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139
            or 0x3030 or 0x303D or 0x3297 or 0x3299 or >= 0x1F000 and <= 0x1FAFF;
        return pictograph && (vs16 || joined || tone);
    }

    /// <summary>Emoji_Presentation: nearly everything from U+1F000 on, and these in the BMP.</summary>
    static bool EmojiByDefault(int cp) => cp >= 0x1F000 && cp <= 0x1FAFF || cp is 0x231A or 0x231B or >= 0x23E9 and <= 0x23EC
        or 0x23F0 or 0x23F3 or 0x25FD or 0x25FE or 0x2614 or 0x2615 or >= 0x2648 and <= 0x2653 or 0x267F or 0x2693 or 0x26A1
        or 0x26AA or 0x26AB or 0x26BD or 0x26BE or 0x26C4 or 0x26C5 or 0x26CE or 0x26D4 or 0x26EA or 0x26F2 or 0x26F3 or 0x26F5
        or 0x26FA or 0x26FD or 0x2705 or 0x270A or 0x270B or 0x2728 or 0x274C or 0x274E or >= 0x2753 and <= 0x2755 or 0x2757
        or >= 0x2795 and <= 0x2797 or 0x27B0 or 0x27BF or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55;
}
