using SkiaSharp;
using Sojourn.Client.Assistant;
using static Sojourn.Session.Assistant.Panel;

namespace Sojourn.Client.Showing;

/// <summary>An element's moving parts as of this frame, worked out on the main thread (quantised, so a redraw happens
/// only when the picture would change): a timer's seconds, which way something is and how far, a map's marks.</summary>
public readonly record struct BlockLive(int Seconds = 0, bool Done = false, int Bearing = 0, int Distance = 0, bool Known = false,
    (int X, int Y, bool Known)[]? Marks = null, int MapRadius = 0, int North = 0)
{
    public string Key => $"{Seconds},{Done},{Bearing},{Distance},{Known},{MapRadius},{North},{(Marks == null ? "" : string.Join(";", Marks))}";
}

/// <summary>
/// Draws the assistant's elements in the panel's look, so everything on screen reads as one system: a card (a glass
/// body, a header with the assistant's spark and the title, the blocks, a close key) or, for one short line, a capsule
/// (the spark and the words). The spark marks them as the assistant's: nothing a person, an object or the client says
/// carries it. Only ink, dim and faint: the accent (the light that means "this needs you": the confirm key, an on
/// switch) never appears, and nothing but the close key looks pressable.
/// </summary>
public static class ElementPainter
{
    const float Pad = 16, Gap = 10, HeaderH = 40, LabelH = 34;
    const float ImageH = 180, MapMax = 220, PictureMaxH = 440;

    public sealed record Drawn(byte[] Rgba, int Width, int Height, float Scale, SKRect? Close, float Frost, float Edge)
    {
        public float LogicalWidth => Width / Scale;
        public float LogicalHeight => Height / Scale;
    }

    internal static float WidthOf(ElementSpec e, Ink k, float widen = 1)
    {
        if (e.IsLabel)
        {
            var t = ((TextBlock)e.Blocks[0]);
            var f = Font(t.Style == TextStyle.Title ? k.Semi : k.Regular, 15);
            return MathF.Min(Caps.WorldWidth, MathF.Ceiling(30 + Measure(f, t.Text) + 15));
        }
        // as wide as what it holds wants (a timer stays small, a table takes what its columns need), within the caps
        bool wide = e.Blocks.Any(b => b is ImageBlock or MapBlock || b is TableBlock { Rows: [{ Length: >= 3 }, ..] } || b is ChartBlock { Line: true });
        bool picture = e.Blocks.Any(b => b is ImageBlock { IsPicture: true });
        float max = e.At.InWorld ? Caps.WorldWidth : picture ? Caps.PictureCardWidth : wide ? Caps.WideCardWidth : Caps.CardWidth;
        float want = e.Title is { } title ? 18 + Measure(Font(k.Semi, 16), title) + 44 - Pad : 0;
        foreach (var b in e.Blocks) want = MathF.Max(want, Natural(b, k));
        // with the person, wider at a smaller size (UiSize.Wide): more on each line, about the same space
        if (!e.At.InWorld) max *= widen;
        return MathF.Ceiling(Math.Clamp(want + 2 * Pad, 200, max));
    }

