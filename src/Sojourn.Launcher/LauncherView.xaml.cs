using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Sojourn.Session.Login;

namespace Sojourn.Launcher;

/// <summary>
/// The launcher's one window: the account (saved ones to pick, or a new one), the password unless it's remembered,
/// where to start; behind the gear the headset, graphics and the assistant's provider; Launch, then the run's state
/// in its place; Sojourn Access at the foot. Little text: names, fields and a word or two of state.
/// Written by Claude Opus 5.5, 2026-10-29.
/// </summary>
public partial class LauncherView : UserControl
{
    readonly Places _places;
    readonly LoginStore _store;
    readonly SettingsFile _settings;
    readonly ClientRun _run;
    readonly bool _desktop;
    readonly IReadOnlyList<string> _extra;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    List<SavedAccount> _accounts = new();
    SavedAccount? _account;   // null: a new one
    bool _settingsOpen, _listOpen, _loading;
    string? _forgetArmed;
    long _forgetArmedAt;
    string? _logPath;
    string? _failed;   // the grid said no: the run is stopped and the reason stays at the foot
    bool _arrived;     // the run got in world, so the client kept the account (and its password if asked)
    DateTime _noteUntil = DateTime.MaxValue;

    public event Action? CloseRequested, MinimiseRequested;

    internal LauncherView(Places places, LoginStore store, bool desktop, IReadOnlyList<string> extra)
    {
        InitializeComponent();
        _places = places;
        _store = store;
        _settings = new SettingsFile(places.SettingsPath);
        _run = new ClientRun(places);
        _desktop = desktop;
        _extra = extra;

        foreach (var (value, label) in SettingsFile.Providers)
        {
            var rb = new RadioButton { GroupName = "provider", Style = (Style)FindResource("Segment"), Content = label, Tag = value };
            rb.Checked += (_, _) => { if (!_loading) _settings.Provider = value; };
            ProviderTrack.Children.Add(rb);
        }

        CloseKey.Click += (_, _) => CloseRequested?.Invoke();
        MinKey.Click += (_, _) => MinimiseRequested?.Invoke();
        AccountKey.Click += (_, _) => { if (_accounts.Count > 0) ShowList(true); };
        NewAccountKey.Click += (_, _) => { Pick(null); ShowList(false); };
        GridOther.Checked += (_, _) => UriBox.Visibility = Visibility.Visible;
        GridSl.Checked += (_, _) => UriBox.Visibility = Visibility.Collapsed;
        GridBeta.Checked += (_, _) => UriBox.Visibility = Visibility.Collapsed;
        StartPlace.Checked += (_, _) => PlaceBox.Visibility = Visibility.Visible;
        StartLast.Checked += (_, _) => PlaceBox.Visibility = Visibility.Collapsed;
        StartHome.Checked += (_, _) => PlaceBox.Visibility = Visibility.Collapsed;
        PassBox.PasswordChanged += (_, _) => { UpdatePassHint(); Mark.SetWrong(PassBox, false); };
        foreach (var box in new[] { UserBox, UriBox, PlaceBox }) box.TextChanged += (s, _) => Mark.SetWrong((DependencyObject)s, false);
        RememberSwitch.Unchecked += (_, _) =>
        {
            // as the client's login sheet: turning it off forgets a kept password at once
            if (!_loading && _account is { Remembered: true } a) { _store.ForgetPassword(a.Id); Reload(a.Id); }
        };
        Standing.Checked += (_, _) => { if (!_loading) _settings.Seated = false; };
        Seated.Checked += (_, _) => { if (!_loading) _settings.Seated = true; };
        GfxLight.Checked += (_, _) => Graphics("light");
        GfxBalanced.Checked += (_, _) => Graphics("balanced");
        GfxFull.Checked += (_, _) => Graphics("full");
        SettingsKey.Click += (_, _) => ShowSettings(!_settingsOpen);
        LaunchKey.Click += (_, _) => Launch();
        StopKey.Click += (_, _) => { _run.Stop(); ShowStatus(_run.Status); };
        AccessKey.Click += (_, _) => OpenAccess();
        LogKey.Click += (_, _) => OpenLog();
        _timer.Tick += (_, _) => Tick();

        Reload(_store.LastId);
        LoadSettings();
        ShowStatus(new RunStatus(Phase.Idle));
    }

