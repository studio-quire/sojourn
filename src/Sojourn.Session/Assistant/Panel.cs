using System.Diagnostics;
using SkiaSharp;

namespace Sojourn.Session.Assistant;

public sealed record PanelChoice(string Id, string Label, string? Detail);

/// <summary>A gated op waiting for the person: what the client says it will do (never the model's words).</summary>
public sealed class PendingConfirm
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    /// <summary>The arguments of the op that raised it (the same object the caller passed), so a caller can tell its own
    /// confirmation from another waiting (Sojourn Access's ConfirmedAsync).</summary>
    public object? For { get; init; }
    public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The confirm key takes a press that starts this long after the confirmation is first on screen (and
    /// after it last moved): a double click, a press meant for whatever was there before, or a panel gliding into view
    /// under the pointer can't confirm what the person hasn't seen.</summary>
    public const long ArmMs = 800;
    long _shownAt = -1;
    /// <summary>When it was first drawn or last moved (Environment.TickCount64), or -1.</summary>
    public long ShownAt => Interlocked.Read(ref _shownAt);
    public void MarkShown() => Interlocked.CompareExchange(ref _shownAt, Environment.TickCount64, -1);
    /// <summary>It moved (the headset's panel glided to follow the head): the arming starts again. Only ever later.</summary>
    public void MarkMoved()
    {
        long now = Environment.TickCount64, was;
        do { was = Interlocked.Read(ref _shownAt); if (was < 0 || was >= now) return; }
        while (Interlocked.CompareExchange(ref _shownAt, now, was) != was);
    }
    public bool ArmedAt(long tick) => ShownAt >= 0 && tick - ShownAt >= ArmMs;
}

/// <summary>What the panel shows. Changed only through <see cref="Panel.Update"/>.</summary>
public sealed class PanelState
{
    public string? Heard;           // the request, as heard or typed
    public string? Typing;          // a request being typed on the desktop (null: not typing)
    public bool Listening, Hearing, Working;
    public string? Model;           // the model working on the request ("haiku 5.5"), shown faintly while working
    public string? Status;          // what it's doing now
    public string? Result;          // what it says back
    public string? Prompt;          // above the choices
    public List<PanelChoice> Choices = new();
    public PendingConfirm? Confirm;
    public string? Notice;          // something about the assistant itself (no microphone, not running)
    public string? Jobs;            // background jobs at work, in their own few words: dim, at the card's foot
    public string? Pointing;        // what the pointer is on: shown while talking or typing, when "that" means it
    public string? PointAsk;        // the assistant asked the person to point at what they mean (its words): waits for it
    public string? TypeAsk;         // the assistant asked them to check a word by typing (its question); Typing holds its guess
    public string Hint = "";        // how to talk, behind the idle panel's help key
    public string PointHow = "Click on it";   // how to answer a request to point (the trigger, or a click), under it
    public bool KeyboardKey;        // the headset: a key that brings up its keyboard for a typed request
    public bool Private;            // incognito is on: its mark on the panel (Private/PanelPrivate.cs)
    public bool PrivateSees;        // ...and the person lets the assistant see (the mark's lenses open)
    public PanelDictation Dictation; // words for other people being dictated (Private/Dictation.cs)
    public PanelDraft? Draft;       // dictated words waiting for the person to send or cancel
}

/// <summary>Something on the panel the pointer can press.</summary>
public enum KeyKind { Choice, Confirm, Cancel, Hide, Menu, Tab, Help, Keyboard, Page, Replies, Draft }
public readonly record struct PanelKey(KeyKind Kind, int Index, SKRect Rect);

/// <summary>
/// The assistant's one panel: state, when it shows, and its look (<see cref="PanelLook"/>: glass by default). Words
/// only where the person needs to read them: no state names, no instructions; a lit orb while it listens, dots while
/// it works, what was asked small above the answer, choices and a confirmation as keys.
/// While listening it's a small capsule (with what the pointer is on); otherwise a card 400 logical px wide. Laid out
/// in logical pixels and rasterised with Skia at any scale (2.4 for the headset, about 1 on the desktop).
/// Thread-safe: the agent API updates it, the main thread hovers and presses, a raster runs off the main thread.
/// </summary>
public sealed partial class Panel
{
    public const float CardWidth = 400;
    const float Pad = 22;

    readonly object _lock = new();
    readonly PanelState _s = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _version = 1;
    double _touched;
    int _hover = -1;
    bool _hidden, _help;

    public long Version { get { lock (_lock) return _version; } }

    /// <summary>Change what it shows. show: bring it back if the person hid it; news: it's something to read (it
    /// keeps the panel up a while), not just what the pointer is on.</summary>
    public void Update(Action<PanelState> change, bool show = true, bool news = true)
    {
        lock (_lock)
        {
            change(_s);
            // a new answer starts on its first page, and joins the session's replies (one taken away by a new request
            // keeps its page: it stays on the card, where the person was reading it, until the new answer comes)
            if (_s.Result != _pagedResult)
            {
                if (!string.IsNullOrEmpty(_s.Result)) _page = 0;
                _pagedResult = _s.Result;
                Keep(_s.Heard, _s.Result);
            }
            _version++;
            if (news) _touched = _clock.Elapsed.TotalSeconds;
            if (show) _hidden = false;
        }
    }

