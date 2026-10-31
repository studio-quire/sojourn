using System.Text.Json.Nodes;
using Sojourn.Core;
using Sojourn.Core.Access;
using Sojourn.Session.Assistant;

namespace Sojourn.Access;

/// <summary>
/// Notices: offers, script menus, permission requests, invitations and llTextBox questions waiting for the person,
/// newest first, and a confirmation the assistant needs at the top. Each is a list item in words; its keys are real
/// buttons ("1: Red") and, on the list, the number keys press them (as A1's keys in the VR client) and Delete puts it
/// away. Nothing is answered without the person's own key (RealInput: a hardware press made after the selected notice
/// last changed by anything but them; with `access_answers_without_key` on hello, Windows Hello instead), and an
/// llTextBox reply sends only text the person's own keys typed, to the notice it was typed for. None of it takes the
/// focus: an arrival is said, and Ctrl+N goes there.
/// </summary>
public sealed partial class MainForm
{
    TabPage _noticesPage = null!;
    ListView _notices = null!;
    TextBox _noticeText = null!, _noticeReply = null!;
    FlowLayoutPanel _noticeButtons = null!;
    Button _noticeIgnore = null!;
    Label _replyLabel = null!;
    long _noticesVersion = -1;
    object? _confirmShown;
    string? _shownKeys;
    (string Object, long At)? _answeredMenu;
    object? _selectedWas;      // the selected notice's id, or the confirmation itself: what an answer acts on
    bool _filling;
    Guid? _replyFor;           // the notice the reply box's text is for
    bool _replyForeign;        // something other than the person's keys put text in the reply box
    PendingConfirm? _cancelledHere;   // the confirmation the person last cancelled here (its op's refusal goes unsaid)
    ContextMenuStrip _noticeMenu = null!;
    (Control Control, TabPage Page)? _noticesFrom;   // where Control+N came from: Escape goes back there

    /// <param name="Kind">the notification's name (ScriptDialog, ScriptQuestion, LoadWebPage ...); "" for a confirmation</param>
    sealed record Notice(Guid Id, PendingConfirm? Confirm, string Words, string Detail, List<(string Name, string Label)> Buttons, bool TextBox, string? ObjectId, string Kind)
    {
        /// <summary>A script's menu (or its text box): what paging by key follows from page to page.</summary>
        public bool Menu => Kind is "ScriptDialog" or "ScriptDialogGroup";
    }

