using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace Sojourn.Client.Login;

/// <summary>One answer the launcher passed on: the grid's terms accepted or declined, or an MFA code.</summary>
public sealed record LauncherAnswer(string? TermsKind, bool Accept, string? Code, bool RememberComputer);

/// <summary>
/// The launcher's answers to the grid's questions (docs/launcher.md > The grid's questions at the desk): the Terms of
/// Service or a critical message accepted or declined, and the MFA code, given by the person at the launcher rather
/// than in the headset. The channel is an anonymous pipe the launcher made for this run and handed down as an
/// inherited handle (--launcher-answers H): only the launcher holds its other end. Here it's made non-inheritable at
/// once, so nothing this process starts (the assistant's sidecar and its Claude Code sessions, helpers) gets it; only a
/// restart of this client (a lost GPU, back to the login screen) is handed a copy, the moment before it starts.
/// The agent API and the assistant have no way in: an answer comes only from the launcher's window, from a press it
/// judged real (the launcher's RealPress). Codes are never logged. The client's own sheet still answers as before,
/// whichever comes first.
/// Written by Claude Opus 5.5, 2026-10-29.
/// </summary>
public static class LauncherAnswers
{
    static SafePipeHandle? _handle;
    static readonly ConcurrentQueue<LauncherAnswer> Queue = new();

    /// <summary>A launcher is listening for this run's questions (the state file says so, so it offers them at the desk).</summary>
    public static bool Open => _handle is { IsInvalid: false, IsClosed: false };

    /// <summary>At the very start (before anything is started from here): take the handle and read answers from it.</summary>
    public static void Start(string? handle)
    {
        if (handle == null || !long.TryParse(handle, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) || h == 0) return;
        var safe = new SafePipeHandle(new IntPtr(h), ownsHandle: true);
        if (safe.IsInvalid) return;
        // children don't get it
        if (OperatingSystem.IsWindows() && !SetHandleInformation(safe, HANDLE_FLAG_INHERIT, 0)) { safe.Dispose(); return; }
        _handle = safe;
        var t = new Thread(() => Read(safe)) { IsBackground = true, Name = "launcher answers" };
        t.Start();
    }

    static void Read(SafePipeHandle handle)
    {
        try
        {
            using var pipe = new AnonymousPipeClientStream(PipeDirection.In, handle);
            using var reader = new StreamReader(pipe);
            while (reader.ReadLine() is { } line)
                if (Parse(line) is { } a) Queue.Enqueue(a);
        }
        catch (Exception) { }   // the launcher closed: the client's own sheet carries on
    }

    static LauncherAnswer? Parse(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return null;
            if (o["terms"] is JsonValue t && t.TryGetValue(out string? kind) && kind is "tos" or "critical")
                return new(kind, o["accept"] is JsonValue v && v.TryGetValue(out bool yes) && yes, null, false);
            if (o["code"] is JsonValue c && c.TryGetValue(out string? code) && code.Trim() is { Length: >= 6 and <= 8 } digits && digits.All(char.IsAsciiDigit))
                return new(null, false, digits, o["remember"] is JsonValue r && r.TryGetValue(out bool keep) && keep);
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>From the frame loop: answers that came in, to the login sheet (which takes them only for the question
    /// it's showing).</summary>
    public static void Apply(LoginSheet sheet)
    {
        while (Queue.TryDequeue(out var a)) sheet.FromLauncher(a);
    }

    /// <summary>A restart of this client: the arguments with the channel handed on (a copy the new process inherits), so
    /// its questions can be answered at the launcher too. Called just before the new process is started.</summary>
    public static void PassOn(List<string> args)
    {
        int i = args.IndexOf("--launcher-answers");
        if (i < 0) return;
        string? copy = null;
        if (Open && OperatingSystem.IsWindows())
        {
            var me = GetCurrentProcess();
            if (DuplicateHandle(me, _handle!.DangerousGetHandle(), me, out var dup, 0, true, DUPLICATE_SAME_ACCESS))
                copy = dup.ToInt64().ToString(CultureInfo.InvariantCulture);   // (left open: this process is ending)
        }
        if (copy != null && i + 1 < args.Count) args[i + 1] = copy;
        else args.RemoveRange(i, Math.Min(2, args.Count - i));
    }

    const uint HANDLE_FLAG_INHERIT = 1, DUPLICATE_SAME_ACCESS = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access,
        bool inherit, uint options);
}
