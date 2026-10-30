// Ported from the Second Life viewer: indra/newview/llsurface.cpp, llsurfacepatch.cpp, llvlcomposition.cpp
// (generateHeights), llviewerlayer.cpp, llviewerregion.cpp (getCompositionXY).
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
namespace Sojourn.Render.TerrainGen;

/// <summary>Which per-vertex normals the viewer computes (LLSurface::idleUpdate&lt;PBR&gt;).</summary>
public enum TerrainNormalMode
{
    /// <summary>calcNormal&lt;false&gt;: smooth, from heights 2 grid steps either side (texture terrain).</summary>
    Classic,
    /// <summary>calcNormal&lt;true&gt; = calcNormalFlat(index 0), used while the region renders PBR terrain.</summary>
    PbrFlat,
}

/// <summary>
/// The camera-independent part of a region's terrain, as LLSurface holds it: the (W+1)^2 height grid
/// with the viewer's east/north buffer row and column, per-grid-point normals, per-patch bounds and the
/// composition layer. Build once per region (and again when heights, neighbours or the material type
/// change); <see cref="TerrainMeshBuilder"/> turns it into geometry for a set of patch strides.
/// </summary>
public sealed class TerrainSurface
{
    public const int GridsPerPatchEdge = 16;

    public TerrainInput Input { get; }
    public TerrainNormalMode NormalMode { get; }

    /// <summary>Grid samples per region edge (256 in SL).</summary>
    public int Width { get; }
    /// <summary>Width + 1: the viewer's array stride, with the east/north buffer.</summary>
    public int GridsPerEdge { get; }
    public int PatchesPerEdge { get; }
    /// <summary>region_width_meters / grids_per_region_edge; always 1 for SL and OpenSim.</summary>
    public float MetersPerGrid { get; } = 1f;

    internal readonly float[] Z;
    internal readonly Vector3[] Norm;
    internal readonly SurfacePatch[] Patches;
    /// <summary>The composition layer (LLVLComposition's LLViewerLayer data), Width*Width, values 0-3.</summary>
    public float[] Composition { get; }

    /// <summary>Neighbour surfaces by direction (heights and composition only).</summary>
    internal readonly TerrainSurface?[] NeighborSurfaces = new TerrainSurface?[8];

    public TerrainSurface(TerrainInput input, TerrainNormalMode normalMode = TerrainNormalMode.Classic)
        : this(input, normalMode, isNeighbor: false)
    {
    }

    private TerrainSurface(TerrainInput input, TerrainNormalMode normalMode, bool isNeighbor)
    {
        if (input.Width % GridsPerPatchEdge != 0 || input.Width <= 0)
            throw new ArgumentException("Region width must be a positive multiple of 16.", nameof(input));
        if (input.Heights.Length != input.Width * input.Width)
            throw new ArgumentException("Heights must hold Width*Width samples.", nameof(input));
        if (input.StartHeights.Length < 4 || input.HeightRanges.Length < 4)
            throw new ArgumentException("StartHeights and HeightRanges need the four corner values.", nameof(input));
        Input = input;
        NormalMode = normalMode;
        Width = input.Width;
        GridsPerEdge = Width + 1;
        PatchesPerEdge = Width / GridsPerPatchEdge;

        int g = GridsPerEdge;
        Z = new float[g * g];
        Norm = new Vector3[g * g];
        for (int y = 0; y < Width; y++)
            Array.Copy(input.Heights, y * Width, Z, y * g, Width);
        Array.Fill(Norm, new Vector3(0f, 0f, 1f));

        Patches = new SurfacePatch[PatchesPerEdge * PatchesPerEdge];
        CreatePatchData();

        if (!isNeighbor)
        {
            for (int dir = 0; dir < 8; dir++)
            {
                var n = dir < input.Neighbors.Length ? input.Neighbors[dir] : null;
                if (n == null)
                    continue;
                if (n.Width != Width)
                    throw new ArgumentException("Neighbouring regions must have the same width (the viewer only connects equal surfaces).");
                NeighborSurfaces[dir] = new TerrainSurface(n, normalMode, isNeighbor: true);
            }
            for (int dir = 0; dir < 8; dir++)
                if (NeighborSurfaces[dir] is { } ns)
                    ConnectNeighbor(ns, dir);
        }

        UpdateEdges();
        if (!isNeighbor)
            foreach (var p in Patches)
                p.UpdateNormals(normalMode == TerrainNormalMode.PbrFlat);
        foreach (var p in Patches)
            p.UpdateVerticalStats();

        Composition = new float[Width * Width];
        GenerateComposition();
    }

