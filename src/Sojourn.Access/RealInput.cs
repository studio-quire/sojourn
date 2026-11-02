using System.Runtime.InteropServices;

namespace Sojourn.Access;

/// <summary>
/// Whether what's being handled comes from the person. Answers to notices, confirmations and the grid's login
/// questions go only from the person's own press, and the person's own actions run without the assistant's
/// confirmations only when they began with the person's input: the assistant can run programs on this computer, and a
/// program can post key messages (which Windows marks as from no device), inject input (marked injected), send a
/// button a click, set a box's text, move the focus or invoke a control through UI Automation, or bring the window to
/// the front under a key meant for something else. Every input message the app takes is judged here as it arrives
/// (<see cref="InputJudge"/>), every change of focus, of the window in front and of the selected notice is noted with
/// who made it, and the standing of the message being handled is what <see cref="Confirms"/>, <see cref="Text"/> and
/// <see cref="Person"/> say. <see cref="Acting"/> carries the standing of the input an action began with through its
/// awaits. SOJOURN_TEST_INPUT=1 lets tests use posted keys, as in the VR client (=keys judges them as the hand's). Not
/// covered: software running as administrator or with UI Access, or a virtual keyboard driver the person installed, all
/// of which Windows itself treats as hardware.
/// </summary>
public sealed class RealInput : IMessageFilter
{
    public static readonly RealInput Filter = new();
    /// <summary>SOJOURN_INPUT_LOG=path: every key's judgement and every change of focus or selection with who made it, for
    /// telling why a press was refused in a real run.</summary>
    static readonly System.IO.StreamWriter? TraceTo = Environment.GetEnvironmentVariable("SOJOURN_INPUT_LOG") is { Length: > 0 } path
        ? new System.IO.StreamWriter(path, append: true) { AutoFlush = true } : null;
    static readonly bool TestInput = Environment.GetEnvironmentVariable("SOJOURN_TEST_INPUT") == "1";
    /// <summary>SOJOURN_TEST_INPUT=keys: posted keys and clicks are judged as the hand's, every other rule applying (the
    /// focus, our own moves, other threads' sends, held keys), and posted characters count as the person's typing.</summary>
    static readonly bool TestKeys = Environment.GetEnvironmentVariable("SOJOURN_TEST_INPUT") == "keys";

    /// <summary>How long an action begun from the person's input runs as theirs (a route, a walk and what it awaits).</summary>
    const long ActLife = 5 * 60_000;

    readonly InputJudge _judge = new();
    KeyHook? _hook;
    Standing _now = Standing.None;
    long _at;

    sealed record Origin(long At);
    static readonly AsyncLocal<Origin?> Began = new();

    // who's changing the focus: our own code's moves for the person
    HookProc? _sentProc;
    int _ours;
    nint _focus;

    /// <summary>When a key last went down here, from anything (what's being said may have been cut off then).</summary>
    public static long LastKeyAt { get; private set; }

    /// <summary>When a key last went down in a box being typed into (no Control or Alt held): the screen reader's speech
    /// stops at each one (NVDA's "speech interrupt for typed characters", on by default).</summary>
    public static long LastTypedAt { get; private set; }

    // the last few typed keys' times, on the window's thread only
    static readonly long[] _typed = new long[32];
    static int _typedNext;

    /// <summary>The first key typed into a box after <paramref name="t"/> among the last 32, or 0 (the window's thread).</summary>
    public static long FirstTypedAfter(long t)
    {
        long first = 0;
        foreach (var k in _typed) if (k > t && (first == 0 || k < first)) first = k;
        return first;
    }

    /// <summary>When Control or Shift was last pressed and let go with no other key between: the screen reader's own keys
    /// for stopping and pausing speech, so what it was saying was silenced on purpose.</summary>
    public static long SilencedAt { get; private set; }
    int _loneModifier;

    /// <summary>On the window's thread, before the first window: the hook's thread (its hook on while the app is in
    /// front) and this thread's watch on focus and activation.</summary>
    public static void Start()
    {
        var f = Filter;
        f._hook ??= KeyHook.Start(f._judge);
        if (f._sentProc != null) return;
        f._sentProc = f.Sent;
        SetWindowsHookEx(WH_CALLWNDPROC, f._sentProc, 0, GetCurrentThreadId());
    }

