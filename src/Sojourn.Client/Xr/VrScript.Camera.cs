using System.Numerics;

namespace Sojourn.Client.Xr;

/// <summary>
/// SOJOURN_VR_SCRIPT=camera: the camera (Control/Shutter.cs) against a simulated headset: taken out by its setting,
/// the two hands held up apart frame a picture and the right trigger takes it, then put away. Shots: camera-away,
/// camera-out (the corners at the hands), camera-flash (just after the shutter). Done at 11 s.
/// </summary>
public sealed partial class VrScript
{
    bool _camera;
    /// <summary>The camera the routine takes out and puts away.</summary>
    public Sojourn.Client.Control.Shutter? Camera { get; set; }

    static readonly (double At, string What)[] CameraSteps =
    {
        (0, "camera away"), (1.5, "the camera out"), (5, "right trigger at the world: a picture"), (8.5, "put away"),
        (11, "done"),
    };

    bool _cameraShots0, _cameraShots1, _cameraShots2;

    void CameraRoutine(XrFrame f, double t)
    {
        int step = Array.FindLastIndex(CameraSteps, s => s.At <= t);
        if (step != _said) { _said = step; _log($"camera script {t:F1} s: {CameraSteps[step].What} (camera {(Camera?.Out == true ? "out" : "away")})"); }
        if (t > 11 || Camera is not { } cam) return;
        var c = f.Input;
        var head = (f.Eyes[0].Position + f.Eyes[1].Position) * 0.5f;
        var rightFrom = head + new Vector3(0.2f, -0.35f, -0.25f);
        // the camera out by its setting (no token on the arm since 2026-10-29), the shutter, then put away
        if (t >= 1.5 && t < 8.5) cam.Out = true; else if (t >= 8.5) cam.Out = false;
        var at = head + new Vector3(0.1f, -0.6f, -3f);
        c.RightAimPos = rightFrom;
        c.RightAim = LookRotation(Vector3.Normalize(at - rightFrom));
        // the hands up apart in front, framing (the shutter is the trigger only while they frame); down after 7.5
        if (t < 7.5)
        {
            c.LeftHandPos = head + new Vector3(-0.18f, 0.1f, -0.45f);
            c.RightHandPos = head + new Vector3(0.16f, -0.12f, -0.45f);
        }
        c.RightTrigger = t is >= 5 and < 5.3 ? 1 : 0;
        f.Input = c;
        if (t >= 1.2 && !_cameraShots0) { _cameraShots0 = true; Shot?.Invoke("camera-away"); }
        if (t >= 4 && !_cameraShots1) { _cameraShots1 = true; Shot?.Invoke("camera-out"); }
        if (t >= 5.05 && !_cameraShots2) { _cameraShots2 = true; Shot?.Invoke("camera-flash"); }
    }
}
