using System.Diagnostics;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Access;

namespace Sojourn.Access;

/// <summary>
/// Chat logs and a conversation's earlier lines (docs/access-app.md > Chat logs). A tab opened again starts with its last
/// lines: from the log, or with logs off from this session's memory, since closing a conversation used to lose it. Nothing
/// from a spell of incognito is written or remembered, nor anything with someone she's marked always incognito, nor an IM
/// at all while IMs are incognito: for those the spell may only begin once a line has arrived.
/// </summary>
public sealed partial class MainForm
{
    ChatLogs? _logs;
    const int EarlierInTab = 60;
    const int RememberedLines = 200;
    readonly Dictionary<string, List<string>> _remembered = new();
    bool _logFailed;

    void StartChatLogs(string gridKey, string first, string last)
    {
        _logs = new ChatLogs(Path.Combine(_opt.BaseDir, "chat-logs", gridKey, $"{first}.{last}".ToLowerInvariant()))
        {
            Failed = e =>
            {
                if (_logFailed) return;
                _logFailed = true;
                Reader?.Answer($"Chat logs can't be written: {e.Message}", keep: false);
            },
        };
        Host!.Session.Api.Register(new ChatLogApi(Host.Session, _logs));
    }

    /// <summary>A line for the log and the session's memory, unless it's incognito's to forget.</summary>
    /// <param name="spell">Incognito was on as it arrived.</param>
    /// <param name="other">Who a one-to-one IM is with; zero otherwise.</param>
    void Keep(string key, string title, string line, bool spell, bool im = false, UUID other = default)
    {
        if (spell || Host?.Session.Private.On == true) return;
        if (other != UUID.Zero && _triggers?.With(other) == true) return;
        if (im && _triggers?.WhileIm == true) return;
        if (!_remembered.TryGetValue(key, out var lines)) _remembered[key] = lines = new();
        lines.Add($"[{DateTime.Now:HH:mm}] {line}");
        if (lines.Count > RememberedLines) lines.RemoveAt(0);
        if (_logs == null || _set.ChatLogs == "off" || (key == ChatLogs.NearbyKey && _set.ChatLogs != "all")) return;
        _logs.Add(key, title, line);   // (written on the logs' own thread; a failure comes back by Failed, said once)
    }

    /// <summary>A new tab's history starts with the conversation's last lines, the caret after them, where new ones arrive.</summary>
    void FillEarlier(Conversation cv)
    {
        // (the log's lines if the disk gives them at once, else this session's)
        var lines = (_logs != null && _set.ChatLogs != "off" ? _logs.Tail(cv.SessionId, EarlierInTab) : null)
            ?? (_remembered.TryGetValue(cv.SessionId, out var kept) ? kept : new());
        if (lines.Count == 0) return;
        cv.History.Text = FriendlyLinks(string.Join("\r\n", lines)) + "\r\n(Those are earlier lines. New ones follow.)";
        cv.History.SelectionStart = cv.History.TextLength;
    }

    /// <summary>Once, for anyone: that logs are being kept, where to read them and how to stop it.</summary>
    async Task SayChatLogsOnceAsync()
    {
        if (_set.ChatLogsTold) return;
        await Task.Delay(TimeSpan.FromSeconds(12));   // (after what comes with arriving)
        if (IsDisposed || _set.ChatLogs == "off") return;
        _set.ChatLogsTold = true;
        _set.Save();
        Reader?.Say(AnnounceKind.Notices, "Your chat and IMs are kept in logs on this PC, as Radegast's are, and never sent anywhere: Read-out, Chat logs, reads them. Alt+0's Chat group turns them off.", from: LineFrom.App);
    }

