namespace Sojourn.Access;

/// <summary>
/// A notecard or script open in a real edit control (a blind tester's wish: no going offline to Notepad): Control+S saves
/// (said back, a script's compile errors read out and listed under the text), Escape closes, asking first when
/// there are changes. Opened by the person's own key, so the focus starts in the text.
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
    readonly Func<string, Task<(string Said, string? Errors)>> _save;
    readonly Action<string> _say;
    readonly string _what;
    string _saved;
    bool _saving;

    /// <param name="save">Saves the text: what to say, and a script's errors (null when none).</param>
    public EditorForm(string what, string name, string text, Func<string, Task<(string Said, string? Errors)>> save, Action<string> say)
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
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(_text, 0, 0);
        t.Controls.Add(_errors, 0, 1);
        t.Controls.Add(row, 0, 2);
        _text.TabIndex = 0; _errors.TabIndex = 1; row.TabIndex = 2;
        Controls.Add(t);
        ActiveControl = _text;
        Shown += (_, _) => { _text.SelectionStart = 0; _text.SelectionLength = 0; _text.Focus(); };
    }

    public string Current => _text.Text;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S)) { _ = SaveAsync(); return true; }
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    async Task SaveAsync()
    {
        if (_saving) return;
        _saving = true;
        try
        {
            var text = _text.Text;
            _say("Saving.");
            var (said, errors) = await _save(text.Replace("\r\n", "\n"));
            if (said.StartsWith("Saved", StringComparison.Ordinal)) _saved = text;
            _errors.Text = errors ?? "";
            _errors.Visible = !string.IsNullOrEmpty(errors);
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
