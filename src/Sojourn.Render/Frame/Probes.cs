using System.Runtime.InteropServices;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

/// <summary>One probe in a view's list (probes.glsl ProbeGpu; the viewer's refSphere/refBox/refParams/refIndex).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuProbe
{
    public Vector4 Sphere;     // xyz origin (view region frame), w radius
    public Matrix4x4 Box;      // region -> unit cube (box probes)
    public Vector4 Params;     // x ambiance scale, y radiance scale, z fade-in
    public int Radiance, Irradiance, Priority, Pad;
}

/// <summary>One captured reflection probe: a prefiltered radiance cube (mip per roughness) and an irradiance cube.</summary>
public sealed class ProbeMaps : IDisposable
{
    public GpuImage Radiance = null!, Irradiance = null!;
    public int RadianceSlot = -1, IrradianceSlot = -1;   // bindless cube slots
    public Vector3 Center;
    public float Radius;
    public long CapturedAt;
    public bool Valid;
    internal Bindless? Bindless;

    public void Dispose()
    {
        if (Bindless != null)
        {
            if (RadianceSlot >= 0) Bindless.FreeCube(RadianceSlot);
            if (IrradianceSlot >= 0) Bindless.FreeCube(IrradianceSlot);
        }
        Radiance?.Dispose();
        Irradiance?.Dispose();
    }
}

/// <summary>
/// Reflection probe capture and filtering, following LLReflectionMapManager::updateProbeFace: render the six faces
/// at twice the probe resolution, gaussian-blur, downsample into a scratch cube with a simple mip chain, then
/// filter into the probe's irradiance map (irradiance pass: direct light only) or radiance map (radiance pass:
/// lit with the fresh irradiance). Probe resolution 128 (RenderReflectionProbeResolution), irradiance 16.
/// Ported shaders: gaussianF, radianceGenF (32 samples), class2 irradianceGenF; the reflectionmipF chain is blits.
/// The maps are R11G11B10 float like the viewer's cube arrays (RGBA16F where that isn't renderable). The viewer
/// renders faces at 4x the probe resolution; 2x here (the gaussian has the same footprint in face UV).
/// </summary>
public sealed unsafe class ProbeSystem : IDisposable
{
    public const uint Resolution = 128;
    public const uint IrradianceResolution = 16;
    public static readonly uint Mips = (uint)Math.Log2(Resolution) + 1;   // 8
    public static float MaxProbeLod => MathF.Log2(Resolution) - 1f;     // the viewer's mMaxProbeLOD

    readonly VulkanContext _vk;
    readonly Bindless _bindless;
    readonly Renderer _renderer;
    public RenderTargets Capture { get; }                 // 256x256 supersampled face renders
    readonly GpuImage _blurTemp;
    readonly int _blurTempSlot;
    readonly GpuImage _scratch;                           // cube, simple mips: the filters' source
    public Format MapFormat { get; }
    readonly int _scratchSlot;
    Pipeline _gaussian, _radianceGen, _irradianceGen;

    // Capture cameras for cube faces +X -X +Y -Y +Z -Z. Each face's up is the cube direction at the face's top
    // row; the cube layout is mirrored relative to a right-handed camera, so the downsample blit flips x.
    public static readonly Vector3[] FaceLook = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
    public static readonly Vector3[] FaceUp = { Vector3.UnitY, Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitY, Vector3.UnitY };

