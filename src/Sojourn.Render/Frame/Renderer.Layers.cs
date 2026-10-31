using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

// See-through surfaces (the user's approvals of 2026-10-27; docs/rendering.md > See-through surfaces). Two settings:
// - A cap on the blended layers a pixel shades. Before the blended faces are drawn, alpha_count.frag draws them nearest
//   first against the opaque depth, without colour, keeping per pixel a budget spent by what the layers it meets hide,
//   and the farthest of their depths (layers.glsl). alpha.frag then drops a fragment behind every counted layer once
//   the pixel's budget is spent. Which fragments get counted first changes only how much is dropped, never what may be:
//   what's dropped lies behind layers that together let through under 1% of it, or behind the cap's N layers (each
//   of alpha 0.1 or more, so with N 16, a stack of faint ones still passes 18% at most).
// - Simpler lighting (alpha_simple.vert, alpha.frag SIMPLE_LIGHT): ambient, atmospherics and local lights per vertex.
// Main eyes only: mirrors and probe captures draw every layer, lit as before. Claude Opus 5.5, 2026-10-27.
public sealed unsafe partial class Renderer
{
    /// <summary>The person's settings (FrameRenderer sets them each frame): the layer cap's N (0: none), and the
    /// simpler lighting.</summary>
    public int AlphaLayers;
    public bool SimpleAlpha;
    /// <summary>For tests: SOJOURN_ALPHA_LAYERS=N and SOJOURN_SIMPLE_ALPHA=0|1 override the settings; SOJOURN_AB=alphalayers
    /// (the setting's N, or 16, against none) or simplealpha flips one every frame.</summary>
    static readonly int? EnvLayers = int.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_ALPHA_LAYERS"), out var n) ? n : null;
    static readonly bool? EnvSimple = System.Environment.GetEnvironmentVariable("SOJOURN_SIMPLE_ALPHA") is { } s ? s == "1" : null;
    static bool? _abLayers, _abSimple;
    int _layers;
    bool _simple;

    Pipeline _alphaCount;
    readonly Pipeline[] _alphaSimple = new Pipeline[5];   // [0] every path, [1-4] ALPHA_PATH's; made on first use
    readonly List<DrawCmd> _nearFirst = new();

    void CreateLayers() => _alphaCount = Make(new GraphicsPipelineDesc
    {
        Vertex = _shaders.Get("scene.vert", ShaderStage.Vertex), Fragment = _shaders.Get("alpha_count.frag", ShaderStage.Fragment),
        ColorFormats = Array.Empty<Format>(), DepthFormat = RenderTargets.DepthFormat, DepthWrite = false, Cull = CullModeFlags.None,
        Layout = Layout,
    });

    /// <summary>This frame's settings, as its recording starts.</summary>
    void ResolveAlpha()
    {
        int layers = EnvLayers ?? AlphaLayers;
        if (_abLayers is { } on) layers = on ? (layers > 0 ? layers : 16) : 0;
        _layers = layers <= 0 ? 0 : Math.Min(layers, 64);
        _simple = _abSimple ?? EnvSimple ?? SimpleAlpha;
    }

    /// <summary>The least a counted layer spends of the 64-unit budget (N layers spend it all).</summary>
    uint LayerMin => _layers == 0 ? 0u : (uint)Math.Max(1, 64 / _layers);

    ulong LayerArenaBytes(DrawList dl) => _layers > 0 ? (ulong)(dl.Alpha.Count * sizeof(DrawIndexedIndirect) + 256) : 0;

    /// <summary>The blended faces nearest first for the count (the list as sorted far to near for drawing), grouped by
    /// geometry arena.</summary>
    List<IndirectBatch>? NearFirst(FrameArena arena, DrawList dl)
    {
        var farToNear = dl.Alpha;
        if (_layers == 0 || farToNear.Count == 0) return null;
        _nearFirst.Clear();
        // fullbright faces aren't counted (nor dropped for long: they're cheap to draw, so counting them costs more than
        // it saves); a layer not counted only means less is dropped
        for (int i = farToNear.Count - 1; i >= 0; i--)
            if (PathOf(dl, farToNear[i].Instance) != 2) _nearFirst.Add(farToNear[i]);
        return _nearFirst.Count == 0 ? null : BuildBatches(arena, _nearFirst, sort: false);
    }

