using LibreMetaverse;
using Sojourn.Client.Xr;
using Sojourn.Render.World;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Control;

/// <summary>
/// Seated, or with a script holding the controls: the sticks are the viewer's movement keys, as vehicles, horses,
/// boats, planes and games that take controls (llTakeControls) read them. The left stick forward and back is W and S
/// (AT_POS / AT_NEG: CONTROL_FWD, CONTROL_BACK), left and right is A and D (YAW_POS / YAW_NEG: CONTROL_ROT_LEFT,
/// CONTROL_ROT_RIGHT, how the viewer's keys steer outside mouselook); the right stick up and down, or A, is E and C
/// (UP_POS / UP_NEG). The right stick still turns you, and the view turns with what you sit on, so a car that turns
/// takes you with it. Nothing walks the room meanwhile: what moves the avatar is the region (Carry).
/// </summary>
public sealed partial class VrPilot
{
    const float DriveDead = 0.3f;

    /// <summary>The sticks drive instead of walking: seated on something, or a script keeps movement controls (taken
    /// without passing them on, so the avatar wouldn't walk on them anyway).</summary>
    bool Driving(GridClient c, bool sitting) =>
        sitting || (_session.ControlsKept & (ScriptControlChange.Forward | ScriptControlChange.Back | ScriptControlChange.Left |
                                            ScriptControlChange.Right | ScriptControlChange.RotateLeft | ScriptControlChange.RotateRight)) != 0;

    static bool DriveHeld(XrControllers input) =>
        MathF.Abs(input.LeftStick.X) > DriveDead || MathF.Abs(input.LeftStick.Y) > DriveDead;

    /// <summary>The left stick as the movement keys (the up and down keys are set with the rest, from the right stick and A).</summary>
    static void Drive(AgentManager.AgentMovement mv, XrControllers input)
    {
        var s = input.LeftStick;
        mv.AtPos = s.Y > DriveDead; mv.AtNeg = s.Y < -DriveDead;
        mv.FastAt = mv.AtPos || mv.AtNeg;   // a held key, as LLAgent::moveAt sends it
        mv.NudgeAtPos = false;
        mv.YawPos = s.X < -DriveDead; mv.YawNeg = s.X > DriveDead;
        mv.TurnLeft = mv.YawPos; mv.TurnRight = mv.YawNeg;
    }

    double _standPush;   // how long the stick has been pushed seated; -1 once that stood you up, until it's let go

    /// <summary>The stick stood you up and hasn't been let go since: it doesn't walk yet.</summary>
    bool StoodByStick => _standPush < 0;

    /// <summary>Seated on something that hasn't taken the movement keys (a chair, a windowsill, a pose ball), the left
    /// stick pushed most of the way for a moment stands you up (the user's session of 2026-10-26: there was no way to
    /// stand without the assistant; the forearm's stand key, Control/StandKey.cs, works on anything).</summary>
    void StandByStick(GridClient c, XrControllers input, bool sitting, double dt)
    {
        const ScriptControlChange keys = ScriptControlChange.Forward | ScriptControlChange.Back | ScriptControlChange.Left |
            ScriptControlChange.Right | ScriptControlChange.RotateLeft | ScriptControlChange.RotateRight | ScriptControlChange.Up | ScriptControlChange.Down;
        if (input.LeftStick.Length() < 0.3f) _standPush = 0;
        if (!sitting || (_session.ControlsTaken & keys) != 0 || input.LeftStick.Length() < 0.7f) return;
        if (_standPush < 0) return;   // stood already: wait for the stick to come back
        _standPush += dt;
        if (_standPush > 0.35)
        {
            _standPush = -1;
            if (_session.RlvActive && _session.RlvRefused(_session.Rlv.CanStand()) != null) return;   // RLV's @unsit
            c.Self.Stand();
            Say?.Invoke("standing up");
        }
    }

    uint _seatId;
    float? _seatYaw;
    long _seatSeenAt;

    /// <summary>The seated avatar's root (its pelvis, region frame) and rotation this frame: on its seat where the
    /// renderer draws the seat, carried on between the region's updates (MotionSmoother.Prims.cs), so riding a vehicle
    /// the view moves as smoothly as the vehicle is drawn. Null if the seat or the avatar isn't known.</summary>
    (Vector3 Pos, Quaternion Rot)? SeatPose(GridClient c, Simulator sim)
    {
        if (c.Self.SittingOn == 0 || _session.Region is not { } w || !sim.ObjectsAvatars.TryGetValue(c.Self.LocalID, out var me)) return null;
        return _motion.Seated(id => w.Prims.TryGetValue(id, out var p) ? p : null, me, sim.Handle,
            sim.Stats.Dilation is > 0 and var d ? d : 1f, sim.SizeX > 0 ? sim.SizeX : 256f);
    }

    /// <summary>Turn the room with the seat: what the avatar sits on turned since last frame, so the view does too,
    /// about the head.</summary>
    void TurnWithSeat(GridClient c, Vector3 room, Quaternion? seatRot)
    {
        uint seat = c.Self.SittingOn;
        if (seat == 0 || seatRot is not { } sr)
        {
            _seatYaw = null;
            return;
        }
        var f = Vector3.Transform(Vector3.UnitX, sr);
        float yaw = MathF.Atan2(f.Y, f.X);
        long now = Environment.TickCount64;
        bool fresh = now - _seatSeenAt > 250;   // sat down again since: no turn to follow yet
        _seatSeenAt = now;
        if (seat == _seatId && !fresh && _seatYaw is { } was)
        {
            float d = AngleDiff(yaw, was);
            if (d != 0)
            {
                var headAt = _origin + Rotate(Flat(room), _yaw);
                _yaw += d;
                _bodyYaw += d;
                _origin = headAt - Rotate(Flat(room), _yaw);
            }
        }
        _seatId = seat;
        _seatYaw = yaw;
    }
}