    public T Read<T>(Func<PanelState, T> read) { lock (_lock) return read(_s); }

    // --- the session's replies (user, 2026-10-27: they vanished before they could be read): kept in memory for the
    // session only, readable again in the reader from the stack key

    /// <summary>A reply: what was asked, the answer, and when it first came (the panel's clock, seconds).</summary>
    public sealed record Reply(string? Asked, string Text, double At = 0);
    readonly List<Reply> _replies = new();
    long _repliesVersion;

    public IReadOnlyList<Reply> Replies { get { lock (_lock) return _replies.ToList(); } }
    public long RepliesVersion { get { lock (_lock) return _repliesVersion; } }

    /// <summary>Under the lock: an answer joins the replies; the same request's answer growing replaces its own.</summary>
    void Keep(string? asked, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { _cleared = true; return; }
        // the same answer growing (streamed, or a status folded in) replaces its own; anything else is a new reply
        if (_replies.Count > 0 && !_cleared && _replies[^1].Asked == asked && text.StartsWith(_replies[^1].Text, StringComparison.Ordinal))
            _replies[^1] = _replies[^1] with { Text = text };
        else _replies.Add(new Reply(asked, text, _clock.Elapsed.TotalSeconds));
        if (_replies.Count > 200) _replies.RemoveAt(0);
        _cleared = false;
        _repliesVersion++;
    }
    bool _cleared = true;   // the answer was taken away since the last kept (a new request): the next is a new reply

    // --- the answer before (user, 2026-10-27: replying took away the answer they were reading) -----------------------

    /// <summary>How long an answer stays with the conversation: shown again while the next request is heard and worked
    /// on, and as a dim strip above the next answer.</summary>
    const double RecentSeconds = 600;

    /// <summary>Under the lock. prev: the last answer, while a new request (talking, typing, heard, worked on) has taken
    /// its place and nothing else (choices, a question, a draft) is up; it stays on the card, whole, until the new
    /// answer comes. earlier: the answer before the one showing, for the dim strip above it.</summary>
    (string? Prev, string? Earlier) Before(PanelState s)
    {
        if (_replies.Count == 0 || s.Confirm != null || s.Choices.Count > 0 || s.PointAsk != null || s.TypeAsk != null || s.Draft != null || s.Dictation != PanelDictation.None)
            return (null, null);
        double now = _clock.Elapsed.TotalSeconds;
        var last = _replies[^1];
        bool asking = s.Listening || s.Hearing || s.Working || s.Typing != null;
        // talking or typing over the answer still showing: it moves up, the new request under it
        if (asking && !string.IsNullOrEmpty(s.Result)) return (s.Result, null);
        if (string.IsNullOrEmpty(s.Result))
            return ((asking || !string.IsNullOrEmpty(s.Heard)) && now - last.At < RecentSeconds ? last.Text : null, null);
        if (_replies.Count >= 2 && last.Text == s.Result && last.At - _replies[^2].At < RecentSeconds)
            return (null, _replies[^2].Text);
        return (null, null);
    }

    /// <summary>How much wider in logical px a sheet is for a fit (Fit^0.7): at 0.7, 1.28x, so a sheet keeps ~90% of
    /// its angular width with type 30% smaller (the user, 2026-10-27: spend the freed space on more content, more
    /// readable). Answers get ~60 characters a line there rather than ~48; not the full 1/Scale, which would run lines
    /// past a comfortable measure. (The VR client's UiSize.WideFor.)</summary>
    public static float WideFor(float fit) => MathF.Pow(fit, 0.7f);

    float _fit = 1;
    /// <summary>How much more an answer's page holds than at size 1 (the headset's UiSize.Fit: smaller text, more lines a
    /// page in the same space).</summary>
    public float Fit
    {
        get { lock (_lock) return _fit; }
        set { lock (_lock) { if (_fit == value) return; _fit = value; _version++; } }
    }

    /// <summary>The pointer's key (index into the last raster's keys), for its lit look.</summary>
    public void SetHover(int key)
    {
        lock (_lock)
        {
            if (_hover == key) return;
            _hover = key;
            _version++;
        }
    }

    public void Hide() { lock (_lock) { _hidden = true; _version++; } }

    double _heldUntil;
    /// <summary>The pointer is on it, or it's being dragged: it doesn't fade away under the person, and stays a moment
    /// after they let go.</summary>
    public void Hold() { lock (_lock) _heldUntil = _clock.Elapsed.TotalSeconds + 3; }

    int _page;
    string? _pagedResult;
    /// <summary>A long answer's page keys: the next page (+1) or the one before (-1). Turning a page keeps it up a while.</summary>
    public void Turn(int dir)
    {
        lock (_lock)
        {
            _page = Math.Max(0, Math.Min(_page, _pageCount - 1) + Math.Sign(dir));
            if (_pageCount > 0) _page = Math.Min(_page, _pageCount - 1);
            _version++;
            _touched = _clock.Elapsed.TotalSeconds;
        }
    }

