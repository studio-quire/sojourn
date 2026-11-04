using System.Numerics;

namespace Sojourn.Access.Walk;

/// <summary>
/// The track a train is on, followed by its guide rails (docs/access-app.md > Walk mode > Rail switches): the rails
/// are single invisible prims 2-14 m long laid end to end, the one in use at a switch named "Guide" and the other
/// "Alternate Guide" (the SLRR standard). From the seat, the rail under it is found and followed along the way the
/// train goes, rail to rail, preferring Guides; where another line parts from it (a facing switch) or joins it (a
/// trailing one), both are followed a little way to see which side the other goes, and the rails' names say which
/// way it's set. What's unclear is said to be unclear: the person can't check, so a wrong answer is worse than none.
///
/// The name, not phantom, says which rail is in use: on the Linden railroad rails named Guide are often phantom and
/// trains run over them (the blind tester's ride of 2026-11-03, on 0.1.0.891), while Alternate Guides are phantom
/// nearly always (111 of 114). Pure geometry, no Second Life types, so it can be run on a log's rails offline.
/// Written by Claude Opus 5.5, 2026-11-03.
/// </summary>
public static class RailTrack
{
    /// <summary>A rail: its middle, its long axis (unit, the flat part normalised), half its length, and whether it's
    /// named Guide (in use) rather than Alternate Guide. Id is the caller's.</summary>
    public readonly record struct Rail(long Id, Vector3 Mid, Vector3 Axis, float Half, bool Guide, bool Phantom)
    {
        public Vector2 Flat => Vector2.Normalize(new Vector2(Axis.X, Axis.Y));
        public Vector3 End(float sign) => Mid + Axis * (Half * sign);
    }

    public enum Kind { Facing, Trailing }

    /// <summary>For: it takes the person on along the line they're on (facing), or is set for the line they come in on
    /// (trailing). Against: trailing, set for the other line. Unclear: both lines' rails (or neither's) are in use.</summary>
    public enum Verdict { For, Against, Unclear }

    public enum Side { None, Left, Right }

    /// <summary>A place where another line parts from or joins the track followed.</summary>
    public sealed record Junction(Kind Kind, float Along, Vector3 At, Verdict Verdict, Side OtherSide, float Separation,
        int OwnRail, int OtherRail, bool OwnGuide, bool OtherGuide, IReadOnlyList<Vector3> OwnLeg, IReadOnlyList<Vector3> OtherLeg);

    /// <summary>The track followed: points along it from the seat (Along[i] metres to Path[i]), the rails taken, the
    /// lines parting and joining, why it ends, and the way behind (for telling which side a joining line comes from).</summary>
    public sealed class Track
    {
        public required List<Vector3> Path { get; init; }
        public required List<float> Along { get; init; }
        public required List<int> Rails { get; init; }
        public required List<Junction> Junctions { get; init; }
        public required List<Vector3> Behind { get; init; }
        public required string Ends { get; set; }
        public float StartOffset { get; init; }          // the seat's distance from the start rail's line
        public float Length => Along.Count == 0 ? 0 : Along[^1];
        public Vector2 EndDir => Path.Count < 2 ? Vector2.Zero : Dir2(Path[^2], Path[^1]);

        /// <summary>How far along the track a point is (negative behind), if it's within <paramref name="maxAside"/>
        /// of the track's line; NaN if not.</summary>
        public float AlongOf(Vector3 p, float maxAside, out float aside)
        {
            aside = float.MaxValue;
            float best = float.NaN;
            var q = new Vector2(p.X, p.Y);
            for (int i = 1; i < Path.Count; i++)
            {
                var a = F(Path[i - 1]);
                var b = F(Path[i]);
                float d = SegDist(q, a, b, out float t);
                if (d < aside) { aside = d; best = Along[i - 1] + t * Vector2.Distance(a, b); }
            }
            // behind the seat: along the way behind, negative
            if (Behind.Count > 0)
            {
                float back = 0;
                var prev = Path.Count > 0 ? F(Path[0]) : F(Behind[0]);
                foreach (var pt in Behind)
                {
                    var b = F(pt);
                    float len = Vector2.Distance(prev, b);
                    if (len > 0.01f)
                    {
                        float d = SegDist(q, prev, b, out float t);
                        if (d < aside) { aside = d; best = -(back + t * len); }
                    }
                    back += len;
                    prev = b;
                }
            }
            return aside <= maxAside ? best : float.NaN;
        }

