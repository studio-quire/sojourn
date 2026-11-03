using System.Numerics;
using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Client.Things;
using Sojourn.Session.Assistant;
using Sojourn.Session.Objects;

namespace Sojourn.Client.Building;

/// <summary>
/// --panel-shots' part for building by hand's readout and a thing's card (Claude Opus 5.5, 2026-11-02):
/// - hb-readout-*.png: the readout's states (nothing selected, selected, the pointer on each key, everything switched
///   on), and some in German (de-hb-readout-*.png);
/// - view-hb-*.png: where the readout and a thing's card rest beside the panel and beside the settings menu, in a
///   Quest 3 left eye (view.png), as the other view-*.png;
/// - a check, printed: at every menu size, the readout and the card rest clear of the panel, the settings menu and
///   each other (their extents as seen from the eyes).
/// </summary>
static class HandBuildShots
{
    const float Deg = MathF.PI / 180;

    public static void Write(string dir, SKBitmap? bg, float scale, Func<Panel.Raster> answer, Func<Panel.Raster> menu)
    {
        var look = new PanelLook();
        var at = new Vector3(128.25f, 64.5f, 22.75f);
        var turn = new Vector3(0, 0, 90);
        void Readout(string name, ReadoutView v)
        {
            var (px, w, h, used, _) = HandBuildPainter.Draw(v, look, scale, false);
            PanelShots.ShotOver(dir, name, Raster(px, w, h, used), bg, scale);
        }
        ReadoutView View(bool selected, int hover, bool snap = false, bool fine = false, bool local = false, string? note = null)
        {
            var keys = new[] { ReadoutKey.Done, ReadoutKey.Snap, ReadoutKey.Fine, ReadoutKey.Local, ReadoutKey.Undo };
            string line = hover >= 0 ? HandBuild.About(keys[hover], snap, fine, local, "0.5", "15")
                : note ?? (selected ? Lang.T("Point at a key to see what it does.") : Lang.T("Tap one of your things to move it."));
            return new ReadoutView(selected ? "Garden bench" : Lang.T("building by hand"), selected ? at : null, selected ? turn : null,
                snap ? Lang.F("grid {0} m · turns {1}°", "0.5", "15") : null, fine, local, selected, line, hover >= 0 || note != null, hover);
        }
        foreach (var lang in new[] { "en", "de" })
        {
            Lang.Use(lang);
            string pre = lang == "en" ? "" : lang + "-";
            Readout(pre + "hb-readout-1-nothing", View(false, -1));
            Readout(pre + "hb-readout-2-selected", View(true, -1));
            Readout(pre + "hb-readout-3-on-snap", View(true, 1));
            Readout(pre + "hb-readout-4-on-fine", View(true, 2));
            Readout(pre + "hb-readout-5-on-axes", View(true, 3));
            if (lang == "en")
            {
                Readout("hb-readout-6-on-undo", View(true, 4));
                Readout("hb-readout-7-on-done", View(true, 0));
                Readout("hb-readout-9-undone", View(true, -1, note: Lang.T("Undone.")));
            }
            Readout(pre + "hb-readout-8-all-on-snap", View(true, 1, snap: true, fine: true, local: true));
            Readout(pre + "hb-readout-8b-all-on-axes", View(true, 3, snap: true, fine: true, local: true));
            // what came of a move: each outcome's line, from the classification of a case like it
            foreach (var (name, c) in Outcomes)
                if (lang == "en" || name is "refusing" or "partly-refused")
                    Readout($"{pre}hb-readout-10-{name}", View(true, -1, note: c.Result == "" ? Lang.T("The land there doesn't allow it.") : HandBuild.Said(c.Outcome())));
        }
        Lang.Use("en");
        Classified();
        Places(dir, answer, menu, look);
    }

