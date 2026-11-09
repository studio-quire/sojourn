using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.World;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>
/// Driving after someone (docs/access-app.md > Walk mode > Driving after someone): //follow while at the wheel of a
/// vehicle steered by its keys holds those keys for the person, along the way the one ahead went, staying about
/// ConvoyGap behind, or as far as //follow NAME METRES says. Any arrow or Space takes it back. Not for what runs on rails or a throttle: the keys there don't
/// steer, and Up held opens a throttle all the way. Written by Claude Opus 5.5, 2026-11-04, after the blind tester asked
/// for vehicles following each other, a beacon over two cars' engines not having been enough. Losing sight of them
/// (2026-11-08, after her scooter trip lost her partner just over a border): it drives on where they went, a little
/// way, and picks them up again.
/// </summary>
public sealed partial class WalkMode
{
    const float ConvoyGap = 10f, ConvoyCrumb = 2.5f, ConvoyFar = 60f, ConvoyJump = 100f, ConvoyDeadband = 0.12f, ConvoyBraking = 4f;
    const long ConvoyEvery = 100, ConvoySteerCycle = 300, ConvoyLostAfter = 6000, ConvoyStuckAfter = 6000;
    // the keys reading as untaken: around a crossing the region left and the one entered each send their word on the
    // vehicle script's keys, in either order, so only 10 s after a crossing counts, and elsewhere 2 s of it
    const long ConvoyCrossingGrace = 10_000, ConvoyKeysGrace = 2000;
    // Lost sight of them: on along their way at their pace (10 m/s at most), to 25 m past the border their way meets
    // within 40 m of where they were last seen, else 15 m past it; said once it's lasted 1.5 s (a moment's gap says
    // nothing); given up 20 s after they were last seen, or 4 s after coming to rest at the end of the way on, either
    // only once 4 s have passed since her own last crossing (the region entered names who's in it within a second or two)
    const long ConvoySayLost = 1500, ConvoySearchFor = 20_000, ConvoyRestAtEnd = 4000, ConvoyAfterCrossing = 4000;
    const float ConvoySearchPace = 10f, ConvoyPastBorder = 25f, ConvoyPastPoint = 15f, ConvoyBorderAhead = 40f, ConvoyBorderBehind = 30f;

    sealed class Convoy(string name, UUID id, float gap)
    {
        public readonly string Name = name;
        public readonly UUID Id = id;
        public readonly float Gap = gap;
        public readonly Queue<Vector2> Way = new();   // where they went, in the grid's metres, so a crossing moves nothing
        public int Guesses;                           // the last this many of Way are the way on guessed while they're lost
        public Vector2? Last, PaceFrom;
        public float Pace;                            // how fast they're going, metres a second
        public long DecideAt, LostSince, StuckSince, LogAt, SteeredAt, PaceAt, KeysLostSince;
        public long CrossedAt = long.MinValue / 2;
        public string? Region, CrossedInto, Logged;
        public float YawOffset;                       // the way it travels against the way the seat faces, learned on straights
        public bool Fwd, Back, Left, Right, ToldWith, ToldFar, Doubt;
        // the last time they were seen: where (the grid's metres), how high, when, in which region, how, and the velocity
        // their copy reported (a copy left frozen where they crossed out keeps the last it had)
        public Vector2? Seen, SeenVel, Raw;
        public float SeenZ;
        public long SeenAt, RawAt;
        public RegionWorld? SeenIn;
        public string SeenHow = "";
        public readonly List<Vector2> Trail = new();  // where they've been lately, a metre or more apart, the newest last
        public Search? Lost;
    }

    /// <summary>Driving on where they went while they can't be seen.</summary>
    sealed class Search
    {
        public long At;             // when they were last seen
        public Vector2 From, Dir;   // where, and the way they were heading (zero: not known, or standing)
        public float Z, Pace, Length, Laid;
        public bool Border, Told;
        public long RestSince;
        public string? Stop;        // what shortened the way on (water, a drop)
        public int Id;
    }

    int _searches;

