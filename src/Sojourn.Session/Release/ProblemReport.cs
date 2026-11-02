using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sojourn.Session.Release;

/// <summary>
/// A report of a problem with the VR client, or of its crash, made fit to leave the PC: the session's log filtered as
/// Sojourn Access's Send a log filters it (LogShare.cs: nothing other people said or wrote, their names and keys
/// stand-ins), with the person's note and, for a crash, how the app ended and what the .NET runtime wrote as it died
/// (folded into the log before filtering, so the same filter covers it). Then sent to Studio Quire's relay (LogSend.cs):
/// with the person's Studio Quire token as their logs go, or without one to its anonymous report endpoint, within its
/// limits (the user's ask, 2026-11-01; tools/relay/README.md). Or, if they'd rather, saved as a text file for them to
/// attach to an issue on GitHub, the issues page opened. Only the person's own press sends or saves one (the launcher's
/// crash window or its "always" setting, the menu's about > report a problem, or the assistant's reportProblem op once
/// they confirm). Shared by the VR client
/// (ReleaseApi, the menu) and the launcher (CrashReport.cs), so it depends only on .NET and the two files beside it.
/// docs/client.md > Problem and crash reports. Written by Claude Opus 5.5, 2026-11-01.
/// </summary>
public static class ProblemReport
{
    public const string IssuesPage = "https://github.com/studio-quire/sojourn/issues";
    public const string App = "Sojourn";

    /// <summary>How a run ended when it crashed: its exit code, and the runtime's last words (an unhandled exception or
    /// a fatal error, with its stack), if any.</summary>
    public sealed record Crash(int ExitCode, string? Words);

    /// <summary>A report ready to go: the filtered log (with the crash folded in), and what goes beside it.</summary>
    public sealed record Report(LogShare.Session Session, LogShare.Result Log, string Note, Crash? Crash, string Version, string? Commit, bool Current)
    {
        public string Kind => Crash != null ? "crash" : "problem";
    }

