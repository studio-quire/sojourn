using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Render.Particles;

namespace Sojourn.Session.Control;

/// <summary>
/// SojournSafety: the person's tools against griefing and clutter, for the assistant and any caller (Core's Safety,
/// docs/viewer-gaps.md > Safety): derendering objects (Firestorm's Derender), the asset blacklist for sounds and
/// animations, the sounds heard lately (its Sound Explorer) and particle sources around (to find whose they are), blocking
/// a particle owner (the viewer's Block particle owner), what antispam has blocked, the session's bumps, pushes and hits,
/// and releasing controls scripts took (Release Keys). The switches (antispam, particles, keep_in_place, the vignette,
/// aim) are SojournSettings. Nothing here is gated: hiding and blocking only change what this viewer shows and plays.
/// </summary>
public sealed class SafetyApi : LeapApi
{
    readonly Action? _changed;
    readonly Action<Func<UUID, UUID, UUID, bool>>? _silence;
    readonly Func<Task<List<(UUID Sound, UUID Object, UUID Owner, bool Loop)>>>? _playing;

    public override string Name => "SojournSafety";
    public override string Desc => "derender objects, block sounds, animations and particle owners, antispam's blocks, bumps and pushes, release keys";

    /// <param name="changed">something the menu shows changed (how many are derendered)</param>
    /// <param name="silence">stop what's playing that a new block covers (world audio)</param>
    /// <param name="playing">what world audio plays now</param>
    public SafetyApi(AgentSession s, Action? changed, Action<Func<UUID, UUID, UUID, bool>>? silence,
        Func<Task<List<(UUID Sound, UUID Object, UUID Owner, bool Loop)>>>? playing) : base(s)
    {
        _changed = changed;
        _silence = silence;
        _playing = playing;
        Op("derender", "{id, permanent?: false} -> {id, name, until: teleport|always}: an object (any prim of it, or something someone "
            + "wears) disappears from this viewer at once and stays gone: not drawn, touched, heard or listed; until the next teleport, or "
            + "for good (kept with the account). Seated on it, the person stands first. People: TesselAgent hideAvatars", Derender);
        Op("rerender", "{id | all: true} -> {shown}: derendered objects come back (asked of the region again)", Rerender);
        Op("hidden", "-> {objects: [{id, name, region, until}], sounds: [...], animations: [...], owners: [{id, name, from}]}: "
            + "everything derendered and blacklisted", _ => Hidden());
        Op("sounds", "{seconds?: 120} -> {sounds: [{sound, object, object_name?, owner, owner_name, how: worn|rezzed|gesture, "
            + "playing?: loop|now, times?, ago?, region, position, blocked}]}: what plays now, then what was heard lately, newest first, "
            + "to block one (blockSound) or someone's (blockSound owner)", Sounds);
        Op("blockSound", "{sound} | {owner, from?: worn|rezzed|gestures|all} -> {blocked}: never play that sound again, or that "
            + "person's worn, rezzed or gesture sounds (all by default); kept with the account; what's playing of it stops", BlockSound);
        Op("blockAnimation", "{id} -> {blocked}: never play that animation on anyone (stopped on the person if it's playing); kept", BlockAnimation);
        Op("unblock", "{id} -> {unblocked: kind}: off the list again (a sound, an animation, an owner's sounds, an object)", Unblock);
        Op("particles", "{within?: 64} -> {sources: [{id, root, name?, owner, owner_name, distance, drawn, worn_by?}], drawn}: what's "
            + "emitting particles around, nearest first, whose, and how many particles each drew in the view (drawn: all of them)", Particles);
        Op("blockParticleOwner", "{id: a source object | owner} -> {owner, name}: the viewer's Block particle owner: the owner joins "
            + "the block list (their particles, sounds, chat, IMs, offers and menus stop; unblock with TesselAgent unmute). To hide "
            + "just one emitter: derender it", BlockParticleOwner);
        Op("spam", "-> {on, blocked: [{source, name, kind, limit, ago}]}: what antispam is holding back (until the person leaves the region)", _ => Spam());
        Op("unspam", "{id | all: true} -> {lifted}: let a blocked source through again", Unspam);
        Op("bumps", "{seconds?} -> {bumps: [{who, name, what, magnitude, ago}]}: who bumped, pushed or hit the person this session "
            + "(Second Life reports these; OpenSim doesn't)", Bumps);
        Op("status", "-> {controls_taken: [...], keep_in_place: {on, holding}, antispam, derendered, position, aiming?, vignette?}: how things "
            + "stand (the controls scripts hold now; aiming and the vignette's strength in the headset)", _ => Status());
        Op("releaseKeys", "-> {released: [controls]}: the viewer's Release Keys: take back every control scripts have taken (a stuck "
            + "vehicle or weapon keeping the sticks)", _ => ReleaseKeys());
    }

