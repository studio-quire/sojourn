using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.World;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>
/// Railway switches (docs/access-app.md > Walk mode > Rail switches): seated on something moving, a switch coming up
/// along the way ahead is said once ("Switch in 40 metres, set to the main line."), W says the next one, Shift+W throws
/// it and says the new setting once it has changed; //switch does the same from routes and notecards.
///
/// The model is the Second Life Railroad's switch (the SL wiki's SLRR standards and SLRR Switch pages, 2010-2012, which
/// the Virtual Rail Consortium's tools follow): a linkset whose root is named "Switch Stand", its description
/// "Identifier~Status~Heading Name~Mainline Name~Branch Name~channel" with Status "Main" or "Branch", a large invisible
/// part over the stand taking touches, and invisible guide rails the trains steer by: the one in use named "Guide" and
/// solid, the other renamed "Alternate Guide" and phantom. So the guides' names swap with the setting: the stand's
/// description says how it's set, and the rails say which way that takes the train. Written by Claude Opus 5.5,
/// 2026-11-01.
///
/// Since 2026-11-03 the track itself is followed where its rails are known (RailTrack): from the seat along the rails
/// the train is on, so a switch is said by its distance along the track, only if it's on the track (not on a line
/// beside it), as facing (it decides the way: which side it takes the train, which side the other line goes) or
/// trailing (a line joins: set for the person or against them). Where the rails don't say, it says less. With no rails
/// known, the way ahead is a straight band from the seat, as before.
/// </summary>
public sealed partial class WalkMode
{
    /// <summary>A switch around: its stand (or the rail standing for it), where it is in the person's region's frame,
    /// what its stand's description says, and, where the track is known, how far along it the switch is (NaN: not on the
    /// track; null: no track known) and what the rails there say.</summary>
    public sealed record RailSwitch(UUID Id, uint LocalId, ulong Region, Vector3 Pos, bool Stand, SwitchStand.Setting? Setting,
        float? Along = null, RailTrack.Junction? Junction = null);

    // said within this far ahead (about 7 s of travel, 60-100 m), names asked for 30 m further, so they're known by
    // then (0.1.0.877 said most switches on the Linden track only 5-25 m out at 11 m/s: names came late)
    float _switchAhead = 60f, _switchLook = 90f;
    static float AheadFor(float speed) => Math.Clamp(speed * 7, 60, 100);
    const float SwitchAsk = 60f;          // W and //switch: the nearest within this far when none is ahead
    const float SwitchReach = 60f;        // a switch's parts (its guide rails) this far from its stand are that switch
    const float SwitchNear = 5f;          // one first due closer than this isn't said: too near to act on
    const long SwitchSettle = 2000;       // a part with no stand near waits this long for one's name before standing alone
    const float StandToPoints = 12f;      // a stand this near where lines part or join is that switch's
    const float StandBeside = 3f;         // a stand this near the track, with no lines seen parting there, is on it

    List<RailSwitch> _switches = new();
    long _switchScanAt, _switchSaidAt, _switchWatchAt, _switchMovedAt = -1_000_000;
    readonly Dictionary<UUID, long> _switchInWay = new();       // when each was last in the way ahead
    readonly HashSet<UUID> _switchPending = new();              // come into the way ahead, not said yet
    readonly Dictionary<UUID, string> _switchToldAs = new();    // the setting last said for each
    readonly Dictionary<UUID, string> _switchToldWay = new();   // and what its rails were said to do
    readonly Dictionary<UUID, long> _switchAlone = new();       // when each part with no stand near was first seen so
    readonly Dictionary<UUID, string> _switchLogged = new();    // what the log last said of each part found
    bool _switchBusy;

    /// <summary>A rail the track is followed by, as found: index-aligned with the rails given to RailTrack.</summary>
    sealed record RailPrim(UUID Id, uint LocalId, ulong Region, string RegionName, Vector3 Local, bool Phantom, bool Scripted);

    RailTrack.Track? _railTrack;              // the track from the seat at the last look, in that look's frame
    List<RailPrim> _railPrims = new();
    ulong _railRegion;
    Vector3 _railSeat;
    long _railLoggedAt;
    string _railLogged = "";
    readonly Dictionary<UUID, string> _junctionLogged = new();
    readonly Dictionary<UUID, bool> _railPhantom = new();       // each rail's phantom flag last look: a flip means a throw
    readonly Dictionary<UUID, (RailTrack.Junction J, ulong Region, long At)> _switchPassing = new();   // said, to log how it went

    /// <summary>The switches the last look found, nearest first (the status op, tests).</summary>
    public IReadOnlyList<RailSwitch> Switches => _switches;

    /// <summary>The track the last look followed, if any (the status op, tests).</summary>
    public RailTrack.Track? RailWay => _railTrack;

    /// <summary>Each tick: seated, a look around for switches (every second while moving, every three for two minutes after), one
    /// said as it comes into the way ahead, and the next one ahead read again now and then in case it changed.</summary>
    void SwitchTick(Body b, long now)
    {
        if (!b.Sitting || _set.WalkMode("switch") == "off") { _switchPending.Clear(); _railTrack = null; return; }
        float speed = Speed(b);
        if (speed > 1f) _switchMovedAt = now;
        _switchAhead = AheadFor(speed);
        _switchLook = _switchAhead + 30;
        // only on something that moves (a seat at a club never looks), and for two minutes after it stops (a station)
        if (now - _switchMovedAt > 120_000 || now - _switchScanAt < (speed > 1f ? 1000 : 3000)) return;
        _switchScanAt = now;
        float yaw = Way(b);
        ScanSwitches(b, yaw);
        PassedLog(b);

        // what's in the way ahead, kept while stopped too (a stop before a switch to throw it doesn't make it new again)
        RailSwitch? next = null;
        float nextAlong = float.MaxValue;
        foreach (var s in _switches)
        {
            if (!WayAhead(b, yaw, s, _switchAhead, out float along)) continue;
            bool fresh = !_switchInWay.TryGetValue(s.Id, out var last) || now - last > 60_000;
            _switchInWay[s.Id] = now;
            if (fresh)
            {
                _switchPending.Add(s.Id);
                SwitchLog($"{PartWords(s)} came into the way ahead, {along:0} m {(s.Along != null ? "along the track" : "along")}, {Aside(b, yaw, s.Pos):0} m aside, at {speed:0} m/s");
            }
            if (along < nextAlong) { next = s; nextAlong = along; }
        }
        _switchPending.RemoveWhere(id => !_switchInWay.TryGetValue(id, out var at) || at != now);
        if (_switchInWay.Count > 200)
            foreach (var id in _switchInWay.Where(x => now - x.Value > 600_000).Select(x => x.Key).ToList())
            { _switchInWay.Remove(id); _switchToldAs.Remove(id); _switchToldWay.Remove(id); }
        if (_switchAlone.Count > 500)
            foreach (var id in _switchAlone.Where(x => now - x.Value > 600_000).Select(x => x.Key).ToList()) _switchAlone.Remove(id);
        if (_switchBusy || speed < 1f) return;

        // the nearest not yet said, read fresh first (one at a time, nearest first, a few seconds apart)
        var say = _switches.Where(s => _switchPending.Contains(s.Id))
            .Select(s => (s, d: WayAhead(b, yaw, s, float.MaxValue, out var al) ? al : float.MaxValue)).OrderBy(x => x.d).FirstOrDefault();
        if (say.s != null && now - _switchSaidAt > 2500)
        {
            _switchPending.Remove(say.s.Id);
            _ = AnnounceAsync(say.s);
            return;
        }
        // the next one ahead, already said: read again every few seconds, said when it changed (another train sprang it,
        // or it went back to the main line by itself)
        if (next != null && (next.Stand || next.Junction != null) && _switchToldAs.ContainsKey(next.Id) && now - _switchWatchAt > 4000)
        {
            _switchWatchAt = now;
            _ = WatchAsync(next);
        }
    }

