using SkiaSharp;

namespace Sojourn.Core.Media;

/// <summary>
/// Targa pictures, which Skia doesn't read and the official viewer's image upload takes (LLImageTGA): truecolour (16,
/// 24 or 32 bits) and greyscale, plain or run-length encoded, either origin. Colour-mapped ones are refused with words.
/// Claude Opus 5.5, 2026-11-01.
/// </summary>
public static class Tga
{
    public static bool Is(string path) => Path.GetExtension(path).Equals(".tga", StringComparison.OrdinalIgnoreCase);

    /// <summary>Width and height from the header alone.</summary>
    public static (int W, int H) Size(byte[] b)
    {
        if (b.Length < 18) throw new MediaFormatException("Not a TGA picture: the file is too short.");
        return (b[12] | b[13] << 8, b[14] | b[15] << 8);
    }

    public static SKBitmap Decode(byte[] b)
    {
        if (b.Length < 18) throw new MediaFormatException("Not a TGA picture: the file is too short.");
        int idLen = b[0], mapType = b[1], type = b[2];
        int mapLen = b[5] | b[6] << 8, mapBits = b[7];
        var (w, h) = Size(b);
        int bits = b[16], desc = b[17];
        bool rle = type is 10 or 11;
        bool grey = type is 3 or 11;
        if (type is 1 or 9) throw new MediaFormatException("Colour-mapped TGA pictures aren't supported; save it as 24 or 32-bit TGA, or PNG.");
        if (type is not (2 or 3 or 10 or 11)) throw new MediaFormatException("Not a TGA picture this can read (an unknown image type).");
        if (w == 0 || h == 0) throw new MediaFormatException("The TGA picture has no pixels.");
        if (grey ? bits is not (8 or 16) : bits is not (16 or 24 or 32))
            throw new MediaFormatException($"{bits}-bit TGA pictures aren't supported; save it as 24 or 32-bit.");
        int bpp = bits / 8;
        int at = 18 + idLen + (mapType == 1 ? mapLen * ((mapBits + 7) / 8) : 0);
        bool alpha = !grey ? bits == 32 || bits == 16 && (desc & 0x0f) > 0 : bits == 16;
        var px = new uint[w * h];   // BGRA as Skia's Bgra8888 holds it, little-endian
        int n = 0;
        uint Read(ref int i)
        {
            if (i + bpp > b.Length) throw new MediaFormatException("The TGA picture is cut short.");
            uint r, g, bl, a = 255;
            if (grey)
            {
                r = g = bl = b[i];
                if (bpp == 2) a = b[i + 1];
            }
            else if (bpp == 2)
            {
                int v = b[i] | b[i + 1] << 8;
                bl = (uint)((v & 0x1f) * 255 / 31);
                g = (uint)((v >> 5 & 0x1f) * 255 / 31);
                r = (uint)((v >> 10 & 0x1f) * 255 / 31);
                if (alpha) a = (v & 0x8000) != 0 ? 255u : 0u;
            }
            else
            {
                bl = b[i]; g = b[i + 1]; r = b[i + 2];
                if (bpp == 4) a = b[i + 3];
            }
            i += bpp;
            return bl | g << 8 | r << 16 | a << 24;
        }
        int p = at;
        while (n < px.Length)
        {
            if (!rle) { px[n++] = Read(ref p); continue; }
            if (p >= b.Length) throw new MediaFormatException("The TGA picture is cut short.");
            int head = b[p++];
            int count = (head & 0x7f) + 1;
            if ((head & 0x80) != 0)
            {
                uint v = Read(ref p);
                for (int k = 0; k < count && n < px.Length; k++) px[n++] = v;
            }
            else
                for (int k = 0; k < count && n < px.Length; k++) px[n++] = Read(ref p);
        }
        bool bottomUp = (desc & 0x20) == 0, rightToLeft = (desc & 0x10) != 0;
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, alpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque));
        var dst = new int[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            dst[y * w + x] = (int)px[(bottomUp ? h - 1 - y : y) * w + (rightToLeft ? w - 1 - x : x)];
        System.Runtime.InteropServices.Marshal.Copy(dst, 0, bmp.GetPixels(), dst.Length);
        return bmp;
    }
}
