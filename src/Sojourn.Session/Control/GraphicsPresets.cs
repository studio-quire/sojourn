using System.Text.Json.Nodes;

namespace Sojourn.Session.Control;

/// <summary>
/// The graphics presets (light, balanced, full): a few of the view's settings set together, the same in the launcher
/// (src/Sojourn.Launcher, which links this file and works on vr-settings.json's keys) and the headset's settings menu
/// (view > graphics). Keys are vr-settings.json's (VrSettings in camelCase). Settings that match none read as "yours"
/// and are left alone until a preset is picked. Plain data and no dependencies, so the launcher can link it.
/// Written by Claude Opus 5.5, 2026-10-29.
/// </summary>
public static class GraphicsPresets
{
    public static readonly string[] Names = { "light", "balanced", "full" };
    public const string Yours = "yours";

    /// <summary>VrSettings' own defaults for the keys the presets set (a key missing from the file reads as these).</summary>
    public static readonly IReadOnlyDictionary<string, JsonNode?> Defaults = new Dictionary<string, JsonNode?>
    {
        ["drawDistance"] = 128, ["shadows"] = true, ["shadowCap"] = null, ["reflections"] = true, ["mirrors"] = true,
        ["mirrorResolution"] = 0.5, ["detail"] = 1.75, ["farShadows"] = 1024, ["fullAvatars"] = 16, ["resolution"] = 1,
    };

    /// <summary>light: for a modest GPU or a busy place (the eyes' size following the GPU); balanced: what the client
    /// was tuned at on an RTX 5070 (the headset's 1536 shadow cap); full: further and sharper, for a faster card.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonNode?>> Values = new Dictionary<string, IReadOnlyDictionary<string, JsonNode?>>
    {
        ["light"] = new Dictionary<string, JsonNode?>
        {
            ["drawDistance"] = 64, ["shadows"] = true, ["shadowCap"] = 1024, ["reflections"] = false, ["mirrors"] = false,
            ["mirrorResolution"] = 0.5, ["detail"] = 1, ["farShadows"] = 0, ["fullAvatars"] = 8, ["resolution"] = null,
        },
        ["balanced"] = new Dictionary<string, JsonNode?>
        {
            ["drawDistance"] = 128, ["shadows"] = true, ["shadowCap"] = 1536, ["reflections"] = true, ["mirrors"] = true,
            ["mirrorResolution"] = 0.5, ["detail"] = 1.75, ["farShadows"] = 1024, ["fullAvatars"] = 16, ["resolution"] = 1,
        },
        ["full"] = new Dictionary<string, JsonNode?>
        {
            ["drawDistance"] = 256, ["shadows"] = true, ["shadowCap"] = 3072, ["reflections"] = true, ["mirrors"] = true,
            ["mirrorResolution"] = 1, ["detail"] = 2.5, ["farShadows"] = 2048, ["fullAvatars"] = 24, ["resolution"] = 1,
        },
    };

    /// <summary>The preset the settings match (read: a key's value as the file has it, null when it's missing or
    /// null there), or "yours".</summary>
    public static string Match(Func<string, (bool Present, JsonNode? Value)> read)
    {
        foreach (var name in Names)
            if (Values[name].All(k => Same(read(k.Key) is (true, var v) ? v : Defaults[k.Key], k.Value))) return name;
        return Yours;
    }

    /// <summary>Match over a JSON object of the settings (the file, or VrSettings serialised).</summary>
    public static string Match(JsonObject o) => Match(k => o.TryGetPropertyValue(k, out var v) ? (true, v) : (false, null));

    static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonValue va && b is JsonValue vb && Number(va) is { } da && Number(vb) is { } db) return Math.Abs(da - db) < 1e-4;
        return JsonNode.DeepEquals(a, b);
    }

    /// <summary>A preset value as a number, whatever it was written as (an int, a double), or null.</summary>
    public static double? Number(JsonNode? n) => n is JsonValue v ? Number(v) : null;

    static double? Number(JsonValue v)
    {
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out float f)) return f;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return l;
        return v.GetValueKind() == System.Text.Json.JsonValueKind.Number && double.TryParse(v.ToJsonString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;
    }
}