        /// <summary>The point at a distance along the track (its last point past the end).</summary>
        public Vector3 At(float along)
        {
            for (int i = 1; i < Path.Count; i++)
                if (Along[i] >= along)
                {
                    float seg = Along[i] - Along[i - 1];
                    return seg <= 0 ? Path[i] : Vector3.Lerp(Path[i - 1], Path[i], (along - Along[i - 1]) / seg);
                }
            return Path.Count > 0 ? Path[^1] : default;
        }
    }

    // how near and how aligned rails must be to count as laid end to end (times the slack: a log's places are rounded
    // to the metre, so looking at one offline wants more)
    const float Joint = 1.0f;          // an end this near the next rail goes on into it
    const float Same = 0.6f;           // an end this near the next rail's end: laid end to end, the same line for sure
    const float Beside = 0.9f;         // a rail's end this near a rail's span parts from or joins it there
    const float Aligned = 0.866f;      // cos 30 degrees: the most a rail turns from the one before (curves turn 5-15)
    const float Rise = 2.5f;           // the most the next rail's end is above or below
    const float Leg = 50f;             // each line followed this far to see where it goes
    const float Apart = 2.5f;          // lines this far apart within Leg are two lines; nearer, one (overlapping rails)

    /// <summary>The track from the seat along <paramref name="dir"/> (unit, flat) for <paramref name="range"/> metres,
    /// or null when no rail is under the seat (or two lines' rails are about as near: which it's on isn't clear).</summary>
    public static Track? Follow(IReadOnlyList<Rail> rails, Vector3 seat, Vector2 dir, float range, float slack = 1f)
    {
        float joint = Joint * slack, same = Same * slack, beside = Beside * slack;
        int start = StartRail(rails, seat, dir, out float offset);
        if (start < 0) return null;
        var r0 = rails[start];
        float sign = Vector2.Dot(r0.Flat, dir) >= 0 ? 1 : -1;
        var t = r0.Flat * sign;
        // where the seat is on it
        var entry = Project(r0, seat);
        var track = new Track
        {
            Path = new() { entry }, Along = new() { 0 }, Rails = new(), Junctions = new(), Behind = new(), Ends = "",
            StartOffset = offset,
        };
        var used = new HashSet<int> { start };
        var legs = new HashSet<int>();     // rails of the other lines found, not to be found again
        // the way behind, for joining lines' sides
        track.Behind.AddRange(Probe(rails, start, -sign, Vector2.Distance(F(entry), F(r0.End(sign))), Leg, used, joint, out var behindRails));
        foreach (var i in behindRails) legs.Add(i);

        int cur = start;
        float along = 0;
        int? onAlternateFrom = r0.Guide ? null : 0;   // the track's rails have been alternates since this many metres (a switch set against it?)
        for (int steps = 0; steps < 400; steps++)
        {
            var r = rails[cur];
            track.Rails.Add(cur);
            var exit = r.End(sign);
            float span = Vector2.Distance(F(entry), F(exit));
            float alongExit = along + span;

            // what goes on from this rail's span ahead, and what joins it from behind
            var ahead = new List<int>();
            var joining = new List<(int I, float Cs)>();
            for (int i = 0; i < rails.Count; i++)
            {
                if (i == cur || used.Contains(i) || legs.Contains(i)) continue;
                var c = rails[i];
                float cos = Vector2.Dot(c.Flat, t);
                if (MathF.Abs(cos) < Aligned) continue;
                float cs = cos >= 0 ? 1 : -1;
                var near = c.End(-cs);
                var far = c.End(cs);
                float rise = Rise + MathF.Abs(r.Axis.Z) * r.Half * 2;
                if (MathF.Abs(near.Z - exit.Z) > rise && MathF.Abs(far.Z - exit.Z) > rise) continue;
                bool nearOn = Vector2.Distance(F(near), F(exit)) <= joint || SegDist(F(near), F(entry), F(exit), out _) <= beside;
                bool passes = SegDist(F(exit), F(near), F(far), out _) <= joint;
                if (Vector2.Dot(F(far) - F(exit), t) >= 1f && (nearOn || passes))
                {
                    // laid end to end with this one: the line goes on. Otherwise it may be another line's rail running in
                    // beside this one (a switch's rails overlap before the points): one whose way back parts from this
                    // track's joins it
                    if (Vector2.Distance(F(near), F(exit)) > same && ComesFromElsewhere(i, cs)) joining.Add((i, cs));
                    else ahead.Add(i);
                }
                else if (SegDist(F(far), F(entry), F(exit), out _) <= beside && Vector2.Dot(F(far) - F(entry), t) > 0.5f)
                    joining.Add((i, cs));
            }
            foreach (var (i, cs) in joining) if (!legs.Contains(i)) TryJoin(i, cs);
            ahead.RemoveAll(legs.Contains);

            if (ahead.Count == 0)
            {
                track.Path.Add(exit);
                track.Along.Add(alongExit);
                track.Ends = onAlternateFrom is { } af ? $"its rails end {alongExit - af:0} m into alternates" : "no rail goes on";
                return track;
            }

            // the lines ahead: candidates grouped by where they go (overlapping rails are one line), each line's rail
            // the one nearest where this one ends, in use if any of them is a Guide
            var groups = new List<(int Best, List<Vector3> Way, List<int> Members, bool Guide)>();
            foreach (var i in ahead.OrderBy(i => NearEndDist(rails[i], exit, t)))
            {
                float cs = Vector2.Dot(rails[i].Flat, t) >= 0 ? 1 : -1;
                var way = new List<Vector3> { rails[i].End(-cs) };
                way.AddRange(Probe(rails, i, cs, 0, Leg, used, joint, out _));
                int g = groups.FindIndex(x => Separation(x.Way, way, t, out _, out _) < Apart);
                if (g < 0) groups.Add((i, way, new List<int> { i }, rails[i].Guide));
                else
                {
                    var x = groups[g];
                    x.Members.Add(i);
                    x.Guide |= rails[i].Guide;
                    groups[g] = x;
                }
            }
            var guides = groups.Where(x => x.Guide).ToList();
            (int Best, List<Vector3> Way, List<int> Members, bool Guide) own;
            if (guides.Count == 1) own = guides[0];
            else if (guides.Count == 0 && groups.Count == 1) own = groups[0];
            else
            {
                // two lines in use, or two alternates and no guide: which way it goes isn't clear
                var (ga, gb) = (groups[0], groups[1]);
                float sep = Separation(ga.Way, gb.Way, t, out _, out _);
                var at = PartsAt(rails, gb.Best, entry, t, along, out float jAlong);
                track.Junctions.Add(new Junction(Kind.Facing, jAlong, at, Verdict.Unclear, Side.None, sep, cur, gb.Best,
                    ga.Guide, gb.Guide, ga.Way, gb.Way));
                track.Path.Add(exit);
                track.Along.Add(alongExit);
                track.Ends = guides.Count >= 2 ? "two lines' rails in use ahead" : "only alternates ahead, two ways";
                return track;
            }
            // on its Guide where one goes on from here too (a switch's rails overlap for a while: an alternate beside the
            // Guide would take the track down the other line where they part)
            int next = own.Members.Where(m => rails[m].Guide && NearEndDist(rails[m], exit, t) <= joint).DefaultIfEmpty(own.Best).First();
            foreach (var m in own.Members) if (m != next) used.Add(m);   // overlapping rails of the same line
            foreach (var other in groups)
            {
                if (other.Best == own.Best) continue;
                foreach (var m in other.Members) legs.Add(m);
                MarkLeg(other.Way, own.Way);
                float sep = Separation(own.Way, other.Way, t, out var side, out _);
                var at = PartsAt(rails, other.Best, entry, t, along, out float jAlong);
                if (jAlong < -0.5f) continue;   // parted behind the seat: passed already
                // the line followed is the Guide's; the other's first rail an alternate: it takes the person on along
                // this line. Both alternates (a switch set against both?) or both guides: unclear.
                var verdict = own.Guide && !other.Guide ? Verdict.For : Verdict.Unclear;
                track.Junctions.Add(new Junction(Kind.Facing, jAlong, at, verdict, sep >= Apart ? side : Side.None, sep, own.Best, other.Best,
                    own.Guide, other.Guide, own.Way, other.Way));
            }

            // on into the next rail: from where it meets this one
            var n = rails[next];
            float ns = Vector2.Dot(n.Flat, t) >= 0 ? 1 : -1;
            var nNear = n.End(-ns);
            // the next rail starts at its near end, or where this one ends if they overlap
            float overlap = Vector2.Dot(F(exit) - F(nNear), n.Flat * ns);
            var nEntry = overlap > 0 ? Project(n, exit) : nNear;
            track.Path.Add(exit);
            track.Along.Add(alongExit);
            along = alongExit + (overlap > 0 ? 0 : Vector2.Distance(F(exit), F(nEntry)));
            if (overlap <= 0) { track.Path.Add(nEntry); track.Along.Add(along); }
            if (!own.Guide && onAlternateFrom == null) onAlternateFrom = (int)along;
            else if (own.Guide) onAlternateFrom = null;
            used.Add(next);
            cur = next;
            sign = ns;
            t = n.Flat * sign;
            entry = nEntry;
            if (along >= range)
            {
                track.Path.Add(n.End(sign));
                track.Along.Add(along + Vector2.Distance(F(nEntry), F(n.End(sign))));
                track.Rails.Add(cur);
                track.Ends = "far enough";
                return track;
            }
        }
        track.Ends = "too many rails";
        return track;

        void MarkLeg(List<Vector3> way, List<Vector3> mine)
        {
            // the other line's rails beyond its first would be found again on the next rails' spans: not new lines
            // (but not those on this track's own way too, where the two still overlap)
            for (int i = 0; i < rails.Count; i++)
                if (!used.Contains(i) && !legs.Contains(i) && OnWay(rails[i], way) && !OnWay(rails[i], mine)) legs.Add(i);
        }

        bool ComesFromElsewhere(int i, float cs)
        {
            // followed back, the rail behind it is this track's own (laid end to end, a little apart): it goes on
            var c = rails[i];
            var back = new List<Vector3> { c.End(cs) };
            back.AddRange(Probe(rails, i, -cs, 0, Leg, legs, joint, out var behind));
            if (behind.Count > 0 && used.Contains(behind[0])) return false;
            return Separation(WayBack(track, c.End(cs)), back, -t, out _, out _) >= Apart;
        }

        void TryJoin(int i, float cs)
        {
            var c = rails[i];
            var way = new List<Vector3> { c.End(cs) };
            way.AddRange(Probe(rails, i, -cs, 0, Leg, used, joint, out var probed));
            // the way this track came, back from where the line joins
            var mine = WayBack(track, c.End(cs));
            float sep = Separation(mine, way, -t, out var side, out _);
            if (sep < Apart) return;   // the same line (overlapping rails), not another joining it
            // where the two meet: the joining line's rails go on beside this track's until they reach rails of its
            // own (the switch's rails overlap for a while before the points)
            var ownAhead = new HashSet<int> { cur };
            Probe(rails, cur, sign, 0, 40, used, joint, out var ownRails);
            foreach (var o in ownRails) ownAhead.Add(o);
            var fwd = Probe(rails, i, cs, 0, 40, used, joint, out var fwdRails, stop: ownAhead.Contains);
            var meet = fwd.Count > 0 ? fwd[^1] : c.End(cs);
            int last = fwdRails.Count > 0 ? fwdRails[^1] : i;
            legs.Add(i);
            foreach (var p in probed) legs.Add(p);
            foreach (var p in fwdRails) legs.Add(p);
            MarkLeg(way, mine);
            float jAlong = along + Math.Max(0, Vector2.Dot(F(meet) - F(entry), t));
            // the side as the person sees it going forward: Separation measured looking back, so mirrored
            var seen = side == Side.Left ? Side.Right : side == Side.Right ? Side.Left : Side.None;
            // their own line's rails up to where they meet: Guides (set for them, the other's an alternate) or an
            // alternate among them (set against them, the other's a Guide)
            bool ownGuide = onAlternateFrom == null && ownAhead.Where(o => Vector2.Dot(F(rails[o].Mid) - F(meet), t) < 0).All(o => rails[o].Guide);
            bool otherGuide = rails[last].Guide;
            var verdict = ownGuide && !otherGuide ? Verdict.For : !ownGuide && otherGuide ? Verdict.Against : Verdict.Unclear;
            track.Junctions.Add(new Junction(Kind.Trailing, jAlong, meet, verdict, seen, sep, cur, last, ownGuide, otherGuide, mine, way));
        }
    }

