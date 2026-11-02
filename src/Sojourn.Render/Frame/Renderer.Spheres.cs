using System.Numerics;
using System.Runtime.InteropServices;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

// RLV's vision spheres (@setsphere, docs/rlv.md): each sphere a pass over an eye's tonemapped image, before glow, reading
// that eye's depth (Shaders/rlv_sphere.frag), as Firestorm's RLVa runs its RlvSphereEffect in the post chain. The blurs
// take two passes (across, then down). Claude Opus 5.5, 2026-11-01.
public sealed unsafe partial class Renderer
{
    /// <summary>One vision sphere for a frame: its origin (region space), mode (0 blend, 1 blur, 2 variable blur, 3 chromatic
    /// aberration, 4 pixelate), which side extends (1 beyond max, 2 below min), the values at the two distances, and the
    /// effect's params (blend: the colour).</summary>
    public readonly record struct SphereFx(Vector3 Origin, int Mode, int Extend, float ValueMin, float DistMin, float ValueMax, float DistMax, Vector4 Params);

    /// <summary>The spheres to draw in the next frames (null or empty: none); set by the client each frame.</summary>
    public IReadOnlyList<SphereFx>? Spheres { get; set; }

    Pipeline _rlvSphere;

    void EnsureSpheres()
    {
        if (_rlvSphere.Handle != 0) return;
        _rlvSphere = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("fullscreen.vert", ShaderStage.Vertex), Fragment = _shaders.Get("rlv_sphere.frag", ShaderStage.Fragment),
            ColorFormats = new[] { RenderTargets.HdrFormat }, DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None,
            VertexInput = false, Layout = Layout,
        });
    }

    void DisposeSpheres()
    {
        if (_rlvSphere.Handle != 0) _vk.Api.DestroyPipeline(_vk.Device, _rlvSphere, null);
    }

    /// <summary>The spheres over the tonemapped image in <paramref name="ldrSlot"/> (Hdr's or HdrCopy's), ping-ponging
    /// between the two; returns the slot that holds the result.</summary>
    int RecordSpheres(CommandBuffer cmd, RenderTargets rt, int ldrSlot)
    {
        var spheres = Spheres;
        if (spheres == null || spheres.Count == 0) return ldrSlot;
        EnsureSpheres();
        var api = _vk.Api;
        rt.Depth.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        var saved = (_push.I0, _push.I2, _push.I3, _push.V0, _push.V1);
        foreach (var s in spheres)
        {
            int passes = s.Mode is 1 or 2 ? 2 : 1;
            for (int pass = 0; pass < passes; pass++)
            {
                bool intoHdr = ldrSlot == rt.HdrCopySlot;
                var dst = intoHdr ? rt.Hdr : rt.HdrCopy;
                (intoHdr ? rt.HdrCopy : rt.Hdr).Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
                dst.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
                _push.I1 = (uint)ldrSlot;
                _push.I3 = (uint)rt.DepthSlot | (uint)(s.Mode & 0xF) << 16 | (uint)(s.Extend & 0xF) << 20 | (uint)pass << 24;
                _push.I0 = Half2(s.Params.X, s.Params.Y);
                _push.I2 = Half2(s.Params.Z, s.Params.W);
                _push.V0 = new Vector4(s.Origin, 1);
                _push.V1 = new Vector4(s.ValueMin, s.DistMin, s.ValueMax, s.DistMax);
                BeginRendering(cmd, rt.Width, rt.Height, new[] { dst }, null, false, false);
                Bind(cmd, _rlvSphere);
                Push(cmd);
                api.CmdDraw(cmd, 3, 1, 0, 0);
                api.CmdEndRendering(cmd);
                dst.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
                ldrSlot = intoHdr ? rt.HdrSlot : rt.HdrCopySlot;
            }
        }
        (_push.I0, _push.I2, _push.I3, _push.V0, _push.V1) = saved;
        Profiler.Mark("rlv spheres");
        return ldrSlot;
    }

    static uint Half2(float a, float b) =>
        BitConverter.HalfToUInt16Bits((Half)a) | (uint)BitConverter.HalfToUInt16Bits((Half)b) << 16;
}
