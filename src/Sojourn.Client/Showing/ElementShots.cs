using System.Text.Json.Nodes;
using LibreMetaverse;
using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Client.Social;

namespace Sojourn.Client.Showing;

/// <summary>
/// `--panel-shots`: the assistant's elements drawn over the world image like the panel's shots (shown-*.png), each kind
/// in the default look and a few in every look; with the Quest eye image (view.png), view-shown.png: a timer and a
/// checklist in the view column beside an answer on the panel and the chat feed, as the headset places them.
/// </summary>
public static class ElementShots
{
    static readonly ulong Here = Utils.UIntsToLong(256000, 256000);
    const double Now = 1000;

    static ElementSpec Spec(string json) => ElementParse.Parse(JsonNode.Parse(json)!.AsObject(), null, Here, Now);

    static readonly UUID Fountain = UUID.Random(), Mira = UUID.Random();

    /// <summary>The samples, with their moving parts as of the shot.</summary>
    static IEnumerable<(string Name, ElementSpec Spec, BlockLive[] Live)> Samples()
    {
        yield return ("timer", Spec("""{"id":"tea","at":"view","title":"Tea","blocks":[{"timer":600}]}"""), new[] { new BlockLive(Seconds: 432) });
        yield return ("timer-done", Spec("""{"id":"tea","at":"view","title":"Tea","blocks":[{"timer":600}]}"""), new[] { new BlockLive(Seconds: 0, Done: true) });
        yield return ("checklist", Spec("""
            {"id":"errand","at":"view","title":"The jazz outfit","blocks":[{"list":[
              {"text":"Find the black dress","done":true},{"text":"Wear it","done":true},
              {"text":"Swap the boots for heels","done":false},{"text":"Teleport to the Pavilion","done":false}]}]}
            """), Array.Empty<BlockLive>());
        yield return ("list", Spec("""
            {"id":"outfits","at":"view","title":"Your outfits","blocks":[{"list":[
              {"text":"Jazz night","detail":"worn"},{"text":"Sunday walk","detail":"6 items"},{"text":"Builder's overalls"},
              {"text":"Winter coat and scarf","detail":"5 items"},{"text":"Formal black"}],"numbered":true}]}
            """), Array.Empty<BlockLive>());
        yield return ("table", Spec("""
            {"id":"events","at":"view","title":"Music nearby tonight","blocks":[{"table":[
              ["Where","What","When"],["Pavilion","Jazz trio","20:00"],["Arrival point","DJ set","21:30"],["The Fountain","Piano","22:00"]],
              "header":true}]}
            """), Array.Empty<BlockLive>());
        yield return ("bars", Spec("""
            {"id":"visits","at":"view","title":"Who came to the gallery","blocks":[{"chart":[["Mon",12],["Tue",8],["Wed",15],["Thu",22],["Fri",31]]},
              {"text":"Friday was the busiest so far.","style":"dim"}]}
            """), Array.Empty<BlockLive>());
        yield return ("line", Spec("""
            {"id":"balance","at":"view","title":"L$ this week","blocks":[{"chart":[["Mon",1250],["",1180],["",1420],["",1400],["",1610],["",1530],["Sun",1890]],"kind":"line","unit":"L$"}]}
            """), Array.Empty<BlockLive>());
        yield return ("progress", Spec("""
            {"id":"sorting","at":"view","title":"Sorting your inventory","blocks":[{"progress":34,"of":120,"label":"Objects into folders"},{"text":"Clothing next.","style":"dim"}]}
            """), Array.Empty<BlockLive>());
        yield return ("toward", Spec($$"""{"id":"fountain","at":"view","blocks":[{"toward":"{{Fountain}}","label":"The Fountain"}]}"""),
            new[] { new BlockLive(Bearing: 35, Distance: 24, Known: true) });
        yield return ("map", Spec($$"""
            {"id":"around","at":"view","title":"Around you","blocks":[{"map":[{"at":"{{Fountain}}","label":"Fountain"},{"at":"{{Mira}}","label":"Mira"},{"at":[140,80,24],"label":"Pavilion"}]}]}
            """), new[] { new BlockLive(Marks: new[] { (420, 610, true), (-300, 180, true), (900, -700, true) }, MapRadius: 50, North: -40) });
        yield return ("figure", Spec("""{"id":"balance-now","at":"wrist","title":"Your balance","blocks":[{"text":"L$1,890","style":"big"},{"text":"after paying the pianist","style":"dim"}]}"""), Array.Empty<BlockLive>());
        yield return ("label", Spec($$"""{"id":"mira","at":"{{Mira}}","text":"spoke to you 2 min ago"}"""), Array.Empty<BlockLive>());
        yield return ("world-card", Spec($$"""{"id":"fountain-note","at":"{{Fountain}}","title":"The Fountain","blocks":[{"text":"Linked, 12 prims. Yours.","style":"dim"}]}"""), Array.Empty<BlockLive>());
    }

