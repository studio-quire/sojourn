using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Sojourn.Launcher;

/// <summary>What the launcher shows of a run.</summary>
enum Phase { Idle, Starting, Form, Connecting, Terms, Code, Failed, World, Ending, Ended, Crashed }

/// <summary>Question: the grid's question while terms or code (terms: {kind, text}; code: {asks}); Answers: the client
/// takes answers from here (its LauncherAnswers is open).</summary>
sealed record RunStatus(Phase Phase, bool Headset = false, string? Detail = null, string? Log = null, JsonObject? Question = null, bool Answers = false)
{
    /// <summary>A crash's exit code (CrashReport.cs).</summary>
    public int ExitCode { get; init; }
}

/// <summary>
/// One run of the VR client: started with the login already handed over (LoginStore.HandOver; the client logs in
/// with it and asks the grid's Terms of Service or MFA code itself), its console copied to logs/launcher-console.log,
/// and its state read from the file it writes (the client's LaunchState.cs). A run that starts itself again (a lost
/// GPU, back to its login screen) is followed: the new process writes its own pid there, and is the old one's child.
/// Stop asks the client's window to close, as the person closing it would, and ends it after 20 s if it hasn't.
/// The grid's terms and MFA code can be answered here (docs/launcher.md > The grid's questions at the desk): each run
/// gets an anonymous pipe whose read end it inherits (--launcher-answers H) and whose write end only this process
/// holds; an answer is one JSON line, sent only from the person's own press (RealPress), never logged.
/// </summary>
sealed class ClientRun
{
    readonly Places _places;
    Process? _proc;
    int _pid;
    StreamWriter? _console;
    readonly object _consoleLock = new();
    DateTime _stateTime;
    DateTime? _stopAskedAt;
    bool _crashMarked;
    AnonymousPipeServerStream? _answers;

    public RunStatus Status { get; private set; } = new(Phase.Idle);
    public bool Running => Status.Phase is not (Phase.Idle or Phase.Ended or Phase.Crashed);

    public ClientRun(Places places) => _places = places;

