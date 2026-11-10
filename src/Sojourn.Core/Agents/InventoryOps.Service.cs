using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Moves and renames that wait for the grid's answer. LibreMetaverse's MoveFolder, MoveItem, UpdateFolderProperties and
/// RequestUpdateItem change the local store first and then, on Second Life, send the change to the inventory service
/// (AIS3) without waiting for it (`_ = AisClient.MoveCategoryAsync(...)`), so a refusal reached only its own logger
/// while the person was told it had gone. Renames and an item's changes go to the service as the viewer sends them (a
/// PATCH), are awaited, and only a success changes the store; a refusal is an error the person hears. Moves don't: the
/// service refuses a PATCH of parent_id (HTTP 400, every delete the blind tester tried on 0.1.0.996), and the official
/// viewer moves by its UDP messages on every grid, so moves go that way and, where there's a service, are said done once
/// it shows the thing in its new folder. Without the service (OpenSim) the UDP messages go as before; no viewer gets an
/// answer to those. In tests a stand-in for the service (SOJOURN_TEST_AIS, InventoryMeta.Service) answers instead,
/// refusing a PATCH of parent_id as Second Life does, and the UDP message follows so OpenSim's inventory matches.
/// Claude Opus 5.5, 2026-11-07; moves by the message, 2026-11-09.
/// </summary>
public sealed partial class InventoryOps
{
    static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(30);