    /// <summary>The session's log as well as the walk log: what driving after someone did is in a log she sends (on
    /// 0.1.0.996 it was only in SOJOURN_WALK_LOG's, so her motorbike trip of 2026-11-08 couldn't show why it went astray).</summary>
    void ConvoyLog(string line)
    {
        Log($"convoy: {line}");
        S.Log.LogInformation("convoy: {Line}", line);
    }

    long _blindFrom, _blindUntil;

    /// <summary>The scripted test's (//blindconvoy, with SOJOURN_TEST_CONVOY_BLIND=1): the one driven after goes unseen
    /// from that many seconds on, for so many, as a region that hides them would leave them.</summary>
    public void BlindConvoy(double after, double secs)
    {
        _blindFrom = Environment.TickCount64 + (long)(after * 1000);
        _blindUntil = _blindFrom + (long)(secs * 1000);
    }

    Convoy? _convoy;

    public string? DrivingAfter => _convoy?.Name;

    bool ConvoyDown(K k) => _convoy is { } c && k switch
    {
        K.Fwd => c.Fwd,
        K.Back => c.Back,
        K.TurnL => c.Left,
        K.TurnR => c.Right,
        _ => false,
    };

    /// <summary>//follow while seated. Where the keys can't do it, a beacon on the person instead, saying why.</summary>
    public void DriveAfter(string name, UUID id, float? gap = null)
    {
        if (Me() is not { Sitting: true } b) { Line("You're not seated."); return; }
        string seat = b.SeatName ?? "your seat";
        string? why = (S.ControlsTaken & MoveKeys) == 0 ? $"{seat} hasn't taken your keys, so you're riding it, not driving it"
            : _railTrack != null ? $"{seat} runs on rails"
            : Throttle != null ? $"{seat} runs on a throttle"
            : null;
        if (why != null)
        {
            Line($"I can't drive after {name}: {why}. Here's a beacon on them instead.");
            BeaconOn(name, id);
            return;
        }
        _convoy = new Convoy(name, id, gap ?? ConvoyGap);
        // the seat's name on a line of its own: a log sent leaves out any line repeating words someone wrote, and on
        // 2026-11-08 that took every line naming her scooter (its name was most likely in an IM she was sent)
        ConvoyLog($"driving after {name}, {_convoy.Gap:0.#} m behind, in {b.RegionName}");
        ConvoyLog($"the seat driven: {seat}");
        Line($"Driving {seat} after {name}, about {Bearings.Metres(_convoy.Gap)} behind. Any arrow key or Space takes it back.");
    }

    /// <summary>Lets go of the keys (the next Drive clears them), saying why if there's a why.</summary>
    public void EndConvoy(string? line)
    {
        if (_convoy is not { } c) return;
        _convoy = null;
        ConvoyLog($"ended driving after {c.Name}: {line ?? "quietly"}");
        if (line != null) Line(line);
    }

    static float Wrap(float a) => MathF.Atan2(MathF.Sin(a), MathF.Cos(a));

