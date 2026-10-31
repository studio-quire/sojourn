using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Render.Realtime;
using Sojourn.Session.Assistant;
using static Sojourn.Session.Assistant.Panel;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Control;

/// <summary>
/// The picture just taken by hand, shown for a few seconds as a phone's camera shows what it took (the user's ask,
/// 2026-10-30): a sheet in the panel's look holding the picture, just above the panel it was taken through, where the
/// eyes are as the shutter goes. Left there in the room (lowering the hands to look at it doesn't take it away), turned
/// to face you, half the panel's size (its longest side 9-20 cm), then gone; a new picture takes its place. No spark (it's
/// the camera's, not the assistant's) and no keys: it takes no ray or trigger. The saved file is decoded and scaled
/// once, off the frame thread, to about 25 px a degree; in the headset it's a quad layer with its frost in the eyes,
/// among the client's boards (Map/MapWiring.HeadsetScene). Hidden, not stopped, while a confirmation waits or the
/// interface is hidden. Claude Opus 5.5, 2026-10-30.
/// </summary>
public sealed class ShutterReview
{
    public const long ShowMs = 3000;
    public const string Key = "camera picture";
    const int Longest = 640;   // the picture's longest side on the sheet, pixels
    const float Pad = 8;       // the glass round it, logical px
    const float Gap = 0.025f, MinSide = 0.09f, MaxSide = 0.2f;

    sealed record Ready(OverlayImage Image, Vector3 At, float WidthM);

    Ready? _ready;     // painted, not yet shown (any thread)
    Ready? _showing;   // main thread
    long _shownAt;
    long _version;

    /// <summary>The picture saved at path, taken through a panel (room space: its centre, up, half width and height):
    /// decoded, scaled and painted here, so call it off the frame thread.</summary>
    public void Taken(string path, Vector3 centre, Vector3 up, float halfW, float halfH, PanelLook look)
    {
        using var full = SKBitmap.Decode(path);
        if (full == null || full.Width < 2 || full.Height < 2) return;
        float k = MathF.Min(1, (float)Longest / Math.Max(full.Width, full.Height));
        int tw = Math.Max(1, (int)MathF.Round(full.Width * k)), th = Math.Max(1, (int)MathF.Round(full.Height * k));
        using var small = full.Resize(new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (small == null) return;
        var (image, cardW, cardH, picLongest) = Paint(small, look, GlassLight.Layers, Interlocked.Increment(ref _version));
        // in metres: the picture half the panel's longest side, the sheet's bottom a little above the panel's top
        float side = Math.Clamp(MathF.Max(halfW, halfH), MinSide, MaxSide);
        float mpp = side / picLongest;
        var at = centre + up * (halfH + Gap + cardH * mpp / 2);
        Volatile.Write(ref _ready, new Ready(image, at, cardW * mpp));
    }

    /// <summary>The sheet's layer this frame (room pose, width in metres), or null; its few seconds run from its first
    /// frame. Main thread.</summary>
    public (string Key, OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width)? Layer(Vector3 head, bool hidden)
    {
        long now = Environment.TickCount64;
        if (Interlocked.Exchange(ref _ready, null) is { } r) (_showing, _shownAt) = (r, now);
        if (_showing is not { } s) return null;
        if (now - _shownAt > ShowMs) { _showing = null; return null; }
        if (hidden) return null;
        return (Key, s.Image, s.At, PanelPlacement.Facing(head, s.At), s.WidthM);
    }

    /// <summary>The picture on a sheet of the look, a little glass round it, its corners following the sheet's; sizes
    /// back in logical px (the sheet's, and the picture's longest side).</summary>
    static (OverlayImage Image, float W, float H, float PicLongest) Paint(SKBitmap photo, PanelLook look, bool layer, long version)
    {
        var ink = Ink.For(look);
        float scale = UiSize.Raster;
        float pw = photo.Width / scale, ph = photo.Height / scale;
        float W = pw + 2 * Pad, H = ph + 2 * Pad;
        var (px, w, h, _) = Rasterise(scale, W, new List<PanelKey>(), (c, height) =>
        {
            if (c == null) return H;
            new Painter(c, ink, W).Body(H);
            var at = new SKRect(Pad, Pad, Pad + pw, Pad + ph);
            float rad = MathF.Max(3, MathF.Min(look.Radius, H / 2) - Pad);
            c.Save();
            c.ClipRoundRect(new SKRoundRect(at, rad), antialias: true);
            using var img = SKImage.FromBitmap(photo);
            c.DrawImage(img, at, new SKSamplingOptions(SKFilterMode.Linear));
            c.Restore();
            return H;
        }, MaxRasterWidth);
        GlassLight.Finish(px, look, layer);
        return (new OverlayImage(px, w, h, version, look.Frost, look.TintOpacity), W, H, MathF.Max(pw, ph));
    }
}
