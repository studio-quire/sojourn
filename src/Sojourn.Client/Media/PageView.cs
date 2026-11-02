using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Xr;
using Sojourn.Render.Gpu;
using Sojourn.Render.Realtime;
using static Sojourn.Session.Assistant.Panel;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Media;

/// <summary>
/// A page or video shown up close, to read or watch it comfortably: one face's media brought to the person (a double
/// tap on it, the page's own fullscreen button, Z on the desktop, or the assistant), or a page an object offered that
/// they chose to open. A strip along its top holds × (put it away), back, forward and reload for a page (only where the
/// face lets them control it), play or pause for a video, and its title.
/// - In the headset, a board as the HUD board is (the panel's glass behind, the page as its own quad layer copied from
///   the renderer's newest frame, so it's as sharp as the panel): 1.2 m away, 56 degrees wide, its middle just above
///   the eyes, following the head lazily as every menu does; the right trigger on the strip drags it somewhere
///   (<see cref="PanelPlacement"/>). Either hand's ray and trigger use the page as the left hand uses a face (tap, drag
///   to scroll, hold to press and drag).
/// - On the desktop, a sheet over the middle of the view (the page drawn at the size it's shown, so text is sharp); the
///   mouse is the page's, the wheel scrolls it, keys go to a focused text field; a click outside it or Esc puts it away.
/// Hidden while a confirmation waits (nothing a page made sits beside it). Main thread.
/// Written by Claude Opus 5.5, 2026-10-26.
/// </summary>
public sealed class PageView
{
    readonly MediaManager _m;
    readonly MediaInput _input;
    readonly Func<PanelLook> _look;
    readonly Action<string> _log;

    public PageView(MediaManager m, MediaInput input, Func<PanelLook> look, Action<string> log)
    {
        _m = m;
        _input = input;
        _look = look;
        _log = log;
    }

    MediaItem? _item;
    public MediaItem? Item => _item;
    public bool Shown => _item != null;

    /// <summary>Bring an item up close (starting it if it waits); any other comes down. Any thread.</summary>
    public void Open(MediaItem i) => _m.Locked(() =>
    {
        foreach (var o in _m.ItemsUnlocked) if (o != i) o.Zoomed = false;
        if (!i.Playing) _m.Start(i);
        if (!i.Zoomed) _place = true;
        i.Zoomed = true;
        _m.Focus(i, 2000);
        _log($"media: up close: {i.Key}");
    });

    public void Close() => _m.Locked(() =>
    {
        foreach (var o in _m.ItemsUnlocked.Where(o => o.Zoomed).ToList())
        {
            o.Zoomed = false;
            // an offered page has nowhere else to be: closing it ends it
            if (o.Kind == MediaItemKind.Page) _m.Stop(o);
        }
    });

    /// <summary>Each frame, first: which item is up close, and its surface told to keep the copy this view draws from.</summary>
    bool _headset;

    void Track(bool headset)
    {
        _headset = headset;
        var now = _m.Locked(() => _m.ItemsUnlocked.FirstOrDefault(i => i.Zoomed && i.Playing));
        if (_item != null && _item != now && _item.Player is { } old) { old.Surface.WantView = false; old.Surface.WantPixels = false; }
        if (now == null && _item != null) { _gestures[0]?.Cancel(); _gestures[1]?.Cancel(); }
        // a trigger still held from what brought it up (a still hold, a press on an auto-zoom face) waits for a release
        if (now != null && now != _item) _armed[0] = _armed[1] = false;
        _item = now;
        if (now?.Player is { } p)
        {
            p.Surface.WantView = headset;
            p.Surface.WantPixels = !headset;
        }
    }

    // ---- the strip --------------------------------------------------------------------------------------------------------

    enum K { None, Close, Back, Forward, Reload, Play, Keyboard }

    /// <summary>The headset: its keyboard asked for, for this page (the strip's keyboard key, there while a text field is
    /// focused: the key appears, never the keyboard itself).</summary>
    public event Action<IKeyTarget>? KeyboardRequested;
    /// <summary>Whether the headset's keyboard is up for an item (its key stays while it is).</summary>
    public Func<MediaItem, bool>? KeyboardOn { get; set; }