    public ProbeSystem(VulkanContext vk, Bindless bindless, ShaderCompiler shaders, Renderer renderer)
    {
        _vk = vk;
        _bindless = bindless;
        _renderer = renderer;
        Capture = new RenderTargets(vk, bindless, Resolution * 2, Resolution * 2);
        _blurTemp = new GpuImage(vk, Resolution * 2, Resolution * 2, RenderTargets.HdrFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, dedicated: true);
        _blurTempSlot = bindless.AllocTexture();
        bindless.Write(_blurTempSlot, _blurTemp.View, bindless.ClampLinearNoMip);
        _scratch = new GpuImage(vk, Resolution, Resolution, RenderTargets.HdrFormat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, Mips, 6, cube: true, dedicated: true);
        _scratchSlot = bindless.AllocCube();
        bindless.Write(_scratchSlot, _scratch.View, bindless.Clamp, cube: true);

        MapFormat = Renderable(vk, Format.B10G11R11UfloatPack32) ? Format.B10G11R11UfloatPack32 : RenderTargets.HdrFormat;
        var fsVs = shaders.Get("fullscreen.vert", ShaderStage.Vertex);
        GraphicsPipelineDesc Full(string frag, Format format) => new()
        {
            Vertex = fsVs, Fragment = shaders.Get(frag, ShaderStage.Fragment), ColorFormats = new[] { format },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = renderer.Layout,
        };
        _gaussian = PipelineBuilder.Create(vk, Full("probe_gaussian.frag", RenderTargets.HdrFormat));
        _radianceGen = PipelineBuilder.Create(vk, Full("probe_radiance.frag", MapFormat));
        _irradianceGen = PipelineBuilder.Create(vk, Full("probe_irradiance.frag", MapFormat));
        int texel = MapFormat == Format.B10G11R11UfloatPack32 ? 4 : 8;
        BytesPerProbe = (ulong)texel * 6 * (Resolution * Resolution * 4 / 3 + IrradianceResolution * IrradianceResolution);
    }

    /// <summary>VRAM of one probe's maps (radiance with mips, irradiance), before allocation padding.</summary>
    public ulong BytesPerProbe { get; }

    static bool Renderable(VulkanContext vk, Format f)
    {
        vk.Api.GetPhysicalDeviceFormatProperties(vk.PhysicalDevice, f, out var props);
        var need = FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.SampledImageFilterLinearBit;
        return (props.OptimalTilingFeatures & need) == need;
    }

    /// <summary>A probe's two cube maps, or null while the cube table has no two slots free (retired probes' slots come
    /// back a few frames on; the caller reuses a resident probe's maps instead or captures later).</summary>
    public ProbeMaps? TryCreateMaps()
    {
        if (!_bindless.TryAllocCube(out int radiance)) return null;
        if (!_bindless.TryAllocCube(out int irradiance)) { _bindless.FreeCube(radiance); return null; }
        var p = new ProbeMaps { Bindless = _bindless };
        p.Radiance = new GpuImage(_vk, Resolution, Resolution, MapFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, Mips, 6, cube: true, dedicated: true);
        p.Irradiance = new GpuImage(_vk, IrradianceResolution, IrradianceResolution, MapFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, 1, 6, cube: true, dedicated: true);
        p.RadianceSlot = radiance;
        _bindless.Write(p.RadianceSlot, p.Radiance.View, _bindless.Clamp, cube: true);
        p.IrradianceSlot = irradiance;
        _bindless.Write(p.IrradianceSlot, p.Irradiance.View, _bindless.Clamp, cube: true);
        return p;
    }

    /// <summary>A capture face camera: 90 degrees, the probe's near clip, the 64 m far clip as a cube cull (and a
    /// per-pixel terrain clip, see probe_capture_far), LODs from <paramref name="lodView"/>.</summary>
    public static ViewSetup FaceView(ViewSetup env, ulong region, float waterHeight, Vector3 center, float near, int face, ViewSetup lodView)
    {
        var v = new ViewSetup
        {
            Width = Resolution * 2, Height = Resolution * 2, Position = center, VFov = MathF.PI / 2, Near = near,
            RegionHandle = region, DrawDistance = Sojourn.Render.World.ProbeManager.CaptureFar, CubeCull = true,
            WaterHeight = waterHeight, HasWater = env.HasWater,
            Time = env.Time, SunDirRegion = env.SunDirRegion, MoonDirRegion = env.MoonDirRegion, SunUp = env.SunUp,
            WaterNormalA = env.WaterNormalA, WaterNormalB = env.WaterNormalB,
            WaterNormalSlotA = env.WaterNormalSlotA, WaterNormalSlotB = env.WaterNormalSlotB,
            CloudNoiseA = env.CloudNoiseA, CloudNoiseB = env.CloudNoiseB,
            CloudNoiseSlotA = env.CloudNoiseSlotA, CloudNoiseSlotB = env.CloudNoiseSlotB,
            SkyU = env.SkyU, SkyIrradianceU = env.SkyIrradianceU, WaterU = env.WaterU, EnvTime = env.EnvTime, LocalSky = env.LocalSky,
            LodView = lodView,
        };
        v.View = Matrix4x4.CreateLookAt(center, center + FaceLook[face], FaceUp[face]);
        v.Proj = ViewSetup.ReverseZInfinite(v.VFov, 1f, v.Near);
        return v;
    }

