using System.Numerics;

namespace Sojourn.Client.Xr;

/// <summary>
/// SOJOURN_VR_SCRIPT=media: the pointer's hand (the right) on a page in the world and the page board, against a simulated headset
/// (Media/MediaInput, Media/PageView), on the face SOJOURN_MEDIA_FACE names (an object's name; "My Media Board", the
/// test account's own board, by default): a tap on its button, a double tap (the page comes up close on the board), the
/// right hand's tap on the button there, × (the board goes), then a drag up the page (it scrolls down); done at 12 s.
/// tools/media.py test vr checks what the page counted.
/// </summary>
public sealed partial class VrScript
{
    string? _mediaFace;
    /// <summary>Where the routine aims (Media/MediaWiring): the face's page and the board's.</summary>
    public Sojourn.Client.Media.MediaWiring? Media { get; set; }
    public Sojourn.Client.Control.VrPilot? Pilot { get; set; }

    static readonly (double At, string What)[] MediaSteps =
    {
        (0, "right hand on the page's button"), (1, "right trigger: tap"), (2, "right hand on the page's middle"),
        (2.5, "double tap: up close"), (4, "right hand on the board's button"), (4.5, "right trigger: tap"), (6, "right hand on ×"),
        (6.5, "right trigger: put away"), (8, "right hand on the page's middle"), (9, "right trigger held, dragging up the page"),
        (11, "released"), (12, "done"),
    };

    static readonly Vector2 MediaButton = new(115 / 1024f, 155 / 512f);   // tools/media_fixtures.py's "Press me"

    void MediaRoutine(XrFrame f, double t)
    {
        int step = Array.FindLastIndex(MediaSteps, s => s.At <= t);
        if (step != _said) { _said = step; _log($"media script {t:F1} s: {MediaSteps[step].What}"); }
        if (t > 12 || Media is not { } media || Pilot is not { } pilot) return;
        var c = f.Input;
        var head = (f.Eyes[0].Position + f.Eyes[1].Position) * 0.5f;
        var rightFrom = head + new Vector3(0.2f, -0.35f, -0.25f);
        Vector2? onFace = t switch
        {
            < 2 => MediaButton,
            < 4 => new Vector2(0.5f, 0.6f),
            < 8 => null,
            < 9 => new Vector2(0.5f, 0.6f),
            // quickly, as a flick starts: a press that holds still for half a second presses and drags instead
            _ => new Vector2(0.5f, 0.6f - 0.4f * (float)Math.Clamp((t - 9) / 0.4, 0, 1)),
        };
        if (onFace is { } uv && media.FacePoint(_mediaFace!, uv) is { } p)
        {
            c.RightAimPos = rightFrom;
            c.RightAim = LookRotation(Vector3.Normalize(pilot.RegionToRoom(p) - rightFrom));
        }
        Vector3? rightAt = t switch
        {
            >= 4 and < 6 => media.View.ScreenPoint(MediaButton),
            >= 6 and < 8 => media.View.KeyPoint("close"),
            _ => null,
        };
        if (rightAt is { } ra)
        {
            c.RightAimPos = rightFrom;
            c.RightAim = LookRotation(Vector3.Normalize(ra - rightFrom));
        }
        c.RightTrigger = t is >= 1 and < 1.25 or >= 2.5 and < 2.6 or >= 2.8 and < 2.9 or >= 4.5 and < 4.75 or >= 6.5 and < 6.8 or >= 9 and < 11 ? 1 : 0;
        f.Input = c;
    }
}
