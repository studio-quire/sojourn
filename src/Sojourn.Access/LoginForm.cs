using Sojourn.Core.Access;
using Sojourn.Session.Assistant;

namespace Sojourn.Access;

/// <summary>What the person chose on the login form.</summary>
public sealed record LoginChoice(string Grid, string First, string Last, string Password, string Start)
{
    public bool SameAccount(LoginChoice o) =>
        string.Equals(Grid, o.Grid, StringComparison.OrdinalIgnoreCase) && string.Equals(First, o.First, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Last, o.Last, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The login form: grid, username, password, where to start, Log in; every field named for the screen reader, and the
/// login's progress and failures said through it. Grid and Start at are plain choice lists (an editable combo box reads
/// as a combo box and then again as an unnamed edit), their last choice opening a box to type another grid's login URI
/// or a place. The grid's Terms of Service and an MFA code are asked in their own small windows, answered only by the
/// person's own keys (or mouse), never the agent API or the assistant.
/// </summary>
public sealed class LoginForm : Form
{
    readonly ComboBox _grid = new() { Name = "grid", AccessibleName = "Grid", DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    readonly LineBox _gridUri = new() { Name = "gridUri", AccessibleName = "Login URI", Width = 320, Visible = false };
    readonly TextBox _user = new() { Name = "user", AccessibleName = "Username", Width = 320 };
    readonly TextBox _password = new() { Name = "password", AccessibleName = "Password", UseSystemPasswordChar = true, Width = 320 };
    readonly CheckBox _showPassword = new() { Name = "showPassword", Text = "Sho&w password", AutoSize = true };
    readonly ComboBox _start = new() { Name = "start", AccessibleName = "Start at", DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    readonly LineBox _place = new() { Name = "place", AccessibleName = "Place", Width = 320, Visible = false };
    readonly Button _login = new() { Name = "login", Text = "&Log in", AutoSize = true };
    readonly Label _status = new() { Name = "status", AutoSize = true, MaximumSize = new Size(440, 0) };
    readonly Label _gridUriLabel = new() { Text = "Login U&RI", AutoSize = true, Anchor = AnchorStyles.Left, Visible = false };
    readonly Label _placeLabel = new() { Text = "Pl&ace", AutoSize = true, Anchor = AnchorStyles.Left, Visible = false };
    readonly IReaderVoice? _nvda;
    readonly FormVoice _uia;
    readonly Func<bool> _hello;
    bool _busy;
    const string LocalLabel = "Local grid (127.0.0.1:9700)", OtherGrid = "Another grid", OtherPlace = "Another place";

    /// <summary>A choice in a list: its words, and what it means.</summary>
    sealed record Choice(string Words, string Value)
    {
        public override string ToString() => Words;
    }

    public event Action<LoginChoice>? Chosen;
    public LoginChoice? Last { get; private set; }

    public LoginForm(AccessOptions opt, AccessSettings settings)
    {
        Name = "login";
        Text = "Sojourn Access: log in";
        AccessibleName = "Sojourn Access: log in";
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Padding = new Padding(12);
        AcceptButton = _login;

        // the grids known (Second Life, the local one where offered, one given or used before), then another typed in;
        // a packaged build starts on Second Life, the local grid only with --local
        var last = settings.LastGrid is { } lg && (opt.LocalOffered || !SameUri(lg, AccessOptions.LocalGrid)) ? lg : null;
        var grid = AccessOptions.GridFor(opt.Grid ?? last ?? (opt.LocalOffered ? AccessOptions.LocalGrid : AccessOptions.SecondLife));
        _grid.Items.Add(new Choice("Second Life", AccessOptions.SecondLife));
        if (opt.LocalOffered) _grid.Items.Add(new Choice(LocalLabel, AccessOptions.LocalGrid));
        int at = _grid.Items.Cast<Choice>().ToList().FindIndex(c => SameUri(grid, c.Value));
        if (at < 0) { _grid.Items.Add(new Choice(grid, grid)); at = _grid.Items.Count - 1; }
        _grid.Items.Add(new Choice(OtherGrid, ""));
        _grid.SelectedIndex = at;
        _user.Text = opt.User ?? settings.LastUser ?? "";
        _password.Text = opt.Password ?? "";
        // where to start: last, home, a place given on the command line, or another typed in
        var start = AccessOptions.StartFor(opt.Start ?? settings.LastStart ?? "last");
        _start.Items.Add(new Choice("My last location", "last"));
        _start.Items.Add(new Choice("Home", "home"));
        if (start is not ("last" or "home")) _start.Items.Add(new Choice(PlaceWords(start), start));
        _start.Items.Add(new Choice(OtherPlace, ""));
        _start.SelectedIndex = start switch { "last" => 0, "home" => 1, _ => 2 };

        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        int tab = 0;
        void Row(Label label, Control c)
        {
            // the label's mnemonic (Alt+letter) moves to its field, the one after it in the tab order; the field carries
            // its own accessible name too
            label.TabIndex = tab++;
            c.TabIndex = tab++;
            table.Controls.Add(label);
            table.Controls.Add(c);
        }
        static Label Named(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, UseMnemonic = true };
        Row(Named("&Grid"), _grid);
        Row(_gridUriLabel, _gridUri);
        Row(Named("&Username"), _user);
        Row(Named("&Password"), _password);
        Row(new Label { AutoSize = true }, _showPassword);
        Row(Named("&Start at"), _start);
        Row(_placeLabel, _place);
        Row(new Label { AutoSize = true }, _login);
        Row(new Label { AutoSize = true }, _status);
        // the version on the login screen (TPV policy 1.g), not in the title, which the screen reader says at every start
        _status.Text = $"Sojourn Access {AppInfo.Version}";
        Controls.Add(table);

        _grid.SelectedIndexChanged += (_, _) => _gridUri.Visible = _gridUriLabel.Visible = Picked(_grid) == "";
        _start.SelectedIndexChanged += (_, _) => _place.Visible = _placeLabel.Visible = Picked(_start) == "";
        _showPassword.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_showPassword.Checked;

        _uia = new FormVoice(this);
        _hello = () => settings.ConfirmByHello;
        _nvda = NvdaVoice.Load(new[] { AppContext.BaseDirectory, Path.Combine(Sidecar.Root ?? AppContext.BaseDirectory, "local", "nvda", "x64") });
        _login.Click += (_, _) => Choose();
        // the first focus where the person types next, so the screen reader doesn't start on Grid and then move
        ActiveControl = _user.Text.Length == 0 ? _user : _password.Text.Length == 0 ? _password : _login;
        Shown += (_, _) => { if (opt.AutoLogin) BeginInvoke(Choose); };
    }

    static string Picked(ComboBox c) => (c.SelectedItem as Choice)?.Value ?? "";

    static bool SameUri(string a, string b) => string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>"uri:Atelier&amp;64&amp;36&amp;24" as "Atelier, 64, 36, 24".</summary>
    static string PlaceWords(string start) => start.StartsWith("uri:", StringComparison.OrdinalIgnoreCase)
        ? string.Join(", ", start[4..].Split('&'))
        : start;

    /// <summary>Through NVDA's API when NVDA runs, else a UI Automation notification from this window.</summary>
    public void Say(string text)
    {
        _status.Text = text;
        if (_nvda is NvdaVoice n && ScreenReaders.Running(n) == "NVDA") n.Speak(text, interrupt: true);
        else _uia.Speak(text, interrupt: true);
    }

    void Choose()
    {
        if (_busy) { Say("Logging in, a moment."); return; }
        var grid = Picked(_grid);
        if (grid.Length == 0)
        {
            grid = AccessOptions.GridFor(_gridUri.Text);
            if (grid.Length == 0) { Say("Type the grid's login URI first."); _gridUri.Focus(); return; }
        }
        if (AccessOptions.Refused(grid) is { } why) { Say(why); _grid.Focus(); return; }
        var (first, last) = AccessOptions.SplitName(_user.Text);
        if (first.Length == 0) { Say("Type your username first."); _user.Focus(); return; }
        if (_password.Text.Length == 0) { Say("Type your password first."); _password.Focus(); return; }
        var start = Picked(_start);
        if (start.Length == 0)
        {
            if (_place.Text.Trim().Length == 0) { Say("Type the place to start at first: a region's name, or Region/x/y/z."); _place.Focus(); return; }
            start = AccessOptions.StartFor(_place.Text);
        }
        Last = new LoginChoice(grid, first, last, _password.Text, start);
        Busy(true);
        Say($"Logging in as {first}{(last == "Resident" ? "" : " " + last)}.");
        Chosen?.Invoke(Last);
    }

    /// <summary>While logging in, Log in waits. Not disabled: a disabled button gives the focus to Grid, which the screen
    /// reader then reads out over the login's progress.</summary>
    public void Busy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
    }

    /// <summary>The login didn't go: said, and the form ready again (the password field, where a mistake usually is).</summary>
    public void Failed(string words)
    {
        Busy(false);
        Say(words);
        _password.SelectAll();
        _password.Focus();
    }

    /// <summary>The grid's Terms of Service or critical message: true when the person agreed.</summary>
    public bool AskTerms(string kind, string message)
    {
        using var d = new TermsDialog(kind, message, Say, _hello);
        return d.ShowDialog(this) == DialogResult.OK;
    }

    /// <summary>The grid asks for a code from the person's authenticator: the code, or null.</summary>
    public string? AskMfa(bool again)
    {
        using var d = new MfaDialog(again, Say, _hello);
        return d.ShowDialog(this) == DialogResult.OK ? d.Code : null;
    }
}

/// <summary>New Terms of Service or a critical message before login: the text to read, "I agree", Continue only once
/// agreed (as the viewer's floater), Decline. Ticking "I agree" and Continue each take the person's own key or click
/// (RealInput.Confirms: a hardware press, after the focus last moved by anything but them), never a program's toggle,
/// click or invoke; with hello on, Continue from anything else asks Windows Hello. No mnemonics: a character message
/// can't press them.</summary>
sealed class TermsDialog : Form
{
    public TermsDialog(string kind, string message, Action<string> say, Func<bool> hello)
    {
        Name = "terms";
        Text = kind == "tos" ? "Terms of Service" : "A message from the grid";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(620, 480);
        MinimizeBox = MaximizeBox = false;
        var intro = kind == "tos"
            ? "The grid's Terms of Service have changed since this account last agreed. Read them (also at lindenlab.com/tos), then agree to log in."
            : "The grid has a message you must read and accept before logging in.";
        var text = new TextBox
        {
            Name = "termsText", AccessibleName = Text, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Text = intro + "\r\n\r\n" + (message ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
        };
        var agree = new CheckBox { Name = "agree", Text = "I agree", AutoSize = true, UseMnemonic = false };
        var go = new Button { Name = "continue", Text = "Continue", Enabled = false, AutoSize = true, UseMnemonic = false };
        var no = new Button { Name = "decline", Text = "Decline", DialogResult = DialogResult.Cancel, AutoSize = true, UseMnemonic = false };
        bool undoing = false;
        agree.CheckedChanged += (_, _) =>
        {
            if (undoing) return;
            // ticked by anything but the person's own press (or anything, with hello on: Continue asks then): undone
            if (agree.Checked && !RealInput.Confirms && !hello())
            {
                undoing = true;
                agree.Checked = false;
                undoing = false;
                say(RealInput.Why);
            }
            go.Enabled = agree.Checked;
        };
        go.Click += (_, _) =>
        {
            if (!agree.Checked) return;
            if (RealInput.Confirms) { Agreed(); return; }
            Hello.Instead(hello(), Handle, "agree to the grid's terms and log in.", RealInput.Why, say, Agreed);
        };
        void Agreed()
        {
            if (!agree.Checked || IsDisposed) return;
            DialogResult = DialogResult.OK;
            Close();
        }
        var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        row.Controls.AddRange(new Control[] { agree, go, no });
        Controls.Add(text);
        Controls.Add(row);
        CancelButton = no;
        text.TabIndex = 0; agree.TabIndex = 1; go.TabIndex = 2; no.TabIndex = 3;
        Shown += (_, _) => text.Focus();
    }
}

/// <summary>The grid's multi-factor question: a code from the person's authenticator app, typed by their own keys
/// and sent by their own Enter or click (a screen reader's passed-on Enter counts); with hello on, anything else asks
/// Windows Hello.</summary>
sealed class MfaDialog : Form
{
    readonly TextBox _code = new() { Name = "code", AccessibleName = "Code from your authenticator", Width = 160 };
    bool _foreign;
    public string Code => _code.Text.Trim();

    public MfaDialog(bool again, Action<string> say, Func<bool> hello)
    {
        Name = "mfa";
        Text = again ? "That code didn't work: try another" : "The code from your authenticator";
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        Padding = new Padding(12);
        var ok = new Button { Name = "ok", Text = "OK", AutoSize = true };
        var cancel = new Button { Name = "cancel", Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        _code.TextChanged += (_, _) =>
        {
            if (_code.TextLength == 0) _foreign = false;
            else if (!RealInput.Person) _foreign = true;
        };
        ok.Click += (_, _) =>
        {
            if (Code.Length == 0) { say("Type the code first."); return; }
            if (RealInput.Text && !_foreign) { Sent(); return; }
            var code = Code;
            Hello.Instead(hello(), Handle, "send the code to the grid.",
                _foreign ? "Something other than your keys changed the code. Clear it and type it again." : RealInput.Why, say,
                () => { if (Code == code) Sent(); });
        };
        void Sent()
        {
            if (IsDisposed) return;
            DialogResult = DialogResult.OK;
            Close();
        }
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        row.Controls.AddRange(new Control[] { new Label { Text = "&Code", AutoSize = true }, _code, ok, cancel });
        Controls.Add(row);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
