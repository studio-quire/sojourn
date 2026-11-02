using LibreMetaverse;
using Sojourn.Core.Agents;
using Sojourn.Core.World;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Session.Hud;

/// <summary>A pick at a point of HUD space (y, z), optionally among some prims only, on the HUDs as last placed.</summary>
public delegate PickHit? HudPicker(Vector2 yz, Func<Primitive, bool>? only = null);

/// <summary>
/// The person's HUDs as a renderer places, draws and picks them (docs/hud.md): the VR client's (Sojourn.Client/Hud/HudView,
/// whose screen is the window's or the headset board's) or the renderer out of sight's (Offscreen/OffscreenRenderer.Hud.cs,
/// a 16:9 screen drawn only when something asks). What SojournHud (<see cref="HudApi"/>) and Sojourn Access's HUD parts,
/// faces and buttons stand on.
/// </summary>
public interface IHudScreen
{
    /// <summary>Run <paramref name="f"/> where the HUDs are placed and picked (the client's main thread, the renderer out
    /// of sight's thread), with what's placed and a pick; <paramref name="fallback"/> when that can't be done (no renderer).</summary>
    Task<T> WithHudsAsync<T>(Func<HudsPlaced, HudPicker, T> f, T fallback, CancellationToken ct);

    /// <summary>A picture of the HUD screen (hud null) or of one HUD bigger, as <see cref="HudPicture.For"/> frames it:
    /// PNG bytes over a dark grey (or transparent), or null when it couldn't be drawn.</summary>
    Task<(HudPicture Picture, byte[] Png)?> PictureAsync(HudObject? hud, bool transparent, CancellationToken ct);

    /// <summary>A picture taken before, by its number, or the last (number null).</summary>
    HudPicture? Picture(int? number);

    /// <summary>Whether the HUDs show (the headset's board up, the desktop's drawn); false where they never do.</summary>
    bool Shown { get; }
    void Show(bool on);
}

/// <summary>A picture of a window of HUD space: its middle (y, z), half height, size in pixels, the screen's aspect and
/// region when taken.</summary>
public sealed record HudPicture(int Number, Vector2 Center, float HalfHeight, int Width, int Height, float Aspect, ulong Region)
{
    public float HalfWidth => HalfHeight * Width / Height;

    /// <summary>A pixel of the picture in HUD space (y, z).</summary>
    public Vector2 ToHud(float x, float y) =>
        new(Center.X - (2 * x / Width - 1) * HalfWidth, Center.Y - (2 * y / Height - 1) * HalfHeight);

    /// <summary>A box of HUD space as the picture's pixels (left, top, width, height), or null if it's outside.</summary>
    public (int X, int Y, int W, int H)? Rect(Vector3 min, Vector3 max)
    {
        float x0 = (Center.X - max.Y) / HalfWidth * 0.5f + 0.5f, x1 = (Center.X - min.Y) / HalfWidth * 0.5f + 0.5f;
        float y0 = (Center.Y - max.Z) / HalfHeight * 0.5f + 0.5f, y1 = (Center.Y - min.Z) / HalfHeight * 0.5f + 0.5f;
        x0 = Math.Clamp(x0, 0, 1); x1 = Math.Clamp(x1, 0, 1); y0 = Math.Clamp(y0, 0, 1); y1 = Math.Clamp(y1, 0, 1);
        if (x1 - x0 < 1e-4f || y1 - y0 < 1e-4f) return null;
        return ((int)MathF.Round(x0 * Width), (int)MathF.Round(y0 * Height), (int)MathF.Round((x1 - x0) * Width), (int)MathF.Round((y1 - y0) * Height));
    }

    /// <summary>The window a picture of the HUD screen (hud null: 1280 wide at the screen's aspect) or of one HUD shows:
    /// its box with a margin, sized so the long side is at most 1024 pixels and a small HUD comes out bigger than it
    /// shows.</summary>
    public static (Vector2 Center, float HalfHeight, int Width, int Height) For(HudObject? hud, float aspect)
    {
        if (hud is not { } one) return (Vector2.Zero, 0.5f, 1280, Math.Clamp((int)MathF.Round(1280 / aspect), 320, 1280));
        float my = 0.04f + (one.Max.Y - one.Min.Y) * 0.08f, mz = 0.04f + (one.Max.Z - one.Min.Z) * 0.08f;
        float bw = one.Max.Y - one.Min.Y + 2 * my, bh = one.Max.Z - one.Min.Z + 2 * mz;
        var center = new Vector2((one.Min.Y + one.Max.Y) / 2, (one.Min.Z + one.Max.Z) / 2);
        float ppu = Math.Min(1024 / Math.Max(bw, bh), 4000);
        int w = Math.Max(32, (int)MathF.Round(bw * ppu)), h = Math.Max(32, (int)MathF.Round(bh * ppu));
        return (center, bh / 2, w, h);
    }

