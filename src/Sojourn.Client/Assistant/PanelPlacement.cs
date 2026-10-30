using System.Numerics;
using Sojourn.Render.Realtime;

namespace Sojourn.Client.Assistant;

/// <summary>
/// Where one of the headset's menus floats, in the room's space (OpenXR: y up, -z forward). Every sheet the person sees
/// has one (the user, 2026-10-28: every menu follows your head until you move it, as the panel did): the assistant's
/// panel (with confirmations), the settings menu, the chat feed, each card shown with the person, the reader, the HUD
/// board, the page board and the keyboard. Two ways each can sit:
/// - Following (the default): body-anchored with a lazy follow. Placed where you face when it appears, it holds still
///   while you look around it (reading, leaning in, glancing at someone doesn't move it); turn or step past that range
///   and it's carried along at the range's edge, continuously, rather than gliding back to the middle (user,
///   2026-10-26), so it sits wherever the head leaves it (<see cref="Leash"/>). Where it rests is its own
///   (<see cref="RestPose"/>): the panel low in view, the feed left of it and the cards right of it, the reader and the
///   boards above it, the keyboard low and near. A sheet beside the panel follows the panel's heading and anchor while
///   the panel shows and follows (<see cref="Leader"/>), so the two never drift into each other; on its own otherwise.
/// - Put somewhere: the person drags it (trigger held on its body or strip) and lets go; it stays there in the room
///   (which the stick carries along, so it stays with you as you walk). It shows there whenever it comes up while that
///   place is in view; otherwise it comes up in front of you, following, for that showing. Dropping it back on the
///   outline of its resting place (drawn while dragging) makes it follow again. Where it was put is kept between
///   sessions (<see cref="MenuPlaces"/>, in the headset's tracking space), which also brings every menu back.
/// Confirmations always come up in front, following, and are never put anywhere. While the pointer is on it or a press
/// is under way its range is 15 degrees wider (it holds still for the hand, but a head turned well away still carries
/// it); a confirmation it carries re-arms after every glide (PendingConfirm.MarkMoved).
///
/// Sizes are angular: a logical pixel spans <see cref="DegPerPx"/> at <see cref="Distance"/> (body text ~1.25 degrees,
/// about 30 of a Quest 3's display pixels, at UiSize 1), nearer the Quest's focal distance (~1.3 m) than arm's length, and past
/// where the controllers wave. A sheet put somewhere keeps its size in metres. The panel, by mode, following:
/// - Panel (status, answers, choices): its top edge 18 degrees below the eyes, growing downward: below the faces and
///   things you look at, read with a glance down; carried along past 30 degrees.
/// - Confirm: its top edge 10 degrees below the eyes, nearer the centre of view; comes up in front, carried past 20.
/// - Menu (the settings menu's own placement): its top edge 4 degrees below the eyes; carried past 35.
/// A hand reaching to it holds its place however close the head comes (turning away still carries it). The assistant
/// brings it in front when asked (SojournAssistant bringPanel); X hiding the interface and showing it again places it
/// afresh (in front, unless where the person put it is in view).
/// </summary>
public sealed class PanelPlacement
{
    public enum Mode { Panel, Confirm, Menu }

    /// <summary>Where a sheet rests while it follows: for the head where it was placed (anchor), the heading it rests
    /// at, the panel's mode (the panel's own rest depends on it) and its size in metres.</summary>
    public delegate (Vector3 Pos, Quaternion Rot) RestPose(Vector3 anchor, float yaw, Mode mode, Vector2 size);

    /// <summary>What a sheet beside the panel follows: the panel's resting heading and the head where it was placed;
    /// exact: taken as it is (a card in the view column, which glides with the panel itself), else glided to.</summary>
    public readonly record struct Leader(float Yaw, Vector3 Anchor, bool Exact = false);

    public const float Distance = 1.1f;
    /// <summary>The settings menu's fixed tilt (a mid-height menu's, as it was when its tilt followed its height).</summary>
    const float MenuTilt = -14 * Deg;
    /// <summary>0.07 at UiSize 1 (the person's size setting scales it).</summary>
    public static float DegPerPx => UiSize.DegPerPx;
    static float MetresPerPx => UiSize.MetresPerPx(Distance);
    const float Deg = MathF.PI / 180;

