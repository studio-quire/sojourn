using System.Text.RegularExpressions;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Access;

/// <summary>What arrives that can be read out, each its own switch.</summary>
public enum AnnounceKind { Nearby, Objects, Ims, Group, Notices, Replies, Music }

/// <summary>
/// Whose words a line read out carries, for the session log (<see cref="Announcer.Heard"/>): a log sent to the studio
/// leaves out anything that may be other people's words (Sojourn.Session/LogShare.cs). App: the app's own words, which
/// may name people; Me: the person's own; People: may quote someone else (chat, IMs, a person's offer or notice, a
/// profile, a listing's description), never sent; Objects: what objects said; Assistant: the assistant's replies.
/// </summary>
public enum LineFrom { App, Me, People, Objects, Assistant }

public static class LineSources
{
    /// <summary>The more guarded of two, for lines said together.</summary>
    public static LineFrom Join(LineFrom a, LineFrom b) => a == LineFrom.People || b == LineFrom.People ? LineFrom.People
        : a == b ? a : a == LineFrom.Objects || b == LineFrom.Objects ? LineFrom.Objects
        : a == LineFrom.Assistant || b == LineFrom.Assistant ? LineFrom.Assistant : LineFrom.App;

    /// <summary>Whose words a notice's line carries, by its notification name: objects' (script menus, their gifts,
    /// pages, places and questions), the app's or the grid's own (friends online, payments, radar, teleport trouble,
    /// joining a group found by search), or else a person's: an offer's or notice's message, a group's invitation, an
    /// estate's message box, and any name not listed here.</summary>
    public static LineFrom OfNotice(string? name) => name switch
    {
        "ScriptDialog" or "ScriptDialogGroup" or "ObjectGiveItem" or "OwnObjectGiveItem" or "LoadWebPage" or "ScriptQuestion"
            or "ScriptQuestionExperience" or "ObjectMessage" or "GotoURL" or "PlaceOffered" => LineFrom.Objects,
        null or "FriendOnlineOffline" or "FriendshipAccepted" or "FriendshipDeclined" or "InventoryAccepted" or "InventoryDeclined"
            or "UserGiveItem" or "InvOfferAutoAcceptUser" or "InvOfferAutoAcceptObject" or "PaymentReceived" or "PaymentSent"
            or "PaymentFailure" or "DisplayNameUpdate" or "IMNotShownNotFriend" or "RadarReport" or "WayOut" or "TeleportStalled"
            or "CouldNotTeleportReason" or "CollisionAlert" or "AntiSpamBlocked" or "DoNotDisturbHeld" or "LookAround"
            or "SetDisplayNameSuccess" or "VoiceCallAutoRejected" or "TeleportViaSLAPP" or "GroupLeaveConfirmMember"
            or "JoinGroupCanAfford" or "JoinGroupNoCost" or "JoinGroupCannotAfford" => LineFrom.App,
        _ => LineFrom.People,
    };
}

/// <summary>
/// The person's choices about their screen reader (kept with the app's settings): whether Sojourn speaks through it at
/// all (auto: while one runs, so a blind person never has to find a switch they can't see first; on; off) and what's
/// read out. All on by default.
/// </summary>
public sealed class AnnounceSettings
{
    public string Mode { get; set; } = "auto";
    public static readonly string[] Modes = { "auto", "on", "off" };
    public bool Nearby { get; set; } = true;
    public bool Objects { get; set; } = true;
    public bool Ims { get; set; } = true;
    public bool Group { get; set; } = true;
    public bool Notices { get; set; } = true;
    public bool Replies { get; set; } = true;
    public bool Music { get; set; } = true;

    public bool Wants(AnnounceKind k) => k switch
    {
        AnnounceKind.Nearby => Nearby, AnnounceKind.Objects => Objects, AnnounceKind.Ims => Ims, AnnounceKind.Group => Group,
        AnnounceKind.Notices => Notices, AnnounceKind.Replies => Replies, _ => Music,
    };

    public void Set(AnnounceKind k, bool on)
    {
        switch (k)
        {
            case AnnounceKind.Nearby: Nearby = on; break;
            case AnnounceKind.Objects: Objects = on; break;
            case AnnounceKind.Ims: Ims = on; break;
            case AnnounceKind.Group: Group = on; break;
            case AnnounceKind.Notices: Notices = on; break;
            case AnnounceKind.Replies: Replies = on; break;
            default: Music = on; break;
        }
    }
}

