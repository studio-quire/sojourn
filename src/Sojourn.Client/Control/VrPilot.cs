using System.Diagnostics;
using LibreMetaverse;
using Silk.NET.Input;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using LQuaternion = LibreMetaverse.Quaternion;
using LVector3 = LibreMetaverse.Vector3;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Control;

/// <summary>
/// The person in the headset and their avatar. The play space (the room) is placed in the region by an origin and a
/// yaw; the eyes are wherever the headset puts them in it, so head tracking never waits on the network. The avatar's
/// position stays the region's (server-authoritative), and the two are tied this way:
/// - The left stick walks the avatar through the region (AgentUpdate controls, as the viewer's keys); the play space
///   rides along with the avatar's dead-reckoned position, so the camera moves as the body does. Letting go stops the
///   camera there and then; the avatar's coast after the stop (region physics, network) doesn't carry it on.
/// - Walking in the room moves only the camera. Once the head is more than ~0.45 m from the avatar, the avatar walks
///   under it; that walk doesn't move the play space (the camera stays put while the body catches up).
/// - Anything else that moves the avatar (falling, sitting, teleports, being pushed, the agent API) carries the play
///   space with it.
/// - Turning (snap, or smooth) turns the play space about the head.
/// Height: with a floor-level space the floor of the room is the avatar's feet, so eye height is the person's own.
/// SL is Z-up (x east, y north); OpenXR's spaces are Y-up with -z forward: (x, y, z)_xr = (-y, z, -x)_sl.
/// </summary>
public sealed partial class VrPilot
{
    enum Follow { Idle, Walking, Settling }

    readonly AgentSession _session;
    readonly MotionSmoother _motion;
    public VrSettings Settings { get; set; }

    bool _placed;
    Vector3 _origin;          // the play space's origin in the region (z unused: height comes from the feet)
    float _yaw;               // the play space's heading in the region (radians, counter-clockwise from east)
    ulong _region;
    Vector3 _lastAvatar, _lastRaw, _lastRawVel;
    double _rawChangedAt;     // when the region's last update came (VrPilot's dead reckoning)
    Vector3 _updRaw, _updVel;
    /// <summary>How long the region's last word is carried on by its velocity: a region sends little while an avatar
    /// moves steadily (OpenSim nothing for a second or more; SL every 60-110 ms), and at once when it changes.</summary>
    const double DeadReckonCap = 1.5;
    Follow _follow;
    double _followSince, _bestGap;
    float _coast = 0.5f;      // how far the avatar runs on after a follow walk stops (learned)
    Vector3 _stoppedAt, _stopDir;
    Vector2? _gaveUpAt;
    float _bodyYaw;
    bool _run, _snapArmed = true, _wasMoving, _learnCoast;
    float? _seatHead;         // the head's height in the room when the view was put on the seated avatar's eyes
    double? _seatPlacedAt;
    double _satAt;
    /// <summary>The renderer's seated eyes and facing (FrameRenderer.SelfSeatedEyes), set each frame.</summary>
    public (Vector3 Eyes, Vector3 Facing)? SeatedEyes { get; set; }
    /// <summary>The same eyes in the seated avatar's own frame (FrameRenderer.SelfSeatedEyesLocal), put on this frame's
    /// seat pose (SeatPose).</summary>
    public Vector3? SeatedEyesLocal { get; set; }
    /// <summary>How far over the agent's position a seated avatar's eyes are, until the renderer has drawn them.</summary>
    const float SeatedEyeGuess = 0.55f;
    XrControllers _prev;
    readonly HashSet<Key> _keysBefore = new();
    uint _lastControls;
    double _sinceSend;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    public bool Flying => _session.Client?.Self.Movement.Fly ?? false;
    /// <summary>Movement "server" (VrSettings.Movement): the view rides the region's avatar, no local walk or model.</summary>
    bool Server => Settings.Movement == MovementMode.Server;
    int _lastUp;              // last frame's climb (flying) or jump key: up 1, down -1
    Vector3 _crossOff;        // the last region crossing's shift of coordinates, and when
    double _crossedAt = -100;
    public bool Running { get => _run; set => _run = value; }
    /// <summary>Messages for the console (toggles the person made).</summary>
    public Action<string>? Say;

    public VrPilot(AgentSession session, MotionSmoother motion, VrSettings settings)
    {
        _session = session;
        _motion = motion;
        _motion.RegionAt ??= new KnownRegions(session).At;
        Settings = settings;
    }

    /// <summary>The own avatar's eye height above its feet (the renderer measures it); null until it has drawn it.</summary>
    public float? AvatarEyeHeight { get; set; }

    // heights: the person's standing eye height (the highest the head has been in the last 20 s, floor-level spaces),
    // and where their head was when seated mode began or they last recentred seated
    readonly float[] _standMax = new float[20];
    long _standSec = -1;
    float? _seatedRef;

    /// <summary>Seated mode or eye height changed: take the seated reference afresh next frame.</summary>
    public void HeightsChanged() => _seatedRef = null;

    float _roomFloorZ;

    /// <summary>The region this frame's room is placed in (0 before the first placed frame).</summary>
    public ulong Region => _placed ? _region : 0;

    /// <summary>A point of the play space (OpenXR) in the region's frame, as placed this frame.</summary>
    public Vector3 RoomToRegion(Vector3 xr)
    {
        var p = ToSl(xr);
        var flat = _origin + Rotate(Flat(p), _yaw);
        return new Vector3(flat.X, flat.Y, _roomFloorZ + p.Z);
    }

    /// <summary>A direction of the play space (OpenXR) in the region's frame.</summary>
    public Vector3 RoomDirToRegion(Vector3 xr) => Rotate(ToSl(xr), _yaw);

    /// <summary>A point of the region in the play space, as placed this frame (RoomToRegion's inverse).</summary>
    public Vector3 RegionToRoom(Vector3 region)
    {
        var flat = Rotate(Flat(region - _origin), -_yaw);
        var sl = new Vector3(flat.X, flat.Y, region.Z - _roomFloorZ);
        return new Vector3(-sl.Y, sl.Z, -sl.X);
    }

    /// <summary>Put the avatar under the head and face the room the way the avatar faces (next frame).</summary>
    public void Recentre() { _placed = false; _seatedRef = null; }

