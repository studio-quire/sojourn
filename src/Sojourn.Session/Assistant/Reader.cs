using System.Text.Json.Nodes;
using SkiaSharp;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Render.Realtime;
using static Sojourn.Session.Assistant.Panel;

namespace Sojourn.Session.Assistant;

/// <summary>One thing in the reader: a reply (what was asked, small and dim, then the answer), or a run of text.</summary>
public sealed record ReaderPart(string? Asked, string Text);

/// <summary>What the reader holds: the session's replies, a notecard, long text the assistant put there (it carries
/// the assistant's spark; a notecard is someone else's words and doesn't), or all of what a waiting confirmation names
/// (the client's own words; For: that confirmation).</summary>
public sealed record ReaderDoc(ReaderKind Kind, string Title, IReadOnlyList<ReaderPart> Parts, object? For = null)
{
    /// <summary>A confirmation's list key: its lists whole, each under its count ("46 items"), or, with none, its title
    /// and detail uncut.</summary>
    public static ReaderDoc Listing(PendingConfirm pc)
    {
        if (pc.Lists.Count == 0)
            return new(ReaderKind.Listing, "", new[] { new ReaderPart(null, pc.Title), new ReaderPart(null, pc.Detail) }.Where(p => p.Text.Length > 0).ToList(), pc);
        if (pc.Lists.Count == 1)
            return new(ReaderKind.Listing, pc.Lists[0].Title, new[] { new ReaderPart(null, string.Join('\n', pc.Lists[0].Names)) }, pc);
        return new(ReaderKind.Listing, "", pc.Lists.Select(l => new ReaderPart(l.Title, string.Join('\n', l.Names))).ToList(), pc);
    }
}

public enum ReaderKind { Replies, Notecard, Text, Listing }

/// <summary>
/// The reader (the session of 2026-10-27: no way to read a notecard in the headset, replies gone before they were read,
/// and show cards too small for long text): a sheet in the panel's look holding long text, scrolled by hand. It holds
/// one thing at a time: the session's replies (opened from the panel's stack key, scrolled to the newest), a notecard
/// (from a notice's read key, or the assistant), or text the assistant put there. A title strip with × at the top; the
/// text below, scrolled by dragging it (the pointer's trigger, or the mouse) or the wheel, with a thin bar at the right
/// that says where you are. Kept to the person's reading: the assistant opens and closes it, nothing on it is pressed
/// but ×. Drawn in the panel's own glass. Thread-safe: the API opens it, the main thread scrolls
/// and draws it (a raster off the main thread).
/// </summary>
public sealed class Reader
{
    public const float BaseWidth = 440, Header = 50, Pad = 24, Bar = 10;

    float _wide = 1;
    /// <summary>How much wider than 440 logical px (the headset's UiSize.Wide: longer lines at a smaller size).</summary>
    public float Wide
    {
        get { lock (_lock) return _wide; }
        set { lock (_lock) { if (_wide == value) return; _wide = value; _layout = null; _version++; } }
    }
    const float BodySize = 17, AskedSize = 14, Line = 24, AskedLine = 19, PartGap = 22;
    public const float BaseViewHeight = 560;

    float _viewHeight = BaseViewHeight;
    /// <summary>The tallest it gets, logical px (the headset: 560 at UiSize 1, more at smaller sizes, so the same space
    /// holds more lines).</summary>
    public float ViewHeight
    {
        get { lock (_lock) return _viewHeight; }
        set { lock (_lock) { if (_viewHeight == value) return; _viewHeight = value; _version++; } }
    }

    readonly object _lock = new();
    ReaderDoc? _doc;
    float _scroll;
    // what a confirmation's list was opened over (hidden behind the confirmation): back when the list closes
    (ReaderDoc Doc, float Scroll)? _under;
    bool _toEnd;
    long _version = 1;
    PanelLook _look = new();

    public bool Open { get { lock (_lock) return _doc != null; } }
    public ReaderDoc? Doc { get { lock (_lock) return _doc; } }
    public long Version { get { lock (_lock) return _version; } }

    /// <summary>The look it's drawn in (the panel's).</summary>
    public PanelLook Look { get { lock (_lock) return _look; } set { lock (_lock) { if (ReferenceEquals(_look, value)) return; _look = value; _layout = null; _version++; } } }

