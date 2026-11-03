using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>
/// A ride recorded, to be told afterwards (docs/access-app.md > Walk mode > Recording a ride; the blind tester's wish to
/// ride a carnival waterslide and hear it described after, 2026-11-01). While it records: every 3 s a moment (where,
/// how fast and which way, climbing or falling, what she sits on, the named things and people within 20 m and where
/// from her, the lines objects said, the seat's floating text when it changes), and every few seconds a picture from
/// her eyes looking the way she's going (DescribeApi.MovingPictureAsync), JPEG, a few tens of KB. The pictures adapt to
/// how fast they come: one at a time, the next at least 2.5 times as long after as the last took (3-15 s), and one
/// due while another is still drawing is skipped, never queued, so on a PC drawing on its processor the ride doesn't
/// wait on them. At most 40 kept: past that every other one goes and they come half as often, so a long ride keeps its
/// whole length. It ends on her word (C, `//ride stop`), when she stands up, when a ride she sat on has moved (3 s of
/// it, not a jolt) and then been still for 8 s, or after fifteen minutes. Nothing leaves the PC until she asks for it told
/// (`Get`, through SojournRide); the last ride stays until the next starts or the session ends. Runs on the window's thread (walk mode's tick); pictures and
/// looks round come back there. Written by Claude Opus 5.5, 2026-11-01.
/// </summary>
public sealed class RideRecorder
{
    public const int MaxPictures = 40;        // kept; past that, every other one goes
    public const int BriefPictures = 16;      // sent for a brief telling, evenly through the ride (rich: all)
    public const int BriefPicturesLong = 24;  // the same for a ride over five minutes
    // the most moments sent for any telling (a five-minute ride's 100 or so, about 30K tokens of them): past that the
    // ones between fold into the next one sent (MomentsForTelling)
    public const int MomentsTold = 100;
    const long MomentEvery = 3000;
    const long FirstInterval = 3000, LongestInterval = 15000;
    const double SlowerThanDrawing = 2.5;      // the next picture at least this many times as long after as the last took
    // fifteen minutes (five until 835 cut both her rides through an undersea tunnel short; a railway loop runs longer)
    const long LongestRide = 15 * 60_000;
    const long StillFor = 8000, StoodFor = 3000;   // (a crossing drops the seat for a moment: held over it, WalkMode.SeatInDoubt)
    // moving this long seated before a stop can end it: a train flipped round at its platform jumps a few metres at once,
    // which read as a ride that had moved and stopped (0.1.0.835 ended her recording that way before the train set off)
    const long MovedFor = 3000;
    const float Near = 20;

    public sealed record Thing(string Name, string Where);

    public sealed class Moment
    {
        public required long Ms { get; init; }              // from the start
        public required string Region { get; init; }
        public required Vector3 Pos { get; init; }          // in Region's frame
        public required Vector3 Global { get; init; }       // across regions, for distances
        public required float Speed { get; init; }          // metres a second over the ground
        public required float Climb { get; init; }          // metres a second, up
        public required double Compass { get; init; }       // the way it was going (facing, when still), 0 north
        public string? Seat { get; init; }
        public string? Shows { get; init; }                 // the seat's floating text, when it changed
        public List<Thing>? Around { get; set; }
        public List<string> Said { get; } = new();
        public JsonObject? Picture { get; set; }
    }

    public sealed class Ride
    {
        public required string Id { get; init; }
        public required long Start { get; init; }
        public required string Began { get; init; }
        public bool Private { get; init; }                  // recorded in incognito: dropped when it ends
        public List<Moment> Moments { get; } = new();
        public string? Ended { get; set; }
        public long EndedAt { get; set; }
        public int Taken, Skipped, Thinned;
        public long LastPictureMs;                          // how long the last picture took
        public long Ms => (Ended != null ? EndedAt : Environment.TickCount64) - Start;
        public int Pictures => Moments.Count(m => m.Picture != null);
    }