    /// <summary>How many pages the answer on the card was last drawn in (0: none): a size change can leave the page
    /// past the last, which Draw clamps.</summary>
    int _pageCount;

    /// <summary>The idle panel's help key: how to talk, shown or not.</summary>
    public void ToggleHelp() { lock (_lock) { _help = !_help; _version++; _touched = _clock.Elapsed.TotalSeconds; } }

    bool _suppressed;
    /// <summary>Kept out of sight with the rest of the interface (the X button, VrSettings.InterfaceHidden): only a
    /// confirmation shows. Everything else (replies, choices, a request to point or to type, a dictated draft) waits on
    /// it until it's shown again; showing it brings the panel up.</summary>
    public bool Suppressed
    {
        get { lock (_lock) return _suppressed; }
        set { lock (_lock) { _suppressed = value; _hidden = false; _version++; if (!value) _touched = _clock.Elapsed.TotalSeconds; } }
    }

    /// <summary>Showing: anything going on or waiting, or something new in the last few seconds (longer for longer
    /// answers), unless the person hid the interface. A confirmation always shows: it's the safety gate, and every
    /// other op waits behind it.</summary>
    public bool Visible
    {
        get
        {
            lock (_lock)
            {
                if (_s.Confirm != null) return true;
                if (_suppressed) return false;
                if (_s.Draft != null) return true;
                if (_hidden) return false;
                if (_clock.Elapsed.TotalSeconds < _heldUntil) return true;
                if (_s.Listening || _s.Hearing || _s.Working || _s.Typing != null || _s.Choices.Count > 0 || _s.PointAsk != null) return true;
                if (_s.Draft != null || _s.Dictation != PanelDictation.None) return true;
                // long enough to read in a headset (user, 2026-10-27: replies went before they were read); and every
                // reply stays in the reader (the stack key)
                double linger = 15 + 0.08 * ((_s.Result?.Length ?? 0) + (_s.Notice?.Length ?? 0));
                return _clock.Elapsed.TotalSeconds - _touched < Math.Min(linger, 75);
            }
        }
    }

    /// <summary>The orb and the dots move: 16 steps a cycle, 8 a second, while listening or working (else 0).</summary>
    public int Phase => Read(s => s.Listening || s.Working || s.Hearing || s.Dictation != PanelDictation.None) ? (int)(_clock.Elapsed.TotalSeconds * 8) % 16 : 0;

    // ---------------------------------------------------------------------------------------------------------------
    // the look

    PanelLook _look = new();
    /// <summary>The look it's drawn in (the person's setting, or what they asked the assistant for).</summary>
    public PanelLook Look
    {
        get { lock (_lock) return _look; }
        set { lock (_lock) { _look = value; _version++; } PanelLook.InUse = value; }
    }

    /// <summary>Adaptive ink (the person's setting): the ink each sheet in the look takes by what's behind it.</summary>
    public AdaptiveInk? Adaptive { get; set; }

    /// <summary>The look a sheet in it is drawn in now: the person's, with adaptive ink's choice for that sheet.</summary>
    public PanelLook LookFor(InkSheet sheet)
    {
        var look = Look;
        return Adaptive?.Look(sheet, look) ?? look;
    }

    /// <summary>A drawn panel, with what its keys stood for when drawn (the confirmation, the choices). Frost: how much
    /// the renderer blurs the world behind it; Edge: the body's least opacity, where its frost reaches full.</summary>
    public sealed record Raster(byte[] Rgba, int Width, int Height, float Scale, IReadOnlyList<PanelKey> Keys, long Version,
        PendingConfirm? Confirm, IReadOnlyList<PanelChoice> Choices, bool Armed, float Frost = 0, float Edge = 1, bool PointAsk = false)
    {
        public float LogicalWidth => Width / Scale;
        public float LogicalHeight => Height / Scale;
        /// <summary>The settings menu's rows and tabs as drawn (logical px): a row's index, or -1 - i for tab i; what a
        /// resting pointer's tip is for.</summary>
        public IReadOnlyList<(int Target, SKRect Rect)>? Spots { get; init; }
    }

