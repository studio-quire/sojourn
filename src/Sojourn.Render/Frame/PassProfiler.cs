using System.Text;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

/// <summary>
/// Optional GPU timestamps around the renderer's pass groups, averaged over every frame recorded while
/// <see cref="Enabled"/> is set (the harness's <c>bench --profile</c>). Each mark closes the interval opened by
/// the previous one, named after the closing mark; a name marked several times in a frame (an eye's passes) is summed,
/// so every pass reads as GPU ms a frame.
/// </summary>
public sealed unsafe class PassProfiler : IDisposable
{
    public static bool Enabled;

    const uint MaxMarks = 96;
    readonly VulkanContext _vk;
    sealed class Slot
    {
        public QueryPool Pool;
        public readonly List<string> Marks = new();
        public ulong UploadBytes;
        public int ProbeFaces;
    }
    readonly Dictionary<FrameResources, Slot> _frames = new();
    readonly Dictionary<string, (double Sum, int Count)> _totals = new();
    readonly Dictionary<string, (double Sum, int Count)> _window = new();
    // SOJOURN_PROFILE_MEDIAN=1: a window's passes as their median frame rather than their mean (robust to frames another
    // process's GPU work sliced into, for timing on a shared GPU)
    static readonly bool Median = System.Environment.GetEnvironmentVariable("SOJOURN_PROFILE_MEDIAN") == "1";
    readonly Dictionary<string, List<double>> _samples = new();
    double _windowFrameSum;
    int _windowFrames, _windowOver;

    /// <summary>Frames whose GPU time exceeds this count as over (the headset sets its display period).</summary>
    public double OverMs { get; set; } = double.PositiveInfinity;
    /// <summary>Each frame's GPU time and loading's part of it as it's read back (render thread, a frame or two after it
    /// was recorded).</summary>
    public Action<GpuFrame>? FrameDone { get; set; }
    /// <summary>How many frames of the last window <see cref="TakeWindow"/> took were over <see cref="OverMs"/>.</summary>
    public int WindowOver { get; private set; }
    readonly List<string> _order = new();
    readonly Dictionary<string, double> _frameSums = new();
    readonly object _lock = new();
    Slot _current = new();
    CommandBuffer _cmd;
    // SOJOURN_GPU_LOG=path: every frame's GPU ms and its loading parts (tools/gpuarrival.py reads it)
    readonly StreamWriter? _log;
    long _logT0, _logFlushAt;

    public PassProfiler(VulkanContext vk)
    {
        _vk = vk;
        if (System.Environment.GetEnvironmentVariable("SOJOURN_GPU_LOG") is { Length: > 0 } path)
            try
            {
                _log = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
                _log.WriteLine("t,gpu_ms,uploads_ms,upload_mb,probes_ms,probe_faces,impostors_ms,shadows_ms,gbuffer_ms,alpha_ms,water_ms,ab");
            }
            catch (IOException) { }
    }

    /// <summary>The bytes the frame's upload pass copies (Uploader.RecordedBytes).</summary>
    public void UploadBytes(ulong bytes)
    {
        if (_cmd.Handle != 0) _current.UploadBytes = bytes;
    }

    /// <summary>Reflection probe faces the frame captures (counted while its marks are suspended too).</summary>
    public void ProbeFaces(int faces)
    {
        if (_cmd.Handle != 0) _current.ProbeFaces += faces;
    }