    public float GetZ(int x, int y) => Z[x + y * GridsPerEdge];

    /// <summary>Per-grid-point normal as the viewer stores it (x, y in 0..Width inclusive).</summary>
    public Vector3 GetNormal(int x, int y) => Norm[x + y * GridsPerEdge];

    /// <summary>Patch bounds as the viewer uses them for LOD and culling (region space).</summary>
    public TerrainPatchBounds GetPatchBounds(int px, int py)
    {
        var p = Patches[px + py * PatchesPerEdge];
        return new TerrainPatchBounds(p.CenterRegion, p.Radius, p.MinZ, p.MaxZ);
    }

    internal TerrainSurface? Neighbor(int dir) => NeighborSurfaces[dir];

    // LLSurface::createPatchData
    private void CreatePatchData()
    {
        int n = PatchesPerEdge;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                Patches[i + j * n] = new SurfacePatch(this, i, j);

        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                var p = Patches[i + j * n];
                p.Neighbors[TerrainDirection.East] = i < n - 1 ? Patches[i + 1 + j * n] : null;
                p.Neighbors[TerrainDirection.North] = j < n - 1 ? Patches[i + (j + 1) * n] : null;
                p.Neighbors[TerrainDirection.West] = i > 0 ? Patches[i - 1 + j * n] : null;
                p.Neighbors[TerrainDirection.South] = j > 0 ? Patches[i + (j - 1) * n] : null;
                p.Neighbors[TerrainDirection.NorthEast] = i < n - 1 && j < n - 1 ? Patches[i + 1 + (j + 1) * n] : null;
                p.Neighbors[TerrainDirection.NorthWest] = i > 0 && j < n - 1 ? Patches[i - 1 + (j + 1) * n] : null;
                p.Neighbors[TerrainDirection.SouthWest] = i > 0 && j > 0 ? Patches[i - 1 + (j - 1) * n] : null;
                p.Neighbors[TerrainDirection.SouthEast] = i < n - 1 && j > 0 ? Patches[i + 1 + (j - 1) * n] : null;
            }
        }
    }

    private SurfacePatch GetPatch(int x, int y) => Patches[x + y * PatchesPerEdge];

    // LLSurface::connectNeighbor, the patch links only (this side; the neighbour's patches aren't rebuilt).
    private void ConnectNeighbor(TerrainSurface nb, int direction)
    {
        int last = PatchesPerEdge - 1;
        switch (direction)
        {
            case TerrainDirection.NorthEast:
                GetPatch(last, last).ConnectNeighbor(nb.GetPatch(0, 0), direction);
                break;
            case TerrainDirection.NorthWest:
                GetPatch(0, last).ConnectNeighbor(nb.GetPatch(last, 0), direction);
                break;
            case TerrainDirection.SouthWest:
                GetPatch(0, 0).ConnectNeighbor(nb.GetPatch(last, last), direction);
                break;
            case TerrainDirection.SouthEast:
                GetPatch(last, 0).ConnectNeighbor(nb.GetPatch(0, last), direction);
                break;
            case TerrainDirection.East:
                for (int i = 0; i <= last; i++)
                    GetPatch(last, i).ConnectNeighbor(nb.GetPatch(0, i), direction);
                for (int i = 0; i < last; i++)
                    GetPatch(last, i).ConnectNeighbor(nb.GetPatch(0, i + 1), TerrainDirection.NorthEast);
                for (int i = 1; i <= last; i++)
                    GetPatch(last, i).ConnectNeighbor(nb.GetPatch(0, i - 1), TerrainDirection.SouthEast);
                break;
            case TerrainDirection.North:
                for (int i = 0; i <= last; i++)
                    GetPatch(i, last).ConnectNeighbor(nb.GetPatch(i, 0), direction);
                for (int i = 0; i < last; i++)
                    GetPatch(i, last).ConnectNeighbor(nb.GetPatch(i + 1, 0), TerrainDirection.NorthEast);
                for (int i = 1; i <= last; i++)
                    GetPatch(i, last).ConnectNeighbor(nb.GetPatch(i - 1, 0), TerrainDirection.NorthWest);
                break;
            case TerrainDirection.West:
                for (int i = 0; i <= last; i++)
                    GetPatch(0, i).ConnectNeighbor(nb.GetPatch(last, i), direction);
                for (int i = 1; i <= last; i++)
                    GetPatch(0, i).ConnectNeighbor(nb.GetPatch(last, i - 1), TerrainDirection.SouthWest);
                for (int i = 0; i < last; i++)
                    GetPatch(0, i).ConnectNeighbor(nb.GetPatch(last, i + 1), TerrainDirection.NorthWest);
                break;
            case TerrainDirection.South:
                for (int i = 0; i <= last; i++)
                    GetPatch(i, 0).ConnectNeighbor(nb.GetPatch(i, last), direction);
                for (int i = 1; i <= last; i++)
                    GetPatch(i, 0).ConnectNeighbor(nb.GetPatch(i - 1, last), TerrainDirection.SouthWest);
                for (int i = 0; i < last; i++)
                    GetPatch(i, 0).ConnectNeighbor(nb.GetPatch(i + 1, last), TerrainDirection.SouthEast);
                break;
        }
    }

    // The buffer row/column as decompressDCTPatch and connectNeighbor leave it (updateEastEdge/updateNorthEdge
    // on every patch), then the northeast corner that updateNormals fills in.
    private void UpdateEdges()
    {
        foreach (var p in Patches)
        {
            p.UpdateEastEdge();
            p.UpdateNorthEdge();
        }
        foreach (var p in Patches)
            p.UpdateNorthEastCorner();
    }

    /// <summary>LLSurface::resolveHeightRegion: height on the rendered triangles at region (x, y); 0 outside.</summary>
    public float ResolveHeightRegion(float x, float y)
    {
        float height = 0f;
        float oometerspergrid = 1f / MetersPerGrid;
        float metersPerEdge = MetersPerGrid * (GridsPerEdge - 1);
        if (x >= 0f && x <= metersPerEdge && y >= 0f && y <= metersPerEdge)
        {
            int left = (int)MathF.Floor(x * oometerspergrid);
            int bottom = (int)MathF.Floor(y * oometerspergrid);
            int right = left + 1 < GridsPerEdge - 1 ? left + 1 : left;
            int top = bottom + 1 < GridsPerEdge - 1 ? bottom + 1 : bottom;

            float leftBottom = GetZ(left, bottom);
            float rightBottom = GetZ(right, bottom);
            float leftTop = GetZ(left, top);
            float rightTop = GetZ(right, top);

            float dx = x - left * MetersPerGrid;
            float dy = y - bottom * MetersPerGrid;
            if (dy > dx)
            {
                dy *= leftTop - leftBottom;
                dx *= rightTop - leftTop;
            }
            else
            {
                dx *= rightBottom - leftBottom;
                dy *= rightTop - rightBottom;
            }
            height = leftBottom + (dx + dy) * oometerspergrid;
        }
        return height;
    }

    // LLVLComposition::generateHeights over the whole layer. Each texel depends only on its own height and
    // position, so the viewer's per-patch calls (17x17 texels each, overlapping) produce exactly this.
    // The arithmetic follows the compiled viewer (MSVC /fp:fast reassociates two sums; see comments), which is
    // what makes it bit-identical rather than within an ulp.
    private void GenerateComposition()
    {
        const float slopeSquared = 1.5f * 1.5f;
        const float xyScale = 4.9215f;
        const float zOffset = 0f;
        const float noiseMagnitude = 2f;
        const int assetCount = 4;
        float xyScaleInv = 1f / xyScale;
        float invWidth = 1f / Width;
        float scale = MetersPerGrid; // LLViewerLayer mScale = region_width_meters / grids_per_region_edge
        var sh = Input.StartHeights;
        var hr = Input.HeightRanges;

        for (int j = 0; j < Width; j++)
        {
            for (int i = 0; i < Width; i++)
            {
                float startHeight = Bilinear(sh[0], sh[1], sh[2], sh[3], i * invWidth, j * invWidth);
                float heightRange = Bilinear(hr[0], hr[1], hr[2], hr[3], i * invWidth, j * invWidth);

                float lx = i * scale, ly = j * scale;
                float height = ResolveHeightRegion(lx, ly) + zOffset;

                float v0 = (float)(Input.OriginGlobalX + lx) * xyScaleInv;
                float v1 = (float)(Input.OriginGlobalY + ly) * xyScaleInv;

                float low = TerrainNoise.Noise2(v0 * 0.2222222222f, v1 * 0.2222222222f);
                float turb = TerrainNoise.Turbulence2(v0, v1, 2);

                // Source: twiddle = (low*6.5 + turb*slope^2) * 2; (height + twiddle - start) * 4 / range.
                // Compiled: ((low*13 + height) + (turb*2.25 + turb*2.25) - start) * 4 / range.
                float lowPart = low * (6.5f * noiseMagnitude);
                float turbPart = turb * slopeSquared;
                turbPart += turbPart;
                float scaled = (lowPart + height + turbPart - startHeight) * assetCount / heightRange;

                // llmax(0, s) then llmin(3, s), as compiled (comiss: NaN becomes 0)
                if (!(scaled >= 0f))
                    scaled = 0f;
                else if (scaled > 3f)
                    scaled = 3f;
                Composition[i + j * Width] = scaled;
            }
        }
    }

    // llvlcomposition.cpp bilinear(): v01 is weighted by (1-x)*y, so corner "01" sits at x=0, y=1.
    // Source sums the four terms left to right; compiled it is (t00 + t10) + (t01 + t11).
    private static float Bilinear(float v00, float v01, float v10, float v11, float xFrac, float yFrac)
    {
        float invX = 1f - xFrac;
        float invY = 1f - yFrac;
        return (invX * invY * v00 + xFrac * invY * v10) + (invX * yFrac * v01 + xFrac * yFrac * v11);
    }

    internal static float LLMax(float a, float b) => b < a ? a : b;
    internal static float LLMin(float a, float b) => a < b ? a : b;

    /// <summary>LLViewerLayer::getValueScaled on the composition layer.</summary>
    public float CompositionScaled(float x, float y)
    {
        float scaleInv = 1f / MetersPerGrid;
        float xFrac = x * scaleInv;
        int x1 = (int)MathF.Floor(xFrac);
        int x2 = x1 + 1;
        xFrac -= x1;
        float yFrac = y * scaleInv;
        int y1 = (int)MathF.Floor(yFrac);
        int y2 = y1 + 1;
        yFrac -= y1;

        int w = Width;
        x1 = Math.Max(0, Math.Min(w - 1, x1));
        x2 = Math.Max(0, Math.Min(w - 1, x2));
        y1 = Math.Max(0, Math.Min(w - 1, y1));
        y2 = Math.Max(0, Math.Min(w - 1, y2));

        float r1l = Composition[y1 * w + x1];
        float r1r = Composition[y1 * w + x2];
        float r2l = Composition[y2 * w + x1];
        float r2r = Composition[y2 * w + x2];
        float r1 = r1l - xFrac * (r1l - r1r);
        float r2 = r2l - xFrac * (r2l - r2r);
        return r1 - yFrac * (r1 - r2);
    }

    /// <summary>
    /// LLViewerRegion::getCompositionXY: the composition at grid (x, y), reaching into the east/north/northeast
    /// neighbour past the edge and shifting its value by whole units toward ours. (The viewer hardcodes 256;
    /// this uses Width, identical for SL regions.)
    /// </summary>
    public float CompositionXY(int x, int y)
    {
        int w = Width;
        if (x >= w)
        {
            if (y >= w)
            {
                if (NeighborSurfaces[TerrainDirection.NorthEast] is { } ne)
                    return MatchFraction(CompositionScaled(w - 1, w - 1), ne.CompositionScaled(x - (float)w, y - (float)w));
            }
            else if (NeighborSurfaces[TerrainDirection.East] is { } e)
            {
                return MatchFraction(CompositionScaled(w - 1, y), e.CompositionScaled(x - (float)w, y));
            }
        }
        else if (y >= w)
        {
            if (NeighborSurfaces[TerrainDirection.North] is { } n)
                return MatchFraction(CompositionScaled(x, w - 1), n.CompositionScaled(x, y - (float)w));
        }
        return CompositionScaled(x, y);
    }

    private static float MatchFraction(float ourComp, float adjComp)
    {
        while (MathF.Abs(ourComp - adjComp) >= 1f)
        {
            if (ourComp > adjComp)
                adjComp += 1f;
            else
                adjComp -= 1f;
        }
        return adjComp;
    }
}

