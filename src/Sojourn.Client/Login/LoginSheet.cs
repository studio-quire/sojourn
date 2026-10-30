using System.Numerics;
using System.Text;
using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;

namespace Sojourn.Client.Login;

/// <summary>What the person chose on the login screen: the account (grid, name, start), the password as it goes to
/// the grid ("$1$" + MD5, in memory only), and whether to remember it.</summary>
public sealed record LoginChoice(SavedAccount Account, string Hash, bool Remember)
{
    /// <summary>Where this login starts, when not the account's own start (back where the person was, after a lost GPU).</summary>
    public string? StartNow { get; init; }
}

/// <summary>
/// The login sheet, in the panel's look and the menu's place (Assistant/PanelPlacement.cs; in the middle of the window
/// on the desktop). Before the session exists (Login/LoginScreen.cs) it's the form: saved accounts to pick, a new one
/// (username, password, grid: Second Life, Beta or any login URI), where to start (last, home, a region or SLurl), the
/// password remembered only when the person switches it on, and forgetting an account. Once logging in it stays in the
/// panel's place until the first arrival: progress, the grid's Terms of Service or critical message (Continue only
/// once "I agree" is on, as the viewer's floater), the MFA code with "remember this computer" (off unless switched
/// on), and failures in plain words (Login/LoginWords.cs) with Back (the form again) and Try again. Later a grid asking
/// for the ToS or a code on a reconnect brings it back the same way. The answers to the grid go only from real
/// presses: a hand on the mouse or the keyboard (Assistant/RealInput.cs), or the controllers; never the agent API.
/// Little text: names, fields and keys; what they mean is behind the ? key.
/// Written by Claude Opus 5.5, 2026-10-28 (batch R).
/// </summary>
public sealed partial class LoginSheet
{
    public enum Stage { Form, Connecting, Terms, Mfa, Failed }

    public const float Width = 460;
    readonly LoginStore? _store;
    readonly Func<PanelLook> _look;
    readonly Action<string> _log;
    AgentSession? _session;

    /// <summary>A sheet for the login screen (the form first), or, store null, one that only follows a login the
    /// command line started (progress, ToS, MFA, failures).</summary>
    public LoginSheet(LoginStore? store, Func<PanelLook> look, Action<string> log)
    {
        _store = store;
        _look = look;
        _log = log;
        if (store != null) LoadAccounts();
    }

    /// <summary>The name and version at the form's foot (TPV policy 1.g: the version on the login screen).</summary>
    public string About { get; init; } = "Sojourn";

    /// <summary>Started from the login screen (Back and Cancel lead there).</summary>
    public bool FromScreen => _store != null;
    /// <summary>The login screen's choice, once the person pressed Log in.</summary>
    public LoginChoice? Chosen { get; set; }
    /// <summary>The person asked to leave for the login screen (Back, Cancel, log out): the client restarts into it.</summary>
    public bool Leaving { get; private set; }
    /// <summary>Esc or × on the form: quit.</summary>
    public bool Quit { get; private set; }
    /// <summary>The account to show selected on the login screen after a restart (Back, log out).</summary>
    public SavedAccount? Account => Chosen?.Account;

    /// <summary>Follows the session's login (after the form, or from the command line).</summary>
    public void Attach(AgentSession session)
    {
        _session = session;
        _form = false;
        if (Chosen is { } c) session.Config.Password = c.Hash;
    }

    bool _form = true;
    bool _saved;

    /// <summary>What the sheet shows now, or null: nothing to show (the session is in world, or the grid's questions
    /// are answered).</summary>
    public Stage? Current
    {
        get
        {
            if (_form) return Stage.Form;
            if (_session is not { } s) return null;
            if (s.Status == AgentStatus.TermsRequired && s.PendingTerms != null) return Stage.Terms;
            if (s.Status == AgentStatus.MfaRequired) return Stage.Mfa;
            if (s.LoginCount > 0)
            {
                if (!_saved) Arrived();
                return null;
            }
            if (s.LoginFailure != null && s.Status != AgentStatus.LoggingIn) return Stage.Failed;
            return Stage.Connecting;
        }
    }

    /// <summary>Showing (the main loop's panel and the pointer give way to it).</summary>
    public bool Active => !Leaving && Current != null;

    long _logoutArmedAt = -10_000;