    /// <summary>How wide a block would like to be (logical px, without the card's padding).</summary>
    static float Natural(Block b, Ink k) => b switch
    {
        TextBlock t => t.Style switch
        {
            TextStyle.Big => Measure(Font(k.Semi, 34), t.Text),
            TextStyle.Title => Measure(Font(k.Semi, 17), t.Text),
            TextStyle.Dim => Measure(Font(k.Regular, 13.5f), t.Text),
            _ => Measure(Font(k.Regular, 15.5f), t.Text),
        },
        ListBlock l => 24 + l.Items.Max(i => Measure(Font(k.Regular, 15.5f), i.Text) + (i.Detail is { } d ? 14 + Measure(Font(k.Regular, 13.5f), d) : 0)),
        TableBlock t => Enumerable.Range(0, t.Rows[0].Length).Sum(c => t.Rows.Select((r, i) => Measure(Font(i == 0 && t.Header ? k.Semi : k.Regular, i == 0 && t.Header ? 13 : 14.5f), r[c])).Max())
                        + 14 * (t.Rows[0].Length - 1),
        TimerBlock => 54 + Measure(Font(k.Semi, 30), "00:00"),
        ProgressBlock => 240,
        ChartBlock { Line: true } => 9999,
        ChartBlock => 260,
        TowardBlock t => 64 + Math.Max(Measure(Font(k.Semi, 24), "000 m"), t.Label is { } l ? Measure(Font(k.Regular, 14.5f), l) : 0),
        // a saved picture: as wide as it is at its tallest (an upright one makes a narrower card)
        ImageBlock { IsPicture: true } im => PictureMaxH * im.Width / im.Height,
        _ => 9999,
    };

    /// <summary>The height it would be drawn at (logical px), for the caps.</summary>
    public static float HeightOf(ElementSpec e, PanelLook look)
    {
        var k = Ink.For(look);
        float w = WidthOf(e, k);
        return e.IsLabel ? LabelH : Layout(new Painter(null, k, w), e, Array.Empty<BlockLive>(), _ => null, false);
    }

    /// <summary>layer: for the headset's quad layer (a card with the person while it runs), not drawn into the eyes
    /// (GlassLight).</summary>
    public static Drawn Draw(ElementSpec e, IReadOnlyList<BlockLive> live, Func<ImageBlock, SKImage?> images, PanelLook look, float scale, bool closeLit, float wide = 1,
        bool layer = false)
    {
        var k = Ink.For(look);
        float w = WidthOf(e, k, wide);
        SKRect? close = null;
        var (px, pw, ph, used) = Rasterise(scale, w, new List<PanelKey>(), (c, height) =>
        {
            var draw = new Painter(c, k, w);
            if (e.IsLabel) return DrawLabel(draw, (TextBlock)e.Blocks[0], height);
            float h = Layout(draw, e, live, images, closeLit, height);
            close = CloseRect(w);
            return h;
        }, Panel.MaxRasterWidth);
        GlassLight.Finish(px, look, layer);
        return new Drawn(px, pw, ph, used, e.IsLabel ? null : close, look.Frost, look.TintOpacity);
    }

    public static SKRect CloseRect(float w) => new(w - 8 - 30, 6, w - 8, 36);

    static float DrawLabel(Painter draw, TextBlock t, float height)
    {
        var k = draw.K;
        draw.Body(LabelH);
        Spark(draw, 16, LabelH / 2, 5.5f, k.Dim);
        var f = Font(t.Style == TextStyle.Title ? k.Semi : k.Regular, 15);
        draw.Text(Ellipsize(t.Text, f, draw.W - 30 - 13), 30, LabelH / 2 + 5.3f, f, k.Text);
        return LabelH;
    }

    static float Layout(Painter draw, ElementSpec e, IReadOnlyList<BlockLive> live, Func<ImageBlock, SKImage?> images, bool closeLit, float height = 0)
    {
        var k = draw.K;
        float W = draw.W, x0 = Pad, x1 = W - Pad;
        draw.Body(height);
        // the header: the assistant's spark, the title, the close key
        Spark(draw, Pad + 5, 21, 6, k.Dim);
        if (e.Title is { } title)
        {
            var tf = Font(k.Semi, 16);
            draw.Text(Ellipsize(title, tf, W - Pad - 20 - 44), Pad + 18, 26.5f, tf, k.Text);
        }
        if (draw.Drawing) draw.GlyphKey(CloseRect(W), Painter.Glyph.Close, closeLit);
        // a card without a title keeps a slimmer header: the spark and the close key
        float y = e.Title != null ? HeaderH : HeaderH - 6;
        for (int i = 0; i < e.Blocks.Count; i++)
        {
            var bl = i < live.Count ? live[i] : default;
            y += Block(draw, e.Blocks[i], bl, images, x0, x1, y) + Gap;
        }
        return MathF.Max(y - Gap + 14, HeaderH + 8);
    }

