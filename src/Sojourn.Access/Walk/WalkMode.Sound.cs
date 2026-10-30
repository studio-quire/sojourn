using LibreMetaverse;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>
/// M4's sounds in walk mode (docs/access-app.md > Beacon, Marks): where a sound about someone or something is placed,
/// the beacon on where a walk is heading (or on someone or something the person put it on, to walk to by hand), and
/// the marks (O): doors, seats and touchable things around, each sounding from where it is. Written by Claude Opus 5.5,
/// 2026-10-29.
/// </summary>
public sealed partial class WalkMode
{
    /// <summary>Where the person faces now (the turn being held, seated the seat's).</summary>
    float Facing(Body b) => b.Sitting ? b.Yaw : (Held || _wasHeld ? _yaw : b.Yaw);

    /// <summary>A sound about something at a region position: placed a metre from the head that way (so it's heard at
    /// a steady level whatever the distance), with how far it really is.</summary>
    public (Vector3d At, float Distance)? Toward(Vector3 pos)
    {
        if (Me() is not { } b) return null;
        var d = new Vector2(pos.X - b.Pos.X, pos.Y - b.Pos.Y);
        float yaw = d.LengthSquared() < 0.01f ? Facing(b) : MathF.Atan2(d.Y, d.X);
        return Around(b, yaw) is { } at ? (at, new Vector3(pos.X - b.Pos.X, pos.Y - b.Pos.Y, pos.Z - b.Pos.Z).Length()) : null;
    }

    public (Vector3d At, float Distance)? Toward(LibreMetaverse.Vector3 pos) => Toward(V(pos));

    /// <summary>The same for someone or something by id, in the region the person is in; null when it isn't there.</summary>
    public (Vector3d At, float Distance)? Toward(UUID id) => PosOf(id) is { } p ? Toward(p) : null;

    Vector3? PosOf(UUID id)
    {
        if (id == UUID.Zero || S.Region is not { } w) return null;
        if (w.AvatarById(id) is { } av) return V(w.RegionPosition(av));
        if (w.PrimById(id) is { } prim) return V(w.RegionPosition(prim));
        return null;
    }

    /// <summary>For the cues' record (tests): a global position as degrees clockwise from facing and metres from the head.</summary>
    (double Bearing, double Distance)? RelativeOf(Vector3d g)
    {
        if (Me() is not { } b) return null;
        Utils.LongToUInts(b.Region, out var gx, out var gy);
        var rel = new Vector3((float)(g.X - gx), (float)(g.Y - gy), (float)g.Z) - (b.Pos + new Vector3(0, 0, 0.7f));
        double bearing = Bearings.Norm(Bearings.Compass(0, 0, rel.X, rel.Y) - Compass(Facing(b)));
        return (Math.Round(bearing, 1), Math.Round(rel.Length(), 2));
    }

    // --- the beacon ----------------------------------------------------------------------------------------------------

    /// <summary>Something to head for: its name and where it is now in the person's region (null: not there now).</summary>
    public sealed record Target(string Name, Func<Vector3?> Where, bool ByHand);

    /// <summary>Where the app's own walk or follow is heading (Commands), if one is under way.</summary>
    public Func<Target?>? Walking { get; set; }
    volatile Target? _hand;
    long _beaconAt, _handLostSince;
    Target? _beaconWas;

    /// <summary>The target the beacon sounds on now: the app's walk or follow, else the assistant's walk (the session's
    /// autopilot), else one the person put on someone or something.</summary>
    public Target? BeaconTarget()
    {
        if (Walking?.Invoke() is { } w) return w;
        if (S.Motion.Destination is { } d && S.Client?.Network.CurrentSim?.Handle == d.Region)
            return new Target("where the assistant is walking you", () => V(d.Target), false);
        return _hand;
    }

    /// <summary>A beacon on someone or something, to walk to by hand (walk mode) or by //go; it stays until reached,
    /// taken off (Shift+B, //beacon off) or lost.</summary>
    public void BeaconOn(string name, UUID id)
    {
        _hand = new Target(name, () => PosOf(id), true);
        _handLostSince = 0;
        _beaconAt = 0;
        var where = PosOf(id) is { } p && _ops.Where(new LibreMetaverse.Vector3(p.X, p.Y, p.Z)) is { } w ? $", {w}" : "";
        Line($"Beacon on {name}{where}.");
    }

    public void BeaconOff()
    {
        if (_hand == null) { Line("No beacon to take off."); return; }
        _hand = null;
        Line("Beacon off.");
    }

    /// <summary>B: where the beacon is (the beacon's words).</summary>
    public void BeaconLine()
    {
        if (BeaconTarget() is not { } t) { Line("No beacon. //beacon and a name puts one on someone or something; a walk has its own."); return; }
        if (t.Where() is not { } p) { Line($"Beacon on {t.Name}: not here now."); return; }
        var w = _ops.Where(new LibreMetaverse.Vector3(p.X, p.Y, p.Z)) ?? "somewhere";
        Line(t.ByHand ? $"Beacon on {t.Name}, {w}." : $"Heading to {t.Name}, {w}.");
    }

