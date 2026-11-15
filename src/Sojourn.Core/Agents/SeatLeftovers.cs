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
    readonly List<Look> _looks = new();
    /// <summary>After a seat's leftovers are seen to, what still animates me is looked at this much later (and this long
    /// after coming into a region, its list of what animates me coming with my avatar).</summary>
    public static readonly TimeSpan CheckAfter = TimeSpan.FromSeconds(3);
    /// <summary>A first look that found nothing in the region the stop went to keeps watching this long from the stop
    /// for my coming into another: a teleport under way carries the pose there.</summary>
    public static readonly TimeSpan WatchFor = TimeSpan.FromSeconds(30);
    ulong _regionNow;
    DateTime _regionSince;

    public SeatLeftovers(AgentSession s) => _s = s;

    /// <summary>A seat as a linkset (its root's id), where it was, and the prims of it seen animating me while I sat.</summary>
    public sealed class Seat(UUID root, ulong region, string name)
    {
        public UUID Root { get; } = root;
        public ulong Region { get; set; } = region;
        public string Name { get; set; } = name;
        public HashSet<UUID> Sources { get; } = new();
        /// <summary>The animations they played on me (by id: another region may name no source for them).</summary>
        public HashSet<UUID> Played { get; } = new();
    }

    /// <summary>A look at what a left seat still plays on me, due at At. Last: the one after its leftovers were stopped
    /// again. StoppedIn: the region the last stop went to. Watching: a first look found nothing in that region; until
    /// WatchUntil, coming into another means a look there.</summary>
    sealed class Look(Seat seat, DateTime at, ulong stoppedIn, DateTime watchUntil)
    {
        public Seat Seat { get; } = seat;
        public DateTime At = at;
        public bool Last, Watching;
        public ulong StoppedIn = stoppedIn;
        public DateTime WatchUntil { get; } = watchUntil;
    }

    enum Found { Nothing, StoppedAgain, Said }

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
        if (w.Handle != _regionNow) { _regionNow = w.Handle; _regionSince = t; }
        if (!unresolved)
        {
            if (_on is { } was && now?.Root != was.Root) _left.Add((was, t));
            _on = now;
        }
        if (_on is { } seat) Learn(c, seat);
        for (int i = _looks.Count - 1; i >= 0; i--)
        {
            var k = _looks[i];
            if (!unresolved && _on?.Root == k.Seat.Root) { _looks.RemoveAt(i); continue; }   // sat on it again: its pose is wanted
            if (k.Watching)
            {
                if (w.Handle != k.StoppedIn) { k.Watching = false; k.At = _regionSince + CheckAfter; }
                else if (t >= k.WatchUntil) _looks.RemoveAt(i);
                continue;
            }
            if (t < k.At) continue;
            if (t - _regionSince < CheckAfter) { k.At = _regionSince + CheckAfter; continue; }   // just come into a region: its list comes with my avatar
            // (in another region than the last stop went to, it's a first look there: that stop never reached this one)
            switch (StillPlaying(c, w, k.Seat, k.Last && w.Handle == k.StoppedIn))
            {
                case Found.StoppedAgain:
                    k.Last = true;
                    k.StoppedIn = w.Handle;
                    k.At = t + CheckAfter;
                    break;
                case Found.Nothing when !k.Last && w.Handle == k.StoppedIn && t < k.WatchUntil:
                    k.Watching = true;
                    break;
                default:
                    _looks.RemoveAt(i);
                    break;
            }
        }
        if (_left.Count == 0) return;

        for (int i = _left.Count - 1; i >= 0; i--)
        {
            var (left, at) = _left[i];
            bool near = left.Region != w.Handle && _s.VisibleRegions().Any(r => r.Handle == left.Region);
            switch (Judge(left, t - at, unresolved ? null : _on?.Root, w.Handle, near))
            {
                case Verdict.Wait: continue;
                case Verdict.Due:
                    StopLeftovers(c, left);
                    if (_looks.Count < 4) _looks.Add(new Look(left, t + CheckAfter, w.Handle, t + WatchFor));
                    break;
            }
            _left.RemoveAt(i);
        }
    }

    /// <summary>The seat's prims that animate me and what they play, noted while I sit: after a teleport away the seat
    /// isn't around to ask.</summary>
    void Learn(GridClient c, Seat seat)
    {
        foreach (var p in _s.Anims.Now())
            if (p.Source != UUID.Zero && p.Source != c.Self.AgentID && (seat.Sources.Contains(p.Source) || RootOf(p.Source) == seat.Root))
            {
                seat.Sources.Add(p.Source);
                seat.Played.Add(p.Id);
            }
    }

    /// <summary>What still animates me a moment after standing, besides the region's own (standing, walking), into the
    /// log; and when what's left looks like the seat's, said. A seat can set its pose as my standing (or flying) one with
    /// llSetAnimationOverride, which stopping animations can't touch: the region plays it again for the state. Taking the
    /// seat's animation permissions back resets it (as the viewer's "Stop animating me" does, StopLeftovers), but only
    /// while the seat is in the region; one gone by then (a tour vehicle deleting itself as it puts the rider off) leaves
    /// the override until the person logs in again (wiki: llSetAnimationOverride). A blind tester stayed in a jet's flying
    /// pose after its tour put her off (2026-11-11), through a stop-all ball, until she logged in again. Left: neither the
    /// seat's own (its prims, still playing after StopLeftovers stopped them) nor one with no source that isn't mine, the
    /// agent API's or the AO's. The stop at standing can go to the region being left as a teleport takes me away, and
    /// the next one still plays the pose (a boat's, as she took a friend's teleport off it, her drive of 2026-11-14; she
    /// logged in again): so at the first look, what of it is the seat's (its prims', or an animation it played on me
    /// while I sat, whatever source this region gives) is stopped again here, and looked at once more; said only if
    /// something is left then, or at once when what's left can't be stopped (no source, not the seat's: an override).</summary>
    Found StillPlaying(GridClient c, RegionWorld w, Seat seat, bool last)
    {
        string name = seat.Name.Length > 0 ? seat.Name : seat.Root.ToString();
        // (until this region has said who started each, every animation has no source: only the seat's by id count)
        bool sources = _s.Anims.KnowsSources();
        var others = _s.Anims.Now().Where(p => !p.BuiltIn).ToList();
        if (others.Count == 0) { _s.Log.LogInformation("after standing from {Seat}: nothing animates me but the region's own", name); return Found.Nothing; }
        bool left = false;
        var again = new List<UUID>();
        var words = others.Select(p =>
        {
            if (seat.Played.Contains(p.Id)) { left = true; again.Add(p.Id); }
            if (p.Source == UUID.Zero || p.Source == c.Self.AgentID)
            {
                bool ours = _s.Anims.Mine(p.Id) || _s.Ao.Plays(p.Id);
                left |= !ours && sources;
                return $"{p.Id} ({(seat.Played.Contains(p.Id) ? "the seat's, no source here" : ours ? "mine" : "no source")})";
            }
            bool seats = seat.Sources.Contains(p.Source) || RootOf(p.Source) == seat.Root;
            left |= seats;
            if (seats) again.Add(p.Id);
            if (w.PrimById(p.Source) is not { } prim) return $"{p.Id} (started by {(seats ? name : p.Source.ToString())}, not in this region)";
            var root = _s.Objects.RootOf(w, prim);
            var by = root.Properties?.Name ?? prim.Properties?.Name ?? p.Source.ToString();
            return $"{p.Id} (started by {by}{(SelfAnimations.WornByMe(w, prim, c.Self.AgentID) ? ", worn" : "")})";
        });
        var list = string.Join("; ", words);   // (the words first: making them gathers left and again)
        _s.Log.LogInformation("after standing from {Seat}: still animating me{Again}: {List}", name, last ? " after stopping it again" : "", list);
        if (!left) return Found.Nothing;
        if (!last && again.Count > 0)
        {
            var stop = again.Distinct().ToList();
            c.Self.Animate(stop.ToDictionary(x => x, _ => false), true);
            _s.Log.LogInformation("after standing from {Seat}: stopped {N} of its again here", name, stop.Count);
            return Found.StoppedAgain;
        }
        _s.Notify.Add("PoseLeft", "notify", $"{name} may have left its pose on you now you're off it. If you're still in it, stopping all your animations may clear it, "
            + "and logging out and in again does", Notifications.None);
        return Found.Said;
    }

    UUID? RootOf(UUID id) => _s.Objects.FindAnywhere(id) is { } f ? _s.Objects.RootOf(f.World, f.Prim).ID : null;

    void StopLeftovers(GridClient c, Seat seat)
    {
        var playing = _s.Anims.Now();
        var stop = Leftovers(playing, seat, c.Self.AgentID, RootOf);
        if (stop.Count == 0) return;
        seat.Played.UnionWith(stop);
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
