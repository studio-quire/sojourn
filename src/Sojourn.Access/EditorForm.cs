namespace Sojourn.Access;

/// <summary>
/// A notecard or script open in a real edit control (a blind tester's wish: no going offline to Notepad): Control+S saves
/// (said back, a script's compile errors read out and listed under the text), Escape closes, asking first when
/// there are changes. Opened by the person's own key, so the focus starts in the text. A notecard that holds things has
/// them in a list a Tab from the text (MainForm.Notecards.cs). Moving around: Control+F finds, F3 and Shift+F3 the next
/// and the one before; Control+H replaces (EditorForm.Replace.cs); Control+G a line by number; F8 and Shift+F8 the last
/// save's errors, line by line (the caret stays where it is until one of these is pressed); Control+L says where the caret
/// is, as Radegast's does. Control+O puts a file's text in place of the text here, Control+Shift+S saves a copy to a file.
/// A script inside an object has the viewer's Mono and Running boxes and Reset (<see cref="UseScriptSwitches"/>).
/// </summary>
public sealed partial class EditorForm : Form
{
    readonly TextBox _text = new()
    {
        Name = "editorText", Multiline = true, AcceptsReturn = true, AcceptsTab = false, ScrollBars = ScrollBars.Both, WordWrap = false,
        Dock = DockStyle.Fill, MaxLength = 0, Font = new Font(FontFamily.GenericMonospace, 10), HideSelection = false,
    };
    readonly TextBox _errors = new()
    {
        Name = "editorErrors", AccessibleName = "Errors", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BackColor = SystemColors.Window, Height = 70, Visible = false,
    };
    readonly TableLayoutPanel _layout;
    readonly FlowLayoutPanel _row;
    readonly Func<string, Task<(string Said, string? Errors, string? Now)>> _save;
    readonly Action<string> _say;
    readonly bool _readOnly;
    readonly string _what, _name;
    string _saved;
    bool _saving;
    string _finding = "";
    readonly List<(int Line, int Column, string Words)> _errorsAt = new();
    int _errorOn = -1;
    ListView? _held;

    /// <param name="save">Saves the text: what to say, a script's errors (null when none), and the text as it now is when
    /// saving changed it (null otherwise).</param>
    /// <param name="embedded">What a notecard holds, listed under the text (null when nothing).</param>
    /// <param name="readOnly">Something to read (a chat log): the same keys for moving around, nothing to save, the caret at the end.</param>
    public EditorForm(string what, string name, string text, Func<string, Task<(string Said, string? Errors, string? Now)>> save, Action<string> say, ListView? embedded = null, bool readOnly = false)
    {
        _readOnly = readOnly;
        _what = what;
        _name = name;
        _save = save;
        _say = say;
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
        if (readOnly) { _text.ReadOnly = true; _text.BackColor = SystemColors.Window; save1.Visible = false; }
        _row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        _row.Controls.Add(save1);
        _row.Controls.Add(close);
        _layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.Controls.Add(_text, 0, 0);
        _layout.Controls.Add(_errors, 0, 1);
        _layout.Controls.Add(_row, 0, 3);
        _text.TabIndex = 0; _errors.TabIndex = 1; _row.TabIndex = 3;
        if (embedded != null) Held(embedded);
        Controls.Add(_layout);
        ActiveControl = _text;
        Shown += (_, _) =>
        {
            _text.SelectionStart = readOnly ? _text.TextLength : 0;
            _text.SelectionLength = 0;
            _text.Focus();
            if (readOnly) _text.ScrollToCaret();
        };
    }

    public string Current => _text.Text;

    /// <summary>What a notecard holds, put under the text (when a card that held nothing is given something, too).</summary>
    public void Held(ListView list)
    {
        if (_held == list) return;
        _held = list;
        list.Dock = DockStyle.Fill;
        list.TabIndex = 2;
        _layout.RowStyles[2] = new RowStyle(SizeType.Absolute, 110);
        _layout.Controls.Add(list, 0, 2);
    }

    /// <summary>Control+Shift+V in a notecard: what was copied in the inventory put in at the caret (MainForm.Notecards.cs).</summary>
    public Func<Task>? PutIn;

