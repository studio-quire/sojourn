using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>What the build editor knows of one prim when it's shown, read from the world model: the prim, its flags,
/// where it is in the region (a child's too), how many faces it has and what to call each, and how to name people and
/// textures. Where: where it is from the person and what it rests on and touches, worked out apart (MainForm.Build.cs).</summary>
public sealed record PrimLook(Primitive P, PrimFlags Flags, bool IsRoot, int Number, int Count, string Name, string RootName,
    Vector3 Pos, Quaternion Rot, int Faces, Func<int, string> FaceName, Func<UUID, string> NameOf, Func<UUID, string> GroupOf,
    Func<UUID, string?> TextureName)
{
    public string? Where { get; init; }
    public string Shape => ObjectViews.ShapeName(P);
    public bool Made => Shape is "mesh" or "sculpt";
    public bool Circular => ((byte)P.PrimData.PathCurve & 0xf0) is not (0x10 or 0x80);
    public bool Sphere => (P.PrimData.profileCurve & 0x0f) == 5 && Circular;
    /// <summary>A torus, tube or ring: the editor's rows for a circular path that isn't a sphere.</summary>
    public bool Ring => Circular && !Sphere;
    public bool Flexible => ((byte)P.PrimData.PathCurve & 0xf0) == 0x80;
    public bool LightOn => P.Light is { Intensity: > 0 };
    public Primitive.ObjectProperties? Props => P.Properties is { } pr && pr.ObjectID == P.ID ? pr : null;

    public Primitive.TextureEntryFace Face(int i) =>
        P.Textures is { } te ? te.GetFace((uint)i) : new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE).DefaultTexture;
}

public enum RowKind { Info, Text, Number, Pair, Switch, Choice, Colour, Texture }

/// <summary>One row of the editor: its words as listed ("Hollow: 50 percent"), what kind of value it takes, the value as
/// it would be typed (Now, put in the box Enter opens), and for numbers and choices how a step moves it.</summary>
public sealed record BuildRow(string Key, string Words, RowKind Kind)
{
    public string Label { get; init; } = "";
    public string Now { get; init; } = "";
    public string Ask { get; init; } = "";
    public double Value { get; init; }
    public double Step { get; init; }
    public string[]? Choices { get; init; }
    public int First { get; init; }
    public int Chosen { get; init; } = -1;
    /// <summary>The person's confirmation in Notices first: "permissions" (who may do what, a sale), "delete" (temporary).</summary>
    public string? Gate { get; init; }
}

/// <summary>
/// The build editor's vocabulary (docs/access.md > M6; a 2009 design for a blind builder's editor):
/// the official editor's tabs (General, Object, Features, Texture, Content) and their settings under the editor's own
/// names, so blind and sighted builders speak the same language; metres and degrees, never quaternions; numbered
/// choices (shininess 0-3, bumpiness 0-17, as scripts number them); a value shared by every face said once, "all
/// sides". Each setting is a row to browse and the same words typed as a command ("hollow 50", "phantom on", "colour
/// red"). Pure words over a <see cref="PrimLook"/>; a change comes back as TesselBuild modify's keys.
/// </summary>
public static class BuildRows
{
    public static readonly string[] Tabs = { "General", "Object", "Features", "Texture", "Content" };

    sealed record Def(string Key, string Tab, string Label, RowKind Kind, string[] Aliases, Func<PrimLook, bool> Shows);

    static bool All(PrimLook _) => true;
    static bool Never(PrimLook _) => false;

    public static readonly string[] ClickWords = { "touch", "sit", "buy", "pay", "open", "play media", "open media", "zoom", "nothing" };
    static readonly string[] ClickOps = { "touch", "sit", "buy", "pay", "open", "play", "open_media", "zoom", "disabled" };
    public static readonly string[] Materials = { "stone", "metal", "glass", "wood", "flesh", "plastic", "rubber" };
    public static readonly string[] Types = { "box", "cylinder", "prism", "sphere", "torus", "tube", "ring" };
    public static readonly string[] Shininess = { "none", "low", "medium", "high" };
    public static readonly string[] Bumps =
    {
        "none", "brightness", "darkness", "woodgrain", "bark", "bricks", "checker", "concrete", "crusty tile", "cut stone",
        "discs", "gravel", "petri dish", "siding", "stone tile", "stucco", "suction", "weave",
    };
    static readonly string[] Mappings = { "default", "planar" };
    static readonly string[] PhysicsShapes = { "prim", "none", "convex hull" };

