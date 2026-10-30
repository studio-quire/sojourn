using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Map;

/// <summary>
/// The location line, as the viewer's location bar (lllocationinputctrl.cpp): the region's rating, the parcel and the
/// region, what's blocked here (voice, flying, pushing, building, scripts, avatars seen from outside), damage with your
/// health, the parcel for sale; and the lag meter's three bars (this computer, the connection, the region). In the
/// headset it shows just above the left wrist while you look at it, as a watch (the assistant's wrist cards stack above
/// it), at the panel's angular size; on the desktop it sits at the top left. A press on it (the right trigger, a click)
/// opens the mini-map over it, and closes it (MapWiring). Main thread.
/// </summary>
public sealed class WristLine
{
    const float Deg = MathF.PI / 180;
    /// <summary>Where it sits over the wrist, up from it (metres): the assistant's wrist cards start above it.</summary>
    public const float Lift = 0.03f, CardsFrom = 0.075f;
    /// <summary>How far from the middle of the view (degrees) it comes up, and how far it may drift before it goes.</summary>
    const float ShowWithin = 25, KeepWithin = 29;
    /// <summary>How far the left palm may turn from facing the eyes (degrees) for it to come up, and to stay.</summary>
    const float PalmShow = 65, PalmKeep = 75;
    /// <summary>Its size is the panel's angular size at this distance (metres), fixed in the room like the forearm's
    /// tokens: it doesn't shrink as the hand comes to the face.</summary>
    const float SizedAt = 0.45f;
    /// <summary>Metres a logical pixel on the hand (the line, and the mini-map held above it).</summary>
    public static float Mpp => SizedAt * MathF.Tan(UiSize.DegPerPx * Deg);
    /// <summary>The gap between the line and what's stacked above it (metres).</summary>
    public const float Gap = 0.006f;

    readonly AgentSession _s;
    string _key = "";
    OverlayImage? _image;
    float _imageScale = 1;
    long _version;

    public WristLine(AgentSession s) => _s = s;

    public bool On { get; set; } = true;
    public bool Meter { get; set; } = true;
    public Location? Location { get; private set; }

    /// <summary>Redraw if what it says changed. stats: the meter's numbers (null: none).</summary>
    public OverlayImage? Picture(Stats? stats, PanelLook look, float scale, bool layer, bool lit, float minW = 0)
    {
        if (!On || Where.Now(_s) is not { } l) { Location = null; return null; }
        Location = l;
        var st = Meter ? stats : null;
        string key = $"{l.Key}|{st?.Key}|{scale}|{layer}|{lit}|{minW}|{look.GetHashCode()}";
        if (key != _key || _image == null)
        {
            _key = key;
            var (px, w, h, used) = MapPainter.Line(l, st, look, scale, layer, lit, minW);
            _image = new OverlayImage(px, w, h, Interlocked.Increment(ref _version), look.Frost, look.TintOpacity);
            _imageScale = used;
        }
        return _image;
    }

    float LogicalW => _image is { } i ? i.Width / _imageScale : 0;
    float LogicalH => _image is { } i ? i.Height / _imageScale : 0;

    /// <summary>The line's width as last drawn in the headset (logical px; 0: not drawn there yet): the mini-map on the
    /// hand takes it.</summary>
    public float HeadsetWidth { get; private set; }

    // --- the headset ----------------------------------------------------------------------------------------------------

    bool _looked, _down, _armed = true;
    (Vector3 Pos, Quaternion Rot, float Width)? _pose;
    (Vector3 From, Vector3 Point)? _beam;

    /// <summary>The right ray is on it this frame.</summary>
    public bool RightOn { get; private set; }
    public bool Lit => RightOn;
    /// <summary>How far along the ray it meets the line, while it does.</summary>
    public float HitT { get; private set; } = float.MaxValue;
    /// <summary>The wrist is looked at as a watch this frame (the line and what's stacked over it show).</summary>
    public bool Looked => _looked;
    /// <summary>While looked at: where something stacked above the line starts (the middle of its bottom edge, in the
    /// room) and the way it faces (the line's, so the two lie in one plane).</summary>
    public (Vector3 Bottom, Quaternion Rot)? StackFrom { get; private set; }