    void BuildNotices()
    {
        _noticesPage = new TabPage("Notices") { Name = "noticesPage", AccessibleName = "Notices" };
        _notices = List("notices", "Notices waiting");
        _noticeText = HistoryBox("noticeText", "Notice");
        _noticeText.Height = 90;
        _noticeText.Dock = DockStyle.Fill;
        _noticeButtons = new FlowLayoutPanel { Name = "noticeButtons", AccessibleName = "Keys", AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        _replyLabel = new Label { Text = "Reply", AutoSize = true, Anchor = AnchorStyles.Left, Visible = false };
        _noticeReply = new LineBox { Name = "noticeReply", AccessibleName = "Reply", Dock = DockStyle.Fill, Visible = false, MaxLength = 511 };
        _noticeIgnore = new Button { Name = "noticeIgnore", Text = "Put away", AutoSize = true, Enabled = false };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = "Waiting", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 0);
        t.Controls.Add(_notices, 1, 0);
        t.Controls.Add(new Label { Text = "Notice", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 1);
        t.Controls.Add(_noticeText, 1, 1);
        t.Controls.Add(_replyLabel, 0, 2);
        t.Controls.Add(_noticeReply, 1, 2);
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        row.Controls.Add(_noticeButtons);
        row.Controls.Add(_noticeIgnore);
        t.Controls.Add(new Label { Text = "Keys", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        t.Controls.Add(row, 1, 3);
        _notices.TabIndex = 0; _noticeText.TabIndex = 1; _noticeReply.TabIndex = 2; row.TabIndex = 3;
        _noticesPage.Controls.Add(t);

        _notices.SelectedIndexChanged += (_, _) => { if (_filling) return; ShowNotice(); NoteSelection(); };
        _notices.KeyDown += (_, e) => NoticeKey(e);
        _noticeMenu = new ContextMenuStrip { Name = "noticeMenu", AccessibleName = "Keys" };
        _notices.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowNoticeMenu(); };
        _noticeIgnore.Click += (_, _) => PutAway(fromButton: true);
        _noticeReply.TextChanged += (_, _) =>
        {
            // empty is safe whoever made it; anything else is typed or pasted by the person's keys, or it's foreign
            if (_noticeReply.TextLength == 0) _replyForeign = false;
            else if (!RealInput.Person) _replyForeign = true;
        };
        _noticeReply.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            if (Selected() is { } n) Reply(n, _noticeReply.Text);
        };
        Fill(_notices, new(), "No notices waiting.");
    }

    Notice? Selected() => _notices.SelectedItems.Count > 0 ? _notices.SelectedItems[0].Tag as Notice : null;

    /// <summary>The selected notice changed (by the person's arrows, our own code, or a notice arriving): noted, with who
    /// changed it, for the answers' rule (RealInput).</summary>
    void NoteSelection()
    {
        var n = Selected();
        object? now = n == null ? null : n.Confirm ?? (object)n.Id;
        if (Equals(now, _selectedWas)) return;
        _selectedWas = now;
        RealInput.Changed();
    }

    /// <summary>What waits now, the confirmation first, then notices newest first.</summary>
    List<Notice> Waiting()
    {
        var s = Host!.Session;
        var list = new List<Notice>();
        if (Host.Assistant.Panel.Read(p => p.Confirm) is { } pc)
            list.Add(new Notice(Guid.Empty, pc, $"Confirm: {pc.Title.TrimEnd('.')}. {pc.Detail}".Trim(), pc.Detail,
                new() { ("confirm", "Confirm"), ("cancel", "Cancel") }, false, null, ""));
        foreach (var n in s.Notify.All.Reverse())
        {
            if (!Guid.TryParse(n.Str("id"), out var id) || !s.Notify.Answerable(id)) continue;
            var (who, what, _) = Host.Feed.NoticeLine(n);
            var words = Sentences(who.Length == 0 ? what : what.Length > 0 && char.IsLower(what[0]) ? $"{who} {what}" : $"{who}: {what}");
            var form = (n["form"] as JsonArray ?? new()).OfType<JsonObject>()
                .Select(b => (Name: b.Str("name") ?? "", Text: b.Str("text") ?? "", Index: b["index"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0))
                .Where(b => b.Name.Length > 0).ToList();
            bool textBox = form.Any(b => b.Name == "!!llTextBox!!");
            var buttons = form.Where(b => b.Index >= 0 && b.Name != "!!llTextBox!!" && !(n.Str("name") == "JoinGroup" && b.Name == "Info"))
                .Concat(form.Where(b => b.Name == "Client_Side_Mute"))
                .Select(b => (b.Name, b.Text.Length > 0 ? b.Text : b.Name)).ToList();
            var detail = Speakable(n.Str("message") ?? "");
            list.Add(new Notice(id, null, words, detail, buttons, textBox, (n["payload"] as JsonObject)?.Str("object_id"), n.Str("name") ?? ""));
        }
        return list;
    }

    static string Speakable(string s) => s.Replace("<nolink>", "").Replace("</nolink>", "").Replace("\r\n", "\n").Replace("\n", "\r\n");

    /// <summary>A notice's lines as one line of sentences (a list item shows only its first line): "would like to:
    /// Animate your avatar. Is this OK?"</summary>
    static string Sentences(string s)
    {
        var lines = s.Replace("<nolink>", "").Replace("</nolink>", "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0) sb.Append(sb[^1] is '.' or '?' or '!' or ':' or ',' or ';' ? " " : ". ");
            sb.Append(line);
        }
        // the radar's "came into chat range (12 m)" in Access's words for distances
        return ShortMetres().Replace(sb.ToString(), m => $", {Bearings.Metres(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))} away");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@" \((\d+) m\)")]
    private static partial System.Text.RegularExpressions.Regex ShortMetres();

    void PollNotices()
    {
        var pc = Host!.Assistant.Panel.Read(p => p.Confirm);
        long v = Host.Session.Notify.Version;
        if (v == _noticesVersion && pc == _confirmShown) return;
        _noticesVersion = v;
        _confirmShown = pc;
        pc?.MarkShown();   // shown to the person here: its keys arm from now (PendingConfirm.ArmMs)
        var waiting = Waiting();
        // the same notices as shown (an alert came and went): nothing changes under the person
        var shownKeys = string.Join(",", waiting.Select(n => n.Id));
        if (shownKeys == _shownKeys && pc == null) return;
        _shownKeys = shownKeys;
        var before = Selected();
        bool inList = _notices.Focused;
        _filling = true;
        try { Fill(_notices, waiting.Select(n => (n.Words + KeyWords(n), n.Id == Guid.Empty ? "confirm" : n.Id.ToString(), (object)n)).ToList(), "No notices waiting."); }
        finally { _filling = false; }
        NoteSelection();
        // the next page of a menu the person just answered by key is picked by itself, so paging goes on (our own move for
        // them: their next key, pressed after it, answers it). Only another menu of that object's: anything else it sends
        // then (a web page, an item, a question) arrives as any notice does, its keys waiting out the moment after it
        // lands, so a key pressed twice can't answer what it never heard
        if (_answeredMenu is { } m && Environment.TickCount64 - m.At < 8000
            && waiting.FirstOrDefault(n => n.Menu && n.ObjectId == m.Object) is { } next)
        {
            _answeredMenu = null;
            var item = _notices.Items[next.Id.ToString()];
            if (item != null)
                RealInput.Ours(() =>
                {
                    item.Selected = true;
                    ReaderList.MakeCurrent(item);
                    if (_areas.SelectedTab == _noticesPage) _notices.Focus();
                    NoteSelection();
                });
        }
        // (none left: the list's own line, "No notices waiting.", is what the screen reader reads)
        else if (before != null && _notices.Items[before.Id == Guid.Empty ? "confirm" : before.Id.ToString()] == null && inList && waiting.Count > 0)
            Reader?.Answer($"{waiting.Count} waiting.", keep: false);
        ShowNotice();
        _noticesPage.Text = waiting.Count > 0 ? $"Notices ({waiting.Count})" : "Notices";
    }

    /// <summary>Its keys after its words, numbered as the number keys press them (as A1 reads a notice).</summary>
    static string KeyWords(Notice n)
    {
        if (n.Confirm != null) return " Enter confirms, Escape cancels.";
        var keys = string.Join(", ", n.Buttons.Select((b, i) => ButtonKey(i) is { } k ? $"{k} {b.Label}" : b.Label));
        var end = n.Words.Length > 0 && n.Words[^1] is '.' or '?' or '!' ? "" : ".";
        return end + (keys.Length > 0 ? $" Keys: {keys}." : "") + (n.TextBox ? " Enter to reply." : "");
    }

    void ShowNotice()
    {
        var n = Selected();
        // a reply belongs to the notice it was typed for: another shown, the box starts empty
        if (n?.Id != _replyFor) { _replyFor = n?.Id; _noticeReply.Clear(); }
        _noticeText.Text = n == null ? "" : n.Confirm != null ? n.Words : $"{n.Words}\r\n\r\n{n.Detail}";
        _noticeButtons.SuspendLayout();
        foreach (Control c in _noticeButtons.Controls.Cast<Control>().ToList()) { _noticeButtons.Controls.Remove(c); c.Dispose(); }
        if (n != null)
            for (int i = 0; i < n.Buttons.Count; i++)
            {
                int at = i;
                var label = n.Buttons[i].Label;
                var b = new Button
                {
                    Name = $"noticeButton{i + 1}", Text = n.Confirm != null ? label : ButtonKey(i) is { } k ? $"{k}: {label}" : label, AutoSize = true, UseMnemonic = false,
                };
                b.Click += (_, _) => Press(n, at, fromButton: true);
                _noticeButtons.Controls.Add(b);
            }
        _noticeButtons.ResumeLayout();
        _noticeReply.Visible = _replyLabel.Visible = n?.TextBox == true;
        _noticeIgnore.Enabled = n != null && n.Confirm == null;
    }

    /// <summary>The key that presses a notice's button i: 1 to 9, 0, minus, equals (twelve, a script menu's most).</summary>
    static string? ButtonKey(int i) => i switch { < 9 => $"{i + 1}", 9 => "0", 10 => "minus", 11 => "equals", _ => null };

    void NoticeKey(KeyEventArgs e)
    {
        var n = Selected();
        if (n == null) return;
        if (e.KeyCode == System.Windows.Forms.Keys.Apps || e.KeyData == (System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.F10))
        {
            e.Handled = e.SuppressKeyPress = true;
            ShowNoticeMenu();
            return;
        }
        int digit = e.KeyCode is >= System.Windows.Forms.Keys.D0 and <= System.Windows.Forms.Keys.D9 && !e.Control && !e.Alt
            ? (e.KeyCode == System.Windows.Forms.Keys.D0 ? 9 : e.KeyCode - System.Windows.Forms.Keys.D1)
            : e.KeyCode is >= System.Windows.Forms.Keys.NumPad0 and <= System.Windows.Forms.Keys.NumPad9
                ? (e.KeyCode == System.Windows.Forms.Keys.NumPad0 ? 9 : e.KeyCode - System.Windows.Forms.Keys.NumPad1)
            // a script menu's 11th and 12th, as on the top row after 0 (and the VR client's reader keys)
            : e.KeyCode is System.Windows.Forms.Keys.OemMinus or System.Windows.Forms.Keys.Subtract && !e.Control && !e.Alt ? 10
            : e.KeyCode == System.Windows.Forms.Keys.Oemplus && !e.Control && !e.Alt && !e.Shift ? 11 : -1;
        if (digit >= 0 && n.Confirm == null) { e.Handled = e.SuppressKeyPress = true; Press(n, digit, fromButton: false); }
        else if (e.KeyCode == System.Windows.Forms.Keys.Delete && n.Confirm == null) { e.Handled = true; PutAway(fromButton: false); }
        else if (e.KeyCode == System.Windows.Forms.Keys.Enter && n.Confirm != null) { e.Handled = e.SuppressKeyPress = true; Press(n, 0, false); }
        else if (e.KeyCode == System.Windows.Forms.Keys.Escape && n.Confirm != null) { e.Handled = e.SuppressKeyPress = true; Press(n, 1, false); }
        else if (e.KeyCode == System.Windows.Forms.Keys.Enter)
        {
            e.Handled = e.SuppressKeyPress = true;
            (n.TextBox ? _noticeReply : _noticeButtons.Controls.Count > 0 ? _noticeButtons.Controls[0] : (Control)_noticeText).Focus();
        }
    }

    /// <summary>The Applications key on a notice: its keys as a menu, each with the key that presses it, and Put away
    /// (a confirmation: Confirm and Cancel). An item answers as the person's own press of it, the same as the key.</summary>
    void ShowNoticeMenu()
    {
        if (Selected() is not { } n) return;
        _noticeMenu.Items.Clear();
        void Add(string text, string? key, Action act) =>
            _noticeMenu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => act()) { ShortcutKeyDisplayString = key });
        if (n.Confirm != null)
        {
            Add("&Confirm", "Enter", () => Press(n, 0, fromButton: true));
            Add("C&ancel", "Escape", () => Press(n, 1, fromButton: true));
        }
        else
        {
            for (int i = 0; i < n.Buttons.Count; i++)
            {
                int at = i;
                var k = ButtonKey(i);
                // no mnemonics: a script's labels are its own words, and its keys are the numbers
                var item = new ToolStripMenuItem(n.Buttons[i].Label.Replace("&", "&&"), null, (_, _) => Press(n, at, fromButton: true))
                {
                    ShortcutKeyDisplayString = k switch { "minus" => "Minus", "equals" => "Equals", _ => k },
                };
                _noticeMenu.Items.Add(item);
            }
            if (n.TextBox) Add("Reply...", "Enter", () => BeginInvoke(() => RealInput.Ours(() => _noticeReply.Focus())));
            if (_noticeMenu.Items.Count > 0) _noticeMenu.Items.Add(new ToolStripSeparator());
            Add("Put away", "Delete", () => PutAway(fromButton: true));
        }
        var r = _notices.SelectedItems.Count > 0 ? _notices.SelectedItems[0].Bounds : Rectangle.Empty;
        _noticeMenu.Show(_notices, new Point(r.Left + 10, r.Bottom));
    }

    /// <summary>Escape in the notices: back where Control+N came from (the page and the control), else to Say.</summary>
    void BackFromNotices()
    {
        var from = _noticesFrom;
        _noticesFrom = null;
        if (from is { Control: { IsDisposed: false } c, Page: var page } && page != _noticesPage)
        {
            _areas.SelectedTab = page;
            // (walk mode isn't gone back into unasked: its keys take the arrows)
            if (c.CanFocus && c != _walkPad) c.Focus(); else FirstControl(page)?.Focus();
            FocusChanged();
            return;
        }
        GoArea(_chatPage);
    }

    /// <summary>Ctrl+N: to the notices, the newest selected (the one just heard).</summary>
    void NewestNotice()
    {
        if (_areas.SelectedTab != _noticesPage && ActiveControl is { } from && _areas.SelectedTab is { } page)
            _noticesFrom = (from, page);
        PollNotices();
        GoArea(_noticesPage);
        if (_notices.Items.Count > 0) { _notices.Items[0].Selected = true; _notices.Items[0].Focused = true; }
        NoteSelection();   // during the person's press: theirs
    }

    void Press(Notice n, int index, bool fromButton)
    {
        // cancelling a confirmation takes the person's input (or anything, with hello on: it only ever says no)
        if (n.Confirm != null && index != 0)
        {
            if (!RealInput.Person && !_set.ConfirmByHello) { Reader?.Answer(RealInput.Why, keep: false); return; }
            var a = Host!.ScreenReader.Actions.Confirm(n.Confirm, false, RealInput.PressedAt);
            if (a == ReaderActions.Answered.Done) _cancelledHere = n.Confirm;
            Answered(a, fromButton);
            return;
        }
        if (n.Confirm == null && index >= n.Buttons.Count) { Reader?.Answer($"No key {ButtonKey(index) ?? $"{index + 1}"} there.", keep: false); return; }
        if (RealInput.Confirms) { Give(RealInput.PressedAt); return; }
        var what = n.Confirm != null ? $"{n.Confirm.Title.TrimEnd('.')}." : $"answer {n.Words.TrimEnd('.')} with {n.Buttons[index].Label}.";
        Hello.Instead(_set.ConfirmByHello, Handle, what, RealInput.Why, w => Reader?.Answer(w, keep: false), () => Give(Environment.TickCount64));

        void Give(long pressedAt)
        {
            if (n.Confirm != null)
            {
                // the confirmation this notice showed, never another that took its place
                Answered(Host!.ScreenReader.Actions.Confirm(n.Confirm, true, pressedAt), fromButton);
                return;
            }
            var (name, label) = n.Buttons[index];
            if (n.Menu && n.ObjectId != null) _answeredMenu = (n.ObjectId, Environment.TickCount64);
            _ = RespondAsync(n, new JsonObject { [name] = true }, label);
            AfterAnswer(fromButton);
        }
    }

    void Answered(ReaderActions.Answered a, bool fromButton)
    {
        switch (a)
        {
            case ReaderActions.Answered.Done: AfterAnswer(fromButton); break;
            case ReaderActions.Answered.TooSoon: Reader?.Answer("That came just as it appeared. Press again to confirm.", keep: false); break;
            default: Reader?.Answer("That confirmation isn't waiting any more.", keep: false); AfterAnswer(fromButton); break;
        }
    }

    /// <summary>Enter in the reply box: text the person's own keys typed (a screen reader's passed-on Enter counts), for
    /// the notice it was typed for; with hello on, anything else is asked of Windows Hello with the text in its words.</summary>
    void Reply(Notice n, string text)
    {
        if (text.Trim().Length == 0) { Reader?.Answer("Type a reply first.", keep: false); return; }
        if (_replyFor != n.Id) { Reader?.Answer("That reply was for another notice. Type it again.", keep: false); return; }
        if (RealInput.Text && !_replyForeign) { Send(); return; }
        var why = _replyForeign ? "Something other than your keys changed the reply. Clear it and type it again." : RealInput.Why;
        Hello.Instead(_set.ConfirmByHello, Handle, $"reply to {n.Words.TrimEnd('.')}: {text}", why, w => Reader?.Answer(w, keep: false), Send);

        async void Send()
        {
            if (_replyFor != n.Id || _noticeReply.Text != text) { Reader?.Answer("The reply changed. Nothing was sent.", keep: false); return; }
            _noticeReply.Clear();
            await RespondAsync(n, new JsonObject { ["!!llTextBox!!"] = text }, "Reply");
            AfterAnswer(fromButton: true);
        }
    }

    async Task RespondAsync(Notice n, JsonObject response, string label)
    {
        var err = await Host!.Session.Notify.RespondAsync(n.Id, response);
        Reader?.Answer(err == null ? $"{label}, done." : $"{label} didn't go through: {err}");
    }

    void PutAway(bool fromButton)
    {
        if (Selected() is not { Confirm: null } n) return;
        // putting away only says no: the person's input, or anything with hello on
        if (!RealInput.Person && !_set.ConfirmByHello) { Reader?.Answer(RealInput.Why, keep: false); return; }
        Host!.Session.Notify.Dismiss(n.Id);
        Reader?.Answer("Put away.");
        AfterAnswer(fromButton);
    }

    /// <summary>After an answer the notice goes: the focus stays in the notices, on the list (not lost with the button).</summary>
    void AfterAnswer(bool fromButton)
    {
        _noticesVersion = -1;
        _shownKeys = null;
        RealInput.Ours(() =>
        {
            PollNotices();
            if (fromButton || !_notices.Focused) _notices.Focus();
        });
    }
}
