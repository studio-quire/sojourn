using LibreMetaverse;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// "Keep me in place" (the keep_in_place setting, off by default): Firestorm's movelock without its LSL bridge.
/// Firestorm's holds the avatar where it stands with a script in a worn object (llMoveToTarget, fslslbridge.cpp), let go
/// while the person moves and taken up again where they stop (RelockMoveLockAfterMovement, ...AfterRegionChange); on
/// OpenSim it falls back to phantom. Sojourn wears nothing of its own, so the viewer's own means stand in: where the
/// avatar comes to rest by the person's doing (their keys or stick, the walk to them in the headset, a sit, a teleport,
/// the assistant walking them) is the place, and when something else moves it off by over half a metre (a push, an
/// orbiter, being shoved along) it's brought back once the shove is over.
/// <list type="bullet">
/// <item>In the headset the room isn't carried along by anything the person didn't do (<see cref="Holding"/>, read by
/// VrPilot.HoldRoom), so the view stays where it was and the rig's own walk to the head brings the avatar back (its
/// teleport to the head when walking can't), as it does after walking in the room; from over 8 m it's teleported back
/// at once, as below.</item>
/// <item>On the desktop it's walked back by the viewer's autopilot, or, failing that twice or from over 30 m, teleported
/// back within the region (at most every 5 s).</item>
/// </list>
/// It can't stop a push as a script would; it undoes it. A moving floor or an elevator counts as being moved too.
/// Claude Opus 5.5, 2026-10-27.
/// </summary>
public sealed class MoveLock
{
    readonly AgentSession _s;
    bool _on;
    Vector3? _anchor;
    ulong _anchorRegion;
    Vector3 _lastPos;
    double _lastOwn = -100, _returnEnded = -100, _lastTeleport = -100;
    volatile bool _returning;
    int _tries;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public MoveLock(AgentSession s) => _s = s;

    /// <summary>The headset's rig brings the avatar back to the view itself (the client sets it in the headset).</summary>
    public bool RigReturns { get; set; }

    /// <summary>In the headset, where the view is (its region, and there): the place, since the room holds still. From
    /// over 8 m (a walk would take long, or the rig gave up), the avatar is teleported to it.</summary>
    public Func<(ulong Region, Vector3 Pos)?>? ViewPlace { get; set; }

    public bool On
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            _anchor = null;
            if (value) HostClock.Subscribe(this, _ => _s.Guard("keep in place", Tick));
            else HostClock.Unsubscribe(this);
        }
    }

    /// <summary>On, standing, and the person hasn't moved themselves for a moment (the stick, keys, the walk to them; a
    /// coast after them): whatever moves the avatar now isn't theirs, so the headset doesn't carry the room along.</summary>
    public bool Holding =>
        _on && _s.Client is { Network.Connected: true } c && c.Self.SittingOn == 0
        && (_returning || (_clock.Elapsed.TotalSeconds - _lastOwn > 1.5 && !Moving(c.Self.Movement) && !_s.Motion.Moving));

    static Vector3 Flat(Vector3 v) => new(v.X, v.Y, 0);

    static bool Moving(AgentManager.AgentMovement mv) =>
        mv.AtPos || mv.AtNeg || mv.LeftPos || mv.LeftNeg || mv.UpPos || mv.UpNeg
        || mv.NudgeAtPos || mv.NudgeAtNeg || mv.NudgeLeftPos || mv.NudgeLeftNeg;

    /// <summary>The headset: the room holds still while it holds (Holding), so the view is the place. The rig's own walk to
    /// the head (which looks like the person's moving, so the anchor below can't be used) brings the avatar back from
    /// near; from far, once the shove is over, it's teleported to the view.</summary>
    void HeadsetTick(GridClient c, Simulator sim, Vector3 pos, float speed, double now)
    {
        if (ViewPlace?.Invoke() is not { } view || view.Region != sim.Handle) return;
        float off = Vector3.Distance(Flat(pos), Flat(view.Pos));
        if (off <= 8f || speed > 1.5f || now - _lastTeleport < 5 || Moving(c.Self.Movement) && !(c.Self.Movement.NudgeAtPos || c.Self.Movement.NudgeAtNeg
            || c.Self.Movement.NudgeLeftPos || c.Self.Movement.NudgeLeftNeg)) return;   // (the stick's walk is the person's)
        if ((sim.Flags & RegionFlags.AllowDirectTeleport) == 0) return;
        _lastTeleport = now;
        _s.Log.LogInformation("keep in place: teleporting back to the view {Off:F1} m", off);
        var fwd = Vector3.UnitX * c.Self.SimRotation;
        var to = new Vector3(view.Pos.X, view.Pos.Y, pos.Z);
        c.Self.RequestTeleport(sim.Handle, to, to + fwd);
    }

    void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (!_on || _s.Client is not { Network.Connected: true } c || c.Network.CurrentSim is not { } sim) { _anchor = null; return; }
        var pos = c.Self.SimPosition;
        float speed = Flat(c.Self.Velocity).Length();
        if (_returning && !_s.Motion.Moving) { _returning = false; _returnEnded = now; }
        if (c.Self.SittingOn != 0) { _anchor = null; _lastOwn = now; return; }
        // the person's own moving (keys, the stick, the walk to them, the assistant walking them): wherever it ends is the place
        if (!_returning && (Moving(c.Self.Movement) || _s.Motion.Moving)) _lastOwn = now;
        if (RigReturns) { HeadsetTick(c, sim, pos, speed, now); return; }
        bool jumped = Vector3.Distance(Flat(pos), Flat(_lastPos)) > 3f && speed < 1f;   // a teleport: no speed carried it there
        _lastPos = pos;
        // (the place follows the avatar while the person moves it, and for a moment after while it coasts to a stop)
        if (_anchor is not { } anchor || sim.Handle != _anchorRegion || jumped || now - _lastOwn < 1.5)
        {
            _anchor = pos;
            _anchorRegion = sim.Handle;
            _tries = 0;
            return;
        }
        float off = Vector3.Distance(Flat(pos), Flat(anchor));
        if (off < 0.5f) { if (off < 0.3f) _tries = 0; return; }
        if (_returning || speed > 1.5f || now - _returnEnded < 1.0) return;   // still being shoved, or settling
        if (off > 60f) { _anchor = pos; return; }   // flung far: this is the place now
        Utils.LongToUInts(sim.Handle, out var gx, out var gy);
        if (off <= 30f && _tries < 2)
        {
            _tries++;
            _returning = true;
            _s.Log.LogInformation("keep in place: walking back {Off:F1} m", off);
            _s.Motion.StartAutoPilot(c, new Vector3d(gx + anchor.X, gy + anchor.Y, anchor.Z), 0.25, null);
            return;
        }
        if (now - _lastTeleport < 5) return;
        if ((sim.Flags & RegionFlags.AllowDirectTeleport) == 0)
        {
            _s.Log.LogInformation("keep in place: {Off:F1} m off and the region doesn't allow teleporting back; this is the place now", off);
            _anchor = pos;
            return;
        }
        _lastTeleport = now;
        _tries = 0;
        _s.Log.LogInformation("keep in place: teleporting back {Off:F1} m", off);
        var fwd = Vector3.UnitX * c.Self.SimRotation;
        c.Self.RequestTeleport(sim.Handle, anchor, anchor + fwd);
    }
}
