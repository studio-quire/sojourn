using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Map;

/// <summary>
/// A map board, one of two (the user, 2026-10-29: the world map and the mini-map apart, each keeping its own state):
/// - The world map (<see cref="MapDock.World"/>): the viewer's world map (llworldmapview.cpp) with a continuous zoom down
///   to the mini-map's scale, opened from the mini-map's globe key or by the assistant, and keeping whatever it showed
///   (place, zoom, what's picked and its Go) between openings. In the headset a sheet like the HUD board
///   (Hud/HudBoard.cs): its own quad layer in the panel's look, 1.25 m away, middle a little above the eyes, following
///   the head lazily (carried past 40 degrees) until the right trigger on its edge puts it somewhere (PanelPlacement,
///   MenuPlaces).
/// - The mini-map (llnetmap.cpp): smaller, following you, opened from the location line. In the headset either held on
///   the left hand above the line (<see cref="MapDock.Hand"/>: the wiring places it each frame, and it shows while the line
///   does), or out as a board of its own (<see cref="MapDock.Free"/>: placed and followed as the world map, at its own
///   resting place); its keys move it between the two.
/// On the map the trigger held and moved drags the map; a press that doesn't move picks the spot (the nearest mark within
/// reach, else the place), and Go teleports there (the person's own press, as the viewer's map's teleport; nothing waits
/// for a confirmation). On the desktop the mouse does the same; the wheel zooms. North up on both.
/// Main thread, but for the picture, drawn on a worker whenever what it shows changes.
/// </summary>
public sealed class MapBoard : IDisposable
{
    const float MinMpp = 0.125f, MaxMpp = 512f;

    readonly AgentSession _s;
    readonly WorldMap _map;
    readonly MapTiles _tiles;
    readonly Func<PanelLook> _look;
    readonly Action<string> _log;
    readonly string _name;
    readonly bool _isWorld;
    readonly float _distance;

    /// <summary>A board: which (the world map, or the mini-map where it starts), its name in the console, where it rests
    /// while it follows the head and how far away, and the scale it first opens at (metres a logical pixel).</summary>
    public MapBoard(AgentSession s, WorldMap map, MapTiles tiles, Func<PanelLook> look, Action<string> log, MapDock dock, string name,
        PanelPlacement.RestPose rest, float distance, float openMpp)
    {
        _s = s;
        _map = map;
        _tiles = tiles;
        _look = look;
        _log = log;
        _name = name;
        _isWorld = dock == MapDock.World;
        _distance = distance;
        _view = new(0, 0, openMpp, true, false, true, false, -1, null, dock);
        // (the world map's place kept under the name the one board had, so where the person put it stays)
        Placement = new(_isWorld ? "map" : "mini map", rest, rangeDeg: 40);
        tiles.Arrived += () => _dirty = true;
        map.Changed += () => _dirty = true;
    }

    MapView _view;
    public MapView View => _view;
    public bool Shown { get; private set; }
    MapLayout L => MapLayout.Of(_view);

    /// <summary>On the hand: the location line's width (logical px), which it takes (MapLayout.Hand).</summary>
    public float HandWidth
    {
        get => _view.Width;
        set { if (value != _view.Width) _view = _view with { Width = value }; }
    }

    /// <summary>Where the mini-map is (on the hand, out, on the desktop); the world map's is always World.</summary>
    public MapDock Dock
    {
        get => _view.Dock;
        set
        {
            if (_isWorld || value == MapDock.World || value == _view.Dock) return;
            if (_view.Dock == MapDock.Hand && value == MapDock.Free) _place = true;   // out: at its resting place
            _view = _view with { Dock = value, Hover = -1 };
            _dirty = true;
        }
    }

    /// <summary>The line under the map with nothing picked (statistics at a glance; the world map's).</summary>
    public Func<string?>? Line { get; set; }

    /// <summary>The tracking beacon, when there's one to drive (track a place: region handle, its local point, a
    /// name; null clears it), and whether it's on.</summary>
    public Action<ulong, System.Numerics.Vector3, string>? Track { get; set; }
    public Action<LibreMetaverse.UUID, string?>? TrackAvatar { get; set; }
    public Action? Untrack { get; set; }
    public Func<bool>? Tracking { get; set; }

