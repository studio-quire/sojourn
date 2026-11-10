using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>What About Land knows of one parcel while it's shown: SojournLand's `parcel` answer, and what each tab reads
/// besides when it's chosen (the allowed and banned lists, who owns the objects, the region, the covenant), with where
/// the person is from it (worked out apart, from the parcel's cells). The parcel is pinned by its id, so a change acts
/// on the parcel shown even if she has walked off it.</summary>
public sealed class LandLook
{
    public required JsonObject Parcel { get; set; }
    public required string Region { get; init; }
    public required UUID Me { get; init; }
    public IReadOnlyList<(UUID Id, string Name)> Groups { get; init; } = Array.Empty<(UUID, string)>();
    public JsonObject? Access { get; set; }
    public string? AccessWhy { get; set; }
    public JsonObject? Objects { get; set; }
    public string? ObjectsWhy { get; set; }
    public JsonObject? RegionInfo { get; set; }
    public JsonObject? Covenant { get; set; }
    public string? Where { get; set; }
    public string? Stats { get; set; }
    /// <summary>The estate's lists and switches (SojournLand estate), for its owner and managers; else why not.</summary>
    public JsonObject? Estate { get; set; }
    public string? EstateWhy { get; set; }
    /// <summary>Whether she's the estate's owner or a manager here (EstateOps.CanManage, as the region's handshake says).</summary>
    public bool ManagesEstate { get; set; }
    /// <summary>When a restart she asked for in this region goes, while it counts down.</summary>
    public DateTime? RestartAt { get; set; }

    public string Name => Parcel.Str("name") is { Length: > 0 } n ? n : "an unnamed parcel";
    public int LocalId => Parcel.Int("local_id", 0);
    public UUID OwnerId => UUID.TryParse(Parcel.Str("owner_id"), out var id) ? id : UUID.Zero;
    public bool GroupOwned => Parcel.Bool("group_owned", false);
    public bool Mine => !GroupOwned && OwnerId == Me;
    /// <summary>Hers, or her group's (whose powers only the region knows: it's asked).</summary>
    public bool MayManage => Mine || GroupOwned;
    public bool Option(string key) => Parcel["options"] is JsonObject o && o.Bool(key, false);
    public JsonObject? Sale => Parcel["for_sale"] as JsonObject;
    public JsonObject? Pass => Parcel["pass"] as JsonObject;
    public bool Manages => RegionInfo?.Bool("i_manage", false) == true;
    /// <summary>"you", "Estate Keeper", "the group Builders".</summary>
    public string OwnerWords => Mine ? "you" : Parcel.Str("owner") is { Length: > 0 } o ? o : "not known";
    /// <summary>"yours", "owned by Estate Keeper".</summary>
    public string Whose => Mine ? "yours" : $"owned by {OwnerWords}";
}

/// <summary>Someone on the parcel's allowed or banned list, as a row of the Access tab.</summary>
public sealed record LandPerson(UUID Id, string Name, bool Banned, string? Until);

/// <summary>Someone with objects on the parcel, as a row of the Objects tab.</summary>
public sealed record LandOwner(UUID Id, string Name, int Prims, bool Group, string Newest);

/// <summary>
/// About Land's vocabulary (docs/access-app.md > About Land): the official viewer's floater tabs (General, Covenant,
/// Objects, Options, Media, Sound, Access) and World > Region/Estate's Region tab, each setting a row under the
/// viewer's own words where they're plain, and the same words typed as a command ("name Our Garden", "fly off",
/// "category residential", "auto return 10", "passes on"). Rows are <see cref="BuildRow"/>s, so the list's keys work
/// as the build editor's (Enter changes, Right and Left step). Pure words over a <see cref="LandLook"/>; a change comes
/// back as the op and its arguments, and whether her confirmation comes first.
/// </summary>
public static class LandRows
{
    public static readonly string[] Tabs = { "General", "Covenant", "Objects", "Options", "Media", "Sound", "Access", "Region", "Estate" };

    static readonly string[] Routing = { "blocked", "landing point", "anywhere" };
    static readonly string[] Maturities = { "general", "moderate", "adult" };

    sealed record Def(string Key, string Tab, string Label, RowKind Kind, string[] Aliases, Func<LandLook, bool> Shows)
    {
        /// <summary>Her confirmation in Notices first, even for her own key: who may enter or reshape the land.</summary>
        public bool Confirm { get; init; }
    }

    static bool All(LandLook _) => true;
    static bool Managing(LandLook l) => l.Manages;
    static bool NotManaging(LandLook l) => !l.Manages;

