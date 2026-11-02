using LibreMetaverse;
using Silk.NET.Input;
using Sojourn.Core.Agents;
using Sojourn.Render.World;
using LQuaternion = LibreMetaverse.Quaternion;
using LVector3 = LibreMetaverse.Vector3;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Control;

public enum CameraMode { ThirdPerson, FirstPerson }

/// <summary>
/// The person's hands on the avatar and the camera: keys move the body through AgentUpdate control flags (as the
/// viewer's keyboard does), the mouse turns body and camera together, and the camera follows a smoothed copy of
/// where the region says the avatar is (dead-reckoned between updates, so it doesn't step at the update rate).
///
/// Keys: W/S or arrows walk, A/D strafe, Q/E or left/right arrows turn, Shift runs, Space jumps (up when flying),
/// C down, F or Home toggles flying, V first/third person, mouse wheel zooms, right mouse button (or first person)
/// steers with the mouse, Esc frees the mouse.
/// </summary>
public sealed class Pilot
{
    readonly AgentSession _session;
    readonly MotionSmoother _motion;
    public CameraMode Mode { get; set; } = CameraMode.ThirdPerson;
    public float Yaw;      // radians, body heading in the region frame (0 = east, +Y north)
    public float Pitch;    // radians, camera tilt (+ = looking up)
    public float Distance = 3.2f;
    public float MouseSensitivity = 0.0035f;
    public float TurnRate = 2.0f;   // radians a second for the turn keys
    bool _yawFromBody = true, _heldBefore, _steered;
    LQuaternion? _bodySet;   // the body rotation this pilot last sent
    uint _lastControls;
    bool _lastRun;
    double _sinceSend;

    /// <summary>SOJOURN_MOTION_LOG=path: per frame, the own avatar's reported and drawn position (checking smoothness).</summary>
    static readonly StreamWriter? MotionLog = DiagLog.Open("SOJOURN_MOTION_LOG");

    public Pilot(AgentSession session, MotionSmoother motion)
    {
        _session = session;
        _motion = motion;
        _motion.RegionAt ??= new VrPilot.KnownRegions(session).At;
    }

    public bool Flying => _session.Client?.Self.Movement.Fly ?? false;

    public void ToggleFly()
    {
        if (_session.Client is not { } c) return;
        bool fly = !c.Self.Movement.Fly;
        if (fly && _session.RlvActive && _session.RlvRefused(_session.Rlv.CanFly()) != null) return;   // RLV's @fly
        c.Self.Fly(fly);
        c.Self.Movement.Fly = fly;
    }

    public void ToggleMode() => Mode = Mode == CameraMode.ThirdPerson ? CameraMode.FirstPerson : CameraMode.ThirdPerson;

    public void Look(Vector2 mouseDelta)
    {
        if (mouseDelta == Vector2.Zero) return;
        Yaw -= mouseDelta.X * MouseSensitivity;
        Pitch = Math.Clamp(Pitch - mouseDelta.Y * MouseSensitivity, -1.45f, 1.45f);
        _yawFromBody = false;
        _steered = true;
    }

    static float YawOf(LQuaternion q)
    {
        var f = LVector3.UnitX * q;
        return MathF.Atan2(f.Y, f.X);
    }

    public void Zoom(float wheel) => Distance = Math.Clamp(Distance * MathF.Pow(0.88f, wheel), 0.6f, 40f);

