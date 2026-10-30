using System.Numerics;
using System.Runtime.InteropServices;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Sojourn.Render.Frame;

/// <summary>Push block layout shared by every pipeline (see Shaders/common.glsl).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PushBlock
{
    public ulong View, Instances, Materials, Lights, Sky;
    public uint I0, I1, I2, I3;
    public Vector4 V0, V1;
    /// <summary>The persistent instance and material tables (Frame/PersistentTable.cs): indices with its bit set.</summary>
    public ulong PersistentInstances, PersistentMaterials;
}

/// <summary>Per frame-in-flight resources: command buffer, fence, transient arena, readback buffer.</summary>
public sealed class FrameResources : IDisposable
{
    readonly VulkanContext _vk;
    public CommandPool Pool;
    public CommandBuffer Cmd;
    /// <summary>A split frame's second buffer (FrameRenderer, a target that takes its output apart from the scene: the
    /// headset's overlap): what writes the target's own images, submitted after the scene's.</summary>
    public CommandBuffer OutputCmd;
    public Fence Fence;
    public FrameArena Arena;
    public GpuBuffer? Readback;
    /// <summary>This frame's posed (skinned) vertices, written by the skinning pre-pass; grown on demand.</summary>
    public GpuBuffer? Skinned;
    public bool InFlight;
    public long SubmittedAt;
    public readonly List<Action> Retire = new();   // run after the fence signals

    public FrameResources(VulkanContext vk)
    {
        _vk = vk;
        Pool = vk.CreateCommandPool();
        Cmd = vk.AllocateCommandBuffer(Pool);
        OutputCmd = vk.AllocateCommandBuffer(Pool);
        Fence = vk.CreateFence(signaled: false);
        Arena = new FrameArena(vk, 16UL << 20);
    }

    /// <summary>Starts recording this slot's frame: resets the pool and begins its one command buffer. The buffer
    /// is allocated once and reused; allocating one per frame leaked them (a reset pool keeps every buffer ever
    /// allocated from it, and the driver keeps command memory for each: ~60 KB of commit per frame, ~4 GB/h with
    /// the spotlight at 20 fps).</summary>
    public CommandBuffer BeginCommands()
    {
        VulkanContext.Check(_vk.Api.ResetCommandPool(_vk.Device, Pool, 0), "vkResetCommandPool");
        _vk.Begin(Cmd);
        return Cmd;
    }

    /// <summary>Begins the frame's second buffer (after <see cref="BeginCommands"/> reset the pool).</summary>
    public CommandBuffer BeginOutput()
    {
        _vk.Begin(OutputCmd);
        return OutputCmd;
    }

    public GpuBuffer EnsureReadback(ulong bytes)
    {
        if (Readback == null || Readback.Size < bytes)
        {
            Readback?.Dispose();
            Readback = new GpuBuffer(_vk, bytes, BufferUsageFlags.TransferDstBit, MemoryUse.Readback);
        }
        return Readback;
    }

    public GpuBuffer EnsureSkinned(ulong bytes)
    {
        if (Skinned == null || Skinned.Size < bytes)
        {
            Skinned?.Dispose();
            ulong n = 4UL << 20;
            while (n < bytes) n *= 2;
            Skinned = new GpuBuffer(_vk, n, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit, MemoryUse.GpuOnly);
        }
        return Skinned;
    }

    public unsafe void Wait()
    {
        if (!InFlight) return;
        var f = Fence;
        VulkanContext.Check(_vk.Api.WaitForFences(_vk.Device, 1, in f, true, ulong.MaxValue), "vkWaitForFences");
        InFlight = false;
        foreach (var a in Retire) a();
        Retire.Clear();
    }

    public unsafe void Dispose()
    {
        Wait();
        Readback?.Dispose();
        Skinned?.Dispose();
        Arena.Dispose();
        _vk.Api.DestroyFence(_vk.Device, Fence, null);
        _vk.Api.DestroyCommandPool(_vk.Device, Pool, null);
    }
}

/// <summary>
/// Records one view's passes in the viewer's deferred order: G-buffer (sky first, then opaque and masked),
/// deferred sun/ambient resolve, local lights, fullbright, haze, alpha, glow, post (glow combine, exposure,
/// tonemap, gamma) into an RGBA8 output, then copies it to a readback buffer.
/// </summary>
public sealed unsafe partial class Renderer : IDisposable
{
    readonly VulkanContext _vk;
    readonly Bindless _bindless;
    readonly ShaderCompiler _shaders;
    readonly GeometryPool _geometry;
    public PipelineLayout Layout;

    readonly Pipeline[] _alphaPreWater = new Pipeline[5];   // the pre-water pool: [0] every path, [1-4] ALPHA_PATH's
    Pipeline _gbuffer, _gbufferTwoSided, _sky, _stars, _light, _localLights, _fullbright, _alpha, _glow, _haze, _tonemap, _cas, _glowExtract, _glowBlur, _post, _water, _projectors, _sunlight, _blurlight, _luminance, _shadow, _spotShadow, _shadowSolid, _spotShadowSolid, _waterHaze, _skin,
        _avatarAlphaNoCull, _avatarAlphaDepth, _avatarAlphaBack, _avatarAlphaFront, _riggedAlpha;

    /// <summary>The four sun cascades' depth maps, shared by every view (re-rendered per view), sized on demand.</summary>
    public readonly GpuImage?[] ShadowMaps = new GpuImage?[7];   // 0-3 sun cascades, 4-5 projector shadows, 6 the far sun cascade
    /// <summary>A probe capture's sun shadow kept between the frames its pass is spread over (the frame's own cascade 0
    /// overwrites the shared map after the captures): copied back into cascade 0 instead of drawn again. Not sampled
    /// itself, so the capture's shaders read cascade 0 as they always did.</summary>
    GpuImage? _captureShadow;
    /// <summary>SOJOURN_AB=captureshadow: on its "off" side a capture's shadow is drawn again every frame, as before.</summary>
    public static bool CaptureShadowEveryFrame;
    public uint ShadowMapSize { get; private set; }

    /// <summary>Bindless slots of the SMAA area and search textures (<see cref="Luts"/>).</summary>
    public int SmaaAreaSlot = -1, SmaaSearchSlot = -1;
    /// <summary>The viewer's mTrueNoiseMap (RenderGlowNoise), dithering the glow extract.</summary>
    public int GlowNoiseSlot = Bindless.Black;
    // SMAA pipelines per preset (0-3) and pass (edges, weights, blending), made on first use
    readonly Pipeline[,] _smaa = new Pipeline[4, 3];

    // particles: a pipeline per blend function pair (made on first use), and their glow pass
    readonly Dictionary<int, Pipeline> _particlePipes = new();
    Pipeline _particleGlow;

    // RenderGlowIterations / RenderGlowWidth defaults
    public int GlowIterations = 2;
    public float GlowWidth = 1.3f;

    public readonly PassProfiler Profiler;

    /// <summary>Multiview (VK_KHR_multiview): the headset's two eyes drawn in one pass where the passes draw geometry
    /// (G-buffer, forward, alpha), each pipeline in a variant with the eyes' view mask; SOJOURN_MULTIVIEW=0 draws the
    /// eyes one after the other, as before (for comparing).</summary>
    public static bool Multiview = System.Environment.GetEnvironmentVariable("SOJOURN_MULTIVIEW") != "0";

    /// <summary>SOJOURN_AB=NAME: an A/B comparison under whatever else the GPU is doing: a named switch flips every frame
    /// and the profiler tags each pass with its side (|on, |off), so both sides share the same contention.</summary>
    static readonly string? AbName = System.Environment.GetEnvironmentVariable("SOJOURN_AB");
    public static readonly Dictionary<string, Action<bool>> AbSwitches = new()
    {
        ["multiview"] = on => Multiview = on,
        ["mvgbuffer"] = on => MultiviewGBuffer = on,
        ["solidshadows"] = on => SolidShadows = on,
        ["gbclear"] = on => GBufferNoClear = !on,
        ["lens"] = on => LensMask = on,
        ["barriers"] = on => GpuImage.PreciseBarriers = on,
        ["alphapaths"] = on => AlphaPaths = on,
        ["captureshadow"] = on => CaptureShadowEveryFrame = !on,   // FrameRenderer.CaptureShadow
        ["farhalf"] = on => FarCascadesHalf = on,
        ["alphalayers"] = on => _abLayers = on,   // Renderer.Layers.cs
        ["simplealpha"] = on => _abSimple = on,
        ["halflight"] = on => _abHalfLight = on,   // Renderer.HalfLight.cs
        ["tinycasters"] = on => AbCasterTexels = on ? AbCasterTexelsOn : 0,   // RenderQuality.MinCasterTexels
        ["farshadows"] = on => Realtime.FrameRenderer.AbFarOff = !on,   // RenderQuality.FarShadowSize
        ["none"] = _ => { },   // an A/A run: what the comparison's noise is
    };
    static bool _abOn;
    /// <summary>SOJOURN_AB=tinycasters: the casters' texel floor this frame (null: the person's setting), the "on"
    /// side's from SOJOURN_CASTER_TEXELS (default 1).</summary>
    public static float? AbCasterTexels = float.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_MIN_CASTER_TEXELS"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : null;   // tests: over the setting
    static readonly float AbCasterTexelsOn = float.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_CASTER_TEXELS"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 1f;

    /// <summary>The G-buffer pass in multiview too (SOJOURN_MULTIVIEW_GBUFFER=1). Off: on the RTX 5070 the four-target
    /// G-buffer drawn to both eyes at once took 2-3 times as long as an eye at a time (club 0.6 -> 1.9 ms a frame).</summary>
    public static bool SolidShadows = true, GBufferNoClear = true;
    /// <summary>SOJOURN_PROFILE_CASCADES=1: each sun cascade timed on its own (the rest of the shadows stay "shadows").</summary>
    static readonly bool ProfileCascades = System.Environment.GetEnvironmentVariable("SOJOURN_PROFILE_CASCADES") == "1";
    public static bool MultiviewGBuffer = System.Environment.GetEnvironmentVariable("SOJOURN_MULTIVIEW_GBUFFER") == "1";

    /// <summary>Called at the start of each frame's recording.</summary>
    public static void AbStep()
    {
        if (AbName == null || !AbSwitches.TryGetValue(AbName, out var set)) return;
        _abOn = !_abOn;
        set(_abOn);
        PassProfiler.Tag = _abOn ? "|on" : "|off";
    }
    // the view mask the passes being recorded draw with (0: one view), and each pipeline's description, for its variants
    uint _viewMask;
    readonly Dictionary<ulong, GraphicsPipelineDesc> _descs = new();
    readonly Dictionary<(ulong, uint), Pipeline> _variants = new();

    Pipeline Make(GraphicsPipelineDesc d)
    {
        var p = PipelineBuilder.Create(_vk, d);
        _descs[p.Handle] = d;
        return p;
    }

    /// <summary>The pipeline as the current passes need it: itself, or its variant for the multiview pass's mask.</summary>
    Pipeline Variant(Pipeline p)
    {
        if (_viewMask == 0) return p;
        if (_variants.TryGetValue((p.Handle, _viewMask), out var v)) return v;
        v = PipelineBuilder.Create(_vk, _descs[p.Handle].WithViewMask(_viewMask));
        _variants[(p.Handle, _viewMask)] = v;
        return v;
    }

    public Renderer(VulkanContext vk, Bindless bindless, ShaderCompiler shaders, GeometryPool geometry)
    {
        _vk = vk;
        Profiler = new PassProfiler(vk);
        _bindless = bindless;
        _shaders = shaders;
        _geometry = geometry;
        shaders.AddVirtualFile("sky_block.glsl", GlslMirror.Struct<GpuSky>("SkyBlock"));
        if (RenderTargets.Narrow) shaders.AddVirtualFile("gbuffer_format.glsl", "#define NARROW_GBUFFER 1" + System.Environment.NewLine);   // gbuffer_util.glsl
        Layout = PipelineBuilder.CreateLayout(vk, new[] { bindless.Layout }, (uint)sizeof(PushBlock));
        CreatePipelines();
    }

