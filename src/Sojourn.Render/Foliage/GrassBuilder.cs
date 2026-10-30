// Ported from the Second Life viewer: indra/newview/llvograss.cpp (LLVOGrass::initClass blade tables,
// updateLOD, getGeometry).
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
namespace Sojourn.Render.Foliage;

/// <summary>
/// The per-blade random tables LLVOGrass::initClass fills once per viewer session with ll_frand (seeded from the
/// clock), shared by every grass object in that session. So no two viewer sessions lay grass out alike; any fixed
/// table is as faithful as another. <see cref="Default"/> is deterministic.
/// </summary>
public sealed class GrassBladeTable
{
    public const int MaxBlades = 32; // GRASS_MAX_BLADES

    public float[] ExpX { get; } = new float[MaxBlades];
    public float[] ExpY { get; } = new float[MaxBlades];
    public float[] RotX { get; } = new float[MaxBlades];
    public float[] RotY { get; } = new float[MaxBlades];
    public float[] DzX { get; } = new float[MaxBlades];
    public float[] DzY { get; } = new float[MaxBlades];
    public float[] WMod { get; } = new float[MaxBlades];

    public static GrassBladeTable Default { get; } = Generate(0x5EED_6A55);

    /// <summary>initClass's distribution (Box-Muller gaussian offsets, SD 0.15), from a seeded generator.</summary>
    public static GrassBladeTable Generate(ulong seed)
    {
        const float sd = 0.15f;        // GRASS_DISTRIBUTION_SD
        const float bladeBase = 0.25f; // GRASS_BLADE_BASE
        ulong state = seed;
        float Frand() // [0, 1), like ll_frand
        {
            // splitmix64
            ulong z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 40) * (1f / (1 << 24));
        }

        var t = new GrassBladeTable();
        for (int i = 0; i < MaxBlades; i++)
        {
            float u = MathF.Sqrt(-2.0f * MathF.Log(MathF.Max(Frand(), 1e-7f)));
            float v = 2.0f * FoliageMath.Pi * Frand();
            float x = u * MathF.Sin(v) * sd;
            float y = u * MathF.Cos(v) * sd;
            float rot = Frand() * FoliageMath.Pi;
            t.ExpX[i] = x;
            t.ExpY[i] = y;
            t.RotX[i] = MathF.Sin(rot);
            t.RotY[i] = MathF.Cos(rot);
            t.DzX[i] = Frand() * (bladeBase * 0.25f);
            t.DzY[i] = Frand() * (bladeBase * 0.25f);
            t.WMod[i] = 0.5f + Frand();
        }
        return t;
    }
}

/// <summary>
/// Linden grass (PCode Grass). A clump of up to 32 crossed-pair blades scattered around the object's position by
/// its X/Y scale; each blade's foot sits on the terrain heightfield (not the object's Z), and the object's
/// rotation is ignored. Output is already in the frame of the given position (the viewer's is agent space);
/// draw with an identity model matrix (or the region offset).
/// </summary>
public static class GrassBuilder
{
    private const float BladeBase = 0.25f;   // GRASS_BLADE_BASE, width at base
    private const float BladeHeight = 0.5f;  // GRASS_BLADE_HEIGHT, metres

    /// <summary>
    /// LLVOGrass::getGeometry. <paramref name="terrainHeight"/> is LLSurface::resolveHeightRegion(x, y) in region
    /// coordinates (the viewer returns 0 outside [0, region width]). Output positions are
    /// region-local + <paramref name="originOffset"/> (pass the region's agent-space origin to get agent space).
    /// Species is the object's State byte (LibreMetaverse <c>PrimData.State</c>; LLVOGrass::updateSpecies).
    /// </summary>
    public static FoliageMesh Build(int species, int blades, Vector3 positionRegion, Vector3 scale,
        Func<float, float, float> terrainHeight, Vector3 originOffset = default, GrassBladeTable? table = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blades, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blades, GrassBladeTable.MaxBlades);
        var sp = FoliageSpecies.Grass(species);
        table ??= GrassBladeTable.Default;

        var pos = new Vector3[blades * 8];
        var nrm = new Vector3[blades * 8];
        var tcs = new Vector2[blades * 8];
        var idx = new uint[blades * 12];

        float width = sp.BladeSizeX;
        float height = sp.BladeSizeY;
        int v = 0, k = 0;
        uint indexOffset = 0;

