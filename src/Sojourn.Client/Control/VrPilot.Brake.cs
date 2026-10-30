using LibreMetaverse;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Control;

/// <summary>
/// Braking when a walk lets go (the user's decision, 2026-10-27, after predicted mode threw them off an edge they had
/// stopped at, and server mode's view coasting on past where they let go). Stopping is the region's: the viewer only
/// sends control flags. The one it has for this is AGENT_CONTROL_STOP, which the official viewer holds for its "stop
/// moving" key and sets as its autopilot arrives (LLAgent::autoPilot, stop_moving in llviewerinput.cpp). So from the
/// frame the stick (or the follow walk to the head) lets go, STOP goes out with the movement keys clear, held until the
/// region reports the avatar still (at least a round trip, at most a second), and dropped at once for anything that moves
/// again (the stick, a jump, a seat, controls a script keeps) or once the avatar is in the air (OpenSim turns a STOP
/// raised mid-fall into hovering). OpenSim otherwise keeps a released avatar walking for a quarter second
/// (ScenePresence's delayed stop) before its physics brakes; with STOP it brakes at once. Its animator keeps the
/// current animation while STOP is held, so nobody sees a step.
/// SOJOURN_BRAKE=off sends nothing (as before), =reverse taps the opposite keys for SOJOURN_BRAKE_REVERSE_MS (100) before
/// the STOP (for comparing; a reverse tap is a visible step back for others).
/// </summary>
public sealed partial class VrPilot
{
    static readonly string BrakeMode = Environment.GetEnvironmentVariable("SOJOURN_BRAKE") is { Length: > 0 } b ? b : "stop";
    static readonly double ReverseSeconds =
        (double.TryParse(Environment.GetEnvironmentVariable("SOJOURN_BRAKE_REVERSE_MS"), out var ms) ? ms : 100) / 1000;

    static readonly bool BrakeLog = Environment.GetEnvironmentVariable("SOJOURN_BRAKE_LOG") == "1";
    double _brakeSince = -1;
    int _brakeOctant;

    /// <summary>A walk (the stick's or the follow's) has just let go, moving the way of <paramref name="octant"/>.</summary>
    void StartBrake(double now, int octant)
    {
        if (BrakeMode == "off") return;
        _brakeSince = now;
        _brakeOctant = octant;
    }

    /// <summary>This frame's brake: whether STOP goes out, and (reverse mode) whether the opposite keys do.</summary>
    (bool Stop, bool Reverse) Brake(double now, bool walk, int up, bool sitting, bool driving, Vector3 rawVel)
    {
        if (_brakeSince < 0) return default;
        double since = now - _brakeSince;
        bool still = (Flying ? rawVel.Length() : Flat(rawVel).Length()) < 0.05f && now - _crossedAt > 0.5;   // (a crossing's arrival says still for a moment)
        bool inAir = !Flying && (MathF.Abs(rawVel.Z) > 1f || now - _jumpAt < 1.0);
        if (walk || up != 0 || sitting || driving || inAir || since > 1.0 || since > _latency + 0.1 && still)
        {
            if (BrakeLog)
                Say?.Invoke($"brake: {since:F2} s, {(still ? "still" : walk ? "walking again" : inAir ? "in the air" : "let go")}");
            _brakeSince = -1;
            return default;
        }
        bool reverse = BrakeMode == "reverse" && since < ReverseSeconds;
        return (!reverse, reverse);
    }

    /// <summary>The movement keys for a way relative to the body (eighths of a turn counter-clockwise, 4 back).</summary>
    static void WayKeys(AgentManager.AgentMovement mv, int octant)
    {
        octant = ((octant % 8) + 8) % 8;
        if (octant > 4) octant -= 8;
        bool ahead = octant is >= -1 and <= 1, back = Math.Abs(octant) >= 3, left = octant is >= 1 and <= 3, right = octant is <= -1 and >= -3;
        mv.AtPos = ahead; mv.AtNeg = back; mv.FastAt = ahead || back;
        mv.LeftPos = left; mv.LeftNeg = right; mv.FastLeft = left || right;
    }

    // --- server movement: the view's stop -------------------------------------------------------------------------
    // In server movement the room rides the avatar as drawn, and the drawing carries the region's last word on by its
    // velocity until the update saying it stopped arrives (a round trip and an update's spacing after the stop), then
    // blends back: run on 0.4 m past where the avatar stood and pulled back over 0.18 s (measured in the simulator with
    // main-grid-like lag, 2026-10-27). And a stop update's difference from that run-on was taken for a jump (a
    // teleport, a push) and the pull back left out, so the view stayed that far ahead of the avatar. So once the stick
    // lets go the view doesn't ride the drawing's run-on: it slows evenly to a stop where the avatar will come to rest
    // (where it was drawn at the release, carried on by its velocity for a stopping time learned per gait from where the
    // region stood it), and once the region has it still, eases onto where it is, forward by the same even slowing, back
    // (when it stopped short of that) by a gentle slide. It hands back to riding the drawing once the drawing is there.

