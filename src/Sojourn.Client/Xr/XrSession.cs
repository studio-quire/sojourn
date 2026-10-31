using System.Numerics;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.FB;
using Sojourn.Render.Gpu;
using Sojourn.Render.Realtime;
using VkFormat = Silk.NET.Vulkan.Format;
using VkImage = Silk.NET.Vulkan.Image;
using VkImageLayout = Silk.NET.Vulkan.ImageLayout;
using VkImageAspectFlags = Silk.NET.Vulkan.ImageAspectFlags;
using VkCommandBuffer = Silk.NET.Vulkan.CommandBuffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;
using Quaternion = System.Numerics.Quaternion;
using Action = System.Action;

namespace Sojourn.Client.Xr;

// OpenXR's Session, not the Sojourn.Session namespace (which a bare name in Sojourn.* finds first)
using Session = Silk.NET.OpenXR.Session;

/// <summary>One eye this frame, in the play space (OpenXR: x right, y up, z back, metres; floor at y = 0 when
/// <see cref="XrSession.FloorLevel"/>).</summary>
public readonly record struct XrEye(Vector3 Position, Quaternion Orientation, float TanLeft, float TanRight, float TanUp, float TanDown);

/// <summary>What a frame needs from the headset: when it will be shown, the eyes, and the controllers. Filled on the
/// main thread (<see cref="XrSession.WaitFrame"/>, <see cref="XrSession.Commit"/>), shown by the render thread
/// (<see cref="XrSession.BeginFrame"/> to the frame's end), which submits it with the same eye poses.</summary>
public sealed class XrFrame
{
    public long DisplayTime;          // XrTime, nanoseconds
    public long? DisplayAt;           // the same as Stopwatch ticks, when the runtime can convert
    public double Period;             // seconds between frames
    public bool ShouldRender;
    public bool Tracked;              // the eyes' poses are valid
    public XrEye[] Eyes = new XrEye[2];
    public XrControllers Input;
    /// <summary>The size its eyes are rendered and submitted at.</summary>
    public int Width, Height;
    // the eyes as located, submitted with the frame's projection layer
    internal readonly CompositionLayerProjectionView[] Views = new CompositionLayerProjectionView[2];
    // what the frame shows besides the eyes (Commit): the quad layers in XrSession's fixed order, the assistant's cards,
    // and the flags the main thread set for it
    internal QuadLayer.Spec[] Layers = [];
    internal List<(string Key, QuadLayer.Spec Spec)> Cards = new();
    internal bool ViewCut, DepthLayers, PanelOnTop;
    // when xrWaitFrame returned, and the head as located then for its display time (the pacing line's pose checks)
    internal long WokeAt;
    internal bool BeganTracked;
    internal Vector3 BeganPos;
    internal Quaternion BeganRot;
}

/// <summary>
/// The headset session: its state machine, the play space, one two-layer swapchain (a layer per eye) and the frame
/// loop (wait, begin, locate the eyes, end with one projection layer). As an <see cref="IFrameTarget"/> it takes the
/// renderer's finished eye images: each is copied into its layer of the acquired swapchain image.
/// Two threads (Sojourn.Client/FramePipe.cs): the main thread waits for each frame (<see cref="WaitFrame"/>: xrWaitFrame,
/// the eyes and controllers located at its display time), sets what it shows (the layers, the flags) and commits that to
/// it (<see cref="Commit"/>); the render thread begins it (xrBeginFrame), renders, and ends it with the eye poses it was
/// located with. Events and the session's state are the main thread's; it drains the render thread before ending the
/// session. With <see cref="Overlap"/> a third thread ends each frame (frame end: GPU wait, release, xrEndFrame) and
/// begins the next if the render thread has taken it (else the render thread does); the frame-end thread holds a frame from <see cref="Present"/> to
/// its end, the render thread from <see cref="BeginFrame"/> to Present, and a split frame's scene part touches only its
/// own state (_current, _scene*, the stage images), never the frame being ended.
/// </summary>
public sealed unsafe class XrSession : IFrameTarget, IDisposable
{
    readonly XrRuntime _rt;
    readonly XR _xr;
    readonly VulkanContext _vk;
    readonly Action<string> _log;
    readonly Session _session;
    /// <summary>The OpenXR session (for what other classes ask the runtime about it, XrLensMask).</summary>
    public Session Handle => _session;
    Space _space;
    Swapchain _swapchain;
    VkImage[] _images = [];
    readonly FBDisplayRefreshRate? _refresh;
    readonly float _wantHz;
    SessionState _state = SessionState.Unknown;
    bool _running, _frameOpen, _imageHeld;
    uint _imageIndex;
    // the eyes' depth beside their colour (XR_KHR_composition_layer_depth), so the compositor can reproject a frame
    // that comes late by where the head moved as well as how it turned: without it a missed frame shows the old view
    // turned but not moved, and the world seems to shift with the head
    Swapchain _depthChain;
    VkImage[] _depthImages = [];
    // a view per image and eye, for the menus' footprints carved into it with menus over walls (Overlay.Walls.cs)
    Silk.NET.Vulkan.ImageView[] _depthViews = [];
    uint _depthIndex;
    bool _depthHeld;
    int _depthWritten;
    readonly CompositionLayerDepthInfoKHR[] _depthInfo = new CompositionLayerDepthInfoKHR[2];
    // the person's hands over the menus (Sojourn.Render's Overlay.Hands.cs): a second projection layer over the quads,
    // from a swapchain of its own (a layer per eye, the eyes' size) that the renderer fills with the hands where they're
    // in front of a menu, premultiplied and clear elsewhere, blended as the quads are; same poses, time and depth as the
    // eyes' layer. Made on first use; acquired when the renderer first asks in a frame. Which eyes got a real image and
    // their hands this frame.
    Swapchain _handChain;
    VkImage[] _handImages = [];
    Silk.NET.Vulkan.ImageView[] _handViews = [];
    uint _handIndex;
    bool _handHeld, _handsLogged;
    int _eyesCopied, _handsWritten;
    /// <summary>Draw the hands over the menus (SOJOURN_HANDS_OVER_MENUS=0 turns it off, for comparing).</summary>
    public bool HandsOverMenus { get; set; } = Environment.GetEnvironmentVariable("SOJOURN_HANDS_OVER_MENUS") != "0";
    /// <summary>Hand the eyes' depth to the compositor (when the runtime offers depth layers). Off, a late frame is
    /// reprojected by rotation only: a switch for telling the runtime's depth-based reprojection and frame synthesis
    /// apart from our own frames in the headset. Main thread, from the next frame committed.</summary>
    public bool DepthLayers { get; set; } = true;
    public bool HasDepthLayers => _depthChain.Handle != 0;
    XrFrame? _frame;   // the render thread's frame, begun and not yet ended

    // frame pacing, for the console: each frame's display time step from the last, in display periods (1 on time; 2
    // or more, slots the runtime skipped: steadily 2 is its half rate, where Meta's runtime synthesises every other
    // frame; 0, the same slot again), frames ended after their own display time, and how far ahead xrWaitFrame woke us
    long _lastDisplayTime;
    readonly int[] _steps = new int[4];   // repeated, 1, 2, 3+
    int _endedLate, _paced, _noImage, _voids, _cuts, _stepChanges;
    long _lastStep;
    double _aheadMs, _aheadMin = double.MaxValue, _aheadMax = double.MinValue;
    readonly object _pacingLock = new();   // counted on both threads (the frames are waited on the main thread, ended on the render thread)

    /// <summary>How far ahead of its predicted display time the runtime woke the last frame waited for, ms (frames.csv's
    /// last column).</summary>
    public double WakeLeadMs { get; private set; }

    /// <summary>Wait for the queue to finish the frame's GPU work before handing its swapchain images back. Without it,
    /// under GPU load the whole view shook finely in the headset, as if older frames were shown at newer head poses
    /// (the jitter of 2026-10-26/27; the user confirmed this removes it): likely Meta's compositor reading an image
    /// before our writes landed, not proven. It cost the CPU/GPU overlap until the wait moved to a thread of its own
    /// (<see cref="Overlap"/>; docs/vr.md > The rig, late frames).</summary>
    public bool WaitGpuBeforeRelease { get; set; } = true;

    /// <summary>Overlap (docs/vr.md > The rig > Overlap): each frame is ended on a thread of its own (wait for its GPU
    /// work, give its images back, xrEndFrame, the mirror window's present), so the render thread records the next frame
    /// meanwhile; the renderer submits the next frame's scene once that's done (<see cref="SubmitScene"/>, xrBeginFrame
    /// first), and writes the swapchain images after (<see cref="RecordOutput"/>). Nothing of a frame goes back before its
    /// GPU work is finished, and the queue is idle when it does, as without. SOJOURN_XR_OVERLAP=0: one thread does it
    /// all in turn, as before (for diagnosis).</summary>
    public bool Overlap { get; } = Environment.GetEnvironmentVariable("SOJOURN_XR_OVERLAP") != "0";
    // overlap: the render thread's frame (waited for and committed; begun once the last frame has ended; null once it's
    // gone to the frame-end thread or been ended), whether it's begun, and whether it's recorded split (TrySplit)
    XrFrame? _current;
    bool _begun, _split;
    // a split frame's scene part: each eye's finished image (the renderer's, one per eye), and which eyes got a real
    // image, their depth copied into our own image, their hands drawn into our own image
    readonly GpuImage?[] _sceneOut = new GpuImage?[2];
    int _sceneOutCount, _sceneEyes, _sceneDepth, _sceneHands;
    GpuImage? _stageDepth, _stageHands;   // two layers each, the swapchains' size
    Silk.NET.Vulkan.ImageView[] _stageDepthViews = [], _stageHandViews = [];
    // the frame-end thread: the frame handed to it (end, then after), what the render thread wants run once it's ended
    // (the next frame begun and its scene submitted), and a failure for the render thread to throw
    Thread? _ender;
    readonly object _endLock = new();
    Action? _endJob, _endAfter, _endThen;
    bool _endBusy, _endStop;
    System.Runtime.ExceptionServices.ExceptionDispatchInfo? _endFailed;
    // for the pacing line: frames begun, split, whose scene was ready before the last frame had ended; the render
    // thread's wait for those ends; the frame-end thread's wait on the GPU and its release and xrEndFrame
    int _olFrames, _olSplit, _olAhead, _olEnds;
    int _depthHeldBack;   // frames whose depth wasn't handed over because menus overlapped (EndFrame)
    double _olWaitMs, _olGpuMs, _olEndMs;

    // how long xrWaitFrame held us, and from its return to xrEndFrame (the frame's own span)
    double _waitMs, _spanMs;
    int _spans;
    // the head's pose as rendered against what the runtime later says it was at that display time (the error the
    // compositor has to correct; it can for rotation, for position only with depth), and against its prediction for the
    // same time again at xrEndFrame (how much the prediction moved while we worked: what re-locating late would gain)
    Space _viewSpace;
    readonly List<(long Time, long At, Vector3 Pos, Quaternion Rot)> _rendered = new();
    readonly List<double> _errDeg = new(), _errMm = new(), _driftDeg = new(), _driftMm = new();
    MetaMetrics? _metrics;