    /// <summary>The parcel's switches: SojournLand set's option names, as About Land words them.</summary>
    static readonly (string Key, string Tab, string Label, string[] Aliases, bool Confirm)[] Switches =
    {
        ("terraform", "Options", "Anyone can edit terrain", new[] { "anyone can edit terrain", "edit terrain", "terraform", "terraforming" }, true),
        ("fly", "Options", "Anyone can fly", new[] { "anyone can fly", "fly", "flying" }, false),
        ("object_entry", "Options", "Anyone's objects can enter", new[] { "anyone's objects can enter", "object entry", "objects enter" }, false),
        ("scripts", "Options", "Anyone's scripts run", new[] { "anyone's scripts run", "run scripts", "scripts" }, false),
        ("landmark", "Options", "Anyone can make landmarks", new[] { "anyone can make landmarks", "landmarks", "landmark" }, false),
        ("group_build", "Options", "The group can build", new[] { "the group can build", "group can build", "group build" }, false),
        ("group_object_entry", "Options", "The group's objects can enter", new[] { "the group's objects can enter", "group object entry", "group objects" }, false),
        ("group_scripts", "Options", "The group's scripts run", new[] { "the group's scripts run", "group scripts" }, false),
        ("damage", "Options", "Damage allowed", new[] { "damage allowed", "damage" }, false),
        ("push", "Options", "Pushing allowed", new[] { "pushing allowed", "pushing", "push" }, false),
        ("show_in_search", "Options", "Show in search", new[] { "show in search", "show place in search", "search" }, false),
        ("sound_local", "Sound", "Sounds kept to this parcel", new[] { "sounds kept to this parcel", "sound local", "sounds local", "restrict sounds" }, false),
        ("voice", "Sound", "Voice", new[] { "voice" }, false),
        ("voice_parcel_only", "Sound", "Voice kept to this parcel", new[] { "voice kept to this parcel", "voice parcel only", "private voice" }, false),
        ("access_list", "Access", "Only the allowed list", new[] { "only the allowed list", "allowed list only", "access list", "restrict access" }, true),
        ("access_group", "Access", "The group may enter", new[] { "the group may enter", "group access" }, true),
        ("pass_list", "Access", "Passes sold", new[] { "passes sold", "sell passes", "passes" }, true),
        ("deny_no_payment_info", "Access", "No one without payment info", new[] { "no one without payment info", "payment info" }, true),
        ("deny_under_18", "Access", "No one under 18", new[] { "no one under 18", "under 18", "age verified" }, true),
    };

    /// <summary>The Region tab's switches (SojournLand setRegion's names), for the estate's owner and managers.</summary>
    static readonly (string Key, string Label, string[] Aliases)[] RegionSwitches =
    {
        ("block_terraform", "Terraforming blocked", new[] { "terraforming blocked", "block terraform" }),
        ("block_fly", "Flying blocked", new[] { "flying blocked", "block fly" }),
        ("block_fly_over", "Flying over blocked", new[] { "flying over blocked", "block fly over" }),
        ("allow_damage", "Damage allowed in the region", new[] { "damage allowed in the region", "region damage" }),
        ("restrict_push", "Pushing restricted in the region", new[] { "pushing restricted in the region", "region push", "restrict push" }),
        ("allow_land_resell", "Land can be resold", new[] { "land can be resold", "resell", "land resale" }),
        ("allow_parcel_changes", "Parcels can be joined and divided", new[] { "parcels can be joined and divided", "join and divide", "parcel changes" }),
        ("block_parcel_search", "Parcels hidden from search", new[] { "parcels hidden from search", "block parcel search" }),
    };

    static readonly Def[] Defs = MakeDefs();

