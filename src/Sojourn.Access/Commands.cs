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
        var words = Split(line.TrimStart()[2..].TrimStart('/'));   // ////unsit as //unsit (a typed extra pair, 0.1.0.877)
        if (words.Count == 0) { Help(""); return false; }
        var verb = words[0].ToLowerInvariant().TrimEnd(':');   // "//build:" as the usage line writes it
        var rest = words.Skip(1).ToList();
        try
        {
            switch (verb)
            {
                case "help": Help(string.Join(" ", rest)); return true;
                case "links": Links?.Invoke(); return true;
                case "chatlog" or "chatlogs": ChatLog?.Invoke(string.Join(" ", rest)); return true;
                case "block": if (Block != null) await Block(string.Join(" ", rest)); return true;
                case "rebake" or "stopanimations" or "hover" or "displayname" or "maturity" or "emptytrash" or "emptylostandfound" or "saveoutfit" or "run":
                    if (Self != null) await Self(verb, string.Join(" ", rest));
                    return true;
                case "profile": if (Profile != null) await Profile(string.Join(" ", rest)); return true;
                case "notes": if (Notes != null) await Notes(string.Join(" ", rest)); return true;
                case "friend": if (Friend != null) await Friend(string.Join(" ", rest)); return true;
                case "unblock": if (Unblock != null) await Unblock(string.Join(" ", rest)); return true;
                case "blocks": if (Unblock != null) await Unblock(""); return true;
                case "holdwindow" when Environment.GetEnvironmentVariable("SOJOURN_TEST_HOLD_WINDOW") == "1":
                    // (the scripted test's: the window's thread held, for WindowWatch)
                    Thread.Sleep(TimeSpan.FromSeconds(double.Parse(rest[0], System.Globalization.CultureInfo.InvariantCulture)));
                    return true;
                case "blindconvoy" when Environment.GetEnvironmentVariable("SOJOURN_TEST_CONVOY_BLIND") == "1":
                    // (the scripted test's: driving after someone, them unseen for a while, WalkMode.Convoy.cs)
                    WalkMode?.BlindConvoy(double.Parse(rest[0], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(rest[1], System.Globalization.CultureInfo.InvariantCulture));
                    return true;
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
                case "thread": return ThreadCommand(AfterVerb(line));
                case "threads": Threads(); return true;
                case "kill": Kill(); return true;
                case "siminfo": await SimInfoAsync(); return true;
                case "parcelinfo": await ParcelInfoAsync(); return true;
                case "scriptinfo": await ScriptInfoAsync(rest); return true;
                case "beacon": await BeaconAsync(rest); return true;
                case "touch": if (Touch != null) await Touch(string.Join(" ", rest)); return true;
                case "details": if (Details != null) await Details(string.Join(" ", rest)); return true;
                case "pay": if (Pay != null) await Pay(string.Join(" ", rest)); return true;
                case "buy": if (Buy != null) await Buy(string.Join(" ", rest)); return true;
                case "switch": if (Switch != null) await Switch(string.Join(" ", rest)); return true;
                case "stop": if (WalkMode is { } walk) walk.RequestStop(); else _say("Not in a region yet."); return true;
                case "ride": if (Ride != null) await Ride(string.Join(" ", rest)); return true;
                case "ridestop" or "stopride": if (Ride != null) await Ride("stop"); return true;
                case "text": await TextAsync(string.Join(" ", rest)); return true;
                case "outfit": if (Outfit != null) await Outfit(); return true;
                case "upload": if (Upload != null) await Upload(AfterVerb(line)); return true;
                case "build": if (Build != null) await Build(AfterVerb(line)); return true;
                case "land": if (Land != null) await Land(AfterVerb(line)); return true;
                case "voice": if (Voice != null) await Voice(string.Join(" ", rest)); return true;
                case "imsound": if (ImSound != null) await ImSound(string.Join(" ", rest)); return true;
                case "pointing": if (Pointing != null) await Pointing(string.Join(" ", rest)); return true;
                case "sendlog": SendLog?.Invoke(); return true;
                case "log" or "sessionlog": SessionLog?.Invoke(); return true;
                case "clearcache": ClearCache?.Invoke(string.Join(" ", rest)); return true;
                case "newwindow": NewWindow?.Invoke(); return true;
                case "logout": LogOut?.Invoke(); return true;
                default:
                {
                    var guess = Typos.Closest(verb, CommandWords);
                    _say($"No command {verb}. {(guess != null ? $"Did you mean //{guess}? " : "")}Control+Up brings your line back; //help lists them.");
                    return false;
                }
            }
        }
        catch (Exception e) { _say($"//{verb} didn't work: {e.Message}"); return false; }
    }

    IEnumerable<string> CommandWords => All.Select(u => u.Name);

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
        ("find", "//find person NAME | //find object NAME | //find NAME", "where everyone and everything with that name is, the nearest first"),
        ("describe", "//describe | //describe me | //describe NAME | //describe person NAME | //describe object NAME | //describe more",
            "the assistant describes where you are, you and what you're wearing, someone or something; more: the last one in detail"),
        ("face", "//face DEGREES | //face DIRECTION | //face person NAME | //face object NAME", "turn: degrees with 0 east, counter-clockwise, as in Second Life"),
        ("sit", "//sit NAME | //sit ground", "sit on the nearest thing with that name, or on the ground"),
        ("stand", "//stand (or //unsit)", "stand up"),
        ("follow", "//follow NAME | //follow NAME METRES | //follow stop | //follow", "follow someone (ask first), stop, or say who; at the wheel of something steered by the arrows, drive after them, METRES behind if given (10 if not)"),
        ("tp", "//tp list | //tp N | //tp NAME | //tp Region/X/Y/Z | //tp SLURL", "teleport by landmark (Radegast Landmarks folder, else Landmarks), or to a place"),
        ("status", "//status [doing|region|parcel|location|health|money|time|music]", "what you're doing, where you are, your health where damage is on, your L$, the time in Second Life, the music playing"),
        ("route", "//route NOTECARD | //route (the lines on the clipboard) | //route stop | //route resume",
            "play a route: a notecard's (or the clipboard's) // lines one at a time, each walk finished before the next, other lines read as notes"),
        ("record", "//record | //record note WORDS | //record undo | //record pause | //record save [NAME] | //record discard",
            "record a route by walking it (R in walk mode): start, or while recording how it's going; a note here; take back the last point; pause or go on; save it as a notecard in Routes; or throw it away"),
        ("pause", "//pause [SECONDS]", "wait (in a route: before its next line; 1 second unless said)"),
        ("thread", "//thread COMMAND", "run a command without waiting for it: in a route, the next line goes on at once"),
        ("threads", "//threads", "what's running: a route, a walk, following, a pause, commands started by //thread"),
        ("kill", "//kill", "stop everything running: a route, a walk, following, pauses, commands started by //thread"),
        ("siminfo", "//siminfo", "the region: its type, maturity, people, scripts, time dilation and frame rate, and its server where the grid says"),
        ("parcelinfo", "//parcelinfo", "every parcel in the region: its name, area, who may enter and traffic, the one you're on first, and its allowed and banned lists' sizes where you may see them"),
        ("scriptinfo", "//scriptinfo | //scriptinfo parcel", "the scripts in what you're wearing, per attachment; or the script memory of the objects on the parcel you're on"),
        ("beacon", "//beacon NAME | //beacon off | //beacon",
            "a sound on someone or something to walk to (by hand in walk mode, or by //go), until you reach it; off; or where it is"),
        ("details", "//details NAME", "whose something within 30 metres is, who made it, its description, what you may do with it"),
        ("touch", "//touch NAME | //touch OBJECT PART | //touch NAME face top (or bottom, front, far, left, right, inside, a number)",
            "touch the nearest thing or part within 10 metres with that name (an object whose only touchable part is a child: that part), on a face if given"),
        ("pay", "//pay NAME | //pay NAME 50",
            "pay someone here, or something within 30 metres that takes payment (a tip jar); the amount asked if not given, and your confirmation either way"),
        ("buy", "//buy NAME", "buy the nearest thing with that name within 30 metres that's for sale; your confirmation with its price"),
        ("switch", "//switch | //switch flip | //switch main | //switch branch",
            "the next rail switch and how it's set; throw it, or set it to the main line or the branch (walk mode's W and Shift+W)"),
        ("stop", "//stop", "stop: seated on something moving, it's stopped by its controls, as Space in walk mode; otherwise a walk, a follow or a route"),
        ("ride", "//ride | //ride stop | //ride describe | //ride more",
            "record a ride (C in walk mode): pictures every few seconds and what's around, kept on this PC; stop it (it stops by itself when you stand up or the ride stops); have the assistant tell it (Shift+C in walk mode), or tell more"),
        ("text", "//text | //text NAME", "floating text: what you're sitting on shows (Shift+V in walk mode), or the nearest thing with that name within 30 metres"),
        ("outfit", "//outfit", "what you're wearing, by name from the worn list: clothing, attachments and where, HUDs, body parts"),
        ("saveoutfit", "//saveoutfit NAME", "save what you're wearing as an outfit: a folder NAME in My Outfits with a link to each thing, as the official viewer's Save Outfit"),
        ("build", "//build | //build COMMAND (hollow 50, colour red, new box, undo, help)",
            "the build editor (Control+B), or one of its commands on what you're editing"),
        ("land", "//land | //land COMMAND (options, fly off, ban NAME, for sale 500, buy, help)",
            "About Land (Control+L) on the parcel you're on, or one of its commands"),
        ("upload", "//upload | //upload PATH", "upload a picture, sound, animation or model: with no path, choose the file (Control+U)"),
        ("voice", "//voice | //voice people | //voice mute all | //voice unmute all | //voice reconnect | //voice answer | //voice decline | //voice mute NAME | //voice unmute NAME | //voice volume NAME N",
            "who's in voice and speaking, your mic; the people in voice to act on (Control+F4); everyone else muted for you, or everyone heard again; voice started afresh; answer or decline a call ringing; one person muted, or how loud they are from 0 to 2, for you alone"),
        ("imsound", "//imsound NAME SOUND | //imsound NAME usual | //imsound NAME | //imsound",
            "a sound of its own for someone's IMs, or a group's chat, in place of the usual one; usual takes it back; with only a name, theirs; with nothing, the sounds and who has one"),
        ("pointing", "//pointing NAME | //pointing off | //pointing",
            "hear what someone nearby points at in their own viewer (a right click on something does), Control+Shift+P going to it; off stops; with nothing, whose you hear and what they last pointed at"),
        ("block", "//block NAME", "block anyone by name, or something nearby: what they say, send or ask stops reaching you"),
        ("rebake", "//rebake", "send your look to the grid again: when you're told you look grey or blurred"),
        ("stopanimations", "//stopanimations", "stop every animation on you, stand up if seated, and take back what scripts were allowed to animate you: when you're told you're stuck in a pose"),
        ("hover", "//hover | //hover N", "your hover height, or change it, from -2 to 2 metres, so your feet meet the floor"),
        ("displayname", "//displayname | //displayname NAME", "your display name and when it can change; or change it, after you confirm"),
        ("maturity", "//maturity | //maturity general, moderate or adult", "the highest rating of region you go into, and how high your account may go; or change it"),
        ("emptytrash", "//emptytrash", "delete everything in the Trash for good, after you confirm"),
        ("emptylostandfound", "//emptylostandfound", "delete everything in Lost and Found for good (what's returned from the world lands there), after you confirm"),
        ("run", "//run | //run on | //run off", "always run, or walk again: walking, //go and following all run while it's on"),
        ("profile", "//profile | //profile NAME", "your own profile, to read and change; or anyone's, by name"),
        ("notes", "//notes NAME", "your own notes on someone, by name, in their profile: only you see them"),
        ("friend", "//friend NAME", "offer anyone friendship, by name"),
        ("unblock", "//unblock NAME", "take someone or something off your block list"),
        ("blocks", "//blocks", "your block list, as Control+Shift+Y: Enter or Delete unblocks one"),
        ("links", "//links", "the links seen in chat, IMs and notices, as Control+Shift+K: Enter opens one"),
        ("chatlog", "//chatlog | //chatlog NAME", "what's been said, kept on this PC: the logs, or the one with that name"),
        ("logout", "//logout", "log out and go back to the login window, after you say so"),
        ("about", "//about", "which version of Sojourn Access this is, and where your settings and logs are"),
        ("sendlog", "//sendlog", "send a session's log to Studio Quire, with a note on what happened; other people's words are left out"),
        ("log", "//log", "read a session's log, as Send a log would send it: what happened, other people's words left out"),
        ("clearcache", "//clearcache | //clearcache cancel", "clear the cache when Sojourn Access next starts: what Second Life sent, which comes again as it's needed; or take that back"),
        ("newwindow", "//newwindow", "another account in a window of its own, at the login"),
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
        // a name alone: someone nearby, else something (she typed it so at a lift's top, 2026-11-05, and heard the usage twice)
        if (kind is not ("xyz" or "person" or "object") && !double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            kind = await _ops.FindPersonAsync(string.Join(" ", a)) != null ? "person" : "object";
            a = a.Prepend(kind).ToList();
        }
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
    /// <summary>Always run (the walk_always_run setting): a //go runs.</summary>
    public Func<bool> AlwaysRun { get; set; } = () => false;

    /// <summary>The region's autopilot toward a point, running with Always run on. OpenSim's autopilot turns running off
    /// as it starts (ScenePresence.MoveToTargetHandle), so it's sent again after, and again half a second on, as the
    /// region may start the walk after the first has landed; what Second Life's does isn't known.</summary>
    void AutoPilotTo(GridClient c, double x, double y, double z)
    {
        c.Self.AutoPilot(x, y, z);
        if (!AlwaysRun()) return;
        c.Self.Movement.AlwaysRun = true;
        _ = Task.Delay(500).ContinueWith(_ => { if (ReferenceEquals(_ops.Client, c) && c.Self.Movement.AlwaysRun) c.Self.Movement.AlwaysRun = true; }, TaskScheduler.Default);
    }
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
            finally
            {
                if (_heading == heading) _heading = null;
                // (ended: nothing for walk mode's next key to cancel)
                Interlocked.CompareExchange(ref _walk, null, walk);
            }
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
            AutoPilotTo(c, g.X, g.Y, g.Z);
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
            AutoPilotTo(c, g.X, g.Y, g.Z);
            var got = await LegAsync(leg, last ? (r.Moved ? 0.6 : near) : 0.5, ct, origin);
            if (got == Leg.Cancelled) return false;
            if (got == Leg.Reached)
            {
                if (!last) continue;
                return r.Moved ? await WatchWalkAsync(leg, 0.8, ct, origin, (target, what)) : await WatchWalkAsync(target, near, ct, origin);
            }
            // stuck on the way: something the model hadn't (a thin post, a doorway the region won't fit): marked, planned again
            CancelAutoPilot();
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

    /// <summary>A point past the edge of the region whose frame it's in (a recorded route's step across a border).</summary>
    static bool Beyond(Vector3 p) => p.X < 0 || p.X >= 256 || p.Y < 0 || p.Y >= 256;

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
        if (Beyond(to)) return null;
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
        CancelAutoPilot();
        var offer = tpHint != null ? $" {tpHint} would teleport you there." : "";
        if (!TeleportFallback() || _ops.Me() is null)
        {
            _say($"No way to walk to {what} found.{offer}");
            return false;
        }
        if (TeleportWontLand(target) is { } why)
        {
            _say($"No way to walk to {what} found, and a teleport wouldn't land there: {why}. If it's a person, asking them to offer you a teleport may work.");
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
    /// is remembered (TeleportWontLand). Someone standing at the spot (`//go tp person`, a walk to them with no way, the
    /// assistant's walk to them) is landed beside, facing them, and said where they are (BesideAsync): sent to their
    /// own position, the region stood her on their head (2026-11-08).</summary>
    async Task<(bool Ok, string Line)> TeleportNearAsync(Vector3 target, string what)
    {
        if (_ops.Me() is not { } me) return (false, "Not in a region yet.");
        var person = await PersonAtAsync(target);
        var at = person != null ? await BesideAsync(person.Pos, me.Pos) : new Vector3(target.X, target.Y, Math.Max(target.Z, me.Pos.Z));
        OwnTeleport = true;
        JsonObject r;
        try
        {
            r = await _ops.DoAsync("LLAgent", "requestTeleport", new JsonObject
            {
                ["regionname"] = me.Region, ["x"] = (double)at.X, ["y"] = (double)at.Y, ["z"] = (double)at.Z,
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
        if (_ops.Me() is { } now && Flat(now.Pos, at) is var off && off > 3)
        {
            var landing = SentElsewhere(at) is { } why ? $" ({why})" : "";
            _missed = (target, Environment.TickCount64, "it landed elsewhere");
            return (false, $"The teleport landed you away from {what}{landing}: {what} is {_ops.Where(target) ?? Bearings.Metres(off)}.");
        }
        if (person == null || _ops.Me() is not { } landed) return (true, $"Teleported to {what}.");
        // (where they are now, seen from here)
        var them = (await _ops.PeopleAsync(64)).FirstOrDefault(p => p.Id == person.Id)?.Pos ?? person.Pos;
        await _ops.DoAsync("TesselAgent", "move", new JsonObject
        {
            ["face"] = Bearings.Compass(landed.Pos.X, landed.Pos.Y, them.X, them.Y), ["secs"] = 0.3, ["wait"] = true,
        });
        return (true, $"Teleported to {what}: {person.Name} is {_ops.Where(them) ?? "near"}.");
    }

    /// <summary>Someone at a spot: within a metre of it across and two up or down, the nearest.</summary>
    async Task<PersonOps.Person?> PersonAtAsync(Vector3 at) =>
        (await _ops.PeopleAsync(512)).Where(p => Flat(p.Pos, at) < 1 && Math.Abs(p.Pos.Z - at.Z) < 2).MinBy(p => Flat(p.Pos, at));

    /// <summary>How far from someone a teleport to them lands (the assistant's go tool hops as far short).</summary>
    const float Beside = 2f;

    /// <summary>Where a teleport to someone lands: on their floor, a couple of metres off on the side she comes from, as
    /// the official viewer's teleport to a person roughly does. With the collision model, planned out from where they
    /// stand (PathPlanner): the nearest spot to that which their floor reaches in a near-straight line (back from a
    /// stage's edge, this side of a wall), at its height; another side when that one is off what they stand on or
    /// behind something; that first side if none is better. Without it, or where the model hasn't what they stand on,
    /// that side at their height, a little over it, so a person on a stage lands her on the stage, not the ground.</summary>
    async Task<Vector3> BesideAsync(Vector3 them, Vector3 from)
    {
        var away = new System.Numerics.Vector2(from.X - them.X, from.Y - them.Y);
        double toward = away.LengthSquared() > 0.01f ? Math.Atan2(away.Y, away.X) : 0;
        var centre = new System.Numerics.Vector2(them.X, them.Y);
        System.Numerics.Vector2 Side(int k) => centre + Beside * new System.Numerics.Vector2((float)Math.Cos(toward + k * Math.PI / 4), (float)Math.Sin(toward + k * Math.PI / 4));
        var straight = new Vector3(Side(0).X, Side(0).Y, them.Z + 0.3f);
        if (Planner is not { CanAsk: true } planner || Beyond(them)) return straight;
        // her own middle over her feet: what's added to a floor to stand her on it
        float half = FeetZ?.Invoke() is { } feet && _ops.Me() is { } me ? Math.Clamp(me.Pos.Z - feet, 0.5f, 1.5f) : 0.95f;
        Vector3? first = null;
        foreach (int k in new[] { 0, 1, -1, 2, -2, 3, -3, 4 })
        {
            var side = Side(k);
            Walk.PathPlanner.Plan? plan;
            try { plan = await planner.PlanAsync(centre, them.Z - 0.95f, side, null, default, them.Z); }
            catch (Exception x) { Walk.WalkMode.Log($"beside: {x.Message}"); return first ?? straight; }
            if (plan is { FloorKnown: false }) return straight;
            if (plan is not { Waypoints.Count: > 0 }) continue;
            var end = plan.Waypoints[^1];
            float apart = System.Numerics.Vector2.Distance(end, centre);
            var spot = new Vector3(end.X, end.Y, plan.GoalFloor + half + 0.3f);
            bool open = apart >= 1 && plan.Length < apart + 1, level = Math.Abs(plan.GoalFloor - plan.StartFloor) < 0.5f;
            Walk.WalkMode.Log($"beside {them}: side {k} at {end}, {apart:0.0} m off, {plan.Length:0.0} m round, floor {plan.GoalFloor:0.00} (theirs {plan.StartFloor:0.00})");
            if (open && level) return spot;
            if (open) first ??= spot;
        }
        return first ?? straight;
    }

    /// <summary>Waits for the walk to end (stopped near the target, stalled, or a minute), then says Radegast's line;
    /// walking to a stand-in for a goal out of reach (<paramref name="outOfReach"/>), how near that is to it.</summary>
    async Task<bool> WatchWalkAsync(Vector3 target, double near, CancellationToken ct, ulong origin, (Vector3 At, string What)? outOfReach = null)
    {
        long start = Environment.TickCount64, stillSince = start;
        bool moved = false, again = false, pushed = false, letGo = false;
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
            // a goal past the border not crossed yet: a route's next line is in the next region's frame, so it isn't done
            // till then. The region's autopilot stops the avatar at the edge and doesn't cross it (seen on the local grid:
            // standing there 15 s, across a second after a forward press), so stood there, it's walked across by the
            // forward control, once
            bool crossing = Beyond(target) && me.Region == origin;
            // a person or a thing (stopping 1.5 m short): the region's autopilot heads for its middle and pushed on into
            // the visitor's body, never still enough to finish (42 s, 2026-11-08), so it's let go once within reach
            if (!crossing && !letGo && near >= 1.5 && off <= near)
            {
                letGo = true;
                CancelAutoPilot();
            }
            if (crossing && still > 1500 && !pushed)
            {
                pushed = true;
                await PushAcrossAsync(c, me.Pos, target, origin);
                stillSince = Environment.TickCount64;
                continue;
            }
            bool arrived = !crossing && off <= near && still > 700, late = now - start > 60_000;
            bool stalled = still > (crossing ? 3000 : moved ? 2000 : 4000);
            if (stalled && !arrived && !again && me.Region != origin && off > near)
            {
                // a crossing on the way: the new region doesn't carry the old one's autopilot on, so asked again once
                again = true;
                Utils.LongToUInts(origin, out var ox, out var oy);
                AutoPilotTo(c, ox + (double)target.X, oy + (double)target.Y, target.Z);
                stillSince = now;
                moved = false;
                continue;
            }
            if (!arrived && !stalled && !late) continue;
            if (!arrived) CancelAutoPilot();
            if (outOfReach is { } o && off < 2.5)
            {
                _say($"Finished walking, as near to {o.What} as the way goes: {o.What} is {_ops.Where(o.At) ?? Bearings.Metres(Flat(me.Pos, o.At))}.");
                return true;
            }
            bool done = arrived || off < 2.5 && !crossing;
            _say(string.Create(CultureInfo.InvariantCulture,
                $"Finished walking, {(off < 3 ? (off < 1 ? "under a metre" : Bearings.MetresTenths(off)) : Bearings.Metres(off))} from the destination{(done ? "" : crossing ? " (it didn't cross into the next region)" : " (something was in the way)")}."));
            return done;
        }
        return false;
    }

    /// <summary>Walks the avatar across the border by the forward control, facing the goal: held until the region
    /// changes (3 s at most), then let go.</summary>
    async Task PushAcrossAsync(GridClient c, Vector3 from, Vector3 target, ulong origin)
    {
        var mv = c.Self.Movement;
        mv.BodyRotation = mv.HeadRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan2(target.Y - from.Y, target.X - from.X));
        mv.AtPos = true;
        mv.SendUpdate(true);
        for (int i = 0; i < 30 && c.Network.CurrentSim?.Handle == origin; i++)
        {
            await Task.Delay(100, CancellationToken.None);
            if (i % 2 == 1) mv.SendUpdate(false);   // (held, as a held key's flags are sent)
        }
        mv.AtPos = false;
        mv.SendUpdate(true);
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
        CancelAutoPilot();
    }

    /// <summary>The region's autopilot cancelled. LibreMetaverse cancels it by a press of forward (AtPos on, sent, then
    /// off), which a vehicle whose script takes the keys hears as Up: a train's throttle opened a step by the person's
    /// first tap after a //go (walk mode's keys stop the walk first). Seated there's no autopilot to cancel, so nothing
    /// is sent.</summary>
    void CancelAutoPilot()
    {
        if (_ops.Client is { } c && c.Self.SittingOn == 0) c.Self.AutoPilotCancel();
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

    /// <summary>//find: everyone or everything that matches, nearest first, as Radegast's says each; the first eight, and how many more.</summary>
    async Task FindAsync(List<string> a)
    {
        if (a.Count == 0) { Help("find"); return; }
        var kind = a[0].ToLowerInvariant();
        bool person = kind == "person", obj = kind == "object";
        var name = string.Join(" ", person || obj ? a.Skip(1) : a);
        if (name.Length == 0) { Help("find"); return; }
        var hits = new List<(string Name, Vector3 Pos, double Dist)>();
        if (!obj) hits.AddRange((await _ops.FindPeopleAsync(name)).Select(p => (p.Name, p.Pos, p.Dist)));
        if (!person) hits.AddRange((await _ops.ObjectsAsync(96, 40, name)).Select(t => (t.Name, t.Pos, t.Dist)));
        if (hits.Count == 0) { _say($"No {(person ? "one" : obj ? "object" : "one or nothing")} called {name} nearby."); return; }
        const int Most = 8;
        var said = hits.OrderBy(h => h.Dist).Take(Most).Select(h => $"{h.Name} is {_ops.Where(h.Pos, full: true) ?? "somewhere"}.").ToList();
        if (hits.Count > Most) said.Add($"And {hits.Count - Most} more.");
        _say(string.Join(" ", said));
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
        // first, or the vehicle's keys going as she stands are heard as the vehicle letting go of them
        if (WalkMode is { DrivingAfter: { } car } w) w.EndConvoy($"No longer driving after {car}.");
        await _ops.DoAsync("LLAgent", "requestStand");
        _say("Standing.");
    }

    // --- follow -----------------------------------------------------------------------------------------------------

    string? _following;
    CancellationTokenSource? _followStop;

    async Task FollowAsync(List<string> a)
    {
        var name = string.Join(" ", a);
        if (name.Length == 0) { _say(WalkMode?.DrivingAfter is { } after ? $"Driving after {after}." : _following != null ? $"Following {_following}." : "Not following anyone."); return; }
        if (name.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (WalkMode is { DrivingAfter: { } car } w) w.EndConvoy($"No longer driving after {car}.");
            else StopFollowing(quiet: false);
            return;
        }
        // a last word that's a number, unless it's in their name: how far behind, when driving after them
        float? gap = null;
        var p = await _ops.FindPersonAsync(name);
        if (p == null && a.Count > 1 && float.TryParse(a[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var metres))
        {
            gap = Math.Clamp(metres, 1f, 50f);
            p = await _ops.FindPersonAsync(string.Join(" ", a.SkipLast(1)));
        }
        // (the name without the gap: on 2026-11-08 she heard "Nobody called NAME 3 nearby.")
        if (p == null) { _say($"Nobody called {(gap != null ? string.Join(" ", a.SkipLast(1)) : name)} nearby."); return; }
        // following walks; seated, a vehicle steered by its keys is driven after them, else a beacon (DriveAfter says which)
        if (_ops.Client is { Self.SittingOn: not 0 } && WalkMode is { } walk)
        {
            StopFollowing(quiet: true);
            walk.DriveAfter(p.Name, p.Id, gap);
            return;
        }
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
        CancelAutoPilot();
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
    public Action? Links { get; set; }

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
    public Func<string, Task>? Details { get; set; }
    public Func<string, Task>? Block { get; set; }
    public Func<string, Task>? Profile { get; set; }
    public Func<string, Task>? Notes { get; set; }
    public Action<string>? ChatLog { get; set; }
    public Action? LogOut { get; set; }
    public Func<string, string, Task>? Self { get; set; }
    public Func<string, Task>? Friend { get; set; }
    public Func<string, Task>? Unblock { get; set; }
    public Func<string, Task>? Pay { get; set; }
    public Func<string, Task>? Buy { get; set; }

    /// <summary>`//switch`'s words, to walk mode's rail switches (Walk/WalkMode.Switches.cs).</summary>
    public Func<string, Task>? Switch { get; set; }

    /// <summary>`//ride`'s words, to the app's ride recording (MainForm.Rides.cs).</summary>
    public Func<string, Task>? Ride { get; set; }

    /// <summary>`//text [NAME]`: the seat's floating text (as Shift+V in walk mode), or the nearest thing's with that name
    /// within 30 m (its root's, as Objects lists it).</summary>
    async Task TextAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            if (WalkMode is { } walk) walk.SeatTextLine(); else _say("Not in a region yet.");
            return;
        }
        var hit = (await _ops.ObjectsAsync(30, 40, name)).FirstOrDefault(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (hit == null) { _say($"Nothing called {name} within 30 metres."); return; }
        _say(Walk.WalkMode.FloatingText(hit.Text) is { } text ? $"{hit.Name} shows: {Walk.WalkMode.Sentence(text)}" : $"{hit.Name} shows no floating text.");
    }

    /// <summary>//upload [PATH]: the words after the verb as typed (a Windows path's backslashes and spaces kept).</summary>
    public Func<string, Task>? Upload { get; set; }

    static string AfterVerb(string line)
    {
        var t = line.TrimStart()[2..].TrimStart();
        int sp = t.IndexOfAny(new[] { ' ', '\t' });
        return sp < 0 ? "" : t[(sp + 1)..].Trim();
    }

    /// <summary>`//build`'s words as typed, to the build editor (MainForm.Build.cs).</summary>
    public Func<string, Task>? Build { get; set; }

    /// <summary>`//land`'s words as typed, to About Land (MainForm.Land.cs).</summary>
    public Func<string, Task>? Land { get; set; }

    /// <summary>`//imsound`'s words (MainForm.ImSounds.cs).</summary>
    public Func<string, Task>? ImSound { get; set; }

    /// <summary>`//pointing`'s words: whose pointing is heard (MainForm.Pointing.cs).</summary>
    public Func<string, Task>? Pointing { get; set; }

    /// <summary>`//outfit`: what's worn by name (MainForm.Outfit.cs).</summary>
    public Func<Task>? Outfit { get; set; }

    /// <summary>`//voice`'s words (MainForm.Voice.cs).</summary>
    public Func<string, Task>? Voice { get; set; }

    /// <summary>`//log`, `//clearcache`, `//newwindow` (MainForm.Session.cs).</summary>
    public Action? SessionLog { get; set; }
    public Action<string>? ClearCache { get; set; }
    public Action? NewWindow { get; set; }

    /// <summary>`//status music`: the parcel's stream and what it's playing, or null when this parcel has none.</summary>
    public Func<string?>? Music { get; set; }

    CancellationTokenSource? _route;
    (string Name, List<string> Lines, int Next)? _routeAt;
    /// <summary>The route playing now and the step it's on (//threads).</summary>
    volatile string? _playing;

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
                _playing = $"the route {name}, at step {step} of {steps}";
                _say($"Step {step} of {steps}.");
                bool ok = await RunAsync(line);
                if (stop.IsCancellationRequested) { _routeAt = (name, lines, i); _say($"Route stopped at step {step} of {steps}. //route resume does it again."); return; }
                if (!ok) { _routeAt = (name, lines, i); _say($"Route paused at step {step} of {steps}. //route resume tries it again; //route stop ends it."); return; }
                await Task.Delay(300);
            }
            _routeAt = null;
            _say($"Route finished: {name}.");
        }
        finally { if (_route == stop) { _route = null; _playing = null; } }
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
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_route?.Token ?? CancellationToken.None, _kill.Token);
        var until = DateTime.UtcNow.AddSeconds(secs);
        lock (_pauses) _pauses.Add(until);
        try { await Task.Delay(TimeSpan.FromSeconds(secs), stop.Token); }
        catch (OperationCanceledException) { return false; }
        finally { lock (_pauses) _pauses.Remove(until); }
        return true;
    }

    // --- thread, threads, kill (Radegast's ThreadCommand) --------------------------------------------------------------
    // Radegast runs typed commands one at a time on a worker, so //thread is how one runs beside the rest, and //kill
    // stops those threads and empties the queue. Here a typed command never holds the next one; what waits is a route's
    // next line, so //thread lets a route go on at once. What //kill can stop is what runs on: a route, a walk (with its
    // autopilot), following, pauses. A teleport under way and the rest, which end by themselves, can't be.

    readonly List<(string Line, Task<bool> Task)> _threaded = new();
    readonly List<DateTime> _pauses = new();
    CancellationTokenSource _kill = new();

    /// <summary>`//thread COMMAND`: the command started and not waited for (true at once, so a route goes on).</summary>
    bool ThreadCommand(string rest)
    {
        var cmd = rest.Trim().TrimStart('/').Trim();
        var verb = cmd.Split(' ', 2)[0].ToLowerInvariant();
        if (cmd.Length == 0) { _say("//thread COMMAND runs it without waiting: //thread go 20, or //thread //follow Kestrel."); return false; }
        if (verb is "thread" or "threads" or "kill" or "route" or "record") { _say($"//{verb} isn't one to run alongside: type it as it is."); return false; }
        var line = "//" + cmd;
        var task = RunAsync(line);
        if (task.IsCompleted) return true;   // (done already: nothing running alongside)
        lock (_threaded) _threaded.Add((line, task));
        _ = task.ContinueWith(_ => { lock (_threaded) _threaded.RemoveAll(t => t.Task == task); }, TaskScheduler.Default);
        return true;
    }

    /// <summary>`//threads`: what's running now.</summary>
    void Threads()
    {
        var running = Running();
        lock (_threaded)
            foreach (var (line, _) in _threaded) running.Add($"{line}, started by //thread");
        if (OwnTeleport) running.Add("a teleport");
        _say(running.Count == 0 ? "Nothing running." : $"Running: {string.Join("; ", running)}. //kill stops it all.");
    }

    /// <summary>What //kill stops, in words.</summary>
    List<string> Running()
    {
        var list = new List<string>();
        if (_route != null) list.Add(_playing ?? "a route");
        if (_walk != null && _following == null) list.Add(_heading is { } h ? $"the walk to {h.Name}" : "a walk");
        if (_following != null) list.Add($"following {_following}");
        int pauses;
        double longest;
        lock (_pauses) { pauses = _pauses.Count; longest = pauses == 0 ? 0 : (_pauses.Max() - DateTime.UtcNow).TotalSeconds; }
        if (pauses > 0) list.Add(pauses == 1 ? $"a pause, {Math.Max(1, Math.Round(longest)):0} seconds left" : $"{pauses} pauses");
        return list;
    }

    /// <summary>`//kill`: everything running stopped, said.</summary>
    void Kill()
    {
        var stopped = Running();
        int threaded;
        lock (_threaded) threaded = _threaded.Count;
        var kill = _kill;
        _kill = new CancellationTokenSource();
        kill.Cancel();
        _route?.Cancel();
        StopWalking();
        StopFollowing(quiet: true);
        var said = stopped.Count == 0 && threaded == 0 ? "Nothing was running."
            : stopped.Count == 0 ? "Stopped what //thread started."
            : $"Stopped {string.Join(", ", stopped)}.";
        _say(said + (OwnTeleport ? " A teleport under way can't be stopped." : ""));
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
        // its server, as the region's handshake gives it (RegionInfo3): empty or zero where a grid leaves it out
        var server = new List<string>();
        if (sim.CPUClass > 0) server.Add($"CPU class {sim.CPUClass}");
        if (sim.CPURatio > 1) server.Add($"{sim.CPURatio} regions per CPU");
        else if (sim.CPURatio == 1) server.Add("a CPU of its own");
        if (!string.IsNullOrWhiteSpace(sim.ColoLocation)) server.Add($"data centre {sim.ColoLocation.Trim()}");
        if (server.Count > 0) parts.Add($"Its server: {string.Join(", ", server)}.");
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
        var (traffic, lists) = await ParcelCountsAsync(c, sim, parcels);
        static string Count(int n, string what) => n == 0 ? $"nobody {what}" : $"{n} {what}";
        var lines = parcels.Select(p =>
        {
            var name = string.IsNullOrWhiteSpace(p.Name) ? "unnamed" : p.Name;
            var who = (p.Flags & ParcelFlags.UseAccessList) != 0 ? ", allowed list only" : (p.Flags & ParcelFlags.UseAccessGroup) != 0 ? ", its group only" : "";
            var visits = traffic.TryGetValue(p.LocalID, out var t) ? string.Create(CultureInfo.InvariantCulture, $", traffic {t:0}") : "";
            var listed = lists.TryGetValue(p.LocalID, out var l) ? $", {Count(l.Allowed, "allowed")} and {Count(l.Banned, "banned")}" : "";
            var desc = string.IsNullOrWhiteSpace(p.Desc) ? "" : $": {p.Desc.Trim()}";
            return $"{(p.LocalID == here ? "Here, " : "")}{name}, {p.Area} square metres{who}{visits}{listed}{desc}.";
        });
        // the parcels' descriptions are their owners' words: the session log marks the line as quoting someone
        Action<string> say = parcels.Any(p => !string.IsNullOrWhiteSpace(p.Desc)) ? Quoted : _say;
        say($"{parcels.Count} {(parcels.Count == 1 ? "parcel" : "parcels")}. " + string.Join(" ", lines));
    }

    /// <summary>Each parcel's traffic (asked of the region: viewers don't keep it) and the sizes of its allowed and banned
    /// lists, which the region sends only to whoever may see them (the parcel's owner, or its group's managers), so only
    /// those parcels say them. Asked of every parcel at once, then what came in up to 3 seconds (Radegast's said 0 for the
    /// rest, which read as nobody).</summary>
    async Task<(Dictionary<int, float> Traffic, Dictionary<int, (int Allowed, int Banned)> Lists)> ParcelCountsAsync(GridClient c, Simulator sim, List<Parcel> parcels)
    {
        var traffic = new System.Collections.Concurrent.ConcurrentDictionary<int, float>();
        var allowed = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        var banned = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        void Dwell(object? _, PacketReceivedEventArgs e)
        {
            if (e.Simulator == sim && e.Packet is LibreMetaverse.Packets.ParcelDwellReplyPacket d) traffic[d.Data.LocalID] = d.Data.Dwell;
        }
        void Access(object? _, PacketReceivedEventArgs e)
        {
            if (e.Simulator != sim || e.Packet is not LibreMetaverse.Packets.ParcelAccessListReplyPacket r) return;
            // an empty list comes as one blank entry; a long one in several packets
            int n = r.List.Count(t => t.ID != UUID.Zero);
            var into = (r.Data.Flags & (uint)AccessList.Ban) != 0 ? banned : (r.Data.Flags & (uint)AccessList.Access) != 0 ? allowed : null;
            into?.AddOrUpdate(r.Data.LocalID, n, (_, was) => was + n);
        }
        c.Network.RegisterCallback(LibreMetaverse.Packets.PacketType.ParcelDwellReply, Dwell);
        c.Network.RegisterCallback(LibreMetaverse.Packets.PacketType.ParcelAccessListReply, Access);
        try
        {
            foreach (var p in parcels)
            {
                c.Parcels.RequestDwell(sim, p.LocalID);
                c.Parcels.RequestParcelAccessList(sim, p.LocalID, AccessList.Access, 0);
                c.Parcels.RequestParcelAccessList(sim, p.LocalID, AccessList.Ban, 0);
            }
            // every parcel's traffic in (an owner's lists come with it) and a moment more, or 3 s at most
            var asked = Environment.TickCount64;
            while (Environment.TickCount64 - asked < 3000 && !(traffic.Count >= parcels.Count && Environment.TickCount64 - asked > 800))
                await Task.Delay(100);
        }
        finally
        {
            c.Network.UnregisterCallback(LibreMetaverse.Packets.PacketType.ParcelDwellReply, Dwell);
            c.Network.UnregisterCallback(LibreMetaverse.Packets.PacketType.ParcelAccessListReply, Access);
        }
        var lists = new Dictionary<int, (int, int)>();
        foreach (var p in parcels)
            if (allowed.TryGetValue(p.LocalID, out var a) && banned.TryGetValue(p.LocalID, out var b)) lists[p.LocalID] = (a, b);
        return (new Dictionary<int, float>(traffic), lists);
    }

    async Task ScriptInfoAsync(List<string> a)
    {
        if (a.Count > 0 && a[0].Equals("parcel", StringComparison.OrdinalIgnoreCase)) { await ParcelScriptsAsync(); return; }
        if (a.Count > 0 && !a[0].Equals("avatar", StringComparison.OrdinalIgnoreCase)) { Help("scriptinfo"); return; }
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

    /// <summary>`//scriptinfo parcel`: the script memory and URLs the objects on the parcel she's on use, of what it has,
    /// and the objects using most (the region's LandResources capability, as Radegast's and the viewers' Script
    /// Information).</summary>
    async Task ParcelScriptsAsync()
    {
        if (_ops.Client is not { } c || c.Network.CurrentSim is not { } sim) { _say("Not in a region yet."); return; }
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        UUID parcel;
        try { parcel = await c.Parcels.RequestRemoteParcelIDAsync(c.Self.SimPosition, sim.Handle, sim.ID, wait.Token); }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException) { parcel = UUID.Zero; }
        if (parcel == UUID.Zero) { _say("The region didn't say which parcel this is."); return; }
        var (ok, info) = await c.Parcels.GetParcelResourcesAsync(parcel, true, wait.Token);
        if (!ok || info == null) { _say("This region doesn't say how much script memory the parcel's objects use."); return; }
        var parts = new List<string>();
        long used = Get(info.SummaryUsed, "memory"), have = Get(info.SummaryAvailable, "memory");
        long urls = Get(info.SummaryUsed, "urls"), urlsHave = Get(info.SummaryAvailable, "urls");
        if (used >= 0) parts.Add($"Scripts on this parcel use {used / 1024} KB of memory{(have > 0 ? $" of {have / 1024} KB" : "")}.");
        if (urls >= 0) parts.Add($"{urls} URLs{(urlsHave > 0 ? $" of {urlsHave}" : "")}.");
        var objects = (info.Parcels ?? []).Where(p => p?.Objects != null).SelectMany(p => p.Objects)
            .Select(o => (o.Name, Kb: Get(o.Resources, "memory") / 1024))
            .OrderByDescending(o => o.Kb).ToList();
        if (objects.Count > 0)
            parts.Add($"{objects.Count} scripted {(objects.Count == 1 ? "object" : "objects")}, the most first: "
                      + string.Join(", ", objects.Take(10).Select(o => $"{(string.IsNullOrWhiteSpace(o.Name) ? "unnamed" : o.Name)} {Math.Max(0, o.Kb)} KB"))
                      + (objects.Count > 10 ? $", and {objects.Count - 10} more" : "") + ".");
        _say(parts.Count > 0 ? string.Join(" ", parts) : "The region sent nothing on this parcel's scripts.");
        static long Get(IDictionary<string, int>? d, string k) => d != null && d.TryGetValue(k, out var v) ? v : -1;
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
        // health, as Radegast's status bar: asked for, or in the whole status while it's below full (the region sends it
        // only once something changes it, on land where damage is on)
        int health = (int)MathF.Round(c.Self.Health);
        if (want.Contains("health")) parts.Add(health > 0 ? $"Health {health}%." : "Health: the region hasn't sent it, as it doesn't until something hurts you where damage is on.");
        else if (want.Count == 0 && health is > 0 and < 100) parts.Add($"Health {health}%.");
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
        _say(parts.Count > 0 ? string.Join(" ", parts) : "//status doing, region, parcel, location, health, money, time or music.");
    }

    string SeatName(GridClient c)
    {
        var w = _s.Region;
        if (w != null && w.Prims.TryGetValue(c.Self.SittingOn, out var seat))
            return _s.Objects.RootOf(w, seat).Properties?.Name is { Length: > 0 } n ? n : "an object";
        return "an object";
    }
}
