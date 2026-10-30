using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sojourn.Session.Social;

/// <summary>
/// Batch B4's settings (the social layer), kept beside vr-settings.json in social-settings.json; the keyword list is
/// the account's (Firestorm keeps FSKeywords per account), in its state dir. Defaults are the viewers' unless said:
/// chat bubbles off (UseChatBubbles), group titles on, friends' colour off (NameTagShowFriends), tags hidden behind
/// walls (LLHUDNameTag depth-tests), hover tips on after 0.7 s (ToolTipDelay) for things only, land tips off,
/// translation off, keyword alerts off until there are keywords (FSKeywordOn), mentions on, Firestorm's radar
/// reports all off. Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class SocialSettings
{
    public bool ChatBubbles { get; set; }
    public bool GroupTitles { get; set; } = true;
    public bool FriendColours { get; set; }
    public bool TagsThroughWalls { get; set; }
    public bool HoverTips { get; set; } = true;
    public float HoverTipDelay { get; set; } = 0.7f;
    public bool MentionAlerts { get; set; } = true;
    public bool KeywordAlerts { get; set; } = true;
    public bool AlertSound { get; set; } = true;
    /// <summary>Firestorm's radar reports, each off, enter, leave or both: chat range, draw distance, the region.</summary>
    public string RadarChat { get; set; } = "off";
    public string RadarDraw { get; set; } = "off";
    public string RadarRegion { get; set; } = "off";
    /// <summary>Firestorm's RadarAvatarAgeAlert: someone this many days old or younger coming within draw distance is
    /// told (0: off, the default).</summary>
    public int RadarAgeDays { get; set; }
    public bool Translate { get; set; }
    /// <summary>The language lines are translated into (ISO 639-1); empty: the system's.</summary>
    public string TranslateTo { get; set; } = "";
    public string Translator { get; set; } = "none";

    public static readonly string[] RadarModes = { "off", "enter", "leave", "both" };

    public static bool Enters(string mode) => mode is "enter" or "both";
    public static bool Leaves(string mode) => mode is "leave" or "both";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static SocialSettings Load(string path, Action<string>? warn = null)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<SocialSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception e) { warn?.Invoke($"social settings: {e.Message} (defaults used)"); }
        return new();
    }

    public void Save(string path, Action<string>? warn = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) { warn?.Invoke($"social settings: couldn't save ({e.Message})"); }
    }

    public void CopyFrom(SocialSettings o)
    {
        foreach (var p in typeof(SocialSettings).GetProperties().Where(p => p.CanWrite)) p.SetValue(this, p.GetValue(o));
    }
}
