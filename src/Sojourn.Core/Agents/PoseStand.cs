using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Firestorm's pose stand (fsfloaterposestand.cpp, fspose.cpp, app_settings/posestand.xml): the avatar held in a still
/// pose, for editing its shape and fitting worn things. On: the AO paused, everything playing stopped (objects keep
/// their permissions), optionally sat on the ground so movement doesn't break the pose (Firestorm's "lock", on by
/// default), the pose played; another pose swaps it; off: the pose stopped, stood up if it sat, the AO back. The poses
/// are Firestorm's list by asset id (on Second Life; other grids lack those assets) or any animation in the inventory.
/// The pose counts as "the agent API animating me", so in the headset the arms follow it under the `tracking` setting's
/// auto (you see the pose in a mirror). Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class PoseStand
{
    readonly AgentSession _s;
    UUID _pose;
    string _poseName = "";
    bool _locked;

    public PoseStand(AgentSession s) => _s = s;

    public bool On => _pose != UUID.Zero;

    /// <summary>posestand.xml's poses (Second Life's assets), with the names its strings give them.</summary>
    public static readonly (string Name, UUID Id)[] Poses =
    {
        ("T pose", new UUID("3481ccd5-cc6c-65ca-c466-dc9df1bf3944")),
        ("arms down, legs together", new UUID("0b4ee516-5dfd-ef4b-4ab6-300bb44cb045")),
        ("arms down, sitting", new UUID("b23c52ef-0b15-11f2-852d-8fbd33417847")),
        ("arms downward, legs apart", new UUID("e14fee5f-3735-a236-2e26-94f92f366d81")),
        ("arms downward, legs together", new UUID("2029d88f-4efc-d72d-18d1-274caf9e9bc0")),
        ("arms forward, legs apart", new UUID("cff5559e-c915-0b5e-d587-f789b5cc299a")),
        ("arms forward, legs together", new UUID("23f3163c-183b-dac0-0cdd-7811bcc3b5f1")),
        ("arms straight, legs apart", new UUID("b2f312aa-2c1e-27b8-50fe-a0fa4ff3df4e")),
        ("arms straight, sitting", new UUID("c17677b8-9fb2-8d6e-bbbe-219edce64ef4")),
        ("arms upward, legs apart", new UUID("2cfeed25-04f9-caff-f3d2-776cddea5771")),
        ("arms upward, legs together", new UUID("6e83f685-2f53-278f-6758-b529327cce09")),
    };

    public JsonObject Status() => new()
    {
        ["on"] = On,
        ["pose"] = On ? _poseName : null,
        ["pose_id"] = On ? _pose.ToString() : null,
        ["locked"] = On ? _locked : null,
        ["poses"] = new JsonArray(Poses.Select(p => (JsonNode)p.Name).ToArray()),
        ["last"] = _s.InventoryStore.Text("pose_last"),
    };

    /// <summary>A pose by Firestorm's name, an inventory animation (item id or name), or an animation asset id.</summary>
    async Task<(UUID Id, string Name)> ResolveAsync(JsonObject a, CancellationToken ct)
    {
        if (a.Uuid("item_id") is { } iid)
        {
            var item = await _s.Inventory.ItemAsync(iid, ct) ?? throw new LeapError($"no inventory item {iid}");
            if (item.AssetType == AssetType.Link && await _s.Inventory.ItemAsync(item.AssetUUID, ct) is { } t) item = t;
            if (item.AssetType != AssetType.Animation) throw new LeapError($"{item.Name} isn't an animation");
            return (item.AssetUUID, item.Name);
        }
        var name = a.Str("pose") ?? a.Str("name") ?? _s.InventoryStore.Text("pose_last") ?? Poses[0].Name;
        if (UUID.TryParse(name, out var asset)) return (asset, Poses.FirstOrDefault(p => p.Id == asset).Name ?? name);
        var known = Poses.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (known.Id != UUID.Zero) return (known.Id, known.Name);
        // an animation in the inventory by name
        var store = _s.Client?.Inventory.Store;
        if (store != null)
            foreach (var n in store.GetContents(_s.Inventory.FolderFor(FolderType.Animation)).OfType<InventoryItem>())
                if (string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase) && n.AssetType == AssetType.Animation) return (n.AssetUUID, n.Name);
        var found = await _s.Inventory.CollectAsync(_s.Inventory.FolderFor(FolderType.Root), name, null, "animatn", "EXCLUDE_LINKS", 5, ct);
        if (found["items"] is JsonObject items)
            foreach (var (_, v) in items)
                if (v is JsonObject o && string.Equals(o.Str("name"), name, StringComparison.OrdinalIgnoreCase) && UUID.TryParse(o.Str("asset_id") ?? "", out var aid))
                    return (aid, o.Str("name") ?? name);
        throw new LeapError($"no pose {name}: Firestorm's ({string.Join(", ", Poses.Select(p => p.Name))}) or an animation of mine by name or item_id");
    }

    public async Task<JsonObject> StartAsync(JsonObject a, CancellationToken ct)
    {
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        var (id, name) = await ResolveAsync(a, ct);
        if (On)
        {
            // another pose: swapped, the rest as it is
            c.Self.AnimationStop(_pose, true);
        }
        else
        {
            _s.Ao.Pause("pose", true);
            await _s.Anims.StopAsync(null, ct, keepPermissions: true);
            bool sitting = _s.Region?.AvatarById(c.Self.AgentID) is { ParentID: not 0 };
            _locked = a.Bool("lock", _s.InventoryStore.Flag("pose_lock", true)) && !sitting;
            if (_locked) c.Self.SitOnGround();
        }
        _pose = id;
        _poseName = name;
        c.Self.AnimationStart(id, true);
        _s.Anims.Started(id, name);
        _s.InventoryStore.Set("pose_last", name);
        if (a.Bool("lock") is { } l) _s.InventoryStore.Set("pose_lock", l);
        await Task.Delay(300, ct);
        var o = Status();
        if (!_s.Anims.Now().Any(p => p.Id == id)) o["note"] = "the region hasn't listed the pose yet (on a grid without that asset it never will)";
        return o;
    }

    public async Task<JsonObject> StopAsync(CancellationToken ct)
    {
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        if (!On) return Status();
        c.Self.AnimationStop(_pose, true);
        _pose = UUID.Zero;
        if (_locked) c.Self.Stand();
        _locked = false;
        await _s.Anims.StopAsync(null, ct, keepPermissions: true);
        _s.Ao.Pause("pose", false);
        return Status();
    }
}
