using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.World;

namespace Sojourn.Core.Agents;

/// <summary>
/// Keeping the person safe and the place bearable (batch B1, docs/viewer-gaps.md > Safety), after Firestorm:
/// <list type="bullet">
/// <item>Derendering and the asset blacklist (Firestorm's fsassetblacklist.cpp, the Derender and Derender Permanently
/// items of its object menu): an object derendered is taken out of the world model as LLViewerObjectList::killObject
/// takes it out of the viewer's, and every later update for it (or a prim linked to it) is dropped as
/// processObjectUpdate drops a blacklisted id, so nothing draws, touches, hears or lists it. Until the next teleport
/// (FSTempDerenderUntilTeleport, on by default) or for good (saved per account, asset-blacklist.json). Sounds are
/// blacklisted by asset, or everything an owner's worn, rezzed or gestured things play (its WORN, REZZED and GESTURE
/// flags); animations by asset, not played on anyone (and stopped on the person, as Firestorm's
/// process_avatar_animation does).</item>
/// <item>Antispam (Safety.Antispam.cs), bumps, pushes and hits (Safety.Bumps.cs), releasing the controls scripts took
/// (the viewer's Release Keys).</item>
/// </list>
/// Avatars aren't derendered here: hideAvatars (TesselAgent) and SojournAvatars render never are the person's tools
/// for people. Incognito: what's added during a spell is never written down, and lists show it only while the spell
/// lasts (and sees), as IM lines are kept (PrivateMode.ShownSpell). Written by Claude Opus 5.5, 2026-10-27.
/// </summary>
public sealed partial class Safety
{
    public enum Kind { Object, Sound, Animation, OwnerSounds }

    /// <summary>Which of an owner's sounds are blocked (Firestorm's eBlacklistFlag): what they wear, what they've rezzed,
    /// their gestures'.</summary>
    [Flags] public enum From { None = 0, Worn = 1, Rezzed = 2, Gestures = 4, All = 7 }

    public sealed record Entry(UUID Id, Kind Kind, string Name, string Region, double At, bool Permanent, From Sounds = From.None, int Spell = 0);

    readonly AgentSession _s;
    readonly ConcurrentDictionary<UUID, Entry> _list = new();
    // what was taken out of each region's world: local id -> (its own id, the derendered root's), so a re-render can ask
    // the region for every prim of it again
    readonly ConcurrentDictionary<ulong, ConcurrentDictionary<uint, (UUID Id, UUID Root)>> _gone = new();
    readonly HashSet<RegionWorld> _watched = new();
    volatile int _objects;   // how many objects are on the list (the world hook's fast path)
    bool _loaded;

    public Safety(AgentSession s)
    {
        _s = s;
        s.World.RegionChanged += (w, created) =>
        {
            if (created) Watch(w);
            else { _gone.TryRemove(w.Handle, out _); lock (_watched) _watched.Remove(w); w.Changed -= OnWorld; }
        };
        foreach (var w in s.World.Regions) Watch(w);
        s.Private.Changed += (on, _, _) => { if (!on) ForgetSpell(); };
    }

    string FilePath => Path.Combine(_s.StateDir, "asset-blacklist.json");

    /// <summary>Called once per login's GridClient (AgentSession.WireEvents).</summary>
    public void Wire(GridClient c)
    {
        Load();
        c.Self.TeleportProgress += (_, e) => _s.Guard("safety teleport", () =>
        {
            if (e.Status == TeleportStatus.Finished) AfterTeleport();
        });
        c.Avatars.AvatarAnimation += (_, e) => _s.Guard("safety animations", () => OnAnimations(c, e));
        c.Network.SimChanged += (_, _) => _s.Guard("safety region", () => ClearSpamBlocks(regionChange: true));
        c.Self.MeanCollision += (_, e) => _s.Guard("safety bump", () => OnBump(c, e));
        // a derendered prim the region deletes: its local id may come to name something else
        c.Objects.KillObject += (_, e) => { if (_gone.TryGetValue(e.Simulator.Handle, out var g)) g.TryRemove(e.ObjectLocalID, out (UUID, UUID) _); };
    }

