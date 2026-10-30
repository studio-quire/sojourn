using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
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
        if (speed < 0.3f)
        {
            float climb = b.Vel.Z;
            Line(b.Sitting ? $"{what} isn't moving." : b.Flying ? (Math.Abs(climb) > 0.3f ? $"Flying {(climb > 0 ? "up" : "down")}." : "Hovering.") : "Standing still.");
            return;
        }
        Line($"{what}{(b.Sitting ? ":" : ",")} {SpeedWords(speed)}, to the {Bearings.Point(heading)}.");
    }

    /// <summary>L: the region, the position, the surface underfoot.</summary>
    public void WhereLine()
    {
        if (Me() is not { } b) { Line("Not in a region yet."); return; }
        var on = b.Sitting ? $", seated on {b.SeatName ?? "something"}" : b.Flying ? ", flying" : _surface != null ? $", on {_surface}" : "";
        Line(string.Create(CultureInfo.InvariantCulture, $"{b.RegionName}, {Math.Round(b.Pos.X):0}, {Math.Round(b.Pos.Y):0}, {Math.Round(b.Pos.Z):0}{on}."));
    }

    async Task<bool> ViewReadyAsync()
    {
        // starting, or back in a moment after a lost GPU: worth a few seconds' wait
        for (int i = 0; i < 25 && _view is { Running: false, State: "starting" or "restarting" }; i++) await Task.Delay(200);
        if (_view is { Running: true }) return true;
        Line(_view == null ? "The view out of sight is off (--no-render): nothing to tell what's around." : $"The view out of sight isn't running ({_view.State}).");
        return false;
    }

    /// <summary>A: the first thing straight ahead within 30 m, at knee, waist or chest height.</summary>
    public async Task AheadAsync()
    {
        if (Me() is not { } b || !await ViewReadyAsync()) return;
        float yaw = b.Sitting ? b.Yaw : (Held || _wasHeld ? _yaw : b.Yaw);
        var (lo, hi) = Heights(b);
        var rays = new[] { lo, (lo + hi) / 2, hi }.Select(z => (new Vector3(b.Pos.X, b.Pos.Y, z), Dir(yaw), 30f)).ToList();
        var got = await _view!.CollideManyAsync(rays, b.Linkset);
        if (got is not { } batch) { Line("The view couldn't answer."); return; }
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
        if (Me() is not { } b || !await ViewReadyAsync()) return;
        float yaw = b.Sitting ? b.Yaw : (Held || _wasHeld ? _yaw : b.Yaw);
        var (lo, hi) = Heights(b);
        var rays = new List<(Vector3, Vector3, float)>(SweepRays * 2);
        foreach (var z in new[] { lo, hi })
            for (int i = 0; i < SweepRays; i++)
                rays.Add((new Vector3(b.Pos.X, b.Pos.Y, z), Dir(yaw + i * 2 * MathF.PI / SweepRays), SweepRange));
        var got = await _view!.CollideManyAsync(rays, b.Linkset);
        if (got is not { } batch) { Line("The view couldn't answer."); return; }
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
            if (await _view!.CollideManyAsync(sub, b.Linkset) is { } fine)
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
        if (Me() is not { } b || !await ViewReadyAsync()) return;
        float yaw = b.Sitting ? b.Yaw : (Held || _wasHeld ? _yaw : b.Yaw);
        var (lo, hi) = Heights(b);
        var rays = new List<(Vector3, Vector3, float)>();
        foreach (var z in new[] { lo, (lo + hi) / 2, hi })
            foreach (var off in new[] { 0f, 0.35f, -0.35f })
                rays.Add((new Vector3(b.Pos.X, b.Pos.Y, z), Dir(yaw + off), 3f));
        var got = await _view!.CollideManyAsync(rays, b.Linkset);
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

    /// <summary>Crossing a border seated (a vehicle): said with whether the seat held, once the region has had time to
    /// say ("Now in Bay City, still on Kart."). The window's own "Now in X." leaves seated crossings to this.</summary>
    public bool SeatedRecently => _seated || _crossed != null;
    void Crossing(Body b, long now)
    {
        if (_region != null && b.RegionName != _region && _seated) _crossed = (b.RegionName, now);
        if (_crossed is { } x && now - x.At > 2500)
        {
            _crossed = null;
            if (_set.WalkText("vehicle"))
                Line(b.Sitting ? $"Now in {x.Region}, still on {b.SeatName ?? _seatName ?? "your seat"}." : $"Now in {x.Region}; the crossing left you standing.");
        }
        _region = b.RegionName;
        if (_crossed == null) { _seated = b.Sitting; if (b.Sitting) _seatName = b.SeatName ?? _seatName; }
    }
}
