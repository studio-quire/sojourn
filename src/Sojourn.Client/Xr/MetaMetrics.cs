using Silk.NET.Core;
using Silk.NET.OpenXR;

namespace Sojourn.Client.Xr;

// OpenXR's Session, not the Sojourn.Session namespace (which a bare name in Sojourn.* finds first)
using Session = Silk.NET.OpenXR.Session;

/// <summary>
/// Meta's runtime's own counters (XR_META_performance_metrics): what the compositor did with our frames (dropped
/// frames, its own CPU and GPU time, motion-to-photon latency, the spacewarp mode), for the console's pacing line.
/// What a person sees under load is decided there, not in our frame times, so the next headset session's log says it
/// directly. Inert where the extension isn't offered.
/// </summary>
sealed unsafe class MetaMetrics
{
    const StructureType StateType = (StructureType)1000232001, CounterType = (StructureType)1000232002;
    delegate* unmanaged[Cdecl]<Instance, uint, uint*, ulong*, Result> _enumerate;
    delegate* unmanaged[Cdecl]<Session, PerformanceMetricsStateMETA*, Result> _setState;
    delegate* unmanaged[Cdecl]<Session, ulong, PerformanceMetricsCounterMETA*, Result> _query;
    readonly Session _session;
    readonly (ulong Path, string Name)[] _counters = [];

    public static MetaMetrics? TryCreate(XrRuntime rt, Session session, Action<string> log)
    {
        if (!rt.Extensions.Contains("XR_META_performance_metrics")) return null;
        try { return new MetaMetrics(rt, session, log); }
        catch (Exception e) { log($"OpenXR: performance metrics unavailable ({e.Message})"); return null; }
    }

    MetaMetrics(XrRuntime rt, Session session, Action<string> log)
    {
        _session = session;
        PfnVoidFunction f;
        XrRuntime.Check(rt.Api.GetInstanceProcAddr(rt.Instance, "xrEnumeratePerformanceMetricsCounterPathsMETA", &f), "xrGetInstanceProcAddr");
        _enumerate = (delegate* unmanaged[Cdecl]<Instance, uint, uint*, ulong*, Result>)(void*)f.Handle;
        XrRuntime.Check(rt.Api.GetInstanceProcAddr(rt.Instance, "xrSetPerformanceMetricsStateMETA", &f), "xrGetInstanceProcAddr");
        _setState = (delegate* unmanaged[Cdecl]<Session, PerformanceMetricsStateMETA*, Result>)(void*)f.Handle;
        XrRuntime.Check(rt.Api.GetInstanceProcAddr(rt.Instance, "xrQueryPerformanceMetricsCounterMETA", &f), "xrGetInstanceProcAddr");
        _query = (delegate* unmanaged[Cdecl]<Session, ulong, PerformanceMetricsCounterMETA*, Result>)(void*)f.Handle;

        uint n = 0;
        XrRuntime.Check(_enumerate(rt.Instance, 0, &n, null), "xrEnumeratePerformanceMetricsCounterPathsMETA");
        var paths = new ulong[n];
        fixed (ulong* p = paths) XrRuntime.Check(_enumerate(rt.Instance, n, &n, p), "xrEnumeratePerformanceMetricsCounterPathsMETA");
        var named = new List<(ulong, string)>();
        var buf = stackalloc byte[256];
        foreach (var path in paths)
        {
            uint len;
            if (rt.Api.PathToString(rt.Instance, path, 256, &len, buf) != Result.Success) continue;
            var s = XrRuntime.Str(buf, 256);
            named.Add((path, s.Replace("/perfmetrics_meta/", "")));
        }
        _counters = named.ToArray();
        var st = new PerformanceMetricsStateMETA { Type = StateType, Enabled = 1 };
        XrRuntime.Check(_setState(session, &st), "xrSetPerformanceMetricsStateMETA");
        log($"OpenXR: performance metrics on ({string.Join(", ", _counters.Select(c => c.Name))})");
    }

    /// <summary>Every counter's current value, as "name value" pairs for the console (empty when none answer).</summary>
    public string Line()
    {
        var parts = new List<string>(_counters.Length);
        foreach (var (path, name) in _counters)
        {
            var c = new PerformanceMetricsCounterMETA { Type = CounterType };
            if (_query(_session, path, &c) != Result.Success) continue;
            var flags = (ulong)c.CounterFlags;
            // the flags say which value is valid: 2 the uint, 4 the float (XrPerformanceMetricsCounterFlagBitsMETA)
            if ((flags & 4) != 0) parts.Add($"{name} {c.FloatValue:0.##}");
            else if ((flags & 2) != 0) parts.Add($"{name} {c.UintValue}");
        }
        return string.Join(", ", parts);
    }
}
