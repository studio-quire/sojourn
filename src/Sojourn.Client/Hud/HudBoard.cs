using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Xr;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using static Sojourn.Session.Assistant.Panel;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Hud;

/// <summary>
/// The HUDs in the headset: a board the person calls up and puts away, holding the HUD screen as the viewer lays it out
/// (16:9, each HUD at its attachment point's edge or corner), so a HUD is where its maker put it. Called up from a token
/// at the left wrist (beside voice's mic) or by the assistant. It comes up in front of you, its middle just above eye
/// level (its bottom edge clear of the assistant's panel), 1.25 m away and 60 degrees wide (most of a monitor's view,
/// so HUD buttons keep about the size they have on a screen), and follows the head lazily as every menu does; the right
/// trigger on an empty part of it drags it somewhere (<see cref="PanelPlacement"/>), and × puts it away.
/// Either hand's ray and trigger press what's on it, as the world's left trigger does (the left button for scripts, and
/// a touch where the ray lands); the ray stops at it. The HUDs are their own quad layer, sharp as the panel's, over a
/// glass board in the panel's look (a second layer behind, with the × key in its top strip, clear of any HUD).
/// </summary>
public sealed class HudBoard
{
    // the board in logical pixels: a strip at the top for ×, thin margins elsewhere, the HUD screen inside
    const float LogicalW = 857, Side = 12, Top = 44, Bottom = 12;
    const float ScreenW = LogicalW - 2 * Side;
    static readonly float ScreenH = ScreenW / HudView.BoardAspect;
    static readonly float LogicalH = ScreenH + Top + Bottom;
    const float Distance = 1.25f, WidthDeg = 60, Raster = 1.2f;
    static readonly SKRect CloseKey = new(LogicalW - Side - 34, 6, LogicalW - Side - 2, 38);

    readonly HudView _view;
    readonly Func<PanelLook> _look;
    readonly Action<string> _log;

    public HudBoard(HudView view, Func<PanelLook> look, Action<string> log)
    {
        _view = view;
        _look = look;
        _log = log;
    }

    public bool Shown { get; private set; }
    /// <summary>Where it floats: following, or where the person put it. Carried along past 40 degrees, not the panel's 30:
    /// it's 60 wide, and looking at a HUD in its corner shouldn't move it.</summary>
    public readonly PanelPlacement Placement = new("hudBoard", Rest, rangeDeg: 40);
    Vector3 _pos;
    Quaternion _rot = Quaternion.Identity;
    float _widthM = Width;
    bool _place, _visible;
    float MetresPerPx => _widthM / LogicalW;
    static float Width => 2 * Distance * MathF.Tan(WidthDeg * Assistant.UiSize.Scale * Assistant.UiSize.Wide / 2 * MathF.PI / 180);

    /// <summary>Bring the board up in front of you (or put it away). Thread-safe: it takes effect next frame.</summary>
    public void Show(bool on)
    {
        if (on && !Shown) _place = true;
        if (on != Shown) _log(on ? "HUD board: up" : "HUD board: put away");
        Shown = on;
    }

    // --- the hands -------------------------------------------------------------------------------------------------

    /// <summary>This frame: the right ray is on the board (or the token), so the panel's pointer, keys and the world
    /// behind wait; the left ray is, so the world's touch waits.</summary>
    public bool RightOnBoard { get; private set; }
    public bool LeftOnBoard { get; private set; }
    /// <summary>The HUD prim the right ray is on, if any.</summary>
    public PickHit? RightHit => _right.Spot == Spot.Screen ? _right.Hit : null;

    enum Spot { None, Screen, Margin, Close, Token }
    readonly record struct Aim(Spot Spot, float T, Vector2 Uv, Vector3 Point, PickHit? Hit);

    bool _rightDown, _leftDown, _rightArmed = true, _leftArmed = true, _closeDown;
    int _pressHand = -1;   // the hand holding a press on a HUD: 0 left, 1 right
    Aim _right, _left;
    Vector3? _token;
    bool _tokenLit, _closeLit;