    /// <summary>The rail under the seat: within 2.5 m of it flat, 8 m up or down, running within 35 degrees of the way it
    /// goes; Guides first. -1 if none, or if another line's rail (not one overlapping it) is about as near.</summary>
    static int StartRail(IReadOnlyList<Rail> rails, Vector3 seat, Vector2 dir, out float offset)
    {
        offset = float.MaxValue;
        var s = F(seat);
        var near = new List<(int I, float D)>();
        for (int i = 0; i < rails.Count; i++)
        {
            var r = rails[i];
            if (MathF.Abs(Vector2.Dot(r.Flat, dir)) < 0.82f || MathF.Abs(r.Mid.Z - seat.Z) > 8) continue;
            float d = SegDist(s, F(r.End(-1)), F(r.End(1)), out _);
            if (d <= 2.5f) near.Add((i, d));
        }
        if (near.Count == 0) return -1;
        var pick = near.Where(x => rails[x.I].Guide).DefaultIfEmpty(near.MinBy(x => x.D)).MinBy(x => x.D);
        // another line about as near: its rail neither overlapping this one's line nor further than a metre beyond
        foreach (var x in near)
        {
            if (x.I == pick.I || x.D > pick.D + 1f) continue;
            var p = rails[pick.I];
            var o = rails[x.I];
            float off = MathF.Min(LineDist(F(o.End(-1)), p), LineDist(F(o.End(1)), p));
            if (off > 1.2f && LineDist(F(o.Mid), p) > 1.2f) return -1;
        }
        offset = pick.D;
        return pick.I;
    }

