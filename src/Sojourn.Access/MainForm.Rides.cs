using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Access.Walk;

namespace Sojourn.Access;

/// <summary>
/// Recording a ride to hear it told afterwards (docs/access-app.md > Walk mode > Recording a ride): C in walk mode or
/// `//ride` starts and stops it, Shift+C or `//ride describe` has it told (again, or `//ride more`, for more). The
/// recording is Walk/RideRecorder's; here it's started, said, given its pictures (the view out of sight) and its look
/// round (People's and Objects' lists), and told through the describe key's path, so the describer gets it in one call.
/// Without the assistant it's recorded without pictures and told from what was known along the way.
/// </summary>
public sealed partial class MainForm
{
    RideRecorder? _rides;

    void StartRides()
    {
        var h = Host!;
        var rec = _rides = new RideRecorder(_walk!, () => h.Session,
            fwd => h.Describe is { } d ? d.MovingPictureAsync(fwd, 640, CancellationToken.None) : Task.FromResult<JsonObject?>(null),
            RideAroundAsync, a => BeginInvoke(a))
        { PicturesWanted = () => RidePicturesOff() == null };
        _walk!.Ride = rec;
        _walk.RideKey = words => _ = RideWordsAsync(words, key: true);
        rec.EndedByItself += r => Answer($"{Cap(r.Ended ?? "")}: ride recorded, {RideRecorder.Length(r.Ms)}, {Pictures(r)}. " +
                                         "Shift+C in walk mode, or //ride describe, tells it.");
        _cmd.Ride = words => (Task)Invoke(() => RideWordsAsync(words, key: false));
        h.Session.Api.Register(new RideApi(h.Session, rec, f => (Task)Invoke(f)));
        // nothing from an incognito spell is kept: a ride recorded in one goes when it ends
        h.Session.Private.Changed += (on, _, _) => { if (!on) BeginInvoke(() => rec.DropPrivate()); };
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    static string Pictures(RideRecorder.Ride r) => r.Pictures == 1 ? "1 picture" : $"{r.Pictures} pictures";

    /// <summary>Why a ride gets no pictures now (no view drawing, or no assistant to tell them), or null.</summary>
    string? RidePicturesOff()
    {
        if (Host is not { } h) return "not in world";
        // (only when there's no view or it gave up: one starting or restarting draws once it's up, and a ride said to be
        // "without pictures" then took twelve in the local test)
        if (h.Offscreen is not { } view || view.State == "stopped") return "the view isn't drawing on this PC";
        if (!_opt.Assistant || (!h.Assistant.SidecarAlive && h.Sidecar == null)) return "the assistant isn't on to tell them";
        if (h.Assistant.ProviderStatus is { } ps && ps["state"]?.GetValue<string>() is { } state && state is not ("ready" or "offline" or "checking"))
            return "the assistant isn't on to tell them";
        return null;
    }

    /// <summary>The named things and people within 20 m, nearest first, where from her going that way; never what she
    /// rides.</summary>
    async Task<List<RideRecorder.Thing>> RideAroundAsync(WalkMode.Body b, double compass)
    {
        var seat = new HashSet<UUID>();
        if (b.Linkset is { } set && Host?.Session.Region is { } w)
            foreach (var id in set) if (w.Prims.TryGetValue(id, out var p)) seat.Add(p.ID);
        var me = _ops.Client?.Self.AgentID ?? UUID.Zero;
        var found = new List<(string Name, LibreMetaverse.Vector3 Pos, double Dist)>();
        foreach (var t in await _ops.ObjectsAsync(20, 16))
            if (!seat.Contains(t.Id) && t.Name is not ("object" or "Object" or "Primitive")) found.Add((t.Name, t.Pos, t.Dist));
        foreach (var p in await _ops.PeopleAsync(20))
            if (p.Id != me) found.Add((p.Name, p.Pos, p.Dist));
        return found.OrderBy(x => x.Dist).Take(6)
            .Select(x => new RideRecorder.Thing(x.Name, Bearings.Relative(x.Pos.X - b.Pos.X, x.Pos.Y - b.Pos.Y, x.Pos.Z - b.Pos.Z, compass)))
            .ToList();
    }

    ToolStripMenuItem RideMenu()
    {
        // (no mnemonic: every letter of its words is taken in World)
        var m = new ToolStripMenuItem("Recording a ride") { Name = "menuRide" };
        m.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("&Start or stop", "C in walk mode", () => _ = RideWordsAsync("", key: true)),
            Item("&Tell the ride", "Shift+C in walk mode", () => _ = RideWordsAsync("describe", key: true)),
        });
        return m;
    }

    /// <summary>`//ride`'s words (and the keys', as words): start (or, recording, how it's going); stop; describe (tell);
    /// more. <paramref name="key"/>: C and Shift+C, where C alone starts or stops.</summary>
    async Task RideWordsAsync(string words, bool key)
    {
        if (_rides is not { } rec || Host is not { } h) { Answer("Not in a region yet."); return; }
        var verb = words.Trim().ToLowerInvariant();
        string K(string keyWords, string command) => key ? keyWords : command;
        if (verb == "" && key) verb = rec.On ? "stop" : "start";
        switch (verb)
        {
            case "" or "start" or "status":
            {
                if (rec.Current is { } now)
                {
                    Answer($"Recording the ride: {RideRecorder.Length(now.Ms)}, {Pictures(now)}. {K("C", "//ride stop")} stops it.");
                    return;
                }
                if (verb == "status")
                {
                    Answer(rec.Latest is { } l ? $"Not recording. The last ride: {RideRecorder.Length(l.Ms)}, {Pictures(l)}." : "Not recording, and no ride recorded yet.");
                    return;
                }
                if (!rec.Start(h.Session.Private.On)) { Answer("Not in a region yet."); return; }
                var off = RidePicturesOff();
                var how = off == null
                    ? "Recording the ride: a picture every few seconds and what's around, kept on this PC until you ask."
                      + (h.Offscreen?.Preparing == true ? " The view is still preparing, so pictures start in a minute or so."
                         : h.Offscreen is { Running: false } ? " The view is still starting, so pictures start once it's up." : "")
                    : $"Recording the ride without pictures ({off}): where you go, how fast, and what's around.";
                Answer($"{how} {K("C stops it", "//ride stop stops it")}, or it stops when you stand up after the ride, or the ride stops; "
                       + $"{K("Shift+C", "//ride describe")} then tells it.");
                return;
            }
            case "stop" or "end":
            {
                if (rec.Stop("you stopped recording") is not { } r) { Answer($"Not recording a ride. {K("C", "//ride")} starts one."); return; }
                Answer($"Ride recorded: {RideRecorder.Length(r.Ms)}, {Pictures(r)}. {K("Shift+C", "//ride describe")} tells it.");
                return;
            }
            case "describe" or "tell" or "more":
            {
                if (rec.On && rec.Stop("you stopped recording") is { } stopped)
                    Answer($"Recording stopped: {RideRecorder.Length(stopped.Ms)}.");
                if (rec.Latest is not { } r) { Answer($"No ride recorded yet. {K("C", "//ride")} starts recording one."); return; }
                var s = RideSubject(r);
                // (the first telling is the gist; she didn't know there was more, 0.1.0.835)
                await DescribeAsync(s, verb == "more" ? "rich" : null, more: $"{K("Shift+C again", "//ride more")} tells more of it.");
                return;
            }
            case "help": await _cmd.RunAsync("//help ride"); return;
            default: Answer("//ride, //ride stop, //ride describe, or //ride more."); return;
        }
    }

    static Subject RideSubject(RideRecorder.Ride r) =>
        new($"ride-{r.Id}", "ride", "your ride", $"Tell me about the ride I recorded ({RideRecorder.Length(r.Ms)}) as it happened");
}