    public void Begin(FrameResources fr, CommandBuffer cmd)
    {
        _cmd = default;
        if (!Enabled) return;
        if (!_frames.TryGetValue(fr, out var f))
        {
            var ci = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = MaxMarks };
            VulkanContext.Check(_vk.Api.CreateQueryPool(_vk.Device, in ci, null, out var pool), "vkCreateQueryPool");
            _frames[fr] = f = new Slot { Pool = pool };
        }
        f.Marks.Clear();
        f.UploadBytes = 0;
        f.ProbeFaces = 0;
        _current = f;
        _cmd = cmd;
        _paused = default;
        _vk.Api.CmdResetQueryPool(cmd, f.Pool, 0, MaxMarks);
        Mark("start");
        var captured = f;
        fr.Retire.Add(() => Collect(captured));
    }

    public bool Suspended;
    /// <summary>Added to every pass's name (an A/B comparison's side, Renderer.AbStep).</summary>
    public static string Tag = "";

    public void Mark(string name)
    {
        if (_cmd.Handle == 0 || Suspended || _current.Marks.Count >= MaxMarks) return;
        _vk.Api.CmdWriteTimestamp2(_cmd, PipelineStageFlags2.AllCommandsBit, _current.Pool, (uint)_current.Marks.Count);
        _current.Marks.Add(name is "start" or Idle ? name : name + Tag);
    }

    // a split frame (FrameRenderer, the headset's overlap): its scene's buffer ends, its output goes into a second one
    // submitted later; the GPU's wait between them isn't the frame's work
    const string Idle = "\u0001idle";
    CommandBuffer _paused;

    /// <summary>The frame's buffer is ending: no marks until <see cref="Resume"/>.</summary>
    public void Pause()
    {
        _paused = _cmd;
        _cmd = default;
    }

    /// <summary>Go on marking the frame in its second buffer; the time between the two isn't counted.</summary>
    public void Resume(CommandBuffer cmd)
    {
        if (_paused.Handle == 0) return;
        _paused = default;
        _cmd = cmd;
        Mark(Idle);
    }

    long _lastEnd;
    double _windowIdleSum;
    int _windowIdles;

    /// <summary>Mean GPU milliseconds between one frame's last mark and the next one's first, over the last window
    /// <see cref="TakeWindow"/> took (gaps over 50 ms left out): how long the GPU sat idle between frames.</summary>
    public double WindowIdleMs { get; private set; }

    void Collect(Slot f)
    {
        int n = f.Marks.Count;
        if (n < 2) return;
        var ticks = new ulong[n];
        fixed (ulong* p = ticks)
            if (_vk.Api.GetQueryPoolResults(_vk.Device, f.Pool, 0, (uint)n, (nuint)(n * 8), p, 8, QueryResultFlags.Result64Bit) != Result.Success) return;
        double period = _vk.Properties.Limits.TimestampPeriod * 1e-6;   // ns per tick -> ms
        double gpuMs;
        GpuFrame done;
        lock (_lock)
        {
            // a pass marked more than once in a frame (once an eye, where the eyes aren't drawn together) counts as its sum:
            // every pass reads as ms a frame
            _frameSums.Clear();
            double idle = 0;
            for (int i = 1; i < n; i++)
            {
                var name = f.Marks[i];
                double ms = (ticks[i] - ticks[i - 1]) * period;
                if (name == Idle) { idle += ms; continue; }
                if (!_frameSums.TryAdd(name, ms)) _frameSums[name] += ms;
            }
            if (_lastEnd != 0 && ticks[0] > (ulong)_lastEnd)
            {
                double gap = (ticks[0] - (ulong)_lastEnd) * period;
                if (gap < 50) { _windowIdleSum += gap; _windowIdles++; }
            }
            _lastEnd = (long)ticks[n - 1];
            foreach (var (name, ms) in _frameSums)
            {
                if (!_totals.TryGetValue(name, out var t)) _order.Add(name);
                _totals[name] = (t.Sum + ms, t.Count + 1);
                _window.TryGetValue(name, out var w);
                _window[name] = (w.Sum + ms, w.Count + 1);
                if (Median) { if (!_samples.TryGetValue(name, out var s)) _samples[name] = s = new(); s.Add(ms); }
            }
            double frameMs = (ticks[n - 1] - ticks[0]) * period - idle;
            _windowFrameSum += frameMs;
            _windowFrames++;
            if (frameMs > OverMs) _windowOver++;
            gpuMs = frameMs;
            // the frame's own A/B side (SOJOURN_AB): the tag it was recorded under, which Tag no longer is by now
            string tag = "";
            foreach (var (name, _) in _frameSums)
                if (name.EndsWith("|on")) { tag = "|on"; break; }
                else if (name.EndsWith("|off")) { tag = "|off"; break; }
            double Sum(string name) => _frameSums.TryGetValue(name + tag, out var ms) ? ms : 0;
            done = new GpuFrame(gpuMs, Sum("uploads"), Sum("probes"), Sum("impostor captures"), f.UploadBytes, f.ProbeFaces);
            if (_log != null)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_logT0 == 0) _logT0 = now;
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                _log.WriteLine(string.Create(ic, $"{(now - _logT0) / (double)System.Diagnostics.Stopwatch.Frequency:F3},{gpuMs:F3},{done.UploadMs:F3},{f.UploadBytes / 1048576.0:F2},{done.ProbeMs:F3},{f.ProbeFaces},{done.CaptureMs:F3},{Sum("shadows"):F3},{Sum("gbuffer"):F3},{Sum("alpha"):F3},{Sum("water haze") + Sum("pre-water alpha") + Sum("water"):F3},{(tag == "|on" ? "1" : tag == "|off" ? "0" : "")}"));
                if (now - _logFlushAt > System.Diagnostics.Stopwatch.Frequency) { _log.Flush(); _logFlushAt = now; }
            }
        }
        FrameDone?.Invoke(done);
    }

    /// <summary>Mean GPU milliseconds per frame since the last call (whole frame, and per pass group), then starts
    /// a new window.</summary>
    public (double FrameMs, int Frames, IReadOnlyList<(string Pass, double Ms)> Passes) TakeWindow()
    {
        lock (_lock)
        {
            var passes = _order.Where(_window.ContainsKey).Select(n => (n, Median && _samples.TryGetValue(n, out var s) && s.Count > 0
                ? s.Order().ElementAt(s.Count / 2) : _window[n].Sum / _window[n].Count)).ToList();
            foreach (var s in _samples.Values) s.Clear();
            var result = (_windowFrames > 0 ? _windowFrameSum / _windowFrames : 0, _windowFrames, passes);
            WindowOver = _windowOver;
            WindowIdleMs = _windowIdles > 0 ? _windowIdleSum / _windowIdles : 0;
            _windowIdleSum = 0;
            _windowIdles = 0;
            _window.Clear();
            _windowFrameSum = 0;
            _windowFrames = 0;
            _windowOver = 0;
            return result;
        }
    }

    /// <summary>Forget the totals (e.g. after warm-up).</summary>
    public void Reset()
    {
        lock (_lock) { _totals.Clear(); _order.Clear(); _window.Clear(); _windowFrameSum = 0; _windowFrames = 0; }
    }

    public string Report()
    {
        var sb = new StringBuilder("GPU pass times (mean ms per frame):");
        lock (_lock)
        {
            double total = 0;
            foreach (var name in _order)
            {
                var (sum, count) = _totals[name];
                sb.Append($"\n  {name,-14} {sum / count,7:F3}  ({count} frames)");
                total += sum / count;
            }
            sb.Append($"\n  {"total",-14} {total,7:F3}");
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var f in _frames.Values) _vk.Api.DestroyQueryPool(_vk.Device, f.Pool, null);
        _frames.Clear();
        _log?.Dispose();
    }
}

/// <summary>A frame's GPU time (ms, both eyes, without the wait between a split frame's two buffers) and loading's
/// part of it: the upload pass (copies and mip chains, and how many bytes), reflection probe captures (and how many
/// faces), impostor captures.</summary>
public readonly record struct GpuFrame(double Ms, double UploadMs, double ProbeMs, double CaptureMs, ulong UploadBytes, int ProbeFaces)
{
    public double LoadMs => UploadMs + ProbeMs + CaptureMs;
}
