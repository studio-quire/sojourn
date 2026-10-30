using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sojourn.Session.Social;

/// <summary>
/// Firestorm's keyword alerts (FSKeywords, kept per account; FSKeywordInChat, FSKeywordInIM, FSKeywordMatchWholeWords,
/// FSKeywordCaseSensitive): a line from someone else holding one of the person's words is lit on the feed with a
/// sound. Where Firestorm has one switch for IMs and group chat, these are apart (a busy group can say a name all day).
/// In the account's state dir as keywords.json. Claude Opus 5.5, 2026-10-28 (batch B4).
/// </summary>
public sealed class Keywords
{
    public List<string> Words { get; set; } = new();
    public bool InChat { get; set; } = true;
    public bool InIms { get; set; } = true;
    public bool InGroups { get; set; } = true;
    public bool WholeWords { get; set; }
    public bool CaseSensitive { get; set; }

    Regex? _re;
    string _reKey = "";

    public bool Matches(FeedKind kind, string text)
    {
        if (Words.Count == 0) return false;
        if (kind == FeedKind.Nearby && !InChat || kind == FeedKind.Im && !InIms || kind == FeedKind.Group && !InGroups) return false;
        var key = $"{string.Join('\u0001', Words)}|{WholeWords}|{CaseSensitive}";
        if (_re == null || key != _reKey)
        {
            var alt = string.Join("|", Words.Where(w => w.Trim().Length > 0).Select(w => Regex.Escape(w.Trim())));
            if (alt.Length == 0) return false;
            var pattern = WholeWords ? $@"(?<![\p{{L}}\p{{N}}_])(?:{alt})(?![\p{{L}}\p{{N}}_])" : $"(?:{alt})";
            _re = new Regex(pattern, (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant);
            _reKey = key;
        }
        return _re.IsMatch(text);
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathIn(string stateDir) => Path.Combine(stateDir, "keywords.json");

    public static Keywords Load(string stateDir)
    {
        try
        {
            var p = PathIn(stateDir);
            if (File.Exists(p)) return JsonSerializer.Deserialize<Keywords>(File.ReadAllText(p), Json) ?? new();
        }
        catch { }
        return new();
    }

    public void Save(string stateDir)
    {
        Directory.CreateDirectory(stateDir);
        var p = PathIn(stateDir);
        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(p + ".tmp", p, overwrite: true);
    }
}
