using System.Numerics;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

/// <summary>
/// Avatar impostors (World/Avatars.Impostors.cs). A capture renders one avatar's faces once, with the G-buffer's own
/// pipelines, into its tile of the atlas: an orthographic view along the line from the camera, whose view matrix is a
/// translation only, so the normals it writes are in world space. In the scene, each impostor is a box (its capture
/// volume) drawn back faces only; its fragment shader walks the eye's ray through the tile's depth to the surface and
/// writes that texel's G-buffer values and depth (impostor.frag). Each eye finds the surface along its own ray, so the
/// two eyes see the depth the avatar had, not a flat card, and the deferred passes light it like everything else.
/// </summary>
public sealed unsafe partial class Renderer
{
    ImpostorAtlas? _atlas;
    Pipeline _impostor;

    /// <summary>The impostor atlas, made on first use.</summary>
    public ImpostorAtlas Atlas => _atlas ??= new ImpostorAtlas(_vk, _bindless);
    public bool HasAtlas => _atlas != null;

    void EnsureImpostorPipeline()
    {
        if (_impostor.Handle != 0) return;
        _impostor = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("impostor.vert", ShaderStage.Vertex), Fragment = _shaders.Get("impostor.frag", ShaderStage.Fragment),
            ColorFormats = new[] { RenderTargets.DiffuseFormat, RenderTargets.SpecularFormat, RenderTargets.NormalFormat, RenderTargets.EmissiveFormat },
            DepthFormat = RenderTargets.DepthFormat, Cull = CullModeFlags.FrontBit, VertexInput = false, Layout = Layout,
        });
    }

    internal readonly record struct CaptureDraws(ImpostorTile Tile, ulong View, List<IndirectBatch> Opaque, List<IndirectBatch> TwoSided);

    static ulong CaptureArenaBytes(DrawList dl) =>
        (ulong)(dl.Captures.Count * (sizeof(GpuView) + 256) + dl.Captures.Sum(c => c.Opaque.Count + c.TwoSided.Count) * sizeof(DrawIndexedIndirect));

    List<CaptureDraws> UploadCaptures(FrameArena arena, DrawList dl)
    {
        var list = new List<CaptureDraws>(dl.Captures.Count);
        ulong baseAddr = arena.Device.Address;
        foreach (var c in dl.Captures)
        {
            var viewProj = c.View * c.Proj;
            Matrix4x4.Invert(c.View, out var invView);
            Matrix4x4.Invert(c.Proj, out var invProj);
            Matrix4x4.Invert(viewProj, out var invViewProj);
            var gv = new GpuView
            {
                View = c.View, Proj = c.Proj, ViewProj = viewProj, InvView = invView, InvProj = invProj, InvViewProj = invViewProj,
                CameraPos = new Vector4(c.Center, 0.1f),
                Viewport = new Vector4(c.Tile.W, c.Tile.H, 1f / c.Tile.W, 1f / c.Tile.H),
            };
            list.Add(new CaptureDraws(c.Tile, baseAddr + arena.Write(gv), BuildBatches(arena, c.Opaque, sort: false), BuildBatches(arena, c.TwoSided, sort: false)));
        }
        return list;
    }

    /// <summary>This frame's impostor captures into their atlas tiles (after skinning, before any view).</summary>
    void RecordCaptures(CommandBuffer cmd, FrameDraws draws, ulong sky)
    {
        if (draws.Captures.Count == 0 || _atlas is not { } a) return;
        var api = _vk.Api;
        var targets = new[] { a.Diffuse, a.Specular, a.Normal, a.Emissive };
        foreach (var t in targets) t.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        a.Depth.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
        BeginRendering(cmd, ImpostorAtlas.Size, ImpostorAtlas.Size, targets, a.Depth, clearColor: false, clearDepth: false);
        _push = default;
        _push.Sky = sky;
        _push.Instances = draws.Instances;
        _push.Materials = draws.Materials;
        _push.Lights = draws.Lights;
        var clears = stackalloc ClearAttachment[5];
        for (uint i = 0; i < 4; i++) clears[i] = new ClearAttachment(ImageAspectFlags.ColorBit, i, new ClearValue(new ClearColorValue(0f, 0f, 0f, 0f)));
        clears[4] = new ClearAttachment(ImageAspectFlags.DepthBit, 0, new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)));
        foreach (var c in draws.Captures)
        {
            var rect = new Rect2D(new Offset2D(c.Tile.X, c.Tile.Y), new Extent2D((uint)c.Tile.W, (uint)c.Tile.H));
            var vp = new Viewport(c.Tile.X, c.Tile.Y, c.Tile.W, c.Tile.H, 0, 1);
            api.CmdSetViewport(cmd, 0, 1, in vp);
            api.CmdSetScissor(cmd, 0, 1, in rect);
            var cr = new ClearRect(rect, 0, 1);
            api.CmdClearAttachments(cmd, 5, clears, 1, in cr);
            _push.View = c.View;
            Bind(cmd, _gbuffer);
            DrawBatches(cmd, c.Opaque);
            Bind(cmd, _gbufferTwoSided);
            DrawBatches(cmd, c.TwoSided);
        }
        api.CmdEndRendering(cmd);
        foreach (var t in targets) t.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        a.Depth.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        Profiler.Mark("impostor captures");
    }

    /// <summary>The impostors into the G-buffer being drawn (push constants already the view's).</summary>
    void DrawImpostors(CommandBuffer cmd, FrameDraws draws)
    {
        if (draws.Impostors.Count == 0 || _atlas == null) return;
        EnsureImpostorPipeline();
        Bind(cmd, _impostor);
        Push(cmd);
        foreach (uint inst in draws.Impostors) _vk.Api.CmdDraw(cmd, 36, 1, 0, inst);
    }

    void DisposeImpostors()
    {
        if (_impostor.Handle != 0) _vk.Api.DestroyPipeline(_vk.Device, _impostor, null);
        _atlas?.Dispose();
    }
}
