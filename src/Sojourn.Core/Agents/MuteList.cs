using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// The block list. The viewer filters muted residents and objects on the client side (their chat, IMs, offers,
/// dialogs, and the chat of a muted resident's objects); the server's mute list only stores it. So the host keeps
/// the list in the state dir (and sends changes to the grid, which keeps them too where it has a mute service) and
/// the event handlers drop what a viewer would hide. Written by Claude Opus 5.5, 2026-10-16.
/// Each entry carries the viewer's mute flags (LLMute: 0 mutes everything; a set bit lets that kind through), so a
/// voice-only mute (the viewer's "Mute voice", flags 0xD) is kept on the account's list without hiding the person's
/// chat; a full block mutes their voice too, as LLMuteList::isMuted(id, flagVoiceChat) does (batch B10, 2026-10-29).
/// </summary>
public sealed class MuteList
{
    public sealed record Entry(UUID Id, string Name, string Type, MuteFlags Flags = MuteFlags.Default);

    readonly AgentSession _s;
    readonly ConcurrentDictionary<string, Entry> _entries = new();
    bool _loaded;

    public MuteList(AgentSession s) => _s = s;

    /// <summary>Raised after any change (an entry added, changed or removed, or the grid's list merged in).</summary>
    public event Action? Changed;

    string FilePath => Path.Combine(_s.StateDir, "mutes.json");
    static string Key(UUID id, string name) => id != UUID.Zero ? id.ToString() : "name:" + name.ToLowerInvariant();
    static MuteType TypeOf(string type) => type switch { "object" => MuteType.Object, "name" => MuteType.ByName, "group" => MuteType.Group, _ => MuteType.Resident };

    void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var n in JsonNode.Parse(File.ReadAllText(FilePath)) as JsonArray ?? new JsonArray())
                if (n is JsonObject o && UUID.TryParse(o.Str("id") ?? "", out var id))
                {
                    var e = new Entry(id, o.Str("name") ?? "", o.Str("type") ?? "resident", (MuteFlags)(o.Int("flags", 0) & (int)MuteFlags.All));
                    _entries[Key(e.Id, e.Name)] = e;
                }
        }
        catch (Exception x) { _s.Log.LogWarning(x, "mute list"); }
    }

    void Save()
    {
        try { File.WriteAllText(FilePath, List().ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception x) { _s.Log.LogWarning(x, "saving the mute list"); }
    }

    public JsonArray List()
    {
        Load();
        var arr = new JsonArray();
        foreach (var e in _entries.Values.OrderBy(e => e.Name))
        {
            var o = new JsonObject { ["id"] = e.Id.ToString(), ["name"] = e.Name, ["type"] = e.Type };
            if (e.Flags != MuteFlags.Default)
            {
                o["flags"] = (int)e.Flags;
                if (e.Flags == (MuteFlags.All & ~MuteFlags.VoiceChat)) o["voice_only"] = true;
            }
            arr.Add(o);
        }
        return arr;
    }

    Entry? Find(UUID id, string? name)
    {
        Load();
        if (_entries.IsEmpty) return null;
        if (id != UUID.Zero && _entries.TryGetValue(id.ToString(), out var e)) return e;
        return !string.IsNullOrEmpty(name) && _entries.TryGetValue("name:" + name.ToLowerInvariant(), out var n) ? n : null;
    }

    /// <summary>Muted for one kind of thing (LLMuteList::isMuted with flags): an entry whose flag for it isn't set.</summary>
    public bool MutedFor(UUID id, string? name, MuteFlags what) => Find(id, name) is { } e && (e.Flags & what) == 0;

    /// <summary>Blocked: muted for text chat (and so for what the viewer hides with it). A voice-only mute doesn't count.</summary>
    public bool IsMuted(UUID id, string? name = null) => MutedFor(id, name, MuteFlags.TextChat);

    /// <summary>Muted directly, or an object whose owner is muted (the viewer hides a muted resident's objects' chat).</summary>
    public bool IsMuted(UUID id, string? name, UUID owner) => IsMuted(id, name) || (owner != UUID.Zero && IsMuted(owner));

    /// <summary>Their voice is muted in the person's hearing: a voice mute, or a full block.</summary>
    public bool VoiceMuted(UUID id) => id != UUID.Zero && MutedFor(id, null, MuteFlags.VoiceChat);

    /// <summary>Mutes what's named (Default: everything, a block), as LLMuteList::add: an entry already there keeps
    /// muting what it did, and mutes what's named now too.</summary>
    public Entry Add(UUID id, string name, string type, GridClient? c, MuteFlags mute = MuteFlags.Default)
    {
        Load();
        var key = Key(id, name);
        _entries.TryGetValue(key, out var old);
        var flags = mute == MuteFlags.Default ? MuteFlags.Default : (old?.Flags ?? MuteFlags.All) & ~mute;
        var e = new Entry(id, name.Length > 0 ? name : old?.Name ?? "", type, flags);
        _entries[key] = e;
        Save();
        try { c?.Self.UpdateMuteListEntry(TypeOf(type), id, e.Name, flags); } catch (Exception x) { _s.Log.LogDebug(x, "mute on the grid"); }
        Changed?.Invoke();
        return e;
    }

    /// <summary>Unmutes what's named (Default: the whole entry), as LLMuteList::remove: an entry left letting everything
    /// through goes; one still muting something is kept with its new flags.</summary>
    public Entry? Remove(UUID id, string? name, GridClient? c, MuteFlags unmute = MuteFlags.Default)
    {
        Load();
        string? key = id != UUID.Zero && _entries.ContainsKey(id.ToString()) ? id.ToString() : null;
        if (key is null && !string.IsNullOrEmpty(name))
            key = _entries.FirstOrDefault(kv => string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase)).Key;
        if (key is null || !_entries.TryGetValue(key, out var was)) return null;
        var flags = unmute == MuteFlags.Default ? MuteFlags.All : was.Flags | unmute;
        if (flags == MuteFlags.All)
        {
            _entries.TryRemove(key, out _);
            Save();
            try { c?.Self.RemoveMuteListEntry(was.Id, was.Name); } catch (Exception x) { _s.Log.LogDebug(x, "unmute on the grid"); }
        }
        else
        {
            _entries[key] = was with { Flags = flags };
            Save();
            try { c?.Self.UpdateMuteListEntry(TypeOf(was.Type), was.Id, was.Name, flags); } catch (Exception x) { _s.Log.LogDebug(x, "mute on the grid"); }
        }
        Changed?.Invoke();
        return was;
    }

    /// <summary>Entries the grid knows (another client's blocks) join the local list; for one both have, the grid's
    /// flags win (a voice mute set in another viewer).</summary>
    public void Merge(GridClient c)
    {
        Load();
        var changed = false;
        foreach (var m in c.Self.MuteList.Values)
        {
            var type = m.Type switch { MuteType.Object => "object", MuteType.ByName => "name", MuteType.Group => "group", _ => "resident" };
            var flags = m.Flags & MuteFlags.All;
            var key = Key(m.ID, m.Name);
            if (_entries.TryGetValue(key, out var have))
            {
                if (have.Flags != flags) { _entries[key] = have with { Flags = flags }; changed = true; }
            }
            else if (_entries.TryAdd(key, new Entry(m.ID, m.Name, type, flags))) changed = true;
        }
        if (changed) { Save(); Changed?.Invoke(); }
    }
}
