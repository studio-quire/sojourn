using LibreMetaverse;
using Sojourn.Client.Xr;
using Sojourn.Render.Realtime;
using LVector3 = LibreMetaverse.Vector3;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Control;

/// <summary>
/// Looking around (the user's request, 2026-10-26): the avatar stays where it is and you move on your own, as the
/// viewer's free camera lets you (alt-cam, flycam), to look around a place. It starts flying, a free camera through
/// anything, the ground included (the user, 2026-10-26): the stick goes where you look (or point), up and down too,
/// A and the right stick climb and sink, the stick's click is faster; B lands you to walk on floors (through walls
/// still) and back. Turning is as ever; the avatar is drawn whole where it stands, as others see it. Nothing is
/// reconciled meanwhile: the avatar isn't walked, nothing puts you back. You stay within a region's width of it (the
/// viewer's DisableCameraConstraints lifts its 50 m limit the same way). The camera is sent to the region as the viewer
/// sends its free camera, so the region sends what's around where you look (its interest list follows the camera);
/// voice already hears at your eyes and speaks from the avatar, tethered within the same 50 m (Voice/VoiceClient.cs),
/// so it does as the viewer's "hear from the camera" does. Y held (0.6 s) starts it, Y (or the menu's switch, or the
/// assistant: SojournSettings look_around) ends it, putting you back on your avatar (a straight cut); so does the avatar
/// going elsewhere (a teleport, a sit). Pointing to teleport while looking around takes the avatar there, and then
/// you're back on it.
/// </summary>
public sealed partial class VrPilot
{
    bool _spectating, _ghostFly;
    Vector3 _spectateFrom;
    uint _spectateSeat;

    /// <summary>The camera tether: a region's width (the viewer's camera with DisableCameraConstraints).</summary>
    const float SpectateReach = 256;

    /// <summary>Looking around away from the avatar.</summary>
    public bool Spectating
    {
        get => _spectating;
        set
        {
            if (value == _spectating) return;
            // (waiting for a teleport's arrival the view holds: looking around neither starts nor moves it; it ends with
            // the arrival's cut, VrPilot.Arrival)
            if (_arriving != null) { if (!value) _spectating = false; return; }
            var c = _session.Client;
            if (value)
            {
                if (c is null || !_placed) return;
                _spectating = true;
                _ghostFly = true;
                _spectateFrom = new Vector3(c.Self.SimPosition.X, c.Self.SimPosition.Y, c.Self.SimPosition.Z);
                _spectateSeat = c.Self.SittingOn;
                _follow = Follow.Idle;
                _correcting = null;
                _walking = false;
                Release(keepPlace: true);
                _session.Notify.Add("LookAround", "notify", "Looking around. Y brings you back", Sojourn.Core.Agents.Notifications.None);
                Say?.Invoke("looking around (Y to come back)");
            }
            else
            {
                _spectating = false;
                // back on the avatar: the head over it, facing the way the avatar faces (not the way the free camera
                // last looked: the user, 2026-10-28), a straight cut
                if (c != null && _placed)
                {
                    var p = c.Self.SimPosition;
                    _yaw = _bodyYaw - Yaw(_lastRoomQ);
                    var headAt = _origin + Rotate(Flat(_lastRoom), _yaw);
                    _origin += new Vector3(p.X, p.Y, 0) - headAt;
                }
                _floorSet = false;
                _seatHead = null;
                Cut();
                Say?.Invoke("back on your avatar");
            }
        }
    }

    Vector3 _lastRoom;
    Quaternion _lastRoomQ = Quaternion.Identity;

    /// <summary>A test aid (VrScript's motion routine): turn the room about the head so the head looks along this
    /// heading of the region (degrees counter-clockwise from east).</summary>
    public void Face(float degrees)
    {
        var headAt = _origin + Rotate(Flat(_lastRoom), _yaw);
        _yaw = degrees * MathF.PI / 180 - Yaw(_lastRoomQ);
        _origin = headAt - Rotate(Flat(_lastRoom), _yaw);
    }
    double _yDownAt = -1;
    bool _yHeldFired;

    /// <summary>The Y button: null, or on release a short press (false), or once held 0.6 s (true).</summary>
    bool? YPress(XrControllers input, double now)
    {
        if (input.Y && !_prev.Y) { _yDownAt = now; _yHeldFired = false; }
        if (input.Y && _yDownAt >= 0 && !_yHeldFired && now - _yDownAt > 0.6) { _yHeldFired = true; return true; }
        if (!input.Y && _prev.Y && _yDownAt >= 0) { _yDownAt = -1; if (!_yHeldFired) return false; }
        return null;
    }

