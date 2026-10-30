using System.Runtime.InteropServices;

namespace Sojourn.Launcher;

/// <summary>
/// The hand's key presses and left-button clicks while the launcher is in front, told to <see cref="PressJudge"/>:
/// low-level hooks on a thread of their own, as Sojourn Access's KeyHook, noting only which key or point, when, and that
/// a window of ours was in front; nothing injected (LLKHF_INJECTED, LLMHF_INJECTED and their lower-integrity flags).
/// The keyboard hook is on only while the launcher is the active app; the mouse hook also while the pointer is over its
/// window, so a click that brings it to the front is seen letting go. Hooks are called newest first and one that
/// swallows keys (a screen reader's) hides them from older ones, so ours are put in front again every 2 s while on
/// (the new hook first, then the old one off, never a gap); Windows also drops a hook that once took too long, and
/// this puts it back.
/// </summary>
sealed class HandHooks
{
    readonly PressJudge _judge;
    readonly uint _pid = (uint)Environment.ProcessId;
    readonly HookProc _keyProc, _mouseProc;
    uint _thread;
    nint _keys, _mouse;
    nuint _timer;

    HandHooks(PressJudge judge)
    {
        _judge = judge;
        _keyProc = KeyProc;
        _mouseProc = MouseProc;
    }

    /// <summary>The hooks' thread, waiting with both off.</summary>
    public static HandHooks Start(PressJudge judge)
    {
        var h = new HandHooks(judge);
        using var ready = new ManualResetEventSlim();
        new Thread(() => h.Run(ready)) { IsBackground = true, Name = "real presses", Priority = ThreadPriority.AboveNormal }.Start();
        ready.Wait(2000);
        return h;
    }

    /// <summary>Which hooks should be on.</summary>
    public void Want(bool keys, bool mouse)
    {
        if (_thread != 0) PostThreadMessage(_thread, WM_WANT, keys ? 1 : 0, mouse ? 1 : 0);
    }

    void Run(ManualResetEventSlim ready)
    {
        _thread = GetCurrentThreadId();
        PeekMessage(out _, 0, 0, 0, 0);   // the queue exists from here, so a Want before the first GetMessage isn't lost
        ready.Set();
        while (GetMessage(out var m, 0, 0, 0) > 0)
        {
            if (m.Message == WM_WANT)
            {
                Set(ref _keys, WH_KEYBOARD_LL, _keyProc, m.WParam != 0, keys: true);
                Set(ref _mouse, WH_MOUSE_LL, _mouseProc, m.LParam != 0, keys: false);
                bool any = _keys != 0 || _mouse != 0;
                if (any && _timer == 0) _timer = SetTimer(0, 0, 2000, 0);
                else if (!any && _timer != 0) { KillTimer(0, _timer); _timer = 0; }
            }
            else if (m.Message == WM_TIMER)
            {
                Renew(ref _keys, WH_KEYBOARD_LL, _keyProc);
                Renew(ref _mouse, WH_MOUSE_LL, _mouseProc);
            }
        }
        if (_keys != 0) UnhookWindowsHookEx(_keys);
        if (_mouse != 0) UnhookWindowsHookEx(_mouse);
    }

    void Set(ref nint hook, int kind, HookProc proc, bool on, bool keys)
    {
        if (on && hook == 0)
        {
            hook = SetWindowsHookEx(kind, proc, GetModuleHandle(null), 0);
            if (RealPress.Tracing) RealPress.Trace($"hook: {(keys ? "keys" : "mouse")} {(hook != 0 ? "on" : $"refused ({Marshal.GetLastWin32Error()})")}");
        }
        else if (!on && hook != 0)
        {
            if (RealPress.Tracing) RealPress.Trace($"hook: {(keys ? "keys" : "mouse")} off");
            UnhookWindowsHookEx(hook);
            hook = 0;
            _judge.Forget(keys, !keys);
        }
    }

    static void Renew(ref nint hook, int kind, HookProc proc)
    {
        if (hook == 0) return;
        var fresh = SetWindowsHookEx(kind, proc, GetModuleHandle(null), 0);
        if (fresh == 0) return;
        UnhookWindowsHookEx(hook);
        hook = fresh;
    }

    nint KeyProc(int code, nint wParam, nint lParam)
    {
        if (code == 0)
        {
            int vk = Marshal.ReadInt32(lParam, 0);
            int flags = Marshal.ReadInt32(lParam, 8);
            if ((flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) == 0 && Front() is var w && w != 0)
            {
                bool down = wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
                _judge.Key(vk, down, Environment.TickCount64, w);
                if (RealPress.Tracing) RealPress.Trace($"hook: a key {(down ? "down" : "up")}");   // (never which)
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    nint MouseProc(int code, nint wParam, nint lParam)
    {
        // every move comes here too: only the left button's presses are looked at
        if (code == 0 && wParam is WM_LBUTTONDOWN or WM_LBUTTONUP)
        {
            int flags = Marshal.ReadInt32(lParam, 12);
            if ((flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) == 0 && Front() != 0)
            {
                int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);
                _judge.Button(wParam == WM_LBUTTONDOWN, x, y, Environment.TickCount64);
                if (RealPress.Tracing) RealPress.Trace($"hook: button {(wParam == WM_LBUTTONDOWN ? "down" : "up")} at {x},{y}");
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>The window in front, if it's one of ours.</summary>
    nint Front()
    {
        var w = GetForegroundWindow();
        return w != 0 && GetWindowThreadProcessId(w, out var pid) != 0 && pid == _pid ? w : 0;
    }

    delegate nint HookProc(int code, nint wParam, nint lParam);

    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_TIMER = 0x0113, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    const int WM_WANT = 0x8001;
    const int LLKHF_INJECTED = 0x10, LLKHF_LOWER_IL_INJECTED = 0x02, LLMHF_INJECTED = 0x01, LLMHF_LOWER_IL_INJECTED = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    struct Msg { public nint Hwnd; public int Message; public nint WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern int GetMessage(out Msg msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PeekMessage(out Msg msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, int msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [DllImport("user32.dll")] static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
