using LibreMetaverse;
using LQuaternion = LibreMetaverse.Quaternion;
using LVector3 = LibreMetaverse.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>Walk mode's keys and what they send: held keys become the agent's control flags (as Sojourn.Client's desktop
/// Pilot sends them), turning is the body's rotation (a tap a step, held smooth), and letting go brakes with the
/// viewer's STOP flag (docs/vr.md > Braking) so the avatar doesn't coast on past where the person stopped.</summary>
public sealed partial class WalkMode
{
    enum K { Fwd, Back, TurnL, TurnR, SideL, SideR, Up, Down }
    readonly bool[] _k = new bool[8];
    // a tap counts as held a moment at least, so it reaches the region (a key down and up between two ticks would
    // send nothing): a short step walking, a nudge of a vehicle's steering
    readonly long[] _until = new long[8];
    readonly Dictionary<Keys, K> _pressedAs = new();
    bool Down(K k) => _k[(int)k] || Environment.TickCount64 < _until[(int)k];
    bool Held => Enumerable.Range(0, 8).Any(i => Down((K)i));
    bool HeldMove => Down(K.Fwd) || Down(K.Back) || Down(K.SideL) || Down(K.SideR);
    int Get(K k) => Down(k) ? 1 : 0;

    float _yaw;
    bool _wasHeld, _ranSet, _turnSmooth, _toldBlocked;
    long _turnSince, _moveSince, _brakeUntil, _brakeFrom, _sentAt;
    uint _lastControls;

    const ScriptControlChange MoveKeys = ScriptControlChange.Forward | ScriptControlChange.Back | ScriptControlChange.Left |
        ScriptControlChange.Right | ScriptControlChange.RotateLeft | ScriptControlChange.RotateRight;

    /// <summary>Seated, or a script keeps the movement keys: the arrows drive (CONTROL_FWD, ROT_LEFT ...) instead of walking.</summary>
    bool Driving(Body b) => b.Sitting || (S.ControlsKept & MoveKeys) != 0;

    /// <summary>F1 in walk mode: its keys, one a line under short headings (shown in a window to read, MainForm.ShowKeys).</summary>
    public const string Help = """
        Moving
        Up and Down arrows: walk forward and back
        Shift with Up or Down: run
        Left and Right: turn, a tap a step, held smoothly
        Shift with Left or Right: step sideways
        Page Up and Page Down: up and down (flying, or jump and crouch)
        Home: fly or land
        Space: stop
        Seated on a vehicle, the arrows drive it

        Asking
        H: your heading
        V: your speed
        L: where you are
        A: what's ahead
        S: the sonar sweep
        D: a door or opening nearby
        O: doors, seats and touchable things around, each sounding from where it is
        B: where the beacon is
        Shift+B: take the beacon off
        M: how far you walked, and where you began counting
        Shift+M: count from here
        After you walk and pause, how far is said

        Doing
        T: touch what's ahead

        Leaving
        Escape or F3: leave walk mode
        """;

    /// <summary>The walk pad got the focus.</summary>
    public void Enter()
    {
        On = true;
        _toldSurface = null;
        Sounds.Play(CueSounds.Cue.ModeOn, 0.45f, null);
    }

    /// <summary>The walk pad lost the focus: everything let go.</summary>
    public void Leave()
    {
        if (!On) return;
        On = false;
        ReleaseAll();
        Sounds.Play(CueSounds.Cue.ModeOff, 0.45f, null);
    }

    public void ReleaseAll()
    {
        bool moving = HeldMove;
        Array.Clear(_k);
        Array.Clear(_until);
        _pressedAs.Clear();
        _turnSmooth = false;
        if (moving) StartBrake();
    }

    /// <summary>A key down on the walk pad; true when it's walk mode's.</summary>
    public bool KeyDown(Keys code, bool shift, bool repeat)
    {
        K? k = code switch
        {
            Keys.Up => K.Fwd, Keys.Down => K.Back, Keys.PageUp => K.Up, Keys.PageDown => K.Down,
            Keys.Left => shift ? K.SideL : K.TurnL, Keys.Right => shift ? K.SideR : K.TurnR,
            _ => null,
        };
        if (k is { } key)
        {
            if (repeat || _pressedAs.ContainsKey(code)) return true;   // held: the key's repeats change nothing
            _pressedAs[code] = key;
            TakeOver();
            _k[(int)key] = true;
            long now = Environment.TickCount64;
            _until[(int)key] = now + 200;
            if (key is K.TurnL or K.TurnR)
            {
                _turnSince = now;
                _turnSmooth = _set.WalkTurnStep <= 0;
                if (!_turnSmooth && Me() is { } b && !Driving(b)) Step(key == K.TurnL ? -1 : +1, b);
            }
            if (key is K.Fwd or K.Back or K.SideL or K.SideR && !HeldMoveBefore(key)) { _moveSince = now; _toldBlocked = false; }
            return true;
        }
        if (repeat) return code is Keys.Home or Keys.Space or Keys.H or Keys.V or Keys.L or Keys.A or Keys.S or Keys.T or Keys.D or Keys.O or Keys.B or Keys.M;
        switch (code)
        {
            case Keys.Home: ToggleFly(); return true;
            case Keys.Space: StopAll(); return true;
            case Keys.H: Heading(); return true;
            case Keys.V: SpeedLine(); return true;
            case Keys.L: WhereLine(); return true;
            case Keys.A: _ = AheadAsync(); return true;
            case Keys.S: _ = SweepAsync(); return true;
            case Keys.T: _ = TouchAheadAsync(); return true;
            case Keys.D: _ = NearestDoorAsync(); return true;
            case Keys.O: _ = MarksAsync(); return true;
            case Keys.B when shift: BeaconOff(); return true;
            case Keys.B: BeaconLine(); return true;
            case Keys.M when shift: DistanceReset(); return true;
            case Keys.M: DistanceLine(); return true;
        }
        return false;
    }

    bool HeldMoveBefore(K pressed) => Enumerable.Range(0, 8).Any(i => i != (int)pressed && (K)i is K.Fwd or K.Back or K.SideL or K.SideR && _k[i]);

    public void KeyUp(Keys code)
    {
        if (!_pressedAs.Remove(code, out var k)) return;
        bool wasMoving = _k[(int)K.Fwd] || _k[(int)K.Back] || _k[(int)K.SideL] || _k[(int)K.SideR];
        _k[(int)k] = false;
        if (k is K.TurnL or K.TurnR && !_k[(int)K.TurnL] && !_k[(int)K.TurnR])
        {
            if (_turnSmooth && Me() is { } b && !Driving(b)) Turned(b, stepped: false);
            _turnSmooth = false;
        }
        if (wasMoving && !(_k[(int)K.Fwd] || _k[(int)K.Back] || _k[(int)K.SideL] || _k[(int)K.SideR])) StartBrake();
    }

    /// <summary>The person moves by hand: a walk, follow or route the app was doing stops, and the autopilot lets go.</summary>
    void TakeOver()
    {
        if (Held) return;
        StopOthers?.Invoke();
        if (S.Client is { } c && S.Motion.Moving) { S.Motion.StopAutoPilot(c, false); S.Motion.Stop(c); }
        // from the body as it is now (the tick follows it, but a //face or the autopilot may have turned it since)
        if (!_wasHeld && Me() is { } b && !Driving(b)) _yaw = b.Yaw;
    }

    /// <summary>A tap of Left or Right: the facing moves to the next multiple of the step that way (so steps of 22.5
    /// land on the compass's sixteen points).</summary>
    void Step(int dir, Body b)
    {
        double step = _set.WalkTurnStep, compass = Compass(_wasHeld || Held ? _yaw : b.Yaw);
        double next = dir > 0 ? (Math.Floor(compass / step + 1e-6) + 1) * step : (Math.Ceiling(compass / step - 1e-6) - 1) * step;
        _yaw = (float)((90 - Bearings.Norm(next)) * Math.PI / 180);
        SendBody();
        Turned(b with { Yaw = _yaw }, stepped: true);
    }

    void Turned(Body b, bool stepped)
    {
        double compass = Compass(_yaw);
        double step = _set.WalkTurnStep;
        bool onPoint = stepped && step > 0 && Math.Abs(Math.IEEERemainder(step, 22.5)) < 1e-6;
        Cue("turns", CueSounds.Cue.Turn, onPoint ? $"{Bearings.Point(compass)}." : $"{Bearings.Point(compass)}, {Bearings.ToSl(compass)} degrees.");
    }

    void SendBody()
    {
        if (S.Client is not { } c) return;
        var body = LQuaternion.CreateFromAxisAngle(LVector3.UnitZ, _yaw);
        c.Self.Movement.BodyRotation = c.Self.Movement.HeadRotation = body;
        c.Self.Movement.SendUpdate(true);
        _sentAt = Environment.TickCount64;
    }

    void StartBrake()
    {
        long now = Environment.TickCount64;
        _brakeFrom = now;
        _brakeUntil = now + 1000;
    }

    /// <summary>Home in walk mode, and the menu bar's Fly: flying, or landing.</summary>
    public void ToggleFly()
    {
        if (S.Client is not { } c || Me() is not { } b) return;
        if (b.Sitting) { Line("Seated: stand first (//stand)."); return; }
        bool fly = !c.Self.Movement.Fly;
        c.Self.Fly(fly);
        c.Self.Movement.Fly = fly;
        Line(fly ? "Flying." : "Not flying: landing.");
    }

    void StopAll()
    {
        StopOthers?.Invoke();
        if (S.Client is { } c && S.Motion.Moving) { S.Motion.StopAutoPilot(c, false); S.Motion.Stop(c); }
        ReleaseAll();
        StartBrake();
        Line("Stopped.");
    }

    /// <summary>Held keys to the region, every tick (the flags are sent at least every 100 ms while held, as the
    /// desktop pilot does); nothing is touched while no key is held and nothing is braking, so the autopilot and the
    /// assistant's moves have the flags to themselves.</summary>
    void Drive(Body b, double dt, long now)
    {
        if (S.Client is not { Network.Connected: true } c) return;
        var mv = c.Self.Movement;
        bool driving = Driving(b);
        bool held = Held;
        int fwd = Get(K.Fwd) - Get(K.Back), side = Get(K.SideR) - Get(K.SideL), up = Get(K.Up) - Get(K.Down), turn = Get(K.TurnL) - Get(K.TurnR);
        if (!driving)
        {
            // the facing follows the body (a //face, the autopilot) whenever no key here moves or turns it. (Following only
            // a body unlike the one last sent here missed a turn back to that same rotation: //face north after an
            // autopilot had turned it, and the next held Up walked on at the autopilot's heading.)
            if (!held && !_wasHeld) _yaw = b.Yaw;
            if (_k[(int)K.TurnL] || _k[(int)K.TurnR]) { if (!_turnSmooth && _set.WalkTurnStep > 0 && now - _turnSince > 450) _turnSmooth = true; }
            else turn = 0;   // a tap turned its step already
            if (turn != 0 && _turnSmooth) _yaw += (float)(turn * _set.WalkTurnRate * Math.PI / 180 * dt);
        }
        bool braking = now < _brakeUntil && !held && !driving && !b.Flying;
        if (braking && now - _brakeFrom > 150 && Speed(b) < 0.1f) { _brakeUntil = 0; braking = false; }
        if (!held && !_wasHeld && !braking && !mv.Stop) return;

        mv.AtPos = fwd > 0; mv.AtNeg = fwd < 0; mv.FastAt = fwd != 0;
        mv.LeftPos = side < 0; mv.LeftNeg = side > 0; mv.FastLeft = side != 0;
        mv.UpPos = up > 0; mv.UpNeg = up < 0; mv.FastUp = up != 0;
        mv.NudgeAtPos = mv.NudgeAtNeg = mv.NudgeLeftPos = mv.NudgeLeftNeg = mv.NudgeUpPos = mv.NudgeUpNeg = false;
        if (driving) { mv.YawPos = turn > 0; mv.YawNeg = turn < 0; mv.TurnLeft = turn > 0; mv.TurnRight = turn < 0; }
        else mv.YawPos = mv.YawNeg = mv.TurnLeft = mv.TurnRight = false;
        mv.Stop = braking;
        bool rotated = false;
        if (!driving && (held || _wasHeld))
        {
            var body = LQuaternion.CreateFromAxisAngle(LVector3.UnitZ, _yaw);
            rotated = body != mv.BodyRotation;
            mv.BodyRotation = mv.HeadRotation = body;
        }
        bool run = fwd != 0 && !driving && (Control.ModifierKeys & Keys.Shift) != 0;
        if (run != _ranSet) { mv.AlwaysRun = run; _ranSet = run; }

        // blocked: walking into something the region stops the avatar at
        if (HeldMove && !driving && !b.Flying && now - _moveSince > 700 && Speed(b) < 0.15f && !_toldBlocked)
        {
            _toldBlocked = true;
            float yaw = _yaw + (fwd < 0 ? MathF.PI : 0) + (side != 0 && fwd == 0 ? -side * MathF.PI / 2 : 0);
            Cue("walls", CueSounds.Cue.Blocked, "Blocked.", b, yaw);
        }
        _wasHeld = held;
        uint controls = mv.AgentControls;
        if (controls != _lastControls || (rotated && now - _sentAt > 50) || ((held || braking) && now - _sentAt > 100))
        {
            mv.SendUpdate(controls != _lastControls);
            _lastControls = controls;
            _sentAt = now;
        }
    }
}