    /// <summary>Draw the panel at a scale (pixels per logical pixel). Premultiplied RGBA8, gamma-encoded, finished with
    /// the glass's light for the headset's layer or the desktop's overlay (layer: GlassLight.Layers unless given).</summary>
    public Raster Draw(float scale, bool? layer = null)
    {
        PanelState s;
        int hover;
        long version;
        PanelLook look;
        bool help, history;
        int page;
        float fit;
        string? prev, earlier;
        int phase = Phase;
        lock (_lock)
        {
            s = new PanelState
            {
                Heard = _s.Heard, Typing = _s.Typing, Listening = _s.Listening, Hearing = _s.Hearing, Working = _s.Working, Model = _s.Model,
                Status = _s.Status, Result = _s.Result, Prompt = _s.Prompt, Choices = _s.Choices.ToList(), Confirm = _s.Confirm,
                Notice = _s.Notice, Pointing = _s.Pointing, Hint = _s.Hint, Jobs = _s.Jobs, KeyboardKey = _s.KeyboardKey, PointAsk = _s.PointAsk, PointHow = _s.PointHow,
                TypeAsk = _s.TypeAsk,
            };
            hover = _hover;
            version = _version;
            look = _look;
            help = _help;
            page = _page;
            (s.Private, s.PrivateSees, s.Dictation, s.Draft) = (_s.Private, _s.PrivateSees, _s.Dictation, _s.Draft);
            history = _replies.Count > 0;
            fit = _fit;
            (prev, earlier) = Before(_s);
            if (prev != null) s.Result = null;   // drawn as the answer before, above the new request
        }

        // while a confirmation waits, the panel shows only the client's words for it: nothing caller-written beside
        // it (what was heard, status, result, notices, the model, choices) to contradict it or move its keys, and it's
        // drawn in a preset (a vetted look), never a look the assistant was asked to make
        if (s.Confirm != null)
        {
            s.Heard = s.Status = s.Result = s.Prompt = s.Notice = s.Model = s.Typing = s.Jobs = s.PointAsk = s.TypeAsk = null;
            s.Choices = new();
            s.Listening = s.Hearing = s.Working = false;
            (s.Draft, s.Dictation) = (null, PanelDictation.None);
            look = PanelLook.PresetNamed(look.Preset);
            prev = earlier = null;
        }
        else if (Adaptive is { } adaptive) look = adaptive.Look(InkSheet.Panel, look);
        bool armed = s.Confirm?.ArmedAt(Environment.TickCount64) ?? false;
        var ink = Ink.For(look);

        var keys = new List<PanelKey>();
        var form = FormOf(s);
        // talking again with the last answer still to read: the card stays, the orb beneath it
        if (prev != null && form == Form.Listening) form = Form.Card;
        float width = form switch
        {
            Form.Listening => s.Dictation != PanelDictation.None ? DictationWidth(s) : ListeningWidth(ink, s) + (s.Private ? 36 : 0),
            Form.Idle => IdleWidth(ink, s, help) + (history ? 40 : 0),
            // at a smaller size, wider in logical px: longer lines in about the same space (UiSize.WideFor)
            _ => CardWidth * WideFor(fit),
        };
        _answerPages = 0;
        var (pixels, w, h, used) = Rasterise(scale, width, keys, (c, height) =>
        {
            var draw = new Painter(c, ink, width);
            return form switch
            {
                Form.Listening => s.Dictation != PanelDictation.None ? LayoutDictation(draw, s, phase, height) : LayoutListening(draw, s, phase, height),
                Form.Idle => LayoutIdle(draw, s, hover, help, keys, height, history),
                _ => LayoutCard(draw, s, hover, phase, armed, keys, height, page, history, fit, prev, earlier),
            };
        }, MaxRasterWidth);
        GlassLight.Finish(pixels, look, layer ?? GlassLight.Layers);
        lock (_lock)
        {
            // the page as drawn (clamped to the last, after a size change made fewer), so ‹ goes back from there
            _pageCount = _answerPages;
            if (_answerPages > 0 && _page >= _answerPages && _version == version) _page = _answerPages - 1;
        }
        return new Raster(pixels, w, h, used, keys, version, s.Confirm, s.Choices, armed, look.Frost, look.TintOpacity, s.PointAsk != null);
    }

    /// <summary>The tallest raster in pixels (the headset's panel swapchain, XrSession.PanelMaxH): a taller panel is drawn
    /// at a smaller scale, so it's never cropped.</summary>
    public const int MaxRasterHeight = 1536;
    /// <summary>The widest (XrSession.PanelMaxW): a wide sheet at a large UiSize is drawn at a smaller scale, not cropped.</summary>
    public const int MaxRasterWidth = 1152;

    /// <summary>Lay out once to find the height (null canvas), then draw at the scale (less if it would be too tall, or
    /// wider than maxWidth pixels: the sheets on the headset's layers pass <see cref="MaxRasterWidth"/>). Keys are those
    /// of the drawing.</summary>
    internal static (byte[] Pixels, int W, int H, float Scale) Rasterise(float scale, float width, List<PanelKey> keys, Func<SKCanvas?, float, float> layout,
        float maxWidth = float.MaxValue)
    {
        float height = layout(null, 0);
        keys.Clear();
        scale = Math.Min(scale, Math.Min((MaxRasterHeight - 1) / Math.Max(height, 1), maxWidth / Math.Max(width, 1)));
        int w = (int)MathF.Ceiling(width * scale), h = (int)MathF.Ceiling(height * scale);
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = new byte[w * h * 4];
        unsafe
        {
            fixed (byte* p = pixels)
            {
                using var surface = SKSurface.Create(info, (nint)p, w * 4) ?? throw new InvalidOperationException("no Skia surface");
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(scale);
                keys.Clear();
                layout(canvas, height);
                canvas.Flush();
            }
        }
        return (pixels, w, h, scale);
    }

    enum Form { Listening, Idle, Card }

    static Form FormOf(PanelState s)
    {
        if (s.Confirm != null || s.Typing != null || s.Choices.Count > 0 || s.PointAsk != null || s.Draft != null) return Form.Card;
        if (s.Listening || s.Dictation != PanelDictation.None) return Form.Listening;
        bool empty = string.IsNullOrEmpty(s.Heard) && string.IsNullOrEmpty(s.Result) && string.IsNullOrEmpty(s.Status)
                     && string.IsNullOrEmpty(s.Notice) && string.IsNullOrEmpty(s.Jobs) && !s.Working && !s.Hearing;
        return empty ? Form.Idle : Form.Card;
    }