    VrView Spectate(GridClient c, Simulator sim, XrFrame frame, bool floorLevel, XrControllers input, int turnKeys, bool keyUp, bool keyDown, bool keyFly,
        Vector3 room, Quaternion roomQ, Vector3 headXr, Quaternion headQXr, Vector3 avatar, Vector3 raw, double now, double dt)
    {
        _lastRoom = room;
        var mv = c.Self.Movement;
        // the avatar went elsewhere (teleported, sat or stood): back on it
        if (Vector3.Distance(raw, _spectateFrom) > 3 || c.Self.SittingOn != _spectateSeat)
        {
            Spectating = false;
            _lastAvatar = avatar;
            _lastRaw = raw;
            return Update(frame, floorLevel, null, dt) ?? default;
        }

        if (input.B && !_prev.B || keyFly) { _ghostFly = !_ghostFly; Say?.Invoke(_ghostFly ? "flying (looking around)" : "walking on floors (looking around)"); }
        if (input.LeftStickClick && !_prev.LeftStickClick) _run = !_run;
        bool back = YPress(input, now) == false;
        _prev = input;
        if (back)
        {
            Spectating = false;
            return Update(frame, floorLevel, null, dt) ?? default;
        }

        TurnRoom(input, turnKeys, room, dt);
        var worldQ = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _yaw) * roomQ);
        float headYaw = Yaw(worldQ);
        float rise = 0;
        if (input.LeftStick.Length() > 0.25f)
        {
            var refQ = worldQ;
            if (Settings.HandRelative && input.LeftAim is { } aim) refQ = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _yaw) * ToSl(aim);
            var s = Vector2.Normalize(input.LeftStick) * Math.Min(1, input.LeftStick.Length());
            float refYaw = Yaw(refQ);
            if (_ghostFly)
            {
                // free camera: forward is wherever you look (or point), up and down included; sideways stays level
                float speed = _run ? 15f : 6f;
                var fwd3 = Vector3.Transform(Vector3.UnitX, refQ);
                var move = fwd3 * s.Y + Rotate(new Vector3(0, -s.X, 0), refYaw);
                _origin += new Vector3(move.X, move.Y, 0) * speed * (float)dt;
                rise = move.Z * speed;
            }
            else
                _origin += Rotate(new Vector3(s.Y, -s.X, 0), refYaw) * (_run ? 5.1f : 3.2f) * (float)dt;
        }
        int up = (input.A || keyUp ? 1 : 0) - (keyDown ? 1 : 0);
        if (MathF.Abs(input.RightStick.Y) > 0.5f) up = MathF.Sign(input.RightStick.Y);
        if (up > 0 && !_ghostFly) _ghostFly = true;   // climbing from the ground takes off, as the avatar's does

        // the tether, flat
        var headFlat = _origin + Rotate(Flat(room), _yaw);
        var off = headFlat - Flat(avatar);
        if (off.Length() > SpectateReach) { _origin -= off * (1 - SpectateReach / off.Length()); headFlat = _origin + Rotate(Flat(room), _yaw); }

        // height: flying, where you climb and sink to, through the ground too; walking, on whatever is under you,
        // terrain or a prim's top
        float floorNow = _floorSet ? _floor : avatar.Z - FootDepth(c, sim);
        if (_ghostFly)
        {
            _floor = floorNow + (up * (_run ? 10f : 4f) + rise) * (float)dt;
            _floorVel = 0;
            _floorSet = true;
        }
        else
        {
            float target = Support(sim, c.Self.LocalID, headFlat.X, headFlat.Y, floorNow + StepHeight + StepSlack, 60f) ?? TerrainAt(sim, headFlat.X, headFlat.Y) ?? floorNow;
            Floor(target, 0, true, dt);
        }
        _floor = Math.Clamp(_floor, avatar.Z - SpectateReach, avatar.Z + SpectateReach);
        float lift = Lift(c, sim, room.Z, floorLevel);
        var head = new Vector3(headFlat.X, headFlat.Y, _floor + lift + room.Z);
        _roomFloorZ = head.Z - room.Z;

        // the avatar holds still; the camera goes to the region as the viewer's free camera does
        var fwd = Vector3.Transform(Vector3.UnitX, worldQ);
        var upV = Vector3.Transform(Vector3.UnitZ, worldQ);
        mv.Camera.Far = _session.Camera.FarClip;
        var focus = head + fwd;
        mv.Camera.LookAt(new LVector3(head.X, head.Y, head.Z), new LVector3(focus.X, focus.Y, focus.Z));
        _session.State.CameraOverride = (new LVector3(head.X, head.Y, head.Z), new LVector3(focus.X, focus.Y, focus.Z));
        _sinceSend += dt;
        if (_sinceSend > 0.25) { mv.SendUpdate(false); _sinceSend = 0; }

        var inv = Quaternion.Conjugate(headQXr);
        var eyes = new EyeSpec[2];
        for (int i = 0; i < 2; i++)
        {
            var e = frame.Eyes[i];
            eyes[i] = new EyeSpec(Vector3.Transform(e.Position - headXr, inv), Quaternion.Normalize(inv * e.Orientation),
                e.TanLeft, e.TanRight, e.TanUp, e.TanDown);
        }
        _lastAvatar = avatar;
        _lastRaw = raw;
        Log?.WriteLine($"{now:F4},{room.X:F3},{room.Y:F3},{room.Z:F3},{head.X:F3},{head.Y:F3},{head.Z:F3},{avatar.X:F3},{avatar.Y:F3},{avatar.Z:F3}," +
                       $"Spectating,0,{_yaw * 180 / MathF.PI:F1},{_bodyYaw * 180 / MathF.PI:F1},{raw.X:F3},{raw.Y:F3},{raw.Z:F3},0,0," +
                       $"{_origin.X:F5},{_origin.Y:F5},{_roomFloorZ:F5},{dt * 1000:F3},{input.LeftStick.Length():F2},0,{up},-,1");
        return new VrView(sim.Handle, head, fwd, upV, eyes, null, Spectating: true);
    }
}
