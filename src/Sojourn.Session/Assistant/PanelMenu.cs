using SkiaSharp;

namespace Sojourn.Session.Assistant;

/// <summary>
/// A settings row. Its control, by what's set:
/// - <see cref="On"/>: a switch; the whole row is one key (Act(0) toggles).
/// - <see cref="Options"/>: a segmented control, each option a key (Act(i) picks option i; <see cref="Selected"/>).
/// - <see cref="Looks"/>: a swatch per look (Act(i) picks look i; <see cref="Selected"/>).
/// - <see cref="Stepper"/>: - and + keys round the value (Act(-1), Act(+1)).
/// - <see cref="Rows"/>: a group; the whole row opens a page of its own rows (a chevron, the value faint before it).
/// - otherwise the value as a key, and the whole row steps it (Act(0)).
/// A note shows under the label only where the person needs it (what a value they set says).
/// </summary>
public sealed record MenuRow(string Label, string Value, bool Stepper, bool Enabled = true, string? Note = null)
{
    public bool? On { get; init; }
    public string[]? Options { get; init; }
    public IReadOnlyList<PanelLook>? Looks { get; init; }
    public int Selected { get; init; } = -1;
    /// <summary>The options' type sizes, where they differ (the size row's A's).</summary>
    public float[]? OptionSizes { get; init; }
    /// <summary>A group's rows, as they stand when it's drawn (the menu opens them in the section's place).</summary>
    public Func<List<(MenuRow Row, Action<int> Act)>>? Rows { get; init; }

    /// <summary>A row opening a page of rows: for a section grown too long to take in at once.</summary>
    public static (MenuRow Row, Action<int> Act) Group(string label, Func<List<(MenuRow Row, Action<int> Act)>> rows, string value = "") =>
        (new(label, value, false) { Rows = rows }, _ => { });

    public static MenuRow Switch(string label, bool on, bool enabled = true, string? note = null) =>
        new(label, on ? "on" : "off", false, enabled, note) { On = on };
    public static MenuRow Choice(string label, string[] options, int selected, bool enabled = true) =>
        new(label, selected >= 0 && selected < options.Length ? options[selected] : "", false, enabled) { Options = options, Selected = selected };
}

public sealed partial class Panel
{
    public const float MenuWidth = 440;

    /// <summary>
    /// The settings menu, in the panel's look (the same material, keys and type, so it reads the same in the headset).
    /// Keys: <see cref="KeyKind.Menu"/> with Index = row * 16 + part (0 the row, 1 minus, 2 plus, 3 + i option or
    /// swatch i); close is <see cref="KeyKind.Hide"/>, help <see cref="KeyKind.Help"/>; the sections along the top are
    /// <see cref="KeyKind.Tab"/> with Index = section. Help (when open) shows the given lines under the header.
    /// A section taller than maxHeight (logical px) goes a page of rows at a time, with ‹ › keys either side of a dot a
    /// page at the foot (<see cref="KeyKind.Page"/>, Index = the page it turns to); every page as tall as the tallest, so
    /// the keys stay put. page: the one to show (clamped). group: a group's rows are shown (its name): the header is a
    /// back key with the name instead of the sections, a <see cref="KeyKind.Tab"/> for the section it's in.
    /// tip: what a row (its index) or tab (-1 - its index) does, drawn on a small pane just under it (over the rows
    /// below; above it where there's no room below). text: a section's own words above its rows (the about section's),
    /// drawn as help is when help isn't open. The raster's Spots say where each row and tab was drawn.
    /// </summary>
    public static Raster DrawMenu(PanelLook look, IReadOnlyList<string> tabs, int section, IReadOnlyList<MenuRow> rows, IReadOnlyList<string>? help,
        int hover, float scale, long version, int page = 0, float maxHeight = float.MaxValue, float wide = 1, bool? layer = null, string? group = null,
        (int Target, string Text)? tip = null, IReadOnlyList<string>? text = null)
    {
        // the person's language, as drawn: the rows keep their English (the tips and the tests find rows by it)
        string? section0 = section >= 0 && section < tabs.Count ? tabs[section] : null;
        tabs = tabs.Select(t => Lang.T(t)).ToList();
        rows = rows.Select(r => Translated(r, section0)).ToList();
        group = Lang.Tn(group);
        help = help?.Select(Lang.T).ToList();
        text = text?.Select(Lang.T).ToList();
        if (tip is { } tp) tip = (tp.Target, Lang.T(tp.Text));
        var ink = Ink.For(look);
        var keys = new List<PanelKey>();
        var spots = new List<(int, SKRect)>();
        float width = MenuWidth * wide;   // wider at a smaller size (UiSize.Wide): room for the rows' controls and labels
        var (pixels, w, h, used) = Rasterise(scale, width, keys, (c, height) =>
        {
            spots.Clear();
            var draw = new Painter(c, ink, width);
            float y = LayoutMenu(draw, tabs, section, rows, help, hover, keys, spots, height, page, maxHeight, group, sectionText: text);
            if (tip is { } t && spots.FirstOrDefault(sp => sp.Item1 == t.Target) is { Item2.Width: > 0 } at) DrawTip(draw, at.Item2, t.Target < 0, t.Text, height);
            return y;
        }, MaxRasterWidth);
        GlassLight.Finish(pixels, look, layer ?? GlassLight.Layers);
        return new Raster(pixels, w, h, used, keys, version, null, Array.Empty<PanelChoice>(), false, look.Frost, look.TintOpacity) { Spots = spots };
    }