    /// <summary>Raised when it opens (the headset places it in front of the person) or closes.</summary>
    public event Action<bool>? OpenChanged;

    /// <summary>Raised when a confirmation's list leaves the reader (closed, or something else opened): its For.</summary>
    public event Action<object>? ListingClosed;

    public void Show(ReaderDoc doc, bool atEnd)
    {
        bool was;
        object? listing;
        lock (_lock)
        {
            was = _doc != null;
            listing = _doc is { Kind: ReaderKind.Listing } old && !ReferenceEquals(old.For, doc.For) ? old.For : null;
            if (doc.Kind == ReaderKind.Listing) { if (_doc is { Kind: not ReaderKind.Listing } under) _under = (under, _scroll); }
            else _under = null;
            _doc = doc;
            _layout = null;
            _scroll = 0;
            _toEnd = atEnd;
            _version++;
        }
        if (listing != null) ListingClosed?.Invoke(listing);
        if (!was) OpenChanged?.Invoke(true);
    }

    /// <summary>The list of this confirmation is what it holds.</summary>
    public bool Lists(object? confirm) { lock (_lock) return confirm != null && _doc is { Kind: ReaderKind.Listing } d && ReferenceEquals(d.For, confirm); }

    /// <summary>The confirmation was answered: its list, if open, goes (what it was opened over comes back).</summary>
    public void CloseListing(object confirm) { if (Lists(confirm)) Close(); }

    /// <summary>New replies while it holds them: the list grows under the person, staying at the end if they were there.</summary>
    public void Refresh(ReaderDoc doc)
    {
        lock (_lock)
        {
            if (_doc?.Kind != doc.Kind) return;
            bool atEnd = _layout is { } l && _scroll >= l.Height - (_viewHeight - Header) - 4;
            _doc = doc;
            _layout = null;
            _toEnd |= atEnd;
            _version++;
        }
    }

    public void Close()
    {
        bool was, still;
        object? listing;
        lock (_lock)
        {
            was = _doc != null;
            listing = _doc is { Kind: ReaderKind.Listing } d ? d.For : null;
            // a confirmation's list closed: back to what was open under it
            if (listing != null && _under is { } u) { _doc = u.Doc; _scroll = u.Scroll; }
            else _doc = null;
            _under = null;
            still = _doc != null;
            _layout = null;
            _version++;
        }
        if (listing != null) ListingClosed?.Invoke(listing);
        if (was && !still) OpenChanged?.Invoke(false);
    }

    /// <summary>Scrolls by logical pixels (positive: further down the text).</summary>
    public void ScrollBy(float dy)
    {
        lock (_lock)
        {
            if (_doc == null || MathF.Abs(dy) < 0.01f) return;
            float before = _scroll;
            _scroll = Clamp(_scroll + dy);
            if (MathF.Abs(_scroll - before) > 0.01f) _version++;
        }
    }

    public float Scroll { get { lock (_lock) return _scroll; } }

    float Clamp(float s) => _layout is { } l ? Math.Clamp(s, 0, Math.Max(0, l.Height - (_viewHeight - Header))) : Math.Max(0, s);

    /// <summary>How far down the text the view is (0 top, 1 end), and whether there's more than one view of it.</summary>
    public (float Fraction, bool Long) Position
    {
        get
        {
            lock (_lock)
            {
                if (_layout is not { } l || l.Height <= _viewHeight - Header) return (0, false);
                return (_scroll / (l.Height - (_viewHeight - Header)), true);
            }
        }
    }

    // --- layout: the whole text as lines, once per text and look -----------------------------------------------------

    // a line with SLurls in it carries its words (Social/Links.cs: links drawn in the accent, pressed to offer the place)
    sealed record LaidLine(float Y, string Text, bool Asked, List<Social.Links.Word>? Words = null, IReadOnlyList<Places.Place>? Spots = null);
    sealed record Laid(List<LaidLine> Lines, List<float> Dividers, float Height);
    Laid? _layout;

