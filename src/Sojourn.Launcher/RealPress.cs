using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Sojourn.Launcher;

/// <summary>
/// Whether the input being handled now came from the person's own hand on the keyboard or mouse. The grid's terms, the
/// MFA code and a key kept here go only from such a press: the assistant runs commands on this computer and must never
/// stand in for the person, so a posted message (PostMessage), injected input (SendInput) or a UI Automation call (an
/// Invoke, a value set) never counts on its own.
///
/// Every message the window's thread takes is judged as it's taken (ComponentDispatcher.ThreadFilterMessage, before
/// WPF handles it), and <see cref="Now"/> answers for the one being handled. Windows' own word
/// (GetCurrentInputMessageSource) is asked there: hardware counts. Asked later, inside WPF's handling, it said
/// unavailable for the person's real keys (the 2026-10-30 guided test; something in WPF's handling peeks the queue),
/// so anything not called hardware counts only as the message of a press <see cref="HandHooks"/> saw the hand make
/// while the launcher was in front (<see cref="PressJudge"/>). SOJOURN_TEST_INPUT=1 lets tests press with posted input,
/// as the client's; SOJOURN_INPUT_LOG=path writes each key and click's judgement (never which character key).
/// </summary>
static class RealPress
{
    static readonly bool TestInput = Environment.GetEnvironmentVariable("SOJOURN_TEST_INPUT") == "1";
    static readonly System.IO.StreamWriter? TraceTo = Environment.GetEnvironmentVariable("SOJOURN_INPUT_LOG") is { Length: > 0 } path
        ? new System.IO.StreamWriter(path, append: true) { AutoFlush = true } : null;

    internal static readonly PressJudge Judge = new();   // (tools/inputcheck gives it presses as the hooks would)
    static HandHooks? _hooks;
    static bool _active, _over;
    static bool _counts;
    static long _at;

    /// <summary>On the window's thread, before the window shows: the judging, and the hooks while it's in front.</summary>
    public static void Start(Window window)
    {
        if (_hooks != null) return;
        _hooks = HandHooks.Start(Judge);
        ComponentDispatcher.ThreadFilterMessage += Taken;
        if (System.Windows.Application.Current is { } app)   // (tools/inputcheck has none: no hooks there)
        {
            app.Activated += (_, _) => { _active = true; Want(); };
            app.Deactivated += (_, _) => { _active = false; Want(); };
        }
        window.MouseEnter += (_, _) => { _over = true; Want(); };
        window.MouseLeave += (_, _) => { _over = false; Want(); };
    }

    static void Want() => _hooks?.Want(keys: _active, mouse: _active || _over);

    /// <summary>The message being handled is the person's own press (or the character of one).</summary>
    public static bool Now() => TestInput || (_counts && Environment.TickCount64 - _at < 1000 && InSendMessageEx(0) == 0);

    static void Taken(ref MSG m, ref bool handled)
    {
        long now = Environment.TickCount64;
        int msg = m.message;
        bool counts;
        switch (msg)
        {
            case WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP:
            {
                var from = Source();
                int vk = (int)m.wParam;
                bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                counts = Judge.KeyMessage(vk, down, from, now, GetAncestor(m.hwnd, GA_ROOT));
                Trace($"key {(down ? "down" : "up")} {Shown(vk)} {from} {(counts ? "counts" : "refused")}");
                break;
            }
            case WM_LBUTTONDOWN or WM_LBUTTONDBLCLK or WM_LBUTTONUP:
            {
                var from = Source();
                long lp = m.lParam;
                var pt = new Point32 { X = (short)(lp & 0xFFFF), Y = (short)((lp >> 16) & 0xFFFF) };
                ClientToScreen(m.hwnd, ref pt);
                counts = Judge.ButtonMessage(msg != WM_LBUTTONUP, pt.X, pt.Y, from, now);
                Trace($"button {(msg == WM_LBUTTONUP ? "up" : "down")} at {pt.X},{pt.Y} {from} {(counts ? "counts" : "refused")}");
                break;
            }
            case WM_CHAR or WM_SYSCHAR or WM_DEADCHAR or WM_SYSDEADCHAR or WM_IME_CHAR:
            {
                var from = Source();
                counts = Judge.CharMessage((char)(long)m.wParam, from);
                Trace($"char {from} {(counts ? "counts" : "refused")}");
                break;
            }
            default:
                // anything else through the queue (a posted callback, a timer, UI Automation's work) isn't a press
                counts = false;
                break;
        }
        _counts = counts;
        _at = now;
    }

    static InputSource Source()
    {
        try
        {
            if (!GetCurrentInputMessageSource(out var src) || src.DeviceType == IMDT_UNAVAILABLE) return InputSource.Unavailable;
            return src.OriginId switch { IMO_HARDWARE => InputSource.Hardware, IMO_INJECTED => InputSource.Injected, _ => InputSource.Unavailable };
        }
        catch (EntryPointNotFoundException) { return InputSource.Unavailable; }
    }

    /// <summary>A key as the trace shows it: character keys (a typed API key or code) only as "a character key",
    /// unless Ctrl is held (Ctrl+V).</summary>
    static string Shown(int vk) =>
        vk is (>= 0x30 and <= 0x5A) or (>= 0x60 and <= 0x6F) or (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDF) or 0xE2 or 0xE7
        && GetKeyState(0x11) >= 0 ? "(a character key)" : $"0x{vk:X2}";

    /// <summary>A line in SOJOURN_INPUT_LOG's file, if it's set (from either thread).</summary>
    internal static void Trace(string line)
    {
        if (TraceTo == null) return;
        lock (TraceTo) TraceTo.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
    }

    internal static bool Tracing => TraceTo != null;

    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102, WM_DEADCHAR = 0x0103, WM_SYSKEYDOWN = 0x0104,
        WM_SYSKEYUP = 0x0105, WM_SYSCHAR = 0x0106, WM_SYSDEADCHAR = 0x0107, WM_IME_CHAR = 0x0286,
        WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
    const int IMDT_UNAVAILABLE = 0, IMO_HARDWARE = 0x01, IMO_INJECTED = 0x02, GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct InputMessageSource { public int DeviceType; public int OriginId; }

    [StructLayout(LayoutKind.Sequential)]
    struct Point32 { public int X, Y; }

    [DllImport("user32.dll")] static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
    [DllImport("user32.dll")] static extern uint InSendMessageEx(nint reserved);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, int flags);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint hwnd, ref Point32 pt);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
}
