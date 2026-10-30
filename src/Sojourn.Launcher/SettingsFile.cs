using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sojourn.Launcher;

/// <summary>
/// The settings the launcher shows, read from and written into the client's own settings file (vr-settings.json in the
/// base dir, Sojourn.Session's VrSettings, camelCase): only the keys named here are touched, everything else is kept as
/// it was. The client reads the file when it starts; while it runs the launcher leaves the file alone (the client saves
/// it whole from its menu).
///
/// Graphics presets are the launcher's own idea: a few of the view's settings set together. A file whose values match
/// none of them shows as "yours" (changed in the headset's menu) and is left as it is until a preset is picked.
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

    // ---- graphics ----

    public static readonly string[] PresetNames = { "light", "balanced", "full" };

    /// <summary>VrSettings' own defaults for the keys the presets set (a key missing from the file reads as these).</summary>
    static readonly Dictionary<string, JsonNode?> Defaults = new()
    {
        ["drawDistance"] = 128, ["shadows"] = true, ["shadowCap"] = null, ["reflections"] = true, ["mirrors"] = true,
        ["mirrorResolution"] = 0.5, ["detail"] = 1.75, ["farShadows"] = 1024, ["fullAvatars"] = 16, ["resolution"] = 1,
    };

    /// <summary>light: for a modest GPU or a busy place (the eyes' size following the GPU); balanced: what the client
    /// was tuned at on this PC's RTX 5070 (the headset's 1536 shadow cap); full: further and sharper, for a faster card.</summary>
    static readonly Dictionary<string, Dictionary<string, JsonNode?>> Presets = new()
    {
        ["light"] = new()
        {
            ["drawDistance"] = 64, ["shadows"] = true, ["shadowCap"] = 1024, ["reflections"] = false, ["mirrors"] = false,
            ["mirrorResolution"] = 0.5, ["detail"] = 1, ["farShadows"] = 0, ["fullAvatars"] = 8, ["resolution"] = null,
        },
        ["balanced"] = new()
        {
            ["drawDistance"] = 128, ["shadows"] = true, ["shadowCap"] = 1536, ["reflections"] = true, ["mirrors"] = true,
            ["mirrorResolution"] = 0.5, ["detail"] = 1.75, ["farShadows"] = 1024, ["fullAvatars"] = 16, ["resolution"] = 1,
        },
        ["full"] = new()
        {
            ["drawDistance"] = 256, ["shadows"] = true, ["shadowCap"] = 3072, ["reflections"] = true, ["mirrors"] = true,
            ["mirrorResolution"] = 1, ["detail"] = 2.5, ["farShadows"] = 2048, ["fullAvatars"] = 24, ["resolution"] = 1,
        },
    };

    static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonValue va && b is JsonValue vb && va.TryGetValue(out double da) && vb.TryGetValue(out double db)) return Math.Abs(da - db) < 1e-4;
        return JsonNode.DeepEquals(a, b);
    }

    /// <summary>The preset the file matches, or "yours".</summary>
    public string Graphics
    {
        get
        {
            var o = Read();
            foreach (var (name, keys) in Presets)
                if (keys.All(k => Same(o.TryGetPropertyValue(k.Key, out var v) ? v : Defaults[k.Key], k.Value))) return name;
            return "yours";
        }
        set
        {
            if (!Presets.TryGetValue(value, out var keys)) return;
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
    /// the Claude account signed in on this PC, the person's Anthropic key, or their OpenRouter key. The launcher only
    /// offers the choice; the keys are pasted in the client's menu or Access (Sojourn.Session's ProviderKeys) and never
    /// pass through here. Null: not in the file, the client's default (claude).</summary>
    public const string ProviderKey = "assistantProvider";
    public static readonly (string Value, string Label)[] Providers =
    {
        ("claude", "Claude"), ("anthropic", "Anthropic"), ("openrouter", "OpenRouter"),
    };

    public string? Provider
    {
        get => Read()[ProviderKey] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        set => Change(o => o[ProviderKey] = value);
    }
}