    /// <summary>The pacing since the last call, as a line for the console (null without frames).</summary>
    public string? TakePacing()
    {
        lock (_pacingLock)
        {
            int n = _steps.Sum();
            if (n == 0) return null;
            string Pc(int i) => $"{100.0 * _steps[i] / n:F0}%";
            // frames the runtime wanted that showed no scene (ended without an image) or a grey void (no eye image to
            // copy): either is a flash in the headset; and cuts, frames where the rig moved the view to a new spot at once
            var line = $"display steps 1: {Pc(1)}, 2: {Pc(2)}, 3+: {Pc(3)}, repeated {_steps[0]}, changed {_stepChanges}; ended after display {_endedLate}; " +
                       $"woken {_aheadMs / Math.Max(1, _paced):F1} ms ahead ({(_paced > 0 ? _aheadMin : 0):F0}-{(_paced > 0 ? _aheadMax : 0):F0}); without the scene {_noImage}, void {_voids}, cut {_cuts}; " +
                       $"waited {_waitMs / Math.Max(1, _spans):F1} ms, wake to end {_spanMs / Math.Max(1, _spans):F1} ms; " +
                       $"depth {(DepthLayers && HasDepthLayers ? "on" : "off")}" +
                       (_depthHeldBack > 0 ? $" (held back in {_depthHeldBack}: menus overlapping)" : "") +
                       (WaitGpuBeforeRelease ? "" : "; NOT waiting on the GPU before release") +
                       OverlapLine() +
                       (LagHook.On ? $"; test lag {LagHook.Cpu} ms" : "") + PoseLine();
            Array.Clear(_steps);
            _endedLate = _paced = _noImage = _voids = _cuts = _spans = _stepChanges = _depthHeldBack = 0;
            _aheadMs = _waitMs = _spanMs = 0;
            (_aheadMin, _aheadMax) = (double.MaxValue, double.MinValue);
            return line;
        }
    }

    /// <summary>The overlap's part of the pacing line (under the pacing lock): of the frames begun, how many were split
    /// and how many had their scene recorded before the last frame had ended (overlapping its GPU work), the render
    /// thread's mean wait for those ends, and the frame-end thread's mean wait on the GPU and release-to-xrEndFrame.</summary>
    string OverlapLine()
    {
        if (!Overlap) return "; frames in turn (SOJOURN_XR_OVERLAP=0)";
        var s = $"; overlap: {_olSplit} of {_olFrames} frames split, {_olAhead} recorded before the last had ended, waited {_olWaitMs / Math.Max(1, _olFrames):F1} ms for its end; " +
                $"ending: GPU wait {_olGpuMs / Math.Max(1, _olEnds):F1} ms, release to xrEndFrame {_olEndMs / Math.Max(1, _olEnds):F2} ms";
        _olFrames = _olSplit = _olAhead = _olEnds = 0;
        _olWaitMs = _olGpuMs = _olEndMs = 0;
        return s;
    }

    /// <summary>Hand the frame just submitted to the frame-end thread: end (wait for the GPU, release, xrEndFrame), then
    /// after (the mirror window's present). Render thread, the thread idle (<see cref="WhenEnded"/>).</summary>
    void HandOver(Action end, Action? after)
    {
        if (_ender == null)
        {
            _ender = new Thread(EndLoop) { Name = "frame end", IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _ender.Start();
        }
        lock (_endLock)
        {
            _endJob = end;
            _endAfter = after;
            _endBusy = true;
            Monitor.PulseAll(_endLock);
        }
    }

    /// <summary>Once the frame-end thread has ended the last frame, run then (null: nothing) with the queue held: here,
    /// or on that thread right after xrEndFrame when it's still busy (no hop between threads in the GPU's idle gap).
    /// Returns once it has run; throws what failed there. Render thread (and the main thread before ending the
    /// session, the render thread drained).</summary>
    void WhenEnded(Action? then)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool ahead = false;
        lock (_endLock)
        {
            if (_endBusy && then != null && _endFailed == null)
            {
                _endThen = then;
                then = null;
                ahead = true;
            }
            while (_endBusy) Monitor.Wait(_endLock);
            if (_endFailed is { } failed)
            {
                _endFailed = null;
                failed.Throw();
            }
        }
        if (then != null) lock (_vk.QueueLock) then();
        lock (_pacingLock)
        {
            _olWaitMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (ahead) _olAhead++;
        }
    }