    public bool PreFilterMessage(ref Message m)
    {
        long now = Environment.TickCount64;
        Standing s;
        switch (m.Msg)
        {
            case WM_KEYDOWN or WM_SYSKEYDOWN:
            {
                LastKeyAt = now;
                int vk = (int)m.WParam;
                if (m.Msg == WM_KEYDOWN && vk is not (VK_SHIFT or VK_CONTROL) && (Control.ModifierKeys & (Keys.Control | Keys.Alt)) == 0
                    && Control.FromHandle(m.HWnd) is TextBoxBase { ReadOnly: false })
                {
                    LastTypedAt = now;
                    _typed[_typedNext++ % _typed.Length] = now;
                }
                if (vk is VK_SHIFT or VK_CONTROL) { if (((long)m.LParam & 0x40000000) == 0) _loneModifier = vk; }
                else _loneModifier = 0;
                var from = Source();
                s = _judge.KeyDown((int)m.WParam, from, now, MessageTime(now), ((long)m.LParam & 0x40000000) != 0, GetAncestor(m.HWnd, GA_ROOT));
                // an injected key with no press behind it: a program, or a screen reader that hooked in after us
                if (!s.Person && from == InputSource.Injected) _hook?.Refresh();
                Trace($"key 0x{(int)m.WParam:X2} {from} {s} sent {InSendMessageEx(0):X}");
                break;
            }
            case WM_KEYUP or WM_SYSKEYUP:
                if (_loneModifier != 0 && (int)m.WParam == _loneModifier) SilencedAt = now;
                _loneModifier = 0;
                s = _judge.KeyUp((int)m.WParam, Source(), now);
                break;
            case WM_LBUTTONDOWN or WM_LBUTTONUP:
                s = _judge.Mouse(m.Msg == WM_LBUTTONDOWN, Source(), MessageTime(now));
                break;
            case WM_CHAR or WM_SYSCHAR or WM_DEADCHAR or WM_SYSDEADCHAR or WM_IME_CHAR:
                s = TestKeys ? Standing.None with { Person = true } : _judge.Char((char)(long)m.WParam, Source());
                break;
            default:
                // anything else coming through the queue (a posted click, a timer, an invoke, a callback) isn't a press
                s = _judge.Other();
                break;
        }
        _now = s;
        _at = now;
        Began.Value = s.Person ? new Origin(now) : null;
        return false;
    }

    /// <summary>Only what Windows calls hardware is the hand's: injected input is a program's (or a screen reader's),
    /// and a posted message or one Windows made itself has no device behind it.</summary>
    static InputSource Source()
    {
        if (TestKeys) return InputSource.Hand;
        try
        {
            if (!GetCurrentInputMessageSource(out var src) || src.DeviceType == IMDT_UNAVAILABLE) return InputSource.Posted;
            return src.OriginId switch { IMO_HARDWARE => InputSource.Hand, IMO_INJECTED => InputSource.Injected, _ => InputSource.Posted };
        }
        catch (EntryPointNotFoundException) { return InputSource.Posted; }
    }

    /// <summary>When the message being handled happened, on Environment.TickCount64's clock (GetMessageTime is its low 32 bits).</summary>
    static long MessageTime(long now)
    {
        uint ago = unchecked((uint)now - (uint)GetMessageTime());
        return ago < 60_000 ? now - ago : now;
    }

    /// <summary>The message being handled is the one judged (taken under a second ago), and nothing from another thread
    /// sent what's being handled now (a program's click sent to a button, its text set into a box): Windows says so for
    /// the whole of its handling, the sends to our own windows it sets off included. (Not by pairing the sends a thread
    /// hook sees with their returns: UI Automation's connection messages, which a screen reader sets off at each focus
    /// or selection change, go in and never come back, and the next answering key was refused, the 2026-10-29 NVDA run.)</summary>
    static bool Current => Environment.TickCount64 - Filter._at < 1000 && InSendMessageEx(0) == 0;

    /// <summary>It answers a confirmation, a notice's key, the ToS: a hardware press, fresh, after what it acts on settled.</summary>
    public static bool Confirms => TestInput || (Filter._now.Confirms && Current);