    /// <summary>The session's log filtered, with the crash's words at its end as plain lines (masked and checked for
    /// others' words like any line of the app's log). Null with why: a log from before the records can't be filtered,
    /// or it couldn't be read.</summary>
    public static Report? Make(LogShare.Session s, string note, Crash? crash, string version, string? commit, bool current, out string? why)
    {
        why = null;
        string log;
        try { log = LogShare.Read(s.Path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = $"the log couldn't be read ({e.Message})"; return null; }
        if (crash != null)
        {
            var sb = new StringBuilder(log.TrimEnd('\r', '\n')).Append('\n');
            sb.Append($"{DateTime.Now:HH:mm:ss} report: the app stopped unexpectedly, {ExitWords(crash.ExitCode)}\n");
            if (crash.Words is { Length: > 0 } w) sb.Append($"{DateTime.Now:HH:mm:ss} report: the .NET runtime said:\n").Append(w).Append('\n');
            log = sb.ToString();
        }
        var r = LogShare.Filter(log, s.Started, App);
        if (r is null) { why = "that log is from an older version and can't be filtered, so it can't be sent"; return null; }
        return new Report(s, r, note.Trim(), crash, version, commit, current);
    }

    /// <summary>The newest session log that can be sent (the crashed run's, or this one's), or null.</summary>
    public static LogShare.Session? Latest(string baseDir, string? path = null)
    {
        var all = LogShare.Sessions(Path.Combine(baseDir, "logs"));
        if (path != null && all.FirstOrDefault(x => string.Equals(Path.GetFullPath(x.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) is { Sendable: true } that)
            return that;
        return all.FirstOrDefault(x => x.Sendable);
    }

    /// <summary>The words the summary shows before sending (for the launcher's window and the menu's page): what goes
    /// and where. In English, each whole as a catalog key (Lang/launcher.json): translated where they're shown.</summary>
    public static string WhatsSent(bool token) => token
        ? "Sent to Studio Quire through its relay, relay.studioquire.org, with your token: your note and the session's log, "
          + "which keeps what Sojourn did, your own chat, objects' lines, the assistant's replies, errors and timings. "
          + "Everything other people said or wrote is left out, and their names become person 1, person 2 and so on; yours "
          + "becomes me. Studio Quire keeps it for 30 days."
        : "Sent to Studio Quire through its relay, relay.studioquire.org: your note and the session's log, which keeps what "
          + "Sojourn did, your own chat, objects' lines, the assistant's replies, errors and timings. Everything other people "
          + "said or wrote is left out, and their names become person 1, person 2 and so on; yours becomes me. Studio Quire "
          + "keeps it for 30 days, and doesn't keep your IP address.";

    /// <summary>The other way, for the menu and the launcher's window.</summary>
    public const string GitHubWords = "Or save it as a file to attach to an issue on GitHub yourself; issues are public.";

    /// <summary>The report as the relay keeps it (tools/relay/README.md: the fields Access sends, and kind, exit_code).</summary>
    public static (byte[] Body, bool Cut) Pack(Report r) =>
        LogSend.Pack(r.Log.Text, text =>
        {
            var o = new JsonObject
            {
                ["v"] = 1, ["app"] = $"{App} {r.Version}", ["commit"] = r.Commit, ["kind"] = r.Kind,
                ["session"] = r.Session.Started.ToString("yyyy-MM-dd HH:mm:ss"), ["current"] = r.Current,
                ["note"] = r.Note, ["lines"] = r.Log.Lines, ["left_out"] = r.Log.LeftOutLines, ["log"] = text,
            };
            if (r.Crash is { } c) o["exit_code"] = $"0x{c.ExitCode:X8}";
            return o;
        });

    /// <summary>Sends it, with the token or (null) without; the outcome in a sentence for the person, and whether it went.</summary>
    public static async Task<(bool Sent, string Said)> SendAsync(Report r, string? token, CancellationToken ct = default)
    {
        var (body, cut) = Pack(r);
        var res = await LogSend.PostAsync(body, token, $"{App}/{r.Version}", ct);
        if (res.Unreached is { } u) return (false, $"Not sent: Studio Quire's relay couldn't be reached ({u}).");
        if (res.Ok) return (true, "Report sent to Studio Quire. Thank you." + (cut ? " The log was long, so part of its middle was left out." : ""));
        return (false, res.Status switch
        {
            401 => "Studio Quire's relay didn't take your token. Nothing was sent.",
            429 => res.Message is { } m && m.Contains("GitHub") ? m : (res.Message ?? "Too many reports sent today. Try again tomorrow.") + " You can save it for GitHub instead.",
            _ => $"Not sent: {res.Message ?? $"the relay answered {res.Status}"}.",
        });
    }

    /// <summary>The report as a text file for an issue on GitHub, in the base dir's reports/ (the newest ten kept); its path.</summary>
    public static string Save(Report r, string baseDir)
    {
        var dir = Path.Combine(baseDir, "reports");
        Directory.CreateDirectory(dir);
        foreach (var old in new DirectoryInfo(dir).GetFiles("sojourn-*.txt").OrderByDescending(f => f.Name).Skip(9))
            try { old.Delete(); } catch (IOException) { }
        var path = Path.Combine(dir, $"sojourn-{r.Kind}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var sb = new StringBuilder();
        sb.Append($"{App} {r.Version}{(r.Commit != null ? $" (commit {r.Commit})" : "")}, a {r.Kind} report made {DateTime.Now:yyyy-MM-dd HH:mm}\n");
        sb.Append($"Windows {Environment.OSVersion.Version}\n");
        if (r.Crash is { } c) sb.Append($"How it ended: {ExitWords(c.ExitCode)}\n");
        sb.Append($"What happened: {(r.Note.Length > 0 ? r.Note : "(no note)")}\n\n");
        sb.Append(r.Log.Text);
        File.WriteAllText(path, sb.ToString().Replace("\n", "\r\n"), new UTF8Encoding(false));
        return path;
    }

    /// <summary>The saved file shown in Explorer and the issues page opened, for the person to attach it there.</summary>
    public static void OpenForGitHub(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); } catch (Exception) { }
        try { Process.Start(new ProcessStartInfo(IssuesPage + "/new") { UseShellExecute = true }); } catch (Exception) { }
    }

    // the runtime's last words: "Unhandled exception. ..." or "Fatal error." and the frames under them
    static readonly Regex DeathStart = new(@"^(Unhandled exception\.|Fatal error\.)", RegexOptions.Compiled);
    static readonly Regex DeathMore = new(@"^(\s+|---|0x[0-9A-Fa-f]+$|Exception_|Stack:)", RegexOptions.Compiled);

    /// <summary>What the .NET runtime wrote as the process died, from its console (the launcher's copy): the last
    /// "Unhandled exception." or "Fatal error." and the lines of its stack, at most 150; null if there's none.</summary>
    public static string? DeathWords(IEnumerable<string> console)
    {
        List<string>? last = null, now = null;
        foreach (var line in console)
        {
            if (DeathStart.IsMatch(line)) { now = new List<string> { line }; last = now; }
            else if (now != null && now.Count < 150 && DeathMore.IsMatch(line)) now.Add(line);
            else now = null;
        }
        return last is null ? null : string.Join('\n', last);
    }

    /// <summary>An exit code in words: 0xC0000005 is an access violation, and so on (English, for logs and reports).</summary>
    public static string ExitWords(int code) =>
        $"exit code {ExitHex(code)}" + (ExitName(code) is { } name ? $", {name}" : "");

    public static string ExitHex(int code) => $"0x{unchecked((uint)code):X8}";

    /// <summary>What an exit code means, in English (a catalog key where it's shown), or null for one without a name.</summary>
    public static string? ExitName(int code)
    {
        uint u = unchecked((uint)code);
        return u switch
        {
            0xC0000005 => "an access violation",
            0xC00000FD => "a stack overflow",
            0xC0000409 => "a fail fast (a security check or an abort)",
            0xC0000374 => "heap corruption",
            0xC000001D => "an illegal instruction",
            0xE0434352 => "an unhandled .NET exception",
            0x80000003 => "a breakpoint",
            0xC0000142 => "a DLL that failed to start",
            75 => "the graphics card reset (Sojourn's own exit code for a lost GPU)",
            _ => null,
        };
    }

    /// <summary>A build's commit (short), from its informational version (the SDK adds +hash), or null.</summary>
    public static string? CommitOf(Assembly asm)
    {
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int plus = info?.IndexOf('+') ?? -1;
        var c = plus >= 0 ? info![(plus + 1)..] : null;
        if (c is { Length: 40 }) c = c[..7];
        return string.IsNullOrEmpty(c) ? null : c;
    }
}
