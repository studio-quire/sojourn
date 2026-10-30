using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Text.Json.Nodes;
using Sojourn.Session.Assistant;
using Sojourn.Session.Login;

namespace Sojourn.Launcher;

/// <summary>
/// The launcher's one window: the account (saved ones to pick, or a new one), the password unless it's remembered,
/// where to start; behind the gear the headset, graphics and the assistant (its provider, the key, OpenRouter's model
/// and data setting, auto mode: chosen only here); Launch, then the run's state in its place, and the grid's terms or
/// MFA code answered in the card while the client waits. Little text: names, fields and a word or two of state.
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
    bool _keyForeign;   // the key box holds text that didn't come from the person's own press
    string? _forgetArmed;
    long _forgetArmedAt;
    string? _logPath;
    string? _failed;   // the grid said no: the run is stopped and the reason stays at the foot
    bool _arrived;     // the run got in world, so the client kept the account (and its password if asked)
    DateTime _noteUntil = DateTime.MaxValue;
    long _forgetPressedAt = -10_000;
    string? _asked;    // the question last answered from here (its JSON): Continue waits for the next one

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
            rb.Checked += (_, _) => { if (!_loading) { _settings.Provider = value; ShowAssistant(); } };
            ProviderTrack.Children.Add(rb);
        }
        // OpenRouter's list arrives off this thread (a fetch at most every 3 days, only once OpenRouter is chosen)
        OpenRouterCatalog.Changed += () => Dispatcher.BeginInvoke(() => { if (ModelList.IsOpen && !_typedList) ShowModels(null); ShowModelName(); });
        ModelBox.TextChanged += (_, _) =>
        {
            ShowModelName();
            // typing: the ids that match, from everything OpenRouter lists that can act (any id is still taken as typed)
            if (_loading || !ModelBox.IsKeyboardFocusWithin) return;
            var typed = ModelBox.Text.Trim();
            var hits = typed.Length >= 2 && typed != OpenRouterCatalog.ClaudeName ? OpenRouterCatalog.Matching(typed, 8) : new();
            if (hits.Count > 0 && !(hits.Count == 1 && hits[0].Id == typed)) ShowModels(hits);
            else if (_typedList) ModelList.IsOpen = false;
        };
        KeyBox.PasswordChanged += (_, _) =>
        {
            // text in the box not from the person's own keys or mouse (UI Automation setting it, posted characters)
            // spoils what's there: the person's Enter mustn't keep a key someone else put in
            _keyForeign = KeyBox.Password.Length > 0 && (_keyForeign || !RealPress.Now());
            Mark.SetWrong(KeyBox, false);
            KeyHint.Visibility = KeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            TryKeep(enter: false);
        };
        KeyBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { TryKeep(enter: true); e.Handled = true; } };
        ForgetKey.Click += (_, _) => ForgetTheKey();
        ModelBox.LostKeyboardFocus += (_, _) => CommitModel();
        ModelBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitModel(); e.Handled = true; } };
        // a popup that doesn't stay open closes on the mouse-down over ▾ itself, before its click: that click mustn't reopen it
        ModelList.Closed += (_, _) => _modelListClosedAt = Environment.TickCount64;
        ModelListKey.Click += (_, _) =>
        {
            if (ModelList.IsOpen && !_typedList) ModelList.IsOpen = false;
            else if (Environment.TickCount64 - _modelListClosedAt > 300) ShowModels(null);
        };
        foreach (var sw in new[] { AutoSwitch, DenyDataSwitch, AgreeSwitch })
        {
            sw.Checked += (_, _) => Switched(sw);
            sw.Unchecked += (_, _) => Switched(sw);
        }
        CodeBox.TextChanged += (_, _) => { Mark.SetWrong(CodeBox, false); UpdateContinue(); };
        CodeBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        CodeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Answer(accept: true); e.Handled = true; } };
        ContinueKey.Click += (_, _) => Answer(accept: true);
        DeclineKey.Click += (_, _) => Answer(accept: false);

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
        // a remembered password: the field greyed out and closed, saying so; turning remember off forgets it and opens it
        bool kept = _account is { Remembered: true };
        PassBox.IsEnabled = !kept;
        PassField.Opacity = kept ? 0.6 : 1;
        PassHint.Text = kept ? "password remembered" : "password";
        PassHint.Visibility = kept || PassBox.SecurePassword.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        ModelBox.Text = Shown(_settings.OpenRouterModel);
        AutoSwitch.IsChecked = _settings.AutoMode;
        DenyDataSwitch.IsChecked = _settings.OpenRouterDenyData;
        _loading = false;
        ShowAssistant();
    }

    // ---- the assistant: its key, OpenRouter's model and data setting ----

    string ProviderNow => _settings.Provider ?? SettingsFile.Providers[0].Value;

    /// <summary>The rows under the provider as they stand: the key's hint (kept or not, never the key), forgetting it,
    /// and OpenRouter's own rows.</summary>
    void ShowAssistant()
    {
        var kind = ProviderNow;
        bool keyed = kind != "claude";
        bool kept = keyed && ProviderKeys.Has(kind);
        bool armed = Environment.TickCount64 - _forgetPressedAt < 4000;
        KeyHint.Text = !keyed ? "API key" : kept ? (armed ? "forget the key?" : $"{ProviderKeys.Name(kind)} key kept") : $"{ProviderKeys.Name(kind)} key";
        // a kept key: the box greyed out and closed, as a remembered password; × (twice) forgets it and opens it again
        KeyBox.IsEnabled = !kept;
        KeyBox.Opacity = kept ? 0.6 : 1;
        KeyHint.Foreground = (Brush)FindResource(armed && kept ? "Coral" : "Faint");
        KeyField.Visibility = keyed ? Visibility.Visible : Visibility.Collapsed;   // a Claude account takes no key
        ForgetKey.Visibility = kept ? Visibility.Visible : Visibility.Collapsed;
        ForgetKey.Foreground = (Brush)FindResource(armed ? "Coral" : "Faint");
        bool openRouter = kind == "openrouter";
        ModelRow.Visibility = openRouter ? Visibility.Visible : Visibility.Collapsed;
        if (openRouter) OpenRouterCatalog.Refresh();
        DenyDataSwitch.Visibility = openRouter ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A key in the box (pasted, or Enter): kept encrypted if it's an Anthropic or OpenRouter key, its provider
    /// chosen, the box cleared. Only from the person's own keys or mouse (RealPress); never shown back or logged.</summary>
    void TryKeep(bool enter)
    {
        if (_loading || KeyBox.Password.Length == 0) return;
        if (!RealPress.Now()) return;   // (not the person's own keys: nothing kept until they press Enter)
        if (_keyForeign)
        {
            KeyBox.Clear();
            Note("paste the key again", null, TimeSpan.FromSeconds(8), alarm: true);
            return;
        }
        var text = KeyBox.Password;
        if (ProviderKeys.Recognise(text) == null)
        {
            if (enter) Mark.SetWrong(KeyBox, true);
            return;
        }
        string? kind;
        try { kind = ProviderKeys.Keep(text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { kind = null; }
        KeyBox.Clear();
        if (kind == null) { Note("couldn't keep the key", null, TimeSpan.FromSeconds(8), alarm: true); return; }
        _settings.Provider = kind;
        LoadSettings();
        Note($"{ProviderKeys.Name(kind)} key kept", null, TimeSpan.FromSeconds(8));
    }

    /// <summary>× twice within four seconds: the provider's key deleted.</summary>
    void ForgetTheKey()
    {
        var kind = ProviderNow;
        if (!ProviderKeys.Has(kind)) return;
        if (Environment.TickCount64 - _forgetPressedAt >= 4000)
        {
            _forgetPressedAt = Environment.TickCount64;
            ShowAssistant();
            return;
        }
        _forgetPressedAt = -10_000;
        ProviderKeys.Forget(kind);
        ShowAssistant();
        Note($"{ProviderKeys.Name(kind)} key forgotten", null, TimeSpan.FromSeconds(8));
    }

    /// <summary>OpenRouter's model as typed (any id) or picked from the list; empty: Claude's three.</summary>
    void CommitModel()
    {
        if (_loading) return;
        var typed = ModelBox.Text.Trim();
        var id = typed.Length == 0 || typed == OpenRouterCatalog.ClaudeName ? OpenRouterCatalog.Claude : typed;
        if (ModelBox.Text != Shown(id)) ModelBox.Text = Shown(id);
        if (id != _settings.OpenRouterModel) _settings.OpenRouterModel = id;
    }

    bool _typedList;   // the list shows what matches the typing, not the quick list

    /// <summary>In the box: a model's tag, or Claude's three by name.</summary>
    static string Shown(string id) => id == OpenRouterCatalog.Claude ? OpenRouterCatalog.ClaudeName : id;

    /// <summary>Under the box, the other half: a tag's name, or Claude's three tags (an id OpenRouter doesn't list: nothing).</summary>
    long _modelListClosedAt = -10_000;

    void ShowModelName()
    {
        var typed = ModelBox.Text.Trim();
        // Claude's three are special: their name says it all, no tags (the user, 2026-10-30)
        ModelName.Text = typed.Length == 0 || typed == OpenRouterCatalog.ClaudeName ? "" : OpenRouterCatalog.NameOf(typed) ?? "";
        ModelName.Visibility = ModelName.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The list under the box: Claude's three and OpenRouter's families' current models (hits null), or the
    /// models matching what's typed. Each row its tag, its name under it.</summary>
    void ShowModels(List<OpenRouterCatalog.Model>? hits)
    {
        FillModels(hits);
        ModelList.IsOpen = ModelChoices.Children.Count > 0;
    }

    void FillModels(List<OpenRouterCatalog.Model>? hits)
    {
        _typedList = hits != null;
        ModelChoices.Children.Clear();
        var rows = hits?.Select(m => (m.Id, Top: m.Id, Under: m.Name))
            ?? new[] { (Id: OpenRouterCatalog.Claude, Top: OpenRouterCatalog.ClaudeName, Under: "") }
                .Concat(OpenRouterCatalog.Quick.Select(m => (m.Id, Top: m.Id, Under: m.Name)));
        foreach (var (id, top, under) in rows)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = top, FontSize = 14, Foreground = (Brush)FindResource("Ink"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (under.Length > 0) text.Children.Add(new TextBlock { Text = under, FontSize = 12, Foreground = (Brush)FindResource("Faint"), TextTrimming = TextTrimming.CharacterEllipsis });
            var row = new Button { Style = (Style)FindResource("Row"), Margin = new Thickness(0), Content = text, Focusable = false };
            System.Windows.Automation.AutomationProperties.SetName(row, under.Length == 0 ? top : top == id ? $"{id}, {under}" : $"{top}, {under}");
            row.Click += (_, _) => { ModelList.IsOpen = false; ModelBox.Text = Shown(id); CommitModel(); };
            ModelChoices.Children.Add(row);
        }
    }

    /// <summary>For the pictures: the list's card as it opens from ▾, drawn on its own (a popup isn't in the view).</summary>
    internal FrameworkElement ModelListShot()
    {
        FillModels(null);
        ModelList.Child = null;
        return ModelListCard;
    }

    void Switched(CheckBox sw)
    {
        if (sw == AgreeSwitch) { UpdateContinue(); return; }
        if (_loading) return;
        if (sw == AutoSwitch) _settings.AutoMode = sw.IsChecked == true;
        else _settings.OpenRouterDenyData = sw.IsChecked == true;
    }

    // ---- the grid's question at the desk (the client takes the answer through ClientRun's pipe) ----

    RunStatus? _questionShown;

    /// <summary>The terms or the code in the card while the client waits for them, if it takes answers from here.</summary>
    void ShowQuestion(RunStatus s)
    {
        bool show = s.Answers && s.Question is not null && s.Phase is Phase.Terms or Phase.Code;
        var key = show ? s.Question!.ToJsonString() : null;
        bool fresh = show && key != _questionShown?.Question?.ToJsonString();
        _questionShown = show ? s : null;
        QuestionPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Pages.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show) { _asked = null; return; }
        if (!fresh) return;
        bool terms = s.Phase == Phase.Terms;
        TermsBlock.Visibility = terms ? Visibility.Visible : Visibility.Collapsed;
        CodeBlock.Visibility = terms ? Visibility.Collapsed : Visibility.Visible;
        DeclineKey.Visibility = terms ? Visibility.Visible : Visibility.Collapsed;
        if (terms)
        {
            bool tos = (string?)s.Question!["kind"] == "tos";
            TermsTitle.Text = tos ? "Terms of Service" : "From the grid";
            TermsText.Text = (string?)s.Question["text"] is { Length: > 0 } t ? t : tos ? "lindenlab.com/tos" : "";
            AgreeSwitch.Visibility = tos ? Visibility.Visible : Visibility.Collapsed;
            AgreeSwitch.IsChecked = false;
        }
        else
        {
            CodeBox.Clear();
            int asks = (int?)s.Question!["asks"] ?? 1;
            CodeAgain.Visibility = asks > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (asks > 1) Mark.SetWrong(CodeBox, true);
            if (!Places.Background) CodeBox.Focus();
        }
        _asked = null;
        UpdateContinue();
    }

    void UpdateContinue()
    {
        if (_questionShown is not { } s) return;
        bool sent = _asked != null;
        ContinueKey.IsEnabled = !sent && (s.Phase == Phase.Terms
            ? (string?)s.Question?["kind"] != "tos" || AgreeSwitch.IsChecked == true
            : CodeBox.Text.Trim().Length >= 6);
        DeclineKey.IsEnabled = !sent;
        ContinueKey.Opacity = ContinueKey.IsEnabled ? 1 : 0.45;
    }

    /// <summary>Continue (accept, or the code) or Decline, from the person's own press only.</summary>
    void Answer(bool accept)
    {
        if (_questionShown is not { } s || _asked != null) return;
        if (!RealPress.Now()) { Note("press it with the mouse or keyboard", null, TimeSpan.FromSeconds(8), alarm: true); return; }
        JsonObject answer;
        if (s.Phase == Phase.Terms)
        {
            var kind = (string?)s.Question?["kind"] ?? "tos";
            if (accept && kind == "tos" && AgreeSwitch.IsChecked != true) return;
            answer = new JsonObject { ["terms"] = kind, ["accept"] = accept };
        }
        else
        {
            var code = CodeBox.Text.Trim();
            if (!accept || code.Length < 6 || !code.All(char.IsAsciiDigit)) { Mark.SetWrong(CodeBox, true); return; }
            answer = new JsonObject { ["code"] = code, ["remember"] = RememberComputer.IsChecked == true };
            CodeBox.Clear();
        }
        if (!_run.Answer(answer)) { Note($"answer {(s.Headset ? "in the headset" : "in the window")}", null, TimeSpan.FromSeconds(8), alarm: true); return; }
        _asked = s.Question?.ToJsonString();
        UpdateContinue();
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
        bool here = s.Answers && s.Question is not null;   // answered here as well as there
        StatusText.Text = s.Phase switch
        {
            Phase.Starting => "starting",
            Phase.Form => $"log in {where}",
            Phase.Connecting => "logging in",
            Phase.Terms => here ? "terms" : $"terms, {where}",
            Phase.Code => here ? "authenticator code" : $"authenticator code, {where}",
            Phase.Failed => $"didn't log in",
            Phase.World => s.Headset ? "in the headset" : "on the desktop",
            Phase.Ending => "closing",
            _ => "",
        };
        if (s.Phase == Phase.World) _arrived = true;
        ShowQuestion(s);
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