    static readonly Def[] Defs =
    {
        // General
        new("name", "General", "Name", RowKind.Text, new[] { "name" }, All),
        new("desc", "General", "Description", RowKind.Text, new[] { "description", "desc" }, All),
        new("creator", "General", "Creator", RowKind.Info, new[] { "creator" }, All),
        new("owner", "General", "Owner", RowKind.Info, new[] { "owner" }, All),
        new("group", "General", "Group", RowKind.Info, new[] { "group" }, All),
        new("share", "General", "Share with group", RowKind.Switch, new[] { "share with group", "share" }, All),
        new("anyone_move", "General", "Anyone can move", RowKind.Switch, new[] { "anyone can move" }, All),
        new("anyone_copy", "General", "Anyone can copy", RowKind.Switch, new[] { "anyone can copy" }, All),
        new("sale", "General", "For sale", RowKind.Text, new[] { "not for sale", "for sale", "sale" }, All),
        new("next_modify", "General", "Next owner can modify", RowKind.Switch, new[] { "next owner can modify", "next owner modify", "next modify" }, All),
        new("next_copy", "General", "Next owner can copy", RowKind.Switch, new[] { "next owner can copy", "next owner copy", "next copy" }, All),
        new("next_transfer", "General", "Next owner can transfer", RowKind.Switch, new[] { "next owner can transfer", "next owner transfer", "next transfer" }, All),
        new("click", "General", "When left-clicked", RowKind.Choice, new[] { "when left-clicked", "when left clicked", "when clicked", "click action", "click" }, All),
        // Object
        new("where", "Object", "Where", RowKind.Info, new[] { "where" }, All),
        new("physical", "Object", "Physical", RowKind.Switch, new[] { "physical" }, All),
        new("temp", "Object", "Temporary", RowKind.Switch, new[] { "temporary on rez", "temporary", "temp" }, All),
        new("phantom", "Object", "Phantom", RowKind.Switch, new[] { "phantom" }, All),
        new("pos.x", "Object", "Position X, east", RowKind.Number, new[] { "position x", "x position", "pos x" }, All),
        new("pos.y", "Object", "Position Y, north", RowKind.Number, new[] { "position y", "y position", "pos y" }, All),
        new("pos.z", "Object", "Position Z, height", RowKind.Number, new[] { "position z", "z position", "pos z", "height above sea" }, All),
        new("size.x", "Object", "Size X", RowKind.Number, new[] { "size x", "x size" }, All),
        new("size.y", "Object", "Size Y", RowKind.Number, new[] { "size y", "y size" }, All),
        new("size.z", "Object", "Size Z", RowKind.Number, new[] { "size z", "z size" }, All),
        new("rot.x", "Object", "Rotation X", RowKind.Number, new[] { "rotation x", "x rotation" }, All),
        new("rot.y", "Object", "Rotation Y", RowKind.Number, new[] { "rotation y", "y rotation" }, All),
        new("rot.z", "Object", "Rotation Z", RowKind.Number, new[] { "rotation z", "z rotation" }, All),
        new("material", "Object", "Material", RowKind.Choice, new[] { "material" }, All),
        new("type", "Object", "Building block type", RowKind.Choice, new[] { "building block type", "block type", "type", "shape" }, l => !l.Made),
        new("made", "Object", "Building block type", RowKind.Info, Array.Empty<string>(), l => l.Made),
        // the editor's cut rows: on a straight path its Path cut is the profile's, and Slice the path's
        new("cut", "Object", "Path cut, begin and end", RowKind.Pair, new[] { "path cut", "cut" }, l => !l.Made),
        new("hollow", "Object", "Hollow", RowKind.Number, new[] { "hollow" }, l => !l.Made),
        new("twist", "Object", "Twist, begin and end", RowKind.Pair, new[] { "twist" }, l => !l.Made),
        new("taper", "Object", "Taper, X and Y", RowKind.Pair, new[] { "taper" }, l => !l.Made && !l.Sphere),
        new("top_shear", "Object", "Top shear, X and Y", RowKind.Pair, new[] { "top shear", "shear" }, l => !l.Made && !l.Sphere),
        new("slice", "Object", "Slice, begin and end", RowKind.Pair, new[] { "slice" }, l => !l.Made && !l.Circular),
        new("dimple", "Object", "Dimple, begin and end", RowKind.Pair, new[] { "dimple" }, l => !l.Made && l.Sphere),
        new("hole_size", "Object", "Hole size, X and Y", RowKind.Pair, new[] { "hole size" }, l => !l.Made && l.Ring),
        new("profile_cut", "Object", "Profile cut, begin and end", RowKind.Pair, new[] { "profile cut" }, l => !l.Made && l.Ring),
        new("radius", "Object", "Radius", RowKind.Number, new[] { "radius" }, l => !l.Made && l.Ring),
        new("revolutions", "Object", "Revolutions", RowKind.Number, new[] { "revolutions" }, l => !l.Made && l.Ring),
        new("skew", "Object", "Skew", RowKind.Number, new[] { "skew" }, l => !l.Made && l.Ring),
        // Features
        new("flexible", "Features", "Flexible path", RowKind.Switch, new[] { "flexible path", "flexible", "flexi" }, l => !l.Made && !l.Circular),
        new("softness", "Features", "Softness", RowKind.Number, new[] { "softness" }, l => l.Flexible),
        new("gravity", "Features", "Gravity", RowKind.Number, new[] { "gravity" }, l => l.Flexible),
        new("drag", "Features", "Drag", RowKind.Number, new[] { "drag" }, l => l.Flexible),
        new("wind", "Features", "Wind", RowKind.Number, new[] { "wind" }, l => l.Flexible),
        new("tension", "Features", "Tension", RowKind.Number, new[] { "tension" }, l => l.Flexible),
        new("light", "Features", "Light", RowKind.Switch, new[] { "light" }, All),
        new("light_colour", "Features", "Light colour", RowKind.Colour, new[] { "light colour", "light color" }, l => l.LightOn),
        new("intensity", "Features", "Light intensity", RowKind.Number, new[] { "light intensity", "intensity" }, l => l.LightOn),
        new("light_radius", "Features", "Light radius", RowKind.Number, new[] { "light radius" }, l => l.LightOn),
        new("falloff", "Features", "Light falloff", RowKind.Number, new[] { "light falloff", "falloff" }, l => l.LightOn),
        new("physics_shape", "Features", "Physics shape type", RowKind.Choice, new[] { "physics shape type", "physics shape" }, All),
        // Texture: of the face chosen, or all sides
        new("face", "Texture", "Face", RowKind.Info, Array.Empty<string>(), All),
        new("texture", "Texture", "Texture", RowKind.Texture, new[] { "texture" }, All),
        new("colour", "Texture", "Colour", RowKind.Colour, new[] { "colour", "color" }, All),
        new("transparency", "Texture", "Transparency", RowKind.Number, new[] { "transparency", "transparent" }, All),
        new("glow", "Texture", "Glow", RowKind.Number, new[] { "glow" }, All),
        new("fullbright", "Texture", "Full bright", RowKind.Switch, new[] { "full bright", "fullbright" }, All),
        new("shiny", "Texture", "Shininess", RowKind.Choice, new[] { "shininess", "shiny" }, All),
        new("bump", "Texture", "Bumpiness", RowKind.Choice, new[] { "bumpiness", "bump" }, All),
        new("mapping", "Texture", "Mapping", RowKind.Choice, new[] { "mapping", "texgen" }, All),
        new("repeats.u", "Texture", "Repeats, horizontal", RowKind.Number, new[] { "horizontal repeats", "repeats horizontal" }, All),
        new("repeats.v", "Texture", "Repeats, vertical", RowKind.Number, new[] { "vertical repeats", "repeats vertical" }, All),
        new("repeats", "Texture", "Repeats, horizontal and vertical", RowKind.Pair, new[] { "repeats" }, Never),
        new("tex_rotation", "Texture", "Texture rotation", RowKind.Number, new[] { "texture rotation" }, All),
        new("offset.u", "Texture", "Offset, horizontal", RowKind.Number, new[] { "horizontal offset", "offset horizontal" }, All),
        new("offset.v", "Texture", "Offset, vertical", RowKind.Number, new[] { "vertical offset", "offset vertical" }, All),
        new("offset", "Texture", "Offset, horizontal and vertical", RowKind.Pair, new[] { "offset" }, Never),
    };

