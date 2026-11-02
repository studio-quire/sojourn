using SkiaSharp;
using Sojourn.Session.Objects;
using static Sojourn.Session.Assistant.Panel;

namespace Sojourn.Client.Things;

public enum CardKeyKind { Close, Back, Action, Amount, CopyAll }

public readonly record struct CardKey(CardKeyKind Kind, int Index, SKRect Rect);

public enum CardPage { Main, Pay, Open }

/// <summary>Everything a thing's card shows, as one snapshot (drawn off the main thread). Amounts and Contents are null
/// while they're being asked for. Asking: the card's ask key has the mic open (Latched: open hands-free, its next press
/// sends). Working: an action is under way.</summary>
public sealed record CardView(string Title, string? Owner, CardPage Page, IReadOnlyList<ThingAction> Actions, bool About,
    IReadOnlyList<(string Label, string Value)> Facts, IReadOnlyList<int>? Amounts, IReadOnlyList<ThingActions.Content>? Contents,
    string? Note, bool Working, bool Asking, bool Latched, int Hover, int Phase);

/// <summary>
/// Draws a thing's card in the panel's look (Assistant/PanelPainter.cs), so it reads as one of the client's own sheets:
/// the body's glass, the name at the top with × (and on the pay and open pages a back key), the owner dim under it,
/// then the actions as full-width glass keys in a column, a price dim at a key's right. "about this" opens its facts in
/// place under it. The pay page is its amounts as keys; the open page the contents and a key to copy them. Nothing in
/// the accent: nothing here needs the person the way a confirmation does.
/// </summary>
public static class ThingCardPainter
{
    public const float W = 300;
    const float Pad = 18, RowH = 44, Gap = 6;

    public static (byte[] Px, int Width, int Height, float Scale, List<CardKey> Keys, float LogicalH) Draw(CardView v,
        Sojourn.Session.Assistant.PanelLook look, float scale, bool layer)
    {
        var k = Ink.For(look);
        var keys = new List<CardKey>();
        var (px, w, h, used) = Rasterise(scale, W, new List<Sojourn.Session.Assistant.PanelKey>(), (c, height) =>
        {
            keys.Clear();
            return Layout(new Painter(c, k, W), v, keys, height);
        }, MaxRasterWidth);
        Sojourn.Session.Assistant.GlassLight.Finish(px, look, layer);
        return (px, w, h, used, keys, h / used);
    }

