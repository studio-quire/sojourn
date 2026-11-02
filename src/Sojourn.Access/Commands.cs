using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// Radegast's `//` commands, with its syntax, so routes kept in notecards and on blogs run here as they are (Radegast's
/// Core/Commands, from ScriptAble's Guide; docs/radegast.md has them all): go, find, face, sit, stand (unsit),
/// follow, tp, status, help. Not Radegast's bug: `//face object` and `//find object` search for the name alone, not
/// the whole argument. Directions come back relative to the person (a clock face from their facing, metres); degrees
/// are Second Life's (0 east, counter-clockwise), as in Radegast. Each answer goes to the reader and the chat history.
/// </summary>
public sealed class Commands
{
    readonly PersonOps _ops;
    readonly Action<string> _say;
    readonly AgentSession _s;

    /// <summary>A teleport this app asked for is under way (the arrival is said by the command, not twice).</summary>
    public volatile bool OwnTeleport;
    long _ownEndedAt;

    /// <summary>This app's teleport is under way or just ended (its arrival event may come after the answer).</summary>
    public bool TeleportingSelf => OwnTeleport || Environment.TickCount64 - Interlocked.Read(ref _ownEndedAt) < 4000;

    void OwnTeleportDone() { OwnTeleport = false; Interlocked.Exchange(ref _ownEndedAt, Environment.TickCount64); }

    /// <param name="say">An answer: read out and put in the nearby chat history.</param>
    public Commands(PersonOps ops, Action<string> say)
    {
        _ops = ops;
        _say = say;
        _s = ops.Session;
    }

    public static bool IsCommand(string text) => text.TrimStart().StartsWith("//", StringComparison.Ordinal);

    /// <summary>A `//` line; true when it did what it says (a walk got there, a teleport arrived), which a route waits
    /// for before its next line.</summary>
    public async Task<bool> RunAsync(string line)
    {
        var words = Split(line.TrimStart()[2..]);
        if (words.Count == 0) { Help(""); return false; }
        var verb = words[0].ToLowerInvariant();
        var rest = words.Skip(1).ToList();
        try
        {
            switch (verb)
            {
                case "help": Help(string.Join(" ", rest)); return true;
                case "about" or "version": _say(About?.Invoke() ?? "Sojourn Access."); return true;
                case "describe": if (Describe != null) await Describe(string.Join(" ", rest)); return true;
                case "go": return await GoAsync(rest);
                case "find": await FindAsync(rest); return true;
                case "face": await FaceAsync(rest); return true;
                case "sit": await SitAsync(rest); return true;
                case "stand" or "unsit": await StandAsync(); return true;
                case "follow": await FollowAsync(rest); return true;
                case "tp": return await TeleportAsync(rest);
                case "status": await StatusAsync(rest); return true;
                case "route": await RouteAsync(rest); return true;
                case "record": if (Record != null) await Record(AfterVerb(line)); return true;
                case "pause": return await PauseAsync(rest);
                case "siminfo": await SimInfoAsync(); return true;
                case "parcelinfo": await ParcelInfoAsync(); return true;
                case "scriptinfo": await ScriptInfoAsync(); return true;
                case "beacon": await BeaconAsync(rest); return true;
                case "touch": if (Touch != null) await Touch(string.Join(" ", rest)); return true;
                case "switch": if (Switch != null) await Switch(string.Join(" ", rest)); return true;
                case "stop": if (WalkMode is { } walk) walk.RequestStop(); else _say("Not in a region yet."); return true;
                case "outfit": if (Outfit != null) await Outfit(); return true;
                case "upload": if (Upload != null) await Upload(AfterVerb(line)); return true;
                case "voice": if (Voice != null) await Voice(string.Join(" ", rest)); return true;
                case "imsound": if (ImSound != null) await ImSound(string.Join(" ", rest)); return true;
                case "sendlog": SendLog?.Invoke(); return true;
                default: _say($"No command {verb}. //help lists them."); return false;
            }
        }
        catch (Exception e) { _say($"//{verb} didn't work: {e.Message}"); return false; }
    }