    /// <summary>Its name where its place is kept (the settings' menuPlaces) and in the console.</summary>
    public readonly string Name;
    readonly RestPose _rest;
    readonly float _range;

    /// <summary>A sheet's placement: its name, where it rests while following (the panel's own by default) and how far
    /// the head turns before it's carried along (the panel's mode decides that for the panel).</summary>
    public PanelPlacement(string name = "panel", RestPose? rest = null, float rangeDeg = 30)
    {
        Name = name;
        _rest = rest ?? PanelRest;
        _range = rangeDeg * Deg;
    }

    /// <summary>A drop put it somewhere, or back to following (main thread).</summary>
    public event Action<PanelPlacement>? PinChanged;

    // following (_following: gliding back in front, after Recentre; _moved: carried along this frame)
    bool _placed, _following, _recentre, _moved;
    float _yaw;
    Vector3 _anchor;
    Mode _mode;
    Leader? _leader;

    // put somewhere by the person: its pose in the room, and whether this showing sits there
    Vector3? _pin;
    Quaternion _pinRot;
    bool _atPin;

    // a drag: where the ray first met the sheet (in its own frame), how far along the ray, whether it has moved yet
    bool _grabbed, _dragMoved;
    float _grabT;
    Vector3 _grabLocal, _grabHit;

    // this frame's pose
    Vector3 _pos, _head;
    Quaternion _rot = Quaternion.Identity;
    float? _headYaw;
    Vector2 _size;

    /// <summary>Moving with the head this frame (carried along, or gliding back in front).</summary>
    public bool Gliding => _following || _moved;
    /// <summary>The person has put it somewhere (it shows there when that place is in view).</summary>
    public bool Pinned => _pin != null;
    /// <summary>Showing where it was put, and that's out of view now.</summary>
    public bool OutOfView => _atPin && _pin is { } p && !InView(p);
    /// <summary>Being dragged (the trigger held on it and moved).</summary>
    public bool Dragging => _grabbed && _dragMoved;
    /// <summary>The trigger went down on it for a drag and is still held (moved or not).</summary>
    public bool Grabbed => _grabbed;
    /// <summary>The heading it rests at while it follows (not put somewhere, not being dragged): the sheets beside the
    /// panel keep their place by it.</summary>
    public float? FollowingYaw => _placed && !_atPin && !Dragging ? _yaw : null;
    /// <summary>The head where it was placed, while it follows (the feed and the view cards share it).</summary>
    public Vector3? FollowingAnchor => FollowingYaw != null ? _anchor : null;
    /// <summary>Both, for the sheets beside it.</summary>
    public Leader? AsLeader => FollowingYaw is { } y ? new Leader(y, _anchor) : null;
    /// <summary>This frame's pose (as last updated or dragged).</summary>
    public (Vector3 Pos, Quaternion Rot) Pose => (_pos, _rot);

    /// <summary>Place it afresh at the next update (it's appearing): where it was put, if that's in view, else in front.</summary>
    public void Place() => _placed = false;

    /// <summary>Glide it in front of the head (a confirmation arriving), unless it's already
    /// there or it sits where the person put it and that's in view.</summary>
    public void Recentre() { if (_placed) _recentre = true; }

    volatile bool _toFront;

    /// <summary>Glide it in front of the head for this showing, from wherever it is, even where the person put it
    /// (that place is kept for next time). Safe from any thread (the assistant's op).</summary>
    public void BringToFront() => _toFront = true;

    /// <summary>Where the person put it in the room (null: it follows). Set from the settings at start, so a place kept
    /// from the session before is where it shows, when that's in view.</summary>
    public (Vector3 Pos, Quaternion Rot)? Pin
    {
        get => _pin is { } p ? (p, _pinRot) : null;
        set
        {
            _pin = value?.Pos;
            _pinRot = value?.Rot ?? Quaternion.Identity;
            if (_atPin) _placed = false;
            _atPin = false;
        }
    }