    void EndLoop()
    {
        while (true)
        {
            Action end;
            Action? after;
            lock (_endLock)
            {
                while (!_endBusy && !_endStop) Monitor.Wait(_endLock);
                if (!_endBusy) return;
                (end, after) = (_endJob!, _endAfter);
            }
            Run(end);
            Run(BeginTaken);   // the render thread's next frame begun at once, so the main thread's xrWaitFrame goes on
            RunThen();   // its scene submitted, if it's waiting: before the window's present
            if (after != null) Run(after);
            Run(BeginTaken);
            while (true)
            {
                lock (_endLock)
                {
                    if (_endThen == null || _endFailed != null)
                    {
                        _endThen = null;
                        _endJob = _endAfter = null;
                        _endBusy = false;
                        Monitor.PulseAll(_endLock);
                        break;
                    }
                }
                RunThen();
            }
        }

        void Run(Action a)
        {
            if (_endFailed != null) return;
            try { lock (_vk.QueueLock) a(); }
            catch (Exception e) { lock (_endLock) _endFailed ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
        }
        void RunThen()
        {
            Action? then;
            lock (_endLock) { then = _endThen; _endThen = null; }
            if (then != null) Run(then);
        }
    }

    /// <summary>Frame-end thread, the last frame just ended: begin the render thread's next one if it has taken it. The
    /// main thread's xrWaitFrame for the frame after waits on that begin, so leaving it to the render thread (after its
    /// recording) would hold the main thread's simulation behind it. Nothing the render thread does before Acquire reads
    /// what beginning sets (_frame, _frameOpen, the quad layers' specs and cards); it begins the frame itself otherwise
    /// (BeginCurrent, in WhenEnded).</summary>
    void BeginTaken()
    {
        lock (_endLock) if (_current == null) return;
        BeginCurrent();
    }

    /// <summary>The render thread's frame failed: begin it (once the frame-end thread is idle, whatever failed there) and
    /// end it without layers, so the main thread's next xrWaitFrame returns and the failure reaches it at its next
    /// hand-over (FramePipe) instead of the headset freezing in xrWaitFrame. Render thread, best effort.</summary>
    public void Abandon()
    {
        if (!Overlap) return;   // (begun at the start, as ever)
        if (_ender != null)
            lock (_endLock)
            {
                _endThen = null;
                while (_endBusy) Monitor.Wait(_endLock);
            }
        try
        {
            lock (_vk.QueueLock)
            {
                BeginCurrent();
                EndUnshown();
            }
        }
        catch (Exception e) { _log($"OpenXR: ending the failed frame: {e.Message}"); }
        lock (_endLock) _current = null;
    }

    string PoseLine()
    {
        static string Stat(List<double> v, string unit)
        {
            if (v.Count == 0) return "-";
            v.Sort();
            return $"{v.Average():F1}/{v[(int)(0.95 * (v.Count - 1))]:F1}/{v[^1]:F1} {unit}";
        }
        var s = _errDeg.Count + _driftDeg.Count == 0 ? "" :
            $"; head as rendered vs as it was (mean/p95/max) {Stat(_errDeg, "deg")}, {Stat(_errMm, "mm")}; prediction moved by end {Stat(_driftDeg, "deg")}, {Stat(_driftMm, "mm")}";
        _errDeg.Clear(); _errMm.Clear(); _driftDeg.Clear(); _driftMm.Clear();
        return s;
    }

    /// <summary>Meta's runtime's own counters (compositor drops, latency), or null where it has none.</summary>
    public string? TakeRuntimeMetrics() => _metrics?.Line() is { Length: > 0 } l ? l : null;

    bool Head(long time, out Vector3 pos, out Quaternion rot)
    {
        var loc = new SpaceLocation { Type = StructureType.SpaceLocation };
        pos = default;
        rot = Quaternion.Identity;
        if (_viewSpace.Handle == 0 || _xr.LocateSpace(_viewSpace, _space, time, &loc) != Result.Success) return false;
        const SpaceLocationFlags Valid = SpaceLocationFlags.PositionValidBit | SpaceLocationFlags.OrientationValidBit;
        if ((loc.LocationFlags & Valid) != Valid) return false;
        pos = V(loc.Pose.Position);
        rot = Q(loc.Pose.Orientation);
        return true;
    }

    static double Degrees(Quaternion a, Quaternion b) =>
        2 * Math.Acos(Math.Min(1, Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))))) * 180 / Math.PI;

    /// <summary>The heads rendered for display times now past, against where the runtime says the head was then (main
    /// thread; the render thread adds them as it ends frames).</summary>
    void CheckRendered()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), margin = System.Diagnostics.Stopwatch.Frequency / 200;   // 5 ms
        List<(long Time, long At, Vector3 Pos, Quaternion Rot)>? due = null;
        lock (_pacingLock)
        {
            for (int i = _rendered.Count - 1; i >= 0; i--)
            {
                if (_rendered[i].At > now - margin) continue;
                (due ??= new()).Add(_rendered[i]);
                _rendered.RemoveAt(i);
            }
            if (_rendered.Count > 32) _rendered.RemoveRange(0, _rendered.Count - 32);
        }
        if (due == null) return;
        foreach (var r in due)
        {
            if (!Head(r.Time, out var p, out var q)) continue;
            lock (_pacingLock)
            {
                _errDeg.Add(Degrees(q, r.Rot));
                _errMm.Add(Vector3.Distance(p, r.Pos) * 1000);
            }
        }
    }
    // three frames' worth of state: one being rendered, one waiting for the render thread, one being simulated
    readonly XrFrame[] _frames = [new(), new(), new()];
    int _nextFrame;

    public XrInput Input { get; }
    /// <summary>The swapchain's size per eye: the largest the eyes can be rendered at.</summary>
    public int Width { get; }
    public int Height { get; }
    /// <summary>The runtime's recommended size per eye.</summary>
    public int RecommendedWidth { get; }
    public int RecommendedHeight { get; }
    /// <summary>The size the eyes are rendered at now (a corner of the swapchain's images; the compositor scales it to
    /// the lenses), set between frames by <see cref="SetRenderScale"/>.</summary>
    public int RenderWidth { get; private set; }
    public int RenderHeight { get; private set; }
    public float RenderScale { get; private set; } = 1;

    /// <summary>Render the eyes at this scale of the recommended size from the next frame (up to the swapchain's size;
    /// rounded to 8 pixels).</summary>
    public void SetRenderScale(float scale)
    {
        int w = Math.Min(Width, Math.Max(256, (int)MathF.Round(RecommendedWidth * scale / 8) * 8));
        int h = Math.Min(Height, Math.Max(256, (int)MathF.Round(RecommendedHeight * scale / 8) * 8));
        RenderScale = scale;
        RenderWidth = w;
        RenderHeight = h;
    }

    public VkFormat Format { get; }
    /// <summary>The play space's y = 0 is the floor (local-floor or stage); otherwise it's the head at start.</summary>
    public bool FloorLevel { get; private set; }
    public string SpaceName { get; private set; } = "";
    public bool Running => _running;
    int _slot;

    // --- the assistant's panel: its own swapchain, shown as a quad layer over the scene (the compositor samples it
    // straight onto the lenses, so text stays sharper than it would drawn into the eye images)
    const int PanelMaxW = 1152, PanelMaxH = 1536;
    QuadLayer? _panel, _feed;   // made once the format is known (the constructor)

    /// <summary>Show the panel image (null hides it) at a pose in the play space, this wide in metres.</summary>
    public void SetPanel(Sojourn.Render.Realtime.OverlayImage? image, Vector3 position, Quaternion orientation, float widthMetres) =>
        _panel?.Set(image, position, orientation, widthMetres);

    // the HUD board (Hud/HudBoard): its glass and × (a picture), and the HUD screen over it (the renderer's image)
    QuadLayer? _hudChrome, _hudScreen;

    /// <summary>The HUD board's layers: its glass under the cards and the feed, the HUDs over them, both under the panel; null
    /// chrome hides both.</summary>
    public void SetHud(Sojourn.Render.Realtime.OverlayImage? chrome, Vector3 chromePos, Quaternion chromeRot, float chromeWidth,
        Func<Sojourn.Render.Gpu.GpuImage?>? screen, Vector3 screenPos, Quaternion screenRot, float screenWidth)
    {
        _hudChrome?.Set(chrome, chromePos, chromeRot, chromeWidth);
        _hudScreen?.SetImage(chrome != null ? screen : null, screenPos, screenRot, screenWidth);
    }

    // a page shown up close (Media/PageView): its glass and keys (a picture), and the page over it (the renderer's copy
    // of its newest frame)
    QuadLayer? _pageChrome, _pageScreen;

    /// <summary>The page board's layers, placed like the HUD board's (null chrome hides both).</summary>
    public void SetPage(Sojourn.Render.Realtime.OverlayImage? chrome, Vector3 chromePos, Quaternion chromeRot, float chromeWidth,
        Func<Sojourn.Render.Gpu.GpuImage?>? screen, Vector3 screenPos, Quaternion screenRot, float screenWidth)
    {
        _pageChrome?.Set(chrome, chromePos, chromeRot, chromeWidth);
        _pageScreen?.SetImage(chrome != null ? screen : null, screenPos, screenRot, screenWidth);
    }

    // the keyboard (Control/HeadsetKeyboard), over everything while it's up (it's hidden while a confirmation waits)
    QuadLayer? _keyboard;

    public void SetKeyboard(Sojourn.Render.Realtime.OverlayImage? image, Vector3 position, Quaternion orientation, float widthMetres) =>
        _keyboard?.Set(image, position, orientation, widthMetres);

    // the reader (Assistant/Reader): a notecard, the session's replies or long text, scrolled with the pointer
    QuadLayer? _reader;

    public void SetReader(Sojourn.Render.Realtime.OverlayImage? image, Vector3 position, Quaternion orientation, float widthMetres) =>
        _reader?.Set(image, position, orientation, widthMetres);

    // the comfort vignette (Control/Comfort.cs): locked to the head (its pose is in the view space), over the eyes and
    // under every other layer, so the menus stay clear
    QuadLayer? _vignette;
    bool _vignetteLogged;

    /// <summary>The vignette's picture (null hides it), this wide, this far ahead of the eyes. Main thread.</summary>
    public void SetVignette(Sojourn.Render.Realtime.OverlayImage? image, float widthMetres, float distance) =>
        _vignette?.Set(image, new Vector3(0, 0, -distance), Quaternion.Identity, widthMetres);

    /// <summary>The chat feed's layer (Social/SocialView), under the panel's.</summary>
    public void SetFeed(Sojourn.Render.Realtime.OverlayImage? image, Vector3 position, Quaternion orientation, float widthMetres) =>
        _feed?.Set(image, position, orientation, widthMetres);

    // the assistant's elements that stay with the person (Showing/ShownView): a layer each, under the feed's, by key;
    // one no longer shown waits a few frames before it goes (its last copy may still be in flight). The layers are the
    // render thread's (made and let go as frames show them); the main thread sets the next frame's list
    readonly Dictionary<string, QuadLayer> _cards = new();
    readonly List<(QuadLayer Layer, int Frames)> _retired = new();
    List<(string Key, QuadLayer.Spec Spec)> _pendingCards = new();

    /// <summary>The assistant's cards from the next frame (all of them: any not listed go), or another group's sheets
    /// placed the same way (the map board and the location line, group "map"; keys unique across groups). Main
    /// thread.</summary>
    public void SetCards(IReadOnlyList<(string Key, Sojourn.Render.Realtime.OverlayImage Image, Vector3 Position, Quaternion Orientation, float WidthMetres)> cards,
        string group = "shown")
    {
        var list = new List<(string Key, QuadLayer.Spec Spec)>(cards.Count);
        foreach (var c in cards) list.Add((c.Key, QuadLayer.Spec.At(c.Image, null, c.Position, c.Orientation, c.WidthMetres, false)));
        _cardGroups[group] = list;
        _pendingCards = _cardGroups.Count == 1 ? list : _cardGroups.Values.SelectMany(g => g).ToList();
    }

    readonly Dictionary<string, List<(string Key, QuadLayer.Spec Spec)>> _cardGroups = new();

    /// <summary>Cards held at a hand like the wrist's tokens, by key (the location line, Map/MapWiring): left out of the
    /// menus' order and the hands' cut, and composited last, over the hands' layer, so nothing covers them. Set once.</summary>
    public string[] HeldCards { get; set; } = [];

    /// <summary>A frame's cards onto their layers (render thread, as it begins).</summary>
    void UseCards(List<(string Key, QuadLayer.Spec Spec)> cards)
    {
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Frames <= 0) { _retired[i].Layer.Dispose(); _retired.RemoveAt(i); }
            else _retired[i] = (_retired[i].Layer, _retired[i].Frames - 1);
        }
        foreach (var key in _cards.Keys.Where(k => !cards.Any(c => c.Key == k)).ToList())
        {
            _cards[key].Use(default);
            _retired.Add((_cards[key], 4));
            _cards.Remove(key);
        }
        foreach (var (key, spec) in cards)
        {
            if (!_cards.TryGetValue(key, out var q))
                _cards[key] = q = new QuadLayer(_xr, _vk, _session, (long)Format, "shown " + key, PanelMaxW, PanelMaxH, fit: true);
            q.Use(spec);
        }
    }

    // the fixed layers in the order a frame's specs keep them (Commit, BeginFrame)
    QuadLayer?[] Fixed => _fixed ??= [_panel, _feed, _hudChrome, _hudScreen, _pageChrome, _pageScreen, _keyboard, _reader, _vignette];
    QuadLayer?[]? _fixed;

    /// <summary>What the frame shows besides the eyes, as the main thread has set it: the layers, the cards, blackout,
    /// depth and the panel on top. Main thread, before handing the frame to the render thread.</summary>
    public void Commit(XrFrame f)
    {
        var fixedLayers = Fixed;
        if (f.Layers.Length != fixedLayers.Length) f.Layers = new QuadLayer.Spec[fixedLayers.Length];
        for (int i = 0; i < fixedLayers.Length; i++) f.Layers[i] = fixedLayers[i]?.Pending ?? default;
        f.Cards = _pendingCards;
        f.ViewCut = ViewCut;
        f.DepthLayers = DepthLayers;
        f.PanelOnTop = PanelOnTop;
    }
    /// <summary>Called on the main thread before the session ends (xrEndSession): wait for the render thread's frames.</summary>
    public System.Action? BeforeEnd { get; set; }

    /// <summary>The runtime is ending the session (the user quit from the headset, or the device went away).</summary>
    public bool Exiting { get; private set; }
    /// <summary>The play space moved under us (the headset was recentred): the rig should place itself again.</summary>
    public bool Recentred { get; set; }
    public bool Focused => _state == SessionState.Focused;
    public float RefreshRate { get; private set; }
    public SessionState State => _state;

    public XrSession(XrRuntime rt, VulkanContext vk, Action<string> log, float renderScale, float wantHz)
    {
        _rt = rt;
        _xr = rt.Api;
        _vk = vk;
        _log = log;
        _wantHz = wantHz;

        var binding = new GraphicsBindingVulkanKHR
        {
            Type = StructureType.GraphicsBindingVulkanKhr,
            Instance = new Silk.NET.Core.Native.VkHandle(vk.Instance.Handle),
            PhysicalDevice = new Silk.NET.Core.Native.VkHandle(vk.PhysicalDevice.Handle),
            Device = new Silk.NET.Core.Native.VkHandle(vk.Device.Handle),
            QueueFamilyIndex = vk.QueueFamily,
            QueueIndex = 0,
        };
        var sci = new SessionCreateInfo { Type = StructureType.SessionCreateInfo, Next = &binding, SystemId = rt.SystemId };
        Session session;
        XrRuntime.Check(_xr.CreateSession(rt.Instance, &sci, &session), "xrCreateSession");
        _session = session;

        CreateSpace();

        // the eyes' recommended size, scaled; one swapchain with a layer per eye
        uint n = 0;
        XrRuntime.Check(_xr.EnumerateViewConfigurationView(rt.Instance, rt.SystemId, ViewConfigurationType.PrimaryStereo, 0, &n, null), "xrEnumerateViewConfigurationViews");
        var views = new ViewConfigurationView[n];
        for (int i = 0; i < n; i++) views[i].Type = StructureType.ViewConfigurationView;
        fixed (ViewConfigurationView* p = views)
            XrRuntime.Check(_xr.EnumerateViewConfigurationView(rt.Instance, rt.SystemId, ViewConfigurationType.PrimaryStereo, n, &n, p), "xrEnumerateViewConfigurationViews");
        RecommendedWidth = (int)views[0].RecommendedImageRectWidth;
        RecommendedHeight = (int)views[0].RecommendedImageRectHeight;
        Width = (int)Math.Min(views[0].MaxImageRectWidth, Math.Round(views[0].RecommendedImageRectWidth * renderScale));
        Height = (int)Math.Min(views[0].MaxImageRectHeight, Math.Round(views[0].RecommendedImageRectHeight * renderScale));
        Width &= ~1; Height &= ~1;
        RenderWidth = Width;
        RenderHeight = Height;
        RenderScale = renderScale;
        Format = PickFormat();
        // the look's sheets are re-premultiplied in linear light for these layers (the compositor decodes sRGB first)
        Sojourn.Session.Assistant.GlassLight.Layers = Format == VkFormat.R8G8B8A8Srgb;
        // split frames copy each eye in after both are drawn (a stereo set made before this keeps one output: such
        // frames go unsplit, FrameRenderer checks)
        Sojourn.Render.Frame.RenderTargets.OutputPerEye = Overlap;
        _panel = new QuadLayer(_xr, _vk, _session, (long)Format, "panel", PanelMaxW, PanelMaxH);
        // as big as the panel's: an open feed (up to 640 logical px tall) at a large UiSize is that big (it was cropped at 1024)
        _feed = new QuadLayer(_xr, _vk, _session, (long)Format, "feed", PanelMaxW, PanelMaxH);
        _hudChrome = new QuadLayer(_xr, _vk, _session, (long)Format, "hud board", 1152, 768);
        _hudScreen = new QuadLayer(_xr, _vk, _session, (long)Format, "hud screen", 1920, 1080);
        _pageChrome = new QuadLayer(_xr, _vk, _session, (long)Format, "page board", 1152, 1152, fit: true);
        _pageScreen = new QuadLayer(_xr, _vk, _session, (long)Format, "page", 2048, 2048);
        _keyboard = new QuadLayer(_xr, _vk, _session, (long)Format, "keyboard", 1024, 512, fit: true);
        _reader = new QuadLayer(_xr, _vk, _session, (long)Format, "reader", PanelMaxW, PanelMaxH);
        _vignette = new QuadLayer(_xr, _vk, _session, (long)Format, "vignette", 512, 512);
        CreateSwapchain();
        CreateDepthSwapchain();
        _log($"OpenXR: eyes {Width}x{Height} (recommended {views[0].RecommendedImageRectWidth}x{views[0].RecommendedImageRectHeight}, x{renderScale:0.##}), {Format}, space {SpaceName}");

        if (rt.Extensions.Contains("XR_FB_display_refresh_rate") && _xr.TryGetInstanceExtension(null, rt.Instance, out FBDisplayRefreshRate fb)) _refresh = fb;
        Input = new XrInput(rt, _session);
        var vsi = new ReferenceSpaceCreateInfo
        {
            Type = StructureType.ReferenceSpaceCreateInfo, ReferenceSpaceType = ReferenceSpaceType.View,
            PoseInReferenceSpace = new Posef { Orientation = new Quaternionf(0, 0, 0, 1) },
        };
        Space vs;
        if (_xr.CreateReferenceSpace(_session, &vsi, &vs) == Result.Success) _viewSpace = vs;
        _metrics = MetaMetrics.TryCreate(rt, _session, log);
    }

    void CreateSpace()
    {
        uint n = 0;
        XrRuntime.Check(_xr.EnumerateReferenceSpaces(_session, 0, &n, null), "xrEnumerateReferenceSpaces");
        var types = new ReferenceSpaceType[n];
        fixed (ReferenceSpaceType* p = types) XrRuntime.Check(_xr.EnumerateReferenceSpaces(_session, n, &n, p), "xrEnumerateReferenceSpaces");
        // local-floor follows the headset's recentring and has the floor at y = 0; stage has the floor but not the
        // recentring; local has neither
        var type = types.Contains(ReferenceSpaceType.LocalFloorExt) ? ReferenceSpaceType.LocalFloorExt
            : types.Contains(ReferenceSpaceType.Stage) ? ReferenceSpaceType.Stage : ReferenceSpaceType.Local;
        FloorLevel = type != ReferenceSpaceType.Local;
        SpaceName = type switch { ReferenceSpaceType.LocalFloorExt => "local-floor", ReferenceSpaceType.Stage => "stage", _ => "local" };
        var ci = new ReferenceSpaceCreateInfo
        {
            Type = StructureType.ReferenceSpaceCreateInfo,
            ReferenceSpaceType = type,
            PoseInReferenceSpace = new Posef { Orientation = new Quaternionf(0, 0, 0, 1) },
        };
        Space space;
        XrRuntime.Check(_xr.CreateReferenceSpace(_session, &ci, &space), "xrCreateReferenceSpace");
        _space = space;
    }

    VkFormat PickFormat()
    {
        uint n = 0;
        XrRuntime.Check(_xr.EnumerateSwapchainFormats(_session, 0, &n, null), "xrEnumerateSwapchainFormats");
        var formats = new long[n];
        fixed (long* p = formats) XrRuntime.Check(_xr.EnumerateSwapchainFormats(_session, n, &n, p), "xrEnumerateSwapchainFormats");
        // the renderer's output is gamma-encoded RGBA8: copied bit for bit into an sRGB image, the compositor decodes
        // it right. Into a UNORM one it would be read as linear (too bright), so that's only the fallback.
        if (formats.Contains((long)VkFormat.R8G8B8A8Srgb)) return VkFormat.R8G8B8A8Srgb;
        _log("OpenXR: no R8G8B8A8_SRGB swapchain; using UNORM, colours will look washed out");
        if (formats.Contains((long)VkFormat.R8G8B8A8Unorm)) return VkFormat.R8G8B8A8Unorm;
        throw new XrException($"no RGBA8 swapchain format (have {string.Join(", ", formats.Select(f => (VkFormat)f))})", Result.ErrorSwapchainFormatUnsupported);
    }

    void CreateSwapchain()
    {
        var ci = new SwapchainCreateInfo
        {
            Type = StructureType.SwapchainCreateInfo,
            UsageFlags = SwapchainUsageFlags.TransferDstBit | SwapchainUsageFlags.ColorAttachmentBit,
            Format = (long)Format,
            SampleCount = 1,
            Width = (uint)Width,
            Height = (uint)Height,
            FaceCount = 1,
            ArraySize = 2,
            MipCount = 1,
        };
        Swapchain sc;
        XrRuntime.Check(_xr.CreateSwapchain(_session, &ci, &sc), "xrCreateSwapchain");
        _swapchain = sc;
        uint n = 0;
        XrRuntime.Check(_xr.EnumerateSwapchainImages(sc, 0, &n, null), "xrEnumerateSwapchainImages");
        var imgs = new SwapchainImageVulkanKHR[n];
        for (int i = 0; i < n; i++) imgs[i].Type = StructureType.SwapchainImageVulkanKhr;
        fixed (SwapchainImageVulkanKHR* p = imgs)
            XrRuntime.Check(_xr.EnumerateSwapchainImages(sc, n, &n, (SwapchainImageBaseHeader*)p), "xrEnumerateSwapchainImages");
        _images = imgs.Select(i => new VkImage(i.Image)).ToArray();
    }

    /// <summary>The hand layer's swapchain (the eyes' format and size, a layer each, drawn into) and a view per image
    /// and eye; false if the runtime won't make one (the hands then stay under the menus).</summary>
    bool CreateHandSwapchain()
    {
        var ci = new SwapchainCreateInfo
        {
            Type = StructureType.SwapchainCreateInfo,
            // (sampled too: the mirror window draws the hands over its menus from it; a split frame's are copied in)
            UsageFlags = SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.SampledBit | SwapchainUsageFlags.TransferDstBit,
            Format = (long)Format, SampleCount = 1, Width = (uint)Width, Height = (uint)Height, FaceCount = 1, ArraySize = 2, MipCount = 1,
        };
        Swapchain sc;
        if (_xr.CreateSwapchain(_session, &ci, &sc) != Result.Success) { _log("OpenXR: couldn't make the hands' swapchain; hands stay under the menus"); return false; }
        _handChain = sc;
        uint n = 0;
        XrRuntime.Check(_xr.EnumerateSwapchainImages(sc, 0, &n, null), "xrEnumerateSwapchainImages");
        var imgs = new SwapchainImageVulkanKHR[n];
        for (int i = 0; i < n; i++) imgs[i].Type = StructureType.SwapchainImageVulkanKhr;
        fixed (SwapchainImageVulkanKHR* p = imgs)
            XrRuntime.Check(_xr.EnumerateSwapchainImages(sc, n, &n, (SwapchainImageBaseHeader*)p), "xrEnumerateSwapchainImages");
        _handImages = imgs.Select(i => new VkImage(i.Image)).ToArray();
        _handViews = new Silk.NET.Vulkan.ImageView[_handImages.Length * 2];
        for (int i = 0; i < _handViews.Length; i++)
        {
            var vci = new Silk.NET.Vulkan.ImageViewCreateInfo
            {
                SType = Silk.NET.Vulkan.StructureType.ImageViewCreateInfo,
                Image = _handImages[i / 2],
                ViewType = Silk.NET.Vulkan.ImageViewType.Type2D,
                Format = Format,
                SubresourceRange = new Silk.NET.Vulkan.ImageSubresourceRange(VkImageAspectFlags.ColorBit, 0, 1, (uint)(i % 2), 1),
            };
            Silk.NET.Vulkan.ImageView view;
            VulkanContext.Check(_vk.Api.CreateImageView(_vk.Device, in vci, null, out view), "hands' swapchain image view");
            _handViews[i] = view;
        }
        return true;
    }

    void CreateDepthSwapchain()
    {
        if (!_rt.Extensions.Contains("XR_KHR_composition_layer_depth")) { _log("OpenXR: no depth layers (the runtime lacks XR_KHR_composition_layer_depth)"); return; }
        uint n = 0;
        XrRuntime.Check(_xr.EnumerateSwapchainFormats(_session, 0, &n, null), "xrEnumerateSwapchainFormats");
        var formats = new long[n];
        fixed (long* p = formats) XrRuntime.Check(_xr.EnumerateSwapchainFormats(_session, n, &n, p), "xrEnumerateSwapchainFormats");
        if (!formats.Contains((long)Sojourn.Render.Frame.RenderTargets.DepthFormat)) { _log("OpenXR: no D32 depth swapchain; no depth layers"); return; }
        var ci = new SwapchainCreateInfo
        {
            Type = StructureType.SwapchainCreateInfo,
            UsageFlags = SwapchainUsageFlags.TransferDstBit | SwapchainUsageFlags.DepthStencilAttachmentBit,
            Format = (long)Sojourn.Render.Frame.RenderTargets.DepthFormat,
            SampleCount = 1, Width = (uint)Width, Height = (uint)Height, FaceCount = 1, ArraySize = 2, MipCount = 1,
        };
        Swapchain sc;
        if (_xr.CreateSwapchain(_session, &ci, &sc) != Result.Success) { _log("OpenXR: couldn't make a depth swapchain; no depth layers"); return; }
        _depthChain = sc;
        XrRuntime.Check(_xr.EnumerateSwapchainImages(sc, 0, &n, null), "xrEnumerateSwapchainImages");
        var imgs = new SwapchainImageVulkanKHR[n];
        for (int i = 0; i < n; i++) imgs[i].Type = StructureType.SwapchainImageVulkanKhr;
        fixed (SwapchainImageVulkanKHR* p = imgs)
            XrRuntime.Check(_xr.EnumerateSwapchainImages(sc, n, &n, (SwapchainImageBaseHeader*)p), "xrEnumerateSwapchainImages");
        _depthImages = imgs.Select(i => new VkImage(i.Image)).ToArray();
        _depthViews = new Silk.NET.Vulkan.ImageView[_depthImages.Length * 2];
        for (int i = 0; i < _depthViews.Length; i++)
        {
            var vci = new Silk.NET.Vulkan.ImageViewCreateInfo
            {
                SType = Silk.NET.Vulkan.StructureType.ImageViewCreateInfo,
                Image = _depthImages[i / 2],
                ViewType = Silk.NET.Vulkan.ImageViewType.Type2D,
                Format = Sojourn.Render.Frame.RenderTargets.DepthFormat,
                SubresourceRange = new Silk.NET.Vulkan.ImageSubresourceRange(VkImageAspectFlags.DepthBit, 0, 1, (uint)(i % 2), 1),
            };
            Silk.NET.Vulkan.ImageView view;
            VulkanContext.Check(_vk.Api.CreateImageView(_vk.Device, in vci, null, out view), "depth swapchain image view");
            _depthViews[i] = view;
        }
        _log("OpenXR: depth layers on (positional reprojection of late frames)");
    }

    /// <summary>Handle the runtime's events: session state (begin and end the session as it asks), recentring, loss.</summary>
    public void PollEvents()
    {
        while (true)
        {
            var ev = new EventDataBuffer { Type = StructureType.EventDataBuffer };
            var r = _xr.PollEvent(_rt.Instance, &ev);
            if (r == Result.EventUnavailable) break;
            XrRuntime.Check(r, "xrPollEvent");
            switch (ev.Type)
            {
                case StructureType.EventDataSessionStateChanged:
                    OnState(((EventDataSessionStateChanged*)&ev)->State);
                    break;
                case StructureType.EventDataReferenceSpaceChangePending:
                {
                    // a runtime may announce its space at the start without moving it (the XR Simulator does): only a
                    // move is a recentring (places kept in the room are forgotten on one)
                    var ch = (EventDataReferenceSpaceChangePending*)&ev;
                    var pose = ch->PoseInPreviousSpace;
                    bool moved = ch->PoseValid == 0 || pose.Position.X * pose.Position.X + pose.Position.Y * pose.Position.Y
                        + pose.Position.Z * pose.Position.Z > 1e-6f || MathF.Abs(pose.Orientation.W) < 0.99999f;
                    _log($"OpenXR: reference space change ({ch->ReferenceSpaceType}){(moved ? "" : ", not moved")}");
                    if (moved) Recentred = true;
                    break;
                }
                case StructureType.EventDataInstanceLossPending:
                    _log("OpenXR: the runtime is going away");
                    Exiting = true;
                    break;
            }
        }
    }

    void OnState(SessionState state)
    {
        _log($"OpenXR: session {state}");
        _state = state;
        switch (state)
        {
            case SessionState.Ready:
            {
                var bi = new SessionBeginInfo { Type = StructureType.SessionBeginInfo, PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo };
                XrRuntime.Check(_xr.BeginSession(_session, &bi), "xrBeginSession");
                _running = true;
                RequestRefreshRate();
                break;
            }
            case SessionState.Stopping:
                _running = false;
                BeforeEnd?.Invoke();   // the render thread's frames ended first
                if (Overlap) WhenEnded(null);   // and the last of them on the frame-end thread
                XrRuntime.Check(_xr.EndSession(_session), "xrEndSession");
                break;
            case SessionState.Exiting:
            case SessionState.LossPending:
                _running = false;
                Exiting = true;
                break;
        }
    }

    void RequestRefreshRate()
    {
        if (_refresh is not { } fb) return;
        uint n = 0;
        if (fb.EnumerateDisplayRefreshRatesFB(_session, 0, &n, null) != Result.Success || n == 0) return;
        var rates = new float[n];
        fixed (float* p = rates) fb.EnumerateDisplayRefreshRatesFB(_session, n, &n, p);
        if (_wantHz > 0 && rates.Any(r => MathF.Abs(r - _wantHz) < 0.5f))
            fb.RequestDisplayRefreshRateFB(_session, rates.First(r => MathF.Abs(r - _wantHz) < 0.5f));
        float now;
        if (fb.GetDisplayRefreshRateFB(_session, &now) == Result.Success) RefreshRate = now;
        _log($"OpenXR: refresh {RefreshRate:0.#} Hz (offered {string.Join(", ", rates.Select(r => r.ToString("0.#")))})");
    }

    /// <summary>Wait for the runtime's next frame slot and locate the eyes and controllers at its predicted display
    /// time. Blocks for pacing (until the render thread has begun the last frame, too). Main thread, only while
    /// <see cref="Running"/>; the frame then goes to <see cref="Commit"/> and the render thread's
    /// <see cref="BeginFrame"/>.</summary>
    public XrFrame WaitFrame()
    {
        var wi = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
        var fs = new FrameState { Type = StructureType.FrameState };
        long waitFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        XrRuntime.Check(_xr.WaitFrame(_session, &wi, &fs), "xrWaitFrame");
        long woke = System.Diagnostics.Stopwatch.GetTimestamp();

        // three frames' worth of state, so the one being rendered and the one waiting are never the one being filled
        var f = _frames[_nextFrame];
        _nextFrame = (_nextFrame + 1) % _frames.Length;
        (f.Width, f.Height) = (RenderWidth, RenderHeight);
        f.DisplayTime = fs.PredictedDisplayTime;
        f.DisplayAt = _rt.ToStopwatch(fs.PredictedDisplayTime);
        f.Period = fs.PredictedDisplayPeriod / 1e9;
        f.ShouldRender = fs.ShouldRender != 0;
        f.WokeAt = woke;
        lock (_pacingLock)
        {
            _waitMs += (woke - waitFrom) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (_lastDisplayTime != 0 && fs.PredictedDisplayPeriod > 0)
            {
                long k = (long)Math.Round((double)(fs.PredictedDisplayTime - _lastDisplayTime) / fs.PredictedDisplayPeriod);
                _steps[k <= 0 ? 0 : Math.Min(3, (int)k)]++;
                if (k != _lastStep) _stepChanges++;
                _lastStep = k;
            }
            _lastDisplayTime = fs.PredictedDisplayTime;
            if (f.DisplayAt is { } at)
            {
                WakeLeadMs = (at - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _aheadMs += WakeLeadMs;
                _aheadMin = Math.Min(_aheadMin, WakeLeadMs);
                _aheadMax = Math.Max(_aheadMax, WakeLeadMs);
                _paced++;
            }
        }

        var li = new ViewLocateInfo
        {
            Type = StructureType.ViewLocateInfo,
            ViewConfigurationType = ViewConfigurationType.PrimaryStereo,
            DisplayTime = fs.PredictedDisplayTime,
            Space = _space,
        };
        var vs = new ViewState { Type = StructureType.ViewState };
        var views = stackalloc View[2];
        views[0] = new View { Type = StructureType.View };
        views[1] = new View { Type = StructureType.View };
        uint count = 0;
        XrRuntime.Check(_xr.LocateView(_session, &li, &vs, 2, &count, views), "xrLocateViews");
        f.Tracked = count == 2 && (vs.ViewStateFlags & ViewStateFlags.OrientationValidBit) != 0;
        for (int i = 0; i < 2; i++)
        {
            var v = views[i];
            f.Eyes[i] = new XrEye(V(v.Pose.Position), Q(v.Pose.Orientation),
                MathF.Tan(v.Fov.AngleLeft), MathF.Tan(v.Fov.AngleRight), MathF.Tan(v.Fov.AngleUp), MathF.Tan(v.Fov.AngleDown));
            f.Views[i] = new CompositionLayerProjectionView
            {
                Type = StructureType.CompositionLayerProjectionView,
                Pose = v.Pose,
                Fov = v.Fov,
                SubImage = new SwapchainSubImage
                {
                    Swapchain = _swapchain,
                    ImageRect = new Rect2Di(new Offset2Di(0, 0), new Extent2Di(f.Width, f.Height)),
                    ImageArrayIndex = (uint)i,
                },
            };
        }
        f.Input = Input.Sync(_space, fs.PredictedDisplayTime, Focused);
        CheckRendered();
        f.BeganTracked = f.Tracked && Head(fs.PredictedDisplayTime, out f.BeganPos, out f.BeganRot);
        return f;
    }

    /// <summary>Begin a frame the main thread waited for and committed (xrBeginFrame) and take what it shows. Render
    /// thread; the frame ends with <see cref="Present"/> or <see cref="EndFrameIfOpen"/>. With <see cref="Overlap"/> it's
    /// only taken here, and begun once the last frame has ended, right before its first GPU work (BeginCurrent).</summary>
    public void BeginFrame(XrFrame f)
    {
        if (Overlap)
        {
            // (the frame-end thread may begin it as soon as the last frame has ended: BeginTaken)
            lock (_endLock)
            {
                _begun = _split = false;
                _current = f;
            }
            return;
        }
        BeginNow(f);
    }

    /// <summary>Overlap: begin the render thread's frame if it isn't yet (the last frame ended, the queue held:
    /// <see cref="WhenEnded"/>).</summary>
    void BeginCurrent()
    {
        if (_begun || _current is not { } f) return;
        BeginNow(f);
        _begun = true;
        lock (_pacingLock) _olFrames++;
    }

    void BeginNow(XrFrame f)
    {
        var bi = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
        XrRuntime.Check(_xr.BeginFrame(_session, &bi), "xrBeginFrame");
        _frameOpen = true;
        _frame = f;
        var fixedLayers = Fixed;
        for (int i = 0; i < fixedLayers.Length && i < f.Layers.Length; i++) fixedLayers[i]?.Use(f.Layers[i]);
        UseCards(f.Cards);
    }

    static Vector3 V(Vector3f v) => new(v.X, v.Y, v.Z);
    static Quaternion Q(Quaternionf q) => new(q.X, q.Y, q.Z, q.W);

    // --- IFrameTarget ------------------------------------------------------------------------------------------

    public bool Acquire(int slot, out VkSemaphore ready, out VkSemaphore done)
    {
        ready = done = default;   // the runtime synchronises with our queue itself
        if (Overlap) WhenEnded(BeginCurrent);   // (a split frame's is begun already, with its scene)
        if (!_frameOpen || _frame is not { ShouldRender: true, Tracked: true }) return false;
        var ai = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
        uint index;
        lock (_vk.QueueLock) XrRuntime.Check(_xr.AcquireSwapchainImage(_swapchain, &ai, &index), "xrAcquireSwapchainImage");
        var wi = new SwapchainImageWaitInfo { Type = StructureType.SwapchainImageWaitInfo, Timeout = long.MaxValue };
        XrRuntime.Check(_xr.WaitSwapchainImage(_swapchain, &wi), "xrWaitSwapchainImage");
        _imageIndex = index;
        _imageHeld = true;
        _slot = slot;
        _depthWritten = 0;
        _eyesCopied = _handsWritten = 0;
        if (_depthChain.Handle != 0)
        {
            lock (_vk.QueueLock) XrRuntime.Check(_xr.AcquireSwapchainImage(_depthChain, &ai, &index), "xrAcquireSwapchainImage (depth)");
            XrRuntime.Check(_xr.WaitSwapchainImage(_depthChain, &wi), "xrWaitSwapchainImage (depth)");
            _depthIndex = index;
            _depthHeld = true;
        }
        return true;
    }

    // --- a split frame (overlap): the scene part writes our own images, the output part copies them in ----------------

    /// <summary>Take the render thread's frame split (it'll be shown, and overlap's on): RecordDepth, DepthOut,
    /// RecordPresent and Hands go into our own images until <see cref="RecordOutput"/>.</summary>
    public bool TrySplit()
    {
        if (!Overlap || _current is not { ShouldRender: true, Tracked: true }) return false;
        _split = true;
        _sceneOutCount = _sceneEyes = _sceneDepth = _sceneHands = 0;
        _sceneOut[0] = _sceneOut[1] = null;
        lock (_pacingLock) _olSplit++;
        return true;
    }

    /// <summary>The render thread's frame is recorded split (Window/MirrorTarget.cs leaves the window to the output).</summary>
    public bool Splitting => _split;

    /// <summary>The split frame's scene commands go to the queue once the last frame has ended, the frame begun first.</summary>
    public void SubmitScene(Action submit)
    {
        if (!Overlap) { submit(); return; }
        WhenEnded(() => { BeginCurrent(); submit(); });
    }

    /// <summary>An eye's finished image as a split frame's scene left it (for the mirror window's copy), or null.</summary>
    public GpuImage? SceneOutput(int eye) => _split && eye < 2 ? _sceneOut[_sceneOutCount == 1 ? 0 : eye] : null;
    public int SceneEyes => _sceneOutCount;

    GpuImage StageDepth(VkCommandBuffer cmd)
    {
        if (_stageDepth != null) return _stageDepth;
        var img = _stageDepth = new GpuImage(_vk, (uint)Width, (uint)Height, Sojourn.Render.Frame.RenderTargets.DepthFormat,
            Silk.NET.Vulkan.ImageUsageFlags.TransferSrcBit | Silk.NET.Vulkan.ImageUsageFlags.TransferDstBit | Silk.NET.Vulkan.ImageUsageFlags.DepthStencilAttachmentBit,
            layers: 2, dedicated: true);
        _stageDepthViews = [img.CreateView(Silk.NET.Vulkan.ImageViewType.Type2D, 0, 1, 0, 1), img.CreateView(Silk.NET.Vulkan.ImageViewType.Type2D, 0, 1, 1, 1)];
        // kept in DEPTH_STENCIL_ATTACHMENT_OPTIMAL between uses, as the depth swapchain's images come and go
        GpuImage.Barrier(_vk, cmd, img.Handle, VkImageAspectFlags.DepthBit, VkImageLayout.Undefined, VkImageLayout.DepthStencilAttachmentOptimal, 0, 1, 0, 2);
        return img;
    }

    GpuImage StageHands(VkCommandBuffer cmd)
    {
        if (_stageHands != null) return _stageHands;
        var img = _stageHands = new GpuImage(_vk, (uint)Width, (uint)Height, Format,
            Silk.NET.Vulkan.ImageUsageFlags.ColorAttachmentBit | Silk.NET.Vulkan.ImageUsageFlags.TransferSrcBit, layers: 2, dedicated: true);
        _stageHandViews = [img.CreateView(Silk.NET.Vulkan.ImageViewType.Type2D, 0, 1, 0, 1), img.CreateView(Silk.NET.Vulkan.ImageViewType.Type2D, 0, 1, 1, 1)];
        // kept in COLOR_ATTACHMENT_OPTIMAL between uses, as the hands' swapchain images come and go
        GpuImage.Barrier(_vk, cmd, img.Handle, VkImageAspectFlags.ColorBit, VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal, 0, 1, 0, 2);
        return img;
    }

    /// <summary>A split frame, once acquired: the quad layers' pictures, then each eye's image, its depth (the menus'
    /// walls already in it) and its hands copied from where the scene left them into the swapchain images. Render
    /// thread, into the frame's second command buffer.</summary>
    public void RecordOutput(VkCommandBuffer cmd)
    {
        if (!_split || _frame is not { } f || !_imageHeld) return;
        RecordPanel(cmd);
        var img = _images[_imageIndex];
        for (int layer = 0; layer < 2; layer++)
            CopyEye(cmd, img, layer, _sceneOutCount == 1 ? _sceneOut[0] : _sceneOut[layer], f, countVoid: _sceneOutCount != 1 || layer == 0);
        if (_depthHeld && f.DepthLayers && _stageDepth is { } sd)
            for (int eye = 0; eye < 2; eye++)
            {
                if ((_sceneDepth & (1 << eye)) == 0) continue;
                CopyLayer(cmd, sd.Handle, _depthImages[_depthIndex], VkImageAspectFlags.DepthBit, VkImageLayout.DepthStencilAttachmentOptimal, eye, f);
                _depthWritten |= 1 << eye;
            }
        int hands = _sceneHands & _eyesCopied;
        if (hands != 0 && HandsOverMenus && _stageHands is { } sh && AcquireHands())
            for (int eye = 0; eye < 2; eye++)
            {
                if ((hands & (1 << eye)) == 0) continue;
                CopyLayer(cmd, sh.Handle, _handImages[_handIndex], VkImageAspectFlags.ColorBit, VkImageLayout.ColorAttachmentOptimal, eye, f);
                _handsWritten |= 1 << eye;
            }
    }

    /// <summary>One eye's layer of our own image into the same layer of a swapchain image, both kept in this layout.</summary>
    void CopyLayer(VkCommandBuffer cmd, VkImage from, VkImage to, VkImageAspectFlags aspect, VkImageLayout kept, int eye, XrFrame f)
    {
        GpuImage.Barrier(_vk, cmd, from, aspect, kept, VkImageLayout.TransferSrcOptimal, 0, 1, (uint)eye, 1);
        GpuImage.Barrier(_vk, cmd, to, aspect, kept, VkImageLayout.TransferDstOptimal, 0, 1, (uint)eye, 1);
        var copy = new Silk.NET.Vulkan.ImageCopy
        {
            SrcSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(aspect, 0, (uint)eye, 1),
            DstSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(aspect, 0, (uint)eye, 1),
            Extent = new Silk.NET.Vulkan.Extent3D((uint)f.Width, (uint)f.Height, 1),
        };
        _vk.Api.CmdCopyImage(cmd, from, VkImageLayout.TransferSrcOptimal, to, VkImageLayout.TransferDstOptimal, 1, in copy);
        GpuImage.Barrier(_vk, cmd, to, aspect, VkImageLayout.TransferDstOptimal, kept, 0, 1, (uint)eye, 1);
        GpuImage.Barrier(_vk, cmd, from, aspect, VkImageLayout.TransferSrcOptimal, kept, 0, 1, (uint)eye, 1);
    }

    // --- the frame's images ---------------------------------------------------------------------------------------

    public void RecordDepth(VkCommandBuffer cmd, int eye, GpuImage depth)
    {
        if (_split)
        {
            if (_current is not { } sf || !HasDepthLayers || !sf.DepthLayers || eye > 1 || depth.Width != sf.Width || depth.Height != sf.Height) return;
            CopyDepth(cmd, depth, StageDepth(cmd).Handle, eye, sf);
            _sceneDepth |= 1 << eye;
            return;
        }
        if (_frame is not { } f || !_depthHeld || !f.DepthLayers || eye > 1 || depth.Width != f.Width || depth.Height != f.Height) return;
        CopyDepth(cmd, depth, _depthImages[_depthIndex], eye, f);
        _depthWritten |= 1 << eye;
    }

    void CopyDepth(VkCommandBuffer cmd, GpuImage depth, VkImage img, int eye, XrFrame f)
    {
        var was = depth.Layout;
        depth.Transition(cmd, VkImageLayout.TransferSrcOptimal);
        // depth swapchain images come and go in DEPTH_STENCIL_ATTACHMENT_OPTIMAL (XR_KHR_vulkan_enable)
        GpuImage.Barrier(_vk, cmd, img, VkImageAspectFlags.DepthBit, VkImageLayout.DepthStencilAttachmentOptimal, VkImageLayout.TransferDstOptimal, 0, 1, (uint)eye, 1);
        var copy = new Silk.NET.Vulkan.ImageCopy
        {
            SrcSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(VkImageAspectFlags.DepthBit, 0, depth.BaseLayer, 1),
            DstSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(VkImageAspectFlags.DepthBit, 0, (uint)eye, 1),
            Extent = new Silk.NET.Vulkan.Extent3D((uint)f.Width, (uint)f.Height, 1),
        };
        _vk.Api.CmdCopyImage(cmd, depth.Handle, VkImageLayout.TransferSrcOptimal, img, VkImageLayout.TransferDstOptimal, 1, in copy);
        GpuImage.Barrier(_vk, cmd, img, VkImageAspectFlags.DepthBit, VkImageLayout.TransferDstOptimal, VkImageLayout.DepthStencilAttachmentOptimal, 0, 1, (uint)eye, 1);
        depth.Transition(cmd, was);
    }

    /// <summary>The eye's layer of the depth just copied for the compositor (RecordDepth, this frame), in
    /// DEPTH_STENCIL_ATTACHMENT_OPTIMAL: the menus' footprints are carved into it with menus over walls.</summary>
    public DepthTarget? DepthOut(int eye)
    {
        if (_split)
            return _current is { } sf && eye <= 1 && (_sceneDepth & (1 << eye)) != 0 ? new DepthTarget(_stageDepthViews[eye], (uint)sf.Width, (uint)sf.Height) : null;
        if (_frame is not { } f || !_depthHeld || eye > 1 || (_depthWritten & (1 << eye)) == 0 || _depthViews.Length == 0) return null;
        return new DepthTarget(_depthViews[_depthIndex * 2 + eye], (uint)f.Width, (uint)f.Height);
    }

    void ReleaseDepth()
    {
        if (!_depthHeld) return;
        var ri = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
        _xr.ReleaseSwapchainImage(_depthChain, &ri);
        _depthHeld = false;
    }

    /// <summary>The rig moved the view to a new spot at once this frame (VrPilot's cut; for the pacing line's count:
    /// the black blink that used to hide it was dropped by the user, 2026-10-28). Main thread, for the next frame committed.</summary>
    public bool ViewCut { get; set; }

    public void RecordPresent(VkCommandBuffer cmd, int eye, int eyeCount, GpuImage output)
    {
        if (_split)
        {
            // kept for the output part: each eye finished into its own image (FrameRenderer splits only then)
            if (_current is not { } sf || eye > 1) return;
            _sceneOut[eye] = output;
            _sceneOutCount = eyeCount;
            if (output.Width == sf.Width && output.Height == sf.Height) _sceneEyes |= eyeCount == 1 ? 3 : 1 << eye;
            return;
        }
        var f = _frame!;
        if (eye == 0) RecordPanel(cmd);
        var img = _images[_imageIndex];
        for (int layer = eye; layer < (eyeCount == 1 ? 2 : eye + 1); layer++) CopyEye(cmd, img, layer, output, f, countVoid: layer == eye);
    }

    /// <summary>An eye's finished image into its layer of the acquired swapchain image (a dark grey void when it isn't
    /// one: the loading frame).</summary>
    void CopyEye(VkCommandBuffer cmd, VkImage img, int layer, GpuImage? output, XrFrame f, bool countVoid)
    {
        var api = _vk.Api;
        // swapchain images come and go in COLOR_ATTACHMENT_OPTIMAL (XR_KHR_vulkan_enable)
        GpuImage.Barrier(_vk, cmd, img, VkImageAspectFlags.ColorBit, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.TransferDstOptimal, 0, 1, (uint)layer, 1);
        if (output != null && output.Width == f.Width && output.Height == f.Height)
        {
            // bit for bit (UNORM into sRGB: the bytes are already gamma-encoded)
            var copy = new Silk.NET.Vulkan.ImageCopy
            {
                SrcSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(VkImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(VkImageAspectFlags.ColorBit, 0, (uint)layer, 1),
                Extent = new Silk.NET.Vulkan.Extent3D((uint)f.Width, (uint)f.Height, 1),
            };
            api.CmdCopyImage(cmd, output.Handle, VkImageLayout.TransferSrcOptimal, img, VkImageLayout.TransferDstOptimal, 1, in copy);
            _eyesCopied |= 1 << layer;
        }
        else
        {
            if (countVoid) lock (_pacingLock) _voids++;
            var c = new Silk.NET.Vulkan.ClearColorValue(0.02f, 0.025f, 0.03f, 1f);
            var range = new Silk.NET.Vulkan.ImageSubresourceRange(VkImageAspectFlags.ColorBit, 0, 1, (uint)layer, 1);
            api.CmdClearColorImage(cmd, img, VkImageLayout.TransferDstOptimal, in c, 1, in range);
        }
        GpuImage.Barrier(_vk, cmd, img, VkImageAspectFlags.ColorBit, VkImageLayout.TransferDstOptimal, VkImageLayout.ColorAttachmentOptimal, 0, 1, (uint)layer, 1);
    }

    /// <summary>The eye's layer of the hands' swapchain to draw the hands into, once RecordPresent put a real eye into
    /// the headset's image (acquired on the frame's first ask); a split frame's go into our own image first.</summary>
    public HandsTarget? Hands(VkCommandBuffer cmd, int eye)
    {
        if (_split)
        {
            if (_current is not { } sf || !HandsOverMenus || eye > 1 || (_sceneEyes & (1 << eye)) == 0) return null;
            StageHands(cmd);
            _sceneHands |= 1 << eye;
            return new HandsTarget(_stageHandViews[eye], Format, (uint)sf.Width, (uint)sf.Height);
        }
        if (_frame is not { } f || !HandsOverMenus || !_imageHeld || eye > 1 || (_eyesCopied & (1 << eye)) == 0) return null;
        if (!AcquireHands()) return null;
        _handsWritten |= 1 << eye;
        return new HandsTarget(_handViews[_handIndex * 2 + eye], Format, (uint)f.Width, (uint)f.Height);
    }

    /// <summary>The hands' swapchain made (once) and an image of it acquired and waited for this frame; false if the
    /// runtime won't make one (the hands then stay under the menus).</summary>
    bool AcquireHands()
    {
        if (_handChain.Handle == 0 && !CreateHandSwapchain()) { HandsOverMenus = false; return false; }
        if (_handHeld) return true;
        var ai = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
        uint index;
        lock (_vk.QueueLock) XrRuntime.Check(_xr.AcquireSwapchainImage(_handChain, &ai, &index), "xrAcquireSwapchainImage (hands)");
        var wi = new SwapchainImageWaitInfo { Type = StructureType.SwapchainImageWaitInfo, Timeout = long.MaxValue };
        XrRuntime.Check(_xr.WaitSwapchainImage(_handChain, &wi), "xrWaitSwapchainImage (hands)");
        _handIndex = index;
        _handHeld = true;
        return true;
    }

    void ReleaseHands()
    {
        if (!_handHeld) return;
        var ri = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
        _xr.ReleaseSwapchainImage(_handChain, &ri);
        _handHeld = false;
    }

    /// <summary>The menus this frame shows (its quad layers), by their corners mapped from the play space (to the
    /// region): where the hands go over them; null when none show or the hands are off.</summary>
    public List<Vector3[]>? MenuCorners(Func<Vector3, Vector3> map)
    {
        if (!HandsOverMenus) return null;
        List<Vector3[]>? menus = null;
        void Add(QuadLayer? q)
        {
            if (q?.Corners() is not { } c) return;
            for (int i = 0; i < 4; i++) c[i] = map(c[i]);
            (menus ??= new()).Add(c);
        }
        Add(_hudChrome);
        Add(_hudScreen);
        Add(_pageChrome);
        Add(_pageScreen);
        foreach (var (key, spec) in _pendingCards)
            if (Array.IndexOf(HeldCards, key) < 0 && QuadLayer.CornersOf(spec, PanelMaxW, PanelMaxH) is { } cc)
            {
                for (int i = 0; i < 4; i++) cc[i] = map(cc[i]);
                (menus ??= new()).Add(cc);
            }
        Add(_feed);
        Add(_reader);
        Add(_panel);
        Add(_keyboard);
        return menus;
    }

    void RecordPanel(VkCommandBuffer cmd)
    {
        var m = Mirroring;
        var k = Cutter;
        if (_frame is { } f) PlanMenus(f, cut: k != null);   // the order first: it says which layers go in parts
        _hudChrome?.Record(cmd, _slot, m, k);
        _hudScreen?.Record(cmd, _slot, m, k);
        _pageChrome?.Record(cmd, _slot, m, k);
        _pageScreen?.Record(cmd, _slot, m, k);
        foreach (var c in _cards.Values) c.Record(cmd, _slot, m, k);
        _feed?.Record(cmd, _slot, m, k);
        _panel?.Record(cmd, _slot, m, k);
        _keyboard?.Record(cmd, _slot, m, k);
        _reader?.Record(cmd, _slot, m, k);
        _vignette?.Record(cmd, _slot, m);
    }

    /// <summary>The mirror window shows this frame's menus (Window/MirrorTarget.cs sets it as the frame is acquired, null
    /// when it doesn't: the window blank, off, or the setting off): the layers keep their pictures' copies with it.
    /// Render thread.</summary>
    public LayerMirror? Mirroring { get; set; }

    readonly List<MirrorQuad> _mirrorQuads = new();
    readonly MirrorEye[] _mirrorEyes = new MirrorEye[2];
    readonly MirrorHands?[] _mirrorHands = new MirrorHands?[2];

    /// <summary>What the mirror window draws over the eyes this frame, as the compositor will show it (EndFrame's
    /// order and the hands' layer over it): the eyes as rendered, fitted into these cells of the window; false when no
    /// menu shows. Render thread, after the eyes are recorded.</summary>
    public bool MirrorLayers(Silk.NET.Vulkan.Rect2D left, Silk.NET.Vulkan.Rect2D right, out ReadOnlySpan<MirrorEye> eyes,
        out IReadOnlyList<MirrorQuad> quads, out ReadOnlySpan<MirrorHands?> hands)
    {
        eyes = default;
        hands = default;
        quads = _mirrorQuads;
        _mirrorQuads.Clear();
        if (_frame is not { } f || !_imageHeld) return false;
        var head = (f.Eyes[0].Position + f.Eyes[1].Position) * 0.5f;
        // the vignette first, as the compositor has it: its corners are the head's, put in the play space
        if (_viewSpace.Handle != 0 && _vignette?.Mirror() is { } vq)
        {
            var hq = f.Eyes[0].Orientation;
            Vector3 P(Vector3 v) => head + Vector3.Transform(v, hq);
            _mirrorQuads.Add(vq with { TopLeft = P(vq.TopLeft), TopRight = P(vq.TopRight), BottomRight = P(vq.BottomRight), BottomLeft = P(vq.BottomLeft) });
        }
        foreach (var o in Plan(f))
            if (o.Layer.Submits(o.Piece, out bool whole) && o.Layer.Mirror(whole ? -1 : o.Piece) is { } q) _mirrorQuads.Add(q);
        foreach (var h in _planHeld)   // (the window draws them under its hands; in the headset they're over)
            if (h.Submits(-1, out _) && h.Mirror(-1) is { } q) _mirrorQuads.Add(q);
        if (_mirrorQuads.Count == 0) return false;
        for (int i = 0; i < 2; i++)
        {
            var e = f.Eyes[i];
            _mirrorEyes[i] = new MirrorEye(e.Position, e.Orientation, e.TanLeft, e.TanRight, e.TanUp, e.TanDown, i == 0 ? left : right);
            _mirrorHands[i] = _handHeld && (_handsWritten & (1 << i)) != 0
                ? new MirrorHands(_handImages[_handIndex], (uint)i, _handViews[_handIndex * 2 + i], new Vector2((float)f.Width / Width, (float)f.Height / Height))
                : null;
        }
        eyes = _mirrorEyes;
        hands = _mirrorHands;
        return true;
    }

    MenuOrder? _seenOrder;

    // the fixed layers' names, in Fixed's order
    static readonly string[] FixedNames = ["panel", "feed", "hud board", "hud screen", "page board", "page", "keyboard", "reader", "vignette"];

    /// <summary>What a committed frame shows over its first eye, for a picture of what the person sees
    /// (Assistant/SeenShots.cs): the eye, and the layers with pictures in the order the compositor draws them (as
    /// <see cref="PlanMenus"/> and EndFrame have it, parts included, from the frame's specs rather than the layers' render-thread state);
    /// the renderer's own images (the HUD screen, a page) and the hands' layer are only named. Main thread, after
    /// <see cref="Commit"/>.</summary>
    public Sojourn.Client.Assistant.SeenView Seen(XrFrame f)
    {
        var fixedLayers = Fixed;
        var head = (f.Eyes[0].Position + f.Eyes[1].Position) * 0.5f;
        var layers = new List<Sojourn.Client.Assistant.SeenLayer>();
        var leftOut = new List<string>();
        var order = new List<(MenuOrder.Menu Menu, Sojourn.Client.Assistant.SeenLayer Layer)>();

        Sojourn.Client.Assistant.SeenLayer? Picture(string name, in QuadLayer.Spec spec, int maxW, int maxH)
        {
            if (spec.Image is not { Width: > 0, Height: > 0 } img || spec.WidthM <= 0) return null;
            var o = spec.Pose.Orientation;
            var p = spec.Pose.Position;
            return new(name, img, Math.Min(img.Width, maxW), Math.Min(img.Height, maxH),
                new Vector3(p.X, p.Y, p.Z), new Quaternion(o.X, o.Y, o.Z, o.W), spec.WidthM);
        }
        void Add(int i, bool top = false)
        {
            if (i >= f.Layers.Length || fixedLayers[i] is not { } q) return;
            var spec = f.Layers[i];
            if (spec.GpuLayer) { if (spec.Gpu != null) leftOut.Add(FixedNames[i]); return; }
            if (Picture(FixedNames[i], spec, q.MaxW, q.MaxH) is { } l && QuadLayer.CornersOf(spec, q.MaxW, q.MaxH) is { } c)
                order.Add((QuadLayer.MenuOf(c, top: top), l));
        }

        // the vignette first, in the view space: put in the play space as MirrorLayers does
        if (_viewSpace.Handle != 0 && fixedLayers[8] is { } vq && f.Layers.Length > 8 && Picture(FixedNames[8], f.Layers[8], vq.MaxW, vq.MaxH) is { } v)
        {
            var hq = f.Eyes[0].Orientation;
            layers.Add(v with { Centre = head + Vector3.Transform(v.Centre, hq), Rotation = hq * v.Rotation });
        }
        Add(2); Add(3);   // the HUD board's glass, its screen over it
        Add(4); Add(5);   // the page board's
        var held = new List<Sojourn.Client.Assistant.SeenLayer>();
        foreach (var (key, spec) in f.Cards)
            if (Picture("card " + key, spec, PanelMaxW, PanelMaxH) is { } c && QuadLayer.CornersOf(spec, PanelMaxW, PanelMaxH) is { } cc)
                if (Array.IndexOf(HeldCards, key) >= 0) held.Add(c);
                else order.Add((QuadLayer.MenuOf(cc), c));
        Add(1);
        Add(7);
        Add(0, top: f.PanelOnTop);
        Add(6);
        // the compositor's order and parts as EndFrame has them (MenuOrder; main thread, so an instance of its own)
        foreach (var p in (_seenOrder ??= new MenuOrder()).Order(order.Select(o => o.Menu).ToList(), head, cut: Cutter != null))
            layers.Add(order[p.Menu].Layer with { Clip = p.Clip });
        if (HandsOverMenus && layers.Count > 0) leftOut.Add("hands");
        layers.AddRange(held);   // over the hands' layer (HeldCards)
        return new Sojourn.Client.Assistant.SeenView(f.Eyes[0], layers, leftOut);
    }

    void ReleasePanel()
    {
        _hudChrome?.Release();
        _hudScreen?.Release();
        _pageChrome?.Release();
        _pageScreen?.Release();
        foreach (var c in _cards.Values) c.Release();
        _feed?.Release();
        _panel?.Release();
        _keyboard?.Release();
        _reader?.Release();
        _vignette?.Release();
    }

    /// <summary>After the frame's submission: give the image back and end the frame with it.</summary>
    public void Present() => Present(null);

    /// <summary>After the frame's submission: give the image back and end the frame with it, then run after (the mirror
    /// window's present). With <see cref="Overlap"/> that's on the frame-end thread, and this returns at once.</summary>
    public void Present(Action? after)
    {
        if (!Overlap)
        {
            lock (_vk.QueueLock) EndShown();
            after?.Invoke();
            return;
        }
        lock (_endLock) _current = null;   // the frame-end thread's now
        HandOver(EndShown, after);
    }

    /// <summary>The jitter fix (docs/vr.md > The jitter, and its fix): the queue waited idle, so all of the frame's GPU
    /// work is finished, before any of its images go back or it ends. The queue held.</summary>
    void EndShown()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (WaitGpuBeforeRelease) WaitQueue();
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        ReleasePanel();
        ReleaseDepth();
        ReleaseHands();
        if (_imageHeld)
        {
            var ri = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
            XrRuntime.Check(_xr.ReleaseSwapchainImage(_swapchain, &ri), "xrReleaseSwapchainImage");
            _imageHeld = false;
            EndFrame(withLayer: true);
        }
        else EndFrame(withLayer: false);
        lock (_pacingLock)
        {
            _olGpuMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
            _olEndMs += System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
            _olEnds++;
        }
    }

    /// <summary>The queue waited idle; a lost device thrown as the renderer throws it (DeviceLoss.Is knows it), rather
    /// than going on to hand the runtime images it will fail on.</summary>
    void WaitQueue()
    {
        var r = _vk.Api.QueueWaitIdle(_vk.Queue);
        if (r == Silk.NET.Vulkan.Result.ErrorDeviceLost) throw new VulkanException("vkQueueWaitIdle", r);
    }

    /// <summary>End the open frame if the renderer didn't (nothing to show, not tracked): no layers, the
    /// compositor keeps the last image. With <see cref="Overlap"/>, a frame not handed to the frame-end thread is begun
    /// (once the last has ended: every frame waited for is) and ended here.</summary>
    public void EndFrameIfOpen()
    {
        if (Overlap)
        {
            if (_current == null) return;   // with the frame-end thread
            WhenEnded(BeginCurrent);
            lock (_endLock) _current = null;
        }
        lock (_vk.QueueLock) EndUnshown();
    }

    void EndUnshown()
    {
        // the jitter fix holds here too: nothing of the frame goes back, nor does it end, before the GPU is done with it
        if (WaitGpuBeforeRelease && (_imageHeld || _frameOpen)) WaitQueue();
        ReleasePanel();
        ReleaseDepth();
        ReleaseHands();
        _depthWritten = 0;
        _handsWritten = 0;
        if (_imageHeld)
        {
            var ri = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
            _xr.ReleaseSwapchainImage(_swapchain, &ri);
            _imageHeld = false;
        }
        if (_frameOpen) EndFrame(withLayer: false);
    }

    /// <summary>The panel's layer goes over every other (a confirmation: nothing may cover it, wherever it is). Main
    /// thread, from the next frame committed.</summary>
    public bool PanelOnTop { get; set; }

    /// <summary>Clears what a menu's part doesn't show (Render's LayerCut; set once the renderer is up). Without it
    /// menus are never cut in parts, only ordered.</summary>
    public LayerCut? Cutter { get; set; }

    // the frame's menus in the compositor's order (MenuOrder): worked out as the layers are recorded (their parts made
    // then), read where the frame ends and by the mirror window
    readonly MenuOrder _menuOrder = new();
    readonly List<QuadLayer> _planLayers = new();
    readonly List<MenuOrder.Menu> _planMenus = new();
    readonly List<(QuadLayer Layer, int Piece)> _plan = new();
    readonly List<QuadLayer> _planHeld = new();   // HeldCards' layers, whole, after the hands' layer
    readonly List<MenuClip> _planClips = new();
    XrFrame? _planFor;
    bool _planOverlap;   // two menus overlap in view this frame (EndFrame holds the depth back)

    /// <summary>
    /// The frame's menus in the compositor's order (it draws them in order, with no depth between them), each over
    /// another exactly where it's in front of it from between the eyes; a menu another passes through goes in parts on
    /// either side of it (MenuOrder, QuadLayer's pieces); a confirmation's panel goes over everything (nothing else is
    /// up then anyway). cut: false orders whole layers only (a frame whose layers weren't recorded: no parts made). Tells
    /// each layer its parts. Render thread, after the frame is begun (the frame-end thread for a frame that ends
    /// unrecorded).
    /// </summary>
    void PlanMenus(XrFrame f, bool cut)
    {
        _planLayers.Clear();
        _planMenus.Clear();
        _plan.Clear();
        void Add(QuadLayer? q, int stack = 0, bool top = false)
        {
            if (q == null) return;
            q.UsePieces([]);
            if (q.AsMenu(stack, top) is not { } m) return;
            _planLayers.Add(q);
            _planMenus.Add(m);
        }
        Add(_hudChrome);
        Add(_hudScreen, stack: 1);   // its picture over its glass (2 mm out; coplanar from the side)
        Add(_pageChrome);
        Add(_pageScreen, stack: 1);
        _planHeld.Clear();
        foreach (var (key, c) in _cards)
            if (Array.IndexOf(HeldCards, key) < 0) Add(c);
            else { c.UsePieces([]); _planHeld.Add(c); }
        Add(_feed);
        Add(_reader);
        Add(_panel, top: f.PanelOnTop);
        Add(_keyboard);
        var head = (f.Eyes[0].Position + f.Eyes[1].Position) * 0.5f;
        var parts = _menuOrder.Order(_planMenus, head, cut);
        _planOverlap = _menuOrder.AnyOverlap;
        for (int li = 0; li < _planLayers.Count; li++)
        {
            _planClips.Clear();
            foreach (var p in parts) if (p.Menu == li && !p.Clip.Whole) _planClips.Add(p.Clip);
            if (_planClips.Count > 0) _planLayers[li].UsePieces(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_planClips));
        }
        Span<int> next = stackalloc int[_planLayers.Count];
        foreach (var p in parts)
            _plan.Add((_planLayers[p.Menu], p.Clip.Whole ? -1 : next[p.Menu]++));
        _planFor = f;
    }

    /// <summary>The frame's order (<see cref="PlanMenus"/>), whole layers only if its layers weren't recorded.</summary>
    List<(QuadLayer Layer, int Piece)> Plan(XrFrame f)
    {
        if (_planFor != f) PlanMenus(f, cut: false);
        return _plan;
    }


    void EndFrame(bool withLayer)
    {
        if (!_frameOpen || _frame == null) return;
        _frameOpen = false;
        var frame = _frame;
        fixed (CompositionLayerProjectionView* views = frame.Views)
        fixed (CompositionLayerDepthInfoKHR* depth = _depthInfo)
        {
            // the renderer's depth is reverse-Z with an infinite far plane: 0 is infinitely far, 1 the near plane
            // (ViewSetup.HeadsetNear, 2 cm).
            // The spec allows an infinite nearZ, but a runtime that derives its projection from nearZ and farZ the
            // usual way gets inf/inf there (NaN) and reprojects by garbage; 10 km is as good as infinite for
            // reprojection (under 1% off at 100 m) and finite on every path.
            var order = Plan(frame);
            int nq = 0;
            bool parts = false;
            foreach (var o in order)
                if (o.Layer.Submits(o.Piece, out bool whole))
                {
                    nq++;
                    parts |= !whole;
                }
            // No depth while a menu goes in parts (docs/vr.md > Menus over menus > The compositor's depth): where its
            // depth test runs (a patch around the view, over Link), Meta's compositor seems to depth-test quad layers
            // against each other too, a part's cleared side counting as solid, so where one menu passed through another
            // a bubble of one or both went missing, our frost showing in their place, moving with the head. Without depth
            // it tests nothing and the order and parts are exact everywhere; late frames reproject by rotation alone
            // meanwhile. Walls hide menus by our own drawing either way (Overlay.Walls.cs).
            // The same flip came back with whole menus merely overlapping (the reader just behind the panel, the user
            // in the headset, 2026-10-29; reprojection_depth off cured it): so no depth while any two overlap at all.
            // (Not our carving or the hands' layer's depth: the flip stayed with each turned off, in the headset.)
            parts |= _planOverlap;
            // held cards go over the hands' layer; with depth the compositor would hide them behind the hand
            int nh = 0;
            foreach (var h in _planHeld) if (h.Submits(-1, out _)) nh++;
            parts |= nh > 0;
            bool withDepth = withLayer && frame.DepthLayers && _depthWritten == 3 && !parts;
            if (withLayer && frame.DepthLayers && _depthWritten == 3 && parts) lock (_pacingLock) _depthHeldBack++;
            for (int i = 0; i < 2; i++)
            {
                depth[i] = new CompositionLayerDepthInfoKHR
                {
                    Type = StructureType.CompositionLayerDepthInfoKhr,
                    SubImage = new SwapchainSubImage
                    {
                        Swapchain = _depthChain,
                        ImageRect = views[i].SubImage.ImageRect,
                        ImageArrayIndex = (uint)i,
                    },
                    MinDepth = 0, MaxDepth = 1,
                    NearZ = 10000f, FarZ = Sojourn.Render.Frame.ViewSetup.HeadsetNear,
                };
                views[i].Next = withDepth ? &depth[i] : null;
            }
            var layer = new CompositionLayerProjection
            {
                Type = StructureType.CompositionLayerProjection,
                Space = _space,
                ViewCount = 2,
                Views = views,
            };
            bool vignette = withLayer && _viewSpace.Handle != 0 && _vignette is { Visible: true };
            var quads = stackalloc CompositionLayerQuad[nq + nh + 1];
            // the hands over the menus: their own images, with the eyes' poses, field of view, rectangle and depth
            bool withHands = withLayer && _handsWritten == 3 && nq > 0;
            var handViews = stackalloc CompositionLayerProjectionView[2];
            for (int i = 0; i < 2 && withHands; i++)
            {
                handViews[i] = views[i];
                handViews[i].SubImage.Swapchain = _handChain;
            }
            var hands = layer with { LayerFlags = CompositionLayerFlags.BlendTextureSourceAlphaBit, Views = handViews };
            if (withHands && !_handsLogged) { _handsLogged = true; _log("OpenXR: the hands go over the menus (a projection layer over them)"); }
            var layers = stackalloc CompositionLayerBaseHeader*[3 + nq + nh];
            uint count = 0;
            if (withLayer) layers[count++] = (CompositionLayerBaseHeader*)&layer;
            int qi = 0;
            if (vignette && !_vignetteLogged) { _vignetteLogged = true; _log("OpenXR: the comfort vignette is up (a quad layer in the view space)"); }
            if (vignette)
            {
                quads[qi] = _vignette!.Layer(_viewSpace);
                layers[count++] = (CompositionLayerBaseHeader*)&quads[qi++];
            }
            foreach (var o in order)
            {
                if (!o.Layer.Submits(o.Piece, out bool whole)) continue;
                quads[qi] = whole ? o.Layer.Layer(_space) : o.Layer.PieceLayer(o.Piece, _space);
                layers[count++] = (CompositionLayerBaseHeader*)&quads[qi++];
            }
            if (withHands) layers[count++] = (CompositionLayerBaseHeader*)&hands;
            foreach (var h in _planHeld)
            {
                if (!h.Submits(-1, out _)) continue;
                quads[qi] = h.Layer(_space);
                layers[count++] = (CompositionLayerBaseHeader*)&quads[qi++];
            }
            var ei = new FrameEndInfo
            {
                Type = StructureType.FrameEndInfo,
                DisplayTime = _frame.DisplayTime,
                EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                LayerCount = count,
                Layers = layers,
            };
            // how far the head's prediction for this display time moved while the frame was made (located on this thread)
            (double Deg, double Mm)? drift = null;
            if (withLayer && frame.BeganTracked && Head(frame.DisplayTime, out var hp, out var hq))
                drift = (Degrees(hq, frame.BeganRot), Vector3.Distance(hp, frame.BeganPos) * 1000);
            lock (_pacingLock)
            {
                if (_frame.DisplayAt is { } at && System.Diagnostics.Stopwatch.GetTimestamp() > at) _endedLate++;
                if (!withLayer && _frame.ShouldRender) _noImage++;
                if (withLayer && frame.ViewCut) _cuts++;
                if (withLayer && frame.BeganTracked && frame.DisplayAt is { } shownAt) _rendered.Add((frame.DisplayTime, shownAt, frame.BeganPos, frame.BeganRot));
                if (drift is { } d) { _driftDeg.Add(d.Deg); _driftMm.Add(d.Mm); }
                _spanMs += (System.Diagnostics.Stopwatch.GetTimestamp() - frame.WokeAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _spans++;
            }
            XrRuntime.Check(_xr.EndFrame(_session, &ei), "xrEndFrame");
        }
    }

    /// <summary>Ask the runtime to end the session (it answers with Stopping, then Exiting).</summary>
    public void RequestExit()
    {
        if (_running) _xr.RequestExitSession(_session);
    }

    /// <summary>The process is ending after a lost GPU (or to start again): give the session back to the runtime at once,
    /// without touching the device (destroying a session destroys its swapchains and spaces with it). Left to the
    /// process's exit, Meta's runtime noticed the old session's end ~11 s later, when the restarted run's session was
    /// already showing, and took that one out of view for good (2026-10-31; DeviceLoss.LetGo). Main thread, the render
    /// thread drained or given up on; best effort.</summary>
    public void LetGo()
    {
        if (_ender != null)
        {
            lock (_endLock) { _endStop = true; _endThen = null; Monitor.PulseAll(_endLock); }
            _ender.Join(1000);
        }
        _xr.DestroySession(_session);
    }

    public void Dispose()
    {
        Sojourn.Session.Assistant.GlassLight.Layers = false;
        if (_ender != null)
        {
            // the last frame ended (or given up on, the GPU lost), then the thread let go
            try { WhenEnded(null); } catch (Exception e) { _log($"OpenXR: ending the last frame failed: {e.Message}"); }
            lock (_endLock) { _endStop = true; Monitor.PulseAll(_endLock); }
            _ender.Join(2000);
        }
        _vk.WaitIdle();
        Input.Dispose();
        foreach (var v in _stageDepthViews.Concat(_stageHandViews)) _vk.Api.DestroyImageView(_vk.Device, v, null);
        _stageDepth?.Dispose();
        _stageHands?.Dispose();
        if (_swapchain.Handle != 0) _xr.DestroySwapchain(_swapchain);
        foreach (var v in _depthViews) _vk.Api.DestroyImageView(_vk.Device, v, null);
        if (_depthChain.Handle != 0) _xr.DestroySwapchain(_depthChain);
        foreach (var v in _handViews) _vk.Api.DestroyImageView(_vk.Device, v, null);
        if (_handChain.Handle != 0) _xr.DestroySwapchain(_handChain);
        _panel?.Dispose();
        _feed?.Dispose();
        _hudChrome?.Dispose();
        _hudScreen?.Dispose();
        _pageChrome?.Dispose();
        _pageScreen?.Dispose();
        _keyboard?.Dispose();
        _reader?.Dispose();
        _vignette?.Dispose();
        foreach (var c in _cards.Values) c.Dispose();
        foreach (var (c, _) in _retired) c.Dispose();
        if (_viewSpace.Handle != 0) _xr.DestroySpace(_viewSpace);
        if (_space.Handle != 0) _xr.DestroySpace(_space);
        _xr.DestroySession(_session);
    }
}