/// <summary>
/// Speaks what arrives through the person's own screen reader: NVDA by its controller client, anything else by UI
/// Automation notifications. Never a voice of ours (the person's reader has their voice, rate and braille). Shared by
/// every Sojourn app; the app says what arrived (<see cref="Say"/>), what answers the person's keys (<see cref="Answer"/>)
/// and what must be heard now (<see cref="Urgent"/>), and supplies its window for UIA.
/// - In the order things arrive: incoming lines go into the reader's own queue one after another, never cutting each
///   other off. Answers to the person's keys cut in (what screen readers do on a key); a confirmation cuts in and, on
///   NVDA 2024.1+, the interrupted line resumes after. The reader's own key (Ctrl) silences it, as everywhere.
/// - Held while a confirmation waits (<see cref="Hold"/>): nothing others or the assistant wrote is read beside it, as
///   the panel shows nothing beside it; it all follows once it's answered.
/// - The session log records each line as it's spoken, with whose words it carries (<see cref="Heard"/>, <see cref="LineFrom"/>).
/// - Incognito: what's read is what the screen shows the person; nothing is kept. Lines are never logged, the review
///   history keeps a spell's lines only while it lasts, and <see cref="Quiet"/> (the headset, where the PC's monitor
///   blanks too) keeps the PC's reader silent for the spell.
/// - Not on the lock screen or a secure desktop (NVDA's advice to apps).
/// </summary>
public sealed class Announcer : IDisposable
{
    readonly AnnounceSettings _st;
    readonly NvdaVoice? _nvda;
    readonly IReaderVoice? _uia;
    readonly PrivateMode? _private;
    readonly Action<string> _log;
    readonly object _lock = new();
    readonly List<Item> _queue = new();          // incoming, in order
    readonly Queue<Item> _now = new();           // answers and confirmations: ahead of the queue
    readonly List<Item> _history = new();
    readonly Dictionary<string, long> _recent = new();   // text -> when, against the same line repeated
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    volatile bool _stop;
    int _cursor = -1;   // the line last read back in the history (-1: not reading back)
    long _spoken;

    sealed record Item(string Text, bool Holdable, bool Interrupt, bool Urgent, int Spell, long At, LineFrom From, bool Again = false,
        AnnounceKind? Kind = null);

    /// <summary>An arrival as it went to the reader: its words (as spoken), kind, whose words, when it was handed over,
    /// and whether it was a line said again (<see cref="Say"/>'s again).</summary>
    public sealed record SpokenLine(string Text, AnnounceKind Kind, LineFrom From, long At, bool Again);

    /// <summary>Each arrival just handed to the reader (on the announcer's thread), held lines when they're read, never
    /// one that wasn't spoken: Sojourn Access's watch on lines cut off by typing.</summary>
    public Action<SpokenLine>? Spoke { get; set; }

    /// <summary>A line just spoken (on the announcer's thread): its words, whose words they are, how it came ("arrival",
    /// "answer", "urgent", "again" for one read back) and what spoke it. For the session log; never called for a line
    /// of an incognito spell.</summary>
    public Action<string, LineFrom, string, string>? Heard { get; set; }

    /// <summary>Keep the PC's reader silent now (the app's rule: the headset during incognito).</summary>
    public Func<bool> Quiet { get; set; } = () => false;
    /// <summary>A confirmation waits: incoming lines wait behind it.</summary>
    public Func<bool> Hold { get; set; } = () => false;
    /// <summary>Said once each time it becomes active: what this is and the key for the keys.</summary>
    public string? Greeting { get; set; }

    /// <summary>The reader found running (NVDA, JAWS, Narrator, ...), or null; checked every few seconds.</summary>
    public string? Reader { get; private set; }
    /// <summary>Speaking: on, or auto with a reader running.</summary>
    public bool Active { get; private set; }
    public event Action? ActiveChanged;
    public long Spoken => Interlocked.Read(ref _spoken);
    /// <summary>What speech goes through now.</summary>
    public string Through => Voice()?.Name ?? "nothing";
    public AnnounceSettings Settings => _st;