    static Def[] MakeDefs()
    {
        var d = new List<Def>
        {
            new("name", "General", "Name", RowKind.Text, new[] { "name" }, All),
            new("desc", "General", "Description", RowKind.Text, new[] { "description", "desc" }, All),
            new("owner", "General", "Owner", RowKind.Info, new[] { "owner" }, All),
            new("group", "General", "Group", RowKind.Choice, new[] { "group" }, All) { Confirm = true },
            new("area", "General", "Area", RowKind.Info, new[] { "area", "size" }, All),
            new("where", "General", "Where", RowKind.Info, new[] { "where" }, All),
            new("region", "General", "Region", RowKind.Info, Array.Empty<string>(), All),
            new("sale", "General", "For sale", RowKind.Text, new[] { "not for sale", "for sale", "sale" }, All) { Confirm = true },
            new("buy", "General", "Buy this land", RowKind.Info, Array.Empty<string>(), l => !l.Mine && l.Sale != null),
            new("abandon", "General", "Abandon land", RowKind.Info, Array.Empty<string>(), l => l.MayManage),
            new("deed", "General", "Deed to the group", RowKind.Info, Array.Empty<string>(), l => l.Mine),
            // Objects
            new("prims", "Objects", "Land impact", RowKind.Info, new[] { "land impact", "prims" }, All),
            new("prims_owner", "Objects", "The owner's", RowKind.Info, Array.Empty<string>(), All),
            new("prims_group", "Objects", "Set to the group", RowKind.Info, Array.Empty<string>(), All),
            new("prims_others", "Objects", "Others'", RowKind.Info, Array.Empty<string>(), All),
            new("auto_return", "Objects", "Auto return of others' objects", RowKind.Number, new[] { "auto return", "autoreturn", "auto-return" }, All),
            // Options
            new("category", "Options", "Category", RowKind.Choice, new[] { "category" }, All),
            new("see_avatars", "Options", "Seen and heard from other parcels", RowKind.Switch,
                new[] { "seen and heard from other parcels", "avatars seen from outside", "see avatars", "seen from outside" }, All),
            new("routing", "Options", "Teleport routing", RowKind.Choice, new[] { "teleport routing", "routing" }, All),
            new("landing", "Options", "Landing point", RowKind.Info, new[] { "landing point", "landing" }, All),
            // Media, Sound
            new("media_url", "Media", "Media URL", RowKind.Text, new[] { "media url", "media" }, All),
            new("music_url", "Sound", "Music URL", RowKind.Text, new[] { "music url", "music" }, All),
            // Access
            new("open_to", "Access", "Open to", RowKind.Info, new[] { "open to" }, All),
            new("pass_price", "Access", "Pass price", RowKind.Number, new[] { "pass price" }, l => l.Option("pass_list")) { Confirm = true },
            new("pass_hours", "Access", "Pass hours", RowKind.Number, new[] { "pass hours" }, l => l.Option("pass_list")) { Confirm = true },
            new("buy_pass", "Access", "Buy a pass", RowKind.Info, Array.Empty<string>(), l => !l.Mine && l.Option("pass_list")),
            // Region
            new("r.name", "Region", "Region", RowKind.Info, Array.Empty<string>(), All),
            // ("estate" typed is the Estate tab's)
            new("r.estate", "Region", "Estate", RowKind.Info, Array.Empty<string>(), All),
            new("r.maturity", "Region", "Maturity", RowKind.Choice, new[] { "maturity", "rating" }, Managing),
            new("r.maturity_info", "Region", "Maturity", RowKind.Info, Array.Empty<string>(), NotManaging),
            new("r.agents", "Region", "People at most", RowKind.Number, new[] { "people at most", "agent limit" }, Managing),
            new("r.agents_info", "Region", "People at most", RowKind.Info, Array.Empty<string>(), NotManaging),
            new("r.now", "Region", "Now", RowKind.Info, Array.Empty<string>(), All),
            new("r.manage", "Region", "You manage it", RowKind.Info, Array.Empty<string>(), All),
            new("r.water", "Region", "Water height", RowKind.Info, new[] { "water height", "water" }, All),
        };
        // the switches go where About Land has them, before the rows after them on their tab
        foreach (var s in Switches)
        {
            var def = new Def(s.Key, s.Tab, s.Label, RowKind.Switch, s.Aliases, All) { Confirm = s.Confirm };
            int at = s.Tab switch
            {
                "Options" => d.FindIndex(x => x.Key == "category"),
                "Access" => d.FindIndex(x => x.Key == "pass_price"),
                _ => -1,
            };
            if (at < 0) d.Add(def); else d.Insert(at, def);
        }
        foreach (var s in RegionSwitches)
        {
            d.Add(new Def("r." + s.Key, "Region", s.Label, RowKind.Switch, s.Aliases, Managing) { Confirm = true });
            d.Add(new Def("r." + s.Key + "_info", "Region", s.Label, RowKind.Info, Array.Empty<string>(), NotManaging));
        }
        return d.ToArray();
    }

    static readonly (string Alias, Def Def)[] ByAlias = Defs.SelectMany(d => d.Aliases.Select(a => (a, d)))
        .OrderByDescending(x => x.a.Length).ToArray();

    public static string TabOf(string key) => Defs.FirstOrDefault(d => d.Key == key)?.Tab ?? "General";

