using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;

namespace Sojourn.Core.Agents;

/// <summary>
/// Antispam, after Firestorm's NACLantispam.cpp: each source (an object, or a resident and everything of theirs) may
/// send so many of a kind in a window; past that it's blocked on that kind, silently, until the person leaves the
/// region (NACLAntiSpamRegistry::purgeAllQueues on a region change) or lifts it, and the feed says so once. Firestorm's
/// numbers: 10 in 4 seconds, sounds and their preloads 6 times as many, a message of more than 70 lines blocks its
/// sender's messages at once. Its kinds: nearby chat (an object by its id, a resident by theirs), IMs and everything
/// that travels as one (offers, teleport offers and requests, friendship offers, objects' IMs: by the sending
/// resident), script menus and questions (by the object; here by its owner too, so a linkset of prims each sending one
/// counts together), sounds and preloads (by object and by owner). Off by default, as in Firestorm (UseAntiSpam; the
/// user, 2026-10-27): the antispam setting turns it on. The person's own objects never count (FSUseAntiSpamMine, off).
/// </summary>
public sealed partial class Safety
{
    public enum Queue { Chat, Im, Dialog, Sound, SoundPreload }

    /// <summary>The setting (antispam): off by default.</summary>
    public bool SpamOn { get; set; }

    const int SpamAmount = 10;
    const double SpamSeconds = 4;
    const int SoundMultiplier = 6;   // _NACL_AntiSpamSoundMulti, _NACL_AntiSpamSoundPreloadMulti
    const int MaxNewlines = 70;      // _NACL_AntiSpamNewlines

    sealed class Count
    {
        public int N;
        public double Start, BlockedAt;
        public bool Blocked;
        public string Name = "";
        public int Limit, Spell;
    }

    readonly ConcurrentDictionary<(Queue, UUID), Count> _spam = new();

    static string What(Queue q) => q switch
    {
        Queue.Chat => "chat lines", Queue.Im => "messages and offers", Queue.Dialog => "menus and questions",
        Queue.Sound => "sounds", _ => "sound preloads",
    };

    /// <summary>One more of a kind from a source (a network thread): true when it's to be dropped (over the limit now, or
    /// blocked already). mine: the person's own object (never counted).</summary>
    public bool Spam(Queue q, UUID source, string? name, bool mine = false)
    {
        if (!SpamOn || mine || source == UUID.Zero || source == _s.AgentId) return false;
        int limit = SpamAmount * (q is Queue.Sound or Queue.SoundPreload ? SoundMultiplier : 1);
        double now = Json.UnixNow();
        var c = _spam.GetOrAdd((q, source), _ => new Count { Start = now });
        lock (c)
        {
            if (c.Blocked) return true;
            if (now - c.Start > SpamSeconds) { c.Start = now; c.N = 0; }
            if (++c.N <= limit) return false;
            Block(c, name, limit, now);
        }
        Notice($"Blocked {Who(name)} for sending more than {limit} {What(q)} in {SpamSeconds:0} seconds, until you leave the region.",
            q, source, name, limit);
        return true;
    }

    /// <summary>A message (chat or IM) of more than 70 lines blocks its sender's messages; one already blocked on them is
    /// dropped too (NACLAntiSpamRegistry::checkNewlineFlood, which goes by the IM block whichever kind it checks).</summary>
    public bool NewlineFlood(UUID source, string? name, string? message, bool mine = false)
    {
        if (!SpamOn || mine || source == UUID.Zero || source == _s.AgentId) return false;
        var c = _spam.GetOrAdd((Queue.Im, source), _ => new Count { Start = Json.UnixNow() });
        lock (c)
        {
            if (c.Blocked) return true;
            if (message is null || message.Count(ch => ch == '\n') <= MaxNewlines) return false;
            Block(c, name, MaxNewlines, Json.UnixNow());
        }
        Notice($"Blocked {Who(name)} for a message of more than {MaxNewlines} lines, until you leave the region.", Queue.Im, source, name, MaxNewlines);
        return true;
    }

    void Block(Count c, string? name, int limit, double now)
    {
        c.Blocked = true;
        c.BlockedAt = now;
        c.Name = name ?? "";
        c.Limit = limit;
        c.Spell = _s.Private.Spell;
    }

    static string Who(string? name) => string.IsNullOrWhiteSpace(name) ? "something" : $"<nolink>{name}</nolink>";

    void Notice(string text, Queue q, UUID source, string? name, int limit) =>
        _s.Notify.Add("AntiSpamBlocked", "notifytip", text, Notifications.None,
            payload: new JsonObject { ["source"] = source.ToString(), ["queue"] = q.ToString().ToLowerInvariant() },
            substitutions: new JsonObject
            {
                ["SOURCE"] = name ?? "", ["QUEUE"] = What(q), ["COUNT"] = limit, ["PERIOD"] = SpamSeconds,
            });

    /// <summary>What's blocked now (that callers may be shown).</summary>
    public IReadOnlyList<(Queue Queue, UUID Source, string Name, int Limit, double At)> SpamBlocks =>
        _spam.Where(kv => kv.Value.Blocked && _s.Private.ShownSpell(kv.Value.Spell))
            .Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.Name, kv.Value.Limit, kv.Value.BlockedAt))
            .OrderBy(x => x.BlockedAt).ToList();

    /// <summary>Lift the blocks on a source (every kind); the number lifted.</summary>
    public int Unspam(UUID source)
    {
        int n = 0;
        foreach (var k in _spam.Keys.Where(k => k.Item2 == source).ToList())
            if (_spam.TryRemove(k, out var c) && c.Blocked) n++;
        return n;
    }

    /// <summary>Every block lifted and every count started afresh (a region change does it, as Firestorm's does).</summary>
    public int ClearSpamBlocks(bool regionChange = false)
    {
        int n = _spam.Values.Count(c => c.Blocked);
        _spam.Clear();
        return n;
    }
}