    readonly float[] _stopTime = { 0.15f, 0.15f, 0.4f };   // walking, running, flying (s): how long the avatar runs on (OpenSim: 0.14-0.17)
    bool _glide;
    Vector3 _glideAt, _glideFrom, _glideDir, _glideDrawn;
    bool _glideCatch;
    float _glideSpeed, _glideV0;
    int _glideGait;
    double _glideSince;

    /// <summary>Server movement: the stick has just let go with the avatar drawn at <paramref name="drawn"/>, moving at
    /// <paramref name="vel"/> by the region's word.</summary>
    void StartGlide(Vector3 drawn, Vector3 vel, double now)
    {
        var v = Flat(vel);
        _glide = v.Length() > 0.3f && !Flying && Environment.GetEnvironmentVariable("SOJOURN_SERVER_GLIDE") != "0";
        if (!_glide) return;
        (_glideAt, _glideFrom, _glideDir, _glideDrawn, _glideCatch) = (Flat(drawn), Flat(drawn), Vector3.Normalize(v), Flat(drawn), false);
        (_glideSpeed, _glideV0, _glideGait, _glideSince) = (v.Length(), v.Length(), Gait(), now);
    }

    /// <summary>This frame's carry for the room while the view's stop glides (null: ride the drawing as usual).</summary>
    Vector3? Glide(Vector3 drawn, Vector3 raw, Vector3 rawVel, bool cancel, double now, double dt)
    {
        if (!_glide) return null;
        if (cancel || now - _glideSince > 3) { _glide = false; return null; }
        float t = (float)dt;
        var before = _glideAt;
        var drawnMoved = Flat(drawn) - _glideDrawn;
        _glideDrawn = Flat(drawn);
        // off the ground (it walked off an edge): back onto the drawing, moving with it and closing the gap over a
        // few tenths of a second, so the fall's arc is the region's
        if (_glideCatch || !Flying && MathF.Abs(rawVel.Z) > 1f)
        {
            _glideCatch = true;
            _glideAt += drawnMoved + (Flat(drawn) - _glideAt) * (1f - MathF.Exp(-t / 0.15f));
            if (Vector3.Distance(_glideAt, Flat(drawn)) < 0.01f) { _glideAt = Flat(drawn); _glide = false; }
            return _glideAt - before;
        }
        bool still = Flat(rawVel).Length() < 0.05f && now - _crossedAt > 0.5;
        if (!still)
        {
            // not stopped yet by the region's word: slow evenly to where it's expected to rest
            float left = _glideV0 * _stopTime[_glideGait] - Vector3.Dot(_glideAt - _glideFrom, _glideDir);
            Ease(_glideDir, left, t);
        }
        else
        {
            var to = Flat(raw) - _glideAt;
            float d = to.Length();
            if (d > 0.005f && Vector3.Dot(to, _glideDir) > 0 && _glideSpeed > 0.05f) Ease(to / d, d, t);
            else
            {
                // (it stopped short of where the view went: back gently, at most 0.4 m/s)
                _glideSpeed = 0;
                _glideAt += d > 0.4f * t ? to / d * 0.4f * t : to;
            }
            if (Vector3.Distance(_glideAt, Flat(raw)) < 0.01f && Vector3.Distance(Flat(drawn), Flat(raw)) < 0.03f)
            {
                // learned: how long, at the speed it was let go at, the region ran it on
                float ran = Vector3.Dot(Flat(raw) - _glideFrom, _glideDir) / _glideV0;
                if (now - _glideSince < 2) _stopTime[_glideGait] = Math.Clamp(0.7f * _stopTime[_glideGait] + 0.3f * ran, 0f, 1f);
                if (BrakeLog) Say?.Invoke($"server stop: ran on {ran:F2} s; {_stopTime[_glideGait]:F2} s from now on");
                _glide = false;
            }
        }
        return _glideAt - before;
    }

    /// <summary>One frame of slowing evenly from the glide's speed to a stop <paramref name="left"/> metres on.</summary>
    void Ease(Vector3 dir, float left, float t)
    {
        if (left <= 0.001f || _glideSpeed <= 0.001f) { _glideSpeed = 0; return; }
        float decel = _glideSpeed * _glideSpeed / (2 * left);
        float v1 = MathF.Max(0, _glideSpeed - decel * t);
        float step = MathF.Min(left, 0.5f * (_glideSpeed + v1) * t);
        _glideAt += dir * step;
        _glideSpeed = v1;
    }

