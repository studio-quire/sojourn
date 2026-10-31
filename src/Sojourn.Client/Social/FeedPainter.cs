using LibreMetaverse;
using SkiaSharp;
using Sojourn.Client.Assistant;
using static Sojourn.Session.Assistant.Panel;

namespace Sojourn.Client.Social;

/// <summary>
/// Draws the feed: one glass sheet in the panel's look, newest at the bottom (as chat logs run). Nearby lines are the
/// speaker's name in semibold and their words after it; a conversation (an IM, a group) is a pane of its own headed by
/// who or which group it is, its lines inside, the person's own on the right; notices (offers, script menus, friends
/// coming online) are a pane with a small bell. No words of ours beyond the notices' few.
/// Pressed (the pointer, or a click; user, 2026-10-26), a block opens in full: a long line, notice or script menu is cut
/// to six lines otherwise, ending in "...". The block under the pointer is lit, and while the pointer is on the sheet a
/// close key at its top right puts away everything on it.
/// </summary>
public static class FeedPainter
{
    public const float BaseWidth = 300;
    // the sheet being drawn (Draw, on one thread): its width, and how much more it holds than at size 1 (UiSize.Fit:
    // smaller type, so wider in logical px and taller, more lines in about the same space)
    [ThreadStatic] static float _width, _fit;
    static float Width => _width > 0 ? _width : BaseWidth;
    static float Fit => _fit > 0 ? _fit : 1;
    const float Pad = 14, MaxHeight = 380, WaitingMaxHeight = 520, OpenMaxHeight = 640, Gap = 8, CloseRow = 34;
    const float KeyH = 32, KeyGap = 6;
    const float TextSize = 15.5f, Line = 21, HeaderSize = 13.5f;
    const int CutLines = 6, OpenLines = 40;

    /// <summary>What can be pressed, in the sheet's logical pixels: each block (by its key: its first entry's Seq), the
    /// close key, and the notices' keys (their buttons, read, put away; the clear key). MaxScroll: how far back it can be
    /// scrolled (logical px; 0 when everything fits).</summary>
    public sealed record Drawn(byte[] Rgba, int Width, int Height, float Scale, float Frost, float Edge,
        IReadOnlyList<(SKRect Rect, long Key)> Blocks, SKRect? Close, IReadOnlyList<(SKRect Rect, FeedKey Key)>? Keys = null,
        float MaxScroll = 0)
    {
        public float LogicalWidth => Width / Scale;
        public float LogicalHeight => Height / Scale;

        /// <summary>The block at a point (its key), -1 for the close key, or 0 for nothing.</summary>
        public long At(float x, float y)
        {
            if (Close is { } c && c.Contains(x, y)) return -1;
            foreach (var (r, k) in Blocks) if (r.Contains(x, y)) return k;
            return 0;
        }

        /// <summary>A notice's key at a point, if any.</summary>
        public FeedKey? KeyAt(float x, float y)
        {
            foreach (var (r, k) in Keys ?? []) if (r.Contains(x, y)) return k;
            return null;
        }
    }

    public enum FeedKeyKind { Answer, Read, Dismiss, ClearAll, Link }

    /// <summary>A key on a notice: one of its buttons (by the notification's button name), read (a notecard given), put
    /// away (its ×); the sheet's clear key, which puts every waiting notice away; or a SLurl in a line (Button: the
    /// place's SLurl, Entry: the line's Seq), whose press offers the place.</summary>
    public sealed record FeedKey(FeedKeyKind Kind, Guid Notice, string? Button = null, long Entry = 0);

    /// <summary>A waiting notice's keys, as its notification's form has them: (button name, label). Grid: a script menu,
    /// laid out as the viewer does (rows of three, the first three along the bottom). Armed: on screen long enough to
    /// take a press.</summary>
    public sealed record NoticeForm(IReadOnlyList<(string Name, string Label)> Buttons, bool Grid, bool Notecard, bool Armed);

