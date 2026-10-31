using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Sojourn.Client;

/// <summary>
/// The render thread (docs/client.md > The frame, two threads). The main thread simulates a frame (window and headset
/// input, the rig, panels, name tags, picks) and posts it here; this thread renders it (the scene, recording, submission,
/// the headset's xrBeginFrame and xrEndFrame) while the main thread goes on to the next.
///
/// The world (the renderer's scenes, avatars and everything picks read) has one owner at a time, handed back and forth
/// in order: a posted frame owns it until its scene is built (FrameRenderer.WorldReleased, before it's recorded), then
/// the main thread does until its next post. So the main thread's next frame starts once the last one's scene is built,
/// and every frame sees the world exactly as a single thread would have left it; only recording, submitting and
/// presenting (which read the frame's own draw list and the GPU's resources) overlap the next frame's simulation.
///
/// One frame waits in the mailbox at most: posting blocks while one is there. A failure on the render thread (a lost
/// GPU) is thrown again on the main thread at its next call. Threaded: false runs each frame on the caller instead (the
/// bench; SOJOURN_RENDER_THREAD=0 for comparing).
/// </summary>
public sealed class FramePipe : IDisposable
{
    readonly Thread? _thread;
    readonly object _gate = new();
    Action? _next;
    long _posted, _taken, _released;
    bool _busy, _stop;
    ExceptionDispatchInfo? _failed;
    long _failedAt;
    // SOJOURN_RENDER_STRESS=1: random delays on both sides of the hand-over, for shaking out ordering assumptions
    readonly bool _stress = Environment.GetEnvironmentVariable("SOJOURN_RENDER_STRESS") == "1";
    readonly Random _rng = new();
    // SOJOURN_FAKE_HELD=1 (with SOJOURN_FAKE_DEVICE_LOST): a failure reaches the main thread's hand-over a minute late, as
    // when Meta's xrWaitFrame held it after a driver reset, so DeviceLoss's watch can be seen restarting meanwhile
    readonly bool _fakeHeld = Environment.GetEnvironmentVariable("SOJOURN_FAKE_HELD") == "1";

    public bool Threaded => _thread != null;
    /// <summary>The thread that owns the world now (the main thread between posts, the render thread from a frame's
    /// start until its scene is built); for checks (FrameRenderer.OwnsWorld).</summary>
    public int WorldOwner { get; private set; } = Environment.CurrentManagedThreadId;
    /// <summary>Milliseconds the main thread waited for the world in the last <see cref="TakeWorld"/> and posting (the
    /// render thread still building the last frame's scene, or the mailbox full).</summary>
    public double WaitedMs { get; private set; }

    /// <summary>The render thread's failure and when (Stopwatch ticks), null while it renders; for DeviceLoss's watch,
    /// which restarts when the main thread is held where the failure can't reach it.</summary>
    public (Exception Error, long At)? Failure
    {
        get { lock (_gate) return _failed is { } f ? (f.SourceException, _failedAt) : null; }
    }

    public FramePipe(bool threaded)
    {
        if (!threaded) return;
        _thread = new Thread(Run) { Name = "render", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Hand a frame to the render thread (or run it now, unthreaded). Blocks while another waits.</summary>
    public void Post(Action frame)
    {
        if (_thread == null)
        {
            frame();
            return;
        }
        long t0 = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            while (_next != null && _failed == null) Monitor.Wait(_gate);
            HandFailure();
            _next = frame;
            _posted++;
            WorldOwner = -1;   // the frame's now, though the render thread hasn't taken it yet
            Monitor.PulseAll(_gate);
        }
        WaitedMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>The main thread's turn with the world: once every frame posted has built its scene.</summary>
    public void TakeWorld()
    {
        WaitedMs = 0;
        if (_thread == null) return;
        long t0 = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            while (_released < _posted && _failed == null) Monitor.Wait(_gate);
            HandFailure();
            WorldOwner = Environment.CurrentManagedThreadId;
        }
        WaitedMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (_stress && _rng.Next(4) == 0) Thread.Sleep(_rng.Next(3));
    }

    /// <summary>The main thread's hand-over meets a render-thread failure: thrown here. Under the gate.</summary>
    void HandFailure()
    {
        if (_failed == null) return;
        if (_fakeHeld) Monitor.Wait(_gate, 60_000);
        _failed.Throw();
    }

    /// <summary>Called on the render thread when the frame being rendered is done with the world (its scene built).
    /// Also called at every frame's end, for frames that never got that far.</summary>
    public void ReleaseWorld()
    {
        if (_thread == null || Environment.CurrentManagedThreadId != _thread.ManagedThreadId) return;
        if (_stress && _rng.Next(4) == 0) Thread.Sleep(_rng.Next(3));
        lock (_gate)
        {
            if (_released >= _taken) return;
            _released = _taken;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Wait until nothing is rendering or waiting to (before touching the GPU from the main thread: a
    /// swapchain or session torn down, waiting for the device to idle). Throws a render-thread failure.</summary>
    public void Drain() => Drain(Timeout.InfiniteTimeSpan);

    /// <summary>As <see cref="Drain()"/>, giving up after <paramref name="within"/> (a render thread held in a call on a lost
    /// device); false if it did.</summary>
    public bool Drain(TimeSpan within)
    {
        if (_thread == null) return true;
        long t0 = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            while ((_next != null || _busy) && _failed == null)
            {
                if (within == Timeout.InfiniteTimeSpan) { Monitor.Wait(_gate); continue; }
                var left = within - Stopwatch.GetElapsedTime(t0);
                if (left <= TimeSpan.Zero) return false;
                Monitor.Wait(_gate, left);
            }
            _failed?.Throw();
            if (_released >= _posted) WorldOwner = Environment.CurrentManagedThreadId;
        }
        return true;
    }

    void Run()
    {
        while (true)
        {
            Action frame;
            lock (_gate)
            {
                while (_next == null && !_stop) Monitor.Wait(_gate);
                if (_stop) return;
                frame = _next!;
                _next = null;
                _busy = true;
                _taken++;
                WorldOwner = Environment.CurrentManagedThreadId;
                Monitor.PulseAll(_gate);
            }
            try
            {
                frame();
                if (_stress && _rng.Next(4) == 0) Thread.Sleep(_rng.Next(4));
            }
            catch (Exception e)
            {
                lock (_gate)
                    if (_failed == null)
                    {
                        _failed = ExceptionDispatchInfo.Capture(e);
                        _failedAt = Stopwatch.GetTimestamp();
                    }
            }
            ReleaseWorld();
            lock (_gate)
            {
                _busy = false;
                Monitor.PulseAll(_gate);
                if (_failed != null) return;   // the main thread rethrows it; nothing more renders
            }
        }
    }

    public void Dispose()
    {
        if (_thread == null) return;
        lock (_gate)
        {
            _stop = true;
            Monitor.PulseAll(_gate);
        }
        _thread.Join(5000);
    }
}
