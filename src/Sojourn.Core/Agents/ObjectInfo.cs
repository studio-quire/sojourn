using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// What an object costs and runs: land impact as the edit floater shows it (the region's GetObjectCost: the linkset's
/// and each prim's resource cost, physics, streaming; ResourceCostSelected for a selection's totals) and its scripts
/// as Firestorm's Script Info counts them (how many, how many running; time and memory only come from the region's
/// Top Scripts, for estate managers, SojournLand topScripts). Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class ObjectInfo
{
    readonly AgentSession _s;
    public ObjectInfo(AgentSession s) => _s = s;

    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    /// <summary>{ids} -> each linkset's land impact and its prims' costs.</summary>
    public async Task<JsonObject> LandImpactAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var w = _s.Region ?? throw new LeapError("no region");
        var roots = new List<Primitive>();
        foreach (var id in a.UuidList("ids").Concat(a.Uuid("id") is { } one ? new[] { one } : Array.Empty<UUID>()))
        {
            var p = w.PrimById(id) ?? throw new LeapError($"object not found in this region: {id}");
            var r = _s.Objects.RootOf(w, p);
            if (!roots.Any(x => x.LocalID == r.LocalID)) roots.Add(r);
        }
        if (roots.Count == 0) throw new LeapError("need id or ids");
        var cap = c.Network.CurrentSim?.Caps?.CapabilityURI("GetObjectCost") ?? throw new LeapError("this region has no GetObjectCost capability");
        var prims = roots.SelectMany(r => _s.Objects.Linkset(w, r)).ToList();
        var req = new OSDMap { ["object_ids"] = new OSDArray(prims.Select(p => OSD.FromUUID(p.ID)).ToList()) };
        var (resp, body) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, req, ct);
        if (!resp.IsSuccessStatusCode) throw new LeapError($"GetObjectCost: {(int)resp.StatusCode}");
        var map = OSDParser.Deserialize(body ?? Array.Empty<byte>()) as OSDMap ?? throw new LeapError("GetObjectCost: no answer");
        double D(OSDMap m, string k) => m.TryGetValue(k, out var v) ? Math.Round(v.AsReal(), 3) : 0;
        var arr = new JsonArray();
        double total = 0;
        foreach (var r in roots)
        {
            var o = new JsonObject { ["id"] = r.ID.ToString(), ["name"] = r.Properties?.Name ?? "" };
            if (map.TryGetValue(r.ID.ToString(), out var rv) && rv is OSDMap rm)
            {
                var li = D(rm, "linked_set_resource_cost");
                total += Math.Ceiling(li - 1e-6);
                o["land_impact"] = Math.Ceiling(li - 1e-6);
                o["land_impact_exact"] = li;
                o["physics"] = D(rm, "linked_set_physics_cost");
            }
            else o["land_impact"] = null;
            var parts = new JsonArray();
            foreach (var p in _s.Objects.Linkset(w, r))
                if (map.TryGetValue(p.ID.ToString(), out var pv) && pv is OSDMap pm)
                    parts.Add(new JsonObject
                    {
                        ["id"] = p.ID.ToString(), ["name"] = p.Properties?.Name ?? "", ["resource"] = D(pm, "resource_cost"),
                        ["physics"] = D(pm, "physics_cost"),
                    });
            if (a.Bool("prims", false)) o["prims"] = parts;
            o["prim_count"] = _s.Objects.Linkset(w, r).Count;
            arr.Add(o);
        }
        var result = new JsonObject { ["objects"] = arr, ["land_impact"] = total };
        // the selection's totals, as the edit floater's "Selected: N" line
        if (c.Network.CurrentSim?.Caps?.CapabilityURI("ResourceCostSelected") is { } sel)
        {
            try
            {
                var (sr, sb) = await c.HttpCapsClient.PostAsync(sel, OSDFormat.Xml,
                    new OSDMap { ["selected_roots"] = new OSDArray(roots.Select(r => OSD.FromUUID(r.ID)).ToList()) }, ct);
                if (sr.IsSuccessStatusCode && OSDParser.Deserialize(sb ?? Array.Empty<byte>()) is OSDMap sm && sm.TryGetValue("selected", out var s) && s is OSDMap ss)
                    result["selected"] = new JsonObject { ["physics"] = D(ss, "physics"), ["streaming"] = D(ss, "streaming"), ["simulation"] = D(ss, "simulation") };
            }
            catch (HttpRequestException) { }
        }
        return result;
    }

    /// <summary>{ids} -> the scripts in each linkset: count, running, by prim.</summary>
    public async Task<JsonObject> ScriptInfoAsync(JsonObject a, CancellationToken ct)
    {
        var listed = await _s.ScriptChores.ListAsync(a, ct);
        var scripts = listed["scripts"] as JsonArray ?? new JsonArray();
        var w = _s.Region!;
        var byRoot = new JsonArray();
        foreach (var g in scripts.OfType<JsonObject>().GroupBy(x => w.PrimById(x.Uuid("prim_id") ?? UUID.Zero) is { } p ? _s.Objects.RootOf(w, p).ID : UUID.Zero))
        {
            var root = w.PrimById(g.Key);
            byRoot.Add(new JsonObject
            {
                ["id"] = g.Key.ToString(), ["name"] = root?.Properties?.Name ?? "", ["scripts"] = g.Count(),
                ["running"] = g.Count(x => x.Bool("running", false)),
            });
        }
        return new JsonObject
        {
            ["objects"] = byRoot, ["scripts"] = listed["count"]?.DeepClone(), ["running"] = listed["running"]?.DeepClone(),
            ["note"] = "time and memory per object: the region's Top Scripts (SojournLand topScripts {object: name}), for estate managers",
        };
    }
}
