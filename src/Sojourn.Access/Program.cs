using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Access;
using Sojourn.Core.Agents;
using Sojourn.Session.Login;

namespace Sojourn.Access;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var opt = AccessOptions.Parse(args);
        // Studio Quire's relay as the assistant's provider is Access's alone (the people the studio covers are here); the
        // VR client and its launcher leave it off (Sojourn.Session's ProviderKeys.RelayOffered)
        Sojourn.Session.Assistant.ProviderKeys.RelayOffered = true;
        // its assistant's problem reports labelled and filed as Sojourn Access's own, not the VR client's
        Sojourn.Session.Release.ProblemReport.UseAccess();
        ApplicationConfiguration.Initialize();
        AppIcon.Apply();
        Application.AddMessageFilter(RealInput.Filter);
        RealInput.Start();
        // an exception escaping on the window's thread: into the session log and said, the app going on, rather than
        // WinForms' own dialog, which takes the focus and waits on a button nobody was told about
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Console.Error.WriteLine($"access: unhandled on the window's thread: {e.Exception}");
            MainForm.Oops?.Invoke(e.Exception);
        };
        var app = new AccessApp(opt, args);
        Application.Run(app);
        return 0;
    }
}

/// <summary>
/// The login form first; once the person logs in, the session starts behind it, the grid's questions (Terms of
/// Service, an MFA code) are asked there, and on arrival the main window opens with the focus in the Say box, as
/// Radegast does ("you'll know you're logged in because the cursor is in the chat input"). A failed login goes back to
/// the form; another account than the one the session was made for starts the app again (each account has its own
/// state, made with the session).
/// </summary>
sealed class AccessApp : ApplicationContext
{
    readonly AccessOptions _opt;
    readonly string[] _args;
    readonly AccessSettings _settings;
    readonly LoginForm _login;
    readonly System.Windows.Forms.Timer _watch = new() { Interval = 250 };
    readonly WindowWatch _held;
    MainForm? _main;
    LoginChoice? _sessionFor;
    /// <summary>The login chosen last, until it works: then its password is kept or forgotten (LoginForm.LoggedIn).</summary>
    LoginChoice? _toKeep;
    bool _askedTerms, _shown;
    int _mfaAsked;

    public AccessApp(AccessOptions opt, string[] args)
    {
        _opt = opt;
        _args = args;
        _settings = AccessSettings.Load(opt.BaseDir);
        _login = new LoginForm(opt, _settings);
        _login.Chosen += OnChosen;
        _login.FormClosed += (_, _) => { if (!_shown) _ = QuitAsync(); };
        _watch.Tick += (_, _) => Watch();
        _held = new WindowWatch(SayWhileHeld, LogWhileHeld, RestartWhileHeld);
        _login.Show();
    }

    // --- the window's thread held (WindowWatch): all three on the watch's thread ------------------------------------------

    /// <summary>NVDA's API works from any thread; a notification (any other reader's words, and the announcer's way when
    /// NVDA refuses) would wait on the held thread, so Windows' own voice says it.</summary>
    void SayWhileHeld(string text)
    {
        var said = _main?.Host?.ScreenReader?.Announcer;
        var reader = said?.Reader ?? ScreenReaders.Running(null);
        if (said?.Through == "NVDA") said.Answer(text, keep: false);
        else if (said == null && reader == "NVDA" && _login.SayByNvda(text)) { }
        else if (reader != null) WindowWatch.SayByWindows(text);
        else said?.Answer(text, keep: false);   // (no reader here, as in the scripted test: it arrives when the thread does)
    }

    void LogWhileHeld(string line)
    {
        if (_main?.Host is { } host) host.Log.LogInformation("{Line}", line);
        else Console.Error.WriteLine($"access: {line}");
    }

    void RestartWhileHeld()
    {
        try
        {
            var again = StartInfo(_sessionFor);
            if (_main?.Host?.Session is { } s) Task.Run(() => s.LogoutAsync()).Wait(3000);
            Process.Start(again);
        }
        catch (Exception e) { LogWhileHeld($"window: couldn't start again ({e.GetType().Name}: {e.Message})"); }
        Process.GetCurrentProcess().Kill();
    }