    /// <summary>Teleports go through here (the session's TeleportAsync), for a test to watch.</summary>
    public Func<string, LibreMetaverse.Vector3, Task<(bool Ok, string Reason)>>? Teleport { get; set; }

    /// <summary>The mini-map's keys: the world map (and whether it's up, for the key's light), out to a board of its own,
    /// back on the hand, and × (out of sight until the line is pressed again). Without Closed, × puts the board away.</summary>
    public Action? OpenWorld { get; set; }
    public Func<bool>? WorldUp { get; set; }
    public Action? PopOut { get; set; }
    public Action? PutBack { get; set; }
    public Action? Closed { get; set; }

    /// <summary>Up or put away. The world map comes back as it was; the mini-map comes back following you, what was
    /// picked on it let go.</summary>
    public void Show(bool on)
    {
        if (on && !Shown)
        {
            _place = true;
            if (!_isWorld) _view = _view with { Follow = true, Picked = null };
        }
        if (on != Shown) _log(on ? $"map: {_name} up" : $"map: {_name} put away");
        Shown = on;
        _dirty = true;
    }

    public void Toggle() => Show(!Shown);

    /// <summary>Something it shows changed elsewhere (the tracker, the world map up or away): redraw.</summary>
    public void Redraw() => _dirty = true;

    /// <summary>Look somewhere (global metres) at a scale, as the assistant asks ("show me Bay City on the map"), the
    /// place there picked or (null) what was picked before kept.</summary>
    public void LookAt(double x, double y, float? mpp, MapPick? pick)
    {
        _view = _view with { CX = x, CY = y, Follow = false, Mpp = Math.Clamp(mpp ?? _view.Mpp, MinMpp, MaxMpp), Picked = pick ?? _view.Picked, Help = false };
        Show(true);
    }

    /// <summary>Follow you again, something there picked or not (the assistant's show without a place).</summary>
    public void FollowMe(bool pick)
    {
        var me = _map.Me();
        _view = _view with
        {
            Follow = true, CX = me?.X ?? _view.CX, CY = me?.Y ?? _view.CY,
            Picked = pick && me is { } m ? new MapPick(m.X, m.Y, _map.RegionAt(m.X, m.Y), null) : _view.Picked,
        };
        _dirty = true;
    }

    public void SetLayers(bool? sale, bool? events)
    {
        _view = _view with { Sale = sale ?? _view.Sale, Events = events ?? _view.Events };
        _dirty = true;
    }

    // --- the picture ----------------------------------------------------------------------------------------------------

    volatile bool _dirty = true;
    int _drawing;
    long _version;
    float _scale = 1.5f;
    bool _layer;
    string _drawnKey = "";
    long _drawnAt;
    OverlayImage? _image;

    /// <summary>The newest picture (null until the first is drawn, or while it's put away). The mini-map's last one is
    /// kept while it's out of sight, so it's there the moment the wrist is raised (and redrawn then).</summary>
    public OverlayImage? Image => Shown ? _image : null;
    public float ImageScale { get; private set; } = 1;

    /// <summary>Each frame while shown: follow the agent, ask for what the view lacks, and start a redraw when the
    /// picture would change (one at a time; changes meanwhile wait for the next).</summary>
    void Refresh(float scale, bool layer)
    {
        var me = _map.Me();
        if (_view.Follow && me is { } m) _view = _view with { CX = m.X, CY = m.Y };
        var r = L.MapRect;
        var (w, n) = MapPainter.ToGlobal(_view, r.Left, r.Top);
        var (e, s) = MapPainter.ToGlobal(_view, r.Right, r.Bottom);
        _map.Want(w, s, e, n, people: _view.Mpp <= 8);
        bool worldUp = WorldUp?.Invoke() == true;
        string meKey = me is { } q ? $"{Math.Round(q.X / _view.Mpp)},{Math.Round(q.Y / _view.Mpp)},{MathF.Round(q.Heading * 12)}" : "";
        string key = $"{_view}|{meKey}|{scale}|{layer}|{_look().GetHashCode()}|{Tracking?.Invoke()}|{worldUp}";
        long now = Environment.TickCount64;
        if (key == _drawnKey && !_dirty && now - _drawnAt < 2000) return;   // (every 2 s anyway: people move)
        if (Interlocked.CompareExchange(ref _drawing, 1, 0) != 0) return;
        _dirty = false;
        _drawnKey = key;
        _drawnAt = now;
        var view = _view;
        var look = _look();
        var scene = new MapPainter.Scene(_map.Regions(), _map.Marks(closeUp: view.Mpp <= 2), me, view.Picked == null ? Line?.Invoke() : null,
            Track != null, Tracking?.Invoke() == true, worldUp);
        _ = Task.Run(() =>
        {
            try
            {
                var (px, pw, ph, used) = MapPainter.Board(view, scene, _tiles, look, scale, layer);
                _image = new OverlayImage(px, pw, ph, Interlocked.Increment(ref _version), look.Frost, look.TintOpacity);
                ImageScale = used;
            }
            catch (Exception x) { _log($"map: drawing failed: {x.Message}"); }
            finally { Interlocked.Exchange(ref _drawing, 0); }
        });
    }

