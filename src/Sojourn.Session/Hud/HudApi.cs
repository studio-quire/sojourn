using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Core.World;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Vector2 = System.Numerics.Vector2;

namespace Sojourn.Session.Hud;

/// <summary>
/// SojournHud: the person's HUD attachments for any caller (the assistant's hud tool, Sojourn Access's lists): what's
/// worn, a picture of the HUD screen or of one HUD with every named part's place on it, a press by a point on that
/// picture, a part's id (and face) or its name, or a button read off its picture; a part's faces as they show; the
/// buttons read for a HUD's look and saving them; and the headset's board up or away. A press is the person's click on
/// their own attachment (no confirmation: the gate's touch rule is about other people's things), with the surface data
/// the viewer's click sends. The screen is whichever renderer the app has (<see cref="IHudScreen"/>): the VR client's
/// (Sojourn.Client/Hud/HudView) or the renderer out of sight's.
/// </summary>
public sealed class HudApi : LeapApi
{
    readonly IHudScreen _screen;
    readonly HudButtons? _buttons;

    public override string Name => "SojournHud";
    public override string Desc => "the person's HUD attachments: list, pictures of them, pressing their buttons, buttons read off their pictures, the headset's HUD board";

    public HudApi(AgentSession s, IHudScreen screen, HudButtons? buttons) : base(s)
    {
        _screen = screen;
        _buttons = buttons;
        Op("list", "-> {huds: [{id, name, point, prims, drawn, buttons_read}], shown}: the HUDs worn", (a, ct) => ListAsync(ct));
        Op("look", "{hud?: id or name} -> {picture, width, height, png (base64), huds: [{id, name, rect}], parts: [{id, name, desc?, hud, rect}], buttons?: [{name, rect}]}: a "
            + "picture of the HUD screen, or of one HUD bigger; rect = [x, y, w, h] in the picture's pixels; buttons: those read for it before "
            + "(transparent: no backdrop, for tests)", LookAsync);
        Op("press", "{at: [x, y], picture?} | {part: id, face?} | {name: words, hud?} | {button: number or words, hud} , hold? seconds -> "
            + "{touched, name, face, st}: a click on a HUD, at a point of a picture (the last one by default), on a part by id (on one of "
            + "its faces as it shows), on the part or read button whose name fits, or on a read button by its number", PressAsync);
        Op("faces", "{part: id} -> {faces: [{face, share, where}]}: a HUD part's faces as they show on the screen, the most of it first "
            + "(share: of the part as seen; where: on the part, as on the screen)", FacesAsync);
        Op("buttons", "{hud: id or name} -> {hud, look, buttons: [{n, name, where}], read_by?, read_at?}: the buttons read off this "
            + "HUD's picture before (for its look: another page of it is another look)", ButtonsAsync);
        Op("save_buttons", "{hud: id or name, picture?: number, buttons: [{name, rect: [x, y, w, h]}], by?} -> {saved, dropped}: the "
            + "buttons seen on a picture of that HUD (look's, the last by default), kept for its look; one whose middle isn't on "
            + "the HUD is dropped", SaveButtonsAsync);
        Op("forget_buttons", "{hud} -> {}: the buttons read for its look forgotten", ForgetAsync);
        Op("show", "{on=true}: the HUDs shown (in the headset, the board brought up in front; on the desktop, drawn over the view)", a =>
        {
            bool on = a.Bool("on", true);
            _screen.Show(on);
            return new JsonObject { ["shown"] = on };
        });
    }

    RegionWorld W => S.Region ?? throw new LeapError("not in a region");

    /// <summary>What's placed now; a renderer that can't answer (restarting after a lost GPU, stopped) is said so,
    /// rather than read as no HUDs worn.</summary>
    async Task<HudsPlaced> PlacedAsync(CancellationToken ct) =>
        await _screen.WithHudsAsync<HudsPlaced?>((placed, _) => placed, null, ct)
        ?? throw new LeapError("the HUDs can't be seen just now (the view isn't running: try again in a moment)");