    /// <param name="nvdaFolders">Where to look for nvdaControllerClient.dll.</param>
    /// <param name="uia">The app window's UIA voice (UiaVoice.For), or null.</param>
    public Announcer(AnnounceSettings settings, IEnumerable<string> nvdaFolders, IReaderVoice? uia, PrivateMode? privacy, Action<string> log)
    {
        _st = settings;
        _log = log;
        _nvda = NvdaVoice.Load(nvdaFolders, log);
        _uia = uia;
        _private = privacy;
        if (privacy != null) privacy.Changed += (on, _, _) => { if (!on) ForgetSpells(); };
        log($"screen reader: NVDA's controller client {(_nvda != null ? "at " + _nvda.Path : "not found (python tools/screenreader.py setup)")}"
            + $"; UI Automation {(uia != null ? "ready" : "unavailable")}");
        _thread = new Thread(Run) { IsBackground = true, Name = "screen reader", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    // --- what the app says ------------------------------------------------------------------------------------------

    /// <summary>Something that arrived, of this kind: read in turn if the person wants that kind. keep: joins the review
    /// history (not for a line said again). from: whose words it carries; by default, as guarded as its kind allows
    /// (chat, IMs and notices: other people's). again: lines said again after being cut off (logged so, and never
    /// watched for being cut off themselves).</summary>
    public void Say(AnnounceKind kind, string text, bool keep = true, LineFrom? from = null, bool again = false)
    {
        if (!_st.Wants(kind)) return;
        // a greeter or a game saying the same line again and again is heard once a while
        if (kind is AnnounceKind.Objects && !again && Repeated(text)) return;
        Add(text, holdable: true, interrupt: false, urgent: false, keep, from ?? kind switch
        {
            AnnounceKind.Objects => LineFrom.Objects,
            AnnounceKind.Replies => LineFrom.Assistant,
            AnnounceKind.Music => LineFrom.App,
            _ => LineFrom.People,
        }, again, kind);
    }

    /// <summary>The answer to the person's own key or action: cuts in. keep: joins the review history (not for saying
    /// again what's already there, or the keys' help). With <see cref="WholeAnswers"/>, a long one is read whole, a few
    /// sentences at a time in turn (each its own line in the review history), instead of cut short. from: whose words
    /// it carries (the app's own unless said); whole: read whole even without WholeAnswers (a confirmation's list asked for).</summary>
    public void Answer(string text, bool keep = true, LineFrom from = LineFrom.App, bool whole = false)
    {
        if (!(WholeAnswers || whole) || text.Length <= MaxLine)
        {
            Add(text, holdable: false, interrupt: true, urgent: false, keep, from);
            return;
        }
        bool first = true;
        foreach (var piece in Pieces(text))
        {
            Add(piece, holdable: false, interrupt: first, urgent: false, keep, from);
            first = false;
        }
    }

    /// <summary>Answers past a line's length read whole in pieces rather than cut short (Sojourn Access, which has no
    /// panel to show them on: its keys' help, a long list). Off (the VR client): cut, as <see cref="Speakable"/> does.</summary>
    public bool WholeAnswers { get; set; }

    const int MaxLine = 700;

    /// <summary>Text in pieces of whole sentences, each under 600 characters; a sentence longer than that is split at its
    /// semicolons, then commas, then spaces.</summary>
    public static IEnumerable<string> Pieces(string text, int max = 600)
    {
        var piece = new System.Text.StringBuilder();
        foreach (var sentence in Regex.Split(text.Trim(), @"(?<=[.!?])\s+"))
            foreach (var part in Shorter(sentence, max))
            {
                if (piece.Length > 0 && piece.Length + 1 + part.Length > max) { yield return piece.ToString(); piece.Clear(); }
                if (piece.Length > 0) piece.Append(' ');
                piece.Append(part);
            }
        if (piece.Length > 0) yield return piece.ToString();
    }

    static IEnumerable<string> Shorter(string s, int max)
    {
        while (s.Length > max)
        {
            int cut = -1;
            foreach (var at in new[] { "; ", ", ", " " })
                if (s.LastIndexOf(at, max - 1, StringComparison.Ordinal) is var i and > 0) { cut = i + at.Length - 1; break; }
            if (cut <= 0) cut = max;
            yield return s[..cut].TrimEnd();
            s = s[cut..].TrimStart();
        }
        if (s.Length > 0) yield return s;
    }

    /// <summary>Something that waits for the person (a confirmation): cuts in, the interrupted line resuming after
    /// where the reader can.</summary>
    public void Urgent(string text, LineFrom from = LineFrom.App) => Add(text, holdable: false, interrupt: false, urgent: true, from: from);

    void Add(string text, bool holdable, bool interrupt, bool urgent, bool keep = true, LineFrom from = LineFrom.App,
        bool again = false, AnnounceKind? kind = null)
    {
        if (!Active || Quiet()) return;
        text = Speakable(text);
        if (text.Length == 0) return;
        var item = new Item(text, holdable, interrupt, urgent, _private is { On: true } p ? Math.Max(1, p.Spell) : 0, Environment.TickCount64,
            from, again, kind);
        lock (_lock)
        {
            if (holdable) _queue.Add(item); else _now.Enqueue(item);
            if (keep) _history.Add(item);
            if (_history.Count > 200) { _history.RemoveAt(0); if (_cursor > 0) _cursor--; if (_mark > 0) _mark--; }
        }
        _wake.Set();
    }

    bool Repeated(string text)
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            foreach (var k in _recent.Where(kv => now - kv.Value > 10_000).Select(kv => kv.Key).ToList()) _recent.Remove(k);
            if (_recent.ContainsKey(text)) return true;
            _recent[text] = now;
            return false;
        }
    }