    /// <summary>Each headset frame, before the panel's and the world's hands: places the board, reads both rays and
    /// triggers. panel: the panel's layer last frame (it's in front: a ray reaching it first isn't on the board).
    /// blocked: a confirmation or the menu is up (nothing is pressed, as for the world's touch). hidden: a confirmation
    /// waits, so the board isn't drawn (nothing an object made sits beside it) and takes no ray.</summary>
    public void Frame(XrControllers input, Vector3 head, Quaternion headRot, VrPilot pilot, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel,
        bool blocked, bool hidden, bool focused, float dt)
    {
        (_rightFrom, _leftFrom) = (input.RightAimPos, input.LeftAimPos);
        var worn = _view.Worn.Objects;
        if (worn.Count == 0 && Shown) Shown = false;
        // placed afresh each time it's called up: where it was put if that's in view, else following in front. Hidden a
        // while (a confirmation, X, a page up close) it goes on following out of sight, and comes back where that left it
        if (Shown && (!_visible || _place)) { Placement.Place(); _place = false; }
        if (hidden || !Shown) Placement.CancelGrab();
        _visible = Shown;
        if (Shown)
        {
            _widthM = Width;
            (_pos, _rot) = Placement.Update(head, headRot, new Vector2(_widthM, LogicalH * MetresPerPx), dt,
                frozen: RightOnBoard || LeftOnBoard || _pressHand >= 0 || _mediaHand >= 0);
        }

        _token = worn.Count > 0 && !hidden ? pilot.Watch(input, along: 0.045f) is { } t ? pilot.RegionToRoom(t) : null : null;
        if (hidden)
        {
            _right = _left = default;
            RightOnBoard = LeftOnBoard = _tokenLit = _closeLit = false;
            _rightArmed = _leftArmed = false;   // a trigger held over from the confirmation waits for a release
            EndAll();
            return;
        }
        _right = AimOf(input.RightAim, input.RightAimPos, panel, token: true);
        _left = AimOf(input.LeftAim, input.LeftAimPos, panel, token: false);
        RightOnBoard = _right.Spot != Spot.None;
        LeftOnBoard = _left.Spot is not (Spot.None or Spot.Token);
        _tokenLit = _right.Spot == Spot.Token;
        _closeLit = _right.Spot == Spot.Close;

        if (!focused)
        {
            _rightArmed = _leftArmed = false;
            EndAll();
            return;
        }
        Hand(1, input.RightTrigger, ref _rightDown, ref _rightArmed, _right, input.RightAimPos, input.RightAim, blocked);
        Hand(0, input.LeftTrigger, ref _leftDown, ref _leftArmed, _left, input.LeftAimPos, input.LeftAim, blocked);
    }

    /// <summary>The headset's own recentring moved the room: a board that's up comes up in front again (MenuPlaces
    /// forgets where it was put).</summary>
    public void Recentred()
    {
        if (Shown) _place = true;
    }

    /// <summary>Where it rests while it follows: 1.25 m out where the head faces, its middle 2 degrees above the eyes,
    /// so its bottom edge clears the assistant's panel resting 18 below them.</summary>
    static (Vector3, Quaternion) Rest(Vector3 anchor, float yaw, PanelPlacement.Mode mode, Vector2 size) =>
        PanelPlacement.Toward(anchor, yaw, 2 * MathF.PI / 180, Distance);