    public static void Write(string dir, SKBitmap? bg, float scale)
    {
        // a crop of the world for the image block
        SKImage? picture = null;
        if (bg != null)
        {
            using var full = SKImage.FromBitmap(bg);
            int w = bg.Width / 2, h = bg.Height / 2;
            picture = full.Subset(new SKRectI(bg.Width / 4, bg.Height / 4, bg.Width / 4 + w, bg.Height / 4 + h));
        }
        var look = new PanelLook();
        foreach (var (name, spec, live) in Samples())
            Shot(dir, $"shown-{name}", spec, live, look, bg, scale, null);
        var image = Spec("""{"id":"view","at":"view","title":"What you saw","blocks":[{"image":"view"}]}""");
        Shot(dir, "shown-image", image, Array.Empty<BlockLive>(), look, bg, scale, picture);
        // a saved picture (a selfie just taken): wide, then upright, then still being read
        if (bg != null)
        {
            using var full = SKImage.FromBitmap(bg);
            int ph = bg.Height * 3 / 4, pw = ph * 9 / 16;
            using var upright = full.Subset(new SKRectI(bg.Width / 2 - pw / 2, bg.Height / 8, bg.Width / 2 + pw / 2, bg.Height / 8 + ph));
            var saved = Spec("""{"id":"picture","at":"room","title":"Selfie","blocks":[{"text":"-"}]}""");
            Shot(dir, "shown-picture", saved with { Blocks = new Block[] { new ImageBlock("selfie.png", bg.Width, bg.Height) } }, Array.Empty<BlockLive>(), look, bg, scale, full);
            Shot(dir, "shown-picture-upright", saved with { Title = null, Blocks = new Block[] { new ImageBlock("upright.png", pw, ph) } }, Array.Empty<BlockLive>(), look, bg, scale, upright);
            Shot(dir, "shown-picture-reading", saved with { Blocks = new Block[] { new ImageBlock("selfie.png", 1920, 1080) } }, Array.Empty<BlockLive>(), look, bg, scale, null);
        }
        foreach (var preset in PanelLook.Presets.Skip(1))
            foreach (var (name, spec, live) in Samples().Where(s => s.Name is "timer" or "checklist" or "label" or "bars"))
                Shot(dir, $"shown-{preset.Preset}-{name}", spec, live, preset, bg, scale, null);
        View(dir);
    }

    static Panel.Raster Raster(ElementPainter.Drawn d) =>
        new(d.Rgba, d.Width, d.Height, d.Scale, Array.Empty<PanelKey>(), 0, null, Array.Empty<PanelChoice>(), false, d.Frost, d.Edge);

    static void Shot(string dir, string name, ElementSpec spec, BlockLive[] live, PanelLook look, SKBitmap? bg, float scale, SKImage? picture)
    {
        try
        {
            float h = ElementPainter.HeightOf(spec, look);
            var d = ElementPainter.Draw(spec, live, _ => picture, look, scale, false);
            PanelShots.ShotOver(dir, name, Raster(d), bg, scale);
        }
        catch (Exception e) { Console.WriteLine($"{name}: {e.Message}"); }
    }