    static Laid LayOut(ReaderDoc doc, Ink k, float sheetW)
    {
        var lines = new List<LaidLine>();
        var dividers = new List<float>();
        var body = Font(k.Regular, BodySize);
        var asked = Font(k.Regular, AskedSize);
        float width = sheetW - 2 * Pad - Bar, y = 14;
        for (int i = 0; i < doc.Parts.Count; i++)
        {
            var p = doc.Parts[i];
            // replies apart by a faint rule; a notecard's paragraphs by a blank line's space
            if (i > 0 && doc.Kind == ReaderKind.Replies) { dividers.Add(y + PartGap / 2 - 4); y += PartGap; }
            else if (i > 0) y += 12;
            if (p.Asked is { Length: > 0 } a)
            {
                foreach (var l in Wrap(a, asked, width, 4)) { lines.Add(new LaidLine(y, l, true)); y += AskedLine; }
                y += 6;
            }
            if (Social.Links.Has(p.Text) && Social.Links.Parse(p.Text) is { Places.Count: > 0 } parsed)
                foreach (var l in Social.Links.Wrap(parsed, body, width, 0, int.MaxValue))
                {
                    lines.Add(new LaidLine(y, string.Join(' ', l.Select(w => w.Text)), false, l, parsed.Places));
                    y += Line;
                }
            else foreach (var l in Wrap(p.Text, body, width, int.MaxValue)) { lines.Add(new LaidLine(y, l, false)); y += Line; }
        }
        return new Laid(lines, dividers, y + 14);
    }

    /// <summary>A SLurl in the text pressed (not dragged): its place, for the feed to offer.</summary>
    public event Action<Places.Place>? LinkPressed;

    /// <summary>The place of the link at a point on the sheet (logical pixels), if any.</summary>
    public Places.Place? LinkAt(System.Numerics.Vector2 at)
    {
        Laid? laid;
        float scroll;
        lock (_lock) { laid = _layout; scroll = _scroll; }
        if (laid == null || at.Y < Header) return null;
        float y = at.Y - Header + scroll;
        var body = Font(Ink.For(Look).Regular, BodySize);
        foreach (var l in laid.Lines)
        {
            if (l.Words == null || l.Spots == null || y < l.Y || y > l.Y + Line) continue;
            foreach (var (box, link) in Social.Links.Draw(null, l.Words, Pad, l.Y + 18, body, default, default))
                if (box.Contains(at.X, y)) return l.Spots[link];
        }
        return null;
    }

    // --- drawing ---------------------------------------------------------------------------------------------------------

    /// <summary>A drawn view of it: the picture, where × is (logical pixels), and the version drawn.</summary>
    public sealed record Raster(OverlayImage Image, float Scale, SKRect Close, long Version)
    {
        public float LogicalWidth => Image.Width / Scale;
        public float LogicalHeight => Image.Height / Scale;
    }

    public SKRect CloseKey => CloseAt(BaseWidth * Wide);
    static SKRect CloseAt(float w) => new(w - 12 - 36, 8, w - 12, 44);
    long _imageVersion;

