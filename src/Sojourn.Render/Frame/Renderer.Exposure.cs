using System.Diagnostics;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

// Dynamic exposure as the viewer does it in real time (LLPipeline::generateExposure with history): once a frame,
// from the first eye's luminance, eased toward the view's steady-state exposure over a couple of seconds
// (RenderDynamicExposureSpeedError / SpeedTarget), and the same value for every eye of the frame. Multitude's port
// rendered still frames, so it took the steady state per view; in real time that makes the exposure jump with every
// small head movement over fine bright detail (flowers, leaves, bulbs), and differ between the eyes. (Only with
// post-processing on: ViewerRenderSettings.SnapshotNoPost, Multitude's snapshot look, keeps it off for now.)
public sealed unsafe partial class Renderer
{
    const Format ExposureFormat = Format.R32G32Sfloat;
    Pipeline _exposure;
    GpuImage? _exposureA, _exposureB;
    int _exposureSlotA = -1, _exposureSlotB = -1;
    bool _exposureOnA, _exposureValid;
    int _exposureFrameSlot = -1;   // this frame's exposure image slot, once the first eye has made it
    long _exposureAt;
    readonly Dictionary<FrameResources, GpuBuffer> _exposureReadback = new();
    readonly ExposureStats _exposureStats = new();

    /// <summary>The exposure kept where the last frame left it (a still's tiles and cube faces, Realtime/FrameRenderer.Still.cs).</summary>
    public bool HoldExposure;

    /// <summary>Called at the start of a frame's recording: the next eye's post makes this frame's exposure.</summary>
    void BeginExposureFrame() => _exposureFrameSlot = -1;

    /// <summary>This frame's exposure image's slot (luminance and the exposure pass the first time a frame asks), or
    /// 0xFFFF without dynamic exposure.</summary>
    int FrameExposure(FrameResources fr, CommandBuffer cmd, ViewSetup v, RenderTargets rt, bool dynamic)
    {
        if (!dynamic) { _exposureValid = false; return 0xFFFF; }
        if (_exposureFrameSlot >= 0) return _exposureFrameSlot;
        EnsureExposure();
        var api = _vk.Api;
        // generateLuminance: sample into the 256x256 luminance map, then its mip chain
        rt.Luminance.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        _push.I1 = (uint)rt.HdrSlot;
        _push.I2 = (uint)Bindless.Black;   // the viewer adds last frame's glow; a still frame has none
        _push.I3 = (uint)rt.NormalSlot;
        BeginRendering(cmd, 256, 256, new[] { rt.Luminance }, null, false, false, viewOverride: rt.LuminanceMip0);
        Bind(cmd, _luminance);
        Push(cmd);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
        GenerateMips(cmd, rt.Luminance);

        // the exposure pass: last frame's image in, this frame's out
        var (prev, prevSlot, next, nextSlot) = _exposureOnA
            ? (_exposureA!, _exposureSlotA, _exposureB!, _exposureSlotB)
            : (_exposureB!, _exposureSlotB, _exposureA!, _exposureSlotA);
        long now = Stopwatch.GetTimestamp();
        float dt = _exposureValid && !HoldExposure ? (float)Math.Clamp(Stopwatch.GetElapsedTime(_exposureAt, now).TotalSeconds, 0, 1) : 0;
        _exposureAt = now;
        // RenderDynamicExposureSpeedError / SpeedTarget: within that fraction of the target after that many seconds
        float err = Math.Clamp(v.SkyU.DynamicExposureParams.W, 1e-4f, 0.999f), secs = Math.Max(0.05f, v.SkyU.DynamicExposureParams2.W);
        float speed = -MathF.Log(err) / secs;
        prev.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        next.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        _push.I1 = (uint)rt.LuminanceSlot;
        _push.I2 = (uint)prevSlot;
        _push.V0 = new System.Numerics.Vector4(dt, _exposureValid ? 1 : 0, speed, 0);
        BeginRendering(cmd, 1, 1, new[] { next }, null, false, false);
        Bind(cmd, _exposure);
        Push(cmd);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
        _push.V0 = default;

        // read back for the log (what the view did, and what the old per-view exposure would have done)
        if (!_exposureReadback.TryGetValue(fr, out var rb))
            _exposureReadback[fr] = rb = new GpuBuffer(_vk, 8, BufferUsageFlags.TransferDstBit, MemoryUse.Readback);
        next.Transition(cmd, ImageLayout.TransferSrcOptimal);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(1, 1, 1),
        };
        api.CmdCopyImageToBuffer(cmd, next.Handle, ImageLayout.TransferSrcOptimal, rb.Handle, 1, in copy);
        next.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        var stats = _exposureStats;
        fr.Retire.Add(() =>
        {
            var p = (float*)rb.Alloc.Mapped;
            stats.Add(p[0], p[1]);
        });