    // --- listening: a capsule with the orb, and what "that" is ------------------------------------------------------

    const float PillH = 52;

    static float ListeningWidth(Ink k, PanelState s)
    {
        if (s.Pointing is not { Length: > 0 } p) return 64;
        return Math.Min(CardWidth, 64 + 22 + Measure(Font(k.Regular, 15), p) + 22);
    }

    static float LayoutListening(Painter draw, PanelState s, int phase, float height)
    {
        draw.Body(height);
        draw.Orb(32, PillH / 2, phase);
        if (s.Pointing is { Length: > 0 } p)
        {
            draw.TargetDot(64 + 8, PillH / 2);
            var f = Font(draw.K.Regular, 15);
            draw.Text(Ellipsize(p, f, draw.W - 64 - 22 - 20 - (s.Private ? 36 : 0)), 64 + 22, PillH / 2 + 5.4f, f, draw.K.Text);
        }
        if (s.Private) PrivateMark(draw, draw.W - 30, PillH / 2, s.PrivateSees);
        return PillH;
    }

    // --- idle, nothing to show: the microphone, and help behind a key -------------------------------------------------

    static float IdleWidth(Ink k, PanelState s, bool help) =>
        (help && s.Hint.Length > 0 ? Math.Min(CardWidth + 40, 52 + Measure(Font(k.Regular, 15), s.Hint) + 18 + 44) : 108) + (s.KeyboardKey ? 40 : 0);

    static float LayoutIdle(Painter draw, PanelState s, int hover, bool help, List<PanelKey> keys, float height, bool history)
    {
        var k = draw.K;
        draw.Body(height);
        if (s.Private) PrivateMark(draw, 30, PillH / 2, s.PrivateSees); else draw.Mic(30, PillH / 2, 22, k.Text);
        if (help && s.Hint.Length > 0)
        {
            var f = Font(k.Regular, 15);
            draw.Text(Ellipsize(s.Hint, f, draw.W - 52 - 62 - (s.KeyboardKey ? 40 : 0)), 52, PillH / 2 + 5.4f, f, k.Dim);
        }
        var helpKey = new SKRect(draw.W - 46, 8, draw.W - 10, PillH - 8);
        if (s.KeyboardKey)
        {
            var kb = new SKRect(helpKey.Left - 40, 8, helpKey.Left - 4, PillH - 8);
            draw.GlyphKey(kb, Painter.Glyph.Keyboard, hover == keys.Count);
            keys.Add(new PanelKey(KeyKind.Keyboard, 0, kb));
        }
        if (history)
        {
            // the session's replies, in the reader
            var stack = new SKRect(helpKey.Left - 40 - (s.KeyboardKey ? 40 : 0), 8, helpKey.Left - 4 - (s.KeyboardKey ? 40 : 0), PillH - 8);
            draw.GlyphKey(stack, Painter.Glyph.Stack, hover == keys.Count);
            keys.Add(new PanelKey(KeyKind.Replies, 0, stack));
        }
        draw.GlyphKey(helpKey, Painter.Glyph.Help, hover == keys.Count, on: help);
        keys.Add(new PanelKey(KeyKind.Help, 0, helpKey));
        return PillH;
    }

    // --- the card ---------------------------------------------------------------------------------------------------

    /// <summary>An answer up to this many lines shows whole (the card grows); a longer one goes a page of
    /// <see cref="PageLines"/> at a time.</summary>
    const int WholeLines = 14, PageLines = 12;

    /// <summary>A long answer's lines in pages of about equal length, at most <see cref="PageLines"/> each, a page
    /// ending at a sentence's end when one falls within its last few lines (so a page doesn't stop mid-thought).</summary>
    static List<(int Start, int Count)> Pages(List<string> lines, float fit = 1)
    {
        var pages = new List<(int, int)>();
        // at a smaller size (fit > 1) a page holds more lines in the same space
        int whole = (int)MathF.Round(WholeLines * fit), pageLines = Math.Max(4, (int)MathF.Round(PageLines * fit));
        if (lines.Count <= whole) { pages.Add((0, lines.Count)); return pages; }
        int n = (lines.Count + pageLines - 1) / pageLines, per = (lines.Count + n - 1) / n;
        for (int start = 0; start < lines.Count;)
        {
            int end = Math.Min(lines.Count, start + per);
            if (end < lines.Count)
                for (int e = end; e >= end - 3 && e > start + 3; e--)
                    if (lines[e - 1].TrimEnd() is { Length: > 0 } t && t[^1] is '.' or '!' or '?' or ':') { end = e; break; }
            pages.Add((start, end - start));
            start = end;
        }
        return pages;
    }

    /// <summary>An answer in full, a page at a time when long (‹ › keys and a dot a page under it, every page as tall as
    /// the tallest so the keys stay put). Returns the y under it.</summary>
    [ThreadStatic] static int _answerPages;   // the pages the answer on the card was drawn in (Draw reads it back)

