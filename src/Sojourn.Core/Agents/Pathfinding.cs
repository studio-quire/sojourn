using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Second Life's pathfinding, as the viewer's Pathfinding windows (llpathfindingmanager.cpp, llpathfindinglinkset.cpp,
/// llpathfindingcharacter.cpp): the region's navmesh status and its rebake (NavMeshGenerationStatus), linksets and
/// their use (RegionObjects, ObjectNavMeshProperties), characters (CharacterProperties). A region without
/// RetrieveNavMeshSrc has pathfinding off (OpenSim never has it). The navmesh's own view is the long tail.
/// Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class Pathfinding
{
    readonly AgentSession _s;
    public Pathfinding(AgentSession s) => _s = s;

    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    Uri Cap(string name) => C.Network.CurrentSim?.Caps?.CapabilityURI(name)
        ?? throw new LeapError(C.Network.CurrentSim?.Caps?.CapabilityURI("RetrieveNavMeshSrc") is null
            ? "this region has no pathfinding (no navmesh capabilities)"
            : $"this region has no {name} capability");

    async Task<OSD> GetAsync(string cap, CancellationToken ct)
    {
        var (resp, body) = await C.HttpCapsClient.GetAsync(Cap(cap), ct);
        if (!resp.IsSuccessStatusCode) throw new LeapError($"{cap}: {(int)resp.StatusCode}");
        return OSDParser.Deserialize(body ?? Array.Empty<byte>());
    }

    /// <summary>Uses by (phantom, navmesh_category), as LLPathfindingLinkset names them.</summary>
    static readonly (string Use, bool Phantom, int Category)[] Uses =
    {
        ("walkable", false, 0), ("static_obstacle", false, 1), ("dynamic_obstacle", false, 2),
        ("material_volume", true, 0), ("exclusion_volume", true, 1), ("dynamic_phantom", true, 2),
    };

    static string UseOf(bool phantom, int category) => Uses.FirstOrDefault(u => u.Phantom == phantom && u.Category == category).Use ?? "unknown";

    public async Task<JsonObject> StatusAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var caps = c.Network.CurrentSim?.Caps;
        var on = caps?.CapabilityURI("RetrieveNavMeshSrc") is not null;
        var o = new JsonObject { ["region_enabled"] = on };
        if (!on) return o;
        if (caps!.CapabilityURI("NavMeshGenerationStatus") is not null && await GetAsync("NavMeshGenerationStatus", ct) is OSDMap st)
            o["navmesh"] = new JsonObject { ["status"] = st["status"].AsString(), ["version"] = st["version"].AsInteger() };
        if (caps.CapabilityURI("AgentState") is not null && await GetAsync("AgentState", ct) is OSDMap ag)
            o["can_modify_navmesh"] = ag["can_modify_navmesh"].AsBoolean();
        return o;
    }

    public async Task<JsonObject> LinksetsAsync(JsonObject a, CancellationToken ct)
    {
        var map = await GetAsync("RegionObjects", ct) as OSDMap ?? throw new LeapError("RegionObjects: no answer");
        var arr = new JsonArray();
        foreach (var id in map.Keys)
        {
            if (map[id] is not OSDMap m) continue;
            var phantom = m["phantom"].AsBoolean();
            var cat = m["navmesh_category"].AsInteger();
            var o = new JsonObject
            {
                ["id"] = id, ["name"] = m["name"].AsString(), ["owner"] = m["owner"].AsUUID().ToString(), ["owner_is_group"] = m["owner_is_group"].AsBoolean(),
                ["use"] = UseOf(phantom, cat), ["a"] = m["A"].AsInteger(), ["b"] = m["B"].AsInteger(), ["c"] = m["C"].AsInteger(), ["d"] = m["D"].AsInteger(),
                ["land_impact"] = m["landimpact"].AsInteger(), ["modifiable"] = m["modifiable"].AsBoolean(), ["can_be_volume"] = m["can_be_volume"].AsBoolean(),
                ["position"] = Json.Arr(m["position"].AsVector3()),
            };
            if (m.ContainsKey("is_scripted")) o["scripted"] = m["is_scripted"].AsBoolean();
            arr.Add(o);
        }
        return new JsonObject { ["linksets"] = arr, ["count"] = arr.Count };
    }

    public async Task<JsonObject> SetLinksetsAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var ids = a.UuidList("ids");
        if (a.Uuid("id") is { } one && !ids.Contains(one)) ids.Add(one);
        if (ids.Count == 0) throw new LeapError("need id or ids (linksets' roots)");
        // what the region has for them now, to send only what changes and refuse what it would (LLPathfindingLinkset)
        var now = await GetAsync("RegionObjects", ct) as OSDMap ?? new OSDMap();
        (string Use, bool Phantom, int Category)? use = a.Str("use") is { } u
            ? Uses.FirstOrDefault(x => x.Use == u.Trim().ToLowerInvariant().Replace(' ', '_').Replace("movable_obstacle", "dynamic_obstacle").Replace("movable_phantom", "dynamic_phantom")) is var hit && hit.Use is not null
                ? hit : throw new LeapError($"use: {string.Join(", ", Uses.Select(x => x.Use))}")
            : null;
        var body = new OSDMap();
        foreach (var id in ids)
        {
            var w = _s.Region;
            var root = w?.PrimById(id) is { } p ? _s.Objects.RootOf(w, p).ID : id;
            if (now[root.ToString()] is not OSDMap cur) throw new LeapError($"{root} isn't one of the region's pathfinding linksets");
            var change = new OSDMap();
            if (use is { } us)
            {
                if (us.Phantom && !cur["can_be_volume"].AsBoolean()) throw new LeapError($"{cur["name"].AsString()} can't be a volume (only plain prims can)");
                if (cur["navmesh_category"].AsInteger() != us.Category || cur["phantom"].AsBoolean() != us.Phantom)
                {
                    change["navmesh_category"] = OSD.FromInteger(us.Category);
                    if (cur["modifiable"].AsBoolean()) change["phantom"] = OSD.FromBoolean(us.Phantom);
                }
            }
            foreach (var k in new[] { "a", "b", "c", "d" })
                if (a.Num(k) is { } n) change[k.ToUpperInvariant()] = OSD.FromInteger((int)Math.Clamp(Math.Round(n), 0, 100));
            if (change.Count > 0) body[root.ToString()] = change;
        }
        if (body.Count == 0) return new JsonObject { ["changed"] = 0, ["note"] = "they already are" };
        var (resp, _) = await c.HttpCapsClient.PutAsync(Cap("ObjectNavMeshProperties"), OSDFormat.Xml, body, ct);
        if (!resp.IsSuccessStatusCode) throw new LeapError($"the region refused the change ({(int)resp.StatusCode})");
        return new JsonObject { ["changed"] = body.Count, ["note"] = "rebake the navmesh for characters to see it (rebake)" };
    }

    public async Task<JsonObject> CharactersAsync(JsonObject a, CancellationToken ct)
    {
        var map = await GetAsync("CharacterProperties", ct) as OSDMap ?? throw new LeapError("CharacterProperties: no answer");
        var arr = new JsonArray();
        foreach (var id in map.Keys)
        {
            if (map[id] is not OSDMap m) continue;
            arr.Add(new JsonObject
            {
                ["id"] = id, ["name"] = m["name"].AsString(), ["owner"] = m["owner"].AsUUID().ToString(), ["owner_is_group"] = m["owner_is_group"].AsBoolean(),
                ["cpu_us"] = Math.Round(m["cpu_time"].AsReal()), ["shape"] = m["horizontal"].AsBoolean() ? "horizontal capsule" : "vertical capsule",
                ["length"] = Json.R(m["length"].AsReal(), 2), ["radius"] = Json.R(m["radius"].AsReal(), 2), ["position"] = Json.Arr(m["position"].AsVector3()),
            });
        }
        return new JsonObject { ["characters"] = arr, ["count"] = arr.Count };
    }

    public async Task<JsonObject> RebakeAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (resp, _) = await c.HttpCapsClient.PostAsync(Cap("NavMeshGenerationStatus"), OSDFormat.Xml, new OSDMap { ["command"] = OSD.FromString("rebuild") }, ct);
        if (!resp.IsSuccessStatusCode) throw new LeapError($"the region refused the rebake ({(int)resp.StatusCode})");
        return new JsonObject { ["requested"] = true };
    }
}
