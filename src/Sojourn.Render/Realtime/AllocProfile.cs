using System.Diagnostics.Tracing;

namespace Sojourn.Render.Realtime;

/// <summary>
/// SOJOURN_ALLOC_PROFILE=1: what the process allocates, by type, from the runtime's own allocation samples (an event
/// about every 100 KB allocated, naming the type that crossed the mark), logged every 10 s as the top types and their
/// share. Cheap enough to leave on for a session; it says what to pool, not where from (no stacks). Sojourn Access
/// keeps one on in every session at five minutes (<see cref="Every"/>), so a tester's log says what a long ride on
/// Second Life allocates, which the perf grid doesn't reproduce (docs/performance.md > Sojourn Access on a small PC).
/// Each top type also says how big the objects it's allocated as are (the size half its bytes come in at or above:
/// a terrain patch's 1 KB arrays, a sound's megabytes) and how much went to the large object heap, which narrows where
/// from without stacks. (Which threads allocated told nothing: the thread pool's, a different few each time.)
/// </summary>
public sealed class AllocProfile : EventListener
{
    readonly Action<string> _log;
    readonly Dictionary<string, Tally> _types = new();
    readonly object _lock = new();
    readonly long _period;
    long _lastReport = System.Environment.TickCount64;

    sealed class Tally
    {
        public long Bytes, Large;
        public readonly List<long> Sizes = new();   // the sampled objects' sizes (each sample stands for ~100 KB)
    }

    AllocProfile(Action<string> log, long periodMs) { _log = log; _period = periodMs; }

    public static AllocProfile? Start(Action<string>? log) =>
        log != null && System.Environment.GetEnvironmentVariable("SOJOURN_ALLOC_PROFILE") == "1" ? new AllocProfile(log, 10_000) : null;

    /// <summary>One reporting every <paramref name="periodMs"/>, unless SOJOURN_ALLOC_PROFILE says 0 (none) or 1 (the
    /// renderer's own, every 10 s, does it).</summary>
    public static AllocProfile? Every(Action<string> log, long periodMs) =>
        System.Environment.GetEnvironmentVariable("SOJOURN_ALLOC_PROFILE") is "0" or "1" ? null : new AllocProfile(log, periodMs);

    protected override void OnEventSourceCreated(EventSource source)
    {
        // the GC keyword (0x1) at Verbose carries GCAllocationTick
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick") || e.Payload == null || e.PayloadNames == null) return;
        string type = "?";
        long amount = 100_000, size = 0;
        bool large = false;
        for (int i = 0; i < e.PayloadNames.Count; i++)
        {
            switch (e.PayloadNames[i])
            {
                case "TypeName" when e.Payload[i] is string t: type = t; break;
                case "AllocationAmount64" when e.Payload[i] is ulong a: amount = (long)a; break;
                case "AllocationKind" when e.Payload[i] is uint k: large = k == 1; break;
                case "ObjectSize" when e.Payload[i] is ulong s: size = (long)s; break;
            }
        }
        string? report = null;
        lock (_lock)
        {
            if (!_types.TryGetValue(type, out var tally)) _types[type] = tally = new Tally();
            tally.Bytes += amount;
            if (large) tally.Large += amount;
            if (size > 0 && tally.Sizes.Count < 512) tally.Sizes.Add(size);
            long now = System.Environment.TickCount64;
            if (_period > 0 && now - _lastReport >= _period)   // (events can come from the base constructor, before _period is set)
            {
                report = Report((now - _lastReport) / 1000.0);
                _lastReport = now;
                _types.Clear();
            }
        }
        if (report != null) _log(report);
    }

    string Report(double secs)
    {
        long total = Math.Max(1, _types.Values.Sum(t => t.Bytes));
        var parts = _types.OrderByDescending(kv => kv.Value.Bytes).Take(14).Select(kv =>
        {
            var t = kv.Value;
            string pct = $"{kv.Key} {100.0 * t.Bytes / total:F0}%";
            // the top few say what they're made of
            if (100.0 * t.Bytes / total < 5 || t.Sizes.Count == 0) return pct;
            t.Sizes.Sort();
            long median = t.Sizes[t.Sizes.Count / 2];
            string big = t.Large > 0 ? $", {100.0 * t.Large / t.Bytes:F0}% large" : "";
            return $"{pct} (half in objects of {Size(median)} or more{big})";
        });
        long largeAll = _types.Values.Sum(t => t.Large);
        return $"allocations {total / secs / (1 << 20):F0} MB/s ({100.0 * largeAll / total:F0}% large objects): " + string.Join(", ", parts);
    }

    static string Size(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.#} MB" : b >= 1024 ? $"{b / 1024} KB" : $"{b} bytes";
}