        for (int i = 0; i < blades; i++)
        {
            float x = table.ExpX[i] * scale.X;
            float y = table.ExpY[i] * scale.Y;
            float xf = table.RotX[i] * BladeBase * width * table.WMod[i];
            float yf = table.RotY[i] * BladeBase * width * table.WMod[i];
            float dzx = table.DzX[i];
            float dzy = table.DzY[i];
            float bladeHeight = BladeHeight * height * table.WMod[i];

            tcs[v + 0] = new Vector2(0, 0);
            tcs[v + 1] = new Vector2(0, 0);
            tcs[v + 2] = new Vector2(0, 0.98f);
            tcs[v + 3] = new Vector2(0, 0.98f);
            tcs[v + 4] = new Vector2(1, 0);
            tcs[v + 5] = new Vector2(1, 0);
            tcs[v + 6] = new Vector2(1, 0.98f);
            tcs[v + 7] = new Vector2(1, 0.98f);

            Vector3 p;
            p.X = positionRegion.X + x + xf;
            p.Y = positionRegion.Y + y + yf;
            p.Z = terrainHeight(p.X, p.Y);
            Vector3 v1 = p + originOffset;
            pos[v + 0] = v1;
            pos[v + 1] = v1;

            p.X += dzx;
            p.Y += dzy;
            p.Z += bladeHeight;
            Vector3 v2 = p + originOffset;
            pos[v + 2] = v2;
            pos[v + 3] = v2;

            p.X = positionRegion.X + x - xf;
            p.Y = positionRegion.Y + y - xf; // sic: the viewer uses xf here, not yf
            p.Z = terrainHeight(p.X, p.Y);
            Vector3 v3 = p + originOffset;
            pos[v + 4] = v3;
            pos[v + 5] = v3;

            Vector3 normal1 = FoliageMath.Cross(v1 - v2, v2 - v3);
            normal1.Z = 0.75f;
            normal1 = FoliageMath.Normalize(normal1);
            Vector3 normal2 = -normal1;
            normal2.Z = -normal2.Z;

            p.X += dzx;
            p.Y += dzy;
            p.Z += bladeHeight;
            v1 = p + originOffset;
            pos[v + 6] = v1;
            pos[v + 7] = v1;

            for (int n = 0; n < 8; n++)
                nrm[v + n] = (n & 1) == 0 ? normal1 : normal2;

            idx[k++] = indexOffset + 0;
            idx[k++] = indexOffset + 2;
            idx[k++] = indexOffset + 4;

            idx[k++] = indexOffset + 2;
            idx[k++] = indexOffset + 6;
            idx[k++] = indexOffset + 4;

            idx[k++] = indexOffset + 1;
            idx[k++] = indexOffset + 5;
            idx[k++] = indexOffset + 3;

            idx[k++] = indexOffset + 3;
            idx[k++] = indexOffset + 5;
            idx[k++] = indexOffset + 7;
            indexOffset += 8;
            v += 8;
        }

        return new FoliageMesh
        {
            Positions = pos,
            Normals = nrm,
            TexCoords = tcs,
            Indices = idx,
            Texture = sp.Texture,
            AlphaCutoff = 0.5f,         // LLDrawPoolGrass::renderDeferred setMinimumAlpha(0.5)
            ShadowAlphaCutoff = 0.598f, // pipeline.cpp shadow "alpha grass": ALPHA_BLEND_CUTOFF
        };
    }

    /// <summary>
    /// The blade count the viewer settles on for a still camera (LLVOGrass::updateLOD from a fresh object, which
    /// starts at 32): n = clamp(trunc(scale.x * scale.y / distance * 5), 1, 32), then 32 if n > 16, else the
    /// largest power of two not above n. <paramref name="distance"/> is from the camera to the object's position.
    /// Returns 0 when RenderTreeLODFactor is effectively zero (the viewer stops drawing grass with trees).
    /// </summary>
    public static int ChooseBladeCount(Vector3 scale, float distance, float treeLodFactor = 0.5f)
    {
        if (treeLodFactor < FoliageMath.AlmostZero) // LLVOTree::isTreeRenderingStopped
            return 0;
        float d = FoliageMath.Round(distance, 0.01f); // mDistanceWRTCamera
        float tanAngle = (scale.X * scale.Y) / d;
        int num = (int)Math.Clamp(tanAngle * 5, int.MinValue, int.MaxValue);
        num = Math.Clamp(num, 1, GrassBladeTable.MaxBlades);
        int blades = GrassBladeTable.MaxBlades;
        if (num <= blades >> 1)
            while (blades > num)
                blades >>= 1;
        return blades;
    }
}
