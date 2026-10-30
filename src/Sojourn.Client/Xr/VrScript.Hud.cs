using System.Numerics;

namespace Sojourn.Client.Xr;

/// <summary>
/// SOJOURN_VR_SCRIPT=hud: the HUD board against a simulated headset (Hud/HudBoard). The left hand is raised as if to
/// look at a watch, and the right points at the HUD token on its wrist and taps the trigger (the board comes up); the
/// right taps a point of the HUD screen (SOJOURN_HUD_AIMS="u,v;u,v", 0-1 across and down the screen: first a button);
/// it holds its trigger on the second point while sweeping across it (a drag); the right takes the board by its
/// top strip and drags it 12 degrees right; the right taps × (the board goes); done at 16 s.
/// </summary>
public sealed partial class VrScript
{
    Vector2[]? _hudAims;
    /// <summary>Where the routine aims (Hud/HudBoard): the token, a screen point (u, v), the × key, the top strip.</summary>
    public Sojourn.Client.Hud.HudBoard? Hud { get; set; }

    static Vector2[] HudAims(string? spec) =>
        (spec ?? "0.854,0.14;0.08,0.86").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Split(','))
            .Select(p => new Vector2(float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

    static readonly (double At, string What)[] HudSteps =
    {
        (0, "left hand raised, right hand on the HUD token"), (1.5, "right trigger: the board comes up"),
        (3, "right hand on the first point"), (4, "right trigger: press"), (5.5, "right hand on the second point"),
        (6.5, "right trigger held, sweeping across it"), (9, "right trigger released"), (10, "right hand on the board's top strip"),
        (10.5, "right trigger held: dragging the board 12 deg right"), (12.5, "released"), (13.5, "right hand on ×"),
        (14.5, "right trigger: the board goes"), (16, "done"),
    };

    Vector3? _dragFrom;

    void HudRoutine(XrFrame f, double t)
    {
        int step = Array.FindLastIndex(HudSteps, s => s.At <= t);
        if (step != _said) { _said = step; _log($"hud script {t:F1} s: {HudSteps[step].What}"); }
        if (t > 16 || Hud is not { } board) return;
        var c = f.Input;
        var head = (f.Eyes[0].Position + f.Eyes[1].Position) * 0.5f;
        var rightFrom = head + new Vector3(0.2f, -0.35f, -0.25f);
        // the left hand held up before the eyes, back of the wrist toward them (a look at a watch)
        if (t < 3)
        {
            c.LeftHandPos = head + new Vector3(-0.08f, -0.28f, -0.32f);
            c.LeftHand = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 70 * MathF.PI / 180) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 20 * MathF.PI / 180);
        }
        Vector3? rightAt = t switch
        {
            < 3 => board.TokenRoom,
            < 5.5 => board.ScreenPoint(_hudAims![0]),
            >= 10 and < 13.5 => _dragFrom ??= board.TopStripPoint(),
            >= 13.5 => board.ClosePoint(),
            _ => null,
        };
        if (t is >= 10.5 and < 12.5 && _dragFrom is { } d)
        {
            // swept right about the head while held
            float k = (float)Math.Clamp((t - 10.5) / 1.5, 0, 1) * 12 * MathF.PI / 180;
            rightAt = head + Vector3.Transform(d - head, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -k));
        }
        if (rightAt is { } ra)
        {
            c.RightAimPos = rightFrom;
            c.RightAim = LookRotation(Vector3.Normalize(ra - rightFrom));
        }
        if (t is >= 5.5 and < 9.5 && _hudAims!.Length > 1 && board.ScreenPoint(_hudAims[1] + new Vector2((float)Math.Clamp((t - 6.5) / 2, 0, 1) * 0.06f, 0)) is { } la)
        {
            c.RightAimPos = rightFrom;
            c.RightAim = LookRotation(Vector3.Normalize(la - rightFrom));
        }
        c.RightTrigger = t is >= 1.5 and < 1.8 or >= 4 and < 4.3 or >= 6.5 and < 9 or >= 10.5 and < 12.5 or >= 14.5 and < 14.8 ? 1 : 0;
        f.Input = c;
    }
}
