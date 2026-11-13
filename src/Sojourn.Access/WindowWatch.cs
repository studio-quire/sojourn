using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace Sojourn.Access;

/// <summary>
/// The window's thread not coming back to its messages, noticed from a thread of its own and said. A frozen window is
/// silent to someone who can't see it go grey: their keys go nowhere and nothing tells them. Four times a second the
/// watch posts a message to the window's thread (once the last was answered), which stamps the time when it's handled;
/// the stamp stands still while the thread is held, and while a flood of posted messages keeps it from her keys, which
/// come after them. (Not a sent ping: a wait inside a COM call still answers those. Not a WinForms timer: its ticks
/// stop under a flood of posted messages while the window still answers, and a COM wait can dispatch them.)
///
/// Held 1 s or more is logged when it ends, for a tester's log, with the thread's own processor time over it (waiting or
/// at work), how busy the PC's processors were and its memory free. At 5 s it's said once, and at 30 s, then each minute,
/// with the way out: Control+Shift+Q, registered only while the thread is held, starts Sojourn Access again at the
/// login window (nothing else could: the window's own keys need that thread, and Windows' "not responding" dialog is
/// nothing to rely on). If it comes back after being said, that's said too. At 2 s, and again at 10 s, what the thread
/// is doing is logged: its stack, read from a snapshot of this process (ClrMD), since a tester's log said only how long
/// (13 s once, 2026-11-10, with nothing around it to say why). docs/access-app.md > How it fits.
/// </summary>
sealed class WindowWatch
{
    const long LogFrom = 1000, SayAt = 5000, OfferAt = 30_000, OfferEvery = 60_000, StackAt = 2000, StackAgainAt = 10_000;
    const int StacksAtMost = 12;   // a session's: a PC that keeps stalling needn't fill the log
    const int HotKeyId = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000, VK_Q = 0x51, WM_HOTKEY = 0x0312, PM_REMOVE = 1;

