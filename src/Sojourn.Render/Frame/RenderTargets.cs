using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

/// <summary>
/// The per-resolution set of targets one view renders through. Formats follow the viewer's deferred pipeline:
/// diffuse RGBA8 (legacy shaders write sRGB, PBR writes linear), specular RGBA8, normal RGBA16F
/// (octahedral normal, env intensity, gbuffer flag), emissive RGBA16F, reverse-Z depth, HDR scene RGBA16F.
/// Every sampled target is registered in the bindless table.
/// Stereo sets (layers 2) hold the targets both eyes draw into at once (multiview: the G-buffer, depth and HDR scene) as
/// two-layer images, and <see cref="Eye"/> gives each eye a set of its own over them: 2D views of its layer, registered
/// for the fullscreen passes that light and finish it, and the rest shared, as the eyes take their turns.
/// </summary>
public sealed class RenderTargets : IDisposable
{
    public const Format DiffuseFormat = Format.R8G8B8A8Unorm;
    public const Format SpecularFormat = Format.R8G8B8A8Unorm;
    /// <summary>The narrow G-buffer (RenderQuality's narrow_gbuffer setting, read once at start; off by default):
    /// normal A2B10G10R10 (10-bit octahedral normal, env intensity and the self mark in b, the flag in 2 bits:
    /// gbuffer_util.glsl gbNormOut/gbNormIn), 20 bytes a pixel instead of 24. Emissive stays RGBA16F: as B10G11R11
    /// (tried 2026-10-27) the sky, which the sky pass writes there, banded in visible rings (blue has a 5-bit
    /// mantissa). Set before the first renderer is made and never changed after.</summary>
    public static bool Narrow = System.Environment.GetEnvironmentVariable("SOJOURN_NARROW_GBUFFER") == "1";
    public static Format NormalFormat => Narrow ? Format.A2B10G10R10UnormPack32 : Format.R16G16B16A16Sfloat;
    public const Format EmissiveFormat = Format.R16G16B16A16Sfloat;
    public const Format DepthFormat = Format.D32Sfloat;
    public const Format HdrFormat = Format.R16G16B16A16Sfloat;
    public const Format OutputFormat = Format.R8G8B8A8Unorm;
    public const Format GlowFormat = Format.R8G8B8A8Unorm;

    readonly VulkanContext _vk;
    readonly Bindless _bindless;
    public uint Width, Height;
    public GpuImage Diffuse, Specular, Normal, Emissive, Depth, Hdr, HdrCopy, Glow, GlowTemp, Output, SceneCopy, DepthCopy, LightMap, LightMapTemp, Luminance;
    /// <summary>SMAA: the glow-combined image (the viewer's mPostPingMap), edges (mFXAAMap), blend weights (mSMAABlendBuffer).</summary>
    public GpuImage Ldr, SmaaEdges, SmaaBlend;
    public int LdrSlot, SmaaEdgesSlot, SmaaBlendSlot;
    public int DiffuseSlot, SpecularSlot, NormalSlot, EmissiveSlot, DepthSlot, HdrSlot, HdrCopySlot, GlowSlot, GlowTempSlot, SceneCopySlot, DepthCopySlot, LightMapSlot, LightMapTempSlot, LuminanceSlot;
    public const Format LuminanceFormat = Format.R16Sfloat;
    public ImageView LuminanceMip0;   // render-target view of mip 0 (attachments take one level)
    public const Format LightMapFormat = Format.R8G8B8A8Unorm;
    public long LastUsedTicks;
    /// <summary>The light tiles' records (Renderer.Tiles.cs), made when a main eye first renders through these.</summary>
    public GpuBuffer? LightTiles;
    public uint TileCols, TileRows;
    public ulong Bytes { get; }
    /// <summary>Array layers of the multiview targets (2: a stereo set, both eyes drawn at once; its eyes' sets in
    /// <see cref="Eyes"/>).</summary>
    public readonly uint Layers = 1;
    /// <summary>Of a stereo set: each eye's own set over its layer. Of an eye's set: the stereo set it belongs to.</summary>
    public readonly RenderTargets[] Eyes = [];
    public readonly RenderTargets? Stereo;
    /// <summary>Of an eye's set: its layer of the stereo set.</summary>
    public readonly uint Layer;
    // of a stereo set: each eye's output (the first is Output; the others too unless OutputPerEye)
    readonly GpuImage[] _outputs = [];
    /// <summary>Stereo sets made from now on give each eye an output of its own (the headset's overlap needs it; one
    /// more eye-sized image, ~18 MB at the Quest's size). Set by Sojourn.Client's XrSession.</summary>
    public static bool OutputPerEye;

