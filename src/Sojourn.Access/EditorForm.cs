namespace Sojourn.Access;

/// <summary>
/// A notecard or script open in a real edit control (a blind tester's wish: no going offline to Notepad): Control+S saves
/// (said back, a script's compile errors read out and listed under the text), Escape closes, asking first when
/// there are changes. Opened by the person's own key, so the focus starts in the text. A notecard that holds things has
/// them in a list a Tab from the text (MainForm.Notecards.cs). Moving around: Control+F finds, F3 and Shift+F3 the next
/// and the one before; Control+G a line by number; F8 and Shift+F8 the last save's errors, line by line (the caret stays
/// where it is until one of these is pressed); Control+L says where the caret is, as Radegast's does.
/// </summary>
public sealed class EditorForm : Form
{
    readonly TextBox _text = new()
    {
        Name = "editorText", Multiline = true, AcceptsReturn = true, AcceptsTab = false, ScrollBars = ScrollBars.Both, WordWrap = false,
        Dock = DockStyle.Fill, MaxLength = 0, Font = new Font(FontFamily.GenericMonospace, 10),
    };
    readonly TextBox _errors = new()
    {
        Name = "editorErrors", AccessibleName = "Errors", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BackColor = SystemColors.Window, Height = 70, Visible = false,
    };
    readonly Func<string, Task<(string Said, string? Errors, string? Now)>> _save;
    readonly Action<string> _say;
    readonly string _what;
    string _saved;
    bool _saving;
    string _finding = "";
    readonly List<(int Line, int Column, string Words)> _errorsAt = new();
    int _errorOn = -1;