    /// <summary>A typed command's setting and the words after its name ("fly off": fly and "off"); null when it names
    /// none. The value keeps its case (a name as typed).</summary>
    public static (string Key, string Value)? Match(string command)
    {
        var t = command.Trim();
        var lower = t.ToLowerInvariant();
        foreach (var (alias, def) in ByAlias)
        {
            if (!lower.StartsWith(alias, StringComparison.Ordinal)) continue;
            if (lower.Length > alias.Length && !char.IsWhiteSpace(lower[alias.Length]) && lower[alias.Length] != ':') continue;
            var value = t[alias.Length..].TrimStart(':', ' ', '\t');
            if (alias == "not for sale") value = "no";
            if (value.StartsWith("to ", StringComparison.OrdinalIgnoreCase) && def.Kind != RowKind.Text) value = value[3..];
            return (def.Key, value);
        }
        return null;
    }

    /// <summary>A tab's rows for this parcel: its settings, then what the tab lists (the covenant's lines, who owns the
    /// objects, who's allowed and banned). Each: its words, a key that stays the same while its words change, and what
    /// it is (a <see cref="BuildRow"/>, a <see cref="LandPerson"/>, a <see cref="LandOwner"/>, or a line's text).</summary>
    public static List<(string Words, string Key, object Tag)> For(LandLook l, string tab)
    {
        var items = Defs.Where(d => d.Tab == tab && d.Shows(l)).Select(d => Row(l, d)).Select(r => (r.Words, r.Key, (object)r)).ToList();
        switch (tab)
        {
            case "Covenant":
                items.AddRange(CovenantRows(l));
                break;
            case "Objects":
                if (l.Objects?["owners"] is JsonArray owners)
                {
                    var list = owners.OfType<JsonObject>().Select(o => new LandOwner(UUID.TryParse(o.Str("owner_id"), out var id) ? id : UUID.Zero,
                        o.Str("name") ?? "someone", o.Int("prims", 0), o.Bool("group_owned", false), o.Str("newest") ?? "")).ToList();
                    items.Add(($"Who owns them: {(list.Count == 0 ? "nobody, nothing is here" : Count(list.Count, "owner"))}", "owners", "owners"));
                    items.AddRange(list.Select(o => (OwnerWords(l, o), "owner." + o.Id, (object)o)));
                }
                else items.Add(($"Who owns them: {l.ObjectsWhy ?? "not read yet; F5 reads them"}", "owners", "owners"));
                break;
            case "Access":
                if (l.Access is { } a)
                {
                    foreach (var (field, banned) in new[] { ("allowed", false), ("banned", true) })
                    {
                        var people = (a[field] as JsonArray ?? new()).OfType<JsonObject>()
                            .Select(o => new LandPerson(UUID.TryParse(o.Str("id"), out var id) ? id : UUID.Zero, o.Str("name") ?? "someone", banned, o.Str("until"))).ToList();
                        var title = banned ? "Banned" : "Allowed";
                        items.Add(($"{title} list: {(people.Count == 0 ? "nobody" : Count(people.Count, "person", "people"))}", field, field));
                        items.AddRange(people.Select(p => (PersonWords(p), $"{field}.{p.Id}", (object)p)));
                    }
                }
                else items.Add(($"Allowed and banned lists: {l.AccessWhy ?? "not read yet; F5 reads them"}", "lists", "lists"));
                break;
            case "Estate":
                items.AddRange(EstateRows.For(l));
                break;
        }
        return items;
    }

    static IEnumerable<(string, string, object)> CovenantRows(LandLook l)
    {
        if (l.Covenant is not { } c) { yield return ("Covenant: not read yet; F5 reads it", "cov", "cov"); yield break; }
        var text = (c.Str("covenant") ?? "").Trim();
        if (text.StartsWith("none:", StringComparison.Ordinal)) { yield return ("Covenant: none, the estate has no covenant", "cov", "cov"); yield break; }
        var lines = CovenantLines(text);
        yield return ($"Covenant: {Count(lines.Count, "line")}{(c.Str("changed") is { } ch ? $", last changed {ch}" : "")}", "cov", "cov");
        for (int i = 0; i < lines.Count; i++) yield return (lines[i], $"cov.{i}", lines[i]);
    }

    public static List<string> CovenantLines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    public static string PersonWords(LandPerson p) => $"{(p.Banned ? "Banned" : "Allowed")}: {p.Name}{(p.Until is { } u ? $", until {u}" : "")}";

    static string OwnerWords(LandLook l, LandOwner o) =>
        $"{(o.Id == l.Me ? "You" : o.Group ? "Set to a group" : o.Name)}: {Count(o.Prims, "prim")}{(o.Newest.Length > 0 ? $", the newest from {o.Newest}" : "")}";