    public RenderTargets(VulkanContext vk, Bindless bindless, uint width, uint height, uint layers = 1)
    {
        _vk = vk;
        _bindless = bindless;
        Width = width; Height = height; Layers = layers;
        const ImageUsageFlags rt = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit;
        Diffuse = new GpuImage(vk, width, height, DiffuseFormat, rt, layers: layers, dedicated: true);
        Specular = new GpuImage(vk, width, height, SpecularFormat, rt, layers: layers, dedicated: true);
        Normal = new GpuImage(vk, width, height, NormalFormat, rt, layers: layers, dedicated: true);
        Emissive = new GpuImage(vk, width, height, EmissiveFormat, rt, layers: layers, dedicated: true);
        Depth = new GpuImage(vk, width, height, DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, layers: layers, dedicated: true);
        Hdr = new GpuImage(vk, width, height, HdrFormat, rt | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.StorageBit, layers: layers, dedicated: true);
        HdrCopy = new GpuImage(vk, width, height, HdrFormat, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit, dedicated: true);
        SceneCopy = new GpuImage(vk, width, height, HdrFormat, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, dedicated: true);
        DepthCopy = new GpuImage(vk, width, height, DepthFormat, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, dedicated: true);
        LightMap = new GpuImage(vk, width, height, LightMapFormat, rt, dedicated: true);
        LightMapTemp = new GpuImage(vk, width, height, LightMapFormat, rt, dedicated: true);
        // the viewer's mLuminanceMap: 256x256 R16F with a full mip chain (exposure reads mip 8)
        Luminance = new GpuImage(vk, 256, 256, LuminanceFormat, rt | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, mips: 9);
        LuminanceMip0 = Luminance.CreateView(ImageViewType.Type2D, 0, 1, 0, 1);
        // the viewer's glow buffers are 512 x 512 whatever the screen size (RenderGlowResolutionPow 9)
        uint gw = 512, gh = 512;
        // RGBA8 like the viewer's (RenderGlowHDR 0): every blur pass saturates at 1
        Glow = new GpuImage(vk, gw, gh, GlowFormat, rt, dedicated: true);
        GlowTemp = new GpuImage(vk, gw, gh, GlowFormat, rt, dedicated: true);
        // sampled too: the headset's hands over its menus are drawn from it (Realtime/Overlay.Hands.cs); cleared by the
        // frames without a scene (FrameRenderer.Clear, FrameRenderer.Void.cs)
        Output = new GpuImage(vk, width, height, OutputFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit, dedicated: true);
        Ldr = new GpuImage(vk, width, height, OutputFormat, rt, dedicated: true);
        SmaaEdges = new GpuImage(vk, width, height, Format.R8G8Unorm, rt, dedicated: true);
        SmaaBlend = new GpuImage(vk, width, height, Format.R8G8B8A8Unorm, rt, dedicated: true);

        if (layers == 1) RegisterLayered();
        else DiffuseSlot = SpecularSlot = NormalSlot = EmissiveSlot = DepthSlot = HdrSlot = -1;   // array views: the eyes' sets sample them
        HdrCopySlot = Register(HdrCopy, bindless.ClampLinearNoMip);
        GlowSlot = Register(Glow, bindless.ClampLinearNoMip);
        GlowTempSlot = Register(GlowTemp, bindless.ClampLinearNoMip);
        SceneCopySlot = Register(SceneCopy, bindless.ClampLinearNoMip);
        DepthCopySlot = Register(DepthCopy, bindless.Nearest);
        LightMapSlot = Register(LightMap, bindless.Nearest);
        LightMapTempSlot = Register(LightMapTemp, bindless.Nearest);
        LuminanceSlot = Register(Luminance, bindless.Clamp);
        LdrSlot = Register(Ldr, bindless.ClampLinearNoMip);
        SmaaEdgesSlot = Register(SmaaEdges, bindless.ClampLinearNoMip);
        SmaaBlendSlot = Register(SmaaBlend, bindless.ClampLinearNoMip);
        Bytes = Diffuse.Bytes + Specular.Bytes + Normal.Bytes + Emissive.Bytes + Depth.Bytes + Hdr.Bytes + HdrCopy.Bytes + Glow.Bytes + GlowTemp.Bytes + Output.Bytes + SceneCopy.Bytes + DepthCopy.Bytes + LightMap.Bytes + LightMapTemp.Bytes + Luminance.Bytes
            + Ldr.Bytes + SmaaEdges.Bytes + SmaaBlend.Bytes;
        if (layers > 1)
        {
            // with OutputPerEye each eye finishes into an output of its own, so an eye's image is still whole once the
            // next eye is done: the headset's overlap copies both into its swapchain after the scene (Sojourn.Client's
            // XrSession); otherwise they share one
            _outputs = new GpuImage[layers];
            _outputs[0] = Output;
            for (int i = 1; i < layers; i++) _outputs[i] = Output;
            for (int i = 1; i < layers && OutputPerEye; i++)
            {
                _outputs[i] = new GpuImage(vk, width, height, OutputFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit, dedicated: true);
                Bytes += _outputs[i].Bytes;
            }
            Eyes = Enumerable.Range(0, (int)layers).Select(i => new RenderTargets(this, (uint)i)).ToArray();
        }
    }

