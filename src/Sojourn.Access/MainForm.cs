using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sojourn.Core;
using Sojourn.Core.Access;
using Sojourn.Core.Agents;
using Sojourn.Session;
using Sojourn.Session.Assistant;
using Sojourn.Session.Login;
using Sojourn.Session.Social;

namespace Sojourn.Access;

/// <summary>
/// Sojourn Access's window: areas as tabs reached by Alt+1-9 (Radegast's numbers where it has the same area: 1 chat, 4
/// inventory, 5 search, 6 map and places, 7 objects), each built from standard controls a screen reader knows. What
/// arrives is read out through the person's own screen reader (A1's announcer, shared with the VR client) and waits:
/// nothing here moves the focus but the person's own keys. The session is Sojourn.Session's host, as the VR client's.
/// The areas are in MainForm.*.cs.
/// </summary>
public sealed partial class MainForm : Form
{
    readonly AccessOptions _opt;
    readonly AccessSettings _set;
    readonly string[] _args;
    public SessionHost? Host { get; private set; }
    PersonOps _ops = null!;
    Commands _cmd = null!;
    Announcer? Reader => Host?.ScreenReader?.Announcer;

    readonly TabControl _areas = new() { Name = "areas", AccessibleName = "Areas", Dock = DockStyle.Fill };
    readonly StatusStrip _statusStrip = new() { Name = "statusBar", AccessibleName = "Status" };
    readonly ToolStripStatusLabel _where = new() { Name = "where", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 50 };
    readonly Sojourn.Access.Walk.WalkPad _walkPad = new();
    Sojourn.Access.Walk.WalkMode? _walk;
    Sojourn.Access.Sound.Earcons? _earcons;
    Control? _beforeWalk;
    readonly System.Windows.Forms.Timer _slow = new() { Interval = 250 };
    bool _ended;

    public MainForm(AccessOptions opt, AccessSettings set, string[] args)
    {
        _opt = opt;
        _set = set;
        _args = args;
        Name = "main";
        Text = "Sojourn Access";
        Size = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildChat();
        BuildConversations();
        BuildPeople();
        BuildInventory();
        BuildSearch();
        BuildPlaces();
        BuildObjects();
        BuildNotices();
        BuildAssistant();
        BuildSettings();
        _areas.TabPages.AddRange(new[] { _chatPage, _conversationsPage, _peoplePage, _inventoryPage, _searchPage, _placesPage, _objectsPage, _noticesPage, _assistantPage, _settingsPage });
        _statusStrip.Items.Add(_where);
        Controls.Add(_areas);
        Controls.Add(_walkPad);
        Controls.Add(_statusStrip);

        _areas.SelectedIndexChanged += (_, _) => { FocusChanged(); AreaShown(); };
        // in walk mode the person isn't in the conversation they came from: its lines say whose IM they are again
        _walkPad.GotFocus += (_, _) => { _inWalk = true; FocusChanged(); };
        _walkPad.LostFocus += (_, _) => { _inWalk = false; FocusChanged(); };
        Activated += (_, _) => { _active = true; FocusChanged(); };
        Deactivate += (_, _) => { _active = false; FocusChanged(); _walk?.ReleaseAll(); VoiceRelease(); };
        _tick.Tick += (_, _) => Tick();
        _slow.Tick += (_, _) => Slow();
    }

    // --- the session --------------------------------------------------------------------------------------------------

