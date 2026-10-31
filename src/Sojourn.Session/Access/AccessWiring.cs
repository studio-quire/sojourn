using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sojourn.Session.Assistant;
using Sojourn.Session.Control;
using Sojourn.Session.Social;
using Sojourn.Core;
using Sojourn.Core.Access;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Access;

/// <summary>
/// The person's screen reader in this client (the shared part is Sojourn.Core/Access: the voices, the announcer, the
/// actions): what arrives is read out through it (nearby chat, objects, IMs, group chat, notices, the assistant's
/// replies and questions, the parcel's song as it changes, a confirmation the moment it waits), and the app's keys
/// (the VR client's Access/ReaderKeys.cs, over <see cref="Actions"/>) answer what waits. Off unless a screen reader
/// runs or the person turns it on. Its settings are the menu's reader section and SojournSettings; SojournScreenReader
/// status says what it's doing.
/// </summary>
public sealed class AccessWiring : IDisposable
{
    public readonly Announcer Announcer;
    public readonly ReaderActions Actions;
    readonly AgentSession _s;
    readonly VrSettings _st;
    readonly AssistantHost _assistant;
    readonly SocialFeed _feed;
    readonly Audio.MusicStream? _music;
    readonly Thread _thread;
    volatile bool _stop;

    AnnounceSettings St => _st.ScreenReader;