    public VrView? Update(XrFrame frame, bool floorLevel, IKeyboard? kb, double dt)
    {
        var c = _session.Client;
        if (c is null || !c.Network.Connected || c.Network.CurrentSim is not { } sim) return null;
        if (!frame.Tracked) return null;
        double now = _clock.Elapsed.TotalSeconds;
        var mv = c.Self.Movement;
        bool sitting = c.Self.SittingOn != 0;
        bool driving = Driving(c, sitting);   // the sticks are the movement keys (VrPilot.Seated.cs)
        var input = frame.Input;
        var (stick, turnKeys, keyUp, keyDown, keyFly) = Keys(kb);
        if (stick != Vector2.Zero) input.LeftStick = stick;

        // A teleport to another region: from the grid's TeleportFinish until the region there has taken the agent in, the
        // view holds where it is, in the old region, and then cuts once to where the avatar landed (Arrival, below).
        // Decided before the position is read: once the arrival reply has been heard, the position is the new region's.
        bool arrived = Arrival(c, sim, now);

        var (rawPos, rawVel) = _lag.Read(c, now);
        (rawPos, rawVel) = CrossingWord(sim, rawPos, rawVel, arrived, sitting, now);   // (VrPilot.Crossing.cs)
        // the head: between the eyes, in the room (SL axes)
        var e0 = frame.Eyes[0]; var e1 = frame.Eyes[1];
        var headXr = (e0.Position + e1.Position) * 0.5f;
        var headQXr = Quaternion.Normalize(Quaternion.Slerp(e0.Orientation, e1.Orientation, 0.5f));
        var room = ToSl(headXr);
        var roomQ = ToSl(headQXr);
        _lastRoom = room;
        _lastRoomQ = roomQ;
        if (_arriving != null) return Hold(c, frame, floorLevel, input, turnKeys, room, roomQ, headXr, headQXr, now, dt);

        // seated, the avatar rides its seat as the renderer draws it this frame (a vehicle carried on between updates)
        var seatPose = sitting ? SeatPose(c, sim) : null;
        var avatar = seatPose?.Pos ?? SmoothedAvatar(c, sim, rawPos, rawVel);
        PhysicsAround(sim.Handle, avatar);   // (server movement too: its landings stop on what's known, below)
        if (_placed && sim.Handle != _region)
        {
            // a region crossing: the same places in the new region's coordinates
            var off = RegionOffset(_region, sim.Handle);
            _origin += off;
            _lastAvatar += off;
            _lastRaw += off;
            _walkPos += off;
            _updRaw += off;
            _glideAt += off;
            _glideFrom += off;
            _glideDrawn += off;
            if (!NoCrossingFix) ShiftForCrossing(off);
            (_crossOff, _crossedAt) = (off, now);
        }
        if (sim.Handle != _region) _regionSetAt = DateTime.UtcNow;
        _region = sim.Handle;
        _regionSize = new Vector2(sim.SizeX, sim.SizeY);
        // LibreMetaverse makes the new region current a moment before it moves the agent's position into its
        // coordinates: a position well outside the region is the old one, so hold where the avatar was. So is one just
        // over the border that's nearer where it was in the old coordinates than in the new (walking across, the old
        // coordinates are only centimetres outside: 2026-10-27, in the simulator, that was taken for a 256 m jump there
        // and back).
        bool oldCoords = _placed && now - _crossedAt < 3
            && Vector3.Distance(Flat(rawPos) + _crossOff, Flat(_lastRaw)) < Vector3.Distance(Flat(rawPos), Flat(_lastRaw));
        if (_placed && !arrived && (oldCoords || avatar.X < -8 || avatar.Y < -8 || avatar.X > sim.SizeX + 8 || avatar.Y > sim.SizeY + 8))
        {
            avatar = _lastAvatar;
            rawPos = _lastRaw with { Z = avatar.Z };
        }
        if (arrived && _placed) Arrive(avatar, rawPos, rawVel, room, now);

        if (Spectating && _placed) return Spectate(c, sim, frame, floorLevel, input, turnKeys, keyUp, keyDown, keyFly, room, roomQ, headXr, headQXr, avatar, rawPos, now, dt);

        if (!_placed)
        {
            // the avatar under the head, the room turned so that looking ahead is looking where the avatar faces
            var bodyFwd = LVector3.UnitX * c.Self.SimRotation;
            float avatarYaw = MathF.Atan2(bodyFwd.Y, bodyFwd.X);
            _yaw = avatarYaw - Yaw(roomQ);
            _origin = Flat(avatar) - Rotate(Flat(room), _yaw);
            _lastAvatar = avatar;
            _lastRaw = rawPos;
            (_updRaw, _updVel, _rawChangedAt) = (Flat(rawPos), rawVel, now);
            _bodyYaw = avatarYaw;
            _follow = Follow.Idle;
            _gaveUpAt = null;
            _floorSet = false;
            _correcting = null;
            _ride = null;
            _placed = true;
        }

        // The room moves only for what the person does (the stick, turning) and for what the region clearly does to
        // the avatar (a jump such as a teleport or a sit, being carried along at speed). Small disagreements never
        // move it: the avatar is walked to the head instead (Follow), and only when that can't work is the view put
        // back on the avatar, once still (Correct). Region positions arrive a round trip late and in steps, so
        // anything that followed them frame by frame would shake the view (user, 2026-10-25; docs/vr.md > The rig).
        var moved = Flat(avatar - _lastAvatar);
        var rawJump = Flat(rawPos - _lastRaw);
        // what an update's step doesn't explain by the velocity it had (an update while walking moves on by
        // velocity x interval; a teleport or a sit doesn't)
        // (an update: moved over 2 cm since the last, or a new velocity; smaller wobbles aren't news)
        var unexplained = Vector3.Zero;
        if (rawJump != Vector3.Zero && (Vector3.Distance(Flat(rawPos), _updRaw) > 0.02f || Vector3.Distance(rawVel, _updVel) > 0.1f))
        {
            float since = (float)Math.Min(DeadReckonCap, now - _rawChangedAt);
            var step = Flat(rawPos) - _updRaw;
            unexplained = step - Flat(_updVel) * since;
            // an update that only came late or early along the avatar's own way is the region's timing, not a jump: a
            // slow region pauses its updates and comes back short of where the velocity would have had it, then
            // catches up. Taken for a jump, the view froze and then blinked carrying the next update (the session of
            // 2026-10-28, 00:05:34: flying at 16 m/s, a 0.25 s pause, 2.5 m short); the drawn avatar glides through it
            if (AlongTheWay(step, Flat(_updVel), since) && !NoAlongTheWay) unexplained = Vector3.Zero;
            _updRaw = Flat(rawPos);
            _updVel = rawVel;
            _rawChangedAt = now;
        }
        _lastRawVel = rawVel;
        if (MathF.Abs(rawPos.Z - _steadyZ) > 0.01f || rawVel.Length() > 0.05f) (_steadyZ, _steadySince) = (rawPos.Z, now);
        if (MathF.Abs(rawPos.Z - _levelZ) > 0.03f || MathF.Abs(rawVel.Z) > 0.1f) (_levelZ, _levelSince) = (rawPos.Z, now);
        _lastAvatar = avatar;
        var rawBefore = Flat(_lastRaw);
        _lastRaw = rawPos;
        WatchTeleport(rawPos, rawJump, Flat(_origin + Rotate(Flat(room), _yaw)) - Flat(avatar), now);
        bool stickWalk = input.LeftStick.Length() > 0.25f && !driving && !StoodByStick;
        // a jump that brings the avatar to where you are (the region catching up after it had held the avatar back)
        // isn't one to follow; one that takes it away (a teleport, being pushed) is
        bool Away(Vector3 at) => rawJump.Length() > 20f || Vector3.Distance(Flat(rawPos), at) > Vector3.Distance(rawBefore, at) - 0.3f;
        bool carried = false;
        if (Server && !sitting)
        {
            // movement "server": the room rides the region's avatar as the renderer draws it (dead-reckoned, each update's
            // difference decaying), so walls, steps and falls are the region's; only the follow walk to the head in the
            // room and its coast leave the room where it is. A jump the avatar's velocity doesn't explain (a teleport, a
            // push) is drawn where the region has it at once and carried with it, so nothing of a glide is carried twice;
            // one that brings the avatar to you (a teleport to you) isn't carried at all; over a metre, a straight cut.
            _walking = false;
            if (stickWalk) _correcting = null;
            if (_correcting != null) _origin += Slide(dt);
            var headNow = _origin + Rotate(Flat(room), _yaw);
            // (moving or just stopped, an update off the drawing's run-on by less than 2 m is its velocity changing, as in
            // predicted mode below: a stop's update was taken for a jump, and the drawing's pull back left out)
            bool jumped = unexplained.Length() > (_follow == Follow.Idle && !_wasMoving ? 0.3f : 2f);
            // (across a crossing at speed the room rides a follower of the drawing, which takes the region's lurches
            // there as changes of speed: VrPilot.Crossing.cs. Only a real jump ends it)
            var ride = unexplained.Length() > 20f ? (_ride = null) : Ride(avatar, rawVel, moved, now, dt);
            if (ride != null) jumped = false;
            if (jumped)
            {
                // (before 2026-10-28 the room went by the region's own positions for a quarter second, which stepped the
                // view at each update; with the drawing's slower decay the rest of its glide would then be carried twice)
                var fresh = SmoothedAvatar(c, sim, rawPos, rawVel, snap: true);
                moved += Flat(fresh - avatar);
                avatar = _lastAvatar = fresh;
            }
            var by = ride ?? moved;
            if (Glide(avatar, rawPos, rawVel, stickWalk || jumped, now, dt) is { } glide) by = glide;   // (VrPilot.Brake.cs)
            bool follows = _follow == Follow.Walking || _follow == Follow.Settling && _learnCoast;
            // (keep me in place holding, VrPilot.Aim.cs: a push isn't carried, however far it throws, while a teleport,
            // arriving without speed, still is; the walk to the head undoes it)
            if (!follows && !(jumped && !Away(headNow)) && by != Vector3.Zero && !(Held() && (by.Length() < 4f || rawVel.Length() > 1f)))
            {
                _origin += by;
                carried = true;
                if (by.Length() > 1f) { Cut(); _floorSet = false; }
            }
        }
        else if (stickWalk)
        {
            _correcting = null;
            float footDepth = FootDepth(c, sim);
            // (the surface walked on: the avatar's small hover over it isn't part of a step's height)
            float feetZ = Flying ? avatar.Z - footDepth : _floor - _lift - (_onModel ? MathF.Max(_hover, 0) : 0);
            _origin += Walk(sim, c.Self.LocalID, input, rawPos, rawVel, avatar, roomQ, _origin + Rotate(Flat(room), _yaw), feetZ, 2 * footDepth, now, dt);
            // teleported mid-walk (not the region's lurches across a crossing: VrPilot.Crossing.cs)
            if (unexplained.Length() > (InCrossing(now) && !NoCrossingFix ? 20f : 2f) && Away(_walkPos)) Carry(rawJump, now);
        }
        else
        {
            if (_walking) _blockedWalk = _servo < 0.6f;
            _walking = false;
            _origin += PullBack(dt);   // (what's left of one the region asked for as the stick let go)
            if (sitting)
            {
                // seated, the region moves you (vehicles, seats): along with the seat as it's drawn, frame by frame, and
                // a straight cut only for a jump its motion doesn't explain (a vehicle at speed moves metres a frame)
                if (moved != Vector3.Zero) CarrySeated(moved, now);
                else _seatMoved = Vector3.Zero;
                TurnWithSeat(c, room, seatPose?.Rot);
                _floorSet = false;
                _correcting = null;
            }
            else if (unexplained.Length() > (_follow == Follow.Idle && !_wasMoving ? 0.3f : 2f) && Away(_origin + Rotate(Flat(room), _yaw))
                     && !(Held() && (rawJump.Length() < 4f || rawVel.Length() > 1f))) Carry(rawJump, now);
            else if (_follow == Follow.Idle && !_wasMoving && _correcting == null && new Vector2(rawVel.X, rawVel.Y).Length() > 0.3f && !Held()
                     && now - _tpLandedAt > 1.0)   // (an avatar teleported to you can arrive still coasting)
            {
                // moving without the person asking (pushed, a moving floor, the agent API walking it): ride along
                _origin += moved;
                carried = true;
            }
            if (_correcting != null) _origin += Slide(dt);
        }

        var rs = input.RightStick;
        float turn = TurnRoom(input, turnKeys, room, dt);

        var worldQ = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _yaw) * roomQ);
        float headYaw = Yaw(worldQ);
        var headFlat = _origin + Rotate(Flat(room), _yaw);
        float headZ;
        if (sitting)
        {
            // seated (either posture): the avatar's eyes where its sit animation puts them (the renderer's, last frame;
            // a guess over the agent's position until it has drawn them), moved by how the real head moves from where it
            // was when the view was put there. Once, as it settles on the seat, the head goes onto its eyes and the room
            // turns to face the way it sits, a straight cut (the session of 2026-10-26: the view stayed at standing
            // height on a windowsill).
            var seatEyes = Settings.ShowBody && SeatedEyesLocal is { } local && seatPose is { } sp ? sp.Pos + Vector3.Transform(local, sp.Rot)
                : Settings.ShowBody && SeatedEyes is { } se ? se.Eyes : avatar + new Vector3(0, 0, SeatedEyeGuess);
            if (_seatHead == null) { _seatHead = room.Z; _seatPlacedAt = null; _satAt = now; }
            if (_seatPlacedAt == null && (Settings.ShowBody && SeatedEyes != null && now - _satAt > 0.3 || now - _satAt > 1.5))
            {
                var facing = seatPose is { } fp ? Vector3.Transform(Vector3.UnitX, fp.Rot) : SeatedEyes?.Facing ?? Vector3.Zero;
                if (Settings.ShowBody && SeatedEyes != null && facing.X * facing.X + facing.Y * facing.Y > 0.01f)
                {
                    var headAt = _origin + Rotate(Flat(room), _yaw);
                    _yaw = MathF.Atan2(facing.Y, facing.X) - Yaw(roomQ);
                    _origin = headAt - Rotate(Flat(room), _yaw);
                    _bodyYaw = MathF.Atan2(facing.Y, facing.X);
                }
                _origin += Flat(seatEyes) - (_origin + Rotate(Flat(room), _yaw));
                _seatHead = room.Z;
                _seatPlacedAt = now;
                Cut();
            }
            headZ = seatEyes.Z + (room.Z - _seatHead.Value);
            worldQ = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _yaw) * roomQ);
            headYaw = Yaw(worldQ);
            headFlat = _origin + Rotate(Flat(room), _yaw);
        }
        else
        {
            _seatHead = null;
            _lift = Lift(c, sim, room.Z, floorLevel);
            float footDepth = FootDepth(c, sim);
            // (flying, just let go of the climb: the height eases to where the avatar stops, VrPilot.Brake.cs)
            bool climbHeld = input.A || keyUp || keyDown || MathF.Abs(input.RightStick.Y) > 0.5f;
            var climb = ClimbStop(avatar.Z, rawPos.Z, rawVel.Z, !Flying || climbHeld || driving, now, dt);
            float climbVz = climb?.Vz ?? rawVel.Z;
            if (Server)
            {
                // the avatar's feet as drawn (the region's own once it reports it still: the drawing runs on past a
                // landing until then), through the floor's spring, so steps and updates glide
                float feetS = (rawVel.Length() < 0.05f ? rawPos.Z : avatar.Z) - footDepth;
                float floorS = climb is { } cs ? ClimbFloor(cs.Z - footDepth + _lift, climbVz) : Floor(feetS + _lift, rawVel.Z, stickWalk || carried, dt);
                _onModel = false;
                // and never down through a floor the local model knows (Underfoot, VrPilot.Collide.cs): after a fall the
                // drawing runs on down past the landing until the region's word arrives, and the view went with it, 2 m
                // through a plate after 10 m and 5 m into the ground after 100 (in the simulator, 2026-10-27). The region's
                // avatar landed and still near the head wins over anything holding the view higher, as in predicted mode
                _onRegion = !Flying && now - _steadySince > 0.3 && Vector3.Distance(Flat(rawPos), Flat(headFlat)) < 0.8f ? rawPos.Z - footDepth : null;
                _walkLevel = null;
                headZ = Underfoot(sim, c.Self.LocalID, headFlat, avatar, rawPos, footDepth, floorS, now) + room.Z;
            }
            else
            {
                // (on the local model the floor needs no lead from the region's climb rate: it already knows the step)
                float ground = GroundUnder(sim, c.Self.LocalID, headFlat, avatar, rawPos, footDepth, rawVel.Z, stickWalk, dt);
                float floor = climb is { } cp ? ClimbFloor(cp.Z - footDepth + _lift, climbVz)
                    : Floor(ground + _lift, _onModel ? 0 : rawVel.Z, stickWalk || carried, dt);
                headZ = Underfoot(sim, c.Self.LocalID, headFlat, avatar, rawPos, footDepth, floor, now) + room.Z;
            }
        }
        var head = new Vector3(headFlat.X, headFlat.Y, headZ);
        _roomFloorZ = headZ - room.Z;

        // buttons: edges
        if (input.B && !_prev.B || keyFly) ToggleFly(c);
        StandByStick(c, input, sitting, dt);
        if (input.LeftStickClick && !_prev.LeftStickClick) { _run = !_run; Say?.Invoke(_run ? "running" : "walking"); }
        if (YPress(input, now) is { } yHeld)
        {
            if (yHeld) Spectating = true;
            else { _placed = false; Say?.Invoke("recentred"); }
        }
        _prev = input;

        // the left stick: walk in the direction it points, relative to the head or the left hand
        bool moving = input.LeftStick.Length() > 0.25f && !driving && !StoodByStick;
        Vector3 moveDir = default;
        if (moving)
        {
            float refYaw = headYaw;
            if (Settings.HandRelative && input.LeftAim is { } aim) refYaw = Yaw(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _yaw) * ToSl(aim));
            var s = Vector2.Normalize(input.LeftStick);
            moveDir = Rotate(new Vector3(s.Y, -s.X, 0), refYaw);
            _follow = Follow.Idle;
            _gaveUpAt = null;
        }
        else if (_wasMoving && !sitting)
        {
            // let go of the stick: the camera stops here, while the avatar coasts on (and steps back if it went far)
            _follow = Follow.Settling;
            _followSince = now;
            _learnCoast = false;
            StartBrake(now, _octant);   // (VrPilot.Brake.cs: the region told to stop it here, not coast on)
            if (Server)
            {
                // (from where the room rides: after a crossing that can be a little off the drawing)
                StartGlide(_ride ?? avatar, rawVel, now);
                _glideDrawn = Flat(avatar);
                if (_glide) _ride = null;
            }
        }
        _wasMoving = moving;

        // the avatar follows the head, at the slow "nudge" walk (a viewer's tapped key); it coasts on after the
        // stop (the region's physics, and on the main grid the network), so stop that far short, learning how far
        var gap = headFlat - Flat(avatar);
        var gap2 = new Vector2(gap.X, gap.Y);
        var flatVel = new Vector2(rawVel.X, rawVel.Y);
        switch (_follow)
        {
            case Follow.Idle:
                // not while flying: a flying avatar glides on, and nudges over- and undershoot
                // seated, a lean shouldn't walk the avatar: only a bigger gap does
                if (gap2.Length() <= (Settings.Seated ? 0.8f : 0.5f)) { _blockedWalk = false; break; }
                if (moving || sitting || _correcting != null || _tpAsked != null || _arriving != null) break;
                // (nor while it's in the air: walked off an edge, a walk back to the head took it back over a region's
                // border mid-fall, 2026-10-27 in the simulator)
                if (!Flying && MathF.Abs(rawVel.Z) > 1f) break;
                // (and the drawn avatar caught up with it: just after a teleport it glides over from where it was, and
                // the gap to that isn't one to put right; 2026-10-27, a teleport to you was followed by two blinks back)
                bool regionStill = flatVel.Length() < 0.05f && Vector3.Distance(Flat(avatar), Flat(rawPos)) < 0.1f;
                if (Flying)
                {
                    // flying, the room-scale gap just stays (nothing walks there); only a long glide is put right
                    if (gap2.Length() > 4f && regionStill) PutBack(c, sim, gap, head, now);
                }
                else if (_blockedWalk || _gaveUpAt is { } g && Vector2.Distance(g, gap2) <= 0.3f)
                {
                    // the stick walk ran into something, or walking the avatar here didn't get it closer: put the
                    // view back on the avatar instead
                    if (regionStill) PutBack(c, sim, gap, head, now);
                }
                else
                {
                    _follow = Follow.Walking;
                    _followSince = now;
                    _bestGap = gap2.Length();
                }
                break;
            case Follow.Walking:
                if (sitting || Flying) { _follow = Follow.Idle; break; }
                if (gap2.Length() < MathF.Max(0.1f, _coast))
                {
                    _follow = Follow.Settling;
                    _followSince = now;
                    _learnCoast = true;
                    _stoppedAt = Flat(avatar);
                    _stopDir = Vector3.Normalize(gap);
                    StartBrake(now, _octant);
                    break;
                }
                if (gap2.Length() < _bestGap - 0.1) { _bestGap = gap2.Length(); _followSince = now; }
                else if (now - _followSince > 1.5) { _follow = Follow.Idle; _gaveUpAt = gap2; }   // blocked: leave it
                break;
            case Follow.Settling:
                // done once the region says the avatar has stopped and the drawn position has caught up with it
                bool still = flatVel.Length() < 0.05f && Vector3.Distance(Flat(avatar), Flat(rawPos)) < 0.03f;
                if ((still && now - _followSince > 0.15) || now - _followSince > 2.5)
                {
                    if (still && _learnCoast) _coast = Math.Clamp(0.7f * _coast + 0.3f * Vector3.Dot(Flat(avatar) - _stoppedAt, _stopDir), 0.05f, 1.5f);
                    _follow = Follow.Idle;
                }
                break;
        }

        // the body faces where the head looks, walking too (user, 2026-10-26: walking backward it swung round and
        // bumped into people), and moves by the keys relative to it: forward, back, sideways or diagonally (Octant);
        // standing, it turns to the head once that's well off
        if (moving) _bodyYaw = Octant(MathF.Atan2(moveDir.Y, moveDir.X), headYaw);
        else if (_follow == Follow.Walking) _bodyYaw = Octant(MathF.Atan2(gap.Y, gap.X), headYaw);
        else
        {
            _octant = 0;
            if (MathF.Abs(AngleDiff(headYaw, _bodyYaw)) > 40f * MathF.PI / 180f) _bodyYaw = headYaw;
        }

        int up = (input.A || keyUp ? 1 : 0) - (keyDown ? 1 : 0);
        if (Flying && MathF.Abs(rs.Y) > 0.5f) up = MathF.Sign(rs.Y);
        if (driving && MathF.Abs(rs.Y) > 0.5f) up = MathF.Sign(rs.Y);
        if (up > 0 && !Flying && !sitting && !driving) _jumpAt = now;
        if (Flying && _lastUp != 0 && up == 0 && !sitting && !driving)
        {
            // let go of the climb: the region told to stop it, the view's height eased to where it will (VrPilot.Brake.cs)
            StartClimbStop(_floor - _lift + FootDepth(c, sim), _floorVel, rawPos.Z, rawVel.Z, now);
            if (!moving) StartBrake(now, 0);
        }
        _lastUp = up;
        bool walk = moving || _follow == Follow.Walking;
        bool held = walk || up != 0 || turn != 0 || (driving && DriveHeld(input));

        // the agent API drives the body while it runs and nothing is held; the room rides along
        if (!(_session.Motion.Moving && !held))
        {
            bool nudge = _follow == Follow.Walking;
            bool ahead = _octant is >= -1 and <= 1, back = Math.Abs(_octant) >= 3, left = _octant is >= 1 and <= 3, right = _octant is <= -1 and >= -3;
            mv.AtPos = moving && ahead; mv.AtNeg = moving && back; mv.FastAt = mv.AtPos || mv.AtNeg;
            mv.LeftPos = moving && left; mv.LeftNeg = moving && right; mv.FastLeft = mv.LeftPos || mv.LeftNeg;
            mv.NudgeAtPos = nudge && ahead; mv.NudgeAtNeg = nudge && back;
            mv.NudgeLeftPos = nudge && left; mv.NudgeLeftNeg = nudge && right;
            mv.UpPos = up > 0; mv.UpNeg = up < 0; mv.FastUp = up != 0;
            mv.TurnLeft = mv.TurnRight = mv.YawPos = mv.YawNeg = false;
            mv.BodyRotation = LQuaternion.CreateFromAxisAngle(LVector3.UnitZ, _bodyYaw);
            mv.HeadRotation = new LQuaternion(worldQ.X, worldQ.Y, worldQ.Z, worldQ.W);
            bool runNow = _run && walk && _follow != Follow.Walking;
            if (mv.AlwaysRun != runNow) mv.AlwaysRun = runNow;
            var (brake, reverse) = Brake(now, walk, up, sitting, driving, rawVel);
            mv.Stop = brake;
            if (reverse) WayKeys(mv, _brakeOctant + 4);
            if (driving) Drive(mv, input);
        }
        else mv.Stop = false;
        mv.Mouselook = false;

        var fwd = Vector3.Transform(Vector3.UnitX, worldQ);
        var upV = Vector3.Transform(Vector3.UnitZ, worldQ);
        mv.Camera.Far = _session.Camera.FarClip;
        var focus = head + fwd;
        mv.Camera.LookAt(new LVector3(head.X, head.Y, head.Z), new LVector3(focus.X, focus.Y, focus.Z));
        _session.State.CameraOverride = (new LVector3(head.X, head.Y, head.Z), new LVector3(focus.X, focus.Y, focus.Z));
        AimFrame(mv, input, head, worldQ);   // weapons and mouselook games (VrPilot.Aim.cs)

        _sinceSend += dt;
        uint controls = mv.AgentControls;
        if (controls != _lastControls || (held && _sinceSend > 0.1) || _sinceSend > 0.25)
        {
            mv.SendUpdate(controls != _lastControls);
            _lastControls = mv.AgentControls;
            _sinceSend = 0;
        }

        _framesSinceReport++;
        if (now - _timeRaysAt >= 5) { ReportRays(now, _framesSinceReport); _framesSinceReport = 0; }
        // the eyes relative to the head, in the head's own frame (OpenXR's view space is the renderer's eye space)
        var inv = Quaternion.Conjugate(headQXr);
        var eyes = new EyeSpec[2];
        for (int i = 0; i < 2; i++)
        {
            var e = frame.Eyes[i];
            eyes[i] = new EyeSpec(Vector3.Transform(e.Position - headXr, inv), Quaternion.Normalize(inv * e.Orientation),
                e.TanLeft, e.TanRight, e.TanUp, e.TanDown);
        }
        Log?.WriteLine($"{now:F4},{room.X:F3},{room.Y:F3},{room.Z:F3},{head.X:F3},{head.Y:F3},{head.Z:F3},{avatar.X:F3},{avatar.Y:F3},{avatar.Z:F3}," +
                       $"{_follow},{(walk ? 1 : 0)},{_yaw * 180 / MathF.PI:F1},{_bodyYaw * 180 / MathF.PI:F1}," +
                       $"{rawPos.X:F3},{rawPos.Y:F3},{rawPos.Z:F3},{rawVel.X:F3},{rawVel.Y:F3}," +
                       $"{_origin.X:F5},{_origin.Y:F5},{_roomFloorZ:F5},{dt * 1000:F3},{input.LeftStick.Length():F2},{(turn != 0 ? 1 : 0)},{up}," +
                       $"{(_correcting != null ? "slide" : _cutPending ? "cut" : carried ? "carried" : "-")},{_servo:F2},{(mv.Stop ? 1 : 0)}");
        SelfBody? self = null;
        if (Settings.ShowBody)
            self = new SelfBody
            {
                Id = c.Self.AgentID, Head = head, Yaw = _bodyYaw,
                // standing at your own height, the body is scaled to it; otherwise it keeps its size (the eyes are its)
                EyeHeight = floorLevel && !Settings.Seated && Settings.EyeHeight == EyeHeightMode.Real ? StandingEye(room.Z) : 0,
                Left = HandsTracked() ? Hand(input.LeftHand, input.LeftHandPos, left: true) : null,
                Right = HandsTracked() ? Hand(input.RightHand, input.RightHandPos, left: false) : null,
            };
        return new VrView(sim.Handle, head, fwd, upV, eyes, self);
    }

    /// <summary>Turning, about the head: snap, or smooth past a small dead zone (the rate growing linearly with the
    /// stick, from zero to full speed at the rim). The degrees turned this frame.</summary>
    float TurnRoom(XrControllers input, int turnKeys, Vector3 room, double dt)
    {
        float turn = 0;
        var rs = input.RightStick;
        if (!Settings.SmoothTurn)
        {
            if (MathF.Abs(rs.X) < 0.35f) _snapArmed = true;
            else if (_snapArmed && MathF.Abs(rs.X) > 0.7f) { turn = -MathF.Sign(rs.X) * Settings.SnapAngle; _snapArmed = false; }
            turn += turnKeys * Settings.SnapAngle;
        }
        else
        {
            const float dead = 0.15f;
            if (MathF.Abs(rs.X) > dead) turn = -MathF.Sign(rs.X) * Math.Min(1, (MathF.Abs(rs.X) - dead) / (1 - dead)) * Settings.TurnSpeed * (float)dt;
            if (turnKeys != 0) turn += turnKeys * Settings.TurnSpeed * (float)dt;
        }
        if (turn != 0)
        {
            var headAt = _origin + Rotate(Flat(room), _yaw);
            _yaw += turn * MathF.PI / 180f;
            _origin = headAt - Rotate(Flat(room), _yaw);
        }
        return turn;
    }

    // --- the stick walk: local ----------------------------------------------------------------------------------
    // The region moves the avatar a round trip after the stick does (on the main grid ~100-190 ms, measured
    // 2026-10-25), and its updates come every ~60-110 ms, each a little off the last. So the walk is the client's own:
    // from the first frame the stick is held, the room moves at the speed the avatar walks (learned from the region's
    // own velocity per gait, 3.2 m/s walking there), steadily, and the region's positions never move it. They only
    // tell it when it's running far ahead of the avatar, walking into a wall or up against something: beyond a dead
    // zone (0.7 m ahead along the walk, 1 m to the side) the speed eases down (or the side drifts back) over a few
    // tenths of a second, so even then the view's motion stays smooth. Letting go stops at once; whatever gap is left
    // is closed once still, by walking the avatar to you or, failing that, putting you back on it (Correct).

    bool _walking, _blockedWalk, _latencySeen;
    Vector3 _walkPos, _side;
    float _servo = 1;
    double _pressAt;
    Vector3 _pressRaw;
    float _latency = 0.15f;
    // walking, running, flying (m/s; learned as they're seen), each in the five ways the body can move relative to
    // where it faces (forward, diagonally forward, sideways, diagonally back, back)
    readonly float[,] _speeds = { { 3.2f, 3.2f, 3.2f, 3.2f, 3.2f }, { 5.13f, 5.13f, 5.13f, 5.13f, 5.13f }, { 4.4f, 4.4f, 4.4f, 4.4f, 4.4f } };
    readonly bool[,] _speedSeen = new bool[3, 5];

    int Gait() => Flying ? 2 : _run ? 1 : 0;

    Vector3 Walk(Simulator sim, uint self, XrControllers input, Vector3 raw, Vector3 rawVel, Vector3 avatar, Quaternion roomQ, Vector3 head,
        float feetZ, float height, double now, double dt)
    {
        var serverVel = Flat(rawVel);
        if (!_walking)
        {
            _walking = true;
            _walkPos = Flat(avatar);
            _pressAt = now;
            _pressRaw = raw;
            _latencySeen = false;
            _servo = 1;
            _side = Vector3.Zero;
            _blockedWalk = false;
            _trail.Clear();
            _blockedBy = 0;
            _blockedSince = -1;
            _xingSlack = 0;
        }
        // where the stick points, as the walk below steers (head or left hand)
        float refYaw = Yaw(Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _yaw) * roomQ));
        if (Settings.HandRelative && input.LeftAim is { } aim) refYaw = Yaw(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _yaw) * ToSl(aim));
        var s = Vector2.Normalize(input.LeftStick);
        var dir = Rotate(new Vector3(s.Y, -s.X, 0), refYaw);
        int gait = Gait();
        int way = Math.Abs(_octant);   // forward, diagonally forward, sideways, diagonally back, back
        float since = (float)(now - _pressAt);

        // learn the round trip (the region's position first moving) and the gait's speed (steady, well into a walk)
        if (!_latencySeen && Vector3.Distance(raw, _pressRaw) > 0.05f)
        {
            _latencySeen = true;
            if (since is > 0.02f and < 0.6f) _latency = 0.8f * _latency + 0.2f * since;
        }
        if (since > 0.8f && serverVel.Length() > 0.5f && Vector3.Dot(Vector3.Normalize(serverVel), dir) > 0.9f)
        {
            if (!_speedSeen[gait, way])
            {
                // the first steady sight of this gait in this direction: the others in it start from it
                _speedSeen[gait, way] = true;
                for (int w = 0; w < 5; w++) if (!_speedSeen[gait, w]) _speeds[gait, w] = serverVel.Length();
            }
            _speeds[gait, way] = Math.Clamp(0.97f * _speeds[gait, way] + 0.03f * serverVel.Length(), 0.5f, 30f);
        }

        // how far the walk has run ahead of where the region will have the avatar now (its position a round trip
        // old, carried on by its velocity), along the walk and across it
        float k = 1f - MathF.Exp(-(float)dt / 0.25f);
        var sideDrift = Vector3.Zero;
        if (since > _latency + 0.25f)
        {
            // (dead-reckoned from its last update: a region sends few while an avatar walks steadily)
            var err = _walkPos - (Flat(raw) + serverVel * (_latency + (float)Math.Min(DeadReckonCap, now - _rawChangedAt)));
            float ahead = Vector3.Dot(err, dir);
            var across = err - dir * ahead;
            ahead -= CrossingSlack(ahead, now);   // (VrPilot.Crossing.cs)
            // ahead: ease off past 0.7 m, stopped at 1.5; behind (the region let the avatar on where the walk stopped, or
            // it walks this way faster than learned): catch up, up to a third faster
            float want = ahead <= 0.7f ? 1f + Math.Clamp((-ahead - 0.3f) / 1f, 0f, 0.33f) : Math.Clamp(1f - (ahead - 0.7f) / 0.8f, 0f, 1f);
            _servo += (want - _servo) * k;
            // across the walk: drift back at most 0.5 m/s, only past a metre
            float al = across.Length();
            var wantSide = al > 1f ? -across / al * MathF.Min(0.5f, (al - 1f) * 0.8f) : Vector3.Zero;
            _side += (wantSide - _side) * k;
            sideDrift = _side * (float)dt;
        }
        float speed = _speeds[gait, way] * _servo;
        // (not across a crossing: the region's word there isn't the avatar being stopped, VrPilot.Crossing.cs)
        if (!InCrossing(now) || NoCrossingFix) Judge(sim, self, feetZ, raw, rawVel, dir, speed, since, now);
        var step = dir * (speed * (float)dt - CrossingGive(now, dt)) + sideDrift;
        step = Sweep(sim.Handle, self, head, _walkPos, feetZ, height, step, now) + PullBack(dt);
        _walkPos += step;
        return step;
    }

    // --- what the region does to the avatar, and putting the view back on it -------------------------------------

    Vector3? _correcting;     // what's left of a slide back onto the avatar
    Vector3 _slideVel;
    // A move of the view to a new spot at once (a teleport, a put-back, a crossing's or a seat's jump, a long drop) is a
    // straight cut: the user dropped the 90 ms black blink that used to hide it (2026-10-28: at 30-45 fps it was a few
    // solid black frames each time, what they'd been reporting as black frames). A fade may come later as a comfort
    // option, off by default.
    bool _cutPending;

    void Cut() => _cutPending = true;

    /// <summary>Whether the view was cut to a new spot since the last call (once a frame, after Update: the console's
    /// line and the pacing line's count); <see cref="CutThisFrame"/> keeps the answer for the frame.</summary>
    public bool TakeCut()
    {
        CutThisFrame = _cutPending;
        _cutPending = false;
        return CutThisFrame;
    }

    /// <summary>The view was cut to a new spot this frame (TakeCut's answer): motion measured across it isn't motion.</summary>
    public bool CutThisFrame { get; private set; }

    /// <summary>A move of the avatar that the room goes along with at once (teleports, sits, crossings within a
    /// region); over a metre, a straight cut of the view.</summary>
    void Carry(Vector3 by, double now)
    {
        _origin += by;
        if (by.Length() > 1f)
        {
            Cut();
            _floorSet = false;   // its height goes with it, in the same cut
        }
        _correcting = null;
    }


    double? _arriving;        // since when the view has been held for a teleport's arrival (Arrival; null: not arriving)
    Vector2 _regionSize = new(256, 256);
    DateTime _regionSetAt;    // when the view came into its region (UTC, as TeleportWatch's times)

    /// <summary>Regions that touch, edge or corner (<paramref name="off"/>: RegionOffset from the first to the
    /// second): a move between them is a crossing, or a teleport whose coordinates carry over.</summary>
    static bool Neighbours(Vector3 off, Vector2 fromSize, Vector2 toSize) =>
        // the old region spans [0, from) in its own coordinates, the new one [-off, -off + to) in them
        -off.X <= fromSize.X + 0.5f && -off.X + toSize.X >= -0.5f && -off.Y <= fromSize.Y + 0.5f && -off.Y + toSize.Y >= -0.5f;

    /// <summary>
    /// A teleport to another region (a neighbour too). From the grid's TeleportFinish until the region there has taken
    /// the agent in (its AgentMovementComplete, with that region current), the agent's position can't be trusted:
    /// LibreMetaverse makes the new region current a moment after it sends CompleteAgentMovement, so for a round trip
    /// or more the position is still the old region's, read in the new region's coordinates (some unrelated spot of
    /// it), or the reply comes first and moves it into the new region's coordinates while the old region is still
    /// current. So meanwhile the view stays exactly where it was, in the old region (its scene, sky and water as they
    /// were; Program.cs keeps the old region's scene drawn while the view is in it, even if the region lets the agent
    /// go first), head tracking and turning as ever, nothing walked, carried or put back; then one straight cut to
    /// where the avatar landed (Arrive). No time limit: a teleport whose arrival never comes ends in a new login
    /// (TeleportWatch.Stalled), which places the view afresh. Without a TeleportFinish heard (the reply came between
    /// two frames, or another way into a region far off), a region change that isn't a crossing is held the same way
    /// until its region has the agent. True on the frame the view is to go onto the avatar in the new region.
    /// </summary>
    bool Arrival(GridClient c, Simulator sim, double now)
    {
        var dest = _session.Teleports.Arriving(c);
        if (_arriving == null)
        {
            if (!_placed) return false;
            var (lastTo, lastAt) = _session.Teleports.LastFinish;
            bool teleported = dest is { } d && d != _region
                || sim.Handle != _region && (lastTo == sim.Handle && lastAt > _regionSetAt
                                             || !Neighbours(RegionOffset(_region, sim.Handle), _regionSize, new Vector2(sim.SizeX, sim.SizeY)));
            if (!teleported) return false;
            _arriving = now;
            Release(keepPlace: true);   // nothing walks on into the new region
            Utils.LongToUInts(dest ?? sim.Handle, out uint x, out uint y);
            Say?.Invoke($"arriving at {x / 256},{y / 256}: the view held until the region has you");
        }
        if (dest != null) return false;
        // the watch has the arrival (or has given up: the session logs in again); the region now current, once it has
        // the agent, is where the view goes (the old one, if the teleport came to nothing there)
        // (and that region is the one this frame goes by: the new one may have become current a moment ago)
        if (!ReferenceEquals(c.Network.CurrentSim, sim) || !sim.AgentMovementComplete) return false;
        Say?.Invoke($"arrived in {sim.Name}: the view held {now - _arriving:F2} s, then cut to where you landed");
        _arriving = null;
        if (sim.Handle != _region) (_region, _regionSize, _regionSetAt) = (sim.Handle, new Vector2(sim.SizeX, sim.SizeY), DateTime.UtcNow);
        _crossedAt = -100;
        _spectating = false;   // (looking around ends with the teleport, in the same cut)
        // the drawn avatar starts afresh where the region has it: a teleport a few regions away could otherwise be
        // taken for a crossing and glide over from the old place (MotionSmoother's crossing offset)
        _motion.Forget(c.Self.AgentID);
        // (the test lag's queue still holds the old region's positions: the reply is already the region's word, so
        // the position read next is the one it gave, not one a round trip older)
        _lag.Restart();
        return true;
    }

    /// <summary>The view while a teleport's arrival is awaited: where it was, the room placed as it was (its floor
    /// too), head tracking and turning as ever; the body drawn where it stood.</summary>
    VrView Hold(GridClient c, XrFrame frame, bool floorLevel, XrControllers input, int turnKeys, Vector3 room, Quaternion roomQ,
        Vector3 headXr, Quaternion headQXr, double now, double dt)
    {
        _prev = input;   // (presses meanwhile don't act once it's over)
        TurnRoom(input, turnKeys, room, dt);
        var worldQ = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _yaw) * roomQ);
        var headFlat = _origin + Rotate(Flat(room), _yaw);
        var head = new Vector3(headFlat.X, headFlat.Y, _roomFloorZ + room.Z);
        var fwd = Vector3.Transform(Vector3.UnitX, worldQ);
        var upV = Vector3.Transform(Vector3.UnitZ, worldQ);
        var inv = Quaternion.Conjugate(headQXr);
        var eyes = new EyeSpec[2];
        for (int i = 0; i < 2; i++)
        {
            var e = frame.Eyes[i];
            eyes[i] = new EyeSpec(Vector3.Transform(e.Position - headXr, inv), Quaternion.Normalize(inv * e.Orientation),
                e.TanLeft, e.TanRight, e.TanUp, e.TanDown);
        }
        Log?.WriteLine($"{now:F4},{room.X:F3},{room.Y:F3},{room.Z:F3},{head.X:F3},{head.Y:F3},{head.Z:F3},{_lastAvatar.X:F3},{_lastAvatar.Y:F3},{_lastAvatar.Z:F3}," +
                       $"Arriving,0,{_yaw * 180 / MathF.PI:F1},{_bodyYaw * 180 / MathF.PI:F1},{_lastRaw.X:F3},{_lastRaw.Y:F3},{_lastRaw.Z:F3},0,0," +
                       $"{_origin.X:F5},{_origin.Y:F5},{_roomFloorZ:F5},{dt * 1000:F3},{input.LeftStick.Length():F2},0,0,-,1,0");
        // (looking around, as it was: no body of your own, the avatar drawn whole where it stood)
        SelfBody? self = null;
        if (Settings.ShowBody && !_spectating)
            self = new SelfBody
            {
                Id = c.Self.AgentID, Head = head, Yaw = _bodyYaw,
                EyeHeight = floorLevel && !Settings.Seated && Settings.EyeHeight == EyeHeightMode.Real ? StandingEye(room.Z) : 0,
                Left = HandsTracked() ? Hand(input.LeftHand, input.LeftHandPos, left: true) : null,
                Right = HandsTracked() ? Hand(input.RightHand, input.RightHandPos, left: false) : null,
            };
        return new VrView(_region, head, fwd, upV, eyes, self, Spectating: _spectating);
    }

    /// <summary>After a teleport elsewhere, once the region has the agent: the head over the avatar, the room's heading
    /// kept (as a teleport within a region carries the room), a straight cut; nothing of the old region's walking,
    /// following, putting back or seat carries over.</summary>
    void Arrive(Vector3 avatar, Vector3 raw, Vector3 rawVel, Vector3 room, double now)
    {
        _origin = Flat(avatar) - Rotate(Flat(room), _yaw);
        _lastAvatar = avatar;
        _lastRaw = raw;
        (_updRaw, _updVel, _rawChangedAt) = (Flat(raw), rawVel, now);
        _follow = Follow.Idle;
        _gaveUpAt = null;
        _blockedWalk = false;
        _walking = false;
        _wasMoving = false;
        _correcting = null;
        _glide = false;
        _climb = false;
        _ride = null;
        _xingAt = -100;
        _tpAsked = null;
        _tpLandedAt = now;
        _seatHead = null;
        _seatPlacedAt = null;
        _floorSet = false;
        Cut();
    }

    Vector3 _seatMoved;   // how far the seat carried you last frame

    /// <summary>Seated: go along with the seat. Smooth motion moves about the same each frame; a jump that differs from
    /// last frame's by over a metre (sitting down elsewhere, a teleport, a seat jumping) is a cut.</summary>
    void CarrySeated(Vector3 by, double now)
    {
        bool jump = Vector3.Distance(by, _seatMoved) > 1f;
        _origin += by;
        if (jump) Cut();
        _correcting = null;
        _seatMoved = jump ? Vector3.Zero : by;
    }

    /// <summary>Put the head back over the avatar (the gap from the avatar to the head): a jump (a straight cut), or a slide.</summary>
    void Correct(Vector3 gap, double now)
    {
        _blockedWalk = false;
        _gaveUpAt = null;
        Say?.Invoke($"putting you back on your avatar ({Flat(gap).Length():F1} m)");
        if (Settings.Correction == CorrectionStyle.Snap)
        {
            _origin -= Flat(gap);
            Cut();
        }
        else
        {
            _correcting = -Flat(gap);
            _slideVel = Vector3.Zero;
        }
    }

    /// <summary>One frame of a slide: a critically damped approach, at most 0.6 m/s, so it starts and ends gently.</summary>
    Vector3 Slide(double dt)
    {
        var rem = _correcting!.Value;
        const float w = 4f, maxSpeed = 0.6f;
        var moved = Vector3.Zero;
        for (double t = dt; t > 1e-6; t -= 1.0 / 240)
        {
            float h = (float)Math.Min(t, 1.0 / 240);
            _slideVel += (rem * w * w - _slideVel * 2 * w) * h;
            if (_slideVel.Length() > maxSpeed) _slideVel = Vector3.Normalize(_slideVel) * maxSpeed;
            var d = _slideVel * h;
            rem -= d;
            moved += d;
        }
        _correcting = rem.Length() < 0.005f && _slideVel.Length() < 0.02f ? null : rem;
        return moved;
    }

    // --- the room's floor -------------------------------------------------------------------------------------------
    // The floor (the avatar's feet plus the lift) follows the region's height through a critically damped spring fed
    // by the region's own vertical speed, so steps, slopes and each update's small error turn into a smooth glide.
    // Standing still it holds within a few centimetres: the region's height settling by millimetres never moves it.

    bool _floorSet, _floorSnapped;
    float? _snapFrom;   // where the floor was before a jump of over 3 m this frame
    float _floor, _floorVel, _groundHeld, _hover, _lift, _modelUnder, _airOffset;
    double _modelSeenAt = -100, _jumpAt = -100;
    bool _onModel;

    /// <summary>Where the room's floor should be (before the lift). Wherever the local model (the terrain's heightmap and
    /// the prims' drawn tops, VrPilot.Collide.cs) has the avatar standing where the region has its feet, it's the
    /// surface under the head while the stick walks, read locally, so walking over hills, kerbs and decks needs nothing
    /// from the network, and the surface under the avatar otherwise: standing, a lean or a step in the room never moves
    /// the floor, even on a slope (the head-jitter findings of 2026-10-26: following the ground under the head glided
    /// the world a few cm as the head moved); the room-scale follow walk moves it with the avatar. The surface under the
    /// head counts only within a step up or down (a drop waits for the avatar to fall). The avatar's own small height
    /// over the surface is learned slowly. Where the model doesn't match (flying, jumping, falling, a floor still
    /// loading, physics unlike the drawn shape) it's the avatar's feet, as far from them as the view stood when it left
    /// the model: led by the region's climb rate while walking, and held while the avatar steps about away from the
    /// head, so the room never bobs to ground the head isn't over. A jump or a fall is followed at once (the session of
    /// 2026-10-27: a jump was held on the ground for 0.8 s, then the view jumped metres up to the avatar unblinked).
    /// Underfoot keeps the result from sinking through anything the model knows.</summary>
    float GroundUnder(Simulator sim, uint self, Vector3 headFlat, Vector3 avatar, Vector3 raw, float footDepth, float vz, bool walking, double dt)
    {
        float ground = GroundModel(sim, self, headFlat, avatar, raw, footDepth, vz, walking, dt);
        // Once the region's avatar has landed and holds still under the head (not walked by the stick or the follow,
        // its position unchanged with no velocity for 0.3 s), its feet are the floor, up or down: the local model is
        // there to lead the walk and catch falls, and where it disagrees with an avatar standing still it's the model
        // that's wrong (the user's session of 2026-10-27, Inarishima: after falls the view rested 0.1-0.5 m high, held
        // by a surface the region doesn't have). Underfoot lets go of anything holding the view above it.
        double now = _clock.Elapsed.TotalSeconds;
        float headGap = Vector3.Distance(Flat(raw), Flat(headFlat));
        _onRegion = !walking && !Flying && _follow == Follow.Idle && now - _steadySince > 0.3 && headGap < 0.8f ? raw.Z - footDepth : null;
        // walking: the region's avatar keeps level (no climb, its height unchanged for 0.4 s) near the head
        _walkLevel = walking && !Flying && now - _levelSince > 0.4 && headGap < 1.6f ? raw.Z - footDepth : null;
        return _onRegion is { } rf ? _groundHeld = rf : ground;
    }

    float GroundModel(Simulator sim, uint self, Vector3 headFlat, Vector3 avatar, Vector3 raw, float footDepth, float vz, bool walking, double dt)
    {
        float feet = avatar.Z - footDepth;
        double now = _clock.Elapsed.TotalSeconds;
        bool jumped = now - _jumpAt < 1.0;
        float? support = Flying ? null : Support(sim, self, avatar.X, avatar.Y, feet + 0.3f, 1.2f);
        bool matches = support is { } sup && MathF.Abs(feet - sup - _hover) < 0.25f;
        if (!matches && !Flying)
        {
            // landing, the drawn avatar runs on down past the floor until the region's word that it stopped arrives
            // (then glides back up): the region's own position has it on the floor first
            float rawFeet = raw.Z - footDepth;
            if (Support(sim, self, raw.X, raw.Y, rawFeet + 0.3f, 1.2f) is { } rs && MathF.Abs(rawFeet - rs - _hover) < 0.25f)
            {
                support = rs;
                matches = true;
                feet = rawFeet;
            }
        }
        if (matches)
        {
            // the region's avatar stepped up onto something taller than the step height held so far: it climbs that
            // (from one frame to the next on the ground, so a jump's landing doesn't count)
            if (walking && _onModel && !jumped && now - _modelSeenAt < 0.15 && support!.Value - _modelUnder is > 0.1f and < 0.75f and var rise && rise > StepHeight)
            {
                StepHeight = rise + 0.02f;
                Say?.Invoke($"steps: the region climbed {rise:F2} m; {StepHeight:F2} from now on");
            }
            _modelSeenAt = now;
            _modelUnder = support!.Value;
        }
        // walking, a moment's mismatch (the region's avatar hops a little on a step's edge) keeps to the model; a jump,
        // a fall or a launch (more than a step off it, or the person pressed jump) doesn't
        else if (walking && _onModel && !jumped && now - _modelSeenAt < 0.8 && MathF.Abs(feet - _modelUnder - _hover) < StepHeight + 0.1f)
        {
            support = _modelUnder;
            matches = true;
        }
        if (matches && support is { } under)
        {
            if (!_onModel) _hover = Math.Clamp(feet - under, -0.2f, 0.2f);
            _onModel = true;
            if (now == _modelSeenAt) _hover += (Math.Clamp(feet - under, -0.2f, 0.2f) - _hover) * 0.01f;
            float ground = under;
            if (walking && Support(sim, self, headFlat.X, headFlat.Y, under + StepHeight + StepSlack, 2 * StepHeight + StepSlack) is { } h
                && h >= under - StepHeight)
            {
                ground = h;
                // a step up the view leads onto only where the region's said what the thing is (a mesh plant is its hull
                // until then): otherwise it waits for the avatar to climb it
                if (h > under + 0.05f && (Support(sim, self, headFlat.X, headFlat.Y, under + StepHeight + StepSlack, 2 * StepHeight + StepSlack, knownOnly: true) ?? under) is var known
                    && known < h - 0.02f)
                    ground = MathF.Max(known, under);
            }
            // (where the view stands relative to the avatar's feet, for when it leaves the model)
            if (now == _modelSeenAt) _airOffset = ground - under + MathF.Max(_hover, 0) - _hover;
            // (walking, the view keeps to the model's surface even where the region holds the avatar a little lower;
            // standing still, GroundUnder puts it on the region's feet)
            return _groundHeld = ground + MathF.Max(_hover, 0);
        }
        _onModel = false;
        _airOffset *= MathF.Exp(-(float)dt / 3f);
        float target = feet + _airOffset;
        bool away = !walking && (_follow is Follow.Settling or Follow.Walking);
        if (away && _floorSet && !jumped && MathF.Abs(vz) < 1.5f && MathF.Abs(target - _groundHeld) < StepHeight + 0.1f) return _groundHeld;
        // (the lead is for stairs and ramps the model doesn't know, not a jump's or a fall's speed: that ran the view
        // a metre and more past the landing)
        return _groundHeld = target + (walking ? Math.Clamp(vz, -1.5f, 1.5f) * _latency : 0);
    }

    /// <summary>The region's terrain height at a point (bilinear between its 1 m samples), if it has arrived.</summary>
    float? TerrainAt(Simulator sim, float x, float y)
    {
        if (_session.World.Region(_session.GridKey, sim.Handle) is not { } world) return null;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        if (x0 < 0 || y0 < 0 || x0 + 1 >= sim.SizeX || y0 + 1 >= sim.SizeY) return null;
        if (world.TerrainHeight(x0, y0) is not { } h00 || world.TerrainHeight(x0 + 1, y0) is not { } h10 ||
            world.TerrainHeight(x0, y0 + 1) is not { } h01 || world.TerrainHeight(x0 + 1, y0 + 1) is not { } h11) return null;
        float fx = x - x0, fy = y - y0;
        return (h00 * (1 - fx) + h10 * fx) * (1 - fy) + (h01 * (1 - fx) + h11 * fx) * fy;
    }

    float Floor(float target, float vz, bool moving, double dt)
    {
        if (!_floorSet || MathF.Abs(target - _floor) > 3f)
        {
            // (a jump of the view this big is a cut, not a glide, once Underfoot has kept it out of the floor: after a
            // hitch mid-fall it still lands; only a floor put somewhere new skips that)
            if (_floorSet) _snapFrom = _floor;
            else { _floorSnapped = true; _snapFrom = null; }
            _floor = target;
            _floorVel = 0;
            _floorSet = true;
            return _floor;
        }
        float feed = MathF.Abs(vz) > 0.3f ? vz : 0f;
        if (!moving && feed == 0 && MathF.Abs(target - _floor) < 0.04f && MathF.Abs(_floorVel) < 0.02f)
        {
            _floorVel = 0;
            return _floor;
        }
        const float w = 9f;
        for (double t = dt; t > 1e-6; t -= 1.0 / 240)
        {
            float h = (float)Math.Min(t, 1.0 / 240);
            _floorVel += ((target - _floor) * w * w + (feed - _floorVel) * 2 * w) * h;
            _floor += _floorVel * h;
            target += feed * h;
        }
        return _floor;
    }

    /// <summary>SOJOURN_VR_LOG=path: per frame, the head in the room, the head and the avatar in the region, the
    /// follow state, whether walking, the room's yaw and the body's, the region's position and velocity for the avatar
    /// (checking the rig without a headset on).</summary>
    static readonly StreamWriter? Log = DiagLog.Open("SOJOURN_VR_LOG");

    /// <summary>Let go of every control (the headset session ended, shutting down).</summary>
    public void Release(bool keepPlace = false)
    {
        if (_session.Client is not { } c) return;
        var mv = c.Self.Movement;
        mv.AtPos = mv.AtNeg = mv.FastAt = mv.NudgeAtPos = mv.NudgeAtNeg = false;
        mv.LeftPos = mv.LeftNeg = mv.FastLeft = mv.NudgeLeftPos = mv.NudgeLeftNeg = false;
        mv.UpPos = mv.UpNeg = mv.FastUp = false;
        mv.Stop = false;
        _brakeSince = -1;
        if (mv.AlwaysRun) mv.AlwaysRun = false;
        if (c.Network.Connected) mv.SendUpdate(true);
        _follow = Follow.Idle;
        _lastControls = 0;
        if (!keepPlace) _placed = false;
    }

    void ToggleFly(GridClient c)
    {
        bool fly = !c.Self.Movement.Fly;
        c.Self.Fly(fly);
        c.Self.Movement.Fly = fly;
        Say?.Invoke(fly ? "flying" : "landing");
    }

    /// <summary>Keyboard stand-ins (the mirror window focused): WASD/arrows the left stick, Q/E turn, Space climbs or
    /// jumps, C sinks, F toggles flying.</summary>
    (Vector2 Stick, int Turn, bool Up, bool Down, bool Fly) Keys(IKeyboard? kb)
    {
        if (kb == null) return default;
        bool Down(params Key[] keys) => keys.Any(kb.IsKeyPressed);
        bool Pressed(Key k)
        {
            bool down = kb.IsKeyPressed(k), was = _keysBefore.Contains(k);
            if (down) _keysBefore.Add(k); else _keysBefore.Remove(k);
            return down && !was;
        }
        var stick = new Vector2((Down(Key.D, Key.Right) ? 1 : 0) - (Down(Key.A, Key.Left) ? 1 : 0),
                                (Down(Key.W, Key.Up) ? 1 : 0) - (Down(Key.S, Key.Down) ? 1 : 0));
        int turn = !Settings.SmoothTurn
            ? (Pressed(Key.Q) ? 1 : 0) - (Pressed(Key.E) ? 1 : 0)
            : (Down(Key.Q) ? 1 : 0) - (Down(Key.E) ? 1 : 0);
        return (stick, turn, Down(Key.Space), Down(Key.C, Key.PageDown), Pressed(Key.F));
    }

    /// <summary>The avatar as the renderer draws it this frame, its update dated by when it came in (SelfUpdateGuard;
    /// not while the test lag holds the region's word back, its position then not the last one heard).
    /// <paramref name="snap"/>: on the region's trajectory at once (a jump).</summary>
    Vector3 SmoothedAvatar(GridClient c, Simulator sim, Vector3 raw, Vector3 vel, bool snap = false)
    {
        if (c.Self.SittingOn != 0) return raw;
        long? arrived = SelfUpdateGuard.LastArrival(c) is { } la && la.Pos.X == raw.X && la.Pos.Y == raw.Y && la.Pos.Z == raw.Z ? la.Ticks : null;
        return _motion.Position(c.Self.AgentID, sim.Handle, raw, vel, sim.SizeX > 0 ? sim.SizeX : 256f, arrived, snap);
    }

    /// <summary>Whether a region the session has a circuit to lies at a point given in a region's coordinates
    /// (MotionSmoother.RegionAt: no avatar is drawn running on over an edge with nothing beyond). The regions are
    /// read at most twice a second, and never waited for (LibreMetaverse holds their list while it connects one).</summary>
    internal sealed class KnownRegions(AgentSession session)
    {
        (uint X, uint Y, uint W, uint H)[] _regions = [];
        long _readAt;

        public bool At(ulong handle, System.Numerics.Vector2 p)
        {
            if (session.Client is not { } c) return true;
            long now = Stopwatch.GetTimestamp();
            if (now - _readAt > Stopwatch.Frequency / 2 && Monitor.TryEnter(c.Network.Simulators))
                try
                {
                    _regions = c.Network.Simulators.Select(s =>
                    {
                        Utils.LongToUInts(s.Handle, out uint sx, out uint sy);
                        return (sx, sy, s.SizeX > 0 ? s.SizeX : 256u, s.SizeY > 0 ? s.SizeY : 256u);
                    }).ToArray();
                    _readAt = now;
                }
                finally { Monitor.Exit(c.Network.Simulators); }
            if (_regions.Length == 0) return true;
            Utils.LongToUInts(handle, out uint hx, out uint hy);
            double gx = hx + (double)p.X, gy = hy + (double)p.Y;
            foreach (var (x, y, w, h) in _regions)
                if (gx >= x && gy >= y && gx < x + w && gy < y + h) return true;
            return false;
        }
    }

    readonly SelfLag _lag = SelfLag.FromEnvironment();

    /// <summary>The region's word on the own avatar (position and velocity). For testing on the local grid, where it
    /// answers at once: SOJOURN_SELF_LAG_MS delays it like the main grid's round trip, SOJOURN_SELF_UPDATE_MS spaces its
    /// updates (the main grid's ~60-110 ms while walking), SOJOURN_SELF_NOISE_MM adds that much error to each.
    /// SOJOURN_SELF_STALL_EVERY_S with SOJOURN_SELF_STALL_MS makes the region stall now and then, as a slow main-grid
    /// sim does (2026-10-28, 00:05:34): no updates for that long, then positions come back about two thirds of it
    /// behind, catching up over a second.</summary>
    sealed class SelfLag(double lag, double every, float noise, double stallEvery = 0, double stall = 0)
    {
        readonly Queue<(double At, Vector3 Pos, Vector3 Vel)> _queue = new();
        (Vector3 Pos, Vector3 Vel) _shown;
        double _shownAt = double.MinValue;
        LVector3 _last, _lastVel;
        double _changedAt, _enqueuedAt = double.MinValue;
        ulong _handle;
        double _handleAt = -100;
        readonly Random _rng = new(1);
        bool Active => lag > 0 || every > 0 || noise > 0 || stallEvery > 0;

        /// <summary>The next read gives the current position at once, as the first one does (after a teleport's
        /// arrival: before, one to a spot under 20 m from the old one in the old region's coordinates was taken for
        /// motion and delayed, so the arrival's cut went to the old spot and a second cut followed).</summary>
        public void Restart()
        {
            _queue.Clear();
            _shownAt = double.MinValue;
        }

        public static SelfLag FromEnvironment()
        {
            static double Env(string n) => double.TryParse(System.Environment.GetEnvironmentVariable(n), out var v) ? v : 0;
            return new SelfLag(Env("SOJOURN_SELF_LAG_MS") / 1000, Env("SOJOURN_SELF_UPDATE_MS") / 1000, (float)Env("SOJOURN_SELF_NOISE_MM") / 1000,
                Env("SOJOURN_SELF_STALL_EVERY_S"), Env("SOJOURN_SELF_STALL_MS") / 1000);
        }

        public (Vector3 Pos, Vector3 Vel) Read(GridClient c, double now)
        {
            var p = c.Self.SimPosition;
            var v = c.Self.Velocity;
            var pos = new Vector3(p.X, p.Y, p.Z);
            var vel = new Vector3(v.X, v.Y, v.Z);
            if (!Active) return (pos, vel);
            ulong handle = c.Network.CurrentSim?.Handle ?? 0;
            if (handle != _handle) (_handle, _handleAt) = (handle, now);
            // OpenSim sends an avatar's position only as its velocity changes; SL every 60-110 ms while it moves. So
            // while it moves, an update every SOJOURN_SELF_UPDATE_MS where it is by then (the last one carried on by its
            // velocity). Before, the last position was stamped afresh instead, so OpenSim's quiet steady walks looked
            // like the same old position arriving again and again (the view in server movement stood still, then blinked)
            bool news = p != _last || v != _lastVel || _shownAt == double.MinValue;
            if (news || every > 0 && vel.Length() > 0.05f && now - _enqueuedAt >= every)
            {
                if (news) (_last, _lastVel, _changedAt) = (p, v, now);
                var n = noise > 0 ? new Vector3((float)_rng.NextDouble() - 0.5f, (float)_rng.NextDouble() - 0.5f, 0) * 2 * noise : Vector3.Zero;
                _queue.Enqueue((now, pos + vel * (float)(now - _changedAt) + n, vel));
                _enqueuedAt = now;
            }
            // A crossing: what's waiting is read in the new region's coordinates and comes as late as the rest. (Before,
            // the crossing's position was shown at once, a lag's worth of travel ahead of the last, 1.9 m at 12.6 m/s,
            // which the main grid, where everything comes equally late, doesn't do.) Known by the position moving a whole
            // number of regions, give or take the travel, around a change of region
            if (_queue.Count > 0 && _shownAt != double.MinValue && Math.Abs(now - _handleAt) < 3
                && _queue.Last().Pos is var newest && newest - _shown.Pos is var jump && jump.Length() > 20
                && new Vector3(MathF.Round(jump.X / 256) * 256, MathF.Round(jump.Y / 256) * 256, 0) is var o
                && o != Vector3.Zero && Vector3.Distance(jump, o) < 20)
            {
                var queued = _queue.ToArray();
                _queue.Clear();
                foreach (var (at, qp, qv) in queued)
                    _queue.Enqueue((at, Vector3.Distance(qp + o, newest) < Vector3.Distance(qp, newest) ? qp + o : qp, qv));
                _shown.Pos += o;
            }
            // the first answer, and a jump (a teleport), aren't delayed: the region's coordinates changed under it
            if (_queue.Count > 0 && (_shownAt == double.MinValue || Vector3.Distance(_queue.Last().Pos, _shown.Pos) > 20)) { _shown = (_queue.Last().Pos, _queue.Last().Vel); _queue.Clear(); _shownAt = now; return _shown; }
            double late = lag;
            if (stallEvery > 0)
            {
                double into = now % stallEvery;   // a stall at the start of each period
                if (into < stall) return _shown;
                late += stall * 0.65 * Math.Max(0, 1 - (into - stall));
            }
            while (_queue.Count > 0 && now - _queue.Peek().At >= late && now - _shownAt >= every)
            {
                var (_, qp, qv) = _queue.Dequeue();
                // the newest sample that's old enough
                while (_queue.Count > 0 && now - _queue.Peek().At >= late) (_, qp, qv) = _queue.Dequeue();
                _shown = (qp, qv);
                _shownAt = now;
            }
            return _shown;
        }
    }

    /// <summary>How far the feet are below the agent's position: half the avatar's height, which the region sends as
    /// its scale (the size the appearance gave it).</summary>
    /// <summary>How far the room's floor is above the avatar's feet. Standing at your own height, 0 (your real floor is
    /// its feet). At the avatar's eye height, the difference between its eyes and your standing ones. Seated, between its
    /// eyes and where your head was when you sat (or recentred). A space without a floor (local) starts at the eyes.</summary>
    float Lift(GridClient c, Simulator sim, float headInRoom, bool floorLevel)
    {
        float avatarEye = AvatarEyeHeight is > 0.5f and < 4f and var e ? e : Math.Max(0.6f, 2 * FootDepth(c, sim) - 0.12f);
        if (!floorLevel) return avatarEye;
        float stand = StandingEye(headInRoom);
        if (Settings.Seated)
        {
            _seatedRef ??= headInRoom;
            return avatarEye - _seatedRef.Value;
        }
        _seatedRef = null;
        return Settings.EyeHeight == EyeHeightMode.Avatar ? avatarEye - stand : 0;
    }

    /// <summary>The person's standing eye height: the highest their head has been over the last 20 s (a crouch or a
    /// look at the floor doesn't lower it; sitting down for a while does).</summary>
    float StandingEye(float headInRoom)
    {
        long sec = (long)_clock.Elapsed.TotalSeconds;
        if (_standSec < 0) Array.Fill(_standMax, headInRoom);
        else
            for (long t = _standSec + 1; t <= sec && t <= _standSec + _standMax.Length; t++) _standMax[t % _standMax.Length] = headInRoom;
        _standSec = sec;
        ref float slot = ref _standMax[sec % _standMax.Length];
        slot = Math.Max(slot, headInRoom);
        return Math.Clamp(_standMax.Max(), 0.9f, 2.3f);
    }

    /// <summary>Whether the own avatar's arms reach for the controllers (the tracking setting: in auto, not while
    /// something around you animates you, Core/Agents/SelfAnimations.cs).</summary>
    bool HandsTracked() => Settings.Tracking switch
    {
        HandTracking.On => true,
        HandTracking.Off => false,
        _ => !_session.Anims.AnimatedByThings(),
    };

    /// <summary>Where the own avatar's wrist goes for a controller held in that hand (its grip pose, in the room).
    /// OpenXR's grip pose sits in the handle where the palm wraps it: its -Z runs along the tube the closed fingers make
    /// (little finger to thumb), its +X out of the left palm and into the right one, so the palm faces +X on the left
    /// and -X on the right. The hand's own length (wrist to knuckles) isn't the tube: a Touch handle lies across the
    /// fingers at a slant, and Meta's runtime puts the controller's aim (the way the hand points) 60 degrees from the
    /// grip's -Z toward its -Y, which is where the hand's bones run. The wrist joint sits 7.5 cm back along them from the
    /// handle's centre and 3 cm toward the back of the hand (the handle is in the palm, the bones above it).</summary>
    HandTarget? Hand(Quaternion? grip, Vector3? at, bool left)
    {
        if (grip is not { } q || at is not { } p) return null;
        var world = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _yaw) * ToSl(q));
        var tube = Vector3.Transform(Vector3.UnitX, world);          // the grip's -Z
        var gripY = Vector3.Transform(Vector3.UnitZ, world);         // the grip's +Y
        var gripX = Vector3.Transform(-Vector3.UnitY, world);        // the grip's +X
        const float slant = 60 * MathF.PI / 180;
        var fingers = Vector3.Normalize(tube * MathF.Cos(slant) - gripY * MathF.Sin(slant));
        var back = left ? -gripX : gripX;
        var wrist = RoomToRegion(p) - fingers * 0.075f + back * 0.03f;
        // the skeleton's hand frame: Y along the arm outward (the fingers on the left, back toward the arm on the
        // right), Z the back of the hand, X = Y x Z
        var y = left ? fingers : -fingers;
        var z = back;
        var x = Vector3.Cross(y, z);
        var m = new System.Numerics.Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return new HandTarget(wrist, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m)));
    }

    /// <summary>Where a watch would sit on the left wrist (4 cm off the back of it, in the region): voice's mic token.</summary>
    public Vector3? Watch(XrControllers input) =>
        Hand(input.LeftHand, input.LeftHandPos, left: true) is { } h ? h.Wrist + Vector3.Transform(Vector3.UnitZ, h.Rotation) * 0.04f : null;

    /// <summary>The same, moved along the forearm toward the elbow by <paramref name="along"/> metres (the HUD token's place).</summary>
    public Vector3? Watch(XrControllers input, float along) =>
        Hand(input.LeftHand, input.LeftHandPos, left: true) is { } h
            ? h.Wrist + Vector3.Transform(Vector3.UnitZ, h.Rotation) * 0.04f - Vector3.Transform(Vector3.UnitY, h.Rotation) * along : null;

    static float FootDepth(GridClient c, Simulator sim)
    {
        if (sim.ObjectsAvatars.TryGetValue(c.Self.LocalID, out var av) && av.Scale.Z is > 0.5f and < 4f) return av.Scale.Z * 0.5f;
        return 0.95f;
    }

    /// <summary>Whether an update's step (<paramref name="step"/>, over <paramref name="since"/> s since the last, which
    /// had velocity <paramref name="vel"/>) went the avatar's own way, short of the prediction or a little past it,
    /// and not off to the side: a region's late or bunched updates while moving (over 1 m/s), not a teleport or a push.</summary>
    static readonly bool NoAlongTheWay = System.Environment.GetEnvironmentVariable("SOJOURN_ALONG_THE_WAY") == "0";   // (for comparing)

    static bool AlongTheWay(Vector3 step, Vector3 vel, float since)
    {
        float speed = vel.Length();
        if (speed < 1f) return false;
        var dir = vel / speed;
        float along = Vector3.Dot(step, dir);
        float across = (step - dir * along).Length();
        return along > -0.5f && along < speed * since + MathF.Max(2f, speed * 0.5f) && across < 1f + 0.15f * MathF.Abs(along);
    }

    static Vector3 RegionOffset(ulong from, ulong to)
    {
        Utils.LongToUInts(from, out uint fx, out uint fy);
        Utils.LongToUInts(to, out uint tx, out uint ty);
        return new Vector3((float)((long)fx - tx), (float)((long)fy - ty), 0);
    }

    static Vector3 ToSl(Vector3 v) => new(-v.Z, -v.X, v.Y);
    static Quaternion ToSl(Quaternion q) => new(-q.Z, -q.X, q.Y, q.W);
    static Vector3 Flat(Vector3 v) => new(v.X, v.Y, 0);
    static Vector3 Rotate(Vector3 v, float yaw) => Vector3.Transform(v, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw));

    /// <summary>Heading of an SL-frame orientation (radians from east), steady when looking straight up or down.</summary>
    static float Yaw(Quaternion q)
    {
        var f = Vector3.Transform(Vector3.UnitX, q);
        if (f.X * f.X + f.Y * f.Y < 0.04f)
        {
            var up = Vector3.Transform(Vector3.UnitZ, q);
            f = f.Z > 0 ? -up : up;   // looking up, the top of the head points back
        }
        return MathF.Atan2(f.Y, f.X);
    }

    long _framesSinceReport;
    int _octant;   // which way the body moves relative to its facing, in eighths of a turn counter-clockwise (4: back)

    /// <summary>The body's facing for a move toward <paramref name="moveYaw"/> with the head toward
    /// <paramref name="headYaw"/>: the region moves an avatar only forward, back, sideways or diagonally relative to its
    /// body, so the body faces the head within 22.5 degrees, turned so that one of those eight is exactly the move
    /// (held until the head is 30 degrees past it, so it doesn't flick between two).</summary>
    float Octant(float moveYaw, float headYaw)
    {
        const float eighth = MathF.PI / 4;
        float rel = AngleDiff(moveYaw, headYaw);
        if (MathF.Abs(AngleDiff(rel, _octant * eighth)) > 30f * MathF.PI / 180f)
        {
            _octant = (int)MathF.Round(rel / eighth);
            if (_octant == -4) _octant = 4;
        }
        return moveYaw - _octant * eighth;
    }

    static float AngleDiff(float a, float b)
    {
        float d = (a - b) % (2 * MathF.PI);
        if (d > MathF.PI) d -= 2 * MathF.PI;
        if (d < -MathF.PI) d += 2 * MathF.PI;
        return d;
    }
}
