using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Sojourn.Session.Assistant;

/// <summary>
/// The assistant's Python side (assistant/main.py in the assistant venv: Whisper, the Claude sessions, the tools),
/// started with the client and tied to it by a Windows job object, so it and everything it starts (the Claude Code
/// processes) end when the client does, even if the client crashes.
/// </summary>
public sealed class Sidecar : IDisposable
{
    readonly Process _proc;
    readonly nint _job;

    Sidecar(Process proc, nint job) { _proc = proc; _job = job; }

    /// <summary>Its process is still there (loading, or running).</summary>
    public bool Running
    {
        get
        {
            try { return !_proc.HasExited; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Proves a call is the client's own sidecar, for what only the person's own voice may do through it
    /// (Private/PrivateApi.cs: dictation). Only in the sidecar's environment,
    /// which it takes out of its own before any Claude session starts.</summary>
    public static readonly string Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

    public static bool IsToken(string? given) => given != null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(Token));

    /// <summary>devToken: dev mode (docs/dev-mode.md): the sidecar hears and speaks but passes what the person says to the
    /// session driving the client instead of asking Claude, proving it's the client's own with the token.</summary>
    public static Sidecar? Start(int apiPort, bool vr, string mode, ILogger log, string? devToken = null)
    {
        var root = FindRoot();
        var bundled = root == null ? Bundled : null;
        if (root == null && bundled == null) { log.LogWarning("assistant: no assistant/main.py above {Dir}", AppContext.BaseDirectory); return null; }
        var python = root != null
            ? Path.Combine(root, "local", "assistant-venv", "Scripts", "python.exe")
            : Path.Combine(bundled!, "python", "python.exe");
        if (!File.Exists(python))
        {
            log.LogWarning("assistant: no venv at {Python}; run python tools/assistant.py setup", python);
            return null;
        }
        var home = root ?? bundled!;
        var psi = new ProcessStartInfo(python)
        {
            WorkingDirectory = root ?? LocalDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-u");
        if (root == null)
        {
            // a packaged build (docs/access-app.md > Packaging): its own Python beside the program, which stays as it
            // was unpacked (no bytecode written, nothing from the PC's Python environment), the data in LocalDir
            Directory.CreateDirectory(LocalDir);
            psi.ArgumentList.Add("-B");
            psi.ArgumentList.Add("-E");
            psi.ArgumentList.Add("-s");
            psi.Environment["SOJOURN_LOCAL"] = LocalDir;
            psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            foreach (var k in new[] { "PYTHONHOME", "PYTHONPATH", "PYTHONSTARTUP", "PYTHONUSERBASE" }) psi.Environment.Remove(k);
        }
        psi.ArgumentList.Add(Path.Combine(home, "assistant", "main.py"));
        psi.ArgumentList.Add("--api");
        psi.ArgumentList.Add($"http://127.0.0.1:{apiPort}");
        if (vr) psi.ArgumentList.Add("--vr");
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add(mode);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["SOJOURN_SIDECAR_TOKEN"] = Token;
        if (devToken != null)
        {
            psi.ArgumentList.Add("--dev");
            psi.Environment["SOJOURN_DEV_TOKEN"] = devToken;
        }
        else psi.Environment.Remove("SOJOURN_DEV_TOKEN");
        var job = CreateKillOnCloseJob();
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start the assistant");
        if (job != 0) AssignProcessToJobObject(job, proc.Handle);
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        proc.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) log.LogInformation("assistant: {Line}", l); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) log.LogInformation("assistant: {Line}", l); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        log.LogInformation("assistant: started (pid {Pid})", proc.Id);
        return new Sidecar(proc, job);
    }

    /// <summary>The repo the assistant lives in (with assistant/main.py and src/), or null.</summary>
    public static string? Root => FindRoot();

    /// <summary>A packaged build's own assistant: assistant/ and an embedded Python (python/python.exe) beside the
    /// program (tools/access.py package), or null.</summary>
    public static string? Bundled { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "assistant", "main.py"))
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "python", "python.exe")) ? AppContext.BaseDirectory : null;

    /// <summary>The assistant's Python side is here, from the repo or bundled.</summary>
    public static bool Available => Root != null || Bundled != null;

    /// <summary>Where the sidecar keeps its things (its memory under assistant/home, scratch, settings): the repo's
    /// local/, or for a bundled one the app's data folder (the app sets <see cref="DataDir"/>; assistant/paths.py).</summary>
    public static string LocalDir => FindRoot() is { } root ? Path.Combine(root, "local")
        : DataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sojourn");

    /// <summary>The app's data folder, for a bundled sidecar (SessionHost sets it from its BaseDir).</summary>
    public static string? DataDir { get; set; }

    static string? FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "assistant", "main.py")) && Directory.Exists(Path.Combine(d.FullName, "src")))
                return d.FullName;
        return null;
    }

    public void Dispose()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        if (_job != 0) CloseHandle(_job);
        _proc.Dispose();
    }

    // --- a job that kills its processes when its last handle closes (ours, at exit or crash) -------------------------

    static nint CreateKillOnCloseJob()
    {
        var job = CreateJobObject(0, null);
        if (job == 0) return 0;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, 9, ptr, (uint)size)) { CloseHandle(job); return 0; }   // JobObjectExtendedLimitInformation
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint CreateJobObject(nint attrs, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
}
