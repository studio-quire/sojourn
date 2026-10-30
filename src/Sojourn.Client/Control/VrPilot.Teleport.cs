using LibreMetaverse;
using LVector3 = LibreMetaverse.Vector3;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Control;

/// <summary>
/// Teleports the headset asks for. Two kinds:
/// - The fail-safe (the user's call, 2026-10-26: teleporting is the quick way to make the region agree): when the
///   avatar can't be walked to where you see yourself (VrPilot's follow gave up, the stick walk ended against
///   something, a flying glide left it far off), it's teleported there, within the region, instead of the view being
///   put back on it. Only where that's sure to land where asked: not seated, the region allows direct teleports, the
///   parcel there takes them anywhere (or is yours), no wall between the avatar and you (walking into a wall never
///   gets you through it), and teleports here have landed where asked so far. Otherwise, or if it fails, the view is
///   put back on the avatar as before (jump or slide).
/// - Pointing (the left trigger held on open ground, Control/PointTeleport.cs): the avatar goes where the ray lands, as
///   the viewer's double-click teleport does, its landing rules included.
/// A teleport that lands far from where it was asked (a landing point, a telehub) turns the fail-safe off for that
/// region; the room goes with the avatar as for any teleport (VrPilot's Carry).
/// </summary>
public sealed partial class VrPilot
{
    (Vector3 Target, double At, bool FailSafe)? _tpAsked;
    ulong _tpRefusedRegion;
    double _tpLastAt = -100, _tpLandedAt = -100;

    /// <summary>A teleport the headset asked for is under way.</summary>
    public bool Teleporting => _tpAsked != null;

    /// <summary>Why the fail-safe can't teleport the avatar to <paramref name="target"/> in this region, or null.</summary>
    string? TeleportRefusal(GridClient c, Simulator sim, Vector3 target)
    {
        if (c.Self.SittingOn != 0) return "seated";
        if (_tpRefusedRegion == sim.Handle) return "teleports here don't land where asked";
        if ((sim.Flags & RegionFlags.AllowDirectTeleport) == 0) return "the region sends teleports to its telehub";
        if (target.X < 0.5f || target.Y < 0.5f || target.X > sim.SizeX - 0.5f || target.Y > sim.SizeY - 0.5f) return "the spot is in another region";
        int local = 0;
        try { local = c.Parcels.GetParcelLocalID(sim, new LVector3(target.X, target.Y, target.Z)); } catch { }
        if (local == 0 || !sim.Parcels.TryGetValue(local, out var parcel))
        {
            // ask, for next time
            try { c.Parcels.RequestParcelProperties(sim, target.Y + 1, target.X + 1, target.Y - 1, target.X - 1, 0, false); } catch { }
            return "the parcel there isn't known yet";
        }
        if (parcel.Landing != LandingType.Direct && parcel.OwnerID != c.Self.AgentID) return "the parcel sends teleports to its landing point";
        return null;
    }

    /// <summary>The avatar can't be walked to the head: teleport it there if that's sure to work, else put the view
    /// back on it (Correct).</summary>
    void PutBack(GridClient c, Simulator sim, Vector3 gap, Vector3 head, double now)
    {
        if (Settings.TeleportBack && now - _tpLastAt > 5)
        {
            float footDepth = FootDepth(c, sim);
            var avatar = new Vector3(c.Self.SimPosition.X, c.Self.SimPosition.Y, c.Self.SimPosition.Z);
            float feetHere = Flying ? head.Z - (AvatarEyeHeight ?? 1.6f) : _floor - _lift;
            var target = new Vector3(head.X, head.Y, feetHere + footDepth + 0.05f);
            string? why = TeleportRefusal(c, sim, target)
                ?? (WallBetween(sim.Handle, c.Self.LocalID, avatar, head, avatar.Z - footDepth, feetHere) ? "there's a wall between" : null);
            if (why == null)
            {
                Say?.Invoke($"teleporting your avatar to you ({Flat(gap).Length():F1} m)");
                Teleport(c, sim.Handle, target, now, failSafe: true);
                return;
            }
            Say?.Invoke($"not teleporting ({why})");
        }
        Correct(gap, now);
    }

    void Teleport(GridClient c, ulong region, Vector3 target, double now, bool failSafe)
    {
        var fwd = Vector3.Transform(Vector3.UnitX, System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _bodyYaw));
        c.Self.RequestTeleport(region, new LVector3(target.X, target.Y, target.Z), new LVector3(target.X + fwd.X, target.Y + fwd.Y, target.Z));
        _tpAsked = (target, now, failSafe);
        _tpLastAt = now;
    }

    /// <summary>Teleport the avatar to a point the person pointed at (on a region's ground or a floor, in this frame's
    /// region coordinates; <paramref name="hitRegion"/> is the region it's in, maybe a neighbour).</summary>
    public string? TeleportToPoint(Vector3 point, ulong hitRegion)
    {
        var c = _session.Client;
        if (c is null || c.Network.CurrentSim is not { } sim || !_placed) return "not in a region";
        if (_tpAsked != null) return "a teleport is under way";
        double now = _clock.Elapsed.TotalSeconds;
        var local = point + RegionOffset(_region, hitRegion);   // into the hit region's own coordinates
        float footDepth = FootDepth(c, sim);
        Say?.Invoke($"teleporting to where you pointed ({local.X:F0}, {local.Y:F0}, {local.Z:F0})");
        var at = local + new Vector3(0, 0, footDepth + 0.1f);
        if (c.Self.SittingOn != 0)
        {
            // stand first (a seated avatar isn't teleported), then go once the region has it standing, as the way out does
            c.Self.Stand();
            _ = Task.Delay(300).ContinueWith(_ => c.Self.RequestTeleport(hitRegion, new LVector3(at.X, at.Y, at.Z), new LVector3(at.X + 1, at.Y, at.Z)));
        }
        else Teleport(c, hitRegion, at, now, failSafe: false);
        // the arrival check works in this region's frame
        _tpAsked = (point + new Vector3(0, 0, footDepth + 0.1f), now, false);
        _tpLastAt = now;
        return null;
    }

    /// <summary>Each frame: has the asked-for teleport landed, landed elsewhere, or come to nothing?</summary>
    void WatchTeleport(Vector3 raw, Vector3 rawJump, Vector3 gap, double now)
    {
        if (_tpAsked is not { } tp) return;
        float off = Vector3.Distance(Flat(raw), Flat(tp.Target));
        if (off < 1.5f && MathF.Abs(raw.Z - tp.Target.Z) < 4f)
        {
            _tpAsked = null;
            _tpLandedAt = now;
            _follow = Follow.Idle;
            _gaveUpAt = null;
            _blockedWalk = false;
            return;
        }
        if (rawJump.Length() > 2f && off > 3f)
        {
            // landed somewhere else (a landing point, a telehub): the fail-safe stays off here
            if (tp.FailSafe) _tpRefusedRegion = _region;
            Say?.Invoke($"the teleport landed {off:F0} m from where it was asked");
            _tpAsked = null;
            return;
        }
        if (now - tp.At > 6)
        {
            // refused or lost: put the view back instead
            _tpAsked = null;
            if (tp.FailSafe)
            {
                _tpRefusedRegion = _region;
                Correct(gap, now);
            }
        }
    }
}
