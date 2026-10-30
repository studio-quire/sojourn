using System.Runtime.InteropServices;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Sojourn.Render.Realtime;

/// <summary>An eye as the mirror window shows it: its pose in the headset's play space (OpenXR: x right, y up, looking
/// down -z), its field of view's tangents (left and down negative), and the window's pixels its image was fitted into.</summary>
public readonly record struct MirrorEye(Vector3 Position, Quaternion Orientation, float TanLeft, float TanRight, float TanUp, float TanDown, Rect2D Cell);

/// <summary>A layer the frame shows: its picture and its corners in the play space (top left, top right, bottom right,
/// bottom left: the picture's own); Clip: the part of it shown, when the menu goes in parts (MenuClip).</summary>
public readonly record struct MirrorQuad(MirrorPicture Picture, Vector3 TopLeft, Vector3 TopRight, Vector3 BottomRight, Vector3 BottomLeft,
    MenuClip Clip = default);

/// <summary>An eye's layer of the headset's hands image (Overlay.Hands.cs), in COLOR_ATTACHMENT_OPTIMAL and left so: its
/// sRGB view, and the part of it the eye was rendered into (uv).</summary>
public readonly record struct MirrorHands(Image Image, uint Layer, ImageView View, Vector2 Extent);

/// <summary>A quad layer's picture as the mirror keeps it (<see cref="LayerMirror"/>): a copy with mips, RGBA8,
/// gamma-encoded and premultiplied as the overlay's pictures are; <see cref="Extent"/> the part the layer shows (uv).
/// Made by <see cref="LayerMirror.NewPicture"/>; render thread. Disposing lets it go once the frames that may still read
/// it are done.</summary>
public sealed class MirrorPicture : IDisposable
{
    readonly LayerMirror _owner;
    internal GpuImage? Image;
    internal DescriptorSet Set;
    /// <summary>The picture's version in the copy (the caller's to keep; -1: none).</summary>
    public long Version = -1;
    public Vector2 Extent { get; internal set; }
    public bool Ready => Image != null && Set.Handle != 0;

    internal MirrorPicture(LayerMirror owner) => _owner = owner;

    public void Dispose()
    {
        _owner.Retire(this);
        Version = -1;
    }
}

/// <summary>
/// The headset's quad layers (its menus: the panel, the feed, cards, boards, the keyboard, the reader) and the hands'
/// layer over them, drawn into the PC's mirror window as the compositor puts them over the lenses: each picture mapped in
/// perspective by its corners in the eye's view, in the compositor's order (Sojourn.Client's MenuOrder, a menu's part
/// by its clip; the hands last),
/// premultiplied over the eye. The pictures are copies the layers keep for it (<see cref="MirrorPicture"/>: made again
/// only when a picture changes; a board's screen, the renderer's own image, each frame it's up), with mips, since the
/// window shows them a few times smaller than the lenses do. The draw is a quad per layer per eye over the window's
/// pixels. Render thread.
/// </summary>
public sealed unsafe class LayerMirror : IDisposable
{
    const uint MaxMips = 8;
    const int MaxSets = 128;
    const int RetireFrames = 4;    // past the frames in flight
    const float Near = 0.02f;      // the headset's near plane (ViewSetup.HeadsetNear): nearer than this, a layer is cut
    readonly VulkanContext _vk;
    readonly ShaderCompiler _shaders;
    readonly DescriptorSetLayout _setLayout;
    readonly DescriptorPool _pool;
    readonly PipelineLayout _layout;
    readonly Sampler _sampler;
    readonly Dictionary<Format, (Pipeline Pictures, Pipeline Hands)> _pipelines = new();
    readonly Dictionary<ulong, DescriptorSet> _viewSets = new();   // the hands' images' views, kept while the headset is
    readonly List<(Action Free, int Frames)> _retired = new();
    bool _full;

    /// <summary>The GPU profiler's marks (FrameRenderer's): the draw is timed as "mirror".</summary>
    public Action<string>? Mark { get; set; }
    public Action<string>? Log { get; set; }

    [StructLayout(LayoutKind.Sequential)]
    struct Push
    {
        public Vector4 C0, C1, C2, C3, Extent;   // Extent.z: how many of the clip's lines
        public Vector4 K0, K1, K2;               // the clip's lines over the layer (u across, v down, 0-1)
    }

