using LibreMetaverse;
using SkiaSharp;
using Sojourn.Client.Assistant;

namespace Sojourn.Client.Social;

/// <summary>
/// `--panel-shots`: the feed and name tags drawn over the world image like the panel's shots (social-*.png), and with
/// the Quest eye image (view.png) the feed placed as the headset places it beside an answer (view-social.png).
/// </summary>
public static class SocialShots
{
    static readonly UUID Mira = UUID.Random(), Kes = UUID.Random(), Ada = UUID.Random(), Me = UUID.Random();

    internal static List<FeedEntry> Sample()
    {
        int seq = 0;
        FeedEntry E(FeedKind k, string? session, string? sname, UUID from, string name, string text, FeedStyle style = FeedStyle.Say, bool mine = false) =>
            new(++seq, k, session, sname, from, name, text, style, mine, 0, 60);
        return new()
        {
            E(FeedKind.Nearby, null, null, Mira, "Mira Lane", "has anyone seen the new gallery on the east lawn?"),
            E(FeedKind.Nearby, null, null, Kes, "Kestrel", "waves", FeedStyle.Emote),
            E(FeedKind.Nearby, null, null, UUID.Zero, "Welcome Board", "Welcome to Atelier! Touch the board for a notecard.", FeedStyle.Object),
            E(FeedKind.Nearby, null, null, Me, "Sojourn Pilot", "hi everyone", mine: true),
            E(FeedKind.Im, "p2p-ada", "Ada", Ada, "Ada", "are you coming to the jazz set tonight?"),
            E(FeedKind.Im, "p2p-ada", "Ada", Me, "Sojourn Pilot", "yes, see you there", mine: true),
            E(FeedKind.Group, "g-builders", "Builders' Guild", Kes, "Kestrel", "sandbox resets in 10 minutes"),
            E(FeedKind.Notice, null, null, Ada, "Ada", "offers a teleport “come to the Pavilion”"),
        };
    }

