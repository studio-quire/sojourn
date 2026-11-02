using System.Globalization;
using LibreMetaverse;
using Sojourn.Session.Collision;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>
/// Routes recorded by walking them (M3; docs/access-app.md > Walk mode > Recording a route): while it records, the walk
/// becomes a route of Radegast's `//go xyz` lines in the form the blind tester and her partner write and share, kept
/// only where they matter, so a route played by `//route` (Commands) or pasted a line at a time into Radegast goes the
/// same way. However the avatar moves (the arrows, `//go`, a follow, a ride, a teleport), the points come from where it
/// goes:
/// - a turn or a change of level: the track kept as straight stretches, a point where the stretch from the last point
///   would stray more than 0.8 m from where she walked (heights counting half again, so the top of a stair is kept);
/// - a doorway or a narrow opening walked through: rays alongside the stretch just walked meeting something on both
///   sides within 1.1 m while the way itself was clear;
/// - a region crossed on foot: the last point before the border and the first after it;
/// - a teleport: `//go tp xyz` within the region, else `//tp` and its SLurl;
/// - sitting (`//sit NAME`, when the seat has a name) and standing; a seat that moved her more than 5 m (a ride) leaves
///   a note and a `//pause` as long as the ride took before `//stand`;
/// - things touched on the way (`//touch NAME` and a `//pause 2` for a door to swing), and notes she adds (plain lines,
///   which `//route` reads out).
/// Each `//go xyz` is in the coordinates of the region the step before it ended in, as a played route will be in that
/// region when it starts the line (so across a border they run past 256 or below 0); Z is written only where the level
/// changed by more than a metre (otherwise `//go xyz` keeps her height, as her routes do). Runs on the window's thread
/// (walk mode's tick); the doorway rays come back there. Written by Claude Opus 5.5, 2026-11-01.
/// </summary>
public sealed class RouteRecorder
{
    public enum Kind { Go, Teleport, Sit, Stand, Touch, Note }

    /// <summary>One thing in the route. Region and Pos: where she was (the region it happened in, its frame); Text: the
    /// seat, the thing touched or the note; RideSecs: for a stand after a ride, how long it took; Why: what kept a point.</summary>
    public sealed record Step(Kind Kind, ulong Region, string RegionName, Vector3 Pos, string? Text = null, int RideSecs = 0, string? Why = null);

    const float Tolerance = 0.8f;      // a stretch may stray this far from the walk before a point is kept
    const float HeightWeight = 1.5f;   // heights count half again: stairs and ramps keep their ends
    const float MinGap = 1.0f;         // no two points nearer than this
    const float Sample = 0.3f;         // the track: a sample each 0.3 m
    const float SideReach = 1.1f;      // a doorway: something on both sides within this of the way
    const float Jump = 12f;            // further than this between two ticks: a teleport or a region's change, not a step
    const float Crossed = 15f;         // a region changed and it settled within this of where she was: walked across

    readonly WalkMode _walk;
    readonly LocalCollision _rays;
    readonly List<Step> _steps = new();
    readonly List<Vector3> _track = new();   // since the last point, in _trackRegion's frame; [0] is the last point
    ulong _trackRegion;
    WalkMode.Body? _prev;
    (WalkMode.Body From, ulong Region, long SettleFrom)? _jump;
    bool _seated;
    long _satAt;
    Vector2 _satFrom;
    string? _seat;
    float _walked;
    Vector3? _doorFrom;
    bool _doorBusy;

    public RouteRecorder(WalkMode walk, LocalCollision rays)
    {
        _walk = walk;
        _rays = rays;
    }

    /// <summary>Where the avatar is as the region last said (WalkMode.Reported).</summary>
    WalkMode.Body? Here() => _walk.Me() is { } b ? _walk.Reported(b) : null;

    public bool On { get; private set; }
    public bool Paused { get; private set; }
    public IReadOnlyList<Step> Steps => _steps;
    public float Walked => _walked;
    /// <summary>The region it started in (a name for the route).</summary>
    public string? StartRegion => _steps.Count > 0 ? _steps[0].RegionName : null;

