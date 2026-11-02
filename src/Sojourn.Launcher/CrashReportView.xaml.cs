using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sojourn.Session.Assistant;
using Sojourn.Session.Release;

namespace Sojourn.Launcher;

/// <summary>
/// After the VR client crashes, as the person's crash_reports setting says (menu > about; Firestorm's model): ask (the
/// default) opens this window; always sends the report without asking; never leaves the launcher's note alone. The
/// report (Sojourn.Session/Release/ProblemReport.cs) is the crashed session's log filtered as Sojourn Access's Send a
/// log filters it, with how the run ended and the .NET runtime's last words from its console, and the person's note.
/// Send goes to Studio Quire's relay, with their Studio Quire token if one is kept, else to its anonymous report
/// endpoint; "save instead" keeps it as a file for an issue on GitHub. Only the person's own press sends or saves
/// (RealPress). docs/launcher.md > After a crash. Written by Claude Opus 5.5, 2026-11-01.
/// </summary>
public partial class CrashReportView : UserControl
{
    readonly Places _places;
    readonly RunStatus _status;
    readonly bool _token;
    bool _busy, _done;

    public event Action? CloseRequested;

    internal CrashReportView(Places places, RunStatus status)
    {
        InitializeComponent();
        Words.Remember(this);
        _places = places;
        _status = status;
        _token = ProviderKeys.RelayToken() != null;
        CloseKey.Click += (_, _) => CloseRequested?.Invoke();
        SaveKey.Click += async (_, _) => await GoAsync(save: true);
        SkipKey.Click += (_, _) => CloseRequested?.Invoke();
        ReviewKey.Click += (_, _) => Review();
        SendKey.Click += async (_, _) => await GoAsync(save: false);
        Loaded += (_, _) => { Lang.Changed -= LanguageChanged; Lang.Changed += LanguageChanged; };
        Unloaded += (_, _) => Lang.Changed -= LanguageChanged;
        ShowWords();
    }

    /// <summary>The words code sets, in the person's language (the report itself stays English: ProblemReport).</summary>
    void ShowWords()
    {
        var code = ProblemReport.ExitHex(_status.ExitCode);
        // the run's detail is ours (ClientRun's), and only said when the code has no name of its own
        How.Text = ProblemReport.ExitName(_status.ExitCode) is { } name ? Lang.F("Exit code {0}, {1}.", code, Lang.T(name))
            : _status.Detail is { } d ? Lang.F("Exit code {0}: {1}.", code, Lang.T(d))
            : Lang.F("Exit code {0}.", code);
        WhatsSent.Text = Lang.T(ProblemReport.WhatsSent(_token)) + " " + Lang.T(ProblemReport.GitHubWords);
        if (_done) SendKey.Content = Lang.T("close");
        if (_said != null) Say(_said, _saidAlarm);
        if (Window.GetWindow(this) is { } w) { w.Language = Words.Language; w.Title = Lang.T("Sojourn: a crash report"); }
    }