    public static void Write(string dir, SKBitmap? bg, float scale)
    {
        foreach (var look in PanelLook.Presets)
        {
            var d = FeedPainter.Draw(look, Sample(), new List<(UUID, string)> { (Ada, "Ada") }, scale);
            if (d != null) PanelShots.ShotOver(dir, look.Preset == "glass" ? "social-feed" : $"social-feed-{look.Preset}", Raster(d), bg, scale);
        }
        // a script menu too long for its six lines: cut, then pointed at (lit, the close key showing), then opened
        var menu = Sample();
        menu.Add(new FeedEntry(99, FeedKind.Notice, null, null, UUID.Zero, "Pavilion Dance Ball",
            "Welcome to the Pavilion Dance Ball! Choose a dance to join the floor. Couples dances need a partner on the "
            + "other ball; solo dances start at once. Your animation stops when you stand up or choose Stop. Tips for the DJ "
            + "go in the jar by the stage, and requests can be sent in local chat. This ball refreshes its menu every hour, "
            + "and new dances arrive on Fridays.\nWaltz  ·  Foxtrot  ·  Swing  ·  Tango  ·  Slow  ·  Stop", FeedStyle.Say, false, 0, 120));
        var look0 = new PanelLook();
        var typing0 = new List<(UUID, string)>();
        if (FeedPainter.Draw(look0, menu, typing0, scale) is { } cut) PanelShots.ShotOver(dir, "social-feed-menu-cut", Raster(cut), bg, scale);
        if (FeedPainter.Draw(look0, menu, typing0, scale, new FeedPainter.Use(new HashSet<long>(), 99, true, false)) is { } lit)
            PanelShots.ShotOver(dir, "social-feed-menu-pointed", Raster(lit), bg, scale);
        if (FeedPainter.Draw(look0, menu, typing0, scale, new FeedPainter.Use(new HashSet<long> { 99 }, 99, true, true)) is { } open)
            PanelShots.ShotOver(dir, "social-feed-menu-open", Raster(open), bg, scale);
        // more than fits, scrolled back partway by the right stick: the window onto every block, its bar at the right
        var longer = Sample().Concat(menu.Skip(Sample().Count)).Concat(Sample().Select(e => e with { Seq = e.Seq + 200 })).ToList();
        if (FeedPainter.Draw(look0, longer, typing0, scale) is { MaxScroll: > 0 } rest
            && FeedPainter.Draw(look0, longer, typing0, scale, new FeedPainter.Use(new HashSet<long>(), 0, true, false, null, rest.MaxScroll * 0.6f)) is { } back)
            PanelShots.ShotOver(dir, "social-feed-scrolled", Raster(back), bg, scale);
        // waiting notices with their keys: the Tidewater Harp's menu (eleven buttons, More > first, so bottom left) and a
        // notecard given (read first); the pointer on the sheet, one key lit
        var harp = Guid.NewGuid();
        var card = Guid.NewGuid();
        var waiting = new List<FeedEntry>
        {
            new(201, FeedKind.Nearby, null, null, Ada, "Ada", "that harp is lovely", FeedStyle.Say, false, 0, 60),
            new(202, FeedKind.Notice, null, null, UUID.Zero, "Tidewater Harp", "Tidewater Harp: choose a piece.\n(made by Tessel the AI)",
                FeedStyle.Say, false, 0, 120) { NoticeId = harp },
            new(203, FeedKind.Notice, null, null, UUID.Zero, "m1s1", "gives you ITLP m1s1: Primer", FeedStyle.Say, false, 0, 120) { NoticeId = card },
        };
        var harpButtons = new[] { "More >", "Stop", "Night Sail", "Lost Unicorn", "Pigeon-sized", "Stella's Garden", "The Waypost", "Tranquility Base",
            "Tidewater Harp", "Fireside Air", "Moonwater" }.Select(b => (b, b)).ToList();
        var forms = new Dictionary<Guid, FeedPainter.NoticeForm>
        {
            [harp] = new(harpButtons, true, false, true),
            [card] = new(new List<(string, string)> { ("Keep", "Keep"), ("Discard", "Discard"), ("Mute", "Block") }, false, true, true),
        };
        var litKey = new FeedPainter.FeedKey(FeedPainter.FeedKeyKind.Answer, harp, "Night Sail");
        if (FeedPainter.Draw(look0, waiting, typing0, scale, new FeedPainter.Use(new HashSet<long>(), 0, true, false, litKey), forms) is { } w)
            PanelShots.ShotOver(dir, "social-feed-notices", Raster(w), bg, scale);
        // batch B4: a keyword alert, a mention, a SLurl as a link (lit under the pointer), a translated line
        var b4 = new List<FeedEntry>
        {
            new(301, FeedKind.Nearby, null, null, Mira, "Mira Lane", "Juniper, the gallery opens at eight", FeedStyle.Say, false, 0, 60) { Alert = true },
            new(302, FeedKind.Nearby, null, null, Kes, "Kestrel", "come see it: secondlife:///app/teleport/Bay%20City%20-%20Gully/128/90/25 by the fountain", FeedStyle.Say, false, 0, 60),
            new(303, FeedKind.Nearby, null, null, Ada, "Ada", "bonsoir tout le monde", FeedStyle.Say, false, 0, 60) { Translation = "good evening everyone" },
            new(304, FeedKind.Group, "g-builders", "Builders' Guild", Kes, "Kestrel", "@Juniper the sandbox is yours", FeedStyle.Say, false, 0, 60) { Alert = true },
        };
        var linkKey = new FeedPainter.FeedKey(FeedPainter.FeedKeyKind.Link, Guid.Empty, Core.Agents.Places.Parse(b4[1].Text)[0].Slurl, 302);
        if (FeedPainter.Draw(look0, b4, typing0, scale, new FeedPainter.Use(new HashSet<long>(), 0, true, false, linkKey)) is { } b4d)
            PanelShots.ShotOver(dir, "social-feed-b4", Raster(b4d), bg, scale);
        PanelShots.ShotOver(dir, "social-tags", Tags(new PanelLook(), scale), bg, scale);
        PanelShots.ShotOver(dir, "social-tags-smoke", Tags(PanelLook.PresetNamed("smoke"), scale), bg, scale);
        View(dir);
    }

