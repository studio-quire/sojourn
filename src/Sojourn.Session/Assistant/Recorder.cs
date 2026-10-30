using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using SkiaSharp;
using Sojourn.Render.Realtime;

namespace Sojourn.Session.Assistant;

/// <summary>
/// Short videos of the person's view, for the assistant (SojournCapture record): the first eye as they see it, without
/// the panel and the rest of the overlay (as the assistant's pictures are), scaled down on the GPU
/// (FrameRenderer.Video.cs) and encoded by ffmpeg to an MP4 in the person's Videos folder (Videos\Sojourn), where they
/// can open it. No sound: voice chat shares the mix, and other people's voices aren't the assistant's to record.
/// Claude Opus 5.5, 2026-10-26, for capturing flicker the person sees in the headset.
///
/// The video runs at a constant rate (fps), in real time: each slot of 1/fps gets the first frame rendered in it, and
/// slots no frame reached (the client rendering slower than fps) repeat the frame before, so the video plays at the
/// speed it happened. A flicker that lasts one frame needs fps at the render rate (72-90 in the headset) to be sure of
/// being in it. ffmpeg is the one on PATH (or SOJOURN_FFMPEG); the first encoder that works of NVENC (the GPU's own
/// encoder, apart from its 3D work), Media Foundation's and libx264.
/// </summary>
public sealed class Recorder
{
    public static string VideosDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Sojourn");
    public const int MaxSeconds = 120;
    const int MaxBacklog = 24;   // frames taken but not yet written: past this, slots repeat rather than memory grows

    readonly Action<string> _log;
    volatile Recording? _rec;
    string[]? _encoder;
    JsonObject? _last;

    public Recorder(Action<string> log) => _log = log;

    public bool Busy => _rec != null;
    /// <summary>The last recording's result, once it's saved.</summary>
    public JsonObject? Last => _last;

    sealed class Recording
    {
        public required string Path;
        public required double Seconds;
        public required int Fps;
        public required int MaxWidth;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public long LastSlot = -1;
        public int Backlog, Rendered, Skipped;
        public volatile bool Ended;
        public int Width, Height;     // fixed by the first frame
        public readonly ConcurrentQueue<(long Slot, TaskCompletionSource<(byte[] Px, int W, int H)> Frame)> Taken = new();
        public readonly SemaphoreSlim Ready = new(0);
        public readonly TaskCompletionSource<JsonObject> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Encoder = "";
    }

