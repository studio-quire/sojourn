using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Access;

/// <summary>
/// Chat logs on this PC: a plain text file a conversation, under the data folder's chat-logs/GRID/ACCOUNT, for rereading
/// what someone said yesterday. These are other people's words: nothing here sends them anywhere, a log sent to Studio
/// Quire only ever takes logs/ (LogShare), and the assistant's file tools can't read them (harness.py). Which conversation
/// a file is stays in its name, by the end of its session id, as display names change: "Kestrel - 1a2b3c4d.txt". Each run
/// of lines starts under a line with its date, so the lines themselves need only the time.
/// </summary>
public sealed class ChatLogs(string dir)
{
    public const string NearbyKey = "nearby";
    /// <summary>The assistant's requests and replies, descriptions among them (the blind tester kept forgetting hers, 2026-11-11).</summary>
    public const string AssistantKey = "assistant";
    const int TailBytes = 64 * 1024;

    public sealed record Log(string Path, string Title, DateTime Last);

    readonly Dictionary<string, string> _files = new();
    readonly HashSet<string> _headed = new();
    string _day = "";
    // lines go to disk from a thread of their own: a write once held the window 9 s on the blind tester's PC (its stack in
    // her log 2.1 s in, 2026-11-11, as her train came into its station and she couldn't stop it). Reading a tab's earlier
    // lines is queued there too, so it comes after every line queued before it and before any queued after. Everything
    // that touches the files holds _lock; the window's other readers wait for what's queued first
    readonly object _lock = new();
    readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
    int _pending;
    Thread? _writer;
    /// <summary>A line couldn't be written (from the writer's thread): said once by the window.</summary>
    public Action<Exception>? Failed { get; set; }
    static readonly Regex Ending = new(@" - [0-9a-f]{8}$", RegexOptions.Compiled);

    public string Dir => dir;

    static string Suffix(string key) => key.Replace("-", "") is { Length: >= 8 } s ? s[..8].ToLowerInvariant() : key;

    string? Existing(string key)
    {
        if (_files.TryGetValue(key, out var known) && File.Exists(known)) return known;
        if (!Directory.Exists(dir)) return null;
        var pattern = Fixed(key) ?? $"* - {Suffix(key)}.txt";
        var found = Directory.EnumerateFiles(dir, pattern).FirstOrDefault();
        if (found != null) _files[key] = found;
        return found;
    }

    /// <summary>A log with a name of its own rather than a conversation's.</summary>
    static string? Fixed(string key) => key switch { NearbyKey => "Nearby chat.txt", AssistantKey => "Assistant.txt", _ => null };

    static string SafeName(string title)
    {
        var chars = title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim().TrimEnd('.');
        if (name.Length > 60) name = name[..60].TrimEnd();
        return name.Length > 0 ? name : "Someone";
    }

    /// <summary>A line added, under a new line with the date when the day or the run of lines has changed; written on the
    /// writer's thread, so this never waits on the disk.</summary>
    public void Add(string key, string title, string line)
    {
        var at = DateTime.Now;
        Queue(() => { lock (_lock) Append(key, title, line, at); });   // (a failure goes to Failed)
    }

    /// <summary>The conversation's last lines, for a tab opened again, handed to `done` on the writer's thread once every
    /// line queued before this is written, so none queued after (the line that opened the tab); none without a log. The
    /// window never waits on the disk for them: a tab is opened by an arriving IM, on its thread.</summary>
    public void TailThen(string key, int lines, Action<List<string>> done) => Then(() => TailLocked(key, lines), new List<string>(), done);

    /// <summary>Every log, the most recent first, named as the conversation was when it began, to `done` on the writer's
    /// thread after what's queued (Read-out > Chat logs: the window doesn't wait on the disk for the list either).</summary>
    public void AllThen(Action<List<Log>> done) => Then(AllLocked, new List<Log>(), done);

    /// <summary>A conversation's log, or null, as AllThen.</summary>
    public void OfThen(string key, Action<Log?> done) =>
        Then(() => Existing(key) is { } path ? AllLocked().FirstOrDefault(l => l.Path == path) : null, null, done);

