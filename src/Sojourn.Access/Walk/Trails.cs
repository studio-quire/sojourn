using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>
/// Where the people around her have walked lately, as the region sent their positions: the way a person following
/// someone by eye goes when the collision model has no way to where they are (its stairs aren't in the model: the blind
/// tester's partner up steps at a place on Second Life, 2026-11-19, where every follow said "no way up from here" and
/// stopped). Following (Commands.FollowLoopAsync) and a walk to a person (Commands.RouteAsync) take it where the planner
/// finds their floor out of reach or no way at all.
/// A sample is kept each time someone's position has moved a quarter metre since the last (polled five times a second:
/// the world model has their position as last sent, never carried on), with their feet's height and velocity. Two in a
/// row are joined, walkable from one to the other, when they're near (Link) or the first's velocity carried on for the
/// time between lands near the second (a steady straight walk between sparse updates, OpenSim's way, which sends a
/// walking avatar's position when its velocity changes); not when they were seated, flying, jumping or teleported, moved
/// faster than a run, were falling fast, or climbed or dropped more steeply than stairs over the run so far (Steep: a
/// lift ridden standing, a slow descent flying, which a look at a time can't tell from a step), so the line between two
/// samples never stands for a way nobody walked: a corner cut where updates were lost could cross an edge they walked
/// round. Flying and jumping are known only by the default animations, and an animation overrider plays its own, so
/// the steepness over the run and how far down a way goes (MaxDown) stand in for them. Positions in her region's frame;
/// a region change of hers starts afresh. Claude Opus 5.5, 2026-11-19.
/// </summary>
public sealed class Trails
{
    readonly AgentSession _s;
    readonly object _lock = new();
    readonly Dictionary<UUID, Person> _people = new();
    ulong _region;
    bool _failed;

    /// <summary>Where someone was (their middle and their feet's height), when (TickCount64), their velocity then,
    /// whether the way from the sample before is one they walked, and whether the region sent a foot plane with it (the
    /// surface it has them standing on: in the log only, for whether Second Life's tells standing from in the air).</summary>
    public readonly record struct Sample(Vector3 Pos, float Feet, long At, Vector3 Vel, bool Joined, bool Plane);

    sealed class Person
    {
        public readonly List<Sample> Samples = new();
        public bool Broken = true;          // the next sample doesn't join the last
        public string? BrokenBy;            // the last break, for the log: "a seat", "flying", ...
        public Vector3 BrokenAt;
        public Vector3 Now;                 // where they are, as last looked at (sampled or not)
        public long Seen;
    }

    const int Period = 200;
    /// <summary>How far around her people's ways are kept; how long (a way's older runs: the one someone is on now is
    /// kept whatever its age); how many samples a person at most.</summary>
    const float Around = 96f;
    const long Keep = 180_000;
    const int Most = 1200;
    /// <summary>A new sample once someone has moved this far (across, or up or down).</summary>
    const float Moved = 0.25f;
    /// <summary>Samples this near are joined whatever their velocity said (a corner between them cut by at most a few
    /// tenths); further apart only when the first's velocity explains the second (Lead and Slack).</summary>
    const float Link = 2f;
    const float Slack = 0.5f, SlackShare = 0.15f;
    /// <summary>Faster than this between two samples is a teleport (or the region's word lost for a while), not walking
    /// or running (5 m/s or so).</summary>
    const float Teleport = 9f;
    /// <summary>A drop steeper than stairs of more than this between two samples is a fall, not walked after; so is
    /// coming down faster than Plummet (a fall of 2.5 m or so; nobody walks or runs down stairs at that).</summary>
    const float Fall = 1.5f, Plummet = 7f;
    /// <summary>Steep: up or down more than this from a sample of the run, over less ground across than that height
    /// (stairs rise one for one at the steepest), looked back over at most SteepBack of ground.</summary>
    const float Steeper = 1.5f, SteepBack = 12f;

