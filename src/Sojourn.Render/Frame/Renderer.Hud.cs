using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

/// <summary>
/// HUD attachments (Realtime/Hud.cs): drawn with their own orthographic view into an image of their own (premultiplied,
/// cleared to nothing), and that image laid over an eye's output. The viewer draws HUDs with its forward shaders under
/// its HUD flag, after gamma: no sun, sky or fog, glow or shine, so a texture and its tint as they are, blended in sRGB.
/// The desktop's image is UNORM and keeps that; the headset's is sRGB (linear light), as its quad layer wants.
/// </summary>
public sealed unsafe partial class Renderer
{
    /// <summary>The format of a HUD image: linear light in an sRGB image (the headset), or sRGB bytes as the viewer blends them.</summary>
    public static Format HudFormat(bool linear) => linear ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;
    readonly Pipeline[] _hudOpaque = new Pipeline[2], _hudTwoSided = new Pipeline[2], _hudAlpha = new Pipeline[2];
    Pipeline _hudCompose, _hudComposeSrgb;

    void EnsureHudPipelines()
    {
        if (_hudCompose.Handle != 0) return;
        var vs = _shaders.Get("scene.vert", ShaderStage.Vertex);
        var fs = _shaders.Get("hud.frag", ShaderStage.Fragment);
        for (int i = 0; i < 2; i++)
        {
            var format = new[] { HudFormat(i == 1) };
            _hudOpaque[i] = PipelineBuilder.Create(_vk, new GraphicsPipelineDesc
            {
                Vertex = vs, Fragment = fs, ColorFormats = format, DepthFormat = RenderTargets.DepthFormat, Layout = Layout,
            });
            _hudTwoSided[i] = PipelineBuilder.Create(_vk, new GraphicsPipelineDesc
            {
                Vertex = vs, Fragment = fs, ColorFormats = format, DepthFormat = RenderTargets.DepthFormat, Layout = Layout, Cull = CullModeFlags.None,
            });
            // blended faces over what's there: colour by its alpha, coverage accumulating, so the image stays premultiplied
            _hudAlpha[i] = PipelineBuilder.Create(_vk, new GraphicsPipelineDesc
            {
                Vertex = vs, Fragment = fs, ColorFormats = format, DepthFormat = RenderTargets.DepthFormat, DepthWrite = false, Layout = Layout,
                Blend0 = new PipelineColorBlendAttachmentState(true, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
                    BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
                    ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit),
            });
        }
        _hudCompose = PipelineBuilder.Create(_vk, new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("hud_compose.vert", ShaderStage.Vertex), Fragment = _shaders.Get("hud_compose.frag", ShaderStage.Fragment),
            ColorFormats = new[] { RenderTargets.OutputFormat }, Blend = new[] { BlendMode.PremultipliedAlpha },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
        _hudComposeSrgb = PipelineBuilder.Create(_vk, new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("hud_compose.vert", ShaderStage.Vertex), Fragment = _shaders.Get("hud_compose.frag", ShaderStage.Fragment),
            ColorFormats = new[] { HudFormat(true) }, Blend = new[] { BlendMode.PremultipliedAlpha },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
    }

    /// <summary>Pictures (premultiplied sRGB bytes, shader-readable at <paramref name="slots"/>) over a HUD image left
    /// ColorAttachmentOptimal by <see cref="RecordHud"/>, each on the quad at its corners' address (HUD text: the viewer
    /// draws it over the HUDs, never hidden by them).</summary>
    public void RecordHudLabels(CommandBuffer cmd, GpuImage color, int[] slots, ulong[] corners)
    {
        EnsureHudPipelines();
        var set = _bindless.Set;
        _vk.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, Layout, 0, 1, in set, 0, null);
        bool linear = color.Format == HudFormat(true);
        BeginRendering(cmd, color.Width, color.Height, new[] { color }, null, clearColor: false, clearDepth: false);
        Bind(cmd, linear ? _hudComposeSrgb : _hudCompose);
        for (int i = 0; i < slots.Length; i++)
        {
            _push = default;
            _push.View = corners[i];
            _push.I0 = (uint)slots[i];
            _push.I1 = linear ? 2u : 0u;   // into linear light, or the sRGB bytes as they are
            Push(cmd);
            _vk.Api.CmdDraw(cmd, 6, 1, 0, 0);
        }
        _vk.Api.CmdEndRendering(cmd);
    }

    /// <summary>Record one HUD view of a draw list into its colour image (left ColorAttachmentOptimal) and depth.</summary>
    public void RecordHud(CommandBuffer cmd, FrameDraws draws, ViewData vd, GpuImage color, GpuImage depth)
    {
        EnsureHudPipelines();
        var set = _bindless.Set;
        _vk.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, Layout, 0, 1, in set, 0, null);
        color.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        depth.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
        BeginRendering(cmd, color.Width, color.Height, new[] { color }, depth, clearColor: true, clearDepth: true);
        _push = default;
        _push.View = vd.View;
        _push.Sky = vd.Sky;
        _push.Instances = draws.Instances;
        _push.Materials = draws.Materials;
        int f = color.Format == HudFormat(true) ? 1 : 0;
        _push.I2 = (uint)f;
        Bind(cmd, _hudOpaque[f]);
        DrawBatches(cmd, draws.Opaque);
        DrawBatches(cmd, draws.Fullbright);
        Bind(cmd, _hudTwoSided[f]);
        DrawBatches(cmd, draws.TwoSided);
        _push.I3 = 1;   // the blended pass
        Bind(cmd, _hudAlpha[f]);
        DrawBatches(cmd, draws.Alpha);
        _vk.Api.CmdEndRendering(cmd);
    }

    /// <summary>Lay a HUD image (shader-readable, in the bindless table at <paramref name="slot"/>) over an output image
    /// (TransferSrcOptimal before and after) on the quad at <paramref name="corners"/>: the device address of four clip
    /// positions (vec4: top left, top right, bottom right, bottom left).</summary>
    public void RecordHudCompose(CommandBuffer cmd, GpuImage target, int slot, bool linear, ulong corners)
    {
        EnsureHudPipelines();
        var set = _bindless.Set;
        _vk.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, Layout, 0, 1, in set, 0, null);
        target.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        BeginRendering(cmd, target.Width, target.Height, new[] { target }, null, clearColor: false, clearDepth: false);
        _push = default;
        _push.View = corners;
        _push.I0 = (uint)slot;
        _push.I1 = linear ? 1u : 0u;
        Bind(cmd, _hudCompose);
        Push(cmd);
        _vk.Api.CmdDraw(cmd, 6, 1, 0, 0);
        _vk.Api.CmdEndRendering(cmd);
        target.Transition(cmd, ImageLayout.TransferSrcOptimal);
    }

    void DisposeHud()
    {
        foreach (var p in _hudOpaque.Concat(_hudTwoSided).Concat(_hudAlpha).Append(_hudCompose).Append(_hudComposeSrgb))
            if (p.Handle != 0) _vk.Api.DestroyPipeline(_vk.Device, p, null);
    }
}