    /// <summary>How it's being used: blocks opened in full (keys), the block under the pointer (0: none), the pointer
    /// on the sheet (the close key shows), the close key lit; the notice key under the pointer; how far back it's
    /// scrolled (logical px up from the newest's foot: the right stick, the wheel).</summary>
    public sealed record Use(IReadOnlySet<long> Open, long Hover, bool OnSheet, bool CloseLit, FeedKey? HoverKey = null, float Scroll = 0)
    {
        public static readonly Use None = new(new HashSet<long>(), 0, false, false);
        public string Key => $"{string.Join(",", Open.Order())}|{Hover}|{OnSheet}|{CloseLit}|{HoverKey}|{Scroll:0.#}";
    }

    abstract record Block;
    sealed record Said(FeedEntry Entry) : Block;
    sealed record Notice(FeedEntry Entry) : Block;
    sealed record Talk(FeedKind Kind, string Title, UUID Other, List<FeedEntry> Lines, bool Typing) : Block;

    /// <summary>The feed, or null when there's nothing to show.</summary>
    public static Drawn? Draw(PanelLook look, IReadOnlyList<FeedEntry> entries, IReadOnlyList<(UUID Id, string Name)> typingIm, float scale, Use? use = null,
        IReadOnlyDictionary<Guid, NoticeForm>? forms = null, float fit = 1, bool? layer = null)
    {
        (_width, _fit) = (BaseWidth * UiSize.WideFor(fit), fit);
        try { return DrawSheet(look, entries, typingIm, scale, use, forms, layer); }
        finally { (_width, _fit) = (0, 0); }
    }

