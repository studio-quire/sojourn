using System.Diagnostics;
using Sojourn.Core.Agents;
using Sojourn.Session.Login;

namespace Sojourn.Access;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var opt = AccessOptions.Parse(args);
        ApplicationConfiguration.Initialize();
        Application.AddMessageFilter(RealInput.Filter);
        RealInput.Start();
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
    MainForm? _main;
    LoginChoice? _sessionFor;
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
        _login.Show();
    }

    async void OnChosen(LoginChoice choice)
    {
        // the session outlives the press of Log in: nothing it starts runs as the person's own action
        RealInput.Detach();
        _settings.LastGrid = choice.Grid;
        _settings.LastUser = choice.Last == "Resident" ? choice.First : $"{choice.First} {choice.Last}";
        _settings.LastStart = choice.Start.StartsWith("uri:") ? null : choice.Start;
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
                _watch.Stop();
                _toldFailure = null;
                if (!_shown)
                {
                    _shown = true;
                    _main.Show();
                    _main.Activate();
                    _main.Arrived();
                    _login.Hide();
                }
                break;
            case AgentStatus.TermsRequired when !_askedTerms && s.PendingTerms is { } t:
                _askedTerms = true;
                _login.Say(t.Kind == "tos" ? "The grid asks you to agree to its Terms of Service." : "The grid has a message to read before you log in.");
                s.AnswerTerms(_login.AskTerms(t.Kind, t.Message));
                break;
            case AgentStatus.MfaRequired when s.MfaAsks > _mfaAsked:
                _mfaAsked = s.MfaAsks;
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
        var args = new List<string> { "--grid", choice.Grid, "--user", $"{choice.First} {choice.Last}", "--start", choice.Start };
        for (int i = 0; i < _args.Length; i++)
        {
            if (_args[i] is "--grid" or "--user" or "--password" or "--start") { i++; continue; }
            if (_args[i] == "--autologin") continue;
            args.Add(_args[i]);
        }
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            // run by dotnet (in the repo) the program is the dll; a packaged build is its own exe
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) is "dotnet")
                psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SojournAccess.dll"));
            foreach (var a in args) psi.ArgumentList.Add(a);
            _ = QuitAsync(then: () => Process.Start(psi));
        }
        catch (Exception e) { _login.Failed($"Couldn't start again for that account: {e.Message}"); }
    }

    async Task QuitAsync(Action? then = null)
    {
        _watch.Stop();
        if (_main != null) await _main.EndSessionAsync();
        then?.Invoke();
        ExitThread();
    }
}