    static string Count(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";

    /// <summary>One setting's row, shown or not (a command can read any).</summary>
    public static BuildRow? Row(LandLook l, string key) => Defs.FirstOrDefault(d => d.Key == key) is { } d ? Row(l, d) : null;

    static BuildRow Row(LandLook l, Def d)
    {
        var p = l.Parcel;
        BuildRow R(string value, string now = "", string ask = "") =>
            new BuildRow(d.Key, $"{d.Label}: {value}", d.Kind) { Label = d.Label, Now = now.Length > 0 ? now : value, Ask = ask.Length > 0 ? ask : d.Label, Gate = d.Confirm ? "permissions" : null };
        BuildRow Sw(bool on) => R(on ? "on" : "off") with { Value = on ? 1 : 0 };
        BuildRow Pick(string[] names, int chosen) =>
            R(chosen >= 0 && chosen < names.Length ? names[chosen] : "not known", chosen >= 0 && chosen < names.Length ? names[chosen] : "",
                $"{d.Label} ({string.Join(", ", names)})") with { Choices = names, Chosen = chosen };
        var ri = l.RegionInfo;
        var rs = ri?["settings"] as JsonObject;
        if (Switches.Any(s => s.Key == d.Key)) return Sw(l.Option(d.Key));
        if (d.Key.StartsWith("r.") && RegionSwitches.FirstOrDefault(s => "r." + s.Key == d.Key || "r." + s.Key + "_info" == d.Key) is { Key: { } rk })
            return rs == null ? R("not read yet") : d.Kind == RowKind.Switch ? Sw(rs.Bool(rk, false)) : R(rs.Bool(rk, false) ? "on" : "off");
        switch (d.Key)
        {
            case "name": return R(l.Name, p.Str("name") ?? "");
            case "desc": return R(p.Str("desc") is { Length: > 0 } ds ? ds : "none", p.Str("desc") ?? "");
            case "owner": return R(l.Mine ? "you" : l.OwnerWords);
            case "group":
            {
                var names = new[] { "none" }.Concat(l.Groups.Select(g => g.Name)).ToArray();
                var now = p.Str("group") is { Length: > 0 } gn && gn != "none" ? (UUID.TryParse(gn, out _) ? "a group you're not in" : gn) : "none";
                return R(now, now, "Group (none, or one of your groups by name)") with { Choices = names, Chosen = Array.IndexOf(names, now) };
            }
            case "area": return R(AreaWords(p));
            case "where": return R(l.Where ?? "not worked out yet");
            case "region":
                return R(ri?.Str("maturity") is { Length: > 0 } m ? $"{l.Region}, {m.ToLowerInvariant()}" : l.Region);
            case "sale":
                return R(SaleWords(l), l.Sale is { } s ? $"{s.Int("price", 0)}" : "no", "For sale (a price in L$, and to NAME or with objects; or no)");
            case "buy":
                return R(l.Sale is { } bs ? $"L${bs.Int("price", 0)}{(bs.Str("buyer") is { } who ? (UUID.TryParse(bs.Str("buyer_id"), out var bid) && bid == l.Me ? ", for sale to you" : $", for sale only to {who}") : "")}; Enter to buy it, after your confirmation" : "not for sale");
            case "abandon": return R("Enter gives it back to the estate, after your confirmation");
            case "deed": return R(p.Str("group") is { Length: > 0 } dg && dg != "none" ? $"Enter gives it to {dg}, after your confirmation" : "it has no group; set one first");
            case "prims":
                return p["prims"] is JsonObject pr ? R($"{pr.Int("used", 0)} of {pr.Int("max", 0)} used") : R("not known");
            case "prims_owner": return R($"{(p["prims"] as JsonObject)?.Int("owner", 0) ?? 0}");
            case "prims_group": return R($"{(p["prims"] as JsonObject)?.Int("group", 0) ?? 0}");
            case "prims_others": return R($"{(p["prims"] as JsonObject)?.Int("others", 0) ?? 0}");
            case "auto_return":
            {
                int mins = p.Int("auto_return_minutes", 0);
                return R(mins <= 0 ? "off" : mins == 1 ? "after 1 minute" : $"after {mins} minutes", $"{mins}", "Auto return of others' objects, in minutes (0: off)") with { Value = mins, Step = 5 };
            }
            case "category":
            {
                var cat = p.Str("category") ?? "none";
                return Pick(ParcelOps.Categories, Array.IndexOf(ParcelOps.Categories, cat));
            }
            case "see_avatars": return Sw(p.Bool("see_avatars", true));
            case "routing":
                return Pick(Routing, p.Str("landing") switch { "point" => 1, "anywhere" => 2, "blocked" => 0, _ => -1 });
            case "landing":
                return R(p.Str("landing") == "point" && p["landing_point"] is JsonArray lp && lp.Count >= 3
                    ? $"{Xyz(lp)}; Enter moves it to where you stand" : "none; Enter makes one where you stand");
            case "media_url": return R(p.Str("media_url") is { Length: > 0 } mu ? mu : "none", p.Str("media_url") ?? "");
            case "music_url": return R(p.Str("music_url") is { Length: > 0 } su ? su : "none", p.Str("music_url") ?? "");
            case "open_to": return R(OpenTo(l));
            case "pass_price":
                return R(l.Pass is { } pp ? $"L${pp.Int("price", 0)}" : "not known", $"{l.Pass?.Int("price", 0) ?? 0}", "Pass price in L$") with { Value = l.Pass?.Int("price", 0) ?? 0, Step = 5 };
            case "pass_hours":
            {
                double h = l.Pass?.Num("hours") ?? 1;
                return R(h == 1 ? "1 hour" : $"{N(h)} hours", N(h), "Pass hours") with { Value = h, Step = 0.5 };
            }
            case "buy_pass":
                return R(l.Pass is { } bp ? $"L${bp.Int("price", 0)} for {(bp.Num("hours") is 1 ? "1 hour" : $"{N(bp.Num("hours") ?? 1)} hours")}; Enter to buy one, after your confirmation" : "not sold here");
            case "r.name":
                return R(ri == null ? l.Region : $"{ri.Str("name") ?? l.Region}{(ri.Str("type") is { Length: > 0 } t ? $", {t}" : "")}");
            case "r.estate":
                return R(l.Covenant is { } ce ? $"{ce.Str("estate") ?? "not known"}, owned by {ce.Str("estate_owner") ?? "someone not known"}" : "not read yet");
            case "r.maturity" or "r.maturity_info":
            {
                var mat = (ri?.Str("maturity") ?? "").ToLowerInvariant() switch { "pg" or "general" => "general", "mature" or "moderate" => "moderate", "adult" => "adult", var x => x };
                return d.Kind == RowKind.Choice ? Pick(Maturities, Array.IndexOf(Maturities, mat)) : R(mat.Length > 0 ? mat : "not read yet");
            }
            case "r.agents" or "r.agents_info":
            {
                int lim = ri?.Int("agent_limit", 0) ?? 0;
                return R(lim > 0 ? $"{lim}" : "not read yet", $"{lim}", $"People at most{(ri?.Int("agent_limit_max", 0) is > 0 and var mx ? $" (up to {mx})" : "")}") with { Value = lim, Step = 1 };
            }
            case "r.now": return R(l.Stats ?? "not known");
            case "r.manage": return R(ri == null ? "not read yet" : l.Manages ? "yes, as its estate's owner or a manager" : "no");
            case "r.water": return R(ri?.Num("water_height") is { } wh ? $"{N(wh)} metres" : "not read yet");
        }
        return R("?");
    }

    public static string AreaWords(JsonObject p)
    {
        int area = p.Int("area", 0);
        if (p["bounds"] is not JsonArray b || b.Count < 4) return $"{area} square metres";
        double w = (Json.AsDouble(b[2]) ?? 0) - (Json.AsDouble(b[0]) ?? 0), h = (Json.AsDouble(b[3]) ?? 0) - (Json.AsDouble(b[1]) ?? 0);
        return Math.Abs(w * h - area) < 1
            ? $"{area} square metres, {N(w)} metres east to west by {N(h)} north to south"
            : $"{area} square metres, within {N(w)} metres east to west by {N(h)} north to south";
    }

    public static string SaleWords(LandLook l)
    {
        if (l.Sale is not { } s) return "no";
        var to = s.Str("buyer") is { } who ? UUID.TryParse(s.Str("buyer_id"), out var id) && id == l.Me ? "to you" : $"only to {who}" : "to anyone";
        return $"L${s.Int("price", 0)} {to}{(s.Bool("objects_included", false) ? ", with the objects on it" : "")}";
    }

    public static string OpenTo(LandLook l)
    {
        bool list = l.Option("access_list"), group = l.Option("access_group"), pass = l.Option("pass_list");
        var who = !list && !group ? "everyone" : list && group ? "the allowed list and the group" : list ? "only the allowed list" : "only the group";
        if (pass && (list || group)) who += ", and anyone with a pass";
        var but = new List<string>();
        if (l.Option("deny_no_payment_info")) but.Add("no one without payment info");
        if (l.Option("deny_under_18")) but.Add("no one under 18");
        return but.Count == 0 ? who : $"{who}; {string.Join(", ", but)}";
    }

    // --- a change --------------------------------------------------------------------------------------------------------

    /// <summary>A change to a setting, as an op and its arguments (the parcel's pin added by the caller), and whether her
    /// confirmation comes first; or why not, in words. Sales and the actions (buying, abandoning) aren't here: they're
    /// commands of their own.</summary>
    public static (string? Op, JsonObject? Args, bool Confirm, string? Error) Change(LandLook l, string key, string value)
    {
        var v = value.Trim();
        var def = Defs.FirstOrDefault(d => d.Key == key);
        var label = def?.Label ?? key;
        (string?, JsonObject?, bool, string?) Set(JsonObject o) => ("set", o, def?.Confirm == true, null);
        (string?, JsonObject?, bool, string?) No(string why) => (null, null, false, why);
        if (v.Length == 0 && def?.Kind != RowKind.Text) return No($"{label}: give a value. {Row(l, key)?.Ask}.");
        bool? on = On(v);
        if (Switches.FirstOrDefault(s => s.Key == key) is { Key: { } sk })
            return on is { } so ? Set(new JsonObject { ["options"] = new JsonObject { [sk] = so } }) : No($"{label}: on or off.");
        if (key.StartsWith("r."))
        {
            if (!l.Manages) return No($"{label}: only the estate's owner and its managers may change the region.");
            var rk = key[2..];
            if (RegionSwitches.Any(s => s.Key == rk))
                return on is { } ro ? ("setRegion", new JsonObject { [rk] = ro }, true, null) : No($"{label}: on or off.");
            if (rk == "maturity")
                return Choose(Maturities, v) is { } mi ? ("setRegion", new JsonObject { ["maturity"] = Maturities[mi] }, true, null) : No("Maturity: general, moderate or adult.");
            if (rk == "agents")
                return int.TryParse(Regex.Match(v, @"\d+").Value, out var lim) && lim > 0 ? ("setRegion", new JsonObject { ["agent_limit"] = lim }, true, null) : No("People at most: a number.");
            return No($"{label} can't be changed here.");
        }
        switch (key)
        {
            case "name": return v.Length > 63 ? No("A parcel's name is at most 63 characters.") : Set(new JsonObject { ["name"] = v });
            case "desc": return v.Length > 255 ? No("A parcel's description is at most 255 characters.") : Set(new JsonObject { ["desc"] = v });
            case "media_url" or "music_url":
            {
                var url = v.ToLowerInvariant() is "none" or "no" or "off" or "clear" ? "" : v;
                if (url.Length > 0 && !Uri.TryCreate(url, UriKind.Absolute, out _)) return No($"{label}: a web address (http://...), or none.");
                return Set(new JsonObject { [key] = url });
            }
            case "group":
            {
                var w = v.ToLowerInvariant();
                if (w is "none" or "no" or "no group") return Set(new JsonObject { ["group_id"] = UUID.Zero.ToString() });
                var g = l.Groups.FirstOrDefault(x => x.Name.Equals(v, StringComparison.OrdinalIgnoreCase));
                if (g.Name == null) g = l.Groups.FirstOrDefault(x => x.Name.Contains(v, StringComparison.OrdinalIgnoreCase));
                return g.Name == null ? No($"You're not in a group called {v}. Group: none, or {(l.Groups.Count == 0 ? "a group you're in (you're in none)" : string.Join(", ", l.Groups.Select(x => x.Name)))}.")
                    : Set(new JsonObject { ["group_id"] = g.Id.ToString() });
            }
            case "category":
                return Choose(ParcelOps.Categories, v) is { } ci ? Set(new JsonObject { ["category"] = ParcelOps.Categories[ci] })
                    : No($"Category: {string.Join(", ", ParcelOps.Categories)}.");
            case "see_avatars":
                return on is { } sa ? Set(new JsonObject { ["see_avatars"] = sa }) : No($"{label}: on or off.");
            case "routing":
                return Choose(Routing, v) is { } ri ? Set(new JsonObject { ["landing"] = ri switch { 0 => "blocked", 1 => "here", _ => "anywhere" } })
                    : No("Teleport routing: blocked, landing point (where you stand), or anywhere.");
            case "landing":
            {
                var w = v.ToLowerInvariant();
                return w switch
                {
                    "here" or "set" or "" or "on" => Set(new JsonObject { ["landing"] = "here" }),
                    "anywhere" or "none" or "clear" or "off" => Set(new JsonObject { ["landing"] = "anywhere" }),
                    "blocked" => Set(new JsonObject { ["landing"] = "blocked" }),
                    _ => No("Landing point: here (where you stand), or anywhere to take it away."),
                };
            }
            case "auto_return":
            {
                if (on == false) return Set(new JsonObject { ["auto_return_minutes"] = 0 });
                return int.TryParse(Regex.Match(v, @"\d+").Value, out var mins) && mins >= 0
                    ? Set(new JsonObject { ["auto_return_minutes"] = mins }) : No("Auto return: a number of minutes, or 0 for off.");
            }
            case "pass_price":
                return int.TryParse(Regex.Match(v, @"\d+").Value, out var price) ? Set(new JsonObject { ["pass_price"] = price }) : No("Pass price: a number of L$.");
            case "pass_hours":
                return BuildRows.Numbers(v, false) is { Count: 1 } hrs && hrs[0] > 0 ? Set(new JsonObject { ["pass_hours"] = hrs[0] }) : No("Pass hours: a number of hours.");
        }
        return No($"{label} can't be changed here: it's read from the region.");
    }

