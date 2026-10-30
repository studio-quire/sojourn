namespace Sojourn.Launcher;

/// <summary>Where Windows says an input message came from (GetCurrentInputMessageSource).</summary>
enum InputSource
{
    /// <summary>IMO_HARDWARE: a keyboard or mouse in the person's hands.</summary>
    Hardware,
    /// <summary>IMO_INJECTED: SendInput or keybd_event (a program, or a screen reader passing on a key it took).</summary>
    Injected,
    /// <summary>Anything else: posted, made by the system, or Windows can't say (what WPF's real keys report too, seen
    /// 2026-10-30, hence the hook).</summary>
    Unavailable,
}

/// <summary>
/// Whether an input message is the person's: pure logic, fed by <see cref="HandHooks"/> (low-level hooks noting the
/// hand's presses while the launcher is in front) and <see cref="RealPress"/> (every message the window's thread takes,
/// as it's taken). A message Windows calls hardware is the hand's. Any other key or button message is the hand's only
/// as the message of a press the hooks saw: the same key, up or down, into the window that was in front (a button: at
/// the same point), within <see cref="Match"/>; each press is good for one message. So a program's posted, injected or
/// automated input never counts on its own; at most, raced in just behind the person's own press, it can stand for
/// that same press once. A character counts only as the character of the counted key-down just before it.
/// </summary>
sealed class PressJudge
{
    /// <summary>How long a hand's press waits for its message (a screen reader sends a key it took on within a few
    /// hundred ms).</summary>
    public const long Match = 1000;
    /// <summary>How far a button message's point may be from the hook's, in pixels (both per-monitor physical).</summary>
    const int Near = 2;

    enum Kind { KeyDown, KeyUp, ButtonDown, ButtonUp }

    readonly record struct Note(Kind Kind, int Vk, int X, int Y, nint Window, long At);

    readonly object _lock = new();
    readonly List<Note> _notes = new();
    int _chars;   // characters the last counted key-down may still make (two for a surrogate pair)

    // --- the hooks' thread -----------------------------------------------------------------------------------------

    /// <summary>The hand pressed (or let go of) a key while one of the launcher's windows was in front.</summary>
    public void Key(int vk, bool down, long at, nint window) => Add(new Note(down ? Kind.KeyDown : Kind.KeyUp, Generic(vk), 0, 0, window, at));

    /// <summary>The hand pressed (or let go of) the left button at this screen point while the launcher was in front.</summary>
    public void Button(bool down, int x, int y, long at) => Add(new Note(down ? Kind.ButtonDown : Kind.ButtonUp, 0, x, y, 0, at));

    /// <summary>A hook came off: its notes go.</summary>
    public void Forget(bool keys, bool buttons)
    {
        lock (_lock) _notes.RemoveAll(n => n.Kind is Kind.KeyDown or Kind.KeyUp ? keys : buttons);
    }

    void Add(Note n)
    {
        lock (_lock)
        {
            Prune(n.At);
            _notes.Add(n);
            if (_notes.Count > 64) _notes.RemoveAt(0);
        }
    }

    // --- the window's thread: each message as it's taken --------------------------------------------------------------

    /// <summary>A key message (down or up) for this top-level window.</summary>
    public bool KeyMessage(int vk, bool down, InputSource from, long now, nint window)
    {
        vk = Generic(vk);
        var kind = down ? Kind.KeyDown : Kind.KeyUp;
        lock (_lock)
        {
            bool counts = Take(n => n.Kind == kind && n.Vk == vk && n.Window == window, now) || from == InputSource.Hardware;
            _chars = down && counts ? 1 : 0;
            return counts;
        }
    }

    /// <summary>A left-button message (down or up) at this screen point.</summary>
    public bool ButtonMessage(bool down, int x, int y, InputSource from, long now)
    {
        var kind = down ? Kind.ButtonDown : Kind.ButtonUp;
        lock (_lock)
        {
            _chars = 0;
            return Take(n => n.Kind == kind && Math.Abs(n.X - x) <= Near && Math.Abs(n.Y - y) <= Near, now) || from == InputSource.Hardware;
        }
    }

    /// <summary>A character message (WM_CHAR and the like).</summary>
    public bool CharMessage(char ch, InputSource from)
    {
        lock (_lock)
        {
            if (from == InputSource.Hardware) return true;
            if (_chars <= 0) return false;
            _chars = char.IsHighSurrogate(ch) ? 1 : _chars - 1;
            return true;
        }
    }

    // A hardware message takes its press too, so nothing else can ride on it.
    bool Take(Predicate<Note> match, long now)
    {
        Prune(now);
        int i = _notes.FindIndex(match);
        if (i < 0) return false;
        _notes.RemoveAt(i);
        return true;
    }

    void Prune(long now) => _notes.RemoveAll(n => now - n.At > Match || n.At - now > Match);

    /// <summary>The hook sees left and right Shift, Ctrl and Alt; windows get the plain ones.</summary>
    static int Generic(int vk) => vk switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => vk,
    };
}
