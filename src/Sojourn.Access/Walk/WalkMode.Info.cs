using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Render.World;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>Walk mode's keys that tell rather than move: heading, speed, where, what's ahead, the sonar sweep, touching
/// what's ahead, doors nearby; and a seated border crossing said (whether the seat held).</summary>
public sealed partial class WalkMode
{
    /// <summary>H: "Facing northeast, 45 degrees." (Second Life's degrees, 0 east, counter-clockwise, as //face takes them).</summary>
    public void Heading()
    {
        if (Me() is not { } b) { Line("Not in a region yet."); return; }
        double compass = Compass(b.Sitting ? b.Yaw : (Held || _wasHeld ? _yaw : b.Yaw));
        Line($"Facing {Bearings.Point(compass)}, {Bearings.ToSl(compass)} degrees.");
    }

    /// <summary>V: how fast and which way: "Walking, 3 m a second, to the north." Seated: the vehicle's.</summary>
    public void SpeedLine()
    {
        if (Me() is not { } b) { Line("Not in a region yet."); return; }
        float speed = Speed(b);
        double heading = Bearings.Compass(0, 0, b.Vel.X, b.Vel.Y);
        string what = b.Sitting ? (b.SeatName ?? "What you're on") : b.Flying ? "Flying" : speed > 4.2f ? "Running" : "Walking";
        // seated: what the vehicle itself shows, after (the blind tester's train has its speed and throttle as floating text)
        var shows = b.Sitting && SeatText(b) is { } t ? $" It shows: {t}" : "";
        if (speed < 0.3f)
        {
            float climb = b.Vel.Z;
            Line((b.Sitting ? $"{what} isn't moving." : b.Flying ? (Math.Abs(climb) > 0.3f ? $"Flying {(climb > 0 ? "up" : "down")}." : "Hovering.") : "Standing still.") + shows);
            return;
        }
        Line($"{what}{(b.Sitting ? ":" : ",")} {SpeedWords(speed)}, to the {Bearings.Point(heading)}.{shows}");
    }

    /// <summary>Shift+V (and `//text` with no name): what the seat shows as floating text, alone.</summary>
    public void SeatTextLine()
    {
        if (Me() is not { } b) { Line("Not in a region yet."); return; }
        if (!b.Sitting) { Line("Not seated. //text NAME reads a thing's floating text."); return; }
        var what = b.SeatName ?? "What you're on";
        Line(SeatText(b) is { } t ? $"{what} shows: {t}" : $"{what} shows no floating text.");
    }

    /// <summary>The floating text of the seat's linkset (llSetText on any of its parts), root first, each part's lines
    /// joined as a sentence ("Throttle 60%, Speed 14 km/h."), parts apart by semicolons; null when none shows any.</summary>
    public string? SeatText(Body b)
    {
        if (!b.Sitting || b.Linkset is not { } set || S.Region is not { } w) return null;
        var parts = new List<(bool Root, uint Id, string Text)>();
        foreach (var id in set)
            if (w.Prims.TryGetValue(id, out var p) && FloatingText(p.Text) is { } t) parts.Add((p.ParentID == 0, id, t));
        if (parts.Count == 0) return null;
        return Sentence(string.Join("; ", parts.OrderByDescending(x => x.Root).ThenBy(x => x.Id).Select(x => x.Text).Distinct()));
    }