    void ConvoyTick(Body b, long now)
    {
        if (_convoy is not { } c || now < c.DecideAt) return;
        c.DecideAt = now + ConvoyEvery;
        bool doubt = SeatInDoubt(b);
        if (!b.Sitting && !doubt) { EndConvoy($"You're off your seat, so no longer driving after {c.Name}."); return; }
        if (c.Region != null && b.RegionName != c.Region)
        {
            c.CrossedAt = now;
            c.CrossedInto = b.RegionName;
            string keys = (S.ControlsTaken & MoveKeys) != 0 ? "taken" : "not taken";
            ConvoyLog($"crossed into {b.RegionName}{(doubt ? ", the seat not sent yet" : "")}; the keys {keys}");
        }
        c.Region = b.RegionName;
        bool crossing = doubt || now - c.CrossedAt < ConvoyCrossingGrace;
        // 0.1.0.996 ended at the first tick of a crossing that read the keys untaken (her scooter into Berbera,
        // 2026-11-08), and with the old region's word and the new one's crossing on the way, a moment of that is expected
        if ((S.ControlsTaken & MoveKeys) == 0)
        {
            if (c.KeysLostSince == 0)
            {
                c.KeysLostSince = now;
                string since = crossing ? $", {(now - c.CrossedAt) / 1000.0:0.0} s after crossing into {c.CrossedInto}" : "";
                ConvoyLog($"the keys read as not taken{since}");
            }
            else if (!crossing && now - c.KeysLostSince > ConvoyKeysGrace)
            {
                bool atCrossing = c.CrossedInto != null && c.KeysLostSince - c.CrossedAt < ConvoyCrossingGrace;
                EndConvoy(atCrossing
                    ? $"The vehicle let go of your keys at the crossing into {c.CrossedInto}, so no longer driving after {c.Name}. Sitting on it again usually gives them back."
                    : $"The vehicle let go of your keys, so no longer driving after {c.Name}.");
                return;
            }
        }
        else if (c.KeysLostSince != 0)
        {
            ConvoyLog($"the keys are taken again, after {now - c.KeysLostSince} ms");
            c.KeysLostSince = 0;
        }
        // until the region has sent her avatar and its seat, where she is may be in the frame of the region she left
        // (0.1.0.996 said "getting away, 289 metres off" at that crossing, and 276 and 226 on 2026-11-05): the keys stay
        // as they are, as a driver holds the throttle over a border
        if (doubt != c.Doubt) { c.Doubt = doubt; if (doubt) ConvoyLog("the seat not sent yet: keys held as they are"); }
        if (doubt) return;

        Utils.LongToUInts(b.Region, out var gx, out var gy);
        var me = Global(b);
        float speed = Speed(b), reach = MathF.Max(5f, speed);
        Vector2 them;
        var sight = ConvoySees(c, now);
        if (sight is { } s)
        {
            // a region sends a moving avatar's place only now and then (when its velocity changes, OpenSim; when carrying
            // it on by its velocity would be out, Second Life): a copy is carried on by the velocity it last reported, as a
            // viewer draws it, for up to a second and a half since its place last changed
            var raw = new Vector2(gx + s.Pos.X, gy + s.Pos.Y);
            if (c.Raw != raw) { c.Raw = raw; c.RawAt = now; }
            them = raw;
            if (s.Vel is { } vel && vel.Length() >= 0.5f) them += vel * MathF.Min(1.5f, (now - c.RawAt) / 1000f);
            if (c.LostSince != 0) ConvoyLog($"{c.Name} seen after {now - c.LostSince} ms");
            c.LostSince = 0;
            if (c.Lost is { } lost)
            {
                // seen again: far from where they could have got to since, they jumped (a teleport)
                float could = ConvoyJump + MathF.Max(lost.Pace, 5f) * (now - lost.At) / 1000f;
                float off = Vector2.Distance(lost.From, them);
                if (off > could)
                {
                    EndConvoy($"{c.Name} jumped away, a teleport most likely, so I've let go of the keys.");
                    RequestStop();
                    return;
                }
                ConvoyLog($"{c.Name} seen again after {now - lost.At} ms, by {s.How} in {s.Region.Name} {s.Pos.X - S.FrameOffset(s.Region).X:0},{s.Pos.Y - S.FrameOffset(s.Region).Y:0}, {off:0} m from where they were last seen, {Vector2.Distance(them, me):0} m off");
                if (lost.Told) Line($"Found {c.Name} again, {Bearings.Metres(Vector2.Distance(them, me))} away.");
                // the way on was a guess: what's left of it goes, and from what's left of theirs it's straight to them
                var kept = c.Way.Take(c.Way.Count - c.Guesses).ToList();
                c.Way.Clear();
                foreach (var p in kept) c.Way.Enqueue(p);
                c.Guesses = 0;
                c.Last = null;
                c.PaceFrom = null;
                c.Trail.Clear();
                c.Lost = null;
            }
            else if (c.Last is { } last && Vector2.Distance(last, them) > ConvoyJump)
            {
                EndConvoy($"{c.Name} jumped away, a teleport most likely, so I've let go of the keys.");
                RequestStop();
                return;
            }
            c.Seen = them;
            c.SeenZ = s.Pos.Z;
            c.SeenAt = now;
            c.SeenIn = s.Region;
            c.SeenHow = s.How;
            c.SeenVel = s.Vel;
            if (c.Trail.Count == 0 || Vector2.Distance(c.Trail[^1], them) >= 1f) { c.Trail.Add(them); if (c.Trail.Count > 12) c.Trail.RemoveAt(0); }
            if (c.Last is not { } was || Vector2.Distance(was, them) >= ConvoyCrumb) { c.Way.Enqueue(them); c.Last = them; }
            // their pace over half a second or more (positions come in steps), and the room it takes to come down to it
            if (c.PaceFrom is not { } from) { c.PaceFrom = them; c.PaceAt = now; }
            else if (now - c.PaceAt >= 500)
            {
                c.Pace = Vector2.Distance(from, them) * 1000f / (now - c.PaceAt);
                c.PaceFrom = them;
                c.PaceAt = now;
            }
        }
        else if (c.Seen is null)
        {
            // never seen since it started (it found them a moment before): let go after a while, as before there was a way on
            c.Fwd = c.Back = c.Left = c.Right = false;
            if (c.LostSince == 0) { c.LostSince = now; ConvoyLog($"{c.Name} not seen ({InView(c.Id)})"); }
            else if (now - c.LostSince > ConvoyLostAfter) { EndConvoy($"Lost {c.Name}, so I've let go of the keys."); RequestStop(); }
            return;
        }
        else
        {
            var lost = c.Lost ??= LoseSight(c, b, now);
            // where they'd be by now along the way on, and points along it to steer by
            float along = MathF.Min(lost.Length, lost.Pace * (now - lost.At) / 1000f);
            them = lost.From + lost.Dir * along;
            while (lost.Laid + ConvoyCrumb <= along) { lost.Laid += ConvoyCrumb; c.Way.Enqueue(lost.From + lost.Dir * lost.Laid); c.Guesses++; }
            c.Pace = along < lost.Length ? lost.Pace : 0;
            if (!lost.Told && now - lost.At >= ConvoySayLost)
            {
                lost.Told = true;
                Line($"Lost sight of {c.Name}{(lost.Border ? " at the border" : "")}; going {(lost.Pace > 0 ? "where they went" : "to where they were")}.");
            }
            bool settled = now - c.CrossedAt > ConvoyAfterCrossing;
            if (along >= lost.Length && speed < 0.5f) { if (lost.RestSince == 0) lost.RestSince = now; }
            else lost.RestSince = 0;
            if (settled && (now - lost.At > ConvoySearchFor || lost.RestSince != 0 && now - lost.RestSince > ConvoyRestAtEnd))
            {
                ConvoyLog($"searched {(now - lost.At) / 1000.0:0.0} s, {along:0} m along the way on of {lost.Length:0}: {InView(c.Id)}");
                EndConvoy($"Couldn't find {c.Name} again, so I've let go of the keys.");
                RequestStop();
                return;
            }
        }
        bool guessing = c.Lost != null;

        // points she's come within reach of, or gone past to one side (the next no further than it), are behind her
        while (c.Way.Count > 1)
        {
            var head = c.Way.Peek();
            float dHead = Vector2.Distance(head, me);
            if (dHead >= reach && Vector2.Distance(c.Way.ElementAt(1), me) > dHead) break;
            c.Way.Dequeue();
            c.Guesses = Math.Min(c.Guesses, c.Way.Count);
        }
        var aim = c.Way.Count > 0 ? c.Way.Peek() : them;
        float dist = Vector2.Distance(them, me);
        // close behind them the way runs out within the reach: aim on past them the way they're heading, not at a point a
        // metre or two ahead, where every half-metre to one side read as a sharp turn and swung the keys (a 3 m gap at
        // 11 m/s on her scooter trip of 2026-11-08 left the aim 0.5 to 3 m ahead; her steering weaved)
        if (Vector2.Distance(aim, me) < reach)
            aim = dist < reach && ConvoyHeading(c) is { } heading ? them + heading * (reach - dist) : them;

        // steered by the way it faces, not the way it's going: travel lags the wheel, and steering by it overshoots into a spin
        if (speed > 2f && now - c.SteeredAt > 500)
            c.YawOffset += 0.3f * Wrap(MathF.Atan2(b.Vel.Y, b.Vel.X) - b.Yaw - c.YawOffset);
        float err = Wrap(MathF.Atan2(aim.Y - me.Y, aim.X - me.X) - b.Yaw - c.YawOffset);   // positive is to the left
        float closing = MathF.Max(0f, speed - c.Pace), room = closing * closing / (2 * ConvoyBraking) + closing * 0.3f;
        bool close = dist < c.Gap;
        c.Fwd = dist > c.Gap + room && !(MathF.Abs(err) > 1f && speed > 6f);
        c.Back = closing > 1f && dist < c.Gap + room * 0.6f;
        bool steering = c.Fwd || speed > 0.5f;
        // the keys held for a share of each cycle, by how far off it is: a held key turns as far as the vehicle's script likes
        bool turning = steering && MathF.Abs(err) > ConvoyDeadband
            && now % ConvoySteerCycle < ConvoySteerCycle * Math.Clamp(MathF.Abs(err) / 0.5f, 0.3f, 1f);
        c.Left = turning && err > 0;
        c.Right = turning && err < 0;
        if (c.Left || c.Right) c.SteeredAt = now;

        // (not while the keys read as untaken: that has its own end, and a vehicle not driven doesn't move)
        if (c.Fwd && speed < 0.3f && c.KeysLostSince == 0)
        {
            if (c.StuckSince == 0) c.StuckSince = now;
            else if (now - c.StuckSince > ConvoyStuckAfter) { EndConvoy($"{b.SeatName ?? "The vehicle"} isn't moving, so I've let go of the keys."); return; }
        }
        else c.StuckSince = 0;

        if (_set.WalkText("follow") && !guessing)
        {
            if (close && speed < 0.5f && !c.ToldWith) { c.ToldWith = true; Line($"With {c.Name}, {Bearings.Metres(dist)} away."); }
            if (dist > c.Gap * 2) c.ToldWith = false;
            if (dist > ConvoyFar && !c.ToldFar) { c.ToldFar = true; Line($"{c.Name} is getting away, {Bearings.Metres(dist)} off."); }
            if (dist < ConvoyFar / 2) c.ToldFar = false;
        }
        if (now - c.LogAt > 2000)
        {
            string keys = $"{(c.Fwd ? "up " : "")}{(c.Back ? "down " : "")}{(c.Left ? "left " : "")}{(c.Right ? "right" : "")}".TrimEnd();
            string seen = guessing ? "where they'd be by now (not seen)" : $"{c.Name}, by {c.SeenHow} in {c.SeenIn?.Name}";
            var state = $"{dist:0.0} m from {seen} (their pace {c.Pace:0.0} m/s) at {speed:0.0} m/s, in {b.RegionName} {b.Pos.X:0},{b.Pos.Y:0},"
                + $" aim {err * 180 / MathF.PI:0} degrees off ({Vector2.Distance(aim, me):0} m on), {c.Way.Count} points ahead, keys {(keys.Length > 0 ? keys : "none")}";
            // (every 2 s while anything changes; waiting still behind them, every 15 s)
            if (state != c.Logged || now - c.LogAt > 15_000) { ConvoyLog(state); c.Logged = state; c.LogAt = now; }
        }
    }