    public LayerMirror(VulkanContext vk, ShaderCompiler shaders)
    {
        _vk = vk;
        _shaders = shaders;
        var api = vk.Api;
        var binding = new DescriptorSetLayoutBinding(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        var lci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding };
        VulkanContext.Check(api.CreateDescriptorSetLayout(vk.Device, in lci, null, out _setLayout), "layer mirror set layout");
        var size = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, MaxSets);
        var pci = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = MaxSets, PoolSizeCount = 1, PPoolSizes = &size,
        };
        VulkanContext.Check(api.CreateDescriptorPool(vk.Device, in pci, null, out _pool), "layer mirror pool");
        _layout = PipelineBuilder.CreateLayout(vk, [_setLayout], (uint)sizeof(Push));
        var sci = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear, MinFilter = Filter.Linear, MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge, AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = MaxMips,
        };
        VulkanContext.Check(api.CreateSampler(vk.Device, in sci, null, out _sampler), "layer mirror sampler");
    }

    public MirrorPicture NewPicture() => new(this);

    (Pipeline Pictures, Pipeline Hands) Pipelines(Format format)
    {
        if (_pipelines.TryGetValue(format, out var p)) return p;
        GraphicsPipelineDesc Desc(params string[] defines) => new()
        {
            Vertex = _shaders.Get("mirrorlayer.vert", ShaderStage.Vertex), Fragment = _shaders.Get("mirrorlayer.frag", ShaderStage.Fragment, defines),
            ColorFormats = [format], Blend = [BlendMode.PremultipliedAlpha], DepthTest = false, DepthWrite = false,
            Cull = CullModeFlags.None, VertexInput = false, Layout = _layout,
        };
        p = (PipelineBuilder.Create(_vk, Desc()), PipelineBuilder.Create(_vk, Desc("HANDS")));
        return _pipelines[format] = p;
    }

    DescriptorSet Allocate(ImageView view)
    {
        var layout = _setLayout;
        var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &layout };
        if (_vk.Api.AllocateDescriptorSets(_vk.Device, in ai, out var set) != Result.Success)
        {
            if (!_full) Log?.Invoke("the mirror's menus: out of descriptor sets; some menus won't show in the window");
            _full = true;
            return default;
        }
        var info = new DescriptorImageInfo(_sampler, view, ImageLayout.ShaderReadOnlyOptimal);
        var w = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &info,
        };
        _vk.Api.UpdateDescriptorSets(_vk.Device, 1, in w, 0, null);
        return set;
    }

    void Free(DescriptorSet set)
    {
        if (set.Handle == 0) return;
        _vk.Api.FreeDescriptorSets(_vk.Device, _pool, 1, in set);
        _full = false;
    }

    /// <summary>The picture's image and set go once the frames that may still read them are done.</summary>
    internal void Retire(MirrorPicture p)
    {
        if (p.Image is { } img)
        {
            var set = p.Set;
            _retired.Add((() => { Free(set); img.Dispose(); }, RetireFrames));
        }
        p.Image = null;
        p.Set = default;
    }

    /// <summary>Once a frame (whether or not it drew): pictures let go long enough ago are freed.</summary>
    public void EndFrame()
    {
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Frames <= 0) { _retired[i].Free(); _retired.RemoveAt(i); }
            else _retired[i] = (_retired[i].Free, _retired[i].Frames - 1);
        }
    }

    /// <summary>The picture's image at least this big (grow: kept if bigger, so a picture that changes size doesn't make
    /// one each time) or exactly this big, with its mips and set.</summary>
    void Ensure(MirrorPicture p, uint w, uint h, bool grow)
    {
        if (p.Image is { } have && (grow ? have.Width >= w && have.Height >= h : have.Width == w && have.Height == h))
        {
            if (p.Set.Handle == 0) p.Set = Allocate(have.View);   // (the pool was full when it was made)
            return;
        }
        if (grow && p.Image is { } old) (w, h) = (Math.Max(w, old.Width), Math.Max(h, old.Height));
        Retire(p);
        uint mips = 1;
        while (mips < MaxMips && Math.Max(w, h) >> (int)mips > 0) mips++;
        p.Image = new GpuImage(_vk, w, h, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit, mips);
        p.Set = Allocate(p.Image.View);
    }

    /// <summary>A new picture from a staging buffer (rows of rowLength pixels, RGBA8 gamma-encoded, premultiplied): w x h
    /// of it shown.</summary>
    public void Upload(CommandBuffer cmd, MirrorPicture p, Buffer staging, int rowLength, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        Ensure(p, (uint)((w + 127) / 128 * 128), (uint)((h + 127) / 128 * 128), grow: true);
        var img = p.Image!;
        var api = _vk.Api;
        img.Transition(cmd, ImageLayout.TransferDstOptimal);
        if (w < img.Width || h < img.Height)
        {
            // clear past the picture: its mips would carry the last picture's edge in
            var clear = new ClearColorValue(0f, 0f, 0f, 0f);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            api.CmdClearColorImage(cmd, img.Handle, ImageLayout.TransferDstOptimal, in clear, 1, in range);
            GpuImage.Barrier(_vk, cmd, img.Handle, img.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.TransferDstOptimal, 0, 1, 0, 1);
        }
        var copy = new BufferImageCopy
        {
            BufferRowLength = (uint)rowLength,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D((uint)w, (uint)h, 1),
        };
        api.CmdCopyBufferToImage(cmd, staging, img.Handle, ImageLayout.TransferDstOptimal, 1, in copy);
        RecordMips(cmd, img);
        p.Extent = new Vector2((float)w / img.Width, (float)h / img.Height);
    }

    /// <summary>A new picture from an image the renderer drew (TransferSrcOptimal, left so): an RGBA8 one copied bit for
    /// bit (an sRGB one's bytes are gamma-encoded already), any other converted.</summary>
    public void Copy(CommandBuffer cmd, MirrorPicture p, GpuImage src)
    {
        Ensure(p, src.Width, src.Height, grow: false);
        var img = p.Image!;
        var api = _vk.Api;
        img.Transition(cmd, ImageLayout.TransferDstOptimal);
        if (src.Format is Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb)
        {
            var copy = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                Extent = new Extent3D(src.Width, src.Height, 1),
            };
            api.CmdCopyImage(cmd, src.Handle, ImageLayout.TransferSrcOptimal, img.Handle, ImageLayout.TransferDstOptimal, 1, in copy);
        }
        else
        {
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            };
            blit.SrcOffsets[1] = new Offset3D((int)src.Width, (int)src.Height, 1);
            blit.DstOffsets[1] = new Offset3D((int)src.Width, (int)src.Height, 1);
            api.CmdBlitImage(cmd, src.Handle, ImageLayout.TransferSrcOptimal, img.Handle, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Nearest);
        }
        RecordMips(cmd, img);
        p.Extent = Vector2.One;
    }

    /// <summary>Level 0 (TransferDstOptimal) down the mips, then all of it shader-readable.</summary>
    void RecordMips(CommandBuffer cmd, GpuImage img)
    {
        var api = _vk.Api;
        int mw = (int)img.Width, mh = (int)img.Height;
        for (uint m = 1; m < img.Mips; m++)
        {
            GpuImage.Barrier(_vk, cmd, img.Handle, img.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, m - 1, 1, 0, 1);
            int nw = Math.Max(1, mw / 2), nh = Math.Max(1, mh / 2);
            var down = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m, 0, 1),
            };
            down.SrcOffsets[1] = new Offset3D(mw, mh, 1);
            down.DstOffsets[1] = new Offset3D(nw, nh, 1);
            api.CmdBlitImage(cmd, img.Handle, ImageLayout.TransferSrcOptimal, img.Handle, ImageLayout.TransferDstOptimal, 1, in down, Filter.Linear);
            (mw, mh) = (nw, nh);
        }
        if (img.Mips > 1)
            GpuImage.Barrier(_vk, cmd, img.Handle, img.Aspect, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, 0, img.Mips - 1, 0, 1);
        GpuImage.Barrier(_vk, cmd, img.Handle, img.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, img.Mips - 1, 1, 0, 1);
        img.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    /// <summary>A play-space point in the eye's clip space: x and y by its field of view (y down, as Vulkan's), depth
    /// Near / distance, so what's within Near of the eye or behind it is clipped.</summary>
    static Vector4 Clip(in MirrorEye e, Vector3 p)
    {
        var l = Vector3.Transform(p - e.Position, Quaternion.Conjugate(e.Orientation));
        float d = -l.Z;
        float x = (2 * l.X - (e.TanRight + e.TanLeft) * d) / (e.TanRight - e.TanLeft);
        float y = (2 * l.Y - (e.TanUp + e.TanDown) * d) / (e.TanDown - e.TanUp);
        return new Vector4(x, y, Near, d);
    }

    /// <summary>
    /// Draw the layers over the eyes in the window's image (target: its view, COLOR_ATTACHMENT_OPTIMAL before and after):
    /// for each eye, in its cell, every quad in order, then that eye's hands where the quads are.
    /// </summary>
    public void Draw(CommandBuffer cmd, ImageView target, Format format, Extent2D size, ReadOnlySpan<MirrorEye> eyes, IReadOnlyList<MirrorQuad> quads,
        ReadOnlySpan<MirrorHands?> hands)
    {
        var api = _vk.Api;
        var (pictures, handsPipe) = Pipelines(format);
        // the hands' images are the headset's, released as attachments: readable here, and back
        foreach (var h in hands)
            if (h is { } hv) GpuImage.Barrier(_vk, cmd, hv.Image, ImageAspectFlags.ColorBit, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, 0, 1, hv.Layer, 1);
        var att = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = target, ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load, StoreOp = AttachmentStoreOp.Store,
        };
        var ri = new RenderingInfo
        {
            SType = StructureType.RenderingInfo, RenderArea = new Rect2D(new Offset2D(0, 0), size), LayerCount = 1,
            ColorAttachmentCount = 1, PColorAttachments = &att,
        };
        api.CmdBeginRendering(cmd, in ri);
        for (int e = 0; e < eyes.Length; e++)
        {
            var eye = eyes[e];
            var cell = eye.Cell;
            var vp = new Viewport(cell.Offset.X, cell.Offset.Y, cell.Extent.Width, cell.Extent.Height, 0, 1);
            api.CmdSetViewport(cmd, 0, 1, in vp);
            api.CmdSetScissor(cmd, 0, 1, in cell);
            api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pictures);
            // where the quads are in the cell (normalised), for the hands' draw: all of it if one reaches behind the eye
            float x0 = 1, y0 = 1, x1 = -1, y1 = -1;
            bool all = false;
            foreach (var q in quads)
            {
                if (!q.Picture.Ready) continue;
                var push = new Push
                {
                    C0 = Clip(eye, q.TopLeft), C1 = Clip(eye, q.TopRight), C2 = Clip(eye, q.BottomRight), C3 = Clip(eye, q.BottomLeft),
                    Extent = new Vector4(q.Picture.Extent, q.Clip.Count, 0),
                    K0 = q.Clip.P0, K1 = q.Clip.P1, K2 = q.Clip.P2,
                };
                foreach (var c in (ReadOnlySpan<Vector4>)[push.C0, push.C1, push.C2, push.C3])
                {
                    if (c.W < Near) { all = true; break; }
                    (x0, y0, x1, y1) = (MathF.Min(x0, c.X / c.W), MathF.Min(y0, c.Y / c.W), MathF.Max(x1, c.X / c.W), MathF.Max(y1, c.Y / c.W));
                }
                var set = q.Picture.Set;
                api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);
                api.CmdPushConstants(cmd, _layout, ShaderStageFlags.All, 0, (uint)sizeof(Push), &push);
                api.CmdDraw(cmd, 6, 1, 0, 0);
            }
            if (e >= hands.Length || hands[e] is not { } h || (!all && x1 <= x0) || HandsSet(h.View) is not { Handle: not 0 } hs) continue;
            if (!all)
            {
                // a few pixels round the quads, within the cell
                int px0 = (int)Math.Clamp(MathF.Floor((x0 + 1) / 2 * cell.Extent.Width) - 2, 0, cell.Extent.Width);
                int py0 = (int)Math.Clamp(MathF.Floor((y0 + 1) / 2 * cell.Extent.Height) - 2, 0, cell.Extent.Height);
                int px1 = (int)Math.Clamp(MathF.Ceiling((x1 + 1) / 2 * cell.Extent.Width) + 2, 0, cell.Extent.Width);
                int py1 = (int)Math.Clamp(MathF.Ceiling((y1 + 1) / 2 * cell.Extent.Height) + 2, 0, cell.Extent.Height);
                if (px1 <= px0 || py1 <= py0) continue;
                var box = new Rect2D(new Offset2D(cell.Offset.X + px0, cell.Offset.Y + py0), new Extent2D((uint)(px1 - px0), (uint)(py1 - py0)));
                api.CmdSetScissor(cmd, 0, 1, in box);
            }
            var whole = new Push
            {
                C0 = new Vector4(-1, -1, 0.5f, 1), C1 = new Vector4(1, -1, 0.5f, 1), C2 = new Vector4(1, 1, 0.5f, 1), C3 = new Vector4(-1, 1, 0.5f, 1),
                Extent = new Vector4(h.Extent, 0, 0),
            };
            api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, handsPipe);
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, in hs, 0, null);
            api.CmdPushConstants(cmd, _layout, ShaderStageFlags.All, 0, (uint)sizeof(Push), &whole);
            api.CmdDraw(cmd, 6, 1, 0, 0);
        }
        api.CmdEndRendering(cmd);
        foreach (var h in hands)
            if (h is { } hv) GpuImage.Barrier(_vk, cmd, hv.Image, ImageAspectFlags.ColorBit, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.ColorAttachmentOptimal, 0, 1, hv.Layer, 1);
        Mark?.Invoke("mirror");
    }

    DescriptorSet? HandsSet(ImageView view)
    {
        if (_viewSets.TryGetValue(view.Handle, out var set)) return set;
        set = Allocate(view);
        if (set.Handle != 0) _viewSets[view.Handle] = set;
        return set;
    }

    public void Dispose()
    {
        var api = _vk.Api;
        foreach (var r in _retired) r.Free();
        _retired.Clear();
        foreach (var (a, b) in _pipelines.Values)
        {
            api.DestroyPipeline(_vk.Device, a, null);
            api.DestroyPipeline(_vk.Device, b, null);
        }
        api.DestroySampler(_vk.Device, _sampler, null);
        api.DestroyPipelineLayout(_vk.Device, _layout, null);
        api.DestroyDescriptorPool(_vk.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_vk.Device, _setLayout, null);
    }
}
