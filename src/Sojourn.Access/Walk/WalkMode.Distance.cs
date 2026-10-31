using LibreMetaverse;
using Vector2 = System.Numerics.Vector2;

namespace Sojourn.Access.Walk;

/// <summary>
/// How far the arrows walked (a blind tester's ask, 2026-10-30: a tap walks about 0.9 m, and without the sonar she
/// couldn't tell how far she'd gone). A stretch runs from the first walking key until the keys have rested and the
/// avatar has stopped; then its length is said ("3 metres.", "0.9 metres backward."), straight from where it began,
/// so taps in quick succession add up to one. M asks: the last stretch (or this one so far), all the stretches since
/// Shift+M (or the last teleport), and where that point is now. The cue `distance` (text by default; it has no sound).
/// Only walking by the arrows counts, not a //go or a vehicle.
/// </summary>
public sealed partial class WalkMode
{
    Vector2? _stretchFrom;             // global, where the stretch began
    float _stretchYaw;
    bool _stretchFwd, _stretchBack, _stretchLeft, _stretchRight;
    long _stretchKeysAt;               // a walking key last down
    float? _lastStretch;               // metres
    /// <summary>The last stretch walked by the arrows and the count since Shift+M or a teleport, metres (the status op).</summary>
    public (float? Last, float All) Walked => (_lastStretch, _tripWalked);
    string? _lastStretchWay;
    float _tripWalked;
    Vector2? _tripFrom, _lastAt;
    long _lineSaidAt;
    int _lineSaidLength;
    bool _linesHooked;

    static Vector2 Global(Body b)
    {
        Utils.LongToUInts(b.Region, out var gx, out var gy);
        return new Vector2(gx + b.Pos.X, gy + b.Pos.Y);
    }

    /// <summary>Each tick: the stretch opened, carried or ended and said.</summary>
    void Distance(Body b, long now)
    {
        if (!_linesHooked) { Lines += t => { _lineSaidAt = Environment.TickCount64; _lineSaidLength = t.Length; }; _linesHooked = true; }
        var at = Global(b);
        // a teleport (anything over 15 m in a tick): the count starts again where it landed
        if (_lastAt is { } last && Vector2.Distance(last, at) > 15) { _stretchFrom = null; _tripFrom = at; _tripWalked = 0; }
        _lastAt = at;
        _tripFrom ??= at;
        if (Driving(b)) { _stretchFrom = null; return; }
        if (HeldMove && On)
        {
            if (_stretchFrom == null)
            {
                _stretchFrom = at;
                _stretchYaw = _yaw;
                _stretchFwd = _stretchBack = _stretchLeft = _stretchRight = false;
            }
            _stretchFwd |= Down(K.Fwd); _stretchBack |= Down(K.Back); _stretchLeft |= Down(K.SideL); _stretchRight |= Down(K.SideR);
            _stretchKeysAt = now;
            return;
        }
        if (_stretchFrom is not { } from) return;
        // the keys rested (any key: a tap of Left between taps of Up keeps it one stretch) and the avatar stopped
        // (the region's last word on the velocity can be late: 3 s after the keys, it's taken as stopped)
        if (now - _stretchKeysAt < 700 || now - RealInput.LastKeyAt < 700 || Speed(b) > 0.15f && now - _stretchKeysAt < 3000) return;
        // and what walk mode said just before (a turn, Blocked.) heard out first, since its lines cut in
        if (On && now - _lineSaidAt < Math.Clamp(_lineSaidLength * 45L, 600, 2500)) return;
        _stretchFrom = null;
        float m = Vector2.Distance(from, at);
        if (m < 0.3f) return;
        _tripWalked += m;
        _lastStretch = m;
        _lastStretchWay = Way();
        // out of walk mode now: kept for M, not said
        if (On && _set.WalkText("distance")) Line($"{Metres(m)}{_lastStretchWay}.");
    }

    /// <summary>Which way, when it wasn't forward: by the keys that walked it.</summary>
    string Way() => _stretchFwd ? "" : _stretchBack && !_stretchLeft && !_stretchRight ? " backward"
        : _stretchLeft && !_stretchRight && !_stretchBack ? " to the left" : _stretchRight && !_stretchLeft && !_stretchBack ? " to the right" : "";

    /// <summary>To a tenth under 3 metres (a tap's length is worth hearing), whole metres beyond.</summary>
    static string Metres(float m) => m < 2.95f ? Bearings.MetresTenths(m) : Bearings.Metres(m);

    /// <summary>M: how far.</summary>
    void DistanceLine()
    {
        if (Me() is not { } b) return;
        var at = Global(b);
        var parts = new List<string>();
        if (_stretchFrom is { } from)
        {
            float so = Vector2.Distance(from, at);
            parts.Add($"{Metres(so)} so far.");
            if (_tripWalked > 0) parts.Add($"Before that, {Metres(_tripWalked)} in all.");
        }
        else if (_lastStretch is { } l)
        {
            parts.Add($"Last walk: {Metres(l)}{_lastStretchWay}.");
            if (_tripWalked > l + 0.05f) parts.Add($"In all: {Metres(_tripWalked)}.");
        }
        else parts.Add("Nothing walked yet.");
        if (_tripFrom is { } start)
        {
            var d = start - at;
            if (d.Length() < 0.5f) { if (_lastStretch != null || _stretchFrom != null) parts.Add("Back where the count began."); }
            else parts.Add($"Where the count began: {Metres(d.Length())}, {Bearings.Clock(Bearings.Compass(0, 0, d.X, d.Y) - Compass(b.Yaw))}.");
        }
        Line(string.Join(" ", parts));
    }

    /// <summary>Shift+M: counting from here.</summary>
    void DistanceReset()
    {
        if (Me() is not { } b) return;
        _tripFrom = Global(b);
        _tripWalked = 0;
        _lastStretch = null;
        Line("Counting from here.");
    }
}
