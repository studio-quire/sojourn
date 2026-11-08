using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Moves and renames that wait for the grid's answer. LibreMetaverse's MoveFolder, MoveItem, UpdateFolderProperties and
/// RequestUpdateItem change the local store first and then, on Second Life, send the change to the inventory service
/// (AIS3) without waiting for it (`_ = AisClient.MoveCategoryAsync(...)`), so a refusal reached only its own logger
/// while the person was told it had gone. Here the change goes to the service as LibreMetaverse would send it (a PATCH
/// of parent_id or name), is awaited, and only a success changes the store; a refusal is an error the person hears.
/// Without the service (OpenSim) the UDP messages go as before; no viewer gets an answer to those. In tests a
/// stand-in for the service (SOJOURN_TEST_AIS, InventoryMeta.Service) answers instead, and the UDP message follows so
/// OpenSim's inventory matches. Claude Opus 5.5, 2026-11-07.
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

    /// <summary>A node into a folder: on a grid with the inventory service, once it has said yes (the store follows
    /// then); without one, the viewer's UDP message, the store moved at once.</summary>
    async Task MoveNodeAsync(InventoryBase n, UUID dest, CancellationToken ct)
    {
        var c = C;
        bool folder = n is InventoryFolder;
        if (_s.InventoryMeta.Service(out var standIn) is { } cap)
        {
            await PatchAsync(cap, folder, n.UUID, new OSDMap { ["parent_id"] = OSD.FromUUID(dest) }, folder ? $"move of the folder {n.Name}" : $"move of {n.Name}", ct);
            if (c.Inventory.Store is { } store && store.TryGetValue(n.UUID, out var live) && live != null)
            {
                live.ParentUUID = dest;
                store.UpdateNodeFor(live);
            }
            if (!standIn) return;
        }
        if (folder) c.Inventory.MoveFolder(n.UUID, dest); else c.Inventory.MoveItem(n.UUID, dest);
    }

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
