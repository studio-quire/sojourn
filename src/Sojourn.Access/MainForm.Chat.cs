using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Session.Social;

namespace Sojourn.Access;

/// <summary>Nearby chat and the conversations (IMs and group chat as tabs): read-only histories the screen reader can
/// review line by line, and real input lines. New conversations open as tabs that wait; the focus stays where it was.</summary>
public sealed partial class MainForm
{
    TabPage _chatPage = null!;
    TextBox _chatInput = null!, _chatHistory = null!;
    readonly List<string> _chatSent = new();
    int _chatRecall = -1;

    TabPage _conversationsPage = null!;
    TabControl _conversations = null!;
    TextBox _noConversations = null!;
    readonly Dictionary<string, Conversation> _convs = new();

    sealed class Conversation
    {
        public required string SessionId;
        public required string Title;
        public required bool Group;
        public UUID Other;
        public required TabPage Page;
        public required TextBox Input, History;
        public readonly List<string> Sent = new();
        public int Recall = -1;
    }

    static TextBox HistoryBox(string name, string accessibleName) => new()
    {
        Name = name, AccessibleName = accessibleName, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        WordWrap = true, Dock = DockStyle.Fill, MaxLength = 0, BackColor = SystemColors.Window, TabStop = true,
    };

    static TextBox InputBox(string name, string accessibleName) => new LineBox
    {
        Name = name, AccessibleName = accessibleName, Dock = DockStyle.Fill, MaxLength = 1023,
    };

