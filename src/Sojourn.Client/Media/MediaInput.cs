using LibreMetaverse;
using Silk.NET.Input;
using Sojourn.Client.Control;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Media;

/// <summary>
/// The person's own hands on media in the world (the left trigger in the headset, the mouse on the desktop), before
/// the world's touch (Control/DirectTouch): media that isn't playing starts on a press (local, always allowed); a page
/// the person may interact with takes the press as a click where it lands, a video toggles pausing; a face they may not
/// interact with is touched as any other. In the headset the press follows phone norms: a tap clicks, a drag scrolls (the
/// page follows the ray), a hold that then moves presses and drags (selecting, sliders), a hold kept still (a ring fills)
/// or a double tap shows it up close (PageView; up close a hold presses at once, as before). On the
/// desktop the mouse is the page's: hover, click, drag, the wheel scrolls it, and typing goes to a text field the page has
/// focused (Esc gives the keys back); Z shows what's under the pointer up close. Main thread.
/// Written by Claude Opus 5.5, 2026-10-26.
/// </summary>
public sealed class MediaInput
{
    readonly MediaManager _m;
    readonly AgentSession _s;
    readonly FrameRenderer _r;

    public MediaInput(MediaManager m, AgentSession s, FrameRenderer r)
    {
        _m = m;
        _s = s;
        _r = r;
    }

    /// <summary>Media under a hit: the item, where on its page (content pixels, null off the page or not loaded), and
    /// what a press would do.</summary>
    public readonly record struct Spot(MediaItem Item, Vector2? Px, bool Playing, bool Interact, bool Video);

    public Spot? At(PickHit? hit)
    {
        if (hit is not { Kind: PickKind.Object, Surface: { } surf } h) return null;
        return _m.Locked(() =>
        {
            var item = _m.Item(MediaManager.PrimKey(h.Region, h.LocalId, surf.Face));
            if (item == null && _m.Item("parcel") is { Texture: var tex } parcel && tex != UUID.Zero
                && _s.World.Region(_s.GridKey, h.Region)?.Prims.TryGetValue(h.LocalId, out var prim) == true
                && prim.Textures?.GetFace((uint)surf.Face)?.TextureID == tex)
                item = parcel;
            if (item == null) return (Spot?)null;
            var p = item.Player is { Closed: false } pl ? pl : null;
            Vector2? px = p?.Surface.ContentAt(surf.UV.X, surf.UV.Y) is { } c ? new Vector2(c.X, c.Y) : null;
            return new Spot(item, px, p != null, p?.Kind == MediaKind.Web && _m.May(item, control: false), p?.Kind == MediaKind.Video);
        });
    }

    /// <summary>Whether anything with media is about at all (the headset's extra pick is skipped otherwise).</summary>
    public bool Any => _m.Locked(() => _m.ItemsUnlocked.Count > 0);

    // ---- a press on a page, as phones read it (the headset's rays, here and on the page board) -----------------------

    /// <summary>SOJOURN_MEDIA_DEBUG=1: each gesture's steps in the log (tests, the headset's first sessions).</summary>
    public static Action<string>? Debug;

    /// <summary>One hand's press on a page. opens: a still hold brings it up close (out in the world and on the HUD
    /// board; not on the page board, where it's already up close and a still hold presses as ever).</summary>
    public sealed class Gesture(MediaManager m, bool opens = true)
    {
        // Hold: held still past HoldMs where a still hold opens; nothing has gone to the page yet. Moving makes it a press
        // and drag (the press sent where it began), staying still to OpenMs brings it up close, letting go is a click.
        enum Mode { None, Pending, Scroll, Hold, Press, Opened }
        Mode _mode;
        MediaItem? _item;
        Vector2 _downPx, _lastPx;
        Vector3 _downDir;
        long _downAt, _tapAt;
        Vector2 _tapPx;
        MediaItem? _tapItem;
        Vector2 _scrollRest;
        const float MoveDeg = 1.6f;
        const int HoldMs = 450, OpenMs = 900, DoubleMs = 380;

        public bool Active => _mode != Mode.None;
        public MediaItem? Item => _item;
        public bool Pressing => _mode == Mode.Press;

        /// <summary>While a still hold is on its way to opening up close: how far (0-1), for the cue's ring.</summary>
        public float? Filling => _mode == Mode.Hold ? Math.Clamp((Environment.TickCount64 - _downAt - HoldMs) / (float)(OpenMs - HoldMs), 0, 1) : null;

