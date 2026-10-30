using LibreMetaverse;

namespace Sojourn.Access;

/// <summary>Small modal dialogs from standard controls, each opened by the person's own key (so the focus going into
/// them is theirs): a line of text asked for, and a person picked.</summary>
public static class Dialogs
{
    /// <summary>A labelled edit box with OK and Cancel; the text, or null when cancelled or left empty.</summary>
    public static string? Ask(IWin32Window owner, string title, string label, string text = "")
    {
        using var f = Frame(title);
        var box = new TextBox { Name = "answer", AccessibleName = label, Text = text, Width = 360 };
        var t = Table(f, 2);
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        t.Controls.Add(box, 1, 0);
        t.Controls.Add(Buttons(f), 1, 1);
        box.SelectAll();
        f.ActiveControl = box;
        return f.ShowDialog(owner) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    public sealed record Someone(UUID Id, string Name, string Where);

    /// <summary>
    /// Someone picked: the people listed (nearby, friends), narrowed as a name is typed; Enter on the list or in the
    /// box takes the one selected (in the box: the only match, or a full name the grid knows, by find).
    /// </summary>
    public static Someone? Pick(IWin32Window owner, string title, List<Someone> people, Func<string, Task<Someone?>> find)
    {
        using var f = Frame(title);
        var box = new LineBox { Name = "pickName", AccessibleName = "Name", Width = 360 };
        var list = new ListBox { Name = "pickList", AccessibleName = "People", Width = 360, Height = 220, IntegralHeight = false };
        var t = Table(f, 3);
        t.Controls.Add(new Label { Text = "&Name", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        t.Controls.Add(box, 1, 0);
        t.Controls.Add(new Label { Text = "&People", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 1);
        t.Controls.Add(list, 1, 1);
        t.Controls.Add(Buttons(f), 1, 2);
        Someone? chosen = null;
        void Fill()
        {
            var w = box.Text.Trim();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var p in people.Where(p => w.Length == 0 || p.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(new Shown(p));
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            list.EndUpdate();
        }
        Fill();
        box.TextChanged += (_, _) => Fill();
        async void Take(bool fromBox)
        {
            if (list.SelectedItem is Shown s && (!fromBox || list.Items.Count == 1 || s.P.Name.Equals(box.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                chosen = s.P;
            else if (fromBox && box.Text.Trim().Length > 0)
            {
                box.Enabled = false;
                chosen = await find(box.Text.Trim());
                box.Enabled = true;
                if (chosen == null) { box.Focus(); return; }
            }
            if (chosen == null) return;
            f.DialogResult = DialogResult.OK;
        }
        f.AcceptButton = null;
        var ok = (Button)f.Tag!;
        ok.DialogResult = DialogResult.None;
        ok.Click += (_, _) => Take(f.ActiveControl == box);
        box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Take(true); } };
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Take(false); } };
        list.DoubleClick += (_, _) => Take(false);
        f.ActiveControl = list.Items.Count > 0 ? list : box;
        return f.ShowDialog(owner) == DialogResult.OK ? chosen : null;
    }

    sealed record Shown(Someone P)
    {
        public override string ToString() => P.Where.Length > 0 ? $"{P.Name}, {P.Where}" : P.Name;
    }

    static Form Frame(string title) => new()
    {
        Text = title, AccessibleName = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
        StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ShowInTaskbar = false,
        KeyPreview = true,
    };

    static TableLayoutPanel Table(Form f, int rows)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, RowCount = rows, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
        f.Controls.Add(t);
        return t;
    }

    /// <summary>OK and Cancel (Escape); OK is kept in the form's Tag for dialogs that take it over.</summary>
    static FlowLayoutPanel Buttons(Form f)
    {
        var ok = new Button { Name = "ok", Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Name = "cancel", Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.Tag = ok;
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        row.Controls.Add(ok);
        row.Controls.Add(cancel);
        return row;
    }
}

/// <summary>Quitting while in world: "Log out and quit?", Quit (Enter) or Stay (Escape), the focus on Quit.</summary>
static class QuitDialog
{
    public static bool Ask(IWin32Window owner)
    {
        using var f = new Form
        {
            Name = "quit", Text = "Quit Sojourn Access?", AccessibleName = "Quit Sojourn Access?", FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ShowInTaskbar = false, Padding = new Padding(10),
        };
        var quit = new Button { Name = "quitYes", Text = "&Quit", AutoSize = true, DialogResult = DialogResult.OK };
        var stay = new Button { Name = "quitNo", Text = "&Stay", AutoSize = true, DialogResult = DialogResult.Cancel };
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        row.Controls.Add(new Label { Text = "Log out and quit?", AutoSize = true, Anchor = AnchorStyles.Left });
        row.Controls.Add(quit);
        row.Controls.Add(stay);
        f.Controls.Add(row);
        f.AcceptButton = quit;
        f.CancelButton = stay;
        f.ActiveControl = quit;
        return f.ShowDialog(owner) == DialogResult.OK;
    }
}
