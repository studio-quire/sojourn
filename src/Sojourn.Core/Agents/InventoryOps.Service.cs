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

    /// <summary>Things into a folder by the official viewer's UDP messages (MoveInventoryItem or MoveInventoryFolder, as
    /// LLViewerInventoryItem::updateParentOnServer sends them, stamped into the Trash as its delete does), all sent before
    /// any is waited for. On a grid with the inventory service each is done once the service shows it in that folder (the
    /// store follows then). A read that fails or names no folder shows nothing, so it's asked again (the tests' stand-in
    /// names none, and there that counts as done). After 15 s, what's still shown elsewhere is an error saying so, the
    /// store following the service; what couldn't be read at all is one saying it couldn't be asked. Without a service,
    /// the store moved at once. verb: what the ones that went were ("moved", "deleted"), for an error naming the rest.</summary>
    async Task MoveAllAsync(IReadOnlyList<InventoryBase> nodes, UUID dest, string verb, CancellationToken ct)
    {
        var c = C;
        bool trash = dest == _s.Inventory.FolderFor(FolderType.Trash);
        foreach (var n in nodes) SendMove(c, n.UUID, dest, n is InventoryFolder, stamp: trash);
        var pending = nodes.ToList();
        var shownAt = new Dictionary<UUID, UUID>();   // where the service last showed each still pending
        if (_s.InventoryMeta.Service(out bool standIn) is not null)
        {
            long start = Environment.TickCount64;
            int reads = 0;
            for (int round = 0; pending.Count > 0 && Environment.TickCount64 - start < MoveShownWithin.TotalMilliseconds; round++)
            {
                await Task.Delay(round == 0 ? 250 : round < 4 ? 500 : 1000, ct);
                foreach (var n in pending.ToList())
                {
                    reads++;
                    var (answered, parent) = await _s.InventoryMeta.ParentOnServiceAsync(n.UUID, n is InventoryFolder, ct);
                    if (parent == dest || answered && parent is null && standIn) { pending.Remove(n); Follow(c, n, dest); }
                    else if (parent is { } p) shownAt[n.UUID] = p;
                }
            }
            _s.Log.LogInformation("inventory: {Count} moved into {Dest}, shown by the service after {Ms} ms ({Reads} reads); {Left} not shown",
                nodes.Count - pending.Count, dest, Environment.TickCount64 - start, reads, pending.Count);
        }
        else foreach (var n in nodes) Follow(c, n, dest);
        if (pending.Count == 0) return;
        int went = nodes.Count - pending.Count;
        string others = went > 0 ? $" (the {went} other{(went == 1 ? " was" : "s were")} {verb})" : "";
        var still = pending.Where(n => shownAt.ContainsKey(n.UUID)).ToList();
        foreach (var n in still) Follow(c, n, shownAt[n.UUID]);
        if (still.Count > 0)
            throw new LeapError($"Second Life still had {Names(still)} where {(still.Count == 1 ? "it was" : "they were")} {MoveShownWithin.TotalSeconds:0} seconds later{others}");
        throw new LeapError($"Second Life couldn't be asked whether {Names(pending)} moved; F5 shows where things are now{others}");
    }

    static string Names(List<InventoryBase> ns) =>
        ns.Count <= 3 ? string.Join(", ", ns.Select(n => n.Name)) : $"{string.Join(", ", ns.Take(3).Select(n => n.Name))} and {ns.Count - 3} more";

    /// <summary>The store's node moved to where the grid has it (the node callers hold: read where it was before).</summary>
    static void Follow(GridClient c, InventoryBase n, UUID parent)
    {
        if (c.Inventory.Store is { } store && store.TryGetValue(n.UUID, out var live) && live != null && live.ParentUUID != parent)
        {
            live.ParentUUID = parent;
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

    /// <summary>For things outside InventoryOps that move inventory (the AO's folders into the Trash): MoveAllAsync.</summary>
    public Task MoveToAsync(IEnumerable<UUID> ids, UUID dest, CancellationToken ct) => MoveAllAsync(ids.Select(NodeOf).ToList(), dest, "moved", ct);

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
