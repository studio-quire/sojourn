using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Leap;

/// <summary>The avatar's own, batch B7: the client AO (Firestorm's, from an inventory folder in its format or a ZHAO-II
/// notecard) and the pose stand. Agents/AoEngine.cs, AoFolders.cs, AoDriver.cs, PoseStand.cs. Claude Opus 5.5, 2026-10-29.</summary>
public sealed class SojournAo : SessionApi
{
    public override string Name => "SojournAo";
    public override string Desc => "The person's animation overrider (AO: stands, walks, sits... from an inventory folder in Firestorm's format), "
        + "and the pose stand";

    public SojournAo(AgentSession s) : base(s)
    {
        var ao = s.Ao;
        var pose = s.PoseStand;

        Op("status", "-> {on, playing_now, stands, paused_by, folder: {choice, id, name, kind, how}, sets, set, state?, step?, opencollar, "
            + "lockmeister, lock_folders, warnings?}: the AO as it is", _ => ao.Status());
        Op("on", "{on=true} -> status: the AO on or off (off by default, as in Firestorm; kept with the account)", a =>
        {
            ao.On = a.Bool("on", true);
            return ao.Status();
        });
        Op("stands", "{on=true} -> status: whether it overrides standing (and turning) too", a =>
        {
            ao.StandsOn = a.Bool("on", true);
            return ao.Status();
        });
        OpR("folder", "{folder: firestorm | none | a folder id} -> status: which folder the AO plays. firestorm (the default) is "
            + "#Firestorm/#AO when the person has one (Firestorm's own, shared with it); a picked folder may hold sets, be one set (its "
            + "subfolders named for states: Standing, Walking, Sitting...), or be a scripted AO's folder with a ZHAO-II notecard and its "
            + "animations (read in place, nothing copied)", (a, ct) => J(ao.ChooseAsync((a.Str("folder") ?? a.Str("id") ?? throw new LeapError("need folder")).Trim(), ct)));
        OpR("reload", "-> status: read the folder again (after changing it by hand)", async (_, ct) =>
        {
            await ao.ReloadAsync(ct);
            return ao.Status();
        });
        OpR("set", "{name? (the current set)} -> {name, default, sit_override, smart_sit, no_mouselook_stands, states: [{state, cycle, random, "
            + "cycle_time, steps: [{name, group?, animations?}], tracks?, playing?}]}: a set's states and animations", a => ao.SetDetail(ao.SetNamed(a.Str("name"))));
        OpR("select", "{name} -> status: play another set", a =>
        {
            ao.Select(ao.SetNamed(a.Str("name") ?? throw new LeapError("need name")));
            return ao.Status();
        });
        OpR("next", "-> status: the state playing now goes on to its next animation", _ => { ao.Run(e => e.Cycle(AoEngine.CycleMode.Next, true)); return ao.Status(); });
        OpR("previous", "-> status: back to its previous animation", _ => { ao.Run(e => e.Cycle(AoEngine.CycleMode.Previous, true)); return ao.Status(); });
        OpR("play", "{step (a name) | index} -> status: that animation of the state playing now", a =>
        {
            int index = a.Int("index", -1);
            if (a.Str("step") is { } name)
            {
                index = ao.Run(e => e.StateForMotion(e.LastMotion)?.Steps.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) ?? -1);
                if (index < 0) throw new LeapError($"the state playing now has no {name}");
            }
            ao.Run(e => e.Play(index));
            return ao.Status();
        });
        OpR("setFlags", "{name? (the current set), default?, sit_override?, smart_sit?, no_mouselook_stands?} -> the set: its switches "
            + "(sit_override: the set's sits replace seats' own; smart_sit: unless the seat plays its own animation; default: chosen at login), "
            + "kept in its folder's name as Firestorm keeps them", (a, ct) => J(SetFlagsAsync(a, ct)));
        OpR("stateFlags", "{set?, state (Standing, Walking, Sitting...), cycle?, random?, cycle_time? (seconds; 0: no timer)} -> the set: "
            + "whether a state goes through its animations (in order or at random) and how often", (a, ct) => J(StateFlagsAsync(a, ct)));
        OpR("add", "{set?, state, item_id | item_ids (animations of mine)} -> {set, state, added}: animations into a state, as links (the "
            + "animations stay where they are); makes the state's folder in the set when it has none", async (a, ct) =>
        {
            var set = ao.SetNamed(a.Str("set"));
            var kind = State(a);
            var ids = a.UuidList("item_ids");
            if (a.Uuid("item_id") is { } one) ids.Add(one);
            if (ids.Count == 0) throw new LeapError("need item_id or item_ids");
            var items = new List<InventoryItem>();
            foreach (var id in ids)
            {
                var it = await s.Inventory.ItemAsync(id, ct) ?? throw new LeapError($"no inventory item {id}");
                if (it.AssetType == AssetType.Link && await s.Inventory.ItemAsync(it.AssetUUID, ct) is { } t) it = t;
                if (it.AssetType != AssetType.Animation) throw new LeapError($"{it.Name} isn't an animation");
                items.Add(it);
            }
            return await ao.AddAsync(set, kind, items, ct);
        });
        OpR("remove", "{set?, state, step (a name) | index} -> {removed}: an animation (or group) out of a state: its link deleted, the "
            + "animation itself kept; the person confirms", async (a, ct) =>
        {
            var set = ao.SetNamed(a.Str("set"));
            var nodes = ao.Removal(set, State(a), a.Str("step"), a.Has("index") ? a.Int("index", 0) : null);
            await ao.RemoveNodesAsync(nodes, ct);
            return new JsonObject { ["removed"] = new JsonArray(nodes.Select(n => (JsonNode)n.Name).ToArray()) };
        });
        OpR("newSet", "{name} -> {set, folder_id, made?}: an empty set in the AO's folder (fill it with add). With no AO folder yet, "
            + "this makes Firestorm's #Firestorm/#AO, so only when the person asks for a set", (a, ct) =>
            J(ao.NewSetAsync(a.Str("name") ?? throw new LeapError("need name"), ct)));
        OpR("removeSet", "{name} -> {removed}: a set's folder to Trash (its links; the animations stay); the person confirms", async (a, ct) =>
        {
            var set = ao.SetNamed(a.Str("name") ?? throw new LeapError("need name"));
            if (set.FromNotecard) throw new LeapError($"{set.Name} is a scripted AO's folder read in place; pick another folder instead (folder)");
            await ao.RemoveNodesAsync(new() { (set.FolderId, set.Name, true) }, ct);
            return new JsonObject { ["removed"] = set.Name };
        });
        OpR("renameSet", "{name, to} -> the set", async (a, ct) =>
        {
            var set = ao.SetNamed(a.Str("name") ?? throw new LeapError("need name"));
            if (set.FromNotecard) throw new LeapError($"{set.Name} takes its folder's name; rename the folder");
            var to = AoFolders.CleanName(a.Str("to") ?? "");
            if (to.Length == 0) throw new LeapError("need to (no ':' or '|')");
            if (ao.Run(e => e.SetByName(to)) is { } other && other != set) throw new LeapError($"there's already a set named {to}");
            set.Name = to;
            await ao.SaveSetAsync(set, ct);
            if (ao.Run(e => e.Current) == set) s.InventoryStore.Set("ao_set", to);
            return ao.SetDetail(set);
        });
        Op("collar", "{opencollar?, lockmeister?, lock_folders?} -> status: whether an OpenCollar's or a LockMeister item's AO commands are "
            + "obeyed (on by default, as in Firestorm; they reach Sojourn through a worn relay: relayScript), and whether the AO's folder is "
            + "locked against deleting, moving and renaming", a =>
        {
            if (a.Bool("opencollar") is { } oc) ao.OpenCollar = oc;
            if (a.Bool("lockmeister") is { } lm) ao.LockMeister = lm;
            if (a.Bool("lock_folders") is { } lf) ao.LockFolders = lf;
            return ao.Status();
        });
        Op("relayScript", "-> {name, text, how}: the LSL relay that lets a collar switch this AO (Firestorm's bridge does the same inside its "
            + "own worn object); wearing one is the person's choice", _ => new JsonObject
        {
            ["name"] = "Sojourn AO relay", ["text"] = AoDriver.RelayScript,
            ["how"] = "a script in a small worn object (any attachment point); it only tells its owner's viewer the collar's AO on/off",
        });