    /// <summary>Each headset frame: placed over the left wrist while it's looked at; true when a press on it asks for the
    /// mini-map. hidden: not shown (a confirmation, the interface hidden). above: the height (metres) of what's stacked
    /// over the line (the mini-map on the hand; 0: none), which counts as looked at too, loosens the look rule and
    /// shows even with the line off; hold: a press is under way on that, so the look doesn't drop it mid-press; minW: the
    /// line drawn at least this wide (logical px), as wide as what's stacked over it.</summary>
    public bool Headset(XrControllers input, Vector3 head, Quaternion headRot, Stats? stats, bool hidden, bool blocked, bool focused,
        float above = 0, bool hold = false, float minW = 0)
    {
        _pose = null;
        _beam = null;
        RightOn = false;
        HitT = float.MaxValue;
        StackFrom = null;
        if (!On && above <= 0 || hidden || input.LeftHandPos is not { } gp || input.LeftHand is not { } gr) { _looked = false; _down = false; return false; }
        var wrist = gp + Vector3.Transform(new Vector3(0, 0, 0.06f), gr);
        var to = wrist - head;
        var facing = Vector3.Transform(-Vector3.UnitZ, headRot);
        // looked at: the line's own place (just above the wrist) near the middle of the view; gone as soon as the look
        // moves off it, with only enough hysteresis not to flicker at the edge (it lingered to 40 degrees, well into the
        // edge of view: the user, 2026-10-29). With the mini-map stacked over it, its middle counts as well, so reading
        // the map doesn't lose it
        float Off(Vector3 at)
        {
            var seen = at - head;
            float d = seen.Length();
            return d > 0.05f ? MathF.Acos(Math.Clamp(Vector3.Dot(seen / d, facing), -1, 1)) : MathF.PI;
        }
        float lineH = On ? MapPainter.LineHeight * Mpp : 0;
        float angle = Off(wrist + Vector3.UnitY * Lift);
        if (above > 0) angle = MathF.Min(angle, Off(wrist + Vector3.UnitY * (Lift + lineH + Gap + above / 2)));
        float dist = to.Length();
        // and only with the palm turned to the eyes, not the back of the hand or the side (the user, 2026-10-29); the
        // grip pose's +X comes out of the left palm (VrPilot's hand notes). With the mini-map open on the hand, no rule:
        // it stays over the hand until closed (even a very loose one felt like a bug when it went: the user, 2026-10-29)
        var palm = Vector3.Transform(Vector3.UnitX, gr);
        float palmAngle = dist > 0.05f ? MathF.Acos(Math.Clamp(Vector3.Dot(palm, -to / dist), -1, 1)) : MathF.PI;
        float within = _looked ? KeepWithin : ShowWithin, palmWithin = _looked ? PalmKeep : PalmShow;
        _looked = above > 0 || _looked && hold || dist < 0.85f && angle < within * Deg && palmAngle < palmWithin * Deg;
        if (!_looked) { _down = false; return false; }
        if (!On)
        {
            var at = wrist + Vector3.UnitY * Lift - Vector3.Normalize(to) * 0.02f;
            StackFrom = (at, PanelPlacement.Facing(head, at));
            _down = false;
            return false;
        }
        // (lit by where the ray was last frame: the picture comes before this frame's ray, which needs its size)
        if (Picture(stats, LookNow, UiSize.Raster, GlassLight.Layers, _wasOn, minW) is null) return false;
        HeadsetWidth = LogicalW;
        var size = new Vector2(LogicalW, LogicalH) * Mpp;
        var centre = wrist + Vector3.UnitY * (Lift + size.Y / 2) - Vector3.Normalize(to) * 0.02f;
        var rot = PanelPlacement.Facing(head, centre);
        _pose = (centre, rot, size.X);
        StackFrom = (centre + Vector3.Transform(Vector3.UnitY, rot) * (size.Y / 2 + Gap), rot);

        bool asked = false;
        if (input.RightAim is { } q && input.RightAimPos is { } o)
        {
            var dir = Vector3.Transform(-Vector3.UnitZ, q);
            // either side: it faces the eyes, tilted up, so a hand held low points at it from behind its plane
            if (PanelPlacement.Hit(o, dir, centre, rot, size, out float t, out _)
                || PanelPlacement.Hit(o, dir, centre, rot * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), size, out t, out _))
            {
                RightOn = true;
                HitT = t;
                _beam = (o, o + dir * t);
            }
        }
        if (Debug != null && Environment.TickCount64 - _debugAt > 500)
        {
            _debugAt = Environment.TickCount64;
            Debug($"map line: at {centre} size {size} ray {(input.RightAimPos is { } ro ? ro.ToString() : "none")} on {RightOn} trigger {input.RightTrigger:0.0}");
        }
        _wasOn = RightOn;
        if (!focused) { _armed = false; return false; }
        if (input.RightTrigger < 0.35f) { _armed = true; if (_down && RightOn && !blocked) asked = true; _down = false; }
        else if (input.RightTrigger > 0.6f && _armed && RightOn) { _down = true; _armed = false; }
        return asked;
    }

    bool _wasOn;
    long _debugAt;

    /// <summary>SOJOURN_MAP_DEBUG=1: where the line is and whether the ray meets it, twice a second.</summary>
    public Action<string>? Debug { get; set; }

    /// <summary>The look the line is drawn in (set by the wiring each frame).</summary>
    public PanelLook LookNow { get; set; } = PanelLook.InUse;

    public (OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width)? Layer() =>
        _pose is { } p && _image is { } img ? (img, p.Pos, p.Rot, p.Width) : null;

    public OverlayScene? Decorate(OverlayScene? scene, Func<Vector3, Vector3> roomToRegion)
    {
        if (_beam is not { } b) return scene;
        var a = roomToRegion(b.From);
        var c = roomToRegion(b.Point);
        var beams = new List<OverlayBeam>(scene?.Beams ?? []) { new(a + Vector3.Normalize(c - a) * 0.04f, c, 0.003f, PanelLook.InUse.Light(0.9f) with { W = 0.55f }) };
        return (scene ?? new OverlayScene()) with { Beams = beams };
    }

    // --- the desktop --------------------------------------------------------------------------------------------------

    SKRect _desk;
    bool _deskLit;

    /// <summary>The desktop: drawn at the top left at this UI scale; null when off.</summary>
    public (OverlayImage Image, Vector2 At)? Desktop(Stats? stats, PanelLook look, float ui, bool hidden)
    {
        if (hidden || Picture(stats, look, ui, false, _deskLit) is not { } img) { _desk = SKRect.Empty; return null; }
        _desk = new SKRect(16 * ui, 16 * ui, 16 * ui + img.Width, 16 * ui + img.Height);
        return (img, new Vector2(_desk.Left, _desk.Top));
    }

    /// <summary>The bottom of the line on the desktop (the board goes under it).</summary>
    public float DesktopBottom(float ui) => _desk.IsEmpty ? 16 * ui : _desk.Bottom + 8 * ui;

    public bool DesktopOver(Vector2 px) => _desk.Contains(px.X, px.Y);

    public SKRect? DesktopRect => _desk.IsEmpty ? null : _desk;

    public void DesktopMove(Vector2 px) => _deskLit = DesktopOver(px);
}