    /// <summary>Puts the keys in the first field that wants typing (never when started in the background).</summary>
    public void FocusFirst()
    {
        if (Places.Background) return;
        if (_account == null) UserBox.Focus();
        else if (!_account.Remembered) PassBox.Focus();
        else LaunchKey.Focus();
    }

    // ---- the accounts ----

    void Reload(string? select)
    {
        _accounts = _store.Accounts().ToList();
        BuildList();
        Pick(_accounts.FirstOrDefault(a => a.Id == select) ?? _accounts.FirstOrDefault());
    }

    void Pick(SavedAccount? a)
    {
        _loading = true;
        _account = a;
        NewBlock.Visibility = a == null ? Visibility.Visible : Visibility.Collapsed;
        AccountKey.Visibility = _accounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (a != null)
        {
            AccountName.Text = a.Name;
            AccountName.Foreground = (Brush)FindResource("Ink");
            AccountGrid.Text = Grids.IsSecondLife(a.Grid) && a.Grid == Grids.Main ? "" : Grids.Label(a.Grid, a.GridName);
            AccountGrid.Visibility = AccountGrid.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            RememberSwitch.IsChecked = a.Remembered;
            (a.Start switch { "last" => StartLast, "home" => StartHome, _ => StartPlace }).IsChecked = true;
            PlaceBox.Text = Starts.PlaceText(a.Start);
        }
        else
        {
            AccountName.Text = "new account";
            AccountName.Foreground = (Brush)FindResource("Dim");
            AccountGrid.Visibility = Visibility.Collapsed;
            RememberSwitch.IsChecked = false;
            UserBox.Text = "";
            GridSl.IsChecked = true;
            StartLast.IsChecked = true;
            PlaceBox.Text = "";
        }
        PassBox.Clear();
        UpdatePassHint();
        _loading = false;
    }