    /// <summary>Makes the session for this account and starts it (login begins); the window stays hidden until arrival.</summary>
    public async Task StartAsync(LoginChoice c)
    {
        var handle = Handle;   // the window exists (hidden) for the screen reader's voice
        var reader = _opt.Reader;
        var options = new SessionOptions
        {
            LoginUri = c.Grid, First = c.First, Last = c.Last, Password = c.Password, PasswordFile = null,
            Start = c.Start, StartGiven = true, BridgePort = _opt.Port, Api = _opt.Api, BaseDir = _opt.BaseDir,
            Assistant = _opt.Assistant, Voice = _opt.Voice, Sounds = _opt.Sounds, Media = false,
            // Access's own quieter sound defaults first (kept in the file), then the command line's for this session only
            VrOverrides = new Action<Sojourn.Session.Control.VrSettings>[]
            {
                s => _set.ApplySessionDefaults(s, Sojourn.Session.Control.VrSettings.PathIn(_opt.BaseDir)),
                s => { if (reader is "on" or "off" or "auto") s.ScreenReader.Mode = reader; },
            },
        };
        // the renderer's trackers attach to the session's client when there's a renderer out of sight
        var host = Host = new SessionHost(options, renderHooks: _opt.Render) { App = "access" };
        host.Config.Version = AppInfo.Version;   // a packaged build's own number at the login (docs/tpv-policy.md)
        // a grid on this PC is reached over loopback alone: no port open to the network, and Windows Firewall never asks
        LibreMetaverse.Settings.BindAddress = Uri.TryCreate(c.Grid, UriKind.Absolute, out var gu) && gu.IsLoopback
            ? System.Net.IPAddress.Loopback : System.Net.IPAddress.Any;
        // the account's own state and cache under its grid (as the VR client's login screen keeps them)
        var key = Grids.KeyOf(c.Grid);
        host.Config.Grids[key] = new Sojourn.Core.Config.GridConfig { LoginUri = c.Grid };
        host.Agent.Grid = key;
        host.Agent.StateDir = Path.Combine(_opt.BaseDir, "state", key, $"{c.First}.{c.Last}".ToLowerInvariant());
        // no greeting while logging in (arriving says where the keys are); after that, when a screen reader starts
        await host.StartAllAsync(_args, handle, new FormVoice(this), "");
        // no frame loop here: the autopilot (the assistant's walks) steers by the avatar dead-reckoned between updates
        host.Session.Motion.DeadReckon = Environment.GetEnvironmentVariable("SOJOURN_DEAD_RECKON") != "0";
        // the renderer out of sight: pictures for the assistant's look, touches on faces, the collision model
        var view = _opt.Render ? host.StartOffscreen() : null;
        _view = view;
        host.StartDescribe(view);

        _ops = new PersonOps(host.Session);
        _cmd = new Commands(_ops, Answer)
        {
            Clipboard = () => (string?)Invoke(() => { try { return System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null; } catch { return null; } }),
        };
        _walk = new Sojourn.Access.Walk.WalkMode(host, _ops, _set, view, line => Reader?.Answer(line)) { StopOthers = () => _cmd.StopAll() };
        _cmd.FeetZ = () => _walk.Me()?.Feet;
        _cmd.Describe = words => (Task)Invoke(() => DescribeWordsAsync(words));
        _cmd.About = () => AppInfo.About(_opt.BaseDir);
        _cmd.Touch = words => (Task)Invoke(() => TouchWordsAsync(words));
        _cmd.Outfit = () => (Task)Invoke(() => OutfitListAsync());
        _cmd.Music = MusicWords;
        _cmd.TeleportFallback = () => _set.WalkTeleportFallback;
        _cmd.CueText = cue => _set.WalkText(cue);
        if (view != null)
        {
            var planner = _cmd.Planner = new Sojourn.Access.Walk.PathPlanner(host.Session, view);
            host.Session.Motion.Router = (c, from, to) => RouteAsync(planner, from, to);
        }
        _walkPad.Walk = _walk;
        _cmd.WalkMode = _walk;
        _walk.Walking = () => _cmd.Heading;
        // sound as a map (M4): a sound per kind of thing arriving, standing in for the viewer's new-IM and alert sounds
        _earcons = new Sojourn.Access.Sound.Earcons(_set, _walk, e => _set.IsMuted(SourceOf(e))
            || (e.Kind is FeedKind.Im or FeedKind.Group && e.Session != null && e.Session == _focusedSource));
        host.Feed.Joined += e => BeginInvoke(() => _earcons.Arrived(e));
        if (host.Audio is { } audio) audio.ConversationSound = false;
        host.Social.AppAlertSound = true;
        host.Session.Api.Register(new Sojourn.Access.Walk.WalkApi(host.Session, _walk, f => (Task)Invoke(f)));
        _set.Register(host.SettingsApi, () => BeginInvoke(() => { RefreshHistories(); _walk?.ApplyFootsteps(); }), view, host.Audio?.Sounds);
        StartIncognito();
        AddIncognitoSettings(host.SettingsApi);
        StartVoiceKeys();
        AddVoiceSettings(host.SettingsApi);
        _cmd.Voice = words => (Task)Invoke(() => VoiceWordsAsync(words));
        host.ScreenReader.Shape = Shape;
        host.ScreenReader.WholeReplies = true;   // rich descriptions run past the announcer's length
        host.ScreenReader.Announcer.WholeAnswers = true;   // so do F1's keys, //help, a long outfit or parcel list
        host.ScreenReader.MusicMuted = () => _set.MuteAll;
        host.ScreenReader.Actions.ConfirmKeys = "Enter confirms, Escape cancels.";
        host.Feed.Joined += e => BeginInvoke(() => Joined(e));
        StartEvents();
        _tick.Start();
        _slow.Start();
    }