    async Task AnnounceAsync(RailSwitch s)
    {
        _switchBusy = true;
        try
        {
            var fresh = await ReadSwitchAsync(s, rails: true);
            if (Me() is not { } now) return;
            // where it is now, in the frame the person is in now: a crossing meanwhile moved the frame (0.1.0.811 said
            // "Switch in 185 metres" at crossings, from the old region's position against the new one's)
            if (!WayAhead(now, Way(now), fresh, _switchAhead + 10, out float along) || along < SwitchNear)
            {
                SwitchLog($"{PartWords(fresh)} not said: {(along < SwitchNear && along > -SwitchNear ? $"{along:0} m ahead, too near" : "no longer in the way ahead")}");
                return;
            }
            _switchSaidAt = Environment.TickCount64;
            Told(fresh);
            // under a second and a half away it's said as just ahead: too near to throw, but which way it takes you
            var line = Sentence(fresh, along < Speed(now) * 1.5f ? "just ahead" : $"in {SwitchMetres(along)}");
            SwitchLog($"said \"{line}\" of {PartWords(fresh)}{JunctionLogWords(fresh)}");
            Cue("switch", CueSounds.Cue.Switch, line, now, MathF.Atan2(fresh.Pos.Y - now.Pos.Y, fresh.Pos.X - now.Pos.X));
        }
        finally { _switchBusy = false; }
    }

    async Task WatchAsync(RailSwitch s)
    {
        _switchBusy = true;
        try
        {
            var fresh = await ReadSwitchAsync(s, rails: true);
            var status = fresh.Setting?.Status ?? "";
            var way = WayKey(fresh);
            bool statusChanged = _switchToldAs.TryGetValue(s.Id, out var was) && was != status && status != "";
            bool wayChanged = _switchToldWay.TryGetValue(s.Id, out var wasWay) && wasWay != way && way != "";
            // a stand serving two places: passing one, the next is another switch, facing where the last was trailing, not a
            // change (0.1.0.903 said "now set for you" of it as she went by)
            if (wayChanged && wasWay is { Length: > 0 } && wasWay.Split(' ')[0] != way.Split(' ')[0]) { wayChanged = false; _switchToldWay[s.Id] = way; }
            if (!statusChanged && !wayChanged) return;
            if (Me() is not { } b) return;
            SwitchLog($"{PartWords(fresh)} changed by itself: {(was is null or "" ? "unknown" : was)} to {(status == "" ? "unknown" : status)}"
                + $", its rails {(wasWay is null or "" ? "unclear" : wasWay)} to {(way == "" ? "unclear" : way)}{JunctionLogWords(fresh)}");
            Told(fresh);
            _switchSaidAt = Environment.TickCount64;
            // what the rails now do, where they say; else the stand's new setting
            var line = wayChanged && Changed(fresh) is { } c ? $"The switch ahead {c}." : $"The switch ahead is now {SettingWords(fresh)}.";
            Cue("switch", CueSounds.Cue.Switch, line, b, MathF.Atan2(fresh.Pos.Y - b.Pos.Y, fresh.Pos.X - b.Pos.X));
        }
        finally { _switchBusy = false; }
    }

    // --- what's said ------------------------------------------------------------------------------------------------

    /// <summary>A switch's line: "Switch in 40 metres, set to the main line, North Station: it takes you left; the branch,
    /// East Yard, goes right." Facing with its rails clear: which side it takes the person, which side the other line
    /// goes (named from the stand where it says how it's set). Trailing with its rails clear: "Switch in 40 metres where a
    /// line joins from the left: set for your line." (or the other line). Else the stand's words, as without a track.</summary>
    static string Sentence(RailSwitch s, string where)
    {
        var j = s.Junction;
        if (j is { Kind: RailTrack.Kind.Facing, Verdict: RailTrack.Verdict.For, OtherSide: not RailTrack.Side.None })
        {
            string own = SideWord(j.OtherSide == RailTrack.Side.Left ? RailTrack.Side.Right : RailTrack.Side.Left), other = SideWord(j.OtherSide);
            string otherLine = s.Setting?.Status switch
            {
                "Main" => "the branch" + (s.Setting.Branch is { } br ? $", {br}," : ""),
                "Branch" => "the main line" + (s.Setting.Mainline is { } m ? $", {m}," : ""),
                _ => "the other line",
            };
            var set = s.Setting?.Status != null ? $", {SettingWords(s)}" : "";
            return $"Switch {where}{set}: it takes you {own}; {otherLine} goes {other}.";
        }
        if (j is { Kind: RailTrack.Kind.Trailing, Verdict: RailTrack.Verdict.For or RailTrack.Verdict.Against })
        {
            var joins = j.OtherSide == RailTrack.Side.None ? "another line joins" : $"a line joins from the {SideWord(j.OtherSide)}";
            return $"Switch {where} where {joins}: set for {(j.Verdict == RailTrack.Verdict.For ? "your" : "the other")} line.";
        }
        return $"Switch {where}, {SettingWords(s)}.";
    }

    /// <summary>What a switch already said now does, when its rails changed: "now takes you right", "is now set for the other
    /// line"; null if its rails no longer say.</summary>
    static string? Changed(RailSwitch s) => s.Junction switch
    {
        { Kind: RailTrack.Kind.Facing, Verdict: RailTrack.Verdict.For, OtherSide: not RailTrack.Side.None } j =>
            $"now takes you {SideWord(j.OtherSide == RailTrack.Side.Left ? RailTrack.Side.Right : RailTrack.Side.Left)}",
        { Kind: RailTrack.Kind.Trailing, Verdict: RailTrack.Verdict.For } => "is now set for your line",
        { Kind: RailTrack.Kind.Trailing, Verdict: RailTrack.Verdict.Against } => "is now set for the other line",
        _ => null,
    };

    /// <summary>What the rails were said to do, to tell when it changes ("" when they didn't say).</summary>
    static string WayKey(RailSwitch s) => s.Junction is { } j && Changed(s) != null ? $"{j.Kind} {j.Verdict} {j.OtherSide}" : "";

    static string SideWord(RailTrack.Side side) => side == RailTrack.Side.Left ? "left" : "right";

    static string SwitchMetres(float m) => m < 20 ? Bearings.Metres(m) : Bearings.Metres(Math.Round(m / 5) * 5);

    static string SettingWords(RailSwitch s) => s.Setting?.Status switch
    {
        "Main" => "set to the main line" + (s.Setting!.Mainline is { } to ? $", {to}" : ""),
        "Branch" => "set to the branch" + (s.Setting!.Branch is { } to ? $", {to}" : ""),
        _ => s.Stand ? "its stand doesn't say how it's set" : s.Junction != null ? "no switch stand found for it" : "how it's set isn't known: no switch stand found for it",
    };

    // --- finding them ------------------------------------------------------------------------------------------------