    UUID Id(JsonObject a, string key = "id") =>
        a.Uuid(key) ?? throw new LeapError($"{key}: an id (a UUID)");

    JsonNode? Derender(JsonObject a)
    {
        var (e, why) = S.Safety.Derender(Id(a), a.Bool("permanent", false));
        if (e == null) throw new LeapError(why ?? "couldn't");
        _changed?.Invoke();
        return new JsonObject { ["id"] = e.Id.ToString(), ["name"] = e.Name, ["until"] = e.Permanent ? "always" : "teleport" };
    }

    JsonNode? Rerender(JsonObject a)
    {
        int n;
        if (a.Bool("all", false)) n = S.Safety.RerenderAll();
        else
        {
            var id = Id(a);
            var e = S.Safety.Entries.FirstOrDefault(x => x.Id == id && x.Kind == Safety.Kind.Object);
            n = e != null && S.Safety.Remove(id) != null ? 1 : 0;
            if (n == 0) throw new LeapError("that isn't derendered");
        }
        _changed?.Invoke();
        return new JsonObject { ["shown"] = n };
    }

    JsonObject Hidden()
    {
        JsonArray Of(Safety.Kind k, Func<Safety.Entry, JsonObject> f) => new(S.Safety.Entries.Where(e => e.Kind == k).Select(e => (JsonNode)f(e)).ToArray());
        return new JsonObject
        {
            ["objects"] = Of(Safety.Kind.Object, e => new JsonObject
            {
                ["id"] = e.Id.ToString(), ["name"] = e.Name, ["region"] = e.Region, ["until"] = e.Permanent ? "always" : "teleport",
            }),
            ["sounds"] = Of(Safety.Kind.Sound, e => new JsonObject { ["id"] = e.Id.ToString(), ["name"] = e.Name }),
            ["animations"] = Of(Safety.Kind.Animation, e => new JsonObject { ["id"] = e.Id.ToString(), ["name"] = e.Name }),
            ["owners"] = Of(Safety.Kind.OwnerSounds, e => new JsonObject
            {
                ["id"] = e.Id.ToString(), ["name"] = e.Name, ["from"] = e.Sounds.ToString().ToLowerInvariant().Replace(", ", ","),
            }),
        };
    }

    JsonNode? Sounds(JsonObject a)
    {
        double now = Json.UnixNow();
        var arr = new JsonArray();
        var seen = new HashSet<(UUID, UUID)>();
        JsonObject Row(UUID sound, UUID obj, UUID owner, string how)
        {
            var o = new JsonObject
            {
                ["sound"] = sound.ToString(), ["object"] = obj.ToString(), ["owner"] = owner.ToString(),
                ["owner_name"] = S.Names.DisplayName(owner) ?? "", ["how"] = how,
                ["blocked"] = S.Safety.SoundBlocked(sound, obj, owner, how == "worn"),
            };
            if (S.Objects.FindAnywhere(obj) is { } f)
            {
                if (f.Prim.Properties?.Name is { Length: > 0 } n) o["object_name"] = n;
                o["region"] = f.World.Name;
                o["position"] = Json.Arr(f.World.RegionPosition(f.Prim));
            }
            return o;
        }
        // what plays now first (a loop that started long ago is the usual nuisance), then what was heard lately
        var playing = _playing?.Invoke();
        if (playing != null && playing.Wait(TimeSpan.FromSeconds(1)))
            foreach (var (sound, obj, owner, loop) in playing.Result)
            {
                if (!seen.Add((sound, obj))) continue;
                var row = Row(sound, obj, owner, obj == owner ? "gesture" : Worn(obj) ? "worn" : "rezzed");
                row["playing"] = loop ? "loop" : "now";
                arr.Add(row);
            }
        foreach (var h in S.Safety.HeardSince(a.Num("seconds", 120)).Take(60))
        {
            if (!seen.Add((h.Sound, h.Object))) continue;
            var row = Row(h.Sound, h.Object, h.Owner, h.How);
            row["times"] = h.Times;
            row["ago"] = Math.Round(now - h.At, 1);
            arr.Add(row);
        }
        return new JsonObject { ["sounds"] = arr };
    }

