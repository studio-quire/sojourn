using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Access.Walk;

namespace Sojourn.Access;

/// <summary>
/// Recording a route by walking it (M3; docs/access-app.md > Walk mode > Recording a route): R in walk mode or
/// `//record` starts it, and the same words run it from walk mode's keys, the command and World's menu alike (a note, a
/// wait, take back the last, pause, save, discard). Pausing the recording and a wait in the route are told apart in
/// what's said: a pause gone on from where it began becomes a wait as long, said so with how to take it back (the blind
/// tester paused to listen and expected the route to wait, 2026-11-22). The route itself is Walk/RouteRecorder's; here it's asked a name for, saved as a
/// notecard in the inventory's Routes folder (made at the top when there's none) and said, so `//route NAME` plays it at
/// once.
/// </summary>
public sealed partial class MainForm
{
    RouteRecorder? _route;

    void StartRouteRecording(Sojourn.Session.Collision.LocalCollision rays)
    {
        var rec = _route = new RouteRecorder(_walk!, rays);
        _walk!.Recorder = rec;
        _walk.RecordKey = words => _ = RecordWordsAsync(words, key: true);
        _walk.Touched += name => rec.Touched(name);
        _cmd.Record = words => (Task)Invoke(() => RecordWordsAsync(words, key: false));
    }

