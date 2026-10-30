using LibreMetaverse;
using LibreMetaverse.Packets;
using Microsoft.Extensions.Logging;
using Sojourn.Core.World;

namespace Sojourn.Core.Agents;

/// <summary>
/// When I stand up from something (by any means: the stick, the forearm key, the assistant, the region unseating me, a
/// teleport away), what that seat's scripts left playing on me is stopped after a short grace, and only that: the
/// animations started by the seat's linkset, not the region's own (standing, walking), not worn things' (an AO), not
/// anyone else's. That object's animation permissions are revoked too, as the viewer's "Stop animating me" does with
/// RevokePermsOnStopAnimation, so its script can't start the pose again; but only that object's. Other viewers leave
/// this to the person; the user decided Sojourn does it by itself (2026-10-28, after a Mirromere bench's sit pose
/// stayed on them after standing). Written by Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class SeatLeftovers
{
    /// <summary>Seats that stop their own animations when you stand do it within this.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(1.5);
    /// <summary>Left while the region changed and the old one is still a neighbour: likely a vehicle crossing a border
    /// with me on it, which can take a while under lag; waited this long for it to seat me again.</summary>
    public static readonly TimeSpan CrossingWait = TimeSpan.FromSeconds(10);

    readonly AgentSession _s;
    double _acc;
    Seat? _on;
    readonly List<(Seat Seat, DateTime At)> _left = new();

    public SeatLeftovers(AgentSession s) => _s = s;

    /// <summary>A seat as a linkset (its root's id), where it was, and the prims of it seen animating me while I sat.</summary>
    public sealed class Seat(UUID root, ulong region, string name)
    {
        public UUID Root { get; } = root;
        public ulong Region { get; set; } = region;
        public string Name { get; set; } = name;
        public HashSet<UUID> Sources { get; } = new();
    }

    /// <summary>Of what plays now, the ids started by the seat (a prim of it seen animating me, or one whose linkset's
    /// root is the seat's). Never the region's own, nor mine.</summary>
    public static List<UUID> Leftovers(IEnumerable<SelfAnimations.Playing> playing, Seat seat, UUID me, Func<UUID, UUID?> rootOf) =>
        playing.Where(p => p.Source != UUID.Zero && p.Source != me
                           && (seat.Sources.Contains(p.Source) || rootOf(p.Source) == seat.Root))
            .Select(p => p.Id).Distinct().ToList();

    /// <summary>What a left seat comes to now: still waiting, dropped (sat on it again), or due.</summary>
    public enum Verdict { Wait, Drop, Due }

    public static Verdict Judge(Seat left, TimeSpan since, UUID? onNow, ulong regionNow, bool oldRegionStillNear)
    {
        if (onNow == left.Root) return Verdict.Drop;
        if (since < Grace) return Verdict.Wait;
        if (left.Region != regionNow && oldRegionStillNear && since < CrossingWait) return Verdict.Wait;
        return Verdict.Due;
    }

    /// <summary>On the session's clock (10 Hz); looks four times a second.</summary>
    public void Tick(GridClient c, double dt)
    {
        _acc += dt;
        if (_acc < 0.25) return;
        _acc = 0;
        if (_s.Region is not { } w) return;

        // what I sit on, as the current region has my avatar (per region, unlike Self.SittingOn, which a teleport leaves stale)
        uint seatLocal = w.AvatarById(c.Self.AgentID)?.ParentID ?? 0;
        bool unresolved = false;
        Seat? now = null;
        if (seatLocal != 0)
        {
            if (w.Prims.TryGetValue(seatLocal, out var prim))
            {
                var root = _s.Objects.RootOf(w, prim);
                string name = root.Properties?.Name ?? prim.Properties?.Name ?? "";
                if (_on is { } same && same.Root == root.ID)
                {
                    now = same;
                    now.Region = w.Handle;   // a vehicle carrying me over a border
                    if (now.Name.Length == 0) now.Name = name;
                }
                else now = new Seat(root.ID, w.Handle, name);
            }
            else unresolved = true;   // its prim hasn't arrived yet: judge when it has
        }
        var t = DateTime.UtcNow;
        if (!unresolved)
        {
            if (_on is { } was && now?.Root != was.Root) _left.Add((was, t));
            _on = now;
        }
        if (_on is { } seat) Learn(c, seat);
        if (_left.Count == 0) return;

        for (int i = _left.Count - 1; i >= 0; i--)
        {
            var (left, at) = _left[i];
            bool near = left.Region != w.Handle && _s.VisibleRegions().Any(r => r.Handle == left.Region);
            switch (Judge(left, t - at, unresolved ? null : _on?.Root, w.Handle, near))
            {
                case Verdict.Wait: continue;
                case Verdict.Due: StopLeftovers(c, left); break;
            }
            _left.RemoveAt(i);
        }
    }

    /// <summary>The seat's prims that animate me, noted while I sit: after a teleport away the seat isn't around to ask.</summary>
    void Learn(GridClient c, Seat seat)
    {
        foreach (var p in _s.Anims.Now())
            if (p.Source != UUID.Zero && p.Source != c.Self.AgentID && !seat.Sources.Contains(p.Source) && RootOf(p.Source) == seat.Root)
                seat.Sources.Add(p.Source);
    }

    UUID? RootOf(UUID id) => _s.Objects.FindAnywhere(id) is { } f ? _s.Objects.RootOf(f.World, f.Prim).ID : null;

    void StopLeftovers(GridClient c, Seat seat)
    {
        var playing = _s.Anims.Now();
        var stop = Leftovers(playing, seat, c.Self.AgentID, RootOf);
        if (stop.Count == 0) return;
        c.Self.Animate(stop.ToDictionary(x => x, _ => false), true);
        // RevokePermsOnStopAnimation, for this object only: each prim of it that animated me, and its root
        var sources = playing.Where(p => stop.Contains(p.Id)).Select(p => p.Source).Append(seat.Root).Distinct().ToList();
        bool revoked = false;
        if (_s.World.Region(_s.GridKey, seat.Region)?.Simulator is { Connected: true } sim)
        {
            foreach (var id in sources)
                c.Network.SendPacket(new RevokePermissionsPacket
                {
                    AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                    Data = { ObjectID = id, ObjectPermissions = SelfAnimations.AnimationPermissions },
                }, sim);
            revoked = true;
        }
        string name = seat.Name.Length > 0 ? seat.Name : seat.Root.ToString();
        _s.Log.LogInformation("stood up from {Seat}: stopped its {N} animation{S}{Revoked}", name, stop.Count, stop.Count == 1 ? "" : "s",
            revoked ? ", its animation permission revoked" : " (its region is gone, nothing to revoke there)");
    }
}
