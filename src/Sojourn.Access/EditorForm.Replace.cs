namespace Sojourn.Access;

/// <summary>
/// Control+H: Windows' Replace window as Notepad has it (Find what, Replace with, Match case, Whole words; Find next,
/// Replace, Replace all, Close), each press said as find's keys say theirs: "Line 7: ...". Replace changes the match that's
/// selected and goes to the next; Replace all says how many and on which lines. Nothing is saved into Second Life until
/// Control+S, and Control+Z in the text undoes the last replace. F3 and Shift+F3 go on finding the same words after, with
/// the same choices; Control+F's own find is any case, anywhere, as it always was. Claude Opus 5.5, 2026-11-08.
/// </summary>
public sealed partial class EditorForm
{
    bool _matchCase, _wholeWords;
    string _replacing = "";

    StringComparison Comparison => _matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    static bool WordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    bool IsMatch(string text, int at) =>
        at >= 0 && at + _finding.Length <= text.Length && string.Compare(text, at, _finding, 0, _finding.Length, Comparison) == 0
        && (!_wholeWords || ((at == 0 || !WordChar(text[at - 1])) && (at + _finding.Length == text.Length || !WordChar(text[at + _finding.Length]))));

    /// <summary>Where the find words next are, forward from or back before a place in the text, going round the ends; -1: nowhere.</summary>
    int Next(int way, int from)
    {
        var text = _text.Text;
        if (_finding.Length == 0 || text.Length < _finding.Length) return -1;
        from = Math.Clamp(from, 0, text.Length);
        if (way > 0)
        {
            for (int at = text.IndexOf(_finding, from, Comparison); at >= 0; at = at + 1 < text.Length ? text.IndexOf(_finding, at + 1, Comparison) : -1)
                if (IsMatch(text, at)) return at;
            for (int at = text.IndexOf(_finding, Comparison); at >= 0 && at < from; at = at + 1 < text.Length ? text.IndexOf(_finding, at + 1, Comparison) : -1)
                if (IsMatch(text, at)) return at;
            return -1;
        }
        for (int at = from - 1; at >= 0; at--) if (IsMatch(text, at)) return at;
        for (int at = text.Length - _finding.Length; at >= from; at--) if (IsMatch(text, at)) return at;
        return -1;
    }

    /// <summary>Every match, in order, none overlapping.</summary>
    List<int> All()
    {
        var text = _text.Text;
        var found = new List<int>();
        if (_finding.Length == 0) return found;
        for (int at = text.IndexOf(_finding, Comparison); at >= 0; at = at < text.Length ? text.IndexOf(_finding, at, Comparison) : -1)
        {
            if (IsMatch(text, at)) { found.Add(at); at += _finding.Length; }
            else at++;
        }
        return found;
    }

    void Replace()
    {
        using var f = new Form
        {
            Name = "replace", Text = "Replace", AccessibleName = "Replace", FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ShowInTaskbar = false, KeyPreview = true,
        };
        var picked = _text.SelectionLength > 0 && !_text.SelectedText.Contains('\n') ? _text.SelectedText : _finding;
        var find = new LineBox { Name = "replaceFind", AccessibleName = "Find what", Text = picked, Width = 320 };
        var with = new LineBox { Name = "replaceWith", AccessibleName = "Replace with", Text = _replacing, Width = 320 };
        var matchCase = new CheckBox { Name = "replaceCase", Text = "Match &case", Checked = _matchCase, AutoSize = true };
        var whole = new CheckBox { Name = "replaceWhole", Text = "&Whole words only", Checked = _wholeWords, AutoSize = true };
        var next = new Button { Name = "replaceNext", Text = "&Find next", AutoSize = true };
        var one = new Button { Name = "replaceOne", Text = "&Replace", AutoSize = true };
        var all = new Button { Name = "replaceAll", Text = "Replace &all", AutoSize = true };
        var close = new Button { Name = "replaceClose", Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 4, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
        t.Controls.Add(new Label { Text = "Fi&nd what", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        t.Controls.Add(find, 1, 0);
        t.Controls.Add(new Label { Text = "Re&place with", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        t.Controls.Add(with, 1, 1);
        var checks = new FlowLayoutPanel { AutoSize = true };
        checks.Controls.AddRange(new Control[] { matchCase, whole });
        t.Controls.Add(checks, 1, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.AddRange(new Control[] { next, one, all, close });
        t.Controls.Add(buttons, 1, 3);
        f.Controls.Add(t);
        f.AcceptButton = next;
        f.CancelButton = close;
        f.ActiveControl = find;
        find.SelectAll();

        bool Take()
        {
            _finding = find.Text;
            _replacing = with.Text;
            _matchCase = matchCase.Checked;
            _wholeWords = whole.Checked;
            if (_finding.Length > 0) return true;
            _say("Type what to find first.");
            find.Focus();
            return false;
        }
        string Line(int at) { int line = _text.GetLineFromCharIndex(at); return $"Line {line + 1}: {LineText(line)}"; }
        void Show(int at) { _text.Select(at, _finding.Length); _text.ScrollToCaret(); }

        next.Click += (_, _) =>
        {
            if (!Take()) return;
            int start = _text.SelectionStart, at = Next(+1, start + (IsMatch(_text.Text, start) && _text.SelectionLength > 0 ? 1 : 0));
            if (at < 0) { _say($"{_finding} isn't in it."); return; }
            Show(at);
            _say($"{(at < start ? "From the other end: " : "")}{Line(at)}");
        };
        one.Click += (_, _) =>
        {
            if (!Take()) return;
            int start = _text.SelectionStart;
            // as Notepad: the first press finds, a press on the match selected replaces it and goes to the next
            if (_text.SelectionLength != _finding.Length || !IsMatch(_text.Text, start))
            {
                int at0 = Next(+1, start);
                if (at0 < 0) { _say($"{_finding} isn't in it."); return; }
                Show(at0);
                _say($"Replace again replaces this one: {Line(at0)}");
                return;
            }
            var line = _text.GetLineFromCharIndex(start) + 1;
            _text.Paste(_replacing);
            int after = start + _replacing.Length, at = Next(+1, after);
            if (at < 0) { _text.Select(after, 0); _say($"Replaced on line {line}. No more."); return; }
            Show(at);
            _say($"Replaced on line {line}. Next{(at < after ? ", from the other end" : "")}: {Line(at)}");
        };
        all.Click += (_, _) =>
        {
            if (!Take()) return;
            var found = All();
            if (found.Count == 0) { _say($"{_finding} isn't in it."); return; }
            var lines = found.Select(at => _text.GetLineFromCharIndex(at) + 1).Distinct().ToList();
            var text = _text.Text;
            var sb = new System.Text.StringBuilder(text.Length);
            int from = 0;
            foreach (var at in found) { sb.Append(text, from, at - from).Append(_replacing); from = at + _finding.Length; }
            sb.Append(text, from, text.Length - from);
            int caret = _text.SelectionStart;
            _text.SelectAll();
            _text.Paste(sb.ToString());   // one change, so one Control+Z undoes it
            _text.Select(Math.Min(caret, _text.TextLength), 0);
            _text.ScrollToCaret();
            var on = lines.Count == 1 ? $"line {lines[0]}" : lines.Count <= 8 ? $"lines {string.Join(", ", lines.Take(lines.Count - 1))} and {lines[^1]}" : $"{lines.Count} lines, from {lines[0]} to {lines[^1]}";
            _say($"Replaced {found.Count}, on {on}. Control+Z in the text undoes it.");
        };
        f.ShowDialog(this);
        _text.Focus();
    }
}
