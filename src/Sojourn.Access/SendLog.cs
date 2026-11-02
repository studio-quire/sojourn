using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sojourn.Session;
using Sojourn.Session.Assistant;

namespace Sojourn.Access;

/// <summary>
/// Help > Send a log (//sendlog): a session's log sent to Studio Quire through its relay with the person's Studio Quire
/// token, with a note on what happened, other people's words left out and their names made stand-ins
/// (Sojourn.Session/LogShare.cs; sent by LogSend.cs, which the VR client's reports share). A small window of standard
/// controls, opened by the person's own key, the focus in its note; what's sent can be read before sending; the outcome is said. Without a token it says that sending needs
/// one, and where problems can be reported instead. docs/access-app.md > Sending a log. Written by Claude Opus 5.5, 2026-10-31.
/// </summary>
public sealed partial class MainForm
{
    const string IssuesPage = "github.com/studio-quire/sojourn-access/issues";

    void SendLog()
    {
        if (ProviderKeys.RelayToken() is null)
        {
            Answer("Sending a log needs a token from Studio Quire, pasted into the key row at the top of Alt+0. "
                + $"Without one, problems can be reported on the issues page, {IssuesPage}.");
            return;
        }
        var all = LogShare.Sessions(Path.Combine(_opt.BaseDir, "logs"));
        var logs = all.Where(s => s.Sendable).ToList();
        if (logs.Count == 0) { Answer("There's no log this version can send yet."); return; }
        var current = Host?.SessionLogPath is { } p ? Path.GetFullPath(p) : null;
        using var f = new SendLogForm(logs, current, all.Count - logs.Count, Host?.Log);
        f.ShowDialog(this);
        if (f.Said is { } said) Answer(said);
    }
}

sealed class SendLogForm : Form
{
    readonly List<LogShare.Session> _logs;
    readonly string? _current;
    readonly ILogger? _log;
    readonly ComboBox _which;
    readonly TextBox _note;
    readonly Button _send, _review, _cancel;

    /// <summary>What to say once the window has closed (the outcome), or null.</summary>
    public string? Said { get; private set; }