    /// <summary>Apply the held keys to the avatar (control flags, body rotation, run) and send an AgentUpdate when
    /// anything changed, or every 100 ms while something is held. Returns the camera for this frame.</summary>
    public CameraPose? Update(IKeyboard? kb, double dt, float vfov)
    {
        var c = _session.Client;
        if (c is null || !c.Network.Connected || c.Network.CurrentSim is not { } sim) return null;
        var mv = c.Self.Movement;
        if (_yawFromBody && c.Self.SimRotation != LQuaternion.Identity)
        {
            // start facing where the region put us
            Yaw = YawOf(c.Self.SimRotation);
            _yawFromBody = false;
        }

        bool Down(params Key[] keys) => kb != null && keys.Any(kb.IsKeyPressed);
        int forward = (Down(Key.W, Key.Up) ? 1 : 0) - (Down(Key.S, Key.Down) ? 1 : 0);
        int strafe = (Down(Key.D) ? 1 : 0) - (Down(Key.A) ? 1 : 0);   // + = right
        int turn = (Down(Key.Q, Key.Left) ? 1 : 0) - (Down(Key.E, Key.Right) ? 1 : 0);   // + = left (counter-clockwise)
        int up = (Down(Key.Space) ? 1 : 0) - (Down(Key.C, Key.PageDown) ? 1 : 0);
        bool run = Down(Key.ShiftLeft, Key.ShiftRight) && !(_session.RlvActive && _session.Rlv.TempRunBlocked);   // (RLV's @temprun)
        bool held = forward != 0 || strafe != 0 || up != 0 || turn != 0;
        bool rotated = false;

        // the agent API (TesselAgent move, LLAgent autopilot) drives the body while it runs and no key is held:
        // the camera follows the body then, instead of the keys steering it
        if (_session.Motion.Moving && !held)
        {
            if (!_steered) { Yaw = YawOf(mv.BodyRotation); _yawFromBody = false; }
        }
        else
        {
            // the body turned by someone else since this pilot last turned it (lookAt's short move ending between two
            // of our frames, the session's first facing): the view follows it rather than turning it back; seeing
            // Moving alone missed turns whenever the frame loop was slower than the move (2026-10-29)
            if (!held && !_steered && mv.BodyRotation != (_bodySet ?? LQuaternion.Identity))
            {
                Yaw = YawOf(mv.BodyRotation);
                _yawFromBody = false;
            }
            Yaw += turn * TurnRate * (float)dt;
            if (turn != 0) _yawFromBody = false;
            if (held || _heldBefore)
            {
                mv.AtPos = forward > 0; mv.AtNeg = forward < 0; mv.FastAt = forward != 0;
                mv.LeftPos = strafe < 0; mv.LeftNeg = strafe > 0; mv.FastLeft = strafe != 0;
                if (up > 0 && !Flying && _session.RlvActive && _session.Rlv.CanJump() != null) up = 0;   // RLV's @jump
                mv.UpPos = up > 0; mv.UpNeg = up < 0; mv.FastUp = up != 0;
                mv.TurnLeft = turn > 0; mv.TurnRight = turn < 0;
                mv.YawPos = turn > 0; mv.YawNeg = turn < 0;
            }
            var body = LQuaternion.CreateFromAxisAngle(LVector3.UnitZ, Yaw);
            rotated = body != mv.BodyRotation;
            mv.BodyRotation = body;
            _bodySet = body;
            // the head follows the camera's pitch (the viewer sends the camera's look as the head rotation)
            mv.HeadRotation = body * LQuaternion.CreateFromAxisAngle(LVector3.UnitY, -Pitch);
        }
        _heldBefore = held;
        _steered = false;
        mv.Mouselook = Mode == CameraMode.FirstPerson;
        if (run != _lastRun) { mv.AlwaysRun = run; _lastRun = run; }

        var pose = Camera(c, sim, dt, vfov);
        mv.Camera.Far = _session.Camera.FarClip;
        var focus = pose.Position + pose.Forward;
        mv.Camera.LookAt(new LVector3(pose.Position.X, pose.Position.Y, pose.Position.Z), new LVector3(focus.X, focus.Y, focus.Z));
        _session.State.CameraOverride = (new LVector3(pose.Position.X, pose.Position.Y, pose.Position.Z), new LVector3(focus.X, focus.Y, focus.Z));

        _sinceSend += dt;
        uint controls = mv.AgentControls;
        if (controls != _lastControls || (rotated && _sinceSend > 0.05) || (held && _sinceSend > 0.1) || _sinceSend > 0.5)
        {
            mv.SendUpdate(controls != _lastControls);
            _lastControls = mv.AgentControls;
            _sinceSend = 0;
        }
        return pose;
    }

