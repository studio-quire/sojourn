using System.Diagnostics.Tracing;

namespace Sojourn.Render.Realtime;

/// <summary>
/// SOJOURN_ALLOC_PROFILE=1: what the process allocates, by type, from the runtime's own allocation samples (an event
/// about every 100 KB allocated, naming the type that crossed the mark), logged every 10 s as the top types and their
/// share. Cheap enough to leave on for a session; it says what to pool, not where from (no stacks). Sojourn Access
/// keeps one on in every session at five minutes (<see cref="Every"/>), so a tester's log says what a long ride on
/// Second Life allocates, which the perf grid doesn't reproduce (docs/performance.md > Sojourn Access on a small PC).
/// </summary>
public sealed class AllocProfile : EventListener
{
    readonly Action<string> _log;
    readonly Dictionary<string, long> _bytes = new();
    readonly object _lock = new();
    readonly long _period;
    long _lastReport = System.Environment.TickCount64;

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
        long amount = 100_000;
        for (int i = 0; i < e.PayloadNames.Count; i++)
        {
            if (e.PayloadNames[i] == "TypeName" && e.Payload[i] is string t) type = t;
            else if (e.PayloadNames[i] == "AllocationAmount64" && e.Payload[i] is ulong a) amount = (long)a;
        }
        string? report = null;
        lock (_lock)
        {
            _bytes[type] = _bytes.GetValueOrDefault(type) + amount;
            long now = System.Environment.TickCount64;
            if (_period > 0 && now - _lastReport >= _period)   // (events can come from the base constructor, before _period is set)
            {
                double secs = (now - _lastReport) / 1000.0;
                _lastReport = now;
                long total = _bytes.Values.Sum();
                report = $"allocations {total / secs / (1 << 20):F0} MB/s: " + string.Join(", ", _bytes.OrderByDescending(kv => kv.Value).Take(14)
                    .Select(kv => $"{kv.Key} {100.0 * kv.Value / Math.Max(1, total):F0}%"));
                _bytes.Clear();
            }
        }
        if (report != null) _log(report);
    }
}