    async void OnChosen(LoginChoice choice)
    {
        // the session outlives the press of Log in: nothing it starts runs as the person's own action
        RealInput.Detach();
        _toKeep = choice;
        _settings.LastGrid = choice.Grid;
        _settings.LastUser = choice.Last == "Resident" ? choice.First : $"{choice.First} {choice.Last}";
        _settings.LastStart = choice.Start.StartsWith("uri:") && !_login.IsFavoriteStart(choice.Start) ? null : choice.Start;
        _settings.Save();
        if (_main != null && _sessionFor != null)
        {
            if (choice.SameAccount(_sessionFor))
            {
                _askedTerms = false;
                _mfaAsked = 0;
                _toldFailure = null;
                _main.Retry(choice);
                _watch.Start();
                return;
            }
            // another account: its own state dir and session, so a fresh run with the form filled in
            Restart(choice);
            return;
        }
        _sessionFor = choice;
        _main = new MainForm(_opt, _settings, _args);
        _main.FormClosed += (_, _) => ExitThread();
        _main.LogOutAsked += () => { if (_sessionFor is { } account) Restart(account); };
        _main.NeedsLogin += words =>
        {
            // after arrival the session gave up logging in again by itself: the login window takes over, as at the start
            _askedTerms = false;
            _mfaAsked = 0;
            _login.Show();
            _login.Activate();
            _login.Failed(words);
        };
        try { await _main.StartAsync(choice); }
        catch (Exception e) { _login.Failed($"Couldn't start: {e.Message}"); return; }
        _watch.Start();
    }

    string? _toldFailure;

    void Watch()
    {
        if (_main?.Host?.Session is not { } s) return;
        switch (s.Status)
        {
            case AgentStatus.Online:
                _toldFailure = null;
                if (_toKeep is { } kept)
                {
                    _toKeep = null;
                    _login.LoggedIn(kept);
                }
                if (!_shown)
                {
                    _shown = true;
                    _main.Show();
                    _main.Activate();
                    _main.Arrived();
                    _login.Hide();
                }
                else if (_login.Visible)
                {
                    // logged in again from the login window after the session had given up: back to the main window
                    _login.Hide();
                    _main.Activate();
                }
                // (still watched while in world: logging in again by itself after a drop may meet the grid's terms or MFA)
                _askedTerms = false;
                _mfaAsked = 0;
                break;
            case AgentStatus.TermsRequired when !_askedTerms && s.PendingTerms is { } t:
                _askedTerms = true;
                if (_shown) { _login.Show(); _login.Activate(); }
                _login.Say(t.Kind == "tos" ? "The grid asks you to agree to its Terms of Service." : "The grid has a message to read before you log in.");
                s.AnswerTerms(_login.AskTerms(t.Kind, t.Message));
                break;
            case AgentStatus.MfaRequired when s.MfaAsks > _mfaAsked:
                _mfaAsked = s.MfaAsks;
                if (_shown) { _login.Show(); _login.Activate(); }
                _login.Say(_mfaAsked > 1 ? "That code didn't work. The code from your authenticator, again." : "The grid asks for the code from your authenticator.");
                if (_login.AskMfa(_mfaAsked > 1) is { } code) s.SubmitMfa(code);
                else _ = s.LogoutAsync();   // cancelled: no login until the person asks again
                break;
            case AgentStatus.Failed or AgentStatus.Offline or AgentStatus.Reconnecting when !_shown && s.LoginFailure is { } f:
                // said once per failure; the session may still retry by itself (a region restarting), so keep watching
                var key = $"{f.Key}|{f.Message}";
                if (key == _toldFailure) break;
                _toldFailure = key;
                var (plain, detail) = LoginWords.For(f.Key, f.Message);
                _login.Failed(plain + (detail != null ? " " + detail : ""));
                break;
        }
    }

    void Restart(LoginChoice choice)
    {
        try
        {
            var again = StartInfo(choice);
            _ = QuitAsync(then: () => Process.Start(again));
        }
        catch (Exception e) { _login.Failed($"Couldn't start again for that account: {e.Message}"); }
    }

    /// <summary>This program again, at the login window, the form filled in for that account if there's one.</summary>
    ProcessStartInfo StartInfo(LoginChoice? choice)
    {
        var args = choice is { } c ? new List<string> { "--grid", c.Grid, "--user", $"{c.First} {c.Last}", "--start", c.Start } : new List<string>();
        for (int i = 0; i < _args.Length; i++)
        {
            if (_args[i] is "--grid" or "--user" or "--password" or "--start") { i++; continue; }
            if (_args[i] == "--autologin") continue;
            args.Add(_args[i]);
        }
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        // run by dotnet (in the repo) the program is the dll; a packaged build is its own exe
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) is "dotnet")
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SojournAccess.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    async Task QuitAsync(Action? then = null)
    {
        _watch.Stop();
        if (_main != null) await _main.EndSessionAsync();
        then?.Invoke();
        ExitThread();
    }
}