    // --- reviewing what was said (the app's keys) ---------------------------------------------------------------------

    /// <summary>The line before the one last reviewed (or before the newest), read again.</summary>
    public void Older() => Review(-1);
    public void Newer() => Review(+1);

    /// <summary>The newest line again.</summary>
    public void Repeat()
    {
        string? text;
        Item? last;
        lock (_lock) { _cursor = -1; last = _history.LastOrDefault(); }
        Answer(last?.Text ?? "Nothing yet.", keep: false, last?.From ?? LineFrom.App);
    }

    /// <summary>The oldest line kept, read again (the old NVDA add-on's top of the history).</summary>
    public void Oldest()
    {
        string text;
        Item? of = null;
        lock (_lock)
        {
            if (_history.Count == 0) text = "Nothing yet.";
            else { _cursor = 0; of = _history[0]; text = of.Text; }
        }
        ReadAgain(text, of);
    }

    /// <summary>The lines that arrived (what <see cref="Say"/> kept; answers to the person's keys left out), newest
    /// first, at most <paramref name="count"/>.</summary>
    public List<string> RecentArrivals(int count)
    {
        lock (_lock)
            return Enumerable.Range(0, _history.Count).Select(i => _history[_history.Count - 1 - i])
                .Where(i => i.Holdable).Take(count).Select(i => i.Text).ToList();
    }

    /// <summary>The nth newest line that arrived (1 the newest; answers to keys left out) read again, review going on
    /// from it (NVDA's VIP Mud convention: Control+1 to 9 in Sojourn Access).</summary>
    public void ArrivedAgo(int n)
    {
        string text;
        Item? of = null;
        lock (_lock)
        {
            int at = -1, seen = 0;
            for (int i = _history.Count - 1; i >= 0 && at < 0; i--)
                if (_history[i].Holdable && ++seen == n) at = i;
            if (at < 0) text = seen == 0 ? "Nothing yet." : $"Only {seen} {(seen == 1 ? "message" : "messages")} so far.";
            else { _cursor = at; of = _history[at]; text = of.Text; }
        }
        ReadAgain(text, of);
    }

    int _mark = -1;

    /// <summary>The line under review (or the newest) marked as where a copy starts (the add-on's mark); what's said.</summary>
    public string Mark()
    {
        lock (_lock)
        {
            if (_history.Count == 0) return "Nothing to mark yet.";
            _mark = _cursor < 0 ? _history.Count - 1 : _cursor;
            return "Marked.";
        }
    }

    /// <summary>The lines from the mark to the one under review (or the newest), in the order they came; null with no
    /// mark (the add-on's copy).</summary>
    public string? FromMark()
    {
        lock (_lock)
        {
            if (_mark < 0 || _mark >= _history.Count) return null;
            int end = _cursor < 0 ? _history.Count - 1 : _cursor;
            int a = Math.Min(_mark, end), b = Math.Max(_mark, end);
            return string.Join(Environment.NewLine, _history.Skip(a).Take(b - a + 1).Select(i => i.Text));
        }
    }