        _exposureOnA = !_exposureOnA;
        _exposureValid = true;
        return _exposureFrameSlot = nextSlot;
    }

    /// <summary>The exposure as it stands, for a view that mustn't make or move it (the handheld camera's preview,
    /// Renderer.Preview.cs): this frame's if an eye has made it, else the last frame's; 0xFFFF without one.</summary>
    int HeldExposure(bool dynamic)
    {
        if (!dynamic || !_exposureValid) return 0xFFFF;
        if (_exposureFrameSlot >= 0) return _exposureFrameSlot;
        return _exposureOnA ? _exposureSlotA : _exposureSlotB;   // (the next frame's "prev": the last one written)
    }

    void EnsureExposure()
    {
        if (_exposureA != null) return;
        const ImageUsageFlags use = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit;
        _exposureA = new GpuImage(_vk, 1, 1, ExposureFormat, use);
        _exposureB = new GpuImage(_vk, 1, 1, ExposureFormat, use);
        _exposureSlotA = _bindless.AllocTexture();
        _bindless.Write(_exposureSlotA, _exposureA.View, _bindless.Nearest);
        _exposureSlotB = _bindless.AllocTexture();
        _bindless.Write(_exposureSlotB, _exposureB.View, _bindless.Nearest);
        _exposure = PipelineBuilder.Create(_vk, new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("fullscreen.vert", ShaderStage.Vertex), Fragment = _shaders.Get("exposure.frag", ShaderStage.Fragment),
            ColorFormats = new[] { ExposureFormat }, DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
    }

    /// <summary>The exposure over the frames since the last call, for the session log: the value used, how far it moved
    /// in a frame, and how the steady-state exposure (what each frame showed before it had history) jumped. Null when
    /// nothing was measured.</summary>
    public string? TakeExposureLine() => _exposureStats.Take();

    void DisposeExposure()
    {
        foreach (var b in _exposureReadback.Values) b.Dispose();
        _exposureReadback.Clear();
        if (_exposureA == null) return;
        _vk.Api.DestroyPipeline(_vk.Device, _exposure, null);
        _bindless.FreeTexture(_exposureSlotA);
        _bindless.FreeTexture(_exposureSlotB);
        _exposureA.Dispose();
        _exposureB!.Dispose();
    }

    sealed class ExposureStats
    {
        int _frames, _targetJumps;
        float _last = float.NaN, _lastTarget = float.NaN, _maxStep, _maxTargetStep, _min = float.MaxValue, _max = float.MinValue;

        public void Add(float s, float target)
        {
            if (!float.IsNaN(_last) && _last > 0)
            {
                _maxStep = MathF.Max(_maxStep, MathF.Abs(s - _last) / _last);
                float jump = MathF.Abs(target - _lastTarget) / MathF.Max(1e-3f, _lastTarget);
                _maxTargetStep = MathF.Max(_maxTargetStep, jump);
                if (jump > 0.02f) _targetJumps++;
            }
            _last = s;
            _lastTarget = target;
            _min = MathF.Min(_min, target);
            _max = MathF.Max(_max, target);
            _frames++;
        }

        public string? Take()
        {
            if (_frames == 0) return null;
            var line = $"exposure {_last:F2} (moved at most {_maxStep * 100:F1}% a frame; steady state {_min:F2}-{_max:F2}, " +
                       $"jumping over 2% in {_targetJumps} of {_frames} frames, at most {_maxTargetStep * 100:F0}%)";
            _frames = _targetJumps = 0;
            _maxStep = _maxTargetStep = 0;
            _min = float.MaxValue;
            _max = float.MinValue;
            return line;
        }
    }
}