    /// <summary>A raster mapped onto four points (top left, top right, bottom right, bottom left) in perspective, over
    /// the world blurred where its body is (PanelShots.DrawFrosted's frost, through the same mapping).</summary>
    static void DrawQuad(SKCanvas c, SKImage world, Panel.Raster r, SKPoint[] q, float blur)
    {
        using var img = SKImage.FromPixels(new SKImageInfo(r.Width, r.Height, SKColorType.Rgba8888, SKAlphaType.Premul), SKData.CreateCopy(r.Rgba), r.Width * 4);
        var m = SquareTo(q, r.Width, r.Height);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        if (r.Frost > 0)
        {
            c.Save();
            using (var clip = new SKPath()) { clip.AddPoly(q, true); c.ClipPath(clip, antialias: true); }
            c.SaveLayer();
            using (var b = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(blur * r.Frost, blur * r.Frost, SKShaderTileMode.Clamp) })
                c.DrawImage(world, 0, 0, b);
            using (var b = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(4 * blur * r.Frost, 4 * blur * r.Frost, SKShaderTileMode.Clamp), Color = new SKColor(255, 255, 255, 153) })
                c.DrawImage(world, 0, 0, b);
            float k = 1 / Math.Max(r.Edge * 0.9f, 0.05f);
            using (var mask = new SKPaint
                   {
                       BlendMode = SKBlendMode.DstIn,
                       ColorFilter = SKColorFilter.CreateColorMatrix(new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, k, 0 }),
                   })
            {
                c.Save();
                c.Concat(in m);
                c.DrawImage(img, 0, 0, sampling, mask);
                c.Restore();
            }
            c.Restore();
            c.Restore();
        }
        c.Save();
        c.Concat(in m);
        c.DrawImage(img, 0, 0, sampling);
        c.Restore();
    }

    /// <summary>The perspective mapping of a w x h picture onto a quad (Heckbert's square-to-quad).</summary>
    static SKMatrix SquareTo(SKPoint[] q, float w, float h)
    {
        float dx1 = q[1].X - q[2].X, dx2 = q[3].X - q[2].X, dx3 = q[0].X - q[1].X + q[2].X - q[3].X;
        float dy1 = q[1].Y - q[2].Y, dy2 = q[3].Y - q[2].Y, dy3 = q[0].Y - q[1].Y + q[2].Y - q[3].Y;
        float den = dx1 * dy2 - dx2 * dy1;
        float g = (dx3 * dy2 - dx2 * dy3) / den, hh = (dx1 * dy3 - dx3 * dy1) / den;
        float a = q[1].X - q[0].X + g * q[1].X, b = q[3].X - q[0].X + hh * q[3].X;
        float d = q[1].Y - q[0].Y + g * q[1].Y, e = q[3].Y - q[0].Y + hh * q[3].Y;
        return new SKMatrix(a / w, b / h, q[0].X, d / w, e / h, q[0].Y, g / w, hh / h, 1);
    }

    /// <summary>view-shown.png: the panel answering, the feed on the left and a timer and a checklist in the view column
    /// on the right, at their angular sizes in a Quest 3 left eye, looking level.</summary>
    static void View(string dir)
    {
        View(dir, "view-shown");
        // the panel, the feed and the cards edge to edge at each of the person's sizes (UiSize)
        foreach (var size in UiSize.Steps)
        {
            UiSize.Scale = size;
            View(dir, $"view-shown-size-{size:0.00}");
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
        SKPoint[] Quad(System.Numerics.Vector3[] corners) => corners.Select(c => c + new System.Numerics.Vector3(-0.032f, 0, 0)).Select(Project).ToArray();
        using var surface = SKSurface.Create(new SKImageInfo(eye.Width, eye.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.DrawImage(eyeImage, 0, 0);
        var look = new PanelLook();

        var panel = new Panel { Fit = UiSize.Fit };
        panel.Update(s =>
        {
            s.Heard = "keep a timer for ten minutes for my tea";
            s.Result = "Your tea timer is running.";
        });
        var pr = panel.Draw(UiSize.Raster);
        var place = new PanelPlacement();
        var (pos, rot, size) = place.Update(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity, PanelPlacement.Mode.Panel, pr.LogicalWidth, pr.LogicalHeight, 0, frozen: false);
        float yaw = place.FollowingYaw ?? 0;

        var samples = Samples().ToDictionary(s => s.Name);
        var cards = new[] { samples["timer"], samples["checklist"] }
            .Select(s => ElementPainter.Draw(s.Spec, s.Live, _ => null, look, UiSize.Raster, false, UiSize.Wide)).ToList();
        var poses = ShownView.Column(System.Numerics.Vector3.Zero, yaw, cards.Select(k => new System.Numerics.Vector2(k.LogicalWidth, k.LogicalHeight)).ToList());
        for (int i = 0; i < cards.Count; i++)
        {
            var sz = new System.Numerics.Vector2(cards[i].LogicalWidth, cards[i].LogicalHeight) * ShownView.MetresPerPx;
            DrawQuad(c, eyeImage, Raster(cards[i]), Quad(PanelPlacement.Corners(poses[i].Pos, poses[i].Rot, sz)), 10);
        }
        // the chat feed on the left
        if (Social.FeedPainter.Draw(look, Social.SocialShots.Sample(), new List<(LibreMetaverse.UUID, string)>(), UiSize.Raster, fit: UiSize.Fit) is { } feed)
        {
            var fsize = new System.Numerics.Vector2(feed.LogicalWidth, feed.LogicalHeight) * Social.SocialView.MetresPerPx;
            var (fpos, frot) = Social.SocialView.Pose(System.Numerics.Vector3.Zero, yaw, fsize);
            DrawQuad(c, eyeImage, Social.SocialShots.Raster(feed), Quad(PanelPlacement.Corners(fpos, frot, fsize)), 10);
        }
        DrawQuad(c, eyeImage, pr, Quad(PanelPlacement.Corners(pos, rot, size)), 10);
        using (var p = new SKPaint { Color = new SKColor(255, 255, 255, 70), StrokeWidth = 2, IsAntialias = true })
        {
            var centre = Project(new System.Numerics.Vector3(0.032f, 0, -1));
            c.DrawLine(centre.X - 30, centre.Y, centre.X + 30, centre.Y, p);
            c.DrawLine(centre.X, centre.Y - 30, centre.X, centre.Y + 30, p);
        }
        PanelShots.Save(surface, Path.Combine(dir, name + ".png"));
    }
}