    /// <summary>Roots in the regions in view, near enough: switch stands, the guide rails, and the parts that don't say
    /// their setting. Seated, the track is followed by the rails (RailTrack) and each stand placed on it: at the place
    /// lines part or join nearest it, else on the track's line, else not on the track (a line beside it); places where
    /// lines part or join with no stand that the rails say something clear of are switches too. With no track, the
    /// parts that don't say (a stand that doesn't, an alternate guide) are switches of their own only with no such stand
    /// within 60 m and after a moment alone (W asks at once: not patient). The names of the nearest unnamed ones asked
    /// for (thirty a look: the rail-shaped first, then around a part waiting for its stand, then ahead), so they're known
    /// by the time they're near. Region-wide scans only once a second at most (a train ride's garbage, WalkMode.SeatLinkset).</summary>
    void ScanSwitches(Body b, float yaw, bool patient = true)
    {
        long now = Environment.TickCount64;
        var known = new List<RailSwitch>();      // stands whose description says Main or Branch
        var parts = new List<RailSwitch>();      // the rest: stands that don't say, alternate guides
        var rails = new List<RailTrack.Rail>();
        var railPrims = new List<RailPrim>();
        var unnamed = new List<(RegionWorld W, Primitive P, Vector3 Pos, float D, bool Ahead, bool Rail)>();
        var dir = new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
        var asked = DateTime.UtcNow - TimeSpan.FromSeconds(5);
        foreach (var w in S.VisibleRegions())
        {
            var off = S.FrameOffset(w);
            var parents = new HashSet<uint>();
            int from = unnamed.Count;
            foreach (var (_, p) in w.Prims)
            {
                if (p.ParentID != 0) { parents.Add(p.ParentID); continue; }
                if (p.PrimData.PCode != PCode.Prim || p.IsAttachment) continue;
                var pos = new Vector3(p.Position.X + off.X, p.Position.Y + off.Y, p.Position.Z);
                var rel = new Vector2(pos.X - b.Pos.X, pos.Y - b.Pos.Y);
                float d = rel.Length();
                // stands a little further than looked for, so a guide rail ahead finds its stand beyond it
                if (d > _switchLook + SwitchReach || MathF.Abs(pos.Z - b.Pos.Z) > 30) continue;
                if (p.Properties is not { } props || props.ObjectID != p.ID)
                {
                    // not those asked for in the last 5 s (FetchAsync skips them): twenty a look that never answer
                    // would otherwise hold back every name behind them
                    if (!(w.PropsMeta.TryGetValue(p.ID, out var m) && m.RequestedAt > asked))
                        unnamed.Add((w, p, pos, d, Vector2.Dot(rel, dir) > -10 || d < SwitchAsk, RailShaped(p)));
                    continue;
                }
                var name = props.Name?.Trim() ?? "";
                bool guide = SwitchStand.IsGuide(name), alternate = SwitchStand.IsAlternateGuide(name);
                if ((guide || alternate) && d <= _switchLook + 60 && RailOf(p, pos, guide) is { } rail)
                {
                    rails.Add(rail with { Id = rails.Count });
                    railPrims.Add(new RailPrim(p.ID, p.LocalID, w.Handle, w.Name, new(p.Position.X, p.Position.Y, p.Position.Z), rail.Phantom,
                        (S.State.PrimFlagsOf(p) & PrimFlags.Scripted) != 0));
                }
                bool stand = SwitchStand.IsStand(name);
                if (!stand && !alternate) continue;
                var s = new RailSwitch(p.ID, p.LocalID, w.Handle, pos, stand, stand ? SwitchStand.Parse(props.Description) : null);
                LogPart(w, p, name, props.Description, s, b, yaw);
                (s.Setting?.Status != null ? known : parts).Add(s);
            }
            // a rail is a single prim
            for (int i = from; i < unnamed.Count; i++)
                if (unnamed[i].Rail && parents.Contains(unnamed[i].P.LocalID)) unnamed[i] = unnamed[i] with { Rail = false };
        }
        RenewFlipped(railPrims);

        var found = new List<RailSwitch>();
        var waiting = new List<RailSwitch>();
        var track = b.Sitting && rails.Count > 0 ? RailTrack.Follow(rails, b.Pos, dir, _switchLook) : null;
        _railTrack = track;
        _railPrims = railPrims;
        _railRegion = b.Region;
        _railSeat = b.Pos;
        if (track != null)
        {
            found = OnTrack(track, known, parts, b);
            TrackLog(b, yaw, track, rails.Count, unnamed.Count(x => x.Rail));
        }
        else
        {
            found.AddRange(known);
            foreach (var s in parts.OrderBy(s => s.Stand ? 0 : 1).ThenBy(s => Vector3.DistanceSquared(s.Pos, b.Pos)))
            {
                if (found.Where(f => Flat(f.Pos, s.Pos) < SwitchReach).MinBy(f => Flat(f.Pos, s.Pos)) is { } of)
                {
                    PlacedLog(s, $"part of {PartWords(of)}, {Flat(of.Pos, s.Pos):0} m from it");
                    continue;
                }
                if (!_switchAlone.TryGetValue(s.Id, out var since)) _switchAlone[s.Id] = since = now;
                if (patient && now - since < SwitchSettle) { waiting.Add(s); continue; }
                var stand = known.Count == 0 ? null : known.MinBy(k => Flat(k.Pos, s.Pos));
                PlacedLog(s, "a switch of its own: no stand saying how it's set within " + Bearings.Metres(SwitchReach)
                    + (stand != null ? $" (the nearest {Bearings.Metres(Flat(stand.Pos, s.Pos))} away)" : ""));
                found.Add(s);
            }
            if (b.Sitting && rails.Count > 0 && Speed(b) > 1f && now - _railLoggedAt > 15_000)
            {
                _railLoggedAt = now;
                _railLogged = "";
                SwitchLog($"no track: no rail under the seat at {S.Region?.Name} {b.Pos.X:0.0}, {b.Pos.Y:0.0}, {b.Pos.Z:0.0} heading {Deg(yaw):0}"
                    + $" (or two lines' about as near); {rails.Count} named rails around, nearest {NearestRail(rails, b.Pos)}");
            }
        }
        _switches = found.Where(s => Flat(s.Pos, b.Pos) <= _switchLook + 10).OrderBy(s => Vector3.DistanceSquared(s.Pos, b.Pos)).ToList();
        // names asked, thirty a look: rails first (the track is followed by them), nearest first, twelve at most; then
        // around a part waiting for its stand (ten at most), then the way ahead nearest along it (what a train meets
        // next), then the rest nearest. Nearest all round first left a busy track's names behind: 0.1.0.877 found most
        // switches only 5-25 m out at 11 m/s
        const int Names = 30;
        var ask = b.Sitting ? unnamed.Where(x => x.Rail && x.D <= _switchLook).OrderBy(x => x.D).Take(12).ToList() : new();
        ask.AddRange(unnamed.Where(x => x.D <= _switchLook + SwitchReach && waiting.Any(s => Flat(s.Pos, x.Pos) < SwitchReach))
            .OrderBy(x => x.D).Where(x => !ask.Contains(x)).Take(10));
        foreach (var x in unnamed.Select(x => (x, al: InWayAhead(b, yaw, x.Pos, _switchLook, out var al) ? al : float.NaN))
                     .Where(t => !float.IsNaN(t.al)).OrderBy(t => t.al).Select(t => t.x))
        {
            if (ask.Count >= Names) break;
            if (!ask.Contains(x)) ask.Add(x);
        }
        if (ask.Count < Names)
            ask.AddRange(unnamed.Where(x => x.D <= _switchLook && x.Ahead).OrderBy(x => x.D).Where(x => !ask.Contains(x)).Take(Names - ask.Count));
        foreach (var g in ask.GroupBy(x => x.W))
            _ = S.Objects.FetchAsync(g.Key, g.Select(x => x.P).ToList(), TimeSpan.Zero, CancellationToken.None, sim: g.Key.Simulator);
    }

