using System.Text;
using System.Text.Json.Nodes;

namespace Sojourn.Core.Events;

/// <summary>
/// One agent's event log, the same contract as slbridge.py: monotonic seq (kept across restarts by reading the
/// file's last line), an in-memory window of recent events served by /events, and every event appended to
/// events.jsonl (tools like listen.py tail that file directly).
/// Incognito (Agents/PrivateMode.cs) seals what's logged while it's on: not written to the file, and /events
/// (<see cref="GetVisibleJsonAsync"/>) serves only the person's own kinds of the spell under way (all of it while the
/// spell sees), and nothing of it after; the client's own waits (GetAsync, WaitForAsync) still see everything.
/// </summary>
public sealed class EventLog : IDisposable
{
    const int MaxInMemory = 5000;
    const int TrimBy = 1000;

    readonly object _lock = new();
    readonly List<(long Seq, string Json)> _events = new();  // serialized: a JsonObject per event costs ~5x the memory
    readonly List<(long Seq, string Kind, int Spell)> _meta = new();   // beside _events, for /events' private filter
    readonly string? _path;
    readonly StreamWriter? _writer;
    TaskCompletionSource _changed = NewTcs();
    long _seq;

    public EventLog(string? path)
    {
        _path = path;
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _seq = ReadLastSeq(path);
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public long Seq { get { lock (_lock) return _seq; } }

    /// <summary>The spell of private mode under way (0: none): events logged in one are sealed with it.</summary>
    public Func<int> Spell { get; set; } = () => 0;

    /// <summary>Whether the spell under way lets callers see ("let it see"): then /events serves all of it while it lasts.</summary>
    public Func<bool> Sees { get; set; } = () => false;

    /// <summary>Event kinds /events serves while blind (the person's own words and presses).</summary>
    public IReadOnlySet<string> PrivateKinds { get; set; } = new HashSet<string>();
    public string? FilePath => _path;

    static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static long ReadLastSeq(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = f.Length;
            var take = (int)Math.Min(len, 65536);
            f.Seek(len - take, SeekOrigin.Begin);
            var buf = new byte[take];
            f.ReadExactly(buf);
            var lines = Encoding.UTF8.GetString(buf).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                try
                {
                    if (JsonNode.Parse(lines[i]) is JsonObject o && o["seq"] is JsonValue v && v.TryGetValue<long>(out var s))
                        return s;
                }
                catch { /* a torn last line: try the one before */ }
            }
        }
        catch { }
        return 0;
    }

    public long Log(string kind, JsonNode? data)
    {
        TaskCompletionSource toSignal;
        long seq;
        int spell = Spell();
        lock (_lock)
        {
            seq = ++_seq;
            var ev = new JsonObject
            {
                ["seq"] = seq,
                ["t"] = Json.UnixNow(),
                ["kind"] = kind,
                ["data"] = data ?? new JsonObject(),
            };
            var line = ev.ToJsonString(Json.Compact);
            _events.Add((seq, line));
            _meta.Add((seq, kind, spell));
            if (_events.Count > MaxInMemory) { _events.RemoveRange(0, TrimBy); _meta.RemoveRange(0, TrimBy); }
            if (spell == 0)
            {
                try { _writer?.WriteLine(line); }
                catch { /* disk trouble must not stop the agent */ }
            }
            toSignal = _changed;
            _changed = NewTcs();
        }
        toSignal.TrySetResult();
        return seq;
    }

    /// <summary>Events after <paramref name="since"/>; waits up to <paramref name="wait"/> for one if there are none yet.</summary>
    public async Task<List<string>> GetRawAsync(long since, TimeSpan wait, CancellationToken ct = default)
    {
        Task waitTask;
        lock (_lock)
        {
            if (_seq > since || wait <= TimeSpan.Zero) return Snapshot(since);
            waitTask = _changed.Task;
        }
        try { await waitTask.WaitAsync(wait, ct); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        lock (_lock) return Snapshot(since);
    }

    /// <summary>The /events body: a JSON array of the events after <paramref name="since"/>.</summary>
    public async Task<string> GetJsonAsync(long since, TimeSpan wait, CancellationToken ct = default)
    {
        var lines = await GetRawAsync(since, wait, ct);
        return "[" + string.Join(",", lines) + "]";
    }

    /// <summary>/events: as <see cref="GetJsonAsync"/>, but only what private mode lets callers see (above); waits
    /// for a visible event, not just any.</summary>
    public async Task<string> GetVisibleJsonAsync(long since, TimeSpan wait, CancellationToken ct = default)
    {
        var end = DateTime.UtcNow + wait;
        while (true)
        {
            Task changed;
            List<string> lines;
            lock (_lock)
            {
                lines = Snapshot(since, Visible(Spell()));
                changed = _changed.Task;
            }
            var left = end - DateTime.UtcNow;
            if (lines.Count > 0 || left <= TimeSpan.Zero) return "[" + string.Join(",", lines) + "]";
            try { await changed.WaitAsync(left, ct); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { return "[]"; }
        }
    }

    Func<int, bool> Visible(int now)
    {
        bool sees = Sees();
        return i => now == 0 ? _meta[i].Spell == 0
            : sees ? _meta[i].Spell == 0 || _meta[i].Spell == now
            : _meta[i].Spell == now && PrivateKinds.Contains(_meta[i].Kind);
    }

    public async Task<List<JsonObject>> GetAsync(long since, TimeSpan wait, CancellationToken ct = default) =>
        (await GetRawAsync(since, wait, ct)).Select(l => (JsonObject)JsonNode.Parse(l)!).ToList();

    /// <summary>Waits for the first event after <paramref name="since"/> matching <paramref name="pred"/>.</summary>
    public async Task<JsonObject?> WaitForAsync(long since, Func<JsonObject, bool> pred, TimeSpan timeout, CancellationToken ct = default)
    {
        var end = DateTime.UtcNow + timeout;
        while (true)
        {
            var left = end - DateTime.UtcNow;
            var evs = await GetAsync(since, left > TimeSpan.Zero ? left : TimeSpan.Zero, ct);
            foreach (var o in evs)
            {
                since = Math.Max(since, (long)o["seq"]!);
                if (pred(o)) return o;
            }
            if (DateTime.UtcNow >= end || ct.IsCancellationRequested) return null;
        }
    }

    List<string> Snapshot(long since, Func<int, bool>? keep = null)
    {
        var list = new List<string>();
        // events are seq-ordered: binary search for the first one after `since`
        int lo = 0, hi = _events.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_events[mid].Seq <= since) lo = mid + 1; else hi = mid;
        }
        for (var i = lo; i < _events.Count; i++) if (keep == null || keep(i)) list.Add(_events[i].Json);
        return list;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _writer?.Dispose(); } catch { }
        }
    }
}
