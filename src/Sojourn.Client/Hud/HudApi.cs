using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Core.World;
using Sojourn.Render.World;
using Vector2 = System.Numerics.Vector2;

namespace Sojourn.Client.Hud;

/// <summary>
/// SojournHud: the person's HUD attachments for any caller (the assistant's hud tool): what's worn, a picture of the
/// HUD screen or of one HUD with every named part's place on it, a press by a point on that picture, a part's id or its
/// name, and the headset's board up or away. A press is the person's click on their own attachment (no confirmation:
/// the gate's touch rule is about other people's things), with the surface data the viewer's click sends.
/// </summary>
public sealed class HudApi : LeapApi
{
    readonly HudView _view;
    readonly Func<HudBoard?> _boardNow;
    HudBoard? _board => _boardNow();

    public override string Name => "SojournHud";
    public override string Desc => "the person's HUD attachments: list, pictures of them, pressing their buttons, the headset's HUD board";

    /// <param name="board">the headset's board while there's a headset</param>
    public HudApi(AgentSession s, HudView view, Func<HudBoard?> board) : base(s)
    {
        _view = view;
        _boardNow = board;
        Op("list", "-> {huds: [{id, name, point, prims}], shown}: the HUDs worn", (a, ct) => ListAsync(ct));
        Op("look", "{hud?: id or name} -> {picture, width, height, png (base64), huds: [{id, name, rect}], parts: [{id, name, desc?, hud, rect}]}: a "
            + "picture of the HUD screen, or of one HUD bigger; rect = [x, y, w, h] in the picture's pixels (transparent: no backdrop, for tests)", LookAsync);
        Op("press", "{at: [x, y], picture?} | {part: id} | {name: words, hud?} , hold? seconds -> {touched, name, face, st}: a click on a HUD, at a point "
            + "of a picture (the last one by default), on a part by id, or on the part whose name or description fits", PressAsync);
        Op("show", "{on=true}: the HUDs shown (in the headset, the board brought up in front; on the desktop, drawn over the view)", a =>
        {
            bool on = a.Bool("on", true);
            if (_board != null) _board.Show(on); else _view.DesktopShown = on;
            return new JsonObject { ["shown"] = on };
        });
    }

    bool Shown => _board?.Shown ?? _view.DesktopShown;

    RegionWorld W => S.Region ?? throw new LeapError("not in a region");

