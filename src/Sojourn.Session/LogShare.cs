using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sojourn.Session;

/// <summary>
/// A session log made fit to send to the studio (Sojourn Access's Help > Send a log, //sendlog; docs/access-app.md >
/// Sending a log). Second Life's terms forbid collecting data about other users without their consent (ToS 6.2(i),
/// docs/tpv-policy.md > Data and privacy), so what's sent never holds what other people said or wrote, and their names
/// and keys become stand-ins, "person 1", "person 2" in the order they come up, the person's own "me".
///
/// It reads the records the log keeps of whose words its lines carry (SessionLog, LogWords.cs) rather than guessing
/// from text, and leaves out what it can't place:
/// - <c>~words</c> (what joined the feed): other people's lines and their offers' and notices' words left out, only
///   who and what kind kept; the person's own lines, objects' lines and the app's or grid's notices kept.
/// - <c>~heard</c> (what the screen reader was given): lines marked as other people's words left out; the rest kept.
/// - <c>~name</c>: only feeds the stand-ins. Any other record, or one that doesn't parse: left out.
/// - Plain lines (the app's log, the assistant's, errors): kept.
/// Then, in everything kept: every name and key of a person the log knows replaced; an object's line that relays
/// someone ("person 2: ...") and any line repeating a run of someone's left-out words (five words in a row, or a whole
/// short line) left out; keys, tokens, capability URLs, the Windows account's folder and public IP addresses masked.
/// A log from before these records (no <c>~log</c> line of format 2) can't be filtered, so it isn't offered.
/// Depends only on .NET itself (tools/logcheck links this file). Written by Claude Opus 5.5, 2026-10-31.
/// </summary>
public static class LogShare
{
    /// <summary>How the log's records are written: readable text (not \u-escaped), one line each.</summary>
    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public const string LeftOut = "(words left out)";

    public sealed record Result(string Text, int Lines, int LeftOutLines, int People);

    /// <summary>A session's log as it can be offered: its file, when it started, whether it can be filtered.</summary>
    public sealed record Session(string Path, DateTime Started, bool Sendable);