    /// <summary>Each tick: a soft pip from the target's direction, more often as it nears, brighter while it's straight
    /// ahead (front and back are hard to tell apart by ear); a hand beacon ends on reaching it, said.</summary>
    void BeaconTick(Body b, long now)
    {
        var t = BeaconTarget();
        if (t != _beaconWas) { _beaconWas = t; _beaconAt = 0; }
        if (t == null) return;
        if (t.Where() is not { } pos)
        {
            if (!t.ByHand) return;
            if (_handLostSince == 0) _handLostSince = now;
            else if (now - _handLostSince > 5000)
            {
                _hand = null;
                if (_set.WalkText("beacon")) Line($"Beacon lost: {t.Name} isn't here now.");
            }
            return;
        }
        _handLostSince = 0;
        var d = new Vector2(pos.X - b.Pos.X, pos.Y - b.Pos.Y);
        float dist = d.Length();
        if (t.ByHand && dist < 1.6f && MathF.Abs(pos.Z - b.Pos.Z) < 3)
        {
            _hand = null;
            if (_set.WalkSound("beacon")) Sounds.Play(CueSounds.Cue.BeaconReached, _set.WalkLevel("beacon"), null);
            if (_set.WalkText("beacon")) Line($"Reached {t.Name}.");
            return;
        }
        if (!_set.WalkSound("beacon") || dist < 0.8f) return;
        long every = (long)(600 + 800 * Math.Clamp((dist - 2) / 18, 0, 1));
        if (now - _beaconAt < every) return;
        _beaconAt = now;
        float yaw = MathF.Atan2(d.Y, d.X);
        double off = Bearings.Norm(Compass(yaw) - Compass(Facing(b)));
        bool ahead = off < 20 || off > 340;
        float near = 1 - Math.Clamp(dist / 30, 0, 1);
        Sounds.Play(ahead ? CueSounds.Cue.BeaconAhead : CueSounds.Cue.Beacon, _set.WalkLevel("beacon") * (0.6f + 0.4f * near), Around(b, yaw));
    }

    // --- the marks --------------------------------------------------------------------------------------------------------

    const double MarksRange = 12;
    static readonly string[] SeatWords = { "chair", "seat", "bench", "sofa", "couch", "stool", "cushion", "armchair", "poseball", "pose ball", "hammock", "swing" };

    /// <summary>What a thing around is for the marks: a door (by name), a seat (sits on a click, or by name), something
    /// to touch (a script listens for touches), or null.</summary>
    static string? MarkOf(PersonOps.Thing t) =>
        IsDoor(t.Name) ? "door"
        : t.Sit || SeatWords.Any(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) ? "seat"
        : t.Touch ? "touch" : null;

    /// <summary>The marks' last words and what sounded (the status op, tests).</summary>
    public IReadOnlyList<(string Name, string Mark, double Bearing, double Dist)>? LastMarks { get; private set; }

    /// <summary>O: doors, seats and touchable things within 12 m, said and sounded one after another going round from
    /// ahead to the right, as the sonar goes: a door knocks, a seat sinks, a touchable thing taps, each from where it is.</summary>
    public async Task MarksAsync()
    {
        if (Me() is not { } b) { Line("Not in a region yet."); return; }
        float facing = Facing(b);
        var seat = b.Linkset;
        var things = (await _ops.ObjectsAsync(MarksRange, 40))
            .Select(t => (t, mark: MarkOf(t)))
            .Where(x => x.mark != null && (seat == null || S.Region?.PrimById(x.t.Id) is not { } p || !seat.Contains(p.LocalID)))
            .Take(8)
            .Select(x => (x.t, x.mark!, bearing: Bearings.Norm(Bearings.Compass(b.Pos.X, b.Pos.Y, x.t.Pos.X, x.t.Pos.Y) - Compass(facing))))
            .OrderBy(x => x.bearing)
            .ToList();
        LastMarks = things.Select(x => (x.t.Name, x.Item2, x.bearing, x.t.Dist)).ToList();
        if (things.Count == 0) { Line($"No doors, seats or touchable things within {Bearings.Metres(MarksRange)}."); return; }
        if (_set.WalkText("marks"))
            Line(string.Join(" ", things.Select(x => $"{x.t.Name}, {(x.Item2 == "door" ? "a door" : x.Item2 == "seat" ? "a seat" : "touch")}, "
                + $"{Bearings.Metres(x.t.Dist)}, {Bearings.Clock(x.bearing)}.")));
        if (!_set.WalkSound("marks")) return;
        float level = _set.WalkLevel("marks");
        foreach (var (t, mark, _) in things)
        {
            var cue = mark == "door" ? CueSounds.Cue.Door : mark == "seat" ? CueSounds.Cue.MarkSeat : CueSounds.Cue.MarkTouch;
            if (Toward(t.Pos) is { } at) Sounds.Play(cue, level * (1 - 0.3f * (float)Math.Clamp(t.Dist / MarksRange, 0, 1)), at.At);
            await Task.Delay(380);
        }
    }

}