    readonly Control _ear = new();
    readonly Action<string> _say, _log;
    readonly Action _restart;
    readonly int _windowThread = Environment.CurrentManagedThreadId;   // (made on the window's thread)
    readonly nint _thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, GetCurrentThreadId());   // (the same)
    long _stamp = Environment.TickCount64;
    int _asked, _stacks;

    /// <param name="say">Said through the reader, from the watch's thread.</param>
    /// <param name="log">Into the session's log, from the watch's thread.</param>
    /// <param name="restart">The person asked to start again (the watch's thread; the window's is held).</param>
    public WindowWatch(Action<string> say, Action<string> log, Action restart)
    {
        _say = say;
        _log = log;
        _restart = restart;
        _ = _ear.Handle;   // (made now, on the window's thread: what the watch's posts go to; WinForms parks it, hidden)
        new Thread(Run) { IsBackground = true, Name = "window watch", Priority = ThreadPriority.AboveNormal }.Start();
    }

    void Run()
    {
        long last = Environment.TickCount64, heldFrom = 0, offerAt = 0;
        bool said = false, hotKey = false;
        int stacksThisTime = 0;
        var quiet = Read(last);   // the times when the thread last answered promptly, to measure a hold from
        while (true)
        {
            Thread.Sleep(250);
            long now = Environment.TickCount64;
            // the watch itself didn't run for a while (the PC asleep, or everything starved): that says nothing of the window
            if (now - last > 3000) Interlocked.Exchange(ref _stamp, now);
            last = now;
            if (Interlocked.CompareExchange(ref _asked, 1, 0) == 0)
            {
                try { _ear.BeginInvoke(Answered); }
                catch (InvalidOperationException) { return; }   // (the app is closing)
            }
            long stamp = Interlocked.Read(ref _stamp);
            long held = now - stamp;
            if (held < LogFrom)
            {
                if (heldFrom != 0)
                {
                    _log($"window: its thread was held {(stamp - heldFrom) / 1000.0:0.0} s ({During(quiet, now)})");
                    if (said) _say("The window is back.");
                    if (hotKey) UnregisterHotKey(0, HotKeyId);
                    heldFrom = 0;
                    said = hotKey = false;
                    stacksThisTime = 0;
                }
                if (held < 600) quiet = Read(now);
                continue;
            }
            if (heldFrom == 0) heldFrom = stamp;
            if (Debugger.IsAttached) continue;
            if (held >= (stacksThisTime == 0 ? StackAt : StackAgainAt) && stacksThisTime < 2 && _stacks < StacksAtMost)
            {
                stacksThisTime++;
                _stacks++;
                LogStack(held);
            }
            if (!said && held >= SayAt)
            {
                said = true;
                offerAt = heldFrom + OfferAt;
                _log($"window: its thread has been held 5 s ({During(quiet, now)})");
                _say("The window isn't answering. Waiting for it.");
            }
            if (said && now >= offerAt)
            {
                offerAt = now + OfferEvery;
                hotKey = hotKey || RegisterHotKey(0, HotKeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_Q);
                _log($"window: its thread has been held {held / 1000} s; {(hotKey ? "Control+Shift+Q offered" : "Control+Shift+Q taken by another program")}");
                _say(hotKey
                    ? $"The window still isn't answering, {held / 1000} seconds now. Control+Shift+Q closes it and starts Sojourn Access again, at the login window."
                    : $"The window still isn't answering, {held / 1000} seconds now.");
            }
            if (hotKey && PeekMessage(out var m, 0, WM_HOTKEY, WM_HOTKEY, PM_REMOVE) && m.Message == WM_HOTKEY && m.WParam == HotKeyId)
            {
                _log("window: Control+Shift+Q, starting again");
                _say("Starting again.");
                Thread.Sleep(800);   // (the reader's line goes out before this process does)
                _restart();
                return;
            }
        }
    }

    /// <summary>The window's thread's stack into the log, innermost first, from a thread of its own (a snapshot can take
    /// a second, and the watch must go on).</summary>
    void LogStack(long held) => new Thread(() =>
    {
        try
        {
            var took = Stopwatch.StartNew();
            // the runtime's reader of its state: the package's own copy beside the exe (a single file leaves it out); from
            // source, the installed runtime's. Never fetched from Microsoft's symbol server, ClrMD's last resort
            var dac = Path.Combine(AppContext.BaseDirectory, "mscordaccore.dll");
            bool single = string.IsNullOrEmpty(typeof(WindowWatch).Assembly.Location);
            if (single && !File.Exists(dac)) { _log($"window: held {held / 1000.0:0.0} s; no mscordaccore.dll beside the app to read its stack"); return; }
            using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            using var runtime = File.Exists(dac) ? target.ClrVersions[0].CreateRuntime(dac) : target.ClrVersions[0].CreateRuntime();
            if (runtime.Threads.FirstOrDefault(t => t.ManagedThreadId == _windowThread) is not { } thread)
            {
                _log($"window: held {held / 1000.0:0.0} s; its thread not found in the snapshot");
                return;
            }
            var frames = new List<string>();
            foreach (var f in thread.EnumerateStackTrace())
                if (Frame(f) is { } name && (frames.Count == 0 || frames[^1] != name)) frames.Add(name);
            _log($"window: held {held / 1000.0:0.0} s, its thread in (innermost first; read in {took.ElapsedMilliseconds} ms): "
                + string.Join(" < ", frames.Take(32)) + (frames.Count > 32 ? $" < ... {frames.Count - 32} more" : ""));
        }
        catch (Exception e) { _log($"window: held {held / 1000.0:0.0} s; its stack couldn't be read ({e.GetType().Name}: {e.Message})"); }
    }) { IsBackground = true, Name = "window watch stack" }.Start();

    static string? Frame(ClrStackFrame f)
    {
        if (f.Method is { } m) return $"{Short(m.Type?.Name)}.{Plain(m.Name)}";
        return f.FrameName is { Length: > 0 } n ? $"[{n}]" : null;

        // "Sojourn.Access.Commands+<RunAsync>d__10" as "Commands+<RunAsync>": an async method's or lambda's own name kept
        static string Short(string? type)
        {
            if (type == null) return "?";
            var nested = type.IndexOf("+<", StringComparison.Ordinal);
            if (nested > 0 && type.IndexOf('>', nested) is var end and > 0) return Short(type[..nested]) + type[(nested)..(end + 1)];
            var plain = Plain(type);
            return plain[(plain.LastIndexOf('.') + 1)..];
        }

        static string Plain(string? name)   // generic arguments left out
        {
            if (name == null) return "?";
            var generic = name.IndexOfAny(new[] { '[', '`' });
            return generic > 0 ? name[..generic] : name;
        }
    }

    /// <summary>The window's thread's processor time and the PC's (100 ns units; the kernel's time includes idle time).</summary>
    readonly record struct Times(long At, long Thread, long Idle, long All);

    Times Read(long at)
    {
        long thread = GetThreadTimes(_thread, out _, out _, out var kernel, out var user) ? kernel + user : -1;
        if (!GetSystemTimes(out var idle, out var sysKernel, out var sysUser)) idle = sysKernel = sysUser = 0;
        return new(at, thread, idle, sysKernel + sysUser);
    }

    /// <summary>Over a hold, for telling who held it: a thread with little processor time of its own was kept waiting (the
    /// disk, a lock, another program), one at work throughout was busy with something of ours; and how busy the PC was and
    /// how much memory it had left (a tester's 9 s, 2026-11-11, had a stack 2.1 s in and nothing to say about the rest).</summary>
    string During(Times from, long now)
    {
        var to = Read(now);
        var parts = new List<string>();
        if (from.Thread >= 0 && to.Thread >= 0)
            parts.Add($"its own processor time {(to.Thread - from.Thread) / 1e7:0.0} s of the {(to.At - from.At) / 1000.0:0.0} s measured");
        long all = to.All - from.All;
        if (all > 0) parts.Add($"the PC's processors {100 - 100.0 * (to.Idle - from.Idle) / all:0}% busy");
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref m))
        {
            long mine;
            try { using var p = Process.GetCurrentProcess(); mine = p.PrivateMemorySize64; }
            catch (Exception) { mine = Environment.WorkingSet; }
            parts.Add($"{m.AvailPhys / 1e9:0.0} GB of its {m.TotalPhys / 1e9:0.0} GB memory free, this app {mine / 1e9:0.0} GB");
        }
        return string.Join("; ", parts);
    }

    void Answered()
    {
        Interlocked.Exchange(ref _stamp, Environment.TickCount64);
        Interlocked.Exchange(ref _asked, 0);
    }

    /// <summary>Spoken by Windows' own voice, for a reader whose words go through UI Automation: a notification would
    /// wait on the held thread. Only with a reader running (a test's hidden desktop has none, and the PC's speakers are
    /// the person's).</summary>
    public static void SayByWindows(string text) => new Thread(() =>
    {
        try
        {
            if (Type.GetTypeFromProgID("SAPI.SpVoice") is not { } type || Activator.CreateInstance(type) is not { } voice) return;
            type.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, new object[] { text, 0 });
            Marshal.FinalReleaseComObject(voice);
        }
        catch (Exception) { }   // (nothing more to try)
    }) { IsBackground = true, Name = "window watch voice" }.Start();

    [StructLayout(LayoutKind.Sequential)]
    struct Msg { public nint Hwnd; public int Message; public nint WParam, LParam; public uint Time; public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }

    const int THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    [DllImport("kernel32.dll")] static extern nint OpenThread(int access, bool inherit, int id);
    [DllImport("kernel32.dll")] static extern int GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(nint hwnd, int id, int mods, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] static extern bool PeekMessage(out Msg msg, nint hwnd, uint min, uint max, uint remove);
}
