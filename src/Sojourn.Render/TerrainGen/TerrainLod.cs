// Ported from the Second Life viewer: indra/newview/llpatchvertexarray.cpp (render level tables),
// llsurfacepatch.cpp (updateVisibility, updateCameraDistanceRegion), llappviewer.cpp (sLODFactor).
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
namespace Sojourn.Render.TerrainGen;

/// <summary>
/// The viewer's per-patch terrain LOD: each 16x16 patch renders at stride 1, 2, 4, 8 or 16 grid steps
/// depending on its distance from the camera.
/// </summary>
public static class TerrainLod
{
    // RenderTerrainLODFactor in the stock graphics presets (featuretable.txt): Low 1, LowMid 1.0, Mid and above
    // 2.0. settings.xml default 1.0. The viewer squares it (llappviewer.cpp: "exponential range of [1,4]").
    public const float LodFactorDefault = 1.0f;
    public const float LodFactorMidAndAbove = 2.0f;

    private const float DefaultDeltaAngle = 0.15f;

    // LLPatchVertexArray::init for a 16-wide patch. Stride 0-1 -> level 4, then each level covers twice as many.
    private static readonly int[] RenderStrideByLevel;
    private static readonly int[] RenderLevelByStride;

    static TerrainLod() => RenderStrideByLevel = BuildStrideTable(TerrainSurface.GridsPerPatchEdge, out RenderLevelByStride);

    private static int[] BuildStrideTable(int patchWidth, out int[] levelByStride)
    {
        int order = 0;
        for (int p = 1; p < patchWidth; p *= 2)
            order++;
        var strideByLevel = new int[order + 1];
        int stride = patchWidth;
        for (int level = 0; level < order + 1; level++)
        {
            strideByLevel[level] = stride;
            stride /= 2;
        }

        levelByStride = new int[2 * patchWidth + 1];
        int lvl = order, k = 2;
        levelByStride[0] = order;
        levelByStride[1] = order;
        stride = 2;
        while (stride < 2 * patchWidth)
        {
            for (int j = 0; j < k && stride < 2 * patchWidth; j++)
                levelByStride[stride++] = lvl;
            k *= 2;
            lvl--;
        }
        levelByStride[2 * patchWidth] = 0;
        return strideByLevel;
    }

    /// <summary>
    /// LLSurfacePatch::updateCameraDistanceRegion + updateVisibility: the stride a visible patch gets.
    /// <paramref name="cameraRegion"/> is the camera in this region's coordinates (may lie outside it).
    /// <paramref name="lodFactorSetting"/> is RenderTerrainLODFactor as set (it gets squared here).
    /// <paramref name="dynamicLod"/> is RenderDynamicLOD (on by default); off renders every patch at stride 1.
    /// </summary>
    public static int ViewerPatchStride(TerrainPatchBounds bounds, Vector3 cameraRegion, float lodFactorSetting = LodFactorDefault,
        float metersPerGrid = 1f, bool dynamicLod = true)
    {
        float distance = 0f;
        if (dynamicLod)
        {
            float lodFactor = lodFactorSetting * lodFactorSetting;
            float dx = cameraRegion.X - bounds.Center.X, dy = cameraRegion.Y - bounds.Center.Y, dz = cameraRegion.Z - bounds.Center.Z;
            float mag = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            distance = TerrainSurface.LLMax(0f, mag - bounds.Radius) / TerrainSurface.LLMax(lodFactor, 0.1f);
        }

        float stridePerDistance = DefaultDeltaAngle / metersPerGrid;
        int gridsPerPatchEdge = TerrainSurface.GridsPerPatchEdge;
        uint maxRenderStride = (uint)(int)(distance * stridePerDistance);
        maxRenderStride = Math.Min(maxRenderStride, (uint)(2 * gridsPerPatchEdge));
        return RenderStrideByLevel[RenderLevelByStride[maxRenderStride]];
    }

    /// <summary>
    /// A stride function for <see cref="TerrainMeshBuilder"/> as a resident's viewer would have it with the
    /// camera at <paramref name="cameraRegion"/>: every patch at its distance stride, including the patches of
    /// connected east/north neighbours (asked for as px == PatchesPerEdge or py == PatchesPerEdge).
    /// The viewer only updates a patch's stride while it is inside the view frustum (patches never seen keep
    /// the initial 16). Pass <paramref name="inFrustum"/> (sphere centre in region space, radius) to model that
    /// for a fresh login; without it every patch is treated as seen, which only differs in how visible patches
    /// stitch to just-off-screen neighbours.
    /// </summary>
    public static Func<int, int, int> CameraStrides(TerrainSurface surface, Vector3 cameraRegion, float lodFactorSetting = LodFactorDefault,
        bool dynamicLod = true, Func<Vector3, float, bool>? inFrustum = null)
    {
        int n = surface.PatchesPerEdge;
        float edge = surface.Width * surface.MetersPerGrid;
        var cache = new Dictionary<(int, int), int>();
        return (px, py) =>
        {
            if (cache.TryGetValue((px, py), out int s))
                return s;
            TerrainSurface? target = surface;
            Vector3 cam = cameraRegion;
            Vector3 offset = Vector3.Zero;
            int tx = px, ty = py;
            if (px >= n && py >= n) { target = surface.Neighbor(TerrainDirection.NorthEast); tx -= n; ty -= n; offset = new Vector3(edge, edge, 0); }
            else if (px >= n) { target = surface.Neighbor(TerrainDirection.East); tx -= n; offset = new Vector3(edge, 0, 0); }
            else if (py >= n) { target = surface.Neighbor(TerrainDirection.North); ty -= n; offset = new Vector3(0, edge, 0); }
            else if (px < 0 || py < 0) target = null;

            if (target == null || tx < 0 || ty < 0 || tx >= n || ty >= n)
            {
                s = TerrainSurface.GridsPerPatchEdge;
            }
            else
            {
                var b = target.GetPatchBounds(tx, ty);
                cam -= offset;
                if (inFrustum != null && !inFrustum(b.Center + offset, b.Radius))
                    s = TerrainSurface.GridsPerPatchEdge;
                else
                    s = ViewerPatchStride(b, cam, lodFactorSetting, surface.MetersPerGrid, dynamicLod);
            }
            cache[(px, py)] = s;
            return s;
        };
    }

    /// <summary>Every patch at one stride (1 = full detail).</summary>
    public static Func<int, int, int> Uniform(int stride) => (_, _) => stride;
}
