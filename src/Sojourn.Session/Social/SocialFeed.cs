using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Social;

public enum FeedKind { Nearby, Im, Group, Notice }

/// <summary>How a line was said: emotes read "Name waves"; whispers are quieter (dim), shouts louder (semibold); objects
/// talk in dim ink, as the viewer greys them apart from people.</summary>
public enum FeedStyle { Say, Emote, Whisper, Shout, Object }

/// <summary>One thing said or one notice. Session: the IM or group conversation it belongs to (null nearby). Mine: the
/// person's own words (or the assistant's, sent as them).</summary>
public sealed record FeedEntry(long Seq, FeedKind Kind, string? Session, string? SessionName, UUID From, string Name, string Text,
    FeedStyle Style, bool Mine, double At, double Life)
{
    /// <summary>A notice still waiting for an answer (Notifications): the person can answer or put it away on the feed.
    /// Empty for everything else.</summary>
    public Guid NoticeId { get; init; }

    /// <summary>A notice's notification name (ScriptDialog, TeleportOffered, FriendOnlineOffline ...); null for
    /// everything else and for the region's own messages.</summary>
    public string? NoticeName { get; init; }

    /// <summary>A keyword alert or a mention of the person (SocialWiring): lit on the feed, with a sound.</summary>
    public bool Alert { get; init; }

    /// <summary>The line in the person's language, once a translator has it (Translation.cs); null: none.</summary>
    public string? Translation { get; init; }
}

/// <summary>
/// What the person should see of other people's words: nearby chat, IMs, group chat and the notices that would be
/// toasts in the viewer (offers, script menus, friends coming online). Read from the session's event log, the same
/// stream the assistant reads (so mutes and the viewer's filtering already apply), plus typing from the protocol
/// (not in the log). Thread-safe; the drawing reads <see cref="Snapshot"/> and <see cref="Version"/>.
/// </summary>
public sealed class SocialFeed
{
    readonly AgentSession _s;
    readonly object _lock = new();
    readonly List<FeedEntry> _entries = new();
    readonly Dictionary<UUID, double> _typingNearby = new(), _typingIm = new(), _said = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _version = 1, _seq;

    public double Now => _clock.Elapsed.TotalSeconds;
    public long Version { get { lock (_lock) return _version; } }

    /// <summary>Whether a line from someone else is an alert (keywords, a mention of the person: SocialWiring): kind,
    /// style, its words as shown, and whether it mentions the person (the viewer's @mention link to them).</summary>
    public Func<FeedKind, FeedStyle, string, bool, bool>? Alerting { get; set; }

    /// <summary>A line or notice joined the feed (off the feed's lock; the alert sound, the translator).</summary>
    public event Action<FeedEntry>? Joined;

