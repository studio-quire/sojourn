using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Core.Leap;
using Sojourn.Core.World;

namespace Sojourn.Core.Agents;

/// <summary>
/// The animations playing on my own avatar, as the region last listed them (AvatarAnimation, with the object that
/// started each), and stopping them as the viewer's "Stop animating me" does (LLAgent::stopCurrentAnimations): every
/// one stopped but do-not-disturb and the ground sit, the region's animation state reset, the animation permissions
/// of the region's objects revoked (so a dance machine can't start it again), standing up if that leaves a seat
/// without its permission, and the default stand asked for again. Written by Claude Opus 5.5, 2026-10-26, after a
/// dance machine's dance kept playing after a teleport home and the assistant had no way to see or stop it.
/// Also says whether something around me is animating me (<see cref="AnimatedByThings"/>), for the headset's arms.
/// </summary>
public sealed class SelfAnimations
{
    readonly AgentSession _s;
    readonly ConcurrentDictionary<UUID, DateTime> _since = new();
    /// <summary>Started through the agent API (playAnimation), with the name it was asked for by.</summary>
    readonly ConcurrentDictionary<UUID, string> _mine = new();

    public SelfAnimations(AgentSession s) => _s = s;

    public readonly record struct Playing(UUID Id, UUID Source, bool BuiltIn);

    // --- a one-shot that has played through is stopped, as the viewer stops it ---------------------------------------
    // LLMotionController sends the region a stop for a non-looping motion on your own avatar when it has played
    // (requestStopMotion at duration less ease-out, or at once for a zero-length one), since the region keeps
    // listing an animation until someone stops it. Without that a bow, a gesture's or a script's one-shot stayed
    // listed, and the same one asked for again changed nothing anywhere: nobody saw a second bow.

    readonly ConcurrentDictionary<UUID, int> _sequence = new();
    readonly ConcurrentDictionary<UUID, Task<float?>> _stopAfter = new();

    public void Wire(GridClient c)
    {
        _sequence.Clear();
        _s.Ao.Wire(c);
        c.Avatars.AvatarAnimation += (_, e) => _s.Guard("selfanims", () =>
        {
            if (e.AvatarID != c.Self.AgentID) return;
            // the AO overrides the region's default motions as they start and stop (Agents/AoDriver.cs)
            _s.Guard("ao", () => _s.Ao.OnSelfAnimations(c, e.Animations));
            var listed = new HashSet<UUID>();
            foreach (var a in e.Animations)
            {
                listed.Add(a.AnimationID);
                if (_sequence.TryGetValue(a.AnimationID, out var seq) && seq == a.AnimationSequence) continue;
                _sequence[a.AnimationID] = a.AnimationSequence;
                _ = StopWhenPlayedAsync(c, a.AnimationID, a.AnimationSequence);
            }
            foreach (var id in _sequence.Keys) if (!listed.Contains(id)) _sequence.TryRemove(id, out int _);
        });
    }

    async Task StopWhenPlayedAsync(GridClient c, UUID id, int sequence)
    {
        var started = DateTime.UtcNow;
        var after = await _stopAfter.GetOrAdd(id, OneShotLengthAsync).ConfigureAwait(false);
        if (after is not { } seconds) { _stopAfter.TryRemove(id, out Task<float?>? _); return; }
        var wait = TimeSpan.FromSeconds(seconds) - (DateTime.UtcNow - started);
        if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
        // still the same activation (not stopped, nor started again since)
        if (_s.Client != c || !_sequence.TryGetValue(id, out var now) || now != sequence) return;
        c.Self.AnimationStop(id, true);
    }

    /// <summary>For a non-looping keyframe animation, seconds from its start to the viewer's stop; null for a looping
    /// one, or one that can't be read (built-in procedural motions have no asset).</summary>
    async Task<float?> OneShotLengthAsync(UUID id)
    {
        try
        {
            var asset = await _s.World.AssetAsync(_s.GridKey, id, AssetType.Animation).ConfigureAwait(false);
            if (asset?.AssetData is not { Length: > 0 } data) return null;
            var anim = Sojourn.Core.Media.AnimFile.Parse(data);
            if (anim.Loop) return null;
            return anim.Duration == 0 ? 0 : Math.Max(anim.Duration - anim.EaseOut, 0);
        }
        catch (Exception) { return null; }
    }