    /// <param name="save">Saves the text: what to say, a script's errors (null when none), and the text as it now is when
    /// saving changed it (null otherwise).</param>
    /// <param name="embedded">What a notecard holds, listed under the text (null when nothing).</param>
    public EditorForm(string what, string name, string text, Func<string, Task<(string Said, string? Errors, string? Now)>> save, Action<string> say, ListView? embedded = null)
    {
        _what = what;
        _save = save;
        _say = say;
        _saved = text;
        Name = "editor";
        Text = $"{what}: {name}";
        AccessibleName = Text;
        _text.AccessibleName = $"{what} text";
        _text.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _saved = _text.Text;
        Size = new Size(760, 560);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        var save1 = new Button { Name = "editorSave", Text = "&Save", AutoSize = true };
        var close = new Button { Name = "editorClose", Text = "Close", AutoSize = true };
        save1.Click += async (_, _) => await SaveAsync();
        close.Click += (_, _) => Close();
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        row.Controls.Add(save1);
        row.Controls.Add(close);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(embedded == null ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 110));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(_text, 0, 0);
        t.Controls.Add(_errors, 0, 1);
        if (embedded != null) { t.Controls.Add(embedded, 0, 2); embedded.TabIndex = 2; }
        t.Controls.Add(row, 0, 3);
        _text.TabIndex = 0; _errors.TabIndex = 1; row.TabIndex = 3;
        Controls.Add(t);
        ActiveControl = _text;
        Shown += (_, _) => { _text.SelectionStart = 0; _text.SelectionLength = 0; _text.Focus(); };
    }

    public string Current => _text.Text;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.S: _ = SaveAsync(); return true;
            case Keys.Escape: Close(); return true;
            case Keys.Control | Keys.F: Find(); return true;
            case Keys.F3: FindNext(+1); return true;
            case Keys.Shift | Keys.F3: FindNext(-1); return true;
            case Keys.Control | Keys.G: GoToLine(); return true;
            case Keys.Control | Keys.L: SayWhere(); return true;
            case Keys.Control | Keys.K: OpenLinkHere(); return true;
            case Keys.F8: NextError(+1); return true;
            case Keys.Shift | Keys.F8: NextError(-1); return true;
            case Keys.F1:
                _say("Control+S saves, Escape closes. Control+F finds, F3 the next, Shift+F3 the one before. Control+G goes to a line. "
                     + "Control+L says the line and column. Control+K opens the link on the line. F8 goes to the next error from the last save, Shift+F8 the one before. Tab: the errors, then what it holds, then Save and Close.");
                return true;
        }
        if (keyData == (Keys.Control | Keys.Back) && ActiveControl == _text && TextKeys.DeleteWordBack(_text)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    int LineCount => _text.GetLineFromCharIndex(Math.Max(0, _text.TextLength)) + 1;

    string LineText(int line)
    {
        int start = _text.GetFirstCharIndexFromLine(line);
        if (start < 0) return "";
        int next = _text.GetFirstCharIndexFromLine(line + 1);
        var words = next < 0 ? _text.Text[start..] : _text.Text[start..next];
        words = words.TrimEnd('\r', '\n').Trim();
        return words.Length == 0 ? "blank" : words;
    }

    /// <summary>The caret put there, the text scrolled to it and the focus in the text (the key asked for it).</summary>
    void Place(int at, int length = 0)
    {
        _text.Focus();
        _text.Select(at, length);
        _text.ScrollToCaret();
    }

    /// <summary>The app's way of opening the first link in some text; false when there's none. Enter makes a new line here,
    /// so links in a card have a key of their own (the blind tester, 2026-11-07).</summary>
    public Func<string, bool>? OpenFirstLink;

    void OpenLinkHere()
    {
        var text = _text.Text;
        int at = Math.Min(_text.SelectionStart, text.Length);
        int from = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1, to = text.IndexOf('\n', at);
        if (OpenFirstLink?.Invoke(text[from..(to < 0 ? text.Length : to)]) != true) _say("No link on this line.");
    }

    void SayWhere()
    {
        int at = _text.SelectionStart, line = _text.GetLineFromCharIndex(at);
        _say($"Line {line + 1} of {LineCount}, column {at - _text.GetFirstCharIndexFromLine(line) + 1}.");
    }

    void Find()
    {
        if (Dialogs.Ask(this, "Find", "Find", _finding) is not { } words) return;
        _finding = words;
        FindNext(+1, fromSelection: false);
    }

    void FindNext(int way, bool fromSelection = true)
    {
        if (_finding.Length == 0) { Find(); return; }
        var text = _text.Text;
        int start = _text.SelectionStart, found;
        if (way > 0)
        {
            int from = Math.Min(text.Length, fromSelection ? start + Math.Max(1, _text.SelectionLength) : start);
            found = text.IndexOf(_finding, from, StringComparison.OrdinalIgnoreCase);
            if (found < 0) found = text.IndexOf(_finding, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            found = start > 0 ? text.LastIndexOf(_finding, start - 1, StringComparison.OrdinalIgnoreCase) : -1;
            if (found < 0) found = text.LastIndexOf(_finding, StringComparison.OrdinalIgnoreCase);
        }
        if (found < 0) { _say($"{_finding} isn't in it."); return; }
        bool round = way > 0 ? found < start : found > start;
        Place(found, _finding.Length);
        int line = _text.GetLineFromCharIndex(found);
        _say($"{(round ? "From the other end: " : "")}Line {line + 1}: {LineText(line)}");
    }

    void GoToLine()
    {
        int lines = LineCount;
        var words = Dialogs.Ask(this, "Go to line", $"Line number, 1 to {lines}", (_text.GetLineFromCharIndex(_text.SelectionStart) + 1).ToString());
        if (words == null) return;
        if (!int.TryParse(words, out int n) || n < 1 || n > lines) { _say($"{words} isn't a line here: 1 to {lines}."); return; }
        Place(_text.GetFirstCharIndexFromLine(n - 1));
        _say($"Line {n}: {LineText(n - 1)}");
    }

    void NextError(int way)
    {
        if (_errorsAt.Count == 0) { _say("No errors from the last save."); return; }
        _errorOn = _errorOn < 0 ? (way > 0 ? 0 : _errorsAt.Count - 1) : (_errorOn + way + _errorsAt.Count) % _errorsAt.Count;
        var (line, column, words) = _errorsAt[_errorOn];
        var count = $"Error {_errorOn + 1} of {_errorsAt.Count}";
        if (line < 1 || line > LineCount) { _say($"{count}: {words}"); return; }
        int start = _text.GetFirstCharIndexFromLine(line - 1), next = _text.GetFirstCharIndexFromLine(line), length = next >= 0 ? next - start : _text.TextLength - start;
        Place(start + Math.Clamp(column - 1, 0, Math.Max(0, length)));
        _say($"{count}, line {line}, column {column}: {words} The line: {LineText(line - 1)}");
    }

    /// <summary>The errors box's lines, those saying where ("Line 3, column 5: ...") with their places, for F8.</summary>
    void NoteErrors(string? errors)
    {
        _errorsAt.Clear();
        _errorOn = -1;
        foreach (var line in (errors ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^Line (\d+), column (\d+): (.*)$");
            _errorsAt.Add(m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Value) : (0, 0, line));
        }
    }

    async Task SaveAsync()
    {
        if (_saving) return;
        _saving = true;
        try
        {
            var text = _text.Text;
            _say("Saving.");
            var (said, errors, now) = await _save(text.Replace("\r\n", "\n"));
            if (now != null && _text.Text == text)
            {
                var at = _text.SelectionStart;
                text = _text.Text = now.Replace("\r\n", "\n").Replace("\n", "\r\n");
                _text.SelectionStart = Math.Min(at, text.Length);
            }
            if (said.StartsWith("Saved", StringComparison.Ordinal)) _saved = text;
            _errors.Text = errors ?? "";
            _errors.Visible = !string.IsNullOrEmpty(errors);
            NoteErrors(errors);
            _say(said);
        }
        finally { _saving = false; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!e.Cancel && _text.Text != _saved && e.CloseReason == CloseReason.UserClosing)
        {
            var r = MessageBox.Show(this, $"Save the changes to this {_what.ToLowerInvariant()}?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes)
            {
                // saved, then closed (a failed save keeps it open)
                e.Cancel = true;
                _ = SaveThenCloseAsync();
                return;
            }
            _saved = _text.Text;
        }
        base.OnFormClosing(e);
    }

    async Task SaveThenCloseAsync()
    {
        await SaveAsync();
        if (_text.Text == _saved) Close();
    }
}