    /// <summary>The same account again after a failed login (a mistyped password, say).</summary>
    public void Retry(LoginChoice c)
    {
        if (Host == null) return;
        Host.Agent.Password = c.Password;
        Host.Agent.Start = c.Start;
        Host.Session.RequestLogin();
    }

    /// <summary>In world for the first time: the focus in the Say box.</summary>
    public void Arrived()
    {
        _areas.SelectedTab = _chatPage;
        _chatInput.Focus();
        UpdateWhere();
        if (_ops.Me() is { } me) Reader?.Answer($"Logged in, in {me.Region}. F1 for keys.", keep: false);
        if (Reader is { } r) r.Greeting = "Sojourn Access. F1 for keys.";
    }

    void Tick()
    {
        Host?.Tick();
        _walk?.Tick();
        NoticeCutOff();
        if (Host?.Chat.QuitRequested == true && !_ended) { _quitAsked = true; Close(); }   // the assistant, asked to quit
    }

    void Slow()
    {
        if (Host == null) return;
        PollNotices();
        PollAssistant();
        UpdateWhere();
        AutoRefreshLists();
        WatchWorn();
        WatchSpeakers();
    }

    string? _lastRegion;

    void UpdateWhere()
    {
        if (_ops?.Me() is not { } me) { _where.Text = Host?.Session.StatusMessage ?? ""; return; }
        var c = _ops.Client!;
        var name = c.Self.Name;
        // incognito: the title (in the taskbar and Alt+Tab, where others may see it) leaves out the place
        bool incognito = Host!.Session.Private.On;
        var title = incognito ? $"Sojourn Access: {name}, incognito" : $"Sojourn Access: {name}, {me.Region}";
        if (Text != title) Text = title;
        var money = Host!.Session.BalanceKnown ? $", L${c.Self.Balance}" : "";
        _where.Text = $"{me.Region} {Math.Round(me.Pos.X)}, {Math.Round(me.Pos.Y)}, {Math.Round(me.Pos.Z)}{money}{(incognito ? ", incognito" : "")}";
        if (_lastRegion != null && me.Region != _lastRegion && !_cmd.TeleportingSelf && Environment.TickCount64 - _teleportedAt > 15_000
            && _walk?.SeatedRecently != true)   // a seated crossing is walk mode's to say, with whether the seat held
            Announce($"Now in {me.Region}.");
        _lastRegion = me.Region;
    }

    bool _closeByHand, _quitAsked;

