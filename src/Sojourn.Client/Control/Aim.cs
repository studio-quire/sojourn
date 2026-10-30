using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Control;

/// <summary>
/// Aiming in the headset (VrPilot.Aim.cs): the right trigger is the mouselook button (CONTROL_ML_LBUTTON, down while
/// held, up once, as a click in the viewer's mouselook sends it), unless the ray is on a menu, a board or a key the
/// person is pressing; and a crosshair where the aim lands (Firestorm's mouselook crosshair), on whatever's there so it
/// sits at the right depth in both eyes. Main thread, each headset frame.
/// </summary>
public sealed class Aim(AgentSession s, FrameRenderer r)
{
    bool _armed = true, _down;

    static Vector4 Ink => Sojourn.Session.Assistant.PanelLook.InUse.Light(0.9f);   // the look's light; Hot, a shot held, stays amber
    static readonly Vector4 Hot = new(1f, 0.82f, 0.55f, 0.95f);

    /// <param name="free">the trigger may fire: no confirmation or menu, the assistant isn't asking to point, the ray isn't
    /// on the panel, the feed, a board or a forearm key</param>
    public OverlayScene? Headset(OverlayScene? scene, XrControllers input, VrPilot pilot, bool free, bool focused)
    {
        if (!pilot.Aiming || !focused || pilot.Region == 0)
        {
            if (_down) Button(false);
            _armed = input.RightTrigger < 0.35f;   // a pull held as aiming starts isn't a shot
            return scene;
        }
        if (input.RightTrigger < 0.35f) { _armed = true; if (_down) Button(false); }
        else if (!_down && _armed && free && input.RightTrigger > 0.6f) { _armed = false; Button(true); }

        var (from, dir) = pilot.AimRay;
        uint self = s.Client?.Self.LocalID ?? 0;
        float d = r.Pick(pilot.Region, from, dir, 96, self)?.Distance ?? 30;
        var at = from + dir * d;
        float radius = Math.Clamp(0.012f * d, 0.006f, 0.6f);
        var beams = new List<OverlayBeam>(scene?.Beams ?? []);
        var dots = new List<OverlayDot>(scene?.Dots ?? []);
        var ink = _down ? Hot : Ink;
        // a ring with a gap at each quarter, and a dot in the middle
        var toEye = Vector3.Normalize(from - at);
        var a = Vector3.Normalize(Vector3.Cross(toEye, MathF.Abs(toEye.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
        var b = Vector3.Cross(toEye, a);
        const int n = 24;
        for (int i = 0; i < n; i++)
        {
            if (i % 6 == 0 || i % 6 == 5) continue;
            float t0 = i * MathF.Tau / n + MathF.PI / 4, t1 = (i + 1) * MathF.Tau / n + MathF.PI / 4;
            beams.Add(new OverlayBeam(at + (a * MathF.Cos(t0) + b * MathF.Sin(t0)) * radius, at + (a * MathF.Cos(t1) + b * MathF.Sin(t1)) * radius, radius * 0.16f, ink));
        }
        dots.Add(new OverlayDot(at, radius * 0.14f, ink));
        // aiming with the hand: a faint line from it, so the aim reads as the hand's
        if (pilot.Settings.AimWithHand) beams.Add(new OverlayBeam(from + dir * 0.05f, at - dir * radius, 0.0025f, ink with { W = 0.55f }));
        return (scene ?? new OverlayScene()) with { Beams = beams, Dots = dots };
    }

    void Button(bool down)
    {
        _down = down;
        if (s.Client is not { Network.Connected: true } c) return;
        var mv = c.Self.Movement;
        if (down)
        {
            mv.MLButtonDown = true;
            mv.SendUpdate(true);
            return;
        }
        mv.MLButtonDown = false;
        mv.MLButtonUp = true;
        mv.SendUpdate(true);
        mv.MLButtonUp = false;
    }
}
