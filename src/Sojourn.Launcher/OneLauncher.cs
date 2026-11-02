using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Sojourn.Launcher;

/// <summary>
/// One launcher per base dir: a second start (the Start menu again, or an agent's <c>dev.py launch</c> while the person
/// already has it open) hands its wish to the open one and exits with <see cref="HandedOver"/>, so there are never two
/// windows to sign in through. The wish is a line of words: <c>dev</c> (this launch's assistant is the person's own
/// agent, as <c>--dev</c>) and <c>front</c> (bring the window forward, from a start the person made). Nothing else is
/// taken, and only from this Windows account (the pipe is CurrentUserOnly). docs/launcher.md > One launcher.
/// Written by Claude Opus 5.5, 2026-11-02.
/// </summary>
static class OneLauncher
{
    public const int HandedOver = 3;

    static Mutex? _mine;   // held for the process's life

    static string Name(Places places) =>
        "sojourn-launcher-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(places.BaseDir.ToLowerInvariant())))[..16];

    /// <summary>True: this is the launcher for its base dir (wishes from later starts go to <paramref name="wish"/>, on
    /// a thread of its own). False: one was open and has this start's wish; exit.</summary>
    public static bool Claim(Places places, bool dev, Action<IReadOnlySet<string>> wish)
    {
        var name = Name(places);
        _mine = new Mutex(true, @"Local\" + name, out bool first);
        if (!first)
        {
            _mine.Dispose();
            _mine = null;
            if (HandOver(name, dev)) return false;
            return true;   // the open one didn't answer (closing as we start): a window of our own, as before
        }
        var listener = new Thread(() => Listen(name, wish)) { IsBackground = true, Name = "one launcher" };
        listener.Start();
        return true;
    }

    static bool HandOver(string name, bool dev)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(3000);
            // a start the person made may bring the open window forward (Windows lets the process it was given to)
            if (!Places.Background) AllowSetForegroundWindow(-1);   // ASFW_ANY
            var words = new List<string>();
            if (dev) words.Add("dev");
            if (!Places.Background) words.Add("front");
            var line = Encoding.UTF8.GetBytes(string.Join(' ', words) + "\n");
            pipe.Write(line);
            pipe.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    static void Listen(string name, Action<IReadOnlySet<string>> wish)
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                var buf = new byte[256];
                int n = 0, got;
                while (n < buf.Length && (got = pipe.Read(buf, n, buf.Length - n)) > 0)
                {
                    n += got;
                    if (Array.IndexOf(buf, (byte)'\n', 0, n) >= 0) break;
                }
                var words = Encoding.UTF8.GetString(buf, 0, n).Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w is "dev" or "front").ToHashSet();
                wish(words);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
