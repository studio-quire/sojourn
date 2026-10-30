using System.Diagnostics;
using System.Globalization;

namespace Sojourn.Client.Xr;

/// <summary>
/// Lag on purpose, for testing how late frames look: SOJOURN_LAG_CPU_MS=8 (or a range, 4-20: each frame a random
/// amount in it) spins the render thread that long in each headset frame, between xrWaitFrame and the render, as a
/// heavy scene would. SOJOURN_LAG_SPIKE=N:MS adds MS more to one frame in N (a hitch). Off unless set.
/// SOJOURN_LAG_TEST=1 (tools/lagtest.py) also puts a "test lag" row in the settings menu's view section and a
/// `test_lag` setting on the agent API, so the lag can be changed live in the headset, and the two diagnostics below
/// on the agent API (`test_wait_gpu`, `test_mirror`; the mirror on the menu too).
/// </summary>
static class LagHook
{
    static (double Lo, double Hi)? _cpu = Range(Environment.GetEnvironmentVariable("SOJOURN_LAG_CPU_MS"));
    static readonly (int Every, double Ms)? Spike = SpikeOf(Environment.GetEnvironmentVariable("SOJOURN_LAG_SPIKE"));
    static readonly Random Rng = new(1);
    static long _frames;

    /// <summary>The menu row and the agent API setting are offered (a lag test run).</summary>
    public static readonly bool Testing = Environment.GetEnvironmentVariable("SOJOURN_LAG_TEST") == "1";
    /// <summary>The menu's steps: off, fixed amounts, and ranges (a random amount each frame).</summary>
    public static readonly string[] Steps = { "off", "4", "8", "6-14", "12", "10-20", "20", "30" };

    public static bool On => _cpu != null || Spike != null;

    /// <summary>Waiting for the frame's GPU work before handing the headset its images (XrSession.WaitGpuBeforeRelease):
    /// the jitter fix, always on; SOJOURN_XR_WAIT_GPU=0 or the lag test's `test_wait_gpu` turns it off, for diagnosis.
    /// And the desktop mirror (MirrorTarget.WindowOn; off with SOJOURN_NO_MIRROR=1), a lag-test diagnostic.</summary>
    public static bool WaitGpu { get; set; } = Environment.GetEnvironmentVariable("SOJOURN_XR_WAIT_GPU") != "0";
    public static bool Mirror { get; set; } = Environment.GetEnvironmentVariable("SOJOURN_NO_MIRROR") != "1";

    /// <summary>The CPU lag now, as text ("off", "8", "6-14").</summary>
    public static string Cpu
    {
        get => _cpu is not { } c ? "off" : c.Lo == c.Hi ? $"{c.Lo:0.#}" : $"{c.Lo:0.#}-{c.Hi:0.#}";
        set => _cpu = value is "off" or "0" or "" ? null : Range(value);
    }

    /// <summary>Parses "8" or "6-14" (ms); null if it isn't one.</summary>
    public static (double Lo, double Hi)? Range(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var parts = v.Split('-', 2);
        if (!double.TryParse(parts[0], CultureInfo.InvariantCulture, out var lo) || lo < 0) return null;
        double hi = parts.Length > 1 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var h) ? h : lo;
        return lo == 0 && hi == 0 ? null : (lo, Math.Min(100, Math.Max(lo, hi)));
    }

    public static void Frame()
    {
        double ms = 0;
        if (_cpu is { } c) ms += c.Lo + Rng.NextDouble() * (c.Hi - c.Lo);
        if (Spike is { } s && ++_frames % s.Every == 0) ms += s.Ms;
        if (ms <= 0) return;
        long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
    }

    static (int, double)? SpikeOf(string? v)
    {
        var parts = v?.Split(':', 2);
        return parts is { Length: 2 } && int.TryParse(parts[0], out var n) && n > 0 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var ms) ? (n, ms) : null;
    }
}