    /// <summary>The settings menu's row (the "you" section, login-screen runs): log out and back to the login screen,
    /// a second press within four seconds to be sure.</summary>
    public (MenuRow Row, Action<int> Act) LogOutRow()
    {
        bool armed = Environment.TickCount64 - _logoutArmedAt < 4000;
        return (new MenuRow("log out", armed ? "sure?" : "out", false), _ =>
        {
            if (Environment.TickCount64 - _logoutArmedAt < 4000) { Leaving = true; _log("login: the person logged out"); }
            else _logoutArmedAt = Environment.TickCount64;
        });
    }

    // ---- the form's state ----

    List<SavedAccount> _accounts = new();
    int _selected = -1;   // a saved account, or -1: a new one
    readonly Field _user = new(), _pass = new(masked: true), _uri = new(), _region = new(), _code = new(digits: true);
    bool _remember;
    int _grid;            // 0 Second Life, 1 Beta, 2 another by its URI
    int _start;           // 0 last, 1 home, 2 a region
    bool _forgetArmed, _help, _agree, _rememberComputer;
    string? _note;        // what's missing, in a few words
    int _termsPage;
    Field? _focus;
    readonly Dictionary<string, string> _gridNames = new();

    void LoadAccounts()
    {
        _accounts = _store!.Accounts().ToList();
        string? last = _store.LastId;
        _selected = _accounts.FindIndex(a => a.Id == last);
        if (_selected < 0 && _accounts.Count > 0) _selected = 0;
        Select(_selected);
    }

    /// <summary>Show an account selected (after Back or a log out), or fill the form with one that isn't saved.</summary>
    public void Preselect(SavedAccount a, string? start = null)
    {
        int i = _accounts.FindIndex(x => x.Id == a.Id);
        if (i >= 0) Select(i);
        else
        {
            Select(-1);
            _user.Set(a.Name);
            _grid = a.Grid == Grids.Main ? 0 : a.Grid == Grids.Beta ? 1 : 2;
            if (_grid == 2) _uri.Set(a.Grid);
            SetStart(a.Start);
        }
        if (start != null) SetStart(start);
        _focus = _selected >= 0 && !_accounts[_selected].Remembered || _selected < 0 && _user.Length > 0 ? _pass : _focus;
        Changed();
    }

    void Select(int i)
    {
        _selected = i;
        _pass.Clear();
        _forgetArmed = false;
        _note = null;
        if (i >= 0 && i < _accounts.Count)
        {
            var a = _accounts[i];
            _remember = a.Remembered;
            SetStart(a.Start);
            _focus = a.Remembered ? null : _pass;
        }
        else
        {
            _selected = -1;
            _user.Clear();
            _remember = false;
            _start = 0;
            _region.Clear();
            _focus = _user;
        }
    }

    void SetStart(string start)
    {
        _region.Clear();
        if (start == "home") _start = 1;
        else if (start.StartsWith("uri:"))
        {
            _start = 2;
            var parts = start[4..].Split('&');
            _region.Set(parts.Length >= 4 && parts[1] == "128" && parts[2] == "128" && parts[3] == "0" ? parts[0] : string.Join('/', parts));
        }
        else _start = 0;
    }

    /// <summary>The start as the login sends it: last, home, or uri:Region&amp;x&amp;y&amp;z from a name or a SLurl.</summary>
    static string? StartOf(int start, string region)
    {
        if (start == 0) return "last";
        if (start == 1) return "home";
        var t = Uri.UnescapeDataString(region.Trim());
        if (t.Length == 0) return null;
        // SLurls: secondlife://Region/x/y/z, http(s)://maps.secondlife.com/secondlife/Region/x/y/z, or Region/x/y/z
        bool hop = t.StartsWith("hop://", StringComparison.OrdinalIgnoreCase);
        foreach (var prefix in new[] { "secondlife:///app/teleport/", "secondlife://", "https://maps.secondlife.com/secondlife/", "http://maps.secondlife.com/secondlife/",
                     "https://slurl.com/secondlife/", "http://slurl.com/secondlife/", "hop://" })
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { t = t[prefix.Length..]; break; }
        var p = t.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (hop && p.Length > 1) p = p[1..];   // hop://grid:port/Region/x/y/z
        if (p.Length == 0) return null;
        return $"uri:{p[0]}&{N(p, 1, "128")}&{N(p, 2, "128")}&{N(p, 3, "0")}";

        static string N(string[] p, int i, string d) => i < p.Length && float.TryParse(p[i], System.Globalization.CultureInfo.InvariantCulture, out var f)
            ? ((int)MathF.Round(f)).ToString(System.Globalization.CultureInfo.InvariantCulture) : d;
    }

    // ---- what the person does ----