    // --- flying up or down: the view's stop -------------------------------------------------------------------
    // Letting go of the climb, the drawing carries the climb on until the region's word that it stopped arrives (a
    // round trip and an update's spacing), and the floor's spring, fed the region's climb rate, ran on past that too:
    // OpenSim climbs at ~16 m/s, and the view rose 3-4 m over where the avatar stopped, then blinked back down (both
    // movement modes, in the simulator, 2026-10-27). So from the release the height the view follows slows evenly to
    // where the avatar will stop (the view's height and climb at the release, carried on for a time learned from where
    // the region stopped it), then eases onto the region's height once it says it's still, the floor set to it directly
    // rather than through its spring (whose lead overshot by itself); and STOP goes out as for a walk (OpenSim
    // otherwise climbs on for a quarter second).

    // up, down (s): how long, at the climb rate the region last said, its position climbs on from where it last said
    // at the release (a round trip and the stop; OpenSim with STOP and main-grid-like lag: 0.2-0.25 s)
    readonly float[] _climbTime = { 0.25f, 0.25f };
    bool _climb;
    float _climbAt, _climbRaw0, _climbRawV, _climbSpeed, _climbSign;
    double _climbSince;

    /// <summary>The climb key has just let go, flying, the region's avatar climbing at <paramref name="vz"/>; the view
    /// stands for an avatar at <paramref name="viewZ"/> (its floor less the lift, plus the feet's depth), climbing at
    /// <paramref name="viewVz"/> (the floor's spring runs a little ahead of the drawing).</summary>
    void StartClimbStop(float viewZ, float viewVz, float rawZ, float vz, double now)
    {
        _climb = MathF.Abs(vz) > 0.5f && viewVz * vz > 0 && Environment.GetEnvironmentVariable("SOJOURN_CLIMB_STOP") != "0";
        if (!_climb) return;
        (_climbAt, _climbSpeed, _climbSign, _climbSince) = (viewZ, MathF.Abs(viewVz), MathF.Sign(vz), now);
        (_climbRaw0, _climbRawV) = (rawZ, MathF.Abs(vz));
    }

    /// <summary>This frame's height for the avatar (and its climb rate) while the climb's stop eases (null: the drawing's).</summary>
    (float Z, float Vz)? ClimbStop(float drawnZ, float rawZ, float rawVz, bool cancel, double now, double dt)
    {
        if (!_climb) return null;
        if (cancel || now - _climbSince > 3) { _climb = false; return null; }
        float t = (float)dt, before = _climbAt;
        int k = _climbSign > 0 ? 0 : 1;
        if (MathF.Abs(rawVz) >= 0.05f || now - _crossedAt < 0.5)
        {
            // not stopped yet by the region's word: slow evenly to where it's expected to (already past it: wait there)
            float left = (_climbRaw0 + _climbSign * _climbRawV * _climbTime[k] - _climbAt) * _climbSign;
            if (left > 0) EaseZ(left, t); else _climbSpeed = 0;
        }
        else
        {
            float to = (rawZ - _climbAt) * _climbSign;
            if (to > 0.005f && _climbSpeed > 0.05f) EaseZ(to, t);
            else
            {
                // (it stopped short of where the view went, or past where it waited: there gently, at most 1 m/s)
                _climbSpeed = 0;
                float d = rawZ - _climbAt;
                _climbAt += MathF.Abs(d) > t ? MathF.Sign(d) * t : d;
            }
            if (MathF.Abs(_climbAt - rawZ) < 0.01f && MathF.Abs(drawnZ - rawZ) < 0.03f)
            {
                float ran = (rawZ - _climbRaw0) * _climbSign / _climbRawV;
                if (now - _climbSince < 2) _climbTime[k] = Math.Clamp(0.7f * _climbTime[k] + 0.3f * ran, 0f, 1f);
                if (BrakeLog) Say?.Invoke($"climb stop: ran on {ran:F2} s; {_climbTime[k]:F2} s from now on");
                _climb = false;
            }
        }
        return (_climbAt, t > 0 ? (_climbAt - before) / t : 0);
    }

    /// <summary>The room's floor set straight to the climb's eased height (its spring kept in step for afterwards).</summary>
    float ClimbFloor(float floor, float vz)
    {
        (_floor, _floorVel, _floorSet) = (floor, vz, true);
        return floor;
    }

    void EaseZ(float left, float t)
    {
        if (left <= 0.001f || _climbSpeed <= 0.001f) { _climbSpeed = 0; return; }
        float decel = _climbSpeed * _climbSpeed / (2 * left);
        float v1 = MathF.Max(0, _climbSpeed - decel * t);
        _climbAt += _climbSign * MathF.Min(left, 0.5f * (_climbSpeed + v1) * t);
        _climbSpeed = v1;
    }
}
