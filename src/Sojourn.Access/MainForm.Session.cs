using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Access;
using Sojourn.Session;

namespace Sojourn.Access;

/// <summary>
/// The session itself, as Radegast's File and Tools menus have it (docs/access-gaps.md, item 21): a session's log to read
/// (Help > Read a session's log..., `//log`), the cache cleared at the next start (Help > Clear the cache at the next
/// start, `//clearcache`), and another account in a window of its own (World > Another account, in a new window...,
/// `//newwindow`). Written by Claude Opus 5.5, 2026-11-08.
/// </summary>
public sealed partial class MainForm
{
    void StartSessionTools()
    {
        _cmd.SessionLog = () => BeginInvoke(() => RealInput.Ours(ShowSessionLogs));
        _cmd.ClearCache = words => BeginInvoke(() => _ = ClearCacheWordsAsync(words));
        _cmd.NewWindow = () => BeginInvoke(NewWindow);
    }

    // --- a session's log -------------------------------------------------------------------------------------------------

    /// <summary>The sessions' logs, this one first, then earlier ones by day and time (the one before a crash is there);
    /// Enter reads one. Only one: read at once.</summary>
    void ShowSessionLogs()
    {
        var all = LogShare.Sessions(Path.Combine(_opt.BaseDir, "logs")).Where(s => s.Sendable).ToList();
        if (all.Count == 0) { Answer("There's no session log this version can show yet."); return; }
        var current = Host?.SessionLogPath is { } p ? Path.GetFullPath(p) : null;
        if (all.Count == 1) { _ = OpenSessionLogAsync(all[0], current); return; }
        using var form = new Form
        {
            Name = "sessionLogs", Text = "Session logs", AccessibleName = "Session logs", Size = new Size(480, 380),
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false,
        };
        var list = new ListBox { Name = "sessionLogsList", AccessibleName = "Session logs, this one first. Enter reads one.", Dock = DockStyle.Fill };
        list.Items.AddRange(all.Select(s => (object)SessionWords(s, current)).ToArray());
        list.SelectedIndex = 0;
        var read = new Button { Name = "sessionLogsRead", Text = "&Read", AutoSize = true, DialogResult = DialogResult.OK };
        var close = new Button { Name = "sessionLogsClose", Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        form.AcceptButton = read;
        form.CancelButton = close;
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        row.Controls.AddRange(new Control[] { read, close });
        form.Controls.Add(list);
        form.Controls.Add(row);
        form.ActiveControl = list;
        if (form.ShowDialog(this) == DialogResult.OK && list.SelectedIndex >= 0) _ = OpenSessionLogAsync(all[list.SelectedIndex], current);
    }

    static string SessionWords(LogShare.Session s, string? current) =>
        current != null && string.Equals(Path.GetFullPath(s.Path), current, StringComparison.OrdinalIgnoreCase) ? $"This session, started at {s.Started:HH:mm}"
        : s.Started.Date == DateTime.Today ? $"Today at {s.Started:HH:mm}"
        : s.Started.Date == DateTime.Today.AddDays(-1) ? $"Yesterday at {s.Started:HH:mm}"
        : $"{s.Started:dddd d MMMM}, at {s.Started:HH:mm}";

    /// <summary>The log as Send a log would send it (LogShare: records in words, other people's words left out and their
    /// names as person 1, person 2), in a read-only window as a chat log's, at its newest line: what happened, readable,
    /// and exactly what she'd be sending. The raw file is records of JSON, not for reading.</summary>
    async Task OpenSessionLogAsync(LogShare.Session s, string? current)
    {
        var words = SessionWords(s, current);
        LogShare.Result? r;
        try { r = await Task.Run(() => LogShare.Filter(LogShare.Read(s.Path), s.Started)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Answer($"That log couldn't be read: {e.Message}"); return; }
        if (r == null) { Answer("That log is from an older version and can't be shown."); return; }
        var form = new EditorForm("Session log", words, r.Text, _ => Task.FromResult<(string, string?, string?)>(("", null, null)), t => Reader?.Answer(t, keep: false), readOnly: true);
        form.FormClosed += (_, _) => { if (!IsDisposed) Activate(); };
        form.Shown += (_, _) => Reader?.Answer($"Session log, {words.ToLowerInvariant()}, as Send a log would send it: other people's words left out, "
                                               + $"{r.Lines} lines. At its newest line. F1 for its keys.", keep: false);
        form.Show(this);
    }

    // --- the cache ---------------------------------------------------------------------------------------------------------

    /// <summary>Help > Clear the cache at the next start, `//clearcache` (`cancel` to take it back): marked, said with what
    /// goes. Cleared as the app next starts, before anything uses it, as the official viewer and Firestorm do: while
    /// logged in its files are in use, and what's already loaded would stay in memory anyway.</summary>
    async Task ClearCacheWordsAsync(string words)
    {
        var w = words.Trim().ToLowerInvariant();
        bool marked = CacheClear.Marked(_opt.BaseDir);
        if (w is "cancel" or "no" or "off" || (w.Length == 0 && marked))
        {
            if (!marked) { Answer("The cache isn't marked to be cleared."); return; }
            CacheClear.Unmark(_opt.BaseDir);
            Answer("The cache won't be cleared after all.");
            return;
        }
        if (marked) { Answer("The cache is already marked to be cleared at the next start. //clearcache cancel takes that back."); return; }
        var bytes = await Task.Run(() => CacheClear.Size(_opt.BaseDir));
        try { CacheClear.Mark(_opt.BaseDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Answer($"Couldn't mark it: {e.Message}"); return; }
        Answer($"The cache will be cleared when Sojourn Access next starts: {CacheClear.Words(bytes)} of what Second Life sent, "
               + "pictures, sounds and models, which come again as they're needed, so places fill in slower at first. "
               + "Your settings, logins, chat logs and inventory stay. World's Log out, back to the login window, starts it again now; "
               + "clearing the cache again, in Help or by //clearcache, takes this back.");
    }

    /// <summary>After arriving, once: what the start's clearing took (CacheClear, begun by Program).</summary>
    async Task SayCacheClearedAsync()
    {
        if (CacheClear.Running is not { } running) return;
        var (bytes, left) = await running;
        CacheClear.Running = null;
        await Task.Delay(TimeSpan.FromSeconds(3));   // (after the arrival's own lines)
        if (IsDisposed) return;
        Host?.Log.LogInformation("cache: cleared {Bytes} bytes at the start, {Left} files left in use", bytes, left);
        Reader?.Say(AnnounceKind.Notices, $"The cache was cleared as Sojourn Access started: {CacheClear.Words(bytes)}."
            + (left > 0 ? $" {left} {(left == 1 ? "file was" : "files were")} in use, probably by another window of Sojourn Access, and stay." : ""), from: LineFrom.App);
    }

    // --- another account -----------------------------------------------------------------------------------------------

    /// <summary>World > Another account, in a new window..., `//newwindow`: this program again, at the login window with the
    /// username empty, as Radegast's New window. Its own process, so each account has its own session, sounds and screen
    /// reader lines; the settings, logs and cache are the same folder's. Its agent API on a port of its own (the assistant's
    /// way in), since this one's is taken.</summary>
    void NewWindow()
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        // run by dotnet (in the repo) the program is the dll; a packaged build is its own exe
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) is "dotnet")
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SojournAccess.dll"));
        for (int i = 0; i < _args.Length; i++)
        {
            if (_args[i] is "--grid" or "--user" or "--password" or "--start" or "--port") { i++; continue; }
            if (_args[i] == "--autologin") continue;
            psi.ArgumentList.Add(_args[i]);
        }
        if (Host?.Options.LoginUri is { Length: > 0 } grid) { psi.ArgumentList.Add("--grid"); psi.ArgumentList.Add(grid); }
        psi.ArgumentList.Add("--user");
        psi.ArgumentList.Add("");
        psi.ArgumentList.Add("--start");   // (not this account's last start, which may be one of its favourites)
        psi.ArgumentList.Add("last");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            var p = Process.Start(psi);
            Host?.Log.LogInformation("access: a new window for another account (pid {Pid})", p?.Id);
            Answer("A new window is opening at the login, for another account. Each window is its own session; Alt+Tab goes between them.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Answer($"Couldn't open a new window: {e.Message}"); }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}

/// <summary>
/// The cache cleared at a start (Help > Clear the cache at the next start): every grid's folder under the data folder's
/// cache/, what Second Life sent (pictures, sounds, models, animations, notecards and scripts read, partial downloads) and
/// what's made from it here (client-side bakes on grids without server baking). Not the compiled drawing code
/// (cache/render): it's built for this PC, not sent by anyone, and on a PC drawing on the processor it takes minutes to build
/// again. Nor the local grid's record of its test kit (starter.json). A file still in use (another window running) stays.
/// </summary>
static class CacheClear
{
    static string MarkerOf(string baseDir) => Path.Combine(baseDir, "clear-cache-at-start");
    public static bool Marked(string baseDir) => File.Exists(MarkerOf(baseDir));
    public static void Mark(string baseDir) => File.WriteAllText(MarkerOf(baseDir), $"asked {DateTime.Now:yyyy-MM-dd HH:mm}\n");
    public static void Unmark(string baseDir) => File.Delete(MarkerOf(baseDir));

    /// <summary>This start's clearing, while it runs and until it's been said: bytes cleared, files left in use.</summary>
    public static Task<(long Bytes, int Left)>? Running;

    /// <summary>At the start, before the login form: cleared in the background if marked (the session waits for it).</summary>
    public static void StartIfMarked(string baseDir)
    {
        if (!Marked(baseDir)) return;
        try { Unmark(baseDir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Running = Task.Run(() => Clear(baseDir));
    }

    static IEnumerable<FileInfo> Files(string baseDir)
    {
        var root = new DirectoryInfo(Path.Combine(baseDir, "cache"));
        if (!root.Exists) yield break;
        foreach (var grid in root.EnumerateDirectories())
        {
            if (grid.Name.Equals("render", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var f in grid.EnumerateFiles("*", SearchOption.AllDirectories))
                if (!f.Name.Equals("starter.json", StringComparison.OrdinalIgnoreCase)) yield return f;
        }
    }

    public static long Size(string baseDir)
    {
        try { return Files(baseDir).Sum(f => { try { return f.Length; } catch (IOException) { return 0L; } }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    static (long Bytes, int Left) Clear(string baseDir)
    {
        long bytes = 0;
        int left = 0;
        List<FileInfo> files;
        try { files = Files(baseDir).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (0, 0); }
        foreach (var f in files)
        {
            try
            {
                long n = f.Length;
                f.Delete();
                bytes += n;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left++; }
        }
        return (bytes, left);
    }

    /// <summary>"1.1 GB", "340 MB", "12 KB".</summary>
    public static string Words(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.#} GB"),
        >= 1L << 20 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0} MB"),
        >= 1L << 10 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
        _ => bytes == 0 ? "nothing" : $"{bytes} bytes",
    };
}
