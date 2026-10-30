using System.Runtime.InteropServices;
using Sojourn.Render.Gpu;
using Sojourn.Render.World;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

// Property and ban lines (World/ParcelLines.cs) over an eye's finished image, as the viewer's render_hud_elements draws
// them after renderFinalize: colours as given (the UI's), alpha-blended, depth-tested against the eye's scene; the
// property lines write depth (LLGLDepthTest(GL_TRUE)), the ban walls don't. Recorded from the frame's present step,
// after pictures and videos take the image (the viewer's snapshots leave the interface out by default) and before
// HUDs and menus. Claude Opus 5.5, 2026-10-28.
public sealed unsafe partial class Renderer
{
    Pipeline _parcelLines, _banWalls;
    ulong _parcelLineVertices;
    int _parcelLineCount, _propertyLineCount;

    void CreateParcelLines()
    {
        GraphicsPipelineDesc Desc(bool depthWrite) => new()
        {
            Vertex = _shaders.Get("parcel_lines.vert", ShaderStage.Vertex), Fragment = _shaders.Get("parcel_lines.frag", ShaderStage.Fragment),
            ColorFormats = new[] { RenderTargets.OutputFormat }, Blend = new[] { BlendMode.Alpha },
            WriteMasks = new[] { ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit },
            DepthFormat = RenderTargets.DepthFormat, DepthWrite = depthWrite, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        };
        _parcelLines = Make(Desc(true));
        _banWalls = Make(Desc(false));
    }

    void DisposeParcelLines()
    {
        _vk.Api.DestroyPipeline(_vk.Device, _parcelLines, null);
        _vk.Api.DestroyPipeline(_vk.Device, _banWalls, null);
    }

    /// <summary>The main list's lines into the frame arena, for each eye's present step.</summary>
    void UploadParcelLines(FrameArena arena, DrawList dl)
    {
        _parcelLineCount = dl.ParcelLines.Count;
        if (_parcelLineCount == 0) return;
        _parcelLineVertices = arena.Device.Address + arena.Write<ParcelLineVertex>(CollectionsMarshal.AsSpan(dl.ParcelLines));
        _propertyLineCount = dl.PropertyLineVertices;   // property lines first, then ban walls
    }

    /// <summary>This frame's lines over an eye's image (in TransferSrcOptimal, left so), tested against its depth; the
    /// eye's view is the one its post just pushed.</summary>
    public void RecordParcelLines(CommandBuffer cmd, GpuImage target, GpuImage depth)
    {
        if (_parcelLineCount == 0) return;
        var api = _vk.Api;
        target.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        depth.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
        var set = _bindless.Set;
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, Layout, 0, 1, in set, 0, null);
        BeginRendering(cmd, target.Width, target.Height, new[] { target }, depth, clearColor: false, clearDepth: false);
        var keep = _push.Instances;
        _push.Instances = _parcelLineVertices;
        Push(cmd);
        if (_propertyLineCount > 0)
        {
            Bind(cmd, _parcelLines);
            api.CmdDraw(cmd, (uint)_propertyLineCount, 1, 0, 0);
        }
        if (_parcelLineCount > _propertyLineCount)
        {
            Bind(cmd, _banWalls);
            api.CmdDraw(cmd, (uint)(_parcelLineCount - _propertyLineCount), 1, (uint)_propertyLineCount, 0);
        }
        _push.Instances = keep;
        api.CmdEndRendering(cmd);
        target.Transition(cmd, ImageLayout.TransferSrcOptimal);
    }
}
