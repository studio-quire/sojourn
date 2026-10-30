using LibreMetaverse;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Control;

/// <summary>
/// The comfort vignette (batch B1; docs/vr.md > Comfort): while the view moves without the person's body moving, its
/// edges darken, narrowing it toward the middle, the common answer to vection in VR. What counts: the room carried by
/// what the person didn't do (a push, a moving floor, a seat or vehicle carrying and turning them, the agent API walking
/// them: the rig's carries, docs/vr.md > The rig), and in "all" also the stick's walk and smooth turns. Snap turns,
/// cuts of the view (teleports, put-backs) and recentring are single jumps, not motion. Neither viewer has one (the official viewer's OpenXR branch
/// stalled before comfort options); it's local, nothing anyone else sees.
/// Drawn as a quad layer locked to the head (Xr/XrSession.cs SetVignette), under the menus and the panel, so they stay
/// clear: one picture (a clear middle feathered to black) made once, its size changed with how strong the vignette is
/// now (a bigger quad, a wider clear middle). Stronger with speed (0.25-2.5 m/s) and turning (8-60 deg/s), in over
/// 0.12 s and out over 0.35 s; vignette_strength scales how far it closes (at 1 and full motion, to 30 degrees from the
/// middle). Claude Opus 5.5, 2026-10-27.
/// </summary>
public sealed class Comfort(AgentSession session, VrSettings settings, Action<string>? say = null)
{
    // the picture: clear inside HoleR of the half-width, black from SolidR out, a smooth step between
    const int Size = 512;
    const float HoleR = 0.25f, SolidR = 0.45f;
    const float Distance = 10f;   // metres ahead: far enough that its depth doesn't fight what it's over
    static OverlayImage? _image;

    float _level;
    Vector3 _lastOrigin, _lastFwd;
    ulong _lastRegion;
    bool _have;
    double _now, _ownUntil, _onSince = -1;
    float _peak;

    static readonly float? ForceLevel = float.TryParse(Environment.GetEnvironmentVariable("SOJOURN_VIGNETTE"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : null;

    /// <summary>How strong the vignette is this frame (0: none).</summary>
    public float Level => _level;

    const ScriptControlChange MoveKeys = ScriptControlChange.Forward | ScriptControlChange.Back | ScriptControlChange.Left |
        ScriptControlChange.Right | ScriptControlChange.RotateLeft | ScriptControlChange.RotateRight;

    /// <summary>Each headset frame, after the rig placed the room (VrPilot.Update).</summary>
    public void Frame(XrSession hs, VrPilot pilot, XrControllers input, double dt)
    {
        _now += dt;
        float target = 0;
        var mode = settings.ComfortVignette;
        if (mode != Vignette.Off && pilot.Region != 0 && dt > 0)
        {
            var origin = pilot.RoomToRegion(Vector3.Zero);
            var fwd = pilot.RoomDirToRegion(-Vector3.UnitZ);
            if (_have && pilot.Region == _lastRegion && !pilot.CutThisFrame)
            {
                var step = origin - _lastOrigin;
                float speed = new Vector2(step.X, step.Y).Length() / (float)dt;
                float turn = MathF.Abs(MathF.Atan2(_lastFwd.X * fwd.Y - _lastFwd.Y * fwd.X, _lastFwd.X * fwd.X + _lastFwd.Y * fwd.Y)) * 180 / MathF.PI / (float)dt;
                // a jump (a teleport, a put-back, recentring, a snap turn) is one move, not motion; a plane at 60 m/s
                // goes under a metre a frame
                if (new Vector2(step.X, step.Y).Length() > 2f) speed = 0;
                if (turn * dt > 10) turn = 0;
                bool sitting = session.Client is { } c && c.Self.SittingOn != 0;
                bool driving = sitting || (session.ControlsKept & MoveKeys) != 0;
                bool walking = !driving && input.LeftStick.Length() > 0.25f;
                if (walking) _ownUntil = _now + 0.6;   // and the coast after it
                bool ownMove = walking || _now < _ownUntil || pilot.Spectating;
                bool ownTurn = settings.SmoothTurn && MathF.Abs(input.RightStick.X) > 0.15f;
                if (mode == Vignette.All || !ownMove) target = MathF.Max(target, Smooth(0.25f, 2.5f, speed));
                if (mode == Vignette.All || !ownTurn) target = MathF.Max(target, Smooth(8, 60, turn));
            }
            (_lastOrigin, _lastFwd, _lastRegion, _have) = (origin, fwd, pilot.Region, true);
        }
        else _have = false;
        if (ForceLevel is { } forced) target = forced;   // SOJOURN_VIGNETTE=level: held there (checking the picture)
        float tau = target > _level ? 0.12f : 0.35f;
        _level += (target - _level) * (1 - MathF.Exp(-(float)dt / tau));
        if (_level < 0.01f && target == 0) _level = 0;
        // the console says when it comes and goes (one line each), for checking without a headset
        if (_level > 0.05f && _onSince < 0) { _onSince = _now; _peak = 0; say?.Invoke($"comfort vignette: on ({(mode == Vignette.All ? "motion" : "something else moving you")})"); }
        _peak = MathF.Max(_peak, _level);
        if (_level == 0 && _onSince >= 0) { say?.Invoke($"comfort vignette: off after {_now - _onSince:F1} s, at most {_peak:F2}"); _onSince = -1; }
        Show(hs);
    }

    static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    void Show(XrSession hs)
    {
        if (_level <= 0) { hs.SetVignette(null, 0, 0); return; }
        // the clear middle's half-angle: 80 degrees (past the lenses' edge) closing to 30 at full strength
        float hole = (80 - 50 * _level * Math.Clamp(settings.VignetteStrength, 0, 1)) * MathF.PI / 180;
        float width = 2 * Distance * MathF.Tan(hole) / HoleR;
        hs.SetVignette(_image ??= Picture(), width, Distance);
    }

    static OverlayImage Picture()
    {
        var px = new byte[Size * Size * 4];
        float c = (Size - 1) / 2f;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float r = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                // premultiplied black: only alpha
                px[(y * Size + x) * 4 + 3] = (byte)MathF.Round(255 * Smooth(HoleR, SolidR, r));
            }
        return new OverlayImage(px, Size, Size, 1);
    }
}