    /// <summary>A line read back: of, the history's line it reads (its words' source and spell go with it).</summary>
    void ReadAgain(string text, Item? of)
    {
        if (!Active || Quiet()) return;
        lock (_lock) _now.Enqueue(new Item(text, false, true, false, of?.Spell ?? 0, Environment.TickCount64, of?.From ?? LineFrom.App, Again: true));
        _wake.Set();
    }

    void Review(int dir)
    {
        string text;
        Item? of = null;
        lock (_lock)
        {
            if (_history.Count == 0) text = "Nothing yet.";
            else if (_cursor < 0 && dir > 0) text = "No newer. " + (of = _history[^1]).Text;
            else
            {
                // the first step back reads the newest; then one at a time
                int next = _cursor < 0 ? _history.Count - 1 : _cursor + dir;
                if (next < 0) { text = "No older. " + (of = _history[0]).Text; _cursor = 0; }
                else if (next >= _history.Count) { text = "No newer. " + (of = _history[^1]).Text; _cursor = -1; }
                else { _cursor = next; text = (of = _history[next]).Text; }
            }
        }
        // read without joining the history itself
        ReadAgain(text, of);
    }

    /// <summary>Incognito ended: nothing of it stays, here either.</summary>
    void ForgetSpells()
    {
        lock (_lock)
        {
            _history.RemoveAll(i => i.Spell != 0);
            _queue.RemoveAll(i => i.Spell != 0);
            _recent.Clear();
            _cursor = -1;
            _mark = -1;
        }
    }

    // --- the worker --------------------------------------------------------------------------------------------------

    IReaderVoice? Voice() => Reader == "NVDA" && _nvda != null && !NvdaRefusing ? _nvda : _uia ?? (IReaderVoice?)_nvda;

    // NVDA's controller client can refuse (a tester's NVDA, 2026-10-30: everything that arrived went silent, nothing
    // logged): then speech goes through UI Automation's notifications instead, which NVDA reads for the window in
    // front, and the controller is tried again after a while
    long _nvdaRefusedUntil;
    int _nvdaRefusals;
    bool NvdaRefusing => Environment.TickCount64 < Interlocked.Read(ref _nvdaRefusedUntil);

    void NvdaRefused()
    {
        int n = ++_nvdaRefusals;
        Interlocked.Exchange(ref _nvdaRefusedUntil, Environment.TickCount64 + 60_000);
        if (n <= 3 || n % 50 == 0)
            _log($"screen reader: NVDA's controller refused speech (error {_nvda?.LastError}, {n} so far); speaking through UI Automation instead for a minute");
    }

    void Detect()
    {
        string? reader;
        try { reader = ScreenReaders.Running(_nvda); }
        catch (Exception) { reader = null; }
        bool active = _st.Mode switch { "on" => true, "off" => false, _ => reader != null };
        bool changed = active != Active || reader != Reader;
        Reader = reader;
        if (active != Active)
        {
            Active = active;
            _log(active ? $"screen reader: speaking through {(reader ?? "UI Automation")} ({Through})" : "screen reader: not speaking");
            if (!active) lock (_lock) { _queue.Clear(); _now.Clear(); }
            else if (Greeting != null) Answer(Greeting, keep: false);
        }
        if (changed) ActiveChanged?.Invoke();
    }

    /// <summary>The mode changed (menu, settings): look again now.</summary>
    public void Recheck() { _detectAt = 0; _wake.Set(); }
    long _detectAt;