    /// <summary>What the headset's keyboard types into on a page: its focused field, key by key as the desktop's
    /// keyboard sends them (nothing kept or logged).</summary>
    sealed class PageKeys(PageView v, MediaItem item) : IKeyTarget
    {
        MediaPlayer? P => item.Player is { Closed: false } p ? p : null;
        public string Label => v._item == item ? v.Title() : Lang.T("the page");
        public bool Alive => item.Zoomed && P != null;
        public object Owner => item;
        public void Opened() { }
        public void Closed() { }
        public void Char(char ch) => P?.Key("char", ch, ch);
        public void Backspace() => Press(8);
        public bool Enter() { Press(13); return false; }
        void Press(int vk)
        {
            if (P is not { } p) return;
            p.Key("down", vk);
            p.Key("char", vk, vk);
            p.Key("up", vk);
        }
    }
    const float Top = 46, Side = 10, Bottom = 10, KeyS = 34;

    List<(K Key, SKRect Rect)> Keys(float width)
    {
        var keys = new List<(K, SKRect)> { (K.Close, new SKRect(width - Side - KeyS, 6, width - Side, 6 + KeyS)) };
        if (_item?.Player is not { } p) return keys;
        float x = Side;
        void Add(K k) { keys.Add((k, new SKRect(x, 6, x + KeyS, 6 + KeyS))); x += KeyS + 4; }
        if (p.Kind == MediaKind.Video) Add(K.Play);
        else if (_m.Locked(() => _m.May(_item, control: true)))
        {
            Add(K.Back);
            Add(K.Forward);
            Add(K.Reload);
        }
        if (_headset && p.Kind == MediaKind.Web && (p.WantsKeys || KeyboardOn?.Invoke(_item) == true))
            keys.Add((K.Keyboard, new SKRect(width - Side - 2 * KeyS - 6, 6, width - Side - KeyS - 6, 6 + KeyS)));
        return keys;
    }

    void Act(K k)
    {
        if (_item?.Player is not { } p) return;
        switch (k)
        {
            case K.Close: Close(); break;
            case K.Back: _m.Locked(() => _m.UserActed(_item)); p.Back(); break;
            case K.Forward: _m.Locked(() => _m.UserActed(_item)); p.Forward(); break;
            case K.Reload: p.Reload(); break;
            case K.Play: if (p.State == "playing") p.Pause(); else p.Play(); break;
            case K.Keyboard: KeyboardRequested?.Invoke(new PageKeys(this, _item)); break;
        }
    }

    string Title()
    {
        if (_item?.Player is not { } p) return "";
        if (p.Title is { Length: > 0 } t) return t;
        var u = MediaUrls.Parse(p.Url);
        return u.Host.Length > 0 ? u.Host : p.Url;
    }

    /// <summary>The glass, the strip's keys and the title, at logical size w x h (the page goes in below the strip).</summary>
    void DrawChrome(SKCanvas canvas, PanelLook look, float w, float h, K lit, bool body, bool faded = true)
    {
        var ink = Ink.For(look);
        var draw = new Painter(canvas, ink, w);
        if (body && !faded) draw.Body(h);
        else if (body)
            using (var fade = new SKPaint { Color = SKColors.White.WithAlpha(150) })
            {
                canvas.SaveLayer(fade);
                draw.Body(h);
                canvas.Restore();
            }
        var keys = Keys(w);
        var p = _item?.Player;
        foreach (var (k, r) in keys)
        {
            var glyph = k switch
            {
                K.Close => Painter.Glyph.Close, K.Back => Painter.Glyph.Back, K.Forward => Painter.Glyph.Forward, K.Reload => Painter.Glyph.Reload,
                K.Keyboard => Painter.Glyph.Keyboard,
                _ => p?.State == "playing" ? Painter.Glyph.Pause : Painter.Glyph.Play,
            };
            bool enabled = k switch { K.Back => p?.CanBack == true, K.Forward => p?.CanForward == true, _ => true };
            if (enabled) draw.GlyphKey(r, glyph, k == lit);
            else using (var dim = new SKPaint { Color = SKColors.White.WithAlpha(90) }) { canvas.SaveLayer(dim); draw.GlyphKey(r, glyph, false); canvas.Restore(); }
        }
        float left = keys.Where(k => k.Key is not (K.Close or K.Keyboard)).Select(k => k.Rect.Right).DefaultIfEmpty(Side).Max() + 10;
        float right = keys.Where(k => k.Key is K.Close or K.Keyboard).Min(k => k.Rect.Left) - 10;
        var font = Font(ink.Regular, 17);
        draw.Text(Ellipsize(Title(), font, right - left), left, 6 + KeyS / 2 + 6, font, ink.Text);
    }

