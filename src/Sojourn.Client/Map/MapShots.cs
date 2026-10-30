using LibreMetaverse;
using SkiaSharp;
using Sojourn.Client.Assistant;

namespace Sojourn.Client.Map;

/// <summary>`--panel-shots`: the world map (close up, a few regions, far out, with help and something picked), the
/// mini-map (on the hand, out on its own with a friend picked, on the desktop) and the location line (plain, everything blocked with damage, each meter state) over the world screenshot in each look. Tiles
/// from SOJOURN_MAP_SERVER (default the local grid's, http://127.0.0.1:9700/), the four Atelier regions marked as a
/// session would.</summary>
public static class MapShots
{
    public static void Write(string dir, SKBitmap? bg, float scale)
    {
        using var tiles = new MapTiles(() => Environment.GetEnvironmentVariable("SOJOURN_MAP_SERVER") ?? "http://127.0.0.1:9700/");
        double ox = 1000 * 256, oy = 1000 * 256;
        var regions = new List<MapRegion>
        {
            new(Utils.UIntsToLong(1000 * 256, 1000 * 256), ox, oy, "Atelier", "moderate", 256),
            new(Utils.UIntsToLong(1001 * 256, 1000 * 256), ox + 256, oy, "Atelier East", "moderate", 256),
            new(Utils.UIntsToLong(1000 * 256, 1001 * 256), ox, oy + 256, "Atelier North", "moderate", 256),
            new(Utils.UIntsToLong(1001 * 256, 1001 * 256), ox + 256, oy + 256, "Atelier Northeast", "moderate", 256),
        };
        var marks = new List<MapMark>
        {
            new(MarkKind.Person, ox + 140, oy + 120), new(MarkKind.Person, ox + 150, oy + 112), new(MarkKind.Person, ox + 60, oy + 200),
            new(MarkKind.Friend, ox + 118, oy + 150, "Mira Lindqvist"),
            new(MarkKind.Crowd, ox + 256 + 128, oy + 256 + 128, Count: 7),
            new(MarkKind.LandForSale, ox + 256 + 60, oy + 80, "Lakeside lot", Price: 1200, Area: 512),
            new(MarkKind.Event, ox + 40, oy + 256 + 200, "Live music at the pier"),
        };
        var me = (ox + 128.0, oy + 128.0, 25f, 0.6f);
        var views = new (string Name, MapView View)[]
        {
            ("close", new MapView(ox + 128, oy + 128, 0.25f, true, false, true, false, -1, null)),
            ("near", new MapView(ox + 200, oy + 200, 1f, false, true, true, false, (int)MapKey.In, null)),
            ("far", new MapView(ox + 256, oy + 256, 24f, false, true, true, false, -1, null)),
            ("help", new MapView(ox + 200, oy + 200, 1f, true, false, true, true, (int)MapKey.Help, null)),
            ("picked", new MapView(ox + 200, oy + 200, 1f, false, true, true, false, (int)MapKey.Go,
                new MapPick(ox + 256 + 60, oy + 80, regions[1], marks[5]))),
            // the mini-map: on the hand, out on its own with something picked, on the desktop
            ("mini-hand", new MapView(ox + 128, oy + 128, 0.5f, true, false, true, false, (int)MapKey.PopOut, null, MapDock.Hand, Width: 420)),
            ("mini-free", new MapView(ox + 150, oy + 140, 0.5f, false, false, true, false, (int)MapKey.PutBack,
                new MapPick(ox + 118, oy + 150, regions[0], marks[3]), MapDock.Free)),
            ("mini-desktop", new MapView(ox + 128, oy + 128, 1f, true, false, true, false, -1, null, MapDock.Desktop)),
        };
        // let the tiles come: ask, then wait a moment for them
        foreach (var (_, v) in views)
            for (int i = 0; i < 2; i++)
                MapPainter.Board(v, new MapPainter.Scene(regions, marks, me, null, true, false), tiles, new PanelLook(), scale, false);
        for (int i = 0; i < 150 && tiles.Count < 6; i++) Thread.Sleep(100);
        Thread.Sleep(500);
        Console.WriteLine($"map shots: {tiles.Count} tiles{(tiles.LastError is { } err ? $", last failure {err}" : "")}");

        var stats = new Stats(88, 90, 42, 0, 0.99f, 45, 45, 11, 2, 1, 1, 1, 0, 3, 2, 5400, 120, 300, 40);
        foreach (var look in PanelLook.Presets)
        {
            bool all = look.Preset == new PanelLook().Preset;
            foreach (var (name, v) in views)
            {
                if (!all && name != "near") continue;
                var scene = new MapPainter.Scene(regions, marks, me, name == "near" ? "region 45 fps · time 0.99 · ping 42 ms · 3 people · you 88 fps" : null, true,
                    name == "picked", WorldUp: name == "mini-free");
                var (px, w, h, _) = MapPainter.Board(v, scene, tiles, look, scale, false);
                Save(dir, $"map-{name}-{look.Preset}.png", px, w, h, bg);
            }
            var plain = new Location("Atelier", 0, new Vector3(128, 128, 25), "moderate", "The Atelier gardens", 1, false, false, false, false, false, false, 100, false, null);
            var everything = new Location("Atelier East", 0, new Vector3(30, 30, 25), "adult", "Arena of the Long Name That Runs On", 2, true, true, true, true, true, true, 87, true, 1200);
            foreach (var (name, l, st) in new[]
            {
                ("plain", plain, stats),
                ("everything", everything, stats with { Fps = 50, PingMs = 350 }),
                ("lag", plain, stats with { Fps = 40, PingMs = 700, Dilation = 0.6f, FrameMs = 60 }),
                ("general", plain with { Rating = "general", Parcel = null }, (Stats?)null),
            })
            {
                if (!all && name != "plain") continue;
                var (px, w, h, _) = MapPainter.Line(l, st, look, scale, false, false);
                Save(dir, $"line-{name}-{look.Preset}.png", px, w, h, bg);
            }
        }
    }

    static void Save(string dir, string file, byte[] px, int w, int h, SKBitmap? bg)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w + 60, h + 60, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        if (bg != null) c.DrawBitmap(bg, new SKRect(0, 0, w + 60, h + 60));
        else c.Clear(new SKColor(90, 100, 110));
        using var img = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(px, 0, img.GetPixels(), px.Length);
        c.DrawBitmap(img, 30, 30);
        PanelShots.Save(surface, Path.Combine(dir, file));
    }
}