    void Run()
    {
        while (!_stop)
        {
            _wake.WaitOne(Watching() ? 100 : 250);
            if (_stop) break;
            long now = Environment.TickCount64;
            if (now >= _detectAt) { _detectAt = now + 3000; Detect(); }
            while (Next() is { } item)
            {
                if (Quiet() || !ScreenReaders.DesktopOpen()) continue;
                var voice = Voice();
                try
                {
                    long sentAt = Environment.TickCount64;
                    bool said = voice?.Speak(item.Text, item.Interrupt, item.Urgent) == true;
                    if (!said && voice == _nvda && _uia != null)
                    {
                        NvdaRefused();
                        voice = _uia;
                        said = _uia.Speak(item.Text, item.Interrupt, item.Urgent);
                    }
                    if (said)
                    {
                        Interlocked.Increment(ref _spoken);
                        if (voice == _nvda) _nvda!.Braille(item.Text.Length > 300 ? item.Text[..300] : item.Text);
                        if (item.Spell == 0 && _private is not { On: true })
                            Heard?.Invoke(item.Text, item.From, item.Again ? "again" : item.Urgent ? "urgent" : item.Holdable ? "arrival" : "answer",
                                voice?.Name ?? "");
                        if (item.Holdable && item.Kind is { } kind)
                        {
                            Interlocked.Exchange(ref _arrivedAt, sentAt);
                            Spoke?.Invoke(new SpokenLine(item.Text, kind, item.From, sentAt, item.Again));
                        }
                    }
                }
                catch (Exception e) { _log($"screen reader: {voice?.Name} failed ({e.GetType().Name})"); }
            }
            if (Watching()) WatchSpeaking();
        }
    }

    // --- whether the reader is speaking (NVDA 2026.3+) -----------------------------------------------------------------

    // NVDA's controller can say whether it's speaking (nvdaController_isSpeaking, 2026.3): while arrivals are recent it's
    // asked every 100 ms and each quiet moment kept a while, so a line the reader fell quiet after, before any key, is
    // known to have been heard out rather than judged by its length. Older NVDAs answer 1717 and aren't asked again.
    long _arrivedAt = long.MinValue / 2;
    readonly Queue<long> _quietSeen = new();
    volatile bool _askSpeaking = true;

    bool Watching() => _askSpeaking && _nvda != null && Voice() == _nvda
        && Environment.TickCount64 - Interlocked.Read(ref _arrivedAt) < 15_000;

    void WatchSpeaking()
    {
        bool? speaking;
        try { speaking = _nvda!.Speaking(); }
        catch (Exception) { speaking = null; }
        if (speaking == null)
        {
            _askSpeaking = false;
            _log("screen reader: NVDA didn't say whether it's speaking (it can from 2026.3); lines cut off by typing are judged by their length");
            return;
        }
        if (speaking == true) return;
        long now = Environment.TickCount64;
        lock (_quietSeen)
        {
            _quietSeen.Enqueue(now);
            while (_quietSeen.Count > 0 && now - _quietSeen.Peek() > 30_000) _quietSeen.Dequeue();
        }
    }

    /// <summary>The reader was seen quiet at some moment after <paramref name="from"/> and before <paramref name="to"/>
    /// (NVDA 2026.3 and later; always false where that can't be known).</summary>
    public bool QuietBetween(long from, long to)
    {
        lock (_quietSeen) return _quietSeen.Any(t => t > from && t < to);
    }

    /// <summary>What to speak next: answers and confirmations first, then what arrived, unless a confirmation holds it.</summary>
    Item? Next()
    {
        lock (_lock)
        {
            if (_now.Count > 0) return _now.Dequeue();
            if (_queue.Count == 0 || Hold()) return null;
            var item = _queue[0];
            _queue.RemoveAt(0);
            return item;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(500);
        _uia?.Dispose();
    }

    // --- words fit for speech ------------------------------------------------------------------------------------------

    static readonly Regex Url = new(@"\b(?:https?://|www\.)([^\s/?#]+)[^\s]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SlUrl = new(@"(?:secondlife://|https?://maps\.secondlife\.com/secondlife/)(?:/app/[^\s]+|([^\s/]+)[^\s]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Links read as where they go ("a link to example.com", "a place, Bay City") rather than spelled out; the
    /// viewer's markup out; whitespace folded; very long text cut short, saying so (the panel or the reader has it whole).</summary>
    public static string Speakable(string text)
    {
        text = text.Replace("<nolink>", "").Replace("</nolink>", "");
        text = SlUrl.Replace(text, m => m.Groups[1].Success ? "a place, " + Uri.UnescapeDataString(m.Groups[1].Value) : "a link");
        text = Url.Replace(text, m => "a link to " + m.Groups[1].Value.ToLowerInvariant().Replace("www.", ""));
        text = Space.Replace(text, " ").Trim();
        if (text.Length > MaxLine)
        {
            int cut = text.LastIndexOf(' ', 680);
            text = text[..(cut > 400 ? cut : 680)] + " ... (cut short)";
        }
        return text;
    }
}