    // LLAgent::stopCurrentAnimations keeps these: viewers judge ground sitting by the animation
    static readonly UUID GroundSit = new("1a2bd58e-87ff-0df8-0b4c-53e047b0bb6e");   // ANIM_AGENT_SIT_GROUND_CONSTRAINED
    static readonly UUID DoNotDisturb = Animations.BUSY;
    // SCRIPT_PERMISSION_TRIGGER_ANIMATION and SCRIPT_PERMISSION_OVERRIDE_ANIMATIONS
    internal const uint AnimationPermissions = 0x10 | 0x8000;

    /// <summary>The animations playing on me now (empty before the region has said).</summary>
    public List<Playing> Now()
    {
        var c = _s.Client;
        var w = _s.Region;
        if (c is null || w is null) return new();
        var av = w.AvatarById(c.Self.AgentID);
        List<Animation> list;
        if (av?.Animations is { } anims) lock (av) list = anims.ToList();
        else list = c.Self.SignaledAnimations.Keys.Select(k => new Animation { AnimationID = k }).ToList();
        var now = DateTime.UtcNow;
        foreach (var k in _since.Keys) if (list.All(a => a.AnimationID != k)) _since.TryRemove(k, out _);
        foreach (var k in _mine.Keys) if (list.All(a => a.AnimationID != k)) _mine.TryRemove(k, out _);
        var out_ = new List<Playing>(list.Count);
        foreach (var a in list)
        {
            _since.TryAdd(a.AnimationID, now);
            out_.Add(new Playing(a.AnimationID, a.AnimationSourceObjectID, GestureOps.BuiltInName(a.AnimationID) != null));
        }
        return out_;
    }

    /// <summary>The agent API started it (the name it was asked for: an item's, or a built-in's).</summary>
    public void Started(UUID id, string? name) => _mine[id] = name ?? "";

    /// <summary>Whether my arms are being animated by something other than my own defaults: an animation started by an
    /// object I'm not wearing (a dance machine, a pose ball, a seat's own sit; old dance balls play the built-in dances),
    /// or one the agent API started for me (a dance from my inventory). Not the region's own standing, walking and
    /// sitting, nor my AO or other attachments: they animate me all the time, and the tracked hands win over them.</summary>
    public bool AnimatedByThings()
    {
        // asked every frame in the headset; the list changes a few times a minute at most
        long now = Environment.TickCount64;
        if (now - _checkedAt < 250) return _animated;
        _checkedAt = now;
        return _animated = Check();
    }

    long _checkedAt = -1000;
    bool _animated;

    bool Check()
    {
        var c = _s.Client;
        var w = _s.Region;
        if (c is null || w is null) return false;
        foreach (var p in Now())
        {
            if (_mine.ContainsKey(p.Id)) return true;
            if (p.Source == UUID.Zero || p.Source == c.Self.AgentID) continue;
            // an object elsewhere (a dance machine left behind by a teleport, still dancing you) counts as not worn
            var prim = w.PrimById(p.Source);
            if (prim is null || !WornByMe(w, prim, c.Self.AgentID)) return true;
        }
        return false;
    }

    static bool WornByMe(RegionWorld w, Primitive p, UUID me)
    {
        var cur = p;
        for (var depth = 0; cur.ParentID != 0 && depth < 8; depth++)
        {
            if (w.Prims.TryGetValue(cur.ParentID, out var parent)) { cur = parent; continue; }
            return w.Avatars.TryGetValue(cur.ParentID, out var av) && av.ID == me;
        }
        return false;
    }