    /// <summary>One eye's set of a stereo set: its layer of the multiview targets, the rest shared.</summary>
    RenderTargets(RenderTargets stereo, uint layer)
    {
        _vk = stereo._vk;
        _bindless = stereo._bindless;
        Stereo = stereo;
        Layer = layer;
        Width = stereo.Width; Height = stereo.Height;
        Diffuse = new GpuImage(stereo.Diffuse, layer);
        Specular = new GpuImage(stereo.Specular, layer);
        Normal = new GpuImage(stereo.Normal, layer);
        Emissive = new GpuImage(stereo.Emissive, layer);
        Depth = new GpuImage(stereo.Depth, layer);
        Hdr = new GpuImage(stereo.Hdr, layer);
        RegisterLayered();
        (HdrCopy, Glow, GlowTemp, Output, SceneCopy, DepthCopy, LightMap, LightMapTemp, Luminance, Ldr, SmaaEdges, SmaaBlend) =
            (stereo.HdrCopy, stereo.Glow, stereo.GlowTemp, stereo._outputs[layer], stereo.SceneCopy, stereo.DepthCopy, stereo.LightMap, stereo.LightMapTemp,
             stereo.Luminance, stereo.Ldr, stereo.SmaaEdges, stereo.SmaaBlend);
        (HdrCopySlot, GlowSlot, GlowTempSlot, SceneCopySlot, DepthCopySlot, LightMapSlot, LightMapTempSlot, LuminanceSlot, LdrSlot, SmaaEdgesSlot, SmaaBlendSlot) =
            (stereo.HdrCopySlot, stereo.GlowSlot, stereo.GlowTempSlot, stereo.SceneCopySlot, stereo.DepthCopySlot, stereo.LightMapSlot, stereo.LightMapTempSlot,
             stereo.LuminanceSlot, stereo.LdrSlot, stereo.SmaaEdgesSlot, stereo.SmaaBlendSlot);
        LuminanceMip0 = stereo.LuminanceMip0;
    }

    void RegisterLayered()
    {
        DiffuseSlot = Register(Diffuse, _bindless.Nearest);
        SpecularSlot = Register(Specular, _bindless.Nearest);
        NormalSlot = Register(Normal, _bindless.Nearest);
        EmissiveSlot = Register(Emissive, _bindless.Nearest);
        DepthSlot = Register(Depth, _bindless.Nearest);
        HdrSlot = Register(Hdr, _bindless.ClampLinearNoMip);
    }

    /// <summary>The eye's lens mask (Renderer.Lens.cs), made on first use, and what it was last drawn from.</summary>
    public GpuImage? LensMaskImage;
    public int LensMaskSlot = -1;
    public System.Numerics.Vector2[]? LensMesh;
    public System.Numerics.Matrix4x4 LensProj;
    public const Format LensMaskFormat = Format.R8Unorm;

    public void EnsureLensMask()
    {
        if (LensMaskImage != null) return;
        LensMaskImage = new GpuImage(_vk, Width, Height, LensMaskFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, dedicated: true);
        LensMaskSlot = Register(LensMaskImage, _bindless.Nearest);
    }

    public void EnsureLightTiles()
    {
        if (LightTiles != null) return;
        if (Stereo != null)
        {
            // the eyes take turns with one set of records (each eye's tile cull waits for the last eye's light passes)
            Stereo.EnsureLightTiles();
            (LightTiles, TileCols, TileRows) = (Stereo.LightTiles, Stereo.TileCols, Stereo.TileRows);
            return;
        }
        TileCols = (Width + Renderer.TileSize - 1) / Renderer.TileSize;
        TileRows = (Height + Renderer.TileSize - 1) / Renderer.TileSize;
        LightTiles = new GpuBuffer(_vk, (ulong)TileCols * TileRows * Renderer.TileStride * 4, BufferUsageFlags.StorageBufferBit, MemoryUse.GpuOnly);
    }

    /// <summary>The see-through layers' records (Renderer.Layers.cs): a uint a pixel, for every layer of a stereo set;
    /// made when a main eye first draws with the layer cap.</summary>
    public GpuBuffer? AlphaLayers;

    /// <summary>This eye's records' address (its layer's part of its stereo set's).</summary>
    public ulong EnsureAlphaLayers()
    {
        var owner = Stereo ?? this;
        owner.AlphaLayers ??= new GpuBuffer(_vk, (ulong)Width * Height * owner.Layers * 4, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryUse.GpuOnly);
        return owner.AlphaLayers.Address + (ulong)Width * Height * Layer * 4;
    }