    /// <summary>The stands placed on the track: each at the place lines part or join nearest it (within 12 m, each place
    /// one stand), else on the track's line (within 3 m: a switch whose other line's rails aren't known), else beyond
    /// where the track is known (the straight band from its end), else not on the track. Places where lines part or
    /// join with no stand become switches of their own when the rails say something clear that matters (which side a
    /// facing one takes the person; a trailing one set against them).</summary>
    List<RailSwitch> OnTrack(RailTrack.Track track, List<RailSwitch> known, List<RailSwitch> parts, Body b)
    {
        var stands = known.Concat(parts.Where(p => p.Stand)).ToList();
        var js = track.Junctions.Where(j => j.Along > -20).ToList();
        var standOf = new Dictionary<int, RailSwitch>();
        var junctionOf = new Dictionary<UUID, RailTrack.Junction>();
        foreach (var (s, j, _) in stands.SelectMany(s => js.Select(j => (s, j, d: Flat(s.Pos, j.At)))).Where(x => x.d <= StandToPoints).OrderBy(x => x.d))
        {
            int k = js.IndexOf(j);
            // a stand that says how it's set before one that doesn't, at the same place
            if (junctionOf.ContainsKey(s.Id) || standOf.ContainsKey(k)) continue;
            standOf[k] = s;
            junctionOf[s.Id] = j;
        }
        var o = new List<RailSwitch>();
        var end = track.Path.Count > 0 ? track.Path[^1] : b.Pos;
        var endDir = track.EndDir;
        foreach (var s in stands)
        {
            if (junctionOf.TryGetValue(s.Id, out var j)) { o.Add(s with { Along = j.Along, Junction = j }); continue; }
            float along = track.AlongOf(s.Pos, StandBeside, out float aside);
            if (!float.IsNaN(along)) { o.Add(s with { Along = along }); continue; }
            // beyond the rails known: the straight band on from the track's end
            if (endDir != Vector2.Zero && Flat(s.Pos, end) < Flat(s.Pos, b.Pos)
                && InWayAhead(new Body(b.Region, b.RegionName, end, end.Z, MathF.Atan2(endDir.Y, endDir.X), default, false, true, null, null),
                    MathF.Atan2(endDir.Y, endDir.X), s.Pos, _switchLook, out float beyond))
            {
                o.Add(s with { Along = track.Length + beyond });
                continue;
            }
            // a stand beside a switch that has its own (a second stand, a signal box) is part of it; else not on the track
            if (o.Concat(stands).FirstOrDefault(x => x.Id != s.Id && junctionOf.ContainsKey(x.Id) && Flat(x.Pos, s.Pos) < 20) is { } of)
                PlacedLog(s, $"part of {PartWords(of)}, {Flat(of.Pos, s.Pos):0} m from it");
            o.Add(s with { Along = float.NaN });
        }
        for (int k = 0; k < js.Count; k++)
        {
            var j = js[k];
            if (standOf.ContainsKey(k)) continue;
            bool clear = j is { Kind: RailTrack.Kind.Facing, Verdict: RailTrack.Verdict.For, OtherSide: not RailTrack.Side.None }
                or { Kind: RailTrack.Kind.Trailing, Verdict: RailTrack.Verdict.Against };
            if (!clear || j.OtherRail >= _railPrims.Count) continue;
            var r = _railPrims[j.OtherRail];
            o.Add(new RailSwitch(r.Id, r.LocalId, r.Region, j.At, false, null, j.Along, j));
        }
        return o;
    }

    /// <summary>A guide rail as RailTrack takes it: its long axis (x, or y if longer), half its length, its name.</summary>
    RailTrack.Rail? RailOf(Primitive p, Vector3 pos, bool guide)
    {
        var q = p.Rotation;
        bool alongY = p.Scale.Y > p.Scale.X;
        var axis = alongY
            ? new Vector3(2 * (q.X * q.Y - q.W * q.Z), 1 - 2 * (q.X * q.X + q.Z * q.Z), 2 * (q.Y * q.Z + q.W * q.X))
            : new Vector3(1 - 2 * (q.Y * q.Y + q.Z * q.Z), 2 * (q.X * q.Y + q.W * q.Z), 2 * (q.X * q.Z - q.W * q.Y));
        float len = alongY ? p.Scale.Y : p.Scale.X;
        if (len < 1f || new Vector2(axis.X, axis.Y).Length() < 0.5f) return null;   // standing on end: not a rail to follow
        return new RailTrack.Rail(0, pos, Vector3.Normalize(axis), len / 2, guide, Phantom(p));
    }

    /// <summary>Shaped like a guide rail: one long axis 1.5-20 m, the others a metre or less (the Linden track's are
    /// 0.5 by 0.5, 2-14 m long).</summary>
    static bool RailShaped(Primitive p)
    {
        var s = p.Scale;
        float lng = Math.Max(s.X, s.Y), across = Math.Min(s.X, s.Y);
        return lng is >= 1.5f and <= 20f && across <= 1.2f && s.Z <= 1.2f;
    }

    /// <summary>A rail whose phantom flag flipped since the last look was most likely thrown, and renamed with it: the
    /// region sends a new name only to whoever has the object selected, so it's asked again.</summary>
    void RenewFlipped(List<RailPrim> rails)
    {
        var flipped = new List<RailPrim>();
        foreach (var r in rails)
        {
            if (_railPhantom.TryGetValue(r.Id, out var was) && was != r.Phantom) flipped.Add(r);
            _railPhantom[r.Id] = r.Phantom;
        }
        if (_railPhantom.Count > 5000) _railPhantom.Clear();
        if (flipped.Count > 0) _ = RenewRailsAsync(flipped);
    }

    async Task RenewRailsAsync(IEnumerable<RailPrim> rails)
    {
        foreach (var g in rails.GroupBy(r => r.Region))
        {
            if (S.VisibleRegions().FirstOrDefault(w => w.Handle == g.Key) is not { } w) continue;
            var prims = g.Select(r => w.Prims.TryGetValue(r.LocalId, out var p) ? p : null).OfType<Primitive>().ToList();
            try { await S.Objects.RenewAsync(w, prims, TimeSpan.FromSeconds(2), CancellationToken.None, w.Simulator); }
            catch (Exception) { }
        }
    }

