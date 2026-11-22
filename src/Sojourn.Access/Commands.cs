using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
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
public sealed partial class Commands
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
    /// for before its next line. <paramref name="frame"/>: a route's line, the region whose coordinates its `//go xyz`
    /// is in, when that isn't the one she's in (a step left out by `//route skip` that would have crossed a border).</summary>
    public async Task<bool> RunAsync(string line, ulong frame = 0)
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
                case "rebake" or "stopanimations" or "hover" or "displayname" or "maturity" or "emptytrash" or "emptylostandfound" or "saveoutfit" or "run" or "walkround":
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
                case "farconvoy" when Environment.GetEnvironmentVariable("SOJOURN_TEST_CONVOY_BLIND") == "1":
                    // (the scripted test's: them seen 150 m off for a while, as a crossing's moment once put her partner)
                    WalkMode?.FarConvoy(double.Parse(rest[0], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(rest[1], System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                case "glitchconvoy" when Environment.GetEnvironmentVariable("SOJOURN_TEST_CONVOY_BLIND") == "1":
                    // (the scripted test's: the region's word on the seat's velocity read reversed now and then)
                    WalkMode?.GlitchConvoy(double.Parse(rest[0], System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                case "doubtconvoy" when Environment.GetEnvironmentVariable("SOJOURN_TEST_CONVOY_BLIND") == "1":
                    // (the scripted test's: the next crossing's seat counted as not sent for a while)
                    WalkMode?.DoubtConvoy(double.Parse(rest[0], System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                case "awaybyitself" when Environment.GetEnvironmentVariable("SOJOURN_TEST_AWAY_BY_ITSELF") == "1":
                    // (the scripted test's: away as five minutes without her sets it, which its posted keys can't wait for)
                    _s.Presence.SetAway(true, auto: true);
                    return true;
                case "about" or "version": _say(About?.Invoke() ?? "Sojourn Access."); return true;
                case "describe": if (Describe != null) await Describe(string.Join(" ", rest)); return true;
                case "go": return await GoAsync(rest, frame);
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
                case "export": if (Export != null) await Export(AfterVerb(line)); return true;
                case "import": if (Import != null) await Import(AfterVerb(line)); return true;
                case "autosit": if (AutoSit != null) await AutoSit(string.Join(" ", rest)); return true;
                case "build": if (Build != null) await Build(AfterVerb(line)); return true;
                case "land": if (Land != null) await Land(AfterVerb(line)); return true;
                case "voice": if (Voice != null) await Voice(string.Join(" ", rest)); return true;
                case "imsound": if (ImSound != null) await ImSound(string.Join(" ", rest)); return true;
                case "pointing": if (Pointing != null) await Pointing(string.Join(" ", rest)); return true;
                case "point": if (PointAt != null) await PointAt(string.Join(" ", rest)); return true;
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
        ("go", "//go [tp] N [direction] | //go [tp] xyz X,Y[,Z] (or just X,Y[,Z]) | //go [tp] person NAME | //go [tp] object NAME",
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
        ("route", "//route NOTECARD | //route (the lines on the clipboard) | //route stop | //route resume | //route skip",
            "play a route: a notecard's (or the clipboard's) // lines one at a time, each walk finished before the next, other lines read as notes; where a step doesn't get there the route pauses, resume tries that step again and skip goes on without it (skip while it plays leaves out the step it's on)"),
        ("record", "//record | //record note WORDS | //record wait SECONDS | //record undo | //record pause | //record resume | //record save [NAME] | //record discard",
            "record a route by walking it (R in walk mode): start, or while recording how it's going; a note here; a wait here, where the played route stands still that long; take back the last point, note or wait; pause recording, and go on (going on where you paused, the route waits there as long as you were paused); save it as a notecard in Routes; or throw it away"),
        ("pause", "//pause [SECONDS]", "wait (in a route: before its next line; 1 second unless said); while recording a route, //record wait SECONDS puts one in it"),
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
            "record a ride (C in walk mode): pictures every few seconds and what's around, kept on this PC; stop it (it stops by itself when you stand up after the ride, and goes on through stops while you stay on); have the assistant tell it (Shift+C in walk mode), or tell more"),
        ("text", "//text | //text NAME", "floating text: what you're sitting on shows (Shift+V in walk mode), or the nearest thing with that name within 30 metres"),
        ("outfit", "//outfit", "what you're wearing, by name from the worn list: clothing, attachments and where, HUDs, body parts"),
        ("saveoutfit", "//saveoutfit NAME", "save what you're wearing as an outfit: a folder NAME in My Outfits with a link to each thing, as the official viewer's Save Outfit"),
        ("build", "//build | //build COMMAND (hollow 50, colour red, new box, undo, help)",
            "the build editor (Control+B), or one of its commands on what you're editing"),
        ("land", "//land | //land COMMAND (options, fly off, ban NAME, for sale 500, buy, help)",
            "About Land (Control+L) on the parcel you're on, or one of its commands"),
        ("upload", "//upload | //upload PATH", "upload a picture, sound, animation or model: with no path, choose the file (Control+U)"),
        ("export", "//export | //export NAME | //export NAME as dae (or xml, oxp)",
            "save something you made to a file: what you're editing, or the nearest of yours with that name; a backup Firestorm and Sojourn rebuild, Radegast's XML, or a Collada model"),
        ("import", "//import | //import PATH", "rebuild an object from a file (a Firestorm or Sojourn backup, or Radegast's XML) 2 metres ahead: with no path, choose the file"),
        ("autosit", "//autosit | //autosit on | //autosit off | //autosit here | //autosit NAME | //autosit now | //autosit forget [NAME | all]",
            "sit back on your seat in a region when you log in or teleport there (Control+Shift+S turns it on and off): how it stands; on or off; what you're sitting on, or the nearest thing with that name, as this region's seat; sit on it now; forget one"),
        ("voice", "//voice | //voice people | //voice mute all | //voice unmute all | //voice reconnect | //voice answer | //voice decline | //voice mute NAME | //voice unmute NAME | //voice volume NAME N",
            "who's in voice and speaking, your mic; the people in voice to act on (Control+F4); everyone else muted for you, or everyone heard again; voice started afresh; answer or decline a call ringing; one person muted, or how loud they are from 0 to 2, for you alone"),
        ("imsound", "//imsound NAME SOUND | //imsound NAME usual | //imsound NAME | //imsound",
            "a sound of its own for someone's IMs, or a group's chat, in place of the usual one; usual takes it back; with only a name, theirs; with nothing, the sounds and who has one"),
        ("point", "//point NAME | //point off | //point",
            "point at the nearest thing within 30 metres with that name (else a part within 10) for a minute, as Control+P in Objects: people nearby see your arm and a beam on it; off stops; with nothing, what you're pointing at"),
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
        ("walkround", "//walkround | //walkround on | //walkround off", "walks by //go and Walk to go round people standing in the way, or straight again"),
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


    async Task<bool> GoAsync(List<string> a, ulong frame = 0)
    {
        bool tp = a.Count > 0 && a[0].Equals("tp", StringComparison.OrdinalIgnoreCase);
        if (tp) a = a.Skip(1).ToList();
        if (a.Count == 0 || a[0].Equals("help", StringComparison.OrdinalIgnoreCase)) { Help("go"); return false; }
        if (_ops.Me() is not { } me) { _say("Not in a region yet."); return false; }
        Vector3 target;
        string what;
        UUID toPerson = default, thing = default;
        double stop = 0.6;   // the autopilot coasts a little after letting go: routes of waypoints want it close
        bool level = false;   // (a point given with its height)
        var kind = a[0].ToLowerInvariant();
        // coordinates without the word xyz (//go 25,25,56, as she typed it once, 2026-11-18; //go 25 25 56): two or
        // three numbers and nothing else, which a distance and a direction never are
        var bare = string.Join(",", a).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if ((a.Count > 1 || a[0].Contains(',')) && bare.Length is 2 or 3
            && bare.All(n => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            kind = "xyz";
            a = a.Prepend(kind).ToList();
        }
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
            level = nums.Count > 2;
            // (a route's line in a neighbouring region's frame, said as written)
            if (frame != 0 && _ops.Client?.Network.CurrentSim is { } sim && sim.Handle != frame) target = InFrame(target, frame, sim.Handle);
        }
        else if (kind == "person")
        {
            var name = string.Join(" ", a.Skip(1));
            if (await _ops.FindPersonAsync(name) is not { } p) { _say($"Nobody called {name} nearby."); return false; }
            target = p.Pos;
            what = p.Name;
            toPerson = p.Id;
            stop = 1.5;
        }
        else if (kind == "object")
        {
            var name = string.Join(" ", a.Skip(1));
            var things = await _ops.ObjectsAsync(96, 5, name);
            if (things.FirstOrDefault() is not { } t) { _say($"No object called {name} nearby."); return false; }
            target = t.Pos;
            what = t.Name;
            thing = t.Id;
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
        ulong origin = _ops.Client?.Network.CurrentSim?.Handle ?? 0;
        bool walked = await WalkAsync(target, what, stop, hint, toPerson, thing);
        if (kind == "xyz") _pointEnd = (MeIn(origin), target, level);
        return walked;
    }

    /// <summary>Where the last walk to a point ended, the point, and whether it was given with its height: for a route,
    /// whose step to a point is done only near it (PointMissed).</summary>
    (Vector3? At, Vector3 Point, bool Level)? _pointEnd;

    /// <summary>How far across from a route's point its step may end and be done: as any walk to a point is (WatchWalkAsync's
    /// 2.5 m), however it ended. A walk's own finish is under a metre, often 1.1 to 1.2 m on Second Life.</summary>
    const float RoutePointOff = 2.5f;

    /// <summary>A route's step to a point that ended off it, though the walk called it done ("as near as the way goes", trusted
    /// to the region, on straight): 2.5 m or more across, or with the point's height given, on another floor (2 m over or
    /// under). Her routes' points are where she walked to recording them, so one ended well off means something went wrong,
    /// and the route going on from there starts its next step from the wrong place (her route up her partner's steps on
    /// 1187, 2026-11-21: the steps' top, out of reach in the model, ended east of it, and the next point, written without a
    /// height, was taken at the deck's, in mid-air). The words, or null.</summary>
    string? PointMissed()
    {
        if (_pointEnd is not { At: { } at } e) return null;
        float off = Flat(at, e.Point), up = at.Z - e.Point.Z;
        var missed = off >= RoutePointOff ? $"it ended {Bearings.Metres(off)} short of its point"
            : e.Level && OtherFloor(at, e.Point) ? $"it ended {Bearings.Metres(Math.Abs(up))} {(up < 0 ? "under" : "over")} its point"
            : null;
        if (missed != null)
            WalkNote(string.Create(CultureInfo.InvariantCulture,
                $"route: a step to {e.Point.X:0.00},{e.Point.Y:0.00}{(e.Level ? $",{e.Point.Z:0.00}" : "")} walked as done, but ended at {at.X:0.00},{at.Y:0.00},{at.Z:0.00}, {off:0.0} m off across and {up:+0.0;-0.0} up: the route paused"));
        return missed;
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
    /// region may start the walk after the first has landed; what Second Life's does isn't known.
    /// Walking off Second Life, a point more than 2 m over the ground under it (upstairs, a balcony) goes at 2 m:
    /// OpenSim's autopilot flies the avatar to any point over one and a half avatar heights above the ground there
    /// (MoveToTarget), and a walk on the ground or a floor steers by X and Y alone, the floor carrying the avatar up the
    /// stairs (2026-11-11). On Second Life, walking, it gets her own height (Level), and again as she climbs or comes
    /// down (KeepLevel): given a point above her it settles her east of it by about as far as it's above her. Flying, the
    /// height as given; a flying animation during a walk there is logged (WatchFlying).</summary>
    void AutoPilotTo(GridClient c, double x, double y, double z)
    {
        _aim = new Aim(x, y, z);
        if (!c.Self.Movement.Fly && !OnSecondLife && c.Network.CurrentSim is { } sim && _s.Region is { } w)
        {
            Utils.LongToUInts(sim.Handle, out var rx, out var ry);
            float lx = (float)(x - rx), ly = (float)(y - ry);
            if (lx >= 0 && ly >= 0 && lx < w.SizeX && ly < w.SizeY && w.TerrainHeight(lx, ly) is { } ground) z = Math.Min(z, ground + 2);
        }
        else if (!c.Self.Movement.Fly && OnSecondLife && _ops.Me() is { } me) z = Level(me.Pos.Z, z);
        _sent = (x, y, z);
        _sentAt = Environment.TickCount64;
        c.Self.AutoPilot(x, y, z);
        if (!AlwaysRun()) return;
        c.Self.Movement.AlwaysRun = true;
        _ = Task.Delay(500).ContinueWith(_ => { if (ReferenceEquals(_ops.Client, c) && c.Self.Movement.AlwaysRun) c.Self.Movement.AlwaysRun = true; }, TaskScheduler.Default);
    }

    /// <summary>The height Second Life's autopilot is sent for a walk: hers (her middle), not the point's. Each of the 13
    /// stretches in the blind tester's logs at her partner's place (three sessions, 2026-11-19 to 21) that stuck or ended
    /// short with the point 1 to 5 m over her middle (his height, a point on his floor) left her within 0.07 m of it north
    /// to south and east of it by 0.7 to 1.2 times that height, wherever she'd set out from and whichever way the stretch
    /// went: she walked a metre the wrong way, stuck with nothing ahead, and drifted east off the foot of his steps; the
    /// walks sent a point at her height arrived. The official viewer's Go here steers on its own side by X and Y
    /// (LLAgent::autoPilot), and Radegast's `//go xyz X,Y`, which she and her partner write their routes in, sends her
    /// height. Logged once a walk where the point was half a metre or more over or under her.</summary>
    double Level(double mine, double given)
    {
        if (Math.Abs(given - mine) >= 0.5 && Interlocked.Exchange(ref _levelSaid, 1) == 0)
            WalkNote(string.Create(CultureInfo.InvariantCulture, $"go: the region's autopilot sent her own height ({mine:0.00}), not the point's ({given:0.00}), as on Second Life"));
        return mine;
    }

    /// <summary>On Second Life, walking: the region's autopilot sent the point again at her height when she's climbed or
    /// come down 0.4 m or more since it was last sent (at most twice a second), so a point a stretch is walked to never
    /// stands much over or under her (Level). Called from the loops watching a walk.</summary>
    void KeepLevel()
    {
        if (_aim is not { } a || _ops.Me() is not { } me || Math.Abs(me.Pos.Z - _sent.Z) < 0.4 || Environment.TickCount64 - _sentAt < 500) return;
        if (_ops.Client is not { } c || c.Self.Movement.Fly || !OnSecondLife) return;
        AutoPilotTo(c, a.X, a.Y, a.Z);
    }

    /// <summary>Where the region's autopilot is walking her now, as given (global), till it's let go.</summary>
    sealed record Aim(double X, double Y, double Z);
    volatile Aim? _aim;
    long _sentAt;
    int _levelSaid;
    /// <summary>The session is on Second Life's own grid (or its beta grid), not OpenSim's.</summary>
    bool OnSecondLife => Sojourn.Session.Login.Grids.IsSecondLife(Sojourn.Session.Login.Grids.Normalise(_s.Host.LoginUriFor(_s.Config)) ?? "");

    /// <summary>The last point the region's autopilot was sent (global), for the flying line.</summary>
    (double X, double Y, double Z) _sent;

    static readonly Dictionary<UUID, string> Flying = new()
    {
        [Animations.FLY] = "fly", [Animations.FLYSLOW] = "flyslow", [Animations.HOVER] = "hover",
        [Animations.HOVER_UP] = "hover_up", [Animations.HOVER_DOWN] = "hover_down",
    };

    /// <summary>Logs a flying animation on her while a walk of the region's autopilot goes (once a walk, in the session's
    /// log she sends as well as the walk log): on Second Life each stretch is sent at the goal's height, and whether its
    /// autopilot ever flies her to a point upstairs, as OpenSim's did, isn't known. Returns what stops the watch.</summary>
    Action? WatchFlying(string what)
    {
        if (_ops.Client is not { } c || c.Self.Movement.Fly) return null;
        int told = 0;
        void On(object? _, AvatarAnimationEventArgs e)
        {
            if (e.AvatarID != c.Self.AgentID || c.Self.Movement.Fly) return;
            var names = e.Animations.Where(a => Flying.ContainsKey(a.AnimationID)).Select(a => Flying[a.AnimationID]).ToList();
            if (names.Count == 0 || Interlocked.Exchange(ref told, 1) == 1) return;
            var at = c.Self.SimPosition;
            Utils.LongToUInts(c.Network.CurrentSim?.Handle ?? 0, out var rx, out var ry);
            var ground = _s.Region?.TerrainHeight(at.X, at.Y) is { } gh ? gh.ToString("0.0", CultureInfo.InvariantCulture) : "unknown";
            var line = string.Create(CultureInfo.InvariantCulture,
                $"go: a flying animation ({string.Join(", ", names)}) on the walk to {what}{(OnSecondLife ? " on Second Life" : "")}: at {at.X:0.0}, {at.Y:0.0}, {at.Z:0.0}, the ground {ground}; the autopilot was last sent {_sent.X - rx:0.0}, {_sent.Y - ry:0.0}, {_sent.Z:0.0}");
            Walk.WalkMode.Log(line);
            _s.Log.LogInformation("{Line}", line);
        }
        c.Avatars.AvatarAnimation += On;
        return () => c.Avatars.AvatarAnimation -= On;
    }

    /// <summary>Whether a cue's text is wanted (walk mode's settings: "follow").</summary>
    public Func<string, bool> CueText { get; set; } = _ => true;

    /// <summary>Walks to a point in the region by the region's own autopilot (as Radegast's //go: the region knows where
    /// the avatar really is; a host with no renderer hears its own position only now and then), saying how far, then
    /// how close it got once it has stopped. With the collision model, around what's in the way: a path of waypoints
    /// (PathPlanner), each walked by the region's autopilot in turn, each new stretch said; a stretch that sticks is
    /// marked and the rest planned again (twice), unless the region holds her on something the model hasn't got
    /// (TrustRegionAsync); to someone whose floor the plan can't reach, the way they walked there where they did lately
    /// (TrailRouteAsync). A goal out of reach where this floor meets it (on a stage, inside a
    /// bar) is walked as near as the way goes, said, unless that's a storey or more under it with its floor meeting hers
    /// only far off (PathPlanner.Plan.Beneath: no way); a person or a point on another floor, then on straight by the
    /// region (OnFromAsync). Seated, it stands first. With no way at all it says so, and
    /// teleports there once where that can work (walk_teleport_fallback; NoWayAsync), else names the command that
    /// would (<paramref name="tpHint"/>). Completes when the walk ends: true when it got there (or within 2.5 m, or as
    /// near as the way goes). People on their feet are gone round like things, those there as it sets out and those
    /// who step into the way after (Commands.WalkPeople.cs); <paramref name="toPerson"/>, the one it's to, excepted.
    /// Where the only way is through someone it ends short of them, saying who, and never by the teleport.
    /// <paramref name="thing"/>: the object it's to, whose floor is the one at its foot (FootOf).</summary>
    public Task<bool> WalkAsync(Vector3 target, string what, double stop = 1, string? tpHint = null, UUID toPerson = default, UUID thing = default)
    {
        if (_ops.Me() is null || _ops.Client is null) return Task.FromResult(false);
        StopFollowing(quiet: true);
        _walk?.Cancel();
        var walk = _walk = new CancellationTokenSource();
        var heading = _heading = new Walk.WalkMode.Target(what, () => new System.Numerics.Vector3(target.X, target.Y, target.Z), false);
        tpHint ??= string.Create(CultureInfo.InvariantCulture, $"//go tp xyz {target.X:0},{target.Y:0}");
        var foot = thing != UUID.Zero ? FootOf(thing, target) : ((float Z, float Reach)?)null;
        return Task.Run(async () =>
        {
            var flying = WatchFlying(what);
            Interlocked.Exchange(ref _levelSaid, 0);
            try { return await RouteAsync(target, what, stop, tpHint, walk.Token, toPerson, foot); }
            finally
            {
                flying?.Invoke();
                if (_walk == walk || _walk == null) _aim = null;   // (nothing for KeepLevel to send again)
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

    async Task<bool> RouteAsync(Vector3 target, string what, double stop, string tpHint, CancellationToken ct, UUID toPerson = default, (float Z, float Reach)? foot = null)
    {
        if (!await StandFirstAsync(ct)) return false;
        if (_ops.Me() is not { } me || _ops.Client is not { } c || c.Network.CurrentSim is not { } sim) return false;
        ulong origin = sim.Handle;   // the target's frame: a route's point past a border runs beyond 256 or below 0
        double near = Math.Max(stop, 1.2);
        var flat = new Vector2(target.X - me.Pos.X, target.Y - me.Pos.Y).Length();
        var people = new Bystanders(toPerson);
        var route = await PlanAsync(me.Pos, target, ct, people, foot);
        if (ct.IsCancellationRequested) return false;
        // no step to take for the people she's standing among: planned again without those against her (Bystanders.Beside)
        if (route is { Legs.Count: 0, InWay: not null } && SetsOutAmong(people, me.Pos))
        {
            route = await PlanAsync(me.Pos, target, ct, people, foot);
            if (ct.IsCancellationRequested) return false;
        }
        // to someone whose floor the plan can't reach, or with no way at all: the way they walked there, where they did
        // lately (Trails: her partner up steps the model lacked, 2026-11-19)
        if (toPerson != UUID.Zero && route is { InWay: null } planned && (planned.Legs.Count == 0 || planned.Beyond)
            && await TrailRouteAsync(toPerson, me.Pos, planned) is { } byTrail)
            route = byTrail;
        // no way: past a border since it set out, the target put in this region's frame, and no teleport while it's
        // still over the border (the teleport is within the region she's in) nor a hint by coordinates of the other's
        Task<bool> NoWay()
        {
            if (_ops.Client?.Network.CurrentSim is not { } now || now.Handle == origin) return NoWayAsync(target, what, tpHint);
            var here = InFrame(target, origin, now.Handle);
            return NoWayAsync(here, what, tpHint.StartsWith("//go tp xyz", StringComparison.Ordinal) ? null : tpHint, teleport: !Beyond(here));
        }
        if (route is { Legs.Count: 0, InWay: null }) return await NoWay();
        if (route is { Legs.Count: 0, InWay: { } hemmed })
        {
            _say(await NotWalkingAsync(hemmed, target, what));
            return flat < 2.5;
        }
        // (no way planned: one straight line by the region's autopilot, watched to its end)
        var r = route ?? new Route(new List<System.Numerics.Vector2> { new(target.X, target.Y) }, false) { Straight = true };
        var legs = r.Legs;
        // a goal out of reach (up on a stage, inside a bar): its stand-in, the nearest spot the way goes to; a way cut
        // short of someone standing in it: where it was cut
        Vector3 End() => r.Moved || r.InWay != null ? new Vector3(legs[^1].X, legs[^1].Y, target.Z) : target;
        string Short() => r.Moved ? $"; {what} is out of reach, so as near as the way goes, {Bearings.Metres(Flat(End(), target))} short" : "";
        // a step aside for someone (Aside): the same walk to her, so the plain line, and its stretches walked unsaid
        // (their trail: its points unsaid, as a person following them says none)
        bool quiet = r.Trail || SameWalk(r, Length(me.Pos, legs) - flat);
        if (r.Straight) _say(string.Create(CultureInfo.InvariantCulture, $"Walking {Bearings.Metres(flat)} to {what}."));
        else if (r.Trail) _say($"Walking {Bearings.Metres(Length(me.Pos, legs))} to {what} the way they went{(Math.Abs(r.Rise) >= 1 ? $", {(r.Rise > 0 ? "up" : "down")} {Bearings.Metres(Math.Abs(r.Rise))}" : "")}.");
        else if (quiet) _say($"Walking {Bearings.Metres(Length(me.Pos, legs))} to {what}.");
        else
        {
            var line = legs.Count == 1 ? $"Walking {Bearings.Metres(Length(me.Pos, legs))} toward {what}{Short()}."
                : r.Around is { Count: > 0 } round ? $"Walking {Bearings.Metres(Length(me.Pos, legs))} to {what}, around {await NamesAsync(round)}: {legs.Count} stretches{Short()}."
                : Math.Abs(r.Rise) >= 1.5f ? $"Walking {Bearings.Metres(Length(me.Pos, legs))} to {what}, {(r.Rise > 0 ? "up" : "down")} {Bearings.Metres(Math.Abs(r.Rise))}: {legs.Count} stretches{Short()}."
                : $"Walking {Bearings.Metres(Length(me.Pos, legs))} to {what}, around something in the way: {legs.Count} stretches{Short()}.";
            _say(r.InWay is { } cutAt ? $"{line} {await OnlyAsFarAsync(cutAt, target)}" : line);
        }
        int replans = 0, dodges = 0;
        bool trustedGoal = false;   // (the region trusted with the goal itself: once a walk)
        bool shutIn = false;   // (a stuck spot that left no way taken back and the region trusted: once a walk)
        for (int i = 0; i < legs.Count; i++)
        {
            System.Numerics.Vector2? marked = null;   // (the stuck spot marked at this stretch, if one was)
            if (ct.IsCancellationRequested || _ops.Me() is not { } here) return false;
            bool last = i == legs.Count - 1;
            var leg = last ? End() : new Vector3(legs[i].X, legs[i].Y, target.Z);
            if (i > 0 && !quiet)
            {
                var d = new Vector2(leg.X - here.Pos.X, leg.Y - here.Pos.Y);
                _say($"{i + 1} of {legs.Count}: {Bearings.Metres(d.Length())} to the {Bearings.Point(Bearings.Compass(0, 0, d.X, d.Y))}.");
            }
            var g = GlobalIn(leg, origin);
            AutoPilotTo(c, g.X, g.Y, g.Z);
            // someone stepping into the way has it planned afresh round them, a few times a walk, where there's a model to plan by
            // (not on someone's trail: planned afresh round people, it would leave it)
            bool dodging = HeedPeople && dodges < Dodges && Planner is { CanAsk: true } && !Beyond(target) && !r.Trail;
            Leg got;
            if (r.Straight)
            {
                bool there = await WatchWalkAsync(target, near, ct, origin, null, people, dodging);
                if (people.Met == null) return there;
                got = Leg.Met;
            }
            else got = await LegAsync(leg, last ? (r.InWay != null ? 1.3 : r.Moved ? 0.6 : near) : 0.5, ct, origin, dodging ? people : null,
                last && r.InWay != null ? 1.2 : 0);
            if (got == Leg.Cancelled) return false;
            UUID? who = null;   // whoever the way is planned again for
            bool cutEnd = false, onItsFloor = false;
            if (got == Leg.Reached)
            {
                if (!last) continue;
                if (r.InWay is { } cut)
                {
                    // as far as the way went, short of someone standing in it: let go at once, 1.3 m before its end (she
                    // goes on 1 to 1.5 m at a walk: let go a metre before it she ended 1.1 to 1.6 m from the visitor, for
                    // the 1.5 meant) and sooner at a run (LegAsync's lead: a metre before it at 5 m/s she ran on into the
                    // visitor), and planned once more from here, as they may have moved on while she walked up
                    CancelAutoPilot();
                    (who, cutEnd) = (cut, true);
                }
                else if (r.Moved && r.Beyond && foot == null && !trustedGoal)
                {
                    // as near as the way goes to a person or a point on a floor the model can't reach from hers: on to it
                    // straight by the region, trusted, where that can't be a fall of more than a few metres (OnFromAsync)
                    trustedGoal = true;
                    if (await OnFromAsync(leg, target, what, near, ct, origin, people) is { } ended) return ended;
                    onItsFloor = true;
                }
                else return r.Moved ? await WatchWalkAsync(leg, 0.8, ct, origin, (target, what), people) : await WatchWalkAsync(target, near, ct, origin, null, people);
            }
            else if (got == Leg.Met)
            {
                who = people.Met;
                people.Met = null;
                dodges++;
            }
            else
            {
                CancelAutoPilot();
                if (_ops.Me() is not { } stuck || Planner == null) return false;
                // up against someone: it ends here and says who. No spot marked (they'll move), no pushing on at them
                // again, and never the teleport a way that keeps sticking ends in
                if (Against(people, stuck.Pos, leg) is { } person) return await EndInWayAsync(person, target, letGo: false);
                var head = $"go: stuck on stretch {i + 1} of {legs.Count}{(r.Trail ? " of their trail" : "")}, time {replans + 1} of 3 (the third ends the walk)";
                // the region holding her on something the model hasn't got, with nothing of the model's ahead and no drop
                // it has on the way: its word wins, so no stuck spot, on by its autopilot (her walks of 2026-11-19, carried
                // up steps the model lacked while each stretch stuck, was marked and gone round, and the third ended in "no
                // way"): to the goal itself, once a walk, where the model has its floor out of reach (her walks were planned
                // to a spot 7 m short of her partner); else to this stretch's end, then on with the rest
                bool toGoal = r.Beyond && !trustedGoal;
                var aim = toGoal ? new Vector3(target.X, target.Y, foot?.Z ?? target.Z) : leg;
                var (lifted, untrusted) = await LiftedAsync(stuck.Pos, aim, origin);
                if (lifted != null)
                {
                    NoteStuck($"{head}; lifted, so trusting the region", stuck.Pos, aim, target, origin);
                    trustedGoal |= toGoal;
                    // (the walk's own end, where she has to be on its floor; a corner or a stand-in, there across)
                    bool end = toGoal || last, itsFloor = toGoal || last && !r.Moved;
                    var trust = await TrustRegionAsync(aim, $"{lifted} and nothing of the model's ahead: no stuck spot", toGoal ? near : last ? (r.Moved ? 0.6 : near) : 0.5, itsFloor, end,
                        toGoal ? "so on as near as the way goes, planned again from here" : "no way, so it ends as one", ct, origin, ontoItsFloor: toGoal);
                    switch (trust.Got)
                    {
                        case Trust.Cancelled: return false;
                        case Trust.There when toGoal: return await WatchWalkAsync(aim, near, ct, origin, null, people);
                        case Trust.There when !last: continue;
                        case Trust.There:
                            return r.Moved ? await WatchWalkAsync(leg, 0.8, ct, origin, (target, what), people) : await WatchWalkAsync(target, near, ct, origin, null, people);
                        case Trust.Close:
                            if (!toGoal && r.Moved) _say($"Finished walking, as near to {what} as the way goes: {what} is {_ops.Where(target) ?? Bearings.Metres(Flat(trust.At, target))}.");
                            else _say($"Finished walking, {OffWords(Flat(trust.At, aim))} from the destination.");
                            return true;
                        case Trust.OnItsFloor:
                            onItsFloor = true;
                            break;
                        case Trust.Fell:
                            return await FellAsync(stuck.Pos.Z, target, what, origin);
                    }
                    // no nearer: making for the goal itself, the walk goes on as near as the way goes, as it did before
                    // the region was trusted (a stage with no steps); else it ends as one with no way does (the
                    // teleport where one can land, which the third stick had)
                    if (!onItsFloor && (!toGoal || ++replans > 2)) return await NoWay();
                }
                else
                {
                    // stuck on the way: something the model hadn't (a thin post, a doorway the region won't fit): marked,
                    // planned again; the session log says what the model has there, so her log can tell
                    NoteStuck(untrusted != null ? $"{head}; {untrusted}" : head, stuck.Pos, leg, target, origin);
                    var dir = Vector2.Normalize(new Vector2(leg.X - stuck.Pos.X, leg.Y - stuck.Pos.Y));
                    marked = new System.Numerics.Vector2(stuck.Pos.X + dir.X * 0.7f, stuck.Pos.Y + dir.Y * 0.7f);
                    Planner.Block(marked.Value, 0.6f, TimeSpan.FromMinutes(2), stuck.Pos.Z);
                    if (++replans > 2) return await NoWay();
                }
            }
            if (_ops.Me() is not { } from) return false;
            Vector3? cutStood = cutEnd && who is { } blocker && people.Stood.TryGetValue(blocker, out var stoodAt) ? stoodAt : null;
            var again = await PlanAsync(from.Pos, target, ct, people, foot);
            if (ct.IsCancellationRequested) return false;
            if (again is { Legs.Count: 0, InWay: null })
            {
                // the spot just marked left her no way, where the model had one through it: the only way it has, which
                // the region held her on for a moment (the blind tester on her partner's steps, 2026-11-22: stuck
                // mid-steps on the way the model had up them, the spot shut her in, 12 floor nodes she could get to,
                // and "no way" for two minutes, every walk from there; minutes later the same route went up from half
                // a metre on). The spot taken back, on by the region to the walk's end, once a walk
                if (marked is { } spot && !shutIn && Planner is { } planner)
                {
                    shutIn = true;
                    planner.Unblock(spot);
                    var without = await PlanAsync(from.Pos, target, ct, people, foot);
                    if (ct.IsCancellationRequested) return false;
                    if (without is not { Legs.Count: 0 } && await OnThroughAsync(r, End(), target, what, near, ct, origin, people) is { } through)
                        return through;
                }
                return await NoWay();
            }
            // still there, or no step further to take toward them
            // (let go already unless it was walking on when someone stepped in: letting go is a press of forward, a step more each time)
            bool walkingOn = got == Leg.Met;
            if (again is { InWay: { } still } && (again.Value.Legs.Count == 0 || cutEnd && still == who)) return await EndInWayAsync(still, target, walkingOn);
            // within 2 m of the point no way is planned (PlanAsync), and the straight line left is through them: it ends
            // here, unless the one the way was cut at has moved on
            if (again == null && who is { } close && Flat(from.Pos, target) < 2
                && (!cutEnd || cutStood is { } was && OnFoot(people.To).Any(p => p.Id == close && Flat(p.Pos, was) < 0.4f)))
                return await EndInWayAsync(close, target, walkingOn);
            // how much further the new way is than what was left of the old: said from Further up, and under Aside,
            // round people alone, it's the same walk still (who stepped in is said, the stretches aren't)
            float left = Length(from.Pos, legs.GetRange(i, legs.Count - i));
            r = again ?? new Route(new List<System.Numerics.Vector2> { new(target.X, target.Y) }, false);
            legs = r.Legs;
            float now = Length(from.Pos, legs), more = now - left;
            // (up or down on its floor by the region's way: the rest walked unsaid, its end said as any walk's)
            quiet = onItsFloor || again != null && !cutEnd && who != null && SameWalk(r, more);
            var stretches = $"{legs.Count} {(legs.Count == 1 ? "stretch" : "stretches")}";
            var far = more >= Further ? $", {Bearings.Metres(now)} from here" : "";
            if (r.InWay is { } only) _say(await OnlyAsFarAsync(only, target));
            else if (onItsFloor) { }
            else if (cutEnd) _say($"The way is clear now; going on{far}: {stretches}.");
            else if (who is not { } stepped) _say($"Something's in the way; going round{far}: {stretches}.");
            else if (quiet) _say($"{Cap(await NameOfAsync(stepped))} is in the way; going round.");
            else if (again != null) _say($"{Cap(await NameOfAsync(stepped))} is in the way; going round{far}: {stretches}.");
            i = -1;
        }
        return false;
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    /// <summary>A walk that stuck, in the session log: <paramref name="what"/>, where she is (her feet), where she was
    /// heading and the walk's end, then what the collision model has there (walk mode's StuckAsync: rays ahead, what it
    /// hasn't got, the parcels, the region's word). <paramref name="at"/> in the frame of the region she's in, the
    /// other two in <paramref name="origin"/>'s (where the walk began), put in hers when she's crossed since, since the
    /// model's rays and the parcels are hers.</summary>
    void NoteStuck(string what, Vector3 at, Vector3 toward, Vector3 end, ulong origin)
    {
        if (WalkMode is not { } walk) return;
        if (_ops.Client?.Network.CurrentSim is { } sim && sim.Handle != origin)
        {
            (toward, end) = (InFrame(toward, origin, sim.Handle), InFrame(end, origin, sim.Handle));
            what += $", in {sim.Name} past the border (its frame)";
        }
        float feet = FeetZ?.Invoke() ?? at.Z - 0.95f;
        walk.NoteStuck(string.Create(CultureInfo.InvariantCulture,
                $"{what}: at {at.X:0.00},{at.Y:0.00},{at.Z:0.00} (feet {feet:0.00}), heading for {toward.X:0.00},{toward.Y:0.00} ({Flat(at, toward):0.0} m on), the walk's end {end.X:0.00},{end.Y:0.00},{end.Z:0.00}"),
            new(at.X, at.Y, at.Z), feet, new(toward.X, toward.Y, toward.Z));
    }

    /// <summary>Stuck where the region holds her on something the model hasn't got, with nothing of the model's ahead and
    /// no drop it has on the way to <paramref name="toward"/> (WalkMode.LiftedAsync), standing (not flying, not falling
    /// or jumping): the words for the log, or null; and, lifted but not trusted, why not. <paramref name="toward"/> in
    /// <paramref name="origin"/>'s frame, <paramref name="at"/> in hers.</summary>
    async Task<(string? Lifted, string? Not)> LiftedAsync(Vector3 at, Vector3 toward, ulong origin)
    {
        if (WalkMode is not { } walk || _ops.Client is not { Network.CurrentSim: { } sim } c || c.Self.Movement.Fly || Math.Abs(c.Self.Velocity.Z) > 1) return (null, null);
        var ahead = InFrame(toward, origin, sim.Handle);
        float feet = FeetZ?.Invoke() ?? at.Z - 0.95f;
        return await walk.LiftedAsync(new(at.X, at.Y, at.Z), feet, new(ahead.X, ahead.Y, ahead.Z));
    }

    enum Trust { There, Close, NoNearer, Cancelled, OnItsFloor, Fell }

    /// <summary>A walk where the region has a way the model hasn't got (<paramref name="why"/> says how it's known, for
    /// the log): no stuck spot marked, no way round planned on a model that lacks what she's on, but on toward
    /// <paramref name="goal"/> by the region's autopilot, watched for getting nearer (across and up or down alike, so
    /// climbing steps counts) by a tenth of a metre in 3 s. There: within <paramref name="within"/> across, and on its
    /// floor where <paramref name="itsFloor"/> (the walk's own goal: under or over it isn't there). No nearer: Close
    /// where it's the walk's <paramref name="end"/> and she's within 2.5 m of it (on its floor, as before), else
    /// NoNearer, the log saying what comes of that (<paramref name="otherwise"/>). With <paramref name="ontoItsFloor"/>
    /// (making for the walk's goal on another floor), on its floor is level with it (her middle within 0.6 m of its
    /// height, or her feet within 0.4 m: a point given at a floor's height), not within 2 m (OtherFloor); and OnItsFloor
    /// once she's climbed or come down half a metre or more and is on it, so the model, which has that floor, plans the
    /// rest; and Fell, let go at once, when she's come down more than a step instead, making for a goal above where she
    /// set out: off an edge, which is no way to it (her partner's deck on Second Life, 2026-11-22: straight on from its
    /// north edge she came down off it and "as near as the way goes" was said 4 m under him). At: where she was last, in
    /// <paramref name="origin"/>'s frame.</summary>
    async Task<(Trust Got, Vector3 At)> TrustRegionAsync(Vector3 goal, string why, double within, bool itsFloor, bool end, string otherwise,
        CancellationToken ct, ulong origin, bool ontoItsFloor = false)
    {
        if (_ops.Client is not { } c) return (Trust.Cancelled, goal);
        WalkNote(string.Create(CultureInfo.InvariantCulture, $"go: trusting the region, {why}, on to {goal.X:0.00},{goal.Y:0.00},{goal.Z:0.00} by its autopilot"));
        var g = GlobalIn(goal, origin);
        AutoPilotTo(c, g.X, g.Y, g.Z);
        long start = Environment.TickCount64, progressAt = start;
        double best = double.MaxValue;
        Vector3 at = MeIn(origin) ?? goal;
        float fromZ = at.Z;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(150, CancellationToken.None);
            if (ct.IsCancellationRequested) return (Trust.Cancelled, at);
            if (MeIn(origin) is not { } pos) continue;
            KeepLevel();
            at = pos;
            if (ontoItsFloor && goal.Z > fromZ + 0.5f && pos.Z < fromZ - 0.6f)
            {
                CancelAutoPilot();
                WalkNote(string.Create(CultureInfo.InvariantCulture, $"go: trusting the region, came down instead: at {pos.X:0.00},{pos.Y:0.00},{pos.Z:0.00}, {pos.Z - fromZ:+0.0;-0.0} m from where it set out, {Flat(pos, goal):0.0} m off across, after {(Environment.TickCount64 - start) / 1000.0:0.0} s; off an edge, so it ends there"));
                return (Trust.Fell, pos);
            }
            double off = Flat(pos, goal), up = pos.Z - goal.Z;
            // (making for a goal on another floor, on its floor is level with it, her middle with its height or her feet
            // with a floor's: two metres either way, OtherFloor, had her "there" at the foot of a plinth a metre high)
            bool onIts = ontoItsFloor ? Math.Abs(up) < 0.6 || FeetZ?.Invoke() is { } herFeet && Math.Abs(herFeet - goal.Z) < 0.4 : !OtherFloor(pos, goal);
            if (off <= within && (!itsFloor || onIts))
            {
                WalkNote(string.Create(CultureInfo.InvariantCulture, $"go: trusting the region, there: at {pos.X:0.00},{pos.Y:0.00},{pos.Z:0.00}, {off:0.0} m off, after {(Environment.TickCount64 - start) / 1000.0:0.0} s"));
                return (Trust.There, pos);
            }
            if (ontoItsFloor && Math.Abs(pos.Z - fromZ) >= 0.5 && onIts)
            {
                WalkNote(string.Create(CultureInfo.InvariantCulture, $"go: trusting the region, on its floor: at {pos.X:0.00},{pos.Y:0.00},{pos.Z:0.00}, {pos.Z - fromZ:+0.0;-0.0} m from where it set out, {off:0.0} m off across, after {(Environment.TickCount64 - start) / 1000.0:0.0} s; planned again from here"));
                return (Trust.OnItsFloor, pos);
            }
            double d = Math.Sqrt(off * off + up * up);
            long now = Environment.TickCount64;
            if (d < best - 0.1) (best, progressAt) = (d, now);
            if (now - progressAt > 3000 || now - start > 60_000) break;
        }
        if (ct.IsCancellationRequested) return (Trust.Cancelled, at);
        CancelAutoPilot();
        double left = Flat(at, goal);
        bool close = end && left < 2.5 && !(itsFloor && OtherFloor(at, goal));
        float? feet = FeetZ?.Invoke();
        WalkNote(string.Create(CultureInfo.InvariantCulture, $"go: trusting the region, no nearer for 3 s: at {at.X:0.00},{at.Y:0.00},{at.Z:0.00}{(feet is { } ft ? $" (feet {ft:0.00})" : "")}, {left:0.0} m off across and {at.Z - goal.Z:+0.0;-0.0} up, after {(Environment.TickCount64 - start) / 1000.0:0.0} s; {(close ? "near enough: it ends there" : otherwise)}"));
        return (close ? Trust.Close : Trust.NoNearer, at);
    }

    /// <summary>How far a walk on from as near as the way goes to a goal out of reach may fall (OnFromAsync): off a deck
    /// or a storey (her partner's deck stands 2.3 m over the ground, the ground under his floor 2.7 m under hers).</summary>
    const float OnFromDrop = 4f;

    /// <summary>As near as the way goes (<paramref name="standIn"/>, where she's got to) to a person or a point on a floor
    /// the model can't reach from hers: on to it straight by the region's autopilot, trusted (TrustRegionAsync), unless
    /// the model has a drop of more than OnFromDrop under her feet on the straight way there, or someone else stands on
    /// it. Short of its floor she isn't there, however near across (a plinth's foot). Her partner up steps from
    /// a wooden deck that the model lacked (her logs of 2026-11-19 to 21): with no trail of his to take (he'd gone up
    /// before she came), every walk ended at the deck's edge "as near as the way goes", while until 2026-11-11 a walk
    /// had aimed straight at him and the region had carried her up. Something the model hasn't got is the likeliest
    /// reason a floor someone stands on is out of reach from hers; a stage with no steps or a wall stops her in 3 s,
    /// where she'd have stopped anyway, and the worst is a fall of a few metres, which Second Life doesn't hurt and walk
    /// mode says. "Trying straight on to ..." once; on its floor (OnItsFloor) the model plans the rest: null. Else how
    /// the walk ended, said.</summary>
    async Task<bool?> OnFromAsync(Vector3 standIn, Vector3 target, string what, double near, CancellationToken ct, ulong origin, Bystanders people)
    {
        Task<bool> AsBefore() => WatchWalkAsync(standIn, 0.8, ct, origin, (target, what), people);
        if (WalkMode is not { } walk || _ops.Client?.Network.CurrentSim?.Handle != origin || MeIn(origin) is not { } at) return await AsBefore();
        float feet = FeetZ?.Invoke() ?? at.Z - 0.95f;
        // (looked down from over the goal's floor where that's above her: its edge is where she'd fall from)
        if (await walk.DropOnWayAsync(new(at.X, at.Y, at.Z), feet, new(target.X, target.Y, target.Z), OnFromDrop, feet + (target.Z - at.Z) + 0.3f) is { } drop)
        {
            WalkNote($"go: as near as the way goes, and not on to the goal by the region: {drop}");
            return await AsBefore();
        }
        // (nor through someone standing on the straight way: the region's autopilot goes round nobody)
        if (HeedPeople && OnFoot(people.To).Any(p => ToLine(at, target, p.Pos) < 0.8f && p.Pos.Z > Math.Min(at.Z, target.Z) - 1
                && p.Pos.Z < Math.Max(at.Z, target.Z) + 1))
        {
            WalkNote($"go: as near as the way goes, and not on to the goal by the region: someone standing on the straight way");
            return await AsBefore();
        }
        float rise = target.Z - at.Z;
        _say($"Trying straight on to {what}{(Math.Abs(rise) >= 1 ? $", {(rise > 0 ? "up" : "down")} {Bearings.Metres(Math.Abs(rise))}" : "")}.");
        // (no nearer, never "close": short of its floor, she isn't there however near across, as at a plinth's foot)
        var trust = await TrustRegionAsync(target, $"as near as the way goes to a goal on a floor out of reach, and no drop of over {OnFromDrop:0} m the model has on the straight way",
            near, itsFloor: true, end: false, "so it ends as near as the way goes", ct, origin, ontoItsFloor: true);
        return trust.Got switch
        {
            Trust.Cancelled => false,
            Trust.OnItsFloor => null,
            Trust.There => await WatchWalkAsync(target, near, ct, origin, null, people),
            Trust.Fell => await FellAsync(at.Z, target, what, origin),
            // (where she stopped, its end logged and said as any walk's)
            _ => await WatchWalkAsync(trust.At, 0.8, ct, origin, (target, what), people),
        };
    }

    /// <summary>A walk on by the region toward a goal above her that took her down off an edge (Trust.Fell): once she's
    /// landed, said as what it was, and the walk ends there, not there. Until then it ended "as near as the way goes"
    /// under him (her partner's deck, 2026-11-22). <paramref name="fromZ"/>: her middle where it set out.</summary>
    async Task<bool> FellAsync(float fromZ, Vector3 target, string what, ulong origin)
    {
        Vector3? at = MeIn(origin);
        // (landed: her height still between two looks, up to 3 s)
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(150, CancellationToken.None);
            if (MeIn(origin) is not { } now) continue;
            bool still = at is { } was && Math.Abs(now.Z - was.Z) < 0.05f && (_ops.Client is not { } c || Math.Abs(c.Self.Velocity.Z) < 0.3f);
            at = now;
            if (still) break;
        }
        var end = at ?? target;
        float? feet = FeetZ?.Invoke();
        WalkNote(string.Create(CultureInfo.InvariantCulture, $"go: walk over, came down {fromZ - end.Z:0.0} m off an edge on the way: at {end.X:0.00},{end.Y:0.00},{end.Z:0.00}{(feet is { } ft ? $" (feet {ft:0.00})" : "")}, {Flat(end, target):0.0} m off across and {end.Z - target.Z:+0.0;-0.0} up"));
        _say($"Finished walking: you came down {Bearings.Metres(fromZ - end.Z)} off an edge on the way; {what} is {_ops.Where(target) ?? Bearings.Metres(Flat(end, target))}.");
        return false;
    }

    /// <summary>Where the stuck spot a walk marked left her no way and the model had one through it (RouteAsync): on to
    /// the walk's end <paramref name="end"/> straight by the region, trusted, the guards OnFromAsync has (a drop of more
    /// than OnFromDrop the model has on the straight way, someone standing on it). Null: not tried (the walk ends as
    /// with no way), or no nearer; else how the walk ended.</summary>
    async Task<bool?> OnThroughAsync(Route r, Vector3 end, Vector3 target, string what, double near, CancellationToken ct, ulong origin, Bystanders people)
    {
        if (WalkMode is not { } walk || _ops.Client?.Network.CurrentSim?.Handle != origin || MeIn(origin) is not { } at) return null;
        float feet = FeetZ?.Invoke() ?? at.Z - 0.95f;
        if (await walk.DropOnWayAsync(new(at.X, at.Y, at.Z), feet, new(end.X, end.Y, end.Z), OnFromDrop, feet + (end.Z - at.Z) + 0.3f) is { } drop)
        {
            WalkNote($"go: the stuck spot left no way, and not on by the region: {drop}");
            return null;
        }
        if (HeedPeople && OnFoot(people.To).Any(p => ToLine(at, end, p.Pos) < 0.8f && p.Pos.Z > Math.Min(at.Z, end.Z) - 1 && p.Pos.Z < Math.Max(at.Z, end.Z) + 1))
        {
            WalkNote("go: the stuck spot left no way, and not on by the region: someone standing on the straight way");
            return null;
        }
        var trust = await TrustRegionAsync(end, "the stuck spot just marked left no way, the model's only way going on where she stuck: the spot taken back",
            r.Moved ? 0.6 : near, itsFloor: !r.Moved, end: true, "no way, so it ends as one", ct, origin);
        switch (trust.Got)
        {
            case Trust.Cancelled: return false;
            case Trust.There:
                return r.Moved ? await WatchWalkAsync(end, 0.8, ct, origin, (target, what), people) : await WatchWalkAsync(target, near, ct, origin, null, people);
            case Trust.Close:
                if (r.Moved) _say($"Finished walking, as near to {what} as the way goes: {what} is {_ops.Where(target) ?? Bearings.Metres(Flat(trust.At, target))}.");
                else _say($"Finished walking, {OffWords(Flat(trust.At, end))} from the destination.");
                return true;
            default: return null;
        }
    }

    /// <summary>How far <paramref name="p"/> is across from the stretch <paramref name="a"/> to <paramref name="b"/>.</summary>
    static float ToLine(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = new Vector2(b.X - a.X, b.Y - a.Y);
        var ap = new Vector2(p.X - a.X, p.Y - a.Y);
        float t = ab.LengthSquared() > 1e-6f ? Math.Clamp(Vector2.Dot(ap, ab) / ab.LengthSquared(), 0, 1) : 0;
        return (ap - ab * t).Length();
    }

    /// <summary>A point in the frame of the region with handle <paramref name="origin"/> (where a walk began) as a
    /// global position, wherever she is now: PersonOps.Global puts it in the region she's in, 256 m off past a border.</summary>
    static Vector3d GlobalIn(Vector3 p, ulong origin)
    {
        Utils.LongToUInts(origin, out var ox, out var oy);
        return new Vector3d(ox + p.X, oy + p.Y, p.Z);
    }

    /// <summary>A walk to someone the plan can't take her to (<paramref name="planned"/>: their floor out of reach, or no
    /// way at all) by the way they walked there (TrailWaysAsync), said in the session log either way; null where
    /// there's none.</summary>
    async Task<Route?> TrailRouteAsync(UUID who, Vector3 from, Route planned)
    {
        if (Planner is not { CanAsk: true } planner) return null;
        float feet = FeetZ?.Invoke() ?? from.Z - 0.95f;
        var got = await TrailWaysAsync(who, planner, from, feet);
        var why = PlanSaid(planned.Of);
        var her = string.Create(CultureInfo.InvariantCulture, $"from {from.X:0.00},{from.Y:0.00},{from.Z:0.00} (feet {feet:0.00})");
        if (got.Ways is not { } ways || got.Way is not { } way)
        {
            WalkNote($"go: {why}, and no trail of theirs to take ({got.Said}), {her}");
            return null;
        }
        WalkNote($"go: by their trail, {why}, {her}: their trail {got.Said}");
        return new Route(ways, false) { Trail = true, Rise = way.Points[^1].Z - from.Z, Of = planned.Of };
    }

    /// <summary>Whether a point (a person's middle, a thing's, one given) is on a floor over or under hers: 2 m or more
    /// above or below her middle.</summary>
    public static bool OtherFloor(Vector3 her, Vector3 at) => Math.Abs(at.Z - her.Z) >= 2;

    /// <summary>The height whose floor a walk to a thing is to: where someone standing at its foot would have their
    /// middle, a metre over its lowest point (its linked set's boxes), or its own middle where that's lower. Its
    /// position is its middle, which for a building of two storeys in one mesh is upstairs, and for a tower or a big
    /// block high over anything. Reach: how far across it goes from that middle (the furthest of its boxes' corners
    /// seen from above), so the planner looks that far out for a spot beside a big one (PathPlanner.GoalReach).</summary>
    (float Z, float Reach) FootOf(UUID thing, Vector3 middle)
    {
        if (_s.Region is not { } w || w.PrimById(thing) is not { } root) return (middle.Z, 0);
        float low = float.MaxValue, reach = 0;
        void Add(Primitive p)
        {
            // (a box's reach from its middle along the world's axes: its half sizes as its rotation turns them)
            var q = w.RegionRotation(p);
            float Half(float a, float b, float c) => 0.5f * (Math.Abs(a) * p.Scale.X + Math.Abs(b) * p.Scale.Y + Math.Abs(c) * p.Scale.Z);
            float down = Half(2 * (q.X * q.Z - q.W * q.Y), 2 * (q.Y * q.Z + q.W * q.X), 1 - 2 * (q.X * q.X + q.Y * q.Y));
            float ex = Half(1 - 2 * (q.Y * q.Y + q.Z * q.Z), 2 * (q.X * q.Y - q.W * q.Z), 2 * (q.X * q.Z + q.W * q.Y));
            float ey = Half(2 * (q.X * q.Y + q.W * q.Z), 1 - 2 * (q.X * q.X + q.Z * q.Z), 2 * (q.Y * q.Z - q.W * q.X));
            if (!w.TryRegionPosition(p, out var at)) return;
            low = Math.Min(low, at.Z - down);
            reach = Math.Max(reach, new Vector2(Math.Abs(at.X - middle.X) + ex, Math.Abs(at.Y - middle.Y) + ey).Length());
        }
        Add(root);
        foreach (var (_, p) in w.Prims) if (p.ParentID == root.LocalID && !w.Avatars.ContainsKey(p.LocalID)) Add(p);
        return (low < float.MaxValue ? Math.Min(middle.Z, low + 1) : middle.Z, reach);
    }

    /// <summary>A point past the edge of the region whose frame it's in (a recorded route's step across a border).</summary>
    static bool Beyond(Vector3 p) => p.X < 0 || p.X >= 256 || p.Y < 0 || p.Y >= 256;

    /// <summary>The avatar's position in the frame of the region with this handle (where a walk began: its target stays
    /// in that frame after a crossing), or null between regions.</summary>
    Vector3? MeIn(ulong origin)
    {
        if (_ops.Me() is not { } me || _ops.Client?.Network.CurrentSim is not { } sim) return null;
        return InFrame(me.Pos, sim.Handle, origin);
    }

    /// <summary>A position in the region with handle <paramref name="sim"/>, in the frame of the one with <paramref name="origin"/>.</summary>
    static Vector3 InFrame(Vector3 pos, ulong sim, ulong origin)
    {
        if (sim == origin) return pos;
        Utils.LongToUInts(sim, out var cx, out var cy);
        Utils.LongToUInts(origin, out var ox, out var oy);
        return pos + new Vector3((float)((double)cx - ox), (float)((double)cy - oy), 0);
    }

    /// <summary>A planned way: its waypoints (the last the goal, or its stand-in when the goal was out of reach).</summary>
    readonly record struct Route(List<System.Numerics.Vector2> Legs, bool Moved)
    {
        /// <summary>The people it goes round, when they're all that keeps it from being straight (the first line's words).</summary>
        public IReadOnlyList<UUID>? Around { get; init; }
        /// <summary>Someone standing where the only way goes: it ends short of them (no legs: she's that near already).</summary>
        public UUID? InWay { get; init; }
        /// <summary>Not planned: one straight line (nothing in the way, or no model to plan by).</summary>
        public bool Straight { get; init; }
        /// <summary>How far the floor at its end is above the floor where it starts (below: less than 0), where the model
        /// has what she stands on: a walk upstairs says so.</summary>
        public float Rise { get; init; }
        /// <summary>The goal's floor out of reach from hers as the model has them, or as it guesses from her feet with
        /// nothing under her in it (its way ends as near as it goes on another floor).</summary>
        public bool Beyond { get; init; }
        /// <summary>The way the person walked to is the way they went there (Trails), not a plan.</summary>
        public bool Trail { get; init; }
        /// <summary>The plan it came from (null: none asked, or no way at all), for the log.</summary>
        public Walk.PathPlanner.Plan? Of { get; init; }
    }

    static float Length(Vector3 from, List<System.Numerics.Vector2> legs)
    {
        float len = 0;
        var prev = new System.Numerics.Vector2(from.X, from.Y);
        foreach (var p in legs) { len += System.Numerics.Vector2.Distance(prev, p); prev = p; }
        return len;
    }

    /// <summary>The way around what's in the way; null: straight (no collision model, or nothing in the way); no legs:
    /// no way at all. <paramref name="foot"/>: a thing's foot and reach (FootOf), the height whose floor a walk to that
    /// thing is to and how far across it goes.</summary>
    async Task<Route?> PlanAsync(Vector3 from, Vector3 to, CancellationToken ct, Bystanders? people = null, (float Z, float Reach)? foot = null)
    {
        if (Planner is not { } planner) return null;
        // beyond this region's edge (a recorded route's crossing): straight, by the region's autopilot
        if (Beyond(to)) return null;
        var a = new System.Numerics.Vector2(from.X, from.Y);
        var b = new System.Numerics.Vector2(to.X, to.Y);
        float toZ = foot?.Z ?? to.Z;
        // (a point just over or under her on another floor is still a way to plan, by the stairs)
        if (System.Numerics.Vector2.Distance(a, b) < 2 && !OtherFloor(from, new Vector3(to.X, to.Y, toZ))) return null;
        try
        {
            var standing = people != null ? ToGoRound(people, from) : null;
            float feet = FeetZ?.Invoke() ?? from.Z - 0.95f;
            // (the plan and the planner's reasons go in the session log she sends: Walk/WalkMode.Stuck.cs)
            // (to a person or a point out of reach, the way may end where the floors meet by steps, and go on by the
            // region from there: OnFromAsync)
            var plan = await planner.PlanAsync(a, feet, b, null, ct, toZ, standing, thing: foot?.Reach,
                why: WalkMode is { } wm ? d => wm.NoteWhy("go: why: ", d) : null, wayOn: foot == null);
            var line = $"go: {Walk.PathPlanner.Line(a, feet, b, toZ, plan, planner.CanAsk, standing)}";
            if (WalkMode is { } walk) walk.Note(line);
            else Walk.WalkMode.Log(line);
            // (a storey or more under it, where its floor meets hers only far off, as in the room under someone upstairs
            // whom a teleporter reaches: no way, as for a room upstairs behind a shut door, so the teleport where it lands)
            if (plan == null || plan.Beneath) return planner.CanAsk ? new Route(new(), false) { Of = plan } : null;
            float rise = plan.FloorKnown ? plan.GoalFloor - plan.StartFloor : 0;
            if (plan.InWay is { } inWay) return new Route(plan.Waypoints, false) { InWay = people!.Given[inWay], Rise = rise, Of = plan };
            return plan.Direct && !plan.GoalMoved ? null
                : new Route(plan.Waypoints, plan.GoalMoved)
                {
                    Around = plan.Around.Select(p => people!.Given[p]).ToList(), Rise = rise, Of = plan,
                    Beyond = !plan.OnGoalFloor,
                };
        }
        catch (OperationCanceledException) { return null; }
    }

    enum Leg { Reached, Stalled, Cancelled, Met }

    /// <summary>A stretch on the way: until the avatar is within <paramref name="within"/> of its end (0.5 m at a corner:
    /// the next stretch starts without stopping, and a wider berth cut the corner into the wall), or it stops getting
    /// anywhere for two seconds, or (with <paramref name="people"/>) someone has stepped into it (Met, people.Met who).
    /// <paramref name="lead"/>: how much sooner it counts as there for each metre a second she's going over a walk's
    /// 3.2 (a stretch ending short of someone, where she's let go and has to have stopped before them).</summary>
    async Task<Leg> LegAsync(Vector3 to, double within, CancellationToken ct, ulong origin, Bystanders? people = null, double lead = 0)
    {
        long start = Environment.TickCount64, progressAt = start;
        double best = double.MaxValue;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(150, CancellationToken.None);
            if (ct.IsCancellationRequested || MeIn(origin) is not { } pos) return Leg.Cancelled;
            KeepLevel();
            double off = new Vector2(to.X - pos.X, to.Y - pos.Y).Length();
            if (lead > 0 && _ops.Client?.Self.Velocity is { } v) off -= lead * Math.Max(0, new Vector2(v.X, v.Y).Length() - 3.2);
            if (off <= within) return Leg.Reached;
            if (people != null && _ops.Client?.Network.CurrentSim?.Handle == origin && Stepped(people, pos, to) is { } who)
            {
                people.Met = who;
                return Leg.Met;
            }
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
    /// (<paramref name="tpHint"/>). Public: the assistant's walks (MainForm.RouteAsync) come here too.
    /// <paramref name="teleport"/>: false where a teleport within her region can't land there (a walk's target still
    /// over the border it crossed).</summary>
    public async Task<bool> NoWayAsync(Vector3 target, string what, string? tpHint, bool teleport = true)
    {
        CancelAutoPilot();
        var offer = tpHint != null ? $" {tpHint} would teleport you there." : "";
        if (!teleport || !TeleportFallback() || _ops.Me() is null)
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

    /// <summary>Waits for the walk to end, then says Radegast's line; walking to a stand-in for a goal out of reach
    /// (<paramref name="outOfReach"/>), how near that is to it. It has ended when she has stopped near the target, stood
    /// still two seconds, got no nearer the target for three (below), or a minute has gone.
    /// No nearer: movement alone kept the wait going while she got nowhere (someone on the way slid ahead of her and she
    /// slid on 7 m past the point, then crept: the line came after 40 s, 2026-11-09). It's judged by the region's own
    /// updates of her, as each comes, not by the clock: the position here is carried on by her last velocity between
    /// them, and a walk going on through a gap in them (a steady walk has few, a slow region fewer) isn't given up on.
    /// Nearer is 0.1 m nearer than she has yet been, as a stretch's (LegAsync); a walk here is one straight line by the
    /// region's autopilot, whose push along anything it meets still closes on the target, so three seconds of updates
    /// with none nearer is a walk that isn't getting there. While she's still travelling (over 0.25 m/s between two
    /// updates: sliding on past the point, which the local grid's avatars do for 6 s, the region giving her velocity as
    /// zero meanwhile) it waits, ten seconds in all at most, so the distance said is where she stops.
    /// With <paramref name="dodge"/> it returns early, nothing said, when someone has stepped into the line more than
    /// 2 m from its end (people.Met who: the caller plans afresh). A walk that ends short up against someone says who
    /// in place of "something was in the way".</summary>
    async Task<bool> WatchWalkAsync(Vector3 target, double near, CancellationToken ct, ulong origin, (Vector3 At, string What)? outOfReach = null,
        Bystanders? people = null, bool dodge = false)
    {
        long start = Environment.TickCount64, stillSince = start, nearerAt = start, noNearer = 0;
        bool moved = false, again = false, pushed = false, letGo = false, nearer = false, travelling = false;
        var last = MeIn(origin) ?? target;
        double nearest = Flat(last, target);
        var heard = _ops.Client is { } c0 ? SelfUpdateGuard.LastArrival(c0) : null;   // (an update from before the walk)
        // (someone standing on the very spot, a walk too short to have been planned: let go within reach, as of a person walked to)
        bool taken = HeedPeople && people != null && OnFoot(people.To).Any(p => Flat(p.Pos, target) < 0.9f && Math.Abs(p.Pos.Z - target.Z) < 2);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(250, CancellationToken.None);
            if (ct.IsCancellationRequested || _ops.Client is not { } c) return false;
            if (MeIn(origin) is not { } pos) continue;   // (between regions a moment)
            KeepLevel();
            var me = (Pos: pos, Region: c.Network.CurrentSim?.Handle ?? origin);
            long now = Environment.TickCount64;
            bool going = c.Self.Velocity.Length() > 0.15f || Vector3.Distance(me.Pos, last) > 0.05f;
            last = me.Pos;
            if (going) { stillSince = now; moved = true; }
            var off = new Vector2(target.X - me.Pos.X, target.Y - me.Pos.Y).Length();
            long still = now - stillSince;
            if (SelfUpdateGuard.LastArrival(c) is { } word && word.Ticks != heard?.Ticks)
            {
                var at = InFrame(word.Pos, me.Region, origin);
                travelling = heard is { } before
                    && Vector3.Distance(at, before.Pos) > 0.25 * System.Diagnostics.Stopwatch.GetElapsedTime(before.Ticks, word.Ticks).TotalSeconds;
                heard = (word.Ticks, at);
                double offHeard = Flat(at, target);
                if (offHeard < nearest - 0.1) { nearest = offHeard; nearerAt = now; nearer = true; noNearer = 0; }
                else noNearer = now - nearerAt;
            }
            // a goal past the border not crossed yet: a route's next line is in the next region's frame, so it isn't done
            // till then. The region's autopilot stops the avatar at the edge and doesn't cross it (seen on the local grid:
            // standing there 15 s, across a second after a forward press), so stood there, it's walked across by the
            // forward control, once
            bool crossing = Beyond(target) && me.Region == origin;
            if (dodge && people != null && me.Region == origin && off > Math.Max(near, 2) && Stepped(people, me.Pos, target) is { } met)
            {
                people.Met = met;
                return false;
            }
            // a person or a thing (stopping 1.5 m short): the region's autopilot heads for its middle and pushed on into
            // the visitor's body, never still enough to finish (42 s, 2026-11-08), so it's let go once within reach
            if (!crossing && !letGo && (near >= 1.5 || taken) && off <= near)
            {
                letGo = true;
                CancelAutoPilot();
            }
            if (crossing && still > 1500 && !pushed)
            {
                pushed = true;
                await PushAcrossAsync(c, me.Pos, target, origin);
                stillSince = nearerAt = Environment.TickCount64;
                noNearer = 0;
                continue;
            }
            bool arrived = !crossing && off <= near && still > 700, late = now - start > 60_000;
            // (up to a border not crossed yet, the push across above is what a stop gets, as before)
            bool held = !crossing && noNearer > (nearer ? 3000 : 4000) && (!travelling || noNearer > 10_000);
            bool stalled = held || still > (crossing ? 3000 : moved ? 2000 : 4000);
            if (stalled && !arrived && !again && me.Region != origin && off > near)
            {
                // a crossing on the way: the new region doesn't carry the old one's autopilot on, so asked again once
                again = true;
                Utils.LongToUInts(origin, out var ox, out var oy);
                AutoPilotTo(c, ox + (double)target.X, oy + (double)target.Y, target.Z);
                stillSince = nearerAt = now;
                noNearer = 0;
                moved = nearer = false;
                continue;
            }
            if (!arrived && !stalled && !late) continue;
            var over = string.Create(CultureInfo.InvariantCulture,
                $"go: walk over after {(now - start) / 1000.0:0.0} s, {(arrived ? "arrived" : held ? $"no nearer for {noNearer / 1000.0:0.0} s" : late ? "a minute" : "stood still")}: {off:0.0} m off {target.X:0.00},{target.Y:0.00},{target.Z:0.00}, nearest {nearest:0.0}; at {me.Pos.X:0.00},{me.Pos.Y:0.00},{me.Pos.Z:0.00}{(FeetZ?.Invoke() is { } ft ? $" (feet {ft:0.00})" : "")}");
            if (WalkMode is { } wm) wm.Note(over);
            else Walk.WalkMode.Log(over);
            // ended short, not up against someone nor at a border: what the model has there, so her log can say why
            if (!arrived && !crossing && off >= 2.5 && !(people != null && Against(people, me.Pos, target) != null))
                NoteStuck("go: ended short", InFrame(me.Pos, origin, me.Region), target, target, origin);
            if (!arrived) CancelAutoPilot();
            if (outOfReach is { } o && off < 2.5)
            {
                _say($"Finished walking, as near to {o.What} as the way goes: {o.What} is {_ops.Where(o.At) ?? Bearings.Metres(Flat(me.Pos, o.At))}.");
                return true;
            }
            bool done = arrived || off < 2.5 && !crossing;
            var why = done ? "" : crossing ? " (it didn't cross into the next region)"
                : people != null && Against(people, me.Pos, target) is { } person ? $": {await InWayAsync(person, target)}" : " (something was in the way)";
            _say(string.Create(CultureInfo.InvariantCulture, $"Finished walking, {OffWords(off)} from the destination{why}."));
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
        if (route) _say("Route stopped: you're walking by hand. //route resume goes on; //route skip goes on without the step it was on.");
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
        _aim = null;
        if (_ops.Client is not { } c || c.Self.SittingOn != 0) return;
        c.Self.AutoPilotCancel();
        WalkMode?.OwnCancel();   // (the region answers it with its word that its autopilot stopped: not news for a stuck line)
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
    /// <summary>A follow is going (its loop logs its own stuck line: the session autopilot's giving up isn't one).</summary>
    public bool Following => _following != null;
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
    /// straight line is blocked (PathPlanner, every 1.5 s: its corners in turn, and up or down the stairs to someone on
    /// another floor; the way they walked where the plan has none to their floor: FollowLoopAsync), standing still once
    /// close on their floor. Said as it goes (the "follow"
    /// cue's text): when they stop and you're with them, when they sit, when they're getting away, when their floor is
    /// out of reach from where she can get to (hovering, up on a stage with no steps, a balcony a teleporter goes to),
    /// and when they're gone (a teleport or logging off) or out of range.</summary>
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

    /// <summary>Following them: see <see cref="Follow"/>. Where the plan has no way to their floor (out of reach,
    /// beneath them, no way at all), or only a guess from what the model hasn't got under her (carried on level from her
    /// feet) while she's on their trail, she goes the way they walked there (Trails; TrailWaysAsync), as a person
    /// walking behind them would: the blind tester's partner went up steps the collision model lacks (2026-11-19) and
    /// every follow said "no way up from here" and stood. Until 2026-11-11 the follow aimed 2.5 m short of them whatever
    /// their height and steered straight at them where the plan found no way, and the region's own physics carried her
    /// up; planned to their height since, it stopped where the model had no way. Their trail is left when she's with
    /// them, when the plan has a way to their floor, or when she makes no half metre on it in 3 s (then not taken
    /// again for 20 s, or till they've gone 3 m on; again after that, twice as long each time, up to 160 s), and "no way
    /// up from here" is said only with neither, and once until they're in reach.</summary>
    async Task FollowLoopAsync(PersonOps.Person p, CancellationToken ct)
    {
        const float Behind = 2.5f, Restart = 3.6f, Corner = 1f, TrailCorner = 0.7f;
        const long TrailRetry = 20_000;
        int lost = 0;
        Vector3? lastPos = null;
        long lastAt = 0, toldFarAt = 0, planAt = 0;
        var vel = Vector3.Zero;
        bool toldWith = false, toldSat = false;
        double lastDist = 0, farDist = 0;
        List<System.Numerics.Vector2>? ways = null;   // the way's corners while the straight line is blocked
        int wayAt = 0;
        // their floor out of reach from here: the way goes as near as it can, and that's said once she's there; noWay:
        // plans in a row that found no way at all
        bool beyond = false, toldBeyond = false;
        int noWay = 0;
        // on their trail (ways its points); when it last stuck, and where they were; the log told there's none to take
        bool onTrail = false, toldNoTrail = false;
        (long At, Vector3 Them)? trailStuck = null;
        // (how many times in a row it has: each waits twice as long before it's taken again, 20 s to 160 s, and only the
        // first is taken again once they've gone 3 m on, as a way she can't walk stays one; with them, or the plan
        // reaching them, starts afresh)
        int trailSticks = 0;
        long TrailWait() => TrailRetry << Math.Clamp(trailSticks - 1, 0, 3);
        long toldTrailAt = -60_000;   // (said once in a while: a plan and the trail can take turns on stairs both have)
        // stuck while steering after them, for the session log: where she was when she last made half a metre, and when
        (Vector3 At, long Since)? stuckFrom = null;
        long steeredAt = 0, stuckNoted = 0;
        bool Say(string text) { if (CueText("follow")) _say(text); return true; }
        static float Off(System.Numerics.Vector2 w, Vector3 at) => new Vector2(w.X - at.X, w.Y - at.Y).Length();
        void OffTrail(string why, Vector3 at)
        {
            if (!onTrail) return;
            onTrail = false;
            WalkNote(string.Create(CultureInfo.InvariantCulture, $"follow: off their trail, {why}: she at {at.X:0.00},{at.Y:0.00},{at.Z:0.00}"));
        }
        while (!ct.IsCancellationRequested)
        {
            // a look at them every 400 ms; meanwhile, ten times a second, a corner of the way she has come within a metre
            // of is left for the next (looked at only every 400 ms, the autopilot had stopped her at each, a stutter at
            // every turn and every flight of stairs); on their trail its last point too, then the spot behind them
            for (int k = 0; k < 4 && !ct.IsCancellationRequested; k++)
            {
                await Task.Delay(100, CancellationToken.None);
                if (ways == null || wayAt >= ways.Count - 1 || _ops.Client is not { } cc || _ops.Me() is not { } at || Off(ways[wayAt], at.Pos) >= (onTrail ? TrailCorner : Corner)) continue;
                wayAt++;
                bool more = wayAt < ways.Count - 1;
                if (!more && !beyond && !onTrail) break;   // (the last stretch: to the spot behind them, worked out below)
                if (_ops.Global(new Vector3(ways[wayAt].X, ways[wayAt].Y, at.Pos.Z)) is { } gc) Steer(cc, gc, more || onTrail);
            }
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
                OffTrail("they sat down", me.Pos);
                (ways, planAt) = (null, 0);   // (no corner for the looks between to walk her on to; planned afresh once she goes on)
                continue;
            }
            toldSat = false;
            var ahead = there + new Vector3(vel.X, vel.Y, 0) * 0.6f;
            var to = new Vector2(ahead.X - me.Pos.X, ahead.Y - me.Pos.Y);
            float d = to.Length();
            // (right over or under her on another floor isn't with her: the way there is by the stairs)
            if (d <= Restart && (!_s.Motion.Moving || d <= Behind + 0.3f) && !OtherFloor(me.Pos, there))
            {
                if (_s.Motion.Moving) _s.Motion.StopAutoPilot(c, true);
                if (speed < 0.3f && !toldWith) toldWith = Say($"With {p.Name}, {_ops.Where(there) ?? "here"}.");
                OffTrail("with them", me.Pos);
                (ways, planAt, trailStuck, trailSticks) = (null, 0, null, 0);
                continue;
            }
            if (speed > 0.5f) toldWith = false;
            var dir = to / Math.Max(d, 0.01f);
            var aim = new Vector3(ahead.X - dir.X * Behind, ahead.Y - dir.Y * Behind, ahead.Z);
            // around what's in the way, and up or down the stairs: planned to where they'll be, at their height (the spot
            // short of them can be in the air beside a balcony they're on, over the ground)
            if (Planner is { } planner && t - planAt > 1500)
            {
                planAt = t;
                float feet = FeetZ?.Invoke() ?? me.Pos.Z - 0.95f;
                var plan = await planner.PlanAsync(new(me.Pos.X, me.Pos.Y), feet, new(ahead.X, ahead.Y), null, CancellationToken.None, ahead.Z);
                noWay = plan == null && planner.CanAsk ? noWay + 1 : 0;
                beyond = plan is { FloorKnown: true, OnGoalFloor: false };
                // a way to their floor; from what the model hasn't got under her it's a guess, which their trail beats
                bool planned = plan is { OnGoalFloor: true } && !(onTrail && !plan.FloorKnown);
                bool trailed = false;
                if (planned) (trailStuck, trailSticks) = (null, 0);
                if (!planned && planner.CanAsk && (trailStuck is not { } ts || t - ts.At > TrailWait() || trailSticks == 1 && Flat(ts.Them, there) > 3))
                {
                    var got = await TrailWaysAsync(p.Id, planner, me.Pos, feet);
                    if (got.Ways is { } tw)
                    {
                        if (!onTrail)
                        {
                            WalkNote(string.Create(CultureInfo.InvariantCulture, $"follow: by their trail, {PlanSaid(plan)}: she at {me.Pos.X:0.00},{me.Pos.Y:0.00},{me.Pos.Z:0.00} (feet {feet:0.00}), they at {there.X:0.00},{there.Y:0.00},{there.Z:0.00}; their trail {got.Said}"));
                            float up = there.Z - me.Pos.Z;
                            if (t - toldTrailAt > 30_000)
                            {
                                toldTrailAt = t;
                                Say($"Going the way {p.Name} went{(Math.Abs(up) >= 1 ? $", {(up > 0 ? "up" : "down")} {Bearings.Metres(Math.Abs(up))}" : "")}.");
                            }
                        }
                        // ("no way up" said already stays said: it and the trail took turns, every 20 to 30 s, at a
                        // trail she couldn't walk)
                        (onTrail, trailed, toldNoTrail) = (true, true, false);
                        (ways, wayAt) = (tw, 0);
                    }
                    else if (!toldNoTrail)
                    {
                        toldNoTrail = true;
                        WalkNote(string.Create(CultureInfo.InvariantCulture, $"follow: {PlanSaid(plan)}, and no trail of theirs to take ({got.Said}): she at {me.Pos.X:0.00},{me.Pos.Y:0.00},{me.Pos.Z:0.00} (feet {feet:0.00}), they at {there.X:0.00},{there.Y:0.00},{there.Z:0.00}"));
                    }
                }
                if (!trailed)
                {
                    OffTrail(planned ? "the plan has a way to their floor" : "their trail gone", me.Pos);
                    if (plan != null && !beyond) (toldBeyond, toldNoTrail) = (false, false);
                    (ways, wayAt) = (plan is { Waypoints.Count: > 0 } && (beyond || plan is { Direct: false, Waypoints.Count: > 1 }) ? plan.Waypoints : null, 0);
                }
            }
            // its corners in turn (up a stair's flight, round a corner), then the spot behind them; or, their floor out of
            // reach, as near as the way goes, said once she's there (or when there's no way at all); on their trail, its
            // points in turn, the last too, then the spot behind them
            float pass = onTrail ? TrailCorner : Corner;
            while (ways != null && wayAt < ways.Count - 1 && Off(ways[wayAt], me.Pos) < pass) wayAt++;
            bool corner = ways != null && (wayAt < ways.Count - 1 || onTrail && Off(ways[^1], me.Pos) >= pass);
            bool standIn = beyond && !onTrail && ways != null && !corner;
            // (there: stopped within reach of the stand-in, which the autopilot lets go of up to a metre short at a walk)
            bool arrived = standIn && Off(ways![^1], me.Pos) is var left && (left < 0.6f || left < 1.5f && !_s.Motion.Moving);
            if (!toldBeyond && !onTrail && (arrived || noWay >= 2)) toldBeyond = Say(OutOfReach(p.Name, there, me.Pos, me.Heading));
            var goal = corner || standIn ? new Vector3(ways![wayAt].X, ways[wayAt].Y, me.Pos.Z) : aim;
            if (arrived) { if (_s.Motion.Moving) _s.Motion.StopAutoPilot(c, true); }
            else if (_ops.Global(goal) is { } g) Steer(c, g, corner);
            // stuck: steering after them, for a point over a metre off, through the last 3 s and no half metre made. The
            // session autopilot's own giving up never comes in a follow (each look moves its target on, its progress
            // counted afresh), so the follow says what the model has there itself, once in 10 s; on their trail, it's
            // left (the plan's way, or as near as it goes, again)
            bool pushing = !arrived && Flat(goal, me.Pos) > 1f;
            if (!pushing || t - steeredAt > 1000) stuckFrom = null;
            steeredAt = pushing ? t : 0;
            if (pushing && (stuckFrom is not { } sf || Flat(sf.At, me.Pos) > 0.5f)) stuckFrom = (me.Pos, t);
            else if (stuckFrom is { } held && t - held.Since > 3000)
            {
                if (t - stuckNoted > 10_000)
                {
                    stuckNoted = t;
                    NoteStuck(string.Create(CultureInfo.InvariantCulture, $"follow: stuck{(onTrail ? " on their trail" : "")}, no half metre made in {(t - held.Since) / 1000.0:0.0} s, {d:0.0} m from where they'll be"),
                        me.Pos, goal, there, _ops.Client?.Network.CurrentSim?.Handle ?? 0);
                }
                if (onTrail)
                {
                    trailSticks++;
                    OffTrail($"no half metre made on it in 3 s (not taken again for {TrailWait() / 1000} s{(trailSticks == 1 ? ", or till they've gone 3 m on" : "")})", me.Pos);
                    trailStuck = (t, there);
                    (ways, planAt, stuckFrom) = (null, 0, null);
                }
            }
            if (now.Dist > 20 && now.Dist > farDist + 2 && t - toldFarAt > 10_000)
            {
                toldFarAt = t;
                farDist = now.Dist;
                Say($"{p.Name} is getting away: {_ops.Where(now.Pos)}.");
            }
            else if (now.Dist < 15) farDist = 0;
        }
    }

    /// <summary>A walk's line in the session log she sends and the walk log.</summary>
    void WalkNote(string line)
    {
        if (WalkMode is { } wm) wm.Note(line);
        else Walk.WalkMode.Log(line);
    }

    /// <summary>Why a plan to someone (a follow's, a walk's) took their trail, for the log: no way, beneath them, their
    /// floor out of reach and how far short the way ends, or a guess from nothing under her in the model.</summary>
    static string PlanSaid(Walk.PathPlanner.Plan? plan) => plan == null ? "no way planned"
        : plan.Beneath ? "the plan's way ending a storey or more under them"
        : !plan.OnGoalFloor ? "their floor out of reach" + (plan.Meets is { } mt and > 0 ? string.Create(CultureInfo.InvariantCulture, $" (the floors meeting {mt:0.0} m from them)") : "")
            + (plan.FloorKnown ? "" : ", reckoned on level from her feet with nothing under her in the model")
        : "the plan's way a guess, reckoned on level from her feet with nothing under her in the model";

    /// <summary>Someone's trail (Trails.Find: from where they last left her floor within TrailJoin, else where it passes
    /// nearest her on her level), the way through its points from there to where they are, got onto by the plan (a wall
    /// the model has between, or a gap: until 2026-11-20 straight within 3 m, unchecked): on its floor, or, where the
    /// model's floor of hers ends within TrailEdge of it (the top of steps it lacks), to there and on. Within TrailBeside
    /// she's on it already. Null and why not (for the log) where they haven't one here, or it can't be got onto.</summary>
    async Task<(List<System.Numerics.Vector2>? Ways, Walk.Trails.Way? Way, string Said)> TrailWaysAsync(UUID who, Walk.PathPlanner planner, Vector3 her, float feet)
    {
        const float TrailJoin = 30f, TrailBeside = 0.5f, TrailEdge = 1f;
        if (Trails is not { } trails) return (null, null, "no trails kept");
        var (found, why) = trails.Find(who, new(her.X, her.Y, her.Z), feet, TrailJoin);
        var missed = new List<string>();
        foreach (var way in found)
        {
            var pts = way.Points.Select(v => new System.Numerics.Vector2(v.X, v.Y)).ToList();
            if (way.Off <= TrailBeside) return (pts, way, Walk.Trails.Said(way));
            var plan = await planner.PlanAsync(new(her.X, her.Y), feet, pts[0], null, CancellationToken.None, way.Join.Z);
            if (plan is { InWay: null, Waypoints.Count: > 0 }
                && (plan is { OnGoalFloor: true, GoalMoved: false } || System.Numerics.Vector2.Distance(plan.Waypoints[^1], pts[0]) <= TrailEdge))
                return ((plan.GoalMoved ? plan.Waypoints.Concat(pts) : plan.Waypoints.Concat(pts.Skip(1))).ToList(), way,
                    $"{Walk.Trails.Said(way)}, the plan's {plan.Waypoints.Count} {(plan.Waypoints.Count == 1 ? "stretch" : "stretches")} onto it{(plan.GoalMoved ? " to its floor's edge" : "")}");
            missed.Add(string.Create(CultureInfo.InvariantCulture, $"no way planned onto it at {way.Join.X:0.0},{way.Join.Y:0.0},{way.Join.Z:0.0}, {way.Off:0.0} m off"));
        }
        if (why.Length > 0) missed.Add(why);
        return (null, found.FirstOrDefault(), string.Join("; ", missed));
    }

    /// <summary>Where people around have walked lately (MainForm starts it): following and a walk to someone take it
    /// where the plan has no way to their floor.</summary>
    public Walk.Trails? Trails { get; set; }

    /// <summary>The session's autopilot, following, toward a point. Toward a corner of the way it never counts as there
    /// (TickPilot stops within its stop distance and her speed's lead): the follow moves it on to the next corner as she
    /// comes within a metre of this one, so she turns there without stopping.</summary>
    void Steer(GridClient c, Vector3d to, bool corner)
    {
        const double Through = -10;
        if (_s.Motion.RetargetAutoPilot(c, to, corner ? Through : 0.5)) return;
        _s.Motion.StartAutoPilot(c, to, 0.5, null, route: false);
        if (corner) _s.Motion.RetargetAutoPilot(c, to, Through);
    }

    /// <summary>Someone followed whose floor is out of reach from here, said once she's as near as the way goes: "Kestrel
    /// is 4 metres above you; no way up from here.", with how far off and which way when they aren't overhead.</summary>
    static string OutOfReach(string name, Vector3 them, Vector3 me, double heading)
    {
        float dx = them.X - me.X, dy = them.Y - me.Y, dz = them.Z - me.Z, flat = new Vector2(dx, dy).Length();
        if (Math.Abs(dz) < 1) return $"No way to {name} from here: {Bearings.Relative(dx, dy, dz, heading)}.";
        var off = flat >= 2 ? $", {Bearings.Metres(flat)} away at {Bearings.Clock(Bearings.Compass(0, 0, dx, dy) - heading)}" : "";
        return $"{name} is {Bearings.Metres(Math.Abs(dz))} {(dz > 0 ? "above" : "below")} you{off}; no way {(dz > 0 ? "up" : "down")} from here.";
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
        int at = FindLandmark(marks, arg);
        if (at < 0) { _say($"No landmark {arg}. //tp list lists them; //tp Region/X/Y/Z goes to a place."); return false; }
        return await TeleportLandmarkAsync(marks[at].Id, marks[at].Name);
    }

    /// <summary>`//tp N` or `//tp NAME`'s landmark: by its number in the list, else the first whose name starts so; -1 for none.</summary>
    static int FindLandmark(List<(UUID Id, string Name)> marks, string arg) =>
        int.TryParse(arg, out var n) && n >= 1 && n <= marks.Count ? n - 1
            : marks.FindIndex(m => m.Name.StartsWith(arg, StringComparison.OrdinalIgnoreCase));

    /// <summary>The region a `//tp` goes to: a place's, or its landmark's; null when that can't be told.</summary>
    async Task<string?> TeleportRegionAsync(string arg)
    {
        if (Places.Parse(arg) is { } place) return place.Region;
        var marks = await LandmarksAsync();
        if (FindLandmark(marks, arg) is not (>= 0 and var at)) return null;
        var where = await _ops.ReadAsync("TesselInventory", "landmark", new JsonObject { ["item_id"] = marks[at].Id.ToString() });
        return where.Str("region") is { Length: > 0 } region ? region : null;
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
            _s.Places.TeleportArrived(c);   // (for the history: one counted by being there may never report finishing)
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

    /// <summary>Teleport to a place (an SLurl, Region/x/y/z): said as it goes, with what it is when there's more to say
    /// ("Teleporting to Pier Party, an event on now: Bay City 62, 58, 24."), and the arrival with where.</summary>
    public async Task<bool> GoToAsync(Place place, string? what = null)
    {
        _say(what != null ? $"Teleporting to {what}: {place}." : $"Teleporting to {place}.");
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
    /// <summary>//export [NAME] [as KIND], //import [PATH] (MainForm.Exports.cs); //autosit WORDS (MainForm.AutoSit.cs).</summary>
    public Func<string, Task>? Export { get; set; }
    public Func<string, Task>? Import { get; set; }
    public Func<string, Task>? AutoSit { get; set; }

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

    /// <summary>`//point`'s words: her own pointing (MainForm.PointAt.cs).</summary>
    public Func<string, Task>? PointAt { get; set; }

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
    /// <summary>A route paused or stopped: its lines, the line it goes on from, and the region whose coordinates that
    /// line is in (where its step began: a `//go xyz` is in the frame of the region the step before it ended in).</summary>
    (string Name, List<string> Lines, int Next, ulong Frame)? _routeAt;
    /// <summary>The route playing now and the step it's on (//threads).</summary>
    volatile string? _playing;
    /// <summary>The route playing (PlayLinesAsync), for `//route skip` to wait out.</summary>
    Task _played = Task.CompletedTask;
    /// <summary>`//route skip` while a route plays: it stops quietly where it is, and the skip goes on from there.</summary>
    volatile bool _skipping;

    async Task RouteAsync(List<string> a)
    {
        var arg = string.Join(" ", a).Trim();
        if (arg.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (_route == null)
            {
                // (a paused route is forgotten, as its words say)
                if (_routeAt is { } paused) { _routeAt = null; _say($"Route ended: {paused.Name}."); }
                else _say("No route playing.");
                return;
            }
            _route.Cancel();
            StopWalking();
            return;
        }
        if (arg.Equals("resume", StringComparison.OrdinalIgnoreCase) || arg.Equals("again", StringComparison.OrdinalIgnoreCase))
        {
            // (playing, it isn't stopped: said so, as a walk's trouble can sound like the route's end)
            if (_route != null) { _say($"Still playing {_playing ?? "a route"}. //route skip leaves out the step it's on; //route stop stops it."); return; }
            if (_routeAt is not { } at) { _say("No route to resume."); return; }
            await PlayAsync(at.Name, at.Lines, at.Next, at.Frame);
            return;
        }
        if (arg.Equals("skip", StringComparison.OrdinalIgnoreCase) || arg.Equals("next", StringComparison.OrdinalIgnoreCase))
        {
            await SkipAsync();
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
    /// `//route resume` goes on from that step, `//route skip` from the one after (SkipAsync).</summary>
    public async Task PlayRouteAsync(string name, string text)
    {
        var lines = RouteLines(text);
        if (!lines.Any(IsCommand)) { _say($"{name} has no // lines to play."); return; }
        await PlayAsync(name, lines, 0);
    }

    static List<string> RouteLines(string text) => text.Replace("\r\n", "\n").Split('\n')
        .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    Task PlayAsync(string name, List<string> lines, int from, ulong frame = 0, string? opening = null) =>
        _played = PlayLinesAsync(name, lines, from, frame, opening);

    /// <param name="frame">The region whose coordinates the line at <paramref name="from"/> is in; 0, hers.</param>
    /// <param name="opening">Said first, instead of that it's playing or resuming.</param>
    async Task PlayLinesAsync(string name, List<string> lines, int from, ulong frame, string? opening)
    {
        _route?.Cancel();
        var stop = _route = new CancellationTokenSource();
        _routeAt = null;   // (playing, nothing's paused)
        int steps = lines.Count(IsCommand);
        int step = lines.Take(from).Count(IsCommand);
        _playing = $"the route {name}";
        _say(opening ?? (from == 0 ? $"Playing {name}: {steps} {(steps == 1 ? "step" : "steps")}. //route stop stops." : $"Resuming {name} at step {step + 1} of {steps}."));
        if (frame == 0) frame = Here();
        // stopped (by //route stop, walking by hand, //kill) or by //route skip, which says what comes next itself
        void Stopped(int i, ulong at, string said)
        {
            _routeAt = (name, lines, i, at);
            if (!_skipping) _say(said);
        }
        try
        {
            for (int i = from; i < lines.Count; i++)
            {
                if (stop.IsCancellationRequested) { Stopped(i, frame, $"Route stopped before step {step + 1} of {steps}. //route resume goes on; //route skip goes on without it."); return; }
                var line = lines[i];
                if (!IsCommand(line)) { Quoted($"Note: {line}"); continue; }
                step++;
                var verb = line.TrimStart()[2..].TrimStart();
                if (verb.StartsWith("route", StringComparison.OrdinalIgnoreCase) || verb.StartsWith("record", StringComparison.OrdinalIgnoreCase)) continue;
                _playing = $"the route {name}, at step {step} of {steps}";
                // a wait says so where it starts, or a listener takes the quiet for a stall; a run of //pause lines (a
                // long wait, ten minutes a line) is said once as its total, and a wait under a second isn't said
                bool waitGoingOn = PauseOf(line) != null && i > from && PauseOf(lines[i - 1]) != null;
                if (!waitGoingOn)
                {
                    double wait = lines.Skip(i).Select(PauseOf).TakeWhile(s => s != null).Sum(s => s!.Value);
                    _say(wait >= 1 ? $"Step {step} of {steps}: waiting {Walk.RouteRecorder.WaitWords((int)Math.Round(wait))}." : $"Step {step} of {steps}.");
                }
                _pointEnd = null;
                ulong at = frame, began = Here();
                bool ok = await RunAsync(line, LineFrame(at));
                if (stop.IsCancellationRequested) { Stopped(i, at, $"Route stopped at step {step} of {steps}. //route resume does it again; //route skip goes on without it."); return; }
                // (a step to a point that the walk called done well off it: as one that didn't get there)
                var missed = ok ? PointMissed() : null;
                if (!ok || missed != null)
                {
                    _routeAt = (name, lines, i, at);
                    _say($"Route paused at step {step} of {steps}{(missed != null ? $": {missed}" : "")}. //route resume tries it again; //route skip goes on without it; //route stop ends it.");
                    return;
                }
                // the next line is in the coordinates of the region this step left her in, as Radegast reads it in the
                // one she's in (a wait while seated on a ride counts, carried across a border)
                if (Moves(line) || Here() != began) frame = Here();
                await Task.Delay(300);
            }
            _routeAt = null;
            _say($"Route finished: {name}.");
        }
        finally { if (_route == stop) { _route = null; _playing = null; } }
    }

    /// <summary>`//route skip`: the route goes on without the step it's paused or stopped at, from where she is; playing,
    /// without the step it's on (a walk stopped, a wait cut short, a teleport under way left out once it's over). Said
    /// with the step it goes on with and what leaving this one out means: a wait goes whole (a long one is several
    /// lines); a teleport elsewhere takes the steps there with it, up to the route's next teleport, unless she's there
    /// already (with no teleport after them, nothing's left out, and it says how to go on from there); sitting down,
    /// standing, takes the ride after it and standing up; a walk across a border leaves the next line in the coordinates
    /// of the region it went into.</summary>
    async Task SkipAsync()
    {
        if (_route is { } playing)
        {
            _skipping = true;
            try { playing.Cancel(); StopWalking(); await _played; }
            finally { _skipping = false; }
        }
        if (_routeAt is not { } paused) { _say("No route playing or paused, so no step to skip."); return; }
        var (name, lines, i, frame) = paused;
        // (stopped between steps: the notes before the next are read, as resuming would)
        for (; i < lines.Count && !IsCommand(lines[i]); i++) Quoted($"Note: {lines[i]}");
        if (i >= lines.Count) { _routeAt = null; _say($"Route finished: {name}."); return; }
        var line = lines[i];
        var words = Split(line.TrimStart()[2..].TrimStart('/'));
        var rest = words.Skip(1).ToList();
        var sim = _ops.Client?.Network.CurrentSim;
        int steps = lines.Count(IsCommand), first = lines.Take(i + 1).Count(IsCommand), next = i + 1;
        string what = "", after = "";
        bool moved = Moves(line);   // (the next step starts from where she is, not where this one would have left her)
        switch (LineVerb(line))
        {
            case "pause":
                while (next < lines.Count && PauseOf(lines[next]) != null) next++;
                break;
            case "tp" when rest.Count > 0 && lines.Skip(next).Any(IsCommand):
                var region = await TeleportRegionAsync(string.Join(" ", rest));
                if (region == null) { what = ", a teleport"; after = $": the steps after it were written for where it goes, and you're still in {sim?.Name}"; break; }
                what = $", the teleport to {region}";
                if (sim != null && sim.Name.Equals(region, StringComparison.OrdinalIgnoreCase)) { after = ": you're there already"; frame = sim.Handle; break; }
                // not there: what the route does there can't be done from here
                int to = next;
                while (to < lines.Count && LineVerb(lines[to]) != "tp") to++;
                if (to >= lines.Count)
                {
                    _say($"Step {first} of {steps} is the teleport to {region}, and the rest of the route is there; you're in {sim?.Name}. "
                         + $"//route resume tries the teleport again; once you're in {region} another way, //route skip goes on with step {first + 1}.");
                    return;
                }
                if (lines.Skip(next).Take(to - next).Any(IsCommand)) what += " and the steps there";
                next = to;
                after = $": you're in {sim?.Name}";
                moved = false;
                break;
            case "go" when rest.FirstOrDefault()?.Equals("tp", StringComparison.OrdinalIgnoreCase) == true:
                what = ", a teleport within the region";
                break;
            case "go" when GoPoint(rest) is { } p && sim != null:
                // a walk across a border: the lines after it are in the coordinates of the region it went into
                var g = InFrame(p, frame, 0);
                var into = _s.VisibleRegions().FirstOrDefault(w =>
                {
                    Utils.LongToUInts(w.Handle, out var wx, out var wy);
                    return g.X >= wx && g.X < wx + w.SizeX && g.Y >= wy && g.Y < wy + w.SizeY;
                });
                if (into == null || into.Handle == frame) break;
                frame = into.Handle;
                if (into.Handle != sim.Handle) { what = $", the walk across into {into.Name}"; after = $": you're still in {sim.Name}"; }
                break;
            case "sit":
                var seat = string.Join(" ", rest);
                what = seat.Equals("ground", StringComparison.OrdinalIgnoreCase) ? ", sitting on the ground" : $", sitting on {seat}";
                if (_ops.Client?.Self.SittingOn is not 0) break;   // (seated already: what follows goes on as written)
                // standing: a ride after it (waits and notes before standing up) and the standing up go with it
                int up = next;
                while (up < lines.Count && (!IsCommand(lines[up]) || PauseOf(lines[up]) != null)) up++;
                if (up >= lines.Count || LineVerb(lines[up]) is not ("stand" or "unsit")) break;
                what += lines.Skip(next).Take(up - next).Any(l => PauseOf(l) != null) ? " and the ride after it" : " and standing up";
                next = up + 1;
                break;
        }
        int last = lines.Take(next).Count(IsCommand);
        var which = first == last ? $"step {first} of {steps}" : last == first + 1 ? $"steps {first} and {last} of {steps}" : $"steps {first} to {last} of {steps}";
        var said = (LineVerb(line) == "pause" ? $"Skipped the wait at {which}." : $"Skipped {which}{what}{after}.")
                   + (lines.Skip(next).Any(IsCommand) ? $" Going on with step {last + 1}{(moved ? " from where you are" : "")}." : "");
        WalkNote($"route: skipped {which} ({line}), on from line {next + 1} of {lines.Count}");
        await PlayAsync(name, lines, next, frame, said);
    }

    ulong Here() => _ops.Client?.Network.CurrentSim?.Handle ?? 0;

    /// <summary>The frame a route's line is run in (RunAsync): the region whose coordinates it's in, when she's in another
    /// and that one is in view (a neighbour); 0 otherwise, the line taken in hers, as before (gone far, by a teleport
    /// since it paused, a point there would be far off).</summary>
    ulong LineFrame(ulong frame) => frame != 0 && frame != Here() && _s.VisibleRegions().Any(w => w.Handle == frame) ? frame : 0;

    /// <summary>A `//` line's command word, as RunAsync reads it.</summary>
    static string LineVerb(string line)
    {
        var words = Split(line.TrimStart()[2..].TrimStart('/'));
        return words.Count == 0 ? "" : words[0].ToLowerInvariant().TrimEnd(':');
    }

    /// <summary>A step that takes her somewhere (as RouteRecorder keeps the frame: a walk, a teleport, sitting, standing).</summary>
    static bool Moves(string line) => LineVerb(line) is "go" or "tp" or "sit" or "stand" or "unsit";

    /// <summary>A `//go xyz X,Y[,Z]` line's point (or bare `//go X,Y[,Z]`, as GoAsync reads them), from the words after go;
    /// null for any other.</summary>
    static Vector3? GoPoint(List<string> a)
    {
        if (a.Count == 0) return null;
        bool xyz = a[0].Equals("xyz", StringComparison.OrdinalIgnoreCase);
        if (!xyz && a.Count == 1 && !a[0].Contains(',')) return null;
        var nums = string.Join(",", xyz ? a.Skip(1) : a).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(n => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToList();
        if (nums.Count < 2 || (!xyz && nums.Count > 3) || nums.Any(double.IsNaN)) return null;
        return new Vector3((float)nums[0], (float)nums[1], 0);
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

    static double PauseSecs(List<string> a) =>
        a.Count > 0 && double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 600) : 1;

    /// <summary>How long a `//pause` line waits; null for any other line.</summary>
    static double? PauseOf(string line)
    {
        if (!IsCommand(line)) return null;
        var words = Split(line.TrimStart()[2..].TrimStart('/'));
        return words.Count > 0 && words[0].ToLowerInvariant().TrimEnd(':') == "pause" ? PauseSecs(words.Skip(1).ToList()) : null;
    }

    async Task<bool> PauseAsync(List<string> a)
    {
        double secs = PauseSecs(a);
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