    void CreatePipelines()
    {
        var sceneVs = _shaders.Get("scene.vert", ShaderStage.Vertex);
        var fsVs = _shaders.Get("fullscreen.vert", ShaderStage.Vertex);
        var gbufFormats = new[] { RenderTargets.DiffuseFormat, RenderTargets.SpecularFormat, RenderTargets.NormalFormat, RenderTargets.EmissiveFormat };
        var gbFs = _shaders.Get("gbuffer.frag", ShaderStage.Fragment);
        _gbuffer = Make(new GraphicsPipelineDesc
        {
            Vertex = sceneVs, Fragment = gbFs, ColorFormats = gbufFormats, DepthFormat = RenderTargets.DepthFormat, Layout = Layout,
        });
        _gbufferTwoSided = Make(new GraphicsPipelineDesc
        {
            Vertex = sceneVs, Fragment = gbFs, ColorFormats = gbufFormats, DepthFormat = RenderTargets.DepthFormat, Layout = Layout,
            Cull = CullModeFlags.None,
        });
        _sky = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("sky.frag", ShaderStage.Fragment), ColorFormats = gbufFormats,
            DepthFormat = RenderTargets.DepthFormat, DepthTest = true, DepthWrite = false, Cull = CullModeFlags.None,
            VertexInput = false, Layout = Layout,
        });
        _stars = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("stars.vert", ShaderStage.Vertex), Fragment = _shaders.Get("stars.frag", ShaderStage.Fragment), ColorFormats = gbufFormats,
            Blend = new[] { BlendMode.None, BlendMode.None, BlendMode.None, BlendMode.AddWithAlpha },
            WriteMasks = new ColorComponentFlags[] { 0, 0, 0, ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit },
            DepthFormat = RenderTargets.DepthFormat, DepthTest = true, DepthWrite = false, Cull = CullModeFlags.None,
            VertexInput = false, Layout = Layout,
        });
        _light = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("light.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.HdrFormat },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        _localLights = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("locallights.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend = new[] { BlendMode.Additive }, WriteMasks = new[] { ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        _projectors = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("projectors.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend = new[] { BlendMode.Additive }, WriteMasks = new[] { ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        var hdrRgb = new[] { ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit };
        _fullbright = Make(new GraphicsPipelineDesc
        {
            Vertex = sceneVs, Fragment = _shaders.Get("fullbright.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.HdrFormat },
            WriteMasks = hdrRgb, DepthFormat = RenderTargets.DepthFormat, DepthWrite = true, Layout = Layout,
        });
        _haze = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("haze.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend = new[] { BlendMode.Haze },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        _alpha = Make(new GraphicsPipelineDesc
        {
            Vertex = sceneVs, Fragment = _shaders.Get("alpha.frag", ShaderStage.Fragment, AlphaDefines(0, simple: false)), ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend = new[] { BlendMode.Alpha }, DepthFormat = RenderTargets.DepthFormat, DepthWrite = false, Layout = Layout,
            Cull = CullModeFlags.None,   // double-sided materials show both sides (alpha.frag drops other back faces)
        });
        // the pre-water alpha pool writes depth ("needed for accurate water fog": LLDrawPoolAlpha::forwardRender) and is
        // never cut by the layer count (drawn before it); lit in full here, simply by PreWaterPipeline; [0] every path
        for (int path = 0; path <= 4; path++)
            _alphaPreWater[path] = Make(new GraphicsPipelineDesc
            {
                Vertex = sceneVs, Fragment = _shaders.Get("alpha.frag", ShaderStage.Fragment, path > 0 ? new[] { "ALPHA_PATH=" + path } : Array.Empty<string>()),
                ColorFormats = new[] { RenderTargets.HdrFormat },
                Blend = new[] { BlendMode.Alpha }, DepthFormat = RenderTargets.DepthFormat, DepthWrite = true, Layout = Layout,
                Cull = CullModeFlags.None,
            });
        for (int path = 1; path <= 4; path++)
            _alphaPaths[path] = Make(new GraphicsPipelineDesc
            {
                Vertex = sceneVs, Fragment = _shaders.Get("alpha.frag", ShaderStage.Fragment, AlphaDefines(path, simple: false)), ColorFormats = new[] { RenderTargets.HdrFormat },
                Blend = new[] { BlendMode.Alpha }, DepthFormat = RenderTargets.DepthFormat, DepthWrite = false, Layout = Layout,
                Cull = CullModeFlags.None,
            });
        // avatar hair, eyelashes and skirts (LLViewerJoint::render, transparent meshes): large ones draw colour without
        // depth writes, then depth only, then colour again where they are nearest, all unculled; small ones draw back
        // faces without depth writes, then front faces with them
        var alphaFs = _shaders.Get("alpha.frag", ShaderStage.Fragment);
        _avatarAlphaNoCull = Make(new GraphicsPipelineDesc
        {
            Vertex = sceneVs, Fragment = alphaFs, ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend = new[] { BlendMode.Alpha }, WriteMasks = hdrRgb, DepthFormat = RenderTargets.DepthFormat, DepthWrite = false,
            Cull = CullModeFlags.None, Layout = Layout,
        });
        _avatarAlphaDepth = Make(new GraphicsPipelineDesc
        {
            Vertex = sceneVs, Fragment = alphaFs, ColorFormats = new[] { RenderTargets.HdrFormat },
            WriteMasks = new[] { (ColorComponentFlags)0 }, DepthFormat = RenderTargets.DepthFormat, DepthWrite = true,
            Cull = CullModeFlags.None, Layout = Layout,
        });
        _avatarAlphaBack = Make(new GraphicsPipelineDesc
        {
            Vertex = sceneVs, Fragment = alphaFs, ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend = new[] { BlendMode.Alpha }, WriteMasks = hdrRgb, DepthFormat = RenderTargets.DepthFormat, DepthWrite = false,
            Cull = CullModeFlags.FrontBit, Layout = Layout,
        });
        // rigged blended faces write depth (LLDrawPoolAlpha renders the rigged pass with depth writes on)
        _riggedAlpha = Make(new GraphicsPipelineDesc
        {
            Vertex = sceneVs, Fragment = alphaFs, ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend = new[] { BlendMode.Alpha }, WriteMasks = hdrRgb, DepthFormat = RenderTargets.DepthFormat, DepthWrite = true, Layout = Layout,
        });
        _avatarAlphaFront = Make(new GraphicsPipelineDesc
        {
            Vertex = sceneVs, Fragment = alphaFs, ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend = new[] { BlendMode.Alpha }, WriteMasks = hdrRgb, DepthFormat = RenderTargets.DepthFormat, DepthWrite = true,
            Cull = CullModeFlags.BackBit, Layout = Layout,
        });
        _sunlight = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("sunlight.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.LightMapFormat },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        _blurlight = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("blurlight.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.LightMapFormat },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        _luminance = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("luminance.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.LuminanceFormat },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        _shadow = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("shadow.vert", ShaderStage.Vertex), Fragment = _shaders.Get("shadow.frag", ShaderStage.Fragment),
            ColorFormats = Array.Empty<Format>(), DepthFormat = ShadowFormat, DepthCompare = CompareOp.Less,
            Cull = CullModeFlags.BackBit, FrontFace = FrontFace.Clockwise, DepthClamp = true, Layout = Layout,
        });
        // projector shadows: a perspective frustum, near and far clip as the viewer's
        _spotShadow = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("shadow.vert", ShaderStage.Vertex), Fragment = _shaders.Get("shadow.frag", ShaderStage.Fragment),
            ColorFormats = Array.Empty<Format>(), DepthFormat = ShadowFormat, DepthCompare = CompareOp.Less,
            Cull = CullModeFlags.BackBit, FrontFace = FrontFace.Clockwise, DepthClamp = false, Layout = Layout,
        });
        // casters that cut nothing (no alpha, not water: shadow.frag would only return) as depth alone, without a
        // fragment shader: the same depths, written as they're tested
        _shadowSolid = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("shadow.vert", ShaderStage.Vertex),
            ColorFormats = Array.Empty<Format>(), DepthFormat = ShadowFormat, DepthCompare = CompareOp.Less,
            Cull = CullModeFlags.BackBit, FrontFace = FrontFace.Clockwise, DepthClamp = true, Layout = Layout,
        });
        _spotShadowSolid = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("shadow.vert", ShaderStage.Vertex),
            ColorFormats = Array.Empty<Format>(), DepthFormat = ShadowFormat, DepthCompare = CompareOp.Less,
            Cull = CullModeFlags.BackBit, FrontFace = FrontFace.Clockwise, DepthClamp = false, Layout = Layout,
        });
        _skin = PipelineBuilder.CreateCompute(_vk, _shaders.Get("skin.comp", ShaderStage.Compute), Layout);
        CreateTiles();   // Renderer.Tiles.cs
        CreateLens();    // Renderer.Lens.cs
        CreateLayers();  // Renderer.Layers.cs
        CreateWater();   // Renderer.Water.cs
        CreateParcelLines();   // Renderer.ParcelLines.cs
        CreateBeacons();   // Renderer.Beacons.cs
        _waterHaze = Make(new GraphicsPipelineDesc
        {
            Vertex = sceneVs, Fragment = _shaders.Get("waterhaze.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend = new[] { BlendMode.Haze }, DepthFormat = RenderTargets.DepthFormat, DepthWrite = false,
            Cull = CullModeFlags.None, Layout = Layout,
        });
        _water = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("water.vert", ShaderStage.Vertex), Fragment = _shaders.Get("water.frag", ShaderStage.Fragment),
            ColorFormats = new[] { RenderTargets.HdrFormat }, DepthFormat = RenderTargets.DepthFormat, DepthWrite = true,
            Cull = CullModeFlags.None, Layout = Layout,
        });
        _glow = Make(new GraphicsPipelineDesc
        {
            Vertex = sceneVs, Fragment = _shaders.Get("glow.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend = new[] { BlendMode.Additive }, WriteMasks = new[] { ColorComponentFlags.ABit },
            DepthFormat = RenderTargets.DepthFormat, DepthWrite = false, DepthBias = true, Layout = Layout,
        });
        _tonemap = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("tonemap.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.HdrFormat },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        _cas = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("cas.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.HdrFormat },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        _glowExtract = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("glow_extract.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.GlowFormat },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        _glowBlur = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("glow_blur.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.GlowFormat },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        _post = Make(new GraphicsPipelineDesc
        {
            Vertex = fsVs, Fragment = _shaders.Get("post.frag", ShaderStage.Fragment), ColorFormats = new[] { RenderTargets.OutputFormat },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        // glow-accumulating blend: colour untouched, glow added to alpha (LLDrawPoolAlpha's emissive pass)
        _particleGlow = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("particle.vert", ShaderStage.Vertex), Fragment = _shaders.Get("particle_glow.frag", ShaderStage.Fragment),
            ColorFormats = new[] { RenderTargets.HdrFormat }, Blend = new[] { BlendMode.Additive }, WriteMasks = new[] { ColorComponentFlags.ABit },
            DepthFormat = RenderTargets.DepthFormat, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
    }

    /// <summary>LL_PART_BF_* (cast straight to LLRender::eBlendFactor by the viewer) to Vulkan; unknown values fall back
    /// to the default alpha blend's factors.</summary>
    static BlendFactor PartBlend(byte f, BlendFactor fallback) => f switch
    {
        0 => BlendFactor.One, 1 => BlendFactor.Zero, 2 => BlendFactor.DstColor, 3 => BlendFactor.SrcColor,
        4 => BlendFactor.OneMinusDstColor, 5 => BlendFactor.OneMinusSrcColor, 6 => BlendFactor.DstAlpha,
        7 => BlendFactor.SrcAlpha, 8 => BlendFactor.OneMinusDstAlpha, 9 => BlendFactor.OneMinusSrcAlpha, _ => fallback,
    };

    /// <summary>The particle pipeline for a blend pair: the system's colour factors, the alpha pool's glow suppression
    /// (ZERO, ONE_MINUS_SRC_ALPHA) on the alpha channel; no depth writes, no culling.</summary>
    Pipeline ParticlePipeline(int key)
    {
        if (_particlePipes.TryGetValue(key, out var p)) return p;
        var src = PartBlend((byte)(key >> 4), BlendFactor.SrcAlpha);
        var dst = PartBlend((byte)(key & 15), BlendFactor.OneMinusSrcAlpha);
        p = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("particle.vert", ShaderStage.Vertex), Fragment = _shaders.Get("particle.frag", ShaderStage.Fragment),
            ColorFormats = new[] { RenderTargets.HdrFormat },
            Blend0 = new PipelineColorBlendAttachmentState(true, src, dst, BlendOp.Add, BlendFactor.Zero, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
                ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit),
            DepthFormat = RenderTargets.DepthFormat, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        _particlePipes[key] = p;
        return p;
    }

    public const Format ShadowFormat = Format.D32Sfloat;

    /// <summary>(Re)create the cascade maps at this size and register them in the shadow sampler array.</summary>
    public void EnsureShadowMaps(uint width, uint height)
    {
        if (ShadowMaps[0] is { } m && m.Width == width && m.Height == height) return;
        _vk.WaitIdle();
        for (int i = 0; i < 4; i++)
        {
            ShadowMaps[i]?.Dispose();
            // cascade 0 also takes copies (a probe capture's shadow kept and put back: KeepCaptureShadow)
            ShadowMaps[i] = new GpuImage(_vk, width, height, ShadowFormat, ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit
                | (i == 0 ? ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit : 0), dedicated: true);
            _bindless.WriteShadowMap(i, ShadowMaps[i]!.View);
        }
        ShadowMapSize = width;
        _captureShadow?.Dispose();   // made again at the new size when a capture needs it
        _captureShadow = null;
        CaptureShadowVersion++;
    }

    /// <summary>Bumped whenever the kept capture shadow is gone (the maps made anew).</summary>
    public int CaptureShadowVersion { get; private set; }

    /// <summary>Cascade 0 (a probe capture's shadow just drawn there) kept for the pass's later frames.</summary>
    public bool KeepCaptureShadow(CommandBuffer cmd)
    {
        if (ShadowMaps[0] is not { } c0) return false;
        _captureShadow ??= new GpuImage(_vk, c0.Width, c0.Height, ShadowFormat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, dedicated: true);
        CopyImage(cmd, c0, _captureShadow, ImageLayout.ShaderReadOnlyOptimal);
        return true;
    }

    /// <summary>The kept capture shadow back into cascade 0, for the capture's faces this frame.</summary>
    public bool RestoreCaptureShadow(CommandBuffer cmd)
    {
        if (ShadowMaps[0] is not { } c0 || _captureShadow is not { } kept) return false;
        CopyImage(cmd, kept, c0, ImageLayout.ShaderReadOnlyOptimal);
        return true;
    }

    /// <summary>(Re)create the far sun cascade's map (SunShadowSetup.Far: square, coarse).</summary>
    public void EnsureFarShadowMap(uint size)
    {
        if (ShadowMaps[6] is { } m && m.Width == size) return;
        _vk.WaitIdle();
        ShadowMaps[6]?.Dispose();
        ShadowMaps[6] = new GpuImage(_vk, size, size, ShadowFormat, ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, dedicated: true);
        _bindless.WriteShadowMap(6, ShadowMaps[6]!.View);
        FarShadowDrawn++;   // what it held is gone
    }

    /// <summary>Counts the far cascade's drawings (and its map's remakings), so a kept map is known to be the one drawn
    /// (FrameRenderer.FarShadow).</summary>
    public long FarShadowDrawn;

    /// <summary>(Re)create the two projector shadow maps (mSpotShadow: square, screen width on a side).</summary>
    public void EnsureSpotShadowMaps(uint size)
    {
        if (ShadowMaps[4] is { } m && m.Width == size) return;
        _vk.WaitIdle();
        for (int i = 4; i < 6; i++)
        {
            ShadowMaps[i]?.Dispose();
            ShadowMaps[i] = new GpuImage(_vk, size, size, ShadowFormat, ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, dedicated: true);
            _bindless.WriteShadowMap(i, ShadowMaps[i]!.View);
        }
    }

    public ulong ShadowBytes => ShadowMaps.Aggregate(0UL, (s, m) => s + (m?.Bytes ?? 0)) + (_captureShadow?.Bytes ?? 0);

    // --- per-frame recording ---------------------------------------------------------------

    internal struct IndirectBatch
    {
        public int Arena;
        public ulong Offset;
        public uint Count;
        /// <summary>Blended faces: which of alpha.frag's paths the batch's materials take (AlphaPaths), 0 not split.</summary>
        public int Path;
    }

    /// <summary>Blended faces drawn with alpha.frag compiled for their material's path alone (ALPHA_PATH: PBR, fullbright,
    /// alphaF, materialF), the batches split where the path changes in their sorted order: the same code a fragment
    /// runs, fewer registers held for paths it doesn't take. SOJOURN_ALPHA_PATHS=0 for the one shader.</summary>
    public static bool AlphaPaths = System.Environment.GetEnvironmentVariable("SOJOURN_ALPHA_PATHS") != "0";
    readonly Pipeline[] _alphaPaths = new Pipeline[5];

    /// <summary>alpha.frag's path for a material (its branches on the flags, in order).</summary>
    static int AlphaPath(uint flags) =>
        (flags & MaterialFlags.Pbr) != 0 ? 1
        : (flags & MaterialFlags.HasLegacyMaterial) != 0 ? 4
        : (flags & MaterialFlags.Fullbright) != 0 ? 2 : 3;

    int PathOf(DrawList dl, uint instance) => AlphaPath(MaterialFlagsOf(dl, instance));

    /// <summary>A draw's material flags: its instance and material the frame's own, or the persistent tables'
    /// (Frame/PersistentTable.cs, their index's bit set).</summary>
    uint MaterialFlagsOf(DrawList dl, uint instance)
    {
        const uint Bit = PersistentTable<GpuInstance>.Bit;
        uint m = (instance & Bit) != 0 ? Persistent!.Value.Instances[(int)(instance & ~Bit)].Material : dl.Instances[(int)instance].Material;
        return (m & Bit) != 0 ? Persistent!.Value.Materials[(int)(m & ~Bit)].Flags : dl.Materials[(int)m].Flags;
    }

    void DrawAlphaBatches(CommandBuffer cmd, List<IndirectBatch> batches, bool preWater = false)
    {
        // both pools lit as the setting says (Renderer.Layers.cs)
        Pipeline For(int path) => preWater ? PreWaterPipeline(path) : AlphaPipeline(path);
        if (!AlphaPaths || batches.Count == 0 || batches[0].Path == 0)
        {
            Bind(cmd, For(0));
            DrawBatches(cmd, batches);
            return;
        }
        Push(cmd);
        int bound = -1;
        foreach (var b in batches)
        {
            if (b.Path != bound) { Bind(cmd, For(b.Path)); bound = b.Path; }
            DrawBatch(cmd, b);
        }
    }

    PushBlock _push;

    /// <summary>A draw list's data in the frame arena: device addresses and indirect batches, reusable by any
    /// number of views recorded in the same frame.</summary>
    public sealed class FrameDraws
    {
        public ulong Instances, Materials, Lights, Projectors, Palettes;
        public uint LightCount, ProjectorCount;
        internal List<IndirectBatch> Opaque = null!, TwoSided = null!, Fullbright = null!, Alpha = null!, Glow = null!, AlphaGlow = null!, Water = null!, WaterExclusion = null!;
        /// <summary>Each shadow map's casters: those shadow.frag can cut (alpha, water) and the solid rest.</summary>
        internal List<IndirectBatch>[] Shadow = null!, ShadowSolid = null!;
        internal List<AvatarAlphaCmd> AvatarAlpha = null!;
        internal List<IndirectBatch> RiggedAlpha = null!;
        /// <summary>Blended faces with a part on the far side of the water from the camera, drawn before the water with
        /// depth writes (the viewer's pre-water alpha pool); null when the frame has no water, and then
        /// <see cref="Alpha"/> holds all of them. <see cref="CameraUnderWater"/>: which side is far.</summary>
        internal List<IndirectBatch>? AlphaPreWater;
        internal float WaterHeight;
        internal bool CameraUnderWater;
        /// <summary>The blended faces nearest first, for the see-through layers' count (Renderer.Layers.cs); null without the cap.</summary>
        internal List<IndirectBatch>? AlphaNear;
        /// <summary>The alpha pass in depth order when particles are drawn: runs of blended faces and of particle
        /// quads interleaved far to near (null: blended faces only, in <see cref="Alpha"/>).</summary>
        internal List<AlphaStep>? AlphaSteps;
        internal ulong ParticleVertices;
        internal List<Particles.ParticleQuad> ParticleQuads = null!;
        internal List<CaptureDraws> Captures = null!;
        internal List<uint> Impostors = null!;
    }

    /// <summary>A stretch of the alpha pass: blended faces (Batches) or particle quads [Start, Start + Count).</summary>
    internal readonly record struct AlphaStep(List<IndirectBatch>? Batches, int Start, int Count);

    /// <summary>A view's camera/environment data in the frame arena.</summary>
    public readonly record struct ViewData(ulong View, ulong Sky);

    public static ulong ArenaBytes(DrawList dl, int views) => (ulong)(views * (sizeof(GpuView) + sizeof(GpuSky) + 1024) + 4096
        + dl.Instances.Count * sizeof(GpuInstance) + dl.Materials.Count * sizeof(GpuMaterial)
        + dl.Lights.Count * sizeof(GpuLight) + dl.Projectors.Count * sizeof(GpuProjector) + dl.TotalDraws * sizeof(DrawIndexedIndirect)
        + dl.Palettes.Count * sizeof(Matrix4x4) + 64 * 256
        + dl.ParticleVertices.Count * sizeof(Particles.ParticleVertex) + (dl.ParticleQuads.Count > 0 ? dl.Alpha.Count * sizeof(DrawIndexedIndirect) + 256 : 0))
        + CaptureArenaBytes(dl) + FlexiArenaBytes(dl) + (ulong)(dl.ParcelLines.Count * sizeof(World.ParcelLineVertex) + 256);

    /// <summary>Flexible prims' vertices this frame and one block of unit weights for all of them.</summary>
    static ulong FlexiArenaBytes(DrawList dl)
    {
        if (dl.FlexiCopies.Count == 0) return 0;
        ulong bytes = 512;
        int max = 0;
        foreach (var c in dl.FlexiCopies)
        {
            bytes += (ulong)c.Used * PipelineBuilder.VertexStride + 256;
            if (c.Weights == 0) max = Math.Max(max, c.Used);
        }
        return bytes + (ulong)max * (ulong)sizeof(SkinWeight);
    }

    readonly List<SkinJob> _flexiJobs = new();

    /// <summary>Flexible prims' vertices into the frame arena, each a skinning job on one identity joint (World/
    /// RegionScene.Flexi.cs): the pre-pass copies them into the frame's skinned vertex buffer with the rest.</summary>
    void UploadFlexi(FrameArena arena, DrawList dl, ulong baseAddr)
    {
        _flexiJobs.Clear();
        if (dl.FlexiCopies.Count == 0) return;
        int max = 0;
        foreach (var c in dl.FlexiCopies) if (c.Weights == 0) max = Math.Max(max, c.Used);
        ulong woff = 0;
        if (max > 0) arena.Alloc<SkinWeight>(max, out woff).Fill(new SkinWeight { W01 = 0xFFFF });
        foreach (var c in dl.FlexiCopies)
        {
            ulong off = arena.Write<MeshVertex>(c.Vertices.AsSpan(0, c.Used));
            _flexiJobs.Add(new SkinJob(baseAddr + off, c.Weights != 0 ? c.Weights : baseAddr + woff, c.Palette, c.Dest, (uint)c.Used));
        }
    }

    /// <summary>SOJOURN_PRE_WATER=0: every blended face after the water, unclipped, as before (for comparing).</summary>
    static readonly bool PreWaterOff = System.Environment.GetEnvironmentVariable("SOJOURN_PRE_WATER") == "0";

    public FrameDraws Upload(FrameArena arena, DrawList dl, (float Height, bool CameraUnder)? water = null)
    {
        ulong baseAddr = arena.Device.Address;
        // with water in view, the blended faces split by its plane as the viewer's two alpha pools do
        // (LLDrawPoolAlpha::renderAlpha: a face goes in each pool its bounds reach into, clipped there by waterSign)
        var alpha = dl.Alpha;
        List<DrawCmd>? preWater = null;
        if (water is { } w && dl.Water.Count > 0 && !PreWaterOff)
        {
            preWater = new List<DrawCmd>();
            alpha = new List<DrawCmd>(dl.Alpha.Count);
            // (sorted as BuildBatches sorts: the see-through layers' count reads dl.Alpha far to near, Renderer.Layers.cs)
            dl.Alpha.Sort((a, b) => b.Depth != a.Depth ? b.Depth.CompareTo(a.Depth)
                : b.SubDepth != a.SubDepth ? b.SubDepth.CompareTo(a.SubDepth) : a.Instance.CompareTo(b.Instance));
            foreach (var c in dl.Alpha)
            {
                // by the face's own box where it's known (each pool's clip in alpha.frag drops everything on the other
                // side, so a face that can't reach a side draws nothing there), else by its object's sphere; a draw
                // with no bounds at all (an avatar's loading cloud) in both
                float reach = c.HalfHeight > 0 ? c.HalfHeight : c.Bounds.W;
                bool unknown = c.HalfHeight == 0 && c.Bounds == Vector4.Zero;
                bool below = unknown || c.Bounds.Z - reach < w.Height, above = unknown || c.Bounds.Z + reach > w.Height;
                if (w.CameraUnder ? above : below) preWater.Add(c);
                if (w.CameraUnder ? below : above) alpha.Add(c);
            }
        }
        var d = new FrameDraws
        {
            Instances = baseAddr + arena.Write<GpuInstance>(CollectionsMarshal.AsSpan(dl.Instances)),
            Materials = baseAddr + arena.Write<GpuMaterial>(CollectionsMarshal.AsSpan(dl.Materials)),
            Lights = baseAddr + arena.Write<GpuLight>(CollectionsMarshal.AsSpan(dl.Lights)),
            LightCount = (uint)dl.Lights.Count,
            Projectors = baseAddr + arena.Write<GpuProjector>(CollectionsMarshal.AsSpan(dl.Projectors)),
            ProjectorCount = (uint)dl.Projectors.Count,
            Palettes = baseAddr + arena.Write<Matrix4x4>(CollectionsMarshal.AsSpan(dl.Palettes)),
            Opaque = BuildBatches(arena, dl.Opaque, sort: false),
            TwoSided = BuildBatches(arena, dl.OpaqueTwoSided, sort: false),
            Fullbright = BuildBatches(arena, dl.Fullbright, sort: false),
            Alpha = BuildBatches(arena, alpha, sort: true, paths: dl),
            AlphaPreWater = preWater != null ? BuildBatches(arena, preWater, sort: true, paths: dl) : null,
            WaterHeight = water?.Height ?? 0, CameraUnderWater = water?.CameraUnder ?? false,
            Glow = BuildBatches(arena, dl.Glow, sort: false),
            AlphaGlow = BuildBatches(arena, dl.AlphaGlow, sort: true),
            Water = BuildBatches(arena, dl.Water, sort: false),
            WaterExclusion = BuildBatches(arena, dl.WaterExclusion, sort: false),
            RiggedAlpha = BuildBatches(arena, dl.RiggedAlpha, sort: false, keepOrder: true),
        };
        d.AlphaNear = NearFirst(arena, dl);   // dl.Alpha was sorted far to near above (Renderer.Layers.cs)
        d.Shadow = new List<IndirectBatch>[dl.ShadowCasters.Length];
        d.ShadowSolid = new List<IndirectBatch>[dl.ShadowCasters.Length];
        for (int c = 0; c < dl.ShadowCasters.Length; c++)
        {
            SplitCasters(dl, dl.ShadowCasters[c]);
            d.Shadow[c] = BuildBatches(arena, _cut, sort: false);
            d.ShadowSolid[c] = BuildBatches(arena, _solid, sort: false);
        }
        d.Captures = UploadCaptures(arena, dl);
        d.Impostors = dl.Impostors;
        d.ParticleQuads = dl.ParticleQuads;
        if (dl.ParticleQuads.Count > 0)
        {
            d.ParticleVertices = baseAddr + arena.Write<Particles.ParticleVertex>(CollectionsMarshal.AsSpan(dl.ParticleVertices));
            d.AlphaSteps = InterleaveAlpha(arena, dl, alpha, dl.ParticleQuads);   // alpha was sorted far to near above
        }
        // far to near, each avatar's meshes in their own order (skirt, eyelashes, hair)
        d.AvatarAlpha = dl.AvatarAlpha.Select((a, i) => (a, i)).OrderByDescending(x => x.a.Cmd.Depth).ThenBy(x => x.i).Select(x => x.a).ToList();
        return d;
    }

    public ViewData WriteView(FrameArena arena, ViewSetup v, uint width, uint height, RenderTargets? tiles = null)
    {
        ulong baseAddr = arena.Device.Address;
        return new ViewData(baseAddr + arena.Write(MakeView(v, width, height, tiles)), baseAddr + arena.Write(v.Sky));
    }

    /// <summary>The eyes' views and skies, each kind side by side (a multiview pass reads its eye's by gl_ViewIndex).</summary>
    ViewData[] WriteViews(FrameArena arena, IReadOnlyList<Eye> eyes)
    {
        var views = new GpuView[eyes.Count];
        var skies = new GpuSky[eyes.Count];
        for (int i = 0; i < eyes.Count; i++)
        {
            var (v, rt) = eyes[i];
            if (LightTiles) rt.EnsureLightTiles();   // Renderer.Tiles.cs
            views[i] = MakeView(v, rt.Width, rt.Height, LightTiles ? rt : null);
            if (UsesLens(v, rt)) views[i].LensMask = (uint)rt.LensMaskSlot + 1;   // Renderer.Lens.cs
            LayerView(ref views[i], rt);   // Renderer.Layers.cs
            skies[i] = v.Sky;
        }
        ulong baseAddr = arena.Device.Address;
        ulong view0 = baseAddr + arena.Write<GpuView>(views), sky0 = baseAddr + arena.Write<GpuSky>(skies);
        var vds = new ViewData[eyes.Count];
        for (int i = 0; i < vds.Length; i++) vds[i] = new ViewData(view0 + (ulong)(i * sizeof(GpuView)), sky0 + (ulong)(i * sizeof(GpuSky)));
        return vds;
    }

    static GpuView MakeView(ViewSetup v, uint width, uint height, RenderTargets? tiles)
    {
        Matrix4x4.Invert(v.View, out var invView);
        Matrix4x4.Invert(v.Proj, out var invProj);
        var viewProj = v.View * v.Proj;
        Matrix4x4.Invert(viewProj, out var invViewProj);
        var gv = new GpuView
        {
            View = v.View, Proj = v.Proj, ViewProj = viewProj, InvView = invView, InvProj = invProj, InvViewProj = invViewProj,
            CameraPos = new Vector4(v.Position, v.Near),
            Viewport = new Vector4(width, height, 1f / width, 1f / height),
            SunDir = new Vector4(v.SunDirRegion, v.SunUp ? 1 : 0),
            FarClip = new Vector4(v.DrawDistance, v.WaterHeight, v.Time, v.HasWater ? 1 : 0),
            ClipPlane = v.ClipPlane, Mask = v.Mask,
            TilesLo = (uint)(tiles?.LightTiles?.Address ?? 0), TilesHi = (uint)((tiles?.LightTiles?.Address ?? 0) >> 32), TileCols = tiles?.TileCols ?? 0,
        };
        return gv;
    }

    /// <summary>One eye of a frame: its camera and environment, and the targets it renders through.</summary>
    public readonly record struct Eye(ViewSetup View, RenderTargets Targets);

    /// <summary>Upload one draw list and record a frame: shared work once (skinning, probe captures via
    /// <paramref name="beforeScene"/>, sun and projector shadow maps), then each eye's scene and post. Each eye's
    /// output goes to <paramref name="present"/> (left in TransferSrcOptimal), or to the frame's readback buffer when
    /// there is none (one eye). Eye views must share the first eye's shadow setup (see FrameRenderer). A preview (the
    /// handheld camera's, Renderer.Preview.cs) is drawn before the eyes, from the same draw list and shadows.</summary>
    public ulong Record(FrameResources fr, IReadOnlyList<Eye> eyes, DrawList dl, Uploader uploads,
        Func<FrameArena, int>? prepareViews = null, Action<CommandBuffer>? beforeScene = null, ulong extraArenaBytes = 0,
        Action<CommandBuffer, int, GpuImage>? present = null, IReadOnlyList<MirrorView?>? mirrors = null, PreviewView? preview = null)
    {
        var cmd = fr.Cmd;
        var arena = fr.Arena;
        var previewMirror = preview?.Mirror;   // (its reflection, Renderer.Mirror.cs)
        BeginExposureFrame();
        ResolveAlpha();   // Renderer.Layers.cs
        arena.Reset();
        arena.Reserve(ArenaBytes(dl, eyes.Count + (preview != null ? 1 : 0)) + extraArenaBytes + (mirrors != null ? MirrorArenaBytes(dl, mirrors.Count) : 0) + (previewMirror != null ? MirrorArenaBytes(dl, 1) : 0) + LensArenaBytes(eyes) + LayerArenaBytes(dl) + PersistentBytes + BeaconArenaBytes(dl));
        var v0 = eyes[0].View;
        var draws = Upload(arena, dl, v0.HasWater ? (v0.WaterHeight, v0.Sky.camera_under_water != 0) : null);
        UploadFlexi(arena, dl, arena.Device.Address);   // (the main list's alone: skinned once a frame, below)
        UploadParcelLines(arena, dl);   // Renderer.ParcelLines.cs
        UploadBeacons(arena, dl, draws);   // Renderer.Beacons.cs
        prepareViews?.Invoke(arena);   // probe captures' data, and the view's probe list (into v.Sky)
        PlanCascades(eyes, previewMirror != null ? [.. mirrors ?? [], previewMirror] : mirrors, preview?.View);   // Renderer.Cascades.cs
        var vds = WriteViews(arena, eyes);
        PrepareLensMasks(arena, eyes);   // Renderer.Lens.cs
        var mvds = mirrors != null ? WriteMirrorViews(arena, dl, mirrors) : null;   // Renderer.Mirror.cs
        var pvd = preview != null ? WriteView(arena, preview.View, preview.Targets.Width, preview.Targets.Height) : default;
        var pmvd = previewMirror != null ? WritePreviewMirror(arena, dl, previewMirror) : default;

        // the frame's GPU time includes its uploads and mip chains (the load governor weighs loading by it)
        Profiler.Begin(fr, cmd);
        ulong uploadMark = uploads.Record(cmd);
        Profiler.Mark("uploads");   // (loading's copies and mip chains alone: the governor paces them by this)
        Profiler.UploadBytes(uploads.RecordedBytes);
        arena.Flush(cmd);
        RecordPersistent(cmd, arena);
        Profiler.Mark("frame data");
        var set = _bindless.Set;
        _vk.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, Layout, 0, 1, in set, 0, null);
        RecordSkinning(cmd, fr, dl, draws.Palettes);
        Profiler.Mark("skinning");
        RecordLensMasks(cmd, eyes, vds);   // Renderer.Lens.cs
        RecordCaptures(cmd, draws, vds[0].Sky);
        Profiler.Suspended = true;   // probe captures reuse RecordScene; time them as one block
        beforeScene?.Invoke(cmd);
        Profiler.Suspended = false;
        Profiler.Mark("probes");

        RecordShadows(cmd, draws, vds[0], eyes[0].View);
        _cascadesReused = 0;   // only this frame's main shadows keep a cascade (Renderer.Cascades.cs)
        if (preview != null)
        {
            if (previewMirror != null) RecordMirror(cmd, draws, pmvd, previewMirror, _previewMirrorDraws);
            RecordPreview(fr, draws, pvd, preview);   // Renderer.Preview.cs
        }
        if (eyes.Count == 2 && eyes[0].Targets.Stereo is { } st && eyes[1].Targets.Stereo == st && Multiview)
        {
            // both eyes at once where the passes draw geometry, one at a time where they light and finish an eye
            for (int i = 0; i < 2; i++)
                if (mirrors != null && i < mirrors.Count && mirrors[i] is { } mv) RecordMirror(cmd, draws, mvds![i], mv, _eyesMirrorDraws);
            RecordStereoScene(cmd, draws, vds, eyes, st);
            for (int i = 0; i < 2; i++)
            {
                int eye = i;
                if (i > 0) BindBindless(cmd);
                PushView(draws, vds[i]);   // the eye's own view and sky, as its scene left them in the eyes' turn
                RecordPost(fr, eyes[i].View, eyes[i].Targets, present == null ? null : (c, img) => present(c, eye, img));
            }
            return uploadMark;
        }
        for (int i = 0; i < eyes.Count; i++)
        {
            var (v, rt) = eyes[i];
            if (i > 0) BindBindless(cmd);
            if (mirrors != null && i < mirrors.Count && mirrors[i] is { } mv) RecordMirror(cmd, draws, mvds![i], mv, _eyesMirrorDraws);
            _tiles = rt;
            RecordScene(cmd, draws, vds[i], v, rt, shadows: false, cubeSnapshot: false);
            _tiles = null;
            RecordPost(fr, v, rt, present == null ? null : (c, img) => present(c, i, img));
        }
        return uploadMark;
    }

    /// <summary>The bindless set again before the next eye: the last eye's present (the overlay, the hands' cut) bound
    /// sets of its own layouts, which leave set 0 incompatible with the renderer's pipelines (the validation layer:
    /// VUID-vkCmdDraw-None-08600, and reads through the overlay's set; 2026-10-29).</summary>
    void BindBindless(CommandBuffer cmd)
    {
        var set = _bindless.Set;
        _vk.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, Layout, 0, 1, in set, 0, null);
    }

    ulong PersistentBytes => Persistent is var (pi, pm) ? pi.PendingBytes + pm.PendingBytes : 0;

    /// <summary>This frame's writes to the persistent tables, before anything draws (from the arena's upload side: after
    /// its flush is fine).</summary>
    void RecordPersistent(CommandBuffer cmd, FrameArena arena)
    {
        if (Persistent is not var (pi, pm)) return;
        pi.Record(cmd, arena);
        pm.Record(cmd, arena);
    }

    /// <summary>Record a frame of probe captures only (no view): arena data from <paramref name="prepare"/>, then
    /// <paramref name="record"/>. Returns the upload mark like <see cref="Record"/>.</summary>
    public ulong RecordCaptures(FrameResources fr, Uploader uploads, ulong arenaBytes, Action<FrameArena> prepare, Action<CommandBuffer> record)
    {
        var cmd = fr.Cmd;
        var arena = fr.Arena;
        arena.Reset();
        arena.Reserve(arenaBytes + PersistentBytes);
        prepare(arena);
        ulong uploadMark = uploads.Record(cmd);
        arena.Flush(cmd);
        RecordPersistent(cmd, arena);
        var set = _bindless.Set;
        _vk.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, Layout, 0, 1, in set, 0, null);
        // the profiler isn't begun on this command buffer: its marks would land in the last view's (already submitted) one
        bool suspended = Profiler.Suspended;
        Profiler.Suspended = true;
        try { record(cmd); }
        finally { Profiler.Suspended = suspended; }
        return uploadMark;
    }

    /// <summary>Render one sun shadow map (cascade slot <paramref name="map"/>, caster list of the same index) for a
    /// view whose sky holds the light matrix (probe captures: one map covering the whole capture).</summary>
    public void RecordShadowMap(CommandBuffer cmd, FrameDraws draws, ViewData vd, int map)
    {
        var m = ShadowMaps[map];
        if (m == null) return;
        _push = default;
        _push.View = vd.View;
        _push.Sky = vd.Sky;
        _push.Instances = draws.Instances;
        _push.Materials = draws.Materials;
        _push.Lights = draws.Lights;
        _push.I0 = (uint)map;
        m.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
        BeginShadowRendering(cmd, m);
        DrawCasters(cmd, draws, map, spot: false);
        _vk.Api.CmdEndRendering(cmd);
        m.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
    }

    /// <summary>The sun cascades and projector shadow maps a view's sky asks for (shared by both eyes).</summary>
    public void RecordShadows(CommandBuffer cmd, FrameDraws draws, ViewData vd, ViewSetup v)
    {
        var api = _vk.Api;
        _push = default;
        _push.View = vd.View;
        _push.Sky = vd.Sky;
        _push.Instances = draws.Instances;
        _push.Materials = draws.Materials;
        _push.Lights = draws.Lights;
        var dlLightCount = draws.LightCount;
        // --- sun shadow cascades
        if (v.Sky.shadows_on != 0 && ShadowMaps[0] != null)
        {
            for (int c = 0; c < 4; c++)
            {
                if ((_cascadesReused & (1 << c)) != 0) continue;   // last frame's map (Renderer.Cascades.cs)
                var map = ShadowMaps[c]!;
                map.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
                BeginShadowRendering(cmd, map);
                _push.I0 = (uint)c;
                DrawCasters(cmd, draws, c, spot: false);
                api.CmdEndRendering(cmd);
                map.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
                if (ProfileCascades) Profiler.Mark($"cascade {c}");
            }
            if (v.Sky.shadow_far.W > 0 && !v.FarShadowKept && ShadowMaps[6] is { } far)
            {
                far.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
                BeginShadowRendering(cmd, far);
                _push.I0 = ViewSetup.FarCascade;
                DrawCasters(cmd, draws, ViewSetup.FarCascade, spot: false);
                api.CmdEndRendering(cmd);
                far.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
                FarShadowDrawn++;
                if (ProfileCascades) Profiler.Mark("cascade far");
            }
            _push.I0 = dlLightCount;
        }
        if (v.Sky.spot_shadows != 0 && ShadowMaps[4] != null)
        {
            for (int i = 0; i < 2; i++)
            {
                if ((v.Sky.spot_shadows & (1 << i)) == 0) continue;
                var map = ShadowMaps[4 + i]!;
                map.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
                BeginShadowRendering(cmd, map);
                _push.I0 = (uint)(4 + i);
                DrawCasters(cmd, draws, 4 + i, spot: true);
                api.CmdEndRendering(cmd);
                map.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
            }
            _push.I0 = dlLightCount;
        }
        Profiler.Mark("shadows");
    }

    /// <summary>Everything up to the composed HDR scene (shadows, G-buffer, lighting, forward passes, haze, alpha).</summary>
    /// <summary>LLVOWLSky::drawStars draws getStarsNumVerts() * 4 of its 6000 vertices.</summary>
    const uint StarVertices = 4000;

    public void RecordScene(CommandBuffer cmd, FrameDraws draws, ViewData vd, ViewSetup v, RenderTargets rt, bool shadows, bool? cubeSnapshot = null)
    {
        bool isCubeSnapshot = cubeSnapshot ?? !shadows;   // probe captures pass shadows: false
        PushView(draws, vd);
        if (shadows) RecordShadows(cmd, draws, vd, v);
        GBufferPass(cmd, draws, v, rt, isCubeSnapshot);
        LightingPass(cmd, draws, vd, v, rt);
        ForwardPass(cmd, draws, rt);
        if (!WaterPass(cmd, draws, v, rt)) HazePass(cmd, rt);   // under water, the water pass draws the haze itself
        AlphaPass(cmd, draws, rt);
    }

    /// <summary>The headset's eyes through a stereo set (RenderTargets.Eyes): the G-buffer, forward and alpha passes
    /// draw both at once (multiview: the eyes' views side by side, each read by its gl_ViewIndex), the lighting, water
    /// and haze passes go an eye at a time through its own layer. The same passes, in the same order for each eye, as
    /// two RecordScenes.</summary>
    void RecordStereoScene(CommandBuffer cmd, FrameDraws draws, ViewData[] vds, IReadOnlyList<Eye> eyes, RenderTargets stereo)
    {
        if (MultiviewGBuffer)
        {
            PushView(draws, vds[0]);
            _viewMask = 0b11;
            GBufferPass(cmd, draws, eyes[0].View, stereo, isCubeSnapshot: false);
            _viewMask = 0;
        }
        else
            for (int i = 0; i < 2; i++)
            {
                PushView(draws, vds[i]);
                GBufferPass(cmd, draws, eyes[i].View, eyes[i].Targets, isCubeSnapshot: false);
            }
        for (int i = 0; i < 2; i++)
        {
            PushView(draws, vds[i]);
            _tiles = eyes[i].Targets;
            LightingPass(cmd, draws, vds[i], eyes[i].View, eyes[i].Targets);
            _tiles = null;
        }
        PushView(draws, vds[0]);
        _viewMask = 0b11;
        ForwardPass(cmd, draws, stereo);
        _viewMask = 0;
        for (int i = 0; i < 2; i++)
        {
            PushView(draws, vds[i]);
            if (!WaterPass(cmd, draws, eyes[i].View, eyes[i].Targets)) HazePass(cmd, eyes[i].Targets);
        }
        PushView(draws, vds[0]);
        _viewMask = 0b11;
        AlphaPass(cmd, draws, stereo);
        _viewMask = 0;
    }

    void PushView(FrameDraws draws, ViewData vd)
    {
        _push = default;
        _push.View = vd.View;
        _push.Sky = vd.Sky;
        _push.Instances = draws.Instances;
        _push.Materials = draws.Materials;
        _push.Lights = draws.Lights;
        _push.I0 = draws.LightCount;
    }

    /// <summary>Opaque and masked faces, impostors, then the sky and stars where nothing was drawn.</summary>
    void GBufferPass(CommandBuffer cmd, FrameDraws draws, ViewSetup v, RenderTargets rt, bool isCubeSnapshot)
    {
        var api = _vk.Api;
        var bindless = _bindless.Set;   // the previous eye's overlay bound a set of its own
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, Layout, 0, 1, in bindless, 0, null);
        rt.Diffuse.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        rt.Specular.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        rt.Normal.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        rt.Emissive.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        rt.Depth.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
        // no colour clear: every pixel gets the geometry's values or, where there's none, the sky's (all four targets)
        BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.Diffuse, rt.Specular, rt.Normal, rt.Emissive }, rt.Depth, clearColor: !GBufferNoClear, clearDepth: true, overwrite: GBufferNoClear);
        _push.I1 = (uint)(v.CloudNoiseSlotA < 0 ? 0xFFFF : v.CloudNoiseSlotA) | ((uint)(v.CloudNoiseSlotB < 0 ? 0xFFFF : v.CloudNoiseSlotB) << 16);
        Bind(cmd, _gbuffer);
        DrawBatches(cmd, draws.Opaque);
        Bind(cmd, _gbufferTwoSided);
        DrawBatches(cmd, draws.TwoSided);
        if (!_mirrorPass) DrawImpostors(cmd, draws);   // captured from the eyes' side
        Profiler.Mark("gbuffer");
        // the sky after the geometry, depth-tested at the far plane, so it shades only the pixels nothing covers (the
        // viewer draws it first; nothing in the G-buffer blends with it, so the result is the same)
        Bind(cmd, _sky);
        Push(cmd);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        // stars (renderStarsDeferred): not in cube snapshots, i.e. probe captures
        if (!isCubeSnapshot && (v.Sky.heavenly_flags & GpuSky.StarsFlag) != 0 && v.Sky.star_tex >= 0)
        {
            Bind(cmd, _stars);
            api.CmdDraw(cmd, StarVertices, 1, 0, 0);
        }
        api.CmdEndRendering(cmd);
        Profiler.Mark("sky");
    }

    /// <summary>The deferred resolve of one view: light tiles, the sun shadow and SSAO light map and its blur, sun and
    /// ambient, local lights, projectors, into the HDR scene.</summary>
    void LightingPass(CommandBuffer cmd, FrameDraws draws, ViewData vd, ViewSetup v, RenderTargets rt)
    {
        var api = _vk.Api;
        var dlLightCount = draws.LightCount;
        rt.Diffuse.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        rt.Specular.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        rt.Normal.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        rt.Emissive.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        rt.Depth.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        RecordTileCull(cmd, draws, vd, rt);   // Renderer.Tiles.cs
        _push.I1 = (uint)rt.DiffuseSlot | ((uint)rt.SpecularSlot << 16);
        _push.I2 = (uint)rt.NormalSlot | ((uint)rt.EmissiveSlot << 16);

        // direct-lighting light map: sun shadow + SSAO, then the viewer's two blur passes (at half size for the main eyes
        // when asked: Renderer.HalfLight.cs)
        if ((_abHalfLight ?? HalfLight) && v.Mask == ViewMask.Main && v.LodView == null && rt.Width >= 64 && rt.Height >= 64)
            RecordHalfLight(cmd, v, rt);
        else
            FullLight(cmd, v, rt);
        _push.V0 = Vector4.Zero;

        rt.Hdr.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        _push.I3 = (uint)rt.DepthSlot | ((uint)rt.LightMapSlot << 16);
        BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.Hdr }, null, clearColor: true, clearDepth: false);
        Bind(cmd, _light);
        Push(cmd);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        Profiler.Mark("sun+ambient");
        LightingRest(cmd, draws, v, rt, dlLightCount);
    }

    void FullLight(CommandBuffer cmd, ViewSetup v, RenderTargets rt)
    {
        var api = _vk.Api;
        rt.LightMap.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        _push.I3 = (uint)rt.DepthSlot;
        BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.LightMap }, null, clearColor: false, clearDepth: false);
        Bind(cmd, _sunlight);
        Push(cmd);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
        Profiler.Mark("sunlight");
        if (v.Sky.ssao_on != 0)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var src = pass == 0 ? rt.LightMap : rt.LightMapTemp;
                var dst = pass == 0 ? rt.LightMapTemp : rt.LightMap;
                src.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
                dst.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
                _push.I3 = (uint)rt.DepthSlot | ((uint)(pass == 0 ? rt.LightMapSlot : rt.LightMapTempSlot) << 16);
                _push.V0 = pass == 0 ? new Vector4(1, 0, 0, 0) : new Vector4(0, 1, 0, 0);
                BeginRendering(cmd, rt.Width, rt.Height, new[] { dst }, null, false, false);
                Bind(cmd, _blurlight);
                Push(cmd);
                api.CmdDraw(cmd, 3, 1, 0, 0);
                api.CmdEndRendering(cmd);
            }
        }
        rt.LightMap.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        _push.V0 = Vector4.Zero;
        Profiler.Mark("ssao blur");
    }

    /// <summary>Local lights and projectors, in the sun and ambient pass's rendering (still open).</summary>
    void LightingRest(CommandBuffer cmd, FrameDraws draws, ViewSetup v, RenderTargets rt, uint dlLightCount)
    {
        var api = _vk.Api;
        if (dlLightCount > 0)
        {
            Bind(cmd, _localLights);
            Push(cmd);
            api.CmdDraw(cmd, 3, 1, 0, 0);
        }
        Profiler.Mark("local lights");
        if (draws.ProjectorCount > 0)
        {
            // renderDeferredLighting's projector passes, after the point lights
            _push.Lights = draws.Projectors;
            _push.I0 = draws.ProjectorCount;
            Bind(cmd, _projectors);
            Push(cmd);
            api.CmdDraw(cmd, 3, 1, 0, 0);
            _push.Lights = draws.Lights;
            _push.I0 = dlLightCount;
        }
        api.CmdEndRendering(cmd);
        Profiler.Mark("projectors");
    }

    /// <summary>Forward geometry over the lit scene, depth from the G-buffer pass (viewer pool order: fullbright, glow,
    /// the avatar pool's post-deferred pass).</summary>
    void ForwardPass(CommandBuffer cmd, FrameDraws draws, RenderTargets rt)
    {
        var api = _vk.Api;
        rt.Hdr.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        rt.Depth.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
        BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.Hdr }, rt.Depth, clearColor: false, clearDepth: false);
        Bind(cmd, _fullbright);
        DrawBatches(cmd, draws.Fullbright);
        Bind(cmd, _glow);
        api.CmdSetDepthBias(cmd, 1f, 0f, 1f);   // glPolygonOffset(-1, -1) toward the camera; reverse-Z flips the sign
        DrawBatches(cmd, draws.Glow);
        // the avatar pool's post-deferred pass (renderTransparent), before water and the alpha pools
        var avatarAlpha = _mirrorPass ? _md.AvatarAlpha ?? draws.AvatarAlpha : draws.AvatarAlpha;
        if (avatarAlpha.Count > 0)
        {
            Push(cmd);
            foreach (var a in avatarAlpha)
            {
                if (a.Large)
                {
                    Bind(cmd, _avatarAlphaNoCull); DrawSingle(cmd, a.Cmd);
                    Bind(cmd, _avatarAlphaDepth); DrawSingle(cmd, a.Cmd);
                    Bind(cmd, _avatarAlphaNoCull); DrawSingle(cmd, a.Cmd);
                }
                else
                {
                    Bind(cmd, _avatarAlphaBack); DrawSingle(cmd, a.Cmd);
                    Bind(cmd, _avatarAlphaFront); DrawSingle(cmd, a.Cmd);
                }
            }
        }
        api.CmdEndRendering(cmd);
        Profiler.Mark("fullbright");
    }

    /// <summary>Atmospheric haze over one view's scene (reads its depth).</summary>
    void HazePass(CommandBuffer cmd, RenderTargets rt)
    {
        var api = _vk.Api;
        _push.I2 = (uint)rt.NormalSlot | ((uint)rt.EmissiveSlot << 16);
        _push.I3 = (uint)rt.DepthSlot | ((uint)rt.LightMapSlot << 16);
        rt.Depth.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        rt.Hdr.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.Hdr }, null, clearColor: false, clearDepth: false);
        Bind(cmd, _haze);
        Push(cmd);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
        Profiler.Mark("haze");
    }

    /// <summary>The alpha pools: rigged blended faces, blended faces and particles far to near, their glow.</summary>
    void AlphaPass(CommandBuffer cmd, FrameDraws draws, RenderTargets rt)
    {
        var api = _vk.Api;
        RecordLayerCount(cmd, draws, rt);   // Renderer.Layers.cs
        rt.Hdr.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        rt.Depth.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
        BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.Hdr }, rt.Depth, clearColor: false, clearDepth: false);
        if (draws.RiggedAlpha.Count > 0)
        {
            Bind(cmd, _riggedAlpha);
            DrawBatches(cmd, draws.RiggedAlpha);
        }
        // the post-water pool keeps to the camera's side of the water when the pre-water one took the other
        if (!_mirrorPass && draws.AlphaPreWater != null && draws.Water.Count > 0)
            _push.V1 = new Vector4(0, 0, draws.CameraUnderWater ? -1 : 1, draws.WaterHeight);
        // particles among the blended faces; in a mirror, only a dynamic one's (cube snapshots of dynamic probes take
        // them), turned to face the mirrored eye (Renderer.Mirror.cs)
        if ((_mirrorPass ? _md.Steps : draws.AlphaSteps) is { } steps)
        {
            ulong verts = _mirrorPass ? _md.ParticleVertices : draws.ParticleVertices;
            var quads = _mirrorPass ? _md.Quads : draws.ParticleQuads;
            foreach (var step in steps)
            {
                if (step.Batches != null) DrawAlphaBatches(cmd, step.Batches);
                else DrawParticles(cmd, verts, quads, step);
            }
        }
        else
        {
            DrawAlphaBatches(cmd, _mirrorPass ? _md.Alpha ?? draws.Alpha : draws.Alpha);
        }
        _push.V1 = Vector4.Zero;
        Bind(cmd, _glow);
        api.CmdSetDepthBias(cmd, 0f, 0f, 0f);   // the alpha pool's glow draws have no polygon offset
        DrawBatches(cmd, draws.AlphaGlow);
        api.CmdEndRendering(cmd);
        Profiler.Mark("alpha");
    }

    readonly List<DrawCmd> _cut = new(), _solid = new();

    /// <summary>A shadow map's casters into those shadow.frag can discard from (blended or masked, water) and the rest,
    /// whose depth needs no fragment shader. Order within a map doesn't matter: depth keeps the nearest.</summary>
    void SplitCasters(DrawList dl, List<DrawCmd> casters)
    {
        _cut.Clear();
        _solid.Clear();
        foreach (var d in CollectionsMarshal.AsSpan(casters))
        {
            uint f = MaterialFlagsOf(dl, d.Instance);
            uint mode = f & MaterialFlags.AlphaModeMask;
            // shadow.frag: water is dropped; avatars cut only blended faces; other faces cut masked and blended ones
            bool cuts = (f & MaterialFlags.Water) != 0 || mode == MaterialFlags.AlphaBlend
                        || (mode == MaterialFlags.AlphaMask && (f & MaterialFlags.Avatar) == 0);
            (cuts ? _cut : _solid).Add(d);
        }
    }

    /// <summary>One shadow map's casters, solid ones first (depth alone), then those the fragment shader cuts.</summary>
    void DrawCasters(CommandBuffer cmd, FrameDraws draws, int map, bool spot)
    {
        if (draws.ShadowSolid[map].Count > 0)
        {
            Bind(cmd, !SolidShadows ? (spot ? _spotShadow : _shadow) : spot ? _spotShadowSolid : _shadowSolid);
            DrawBatches(cmd, draws.ShadowSolid[map]);
        }
        if (draws.Shadow[map].Count > 0)
        {
            Bind(cmd, spot ? _spotShadow : _shadow);
            DrawBatches(cmd, draws.Shadow[map]);
        }
    }

    /// <summary>Merge sorted blended faces and sorted particle quads far to near (the viewer sorts alpha groups,
    /// particle groups among them, by distance along the camera axis; both here by distance from the head's centre).</summary>
    List<AlphaStep> InterleaveAlpha(FrameArena arena, DrawList dl, List<DrawCmd> alpha, List<Particles.ParticleQuad> quads)
    {
        var steps = new List<AlphaStep>();
        int i = 0, q = 0;
        while (i < alpha.Count || q < quads.Count)
        {
            if (q < quads.Count && (i >= alpha.Count || quads[q].Depth >= alpha[i].Depth))
            {
                int q0 = q;
                float limit = i < alpha.Count ? alpha[i].Depth : float.NegativeInfinity;
                while (q < quads.Count && quads[q].Depth >= limit) q++;
                steps.Add(new AlphaStep(null, q0, q - q0));
            }
            else
            {
                int i0 = i;
                float limit = q < quads.Count ? quads[q].Depth : float.NegativeInfinity;
                while (i < alpha.Count && alpha[i].Depth > limit) i++;
                steps.Add(new AlphaStep(BuildBatches(arena, alpha.GetRange(i0, i - i0), sort: false, keepOrder: true, paths: dl), 0, 0));
            }
        }
        return steps;
    }

    void DrawParticles(CommandBuffer cmd, ulong vertices, List<Particles.ParticleQuad> quads, AlphaStep step)
    {
        var saved = _push.Instances;
        _push.Instances = vertices;
        Push(cmd);
        int end = step.Start + step.Count;
        bool glow = false;
        for (int q = step.Start; q < end;)
        {
            int key = quads[q].BlendKey;
            int r = q;
            while (r < end && quads[r].BlendKey == key) { glow |= quads[r].Glow; r++; }
            Bind(cmd, ParticlePipeline(key));
            _vk.Api.CmdDraw(cmd, (uint)(6 * (r - q)), 1, (uint)(6 * q), 0);
            q = r;
        }
        // then this stretch's glow into alpha (LLDrawPoolAlpha renders each group's emissives after its colour)
        if (glow)
        {
            Bind(cmd, _particleGlow);
            _vk.Api.CmdDraw(cmd, (uint)(6 * step.Count), 1, (uint)(6 * step.Start), 0);
        }
        _push.Instances = saved;
    }

    /// <summary>Post chain (exposure, tonemap, glow) into the output and the readback copy.</summary>
    public void RecordPost(FrameResources fr, ViewSetup v, RenderTargets rt, Action<CommandBuffer, GpuImage>? present = null)
    {
        var cmd = fr.Cmd;
        var api = _vk.Api;
        // --- post (viewer renderFinalize order): tonemap + gamma into the LDR target (alpha keeps the glow
        // mask), glow extract + separable blur from that, then combine glow into the output.
        rt.Hdr.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        bool dynamicExposure = v.Sky.no_post == 0 && v.Sky.dynamic_exposure_min != v.Sky.dynamic_exposure_max;
        // once a frame, adapted over time (Renderer.Exposure.cs); the camera's preview takes it as it stands
        int exposureSlot = _previewPass ? HeldExposure(dynamicExposure) : FrameExposure(fr, cmd, v, rt, dynamicExposure);
        rt.HdrCopy.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        _push.I1 = (uint)rt.HdrSlot;
        _push.I2 = (uint)exposureSlot;
        BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.HdrCopy }, null, false, false);
        Bind(cmd, _tonemap);
        Push(cmd);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);

        rt.HdrCopy.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        Profiler.Mark("tonemap");
        // applyCAS: sharpen the tonemapped linear image and gamma-correct it, into the (now free) HDR target
        int ldrSlot = rt.HdrCopySlot;
        if (v.Sky.cas_sharpness > 0f)
        {
            rt.Hdr.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
            _push.I1 = (uint)rt.HdrCopySlot;
            BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.Hdr }, null, false, false);
            Bind(cmd, _cas);
            Push(cmd);
            api.CmdDraw(cmd, 3, 1, 0, 0);
            api.CmdEndRendering(cmd);
            rt.Hdr.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
            ldrSlot = rt.HdrSlot;
        }
        Profiler.Mark("cas");
        rt.Glow.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        _push.I1 = (uint)ldrSlot;
        _push.I2 = (uint)GlowNoiseSlot;
        BeginRendering(cmd, rt.Glow.Width, rt.Glow.Height, new[] { rt.Glow }, null, true, false);
        Bind(cmd, _glowExtract);
        Push(cmd);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
        for (int iter = 0; iter < GlowIterations; iter++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var src = pass == 0 ? rt.Glow : rt.GlowTemp;
                var dst = pass == 0 ? rt.GlowTemp : rt.Glow;
                src.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
                dst.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
                _push.I1 = (uint)(pass == 0 ? rt.GlowSlot : rt.GlowTempSlot);
                _push.V0 = pass == 0 ? new Vector4(GlowWidth / rt.Glow.Width, 0, 0, 0) : new Vector4(0, GlowWidth / rt.Glow.Height, 0, 0);
                BeginRendering(cmd, dst.Width, dst.Height, new[] { dst }, null, false, false);
                Bind(cmd, _glowBlur);
                Push(cmd);
                api.CmdDraw(cmd, 3, 1, 0, 0);
                api.CmdEndRendering(cmd);
            }
        }
        rt.Glow.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        Profiler.Mark("glow");

        // glow combine; with SMAA (renderFinalize: after glow, before the final blit) into the LDR target first
        bool smaa = v.Sky.smaa_quality >= 0 && SmaaAreaSlot >= 0;
        var combined = smaa ? rt.Ldr : rt.Output;
        combined.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        _push.I1 = (uint)ldrSlot;
        _push.I2 = (uint)rt.GlowSlot;
        _push.V0 = Vector4.Zero;
        BeginRendering(cmd, rt.Width, rt.Height, new[] { combined }, null, false, false);
        Bind(cmd, _post);
        Push(cmd);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
        Profiler.Mark("combine");
        if (smaa) RecordSmaa(cmd, rt, v.Sky.smaa_quality);
        Profiler.Mark("smaa");

        rt.Output.Transition(cmd, ImageLayout.TransferSrcOptimal);
        if (present != null)
        {
            present(cmd, rt.Output);
            Profiler.Mark("present");
            return;
        }
        // --- readback
        var rb = fr.EnsureReadback((ulong)rt.Width * rt.Height * 4);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(rt.Width, rt.Height, 1),
        };
        api.CmdCopyImageToBuffer(cmd, rt.Output.Handle, ImageLayout.TransferSrcOptimal, rb.Handle, 1, in copy);
        var hb = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit, DstAccessMask = AccessFlags2.HostReadBit,
        };
        var di = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &hb };
        api.CmdPipelineBarrier2(cmd, in di);
        Profiler.Mark("readback");
    }

    /// <summary>LLPipeline::generateSMAABuffers + applySMAA: colour edge detection on the gamma-encoded image,
    /// blend weights from the area/search textures, neighbourhood blending into the output.</summary>
    void RecordSmaa(CommandBuffer cmd, RenderTargets rt, int quality)
    {
        var api = _vk.Api;
        _push.V0 = new Vector4(1f / rt.Width, 1f / rt.Height, rt.Width, rt.Height);
        rt.Ldr.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        (GpuImage dst, uint i1, uint i2, uint i3)[] passes =
        {
            (rt.SmaaEdges, (uint)rt.LdrSlot, 0, 0),
            (rt.SmaaBlend, (uint)rt.SmaaEdgesSlot, (uint)SmaaAreaSlot, (uint)SmaaSearchSlot),
            (rt.Output, (uint)rt.LdrSlot, (uint)rt.SmaaBlendSlot, 0),
        };
        for (int pass = 0; pass < 3; pass++)
        {
            var (dst, i1, i2, i3) = passes[pass];
            dst.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
            _push.I1 = i1; _push.I2 = i2; _push.I3 = i3;
            BeginRendering(cmd, rt.Width, rt.Height, new[] { dst }, null, clearColor: true, clearDepth: false);
            Bind(cmd, SmaaPipeline(quality, pass, dst.Format));
            Push(cmd);
            api.CmdDraw(cmd, 3, 1, 0, 0);
            api.CmdEndRendering(cmd);
            if (pass < 2) dst.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        }
        _push.V0 = Vector4.Zero;
    }

    static readonly string[] SmaaPresets = { "SMAA_PRESET_LOW", "SMAA_PRESET_MEDIUM", "SMAA_PRESET_HIGH", "SMAA_PRESET_ULTRA" };

    Pipeline SmaaPipeline(int quality, int pass, Format format)
    {
        ref var p = ref _smaa[quality, pass];
        if (p.Handle != 0) return p;
        var defs = new[] { SmaaPresets[quality] + "=1", "SMAA_PASS=" + pass };
        p = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("smaa.vert", ShaderStage.Vertex, defs), Fragment = _shaders.Get("smaa.frag", ShaderStage.Fragment, defs),
            ColorFormats = new[] { format }, DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        return p;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct SkinPush
    {
        public ulong Source, Weights, Palette, Dest;
        public uint Count;
    }

    /// <summary>The skinning pre-pass: pose every skinned mesh of the draw list into the frame's skinned vertex
    /// buffer (skin.comp), then make the writes visible to vertex input.</summary>
    void RecordSkinning(CommandBuffer cmd, FrameResources fr, DrawList dl, ulong palettes)
    {
        _currentSkinned = null;
        if (dl.Skins.Count == 0 && _flexiJobs.Count == 0) return;
        var api = _vk.Api;
        var dst = fr.EnsureSkinned((ulong)dl.SkinnedVertices * PipelineBuilder.VertexStride);
        _currentSkinned = dst;
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _skin);
        for (int list = 0; list < 2; list++)
            foreach (var j in list == 0 ? dl.Skins : _flexiJobs)
            {
                var sp = new SkinPush
                {
                    Source = j.Source, Weights = j.Weights,
                    Palette = palettes + (ulong)j.Palette * (ulong)sizeof(Matrix4x4),
                    Dest = dst.Address + (ulong)j.Dest * PipelineBuilder.VertexStride, Count = j.Count,
                };
                api.CmdPushConstants(cmd, Layout, ShaderStageFlags.All, 0, (uint)sizeof(SkinPush), &sp);
                api.CmdDispatch(cmd, (j.Count + 63) / 64, 1, 1);
            }
        var mb = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit, SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.VertexAttributeInputBit, DstAccessMask = AccessFlags2.VertexAttributeReadBit,
        };
        var di = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &mb };
        api.CmdPipelineBarrier2(cmd, in di);
    }

    GpuBuffer? _currentSkinned;
    int[] _sortOrder = [];
    DrawCmd[] _sortCopy = [];

    /// <summary>Far to near, equal depths in the order added (a stable sort).</summary>
    void SortFarToNear(List<DrawCmd> draws)
    {
        int n = draws.Count;
        if (_sortOrder.Length < n) (_sortOrder, _sortCopy) = (new int[n * 2], new DrawCmd[n * 2]);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(draws);
        for (int i = 0; i < n; i++) { _sortOrder[i] = i; _sortCopy[i] = span[i]; }
        var copy = _sortCopy;
        Array.Sort(_sortOrder, 0, n, Comparer<int>.Create((a, b) =>
        {
            int c = copy[b].Depth.CompareTo(copy[a].Depth);
            if (c == 0) c = copy[b].SubDepth.CompareTo(copy[a].SubDepth);
            return c != 0 ? c : a.CompareTo(b);
        }));
        for (int i = 0; i < n; i++) span[i] = copy[_sortOrder[i]];
    }

    List<IndirectBatch> BuildBatches(FrameArena arena, List<DrawCmd> draws, bool sort, bool keepOrder = false, DrawList? paths = null)
    {
        var batches = new List<IndirectBatch>();
        if (draws.Count == 0) return batches;
        // far to near; ties in the order drawn (List.Sort isn't stable: equal depths swapped as the list changed; and
        // persistent instances' indices don't follow the order drawn)
        if (sort) SortFarToNear(draws);
        else if (!keepOrder) GroupByArena(draws);
        var span = arena.Alloc<DrawIndexedIndirect>(draws.Count, out var off);
        int start = 0;
        for (int i = 0; i < draws.Count; i++)
        {
            var d = draws[i];
            span[i] = new DrawIndexedIndirect
            {
                IndexCount = d.Range.IndexCount, InstanceCount = 1, FirstIndex = d.Range.FirstIndex,
                VertexOffset = (int)d.Range.FirstVertex, FirstInstance = d.Instance,
            };
            int path = paths != null ? PathOf(paths, d.Instance) : 0;
            bool last = i == draws.Count - 1 || draws[i + 1].Range.Arena != d.Range.Arena
                        || (paths != null && PathOf(paths, draws[i + 1].Instance) != path);
            if (last)
            {
                batches.Add(new IndirectBatch { Arena = d.Range.Arena, Offset = off + (ulong)(start * sizeof(DrawIndexedIndirect)), Count = (uint)(i - start + 1), Path = path });
                start = i + 1;
            }
        }
        return batches;
    }

    // counts per geometry arena for GroupByArena, and its scratch copy
    int[] _arenaCounts = new int[64];
    DrawCmd[] _groupScratch = new DrawCmd[1024];

    /// <summary>Draws grouped by geometry arena (one bind and one indirect batch each), in the order they came within
    /// an arena: a counting pass instead of a comparison sort, which was the costliest part of recording.</summary>
    void GroupByArena(List<DrawCmd> draws)
    {
        // an arena's key: its index, and whether it's this frame's skinned vertices (MeshRange.Skinned, a high bit)
        static int Key(int arena) => ((arena & (MeshRange.Skinned - 1)) << 1) | ((arena & MeshRange.Skinned) != 0 ? 1 : 0);
        int n = draws.Count, max = 0;
        var span = CollectionsMarshal.AsSpan(draws);
        int first = span[0].Range.Arena;
        bool mixed = false;
        foreach (ref readonly var d in span)
        {
            mixed |= d.Range.Arena != first;
            int k = Key(d.Range.Arena);
            if (k > max) max = k;
        }
        if (!mixed) return;
        if (_arenaCounts.Length <= max + 1) Array.Resize(ref _arenaCounts, Math.Max(max + 2, _arenaCounts.Length * 2));
        if (_groupScratch.Length < n) _groupScratch = new DrawCmd[Math.Max(n, _groupScratch.Length * 2)];
        var counts = _arenaCounts;
        Array.Clear(counts, 0, max + 2);
        foreach (ref readonly var d in span) counts[Key(d.Range.Arena) + 1]++;
        for (int k = 1; k <= max + 1; k++) counts[k] += counts[k - 1];   // counts[k]: where key k starts
        span.CopyTo(_groupScratch);
        for (int i = 0; i < n; i++) span[counts[Key(_groupScratch[i].Range.Arena)]++] = _groupScratch[i];
    }

    void DrawSingle(CommandBuffer cmd, DrawCmd d)
    {
        var arena = _geometry.Arenas[d.Range.Arena & ~MeshRange.Skinned];
        ulong zero = 0;
        var vb = (d.Range.Arena & MeshRange.Skinned) != 0 ? _currentSkinned!.Handle : arena.Vertices.Handle;
        _vk.Api.CmdBindVertexBuffers(cmd, 0, 1, in vb, in zero);
        _vk.Api.CmdBindIndexBuffer(cmd, arena.Indices.Handle, 0, IndexType.Uint32);
        _vk.Api.CmdDrawIndexed(cmd, d.Range.IndexCount, 1, d.Range.FirstIndex, (int)d.Range.FirstVertex, d.Instance);
    }

    void DrawBatches(CommandBuffer cmd, List<IndirectBatch> batches)
    {
        if (batches.Count == 0) return;
        Push(cmd);
        foreach (var b in batches) DrawBatch(cmd, b);
    }

    void DrawBatch(CommandBuffer cmd, IndirectBatch b)
    {
        var arena = _geometry.Arenas[b.Arena & ~MeshRange.Skinned];
        ulong zero = 0;
        var vb = (b.Arena & MeshRange.Skinned) != 0 ? _currentSkinned!.Handle : arena.Vertices.Handle;
        _vk.Api.CmdBindVertexBuffers(cmd, 0, 1, in vb, in zero);
        _vk.Api.CmdBindIndexBuffer(cmd, arena.Indices.Handle, 0, IndexType.Uint32);
        _vk.Api.CmdDrawIndexedIndirect(cmd, _currentArena.Device.Handle, b.Offset, b.Count, (uint)sizeof(DrawIndexedIndirect));
    }

    /// <summary>Build mips 1..n from mip 0 (currently in ColorAttachmentOptimal); leaves the image shader-readable.</summary>
    void GenerateMips(CommandBuffer cmd, GpuImage img)
    {
        var api = _vk.Api;
        GpuImage.Barrier(_vk, cmd, img.Handle, img.Aspect, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal, 0, 1, 0, 1);
        GpuImage.Barrier(_vk, cmd, img.Handle, img.Aspect, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 1, img.Mips - 1, 0, 1);
        int w = (int)img.Width, h = (int)img.Height;
        for (uint m = 1; m < img.Mips; m++)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(img.Aspect, m - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(img.Aspect, m, 0, 1),
            };
            blit.SrcOffsets[1] = new Offset3D(w, h, 1);
            blit.DstOffsets[1] = new Offset3D(nw, nh, 1);
            api.CmdBlitImage(cmd, img.Handle, ImageLayout.TransferSrcOptimal, img.Handle, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
            GpuImage.Barrier(_vk, cmd, img.Handle, img.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, m, 1, 0, 1);
            w = nw; h = nh;
        }
        GpuImage.Barrier(_vk, cmd, img.Handle, img.Aspect, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, 0, img.Mips, 0, 1);
        img.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    /// <summary>Whole-image copy (same size and format); src goes back to srcLayout, dst ends shader-readable.</summary>
    void CopyImage(CommandBuffer cmd, GpuImage src, GpuImage dst, ImageLayout srcLayout)
    {
        src.Transition(cmd, ImageLayout.TransferSrcOptimal);
        dst.Transition(cmd, ImageLayout.TransferDstOptimal);
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(src.Aspect, 0, src.BaseLayer, 1),
            DstSubresource = new ImageSubresourceLayers(dst.Aspect, 0, dst.BaseLayer, 1),
            Extent = new Extent3D(src.Width, src.Height, 1),
        };
        _vk.Api.CmdCopyImage(cmd, src.Handle, ImageLayout.TransferSrcOptimal, dst.Handle, ImageLayout.TransferDstOptimal, 1, in region);
        dst.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        src.Transition(cmd, srcLayout);
    }

    FrameArena _currentArena = null!;
    public void BeginFrame(FrameResources fr) => _currentArena = fr.Arena;

    void Bind(CommandBuffer cmd, Pipeline p) => _vk.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, Variant(p));

    /// <summary>The world's persistent instances and materials, whose slots draws name with PersistentTable's bit
    /// (World/RenderWorld.cs makes them; null: none).</summary>
    public (PersistentTable<GpuInstance> Instances, PersistentTable<GpuMaterial> Materials)? Persistent;

    void Push(CommandBuffer cmd)
    {
        var pb = _push;
        if (Persistent is var (pi, pm)) (pb.PersistentInstances, pb.PersistentMaterials) = (pi.Address, pm.Address);
        _vk.Api.CmdPushConstants(cmd, Layout, ShaderStageFlags.All, 0, (uint)sizeof(PushBlock), &pb);
    }

    /// <summary>Caster depth bias (glPolygonOffset-style); the shadow setup port supplies the viewer's values.</summary>
    public float ShadowBiasConstant = 1f, ShadowBiasSlope = 1f;

    void BeginShadowRendering(CommandBuffer cmd, GpuImage map)
    {
        var datt = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = map.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0)),
        };
        var ri = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(map.Width, map.Height)),
            LayerCount = 1,
            PDepthAttachment = &datt,
        };
        _vk.Api.CmdBeginRendering(cmd, in ri);
        var vp = new Viewport(0, 0, map.Width, map.Height, 0, 1);
        var sc = new Rect2D(new Offset2D(0, 0), new Extent2D(map.Width, map.Height));
        _vk.Api.CmdSetViewport(cmd, 0, 1, in vp);
        _vk.Api.CmdSetScissor(cmd, 0, 1, in sc);
    }

    void BeginRendering(CommandBuffer cmd, uint w, uint h, GpuImage[] colors, GpuImage? depth, bool clearColor, bool clearDepth,
        ImageView viewOverride = default, bool overwrite = false)
    {
        var atts = stackalloc RenderingAttachmentInfo[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            atts[i] = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = i == 0 && viewOverride.Handle != 0 ? viewOverride : colors[i].View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = overwrite ? AttachmentLoadOp.DontCare : clearColor ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearColorValue(0f, 0f, 0f, 0f)),
            };
        }
        var datt = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = depth?.View ?? default,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = clearDepth ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)),
        };
        // a mirror's reflection renders only where the mirror can show it
        var area = _area ?? new Rect2D(new Offset2D(0, 0), new Extent2D(w, h));
        var ri = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = area,
            LayerCount = 1,
            ViewMask = _viewMask,
            ColorAttachmentCount = (uint)colors.Length,
            PColorAttachments = atts,
            PDepthAttachment = depth != null ? &datt : null,
        };
        _vk.Api.CmdBeginRendering(cmd, in ri);
        var vp = new Viewport(0, 0, w, h, 0, 1);
        _vk.Api.CmdSetViewport(cmd, 0, 1, in vp);
        _vk.Api.CmdSetScissor(cmd, 0, 1, in area);
    }

    public void Dispose()
    {
        Profiler.Dispose();
        foreach (var m in ShadowMaps) m?.Dispose();
        _captureShadow?.Dispose();
        foreach (var p in new[] { _gbuffer, _gbufferTwoSided, _sky, _stars, _light, _localLights, _fullbright, _alpha, _glow, _haze, _tonemap, _cas, _glowExtract, _glowBlur, _post, _water, _sunlight, _blurlight, _luminance, _shadow, _spotShadow, _shadowSolid, _spotShadowSolid, _waterHaze, _projectors , _skin,
                     _avatarAlphaNoCull, _avatarAlphaDepth, _avatarAlphaBack, _avatarAlphaFront, _riggedAlpha })
            _vk.Api.DestroyPipeline(_vk.Device, p, null);
        foreach (var p in _smaa)
            if (p.Handle != 0) _vk.Api.DestroyPipeline(_vk.Device, p, null);
        foreach (var p in _particlePipes.Values) _vk.Api.DestroyPipeline(_vk.Device, p, null);
        foreach (var p in _variants.Values) _vk.Api.DestroyPipeline(_vk.Device, p, null);
        for (int i = 1; i <= 4; i++) _vk.Api.DestroyPipeline(_vk.Device, _alphaPaths[i], null);
        foreach (var p in _alphaPreWater) _vk.Api.DestroyPipeline(_vk.Device, p, null);
        _vk.Api.DestroyPipeline(_vk.Device, _particleGlow, null);
        DisposeHud();
        DisposeImpostors();
        DisposeMirror();
        DisposeExposure();
        DisposeTiles();
        DisposeLens();
        DisposeLayers();
        DisposeHalfLight();
        DisposeWater();
        DisposeParcelLines();
        DisposeBeacons();
        _vk.Api.DestroyPipelineLayout(_vk.Device, Layout, null);
    }
}