    static float Layout(Painter draw, CardView v, List<CardKey> keys, float height)
    {
        var k = draw.K;
        draw.Body(height);
        bool lit(int i) => v.Hover == i;

        // × at the top right; on a page under the main one, back beside it
        var close = new SKRect(W - 10 - 36, 10, W - 10, 46);
        draw.GlyphKey(close, Painter.Glyph.Close, lit(keys.Count));
        keys.Add(new CardKey(CardKeyKind.Close, 0, close));
        float right = close.Left - 4;
        if (v.Page != CardPage.Main)
        {
            var back = new SKRect(right - 36, 10, right, 46);
            draw.GlyphKey(back, Painter.Glyph.Back, lit(keys.Count));
            keys.Add(new CardKey(CardKeyKind.Back, 0, back));
            right = back.Left - 4;
        }

        float y = 14;
        var titleF = Font(k.Semi, 18);
        var lines = Wrap(v.Title, titleF, right - Pad, 2);
        if (lines.Count == 2) lines[1] = Ellipsize(lines[1], titleF, right - Pad);
        foreach (var l in lines) { draw.Text(l, Pad, y + 18, titleF, k.Text); y += 24; }
        var dimF = Font(k.Regular, 14);
        string? sub = v.Page switch { CardPage.Pay => Lang.T("pay"), CardPage.Open => Lang.T("inside it"), _ => v.Owner };
        if (sub is { Length: > 0 })
        {
            draw.Text(Ellipsize(sub, dimF, (y < 48 ? right : W - Pad) - Pad), Pad, y + 15, dimF, k.Dim);
            y += 20;
        }
        y = MathF.Max(y, 46) + 10;

        if (v.Note is { Length: > 0 } note)
        {
            var f = Font(k.Regular, 15);
            foreach (var l in Wrap(note, f, W - 2 * Pad, 4)) { draw.Text(l, Pad, y + 15, f, k.Text); y += 21; }
            y += 8;
        }
        if (v.Working)
        {
            draw.Dots(Pad + 4, y + 8, v.Phase);
            y += 24;
        }

        switch (v.Page)
        {
            case CardPage.Main:
                for (int i = 0; i < v.Actions.Count; i++)
                {
                    var a = v.Actions[i];
                    var row = new SKRect(Pad - 8, y, W - Pad + 8, y + RowH);
                    bool on = a.Do == ThingDo.Ask && v.Asking || a.Do == ThingDo.About && v.About;
                    draw.KeyFace(row, Math.Min(16, k.Look.Radius * 0.75f), lit(keys.Count), on ? 1.6f : 1);
                    var lf = Font(k.Regular, 16.5f);
                    string label = a.Do == ThingDo.Ask && v.Latched ? Lang.T("send") : a.Label;
                    float detailW = a.Detail is { } d ? Measure(dimF, d) + 10 : 0;
                    draw.Text(Ellipsize(label, lf, row.Width - 32 - detailW), row.Left + 16, row.MidY + 6, lf, k.Text);
                    if (a.Detail is { } dt) draw.TextRight(dt, row.Right - 16, row.MidY + 5, dimF, k.Dim);
                    if (a.Do == ThingDo.Ask && v.Asking && !v.Latched) draw.Dots(row.Right - 40, row.MidY, v.Phase);
                    keys.Add(new CardKey(CardKeyKind.Action, i, row));
                    y += RowH + Gap;
                    if (a.Do == ThingDo.About && v.About) y = Facts(draw, v.Facts, y);
                }
                break;
            case CardPage.Pay:
                if (v.Amounts == null) { draw.Dots(Pad + 4, y + 8, v.Phase); y += 24; break; }
                {
                    float cell = (W - 2 * (Pad - 8) - 2 * Gap) / 3;
                    for (int i = 0; i < v.Amounts.Count; i++)
                    {
                        float x = Pad - 8 + (i % 3) * (cell + Gap);
                        if (i > 0 && i % 3 == 0) y += RowH + Gap;
                        var r = new SKRect(x, y, x + cell, y + RowH);
                        draw.Key(r, Lang.F("L${0}", v.Amounts[i]), lit(keys.Count), 16.5f);
                        keys.Add(new CardKey(CardKeyKind.Amount, i, r));
                    }
                    if (v.Amounts.Count > 0) y += RowH + Gap;
                }
                break;
            case CardPage.Open:
                if (v.Contents == null) { draw.Dots(Pad + 4, y + 8, v.Phase); y += 24; break; }
                {
                    var f = Font(k.Regular, 15);
                    const int Shown = 8;
                    foreach (var it in v.Contents.Take(Shown))
                    {
                        draw.Text(Ellipsize(it.Name, f, W - 2 * Pad - 70), Pad, y + 15, f, it.Copy ? k.Text : k.Dim);
                        draw.TextRight(Lang.T(it.Type, "type"), W - Pad, y + 15, Font(k.Regular, 13), k.Faint);
                        y += 21;
                    }
                    if (v.Contents.Count > Shown) { draw.Text(Lang.F("and {0} more", v.Contents.Count - Shown), Pad, y + 15, f, k.Dim); y += 21; }
                    if (v.Contents.Count == 0) { draw.Text(Lang.T("nothing"), Pad, y + 15, f, k.Dim); y += 21; }
                    y += 10;
                    int copyable = v.Contents.Count(c => c.Copy);
                    if (copyable > 0)
                    {
                        var row = new SKRect(Pad - 8, y, W - Pad + 8, y + RowH);
                        draw.KeyFace(row, Math.Min(16, k.Look.Radius * 0.75f), lit(keys.Count));
                        draw.Text(Lang.T("copy to inventory"), row.Left + 16, row.MidY + 6, Font(k.Regular, 16.5f), k.Text);
                        if (copyable < v.Contents.Count) draw.TextRight(Lang.F("{0} of {1}", copyable, v.Contents.Count), row.Right - 16, row.MidY + 5, dimF, k.Dim);
                        keys.Add(new CardKey(CardKeyKind.CopyAll, 0, row));
                        y += RowH + Gap;
                    }
                }
                break;
        }
        return y + 8;
    }

    /// <summary>About it: each fact a dim label over its value, under the about key.</summary>
    static float Facts(Painter draw, IReadOnlyList<(string Label, string Value)> facts, float y)
    {
        var k = draw.K;
        var lf = Font(k.Regular, 12.5f);
        var vf = Font(k.Regular, 15);
        y += 2;
        if (facts.Count == 0) { draw.Dots(Pad + 4, y + 8, 0); return y + 24; }
        foreach (var (label, value) in facts)
        {
            draw.Text(Lang.T(label), Pad, y + 12, lf, k.Faint);
            y += 16;
            foreach (var l in Wrap(value, vf, W - 2 * Pad, label == "description" ? 4 : 2)) { draw.Text(l, Pad, y + 15, vf, k.Text); y += 20; }
            y += 6;
        }
        return y + 6;
    }
}