    void UpdatePassHint()
    {
        PassHint.Text = _account is { Remembered: true } ? "remembered" : "password";
        PassHint.Visibility = PassBox.SecurePassword.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void BuildList()
    {
        AccountList.Children.Clear();
        foreach (var a in _accounts)
        {
            var row = new Grid { Margin = new Thickness(-14, 0, -14, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = a.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Ink") });
            if (a.Grid != Grids.Main)
                text.Children.Add(new TextBlock { Text = Grids.Label(a.Grid, a.GridName), FontSize = 12, Foreground = (Brush)FindResource("Faint") });
            var pick = new Button { Style = (Style)FindResource("Row"), Content = text };
            System.Windows.Automation.AutomationProperties.SetName(pick, a.Name);
            pick.Click += (_, _) => { Pick(a); ShowList(false); };
            bool armed = _forgetArmed == a.Id && Environment.TickCount64 - _forgetArmedAt < 4000;
            var forget = new Button
            {
                Style = (Style)FindResource("Key"), Content = armed ? "" : "", FontSize = armed ? 14 : 11, Margin = new Thickness(4, 0, 8, 0),
                Foreground = armed ? (Brush)FindResource("Coral") : (Brush)FindResource("Faint"), VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(forget, armed ? $"Forget {a.Name}: press again" : $"Forget {a.Name}");
            forget.Click += (_, _) =>
            {
                if (_forgetArmed == a.Id && Environment.TickCount64 - _forgetArmedAt < 4000)
                {
                    _store.Forget(a.Id);
                    _forgetArmed = null;
                    var keep = _account?.Id == a.Id ? null : _account?.Id;
                    Reload(keep);
                    if (_accounts.Count == 0) ShowList(false);
                }
                else { _forgetArmed = a.Id; _forgetArmedAt = Environment.TickCount64; BuildList(); }
            };
            row.Children.Add(pick);
            Grid.SetColumn(forget, 1);
            row.Children.Add(forget);
            AccountList.Children.Add(row);
        }
    }

    void ShowList(bool open)
    {
        _listOpen = open;
        if (open) { _settingsOpen = false; BuildList(); }
        UpdatePages();
    }

    void ShowSettings(bool open)
    {
        _settingsOpen = open;
        if (open) { _listOpen = false; LoadSettings(); }
        UpdatePages();
    }

    void UpdatePages()
    {
        LoginPage.Visibility = !_settingsOpen && !_listOpen ? Visibility.Visible : Visibility.Collapsed;
        AccountsPage.Visibility = _listOpen ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
        SettingsKey.Content = _settingsOpen ? "" : "";
        System.Windows.Automation.AutomationProperties.SetName(SettingsKey, _settingsOpen ? "Back" : "Settings");
    }

    // ---- settings ----

    void LoadSettings()
    {
        _loading = true;
        (_settings.Seated ? Seated : Standing).IsChecked = true;
        var g = _settings.Exists ? _settings.Graphics : "balanced";
        GfxYours.Visibility = g == "yours" ? Visibility.Visible : Visibility.Collapsed;
        (g switch { "light" => GfxLight, "balanced" => GfxBalanced, "full" => GfxFull, _ => GfxYours }).IsChecked = true;
        var provider = _settings.Provider ?? SettingsFile.Providers[0].Value;
        foreach (RadioButton rb in ProviderTrack.Children) rb.IsChecked = (string)rb.Tag == provider;
        _loading = false;
    }

    void Graphics(string preset)
    {
        if (_loading) return;
        _settings.Graphics = preset;
        GfxYours.Visibility = Visibility.Collapsed;
    }

    // ---- launching ----

    void Launch()
    {
        if (_run.Running) return;
        SavedAccount account;
        string? hash;
        if (_account is { } a)
        {
            hash = PasswordHash.Of(PassBox.SecurePassword) ?? LoginStore.HashOf(a);
            if (hash == null) { Wrong(PassBox); return; }
            account = a;
        }
        else
        {
            var name = UserBox.Text.Trim().Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (name.Length is 0 or > 2) { Wrong(UserBox); return; }
            string grid = GridSl.IsChecked == true ? Grids.Main : GridBeta.IsChecked == true ? Grids.Beta : Grids.Normalise(UriBox.Text) ?? "";
            if (grid.Length == 0) { Wrong(UriBox); return; }
            hash = PasswordHash.Of(PassBox.SecurePassword);
            if (hash == null) { Wrong(PassBox); return; }
            account = new SavedAccount { First = name[0], Last = name.Length > 1 ? name[1] : "Resident", Grid = grid };
        }
        var start = Starts.Of(StartLast.IsChecked == true ? 0 : StartHome.IsChecked == true ? 1 : 2, PlaceBox.Text);
        if (start == null) { Wrong(PlaceBox); return; }
        account = account with { Start = start };

        _settings.FirstStart();
        _store.HandOver(account, hash, RememberSwitch.IsChecked == true);
        hash = null;
        PassBox.Clear();
        var failed = _run.Start(_desktop, _extra);
        if (failed != null)
        {
            _store.DropHandOver();
            Note(failed, null);
            return;
        }
        _failed = null;
        _timer.Start();
        ShowSettings(false);
        ShowList(false);
        ShowStatus(_run.Status);
    }

    void Wrong(Control field)
    {
        Mark.SetWrong(field, true);
        if (!Places.Background) field.Focus();
    }

    void Tick()
    {
        if (_run.Poll()) ShowStatus(_run.Status);
        if (!_run.Running)
        {
            _timer.Stop();
            if (_arrived) Reload(_store.LastId);   // the client keeps a login that worked
            _arrived = false;
            if (_failed != null && _account is not { Remembered: true }) Mark.SetWrong(PassBox, true);
        }
        if (DateTime.UtcNow > _noteUntil) Note(null, null);
    }

    /// <summary>The run's state where Launch was (a word, an orb), or Launch again with a note at the foot.</summary>
    internal void ShowStatus(RunStatus s)
    {
        bool running = s.Phase is not (Phase.Idle or Phase.Ended or Phase.Crashed);
        LaunchKey.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StatusPill.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        Pages.IsEnabled = !running;
        Pages.Opacity = running ? 0.55 : 1;
        SettingsKey.IsEnabled = !running;
        string where = s.Headset ? "in the headset" : "in the window";
        StatusText.Text = s.Phase switch
        {
            Phase.Starting => "starting",
            Phase.Form => $"log in {where}",
            Phase.Connecting => "logging in",
            Phase.Terms => $"terms, {where}",
            Phase.Code => $"code, {where}",
            Phase.Failed => $"didn't log in",
            Phase.World => s.Headset ? "in the headset" : "on the desktop",
            Phase.Ending => "closing",
            _ => "",
        };
        if (s.Phase == Phase.World) _arrived = true;
        bool waiting = s.Phase is Phase.Terms or Phase.Code or Phase.Form;
        bool busy = s.Phase is Phase.Starting or Phase.Connecting or Phase.Ending;
        Orb.Fill = (Brush)FindResource(s.Phase == Phase.Failed ? "Coral" : "Accent");
        OrbGlow.Color = s.Phase == Phase.Failed ? Color.FromRgb(0xB8, 0x5A, 0x52) : (Color)FindResource("AccentColor");
        Orb.BeginAnimation(OpacityProperty, waiting || busy
            ? new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(waiting ? 0.7 : 1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() }
            : null);
        switch (s.Phase)
        {
            case Phase.Failed:
                // the reason here, the form back to fix it: the client's own sheet (Back, Try again) is for a login
                // that fails later, in the headset
                _failed = s.Detail ?? "didn't log in";
                Note(_failed, null, alarm: true);
                _run.Stop();
                break;
            case Phase.Crashed: Note(s.Detail ?? "stopped unexpectedly", s.Log, alarm: true); break;
            case Phase.Ended when _failed != null: Note(_failed, null, alarm: true); break;
            case Phase.Ended: Note("ended", null, TimeSpan.FromSeconds(8)); break;
            case Phase.Idle: Note(null, null); break;
            default: if (running) Note(null, null); break;
        }
    }

    void Note(string? text, string? log, TimeSpan? lasts = null, bool alarm = false)
    {
        _logPath = log;
        _noteUntil = lasts is { } t ? DateTime.UtcNow + t : DateTime.MaxValue;
        NoteText.Text = text ?? $"Sojourn {Places.Version}";
        NoteText.Foreground = (Brush)FindResource(alarm ? "Coral" : "Faint");
        LogKey.Visibility = log != null ? Visibility.Visible : Visibility.Collapsed;
    }

    void OpenLog()
    {
        if (_logPath is not { } p) return;
        try
        {
            if (File.Exists(p)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(p)}\"") { UseShellExecute = false });
        }
        catch (Exception) { }
    }

    void OpenAccess()
    {
        var (psi, missing) = _places.Access();
        if (psi == null) { Note(missing, null, TimeSpan.FromSeconds(8)); return; }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        try { Process.Start(psi); }
        catch (Exception e) { Note(e.Message, null, TimeSpan.FromSeconds(8)); }
    }

    // ---- for the pictures (Shots.cs) ----

    internal void ShowPage(string page)
    {
        switch (page)
        {
            case "accounts": ShowList(true); break;
            case "settings": ShowSettings(true); break;
            case "new": Pick(null); ShowList(false); ShowSettings(false); break;
            default: ShowList(false); ShowSettings(false); break;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_listOpen || _settingsOpen)) { ShowList(false); ShowSettings(false); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