    /// <summary>Starts recording where the avatar is; false when it doesn't know where that is yet.</summary>
    public bool Start()
    {
        if (Here() is not { } b) return false;
        _steps.Clear();
        On = true;
        Paused = false;
        _walked = 0;
        _jump = null;
        _steps.Add(new Step(Kind.Go, b.Region, b.RegionName, b.Pos, Why: "the start"));
        _seated = false;
        if (b.Sitting) SatDown(b, Environment.TickCount64);
        Restart(b);
        return true;
    }

    public void Discard()
    {
        On = Paused = false;
        _steps.Clear();
        _track.Clear();
    }

    /// <summary>Stops sampling, keeping where she is as a point (pause, and before saving).</summary>
    public void Pause()
    {
        if (!On || Paused) return;
        if (Here() is { } b && !b.Sitting) KeepHere(b, "where you paused");
        Paused = true;
    }

    /// <summary>Goes on after a pause: from a far place (another region, or over 30 m), by a teleport, since the route
    /// can't know the way; otherwise a walk from the last point to here. The words for what it did.</summary>
    public string Resume()
    {
        if (!On || !Paused) return "";
        Paused = false;
        if (Here() is not { } b) return "";
        var last = LastPlace();
        string said = "";
        if (b.Sitting) { }
        else if (last is { } l && (l.Region != b.Region || Vector2.Distance(Global(l.Region, l.Pos), Global(b)) > 30))
        {
            _steps.Add(new Step(Kind.Teleport, b.Region, b.RegionName, b.Pos, Why: $"a teleport to {b.RegionName}"));
            said = " The route goes on from here by a teleport, as you're far from its last point.";
        }
        else if (last is { } l2 && Vector2.Distance(Global(l2.Region, l2.Pos), Global(b)) >= MinGap)
        {
            _steps.Add(new Step(Kind.Go, b.Region, b.RegionName, b.Pos, Why: "where you went on"));
            said = $" The route walks from its last point to here, {Bearings.Metres(Vector2.Distance(Global(l2.Region, l2.Pos), Global(b)))}.";
        }
        Restart(b);
        return said;
    }

    /// <summary>The last thing in the route taken back (never the start): the words for what it was, or null.</summary>
    public string? Undo()
    {
        if (_steps.Count <= 1) return null;
        var s = _steps[^1];
        _steps.RemoveAt(_steps.Count - 1);
        if (Here() is { } b)
        {
            Restart(b);
            // the stretch goes on from the last point that's left, when it's in this region
            if (LastPlace() is { } l && l.Region == b.Region) { _track.Clear(); _track.Add(l.Pos); _track.Add(b.Pos); _trackRegion = b.Region; }
        }
        return Words(s);
    }

    /// <summary>A note at the place she's at: the place kept as a point first, so a played route reads it on arriving.</summary>
    public void Note(string text)
    {
        if (!On || Here() is not { } b) return;
        if (!b.Sitting) KeepHere(b, "a note");
        var t = text.Replace("\r", " ").Replace("\n", " ").Trim().TrimStart('/').Trim();
        if (t.Length > 0) _steps.Add(new Step(Kind.Note, b.Region, b.RegionName, b.Pos, t));
    }

    /// <summary>Just a point where she is (a note left empty): false when there's one here already.</summary>
    public bool PointHere()
    {
        if (!On || Here() is not { } b || b.Sitting) return false;
        int before = _steps.Count;
        KeepHere(b, "a point you kept");
        return _steps.Count > before;
    }

    /// <summary>Something touched while recording, by the name `//touch` finds it by.</summary>
    public void Touched(string name)
    {
        if (!On || Paused || Here() is not { } b) return;
        if (!b.Sitting) KeepHere(b, "a touch");
        _steps.Add(new Step(Kind.Touch, b.Region, b.RegionName, b.Pos, name.Trim()));
        _walk.PointCue($"Step {CountSteps()}: touching {name.Trim()}.");
    }

    /// <summary>Ends the route where she is (before naming and saving).</summary>
    public void Finish()
    {
        if (!On) return;
        if (!Paused && Here() is { } b && !b.Sitting) KeepHere(b, "the end");
        Paused = true;
    }

    /// <summary>The `//` lines a played route counts as steps.</summary>
    public int CountSteps() => Lines().Count(Commands.IsCommand);