    /// <summary>Record the capture of one pass (6 faces already rendered by the caller into Capture.Hdr one at a time
    /// via <paramref name="renderFace"/>) and its filtering into the probe.</summary>
    public void RecordPass(CommandBuffer cmd, ProbeMaps probe, bool radiancePass, Action<int> renderFace, Renderer.ViewData anyView)
    {
        RecordFaces(cmd, 0, 6, renderFace, anyView);
        Filter(cmd, probe, radiancePass, anyView);
    }

    /// <summary>Faces [<paramref name="from"/>, <paramref name="to"/>) of a pass into the scratch cube. A pass may be
    /// spread over several frames (one capture at a time: the scratch holds its faces in between).</summary>
    public void RecordFaces(CommandBuffer cmd, int from, int to, Action<int> renderFace, Renderer.ViewData anyView)
    {
        for (int face = from; face < to; face++)
        {
            renderFace(face);
            DownsampleFace(cmd, face, anyView);
        }
    }

    /// <summary>Filter the scratch cube's six faces into the probe's irradiance or radiance map.</summary>
    public void Filter(CommandBuffer cmd, ProbeMaps probe, bool radiancePass, Renderer.ViewData anyView)
    {
        // scratch now complete: shader-readable for the filters
        _scratch.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        if (radiancePass) FilterRadiance(cmd, probe, anyView);
        else FilterIrradiance(cmd, probe, anyView);
        probe.Valid = true;
    }

    void SetPush(Renderer.ViewData vd, uint i1, uint i2, Vector4 v0, Vector4 v1)
    {
        _push = default;
        _push.View = vd.View;
        _push.Sky = vd.Sky;
        _push.I1 = i1;
        _push.I2 = i2;
        _push.V0 = v0;
        _push.V1 = v1;
    }

    PushBlock _push;