    /// <summary>Forget where it was put (the room's origin moved: the headset's own recentring; or every menu brought
    /// back): it follows, from in front, at its next update.</summary>
    public void Unpin()
    {
        _pin = null;
        if (_atPin) _placed = false;
        _atPin = false;
        if (_grabbed) CancelGrab();
    }

    float Threshold(Mode m) => m switch { Mode.Confirm => 20 * Deg, Mode.Menu => 35 * Deg, _ => _range };
    const float FrozenSlack = 15 * Deg;

    /// <summary>The panel's pose and size (metres) this frame for its logical size (its pixels at the person's UiSize).
    /// frozen: the pointer is on it or a press is under way.</summary>
    public (Vector3 Position, Quaternion Rotation, Vector2 Size) Update(Vector3 head, Quaternion headRot, Mode mode, float logicalW, float logicalH,
        float dt, bool frozen, bool reaching = false)
    {
        var size = new Vector2(logicalW, logicalH) * MetresPerPx;
        var (pos, rot) = Update(head, headRot, size, dt, frozen, reaching, null, mode);
        return (pos, rot, size);
    }

    /// <summary>This frame's pose for a sheet of the given size in metres. frozen: the pointer is on it or a press is
    /// under way; reaching: a hand is held out to it; leader: the panel's resting place, for a sheet beside it, while the
    /// panel shows and follows.</summary>
    public (Vector3 Position, Quaternion Rotation) Update(Vector3 head, Quaternion headRot, Vector2 size, float dt, bool frozen = false,
        bool reaching = false, Leader? leader = null, Mode mode = Mode.Panel)
    {
        var look = Vector3.Transform(-Vector3.UnitZ, headRot);
        var flat = new Vector2(look.X, look.Z);
        float? headYaw = flat.Length() > 0.25f ? MathF.Atan2(-flat.X, -flat.Y) : null;   // unknown looking straight up or down
        _head = head;
        _headYaw = headYaw;
        _size = size;
        _leader = leader;

        // a confirmation taking the layer from a panel at its place: in front, afresh; and once it's done, back to its
        // place if that's in view (a confirmation is never put anywhere)
        bool pins = mode != Mode.Confirm;
        if (mode != _mode && (_atPin || (pins && _pin != null))) _placed = false;
        _mode = mode;

        if (_grabbed && _dragMoved) return (_pos, _rot);
        if (!_placed)
        {
            _yaw = leader?.Yaw ?? headYaw ?? _yaw;
            _anchor = leader?.Anchor ?? head;
            _placed = true;
            _following = false;
            _recentre = false;
            _atPin = pins && _pin is { } p && InView(p);
        }
        if (_toFront && _placed)
        {
            _toFront = false;
            _atPin = false;
            _following = true;
            _recentre = false;
        }
        if (_recentre)
        {
            _recentre = false;
            if (!_atPin) _following = true;
            else if (!InView(_pin!.Value))
            {
                // its place is out of view: in front for this showing
                _atPin = false;
                _yaw = headYaw ?? _yaw;
                _anchor = head;
            }
        }
        if (_atPin)
        {
            _following = false;
            (_pos, _rot) = (_pin!.Value, _pinRot);
            return (_pos, _rot);
        }

        _moved = false;
        if (leader is { } ld)
        {
            // beside the panel: where it rests, this rests, and it moves with it (a jump, as when the panel comes up
            // elsewhere, is glided). The panel's own anchor too: with one of its own (each leashed to the head apart)
            // the two drifted up to 20 cm apart, enough to close the gap between them
            float d = Wrap(ld.Yaw - _yaw);
            float a = ld.Exact ? 1 : MathF.Abs(d) < 10 * Deg ? 1 - MathF.Exp(-dt / 0.03f) : 1 - MathF.Exp(-dt / 0.18f);
            _yaw = Wrap(_yaw + d * a);
            _anchor += (ld.Anchor - _anchor) * a;
            _following = false;
            _moved = MathF.Abs(d) > 0.002f;
        }
        else if (!frozen && !reaching)
        {
            if (_following)
            {
                // brought in front: all the way, then it rests there
                float dYaw = headYaw is { } hy ? Wrap(hy - _yaw) : 0;
                var dPos = head - _anchor;
                float a = 1 - MathF.Exp(-dt / 0.18f);
                _yaw = Wrap(_yaw + dYaw * a);
                _anchor += dPos * a;
                if (MathF.Abs(dYaw) < 0.03f && dPos.Length() < 0.02f) _following = false;
            }
            else _moved = Leash(ref _yaw, ref _anchor, headYaw, head, Threshold(mode), dt);
        }
        else if (reaching)
        {
            // a hand at it: the head can come as close as it likes (the panel is 1.1 m out, past arm's reach, so
            // touching it means stepping in, which carried it away from the finger; session of 2026-10-27 evening)
            _following = false;
            _moved = Leash(ref _yaw, ref _anchor, headYaw, head, Threshold(mode) + FrozenSlack, dt, holdPlace: true);
        }
        else
        {
            // the pointer on it or a press under way: it holds still for the hand, but not past a wider range. A
            // resting hand's ray lies across the panel low in view, and the panel (and the feed and cards beside it)
            // stayed behind as the head turned away until the ray happened to slip off it (session of 2026-10-27).
            _following = false;
            _moved = Leash(ref _yaw, ref _anchor, headYaw, head, Threshold(mode) + FrozenSlack, dt);
        }

        (_pos, _rot) = _rest(_anchor, _yaw, mode, size);
        return (_pos, _rot);
    }