    /// <summary>The last place in the route (a point, a teleport's landing, a seat, where she stood up).</summary>
    public Step? LastPlace() => _steps.LastOrDefault(s => s.Kind is Kind.Go or Kind.Teleport or Kind.Sit or Kind.Stand);

    // --- the tick ------------------------------------------------------------------------------------------------------

    /// <summary>Walk mode's tick (50 ms), with the body as it is.</summary>
    public void Tick(WalkMode.Body b, long now)
    {
        if (!On || Paused) { _prev = null; return; }
        if (_prev is not { } p) { _prev = b; return; }
        // a region changed or a jump: wait for the position to settle (a crossing's first updates can carry the old
        // region's coordinates), then tell a crossing from a teleport by how far it really went
        if (_jump is { } j)
        {
            if (b.Region != j.Region) _jump = j with { Region = b.Region, SettleFrom = now };
            else if (now - j.SettleFrom >= 1200)
            {
                _jump = null;
                Landed(j.From, b, now);
                _prev = b;
            }
            return;
        }
        if (b.Region != p.Region || Vector2.Distance(Global(p), Global(b)) > Jump)
        {
            _jump = (p, b.Region, now);
            return;
        }
        if (b.Sitting != _seated)
        {
            if (b.Sitting) { KeepHere(p, "before sitting"); SatDown(b, now); }
            else StoodUp(b, now);
            _prev = b;
            return;
        }
        _prev = b;
        if (b.Sitting) return;
        if (_trackRegion != b.Region || _track.Count == 0) Restart(b);
        if (Vector2.Distance(Flat(_track[^1]), Flat(b.Pos)) < Sample) return;
        _walked += Vector2.Distance(Flat(_track[^1]), Flat(b.Pos));
        _track.Add(b.Pos);
        Straighten(b);
        Doorway(b);
    }

    void Landed(WalkMode.Body from, WalkMode.Body to, long now)
    {
        float far = Vector2.Distance(Global(from), Global(to));
        if (from.Sitting || to.Sitting)
        {
            // sitting down onto a seat a way off, a ride across a border, a teleport that stood her up: the seat's business
            if (!_seated && to.Sitting) { KeepHere(from, "before sitting"); SatDown(to, now); }
            else if (_seated && !to.Sitting) StoodUp(to, now);
            Restart(to);
            return;
        }
        if (far > Crossed || from.Region == to.Region)
        {
            KeepHere(from, "before the teleport");
            _steps.Add(new Step(Kind.Teleport, to.Region, to.RegionName, to.Pos, Why: $"a teleport to {to.RegionName}"));
            _walk.PointCue($"Step {CountSteps()}: a teleport to {to.RegionName}.");
        }
        else if (from.Region != to.Region)
        {
            // walked across a border: the last point on this side and the first on the other, so the stretch across is short
            KeepHere(from, $"the edge of {from.RegionName}");
            Keep(to.Region, to.RegionName, to.Pos, $"into {to.RegionName}");
        }
        Restart(to);
    }

    void SatDown(WalkMode.Body b, long now)
    {
        _seated = true;
        _satAt = now;
        _satFrom = Global(b);
        _seat = b.SeatName;
        if (_seat is { Length: > 0 } name)
        {
            _steps.Add(new Step(Kind.Sit, b.Region, b.RegionName, b.Pos, name));
            _walk.PointCue($"Step {CountSteps()}: sitting on {name}.");
        }
        else _walk.PointCue("Sitting on something with no name: the route can't sit there by name, and goes on from where you stand up.");
    }

    void StoodUp(WalkMode.Body b, long now)
    {
        _seated = false;
        int secs = (int)((now - _satAt) / 1000);
        bool ride = Vector2.Distance(_satFrom, Global(b)) > 5;
        _steps.Add(new Step(Kind.Stand, b.Region, b.RegionName, b.Pos, _seat, ride ? Math.Max(1, secs) : 0));
        _walk.PointCue($"Step {CountSteps()}: standing up.");
        Restart(b);
    }

    /// <summary>The track since the last point starts again here.</summary>
    void Restart(WalkMode.Body b)
    {
        _track.Clear();
        _track.Add(b.Pos);
        _trackRegion = b.Region;
        _doorFrom = b.Pos;
        _prev = b;
    }