    /// <summary>"for sale 500", "for sale 0 to Kestrel", "for sale 1000 with objects", "no": the price, to whom (a name
    /// to look up), with the objects, or not for sale.</summary>
    public static (bool Stop, int Price, string? To, bool Objects, string? Error) Sale(string words)
    {
        var w = words.Trim();
        var lower = w.ToLowerInvariant();
        if (lower is "no" or "not" or "off" or "none" or "stop") return (true, 0, null, false, null);
        bool objects = Regex.IsMatch(lower, @"\bwith (the )?objects\b");
        var to = Regex.Match(w, @"\bto\s+(?<who>.+?)(?:\s+with (the )?objects)?$", RegexOptions.IgnoreCase);
        var price = Regex.Match(lower, @"(?:l\$\s*)?(\d+)");
        if (!price.Success) return (false, 0, null, false, "For sale: a price in L$ (for sale 500, or for sale 0 to NAME), or no.");
        return (false, int.Parse(price.Groups[1].Value, CultureInfo.InvariantCulture), to.Success ? to.Groups["who"].Value.Trim() : null, objects, null);
    }

    static int? Choose(string[] names, string v)
    {
        var w = v.Trim().ToLowerInvariant();
        if (int.TryParse(w, out var i)) return i >= 0 && i < names.Length ? i : null;
        var exact = Array.FindIndex(names, n => n == w);
        if (exact >= 0) return exact;
        var starts = Array.FindIndex(names, n => n.StartsWith(w, StringComparison.Ordinal));
        return starts >= 0 ? starts : null;
    }

