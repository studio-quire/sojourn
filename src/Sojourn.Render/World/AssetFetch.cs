using System.Buffers.Binary;
using LibreMetaverse;

namespace Sojourn.Render.World;

public enum FetchStatus { Ok, NotFound, Retry, Unavailable }

/// <summary>What one ranged fetch got: the bytes from the asked-for offset, the file's whole size when the server said
/// (else -1), and whether these bytes reach its end.</summary>
public readonly record struct RangeFetch(FetchStatus Status, byte[]? Data, int Total, bool ToEnd, TimeSpan RetryAfter = default);

/// <summary>
/// A source that serves textures by byte range (the viewer's way: a J2C codestream holds its lower resolutions first,
/// so a prefix decodes at a lower level) and keeps what it got on disk, and whole meshes, each through its own
/// connection pool. <see cref="TextureStore"/> asks it for the bytes a texture's on-screen size needs.
/// </summary>
public interface IRangedAssetSource
{
    /// <summary>What's on disk for a texture: a prefix of it (or all of it), and whether it's all of it.</summary>
    (byte[]? Data, bool Complete) CachedTexture(UUID id);
    /// <summary>Bytes [offset, offset + length) of a texture (length -1: to its end).</summary>
    Task<RangeFetch> TextureRangeAsync(UUID id, int offset, int length, CancellationToken ct);
    /// <summary>Keep what's been fetched of a texture on disk.</summary>
    void StoreTexture(UUID id, byte[] data, bool complete);
    /// <summary>A whole mesh asset: from disk if it's there, else over the region's mesh capability through a
    /// connection pool of its own (not LibreMetaverse's download queue, which bakes, sounds and the rest share), and
    /// kept on disk.</summary>
    Task<RangeFetch> MeshAsync(UUID id, CancellationToken ct);
}

/// <summary>J2C sizes and headers as the viewer reckons them (llimage/llimagej2c.cpp, llimage.h; LGPL 2.1, Linden
/// Research, Inc.).</summary>
public static class J2c
{
    public const int MaxDiscard = 5, FirstPacketSize = 600;

    /// <summary>LLImageJ2C::calcDataSizeJ2C: the bytes a codestream needs to decode at a discard level (an estimate
    /// that assumes four 8-bit components and the uploader's 1:8 rate; 0 for unknown dimensions means 2048).</summary>
    public static int DataSize(int w, int h, int discard)
    {
        const float rate = 1f / 8f;
        int width = w > 0 ? w : 2048, height = h > 0 ? h : 2048;
        int maxDim = Math.Max(width, height);
        int blockArea = 64 * 64;
        int maxLayers = (int)Math.Max(MathF.Round(MathF.Log2(maxDim) - MathF.Log2(64)), 4);
        blockArea *= Math.Max(maxLayers, 1);
        long total = 2000 * 4 * 8;
        for (int layer = 0; layer <= maxLayers; layer++)
        {
            if (layer <= MaxDiscard - discard) total += (long)(blockArea * 4 * 8 * rate);
            blockArea *= 4;
        }
        return (int)Math.Min(int.MaxValue, total / 8 + FirstPacketSize);
    }

    /// <summary>The finest discard level a prefix of <paramref name="bytes"/> decodes well at.</summary>
    public static int LevelFor(int w, int h, int bytes, bool complete, int levels)
    {
        if (complete) return 0;
        for (int d = 0; d <= Math.Min(MaxDiscard, levels); d++)
            if (DataSize(w, h, d) <= bytes) return d;
        return -1;
    }

    /// <summary>The codestream's size, components and decomposition levels (SIZ and COD), if the bytes hold them.</summary>
    public static bool Header(ReadOnlySpan<byte> b, out int w, out int h, out int comps, out int levels)
    {
        w = h = comps = 0;
        levels = -1;
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0x4F) return false;
        int i = 2;
        while (i + 4 <= b.Length)
        {
            ushort marker = BinaryPrimitives.ReadUInt16BigEndian(b[i..]);
            if (marker == 0xFF90) break;   // the first tile: the main header is done
            int len = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
            if (i + 2 + len > b.Length) return false;
            var seg = b.Slice(i + 4, len - 2);
            if (marker == 0xFF51 && seg.Length >= 36)
            {
                int xs = (int)BinaryPrimitives.ReadUInt32BigEndian(seg[2..]), ys = (int)BinaryPrimitives.ReadUInt32BigEndian(seg[6..]);
                int xo = (int)BinaryPrimitives.ReadUInt32BigEndian(seg[10..]), yo = (int)BinaryPrimitives.ReadUInt32BigEndian(seg[14..]);
                w = xs - xo;
                h = ys - yo;
                comps = BinaryPrimitives.ReadUInt16BigEndian(seg[34..]);
            }
            else if (marker == 0xFF52 && seg.Length >= 6) levels = seg[5];
            i += 2 + len;
        }
        return w > 0 && h > 0 && levels >= 0;
    }

    /// <summary>The discard level a view wants for a texture of this size, from the screen area (pixels) it covers:
    /// each level down quarters the texels, so one per factor of four the texture's area exceeds the screen's.</summary>
    public static int WantedLevel(int w, int h, float area, int levels, int maxDimension)
    {
        if (w <= 0 || h <= 0) { w = h = 1024; }
        int floor = 0;
        while ((w >> floor) > maxDimension || (h >> floor) > maxDimension) floor++;
        if (float.IsPositiveInfinity(area)) return floor;
        double ratio = (double)w * h / Math.Max(1.0, area);
        int d = ratio <= 1 ? 0 : (int)Math.Floor(0.5 * Math.Log2(ratio));
        int cap = Math.Max(floor, levels >= 0 ? Math.Min(MaxDiscard, levels) : MaxDiscard);
        return Math.Clamp(d, floor, cap);
    }
}
