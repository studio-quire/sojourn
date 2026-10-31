using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;

namespace Sojourn.Core.World;

/// <summary>
/// Avatar names for one grid, shared by every agent on it: legacy names from chat, IMs, object updates and
/// UUIDNameReply; display names from the display-name service where the grid has one.
/// </summary>
public sealed class NameCache
{
    sealed record Entry(string Legacy, string? Display, bool IsDefaultDisplay);

    readonly ConcurrentDictionary<UUID, Entry> _names = new();

    /// <summary>A name learned or changed: the id, the legacy name and the display name (null if none yet). The session
    /// log keeps them, so a log sent to the studio can put stand-ins for every name in it (Sojourn.Session/LogShare.cs).</summary>
    public event Action<UUID, string, string?>? Learned;

    public void SetLegacy(UUID id, string? fullName)
    {
        if (id == UUID.Zero || string.IsNullOrWhiteSpace(fullName)) return;
        fullName = fullName.Trim();
        _names.TryGetValue(id, out var was);
        var now = _names.AddOrUpdate(id, _ => new Entry(fullName, null, true), (_, e) => e with { Legacy = fullName });
        if (was is null || was.Legacy != fullName) Learned?.Invoke(id, now.Legacy, now.Display);
    }

    public void SetDisplay(AgentDisplayName d)
    {
        var legacy = $"{d.LegacyFirstName} {d.LegacyLastName}".Trim();
        _names.TryGetValue(d.ID, out var was);
        var now = _names[d.ID] = new Entry(legacy.Length > 0 ? legacy : d.UserName ?? "", d.DisplayName, d.IsDefaultDisplayName);
        if (was is null || was.Legacy != now.Legacy || was.Display != now.Display) Learned?.Invoke(d.ID, now.Legacy, now.Display);
    }

    /// <summary>Every name known now: id, legacy name, display name (null if none yet).</summary>
    public List<(UUID Id, string Legacy, string? Display)> All() => _names.Select(kv => (kv.Key, kv.Value.Legacy, kv.Value.Display)).ToList();

    public bool Has(UUID id) => _names.ContainsKey(id);

    public string? Legacy(UUID id) => _names.TryGetValue(id, out var e) ? e.Legacy : null;

    /// <summary>The viewer's LLAvatarName::getUserName: "First Last", or just "First" when the last name is Resident.</summary>
    public static string UserName(string legacy)
    {
        var parts = legacy.Split(' ', 2);
        return parts.Length == 2 && parts[1].Equals("Resident", StringComparison.OrdinalIgnoreCase) ? parts[0] : legacy;
    }

    public string? DisplayName(UUID id)
    {
        if (!_names.TryGetValue(id, out var e)) return null;
        return !string.IsNullOrEmpty(e.Display) ? e.Display : UserName(e.Legacy);
    }

    /// <summary>{display_name, username}, or null if unknown.</summary>
    public JsonObject? Json(UUID id)
    {
        if (!_names.TryGetValue(id, out var e)) return null;
        var user = UserName(e.Legacy);
        return new JsonObject { ["display_name"] = string.IsNullOrEmpty(e.Display) ? user : e.Display, ["username"] = user };
    }

    /// <summary>The viewer's "complete name": "Display (user)" when a display name differs, else the user name.</summary>
    public string Complete(UUID id, string fallback)
    {
        if (!_names.TryGetValue(id, out var e)) return fallback;
        var user = UserName(e.Legacy);
        return !string.IsNullOrEmpty(e.Display) && !e.IsDefaultDisplay && e.Display != user ? $"{e.Display} ({user})" : user;
    }

    /// <summary>Looks up unknown names through a client and waits up to <paramref name="timeout"/>.</summary>
    public async Task ResolveAsync(GridClient c, IEnumerable<UUID> ids, TimeSpan timeout, CancellationToken ct = default)
    {
        var missing = ids.Where(i => i != UUID.Zero && !_names.ContainsKey(i)).Distinct().ToList();
        if (missing.Count == 0) return;
        try
        {
            if (c.Avatars.DisplayNamesAvailable())
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                var (ok, names, _) = await c.Avatars.GetDisplayNamesAsync(missing, cts.Token);
                if (ok && names is not null) foreach (var n in names) SetDisplay(n);
            }
        }
        catch { /* fall back to legacy names */ }
        missing = missing.Where(i => !_names.ContainsKey(i)).ToList();
        if (missing.Count == 0) return;
        c.Avatars.RequestAvatarNames(missing);
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end && missing.Any(i => !_names.ContainsKey(i)) && !ct.IsCancellationRequested)
            await Task.Delay(100, ct);
    }
}
