using SkiaSharp;
using Sojourn.Client.Assistant;
using static Sojourn.Session.Assistant.Panel;

namespace Sojourn.Client.Map;

/// <summary>What a board shows: its middle in global metres (x east, y north), how many metres a logical pixel spans,
/// whether it follows you, the layers on, help open, the key pointed at, what's picked, and which board it is and where
/// (the keys and layout follow from that). Width: on the hand, the line's width it takes (logical px; see MapLayout.Hand).</summary>
public sealed record MapView(double CX, double CY, float Mpp, bool Follow, bool Sale, bool Events, bool Help, int Hover, MapPick? Picked,
    MapDock Dock = MapDock.World, float Width = 0);

/// <summary>A point picked on the map: where, its region (if the map knows it), and the thing there if any.</summary>
public sealed record MapPick(double X, double Y, MapRegion? Region, MapMark? Mark);

public enum MapKey { None = -1, Out, In, Me, Sale, Events, Help, Close, Go, Track, World, PopOut, PutBack }

/// <summary>Which board: the world map, or the mini-map on the hand, out as a board of its own, or on the desktop.</summary>
public enum MapDock { World, Hand, Free, Desktop }

/// <summary>A board's logical layout: its width, the key row above the map, the map, and below it the strip (the world
/// map's: what's picked, or statistics) or, with none (the mini-map), a margin, what's picked then going on a band over
/// the map's foot.</summary>
public sealed record MapLayout(float W, float Top, float MapH, float StripH, float Side)
{
    public static readonly MapLayout World = new(624, 48, 440, 56, 12);
    /// <summary>The mini-map out on its own or on the desktop: its map near square.</summary>
    public static readonly MapLayout Mini = new(272, 46, 216, 0, 10);

    /// <summary>On the hand: a fixed HandMin wide and HandH tall, about a typical location line's width (the line is drawn
    /// at least that wide while it's there). Not the line's own width: following it from parcel to parcel felt wrong
    /// (the user, 2026-10-29).</summary>
    public const float HandMin = 380, HandMapH = 250;
    public static float HandH => 46 + HandMapH + 10;

    static MapLayout? _hand;

    public static MapLayout Hand(float w)
    {
        w = HandMin;
        var h = _hand;
        if (h == null || h.W != w) _hand = h = new MapLayout(w, 46, HandMapH, 0, 10);
        return h;
    }

    public static MapLayout Of(MapView v) => v.Dock switch { MapDock.World => World, MapDock.Hand => Hand(v.Width), _ => Mini };

    public float H => Top + MapH + (StripH > 0 ? StripH : Side);
    public SKRect MapRect => new(Side, Top, W - Side, Top + MapH);
    /// <summary>The mini-map's band over the map's foot while something's picked.</summary>
    public SKRect Band => new(Side + 6, Top + MapH - 46, W - Side - 6, Top + MapH - 6);
}

/// <summary>
/// Draws the map board and the location line in the panel's look (Assistant/PanelPainter.cs), so they read as the same
/// system as the panel, the HUD board and the reader: the body's glass, the quiet round keys that light when pointed at,
/// ink and dim and nothing in the accent. The map itself is the grid's tiles north up, regions named once they're big
/// enough to read (the viewer's 96 pixels a region), you an arrow the way you face with the chat (20 m) and shout (100
/// m) rings close up (Firestorm's mini-map), people as white dots, friends ringed and named, crowds counted, land for
/// sale and events marked when their keys are on. Glyphs rather than words on the keys; the legend is behind ?.
/// </summary>
public static class MapPainter
{
    // the world map's layout (MapLayout.World), as tests and the assistant's API know it
    public static float W => MapLayout.World.W;
    public static float H => MapLayout.World.H;
    public static SKRect MapRect => MapLayout.World.MapRect;
    const float LineH = 36;

    static readonly SKColor Water = new(0x26, 0x47, 0x5a);

    /// <summary>A key of the world map.</summary>
    public static SKRect KeyRect(MapKey k) => KeyRect(k, MapLayout.World, MapDock.World);

    public static SKRect KeyRect(MapKey k, MapView v) => KeyRect(k, MapLayout.Of(v), v.Dock);