    /// <summary>The one driven after, where a convoy goes by: as <see cref="See"/>, but a copy of them that has been a
    /// while in a region whose list leaves them out doesn't count, since that region says they're gone from it (a copy
    /// left frozen where they crossed out, until it's dropped as a ghost, would have the vehicle drive to the border and
    /// stop there). One just come into such a region does: the handover that brought it can take a few seconds.</summary>
    Sighting? ConvoySees(Convoy c, long now)
    {
        if (now >= _blindFrom && now < _blindUntil) return null;
        return See(c.Id) is { Rank: < 4 } s ? s : null;
    }

    /// <summary>The way they're heading, from where they've been over the last 6 m or so (else what their copy last
    /// reported); null when not known or standing.</summary>
    static Vector2? ConvoyHeading(Convoy c)
    {
        if (c.Lost is { } lost) return lost.Dir == Vector2.Zero ? null : lost.Dir;
        if (c.Trail.Count >= 2)
        {
            var last = c.Trail[^1];
            Vector2 from = c.Trail[0];
            for (int i = c.Trail.Count - 2; i >= 0; i--) if (Vector2.Distance(c.Trail[i], last) >= 6f) { from = c.Trail[i]; break; }
            if (Vector2.Distance(from, last) >= 2f) return Vector2.Normalize(last - from);
        }
        return c.SeenVel is { } v && v.Length() >= 0.5f ? Vector2.Normalize(v) : null;
    }