    void LanguageChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(LanguageChanged); return; }
        Words.Translate(this);
        ShowWords();
    }

    /// <summary>The crash as the report tells it: its exit code and the runtime's last words from the console.</summary>
    ProblemReport.Crash Crash()
    {
        string? words = null;
        try
        {
            if (File.Exists(_places.ConsoleLog)) words = ProblemReport.DeathWords(LogShare.Read(_places.ConsoleLog).Replace("\r", "").Split('\n'));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new ProblemReport.Crash(_status.ExitCode, words);
    }

    /// <summary>The crashed session's log (the one the client named, else the newest that can be sent).</summary>
    LogShare.Session? Log() => ProblemReport.Latest(_places.BaseDir,
        _status.Log is { } l && Path.GetFileName(l).StartsWith("session-", StringComparison.Ordinal) ? l : null);

    internal ProblemReport.Report? Make(string note, out string? why)
    {
        if (Log() is not { } log) { why = "there's no log this version can send"; return null; }
        return ProblemReport.Make(log, note, Crash(), Places.Version, ProblemReport.CommitOf(typeof(Places).Assembly), current: true, out why);
    }

    /// <summary>Why a report can't be made (ProblemReport's English, or ours above), as the person reads it.</summary>
    static string CantBeMade(string? why) => Lang.F("Can't be made: {0}.", Lang.T(why ?? ""));

    void Review()
    {
        if (Make(NoteBox.Text, out var why) is not { } r) { Say(() => CantBeMade(why), alarm: true); return; }
        var text = new TextBox
        {
            Text = r.Log.Text.Replace("\n", "\r\n"), IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10),
            Style = new Style(typeof(TextBox)),   // the plain control, not the theme's one-line well
        };
        var w = new Window
        {
            Title = Lang.F("What a report sends: {0} lines, {1} with words left out", r.Log.Lines, r.Log.LeftOutLines), Content = text, Width = 860, Height = 640,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowActivated = !Places.Background,
            Language = Words.Language,
        };
        w.Show();
    }

    async Task GoAsync(bool save)
    {
        if (_busy) return;
        if (_done) { CloseRequested?.Invoke(); return; }
        if (!RealPress.Now()) { Say(() => Lang.T("press it with the mouse or keyboard"), alarm: true); return; }
        _busy = true;
        SendKey.IsEnabled = SaveKey.IsEnabled = false;
        Say(() => Lang.T(save ? "saving…" : "sending…"));
        var note = NoteBox.Text;
        try
        {
            // the outcome as a way to say it: ProblemReport's sentences are English (the client logs them and the
            // assistant reads them), translated here as they're shown, the relay's own words within them kept as they came
            var (done, outcome) = await Task.Run<(bool, Func<string>)>(async () =>
            {
                if (Make(note, out var why) is not { } r) return (false, () => CantBeMade(why));
                if (!save)
                {
                    var (sent, said) = await ProblemReport.SendAsync(r, ProviderKeys.RelayToken());
                    return (sent, () => Lang.T(said));
                }
                var path = ProblemReport.Save(r, _places.BaseDir);
                ProblemReport.OpenForGitHub(path);
                var file = Path.GetFileName(path);
                return (true, () => Lang.F("Saved as {0} (shown in Explorer). Attach it to a new issue on the page that opened.", file));
            });
            Say(outcome, alarm: !done);
            if (done) { _done = true; SendKey.Content = Lang.T("close"); NoteBox.IsReadOnly = true; SaveKey.Visibility = Visibility.Collapsed; }
        }
        catch (Exception e) { var m = e.Message; Say(() => Lang.F("Not done: {0}", m), alarm: true); }
        finally { _busy = false; SendKey.IsEnabled = SaveKey.IsEnabled = true; }
    }

    Func<string>? _said;
    bool _saidAlarm;

    void Say(Func<string> said, bool alarm = false)
    {
        _said = said;
        _saidAlarm = alarm;
        Outcome.Text = said();
        Outcome.Foreground = (Brush)FindResource(alarm ? "Coral" : "Faint");
        Outcome.Visibility = Visibility.Visible;
    }
}

/// <summary>The crash's window, and what the launcher does after a crash by the person's setting.</summary>
static class CrashReport
{
    static RunStatus? _offered;

    /// <summary>Once per crashed run: ask (this window), always (sent without asking), or never. note: the launcher's
    /// note, when it sent one itself.</summary>
    public static void Offer(FrameworkElement launcher, Places places, SettingsFile settings, RunStatus s, Action<Func<string>> note)
    {
        if (ReferenceEquals(_offered, s) || Window.GetWindow(launcher) is not { } owner) return;   // (pictures: no window)
        _offered = s;
        var how = settings.CrashReports;
        if (how == "never") return;
        if (how == "always")
        {
            var token = ProviderKeys.RelayToken();
            var view = new CrashReportView(places, s);
            _ = Task.Run(async () =>
            {
                var r = view.Make("", out _);
                var (sent, said) = r is null ? (false, "no log to send") : await ProblemReport.SendAsync(r, token);
                launcher.Dispatcher.Invoke(() => note(sent ? () => Lang.T("stopped unexpectedly; a report went to Studio Quire")
                    : () => Lang.F("stopped unexpectedly; the report wasn't sent ({0})", Lang.T(said))));
            });
            return;
        }
        var v = new CrashReportView(places, s);
        var w = new Window
        {
            Title = Lang.T("Sojourn: a crash report"), Language = Words.Language, Content = v, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowActivated = !Places.Background, Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xEE, 0xF1)),
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(w, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 40, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(v.CloseKey, true);
        v.CloseRequested += w.Close;
        w.Show();
    }
}