    static Drawn? DrawSheet(PanelLook look, IReadOnlyList<FeedEntry> entries, IReadOnlyList<(UUID Id, string Name)> typingIm, float scale, Use? use,
        IReadOnlyDictionary<Guid, NoticeForm>? forms, bool? layer)
    {
        use ??= Use.None;
        forms ??= new Dictionary<Guid, NoticeForm>();
        var blocks = Blocks(entries, typingIm);
        if (blocks.Count == 0) return null;
        var ink = Ink.For(look);
        bool anyOpen = blocks.Any(b => use.Open.Contains(KeyOf(b)));
        // waiting notices make room for their keys
        bool waiting = blocks.Any(b => b is Notice { Entry.NoticeId: var id } && id != Guid.Empty);
        float max = (anyOpen ? OpenMaxHeight : waiting ? WaitingMaxHeight : MaxHeight) * Fit;
        float top = use.OnSheet ? CloseRow : 10;
        var measure = new Painter(null, ink, Width);
        var noKeys = new List<(SKRect, FeedKey)>();
        var heights = blocks.Select(b => Layout(measure, b, 0, use.Open.Contains(KeyOf(b)), false, forms, null, noKeys)).ToList();
        // at rest, the newest whole blocks that fit
        float total = top;
        int first = blocks.Count;
        while (first > 0)
        {
            float h = heights[first - 1] + Gap;
            if (total + h > max && first < blocks.Count) break;
            total += h;
            first--;
        }
        // scrolled back (the right stick, the wheel): the sheet as tall as it gets, a window onto every block that far
        // up from the newest's foot, the blocks cut at its edges
        float content = heights.Sum() + Gap * (blocks.Count - 1);
        float sheet = Math.Max(max, total - Gap + 10), view = sheet - top - 10;
        float maxScroll = Math.Max(0, content - view);
        float scroll = Math.Clamp(use.Scroll, 0, maxScroll);
        bool scrolled = scroll > 0.5f;
        if (scrolled) first = 0;
        float shift = scrolled ? content - scroll - view : 0;   // the content's y at the window's top
        var placed = new List<(SKRect, long)>();
        var keys = new List<(SKRect, FeedKey)>();
        SKRect? close = null;
        var (pixels, w, hh, used) = Rasterise(scale, Width, new List<PanelKey>(), (c, height) =>
        {
            if (scrolled && c == null) return sheet;
            var draw = new Painter(c, ink, Width);
            draw.Body(height);
            placed.Clear();
            keys.Clear();
            if (use.OnSheet)
            {
                var ck = new SKRect(Width - 8 - 30, 4, Width - 8, 34);
                draw.GlyphKey(ck, Painter.Glyph.Close, use.CloseLit);
                close = ck;
                if (waiting)
                {
                    // clear: every waiting notice put away
                    var all = new SKRect(ck.Left - 36, 4, ck.Left - 6, 34);
                    var clearKey = new FeedKey(FeedKeyKind.ClearAll, Guid.Empty);
                    draw.GlyphKey(all, Painter.Glyph.ClearAll, use.HoverKey == clearKey);
                    keys.Add((all, clearKey));
                }
            }
            float y = top - shift;
            float viewTop = use.OnSheet ? top : top - 4, viewBottom = sheet - 6;   // (under the close row, not into it)
            if (scrolled) { c!.Save(); c.ClipRect(new SKRect(0, viewTop, Width, viewBottom)); }
            for (int i = first; i < blocks.Count; i++)
            {
                var b = blocks[i];
                long key = KeyOf(b);
                // scrolled: only what's in the window is drawn, and only what's wholly in it can be pressed
                if (scrolled && (y + heights[i] < viewTop || y > viewBottom)) { y += heights[i] + Gap; continue; }
                int keysBefore = keys.Count;
                float h = Layout(draw, b, y, use.Open.Contains(key), key != 0 && key == use.Hover, forms, use.HoverKey, keys);
                if (scrolled)
                {
                    for (int k = keys.Count - 1; k >= keysBefore; k--)
                        if (keys[k].Item1.Top < viewTop || keys[k].Item1.Bottom > viewBottom) keys.RemoveAt(k);
                    var cut = new SKRect(Pad - 6, Math.Max(y, viewTop), Width - Pad + 6, Math.Min(y + h, viewBottom));
                    if (key != 0 && cut.Height > 0) placed.Add((cut, key));
                }
                else if (key != 0) placed.Add((new SKRect(Pad - 6, y, Width - Pad + 6, y + h), key));
                y += h + Gap;
            }
            if (!scrolled) return y - Gap + 10;
            c!.Restore();
            // where the window is: a thin bar at the right, as the reader has
            float track = viewBottom - viewTop - 8, thumb = Math.Max(24, track * view / content);
            float at = viewTop + 4 + (track - thumb) * (1 - scroll / maxScroll);
            using (var bar = new SKPaint { Color = A(ink.Dim, 0.45f), IsAntialias = true })
                c.DrawRoundRect(new SKRect(Width - 7, at, Width - 4, at + thumb), 1.5f, 1.5f, bar);
            return sheet;
        }, Panel.MaxRasterWidth);
        GlassLight.Finish(pixels, look, layer ?? GlassLight.Layers);
        return new Drawn(pixels, w, hh, used, look.Frost, look.TintOpacity, placed.ToList(), close, keys.ToList(), maxScroll);
    }

    /// <summary>A block's key: its first entry's Seq (0: a pane with only someone typing, which doesn't open).</summary>
    static long KeyOf(Block b) => b switch
    {
        Said s => s.Entry.Seq,
        Notice n => n.Entry.Seq,
        Talk { Lines.Count: > 0 } t => t.Lines[0].Seq,
        _ => 0,
    };

    /// <summary>The entries a block holds (what opening it keeps on screen).</summary>
    public static IEnumerable<long> Members(IReadOnlyList<FeedEntry> entries, long key)
    {
        var e = entries.FirstOrDefault(x => x.Seq == key);
        if (e == null) yield break;
        if (e.Kind is FeedKind.Nearby or FeedKind.Notice) { yield return key; yield break; }
        foreach (var x in entries) if (x.Session == e.Session && x.Kind == e.Kind) yield return x.Seq;
    }