    static float Block(Painter draw, Block b, BlockLive live, Func<ImageBlock, SKImage?> images, float x0, float x1, float y) => b switch
    {
        TextBlock t => Text(draw, t, x0, x1, y),
        ListBlock l => List(draw, l, x0, x1, y),
        TableBlock t => Table(draw, t, x0, x1, y),
        TimerBlock t => Timer(draw, t, live, x0, y),
        ProgressBlock p => Progress(draw, p, x0, x1, y),
        ChartBlock c => c.Line ? Line(draw, c, x0, x1, y) : Bars(draw, c, x0, x1, y),
        ImageBlock im => Image(draw, im, images(im), x0, x1, y),
        TowardBlock t => Toward(draw, t, live, x0, x1, y),
        MapBlock m => Map(draw, m, live, x0, x1, y),
        _ => 0,
    };

    // --- text, lists, tables -----------------------------------------------------------------------------------------

    static float Text(Painter draw, TextBlock t, float x0, float x1, float y)
    {
        var k = draw.K;
        var (face, size, line, colour, max) = t.Style switch
        {
            TextStyle.Title => (k.Semi, 17f, 23f, k.Text, 3),
            TextStyle.Big => (k.Semi, 34f, 40f, k.Text, 1),
            TextStyle.Dim => (k.Regular, 13.5f, 19f, k.Dim, 6),
            _ => (k.Regular, 15.5f, 21f, k.Text, 12),
        };
        var f = Font(face, size);
        var lines = Wrap(t.Text, f, x1 - x0, max);
        float yy = y;
        foreach (var l in lines) { draw.Text(l, x0, yy + line * 0.74f, f, colour); yy += line; }
        return yy - y;
    }

    static float List(Painter draw, ListBlock l, float x0, float x1, float y)
    {
        var k = draw.K;
        var f = Font(k.Regular, 15.5f);
        var df = Font(k.Regular, 13.5f);
        var nf = Font(k.Regular, 13.5f);
        bool checks = l.Items.Any(i => i.Done != null);
        float tx = x0 + (checks || l.Numbered ? 24 : 16);
        float yy = y;
        for (int i = 0; i < l.Items.Count; i++)
        {
            var it = l.Items[i];
            float mid = yy + 10.5f;
            if (checks) Check(draw, x0 + 8, mid, it.Done == true);
            else if (l.Numbered) draw.Text((i + 1).ToString(), x0 + 1, yy + 15.5f, nf, k.Faint);
            else if (draw.Drawing)
                using (var p = new SKPaint { IsAntialias = true, Color = A(k.Dim, 0.8f) }) draw.Canvas!.DrawCircle(x0 + 4, mid, 2.2f, p);
            var colour = it.Done == true ? k.Dim : k.Text;
            float detailW = it.Detail is { } d ? Panel.Measure(df, d) : 0;
            bool inline_ = it.Detail != null && Panel.Measure(f, it.Text) + 14 + detailW <= x1 - tx;
            var lines = Wrap(it.Text, f, x1 - tx, 2);
            foreach (var line in lines) { draw.Text(line, tx, yy + 15.5f, f, colour); yy += 21; }
            if (it.Detail is { } det)
            {
                if (inline_) draw.TextRight(det, x1, yy - 21 + 15.5f, df, k.Dim);
                else { draw.Text(Ellipsize(det, df, x1 - tx), tx, yy + 13.5f, df, k.Dim); yy += 18; }
            }
            yy += 4;
        }
        return yy - 4 - y;
    }