    readonly WalkMode _walk;
    readonly Func<Vector3, Task<JsonObject?>> _picture;
    readonly Func<WalkMode.Body, double, Task<List<Thing>>> _around;
    readonly Action<Action> _post;
    AgentSession S => _session();
    readonly Func<AgentSession> _session;

    /// <summary>Pictures can be taken and told now (the view runs, the assistant is there to tell them).</summary>
    public Func<bool> PicturesWanted { get; set; } = () => true;
    /// <summary>A recording ended by itself (stood up, still, the time): the reason, for the window to say.</summary>
    public event Action<Ride>? EndedByItself;

    volatile Ride? _ride;
    Ride? _last;
    public Ride? Current => _ride;
    /// <summary>The ride recording now, else the last one recorded this session.</summary>
    public Ride? Latest => _ride ?? _last;
    public bool On => _ride != null;

    /// <param name="picture">one picture from the eyes looking along a direction (DescribeApi.MovingPictureAsync)</param>
    /// <param name="around">the named things and people within 20 m, where from the body going that compass way</param>
    /// <param name="post">runs on the window's thread</param>
    public RideRecorder(WalkMode walk, Func<AgentSession> session, Func<Vector3, Task<JsonObject?>> picture,
        Func<WalkMode.Body, double, Task<List<Thing>>> around, Action<Action> post)
    {
        _walk = walk;
        _session = session;
        _picture = picture;
        _around = around;
        _post = post;
        lock (AgentSession.ClientHooks) AgentSession.ClientHooks.Add(Hook);
        if (S.Client is { } c) Hook(c);
    }

