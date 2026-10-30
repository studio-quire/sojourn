using LibreMetaverse;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Control;

/// <summary>
/// Crossing a region's border at speed (Claude Opus 5.5, 2026-10-28; docs/vr.md > The rig > Crossing at speed). The
/// region's word on the avatar is unreliable for a moment there:
/// - The new region's first full update for the avatar says it's standing still at the handover point. OpenSim makes
///   the avatar physical only when the viewer's CompleteAgentMovement comes, giving it its momentum back for the next
///   physics step, and its full update is made in between (self trace, 2026-10-28: the update's velocity 0 at the
///   handover point, then 47 ms later a terse update 0.9 m on at full speed). Read as the truth, server movement glided
///   the view back 2-2.4 m toward that point (a cut where it was over a metre in a frame) and predicted movement took it
///   for the region stopping the avatar (a wall: the view held at the border while the avatar flew on, then a put-back
///   over 21-43 m). The region's positions for the walk's judgement were also still in the old region's coordinates.
/// - The handover position can be off the old trajectory (OpenSim's was 1.3 m ahead of it), and LibreMetaverse writes
///   it again once it has connected (on the main grid a round trip after the region handed the avatar over, with the
///   old velocity), so the drawn avatar can lurch either way by a metre or two.
/// So for <see cref="CrossingWindow"/> after a crossing while moving: a still report keeps the velocity from before
/// (the position is taken) until a moving one has come after it. Predicted movement doesn't judge walls by the region's
/// word, take a step back for a teleport or let the servo slow the walk for how far the region fell behind; that slack
/// is given up afterwards at a steady 15% of the speed plus 0.3 m/s (CrossingGive). In server movement the room rides a
/// follower of the drawn avatar instead of the drawing itself: it moves at the region's velocity and eases onto the
/// drawing at most a quarter of the speed (plus 0.3 m/s) faster or slower. Either way a lurch becomes a short change
/// of speed, never a step back, a stop or a cut. SOJOURN_CROSSING_FIX=0 turns it all off for comparing.
/// </summary>
public sealed partial class VrPilot
{
    const double CrossingWindow = 0.8;
    static readonly bool NoCrossingFix = Environment.GetEnvironmentVariable("SOJOURN_CROSSING_FIX") == "0";
    double _xingAt = -100;
    Vector3 _xingVel;         // the velocity before the crossing
    bool _xingStillSeen, _xingDone;
    Vector3? _ride;           // server movement: where the room rides while it eases onto the drawn avatar
    Vector3 _rideVel;
    bool InCrossing(double now) => now - _xingAt < CrossingWindow;

    /// <summary>The region's word on the avatar as the rig takes it: across a crossing at speed, a still report keeps
    /// the velocity it had.</summary>
    (Vector3 Pos, Vector3 Vel) CrossingWord(Simulator sim, Vector3 pos, Vector3 vel, bool arrived, bool sitting, double now)
    {
        if (NoCrossingFix) return (pos, vel);
        if (_placed && _arriving == null && !arrived && sim.Handle != _region && !sitting
            && Neighbours(RegionOffset(_region, sim.Handle), _regionSize, new System.Numerics.Vector2(sim.SizeX, sim.SizeY)))
        {
            _xingAt = now;
            _xingVel = _lastRawVel;
            _xingStillSeen = _xingDone = false;
            if (Flat(_xingVel).Length() > 1f)
                Say?.Invoke($"crossing into {sim.Name} at {Flat(_xingVel).Length():F1} m/s");
        }
        if (!InCrossing(now) || _xingDone || Flat(_xingVel).Length() < 1f || !_wasMoving && _lastUp == 0) return (pos, vel);
        if (Flat(vel).Length() < 0.05f)
        {
            if (!_xingStillSeen) Say?.Invoke($"crossing: the region reported you still at the handover ({pos.X:F1}, {pos.Y:F1}); carrying on at {Flat(_xingVel).Length():F1} m/s");
            _xingStillSeen = true;
            return (pos, _xingVel with { Z = vel.Z });
        }
        if (_xingStillSeen) _xingDone = true;   // moving again: the region's word is its own from here
        return (pos, vel);
    }

    float _xingSlack;

    /// <summary>Predicted movement: how much of how far the walk runs ahead of the region's avatar
    /// (<paramref name="ahead"/>) is the crossing's doing, and so isn't for the walk's servo, which would stop the view
    /// at the border for the region to catch up: across the window, whatever exceeds the usual allowance; after it,
    /// never more than the excess still there, and given up by CrossingGive.</summary>
    float CrossingSlack(float ahead, double now)
    {
        if (NoCrossingFix) return 0;
        if (InCrossing(now) && Flat(_xingVel).Length() > 1f) _xingSlack = MathF.Max(_xingSlack, ahead - 0.7f);
        else _xingSlack = Math.Clamp(_xingSlack, 0, MathF.Max(0, ahead - 0.7f));
        return _xingSlack;
    }

    /// <summary>How far to hold the walk back this frame to give up the crossing's slack: a steady 15% of the speed plus
    /// 0.3 m/s (2.2 m/s at OpenSim's 12.6 m/s flight) until it's gone, so the view slows by that much for a second or so
    /// and the servo sees nothing of it. (At a quarter of the speed it was 3.4 m/s for about 0.9 s in the simulator;
    /// left to the servo, a dip to 5.5 m/s.)</summary>
    float CrossingGive(double now, double dt)
    {
        if (InCrossing(now) || _xingSlack <= 0) return 0;
        float give = MathF.Min(_xingSlack, (0.15f * Flat(_xingVel).Length() + 0.3f) * (float)dt);
        _xingSlack -= give;
        return give;
    }

    /// <summary>A crossing's shift of coordinates for what the walk keeps in the region's frame.</summary>
    void ShiftForCrossing(Vector3 off)
    {
        var trail = _trail.ToArray();
        _trail.Clear();
        foreach (var (at, raw, dir) in trail) _trail.Enqueue((at, raw + off, dir));
        for (int i = 0; i < _refused.Count; i++) _refused[i] = _refused[i] with { At = _refused[i].At + off };
        _blockedAt += off;
        if (_ride is { } r) _ride = r + off;
    }

    /// <summary>Server movement: this frame's carry for the room while it rides a follower of the drawn avatar after a
    /// crossing at speed (null: ride the drawing itself). <paramref name="moved"/> is the drawing's own move this
    /// frame.</summary>
    Vector3? Ride(Vector3 avatar, Vector3 rawVel, Vector3 moved, double now, double dt)
    {
        if (NoCrossingFix) return null;
        var target = Flat(avatar);
        if (_ride == null)
        {
            if (!InCrossing(now) || now - _xingAt > 0.05 || Flat(_xingVel).Length() < 1f) return null;
            // from where the room rode last frame, at the speed it had
            _ride = target - Flat(moved);
            _rideVel = Flat(_xingVel);
        }
        float t = (float)Math.Max(dt, 1e-4);
        var refV = Flat(rawVel);
        var err = target - _ride.Value;
        float cap = 0.25f * refV.Length() + 0.3f;
        var corr = err / 0.35f;
        if (corr.Length() > cap) corr *= cap / corr.Length();
        _rideVel += (refV + corr - _rideVel) * (1f - MathF.Exp(-t / 0.12f));
        var step = _rideVel * t;
        _ride += step;
        if (!InCrossing(now) && Vector3.Distance(_ride.Value, target) < 0.03f && Vector3.Distance(_rideVel, refV) < 0.3f)
        {
            step += target - _ride.Value;
            _ride = null;
        }
        return step;
    }
}
