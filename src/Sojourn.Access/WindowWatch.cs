using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Sojourn.Access;

/// <summary>
/// The window's thread not coming back to its messages, noticed from a thread of its own and said. A frozen window is
/// silent to someone who can't see it go grey: their keys go nowhere and nothing tells them. A timer on the window's
/// thread stamps the time four times a second; the stamp stands still when the thread is held. (Not a ping by a sent
/// message: a wait inside a COM call still answers those, while the person's keys, posted, wait.)
///
/// Held 1 s or more is logged when it ends, for a tester's log. At 5 s it's said once, and at 30 s, then each minute,
/// with the way out: Control+Shift+Q, registered only while the thread is held, starts Sojourn Access again at the
/// login window (nothing else could: the window's own keys need that thread, and Windows' "not responding" dialog is
/// nothing to rely on). If it comes back after being said, that's said too. docs/access-app.md > How it fits.
/// </summary>
sealed class WindowWatch
{
    const long LogFrom = 1000, SayAt = 5000, OfferAt = 30_000, OfferEvery = 60_000;
    const int HotKeyId = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000, VK_Q = 0x51, WM_HOTKEY = 0x0312, PM_REMOVE = 1;

    readonly System.Windows.Forms.Timer _beat = new() { Interval = 250 };
    readonly Action<string> _say, _log;
    readonly Action _restart;
    long _stamp = Environment.TickCount64;

    /// <param name="say">Said through the reader, from the watch's thread.</param>
    /// <param name="log">Into the session's log, from the watch's thread.</param>
    /// <param name="restart">The person asked to start again (the watch's thread; the window's is held).</param>
    public WindowWatch(Action<string> say, Action<string> log, Action restart)
    {
        _say = say;
        _log = log;
        _restart = restart;
        _beat.Tick += (_, _) => Interlocked.Exchange(ref _stamp, Environment.TickCount64);
        _beat.Start();
        new Thread(Run) { IsBackground = true, Name = "window watch", Priority = ThreadPriority.AboveNormal }.Start();
    }

    void Run()
    {
        long last = Environment.TickCount64, heldFrom = 0, offerAt = 0;
        bool said = false, hotKey = false;
        while (true)
        {
            Thread.Sleep(250);
            long now = Environment.TickCount64;
            // the watch itself didn't run for a while (the PC asleep, or everything starved): that says nothing of the window
            if (now - last > 3000) Interlocked.Exchange(ref _stamp, now);
            last = now;
            long stamp = Interlocked.Read(ref _stamp);
            long held = now - stamp;
            if (held < LogFrom)
            {
                if (heldFrom != 0)
                {
                    _log($"window: its thread was held {(stamp - heldFrom) / 1000.0:0.0} s");
                    if (said) _say("The window is back.");
                    if (hotKey) UnregisterHotKey(0, HotKeyId);
                    heldFrom = 0;
                    said = hotKey = false;
                }
                continue;
            }
            if (heldFrom == 0) heldFrom = stamp;
            if (Debugger.IsAttached) continue;
            if (!said && held >= SayAt)
            {
                said = true;
                offerAt = heldFrom + OfferAt;
                _log("window: its thread has been held 5 s");
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

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(nint hwnd, int id, int mods, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] static extern bool PeekMessage(out Msg msg, nint hwnd, uint min, uint max, uint remove);
}
