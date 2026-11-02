using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sojourn.Session.Offscreen;

/// <summary>
/// How busy the whole processor was over a stretch, and how much of it was this app, for the view on the processor's
/// log lines: whether a slow first picture on a tester's PC was the driver compiling or waiting its turn behind
/// something else (its threads run below normal priority). Claude Opus 5.5, 2026-11-01.
/// </summary>
readonly struct ProcessorShare
{
    readonly long _idle, _total, _app;

    ProcessorShare(long idle, long total, long app) => (_idle, _total, _app) = (idle, total, app);

    public static ProcessorShare Now()
    {
        long idle = 0, total = 0;
        if (OperatingSystem.IsWindows() && GetSystemTimes(out var i, out var k, out var u)) (idle, total) = (i, k + u);   // (kernel time includes idle)
        using var self = Process.GetCurrentProcess();
        return new(idle, total, self.TotalProcessorTime.Ticks);
    }

    /// <summary>"the processor 85% busy meanwhile, this app 60% of it" since this mark (of all its logical processors).</summary>
    public string Since()
    {
        var now = Now();
        long total = now._total - _total, busy = total - (now._idle - _idle);
        if (total <= 0) return "";
        // (the system's times and the app's are both summed over every logical processor)
        long app = Math.Min(now._app - _app, busy);
        return $"the processor {100.0 * busy / total:0}% busy meanwhile, this app {100.0 * app / total:0}% of it";
    }

    [DllImport("kernel32")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
