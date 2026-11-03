using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Building;

/// <summary>
/// SojournHandBuild: building by hand for callers of the agent API (the assistant): turn it on with a thing selected
/// for the person (its arrows, rings and middle handle on it), turn it off, see what they did, and straighten a thing
/// (<see cref="HandMove"/>). Registered by a host with hands (the VR client); moving by hand is only ever the person's.
/// </summary>
public sealed class HandBuildApi : LeapApi
{
    readonly HandMove _m;

    public override string Name => "SojournHandBuild";
    public override string Desc => "building by hand: the person selects a thing by tapping it and moves and turns it by its arrows, rings and middle "
        + "handle (the assistant places roughly, the person fine-tunes); moves by hand are undo steps like TesselBuild's";

    public HandBuildApi(AgentSession s, HandMove m) : base(s)
    {
        _m = m;
        Op("start", "{id?} -> state: turns building by hand on, with that object (any prim of it; its whole linkset moves) selected for them, "
            + "its handles on it; refused, with why, if they may not move it (not theirs to modify, worn, RLV). Without id: on, nothing selected", Start);
        Op("stop", "{} -> state: turns it off (the person's readout has a done key too)", _ =>
        {
            _m.Stop("asked through the agent API");
            return _m.State();
        });
        Op("state", "{} -> {on, selected?: {id, name, pos, rot_euler}, holding?: {id, name, pos, rot_euler}, last?: {id, name, from, to, rot_euler, "
            + "moved_m, turned_deg, result: moving | placed | refused (the region kept it where it was) | partly refused (the region took it "
            + "part of the way, as far as the land allows: 'to' is where it stopped) | nudged (the region adjusted it a few cm) | moved elsewhere "
            + "(physics or a script, no refusal) | gone, put? (where their hand put it), off_m?, region_said? (the region's refusal notice "
            + "that answered the drag, e.g. \"Can't rotate -- permission denied\" when the land there doesn't allow their objects), ago_s}}", _ => _m.State());
        Op("straighten", "{id, level?} -> {id, name, pos, rot_euler, turned_deg}: its rotation back to none (the edit floater's 0,0,0), or with "
            + "level: true only its tilt taken out, keeping which way it faces; the whole linkset turns about its middle, so it stays where it is. "
            + "One undo step. Works whether or not building by hand is on", a =>
        {
            var id = a.Uuid("id") ?? throw new LeapError("need id");
            try { return _m.Straighten(id, a["level"]?.GetValue<bool>() ?? false); }
            catch (InvalidOperationException e) { throw new LeapError($"can't straighten that: {e.Message}"); }
        });
    }

    JsonNode? Start(JsonObject a)
    {
        var id = a.Uuid("id") ?? UUID.Zero;
        var root = UUID.Zero;
        if (id != UUID.Zero)
        {
            if (S.Client?.Network.CurrentSim is not { } sim) throw new LeapError("not in a region");
            var (r, why) = _m.May(sim.Handle, id, chosen: true, tell: true);
            if (r is null) throw new LeapError($"they can't move that by hand: {why}");
            root = r.ID;
        }
        _m.Start(root, "the agent API");
        return _m.State();
    }
}
