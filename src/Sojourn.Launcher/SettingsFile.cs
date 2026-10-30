using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sojourn.Session.Control;

namespace Sojourn.Launcher;

/// <summary>
/// The settings the launcher shows, read from and written into the client's own settings file (vr-settings.json in the
/// base dir, Sojourn.Session's VrSettings, camelCase): only the keys named here are touched, everything else is kept as
/// it was. The client reads the file when it starts; while it runs the launcher leaves the file alone (the client saves
/// it whole from its menu).
///
/// Graphics presets (GraphicsPresets, shared with the headset's menu): a few of the view's settings set together. A file
/// whose values match none of them shows as "yours" and is left as it is until a preset is picked.
/// </summary>
sealed class SettingsFile
{
    readonly string _path;
    public SettingsFile(string path) => _path = path;

    public bool Exists => File.Exists(_path);

    JsonObject Read()
    {
        try { return File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    void Change(Action<JsonObject> change)
    {
        var o = Read();
        change(o);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".launcher.tmp";
        File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    // ---- the headset ----

    public bool Seated
    {
        get => Read()["seated"] is JsonValue v && v.TryGetValue(out bool b) && b;
        set => Change(o => o["seated"] = value);
    }

    // ---- graphics: the presets (Sojourn.Session's GraphicsPresets, linked; the headset's menu offers the same) ----

    public static string[] PresetNames => GraphicsPresets.Names;

    /// <summary>The preset the file matches, or "yours".</summary>
    public string Graphics
    {
        get => GraphicsPresets.Match(Read());
        set
        {
            if (!GraphicsPresets.Values.TryGetValue(value, out var keys)) return;
            Change(o => { foreach (var (k, v) in keys) o[k] = v?.DeepClone(); });
        }
    }

    /// <summary>A first start (no settings yet): the balanced preset, so the headset's shadows start capped as
    /// tools/client.py vr has them rather than at the eye's full size.</summary>
    public void FirstStart()
    {
        if (!Exists) Graphics = "balanced";
    }

    // ---- the assistant ----

    /// <summary>Whose models the assistant runs on: VrSettings.AssistantProvider and its values (assistant/provider.py):
    /// the Claude account signed in on this PC, the person's Anthropic key, or their OpenRouter key; chosen here only
    /// (the headset's menu shows the model choices for it). The keys go to ProviderKeys, never into this file.
    /// Null: not in the file, the client's default (claude).</summary>
    public const string ProviderKey = "assistantProvider";
    public static readonly (string Value, string Label)[] Providers =
    {
        ("claude", "Claude"), ("anthropic", "Anthropic"), ("openrouter", "OpenRouter"),
    };

    public string? Provider
    {
        get => Str(ProviderKey);
        set => Change(o => o[ProviderKey] = value);
    }

    /// <summary>With OpenRouter, the model: "claude" (Anthropic's, the usual three) or any OpenRouter id.</summary>
    public string OpenRouterModel
    {
        get => Str("openRouterModel") is { Length: > 0 } m ? m : "claude";
        set => Change(o => o["openRouterModel"] = value);
    }

    /// <summary>The menu's quick list (VrSettings.OpenRouterModels, kept the same): OpenRouter's rolling "latest" ids.</summary>
    public static readonly (string Id, string Name)[] OpenRouterModels =
    {
        ("claude", "claude"), ("~google/gemini-pro-latest", "gemini pro"), ("~google/gemini-flash-latest", "gemini flash"),
        ("~openai/gpt-sol-latest", "gpt sol"), ("~x-ai/grok-latest", "grok"), ("~moonshotai/kimi-latest", "kimi"),
    };

    /// <summary>With OpenRouter, only providers that don't collect data (on unless the file says otherwise).</summary>
    public bool OpenRouterDenyData
    {
        get => Bool("openRouterDenyDataCollection") ?? true;
        set => Change(o => o["openRouterDenyDataCollection"] = value);
    }

    /// <summary>The assistant under Claude Code's auto mode (on unless the file says otherwise).</summary>
    public bool AutoMode
    {
        get => Bool("assistantAutoMode") ?? true;
        set => Change(o => o["assistantAutoMode"] = value);
    }

    string? Str(string key) => Read()[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    bool? Bool(string key) => Read()[key] is JsonValue v && v.TryGetValue(out bool b) ? b : null;
}
