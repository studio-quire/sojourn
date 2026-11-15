using System.Runtime;

namespace Sojourn.Render.Realtime;

/// <summary>
/// SOJOURN_COMPACT_HEAP=MINUTES (off unless set): every so many minutes, when the last full collection left more than
/// 64 MB free in the large object heap, a full blocking collection that compacts it too, logged with how long every
/// thread was held and what it gave back. The large object heap is never compacted otherwise, so the free space a burst
/// of big arrays leaves (a login, a new region's meshes) stays committed for the session; this measures what giving it
/// back costs (a pause of every thread, the window's too) against what it saves, for docs/performance.md > Sojourn
/// Access on a small PC. Whether a tester's app should do it is undecided. Claude Opus 5.5, 2026-11-14.
/// </summary>
public sealed class HeapCompaction : IDisposable
{
    readonly Timer _timer;
    readonly Action<string> _log;

    HeapCompaction(Action<string> log, TimeSpan every)
    {
        _log = log;
        _timer = new Timer(_ => Run(), null, every, every);
    }

    public static HeapCompaction? Start(Action<string> log) =>
        double.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_COMPACT_HEAP"), out var m) && m > 0
            ? new HeapCompaction(log, TimeSpan.FromMinutes(m)) : null;

    void Run()
    {
        try
        {
            var before = Full();
            long freeBefore = before.GenerationInfo.Length > 3 ? before.GenerationInfo[3].FragmentationAfterBytes : 0;
            if (freeBefore < 64L << 20) return;
            long privBefore = Private(), committedBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
            var pausedBefore = GC.GetTotalPauseDuration();
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            var after = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            var g2 = after.GenerationInfo[2];
            var loh = after.GenerationInfo[3];
            _log($"compacted the heap: every thread held {(GC.GetTotalPauseDuration() - pausedBefore).TotalMilliseconds:0} ms ({ms:0} ms in all); " +
                 $"committed {committedBefore >> 20} -> {after.TotalCommittedBytes >> 20} MB, private {privBefore >> 20} -> {Private() >> 20} MB; " +
                 $"large object heap {before.GenerationInfo[3].SizeAfterBytes >> 20} MB ({freeBefore >> 20} free) -> {loh.SizeAfterBytes >> 20} ({loh.FragmentationAfterBytes >> 20} free), " +
                 $"gen2 {g2.SizeAfterBytes >> 20} MB live after");
        }
        catch (Exception e) { _log($"compacting the heap: {e.GetType().Name}: {e.Message}"); }
    }

    static GCMemoryInfo Full()
    {
        GCMemoryInfo bg = GC.GetGCMemoryInfo(GCKind.Background), fb = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return bg.Index > fb.Index ? bg : fb;
    }

    static long Private()
    {
        try { using var p = System.Diagnostics.Process.GetCurrentProcess(); return p.PrivateMemorySize64; }
        catch (Exception) { return 0; }
    }

    public void Dispose() => _timer.Dispose();
}
