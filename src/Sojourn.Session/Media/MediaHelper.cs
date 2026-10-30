using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using Sojourn.Render.Media;

namespace Sojourn.Session.Media;

/// <summary>
/// One media helper process and its control pipe (src/Sojourn.Media.Native: sojourn_web, CEF; sojourn_video, libVLC).
/// Started when first needed, in a job object of its own that ends it with the client and holds it below normal
/// priority; the video helper also runs at low integrity, with no children and no access to other programs' windows
/// or the clipboard (libVLC has no sandbox of its own). JSON lines both ways; events arrive on the reader thread.
/// Started again after a crash, with backoff.
/// Written by Claude Opus 5.5, 2026-10-26.
/// </summary>
public sealed class MediaHelper : IDisposable
{
    public string Kind { get; }
    public bool Running => _proc != IntPtr.Zero && _pipe is { IsConnected: true } && !_exited;
    public bool Ready { get; private set; }
    public string? Failure { get; private set; }
    /// <summary>The helper's own word on itself ("ready" carries CEF's version and whether the sandbox is on).</summary>
    public JsonObject? Hello { get; private set; }
    public event Action<JsonObject>? Event;
    public event Action? Exited;

    readonly string _exe;
    readonly string[] _args;
    readonly bool _lowIntegrity;
    readonly Action<string> _log;
    NamedPipeServerStream? _pipe;
    IntPtr _proc, _job;
    volatile bool _exited;
    readonly object _write = new();
    int _restarts;
    DateTime _retryAt = DateTime.MinValue;
    static int _seq;

    public MediaHelper(string kind, string exe, string[] args, bool lowIntegrity, Action<string> log)
    {
        Kind = kind;
        _exe = exe;
        _args = args;
        _lowIntegrity = lowIntegrity;
        _log = log;
    }

    Task? _starting;

    /// <summary>True once it's up and has said it's ready. Otherwise starts it in the background (once, and not while
    /// waiting out a backoff) and returns false: starting waits for the helper to connect, which mustn't hold the
    /// manager's lock (the frame takes it). <paramref name="settled"/> runs when a start ends, either way.</summary>
    public bool Ensure(Action? settled = null)
    {
        if (Running) return Ready;
        if (_starting is { IsCompleted: false }) return false;
        if (DateTime.UtcNow < _retryAt) return false;
        if (!File.Exists(_exe)) { Failure = $"{Kind} helper missing ({_exe}): python tools/media.py build"; return false; }
        _starting = Task.Run(() =>
        {
            try
            {
                Stop();
                Start();
            }
            catch (Exception e)
            {
                Failure = $"{Kind} helper didn't start: {e.Message}";
                _log($"media: {Failure}");
                Backoff();
            }
            finally { settled?.Invoke(); }
        });
        return false;
    }

