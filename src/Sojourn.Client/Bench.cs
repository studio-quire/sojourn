using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client;

/// <summary>
/// The measurement tour (--bench): fixed views of the Atelier test scene (Multitude's comparison views) and a
/// moving camera, each held a few seconds after everything in view has loaded, with CPU phase times per frame and
/// GPU pass times per view. Prints a table (and writes it to --bench-out), then quits.
/// </summary>
public sealed class Bench
{
    sealed record View(string Name, Vector3 Eye, Vector3 Focus, bool Orbit = false);

    static readonly View[] AtelierViews =
    {
        new("overview", new(190, 112, 46), new(128, 136, 24)),
        new("plaza", new(128, 108, 25.5f), new(128, 150, 25)),
        new("pavilion", new(128, 141, 25.3f), new(128, 156, 25)),
        new("pbr", new(146, 105, 25.5f), new(156, 128, 24)),
        new("waterside", new(95, 132, 23.5f), new(78, 118, 21)),
        new("pond", new(145, 145, 26), new(156, 156, 22)),
        new("orbit", new(128, 128, 27), new(128, 128, 25), Orbit: true),
    };

    /// <summary>The tour: Atelier's views, or --bench-views FILE's (a view a line: name, eye x y z, focus x y z, in the
    /// login region's coordinates; "orbit" as a seventh word circles the focus).</summary>
    readonly View[] Views;

    /// <summary>The environment's time during the tour: fixed, so clouds and water stand still and shots of two builds
    /// can be compared pixel for pixel.</summary>
    public static readonly DateTime FixedTime = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    static View[] Load(string path) => File.ReadAllLines(path)
        .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
        .Select(l =>
        {
            var w = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            float F(int i) => float.Parse(w[i], CultureInfo.InvariantCulture);
            return new View(w[0], new(F(1), F(2), F(3)), new(F(4), F(5), F(6)), w.Length > 7 && w[7] == "orbit");
        }).ToArray();

    sealed class Sample
    {
        public readonly List<double> Frame = new(), Cpu = new(), Scene = new(), Avatar = new(), Probe = new(), Record = new();
        public double GpuMs;
        public IReadOnlyList<(string Pass, double Ms)> Passes = [];
        public int Draws, Instances, Captures;
    }

    readonly ClientOptions _opt;
    readonly FrameRenderer _renderer;
    readonly AgentSession _session;
    readonly ILogger _log;
    readonly Dictionary<string, Sample> _results = new();
    int _view = -1;
    bool _warming = true;
    double _phaseStart, _settledSince = -1, _lastFrame;
    public bool Done { get; private set; }

    public Bench(ClientOptions opt, FrameRenderer renderer, AgentSession session, ILogger log)
    {
        _opt = opt;
        _renderer = renderer;
        _session = session;
        _log = log;
        Views = opt.BenchViews is { } file ? Load(file) : AtelierViews;
    }

    public (ulong Region, Vector3 Position, Vector3 Forward)? Camera(double now)
    {
        if (_session.Client is not { Network.CurrentSim: { } sim } || !_session.Online) return null;
        if (_view < 0) { Next(now); }
        if (Done) return null;
        var v = Views[_view];
        if (!v.Orbit) return (sim.Handle, v.Eye, v.Focus - v.Eye);
        // a full turn around the focus at 22 m every 20 s, looking inward: the camera always moving, as in a headset
        double a = (now - _phaseStart) / 20.0 * Math.Tau;
        var eye = v.Focus + new Vector3((float)Math.Cos(a) * 22, (float)Math.Sin(a) * 22, v.Eye.Z - v.Focus.Z);
        return (sim.Handle, eye, v.Focus - eye);
    }

    void Next(double now)
    {
        _view++;
        if (_view >= Views.Length)
        {
            if (_warming) { _warming = false; _view = 0; _log.LogInformation("bench: warm, measuring"); }
            else { Done = true; return; }
        }
        _phaseStart = now;
        _settledSince = -1;
        _shotTaken = false;
        _renderer.Profiler.TakeWindow();
        if (!_warming) _results[Views[_view].Name] = new Sample();
        _log.LogInformation("bench: {Phase} {View}", _warming ? "warming" : "measuring", Views[_view].Name);
    }

