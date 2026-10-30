using System.Diagnostics.Tracing;

namespace Sojourn.Render.Realtime;

/// <summary>
/// SOJOURN_ALLOC_PROFILE=1: what the process allocates, by type, from the runtime's own allocation samples (an event
/// about every 100 KB allocated, naming the type that crossed the mark), logged every 10 s as the top types and their
/// share. Cheap enough to leave on for a session; it says what to pool, not where from (no stacks).
/// </summary>
public sealed class AllocProfile : EventListener
{
    readonly Action<string> _log;
    readonly Dictionary<string, long> _bytes = new();
    readonly object _lock = new();
    long _lastReport = System.Environment.TickCount64;

    AllocProfile(Action<string> log) { _log = log; }

    public static AllocProfile? Start(Action<string>? log) =>
        log != null && System.Environment.GetEnvironmentVariable("SOJOURN_ALLOC_PROFILE") == "1" ? new AllocProfile(log) : null;

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
            if (now - _lastReport >= 10_000)
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