    /// <summary>A main eye's view: its records and the cap's least share of a layer.</summary>
    void LayerView(ref GpuView view, RenderTargets rt)
    {
        if (_layers == 0) return;
        ulong a = rt.EnsureAlphaLayers();
        (view.LayersLo, view.LayersHi, view.LayerMin) = ((uint)a, (uint)(a >> 32), LayerMin);
    }

    /// <summary>The count, at the start of the alpha pass (a stereo set's eyes at once): records cleared, the blended
    /// faces drawn nearest first into them.</summary>
    void RecordLayerCount(CommandBuffer cmd, FrameDraws draws, RenderTargets rt)
    {
        if (_layers == 0 || _mirrorPass || draws.AlphaNear is not { Count: > 0 } near || (rt.Stereo ?? rt).AlphaLayers is not { } buf) return;
        ulong slice = (ulong)rt.Width * rt.Height * 4;
        ulong offset = rt.Stereo != null ? slice * rt.Layer : 0, size = rt.Stereo != null ? slice : Math.Min(buf.Size, slice * rt.Layers);
        var api = _vk.Api;
        GpuImage.MemoryBarrier(_vk, cmd);   // the last eye's (or frame's) blended faces are done reading them
        api.CmdFillBuffer(cmd, buf.Handle, offset, size, 0);
        GpuImage.MemoryBarrier(_vk, cmd);
        rt.Depth.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
        BeginRendering(cmd, rt.Width, rt.Height, Array.Empty<GpuImage>(), rt.Depth, clearColor: false, clearDepth: false);
        Bind(cmd, _alphaCount);
        DrawBatches(cmd, near);
        api.CmdEndRendering(cmd);
        GpuImage.MemoryBarrier(_vk, cmd);
        Profiler.Mark("alpha layers");
    }

    /// <summary>The blended faces' pipeline for a material path (0: alpha.frag's every path), lit as the setting says.</summary>
    Pipeline AlphaPipeline(int path)
    {
        if (!_simple) return path == 0 ? _alpha : _alphaPaths[path];
        if (_alphaSimple[path].Handle != 0) return _alphaSimple[path];
        var fs = _shaders.Get("alpha.frag", ShaderStage.Fragment, AlphaDefines(path, simple: true));
        return _alphaSimple[path] = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("alpha_simple.vert", ShaderStage.Vertex), Fragment = fs, ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend = new[] { BlendMode.Alpha }, DepthFormat = RenderTargets.DepthFormat, DepthWrite = false, Layout = Layout,
            Cull = CullModeFlags.None,
        });
    }

    /// <summary>The pre-water pool's pipeline for a material path, lit as the setting says, as every other blended face
    /// is (with depth writes, no layer cut: Renderer.cs).</summary>
    readonly Pipeline[] _preWaterSimple = new Pipeline[5];

    Pipeline PreWaterPipeline(int path)
    {
        if (!_simple) return _alphaPreWater[path];
        if (_preWaterSimple[path].Handle != 0) return _preWaterSimple[path];
        var fs = _shaders.Get("alpha.frag", ShaderStage.Fragment, path > 0 ? new[] { "ALPHA_PATH=" + path, "SIMPLE_LIGHT" } : new[] { "SIMPLE_LIGHT" });
        return _preWaterSimple[path] = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("alpha_simple.vert", ShaderStage.Vertex), Fragment = fs, ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend = new[] { BlendMode.Alpha }, DepthFormat = RenderTargets.DepthFormat, DepthWrite = true, Layout = Layout,
            Cull = CullModeFlags.None,
        });
    }

    /// <summary>SOJOURN_LAYERS_DEBUG=1: what the cap drops is drawn magenta instead.</summary>
    static readonly bool LayersDebug = System.Environment.GetEnvironmentVariable("SOJOURN_LAYERS_DEBUG") == "1";

    /// <summary>alpha.frag's defines for the blended faces' pipelines (path 0: every path).</summary>
    static string[] AlphaDefines(int path, bool simple)
    {
        var d = new List<string> { "LAYER_CUT" };
        if (path > 0) d.Add("ALPHA_PATH=" + path);
        if (simple) d.Add("SIMPLE_LIGHT");
        if (LayersDebug) d.Add("LAYER_DEBUG");
        return d.ToArray();
    }

    void DisposeLayers()
    {
        _vk.Api.DestroyPipeline(_vk.Device, _alphaCount, null);
        foreach (var p in _alphaSimple.Concat(_preWaterSimple))
            if (p.Handle != 0) _vk.Api.DestroyPipeline(_vk.Device, p, null);
    }
}