    /// <summary>What plays, for the API: names where they're known (built-in, the API's own, the object that started
    /// it), the source, and since when.</summary>
    public JsonObject List()
    {
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        var w = _s.Region;
        var arr = new JsonArray();
        foreach (var p in Now())
        {
            var o = new JsonObject { ["id"] = p.Id.ToString() };
            var builtIn = GestureOps.BuiltInName(p.Id);
            if (builtIn != null) o["name"] = builtIn;
            else if (_mine.TryGetValue(p.Id, out var mine) && mine.Length > 0) o["name"] = mine;
            else if (_s.Ao.NameOf(p.Id) is { } aoName) o["name"] = aoName;
            o["built_in"] = builtIn != null;
            if (p.Source != UUID.Zero && p.Source != c.Self.AgentID)
            {
                var src = new JsonObject { ["id"] = p.Source.ToString() };
                if (w?.PrimById(p.Source) is { } prim)
                {
                    var root = _s.Objects.RootOf(w, prim);
                    src["name"] = root.Properties?.Name ?? prim.Properties?.Name;
                    src["worn"] = WornByMe(w, prim, c.Self.AgentID);
                    if (c.Self.SittingOn != 0 && (c.Self.SittingOn == prim.LocalID || c.Self.SittingOn == root.LocalID)) src["sitting_on"] = true;
                }
                else src["note"] = "not in this region (an object elsewhere, or gone)";
                o["started_by"] = src;
            }
            else o["started_by"] = _mine.ContainsKey(p.Id) ? "the agent API" : _s.Ao.Plays(p.Id) ? "my AO (SojournAo)"
                : builtIn != null ? "the region (moving, standing, sitting)" : "me";
            if (_since.TryGetValue(p.Id, out var t)) o["seconds"] = Math.Round((DateTime.UtcNow - t).TotalSeconds);
            arr.Add(o);
        }
        // whether, in the headset, the tracked hands give way to these (tracking auto)
        return new JsonObject { ["animations"] = arr, ["sitting"] = c.Self.SittingOn != 0, ["moved_by_things"] = Check() };
    }

    /// <summary>One animation, or all of them as the viewer's "Stop animating me" does. keepPermissions: as
    /// LLAgent::stopCurrentAnimations(true), objects keep their animation permissions and a seat isn't left (the pose
    /// stand's way in and out).</summary>
    public async Task<JsonObject> StopAsync(UUID? only, CancellationToken ct, bool keepPermissions = false)
    {
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        var sim = c.Network.CurrentSim ?? throw new LeapError("no region");
        var playing = Now();
        if (only is { } id)
        {
            c.Self.AnimationStop(id, true);
            _mine.TryRemove(id, out _);
            return new JsonObject { ["stopped"] = new JsonArray(id.ToString()), ["was_playing"] = playing.Any(p => p.Id == id) };
        }
        var stop = playing.Select(p => p.Id).Where(x => x != GroundSit && x != DoNotDisturb).Distinct().ToList();
        if (stop.Count > 0) c.Self.Animate(stop.ToDictionary(x => x, _ => false), true);
        // the region's animation state overrides cleared: a null animation, stopped, with an empty physical event
        c.Network.SendPacket(new AgentAnimationPacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            AnimationList = new[] { new AgentAnimationPacket.AnimationListBlock { AnimID = UUID.Zero, StartAnim = false } },
            PhysicalAvatarEventList = new[] { new AgentAnimationPacket.PhysicalAvatarEventListBlock { TypeData = Array.Empty<byte>() } },
        }, sim);
        // RevokePermsOnStopAnimation (on by default): the region's objects lose their animation permissions
        if (!keepPermissions)
            c.Network.SendPacket(new RevokePermissionsPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                Data = { ObjectID = sim.RegionID, ObjectPermissions = AnimationPermissions },
            }, sim);
        bool stood = false;
        if (c.Self.SittingOn != 0 && !keepPermissions)
        {
            // a seat's sit animation permission is granted by sitting; with it revoked, the viewer stands up
            c.Self.Stand();
            stood = true;
        }
        c.Self.AnimationStart(Animations.STAND, true);
        _mine.Clear();
        await Task.Delay(400, ct);
        var o = new JsonObject { ["stopped"] = new JsonArray(stop.Select(x => (JsonNode)x.ToString()).ToArray()) };
        if (stood) o["stood_up"] = true;
        return o;
    }
}