    // --- pressing (logical pixels of the board, either way of holding it) -------------------------------------------------

    MapKey _pressedKey = MapKey.None;
    Vector2? _panFrom;
    (double X, double Y) _panCentre;
    bool _panMoved;

    MapKey KeyAt(Vector2 p)
    {
        foreach (var k in MapPainter.Keys(_view, Track != null))
            if (MapPainter.KeyRect(k, _view).Contains(p.X, p.Y)) return k;
        return MapKey.None;
    }

    bool OnMap(Vector2 p) => L.MapRect.Contains(p.X, p.Y);

    /// <summary>The pointer over the board (null: off it): lights the key under it.</summary>
    void Hover(Vector2? p)
    {
        int h = p is { } q ? (int)KeyAt(q) : -1;
        if (h != _view.Hover) _view = _view with { Hover = h };
    }

    /// <summary>A press goes down: true if the board took it (a key or the map); false on its margins (the headset then
    /// drags the board).</summary>
    bool Down(Vector2 p)
    {
        var k = KeyAt(p);
        if (k != MapKey.None) { _pressedKey = k; return true; }
        if (!OnMap(p)) return false;
        // (the mini-map's band over the map's foot: its own, not the map's)
        if (L.StripH == 0 && _view.Picked != null && L.Band.Contains(p.X, p.Y)) return true;
        _panFrom = p;
        _panCentre = (_view.CX, _view.CY);
        _panMoved = false;
        return true;
    }

    void Move(Vector2 p)
    {
        if (_panFrom is not { } from) return;
        var d = p - from;
        if (!_panMoved && d.Length() < 6) return;
        _panMoved = true;
        _view = _view with { Follow = false, CX = _panCentre.X - d.X * _view.Mpp, CY = _panCentre.Y + d.Y * _view.Mpp };
    }

    void Up(Vector2? p)
    {
        var key = _pressedKey;
        _pressedKey = MapKey.None;
        if (key != MapKey.None)
        {
            if (p is { } q && KeyAt(q) == key) Act(key);
            return;
        }
        if (_panFrom != null && !_panMoved && p is { } at) Pick(at);
        _panFrom = null;
    }

    void Act(MapKey k)
    {
        switch (k)
        {
            case MapKey.In: Zoom(1); break;
            case MapKey.Out: Zoom(-1); break;
            case MapKey.Me: _view = _view with { Follow = true }; break;
            case MapKey.Sale: _view = _view with { Sale = !_view.Sale }; break;
            case MapKey.Events: _view = _view with { Events = !_view.Events }; break;
            case MapKey.Help: _view = _view with { Help = !_view.Help }; break;
            case MapKey.Close: if (Closed != null) Closed(); else Show(false); break;
            case MapKey.World: OpenWorld?.Invoke(); _dirty = true; break;
            case MapKey.PopOut: PopOut?.Invoke(); break;
            case MapKey.PutBack: PutBack?.Invoke(); break;
            case MapKey.Go: Go(); break;
            case MapKey.Track:
                if (Tracking?.Invoke() == true) Untrack?.Invoke();
                else if (_view.Picked is { Mark: { Kind: MarkKind.Friend or MarkKind.Person } who } && who.Id != LibreMetaverse.UUID.Zero && TrackAvatar != null)
                    TrackAvatar(who.Id, who.Name);
                else if (_view.Picked is { Region: { } r } p) Track?.Invoke(r.Handle, new((float)(p.X - r.X), (float)(p.Y - r.Y), 0), p.Mark?.Name ?? r.Name);
                _dirty = true;
                break;
        }
    }