    static float Answer(Painter draw, string text, float right, int page, float fit, int hover, List<PanelKey> keys, float y)
    {
        var k = draw.K;
        float W = draw.W;
        var f = Font(k.Regular, 18);
        var lines = WrapUnderKeys(text, f, right - Pad, W - 2 * Pad);
        var breaks = Pages(lines, fit);
        // paged, every page's first line sits beside the keys
        if (breaks.Count > 1 && right < W - Pad) breaks = Pages(lines = Wrap(text, f, right - Pad, int.MaxValue), fit);
        int pages = breaks.Count;
        _answerPages = pages;
        int pg = Math.Clamp(page, 0, pages - 1);
        var shown = lines.Skip(breaks[pg].Start).Take(breaks[pg].Count).ToList();
        foreach (var l in shown) { draw.Text(l, Pad, y + 18, f, k.Text); y += 25; }
        if (pages > 1)
        {
            y += 25 * (breaks.Max(b => b.Count) - shown.Count) + 4;
            var back = new SKRect(Pad - 8, y, Pad + 32, y + 40);
            var next = new SKRect(W - Pad - 32, y, W - Pad + 8, y + 40);
            if (pg > 0) { draw.GlyphKey(back, Painter.Glyph.Back, hover == keys.Count); keys.Add(new PanelKey(KeyKind.Page, -1, back)); }
            if (pg < pages - 1) { draw.GlyphKey(next, Painter.Glyph.Forward, hover == keys.Count); keys.Add(new PanelKey(KeyKind.Page, 1, next)); }
            draw.PageDots(W / 2, y + 20, pages, pg);
            y += 40;
        }
        return y;
    }