    long _version;
    void Changed() => Interlocked.Increment(ref _version);

    long _stageShown = Environment.TickCount64;
    Stage? _lastStage;
    /// <summary>Keys that answer the grid take a press only once their sheet has been up this long (as confirmations).</summary>
    const long ArmMs = 800;
    bool Armed => Environment.TickCount64 - _stageShown >= ArmMs;

    void Act(int id, bool real)
    {
        var stage = Current;
        _note = null;
        if (id != Ids.Forget) _forgetArmed = false;
        switch (id)
        {
            case Ids.Help: _help = !_help; break;
            case Ids.Close: if (stage == Stage.Form) Quit = true; break;
            case Ids.New: Select(-1); break;
            case >= Ids.Account and < Ids.Account + 64: Select(id - Ids.Account); break;
            case Ids.UserField: _focus = _user; KeyboardFor(_user); break;
            case Ids.PassField: _focus = _pass; KeyboardFor(_pass); break;
            case Ids.UriField: _focus = _uri; KeyboardFor(_uri); break;
            case Ids.RegionField: _focus = _region; KeyboardFor(_region); break;
            case Ids.CodeField: _focus = _code; KeyboardFor(_code); break;
            case >= Ids.Grid and < Ids.Grid + 3:
                _grid = id - Ids.Grid;
                if (_grid == 2) { _focus = _uri; KeyboardFor(_uri); }
                break;
            case >= Ids.Start and < Ids.Start + 3:
                _start = id - Ids.Start;
                if (_start == 2) { _focus = _region; KeyboardFor(_region); }
                break;
            case Ids.Remember:
                _remember = !_remember;
                if (!_remember && _selected >= 0 && _accounts[_selected].Remembered)
                {
                    // switched off: the kept password goes now, not at the next login
                    _store?.ForgetPassword(_accounts[_selected].Id);
                    _accounts[_selected] = _accounts[_selected] with { Secret = null };
                    _focus = _pass;
                }
                break;
            case Ids.Forget when _selected >= 0:
                if (!_forgetArmed) { _forgetArmed = true; break; }
                _store?.Forget(_accounts[_selected].Id);
                _log($"login: forgot an account");
                LoadAccounts();
                break;
            case Ids.LogIn: LogIn(); break;
            case Ids.Agree: _agree = !_agree; break;
            case Ids.RememberComputer: _rememberComputer = !_rememberComputer; break;
            case Ids.PagePrev: _termsPage = Math.Max(0, _termsPage - 1); break;
            case Ids.PageNext: _termsPage++; break;
            case Ids.Continue when stage == Stage.Terms && real && Armed:
                if (_session?.PendingTerms is { } t && (t.Kind != "tos" || _agree))
                {
                    _log($"login: the person accepted the grid's {(t.Kind == "tos" ? "Terms of Service" : "message")}");
                    _session.AnswerTerms(true);
                    _agree = false;
                }
                break;
            case Ids.Decline when stage == Stage.Terms && real && Armed:
                _log("login: the person declined");
                _session?.AnswerTerms(false);
                break;
            case Ids.Continue when stage == Stage.Mfa && real && Armed: SubmitCode(); break;
            case Ids.Cancel when stage == Stage.Mfa && real:
            case Ids.Cancel when stage == Stage.Connecting:
                _ = _session?.LogoutAsync();
                if (FromScreen) Leaving = true;
                break;
            case Ids.Back: if (FromScreen) { _ = _session?.LogoutAsync(); Leaving = true; } break;
            case Ids.TryAgain: TryAgain(); break;
        }
        Changed();
    }

    void LogIn()
    {
        if (_store == null) return;
        SavedAccount account;
        string? hash;
        if (_selected >= 0)
        {
            var a = _accounts[_selected];
            hash = _pass.Length > 0 ? _pass.TakeHash() : LoginStore.HashOf(a);
            if (hash == null) { _note = "password"; _focus = _pass; return; }
            account = a;
        }
        else
        {
            var name = _user.Text.Trim().Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (name.Length is 0 or > 2) { _note = "username"; _focus = _user; return; }
            string grid = _grid switch { 0 => Grids.Main, 1 => Grids.Beta, _ => Grids.Normalise(_uri.Text) ?? "" };
            if (grid.Length == 0) { _note = "login URI"; _focus = _uri; return; }
            if (_pass.Length == 0) { _note = "password"; _focus = _pass; return; }
            hash = _pass.TakeHash();
            account = new SavedAccount
            {
                First = name[0], Last = name.Length > 1 ? name[1] : "Resident", Grid = grid,
                GridName = _gridNames.GetValueOrDefault(grid),
            };
        }
        var start = StartOf(_start, _region.Text);
        if (start == null) { _note = "region"; _focus = _region; return; }
        account = account with { Start = start };
        Chosen = new LoginChoice(account, hash, _remember);
        _log($"login: logging in on {Grids.Label(account.Grid, account.GridName)}{(_remember ? ", password remembered after it works" : "")}");
    }

