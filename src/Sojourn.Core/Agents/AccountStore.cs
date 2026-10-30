using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// A JSON object kept per account in the state dir (as the viewer's gSavedPerAccountSettings): read once, written whole
/// on each change, thread-safe. PeopleStore is B2's own; this one is for batch B7's (inventory.json: protected folders,
/// the AO's switches and folder, the pose stand). Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class AccountStore
{
    readonly AgentSession _s;
    readonly string _file;
    readonly object _lock = new();
    JsonObject? _data;

    public AccountStore(AgentSession s, string file)
    {
        _s = s;
        _file = file;
    }

    string FilePath => Path.Combine(_s.StateDir, _file);

    JsonObject Data()
    {
        if (_data != null) return _data;
        try { _data = File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject : null; }
        catch (Exception e) { _s.Log.LogWarning(e, "{File}", _file); }
        return _data ??= new JsonObject();
    }

    public T Read<T>(Func<JsonObject, T> f) { lock (_lock) return f(Data()); }

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
            catch (Exception e) { _s.Log.LogWarning(e, "saving {File}", _file); }
            return r;
        }
    }

    public void Change(Action<JsonObject> f) => Change(d => { f(d); return 0; });

    public string? Text(string key) => Read(d => d.Str(key));
    public bool Flag(string key, bool dflt) => Read(d => d.Bool(key) ?? dflt);
    public void Set(string key, JsonNode? value) => Change(d => { d[key] = value; });

    /// <summary>A list of ids under a key.</summary>
    public HashSet<LibreMetaverse.UUID> Ids(string key) => Read(d =>
    {
        var set = new HashSet<LibreMetaverse.UUID>();
        if (d[key] is JsonArray arr)
            foreach (var n in arr)
                if (LibreMetaverse.UUID.TryParse(n?.ToString() ?? "", out var id)) set.Add(id);
        return set;
    });

    public void SetIds(string key, IEnumerable<LibreMetaverse.UUID> ids) =>
        Set(key, new JsonArray(ids.Select(i => (JsonNode)i.ToString()).ToArray()));
}