        // the pose stand
        Op("poseStand", "{pose? (a name from poses, an animation of mine by name, or an asset id), item_id?, lock? (sit on the ground so moving "
            + "can't break the pose; default on)} -> {on, pose, ...}: hold the avatar in a pose for editing its shape or fitting things, as "
            + "Firestorm's pose stand (the AO paused, everything else stopped); called again, another pose", (a, ct) => J(pose.StartAsync(a, ct)));
        Op("poseStandOff", "-> {on}: leave the pose stand (the AO back)", (_, ct) => J(pose.StopAsync(ct)));
        Op("poses", "-> {on, pose?, poses, last}: the stand's poses (Firestorm's, on Second Life) and what's on", _ => pose.Status());
    }

    /// <summary>An op that needs the AO's folder read first (at login it's read once the inventory has loaded).</summary>
    void OpR(string name, string desc, OpHandler run) =>
        Op(name, desc, async (a, ct) => { await S.Ao.ReadyAsync(ct); return await run(a, ct); });

    void OpR(string name, string desc, Func<JsonObject, JsonNode?> run) =>
        Op(name, desc, async (a, ct) => { await S.Ao.ReadyAsync(ct); return run(a); });

    static AoStateKind State(JsonObject a)
    {
        var name = a.Str("state") ?? throw new LeapError("need state (Standing, Walking, Running, Sitting, Sitting On Ground, Flying...)");
        return AoMotions.KindByName(name.Trim()) ?? throw new LeapError($"no state {name}; states: {string.Join(", ", AoMotions.Names.Select(n => n[0]))}");
    }

    async Task<JsonObject> SetFlagsAsync(JsonObject a, CancellationToken ct)
    {
        var ao = S.Ao;
        var set = ao.SetNamed(a.Str("name"));
        if (a.Bool("sit_override") is { } so) ao.Run(e => e.SetSitOverride(set, so));
        if (a.Bool("smart_sit") is { } sm) ao.Run(e => e.SetSmart(set, sm));
        if (a.Bool("no_mouselook_stands") is { } dm) ao.Run(e => e.SetMouselookStandDisable(set, dm));
        var renamed = new List<AoSet> { set };
        if (a.Bool("default") is { } d)
        {
            var was = ao.Run(e => e.Default);
            ao.Run(e => e.SetDefault(d ? set : was == set ? null : was));
            if (was != null && was != set) renamed.Add(was);
        }
        foreach (var x in renamed) await ao.SaveSetAsync(x, ct);
        return ao.SetDetail(set);
    }

    async Task<JsonObject> StateFlagsAsync(JsonObject a, CancellationToken ct)
    {
        var ao = S.Ao;
        var set = ao.SetNamed(a.Str("set"));
        var st = set.State(State(a));
        if (a.Bool("cycle") is { } cy) ao.Run(e => e.SetCycle(st, cy));
        if (a.Bool("random") is { } rn) ao.Run(e => e.SetRandom(st, rn));
        if (a.Num("cycle_time") is { } ct2)
        {
            if (ct2 < 0 || ct2 > 3600) throw new LeapError("cycle_time: 0 to 3600 seconds");
            ao.Run(e => e.SetCycleTime(st, (float)ct2));
        }
        await ao.SaveStateAsync(set, st, ct);
        return ao.SetDetail(set);
    }

    static async Task<JsonNode?> J(Task<JsonObject> t) => await t;
}
