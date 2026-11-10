using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// Pointing at something herself (docs/access-app.md > Pointing at something yourself; Radegast's Point At): her avatar's
/// arm points at a thing, with a beam from her hand to it, so a sighted friend nearby sees which "Object" she means without
/// logging in as her. Point at on Objects' and Parts' menus (Stop pointing on the one she points at), Control+P in either
/// list, `//point NAME`, `//point off`. A minute at a time; Core's Pointing sends it and ends it when she moves away or the
/// thing goes. Only her own press starts it, as with answers to confirmations: a program's keys or a UI Automation invoke
/// can't point her arm, and nothing on the agent API does. Claude Opus 5.5, 2026-11-09.
/// </summary>
public sealed partial class MainForm
{
    static readonly TimeSpan PointFor = TimeSpan.FromMinutes(1);

    /// <summary>What she points at: the prim (a root or a part) and its words as said ("Tip Jar", "Latch, part 3 of
    /// Treasure Chest").</summary>
    (UUID Target, string Words)? _pointingAt;

    void StartPointAt()
    {
        Host!.Session.Pointing.MineEnded += (target, why) => BeginInvoke(() => PointEnded(target, why));
        _cmd.PointAt = words => (Task)Invoke(() => PointWordsAsync(words));
    }

    bool PointingAt(UUID target) => target != UUID.Zero && Host?.Session.Pointing.Mine == target;

    /// <summary>A part as said when pointed at: the root by its object's name, a named part with its number and object.</summary>
    static string PartPointWords(Linkset.Part p) =>
        p.Number == 1 ? p.RootName : p.Named ? $"{p.Name}, part {p.Number} of {p.RootName}" : $"part {p.Number} of {p.RootName}";

    /// <summary>Point at or Stop pointing on Objects' menu, or Control+P there: run within the press, which is judged here.</summary>
    void PointThing(PersonOps.Thing t)
    {
        if (t.Hud || t.WornBy != UUID.Zero) { Answer("Pointing is at things out in the world, not at what's worn."); return; }
        PointPressed(t.Id, t.Name, t.Pos, RealInput.Person, toggle: true);
    }

    void PointPart(Linkset.Part p)
    {
        if (p.Hud) { Answer("Pointing is at things out in the world, not at what's worn."); return; }
        PointPressed(p.Id, PartPointWords(p), L(p.Pos), RealInput.Person, toggle: true);
    }

    /// <summary>Pointing at it (on the thing she points at already, for another minute, or with toggle, stopping). Starting
    /// takes her own press (a key on the keyboard, or one her screen reader passed on; with answers without a key set to
    /// Windows Hello, Hello asks): stopping takes any.</summary>
    void PointPressed(UUID target, string words, LibreMetaverse.Vector3 pos, bool hers, bool toggle)
    {
        if (toggle && PointingAt(target)) { StopPointing(); return; }
        if (hers) { _ = PointAtAsync(target, words, pos); return; }
        Hello.Instead(_set.ConfirmByHello, Handle, $"point at {words}", RealInput.Why, t => Reader?.Answer(t, keep: false),
            () => _ = PointAtAsync(target, words, pos));
    }

    async Task PointAtAsync(UUID target, string words, LibreMetaverse.Vector3 pos)
    {
        if (Host?.Session is not { } s) return;
        bool again = PointingAt(target);
        // facing it when it's well to a side or behind, as the official viewer turns its avatar toward what it selects
        // (lltoolselect.cpp, past about 53 degrees): the arm that points, the left, reaches only ahead and to the left.
        // Not seated, where the seat has the say.
        bool turned = false;
        if (!again && _ops.Me() is { } me && _ops.Client is { Self.SittingOn: 0 }
            && Math.Abs(pos.X - me.Pos.X) + Math.Abs(pos.Y - me.Pos.Y) > 0.5)
        {
            double bearing = Bearings.Compass(me.Pos.X, me.Pos.Y, pos.X, pos.Y);
            if (Math.Abs(Bearings.Norm(bearing - me.Heading + 180) - 180) > 53)
                turned = PersonOps.Error(await _ops.DoAsync("TesselAgent", "move", new JsonObject { ["face"] = bearing, ["secs"] = 0.3, ["wait"] = true })) is null;
        }
        if (!s.Pointing.PointAt(target, PointFor)) { Answer("Not in a region yet."); return; }
        _pointingAt = (target, words);
        Host.Log.LogInformation("pointing: she points at {Target}{Turned}", target, turned ? ", turned to face it" : "");
        Answer(again ? $"Still pointing at {words}, for another minute."
            : turned ? $"Turned to face {words}, pointing at it for a minute: people nearby see your arm and a beam on it."
            : $"Pointing at {words} for a minute: people nearby see your arm and a beam on it.");
    }

    void StopPointing()
    {
        var words = _pointingAt?.Words;
        _pointingAt = null;
        Answer(Host?.Session.Pointing.StopPointing() == true ? $"Stopped pointing at {words ?? "it"}." : "You weren't pointing at anything.");
    }

    /// <summary>It ended by itself: said in turn, as what arrives is.</summary>
    void PointEnded(UUID target, Pointing.Ended why)
    {
        if (IsDisposed) return;
        var words = _pointingAt is { } p && p.Target == target ? p.Words : "it";
        if (_pointingAt?.Target == target) _pointingAt = null;
        var line = why switch
        {
            Pointing.Ended.Time => $"Stopped pointing at {words} after a minute.",
            Pointing.Ended.Moved => $"Stopped pointing at {words}: you moved away.",
            _ => $"Stopped pointing at {words}: it isn't here any more.",
        };
        Host?.Log.LogInformation("pointing: she stopped pointing at {Target} ({Why})", target, why);
        if (_set.MuteAll) AppendChat(line);
        else Announce(line);
    }

    /// <summary>`//point NAME` (the nearest thing with that name within 30 metres, else a named part within 10),
    /// `//point off`, `//point` (what she points at). Typed by her: a line a program typed and sent doesn't point.</summary>
    async Task PointWordsAsync(string words)
    {
        var w = words.Trim();
        bool hers = RealInput.Acting;
        if (w.Equals("off", StringComparison.OrdinalIgnoreCase) || w.Equals("stop", StringComparison.OrdinalIgnoreCase)) { StopPointing(); return; }
        if (w.Length == 0)
        {
            Answer(_pointingAt is { } p && PointingAt(p.Target) ? $"Pointing at {p.Words}. //point off stops." : "Not pointing at anything. //point NAME points at something nearby by its name.");
            return;
        }
        if (Named(await _ops.ObjectsAsync(30, 60, w), w) is { } t) PointPressed(t.Id, t.Name, t.Pos, hers, toggle: false);
        else if (await FindPartAsync(w) is { Hud: false } part) PointPressed(part.Id, PartPointWords(part), L(part.Pos), hers, toggle: false);
        else Answer($"Nothing called {w} within 30 metres.{(await _ops.FindPersonAsync(w) is { } who ? $" To hear what {who.Name} points at: //pointing {w}." : "")}");
    }
}
