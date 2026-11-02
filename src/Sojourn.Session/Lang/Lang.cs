using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sojourn.Session;

/// <summary>
/// Sojourn's own words in the person's language (docs/languages.md). The code says everything in English; the catalogs
/// (Lang/*.json, embedded) hold each English string once with its translations, and whatever has none in the language in
/// use comes out in English. Self-contained: the launcher links this file and embeds the same catalogs.
///
/// - <see cref="T(string)"/>: a fixed string. On a miss it also tries the templates, so a string built elsewhere in
///   English ("haiku effort", "≤ 3 days") still finds "{0} effort", "≤ {0} days": that's how the drawn menu translates
///   its rows as it draws them, while the rows keep their English labels as keys (tips, tests' presses).
/// - <see cref="F"/>: a template with {0}, {1}... filled in after it's translated.
/// - A key may carry a context after #, "look#assistant", for an English word that needs two translations: T(text, ctx)
///   tries "text#ctx" first, then "text".
///
/// The app chooses: <see cref="Use"/> with "auto" (Windows' display language, when Sojourn has it) or a code. An app
/// that never calls it (Sojourn Access) stays in English, so the session's shared strings stay English there.
/// </summary>
public static class Lang
{
    /// <summary>A language Sojourn speaks: its code, its own name, its name in English, a BCP 47 tag for fonts.</summary>
    public sealed record Language(string Code, string Name, string English, string Tag);

    /// <summary>The viewers' languages (the official viewer's and Firestorm's xui folders), by their own names.</summary>
    public static readonly IReadOnlyList<Language> All =
    [
        new("en", "English", "English", "en-US"),
        new("az", "Azərbaycanca", "Azerbaijani", "az-Latn-AZ"),
        new("da", "Dansk", "Danish", "da-DK"),
        new("de", "Deutsch", "German", "de-DE"),
        new("es", "Español", "Spanish", "es-ES"),
        new("fr", "Français", "French", "fr-FR"),
        new("it", "Italiano", "Italian", "it-IT"),
        new("pl", "Polski", "Polish", "pl-PL"),
        new("pt", "Português", "Portuguese", "pt-BR"),
        new("tr", "Türkçe", "Turkish", "tr-TR"),
        new("ru", "Русский", "Russian", "ru-RU"),
        new("ja", "日本語", "Japanese", "ja-JP"),
        new("zh-Hans", "中文（简体）", "Chinese (Simplified)", "zh-Hans"),
        new("zh", "中文（繁體）", "Chinese (Traditional)", "zh-TW"),
    ];

    /// <summary>The setting's values: auto, then every code.</summary>
    public static string[] Settings => ["auto", .. All.Select(l => l.Code)];

    static volatile Table _table = new("en", new(), [], false);

    /// <summary>The language in use.</summary>
    public static Language Current => Find(_table.Code)!;
    public static string Code => _table.Code;

    /// <summary>The app chose a language (Use was called): false in an app that stays English.</summary>
    public static bool Chosen => _table.Chosen;

    /// <summary>The language in use changed (redraw what was drawn in the old one).</summary>
    public static event Action? Changed;

    public static Language? Find(string? code) => All.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>What "auto" means here: Windows' display language if Sojourn has it, else English. Chinese goes by
    /// script: Taiwan's, Hong Kong's and Macau's (and any marked Hant) Traditional, the rest Simplified (the user,
    /// 2026-11-01); Portuguese of any region is the viewers' (Brazil's).</summary>
    public static string System() => For(CultureInfo.CurrentUICulture);