    /// <summary>After the first arrival: the account kept for next time (its password only if asked), and kept for a
    /// restart after a lost GPU.</summary>
    void Arrived()
    {
        _saved = true;
        if (_store == null || Chosen is not { } c) return;
        var account = c.Account;
        if (!Grids.IsSecondLife(account.Grid) && account.GridName == null)
            _ = Task.Run(async () =>
            {
                var name = await Grids.AskNameAsync(Grids.UriOf(account.Grid));
                _store.Logged(account with { GridName = name }, c.Hash, c.Remember);
            });
        else _store.Logged(account, c.Hash, c.Remember);
        DeviceLoss.BeforeRelaunch = () =>
        {
            _store.HandOver(account, Chosen?.Hash ?? c.Hash);
            return new[] { "--login-resume" };
        };
    }

    void TryAgain()
    {
        if (_session == null) return;
        if (_pass.Length > 0)
        {
            var hash = _pass.TakeHash();
            _session.Config.Password = hash;
            if (Chosen is { } c) Chosen = c with { Hash = hash };
        }
        _session.RequestLogin();
        _log("login: trying again");
    }

    void SubmitCode()
    {
        if (_session == null) return;
        var code = _code.Text.Trim();
        if (code.Length < 6) { _focus = _code; _note = "code"; return; }
        _session.KeepMfaHash = _rememberComputer;
        _session.SubmitMfa(code);
        _code.Clear();
        _log($"login: the person gave a code{(_rememberComputer ? ", this computer remembered" : "")}");
    }

    // ---- typing: the PC's keyboard, or the headset's (Control/HeadsetKeyboard.cs) ----

    /// <summary>A field has the keys (the form, or the code).</summary>
    public bool TakesKeys => _focus != null && Current is Stage.Form or Stage.Mfa or Stage.Failed;

    public void Char(char ch)
    {
        if (!TakesKeys || char.IsControl(ch)) return;
        _focus!.Add(ch);
        _note = null;
        Changed();
    }

    /// <summary>A key on the PC's keyboard; true when the sheet took it. real: a hand on the keyboard (Enter answers the
    /// grid only then).</summary>
    public bool Key(Silk.NET.Input.Key key, bool shift, bool ctrl, Func<string?> clipboard, bool real)
    {
        var stage = Current;
        if (stage == null) return false;
        switch (key)
        {
            case Silk.NET.Input.Key.Escape when stage == Stage.Form: Quit = true; return true;
            case Silk.NET.Input.Key.Backspace when TakesKeys: _focus!.Backspace(); Changed(); return true;
            case Silk.NET.Input.Key.V when ctrl && TakesKeys:
                foreach (var ch in (clipboard() ?? "").Trim()) if (!char.IsControl(ch)) _focus!.Add(ch);
                Changed();
                return true;
            case Silk.NET.Input.Key.Tab when stage == Stage.Form:
                var order = FormFields();
                int i = _focus == null ? -1 : order.IndexOf(_focus);
                _focus = order.Count == 0 ? null : order[((shift ? i - 1 : i + 1) % order.Count + order.Count) % order.Count];
                Changed();
                return true;
            case Silk.NET.Input.Key.Enter or Silk.NET.Input.Key.KeypadEnter:
                if (stage == Stage.Form) Act(Ids.LogIn, real);
                else if (stage == Stage.Mfa && real) Act(Ids.Continue, real);
                else if (stage == Stage.Failed) Act(Ids.TryAgain, real);
                return true;
        }
        return TakesKeys;   // (a letter: KeyChar types it)
    }

    List<Field> FormFields()
    {
        var f = new List<Field>();
        if (_selected < 0) f.Add(_user);
        f.Add(_pass);
        if (_selected < 0 && _grid == 2) f.Add(_uri);
        if (_start == 2) f.Add(_region);
        return f;
    }

    /// <summary>Set by the headset: the keyboard brought up for a field the person pressed.</summary>
    public Action<IKeyTarget>? ShowKeyboard;

    void KeyboardFor(Field f) => ShowKeyboard?.Invoke(new FieldKeys(this, f));

