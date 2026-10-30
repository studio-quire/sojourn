using System.Globalization;
using System.Numerics;

namespace Sojourn.Client.Xr;

/// <summary>
/// SOJOURN_VR_SCRIPT=motion: a routine given in SOJOURN_MOTION, for testing walking, walls, steps, seats and looking
/// around in the simulator (tools/motion.py). Steps separated by ';', each run for its seconds:
///   wait S | stick X Y S | rstick X Y S | press BUTTON S (A, B, X, Y, menu, lclick) | ltrig V S | rtrig V S | head DX DY DZ S
///   (the head moved to that offset in the room over S seconds, OpenXR axes: x right, y up, z back; it stays there) |
///   aim YAW PITCH / raim YAW PITCH (the left or right hand pointing, degrees off the room's forward: left and up
///   positive; stays) |
///   face DEG (the view turned to that heading of the region, counter-clockwise from east) | mark TEXT |
///   lhand X Y Z S / rhand X Y Z S (the left or right controller's grip moved over S seconds to that offset from the
///   head in the room, held level with the fingers pointing ahead, the hand's aim with them unless aim/raim says; stays) |
///   panel X Y Z (the assistant's panel put at that offset from the head, facing it, as if dragged there) |
///   shot NAME (both eyes saved as shots/NAME.png and NAME-eye1.png in the base dir, and the mirror window as it
///   shows, the menus over the eyes, as NAME-mirror.png)
/// The console logs "motion N: step" as each starts, and "motion done".
/// </summary>
public sealed partial class VrScript
{
    (string Op, float[] Args, string Text)[]? _motion;
    int _motionAt = -1;
    double _motionStepStart;
    Vector3 _headFrom, _headTo, _head;
    (float Yaw, float Pitch)? _motionAim, _motionRightAim;
    Vector3? _lhandFrom, _lhandTo, _lhand, _rhandFrom, _rhandTo, _rhand;

    /// <summary>Puts the assistant's panel somewhere in the room (position, rotation), as a drag would.</summary>
    public Action<Vector3, Quaternion>? PinPanel { get; set; }
    /// <summary>Saves the next frame's eyes under a name.</summary>
    public Action<string>? Shot { get; set; }

    static (string, float[], string)[] ParseMotion(string? spec) =>
        (spec ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s =>
        {
            var parts = s.Split(' ', 2, StringSplitOptions.TrimEntries);
            string op = parts[0].ToLowerInvariant();
            string rest = parts.Length > 1 ? parts[1] : "";
            if (op is "mark" or "press" or "shot")
            {
                var p = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return op == "press" ? (op, new[] { float.Parse(p[1], CultureInfo.InvariantCulture) }, p[0].ToLowerInvariant()) : (op, Array.Empty<float>(), rest);
            }
            return (op, rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(), "");
        }).ToArray();

    static float Duration((string Op, float[] Args, string Text) s) => s.Op switch
    {
        "wait" => s.Args[0], "stick" or "rstick" => s.Args[2], "press" => s.Args[0], "ltrig" or "rtrig" => s.Args[1], "head" => s.Args[3],
        "lhand" or "rhand" => s.Args[3],
        _ => 0,
    };

    bool _motionDone;