    /// <summary>A culture's language as Sojourn has it, else English.</summary>
    public static string For(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (c.TwoLetterISOLanguageName == "zh")
                return culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    || culture.Name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                    || culture.Name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                    || culture.Name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase) ? "zh" : "zh-Hans";
            if (Find(c.TwoLetterISOLanguageName) is { } l) return l.Code;
        }
        return "en";
    }

    /// <summary>A setting's value as a code: auto (or anything unknown) is <see cref="System"/>.</summary>
    public static string Resolve(string? setting) => Find(setting)?.Code ?? System();

    /// <summary>Use a language from now on ("auto" or a code).</summary>
    public static void Use(string? setting)
    {
        var code = Resolve(setting);
        if (code == _table.Code && _table.Chosen) return;
        _table = Load(code);
        Changed?.Invoke();
    }

    /// <summary>A fixed string in the language in use (itself if there's no translation).</summary>
    public static string T(string english)
    {
        if (string.IsNullOrEmpty(english)) return english;
        var t = _table;
        if (t.Exact.TryGetValue(english, out var s)) return s;
        if (Record) Asked(english);
        if (t.Code == "en" || t.Templates.Length == 0) return english;
        if (t.Matched.TryGetValue(english, out var m)) return m;
        var found = Match(t, english) ?? english;
        if (t.Matched.Count > 4096) t.Matched.Clear();
        t.Matched[english] = found;
        return found;
    }

    /// <summary>A string whose translation depends on where it is ("look#assistant"), else as <see cref="T(string)"/>.</summary>
    public static string T(string english, string? context) =>
        context != null && _table.Exact.TryGetValue(english + "#" + context, out var s) ? s : T(english);

    /// <summary>A null-tolerant T, for optional words.</summary>
    public static string? Tn(string? english) => english == null ? null : T(english);

    /// <summary>A template ("{0} offers a teleport") translated, then filled in.</summary>
    public static string F(string template, params object?[] args)
    {
        var t = _table;
        if (!t.Exact.TryGetValue(template, out var s)) { s = template; if (Record) Asked(template); }
        return Fill(s, args);
    }

    static string Fill(string s, IReadOnlyList<object?> args)
    {
        if (args.Count == 0 || s.IndexOf('{') < 0) return s;
        var sb = new StringBuilder(s.Length + 16);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{' && i + 2 < s.Length && char.IsAsciiDigit(s[i + 1]) && s[i + 2] == '}')
            {
                int n = s[i + 1] - '0';
                sb.Append(n < args.Count ? Show(args[n]) : "");
                i += 2;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    static string Show(object? o) => o switch
    {
        null => "",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => o.ToString() ?? "",
    };

    static string? Match(Table t, string text)
    {
        foreach (var (re, translated) in t.Templates)
        {
            var m = re.Match(text);
            if (!m.Success) continue;
            var args = new object?[m.Groups.Count - 1];
            for (int g = 1; g < m.Groups.Count; g++)
            {
                // a filled-in part may itself be one of ours ("on" in "radar · on")
                var part = m.Groups[g];
                int n = int.Parse(part.Name[1..]);
                if (n < args.Length) args[n] = t.Exact.TryGetValue(part.Value, out var inner) ? inner : part.Value;
            }
            return Fill(translated, args);
        }
        return null;
    }

    // --- the catalogs ------------------------------------------------------------------------------------------

    sealed record Table(string Code, Dictionary<string, string> Exact, (Regex Re, string Translated)[] Templates, bool Chosen)
    {
        public readonly ConcurrentDictionary<string, string> Matched = new();
    }

    /// <summary>Every catalog entry: English key -> {code: translation, "_": a note for translators}.</summary>
    public static Dictionary<string, Dictionary<string, string>> Catalog() => _catalog.Value;

    static readonly Lazy<Dictionary<string, Dictionary<string, string>>> _catalog = new(ReadCatalog);

    static Dictionary<string, Dictionary<string, string>> ReadCatalog()
    {
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var asm = typeof(Lang).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("lang/", StringComparison.Ordinal) && n.EndsWith(".json")).Order())
        {
            try
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    var tr = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var p in entry.Value.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v) tr[p.Name] = v;
                    all[entry.Name] = tr;   // (a key in two files: the later file's, by name)
                }
            }
            catch (JsonException e) { Console.Error.WriteLine($"lang: {name}: {e.Message}"); }
        }
        return all;
    }

    static Table Load(string code)
    {
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var templates = new List<(Regex, string, int)>();
        if (code != "en")
            foreach (var (key, tr) in Catalog())
            {
                if (!tr.TryGetValue(code, out var s)) continue;
                exact[key] = s;
                if (key.Contains('{') && Template(key) is { } re) templates.Add((re, s, key.Length));
            }
        // the most specific first: the longest fixed words
        return new Table(code, exact, templates.OrderByDescending(x => x.Item3).Select(x => (x.Item1, x.Item2)).ToArray(), true);
    }

    static readonly Regex Hole = new(@"\{(\d)\}", RegexOptions.Compiled);

    /// <summary>A template's English as a pattern: its words as they are, each {n} any text (a context suffix
    /// dropped). Null if it has no hole.</summary>
    static Regex? Template(string key)
    {
        int hash = key.LastIndexOf('#');
        if (hash > 0 && !key[hash..].Contains('}')) key = key[..hash];
        var holes = Hole.Matches(key);
        if (holes.Count == 0) return null;
        var sb = new StringBuilder("^");
        int at = 0;
        var seen = new HashSet<string>();
        foreach (Match h in holes)
        {
            sb.Append(Regex.Escape(key[at..h.Index]));
            var g = "g" + h.Groups[1].Value;
            sb.Append(seen.Add(g) ? $"(?<{g}>.+?)" : $"\\k<{g}>");
            at = h.Index + h.Length;
        }
        sb.Append(Regex.Escape(key[at..])).Append('$');
        return new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    // --- what was asked for and isn't in the catalog (tools/lang.py: SOJOURN_LANG_MISSING=<file>) ---------------

    static readonly string? RecordTo = Environment.GetEnvironmentVariable("SOJOURN_LANG_MISSING");
    static bool Record => RecordTo != null;
    static readonly ConcurrentDictionary<string, byte> _asked = new();

    static readonly Lazy<Regex[]> _allTemplates = new(() => Catalog().Keys.Where(k => k.Contains('{')).Select(Template).OfType<Regex>().ToArray());

    static void Asked(string english)
    {
        if (Catalog().ContainsKey(english) || _asked.ContainsKey(english) || _allTemplates.Value.Any(re => re.IsMatch(english))
            || !_asked.TryAdd(english, 0)) return;
        try { lock (_asked) File.AppendAllText(RecordTo!, JsonSerializer.Serialize(english) + "\n"); }
        catch (IOException) { }
    }
}