/// <summary>A patch's bounding sphere and height range (LLSurfacePatch::updateVerticalStats), region space.</summary>
public readonly record struct TerrainPatchBounds(Vector3 Center, float Radius, float MinZ, float MaxZ);

// LLSurfacePatch: the camera-independent parts.
internal sealed class SurfacePatch
{
    public readonly TerrainSurface Surface;
    public readonly int Px, Py;
    public readonly int DataOffset;
    public readonly SurfacePatch?[] Neighbors = new SurfacePatch?[8];
    public byte ConnectedEdge;
    public float MinZ, MaxZ;
    public Vector3 CenterRegion;
    public float Radius;
    public readonly float OriginRegionX, OriginRegionY;
    public readonly double OriginGlobalX, OriginGlobalY;

    public const byte EastEdge = 0x01, NorthEdge = 0x02, WestEdge = 0x04, SouthEdge = 0x08;

    public SurfacePatch(TerrainSurface surface, int px, int py)
    {
        Surface = surface;
        Px = px;
        Py = py;
        const int g = TerrainSurface.GridsPerPatchEdge;
        DataOffset = px * g + py * g * surface.GridsPerEdge;
        // setOriginGlobal: origin_region = i*mpg*16, center = origin + 8*mpg (z from the vertical stats)
        float mpg = surface.MetersPerGrid;
        OriginRegionX = px * mpg * g;
        OriginRegionY = py * mpg * g;
        OriginGlobalX = surface.Input.OriginGlobalX + OriginRegionX;
        OriginGlobalY = surface.Input.OriginGlobalY + OriginRegionY;
        CenterRegion = new Vector3(OriginRegionX + 0.5f * g * mpg, OriginRegionY + 0.5f * g * mpg, 0f);
    }