    /// <summary>A part's floating text as one line: its lines trimmed and joined by commas; null when there's none.</summary>
    public static string? FloatingText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimEnd(',', ';')).Where(l => l.Length > 0).ToList();
        return lines.Count == 0 ? null : string.Join(", ", lines);
    }

    /// <summary>Ends with a full stop unless it ends a sentence already.</summary>
    public static string Sentence(string s) => s.Length > 0 && s[^1] is '.' or '!' or '?' ? s : s + ".";

    /// <summary>L: the region, the position, the surface underfoot.</summary>
    public void WhereLine()
    {
        if (Me() is not { } b) { Line("Not in a region yet."); return; }
        var on = b.Sitting ? $", seated on {b.SeatName ?? "something"}" : b.Flying ? ", flying" : _surface != null ? $", on {_surface}" : "";
        Line(string.Create(CultureInfo.InvariantCulture, $"{b.RegionName}, {Math.Round(b.Pos.X):0}, {Math.Round(b.Pos.Y):0}, {Math.Round(b.Pos.Z):0}{on}."));
    }

    bool RaysReady()
    {
        if (_rays.Ready) return true;
        Line("Not in a region yet.");
        return false;
    }

    /// <summary>A: the first thing straight ahead within 30 m, at knee, waist or chest height.</summary>
    public async Task AheadAsync()
    {
        if (Me() is not { } b || !RaysReady()) return;
        float yaw = b.Sitting ? b.Yaw : (Held || _wasHeld ? _yaw : b.Yaw);
        var (lo, hi) = Heights(b);
        var rays = new[] { lo, (lo + hi) / 2, hi }.Select(z => (new Vector3(b.Pos.X, b.Pos.Y, z), Dir(yaw), 30f)).ToList();
        var got = await _rays.CollideManyAsync(rays, b.Linkset);
        if (got is not { } batch) { Line("Not in a region yet."); return; }
        var hit = batch.Hits.Aggregate((CollideHit?)null, Nearer);
        Line(hit is { } h ? $"{Obstacle(h)}, {Bearings.Metres(h.Distance)} ahead." : "Clear for 30 metres ahead.");
    }

    (float Lo, float Hi) Heights(Body b) => b.Sitting ? (b.Pos.Z - 0.2f, b.Pos.Z + 0.6f) : (b.Feet + 0.5f, b.Feet + 1.3f);

    const int SweepRays = 360;
    const float SweepRange = 15f;

    /// <summary>S: the sonar. Rays all around at knee and chest height; the words (walls on four sides, openings by
    /// clock face, named doors near) said at once, and the sweep heard: a ping from each hour of the clock, starting
    /// ahead and going round to the right, higher the nearer the wall, a breath where it's open; then a chime from each
    /// opening.</summary>
    public async Task SweepAsync()
    {
        if (Me() is not { } b || !RaysReady()) return;
        float yaw = b.Sitting ? b.Yaw : (Held || _wasHeld ? _yaw : b.Yaw);
        var (lo, hi) = Heights(b);
        var rays = new List<(Vector3, Vector3, float)>(SweepRays * 2);
        foreach (var z in new[] { lo, hi })
            for (int i = 0; i < SweepRays; i++)
                rays.Add((new Vector3(b.Pos.X, b.Pos.Y, z), Dir(yaw + i * 2 * MathF.PI / SweepRays), SweepRange));
        var got = await _rays.CollideManyAsync(rays, b.Linkset);
        if (got is not { } batch) { Line("Not in a region yet."); return; }
        var d = new double[SweepRays];
        for (int i = 0; i < SweepRays; i++)
        {
            var hit = Nearer(batch.Hits[i], batch.Hits[i + SweepRays]);
            d[i] = hit is { } h ? h.Distance : double.PositiveInfinity;
        }
        LastSweep = d;
        var gaps = Sonar.Gaps(d);
        if (gaps.Count > 0)
        {
            // each opening's edges found finer: a gap seen at a slant spans few rays
            var bearings = Sonar.RefineBearings(gaps, SweepRays).ToList();
            var sub = new List<(Vector3, Vector3, float)>(bearings.Count * 2);
            foreach (var z in new[] { lo, hi })
                foreach (var a in bearings) sub.Add((new Vector3(b.Pos.X, b.Pos.Y, z), Dir(yaw + (float)a), SweepRange));
            if (await _rays.CollideManyAsync(sub, b.Linkset) is { } fine)
            {
                var sd = new double[bearings.Count];
                for (int i = 0; i < sd.Length; i++)
                    sd[i] = Nearer(fine.Hits[i], fine.Hits[i + sd.Length]) is { } h ? h.Distance : double.PositiveInfinity;
                gaps = Sonar.Refine(d, gaps, sd);
            }
        }
        LastGaps = gaps;
        var words = Sonar.Describe(d, SweepRange, gaps);
        var doors = await DoorsAsync(b, 10);
        if (doors.Count > 0) words += " " + string.Join(" ", doors.Take(2).Select(x => $"{x.Name} at {x.Clock}, {Bearings.Metres(x.Dist)}."));
        if (_set.WalkText("sonar")) Line(words);
        if (!_set.WalkSound("sonar")) return;
        float level = _set.WalkLevel("sonar");
        for (int hour = 0; hour < 12; hour++)
        {
            double bearing = -hour * Math.PI / 6;   // clockwise from ahead
            double near = Sonar.Nearest(d, bearing, 15 * Math.PI / 180);
            var ping = !double.IsFinite(near) ? CueSounds.Cue.PingOpen : near < 2.5 ? CueSounds.Cue.PingNear : near < 7 ? CueSounds.Cue.PingMid : CueSounds.Cue.PingFar;
            Sounds.Play(ping, level, Around(b, yaw + (float)bearing));
            await Task.Delay(80);
        }
        foreach (var g in gaps.Where(g => g.Width >= 0.45).OrderBy(g => g.Distance).Take(3))
        {
            await Task.Delay(220);
            Sounds.Play(CueSounds.Cue.Gap, level, Around(b, yaw + (float)g.Bearing));
        }
    }

    /// <summary>The last sweep's distances and openings (the status op and tests).</summary>
    public double[]? LastSweep { get; private set; }
    public IReadOnlyList<Sonar.Gap>? LastGaps { get; private set; }

    /// <summary>T: touches the nearest thing straight ahead within 3 m (a door opens, a sign answers), at the spot the ray
    /// met, as the person's own touch.</summary>
    public async Task TouchAheadAsync()
    {
        if (Me() is not { } b || !RaysReady()) return;
        float yaw = b.Sitting ? b.Yaw : (Held || _wasHeld ? _yaw : b.Yaw);
        var (lo, hi) = Heights(b);
        var rays = new List<(Vector3, Vector3, float)>();
        foreach (var z in new[] { lo, (lo + hi) / 2, hi })
            foreach (var off in new[] { 0f, 0.35f, -0.35f })
                rays.Add((new Vector3(b.Pos.X, b.Pos.Y, z), Dir(yaw + off), 3f));
        var got = await _rays.CollideManyAsync(rays, b.Linkset);
        var hit = got?.Hits.Select((h, i) => (h, i)).Where(x => x.h is { Avatar: false }).OrderBy(x => x.h!.Value.Distance).FirstOrDefault();
        if (hit?.h is not { } h || S.Region is not { } w || !w.Prims.TryGetValue(h.LocalId, out var prim)) { Line("Nothing to touch within 3 metres ahead."); return; }
        var (from, dir, _) = rays[hit.Value.i];
        var at = from + System.Numerics.Vector3.Normalize(dir) * h.Distance;
        var name = Obstacle(h);
        var r = await _ops.DoAsync("LLAgent", "requestTouch", new JsonObject
        {
            ["obj_uuid"] = prim.ID.ToString(), ["at"] = new JsonArray((double)at.X, (double)at.Y, (double)at.Z),
        });
        Line(PersonOps.Error(r) is { } e ? $"Couldn't touch {name}: {e}" : $"Touched {name}.");
        if (PersonOps.Error(r) is null && name is not ("Wall" or "Something")) Touched?.Invoke(name);
    }

    /// <summary>D: things called door or gate within 15 m, nearest first.</summary>
    public async Task NearestDoorAsync()
    {
        if (Me() is not { } b) return;
        var doors = await DoorsAsync(b, 15);
        Line(doors.Count == 0 ? "No doors or gates by name within 15 metres." : string.Join(" ", doors.Take(3).Select(x => $"{x.Name}, {Bearings.Metres(x.Dist)}, {x.Clock}.")));
    }

    async Task<List<(string Name, double Dist, string Clock)>> DoorsAsync(Body b, double within)
    {
        var found = new List<(string, double, string)>();
        foreach (var word in new[] { "door", "gate" })
            foreach (var t in await _ops.ObjectsAsync(within, 6, word))
            {
                if (!IsDoor(t.Name)) continue;
                var rel = Bearings.Compass(b.Pos.X, b.Pos.Y, t.Pos.X, t.Pos.Y) - Compass(b.Yaw);
                found.Add((t.Name, t.Dist, Bearings.Clock(rel)));
            }
        return found.DistinctBy(x => x.Item1 + x.Item2.ToString("0.0")).OrderBy(x => x.Item2).ToList();
    }

    // --- a seated crossing --------------------------------------------------------------------------------------

    string? _region;
    bool _seated;
    string? _seatName;
    (string Region, long At)? _crossed;

    /// <summary>Crossing a border seated (a vehicle): said with whether the seat held, once the new region has sent the
    /// avatar, and the seat it sits on ("Now in Bay City, still on Kart."). The window's own "Now in X." leaves seated
    /// crossings to this. (Judged 2.5 s after the crossing whatever had come, it said "the crossing left you standing"
    /// seven times on the blind tester's ride of 2026-11-02, each time a second or three before the avatar arrived seated.)</summary>
    public bool SeatedRecently => _seated || _crossed != null;

    /// <summary>Seated before and the region can't say yet whether still (a crossing's first seconds, or the seat not
    /// sent yet): not standing up. The ride recorder holds the seat over it.</summary>
    // (the region changing this tick counts before Crossing has seen it: driving after someone ticks first, and the new
    // region's first word can have her standing a moment, which ended a convoy as "off your seat" mid-crossing, 2026-11-09)
    public bool SeatInDoubt(Body b) => !b.Sitting && (_crossed != null || _seated && (_region != null && b.RegionName != _region || SeatKnown() == null));
    /// <summary>The seat as last known (kept over a crossing).</summary>
    public string? LastSeatName => _seatName;

    /// <summary>A line for the nearby chat history, to copy from: a seated crossing's (written whether or not it's read
    /// out) and whatever's said of a rail switch (the tester, explaining one to her partner, 2026-11-04).</summary>
    public event Action<string>? ForHistory;

    void Crossing(Body b, long now)
    {
        if (_region != null && b.RegionName != _region && _seated) _crossed = (b.RegionName, now);
        bool? seated = b.Sitting ? true : _crossed != null || _seated ? SeatKnown() : false;
        if (b.Sitting) NoteSeat();
        bool judged = false;
        if (_crossed is { } x && now - x.At > 2500 && (seated != null || now - x.At > 15_000))
        {
            _crossed = null;
            judged = true;
            var line = seated == true ? $"Now in {x.Region}, still on {b.SeatName ?? _seatName ?? "your seat"}."
                : seated == false ? $"Now in {x.Region}; the crossing left you standing."
                : $"Now in {x.Region}; it hasn't said yet whether you're still seated.";
            Log($"seated crossing into {x.Region}: {(seated == true ? "seated" : seated == false ? "standing" : "not known")} after {now - x.At} ms");
            if (seated == null)
            {
                _unsure = (x.Region, now);
                S.Log.LogInformation("seated crossing into {Region}: not known after {Ms} ms: {Where}", x.Region, now - x.At, SeatWhere());
            }
            if (_set.WalkText("vehicle")) { if (seated == true) ByItself(line); else Line(line); }
            ForHistory?.Invoke(line);
        }
        else if (_unsure is { } u)
        {
            // what the region says after "it hasn't said yet", once it does: unsaid, she was left wondering (2026-11-11)
            if (b.RegionName != u.Region) _unsure = null;
            else if (seated != null)
            {
                _unsure = null;
                judged = true;
                var seat = (seated == true ? b.SeatName : null) ?? _seatName ?? "your seat";   // (on something else by then: that)
                var line = seated == true ? $"{u.Region} has you on {seat} now." : $"{u.Region} has you standing now, off {seat}.";
                S.Log.LogInformation("seated crossing into {Region}: {Seated} {Ms} ms after it wasn't known", u.Region, seated == true ? "seated" : "standing", now - u.At);
                if (_set.WalkText("vehicle")) Line(line);
                ForHistory?.Invoke(line);
            }
            else if (now - u.At > 120_000)
            {
                _unsure = null;
                S.Log.LogInformation("seated crossing into {Region}: still not known 2 minutes on: {Where}", u.Region, SeatWhere());
            }
        }
        if (b.Sitting && Speed(b) > 1f) _seatMovingAt = now;
        _region = b.RegionName;
        // (not while the region hasn't said: a moment without the avatar or its seat isn't standing up)
        if (_crossed == null && seated != null)
        {
            // stood up off something moving, not at a crossing: the region left her behind (the tester's train crossed
            // out of Bhaga without her on 2026-11-02, and only its own chat said so), or she stood herself
            if (_seated && seated == false && !judged && now - _seatMovingAt < 5000 && _set.WalkText("vehicle"))
            {
                Log($"off {_seatName ?? "the seat"} while it was moving, in {b.RegionName}");
                Line($"You're off {_seatName ?? "your seat"}, while it was moving.");
            }
            _seated = seated.Value;
            if (b.Sitting) _seatName = b.SeatName ?? _seatName;
        }
    }

    long _seatMovingAt = long.MinValue / 2;
    (string Region, long At)? _unsure;   // a seated crossing said as not known yet, in that region
    uint _seatLocal;
    UUID _seatRoot;                      // the seat's linkset's root as last seen sat on, for the log

    void NoteSeat()
    {
        if (S.Client is not { } c || S.Region is not { } w || w.AvatarById(c.Self.AgentID) is not { ParentID: not 0 } av || av.ParentID == _seatLocal) return;
        if (!w.Prims.TryGetValue(av.ParentID, out var seat)) return;
        _seatLocal = av.ParentID;
        _seatRoot = S.Objects.RootOf(w, seat).ID;
    }

    /// <summary>For the log when a seated crossing isn't known 15 s on: what the region has of her avatar, and where the
    /// seat is now. (A tour car's crossing on 2026-11-11 left the log unable to say whether the car stayed behind, was
    /// gone, or the region never sent her at all.)</summary>
    string SeatWhere()
    {
        if (S.Client is not { } c || S.Region is not { } w) return "no region";
        var av = w.AvatarById(c.Self.AgentID);
        string me = av == null ? $"{w.Name} hasn't sent my avatar"
            : av.ParentID == 0 ? $"{w.Name} has me standing"
            : $"{w.Name} has me on local id {av.ParentID}, which it hasn't sent";
        string seat = _seatRoot == UUID.Zero ? "the seat unknown"
            : S.Objects.FindAnywhere(_seatRoot) is var (sw, sp) ? $"{_seatName ?? "the seat"} in {sw.Name} at {Where(sw.RegionPosition(sp))}"
            : $"{_seatName ?? "the seat"} ({_seatRoot}) in no region heard";
        return $"{me}; {seat}";
        static string Where(LibreMetaverse.Vector3 p) => string.Create(CultureInfo.InvariantCulture, $"{p.X:F0}, {p.Y:F0}, {p.Z:F0}");
    }

    /// <summary>Seated (true) or standing (false) as the current region has the avatar, or null while it can't say: the
    /// avatar not sent yet (a crossing's first seconds), or sent sitting on something that hasn't come yet.</summary>
    bool? SeatKnown()
    {
        if (S.Client is not { } c || S.Region is not { } w || w.AvatarById(c.Self.AgentID) is not { } av) return null;
        return av.ParentID == 0 ? false : w.Prims.ContainsKey(av.ParentID) ? true : null;
    }
}