    /// <summary>Wrapped lines, the first narrower (beside the keys at the top right), the rest full width.</summary>
    static List<string> WrapUnderKeys(string text, SKFont f, float first, float rest)
    {
        var narrow = Wrap(text, f, first, int.MaxValue);
        if (first >= rest || narrow.Count < 2) return narrow;
        // the first paragraph's words after the first line, and the paragraphs after it
        var paras = text.Replace("\r", "").Split('\n');
        var words = paras[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int n = narrow[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (n == 0 || n > words.Length || string.Join(" ", words.Take(n)) != narrow[0]) return narrow;   // a word broken across lines
        var after = string.Join("\n", new[] { string.Join(" ", words.Skip(n)) }.Concat(paras.Skip(1)));
        var lines = new List<string> { narrow[0] };
        if (n < words.Length || paras.Length > 1) lines.AddRange(Wrap(after, f, rest, int.MaxValue));
        return lines;
    }

    static float LayoutCard(Painter draw, PanelState s, int hover, int phase, bool armed, List<PanelKey> keys, float height, int page, bool history = false,
        float fit = 1, string? prev = null, string? earlier = null)
    {
        var k = draw.K;
        float W = draw.W, inner = W - 2 * Pad;
        bool confirming = s.Confirm != null;
        draw.Body(height);
        float y = 18;

        // close (not on a confirmation: cancel answers it, and it can't be put away)
        float topRight = W - Pad;
        if (!confirming && s.Draft == null)   // a draft, too, is answered by its own keys
        {
            var close = new SKRect(W - 12 - 36, 10, W - 12, 46);
            draw.GlyphKey(close, Painter.Glyph.Close, hover == keys.Count);
            keys.Add(new PanelKey(KeyKind.Hide, 0, close));
            topRight = close.Left - 6;
            if (s.KeyboardKey && s.Typing == null && !s.Working && !s.Hearing)
            {
                // a typed follow-up, as from the idle panel
                var kb = new SKRect(close.Left - 42, 10, close.Left - 6, 46);
                draw.GlyphKey(kb, Painter.Glyph.Keyboard, hover == keys.Count);
                keys.Add(new PanelKey(KeyKind.Keyboard, 0, kb));
                topRight = kb.Left - 6;
            }
            if (history && s.Typing == null && (prev != null || s.Result is { Length: > 0 } && !s.Working && !s.Hearing))
            {
                // this reply and the ones before it, in the reader
                var stack = new SKRect(topRight - 36, 10, topRight, 46);
                draw.GlyphKey(stack, Painter.Glyph.Stack, hover == keys.Count);
                keys.Add(new PanelKey(KeyKind.Replies, 0, stack));
                topRight = stack.Left - 6;
            }
        }
        if (s.Private && !confirming) { PrivateMark(draw, topRight - 18, 28, s.PrivateSees); topRight -= 42; }

        // the answer before: while the next request is heard and worked on, the last answer stays whole where it was
        // read, the new request under a rule below it; once the new answer is here, the one before it is a dim strip
        // above (pressed, it opens the replies in the reader)
        if (prev != null)
        {
            y = Answer(draw, prev, topRight, page, fit, hover, keys, y);
            y += 8;
            draw.Hairline(Pad - 4, W - Pad + 4, y);
            y += 12;
        }
        else if (earlier != null)
        {
            var f = Font(k.Regular, 14.5f);
            var lines = Wrap(earlier, f, topRight - Pad, 2);
            var strip = new SKRect(Pad - 8, y - 6, topRight + 2, y + lines.Count * 20 + 4);
            if (hover == keys.Count) draw.Fill(strip, 12, k.Look.KeyOpacity * 0.9f);
            keys.Add(new PanelKey(KeyKind.Replies, 0, strip));
            foreach (var l in lines) { draw.Text(l, Pad, y + 14, f, k.Dim); y += 20; }
            y += 8;
            draw.Hairline(Pad - 4, W - Pad + 4, y);
            y += 12;
        }

        // what was asked: while it's being worked on, beside the dots and in full ink; after, small and dim above the
        // answer (the answer is what matters now)
        bool busy = (s.Working || s.Hearing) && s.TypeAsk == null;   // asked to type, the request waits on the person
        if (s.Listening && s.Typing == null && !busy)
        {
            // talking again over the last answer: the orb where the request will go, and what "that" is
            draw.Orb(Pad + 10, y + 16, phase);
            if (s.Pointing is { Length: > 0 } p)
            {
                draw.TargetDot(Pad + 40, y + 16);
                var pf = Font(k.Regular, 15);
                draw.Text(Ellipsize(p, pf, W - Pad - (Pad + 54)), Pad + 54, y + 21.4f, pf, k.Text);
            }
            y += 38;
        }
        else if (s.Typing != null && s.TypeAsk is { } typeAsk)
        {
            // asked to check a word: the assistant's question, and its guess in a field below, the caret at its end,
            // ready to send as it is or after a fix; the request it's about small and dim above
            if (s.Heard is { Length: > 0 } about)
            {
                var hf = Font(k.Regular, 14.5f);
                foreach (var l in Wrap(about, hf, topRight - Pad, 2)) { draw.Text(l, Pad, y + 14, hf, k.Dim); y += 20; }
                y += 10;
            }
            var f = Font(k.Regular, 17.5f);
            foreach (var l in Wrap(typeAsk, f, (y < 40 ? topRight : W - Pad) - Pad, 4)) { draw.Text(l, Pad, y + 17, f, k.Text); y += 24; }
            y += 8;
            var lines = Wrap(s.Typing + "|", f, inner - 8 - (s.Listening ? 36 : 0), 4);
            var field = new SKRect(Pad - 8, y, W - Pad + 8, y + lines.Count * 24 + 16);
            draw.Recess(field, Math.Min(14, k.Look.Radius * 0.6f));
            if (s.Listening) draw.Orb(field.Right - 22, field.Top + 20, phase);   // answering by voice instead
            y += 8;
            foreach (var l in lines) { draw.Text(l, Pad + 4, y + 17, f, k.Text); y += 24; }
            y += 18;
        }
        else if (s.Typing != null)
        {
            var f = Font(k.Regular, 17.5f);
            var lines = Wrap(s.Typing + "|", f, topRight - Pad, 4);
            foreach (var l in lines) { draw.Text(l, Pad, y + 17, f, k.Text); y += 24; }
            y += 8;
            if (s.Pointing is { Length: > 0 } p)
            {
                draw.TargetDot(Pad + 6, y + 8);
                var pf = Font(k.Regular, 14);
                draw.Text(Ellipsize(p, pf, inner - 24), Pad + 20, y + 13, pf, k.Dim);
                y += 24;
            }
        }
        else if (busy)
        {
            draw.Dots(Pad + 4, y + 11, phase);
            if (s.Heard is { Length: > 0 } heard)
            {
                var f = Font(k.Regular, 16.5f);
                foreach (var l in Wrap(heard, f, topRight - Pad - 34, 3)) { draw.Text(l, Pad + 34, y + 16, f, k.Text); y += 23; }
            }
            else y += 23;
            y += 6;
        }
        else if (s.Heard is { Length: > 0 } heard)
        {
            var f = Font(k.Regular, 14.5f);
            foreach (var l in Wrap(heard, f, topRight - Pad, 2)) { draw.Text(l, Pad, y + 14, f, k.Dim); y += 20; }
            y += 10;
        }

        if (s.Notice is { Length: > 0 } notice)
        {
            var f = Font(k.Regular, 15);
            foreach (var l in Wrap(notice, f, (y < 40 ? topRight : W - Pad) - Pad, 5)) { draw.Text(l, Pad, y + 15, f, k.Accent); y += 21; }
            y += 8;
        }
        if (s.Status is { Length: > 0 } status)
        {
            // what it's doing now, and (while working) which model has it: why it's quick or slow
            var f = Font(k.Regular, 14.5f);
            var mf = Font(k.Regular, 12.5f);
            string? model = busy && s.Model is { Length: > 0 } m ? m : null;
            float room = (y < 40 ? topRight : W - Pad) - Pad - (model != null ? Measure(mf, model) + 14 : 0);
            var lines = Wrap(status, f, room, 2);
            if (model != null) draw.TextRight(model, W - Pad, y + 14, mf, k.Faint);
            foreach (var l in lines) { draw.Text(l, busy ? Pad + 34 : Pad, y + 14, f, k.Dim); y += 20; }
            y += 8;
        }
        else if (busy && s.Model is { Length: > 0 } model)
        {
            var mf = Font(k.Regular, 12.5f);
            draw.Text(model, Pad + 34, y + 12, mf, k.Faint);
            y += 22;
        }
        if (s.PointAsk is { } ask)
        {
            // asked to point: the assistant's question, and what the pointer is on now (the amber dot, as out there)
            var f = Font(k.Regular, 17.5f);
            foreach (var l in Wrap(ask, f, (y < 40 ? topRight : W - Pad) - Pad, 4)) { draw.Text(l, Pad, y + 17, f, k.Text); y += 24; }
            y += 6;
            draw.TargetDot(Pad + 6, y + 8);
            if (s.Pointing is { Length: > 0 } p)
            {
                var pf = Font(k.Regular, 15);
                draw.Text(Ellipsize(p, pf, inner - 24), Pad + 20, y + 13, pf, k.Dim);
            }
            y += 30;
            if (s.PointHow.Length > 0)
            {
                // how to answer, which isn't guessable (the user, 2026-10-28: they said "I'm pointing" and waited)
                var hf = Font(k.Regular, 13.5f);
                draw.Text(s.PointHow, Pad, y + 13, hf, k.Faint);
                y += 22;
            }
        }
        if (s.Result is { Length: > 0 } result)
        {
            // the whole answer: the card grows to WholeLines (more at a smaller size), past that it pages
            y = Answer(draw, result, y < 40 ? topRight : W - Pad, page, fit, hover, keys, y);
            y += 10;
        }
        if (s.Jobs is { Length: > 0 } jobs)
        {
            // what's going on in the background (a build), quietly: the panel doesn't come up for it
            var f = Font(k.Regular, 13.5f);
            foreach (var l in Wrap(jobs, f, (y < 40 ? topRight : W - Pad) - Pad, 2)) { draw.Text(l, Pad, y + 13, f, k.Dim); y += 19; }
            y += 8;
        }
        if (s.Draft is { } draft) y = LayoutDraft(draw, draft, hover, keys, y, y < 40 ? topRight : W - Pad);

        if (s.Choices.Count > 0)
        {
            if (s.Prompt is { Length: > 0 } prompt)
            {
                var f = Font(k.Regular, 17.5f);
                foreach (var l in Wrap(prompt, f, (y < 40 ? topRight : W - Pad) - Pad, 5)) { draw.Text(l, Pad, y + 17, f, k.Text); y += 24; }
                y += 10;
            }
            var num = Font(k.Regular, 14);
            var lf = Font(k.Regular, 17);
            var df = Font(k.Regular, 13.5f);
            for (int i = 0; i < s.Choices.Count; i++)
            {
                var ch = s.Choices[i];
                bool detail = ch.Detail is { Length: > 0 };
                // in full: a long label or detail wraps and the key grows, rather than being cut
                float textW = W - Pad + 8 - (Pad - 8) - 56;
                var ll = Wrap(ch.Label, lf, textW, 3);
                var dl = detail ? Wrap(ch.Detail!, df, textW, 3) : new List<string>();
                float rowH = 46 + (ll.Count - 1) * 22 + (detail ? 10 + (dl.Count - 1) * 19 : 0);
                var row = new SKRect(Pad - 8, y, W - Pad + 8, y + rowH);
                bool lit = hover == keys.Count;
                draw.KeyFace(row, Math.Min(18, k.Look.Radius * 0.75f), lit);
                // numbered: the desktop's number keys, and "the second one"
                draw.Text((i + 1).ToString(), row.Left + 16, row.Top + (detail ? 23 : 29), num, k.Faint);
                float ly = row.Top + (detail ? 24 : 29);
                foreach (var l in ll) { draw.Text(l, row.Left + 38, ly, lf, k.Text); ly += 22; }
                ly += -2;
                foreach (var l in dl) { draw.Text(l, row.Left + 38, ly, df, k.Dim); ly += 19; }
                keys.Add(new PanelKey(KeyKind.Choice, i, row));
                y += row.Height + 8;
            }
        }

        if (s.Confirm is { } confirm)
        {
            var titleF = Font(k.Semi, 20);
            var detailF = Font(k.Regular, 15);
            if (y > 30) y += 2;
            foreach (var l in Wrap(confirm.Title, titleF, inner, 4)) { draw.Text(l, Pad, y + 20, titleF, k.Text); y += 27; }
            var dl = Wrap(confirm.Detail, detailF, inner, 10);
            // the detail carries amounts and names: full ink
            if (dl.Count > 0) { y += 4; foreach (var l in dl) { draw.Text(l, Pad, y + 15, detailF, k.Text); y += 21; } }
            y += 18;
            float half = (inner - 12) / 2;
            var cancel = new SKRect(Pad, y, Pad + half, y + 52);
            var ok = new SKRect(cancel.Right + 12, y, W - Pad, y + 52);
            draw.Key(cancel, "cancel", hover == keys.Count, 16.5f);
            keys.Add(new PanelKey(KeyKind.Cancel, 0, cancel));
            draw.AccentKey(ok, "confirm", armed && hover == keys.Count, armed);
            keys.Add(new PanelKey(KeyKind.Confirm, 0, ok));
            y += 52 + 6;
        }

        return Math.Max(y + 14, 56);
    }
}
