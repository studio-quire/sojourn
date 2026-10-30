using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sojourn.Session;

/// <summary>
/// A few of the person's choices kept per machine beside vr-settings.json (whose class is batch M's): voice's hearing
/// place and media's first click and auto-zoom, as the viewer keeps them in its own settings. A JSON object, written
/// at each change. Claude Opus 5.5, 2026-10-29 (batch B10).
/// </summary>
public sealed class LocalPrefs
{
    readonly string _path;
    readonly object _lock = new();
    JsonObject _data;

    public LocalPrefs(string baseDir)
    {
        _path = Path.Combine(baseDir, "voice-media-settings.json");
        try { _data = File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new() : new(); }
        catch { _data = new(); }
    }

    public string? Text(string key) { lock (_lock) return _data[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; }
    public bool Flag(string key, bool dflt) { lock (_lock) return _data[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : dflt; }

    public void Set(string key, JsonNode? value)
    {
        lock (_lock)
        {
            _data[key] = value;
            try { File.WriteAllText(_path, _data.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }
    }
}