    /// <summary>They've gone from sight: from where they were last seen, on the way they were heading at their pace,
    /// across the border if that's where they were going. Logged with what each region in view had of them.</summary>
    Search LoseSight(Convoy c, Body b, long now)
    {
        var from = c.Seen!.Value;
        var dir = ConvoyHeading(c) ?? Vector2.Zero;
        // a copy left where they crossed out keeps the speed it last reported, while where it is stops changing
        float reported = c.SeenVel?.Length() ?? 0;
        float pace = reported >= 1f ? MathF.Max(reported, c.Pace) : c.Pace;
        pace = dir == Vector2.Zero || pace < 1f ? 0 : MathF.Min(pace, ConvoySearchPace);
        float ahead = float.PositiveInfinity, behind = float.PositiveInfinity;
        string where = "";
        if (c.SeenIn is { } w)
        {
            Utils.LongToUInts(w.Handle, out var wx, out var wy);
            where = $" in {w.Name} {from.X - wx:0},{from.Y - wy:0}";
            if (dir != Vector2.Zero)
            {
                ahead = ToEdge(from, dir, wx, wy, w.SizeX, w.SizeY);
                behind = ToEdge(from, -dir, wx, wy, w.SizeX, w.SizeY);
            }
        }
        var lost = new Search
        {
            // (how high: a copy's, else hers, on the same road; the map's heights come in 4 m steps)
            At = c.SeenAt, From = from, Dir = dir, Z = c.SeenHow == "copy" ? c.SeenZ : b.Pos.Z, Pace = pace, Id = ++_searches,
            Border = ahead <= ConvoyBorderAhead || behind <= ConvoyBorderBehind,
            Length = pace == 0 ? 0 : ahead <= ConvoyBorderAhead ? ahead + ConvoyPastBorder : ConvoyPastPoint,
        };
        string heading = dir == Vector2.Zero ? "heading not known" : $"heading {Bearings.Point(Bearings.Compass(0, 0, dir.X, dir.Y))}";
        ConvoyLog($"lost sight of {c.Name}: last seen {(now - c.SeenAt) / 1000.0:0.0} s ago{where}, by {c.SeenHow}, {heading} at {pace:0.0} m/s"
            + $"{(float.IsFinite(ahead) ? $", the border {ahead:0} m on" : "")}; {InView(c.Id)}");
        ConvoyLog(lost.Length > 0 ? $"going on {lost.Length:0} m past where they were last seen{(ahead <= ConvoyBorderAhead ? $", {ConvoyPastBorder:0} m over the border" : "")}"
            : "going to where they were last seen");
        if (lost.Length > 0) _ = CheckWayOnAsync(lost, b);
        return lost;
    }