    private float[] Z => Surface.Z;

    public void ConnectNeighbor(SurfacePatch nb, int direction)
    {
        Neighbors[direction] = nb;
        if (direction == TerrainDirection.East) ConnectedEdge |= EastEdge;
        else if (direction == TerrainDirection.North) ConnectedEdge |= NorthEdge;
        else if (direction == TerrainDirection.West) ConnectedEdge |= WestEdge;
        else if (direction == TerrainDirection.South) ConnectedEdge |= SouthEdge;
    }

    public void UpdateEastEdge()
    {
        const int gp = TerrainSurface.GridsPerPatchEdge;
        int ge = Surface.GridsPerEdge;
        float[] west, east;
        int westOff, eastOff;
        var e = Neighbors[TerrainDirection.East];
        if (e == null)
        {
            west = Z; westOff = DataOffset + gp;
            east = Z; eastOff = DataOffset + gp - 1;
        }
        else if ((ConnectedEdge & EastEdge) != 0)
        {
            west = Z; westOff = DataOffset + gp;
            east = e.Z; eastOff = e.DataOffset;
        }
        else
        {
            return;
        }
        for (int j = 0; j < gp; j++)
        {
            int k = j * ge;
            west[westOff + k] = east[eastOff + k];
        }
    }

    public void UpdateNorthEdge()
    {
        const int gp = TerrainSurface.GridsPerPatchEdge;
        int ge = Surface.GridsPerEdge;
        float[] south, north;
        int southOff, northOff;
        var n = Neighbors[TerrainDirection.North];
        if (n == null)
        {
            south = Z; southOff = DataOffset + gp * ge;
            north = Z; northOff = DataOffset + (gp - 1) * ge;
        }
        else if ((ConnectedEdge & NorthEdge) != 0)
        {
            south = Z; southOff = DataOffset + gp * ge;
            north = n.Z; northOff = n.DataOffset;
        }
        else
        {
            return;
        }
        for (int i = 0; i < gp; i++)
            south[southOff + i] = north[northOff + i];
    }