    /// <summary>A row's words in the language in use (a word may need its section: "yours#sound").</summary>
    static MenuRow Translated(MenuRow r, string? section) => r with
    {
        Label = Lang.T(r.Label, section),
        Value = Lang.T(r.Value, section),
        Note = Lang.Tn(r.Note),
        Options = r.Options?.Select(o => Lang.T(o, section)).ToArray(),
    };

    /// <summary>The tip's pane, fitted to its words (up to three lines), from the row's label (a tab's left edge),
    /// inside the menu.</summary>
    static void DrawTip(Painter draw, SKRect at, bool tab, string text, float height)
    {
        if (height <= 0) return;   // (the measuring pass)
        var k = draw.K;
        const float pad = 16, padX = 12, lineH = 19.5f;
        var f = Font(k.Regular, 14.5f);
        float most = Math.Min(draw.W - 2 * pad, 380);
        var lines = Wrap(text, f, most - 2 * padX, 3);
        float w = lines.Max(l => Measure(f, l)) + 2 * padX, h = lines.Count * lineH + 16;
        float left = Math.Clamp(at.Left + (tab ? 0 : 4), pad - 6, draw.W - pad + 6 - w);
        float top = at.Bottom + 4;
        if (!tab && top + h > height - 10) top = at.Top - 4 - h;
        var pane = new SKRect(left, top, left + w, top + h);
        draw.Pane(pane, 12, height);
        float y = top + 21;
        foreach (var l in lines) { draw.Text(l, left + padX, y, f, k.Text); y += lineH; }
    }

    static float MenuRowHeight(MenuRow r) => r.Note is { Length: > 0 } ? 62 : 50;
    const float MenuFoot = 52;   // the page keys' strip

    /// <summary>The rows in pages that fit room (logical px under the header): one page if they all fit, else filled in
    /// order, leaving room for the page keys.</summary>
    static List<(int Start, int Count)> MenuPages(IReadOnlyList<MenuRow> rows, float room)
    {
        var pages = new List<(int, int)>();
        if (rows.Sum(MenuRowHeight) <= room) { pages.Add((0, rows.Count)); return pages; }
        room -= MenuFoot;
        for (int start = 0; start < rows.Count;)
        {
            int end = start;
            float used = 0;
            while (end < rows.Count && (end == start || used + MenuRowHeight(rows[end]) <= room)) used += MenuRowHeight(rows[end++]);
            pages.Add((start, end - start));
            start = end;
        }
        return pages;
    }