    sealed class FieldKeys(LoginSheet s, Field f) : IKeyTarget
    {
        public string Label => f == s._pass ? "password" : f == s._code ? "code" : f == s._uri ? "login URI" : f == s._region ? "region" : "username";
        public bool Alive => s.Active && s._focus == f;
        public object Owner => f;
        public void Char(char ch) { f.Add(ch); s.Changed(); }
        public void Backspace() { f.Backspace(); s.Changed(); }
        public bool Enter()
        {
            var stage = s.Current;
            if (stage == Stage.Form) s.Act(Ids.LogIn, real: true);
            else if (stage == Stage.Mfa) s.Act(Ids.Continue, real: true);
            else if (stage == Stage.Failed) s.Act(Ids.TryAgain, real: true);
            return true;
        }
        public void Opened() { }
        public void Closed() { }
    }

    /// <summary>A text field. The password's characters never become a string: they're hashed straight from the buffer
    /// ($1$ + MD5, as LibreMetaverse sends a plain one) and cleared.</summary>
    sealed class Field(bool masked = false, bool digits = false)
    {
        char[] _buf = new char[64];
        int _len;
        public int Length => _len;
        public bool Masked => masked;
        public string Text => masked ? new string('•', _len) : new string(_buf, 0, _len);
        public string Shown => masked ? new string('•', Math.Min(_len, 24)) : Text;

        public void Add(char ch)
        {
            if (digits && !char.IsDigit(ch)) return;
            if (digits && _len >= 8) return;
            if (_len == _buf.Length)
            {
                var bigger = new char[_buf.Length * 2];
                Array.Copy(_buf, bigger, _len);
                Array.Clear(_buf);
                _buf = bigger;
            }
            _buf[_len++] = ch;
        }

        public void Backspace() { if (_len > 0) _buf[--_len] = '\0'; }
        public void Clear() { Array.Clear(_buf); _len = 0; }
        public void Set(string s) { Clear(); foreach (var ch in s) Add(ch); }

        public string TakeHash()
        {
            var bytes = new byte[Encoding.UTF8.GetMaxByteCount(_len)];
            try
            {
                int n = Encoding.UTF8.GetBytes(_buf, 0, _len, bytes, 0);
                var md5 = System.Security.Cryptography.MD5.HashData(bytes.AsSpan(0, n));
                return "$1$" + Convert.ToHexString(md5).ToLowerInvariant();
            }
            finally { Array.Clear(bytes); Clear(); }
        }
    }

    static class Ids
    {
        public const int Close = 1, Help = 2, New = 3, UserField = 10, PassField = 11, UriField = 12, RegionField = 13, CodeField = 14,
            Grid = 20, Start = 30, Remember = 40, Forget = 41, LogIn = 42, Agree = 50, Continue = 51, Decline = 52, RememberComputer = 53,
            PagePrev = 54, PageNext = 55, Back = 60, TryAgain = 61, Cancel = 62, Account = 100;
    }

    // ---- presses: the drawn sheet's keys, from the mouse or a controller's ray ----

    int _hover = -1;
    int? _down;

    /// <summary>The key at a point of the drawn sheet (logical px), or -1.</summary>
    public int KeyAt(Vector2 logical)
    {
        if (_drawn is not { } d) return -1;
        for (int i = 0; i < d.Keys.Count; i++)
            if (d.Keys[i].Rect.Contains(logical.X, logical.Y)) return d.Keys[i].Id;
        return -1;
    }

    public void Hover(int id)
    {
        if (id == _hover) return;
        _hover = id;
        Changed();
    }

    public void PressDown(int id) => _down = id >= 0 ? id : null;

    /// <summary>A press let go: acted on if it went down on the same key. real: a hand on the mouse, or a controller.</summary>
    public void PressUp(int id, bool real)
    {
        var down = _down;
        _down = null;
        if (down is { } d && d == id && id >= 0) Act(id, real);
    }

    /// <summary>Each frame before drawing: the stage's arrival noted (its answer keys arm after a moment).</summary>
    void Tick()
    {
        var stage = Current;
        if (stage != _lastStage)
        {
            _lastStage = stage;
            _stageShown = Environment.TickCount64;
            _termsPage = 0;
            if (stage == Stage.Mfa) _focus = _code;
            else if (stage == Stage.Failed && FromScreen && _session?.LoginFailure?.Key == "key") _focus = _pass;
            else if (stage is Stage.Connecting or Stage.Terms) _focus = null;
            Changed();
        }
    }
}