    JsonNode? BlockSound(JsonObject a)
    {
        if (a.Uuid("sound") is { } sound)
        {
            var e = S.Safety.BlockSound(sound, a.Str("name") ?? "");
            _silence?.Invoke((snd, _, _) => snd == sound);
            return new JsonObject { ["blocked"] = "sound", ["id"] = e.Id.ToString() };
        }
        if (a.Uuid("owner") is not { } owner) throw new LeapError("sound (an asset id) or owner (a person's id)");
        var from = (a.Str("from") ?? "all").Trim().ToLowerInvariant() switch
        {
            "worn" => Safety.From.Worn, "rezzed" => Safety.From.Rezzed, "gestures" or "gesture" => Safety.From.Gestures, "all" => Safety.From.All,
            var x => throw new LeapError($"from: worn, rezzed, gestures or all (not '{x}')"),
        };
        var name = S.Names.DisplayName(owner) ?? "";
        var en = S.Safety.BlockOwnerSounds(owner, name, from);
        _silence?.Invoke((snd, obj, own) => own == owner && S.Safety.SoundBlocked(snd, obj, own, Worn(obj)));
        return new JsonObject { ["blocked"] = "owner", ["id"] = owner.ToString(), ["name"] = name, ["from"] = en.Sounds.ToString().ToLowerInvariant() };
    }

    bool Worn(UUID obj) => S.Objects.FindAnywhere(obj) is { } f && S.Objects.IsAttachment(f.World, f.Prim, out _, out _);

    JsonNode? BlockAnimation(JsonObject a)
    {
        var e = S.Safety.BlockAnimation(Id(a), a.Str("name") ?? "");
        return new JsonObject { ["blocked"] = "animation", ["id"] = e.Id.ToString() };
    }

    JsonNode? Unblock(JsonObject a)
    {
        var e = S.Safety.Remove(Id(a)) ?? throw new LeapError("that isn't on the list");
        _changed?.Invoke();
        return new JsonObject { ["unblocked"] = e.Kind switch
        {
            Safety.Kind.Object => "object", Safety.Kind.Sound => "sound", Safety.Kind.Animation => "animation", _ => "owner's sounds",
        } };
    }

    JsonNode? Particles(JsonObject a)
    {
        double within = a.Num("within", 64);
        // how many each drew in the next view (the render thread counts once when asked)
        ParticleBuilder.Counts = null;
        ParticleBuilder.CountNext = true;
        for (int i = 0; i < 25 && ParticleBuilder.Counts == null; i++) Thread.Sleep(20);
        var counts = ParticleBuilder.Counts ?? new Dictionary<UUID, int>();
        var me = S.Client?.Self;
        var list = new List<(double D, JsonObject O)>();
        foreach (var w in S.VisibleRegions())
        {
            var off = S.FrameOffset(w);
            foreach (var p in w.Prims.Values)
            {
                var ps = ParticleSystemTracker.Shared.TryGet(w.Handle, p.LocalID, out var tracked) ? tracked ?? default : p.ParticleSys;
                if (!EffectTracker.IsOn(ps)) continue;
                var pos = w.RegionPosition(p) + off;
                double d = me != null ? Vector3.Distance(pos, me.SimPosition) : 0;
                if (d > within) continue;
                bool worn = S.Objects.IsAttachment(w, p, out var wearer, out var root);
                var owner = p.OwnerID != UUID.Zero ? p.OwnerID : root.OwnerID;
                var o = new JsonObject
                {
                    ["id"] = p.ID.ToString(), ["root"] = root.ID.ToString(), ["owner"] = owner.ToString(),
                    ["owner_name"] = S.Names.DisplayName(owner) ?? "", ["distance"] = Math.Round(d, 1),
                    ["drawn"] = counts.GetValueOrDefault(p.ID),
                };
                if ((root.Properties?.Name ?? p.Properties?.Name) is { Length: > 0 } n) o["name"] = n;
                if (worn && wearer != null) o["worn_by"] = S.Names.DisplayName(wearer.ID) ?? wearer.Name;
                list.Add((d, o));
            }
        }
        return new JsonObject
        {
            ["sources"] = new JsonArray(list.OrderBy(x => x.D).Take(40).Select(x => (JsonNode)x.O).ToArray()),
            ["drawn"] = ParticleBuilder.Drawn,   // particles in the last view drawn
        };
    }