    public bool Start(bool incognito)
    {
        if (_walk.Me() is not { } b) return false;
        _last = null;
        _ride = new Ride { Id = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), Start = Environment.TickCount64, Began = b.RegionName, Private = incognito };
        _track.Clear();
        (_seatedEver, _moved, _standingSince, _stillSince, _nextMoment, _nextPicture, _interval, _floor, _shows) = (false, false, 0, 0, 0, 0, FirstInterval, FirstInterval, null);
        (_movingMs, _lastTick) = (0, 0);
        while (_heard.TryDequeue(out _)) { }
        Tick(b, Environment.TickCount64);
        return true;
    }

    /// <summary>Ends the recording (her word, or by itself); the ride stays as the last one.</summary>
    public Ride? Stop(string why)
    {
        if (_ride is not { } r) return null;
        r.Ended = why;
        r.EndedAt = Environment.TickCount64;
        _ride = null;
        _last = r;
        return r;
    }

    /// <summary>Incognito ended: a ride recorded in it isn't kept.</summary>
    public void DropPrivate()
    {
        if (_ride is { Private: true }) _ride = null;
        if (_last is { Private: true }) _last = null;
    }

    // --- the tick --------------------------------------------------------------------------------------------------

    readonly List<(long T, Vector3 G)> _track = new();
    bool _seatedEver, _moved, _shooting, _looking;
    long _standingSince, _stillSince, _nextMoment, _nextPicture, _interval, _floor, _movingMs, _lastTick;
    string? _shows;

    public void Tick(WalkMode.Body b, long now)
    {
        if (_ride is not { } r) return;
        Utils.LongToUInts(b.Region, out var gx, out var gy);
        var g = new Vector3(gx + b.Pos.X, gy + b.Pos.Y, b.Pos.Z);
        // where it was each tick over the last 2 s (still too: a stretch standing still must read as still)
        _track.Add((now, g));
        while (_track.Count > 2 && now - _track[1].T > 2000) _track.RemoveAt(0);
        var (speed, climb, compass) = Motion(b, now);
        long dt = _lastTick == 0 ? 0 : Math.Min(now - _lastTick, 500);
        _lastTick = now;
        // seated, or seated before and the region not saying yet (a crossing): a crossing's moments aren't standing
        // (0.1.0.835 told her "you were standing on top of Zephyr" from a moment taken mid-crossing)
        bool sitting = b.Sitting || _walk.SeatInDoubt(b);

        // over by itself: stood up, a ride sat on gone still, or the longest a ride is kept
        if (sitting) { _seatedEver = true; _standingSince = 0; }
        else if (_seatedEver && _standingSince == 0) _standingSince = now;
        if (sitting && speed >= 1 && (_movingMs += dt) >= MovedFor) _moved = true;
        if (speed >= 0.3f) _stillSince = 0;
        else if (_stillSince == 0) _stillSince = now;
        var why = _standingSince != 0 && now - _standingSince >= StoodFor ? "you stood up"
            : _moved && sitting && _stillSince != 0 && now - _stillSince >= StillFor ? "the ride stopped moving"
            : now - r.Start >= LongestRide ? "fifteen minutes, the longest a ride is recorded" : null;
        if (why != null)
        {
            TakeMoment(r, b, sitting, g, now, speed, climb, compass);
            Stop(why);
            EndedByItself?.Invoke(r);
            return;
        }

        bool picture = now >= _nextPicture && PicturesWanted();
        if (picture && _shooting)
        {
            // still drawing the last: this one's skipped, never queued (her PC may draw on its processor)
            r.Skipped++;
            _nextPicture = now + _interval;
            picture = false;
        }
        if (!picture && now < _nextMoment) return;
        var m = TakeMoment(r, b, sitting, g, now, speed, climb, compass);
        if (picture) Shoot(r, m, compass, climb, speed, now);
    }

    /// <summary>Speed over the ground, climb and the way it's going, from where the body went over the last 1-2 s
    /// (a ride moved by its script has no velocity of its own), else its velocity; still, the way it faces.</summary>
    (float Speed, float Climb, double Compass) Motion(WalkMode.Body b, long now)
    {
        if (_track.Count >= 2 && now - _track[0].T >= 1000)
        {
            var (t0, g0) = _track[0];
            var (_, g1) = _track[^1];
            float dt = (now - t0) / 1000f;
            var d = g1 - g0;
            float flat = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
            double way = flat > 0.3f ? Bearings.Compass(0, 0, d.X, d.Y) : Bearings.Norm(90 - b.Yaw * 180 / Math.PI);
            return (flat / dt, d.Z / dt, way);
        }
        float v = MathF.Sqrt(b.Vel.X * b.Vel.X + b.Vel.Y * b.Vel.Y);
        return (v, b.Vel.Z, v > 0.3f ? Bearings.Compass(0, 0, b.Vel.X, b.Vel.Y) : Bearings.Norm(90 - b.Yaw * 180 / Math.PI));
    }

    Moment TakeMoment(Ride r, WalkMode.Body b, bool sitting, Vector3 g, long now, float speed, float climb, double compass)
    {
        // (mid-crossing the seat's text isn't there: what it showed last stands)
        var shows = b.Sitting ? _walk.SeatText(b) : sitting ? _shows : null;
        var seat = sitting ? b.SeatName ?? _walk.LastSeatName ?? "something unnamed" : null;
        var m = new Moment
        {
            Ms = now - r.Start, Region = b.RegionName, Pos = b.Pos, Global = g, Speed = speed, Climb = climb, Compass = compass,
            Seat = seat, Shows = shows != _shows ? shows : null,
        };
        _shows = shows;
        while (_heard.TryDequeue(out var h))
            if (Keep(b, h) is { } line && m.Said.Count < 4) m.Said.Add(line);
        r.Moments.Add(m);
        _nextMoment = now + MomentEvery;
        if (!_looking)
        {
            _looking = true;
            var task = _around(b, compass);
            task.ContinueWith(t => _post(() =>
            {
                _looking = false;
                // (what she rides is left out by its parts; mid-crossing they aren't known, so by its name too)
                if (t.IsCompletedSuccessfully) m.Around = seat == null ? t.Result : t.Result.Where(x => x.Name != seat).ToList();
            }), TaskScheduler.Default);
        }
        return m;
    }

    void Shoot(Ride r, Moment m, double compass, float climb, float speed, long now)
    {
        _shooting = true;
        // the way she's going, tipped as it climbs or falls (a slide's drop), a little down as eyes are, within reason
        double yaw = (90 - compass) * Math.PI / 180;
        double pitch = Math.Clamp(speed > 0.5f ? Math.Atan2(climb, speed) : 0, -35 * Math.PI / 180, 20 * Math.PI / 180) - 7 * Math.PI / 180;
        var fwd = new Vector3((float)(Math.Cos(yaw) * Math.Cos(pitch)), (float)(Math.Sin(yaw) * Math.Cos(pitch)), (float)Math.Sin(pitch));
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _picture(fwd).ContinueWith(t => _post(() =>
        {
            _shooting = false;
            long took = (long)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            r.LastPictureMs = took;
            _interval = Math.Clamp((long)(took * SlowerThanDrawing), _floor, Math.Max(_floor, LongestInterval));
            if (!t.IsCompletedSuccessfully || t.Result is not { } pic || pic["jpeg"] is null) return;
            m.Picture = pic;
            r.Taken++;
            if (r.Pictures > MaxPictures) Thin(r);
        }), TaskScheduler.Default);
        _nextPicture = now + _interval;
    }

    /// <summary>Past the most kept: every other picture goes (the first and the newest stay), and they come half as often.</summary>
    void Thin(Ride r)
    {
        var with = r.Moments.Where(x => x.Picture != null).ToList();
        for (int i = 1; i < with.Count - 1; i += 2) { with[i].Picture = null; r.Thinned++; }
        _floor = Math.Min(_floor * 2, LongestInterval * 2);
        _interval = Math.Max(_interval, _floor);
    }

    // --- what objects said -------------------------------------------------------------------------------------------

    readonly record struct Heard(UUID Source, UUID Owner, string Name, string Text, Vector3 At, bool ToHer);
    readonly ConcurrentQueue<Heard> _heard = new();
    readonly ConditionalWeakTable<GridClient, object> _hooked = new();

    void Hook(GridClient c)
    {
        lock (_hooked)
        {
            if (_hooked.TryGetValue(c, out _)) return;
            _hooked.Add(c, new object());
        }
        // objects only, never people's chat: a ride's own announcements, not anyone's conversation
        c.Self.ChatFromSimulator += (_, e) =>
        {
            if (_ride == null || e.SourceType != ChatSourceType.Object || e.Type is ChatType.StartTyping or ChatType.StopTyping or ChatType.Debug
                || string.IsNullOrWhiteSpace(e.Message) || _heard.Count > 50) return;
            _heard.Enqueue(new Heard(e.SourceID, e.OwnerID, e.FromName ?? "", e.Message, new Vector3(e.Position.X, e.Position.Y, e.Position.Z), e.Type == ChatType.OwnerSay));
        };
        c.Self.IM += (_, e) =>
        {
            if (_ride == null || e.IM.Dialog != InstantMessageDialog.MessageFromObject || string.IsNullOrWhiteSpace(e.IM.Message) || _heard.Count > 50) return;
            _heard.Enqueue(new Heard(UUID.Zero, e.IM.FromAgentID, e.IM.FromAgentName ?? "", e.IM.Message, default, true));
        };
    }

    /// <summary>A line kept for the ride: the seat's (its linkset or its name), her own things', one said to her alone, or
    /// any object's within 20 m; "Name: words", cut at 160 characters.</summary>
    string? Keep(WalkMode.Body b, Heard h)
    {
        bool seat = S.Region?.PrimById(h.Source) is { } p && b.Linkset?.Contains(p.LocalID) == true
                    || b.SeatName is { } n && h.Name.Trim().Equals(n.Trim(), StringComparison.OrdinalIgnoreCase);
        bool mine = S.Client is { } c && h.Owner == c.Self.AgentID;
        bool near = h.At != default && Vector3.Distance(h.At, b.Pos) <= Near;
        if (!(seat || mine || h.ToHer || near)) return null;
        var text = h.Text.Replace('\n', ' ').Trim();
        if (text.Length > 160) text = text[..160] + "...";
        return $"{(h.Name.Length > 0 ? h.Name : "an unnamed object")}{(seat ? " (what you're riding)" : h.ToHer ? " (to you)" : "")}: {text}";
    }

    // --- told ----------------------------------------------------------------------------------------------------------

    public static string Length(long ms)
    {
        long s = (ms + 500) / 1000;
        if (s < 60) return s == 1 ? "1 second" : $"{s} seconds";
        long min = s / 60, sec = s % 60;
        var m = min == 1 ? "1 minute" : $"{min} minutes";
        return sec == 0 ? m : $"{m} {(sec == 1 ? "1 second" : $"{sec} seconds")}";
    }

    static string Clock(long ms) => $"{ms / 60000}:{ms / 1000 % 60:00}";

    /// <summary>Which moments a telling gets: all up to <see cref="MomentsTold"/>; past that the first and the last,
    /// those with a picture sent, those where the region or what she rides changed, and others evenly between.</summary>
    static SortedSet<int> MomentsForTelling(Ride r, HashSet<int> pictures)
    {
        int n = r.Moments.Count;
        var keep = new SortedSet<int>();
        if (n <= MomentsTold)
        {
            for (int i = 0; i < n; i++) keep.Add(i);
            return keep;
        }
        keep.Add(0);
        keep.Add(n - 1);
        keep.UnionWith(pictures);
        for (int i = 1; i < n; i++)
            if (r.Moments[i].Region != r.Moments[i - 1].Region || r.Moments[i].Seat != r.Moments[i - 1].Seat) keep.Add(i);
        int room = MomentsTold - keep.Count;
        for (int k = 1; k <= room; k++) keep.Add((int)Math.Round(k * (n - 1) / (double)(room + 1)));
        return keep;
    }

    /// <summary>The ride for the describer (SojournRide get): its moments in order, each with its picture where it has
    /// one (brief: at most <see cref="BriefPictures"/>, or <see cref="BriefPicturesLong"/> past five minutes, evenly
    /// through the ride; rich: all). A long ride's moments are thinned to <see cref="MomentsTold"/>, each one left out
    /// folded into the next sent (the named things near, objects' lines, what the seat came to show), so nothing passed
    /// goes untold and the request stays about a five-minute ride's size.</summary>
    public static JsonObject ForTelling(Ride r, bool rich)
    {
        var with = r.Moments.Select((m, i) => (m, i)).Where(x => x.m.Picture != null).Select(x => x.i).ToList();
        var send = new HashSet<int>(with);
        int brief = r.Ms > 5 * 60_000 ? BriefPicturesLong : BriefPictures;
        if (!rich && with.Count > brief)
        {
            send.Clear();
            for (int k = 0; k < brief; k++) send.Add(with[(int)Math.Round(k * (with.Count - 1) / (double)(brief - 1))]);
        }
        var told = MomentsForTelling(r, send);
        var start = r.Moments.FirstOrDefault();
        var moments = new JsonArray();
        string? region = null, seat = null;
        // what the moments left out since the last one sent, for the next
        var nearSince = new List<Thing>();
        var saidSince = new List<string>();
        string? showsSince = null;
        for (int i = 0; i < r.Moments.Count; i++)
        {
            var m = r.Moments[i];
            if (!told.Contains(i))
            {
                if (m.Around != null) nearSince.AddRange(m.Around);
                saidSince.AddRange(m.Said);
                showsSince = m.Shows ?? showsSince;
                continue;
            }
            var near = nearSince.Concat(m.Around ?? new()).DistinctBy(t => t.Name).Take(8).ToList();
            var said = saidSince.Concat(m.Said).Distinct().ToList();
            if (said.Count > 6) said = said.Skip(said.Count - 6).ToList();
            var shows = m.Shows ?? showsSince;
            (nearSince, saidSince, showsSince) = (new(), new(), null);
            var o = new JsonObject { ["at"] = Clock(m.Ms) };
            // the region and the seat only when they change
            if (m.Region != region) o["region"] = region = m.Region;
            o["speed_m_s"] = Math.Round(m.Speed, 1);
            o["going"] = Bearings.Point(m.Compass);
            o["height_m"] = Math.Round(m.Pos.Z, 1);
            if (Math.Abs(m.Climb) >= 0.5f) o["climb_m_s"] = Math.Round(m.Climb, 1);
            if (start != null) o["from_start_m"] = Math.Round(Vector3.Distance(m.Global, start.Global));
            if (m.Seat != seat) o["riding"] = (seat = m.Seat) ?? "nothing now: standing";
            if (shows != null) o["it_shows"] = shows;
            if (near.Count > 0) o["near"] = new JsonArray(near.Select(t => (JsonNode?)$"{t.Name}, {t.Where}").ToArray());
            if (said.Count > 0) o["said"] = new JsonArray(said.Select(x => (JsonNode?)x).ToArray());
            if (m.Picture != null && send.Contains(i)) o["picture"] = m.Picture.DeepClone();
            moments.Add(o);
        }
        var seats = r.Moments.Select(m => m.Seat).OfType<string>().Distinct().ToList();
        return new JsonObject
        {
            ["id"] = r.Id,
            ["ride"] = new JsonObject
            {
                ["riding"] = seats.Count > 0 ? string.Join(", then ", seats) : "nothing: on foot or carried",
                ["began_in"] = r.Began, ["length"] = Length(r.Ms),
                ["ended"] = r.Ended ?? "still recording",
                ["pictures"] = $"{send.Count} of {with.Count} kept" + (r.Thinned > 0 ? " (a long ride: fewer kept toward the start)" : "")
                               + (with.Count == 0 ? ": none, only what was known along the way" : ""),
                ["moments_told"] = told.Count < r.Moments.Count
                    ? $"{told.Count} of {r.Moments.Count}: a long ride, so each with what was near and said since the one before"
                    : $"all {r.Moments.Count}, every few seconds",
                ["moments"] = moments,
            },
        };
    }

    /// <summary>What the recording knows in words, with no model: how long, on what, how far and fast, the named things
    /// passed in order.</summary>
    public static string Summary(Ride r)
    {
        if (r.Moments.Count == 0) return "Nothing recorded.";
        var first = r.Moments[0];
        var last = r.Moments[^1];
        float dist = 0;
        for (int i = 1; i < r.Moments.Count; i++) dist += Vector3.Distance(r.Moments[i].Global, r.Moments[i - 1].Global) is var d && d < 100 ? d : 0;
        var seats = r.Moments.Select(m => m.Seat).OfType<string>().Distinct().ToList();
        var passed = new List<string>();
        foreach (var m in r.Moments)
            foreach (var t in m.Around ?? new())
                if (!passed.Contains(t.Name)) passed.Add(t.Name);
        float top = r.Moments.Max(m => m.Speed);
        float drop = last.Pos.Z - first.Pos.Z;
        var parts = new List<string>
        {
            $"{Length(r.Ms)}{(seats.Count > 0 ? $" on {string.Join(", then ", seats)}" : "")}, from {first.Region}"
            + (last.Region != first.Region ? $" to {last.Region}" : ""),
            $"{Bearings.Metres(dist)} travelled, at most {WalkMode.SpeedWords(top)}"
            + (Math.Abs(drop) >= 2 ? $", ending {Bearings.Metres(Math.Abs(drop))} {(drop < 0 ? "lower" : "higher")} than it began" : ""),
        };
        if (passed.Count > 0) parts.Add("passing " + string.Join(", ", passed.Take(12)) + (passed.Count > 12 ? $" and {passed.Count - 12} more" : ""));
        int said = r.Moments.Sum(m => m.Said.Count);
        if (said > 0) parts.Add($"{said} line{(said == 1 ? "" : "s")} said by objects");
        return string.Join("; ", parts) + ".";
    }
}