    // The NORTHEAST block of LLSurfacePatch::updateNormals: fills this patch's (16,16) height.
    public void UpdateNorthEastCorner()
    {
        const int gp = TerrainSurface.GridsPerPatchEdge;
        int ge = Surface.GridsPerEdge;
        int corner = DataOffset + gp + gp * ge;
        int diag = DataOffset + gp - 1 + (gp - 1) * ge;
        var ne = Neighbors[TerrainDirection.NorthEast];
        var n = Neighbors[TerrainDirection.North];
        var e = Neighbors[TerrainDirection.East];
        if (ne == null)
        {
            if (n == null)
            {
                if (e == null)
                    Z[corner] = Z[diag]; // no north or east neighbours: pull from the diagonal in your own patch
                else
                    Z[corner] = e.Z[e.DataOffset + (gp - 1) * ge]; // east, but not north: east's northwest point
            }
            else
            {
                if (e != null)
                    Z[corner] = Z[diag]; // north and east, but not northeast
                else
                    Z[corner] = n.Z[n.DataOffset + (gp - 1)]; // north, but not east: north's southeast corner
            }
        }
        else if (ne.Surface != Surface)
        {
            if ((n == null || n.Surface != Surface) && (e == null || e.Surface != Surface))
                Z[corner] = ne.Z[ne.DataOffset];
        }
        // else: a northeast patch in the same surface handles it
    }

