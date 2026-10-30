using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Realtime;

/// <summary>
/// The headset's menus this frame (its quad layers: the panel, the feed, cards, boards, the keyboard, the reader), for
/// the person's hands to be drawn over them where they're in front: each by its corners in the frame's region (top
/// left, top right, bottom right, bottom left). Hands: where the controllers are (the region); beams, dots and tokens
/// the overlay draws near them count as held, and show over the menus with the hands.
/// </summary>
public sealed record HandCut(IReadOnlyList<Vector3[]> Menus, IReadOnlyList<Vector3> Hands);

/// <summary>Where a display wants the hands over its menus: an image the eye's size (a layer of the headset's hand
/// swapchain, Sojourn.Client's XrSession) the renderer fills with the hands, premultiplied, clear elsewhere.</summary>
public readonly record struct HandsTarget(ImageView View, Format Format, uint Width, uint Height);

// The hands over the headset's menus (the user's decision, 2026-10-27): the menus are quad layers the compositor puts
// over the eye images, so nothing in the eyes can cover them, and a hand in front of one was hidden under its glass,
// the frost smearing it to a brown splotch. Per eye, after the scene and before the overlay:
//   - a mask (R8, the eye's size): 1 where the person's own body (InstanceFlags.Self, marked in the G-buffer's flag)
//     is in front of every menu whose outline it's inside, from the eye's depth (handcut.glsl), or with walls hiding
//     menus anything in the eye that is (Overlay.Walls.cs: the compositor's own test is patchy); then the held things
//     (the pointer's beam, the wrist's tokens: what the overlay draws near the hands) where they're in front, by their
//     own depth;
//   - the frost leaves the mask's pixels sharp, and its backdrop has them taken out (weighted by what's left), so the
//     hands neither blur nor bleed into the glass around them;
//   - after the eye goes to the headset, the eye where the mask is (premultiplied by it, clear elsewhere) goes into a
//     swapchain of the headset's own, which it submits as a second projection layer over the quads, alpha-blended as
//     the quads are (same poses, time and depth as the eyes' layer, so late frames reproject both alike).
// Without a cut (the desktop, no menus up) none of it runs.
public sealed unsafe partial class Overlay
{
    const int CutMenus = 16;   // handcut.glsl's CUT_MENUS
    const int CutBlockBytes = 64 + 16 + 16 + CutMenus * 3 * 16;
    const int CutStride = 1024;   // a block per eye in each slot's buffer (past any uniform offset alignment)
    const float HeldReach = 0.4f; // what's drawn this near a hand is held (tokens sit up to ~0.3 m up the forearm)
    const float CutReach = 0.05f; // handcut.glsl's CUT_REACH: radians past a menu's edge the cut still takes what's in front

    DescriptorSetLayout _cutSetLayout;
    DescriptorPool _cutPool;
    DescriptorSet[] _cutSets = [];
    GpuImage?[][] _cutSetImages = [];
    GpuBuffer[] _cutBuffers = [];
    PipelineLayout _cutLayout, _heldLayout;
    Pipeline _cutMask, _cutHeld, _cutBackdrop;
    readonly Dictionary<Format, Pipeline> _cutAlpha = new();
    GpuImage? _mask;
    GpuImage _noMask = null!;
    ImageView _backdropMip0;
    bool _cutThisEye;
    Rect2D _cutBox;   // this eye's menus on screen: the cut's passes draw only there (the rest is cleared)

    /// <summary>The GPU profiler's marks (FrameRenderer's): the cut's passes are timed as "hands".</summary>
    public Action<string>? Mark { get; set; }

    /// <summary>The mask the overlay's frost and previews read (binding 6): this eye's, or a zero pixel.</summary>
    GpuImage MaskBound => _cutThisEye && _mask != null ? _mask : _noMask;