    /// <summary>Zoom in (positive) or out by steps of two, the middle kept.</summary>
    public void Zoom(float steps)
    {
        _view = _view with { Mpp = Math.Clamp(_view.Mpp * MathF.Pow(2, -steps), MinMpp, MaxMpp) };
    }

    void Pick(Vector2 p)
    {
        var (gx, gy) = MapPainter.ToGlobal(_view, p.X, p.Y);
        MapMark? best = null;
        float bestD = 12;
        foreach (var m in _map.Marks(closeUp: _view.Mpp <= 2))
        {
            if (m.Kind == MarkKind.LandForSale && !_view.Sale || m.Kind == MarkKind.Event && !_view.Events || m.Kind == MarkKind.Telehub) continue;
            var (mx, my) = MapPainter.ToBoard(_view, m.X, m.Y);
            float d = Vector2.Distance(new Vector2(mx, my), p);
            if (d < bestD) { bestD = d; best = m; }
        }
        if (best is { } b) (gx, gy) = (b.X, b.Y);
        var pick = new MapPick(gx, gy, _map.RegionAt(gx, gy), best);
        // pressing the picked spot again lets it go
        _view = _view with { Picked = _view.Picked is { } old && Math.Abs(old.X - gx) < _view.Mpp * 3 && Math.Abs(old.Y - gy) < _view.Mpp * 3 ? null : pick };
    }

    /// <summary>Go: the world map is put away, what was picked kept with it (it comes back as it was); the mini-map
    /// stays, following you again, the spot let go.</summary>
    void Go()
    {
        if (_view.Picked is not { Region: { } r } p) return;
        var at = new LibreMetaverse.Vector3((float)(p.X - r.X), (float)(p.Y - r.Y), 0);
        _log($"map: going to {r.Name} {at.X:0},{at.Y:0}");
        var tp = Teleport ?? _s.TeleportAsync;
        _ = Task.Run(async () =>
        {
            var (ok, why) = await tp(r.Name, at);
            if (!ok) _log($"map: the teleport to {r.Name} failed: {why}");
        });
        if (_isWorld) Show(false);
        else _view = _view with { Follow = true, Picked = null };
    }

    // --- the headset --------------------------------------------------------------------------------------------------

    public readonly PanelPlacement Placement;
    Vector3 _pos;
    Quaternion _rot = Quaternion.Identity;
    Vector2 _size;
    bool _place, _visible, _down, _armed = true, _dragging, _onBoard, _wasHeld;
    float _hitT;
    (Vector3 From, Vector3 Point)? _beam;

    /// <summary>The world map's resting place: its middle 3 degrees above the eyes, so its bottom edge clears the panel
    /// resting 18 below them.</summary>
    public static (Vector3, Quaternion) WorldRest(Vector3 anchor, float yaw, PanelPlacement.Mode mode, Vector2 size) =>
        PanelPlacement.Toward(anchor, yaw, 3 * MathF.PI / 180, WorldDistance);
    public const float WorldDistance = 1.25f;

    /// <summary>The mini-map out on its own: 32 degrees right of where you face, its middle just below the eyes, clear of
    /// the world map (about 44 degrees wide, centred) and of the panel and the cards low in view.</summary>
    public static (Vector3, Quaternion) MiniRest(Vector3 anchor, float yaw, PanelPlacement.Mode mode, Vector2 size) =>
        PanelPlacement.Toward(anchor, yaw - 32 * MathF.PI / 180, -2 * MathF.PI / 180, MiniDistance);
    public const float MiniDistance = 1.0f;

    /// <summary>The right ray is on the board this frame (the panel, the world and other boards wait).</summary>
    public bool RightOn => _onBoard;
    /// <summary>How far along the ray it meets the board, while it does.</summary>
    public float HitT => _onBoard ? _hitT : float.MaxValue;
    /// <summary>A press is under way on it (the trigger went down on a key or the map and is still held).</summary>
    public bool Pressing => _down;
    public bool Visible => _visible;

    public void Recentred() { if (Shown) _place = true; }

