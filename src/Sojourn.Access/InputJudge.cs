namespace Sojourn.Access;

/// <summary>Where an input message came from, as Windows says (GetCurrentInputMessageSource).</summary>
public enum InputSource
{
    /// <summary>A keyboard or mouse in the person's hands (IMO_HARDWARE, and nothing else).</summary>
    Hand,
    /// <summary>SendInput or keybd_event: a program, or a screen reader passing on a key it took.</summary>
    Injected,
    /// <summary>Posted to a window (no device at all), or anything else Windows won't call hardware: a program.</summary>
    Posted,
}

/// <summary>Who changed what an answer would act on (the focused control, the selected notice, the window in front).</summary>
public enum Cause
{
    /// <summary>While one of the person's own presses was being handled (Tab, an arrow, a click).</summary>
    Person,
    /// <summary>Our own code acting for them (the focus put back on the notices after an answer).</summary>
    Ours,
    /// <summary>Anything else: another program, a UI Automation call, a notice arriving, the window coming to the front.</summary>
    Foreign,
}

/// <summary>Why a message doesn't answer, in the words the person hears.</summary>
public enum Refusal { None, NotYourKey, Held, FocusMoved }

/// <summary>What the message being handled may do.</summary>
/// <param name="Person">It's the person's input (their hand, or a key a screen reader took from their hand and passed on),
/// repeats included: their own actions run on it.</param>
/// <param name="Confirms">It answers a confirmation, a notice's key, the ToS: a hardware key or click, a fresh press
/// (not a held key's repeat), made after what it acts on last changed by anything but the person.</param>
/// <param name="Text">It sends a typed answer from an edit field (the llTextBox reply, the MFA code): as Confirms, or a
/// key the screen reader passed on for such a press.</param>
/// <param name="PressedAt">When its key or button first went down (Environment.TickCount64); for a release, its press's.</param>
public readonly record struct Standing(bool Person, bool Confirms, bool Text, long PressedAt, Refusal Why)
{
    public static readonly Standing None = new(false, false, false, long.MinValue, Refusal.NotYourKey);
    public static readonly Standing All = new(true, true, true, long.MinValue, Refusal.None);
}

/// <summary>
/// Whether an input message is the person's, and how far it counts. Pure logic, fed by RealInput (the window's
/// message filter) and KeyHook (a low-level keyboard hook); tools/inputcheck checks it with simulated sequences.
///
/// Confirmations and notices are answered on lists and buttons, where screen readers don't take keys, so they take a
/// hardware key or click only, and only a fresh press (Windows' repeat bit; a click's press) made after the focus, the
/// selected notice and the window in front last changed by anything but the person: a change by our own code acting
/// for them binds presses from then on, any other change from <see cref="Settle"/> after it (time to hear where they
/// are now). So a program can't move the focus onto Confirm, select another notice or bring the window to the front
/// under a key the person was pressing for something else.
///
/// Typed answers are sent from edit fields, where NVDA takes Enter, the arrows, Home, End, Page Up and Down, Backspace
/// and Delete before any window sees them and sends each on with keybd_event: the window gets them as injected input,
/// exactly as from a program. What tells them apart is the press: the hook, ahead of the screen reader's, sees the
/// hand's press while the app is in front, and each such press is good for one injected message of the same key into
/// the window that was in front when it was pressed, within <see cref="Match"/>. Those count for the person's actions
/// and for typed answers, never for confirmations.
///
/// Characters (WM_CHAR and the like) never answer anything; one counts as the person's only as the character of the
/// person's key-down just before it (what TranslateMessage makes of it), so text built in a box can be told apart from
/// text a program put there.
/// </summary>
public sealed class InputJudge
{
    /// <summary>How long a hand's press waits for the screen reader to send it on (NVDA does within a few hundred ms).</summary>
    public const long Match = 1000;
    /// <summary>How long after a change not the person's own a press must come to answer what's there now.</summary>
    public const long Settle = 700;

    readonly object _lock = new();

    // the hook's view: the hand's presses not yet matched, and which keys the hand holds down
    readonly List<Press> _presses = new();
    readonly Dictionary<int, long> _held = new();

    // the window's view: when each key's hardware press went down, and the key a screen reader just passed on
    readonly Dictionary<int, long> _downAt = new();
    (int Vk, long At, Press P)? _passed;
    long _mouseDownAt = long.MinValue;
    (bool Person, int Left)? _char;

    long _foreignAt = long.MinValue / 2, _oursAt = long.MinValue / 2;

    /// <summary>A press the hook saw: the key, when it went down, whether it's a held key's repeat, and the window in front.</summary>
    public readonly record struct Press(int Vk, long At, long FirstAt, bool Repeat, nint Window);

    // --- the hook's thread -----------------------------------------------------------------------------------------

    /// <summary>The hand pressed this key (or a held key repeated) while the app was in front, window in front.</summary>
    public void Pressed(int vk, long at, nint window)
    {
        vk = Generic(vk);
        lock (_lock)
        {
            bool repeat = _held.TryGetValue(vk, out var first);
            if (!repeat) _held[vk] = first = at;
            Prune(at);
            _presses.Add(new Press(vk, at, first, repeat, window));
            if (_presses.Count > 64) _presses.RemoveAt(0);
        }
    }

