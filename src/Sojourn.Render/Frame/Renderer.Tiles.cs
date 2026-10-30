using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

// Light tiles: the local-light and projector passes loop over every light in view for every pixel (the viewer draws
// a volume per light instead). Busy places hold 100-160 lights and 40-60 projectors in view, which made those two
// passes the costliest of the frame (Envision, 2026-10-27: 5 ms an eye). After the G-buffer, tilecull.comp lists for
// each 16x16-pixel tile of an eye the lights whose spheres reach its pixels; the passes loop over their tile's list
// (Shaders/tiles.glsl). The spheres are the ones the passes already test per pixel, so the picture is the same.
// Only the main eyes have tiles; probe faces and mirrors loop over everything as before.
// Written by Claude Opus 5.5, 2026-10-27; docs/rendering.md > Light tiles.
public sealed unsafe partial class Renderer
{
    /// <summary>Tiles.glsl's TILE_SIZE and TILE_STRIDE (uints a tile record).</summary>
    public const uint TileSize = 16, TileStride = 256;

    /// <summary>SOJOURN_LIGHT_TILES=0 loops over every light for every pixel again (for comparing).</summary>
    public static bool LightTiles = System.Environment.GetEnvironmentVariable("SOJOURN_LIGHT_TILES") != "0";

    Pipeline _tileCull;
    // the eye being recorded's targets, when its light passes read tiles (set around the main eyes' RecordScene)
    RenderTargets? _tiles;

    void CreateTiles() => _tileCull = PipelineBuilder.CreateCompute(_vk, _shaders.Get("tilecull.comp", ShaderStage.Compute), Layout);

    /// <summary>The tile list for this eye's light passes, after the G-buffer (its depth readable).</summary>
    void RecordTileCull(CommandBuffer cmd, FrameDraws draws, ViewData vd, RenderTargets rt)
    {
        if (!LightTiles || _tiles != rt || rt.LightTiles is not { } buf || (draws.LightCount == 0 && draws.ProjectorCount == 0)) return;
        var api = _vk.Api;
        GpuImage.MemoryBarrier(_vk, cmd);   // the last eye's (or frame's) light passes are done reading the records
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _tileCull);
        var set = _bindless.Set;
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, Layout, 0, 1, in set, 0, null);
        var pb = new PushBlock
        {
            View = vd.View, Lights = draws.Lights, Instances = draws.Projectors, Materials = buf.Address,
            I0 = draws.LightCount, I1 = draws.ProjectorCount, I2 = (uint)rt.DepthSlot | ((uint)rt.NormalSlot << 16), I3 = rt.TileCols,
        };
        api.CmdPushConstants(cmd, Layout, ShaderStageFlags.All, 0, (uint)sizeof(PushBlock), &pb);
        api.CmdDispatch(cmd, rt.TileCols, rt.TileRows, 1);
        GpuImage.MemoryBarrier(_vk, cmd);
        Profiler.Mark("light tiles");
    }

    void DisposeTiles() => _vk.Api.DestroyPipeline(_vk.Device, _tileCull, null);
}