    static List<Block> Blocks(IReadOnlyList<FeedEntry> entries, IReadOnlyList<(UUID Id, string Name)> typingIm)
    {
        var blocks = new List<Block>();
        foreach (var e in entries)
        {
            if (e.Kind == FeedKind.Nearby) blocks.Add(new Said(e));
            else if (e.Kind == FeedKind.Notice) blocks.Add(new Notice(e));
            else if (blocks.Count > 0 && blocks[^1] is Talk t && t.Lines[0].Session == e.Session) t.Lines.Add(e);
            else blocks.Add(new Talk(e.Kind, e.SessionName ?? e.Name, e.Mine ? UUID.Zero : e.From, new List<FeedEntry> { e }, false));
        }
        // someone typing an IM: under their conversation, else a pane of its own
        foreach (var (id, name) in typingIm)
        {
            int at = blocks.FindLastIndex(b => b is Talk { Kind: FeedKind.Im } t && (t.Other == id || t.Lines.Any(l => l.From == id)));
            if (at >= 0) blocks[at] = ((Talk)blocks[at]) with { Typing = true };
            else blocks.Add(new Talk(FeedKind.Im, name, id, new List<FeedEntry>(), true));
        }
        return blocks;
    }

    /// <summary>Lays out (and, with a canvas, draws) a block at y; returns its height. open: in full; lit: the
    /// pointer's on it.</summary>
    static float Layout(Painter draw, Block b, float y, bool open, bool lit, IReadOnlyDictionary<Guid, NoticeForm> forms, FeedKey? hoverKey,
        List<(SKRect, FeedKey)> keys)
    {
        var k = draw.K;
        float x0 = Pad, x1 = Width - Pad;
        int lines = open ? OpenLines : (int)MathF.Round(CutLines * Fit);
        float pane = (k.Light ? 0.42f : 0.1f) * (lit ? 1.6f : 1);
        switch (b)
        {
            case Said s:
            {
                if (lit || s.Entry.Alert)
                {
                    float h = Flow(new Painter(null, k, Width), s.Entry, x0, x1, 0, showName: true, rightAlign: false, maxLines: lines);
                    var r = new SKRect(x0 - 6, y - 2, x1 + 6, y + h + 2);
                    // a keyword or a mention of the person: washed in the accent (the viewer's highlight colour)
                    if (s.Entry.Alert && draw.Canvas is { } cv)
                        using (var wash = new SKPaint { Color = A(k.Accent, k.Light ? 0.18f : 0.26f), IsAntialias = true })
                            cv.DrawRoundRect(r, 10, 10, wash);
                    if (lit) draw.Fill(r, 10, pane * 0.6f);
                }
                return Flow(draw, s.Entry, x0, x1, y, showName: true, rightAlign: false, maxLines: lines, keys, hoverKey);
            }
            case Notice n:
            {
                // a waiting notice: its × at the top right, its keys under the words
                var id = n.Entry.NoticeId;
                bool waiting = id != Guid.Empty;
                var form = waiting ? forms.GetValueOrDefault(id) : null;
                float textRight = waiting ? x1 - 30 : x1 - 6;
                float textH = Flow(new Painter(null, k, Width), n.Entry, x0 + 30, textRight, 0, showName: n.Entry.Name.Length > 0, false, lines);
                float keysH = form != null ? NoticeKeys(new Painter(null, k, Width), form, id, x0, x1, 0, null, null) : 0;
                float h = textH + 14 + (keysH > 0 ? keysH + 4 : 0);
                draw.Fill(new SKRect(x0 - 6, y, x1 + 6, y + h), 13, pane);
                Bell(draw, x0 + 11, y + 7 + Line / 2, k.Dim);
                Flow(draw, n.Entry, x0 + 30, textRight, y + 7, showName: n.Entry.Name.Length > 0, false, lines, keys, hoverKey);
                if (waiting)
                {
                    var away = new SKRect(x1 - 24, y + 4, x1 + 2, y + 30);
                    var awayKey = new FeedKey(FeedKeyKind.Dismiss, id);
                    draw.GlyphKey(away, Painter.Glyph.Close, hoverKey == awayKey);
                    if (draw.Drawing) keys.Add((away, awayKey));
                }
                if (form != null) NoticeKeys(draw, form, id, x0, x1, y + 7 + textH + 4, hoverKey, keys);
                return h;
            }
            case Talk t:
            {
                // measure, then the pane and its lines
                float inner(Painter p, float top)
                {
                    float yy = top + 8;
                    var hf = Font(k.Semi, HeaderSize);
                    if (p.Drawing)
                    {
                        if (t.Kind == FeedKind.Im) Envelope(p, x0 + 8, yy + 8, k.Dim);
                        else People(p, x0 + 8, yy + 8, k.Dim);
                        p.Text(Ellipsize(t.Title, hf, x1 - x0 - 30), x0 + 22, yy + 13, hf, k.Dim);
                    }
                    yy += 22;
                    foreach (var l in t.Lines)
                    {
                        float lh = Flow(p, l, x0 + 4, x1 - 4, yy, showName: t.Kind != FeedKind.Im && !l.Mine, rightAlign: l.Mine, maxLines: lines, keys, hoverKey);
                        // a keyword or a mention of the person: a bar in the accent beside the line
                        if (l.Alert && p.Canvas is { } cv)
                            using (var bar = new SKPaint { Color = A(k.Accent, 0.9f), IsAntialias = true })
                                cv.DrawRoundRect(new SKRect(x0 - 2, yy + 2, x0 + 0.5f, yy + lh - 2), 1.2f, 1.2f, bar);
                        yy += lh + 2;
                    }
                    if (t.Typing)
                    {
                        if (p.Drawing)
                            using (var dot = new SKPaint { IsAntialias = true, Color = A(k.Dim, 0.85f) })
                                for (int i = 0; i < 3; i++) p.Canvas!.DrawCircle(x0 + 9 + i * 9, yy + 10, 2.8f, dot);
                        yy += 20;
                    }
                    return yy + 6 - top;
                }
                float h = inner(new Painter(null, k, Width), y);
                draw.Fill(new SKRect(x0 - 6, y, x1 + 6, y + h), 13, pane);
                inner(draw, y);
                return h;
            }
        }
        return 0;
    }