    /// <summary>The half-size sun-light map and its blur's other half (Renderer.HalfLight.cs), made on first use; a
    /// stereo set's eyes share its pair, as they share the full-size one.</summary>
    public GpuImage? LightHalf, LightHalfTemp;
    public int LightHalfSlot = -1, LightHalfTempSlot = -1;

    public void EnsureLightHalf()
    {
        if (LightHalf != null) return;
        var owner = Stereo ?? this;
        if (owner.LightHalf == null)
        {
            uint w = (Width + 1) / 2, h = (Height + 1) / 2;
            const ImageUsageFlags rt = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit;
            owner.LightHalf = new GpuImage(_vk, w, h, LightMapFormat, rt, dedicated: true);
            owner.LightHalfTemp = new GpuImage(_vk, w, h, LightMapFormat, rt, dedicated: true);
            owner.LightHalfSlot = Register(owner.LightHalf, _bindless.Nearest);
            owner.LightHalfTempSlot = Register(owner.LightHalfTemp, _bindless.Nearest);
        }
        (LightHalf, LightHalfTemp, LightHalfSlot, LightHalfTempSlot) = (owner.LightHalf, owner.LightHalfTemp, owner.LightHalfSlot, owner.LightHalfTempSlot);
    }

    /// <summary>The water exclusion mask and its own depth (Renderer.Water.cs), made when something first excludes
    /// water in a view through these; a stereo set's eyes share one, as their water passes take turns.</summary>
    public GpuImage? WaterMask, WaterMaskDepth;
    public int WaterMaskSlot = -1;
    public const Format WaterMaskFormat = Format.R8Unorm;

    public void EnsureWaterMask()
    {
        if (WaterMask != null) return;
        var owner = Stereo ?? this;
        if (owner.WaterMask == null)
        {
            owner.WaterMask = new GpuImage(_vk, Width, Height, WaterMaskFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, dedicated: true);
            owner.WaterMaskDepth = new GpuImage(_vk, Width, Height, DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit, dedicated: true);
            owner.WaterMaskSlot = Register(owner.WaterMask, _bindless.Nearest);
        }
        (WaterMask, WaterMaskDepth, WaterMaskSlot) = (owner.WaterMask, owner.WaterMaskDepth, owner.WaterMaskSlot);
    }

    int Register(GpuImage img, Sampler sampler)
    {
        int slot = _bindless.AllocTexture();
        _bindless.Write(slot, img.View, sampler);
        return slot;
    }

    public unsafe void Dispose()
    {
        if (LensMaskSlot >= 0) _bindless.FreeTexture(LensMaskSlot);
        LensMaskImage?.Dispose();
        if (Stereo != null)
        {
            foreach (var s in new[] { DiffuseSlot, SpecularSlot, NormalSlot, EmissiveSlot, DepthSlot, HdrSlot }) _bindless.FreeTexture(s);
            foreach (var i in new[] { Diffuse, Specular, Normal, Emissive, Depth, Hdr }) i.Dispose();   // the layer views
            return;
        }
        foreach (var e in Eyes) e.Dispose();
        for (int i = 1; i < _outputs.Length; i++) if (_outputs[i] != Output) _outputs[i].Dispose();
        _vk.Api.DestroyImageView(_vk.Device, LuminanceMip0, null);
        foreach (var s in new[] { DiffuseSlot, SpecularSlot, NormalSlot, EmissiveSlot, DepthSlot, HdrSlot, HdrCopySlot, GlowSlot, GlowTempSlot, SceneCopySlot, DepthCopySlot, LightMapSlot, LightMapTempSlot, LuminanceSlot, LdrSlot, SmaaEdgesSlot, SmaaBlendSlot })
            if (s >= 0) _bindless.FreeTexture(s);
        foreach (var i in new[] { Diffuse, Specular, Normal, Emissive, Depth, Hdr, HdrCopy, Glow, GlowTemp, Output, SceneCopy, DepthCopy, LightMap, LightMapTemp, Luminance, Ldr, SmaaEdges, SmaaBlend })
            i.Dispose();
        LightTiles?.Dispose();
        AlphaLayers?.Dispose();
        if (LightHalf != null)
        {
            _bindless.FreeTexture(LightHalfSlot);
            _bindless.FreeTexture(LightHalfTempSlot);
            LightHalf.Dispose();
            LightHalfTemp!.Dispose();
        }
        if (WaterMask != null)
        {
            _bindless.FreeTexture(WaterMaskSlot);
            WaterMask.Dispose();
            WaterMaskDepth!.Dispose();
        }
    }
}
