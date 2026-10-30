using System.Collections.Concurrent;
using SkiaSharp;
using Sojourn.Core.Rendering;

namespace Sojourn.Session.Assistant;

/// <summary>
/// The snapshot ops (LLViewerWindow saveSnapshot) served by the real-time renderer: the next frame's first eye, as
/// the person sees it (without the panel or pointer), scaled to fit the size asked for and saved as a PNG. The camera
/// is always the person's: a snapshot never moves their view. Requests are taken by the main loop.
/// With showui, what the person sees, menus and all (<see cref="ISeenView"/>; the VR client's SeenShots): the eye as it
/// went to the display with the headset's quad layers composited over it, and a close-up of one if asked.
/// </summary>
public sealed class LiveSnapshots : ISnapshotProvider
{
    readonly ConcurrentQueue<(SnapshotRequest Req, TaskCompletionSource<SnapshotResult> Done)> _queue = new();
    readonly ConcurrentQueue<(SnapshotRequest Req, TaskCompletionSource<SnapshotResult> Done)> _seen = new();

    public Task<SnapshotResult> RenderAsync(SnapshotRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<SnapshotResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        (request.ShowUi ? _seen : _queue).Enqueue((request, tcs));
        // a picture with the menus also composites and may save a close-up on its worker
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(request.ShowUi ? 15 : 8), ct)
            .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : new SnapshotResult(false, "no frame was rendered (minimised, or not in a region yet)"), ct);
    }

    /// <summary>The largest picture someone is waiting for (without the menus), or null: a renderer that draws small
    /// between pictures (Offscreen/OffscreenRenderer.cs) draws the next frames this big.</summary>
    public (int Width, int Height)? WaitingSize
    {
        get
        {
            if (_queue.IsEmpty) return null;
            int w = 0, h = 0;
            foreach (var (req, _) in _queue) (w, h) = (Math.Max(w, req.Width), Math.Max(h, req.Height));
            return (w, h);
        }
    }

    /// <summary>For the frame about to render: a capture callback when someone is waiting for a picture.</summary>
    public Action<byte[], int, int>? Take()
    {
        if (_queue.IsEmpty) return null;
        var waiting = Drain(_queue);
        return (rgba, w, h) =>
        {
            foreach (var (req, done) in waiting)
            {
                try { done.TrySetResult(Save(req, rgba, w, h)); }
                catch (Exception e) { done.TrySetResult(new SnapshotResult(false, e.Message)); }
            }
        };
    }

    /// <summary>Someone is waiting for a picture with the menus (the main loop then asks <see cref="TakeSeen"/>).</summary>
    public bool SeenWaiting => !_seen.IsEmpty;

    /// <summary>For the frame about to render, with what it shows over its first eye: the callback for
    /// FrameRequest.SeenTo when someone is waiting for a picture with the menus.</summary>
    public Action<byte[], int, int>? TakeSeen(ISeenView view)
    {
        if (_seen.IsEmpty) return null;
        var waiting = Drain(_seen);
        return (rgba, w, h) =>
        {
            byte[]? composed = null;
            foreach (var (req, done) in waiting)
            {
                try
                {
                    composed ??= view.Compose(rgba, w, h);
                    var r = Save(req, composed, w, h) with { Layers = view.LayerNames.ToArray(), LeftOut = view.LeftOut };
                    if (req.Close is { } name)
                    {
                        if (view.CloseUp(rgba, w, h, name, out var why) is { } c)
                        {
                            var close = c.Rgba;
                            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(req.FileName))!,
                                Path.GetFileNameWithoutExtension(req.FileName) + "-" + Slug(name) + ".png");
                            Write(path, close, c.W, c.H);
                            r = r with { Close = path };
                        }
                        else r = r with { CloseError = why };
                    }
                    done.TrySetResult(r);
                }
                catch (Exception e) { done.TrySetResult(new SnapshotResult(false, e.Message)); }
            }
        };
    }

    static List<(SnapshotRequest Req, TaskCompletionSource<SnapshotResult> Done)> Drain(ConcurrentQueue<(SnapshotRequest Req, TaskCompletionSource<SnapshotResult> Done)> q)
    {
        var waiting = new List<(SnapshotRequest Req, TaskCompletionSource<SnapshotResult> Done)>();
        while (q.TryDequeue(out var r)) waiting.Add(r);
        return waiting;
    }

    static string Slug(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-') is { Length: > 0 } t ? t : "close";

    static SnapshotResult Save(SnapshotRequest req, byte[] rgba, int w, int h)
    {
        float k = Math.Min(1f, Math.Min((float)Math.Max(64, req.Width) / w, (float)Math.Max(64, req.Height) / h));
        int ow = Math.Max(1, (int)(w * k)), oh = Math.Max(1, (int)(h * k));
        using var src = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, src.GetPixels(), rgba.Length);
        using var scaled = src.Resize(new SKImageInfo(ow, oh, SKColorType.Rgba8888, SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell));
        Write(Path.GetFullPath(req.FileName), scaled);
        return new SnapshotResult(true, null, ow, oh);
    }

    static void Write(string path, byte[] rgba, int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
        Write(path, bmp);
    }

    static void Write(string path, SKBitmap bmp)
    {
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 90);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var f = File.Create(path);
        data.SaveTo(f);
    }
}