    // LLSurfacePatch::updateNormals with every section invalid (a full rebuild).
    public void UpdateNormals(bool pbr)
    {
        const int gp = TerrainSurface.GridsPerPatchEdge;
        int i, j;
        for (j = 0; j <= gp; j++)
        {
            CalcNormal(pbr, gp, j, 2);
            CalcNormal(pbr, gp - 1, j, 2);
            CalcNormal(pbr, gp - 2, j, 2);
        }
        for (i = 0; i <= gp; i++)
        {
            CalcNormal(pbr, i, gp, 2);
            CalcNormal(pbr, i, gp - 1, 2);
            CalcNormal(pbr, i, gp - 2, 2);
        }
        for (j = 0; j < gp; j++)
        {
            CalcNormal(pbr, 0, j, 2);
            CalcNormal(pbr, 1, j, 2);
        }
        for (i = 0; i < gp; i++)
        {
            CalcNormal(pbr, i, 0, 2);
            CalcNormal(pbr, i, 1, 2);
        }
        CalcNormal(pbr, gp, gp, 2);
        CalcNormal(pbr, gp, gp - 1, 2);
        CalcNormal(pbr, gp - 1, gp, 2);
        CalcNormal(pbr, gp - 1, gp - 1, 2);
        for (j = 2; j < gp - 2; j++)
            for (i = 2; i < gp - 2; i++)
                CalcNormal(pbr, i, j, 2);
    }

    private void CalcNormal(bool pbr, int x, int y, int stride)
    {
        Vector3 normal = pbr ? CalcNormalFlat(x, y) : CalcNormalSmooth(x, y, stride);
        Surface.Norm[DataOffset + Surface.GridsPerEdge * y + x] = normal;
    }

