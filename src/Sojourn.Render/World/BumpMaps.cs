using LibreMetaverse;

namespace Sojourn.Render.World;

/// <summary>
/// The viewer's legacy bump maps: the standard presets (app_settings/std_bump.ini, codes 3-17) and the normal maps
/// it derives from them (LLBumpImageList::generateNormalMapFromAlpha) or, for brightness and darkness (codes 1 and
/// 2), from the face's diffuse texture (normgenF.glsl). Ported from newview/lldrawpoolbump.cpp (LGPL 2.1, Linden
/// Research, Inc.). Images are rows bottom-up as the decoders give them, which is the viewer's order too.
/// </summary>
public static class BumpMaps
{
    /// <summary>RenderNormalMapScale.</summary>
    const float NormScale = 64f;

    static readonly UUID[] Presets =
    {
        new("058c75c0-a0d5-f2f8-43f3-e9699a89c2fc"),   // 3 woodgrain
        new("6c9fa78a-1c69-2168-325b-3e03ffa348ce"),   // 4 bark
        new("b8eed5f0-64b7-6e12-b67f-43fa8e773440"),   // 5 bricks
        new("9deab416-9c63-78d6-d558-9a156f12044c"),   // 6 checker
        new("db9d39ec-a896-c287-1ced-64566217021e"),   // 7 concrete
        new("f2d7b6f6-4200-1e9a-fd5b-96459e950f94"),   // 8 crustytile
        new("d9258671-868f-7511-c321-7baef9e948a4"),   // 9 cutstone
        new("d21e44ca-ff1c-a96e-b2ef-c0753426b7d9"),   // 10 discs
        new("4726f13e-bd07-f2fb-feb0-bfa2ac58ab61"),   // 11 gravel
        new("e569711a-27c2-aad4-9246-0c910239a179"),   // 12 petridish
        new("073c9723-540c-5449-cdd4-0e87fdc159e3"),   // 13 siding
        new("ae874d1a-93ef-54fb-5fd3-eb0cb156afc0"),   // 14 stonetile
        new("92e66e00-f56f-598a-7997-048aa64cde18"),   // 15 stucco
        new("83b77fc6-10b4-63ec-4de7-f40629f238c5"),   // 16 suction
        new("735198cf-6ea0-2550-e222-21d3c6a341ae"),   // 17 weave
    };

    /// <summary>The texture a bump code's normal map comes from, or null for none/unknown codes (18-31 are ignored).</summary>
    public static UUID? Source(int code, UUID diffuse) => code switch
    {
        1 or 2 => diffuse,
        >= 3 and <= 17 => Presets[code - 3],
        _ => null,
    };

    /// <summary>generateNormalMapFromAlpha: height = the image's last channel (luminance, alpha, or blue for RGB),
    /// in bytes; RGB = the encoded normal, A = the height.</summary>
    public static byte[] FromHeight(byte[] rgba, int w, int h, int components)
    {
        int ch = components switch { 1 => 0, 3 => 2, _ => 3 };
        var dst = new byte[w * h * 4];
        for (int j = 0; j < h; j++)
        {
            int up = (j + h - 1) % h * w, down = (j + 1) % h * w, row = j * w;
            for (int i = 0; i < w; i++)
            {
                int l = (i + w - 1) % w, r = (i + 1) % w;
                // the four cross products of the viewer reduce to (h[x-1]-h[x+1], h[y-1]-h[y+1], 2*scale)
                float nx = rgba[(row + l) * 4 + ch] - rgba[(row + r) * 4 + ch];
                float ny = rgba[(up + i) * 4 + ch] - rgba[(down + i) * 4 + ch];
                Encode(dst, (row + i) * 4, nx, ny, 2f * NormScale, truncate: true);
                dst[(row + i) * 4 + 3] = rgba[(row + i) * 4 + ch];
            }
        }
        return dst;
    }

    /// <summary>normgenF: height = luminance of the sRGB-encoded diffuse (inverted for darkness), differences times
    /// 512; written to an RGBA8 target (rounded), A = the height.</summary>
    public static byte[] FromBrightness(byte[] rgba, int w, int h, bool darkness)
    {
        var lum = new float[w * h];
        for (int p = 0; p < lum.Length; p++)
        {
            float l = (rgba[p * 4] * 0.2995f + rgba[p * 4 + 1] * 0.5875f + rgba[p * 4 + 2] * 0.1145f) / 255f;
            lum[p] = darkness ? 1f - l : l;
        }
        var dst = new byte[w * h * 4];
        const float Scaler = 512f;
        for (int j = 0; j < h; j++)
        {
            int up = (j + h - 1) % h * w, down = (j + 1) % h * w, row = j * w;
            for (int i = 0; i < w; i++)
            {
                int l = (i + w - 1) % w, r = (i + 1) % w;
                float nx = (lum[row + l] - lum[row + r]) * Scaler;
                float ny = (lum[up + i] - lum[down + i]) * Scaler;
                Encode(dst, (row + i) * 4, nx, ny, 2f * NormScale, truncate: false);
                dst[(row + i) * 4 + 3] = (byte)MathF.Round(Math.Clamp(lum[row + i], 0f, 1f) * 255f);
            }
        }
        return dst;
    }

    static void Encode(byte[] dst, int at, float x, float y, float z, bool truncate)
    {
        float inv = 1f / MathF.Sqrt(x * x + y * y + z * z);
        float bx = (x * inv * 0.5f + 0.5f) * 255f, by = (y * inv * 0.5f + 0.5f) * 255f, bz = (z * inv * 0.5f + 0.5f) * 255f;
        if (truncate) { dst[at] = (byte)bx; dst[at + 1] = (byte)by; dst[at + 2] = (byte)bz; }
        else { dst[at] = (byte)MathF.Round(bx); dst[at + 1] = (byte)MathF.Round(by); dst[at + 2] = (byte)MathF.Round(bz); }
    }
}