    static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));

    /// <summary>The way the person goes: seated and moving, the seat's way; seated and still, its facing; else theirs.</summary>
    float Way(Body b) => b.Sitting ? (Speed(b) > 0.5f ? MathF.Atan2(b.Vel.Y, b.Vel.X) : b.Yaw) : Facing(b);

    /// <summary>In the way ahead: on the track, up to <paramref name="range"/> along it from where the seat is now; with no
    /// track known, the straight band (InWayAhead).</summary>
    bool WayAhead(Body b, float yaw, RailSwitch s, float range, out float along)
    {
        if (s.Along is not { } at) return InWayAhead(b, yaw, s.Pos, range, out along);
        along = float.NaN;
        if (float.IsNaN(at)) return false;
        along = at - TrackGone(b);
        return along > 1 && along <= range && MathF.Abs(s.Pos.Z - b.Pos.Z) < 20;
    }

    /// <summary>How far the seat has gone along the track since it was followed (the frame may have moved meanwhile).</summary>
    float TrackGone(Body b)
    {
        if (_railTrack == null) return 0;
        var p = b.Pos + Shift(b.Region, _railRegion);
        float g = _railTrack.AlongOf(p, 6, out _);
        return float.IsNaN(g) ? Flat(p, _railSeat) : g;
    }

    /// <summary>What to add to a position in one region's frame to have it in another's.</summary>
    static Vector3 Shift(ulong from, ulong to)
    {
        if (from == to) return Vector3.Zero;
        Utils.LongToUInts(from, out var fx, out var fy);
        Utils.LongToUInts(to, out var tx, out var ty);
        return new Vector3((float)((double)fx - tx), (float)((double)fy - ty), 0);
    }

    // --- what the log says of them (the first real switches left too little to tell, 2026-11-02) ----------------------

    void SwitchLog(string line) => S.Log.LogInformation("switch: {Line}", line);

    static float Deg(float yaw) => (MathF.Round(yaw * 180 / MathF.PI) % 360 + 360) % 360;

    /// <summary>A part as the log names it: its name, local id, region and place there.</summary>
    string PartWords(RailSwitch s)
    {
        var w = RegionOf(s);
        var p = w != null && w.Prims.TryGetValue(s.LocalId, out var q) ? q : null;
        var at = p != null ? $"{p.Position.X:0}, {p.Position.Y:0}, {p.Position.Z:0}" : "gone";
        return $"{p?.Properties?.Name?.Trim() ?? (s.Stand ? "a stand" : "an alternate guide")} (local {s.LocalId}, {w?.Name ?? "?"} {at})";
    }

    /// <summary>A rail as the log names it: Guide or Alternate (G, A), local id, region and place to the decimetre,
    /// length, facing to the degree, phantom.</summary>
    string RailWords(int i)
    {
        if (_railTrack == null || i < 0 || i >= _railPrims.Count) return "?";
        var r = _railPrims[i];
        var w = S.VisibleRegions().FirstOrDefault(x => x.Handle == r.Region);
        var p = w != null && w.Prims.TryGetValue(r.LocalId, out var q) ? q : null;
        string name = p?.Properties?.Name?.Trim() is { } n && SwitchStand.IsGuide(n) ? "G" : "A";
        return p == null ? $"{name} local {r.LocalId} gone"
            : $"{name} local {r.LocalId} {r.RegionName} {p.Position.X:0.0},{p.Position.Y:0.0},{p.Position.Z:0.0} {Math.Max(p.Scale.X, p.Scale.Y):0.0}m {YawOf(p):0}deg"
              + $"{(Phantom(p) ? " phantom" : "")}{(r.Scripted ? " scripted" : "")}";
    }

    static string NearestRail(List<RailTrack.Rail> rails, Vector3 at)
    {
        var n = rails.MinBy(r => Vector3.Distance(r.Mid, at));
        return $"{(n.Guide ? "G" : "A")} at {n.Mid.X:0.0},{n.Mid.Y:0.0},{n.Mid.Z:0.0} ({Vector3.Distance(n.Mid, at):0.0} m)";
    }

    /// <summary>The track followed, when what it found changed (or every 15 s while moving): where the seat was, the
    /// rails taken (each with its place, so the log can be checked against the world), why it ends, and each place
    /// lines part or join, the first time and again when what its rails say changes.</summary>
    void TrackLog(Body b, float yaw, RailTrack.Track track, int named, int railShapedUnnamed)
    {
        long now = Environment.TickCount64;
        string key = track.Ends + "|" + string.Join(";", track.Junctions.Select(j => $"{j.OtherRail}{j.Kind}{j.Verdict}{j.OtherSide}"));
        foreach (var j in track.Junctions)
        {
            if (j.OtherRail >= _railPrims.Count) continue;
            var id = _railPrims[j.OtherRail].Id;
            var said = $"{j.Kind} {j.Verdict} {j.OtherSide}";
            if (_junctionLogged.TryGetValue(id, out var was) && was == said) continue;
            if (_junctionLogged.Count > 2000) _junctionLogged.Clear();
            _junctionLogged[id] = said;
            SwitchLog($"{(was == null ? "lines" : "now")} {(j.Kind == RailTrack.Kind.Facing ? "part" : "join")} {j.Along:0} m along the track, at "
                + $"{S.Region?.Name} {j.At.X:0.0},{j.At.Y:0.0}: {j.Verdict.ToString().ToLowerInvariant()}, the other line {j.OtherSide.ToString().ToLowerInvariant()}"
                + $" ({j.Separation:0.0} m apart within 50 m); this track's rail {RailWords(j.OwnRail)}{(j.OwnGuide ? "" : ", its line an alternate's")};"
                + $" the other's {RailWords(j.OtherRail)}; this line {Points(j.OwnLeg)}; the other {Points(j.OtherLeg)}");
        }
        if (key == _railLogged && (now - _railLoggedAt < 15_000 || Speed(b) < 1f)) return;
        _railLogged = key;
        _railLoggedAt = now;
        SwitchLog($"track from {S.Region?.Name} {b.Pos.X:0.0}, {b.Pos.Y:0.0}, {b.Pos.Z:0.0} heading {Deg(yaw):0} at {Speed(b):0} m/s,"
            + $" {track.StartOffset:0.0} m off its rail: {track.Rails.Count} rails, {track.Length:0} m, ends: {track.Ends};"
            + $" {named} named rails around, {railShapedUnnamed} rail-shaped not named yet; rails: {string.Join(" > ", track.Rails.Select(RailWords))}"
            + (track.Junctions.Count == 0 ? "" : "; " + string.Join("; ", track.Junctions.Select(j =>
                $"{j.Kind.ToString().ToLowerInvariant()} at {j.Along:0} m {j.Verdict.ToString().ToLowerInvariant()}, other {j.OtherSide.ToString().ToLowerInvariant()}"))));
    }

    static string Points(IReadOnlyList<Vector3> pts) => string.Join(" ", pts.Take(8).Select(p => $"{p.X:0},{p.Y:0}"));

    /// <summary>For the log line of a switch said: what its rails said, and the place on the track it was placed at.</summary>
    static string JunctionLogWords(RailSwitch s) =>
        s.Junction is { } j ? $" [{j.Kind.ToString().ToLowerInvariant()} {j.Verdict.ToString().ToLowerInvariant()}, other {j.OtherSide.ToString().ToLowerInvariant()}, {j.Along:0} m along at the last look]"
        : s.Along is { } a && !float.IsNaN(a) ? $" [on the track, {a:0} m along at the last look, no lines seen parting or joining there]" : "";

    /// <summary>Once past a switch it said, which line the seat is on: for a facing one, the line it said it would take
    /// or the other (so the next log confirms or corrects the model).</summary>
    void PassedLog(Body b)
    {
        long now = Environment.TickCount64;
        foreach (var (id, (j, region, at)) in _switchPassing.ToList())
        {
            var shift = Shift(region, b.Region);
            var point = j.At + shift;
            var rel = new Vector2(b.Pos.X - point.X, b.Pos.Y - point.Y);
            var dir = new Vector2(MathF.Cos(Way(b)), MathF.Sin(Way(b)));
            if (now - at > 300_000) { _switchPassing.Remove(id); continue; }
            if (rel.Length() < 15 || Vector2.Dot(rel, dir) < 10) continue;
            _switchPassing.Remove(id);
            float own = LegDist(j.OwnLeg, shift, b.Pos), other = LegDist(j.OtherLeg, shift, b.Pos);
            var on = j.Kind == RailTrack.Kind.Trailing ? "past where the lines join"
                : own + 1.5f < other ? "on the line it said it would take" : other + 1.5f < own ? "on the OTHER line" : "not clear which line";
            SwitchLog($"passed the {j.Kind.ToString().ToLowerInvariant()} switch at {point.X:0.0},{point.Y:0.0} said {j.Verdict.ToString().ToLowerInvariant()}"
                + $", other {j.OtherSide.ToString().ToLowerInvariant()}: now {on} ({own:0.0} m from this line, {other:0.0} from the other) at {Speed(b):0} m/s,"
                + $" at {S.Region?.Name} {b.Pos.X:0.0},{b.Pos.Y:0.0}");
        }
    }

    static float LegDist(IReadOnlyList<Vector3> leg, Vector3 shift, Vector3 p)
    {
        float best = float.MaxValue;
        for (int i = 1; i < leg.Count; i++)
        {
            var a = new Vector2(leg[i - 1].X + shift.X, leg[i - 1].Y + shift.Y);
            var c = new Vector2(leg[i].X + shift.X, leg[i].Y + shift.Y);
            var ab = c - a;
            float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(new Vector2(p.X, p.Y) - a, ab) / ab.LengthSquared(), 0, 1);
            best = MathF.Min(best, Vector2.Distance(new Vector2(p.X, p.Y), a + ab * t));
        }
        return best;
    }

    /// <summary>Each stand or alternate guide the first time it's found, and again when its name or description
    /// changes (a throw, a spring, another driver): name, place, description as written, its linkset, which part takes
    /// touches, phantom or not.</summary>
    void LogPart(RegionWorld w, Primitive p, string name, string? desc, RailSwitch s, Body b, float yaw)
    {
        var said = name + "\n" + desc;
        bool first = !_switchLogged.TryGetValue(p.ID, out var was);
        if (!first && was == said) return;
        if (_switchLogged.Count > 5000) _switchLogged.Clear();
        _switchLogged[p.ID] = said;
        var how = s.Stand ? $"read as {s.Setting?.Status ?? "unknown"}" : "an alternate guide";
        if (!first) { SwitchLog($"{PartWords(s)} now \"{desc}\", {how}"); return; }
        var set = S.Objects.Linkset(w, p);
        var touch = set.Where(x => (S.State.PrimFlagsOf(x) & PrimFlags.Touch) != 0)
            .Select(x => $"{(x.LocalID == p.LocalID ? "the root" : x.Properties?.Name?.Trim() ?? "a part")} {x.Scale.X:0.#}x{x.Scale.Y:0.#}x{x.Scale.Z:0.#}").ToList();
        SwitchLog($"found {PartWords(s)}: \"{desc}\", {how}; {set.Count} prim{(set.Count == 1 ? "" : "s")}, {p.Scale.X:0.#}x{p.Scale.Y:0.#}x{p.Scale.Z:0.#}"
            + $"{(Phantom(p) ? ", phantom" : "")}, facing {YawOf(p):0} degrees; touches: {(touch.Count == 0 ? "none" : string.Join("; ", touch))}"
            + $"; {Flat(s.Pos, b.Pos):0} m from me, {Along(b, yaw, s.Pos):0} along, {Aside(b, yaw, s.Pos):0} aside, at {Speed(b):0} m/s");
        // the rails around a stand, for working out which way each line goes (only those whose names have come)
        if (s.Stand)
            SwitchLog($"rails near {PartWords(s)}: " + string.Join("; ", w.Prims.Values
                .Where(g => g.ParentID == 0 && g.Properties?.Name?.Trim() is { } n && (SwitchStand.IsGuide(n) || SwitchStand.IsAlternateGuide(n))
                    && Vector2.Distance(new(g.Position.X, g.Position.Y), new(p.Position.X, p.Position.Y)) < SwitchReach)
                .OrderBy(g => Vector2.Distance(new(g.Position.X, g.Position.Y), new(p.Position.X, p.Position.Y)))
                .Take(40)
                .Select(g => $"{(SwitchStand.IsGuide(g.Properties!.Name!.Trim()) ? "G" : "A")} {g.Position.X:0.0},{g.Position.Y:0.0},{g.Position.Z:0.0}"
                    + $" {Math.Max(g.Scale.X, g.Scale.Y):0.0}m {YawOf(g):0}deg{(Phantom(g) ? " phantom" : "")}")));
    }

    readonly HashSet<(UUID, bool)> _switchPlaced = new();

    /// <summary>Where a part that doesn't say its setting went, once: a switch's own, or a switch of its own.</summary>
    void PlacedLog(RailSwitch s, string what)
    {
        if (_switchPlaced.Count > 5000) _switchPlaced.Clear();
        if (_switchPlaced.Add((s.Id, what.StartsWith("part")))) SwitchLog($"{PartWords(s)}: {what}");
    }

    static float Aside(Body b, float yaw, Vector3 pos)
    {
        var rel = new Vector2(pos.X - b.Pos.X, pos.Y - b.Pos.Y);
        return rel.X * MathF.Sin(yaw) - rel.Y * MathF.Cos(yaw);   // positive: to the right of the way
    }

    /// <summary>Which way a prim's own x axis points, degrees as Second Life counts them (0 east, counter-clockwise).</summary>
    static float YawOf(Primitive p)
    {
        var q = p.Rotation;
        float y = MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z)) * 180 / MathF.PI;
        return y < 0 ? y + 360 : y;
    }

    static float Along(Body b, float yaw, Vector3 pos) => Vector2.Dot(new(pos.X - b.Pos.X, pos.Y - b.Pos.Y), new(MathF.Cos(yaw), MathF.Sin(yaw)));

    /// <summary>In the way ahead with no track known: up to <paramref name="range"/> along the way the person goes, within
    /// a widening band either side (6 m, and a third of the way out: a curve's switch is a little off the straight line).</summary>
    static bool InWayAhead(Body b, float yaw, Vector3 pos, float range, out float along)
    {
        var rel = new Vector2(pos.X - b.Pos.X, pos.Y - b.Pos.Y);
        var dir = new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
        along = Vector2.Dot(rel, dir);
        float side = MathF.Abs(rel.X * dir.Y - rel.Y * dir.X);
        return along > 1 && along <= range && side <= 6 + along / 3 && MathF.Abs(pos.Z - b.Pos.Z) < 20;
    }

    /// <summary>The stand's description asked again (the region sends a changed one only to whoever has it selected);
    /// with rails, the names of the rails where its lines part or join too (a script renames them on a throw), and the
    /// track followed again with them, so what's said is what they say now.</summary>
    async Task<RailSwitch> ReadSwitchAsync(RailSwitch s, bool rails = false)
    {
        if (rails && s.Junction is { } j && _railTrack != null)
        {
            var ids = new[] { j.OwnRail, j.OtherRail }.Concat(_railTrack.Rails.Where(i => i < _railPrims.Count
                    && Vector3.Distance(Local(i), j.At) < 30)).Distinct()
                .Where(i => i >= 0 && i < _railPrims.Count && _railPrims[i].Scripted).Select(i => _railPrims[i]).ToList();
            if (ids.Count > 0) await RenewRailsAsync(ids);
        }
        RailSwitch fresh = s with { Pos = PosNow(s) ?? s.Pos };
        if (s.Stand && RegionOf(s) is { } w && w.Prims.TryGetValue(s.LocalId, out var p))
        {
            try { await S.Objects.RenewAsync(w, new[] { p }, TimeSpan.FromSeconds(2), CancellationToken.None, w.Simulator); }
            catch (Exception) { return s; }
            // and where it is now: the person's frame moves at a crossing
            var off = S.FrameOffset(w);
            fresh = s with { Setting = SwitchStand.Parse(p.Properties?.Description), Pos = new(p.Position.X + off.X, p.Position.Y + off.Y, p.Position.Z) };
        }
        if (rails && s.Along != null && Me() is { } b)
        {
            // the track again, with what's just been read: the switch as it's placed on it now
            ScanSwitches(b, Way(b));
            if (_switches.FirstOrDefault(x => x.Id == s.Id) is { } again) fresh = again with { Setting = fresh.Setting ?? again.Setting };
            else fresh = fresh with { Along = _railTrack != null ? float.NaN : null, Junction = null };
        }
        int i2 = _switches.FindIndex(x => x.Id == s.Id);
        if (i2 >= 0) _switches[i2] = fresh;
        return fresh;

        Vector3 Local(int i) => _railPrims[i].Local + Shift(_railPrims[i].Region, _railRegion);
    }

    RegionWorld? RegionOf(RailSwitch s) => S.VisibleRegions().FirstOrDefault(w => w.Handle == s.Region);

    /// <summary>Where it is now in the person's region's frame, or null if it's gone. A switch with no stand is where its
    /// lines part or join, not where its rail's middle is.</summary>
    Vector3? PosNow(RailSwitch s)
    {
        if (!s.Stand && s.Junction != null) return s.Pos + Shift(_railRegion, S.Client?.Network.CurrentSim?.Handle ?? _railRegion);
        if (RegionOf(s) is not { } w || !w.Prims.TryGetValue(s.LocalId, out var p)) return null;
        var off = S.FrameOffset(w);
        return new(p.Position.X + off.X, p.Position.Y + off.Y, p.Position.Z);
    }

    // --- asking and throwing ------------------------------------------------------------------------------------------

    /// <summary>The switch W and Shift+W are about: the nearest in the way ahead (within 90 m; along the track where it's
    /// known), else the nearest within 60 m any way.</summary>
    async Task<RailSwitch?> TheSwitchAsync(Body b)
    {
        float yaw = Way(b);
        ScanSwitches(b, yaw, patient: false);
        if (_switches.Count == 0)
        {
            // names asked just now: a moment for them to come
            await Task.Delay(1200);
            if (Me() is { } again) { b = again; ScanSwitches(b, yaw, patient: false); }
        }
        var ahead = _switches.Select(s => (s, ok: WayAhead(b, yaw, s, _switchLook, out var along), along))
            .Where(x => x.ok).OrderBy(x => x.along).Select(x => x.s).FirstOrDefault();
        return ahead ?? _switches.FirstOrDefault(s => Vector2.Distance(new(s.Pos.X, s.Pos.Y), new(b.Pos.X, b.Pos.Y)) <= SwitchAsk);
    }

    string WhereWords(Body b, RailSwitch s)
    {
        float yaw = Way(b);
        if (WayAhead(b, yaw, s, _switchLook, out var along)) return $"{SwitchMetres(along)} ahead";
        var rel = Bearings.Norm(Bearings.Compass(b.Pos.X, b.Pos.Y, s.Pos.X, s.Pos.Y) - Compass(Facing(b)));
        float d = Vector2.Distance(new(s.Pos.X, s.Pos.Y), new(b.Pos.X, b.Pos.Y));
        return rel is > 150 and < 210 ? $"{SwitchMetres(d)} behind you" : $"{SwitchMetres(d)}, {Bearings.Clock(rel)}";
    }

    /// <summary>W: the next switch and how it's set, both ways' names where its stand has them.</summary>
    public async Task SwitchLineAsync()
    {
        if (Me() is not { } b) { Line("Not in a region yet."); return; }
        if (await TheSwitchAsync(b) is not { } s) { Line($"No switch within {Bearings.Metres(SwitchAsk)}."); return; }
        s = await ReadSwitchAsync(s, rails: true);
        var line = Sentence(s, WhereWords(Me() ?? b, s));
        if (s.Setting is { Mainline: var m, Branch: var br } && (m != null || br != null))
            line += $" Main line: {m ?? "not named"}; branch: {br ?? "not named"}.";
        if (!s.Stand) line += " No switch stand found to read or throw it.";
        SwitchLog($"asked: \"{line}\" of {PartWords(s)}{JunctionLogWords(s)}");
        Told(s);
        Line(line);
        ForHistory?.Invoke(line);
    }

    /// <summary>Shift+W, //switch flip, //switch main or branch: throws the switch W is about by touching its stand, as a
    /// person clicks it, then reads the stand until its setting changes (six seconds at most) and says the new one. To a
    /// setting it's already at, only says so.</summary>
    public async Task ThrowSwitchAsync(string? to = null)
    {
        // a switch being read as it comes up (every few seconds while one is ahead) is waited for, not a reason to refuse:
        // a throw is pressed with the switch coming up fast
        for (int i = 0; _switchBusy && i < 30; i++) await Task.Delay(100);
        if (_switchBusy) { Line("Still reading a switch: a moment."); return; }
        if (Me() is not { } b) { Line("Not in a region yet."); return; }
        if (await TheSwitchAsync(b) is not { } s) { Line($"No switch within {Bearings.Metres(SwitchAsk)} to throw."); return; }
        if (!s.Stand) { Line("That switch has no switch stand to throw it by."); return; }
        if (S.Region is not { } here || here.Handle != s.Region) { Line($"That switch is across the border, {WhereWords(b, s)}: throw it once you're in its region."); return; }
        _switchBusy = true;
        try
        {
            s = await ReadSwitchAsync(s);
            var was = s.Setting?.Status;
            if (to != null && was == to) { Written($"The switch is already {SettingWords(s)}."); return; }
            var guides = GuidesNear(here, s.Pos);
            var part = TouchPartOf(here, s.LocalId);
            if (part == null) { Line("The switch isn't here any more."); return; }
            long t0 = Environment.TickCount64;
            SwitchLog($"throwing {PartWords(s)}{(to != null ? " to " + to : "")}: was {was ?? "unknown"}; touching "
                + $"{(part.LocalID == s.LocalId ? "the root" : $"{part.Properties?.Name?.Trim() ?? "a part"} (local {part.LocalID})")}, "
                + $"{part.Scale.X:0.#}x{part.Scale.Y:0.#}x{part.Scale.Z:0.#}; guide rails within 60 m: {guides.Count}{JunctionLogWords(s)}");
            var r = await _ops.DoAsync("LLAgent", "requestTouch", new JsonObject { ["obj_uuid"] = part.ID.ToString() });
            if (PersonOps.Error(r) is { } e) { Line($"Couldn't throw the switch: {e}"); return; }
            var end = Environment.TickCount64 + 6000;
            RailSwitch now = s;
            bool guidesMoved = false;
            while (Environment.TickCount64 < end)
            {
                await Task.Delay(600);
                now = await ReadSwitchAsync(s);
                guidesMoved = await GuidesMovedAsync(here, guides);
                if (now.Setting?.Status is { } st && st != was) break;
                if (was == null && guidesMoved) break;
            }
            var status = now.Setting?.Status;
            SwitchLog($"after {Environment.TickCount64 - t0} ms: {status ?? "unknown"}"
                + $" (\"{(RegionOf(s) is { } rw && rw.Prims.TryGetValue(s.LocalId, out var sp) ? sp.Properties?.Description : null)}\")"
                + $"; guide rails {(guidesMoved ? "changed" : "unchanged")}: {GuidesWords(here, guides)}");
            Told(now);
            var pos = Me() is { } me ? me : b;
            float yaw = MathF.Atan2(s.Pos.Y - pos.Pos.Y, s.Pos.X - pos.Pos.X);
            if (status != null && status != was)
            {
                // the Pawpaw Wye report (SL feedback, 2025-12): a stand that changed while its guides didn't
                if (guides.Count >= 2 && !guidesMoved)
                {
                    await Task.Delay(1500);
                    guidesMoved = await GuidesMovedAsync(here, guides);
                }
                var line = $"Switch {SettingWords(now)}.";
                if (to != null && status != to) line += $" Not the {(to == "Main" ? "main line" : "branch")}, though.";
                if (guides.Count >= 2 && !guidesMoved) line += " Its guide rails didn't change, though: the stand may be out of order.";
                Cue("switch", CueSounds.Cue.SwitchThrown, line, pos, yaw);
            }
            else if (was == null && guidesMoved) Cue("switch", CueSounds.Cue.SwitchThrown, "Switch thrown: its guide rails changed, but its stand doesn't say which way.", pos, yaw);
            else Written(was == null ? "Touched the switch stand; no change seen." : $"The switch didn't change: still {SettingWords(now)}.");
        }
        finally { _switchBusy = false; }
    }

    void Written(string line) { Line(line); ForHistory?.Invoke(line); }

    /// <summary>//switch [flip|main|branch].</summary>
    public Task SwitchWordsAsync(string words)
    {
        var w = words.Trim().ToLowerInvariant();
        return w switch
        {
            "" or "next" or "state" => SwitchLineAsync(),
            "flip" or "throw" or "toggle" => ThrowSwitchAsync(),
            "main" or "main line" or "mainline" => ThrowSwitchAsync("Main"),
            "branch" => ThrowSwitchAsync("Branch"),
            _ => Say("//switch says the next switch; //switch flip throws it; //switch main or //switch branch sets it."),
        };

        Task Say(string text) { Line(text); return Task.CompletedTask; }
    }

    /// <summary>Said on asking or throwing: not said again as it comes up ahead; and once past, the log says which line
    /// the seat went on.</summary>
    void Told(RailSwitch s)
    {
        _switchToldAs[s.Id] = s.Setting?.Status ?? "";
        _switchToldWay[s.Id] = WayKey(s);
        _switchInWay[s.Id] = Environment.TickCount64;
        _switchPending.Remove(s.Id);
        if (s.Junction is { } j) _switchPassing[s.Id] = (j, _railRegion, Environment.TickCount64);
    }

    bool Phantom(Primitive p) => (S.State.PrimFlagsOf(p) & PrimFlags.Phantom) != 0;

    /// <summary>The guide rails near a stand (roots named Guide or Alternate Guide within 60 m: on the Linden railroad
    /// they're objects of their own reaching on past the stand), each with whether it's phantom and what it's named.</summary>
    Dictionary<uint, (bool Phantom, string Name)> GuidesNear(RegionWorld w, Vector3 at)
    {
        var o = new Dictionary<uint, (bool, string)>();
        var off = S.FrameOffset(w);
        foreach (var (id, p) in w.Prims)
            if (p.ParentID == 0 && p.Properties?.Name?.Trim() is { } n && (SwitchStand.IsGuide(n) || SwitchStand.IsAlternateGuide(n))
                && Vector3.Distance(new(p.Position.X + off.X, p.Position.Y + off.Y, p.Position.Z), at) < SwitchReach)
                o[id] = (Phantom(p), n);
        return o;
    }

    /// <summary>Whether the guide rails changed since: phantom flags come by themselves, names only asked again (the
    /// name is what says which rail is in use, the tester's ride of 2026-11-03 showed: trains run over phantom Guides).</summary>
    async Task<bool> GuidesMovedAsync(RegionWorld w, Dictionary<uint, (bool Phantom, string Name)> guides)
    {
        var prims = guides.Keys.Select(id => w.Prims.TryGetValue(id, out var p) ? p : null).OfType<Primitive>().ToList();
        try { await S.Objects.RenewAsync(w, prims, TimeSpan.FromSeconds(1), CancellationToken.None, w.Simulator); }
        catch (Exception) { }
        return guides.Any(g => w.Prims.TryGetValue(g.Key, out var gp) && (Phantom(gp) != g.Value.Phantom || gp.Properties?.Name?.Trim() is { } n && n != g.Value.Name));
    }

    string GuidesWords(RegionWorld w, Dictionary<uint, (bool Phantom, string Name)> guides) => string.Join("; ", guides.Take(16).Select(g =>
        w.Prims.TryGetValue(g.Key, out var p)
            ? $"{p.Position.X:0.0},{p.Position.Y:0.0} {g.Value.Name}{(g.Value.Phantom ? " phantom" : "")} > {p.Properties?.Name?.Trim()}{(Phantom(p) ? " phantom" : "")}"
            : $"local {g.Key} gone"));

    /// <summary>What a click on the switch goes to: its part that takes touches (the standard's large invisible prim over
    /// the stand; the largest, if several do), else the stand itself.</summary>
    Primitive? TouchPartOf(RegionWorld w, uint root)
    {
        if (!w.Prims.TryGetValue(root, out var r)) return null;
        Primitive? best = null;
        float size = 0;
        foreach (var (_, p) in w.Prims)
        {
            if ((p.LocalID != root && p.ParentID != root) || (S.State.PrimFlagsOf(p) & PrimFlags.Touch) == 0) continue;
            float v = p.Scale.X * p.Scale.Y * p.Scale.Z;
            if (best == null || v > size) { best = p; size = v; }
        }
        return best ?? r;
    }
}