    /// <summary>A shot's premultiplied sRGB over a dark grey (HUDs are made to sit over a scene), or kept transparent,
    /// as a PNG.</summary>
    public static byte[] Png(byte[] rgba, int w, int h, bool transparent)
    {
        if (transparent)
        {
            using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
            using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        const int grey = 46;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            for (int c = 0; c < 3; c++) rgba[i + c] = (byte)Math.Min(255, rgba[i + c] + grey * (255 - a) / 255);
            rgba[i + 3] = 255;
        }
        return Sojourn.Render.ImageCodec.EncodePng(rgba, w, h);
    }
}

/// <summary>What the HUD code shares: surfaces, picks on a part, which prims are the person's HUDs.</summary>
public static class Huds
{
    /// <summary>A pick's surface as ObjectGrab carries it (HUD space for a HUD: position, normal and binormal).</summary>
    public static TouchSurface Surface(PickHit h) => h.Surface is { } s
        ? new TouchSurface(s.Face, L(s.ST), L(s.UV), L(h.Point), L(s.Normal), L(s.Binormal))
        : TouchSurface.None with { Position = L(h.Point) };

    public static TouchPoint Point(PickHit h) => new(h.Region, h.LocalId, h.Id, Surface(h));

    static LibreMetaverse.Vector3 L(Vector3 v) => new(v.X, v.Y, v.Z);
    static LibreMetaverse.Vector2 L(Vector2 v) => new(v.X, v.Y);

    /// <summary>A pick on one part alone, at its middle or, failing that, a few points across it (a ring, a cut-out).</summary>
    public static PickHit? PickPart(HudPicker pick, HudPart part)
    {
        var lo = new Vector2(part.Min.Y, part.Min.Z);
        var hi = new Vector2(part.Max.Y, part.Max.Z);
        foreach (var f in new[] { (0.5f, 0.5f), (0.3f, 0.3f), (0.7f, 0.3f), (0.3f, 0.7f), (0.7f, 0.7f), (0.5f, 0.2f), (0.5f, 0.8f), (0.2f, 0.5f), (0.8f, 0.5f) })
        {
            var at = new Vector2(lo.X + (hi.X - lo.X) * f.Item1, lo.Y + (hi.Y - lo.Y) * f.Item2);
            if (pick(at, p => p.ID == part.Id) is { } h) return h;
        }
        return null;
    }

    /// <summary>A part of a placed HUD by prim id, and the HUD it belongs to.</summary>
    public static (HudObject Hud, HudPart Part)? PartOf(HudsPlaced placed, UUID prim)
    {
        foreach (var h in placed.Objects)
            foreach (var p in h.Parts)
                if (p.Id == prim) return (h, p);
        return null;
    }

    /// <summary>Whether a prim is part of something the person wears on a HUD point (31-38); its root too.</summary>
    public static bool Mine(AgentSession s, UUID prim, out Primitive? root)
    {
        root = null;
        if (s.Client is not { } c || s.Region is not { } w || w.PrimById(prim) is not { } p) return false;
        if (!s.Objects.IsAttachment(w, p, out var wearer, out var r) || wearer?.ID != c.Self.AgentID) return false;
        root = r;
        return Render.Realtime.Hud.IsHudPoint((int)r.PrimData.AttachmentPoint);
    }

    /// <summary>What a press on a HUD prim does, as the viewer's: a touch when it or its root takes touches (click actions
    /// never apply to attachments).</summary>
    public static bool Touchable(AgentSession s, UUID prim)
    {
        if (s.Region is not { } w || w.PrimById(prim) is not { } p) return false;
        s.Objects.IsAttachment(w, p, out _, out var root);
        var flags = s.State.PrimFlagsOf(p) | (root != p ? s.State.PrimFlagsOf(root) : PrimFlags.None);
        return (flags & PrimFlags.Touch) != 0;
    }

    /// <summary>"the top left", "the middle", "the right" of a box, from a point's place across it (0-1 from the left
    /// and from the top), as on the screen.</summary>
    public static string Where(float across, float down)
    {
        string h = across < 0.34f ? "left" : across > 0.66f ? "right" : "";
        string v = down < 0.34f ? "top" : down > 0.66f ? "bottom" : "";
        return (v, h) switch
        {
            ("", "") => "the middle",
            ("", _) => $"the {h}",
            (_, "") => $"the {v}",
            _ => $"the {v} {h}",
        };
    }
}