    public Trails(AgentSession s)
    {
        _s = s;
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(Period));
        while (await tick.WaitForNextTickAsync().ConfigureAwait(false))
        {
            try { Look(); }
            catch (Exception e)
            {
                // (a sample missed: the next tick's; said once in the session's log)
                if (_failed) continue;
                _failed = true;
                var line = $"trails: a look failed, {e.GetType().Name}: {e.Message} ({e.StackTrace?.Split('\n')[0].Trim()}); said once";
                WalkMode.Log(line);
                _s.Log.LogWarning("{Line}", line);
            }
        }
    }

    void Look()
    {
        if (_s.Client is not { Network.CurrentSim: { } sim } c || _s.Region is not { } w) return;
        long now = Environment.TickCount64;
        var me = c.Self.SimPosition;
        lock (_lock)
        {
            if (sim.Handle != _region) { _people.Clear(); _region = sim.Handle; }
            foreach (var (_, av) in w.Avatars)
            {
                if (av.ID == c.Self.AgentID || av.ID == UUID.Zero) continue;
                if (!w.TryRegionPosition(av, out var lp)) continue;
                var pos = new Vector3(lp.X, lp.Y, lp.Z);
                if (Vector2.Distance(new(pos.X, pos.Y), new(me.X, me.Y)) > Around) continue;
                if (!_people.TryGetValue(av.ID, out var p)) _people[av.ID] = p = new Person();
                (p.Seen, p.Now) = (now, pos);
                float half = av.Scale.Z is > 1f and < 3.5f ? av.Scale.Z / 2 : 0.95f;
                string? off = av.ParentID != 0 ? "a seat" : Off(av);
                if (off != null)
                {
                    if (!p.Broken || p.BrokenBy != off) (p.BrokenBy, p.BrokenAt) = (off, pos);
                    p.Broken = true;
                    continue;
                }
                var vel = new Vector3(av.Velocity.X, av.Velocity.Y, av.Velocity.Z);
                bool joined = false;
                if (p.Samples.Count > 0)
                {
                    var last = p.Samples[^1];
                    var d = pos - last.Pos;
                    if (new Vector2(d.X, d.Y).Length() < Moved && MathF.Abs(d.Z) < Moved) continue;
                    if (!p.Broken)
                    {
                        var why = Unwalked(last, pos, vel, now) ?? Steep(p.Samples, pos);
                        joined = why == null;
                        if (why != null) (p.BrokenBy, p.BrokenAt) = (why, pos);
                    }
                }
                var fp = av.CollisionPlane;
                p.Samples.Add(new Sample(pos, pos.Z - half, now, vel, joined, fp.X * fp.X + fp.Y * fp.Y + fp.Z * fp.Z > 0.25f));
                p.Broken = false;
                if (p.Samples.Count > Most) p.Samples.RemoveRange(0, p.Samples.Count - Most);
            }
            // (the old let go: samples past Keep before the run someone is on now, which stays whatever its age, as
            // someone who walked up and has stood there since still has the way there; people not seen for as long)
            foreach (var (id, p) in _people.ToList())
            {
                if (now - p.Seen > Keep) { _people.Remove(id); continue; }
                int run = RunStart(p.Samples), old = 0;
                while (old < run && now - p.Samples[old].At > Keep) old++;
                if (old > 0) p.Samples.RemoveRange(0, old);
            }
        }
    }

    /// <summary>Where the last run of joined samples starts (0 with none).</summary>
    static int RunStart(List<Sample> samples)
    {
        int s = samples.Count - 1;
        while (s > 0 && samples[s].Joined) s--;
        return Math.Max(s, 0);
    }

    static readonly UUID[] Flying = { Animations.FLY, Animations.FLYSLOW, Animations.HOVER, Animations.HOVER_UP, Animations.HOVER_DOWN };
    static readonly UUID[] Jumping = { Animations.JUMP, Animations.PRE_JUMP };

    /// <summary>Why someone's position now isn't one they walked to: flying, jumping, seated on the ground; or null.</summary>
    static string? Off(Avatar av)
    {
        if (av.Animations is not { } anims) return null;
        lock (av)
            foreach (var a in anims)
            {
                if (Array.IndexOf(Flying, a.AnimationID) >= 0) return "flying";
                if (Array.IndexOf(Jumping, a.AnimationID) >= 0) return "a jump";
                if (a.AnimationID == Animations.SIT_GROUND || a.AnimationID == Animations.SIT_GROUND_staticRAINED) return "a seat";
            }
        return null;
    }

    /// <summary>Why the way from one sample to the next isn't one they walked (null: it is). <paramref name="vel"/>: the
    /// velocity the region had for them at the second.</summary>
    static string? Unwalked(Sample a, Vector3 b, Vector3 vel, long now)
    {
        float dt = Math.Max(0.05f, (now - a.At) / 1000f);
        var d = b - a.Pos;
        float flat = new Vector2(d.X, d.Y).Length();
        if (flat / dt > Teleport) return "a teleport";
        // (a lift or a sit-and-stand somewhere else rising fast between two looks; falling 1.5 m between two is a fall
        // of 3 m or so, off a skybox's edge as likely as a deck's, not to be walked after; slower ones: Steep)
        if (d.Z > flat + 0.6f) return "a rise";
        if (-d.Z > Fall && -d.Z > flat + 0.6f || vel.Z < -Plummet) return "a fall";
        if (flat <= Link) return null;
        var carried = a.Pos + a.Vel * dt;
        return Vector2.Distance(new(carried.X, carried.Y), new(b.X, b.Y)) <= Slack + SlackShare * flat ? null : "a gap in what the region sent";
    }

    /// <summary>Whether the run so far and a new position make a climb or a drop no stairs make: more than Steeper up or
    /// down from one of the run's samples over less ground across (along the samples, so a spiral stair's turns count)
    /// than that height. Each look's step is too small to tell (a quarter metre or so), however slowly someone rides a
    /// lift standing or flies down; null: not.</summary>
    static string? Steep(List<Sample> samples, Vector3 b)
    {
        float across = 0;
        var next = b;
        for (int k = samples.Count - 1; k >= 0; k--)
        {
            var s = samples[k];
            across += Vector2.Distance(new(next.X, next.Y), new(s.Pos.X, s.Pos.Y));
            float up = b.Z - s.Pos.Z;
            if (MathF.Abs(up) > Steeper && MathF.Abs(up) > across) return up > 0 ? "a climb steeper than stairs" : "a drop steeper than stairs";
            if (!s.Joined || across > SteepBack) break;
            next = s.Pos;
        }
        return null;
    }

    /// <summary>Their way to where they are now, from a sample on her level: its points from there to their last,
    /// simplified to its corners (Tolerance). Join: that sample; Off: how far it is from her across. Length: along the
    /// points from there; Samples: how many; Planes: how many had a foot plane.</summary>
    public sealed record Way(List<Vector3> Points, Vector3 Join, float Off, float Length, int Samples, int Planes);

    /// <summary>How far the way's points may stray from the samples between them (corners kept).</summary>
    const float Tolerance = 0.15f;
    /// <summary>Feet within Level of hers is her level, within OnFloor her floor (a step either way).</summary>
    const float Level = 1f, OnFloor = 0.4f;
    /// <summary>Their last step this far across from where they are now, or a metre up or down, isn't where they are.</summary>
    const float EndOff = 2f;
    /// <summary>A way going further down than this from her feet isn't offered: walked down stairs the model lacks, or
    /// flown down slowly where an animation overrider hid the flying (Steep catches only a descent steeper than stairs),
    /// it would be a fall of that much if it was the second.</summary>
    const float MaxDown = 5f;

    /// <summary>The ways someone walked to where they are from her level, best first: from where they last left her floor
    /// (a step either way) within <paramref name="within"/> metres across, so she goes straight to where they climbed
    /// rather than round all they walked before it; then from where it passes nearest her on her level (of samples about
    /// as near, the later). None while they're flying or mid-jump, or where they are isn't where their last steps
    /// ended, as their way ends where they left the ground; and why not, or why one was left out, for the log.</summary>
    public (List<Way> Ways, string Why) Find(UUID who, Vector3 her, float feet, float within)
    {
        List<Sample> run;
        string? brokenBy;
        Vector3 brokenAt, now;
        bool broken;
        lock (_lock)
        {
            if (!_people.TryGetValue(who, out var p) || p.Samples.Count == 0) return (new(), "none of their steps seen lately");
            int s = RunStart(p.Samples);
            run = p.Samples.GetRange(s, p.Samples.Count - s);
            // (the last break is where this run starts, or one going on now: seated, flying)
            (brokenBy, brokenAt, now, broken) = (p.BrokenBy, p.BrokenAt, p.Now, p.Broken);
        }
        string Since() => brokenBy != null ? FormattableString.Invariant($" since {brokenBy} at {brokenAt.X:0.0},{brokenAt.Y:0.0},{brokenAt.Z:0.0}") : "";
        // (following someone who took off had her taken to where they left the ground and held there, up to three
        // minutes, as the run ending there was all there was)
        if (broken && brokenBy is "flying" or "a jump")
            return (new(), FormattableString.Invariant($"{brokenBy} since {brokenAt.X:0.0},{brokenAt.Y:0.0},{brokenAt.Z:0.0}, where their steps end"));
        var end = run[^1].Pos;
        float apart = Vector2.Distance(new(end.X, end.Y), new(now.X, now.Y)), above = now.Z - end.Z;
        if (apart > EndOff || MathF.Abs(above) > 1f)
            return (new(), FormattableString.Invariant($"their steps{Since()} ending {apart:0.0} m across and {above:+0.0;-0.0} m up from where they are"));
        int left = -1, nearest = -1;
        float best = float.MaxValue;
        for (int i = 0; i < run.Count; i++)
        {
            float up = MathF.Abs(run[i].Feet - feet);
            if (up > Level) continue;
            float off = Vector2.Distance(new(run[i].Pos.X, run[i].Pos.Y), new(her.X, her.Y));
            if (up <= OnFloor && off <= within) left = i;
            // (as near, or nearly: the later, further along their way)
            if (off <= best + 0.3f) { if (off < best) best = off; nearest = i; }
        }
        if (nearest < 0) return (new(), $"none of their {run.Count} steps{Since()} on her level");
        if (best > within) return (new(), FormattableString.Invariant($"the nearest of their steps{Since()} on her level {best:0.0} m from her"));
        var ways = new List<Way>();
        var why = "";
        foreach (int join in left >= 0 && left != nearest ? new[] { left, nearest } : new[] { nearest })
        {
            var from = run.Skip(join).ToList();
            float low = from.Min(x => x.Feet);
            if (feet - low > MaxDown)
            {
                why = FormattableString.Invariant($"their way from {run[join].Pos.X:0.0},{run[join].Pos.Y:0.0},{run[join].Pos.Z:0.0} going {feet - low:0.0} m down");
                continue;
            }
            var pts = Simplify(from.Select(x => x.Pos).ToList());
            float len = 0;
            for (int i = 1; i < pts.Count; i++) len += Vector3.Distance(pts[i - 1], pts[i]);
            float joinOff = Vector2.Distance(new(run[join].Pos.X, run[join].Pos.Y), new(her.X, her.Y));
            ways.Add(new Way(pts, run[join].Pos, joinOff, len, from.Count, from.Count(x => x.Plane)));
        }
        return (ways, why);
    }

    /// <summary>Douglas-Peucker: the points whose leaving out would move the line more than Tolerance.</summary>
    static List<Vector3> Simplify(List<Vector3> pts)
    {
        if (pts.Count <= 2) return pts;
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var todo = new Stack<(int A, int B)>();
        todo.Push((0, pts.Count - 1));
        while (todo.TryPop(out var r))
        {
            int far = -1;
            float most = Tolerance;
            for (int i = r.A + 1; i < r.B; i++)
            {
                float d = FromSegment(pts[i], pts[r.A], pts[r.B]);
                if (d > most) (most, far) = (d, i);
            }
            if (far < 0) continue;
            keep[far] = true;
            todo.Push((r.A, far));
            todo.Push((far, r.B));
        }
        return pts.Where((_, i) => keep[i]).ToList();
    }

    static float FromSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector3.Distance(p, a + ab * t);
    }

    /// <summary>A way's points for a log line: the first and last, how many, how long, the foot planes.</summary>
    public static string Said(Way w) => FormattableString.Invariant(
        $"from {w.Points[0].X:0.0},{w.Points[0].Y:0.0},{w.Points[0].Z:0.0} to {w.Points[^1].X:0.0},{w.Points[^1].Y:0.0},{w.Points[^1].Z:0.0}, {w.Points.Count} points over {w.Length:0.0} m ({w.Samples} samples, a foot plane at {w.Planes}), {w.Off:0.0} m from her where she joins it");
}