    Aim AimOf(Quaternion? q, Vector3? from, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel, bool token)
    {
        if (q is not { } rot || from is not { } o) return default;
        var dir = Vector3.Transform(-Vector3.UnitZ, rot);
        float panelT = float.MaxValue;
        if (panel is { } pl && PlaneHit(o, dir, pl.Pos, pl.Rot, pl.Size, out float pt, out _)) panelT = pt;
        if (token && _token is { } tk)
        {
            // the token: a ray within 2.5 cm of its middle
            float along = Vector3.Dot(tk - o, dir);
            if (along > 0 && along < panelT && Vector3.Distance(o + dir * along, tk) < 0.025f) return new Aim(Spot.Token, along, default, tk, null);
        }
        if (!_visible || !PlaneHit(o, dir, _pos, _rot, new Vector2(_widthM, LogicalH * MetresPerPx), out float t, out var local) || t >= panelT)
            return default;
        // the board's logical pixels from its top left
        var px = new Vector2((local.X / _widthM + 0.5f) * LogicalW, (0.5f - local.Y / (LogicalH * MetresPerPx)) * LogicalH);
        var point = o + dir * t;
        if (CloseKey.Contains(px.X, px.Y)) return new Aim(Spot.Close, t, default, point, null);
        var uv = new Vector2((px.X - Side) / ScreenW, (px.Y - Top) / ScreenH);
        if (uv.X is < 0 or > 1 || uv.Y is < 0 or > 1) return new Aim(Spot.Margin, t, uv, point, null);
        return new Aim(Spot.Screen, t, uv, point, _view.PickAt(uv));
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

    void Hand(int hand, float trigger, ref bool down, ref bool armed, Aim aim, Vector3? from, Quaternion? rot, bool blocked)
    {
        if (trigger < 0.35f) armed = true;
        if (!down && trigger > 0.6f && armed)
        {
            down = true;
            armed = false;
            if (blocked) return;
            switch (aim.Spot)
            {
                case Spot.Token when hand == 1:
                    Show(!Shown);
                    break;
                case Spot.Close when hand == 1:
                    _closeDown = true;
                    break;
                case Spot.Screen when aim.Hit is { } mh && rot is { } mr && _mediaHand < 0 && _view.Media?.Down(hand, mh, Vector3.Transform(-Vector3.UnitZ, mr)) == true:
                    _mediaHand = hand;   // a HUD face's page: the gesture is its
                    break;
                case Spot.Screen when aim.Hit != null && !_view.Pressing:
                    // the press follows this hand's ray across the screen while it's held
                    _pressHand = hand;
                    _view.Press(aim.Hit, () => hand == 1 ? _right.Spot == Spot.Screen ? _right.Uv : null : _left.Spot == Spot.Screen ? _left.Uv : null);
                    break;
                case Spot.Screen or Spot.Margin when hand == 1 && from is { } o && rot is { } gr:
                    _dragging = true;
                    Placement.Grab(o, Vector3.Transform(-Vector3.UnitZ, gr), aim.T);
                    break;
            }
        }
        else if (down && trigger < 0.35f)
        {
            down = false;
            if (_pressHand == hand) { _view.Release(); _pressHand = -1; }
            if (_mediaHand == hand) { _view.Media?.Up(hand, aim.Spot == Spot.Screen ? aim.Hit : null); _mediaHand = -1; }
            if (hand == 1)
            {
                if (_closeDown && aim.Spot == Spot.Close) Show(false);
                _closeDown = false;
                EndDrag();
            }
        }
        else if (down && _mediaHand == hand && rot is { } r1)
            _view.Media?.Held(hand, aim.Spot == Spot.Screen ? aim.Hit : null, Vector3.Transform(-Vector3.UnitZ, r1));
        else if (down && hand == 1 && _dragging && from is { } o2 && rot is { } r2)
        {
            // dragged: the point held stays on the ray at the distance it was taken, the board facing you
            if (Placement.Drag(o2, Vector3.Transform(-Vector3.UnitZ, r2))) (_pos, _rot) = Placement.Pose;
        }
    }

    int _mediaHand = -1;
    bool _dragging;

    /// <summary>A drag ended: where it was left is where it comes up from now on (or it follows again, dropped on its
    /// outline).</summary>
    void EndDrag()
    {
        if (_dragging) Placement.Drop();
        _dragging = false;
    }

    void EndAll()
    {
        if (_pressHand >= 0) { _view.Release(); _pressHand = -1; }
        if (_mediaHand >= 0) { _view.Media?.Cancel(_mediaHand); _mediaHand = -1; }
        _rightDown = _leftDown = _closeDown = false;
        if (_dragging) Placement.CancelGrab();
        _dragging = false;
    }

    // --- what it shows -----------------------------------------------------------------------------------------------

    /// <summary>The board's layers this frame: the chrome (glass and ×) and the HUD screen over it, as room poses and
    /// widths in metres; null when it's put away.</summary>
    public ((OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width) Chrome, (Vector3 Pos, Quaternion Rot, float Width) Screen)? Layers()
    {
        if (!Shown) return null;
        var chrome = Chrome();
        float mpp = MetresPerPx;
        // the screen's middle sits below the board's by half the difference of the top and bottom strips
        var up = Vector3.Transform(Vector3.UnitY, _rot);
        var face = Vector3.Transform(Vector3.UnitZ, _rot);
        var screenPos = _pos - up * ((Top - Bottom) / 2 * mpp) + face * 0.002f;
        return ((chrome, _pos, _rot, _widthM), (screenPos, _rot, ScreenW * mpp));
    }

    // --- where things are, for the scripted test (Xr/VrScript.Hud.cs) ---------------------------------------------------

    /// <summary>The wrist token's place in the room, while HUDs are worn.</summary>
    public Vector3? TokenRoom => _token;

    /// <summary>A point of the board by its logical pixels, in the room.</summary>
    Vector3 BoardPoint(float x, float y)
    {
        float mpp = MetresPerPx;
        return _pos + Vector3.Transform(new Vector3((x - LogicalW / 2) * mpp, (LogicalH / 2 - y) * mpp, 0), _rot);
    }

    /// <summary>A point of the HUD screen (0-1 across and down) in the room, while the board is up.</summary>
    public Vector3? ScreenPoint(Vector2 uv) => Shown ? BoardPoint(Side + uv.X * ScreenW, Top + uv.Y * ScreenH) : null;
    public Vector3? ClosePoint() => Shown ? BoardPoint(CloseKey.MidX, CloseKey.MidY) : null;
    public Vector3? TopStripPoint() => Shown ? BoardPoint(LogicalW * 0.4f, Top / 2) : null;

    /// <summary>The screen's corners in the room (top left, top right, bottom right, bottom left).</summary>
    public Vector3[] ScreenCorners()
    {
        var l = Layers()!.Value.Screen;
        var right = Vector3.Transform(Vector3.UnitX, l.Rot) * (l.Width / 2);
        var up = Vector3.Transform(Vector3.UnitY, l.Rot) * (l.Width / HudView.BoardAspect / 2);
        return new[] { l.Pos - right + up, l.Pos + right + up, l.Pos + right - up, l.Pos - right - up };
    }

    (PanelLook Look, bool Lit, OverlayImage Image)? _chrome;
    long _version;

    OverlayImage Chrome()
    {
        var look = _look();
        if (_chrome is { } c && ReferenceEquals(c.Look, look) && c.Lit == _closeLit) return c.Image;
        var img = DrawChrome(look, _closeLit, Interlocked.Increment(ref _version));
        _chrome = (look, _closeLit, img);
        return img;
    }

    /// <summary>The board's glass and × key in a look; the screen's place (logical pixels) inside it.</summary>
    public static OverlayImage DrawChrome(PanelLook look, bool closeLit, long version, float scale = Raster)
    {
        var ink = Ink.For(look);
        var (pixels, w, h, _) = Rasterise(scale, LogicalW, new List<PanelKey>(), (canvas, height) =>
        {
            if (canvas == null) return LogicalH;
            var draw = new Painter(canvas, ink, LogicalW);
            // the glass a little lighter than the panel's: a board to set HUDs on, the world still through it
            using (var fade = new SKPaint { Color = SKColors.White.WithAlpha(105) })
            {
                canvas.SaveLayer(fade);
                draw.Body(LogicalH);
                canvas.Restore();
            }
            draw.GlyphKey(CloseKey, Painter.Glyph.Close, closeLit);
            return LogicalH;
        });
        return new OverlayImage(pixels, w, h, version);
    }

    /// <summary>The HUD screen's rectangle on the board's picture, in its logical pixels.</summary>
    public static SKRect ScreenRect => new(Side, Top, Side + ScreenW, Top + ScreenH);

    /// <summary>The token at the wrist, the rays' cue on the board (a beam to it, a dot where a press would touch), and,
    /// for checking without a headset, the board's edges and × as lines.</summary>
    public OverlayScene? Decorate(OverlayScene? scene, VrPilot pilot, Func<PanelLook> look, bool preview)
    {
        var beams = new List<OverlayBeam>(scene?.Beams ?? []);
        var dots = new List<OverlayDot>(scene?.Dots ?? []);
        var glyphs = new List<OverlayGlyph>(scene?.Glyphs ?? []);
        bool any = false;
        if (_token is { } tk)
        {
            var l = look();
            float body = Math.Max(l.TintOpacity, 0.88f);
            glyphs.Add(new OverlayGlyph(pilot.RoomToRegion(tk), 0.016f, C(l.Tint, Shown ? body : body * 0.75f), C(Shown ? l.Ink : l.Dim, Shown ? 1 : 0.9f),
                OverlayGlyphKind.Hud, _tokenLit ? 0.6f : 0));
            any = true;
        }
        foreach (var (aim, from, hand) in new[] { (_right, _rightFrom, 1), (_left, _leftFrom, 0) })
        {
            if (aim.Spot == Spot.None || from is not { } o) continue;
            var a = pilot.RoomToRegion(o);
            var b = pilot.RoomToRegion(aim.Point);
            beams.Add(new OverlayBeam(a + Vector3.Normalize(b - a) * 0.04f, b, 0.003f, Cue with { W = 0.55f }));
            float r = Math.Clamp(0.006f * aim.T, 0.008f, 0.05f);
            if (aim.Hit is { } hit && _view.Touchable(hit)) dots.Add(new OverlayDot(b, r, Cue));
            // a still hold on a HUD face's page: the ring toward up close
            if (_mediaHand == hand && _view.Media?.Filling(hand) is { } f) Sojourn.Client.Media.MediaInput.Fill(beams, b, a, r * 2.6f, f);
            any = true;
        }
        if (preview && Shown)
        {
            var k = ScreenCorners();
            var line = Cue with { W = 0.5f };
            for (int i = 0; i < 4; i++) beams.Add(new OverlayBeam(pilot.RoomToRegion(k[i]), pilot.RoomToRegion(k[(i + 1) % 4]), 0.002f, line));
            any = true;
        }
        if (!any) return scene;
        return (scene ?? new OverlayScene()) with { Beams = beams, Dots = dots, Glyphs = glyphs };
    }

    Vector3? _rightFrom, _leftFrom;

    static Vector4 Cue => PanelLook.InUse.Light(0.9f);

    static Vector4 C(string hex, float a)
    {
        var k = SKColor.TryParse(hex, out var v) ? v : SKColors.Gray;
        return new(k.Red / 255f, k.Green / 255f, k.Blue / 255f, a);
    }
}