    public SocialFeed(AgentSession session)
    {
        _s = session;
        lock (AgentSession.ClientHooks) AgentSession.ClientHooks.Add(Hook);
        if (session.Client is { } now) Hook(now);
        var t = new Thread(Read) { IsBackground = true, Name = "social feed", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    // --- what's shown -----------------------------------------------------------------------------------------------

    /// <summary>The entries still in their time on screen, oldest first; and who is typing to the person in IMs. A notice
    /// answered or put away (here, by the assistant, or replaced by the next menu from its object) leaves at once.</summary>
    public (List<FeedEntry> Entries, List<UUID> TypingIm) Snapshot()
    {
        double now = Now;
        lock (_lock)
        {
            Expire(now);
            return (_entries.ToList(), _typingIm.Where(kv => kv.Value > now).Select(kv => kv.Key).ToList());
        }
    }

    // --- notices waiting for the person -------------------------------------------------------------------------------

    long _noticesSeen = -1;

    /// <summary>The notifications' version: a notice answered elsewhere leaves the feed (checked when drawing).</summary>
    public long NoticesVersion => _s.Notify.Version;

    /// <summary>Waiting notices (offers, menus, invitations, questions) that aren't on the feed now: what the bell holds.</summary>
    public int Folded
    {
        get
        {
            HashSet<Guid> shown;
            lock (_lock) shown = _entries.Where(e => e.NoticeId != Guid.Empty).Select(e => e.NoticeId).ToHashSet();
            return Waiting().Count(id => !shown.Contains(id));
        }
    }

    /// <summary>How many notices wait for an answer.</summary>
    public int WaitingCount => Waiting().Count();

    /// <summary>The notifications that wait for an answer, oldest first.</summary>
    IEnumerable<Guid> Waiting() => _s.Notify.All.Select(n => Guid.TryParse(S(n, "id"), out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty && _s.Notify.Answerable(id));

    /// <summary>The bell: every waiting notice back on the feed (for another two minutes), oldest first.</summary>
    public void Recall()
    {
        var waiting = _s.Notify.All.ToList();
        lock (_lock)
        {
            double now = Now;
            foreach (var n in waiting)
            {
                if (!Guid.TryParse(S(n, "id"), out var id) || !_s.Notify.Answerable(id)) continue;
                int at = _entries.FindIndex(e => e.NoticeId == id);
                if (at >= 0) { _entries[at] = _entries[at] with { Life = Math.Max(_entries[at].Life, now - _entries[at].At + 120) }; continue; }
                var (who, what, _) = NoticeLine(n);
                _entries.Add(new FeedEntry(++_seq, FeedKind.Notice, null, null, Id(n["payload"] as JsonObject ?? new JsonObject(), "from_id"), who, what,
                    FeedStyle.Say, false, now, 120) { NoticeId = id, NoticeName = S(n, "name") });
            }
            _version++;
        }
    }

    /// <summary>Drops entries for notices no longer waiting (answered, put away, replaced, expired). Under the lock.</summary>
    void DropAnswered()
    {
        long v = _s.Notify.Version;
        if (v == _noticesSeen) return;
        _noticesSeen = v;
        int before = _entries.Count;
        _entries.RemoveAll(e => e.NoticeId != Guid.Empty && _s.Notify.Get(e.NoticeId) == null);
        if (_entries.Count != before) _version++;
    }

    /// <summary>The person put a notice away (its ×): unanswered, as the viewer's toast closed and cleared.</summary>
    public void DismissNotice(Guid id)
    {
        _s.Notify.Dismiss(id);
        lock (_lock) { _entries.RemoveAll(e => e.NoticeId == id); _version++; }
    }

    /// <summary>Every waiting notice put away unanswered (the feed's clear key).</summary>
    public void ClearNotices()
    {
        foreach (var n in _s.Notify.All.ToList())
            if (Guid.TryParse(S(n, "id"), out var id)) _s.Notify.Dismiss(id);
        lock (_lock) { _entries.RemoveAll(e => e.NoticeId != Guid.Empty); _version++; }
    }

    /// <summary>Typing in nearby chat now.</summary>
    public bool TypingNearby(UUID id) { lock (_lock) return _typingNearby.TryGetValue(id, out var until) && until > Now; }

    /// <summary>The person put the feed away (its close key): everything on it now goes; new words bring it back.</summary>
    public void Dismiss()
    {
        lock (_lock)
        {
            _entries.Clear();
            _typingIm.Clear();
            _version++;
        }
    }

    /// <summary>Keep these entries on screen at least another <paramref name="seconds"/> (one being read in full, or
    /// the pointer on the feed).</summary>
    public void Keep(IEnumerable<long> seqs, double seconds)
    {
        var want = seqs.ToHashSet();
        if (want.Count == 0) return;
        lock (_lock)
        {
            double now = Now;
            for (int i = 0; i < _entries.Count; i++)
                if (want.Contains(_entries[i].Seq) && _entries[i].At + _entries[i].Life < now + seconds)
                    _entries[i] = _entries[i] with { Life = now + seconds - _entries[i].At };
        }
    }

    /// <summary>Seconds since they last said something nearby (or a large number).</summary>
    public double SinceSaid(UUID id) { lock (_lock) return _said.TryGetValue(id, out var at) ? Now - at : 1e9; }

    void Expire(double now)
    {
        DropAnswered();
        int before = _entries.Count;
        _entries.RemoveAll(e => now - e.At > e.Life);
        bool changed = _entries.Count != before;
        foreach (var d in new[] { _typingNearby, _typingIm })
            foreach (var id in d.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) { d.Remove(id); changed = true; }
        if (_lines.Count > 32)
            foreach (var id in _lines.Where(kv => kv.Value.Count == 0 || kv.Value[^1].At < now - 120).Select(kv => kv.Key).ToList()) _lines.Remove(id);
        if (changed) _version++;
    }

    void Add(FeedKind kind, string? session, string? sessionName, UUID from, string name, string text, FeedStyle style, bool mine, double life,
        Guid notice = default, bool mention = false, string? noticeName = null)
    {
        if (string.IsNullOrWhiteSpace(text) && notice == Guid.Empty) return;
        text = text.Trim();
        bool alert = !mine && kind != FeedKind.Notice && Alerting is { } check && check(kind, style, text, mention);
        FeedEntry e;
        lock (_lock)
        {
            double now = Now;
            e = new FeedEntry(++_seq, kind, session, sessionName, from, name, text, style, mine, now, alert ? Math.Max(life, 90) : life)
                { NoticeId = notice, Alert = alert, NoticeName = noticeName };
            _entries.Add(e);
            if (_entries.Count > 60) _entries.RemoveRange(0, _entries.Count - 60);
            // a line arriving ends their typing
            if (kind == FeedKind.Nearby)
            {
                _typingNearby.Remove(from);
                _said[from] = now;
                if (from != UUID.Zero && style is not FeedStyle.Object)
                {
                    if (!_lines.TryGetValue(from, out var said)) _lines[from] = said = new List<(double, string, FeedStyle)>();
                    said.Add((now, text, style));
                    if (said.Count > 12) said.RemoveAt(0);
                }
            }
            else _typingIm.Remove(from);
            _version++;
        }
        try { Joined?.Invoke(e); } catch (Exception ex) { Console.Error.WriteLine($"chat feed: {ex.GetType().Name}: {ex.Message}"); }
    }

    readonly Dictionary<UUID, List<(double At, string Text, FeedStyle Style)>> _lines = new();

    /// <summary>What someone said nearby in the last <paramref name="within"/> seconds, oldest first (chat bubbles).</summary>
    public List<(double At, string Text, FeedStyle Style)> Said(UUID id, double within)
    {
        lock (_lock)
        {
            if (!_lines.TryGetValue(id, out var said)) return new();
            double now = Now;
            said.RemoveAll(l => l.At < now - 120);
            if (said.Count == 0) _lines.Remove(id);
            return said.Where(l => l.At >= now - within).ToList();
        }
    }

    /// <summary>A line's translation arrived: it shows under the line from now on.</summary>
    public void SetTranslation(long seq, string text)
    {
        lock (_lock)
        {
            int i = _entries.FindIndex(e => e.Seq == seq);
            if (i < 0) return;
            _entries[i] = _entries[i] with { Translation = text };
            _version++;
        }
    }

    /// <summary>Long enough to read, longer for long lines; IMs and offers wait longer (they're for you).</summary>
    static double Life(FeedKind kind, string text) => kind switch
    {
        FeedKind.Nearby => Math.Min(75, 36 + 0.08 * text.Length),
        FeedKind.Notice => Math.Min(90, 30 + 0.07 * text.Length),
        _ => Math.Min(120, 60 + 0.1 * text.Length),
    };

    /// <summary>What objects say (greeters, DJ boards' "now playing", vendors) goes sooner than people's words: in a
    /// busy place it would otherwise keep the feed up without end.</summary>
    static double ObjectLife(string text) => Math.Min(40, 18 + 0.05 * text.Length);

    // --- typing, from the protocol -----------------------------------------------------------------------------------

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<GridClient, object> _hooked = new();

    void Hook(GridClient c)
    {
        if (_hooked.TryGetValue(c, out _)) return;
        _hooked.Add(c, new object());
        c.Self.ChatFromSimulator += (_, e) =>
        {
            if (e.Type is not (ChatType.StartTyping or ChatType.StopTyping) || e.SourceID == c.Self.AgentID) return;
            if (_s.Mutes.IsMuted(e.SourceID, e.FromName, e.OwnerID)) return;
            Typing(_typingNearby, e.SourceID, e.Type == ChatType.StartTyping);
        };
        c.Self.IM += (_, e) =>
        {
            if (e.IM.Dialog is not (InstantMessageDialog.StartTyping or InstantMessageDialog.StopTyping) || e.IM.FromAgentID == c.Self.AgentID) return;
            if (_s.Mutes.IsMuted(e.IM.FromAgentID, e.IM.FromAgentName)) return;
            // someone whose IMs the person's switches keep away (only friends and groups) isn't shown typing either
            if (e.IM.Dialog == InstantMessageDialog.StartTyping && _s.Rejects.FriendsOnly
                && !c.Friends.FriendList.ContainsKey(e.IM.FromAgentID)) return;
            _s.Names.SetLegacy(e.IM.FromAgentID, e.IM.FromAgentName);
            Typing(_typingIm, e.IM.FromAgentID, e.IM.Dialog == InstantMessageDialog.StartTyping);
        };
    }

    void Typing(Dictionary<UUID, double> d, UUID id, bool on)
    {
        lock (_lock)
        {
            // a start without its stop (they walked off, the packet was lost) runs out
            if (on) d[id] = Now + 12;
            else d.Remove(id);
            _version++;
        }
    }

    // --- the event log ------------------------------------------------------------------------------------------------

    void Read()
    {
        long since = _s.Events.Seq;
        while (true)
        {
            List<string> lines;
            try { lines = _s.Events.GetRawAsync(since, TimeSpan.FromSeconds(20)).GetAwaiter().GetResult(); }
            catch { Thread.Sleep(1000); continue; }
            foreach (var line in lines)
            {
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject ev) continue;
                    since = Math.Max(since, (long?)ev["seq"] ?? since);
                    if (ev["data"] is JsonObject d) Take((string?)ev["kind"], d);
                }
                catch { /* one odd event doesn't stop the feed */ }
            }
        }
    }

    static string S(JsonObject d, string key) => d[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    static int I(JsonObject d, string key) => d[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    static UUID Id(JsonObject d, string key) => UUID.TryParse(S(d, key), out var id) ? id : UUID.Zero;

    // a name the person calls them (B2's pseudonyms) before their display name
    string NameOf(UUID id, string fallback) => (id != UUID.Zero ? Pseudonym(id) ?? _s.Names.DisplayName(id) : null) ?? Plain(fallback);

    string? Pseudonym(UUID id)
    {
        try { return _s.People.Pseudonym(id); } catch { return null; }
    }

    void Take(string? kind, JsonObject d)
    {
        switch (kind)
        {
            case "LLChat": Chat(d); break;
            case "TesselEvents" when S(d, "event") == "im": Im(d); break;
            case "TesselEvents" when S(d, "event") == "object_im":
                Add(FeedKind.Im, "object:" + S(d, "object_id"), S(d, "from"), UUID.Zero, S(d, "from"), S(d, "message"), FeedStyle.Object, false,
                    ObjectLife(S(d, "message")));
                break;
            case "ClaudeNotify": Notice(d); break;
        }
    }

    void Chat(JsonObject d)
    {
        int type = I(d, "chat_type"), source = I(d, "source");
        // the viewer's script-error window, not chat (ChatType.Debug); start/stop typing never reach the log
        if (type == (int)ChatType.Debug) return;
        var msg = S(d, "message");
        var from = Id(d, "from_id");
        bool mine = from == _s.AgentId;
        // AgentSession.OnChat prefixes these for the assistant's reading; here how it was said is the style
        if (type == (int)ChatType.Whisper && msg.StartsWith("whispers: ")) msg = msg[10..];
        if (type == (int)ChatType.Shout && msg.StartsWith("shouts: ")) msg = msg[8..];
        var style = I(d, "chat_style") == 1 ? FeedStyle.Emote
            : source == (int)ChatSourceType.Object ? FeedStyle.Object
            : type == (int)ChatType.Whisper ? FeedStyle.Whisper
            : type == (int)ChatType.Shout ? FeedStyle.Shout : FeedStyle.Say;
        if (source == (int)ChatSourceType.System)
        {
            Add(FeedKind.Notice, null, null, UUID.Zero, "", msg, FeedStyle.Say, false, Life(FeedKind.Notice, msg));
            return;
        }
        var name = source == (int)ChatSourceType.Agent ? NameOf(from, S(d, "from")) : S(d, "from");
        bool mention = Mentions(msg);
        msg = Shown(msg);
        Add(FeedKind.Nearby, null, null, from, name, msg.TrimStart(), style, mine, style == FeedStyle.Object ? ObjectLife(msg) : Life(FeedKind.Nearby, msg),
            mention: mention);
    }

    void Im(JsonObject d)
    {
        bool outgoing = S(d, "dir") == "out";
        if (!outgoing && _s.Presence.Quiet) return;   // Do Not Disturb: kept in the conversation, not shown (Agents/Presence.cs)
        var type = S(d, "session_type");
        var kind = type == "p2p" ? FeedKind.Im : FeedKind.Group;
        var from = outgoing ? _s.AgentId : Id(d, "from_id");
        var sessionName = kind == FeedKind.Im
            ? NameOf(outgoing ? Id(d, "to_id") : from, S(d, "session_name"))
            : S(d, "session_name");
        var name = outgoing ? NameOf(_s.AgentId, "") : NameOf(from, S(d, "from"));
        var msg = S(d, "message");
        var style = FeedStyle.Say;
        if (msg.StartsWith("/me ") || msg.StartsWith("/me'")) { style = FeedStyle.Emote; msg = msg[3..].TrimStart(); }
        bool mention = Mentions(msg);
        msg = Shown(msg);
        Add(kind, S(d, "session_id"), sessionName, from, name, msg, style, outgoing, Life(kind, msg), mention: mention);
    }

    /// <summary>The viewer's toasts, as one line each in our words: who, and what they offer or say. Answering them is
    /// the assistant's (its dialogs tool), so nothing here asks for a press.</summary>
    void Notice(JsonObject n)
    {
        // Do Not Disturb holds people's offers back (the bell keeps them), as the viewer holds its toasts
        if (_s.Presence.Quiet && S(n, "type") == "offer" && S(n, "name") != "PlaceOffered") return;
        var (who, what, life) = NoticeLine(n);
        // one still waiting (it has buttons): the person can answer it or put it away here
        var id = Guid.TryParse(S(n, "id"), out var g) && _s.Notify.Get(g) != null ? g : Guid.Empty;
        Add(FeedKind.Notice, null, null, Id(n["payload"] as JsonObject ?? new JsonObject(), "from_id"), who, what, FeedStyle.Say, false, life, id,
            noticeName: S(n, "name"));
    }

    public (string Who, string What, double Life) NoticeLine(JsonObject n)
    {
        var name = S(n, "name");
        var subs = n["substitutions"] as JsonObject ?? new JsonObject();
        var pay = n["payload"] as JsonObject ?? new JsonObject();
        string Sub(string k) => Plain(S(subs, k));
        string Who() => NameOf(Id(pay, "from_id"), Sub("NAME") is { Length: > 0 } nm ? nm : S(pay, "from_name"));
        string Quote(string s) => string.IsNullOrWhiteSpace(s) ? "" : $" “{s.Trim()}”";
        (string Who, string What, double Life)? line = name switch
        {
            "TeleportOffered" => (Who(), "offers a teleport" + Quote(Sub("MESSAGE")), 120),
            "TeleportRequest" => (Who(), "asks for a teleport to you" + Quote(Sub("MESSAGE")), 120),
            "OfferFriendship" or "OfferFriendshipNoMessage" => (Who(), "offers friendship" + Quote(Sub("MESSAGE")), 120),
            "FriendshipAccepted" => (Who(), "accepted your friendship", 20),
            "FriendshipDeclined" => (Who(), "declined your friendship", 20),
            "UserGiveItem" => (Who(), $"gives you {S(pay, "item_name")}", 120),
            "ObjectGiveItem" or "OwnObjectGiveItem" => (Sub("OBJECTFROMNAME"), $"gives you {S(pay, "item_name")}", 120),
            "InventoryAccepted" => (Sub("NAME"), "took what you gave", 20),
            "InventoryDeclined" => (Sub("NAME"), "declined what you gave", 20),
            "FriendOnlineOffline" => (Sub("NAME"), Sub("STATUS") == "Online" ? "is online" : "is offline", 10),
            "ScriptDialog" or "ScriptDialogGroup" => (Plain(S(pay, "object_name")) is { Length: > 0 } o ? o : Plain(S(pay, "title")),
                Dialog(n), 120),
            "GroupNotice" => (Sub("SENDER"), $"{Sub("SUBJECT")}: {Sub("MESSAGE")}", 90),
            "JoinGroup" => ("", Plain(Sub("MESSAGE")), 120),
            "LoadWebPage" => (Plain(S(pay, "object_name")), $"offers a web page {S(pay, "url")}", 90),
            // money (Agents/Money.cs, the viewer's sentences): who paid, and how much
            "PaymentReceived" => (Plain(S(pay, "name")) is { Length: > 0 } payer ? payer : "someone",
                $"paid you L${I(pay, "amount")}" + (S(pay, "reason") is { Length: > 0 } why ? $" {why}" : ""), 30),
            "PaymentSent" or "PaymentFailure" => ("", Plain(S(n, "message")), 30),
            "DisplayNameUpdate" => (Sub("OLD_NAME"), $"is now known as {Sub("NEW_NAME")}", 20),
            "IMNotShownNotFriend" => (Sub("NAME"), "sent an IM (only friends and groups can IM you)", 20),
            // Firestorm's radar reports (Social/Radar.cs): who, and what range they came into or left
            "RadarReport" => (NameOf(Id(pay, "from_id"), Sub("NAME")), S(pay, "what"), 25),
            "InvOfferAutoAcceptUser" or "InvOfferAutoAcceptObject" => (Sub("NAME"), $"gave you {Sub("DESC")}", 30),
            "ScriptQuestionExperience" => (Sub("OBJECTNAME"),
                $"asks you to join {(S(pay, "experience_name") is { Length: > 0 } en ? $"the experience {en}" : "an experience")}: {Sub("QUESTIONS").Replace('\n', ',').Trim(',', ' ')}", 120),
            _ => null,
        };
        return line ?? ("", Plain(S(n, "message")), Life(FeedKind.Notice, S(n, "message")));
    }

    /// <summary>A script menu: its message (its buttons are keys under it on the feed).</summary>
    string Dialog(JsonObject n) => Plain(n["substitutions"] is JsonObject subs ? S(subs, "MESSAGE") : S(n, "message"));

    static readonly Regex AgentLink = new(@"secondlife:///app/agent/([0-9a-fA-F-]{36})/(\w+)", RegexOptions.Compiled);

    /// <summary>The viewer's markup out: &lt;nolink&gt; tags, and agent links as the name they stand for; a mention
    /// (the official viewer's chat mentions, 2025: an agent link ending /mention) as "@name".</summary>
    string Plain(string s)
    {
        s = s.Replace("<nolink>", "").Replace("</nolink>", "");
        return AgentLink.Replace(s, m =>
        {
            var name = UUID.TryParse(m.Groups[1].Value, out var id) && (Pseudonym(id) ?? _s.Names.DisplayName(id)) is { } nm ? nm : "someone";
            return m.Groups[2].Value.Equals("mention", StringComparison.OrdinalIgnoreCase) ? "@" + name : name;
        });
    }

    /// <summary>A line as the feed shows it: people as their names (SLurls stay, drawn as links by the painter).</summary>
    string Shown(string s) => s.Contains("secondlife:///app/agent/", StringComparison.OrdinalIgnoreCase) || s.Contains("<nolink>") ? Plain(s) : s;

    /// <summary>The line mentions the person (an agent link to them ending /mention).</summary>
    bool Mentions(string s) => s.Contains("/mention", StringComparison.OrdinalIgnoreCase)
        && AgentLink.Matches(s).Any(m => m.Groups[2].Value.Equals("mention", StringComparison.OrdinalIgnoreCase)
                                         && UUID.TryParse(m.Groups[1].Value, out var id) && id == _s.AgentId);
}