    /// <summary>A page holding a history over its input line, with a label for each (the labels' mnemonics move to them):
    /// the history is one Shift+Tab back from the input, as in Radegast.</summary>
    static TableLayoutPanel HistoryOverInput(string inputLabel, TextBox input, string historyLabel, TextBox history)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var hl = new Label { Text = historyLabel, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top };
        var il = new Label { Text = inputLabel, AutoSize = true, Anchor = AnchorStyles.Left };
        t.Controls.Add(hl, 0, 0);
        t.Controls.Add(history, 1, 0);
        t.Controls.Add(il, 0, 1);
        t.Controls.Add(input, 1, 1);
        hl.TabIndex = 0;
        history.TabIndex = 1;
        il.TabIndex = 2;
        input.TabIndex = 3;
        return t;
    }

    void BuildChat()
    {
        _chatPage = new TabPage("Nearby chat") { Name = "chatPage", AccessibleName = "Nearby chat" };
        _chatInput = InputBox("chatInput", "Say");
        _chatHistory = HistoryBox("chatHistory", "Nearby chat history");
        _chatPage.Controls.Add(HistoryOverInput("Say", _chatInput, "History", _chatHistory));
        _chatInput.KeyDown += (_, e) => ChatKey(e);
    }

    void ChatKey(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = e.Handled = true;
            var text = _chatInput.Text;
            if (text.Trim().Length == 0) return;
            Remember(_chatSent, text);
            _chatRecall = -1;
            _chatInput.Clear();
            if (Commands.IsCommand(text)) { _ = _cmd.RunAsync(text); return; }
            var type = e.Control ? "shout" : e.Shift ? "whisper" : "normal";
            _ = SayAsync(text, type);
        }
        else if (e.Control && e.KeyCode is Keys.Up or Keys.Down)
        {
            e.SuppressKeyPress = e.Handled = true;
            Recall(_chatInput, _chatSent, ref _chatRecall, e.KeyCode == Keys.Up ? +1 : -1);
        }
    }

    async Task SayAsync(string text, string type)
    {
        var r = await _ops.DoAsync("LLChatBar", "sendChat", new JsonObject { ["message"] = text, ["type"] = type });
        if (PersonOps.Error(r) is { } e) Answer($"Not said: {e}");
    }

    static void Remember(List<string> sent, string text)
    {
        sent.Add(text);
        if (sent.Count > 50) sent.RemoveAt(0);
    }

    /// <summary>Ctrl+Up/Down: earlier lines the person sent, as Radegast's.</summary>
    static void Recall(TextBox box, List<string> sent, ref int at, int dir)
    {
        if (sent.Count == 0) return;
        at = Math.Clamp(at + dir, -1, sent.Count - 1);
        box.Text = at < 0 ? "" : sent[sent.Count - 1 - at];
        box.SelectionStart = box.TextLength;
    }

    void BuildConversations()
    {
        _conversationsPage = new TabPage("Conversations") { Name = "conversationsPage", AccessibleName = "Conversations" };
        _conversations = new TabControl { Name = "conversations", AccessibleName = "Conversations", Dock = DockStyle.Fill, TabStop = false };   // (Control+Tab switches)
        _noConversations = new TextBox
        {
            Name = "noConversations", AccessibleName = "Conversations", ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, BackColor = SystemColors.Window,
            Text = NoConversationsText,
        };
        _conversationsPage.Controls.Add(_conversations);
        _conversationsPage.Controls.Add(_noConversations);
        _conversations.Visible = false;
        _conversations.SelectedIndexChanged += (_, _) => FocusChanged();
        // kept while the person is on it when the first conversation opens (hiding it would throw the focus elsewhere)
        _noConversations.Leave += (_, _) => { if (_conversations.Visible) BeginInvoke(() => { if (!_noConversations.Focused) _noConversations.Visible = false; }); };
    }

    const string NoConversationsText = "No conversations yet. An IM from someone opens one here; in People nearby, the Applications key then Send IM starts one.";

    /// <summary>The conversation's tab, made if new: it waits; nothing moves the focus.</summary>
    Conversation Open(string sessionId, string title, bool group, UUID other)
    {
        if (_convs.TryGetValue(sessionId, out var cv)) return cv;
        var label = group ? $"{title} (group)" : title;
        var page = new TabPage(label) { Name = "conv:" + sessionId, AccessibleName = label };
        var input = InputBox("convInput", $"Message to {label}");
        var history = HistoryBox("convHistory", $"Conversation with {label}");
        page.Controls.Add(HistoryOverInput("Message", input, "History", history));
        cv = new Conversation { SessionId = sessionId, Title = label, Group = group, Other = other, Page = page, Input = input, History = history };
        page.Tag = cv;
        input.KeyDown += (_, e) => ConversationKey(cv, e);
        _convs[sessionId] = cv;
        // adding a tab keeps the one selected (and the focus wherever it is); the first one becomes the selected tab
        // of a control the person isn't in
        _conversations.TabPages.Add(page);
        if (!_conversations.Visible)
        {
            _conversations.Visible = true;
            // the person is reading the "no conversations" line: it stays, and says what came, until they leave it
            if (_noConversations.Focused)
            {
                _noConversations.Dock = DockStyle.Top;
                _noConversations.Height = 40;
                _noConversations.Text = $"A conversation opened: {label}. Control+Tab goes to it.";
            }
            else _noConversations.Visible = false;
        }
        return cv;
    }

    void ConversationKey(Conversation cv, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = e.Handled = true;
            var text = cv.Input.Text;
            if (text.Trim().Length == 0) return;
            Remember(cv.Sent, text);
            cv.Recall = -1;
            cv.Input.Clear();
            if (Commands.IsCommand(text)) { _ = _cmd.RunAsync(text); return; }
            _ = SendImAsync(cv, text);
        }
        else if (e.Control && e.KeyCode is Keys.Up or Keys.Down)
        {
            e.SuppressKeyPress = e.Handled = true;
            Recall(cv.Input, cv.Sent, ref cv.Recall, e.KeyCode == Keys.Up ? +1 : -1);
        }
    }

    async Task SendImAsync(Conversation cv, string text)
    {
        var args = new JsonObject { ["message"] = text };
        if (cv.Group) args["group_id"] = cv.SessionId;
        else if (cv.Other != UUID.Zero) args["to"] = cv.Other.ToString();
        else args["session_id"] = cv.SessionId;
        var r = await _ops.DoAsync("TesselIM", "send", args);
        if (PersonOps.Error(r) is { } e) Answer($"Not sent: {e}");
    }

    /// <summary>Starts (or goes to) an IM with someone: the person asked, so the focus goes to its input.</summary>
    void StartIm(UUID who, string name)
    {
        if (_ops.Client is not { } c) return;
        var session = (c.Self.AgentID ^ who).ToString();
        var cv = Open(session, name, group: false, who);
        _conversations.SelectedTab = cv.Page;
        GoArea(_conversationsPage);
    }

    void CloseConversation()
    {
        if (_areas.SelectedTab != _conversationsPage || _conversations.SelectedTab?.Tag is not Conversation cv) { Reader?.Answer("Not in a conversation."); return; }
        _convs.Remove(cv.SessionId);
        _conversations.TabPages.Remove(cv.Page);
        cv.Page.Dispose();
        _ = _ops.DoAsync("TesselIM", "leave", new JsonObject { ["session_id"] = cv.SessionId });
        if (_conversations.TabCount == 0)
        {
            _conversations.Visible = false;
            _noConversations.Text = NoConversationsText;
            _noConversations.Dock = DockStyle.Fill;
            _noConversations.Visible = true;
        }
        Reader?.Answer($"Closed {cv.Title}.");
        GoArea(_conversations.TabCount > 0 ? _conversationsPage : _chatPage);
    }

    // --- what arrives -----------------------------------------------------------------------------------------------

    /// <summary>A line or notice joined the feed (on the UI thread): into its history. Notices still waiting go to the
    /// notices area by themselves (PollNotices); one-off notices (alerts, friends online, a teleport that failed) are
    /// status lines in nearby chat, as in Radegast.</summary>
    void Joined(FeedEntry e)
    {
        switch (e.Kind)
        {
            case FeedKind.Nearby:
                AppendChat(HistoryLine(e, _set.Timestamps));
                break;
            case FeedKind.Im when e.Style == FeedStyle.Object || e.Session?.StartsWith("object:") == true:
                AppendChat($"{e.Name} (to you): {e.Text}");
                break;
            case FeedKind.Im or FeedKind.Group when e.Session != null:
                var cv = Open(e.Session, e.SessionName ?? e.Name, e.Kind == FeedKind.Group, e.Kind == FeedKind.Im && !e.Mine ? e.From : UUID.Zero);
                if (cv.Other == UUID.Zero && e.Kind == FeedKind.Im && !e.Mine) cv.Other = e.From;
                Append(cv.History, HistoryLine(e, _set.Timestamps));
                break;
            case FeedKind.Notice when e.NoticeId == Guid.Empty:
                AppendChat(e.Name.Length > 0 ? (e.Text.Length > 0 && char.IsLower(e.Text[0]) ? $"{e.Name} {e.Text}" : $"{e.Name}: {e.Text}") : e.Text);
                break;
        }
    }

    /// <summary>A line as the histories show it (and as a conversation in focus is read): who, how, the words.</summary>
    static string HistoryLine(FeedEntry e, bool stamp)
    {
        var name = e.Name.Length > 0 ? e.Name : "someone";
        var line = e.Style switch
        {
            FeedStyle.Emote => $"{name} {e.Text}",
            FeedStyle.Whisper => $"{name} whispers: {e.Text}",
            FeedStyle.Shout => $"{name} shouts: {e.Text}",
            _ => $"{name}: {e.Text}",
        };
        return stamp ? $"[{DateTime.Now:HH:mm}] {line}" : line;
    }

    void AppendChat(string line) => Append(_chatHistory, line);

    /// <summary>Adds a line at the end without moving the person's place: while they're reviewing the history, the
    /// caret and selection stay where they are (Radegast's history jumped to the bottom on every line).</summary>
    static void Append(TextBox box, string line)
    {
        if (box.IsDisposed) return;
        bool reviewing = box.Focused;
        int start = box.SelectionStart, length = box.SelectionLength;
        // keep the history to its last ~200,000 characters
        if (box.TextLength > 200_000)
        {
            var text = box.Text;
            int cut = text.IndexOf('\n', text.Length - 150_000) + 1;
            box.Text = text[cut..];
            start = Math.Max(0, start - cut);
        }
        box.AppendText((box.TextLength > 0 ? "\r\n" : "") + line);
        if (reviewing) box.Select(start, length);
    }

    void RefreshHistories() { }   // timestamps apply to new lines; the old ones stay as they were said
}
