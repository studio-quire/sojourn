using System.Numerics;
using Silk.NET.Vulkan;
using Sojourn.Render.Gpu;

namespace Sojourn.Render.Frame;

// The sun-light map at half size (RenderQuality.HalfSsao, off by default; the user's call to try, 2026-10-27): the sun
// shadow, SSAO and projector shadows lit at the top left pixel of each 2x2 block, the viewer's two blur passes at that
// size with their taps spaced in the eye's pixels (so they cover what they do at full size), then brought back to the
// eye's size depth-aware (lightup.frag), where sun and ambient, local lights and projectors read it as before. A
// quarter of the pixels for the three passes that cost most of the light chain (sunlight ~0.5 and ssao blur ~0.55 ms an
// eye at Link's size), for a softer light map: shadow edges and occlusion keep their blur's spread, details under two
// pixels go.
// Written by Claude Opus 5.5, 2026-10-27.
public sealed unsafe partial class Renderer
{
    /// <summary>This frame's light map at half size (set each frame from RenderQuality.HalfSsao; SOJOURN_AB=halflight).</summary>
    public bool HalfLight;
    static bool? _abHalfLight = System.Environment.GetEnvironmentVariable("SOJOURN_HALF_LIGHT") switch { "1" => true, "0" => false, _ => null };   // tests: over the setting

    Pipeline _sunlightHalf, _blurlightHalf, _lightUp;

    void EnsureHalfLightPipelines()
    {
        if (_lightUp.Handle != 0) return;
        var fsVs = _shaders.Get("fullscreen.vert", ShaderStage.Vertex);
        GraphicsPipelineDesc Pass(string frag, params string[] defines) => new()
        {
            Vertex = fsVs, Fragment = _shaders.Get(frag, ShaderStage.Fragment, defines), ColorFormats = new[] { RenderTargets.LightMapFormat },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        };
        _sunlightHalf = Make(Pass("sunlight.frag", "HALF_LIGHT"));
        _blurlightHalf = Make(Pass("blurlight.frag", "HALF_LIGHT"));
        _lightUp = Make(Pass("lightup.frag"));
    }

    /// <summary>The light map, lit and blurred at half size, then brought up into rt.LightMap (left shader-readable).</summary>
    void RecordHalfLight(CommandBuffer cmd, ViewSetup v, RenderTargets rt)
    {
        var api = _vk.Api;
        EnsureHalfLightPipelines();
        rt.EnsureLightHalf();
        var half = rt.LightHalf!;
        var temp = rt.LightHalfTemp!;
        half.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        _push.I3 = (uint)rt.DepthSlot;
        BeginRendering(cmd, half.Width, half.Height, new[] { half }, null, clearColor: false, clearDepth: false);
        Bind(cmd, _sunlightHalf);
        Push(cmd);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
        Profiler.Mark("sunlight");
        if (v.Sky.ssao_on != 0)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var src = pass == 0 ? half : temp;
                var dst = pass == 0 ? temp : half;
                src.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
                dst.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
                _push.I3 = (uint)rt.DepthSlot | ((uint)(pass == 0 ? rt.LightHalfSlot : rt.LightHalfTempSlot) << 16);
                _push.V0 = pass == 0 ? new Vector4(1, 0, 0, 0) : new Vector4(0, 1, 0, 0);
                BeginRendering(cmd, half.Width, half.Height, new[] { dst }, null, false, false);
                Bind(cmd, _blurlightHalf);
                Push(cmd);
                api.CmdDraw(cmd, 3, 1, 0, 0);
                api.CmdEndRendering(cmd);
            }
        }
        _push.V0 = Vector4.Zero;
        Profiler.Mark("ssao blur");
        half.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        rt.LightMap.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        _push.I3 = (uint)rt.DepthSlot | ((uint)rt.LightHalfSlot << 16);
        BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.LightMap }, null, false, false);
        Bind(cmd, _lightUp);
        Push(cmd);
        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
        rt.LightMap.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        Profiler.Mark("light up");
    }

    void DisposeHalfLight()
    {
        foreach (var p in new[] { _sunlightHalf, _blurlightHalf, _lightUp })
            if (p.Handle != 0) _vk.Api.DestroyPipeline(_vk.Device, p, null);
    }
}