    /// <summary>How far from a point along a direction to the edge of a region (its corner and size in the grid's metres).</summary>
    static float ToEdge(Vector2 p, Vector2 d, uint x0, uint y0, uint sx, uint sy)
    {
        float t = float.PositiveInfinity;
        if (d.X > 1e-4f) t = MathF.Min(t, (x0 + sx - p.X) / d.X);
        else if (d.X < -1e-4f) t = MathF.Min(t, (x0 - p.X) / d.X);
        if (d.Y > 1e-4f) t = MathF.Min(t, (y0 + sy - p.Y) / d.Y);
        else if (d.Y < -1e-4f) t = MathF.Min(t, (y0 - p.Y) / d.Y);
        return MathF.Max(0, t);
    }

    /// <summary>The way on stops short of open water or a drop: a ray down every 4 m along it (the collision model, which
    /// has the regions around), the floor the higher of what it meets and the ground, wet below that region's water. A
    /// point in no region in view, or with nothing known under it, tells nothing and stops nothing.</summary>
    async Task CheckWayOnAsync(Search lost, Body b)
    {
        if (!_rays.Ready) return;
        Utils.LongToUInts(b.Region, out var gx, out var gy);
        float top = lost.Z + 12f;
        var points = new List<Vector2>();
        var rays = new List<(Vector3, Vector3, float)>();
        for (float k = 4f; k <= lost.Length; k += 4f)
        {
            var g = lost.From + lost.Dir * k;
            points.Add(g);
            rays.Add((new Vector3(g.X - gx, g.Y - gy, top), -Vector3.UnitZ, 60f));
        }
        if (points.Count == 0) return;
        Sojourn.Session.Collision.RayBatch? got;
        try { got = await _rays.CollideManyAsync(rays, b.Linkset); }
        catch (Exception) { return; }
        if (got is not { } batch || batch.Region != b.Region || _convoy?.Lost is not { } current || current.Id != lost.Id) return;
        float? before = lost.Z - 1f;   // about where they stood or sat
        for (int i = 0; i < points.Count; i++)
        {
            if (RegionAt(points[i]) is not { } w) { before = null; continue; }   // not in view: nothing to tell
            Utils.LongToUInts(w.Handle, out var wx, out var wy);
            float ground = w.TerrainHeight(points[i].X - wx, points[i].Y - wy) ?? float.NegativeInfinity;
            float prim = batch.Hits[i] is { } h ? top - h.Distance : float.NegativeInfinity;
            float floor = MathF.Max(ground, prim);
            if (!float.IsFinite(floor)) { before = null; continue; }
            // (water only when they were above it: under the sea, wet is the way)
            string? why = w.Simulator is { } sim && sim.WaterHeight > floor + 0.3f && sim.WaterHeight < lost.Z ? "water"
                : before is { } f && f - floor > 3f ? "a drop" : null;
            before = floor;
            if (why == null) continue;
            float keep = MathF.Max(0, 4f * (i + 1) - 6f);
            if (keep >= lost.Length) return;
            lost.Length = keep;
            lost.Stop = why;
            ConvoyLog($"the way on meets {why} {4f * (i + 1):0} m past where they were last seen: going no further than {keep:0} m past it");
            return;
        }
    }