    // ---- the headset's board --------------------------------------------------------------------------------------------

    const float LogicalW = 900, Distance = 1.2f, WidthDeg = 56, Raster = 1.2f;
    float ScreenW => LogicalW - 2 * Side;
    float Aspect => _item?.Player is { } p ? Math.Clamp(Shape(p).H / (float)Math.Max(1, Shape(p).W), 0.3f, 1.6f) : 0.625f;

    /// <summary>The shape it's shown in up close: a page's own size; a video's own picture (on a face it may be stretched
    /// over a square texture).</summary>
    static (int W, int H) Shape(MediaPlayer p) =>
        p.Kind == MediaKind.Video && p.VideoWidth > 0 && p.VideoHeight > 0 ? (p.VideoWidth, p.VideoHeight)
            : (Math.Max(1, p.Surface.ContentWidth), Math.Max(1, p.Surface.ContentHeight));
    float ScreenH => ScreenW * Aspect;
    float LogicalH => Top + ScreenH + Bottom;

    /// <summary>Where the board floats: following, or where the person put it.</summary>
    public readonly PanelPlacement Placement = new("page", Rest, rangeDeg: 40);   // (56 wide: carried past 40, as the HUD board)
    Vector3 _pos;
    Quaternion _rot = Quaternion.Identity;
    float _widthM = 2 * Distance * MathF.Tan(WidthDeg / 2 * MathF.PI / 180);
    bool _place = true, _visible;
    float Mpp => _widthM / LogicalW;

    public bool RightOnBoard { get; private set; }
    public bool LeftOnBoard { get; private set; }

    enum Spot { None, Screen, Strip, Key }
    readonly record struct Aim(Spot Spot, float T, Vector2 Px, Vector3 Point, K Key);
    Aim _right, _left;
    Vector3? _rightFrom, _leftFrom;
    readonly MediaInput.Gesture[] _gestures = new MediaInput.Gesture[2];
    readonly bool[] _down = new bool[2], _armed = { true, true };
    K _pressedKey = K.None;
    bool _drag;

    /// <summary>Each headset frame, before the HUD board, the panel's hands and the world's: places the board, reads
    /// both rays and triggers. Same arguments as HudBoard.Frame.</summary>
    public void HeadsetFrame(XrControllers input, Vector3 head, Quaternion headRot, VrPilot pilot, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel,
        bool blocked, bool hidden, bool focused, float dt)
    {
        _gestures[0] ??= new MediaInput.Gesture(_m, opens: false);
        _gestures[1] ??= new MediaInput.Gesture(_m, opens: false);
        Track(headset: true);
        (_rightFrom, _leftFrom) = (input.RightAimPos, input.LeftAimPos);
        bool wasOn = RightOnBoard || LeftOnBoard;
        RightOnBoard = LeftOnBoard = false;
        _right = _left = default;
        if (_item == null) { _place = true; _visible = false; Placement.CancelGrab(); return; }
        // placed afresh each time it comes up (brought up close, or back after a confirmation or X): where it was put
        // if that's in view, else following in front
        bool visible = !hidden;
        if (visible && (!_visible || _place)) { Placement.Place(); _place = false; }
        if (!visible && _visible) Placement.CancelGrab();
        _visible = visible;
        if (visible)
        {
            _widthM = 2 * Distance * MathF.Tan(WidthDeg / 2 * MathF.PI / 180);
            (_pos, _rot) = Placement.Update(head, headRot, new Vector2(_widthM, LogicalH * Mpp), dt, frozen: wasOn);
        }
        if (hidden || !focused)
        {
            _armed[0] = _armed[1] = false;
            EndAll();
            return;
        }
        _right = AimOf(input.RightAim, input.RightAimPos, panel);
        _left = AimOf(input.LeftAim, input.LeftAimPos, panel);
        RightOnBoard = _right.Spot != Spot.None || _gestures[1].Active || _drag;
        LeftOnBoard = _left.Spot != Spot.None || _gestures[0].Active;
        Hand(1, input.RightTrigger, _right, input.RightAim, blocked);
        Hand(0, input.LeftTrigger, _left, input.LeftAim, blocked);
    }