    async Task<JsonNode?> ListAsync(CancellationToken ct)
    {
        var w = W;
        // what the region says is worn on HUD points; drawn: placed by the renderer (its shape and textures may still be coming)
        uint self = S.Client?.Self.LocalID ?? 0;
        var roots = w.Prims.Values.Where(p => p.ParentID == self && self != 0 && Sojourn.Render.Realtime.Hud.IsHudPoint((int)p.PrimData.AttachmentPoint)).ToList();
        if (roots.Count > 0) await S.Objects.FetchAsync(w, roots, TimeSpan.FromSeconds(3), ct);
        var drawn = (await _screen.WithHudsAsync((placed, _) => placed, HudsPlaced.None, ct)).Objects;
        return new JsonObject
        {
            ["huds"] = new JsonArray(roots.Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.ID.ToString(), ["name"] = NameOf(w, p.ID), ["point"] = ObjectViews.AttachPointName((int)p.PrimData.AttachmentPoint),
                ["prims"] = S.Objects.Linkset(w, p).Count, ["drawn"] = drawn.Any(h => h.Id == p.ID),
                ["buttons_read"] = ReadFor(p)?.Buttons.Count ?? 0,
            }).ToArray()),
            ["shown"] = _screen.Shown,
        };
    }

    HudButtons.Entry? ReadFor(Primitive root) => _buttons?.For(HudButtons.LookKey(S, root)) is { Buttons.Count: > 0 } e ? e : null;

    async Task Names(RegionWorld w, IReadOnlyList<HudObject> huds, bool parts, CancellationToken ct)
    {
        var prims = huds.SelectMany(h => parts ? h.Parts.Select(p => p.Id) : new[] { h.Id })
            .Select(id => w.PrimById(id)).OfType<Primitive>().ToList();
        if (prims.Count == 0) return;
        await S.Objects.FetchAsync(w, prims, TimeSpan.FromSeconds(parts ? 1.5 : 3), ct);
        // a worn part's own properties come only when it's selected (as the viewer's edit tool, editing linked parts)
        var missing = prims.Where(p => p.Properties == null).ToList();
        if (parts && missing.Count > 0) await S.Objects.FetchFullAsync(w, missing, TimeSpan.FromSeconds(3), ct);
    }

    static string NameOf(RegionWorld w, UUID id) => w.PrimById(id)?.Properties?.Name is { Length: > 0 } n ? n : "";
    static string DescOf(RegionWorld w, UUID id) => w.PrimById(id)?.Properties?.Description is { Length: > 0 } d ? d : "";

    static HudObject? Find(RegionWorld w, HudsPlaced placed, string? which)
    {
        if (string.IsNullOrWhiteSpace(which)) return null;
        var huds = placed.Objects;
        if (UUID.TryParse(which, out var id)) return huds.FirstOrDefault(h => h.Id == id || h.Parts.Any(p => p.Id == id))
            ?? throw new LeapError($"no HUD {which} is worn (or it isn't drawn yet)");
        var best = huds.Select(h => (h, score: Fit(NameOf(w, h.Id), which))).OrderByDescending(x => x.score).FirstOrDefault();
        return best.score > 0.4 ? best.h : throw new LeapError($"no HUD worn is called {which}");
    }

    /// <summary>How well words fit a name: 1 the same, less by the share of words found.</summary>
    public static double Fit(string name, string words)
    {
        static string[] Words(string s) => System.Text.RegularExpressions.Regex.Split(s.ToLowerInvariant(), "[^a-z0-9]+").Where(x => x.Length > 0).ToArray();
        var n = Words(name);
        var q = Words(words);
        if (q.Length == 0 || n.Length == 0) return 0;
        if (string.Join(" ", n) == string.Join(" ", q)) return 1;
        return 0.9 * q.Count(x => n.Any(y => y == x || y.StartsWith(x) || (x.StartsWith(y) && y.Length > 2))) / q.Length;
    }

    async Task<JsonNode?> LookAsync(JsonObject a, CancellationToken ct)
    {
        var w = W;
        var placed = await PlacedAsync(ct);
        if (placed.Objects.Count == 0) throw new LeapError("no HUDs are worn (or they aren't drawn yet)");
        var one = Find(w, placed, a.Str("hud"));
        await Names(w, one != null ? new[] { one } : placed.Objects, parts: true, ct);
        if (await _screen.PictureAsync(one, a.Bool("transparent", false), ct) is not { } got) throw new LeapError("no picture (the HUDs aren't drawn yet)");
        var (pic, png) = got;
        placed = await PlacedAsync(ct);
        var huds = new JsonArray();
        var parts = new JsonArray();
        var buttons = new JsonArray();
        foreach (var h in placed.Objects)
        {
            if (pic.Rect(h.Min, h.Max) is not { } r) continue;
            huds.Add(new JsonObject { ["id"] = h.Id.ToString(), ["name"] = NameOf(w, h.Id), ["point"] = ObjectViews.AttachPointName(h.Point), ["rect"] = Rect(r) });
            string hudName = NameOf(w, h.Id);
            foreach (var p in h.Parts.Skip(1))
            {
                // parts a person or a script named: "Object" and the HUD's own name say nothing
                string name = NameOf(w, p.Id), desc = DescOf(w, p.Id);
                if ((name is "" or "Object" or "Primitive" || name == hudName) && desc == "") continue;
                if (pic.Rect(p.Min, p.Max) is not { } pr) continue;
                var o = new JsonObject { ["id"] = p.Id.ToString(), ["name"] = name, ["hud"] = hudName, ["rect"] = Rect(pr) };
                if (desc != "" && desc != "(No Description)") o["desc"] = desc;
                parts.Add(o);
                if (parts.Count >= 60) break;
            }
            if (w.PrimById(h.Id) is { } root && ReadFor(root) is { } read)
                foreach (var b in read.Buttons)
                {
                    var (min, max) = ButtonBox(h, b);
                    if (pic.Rect(min, max) is { } br) buttons.Add(new JsonObject { ["name"] = b.Name, ["hud"] = hudName, ["rect"] = Rect(br) });
                }
        }
        var res = new JsonObject
        {
            ["picture"] = pic.Number, ["width"] = pic.Width, ["height"] = pic.Height,
            ["png"] = Convert.ToBase64String(png), ["huds"] = huds, ["parts"] = parts,
        };
        if (buttons.Count > 0) res["buttons"] = buttons;
        return res;
    }

    static JsonArray Rect((int X, int Y, int W, int H) r) => new(r.X, r.Y, r.W, r.H);

    /// <summary>A read button's box in HUD space (x as the HUD's).</summary>
    static (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max) ButtonBox(HudObject h, HudButtons.Button b)
    {
        float dy = h.Max.Y - h.Min.Y, dz = h.Max.Z - h.Min.Z;
        float y = h.Max.Y - b.X * dy, z = h.Max.Z - b.Y * dz;
        return (new(h.Min.X, y - b.W * dy / 2, z - b.H * dz / 2), new(h.Max.X, y + b.W * dy / 2, z + b.H * dz / 2));
    }

    /// <summary>A point across a read button (0-1 of it, from its left and top) in HUD space (y, z).</summary>
    static Vector2 OnButton(HudObject h, HudButtons.Button b, float across = 0.5f, float down = 0.5f)
    {
        float dy = h.Max.Y - h.Min.Y, dz = h.Max.Z - h.Min.Z;
        return new(h.Max.Y - (b.X + (across - 0.5f) * b.W) * dy, h.Max.Z - (b.Y + (down - 0.5f) * b.H) * dz);
    }

    async Task<JsonNode?> PressAsync(JsonObject a, CancellationToken ct)
    {
        var w = W;
        double hold = Math.Clamp(a.Num("hold", 0), 0, 10);
        var placed = await PlacedAsync(ct);
        if (placed.Objects.Count == 0) throw new LeapError("no HUDs are worn (or they aren't drawn yet)");
        Func<HudsPlaced, HudPicker, PickHit?> pick;
        string? what = null;
        if (a.Nums("at") is { Length: >= 2 } at)
        {
            var pic = _screen.Picture(a.Has("picture") ? a.Int("picture", 0) : null) ?? throw new LeapError("no picture to press on: look first");
            if (pic.Aspect != placed.Aspect) throw new LeapError("the HUD screen changed shape since that picture: look again");
            var yz = pic.ToHud((float)at[0], (float)at[1]);
            pick = (_, p) => p(yz);
        }
        else if (a.Uuid("part") is { } pid)
        {
            var part = Huds.PartOf(placed, pid)?.Part ?? throw new LeapError($"no HUD part {pid}");
            if (a.Has("face"))
            {
                int face = a.Int("face", 0);
                pick = (now, p) => Huds.PartOf(now, pid) is { } pp && HudScan.Faces(p, pp.Part).FirstOrDefault(f => f.Face == face) is { Hit: var hit } ? hit : null;
                what = $"face {face}";
            }
            else pick = (now, p) => Huds.PartOf(now, pid) is { } pp ? Huds.PickPart(p, pp.Part) : null;
        }
        else if (a.Has("button"))
        {
            var hud = Find(w, placed, a.Str("hud")) ?? (placed.Objects.Count == 1 ? placed.Objects[0] : throw new LeapError("button needs hud: which HUD"));
            var read = w.PrimById(hud.Id) is { } root ? ReadFor(root) : null;
            if (read == null) throw new LeapError($"no buttons have been read for {NameOf(w, hud.Id)} as it looks now");
            var spec = a["button"]!.ToString();
            HudButtons.Button b = int.TryParse(spec, out int n) ? (n >= 1 && n <= read.Buttons.Count ? read.Buttons[n - 1] : throw new LeapError($"it has {read.Buttons.Count} buttons"))
                : read.Buttons.Select(x => (x, s: Fit(x.Name, spec))).OrderByDescending(x => x.s).FirstOrDefault() is { s: >= 0.45 } best ? best.x
                : throw new LeapError($"no button read on it is called {spec}");
            pick = ButtonPick(hud.Id, b);
            what = b.Name;
        }
        else if (a.Str("name") is { Length: > 0 } words)
        {
            var scope = Find(w, placed, a.Str("hud"));
            var huds = scope != null ? new[] { scope } : placed.Objects;
            await Names(w, huds, parts: true, ct);
            var partBest = huds.SelectMany(h => h.Parts).Select(p => (p, score: Math.Max(Fit(NameOf(w, p.Id), words), Fit(DescOf(w, p.Id), words) * 0.95)))
                .OrderByDescending(x => x.score).FirstOrDefault();
            var buttonBest = huds.SelectMany(h => w.PrimById(h.Id) is { } r && ReadFor(r) is { } e ? e.Buttons.Select(b => (h, b)) : [])
                .Select(x => (x.h, x.b, score: Fit(x.b.Name, words))).OrderByDescending(x => x.score).FirstOrDefault();
            if (buttonBest.b != null && buttonBest.score >= 0.45 && buttonBest.score > partBest.score)
            {
                pick = ButtonPick(buttonBest.h.Id, buttonBest.b);
                what = buttonBest.b.Name;
            }
            else if (partBest.score >= 0.45)
            {
                var target = partBest.p;
                pick = (now, p) => Huds.PartOf(now, target.Id) is { } pp ? Huds.PickPart(p, pp.Part) : null;
            }
            else throw new LeapError($"no HUD part or read button is called {words}: look at the picture and press by position");
        }
        else throw new LeapError("press needs at, part, button or name");

        var hit = await _screen.WithHudsAsync<PickHit?>((now, p) => pick(now, p), null, ct);
        if (hit is not { } h) throw new LeapError("nothing of a HUD there");
        var o = new JsonObject { ["touched"] = h.Id.ToString(), ["name"] = what ?? (NameOf(w, h.Id) is { Length: > 0 } n2 ? n2 : NameOf(w, h.RootId)) };
        if (!Huds.Touchable(S, h.Id))
        {
            o["note"] = "it takes no touches (nothing scripted listens there)";
            return o;
        }
        var surface = Huds.Surface(h);
        S.SendGrab(h.Region, h.LocalId, surface);
        if (hold > 0) await Task.Delay(TimeSpan.FromSeconds(hold), CancellationToken.None);
        S.SendDeGrab(h.Region, h.LocalId, surface);
        if (surface.Face >= 0)
        {
            o["face"] = surface.Face;
            o["st"] = new JsonArray(Math.Round(surface.ST.X, 3), Math.Round(surface.ST.Y, 3));
        }
        return o;
    }

    /// <summary>A pick on a read button of a HUD: its middle, or a few points across it, on that HUD's own prims.</summary>
    static Func<HudsPlaced, HudPicker, PickHit?> ButtonPick(UUID hudId, HudButtons.Button b) => (now, p) =>
    {
        if (now.Objects.FirstOrDefault(x => x.Id == hudId) is not { } h) return null;
        var mine = h.Parts.Select(x => x.Id).ToHashSet();
        foreach (var (u, v) in new[] { (0.5f, 0.5f), (0.35f, 0.5f), (0.65f, 0.5f), (0.5f, 0.35f), (0.5f, 0.65f) })
            if (p(OnButton(h, b, u, v), q => mine.Contains(q.ID)) is { } hit) return hit;
        return null;
    };

    async Task<JsonNode?> FacesAsync(JsonObject a, CancellationToken ct)
    {
        var pid = a.Uuid("part") ?? throw new LeapError("part: a HUD part's id");
        var faces = await _screen.WithHudsAsync<List<HudScan.FaceSeen>?>((now, p) => Huds.PartOf(now, pid) is { } pp ? HudScan.Faces(p, pp.Part) : null, null, ct)
            ?? throw new LeapError("that isn't part of a HUD that's drawn (not worn, or not drawn yet)");
        return new JsonObject
        {
            ["faces"] = new JsonArray(faces.Select(f => (JsonNode)new JsonObject
            {
                ["face"] = f.Face, ["share"] = Math.Round(f.Share, 2), ["where"] = f.Where,
            }).ToArray()),
        };
    }

    async Task<HudObject> HudOf(JsonObject a, CancellationToken ct)
    {
        var placed = await PlacedAsync(ct);
        return Find(W, placed, a.Str("hud") ?? throw new LeapError("hud: its id or name")) ?? throw new LeapError("hud: its id or name");
    }

    async Task<JsonNode?> ButtonsAsync(JsonObject a, CancellationToken ct)
    {
        var w = W;
        var hud = await HudOf(a, ct);
        var root = w.PrimById(hud.Id) ?? throw new LeapError("that HUD isn't here any more");
        var key = HudButtons.LookKey(S, root);
        var read = _buttons?.For(key);
        var o = new JsonObject { ["hud"] = NameOf(w, hud.Id), ["look"] = key };
        var list = new JsonArray();
        if (read != null)
        {
            // where among the buttons, not on the HUD (a panel's title or a meter can fill its top third): spread across
            // them, a row or a column of them saying only the way they run
            float x0 = read.Buttons.Min(b => b.X), x1 = read.Buttons.Max(b => b.X), y0 = read.Buttons.Min(b => b.Y), y1 = read.Buttons.Max(b => b.Y);
            static float Across(float v, float lo, float hi) => hi - lo > 0.05f ? (v - lo) / (hi - lo) : 0.5f;
            int i = 0;
            foreach (var b in read.Buttons)
                list.Add(new JsonObject { ["n"] = ++i, ["name"] = b.Name, ["where"] = Huds.Where(Across(b.X, x0, x1), Across(b.Y, y0, y1)) });
            o["read_by"] = read.By;
            o["read_at"] = read.At.ToString("yyyy-MM-dd HH:mm");
        }
        o["buttons"] = list;
        return o;
    }

    async Task<JsonNode?> SaveButtonsAsync(JsonObject a, CancellationToken ct)
    {
        if (_buttons == null) throw new LeapError("this app keeps no HUD buttons");
        var w = W;
        var hud = await HudOf(a, ct);
        var root = w.PrimById(hud.Id) ?? throw new LeapError("that HUD isn't here any more");
        var pic = _screen.Picture(a.Has("picture") ? a.Int("picture", 0) : null) ?? throw new LeapError("no picture of it: look first");
        if (a["buttons"] is not JsonArray given) throw new LeapError("buttons: [{name, rect: [x, y, w, h]}]");
        float dy = hud.Max.Y - hud.Min.Y, dz = hud.Max.Z - hud.Min.Z;
        if (dy <= 0 || dz <= 0) throw new LeapError("that HUD has no size on the screen");
        var wanted = new List<(string Name, HudButtons.Button B)>();
        foreach (var n in given.OfType<JsonObject>())
        {
            var name = (n.Str("name") ?? "").Trim();
            if (name.Length == 0 || n.Nums("rect") is not { Length: 4 } r) continue;
            var lo = pic.ToHud((float)r[0], (float)r[1]);
            var hi = pic.ToHud((float)(r[0] + r[2]), (float)(r[1] + r[3]));
            // across the HUD's box: 0 at its left (its largest y) and top (its largest z)
            float x0 = (hud.Max.Y - lo.X) / dy, x1 = (hud.Max.Y - hi.X) / dy, y0 = (hud.Max.Z - lo.Y) / dz, y1 = (hud.Max.Z - hi.Y) / dz;
            var b = new HudButtons.Button(name, (x0 + x1) / 2, (y0 + y1) / 2, MathF.Abs(x1 - x0), MathF.Abs(y1 - y0));
            wanted.Add((name, b));
        }
        if (wanted.Count == 0) throw new LeapError("no buttons given");
        // each button's middle has to land on this HUD
        var landed = await _screen.WithHudsAsync((now, p) => wanted.Select(x => ButtonPick(hud.Id, x.B)(now, p) != null).ToArray(), Array.Empty<bool>(), ct);
        var kept = wanted.Where((x, i) => i < landed.Length && landed[i]).Select(x => x.B).ToList();
        var dropped = wanted.Where((x, i) => i >= landed.Length || !landed[i]).Select(x => x.Name).ToList();
        // reading order: rows top to bottom (a row: within half a button's height), left to right in each
        kept = Rows(kept);
        var key = HudButtons.LookKey(S, root);
        if (kept.Count > 0) _buttons.Save(new HudButtons.Entry(key, NameOf(w, hud.Id), a.Str("by") ?? "the assistant", DateTime.Now, kept));
        return new JsonObject
        {
            ["saved"] = kept.Count, ["buttons"] = new JsonArray(kept.Select(b => (JsonNode)b.Name).ToArray()),
            ["dropped"] = new JsonArray(dropped.Select(d => (JsonNode)d).ToArray()), ["look"] = key,
        };
    }

    static List<HudButtons.Button> Rows(List<HudButtons.Button> list)
    {
        var rows = new List<List<HudButtons.Button>>();
        foreach (var b in list.OrderBy(b => b.Y))
        {
            var row = rows.LastOrDefault();
            if (row != null && MathF.Abs(b.Y - row.Average(x => x.Y)) < Math.Max(0.02f, row.Average(x => x.H) / 2)) row.Add(b);
            else rows.Add(new() { b });
        }
        return rows.SelectMany(r => r.OrderBy(b => b.X)).ToList();
    }

    async Task<JsonNode?> ForgetAsync(JsonObject a, CancellationToken ct)
    {
        var hud = await HudOf(a, ct);
        if (_buttons != null && W.PrimById(hud.Id) is { } root) _buttons.Forget(HudButtons.LookKey(S, root));
        return new JsonObject();
    }
}