    /// <summary>A waiting notice's keys, from y; returns their height. A script menu's are three to a row as the viewer
    /// lays them out (the first three along the bottom, so a script's "< Back  OK  More >" row sits where it expects);
    /// others in their form's order, three to a row. Read comes first for a notecard given. Keys take no press until the
    /// notice has been on screen a moment (Armed): drawn quieter until then.</summary>
    static float NoticeKeys(Painter draw, NoticeForm form, Guid id, float x0, float x1, float y, FeedKey? hoverKey, List<(SKRect, FeedKey)>? keys)
    {
        var all = new List<(string? Name, string Label, FeedKeyKind Kind)>();
        if (form.Notecard) all.Add((null, "Read", FeedKeyKind.Read));
        all.AddRange(form.Buttons.Select(b => ((string?)b.Name, b.Label, FeedKeyKind.Answer)));
        if (all.Count == 0) return 0;
        const int cols = 3;
        const float size = 13.5f, line = 16;
        int rows = (all.Count + cols - 1) / cols;
        float left = x0 - 2, width = x1 + 2 - left, kw = (width - (cols - 1) * KeyGap) / cols;
        var f = Font(draw.K.Regular, size);
        // a label too long for its key takes a second line (the row grows), rather than being cut
        // (a hyphen may end a line: "Pigeon-" over "sized")
        var labels = all.Select(a => Wrap(a.Label.Replace("-", "- "), f, kw - 12, 2).Select(l => l.Replace("- ", "-")).ToList()).ToList();
        var rowH = new float[rows];
        for (int i = 0; i < all.Count; i++)
        {
            int row = form.Grid ? rows - 1 - i / cols : i / cols;
            rowH[row] = Math.Max(rowH[row], labels[i].Count > 1 ? KeyH + line : KeyH);
        }
        for (int i = 0; i < all.Count; i++)
        {
            // the viewer's order for a script menu: row 0 (its first three) at the bottom
            int row = form.Grid ? rows - 1 - i / cols : i / cols, col = i % cols;
            float top = y + rowH.Take(row).Sum() + row * KeyGap;
            var r = new SKRect(left + col * (kw + KeyGap), top, left + col * (kw + KeyGap) + kw, top + rowH[row]);
            var key = new FeedKey(all[i].Kind, id, all[i].Name);
            if (draw.Drawing)
            {
                bool lit = form.Armed && hoverKey == key;
                draw.KeyFace(r, Math.Min(KeyH / 2, draw.K.Look.Radius * 0.8f), lit, form.Armed ? 1 : 0.6f);
                var ls = labels[i];
                float ty = r.MidY - (ls.Count - 1) * line / 2 + size * 0.36f;
                foreach (var l in ls)
                {
                    draw.Text(l, r.MidX - Measure(f, l) / 2, ty, f, form.Armed ? draw.K.Text : draw.K.Dim);
                    ty += line;
                }
                keys?.Add((r, key));
            }
        }
        return rowH.Sum() + (rows - 1) * KeyGap;
    }