    /// <summary>Main thread, every frame: a capture for this frame when a recording wants one.</summary>
    public VideoCapture? Take()
    {
        var r = _rec;
        if (r is null || r.Ended) return null;
        double t = r.Clock.Elapsed.TotalSeconds;
        r.Rendered++;
        if (t >= r.Seconds) { End(r); return null; }
        long slot = (long)Math.Floor(t * r.Fps);
        if (slot <= r.LastSlot) return null;
        r.LastSlot = slot;
        if (Volatile.Read(ref r.Backlog) >= MaxBacklog) { r.Skipped++; return null; }
        Interlocked.Increment(ref r.Backlog);
        var tcs = new TaskCompletionSource<(byte[], int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Taken.Enqueue((slot, tcs));
        r.Ready.Release();
        // once the first frame has fixed the size, every frame is blitted to it (the headset's eyes change size with load)
        return new VideoCapture(r.Width > 0 ? r.Width : r.MaxWidth, (px, w, h) => tcs.TrySetResult((px, w, h)), r.Height);
    }

    void End(Recording r)
    {
        if (r.Ended) return;
        r.Ended = true;
        r.Ready.Release();
    }

    /// <summary>Starts one (one at a time); the task completes once the file is written.</summary>
    public async Task<(string Path, Task<JsonObject> Done)> StartAsync(double seconds, int fps, int maxWidth, string? name, CancellationToken ct)
    {
        if (_rec != null) throw new Sojourn.Core.Leap.LeapError("already recording; stop that one first");
        var encoder = _encoder ??= await FindEncoderAsync(ct) ?? throw new Sojourn.Core.Leap.LeapError(
            "recording needs ffmpeg, and none that works was found on PATH (winget install Gyan.FFmpeg, or SOJOURN_FFMPEG=its path)");
        Directory.CreateDirectory(VideosDir);
        var path = Unique(System.IO.Path.Combine(VideosDir, FileName(name, ".mp4")));
        var r = new Recording
        {
            Path = path, Seconds = Math.Clamp(seconds, 1, MaxSeconds), Fps = Math.Clamp(fps, 1, 120),
            MaxWidth = Math.Clamp(maxWidth, 160, 3840) & ~1,
        };
        r.Encoder = encoder[0];
        _rec = r;
        _ = Task.Run(() => Write(r, encoder));
        // ended by time even if no frames are rendered (minimised, or between regions)
        _ = Task.Delay(TimeSpan.FromSeconds(r.Seconds + 3)).ContinueWith(_ => End(r));
        _log($"recording {r.Seconds:0.#} s at {r.Fps} fps to {path} ({r.Encoder})");
        return (path, r.Done.Task);
    }

    public void Stop()
    {
        if (_rec is { } r) End(r);
    }

    public double? SecondsLeft => _rec is { } r ? Math.Max(0, r.Seconds - r.Clock.Elapsed.TotalSeconds) : null;
    public string? CurrentPath => _rec?.Path;

    void Write(Recording r, string[] encoder)
    {
        Process? ff = null;
        Stream? input = null;
        long written = 0, repeated = 0, lastSlot = -1;
        byte[]? lastFrame = null;
        string? error = null;
        try
        {
            while (true)
            {
                r.Ready.Wait(TimeSpan.FromSeconds(1));
                while (r.Taken.TryDequeue(out var t))
                {
                    bool arrived = t.Frame.Task.Wait(TimeSpan.FromSeconds(3));   // a frame the GPU never finished is skipped
                    Interlocked.Decrement(ref r.Backlog);
                    if (!arrived) continue;
                    var f = t.Frame.Task.Result;
                    if (ff is null)
                    {
                        r.Width = f.W;
                        r.Height = f.H;
                        (ff, input) = StartFfmpeg(encoder, f.W, f.H, r.Fps, r.Path);
                    }
                    var px = f.W == r.Width && f.H == r.Height ? f.Px : Resize(f.Px, f.W, f.H, r.Width, r.Height);
                    // slots no frame reached repeat the one before, so the video keeps real time
                    long gap = lastSlot < 0 ? 0 : t.Slot - lastSlot - 1;
                    for (long i = 0; i < gap && lastFrame != null; i++) { input!.Write(lastFrame); repeated++; }
                    input!.Write(px);
                    written += gap + 1;
                    lastSlot = t.Slot;
                    lastFrame = px;
                }
                if (r.Ended && r.Taken.IsEmpty) break;
            }
            // to the end of its time, if the last frames came early
            long total = (long)Math.Round(r.Seconds * r.Fps);
            if (lastFrame != null && r.Clock.Elapsed.TotalSeconds >= r.Seconds)
                for (long i = lastSlot + 1; i < total; i++) { input!.Write(lastFrame); repeated++; written++; }
        }
        catch (Exception e) { error = e.Message; }
        try
        {
            input?.Close();
            if (ff is not null && !ff.WaitForExit(60_000)) { ff.Kill(); error ??= "ffmpeg didn't finish"; }
            if (ff is not null && ff.ExitCode != 0) error ??= $"ffmpeg failed ({ff.ExitCode}): {ff.StandardError.ReadToEnd().Trim()}";
        }
        catch (Exception e) { error ??= e.Message; }
        double secs = r.Clock.Elapsed.TotalSeconds;
        var o = new JsonObject
        {
            ["path"] = r.Path, ["seconds"] = Math.Round(Math.Min(secs, r.Seconds), 1), ["fps"] = r.Fps,
            ["size"] = $"{r.Width}x{r.Height}", ["frames"] = written, ["repeated"] = repeated,
            ["rendered_fps"] = Math.Round(r.Rendered / Math.Max(0.1, Math.Min(secs, r.Seconds)), 1), ["encoder"] = r.Encoder,
        };
        if (r.Skipped > 0) o["skipped"] = r.Skipped;   // the encoder fell behind: those slots repeat the frame before
        if (written == 0) error ??= "no frames were rendered while recording (minimised, or not in a region)";
        if (error != null)
        {
            o["error"] = error;
            try { if (written == 0 && File.Exists(r.Path)) File.Delete(r.Path); } catch { }
        }
        _log($"recording {(error == null ? "saved" : "failed")}: {o.ToJsonString()}");
        _last = o;
        _rec = null;
        r.Done.TrySetResult(o);
    }

    static (Process, Stream) StartFfmpeg(string[] encoder, int w, int h, int fps, string path)
    {
        var psi = new ProcessStartInfo(Ffmpeg)
        {
            RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{w}x{h}",
                                  "-framerate", fps.ToString(), "-i", "-" })
            psi.ArgumentList.Add(a);
        foreach (var a in encoder[1..]) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-pix_fmt", "yuv420p", "-movflags", "+faststart", path }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg didn't start");
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        return (p, p.StandardInput.BaseStream);
    }

    static string Ffmpeg => Environment.GetEnvironmentVariable("SOJOURN_FFMPEG") is { Length: > 0 } f ? f : "ffmpeg";

    static readonly string[][] Encoders =
    {
        new[] { "nvenc", "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "21" },
        new[] { "media foundation", "-c:v", "h264_mf", "-b:v", "16M" },
        new[] { "libx264", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20" },
    };

    /// <summary>The first encoder that encodes a few frames here (NVENC needs the NVIDIA driver; the rest are software).</summary>
    static async Task<string[]?> FindEncoderAsync(CancellationToken ct)
    {
        foreach (var enc in Encoders)
        {
            try
            {
                var psi = new ProcessStartInfo(Ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=gray:s=256x256:r=30:d=0.2" }) psi.ArgumentList.Add(a);
                foreach (var a in enc[1..]) psi.ArgumentList.Add(a);
                foreach (var a in new[] { "-pix_fmt", "yuv420p", "-f", "null", "-" }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p is null) continue;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                await p.WaitForExitAsync(cts.Token);
                if (p.ExitCode == 0) return enc;
            }
            catch (System.ComponentModel.Win32Exception) { return null; }   // no ffmpeg at all
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return null;
    }

    static byte[] Resize(byte[] px, int w, int h, int ow, int oh)
    {
        using var src = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(px, 0, src.GetPixels(), px.Length);
        using var dst = src.Resize(new SKImageInfo(ow, oh, SKColorType.Rgba8888, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear));
        return dst.Bytes;
    }

    /// <summary>"Sojourn 2026-10-26 14-03-22 - the flicker.mp4": the time, and the caller's few words if any.</summary>
    public static string FileName(string? name, string ext)
    {
        var words = new string((name ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_').ToArray()).Trim();
        if (words.Length > 60) words = words[..60].Trim();
        return $"Sojourn {DateTime.Now:yyyy-MM-dd HH-mm-ss}{(words.Length > 0 ? " - " + words : "")}{ext}";
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var stem = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileNameWithoutExtension(path));
        var ext = System.IO.Path.GetExtension(path);
        for (int i = 2; ; i++) if (!File.Exists($"{stem} ({i}){ext}")) return $"{stem} ({i}){ext}";
    }
}
