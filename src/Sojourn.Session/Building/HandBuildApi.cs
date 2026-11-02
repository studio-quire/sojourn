using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Building;

/// <summary>
/// SojournHandBuild: building by hand for callers of the agent API (the assistant): turn it on with a thing put in the
/// person's hands, turn it off, and see what they did (<see cref="HandMove"/>). Registered by a host with hands (the VR
/// client); moving itself is only ever the person's.
/// </summary>
public sealed class HandBuildApi : LeapApi
{
    readonly HandMove _m;

    public override string Name => "SojournHandBuild";
    public override string Desc => "building by hand: the person grabs a thing with the trigger, moves and turns it, and lets go (the assistant places "
        + "roughly, the person fine-tunes); moves by hand are undo steps like TesselBuild's";

    public HandBuildApi(AgentSession s, HandMove m) : base(s)
    {
        _m = m;
        Op("start", "{id?} -> state: turns building by hand on, with that object (any prim of it; its whole linkset moves) chosen and outlined "
            + "for them to grab; refused, with why, if they may not move it (not theirs to modify, worn, RLV). Without id: their own things", Start);
        Op("stop", "{} -> state: turns it off (the person's readout has a done key too)", _ =>
        {
            _m.Stop("asked through the agent API");
            return _m.State();
        });
        Op("state", "{} -> {on, chosen?, holding?: {id, name, pos, rot_euler}, last?: {id, name, from, to, rot_euler, moved_m, turned_deg, "
            + "result: moving | placed | refused (the region kept it where it was) | moved elsewhere | gone, ago_s}}", _ => _m.State());
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