    /// <summary>A move's case: where it was, where the hand put it, where the region has it after, whether a refusal
    /// notice answered the drag, and what it should be called ("" for one still being dragged).</summary>
    sealed record MoveCase(Vector3 From, Vector3 Put, Vector3 At, float TurnedDeg, bool Refusal, string Result)
    {
        public Sojourn.Session.Building.HandMove.Outcome Outcome()
        {
            var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, TurnedDeg * Deg);
            var result = Sojourn.Session.Building.HandMove.Classify(From, Put, Quaternion.Identity, At, turned, Refusal);
            return new Sojourn.Session.Building.HandMove.Outcome(default, "Garden bench", From, At, Quaternion.Identity, turned, result, DateTime.UtcNow)
            {
                Put = Put, PutRot = Quaternion.Identity, Said = Refusal ? "Can't rotate -- permission denied" : null,
            };
        }
    }

    // the first two are the user's on Second Life (0.1.0.852, 2026-11-02), from its log: a drag towards a no-build
    // parcel that the region took as far as the parcel's edge, then one from there that it refused outright (5.5 cm)
    static readonly (string Name, MoveCase Case)[] Outcomes =
    [
        ("partly-refused", new(new(222.75f, 49.93f, 22.05f), new(74.90f, 123.82f, 38.44f), new(92.34f, 114.94f, 36.76f), 0, true, "partly refused")),
        ("refused-stayed", new(new(92.34f, 114.94f, 36.76f), new(59.44f, 133.79f, 42.03f), new(92.35f, 114.90f, 36.80f), 0, true, "refused")),
        ("refused-quiet", new(new(10, 10, 22), new(14, 10, 22), new(10.005f, 10, 22), 0, false, "refused")),
        ("placed", new(new(10, 10, 22), new(14, 10, 22), new(14.01f, 10, 22.005f), 0.5f, false, "placed")),
        ("placed-despite-notice", new(new(10, 10, 22), new(14, 10, 22), new(14.01f, 10, 22), 0, true, "placed")),
        ("nudged", new(new(10, 10, 22), new(14, 10, 22), new(14, 10, 22.04f), 0, false, "nudged")),
        ("moved-elsewhere", new(new(10, 10, 22), new(14, 10, 22.5f), new(14.3f, 10.4f, 21.1f), 0, false, "moved elsewhere")),
        ("turned-elsewhere", new(new(10, 10, 22), new(14, 10, 22), new(14, 10, 22), 40, false, "moved elsewhere")),
        ("refused-small-drag", new(new(10, 10, 22), new(10.06f, 10, 22), new(10.01f, 10, 22), 0, true, "refused")),
        ("partly-refused-near", new(new(10, 10, 22), new(14, 10, 22), new(10.3f, 10, 22), 0, true, "partly refused")),
        ("refusing", new(new(10, 10, 22), new(14, 10, 22), new(10, 10, 22), 0, true, "")),
    ];

    /// <summary>The classification of each case, and which region notices a drag claims, printed as checks.</summary>
    static void Classified()
    {
        int bad = 0;
        foreach (var (name, c) in Outcomes)
        {
            if (c.Result == "") continue;
            var o = c.Outcome();
            bool ok = o.Result == c.Result;
            if (!ok) bad++;
            Console.WriteLine($"building's outcomes: {(ok ? "PASS" : "FAIL")} {name}: {o.Result}{(ok ? "" : $" (wanted {c.Result})")}: \"{HandBuild.Said(o)}\"");
        }
        foreach (var (text, ours) in new[]
        {
            ("Can't rotate -- permission denied", true), ("Can't move object -- permission denied", true), ("Cannot scale -- permission denied", true),
            ("can't rotate -- Permission denied.", true), ("Can't edit object -- permission denied", false), ("Permission denied", false),
            ("Can't rez object 'Box' at { 92, 114, 36 } on parcel 'Welcome' in region Hub because the owner of this land does not allow it.", false),
            ("Can't set position -- permission denied", true), ("You can't rotate that.", false),
        })
        {
            bool ok = Sojourn.Session.Building.HandMove.IsMoveRefusal(text) == ours;
            if (!ok) bad++;
            Console.WriteLine($"building's refusals: {(ok ? "PASS" : "FAIL")} \"{text}\" {(ours ? "answers a drag" : "goes to the feed")}");
        }
        Console.WriteLine($"building's outcomes and refusals: {(bad == 0 ? "all as meant" : $"{bad} wrong")}");
    }

    static Panel.Raster Raster(byte[] px, int w, int h, float used) =>
        new(px, w, h, used, Array.Empty<PanelKey>(), 0, null, Array.Empty<PanelChoice>(), false, 0, 1);

    static Panel.Raster Card(PanelLook look)
    {
        var actions = new List<ThingAction>
        {
            new(ThingDo.Sit, "sit"), new(ThingDo.Touch, "touch"), new(ThingDo.Buy, "buy", "L$25"), new(ThingDo.TakeCopy, "take a copy"),
            new(ThingDo.Open, "open"), new(ThingDo.Move, "move by hand"), new(ThingDo.About, "about this"), new(ThingDo.Ask, "ask the assistant"),
        };
        var v = new CardView("Garden bench", "yours", CardPage.Main, actions, false, [], null, null, null, false, false, false, -1, 0);
        var (px, w, h, used, _, _) = ThingCardPainter.Draw(v, look, UiSize.Raster, false);
        return Raster(px, w, h, used);
    }

    /// <summary>Where they rest, at each menu size: checked clear of the panel and the settings menu, and drawn.</summary>
    static void Places(string dir, Func<Panel.Raster> answer, Func<Panel.Raster> menu, PanelLook look)
    {
        using var eye = File.Exists(Path.Combine(dir, "view.png")) ? SKBitmap.Decode(Path.Combine(dir, "view.png")) : null;
        int bad = 0;
        foreach (var size in UiSize.Steps)
        {
            UiSize.Scale = size;
            float mpp = UiSize.MetresPerPx(PanelPlacement.Distance);
            var panel = answer();
            var settings = menu();
            var (rpx, rw, rh, rused, _) = HandBuildPainter.Draw(new ReadoutView("Garden bench", Vector3.Zero, Vector3.Zero, null, false, false, true,
                "Point at a key to see what it does.", false, -1), look, UiSize.Raster, false);
            var readout = Raster(rpx, rw, rh, rused);
            var card = Card(look);
            Vector2 Size(Panel.Raster r) => new Vector2(r.LogicalWidth, r.LogicalHeight) * mpp;
            var (pp, pr) = PanelPlacement.PanelRest(Vector3.Zero, 0, PanelPlacement.Mode.Panel, Size(panel));
            var (mp, mr) = PanelPlacement.PanelRest(Vector3.Zero, 0, PanelPlacement.Mode.Menu, Size(settings));
            var (rp, rr) = PanelPlacement.RightOfPanel(Vector3.Zero, 0, Size(readout));
            var (cp, cr) = PanelPlacement.RightOfPanel(Vector3.Zero, 0, Size(card), Size(readout));
            var (c1p, c1r) = PanelPlacement.RightOfPanel(Vector3.Zero, 0, Size(card));
            var sheets = new (string Name, Vector3[] Corners)[]
            {
                ("panel", PanelPlacement.Corners(pp, pr, Size(panel))), ("settings menu", PanelPlacement.Corners(mp, mr, Size(settings))),
                ("readout", PanelPlacement.Corners(rp, rr, Size(readout))), ("card over the readout", PanelPlacement.Corners(cp, cr, Size(card))),
                ("card alone", PanelPlacement.Corners(c1p, c1r, Size(card))),
            };
            foreach (var (a, b) in new[] { (2, 0), (2, 1), (3, 0), (3, 1), (3, 2), (4, 0), (4, 1) })
            {
                var (ba, bb) = (Box(sheets[a].Corners), Box(sheets[b].Corners));
                bool clear = ba.Right < bb.Left || bb.Right < ba.Left || ba.Top < bb.Bottom || bb.Top < ba.Bottom;
                if (!clear) bad++;
                Console.WriteLine($"building's sheets at size {size:0.00}: {(clear ? "PASS" : "FAIL")} the {sheets[a].Name} clear of the {sheets[b].Name} "
                    + $"(yaw {ba.Left / Deg:0.0} to {ba.Right / Deg:0.0}, pitch {ba.Bottom / Deg:0.0} to {ba.Top / Deg:0.0}; "
                    + $"its yaw {bb.Left / Deg:0.0} to {bb.Right / Deg:0.0}, pitch {bb.Bottom / Deg:0.0} to {bb.Top / Deg:0.0})");
            }
            if (eye != null)
            {
                View(dir, eye, $"hb-{size:0.00}-panel", [(panel, sheets[0].Corners), (readout, sheets[2].Corners), (card, sheets[3].Corners)]);
                View(dir, eye, $"hb-{size:0.00}-settings", [(settings, sheets[1].Corners), (readout, sheets[2].Corners), (card, sheets[3].Corners)]);
            }
        }
        UiSize.Scale = 1;
        Console.WriteLine($"building's sheets: {(bad == 0 ? "all clear" : $"{bad} overlaps")}");
    }

    /// <summary>A sheet's extent seen from the eyes (at the origin, -z ahead): yaw left to right (yaw grows leftward, so
    /// Left is the smaller here: x to the right), pitch bottom to top, radians.</summary>
    static (float Left, float Right, float Bottom, float Top) Box(Vector3[] corners)
    {
        var yaws = corners.Select(c => MathF.Atan2(c.X, -c.Z)).ToArray();
        var pitches = corners.Select(c => MathF.Atan2(c.Y, new Vector2(c.X, c.Z).Length())).ToArray();
        return (yaws.Min(), yaws.Max(), pitches.Min(), pitches.Max());
    }

    /// <summary>view-NAME.png: sheets at their room corners in the left eye's image, as PanelShots' views.</summary>
    static void View(string dir, SKBitmap eye, string name, (Panel.Raster Raster, Vector3[] Corners)[] sheets)
    {
        using var eyeImage = SKImage.FromBitmap(eye);
        float l = -MathF.Tan(54 * Deg), r = MathF.Tan(42 * Deg), u = MathF.Tan(47 * Deg), d = -MathF.Tan(49 * Deg);
        SKPoint Project(Vector3 p) => new((p.X / -p.Z - l) / (r - l) * eye.Width, (u - p.Y / -p.Z) / (u - d) * eye.Height);
        using var surface = SKSurface.Create(new SKImageInfo(eye.Width, eye.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.DrawImage(eyeImage, 0, 0);
        foreach (var (raster, room) in sheets)
        {
            var pts = room.Select(p => Project(p + new Vector3(-0.032f, 0, 0))).ToArray();
            var rect = new SKRect(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
            PanelShots.DrawFrosted(c, eyeImage, raster, rect, 10);
        }
        PanelShots.Save(surface, Path.Combine(dir, $"view-{name}.png"));
    }
}
