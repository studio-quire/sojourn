using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// What batch B2's parts keep per account, as the viewer keeps them per account (gSavedPerAccountSettings, Firestorm's
/// contact_sets.xml): contact sets and pseudonyms, the autoresponses' words, hover height, away and typing preferences.
/// One JSON object in the state dir (people.json), read once, written whole on each change. Thread-safe.
/// Claude Opus 5.5, 2026-10-27.
/// </summary>
public sealed class PeopleStore
{
    readonly AgentSession _s;
    readonly object _lock = new();
    JsonObject? _data;

    public PeopleStore(AgentSession s) => _s = s;

    string FilePath => Path.Combine(_s.StateDir, "people.json");

    JsonObject Data()
    {
        if (_data != null) return _data;
        try { _data = File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject : null; }
        catch (Exception e) { _s.Log.LogWarning(e, "people.json"); }
        return _data ??= new JsonObject();
    }

    /// <summary>Reads under the lock (the node must not escape; clone what's kept).</summary>
    public T Read<T>(Func<JsonObject, T> f) { lock (_lock) return f(Data()); }

    /// <summary>Changes and saves under the lock.</summary>
    public T Change<T>(Func<JsonObject, T> f)
    {
        lock (_lock)
        {
            var r = f(Data());
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Data().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception e) { _s.Log.LogWarning(e, "saving people.json"); }
            return r;
        }
    }

    public void Change(Action<JsonObject> f) => Change(d => { f(d); return 0; });

    public string? Text(string key) => Read(d => d.Str(key));
    public bool Flag(string key, bool dflt) => Read(d => d.Bool(key) ?? dflt);
    public double Number(string key, double dflt) => Read(d => d.Num(key) ?? dflt);
    public void Set(string key, JsonNode? value) => Change(d => { d[key] = value; });
}