    AccessWiring(AgentSession s, VrSettings st, AssistantHost assistant, SocialFeed feed, Audio.MusicStream? music, nint hwnd,
        Func<bool> inHeadset, Func<int, string?>? keyName, IReaderVoice? voice, string? greeting, Action<string> log)
    {
        _s = s;
        _st = st;
        _assistant = assistant;
        _feed = feed;
        _music = music;
        var root = Sidecar.Root ?? AppContext.BaseDirectory;
        Announcer = new Announcer(st.ScreenReader, new[] { AppContext.BaseDirectory, Path.Combine(root, "local", "nvda", "x64") },
            voice ?? UiaVoice.For(hwnd, "Sojourn", log), s.Private, log)
        {
            // the headset blanks the PC's monitor while incognito; the PC's reader keeps quiet the same way
            Quiet = () => inHeadset() && s.Private.On,
            // nothing others or the assistant wrote beside a confirmation (the panel's rule): it follows once answered
            Hold = () => assistant.Confirmations.Pending,
            Greeting = greeting ?? "Sojourn. F1 for keys.",
        };
        Actions = new ReaderActions(s, Announcer, n => NoticeText(n))
        {
            Gate = new PanelGate(assistant),
            ConfirmKeys = "Enter confirms, Escape cancels.",
            PutAwayKey = "Delete puts it away.",
        };
        if (keyName != null) Actions.KeyName = keyName;
        // while the reader reads the assistant's replies, its own voice (spoken replies) keeps quiet: one voice
        assistant.ReaderSpeaksReplies = () => Announcer.Active && St.Replies;
        Announcer.ActiveChanged += assistant.PrefsChanged;
        s.Private.Changed += (on, _, _) => Announcer.Answer(on ? (s.Private.Sees ? "Incognito on, the assistant sees." : "Incognito on.") : "Incognito off.");
        s.Private.SeesChanged += sees => { if (s.Private.On) Announcer.Answer(sees ? "The assistant sees." : "The assistant doesn't see."); };
        _thread = new Thread(Run) { IsBackground = true, Name = "screen reader watch", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>On the main thread (UIA's window hook is the window's: hwnd, the app's window). keyName: the key that
    /// answers a notice's button i, in words (the app's keys). voice: the app's own UI Automation voice (Sojourn Access
    /// raises notifications from its standard controls), in place of one hooked onto hwnd; greeting: said on becoming
    /// active.</summary>
    public static AccessWiring Start(AgentSession s, VrSettings st, AssistantHost assistant, SocialFeed feed, Audio.MusicStream? music,
        nint hwnd, Func<bool> inHeadset, Func<int, string?>? keyName, ILogger log, IReaderVoice? voice = null, string? greeting = null) =>
        new(s, st, assistant, feed, music, hwnd, inHeadset, keyName, voice, greeting, m => log.LogInformation("{Msg}", m));

    /// <summary>The app's say over each line before it's read (Sojourn Access: the source named only when it isn't the
    /// conversation in focus, a source or everything muted): the entry and the line as worded here, returning the line
    /// to read or null for none. Off the main thread.</summary>
    public Func<FeedEntry, string, string?>? Shape { get; set; }

    /// <summary>The assistant's replies read whole, a few sentences at a time in turn, instead of cut short past the
    /// announcer's length (Sojourn Access, whose rich descriptions run long and which has no panel to read them on).</summary>
    public bool WholeReplies { get; set; }

    /// <summary>The parcel's song titles left unsaid while this says so (Sojourn Access's F6, which mutes everything
    /// that arrives; the feed's lines go through <see cref="Shape"/>). Null: always said.</summary>
    public Func<bool>? MusicMuted { get; set; }

    /// <summary>The person's own lines read back too, nearby once the region has echoed them (so it's heard that they
    /// were sent), IMs as they go (the grid echoes none). Null: never (the VR client, where the person sees them).</summary>
    public Func<bool>? ReadMine { get; set; }

    sealed class PanelGate(AssistantHost a) : IConfirmGate
    {
        public ConfirmView? Waiting => a.Panel.Read(p => p.Confirm) is { } pc ? new ConfirmView(pc, pc.Title, pc.Detail, pc.ArmedAt) : null;
        public void Answer(object token, bool yes) => a.Confirmations.Answer((PendingConfirm)token, yes);
    }

    // --- watching what arrives ------------------------------------------------------------------------------------------

    long _seq = -1;
    readonly HashSet<Guid> _noticesTold = new();
    bool _keysHinted;
    string? _result, _choices, _ask, _notice, _title;
    long _draft = -1;

    void Run()
    {
        while (!_stop)
        {
            Thread.Sleep(100);
            if (!Announcer.Active) { Skip(); continue; }
            try
            {
                Actions.WatchConfirm();
                Feed();
                Panel();
                Music();
            }
            catch (Exception e) { Console.Error.WriteLine($"screen reader: {e.GetType().Name}: {e.Message}"); }
        }
    }

    /// <summary>Not speaking: keep up with what's there, so turning it on doesn't read out the past.</summary>
    void Skip()
    {
        _seq = _feed.Snapshot().Entries.Select(e => e.Seq).DefaultIfEmpty(_seq).Max();
        var (result, draft) = _assistant.Panel.Read(p => (p.Result, p.Draft?.Seq ?? -1));
        _result = result;
        _draft = draft;
        _title = _music?.Title;
    }

    void Feed()
    {
        var (entries, _) = _feed.Snapshot();
        if (_seq < 0) { _seq = entries.Select(e => e.Seq).DefaultIfEmpty(0).Max(); return; }
        foreach (var e in entries.Where(e => e.Seq > _seq).OrderBy(e => e.Seq))
        {
            _seq = e.Seq;
            if (e.Mine && ReadMine?.Invoke() != true) continue;
            if (e.Kind == FeedKind.Notice)
            {
                // a recalled or replaced notice is said once; the next page of a menu answered by key is read whole
                if (e.NoticeId != Guid.Empty && (!_noticesTold.Add(e.NoticeId) || Actions.FollowUp(e.NoticeId))) continue;
                bool keys = e.NoticeId != Guid.Empty && _s.Notify.Get(e.NoticeId) is { } n && ReaderActions.Buttons(n).Count > 0;
                var line = Join(e.Name, e.Text);
                if (keys && !_keysHinted && St.Notices) { line += " Control N to answer."; _keysHinted = true; }
                if (Shaped(e, line) is { } notice) Announcer.Say(AnnounceKind.Notices, notice);
                continue;
            }
            var kind = e.Style == FeedStyle.Object ? AnnounceKind.Objects
                : e.Kind == FeedKind.Nearby ? AnnounceKind.Nearby : e.Kind == FeedKind.Group ? AnnounceKind.Group : AnnounceKind.Ims;
            if (Shaped(e, Line(e)) is { } said) Announcer.Say(kind, said);
        }
    }

    string? Shaped(FeedEntry e, string line) => Shape is { } f ? f(e, line) : line;

    /// <summary>Who said it first, then how and where, then the words.</summary>
    static string Line(FeedEntry e)
    {
        var name = e.Name.Length > 0 ? e.Name : "someone";
        return (e.Kind, e.Style) switch
        {
            (FeedKind.Im, FeedStyle.Emote) => $"{name} {e.Text} (IM)",
            (FeedKind.Im, _) => $"{name}, IM: {e.Text}",
            (FeedKind.Group, FeedStyle.Emote) => $"{name} {e.Text} ({e.SessionName})",
            (FeedKind.Group, _) => $"{name}, {e.SessionName}: {e.Text}",
            (_, FeedStyle.Emote) => $"{name} {e.Text}",
            (_, FeedStyle.Whisper) => $"{name} whispers: {e.Text}",
            (_, FeedStyle.Shout) => $"{name} shouts: {e.Text}",
            _ => $"{name}: {e.Text}",
        };
    }

    /// <summary>A notice's words, as the feed puts them: "Kestrel offers a teleport", "Tidewater Harp: Pick a song".</summary>
    string NoticeText(JsonObject n)
    {
        var (who, what, _) = _feed.NoticeLine(n);
        return Join(who, what);
    }

    // a verb phrase follows the name ("Kestrel is online"); a message of its own gets a colon ("Harp: Pick a song")
    static string Join(string who, string what) =>
        who.Length == 0 ? what : what.Length > 0 && char.IsLower(what[0]) ? $"{who} {what}" : $"{who}: {what}";

    void Panel()
    {
        var (result, prompt, choices, pointAsk, typeAsk, typing, notice, draft) = _assistant.Panel.Read(p =>
            (p.Result, p.Prompt, string.Join(". ", p.Choices.Select((c, i) => $"{i + 1}, {c.Label}")), p.PointAsk, p.TypeAsk, p.Typing, p.Notice, p.Draft));
        if (!string.IsNullOrWhiteSpace(result) && result != _result)
        {
            // an answer growing (streamed) is read on from where it was
            var said = _result != null && result.StartsWith(_result, StringComparison.Ordinal) ? result[_result.Length..] : result;
            if (said.Trim().Length == 0) { }
            else if (WholeReplies) foreach (var part in Announcer.Pieces(said)) Announcer.Say(AnnounceKind.Replies, part);
            else Announcer.Say(AnnounceKind.Replies, said);
        }
        _result = result;
        var choiceWords = choices.Length > 0 ? $"{prompt} {choices}." : null;
        if (choiceWords != null && choiceWords != _choices) Announcer.Say(AnnounceKind.Replies, choiceWords.Trim());
        _choices = choiceWords;
        var ask = pointAsk ?? (typeAsk != null ? $"{typeAsk} Typed: {typing}. Enter sends, Escape cancels." : null);
        if (ask != null && ask != _ask) Announcer.Say(AnnounceKind.Replies, ask);
        _ask = ask;
        if (notice != null && notice != _notice) Announcer.Say(AnnounceKind.Replies, notice == "not sent" ? "Not sent." : notice);
        _notice = notice;
        // dictated words waiting for the person: their own words, read back so they can check before sending
        if (draft is { } d && d.Seq != _draft)
            Announcer.Answer($"Draft to {(d.To == DraftTo.Nearby ? "nearby" : d.Name ?? "the conversation")}: {d.Text}. Enter sends, Escape cancels.");
        _draft = draft?.Seq ?? -1;
    }

    void Music()
    {
        if (_music == null) return;
        var title = _music.On && _music.State == "playing" ? _music.Title : null;
        if (!string.IsNullOrWhiteSpace(title) && title != _title && MusicMuted?.Invoke() != true) Announcer.Say(AnnounceKind.Music, $"Now playing: {title}");
        if (title != null) _title = title;
    }

    // --- settings: the menu's reader section and SojournSettings ----------------------------------------------------

    static readonly (AnnounceKind Kind, string Label, string Name, string Desc)[] Kinds =
    {
        (AnnounceKind.Nearby, "nearby chat", "announce_nearby", "what people nearby say"),
        (AnnounceKind.Objects, "objects", "announce_objects", "what objects say (greeters, HUDs, games, llOwnerSay; the same line again within 10 s once)"),
        (AnnounceKind.Ims, "IMs", "announce_ims", "instant messages"),
        (AnnounceKind.Group, "group chat", "announce_group", "group chat, with the group's name"),
        (AnnounceKind.Notices, "notices", "announce_notices", "offers, script menus, invitations, friends online, alerts (Ctrl+N steps through those waiting)"),
        (AnnounceKind.Replies, "the assistant", "announce_replies", "the assistant's replies, choices and questions (its own spoken replies keep quiet meanwhile)"),
        (AnnounceKind.Music, "music titles", "announce_music", "the parcel's stream: the song's title when it changes"),
    };

    string Found => Announcer.Reader is { } r ? $"{r} running" + (Announcer.Active ? "" : ", not reading") : Announcer.Active ? "none found, reading anyway" : "none running";

    public void Settle(ISettingsMenu? menu, SettingsApi api)
    {
        if (menu != null) Announcer.ActiveChanged += menu.Changed;
        if (menu != null) menu.ReaderRows = () =>
        {
            var rows = new List<(MenuRow, Action<int>)>
            {
                (MenuRow.Choice("read out", AnnounceSettings.Modes, Math.Max(0, Array.IndexOf(AnnounceSettings.Modes, St.Mode))) with { Note = Found },
                    i => { St.Mode = AnnounceSettings.Modes[i]; Announcer.Recheck(); }),
            };
            foreach (var k in Kinds)
                rows.Add((MenuRow.Switch(k.Label, St.Wants(k.Kind)), _ => { St.Set(k.Kind, !St.Wants(k.Kind)); _assistant.PrefsChanged(); }));
            return rows;
        };
        api.Add("screen_reader", new("screen reader", "what arrives (chat, IMs, notices, the assistant's replies, song titles) read out through "
            + "the person's own screen reader (NVDA by its API, others by UI Automation), and keys for notices and confirmations: auto (the "
            + "default) while a screen reader runs, on, or off", "auto, on, off",
            () => St.Mode, SettingsApi.Choice("screen_reader", AnnounceSettings.Modes, i => { St.Mode = AnnounceSettings.Modes[i]; Announcer.Recheck(); })));
        foreach (var k in Kinds)
            api.Add(k.Name, new("screen reader", "read out through the screen reader: " + k.Desc, "on, off",
                () => St.Wants(k.Kind), SettingsApi.Switch(k.Name, on => { St.Set(k.Kind, on); _assistant.PrefsChanged(); })));
        _s.Api.Register(new ScreenReaderApi(_s, this));
    }

    public JsonObject Status() => new()
    {
        ["mode"] = St.Mode, ["reading"] = Announcer.Active, ["reader"] = Announcer.Reader, ["through"] = Announcer.Through,
        ["spoken"] = Announcer.Spoken, ["notice_picked"] = Actions.Picked,
    };

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
        Announcer.Dispose();
    }
}

/// <summary>SojournScreenReader: whether Sojourn is reading out through the person's screen reader, which one, and how
/// (the settings are SojournSettings' screen reader section).</summary>
public sealed class ScreenReaderApi : LeapApi
{
    public override string Name => "SojournScreenReader";
    public override string Desc => "The person's screen reader: whether what arrives is read out through it, which reader runs, and how";
    public override bool NeedsSession => false;

    public ScreenReaderApi(AgentSession s, AccessWiring w) : base(s)
    {
        Op("status", "-> {mode, reading, reader, through, spoken, notice_picked}: reading out or not (mode auto, on, off), the screen reader "
            + "running (NVDA, JAWS, Narrator...), what speech goes through (NVDA, UI Automation), lines spoken this session", _ => w.Status());
    }
}