    // --- the list ------------------------------------------------------------------------------------------------

    /// <summary>Everything on the list that callers may be shown now (not what a past spell of incognito added).</summary>
    public IReadOnlyList<Entry> Entries
    {
        get { Load(); return _list.Values.Where(e => _s.Private.ShownSpell(e.Spell)).OrderBy(e => e.At).ToList(); }
    }

    /// <summary>How many objects are derendered now, those from a spell included (the menu's "show again" row).</summary>
    public int Derendered => _objects;

    public bool IsBlacklisted(UUID id, Kind kind) => _list.TryGetValue(id, out var e) && e.Kind == kind;

    Entry Add(UUID id, Kind kind, string name, string region, bool permanent, From sounds = From.None)
    {
        Load();
        int spell = _s.Private.Spell;
        // a spell keeps nothing: what it adds lasts until the next teleport at most, and is never written down
        var e = new Entry(id, kind, name, region, Json.UnixNow(), permanent && spell == 0, sounds, spell);
        _list.TryGetValue(id, out var had);
        if (had is { Kind: Kind.OwnerSounds } && kind == Kind.OwnerSounds)
            e = e with { Sounds = had.Sounds | sounds, Permanent = had.Permanent || e.Permanent };
        _list[id] = e;
        if (kind == Kind.Object && had?.Kind != Kind.Object) Interlocked.Increment(ref _objects);
        else if (kind != Kind.Object && had?.Kind == Kind.Object) Interlocked.Decrement(ref _objects);
        if (e.Permanent || had?.Permanent == true) Save();
        return e;
    }

    /// <summary>Take an entry off the list (any kind); an object comes back (asked of its region again).</summary>
    public Entry? Remove(UUID id)
    {
        Load();
        if (!_list.TryRemove(id, out var e)) return null;
        if (e.Kind == Kind.Object) { Interlocked.Decrement(ref _objects); AskAgain(id); }
        if (e.Permanent) Save();
        return e;
    }

    /// <summary>Every object derendered comes back (the list's sounds and animations stay).</summary>
    public int RerenderAll()
    {
        int n = 0;
        foreach (var e in _list.Values.Where(e => e.Kind == Kind.Object).ToList())
            if (Remove(e.Id) != null) n++;
        return n;
    }

    /// <summary>A teleport ends what was derendered "until teleport" (FSTempDerenderUntilTeleport): it's let back in.</summary>
    void AfterTeleport()
    {
        foreach (var e in _list.Values.Where(e => e.Kind == Kind.Object && !e.Permanent).ToList()) Remove(e.Id);
    }

    void ForgetSpell()
    {
        // what a spell blacklisted stays in force for now (it's protection), but its names go: the next teleport ends it
        foreach (var e in _list.Values.Where(e => e.Spell != 0).ToList())
            _list[e.Id] = e with { Name = "", Region = "" };
        ForgetSpellRecords();
    }