    static float LayoutMenu(Painter draw, IReadOnlyList<string> tabs, int section, IReadOnlyList<MenuRow> allRows, IReadOnlyList<string>? help,
        int hover, List<PanelKey> keys, List<(int, SKRect)> spots, float height, int page, float maxHeight, string? group,
        IReadOnlyList<string>? sectionText = null)
    {
        var k = draw.K;
        float W = draw.W;
        draw.Body(height);
        const float pad = 16;
        float y = 14;

        // header: the sections (or the one section's name, or the group's with a way back), then help and close
        var close = new SKRect(W - 10 - 38, y, W - 10, y + 38);
        var helpKey = new SKRect(close.Left - 4 - 38, y, close.Left - 4, y + 38);
        float headerBottom = y + 38;
        if (group != null)
        {
            bool lit = hover == keys.Count;
            var back = new SKRect(pad - 6, y, helpKey.Left - 10, y + 38);
            var tf = Font(k.Semi, 17);
            if (lit) draw.Fill(back, 19, k.Look.KeyOpacity * 0.9f);
            draw.GlyphKey(new SKRect(back.Left, y, back.Left + 38, y + 38), Painter.Glyph.Back, false);
            draw.Text(Ellipsize(group, tf, back.Width - 50), back.Left + 42, y + 25, tf, k.Text);
            keys.Add(new PanelKey(KeyKind.Tab, section, back));
        }
        else if (tabs.Count > 1)
        {
            var tf = Font(k.Regular, 15);
            float room = helpKey.Left - pad - 10;
            // more sections than one strip holds with a little air round each name: two strips, split where the longer
            // is shortest, both as wide as the room so they read as one block
            const float minAir = 16;
            float Natural(int from, int to) => Enumerable.Range(from, to - from).Sum(i => Measure(tf, tabs[i]) + minAir) + 8;
            int split = Natural(0, tabs.Count) <= room ? tabs.Count
                : Enumerable.Range(1, tabs.Count - 1).MinBy(s => Math.Max(Natural(0, s), Natural(s, tabs.Count)));
            float rowY = y;
            foreach (var (from, to) in split < tabs.Count ? new[] { (0, split), (split, tabs.Count) } : new[] { (0, tabs.Count) })
            {
                int n = to - from;
                float text = Enumerable.Range(from, n).Sum(i => Measure(tf, tabs[i]));
                // many sections: less air round each name before anything is squeezed
                float air = Math.Max(12, (room - 8 - text) / n);
                if (split == tabs.Count) air = Math.Min(air, 32);
                var widths = Enumerable.Range(from, n).Select(i => Measure(tf, tabs[i]) + air).ToArray();
                float total = Math.Min(widths.Sum() + 8, room);
                float scaleW = (total - 8) / widths.Sum();
                var well = new SKRect(pad, rowY, pad + total, rowY + 38);
                draw.Recess(well, 19);
                float x = well.Left + 4;
                for (int i = from; i < to; i++)
                {
                    var r = new SKRect(x, well.Top + 4, x + widths[i - from] * scaleW, well.Bottom - 4);
                    draw.Segment(r, tabs[i], selected: i == section, lit: hover == keys.Count, enabled: true, size: 15);
                    keys.Add(new PanelKey(KeyKind.Tab, i, r));
                    spots.Add((-1 - i, r));
                    x = r.Right;
                }
                headerBottom = rowY + 38;
                rowY += 38 + 6;
            }
        }
        else if (tabs.Count == 1)
            draw.Text(tabs[0], pad + 4, y + 25, Font(k.Semi, 17), k.Text);
        draw.GlyphKey(helpKey, Painter.Glyph.Help, hover == keys.Count, on: help != null);
        keys.Add(new PanelKey(KeyKind.Help, 0, helpKey));
        draw.GlyphKey(close, Painter.Glyph.Close, hover == keys.Count);
        keys.Add(new PanelKey(KeyKind.Hide, 0, close));
        y = headerBottom + 14;

        if ((help ?? sectionText) is { Count: > 0 } box0)
        {
            var hf = Font(k.Regular, 14);
            // a section's own words: its first line (a title) in the text's ink, the rest as help is, a little air
            // between its paragraphs
            bool own = help == null;
            var lines = box0.SelectMany((l, n) => Wrap(l, hf, W - 2 * pad - 24, own ? 9 : 3)
                .Select((w, i) => (w, Head: own && n == 0, Gap: own && n > 0 && i == 0 ? 6f : 0f))).ToList();
            var box = new SKRect(pad, y, W - pad, y + 14 + lines.Count * 20 + lines.Sum(l => l.Gap));
            draw.Recess(box, 14);
            float hy = y + 7;
            foreach (var (l, head, gap) in lines)
            {
                hy += gap;
                draw.Text(l, pad + 12, hy + 14, head ? Font(k.Semi, 14) : hf, head ? k.Text : k.Dim);
                hy += 20;
            }
            y = box.Bottom + 12;
        }

        var pages = MenuPages(allRows, maxHeight - y - 12);
        int pg = Math.Clamp(page, 0, pages.Count - 1);
        var (first, count) = pages[pg];
        float rowsTop = y;
        var labelF = Font(k.Regular, 16.5f);
        var noteF = Font(k.Regular, 13);
        var valueF = Font(k.Regular, 15);
        var rows = allRows;
        for (int i = first; i < first + count; i++)
        {
            var r = rows[i];
            bool note = r.Note is { Length: > 0 };
            float rowH = MenuRowHeight(r);
            var row = new SKRect(pad - 6, y, W - pad + 6, y + rowH);
            if (i > first) draw.Hairline(row.Left + 10, row.Right - 10, row.Top - 0.5f);
            spots.Add((i, row));
            var labelInk = r.Enabled ? k.Text : k.Faint;
            float right = row.Right - 8;
            float labelY = row.Top + (note ? 26 : rowH / 2 + 6);
            float controlLeft;
            float cy = row.Top + (note ? 26 : rowH / 2) - 1;

            if (r.On is { } on)
            {
                // a switch: the whole row is the key
                bool lit = r.Enabled && hover == keys.Count;
                if (lit) draw.Fill(new SKRect(row.Left, row.Top + 2, row.Right, row.Bottom - 2), 14, k.Look.KeyOpacity * 0.9f);
                var sw = new SKRect(right - 46, cy - 13, right, cy + 13);
                draw.Switch(sw, on, lit, r.Enabled);
                if (r.Enabled) keys.Add(new PanelKey(KeyKind.Menu, i * 16, row));
                controlLeft = sw.Left;
            }
            else if (r.Options is { Length: > 0 } opts)
            {
                float SizeOf(int o) => r.OptionSizes is { } sz && o < sz.Length ? sz[o] : 14.5f;
                var texts = opts.Select((o, n) => r.OptionSizes != null ? Measure(Font(k.Regular, SizeOf(n)), o) : Measure(valueF, o)).ToArray();
                // the options' air gives way (down to 12 px each) before the row's name would be cut short
                float room = right - (row.Left + 32 + Measure(labelF, r.Label)) - 6;
                float air = Math.Clamp((room - texts.Sum()) / opts.Length, 12, r.OptionSizes != null ? 24 : 26);
                var widths = texts.Select(t => t + air).ToArray();
                float total = widths.Sum() + 6;
                var well = new SKRect(right - total, cy - 17, right, cy + 17);
                draw.Recess(well, 17);
                float x = well.Left + 3;
                for (int o = 0; o < opts.Length; o++)
                {
                    var seg = new SKRect(x, well.Top + 3, x + widths[o], well.Bottom - 3);
                    bool lit = r.Enabled && hover == keys.Count;
                    draw.Segment(seg, opts[o], o == r.Selected, lit, r.Enabled, SizeOf(o));
                    if (r.Enabled) keys.Add(new PanelKey(KeyKind.Menu, i * 16 + 3 + o, seg));
                    x = seg.Right;
                }
                controlLeft = well.Left;
            }
            else if (r.Looks is { Count: > 0 } looks)
            {
                const float rad = 13, step = 38;
                float x = right - rad - 4 - (looks.Count - 1) * step;
                controlLeft = x - rad - 6;
                for (int o = 0; o < looks.Count; o++)
                {
                    bool lit = r.Enabled && hover == keys.Count;
                    draw.Swatch(x, cy, rad, looks[o], o == r.Selected, lit);
                    if (r.Enabled) keys.Add(new PanelKey(KeyKind.Menu, i * 16 + 3 + o, new SKRect(x - step / 2, row.Top + 2, x + step / 2, row.Bottom - 2)));
                    x += step;
                }
            }
            else if (r.Rows != null)
            {
                // a group: the whole row opens it; a chevron, and its value (if any) faint before it
                bool lit = r.Enabled && hover == keys.Count;
                if (lit) draw.Fill(new SKRect(row.Left, row.Top + 2, row.Right, row.Bottom - 2), 14, k.Look.KeyOpacity * 0.9f);
                var chevron = new SKRect(right - 30, cy - 15, right, cy + 15);
                draw.GlyphKey(chevron, Painter.Glyph.Forward, false);
                controlLeft = chevron.Left;
                if (r.Value.Length > 0)
                {
                    float vw = Math.Min(Measure(valueF, r.Value), 160);
                    draw.Text(Ellipsize(r.Value, valueF, vw + 1), chevron.Left - 6 - vw, cy + 5.4f, valueF, r.Enabled ? k.Dim : k.Faint);
                    controlLeft = chevron.Left - 6 - vw;
                }
                if (r.Enabled) keys.Add(new PanelKey(KeyKind.Menu, i * 16, row));
            }
            else if (r.Stepper)
            {
                // [-] value [+]
                var plus = new SKRect(right - 38, cy - 17, right, cy + 17);
                float vw = Math.Max(78, Measure(valueF, r.Value) + 16);
                var minus = new SKRect(plus.Left - vw - 38, plus.Top, plus.Left - vw, plus.Bottom);
                draw.Key(minus, "−", r.Enabled && hover == keys.Count, 18);
                if (r.Enabled) keys.Add(new PanelKey(KeyKind.Menu, i * 16 + 1, minus));
                draw.Key(plus, "+", r.Enabled && hover == keys.Count, 18);
                if (r.Enabled) keys.Add(new PanelKey(KeyKind.Menu, i * 16 + 2, plus));
                draw.Text(r.Value, (minus.Right + plus.Left) / 2 - Measure(valueF, r.Value) / 2, cy + 5.4f, valueF, r.Enabled ? k.Text : k.Faint);
                controlLeft = minus.Left;
            }
            else
            {
                // the value as a key; pressing anywhere on the row steps it
                bool lit = r.Enabled && hover == keys.Count;
                if (lit) draw.Fill(new SKRect(row.Left, row.Top + 2, row.Right, row.Bottom - 2), 14, k.Look.KeyOpacity * 0.9f);
                float vw = Math.Min(Measure(valueF, r.Value) + 30, 260);   // room for an OpenRouter tag
                var cap = new SKRect(right - vw, cy - 16, right, cy + 16);
                draw.Key(cap, Ellipsize(r.Value, valueF, vw - 20), lit, 15);
                if (r.Enabled) keys.Add(new PanelKey(KeyKind.Menu, i * 16, row));
                controlLeft = cap.Left;
            }

            float labelRoom = controlLeft - row.Left - 22;
            draw.Text(Ellipsize(r.Label, labelF, labelRoom), row.Left + 10, labelY, labelF, labelInk);
            if (note) draw.Text(Ellipsize(r.Note!, noteF, row.Width - 20), row.Left + 10, row.Top + 48, noteF, k.Text);   // the ink, as the row's name: faint was unreadable over bright frost (the user, 2026-10-30)
            y += rowH;
        }
        if (pages.Count > 1)
        {
            // every page as tall as the tallest, so the keys stay under the pointer
            y = rowsTop + pages.Max(p => Enumerable.Range(p.Start, p.Count).Sum(i => MenuRowHeight(allRows[i]))) + 8;
            var back = new SKRect(pad - 6, y, pad + 34, y + 40);
            var next = new SKRect(W - pad - 34, y, W - pad + 6, y + 40);
            if (pg > 0) { draw.GlyphKey(back, Painter.Glyph.Back, hover == keys.Count); keys.Add(new PanelKey(KeyKind.Page, pg - 1, back)); }
            if (pg < pages.Count - 1) { draw.GlyphKey(next, Painter.Glyph.Forward, hover == keys.Count); keys.Add(new PanelKey(KeyKind.Page, pg + 1, next)); }
            draw.PageDots(W / 2, y + 20, pages.Count, pg);
            y += 44;
        }
        return y + 12;
    }
}