    /// <summary>Where the ray meets it as it was last placed (null: not up, or a miss): for which of the boards is nearer.</summary>
    public float? RayT(XrControllers input)
    {
        if (!_visible || input.RightAim is not { } q || input.RightAimPos is not { } o) return null;
        return PanelPlacement.Hit(o, Vector3.Transform(-Vector3.UnitZ, q), _pos, _rot, _size, out float t, out _) ? t : null;
    }

    /// <summary>Each headset frame. hidden: not shown this frame (a confirmation waits, the interface is hidden, the
    /// mini-map's hand not looked at): not drawn, no ray; blocked: the menu is up (nothing pressed). held: the pose it's
    /// held at on the hand and metres a logical pixel there (null: it's placed as a board of its own). nearer: how far
    /// along the ray something in front of it took the ray (the line, a nearer board).</summary>
    public void Headset(XrControllers input, Vector3 head, Quaternion headRot, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel,
        bool blocked, bool hidden, bool focused, float dt, (Vector3 Pos, Quaternion Rot, float Mpp)? held = null, float nearer = float.MaxValue)
    {
        _beam = null;
        _onBoard = false;
        if (!Shown || hidden)
        {
            if (_dragging) Placement.CancelGrab();
            _dragging = false;
            _visible = false;
            _down = false;
            _armed = false;
            _panFrom = null;
            _pressedKey = MapKey.None;
            if (Shown) Hover(null);
            return;
        }
        _scale = MathF.Min(UiSize.Raster, Panel.MaxRasterWidth / L.W);
        _layer = GlassLight.Layers;
        if (held is { } h)
        {
            if (_dragging) { Placement.CancelGrab(); _dragging = false; }
            _size = new Vector2(L.W, L.H) * h.Mpp;
            (_pos, _rot) = (h.Pos, h.Rot);
        }
        else
        {
            if (!_visible || _place || _wasHeld) { Placement.Place(); _place = false; }
            _size = new Vector2(L.W, L.H) * UiSize.MetresPerPx(_distance);
            (_pos, _rot) = Placement.Update(head, headRot, _size, dt, frozen: _down);
        }
        _wasHeld = held != null;
        _visible = true;
        Refresh(_scale, _layer);

        Vector2? at = null;
        float t = 0;
        if (input.RightAim is { } q && input.RightAimPos is { } o)
        {
            var dir = Vector3.Transform(-Vector3.UnitZ, q);
            float panelT = nearer;
            if (panel is { } pl && PanelPlacement.Hit(o, dir, pl.Pos, pl.Rot, pl.Size, out float pt, out _)) panelT = MathF.Min(panelT, pt);
            bool hit = PanelPlacement.Hit(o, dir, _pos, _rot, _size, out t, out var local);
            // while a press is held the point follows the ray across the board's plane, on it or not
            if ((hit && t < panelT) || (_down && t > 0))
            {
                at = new Vector2((local.X / _size.X + 0.5f) * L.W, (0.5f - local.Y / _size.Y) * L.H);
                if (hit && t < panelT) { _onBoard = true; _hitT = t; _beam = (o, o + dir * t); }
            }
        }
        Hover(_onBoard ? at : null);
        if (!focused) { _armed = false; return; }
        float trigger = input.RightTrigger;
        if (trigger < 0.35f) _armed = true;
        if (!_down && trigger > 0.6f && _armed && _onBoard && at is { } p0)
        {
            _down = true;
            _armed = false;
            if (blocked) return;
            // (held on the hand, a press on its margins does nothing: it goes where the hand goes)
            if (!Down(p0) && held == null && input.RightAimPos is { } go && input.RightAim is { } gq)
            {
                _dragging = true;
                Placement.Grab(go, Vector3.Transform(-Vector3.UnitZ, gq), t);
            }
        }
        else if (_down && trigger < 0.35f)
        {
            _down = false;
            if (_dragging) { Placement.Drop(); _dragging = false; }
            else Up(_onBoard ? at : null);
        }
        else if (_down)
        {
            if (_dragging && input.RightAimPos is { } d0 && input.RightAim is { } dq)
            {
                if (Placement.Drag(d0, Vector3.Transform(-Vector3.UnitZ, dq))) (_pos, _rot) = Placement.Pose;
            }
            else if (at is { } p1) Move(p1);
        }
    }