    /// <summary>The avatar where the renderer draws it this frame (the shared dead reckoning), so the camera and
    /// the body agree; sitting, on its seat as it's drawn (a vehicle carried on between updates).</summary>
    Vector3 SmoothedAvatar(GridClient c, Simulator sim)
    {
        var raw = c.Self.SimPosition;
        var p = new Vector3(raw.X, raw.Y, raw.Z);
        if (c.Self.SittingOn != 0)
        {
            var w = _session.World.Region(_session.GridKey, sim.Handle);
            if (w != null && sim.ObjectsAvatars.TryGetValue(c.Self.LocalID, out var me)
                && _motion.Seated(id => w.Prims.TryGetValue(id, out var prim) ? prim : null, me, sim.Handle,
                    sim.Stats.Dilation is > 0 and var d ? d : 1f, sim.SizeX > 0 ? sim.SizeX : 256f) is { } seated)
                return seated.Pos;
            return p;
        }
        var vel = c.Self.Velocity;
        var shown = _motion.Position(c.Self.AgentID, sim.Handle, p, new Vector3(vel.X, vel.Y, vel.Z));
        MotionLog?.WriteLine($"{Environment.TickCount64},{p.X:F3},{p.Y:F3},{p.Z:F3},{shown.X:F3},{shown.Y:F3},{shown.Z:F3},{vel.X:F3},{vel.Y:F3},{vel.Z:F3}");
        return shown;
    }

    CameraPose Camera(GridClient c, Simulator sim, double dt, float vfov)
    {
        var at = SmoothedAvatar(c, sim);
        var look = new Vector3(MathF.Cos(Pitch) * MathF.Cos(Yaw), MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch));
        float distance = Distance;
        if (_session.RlvActive)
        {
            // RLV's camera limits on the desktop (docs/rlv.md > The camera): no mouselook while refused, the distance
            // from the avatar between @setcam_avdistmin and @setcam_avdistmax (0 max: first person only)
            var cam = _session.Rlv.Camera();
            if (Mode == CameraMode.FirstPerson && cam.NoMouselook) Mode = CameraMode.ThirdPerson;
            if (cam.AvDistMax is { } max && max <= 0.1f) Mode = CameraMode.FirstPerson;
            distance = Math.Clamp(distance, cam.AvDistMin ?? 0f, Math.Max(cam.AvDistMin ?? 0f, cam.AvDistMax ?? float.MaxValue));
        }
        if (Mode == CameraMode.FirstPerson)
        {
            var eye = at + new Vector3(0, 0, 0.72f) + new Vector3(MathF.Cos(Yaw), MathF.Sin(Yaw), 0) * 0.12f;
            return new CameraPose(sim.Handle, eye, look, HideSelf: true);
        }
        // third person: orbit behind the head at the zoom distance, kept above the ground
        var focus = at + new Vector3(0, 0, 0.55f);
        var pos = focus - look * distance + new Vector3(0, 0, 0.35f);
        var world = _session.World.Region(_session.GridKey, sim.Handle);
        if (world?.TerrainHeight(pos.X, pos.Y) is { } ground && pos.Z < ground + 0.4f) pos.Z = ground + 0.4f;
        var fwd = focus + look * 2.0f - pos;
        return new CameraPose(sim.Handle, pos, Vector3.Normalize(fwd), HideSelf: false);
    }

    /// <summary>Let go of every control (window lost focus, shutting down).</summary>
    public void Release()
    {
        if (_session.Client is not { } c) return;
        var mv = c.Self.Movement;
        mv.AtPos = mv.AtNeg = mv.LeftPos = mv.LeftNeg = mv.UpPos = mv.UpNeg = false;
        mv.FastAt = mv.FastLeft = mv.FastUp = false;
        mv.NudgeAtPos = mv.NudgeAtNeg = mv.NudgeLeftPos = mv.NudgeLeftNeg = mv.NudgeUpPos = mv.NudgeUpNeg = false;
        mv.YawPos = mv.YawNeg = mv.TurnLeft = mv.TurnRight = false;
        if (_lastRun) { mv.AlwaysRun = false; _lastRun = false; }
        if (c.Network.Connected) mv.SendUpdate(true);
        _lastControls = 0;
    }
}
