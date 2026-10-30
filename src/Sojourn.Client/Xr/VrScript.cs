using System.Numerics;

namespace Sojourn.Client.Xr;

/// <summary>
/// A test aid (SOJOURN_VR_SCRIPT=1): once logged in, a fixed routine of room-scale steps and controller input laid
/// over each headset frame, so the rig can be checked against a simulated headset with nobody in it. It walks the
/// head across the room, uses the stick, turns, flies, steps sideways, opens and closes the settings menu and toggles
/// the assistant's panel, holds voice chat's push-to-talk (the left grip) for 2 s, turns the pointer on (A), then moves
/// the panel: the right controller points at it from below the head and drags it 35 degrees to the right; it fades, and X brings it up
/// there; the head turns 70 degrees left and X brings it in front; the head turns back (it follows), and it's dragged
/// 25 degrees out and back onto its resting place (so it follows from then on); A turns the pointer off; then idles.
/// The routines that point (touch, hud, media, keyboard) turn the pointer on first. The console logs each
/// change of where the panel sits. The head's offsets are in the play space (OpenXR axes: x right, y up, z back).
/// </summary>
public sealed partial class VrScript
{
    readonly Action<string> _log;
    double _start = -1;
    int _said = -1;

    public static VrScript? FromEnvironment(Action<string> log) => Environment.GetEnvironmentVariable("SOJOURN_VR_SCRIPT") switch
    {
        "1" => new VrScript(log, walk: false),
        "walk" => new VrScript(log, walk: true),
        "touch" => new VrScript(log, walk: false) { _touch = Aims(Environment.GetEnvironmentVariable("SOJOURN_VR_AIMS")) },
        "hud" => new VrScript(log, walk: false) { _hudAims = HudAims(Environment.GetEnvironmentVariable("SOJOURN_HUD_AIMS")) },
        "map" => new VrScript(log, walk: false) { _map = true },
        "camera" => new VrScript(log, walk: false) { _camera = true },
        "media" => new VrScript(log, walk: false) { _mediaFace = Environment.GetEnvironmentVariable("SOJOURN_MEDIA_FACE") ?? "My Media Board" },
        "keyboard" => new VrScript(log, walk: false) { _keyboardTarget = Environment.GetEnvironmentVariable("SOJOURN_KEYBOARD_TARGET") ?? "assistant" },
        "motion" => new VrScript(log, walk: false) { _motion = ParseMotion(Environment.GetEnvironmentVariable("SOJOURN_MOTION")) },
        _ => null,
    };

    // SOJOURN_VR_SCRIPT=walk: stick walks only, for measuring how walking feels (tools/vrlog.py, docs/vr.md): 3 s
    // walks forward, right, back and left with 3 s pauses between, every fourth one turning as it goes, for 96 s
    readonly bool _walk;

    VrScript(Action<string> log, bool walk) { _log = log; _walk = walk; }

    void Walk(XrFrame f, double t)
    {
        int n = (int)(t / 6);
        double inCycle = t - n * 6;
        if (n >= 16) { if (_said != 99) { _said = 99; _log("walk script done"); } return; }
        if (n != _said) { _said = n; _log($"walk script {t:F0} s: walk {n + 1}"); }
        var c = f.Input;
        if (inCycle < 3)
        {
            c.LeftStick = (n % 4) switch { 0 => new Vector2(0, 1), 1 => new Vector2(1, 0), 2 => new Vector2(0, -1), _ => new Vector2(-1, 0) };
            if (n % 4 == 3 && inCycle is > 1 and < 2) c.RightStick = new Vector2(0.6f, 0);
        }
        f.Input = c;
    }

    static readonly (double At, string What)[] Steps =
    {
        (0, "standing"), (3, "stepping 1.2 m forward in the room"), (6, "left stick forward"), (8.5, "turn right"),
        (10, "left stick right"), (12, "B: fly"), (13, "right stick up: climb"), (15, "B: land"), (18, "stepping 0.8 m left in the room"),
        (22, "menu button: settings"), (27, "menu button: close"), (29, "X: assistant panel"), (32, "left grip: talk in voice chat"),
        (34, "left grip released"), (35, "A: the pointer on"), (36, "pointing at the panel"), (37, "trigger: dragging the panel 35 deg right"),
        (40, "trigger released: it stays there"), (44, "X: the panel shows where it was put"), (44.5, "head turning 70 deg left"),
        (47.5, "X: the panel comes in front"), (49.5, "head turning back"), (53.5, "trigger: dragging the panel out and back to rest"),
        (57.5, "trigger released: it follows again"), (58.6, "A: the pointer off"), (59, "done"),
    };