    /// <summary>Words put in at the caret (before any selection), undoable by Control+Z as typing is; where they went.</summary>
    public int InsertAtCaret(string words)
    {
        int at = _text.SelectionStart;
        _text.Select(at, 0);
        _text.Paste(words);
        return at;
    }

    /// <summary>The first place these exact words are in the text, taken out (an item put in, then not wanted).</summary>
    public void TakeOut(string words)
    {
        int at = _text.Text.IndexOf(words, StringComparison.Ordinal);
        if (at < 0) return;
        int caret = _text.SelectionStart;
        _text.Select(at, words.Length);
        _text.Paste("");
        _text.Select(caret > at ? Math.Max(at, caret - words.Length) : caret, 0);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.S: if (!_readOnly) _ = SaveAsync(); return true;
            case Keys.Escape: Close(); return true;
            case Keys.Control | Keys.F: Find(); return true;
            case Keys.F3: FindNext(+1); return true;
            case Keys.Shift | Keys.F3: FindNext(-1); return true;
            case Keys.Control | Keys.H when !_readOnly: Replace(); return true;
            case Keys.Control | Keys.G: GoToLine(); return true;
            case Keys.Control | Keys.L: SayWhere(); return true;
            case Keys.Control | Keys.K: OpenLinkHere(); return true;
            case Keys.Control | Keys.O when !_readOnly: OpenFile(); return true;
            case Keys.Control | Keys.Shift | Keys.S: SaveFile(); return true;
            case Keys.Control | Keys.Shift | Keys.V when PutIn != null: _ = PutIn(); return true;
            case Keys.F8: NextError(+1); return true;
            case Keys.Shift | Keys.F8: NextError(-1); return true;
            case Keys.Alt | Keys.M when _mono != null && _mono.Visible: ToggleMono(); return true;
            case Keys.Alt | Keys.R when _running != null: _ = ToggleRunningAsync(); return true;
            case Keys.Alt | Keys.E when _reset != null: _ = ResetAsync(); return true;
            case Keys.F1: _say(Help()); return true;
        }
        if (keyData == (Keys.Control | Keys.Back) && ActiveControl == _text && TextKeys.DeleteWordBack(_text)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    string Help()
    {
        const string moving = "Control+F finds, F3 the next, Shift+F3 the one before. Control+G goes to a line. Control+L says the line and column. Control+K opens the link on the line.";
        if (_readOnly) return $"Escape closes. {moving} Control+Shift+S saves a copy to a file.";
        var words = new List<string> { "Control+S saves, Escape closes.", moving, "Control+H replaces." };
        if (_what == "Script") words.Add("F8 goes to the next error from the last save, Shift+F8 the one before.");
        if (_running != null)
            words.Add(_mono is { Visible: true } ? "Alt+M turns Mono on or off for the next save, Alt+R runs or stops it, Alt+E resets it." : "Alt+R runs or stops it, Alt+E resets it.");
        if (PutIn != null) words.Add("Control+Shift+V puts what you copied in your inventory into the card, at the caret.");
        words.Add("Control+O puts a file's text in place of this, Control+Shift+S saves a copy to a file.");
        words.Add(_held != null ? "Tab: the errors, then what it holds, then the buttons." : "Tab: the errors, then the buttons.");
        return string.Join(" ", words);
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
        _matchCase = _wholeWords = false;
        FindNext(+1, fromSelection: false);
    }

    void FindNext(int way, bool fromSelection = true)
    {
        if (_finding.Length == 0) { Find(); return; }
        int start = _text.SelectionStart;
        int found = Next(way, fromSelection ? start + (way > 0 ? Math.Max(1, _text.SelectionLength) : 0) : start);
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

    // --- a script inside an object: the viewer's Mono and Running boxes, and Reset ---------------------------------------

    CheckBox? _mono, _running;
    Button? _reset;
    bool _monoSaved;
    Func<bool, Task<bool?>>? _setRunning;
    Func<Task>? _resetIt;

    /// <summary>Whether the next save compiles for Mono (only meaningful with the switches shown).</summary>
    public bool Mono => _mono?.Checked ?? true;

    /// <summary>Whether the script is to run after the next save.</summary>
    public bool Running => _running?.Checked ?? false;

    /// <summary>
    /// The official viewer's boxes for a script in an object. Mono: what the next save compiles it for, Mono (Second Life's
    /// usual engine, 64 KB of memory) or the older LSL2 (16 KB); not shown for a Lua script, which is Lua whatever. Running:
    /// changed at once, as the viewer's box does. Reset starts it over. Each is a Tab stop in the row of buttons, and has a
    /// key that leaves the focus in the text: Alt+M, Alt+R, Alt+E (their underlined letters). setRunning: the grid's answer,
    /// null when it didn't come (said by the caller).
    /// </summary>
    public void UseScriptSwitches(bool mono, bool monoShown, bool running, Func<bool, Task<bool?>> setRunning, Func<Task> reset)
    {
        _setRunning = setRunning;
        _resetIt = reset;
        _monoSaved = mono;
        _mono = new CheckBox { Name = "editorMono", Text = "&Mono", Checked = mono, AutoCheck = false, AutoSize = true, Visible = monoShown };
        _running = new CheckBox { Name = "editorRunning", Text = "&Running", Checked = running, AutoCheck = false, AutoSize = true };
        _reset = new Button { Name = "editorReset", Text = "R&eset", AutoSize = true };
        Describe(_mono, "Compiles the script for Mono, Second Life's usual engine: faster, with 64 KB of memory where the older LSL2 has 16. Most scripts want it. Changes when you save.");
        Describe(_running, "Whether the script runs in the object. Changes at once.");
        Describe(_reset, "Starts the script over, as if it had just been put in.");
        _mono.Click += (_, _) => ToggleMono();
        _running.Click += async (_, _) => await ToggleRunningAsync();
        _reset.Click += async (_, _) => await ResetAsync();
        _row.Controls.Add(_mono);
        _row.Controls.Add(_running);
        _row.Controls.Add(_reset);
        _row.Controls.SetChildIndex(_mono, 1);
        _row.Controls.SetChildIndex(_running, 2);
        _row.Controls.SetChildIndex(_reset, 3);
    }

    /// <summary>What a control does, said after its name and state: MSAA's description (AccessibleDescription) and UI
    /// Automation's help text (a check box's comes only from QueryAccessibilityHelp), whichever a screen reader reads.</summary>
    static void Describe(Control c, string words)
    {
        c.AccessibleDescription = words;
        c.QueryAccessibilityHelp += (_, e) => e.HelpString = words;
    }

    void ToggleMono()
    {
        _mono!.Checked = !_mono.Checked;
        _say(_mono.Checked ? "Mono on: saving compiles it for Mono, Second Life's usual engine."
            : "Mono off: saving compiles it for the older LSL2 engine, with 16 KB of memory rather than Mono's 64.");
    }

    bool _switching;

    async Task ToggleRunningAsync()
    {
        if (_switching || _setRunning == null) return;
        _switching = true;
        try
        {
            if (await _setRunning(!_running!.Checked) is { } now && !IsDisposed) _running.Checked = now;
        }
        finally { _switching = false; }
    }

    async Task ResetAsync()
    {
        if (_resetIt != null) await _resetIt();
    }

    // --- saving -----------------------------------------------------------------------------------------------------

    async Task SaveAsync()
    {
        if (_saving) return;
        _saving = true;
        try
        {
            var text = _text.Text;
            bool mono = Mono;
            _say("Saving.");
            var (said, errors, now) = await _save(text.Replace("\r\n", "\n"));
            if (now != null && _text.Text == text)
            {
                var at = _text.SelectionStart;
                text = _text.Text = now.Replace("\r\n", "\n").Replace("\n", "\r\n");
                _text.SelectionStart = Math.Min(at, text.Length);
            }
            if (said.StartsWith("Saved", StringComparison.Ordinal)) { _saved = text; _monoSaved = mono; }
            _errors.Text = errors ?? "";
            _errors.Visible = !string.IsNullOrEmpty(errors);
            NoteErrors(errors);
            _say(said);
        }
        finally { _saving = false; }
    }

    /// <summary>Something here not yet saved: the text, Mono, or what a card is to hold.</summary>
    bool Changed => _text.Text != _saved || (_mono != null && _mono.Checked != _monoSaved) || (PendingChanges?.Invoke() ?? false);

    /// <summary>A card's things to put in or take off at the next save (MainForm.Notecards.cs).</summary>
    public Func<bool>? PendingChanges;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!e.Cancel && !_readOnly && Changed && e.CloseReason == CloseReason.UserClosing)
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
            _monoSaved = Mono;
            PendingChanges = null;
        }
        base.OnFormClosing(e);
    }

    async Task SaveThenCloseAsync()
    {
        await SaveAsync();
        if (!Changed) Close();
    }

    // --- files on disk ------------------------------------------------------------------------------------------------

    /// <summary>Tests: a folder standing in for Windows' Open and Save windows, the file named as the Save window would
    /// name it (the item's name and kind).</summary>
    static readonly string? TestFiles = Environment.GetEnvironmentVariable("SOJOURN_TEST_FILES");

    const int MaxFileBytes = 1 << 20;

    string FileName()
    {
        var bad = Path.GetInvalidFileNameChars();
        var name = new string(_name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return (name.Length == 0 ? _what.ToLowerInvariant() : name) + (_what == "Script" ? ".lsl" : ".txt");
    }

    string Filter => _what == "Script" ? "LSL scripts (*.lsl)|*.lsl|Text files (*.txt)|*.txt|All files (*.*)|*.*" : "Text files (*.txt)|*.txt|All files (*.*)|*.*";

    static string Where(string path) => $"{Path.GetFileName(path)} in {(Path.GetFileName(Path.GetDirectoryName(path)) is { Length: > 0 } dir ? dir : Path.GetPathRoot(path))}";

    /// <summary>Control+Shift+S: a copy of the text as it stands in a file, by Windows' own Save window (the same text as
    /// the window shows: a card's things as their markers).</summary>
    void SaveFile()
    {
        string path;
        if (TestFiles != null) path = Path.Combine(TestFiles, FileName());
        else
        {
            using var d = new SaveFileDialog { Title = $"Save a copy of {_name} to a file", FileName = FileName(), Filter = Filter, DefaultExt = _what == "Script" ? "lsl" : "txt", AddExtension = true, OverwritePrompt = true };
            if (d.ShowDialog(this) != DialogResult.OK) { _say("No copy saved."); return; }
            path = d.FileName;
        }
        try { File.WriteAllText(path, _text.Text, new System.Text.UTF8Encoding(false)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { _say($"Couldn't save to {Where(path)}: {e.Message}"); return; }
        _say($"Saved a copy to {Where(path)}.");
    }

    /// <summary>Control+O: a file's text in place of the text here, by Windows' own Open window; saved into Second Life
    /// only by Control+S, and Control+Z gives back what was here.</summary>
    void OpenFile()
    {
        string path;
        if (TestFiles != null) path = Path.Combine(TestFiles, FileName());
        else
        {
            using var d = new OpenFileDialog { Title = $"Open a file into {_name}", Filter = Filter, CheckFileExists = true, Multiselect = false };
            if (d.ShowDialog(this) != DialogResult.OK) { _say("Nothing opened."); return; }
            path = d.FileName;
        }
        string text;
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) { _say($"{Path.GetFileName(path)} is over a megabyte: too big for a {_what.ToLowerInvariant()}."); return; }
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { _say($"Couldn't open {Where(path)}: {e.Message}"); return; }
        if (_text.Text != _saved
            && MessageBox.Show(this, $"Put {Path.GetFileName(path)} in place of the changes here? They aren't saved.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            _say("Nothing opened.");
            return;
        }
        text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _text.SelectAll();
        _text.Paste(text);
        Place(0);
        int lines = LineCount;
        _say($"Opened {Path.GetFileName(path)}, {lines} {(lines == 1 ? "line" : "lines")}. Control+S saves it into Second Life.");
    }
}