    static readonly (string Alias, Def Def)[] ByAlias = Defs.SelectMany(d => d.Aliases.Select(a => (a, d)))
        .OrderByDescending(x => x.a.Length).ToArray();

    /// <summary>The tab a setting is on, by its key.</summary>
    public static string TabOf(string key) => Defs.FirstOrDefault(d => d.Key == key)?.Tab ?? "Object";

    /// <summary>A typed command's setting and the value after its name ("hollow 50": hollow and "50"); null when it names
    /// none. The value keeps its case (a name or description as typed).</summary>
    /// <summary>Every word a setting goes by, for a mistyped one.</summary>
    public static IEnumerable<string> Words => ByAlias.Select(a => a.Alias);

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

    /// <summary>The rows a tab shows for this prim (Texture's for one face, or all sides when face is null).</summary>
    public static List<BuildRow> For(PrimLook l, string tab, int? face) =>
        Defs.Where(d => d.Tab.Equals(tab, StringComparison.OrdinalIgnoreCase) && d.Shows(l)).Select(d => Row(l, d, face)).ToList();

    /// <summary>One setting's row, shown or not (a command can read any).</summary>
    public static BuildRow? Row(PrimLook l, string key, int? face) => Defs.FirstOrDefault(d => d.Key == key) is { } d ? Row(l, d, face) : null;