    /// <summary>A line followed from a rail along sign for up to len metres (after skip metres of the first rail), or
    /// until the next rail is one to stop before: the points it passes (each rail's far end), preferring rails named as
    /// the one before and then the most aligned.</summary>
    static List<Vector3> Probe(IReadOnlyList<Rail> rails, int from, float sign, float skip, float len, HashSet<int> avoid, float joint,
        out List<int> taken, Func<int, bool>? stop = null)
    {
        var pts = new List<Vector3>();
        taken = new List<int>();
        int cur = from;
        float went = -skip;
        var seen = new HashSet<int> { from };
        for (int steps = 0; steps < 40; steps++)
        {
            var r = rails[cur];
            var exit = r.End(sign);
            pts.Add(exit);
            went += r.Half * 2;
            if (went >= len) break;
            var t = r.Flat * sign;
            int best = -1;
            float bestScore = float.MaxValue;
            for (int i = 0; i < rails.Count; i++)
            {
                if (seen.Contains(i) || avoid.Contains(i)) continue;
                var c = rails[i];
                float cos = Vector2.Dot(c.Flat, t);
                if (MathF.Abs(cos) < Aligned) continue;
                float cs = cos >= 0 ? 1 : -1;
                var near = c.End(-cs);
                var far = c.End(cs);
                if (MathF.Abs(near.Z - exit.Z) > Rise) continue;
                float d = SegDist(F(exit), F(near), F(far), out _);
                if (d > joint || Vector2.Dot(F(far) - F(exit), t) < 1f) continue;
                float score = d + (1 - MathF.Abs(cos)) * 20 + (c.Guide == r.Guide ? 0 : 1f);
                if (score < bestScore) { bestScore = score; best = i; }
            }
            if (best < 0 || stop?.Invoke(best) == true) break;
            seen.Add(best);
            taken.Add(best);
            sign = Vector2.Dot(rails[best].Flat, t) >= 0 ? 1 : -1;
            cur = best;
        }
        return pts;
    }