    /// <summary>
    /// The lazy follow every menu shares (user, 2026-10-26: keep them still within a range of head movement, and past it
    /// move them continuously rather than snapping back to fixed angles, so they can sit wherever the person wants).
    /// While the head's heading stays within <paramref name="range"/> of <paramref name="yaw"/> and the head within 0.2 m
    /// across and 0.15 m up or down of <paramref name="anchor"/>, nothing moves; past that, they're drawn along to keep
    /// the head at the range's edge, eased over ~60 ms so the head's small tremors don't shake it. Returns whether
    /// anything moved.
    /// </summary>
    public static bool Leash(ref float yaw, ref Vector3 anchor, float? headYaw, Vector3 head, float range, float dt, bool holdPlace = false)
    {
        float a = 1 - MathF.Exp(-dt / 0.06f);
        bool moved = false;
        if (headYaw is { } hy)
        {
            float d = Wrap(hy - yaw), over = MathF.Abs(d) - range;
            if (over > 0) { yaw = Wrap(yaw + MathF.Sign(d) * over * a); moved = true; }
        }
        if (holdPlace) return moved && a > 0;
        var off = head - anchor;
        var flat = new Vector2(off.X, off.Z);
        float across = flat.Length();
        // 20 cm across and 15 up or down (was 35 and 25 until reaching to a panel held it still; the user found
        // things left too far behind, 2026-10-27 evening)
        if (across > 0.2f)
        {
            var step = flat * ((across - 0.2f) / across * a);
            anchor += new Vector3(step.X, 0, step.Y);
            moved = true;
        }
        if (MathF.Abs(off.Y) > 0.15f) { anchor.Y += MathF.Sign(off.Y) * (MathF.Abs(off.Y) - 0.15f) * a; moved = true; }
        return moved && a > 0;
    }