    void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var n in JsonNode.Parse(File.ReadAllText(FilePath)) as JsonArray ?? new JsonArray())
            {
                if (n is not JsonObject o || o.Uuid("id") is not { } id || !Enum.TryParse<Kind>(o.Str("kind") ?? "", true, out var kind)) continue;
                Enum.TryParse<From>(o.Str("sounds") ?? "None", true, out var from);
                _list[id] = new Entry(id, kind, o.Str("name") ?? "", o.Str("region") ?? "", o.Num("at", Json.UnixNow()), true, from);
                if (kind == Kind.Object) Interlocked.Increment(ref _objects);
            }
        }
        catch (Exception x) { _s.Log.LogWarning(x, "the asset blacklist"); }
    }

    void Save()
    {
        try
        {
            var arr = new JsonArray();
            foreach (var e in _list.Values.Where(e => e.Permanent).OrderBy(e => e.At))
                arr.Add(new JsonObject
                {
                    ["id"] = e.Id.ToString(), ["kind"] = e.Kind.ToString(), ["name"] = e.Name, ["region"] = e.Region, ["at"] = e.At,
                    ["sounds"] = e.Sounds.ToString(),
                });
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception x) { _s.Log.LogWarning(x, "saving the asset blacklist"); }
    }

    // --- derendering objects ---------------------------------------------------------------------------------------

    /// <summary>Derender an object (a linkset by any of its prims, or something someone wears): it's taken out of the world
    /// now and kept out. Standing up first if the person sits on it, as Firestorm does. Returns the root that went, or a
    /// reason it can't.</summary>
    public (Entry? Done, string? Why) Derender(UUID id, bool permanent)
    {
        if (_s.Client is not { } c) return (null, "not logged in");
        if (_s.Objects.FindAnywhere(id) is not { } found)
        {
            if (_s.VisibleRegions().Any(w => w.AvatarById(id) != null))
                return (null, "that's a person: hideAvatars (TesselAgent) leaves people out of the view, SojournAvatars render never draws them as a grey shape");
            return (null, "no object with that id is around");
        }
        var (w, prim) = found;
        var root = _s.Objects.RootOf(w, prim);
        if (root.ID == c.Self.AgentID) return (null, "that's the person's own avatar");
        // seated on it: stand up first, or the person would sit on nothing
        if (c.Self.SittingOn != 0 && w.Prims.TryGetValue(c.Self.SittingOn, out var seat) && (seat.LocalID == root.LocalID || seat.ParentID == root.LocalID))
            c.Self.Stand();
        string name = root.Properties?.Name is { Length: > 0 } n ? n : prim.Properties?.Name ?? "";
        var e = Add(root.ID, Kind.Object, name, w.Name, permanent);
        Hide(w, root, root.ID);
        return (e, null);
    }

    void Watch(RegionWorld w)
    {
        lock (_watched)
            if (!_watched.Add(w)) return;
        w.Changed += OnWorld;
    }

    /// <summary>The world model changed (a network thread): an object on the list, or a prim linked to or worn on one,
    /// that arrives or is updated is taken out again.</summary>
    void OnWorld(RegionWorld w, WorldChange change, uint local)
    {
        if (change is not (WorldChange.Added or WorldChange.Updated or WorldChange.Moved)) return;
        if (_objects == 0 && _gone.IsEmpty) return;
        if (!w.Prims.TryGetValue(local, out var p)) return;
        if (_list.TryGetValue(p.ID, out var e) && e.Kind == Kind.Object) { Hide(w, p, p.ID); return; }
        if (p.ParentID != 0 && _gone.TryGetValue(w.Handle, out var gone) && gone.TryGetValue(p.ParentID, out var parent)
            && _list.TryGetValue(parent.Root, out var pe) && pe.Kind == Kind.Object)
            Hide(w, p, parent.Root);
    }

    /// <summary>Out of the region's world with every prim linked to it (and what's linked to those), as a kill would take
    /// them: the renderer, the pointer, sounds, floating text and media lose them together.</summary>
    void Hide(RegionWorld w, Primitive p, UUID root)
    {
        var gone = _gone.GetOrAdd(w.Handle, _ => new ConcurrentDictionary<uint, (UUID, UUID)>());
        var todo = new Stack<Primitive>();
        todo.Push(p);
        while (todo.Count > 0)
        {
            var q = todo.Pop();
            gone[q.LocalID] = (q.ID, root);
            if (w.Prims.TryRemove(q.LocalID, out _)) w.Raise(WorldChange.Removed, q.LocalID);
            foreach (var (_, child) in w.Prims)
                if (child.ParentID == q.LocalID) todo.Push(child);
        }
    }

    /// <summary>A re-render: ask each region for the prims taken out of it again (RequestMultipleObjects), so the object
    /// comes back now rather than at its next change.</summary>
    void AskAgain(UUID root)
    {
        foreach (var (handle, gone) in _gone)
        {
            var locals = gone.Where(kv => kv.Value.Root == root).Select(kv => kv.Key).ToList();
            foreach (var l in locals) gone.TryRemove(l, out _);
            if (locals.Count == 0 || _s.World.Region(_s.GridKey, handle)?.Simulator is not { Connected: true } sim) continue;
            try { sim.Client.Objects.RequestObjects(sim, locals); }
            catch (Exception x) { _s.Log.LogDebug(x, "asking for re-rendered objects"); }
        }
    }

    // --- sounds and animations ------------------------------------------------------------------------------------

    /// <summary>Blacklist a sound by its asset, or everything an owner's things play (from: which of them).</summary>
    public Entry BlockSound(UUID sound, string name) => Add(sound, Kind.Sound, name, _s.Region?.Name ?? "", permanent: true);

    public Entry BlockOwnerSounds(UUID owner, string name, From from) =>
        Add(owner, Kind.OwnerSounds, name, _s.Region?.Name ?? "", permanent: true, sounds: from == From.None ? From.All : from);

    public Entry BlockAnimation(UUID anim, string name)
    {
        var e = Add(anim, Kind.Animation, name, _s.Region?.Name ?? "", permanent: true);
        // playing on the person now: stopped (others see it stop too)
        if (_s.Client is { } c && c.Self.SignaledAnimations.ContainsKey(anim)) c.Self.AnimationStop(anim, true);
        return e;
    }

    /// <summary>Whether a sound is blocked (Firestorm's is_sound_blacklisted): its asset, or its owner's worn, rezzed or
    /// gesture sounds as that owner's entry says. gesture: an avatar's own sound (the object is its owner).</summary>
    public bool SoundBlocked(UUID sound, UUID obj, UUID owner, bool worn)
    {
        if (_list.IsEmpty) return false;
        if (_list.TryGetValue(sound, out var e) && e.Kind == Kind.Sound) return true;
        if (owner == UUID.Zero || !_list.TryGetValue(owner, out var o) || o.Kind != Kind.OwnerSounds) return false;
        var from = obj == owner ? From.Gestures : worn ? From.Worn : From.Rezzed;
        return (o.Sounds & from) != 0;
    }

    /// <summary>An avatar's animations as the region lists them: the blacklisted ones are left out of the list the
    /// renderer plays (a new list, as LibreMetaverse replaces it), and stopped on the person.</summary>
    void OnAnimations(GridClient c, AvatarAnimationEventArgs e)
    {
        if (_list.IsEmpty || !e.Animations.Any(a => IsBlacklisted(a.AnimationID, Kind.Animation))) return;
        var keep = e.Animations.Where(a => !IsBlacklisted(a.AnimationID, Kind.Animation)).ToList();
        foreach (var w in _s.VisibleRegions())
            foreach (var av in w.Avatars.Values)
                if (av.ID == e.AvatarID) lock (av) av.Animations = keep;
        if (e.AvatarID == c.Self.AgentID)
            foreach (var a in e.Animations.Where(a => IsBlacklisted(a.AnimationID, Kind.Animation)))
                c.Self.AnimationStop(a.AnimationID, true);
    }

    // --- release keys ----------------------------------------------------------------------------------------------

    /// <summary>The viewer's Release Keys (LLAgent::forceReleaseControls): the region takes back every control scripts
    /// have taken (it answers with their releases). Returns the controls held before.</summary>
    public ScriptControlChange ReleaseControls()
    {
        var held = _s.ControlsTaken;
        if (_s.Client is not { Network.Connected: true } c) return held;
        c.Network.SendPacket(new LibreMetaverse.Packets.ForceScriptControlReleasePacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
        });
        _s.Events.Log("TesselEvents", new JsonObject { ["event"] = "controls_released", ["held"] = (long)(uint)held });
        return held;
    }
}