    /// <summary>The hand let go of this key.</summary>
    public void Released(int vk)
    {
        vk = Generic(vk);
        lock (_lock) _held.Remove(vk);
    }

    /// <summary>The hook was taken off (the app left the front): what it knew of held keys goes stale.</summary>
    public void HookOff()
    {
        lock (_lock) { _held.Clear(); _presses.Clear(); }
    }

    // --- what an answer acts on ----------------------------------------------------------------------------------------

    /// <summary>The focus, the selected notice or the window in front changed.</summary>
    public void Changed(Cause cause, long at)
    {
        lock (_lock)
        {
            if (cause == Cause.Foreign) _foreignAt = Math.Max(_foreignAt, at);
            else if (cause == Cause.Ours) _oursAt = Math.Max(_oursAt, at);
        }
    }

    /// <summary>A press made at this time may act on what's there now.</summary>
    public bool Settled(long pressedAt)
    {
        lock (_lock) return pressedAt >= _oursAt && pressedAt - _foreignAt >= Settle;
    }

    // --- the window's messages -----------------------------------------------------------------------------------------

    /// <summary>A key-down message. time: when Windows says it happened; repeat: its repeat bit; window: the top-level
    /// window it's for.</summary>
    public Standing KeyDown(int vk, InputSource from, long now, long time, bool repeat, nint window)
    {
        vk = Generic(vk);
        lock (_lock)
        {
            _char = null;
            Standing s;
            switch (from)
            {
                case InputSource.Hand:
                {
                    Take(vk, now, null);   // its press is used up, so nothing injected can ride on it
                    if (!repeat) _downAt[vk] = time;
                    long at = repeat ? _downAt.GetValueOrDefault(vk, long.MinValue) : time;
                    s = Answering(at, fresh: !repeat, hardware: true);
                    break;
                }
                case InputSource.Injected when Take(vk, now, window) is { } p:
                    _passed = (vk, now, p);
                    s = Answering(p.FirstAt, fresh: !p.Repeat, hardware: false);
                    break;
                default:
                    s = Standing.None;
                    break;
            }
            if (s.Person) _char = (true, 1);
            return s;
        }
    }

    /// <summary>A key-up message: the hand's, or the release a screen reader sent with a key it passed on.</summary>
    public Standing KeyUp(int vk, InputSource from, long now)
    {
        vk = Generic(vk);
        lock (_lock)
        {
            _char = null;
            if (from == InputSource.Hand)
            {
                // a button acts on Space's release: it answers as its press would have
                if (!_downAt.Remove(vk, out var at)) return Standing.None with { Person = true, Why = Refusal.Held };
                return Answering(at, fresh: true, hardware: true);
            }
            if (from != InputSource.Injected || _passed is not { } p || p.Vk != vk || now - p.At > Match) return Standing.None;
            _passed = null;
            return Answering(p.P.FirstAt, fresh: !p.P.Repeat, hardware: false);
        }
    }

    /// <summary>A mouse button message (left; down or up).</summary>
    public Standing Mouse(bool down, InputSource from, long time)
    {
        lock (_lock)
        {
            _char = null;
            if (from != InputSource.Hand) return Standing.None;
            if (down) _mouseDownAt = time;
            long at = _mouseDownAt;
            if (!down) _mouseDownAt = long.MinValue;
            if (at == long.MinValue) return Standing.None with { Person = true, Why = Refusal.Held };
            return Answering(at, fresh: true, hardware: true);
        }
    }

    /// <summary>A character message (WM_CHAR, WM_SYSCHAR, WM_DEADCHAR, WM_IME_CHAR): the person's only as the character
    /// of their key-down just before it (two for a surrogate pair), never an answer.</summary>
    public Standing Char(char ch, InputSource from)
    {
        lock (_lock)
        {
            if (from == InputSource.Injected && _passed == null) { _char = null; return Standing.None; }
            if (_char is not { Person: true, Left: > 0 } c) return Standing.None;
            _char = char.IsHighSurrogate(ch) ? c with { Left = 1 } : c with { Left = c.Left - 1 };
            return Standing.None with { Person = true, Why = Refusal.NotYourKey };
        }
    }

    /// <summary>Anything else that comes through the queue (a posted message, a timer, an invoke): nothing. A character
    /// still pending from the last key-down stays pending (a posted callback can come between the two).</summary>
    public Standing Other() => Standing.None;

    Standing Answering(long pressedAt, bool fresh, bool hardware)
    {
        var why = !fresh || pressedAt == long.MinValue ? Refusal.Held
            : !(pressedAt >= _oursAt && pressedAt - _foreignAt >= Settle) ? Refusal.FocusMoved
            : Refusal.None;
        bool text = why == Refusal.None;
        return new Standing(true, text && hardware, text, pressedAt, hardware || why != Refusal.None ? why : Refusal.NotYourKey);
    }

    Press? Take(int vk, long now, nint? window)
    {
        Prune(now);
        int i = _presses.FindIndex(p => p.Vk == vk && (window == null || p.Window == window));
        if (i < 0) return null;
        var press = _presses[i];
        _presses.RemoveAt(i);
        return press;
    }

    void Prune(long now) => _presses.RemoveAll(p => now - p.At > Match || p.At - now > Match);

    /// <summary>The hook sees left and right Shift, Ctrl and Alt; windows get the plain ones.</summary>
    static int Generic(int vk) => vk switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => vk,
    };
}
