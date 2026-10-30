using System.Diagnostics;
using Sojourn.Client.Xr;
using Sojourn.Render.Gpu;
using Sojourn.Render.Realtime;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Sojourn.Client.Window;

/// <summary>The headset as the frame's target, with the eyes mirrored side by side into the window as well, and the
/// headset's menus (its quad layers, which the compositor puts over the lenses) drawn over them there as the headset
/// shows them (Render's LayerMirror), unless the window is blank (incognito) or <see cref="Menus"/> is off. The window
/// never holds the headset up: its swapchain should present without vsync (mailbox). A split frame (the headset's
/// overlap) gets its window copies in the output part, from each eye's own image, and its present after the headset's
/// frame has ended, on the headset's frame-end thread.</summary>
public sealed class MirrorTarget(XrSession headset, Presenter window, LayerMirror layers) : IFrameTarget
{
    bool _window;

    /// <summary>Mirror the eyes into the window (a diagnostic can turn it off: the frame's one submission then no longer
    /// waits on the window's image).</summary>
    public bool WindowOn { get; set; } = true;
    /// <summary>The headset's menus over the eyes in the window (the mirror_menus setting). Main thread; from the next
    /// frame.</summary>
    public bool Menus { get; set; } = true;
    double _headsetMs, _windowMs, _headsetPresentMs, _windowPresentMs, _lastWindowPresentMs;
    int _frames;

    public bool Acquire(int slot, out Semaphore ready, out Semaphore done)
    {
        _window = false;
        headset.Mirroring = null;
        long t0 = Stopwatch.GetTimestamp();
        if (!headset.Acquire(slot, out ready, out done)) return false;
        long t1 = Stopwatch.GetTimestamp();
        _window = WindowOn && window.Acquire(slot, out ready, out done);
        if (!_window) ready = done = default;
        // (before the eyes: the layers copy their pictures for it as the headset's are recorded)
        if (_window && Menus && window.ShowsView) headset.Mirroring = layers;
        lock (this)
        {
            _headsetMs += Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
            _windowMs += Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
            _frames++;
        }
        return true;
    }

    /// <summary>Mean milliseconds a frame spent getting and handing back its headset image (with overlap: waiting for
    /// the last frame's end, acquiring and handing over; without, xrEndFrame included) and its window image, since the
    /// last call. Any thread (the frames are the render thread's).</summary>
    public (double Headset, double Window) TakeTargetTimes()
    {
        lock (this) return Take();
    }

    (double Headset, double Window) Take()
    {
        var r = _frames > 0 ? ((_headsetMs + _headsetPresentMs) / _frames, (_windowMs + _windowPresentMs) / _frames) : (0, 0);
        _headsetMs = _windowMs = _headsetPresentMs = _windowPresentMs = 0;
        _frames = 0;
        return r;
    }

    public void RecordPresent(CommandBuffer cmd, int eye, int eyeCount, GpuImage output)
    {
        headset.RecordPresent(cmd, eye, eyeCount, output);
        if (_window && !headset.Splitting) window.RecordPresent(cmd, eye, eyeCount, output);
    }

    public void RecordDepth(CommandBuffer cmd, int eye, GpuImage depth) => headset.RecordDepth(cmd, eye, depth);

    public HandsTarget? Hands(CommandBuffer cmd, int eye) => headset.Hands(cmd, eye);

    public DepthTarget? DepthOut(int eye) => headset.DepthOut(eye);

    public bool TrySplit() => headset.TrySplit();

    public void SubmitScene(Action submit)
    {
        long t0 = Stopwatch.GetTimestamp();
        headset.SubmitScene(submit);
        lock (this) _headsetMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    public void RecordOutput(CommandBuffer cmd)
    {
        headset.RecordOutput(cmd);
        if (!_window) return;
        int n = headset.SceneEyes;
        for (int eye = 0; eye < n; eye++)
            if (headset.SceneOutput(eye) is { } output) window.RecordPresent(cmd, eye, n, output);
    }

    /// <summary>The menus over both eyes in the window, once the eyes and their hands are recorded (a loading frame,
    /// one picture stretched over the window, gets none).</summary>
    public void RecordFinish(CommandBuffer cmd)
    {
        if (_window && headset.Mirroring != null && window.Eyes == 2
            && headset.MirrorLayers(window.Cell(0), window.Cell(1), out var eyes, out var quads, out var hands))
        {
            window.ToAttachment(cmd);
            layers.Draw(cmd, window.View, Presenter.SurfaceFormat, new Extent2D(window.Width, window.Height), eyes, quads, hands);
        }
        headset.Mirroring = null;
        layers.EndFrame();
        if (_window) window.RecordFinish(cmd);
    }

    public void Present()
    {
        long t0 = Stopwatch.GetTimestamp();
        _lastWindowPresentMs = 0;
        headset.Present(_window ? WindowPresent : null);
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        lock (this) _headsetPresentMs += headset.Overlap ? ms : ms - _lastWindowPresentMs;   // (without overlap the window's present ran inside)
    }

    void WindowPresent()
    {
        long t0 = Stopwatch.GetTimestamp();
        window.Present();
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _lastWindowPresentMs = ms;
        lock (this) _windowPresentMs += ms;
    }
}
