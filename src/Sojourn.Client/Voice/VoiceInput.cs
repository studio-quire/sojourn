using Silk.NET.Input;
using Sojourn.Client.Xr;
using Sojourn.Session.Control;
using Sojourn.Session.Voice;

namespace Sojourn.Client.Voice;

/// <summary>
/// Voice's buttons in this client (Sojourn.Session's <see cref="VoiceWiring"/> does the rest). Push-to-talk (the
/// default): hold the left grip in the headset, B or the middle mouse button on the desktop. Open mic: the right stick's
/// click in the headset, B on the desktop, toggles the mute.
/// </summary>
public static class VoiceInput
{
    public static void Bind(this VoiceWiring w, IKeyboard? kb, IMouse? mouse)
    {
        if (kb != null)
        {
            kb.KeyDown += (_, key, _) => { if (key == Key.B) w.KeyDown(); };
            kb.KeyUp += (_, key, _) => { if (key == Key.B) w.KeyUp(); };
        }
        if (mouse != null)
        {
            mouse.MouseDown += (_, b) => { if (b == MouseButton.Middle) w.MouseHeld(true); };
            mouse.MouseUp += (_, b) => { if (b == MouseButton.Middle) w.MouseHeld(false); };
        }
    }

    public static void HeadsetFrame(this VoiceWiring w, VrView? view, XrControllers input, bool assistantTalking) =>
        w.HeadsetFrame(view, input.LeftGrip, input.RightStickClick, assistantTalking);
}