    /// <summary>Draws what's in view now (null when it's closed). closeLit: the pointer's on ×.</summary>
    public Raster? Draw(float scale, bool closeLit, bool? layer = null)
    {
        ReaderDoc? doc;
        PanelLook look;
        float scroll, viewHeight, Width;
        long version;
        Laid laid;
        lock (_lock)
        {
            if (_doc == null) return null;
            doc = _doc;
            look = _look;
            Width = BaseWidth * _wide;
            _layout ??= LayOut(doc, Ink.For(look), Width);
            laid = _layout;
            if (_toEnd) { _scroll = Clamp(float.MaxValue); _toEnd = false; }
            _scroll = Clamp(_scroll);
            scroll = _scroll;
            version = _version;
            viewHeight = _viewHeight;
        }
        var reading = look;   // the panel's own glass: the user wants every menu alike (2026-10-28)
        var k = Ink.For(reading);
        float view = viewHeight - Header;
        float height = Math.Min(viewHeight, Header + laid.Height);
        var (pixels, w, h, used) = Rasterise(scale, Width, new List<PanelKey>(), (c, hh) =>
        {
            if (c == null) return height;
            var draw = new Painter(c, k, Width);
            draw.Body(height);
            // the strip: the assistant's spark on its own text, the title, ×
            float tx = Pad;
            if (doc.Kind == ReaderKind.Text) { Painter.Spark(draw, Pad + 5, Header / 2, 6, k.Dim); tx += 20; }
            var tf = Font(k.Semi, 16);
            var closeKey = CloseAt(Width);
            draw.Text(Ellipsize(doc.Title, tf, closeKey.Left - 8 - tx), tx, Header / 2 + 5.8f, tf, k.Text);
            draw.GlyphKey(closeKey, Painter.Glyph.Close, closeLit);
            using (var rule = new SKPaint { Color = A(k.Text, 0.12f), IsAntialias = true, StrokeWidth = 1 })
                c.DrawLine(Pad - 6, Header - 0.5f, Width - Pad + 6, Header - 0.5f, rule);
            // the text in view
            c.Save();
            c.ClipRect(new SKRect(0, Header, Width, height - 4));
            var body = Font(k.Regular, BodySize);
            var asked = Font(k.Regular, AskedSize);
            float top = Header - scroll;
            foreach (var l in laid.Lines)
            {
                float y = top + l.Y;
                if (y + Line < Header || y > height) continue;
                if (l.Words != null) Social.Links.Draw(c, l.Words, Pad, y + 18, body, k.Text, k.Link);
                else draw.Text(l.Text, Pad, y + (l.Asked ? 14 : 18), l.Asked ? asked : body, l.Asked ? k.Dim : k.Text);
            }
            using (var div = new SKPaint { Color = A(k.Text, 0.1f), IsAntialias = true, StrokeWidth = 1 })
                foreach (var d in laid.Dividers)
                    if (top + d > Header && top + d < height) c.DrawLine(Pad, top + d, Width - Pad - Bar, top + d, div);
            c.Restore();
            // where you are in it
            if (laid.Height > view)
            {
                float track = height - Header - 16, thumb = Math.Max(28, track * view / laid.Height);
                float at = Header + 8 + (track - thumb) * (scroll / (laid.Height - view));
                using var t = new SKPaint { Color = A(k.Dim, 0.45f), IsAntialias = true };
                c.DrawRoundRect(new SKRect(Width - 12, at, Width - 8, at + thumb), 2, 2, t);
            }
            return height;
        }, MaxRasterWidth);
        GlassLight.Finish(pixels, reading, layer ?? GlassLight.Layers);
        return new Raster(new OverlayImage(pixels, w, h, Interlocked.Increment(ref _imageVersion), reading.Frost, reading.TintOpacity), used, CloseAt(Width), version);
    }

    // --- what the person's hand does (the headset's ReaderView and the desktop share it) ----------------------------------

    bool _closeLit, _onClose, _down, _dragging;
    float _downY, _lastY, _velocity;
    double _lastAt;

    /// <summary>Where the pointer is on it (logical pixels; null: not on it).</summary>
    public void Point(System.Numerics.Vector2? at, double now)
    {
        bool lit = at is { } p && CloseKey.Contains(p.X, p.Y);
        if (lit != _closeLit) { _closeLit = lit; lock (_lock) _version++; }
        if (_down && at is { } q && !_onClose)
        {
            // dragged: the text follows the hand
            float dy = _lastY - q.Y;
            if (MathF.Abs(q.Y - _downY) > 4) _dragging = true;
            if (_dragging)
            {
                ScrollBy(dy);
                double dt = Math.Max(1e-3, now - _lastAt);
                _velocity = 0.6f * _velocity + 0.4f * (float)(dy / dt);
            }
            _lastY = q.Y;
            _lastAt = now;
        }
    }

    public bool CloseLit => _closeLit;

    public void PressDown(System.Numerics.Vector2 at, double now)
    {
        _down = true;
        _dragging = false;
        _onClose = CloseKey.Contains(at.X, at.Y);
        _downY = _lastY = at.Y;
        _lastAt = now;
        _velocity = 0;
    }

    /// <summary>A release: × closes it (a press that went down and up on it); a drag lets the text run on a little.</summary>
    public void PressUp(System.Numerics.Vector2? at)
    {
        if (!_down) return;
        _down = false;
        if (_onClose && at is { } p && CloseKey.Contains(p.X, p.Y)) Close();
        // a press on a SLurl (not a drag): the viewer's click on a link, its place offered
        else if (!_onClose && !_dragging && at is { } q && LinkAt(q) is { } place) LinkPressed?.Invoke(place);
        if (!_dragging) _velocity = 0;
        _dragging = false;
    }

    public bool Pressing => _down;