    static SKRect KeyRect(MapKey k, MapLayout L, MapDock dock)
    {
        float W = L.W, Side = L.Side, Top = L.Top, MapH = L.MapH;
        SKRect Slot(int fromRight) => new(W - Side - 32 - fromRight * 38, 8, W - Side - fromRight * 38, 40);
        if (dock == MapDock.World)
            return k switch
            {
                MapKey.Out => new SKRect(Side, 8, Side + 32, 40),
                MapKey.In => new SKRect(Side + 38, 8, Side + 70, 40),
                MapKey.Me => new SKRect(Side + 76, 8, Side + 108, 40),
                MapKey.Sale => new SKRect(Side + 126, 8, Side + 158, 40),
                MapKey.Events => new SKRect(Side + 164, 8, Side + 196, 40),
                MapKey.Help => new SKRect(W - Side - 72, 8, W - Side - 40, 40),
                MapKey.Close => Slot(0),
                MapKey.Go => new SKRect(W - Side - 76, Top + MapH + 10, W - Side, Top + MapH + 46),
                MapKey.Track => new SKRect(W - Side - 76 - 8 - 76, Top + MapH + 10, W - Side - 76 - 8, Top + MapH + 46),
                _ => SKRect.Empty,
            };
        // the mini-map: zoom and find me on the left; on the right the world map, then (on the hand) out to a board of
        // its own, or (out) back to the hand and ×; Go and Track on the band over the map's foot
        var band = L.Band;
        var go = new SKRect(band.Right - 4 - 52, band.Top + 4, band.Right - 4, band.Bottom - 4);
        return k switch
        {
            MapKey.Out => new SKRect(Side, 8, Side + 32, 40),
            MapKey.In => new SKRect(Side + 38, 8, Side + 70, 40),
            MapKey.Me => new SKRect(Side + 76, 8, Side + 108, 40),
            MapKey.World => Slot(dock switch { MapDock.Hand => 1, MapDock.Free => 2, _ => 0 }),
            MapKey.PopOut when dock == MapDock.Hand => Slot(0),
            MapKey.PutBack when dock == MapDock.Free => Slot(1),
            MapKey.Close when dock == MapDock.Free => Slot(0),
            MapKey.Go => go,
            MapKey.Track => new SKRect(go.Left - 6 - 62, go.Top, go.Left - 6, go.Bottom),
            _ => SKRect.Empty,
        };
    }

    /// <summary>The keys there are this time (Go and Track only with something picked; Track only with a tracker).</summary>
    public static IEnumerable<MapKey> Keys(MapView v, bool canTrack)
    {
        var keys = v.Dock switch
        {
            MapDock.World => new[] { MapKey.Out, MapKey.In, MapKey.Me, MapKey.Sale, MapKey.Events, MapKey.Help, MapKey.Close },
            MapDock.Hand => new[] { MapKey.Out, MapKey.In, MapKey.Me, MapKey.World, MapKey.PopOut },
            MapDock.Free => new[] { MapKey.Out, MapKey.In, MapKey.Me, MapKey.World, MapKey.PutBack, MapKey.Close },
            _ => new[] { MapKey.Out, MapKey.In, MapKey.Me, MapKey.World },
        };
        foreach (var k in keys) yield return k;
        if (v.Picked is { Region: not null })
        {
            yield return MapKey.Go;
            if (canTrack) yield return MapKey.Track;
        }
    }

    /// <summary>A logical pixel of the map area in global metres.</summary>
    public static (double X, double Y) ToGlobal(MapView v, float px, float py)
    {
        var m = MapLayout.Of(v).MapRect;
        return (v.CX + (px - m.MidX) * v.Mpp, v.CY - (py - m.MidY) * v.Mpp);
    }

    public static (float X, float Y) ToBoard(MapView v, double gx, double gy)
    {
        var m = MapLayout.Of(v).MapRect;
        return ((float)(m.MidX + (gx - v.CX) / v.Mpp), (float)(m.MidY - (gy - v.CY) / v.Mpp));
    }

    /// <summary>What's drawn besides the view: regions, marks, you, the statistics line (the world map's strip), whether
    /// there's a tracker and it's on, and (for the mini-map's key) whether the world map is up.</summary>
    public sealed record Scene(IReadOnlyList<MapRegion> Regions, IReadOnlyList<MapMark> Marks, (double X, double Y, float Z, float Heading)? Me,
        string? Line, bool CanTrack, bool Tracking, bool WorldUp = false);

    public static (byte[] Px, int Width, int Height, float Scale) Board(MapView v, Scene s, MapTiles tiles, PanelLook look, float scale, bool layer)
    {
        var k = Ink.For(look);
        var L = MapLayout.Of(v);
        var (px, w, h, used) = Rasterise(scale, L.W, new List<PanelKey>(), (c, height) =>
        {
            if (c == null) return L.H;
            var draw = new Painter(c, k, L.W);
            draw.Body(L.H);
            DrawMap(c, k, v, s, tiles, scale);
            if (L.StripH == 0 && v.Picked != null) DrawBand(draw, v, s);
            foreach (var key in Keys(v, s.CanTrack)) DrawKey(draw, key, v, s);
            if (L.StripH > 0) DrawStrip(draw, v, s);
            if (v.Help) DrawHelp(draw);
            return L.H;
        }, Panel.MaxRasterWidth);
        GlassLight.Finish(px, look, layer);
        return (px, w, h, used);
    }

    // --- the map ----------------------------------------------------------------------------------------------------

