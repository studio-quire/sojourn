using LibreMetaverse;

namespace Sojourn.Core.Appearance;

/// <summary>Per wearable type: its name, texture slots (the viewer's appearance dictionary names), default textures and tint
/// params (llavatarappearancedefines.cpp, llviewerwearable.cpp, llavatarappearance.cpp teToColorParams).</summary>
public static class Slots
{
    public sealed record Slot(string Name, AvatarTextureIndex Index, UUID Default);

    public static readonly UUID DefaultClothing = new("5748decc-f629-461c-9a36-a35a221fe21f");
    public static readonly UUID DefaultHair = new("7ca39b4c-bd19-4699-aff7-f93fd03d3e7b");
    public static readonly UUID DefaultEyes = new("6522e74d-1660-4e7f-b601-6f48c1659a77");
    public static readonly UUID DefaultAvatar = new("c228d1cf-4b5d-4ba8-84f4-899a0796aa97");   // IMG_DEFAULT_AVATAR: an empty slot

    static readonly Dictionary<WearableType, Slot[]> ByType = new()
    {
        [WearableType.Shape] = Array.Empty<Slot>(),
        [WearableType.Physics] = Array.Empty<Slot>(),
        [WearableType.Skin] = new Slot[] { new("head_bodypaint", (AvatarTextureIndex)0, DefaultAvatar), new("upper_bodypaint", (AvatarTextureIndex)5, DefaultAvatar), new("lower_bodypaint", (AvatarTextureIndex)6, DefaultAvatar) },
        [WearableType.Shirt] = new Slot[] { new("upper_shirt", (AvatarTextureIndex)1, DefaultClothing) },
        [WearableType.Pants] = new Slot[] { new("lower_pants", (AvatarTextureIndex)2, DefaultClothing) },
        [WearableType.Eyes] = new Slot[] { new("eyes_iris", (AvatarTextureIndex)3, DefaultEyes) },
        [WearableType.Hair] = new Slot[] { new("hair_grain", (AvatarTextureIndex)4, DefaultHair) },
        [WearableType.Shoes] = new Slot[] { new("lower_shoes", (AvatarTextureIndex)7, DefaultClothing) },
        [WearableType.Socks] = new Slot[] { new("lower_socks", (AvatarTextureIndex)12, DefaultClothing) },
        [WearableType.Jacket] = new Slot[] { new("upper_jacket", (AvatarTextureIndex)13, DefaultClothing), new("lower_jacket", (AvatarTextureIndex)14, DefaultClothing) },
        [WearableType.Gloves] = new Slot[] { new("upper_gloves", (AvatarTextureIndex)15, DefaultClothing) },
        [WearableType.Undershirt] = new Slot[] { new("upper_undershirt", (AvatarTextureIndex)16, DefaultClothing) },
        [WearableType.Underpants] = new Slot[] { new("lower_underpants", (AvatarTextureIndex)17, DefaultClothing) },
        [WearableType.Skirt] = new Slot[] { new("skirt", (AvatarTextureIndex)18, DefaultClothing) },
        [WearableType.Alpha] = new Slot[] { new("lower_alpha", (AvatarTextureIndex)21, DefaultClothing), new("upper_alpha", (AvatarTextureIndex)22, DefaultClothing), new("head_alpha", (AvatarTextureIndex)23, DefaultClothing), new("eyes_alpha", (AvatarTextureIndex)24, DefaultClothing), new("hair_alpha", (AvatarTextureIndex)25, DefaultClothing) },
        [WearableType.Tattoo] = new Slot[] { new("head_tattoo", (AvatarTextureIndex)26, DefaultAvatar), new("upper_tattoo", (AvatarTextureIndex)27, DefaultAvatar), new("lower_tattoo", (AvatarTextureIndex)28, DefaultAvatar) },
        [WearableType.Universal] = new[] { "head_universal_tattoo", "upper_universal_tattoo", "lower_universal_tattoo", "skirt_tattoo", "hair_tattoo",
            "eyes_tattoo", "leftarm_tattoo", "leftleg_tattoo", "aux1_tattoo", "aux2_tattoo", "aux3_tattoo" }
            .Select((n, i) => new Slot(n, (AvatarTextureIndex)(29 + i), DefaultAvatar)).ToArray(),
    };

    static readonly Dictionary<WearableType, int[]> Tint = new()
    {
        [WearableType.Shirt] = new[] { 803, 804, 805 }, [WearableType.Pants] = new[] { 806, 807, 808 },
        [WearableType.Shoes] = new[] { 812, 813, 817 }, [WearableType.Socks] = new[] { 818, 819, 820 },
        [WearableType.Jacket] = new[] { 834, 835, 836 }, [WearableType.Gloves] = new[] { 827, 829, 830 },
        [WearableType.Undershirt] = new[] { 821, 822, 823 }, [WearableType.Underpants] = new[] { 824, 825, 826 },
        [WearableType.Skirt] = new[] { 921, 922, 923 }, [WearableType.Tattoo] = new[] { 1071, 1072, 1073 },
        [WearableType.Universal] = new[] { 1238, 1239, 1240 },
    };

    public static Slot[] For(WearableType t) => ByType.TryGetValue(t, out var s) ? s : Array.Empty<Slot>();
    public static int[]? TintParams(WearableType t) => Tint.TryGetValue(t, out var p) ? p : null;

    static readonly Dictionary<string, WearableType> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shape"] = WearableType.Shape, ["skin"] = WearableType.Skin, ["hair"] = WearableType.Hair, ["eyes"] = WearableType.Eyes,
        ["shirt"] = WearableType.Shirt, ["pants"] = WearableType.Pants, ["shoes"] = WearableType.Shoes, ["socks"] = WearableType.Socks,
        ["jacket"] = WearableType.Jacket, ["gloves"] = WearableType.Gloves, ["undershirt"] = WearableType.Undershirt,
        ["underpants"] = WearableType.Underpants, ["skirt"] = WearableType.Skirt, ["alpha"] = WearableType.Alpha,
        ["tattoo"] = WearableType.Tattoo, ["physics"] = WearableType.Physics, ["universal"] = WearableType.Universal,
    };

    public static WearableType? Parse(string? name) => name is not null && Names.TryGetValue(name.Trim(), out var t) ? t : null;
    public static string TypeName(WearableType t) => Names.FirstOrDefault(kv => kv.Value == t).Key ?? t.ToString().ToLowerInvariant();
    public static bool IsBodyPart(WearableType t) => t is WearableType.Shape or WearableType.Skin or WearableType.Hair or WearableType.Eyes;

    /// <summary>The viewer's slot lookup: full name or TE index, then the part before the first '_'.</summary>
    public static Slot? Find(WearableType t, string key)
    {
        var slots = For(t);
        if (int.TryParse(key, out var idx)) return slots.FirstOrDefault(s => (int)s.Index == idx);
        return slots.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
               ?? slots.FirstOrDefault(s => string.Equals(s.Name.Split('_')[0], key, StringComparison.OrdinalIgnoreCase));
    }
}
