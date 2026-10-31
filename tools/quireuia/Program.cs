// Copied from Radegast Legacy's contrib tools (projects/radegast/tools/QuireUia, written by Claude Opus 5.5) for
// Sojourn Access's checks (tools/access.py); here the server's default port is 9885 and the hidden desktop is
// "sojourn-access", so a Radegast session (9895, "quire") can run beside it. Key chords take alt too, keys and chars
// can go to whatever has the app's focus ("focus" as the selector), and descriptions mark focusable elements.
//
// QuireUia: see what a screen reader gets from a Windows app, and drive it, without touching the real desktop.
//
//   QuireUia launch [--desktop NAME] [--cwd DIR] [--wait] -- EXE ARGS...
//                                                     start EXE on a hidden desktop (default "sojourn-access") in DIR
//                                                     (default the exe's folder), inheriting our environment; prints the PID
//   QuireUia serve [--port N] [--log FILE]            (run on that desktop by launch) record events, take commands
//   QuireUia send [--port N] COMMAND...               send one command to the server, print its reply
//
// A process on another desktop can never take focus or input from the person at the PC: each desktop has its own
// foreground window and keyboard focus, and nothing there is drawn on the visible screen. UI Automation and WinEvents
// don't cross desktops, so the server runs on the hidden desktop beside the app and is reached over localhost TCP.
// Commands are listed in Server.Help.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: QuireUia launch|serve|send ..."); return 2; }
        var rest = args.Skip(1).ToList();
        switch (args[0])
        {
            case "launch": return Launcher.Run(rest);
            case "serve":
                // Nobody sees the console on the hidden desktop, so a crash is written down beside the exe.
                try { return Server.Run(rest); }
                catch (Exception e)
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "serve-crash.txt"), e.ToString());
                    return 1;
                }
            case "send": return Client.Run(rest);
            // (in a process of its own, run by the server's tabs: see ComUia.Tabs)
            case "tabs": Console.Out.Write(ComUia.Tabs(int.Parse(rest[0]))); return 0;
            default: Console.Error.WriteLine("unknown verb " + args[0]); return 2;
        }
    }

    public static string Opt(List<string> args, string name, string dflt)
    {
        int i = args.IndexOf(name);
        if (i < 0 || i + 1 >= args.Count) return dflt;
        var v = args[i + 1];
        args.RemoveRange(i, 2);
        return v;
    }
}

static class Client
{
    public static int Run(List<string> args)
    {
        int port = int.Parse(Program.Opt(args, "--port", "9885"));
        int timeout = int.Parse(Program.Opt(args, "--timeout", "60")) * 1000;
        var line = string.Join(" ", args);
        using (var c = new TcpClient())
        {
            c.Connect(IPAddress.Loopback, port);
            c.ReceiveTimeout = timeout;
            var s = c.GetStream();
            var b = Encoding.UTF8.GetBytes(line.Replace("\r", "").Replace("\n", "\\n") + "\n");
            s.Write(b, 0, b.Length);
            using (var r = new StreamReader(s, Encoding.UTF8))
            {
                var reply = r.ReadToEnd();
                var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                stdout.Write(reply);
                return reply.StartsWith("ERR") ? 1 : 0;
            }
        }
    }
}

static class Launcher
{
    const uint GENERIC_ALL = 0x10000000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, uint flags, uint access, IntPtr sa);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("user32.dll")] static extern uint WaitForInputIdle(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    // CREATE_NO_WINDOW, never a new console: Windows 11 hands new consoles to Windows Terminal, which opens them as a
    // tab on the person's own desktop. GUI programs ignore the flag.
    const uint CREATE_NO_WINDOW = 0x08000000, BELOW_NORMAL_PRIORITY_CLASS = 0x4000;

    public static int Run(List<string> args)
    {
        // our options only before "--", so the program's own arguments are passed through whatever they are
        int dd = args.IndexOf("--");
        var opts = dd < 0 ? args : args.Take(dd).ToList();
        var desktop = Program.Opt(opts, "--desktop", "sojourn-access");
        var cwd = Program.Opt(opts, "--cwd", null);
        bool wait = opts.Remove("--wait");
        if (dd < 0 || dd + 1 >= args.Count) { Console.Error.WriteLine("launch [--desktop NAME] [--cwd DIR] [--wait] -- EXE ARGS"); return 2; }
        var exe = args[dd + 1];
        var cmd = new StringBuilder(Quote(exe));
        foreach (var a in args.Skip(dd + 2)) cmd.Append(' ').Append(Quote(a));

        // Opens the desktop if it already exists. The handle is left open on purpose until we exit; the started
        // process's own threads keep the desktop alive after that.
        var h = CreateDesktop(desktop, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (h == IntPtr.Zero) { Console.Error.WriteLine("CreateDesktop failed: " + Marshal.GetLastWin32Error()); return 1; }

        var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = "WinSta0\\" + desktop };
        if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS,
                IntPtr.Zero, cwd ?? Path.GetDirectoryName(Path.GetFullPath(exe)), ref si, out var pi))
        {
            Console.Error.WriteLine("CreateProcess failed: " + Marshal.GetLastWin32Error());
            return 1;
        }
        CloseHandle(pi.hThread);
        // Our desktop handle is the only thing keeping a new desktop alive until the process's first thread is
        // attached to it, so don't exit (closing the handle) before the process has started up.
        if (WaitForInputIdle(pi.hProcess, 15000) != 0) Thread.Sleep(3000);
        Console.WriteLine(pi.dwProcessId);
        if (wait)
        {
            WaitForSingleObject(pi.hProcess, 0xFFFFFFFF);
            GetExitCodeProcess(pi.hProcess, out var code);
            Console.WriteLine("exit 0x" + code.ToString("x"));
        }
        CloseHandle(pi.hProcess);
        return 0;
    }

    static string Quote(string s) => s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? s : "\"" + s.Replace("\"", "\\\"") + "\"";
}
