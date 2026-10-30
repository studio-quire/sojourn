using System.Runtime.InteropServices;
using Sojourn.Render.Gpu;
using Sojourn.Render.World;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

// Beacons and highlights (World/Beacons.cs) over an eye's finished image, from the frame's present step beside the
// parcel lines: highlighted faces first (the viewer draws them in the scene, before its UI), each tint's colour times
// the smoke image (or flat), alpha-blended and tested against the eye's depth with no writes, back faces culled; then
// the beacons' lines, the long ones over everything and the short ones and the tracking beacon tested.
// Claude Opus 5.5, 2026-10-29.
public sealed unsafe partial class Renderer
{
    Pipeline _beaconOver, _beaconTested, _highlight;
    ulong _beaconVertices, _beaconInstances;
    int _beaconOverCount, _beaconLineCount, _highlightSmoke = -1;
    readonly List<IndirectBatch>?[] _highlights = new List<IndirectBatch>?[4];
    readonly List<DrawCmd> _highlightScratch = new();

    // FaceTint's colours: a beacon's highlight (renderHighlights' red at half), blended, masked, invisible (renderDebugAlpha)
    static readonly Vector4[] TintColors = { new(1, 0, 0, 0.5f), new(1, 0, 0, 1), new(0, 0, 1, 1), new(0, 1, 0, 1) };

    void CreateBeacons()
    {
        GraphicsPipelineDesc Lines(bool tested) => new()
        {
            Vertex = _shaders.Get("parcel_lines.vert", ShaderStage.Vertex), Fragment = _shaders.Get("parcel_lines.frag", ShaderStage.Fragment),
            ColorFormats = new[] { RenderTargets.OutputFormat }, Blend = new[] { BlendMode.Alpha },
            WriteMasks = new[] { ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit },
            DepthFormat = RenderTargets.DepthFormat, DepthTest = tested, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        };
        _beaconOver = Make(Lines(false));
        _beaconTested = Make(Lines(true));
        _highlight = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("scene.vert", ShaderStage.Vertex), Fragment = _shaders.Get("highlight.frag", ShaderStage.Fragment),
            ColorFormats = new[] { RenderTargets.OutputFormat }, Blend = new[] { BlendMode.Alpha },
            WriteMasks = new[] { ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit },
            DepthFormat = RenderTargets.DepthFormat, DepthWrite = false, Cull = CullModeFlags.BackBit, Layout = Layout,
        });
    }

    void DisposeBeacons()
    {
        foreach (var p in new[] { _beaconOver, _beaconTested, _highlight }) _vk.Api.DestroyPipeline(_vk.Device, p, null);
    }

    static ulong BeaconArenaBytes(DrawList dl) =>
        (ulong)(dl.Beacons.Lines.Count * sizeof(ParcelLineVertex) + dl.Beacons.FaceCount * sizeof(DrawIndexedIndirect) + 5 * 256);

    /// <summary>The main list's beacons into the frame arena, for each eye's present step (after the list's own upload:
    /// the highlighted faces' instances are its).</summary>
    void UploadBeacons(FrameArena arena, DrawList dl, FrameDraws draws)
    {
        var b = dl.Beacons;
        _beaconLineCount = b.Lines.Count;
        _beaconOverCount = b.OverVertices;
        if (_beaconLineCount > 0) _beaconVertices = arena.Device.Address + arena.Write<ParcelLineVertex>(CollectionsMarshal.AsSpan(b.Lines));
        _beaconInstances = draws.Instances;
        _highlightSmoke = b.Smoke;
        for (int i = 0; i < _highlights.Length; i++)
        {
            _highlights[i] = null;
            if (b.Faces[i].Count == 0) continue;
            // (grouped in a copy: the frame's own lists keep their order)
            _highlightScratch.Clear();
            _highlightScratch.AddRange(b.Faces[i]);
            _highlights[i] = BuildBatches(arena, _highlightScratch, sort: false);
        }
    }

    /// <summary>This frame's highlights and beacons over an eye's image (in TransferSrcOptimal, left so), tested against
    /// its depth; the eye's view is the one its post just pushed.</summary>
    public void RecordBeacons(CommandBuffer cmd, GpuImage target, GpuImage depth)
    {
        bool faces = _highlights.Any(h => h is { Count: > 0 });
        if (_beaconLineCount == 0 && !faces) return;
        var api = _vk.Api;
        target.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        depth.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
        var set = _bindless.Set;
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, Layout, 0, 1, in set, 0, null);
        BeginRendering(cmd, target.Width, target.Height, new[] { target }, depth, clearColor: false, clearDepth: false);
        var keep = _push;
        if (faces)
        {
            Bind(cmd, _highlight);
            _push.Instances = _beaconInstances;
            for (int i = 0; i < _highlights.Length; i++)
            {
                if (_highlights[i] is not { Count: > 0 } batches) continue;
                bool smoke = (FaceTint)i != FaceTint.Beacon;
                _push.V0 = TintColors[i];
                _push.I0 = smoke && _highlightSmoke >= 0 ? (uint)_highlightSmoke : uint.MaxValue;
                // the smoke image not here yet: the tint at half rather than a solid sheet
                if (smoke && _highlightSmoke < 0) _push.V0.W = 0.4f;
                DrawBatches(cmd, batches);
            }
        }
        if (_beaconLineCount > 0)
        {
            _push.Instances = _beaconVertices;
            Push(cmd);
            if (_beaconOverCount > 0)
            {
                Bind(cmd, _beaconOver);
                api.CmdDraw(cmd, (uint)_beaconOverCount, 1, 0, 0);
            }
            if (_beaconLineCount > _beaconOverCount)
            {
                Bind(cmd, _beaconTested);
                api.CmdDraw(cmd, (uint)(_beaconLineCount - _beaconOverCount), 1, (uint)_beaconOverCount, 0);
            }
        }
        _push = keep;
        api.CmdEndRendering(cmd);
        target.Transition(cmd, ImageLayout.TransferSrcOptimal);
    }
}