    /// <summary>How far apart two lines get, followed from about the same place along about t: the greatest distance
    /// of the second's points from the first, which side of the first it's on there (looking along t), and how far
    /// along. Lines that don't reach far enough to tell count as near.</summary>
    static float Separation(List<Vector3> a, List<Vector3> b, Vector2 t, out Side side, out float at)
    {
        side = Side.None;
        at = 0;
        float best = 0;
        if (a.Count == 0 || b.Count == 0) return 0;
        var origin = F(b[0]);
        foreach (var p in b)
        {
            var q = F(p);
            float along = Vector2.Dot(q - origin, t);
            // only as far as the first line reaches: beyond it, distance says nothing
            float reach = a.Max(x => Vector2.Dot(F(x) - origin, t));
            if (along > reach + 1) break;
            float d = float.MaxValue;
            Vector2 dirAt = t;
            Vector2 nearest = default;
            for (int i = 0; i < a.Count; i++)
            {
                var s0 = i == 0 ? F(a[0]) : F(a[i - 1]);
                var s1 = F(a[i]);
                float dd = SegDist(q, s0, s1, out float tt);
                if (dd < d)
                {
                    d = dd;
                    nearest = Vector2.Lerp(s0, s1, tt);
                    if (Vector2.Distance(s0, s1) > 0.1f) dirAt = Vector2.Normalize(s1 - s0);
                }
            }
            if (d > best)
            {
                best = d;
                at = along;
                float cross = dirAt.X * (q.Y - nearest.Y) - dirAt.Y * (q.X - nearest.X);
                side = cross > 0 ? Side.Left : Side.Right;
            }
        }
        return best;
    }

