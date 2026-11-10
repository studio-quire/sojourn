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

    // EffectType's numbers (llhudobject.h) and the select and clear kinds of EPointAtType and ELookAtType
    const byte BeamFx = 7, EditFx = 13, LookAtFx = 14, PointAtFx = 15;
    const byte PointSelect = 1, PointGrab = 2, PointClear = 3, LookSelect = 7, LookClear = 10;
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
                case PointAtFx when data.Length == 57:
                    if (data[56] is PointSelect or PointGrab && target != UUID.Zero) Point(who, target, "select", ByPointAt);
                    else LetGo(who, ByPointAt);
                    break;
                // sent with the PointAt by the official viewer; alone by one that keeps its PointAt private
                case LookAtFx when data.Length == 57:
                    if (data[56] == LookSelect && target != UUID.Zero) Point(who, target, "select", ByLookAt);
                    else LetGo(who, ByLookAt);
                    break;
                case BeamFx or EditFx when data.Length == 56 && target != UUID.Zero:
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

    // --- the person's own pointing ----------------------------------------------------------------------------------
    //
    // Sojourn Access's Point at (docs/access-app.md > Pointing at something yourself; Radegast's Point At): the person's
    // avatar points its arm at a thing, so a sighted friend nearby sees which one they mean. Only Access's own keys start
    // it, on the person's own press, so it's on no API: the assistant can't point their arm (the VR client's call against
    // sending these, docs/viewer-gaps.md, was about the arm moving for what the assistant did). What's sent is what the
    // official viewer sends while its person has a thing selected, a PointAt and a LookAt of type select at the very prim,
    // so it reads in every viewer as a sighted person's selection, plus the edit beam it sends about once a second while
    // building (LLSelectMgr's selection beam, in its default effect colour), which is what makes it stand out; Radegast
    // sends beams too. It ends by itself after its time, when the person moves a few metres from where they stood (a
    // teleport too), or when the thing goes: Radegast's guide calls a point left on rude.

    public enum Ended { Asked, Time, Moved, Gone }

    /// <summary>The thing the person points at now, zero when nothing.</summary>
    public UUID Mine { get { lock (_lock) return _mine; } }

    /// <summary>The person's pointing ended by itself (the timer's thread): its time up, they moved away, the thing went.</summary>
    public event Action<UUID, Ended>? MineEnded;

    UUID _mine;
    DateTime _mineUntil;
    Vector3d _mineFrom;
    int _mineTicks;
    Timer? _mineTimer;
    readonly UUID _pointId = UUID.Random(), _lookId = UUID.Random(), _beamId = UUID.Random();
    const double MovedAway = 3;   // metres from where they stood

    /// <summary>Point at a thing in the world (a prim, root or part) for this long, sent at once; again on the same thing,
    /// for this long from now. False when not in a region.</summary>
    public bool PointAt(UUID target, TimeSpan lasting)
    {
        if (_s.Client is not { Network.Connected: true } c || c.Network.CurrentSim is null) return false;
        lock (_lock)
        {
            _mine = target;
            _mineUntil = DateTime.UtcNow + lasting;
            _mineFrom = c.Self.GlobalPosition;
            _mineTicks = 0;
            _mineTimer ??= new Timer(_ => _s.Guard("pointing", Tick));
            _mineTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
        return true;
    }

    /// <summary>The person's pointing put down; false when they weren't pointing.</summary>
    public bool StopPointing() => End(Ended.Asked, null) != UUID.Zero;

    /// <summary>Each second while pointing: the beam again (the PointAt and LookAt every fifth, for anyone who missed them),
    /// or the end. Sent under the lock, so a tick can't point again after a stop has let go.</summary>
    void Tick()
    {
        UUID target;
        Ended why;
        lock (_lock)
        {
            target = _mine;
            if (target == UUID.Zero) return;
            if (_s.Client is not { Network.Connected: true } c || c.Network.CurrentSim is null)
            {
                // logged out or dropped: nothing to let go of, nothing to say
                _mine = UUID.Zero;
                _mineTimer?.Dispose();
                _mineTimer = null;
                return;
            }
            Ended? ended = DateTime.UtcNow >= _mineUntil ? Ended.Time
                : Vector3d.Distance(c.Self.GlobalPosition, _mineFrom) > MovedAway ? Ended.Moved
                : !_s.VisibleRegions().Any(w => w.PrimById(target) != null) ? Ended.Gone
                : null;
            if (ended is { } e) why = e;
            else
            {
                if (_mineTicks++ % 5 == 0) Send(c, target, PointSelect, LookSelect, float.MaxValue / 4);
                // re-sent under one id, it lives on in the viewers as one beam (LLHUDEffectSpiral::triggerLocal)
                c.Self.BeamEffect(c.Self.AgentID, target, Vector3d.Zero, Color4.White, 1.5f, _beamId);
                return;
            }
        }
        End(why, target);
    }

    /// <summary>Stops the timer and lets go in the viewers (the PointAt and LookAt cleared, the beam's life ended), as the
    /// official viewer does when a selection ends; said (MineEnded) unless asked for. Only that target when one is given (a
    /// new pointing begun meanwhile stands). Returns what was pointed at.</summary>
    UUID End(Ended why, UUID? only)
    {
        UUID was;
        lock (_lock)
        {
            was = _mine;
            if (was == UUID.Zero || only is { } o && o != was) return UUID.Zero;
            _mine = UUID.Zero;
            _mineTimer?.Dispose();
            _mineTimer = null;
            if (_s.Client is { Network.Connected: true } c) Send(c, UUID.Zero, PointClear, LookClear, 0, endBeam: was);
        }
        if (why != Ended.Asked) MineEnded?.Invoke(was, why);
        return was;
    }

    /// <summary>The PointAt and LookAt in one packet, as the official viewer's LLHUDManager::sendEffects packs what changed
    /// in a frame: a region may hold back a packet of only LookAts and beams from people whose camera is far off (OpenSim
    /// does past 10 m, Scene.ShouldSendDiscardableEffect), never one with a PointAt. Ending, the beam's life too.</summary>
    void Send(GridClient c, UUID target, byte point, byte look, float duration, UUID? endBeam = null)
    {
        var self = c.Self.AgentID;
        byte[] Data(byte kind, int length)
        {
            var d = new byte[length];
            Buffer.BlockCopy(self.GetBytes(), 0, d, 0, 16);
            if (target != UUID.Zero) Buffer.BlockCopy(target.GetBytes(), 0, d, 16, 16);
            if (length == 57) d[56] = kind;   // (the offset between, 24 bytes of a position, stays zero: the prim's middle)
            return d;
        }
        ViewerEffectPacket.EffectBlock Block(UUID id, byte type, float secs, byte[] data, byte[]? color = null) =>
            new() { AgentID = self, Color = color ?? new byte[4], Duration = secs, ID = id, Type = type, TypeData = data };
        var blocks = new List<ViewerEffectPacket.EffectBlock> { Block(_pointId, PointAtFx, duration, Data(point, 57)), Block(_lookId, LookAtFx, duration, Data(look, 57)) };
        if (endBeam is { } beam)
        {
            var d = new byte[56];
            Buffer.BlockCopy(self.GetBytes(), 0, d, 0, 16);
            Buffer.BlockCopy(beam.GetBytes(), 0, d, 16, 16);
            blocks.Add(Block(_beamId, BeamFx, 0, d, Color4.White.GetBytes()));
        }
        c.Network.SendPacket(new ViewerEffectPacket
        {
            AgentData = { AgentID = self, SessionID = c.Self.SessionID },
            Effect = blocks.ToArray(),
        });
    }
}