    public void Recentred() => _place = true;

    Vector2 _stickPx;
    MediaItem? _stickItem;

    /// <summary>The right ray on the board of a page (not a video): the right stick scrolls the page where the ray was
    /// last on it (its middle, if it hasn't been yet; Control/SheetStick).</summary>
    public StickSheet? BoardSheet()
    {
        if (_item?.Player is not { Closed: false, Kind: MediaKind.Web } p || _right.Spot == Spot.None) return null;
        var item = _item;
        if (_stickItem != item) { _stickItem = item; _stickPx = new Vector2(Shape(p).W / 2f, Shape(p).H / 2f); }
        if (_right.Spot == Spot.Screen) _stickPx = _right.Px;
        return new StickSheet(this, Scroll: (down, right) => { if (_item == item) _input.StickWheel(item, _stickPx, down, right); });
    }

    /// <summary>Where it rests while it follows: 1.2 m out where the head faces, its middle 3 degrees above the eyes.</summary>
    static (Vector3, Quaternion) Rest(Vector3 anchor, float yaw, PanelPlacement.Mode mode, Vector2 size) =>
        PanelPlacement.Toward(anchor, yaw, 3 * MathF.PI / 180, Distance);

    Aim AimOf(Quaternion? q, Vector3? from, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel)
    {
        if (q is not { } rot || from is not { } o) return default;
        var dir = Vector3.Transform(-Vector3.UnitZ, rot);
        float panelT = float.MaxValue;
        if (panel is { } pl && PlaneHit(o, dir, pl.Pos, pl.Rot, pl.Size, out float pt, out _)) panelT = pt;
        if (!PlaneHit(o, dir, _pos, _rot, new Vector2(_widthM, LogicalH * Mpp), out float t, out var local) || t >= panelT) return default;
        var px = new Vector2((local.X / _widthM + 0.5f) * LogicalW, (0.5f - local.Y / (LogicalH * Mpp)) * LogicalH);
        var point = o + dir * t;
        foreach (var (k, r) in Keys(LogicalW))
            if (r.Contains(px.X, px.Y)) return new Aim(Spot.Key, t, px, point, k);
        var s = new Vector2((px.X - Side) / ScreenW, (px.Y - Top) / ScreenH);
        if (s.X is < 0 or > 1 || s.Y is < 0 or > 1) return new Aim(Spot.Strip, t, px, point, K.None);
        var shape = Shape(_item!.Player!);
        return new Aim(Spot.Screen, t, new Vector2(s.X * shape.W, s.Y * shape.H), point, K.None);
    }

    static bool PlaneHit(Vector3 from, Vector3 dir, Vector3 pos, Quaternion rot, Vector2 size, out float t, out Vector2 local)
    {
        t = 0; local = default;
        var n = Vector3.Transform(Vector3.UnitZ, rot);
        float denom = Vector3.Dot(dir, n);
        if (denom > -1e-4f) return false;
        t = Vector3.Dot(pos - from, n) / denom;
        if (t <= 0) return false;
        var p = from + dir * t - pos;
        local = new Vector2(Vector3.Dot(p, Vector3.Transform(Vector3.UnitX, rot)), Vector3.Dot(p, Vector3.Transform(Vector3.UnitY, rot)));
        return MathF.Abs(local.X) <= size.X / 2 && MathF.Abs(local.Y) <= size.Y / 2;
    }