    static bool? On(string v)
    {
        var w = v.ToLowerInvariant().TrimEnd('.');
        return w is "on" or "yes" or "true" or "1" or "allowed" or "y" ? true : w is "off" or "no" or "false" or "0" or "n" or "not allowed" ? false : null;
    }

    public static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    static string Xyz(JsonArray a) => $"{N(Math.Round(Json.AsDouble(a[0]) ?? 0))}, {N(Math.Round(Json.AsDouble(a[1]) ?? 0))}, {N(Math.Round(Json.AsDouble(a[2]) ?? 0))}";

    // --- where the parcel is from her ------------------------------------------------------------------------------------

    /// <summary>From a parcel's cells (a bit per 4 m cell, row by row from the south-west, as the region sends them):
    /// whether the point is on it, and the nearest point of its edge (on it) or of it (off it).</summary>
    public static (bool On, float X, float Y)? Nearest(byte[] bitmap, float regionSize, float x, float y)
    {
        int per = (int)(regionSize / 4);
        bool In(int cx, int cy)
        {
            if (cx < 0 || cy < 0 || cx >= per || cy >= per) return false;
            int i = cy * per + cx;
            return i / 8 < bitmap.Length && (bitmap[i / 8] & (1 << (i % 8))) != 0;
        }
        bool on = In((int)Math.Floor(x / 4), (int)Math.Floor(y / 4));
        double best = double.MaxValue;
        (float X, float Y) at = default;
        bool any = false;
        void Consider(float px, float py)
        {
            double d = (px - x) * (px - x) + (py - y) * (py - y);
            if (d < best) { best = d; at = (px, py); }
        }
        for (int cy = 0; cy < per; cy++)
            for (int cx = 0; cx < per; cx++)
            {
                if (!In(cx, cy)) continue;
                any = true;
                float x0 = cx * 4, y0 = cy * 4, x1 = x0 + 4, y1 = y0 + 4;
                if (!on) { Consider(Math.Clamp(x, x0, x1), Math.Clamp(y, y0, y1)); continue; }
                // the cell's sides that are the parcel's edge (no cell of it beyond)
                float cxp = Math.Clamp(x, x0, x1), cyp = Math.Clamp(y, y0, y1);
                if (!In(cx - 1, cy)) Consider(x0, cyp);
                if (!In(cx + 1, cy)) Consider(x1, cyp);
                if (!In(cx, cy - 1)) Consider(cxp, y0);
                if (!In(cx, cy + 1)) Consider(cxp, y1);
            }
        return any ? (on, at.X, at.Y) : null;
    }
}
