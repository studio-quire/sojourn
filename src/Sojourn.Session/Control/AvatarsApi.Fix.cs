using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Render.World;

namespace Sojourn.Session.Control;

/// <summary>
/// SojournAvatars' fixes for avatars drawn wrong (batch B3b): the viewer's Reset Skeleton and Reset Skeleton And
/// Animations (llviewermenu.cpp: for your own, a ResetSkeleton viewer effect, type 19, so every viewer resets it too, and
/// with animations the viewer's Stop animating me; for anyone else, only here), Firestorm's Refresh Attachments (your
/// own: each worn object attached again where it is, which brings back ones the region has but never sent; anyone's:
/// asked of the region again and drawn afresh here) and Resync Animations (everyone's animations restarted together
/// here). Nothing here waits for a confirmation: nothing is spent, given, deleted or changed but how things are drawn,
/// and your own attachments are only attached again.
/// </summary>
public sealed partial class AvatarsApi
{
    void FixOps()
    {
        Op("resetSkeleton", "{id | name | none: yours, animations?: false} -> {id, name, sent?}: the viewer's Reset Skeleton: the avatar's "
            + "shape, the joints its attachments move and its physics worked out again (a body left deformed by a bad animation or worn "
            + "mesh); animations: true also restarts its animations (yours: the viewer's Stop animating me, which stands you up from a "
            + "seat). Yours is also sent, so everyone's viewer resets it; anyone else's only here", ResetSkeletonAsync);
        Op("refreshAttachments", "{id | name | none: yours} -> {id, name, attached?}: Firestorm's Refresh Attachments: yours are each "
            + "attached again where they are (attachments the region has but that never showed come back); anyone's are asked of the "
            + "region again and drawn afresh here", RefreshAttachments);
        Op("resyncAnimations", "-> {}: Firestorm's Resync Animations: every avatar's and animated object's animations restarted "
            + "together here, so people dancing the same dance are in step (only in this viewer; nothing is sent)", _ =>
            {
                _limits.Fixes.Resync();
                return new JsonObject();
            });
    }

    (UUID Id, string Name) WhoOrSelf(JsonObject a)
    {
        if (a.Str("id") is null && a.Str("name") is not { Length: > 0 })
        {
            var c = S.Client ?? throw new NotConnected("viewer not connected");
            return (c.Self.AgentID, "you");
        }
        return Who(a, self: true);
    }

    async Task<JsonNode?> ResetSkeletonAsync(JsonObject a, CancellationToken ct)
    {
        var (id, name) = WhoOrSelf(a);
        bool animations = a.Bool("animations", false);
        var o = new JsonObject { ["id"] = id.ToString(), ["name"] = name };
        if (S.Client is { } c && id == c.Self.AgentID)
        {
            // LLHUDEffectResetSkeleton::packData: the target and a reset-animations byte; the effect resets it here too
            // (originated here), and with animations the viewer's stopCurrentAnimations
            var data = new byte[17];
            id.ToBytes(data, 0);
            data[16] = (byte)(animations ? 1 : 0);
            c.Network.SendPacket(new ViewerEffectPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                Effect =
                [
                    new ViewerEffectPacket.EffectBlock
                    {
                        ID = UUID.Random(), AgentID = c.Self.AgentID, Type = ViewerEffects.ResetSkeleton, Duration = 1f,
                        Color = [0, 0, 0, 255], TypeData = data,
                    },
                ],
            });
            o["sent"] = true;
            _limits.Fixes.ResetSkeleton(id, false);
            if (animations) o["animations"] = await S.Anims.StopAsync(null, ct);
            return o;
        }
        _limits.Fixes.ResetSkeleton(id, animations);
        return o;
    }

    JsonNode? RefreshAttachments(JsonObject a)
    {
        var (id, name) = WhoOrSelf(a);
        var c = S.Client ?? throw new NotConnected("viewer not connected");
        var o = new JsonObject { ["id"] = id.ToString(), ["name"] = name };
        // the region's copy of the avatar and what it wears (its own region's, where it's drawn)
        foreach (var sim in c.Network.Simulators.ToArray())
        {
            var av = sim.ObjectsAvatars.Values.FirstOrDefault(x => x.ID == id);
            if (av == null) continue;
            var worn = sim.ObjectsPrimitives.Values.Where(p => p.ParentID == av.LocalID).ToList();
            if (id == c.Self.AgentID)
            {
                // LLAttachmentsMgr::refreshAttachments: each worn object (not temporary ones) attached again at its point,
                // added beside what's there
                int n = 0;
                foreach (var p in worn)
                {
                    var item = AgentInventory.AttachItemId(p);
                    if (item == UUID.Zero) continue;
                    var point = p.PrimData.AttachmentPoint;
                    if (c.Inventory.Store?.Contains(item) == true && c.Inventory.Store[item] is InventoryItem it)
                        c.Appearance.Attach(it, point, replace: false);
                    else
                        c.Appearance.Attach(item, c.Self.AgentID, "", "", Permissions.NoPermissions, 0, point, replace: false);
                    n++;
                }
                o["attached"] = n;
            }
            else
            {
                var ids = new List<uint> { av.LocalID };
                ids.AddRange(worn.Select(p => p.LocalID));
                foreach (var p in sim.ObjectsPrimitives.Values)
                    if (worn.Any(w => w.LocalID == p.ParentID)) ids.Add(p.LocalID);
                c.Objects.RequestObjects(sim, ids);
                o["asked"] = ids.Count;
            }
            break;
        }
        _limits.Fixes.Refresh(id);
        return o;
    }
}