    async Task<JsonNode?> ListAsync(CancellationToken ct)
    {
        var w = W;
        // what the region says is worn on HUD points; drawn: placed by the renderer (its shape and textures may still be coming)
        uint self = S.Client?.Self.LocalID ?? 0;
        var roots = w.Prims.Values.Where(p => p.ParentID == self && self != 0 && (int)p.PrimData.AttachmentPoint is >= 31 and <= 38).ToList();
        if (roots.Count > 0) await S.Objects.FetchAsync(w, roots, TimeSpan.FromSeconds(3), ct);
        var drawn = _view.Worn.Objects;
        return new JsonObject
        {
            ["huds"] = new JsonArray(roots.Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.ID.ToString(), ["name"] = NameOf(w, p.ID), ["point"] = ObjectViews.AttachPointName((int)p.PrimData.AttachmentPoint),
                ["prims"] = S.Objects.Linkset(w, p).Count, ["drawn"] = drawn.Any(h => h.Id == p.ID),
            }).ToArray()),
            ["shown"] = Shown,
        };
    }

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

    HudObject? Find(RegionWorld w, string? which)
    {
        if (string.IsNullOrWhiteSpace(which)) return null;
        var huds = _view.Worn.Objects;
        if (UUID.TryParse(which, out var id)) return huds.FirstOrDefault(h => h.Id == id || h.Parts.Any(p => p.Id == id))
            ?? throw new LeapError($"no HUD {which} is worn");
        var best = huds.Select(h => (h, score: Fit(NameOf(w, h.Id), which))).OrderByDescending(x => x.score).FirstOrDefault();
        return best.score > 0.4 ? best.h : throw new LeapError($"no HUD worn is called {which}");
    }

    /// <summary>How well words fit a name: 1 the same, less by the share of words found.</summary>
    static double Fit(string name, string words)
    {
        static string[] W(string s) => System.Text.RegularExpressions.Regex.Split(s.ToLowerInvariant(), "[^a-z0-9]+").Where(x => x.Length > 0).ToArray();
        var n = W(name);
        var q = W(words);
        if (q.Length == 0 || n.Length == 0) return 0;
        if (string.Join(" ", n) == string.Join(" ", q)) return 1;
        return 0.9 * q.Count(x => n.Any(y => y == x || y.StartsWith(x) || (x.StartsWith(y) && y.Length > 2))) / q.Length;
    }

    async Task<JsonNode?> LookAsync(JsonObject a, CancellationToken ct)
    {
        var w = W;
        if (_view.Worn.Objects.Count == 0) throw new LeapError("no HUDs are worn");
        var one = Find(w, a.Str("hud"));
        await Names(w, one != null ? new[] { one } : _view.Worn.Objects, parts: true, ct);
        if (await _view.PictureAsync(one, ct, a.Bool("transparent", false)) is not { } got) throw new LeapError("no picture (the HUDs aren't drawn yet)");
        var (pic, png) = got;
        var huds = new JsonArray();
        var parts = new JsonArray();
        foreach (var h in _view.Worn.Objects)
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
        }
        return new JsonObject
        {
            ["picture"] = pic.Number, ["width"] = pic.Width, ["height"] = pic.Height,
            ["png"] = Convert.ToBase64String(png), ["huds"] = huds, ["parts"] = parts,
        };
    }

    static JsonArray Rect((int X, int Y, int W, int H) r) => new(r.X, r.Y, r.W, r.H);

    async Task<JsonNode?> PressAsync(JsonObject a, CancellationToken ct)
    {
        var w = W;
        if (_view.Worn.Objects.Count == 0) throw new LeapError("no HUDs are worn");
        double hold = Math.Clamp(a.Num("hold", 0), 0, 10);
        Func<PickHit?> pick;
        if (a.Nums("at") is { Length: >= 2 } at)
        {
            var pic = (a.Has("picture") ? _view.PictureNumbered(a.Int("picture", 0)) : _view.LastPicture)
                ?? throw new LeapError("no picture to press on: look first");
            if (pic.Aspect != _view.Worn.Aspect) throw new LeapError("the HUD screen changed shape since that picture: look again");
            var yz = pic.ToHud((float)at[0], (float)at[1]);
            pick = () => _view.PickAtHud(yz);
        }
        else
        {
            HudPart? part = null;
            if (a.Uuid("part") is { } pid) part = _view.PartOf(pid)?.Part ?? throw new LeapError($"no HUD part {pid}");
            else if (a.Str("name") is { Length: > 0 } words)
            {
                var scope = Find(w, a.Str("hud"));
                var huds = scope != null ? new[] { scope } : _view.Worn.Objects;
                await Names(w, huds, parts: true, ct);
                var best = huds.SelectMany(h => h.Parts).Select(p => (p, score: Math.Max(Fit(NameOf(w, p.Id), words), Fit(DescOf(w, p.Id), words) * 0.95)))
                    .OrderByDescending(x => x.score).FirstOrDefault();
                if (best.score < 0.45) throw new LeapError($"no HUD part is called {words}: look at the picture and press by position");
                part = best.p;
            }
            else throw new LeapError("press needs at, part or name");
            var target = part.Value;
            pick = () => _view.PickPart(target);
        }

        var done = new TaskCompletionSource<PickHit?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _view.OnMain(() =>
        {
            try { done.TrySetResult(pick()); }
            catch (Exception e) { done.TrySetException(e); }
        });
        var hit = await done.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        if (hit is not { } h) throw new LeapError("nothing of a HUD there");
        var surface = HudView.Surface(h);
        bool touchable = _view.Touchable(h);
        var o = new JsonObject { ["touched"] = h.Id.ToString(), ["name"] = NameOf(w, h.Id) is { Length: > 0 } n ? n : NameOf(w, h.RootId) };
        if (!touchable)
        {
            o["note"] = "it takes no touches (nothing scripted listens there)";
            return o;
        }
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
}