        public void Down(MediaItem item, Vector2 px, Vector3 dir)
        {
            (_mode, _item, _downPx, _lastPx, _downDir, _downAt) = (Mode.Pending, item, px, px, Vector3.Normalize(dir), Environment.TickCount64);
            Debug?.Invoke($"media gesture: down on {item.Key} at {px.X:0},{px.Y:0}");
            _scrollRest = default;
            m.Locked(() => { m.UserActed(item); });
        }

        /// <summary>Each frame while held: px where the ray is on the page now (null off it). True once, when a still hold
        /// has lasted to OpenMs (the caller brings the item up close; the rest of the press does nothing).</summary>
        public bool Held(Vector2? px, Vector3 dir)
        {
            if (_item == null || _mode == Mode.Opened) return false;
            long now = Environment.TickCount64;
            float deg = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(dir), _downDir), -1, 1)) * 180 / MathF.PI;
            var p = Player();
            if (_mode is Mode.Pending or Mode.Hold)
            {
                // a press and drag is a click: only on a page that takes clicks now (focused, or its first click let through)
                var item = _item;
                bool Takes() => m.Locked(() => m.TakesClick(item));
                if (deg > MoveDeg)
                {
                    if (_mode == Mode.Pending || !Takes()) { _mode = Mode.Scroll; _lastPx = px ?? _lastPx; Debug?.Invoke($"media gesture: scrolling ({deg:0.0} deg)"); }
                    else
                    {
                        // held, then moved: the press goes in where it began, then follows the ray
                        Press(p);
                        if (px is { } to) { p?.Mouse("move", to.X, to.Y, held: true); _lastPx = to; }
                        m.Locked(() => m.Focus(_item, 3000));
                    }
                }
                else if (opens && now - _downAt >= OpenMs)
                {
                    _mode = Mode.Opened;
                    Debug?.Invoke("media gesture: held still: up close");
                    return true;
                }
                else if (_mode == Mode.Pending && now - _downAt >= HoldMs)
                {
                    if (opens) { _mode = Mode.Hold; Debug?.Invoke("media gesture: held"); }
                    else if (Takes()) Press(p);
                }
                return false;
            }
            if (px is not { } at) return false;
            if (_mode == Mode.Scroll)
            {
                // the page follows the ray: dragging it down shows what's above (a positive wheel delta scrolls up)
                var d = at - _lastPx + _scrollRest;
                int dx = (int)d.X, dy = (int)d.Y;
                _scrollRest = d - new Vector2(dx, dy);
                if (dx != 0 || dy != 0) p?.Wheel(_downPx.X, _downPx.Y, dx, dy);
                _lastPx = at;
            }
            else if (_mode == Mode.Press && at != _lastPx)
            {
                p?.Mouse("move", at.X, at.Y, held: true);
                _lastPx = at;
            }
            m.Locked(() => m.Focus(_item, 3000));
            return false;
        }

        void Press(MediaPlayer? p)
        {
            _mode = Mode.Press;
            Debug?.Invoke("media gesture: held: pressing");
            p?.Mouse("move", _downPx.X, _downPx.Y);
            p?.Mouse("down", _downPx.X, _downPx.Y);
        }

        /// <summary>The trigger came up: a tap clicks (a video's pauses or plays), or on a face not taking clicks yet only
        /// focuses it (the viewer's first click); a still hold let go before it opened is a tap too. True for a double tap,
        /// or a focusing tap on a face that asks to be zoomed to (the caller shows it up close).</summary>
        public bool Up(Vector2? px)
        {
            if (_item == null) { _mode = Mode.None; return false; }
            var p = Player();
            Debug?.Invoke($"media gesture: up ({_mode}{(p == null ? ", no player" : "")})");
            bool doubled = false;
            long now = Environment.TickCount64;
            switch (_mode)
            {
                case Mode.Pending or Mode.Hold:
                    if (_mode == Mode.Hold) _tapItem = null;   // a slow press doesn't begin a double tap
                    else if (_tapItem == _item && now - _tapAt < DoubleMs && Vector2.Distance(_tapPx, _downPx) < 40) { doubled = true; _tapItem = null; break; }
                    else (_tapItem, _tapAt, _tapPx) = (_item, now, _downPx);
                    var item = _item;
                    var (takes, zoom) = m.Locked(() => { bool was = m.IsFocused(item), t = m.TakesClick(item); return (t, !was && m.SetFocus(item)); });
                    if (zoom) doubled = true;
                    if (!takes) { Debug?.Invoke("media gesture: focused (its first click isn't let through)"); break; }
                    if (p?.Kind == MediaKind.Video) { if (p.State == "playing") p.Pause(); else p.Play(); }
                    else
                    {
                        p?.Mouse("move", _downPx.X, _downPx.Y);
                        p?.Mouse("down", _downPx.X, _downPx.Y);
                        p?.Mouse("up", _downPx.X, _downPx.Y);
                    }
                    break;
                case Mode.Press:
                    var at = px ?? _lastPx;
                    p?.Mouse("up", at.X, at.Y);
                    break;
            }
            _mode = Mode.None;
            _item = null;
            return doubled;
        }

        public void Cancel()
        {
            if (_mode == Mode.Press) Player()?.Mouse("up", _lastPx.X, _lastPx.Y);
            _mode = Mode.None;
            _item = null;
        }

        MediaPlayer? Player() => _item?.Player is { Closed: false } p ? p : null;
    }

    // ---- media on HUD faces, pressed on the headset's HUD board (HudView.IMedia) ----------------------------------------

    readonly Gesture?[] _hud = new Gesture?[2];

    /// <summary>A board press on a HUD hit: media to start starts; on a page or video a gesture begins (a tap clicks or
    /// pauses, a drag scrolls, a hold presses and drags), as on media in the world. False: not media's.</summary>
    public bool HudDown(int hand, PickHit hit, Vector3 dir)
    {
        if (At(hit) is not { } s || s.Playing && !s.Interact && !s.Video) return false;
        if (!s.Playing) { _m.Locked(() => { _m.Start(s.Item); _m.SetFocus(s.Item); }); return true; }
        if (s.Px is not { } px) return true;   // its page isn't there yet: the press is still its, not the HUD's
        (_hud[hand] ??= new Gesture(_m)).Down(s.Item, px, dir);
        return true;
    }

    /// <summary>Each frame while held: the item when a still hold has just lasted long enough (the caller shows it up close).</summary>
    public MediaItem? HudHeld(int hand, PickHit? hit, Vector3 dir)
    {
        if (_hud[hand] is not { Active: true } g) return null;
        var item = g.Item;
        return g.Held(At(hit) is { } sp && sp.Item == item ? sp.Px : null, dir) ? item : null;
    }

    /// <summary>How far a still hold on a HUD face is toward up close (the HUD board's ring), if one is.</summary>
    public float? HudFilling(int hand) => _hud[hand]?.Filling;

    /// <summary>The trigger came up: the item when it was a double tap (the caller shows it up close).</summary>
    public MediaItem? HudUp(int hand, PickHit? hit)
    {
        if (_hud[hand] is not { Active: true } g) return null;
        var item = g.Item;
        return g.Up(At(hit) is { } sp && sp.Item == item ? sp.Px : null) ? item : null;
    }

    public void HudCancel(int hand) => _hud[hand]?.Cancel();

    public bool HudOn(PickHit? hit) => At(hit) is { } s && (!s.Playing || s.Interact || s.Video);

    // ---- the right stick on a page (Control/SheetStick) -----------------------------------------------------------------

    Vector2 _stickRest;

    /// <summary>A page the stick can scroll, as a sheet: playing, one the person may use, the ray on its page.</summary>
    public StickSheet? SheetAt(Spot? spot) =>
        spot is { Playing: true, Interact: true, Video: false, Px: { } px } s ? new StickSheet(s.Item, Scroll: (down, right) => StickWheel(s.Item, px, down, right)) : null;

    /// <summary>The right stick's scroll on a page, at a point on it (content px), in fractions of the page's own size
    /// (positive: further down, further right): the wheel's deltas, which scroll up for positive.</summary>
    public void StickWheel(MediaItem item, Vector2 px, float down, float right)
    {
        if (item.Player is not { Closed: false, Kind: MediaKind.Web } p) return;
        var d = _stickRest + new Vector2(-right * Math.Max(1, p.Surface.ContentWidth), -down * Math.Max(1, p.Surface.ContentHeight));
        int dx = (int)d.X, dy = (int)d.Y;
        _stickRest = d - new Vector2(dx, dy);
        if (dx == 0 && dy == 0) return;
        p.Wheel(px.X, px.Y, dx, dy);
        _m.Locked(() => _m.Focus(item, 3000));
    }

    // ---- the headset's left hand on media in the world -----------------------------------------------------------------

    Gesture? _leftGesture;
    bool _armed = true, _swallow;
    Spot? _leftSpot;
    (Vector3 From, Vector3 To)? _leftRay;

    /// <summary>This frame the left hand is on media (the world's touch waits).</summary>
    public bool LeftOnMedia { get; private set; }

    /// <summary>The media that hand is on this frame, if any (the right stick scrolls a page there).</summary>
    public Spot? HandSpot => _leftSpot;

    /// <summary>The left ray and trigger this frame (before DirectTouch). blocked: a confirmation or the menu is up, or
    /// another board has the hand. zoom: called with a double-tapped item.</summary>
    public void HeadsetLeft(XrControllers input, VrPilot pilot, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel, bool blocked, bool focused, Action<MediaItem> zoom)
    {
        _leftGesture ??= new Gesture(_m);
        var g = _leftGesture;
        LeftOnMedia = false;
        _leftSpot = null;
        _leftRay = null;
        if (!focused) { _armed = false; g.Cancel(); _swallow = false; return; }
        if (input.LeftAim is not { } q || input.LeftAimPos is not { } at || pilot.Region == 0 || _s.Client is not { } c) { g.Cancel(); return; }
        float trigger = input.LeftTrigger;
        if (trigger < 0.35f) { _armed = true; _swallow = false; }
        if (!g.Active && !Any) return;
        _cueRegion = pilot.Region;
        var dirXr = Vector3.Transform(-Vector3.UnitZ, q);
        bool onPanel = panel is { } pl && OnPlane(at, dirXr, pl);
        var origin = pilot.RoomToRegion(at);
        var dir = pilot.RoomDirToRegion(dirXr);
        var hit = onPanel ? null : _r.Pick(pilot.Region, origin, dir, 64, c.Self.LocalID);
        var spot = At(hit);
        if (g.Active)
        {
            var px = spot is { } sp && sp.Item == g.Item ? sp.Px : null;
            var item = g.Item;
            if (trigger < 0.35f) { if (g.Up(px) && item != null) zoom(item); }
            else if (g.Held(px, dir) && item != null) zoom(item);
            LeftOnMedia = true;
        }
        else if (spot is { } s && (!s.Playing || s.Interact || s.Video) && !blocked)
        {
            LeftOnMedia = true;
            if (trigger > 0.6f && _armed)
            {
                _armed = false;
                if (!s.Playing)
                {
                    // started, and focused (the viewer's first click on media that isn't loaded); up close if it asks
                    if (_m.Locked(() => { _m.Start(s.Item); return _m.SetFocus(s.Item); })) zoom(s.Item);
                    _swallow = true;
                }
                else if (s.Px is { } px) g.Down(s.Item, px, dir);
            }
        }
        // a press in the world off media takes the focus away (the viewer's clearFocus)
        else if (trigger > 0.6f && _armed && spot is null && !blocked) _m.Locked(_m.ClearFocus);
        if (_swallow) LeftOnMedia = true;
        if (LeftOnMedia && hit is { } h)
        {
            _leftSpot = spot;
            _leftRay = (origin, h.Point);
        }
    }

    static bool OnPlane(Vector3 from, Vector3 dir, (Vector3 Pos, Quaternion Rot, Vector2 Size) pl)
    {
        var n = Vector3.Transform(Vector3.UnitZ, pl.Rot);
        float denom = Vector3.Dot(dir, n);
        if (denom > -1e-4f) return false;
        float t = Vector3.Dot(pl.Pos - from, n) / denom;
        if (t <= 0) return false;
        var p = from + dir * t - pl.Pos;
        return MathF.Abs(Vector3.Dot(p, Vector3.Transform(Vector3.UnitX, pl.Rot))) <= pl.Size.X / 2
            && MathF.Abs(Vector3.Dot(p, Vector3.Transform(Vector3.UnitY, pl.Rot))) <= pl.Size.Y / 2;
    }

    static Vector4 Ink => Sojourn.Session.Assistant.PanelLook.InUse.Light(0.9f);

    ulong _cueRegion;

    /// <summary>The focused face's outline (the viewer's focus ring), faint, while it isn't up close: its clicks go in.</summary>
    void FocusRing(List<OverlayBeam> beams, ulong region, Vector3 eye)
    {
        var f = _m.Locked(() => _m.Focused is { Kind: MediaItemKind.Prim, Zoomed: false, Frame: { } fr } i && i.Region == region && i.Playing ? fr : (FaceFrame?)null);
        if (f is not { } fr) return;
        var c = fr.Centre + fr.Normal * 0.005f;
        var r = fr.Right * (fr.Width / 2);
        var u = fr.Up * (fr.Height / 2);
        float w = Math.Clamp(0.0012f * Vector3.Distance(eye, c), 0.0015f, 0.02f);
        var ink = Ink with { W = 0.45f };
        Vector3[] k = [c - r - u, c + r - u, c + r + u, c - r + u];
        for (int n = 0; n < 4; n++) beams.Add(new OverlayBeam(k[n], k[(n + 1) % 4], w, ink));
    }

    /// <summary>The left hand's cue on media: a faint beam ending in a dot (a press goes into it), or a small play
    /// triangle (a press starts it); and the focused face's outline (eye: the head, region-local).</summary>
    public OverlayScene? HeadsetCue(OverlayScene? scene, Vector3 eye)
    {
        if (_cueRegion != 0 && _m.Locked(() => _m.Focused != null))
        {
            var ringed = new List<OverlayBeam>(scene?.Beams ?? []);
            FocusRing(ringed, _cueRegion, eye);
            scene = (scene ?? new OverlayScene()) with { Beams = ringed };
        }
        if (_leftRay is not { } ray) return scene;
        var beams = new List<OverlayBeam>(scene?.Beams ?? []) { new(ray.From + Vector3.Normalize(ray.To - ray.From) * 0.04f, ray.To, 0.003f, Ink with { W = 0.35f }) };
        var dots = new List<OverlayDot>(scene?.Dots ?? []);
        float d = Vector3.Distance(ray.From, ray.To);
        float r = Math.Clamp(0.006f * d, 0.008f, 0.18f);
        if (_leftSpot is { Playing: false }) Play(beams, ray.To, ray.From, r * 2.2f);
        else dots.Add(new OverlayDot(ray.To, _leftGesture is { Active: true } ? r * 1.35f : r, Ink));
        if (_leftGesture?.Filling is { } f) Fill(beams, ray.To, ray.From, r * 2.6f, f);
        return (scene ?? new OverlayScene()) with { Beams = beams, Dots = dots };
    }

    /// <summary>A still hold on its way to up close: a ring round the dot facing the eye, drawn clockwise from the top
    /// as far as it's got (it opens when the ring closes).</summary>
    public static void Fill(List<OverlayBeam> beams, Vector3 at, Vector3 eye, float radius, float f)
    {
        if (f <= 0) return;
        var toEye = Vector3.Normalize(eye - at);
        var right = Vector3.Normalize(Vector3.Cross(MathF.Abs(toEye.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX, toEye));
        var up = Vector3.Cross(toEye, right);
        int n = Math.Max(1, (int)MathF.Ceiling(f * 40));
        float end = f * MathF.Tau, w = radius * 0.14f;   // the play triangle's stroke
        Vector3 P(float a) => at + (right * MathF.Sin(a) + up * MathF.Cos(a)) * radius;
        for (int i = 0; i < n; i++) beams.Add(new OverlayBeam(P(end * i / n), P(end * (i + 1) / n), w, Ink));
    }

    /// <summary>A play triangle facing the eye, as lines.</summary>
    static void Play(List<OverlayBeam> beams, Vector3 at, Vector3 eye, float size)
    {
        var toEye = Vector3.Normalize(eye - at);
        var right = Vector3.Normalize(Vector3.Cross(MathF.Abs(toEye.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX, toEye));
        var up = Vector3.Cross(toEye, right);
        var p0 = at - right * size * 0.45f + up * size * 0.6f;
        var p1 = at + right * size * 0.6f;
        var p2 = at - right * size * 0.45f - up * size * 0.6f;
        float w = size * 0.16f;
        beams.Add(new OverlayBeam(p0, p1, w, Ink));
        beams.Add(new OverlayBeam(p1, p2, w, Ink));
        beams.Add(new OverlayBeam(p2, p0, w, Ink));
    }

    // ---- the desktop ---------------------------------------------------------------------------------------------------

    MediaItem? _deskPress, _hover, _keys;
    Vector2 _deskLast;
    long _lastMove, _lastClick;
    Vector2 _lastClickPx;
    int _clicks;
    Spot? _deskSpot;

    /// <summary>The pointer is on media a click would act on (the world's touch and its cue wait).</summary>
    public bool DesktopOver => _deskSpot is { } s && (!s.Playing || s.Interact || s.Video) || _deskPress != null;
    /// <summary>A page has the keys: a text field is focused in the one last clicked.</summary>
    public bool TypingInPage => _keys?.Player is { Closed: false, WantsKeys: true };
    public MediaItem? UnderPointer => _deskSpot?.Item;
    /// <summary>The page that gets typing while it has a text field focused (the page view's clicks set it too).</summary>
    public void SetKeys(MediaItem? i) => _keys = i;

    /// <summary>Each desktop frame: hover moves into the page under the pointer, a held press drags.</summary>
    public void DesktopFrame(PickHit? hit, bool blocked)
    {
        _deskSpot = blocked ? null : At(hit);
        if (_keys != null && _keys.Player is not { Closed: false }) _keys = null;
        long now = Environment.TickCount64;
        if (_deskPress is { Player: { Closed: false } pp })
        {
            if (_deskSpot is { Px: { } px } s && s.Item == _deskPress && px != _deskLast && now - _lastMove >= 16)
            {
                pp.Mouse("move", px.X, px.Y, held: true);
                (_deskLast, _lastMove) = (px, now);
            }
            return;
        }
        // hover goes into a page that takes clicks now (LLToolPie::handleMediaHover: focused, or its first click let through)
        var over = _deskSpot is { Interact: true, Px: not null } o && _m.Locked(() => _m.TakesClick(o.Item)) ? o.Item : null;
        if (_hover != null && _hover != over) _hover.Player?.Mouse("leave", _deskLast.X, _deskLast.Y);
        _hover = over;
        if (over != null && _deskSpot!.Value.Px is { } hp && hp != _deskLast && now - _lastMove >= 33)
        {
            over.Player!.Mouse("move", hp.X, hp.Y);
            (_deskLast, _lastMove) = (hp, now);
        }
    }

    /// <summary>Brings an item up close (MediaWiring: the page view), for a face that asks to be zoomed to.</summary>
    public Action<MediaItem>? Zoom { get; set; }

    /// <summary>A left click: true if media took it. As LLToolPie::handleMediaClick: a face not focused is focused first
    /// (and comes up close if it asks), and only takes the click itself if its first click is let through.</summary>
    public bool DesktopDown()
    {
        Debug?.Invoke($"media click: {(_deskSpot is { } ds ? $"{ds.Item.Key} at {ds.Px}, playing {ds.Playing}, interact {ds.Interact}" : "not on media")}");
        if (_deskSpot is not { } s) { _keys = null; _m.Locked(_m.ClearFocus); return false; }
        if (!s.Playing)
        {
            if (_m.Locked(() => { _m.Start(s.Item); return _m.SetFocus(s.Item); })) Zoom?.Invoke(s.Item);
            return true;
        }
        if (s.Video || s.Interact)
        {
            var (takes, zoom) = _m.Locked(() => { bool was = _m.IsFocused(s.Item), t = _m.TakesClick(s.Item); return (t, !was && _m.SetFocus(s.Item)); });
            if (zoom) Zoom?.Invoke(s.Item);
            if (!takes) { Debug?.Invoke($"media click: focused {s.Item.Key}{(zoom ? ", up close" : "")} (its first click isn't let through)"); return true; }
        }
        if (s.Video)
        {
            var p = s.Item.Player!;
            if (p.State == "playing") p.Pause(); else p.Play();
            return true;
        }
        if (!s.Interact || s.Px is not { } px) { _keys = null; return false; }
        long now = Environment.TickCount64;
        _clicks = now - _lastClick < 450 && Vector2.Distance(px, _lastClickPx) < 6 ? Math.Min(3, _clicks + 1) : 1;
        (_lastClick, _lastClickPx) = (now, px);
        _deskPress = s.Item;
        _deskLast = px;
        _keys = s.Item;
        _m.Locked(() => _m.UserActed(s.Item));
        s.Item.Player!.Mouse("down", px.X, px.Y, clicks: _clicks);
        return true;
    }

    public bool DesktopUp()
    {
        if (_deskPress is not { } i) return false;
        _deskPress = null;
        var at = _deskSpot is { Px: { } px } s && s.Item == i ? px : _deskLast;
        i.Player?.Mouse("up", at.X, at.Y, clicks: _clicks);
        return true;
    }

    /// <summary>The wheel on a page scrolls it (a notch is 100 pixels), not the camera.</summary>
    public bool DesktopWheel(float notches)
    {
        if (_deskSpot is not { Interact: true, Px: { } px } s) return false;
        s.Item.Player!.Wheel(px.X, px.Y, 0, (int)(notches * 100));
        return true;
    }

    /// <summary>A key while a page has a text field focused: true if it went to the page. Esc takes the keys back.</summary>
    public bool Key(Key key, bool down, bool shift, bool ctrl, bool alt)
    {
        if (!TypingInPage) return false;
        var p = _keys!.Player!;
        if (key == Silk.NET.Input.Key.Escape) { if (down) { p.Focus(false); _keys = null; } return true; }
        int mods = (shift ? 1 : 0) | (ctrl ? 2 : 0) | (alt ? 4 : 0);
        // the function keys stay the client's (the menu, a screenshot, stats)
        if (key is >= Silk.NET.Input.Key.F1 and <= Silk.NET.Input.Key.F25) return false;
        int vk = Vk(key);
        // printable keys arrive as characters (KeyChar); here the keys that aren't, and shortcuts
        bool letter = key is >= Silk.NET.Input.Key.A and <= Silk.NET.Input.Key.Z;
        if (vk == 0 || (letter && !ctrl && !alt) || (key is >= Silk.NET.Input.Key.Number0 and <= Silk.NET.Input.Key.Number9 && !ctrl) || key == Silk.NET.Input.Key.Space)
            return true;
        p.Key(down ? "down" : "up", vk, 0, mods);
        if (down && key is Silk.NET.Input.Key.Enter or Silk.NET.Input.Key.KeypadEnter) p.Key("char", 13, 13, mods);
        if (down && key == Silk.NET.Input.Key.Tab) p.Key("char", 9, 9, mods);
        if (down && key == Silk.NET.Input.Key.Backspace) p.Key("char", 8, 8, mods);
        return true;
    }

    public bool Char(char ch)
    {
        if (!TypingInPage || ch < 32) return TypingInPage;
        _keys!.Player!.Key("char", ch, ch);
        return true;
    }

    static int Vk(Key k) => k switch
    {
        >= Silk.NET.Input.Key.A and <= Silk.NET.Input.Key.Z => 'A' + (k - Silk.NET.Input.Key.A),
        >= Silk.NET.Input.Key.Number0 and <= Silk.NET.Input.Key.Number9 => '0' + (k - Silk.NET.Input.Key.Number0),
        Silk.NET.Input.Key.Backspace => 8, Silk.NET.Input.Key.Tab => 9, Silk.NET.Input.Key.Enter or Silk.NET.Input.Key.KeypadEnter => 13,
        Silk.NET.Input.Key.Space => 32, Silk.NET.Input.Key.PageUp => 33, Silk.NET.Input.Key.PageDown => 34, Silk.NET.Input.Key.End => 35,
        Silk.NET.Input.Key.Home => 36, Silk.NET.Input.Key.Left => 37, Silk.NET.Input.Key.Up => 38, Silk.NET.Input.Key.Right => 39,
        Silk.NET.Input.Key.Down => 40, Silk.NET.Input.Key.Delete => 46, Silk.NET.Input.Key.Insert => 45,
        _ => 0,
    };

    /// <summary>The desktop's cue: a play triangle on media a click would start; the focused face's outline.</summary>
    public OverlayScene? DesktopCue(OverlayScene? scene, Vector3 eye, PickHit? hit, ulong region = 0)
    {
        if (region != 0 && _m.Locked(() => _m.Focused != null))
        {
            var ringed = new List<OverlayBeam>(scene?.Beams ?? []);
            FocusRing(ringed, region, eye);
            scene = (scene ?? new OverlayScene()) with { Beams = ringed };
        }
        if (_deskSpot is not { Playing: false } || hit is not { } h) return scene;
        var beams = new List<OverlayBeam>(scene?.Beams ?? []);
        Play(beams, h.Point, eye, Math.Clamp(0.02f * h.Distance, 0.03f, 0.6f));
        return (scene ?? new OverlayScene()) with { Beams = beams };
    }
}