    /// <summary>Where the stretch from the last point would stray too far from the walk, the farthest sample is kept.</summary>
    void Straighten(WalkMode.Body b)
    {
        var a = Scaled(_track[0]);
        var c = Scaled(_track[^1]);
        int worst = -1;
        float most = Tolerance;
        for (int i = 1; i < _track.Count - 1; i++)
        {
            float d = FromSegment(Scaled(_track[i]), a, c);
            if (d > most) { most = d; worst = i; }
        }
        if (worst < 0) return;
        var at = _track[worst];
        bool level = Math.Abs(at.Z - _track[0].Z) > 1 && FromSegment(Flat3(at), Flat3(_track[0]), Flat3(_track[^1])) < Tolerance;
        if (Keep(b.Region, b.RegionName, at, level ? "a change of level" : "a turn")) _track.RemoveRange(0, worst);
        else _track.RemoveRange(1, worst);   // (a wiggle within a metre of the last point: the stretch goes on from that point)
    }

    /// <summary>A doorway or narrow opening walked through: each metre, rays along the stretch just walked, one each side
    /// 1.1 m out and two down its middle; something on both sides and nothing in the middle is a gap she went through.</summary>
    void Doorway(WalkMode.Body b)
    {
        if (_doorBusy || !_rays.Ready || _doorFrom is not { } from) return;
        var to = b.Pos;
        var way = Flat(to) - Flat(from);
        float len = way.Length();
        if (len < 1) return;
        _doorFrom = to;
        var dir = new Vector3(way / len, 0);
        var side = new Vector3(-dir.Y, dir.X, 0);
        float feet = b.Feet;
        Vector3 At(Vector3 p, float up) => new(p.X, p.Y, feet + up);
        var rays = new List<(Vector3, Vector3, float)>
        {
            (At(from, 0.5f), dir, len), (At(from, 1.3f), dir, len),
            (At(from + side * SideReach, 1.0f), dir, len), (At(from - side * SideReach, 1.0f), dir, len),
        };
        ulong region = b.Region;
        var anchor = _track[0];
        _doorBusy = true;
        _ = Run();

        async Task Run()
        {
            try
            {
                var got = await _rays.CollideManyAsync(rays, null);
                if (got is not { } batch || batch.Region != region || !On || Paused || _trackRegion != region) return;
                var h = batch.Hits;
                if (h[0] != null || h[1] != null || h[2] is not { Avatar: false } l || h[3] is not { Avatar: false } r) return;
                // the gap itself (the region's positions can come a few metres apart), kept where no point has been
                // kept since the rays went out: the stretch then goes on from it, with the samples from the ray's end on
                int after = _track.IndexOf(to);
                if (_track[0] != anchor || after < 1) return;
                var gap = from + dir * ((l.Distance + r.Distance) / 2);
                gap.Z = from.Z + (to.Z - from.Z) * Math.Clamp(Vector2.Distance(Flat(gap), Flat(from)) / len, 0, 1);
                if (!Keep(region, b.RegionName, gap, "a doorway")) return;
                _track.RemoveRange(0, after);
                _track.Insert(0, gap);
            }
            catch (Exception) { /* a region left meanwhile: the next metre asks again */ }
            finally { _doorBusy = false; }
        }
    }

    /// <summary>The place she's at kept as a point (unless one is within a metre).</summary>
    void KeepHere(WalkMode.Body b, string why) => Keep(b.Region, b.RegionName, b.Pos, why);

    bool Keep(ulong region, string regionName, Vector3 pos, string why)
    {
        if (LastPlace() is { } l && Vector2.Distance(Global(l.Region, l.Pos), Global(region, pos)) < MinGap) return false;
        _steps.Add(new Step(Kind.Go, region, regionName, pos, Why: why));
        _walk.PointCue($"Step {CountSteps()}: {why}.");
        return true;
    }

    // --- the notecard ------------------------------------------------------------------------------------------------