    public SendLogForm(List<LogShare.Session> logs, string? current, int older, ILogger? log)
    {
        _logs = logs;
        _current = current;
        _log = log;
        Name = "sendLog";
        Text = AccessibleName = "Send a log to Studio Quire";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ShowInTaskbar = false;

        _note = new TextBox
        {
            Name = "sendLogNote", AccessibleName = "What happened", Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical,
            Width = 440, Height = 110, MaxLength = 4000,
            AccessibleDescription = "Optional: what you were doing and what went wrong. Other people's words are left out of the log.",
        };
        _which = new ComboBox { Name = "sendLogSession", AccessibleName = "Session", DropDownStyle = ComboBoxStyle.DropDownList, Width = 440 };
        foreach (var s in logs) _which.Items.Add(Words(s));
        _which.SelectedIndex = 0;
        var what = new TextBox
        {
            Name = "sendLogWhat", AccessibleName = "What's sent", Multiline = true, ReadOnly = true, Width = 440, Height = 120,
            BackColor = SystemColors.Window, ScrollBars = ScrollBars.Vertical,
            Text = "Sent to Studio Quire through its relay, with your token: the session's log and your note. "
                + "The log keeps what Sojourn Access did and read out to you, your own chat, what objects said, the assistant's replies, "
                + "errors and timings. It leaves out everything other people said or wrote, and their names become person 1, "
                + "person 2 and so on; yours becomes me. Studio Quire keeps it for 30 days."
                + (older > 0 ? " Logs from before this version can't be sent." : ""),
        };
        _review = new Button { Name = "sendLogReview", Text = "&Review what's sent", AutoSize = true };
        _send = new Button { Name = "sendLogSend", Text = "Se&nd", AutoSize = true };
        _cancel = new Button { Name = "sendLogCancel", Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        CancelButton = _cancel;
        AcceptButton = _send;   // Enter in the note stays a new line (AcceptsReturn)

        var t =new TableLayoutPanel { ColumnCount = 2, RowCount = 4, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
        t.Controls.Add(new Label { Text = "&What happened", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 0);
        t.Controls.Add(_note, 1, 0);
        t.Controls.Add(new Label { Text = "&Session", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        t.Controls.Add(_which, 1, 1);
        t.Controls.Add(new Label { Text = "What's sent", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 2);
        t.Controls.Add(what, 1, 2);
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        row.Controls.AddRange(new Control[] { _send, _review, _cancel });
        t.Controls.Add(row, 1, 3);
        Controls.Add(t);
        ActiveControl = _note;

        _review.Click += (_, _) => Review();
        _send.Click += async (_, _) => await SendAsync();
    }

    string Words(LogShare.Session s) =>
        _current != null && string.Equals(Path.GetFullPath(s.Path), _current, StringComparison.OrdinalIgnoreCase)
            ? $"This session, started at {s.Started:HH:mm}"
            : s.Started.Date == DateTime.Today ? $"Today at {s.Started:HH:mm}"
            : s.Started.Date == DateTime.Today.AddDays(-1) ? $"Yesterday at {s.Started:HH:mm}"
            : $"{s.Started:dddd d MMMM}, at {s.Started:HH:mm}";

    LogShare.Session Chosen => _logs[Math.Max(0, _which.SelectedIndex)];

    LogShare.Result? Filtered(out string? why)
    {
        why = null;
        try
        {
            var r = LogShare.Filter(LogShare.Read(Chosen.Path), Chosen.Started);
            if (r is null) why = "That log is from an older version and can't be sent.";
            return r;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = $"That log couldn't be read ({e.Message}).";
            return null;
        }
    }

    /// <summary>The filtered text in a read-only window, as F1's keys are shown; Escape closes it.</summary>
    void Review()
    {
        if (Filtered(out var why) is not { } r) { Speak(why!); return; }
        using var f = new Form
        {
            Name = "sendLogText", Text = "What's sent", AccessibleName = "What's sent", Size = new Size(720, 560),
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false,
        };
        var text = new TextBox
        {
            Name = "sendLogTextBox", AccessibleName = $"What's sent: {r.Lines} lines, {r.LeftOutLines} with words left out",
            // one log line a line (no wrapping), so the arrows read it line by line; no length limit (MaxLength 0)
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, MaxLength = 0,
            BackColor = SystemColors.Window, Text = r.Text.Replace("\n", "\r\n"),
        };
        var close = new Button { Name = "sendLogTextClose", Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        f.CancelButton = close;
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        row.Controls.Add(close);
        f.Controls.Add(text);
        f.Controls.Add(row);
        f.ActiveControl = text;
        f.Shown += (_, _) => { text.SelectionStart = 0; text.SelectionLength = 0; };
        f.ShowDialog(this);
    }

    bool _sending;

    async Task SendAsync()
    {
        if (_sending) return;
        if (Filtered(out var why) is not { } r) { Speak(why!); return; }
        if (ProviderKeys.RelayToken() is not { } token) { Speak("The Studio Quire token isn't kept any more: Alt+0's key row takes it."); return; }
        _sending = true;
        _send.Enabled = _review.Enabled = false;
        Speak("Sending.");
        try
        {
            var (body, cut) = Pack(r, _note.Text.Trim());
            var res = await LogSend.PostAsync(body, token, $"SojournAccess/{AppInfo.Version}");
            if (res.Unreached is { } unreached)
            {
                _log?.LogWarning("sendlog: {Msg}", unreached);
                Speak($"Not sent: Studio Quire's relay couldn't be reached ({unreached}).");
                return;
            }
            if (res.Ok)
            {
                _log?.LogInformation("sendlog: {File} sent ({Lines} lines, {Out} with words left out, {Kb} KB{Cut})", Path.GetFileName(Chosen.Path),
                    r.Lines, r.LeftOutLines, body.Length / 1024, cut ? ", cut in the middle to fit" : "");
                Said = "Log sent to Studio Quire. Thank you." + (cut ? " It was long, so part of the middle was left out." : "");
                DialogResult = DialogResult.OK;
                return;
            }
            var told = res.Message;
            _log?.LogWarning("sendlog: the relay answered {Status}: {Msg}", res.Status, told ?? "");
            Speak(res.Status switch
            {
                401 => "Studio Quire's relay didn't take your token. Nothing was sent.",
                429 => told ?? "Too many logs sent today. Try again tomorrow.",
                _ => $"Not sent: {told ?? $"the relay answered {res.Status}"}.",
            });
        }
        finally
        {
            _sending = false;
            if (!IsDisposed && DialogResult != DialogResult.OK) _send.Enabled = _review.Enabled = true;
        }
    }

    /// <summary>The note and the filtered log as gzipped JSON (LogSend.Pack: a log too long for the relay loses lines
    /// from its middle, saying so in the log).</summary>
    (byte[] Body, bool Cut) Pack(LogShare.Result r, string note) =>
        LogSend.Pack(r.Text, text => new JsonObject
        {
            ["v"] = 1, ["app"] = $"Sojourn Access {AppInfo.Version}", ["commit"] = AppInfo.Commit,
            ["session"] = Chosen.Started.ToString("yyyy-MM-dd HH:mm:ss"), ["current"] = Words(Chosen).StartsWith("This session"),
            ["note"] = note, ["lines"] = r.Lines, ["left_out"] = r.LeftOutLines, ["log"] = text,
        });

    /// <summary>Said through the person's screen reader (the main window's announcer, which reads over any window).</summary>
    void Speak(string text) => (Owner as MainForm)?.Answer(text);
}
