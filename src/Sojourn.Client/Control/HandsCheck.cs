using System.Numerics;
using Sojourn.Client.Xr;
using Sojourn.Render.Realtime;

namespace Sojourn.Client.Control;

/// <summary>
/// SOJOURN_SELF_TEST=hands in the headset (Meta's XR Simulator, or a headset with someone else watching the mirror):
/// the camera stands in front of your own body, a little to its left, and looks back at it, and each controller is
/// marked over the image: a dot at its grip's centre, a short line along the grip's -Z (the tube the fingers close
/// around) and a longer one along its aim. The avatar's hands should close around the dots, the aim lines running out
/// past their knuckles. Every few seconds the log gets each controller's aim relative to its grip.
/// SOJOURN_SELF_TEST=watch: the camera stays at the eyes and looks at the left wrist, where voice's mic token sits.
/// </summary>
public sealed class HandsCheck
{
    readonly Action<string> _log;
    readonly bool _watch;
    double _loggedAt = -10;

    HandsCheck(Action<string> log, bool watch) { _log = log; _watch = watch; }

    public static HandsCheck? FromEnvironment(Action<string> log) => Environment.GetEnvironmentVariable("SOJOURN_SELF_TEST") switch
    {
        "hands" => new HandsCheck(log, false),
        "watch" => new HandsCheck(log, true),
        _ => null,
    };

    /// <summary>The camera (region frame) and the controllers' marks for this frame, or null without a body.</summary>
    public (Vector3 Camera, Vector3 Forward, OverlayScene? Marks)? Frame(VrView view, XrControllers input, VrPilot pilot, double now)
    {
        if (_watch) return pilot.Watch(input) is { } w ? (view.Position, Vector3.Normalize(w - view.Position), null) : null;
        if (view.Self is not { } self) return null;
        // where the head faces (the body turns after it)
        var fwd = new Vector3(view.Forward.X, view.Forward.Y, 0);
        fwd = fwd.LengthSquared() > 1e-4f ? Vector3.Normalize(fwd) : new Vector3(MathF.Cos(self.Yaw), MathF.Sin(self.Yaw), 0);
        var left = new Vector3(-fwd.Y, fwd.X, 0);
        var chest = self.Head - Vector3.UnitZ * 0.4f + fwd * 0.25f;
        // in front and a little to the left, then from the left side, alternating every 6 s
        bool side = (int)(now / 6) % 2 == 1;
        var cam = side ? self.Head + left * 1.3f + fwd * 0.3f - Vector3.UnitZ * 0.35f : self.Head + fwd * 1.1f + left * 0.5f - Vector3.UnitZ * 0.2f;

        var beams = new List<OverlayBeam>();
        var dots = new List<OverlayDot>();
        void Mark(Quaternion? grip, Vector3? gripAt, Quaternion? aim, Vector3? aimAt)
        {
            if (grip is not { } g || gripAt is not { } gp) return;
            var at = pilot.RoomToRegion(gp);
            dots.Add(new OverlayDot(at, 0.008f, new Vector4(1f, 0.85f, 0.2f, 0.95f)));
            beams.Add(new OverlayBeam(at, at + pilot.RoomDirToRegion(Vector3.Transform(-Vector3.UnitZ, g)) * 0.07f, 0.004f, new Vector4(1f, 0.85f, 0.2f, 0.9f)));
            if (aim is { } a && aimAt is { } ap)
            {
                var from = pilot.RoomToRegion(ap);
                beams.Add(new OverlayBeam(from, from + pilot.RoomDirToRegion(Vector3.Transform(-Vector3.UnitZ, a)) * 0.25f, 0.003f, new Vector4(0.3f, 0.9f, 1f, 0.9f)));
            }
        }
        // where the arms were asked to put the wrists
        foreach (var h in new[] { self.Left, self.Right })
            if (h is { } t) dots.Add(new OverlayDot(t.Wrist, 0.007f, new Vector4(1f, 0.3f, 0.8f, 0.95f)));
        Mark(input.LeftHand, input.LeftHandPos, input.LeftAim, input.LeftAimPos);
        Mark(input.RightHand, input.RightHandPos, input.RightAim, input.RightAimPos);

        if (now - _loggedAt > 5)
        {
            _loggedAt = now;
            float headYaw = MathF.Atan2(fwd.Y, fwd.X);
            _log($"hands: body {MathF.IEEERemainder(self.Yaw - headYaw, MathF.Tau) * 180 / MathF.PI:F0} deg from the head's heading; left {Relative(input.LeftHand, input.LeftHandPos, input.LeftAim, input.LeftAimPos)}; right {Relative(input.RightHand, input.RightHandPos, input.RightAim, input.RightAimPos)}");
        }
        return (cam, Vector3.Normalize(chest - cam), new OverlayScene { Beams = beams, Dots = dots });
    }

    /// <summary>The aim pose in the grip's frame: its origin (cm) and its -Z, and the grip itself in the room.</summary>
    static string Relative(Quaternion? grip, Vector3? gripAt, Quaternion? aim, Vector3? aimAt)
    {
        if (grip is not { } g || gripAt is not { } gp) return "no grip";
        var inv = Quaternion.Conjugate(g);
        var gz = Vector3.Transform(-Vector3.UnitZ, g);
        string s = $"grip at ({gp.X:F2},{gp.Y:F2},{gp.Z:F2}) -Z ({gz.X:F2},{gz.Y:F2},{gz.Z:F2})";
        if (aim is not { } a || aimAt is not { } ap) return s + ", no aim";
        var o = Vector3.Transform(ap - gp, inv) * 100;
        var d = Vector3.Transform(Vector3.Transform(-Vector3.UnitZ, a), inv);
        float angle = MathF.Acos(Math.Clamp(-d.Z, -1, 1)) * 180 / MathF.PI;
        return s + $", aim origin ({o.X:F1},{o.Y:F1},{o.Z:F1}) cm, aim -Z ({d.X:F2},{d.Y:F2},{d.Z:F2}) = {angle:F0} deg from the grip's -Z";
    }
}