    void Hand(int hand, float trigger, Aim aim, Quaternion? rot, bool blocked)
    {
        var g = _gestures[hand];
        var dir = rot is { } r ? Vector3.Transform(-Vector3.UnitZ, r) : Vector3.UnitZ;
        if (trigger < 0.35f) _armed[hand] = true;
        if (!_down[hand] && trigger > 0.6f && _armed[hand])
        {
            _down[hand] = true;
            _armed[hand] = false;
            if (blocked) return;
            switch (aim.Spot)
            {
                case Spot.Key:
                    _pressedKey = aim.Key;
                    break;
                case Spot.Screen when !_gestures[1 - hand].Active && _item != null:
                    g.Down(_item, aim.Px, dir);
                    break;
                case Spot.Strip or Spot.Screen when hand == 1 && (hand == 1 ? _rightFrom : _leftFrom) is { } go:
                    _drag = true;
                    Placement.Grab(go, dir, aim.T);
                    break;
            }
        }
        else if (_down[hand] && trigger < 0.35f)
        {
            _down[hand] = false;
            if (g.Active) g.Up(aim.Spot == Spot.Screen ? aim.Px : null);
            if (_pressedKey != K.None && aim.Spot == Spot.Key && aim.Key == _pressedKey) Act(_pressedKey);
            _pressedKey = K.None;
            if (hand == 1 && _drag) { Placement.Drop(); _drag = false; }
        }
        else if (_down[hand])
        {
            if (g.Active) g.Held(aim.Spot == Spot.Screen ? aim.Px : null, dir);
            else if (hand == 1 && _drag && (hand == 1 ? _rightFrom : _leftFrom) is { } o && Placement.Drag(o, dir)) (_pos, _rot) = Placement.Pose;
        }
    }

    void EndAll()
    {
        _gestures[0]?.Cancel();
        _gestures[1]?.Cancel();
        _down[0] = _down[1] = false;
        if (_drag) Placement.CancelGrab();
        _drag = false;
        _pressedKey = K.None;
    }

    (PanelLook Look, K Lit, string Title, string State, bool Back, bool Fwd, float Aspect, int Keys, OverlayImage Image)? _chrome;
    long _version;

    /// <summary>The board's layers this frame (chrome and page, room poses, widths in metres); null when nothing's up close.</summary>
    public ((OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width) Chrome, (Func<GpuImage?> Image, Vector3 Pos, Quaternion Rot, float Width) Screen)? Layers()
    {
        if (_item?.Player is not { } p) return null;
        var look = _look();
        K lit = _right.Spot == Spot.Key ? _right.Key : _left.Spot == Spot.Key ? _left.Key : K.None;
        string title = Title();
        int nkeys = Keys(LogicalW).Count;
        if (_chrome is not { } c || !ReferenceEquals(c.Look, look) || c.Lit != lit || c.Title != title || c.State != p.State || c.Back != p.CanBack || c.Fwd != p.CanForward
            || c.Aspect != Aspect || c.Keys != nkeys)
        {
            float w = LogicalW, h = LogicalH;
            var (pixels, pw, ph, _) = Rasterise(Raster, w, new List<PanelKey>(), (canvas, height) =>
            {
                if (canvas != null) DrawChrome(canvas, look, w, h, lit, body: true, faded: false);
                return h;
            });
            _chrome = c = (look, lit, title, p.State, p.CanBack, p.CanForward, Aspect, nkeys, new OverlayImage(pixels, pw, ph, Interlocked.Increment(ref _version), look.Frost, look.TintOpacity));
        }
        float mpp = Mpp;
        var up = Vector3.Transform(Vector3.UnitY, _rot);
        var face = Vector3.Transform(Vector3.UnitZ, _rot);
        var screenPos = _pos - up * ((Top - Bottom) / 2 * mpp) + face * 0.002f;
        var surface = p.Surface;
        return ((c.Image, _pos, _rot, _widthM), (() => surface.View, screenPos, _rot, ScreenW * mpp));
    }

    /// <summary>The rays' cue on the board: a beam to it and a dot where a press would go.</summary>
    public OverlayScene? Decorate(OverlayScene? scene, VrPilot pilot)
    {
        if (_item == null) return scene;
        var beams = new List<OverlayBeam>(scene?.Beams ?? []);
        var dots = new List<OverlayDot>(scene?.Dots ?? []);
        beams.AddRange(Placement.Outline(pilot.RoomToRegion));
        bool any = Placement.Dragging;
        foreach (var (aim, from) in new[] { (_right, _rightFrom), (_left, _leftFrom) })
        {
            if (aim.Spot == Spot.None || from is not { } o) continue;
            var a = pilot.RoomToRegion(o);
            var b = pilot.RoomToRegion(aim.Point);
            beams.Add(new OverlayBeam(a + Vector3.Normalize(b - a) * 0.04f, b, 0.003f, Cue with { W = 0.55f }));
            if (aim.Spot == Spot.Screen) dots.Add(new OverlayDot(b, Math.Clamp(0.006f * aim.T, 0.008f, 0.05f), Cue));
            any = true;
        }
        return any ? (scene ?? new OverlayScene()) with { Beams = beams, Dots = dots } : scene;
    }