    /// <summary>Read-out > Chat logs, //chatlog: the logs, the most recent first; Enter reads one, Delete asks in Notices.</summary>
    void ShowChatLogs(string? words = null)
    {
        if (_logs == null) { Answer("Not in world yet."); return; }
        var all = _logs.All();
        if (words is { Length: > 0 }) all = all.Where(l => l.Title.Contains(words, StringComparison.OrdinalIgnoreCase)).ToList();
        if (all.Count == 0)
        {
            Answer(words is { Length: > 0 } ? $"No chat log with {words} in its name." : _set.ChatLogs == "off" ? "No chat logs: they're turned off, in Alt+0's Chat group." : "No chat logs yet.");
            return;
        }
        if (words is { Length: > 0 } && all.Count == 1) { OpenChatLog(all[0]); return; }
        using var form = new Form
        {
            Name = "chatLogs", Text = "Chat logs", AccessibleName = "Chat logs", Size = new Size(560, 420),
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false,
        };
        var list = new ListBox { Name = "chatLogsList", AccessibleName = "Chat logs, the most recent first. Enter reads one.", Dock = DockStyle.Fill };
        list.Items.AddRange(all.Select(l => (object)$"{l.Title}, {WhenWords(l.Last)}").ToArray());
        list.SelectedIndex = 0;
        var open = new Button { Name = "chatLogsOpen", Text = "&Read", AutoSize = true, DialogResult = DialogResult.OK };
        var close = new Button { Name = "chatLogsClose", Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var folder = new Button { Name = "chatLogsFolder", Text = "Open the &folder", AutoSize = true };
        var delete = new Button { Name = "chatLogsDelete", Text = "&Delete...", AutoSize = true };
        ChatLogs.Log? toDelete = null;
        void AskDelete()
        {
            if (list.SelectedIndex < 0) return;
            toDelete = all[list.SelectedIndex];
            form.DialogResult = DialogResult.Abort;
        }
        delete.Click += (_, _) => AskDelete();
        list.KeyDown += (_, e) => { if (e.KeyData == Keys.Delete) { e.Handled = e.SuppressKeyPress = true; AskDelete(); } };
        folder.Click += (_, _) =>
        {
            Reader?.Answer($"The folder: {_logs.Dir}", keep: false);
            if (Environment.GetEnvironmentVariable("SOJOURN_NO_BROWSER") == "1") return;
            try { Process.Start(new ProcessStartInfo(_logs.Dir) { UseShellExecute = true }); }
            catch (Exception x) when (x is System.ComponentModel.Win32Exception or InvalidOperationException) { Reader?.Answer($"Couldn't open it: {x.Message}", keep: false); }
        };
        form.AcceptButton = open;
        form.CancelButton = close;
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        // (Delete last, past Close: it asks in Notices all the same)
        row.Controls.AddRange(new Control[] { open, close, folder, delete });
        form.Controls.Add(list);
        form.Controls.Add(row);
        form.ActiveControl = list;
        var said = form.ShowDialog(this);
        if (said == DialogResult.OK && list.SelectedIndex >= 0) OpenChatLog(all[list.SelectedIndex]);
        else if (said == DialogResult.Abort && toDelete is { } gone) _ = DeleteChatLogAsync(gone);
    }

    static string WhenWords(DateTime t) => t.Date == DateTime.Today ? $"today at {t:HH:mm}"
        : t.Date == DateTime.Today.AddDays(-1) ? $"yesterday at {t:HH:mm}" : $"{t:d MMMM} at {t:HH:mm}";

    /// <summary>A conversation's log by its session, from a person's or a group's menu.</summary>
    void ChatLogOf(string key, string name)
    {
        if (_logs?.Of(key) is { } log) OpenChatLog(log);
        else Answer(_set.ChatLogs == "off" ? "Chat logs are turned off, in Alt+0's Chat group." : $"No chat log of {name} yet.");
    }

    /// <summary>A log in a read-only window, as a notecard's (find, go to a line, a line's link), the caret at the newest line.</summary>
    void OpenChatLog(ChatLogs.Log log)
    {
        string text;
        try { text = ChatLogs.Read(log.Path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Answer($"Couldn't read it: {e.Message}"); return; }
        var form = new EditorForm("Chat log", log.Title, FriendlyLinks(text), _ => Task.FromResult<(string, string?, string?)>(("", null, null)), t => Reader?.Answer(t, keep: false, from: LineFrom.People), readOnly: true);
        form.OpenFirstLink = OpenFirstLink;
        form.FormClosed += (_, _) => { if (!IsDisposed) Activate(); };
        form.Shown += (_, _) => Reader?.Answer($"Chat log: {log.Title}, at its newest line. F1 for its keys.", keep: false);
        form.Show(this);
    }

    async Task DeleteChatLogAsync(ChatLogs.Log log)
    {
        var args = new JsonObject { ["file"] = Path.GetFileName(log.Path), ["title"] = log.Title };
        if (await ConfirmedAsync("SojournChatLogs", "delete", args) is not { } r) return;
        Answer(PersonOps.Error(r) is { } e ? $"Still there: {e}" : $"Deleted the chat log of {log.Title}.");
    }
}