    static BuildRow Row(PrimLook l, Def d, int? face)
    {
        var p = l.P;
        var props = l.Props;
        var pd = p.PrimData;
        BuildRow R(string value, string now = "", string ask = "") =>
            new(d.Key, $"{d.Label}: {value}", d.Kind) { Label = d.Label, Now = now.Length > 0 ? now : value, Ask = ask.Length > 0 ? ask : d.Label };
        BuildRow Sw(bool on, string? gate = null) => R(on ? "on" : "off") with { Value = on ? 1 : 0, Gate = gate };
        BuildRow Num(double v, string unit, double step, int dp = 3, string ask = "") =>
            R(N(v, dp) + One(N(v, dp), unit), N(v, dp), ask) with { Value = v, Step = step };
        BuildRow Pair(double a, double b, string unit, int dp = 3) =>
            R(a == b ? $"{N(a, dp)}{unit} both" : $"{N(a, dp)}{unit} and {N(b, dp)}{unit}", $"{N(a, dp)} {N(b, dp)}", $"{d.Label} (two numbers)");
        BuildRow Pick(string[] names, int chosen, int first = 0, string? gate = null) =>
            R(chosen >= 0 && chosen < names.Length ? $"{chosen + first} {names[chosen]}" : "not known yet",
                chosen >= 0 && chosen < names.Length ? names[chosen] : "", $"{d.Label} ({first} to {names.Length - 1 + first}, or a name)")
            with { Choices = names, First = first, Chosen = chosen, Gate = gate };
        var perms = props?.Permissions;
        var twistScale = l.Circular ? 360 : 180;
        switch (d.Key)
        {
            case "name": return R(props?.Name is { Length: > 0 } n ? n : "not known yet", props?.Name ?? "");
            case "desc": return R(props?.Description is { Length: > 0 } ds && ds != "(No Description)" ? ds : "none", props?.Description ?? "");
            case "creator": return R(props == null ? "not known yet" : l.NameOf(props.CreatorID));
            case "owner": return R(props == null ? "not known yet" : (l.Flags & PrimFlags.ObjectYouOwner) != 0 ? "you" : l.NameOf(props.OwnerID));
            case "group": return R(props == null ? "not known yet" : props.GroupID == UUID.Zero ? "none" : l.GroupOf(props.GroupID));
            case "share": return Sw(perms is { } g && (g.GroupMask & PermissionMask.Modify) != 0, "permissions");
            case "anyone_move": return Sw(perms is { } m && (m.EveryoneMask & PermissionMask.Move) != 0, "permissions");
            case "anyone_copy": return Sw(perms is { } c && (c.EveryoneMask & PermissionMask.Copy) != 0, "permissions");
            case "next_modify": return Sw(perms is { } nm && (nm.NextOwnerMask & PermissionMask.Modify) != 0, "permissions");
            case "next_copy": return Sw(perms is { } nc && (nc.NextOwnerMask & PermissionMask.Copy) != 0, "permissions");
            case "next_transfer": return Sw(perms is { } nt && (nt.NextOwnerMask & PermissionMask.Transfer) != 0, "permissions");
            case "sale":
            {
                var row = props == null ? R("not known yet", "no") : props.SaleType == SaleType.Not ? R("no", "no")
                    : R($"L${props.SalePrice}, {SaleWords(props.SaleType)}", $"{props.SalePrice} {SaleWords(props.SaleType)}");
                return row with { Gate = "permissions", Ask = "For sale (a price, and original, copy or contents; or no)" };
            }
            case "click": return Pick(ClickWords, Math.Clamp((int)p.ClickAction, 0, ClickWords.Length - 1));
            case "where": return R(l.Where ?? "not worked out yet");
            case "physical": return Sw((l.Flags & PrimFlags.Physics) != 0);
            case "temp": return Sw((l.Flags & PrimFlags.TemporaryOnRez) != 0, "delete");
            case "phantom": return Sw((l.Flags & PrimFlags.Phantom) != 0);
            case "pos.x": return Num(l.Pos.X, " metres", 0.1, ask: "Position X, east, in metres in the region");
            case "pos.y": return Num(l.Pos.Y, " metres", 0.1, ask: "Position Y, north, in metres in the region");
            case "pos.z": return Num(l.Pos.Z, " metres", 0.1, ask: "Position Z, height, in metres");
            case "size.x": return Num(p.Scale.X, " metres", 0.05, ask: "Size X in metres");
            case "size.y": return Num(p.Scale.Y, " metres", 0.05, ask: "Size Y in metres");
            case "size.z": return Num(p.Scale.Z, " metres", 0.05, ask: "Size Z in metres");
            case "rot.x": return Num(Euler(l.Rot).X, " degrees", 15, 1, "Rotation X in degrees");
            case "rot.y": return Num(Euler(l.Rot).Y, " degrees", 15, 1, "Rotation Y in degrees");
            case "rot.z": return Num(Euler(l.Rot).Z, " degrees", 15, 1, "Rotation Z in degrees");
            case "material": return Pick(Materials, (int)pd.Material is >= 0 and <= 6 ? (int)pd.Material : -1);
            case "type": return Pick(Types, Array.IndexOf(Types, l.Shape switch { "half-circle" => "sphere", var s => s }));
            case "made": return R(l.Shape == "mesh" ? "mesh (its shape comes from a model; only size, place and faces change here)" : "sculpted (its shape comes from a sculpt map)");
            case "cut": return l.Circular ? Pair(pd.PathBegin, pd.PathEnd, "") : Pair(pd.ProfileBegin, pd.ProfileEnd, "");
            case "slice": return Pair(pd.PathBegin, pd.PathEnd, "");
            case "dimple" or "profile_cut": return Pair(pd.ProfileBegin, pd.ProfileEnd, "");
            case "hollow": return Num(Math.Round(pd.ProfileHollow * 100, 1), " percent", 5, 1, "Hollow, percent (0 to 95)");
            case "twist": return Pair(Math.Round(pd.PathTwistBegin * twistScale), Math.Round(pd.PathTwist * twistScale), " degrees", 0);
            case "taper": return l.Circular ? Pair(pd.PathTaperX, pd.PathTaperY, "", 2) : Pair(Math.Round(1 - pd.PathScaleX, 2), Math.Round(1 - pd.PathScaleY, 2), "", 2);
            case "top_shear": return Pair(pd.PathShearX, pd.PathShearY, "", 2);
            case "hole_size": return Pair(pd.PathScaleX, pd.PathScaleY, "", 2);
            case "radius": return Num(pd.PathRadiusOffset, "", 0.05, 2);
            case "revolutions": return Num(pd.PathRevolutions, "", 0.1, 2);
            case "skew": return Num(pd.PathSkew, "", 0.05, 2);
            case "flexible": return Sw(l.Flexible);
            case "softness": return Num(p.Flexible?.Softness ?? 2, "", 1, 0, "Softness (0 to 3)");
            case "gravity": return Num(p.Flexible?.Gravity ?? 0.3, "", 0.5, 2);
            case "drag": return Num(p.Flexible?.Drag ?? 2, "", 0.5, 2);
            case "wind": return Num(p.Flexible?.Wind ?? 0, "", 0.5, 2);
            case "tension": return Num(p.Flexible?.Tension ?? 1, "", 0.5, 2);
            case "light": return Sw(l.LightOn);
            case "light_colour":
                return p.Light is { } lc ? R(ColourWords(lc.Color), Rgb(lc.Color), "Light colour (a name, or red, green and blue 0 to 255)") : R("white");
            case "intensity": return Num(p.Light?.Intensity ?? 1, "", 0.1, 2, "Light intensity (0 to 1)");
            case "light_radius": return Num(p.Light?.Radius ?? 10, " metres", 1, 1, "Light radius in metres (0 to 20)");
            case "falloff": return Num(p.Light?.Falloff ?? 0.75, "", 0.25, 2, "Light falloff (0 to 2)");
            case "physics_shape":
                return Pick(PhysicsShapes, p.PhysicsProps is { } ph ? (int)ph.PhysicsShapeType : -1);
            case "face": return R(face is { } f ? l.FaceName(f) : $"all sides ({(l.Faces == 1 ? "1 face" : $"{l.Faces} faces")})");
        }
        // a face's settings: of the face chosen, or every face's, a value they share said once
        string Faces(Func<Primitive.TextureEntryFace, string> words)
        {
            if (face is { } one) return words(l.Face(one));
            var groups = Enumerable.Range(0, Math.Max(1, l.Faces)).GroupBy(i => words(l.Face(i))).OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 1) return $"{groups[0].Key}, all sides";
            return string.Join("; ", groups.Select((g, i) => i == 0 && g.Count() > 1 && groups.Count <= 3
                ? $"{g.Key} on {g.Count()} sides" : $"{g.Key} on {string.Join(" and ", g.Select(l.FaceName))}"));
        }
        var shown = l.Face(face ?? 0);
        BuildRow FaceNum(Func<Primitive.TextureEntryFace, double> v, string unit, double step, int dp, string ask) =>
            R(Faces(f => N(v(f), dp) + One(N(v(f), dp), unit)), N(v(shown), dp), ask) with { Value = v(shown), Step = step };
        BuildRow FacePick(string[] names, Func<Primitive.TextureEntryFace, int> v, int first = 0) =>
            R(Faces(f => v(f) is var i && i >= 0 && i < names.Length ? $"{i + first} {names[i]}" : "unknown"), names[Math.Clamp(v(shown), 0, names.Length - 1)],
                $"{d.Label} ({first} to {names.Length - 1 + first}, or a name)") with { Choices = names, First = first, Chosen = v(shown) };
        switch (d.Key)
        {
            case "texture": return R(Faces(f => TextureWords(l, f.TextureID)), TextureWords(l, shown.TextureID), "Texture (plywood, blank, transparent, or a texture's name in your inventory)");
            case "colour": return R(Faces(f => ColourWords(f.RGBA)), Rgb(shown.RGBA), "Colour (a name, or red, green and blue 0 to 255)");
            case "transparency": return FaceNum(f => Math.Round((1 - f.RGBA.A) * 100), " percent", 10, 0, "Transparency, percent (0 to 100)");
            case "glow": return FaceNum(f => f.Glow, "", 0.05, 2, "Glow (0 to 1)");
            case "fullbright": return R(Faces(f => f.Fullbright ? "on" : "off")) with { Value = shown.Fullbright ? 1 : 0 };
            case "shiny": return FacePick(Shininess, f => (int)f.Shiny >> 6);
            case "bump": return FacePick(Bumps, f => (int)f.Bump is var b && b < Bumps.Length ? b : -1);
            case "mapping": return FacePick(Mappings, f => f.TexMapType == MappingType.Planar ? 1 : 0);
            case "repeats.u": return FaceNum(f => f.RepeatU, "", 0.5, 2, "Horizontal repeats");
            case "repeats.v": return FaceNum(f => f.RepeatV, "", 0.5, 2, "Vertical repeats");
            case "repeats": return R(Faces(f => $"{N(f.RepeatU, 2)} by {N(f.RepeatV, 2)}"), $"{N(shown.RepeatU, 2)} {N(shown.RepeatV, 2)}");
            case "tex_rotation": return FaceNum(f => Math.Round(f.Rotation * 180 / Math.PI, 1), " degrees", 15, 1, "Texture rotation in degrees");
            case "offset.u": return FaceNum(f => f.OffsetU, "", 0.05, 2, "Horizontal offset (-1 to 1)");
            case "offset.v": return FaceNum(f => f.OffsetV, "", 0.05, 2, "Vertical offset (-1 to 1)");
            case "offset": return R(Faces(f => $"{N(f.OffsetU, 2)} and {N(f.OffsetV, 2)}"), $"{N(shown.OffsetU, 2)} {N(shown.OffsetV, 2)}");
        }
        return R("?");
    }

    // --- a change -------------------------------------------------------------------------------------------------------

    /// <summary>A setting changed to the value typed: TesselBuild modify's keys (several sets where faces must each keep
    /// a part of a pair they don't share), or why not, in words. texture: a texture's words to what modify takes.</summary>
    public static (List<JsonObject>? Args, string? Error) Change(PrimLook l, string key, string value, int? face, Func<string, (string? Id, string? Error)> texture)
    {
        var v = value.Trim();
        var p = l.P;
        var pd = p.PrimData;
        var row = Row(l, key, face);
        var label = row?.Label ?? key;
        List<JsonObject> One(JsonObject o) => new() { o };
        (List<JsonObject>?, string?) Ok(JsonObject o) => (One(o), null);
        (List<JsonObject>?, string?) No(string why) => (null, why);
        if (v.Length == 0) return No($"{label}: give a value. {row?.Ask}.");
        // numbers in the words, with units where they're lengths or turns
        bool Number(out double n, bool metres = false) => TryNumber(v, metres, out n);
        bool Two(out double a, out double b, bool metres = false)
        {
            var nums = Numbers(v, metres);
            (a, b) = nums.Count switch { 1 => (nums[0], nums[0]), >= 2 => (nums[0], nums[1]), _ => (0, 0) };
            return nums.Count is 1 or 2;
        }
        JsonArray Arr(params double[] xs) => new(xs.Select(x => (JsonNode)Math.Round(x, 5)).ToArray());
        JsonObject Face(JsonObject o) { if (face is { } f) o["face"] = f; return o; }
        int? Choice(string[] names, int first = 0)
        {
            var w = v.ToLowerInvariant();
            if (int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i - first >= 0 && i - first < names.Length ? i - first : null;
            var exact = Array.FindIndex(names, n => n == w || n.Replace(" ", "") == w.Replace(" ", ""));
            if (exact >= 0) return exact;
            var starts = Array.FindIndex(names, n => n.StartsWith(w, StringComparison.Ordinal));
            return starts >= 0 ? starts : null;
        }
        string Choices(string[] names, int first = 0) => string.Join(", ", names.Select((n, i) => $"{i + first} {n}"));
        bool? On()
        {
            var w = v.ToLowerInvariant().TrimEnd('.');
            return w is "on" or "yes" or "true" or "1" or "checked" or "y" ? true : w is "off" or "no" or "false" or "0" or "unchecked" or "n" ? false : null;
        }
        var perms = l.Props?.Permissions;
        string Letters(PermissionMask m, PermissionMask bit, bool on, PermissionMask keep) =>
            ObjectViews.PermLetters(((on ? m | bit : m & ~bit) & keep)).Replace("-", "");
        switch (key)
        {
            case "name": return v.Length > 63 ? No("A name is at most 63 characters.") : Ok(new JsonObject { ["name"] = v });
            case "desc": return v.Length > 127 ? No("A description is at most 127 characters.") : Ok(new JsonObject { ["desc"] = v });
            case "creator" or "owner" or "group" or "where" or "face" or "made": return No($"{label} can't be changed here.");
            case "share" or "anyone_move" or "anyone_copy" or "next_modify" or "next_copy" or "next_transfer":
            {
                if (On() is not { } on) return No($"{label}: on or off.");
                if (perms is not { } pm) return No($"{label}: its permissions aren't known yet; try again in a moment.");
                return key switch
                {
                    "share" => Ok(new JsonObject { ["group_perms"] = on ? "MCV" : "" }),
                    "anyone_move" => Ok(new JsonObject { ["everyone_perms"] = Letters(pm.EveryoneMask, PermissionMask.Move, on, PermissionMask.Move | PermissionMask.Copy) }),
                    "anyone_copy" => Ok(new JsonObject { ["everyone_perms"] = Letters(pm.EveryoneMask, PermissionMask.Copy, on, PermissionMask.Move | PermissionMask.Copy) }),
                    _ => Ok(new JsonObject
                    {
                        ["next_perms"] = Letters(pm.NextOwnerMask, key switch { "next_modify" => PermissionMask.Modify, "next_copy" => PermissionMask.Copy, _ => PermissionMask.Transfer },
                            on, PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Transfer),
                    }),
                };
            }
            case "sale":
            {
                var w = v.ToLowerInvariant();
                if (w is "no" or "not" or "off" or "none" or "not for sale") return Ok(new JsonObject { ["sale"] = false });
                var m = Regex.Match(w, @"(?:l\$\s*)?(\d+)");
                if (!m.Success) return No("For sale: a price in L$ (and original, copy or contents), or no.");
                var kind = w.Contains("contents") ? "contents" : w.Contains("copy") ? "copy" : w.Contains("original") ? "original"
                    : l.Props is { SaleType: not SaleType.Not } sp ? SaleWords(sp.SaleType) : "original";
                return Ok(new JsonObject { ["sale"] = new JsonObject { ["type"] = kind, ["price"] = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) } });
            }
            case "click":
                return Choice(ClickWords) is { } ci ? Ok(new JsonObject { ["click_action"] = ClickOps[ci] }) : No($"When left-clicked: {Choices(ClickWords)}.");
            case "physical" or "temp" or "phantom":
                return On() is { } fo ? Ok(new JsonObject { [key == "temp" ? "temp" : key == "physical" ? "physics" : "phantom"] = fo }) : No($"{label}: on or off.");
            case "pos.x" or "pos.y" or "pos.z":
            {
                if (!Number(out var n, metres: true)) return No($"{label}: a number of metres.");
                var at = new[] { (double)l.Pos.X, l.Pos.Y, l.Pos.Z };
                int axis = key[^1] - 'x';
                if (axis < 2 && (n < 0 || n > 256)) return No($"{label}: 0 to 256 metres, inside the region.");
                if (axis == 2 && (n < 0 || n > 4096)) return No($"{label}: 0 to 4096 metres.");
                at[axis] = n;
                return Ok(new JsonObject { ["pos"] = Arr(at) });
            }
            case "size.x" or "size.y" or "size.z":
            {
                if (!Number(out var n, metres: true)) return No($"{label}: a number of metres.");
                if (n < 0.01 || n > 64) return No($"{label}: 0.01 to 64 metres.");
                var s = new[] { (double)p.Scale.X, p.Scale.Y, p.Scale.Z };
                s[key[^1] - 'x'] = n;
                return Ok(new JsonObject { ["scale"] = Arr(s) });
            }
            case "rot.x" or "rot.y" or "rot.z":
            {
                if (!Number(out var n)) return No($"{label}: a number of degrees.");
                var e = Euler(l.Rot);
                var r = new[] { (double)e.X, e.Y, e.Z };
                r[key[^1] - 'x'] = n;
                return Ok(new JsonObject { ["rot_euler"] = Arr(r) });
            }
            case "material":
                return Choice(Materials) is { } mi ? Ok(new JsonObject { ["material"] = Materials[mi] }) : No($"Material: {Choices(Materials)}.");
            case "type":
                return Choice(Types) is { } ti ? Ok(new JsonObject { ["shape"] = Types[ti] }) : No($"Building block type: {Choices(Types)}.");
            case "cut" or "slice" or "dimple" or "profile_cut":
            {
                if (!Two(out var a, out var b) || a < 0 || b > 1 || b - a < 0.02) return No($"{label}: begin and end, from 0 to 1, the end at least 0.02 past the begin.");
                // which of the shape's cuts this is, as the editor maps its rows
                var op = key switch { "cut" => l.Circular ? "path_cut" : "profile_cut", "slice" => "path_cut", _ => "profile_cut" };
                return Ok(new JsonObject { [op] = Arr(a, b) });
            }
            case "hollow":
                return Number(out var h) && h >= 0 && h <= 95 ? Ok(new JsonObject { ["hollow"] = Math.Round(h / 100, 4) }) : No("Hollow: a percentage, 0 to 95.");
            case "twist":
            {
                int scale = l.Circular ? 360 : 180;
                if (!Two(out var a, out var b) || Math.Abs(a) > scale || Math.Abs(b) > scale) return No($"Twist: begin and end in degrees, {-scale} to {scale}.");
                return Ok(new JsonObject { ["twist"] = Arr(a / scale, b / scale) });
            }
            case "taper":
                return Two(out var tx, out var ty) && Math.Abs(tx) <= 1 && Math.Abs(ty) <= 1 ? Ok(new JsonObject { ["taper"] = Arr(tx, ty) }) : No("Taper: X and Y, from -1 to 1.");
            case "top_shear":
                return Two(out var sx, out var sy) && Math.Abs(sx) <= 0.5 && Math.Abs(sy) <= 0.5 ? Ok(new JsonObject { ["top_shear"] = Arr(sx, sy) }) : No("Top shear: X and Y, from -0.5 to 0.5.");
            case "hole_size":
                return Two(out var hx, out var hy) && hx is >= 0.05 and <= 1 && hy is >= 0.05 and <= 0.5 ? Ok(new JsonObject { ["hole_size"] = Arr(hx, hy) }) : No("Hole size: X from 0.05 to 1, Y from 0.05 to 0.5.");
            case "radius":
                return Number(out var ro) && Math.Abs(ro) <= 1 ? Ok(new JsonObject { ["radius_offset"] = ro }) : No("Radius: -1 to 1.");
            case "revolutions":
                return Number(out var rv) && rv is >= 1 and <= 4 ? Ok(new JsonObject { ["revolutions"] = rv }) : No("Revolutions: 1 to 4.");
            case "skew":
                return Number(out var sk) && Math.Abs(sk) <= 0.95 ? Ok(new JsonObject { ["skew"] = sk }) : No("Skew: -0.95 to 0.95.");
            case "flexible":
                if (On() is not { } fl) return No("Flexible path: on or off.");
                return Ok(new JsonObject { ["flexi"] = fl ? Flexi(p, null, 0) : false });
            case "softness" or "gravity" or "drag" or "wind" or "tension":
            {
                if (!Number(out var n)) return No($"{label}: a number.");
                var (lo, hi) = key switch { "softness" => (0.0, 3.0), "gravity" => (-10.0, 10.0), _ => (0.0, 10.0) };
                if (n < lo || n > hi) return No($"{label}: {N(lo)} to {N(hi)}.");
                return Ok(new JsonObject { ["flexi"] = Flexi(p, key, n) });
            }
            case "light":
                if (On() is not { } lo2) return No("Light: on or off.");
                return Ok(new JsonObject { ["light"] = lo2 ? Light(p, null, null) : false });
            case "light_colour":
                return ParseColour(v) is { } lcol ? Ok(new JsonObject { ["light"] = Light(p, "color", Arr(lcol.R, lcol.G, lcol.B)) }) : No(ColourHelp("Light colour"));
            case "intensity" or "light_radius" or "falloff":
            {
                if (!Number(out var n, metres: key == "light_radius")) return No($"{label}: a number.");
                var (lo, hi) = key switch { "intensity" => (0.0, 1.0), "light_radius" => (0.0, 20.0), _ => (0.0, 2.0) };
                if (n < lo || n > hi) return No($"{label}: {N(lo)} to {N(hi)}.");
                return Ok(new JsonObject { ["light"] = Light(p, key == "light_radius" ? "radius" : key, n) });
            }
            case "physics_shape":
                return Choice(PhysicsShapes) is { } psi ? Ok(new JsonObject { ["physics_shape"] = new[] { "prim", "none", "convex" }[psi] }) : No($"Physics shape type: {Choices(PhysicsShapes)}.");
            case "texture":
            {
                var (id, why) = texture(v);
                return id == null ? No(why ?? "No such texture.") : Ok(Face(new JsonObject { ["texture"] = id }));
            }
            case "colour":
                return ParseColour(v) is { } col ? Ok(Face(new JsonObject { ["color"] = Arr(col.R, col.G, col.B) })) : No(ColourHelp("Colour"));
            case "transparency":
                return Number(out var tr) && tr is >= 0 and <= 100 ? Ok(Face(new JsonObject { ["alpha"] = Math.Round(1 - tr / 100, 4) })) : No("Transparency: a percentage, 0 to 100.");
            case "glow":
                return Number(out var gl) && gl is >= 0 and <= 1 ? Ok(Face(new JsonObject { ["glow"] = gl })) : No("Glow: 0 to 1.");
            case "fullbright":
                return On() is { } fb ? Ok(Face(new JsonObject { ["fullbright"] = fb })) : No("Full bright: on or off.");
            case "shiny":
                return Choice(Shininess) is { } si ? Ok(Face(new JsonObject { ["shiny"] = si })) : No($"Shininess: {Choices(Shininess)}.");
            case "bump":
                return Choice(Bumps) is { } bi ? Ok(Face(new JsonObject { ["bump"] = Bumps[bi].Replace(" ", "") })) : No($"Bumpiness: {Choices(Bumps)}.");
            case "mapping":
                return Choice(Mappings) is { } mpi ? Ok(Face(new JsonObject { ["texgen"] = Mappings[mpi] })) : No("Mapping: 0 default or 1 planar.");
            case "tex_rotation":
                return Number(out var trot) ? Ok(Face(new JsonObject { ["tex_rotation"] = trot })) : No("Texture rotation: a number of degrees.");
            case "repeats" or "offset":
            {
                if (!Two(out var a, out var b)) return No($"{label}: one number for both, or two.");
                if (key == "offset" && (Math.Abs(a) > 1 || Math.Abs(b) > 1)) return No("Offset: -1 to 1.");
                return Ok(Face(new JsonObject { [key == "repeats" ? "repeats" : "tex_offset"] = Arr(a, b) }));
            }
            case "repeats.u" or "repeats.v" or "offset.u" or "offset.v":
            {
                if (!Number(out var n)) return No($"{label}: a number.");
                bool rep = key.StartsWith("repeats"), u = key.EndsWith(".u");
                if (!rep && Math.Abs(n) > 1) return No($"{label}: -1 to 1.");
                if (rep && (n == 0 || Math.Abs(n) > 100)) return No($"{label}: not 0, and -100 to 100.");
                // one call where every face keeps the same other half; else a call per face, each keeping its own
                var faces = face is { } one ? new[] { one } : Enumerable.Range(0, Math.Max(1, l.Faces)).ToArray();
                double Other(int f) => rep ? (u ? l.Face(f).RepeatV : l.Face(f).RepeatU) : (u ? l.Face(f).OffsetV : l.Face(f).OffsetU);
                var groups = faces.GroupBy(Other).ToList();
                var name = rep ? "repeats" : "tex_offset";
                if (groups.Count == 1)
                    return Ok(Face(new JsonObject { [name] = u ? Arr(n, groups[0].Key) : Arr(groups[0].Key, n) }));
                return (faces.Select(f => new JsonObject { ["face"] = f, [name] = u ? Arr(n, Other(f)) : Arr(Other(f), n) }).ToList(), null);
            }
        }
        return No($"{label} can't be changed here yet.");
    }

    static JsonObject Flexi(Primitive p, string? key, double value)
    {
        var f = p.Flexible;
        double Get(string k, double now) => k == key ? value : now;
        return new JsonObject
        {
            ["softness"] = Get("softness", f?.Softness ?? 2), ["gravity"] = Get("gravity", f?.Gravity ?? 0.3), ["drag"] = Get("drag", f?.Drag ?? 2),
            ["wind"] = Get("wind", f?.Wind ?? 0), ["tension"] = Get("tension", f?.Tension ?? 1),
            ["force"] = new JsonArray((double)(f?.Force.X ?? 0), (double)(f?.Force.Y ?? 0), (double)(f?.Force.Z ?? 0)),
        };
    }

    static JsonObject Light(Primitive p, string? key, JsonNode? value)
    {
        var l = p.Light is { Intensity: > 0 } on ? on : null;
        var o = new JsonObject
        {
            ["color"] = new JsonArray((double)(l?.Color.R ?? 1), (double)(l?.Color.G ?? 1), (double)(l?.Color.B ?? 1)),
            ["intensity"] = (double)(l?.Intensity ?? 1), ["radius"] = (double)(l?.Radius ?? 10), ["falloff"] = (double)(l?.Falloff ?? 0.75),
        };
        if (key != null) o[key] = value?.DeepClone();
        return o;
    }

    // --- words ------------------------------------------------------------------------------------------------------

    public static string N(double v, int dp = 3) =>
        Math.Round(v, dp).ToString("0." + new string('#', Math.Max(1, dp)), CultureInfo.InvariantCulture) is var s && s == "-0" ? "0" : s;

    /// <summary>"1 metre", "1 degree": a unit's singular after a one.</summary>
    static string One(string number, string unit) => number is "1" or "-1" && unit is " metres" or " degrees" ? unit[..^1] : unit;

    static string SaleWords(SaleType t) => t switch { SaleType.Copy => "copy", SaleType.Contents => "contents", _ => "original" };

    /// <summary>The prim's rotation as the editor shows it: degrees about X, Y and Z, each 0 to 360.</summary>
    public static System.Numerics.Vector3 Euler(Quaternion q)
    {
        q.GetEulerAngles(out var roll, out var pitch, out var yaw);
        static float D(float r) => (float)(Math.Round(((r * 180 / Math.PI) % 360 + 360) % 360, 1) % 360);
        return new(D(roll), D(pitch), D(yaw));
    }

    static string TextureWords(PrimLook l, UUID id) =>
        id == new UUID("89556747-24cb-43ed-920b-47caed15465f") ? "plywood"
        : id == new UUID("5748decc-f629-461c-9a36-a35a221fe21f") ? "blank"
        : id == new UUID("8dcd4a48-2d37-4909-9f78-f7a9eb4ef903") ? "transparent"
        : l.TextureName(id) ?? "a texture not in your inventory";

    static readonly (string Name, float R, float G, float B)[] Colours =
    {
        ("white", 1, 1, 1), ("black", 0, 0, 0), ("grey", .5f, .5f, .5f), ("light grey", .75f, .75f, .75f), ("dark grey", .25f, .25f, .25f),
        ("red", 1, 0, 0), ("dark red", .55f, 0, 0), ("orange", 1, .5f, 0), ("yellow", 1, 1, 0), ("gold", 1, .84f, 0),
        ("green", 0, 1, 0), ("dark green", 0, .4f, 0), ("olive", .5f, .5f, 0), ("lime", .6f, 1, .2f), ("teal", 0, .5f, .5f),
        ("cyan", 0, 1, 1), ("light blue", .55f, .75f, 1), ("sky blue", .53f, .81f, .92f), ("blue", 0, 0, 1), ("navy", 0, 0, .5f),
        ("purple", .5f, 0, .5f), ("violet", .56f, 0, 1), ("magenta", 1, 0, 1), ("pink", 1, .75f, .8f), ("hot pink", 1, .41f, .71f),
        ("brown", .55f, .27f, .07f), ("tan", .82f, .71f, .55f), ("beige", .96f, .96f, .86f), ("cream", 1, .99f, .82f),
    };

    static readonly Dictionary<string, string> ColourAliases = new() { ["gray"] = "grey", ["light gray"] = "light grey", ["dark gray"] = "dark grey", ["silver"] = "light grey", ["aqua"] = "cyan", ["fuchsia"] = "magenta", ["maroon"] = "dark red" };

    /// <summary>"red", or "close to orange" (the nearest named colour), with the editor's 0-255 numbers.</summary>
    public static string ColourWords(Color4 c)
    {
        var (name, d) = Colours.Select(k => (k.Name, D: Math.Sqrt(Sq(k.R - c.R) + Sq(k.G - c.G) + Sq(k.B - c.B)))).MinBy(x => x.D);
        return $"{(d < 0.03 ? name : $"close to {name}")} ({Rgb(c).Replace(" ", ", ")})";
        static double Sq(double x) => x * x;
    }

    static string Rgb(Color4 c) => $"{Math.Round(c.R * 255)} {Math.Round(c.G * 255)} {Math.Round(c.B * 255)}";

    static string ColourHelp(string label) => $"{label}: a name (red, light blue, dark grey...), or red, green and blue from 0 to 255.";

    /// <summary>A colour by name or as three numbers (0-255, or 0-1 when none is over 1).</summary>
    public static (double R, double G, double B)? ParseColour(string text)
    {
        var w = Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ").TrimEnd('.');
        if (ColourAliases.TryGetValue(w, out var a)) w = a;
        foreach (var k in Colours) if (k.Name == w) return (k.R, k.G, k.B);
        var nums = Numbers(w, false);
        if (nums.Count != 3 || nums.Any(n => n < 0 || n > 255)) return null;
        double scale = nums.Any(n => n > 1) ? 255 : 1;
        return (nums[0] / scale, nums[1] / scale, nums[2] / scale);
    }

    static readonly Regex NumberWithUnit = new(@"(?<n>-?\d+(?:[.,]\d+)?)\s*(?<u>cm|centimetres?|centimeters?|mm|millimetres?|millimeters?|m|metres?|meters?|ft|feet|foot|%|percent|degrees?|deg)?",
        RegexOptions.Compiled);

    /// <summary>The numbers in the words, metres from centimetres, millimetres and feet where they're lengths.</summary>
    public static List<double> Numbers(string text, bool metres)
    {
        var list = new List<double>();
        foreach (Match m in NumberWithUnit.Matches(text))
        {
            var n = double.Parse(m.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            if (metres) n *= m.Groups["u"].Value switch
            {
                "cm" or "centimetre" or "centimetres" or "centimeter" or "centimeters" => 0.01,
                "mm" or "millimetre" or "millimetres" or "millimeter" or "millimeters" => 0.001,
                "ft" or "feet" or "foot" => 0.3048,
                _ => 1,
            };
            list.Add(n);
        }
        return list;
    }

    static bool TryNumber(string text, bool metres, out double n)
    {
        var nums = Numbers(text, metres);
        n = nums.Count == 1 ? nums[0] : 0;
        return nums.Count == 1;
    }
}