    ToolStripMenuItem RouteMenu()
    {
        var m = new ToolStripMenuItem("Recordi&ng a route") { Name = "menuRoute" };
        m.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("&Start, or how it's going", "R in walk mode", () => _ = RecordWordsAsync("", key: true)),
            Item("A &note here", "N in walk mode", () => _ = RecordWordsAsync("note", key: true)),
            Item("A &wait here", "Shift+P in walk mode", () => _ = RecordWordsAsync("wait", key: true)),
            Item("Take &back the last point, note or wait", "Backspace in walk mode", () => _ = RecordWordsAsync("undo", key: true)),
            Item("&Pause recording, or go on", "P in walk mode", () => _ = RecordWordsAsync("pause", key: true)),
            Item("Sa&ve it", "Shift+R in walk mode", () => _ = RecordWordsAsync("save", key: true)),
            Item("&Throw it away", "//record discard", () => _ = RecordWordsAsync("discard", key: false)),
        });
        return m;
    }

    /// <summary>`//record`'s words (and the keys', as words): start, or how it's going; note [WORDS]; wait [SECONDS];
    /// undo; pause (or resume); save [NAME]; discard. <paramref name="key"/>: said with walk mode's keys rather than the
    /// commands.</summary>
    async Task RecordWordsAsync(string words, bool key)
    {
        if (_route is not { } rec) { Answer("Not in a region yet."); return; }
        var w = words.Trim();
        int sp = w.IndexOf(' ');
        var verb = (sp < 0 ? w : w[..sp]).ToLowerInvariant();
        var rest = sp < 0 ? "" : w[(sp + 1)..].Trim();
        string K(string keyWords, string command) => key ? keyWords : command;
        var undo = K("Backspace", "//record undo");

        if (verb is "" or "start" or "status")
        {
            if (rec.On) { Answer(RouteState(rec, key)); return; }
            if (!rec.Start()) { Answer("Not in a region yet."); return; }
            Answer("Recording a route from here. Walk it however you go: " + K(
                "R says how it's going, N adds a note, Shift+P a wait, Backspace takes back the last thing kept, P pauses recording, Shift+R saves.",
                "//record says how it's going, //record note WORDS adds a note, //record wait SECONDS a wait, //record undo takes back the last thing kept, //record pause pauses recording, //record save NAME saves."));
            return;
        }
        if (verb is "help") { await _cmd.RunAsync("//help record"); return; }
        if (!rec.On) { Answer($"Not recording a route. {K("R in walk mode", "//record")} starts one."); return; }
        switch (verb)
        {
            case "note":
            {
                var text = key && rest.Length == 0
                    ? Dialogs.Ask(this, "A note in the route", "Note, read out where the route is played (left empty: just a point here)", allowEmpty: true)
                    : rest;
                if (text == null) { Answer("No note."); return; }
                if (text.Length == 0)
                {
                    Answer(rec.PointHere() ? $"A point here: step {rec.CountSteps()}." : "There's a point here already.");
                    return;
                }
                rec.Note(text);
                Answer($"Note kept, after step {rec.CountSteps()}: {text}");
                return;
            }
            case "undo" or "back":
            {
                if (rec.Undo() is not { } gone) { Answer("Nothing to take back: the route is only where it starts."); return; }
                Answer($"Took back {gone}. {Steps(rec.CountSteps())}{LastPointWords(rec)}.");
                return;
            }
            case "wait":
            {
                var given = key && rest.Length == 0
                    ? Dialogs.Ask(this, "A wait in the route", "Seconds the route waits here when it's played", "10")
                    : rest;
                if (given == null) { Answer("No wait."); return; }
                if (WaitSeconds(given) is not { } secs)
                {
                    Answer(given.Length == 0
                        ? "//record wait SECONDS puts a wait in the route here: //record wait 20 waits 20 seconds."
                        : $"How long to wait wasn't clear: {given}. A number of seconds, as 20, or of minutes, as 2 minutes.");
                    return;
                }
                bool paused = rec.Paused;
                int s = rec.Wait(secs);
                if (s == 0) { Answer("Not in a region yet."); return; }
                Answer($"The route waits {RouteRecorder.WaitWords(s)} here{(secs > RouteRecorder.MaxWait ? " (an hour at most)" : "")}"
                    + (paused ? ", and going on from the pause adds no other wait" : "") + $"; {undo} takes it back.");
                return;
            }
            case "pause" or "resume" or "continue":
            {
                if (rec.Paused)
                {
                    var (wait, more) = rec.Resume();
                    Answer(wait > 0 ? $"Recording again. The route waits {RouteRecorder.WaitWords(wait)} here, as long as you paused; {undo} takes it back." : $"Recording again.{more}");
                }
                else if (verb == "pause")
                {
                    rec.Pause();
                    Answer($"Recording paused, {Steps(rec.CountSteps())}. {K("P", "//record resume")} goes on; going on from here, the route waits here as long as you were paused.");
                }
                else Answer("Recording already.");
                return;
            }
            case "discard" or "cancel":
            {
                int n = rec.CountSteps();
                rec.Discard();
                Answer($"Route thrown away ({Steps(n)}); not recording.");
                return;
            }
            case "save" or "stop" or "end" or "finish" or "done":
            {
                bool wasPaused = rec.Paused;
                rec.Finish();
                if (rec.Steps.Count <= 1)
                {
                    if (!wasPaused) rec.Resume();
                    Answer("Nothing to save yet: the route is only where it starts. Still recording.");
                    return;
                }
                var name = rest.Length > 0 ? rest : Dialogs.Ask(this, "Save the route", "Route name", $"{rec.StartRegion} route");
                if (name == null)
                {
                    if (!wasPaused) rec.Resume();
                    Answer($"Not saved; still recording{(wasPaused ? ", paused" : "")}.");
                    return;
                }
                await SaveRouteAsync(rec, name, wasPaused);
                return;
            }
            default:
                // (her own word, for a log: twice on 2026-11-22 she typed one it didn't take, and the log couldn't say which)
                Host?.Log.LogInformation("record: no verb {Verb}", verb);
                Answer($"//record doesn't take {verb}. It takes: note WORDS, wait SECONDS, undo, pause, resume, save NAME, discard; alone, how it's going.");
                return;
        }
    }

    static string Steps(int n) => n == 1 ? "1 step" : $"{n} steps";

    /// <summary>A wait's length as typed: seconds ("20", "20 seconds", "1.5 minutes", "2 min", "1:30"), or null.</summary>
    static double? WaitSeconds(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace(',', '.');
        var mmss = System.Text.RegularExpressions.Regex.Match(t, @"^(\d+):([0-5]?\d)$");
        if (mmss.Success) return int.Parse(mmss.Groups[1].Value) * 60 + int.Parse(mmss.Groups[2].Value);
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^(\d+(?:\.\d+)?)\s*(s|secs?|seconds?|m|mins?|minutes?)?$");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0) return null;
        return m.Groups[2].Value.StartsWith('m') ? n * 60 : n;
    }

    string RouteState(RouteRecorder rec, bool key)
    {
        var (waits, secs) = rec.Waits();
        var waited = waits == 0 ? "" : waits == 1 ? $" (one a wait of {RouteRecorder.WaitWords(secs)})" : $" ({waits} of them waits, {RouteRecorder.WaitWords(secs)} in all)";
        var paused = !rec.Paused ? "" : rec.PausedSecs > 0 ? $", paused for {RouteRecorder.WaitWords(rec.PausedSecs)}" : ", paused";
        return $"Recording{paused}: {Steps(rec.CountSteps())}{waited}, {Bearings.Metres(rec.Walked)} walked{LastPointWords(rec)}. "
            + (key ? "Shift+R saves." : "//record save NAME saves.");
    }

    /// <summary>"; the last point is 3 metres, seven o'clock" (or in which region).</summary>
    string LastPointWords(RouteRecorder rec)
    {
        if (rec.LastPlace() is not { } l || _ops.Client?.Network.CurrentSim is not { } sim) return "";
        if (l.Region != sim.Handle) return $"; the last point is in {l.RegionName}";
        var where = _ops.Where(new Vector3(l.Pos.X, l.Pos.Y, l.Pos.Z));
        return where == null ? "" : $"; the last point is {where}";
    }

    /// <summary>The route as a notecard in Routes (a name taken there already gets a number), said with how to play it.</summary>
    async Task SaveRouteAsync(RouteRecorder rec, string name, bool wasPaused)
    {
        var text = string.Join("\n", rec.Lines());
        int steps = rec.CountSteps();
        var inv = Host!.Session.Inventory;
        try
        {
            var root = inv.FolderFor(FolderType.Root);
            await inv.EnsureLoadedAsync(root, CancellationToken.None);
            var store = _ops.Client!.Inventory.Store;
            var folder = store.GetContents(root).OfType<InventoryFolder>().FirstOrDefault(f => f.Name.Equals("Routes", StringComparison.OrdinalIgnoreCase))?.UUID;
            bool made = false;
            if (folder == null)
            {
                var f = await _ops.DoAsync("TesselInventory", "createFolder", new JsonObject { ["name"] = "Routes", ["parent_id"] = root.ToString() });
                if (PersonOps.Error(f) != null || !UUID.TryParse(f.Str("folder_id"), out var fid)) throw new InvalidOperationException(PersonOps.Error(f) ?? "no folder came back");
                folder = fid;
                made = true;
            }
            await inv.EnsureLoadedAsync(folder.Value, CancellationToken.None);
            var taken = store.GetContents(folder.Value).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var given = name;
            for (int i = 2; taken.Contains(name); i++) name = $"{given} {i}";
            var r = await _ops.DoAsync("TesselInventory", "createNotecard", new JsonObject
            {
                ["name"] = name, ["folder_id"] = folder.Value.ToString(), ["text"] = text,
                ["desc"] = $"A route recorded in Sojourn Access, from {rec.StartRegion}: //route {name} plays it",
            });
            if (PersonOps.Error(r) is { } e) throw new InvalidOperationException(e);
            rec.Discard();
            var renamed = name != given ? $" (there's a {given} already)" : "";
            Answer($"Saved {name}{renamed} in {(made ? "a new folder, Routes, at the top of your inventory" : "Routes")}: {Steps(steps)}. //route {name} plays it.");
            RefreshInventory(root, folder.Value);
        }
        catch (Exception ex)
        {
            if (!wasPaused) rec.Resume();
            Answer($"Couldn't save the route: {ex.Message}. Still recording; Shift+R tries again.");
        }
    }
}
