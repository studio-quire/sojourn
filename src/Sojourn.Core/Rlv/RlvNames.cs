namespace Sojourn.Core.Rlv;

/// <summary>Attachment points and wearable layers by the names RLV uses (the viewer's avatar_lad.xml names, lowercased;
/// "root" for Avatar Center as RLVa allows), and the attachment groups.</summary>
public static class RlvNames
{
    /// <summary>Points by index (1..55), as the viewer's avatar_lad.xml names them.</summary>
    public static readonly string[] Points =
    {
        "", "Chest", "Skull", "Left Shoulder", "Right Shoulder", "Left Hand", "Right Hand", "Left Foot", "Right Foot",
        "Spine", "Pelvis", "Mouth", "Chin", "Left Ear", "Right Ear", "Left Eyeball", "Right Eyeball", "Nose",
        "R Upper Arm", "R Forearm", "L Upper Arm", "L Forearm", "Right Hip", "R Upper Leg", "R Lower Leg", "Left Hip",
        "L Upper Leg", "L Lower Leg", "Stomach", "Left Pec", "Right Pec", "Center 2", "Top Right", "Top", "Top Left",
        "Center", "Bottom Left", "Bottom", "Bottom Right", "Neck", "Avatar Center", "Left Ring Finger",
        "Right Ring Finger", "Tail Base", "Tail Tip", "Left Wing", "Right Wing", "Jaw", "Alt Left Ear", "Alt Right Ear",
        "Alt Left Eye", "Alt Right Eye", "Tongue", "Groin", "Left Hind Foot", "Right Hind Foot",
    };

    public const int Count = 55;

    /// <summary>Wearable layers by LibreMetaverse's WearableType (the viewer's LLWearableType order).</summary>
    public static readonly string[] Layers =
    {
        "shape", "skin", "hair", "eyes", "shirt", "pants", "shoes", "socks", "jacket", "gloves", "undershirt",
        "underpants", "skirt", "alpha", "tattoo", "physics", "universal",
    };

    /// <summary>@getoutfit's order (rlvhandler.cpp onGetOutfit).</summary>
    public static readonly int[] OutfitOrder = { 9, 8, 5, 4, 6, 12, 7, 11, 10, 1, 3, 2, 0, 13, 14, 15, 16 };

    public static bool IsBodyPart(int layer) => layer is >= 0 and <= 3;

    static readonly Dictionary<string, int> ByName = Build();

    static Dictionary<string, int> Build()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i <= Count; i++) d[Points[i]] = i;
        d["root"] = 40;
        return d;
    }

    /// <summary>A point's index by name (0: not a point).</summary>
    public static int AttachPoint(string name) => ByName.GetValueOrDefault(name.Trim(), 0);

    public static string PointName(int i) => i is > 0 and <= Count ? Points[i] : "";

    /// <summary>A layer by name (-1: not a layer).</summary>
    public static int WearableType(string name) => Array.FindIndex(Layers, l => l.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static readonly string[] Groups = { "head", "torso", "arms", "legs", "hud" };

    /// <summary>The group of a point (RLVa's rlvAttachGroupFromIndex): head, torso, arms, legs or hud.</summary>
    public static string Group(int point) => point switch
    {
        2 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 47 or 48 or 49 or 50 or 51 or 52 => "head",
        3 or 4 or 5 or 6 or 18 or 19 or 20 or 21 or 41 or 42 => "arms",
        7 or 8 or 22 or 23 or 24 or 25 or 26 or 27 or 54 or 55 => "legs",
        >= 31 and <= 38 => "hud",
        _ => "torso",
    };

    public static bool IsHud(int point) => point is >= 31 and <= 38;

    /// <summary>The text in the last (...) of a name ("Collar (neck)" -> "neck"), or "".</summary>
    public static string LastParenthesised(string s)
    {
        int close = s.LastIndexOf(')');
        if (close < 0) return "";
        int open = s.LastIndexOf('(', close);
        return open < 0 ? "" : s[(open + 1)..close];
    }

    /// <summary>The text in the first (...) and where it starts.</summary>
    public static (string Text, int At) FirstParenthesised(string s)
    {
        int open = s.IndexOf('(');
        if (open < 0) return ("", -1);
        int close = s.IndexOf(')', open);
        return close < 0 ? ("", -1) : (s[(open + 1)..close], open);
    }
}