    /// <summary>The region in view a point (the grid's metres) is in.</summary>
    RegionWorld? RegionAt(Vector2 g)
    {
        foreach (var w in S.VisibleRegions())
        {
            Utils.LongToUInts(w.Handle, out var wx, out var wy);
            if (g.X >= wx && g.X < wx + w.SizeX && g.Y >= wy && g.Y < wy + w.SizeY) return w;
        }
        return null;
    }

    /// <summary>For the log: what each region in view has of someone (its copy and whether the seat it's on is there, its
    /// map list and how old, a copy dropped as a ghost lately).</summary>
    string InView(UUID id)
    {
        long now = Environment.TickCount64;
        var parts = new List<string>();
        foreach (var w in S.VisibleRegions())
        {
            string list = w.CoarseAt == 0 ? "no list yet"
                : $"a list of {w.Coarse.Count} {(w.Coarse.ContainsKey(id) ? "naming them" : "without them")}, {(now - w.CoarseAt) / 1000.0:0.0} s old";
            string copy = w.AvatarById(id) is { } av ? (w.TryRegionPosition(av, out _) ? "a copy" : "a copy on a seat not here") : "no copy";
            string dropped = w.Dropped(id) is { } d ? $", a copy dropped {(now - d.At) / 1000.0:0.0} s ago ({d.Why})" : "";
            parts.Add($"{w.Name}: {copy}, {list}{dropped}");
        }
        return parts.Count == 0 ? "no regions in view" : "in view: " + string.Join("; ", parts);
    }
}