    /// <summary>The way the track came, back from a point on it: the path up to there, reversed, then the way behind
    /// the seat.</summary>
    static List<Vector3> WayBack(Track track, Vector3 from)
    {
        var o = new List<Vector3> { from };
        var q = F(from);
        // the path's points before the one nearest from
        int upTo = 0;
        float bestD = float.MaxValue;
        for (int i = 1; i < track.Path.Count; i++)
        {
            float d = SegDist(q, F(track.Path[i - 1]), F(track.Path[i]), out _);
            if (d < bestD) { bestD = d; upTo = i - 1; }
        }
        for (int i = upTo; i >= 0; i--) o.Add(track.Path[i]);
        o.AddRange(track.Behind);
        return o;
    }

    static bool OnWay(Rail r, List<Vector3> way)
    {
        if (way.Count < 2) return false;
        var a = F(r.End(-1));
        var b = F(r.End(1));
        float da = float.MaxValue, db = float.MaxValue;
        for (int i = 1; i < way.Count; i++)
        {
            da = MathF.Min(da, SegDist(a, F(way[i - 1]), F(way[i]), out _));
            db = MathF.Min(db, SegDist(b, F(way[i - 1]), F(way[i]), out _));
        }
        return da < 0.8f && db < 0.8f;
    }

    /// <summary>Where a line parts from the track: the near end of its first rail, put on the track's line.</summary>
    static Vector3 PartsAt(IReadOnlyList<Rail> rails, int other, Vector3 entry, Vector2 t, float along, out float jAlong)
    {
        var o = rails[other];
        float cs = Vector2.Dot(o.Flat, t) >= 0 ? 1 : -1;
        var near = o.End(-cs);
        float d = Vector2.Dot(F(near) - F(entry), t);
        // on a later rail, a line parting where it began is at its start; on the first, before the seat is behind
        jAlong = along + (along > 0 ? Math.Max(0, d) : d);
        return near;
    }

    static float NearEndDist(Rail r, Vector3 exit, Vector2 t)
    {
        float cs = Vector2.Dot(r.Flat, t) >= 0 ? 1 : -1;
        return Vector2.Distance(F(r.End(-cs)), F(exit));
    }

    static Vector3 Project(Rail r, Vector3 p)
    {
        float k = Math.Clamp(Vector2.Dot(F(p) - F(r.Mid), r.Flat) / Math.Max(1e-3f, new Vector2(r.Axis.X, r.Axis.Y).Length()), -r.Half, r.Half);
        return r.Mid + r.Axis * k;
    }

    static float LineDist(Vector2 p, Rail r)
    {
        var d = p - F(r.Mid);
        var f = r.Flat;
        return MathF.Abs(d.X * f.Y - d.Y * f.X);
    }

    static Vector2 F(Vector3 v) => new(v.X, v.Y);

    static Vector2 Dir2(Vector3 a, Vector3 b)
    {
        var d = F(b) - F(a);
        return d.LengthSquared() < 1e-6f ? Vector2.Zero : Vector2.Normalize(d);
    }

    static float SegDist(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        t = l2 < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }
}