    /// <summary>A PATCH to the inventory service, its answer's side effects (links it removed, folders' new versions)
    /// applied to the store as LibreMetaverse applies them; a refusal or no answer throws, in words.</summary>
    async Task PatchAsync(Uri cap, bool folder, UUID id, OSDMap updates, string what, CancellationToken ct)
    {
        var c = C;
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"{cap.ToString().TrimEnd('/')}/{(folder ? "category" : "item")}/{id}"));
        request.Content = new StringContent(OSDParser.SerializeLLSDXmlString(updates), System.Text.Encoding.UTF8, HttpCapsClient.LLSD_XML);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ServiceWait);
        HttpResponseMessage reply;
        try { reply = await c.HttpCapsClient.SendAsync(request, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LeapError($"Second Life's inventory service didn't answer the {what} within {ServiceWait.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException e) { throw new LeapError($"Second Life's inventory service couldn't be reached for the {what} ({e.Message})"); }
        using (reply)
        {
            if (!reply.IsSuccessStatusCode)
            {
                _s.Log.LogInformation("inventory service: {What} of {Id} answered {Status}", what, id, (int)reply.StatusCode);
                throw new LeapError($"Second Life's inventory service refused the {what} (HTTP {(int)reply.StatusCode}{(reply.ReasonPhrase is { Length: > 0 } rp ? $", {rp}" : "")})");
            }
            OSD? body = null;
            try { body = OSDParser.Deserialize(await reply.Content.ReadAsStringAsync(ct)); } catch (Exception) { }
            if (body is OSDMap m && c.Inventory.Store is { } store)
            {
                var meta = InventoryAISClient.ParseAISResponseMeta(m);
                foreach (var gone in meta.BrokenLinksRemoved.Concat(meta.ItemsRemoved).Concat(meta.CategoriesRemoved))
                    if (store.TryGetValue(gone, out var n) && n != null) store.RemoveNodeFor(n);
                foreach (var (fid, v) in meta.CategoryVersionUpdates)
                    if (store.TryGetValue<InventoryFolder>(fid, out var f)) f!.Version = v;
            }
        }
    }

    static readonly TimeSpan MoveShownWithin = TimeSpan.FromSeconds(15);

    /// <summary>A node into a folder by the official viewer's UDP message (MoveInventoryItem or MoveInventoryFolder, as
    /// LLViewerInventoryItem::updateParentOnServer sends it, stamped into the Trash as its delete does). On a grid with the
    /// inventory service, done once the service shows it in that folder (the store follows then); still elsewhere after
    /// 15 s, an error saying so, the store left as the service has it. Without one, the store moved at once.</summary>
    async Task MoveNodeAsync(InventoryBase n, UUID dest, CancellationToken ct)
    {
        var c = C;
        bool folder = n is InventoryFolder;
        bool trash = dest == _s.Inventory.FolderFor(FolderType.Trash);
        SendMove(c, n.UUID, dest, folder, stamp: trash);
        if (_s.InventoryMeta.Service(out _) is not null)
        {
            var until = DateTime.UtcNow + MoveShownWithin;
            UUID? shown = null;
            for (int i = 0; ; i++)
            {
                await Task.Delay(i == 0 ? 250 : i < 4 ? 500 : 1000, ct);
                shown = await _s.InventoryMeta.ParentOnServiceAsync(n.UUID, folder, ct);
                // (no parent_id in the answer tells nothing: the tests' stand-in holds no inventory)
                if (shown is not { } p || p == dest || DateTime.UtcNow > until) break;
            }
            if (shown is { } at && at != dest)
            {
                _s.Log.LogInformation("inventory: move of {Id} into {Dest} sent; the service still shows it in {At}", n.UUID, dest, at);
                if (c.Inventory.Store is { } st && st.TryGetValue(n.UUID, out var was) && was != null && was.ParentUUID != at)
                {
                    was.ParentUUID = at;
                    st.UpdateNodeFor(was);
                }
                throw new LeapError($"Second Life still had {n.Name} where it was {MoveShownWithin.TotalSeconds:0} seconds later");
            }
        }
        if (c.Inventory.Store is { } store && store.TryGetValue(n.UUID, out var live) && live != null)
        {
            live.ParentUUID = dest;
            store.UpdateNodeFor(live);
        }
    }

    static void SendMove(GridClient c, UUID id, UUID dest, bool folder, bool stamp)
    {
        if (folder)
            c.Network.SendPacket(new MoveInventoryFolderPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID, Stamp = stamp },
                InventoryData = [new MoveInventoryFolderPacket.InventoryDataBlock { FolderID = id, ParentID = dest }],
            });
        else
            c.Network.SendPacket(new MoveInventoryItemPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID, Stamp = stamp },
                InventoryData = [new MoveInventoryItemPacket.InventoryDataBlock { ItemID = id, FolderID = dest, NewName = Utils.EmptyBytes }],
            });
    }

    /// <summary>For things outside InventoryOps that move inventory (the AO's folders into the Trash): MoveNodeAsync.</summary>
    public Task MoveToAsync(UUID id, UUID dest, CancellationToken ct) => MoveNodeAsync(NodeOf(id), dest, ct);

    /// <summary>A folder renamed, waiting for the service where there is one (UpdateFolderProperties otherwise).</summary>
    async Task RenameFolderAsync(InventoryFolder f, string name, CancellationToken ct)
    {
        var c = C;
        if (_s.InventoryMeta.Service(out var standIn) is { } cap)
        {
            await PatchAsync(cap, true, f.UUID, new OSDMap { ["name"] = OSD.FromString(name) }, $"rename of {f.Name}", ct);
            if (c.Inventory.Store is { } store && store.TryGetValue<InventoryFolder>(f.UUID, out var live) && live != null)
            {
                live.Name = name;
                store.UpdateNodeFor(live);
            }
            if (!standIn) return;
        }
        c.Inventory.UpdateFolderProperties(f.UUID, f.ParentUUID, name, f.PreferredType);
    }

    /// <summary>An item's changed fields (already set on it) sent, waiting for the service where there is one, as
    /// LibreMetaverse's RequestUpdateItem would send them; undo puts the store's copy back if the service refuses.</summary>
    async Task UpdateItemOnGridAsync(InventoryItem item, Action undo, CancellationToken ct)
    {
        var c = C;
        if (_s.InventoryMeta.Service(out var standIn) is { } cap)
        {
            var update = (OSDMap)item.GetOSD();
            // (as RequestUpdateItems: the asset isn't the viewer's to set, a transaction's hash stands for it)
            if (update.Remove("asset_id") | update.Remove("shadow_id"))
                if (item.TransactionID != UUID.Zero) update["hash_id"] = item.TransactionID;
            try { await PatchAsync(cap, false, item.UUID, update, $"change to {item.Name}", ct); }
            catch (LeapError)
            {
                undo();
                c.Inventory.Store?.UpdateNodeFor(item);
                throw;
            }
            c.Inventory.Store?.UpdateNodeFor(item);
            if (!standIn) return;
        }
        c.Inventory.RequestUpdateItem(item);
    }

    /// <summary>A link to an item of mine in a folder, as the viewer's Paste as link (link_inventory_array: the link named
    /// as the item, with its description; a link's link goes to its original), once the grid has made it. The viewer
    /// refuses a notecard or material that has no asset yet (an empty notecard never saved), as AIS would.</summary>
    public async Task<JsonObject> LinkAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var item = await ItemAsync(a, ct);
        var folder = a.Uuid("folder_id") ?? throw new LeapError("need folder_id");
        if (NodeOf(folder) is not InventoryFolder dest) throw new LeapError("folder_id isn't a folder");
        RequireMine(folder, "link");
        RequireMine(item.UUID, "link");
        if (dest.PreferredType == FolderType.CurrentOutfit) throw new LeapError($"links in the Current Outfit folder are what's worn: wear {item.Name} instead");
        if (item.AssetUUID == UUID.Zero && item.AssetType is AssetType.Notecard or AssetType.Material)
            throw new LeapError($"{item.Name} has nothing saved in it yet, and Second Life can't link to it until it has");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ServiceWait);
        InventoryItem? link;
        try { link = await c.Inventory.CreateLinkAsync(folder, item.UUID, item.Name, item.Description, item.InventoryType, UUID.Random(), cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { link = null; }
        if (link is null) throw new LeapError($"the grid didn't make the link to {item.Name}");
        return new JsonObject { ["linked"] = true, ["link_id"] = link.UUID.ToString(), ["item_id"] = item.UUID.ToString(), ["name"] = item.Name };
    }
}