    /// <summary>Radegast's parsing: words, "quoted words" as one, backslash escapes.</summary>
    static List<string> Split(string s)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '\\' && i + 1 < s.Length) { cur.Append(s[++i]); any = true; }
            else if (ch == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(ch) && !quoted) { if (any) list.Add(cur.ToString()); cur.Clear(); any = false; }
            else { cur.Append(ch); any = true; }
        }
        if (any) list.Add(cur.ToString());
        return list;
    }

    static readonly (string Name, string Usage, string Desc)[] All =
    {
        ("go", "//go [tp] N [direction] | //go [tp] xyz X,Y[,Z] | //go [tp] person NAME | //go [tp] object NAME",
            "walk (or with tp, teleport) N metres ahead or toward a direction (N, NE, ENE... or left, right, back), to region coordinates, or to someone or something"),
        ("find", "//find person NAME | //find object NAME", "where someone or something is"),
        ("describe", "//describe | //describe me | //describe NAME | //describe person NAME | //describe object NAME | //describe more",
            "the assistant describes where you are, you and what you're wearing, someone or something; more: the last one in detail"),
        ("face", "//face DEGREES | //face DIRECTION | //face person NAME | //face object NAME", "turn: degrees with 0 east, counter-clockwise, as in Second Life"),
        ("sit", "//sit NAME | //sit ground", "sit on the nearest thing with that name, or on the ground"),
        ("stand", "//stand (or //unsit)", "stand up"),
        ("follow", "//follow NAME | //follow stop | //follow", "follow someone (ask first), stop, or say who"),
        ("tp", "//tp list | //tp N | //tp NAME | //tp Region/X/Y/Z | //tp SLURL", "teleport by landmark (Radegast Landmarks folder, else Landmarks), or to a place"),
        ("status", "//status [doing|region|parcel|location|money|time|music]", "what you're doing, where you are, your L$, the time in Second Life, the music playing"),
        ("route", "//route NOTECARD | //route (the lines on the clipboard) | //route stop | //route resume",
            "play a route: a notecard's (or the clipboard's) // lines one at a time, each walk finished before the next, other lines read as notes"),
        ("record", "//record | //record note WORDS | //record undo | //record pause | //record save [NAME] | //record discard",
            "record a route by walking it (R in walk mode): start, or while recording how it's going; a note here; take back the last point; pause or go on; save it as a notecard in Routes; or throw it away"),
        ("pause", "//pause [SECONDS]", "wait (in a route: before its next line; 1 second unless said)"),
        ("siminfo", "//siminfo", "the region: its type, maturity, people, scripts, time dilation and frame rate"),
        ("parcelinfo", "//parcelinfo", "every parcel in the region: its name, area and who may enter, the one you're on first"),
        ("scriptinfo", "//scriptinfo", "the scripts in what you're wearing, per attachment"),
        ("beacon", "//beacon NAME | //beacon off | //beacon",
            "a sound on someone or something to walk to (by hand in walk mode, or by //go), until you reach it; off; or where it is"),
        ("touch", "//touch NAME | //touch OBJECT PART | //touch NAME face top (or bottom, front, far, left, right, inside, a number)",
            "touch the nearest thing or part within 10 metres with that name (an object whose only touchable part is a child: that part), on a face if given"),
        ("switch", "//switch | //switch flip | //switch main | //switch branch",
            "the next rail switch and how it's set; throw it, or set it to the main line or the branch (walk mode's W and Shift+W)"),
        ("stop", "//stop", "stop: seated on something moving, it's stopped by its controls, as Space in walk mode; otherwise a walk, a follow or a route"),
        ("outfit", "//outfit", "what you're wearing, by name from the worn list: clothing, attachments and where, HUDs, body parts"),
        ("upload", "//upload | //upload PATH", "upload a picture, sound, animation or model: with no path, choose the file (Control+U)"),
        ("voice", "//voice | //voice answer | //voice decline", "who's in voice and speaking, your mic; or answer or decline a call ringing"),
        ("imsound", "//imsound NAME SOUND | //imsound NAME usual | //imsound NAME | //imsound",
            "a sound of its own for someone's IMs, or a group's chat, in place of the usual one; usual takes it back; with only a name, theirs; with nothing, the sounds and who has one"),
        ("about", "//about", "which version of Sojourn Access this is, and where your settings and logs are"),
        ("sendlog", "//sendlog", "send a session's log to Studio Quire, with a note on what happened; other people's words are left out"),
        ("help", "//help [word]", "the commands"),
    };

    void Help(string word)
    {
        var hits = All.Where(c => word.Length == 0 || c.Name.Contains(word, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) { _say($"No command with {word} in it."); return; }
        _say(string.Join(" ", hits.Select(c => word.Length == 0 ? $"//{c.Name}: {c.Desc}." : $"//{c.Name}: {c.Desc}. Usage: {c.Usage}.")));
    }

    // --- go -------------------------------------------------------------------------------------------------------


    async Task<bool> GoAsync(List<string> a)
    {
        bool tp = a.Count > 0 && a[0].Equals("tp", StringComparison.OrdinalIgnoreCase);
        if (tp) a = a.Skip(1).ToList();
        if (a.Count == 0 || a[0].Equals("help", StringComparison.OrdinalIgnoreCase)) { Help("go"); return false; }
        if (_ops.Me() is not { } me) { _say("Not in a region yet."); return false; }
        Vector3 target;
        string what;
        double stop = 0.6;   // the autopilot coasts a little after letting go: routes of waypoints want it close
        var kind = a[0].ToLowerInvariant();
        if (kind == "xyz")
        {
            var nums = string.Join(",", a.Skip(1)).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(n => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToList();
            if (nums.Count < 2 || nums.Any(double.IsNaN)) { _say("//go xyz takes X,Y or X,Y,Z: //go xyz 128,128,22"); return false; }
            target = new Vector3((float)nums[0], (float)nums[1], nums.Count > 2 ? (float)nums[2] : me.Pos.Z);
            what = string.Create(CultureInfo.InvariantCulture, $"{nums[0]:0.#}, {nums[1]:0.#}");
        }
        else if (kind == "person")
        {
            var name = string.Join(" ", a.Skip(1));
            if (await _ops.FindPersonAsync(name) is not { } p) { _say($"Nobody called {name} nearby."); return false; }
            target = p.Pos;
            what = p.Name;
            stop = 1.5;
        }
        else if (kind == "object")
        {
            var name = string.Join(" ", a.Skip(1));
            var things = await _ops.ObjectsAsync(96, 5, name);
            if (things.FirstOrDefault() is not { } t) { _say($"No object called {name} nearby."); return false; }
            target = t.Pos;
            what = t.Name;
            stop = 1.5;
        }
        else if (double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var metres))
        {
            double bearing = me.Heading;
            if (a.Count > 1)
            {
                var dir = string.Join(" ", a.Skip(1)).ToLowerInvariant();
                double? b = dir switch
                {
                    "ahead" or "forward" or "forwards" => me.Heading,
                    "back" or "backward" or "backwards" or "behind" => me.Heading + 180,
                    "left" => me.Heading - 90,
                    "right" => me.Heading + 90,
                    _ => Bearings.ParsePoint(dir),
                };
                if (b is null) { _say($"{dir} isn't a direction: N, NNE, NE ... NNW, or ahead, back, left, right."); return false; }
                bearing = b.Value;
            }
            var rad = (90 - bearing) * Math.PI / 180;
            target = me.Pos + new Vector3((float)(Math.Cos(rad) * metres), (float)(Math.Sin(rad) * metres), 0);
            what = $"{Bearings.MetresTenths(metres)} {(a.Count > 1 ? "to the " + Bearings.Point(bearing) : "ahead")}";
        }
        else { Help("go"); return false; }

        if (tp)
        {
            var (ok, line) = await TeleportNearAsync(target, what);
            _say(line);
            return ok;
        }
        var hint = kind is "person" or "object" ? $"//go tp {kind} {string.Join(" ", a.Skip(1))}"
            : string.Create(CultureInfo.InvariantCulture, $"//go tp xyz {target.X:0},{target.Y:0}");
        return await WalkAsync(target, what, stop, hint);
    }

    CancellationTokenSource? _walk;

    /// <summary>Paths around what's in the way (the renderer out of sight's collision model), when there's one.</summary>
    public Walk.PathPlanner? Planner { get; set; }
    /// <summary>The height of the avatar's feet (walk mode knows its size).</summary>
    public Func<float?>? FeetZ { get; set; }
    /// <summary>A walk with no way around teleports there (the walk_teleport_fallback setting).</summary>
    public Func<bool> TeleportFallback { get; set; } = () => true;
    /// <summary>Whether a cue's text is wanted (walk mode's settings: "follow").</summary>
    public Func<string, bool> CueText { get; set; } = _ => true;

    /// <summary>Walks to a point in the region by the region's own autopilot (as Radegast's //go: the region knows where
    /// the avatar really is; a host with no renderer hears its own position only now and then), saying how far, then
    /// how close it got once it has stopped. With the collision model, around what's in the way: a path of waypoints
    /// (PathPlanner), each walked by the region's autopilot in turn, each new stretch said; a stretch that sticks is
    /// marked and the rest planned again (twice). A goal out of reach where this floor meets it (on a stage, inside a
    /// bar) is walked as near as the way goes, said. Seated, it stands first. With no way at all it says so, and
    /// teleports there once where that can work (walk_teleport_fallback; NoWayAsync), else names the command that
    /// would (<paramref name="tpHint"/>). Completes when the walk ends: true when it got there (or within 2.5 m, or as
    /// near as the way goes).</summary>
    public Task<bool> WalkAsync(Vector3 target, string what, double stop = 1, string? tpHint = null)
    {
        if (_ops.Me() is null || _ops.Client is null) return Task.FromResult(false);
        StopFollowing(quiet: true);
        _walk?.Cancel();
        var walk = _walk = new CancellationTokenSource();
        var heading = _heading = new Walk.WalkMode.Target(what, () => new System.Numerics.Vector3(target.X, target.Y, target.Z), false);
        tpHint ??= string.Create(CultureInfo.InvariantCulture, $"//go tp xyz {target.X:0},{target.Y:0}");
        return Task.Run(async () =>
        {
            try { return await RouteAsync(target, what, stop, tpHint, walk.Token); }
            finally { if (_heading == heading) _heading = null; }
        });
    }

    /// <summary>Seated (a chair, a pose ball, a dance): stood up first, as walking needs ("Standing up to walk.").</summary>
    async Task<bool> StandFirstAsync(CancellationToken ct)
    {
        if (_ops.Client is not { } c || c.Self.SittingOn == 0) return true;
        _say("Standing up to walk.");
        await _ops.DoAsync("LLAgent", "requestStand");
        for (int i = 0; i < 30 && c.Self.SittingOn != 0 && !ct.IsCancellationRequested; i++) await Task.Delay(100, CancellationToken.None);
        if (c.Self.SittingOn != 0) { if (!ct.IsCancellationRequested) _say("Couldn't stand up, so not walking."); return false; }
        await Task.Delay(600, CancellationToken.None);   // (the region places the avatar beside the seat)
        return !ct.IsCancellationRequested;
    }

    volatile Walk.WalkMode.Target? _heading;
    /// <summary>Where a walk or a follow of this app's is heading now (walk mode's beacon), or null.</summary>
    public Walk.WalkMode.Target? Heading => _heading;
    /// <summary>Walk mode, for //beacon.</summary>
    public Walk.WalkMode? WalkMode { get; set; }

    async Task<bool> RouteAsync(Vector3 target, string what, double stop, string tpHint, CancellationToken ct)
    {
        if (!await StandFirstAsync(ct)) return false;
        if (_ops.Me() is not { } me || _ops.Client is not { } c || c.Network.CurrentSim is not { } sim) return false;
        ulong origin = sim.Handle;   // the target's frame: a route's point past a border runs beyond 256 or below 0
        double near = Math.Max(stop, 1.2);
        var flat = new Vector2(target.X - me.Pos.X, target.Y - me.Pos.Y).Length();
        var route = await PlanAsync(me.Pos, target, ct);
        if (ct.IsCancellationRequested) return false;
        if (route is { Legs.Count: 0 }) return await NoWayAsync(target, what, tpHint);
        if (route is not { } r)
        {
            if (_ops.Global(target) is not { } g) return false;
            c.Self.AutoPilot(g.X, g.Y, g.Z);
            _say(string.Create(CultureInfo.InvariantCulture, $"Walking {Bearings.Metres(flat)} to {what}."));
            return await WatchWalkAsync(target, near, ct, origin);
        }
        var legs = r.Legs;
        // a goal out of reach (up on a stage, inside a bar): its stand-in, the nearest spot the way goes to
        Vector3 End() => r.Moved ? new Vector3(legs[^1].X, legs[^1].Y, target.Z) : target;
        string Short() => r.Moved ? $"; {what} is out of reach, so as near as the way goes, {Bearings.Metres(Flat(End(), target))} short" : "";
        _say(legs.Count == 1
            ? $"Walking {Bearings.Metres(Length(me.Pos, legs))} toward {what}{Short()}."
            : $"Walking {Bearings.Metres(Length(me.Pos, legs))} to {what}, around something in the way: {legs.Count} stretches{Short()}.");
        int replans = 0;
        for (int i = 0; i < legs.Count; i++)
        {
            if (ct.IsCancellationRequested || _ops.Me() is not { } here) return false;
            bool last = i == legs.Count - 1;
            var leg = last ? End() : new Vector3(legs[i].X, legs[i].Y, target.Z);
            if (i > 0)
            {
                var d = new Vector2(leg.X - here.Pos.X, leg.Y - here.Pos.Y);
                _say($"{i + 1} of {legs.Count}: {Bearings.Metres(d.Length())} to the {Bearings.Point(Bearings.Compass(0, 0, d.X, d.Y))}.");
            }
            if (_ops.Global(leg) is not { } g) return false;
            c.Self.AutoPilot(g.X, g.Y, g.Z);
            var got = await LegAsync(leg, last ? (r.Moved ? 0.6 : near) : 0.5, ct, origin);
            if (got == Leg.Cancelled) return false;
            if (got == Leg.Reached)
            {
                if (!last) continue;
                return r.Moved ? await WatchWalkAsync(leg, 0.8, ct, origin, (target, what)) : await WatchWalkAsync(target, near, ct, origin);
            }
            // stuck on the way: something the model hadn't (a thin post, a doorway the region won't fit): marked, planned again
            c.Self.AutoPilotCancel();
            if (_ops.Me() is not { } stuck || Planner == null) return false;
            var dir = Vector2.Normalize(new Vector2(leg.X - stuck.Pos.X, leg.Y - stuck.Pos.Y));
            Planner.Block(new System.Numerics.Vector2(stuck.Pos.X + dir.X * 0.7f, stuck.Pos.Y + dir.Y * 0.7f), 0.6f, TimeSpan.FromMinutes(2));
            if (++replans > 2) return await NoWayAsync(target, what, tpHint);
            var again = await PlanAsync(stuck.Pos, target, ct);
            if (again is { Legs.Count: 0 }) return await NoWayAsync(target, what, tpHint);
            r = again ?? new Route(new List<System.Numerics.Vector2> { new(target.X, target.Y) }, false);
            legs = r.Legs;
            _say($"Something's in the way; going round: {legs.Count} {(legs.Count == 1 ? "stretch" : "stretches")}.");
            i = -1;
        }
        return false;
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    /// <summary>The avatar's position in the frame of the region with this handle (where a walk began: its target stays
    /// in that frame after a crossing), or null between regions.</summary>
    Vector3? MeIn(ulong origin)
    {
        if (_ops.Me() is not { } me || _ops.Client?.Network.CurrentSim is not { } sim) return null;
        if (sim.Handle == origin) return me.Pos;
        Utils.LongToUInts(sim.Handle, out var cx, out var cy);
        Utils.LongToUInts(origin, out var ox, out var oy);
        return me.Pos + new Vector3((float)((double)cx - ox), (float)((double)cy - oy), 0);
    }

    /// <summary>A planned way: its waypoints (the last the goal, or its stand-in when the goal was out of reach).</summary>
    readonly record struct Route(List<System.Numerics.Vector2> Legs, bool Moved);

    static float Length(Vector3 from, List<System.Numerics.Vector2> legs)
    {
        float len = 0;
        var prev = new System.Numerics.Vector2(from.X, from.Y);
        foreach (var p in legs) { len += System.Numerics.Vector2.Distance(prev, p); prev = p; }
        return len;
    }

    /// <summary>The way around what's in the way; null: straight (no collision model, or nothing in the way); no legs:
    /// no way at all.</summary>
    async Task<Route?> PlanAsync(Vector3 from, Vector3 to, CancellationToken ct)
    {
        if (Planner is not { } planner) return null;
        // beyond this region's edge (a recorded route's crossing): straight, by the region's autopilot
        if (to.X < 0 || to.X >= 256 || to.Y < 0 || to.Y >= 256) return null;
        var a = new System.Numerics.Vector2(from.X, from.Y);
        var b = new System.Numerics.Vector2(to.X, to.Y);
        if (System.Numerics.Vector2.Distance(a, b) < 2) return null;
        try
        {
            var plan = await planner.PlanAsync(a, FeetZ?.Invoke() ?? from.Z - 0.95f, b, null, ct, to.Z);
            Walk.WalkMode.Log($"go {a} -> {b}: {(plan == null ? (planner.CanAsk ? "no way" : "no view") : plan.Direct && !plan.GoalMoved ? "direct" : string.Join(" ", plan.Waypoints) + (plan.GoalMoved ? " (goal moved)" : ""))}");
            if (plan == null) return planner.CanAsk ? new Route(new(), false) : null;
            return plan.Direct && !plan.GoalMoved ? null : new Route(plan.Waypoints, plan.GoalMoved);
        }
        catch (OperationCanceledException) { return null; }
    }

    enum Leg { Reached, Stalled, Cancelled }

    /// <summary>A stretch on the way: until the avatar is within <paramref name="within"/> of its end (0.5 m at a corner:
    /// the next stretch starts without stopping, and a wider berth cut the corner into the wall), or it stops getting
    /// anywhere for two seconds.</summary>
    async Task<Leg> LegAsync(Vector3 to, double within, CancellationToken ct, ulong origin)
    {
        long start = Environment.TickCount64, progressAt = start;
        double best = double.MaxValue;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(150, CancellationToken.None);
            if (ct.IsCancellationRequested || MeIn(origin) is not { } pos) return Leg.Cancelled;
            double off = new Vector2(to.X - pos.X, to.Y - pos.Y).Length();
            if (off <= within) return Leg.Reached;
            long now = Environment.TickCount64;
            if (off < best - 0.1) { best = off; progressAt = now; }
            if (now - progressAt > 2000 || now - start > 60_000) return Leg.Stalled;
        }
        return Leg.Cancelled;
    }

    /// <summary>No way to walk there: said once, plainly. With walk_teleport_fallback on it teleports there instead, but
    /// only where that can land beside it (not where the region or the parcel sends teleports to a hub or landing point,
    /// which in Second Life is most clubs and shops), and only once: a teleport that landed elsewhere (or failed) isn't
    /// tried again for the same spot for a few minutes. Otherwise the line names the command that would try
    /// (<paramref name="tpHint"/>). Public: the assistant's walks (MainForm.RouteAsync) come here too.</summary>
    public async Task<bool> NoWayAsync(Vector3 target, string what, string? tpHint)
    {
        _ops.Client?.Self.AutoPilotCancel();
        var offer = tpHint != null ? $" {tpHint} would teleport you there." : "";
        if (!TeleportFallback() || _ops.Me() is null)
        {
            _say($"No way to walk to {what} found.{offer}");
            return false;
        }
        if (TeleportWontLand(target) is { } why)
        {
            _say($"No way to walk to {what} found, and a teleport wouldn't land there: {why}.");
            return false;
        }
        _say($"No way to walk to {what} found: teleporting there.");
        var (ok, line) = await TeleportNearAsync(target, what);
        _say(line);
        return ok;
    }

    (Vector3 At, long When, string Why)? _missed;

    /// <summary>Why a teleport to a spot in this region wouldn't land there, or null: the region's telehub, the parcel's
    /// landing point (its owner excepted, as the region does), or one there just now that didn't.</summary>
    string? TeleportWontLand(Vector3 at) =>
        _missed is { } m && Environment.TickCount64 - m.When < 5 * 60_000 && Flat(m.At, at) < 4 ? $"one just now didn't ({m.Why})" : SentElsewhere(at);

    /// <summary>Where the region or the parcel sends a teleport to that spot instead, in words, or null.</summary>
    string? SentElsewhere(Vector3 at)
    {
        if (_ops.Client is not { } c || c.Network.CurrentSim is not { } sim) return null;
        if ((sim.Flags & RegionFlags.AllowDirectTeleport) == 0) return "this region sends teleports to its telehub";
        int local = 0;
        try { local = c.Parcels.GetParcelLocalID(sim, at); } catch { }
        if (local != 0 && sim.Parcels.TryGetValue(local, out var parcel) && parcel.Landing != LandingType.Direct && parcel.OwnerID != c.Self.AgentID)
            return "the parcel there sends teleports to its landing point";
        return null;
    }

    /// <summary>A teleport to a spot in this region (`//go tp`, the walk's fallback), and the line for how it went: where
    /// it really landed, since a landing point or telehub takes it elsewhere and the grid calls that a success. A miss
    /// is remembered (TeleportWontLand).</summary>
    async Task<(bool Ok, string Line)> TeleportNearAsync(Vector3 target, string what)
    {
        if (_ops.Me() is not { } me) return (false, "Not in a region yet.");
        OwnTeleport = true;
        JsonObject r;
        try
        {
            r = await _ops.DoAsync("LLAgent", "requestTeleport", new JsonObject
            {
                ["regionname"] = me.Region, ["x"] = (double)target.X, ["y"] = (double)target.Y, ["z"] = (double)Math.Max(target.Z, me.Pos.Z),
                ["skip_confirmation"] = true, ["timeout"] = 20,
            });
        }
        finally { OwnTeleportDone(); }
        if (PersonOps.Error(r) is { } e)
        {
            _missed = (target, Environment.TickCount64, "it didn't work");
            return (false, $"Teleport didn't work: {e}");
        }
        await Task.Delay(1000);   // (the region's word on where it put the avatar)
        if (_ops.Me() is { } now && Flat(now.Pos, target) is var off && off > 3)
        {
            var landing = SentElsewhere(target) is { } why ? $" ({why})" : "";
            _missed = (target, Environment.TickCount64, "it landed elsewhere");
            return (false, $"The teleport landed you away from {what}{landing}: {what} is {_ops.Where(target) ?? Bearings.Metres(off)}.");
        }
        return (true, $"Teleported to {what}.");
    }

    /// <summary>Waits for the walk to end (stopped near the target, stalled, or a minute), then says Radegast's line;
    /// walking to a stand-in for a goal out of reach (<paramref name="outOfReach"/>), how near that is to it.</summary>
    async Task<bool> WatchWalkAsync(Vector3 target, double near, CancellationToken ct, ulong origin, (Vector3 At, string What)? outOfReach = null)
    {
        long start = Environment.TickCount64, stillSince = start;
        bool moved = false, again = false;
        var last = MeIn(origin) ?? target;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(250, CancellationToken.None);
            if (ct.IsCancellationRequested || _ops.Client is not { } c) return false;
            if (MeIn(origin) is not { } pos) continue;   // (between regions a moment)
            var me = (Pos: pos, Region: c.Network.CurrentSim?.Handle ?? origin);
            long now = Environment.TickCount64;
            bool going = c.Self.Velocity.Length() > 0.15f || Vector3.Distance(me.Pos, last) > 0.05f;
            last = me.Pos;
            if (going) { stillSince = now; moved = true; }
            var off = new Vector2(target.X - me.Pos.X, target.Y - me.Pos.Y).Length();
            long still = now - stillSince;
            bool arrived = off <= near && still > 700, stalled = still > (moved ? 2000 : 4000), late = now - start > 60_000;
            if (stalled && !arrived && !again && me.Region != origin && off > near)
            {
                // a crossing on the way: the new region doesn't carry the old one's autopilot on, so asked again once
                again = true;
                Utils.LongToUInts(origin, out var ox, out var oy);
                c.Self.AutoPilot(ox + (double)target.X, oy + (double)target.Y, target.Z);
                stillSince = now;
                moved = false;
                continue;
            }
            if (!arrived && !stalled && !late) continue;
            if (!arrived) c.Self.AutoPilotCancel();
            if (outOfReach is { } o && off < 2.5)
            {
                _say($"Finished walking, as near to {o.What} as the way goes: {o.What} is {_ops.Where(o.At) ?? Bearings.Metres(Flat(me.Pos, o.At))}.");
                return true;
            }
            _say(string.Create(CultureInfo.InvariantCulture,
                $"Finished walking, {(off < 3 ? (off < 1 ? "under a metre" : Bearings.MetresTenths(off)) : Bearings.Metres(off))} from the destination{(arrived || off < 2.5 ? "" : " (something was in the way)")}."));
            return arrived || off < 2.5;
        }
        return false;
    }

    /// <summary>The person takes over by hand (walk mode's keys): a walk, a follow and a route all stop, quietly.</summary>
    public void StopAll()
    {
        bool route = _route != null;
        _route?.Cancel();
        StopWalking();
        StopFollowing(quiet: true);
        if (route) _say("Route stopped: you're walking by hand. //route resume goes on.");
    }

    void StopWalking()
    {
        _heading = null;
        if (_walk == null) return;
        _walk.Cancel();
        _walk = null;
        _ops.Client?.Self.AutoPilotCancel();
    }

    /// <summary>//beacon NAME: walk mode's beacon on the nearest person (by name) or thing (by name) to walk to; off; or where.</summary>
    async Task BeaconAsync(List<string> a)
    {
        if (WalkMode is not { } walk) { _say("Not in a region yet."); return; }
        var arg = string.Join(" ", a);
        if (arg.Length == 0) { walk.BeaconLine(); return; }
        if (arg.ToLowerInvariant() is "off" or "stop") { walk.BeaconOff(); return; }
        var kind = a[0].ToLowerInvariant();
        bool person = kind == "person", obj = kind == "object";
        var name = string.Join(" ", (person || obj) ? a.Skip(1) : a);
        if (!obj && await _ops.FindPersonAsync(name) is { } p) { walk.BeaconOn(p.Name, p.Id); return; }
        if (!person && (await _ops.ObjectsAsync(96, 5, name)).FirstOrDefault() is { } t) { walk.BeaconOn(t.Name, t.Id); return; }
        _say($"No {(person ? "one" : obj ? "object" : "one or nothing")} called {name} nearby.");
    }

    // --- find, face -----------------------------------------------------------------------------------------------

    async Task<(string Name, Vector3 Pos)?> TargetAsync(List<string> a, string verb)
    {
        if (a.Count == 0) { Help(verb); return null; }
        var kind = a[0].ToLowerInvariant();
        bool person = kind == "person", obj = kind == "object";
        var name = string.Join(" ", (person || obj) ? a.Skip(1) : a);
        if (name.Length == 0) { Help(verb); return null; }
        if (!obj && await _ops.FindPersonAsync(name) is { } p) return (p.Name, p.Pos);
        if (!person && (await _ops.ObjectsAsync(96, 5, name)).FirstOrDefault() is { } t) return (t.Name, t.Pos);
        _say($"No {(person ? "one" : obj ? "object" : "one or nothing")} called {name} nearby.");
        return null;
    }

    async Task FindAsync(List<string> a)
    {
        if (await TargetAsync(a, "find") is not { } t) return;
        _say($"{t.Name} is {_ops.Where(t.Pos, full: true) ?? "somewhere"}.");
    }

    async Task FaceAsync(List<string> a)
    {
        if (a.Count == 0) { Help("face"); return; }
        if (_ops.Me() is not { } me) return;
        double compass;
        string to;
        if (double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sl) && a.Count == 1) { compass = Bearings.FromSl(sl); to = ""; }
        else if (a.Count == 1 && Bearings.ParsePoint(a[0]) is { } b) { compass = b; to = ""; }
        else if (a.Count == 1 && a[0].ToLowerInvariant() is "left" or "right" or "back" or "around")
        {
            compass = me.Heading + (a[0].ToLowerInvariant() switch { "left" => -90, "right" => 90, _ => 180 });
            to = "";
        }
        else
        {
            if (await TargetAsync(a, "face") is not { } t) return;
            await FacePointAsync(t.Pos, t.Name);
            return;
        }
        await TurnAsync(compass, to);
    }

    /// <summary>Turns toward a point (someone, something).</summary>
    public async Task FacePointAsync(Vector3 pos, string name)
    {
        if (_ops.Me() is not { } me) return;
        await TurnAsync(Bearings.Compass(me.Pos.X, me.Pos.Y, pos.X, pos.Y), $" {name}");
    }

    async Task TurnAsync(double compass, string to)
    {
        var r = await _ops.DoAsync("TesselAgent", "move", new JsonObject { ["face"] = Bearings.Norm(compass), ["secs"] = 0.3, ["wait"] = true });
        if (PersonOps.Error(r) is { } e) { _say($"Can't turn: {e}"); return; }
        _say(to.Length > 0 ? $"Facing{to}, to the {Bearings.Point(compass)}." : $"Facing {Bearings.Point(compass)}, {Bearings.ToSl(compass)} degrees.");
    }

    // --- sit, stand -------------------------------------------------------------------------------------------------

    async Task SitAsync(List<string> a)
    {
        var name = string.Join(" ", a);
        if (name.Length == 0) { Help("sit"); return; }
        if (name.Equals("ground", StringComparison.OrdinalIgnoreCase))
        {
            await _ops.DoAsync("LLAgent", "requestSit");
            _say("Sitting on the ground.");
            return;
        }
        var t = (await _ops.ObjectsAsync(96, 5, name)).FirstOrDefault();
        if (t == null) { _say($"Nothing called {name} nearby."); return; }
        var r = await _ops.DoAsync("LLAgent", "requestSit", new JsonObject { ["obj_uuid"] = t.Id.ToString() });
        _say(PersonOps.Error(r) is { } e ? $"Couldn't sit on {t.Name}: {e}" : $"Sitting on {t.Name}.");
    }

    async Task StandAsync()
    {
        await _ops.DoAsync("LLAgent", "requestStand");
        _say("Standing.");
    }

    // --- follow -----------------------------------------------------------------------------------------------------

    string? _following;
    CancellationTokenSource? _followStop;

    async Task FollowAsync(List<string> a)
    {
        var name = string.Join(" ", a);
        if (name.Length == 0) { _say(_following != null ? $"Following {_following}." : "Not following anyone."); return; }
        if (name.Equals("stop", StringComparison.OrdinalIgnoreCase)) { StopFollowing(quiet: false); return; }
        if (await _ops.FindPersonAsync(name) is not { } p) { _say($"Nobody called {name} nearby."); return; }
        Follow(p);
    }

    /// <summary>Follows someone as they move (M3): the session's own autopilot steered ten times a second toward a spot
    /// 2.5 m short of where they'll be in a moment (their pace measured as they go), around what's in the way when the
    /// straight line is blocked (PathPlanner, every 1.5 s), standing still once close. Said as it goes (the "follow"
    /// cue's text): when they stop and you're with them, when they sit, when they're getting away, and when they're gone
    /// (a teleport or logging off) or out of reach.</summary>
    public void Follow(PersonOps.Person p)
    {
        StopFollowing(quiet: true);
        StopWalking();
        _following = p.Name;
        _heading = new Walk.WalkMode.Target(p.Name, () => _s.Region is { } w && w.AvatarById(p.Id) is { } av
            ? new System.Numerics.Vector3(w.RegionPosition(av).X, w.RegionPosition(av).Y, w.RegionPosition(av).Z) : null, false);
        var stop = _followStop = new CancellationTokenSource();
        _say($"Following {p.Name}. //follow stop to stop.");
        _ = Task.Run(async () =>
        {
            try { await FollowLoopAsync(p, stop.Token); }
            catch (Exception e) when (!stop.IsCancellationRequested) { _say($"Following stopped: {e.Message}"); StopFollowing(quiet: true); }
        });
    }

    async Task FollowLoopAsync(PersonOps.Person p, CancellationToken ct)
    {
        const float Behind = 2.5f, Restart = 3.6f;
        int lost = 0;
        Vector3? lastPos = null;
        long lastAt = 0, toldFarAt = 0, planAt = 0;
        var vel = Vector3.Zero;
        bool toldWith = false, toldSat = false;
        double lastDist = 0, farDist = 0;
        System.Numerics.Vector2? via = null;
        bool Say(string text) { if (CueText("follow")) _say(text); return true; }
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(400, CancellationToken.None);
            if (ct.IsCancellationRequested) break;
            if (_ops.Client is not { } c || _ops.Me() is not { } me) continue;
            var now = (await _ops.PeopleAsync(512)).FirstOrDefault(x => x.Id == p.Id);
            if (now == null)
            {
                if (++lost < 5) continue;
                _say(lastDist < 30 ? $"{p.Name} is gone, a teleport or logging off; stopped following." : $"Lost {p.Name}, out of reach; stopped following.");
                StopFollowing(quiet: true);
                break;
            }
            lost = 0;
            long t = Environment.TickCount64;
            // where they are now: regions send a walking avatar's position only now and then (OpenSim: when its
            // velocity changes), so it's carried on by their velocity since it last changed, as a viewer draws them
            if (lastPos is not { } lp || lp != now.Pos)
            {
                if (lastPos is { } prev && t > lastAt)
                {
                    var v = (now.Pos - prev) / ((t - lastAt) / 1000f);
                    vel = v.Length() > 15 ? Vector3.Zero : v;   // a jump (a region change, a teleport) isn't a pace
                }
                lastPos = now.Pos;
                lastAt = t;
            }
            if (_s.Region?.AvatarById(p.Id) is { } seen)
            {
                float rv = new Vector2(seen.Velocity.X, seen.Velocity.Y).Length();
                if (rv > 0.1f || t - lastAt > 1500) vel = seen.Velocity;
            }
            float age = Math.Min(2f, (t - lastAt) / 1000f);
            var there = now.Pos + new Vector3(vel.X, vel.Y, 0) * age;
            lastDist = now.Dist;
            float speed = new Vector2(vel.X, vel.Y).Length();
            if (_s.Region?.AvatarById(p.Id) is { ParentID: not 0 })
            {
                if (!toldSat) toldSat = Say($"{p.Name} sat down, {_ops.Where(now.Pos) ?? "nearby"}.");
                if (_s.Motion.Moving) _s.Motion.StopAutoPilot(c, true);
                continue;
            }
            toldSat = false;
            var ahead = there + new Vector3(vel.X, vel.Y, 0) * 0.6f;
            var to = new Vector2(ahead.X - me.Pos.X, ahead.Y - me.Pos.Y);
            float d = to.Length();
            if (d <= Restart && (!_s.Motion.Moving || d <= Behind + 0.3f))
            {
                if (_s.Motion.Moving) _s.Motion.StopAutoPilot(c, true);
                if (speed < 0.3f && !toldWith) toldWith = Say($"With {p.Name}, {_ops.Where(there) ?? "here"}.");
                continue;
            }
            if (speed > 0.5f) toldWith = false;
            var dir = to / Math.Max(d, 0.01f);
            var aim = new Vector3(ahead.X - dir.X * Behind, ahead.Y - dir.Y * Behind, ahead.Z);
            // around what's in the way: the path's first corner while the straight line is blocked
            if (Planner is { } planner && t - planAt > 1500)
            {
                planAt = t;
                via = null;
                var plan = await planner.PlanAsync(new(me.Pos.X, me.Pos.Y), FeetZ?.Invoke() ?? me.Pos.Z - 0.95f, new(aim.X, aim.Y), null, CancellationToken.None);
                if (plan is { Direct: false, Waypoints.Count: > 1 }) via = plan.Waypoints[0];
            }
            if (via is { } w && new Vector2(w.X - me.Pos.X, w.Y - me.Pos.Y).Length() < 1) via = null;
            var goal = via is { } wv ? new Vector3(wv.X, wv.Y, me.Pos.Z) : aim;
            if (_ops.Global(goal) is not { } g) continue;
            if (!_s.Motion.RetargetAutoPilot(c, g, 0.5)) _s.Motion.StartAutoPilot(c, g, 0.5, null, route: false);
            if (now.Dist > 20 && now.Dist > farDist + 2 && t - toldFarAt > 10_000)
            {
                toldFarAt = t;
                farDist = now.Dist;
                Say($"{p.Name} is getting away: {_ops.Where(now.Pos)}.");
            }
            else if (now.Dist < 15) farDist = 0;
        }
    }

    void StopFollowing(bool quiet)
    {
        if (_following == null) { if (!quiet) _say("Not following anyone."); return; }
        _followStop?.Cancel();
        var who = _following;
        _following = null;
        _heading = null;
        _ops.Client?.Self.AutoPilotCancel();
        if (_ops.Client is { } c && _s.Motion.Moving) _s.Motion.StopAutoPilot(c, false);
        if (!quiet) _say($"Stopped following {who}.");
    }

    // --- tp -----------------------------------------------------------------------------------------------------------

    async Task<bool> TeleportAsync(List<string> a)
    {
        var arg = string.Join(" ", a);
        if (arg.Length == 0) { Help("tp"); return false; }
        if (Places.Parse(arg) is { } place) return await GoToAsync(place);
        var marks = await LandmarksAsync();
        if (arg.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            _say(marks.Count == 0 ? "No landmarks (in Radegast Landmarks or Landmarks)."
                : string.Join(" ", marks.Select((m, i) => $"{i + 1}, {m.Name}.")));
            return true;
        }
        int at = int.TryParse(arg, out var n) && n >= 1 && n <= marks.Count ? n - 1
            : marks.FindIndex(m => m.Name.StartsWith(arg, StringComparison.OrdinalIgnoreCase));
        if (at < 0) { _say($"No landmark {arg}. //tp list lists them; //tp Region/X/Y/Z goes to a place."); return false; }
        return await TeleportLandmarkAsync(marks[at].Id, marks[at].Name);
    }

    /// <summary>Teleport by a landmark: said as it goes, and the arrival with where.</summary>
    public async Task<bool> TeleportLandmarkAsync(UUID item, string name)
    {
        if (_ops.Client is not { } c) return false;
        _say($"Teleporting to {name}.");
        OwnTeleport = true;
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Progress(object? _, TeleportEventArgs e)
        {
            if (e.Status == TeleportStatus.Finished) done.TrySetResult(null);
            else if (e.Status is TeleportStatus.Failed or TeleportStatus.Cancelled) done.TrySetResult(string.IsNullOrWhiteSpace(e.Message) ? "it failed" : e.Message);
        }
        // where it points: a teleport within the region may never report finishing (LibreMetaverse, as with
        // AgentSession.TeleportAsync), so being there counts as arrived too
        var where = await _ops.ReadAsync("TesselInventory", "landmark", new JsonObject { ["item_id"] = item.ToString() });
        var target = where["pos"] is JsonArray tp && tp.Count >= 2 ? new Vector2((float)(Json.AsDouble(tp[0]) ?? 0), (float)(Json.AsDouble(tp[1]) ?? 0)) : (Vector2?)null;
        var region = where.Str("region");
        var from = _ops.Me()?.Pos;
        c.Self.TeleportProgress += Progress;
        try
        {
            var r = await _ops.DoAsync("TesselAgent", "teleportLandmark", new JsonObject { ["item_id"] = item.ToString() });
            if (PersonOps.Error(r) is { } e) { _say($"Teleport didn't work: {e}"); return false; }
            var deadline = Environment.TickCount64 + 45_000;
            while (!done.Task.IsCompleted && Environment.TickCount64 < deadline)
            {
                await Task.WhenAny(done.Task, Task.Delay(250));
                if (target is { } t && _ops.Me() is { } me && (region == null || string.Equals(me.Region, region, StringComparison.OrdinalIgnoreCase))
                    && Vector2.Distance(new Vector2(me.Pos.X, me.Pos.Y), t) < 5
                    && (from is not { } f || Vector2.Distance(new Vector2(f.X, f.Y), t) >= 5 || Environment.TickCount64 > deadline - 40_000))
                    done.TrySetResult(null);
            }
            if (!done.Task.IsCompleted) { _say("Still on the way."); return false; }
            if (done.Task.Result is { } why) { _say($"Teleport didn't work: {why}"); return false; }
            await Task.Delay(500);
            _say(ArrivedLine());
            return true;
        }
        finally
        {
            c.Self.TeleportProgress -= Progress;
            OwnTeleportDone();
        }
    }

    /// <summary>"Arrived in Bay City, at 128, 128, 22."</summary>
    public string ArrivedLine() => _ops.Me() is { } me
        ? string.Create(CultureInfo.InvariantCulture, $"Arrived in {me.Region}, at {Math.Round(me.Pos.X):0}, {Math.Round(me.Pos.Y):0}, {Math.Round(me.Pos.Z):0}.")
        : "Arrived.";

    /// <summary>Teleport to a place (an SLurl, Region/x/y/z): said as it goes, and the arrival with where.</summary>
    public async Task<bool> GoToAsync(Place place)
    {
        _say($"Teleporting to {place}.");
        OwnTeleport = true;
        try
        {
            var r = await _ops.DoAsync("LLAgent", "requestTeleport", new JsonObject
            {
                ["regionname"] = place.Region, ["x"] = place.X, ["y"] = place.Y, ["z"] = place.Z, ["skip_confirmation"] = true, ["timeout"] = 45,
            });
            if (PersonOps.Error(r) is { } e) { _say($"Teleport didn't work: {e}"); return false; }
            if (r["teleported"] is not null) { _say(ArrivedLine()); return true; }
            _say("Still on the way.");
            return false;
        }
        finally { OwnTeleportDone(); }
    }

    async Task<List<(UUID Id, string Name)>> LandmarksAsync()
    {
        var inv = _s.Inventory;
        var store = _ops.Client?.Inventory.Store;
        if (store == null) return new();
        var root = inv.FolderFor(FolderType.Root);
        await inv.EnsureLoadedAsync(root, CancellationToken.None);
        var folder = store.GetContents(root).OfType<InventoryFolder>().FirstOrDefault(f => f.Name == "Radegast Landmarks")?.UUID
                     ?? inv.FolderFor(FolderType.Landmark);
        // (off the window's thread: the collecting after the fetch is a walk of every folder under it)
        var found = await Task.Run(() => inv.CollectAsync(folder, null, null, null, null, 500, CancellationToken.None));
        return (found["items"] as JsonObject ?? new()).Select(kv => kv.Value as JsonObject).OfType<JsonObject>()
            .Where(j => j.Str("inv_type") == "landmark" || j.Str("asset_type") == "landmark")
            .OrderBy(j => j["creation_date"]?.GetValue<long>() ?? 0)
            .Select(j => (UUID.Parse(j.Str("id")!), j.Str("name") ?? "landmark")).ToList();
    }

    // --- route ---------------------------------------------------------------------------------------------------------

    /// <summary>The clipboard's text (the window's thread reads it), for `//route` with nothing after it.</summary>
    public Func<string?>? Clipboard { get; set; }

    /// <summary>`//about`: the version and where the data lives (AppInfo).</summary>
    public Func<string>? About { get; set; }

    /// <summary>Said like the rest, for words that quote someone (a parcel's description, a route notecard's note): the
    /// session log marks them so, and a log sent to the studio leaves them out.</summary>
    public Action<string>? Quote { get; set; }

    void Quoted(string text) => (Quote ?? _say)(text);

    /// <summary>`//sendlog`: the window for sending a session's log to the studio (SendLog.cs).</summary>
    public Action? SendLog { get; set; }

    /// <summary>`//describe`'s words, to the app's describing (MainForm.Describe.cs).</summary>
    public Func<string, Task>? Describe { get; set; }

    /// <summary>`//record`'s words as typed, to the app's route recording (MainForm.Routes.cs).</summary>
    public Func<string, Task>? Record { get; set; }

    /// <summary>`//touch`'s words, to the app's parts (MainForm.Parts.cs).</summary>
    public Func<string, Task>? Touch { get; set; }

    /// <summary>`//switch`'s words, to walk mode's rail switches (Walk/WalkMode.Switches.cs).</summary>
    public Func<string, Task>? Switch { get; set; }

    /// <summary>//upload [PATH]: the words after the verb as typed (a Windows path's backslashes and spaces kept).</summary>
    public Func<string, Task>? Upload { get; set; }

    static string AfterVerb(string line)
    {
        var t = line.TrimStart()[2..].TrimStart();
        int sp = t.IndexOfAny(new[] { ' ', '\t' });
        return sp < 0 ? "" : t[(sp + 1)..].Trim();
    }

    /// <summary>`//imsound`'s words (MainForm.ImSounds.cs).</summary>
    public Func<string, Task>? ImSound { get; set; }

    /// <summary>`//outfit`: what's worn by name (MainForm.Outfit.cs).</summary>
    public Func<Task>? Outfit { get; set; }

    /// <summary>`//voice`'s words (MainForm.Voice.cs).</summary>
    public Func<string, Task>? Voice { get; set; }

    /// <summary>`//status music`: the parcel's stream and what it's playing, or null when this parcel has none.</summary>
    public Func<string?>? Music { get; set; }

    CancellationTokenSource? _route;
    (string Name, List<string> Lines, int Next)? _routeAt;

    async Task RouteAsync(List<string> a)
    {
        var arg = string.Join(" ", a).Trim();
        if (arg.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (_route == null) { _say("No route playing."); return; }
            _route.Cancel();
            StopWalking();
            return;
        }
        if (arg.Equals("resume", StringComparison.OrdinalIgnoreCase) || arg.Equals("again", StringComparison.OrdinalIgnoreCase))
        {
            if (_routeAt is not { } at) { _say("No route to resume."); return; }
            await PlayAsync(at.Name, at.Lines, at.Next);
            return;
        }
        if (arg.Length == 0)
        {
            var clip = Clipboard?.Invoke() ?? "";
            var lines = RouteLines(clip);
            if (!lines.Any(IsCommand)) { _say("//route NOTECARD plays a notecard's route; with no name, the // lines on the clipboard (there are none)."); return; }
            await PlayAsync("the clipboard's route", lines, 0);
            return;
        }
        if (await FindNotecardAsync(arg) is not { } card) { _say($"No notecard called {arg}."); return; }
        var r = await _ops.ReadAsync("TesselInventory", "readNotecard", new JsonObject { ["item_id"] = card.Id.ToString() });
        if (PersonOps.Error(r) is { } e) { _say($"Couldn't read {card.Name}: {e}"); return; }
        await PlayRouteAsync(card.Name, r.Str("text") ?? "");
    }

    /// <summary>A notecard's (or any text's) route played: its `//` lines run one at a time, each walk or teleport
    /// finished before the next; other lines are read as notes. Stops where a step didn't get there, said, and
    /// `//route resume` goes on from that step.</summary>
    public async Task PlayRouteAsync(string name, string text)
    {
        var lines = RouteLines(text);
        if (!lines.Any(IsCommand)) { _say($"{name} has no // lines to play."); return; }
        await PlayAsync(name, lines, 0);
    }

    static List<string> RouteLines(string text) => text.Replace("\r\n", "\n").Split('\n')
        .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    async Task PlayAsync(string name, List<string> lines, int from)
    {
        _route?.Cancel();
        var stop = _route = new CancellationTokenSource();
        int steps = lines.Count(IsCommand);
        int step = lines.Take(from).Count(IsCommand);
        _say(from == 0 ? $"Playing {name}: {steps} {(steps == 1 ? "step" : "steps")}. //route stop stops." : $"Resuming {name} at step {step + 1} of {steps}.");
        try
        {
            for (int i = from; i < lines.Count; i++)
            {
                if (stop.IsCancellationRequested) { _routeAt = (name, lines, i); _say($"Route stopped before step {step + 1} of {steps}. //route resume goes on."); return; }
                var line = lines[i];
                if (!IsCommand(line)) { Quoted($"Note: {line}"); continue; }
                step++;
                var verb = line.TrimStart()[2..].TrimStart();
                if (verb.StartsWith("route", StringComparison.OrdinalIgnoreCase) || verb.StartsWith("record", StringComparison.OrdinalIgnoreCase)) continue;
                _say($"Step {step} of {steps}.");
                bool ok = await RunAsync(line);
                if (stop.IsCancellationRequested) { _routeAt = (name, lines, i); _say($"Route stopped at step {step} of {steps}. //route resume does it again."); return; }
                if (!ok) { _routeAt = (name, lines, i); _say($"Route paused at step {step} of {steps}. //route resume tries it again; //route stop ends it."); return; }
                await Task.Delay(300);
            }
            _routeAt = null;
            _say($"Route finished: {name}.");
        }
        finally { if (_route == stop) _route = null; }
    }

    /// <summary>A notecard by name: the exact name, else the first starting with it, else containing it.</summary>
    async Task<(UUID Id, string Name)?> FindNotecardAsync(string name)
    {
        var inv = _s.Inventory;
        var root = inv.FolderFor(FolderType.Root);
        // (off the window's thread: it walks the whole inventory, Trash aside)
        var found = await Task.Run(() => inv.CollectAsync(root, null, null, "notecard", null, 2000, CancellationToken.None));
        var cards = (found["items"] as JsonObject ?? new()).Select(kv => kv.Value as JsonObject).OfType<JsonObject>()
            .Select(j => (Id: UUID.TryParse(j.Str("id"), out var id) ? id : UUID.Zero, Name: j.Str("name") ?? "")).Where(c => c.Id != UUID.Zero).ToList();
        var hit = cards.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (hit.Id == UUID.Zero) hit = cards.FirstOrDefault(c => c.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        if (hit.Id == UUID.Zero) hit = cards.FirstOrDefault(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        return hit.Id == UUID.Zero ? null : hit;
    }

    // --- pause, siminfo, parcelinfo, scriptinfo -----------------------------------------------------------------------

    async Task<bool> PauseAsync(List<string> a)
    {
        double secs = a.Count > 0 && double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 600) : 1;
        var stop = _route?.Token ?? CancellationToken.None;
        try { await Task.Delay(TimeSpan.FromSeconds(secs), stop); }
        catch (OperationCanceledException) { return false; }
        return true;
    }

    async Task SimInfoAsync()
    {
        if (_ops.Client?.Network.CurrentSim is not { } sim) { _say("Not in a region yet."); return; }
        var r = await _ops.ReadAsync("SojournLand", "region");
        var st = sim.Stats;
        var parts = new List<string>();
        var name = r.Str("name") is { Length: > 0 } n ? n : sim.Name;
        var type = r.Str("type") is { Length: > 0 } t ? $", {t}" : "";
        var maturity = r.Str("maturity") is { Length: > 0 } m ? $", {m.ToLowerInvariant()}" : "";
        parts.Add($"Region {name}{type}{maturity}.");
        var limit = Json.AsDouble(r["agent_limit"]) is { } lim && lim > 0 ? $" of {lim:0}" : "";
        parts.Add($"{st.Agents}{limit} {(st.Agents == 1 ? "person" : "people")} here{(st.ChildAgents > 0 ? $", {st.ChildAgents} nearby" : "")}.");
        parts.Add(string.Create(CultureInfo.InvariantCulture,
            $"{st.ActiveScripts} active scripts, {st.Objects} objects; time dilation {st.Dilation:0.00}, {st.FPS} frames a second, physics {st.PhysicsFPS:0}."));
        if (r["debug"] is JsonObject d && (d["scripts_disabled"]?.GetValue<bool>() == true || d["physics_disabled"]?.GetValue<bool>() == true))
            parts.Add("Scripts or physics are switched off here.");
        _say(string.Join(" ", parts));
    }

    async Task ParcelInfoAsync()
    {
        if (_ops.Client is not { } c || c.Network.CurrentSim is not { } sim || _ops.Me() is not { } me) { _say("Not in a region yet."); return; }
        if (sim.Parcels.Count == 0)
        {
            _ = c.Parcels.RequestAllSimParcelsAsync(sim, CancellationToken.None);
            for (int i = 0; i < 30 && sim.Parcels.Count == 0; i++) await Task.Delay(200);
            await Task.Delay(500);
        }
        var here = c.Parcels.GetParcelLocalID(sim, me.Pos);
        var parcels = sim.Parcels.Values.OrderByDescending(p => p.LocalID == here).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (parcels.Count == 0) { _say("The region hasn't said what its parcels are."); return; }
        var lines = parcels.Select(p =>
        {
            var name = string.IsNullOrWhiteSpace(p.Name) ? "unnamed" : p.Name;
            var who = (p.Flags & ParcelFlags.UseAccessList) != 0 ? ", allowed list only" : (p.Flags & ParcelFlags.UseAccessGroup) != 0 ? ", its group only" : "";
            var desc = string.IsNullOrWhiteSpace(p.Desc) ? "" : $": {p.Desc.Trim()}";
            return $"{(p.LocalID == here ? "Here, " : "")}{name}, {p.Area} square metres{who}{desc}.";
        });
        // the parcels' descriptions are their owners' words: the session log marks the line as quoting someone
        Action<string> say = parcels.Any(p => !string.IsNullOrWhiteSpace(p.Desc)) ? Quoted : _say;
        say($"{parcels.Count} {(parcels.Count == 1 ? "parcel" : "parcels")}. " + string.Join(" ", lines));
    }

    async Task ScriptInfoAsync()
    {
        if (_ops.Client is not { } c || _s.Region is not { } w) { _say("Not in a region yet."); return; }
        var me = w.Avatars.Values.FirstOrDefault(a => a.ID == c.Self.AgentID);
        var worn = me == null ? new List<Primitive>() : w.Prims.Values.Where(p => p.ParentID == me.LocalID).ToList();
        if (worn.Count == 0) { _say("Nothing worn, so no scripts in what you're wearing."); return; }
        var r = await _ops.ReadAsync("SojournBuild", "scriptInfo", new JsonObject { ["ids"] = new JsonArray(worn.Select(p => (JsonNode)p.ID.ToString()).ToArray()) });
        if (PersonOps.Error(r) is { } e) { _say($"Couldn't count them: {e}"); return; }
        var objs = (r["objects"] as JsonArray ?? new()).OfType<JsonObject>().Where(o => (Json.AsDouble(o["scripts"]) ?? 0) > 0).ToList();
        int scripts = (int)(Json.AsDouble(r["scripts"]) ?? 0), running = (int)(Json.AsDouble(r["running"]) ?? 0);
        if (scripts == 0) { _say($"No scripts in the {worn.Count} things you're wearing."); return; }
        string Where(JsonObject o) => UUID.TryParse(o.Str("id"), out var id) && worn.FirstOrDefault(p => p.ID == id) is { } p
            ? $" on {ObjectViews.AttachPointName((int)p.PrimData.AttachmentPoint)}" : "";
        _say($"{scripts} scripts in what you're wearing, {running} running. "
             + string.Join(" ", objs.Select(o => $"{o.Str("name") ?? "something"}{Where(o)}: {o["scripts"]}{(Json.AsDouble(o["running"]) is { } rn && rn < (Json.AsDouble(o["scripts"]) ?? 0) ? $", {rn:0} running" : "")}.")));
    }

    // --- status -------------------------------------------------------------------------------------------------------

    async Task StatusAsync(List<string> a)
    {
        var want = a.Select(w => w.ToLowerInvariant()).ToHashSet();
        bool All(string k) => want.Count == 0 || want.Contains(k);
        var c = _ops.Client;
        if (c == null || _ops.Me() is not { } me) { _say("Not logged in."); return; }
        var parts = new List<string>();
        if (All("doing"))
        {
            string doing = c.Self.SittingOn != 0 ? $"Sitting on {SeatName(c)}" : c.Self.Movement.Fly ? "Flying" : "Standing";
            if (c.Self.Movement.AlwaysRun) doing += ", always running";
            if (_following != null) doing += $", following {_following}";
            parts.Add(doing + ".");
        }
        if (All("region"))
        {
            var sim = c.Network.CurrentSim;
            var maturity = sim == null ? "" : Sojourn.Session.Map.Where.Rating(sim.Access);
            parts.Add($"Region {me.Region}{(maturity is { Length: > 0 } and not "unknown" ? $", {maturity}" : "")}.");
        }
        if (All("parcel"))
        {
            var r = await _ops.ReadAsync("TesselAgent", "parcelInfo");
            if (PersonOps.Error(r) is null)
            {
                var notes = new List<string>();
                if (r["options"] is JsonObject opts)
                    foreach (var (k, v) in opts)
                    {
                        if (v is not JsonValue jv || !jv.TryGetValue<bool>(out var on)) continue;
                        if (k == "damage") { if (on) notes.Add("damage on"); }
                        else if (!on && k is "fly" or "scripts" or "build" or "create" or "push" or "voice") notes.Add($"no {k}");
                    }
                parts.Add($"Parcel {r.Str("name") ?? "unnamed"}{(notes.Count > 0 ? ": " + string.Join(", ", notes) : "")}.");
            }
        }
        if (All("location"))
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"At {Math.Round(me.Pos.X):0}, {Math.Round(me.Pos.Y):0}, {Math.Round(me.Pos.Z):0}, facing {Bearings.Point(me.Heading)}, {Bearings.ToSl(me.Heading)} degrees."));
        if (All("money")) parts.Add(_s.BalanceKnown ? $"L${c.Self.Balance}." : "Balance not known yet.");
        if (All("time"))
        {
            TimeZoneInfo pacific;
            try { pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); } catch { pacific = TimeZoneInfo.Utc; }
            parts.Add($"Second Life time {TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pacific).ToString("h:mm tt", CultureInfo.InvariantCulture)}.");
        }
        // the song playing, asked for (a title read out on every change can be too much: Alt+0 turns those off)
        if (All("music") && Music?.Invoke() is var music && (music != null || want.Contains("music")))
            parts.Add(music ?? "No music on this parcel.");
        _say(parts.Count > 0 ? string.Join(" ", parts) : "//status doing, region, parcel, location, money, time or music.");
    }

    string SeatName(GridClient c)
    {
        var w = _s.Region;
        if (w != null && w.Prims.TryGetValue(c.Self.SittingOn, out var seat))
            return _s.Objects.RootOf(w, seat).Properties?.Name is { Length: > 0 } n ? n : "an object";
        return "an object";
    }
}
