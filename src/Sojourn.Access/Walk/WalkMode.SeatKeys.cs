namespace Sojourn.Access.Walk;

/// <summary>
/// The seat's hold on her keys, said: a vehicle that lets go of them while she stays on it, and the arrows pressed while
/// nothing has them. Nothing else told her the arrows had stopped driving: on 0.1.0.1240 (2026-11-26) her partner
/// boarding his train had its script let go of her keys (the region's ScriptControlChange, in the same second as the
/// train's "has boarded!"; a script holds one avatar's permissions at a time, so asking his let hers go, on any viewer),
/// and she logged in again to get them back, where sitting on it again would have done.
/// </summary>
public sealed partial class WalkMode
{
    /// <summary>A crossing lets go and takes again within a second or two; a seat's script takes them a moment after the sit.</summary>
    const long KeysGoneFor = 2000, KeysAfterSit = 3000, KeysAfterCrossing = 10_000;
    /// <summary>An arrow pressed with nothing holding the keys is said at most this often.</summary>
    const long KeysPressedEvery = 20_000;

    // _keysTold: this letting go is dealt with (said, or driving after someone's to say); _keysSpoke: said here, so their
    // coming back is said too
    bool _keysHeld, _keysTold, _keysSpoke;
    long _keysGoneAt, _keysSatAt, _keysCrossedAt = long.MinValue / 2, _keysSaidAt = long.MinValue / 2;
    string? _keysSeat, _keysRegion;

    /// <summary>The movement keys taken and not passed on, as a vehicle takes them. A HUD that takes them passed on (a
    /// group drive's, an AO's) neither stands for the seat nor hides its letting go (the review of 2026-11-27).</summary>
    bool KeysKept => (S.ControlsKept & MoveKeys) != 0;
    /// <summary>Anything holding them, passed on or not: where the arrows go somewhere, "hasn't taken your keys" isn't said.</summary>
    bool KeysAny => (S.ControlsTaken & MoveKeys) != 0;

    long _standAskedAt = long.MinValue / 2;

    /// <summary>She asked to stand (Commands): the seat letting go of her keys as she does isn't news.</summary>
    public void StandAsked() => _standAskedAt = Environment.TickCount64;

    void SeatKeysTick(Body b, long now)
    {
        if (_keysRegion != null && b.RegionName != _keysRegion) _keysCrossedAt = now;
        _keysRegion = b.RegionName;
        if (!b.Sitting)
        {
            if (SeatInDoubt(b)) return;   // a crossing's moment, the seat not sent yet: as it was
            _keysSeat = null;
            _keysHeld = _keysTold = _keysSpoke = false;
            _keysGoneAt = _keysSatAt = 0;
            return;
        }
        string seat = b.SeatName ?? _seatName ?? "Your seat";
        if (_keysSeat != seat) { _keysSeat = seat; _keysSatAt = now; _keysHeld = _keysTold = _keysSpoke = false; _keysGoneAt = 0; }
        if (KeysKept)
        {
            if (_keysSpoke)
            {
                Log($"{seat} took the keys again, {now - _keysGoneAt} ms after letting go");
                if (_set.WalkText("vehicle")) Line($"{seat} has your keys again.");
            }
            _keysHeld = true;
            _keysTold = _keysSpoke = false;
            _keysGoneAt = 0;
            return;
        }
        if (!_keysHeld) return;
        if (_keysGoneAt == 0) _keysGoneAt = now;
        // driving after someone says it in its own words as it lets go (WalkMode.Convoy.cs), after its own wait at a
        // crossing; nothing more here, then or just after it ends
        if (_convoy != null || now - _convoyEndedAt < 3000) { if (_convoy == null) _keysTold = true; return; }
        if (_keysTold || now - _keysGoneAt < KeysGoneFor || now - _keysCrossedAt < KeysAfterCrossing || now - _standAskedAt < 10_000) return;
        _keysTold = _keysSpoke = true;
        _keysSaidAt = now;
        Log($"said {seat} let go of the keys, {now - _keysGoneAt} ms on");
        if (_set.WalkText("vehicle")) Line(KeysGone(seat));
    }

    static string KeysGone(string seat) => $"{seat} let go of your keys, so the arrows don't drive it now. Sitting on it again usually gives them back.";

    /// <summary>An arrow pressed seated (KeyDown): with nothing keeping the keys, said why nothing moves. (Page Up and Down
    /// aren't asked about: a lift may take only those, and these keys say nothing of them.)</summary>
    void KeysPressed(Body b, long now)
    {
        if (!b.Sitting || KeysKept || _convoy != null || now - _keysSatAt < KeysAfterSit || now - _keysCrossedAt < KeysAfterCrossing
            || now - _keysSaidAt < KeysPressedEvery) return;
        string seat = b.SeatName ?? _seatName ?? "Your seat";
        if (_keysHeld)
        {
            // said now, not again by the tick
            _keysTold = _keysSpoke = true;
            if (_keysGoneAt == 0) _keysGoneAt = now;
            _keysSaidAt = now;
            Line(KeysGone(seat));
        }
        else if (!KeysAny)
        {
            _keysSaidAt = now;
            Line($"{seat} hasn't taken your keys, so the arrows don't move it.");
        }
    }
}