    internal static Panel.Raster Raster(FeedPainter.Drawn d) =>
        new(d.Rgba, d.Width, d.Height, d.Scale, Array.Empty<PanelKey>(), 0, null, Array.Empty<PanelChoice>(), false, d.Frost, d.Edge);

    /// <summary>A row of tags as they'd show: speaking (the light), typing (the dots), a username under a display name,
    /// and a far one at its smaller size.</summary>
    static Panel.Raster Tags(PanelLook look, float scale)
    {
        var tags = new (NameTags.Variant V, bool Speaking, float Size)[]
        {
            (new("Mira Lane", "mira.lane", false, look), true, 1),
            (new("Kestrel", null, true, look), false, 1),
            (new("Ada", null, false, look), false, 1),
            (new("✧ Juniper ✧", "juniperberry.resident", false, look), false, 1),
            (new("Far Away", null, false, look), false, 0.68f),
            // batch B4: a group title, away; Do Not Disturb in a contact set's colour; editing their appearance, a friend
            (new("Rowan", "rowan.ashby", false, look, "Builders' Guild", NameTags.Marks.Away), false, 1),
            (new("Sable", null, false, look, null, NameTags.Marks.Busy, 0x5BA8E8), false, 1),
            (new("Wren", null, true, look, "Officer", NameTags.Marks.Appearance, 0xF2D56B), false, 1),
        };
        var pics = tags.Select(t => (t, NameTags.Draw(t.V, scale))).ToList();
        // and a chat bubble over one, a hover tip on a thing for sale
        pics.Add(((new NameTags.Variant("bubble", null, false, look), false, 1), NameTags.DrawBubble(new NameTags.Bubble(
            "has anyone seen the new gallery?\n\u0002(it's by the lawn)\nthe one with the glass roof", look), scale)));
        pics.Add(((new NameTags.Variant("tip", null, false, look), false, 1), HoverTips.Draw(new HoverTips.Tip("Cedar Armchair", "L$25", look), scale)));
        int gap = (int)(24 * scale);
        int w = pics.Sum(p => (int)(p.Item2.W * p.t.Size) + gap) + gap, h = pics.Max(p => p.Item2.H) + 2 * gap;
        var rgba = new byte[w * h * 4];
        unsafe
        {
            fixed (byte* px = rgba)
            {
                using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), (nint)px, w * 4)!;
                var c = surface.Canvas;
                float x = gap;
                var accent = SKColor.Parse(look.Accent);
                var ink = SKColor.Parse(look.Ink);
                foreach (var ((v, speaking, size), pic) in pics)
                {
                    using var img = SKImage.FromPixels(new SKImageInfo(pic.W, pic.H, SKColorType.Rgba8888, SKAlphaType.Premul), SKData.CreateCopy(pic.Rgba), pic.W * 4);
                    float y = h - gap - pic.H * size;
                    c.DrawImage(img, new SKRect(x, y, x + pic.W * size, y + pic.H * size), new SKSamplingOptions(SKFilterMode.Linear));
                    // the marks the overlay adds (Overlay.Tag): approximately
                    if (speaking)
                    {
                        float r = pic.LineY * 0.42f * 1.15f, cx = x - r * 1.25f, cy = y + pic.LineY;
                        using var halo = new SKPaint { IsAntialias = true, Color = accent.WithAlpha(90), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, r * 0.5f) };
                        c.DrawCircle(cx, cy, r * 1.4f, halo);
                        using var dot = new SKPaint { IsAntialias = true, Color = accent };
                        c.DrawCircle(cx, cy, r, dot);
                    }
                    if (v.Typing)
                        for (int i = 0; i < 3; i++)
                            using (var dot = new SKPaint { IsAntialias = true, Color = ink.WithAlpha((byte)(230 - i * 60)) })
                                c.DrawCircle(x + pic.DotsX + i * 7 * scale, y + pic.LineY, 2.3f * scale, dot);
                    x += pic.W * size + gap;
                }
                c.Flush();
            }
        }
        return new Panel.Raster(rgba, w, h, scale, Array.Empty<PanelKey>(), 0, null, Array.Empty<PanelChoice>(), false, 0, 1);
    }

    /// <summary>view-social.png: an answer on the panel where it rests and the feed beside it, in a Quest 3 left eye.</summary>
    static void View(string dir)
    {
        View(dir, "view-social");
        // and at each of the person's sizes (UiSize): the feed and panel wider and holding more when smaller
        foreach (var size in UiSize.Steps)
        {
            UiSize.Scale = size;
            View(dir, $"view-social-size-{size:0.00}");
        }
        UiSize.Scale = 1;
    }

    static void View(string dir, string name)
    {
        var eyePath = Path.Combine(dir, "view.png");
        if (!File.Exists(eyePath)) return;
        using var eye = SKBitmap.Decode(eyePath);
        using var eyeImage = SKImage.FromBitmap(eye);
        float l = -MathF.Tan(54 * MathF.PI / 180), r = MathF.Tan(42 * MathF.PI / 180), u = MathF.Tan(47 * MathF.PI / 180), d = -MathF.Tan(49 * MathF.PI / 180);
        SKPoint Project(System.Numerics.Vector3 p) => new((p.X / -p.Z - l) / (r - l) * eye.Width, (u - p.Y / -p.Z) / (u - d) * eye.Height);
        SKRect Rect(System.Numerics.Vector3[] corners)
        {
            var pts = corners.Select(c => c + new System.Numerics.Vector3(-0.032f, 0, 0)).Select(Project).ToArray();
            return new SKRect(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
        }
        using var surface = SKSurface.Create(new SKImageInfo(eye.Width, eye.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.DrawImage(eyeImage, 0, 0);

        var panel = new Panel { Fit = UiSize.Fit };
        panel.Update(s =>
        {
            s.Heard = "what did Mira say?";
            s.Result = "She asked whether anyone has seen the new gallery on the east lawn.";
        });
        var pr = panel.Draw(UiSize.Raster);
        var place = new PanelPlacement();
        var (pos, rot, size) = place.Update(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity, PanelPlacement.Mode.Panel, pr.LogicalWidth, pr.LogicalHeight, 0, frozen: false);
        var feed = FeedPainter.Draw(new PanelLook(), Sample(), new List<(UUID, string)>(), UiSize.Raster, fit: UiSize.Fit)!;
        var fsize = new System.Numerics.Vector2(feed.LogicalWidth, feed.LogicalHeight) * SocialView.MetresPerPx;
        var (fpos, frot) = SocialView.Pose(System.Numerics.Vector3.Zero, place.FollowingYaw ?? 0, fsize);
        PanelShots.DrawFrosted(c, eyeImage, Raster(feed), Rect(PanelPlacement.Corners(fpos, frot, fsize)), 10);
        PanelShots.DrawFrosted(c, eyeImage, pr, Rect(PanelPlacement.Corners(pos, rot, size)), 10);
        using (var p = new SKPaint { Color = new SKColor(255, 255, 255, 70), StrokeWidth = 2, IsAntialias = true })
        {
            var centre = Project(new System.Numerics.Vector3(0.032f, 0, -1));
            c.DrawLine(centre.X - 30, centre.Y, centre.X + 30, centre.Y, p);
            c.DrawLine(centre.X, centre.Y - 30, centre.X, centre.Y + 30, p);
        }
        PanelShots.Save(surface, Path.Combine(dir, name + ".png"));
    }
}
