using SkiaSharp;

namespace Sojourn.Client.Assistant;

/// <summary>
/// `--panel-shots DIR`: draws the panel in its states, and the settings menu, over a world screenshot and saves them as
/// PNGs, then quits. No grid, window or headset: for judging the look, and showing it, without a session. With an eye
/// image (`view.png` in DIR: a Quest-eye shot, `--stereo --quest-eyes`), also `view-*.png`: the panel where the headset
/// places it, as much of the left eye as it takes (<see cref="PanelPlacement"/>).
/// </summary>
public static class PanelShots
{
    public static void Write(string dir, string? background, float scale = 1.5f)
    {
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, "*.png"))
            if (Path.GetFileName(old) is not ("view.png" or "hud.png")) File.Delete(old);
        using var bg = background != null && File.Exists(background) ? SKBitmap.Decode(background) : null;

        void Shot(string name, Panel.Raster r) => ShotOver(dir, name, r, bg, scale);

        // a confirmation as it is once armed (its key takes presses from a moment after it's first shown)
        Panel.Raster Drawn(Panel p, bool arm = true)
        {
            var r = p.Draw(scale);
            if (r.Confirm is not { } pc || !arm) return r;
            pc.MarkShown();
            Thread.Sleep((int)PendingConfirm.ArmMs + 50);
            return p.Draw(scale);
        }

        Panel.Raster State(Action<PanelState> state, PanelLook? look = null, bool arm = true, Action<Panel>? also = null)
        {
            var p = new Panel { Look = look ?? new PanelLook() };
            p.Update(state);
            also?.Invoke(p);
            return Drawn(p, arm);
        }

        foreach (var (name, state) in States())
            Shot(name, State(state));
        Shot("1b-idle-help", State(s => s.Hint = Hint, also: p => p.ToggleHelp()));
        Shot("6b-confirm-arming", State(States().First(x => x.Name == "6-confirm").State, arm: false));
        // a long answer: the next pages, and the last (which keeps the first's height)
        var paged = States().First(x => x.Name == "4c-answer-paged").State;
        Shot("4d-answer-paged-2", State(paged, also: p => p.Turn(1)));
        Shot("4e-answer-paged-last", State(paged, also: p => { p.Turn(1); p.Turn(1); p.Turn(1); p.Turn(1); }));
        Shot("4f-answer-paged-lit", State(paged, also: p => p.SetHover(1)));
        // replying: the last answer stays while the next request is heard and worked on, then is a strip above the next
        foreach (var (name, panel) in Replying()) Shot(name, Drawn(panel));
        var look = new PanelLook();
        Shot("7b-settings-view-paged", Panel.DrawMenu(look, ViewTabs, 3, ViewRows, null, -1, scale, 1, 0, 44 / UiSize.BaseDegPerPx));
        Shot("7c-settings-view-paged-2", Panel.DrawMenu(look, ViewTabs, 3, ViewRows, null, -1, scale, 1, 1, 44 / UiSize.BaseDegPerPx));
        Shot("7d-settings-view-small", Panel.DrawMenu(look, ViewTabs, 3, ViewRows, null, -1, scale, 1, 0, 44 / (UiSize.BaseDegPerPx * 0.7f)));
        // every section's tab as the headset has them, at size 1 and at 0.7 (wider), and a group opened
        Shot("7e-settings-all-tabs", Panel.DrawMenu(look, AllTabs, 4, SocialRows, null, -1, scale, 1, 0, 44 / UiSize.BaseDegPerPx));
        Shot("7f-settings-all-tabs-small", Panel.DrawMenu(look, AllTabs, 4, SocialRows, null, -1, scale, 1, 0, 44 / (UiSize.BaseDegPerPx * 0.7f),
            UiSize.WideFor(1 / 0.7f)));
        Shot("7g-settings-group", Panel.DrawMenu(look, AllTabs, 9, GraphicsRows, null, -1, scale, 1, 0, 44 / UiSize.BaseDegPerPx, group: "graphics"));
        Shot("7h-settings-group-lit", Panel.DrawMenu(look, AllTabs, 4, SocialRows, null, 23, scale, 1, 0, 44 / UiSize.BaseDegPerPx));
        // the pointer resting on a row (its tip under it), on the last row (above it), and on a tab
        string Tip(string section, MenuRow? row) => Sojourn.Client.Control.MenuGuide.Tip(section, null, row) ?? "(no tip)";
        Shot("7i-settings-tip", Panel.DrawMenu(look, Tabs, 0, MovingRows, null, -1, scale, 1, tip: (3, Tip("moving", MovingRows[3]))));
        Shot("7j-settings-tip-last", Panel.DrawMenu(look, Tabs, 0, MovingRows, null, -1, scale, 1, tip: (MovingRows.Length - 1, Tip("moving", MovingRows[^1]))));
        Shot("7k-settings-tip-tab", Panel.DrawMenu(look, AllTabs, 4, SocialRows, null, -1, scale, 1, 0, 44 / UiSize.BaseDegPerPx, tip: (-1 - 7, Tip("reader", null))));
        Shot("7l-settings-tip-long", Panel.DrawMenu(look, Tabs, 1, AssistantRows(0), null, -1, scale, 1, tip: (6, Tip("assistant", AssistantRows(0)[6]))));
        Shot("7-settings-moving", Panel.DrawMenu(look, Tabs, 0, MovingRows, null, -1, scale, 1));
        Shot("8-settings-assistant", Panel.DrawMenu(look, Tabs, 1, AssistantRows(0), null, -1, scale, 1));
        Shot("8b-settings-help", Panel.DrawMenu(look, Tabs, 1, AssistantRows(0), HelpLines, -1, scale, 1));
        // each preset, answering, confirming and as the settings (and how readable, by the measure the assistant's changes get)
        for (int i = 0; i < PanelLook.Presets.Count; i++)
        {
            var preset = PanelLook.Presets[i];
            if (preset.Readability() is { } note) Console.WriteLine($"preset {preset.Preset}: {note}");
            Console.WriteLine($"preset {preset.Preset}: {preset.Margins()}");
            foreach (var (name, state) in States().Where(x => x.Name is "4-answer" or "5-choices" or "6-confirm"))
                Shot($"look-{preset.Preset}-{name[2..]}", State(state, preset));
            Shot($"look-{preset.Preset}-settings", Panel.DrawMenu(preset, Tabs, 1, AssistantRows(i), null, -1, scale, 1));
        }

        // the reader: the session's replies at the newest, a long notecard partway down, and in smoke
        Panel.Raster Read(Reader rd, float s) => rd.Draw(s, false) is { } r
            ? new Panel.Raster(r.Image.Rgba, r.Image.Width, r.Image.Height, r.Scale, Array.Empty<PanelKey>(), 0, null, Array.Empty<PanelChoice>(), false, 0, 1)
            : throw new InvalidOperationException("reader closed");
        var replies = new Reader();
        replies.Show(new ReaderDoc(ReaderKind.Replies, "", new[]
        {
            new ReaderPart("what's this harp", "The Tidewater Harp: Tessel made it. Touch it and pick a piece; More > shows the rest of the list."),
            new ReaderPart("read me the notecard", "It's the ITLP primer: four modules, each a short walk with a quiz at the end. The first starts at the blue arch."),
            new ReaderPart("how do I get to Moonwater", "It's at Wych 43, 20, 22. I can take you there now, or you can say when."),
        }), atEnd: true);
        Shot("reader-replies", Read(replies, scale));
        var notecard = new Reader();
        notecard.Show(new ReaderDoc(ReaderKind.Notecard, "ITLP m1s1: Primer", ReaderApi.Paragraphs(string.Join("\n\n", Enumerable.Range(1, 14).Select(i =>
            $"{i}. Walk to the marker on the path and read its sign. Each sign holds one idea from the module, and the quiz at the end "
            + "asks about them in order. Take your time: the markers wait, and nothing runs out.")))), atEnd: false);
        notecard.Draw(scale, false);
        notecard.ScrollBy(700);
        Shot("reader-notecard", Read(notecard, scale));
        notecard.Look = PanelLook.PresetNamed("smoke");
        Shot("reader-notecard-smoke", Read(notecard, scale));

        Views(dir, scale);
        GlowSheet(dir, bg, after: true);
        GlowSheet(dir, bg, after: false);
        AdaptiveSheet(dir, bg);
        Social.SocialShots.Write(dir, bg, scale);
        Showing.ElementShots.Write(dir, bg, scale);
        Hud.HudShots.Write(dir, bg, scale);
        Map.MapShots.Write(dir, bg, scale);
        Control.HeadsetKeyboard.WriteShots(dir, bg, scale);
        Private.PrivateShots.Write(dir, bg, scale);

        // a page of them all, to look through in a browser
        var figures = Directory.GetFiles(dir, "*.png").Select(Path.GetFileName).Where(n => n != "view.png").Order()
            .Select(n => $"<figure><img src=\"{n}\"><figcaption>{Path.GetFileNameWithoutExtension(n)}</figcaption></figure>");
        File.WriteAllText(Path.Combine(dir, "index.html"),
            "<!doctype html><meta charset=utf-8><title>Sojourn panel</title><style>body{background:#2b2d31;color:#ccc;font:14px system-ui;margin:24px}" +
            "main{display:flex;flex-wrap:wrap;gap:18px;align-items:flex-start}figure{margin:0}img{max-width:560px;display:block}</style>" +
            "<main>" + string.Join("\n", figures) + "</main>");
    }

    /// <summary>A raster over the world image (cover-fit), frosted as the renderer would, saved as DIR/name.png.</summary>
    internal static void ShotOver(string dir, string name, Panel.Raster r, SKBitmap? bg, float scale)
    {
        int pad = (int)(40 * scale);
        int w = r.Width + 2 * pad, h = r.Height + 2 * pad;
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        using var world = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (bg != null)
        {
            // cover-fit the world behind the panel
            float s = Math.Max((float)w / bg.Width, (float)h / bg.Height);
            float bw = bg.Width * s, bh = bg.Height * s;
            using var bgImage = SKImage.FromBitmap(bg);
            world.Canvas.DrawImage(bgImage, new SKRect((w - bw) / 2, (h - bh) / 2, (w + bw) / 2, (h + bh) / 2), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        else world.Canvas.Clear(new SKColor(90, 110, 130));
        using var worldImage = world.Snapshot();
        c.DrawImage(worldImage, 0, 0);
        DrawFrosted(c, worldImage, r, new SKRect(pad, pad, pad + r.Width, pad + r.Height), 14 * scale);
        Save(surface, Path.Combine(dir, name + ".png"));
    }

    /// <summary>glow-sheet.png: the glass look with frost 0.1 at falling tint_opacity (rows: 0.8, 0.5, 0.3, 0.15, 0.05)
    /// over black, a mid-grey, white and the world (columns), composited as the headset's compositor lays its layer
    /// (each channel decoded, blended in linear light), each row's readability advice printed. after: the glow and the
    /// linear premultiply (GlassLight); before (glow-sheet-before.png): the bytes as they were, no glow.</summary>
    static void GlowSheet(string dir, SKBitmap? bg, bool after)
    {
        const float s = 1.2f;
        float[] opacities = { 0.8f, 0.5f, 0.3f, 0.15f, 0.05f };
        var worlds = new SKColor?[] { SKColors.Black, new SKColor(118, 118, 118), SKColors.White, null };
        var rows = opacities.Select(o =>
        {
            var look = new PanelLook { Preset = "yours", TintOpacity = o, Frost = 0.1f, Glow = after ? new PanelLook().Glow : 0 };
            if (after) Console.WriteLine($"glow sheet, tint_opacity {o}: {look.Readability() ?? "reads well"} ({look.Margins()})");
            var p = new Panel { Look = look };
            p.Update(States().First(x => x.Name == "4-answer").State);
            return p.Draw(s, layer: after);
        }).ToList();
        CompositeSheet(Path.Combine(dir, after ? "glow-sheet.png" : "glow-sheet-before.png"), bg, worlds, rows, s);
    }

    /// <summary>adaptive-sheet.png: adaptive ink (AdaptiveInk) over black, a dark grey, a mid-grey, a light grey, white
    /// and the world (columns), for clear looks (rows: glass at tint_opacity 0.3, 0.15 and 0.05, pearl at 0.15, all
    /// frost 0.1; smoke as it is, which turns dark over bright places): each cell in the ink a sheet takes coming up over
    /// that world (its darker and brighter quarter, measured under the panel), composited as the headset does. The
    /// turned-over colours and each cell's choice are printed.</summary>
    static void AdaptiveSheet(string dir, SKBitmap? bg)
    {
        const float s = 1.2f;
        var worlds = new SKColor?[] { SKColors.Black, new SKColor(40, 40, 40), new SKColor(118, 118, 118), new SKColor(200, 200, 200), SKColors.White, null };
        var glass = new PanelLook { Preset = "yours", Frost = 0.1f };
        var looks = new[]
        {
            glass with { TintOpacity = 0.3f }, glass with { TintOpacity = 0.15f }, glass with { TintOpacity = 0.05f },
            PanelLook.PresetNamed("pearl") with { Preset = "yours", TintOpacity = 0.15f, Frost = 0.1f }, PanelLook.PresetNamed("smoke"),
        };
        var answer = States().First(x => x.Name == "4-answer").State;
        Panel.Raster Draw(PanelLook look)
        {
            var p = new Panel { Look = look };
            p.Update(answer);
            return p.Draw(s, layer: true);
        }
        foreach (var l in looks.DistinctBy(l => (l.Ink, l.Accent)))
        {
            var t = AdaptiveInk.Turned(l);
            Console.WriteLine($"adaptive ink, {l.Material} ink {l.Ink} turned over: ink {t.Ink}, dim {t.Dim}, faint {t.Faint}, accent {t.Accent} (label {t.OnAccent}), key_opacity {t.KeyOpacity:0.00}, sheen_opacity {t.SheenOpacity:0.00}");
        }
        var rows = looks.Select(l => Draw(l with { Glow = 0 })).ToList();
        CompositeSheet(Path.Combine(dir, "adaptive-sheet.png"), bg, worlds, rows, s, (row, col, lo, hi) =>
        {
            var look = AdaptiveInk.Fresh(looks[row], lo, hi);
            bool turned = look.Ink != looks[row].Ink;
            Console.WriteLine($"adaptive sheet: {looks[row].Material} {looks[row].TintOpacity:0.00} over world {col} ({lo:0.000}-{hi:0.000}): "
                + $"{(turned ? "turned" : "its own")} ink, contrast {Math.Min(look.InkContrast(lo), look.InkContrast(hi)):0.0}");
            return turned ? Draw(look) : rows[row];
        });
    }

    /// <summary>Rasters (rows) over worlds (columns: a solid colour, or the world image), frosted, composited as the
    /// headset's compositor lays a layer (each channel decoded, blended in linear light). pick: the raster for a cell
    /// by its row, column and the darker and brighter quarter of the world under it (linear), instead of the row's.</summary>
    static void CompositeSheet(string path, SKBitmap? bg, SKColor?[] worlds, IReadOnlyList<Panel.Raster> rows, float s,
        Func<int, int, float, float, Panel.Raster>? pick = null)
    {
        int pad = (int)(16 * s), cw = rows.Max(r => r.Width) + 2 * pad, ch = rows.Max(r => r.Height) + 2 * pad;
        using var sheet = new SKBitmap(new SKImageInfo(cw * worlds.Length, ch * rows.Count, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int row = 0; row < rows.Count; row++)
            for (int col = 0; col < worlds.Length; col++)
            {
                var r = rows[row];
                using var cell = SKSurface.Create(new SKImageInfo(cw, ch, SKColorType.Rgba8888, SKAlphaType.Premul));
                using (var world = SKSurface.Create(new SKImageInfo(cw, ch, SKColorType.Rgba8888, SKAlphaType.Premul)))
                {
                    if (worlds[col] is { } solid) world.Canvas.Clear(solid);
                    else if (bg != null)
                    {
                        float f = Math.Max((float)cw / bg.Width, (float)ch / bg.Height);
                        using var bgImage = SKImage.FromBitmap(bg);
                        world.Canvas.DrawImage(bgImage, new SKRect((cw - bg.Width * f) / 2, (ch - bg.Height * f) / 2, (cw + bg.Width * f) / 2, (ch + bg.Height * f) / 2),
                            new SKSamplingOptions(SKCubicResampler.Mitchell));
                    }
                    else world.Canvas.Clear(new SKColor(90, 110, 130));
                    using var worldImage = world.Snapshot();
                    cell.Canvas.DrawImage(worldImage, 0, 0);
                    DrawFrosted(cell.Canvas, worldImage, r, new SKRect(pad, pad, pad + r.Width, pad + r.Height), 14 * s, panel: false);
                }
                using var backdrop = new SKBitmap(new SKImageInfo(cw, ch, SKColorType.Rgba8888, SKAlphaType.Premul));
                cell.ReadPixels(backdrop.Info, backdrop.GetPixels(), backdrop.RowBytes, 0, 0);
                var under = backdrop.Bytes;
                if (pick != null)
                {
                    var lum = new List<float>();
                    for (int y = 0; y < r.Height; y += 6)
                        for (int x = 0; x < r.Width; x += 6)
                        {
                            int o = ((y + pad) * cw + x + pad) * 4;
                            lum.Add(0.2126f * GlassLight.Decode(under[o] / 255f) + 0.7152f * GlassLight.Decode(under[o + 1] / 255f) + 0.0722f * GlassLight.Decode(under[o + 2] / 255f));
                        }
                    lum.Sort();
                    int n = lum.Count;
                    r = pick(row, col, lum[(n - 1) / 4], lum[(n - 1) - (n - 1) / 4]);
                }
                var outPx = new byte[under.Length];
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        int o = (y * cw + x) * 4;
                        int px = x - pad, py = y - pad;
                        bool on = px >= 0 && py >= 0 && px < r.Width && py < r.Height;
                        int q = on ? (py * r.Width + px) * 4 : 0;
                        float a = on ? r.Rgba[q + 3] / 255f : 0;
                        for (int k = 0; k < 3; k++)
                        {
                            float lin = (on ? GlassLight.Decode(r.Rgba[q + k] / 255f) : 0) + (1 - a) * GlassLight.Decode(under[o + k] / 255f);
                            outPx[o + k] = (byte)Math.Round(255 * GlassLight.Encode(Math.Min(1, lin)));
                        }
                        outPx[o + 3] = 255;
                    }
                using var done = new SKBitmap(new SKImageInfo(cw, ch, SKColorType.Rgba8888, SKAlphaType.Premul));
                System.Runtime.InteropServices.Marshal.Copy(outPx, 0, done.GetPixels(), outPx.Length);
                using var canvas = new SKCanvas(sheet);
                canvas.DrawBitmap(done, col * cw, row * ch);
            }
        using var img = SKImage.FromBitmap(sheet);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>The panel over a world image at a rectangle (or any four corners), the world under it blurred where
    /// the body is: the renderer's frost, approximated (panel: false, the frost alone).</summary>
    internal static void DrawFrosted(SKCanvas c, SKImage world, Panel.Raster r, SKRect at, float blur, bool panel = true)
    {
        using var img = SKImage.FromPixels(new SKImageInfo(r.Width, r.Height, SKColorType.Rgba8888, SKAlphaType.Premul), SKData.CreateCopy(r.Rgba), r.Width * 4);
        if (r.Frost > 0)
        {
            c.Save();
            c.ClipRect(at);
            c.SaveLayer();
            // as overlay.frag: a near blur with a four-times wider one over it
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
                c.DrawImage(img, at, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), mask);
            c.Restore();
            c.Restore();
        }
        if (panel) c.DrawImage(img, at, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    internal static void Save(SKSurface surface, string path)
    {
        using var data = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
        using var f = File.Create(path);
        data.SaveTo(f);
    }

    /// <summary>view-*.png: the panel placed as the headset places it (looking level, standing), drawn into a Quest 3
    /// left-eye image at its angular size, so how much of the view it takes and where can be judged.</summary>
    static void Views(string dir, float scale)
    {
        var eyePath = Path.Combine(dir, "view.png");
        if (!File.Exists(eyePath)) return;
        using var eye = SKBitmap.Decode(eyePath);
        using var eyeImage = SKImage.FromBitmap(eye);
        // the left eye's frustum (Program.QuestEyes): outer 54, inner 42, up 47, down 49 degrees
        float l = -MathF.Tan(54 * MathF.PI / 180), r = MathF.Tan(42 * MathF.PI / 180), u = MathF.Tan(47 * MathF.PI / 180), d = -MathF.Tan(49 * MathF.PI / 180);
        SKPoint Project(System.Numerics.Vector3 p) => new((p.X / -p.Z - l) / (r - l) * eye.Width, (u - p.Y / -p.Z) / (u - d) * eye.Height);

        void View(string name, Panel.Raster raster, PanelPlacement.Mode mode)
        {
            var place = new PanelPlacement();
            var head = System.Numerics.Vector3.Zero;
            var (pos, rot, size) = place.Update(head, System.Numerics.Quaternion.Identity, mode, raster.LogicalWidth, raster.LogicalHeight, 0, frozen: false);
            Place(name, raster, PanelPlacement.Corners(pos, rot, size));
        }

        void Place(string name, Panel.Raster raster, System.Numerics.Vector3[] room)
        {
            var corners = room.Select(c => c + new System.Numerics.Vector3(-0.032f, 0, 0)).Select(Project).ToArray();
            using var surface = SKSurface.Create(new SKImageInfo(eye.Width, eye.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surface.Canvas;
            c.DrawImage(eyeImage, 0, 0);
            // the panel faces the eye closely enough that its projection is near a rectangle
            var rect = new SKRect(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
            DrawFrosted(c, eyeImage, raster, rect, 10);
            // the line of sight, faintly
            using (var p = new SKPaint { Color = new SKColor(255, 255, 255, 70), StrokeWidth = 2, IsAntialias = true })
            {
                var centre = Project(new System.Numerics.Vector3(0.032f, 0, -1));
                c.DrawLine(centre.X - 30, centre.Y, centre.X + 30, centre.Y, p);
                c.DrawLine(centre.X, centre.Y - 30, centre.X, centre.Y + 30, p);
            }
            Save(surface, Path.Combine(dir, $"view-{name}.png"));
        }

        Panel.Raster S(string state)
        {
            var p = new Panel();
            p.Update(States().First(x => x.Name == state).State);
            var r = p.Draw(2.4f);
            if (r.Confirm is { } pc) { pc.MarkShown(); Thread.Sleep((int)PendingConfirm.ArmMs + 50); r = p.Draw(2.4f); }
            return r;
        }
        View("listening", S("2-listening"), PanelPlacement.Mode.Panel);
        View("answer", S("4-answer"), PanelPlacement.Mode.Panel);
        View("answer-long", S("4b-answer-long"), PanelPlacement.Mode.Panel);
        View("answer-paged", S("4c-answer-paged"), PanelPlacement.Mode.Panel);
        View("point", S("5c-point"), PanelPlacement.Mode.Panel);
        View("choices", S("5-choices"), PanelPlacement.Mode.Panel);
        View("confirm", S("6-confirm"), PanelPlacement.Mode.Confirm);
        View("settings", Panel.DrawMenu(new PanelLook(), Tabs, 0, MovingRows, null, -1, 2.4f, 1), PanelPlacement.Mode.Menu);
        View("reply-working", Replying().First(x => x.Name == "4h-reply-working").Panel.Draw(2.4f), PanelPlacement.Mode.Panel);

        // each of the menu's sizes (UiSize): an answer that pages at 1, and the view section as it pages
        foreach (var size in UiSize.Steps)
        {
            UiSize.Scale = size;
            var p = new Panel { Fit = UiSize.Fit };
            p.Update(States().First(x => x.Name == "4c-answer-paged").State);
            View($"size-{size:0.00}-answer", p.Draw(UiSize.Raster), PanelPlacement.Mode.Panel);
            View($"size-{size:0.00}-settings", Panel.DrawMenu(new PanelLook(), ViewTabs, 3, ViewRows, null, -1, UiSize.Raster, 1, 0, 44 / UiSize.DegPerPx, UiSize.Wide),
                PanelPlacement.Mode.Menu);
            // the reader as ReaderView places it: 1.2 m out, its middle 4 degrees below the eyes
            var reader = new Reader { ViewHeight = Reader.BaseViewHeight * UiSize.Fit, Wide = UiSize.Wide };
            reader.Show(new ReaderDoc(ReaderKind.Notecard, "ITLP m1s1: Primer", ReaderApi.Paragraphs(string.Join("\n\n", Enumerable.Range(1, 14).Select(i =>
                $"{i}. Walk to the marker on the path and read its sign. Each sign holds one idea from the module, and the quiz at the end "
                + "asks about them in order. Take your time: the markers wait, and nothing runs out.")))), atEnd: false);
            if (reader.Draw(UiSize.Raster, false) is { } rr)
            {
                var raster = new Panel.Raster(rr.Image.Rgba, rr.Image.Width, rr.Image.Height, rr.Scale, Array.Empty<PanelKey>(), 0, null, Array.Empty<PanelChoice>(), false, 0, 1);
                var rsize = new System.Numerics.Vector2(rr.LogicalWidth, rr.LogicalHeight) * UiSize.MetresPerPx(PanelPlacement.Distance);
                float pitch = -4 * MathF.PI / 180;
                var at = new System.Numerics.Vector3(0, MathF.Sin(pitch), -MathF.Cos(pitch)) * 1.2f;
                var rot = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, pitch);
                Place($"size-{size:0.00}-reader", raster, PanelPlacement.Corners(at, rot, rsize));
            }
        }
        UiSize.Scale = 1;
    }

    const string Hint = "hold the right grip and speak";

    static IEnumerable<(string Name, Action<PanelState> State)> States()
    {
        yield return ("1-idle", s => { s.Hint = Hint; });
        yield return ("2-listening", s => { s.Hint = Hint; s.Listening = true; s.Pointing = "Fountain"; });
        yield return ("3-working", s =>
        {
            s.Hint = Hint; s.Heard = "put on the grey wool coat"; s.Working = true; s.Model = "haiku 5.5";
            s.Status = "looking in Clothing for coats"; s.Pointing = "Fountain";
        });
        yield return ("4-answer", s =>
        {
            s.Hint = Hint; s.Heard = "put on the grey wool coat";
            s.Result = "You're wearing the grey wool coat now. I took off the denim jacket so they don't clash.";
            s.Pointing = "Fountain";
        });
        yield return ("4b-answer-long", s =>
        {
            s.Hint = Hint; s.Heard = "what's on at the Pavilion tonight?";
            s.Result = "Tonight at the Pavilion there's a jazz set from eight until ten, with Mira Lane on piano and a trio "
                + "she plays with most Fridays. The dance floor opens at seven for a warm-up with recorded swing, and the bar "
                + "upstairs has a quieter lounge if you'd rather talk. Dress is smart casual, though nobody minds. Last week "
                + "about thirty people came, so it gets busy after nine; arriving early means a table by the water.";
        });
        yield return ("4c-answer-paged", s =>
        {
            s.Hint = Hint; s.Heard = "tell me about this sim's history";
            s.Result = string.Join(" ", Enumerable.Repeat(
                "Calas Galadhon began in 2007 as a small forest parcel and grew over the years into one of the best-loved "
                + "nature estates on the grid, with walking trails, a river you can canoe, quiet sitting spots and seasonal "
                + "builds that change through the year.", 4)) + " Visitors are welcome at any hour.";
        });
        yield return ("5-choices", s =>
        {
            s.Hint = Hint; s.Heard = "take me somewhere with music";
            s.Prompt = "Three places nearby have live music:";
            s.Choices = new()
            {
                new("1", "The Pavilion", "jazz, 4 people"),
                new("2", "Arrival point", "a DJ set, 11 people"),
                new("3", "The Fountain", "piano, quiet"),
            };
        });
        yield return ("5b-choices-long", s =>
        {
            s.Hint = Hint; s.Heard = "which outfit should I wear to the gala?";
            s.Prompt = "Three outfits in your Formal folder would suit a gala:";
            s.Choices = new()
            {
                new("1", "Midnight velvet evening gown with silver beading and a long train", "Formal/Gowns, worn once"),
                new("2", "Charcoal three-piece suit", "Formal/Suits, with the burgundy pocket square and the black oxfords"),
                new("3", "Emerald silk dress", null),
            };
        });
        yield return ("5c-point", s =>
        {
            s.Hint = Hint; s.Heard = "sit on that chair";
            s.PointAsk = "There are three chairs by the window. Which one?";
            s.Pointing = "Wicker Armchair · 3 m";
        });
        yield return ("5d-type-ask", s =>
        {
            s.Hint = Hint; s.Heard = "message Quillon I'm on my way"; s.Working = true; s.Model = "haiku 5.5";
            s.TypeAsk = "Who should I message?";
            s.Typing = "Quillon";
        });
        yield return ("5e-type-ask-talking", s =>
        {
            s.Hint = Hint; s.Heard = "message Quillon I'm on my way"; s.Working = true; s.Listening = true;
            s.TypeAsk = "Who should I message?";
            s.Typing = "Quillon";
        });
        yield return ("6-confirm", s =>
        {
            s.Hint = Hint; s.Heard = "tip the pianist 50";
            s.Confirm = new PendingConfirm { Title = "Pay L$50 to Mira Lane", Detail = "Your balance after: L$1,250." };
        });
    }

    static readonly string[] Tabs = { "moving", "assistant", "voice" };
    static readonly string[] ViewTabs = { "moving", "assistant", "voice", "view" };
    static readonly string[] AllTabs = { "moving", "assistant", "incognito", "you", "social", "voice", "sound", "reader", "safety", "view" };

    static List<(MenuRow Row, Action<int> Act)> NoRows() => new();

    /// <summary>The social section: switches, a stepper shown with hover tips, two groups (one with a value).</summary>
    static readonly MenuRow[] SocialRows =
    {
        MenuRow.Switch("names", true), MenuRow.Switch("chat", true), MenuRow.Switch("hover text", true),
        MenuRow.Switch("chat bubbles", false), MenuRow.Switch("tags through walls", false), MenuRow.Switch("group titles", true),
        MenuRow.Switch("friend colour", false), MenuRow.Choice("hover tips", new[] { "off", "0.3 s", "0.7 s", "1.5 s" }, 2),
        MenuRow.Group("alerts", NoRows).Row, MenuRow.Group("radar", NoRows, "on").Row,
    };

    /// <summary>A group's page: the view's graphics, long enough to page at size 1.</summary>
    static readonly MenuRow[] GraphicsRows =
    {
        MenuRow.Switch("shadows", true), MenuRow.Switch("reflections", true), MenuRow.Switch("mirrors", true), MenuRow.Switch("flexible prims", true),
        new("detail", "1.75", true), MenuRow.Switch("load governor", true), new("see-through layers", "all", true),
        MenuRow.Choice("see-through light", new[] { "full", "simple" }, 1), MenuRow.Choice("shadow & ssao size", new[] { "full", "half" }, 0),
        new("far shadow margin", "none", true), new("small shadow casters", "all", true), MenuRow.Choice("g-buffer (on restart)", new[] { "full", "narrow" }, 0),
        new("resolution", "100%", true), MenuRow.Switch("depth reprojection", true), MenuRow.Switch("load all around", false),
    };

    /// <summary>The headset's view section as it stands (size first, a menu put somewhere), long enough to page at size 1.</summary>
    static readonly MenuRow[] ViewRows =
    {
        UiSize.Row(new VrSettings()),
        new("menus", "reset", false),
        new("draw distance", "128 m", true),
        MenuRow.Group("graphics", NoRows).Row, MenuRow.Group("avatars", NoRows).Row,
        MenuRow.Choice("sky", new[] { "region", "dawn", "noon", "dusk", "night" }, 0),
        MenuRow.Choice("location line", new[] { "off", "on", "+ lag" }, 2), MenuRow.Group("beacons", NoRows, "on").Row,
        MenuRow.Group("parcel lines", NoRows).Row,
        MenuRow.Choice("maturity", new[] { "general", "moderate", "adult" }, 1), MenuRow.Group("menus", NoRows).Row,
    };

    /// <summary>Talking again over an answer: the orb under it, then its words worked on, then the new answer with the
    /// one before it as a strip.</summary>
    static IEnumerable<(string Name, Panel Panel)> Replying(float fit = 1)
    {
        Panel After(params Action<PanelState>[] next)
        {
            var p = new Panel { Fit = fit };
            p.Update(States().First(x => x.Name == "4b-answer-long").State);
            foreach (var n in next) p.Update(n);
            return p;
        }
        yield return ("4g-reply-listening", After(s => { s.Listening = true; s.Pointing = "Fountain"; }));
        yield return ("4h-reply-working", After(s =>
        {
            s.Listening = false; s.Heard = "is there anywhere quieter nearby?"; s.Result = null; s.Working = true; s.Model = "haiku 5.5";
            s.Status = "searching places nearby";
        }));
        yield return ("4i-reply-answered", After(
            s => { s.Heard = "is there anywhere quieter nearby?"; s.Result = null; s.Working = true; },
            s =>
            {
                s.Working = false;
                s.Result = "The Willow Garden is two minutes' walk east: benches by a pond and usually two or three people. Want me to take you?";
            }));
    }

    static readonly MenuRow[] MovingRows =
    {
        MenuRow.Choice("posture", new[] { "standing", "seated" }, 0),
        MenuRow.Choice("eye height", new[] { "your own", "the avatar's" }, 0),
        MenuRow.Choice("turning", new[] { "smooth", "snap" }, 0),
        new("turn speed", "180°/s", true),
        MenuRow.Choice("moving toward", new[] { "where you look", "left hand" }, 0),
        MenuRow.Switch("your body", true),
    };

    static MenuRow[] AssistantRows(int look) => new[]
    {
        MenuRow.Switch("panel", true),
        MenuRow.Choice("model", new[] { "haiku", "sonnet", "opus" }, 0),
        new MenuRow("look", PanelLook.Presets[look].Preset, false) { Looks = PanelLook.Presets, Selected = look },
        MenuRow.Switch("spoken replies", false),
        MenuRow.Switch("called Wren", true),
        MenuRow.Switch("its character", false, note: "“warmer, a bit cheeky”"),
        MenuRow.Switch("ask before touching others' things", true),
    };

    static readonly string[] HelpLines =
    {
        "hold the right grip and speak to the assistant",
        "X hides everything and brings it back, held: every menu back in front · Y recentres · this menu: the left menu button",
        "drag any menu anywhere · drop it on its outline to have it follow",
        "seated: sit up straight, then press Y to set your height",
    };
}