    /// <summary>A log's words as a window shows them (Read), or why they couldn't be read, as AllThen.</summary>
    public void ReadThen(string path, Action<(string? Text, string? Error)> done) => Queue(() =>
    {
        (string? Text, string? Error) got;
        try { lock (_lock) got = (Read(path), null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { got = (null, e.Message); }
        Hand(done, got);
    });

    void Then<T>(Func<T> read, T failed, Action<T> done) => Queue(() =>
    {
        T got;
        try { lock (_lock) got = read(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { got = failed; }
        Hand(done, got);
    });

    static void Hand<T>(Action<T> done, T got)
    {
        try { done(got); }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { }   // (the window closing)
    }

    void Queue(Action work)
    {
        Interlocked.Increment(ref _pending);
        _queue.Add(work);
        if (_writer == null)
            lock (_queue)
                if (_writer == null) (_writer = new Thread(Write) { IsBackground = true, Name = "chat logs", Priority = ThreadPriority.BelowNormal }).Start();
    }

    void Write()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            // (anything thrown on this thread would end the app without a word: said, as a write's failure is)
            try { work(); }
            catch (Exception e)
            {
                try { Failed?.Invoke(e); }
                catch (Exception) { }
            }
            finally { Interlocked.Decrement(ref _pending); }
        }
    }

    /// <summary>What's queued written first (at most 3 s: a disk that slow is no reason to hang a reader for ever); false if
    /// some of it still isn't.</summary>
    bool Settle(int ms = 3000)
    {
        var until = Environment.TickCount64 + ms;
        while (Volatile.Read(ref _pending) > 0 && Environment.TickCount64 < until) Thread.Sleep(10);
        return Volatile.Read(ref _pending) == 0;
    }

    void Append(string key, string title, string line, DateTime now)
    {
        var path = Existing(key);
        if (path == null)
        {
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, Fixed(key) ?? $"{SafeName(title)} - {Suffix(key)}.txt");
            _files[key] = path;
        }
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (day != _day) { _day = day; _headed.Clear(); }
        var text = new StringBuilder();
        if (_headed.Add(key)) text.Append("--- ").Append(now.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture)).Append(" ---\r\n");
        text.Append('[').Append(now.ToString("HH:mm", CultureInfo.InvariantCulture)).Append("] ").Append(line.ReplaceLineEndings("\r\n")).Append("\r\n");
        File.AppendAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    List<string> TailLocked(string key, int lines)
    {
        if (Existing(key) is not { } path) return new();
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            bool cut = f.Length > TailBytes;
            if (cut) f.Seek(-TailBytes, SeekOrigin.End);
            var all = new StreamReader(f, Encoding.UTF8).ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
            if (cut && all.Count > 0) all.RemoveAt(0);   // (the seek landed inside it)
            return all.Count > lines ? all.GetRange(all.Count - lines, lines) : all;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }

    List<Log> AllLocked()
    {
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.txt")
            .Select(p => new Log(p, Ending.Replace(Path.GetFileNameWithoutExtension(p), ""), File.GetLastWriteTime(p)))
            .OrderByDescending(l => l.Last).ToList();
    }

    /// <summary>The log's words as a window shows them: at most the last two million characters, from a line's start.</summary>
    public static string Read(string path)
    {
        const int Most = 2_000_000;
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var text = new StreamReader(f, Encoding.UTF8).ReadToEnd();
        if (text.Length <= Most) return text;
        var cut = text.IndexOf('\n', text.Length - Most) + 1;
        return "(Only the last part: the whole log is in the file.)\r\n" + text[cut..];
    }

    /// <summary>A log deleted; false if it isn't one of these.</summary>
    public bool Delete(string file)
    {
        Settle();
        lock (_lock) return DeleteLocked(file);
    }

    bool DeleteLocked(string file)
    {
        var path = Path.GetFullPath(Path.Combine(dir, Path.GetFileName(file)));
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase)) return false;
        File.Delete(path);
        foreach (var key in _files.Where(kv => kv.Value == path).Select(kv => kv.Key).ToList()) { _files.Remove(key); _headed.Remove(key); }
        return true;
    }
}

/// <summary>Deleting a chat log, as an op so her confirmation holds as for any delete. Nothing here reads one: what's in
/// them stays out of the assistant's reach.</summary>
public sealed class ChatLogApi : LeapApi
{
    public override string Name => "SojournChatLogs";
    public override string Desc => "Sojourn Access's chat logs on this PC: only deleting one, after the person's confirmation";

    public ChatLogApi(AgentSession s, ChatLogs logs) : base(s)
    {
        Op("delete", "{file, title}: a log deleted for good", a =>
        {
            var file = a.Str("file") ?? throw new LeapError("need file");
            if (!logs.Delete(file)) throw new LeapError("no such log");
            return new JsonObject();
        });
    }
}
