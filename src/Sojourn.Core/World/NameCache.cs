using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;

namespace Sojourn.Core.World;

/// <summary>
/// Avatar names for one grid, shared by every agent on it: legacy names from chat, IMs, object updates and
/// UUIDNameReply; display names from the display-name service where the grid has one. What's shown or said for someone
/// (<see cref="DisplayName"/>, <see cref="Complete"/>) follows <see cref="Style"/>; matching what a person typed against
/// someone takes every form of their name (<see cref="Forms"/>).
/// </summary>
public sealed class NameCache
{
    sealed record Entry(string Legacy, string? Display, bool IsDefaultDisplay);

    /// <summary>How this app words people's names: Sojourn Access sets it from its People's names (Alt+0), so a display
    /// name in decorative Unicode is said in plain letters (PlainName); the VR client keeps them as written. One app
    /// per process, so it's the process's.</summary>
    public static NameStyle Style { get; set; } = NameStyle.Written;

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

    /// <summary>A display name set or taken away (null) by hand: the scripted test's stand-in for the display-name service
    /// the local grid hasn't got (Sojourn Access's SojournTestNames, only with SOJOURN_TEST_NAMES=1).</summary>
    public void SetDisplay(UUID id, string? display)
    {
        if (!_names.TryGetValue(id, out var was)) return;
        var now = _names[id] = was with { Display = string.IsNullOrEmpty(display) ? null : display, IsDefaultDisplay = string.IsNullOrEmpty(display) };
        if (was.Display != now.Display) Learned?.Invoke(id, now.Legacy, now.Display);
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

    /// <summary>The name shown or said for someone, as <see cref="Style"/> words it: their display name (as written, or in
    /// plain letters, with the username after it, or the username alone); null if unknown.</summary>
    public string? DisplayName(UUID id) =>
        _names.TryGetValue(id, out var e) ? PlainName.Said(e.Display, UserName(e.Legacy), Style) : null;

    /// <summary>The display name exactly as written (the username when they have none), whatever the style: for matching
    /// text that holds it.</summary>
    public string? Written(UUID id) =>
        _names.TryGetValue(id, out var e) ? !string.IsNullOrEmpty(e.Display) ? e.Display : UserName(e.Legacy) : null;

    /// <summary>A display name of their own choosing as written, or null (none, or the default one, their username).</summary>
    public string? ChosenDisplay(UUID id) =>
        _names.TryGetValue(id, out var e) && !string.IsNullOrEmpty(e.Display) && !e.IsDefaultDisplay ? e.Display : null;

    /// <summary>Every form of someone's name a person might type for them, for matching: the display name as written and
    /// in plain letters, the name as said, the username, the legacy name.</summary>
    public IEnumerable<string> Forms(UUID id)
    {
        if (!_names.TryGetValue(id, out var e)) return Array.Empty<string>();
        var user = UserName(e.Legacy);
        return new[] { e.Display, PlainName.Of(e.Display), PlainName.Said(e.Display, user, Style), user, e.Legacy }
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A display name met as text (a change of name's notice) as the style says names: in plain letters unless
    /// the style keeps them as written (as written too when it has no plain letters at all).</summary>
    public static string Readable(string display) =>
        Style == NameStyle.Written ? display : PlainName.Of(display) is { Length: > 0 } p ? p : display;

    /// <summary>{display_name, username}, or null if unknown; with said, the name as the app says it, where the style
    /// words it otherwise (the assistant in Sojourn Access uses it, so its replies name people as the person hears them).</summary>
    public JsonObject? Json(UUID id)
    {
        if (!_names.TryGetValue(id, out var e)) return null;
        var user = UserName(e.Legacy);
        var display = string.IsNullOrEmpty(e.Display) ? user : e.Display;
        var o = new JsonObject { ["display_name"] = display, ["username"] = user };
        if (PlainName.Said(e.Display, user, Style) is var said && said != display) o["said"] = said;
        return o;
    }

    /// <summary>The viewer's "complete name": "Display (user)" when a display name differs, else the user name; the
    /// display name worded by <see cref="Style"/>.</summary>
    public string Complete(UUID id, string fallback) =>
        _names.TryGetValue(id, out var e) ? PlainName.Complete(e.Display, UserName(e.Legacy), e.IsDefaultDisplay, Style) : fallback;

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
