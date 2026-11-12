using System.Collections.Concurrent;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// What a friend points at (docs/access-app.md > What a friend points at; Radegast's Master Controls). Among dozens of
/// things called "Object", what a sighted friend nearby has right-clicked in their own viewer is how they help without
/// logging in as her. Their viewer sends what it selects as a viewer effect (Core's Agents/Pointing.cs); for the people she
/// chose (a person's menu, or //pointing NAME), the thing is said, Control+Shift+P goes to it in Objects (its part in Parts),
/// and the lists say which it is ("pointed at by Kestrel"). Said and waiting: nothing moves her focus but her key. Whose
/// pointing she hears lasts the session. Claude Opus 5.5, 2026-11-08.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Whose pointing she hears, by id, with their name as said (the network's thread reads it).</summary>
    readonly ConcurrentDictionary<UUID, string> _pointers = new();

    /// <summary>The last thing one of them pointed at: whose, the object (its root), the part when it was one ("Latch",
    /// "part 2"), its words.</summary>
    sealed record PointedThing(UUID Who, string WhoName, UUID Root, UUID Part, string PartLabel, string Words, DateTime At);
    PointedThing? _pointed;
    int _pointSerial;
    /// <summary>When Control+Shift+P was pressed: the objects list, once filled, lands on what was pointed at (a fill that
    /// never came, out of world, doesn't move the focus minutes later).</summary>
    long _landOnPointedAt = long.MinValue / 2;

    void StartPointing()
    {
        Host!.Session.Pointing.Changed += p =>
        {
            if (_pointers.ContainsKey(p.Who)) BeginInvoke(() => _ = PointedAsync(p, late: false));
        };
        _cmd.Pointing = words => (Task)Invoke(() => PointingWordsAsync(words));
    }

    bool HearingPointing(UUID id) => _pointers.ContainsKey(id);

    /// <summary>A person's menu: hear what they point at, or stop.</summary>
    void TogglePointing(UUID id, string name)
    {
        if (_pointers.TryRemove(id, out _))
        {
            if (_pointed?.Who == id) _pointed = null;
            Answer($"No longer hearing what {name} points at.");
            return;
        }
        HearPointing(id, name);
    }

    void HearPointing(UUID id, string name)
    {
        _pointers[id] = name;
        Answer($"Hearing what {name} points at: when they right-click something in their viewer, you'll hear it, and Control+Shift+P goes to it.");
        // "I'm pointing at it now" may come before she's turned it on
        if (Host?.Session.Pointing.Last(id) is { } last && DateTime.UtcNow - last.At < TimeSpan.FromMinutes(1)) _ = PointedAsync(last, late: true);
    }

    /// <summary>`//pointing NAME` (someone nearby), `//pointing off`, `//pointing` (whose, and what they last pointed at).</summary>
    async Task PointingWordsAsync(string words)
    {
        var w = words.Trim();
        if (w.Equals("off", StringComparison.OrdinalIgnoreCase) || w.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            var had = _pointers.Values.ToList();
            _pointers.Clear();
            _pointed = null;
            Answer(had.Count == 0 ? "You weren't hearing anyone's pointing." : $"No longer hearing what {AndList(had)} {(had.Count == 1 ? "points" : "point")} at.");
            return;
        }
        if (w.Length == 0)
        {
            var whose = _pointers.Values.ToList();
            if (whose.Count == 0) { Answer("You're not hearing anyone's pointing. //pointing NAME, or Hear what they point at on a person's menu."); return; }
            var last = _pointed is { } p ? $" Last: {p.WhoName} pointed at {p.Words}, {Ago(p.At)}. Control+Shift+P goes to it." : " Nothing pointed at yet.";
            Answer($"Hearing what {AndList(whose)} {(whose.Count == 1 ? "points" : "point")} at.{last}");
            return;
        }
        if (await _ops.FindPersonAsync(w) is not { } who) { Answer($"Nobody called {w} nearby."); return; }
        if (HearingPointing(who.Id)) { Answer($"Already hearing what {who.Name} points at. //pointing off stops it."); return; }
        HearPointing(who.Id, who.Name);
    }

    static string Ago(DateTime at) => (DateTime.UtcNow - at).TotalSeconds switch
    {
        < 60 => "just now",
        < 120 => "a minute ago",
        var s and < 3600 => $"{(int)(s / 60)} minutes ago",
        _ => "over an hour ago",
    };

    /// <summary>One of hers pointed at something: said once its words are known, the last of a quick run of them (a build's
    /// beams, a few right clicks in a row) alone.</summary>
    async Task PointedAsync(Pointing.Pointed p, bool late)
    {
        int serial = ++_pointSerial;
        if (!late) await Task.Delay(400);
        if (serial != _pointSerial || !_pointers.TryGetValue(p.Who, out var whoName)) return;
        var thing = await PointedWordsAsync(p.Target);
        if (serial != _pointSerial || IsDisposed) return;
        string line;
        if (thing is not { } t)
        {
            line = $"{whoName} pointed at something that isn't in view here.";
            Host?.Log.LogInformation("pointing: {Who} pointed at {Target}, not in view", whoName, p.Target);
        }
        else
        {
            _pointed = new PointedThing(p.Who, whoName, t.Root, t.Part, t.PartLabel, t.Words, p.At);
            var where = _ops.Where(t.Pos) is { } wh ? $", {wh}" : "";
            line = $"{whoName} {(late ? "pointed" : "points")} at {t.Words}{where}. Control+Shift+P goes to it.";
            Host?.Log.LogInformation("pointing: {Who} pointed at {Target} ({How}): {Words}", whoName, p.Target, p.How, t.Words);
            // the list beside her shows which it is, unless she's in it
            if (_areas.SelectedTab == _objectsPage && !_objects.Focused && !_parts.Focused && !_faces.Focused) _ = RefreshObjectsAsync(false, behind: true);
        }
        if (_set.MuteAll) AppendChat(line);
        else Announce(line);
    }

    /// <summary>What was pointed at, in the lists' words: an object by name, a part as "Latch, part 3 of Treasure Chest" or
    /// "part 2 of Treasure Chest"; null when it isn't in view.</summary>
    async Task<(UUID Root, UUID Part, string PartLabel, string Words, LibreMetaverse.Vector3 Pos)?> PointedWordsAsync(UUID target)
    {
        if (Host?.Session is not { } s) return null;
        var hit = s.VisibleRegions().Select(w => (W: w, P: w.PrimById(target))).FirstOrDefault(x => x.P != null);
        if (hit.P is not { } prim) return null;
        var root = s.Objects.RootOf(hit.W, prim);
        if (root.LocalID == prim.LocalID)
            return await _ops.ThingAsync(root.ID) is { } t ? (root.ID, UUID.Zero, "", t.Name, t.Pos) : null;
        var parts = await PartsOfAsync(root.ID, false);
        if (parts?.FirstOrDefault(x => x.Id == target) is not { } part)
            return await _ops.ThingAsync(root.ID) is { } whole ? (root.ID, target, "a part", $"a part of {whole.Name}", whole.Pos) : null;
        var label = part.Named ? part.Name : $"part {part.Number}";
        var words = part.Named ? $"{part.Name}, part {part.Number} of {part.RootName}" : $"part {part.Number} of {part.RootName}";
        return (root.ID, target, label, words, L(part.Pos));
    }

    /// <summary>An object's line in Objects: ", pointed at by Kestrel", with the part when it was one.</summary>
    string PointedMark(UUID root) => _pointed is { } p && p.Root == root
        ? p.Part == UUID.Zero ? $", pointed at by {p.WhoName}" : $", pointed at by {p.WhoName} ({p.PartLabel})" : "";

    /// <summary>A part's line in Parts: the part pointed at, or the root when the whole was.</summary>
    string PointedPartMark(UUID part) => _pointed is { } p && (p.Part == part || p.Part == UUID.Zero && p.Root == part) ? $", pointed at by {p.WhoName}" : "";

    /// <summary>Things nearby with no filter on: what was pointed at among them even past the page or the range (by its own
    /// lookup).</summary>
    async Task<List<PersonOps.Thing>> WithPointedAsync(List<PersonOps.Thing> things)
    {
        if (_objectsShow.SelectedIndex != 0 || ObjectsFilter != null) return things;
        if (_pointed is not { } p || things.Any(t => t.Id == p.Root) || await _ops.ThingAsync(p.Root) is not { } pointed) return things;
        var list = things.ToList();
        int at = list.FindIndex(t => t.Dist > pointed.Dist);
        list.Insert(at < 0 ? list.Count : at, pointed);
        return list;
    }

    /// <summary>Control+Shift+P: Objects on things nearby, what was pointed at selected, its part selected in Parts when it
    /// was a part, the focus there; the filter cleared, as what it hides may be the very thing.</summary>
    void GoPointed()
    {
        if (_pointed is null)
        {
            var whose = _pointers.Values.ToList();
            Answer(whose.Count == 0 ? "You're not hearing anyone's pointing: on a person's menu, Hear what they point at, or //pointing NAME."
                : $"{AndList(whose)} {(whose.Count == 1 ? "hasn't" : "haven't")} pointed at anything yet.");
            return;
        }
        if (ObjectsFilter != null)
        {
            _objectsFilterBox.Clear();
            _objectsFilterTimer.Stop();
            _objectsPages = 0;
        }
        // (the focus moves once the list holds it, so a stale line isn't read first)
        _landOnPointedAt = Environment.TickCount64;
        if (_objectsShow.SelectedIndex != 0) _objectsShow.SelectedIndex = 0;   // (refreshes)
        else _ = RefreshObjects(false);
    }

    /// <summary>After the objects list is filled, with no other refresh waiting: on what was pointed at, if Control+Shift+P
    /// asked, the focus there; on its part in Parts when it was a part.</summary>
    async Task LandOnPointedAsync()
    {
        if (Environment.TickCount64 - _landOnPointedAt > 15_000 || _pointed is not { } p) return;
        _landOnPointedAt = long.MinValue / 2;
        if (_objects.Items[p.Root.ToString()] is not { Tag: PersonOps.Thing thing } item)
        {
            Answer($"{p.Words} isn't here any more.");
            return;
        }
        // the area first: a list never shown has no window yet, and the item made current then was lost when it got one
        // (the first Control+Shift+P of a session landed on the list's first line). Its refresh asked for meanwhile is
        // dropped: the list was filled just now (this runs within RefreshObjects, before any await), and filling the list
        // she's in would read the line twice.
        if (_areas.SelectedTab != _objectsPage)
        {
            _areas.SelectedTab = _objectsPage;
            _objectsAgain = null;
        }
        item.Selected = true;
        ReaderList.MakeCurrent(item);
        item.EnsureVisible();
        _objects.Focus();
        FocusChanged();
        if (p.Part == UUID.Zero) return;
        // a part: on it in Parts, once they're listed (the object's line, read first, names the part)
        await RefreshPartsAsync(thing);
        if (_parts.Items[p.Part.ToString()] is not { } part || !_objects.Focused) return;
        part.Selected = true;
        ReaderList.MakeCurrent(part);
        part.EnsureVisible();
        _parts.Focus();
        FocusChanged();
    }
}