    static Vector4 Cue => PanelLook.InUse.Light(0.9f);

    /// <summary>For the scripted test (Xr/VrScript.Media.cs): a point of the page (0-1 across and down) in the room.</summary>
    public Vector3? ScreenPoint(Vector2 uv) => _item == null ? null : BoardPoint(Side + uv.X * ScreenW, Top + uv.Y * ScreenH);
    public Vector3? KeyPoint(string name) =>
        _item == null ? null : Keys(LogicalW).Where(k => k.Key.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)).Select(k => (Vector3?)BoardPoint(k.Rect.MidX, k.Rect.MidY)).FirstOrDefault();

    Vector3 BoardPoint(float x, float y) => _pos + Vector3.Transform(new Vector3((x - LogicalW / 2) * Mpp, (LogicalH / 2 - y) * Mpp, 0), _rot);

    /// <summary>The page's corners in the room, for the in-eyes preview.</summary>
    public Vector3[]? ScreenCorners()
    {
        if (Layers() is not { } l) return null;
        var s = l.Screen;
        var right = Vector3.Transform(Vector3.UnitX, s.Rot) * (s.Width / 2);
        var up = Vector3.Transform(Vector3.UnitY, s.Rot) * (s.Width * Aspect / 2);
        return new[] { s.Pos - right + up, s.Pos + right + up, s.Pos + right - up, s.Pos - right - up };
    }

    // ---- the desktop's sheet --------------------------------------------------------------------------------------------

    OverlayImage? _sheet;
    Task<OverlayImage?>? _composing;
    (long Version, K Lit, string Title, string State, int W, int H, PanelLook Look, bool Back, bool Fwd) _composed;
    Vector2 _sheetAt;
    float _ui = 1;            // the sheet's strip scale (window height)
    float _shownScale = 1;    // screen pixels per page pixel
    int _pageW, _pageH;       // the page's shown size, pixels
    K _deskLit;
    bool _deskOn, _deskPress;
    Vector2 _deskPx;
    long _lastMove;

    /// <summary>The mouse is on the sheet (the world and the panel don't take it).</summary>
    public bool DesktopOn => _sheet != null && _deskOn;
    /// <summary>The sheet is over the view (a click anywhere is its: outside puts it away).</summary>
    public bool SheetUp => _sheet != null;

    /// <summary>Each desktop frame: the sheet over the view, and the page's hover. mouse: in the eye image's pixels.</summary>
    public OverlayScene? Desktop(OverlayScene? scene, Vector2? mouse, int ew, int eh, bool confirming)
    {
        Track(headset: false);
        if (_item?.Player is not { } p || confirming) { _sheet = null; _deskOn = false; return scene; }
        var s = p.Surface;
        _ui = Math.Clamp(eh / 820f, 0.8f, 1.6f);
        float top = Top * _ui, side = Side * _ui, bottom = Bottom * _ui;
        var (cw, ch) = Shape(p);
        float fit = MathF.Min((ew * 0.88f - 2 * side) / cw, (eh * 0.86f - top - bottom) / ch);
        fit = Math.Clamp(fit, 0.25f, 3f);
        _m.DesktopZoomScale = fit;
        (_shownScale, _pageW, _pageH) = (fit, (int)(cw * fit), (int)(ch * fit));
        int sw = (int)(_pageW + 2 * side), sh = (int)(_pageH + top + bottom);
        _sheetAt = new Vector2(MathF.Round((ew - sw) / 2f), MathF.Round((eh - sh) / 2f));

        // where the mouse is on it
        _deskOn = false;
        _deskLit = K.None;
        Vector2? pagePx = null;
        if (mouse is { } m)
        {
            var local = m - _sheetAt;
            _deskOn = local.X >= 0 && local.Y >= 0 && local.X < sw && local.Y < sh;
            var logical = local / _ui;
            foreach (var (k, r) in Keys(sw / _ui))
                if (r.Contains(logical.X, logical.Y)) _deskLit = k;
            var inPage = local - new Vector2(side, top);
            if (_deskOn && inPage.X >= 0 && inPage.Y >= 0 && inPage.X < _pageW && inPage.Y < _pageH) pagePx = inPage / fit;
        }
        long now = Environment.TickCount64;
        if (pagePx is { } hp && p.Kind == MediaKind.Web && hp != _deskPx && now - _lastMove >= 16)
        {
            p.Mouse("move", hp.X, hp.Y, held: _deskPress);
            (_deskPx, _lastMove) = (hp, now);
        }

        // the picture: composed off the main thread when the page, the strip or the size changed
        long version;
        lock (s.PixelsLock) version = s.PixelsVersion;
        var want = (version, _deskLit, Title(), p.State, sw, sh, _look(), p.CanBack, p.CanForward);
        if (_composing is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully && done.Result is { } img) _sheet = img;
            _composing = null;
        }
        if (_composing == null && want != _composed && version > 0)
        {
            _composed = want;
            var (look, lit, ui, pw, ph) = (want.Item7, _deskLit, _ui, _pageW, _pageH);
            _composing = Task.Run(() => Compose(s, look, lit, sw, sh, ui, pw, ph));
        }
        if (_sheet == null) return scene;
        return (scene ?? new OverlayScene()) with { Page = new OverlaySheet(_sheet, _sheetAt) };
    }

    OverlayImage? Compose(Render.Media.MediaSurface s, PanelLook look, K lit, int sw, int sh, float ui, int pw, int ph)
    {
        var info = new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = new byte[sw * sh * 4];
        unsafe
        {
            fixed (byte* dst = pixels)
            {
                using var surface = SKSurface.Create(info, (nint)dst, sw * 4);
                if (surface == null) return null;
                var c = surface.Canvas;
                c.Clear(SKColors.Transparent);
                c.Save();
                c.Scale(ui);
                DrawChrome(c, look, sw / ui, sh / ui, lit, body: true);
                c.Restore();
                lock (s.PixelsLock)
                {
                    if (s.Pixels is not { } src || s.PixelsWidth <= 0) return null;
                    fixed (byte* sp = src)
                    {
                        var srcInfo = new SKImageInfo(s.PixelsWidth, s.PixelsHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                        using var img = SKImage.FromPixels(srcInfo, (nint)sp, s.PixelsWidth * 4);
                        var dstRect = SKRect.Create(Side * ui, Top * ui, pw, ph);
                        using var paint = new SKPaint();
                        var sampling = s.PixelsWidth == pw && s.PixelsHeight == ph ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
                        c.DrawImage(img, SKRect.Create(0, 0, s.PixelsWidth, s.PixelsHeight), dstRect, sampling, paint);
                    }
                }
                c.Flush();
            }
        }
        return new OverlayImage(pixels, sw, sh, Interlocked.Increment(ref _version), Frost: 0.9f);
    }

    /// <summary>A left click: true if the sheet took it (on it, or outside it: that puts it away).</summary>
    public bool DesktopDown()
    {
        if (_sheet == null) return false;
        if (!_deskOn) { Close(); return true; }
        if (_deskLit != K.None) { _pressedKey = _deskLit; return true; }
        if (_item?.Player is { Kind: MediaKind.Web } p && InPage())
        {
            _deskPress = true;
            _m.Locked(() => _m.UserActed(_item));
            _input.SetKeys(_item);
            p.Mouse("down", _deskPx.X, _deskPx.Y);
        }
        else if (_item?.Player is { Kind: MediaKind.Video } v && InPage()) { if (v.State == "playing") v.Pause(); else v.Play(); }
        return true;
    }

    bool InPage() => _deskPx.X >= 0 && _deskPx.Y >= 0;

    public bool DesktopUp()
    {
        if (_sheet == null && !_deskPress) return false;
        if (_pressedKey != K.None) { if (_deskLit == _pressedKey) Act(_pressedKey); _pressedKey = K.None; return true; }
        if (_deskPress) { _deskPress = false; _item?.Player?.Mouse("up", _deskPx.X, _deskPx.Y); return true; }
        return _deskOn;
    }

    public bool DesktopWheel(float notches)
    {
        if (!DesktopOn || _item?.Player is not { Kind: MediaKind.Web } p) return DesktopOn;
        p.Wheel(_deskPx.X, _deskPx.Y, 0, (int)(notches * 100));
        return true;
    }
}