    /// <summary>The logs in a base dir's logs/, newest first.</summary>
    public static List<Session> Sessions(string logsDir)
    {
        var list = new List<Session>();
        try
        {
            foreach (var f in new DirectoryInfo(logsDir).GetFiles("session-*.log").OrderByDescending(f => f.Name))
            {
                var m = Regex.Match(f.Name, @"^session-(\d{8})-(\d{6})\.log$");
                var at = m.Success && DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : f.CreationTime;
                list.Add(new Session(f.FullName, at, Sendable(f.FullName)));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>The file, read while the session may still be writing it.</summary>
    public static string Read(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    static readonly Regex Header = new(@"^\d\d:\d\d:\d\d ~log (\{.*\})\s*$", RegexOptions.Compiled);

    public static bool Sendable(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(s, Encoding.UTF8);
            return Format(r.ReadLine()) >= 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static int Format(string? first)
    {
        if (first is null || Header.Match(first) is not { Success: true } m) return 0;
        try { return JsonNode.Parse(m.Groups[1].Value)?["v"]?.GetValue<int>() ?? 0; }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { return 0; }
    }

    // ---- the filter ---------------------------------------------------------------------------------------------------

    static readonly Regex Stamp = new(@"^\d\d:\d\d:\d\d ", RegexOptions.Compiled);
    static readonly Regex RecordLine = new(@"^(\d\d:\d\d:\d\d) ~(\w+) (\{.*\})\s*$", RegexOptions.Compiled);
    // anything shaped like a record is one, or left out
    static readonly Regex RecordLike = new(@"^\d\d:\d\d:\d\d ~", RegexOptions.Compiled);
    // the console's chat as older versions wrote it into the file ("17:04 Name: words"): never part of a sendable log
    static readonly Regex OldChat = new(@"^\d\d:\d\d [^\d]", RegexOptions.Compiled);
    static readonly Regex StandIn = new(@"^person \d+$", RegexOptions.Compiled);

    sealed class Person
    {
        public int N;   // 0 until it first comes up in what's sent
        public bool Me;
        public readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The log's text filtered for sending; null if it's from before the records (it can't be filtered).
    /// started: when the session began, for the first line; app: whose log it is, for the same.</summary>
    public static Result? Filter(string log, DateTime? started = null, string app = "Sojourn Access")
    {
        var lines = log.Replace("\r", "").Split('\n');
        if (Format(lines.FirstOrDefault()) < 2) return null;

        // entries: a line with a time, and the lines after it without one (a message or a stack trace over lines)
        var entries = new List<List<string>>();
        foreach (var line in lines.Skip(1))
        {
            if (Stamp.IsMatch(line)) entries.Add(new List<string> { line });
            else if (entries.Count > 0 && line.Length > 0) entries[^1].Add(line);
        }

        // first, who's who: everyone the log names, and everything they said (to catch it repeated)
        var people = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);   // by id
        var theirWords = new List<string>();
        Person Of(string id) => people.TryGetValue(id, out var p) ? p : people[id] = new Person();
        foreach (var e in entries)
        {
            if (Parse(e[0]) is not { } r) continue;
            var o = r.Body;
            string? id = Str(o, "id");
            switch (r.Tag)
            {
                case "name" when id is { Length: > 0 }:
                    var p = Of(id);
                    if (o["me"]?.GetValueKind() == JsonValueKind.True) p.Me = true;
                    AddNames(p, Str(o, "legacy"), Str(o, "display"), Str(o, "pseudonym"));
                    break;
                case "words":
                    var from = Str(o, "from");
                    if (from == "people" && Str(o, "text") is { } said) theirWords.Add(said);
                    // a person's line or notice (the app's notices name the friend online, the payer...): their id and name
                    if (id is { Length: > 0 } && from is "people" or "me" or "app")
                    {
                        var q = Of(id);
                        if (from == "me") q.Me = true;
                        AddNames(q, Str(o, "name"), null, null);
                    }
                    break;
                case "heard":
                    if (Str(o, "from") == "people" && Str(o, "text") is { } heard) theirWords.Add(heard);
                    break;
            }
        }
        var names = new Names(people);
        var echo = new Echo(theirWords);

        var outText = new StringBuilder();
        var head = started is { } s ? $"started {s:yyyy-MM-dd HH:mm:ss}" : "";
        outText.Append($"{app} session log{(head.Length > 0 ? ", " + head : "")}, as sent: other people's words left out, ")
            .Append("their names and keys as person 1, person 2 and so on, your own as me.\n");
        int kept = 0, dropped = 0;
        void Put(string line) { outText.Append(line).Append('\n'); kept++; }
        void Drop(string line) { outText.Append(line).Append('\n'); dropped++; }

        foreach (var e in entries)
        {
            var rest = e.Skip(1);
            if (RecordLike.IsMatch(e[0]))
            {
                if (Parse(e[0]) is { } r) Record(r);
                else Drop($"{e[0][..8]} (a record that couldn't be read, left out)");
                // a record is one line: what follows without a time came from elsewhere (an error written bare)
                foreach (var l in rest) Plain(l);
                continue;
            }
            // a plain line of the app's log, and its continuation
            if (e.Skip(1).Any(OldChat.IsMatch)) { Drop($"{e[0][..8]} (lines of an older kind, left out)"); continue; }
            var clean = Mask(names.Replace(string.Join("\n", e)));
            if (echo.Repeats(clean)) Drop($"{e[0][..8]} (a line repeating someone's words, left out)");
            else Put(clean);
        }

        void Plain(string l)
        {
            if (OldChat.IsMatch(l)) { Drop("         (a line of an older kind, left out)"); return; }
            var c = Mask(names.Replace(l));
            if (echo.Repeats(c)) Drop("         (a line repeating someone's words, left out)");
            else Put(c);
        }

        // a field that is always a person's name: their stand-in, or "someone" if the log never said whose it is
        string Someone(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "someone";
            var r = names.Replace(name.Trim());
            return r == "me" || StandIn.IsMatch(r) ? r : "someone";
        }

        void Record((string Time, string Tag, JsonObject Body) r)
        {
            var o = r.Body;
            var t = r.Time;
            switch (r.Tag)
            {
                case "log" or "name":
                    return;
                case "words":
                {
                    var kind = Str(o, "kind") ?? "";
                    var from = Str(o, "from") ?? "";
                    var style = Str(o, "style");
                    var alert = o["alert"]?.GetValueKind() == JsonValueKind.True ? " (an alert)" : "";
                    // an object's own name stays; a person's is their stand-in, or "someone"
                    var who = from == "objects" ? Mask(names.Replace(Str(o, "name") ?? "something"))
                        : Str(o, "id") is { Length: > 0 } id && people.ContainsKey(id) ? names.Replace(id) : Someone(Str(o, "name"));
                    var where = kind switch
                    {
                        "im" => from == "me" ? $"IM to {Someone(Str(o, "session"))}" : "IM",
                        "group" => $"in the group chat {Mask(names.Replace(Str(o, "session") ?? ""))}".TrimEnd(),
                        "notice" => $"notice{(Str(o, "notice") is { Length: > 0 } nn ? $" ({nn})" : "")}",
                        _ => "nearby",
                    };
                    var how = style is "whisper" or "shout" or "emote" ? $" ({style})" : "";
                    var words = Str(o, "text") ?? "";
                    switch (from)
                    {
                        case "me":
                            Put($"{t} me, {where}{how}: {Mask(names.Replace(words))}");
                            return;
                        case "objects":
                        {
                            var said = Mask(names.Replace(words));
                            var label = kind == "notice" ? $"{where} from {who}" : $"object {who}, {where}{how}";
                            if (Relays.IsMatch(said) || echo.Repeats(said)) Drop($"{t} {label}{alert}: (words left out: they repeat someone's)");
                            else Put($"{t} {label}{alert}: {said}");
                            return;
                        }
                        case "app":
                        {
                            // the notice's name is a person's (a friend online, a payer) or none (the grid's own)
                            var named = Str(o, "name") is { Length: > 0 } ? (Str(o, "id") is { Length: > 0 } nid && people.ContainsKey(nid) ? names.Replace(nid) : Someone(Str(o, "name"))) : "";
                            var said = Mask(names.Replace(Join(named, words)));
                            if (echo.Repeats(said)) Drop($"{t} {where}: (words left out: they repeat someone's)");
                            else Put($"{t} {where}: {said}");
                            return;
                        }
                        default:   // people's, and anything not placed
                            Drop(kind == "notice" ? $"{t} {where} from {who}: {LeftOut}" : $"{t} {who}, {where}{how}{alert}: {LeftOut}");
                            return;
                    }
                }
                case "heard":
                {
                    var from = Str(o, "from") ?? "";
                    var label = $"{t} read out ({Str(o, "how") ?? "?"}{(Str(o, "via") is { Length: > 0 } via ? ", " + via : "")})";
                    var said = Mask(names.Replace(Str(o, "text") ?? ""));
                    if (from is "app" or "me" or "assistant" or "objects")
                    {
                        if (from != "me" && (echo.Repeats(said) || from == "objects" && Relays.IsMatch(said)))
                            Drop($"{label}: (words left out: they repeat someone's)");
                        else Put($"{label}{(from is "me" or "app" ? "" : $", {from}")}: {said}");
                    }
                    else Drop($"{label}: (someone else's words, left out)");
                    return;
                }
                default:
                    Drop($"{t} (a record of a kind not known here, left out)");
                    return;
            }
        }

        return new Result(outText.ToString(), kept, dropped, names.Used);
    }

    // a verb phrase follows the name ("person 2 is online"); a message of its own gets a colon (AccessWiring's Join)
    static string Join(string who, string what) =>
        who.Length == 0 ? what : what.Length > 0 && char.IsLower(what[0]) ? $"{who} {what}" : $"{who}: {what}";

    /// <summary>An object's line that passes on what someone said ("person 2: hi", "person 2 says ...").</summary>
    static readonly Regex Relays = new(@"\bperson \d+\s*(:|says\b|said\b|whispers\b|shouts\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static (string Time, string Tag, JsonObject Body)? Parse(string line)
    {
        if (RecordLine.Match(line) is not { Success: true } m) return null;
        try { return JsonNode.Parse(m.Groups[3].Value) is JsonObject o ? (m.Groups[1].Value, m.Groups[2].Value, o) : null; }
        catch (JsonException) { return null; }
    }

    static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>A person's names as they may appear: the legacy name, its username forms (first.last, first) and its
    /// first name alone (people are called by it); a display name, and its first word when that's 4 letters or more;
    /// a pseudonym. More is replaced than strictly needed, never less.</summary>
    static void AddNames(Person p, string? legacy, string? display, string? pseudonym)
    {
        foreach (var n in new[] { display, pseudonym }) if (!string.IsNullOrWhiteSpace(n)) p.Names.Add(n.Trim());
        if (display?.Trim().Split(' ', 2) is [{ Length: >= 4 } first, _]) Short(p, first);
        if (string.IsNullOrWhiteSpace(legacy)) return;
        legacy = legacy.Trim();
        p.Names.Add(legacy);
        // "Display (user)", as the viewer writes a complete name
        if (Regex.Match(legacy, @"^(.+?) \((.+)\)$") is { Success: true } c) { p.Names.Add(c.Groups[1].Value); AddNames(p, c.Groups[2].Value, null, null); return; }
        var parts = legacy.Split(new[] { ' ', '.' }, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            p.Names.Add($"{parts[0]}.{parts[1]}");
            p.Names.Add($"{parts[0]} {parts[1]}");
            if (parts[1].Equals("Resident", StringComparison.OrdinalIgnoreCase)) p.Names.Add(parts[0]);   // the username itself
            else Short(p, parts[0]);
        }
        else p.Names.Add(legacy + " Resident");
    }

    /// <summary>A part of a name standing alone (a first name, a display name's first word), unless it's an everyday
    /// word or one of the log's own (someone called "Voice Smith" would otherwise take every "voice:" line with them):
    /// the whole name is always replaced; alone, such a word says little about who. (Any other common word, whole name
    /// or part, is replaced only where it's capitalised: <see cref="Names"/>.)</summary>
    static void Short(Person p, string word)
    {
        if (!Everyday.Contains(word)) p.Names.Add(word);
    }

    static readonly HashSet<string> Everyday = new(StringComparer.OrdinalIgnoreCase)
    {
        // the log's own words: its categories, the apps', their parts
        "sojourn", "access", "info", "warn", "fail", "crit", "dbug", "trce", "render", "agent", "pilot", "registry", "collision",
        "assistant", "voice", "audio", "music", "media", "screen", "reader", "provider", "relay", "studio", "quire", "local",
        "grid", "region", "parcel", "object", "objects", "notice", "notices", "nearby", "person", "people", "someone", "teleport",
        "chat", "group", "friend", "friends", "home", "world", "walk", "mode", "sound", "sounds", "view", "state", "cache", "logs",
        "session", "login", "second", "life", "linden", "resident", "inventory", "outfit", "search", "places", "settings",
        // everyday English
        "the", "and", "for", "you", "your", "yours", "with", "this", "that", "from", "have", "here", "there", "what", "when",
        "where", "which", "who", "why", "how", "not", "but", "all", "any", "are", "was", "were", "will", "can", "one", "two",
        "out", "off", "into", "over", "under", "about", "after", "before", "just", "like", "love", "good", "great", "best",
        "new", "old", "big", "little", "small", "sweet", "dear", "lady", "lord", "miss", "mister", "sir", "king", "queen",
        "prince", "princess", "baby", "angel", "star", "moon", "sun", "sky", "rain", "snow", "fire", "ice", "water", "wind",
        "rose", "lily", "black", "white", "red", "blue", "green", "gold", "silver", "dark", "light", "night", "day", "summer",
        "winter", "spring", "autumn", "happy", "lucky", "wild", "free", "true", "real", "only", "very", "more", "most", "some",
        "time", "now", "then", "yes", "okay", "hello", "help", "please", "thanks", "thank", "sorry", "back", "down", "left",
        "right", "ahead", "near", "first", "last", "next", "once", "again", "still", "well", "much", "many", "each", "every",
    };

    /// <summary>Every person's names and keys, replaced by their stand-in wherever they stand as words: in any case,
    /// except a name of one word that's a common English word (<see cref="Common"/>), replaced only where it's
    /// capitalised as a name is ("Grey said", not "a grey top"; a name of more words always).</summary>
    sealed class Names
    {
        readonly Dictionary<string, Person> _by = new(StringComparer.OrdinalIgnoreCase);
        readonly Regex? _re, _capitalised;
        int _next;
        public int Used => _next;

        public Names(Dictionary<string, Person> people)
        {
            foreach (var (id, p) in people)
            {
                _by.TryAdd(id, p);
                // the same name for two people: the person's own wins, then whoever the log met first
                foreach (var n in p.Names) if (!_by.ContainsKey(n) || p.Me) _by[n] = p;
            }
            var names = _by.Keys.Where(k => k.Length > 0).OrderByDescending(k => k.Length).ToList();
            var words = names.Where(IsCommon).ToList();
            var rest = names.Except(words, StringComparer.OrdinalIgnoreCase).ToList();
            if (rest.Count > 0)
                _re = new Regex(@"(?<![\p{L}\p{N}_])(?:" + string.Join("|", rest.Select(Regex.Escape)) + @")(?![\p{L}\p{N}_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            // its first letter a capital, the rest in any case
            if (words.Count > 0)
                _capitalised = new Regex(@"(?<![\p{L}\p{N}_])(?:"
                    + string.Join("|", words.Select(w => Regex.Escape(char.ToUpperInvariant(w[0]).ToString()) + "(?i:" + Regex.Escape(w[1..]) + ")"))
                    + @")(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant);
        }

        static bool IsCommon(string name) => name.Length > 1 && name.All(char.IsLetter) && Common.Contains(name.ToLowerInvariant());

        public string Replace(string text)
        {
            if (text.Length == 0) return text;
            if (_re != null) text = _re.Replace(text, StandInFor);
            if (_capitalised != null) text = _capitalised.Replace(text, StandInFor);
            return text;
        }

        string StandInFor(Match m)
        {
            if (!_by.TryGetValue(m.Value, out var p)) return m.Value;
            if (p.Me) return "me";
            if (p.N == 0) p.N = ++_next;
            return $"person {p.N}";
        }
    }

    /// <summary>Common English words (common-words.txt, SCOWL's commonest, embedded by each project that links this
    /// file); none if it's missing, so every name is then replaced in any case.</summary>
    static readonly HashSet<string> Common = LoadCommon();

    static HashSet<string> LoadCommon()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var s = typeof(LogShare).Assembly.GetManifestResourceStream("common-words.txt");
        if (s is null) return set;
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#') set.Add(line.Trim());
        return set;
    }

    /// <summary>Other people's words, to catch a line that repeats them (read back, quoted by the assistant, relayed by
    /// an object): five words in a row from a longer line, or a whole short one of at least 12 characters.</summary>
    sealed class Echo
    {
        const int Run = 5;
        readonly HashSet<string> _runs = new();
        readonly HashSet<string>[] _short = { new(), new(), new(), new(), new() };   // by word count, 2-4

        public Echo(IEnumerable<string> texts)
        {
            foreach (var t in texts)
            {
                var w = Words(t);
                if (w.Length >= Run) for (int i = 0; i + Run <= w.Length; i++) _runs.Add(string.Join(' ', w, i, Run));
                else if (w.Length >= 2 && string.Join(' ', w) is { Length: >= 12 } phrase) _short[w.Length].Add(phrase);
            }
        }

        public bool Repeats(string line)
        {
            var w = Words(line);
            for (int i = 0; i + Run <= w.Length; i++) if (_runs.Contains(string.Join(' ', w, i, Run))) return true;
            for (int k = 2; k < Run; k++)
                if (_short[k].Count > 0)
                    for (int i = 0; i + k <= w.Length; i++) if (_short[k].Contains(string.Join(' ', w, i, k))) return true;
            return false;
        }

        static readonly Regex NotWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

        static string[] Words(string s) => NotWord.Replace(s.ToLowerInvariant(), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    // ---- masks ----------------------------------------------------------------------------------------------------------

    static readonly (Regex Re, string With)[] Masks =
    {
        (new(@"https?://[^\s""'<>]+/cap/[^\s""'<>]+", RegexOptions.IgnoreCase), "(a capability URL)"),
        (new(@"<bridgeAuth>[^<]*</bridgeAuth>", RegexOptions.IgnoreCase), "<bridgeAuth>(left out)</bridgeAuth>"),
        (new(@"\b(?:sk-ant-|sk-or-)[A-Za-z0-9_\-]{8,}|\bsqr-[A-Za-z0-9_\-]{20,}"), "(a key)"),
        // the Windows account's folder name (often the person's own name)
        (new(@"(?i)([A-Z]:[\\/]+Users[\\/]+)[^\\/\s""':]+"), "${1}(you)"),
        // IPv6 addresses (four groups or more, or shortened with ::), any of which could be the person's own
        (new(@"(?i)(?<![\w:])(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}(?![\w:])|(?<![\w:])(?:[0-9a-f]{1,4}:){1,6}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,5})?(?![\w:])"),
            "(an address)"),
    };

    static readonly Regex Ipv4 = new(@"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![\d.])", RegexOptions.Compiled);

    static string Mask(string s)
    {
        foreach (var (re, with) in Masks) s = re.Replace(s, with);
        // public addresses masked (one could be the person's own); loopback and private ones say nothing about anyone
        return Ipv4.Replace(s, m =>
        {
            var o = Enumerable.Range(1, 4).Select(i => int.Parse(m.Groups[i].Value)).ToArray();
            if (o.Any(x => x > 255) || o[0] == 0) return m.Value;   // a version number, not an address
            bool local = o[0] is 10 or 127 || o[0] == 192 && o[1] == 168 || o[0] == 172 && o[1] is >= 16 and <= 31
                || o[0] == 169 && o[1] == 254 || o[0] == 100 && o[1] is >= 64 and <= 127;
            return local ? m.Value : "(an address)";
        });
    }
}