    /// <summary>The panel's own resting pose for an anchor (the head where it was placed) and a heading, by mode.</summary>
    public static (Vector3, Quaternion) PanelRest(Vector3 anchor, float yaw, Mode mode, Vector2 size)
    {
        float half = MathF.Atan(size.Y / 2 / Distance);
        if (mode == Mode.Menu)
        {
            // the settings menu's height changes with its tab: its top edge stays where it was and its tilt is fixed,
            // so switching tabs doesn't swing it (the user, 2026-10-29); a tall one rises so its bottom stays in reach
            float top = Math.Min(Math.Max(-4 * Deg, -42 * Deg + 2 * half), 4 * Deg);
            var (edge, _) = Toward(anchor, yaw, top, Distance);
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, MenuTilt);
            return (edge - Vector3.Transform(Vector3.UnitY, rot) * (size.Y / 2), rot);
        }
        float pitch = mode switch
        {
            Mode.Menu => -4 * Deg - half,
            Mode.Confirm => -10 * Deg - half,
            _ => -18 * Deg - half,
        };
        // never so low that the bottom is a strain to look at, nor the top above the eyes
        float lowest = -42 * Deg + half, highest = 4 * Deg - half;
        pitch = lowest <= highest ? Math.Clamp(pitch, lowest, highest) : (lowest + highest) / 2;
        return Toward(anchor, yaw, pitch, Distance);
    }

    /// <summary>A sheet <paramref name="distance"/> from the anchor at a heading and a pitch (its middle), turned to face
    /// the anchor, upright.</summary>
    public static (Vector3, Quaternion) Toward(Vector3 anchor, float yaw, float pitch, float distance)
    {
        var forward = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        var dir = forward * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch);
        var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
        return (anchor + dir * distance, rot);
    }

    /// <summary>A hand (room space, as the head) reaching to the sheet as last placed: over it or 20 cm past its edges,
    /// and from 60 cm on the head's side of its plane to 30 cm behind it. The panel is 1.1 m out, so an outstretched arm
    /// is still ~0.5 m short when the step toward it starts (30 cm on the head's side was too little: it still backed
    /// away, the user found, 2026-10-27 evening).</summary>
    public bool HandNear(Vector3? left, Vector3? right)
    {
        if (!_placed || _size == default) return false;
        var inv = Quaternion.Inverse(_rot);
        float headSide = MathF.Sign(Vector3.Transform(_head - _pos, inv).Z);
        var towardPanel = _pos - _head;
        towardPanel = new Vector3(towardPanel.X, 0, towardPanel.Z);
        if (towardPanel.LengthSquared() < 1e-4f) return false;
        towardPanel = Vector3.Normalize(towardPanel);
        foreach (var h in new[] { left, right })
        {
            if (h is not { } p) continue;
            // held out toward it, not resting in the lap below it
            if (Vector3.Dot(p - _head, towardPanel) < 0.35f) continue;
            var l = Vector3.Transform(p - _pos, inv);
            float toward = l.Z * headSide;   // + on the head's side
            if (toward < 0.6f && toward > -0.3f && MathF.Abs(l.X) < _size.X / 2 + 0.2f && MathF.Abs(l.Y) < _size.Y / 2 + 0.2f)
                return true;
        }
        return false;
    }

    /// <summary>A place within 60 degrees of where the head faces, and within 3 m.</summary>
    bool InView(Vector3 at)
    {
        var d = at - _head;
        if (d.Length() > 3) return false;
        if (_headYaw is not { } hy) return true;
        var flat = new Vector2(d.X, d.Z);
        if (flat.Length() < 0.1f) return true;
        return MathF.Abs(Wrap(MathF.Atan2(-flat.X, -flat.Y) - hy)) < 60 * Deg;
    }

    // --- dragging ----------------------------------------------------------------------------------------------------

    /// <summary>The trigger went down on the sheet where a drag may start (the panel's body, not a key; a board's strip),
    /// the ray meeting it t along: it moves once the ray has moved 2.5 cm there, so a press that misses a key doesn't
    /// nudge it.</summary>
    public void Grab(Vector3 from, Vector3 dir, float t)
    {
        _grabbed = true;
        _dragMoved = false;
        _grabT = t;
        _grabHit = from + dir * t;
        _grabLocal = Vector3.Transform(_grabHit - _pos, Quaternion.Conjugate(_rot));
    }

    /// <summary>The trigger still held: the sheet follows the ray, the grabbed point under it, turned to face the head.
    /// Returns whether it's being dragged (it has moved).</summary>
    public bool Drag(Vector3 from, Vector3 dir)
    {
        if (!_grabbed) return false;
        var hit = from + dir * _grabT;
        if (!_dragMoved && Vector3.Distance(hit, _grabHit) < 0.025f) return false;
        _dragMoved = true;
        _following = false;
        var rot = Facing(_head, hit);
        var centre = hit - Vector3.Transform(_grabLocal, rot);
        rot = Facing(_head, centre);
        _pos = hit - Vector3.Transform(_grabLocal, rot);
        _rot = rot;
        return true;
    }

    /// <summary>The trigger let go: a dragged sheet stays where it is, or follows again if dropped on its resting place.
    /// Returns whether it was a drag (not a press).</summary>
    public bool Drop()
    {
        bool dragged = _grabbed && _dragMoved;
        _grabbed = _dragMoved = false;
        if (!dragged) return false;
        if (NearRest())
        {
            _pin = null;
            _atPin = false;
            _yaw = _leader?.Yaw ?? _headYaw ?? _yaw;
            _anchor = _leader?.Anchor ?? _head;
            _following = false;
        }
        else
        {
            _pin = _pos;
            _pinRot = _rot;
            _atPin = true;
        }
        PinChanged?.Invoke(this);
        return true;
    }

    /// <summary>Let go of a drag without placing it (the sheet went away, the menu opened).</summary>
    public void CancelGrab()
    {
        if (_grabbed && _dragMoved) _placed = false;
        _grabbed = _dragMoved = false;
    }

    /// <summary>While dragging: the corners of its resting place (where it follows from: beside the panel where the
    /// panel is, else where the head faces now), to draw as a faint outline, and whether the sheet is near enough to drop
    /// into it.</summary>
    public (Vector3[] Corners, bool Near)? Rest()
    {
        if (!Dragging || RestNow() is not { } rest) return null;
        return (Corners(rest.Pos, rest.Rot, _size), NearRest());
    }

    (Vector3 Pos, Quaternion Rot)? RestNow()
    {
        if (_leader is { } ld) return _rest(ld.Anchor, ld.Yaw, RestMode, _size);
        if (_headYaw is not { } hy) return null;
        return _rest(_head, hy, RestMode, _size);
    }

    Mode RestMode => _mode == Mode.Confirm ? Mode.Panel : _mode;

    bool NearRest()
    {
        if (RestNow() is not { } rest) return false;
        var a = Vector3.Normalize(rest.Pos - _head);
        var b = Vector3.Normalize(_pos - _head);
        return Vector3.Dot(a, b) > MathF.Cos(9 * Deg);
    }

    /// <summary>While dragging, its resting place's outline as beams in the frame's region: faint, brighter when a drop
    /// there would make it follow again.</summary>
    public IEnumerable<OverlayBeam> Outline(Func<Vector3, Vector3> roomToRegion)
    {
        if (Rest() is not { } rest) yield break;
        var rc = rest.Corners.Select(roomToRegion).ToArray();
        var ink = PanelLook.InUse.Light(rest.Near ? 0.85f : 0.4f);
        for (int i = 0; i < 4; i++) yield return new OverlayBeam(rc[i], rc[(i + 1) % 4], 0.003f, ink);
    }

    /// <summary>A sheet at a point turned to face the head (upright: no roll).</summary>
    public static Quaternion Facing(Vector3 head, Vector3 at)
    {
        var d = at - head;
        float yaw = MathF.Atan2(-d.X, -d.Z);
        float pitch = MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length());
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
    }

    /// <summary>The corners (top left, top right, bottom right, bottom left) of a sheet at a pose.</summary>
    public static Vector3[] Corners(Vector3 pos, Quaternion rot, Vector2 size)
    {
        var r = Vector3.Transform(Vector3.UnitX, rot) * (size.X / 2);
        var u = Vector3.Transform(Vector3.UnitY, rot) * (size.Y / 2);
        return new[] { pos - r + u, pos + r + u, pos + r - u, pos - r - u };
    }

    /// <summary>Where a ray (room space) meets a sheet at a pose: how far along, and where on it from its middle (x
    /// right, y up, metres); false if it misses or meets its back.</summary>
    public static bool Hit(Vector3 from, Vector3 dir, Vector3 pos, Quaternion rot, Vector2 size, out float t, out Vector2 local)
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

    internal static float Wrap(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }
}
