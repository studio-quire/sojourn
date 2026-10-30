using LibreMetaverse;

namespace Sojourn.Render.TerrainGen;

/// <summary>
/// One region's terrain as the viewer receives it: the decoded height samples plus the RegionInfo
/// composition parameters. Plain data so the builder runs without a live grid; see <see cref="FromHeights"/>.
/// </summary>
public sealed class TerrainInput
{
    /// <summary>Samples per region edge (256 in SL; OpenSim varregions use larger multiples of 256).</summary>
    public required int Width { get; init; }

    /// <summary>Width*Width heights, row-major (index = y*Width + x), x east, y north, metres.</summary>
    public required float[] Heights { get; init; }

    /// <summary>Region origin in global metres (from the region handle); feeds the composition noise.</summary>
    public required double OriginGlobalX { get; init; }
    public required double OriginGlobalY { get; init; }

    /// <summary>TerrainStartHeight00, 01, 10, 11 (RegionInfo order; the viewer's corner indices 0-3).</summary>
    public required float[] StartHeights { get; init; }

    /// <summary>TerrainHeightRange00, 01, 10, 11.</summary>
    public required float[] HeightRanges { get; init; }

    /// <summary>TerrainDetail0-3: texture or GLTF material asset IDs (the viewer tries both).</summary>
    public Guid[] DetailAssets { get; init; } = (Guid[])TerrainShading.DefaultDetailTextures.Clone();

    public float WaterHeight { get; init; } = 20f;

    /// <summary>
    /// Neighbouring regions the viewer has connected, indexed by direction (TerrainDirection: E, N, W, S,
    /// NE, NW, SW, SE); null where there is none. They supply the heights and composition the viewer
    /// stitches across region edges. Must have the same Width.
    /// </summary>
    public TerrainInput?[] Neighbors { get; init; } = new TerrainInput?[8];

    /// <summary>The viewer's region-handle decode (from_region_handle): x in the high 32 bits, y in the low.</summary>
    public static (double X, double Y) OriginFromHandle(ulong handle) => ((uint)(handle >> 32), (uint)(handle & 0xFFFFFFFF));

    /// <summary>
    /// Builds an input from a LibreMetaverse Simulator (Settings.World.StoreLandPatches must be on).
    /// Missing patches read as height 0, as in the viewer before their LayerData arrives.
    /// LibreMetaverse 3.2.0 files patches at Terrain[y*16 + x] whatever the region size, so this places
    /// each patch by its own X/Y; on varregions colliding slots lose patches, so feed those from the
    /// LandPatchReceived event via <see cref="HeightsFromPatches"/> instead.
    /// </summary>
    /// <summary>Heights from the region source, everything else from the region's Simulator.</summary>
    public static TerrainInput FromHeights(Sojourn.Render.World.TerrainHeights heights, Simulator sim)
    {
        var (ox, oy) = OriginFromHandle(sim.Handle);
        return new TerrainInput
        {
            Width = heights.Width,
            Heights = heights.Data,
            OriginGlobalX = ox,
            OriginGlobalY = oy,
            StartHeights = [sim.TerrainStartHeight00, sim.TerrainStartHeight01, sim.TerrainStartHeight10, sim.TerrainStartHeight11],
            HeightRanges = [sim.TerrainHeightRange00, sim.TerrainHeightRange01, sim.TerrainHeightRange10, sim.TerrainHeightRange11],
            DetailAssets = [sim.TerrainDetail0.Guid, sim.TerrainDetail1.Guid, sim.TerrainDetail2.Guid, sim.TerrainDetail3.Guid],
            WaterHeight = sim.WaterHeight,
        };
    }

    /// <summary>Assembles 16x16 patches (Data[y*16 + x], patch coordinates in patch units) into a height grid.</summary>
    public static float[] HeightsFromPatches(int width, IEnumerable<(int X, int Y, float[] Data)> patches)
    {
        var heights = new float[width * width];
        foreach (var (px, py, data) in patches)
        {
            if (px < 0 || py < 0 || (px + 1) * 16 > width || (py + 1) * 16 > width)
                continue;
            for (int y = 0; y < 16; y++)
                Array.Copy(data, y * 16, heights, (py * 16 + y) * width + px * 16, 16);
        }
        return heights;
    }
}

/// <summary>The viewer's direction indices (lldefs.h).</summary>
public static class TerrainDirection
{
    public const int East = 0, North = 1, West = 2, South = 3;
    public const int NorthEast = 4, NorthWest = 5, SouthWest = 6, SouthEast = 7, Middle = 8;

    // gDirOpposite, gDirAxes (indra_constants.cpp)
    public static ReadOnlySpan<int> Opposite => [2, 3, 0, 1, 6, 7, 4, 5];
    public static ReadOnlySpan<int> AxisX => [1, 0, -1, 0, 1, -1, -1, 1];
    public static ReadOnlySpan<int> AxisY => [0, 1, 0, -1, 1, 1, -1, -1];
}