    static void DrawMap(SKCanvas c, Ink k, MapView v, Scene s, MapTiles tiles, float scale)
    {
        var m = MapLayout.Of(v).MapRect;
        float rad = 10;
        c.Save();
        using (var clip = new SKPath())
        {
            clip.AddRoundRect(new SKRoundRect(m, rad, rad));
            c.ClipPath(clip, antialias: true);
        }
        using (var water = new SKPaint { Color = Water })
            c.DrawRect(m, water);

        // tiles at the level that shows them at about their own pixels on this raster
        float ppr = 256f / v.Mpp;   // logical pixels a region
        int level = MapTiles.LevelFor(ppr * scale);
        int step = 1 << (level - 1);
        var (w0, n0) = ToGlobal(v, m.Left, m.Top);
        var (e0, s0) = ToGlobal(v, m.Right, m.Bottom);
        int gx0 = (int)Math.Floor(w0 / 256), gx1 = (int)Math.Floor(e0 / 256), gy0 = (int)Math.Floor(s0 / 256), gy1 = (int)Math.Floor(n0 / 256);
        gx0 = Math.Max(0, gx0 - gx0 % step); gy0 = Math.Max(0, gy0 - gy0 % step);
        using var paint = new SKPaint { IsAntialias = false };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        var (mx, my) = ((m.Left + m.Right) / 2, (m.Top + m.Bottom) / 2);
        for (int tx = gx0; tx <= gx1; tx += step)
            for (int ty = gy0; ty <= gy1; ty += step)
            {
                var (l, t) = ToBoard(v, tx * 256.0, (ty + step) * 256.0);
                var (r, b) = ToBoard(v, (tx + step) * 256.0, ty * 256.0);
                var dest = new SKRect(l, t, r, b);
                float pri = Math.Clamp(MathF.Sqrt(Sq(dest.MidX - mx) + Sq(dest.MidY - my)) / m.Width, 0, 1);
                if (tiles.Get(level, tx, ty, pri) is { } img)
                {
                    c.DrawImage(img, new SKRect(0, 0, img.Width, img.Height), dest, sampling, paint);
                    continue;
                }
                // a coarser tile's part while this one comes
                for (int cl = level + 1; cl <= MapTiles.Levels; cl++)
                {
                    var (cx, cy) = MapTiles.TileOf(tx, ty, cl);
                    if (tiles.Get(cl, cx, cy, 1) is not { } coarse) continue;
                    int cs = 1 << (cl - 1);
                    float u0 = (tx - cx) / (float)cs, v1 = 1 - (ty - cy) / (float)cs;
                    float u1 = u0 + step / (float)cs, v0 = v1 - step / (float)cs;
                    c.DrawImage(coarse, new SKRect(u0 * coarse.Width, v0 * coarse.Height, u1 * coarse.Width, v1 * coarse.Height), dest, sampling, paint);
                    break;
                }
            }

        // regions' edges and names, close enough to read
        if (ppr >= 48)
            using (var edge = new SKPaint { Color = SKColors.White.WithAlpha(40), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke })
                foreach (var r in s.Regions)
                {
                    var (l, t) = ToBoard(v, r.X, r.Y + r.Size);
                    var (rr, bb) = ToBoard(v, r.X + r.Size, r.Y);
                    if (rr < m.Left || l > m.Right || bb < m.Top || t > m.Bottom) continue;
                    c.DrawRect(new SKRect(l, t, rr, bb), edge);
                }
        if (ppr >= 96)
        {
            var f = Font(k.Semi, 12.5f);
            foreach (var r in s.Regions)
            {
                var (l, t) = ToBoard(v, r.X, r.Y + r.Size);
                if (l > m.Right || t > m.Bottom || l + r.Size / v.Mpp < m.Left || t + r.Size / v.Mpp < m.Top) continue;
                Halo(c, r.Name, Math.Max(l, m.Left) + 6, Math.Max(t, m.Top) + 16, f, SKColors.White.WithAlpha(230));
            }
        }

        // chat and shout ranges around you, close up
        if (s.Me is { } me)
        {
            var (x, y) = ToBoard(v, me.X, me.Y);
            using var ring = new SKPaint { Color = SKColors.White.WithAlpha(110), StrokeWidth = 1.2f, IsAntialias = true, Style = SKPaintStyle.Stroke };
            if (20 / v.Mpp >= 14) c.DrawCircle(x, y, 20 / v.Mpp, ring);
            ring.Color = SKColors.White.WithAlpha(60);
            if (100 / v.Mpp >= 14 && 100 / v.Mpp < m.Width * 2) c.DrawCircle(x, y, 100 / v.Mpp, ring);
        }

        // marks: items under people, friends over them, you over all
        var named = Font(k.Semi, 12);
        foreach (var mk in s.Marks.OrderBy(x => x.Kind switch { MarkKind.Friend => 3, MarkKind.Person => 2, MarkKind.Crowd => 1, _ => 0 }))
        {
            if (mk.Kind == MarkKind.LandForSale && !v.Sale || mk.Kind == MarkKind.Event && !v.Events || mk.Kind == MarkKind.Telehub) continue;
            var (x, y) = ToBoard(v, mk.X, mk.Y);
            if (x < m.Left - 20 || x > m.Right + 20 || y < m.Top - 20 || y > m.Bottom + 20) continue;
            switch (mk.Kind)
            {
                case MarkKind.Person: Dot(c, x, y, 3.2f); break;
                case MarkKind.Friend:
                    Dot(c, x, y, 3.6f);
                    using (var r = new SKPaint { Color = SKColors.White, StrokeWidth = 1.6f, IsAntialias = true, Style = SKPaintStyle.Stroke }) c.DrawCircle(x, y, 7, r);
                    if (mk.Name is { } n) Halo(c, First(n), x + 10, y + 4, named, SKColors.White);
                    break;
                case MarkKind.Crowd:
                    float rr = 3 + MathF.Min(6, MathF.Sqrt(mk.Count) * 1.2f);
                    Dot(c, x, y, rr);
                    if (mk.Count > 1 && ppr >= 24) Halo(c, mk.Count.ToString(), x + rr + 3, y + 4, named, SKColors.White);
                    break;
                case MarkKind.LandForSale: Tag(c, x, y); break;
                case MarkKind.Event: Star(c, x, y, 6.5f); break;
            }
        }
        if (v.Picked is { } p)
        {
            var (x, y) = ToBoard(v, p.X, p.Y);
            using var pin = new SKPaint { Color = SKColors.White, StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var dark = new SKPaint { Color = SKColors.Black.WithAlpha(120), StrokeWidth = 4, IsAntialias = true, Style = SKPaintStyle.Stroke };
            c.DrawCircle(x, y, 9, dark);
            c.DrawCircle(x, y, 9, pin);
            c.DrawLine(x, y - 15, x, y - 9, pin);
            c.DrawLine(x, y + 9, x, y + 15, pin);
            c.DrawLine(x - 15, y, x - 9, y, pin);
            c.DrawLine(x + 9, y, x + 15, y, pin);
        }
        if (s.Me is { } at)
        {
            var (x, y) = ToBoard(v, at.X, at.Y);
            Arrow(c, x, y, at.Heading, 9);
        }
        // a faint inner edge so the map sits in the glass
        using (var inner = new SKPaint { Color = SKColors.Black.WithAlpha(50), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke })
            c.DrawRoundRect(new SKRoundRect(new SKRect(m.Left + 0.5f, m.Top + 0.5f, m.Right - 0.5f, m.Bottom - 0.5f), rad, rad), inner);
        c.Restore();
    }

    static string First(string name) => name.Split(' ')[0];
    static float Sq(float x) => x * x;

    /// <summary>Light text with a soft dark halo, readable on any tile.</summary>
    static void Halo(SKCanvas c, string text, float x, float y, SKFont font, SKColor color)
    {
        using (var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(150), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.6f) })
            FallbackText.Draw(c, text, x, y, font, shadow);
        using var p = new SKPaint { Color = color, IsAntialias = true };
        FallbackText.Draw(c, text, x, y, font, p);
    }

    static void Dot(SKCanvas c, float x, float y, float r)
    {
        using var dark = new SKPaint { Color = SKColors.Black.WithAlpha(140), IsAntialias = true };
        c.DrawCircle(x, y, r + 1.3f, dark);
        using var p = new SKPaint { Color = SKColors.White, IsAntialias = true };
        c.DrawCircle(x, y, r, p);
    }

    /// <summary>You: an arrow the way you face (heading: radians from east, counter-clockwise; the board's y is down).</summary>
    static void Arrow(SKCanvas c, float x, float y, float heading, float r)
    {
        float dx = MathF.Cos(heading), dy = -MathF.Sin(heading);
        SKPoint P(float along, float side) => new(x + dx * along - dy * side, y + dy * along + dx * side);
        using var path = new SKPath();
        path.MoveTo(P(r * 1.3f, 0));
        path.LineTo(P(-r * 0.8f, r * 0.85f));
        path.LineTo(P(-r * 0.35f, 0));
        path.LineTo(P(-r * 0.8f, -r * 0.85f));
        path.Close();
        using var dark = new SKPaint { Color = SKColors.Black.WithAlpha(150), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
        c.DrawPath(path, dark);
        using var p = new SKPaint { Color = SKColors.White, IsAntialias = true };
        c.DrawPath(path, p);
    }

    static void Tag(SKCanvas c, float x, float y)
    {
        using var path = new SKPath();
        path.MoveTo(x - 6, y - 5);
        path.LineTo(x + 3, y - 5);
        path.LineTo(x + 8, y);
        path.LineTo(x + 3, y + 5);
        path.LineTo(x - 6, y + 5);
        path.Close();
        using var dark = new SKPaint { Color = SKColors.Black.WithAlpha(140), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
        c.DrawPath(path, dark);
        using var p = new SKPaint { Color = new SKColor(0xf4, 0xe6, 0xb0), IsAntialias = true };
        c.DrawPath(path, p);
        using var hole = new SKPaint { Color = SKColors.Black.WithAlpha(150), IsAntialias = true };
        c.DrawCircle(x - 2.5f, y, 1.4f, hole);
    }

    static SKPath StarPath(float x, float y, float r)
    {
        var path = new SKPath();
        for (int i = 0; i < 10; i++)
        {
            float a = -MathF.PI / 2 + i * MathF.PI / 5, rr = i % 2 == 0 ? r : r * 0.45f;
            var pt = new SKPoint(x + MathF.Cos(a) * rr, y + MathF.Sin(a) * rr);
            if (i == 0) path.MoveTo(pt); else path.LineTo(pt);
        }
        path.Close();
        return path;
    }

    static void Star(SKCanvas c, float x, float y, float r)
    {
        using var path = StarPath(x, y, r);
        using var dark = new SKPaint { Color = SKColors.Black.WithAlpha(140), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
        c.DrawPath(path, dark);
        using var p = new SKPaint { Color = new SKColor(0xd8, 0xee, 0xff), IsAntialias = true };
        c.DrawPath(path, p);
    }

    // --- keys and the strip ------------------------------------------------------------------------------------------

    static void DrawKey(Painter draw, MapKey key, MapView v, Scene s)
    {
        var r = KeyRect(key, v);
        bool lit = v.Hover == (int)key;
        switch (key)
        {
            case MapKey.Close: draw.GlyphKey(r, Painter.Glyph.Close, lit); return;
            case MapKey.Help: draw.GlyphKey(r, Painter.Glyph.Help, lit, v.Help); return;
            case MapKey.Go: draw.Key(r, Lang.T("Go"), lit, v.Dock == MapDock.World ? 16 : 15); return;
            // a word beside Go, as Go is (the user, 2026-10-30: like keys all words or all icons); lit while tracking
            case MapKey.Track: draw.Key(r, Lang.T("Track"), lit, v.Dock == MapDock.World ? 16 : 15, s.Tracking && !lit ? 1.4f : 1); return;
        }
        bool on = key switch { MapKey.Me => v.Follow, MapKey.Sale => v.Sale, MapKey.Events => v.Events, MapKey.Track => s.Tracking, MapKey.World => s.WorldUp, _ => false };
        var c = draw.Canvas!;
        var k = draw.K;
        if (lit || on) draw.KeyFace(r, r.Height / 2, lit, on && !lit ? 1.4f : 1);
        var ink = lit || on ? k.Text : k.Dim;
        using var p = new SKPaint { Color = ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.7f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        float cx = r.MidX, cy = r.MidY, sz = r.Height * 0.2f;
        switch (key)
        {
            case MapKey.Out:
                c.DrawLine(cx - sz, cy, cx + sz, cy, p);
                break;
            case MapKey.In:
                c.DrawLine(cx - sz, cy, cx + sz, cy, p);
                c.DrawLine(cx, cy - sz, cx, cy + sz, p);
                break;
            case MapKey.Me:
                c.DrawCircle(cx, cy, sz * 0.95f, p);
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    c.DrawLine(cx + dx * sz * 1.25f, cy + dy * sz * 1.25f, cx + dx * sz * 1.8f, cy + dy * sz * 1.8f, p);
                using (var fill = new SKPaint { Color = ink, IsAntialias = true }) c.DrawCircle(cx, cy, 1.8f, fill);
                break;
            case MapKey.Sale:
                using (var path = new SKPath())
                {
                    path.MoveTo(cx - sz * 1.3f, cy - sz);
                    path.LineTo(cx + sz * 0.5f, cy - sz);
                    path.LineTo(cx + sz * 1.4f, cy);
                    path.LineTo(cx + sz * 0.5f, cy + sz);
                    path.LineTo(cx - sz * 1.3f, cy + sz);
                    path.Close();
                    c.DrawPath(path, p);
                }
                c.DrawCircle(cx - sz * 0.55f, cy, 1.4f, p);
                break;
            case MapKey.Events:
                using (var star = StarPath(cx, cy + 0.5f, sz * 1.5f)) c.DrawPath(star, p);
                break;
            case MapKey.World:
                // a globe: its rim, a meridian and two parallels
                {
                    float gr = sz * 1.45f;
                    c.DrawCircle(cx, cy, gr, p);
                    c.DrawOval(new SKRect(cx - gr * 0.42f, cy - gr, cx + gr * 0.42f, cy + gr), p);
                    float py = gr * 0.45f, px = MathF.Sqrt(gr * gr - py * py);
                    c.DrawLine(cx - px, cy - py, cx + px, cy - py, p);
                    c.DrawLine(cx - px, cy + py, cx + px, cy + py, p);
                }
                break;
            case MapKey.PopOut:
            case MapKey.PutBack:
                // a frame open at its top right, an arrow out of it (out to a board of its own) or into it (back to
                // the hand)
                {
                    float b = sz * 1.35f;
                    using (var frame = new SKPath())
                    {
                        frame.MoveTo(cx + b * 0.1f, cy - b);
                        frame.LineTo(cx - b, cy - b);
                        frame.LineTo(cx - b, cy + b);
                        frame.LineTo(cx + b, cy + b);
                        frame.LineTo(cx + b, cy - b * 0.1f);
                        c.DrawPath(frame, p);
                    }
                    bool outward = key == MapKey.PopOut;
                    var tail = outward ? new SKPoint(cx - b * 0.15f, cy + b * 0.15f) : new SKPoint(cx + b * 1.15f, cy - b * 1.15f);
                    var head = outward ? new SKPoint(cx + b * 1.15f, cy - b * 1.15f) : new SKPoint(cx - b * 0.15f, cy + b * 0.15f);
                    c.DrawLine(tail, head, p);
                    float hs = b * 0.6f;
                    // the head's two strokes along the arrow's axes (it runs along a diagonal)
                    float sx = outward ? -1 : 1, sy = outward ? 1 : -1;
                    c.DrawLine(head.X, head.Y, head.X + sx * hs, head.Y, p);
                    c.DrawLine(head.X, head.Y, head.X, head.Y + sy * hs, p);
                }
                break;
        }
    }

    /// <summary>The mini-map's band over the map's foot: what's picked, its pin and Go.</summary>
    static void DrawBand(Painter draw, MapView v, Scene s)
    {
        if (v.Picked is not { } p) return;
        var c = draw.Canvas!;
        var k = draw.K;
        var band = MapLayout.Of(v).Band;
        using (var glass = new SKPaint { Color = A(k.Tint, MathF.Max(k.Look.TintOpacity, 0.88f)), IsAntialias = true })
            c.DrawRoundRect(band, 9, 9, glass);
        using (var rim = new SKPaint { Color = SKColors.White.WithAlpha(60), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            c.DrawRoundRect(new SKRect(band.Left + 0.5f, band.Top + 0.5f, band.Right - 0.5f, band.Bottom - 0.5f), 9, 9, rim);
        var (title, detail) = Picked(p);
        float right = (p.Region != null ? KeyRect(s.CanTrack ? MapKey.Track : MapKey.Go, v).Left : band.Right) - 8, x = band.Left + 10;
        var big = Font(k.Semi, 14);
        var small = Font(k.Regular, 12);
        draw.Text(Ellipsize(title, big, right - x), x, band.Top + 17, big, k.Text);
        draw.Text(Ellipsize(detail, small, right - x), x, band.Top + 33, small, k.Dim);
    }

    /// <summary>What's picked, as a title and a line under it.</summary>
    static (string Title, string Detail) Picked(MapPick p)
    {
        if (p.Mark is { } mk)
            return (mk.Kind switch
            {
                MarkKind.Crowd => mk.Count == 1 ? Lang.T("1 person") : Lang.F("{0} people", mk.Count),
                MarkKind.Person => p.Region?.Name ?? Lang.T("someone"),
                _ => mk.Name is { Length: > 0 } n ? n : p.Region?.Name ?? "",
            }, mk.Kind switch
            {
                MarkKind.LandForSale => Lang.F("L${0} · {1} m²", mk.Price.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
                    mk.Area.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)) + Place(p),
                _ => Place(p).TrimStart(' ', '·'),
            });
        return (p.Region?.Name ?? Lang.T("no region here"), p.Region is { } r ? $"{(int)(p.X - r.X)}, {(int)(p.Y - r.Y)} · {Lang.T(r.Rating, "maturity")}" : "");
    }

    /// <summary>What's picked (the place and what's there), or, with nothing picked, the region in the middle and the
    /// line the caller gives (statistics at a glance).</summary>
    static void DrawStrip(Painter draw, MapView v, Scene s)
    {
        var k = draw.K;
        var L = MapLayout.World;
        float Side = L.Side;
        float y0 = L.Top + L.MapH, right = v.Picked is { Region: not null } ? KeyRect(s.CanTrack ? MapKey.Track : MapKey.Go).Left - 10 : L.W - Side;
        var big = Font(k.Semi, 15.5f);
        var small = Font(k.Regular, 13);
        if (v.Picked is { } p)
        {
            var (title, detail) = Picked(p);
            draw.Text(Ellipsize(title, big, right - Side - 4), Side + 4, y0 + 24, big, k.Text);
            draw.Text(Ellipsize(detail, small, right - Side - 4), Side + 4, y0 + 42, small, k.Dim);
            return;
        }
        var (gx, gy) = (v.CX, v.CY);
        var here = s.Regions.FirstOrDefault(r => gx >= r.X && gx < r.X + r.Size && gy >= r.Y && gy < r.Y + r.Size);
        if (here.Name is { Length: > 0 }) draw.Text(Ellipsize(here.Name, big, right - Side - 4), Side + 4, y0 + 24, big, k.Text);
        if (s.Line is { Length: > 0 } line) draw.Text(Ellipsize(line, small, right - Side - 4), Side + 4, y0 + 42, small, k.Dim);
    }

    static string Place(MapPick p) => p.Region is { } r ? $" · {r.Name} {(int)(p.X - r.X)}, {(int)(p.Y - r.Y)}" : "";

    static void DrawHelp(Painter draw)
    {
        var c = draw.Canvas!;
        var k = draw.K;
        var m = MapRect;
        var box = new SKRect(m.Left + 16, m.Top + 16, m.Left + 330, m.Top + 16 + 9 * 26 + 14);
        // in the map's own colours, so the marks look as they do on it
        using (var shade = new SKPaint { Color = Water.WithAlpha(240), IsAntialias = true }) c.DrawRoundRect(box, 10, 10, shade);
        var f = Font(k.Regular, 14);
        float x = box.Left + 44, y = box.Top + 26;
        void Row(Action<float, float> icon, string words)
        {
            icon(box.Left + 22, y - 5);
            draw.Text(Lang.T(words), x, y, f, SKColors.White);
            y += 26;
        }
        Row((ix, iy) => Arrow(c, ix, iy, MathF.PI / 2, 7), "you, the way you face");
        Row((ix, iy) => Dot(c, ix, iy, 3.2f), "someone nearby");
        Row((ix, iy) => { Dot(c, ix, iy, 3.6f); using var r = new SKPaint { Color = SKColors.White, StrokeWidth = 1.6f, IsAntialias = true, Style = SKPaintStyle.Stroke }; c.DrawCircle(ix, iy, 7, r); }, "a friend");
        Row((ix, iy) => Dot(c, ix, iy, 6), "people in a region, counted");
        Row((ix, iy) => Tag(c, ix, iy), "land for sale (its key)");
        Row((ix, iy) => Star(c, ix, iy, 6.5f), "an event (its key)");
        Row((ix, iy) => { using var r = new SKPaint { Color = SKColors.White.WithAlpha(150), StrokeWidth = 1.2f, IsAntialias = true, Style = SKPaintStyle.Stroke }; c.DrawCircle(ix, iy, 8, r); }, "chat reaches 20 m, a shout 100 m");
        Row((_, _) => { }, "drag to move, press a spot to pick it");
        Row((_, _) => { }, "Go takes you there");
    }

    // --- the location line --------------------------------------------------------------------------------------------

    /// <summary>The line's icons: what's blocked where you are, damage with your health, and the meter. minW: at least
    /// this wide (logical px; the mini-map on the hand over it), the meter then at the right end.</summary>
    public static (byte[] Px, int Width, int Height, float Scale) Line(Location l, Stats? st, PanelLook look, float scale, bool layer, bool lit,
        float minW = 0)
    {
        var k = Ink.For(look);
        var name = Font(k.Semi, 14.5f);
        var dim = Font(k.Regular, 13.5f);
        string place = l.Parcel is { Length: > 0 } p ? p : l.Region;
        string region = l.Parcel is { Length: > 0 } ? l.Region : "";
        var icons = Icons(l).ToList();
        float textW = MathF.Min(220, Measure(name, place)) + (region.Length > 0 ? 8 + MathF.Min(150, Measure(dim, region)) : 0);
        float w = MathF.Ceiling(14 + 22 + 8 + textW + 6 + icons.Count * 20 + (l.Damage ? Measure(dim, $"{l.Health}%") + 4 : 0) + (st != null ? 26 : 0) + 14);
        w = MathF.Max(w, MathF.Ceiling(minW));
        var (px, pw, ph, used) = Rasterise(scale, w, new List<PanelKey>(), (c, height) =>
        {
            if (c == null) return LineH;
            var draw = new Painter(c, k, w);
            draw.Body(LineH);
            if (lit)
                using (var ring = new SKPaint { Color = A(k.Text, 0.45f), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f })
                    c.DrawRoundRect(new SKRect(2, 2, w - 2, LineH - 2), LineH / 2 - 2, LineH / 2 - 2, ring);
            float x = 14;
            Rating(draw, x, l.Rating);
            x += 22 + 8;
            float avail = textW - (region.Length > 0 ? 8 + MathF.Min(150, Measure(dim, region)) : 0);
            var shown = Ellipsize(place, name, avail);
            draw.Text(shown, x, LineH / 2 + 5, name, k.Text);
            x += Measure(name, shown) + 8;
            if (region.Length > 0)
            {
                var r = Ellipsize(region, dim, 150);
                draw.Text(r, x, LineH / 2 + 5, dim, k.Dim);
                x += Measure(dim, r);
            }
            x += 6;
            foreach (var ic in icons)
            {
                Icon(c, k, ic, x + 9, LineH / 2);
                x += 20;
            }
            if (l.Damage)
            {
                draw.Text($"{l.Health}%", x, LineH / 2 + 5, dim, k.Text);
                x += Measure(dim, $"{l.Health}%") + 4;
            }
            if (st != null) Meter(c, k, MathF.Max(x, w - 40) + 4, LineH / 2, st);
            return LineH;
        }, Panel.MaxRasterWidth);
        GlassLight.Finish(px, look, layer);
        return (px, pw, ph, used);
    }

    public enum LineIcon { NoFly, NoBuild, NoPush, NoVoice, NoScripts, Damage, HiddenAvatars, ForSale }

    static IEnumerable<LineIcon> Icons(Location l)
    {
        if (l.ForSale != null) yield return LineIcon.ForSale;
        if (l.NoVoice) yield return LineIcon.NoVoice;
        if (l.NoFly) yield return LineIcon.NoFly;
        if (l.NoPush) yield return LineIcon.NoPush;
        if (l.NoBuild) yield return LineIcon.NoBuild;
        if (l.NoScripts) yield return LineIcon.NoScripts;
        if (l.HidesAvatars) yield return LineIcon.HiddenAvatars;
        if (l.Damage) yield return LineIcon.Damage;
    }

    static void Rating(Painter draw, float x, string rating)
    {
        var k = draw.K;
        var r = new SKRect(x, LineH / 2 - 9, x + 22, LineH / 2 + 9);
        draw.Recess(r, 5);
        string letter = rating switch { "general" => "G", "moderate" => "M", "adult" => "A", _ => "?" };
        var f = Font(k.Semi, 12.5f);
        draw.Text(letter, r.MidX - Measure(f, letter) / 2, r.MidY + 4.5f, f, k.Text);
    }

    /// <summary>One icon in a 16-pixel square around (cx, cy); the "no" ones struck through.</summary>
    internal static void Icon(SKCanvas c, Ink k, LineIcon ic, float cx, float cy)
    {
        using var p = new SKPaint { Color = k.Text, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        float s = 6;
        switch (ic)
        {
            case LineIcon.NoFly:
                // a bird in flight, as a child draws a gull
                using (var bird = new SKPath())
                {
                    bird.MoveTo(cx - s * 1.1f, cy - s * 0.1f);
                    bird.QuadTo(cx - s * 0.55f, cy - s * 0.85f, cx, cy + s * 0.25f);
                    bird.QuadTo(cx + s * 0.55f, cy - s * 0.85f, cx + s * 1.1f, cy - s * 0.1f);
                    c.DrawPath(bird, p);
                }
                break;
            case LineIcon.NoBuild:
                using (var cube = new SKPath())
                {
                    cube.MoveTo(cx, cy - s); cube.LineTo(cx + s, cy - s * 0.5f); cube.LineTo(cx + s, cy + s * 0.6f); cube.LineTo(cx, cy + s * 1.1f);
                    cube.LineTo(cx - s, cy + s * 0.6f); cube.LineTo(cx - s, cy - s * 0.5f); cube.Close();
                    cube.MoveTo(cx - s, cy - s * 0.5f); cube.LineTo(cx, cy); cube.LineTo(cx + s, cy - s * 0.5f);
                    cube.MoveTo(cx, cy); cube.LineTo(cx, cy + s * 1.1f);
                    c.DrawPath(cube, p);
                }
                break;
            case LineIcon.NoPush:
                c.DrawLine(cx - s, cy, cx + s * 0.4f, cy, p);
                c.DrawLine(cx, cy - s * 0.45f, cx + s * 0.45f, cy, p);
                c.DrawLine(cx, cy + s * 0.45f, cx + s * 0.45f, cy, p);
                c.DrawLine(cx + s, cy - s, cx + s, cy + s, p);
                break;
            case LineIcon.NoVoice:
                c.DrawRoundRect(new SKRect(cx - 2.6f, cy - s, cx + 2.6f, cy + 1.5f), 2.6f, 2.6f, p);
                using (var arc = new SKPath()) { arc.AddArc(new SKRect(cx - 5, cy - 4, cx + 5, cy + 4), 0, 180); c.DrawPath(arc, p); }
                c.DrawLine(cx, cy + 4, cx, cy + s, p);
                break;
            case LineIcon.NoScripts:
                using (var b = new SKPath())
                {
                    b.MoveTo(cx - 2, cy - s); b.QuadTo(cx - 4.5f, cy - s, cx - 4.5f, cy - 2.5f); b.LineTo(cx - 6, cy); b.LineTo(cx - 4.5f, cy + 2.5f);
                    b.QuadTo(cx - 4.5f, cy + s, cx - 2, cy + s);
                    b.MoveTo(cx + 2, cy - s); b.QuadTo(cx + 4.5f, cy - s, cx + 4.5f, cy - 2.5f); b.LineTo(cx + 6, cy); b.LineTo(cx + 4.5f, cy + 2.5f);
                    b.QuadTo(cx + 4.5f, cy + s, cx + 2, cy + s);
                    c.DrawPath(b, p);
                }
                break;
            case LineIcon.HiddenAvatars:
                using (var eye = new SKPath())
                {
                    eye.MoveTo(cx - s, cy); eye.QuadTo(cx, cy - s * 1.2f, cx + s, cy); eye.QuadTo(cx, cy + s * 1.2f, cx - s, cy);
                    c.DrawPath(eye, p);
                    c.DrawCircle(cx, cy, 2, p);
                }
                break;
            case LineIcon.Damage:
                using (var heart = new SKPath())
                {
                    heart.MoveTo(cx, cy + s);
                    heart.CubicTo(cx - s * 1.6f, cy - s * 0.2f, cx - s * 0.6f, cy - s * 1.4f, cx, cy - s * 0.4f);
                    heart.CubicTo(cx + s * 0.6f, cy - s * 1.4f, cx + s * 1.6f, cy - s * 0.2f, cx, cy + s);
                    c.DrawPath(heart, p);
                }
                break;
            case LineIcon.ForSale:
                using (var tag = new SKPath())
                {
                    tag.MoveTo(cx - s, cy - s * 0.7f); tag.LineTo(cx + s * 0.3f, cy - s * 0.7f); tag.LineTo(cx + s, cy);
                    tag.LineTo(cx + s * 0.3f, cy + s * 0.7f); tag.LineTo(cx - s, cy + s * 0.7f); tag.Close();
                    c.DrawPath(tag, p);
                    c.DrawCircle(cx - s * 0.45f, cy, 1.2f, p);
                }
                break;
        }
        if (ic is LineIcon.NoFly or LineIcon.NoBuild or LineIcon.NoPush or LineIcon.NoVoice or LineIcon.NoScripts or LineIcon.HiddenAvatars)
        {
            // struck through
            c.DrawLine(cx - s * 1.1f, cy + s * 1.1f, cx + s * 1.1f, cy - s * 1.1f, p);
        }
    }

    /// <summary>The lag meter as three small bars (this computer, the connection, the region), full when fine.</summary>
    static void Meter(SKCanvas c, Ink k, float x, float cy, Stats st)
    {
        using var track = new SKPaint { Color = A(k.Text, 0.16f), IsAntialias = true };
        using var bar = new SKPaint { Color = k.Text, IsAntialias = true };
        int i = 0;
        foreach (int level in new[] { st.Client, st.Network, st.Server })
        {
            float bx = x + i * 6, h = 14, filled = h * (3 - level) / 3f;
            c.DrawRoundRect(new SKRect(bx, cy - h / 2, bx + 3.5f, cy + h / 2), 1.5f, 1.5f, track);
            c.DrawRoundRect(new SKRect(bx, cy + h / 2 - filled, bx + 3.5f, cy + h / 2), 1.5f, 1.5f, bar);
            i++;
        }
    }

    public static float LineHeight => LineH;
}