    /// <summary>One entry as flowing text: the name (semibold) and the words after it on the same line, wrapping
    /// under; right-aligned for the person's own lines in a conversation. SLurls in it are links (Links.cs), each a key
    /// that offers its place; a translation (Translation.cs) follows under the words, dim. Returns the height.</summary>
    static float Flow(Painter draw, FeedEntry e, float x0, float x1, float y, bool showName, bool rightAlign, int maxLines,
        List<(SKRect, FeedKey)>? keys = null, FeedKey? hoverKey = null)
    {
        float h = Words(draw, e, x0, x1, y, showName, rightAlign, maxLines, keys, hoverKey);
        if (e.Translation is not { Length: > 0 } tr) return h;
        var k = draw.K;
        var f = Font(k.Regular, TextSize);
        float yy = y + h;
        foreach (var l in Lines(tr, f, x1 - x0, 0, maxLines))
        {
            if (draw.Drawing) draw.Text(l, rightAlign ? x1 - Measure(f, l) : x0, yy + 15.5f, f, k.Dim);
            yy += Line;
        }
        return yy - y;
    }

    static float Words(Painter draw, FeedEntry e, float x0, float x1, float y, bool showName, bool rightAlign, int maxLines,
        List<(SKRect, FeedKey)>? keys, FeedKey? hoverKey)
    {
        var k = draw.K;
        var nameF = Font(k.Semi, TextSize);
        var textF = Font(e.Style == FeedStyle.Shout ? k.Semi : k.Regular, TextSize);
        var textC = e.Style is FeedStyle.Whisper or FeedStyle.Object || (e.Mine && rightAlign) ? k.Dim : k.Text;
        var nameC = e.Style == FeedStyle.Object || e.Mine ? k.Dim : k.Text;
        string lead = showName ? e.Name : "";
        float leadW = lead.Length > 0 ? Measure(nameF, lead) + (e.Style == FeedStyle.Emote ? 5 : 8) : 0;
        if (leadW > (x1 - x0) * 0.6f)
        {
            lead = Ellipsize(lead, nameF, (x1 - x0) * 0.6f - 8);
            leadW = Measure(nameF, lead) + 8;
        }
        float yy = y;
        if (Links.Has(e.Text) && Links.Parse(e.Text) is { Places.Count: > 0 } parsed)
        {
            var linked = Links.Wrap(parsed, textF, x1 - x0, leadW, maxLines);
            int lit = hoverKey is { Kind: FeedKeyKind.Link } hk && hk.Entry == e.Seq ? parsed.Places.ToList().FindIndex(p => p.Slurl == hk.Button) : -1;
            for (int i = 0; i < linked.Count; i++)
            {
                float lx = i == 0 ? x0 + leadW : x0;
                if (rightAlign) lx = x1 - Links.Width(linked[i], textF);
                if (draw.Drawing)
                {
                    if (i == 0 && lead.Length > 0) draw.Text(lead, x0, yy + 15.5f, nameF, nameC);
                    foreach (var (box, link) in Links.Draw(draw.Canvas, linked[i], lx, yy + 15.5f, textF, textC, k.Link, lit))
                        keys?.Add((box, new FeedKey(FeedKeyKind.Link, e.NoticeId, parsed.Places[link].Slurl, e.Seq)));
                }
                yy += Line;
            }
            return yy - y;
        }
        var lines = Lines(e.Text, textF, x1 - x0, leadW, maxLines);
        for (int i = 0; i < lines.Count; i++)
        {
            float lx = i == 0 ? x0 + leadW : x0;
            if (rightAlign) lx = x1 - Measure(textF, lines[i]);
            if (draw.Drawing)
            {
                if (i == 0 && lead.Length > 0) draw.Text(lead, x0, yy + 15.5f, nameF, nameC);
                draw.Text(lines[i], lx, yy + 15.5f, textF, textC);
            }
            yy += Line;
        }
        return yy - y;
    }

