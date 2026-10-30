using System.Numerics;
using LibreMetaverse;

namespace Sojourn.Render.Particles;

/// <summary>The region's wind lattice as the viewer reads it (llwind.cpp LLWind::getVelocity, LGPL 2.1, Linden
/// Research, Inc.): 16x16 cells sent by the simulator in the wind layer, bilinear inside, scaled by
/// WIND_SCALE_HACK. Before any wind arrives the viewer's lattice holds 0.5 m/s east and north in every cell.</summary>
public static class RegionWind
{
    const float ScaleHack = 2f;
    const int Size = 16;

    public static Vector3 At(Simulator? sim, Vector3 posRegion)
    {
        var cells = sim?.WindSpeeds;
        float width = sim?.SizeX > 0 ? sim.SizeX : 256f;
        if (cells == null || cells.Length < Size * Size || AllZero(cells)) return new Vector3(0.5f, 0.5f, 0) * ScaleHack;
        float x = posRegion.X, y = posRegion.Y;
        if (x < 0) x = 0; else if (x >= width) x %= width;
        if (y < 0) y = 0; else if (y >= width) y %= width;
        float fx = x * Size / width, fy = y * Size / width;
        int i = (int)MathF.Floor(fx), j = (int)MathF.Floor(fy);
        float dx = fx - i, dy = fy - j;
        int k = i + j * Size;
        Vector2 C(int n) => new(cells[n].X, cells[n].Y);
        Vector2 v;
        if (i < Size - 1 && j < Size - 1)
            v = C(k) * (1 - dx) * (1 - dy) + C(k + 1) * dx * (1 - dy) + C(k + Size) * dy * (1 - dx) + C(k + Size + 1) * dx * dy;
        else v = C(k);
        return new Vector3(v.X, v.Y, 0) * ScaleHack;
    }

    static bool AllZero(LibreMetaverse.Vector2[] cells)
    {
        foreach (var c in cells) if (c.X != 0 || c.Y != 0) return false;
        return true;
    }
}
