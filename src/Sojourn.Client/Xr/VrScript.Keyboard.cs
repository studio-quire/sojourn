using System.Numerics;

namespace Sojourn.Client.Xr;

/// <summary>
/// SOJOURN_VR_SCRIPT=keyboard: the headset's keyboard (Control/HeadsetKeyboard) against a simulated headset. With
/// SOJOURN_KEYBOARD_TARGET=assistant (the default) the right hand presses the panel's keyboard key; =page, the keyboard
/// key on the page board's strip (a page must be up close with a text field focused: tools/hud.py keyboard sets that up
/// through the agent API). Then: h, i (right), shift and o (left: "O"), ⌫ held (it repeats: "hiO" goes), o, k, 123
/// and ! (the symbols, left), enter; done at 15 s. The assistant's panel then holds the request typed ("ok!", logged:
/// the script's own words); a page's field gets the keys.
/// </summary>
public sealed partial class VrScript
{
    string? _keyboardTarget;
    bool _keyboardReady;
    public Sojourn.Client.Control.HeadsetKeyboard? Keyboard { get; set; }
    public Sojourn.Session.Assistant.AssistantHost? Assistant { get; set; }
    /// <summary>The panel's layer this frame (room pose, size in metres), for aiming at its keys.</summary>
    public Func<(Vector3 Pos, Quaternion Rot, Vector2 Size)?>? PanelPlane { get; set; }

    static readonly (double At, string What, string? Key, int Hand)[] KeyboardSteps =
    {
        (0, "right hand on the keyboard key", null, 1), (1.5, "right trigger: the keyboard comes up", null, 1),
        (3, "h", "h", 1), (4, "i", "i", 1), (5, "shift (left)", "shift", 0), (6, "O (left)", "o", 0),
        (7, "back held 0.8 s: hiO goes", "back", 1), (9, "o", "o", 1), (10, "k", "k", 1), (11, "123", "123", 1), (12, "! (left)", "!", 0),
        (13, "enter", "enter", 1), (15, "done", null, 1),
    };

    static double into0(double t, int step) => t - KeyboardSteps[step].At;

    void KeyboardRoutine(XrFrame f, double t)
    {
        int step = Array.FindLastIndex(KeyboardSteps, s => s.At <= t);
        if (step != _said)
        {
            _said = step;
            _log($"keyboard script {t:F1} s: {KeyboardSteps[step].What}");
            if (KeyboardSteps[step].What == "done" && _keyboardTarget == "assistant" && Assistant is { } a)
                _log($"keyboard script: the panel heard \"{a.Panel.Read(p => p.Heard)}\", keyboard {(Keyboard?.Shown == true ? "up" : "away")}");
        }
        if (t > 15 || Keyboard is not { } kb) return;
        var c = f.Input;
        var head = (f.Eyes[0].Position + f.Eyes[1].Position) * 0.5f;
        var from = new[] { head + new Vector3(-0.2f, -0.35f, -0.25f), head + new Vector3(0.2f, -0.35f, -0.25f) };
        Vector3? at = null;
        int hand = KeyboardSteps[step].Hand;
        if (t < 3)
        {
            if (_keyboardTarget == "page") at = Media?.View.KeyPoint("keyboard");
            else if (PanelPlane?.Invoke() is { } pl && Assistant?.KeyFraction(Sojourn.Session.Assistant.KeyKind.Keyboard) is { } fr)
                at = pl.Pos + Vector3.Transform(new Vector3((fr.X - 0.5f) * pl.Size.X, (0.5f - fr.Y) * pl.Size.Y, 0), pl.Rot);
        }
        else if (KeyboardSteps[step].Key is { } key) at = kb.KeyPoint(key);
        if (at == null && into0(t, step) < 0.05)
            _log($"keyboard script: nothing to aim at for {KeyboardSteps[step].What} (panel {(PanelPlane?.Invoke() != null ? "up" : "away")}, " +
                 $"its key {(Assistant?.KeyFraction(Sojourn.Session.Assistant.KeyKind.Keyboard) != null ? "there" : "not there")}, keyboard {(kb.Shown ? "up" : "away")})");
        if (at is { } p)
        {
            if (hand == 1) { c.RightAimPos = from[1]; c.RightAim = LookRotation(Vector3.Normalize(p - from[1])); }
            else { c.LeftAimPos = from[0]; c.LeftAim = LookRotation(Vector3.Normalize(p - from[0])); }
        }
        // a tap 0.5 s into each step (the back key held 0.9 s: it repeats after 0.45)
        double into = t - KeyboardSteps[step].At;
        bool down = step == 1 ? into < 0.25 : KeyboardSteps[step].Key == "back" ? into is >= 0.5 and < 1.3 : into is >= 0.5 and < 0.7;
        if (step >= 1 && step < KeyboardSteps.Length - 1)
        {
            if (hand == 1) c.RightTrigger = down ? 1 : 0;
            else c.LeftTrigger = down ? 1 : 0;
        }
        f.Input = c;
    }
}