    void Backoff()
    {
        _restarts++;
        _retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(5, _restarts - 1)));
    }

    unsafe void Start()
    {
        Ready = false;
        _exited = false;
        string name = $@"\\.\pipe\sojourn-media-{Environment.ProcessId}-{Kind}-{Interlocked.Increment(ref _seq)}";
        var sa = Security.LowIntegrityForUser(out var sd);
        IntPtr h;
        try
        {
            h = CreateNamedPipeW(name, 0x00000003 | 0x40000000 | 0x00080000 /* duplex, overlapped, first instance */, 0, 1, 1 << 16, 1 << 16, 0, ref sa);
        }
        finally { LocalFree(sd); }
        if (h == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateNamedPipe");
        _pipe = new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, new SafePipeHandle(h, ownsHandle: true));

        _job = CreateJobObjectW(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.Basic.LimitFlags = 0x2000 /* kill on job close */ | 0x20 /* priority class */;
        info.Basic.PriorityClass = 0x4000;   // below normal: the frame comes first
        if (_lowIntegrity)
        {
            info.Basic.LimitFlags |= 0x8 /* active process */ | 0x100 /* process memory */;
            info.Basic.ActiveProcessLimit = 1;
            info.ProcessMemoryLimit = (UIntPtr)(2048UL << 20);
        }
        SetInformationJobObject(_job, 9 /* extended limits */, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        if (_lowIntegrity)
        {
            // no other programs' windows, clipboard, desktops, atoms, display or system settings, no logging off
            var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS { UIRestrictionsClass = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x40 | 0x80 };
            SetInformationJobObject(_job, 4, ref ui, Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>());
        }

        var cmd = new StringBuilder($"\"{_exe}\" --sojourn-pipe={name}");
        foreach (var a in _args) cmd.Append(' ').Append(a.Contains(' ') ? a.Replace("=", "=\"") + "\"" : a);
        var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
        PROCESS_INFORMATION pi;
        const uint flags = 0x4 /* suspended */ | 0x08000000 /* no window */ | 0x4000 /* below normal */;
        string dir = Path.GetDirectoryName(_exe)!;
        bool ok;
        if (_lowIntegrity)
        {
            IntPtr token = LowIntegrityToken();
            try { ok = CreateProcessAsUserW(token, null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, dir, ref si, out pi); }
            finally { CloseHandle(token); }
        }
        else ok = CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, dir, ref si, out pi);
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess");
        if (!AssignProcessToJobObject(_job, pi.hProcess)) _log($"media: {Kind} helper not in its job ({Marshal.GetLastWin32Error()})");
        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);
        _proc = pi.hProcess;
        _log($"media: {Kind} helper started (pid {pi.dwProcessId}{(_lowIntegrity ? ", low integrity" : "")})");

        var pipe = _pipe;
        var connect = pipe.WaitForConnectionAsync();
        if (!connect.Wait(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("the helper didn't connect");
        var t = new Thread(() => Read(pipe)) { Name = $"media-{Kind}-pipe", IsBackground = true };
        t.Start();
        new Thread(() =>
        {
            WaitForSingleObject(_proc, uint.MaxValue);
            if (_exited) return;
            _exited = true;
            GetExitCodeProcess(_proc, out uint code);
            Failure = $"{Kind} helper ended (exit {code:X})";
            _log($"media: {Failure}");
            Ready = false;
            Backoff();
            Exited?.Invoke();
        }) { Name = $"media-{Kind}-watch", IsBackground = true }.Start();
    }

    void Read(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                JsonObject? ev;
                try { ev = JsonNode.Parse(line) as JsonObject; }
                catch { continue; }
                if (ev == null) continue;
                var kind = (string?)ev["ev"];
                if (kind == "ready") { Ready = true; Hello = ev; _restarts = 0; Failure = null; }
                else if (kind == "fatal") { Failure = (string?)ev["text"]; _log($"media: {Kind} helper: {Failure}"); }
                else if (kind == "log") { _log($"media {Kind}: {(string?)ev["msg"]}"); continue; }
                try { Event?.Invoke(ev); }
                catch (Exception e) { _log($"media: {Kind} event: {e.Message}"); }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Send(JsonObject cmd)
    {
        var pipe = _pipe;
        if (pipe is not { IsConnected: true }) return;
        var bytes = Encoding.UTF8.GetBytes(cmd.ToJsonString() + "\n");
        lock (_write)
        {
            try { pipe.Write(bytes, 0, bytes.Length); pipe.Flush(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    void Stop()
    {
        if (_pipe != null)
        {
            try { if (_pipe.IsConnected) Send(new JsonObject { ["op"] = "quit" }); } catch { }
            _pipe.Dispose();
            _pipe = null;
        }
        if (_proc != IntPtr.Zero)
        {
            _exited = true;
            if (WaitForSingleObject(_proc, 2000) != 0) TerminateProcess(_proc, 1);
            CloseHandle(_proc);
            _proc = IntPtr.Zero;
        }
        if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
        Ready = false;
    }

    public void Dispose() => Stop();

    // ---- a token like ours at low integrity (Designing Applications to Run at a Low Integrity Level) ----

    static IntPtr LowIntegrityToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0002 | 0x0001 | 0x0008 | 0x0080 /* duplicate, assign, query, adjust default */, out var own))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");
        try
        {
            if (!DuplicateTokenEx(own, 0x02000000 /* maximum allowed */, IntPtr.Zero, 2 /* impersonation */, 1 /* primary */, out var low))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");
            if (!ConvertStringSidToSidW("S-1-16-4096", out var sid)) throw new Win32Exception(Marshal.GetLastWin32Error(), "low SID");
            try
            {
                var tml = new TOKEN_MANDATORY_LABEL { Sid = sid, Attributes = 0x20 /* SE_GROUP_INTEGRITY */ };
                int size = Marshal.SizeOf<TOKEN_MANDATORY_LABEL>() + GetLengthSid(sid);
                if (!SetTokenInformation(low, 25 /* TokenIntegrityLevel */, ref tml, size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation");
            }
            finally { LocalFree(sid); }
            return low;
        }
        finally { CloseHandle(own); }
    }

    // ---- Win32 ----

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic;
        public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_UI_RESTRICTIONS { public uint UIRestrictionsClass; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_MANDATORY_LABEL { public IntPtr Sid; public uint Attributes; }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outSize, uint inSize, uint timeout, ref Security.SECURITY_ATTRIBUTES sa);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr sa, string? name);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_BASIC_UI_RESTRICTIONS info, int size);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);
    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUserW(IntPtr token, string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32")] static extern uint ResumeThread(IntPtr t);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32")] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32")] static extern IntPtr LocalFree(IntPtr h);
    [DllImport("advapi32", SetLastError = true)] static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr sa, int impersonation, int type, out IntPtr dup);
    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool ConvertStringSidToSidW(string sid, out IntPtr psid);
    [DllImport("advapi32")] static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32", SetLastError = true)]
    static extern bool SetTokenInformation(IntPtr token, int cls, ref TOKEN_MANDATORY_LABEL info, int size);
}