    public void Frame(double now, FrameStats s)
    {
        if (_view < 0 || Done || !s.Presented) { _lastFrame = now; return; }
        double frameMs = (now - _lastFrame) * 1000;
        _lastFrame = now;
        double held = now - _phaseStart;
        if (_warming)
        {
            // hold each view until what's in it has loaded and its probes are captured (bounded)
            bool settled = s.Pending == 0 && s.Captures == 0;
            if (!settled) _settledSince = -1;
            else if (_settledSince < 0) _settledSince = now;
            if ((_settledSince >= 0 && now - _settledSince > 1.5 && held > 2) || held > 30) Next(now);
            return;
        }
        var r = _results[Views[_view].Name];
        if (held > 0.5)   // skip the switch itself
        {
            r.Frame.Add(frameMs);
            r.Cpu.Add(s.TotalCpuMs - s.WaitMs);
            r.Scene.Add(s.SceneMs);
            r.Avatar.Add(s.AvatarMs);
            r.Probe.Add(s.ProbeMs);
            r.Record.Add(s.RecordMs);
            r.Draws = s.Draws;
            r.Instances = s.Instances;
            r.Captures += s.Captures;
        }
        else if (held <= 0.5) _renderer.Profiler.TakeWindow();
        if (held >= _opt.BenchSeconds + 0.5)
        {
            var gpu = _renderer.Profiler.TakeWindow();
            r.GpuMs = gpu.FrameMs;
            r.Passes = gpu.Passes;
            Next(now);
        }
    }

    bool _shotTaken;

    /// <summary>One screenshot per measured view, three quarters into it (the exposure settled).</summary>
    public string? CaptureNow(string baseDir)
    {
        if (_warming || Done || _view < 0 || _shotTaken || _lastFrame - _phaseStart < Math.Max(1.0, _opt.BenchSeconds * 0.75)) return null;
        _shotTaken = true;
        var size = _opt.EyeSize is { } e ? $"{e.W}x{e.H}" : "window";
        return Path.Combine(baseDir, "shots", $"bench-{Views[_view].Name}-{size}{(_opt.Stereo ? "-stereo" : "")}{(_opt.ShadowCap > 0 ? $"-sc{_opt.ShadowCap}" : "")}.png");
    }

    static double Mean(List<double> xs) => xs.Count > 0 ? xs.Average() : 0;
    static double P95(List<double> xs) => xs.Count > 0 ? xs.Order().ElementAt(Math.Min(xs.Count - 1, (int)(xs.Count * 0.95))) : 0;

    public void Finish()
    {
        if (_results.Count == 0) return;
        var inv = CultureInfo.InvariantCulture;
        var eyes = _opt.Stereo ? 2 : 1;
        var size = _opt.EyeSize is { } e ? $"{e.W}x{e.H}" : "window";
        var sb = new StringBuilder();
        sb.AppendLine($"Sojourn bench {DateTime.Now:yyyy-MM-dd HH:mm}, {_renderer.DeviceName}, eye {size} x{eyes}, vertical FOV {_opt.FovDegrees} deg, draw distance {_opt.DrawDistance} m, sky {_opt.Sky ?? "region"}, shadow cap {(_opt.ShadowCap > 0 ? _opt.ShadowCap : "none")}");
        sb.AppendLine();
        sb.AppendLine("| view | frame ms (mean / p95) | GPU ms | CPU ms (mean / p95) | scene | avatars | probes | record | draws | instances |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (name, r) in _results)
            sb.AppendLine(string.Format(inv, "| {0} | {1:F2} / {2:F2} | {3:F2} | {4:F2} / {5:F2} | {6:F2} | {7:F2} | {8:F2} | {9:F2} | {10} | {11} |",
                name, Mean(r.Frame), P95(r.Frame), r.GpuMs, Mean(r.Cpu), P95(r.Cpu), Mean(r.Scene), Mean(r.Avatar), Mean(r.Probe), Mean(r.Record), r.Draws, r.Instances));
        sb.AppendLine();
        var passNames = _results.Values.SelectMany(r => r.Passes.Select(p => p.Pass)).Distinct().ToList();
        sb.AppendLine("GPU ms per pass group (mean per frame):");
        sb.AppendLine();
        sb.AppendLine("| pass | " + string.Join(" | ", _results.Keys) + " |");
        sb.AppendLine("|---|" + string.Concat(_results.Keys.Select(_ => "---|")));
        foreach (var p in passNames)
            sb.AppendLine($"| {p} | " + string.Join(" | ", _results.Values.Select(r =>
                r.Passes.Where(x => x.Pass == p).Select(x => x.Ms.ToString("F3", inv)).FirstOrDefault() ?? "")) + " |");
        var text = sb.ToString();
        Console.WriteLine(text);
        if (_opt.BenchOut is { } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, text + "\n");
        }
    }
}