    public void Apply(XrFrame f, bool loggedIn, double now)
    {
        if (!loggedIn) return;
        if (_start < 0) _start = now + 5;   // let the avatar settle after arriving
        // a page's keyboard key shows once its page is up close with a field focused (the test does that): wait for it
        if (_keyboardTarget == "page" && !_keyboardReady && (_keyboardReady = Media?.View.KeyPoint("keyboard") != null) == false) { _start = now + 1; return; }
        double t = now - _start;
        if (t < 0) return;
        // the routines that point need the pointer on; the default one starts with it off and turns it on with A
        if (!_pointerSet && Hands is { } hp && !_walk && _motion == null)
        {
            _pointerSet = true;
            hp.Set(_touch != null || _hudAims != null || _map || _camera || _mediaFace != null || _keyboardTarget != null);
        }
        if (_walk) { Walk(f, t); return; }
        if (_touch != null) { Touch(f, t); return; }
        if (_hudAims != null) { HudRoutine(f, t); return; }
        if (_map) { MapRoutine(f, t); return; }
        if (_camera) { CameraRoutine(f, t); return; }
        if (_mediaFace != null) { MediaRoutine(f, t); return; }
        if (_keyboardTarget != null) { KeyboardRoutine(f, t); return; }
        if (_motion != null) { MotionRoutine(f, t); return; }
        int step = Array.FindLastIndex(Steps, s => s.At <= t);
        if (step != _said) { _said = step; _log($"script {t:F1} s: {Steps[step].What}"); }

        // the head: 1.2 m forward over 3-4.5 s, then 0.8 m left over 18-19 s, held; turned 70 degrees left over 44.5-46 s
        // and back over 49.5-51 s
        float fwd = (float)Math.Clamp((t - 3) / 1.5, 0, 1) * 1.2f;
        float left = (float)Math.Clamp((t - 18) / 1.0, 0, 1) * 0.8f;
        var offset = new Vector3(-left, 0, -fwd);
        float yaw = (float)(Math.Clamp((t - 44.5) / 1.5, 0, 1) - Math.Clamp((t - 49.5) / 1.5, 0, 1)) * 70 * MathF.PI / 180;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        var centre = (f.Eyes[0].Position + f.Eyes[1].Position) * 0.5f;
        for (int i = 0; i < f.Eyes.Length; i++)
        {
            var e = f.Eyes[i];
            f.Eyes[i] = e with { Position = centre + Vector3.Transform(e.Position - centre, turn) + offset, Orientation = Quaternion.Normalize(turn * e.Orientation) };
        }
        var head = centre + offset;

        var c = f.Input;
        if (t is >= 6 and < 8) c.LeftStick = new Vector2(0, 1);
        if (t is >= 8.5 and < 8.7) c.RightStick = new Vector2(1, 0);
        if (t is >= 10 and < 11.5) c.LeftStick = new Vector2(1, 0);
        if (t is >= 12 and < 12.2) c.B = true;
        if (t is >= 13 and < 14.5) c.RightStick = new Vector2(0, 1);
        if (t is >= 35 and < 35.2 || t is >= 58.6 and < 58.8) c.A = true;
        if (t is >= 15 and < 15.2) c.B = true;
        if (t is >= 22 and < 22.2 || t is >= 27 and < 27.2) c.Menu = true;
        if (t is >= 29 and < 29.2 || t is >= 44 and < 44.2 || t is >= 47.5 and < 47.7) c.X = true;
        if (t is >= 32 and < 34) c.LeftGrip = 1;
        if (t is >= 36 and < 40.5 or >= 53 and < 58.5)   // (pointing at it keeps it up: away between the drags)
        {
            // the right hand below and right of the head, pointing at a spot 1.1 m out, 20 deg below the line of sight
            // (an idle capsule at rest, left of its ? key): swept 35 deg right over 37.5-39 s. Later, where the head's
            // turn back left it (carried along at its range's edge, to the left): 25 deg further out over 54-55 s, then
            // onto its resting place in front over 55.5-56.5 s
            if (t >= 50 && _secondFrom == null && PanelPlane?.Invoke() is { } pl)
            {
                var to = pl.Pos - head;
                _secondFrom = -MathF.Atan2(-to.X, -to.Z) * 180 / MathF.PI - 2;   // its heading, as a sweep (right positive)
            }
            double from2 = _secondFrom ?? -2;
            double sweep = t < 50 ? Math.Clamp((t - 37.5) / 1.5, 0, 1) * 35 - 2
                : from2 - Math.Clamp(t - 54, 0, 1) * 25 + Math.Clamp(t - 55.5, 0, 1) * (25 + (-2 - from2));
            float aimYaw = -(float)sweep * MathF.PI / 180;
            const float down = 20 * MathF.PI / 180;
            var spot = head + Vector3.Transform(new Vector3(0, -MathF.Sin(down), -MathF.Cos(down)) * 1.1f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, aimYaw));
            var from = head + new Vector3(0.2f, -0.35f, -0.25f);
            var dir = Vector3.Normalize(spot - from);
            c.RightAimPos = from;
            c.RightAim = LookRotation(dir);
            c.RightTrigger = t is >= 37 and < 40 || t is >= 53.5 and < 57.5 ? 1 : 0;
        }
        f.Input = c;
    }

    /// <summary>The pointer (Control/HandPointer), set for the routines that point.</summary>
    public Sojourn.Client.Control.HandPointer? Hands { get; set; }
    bool _pointerSet;
    double? _secondFrom;

    /// <summary>A rotation taking -Z to dir, with no roll.</summary>
    static Quaternion LookRotation(Vector3 dir)
    {
        float yaw = MathF.Atan2(-dir.X, -dir.Z);
        float pitch = MathF.Asin(Math.Clamp(dir.Y, -1, 1));
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
    }
}
