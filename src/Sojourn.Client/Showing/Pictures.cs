using SkiaSharp;
using Sojourn.Core.Leap;
using Sojourn.Session.Assistant;

namespace Sojourn.Client.Showing;

/// <summary>
/// The pictures Sojourn has saved (Pictures\Sojourn: snapshots, the shutter's, 360s) as an image block's source: which
/// file the words mean ("last", a file name, a path in the folder; nothing outside it), its size from the header, and
/// a decode at about a card's pixels. Whether one may be shown now (incognito) is <see cref="Shown"/>'s call.
/// Claude Opus 5.5, 2026-10-31, at the user's request (a picture just taken, seen in the headset).
/// </summary>
public static class Pictures
{
    static readonly string[] Kinds = { ".png", ".jpg", ".jpeg" };

    /// <summary>The most a picture is decoded to on its long side: a picture card's raster at the headset's density is
    /// at most 1152 wide (Panel.MaxRasterWidth).</summary>
    public const int DecodeSide = 1280;

    public static bool IsLast(string words) => words.Trim().ToLowerInvariant() is "last" or "latest" or "newest" or "the last" or "last picture";

    /// <summary>Taken in incognito (or named private): what the assistant's file tools never read
    /// (harness.unseen_globs, `*incognito*` and `*private*`).</summary>
    public static bool Unseen(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains("incognito", StringComparison.OrdinalIgnoreCase) || name.Contains("private", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The file words name: "last" (the newest picture), a file name, or a path, in the folder itself.</summary>
    public static string Resolve(string words)
    {
        var dir = Path.GetFullPath(CaptureApi.PicturesDir);
        if (IsLast(words))
        {
            var newest = Directory.Exists(dir)
                ? new DirectoryInfo(dir).EnumerateFiles().Where(f => Kinds.Contains(f.Extension.ToLowerInvariant())).MaxBy(f => f.LastWriteTimeUtc)
                : null;
            return newest?.FullName ?? throw new LeapError("no pictures saved yet (Pictures\\Sojourn is empty)");
        }
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(words) ? words : Path.Combine(dir, words)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new LeapError("image: not a file name"); }
        if (!string.Equals(Path.GetDirectoryName(full), dir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new LeapError("image: only pictures Sojourn saved, in Pictures\\Sojourn itself");
        if (!Kinds.Contains(Path.GetExtension(full).ToLowerInvariant()))
        {
            // a name without its extension
            var found = Kinds.Select(k => full + k).FirstOrDefault(File.Exists);
            full = found ?? throw new LeapError("image: a .png or .jpg in Pictures\\Sojourn");
        }
        if (!File.Exists(full)) throw new LeapError($"no picture called {Path.GetFileName(full)} in Pictures\\Sojourn");
        return full;
    }

    /// <summary>Its size, from the header only.</summary>
    public static (int W, int H) SizeOf(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0) throw new LeapError($"{Path.GetFileName(path)} isn't a picture that can be read");
        return (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>The picture at most <see cref="DecodeSide"/> on its long side (JPEGs decoded smaller where the codec
    /// can, then resampled); null if it can't be read.</summary>
    public static SKImage? Decode(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec == null) return null;
        var (w, h) = (codec.Info.Width, codec.Info.Height);
        float k = Math.Min(1f, (float)DecodeSide / Math.Max(w, h));
        var native = codec.GetScaledDimensions(k);
        if (native.Width < w * k || native.Height < h * k) native = new SKSizeI(w, h);
        using var raw = new SKBitmap(new SKImageInfo(native.Width, native.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var r = codec.GetPixels(raw.Info, raw.GetPixels());
        if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
        int tw = Math.Max(1, (int)MathF.Round(w * k)), th = Math.Max(1, (int)MathF.Round(h * k));
        if (raw.Width == tw && raw.Height == th) return SKImage.FromBitmap(raw);
        using var scaled = raw.Resize(new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return scaled == null ? null : SKImage.FromBitmap(scaled);
    }
}
