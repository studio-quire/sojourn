using System.Numerics;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

// Water, as LLPipeline::renderGeomPostDeferred orders it. With the camera above water: the water exclusion mask, the
// water's haze over what lies below the surface, the pre-water alpha pool, then the surface (water.frag); the
// atmospherics come after (RecordScene's HazePass). With the camera below it (sUnderWaterRender): the mask, the water's
// haze over the whole screen, the pre-water alpha pool, the atmospherics (moved before the water there), then the
// surface seen from below (water_under.frag, the viewer's underWaterF.glsl).
// The water exclusion mask (LLPipeline::doWaterExclusionMask, LLDrawPoolWaterExclusion): an R8 target with a depth of
// its own, the water planes giving it their depth and the exclusion faces in front of them marking it, read by the
// three water shaders (water_mask.glsl). Made only when something in view excludes water.
// Claude Opus 5.5, 2026-10-28 (the under-water order and surface, and the mask; the rest moved here from Renderer.cs).
public sealed unsafe partial class Renderer
{
    Pipeline _waterMaskPlane, _waterMaskFace, _waterUnder, _waterHazeUnder;

    void CreateWater()
    {
        var mask = _shaders.Get("water_exclusion.frag", ShaderStage.Fragment);
        _waterMaskPlane = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("water.vert", ShaderStage.Vertex), Fragment = mask,
            ColorFormats = new[] { RenderTargets.WaterMaskFormat }, DepthFormat = RenderTargets.DepthFormat, DepthWrite = true,
            Cull = CullModeFlags.None, Layout = Layout,   // double-sided, as the viewer draws them for the mask
        });
        _waterMaskFace = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("scene.vert", ShaderStage.Vertex), Fragment = mask,
            ColorFormats = new[] { RenderTargets.WaterMaskFormat }, DepthFormat = RenderTargets.DepthFormat, DepthWrite = true,
            Layout = Layout,
        });
        _waterUnder = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("water.vert", ShaderStage.Vertex), Fragment = _shaders.Get("water_under.frag", ShaderStage.Fragment),
            ColorFormats = new[] { RenderTargets.HdrFormat }, DepthFormat = RenderTargets.DepthFormat, DepthWrite = true,
            Cull = CullModeFlags.None, Layout = Layout,
        });
        _waterHazeUnder = Make(new GraphicsPipelineDesc
        {
            Vertex = _shaders.Get("fullscreen.vert", ShaderStage.Vertex), Fragment = _shaders.Get("waterhaze.frag", ShaderStage.Fragment, "UNDER_WATER"),
            ColorFormats = new[] { RenderTargets.HdrFormat }, Blend = new[] { BlendMode.Haze },
            DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None, VertexInput = false, Layout = Layout,
        });
    }

    void DisposeWater()
    {
        foreach (var p in new[] { _waterMaskPlane, _waterMaskFace, _waterUnder, _waterHazeUnder })
            _vk.Api.DestroyPipeline(_vk.Device, p, null);
    }

    /// <summary>Water: refraction reads a copy of the scene and of its depth (the viewer's mWaterDis). One view. Returns
    /// whether it drew the atmospherics too (the camera under water: they go before the surface there).</summary>
    bool WaterPass(CommandBuffer cmd, FrameDraws draws, ViewSetup v, RenderTargets rt)
    {
        var api = _vk.Api;
        var water = draws.Water;
        bool under = v.Sky.camera_under_water != 0, hazed = false;
        if (water.Count > 0)
        {
            uint mask = WaterMask(cmd, draws, rt);
            rt.Depth.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
            CopyImage(cmd, rt.Depth, rt.DepthCopy, ImageLayout.DepthAttachmentOptimal);
            _push.I3 = (uint)rt.DepthCopySlot | (mask << 16);
            if (!under)
            {
                // doWaterHaze (camera above water): fog what lies below the surface, drawn with the water plane
                BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.Hdr }, rt.Depth, clearColor: false, clearDepth: false);
                Bind(cmd, _waterHaze);
                DrawBatches(cmd, water);
                api.CmdEndRendering(cmd);
            }
            else
            {
                // under water: over the whole screen
                BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.Hdr }, null, clearColor: false, clearDepth: false);
                Bind(cmd, _waterHazeUnder);
                Push(cmd);
                api.CmdDraw(cmd, 3, 1, 0, 0);
                api.CmdEndRendering(cmd);
            }
            Profiler.Mark("water haze");   // (with the mask and the depth copy)
            // the pre-water alpha pool, after the haze and before the water (LLPipeline::renderGeomPostDeferred's pool
            // order): faces on the far side of the water, clipped to it, writing depth so the water's refraction and fog
            // see them
            if (!_mirrorPass && draws.AlphaPreWater is { Count: > 0 } pre)
            {
                BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.Hdr }, rt.Depth, clearColor: false, clearDepth: false);
                _push.V1 = new Vector4(0, 0, draws.CameraUnderWater ? 1 : -1, draws.WaterHeight);
                DrawAlphaBatches(cmd, pre, preWater: true);   // by material path, as the post-water pool
                _push.V1 = Vector4.Zero;
                api.CmdEndRendering(cmd);
                Profiler.Mark("pre-water alpha");
            }
            if (under)
            {
                // atmospherics_pass = POOL_WATER when under water: the haze before the surface, so its refraction has it
                var keep = _push;
                HazePass(cmd, rt);
                _push = keep;
                hazed = true;
                rt.Depth.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
            }
            // LLDrawPoolWater::beginPostDeferredPass: the scene so far (fogged) and its depth for refraction
            CopyImage(cmd, rt.Hdr, rt.SceneCopy, ImageLayout.ColorAttachmentOptimal);
            CopyImage(cmd, rt.Depth, rt.DepthCopy, ImageLayout.DepthAttachmentOptimal);
            BeginRendering(cmd, rt.Width, rt.Height, new[] { rt.Hdr }, rt.Depth, clearColor: false, clearDepth: false);
            Bind(cmd, under ? _waterUnder : _water);
            _push.I1 = (uint)(v.WaterNormalSlotA < 0 ? 0xFFFF : v.WaterNormalSlotA) | ((uint)(v.WaterNormalSlotB < 0 ? 0xFFFF : v.WaterNormalSlotB) << 16);
            _push.I2 = (uint)rt.SceneCopySlot | ((uint)rt.DepthCopySlot << 16);
            _push.I3 = mask;
            DrawBatches(cmd, water);
            api.CmdEndRendering(cmd);
        }
        Profiler.Mark("water");
        return hazed;
    }

    /// <summary>The water exclusion mask for this view: its slot, or 0xFFFF when nothing in view excludes water.</summary>
    uint WaterMask(CommandBuffer cmd, FrameDraws draws, RenderTargets rt)
    {
        if (draws.WaterExclusion.Count == 0) return 0xFFFF;
        rt.EnsureWaterMask();
        var mask = rt.WaterMask!;
        mask.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        rt.WaterMaskDepth!.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
        BeginRendering(cmd, rt.Width, rt.Height, new[] { mask }, rt.WaterMaskDepth, clearColor: true, clearDepth: true);
        var keep = _push.V0;
        _push.V0 = Vector4.Zero;
        Bind(cmd, _waterMaskPlane);
        DrawBatches(cmd, draws.Water);
        _push.V0 = new Vector4(1, 0, 0, 0);
        Bind(cmd, _waterMaskFace);
        DrawBatches(cmd, draws.WaterExclusion);
        _push.V0 = keep;
        _vk.Api.CmdEndRendering(cmd);
        mask.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        return (uint)rt.WaterMaskSlot;
    }
}
