using Silk.NET.Input;
using Sojourn.Client.Private;
using Sojourn.Core.Access;

namespace Sojourn.Client.Access;

/// <summary>
/// This client's keys for a screen reader user (the window's, desktop or the headset's mirror), only while it reads out:
/// - Enter confirms what waits for confirmation, Escape cancels it (a fresh press of a real key, once the confirmation
///   has armed; while typing to the assistant or in a page, Enter is still the typing's).
/// - Ctrl+N a waiting notice, newest first, read whole with its keys (again: older; Ctrl+Shift+N back); then its number keys
///   (1-9, 0, minus, equals) answer it, Delete puts it away, Escape is done with it.
/// - Ctrl+R says it again (the confirmation, the notice, or the newest line); Ctrl+[ and Ctrl+] step back and forward
///   through what was read; F1 says the keys.
/// The Sojourn Access app will have its own keyboard model over the same <see cref="ReaderActions"/>.
/// An answer takes a fresh press of a hardware key (RealKeys) made while the window had the focus: a key already held
/// when the window came to the front (a program can bring it there under a key meant for something else) doesn't
/// answer until it's let go and pressed again.
/// </summary>
public sealed class ReaderKeys(Announcer announcer, ReaderActions actions)
{
    readonly HashSet<Key> _down = new();
    readonly HashSet<Key> _heldAtFocus = new();
    long _focusAt = long.MinValue / 2;

    /// <summary>How long after the window comes to the front a key held then can still arrive as if fresh: GLFW let go
    /// of every key when the focus left, so a held key's first repeat after it comes back looks like a press, and a
    /// held key repeats within Windows' longest repeat delay (1 s).</summary>
    const long HeldWindow = 1500;

    /// <summary>The window got (true) or lost the focus.</summary>
    public void Focused(bool on)
    {
        _heldAtFocus.Clear();
        if (!on) return;
        _focusAt = Environment.TickCount64;
        foreach (var (key, vk) in Answering)
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) _heldAtFocus.Add(key);
    }

    /// <summary>The keys that answer, and their virtual-key codes.</summary>
    static readonly (Key Key, int Vk)[] Answering =
    {
        (Key.Enter, 0x0D), (Key.KeypadEnter, 0x0D), (Key.Escape, 0x1B), (Key.Delete, 0x2E), (Key.KeypadDecimal, 0x6E),
        (Key.Number1, 0x31), (Key.Number2, 0x32), (Key.Number3, 0x33), (Key.Number4, 0x34), (Key.Number5, 0x35),
        (Key.Number6, 0x36), (Key.Number7, 0x37), (Key.Number8, 0x38), (Key.Number9, 0x39), (Key.Number0, 0x30),
        (Key.Minus, 0xBD), (Key.Equal, 0xBB),
    };

    public static string? KeyName(int i) => i switch { < 9 => $"{i + 1}", 9 => "0", 10 => "minus", 11 => "equals", _ => null };

    const string Help = "Sojourn keys. Control N: the next notice waiting, with Shift the one before. On a notice, its number keys answer it, "
        + "Delete puts it away, Escape is done. Enter confirms what the assistant asks you to confirm, Escape cancels. Control R: say it again (a confirmation with every name in its list). "
        + "Control left and right bracket: back and forward through what was read. The assistant: hold T and speak, or Enter to type, Enter to send. "
        + "Control Shift H: stand up and go home.";

    /// <summary>A key went down (repeats of a held key are known as such): true if it was taken.</summary>
    public bool KeyDown(Key key, IKeyboard kb)
    {
        bool fresh = _down.Add(key);
        long now = Environment.TickCount64;
        // held since before the window came to the front: not a press made here
        if (_heldAtFocus.Contains(key) && now - _focusAt < HeldWindow) fresh = false;
        if (!announcer.Active) return false;
        bool ctrl = kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight);
        bool shift = kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight);
        switch (key)
        {
            case Key.Enter or Key.KeypadEnter when !ctrl && actions.ConfirmWaiting:
                // a held Enter (a request just sent) never confirms: only a press that starts now
                if (fresh) actions.Confirm(true, now, RealKeys.Down(RealKeys.Return));
                return true;
            case Key.Escape when actions.ConfirmWaiting:
                if (fresh) actions.Confirm(false, now, true);
                return true;
            case Key.Escape when actions.Picked:
                actions.LetGo();
                return true;
            case Key.F1 when !ctrl:
                announcer.Answer(Help, keep: false);
                return true;
            case Key.N when ctrl:
                if (fresh) actions.Step(shift ? -1 : 1);
                return true;
            case Key.R when ctrl:
                actions.Repeat();
                return true;
            case Key.LeftBracket when ctrl:
                announcer.Older();
                return true;
            case Key.RightBracket when ctrl:
                announcer.Newer();
                return true;
        }
        if (actions.Picked && !ctrl && !actions.ConfirmWaiting && NoticeKey(key) is { } k)
        {
            if (fresh) actions.Press(k.Index, RealKeys.Down(k.Vk));
            return true;
        }
        // the numpad's Del with Num Lock off comes as the keypad's decimal
        if (actions.Picked && !ctrl && key is Key.Delete or Key.KeypadDecimal)
        {
            if (fresh) actions.PutAway(RealKeys.Down(0x2E) || RealKeys.Down(0x6E));
            return true;
        }
        return false;
    }

    public void KeyUp(Key key)
    {
        _down.Remove(key);
        _heldAtFocus.Remove(key);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    /// <summary>The top row's keys for a notice's buttons, in order, and their virtual-key codes.</summary>
    static (int Index, int Vk)? NoticeKey(Key key) => key switch
    {
        >= Key.Number1 and <= Key.Number9 => (key - Key.Number1, 0x31 + (key - Key.Number1)),
        Key.Number0 => (9, 0x30),
        Key.Minus => (10, 0xBD),
        Key.Equal => (11, 0xBB),
        _ => null,
    };
}
