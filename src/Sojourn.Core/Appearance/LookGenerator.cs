using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Appearance;

/// <summary>
/// A first outfit for a new resident on our grids, so forty residents aren't forty identical default avatars: a
/// shape (sex, build, face), skin, hair (style, colour, facial hair), eyes and clothes (shirt, pants or skirt, maybe
/// shoes and a jacket, each tinted), drawn from a seed (the account's name by default), as real wearables in the
/// resident's inventory that it can edit like any other. Runs once per resident (<c>look.json</c> in its state dir),
/// only when it wears nothing but the grid's defaults; never on Second Life.
/// </summary>
public sealed class LookGenerator
{
    readonly AgentSession _s;
    public LookGenerator(AgentSession s) => _s = s;

    // OpenSim's default wearables (AvatarWearable.cs): shape, skin, hair, eyes, shirt, pants
    static readonly HashSet<UUID> GridDefaults = new()
    {
        new("66c41e39-38f9-f75a-024e-585989bfab73"), new("77c41e39-38f9-f75a-024e-585989bbabbb"),
        new("d342e6c0-b9d2-11dc-95ff-0800200c9a66"), new("4bb6fa4d-1cd2-498a-a84c-95c1a0e745a7"),
        new("00000000-38f9-1111-024e-222222111110"), new("00000000-38f9-1111-024e-222222111120"),
    };

    string Marker => Path.Combine(_s.StateDir, "look.json");

    public async Task DressIfNeededAsync(GridClient c, CancellationToken ct)
    {
        if (File.Exists(Marker)) return;
        var worn = await _s.Look.ReadWornAsync(c, ct);
        var wearables = worn.Where(w => w.IsWearable).ToList();
        var defaultsOnly = wearables.All(w => GridDefaults.Contains(w.AssetId)) || !wearables.Any(w => w.Type == WearableType.Shape);
        if (!defaultsOnly)
        {
            await File.WriteAllTextAsync(Marker, new JsonObject { ["kept"] = "the account already had its own look", ["at"] = Json.UnixNow() }.ToJsonString(), ct);
            return;
        }
        await DressAsync(c, _s.Config.LoginName, ct);
    }

    /// <summary>Makes a whole new look from a seed and wears it in place of every worn wearable.</summary>
    public async Task<JsonObject> DressAsync(GridClient c, string seed, CancellationToken ct)
    {
        var rng = new Random(StableHash(seed));
        var first = _s.Config.First;
        var outfit = Design(rng, first, c.Self.AgentID);
        var items = new List<InventoryItem>();
        var made = new JsonObject();
        foreach (var w in outfit)
        {
            var item = await _s.Wardrobe.CreateItemAsync(c, w, null, ct);
            items.Add(item);
            _s.Log.LogInformation("look: made {Type} {Item}", w.Type, item.UUID);
            made[Slots.TypeName(w.Type)] = item.UUID.ToString();
        }
        // the new look replaces every worn wearable (attachments stay)
        var worn = await _s.Look.ReadWornAsync(c, ct);
        var off = worn.Where(w => w.IsWearable && w.LinkId != w.ItemId).Select(w => w.LinkId).Concat(_s.Look.BrokenLinks).ToList();
        if (off.Count > 0) await _s.Look.UnlinkAsync(c, off, ct);
        await _s.Wardrobe.WearAsync(c, items, replace: false, ct);
        var o = new JsonObject { ["seed"] = seed, ["items"] = made, ["at"] = Json.UnixNow() };
        await File.WriteAllTextAsync(Marker, o.ToJsonString(), ct);
        _s.Log.LogInformation("dressed in a generated look ({Count} wearables, seed {Seed})", items.Count, seed);
        return o;
    }

