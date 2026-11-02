using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sojourn.Session.Assistant;

/// <summary>
/// OpenRouter's models for the person's choice of model there (the launcher's box and list, the headset menu's
/// "openrouter model" row, Sojourn Access's Alt+0), by their real tags (google/gemini-4-argon), never OpenRouter's rolling
/// aliases (~google/gemini-pro-latest).
/// - <see cref="Claude"/> ("claude", the default): Claude's three models as the assistant's model setting picks them
///   (quick, sonnet, opus), Claude Code's ids mapped by provider.py's or_slug to <see cref="ClaudeTags"/>.
/// - <see cref="Quick"/>: the current model of each of OpenRouter's rolling families (each alias's alias_target), when it
///   can act for the person (tool use, OpenRouter's Anthropic-compatible /messages, which provider.py's proxy speaks)
///   and see pictures; Claude's three left out, being the first entry already.
/// - <see cref="All"/>: every model that can act, for matching what's typed. Any id typed is still taken as it is.
/// Read from OpenRouter's public list (no key) off the caller's thread, kept in a small cache file, fetched again only
/// when that's 3 days old, and only once something asks (<see cref="Refresh"/>), i.e. only for a person using
/// OpenRouter. Offline and before the first fetch: the cache, else the list built in below (2026-10-30).
/// The launcher links this file, so it depends on nothing else of the session's.
/// Written by Claude Opus 5.5, 2026-10-30.
/// </summary>
public static class OpenRouterCatalog
{
    public record Model(string Id, string Name, bool Pictures);

    /// <summary>The stored value for Claude's three models, by the model setting.</summary>
    public const string Claude = "claude";
    public const string ClaudeName = "Claude Haiku · Sonnet · Opus";
    /// <summary>What Claude Code's model ids (assistant/brain.py MODELS) become on OpenRouter (provider.py or_slug).</summary>
    public static readonly string[] ClaudeTags = { "anthropic/claude-haiku-5.5", "anthropic/claude-sonnet-5.5", "anthropic/claude-opus-5.5" };
    /// <summary>The three tags in a line: anthropic/claude-haiku-5.5 · sonnet-5.5 · opus-5.5.</summary>
    public static string ClaudeTagLine => ClaudeTags[0] + string.Concat(ClaudeTags.Skip(1).Select(t => " · " + t["anthropic/claude-".Length..]));

    // OpenRouter's families' current models with tools and pictures, as its list had them on 2026-10-30
    static readonly Model[] BuiltIn =
    {
        new("anthropic/claude-fable-5.1", "Claude Fable 5.1", true),
        new("deepseek/deepseek-v4.1-flash-1017", "DeepSeek V4.1 Flash 1017", true),
        new("google/gemini-3.8-flash", "Gemini 3.8 Flash", true),
        new("google/gemini-4-argon", "Gemini 4 Argon", true),
        new("moonshotai/kimi-k3", "Kimi K3", true),
        new("openai/gpt-5.4-mini", "GPT-5.4 Mini", true),
        new("openai/gpt-6-astra", "GPT-6 Astra", true),
        new("openai/gpt-6-luna", "GPT-6 Luna", true),
        new("openai/gpt-6.1-sol", "GPT-6.1 Sol", true),
        new("openai/gpt-6.1-terra", "GPT-6.1 Terra", true),
        new("unbiased/pareto-26.10", "Pareto 26.10", true),
        new("x-ai/grok-4.7", "Grok 4.7", true),
        new("z-ai/glm-5.3-flash", "GLM 5.3 Flash", true),
    };

    static readonly object Gate = new();
    static Model[] _quick = BuiltIn, _all = BuiltIn;
    static bool _loaded;
    static Task? _refreshing;

    /// <summary>A fetch or the cache changed the lists (raised on a worker thread).</summary>
    public static event Action? Changed;

    public static IReadOnlyList<Model> Quick { get { Load(); lock (Gate) return _quick; } }
    public static IReadOnlyList<Model> All { get { Load(); lock (Gate) return _all; } }

    /// <summary>Its name for an id: Claude's three, a model OpenRouter lists, or null (an id typed that it doesn't).</summary>
    public static string? NameOf(string id)
    {
        if (id == Claude) return ClaudeName;
        Load();
        lock (Gate) return _all.FirstOrDefault(m => m.Id == id)?.Name ?? _quick.FirstOrDefault(m => m.Id == id)?.Name;
    }

