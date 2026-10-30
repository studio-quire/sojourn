using System.Runtime.InteropServices;
using Sojourn.Render.Frame;
using SkiaSharp;

namespace Sojourn.Render;

/// <summary>PNG/JPEG encoding of RGBA8 frames (SkiaSharp), plus thumbnails.</summary>
public static class ImageCodec
{
    static SKBitmap Wrap(byte[] rgba, int w, int h, out GCHandle pin)
    {
        pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        var bmp = new SKBitmap();
        bmp.InstallPixels(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque), pin.AddrOfPinnedObject(), w * 4);
        return bmp;
    }

    /// <summary>PNG at zlib level 3: about half the encode time of Skia's default for a few percent more bytes
    /// (tools re-encode the shot anyway).</summary>
    public static byte[] EncodePng(byte[] rgba, int w, int h)
    {
        using var bmp = Wrap(rgba, w, h, out var pin);
        try
        {
            using var pixmap = bmp.PeekPixels();
            using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 3));
            return data.ToArray();
        }
        finally { pin.Free(); }
    }

    public static byte[] EncodeJpeg(byte[] rgba, int w, int h, int quality) => Encode(rgba, w, h, SKEncodedImageFormat.Jpeg, quality);

    static byte[] Encode(byte[] rgba, int w, int h, SKEncodedImageFormat fmt, int quality)
    {
        using var bmp = Wrap(rgba, w, h, out var pin);
        try
        {
            using var data = bmp.Encode(fmt, quality);
            return data.ToArray();
        }
        finally { pin.Free(); }
    }

    /// <summary>Downscale an encoded image to a JPEG thumbnail of the given width.</summary>
    public static byte[] Thumbnail(byte[] encoded, int width, int quality = 82)
    {
        using var src = SKBitmap.Decode(encoded);
        int h = Math.Max(1, (int)Math.Round(src.Height * (double)width / src.Width));
        using var dst = src.Resize(new SKImageInfo(width, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var data = dst.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }
}

/// <summary>What the viewer draws over the world with UI on that matters to a resident: avatar name tags.</summary>
public static class Overlay
{
    public static void DrawNameTags(byte[] rgba, int w, int h, List<NameTag> tags, ViewSetup view)
    {
        var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            using var bmp = new SKBitmap();
            bmp.InstallPixels(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), pin.AddrOfPinnedObject(), w * 4);
            using var canvas = new SKCanvas(bmp);
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal), 15);
            using var text = new SKPaint { IsAntialias = true };
            using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 110), IsAntialias = true };
            var vp = view.View * view.Proj;
            foreach (var tag in tags.OrderByDescending(t => Vector3.DistanceSquared(t.Position, view.Position)))
            {
                var clip = Vector4.Transform(new Vector4(tag.Position, 1), vp);
                if (clip.W <= 0.1f) continue;
                float x = (clip.X / clip.W * 0.5f + 0.5f) * w, y = (clip.Y / clip.W * 0.5f + 0.5f) * h;
                if (x < -100 || x > w + 100 || y < -30 || y > h + 30) continue;
                float tw = font.MeasureText(tag.Text);
                var rect = new SKRect(x - tw / 2 - 6, y - 19, x + tw / 2 + 6, y + 3);
                canvas.DrawRoundRect(rect, 5, 5, bg);
                text.Color = new SKColor(tag.Color);
                canvas.DrawText(tag.Text, x - tw / 2, y - 3, font, text);
            }
            canvas.Flush();
        }
        finally { pin.Free(); }
    }
}