    /// <summary>Each frame: a fling slows to a stop.</summary>
    public void Tick(float dt)
    {
        if (_down || MathF.Abs(_velocity) < 20) { if (!_down) _velocity = 0; return; }
        ScrollBy(_velocity * dt);
        _velocity *= MathF.Exp(-dt / 0.35f);
    }

    /// <summary>The mouse wheel over it (a notch: about three lines).</summary>
    public void Wheel(float notches) => ScrollBy(-notches * Line * 3);

    /// <summary>The right stick on it (Control/SheetStick): a fraction of what's in view (positive: further down); a
    /// fling still running stops.</summary>
    public void ScrollViews(float views)
    {
        float view;
        lock (_lock) view = _viewHeight - Header;
        _velocity = 0;
        ScrollBy(views * view);
    }
}

/// <summary>
/// SojournReader: the reader for the assistant. It opens long text there for the person (a notecard by its inventory
/// id, the session's replies, or text it wrote, with a title), closes it, and says what's open. Held like any op while
/// a confirmation is under way, and the reader hides while one waits.
/// </summary>
public sealed class ReaderApi : LeapApi
{
    readonly Reader _reader;
    readonly Func<ReaderDoc> _replies;

    public override string Name => "SojournReader";
    public override string Desc => "the reader: long text the person scrolls through themselves (a notecard, the session's replies, text you write)";
    public override bool NeedsSession => false;

    public const int MaxText = 200_000;

    public ReaderApi(AgentSession s, Reader reader, Func<ReaderDoc> replies) : base(s)
    {
        _reader = reader;
        _replies = replies;
        Op("open", "{notecard: inventory item id} | {replies: true} | {text, title?} -> {open, kind, title, length}: opens it in the reader "
            + "in front of the person (replacing what was there), at the top (replies: at the newest). Text up to 200,000 characters; "
            + "paragraphs by blank lines", (a, ct) => OpenAsync(a, ct));
        Op("close", "{}: closes the reader", _ => { _reader.Close(); return new JsonObject { ["open"] = false }; });
        Op("state", "-> {open, kind?, title?, position?}: what the reader shows (position 0 top, 1 end)", _ => State());
    }

    JsonObject State()
    {
        var d = _reader.Doc;
        var o = new JsonObject { ["open"] = d != null };
        if (d != null)
        {
            o["kind"] = d.Kind.ToString().ToLowerInvariant();
            o["title"] = d.Title;
            o["position"] = Math.Round(_reader.Position.Fraction, 2);
        }
        return o;
    }

    async Task<JsonNode?> OpenAsync(JsonObject a, CancellationToken ct)
    {
        if (a.Uuid("notecard") is { } item)
        {
            var r = await S.InventoryOps.ReadNotecardAsync(new JsonObject { ["item_id"] = item.ToString() }, ct);
            var title = (string?)r["name"] ?? "Notecard";
            var text = (string?)r["text"] ?? "";
            _reader.Show(new ReaderDoc(ReaderKind.Notecard, title, Paragraphs(text)), atEnd: false);
        }
        else if (a.Bool("replies", false)) _reader.Show(_replies(), atEnd: true);
        else if (a.Str("text") is { Length: > 0 } text)
        {
            if (text.Length > MaxText) throw new LeapError($"text is {text.Length} characters; the reader takes up to {MaxText}");
            _reader.Show(new ReaderDoc(ReaderKind.Text, a.Str("title") is { Length: > 0 } t ? t : "", Paragraphs(text)), atEnd: false);
        }
        else throw new LeapError("need notecard (an item id), replies: true, or text");
        var o = State();
        o["length"] = _reader.Doc?.Parts.Sum(p => p.Text.Length) ?? 0;
        return o;
    }

    /// <summary>Text as parts at its blank lines (a long notecard draws a few parts at a time, not one huge one).</summary>
    public static IReadOnlyList<ReaderPart> Paragraphs(string text)
    {
        var parts = new List<ReaderPart>();
        var cur = new System.Text.StringBuilder();
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                if (cur.Length > 0) { parts.Add(new ReaderPart(null, cur.ToString().TrimEnd('\n'))); cur.Clear(); }
                continue;
            }
            cur.Append(line).Append('\n');
        }
        if (cur.Length > 0) parts.Add(new ReaderPart(null, cur.ToString().TrimEnd('\n')));
        if (parts.Count == 0) parts.Add(new ReaderPart(null, ""));
        return parts;
    }
}