    /// <summary>The ids that match what's typed (in the id or the name), quick ones first; at most <paramref name="max"/>.</summary>
    public static List<Model> Matching(string typed, int max)
    {
        var t = typed.Trim();
        if (t.Length == 0) return new();
        bool Hit(Model m) => m.Id.Contains(t, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(t, StringComparison.OrdinalIgnoreCase);
        var quick = Quick.Where(Hit).ToList();
        return quick.Concat(All.Where(m => Hit(m) && !quick.Any(q => q.Id == m.Id))).Take(max).ToList();
    }

    /// <summary>Fetches OpenRouter's list in the background if the cache is missing or 3 days old (never blocks).</summary>
    public static void Refresh()
    {
        lock (Gate)
        {
            if (_refreshing is { IsCompleted: false }) return;
            _refreshing = Task.Run(async () =>
            {
                Load();
                try
                {
                    if (File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < TimeSpan.FromDays(3)) return;
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Sojourn");
                    var text = await http.GetStringAsync(Upstream + "/v1/models");
                    var (quick, all) = Parse(JsonNode.Parse(text));
                    if (quick.Length == 0) return;   // not the list we know: keep what we have
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                    var tmp = CachePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(new Cache(quick, all)));
                    File.Move(tmp, CachePath, overwrite: true);
                    lock (Gate) { _quick = quick; _all = all; }
                    Changed?.Invoke();
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException
                                              or UnauthorizedAccessException or InvalidOperationException) { }
            });
        }
    }

    record Cache(Model[] Quick, Model[] All);

    static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(CachePath) && JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath)) is { Quick.Length: > 0 } c)
                    (_quick, _all) = (c.Quick, c.All is { Length: > 0 } ? c.All : c.Quick);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>OpenRouter's /v1/models answer read into the two lists.</summary>
    public static (Model[] Quick, Model[] All) Parse(JsonNode? list)
    {
        var data = (list?["data"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        var byId = new Dictionary<string, JsonObject>();
        foreach (var m in data) if ((string?)m["id"] is { Length: > 0 } id) byId[id] = m;

        static bool Acts(JsonObject m) =>
            (m["supported_parameters"] as JsonArray)?.Any(p => (string?)p == "tools") == true
            && (m["endpoints"] is not JsonArray eps || eps.Any(e => (string?)e == "/api/v1/messages"));
        static bool Sees(JsonObject m) =>
            (m["architecture"]?["input_modalities"] as JsonArray)?.Any(p => (string?)p == "image") == true;
        static Model Of(string id, JsonObject m)
        {
            var name = (string?)m["name"] ?? id;
            int colon = name.IndexOf(": ", StringComparison.Ordinal);   // "Google: Gemini 4 Argon" -> "Gemini 4 Argon"
            return new Model(id, colon > 0 ? name[(colon + 2)..] : name, Sees(m));
        }

        var all = byId.Where(kv => !kv.Key.StartsWith('~') && !kv.Key.Contains(':') && Acts(kv.Value))
            .Select(kv => Of(kv.Key, kv.Value)).OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        var quick = new List<Model>();
        foreach (var m in data)
            if ((string?)m["alias_target"]?["slug"] is { Length: > 0 } target && byId.TryGetValue(target, out var t)
                && !target.Contains(':') && Acts(t) && Sees(t) && !ClaudeTags.Contains(target) && quick.All(q => q.Id != target))
                quick.Add(Of(target, t));
        // Anthropic's first (beside Claude's three), then by maker and name
        var ordered = quick.OrderBy(m => !m.Id.StartsWith("anthropic/", StringComparison.Ordinal))
            .ThenBy(m => m.Id[..Math.Max(0, m.Id.IndexOf('/'))], StringComparer.Ordinal).ThenBy(m => m.Name, StringComparer.Ordinal).ToArray();
        return (ordered, all);
    }

    // SOJOURN_PROVIDER_UPSTREAM: a test server, as provider.py has it
    static string Upstream => (Environment.GetEnvironmentVariable("SOJOURN_PROVIDER_UPSTREAM") is { Length: > 0 } u ? u : "https://openrouter.ai/api").TrimEnd('/');

    /// <summary>The repo's local/cache, or a release's %LOCALAPPDATA%\Sojourn\cache.</summary>
    static string CachePath
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "src", "Sojourn.Client")) && Directory.Exists(Path.Combine(d.FullName, "assistant")))
                    return Path.Combine(d.FullName, "local", "cache", "openrouter-models.json");
            return Path.Combine(DataFolder.Root, "cache", "openrouter-models.json");
        }
    }
}