    // Step an offset past this patch's edge into the neighbour (or clamp when there's none), x then y.
    private float SampleWalk(int ox, int oy)
    {
        const int pw = TerrainSurface.GridsPerPatchEdge; // mPVArray.mPatchWidth
        SurfacePatch p = this;
        if (ox < 0)
        {
            if (p.Neighbors[TerrainDirection.West] is not { } w) ox = 0;
            else { ox += pw; p = w; }
        }
        if (oy < 0)
        {
            if (p.Neighbors[TerrainDirection.South] is not { } s) oy = 0;
            else { oy += pw; p = s; }
        }
        if (ox >= pw)
        {
            if (p.Neighbors[TerrainDirection.East] is not { } e) ox = pw - 1;
            else { ox -= pw; p = e; }
        }
        if (oy >= pw)
        {
            if (p.Neighbors[TerrainDirection.North] is not { } n) oy = pw - 1;
            else { oy -= pw; p = n; }
        }
        return p.Z[p.DataOffset + ox + oy * p.Surface.GridsPerEdge];
    }

    // calcNormal<false>
    private Vector3 CalcNormalSmooth(int x, int y, int stride)
    {
        float mpg = Surface.MetersPerGrid * stride;
        float z00 = SampleWalk(x - stride, y - stride);
        float z01 = SampleWalk(x - stride, y + stride);
        float z10 = SampleWalk(x + stride, y - stride);
        float z11 = SampleWalk(x + stride, y + stride);
        var p00 = new Vector3(-mpg, -mpg, z00);
        var p01 = new Vector3(-mpg, +mpg, z01);
        var p10 = new Vector3(+mpg, -mpg, z10);
        var p11 = new Vector3(+mpg, +mpg, z11);
        return NormVec(Cross(Sub(p11, p00), Sub(p01, p10)));
    }

    // calcNormalFlat(index 0): the triangle (x,y),(x+1,y),(x,y+1), but placed 2 grid units apart, so
    // slopes come out halved; faithful to the viewer's PBR proof-of-concept normals.
    private Vector3 CalcNormalFlat(int x, int y)
    {
        const int stride = 1;
        float mpg = Surface.MetersPerGrid * stride;
        float z00 = SampleWalk(x, y);
        float z01 = SampleWalk(x, y + stride);
        float z10 = SampleWalk(x + stride, y);
        var p00 = new Vector3(-mpg, -mpg, z00);
        var p01 = new Vector3(-mpg, +mpg, z01);
        var p10 = new Vector3(+mpg, -mpg, z10);
        return NormVec(Cross(Sub(p10, p00), Sub(p01, p00)));
    }

    private static Vector3 Sub(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    // LLVector3 operator%
    private static Vector3 Cross(Vector3 a, Vector3 b) =>
        new(a.Y * b.Z - b.Y * a.Z, a.Z * b.X - b.Z * a.X, a.X * b.Y - b.X * a.Y);

    // LLVector3::normalize
    internal static Vector3 NormVec(Vector3 v)
    {
        float mag = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (mag > 0.0000001f)
        {
            float oomag = 1f / mag;
            return new Vector3(v.X * oomag, v.Y * oomag, v.Z * oomag);
        }
        return Vector3.Zero;
    }

    // LLSurfacePatch::updateVerticalStats
    public void UpdateVerticalStats()
    {
        const int gp = TerrainSurface.GridsPerPatchEdge;
        int ge = Surface.GridsPerEdge;
        float mpg = Surface.MetersPerGrid;
        float z = Z[DataOffset];
        MinZ = z;
        MaxZ = z;
        for (int j = 0; j < gp + 1; j++)
        {
            for (int i = 0; i < gp + 1; i++)
            {
                z = Z[DataOffset + i + j * ge];
                if (z < MinZ) MinZ = z;
                if (z > MaxZ) MaxZ = z;
            }
        }
        CenterRegion.Z = 0.5f * (MinZ + MaxZ);
        float dx = mpg * gp, dy = mpg * gp, dz = MaxZ - MinZ;
        Radius = MathF.Sqrt(dx * dx + dy * dy + dz * dz) * 0.5f;
    }
}