    /// <summary>The route as a notecard's lines: a first line saying where it starts, then the steps.</summary>
    public List<string> Lines()
    {
        var lines = new List<string>();
        if (_steps.Count == 0) return lines;
        var s0 = _steps[0];
        lines.Add(Inv($"Starts in {s0.RegionName}, at {Math.Round(s0.Pos.X):0}, {Math.Round(s0.Pos.Y):0}, {Math.Round(s0.Pos.Z):0}."));
        ulong frame = s0.Region;
        float z = s0.Pos.Z;
        bool first = true;
        foreach (var s in _steps)
        {
            switch (s.Kind)
            {
                case Kind.Go:
                    var p = s.Pos + Offset(s.Region, frame);
                    lines.Add(!first && Math.Abs(s.Pos.Z - z) > 1
                        ? $"//go xyz {N(p.X)},{N(p.Y)},{N(p.Z)}"
                        : $"//go xyz {N(p.X)},{N(p.Y)}");
                    break;
                case Kind.Teleport:
                    lines.Add(s.Region == frame
                        ? Inv($"//go tp xyz {Math.Round(s.Pos.X):0},{Math.Round(s.Pos.Y):0},{Math.Round(s.Pos.Z):0}")
                        : $"//tp {Slurl(s.RegionName, s.Pos)}");
                    break;
                case Kind.Sit:
                    lines.Add($"//sit {s.Text}");
                    break;
                case Kind.Stand:
                    if (s.RideSecs > 0)
                    {
                        lines.Add($"Riding {s.Text ?? "a seat"} for about {Duration(s.RideSecs)}; the route waits as long, then stands up.");
                        // (//pause takes up to ten minutes a line)
                        for (int left = s.RideSecs; left > 0; left -= 600) lines.Add($"//pause {Math.Min(left, 600)}");
                    }
                    lines.Add("//stand");
                    break;
                case Kind.Touch:
                    lines.Add($"//touch {s.Text}");
                    lines.Add("//pause 2");   // (a door swinging open)
                    break;
                case Kind.Note:
                    lines.Add(s.Text!);
                    break;
            }
            first = false;
            if (s.Kind is Kind.Go or Kind.Teleport or Kind.Sit or Kind.Stand) { frame = s.Region; z = s.Pos.Z; }
        }
        return lines;
    }

    /// <summary>What a step was, for taking it back.</summary>
    public static string Words(Step s) => s.Kind switch
    {
        Kind.Go => $"the point at {s.Why ?? "a turn"}",
        Kind.Teleport => $"the teleport to {s.RegionName}",
        Kind.Sit => $"sitting on {s.Text}",
        Kind.Stand => "standing up",
        Kind.Touch => $"touching {s.Text}",
        _ => $"the note: {s.Text}",
    };

    static string Duration(int secs) => secs < 90 ? $"{secs} seconds" : $"{Math.Round(secs / 60.0):0} minutes";

    /// <summary>An SLurl (maps.secondlife.com, as SLurls are shared), which `//tp` takes and Radegast's Control+G does.</summary>
    static string Slurl(string region, Vector3 p) =>
        Inv($"http://maps.secondlife.com/secondlife/{Uri.EscapeDataString(region)}/{Math.Round(p.X):0}/{Math.Round(p.Y):0}/{Math.Round(p.Z):0}");

    static string N(float v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
    static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    // --- geometry ----------------------------------------------------------------------------------------------------

    static Vector2 Flat(Vector3 v) => new(v.X, v.Y);
    static Vector3 Flat3(Vector3 v) => new(v.X, v.Y, 0);
    static Vector3 Scaled(Vector3 v) => new(v.X, v.Y, v.Z * HeightWeight);

    static float FromSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-6f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0, 1);
        return Vector3.Distance(p, a + ab * t);
    }

    static Vector2 Global(WalkMode.Body b) => Global(b.Region, b.Pos);

    static Vector2 Global(ulong region, Vector3 p)
    {
        Utils.LongToUInts(region, out var gx, out var gy);
        return new Vector2(gx + p.X, gy + p.Y);
    }

    /// <summary>What to add to a position in one region to have it in another's frame.</summary>
    static Vector3 Offset(ulong from, ulong to)
    {
        if (from == to) return Vector3.Zero;
        Utils.LongToUInts(from, out var fx, out var fy);
        Utils.LongToUInts(to, out var tx, out var ty);
        return new Vector3((float)((double)fx - tx), (float)((double)fy - ty), 0);
    }
}