    /// <summary>Starts the client (the login already handed over); null, or why it couldn't.</summary>
    public string? Start(bool desktop, IReadOnlyList<string> extra)
    {
        var (psi, missing) = _places.Client();
        if (psi == null) return missing;
        foreach (var a in new[] { "--login", "--login-resume", "--base-dir", _places.BaseDir }) psi.ArgumentList.Add(a);
        // the answers' pipe: its read end inheritable until the client has it (nothing else is started meanwhile)
        _answers?.Dispose();
        _answers = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        psi.ArgumentList.Add("--launcher-answers");
        psi.ArgumentList.Add(_answers.GetClientHandleAsString());
        if (!desktop) psi.ArgumentList.Add("--vr");
        foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;   // its console is copied to a file, not shown
        psi.RedirectStandardInput = true;   // no console chat: it's the window's and the headset's
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.Environment["SOJOURN_STATE_FILE"] = _places.StatePath;
        Dumps(psi);
        try { File.Delete(_places.StatePath); } catch (Exception) { }
        Directory.CreateDirectory(Path.GetDirectoryName(_places.ConsoleLog)!);
        lock (_consoleLock)
        {
            _console?.Dispose();
            _console = new StreamWriter(new FileStream(_places.ConsoleLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        }
        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => Copy(e.Data);
        p.ErrorDataReceived += (_, e) => Copy(e.Data);
        try { p.Start(); }
        catch (Exception e)
        {
            _answers.Dispose();
            _answers = null;
            return e.Message;
        }
        finally { try { _answers?.DisposeLocalCopyOfClientHandle(); } catch (Exception) { } }
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
        _pid = p.Id;
        _stopAskedAt = null;
        _crashMarked = false;
        _stateTime = default;
        Status = new(Phase.Starting);
        return null;
    }

    void Copy(string? line)
    {
        if (line == null) return;
        lock (_consoleLock)
        {
            try { _console?.WriteLine(line); } catch (Exception) { }
            if (line.Contains("Unhandled exception", StringComparison.Ordinal) || line.StartsWith("Fatal error.", StringComparison.Ordinal)) _crashMarked = true;
        }
    }

    /// <summary>The person's answer to the grid's question, to the client (it takes it only for the question it's showing).
    /// False if the client can't be reached that way (it closed, or started without the pipe).</summary>
    public bool Answer(JsonObject answer)
    {
        if (_answers is not { IsConnected: true } pipe || !Status.Answers) return false;
        try
        {
            var line = Encoding.UTF8.GetBytes(answer.ToJsonString() + "\n");
            pipe.Write(line);
            pipe.Flush();
            Array.Clear(line);
            return true;
        }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>Asks the client to close; ended after 20 s if it hasn't.</summary>
    public void Stop()
    {
        if (_proc is not { } p || p.HasExited) return;
        _stopAskedAt ??= DateTime.UtcNow;
        try { if (!p.CloseMainWindow()) p.Kill(entireProcessTree: true); }
        catch (Exception) { }
        Status = Status with { Phase = Phase.Ending };
    }

    /// <summary>The launcher's timer: the state file read, the process followed. Returns whether the status changed.</summary>
    public bool Poll()
    {
        if (_proc == null || !Running) return false;
        var before = Status;
        var file = ReadState();
        if (file is { } f && f.Pid != _pid && Alive(f.Pid) is { } next) Follow(next);

        if (_stopAskedAt is { } asked && DateTime.UtcNow - asked > TimeSpan.FromSeconds(20) && !_proc.HasExited)
            try { _proc.Kill(entireProcessTree: true); } catch (Exception) { }

        if (_proc.HasExited)
        {
            // started again (a lost GPU, back to its login screen): the old run's child carries on
            if (Successor(_pid) is { } succ) { Follow(succ); Status = new(Phase.Starting); }
            else
            {
                int code = SafeExitCode(_proc);
                string? log = LogFor(file);
                bool crashed = _stopAskedAt == null && (code != 0 || _crashMarked);
                Status = crashed ? new(Phase.Crashed, Detail: code == 75 ? "the graphics card was reset" : null, Log: log) { ExitCode = code } : new(Phase.Ended, Log: log);
                lock (_consoleLock) { _console?.Dispose(); _console = null; }
                _answers?.Dispose();
                _answers = null;
                _proc = null;
                new Sojourn.Session.Login.LoginStore(_places.BaseDir).DropHandOver();   // a handover nobody took
            }
        }
        else if (file is { } st && st.Pid == _pid && _stopAskedAt == null)
            Status = new(st.Phase, st.Headset, st.Detail, st.Log, st.Question, st.Answers && _answers != null);
        return Status != before;
    }

    /// <summary>
    /// In runs from the repo (or with SOJOURN_DUMPS=1), a crash leaves a minidump in the base dir's dumps/ (the .NET
    /// runtime's createdump: managed crashes and native faults on .NET's threads; the newest three kept). A dump holds
    /// memory, so other people's words can be in it: it stays on the PC and never goes in a report. For people's own
    /// copies a dump would only take room, so packaged builds make none (docs/client.md > Problem and crash reports).
    /// </summary>
    void Dumps(ProcessStartInfo psi)
    {
        if (_places.Repo == null && Environment.GetEnvironmentVariable("SOJOURN_DUMPS") != "1") return;
        try
        {
            var dir = Path.Combine(_places.BaseDir, "dumps");
            Directory.CreateDirectory(dir);
            foreach (var old in new DirectoryInfo(dir).GetFiles("sojourn-*.dmp").OrderByDescending(f => f.LastWriteTimeUtc).Skip(2))
                try { old.Delete(); } catch (IOException) { }
            psi.Environment["DOTNET_DbgEnableMiniDump"] = "1";
            psi.Environment["DOTNET_DbgMiniDumpType"] = "1";   // stacks, threads and modules; not the heap
            psi.Environment["DOTNET_DbgMiniDumpName"] = Path.Combine(dir, "sojourn-%p.dmp");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    void Follow(Process next)
    {
        _proc = next;
        _pid = next.Id;
        _ = next.Handle;   // opened now, so its exit code can be read once it's gone
    }

    sealed record FileState(int Pid, Phase Phase, bool Headset, string? Detail, string? Log, JsonObject? Question, bool Answers);
    FileState? _last;

    FileState? ReadState()
    {
        try
        {
            var info = new FileInfo(_places.StatePath);
            if (!info.Exists) return _last = null;
            if (info.LastWriteTimeUtc == _stateTime && _last != null) return _last;
            _stateTime = info.LastWriteTimeUtc;
            using var s = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonNode.Parse(s) is not JsonObject o) return _last;
            var phase = (string?)o["state"] switch
            {
                "starting" => Phase.Starting, "form" => Phase.Form, "connecting" => Phase.Connecting, "terms" => Phase.Terms,
                "code" => Phase.Code, "failed" => Phase.Failed, "world" => Phase.World, "ending" => Phase.Ending, _ => Phase.Starting,
            };
            return _last = new((int?)o["pid"] ?? 0, phase, (bool?)o["headset"] ?? false, (string?)o["detail"], (string?)o["log"],
                o["question"] is JsonObject q ? (JsonObject)q.DeepClone() : null, (bool?)o["answers"] ?? false);
        }
        catch (Exception) { return _last; }
    }

    string LogFor(FileState? f)
    {
        if (_crashMarked || f?.Log is not { } log || !File.Exists(log)) return _places.ConsoleLog;
        return log;
    }

    static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch (Exception) { return 0; }
    }

    static Process? Alive(int pid)
    {
        try { var p = Process.GetProcessById(pid); return p.HasExited ? null : p; }
        catch (Exception) { return null; }
    }

    // ---- the old run's child: Toolhelp's snapshot has every process's parent ----

    Process? Successor(int parent)
    {
        string? name = null;
        try { name = Path.GetFileName(_places.Client().Psi?.FileName); } catch (Exception) { }
        foreach (var (pid, ppid, exe) in Snapshot())
            if (ppid == parent && string.Equals(exe, name, StringComparison.OrdinalIgnoreCase) && Alive(pid) is { } p) return p;
        return null;
    }

    static List<(int Pid, int Parent, string Exe)> Snapshot()
    {
        var list = new List<(int, int, string)>();
        var snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new ProcessEntry32 { Size = Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                list.Add(((int)e.ProcessId, (int)e.ParentProcessId, e.ExeFile));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public int Size;
        public uint Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