    JsonNode? BlockParticleOwner(JsonObject a)
    {
        var id = Id(a);
        var owner = S.Objects.FindAnywhere(id) is { } f
            ? (f.Prim.OwnerID != UUID.Zero ? f.Prim.OwnerID : S.Objects.RootOf(f.World, f.Prim).OwnerID)
            : id;
        if (owner == UUID.Zero) throw new LeapError("whose it is isn't known yet");
        if (owner == S.AgentId) throw new LeapError("those are the person's own particles");
        var name = S.Names.DisplayName(owner) ?? owner.ToString();
        S.Mutes.Add(owner, name, "resident", S.Client);
        return new JsonObject { ["owner"] = owner.ToString(), ["name"] = name };
    }

    JsonObject Spam()
    {
        double now = Json.UnixNow();
        return new JsonObject
        {
            ["on"] = S.Safety.SpamOn,
            ["blocked"] = new JsonArray(S.Safety.SpamBlocks.Select(b => (JsonNode)new JsonObject
            {
                ["source"] = b.Source.ToString(), ["name"] = b.Name, ["kind"] = b.Queue.ToString().ToLowerInvariant(),
                ["limit"] = b.Limit, ["ago"] = Math.Round(now - b.At, 1),
            }).ToArray()),
        };
    }

    JsonNode? Unspam(JsonObject a) =>
        new JsonObject { ["lifted"] = a.Bool("all", false) ? S.Safety.ClearSpamBlocks() : S.Safety.Unspam(Id(a)) };

    JsonNode? Bumps(JsonObject a)
    {
        double now = Json.UnixNow(), since = now - a.Num("seconds", 1e9);
        return new JsonObject
        {
            ["bumps"] = new JsonArray(S.Safety.Bumps.Where(b => b.At >= since).Select(b => (JsonNode)new JsonObject
            {
                ["who"] = b.Who.ToString(), ["name"] = b.Name, ["what"] = Safety.BumpText(b.Type, b.Name),
                ["magnitude"] = Math.Round(b.Magnitude, 2), ["ago"] = Math.Round(now - b.At, 1),
            }).ToArray()),
        };
    }

    /// <summary>The headset's part of status (aiming, the vignette), from the client.</summary>
    public Func<JsonObject>? Headset { get; set; }

    static JsonArray Controls(ScriptControlChange held) =>
        new(Enum.GetValues<ScriptControlChange>().Where(f => f != ScriptControlChange.None && (held & f) == f).Select(f => (JsonNode)f.ToString()).ToArray());

    JsonObject Status()
    {
        var o = new JsonObject
        {
            ["controls_taken"] = Controls(S.ControlsTaken),
            ["keep_in_place"] = new JsonObject { ["on"] = S.MoveLock.On, ["holding"] = S.MoveLock.Holding },
            ["antispam"] = S.Safety.SpamOn,
            ["derendered"] = S.Safety.Entries.Count(e => e.Kind == Safety.Kind.Object),
        };
        if (S.Client is { Network.Connected: true } c) o["position"] = Json.Arr(c.Self.SimPosition);
        if (Headset?.Invoke() is { } h) foreach (var (k, v) in h) o[k] = v?.DeepClone();
        return o;
    }

    JsonObject ReleaseKeys()
    {
        var held = S.Safety.ReleaseControls();
        return new JsonObject { ["released"] = Controls(held) };
    }
}