    /// <summary>The board's layer this frame (room pose and width in metres), or null.</summary>
    public (OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width)? Layer() =>
        _visible && Image is { } img ? (img, _pos, _rot, _size.X) : null;

    /// <summary>The ray's beam to where it meets the board, and the outline while it's dragged.</summary>
    public OverlayScene? Decorate(OverlayScene? scene, Func<Vector3, Vector3> roomToRegion)
    {
        if (_beam is not { } b && !(_dragging && Placement.Dragging)) return scene;
        var beams = new List<OverlayBeam>(scene?.Beams ?? []);
        if (_beam is { } bb)
        {
            var a = roomToRegion(bb.From);
            var c = roomToRegion(bb.Point);
            beams.Add(new OverlayBeam(a + Vector3.Normalize(c - a) * 0.04f, c, 0.003f, PanelLook.InUse.Light(0.9f) with { W = 0.55f }));
        }
        if (Placement.Dragging) beams.AddRange(Placement.Outline(roomToRegion));
        return (scene ?? new OverlayScene()) with { Beams = beams };
    }

    // --- the desktop --------------------------------------------------------------------------------------------------

    SKRect _desk;
    bool _deskDown;

    /// <summary>The desktop: where it's drawn this frame (pixels), at this UI scale, its top left there; null while put
    /// away.</summary>
    public (OverlayImage Image, Vector2 At)? Desktop(float ui, float left, float top, bool hidden)
    {
        if (!Shown || hidden) { _desk = SKRect.Empty; return null; }
        _scale = ui;
        _layer = false;
        Refresh(ui, false);
        if (_image is not { } img) return null;
        float s = img.Width / L.W;   // pixels a logical pixel, as drawn
        _desk = new SKRect(left, top, left + img.Width, top + img.Height);
        _deskScale = s;
        return (img, new Vector2(_desk.Left, _desk.Top));
    }

    float _deskScale = 1;

    /// <summary>Where it's drawn on the desktop (pixels: left, top, right, bottom) and pixels a logical pixel; null off it.</summary>
    public (SKRect Rect, float Scale)? DesktopPlace => _desk.IsEmpty ? null : (_desk, _deskScale);

    Vector2? DeskLogical(Vector2 px) => _desk.Contains(px.X, px.Y) || _deskDown ? new Vector2((px.X - _desk.Left) / _deskScale, (px.Y - _desk.Top) / _deskScale) : null;

    /// <summary>The mouse is over the board (a click is the board's, not the world's).</summary>
    public bool DesktopOver(Vector2 px) => Shown && _desk.Contains(px.X, px.Y);

    public void DesktopMove(Vector2 px)
    {
        var p = DeskLogical(px);
        Hover(_desk.Contains(px.X, px.Y) ? p : null);
        if (_deskDown && p is { } q) Move(q);
    }

    public bool DesktopDown(Vector2 px)
    {
        if (!DesktopOver(px) || DeskLogical(px) is not { } p) return false;
        _deskDown = Down(p);
        return true;
    }

    public void DesktopUp(Vector2 px)
    {
        if (!_deskDown) return;
        _deskDown = false;
        Up(_desk.Contains(px.X, px.Y) ? DeskLogical(px) : null);
    }

    public bool DesktopWheel(Vector2 px, float y)
    {
        if (!DesktopOver(px)) return false;
        Zoom(MathF.Sign(y) * 0.5f);
        return true;
    }

    // --- for tests and the assistant -----------------------------------------------------------------------------------

    /// <summary>A press at a logical point of the board (down and up), as a hand would.</summary>
    public void PressAt(Vector2 p)
    {
        if (Down(p)) Up(p);
    }

    /// <summary>A point of the board (logical px) in the room, while it's up in the headset.</summary>
    public Vector3? RoomPoint(Vector2 p) => _visible
        ? _pos + Vector3.Transform(new Vector3((p.X / L.W - 0.5f) * _size.X, (0.5f - p.Y / L.H) * _size.Y, 0), _rot)
        : null;

    /// <summary>The middle of a key in the room, while it's up in the headset.</summary>
    public Vector3? KeyPoint(MapKey k)
    {
        var r = MapPainter.KeyRect(k, _view);
        return r.IsEmpty ? null : RoomPoint(new Vector2(r.MidX, r.MidY));
    }

    public void Dispose() { }
}