    void MotionRoutine(XrFrame f, double t)
    {
        var steps = _motion!;
        // enter every step whose start has passed, in order (zero-length ones take effect then)
        while (_motionAt + 1 < steps.Length && t >= _motionStepStart + (_motionAt >= 0 ? Duration(steps[_motionAt]) : 0))
        {
            if (_motionAt >= 0) _motionStepStart += Duration(steps[_motionAt]);
            var s = steps[++_motionAt];
            _log($"motion {_motionAt}: {s.Op} {string.Join(' ', s.Args.Select(x => x.ToString(CultureInfo.InvariantCulture)))} {s.Text}".TrimEnd());
            if (s.Op == "head") { _headFrom = _head; _headTo = new Vector3(s.Args[0], s.Args[1], s.Args[2]); }
            if (s.Op == "aim") _motionAim = (s.Args[0], s.Args[1]);
            if (s.Op == "raim") _motionRightAim = (s.Args[0], s.Args[1]);
            if (s.Op == "face") Pilot?.Face(s.Args[0]);
            if (s.Op == "shot") Shot?.Invoke(s.Text);
            if (s.Op == "lhand") { _lhandFrom = _lhand ?? new Vector3(s.Args[0], s.Args[1], s.Args[2]); _lhandTo = new Vector3(s.Args[0], s.Args[1], s.Args[2]); }
            if (s.Op == "rhand") { _rhandFrom = _rhand ?? new Vector3(s.Args[0], s.Args[1], s.Args[2]); _rhandTo = new Vector3(s.Args[0], s.Args[1], s.Args[2]); }
            if (s.Op == "panel" && PinPanel is { } pin)
            {
                var d = new Vector3(s.Args[0], s.Args[1], s.Args[2]);
                var head = (f.Eyes[0].Position + f.Eyes[1].Position) * 0.5f + _head;
                float yaw = MathF.Atan2(-d.X, -d.Z), pitch = MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z));
                pin(head + d, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch));
            }
        }
        int at = _motionAt >= 0 && t < _motionStepStart + Duration(steps[_motionAt]) ? _motionAt : steps.Length;
        if (at == steps.Length && _motionAt == steps.Length - 1 && !_motionDone) { _motionDone = true; _log("motion done"); }
        var c = f.Input;
        if (at < steps.Length)
        {
            var s = steps[at];
            double into = t - _motionStepStart;
            switch (s.Op)
            {
                case "stick": c.LeftStick = new Vector2(s.Args[0], s.Args[1]); break;
                case "rstick": c.RightStick = new Vector2(s.Args[0], s.Args[1]); break;
                case "ltrig": c.LeftTrigger = s.Args[0]; break;
                case "rtrig": c.RightTrigger = s.Args[0]; break;
                case "head": _head = Vector3.Lerp(_headFrom, _headTo, (float)Math.Clamp(into / Math.Max(1e-3, s.Args[3]), 0, 1)); break;
                case "lhand": _lhand = Vector3.Lerp(_lhandFrom!.Value, _lhandTo!.Value, (float)Math.Clamp(into / Math.Max(1e-3, s.Args[3]), 0, 1)); break;
                case "rhand": _rhand = Vector3.Lerp(_rhandFrom!.Value, _rhandTo!.Value, (float)Math.Clamp(into / Math.Max(1e-3, s.Args[3]), 0, 1)); break;
                case "press":
                    switch (s.Text)
                    {
                        case "a": c.A = true; break;
                        case "b": c.B = true; break;
                        case "x": c.X = true; break;
                        case "y": c.Y = true; break;
                        case "menu": c.Menu = true; break;
                        case "lclick": c.LeftStickClick = true; break;
                    }
                    break;
            }
        }
        var centre = (f.Eyes[0].Position + f.Eyes[1].Position) * 0.5f;
        for (int i = 0; i < f.Eyes.Length; i++) f.Eyes[i] = f.Eyes[i] with { Position = f.Eyes[i].Position + _head };
        // a grip held level with the fingers ahead: Meta's runtime aims a controller 60 degrees from the grip's -Z
        // toward its -Y, along the hand (VrPilot.Hand), so the grip is pitched up by that and the aim looks ahead
        var level = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 60 * MathF.PI / 180);
        if (_lhand is { } lh)
        {
            (c.LeftHandPos, c.LeftHand) = (centre + _head + lh, level);
            if (_motionAim == null) (c.LeftAimPos, c.LeftAim) = (centre + _head + lh, Quaternion.Identity);
        }
        if (_rhand is { } rh)
        {
            (c.RightHandPos, c.RightHand) = (centre + _head + rh, level);
            if (_motionRightAim == null) (c.RightAimPos, c.RightAim) = (centre + _head + rh, Quaternion.Identity);
        }
        if (_motionAim is { } aim)
        {
            var from = centre + _head + (_lhand ?? new Vector3(-0.2f, -0.35f, -0.25f));
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, aim.Yaw * MathF.PI / 180) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, aim.Pitch * MathF.PI / 180);
            c.LeftAimPos = from;
            c.LeftAim = q;
        }
        if (_motionRightAim is { } raim)
        {
            var from = centre + _head + (_rhand ?? new Vector3(0.2f, -0.35f, -0.25f));
            c.RightAimPos = from;
            c.RightAim = Quaternion.CreateFromAxisAngle(Vector3.UnitY, raim.Yaw * MathF.PI / 180) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, raim.Pitch * MathF.PI / 180);
        }
        f.Input = c;
    }
}