/// <summary>An SLRR switch stand's name and description (the SL wiki's "Second Life Railroad/SLRR Switch"): the root
/// named "Switch Stand", its description "Identifier~Status~Heading Name~Mainline Name~Branch Name~messaging channel",
/// Status "Main" or "Branch".</summary>
public static class SwitchStand
{
    public sealed record Setting(string? Status, string? Ident, string? Mainline, string? Branch, int? Channel);

    public static bool IsStand(string name) =>
        (name.Contains("switch stand", StringComparison.OrdinalIgnoreCase) || name.Contains("switchstand", StringComparison.OrdinalIgnoreCase))
        // a stand's parts or kin named after it aren't stands: the standard's "Switch Stand Signal", or a junction kit's
        // "Switchstand Guide X2" (Switchstand Unlimited on the Marketplace)
        && !Part.IsMatch(name);

    static readonly System.Text.RegularExpressions.Regex Part = new(@"\b(guide|signal|sensor|light)s?\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsGuide(string name) => name.Equals("Guide", StringComparison.OrdinalIgnoreCase);

    public static bool IsAlternateGuide(string name) =>
        name.Equals("Alternate Guide", StringComparison.OrdinalIgnoreCase) || name.Equals("AlternateGuide", StringComparison.OrdinalIgnoreCase);

    /// <summary>The description's fields; Status null unless it says Main or Branch.</summary>
    public static Setting? Parse(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return null;
        var f = desc.Split('~').Select(x => x.Trim()).ToArray();
        string? At(int i) => i < f.Length && f[i].Length > 0 ? f[i] : null;
        var status = At(1)?.ToLowerInvariant() switch { "main" or "mainline" or "main line" => "Main", "branch" => "Branch", _ => null };
        if (status == null && f.Length < 2) return null;
        int? channel = int.TryParse(At(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch) ? ch : null;
        // some stands carry the bare words as the lines' names ("Main", "Branch"), which say nothing more
        string? Named(int i) => At(i) is { } n && !(n.Equals("main", StringComparison.OrdinalIgnoreCase) || n.Equals("mainline", StringComparison.OrdinalIgnoreCase)
            || n.Equals("main line", StringComparison.OrdinalIgnoreCase) || n.Equals("branch", StringComparison.OrdinalIgnoreCase)) ? n : null;
        return new Setting(status, At(0), Named(3), Named(4), channel);
    }
}