    void DrawFullscreen(CommandBuffer cmd, Pipeline p, ImageView target, uint w, uint h)
    {
        var att = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
        };
        var ri = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h)),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &att,
        };
        var api = _vk.Api;
        api.CmdBeginRendering(cmd, in ri);
        var vp = new Viewport(0, 0, w, h, 0, 1);
        var sc = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h));
        api.CmdSetViewport(cmd, 0, 1, in vp);
        api.CmdSetScissor(cmd, 0, 1, in sc);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, p);
        var pb = _push;
        api.CmdPushConstants(cmd, _renderer.Layout, ShaderStageFlags.All, 0, (uint)sizeof(PushBlock), &pb);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
    }

    /// <summary>Gaussian blur the 256 px face render (H into temp, V back), then downsample it into the scratch
    /// cube's face at 128 px and box-filter the remaining mips (reflectionmipF chain).</summary>
    void DownsampleFace(CommandBuffer cmd, int face, Renderer.ViewData vd)
    {
        var hdr = Capture.Hdr;
        uint w2 = Resolution * 2;
        float resScale = 1f / w2;
        // the first face sees the scratch cube in whatever layout the last probe left it; make every face renderable
        hdr.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        _blurTemp.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        SetPush(vd, (uint)Capture.HdrSlot, 0, new Vector4(1, 0, resScale, 0), default);
        DrawFullscreen(cmd, _gaussian, _blurTemp.View, w2, w2);
        _blurTemp.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        hdr.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        SetPush(vd, (uint)_blurTempSlot, 0, new Vector4(0, 1, resScale, 0), default);
        DrawFullscreen(cmd, _gaussian, hdr.View, w2, w2);
        hdr.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);

        // mip 0 from the blurred render, then each mip from the previous one: bilinear 2:1 downsamples, which is
        // what reflectionmipF's chain amounts to
        hdr.Transition(cmd, ImageLayout.TransferSrcOptimal);
        GpuImage.Barrier(_vk, cmd, _scratch.Handle, _scratch.Aspect, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, Mips, (uint)face, 1);
        Blit(cmd, hdr.Handle, 0, 0, (int)w2, _scratch.Handle, 0, (uint)face, (int)Resolution, flipX: true);
        for (uint m = 1; m < Mips; m++)
        {
            GpuImage.Barrier(_vk, cmd, _scratch.Handle, _scratch.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, m - 1, 1, (uint)face, 1);
            Blit(cmd, _scratch.Handle, m - 1, (uint)face, (int)Math.Max(1, Resolution >> (int)(m - 1)), _scratch.Handle, m, (uint)face, (int)Math.Max(1, Resolution >> (int)m));
        }
        GpuImage.Barrier(_vk, cmd, _scratch.Handle, _scratch.Aspect, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, 0, Mips - 1, (uint)face, 1);
        GpuImage.Barrier(_vk, cmd, _scratch.Handle, _scratch.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, Mips - 1, 1, (uint)face, 1);
        hdr.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        _scratch.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    void Blit(CommandBuffer cmd, Silk.NET.Vulkan.Image src, uint srcMip, uint srcLayer, int srcSize, Silk.NET.Vulkan.Image dst, uint dstMip, uint dstLayer, int dstSize, bool flipX = false)
    {
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, srcMip, srcLayer, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, dstMip, dstLayer, 1),
        };
        blit.SrcOffsets[0] = new Offset3D(flipX ? srcSize : 0, 0, 0);
        blit.SrcOffsets[1] = new Offset3D(flipX ? 0 : srcSize, srcSize, 1);
        blit.DstOffsets[1] = new Offset3D(dstSize, dstSize, 1);
        _vk.Api.CmdBlitImage(cmd, src, ImageLayout.TransferSrcOptimal, dst, ImageLayout.TransferDstOptimal, 1, in blit, Silk.NET.Vulkan.Filter.Linear);
    }

    void FilterRadiance(CommandBuffer cmd, ProbeMaps probe, Renderer.ViewData vd)
    {
        GpuImage.Barrier(_vk, cmd, probe.Radiance.Handle, probe.Radiance.Aspect, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, 0, Mips, 0, 6);
        for (uint m = 0; m < Mips; m++)
        {
            uint size = Math.Max(1, Resolution >> (int)m);
            for (int face = 0; face < 6; face++)
            {
                var view = probe.Radiance.CreateView(ImageViewType.Type2D, m, 1, (uint)face, 1);
                SetPush(vd, (uint)_scratchSlot, 0, new Vector4(face, m, MaxProbeLod, Resolution), default);
                DrawFullscreen(cmd, _radianceGen, view, size, size);
                var v = view;
                _pendingViews.Add(v);
            }
        }
        GpuImage.Barrier(_vk, cmd, probe.Radiance.Handle, probe.Radiance.Aspect, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, 0, Mips, 0, 6);
        probe.Radiance.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    void FilterIrradiance(CommandBuffer cmd, ProbeMaps probe, Renderer.ViewData vd)
    {
        GpuImage.Barrier(_vk, cmd, probe.Irradiance.Handle, probe.Irradiance.Aspect, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, 0, 1, 0, 6);
        for (int face = 0; face < 6; face++)
        {
            var view = probe.Irradiance.CreateView(ImageViewType.Type2D, 0, 1, (uint)face, 1);
            SetPush(vd, (uint)_scratchSlot, 0, new Vector4(face, 0, MaxProbeLod, IrradianceResolution), default);
            DrawFullscreen(cmd, _irradianceGen, view, IrradianceResolution, IrradianceResolution);
            _pendingViews.Add(view);
        }
        GpuImage.Barrier(_vk, cmd, probe.Irradiance.Handle, probe.Irradiance.Aspect, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, 0, 1, 0, 6);
        probe.Irradiance.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    // per-face views created while recording; destroyed once the frame that used them completes
    readonly List<ImageView> _pendingViews = new();

    /// <summary>Hand the views created this frame to the frame's retire list.</summary>
    public void RetireViews(FrameResources fr)
    {
        if (_pendingViews.Count == 0) return;
        var views = _pendingViews.ToArray();
        _pendingViews.Clear();
        fr.Retire.Add(() => { foreach (var v in views) _vk.Api.DestroyImageView(_vk.Device, v, null); });
    }

    public void Dispose()
    {
        foreach (var v in _pendingViews) _vk.Api.DestroyImageView(_vk.Device, v, null);
        foreach (var p in new[] { _gaussian, _radianceGen, _irradianceGen }) _vk.Api.DestroyPipeline(_vk.Device, p, null);
        _bindless.FreeTexture(_blurTempSlot);
        _bindless.FreeCube(_scratchSlot);
        _scratch.Dispose();
        _blurTemp.Dispose();
        Capture.Dispose();
    }
}