    /// <summary>A checklist's mark: an open ring, or a disc of the ink with a check cut out of it in the body's colour.</summary>
    static void Check(Painter draw, float cx, float cy, bool done)
    {
        if (!draw.Drawing) return;
        var k = draw.K;
        var c = draw.Canvas!;
        if (!done)
        {
            using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, Color = A(k.Dim, 0.9f) };
            c.DrawCircle(cx, cy, 7, ring);
            return;
        }
        using (var disc = new SKPaint { IsAntialias = true, Color = A(k.Text, 0.82f) }) c.DrawCircle(cx, cy, 7.6f, disc);
        using var tick = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = k.Tint.WithAlpha(255) };
        using var path = new SKPath();
        path.MoveTo(cx - 3.4f, cy + 0.2f);
        path.LineTo(cx - 0.9f, cy + 2.7f);
        path.LineTo(cx + 3.6f, cy - 2.6f);
        c.DrawPath(path, tick);
    }

    static bool Numeric(string s) => s.Length > 0 && double.TryParse(s.Trim().TrimStart('L', '$').TrimEnd('%', 'm', ' '), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _);

    static float Table(Painter draw, TableBlock t, float x0, float x1, float y)
    {
        var k = draw.K;
        var hf = Font(k.Semi, 13);
        var cf = Font(k.Regular, 14.5f);
        int cols = t.Rows[0].Length;
        var widths = new float[cols];
        var right = new bool[cols];
        for (int c = 0; c < cols; c++)
        {
            var body = t.Rows.Skip(t.Header ? 1 : 0).Select(r => r[c]).ToList();
            right[c] = body.Count > 0 && body.All(s => s.Length == 0 || Numeric(s)) && body.Any(Numeric);
            widths[c] = t.Rows.Select((r, i) => Panel.Measure(i == 0 && t.Header ? hf : cf, r[c])).Max();
        }
        float gap = 14, room = x1 - x0 - gap * (cols - 1);
        // too wide: the widest columns give way, none below 44
        while (widths.Sum() > room)
        {
            int widest = Array.IndexOf(widths, widths.Max());
            float over = widths.Sum() - room;
            float second = widths.Where((_, i) => i != widest).DefaultIfEmpty(0).Max();
            float cut = Math.Min(over, Math.Max(widths[widest] - Math.Max(second, 44), 1));
            if (widths[widest] - cut < 44) { widths[widest] = Math.Max(44, widths[widest] - over); break; }
            widths[widest] -= cut;
        }
        float yy = y;
        for (int r = 0; r < t.Rows.Count; r++)
        {
            bool head = r == 0 && t.Header;
            var f = head ? hf : cf;
            float x = x0;
            for (int c = 0; c < cols; c++)
            {
                var s = Ellipsize(t.Rows[r][c], f, widths[c]);
                if (right[c]) draw.TextRight(s, x + widths[c], yy + 15, f, head ? k.Dim : k.Text);
                else draw.Text(s, x, yy + 15, f, head ? k.Dim : k.Text);
                x += widths[c] + gap;
            }
            yy += head ? 22 : 22;
            if (head) { draw.Hairline(x0, x1, yy - 2); yy += 3; }
        }
        return yy - y;
    }

    // --- figures -----------------------------------------------------------------------------------------------------

    static string Clock(int s) => s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";

    static float Timer(Painter draw, TimerBlock t, BlockLive live, float x0, float y)
    {
        const float H = 52;
        if (!draw.Drawing) return H;
        var k = draw.K;
        var c = draw.Canvas!;
        float cx = x0 + 22, cy = y + H / 2, r = 18;
        using (var track = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, Color = A(k.Text, k.Light ? 0.12f : 0.16f) })
            c.DrawCircle(cx, cy, r, track);
        // what's left, from the top round clockwise (a stopwatch: the minute going round)
        float frac = t.Up ? live.Seconds % 60 / 60f : live.Done ? 1 : (float)Math.Clamp(live.Seconds / t.Seconds, 0, 1);
        if (frac > 0)
            using (var arc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, StrokeCap = SKStrokeCap.Round, Color = A(k.Text, live.Done ? 0.45f : 0.8f) })
            using (var path = new SKPath())
            {
                path.AddArc(new SKRect(cx - r, cy - r, cx + r, cy + r), -90, Math.Max(0.5f, 360 * frac));
                c.DrawPath(path, arc);
            }
        if (live.Done)
            using (var tick = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = k.Text })
            using (var path = new SKPath())
            {
                path.MoveTo(cx - 6, cy + 0.5f);
                path.LineTo(cx - 1.8f, cy + 4.6f);
                path.LineTo(cx + 6.5f, cy - 4.5f);
                c.DrawPath(path, tick);
            }
        var f = Font(k.Semi, 30);
        draw.Text(Clock(Math.Max(0, live.Seconds)), x0 + 54, cy + 10.5f, f, live.Done ? k.Dim : k.Text);
        return H;
    }

    static string Amount(double v) => Math.Abs(v - Math.Round(v)) < 1e-9 ? ((long)Math.Round(v)).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
        : v.ToString(Math.Abs(v) < 10 ? "0.##" : "0.#", System.Globalization.CultureInfo.InvariantCulture);

    static float Progress(Painter draw, ProgressBlock p, float x0, float x1, float y)
    {
        var k = draw.K;
        var f = Font(k.Regular, 15);
        var vf = Font(k.Regular, 13.5f);
        string value = p.Max == 1 ? $"{Math.Round(p.Value * 100)}%" : $"{Amount(p.Value)} of {Amount(p.Max)}";
        float yy = y;
        if (p.Label is { } label)
        {
            draw.Text(Ellipsize(label, f, x1 - x0 - Panel.Measure(vf, value) - 14), x0, yy + 15, f, k.Text);
            yy += 22;
        }
        if (p.Label == null) { draw.TextRight(value, x1, yy + 13, vf, k.Dim); yy += 19; }
        else draw.TextRight(value, x1, yy - 22 + 15, vf, k.Dim);
        var track = new SKRect(x0, yy + 2, x1, yy + 10);
        draw.Recess(track, 4);
        float frac = (float)(p.Value / p.Max);
        if (draw.Drawing && frac > 0)
            using (var fill = new SKPaint { IsAntialias = true, Color = A(k.Text, 0.72f) })
                draw.Canvas!.DrawRoundRect(new SKRect(x0, track.Top, x0 + Math.Max(8, (x1 - x0) * frac), track.Bottom), 4, 4, fill);
        return yy + 12 - y;
    }

    /// <summary>A value with its unit: money before it (L$12), a symbol right after it (12%), a word after a space (12 m).</summary>
    static string WithUnit(double v, string? unit) => unit switch
    {
        null or "" => Amount(v),
        _ when unit.EndsWith('$') || unit is "€" or "£" or "¥" => unit + Amount(v),
        _ when !char.IsLetter(unit[0]) => Amount(v) + unit,
        _ => Amount(v) + " " + unit,
    };

    static float Bars(Painter draw, ChartBlock ch, float x0, float x1, float y)
    {
        var k = draw.K;
        var lf = Font(k.Regular, 14);
        var vf = Font(k.Regular, 13);
        string Val(double v) => WithUnit(v, ch.Unit);
        bool labels = ch.Points.Any(p => p.Label is { Length: > 0 });
        float labelW = labels ? MathF.Min(ch.Points.Max(p => Panel.Measure(lf, p.Label ?? "")), (x1 - x0) * 0.4f) : 0;
        float valueW = ch.Points.Max(p => Panel.Measure(vf, Val(p.Value)));
        float bx = x0 + (labels ? labelW + 10 : 0), bmax = x1 - valueW - 8;
        double max = Math.Max(ch.Points.Max(p => p.Value), 1e-9);
        float yy = y;
        if (draw.Drawing)
            using (var axis = new SKPaint { Color = A(k.Text, k.Light ? 0.18f : 0.16f), StrokeWidth = 1 })
                draw.Canvas!.DrawLine(bx + 0.5f, y, bx + 0.5f, y + ch.Points.Count * 22 - 4, axis);
        foreach (var (label, v) in ch.Points)
        {
            if (labels) draw.Text(Ellipsize(label ?? "", lf, labelW), x0, yy + 14.5f, lf, k.Text);
            float len = (float)(v / max) * (bmax - bx);
            if (draw.Drawing && len > 0.5f)
            {
                // square at the baseline, a 4 px rounded end
                using var p = new SKPaint { IsAntialias = true, Color = A(k.Dim, 0.9f) };
                var rr = new SKRoundRect();
                var rect = new SKRect(bx + 1, yy + 4, bx + 1 + Math.Max(len, 2), yy + 16);
                rr.SetRectRadii(rect, new[] { new SKPoint(0, 0), new SKPoint(4, 4), new SKPoint(4, 4), new SKPoint(0, 0) });
                draw.Canvas!.DrawRoundRect(rr, p);
            }
            draw.Text(Val(v), bx + 1 + Math.Max(len, 2) + 6, yy + 14.5f, vf, k.Dim);
            yy += 22;
        }
        return yy - 4 - y;
    }

    static float Line(Painter draw, ChartBlock ch, float x0, float x1, float y)
    {
        const float H = 104;
        if (!draw.Drawing) return H;
        var k = draw.K;
        var c = draw.Canvas!;
        var vf = Font(k.Regular, 13);
        var ff = Font(k.Regular, 12);
        string Val(double v) => WithUnit(v, ch.Unit);
        var vs = ch.Points.Select(p => p.Value).ToList();
        double lo = vs.Min(), hi = vs.Max();
        if (lo >= 0 && lo < hi * 0.35) lo = 0;   // amounts from zero when that doesn't flatten them
        if (hi - lo < 1e-9) { hi += 1; lo -= 1; }
        float endW = Panel.Measure(vf, Val(vs[^1])) + 12;
        float px0 = x0, px1 = x1 - endW, top = y + 18, bottom = y + H - 20;
        SKPoint P(int i) => new(px0 + (px1 - px0) * i / (vs.Count - 1), bottom - (float)((vs[i] - lo) / (hi - lo)) * (bottom - top));
        using (var axis = new SKPaint { Color = A(k.Text, k.Light ? 0.18f : 0.16f), StrokeWidth = 1 })
            c.DrawLine(px0, bottom + 0.5f, px1, bottom + 0.5f, axis);
        using (var area = new SKPath())
        {
            area.MoveTo(px0, bottom);
            for (int i = 0; i < vs.Count; i++) area.LineTo(P(i));
            area.LineTo(px1, bottom);
            area.Close();
            using var wash = new SKPaint { IsAntialias = true, Color = A(k.Text, 0.08f) };
            c.DrawPath(area, wash);
        }
        using (var line = new SKPath())
        {
            line.MoveTo(P(0));
            for (int i = 1; i < vs.Count; i++) line.LineTo(P(i));
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, Color = A(k.Text, 0.78f) };
            c.DrawPath(line, stroke);
        }
        // the end, and the highest point if it isn't the end: the only values written on it
        var end = P(vs.Count - 1);
        using (var ring = new SKPaint { IsAntialias = true, Color = k.Tint.WithAlpha(255) }) c.DrawCircle(end, 6, ring);
        using (var dot = new SKPaint { IsAntialias = true, Color = k.Text }) c.DrawCircle(end, 4, dot);
        draw.Text(Val(vs[^1]), end.X + 9, end.Y + 4.5f, vf, k.Text);
        int top_ = vs.IndexOf(vs.Max());
        if (top_ != vs.Count - 1)
        {
            var tp = P(top_);
            var s = Val(vs[top_]);
            float tx = Math.Clamp(tp.X - Panel.Measure(ff, s) / 2, px0, px1 - Panel.Measure(ff, s));
            draw.Text(s, tx, tp.Y - 7, ff, k.Dim);
        }
        if (ch.Points[0].Label is { Length: > 0 } first) draw.Text(Ellipsize(first, ff, (px1 - px0) / 2 - 6), px0, bottom + 15, ff, k.Faint);
        if (ch.Points[^1].Label is { Length: > 0 } last) draw.TextRight(Ellipsize(last, ff, (px1 - px0) / 2 - 6), px1, bottom + 15, ff, k.Faint);
        return H;
    }

    static float Image(Painter draw, ImageBlock b, SKImage? img, float x0, float x1, float y)
    {
        // a saved picture takes the card's width at its own shape (known from its header before it's decoded), up to a
        // height; the view and textures a fixed strip
        float H = b.IsPicture ? MathF.Min(PictureMaxH, (x1 - x0) * b.Height / b.Width) : ImageH;
        if (!draw.Drawing) return H;
        var box = new SKRect(x0, y, x1, y + H);
        var c = draw.Canvas!;
        if (img == null)
        {
            var wait = b.IsPicture ? Fit(box, b.Width, b.Height) : box;
            draw.Recess(wait, 10);
            draw.Dots(wait.MidX - 9, wait.MidY, 0);
            return H;
        }
        var at = Fit(box, img.Width, img.Height);
        c.Save();
        c.ClipRoundRect(new SKRoundRect(at, b.IsPicture ? 6 : 10), antialias: true);
        c.DrawImage(img, at, new SKSamplingOptions(SKCubicResampler.Mitchell));
        c.Restore();
        return H;
    }

    /// <summary>A w x h picture as large as fits in box, centred.</summary>
    static SKRect Fit(SKRect box, float w, float h)
    {
        float s = MathF.Min(box.Width / w, box.Height / h);
        (w, h) = (w * s, h * s);
        return new SKRect(box.MidX - w / 2, box.MidY - h / 2, box.MidX + w / 2, box.MidY + h / 2);
    }

    // --- where things are --------------------------------------------------------------------------------------------

    static string Metres(int m) => m >= 1000 ? $"{m / 1000.0:0.#} km" : $"{m} m";

    static float Toward(Painter draw, TowardBlock t, BlockLive live, float x0, float x1, float y)
    {
        const float H = 56, R = 23;
        var k = draw.K;
        float cx = x0 + R + 1, cy = y + H / 2;
        draw.Recess(new SKRect(cx - R, cy - R, cx + R, cy + R), R);
        if (draw.Drawing)
        {
            var c = draw.Canvas!;
            if (!live.Known)
            {
                using var p = new SKPaint { IsAntialias = true, Color = A(k.Dim, 0.6f) };
                c.DrawCircle(cx, cy, 3, p);
            }
            else if (live.Distance < 2)
            {
                using var p = new SKPaint { IsAntialias = true, Color = k.Text };
                c.DrawCircle(cx, cy, 6, p);
            }
            else
            {
                // an arrow (a shaft and a head, so which end leads is never in doubt) turned by where it lies
                // against the way they face: straight ahead is up
                c.Save();
                c.Translate(cx, cy);
                c.RotateDegrees(live.Bearing);
                using (var shaft = new SKPaint { IsAntialias = true, Color = k.Text, Style = SKPaintStyle.Stroke, StrokeWidth = 3.2f, StrokeCap = SKStrokeCap.Round })
                    c.DrawLine(0, 12, 0, -3, shaft);
                using var path = new SKPath();
                path.MoveTo(0, -15);
                path.LineTo(8, -3);
                path.LineTo(-8, -3);
                path.Close();
                using var p = new SKPaint { IsAntialias = true, Color = k.Text };
                c.DrawPath(path, p);
                c.Restore();
            }
        }
        float tx = cx + R + 14;
        var df = Font(k.Semi, 24);
        var lf = Font(k.Regular, 14.5f);
        string dist = live.Known ? Metres(live.Distance) : "–";
        if (t.Label is { } label)
        {
            draw.Text(dist, tx, cy - 1, df, k.Text);
            draw.Text(Ellipsize(label, lf, x1 - tx), tx, cy + 18, lf, k.Dim);
        }
        else draw.Text(dist, tx, cy + 8.5f, df, k.Text);
        return H;
    }

    static float Map(Painter draw, MapBlock m, BlockLive live, float x0, float x1, float y)
    {
        float d = MathF.Min(x1 - x0, MapMax), R = d / 2;
        if (!draw.Drawing) return d + 4;
        var k = draw.K;
        var c = draw.Canvas!;
        float cx = (x0 + x1) / 2, cy = y + R;
        draw.Recess(new SKRect(cx - R, cy - R, cx + R, cy + R), R);
        using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = A(k.Text, k.Light ? 0.13f : 0.12f) })
            c.DrawCircle(cx, cy, R * 0.5f, ring);
        var ff = Font(k.Semi, 11.5f);
        // north at the rim (the map turns with them: ahead is up)
        float na = live.North * MathF.PI / 180;
        draw.Text("N", cx + MathF.Sin(na) * (R - 11) - Panel.Measure(ff, "N") / 2, cy - MathF.Cos(na) * (R - 11) + 4, ff, k.Faint);
        if (live.MapRadius > 0)
        {
            var sf = Font(k.Regular, 11.5f);
            var s = Metres(live.MapRadius);
            draw.Text(s, cx + R * 0.5f * 0.72f + 2, cy + R * 0.5f * 0.72f + 12, sf, k.Faint);
        }
        // you: a chevron at the middle, pointing ahead
        using (var you = new SKPath())
        {
            you.MoveTo(cx, cy - 8);
            you.LineTo(cx + 6, cy + 6);
            you.LineTo(cx, cy + 3);
            you.LineTo(cx - 6, cy + 6);
            you.Close();
            using var p = new SKPaint { IsAntialias = true, Color = k.Text };
            c.DrawPath(you, p);
        }
        var lf = Font(k.Regular, 12.5f);
        var marks = live.Marks ?? Array.Empty<(int, int, bool)>();
        for (int i = 0; i < m.Points.Count && i < marks.Length; i++)
        {
            var (mx, my, known) = marks[i];
            if (!known) continue;
            // marks come in 1/1000ths of the radius; past it they sit on the rim, hollow
            float px = mx / 1000f * R, py = -my / 1000f * R;
            float len = MathF.Sqrt(px * px + py * py);
            bool outside = len > R - 7;
            if (outside) { px *= (R - 7) / len; py *= (R - 7) / len; }
            var at = new SKPoint(cx + px, cy + py);
            using (var ringP = new SKPaint { IsAntialias = true, Color = k.Tint.WithAlpha(255) }) c.DrawCircle(at, 6.5f, ringP);
            using (var dot = new SKPaint { IsAntialias = true, Color = k.Text, Style = outside ? SKPaintStyle.Stroke : SKPaintStyle.Fill, StrokeWidth = 1.6f })
                c.DrawCircle(at, outside ? 3.8f : 4.5f, dot);
            if (m.Points[i].Label is { } label)
            {
                var s = Ellipsize(label, lf, R * 0.9f);
                float w = Panel.Measure(lf, s);
                float lx = at.X + 9 + w > cx + R ? at.X - 9 - w : at.X + 9;
                draw.Text(s, lx, at.Y + 4.3f, lf, k.Dim);
            }
        }
        return d + 4;
    }

    /// <summary>The assistant's mark: a small four-pointed spark.</summary>
    internal static void Spark(Painter draw, float cx, float cy, float r, SKColor colour) => Painter.Spark(draw, cx, cy, r, colour);
}