    static int StableHash(string s)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var ch in s) h = (h ^ ch) * 16777619;
            return h;
        }
    }

    static double N(Random r, double sd) => sd * Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
    static double U(Random r, double a, double b) => a + (b - a) * r.NextDouble();

    static void Set(Wearable w, int id, double v)
    {
        if (!VisualParams.Params.TryGetValue(id, out var p)) return;
        if (!w.Params.ContainsKey(id)) return;   // not a param of this wearable type
        w.Params[id] = (float)Math.Clamp(v, p.MinValue, p.MaxValue);
    }

    static void Tint(Wearable w, (double R, double G, double B) c)
    {
        if (Slots.TintParams(w.Type) is not { } tp) return;
        w.Params[tp[0]] = (float)c.R; w.Params[tp[1]] = (float)c.G; w.Params[tp[2]] = (float)c.B;
    }

    static (double, double, double) Hsv(double h, double s, double v)
    {
        h = (h % 1 + 1) % 1 * 6;
        var i = (int)h;
        var f = h - i;
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
    }

    /// <summary>Clothing colours people actually wear: mostly muted, some neutrals, a few bright.</summary>
    static (double, double, double) ClothColor(Random r)
    {
        var k = r.NextDouble();
        if (k < 0.25) { var v = U(r, 0.08, 0.95); return (v, v, v); }                       // black, greys, white
        if (k < 0.40) return Hsv(U(r, 0.55, 0.65), U(r, 0.35, 0.7), U(r, 0.2, 0.55));          // denim and navy
        if (k < 0.55) return Hsv(U(r, 0.06, 0.12), U(r, 0.25, 0.6), U(r, 0.25, 0.75));         // browns, khaki, tan
        if (k < 0.85) return Hsv(r.NextDouble(), U(r, 0.25, 0.6), U(r, 0.3, 0.8));             // muted colours
        return Hsv(r.NextDouble(), U(r, 0.65, 0.95), U(r, 0.6, 0.95));                          // bright
    }

    static List<Wearable> Design(Random r, string first, UUID me)
    {
        var male = r.NextDouble() < 0.5;
        Wearable New(WearableType t, string what) => Wearable.New(t, $"{first}'s {what}", me);

        var shape = New(WearableType.Shape, "Shape");
        Set(shape, 80, male ? 1 : 0);
        Set(shape, 33, N(r, 0.55) + (male ? 0.3 : -0.1));   // height
        Set(shape, 34, N(r, 0.35));                          // body thickness
        Set(shape, 36, N(r, 0.35) + (male ? 0.2 : -0.2));   // shoulders
        Set(shape, 37, N(r, 0.45) + (male ? -0.2 : 0.3));   // hip width
        Set(shape, 38, N(r, 0.25));                          // torso length
        Set(shape, 692, N(r, 0.25));                         // leg length
        Set(shape, 693, N(r, 0.2));                          // arm length
        Set(shape, 683, N(r, 0.1));                          // neck thickness
        Set(shape, 756, N(r, 0.3));                          // neck length
        Set(shape, 682, 0.5 + N(r, 0.12));                   // head size
        if (!male) Set(shape, 105, U(r, 0.25, 0.8));         // breast size
        foreach (var (id, sd) in new[] { (1, .35), (2, .45), (4, .3), (10, .5), (14, .3), (17, .3), (18, .5), (20, .35), (24, .4),
                     (25, .35), (35, .4), (155, .35), (185, .3), (196, .3), (505, .25), (650, .35), (653, .4), (656, .3), (665, .4),
                     (760, .4), (764, .3), (796, .2), (799, .25), (11, .35), (517, .3), (8, .3), (23, .25) })
            if (VisualParams.Params.TryGetValue(id, out var p)) Set(shape, id, p.DefaultValue + N(r, sd));

        var skin = New(WearableType.Skin, "Skin");
        Set(skin, 111, Math.Pow(r.NextDouble(), 2));     // pigment, light to dark (dark from ~0.4 up)
        Set(skin, 110, U(r, 0, 0.06));                    // red skin
        Set(skin, 116, U(r, 0, 0.35));                    // rosy complexion
        Set(skin, 117, U(r, 0.1, 0.5));                   // lip pinkness
        Set(skin, 150, U(r, 0, 0.6));                     // body definition
        Set(skin, 162, U(r, 0.2, 0.8));                   // facial definition
        if (r.NextDouble() < 0.2) { Set(skin, 165, U(r, 0.1, 0.6)); Set(skin, 775, U(r, 0, 0.4)); }   // freckles
        if (!male && r.NextDouble() < 0.4) { Set(skin, 701, U(r, 0.15, 0.5)); Set(skin, 700, r.NextDouble()); }   // lipstick

        var hair = New(WearableType.Hair, "Hair");
        // colour: the defaults are blonde .5 (a mid brown); black is blonde ~0
        var shade = r.NextDouble();
        if (shade < 0.2) Set(hair, 114, U(r, 0, 0.12));                                          // black
        else if (shade < 0.45) Set(hair, 114, U(r, 0.15, 0.4));                                  // dark brown
        else if (shade < 0.6) Set(hair, 114, U(r, 0.4, 0.6));                                    // brown
        else if (shade < 0.75) Set(hair, 114, U(r, 0.7, 1));                                     // blonde
        else if (shade < 0.87) { Set(hair, 113, U(r, 0.35, 0.9)); Set(hair, 114, U(r, 0.2, 0.5)); }   // red, auburn
        else if (shade < 0.95) { Set(hair, 115, U(r, 0.4, 0.95)); Set(hair, 114, U(r, 0.2, 0.5)); }   // grey, white
        else Set(hair, 112, U(r, 0.4, 1));                                                       // dyed
        // style: around the default cut (which is a full head of hair); the ranges keep everyone's head covered
        var longish = male ? r.NextDouble() < 0.15 : r.NextDouble() < 0.75;
        Set(hair, 133, U(r, 0.15, 0.6));                                     // front
        Set(hair, 134, longish ? U(r, 0.55, 0.95) : U(r, 0.35, 0.6));        // sides
        Set(hair, 135, longish ? U(r, 0.65, 1) : U(r, 0.35, 0.6));           // back
        Set(hair, 130, U(r, 0.25, 0.75)); Set(hair, 131, U(r, 0.3, 0.7));    // front and side fringe
        Set(hair, 132, longish ? U(r, 0.5, 1) : U(r, 0.2, 0.5));             // back fringe
        Set(hair, 763, U(r, 0.45, 0.8)); Set(hair, 752, U(r, 0.4, 0.8));     // volume, thickness
        Set(hair, 182, 0.7 + N(r, 0.15)); Set(hair, 181, 0.14 + N(r, 0.2)); Set(hair, 183, 0.05 + N(r, 0.2));   // big top, front, back
        Set(hair, 143, 0.125 + N(r, 0.25));                                  // full sides
        Set(hair, 136, U(r, 0.2, 0.8)); Set(hair, 137, U(r, 0.3, 0.7));      // sweep, tilt
        if (r.NextDouble() < 0.3) Set(hair, r.NextDouble() < 0.5 ? 140 : r.NextDouble() < 0.5 ? 141 : 142, U(r, 0.5, 1.5));   // a part
        Set(hair, 177, r.NextDouble() < 0.25 ? U(r, 0.2, 0.8) : 0);          // rumpled
        Set(hair, 119, U(r, 0.3, 0.8)); Set(hair, 750, U(r, 0.4, 0.9));      // eyebrows
        if (!male && longish && r.NextDouble() < 0.25) Set(hair, r.NextDouble() < 0.6 ? 789 : 785, U(r, 0.6, 1));   // ponytail, pigtails
        if (male && r.NextDouble() < 0.35)
        {
            Set(hair, 166, U(r, 0.3, 1)); Set(hair, 167, U(r, 0.2, 1));
            if (r.NextDouble() < 0.5) Set(hair, 169, U(r, 0.3, 1)); else Set(hair, 168, U(r, 0.2, 0.8));
        }

        var eyes = New(WearableType.Eyes, "Eyes");
        Set(eyes, 99, r.NextDouble());
        Set(eyes, 98, U(r, 0, 0.6));

        var list = new List<Wearable> { shape, skin, hair, eyes };

        var shirt = New(WearableType.Shirt, "Shirt");
        Tint(shirt, ClothColor(r));
        Set(shirt, 800, U(r, 0.15, 1)); Set(shirt, 801, U(r, 0.3, 1)); Set(shirt, 802, U(r, 0, 1)); Set(shirt, 781, U(r, 0, 1));
        Set(shirt, 828, U(r, 0, 0.6));
        list.Add(shirt);

        if (!male && r.NextDouble() < 0.35)
        {
            var skirt = New(WearableType.Skirt, "Skirt");
            Tint(skirt, ClothColor(r));
            Set(skirt, 858, U(r, 0.25, 0.9)); Set(skirt, 863, U(r, 0, 0.6));
            list.Add(skirt);
        }
        else
        {
            var pants = New(WearableType.Pants, "Pants");
            Tint(pants, ClothColor(r));
            Set(pants, 815, U(r, 0.55, 1)); Set(pants, 816, U(r, 0, 0.5)); Set(pants, 814, U(r, 0.2, 0.8));
            list.Add(pants);
        }
        if (r.NextDouble() < 0.7)
        {
            var shoes = New(WearableType.Shoes, "Shoes");
            var v = U(r, 0.05, 0.5);
            Tint(shoes, r.NextDouble() < 0.7 ? (v, v * 0.9, v * 0.8) : ClothColor(r));
            Set(shoes, 616, U(r, 0, 0.6));
            if (!male) Set(shoes, 198, U(r, 0, 0.6));
            list.Add(shoes);
        }
        if (r.NextDouble() < 0.25)
        {
            var jacket = New(WearableType.Jacket, "Jacket");
            Tint(jacket, ClothColor(r));
            Set(jacket, 609, U(r, 0.3, 1)); Set(jacket, 606, U(r, 0.6, 1)); Set(jacket, 608, U(r, 0, 0.7));
            list.Add(jacket);
        }
        return list;
    }
}
