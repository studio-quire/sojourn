using System.Numerics;

namespace Sojourn.Client.Xr;

/// <summary>
/// SOJOURN_VR_SCRIPT=touch: the person's own touch and sit with the pointer's hand (the right, the pointer on), and
/// the sticks while seated, against a simulated headset. SOJOURN_VR_AIMS="yaw,pitch;yaw,pitch" (degrees from the
/// room's forward, left and up positive) says where the hand points: first at something to touch, then at something
/// to sit on. The routine: a tap of the trigger
/// on the first; the trigger held there while the hand sweeps 8 degrees left (a drag across it); a tap on the second
/// (sits); the left stick forward, then left, then the right stick up (the movement keys, seated); a tap with the hand
/// down (the left button for a script that took it); done at 22 s.
/// </summary>
public sealed partial class VrScript
{
    (float Yaw, float Pitch)[]? _touch;

    static (float, float)[] Aims(string? spec) =>
        (spec ?? "0,20;-45,15").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Split(','))
            .Select(p => (float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

    static readonly (double At, string What)[] TouchSteps =
    {
        (0, "right hand on the first aim"), (1.5, "right trigger: touch"), (3, "right trigger held, sweeping 8 deg left"),
        (6, "right trigger released"), (7.5, "right hand on the second aim"), (8.5, "right trigger: sit"),
        (11, "left stick forward"), (13, "left stick left"), (15, "right stick up"), (17, "right hand down"),
        (18, "right trigger: the left button"), (22, "done"),
    };

    void Touch(XrFrame f, double t)
    {
        int step = Array.FindLastIndex(TouchSteps, s => s.At <= t);
        if (step != _said) { _said = step; _log($"touch script {t:F1} s: {TouchSteps[step].What}"); }
        if (t > 22) return;
        var c = f.Input;
        var head = (f.Eyes[0].Position + f.Eyes[1].Position) * 0.5f;
        var aims = _touch!;
        var (yaw, pitch) = t < 7.5 ? aims[0] : t < 17 && aims.Length > 1 ? aims[1] : (0f, -60f);
        if (t is >= 3 and < 7.5) yaw += (float)Math.Min(1, (t - 3) / 3) * 8;
        const float deg = MathF.PI / 180;
        var dir = Vector3.Transform(new Vector3(0, MathF.Sin(pitch * deg), -MathF.Cos(pitch * deg)), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * deg));
        c.RightAimPos = head + new Vector3(0.2f, -0.35f, -0.25f);
        c.RightAim = LookRotation(dir);
        c.RightTrigger = t is >= 1.5 and < 1.8 or >= 3 and < 6 or >= 8.5 and < 8.8 or >= 18 and < 18.3 ? 1 : 0;
        if (t is >= 11 and < 12.5) c.LeftStick = new Vector2(0, 1);
        if (t is >= 13 and < 14.5) c.LeftStick = new Vector2(-1, 0);
        if (t is >= 15 and < 16) c.RightStick = new Vector2(0, 1);
        f.Input = c;
    }
}
