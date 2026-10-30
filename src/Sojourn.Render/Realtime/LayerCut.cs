using System.Runtime.InteropServices;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Realtime;

/// <summary>
/// A headset menu's part (Sojourn.Client's MenuOrder and QuadLayer pieces, Claude Opus 5.5, 2026-10-28): where another
/// menu passes through one, it's submitted as two quad layers, each the whole picture with the other side of the line
/// cleared, laid on either side of the other menu. This clears it: one triangle over the image (already holding the
/// picture, COLOR_ATTACHMENT_OPTIMAL before and after), multiplying every pixel, colour and alpha, by how much of it is
/// kept (antialiased over a pixel along each line, so the two parts' edges add up to the picture). Premultiplied
/// pictures stay premultiplied. Render thread.
/// </summary>
public sealed unsafe class LayerCut : IDisposable
{
    readonly VulkanContext _vk;
    readonly ShaderCompiler _shaders;
    readonly PipelineLayout _layout;
    readonly Dictionary<Format, Pipeline> _pipelines = new();

    [StructLayout(LayoutKind.Sequential)]
    struct Push
    {
        public Vector4 P0, P1, P2, Info;
    }

    public LayerCut(VulkanContext vk, ShaderCompiler shaders)
    {
        _vk = vk;
        _shaders = shaders;
        _layout = PipelineBuilder.CreateLayout(vk, [], (uint)sizeof(Push));
    }

    Pipeline PipelineFor(Format format)
    {
        if (_pipelines.TryGetValue(format, out var p)) return p;
        p = PipelineBuilder.Create(_vk, new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("handcut.vert", ShaderStage.Vertex), Fragment = _shaders.Get("layercut.frag", ShaderStage.Fragment),
            ColorFormats = [format], DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = _layout,
            // dst * the shader's alpha (what's kept), colour and alpha alike
            Blend0 = new PipelineColorBlendAttachmentState(true, BlendFactor.Zero, BlendFactor.SrcAlpha, BlendOp.Add,
                BlendFactor.Zero, BlendFactor.SrcAlpha, BlendOp.Add,
                ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit),
        });
        return _pipelines[format] = p;
    }

    /// <summary>Clear what the clip doesn't keep of a w x h image (its view, in this format).</summary>
    public void Record(CommandBuffer cmd, ImageView view, Format format, uint w, uint h, in MenuClip clip)
    {
        if (clip.Whole || w == 0 || h == 0) return;
        var px = clip.InPixels(w, h);
        var push = new Push { P0 = px.P0, P1 = px.P1, P2 = px.P2, Info = new Vector4(px.Count, 0, 0, 0) };
        var api = _vk.Api;
        var att = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = view, ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load, StoreOp = AttachmentStoreOp.Store,
        };
        var area = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h));
        var ri = new RenderingInfo { SType = StructureType.RenderingInfo, RenderArea = area, LayerCount = 1, ColorAttachmentCount = 1, PColorAttachments = &att };
        var vp = new Viewport(0, 0, w, h, 0, 1);
        api.CmdBeginRendering(cmd, in ri);
        api.CmdSetViewport(cmd, 0, 1, in vp);
        api.CmdSetScissor(cmd, 0, 1, in area);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, PipelineFor(format));
        api.CmdPushConstants(cmd, _layout, ShaderStageFlags.All, 0, (uint)sizeof(Push), &push);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
    }

    /// <summary>A view of a swapchain's image for <see cref="Record"/> (the caller's to destroy with the swapchain).</summary>
    public ImageView ViewOf(Image image, Format format)
    {
        var vci = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = image, ViewType = ImageViewType.Type2D, Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        VulkanContext.Check(_vk.Api.CreateImageView(_vk.Device, in vci, null, out var v), "vkCreateImageView (menu part)");
        return v;
    }

    public void DestroyView(ImageView view)
    {
        if (view.Handle != 0) _vk.Api.DestroyImageView(_vk.Device, view, null);
    }

    public void Dispose()
    {
        foreach (var p in _pipelines.Values) _vk.Api.DestroyPipeline(_vk.Device, p, null);
        _vk.Api.DestroyPipelineLayout(_vk.Device, _layout, null);
    }
}