    /// <summary>Greedy wrap where the first line starts after the name.</summary>
    static List<string> Lines(string text, SKFont font, float width, float firstIndent, int maxLines)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var cur = "";
            float room() => lines.Count == 0 ? width - firstIndent : width;
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = cur.Length == 0 ? word : cur + " " + word;
                if (Measure(font, next) <= room()) { cur = next; continue; }
                if (cur.Length > 0) { lines.Add(cur); cur = word; }
                else cur = word;
                while (Measure(font, cur) > room() && FallbackText.Before(cur, cur.Length) > 0)
                {
                    int n = FallbackText.Before(cur, cur.Length);
                    while (FallbackText.Before(cur, n) > 0 && Measure(font, cur[..n]) > room()) n = FallbackText.Before(cur, n);
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

    static SKPaint Stroke(SKColor c) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

    static void Envelope(Painter p, float cx, float cy, SKColor c)
    {
        if (!p.Drawing) return;
        using var s = Stroke(c);
        var r = new SKRect(cx - 6.5f, cy - 4.5f, cx + 6.5f, cy + 4.5f);
        p.Canvas!.DrawRoundRect(r, 1.5f, 1.5f, s);
        using var v = new SKPath();
        v.MoveTo(r.Left + 0.8f, r.Top + 0.8f);
        v.LineTo(cx, cy + 0.8f);
        v.LineTo(r.Right - 0.8f, r.Top + 0.8f);
        p.Canvas.DrawPath(v, s);
    }

    static void People(Painter p, float cx, float cy, SKColor c)
    {
        if (!p.Drawing) return;
        using var s = Stroke(c);
        var cv = p.Canvas!;
        cv.DrawCircle(cx - 2.5f, cy - 2.5f, 2.3f, s);
        cv.DrawCircle(cx + 3.5f, cy - 1.5f, 1.9f, s);
        using var a = new SKPath();
        a.AddArc(new SKRect(cx - 7, cy + 1, cx + 2, cy + 9), 180, 180);
        cv.DrawPath(a, s);
        using var b = new SKPath();
        b.AddArc(new SKRect(cx + 0.5f, cy + 1.5f, cx + 6.5f, cy + 8), 200, 160);
        cv.DrawPath(b, s);
    }

    static void Bell(Painter p, float cx, float cy, SKColor c)
    {
        if (!p.Drawing) return;
        using var s = Stroke(c);
        using var path = new SKPath();
        path.MoveTo(cx - 5.5f, cy + 3.5f);
        path.CubicTo(cx - 4, cy + 2, cx - 4.5f, cy - 5.5f, cx, cy - 5.5f);
        path.CubicTo(cx + 4.5f, cy - 5.5f, cx + 4, cy + 2, cx + 5.5f, cy + 3.5f);
        path.Close();
        p.Canvas!.DrawPath(path, s);
        p.Canvas.DrawLine(cx - 1.5f, cy + 6, cx + 1.5f, cy + 6, s);
    }
}
