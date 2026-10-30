using System.Runtime.InteropServices;

namespace Sojourn.Access;

/// <summary>
/// The hand's key presses and releases while this app is in front, told to <see cref="InputJudge"/>: a low-level
/// keyboard hook on a thread of its own, noting only which key, when, and the window in front. It's on only while the
/// app is the active one (RealInput turns it on and off at WM_ACTIVATEAPP), and sees nothing while another app is in
/// front. Low-level hooks are called newest first, and a screen reader's swallows the keys it takes before older hooks
/// see them (raw input doesn't see them either: checked 2026-10-29), so ours is put in front again every 2 s while
/// it's on, and at most once a second when a key came without its press (NVDA restarted, say): the new hook first, then
/// the old one off, never a gap.
/// </summary>
sealed class KeyHook
{
    readonly InputJudge _judge;
    readonly uint _pid = (uint)Environment.ProcessId;
    readonly HookProc _proc;
    uint _thread;
    nint _hook;
    nuint _timer;
    long _refreshedAt = long.MinValue / 2;

    KeyHook(InputJudge judge)
    {
        _judge = judge;
        _proc = Proc;
    }

    /// <summary>The hook's thread, waiting with the hook off.</summary>
    public static KeyHook Start(InputJudge judge)
    {
        var h = new KeyHook(judge);
        using var ready = new ManualResetEventSlim();
        new Thread(() => h.Run(ready)) { IsBackground = true, Name = "real keys", Priority = ThreadPriority.AboveNormal }.Start();
        ready.Wait(2000);
        return h;
    }

    /// <summary>On (the app came to the front) or off (it left).</summary>
    public void Set(bool on)
    {
        if (_thread != 0) PostThreadMessage(_thread, on ? WM_HOOK_ON : WM_HOOK_OFF, 0, 0);
    }

    /// <summary>In front of every other hook again, if it's on and wasn't refreshed in the last second.</summary>
    public void Refresh()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _refreshedAt) < 1000) return;
        Interlocked.Exchange(ref _refreshedAt, now);
        if (_thread != 0) PostThreadMessage(_thread, WM_REFRESH, 0, 0);
    }

    void Run(ManualResetEventSlim ready)
    {
        _thread = GetCurrentThreadId();
        // the thread's message queue exists from here, so Set before the first GetMessage isn't lost
        PeekMessage(out _, 0, 0, 0, 0);
        ready.Set();
        while (GetMessage(out var m, 0, 0, 0) > 0)
        {
            switch (m.Message)
            {
                case WM_HOOK_ON when _hook == 0:
                    _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                    _timer = SetTimer(0, 0, 2000, 0);
                    break;
                case WM_HOOK_OFF when _hook != 0:
                    KillTimer(0, _timer);
                    UnhookWindowsHookEx(_hook);
                    _hook = 0;
                    _judge.HookOff();
                    break;
                case WM_TIMER or WM_REFRESH when _hook != 0:
                {
                    var fresh = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                    if (fresh == 0) break;
                    UnhookWindowsHookEx(_hook);
                    _hook = fresh;
                    break;
                }
            }
        }
        if (_hook != 0) UnhookWindowsHookEx(_hook);
    }

    nint Proc(int code, nint wParam, nint lParam)
    {
        if (code == 0)
        {
            int vk = Marshal.ReadInt32(lParam, 0);
            int flags = Marshal.ReadInt32(lParam, 8);
            if ((flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) == 0)
            {
                if (wParam is WM_KEYUP or WM_SYSKEYUP) _judge.Released(vk);
                else if (wParam is WM_KEYDOWN or WM_SYSKEYDOWN && Front() is var w && w != 0) _judge.Pressed(vk, Environment.TickCount64, w);
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>The window in front, if it's one of this app's.</summary>
    nint Front()
    {
        var w = GetForegroundWindow();
        return w != 0 && GetWindowThreadProcessId(w, out var pid) != 0 && pid == _pid ? w : 0;
    }

    delegate nint HookProc(int code, nint wParam, nint lParam);

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_TIMER = 0x0113;
    const int WM_REFRESH = 0x8001, WM_HOOK_ON = 0x8002, WM_HOOK_OFF = 0x8003;
    const int LLKHF_INJECTED = 0x10, LLKHF_LOWER_IL_INJECTED = 0x02;

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
