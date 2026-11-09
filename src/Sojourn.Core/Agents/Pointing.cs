using LibreMetaverse;
using LibreMetaverse.Packets;

namespace Sojourn.Core.Agents;

/// <summary>
/// What other people point at, from the viewer effects their viewers send. The official viewer points its avatar's arm at
/// whatever its person selects in the world (a right click's menu, or the edit tool): a PointAt and a LookAt, both of type
/// select, at the very prim clicked, part or root, cleared when the selection ends (LLSelectMgr::updatePointAt; never at
/// avatars, attachments or what they sit on, LLAgentCamera::setPointAt). Radegast's Point at sends the PointAt too, and
/// building sends beams at what's built on. Radegast's Master Controls follow one person's pointing so a sighted friend
/// can pick out a thing for a blind resident; Sojourn Access does the same (docs/access-app.md > What a friend points
/// at). Each person's last pointing is kept after it clears, since their menu closes long before anyone has caught up
/// with it. Claude Opus 5.5, 2026-11-08.
/// </summary>
public sealed class Pointing
{
    readonly AgentSession _s;

    public Pointing(AgentSession s) => _s = s;

    /// <param name="How">"select" (a PointAt or LookAt of type select or grab) or "beam" (a building beam).</param>
    public readonly record struct Pointed(UUID Who, UUID Target, string How, DateTime At);

    readonly object _lock = new();
    readonly Dictionary<UUID, Pointed> _last = new();
    /// <summary>Whose selection stands now, by what says so (1 their PointAt, 2 their LookAt): their beams meanwhile are
    /// building on what's selected, not pointing anew, and the two effects of one selection are one pointing.</summary>
    readonly Dictionary<UUID, int> _selecting = new();

    /// <summary>Someone pointed at something new, or again at the same thing after letting go of it (the network's thread).</summary>
    public event Action<Pointed>? Changed;

    /// <summary>The last thing someone pointed at, if they have since this session began.</summary>
    public Pointed? Last(UUID who)
    {
        lock (_lock) return _last.TryGetValue(who, out var p) ? p : null;
    }

    // EffectType's numbers (llhudobject.h) and the select kinds of EPointAtType and ELookAtType
    const byte Beam = 7, Edit = 13, LookAt = 14, PointAt = 15;
    const byte PointSelect = 1, PointGrab = 2, LookSelect = 7;
    const int ByPointAt = 1, ByLookAt = 2;

    public void Wire(GridClient c) =>
        c.Network.RegisterCallback(PacketType.ViewerEffect, (_, e) => _s.Guard("pointing", () => On(c, (ViewerEffectPacket)e.Packet)), false);

    void On(GridClient c, ViewerEffectPacket p)
    {
        var self = c.Self.AgentID;
        foreach (var b in p.Effect)
        {
            var data = b.TypeData;
            if (data is not { Length: >= 56 }) continue;
            var who = new UUID(data, 0);
            if (who == self || who == UUID.Zero) continue;
            // the block says whose viewer sent it; a source in the data that isn't theirs is someone speaking for another
            if (b.AgentID != UUID.Zero && b.AgentID != who) continue;
            var target = new UUID(data, 16);
            switch (b.Type)
            {
                case PointAt when data.Length == 57:
                    if (data[56] is PointSelect or PointGrab && target != UUID.Zero) Point(who, target, "select", ByPointAt);
                    else LetGo(who, ByPointAt);
                    break;
                // sent with the PointAt by the official viewer; alone by one that keeps its PointAt private
                case LookAt when data.Length == 57:
                    if (data[56] == LookSelect && target != UUID.Zero) Point(who, target, "select", ByLookAt);
                    else LetGo(who, ByLookAt);
                    break;
                case Beam or Edit when data.Length == 56 && target != UUID.Zero:
                    Point(who, target, "beam", 0);
                    break;
            }
        }
    }

    void LetGo(UUID who, int by)
    {
        lock (_lock)
            if (_selecting.TryGetValue(who, out var had) && (had & ~by) is var left)
            {
                if (left == 0) _selecting.Remove(who);
                else _selecting[who] = left;
            }
    }

    void Point(UUID who, UUID target, string how, int by)
    {
        if (!InWorld(target)) return;
        var now = DateTime.UtcNow;
        Pointed p;
        lock (_lock)
        {
            int had = _selecting.TryGetValue(who, out var bits) ? bits : 0;
            if (by == 0 && had != 0) return;   // a beam at what's selected
            if (by != 0) _selecting[who] = had | by;
            // the PointAt and LookAt of one selection, a beam re-sent: the same pointing still
            bool same = _last.TryGetValue(who, out var was) && was.Target == target && (had != 0 || now - was.At < TimeSpan.FromSeconds(5));
            p = new Pointed(who, target, how, now);
            _last[who] = p;
            if (same) return;
        }
        Changed?.Invoke(p);
    }

    /// <summary>A thing in the world, as the viewer points only at those: not an avatar or anything worn (a beam at
    /// someone's own hair while they dress isn't pointing anything out). One not seen yet counts.</summary>
    bool InWorld(UUID target)
    {
        foreach (var w in _s.VisibleRegions())
        {
            if (w.AvatarById(target) != null) return false;
            if (w.PrimById(target) is { } prim) return !_s.Objects.IsAttachment(w, prim, out _, out _);
        }
        return true;
    }
}