    void CreateCut(ShaderCompiler shaders, int framesInFlight)
    {
        var api = _vk.Api;
        var b = stackalloc DescriptorSetLayoutBinding[5];
        b[0] = new DescriptorSetLayoutBinding(0, DescriptorType.UniformBuffer, 1, ShaderStageFlags.FragmentBit);
        for (uint i = 1; i < 5; i++) b[i] = new DescriptorSetLayoutBinding(i, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        var lci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 5, PBindings = b };
        VulkanContext.Check(api.CreateDescriptorSetLayout(_vk.Device, in lci, null, out _cutSetLayout), "hand cut set layout");
        int sets = framesInFlight * 2;
        var sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize(DescriptorType.UniformBuffer, (uint)sets);
        sizes[1] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, (uint)(sets * 4));
        var pci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)sets, PoolSizeCount = 2, PPoolSizes = sizes };
        VulkanContext.Check(api.CreateDescriptorPool(_vk.Device, in pci, null, out _cutPool), "hand cut pool");
        _cutSets = new DescriptorSet[sets];
        _cutSetImages = Enumerable.Range(0, sets).Select(_ => new GpuImage?[4]).ToArray();
        var layout = _cutSetLayout;
        for (int i = 0; i < sets; i++)
        {
            var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _cutPool, DescriptorSetCount = 1, PSetLayouts = &layout };
            VulkanContext.Check(api.AllocateDescriptorSets(_vk.Device, in ai, out _cutSets[i]), "hand cut set");
        }
        _cutBuffers = Enumerable.Range(0, framesInFlight)
            .Select(_ => new GpuBuffer(_vk, CutStride * 2, BufferUsageFlags.UniformBufferBit, MemoryUse.Upload)).ToArray();
        _cutLayout = PipelineBuilder.CreateLayout(_vk, [_cutSetLayout], 0);
        _heldLayout = PipelineBuilder.CreateLayout(_vk, [_setLayout, _cutSetLayout], 0);

        var vs = shaders.Get("handcut.vert", ShaderStage.Vertex);
        _cutMask = PipelineBuilder.Create(_vk, new GraphicsPipelineDesc
        {
            Vertex = vs, Fragment = shaders.Get("handcut.frag", ShaderStage.Fragment),
            ColorFormats = [Format.R8Unorm], DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None,
            VertexInput = false, Layout = _cutLayout,
        });
        _cutHeld = PipelineBuilder.Create(_vk, new GraphicsPipelineDesc
        {
            Vertex = shaders.Get("overlay.vert", ShaderStage.Vertex), Fragment = shaders.Get("overlay.frag", ShaderStage.Fragment, "HELD_CUT"),
            ColorFormats = [Format.R8Unorm], DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, Layout = _heldLayout,
            Blend0 = new PipelineColorBlendAttachmentState(true, BlendFactor.One, BlendFactor.One, BlendOp.Max,
                BlendFactor.One, BlendFactor.One, BlendOp.Max, ColorComponentFlags.RBit),
        });
        // the backdrop's colour scaled by what's left of the pixel (the shader's alpha), its alpha replaced by it
        _cutBackdrop = PipelineBuilder.Create(_vk, new GraphicsPipelineDesc
        {
            Vertex = vs, Fragment = shaders.Get("handcut.frag", ShaderStage.Fragment, "MASKOUT"),
            ColorFormats = [RenderTargets.OutputFormat], DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None,
            VertexInput = false, Layout = _cutLayout,
            Blend0 = new PipelineColorBlendAttachmentState(true, BlendFactor.Zero, BlendFactor.SrcAlpha, BlendOp.Add,
                BlendFactor.One, BlendFactor.Zero, BlendOp.Add,
                ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit),
        });
        _cutShaders = shaders;
        _noMask = new GpuImage(_vk, 1, 1, Format.R8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        _noMaskCleared = false;
    }

    ShaderCompiler? _cutShaders;
    bool _noMaskCleared;

    Pipeline HandsPipeline(Format format)
    {
        if (_cutAlpha.TryGetValue(format, out var p)) return p;
        p = PipelineBuilder.Create(_vk, new GraphicsPipelineDesc
        {
            Vertex = _cutShaders!.Get("handcut.vert", ShaderStage.Vertex), Fragment = _cutShaders.Get("handcut.frag", ShaderStage.Fragment, "HANDS"),
            ColorFormats = [format], DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = _cutLayout,
        });
        return _cutAlpha[format] = p;
    }

    /// <summary>Is a beam, dot or token at this point held (near a hand)?</summary>
    static bool Held(HandCut? cut, Vector3 at)
    {
        if (cut == null) return false;
        foreach (var h in cut.Hands)
            if (Vector3.DistanceSquared(h, at) < HeldReach * HeldReach) return true;
        return false;
    }

    /// <summary>Is a dot held: near a hand, or where a held beam ends (the pointer's)?</summary>
    static bool HeldDot(HandCut cut, OverlayScene scene, OverlayDot dot)
    {
        if (Held(cut, dot.At)) return true;
        foreach (var b in scene.Beams)
            if (Vector3.DistanceSquared(b.To, dot.At) < 0.02f * 0.02f && Held(cut, b.From)) return true;
        return false;
    }

    /// <summary>This eye's cut block and set: the menus in the eye's region frame, the eye, its inverse projection.</summary>
    void WriteCut(int slot, int eye, HandCut cut, ViewSetup view, GpuImage target, GpuImage depth, GpuImage normal, bool world)
    {
        int si = slot * 2 + (eye & 1);
        var buf = _cutBuffers[slot];
        var block = buf.Mapped.Slice((eye & 1) * CutStride, CutBlockBytes);
        Matrix4x4.Invert(view.View * view.Proj, out var inv);
        var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(block);
        f.Clear();
        System.Runtime.InteropServices.MemoryMarshal.Write(block, in inv);
        int n = 0;
        foreach (var m in cut.Menus)
        {
            if (n == CutMenus) break;
            if (m is not { Length: 4 }) continue;
            var c = (m[0] + m[2]) * 0.5f;
            var r = (m[1] - m[0]) * 0.5f;
            var u = (m[0] - m[3]) * 0.5f;
            int o = 24 + n * 12;
            (f[o], f[o + 1], f[o + 2]) = (c.X, c.Y, c.Z);
            (f[o + 4], f[o + 5], f[o + 6]) = (r.X, r.Y, r.Z);
            (f[o + 8], f[o + 9], f[o + 10]) = (u.X, u.Y, u.Z);
            n++;
        }
        (f[16], f[17], f[18], f[19]) = (view.Position.X, view.Position.Y, view.Position.Z, n);
        (f[20], f[21], f[23]) = (target.Width, target.Height, world ? 1 : 0);
        _cutBox = MenusOnScreen(cut, view.View * view.Proj, target.Width, target.Height, view.Position);

        var want = new[] { depth, normal, _mask!, target };
        var have = _cutSetImages[si];
        if (want[0] == have[0] && want[1] == have[1] && want[2] == have[2] && want[3] == have[3]) return;
        var bi = new DescriptorBufferInfo(buf.Handle, (ulong)((eye & 1) * CutStride), CutBlockBytes);
        var infos = stackalloc DescriptorImageInfo[4];
        var w = stackalloc WriteDescriptorSet[5];
        w[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = _cutSets[si], DstBinding = 0, DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &bi,
        };
        for (int i = 0; i < 4; i++)
        {
            have[i] = want[i];
            infos[i] = new DescriptorImageInfo(_sampler, want[i].View, ImageLayout.ShaderReadOnlyOptimal);
            w[i + 1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = _cutSets[si], DstBinding = (uint)(i + 1), DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &infos[i],
            };
        }
        _vk.Api.UpdateDescriptorSets(_vk.Device, 5, w, 0, null);
    }

    /// <summary>The pixels the menus cover in an eye, with the reach past their edges the cut takes too (handcut.glsl's
    /// CUT_REACH): their grown corners' bounds, a few pixels wider; all of it when one reaches behind the eye.</summary>
    static Rect2D MenusOnScreen(HandCut cut, Matrix4x4 viewProj, uint w, uint h, Vector3 eye)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var m in cut.Menus)
            for (int k = 0; k < m.Length; k++)
            {
                var p = m[k];
                if (m.Length == 4)
                {
                    // out from the middle along the sheet's sides, by the reach at that corner's distance (a little more:
                    // the shader measures it to where the ray meets the plane, a touch farther off-centre)
                    var mid = (m[0] + m[2]) * 0.5f;
                    var r = m[1] - m[0];
                    var u = m[0] - m[3];
                    if (r.LengthSquared() > 1e-12f && u.LengthSquared() > 1e-12f)
                    {
                        r = Vector3.Normalize(r);
                        u = Vector3.Normalize(u);
                        float grow = CutReach * 1.25f * Vector3.Distance(p, eye);
                        var off = p - mid;
                        p += r * MathF.Sign(Vector3.Dot(off, r)) * grow + u * MathF.Sign(Vector3.Dot(off, u)) * grow;
                    }
                }
                var c = Vector4.Transform(new Vector4(p, 1), viewProj);
                if (c.W < 0.02f) return new Rect2D(new Offset2D(0, 0), new Extent2D(w, h));
                float x = (c.X / c.W + 1) * 0.5f * w, y = (c.Y / c.W + 1) * 0.5f * h;
                (x0, y0, x1, y1) = (MathF.Min(x0, x), MathF.Min(y0, y), MathF.Max(x1, x), MathF.Max(y1, y));
            }
        int ix0 = (int)Math.Clamp(MathF.Floor(x0) - 4, 0, w), iy0 = (int)Math.Clamp(MathF.Floor(y0) - 4, 0, h);
        int ix1 = (int)Math.Clamp(MathF.Ceiling(x1) + 4, 0, w), iy1 = (int)Math.Clamp(MathF.Ceiling(y1) + 4, 0, h);
        if (ix1 <= ix0 || iy1 <= iy0) return new Rect2D(new Offset2D(0, 0), new Extent2D(1, 1));
        return new Rect2D(new Offset2D(ix0, iy0), new Extent2D((uint)(ix1 - ix0), (uint)(iy1 - iy0)));
    }

    /// <summary>Start this eye's cut: whether it runs (a cut, the eye's own G-buffer at its size), the mask made the
    /// eye's size, the zero pixel cleared once.</summary>
    void BeginCut(FrameResources fr, CommandBuffer cmd, int slot, int eye, OverlayScene scene, ViewSetup view, GpuImage target, GpuImage? depth, GpuImage? normal)
    {
        if (!_noMaskCleared)
        {
            _noMask.Transition(cmd, ImageLayout.TransferDstOptimal);
            var zero = new ClearColorValue(0f, 0f, 0f, 0f);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            _vk.Api.CmdClearColorImage(cmd, _noMask.Handle, ImageLayout.TransferDstOptimal, in zero, 1, in range);
            _noMask.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
            _noMaskCleared = true;
        }
        _cutThisEye = scene.Cut is { Menus.Count: > 0 } && depth != null && normal != null
            && depth.Width == target.Width && depth.Height == target.Height && normal.Width == target.Width && normal.Height == target.Height;
        if (!_cutThisEye) return;
        if (_mask == null || _mask.Width != target.Width || _mask.Height != target.Height)
        {
            if (_mask != null) fr.Retire.Add(_mask.Dispose);
            _mask = new GpuImage(_vk, target.Width, target.Height, Format.R8Unorm, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit);
        }
        WriteCut(slot, eye, scene.Cut!, view, target, depth!, normal!, WorldOverMenus(scene));
    }

    /// <summary>The mask: the own body over the menus, then the held things (verts[heldFrom..heldTo), already in the
    /// eye's vertex buffer).</summary>
    void RecordMask(CommandBuffer cmd, int slot, int eye, GpuImage depth, GpuImage normal, int heldFrom, int heldTo, GpuBuffer vertices)
    {
        var api = _vk.Api;
        var mask = _mask!;
        Mark?.Invoke("present");
        depth.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        normal.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        mask.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        var att = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = mask.View, ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
        };
        var area = new Rect2D(new Offset2D(0, 0), new Extent2D(mask.Width, mask.Height));
        var ri = new RenderingInfo { SType = StructureType.RenderingInfo, RenderArea = area, LayerCount = 1, ColorAttachmentCount = 1, PColorAttachments = &att };
        var vp = new Viewport(0, 0, mask.Width, mask.Height, 0, 1);
        var set = _cutSets[slot * 2 + (eye & 1)];
        var box = _cutBox;
        api.CmdBeginRendering(cmd, in ri);
        api.CmdSetViewport(cmd, 0, 1, in vp);
        api.CmdSetScissor(cmd, 0, 1, in box);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _cutMask);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _cutLayout, 0, 1, in set, 0, null);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        if (heldTo > heldFrom)
        {
            api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _cutHeld);
            var sets = stackalloc DescriptorSet[2] { _sets[slot], set };
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _heldLayout, 0, 2, sets, 0, null);
            var vb = vertices.Handle;
            ulong vbOffset = 0;
            api.CmdBindVertexBuffers(cmd, 0, 1, in vb, in vbOffset);
            api.CmdDraw(cmd, (uint)(heldTo - heldFrom), 1, (uint)heldFrom, 0);
        }
        api.CmdEndRendering(cmd);
        mask.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        Mark?.Invoke("hands");
    }

    /// <summary>Take the mask's pixels out of the backdrop's top level (TransferDstOptimal before and after): colour and
    /// alpha scaled by what's left of each.</summary>
    void MaskBackdrop(CommandBuffer cmd, int slot, int eye)
    {
        var api = _vk.Api;
        var b = _backdrop;
        if (_backdropMip0.Handle == 0) _backdropMip0 = b.CreateView(ImageViewType.Type2D, 0, 1, 0, 1);
        Mark?.Invoke("present");
        GpuImage.Barrier(_vk, cmd, b.Handle, b.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.ColorAttachmentOptimal, 0, 1, 0, 1);
        var att = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = _backdropMip0, ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load, StoreOp = AttachmentStoreOp.Store,
        };
        var area = new Rect2D(new Offset2D(0, 0), new Extent2D(b.Width, b.Height));
        var ri = new RenderingInfo { SType = StructureType.RenderingInfo, RenderArea = area, LayerCount = 1, ColorAttachmentCount = 1, PColorAttachments = &att };
        var vp = new Viewport(0, 0, b.Width, b.Height, 0, 1);
        var set = _cutSets[slot * 2 + (eye & 1)];
        // only under the menus (elsewhere the mask is clear, nothing to take out), at the backdrop's half size
        int bx0 = Math.Min((int)b.Width - 1, _cutBox.Offset.X / 2), by0 = Math.Min((int)b.Height - 1, _cutBox.Offset.Y / 2);
        var box = new Rect2D(new Offset2D(bx0, by0), new Extent2D(
            (uint)Math.Clamp((_cutBox.Offset.X + (int)_cutBox.Extent.Width + 1) / 2 - bx0, 1, (int)b.Width - bx0),
            (uint)Math.Clamp((_cutBox.Offset.Y + (int)_cutBox.Extent.Height + 1) / 2 - by0, 1, (int)b.Height - by0)));
        api.CmdBeginRendering(cmd, in ri);
        api.CmdSetViewport(cmd, 0, 1, in vp);
        api.CmdSetScissor(cmd, 0, 1, in box);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _cutBackdrop);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _cutLayout, 0, 1, in set, 0, null);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
        GpuImage.Barrier(_vk, cmd, b.Handle, b.Aspect, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferDstOptimal, 0, 1, 0, 1);
        Mark?.Invoke("hands");
    }

    /// <summary>Whether the last <see cref="Record"/> made this eye's mask (the hands can go over the menus).</summary>
    public bool HasHands => _cutThisEye;

    /// <summary>Draw this eye's hands (the finished eye image where the mask is, premultiplied) into the display's
    /// hand layer, clear elsewhere: after the target's RecordPresent. The eye image is left TransferSrcOptimal.</summary>
    public void RecordHands(CommandBuffer cmd, int slot, int eye, GpuImage eyeImage, HandsTarget t)
    {
        if (!_cutThisEye || _mask is not { } mask || t.Width != mask.Width || t.Height != mask.Height) return;
        var api = _vk.Api;
        eyeImage.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        var att = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = t.View, ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
        };
        var area = new Rect2D(new Offset2D(0, 0), new Extent2D(t.Width, t.Height));
        var ri = new RenderingInfo { SType = StructureType.RenderingInfo, RenderArea = area, LayerCount = 1, ColorAttachmentCount = 1, PColorAttachments = &att };
        var vp = new Viewport(0, 0, t.Width, t.Height, 0, 1);
        var set = _cutSets[slot * 2 + (eye & 1)];
        var box = _cutBox;
        Mark?.Invoke("present");
        api.CmdBeginRendering(cmd, in ri);
        api.CmdSetViewport(cmd, 0, 1, in vp);
        api.CmdSetScissor(cmd, 0, 1, in box);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, HandsPipeline(t.Format));
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _cutLayout, 0, 1, in set, 0, null);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
        eyeImage.Transition(cmd, ImageLayout.TransferSrcOptimal);
        Mark?.Invoke("hands");
    }

    void DisposeCut()
    {
        var api = _vk.Api;
        _mask?.Dispose();
        _noMask.Dispose();
        if (_backdropMip0.Handle != 0) api.DestroyImageView(_vk.Device, _backdropMip0, null);
        foreach (var b in _cutBuffers) b.Dispose();
        foreach (var p in _cutAlpha.Values) api.DestroyPipeline(_vk.Device, p, null);
        api.DestroyPipeline(_vk.Device, _cutMask, null);
        api.DestroyPipeline(_vk.Device, _cutHeld, null);
        api.DestroyPipeline(_vk.Device, _cutBackdrop, null);
        api.DestroyPipelineLayout(_vk.Device, _cutLayout, null);
        api.DestroyPipelineLayout(_vk.Device, _heldLayout, null);
        api.DestroyDescriptorPool(_vk.Device, _cutPool, null);
        api.DestroyDescriptorSetLayout(_vk.Device, _cutSetLayout, null);
    }
}