    /// <summary>Alt+F4 and the window's close button come as SC_CLOSE (a program's WM_CLOSE doesn't): those ask first.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == SC_CLOSE) _closeByHand = true;
        base.WndProc(ref m);
        _closeByHand = false;
    }

    const int WM_SYSCOMMAND = 0x0112, SC_CLOSE = 0xF060;

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_ended) { base.OnFormClosing(e); return; }
        // Alt+F4 is next to Alt+4 (the inventory): in world, quitting asks, Enter quits and Escape stays
        if (_closeByHand && !_quitAsked && Host?.Session.Online == true && !QuitDialog.Ask(this))
        {
            e.Cancel = true;
            Reader?.Answer("Still here.", keep: false);
            return;
        }
        e.Cancel = true;
        Hide();
        await EndSessionAsync();
        Close();
    }

    /// <summary>Logs out and stops the session's parts (at most ~12 s: the grid may not answer the logout).</summary>
    public async Task EndSessionAsync()
    {
        if (_ended) return;
        _ended = true;
        _tick.Stop();
        _slow.Stop();
        _eventsStop.Cancel();
        if (Host is { } h)
        {
            var end = Task.Run(async () => { await h.ShutdownAsync(); await h.DisposeAsync(); });
            await Task.WhenAny(end, Task.Delay(12_000));
        }
    }

    // --- read-out ------------------------------------------------------------------------------------------------------

    volatile bool _active;
    volatile string? _focusedSource;   // "nearby", a conversation's session id, or null: what the person is in
    readonly HashSet<string> _toldSessions = new();

    /// <summary>The answer to the person's own key or command: cuts in.</summary>
    public void Answer(string text)
    {
        Reader?.Answer(text);
        if (InvokeRequired) BeginInvoke(() => AppendChat(text));
        else AppendChat(text);
    }

    /// <summary>Something that happened by itself (an arrival): in turn, like what arrives.</summary>
    void Announce(string text)
    {
        Reader?.Say(AnnounceKind.Notices, text);
        AppendChat(text);
    }

    bool _inWalk;

    void FocusChanged()
    {
        if (!_active || _inWalk) { _focusedSource = null; _imInFocus = null; return; }
        _focusedSource = _areas.SelectedTab == _chatPage ? "nearby"
            : _areas.SelectedTab == _conversationsPage && _conversations.SelectedTab?.Tag is Conversation cv ? cv.SessionId : null;
        _imInFocus = _areas.SelectedTab == _conversationsPage && _conversations.SelectedTab?.Tag is Conversation im ? im : null;
    }

    static string SourceOf(FeedEntry e) => e.Kind switch
    {
        FeedKind.Nearby => "nearby",
        FeedKind.Notice => "notices",
        _ => e.Session ?? "",
    };

    /// <summary>The announcer's line for an entry (on its thread): muted all or by source, or worded for where the person
    /// is: a conversation's lines without its name while it's the one in focus; the first line of a new IM says so.</summary>
    string? Shape(FeedEntry e, string line)
    {
        if (_set.MuteAll) return null;
        var source = SourceOf(e);
        if (_set.IsMuted(source)) return null;
        if (e.Kind == FeedKind.Notice)
        {
            var said = Sentences(line);
            NoticeSaid(said);
            return said;
        }
        if (e.Kind is FeedKind.Im or FeedKind.Group && e.Session != null)
        {
            bool first;
            lock (_toldSessions) first = _toldSessions.Add(e.Session);
            if (e.Session == _focusedSource) return HistoryLine(e, stamp: false);
            if (first && e.Kind == FeedKind.Im && e.Style != FeedStyle.Object)
                return e.Style == FeedStyle.Emote ? $"New IM: {e.Name} {e.Text}" : $"{e.Name}, new IM: {e.Text}";
        }
        return line;
    }

    // --- a notice cut off --------------------------------------------------------------------------------------------

    (string Text, long At)? _noticeSaid;
    bool _noticeCut;
    nint _focusWas;
    long _focusMovedAt;

    /// <summary>A notice just handed to the reader (the announcer's thread).</summary>
    void NoticeSaid(string line)
    {
        lock (_noticeLock) { _noticeSaid = (line, Environment.TickCount64); _noticeCut = false; }
    }

    readonly object _noticeLock = new();

    /// <summary>The screen reader stops speaking at a key press or a focus move, so a notice said a moment before one is
    /// lost (the guided test: "Control N to answer." gone under Alt+1). If one came before the notice could have been
    /// heard out, it's said again, once, when the keys have rested a moment; not when the focus went to the notices
    /// (they read it there).</summary>
    void NoticeCutOff()
    {
        long now = Environment.TickCount64;
        var focus = GetFocus();
        if (focus != _focusWas) { _focusWas = focus; _focusMovedAt = now; }
        string? again = null;
        lock (_noticeLock)
        {
            if (_noticeSaid is not { } n) return;
            // Control or Shift alone is the screen reader's own key to stop or pause speech: silenced on purpose, not lost
            if (RealInput.SilencedAt > n.At) { _noticeSaid = null; return; }
            // about as long as a quick screen reader voice takes over it, at least a second and at most three
            long heard = n.At + Math.Clamp(n.Text.Length * 45L, 1000, 3000);
            long cut = Math.Max(RealInput.LastKeyAt, _focusMovedAt);
            if (!_noticeCut && cut > n.At && cut < heard) _noticeCut = true;
            if (!_noticeCut) { if (now > heard) _noticeSaid = null; return; }
            if (now - RealInput.LastKeyAt < 700 || now - _focusMovedAt < 700) return;
            _noticeSaid = null;
            if (!_notices.Focused) again = n.Text;
        }
        if (again != null) Reader?.Say(AnnounceKind.Notices, again, keep: false);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint GetFocus();

    void MuteAll()
    {
        _set.MuteAll = !_set.MuteAll;
        _set.Save();
        Reader?.Answer(_set.MuteAll ? "Read-out muted. F6 again to hear what arrives." : "Read-out on.");
    }

    /// <summary>Control+Shift+C: the lines read from the mark to the one under review, onto the clipboard (the old NVDA
    /// add-on's copy). SOJOURN_TEST_NO_CLIPBOARD leaves the clipboard alone (it's the person's desktop's).</summary>
    void CopyFromMark()
    {
        if (Reader is not { } r) return;
        if (r.FromMark() is not { } text) { r.Answer("Nothing marked: Control+Shift+M marks where a copy starts.", keep: false); return; }
        int lines = text.Split(Environment.NewLine).Length;
        bool test = Environment.GetEnvironmentVariable("SOJOURN_TEST_NO_CLIPBOARD") == "1";
        if (!test)
            try { Clipboard.SetText(text); }
            catch (Exception e) { r.Answer($"Couldn't copy: {e.Message}", keep: false); return; }
        r.Answer($"Copied {(lines == 1 ? "1 line" : $"{lines} lines")}{(test ? " (not onto the clipboard: a test)" : "")}.", keep: false);
    }

    void ToggleTimestamps()
    {
        _set.Timestamps = !_set.Timestamps;
        _set.Save();
        Reader?.Answer(_set.Timestamps ? "Times in the histories, from the next line." : "No times in the histories.", keep: false);
    }

    /// <summary>Control+Shift+A: Firestorm's arrivals and departures within chat range read out, or none of them.</summary>
    async Task ToggleArrivalsAsync()
    {
        var all = (await SettingsOp(new JsonObject(), "get"))["settings"] as JsonObject ?? new();
        var radar = new[] { "radar_chat_range", "radar_draw_distance", "radar_region" };
        bool any = radar.Any(n => (all[n] as JsonObject)?.Str("value") is { } v && v != "off");
        var set = new JsonObject();
        foreach (var n in radar) set[n] = any ? "off" : n == "radar_chat_range" ? "both" : "off";
        var r = await SettingsOp(set, "set");
        if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Not changed: {e}", keep: false); return; }
        Reader?.Answer(any ? "Arrivals and departures not read out." : "Arrivals and departures within chat range read out.", keep: false);
        if (_areas.SelectedTab == _settingsPage) _ = RefreshSettings();
    }

    void MuteSource()
    {
        var source = _focusedSource ?? (_areas.SelectedTab == _conversationsPage ? null : "nearby");
        if (source == null) { Reader?.Answer("Not in a conversation."); return; }
        var name = source == "nearby" ? "Nearby chat" : (_conversations.SelectedTab?.Tag as Conversation)?.Title ?? "This conversation";
        bool muted = _set.ToggleMuted(source);
        Reader?.Answer(muted ? $"{name} muted." : $"{name} read out again.");
    }

    // --- keys -----------------------------------------------------------------------------------------------------------

    const string KeysHelp = "Alt+1 nearby chat, Alt+2 conversations, Alt+3 people, Alt+4 inventory, Alt+5 search, Alt+6 places, Alt+7 objects, Alt+8 notices, Alt+9 the assistant, Alt+0 settings. "
        + "Control+Tab next conversation, Control+W closes one. Enter says, Shift+Enter whispers, Control+Enter shouts; "
        + "slash slash commands in the Say box, //help lists them. Control+N the newest notice; on a notice, numbers press its "
        + "keys and Delete puts it away. Control+G teleports to the SLurl on the clipboard. Applications key: a person's or "
        + "object's actions, and a profile from any list of people. F5 refreshes a list. F6 mutes read-out, Shift+F6 this conversation. Control+left bracket and "
        + "right bracket go back and forward through what was read, with Shift the oldest and newest, Control+R repeats; Control+Shift+M "
        + "marks a line and Control+Shift+C copies from the mark to the line you're on; Control+Shift+T times in the histories; "
        + "Control+Shift+A arrivals and departures read out. Control+D describes what's selected, or where you are; Control+Shift+D you and what you're wearing; "
        + "the same key again tells more. F3 walk mode: arrows walk, F1 there for its keys. In the inventory: Enter wears or opens, Space "
        + "marks, Control+X, C and V move and copy, Delete deletes, F2 renames, Control+F searches; Alt+4 again opens a second pane, Control+M moves "
        + "what's marked into the folder selected there. In Objects, Tab goes to the selected one's parts and a part's faces; Enter touches. "
        + "Control+Shift+W what you're wearing. F4 held talks in voice, Shift+F4 switches to an open mic, Control+Shift+V who's in voice. "
        + "Control+Shift+I incognito on and off. //about says which version this is.";

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (TalkDown(keyData)) return true;
        switch (keyData)
        {
            case Keys.Alt | Keys.D1: GoArea(_chatPage); return true;
            case Keys.Alt | Keys.D2: GoArea(_conversationsPage); return true;
            case Keys.Alt | Keys.D3: GoArea(_peoplePage); return true;
            case Keys.Alt | Keys.D4: if (InInventory && _pane.Tree.Focused) OtherPane(); else GoArea(_inventoryPage); return true;
            case Keys.Alt | Keys.D5: GoArea(_searchPage); return true;
            case Keys.Alt | Keys.D6: GoArea(_placesPage); return true;
            case Keys.Alt | Keys.D7: GoArea(_objectsPage); return true;
            case Keys.Alt | Keys.D8: GoArea(_noticesPage); return true;
            case Keys.Alt | Keys.D9: GoArea(_assistantPage); return true;
            case Keys.Alt | Keys.D0: GoArea(_settingsPage); return true;
            case Keys.Control | Keys.Tab: Ring(+1); return true;
            case Keys.Control | Keys.Shift | Keys.Tab: Ring(-1); return true;
            case Keys.Control | Keys.W: if (InInventory && _pane == _pane2) CloseSecondPane(); else CloseConversation(); return true;
            case Keys.Control | Keys.N: NewestNotice(); return true;
            case Keys.Control | Keys.G: TeleportClipboard(); return true;
            case Keys.F6: MuteAll(); return true;
            case Keys.Shift | Keys.F6: MuteSource(); return true;
            case Keys.Control | Keys.OemOpenBrackets: Reader?.Older(); return true;
            case Keys.Control | Keys.OemCloseBrackets: Reader?.Newer(); return true;
            case Keys.Control | Keys.R: Reader?.Repeat(); return true;
            case Keys.Control | Keys.Shift | Keys.OemOpenBrackets: Reader?.Oldest(); return true;
            case Keys.Control | Keys.Shift | Keys.OemCloseBrackets: Reader?.Repeat(); return true;
            case Keys.Control | Keys.Shift | Keys.M: if (Reader is { } rm) rm.Answer(rm.Mark(), keep: false); return true;
            case Keys.Control | Keys.Shift | Keys.C: CopyFromMark(); return true;
            case Keys.Control | Keys.Shift | Keys.T: ToggleTimestamps(); return true;
            case Keys.Control | Keys.Shift | Keys.A: _ = ToggleArrivalsAsync(); return true;
            case Keys.F1: Reader?.Answer(_walkPad.Focused ? Sojourn.Access.Walk.WalkMode.Help : KeysHelp, keep: false); return true;
            case Keys.F3: ToggleWalk(); return true;
            case Keys.Control | Keys.D: DescribeKey(me: false); return true;
            case Keys.Control | Keys.Shift | Keys.D: DescribeKey(me: true); return true;
            case Keys.Control | Keys.Shift | Keys.W: _ = OutfitListAsync(); return true;
            case Keys.Control | Keys.Shift | Keys.I: ToggleIncognito(); return true;
            case Keys.Control | Keys.Shift | Keys.V: SayVoice(); return true;
            case Keys.Escape when _walkPad.Focused: ToggleWalk(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>The assistant's walks (LLAgent startAutoPilot, the session's own autopilot) around what's in the way: the
    /// path's corners as waypoints (Motion.Router); with no way at all, a teleport there where the region allows
    /// (walk_teleport_fallback), said.</summary>
    async Task<IReadOnlyList<LibreMetaverse.Vector3>?> RouteAsync(Sojourn.Access.Walk.PathPlanner planner, LibreMetaverse.Vector3 from, LibreMetaverse.Vector3 to)
    {
        var a = new System.Numerics.Vector2(from.X, from.Y);
        var b = new System.Numerics.Vector2(to.X, to.Y);
        if (System.Numerics.Vector2.Distance(a, b) < 2) return null;
        var plan = await planner.PlanAsync(a, _walk?.Me()?.Feet ?? from.Z - 0.95f, b);
        Sojourn.Access.Walk.WalkMode.Log($"route {a} -> {b}: {(plan == null ? (planner.CanAsk ? "no way" : "no view") : plan.Direct ? "direct" : string.Join(" ", plan.Waypoints))}");
        if (plan == null)
        {
            if (!planner.CanAsk || !_set.WalkTeleportFallback || _ops.Me() is not { } me) return null;
            Reader?.Answer("No way around found: teleporting there.");
            var r = await _ops.DoAsync("LLAgent", "requestTeleport", new JsonObject
            {
                ["regionname"] = me.Region, ["x"] = (double)to.X, ["y"] = (double)to.Y, ["z"] = (double)Math.Max(to.Z, me.Pos.Z),
                ["skip_confirmation"] = true, ["timeout"] = 20,
            });
            if (PersonOps.Error(r) is { } e) Reader?.Answer($"Teleport didn't work: {e}");
            return new[] { to };
        }
        if (plan.Direct) return null;
        var legs = plan.Waypoints.Select(w => new LibreMetaverse.Vector3(w.X, w.Y, to.Z)).ToList();
        if (!plan.GoalMoved) legs[^1] = to;
        return legs;
    }

    /// <summary>F3: into walk mode (the walk pad takes the keys) and back to where the focus was.</summary>
    void ToggleWalk()
    {
        if (_walk == null) { Reader?.Answer("Not in a region yet."); return; }
        if (_walkPad.Focused)
        {
            var back = _beforeWalk is { IsDisposed: false, Visible: true, CanFocus: true } b ? b : FirstControl(_areas.SelectedTab!);
            back?.Focus();
            Reader?.Answer("Walk mode off.", keep: false);
            return;
        }
        _beforeWalk = ActiveControl == _walkPad ? null : ActiveControl;
        _walkPad.Focus();
        Reader?.Answer("Walk mode. Arrows walk, F1 for its keys.", keep: false);
    }

    /// <summary>`//status music`: the parcel's stream as it is, or null when the parcel has none.</summary>
    string? MusicWords()
    {
        if (Host?.Audio is not { } audio) return Host?.Session.Online == true ? "Sounds are off in this session (--no-sounds), music too." : null;
        var m = audio.Music;
        if (m.ParcelUrl.Length == 0) return null;
        if (!Host.Settings.Music) return "This parcel has music; it's turned off (Alt+0, The parcel's music).";
        return m.State switch
        {
            "playing" => m.Title is { Length: > 0 } t ? $"Playing: {t}." : "Music playing, no title given.",
            "connecting" or "buffering" or "reconnecting" => $"Music {m.State}.",
            var s => $"Music: {s}.",
        };
    }

    void GoArea(TabPage page)
    {
        // a list is read again when its area opens, and when its key is pressed while it's open
        if (_areas.SelectedTab == page) AreaShown();
        _areas.SelectedTab = page;
        FirstControl(page)?.Focus();
        FocusChanged();
    }

    Control? FirstControl(TabPage page) => page == _chatPage ? _chatInput
        : page == _conversationsPage ? (_conversations.SelectedTab?.Tag as Conversation)?.Input ?? (Control)_noConversations
        : page == _peoplePage ? _people
        : page == _inventoryPage ? _pane.Tree
        : page == _placesPage ? _landmarks
        : page == _searchPage ? _searchWords
        : page == _objectsPage ? _objects
        : page == _noticesPage ? _notices
        : page == _assistantPage ? _assistantInput
        : page == _settingsPage ? _settingsList : null;

    /// <summary>Ctrl+Tab: nearby chat and the conversations as one ring, as Radegast's tabs.</summary>
    void Ring(int dir)
    {
        var ring = new List<(TabPage Area, TabPage? Conv)> { (_chatPage, null) };
        foreach (TabPage p in _conversations.TabPages) ring.Add((_conversationsPage, p));
        int at = _areas.SelectedTab == _chatPage ? 0
            : _areas.SelectedTab == _conversationsPage && _conversations.SelectedTab != null ? 1 + _conversations.SelectedIndex : -1;
        int next = at < 0 ? (dir > 0 ? 0 : ring.Count - 1) : ((at + dir) % ring.Count + ring.Count) % ring.Count;
        var (area, conv) = ring[next];
        if (conv != null) _conversations.SelectedTab = conv;
        GoArea(area);
    }

    void TeleportClipboard()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { text = ""; }
        if (Places.Parse(text) is not { } place) { Reader?.Answer("There's no SLurl on the clipboard."); return; }
        _ = _cmd.GoToAsync(place);
    }

    /// <summary>The person's own action that waits for their confirmation (giving, deleting, paying): they asked for it,
    /// so the confirmation is selected in Notices with the focus on it (Enter confirms, Escape cancels), and once it's
    /// answered the focus goes back where it was. It's this call's own confirmation (raised for these arguments) that's
    /// selected, never another showing first (the assistant's); both moves are our own for the person, so their next
    /// key answers where it put them. Null when the person cancelled it here ("Cancelled." is all they hear).</summary>
    async Task<JsonObject?> ConfirmedAsync(string pump, string op, JsonObject args)
    {
        var back = ActiveControl;
        var backPage = _areas.SelectedTab;
        var call = _ops.GatedAsync(pump, op, args);
        PendingConfirm? mine = null;
        while (!call.IsCompleted)
        {
            if (Host!.Assistant.Panel.Read(p => p.Confirm) is { } pc && ReferenceEquals(pc.For, args)) { mine = pc; break; }
            await Task.WhenAny(call, Task.Delay(100));
        }
        bool shown = !call.IsCompleted;
        if (shown)
            RealInput.Ours(() =>
            {
                PollNotices();
                GoArea(_noticesPage);
                if (_notices.Items["confirm"] is { Tag: Notice { Confirm: { } c } } item && ReferenceEquals(c.For, args))
                {
                    item.Selected = true;
                    item.Focused = true;
                }
                NoteSelection();
            });
        var r = await call;
        // (a control on a tab page that isn't showing can't take the focus: the page first)
        if (shown && back is { IsDisposed: false } && backPage != null && _areas.SelectedTab == _noticesPage)
            RealInput.Ours(() =>
            {
                _areas.SelectedTab = backPage;
                back.Focus();
                FocusChanged();
            });
        // cancelled by the person here: "Cancelled." was said, and the op's refusal adds nothing for them
        return shown && ReferenceEquals(_cancelledHere, mine) ? null : r;
    }

    // --- the session's own events: arrivals ---------------------------------------------------------------

    readonly CancellationTokenSource _eventsStop = new();
    long _teleportedAt;

    void StartEvents()
    {
        var s = Host!.Session;
        var stop = _eventsStop.Token;
        new Thread(() =>
        {
            long since = s.Events.Seq;
            while (!stop.IsCancellationRequested)
            {
                List<string> lines;
                try { lines = s.Events.GetRawAsync(since, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
                catch { Thread.Sleep(1000); continue; }
                foreach (var line in lines)
                {
                    try
                    {
                        if (JsonNode.Parse(line) is not JsonObject ev) continue;
                        since = Math.Max(since, (long?)ev["seq"] ?? since);
                        var kind = (string?)ev["kind"];
                        bool ok = ev["data"]?["success"]?.GetValue<bool>() == true;
                        if (kind == "LLTeleport" && ok) BeginInvoke(Teleported);
                    }
                    catch { /* one odd event doesn't stop the rest */ }
                }
            }
        }) { IsBackground = true, Name = "access events", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>A teleport arrived that this app didn't start (an offer taken, home, the assistant): where.</summary>
    void Teleported()
    {
        _teleportedAt = Environment.TickCount64;
        if (_cmd.TeleportingSelf || _ops.Me() is not { } me) return;
        _lastRegion = me.Region;
        Announce($"Arrived in {me.Region}.");
    }
}
