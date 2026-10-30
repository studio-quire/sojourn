using System.Text.Json.Nodes;
using LibreMetaverse;

namespace Sojourn.Core.Agents;

/// <summary>
/// Bumps, pushes and hits (the region's MeanCollisionAlert), as the viewer's Bumps, Pushes &amp; Hits floater lists them
/// (llfloaterbump.cpp) and Firestorm can put them in nearby chat (FSCollisionMessagesInChat, its strings): kept for the
/// session, and on the feed as a quiet notice per kind of the bump_notices setting (pushes and hits by default; bumps
/// are anyone walking into you), at most one per person a half minute with how many came since. Second Life sends these;
/// OpenSim doesn't. And the sounds heard lately, for blocking one by asking (Firestorm's Sound Explorer).
/// </summary>
public sealed partial class Safety
{
    public enum BumpNotices { Off, Pushes, All }

    /// <summary>The setting (bump_notices).</summary>
    public BumpNotices Notices { get; set; } = BumpNotices.Pushes;

    public sealed record Bump(UUID Who, string Name, MeanCollisionType Type, float Magnitude, double At, int Spell);

    readonly List<Bump> _bumps = new();
    readonly Dictionary<UUID, (double At, int Since)> _bumpShown = new();

    public static string BumpText(MeanCollisionType t, string name) => t switch
    {
        MeanCollisionType.Bump => $"{name} bumped you.",
        MeanCollisionType.LLPushObject => $"{name} pushed you with a script.",
        MeanCollisionType.SelectedObjectCollide => $"{name} hit you with an object.",
        MeanCollisionType.ScriptedObjectCollide => $"{name} hit you with a scripted object.",
        MeanCollisionType.PhysicalObjectCollide => $"{name} hit you with a physical object.",
        _ => $"{name} caused a collision of unknown type.",
    };

    void OnBump(GridClient c, MeanCollisionEventArgs e)
    {
        if (e.Aggressor == UUID.Zero || e.Aggressor == c.Self.AgentID) return;
        string name = _s.Names.DisplayName(e.Aggressor) ?? "Someone";
        double now = Json.UnixNow();
        var b = new Bump(e.Aggressor, name, e.Type, e.Magnitude, now, _s.Private.Spell);
        lock (_bumps)
        {
            _bumps.Add(b);
            if (_bumps.Count > 200) _bumps.RemoveAt(0);
        }
        _s.Events.Log("TesselEvents", new JsonObject
        {
            ["event"] = "collision", ["who"] = e.Aggressor.ToString(), ["type"] = e.Type.ToString(), ["magnitude"] = Math.Round(e.Magnitude, 2),
        });
        if (Notices == BumpNotices.Off || (Notices == BumpNotices.Pushes && e.Type == MeanCollisionType.Bump)) return;
        if (_s.Mutes.IsMuted(e.Aggressor)) return;
        int more;
        lock (_bumpShown)
        {
            _bumpShown.TryGetValue(e.Aggressor, out var shown);
            if (now - shown.At < 30) { _bumpShown[e.Aggressor] = (shown.At, shown.Since + 1); return; }
            more = shown.Since;
            _bumpShown[e.Aggressor] = (now, 0);
        }
        var text = BumpText(e.Type, $"<nolink>{name}</nolink>") + (more > 0 ? $" ({more} more since)" : "");
        _s.Notify.Add("CollisionAlert", "notifytip", text, Notifications.None,
            payload: new JsonObject { ["from_id"] = e.Aggressor.ToString(), ["type"] = e.Type.ToString() },
            substitutions: new JsonObject { ["NAME"] = name });
    }

    /// <summary>This session's bumps, pushes and hits, newest last (those callers may be shown).</summary>
    public IReadOnlyList<Bump> Bumps
    {
        get { lock (_bumps) return _bumps.Where(b => _s.Private.ShownSpell(b.Spell)).ToList(); }
    }

    // --- the sounds heard lately -----------------------------------------------------------------------------------

    public sealed record Heard(UUID Sound, UUID Object, UUID Owner, string How, ulong Region, Vector3 Where, double At, int Times, int Spell);

    readonly Dictionary<(UUID Sound, UUID Object), Heard> _heard = new();

    /// <summary>A sound that played (world audio, on its thread): how: worn, rezzed, gesture or collision.</summary>
    public void SoundHeard(UUID sound, UUID obj, UUID owner, string how, ulong region, Vector3 where)
    {
        double now = Json.UnixNow();
        int spell = _s.Private.Spell;
        lock (_heard)
        {
            _heard.TryGetValue((sound, obj), out var had);
            _heard[(sound, obj)] = new Heard(sound, obj, owner, how, region, where, now, (had?.Times ?? 0) + 1, spell);
            if (_heard.Count > 300)
                foreach (var k in _heard.OrderBy(kv => kv.Value.At).Take(50).Select(kv => kv.Key).ToList()) _heard.Remove(k);
        }
    }

    /// <summary>Sounds heard in the last so many seconds, newest first.</summary>
    public IReadOnlyList<Heard> HeardSince(double seconds)
    {
        double since = Json.UnixNow() - seconds;
        lock (_heard) return _heard.Values.Where(h => h.At >= since && _s.Private.ShownSpell(h.Spell)).OrderByDescending(h => h.At).ToList();
    }

    /// <summary>A spell of incognito ended: what it recorded goes.</summary>
    void ForgetSpellRecords()
    {
        lock (_bumps) _bumps.RemoveAll(b => b.Spell != 0);
        lock (_heard)
            foreach (var k in _heard.Where(kv => kv.Value.Spell != 0).Select(kv => kv.Key).ToList()) _heard.Remove(k);
        foreach (var (k, c) in _spam) if (c.Spell != 0) lock (c) c.Name = "";
    }
}