    /// <summary>It sends a typed answer from an edit field: as <see cref="Confirms"/>, or the screen reader's pass-on of such a press.</summary>
    public static bool Text => TestInput || (Filter._now.Text && Current);

    /// <summary>It's the person's input (a held key's repeat too, and a key the screen reader passed on).</summary>
    public static bool Person => TestInput || (Filter._now.Person && Current);

    /// <summary>When the key or button being handled first went down (for a confirmation's arming).</summary>
    public static long PressedAt => TestInput || Filter._now.PressedAt == long.MinValue ? Environment.TickCount64 : Filter._now.PressedAt;

    /// <summary>Why the message being handled doesn't answer.</summary>
    public static Refusal Refused => TestInput ? Refusal.None : Filter._now.Why;

    /// <summary>Why the message being handled doesn't answer, in the person's words.</summary>
    public static string Why => Filter._now.Why switch
    {
        Refusal.Held => "That key was already down. Let go and press it again.",
        Refusal.FocusMoved => "That changed just as you pressed. Press again if you meant it.",
        _ => "That takes your own key.",
    };

    /// <summary>The action being run began with the person's input (the message that started it, carried through its
    /// awaits), not with a timer, a callback, a posted or injected key with no press behind it, or an invoke.</summary>
    public static bool Acting => TestInput || (Began.Value is { } o && Environment.TickCount64 - o.At < ActLife);

    /// <summary>What's started from here on doesn't carry the person's input (a session that outlives the press).</summary>
    public static void Detach() => Began.Value = null;

    /// <summary>Our own code moving the focus or the selection for the person (after an answer, to a confirmation they
    /// asked for): presses from now on may act on where it put them.</summary>
    public static void Ours(Action move)
    {
        Filter._ours++;
        try { move(); }
        finally { Filter._ours--; }
    }

    /// <summary>What an answer would act on changed (the selected notice): noted with who changed it.</summary>
    public static void Changed()
    {
        var cause = Filter.Attribute();
        Trace($"changed: {cause}");
        Filter._judge.Changed(cause, Environment.TickCount64);
    }

    static void Trace(string line)
    {
        if (TraceTo == null) return;
        lock (TraceTo) TraceTo.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
    }

    Cause Attribute() =>
        InSendMessageEx(0) != 0 ? Cause.Foreign
        : _ours > 0 ? Cause.Ours
        : _now.Person && Environment.TickCount64 - _at < 1000 ? Cause.Person
        : Cause.Foreign;

    // --- messages sent to this thread's windows: the focus and activation ---------------------------------------------

    nint Sent(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var cwp = Marshal.PtrToStructure<CallWndProcInfo>(lParam);
            if (cwp.Message == WM_SETFOCUS && cwp.Hwnd != _focus)
            {
                _focus = cwp.Hwnd;
                Changed();
            }
            else if (cwp.Message == WM_ACTIVATEAPP)
            {
                // coming to the front, or leaving it, is never the person's press here: their Alt+Tab is the system's
                _judge.Changed(Cause.Foreign, Environment.TickCount64);
                _hook?.Set(cwp.WParam != 0);
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    delegate nint HookProc(int code, nint wParam, nint lParam);

    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102, WM_DEADCHAR = 0x0103, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105,
        WM_SYSCHAR = 0x0106, WM_SYSDEADCHAR = 0x0107, WM_IME_CHAR = 0x0286, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_SETFOCUS = 0x0007, WM_ACTIVATEAPP = 0x001C;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;
    const int IMDT_UNAVAILABLE = 0, IMO_HARDWARE = 0x01, IMO_INJECTED = 0x02;
    const int WH_CALLWNDPROC = 4, GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct InputMessageSource { public int DeviceType; public int OriginId; }

    [StructLayout(LayoutKind.Sequential)]
    struct CallWndProcInfo { public nint LParam, WParam; public int Message; public nint Hwnd; }

    [DllImport("user32.dll")] static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
    [DllImport("user32.dll")] static extern uint InSendMessageEx(nint reserved);
    [DllImport("user32.dll")] static extern int GetMessageTime();
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, int flags);
    [DllImport("user32.dll")] static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
