using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Giving a folder, as the official viewer's LLGiveInventory::giveInventoryCategory does it rather than LibreMetaverse's
/// GiveFolderAsync, which fetched every item from the grid one at a time (nearly two minutes for a folder of landmarks
/// on Second Life, 2026-11-07) and then returned without a word when the folder held more than 66 things or had no
/// items directly in it, so the person was told it had gone. Here: everything under the folder from the store (its
/// folders fetched first if they aren't), the items that can be given (transferable, not worn; a link by its
/// original), counted with its folders against the viewer's limit of 42, refused in words when it's over or nothing can
/// go, and the offer sent with the viewer's bucket. Claude Opus 5.5, 2026-11-07.
/// </summary>
public sealed partial class InventoryOps
{
    /// <summary>The most things, folders included, one offer may carry: the official viewer's MAX_ITEMS
    /// (llgiveinventory.cpp, "cut down a bit" from what fits a packet; its notification says 42 too).</summary>
    public const int MaxGive = 42;

    /// <summary>What giving a folder would send: its folders (not itself), the items that can go, and those that stay
    /// (no transfer, or worn); NoCopy of the items going leave the inventory.</summary>
    public sealed record FolderGive(InventoryFolder Folder, List<InventoryFolder> Folders, List<InventoryItem> Items, int Kept, int NoCopy)
    {
        public int Count => Folders.Count + Items.Count;
    }

    /// <summary>A folder's give from the store as it stands (nothing fetched), or null for no such folder.</summary>
    public FolderGive? PlanFolderGive(UUID folderId)
    {
        var c = C;
        if (c.Inventory.Store is not { } store || !store.TryGetValue<InventoryFolder>(folderId, out var root) || root is null) return null;
        var trash = _s.Inventory.FolderFor(FolderType.Trash);
        var wornIds = WornIds();
        var folders = new List<InventoryFolder>();
        var items = new List<InventoryItem>();
        int kept = 0, noCopy = 0;
        var seen = new HashSet<UUID> { folderId };
        var open = new Stack<UUID>();
        open.Push(folderId);
        while (open.Count > 0)
        {
            List<InventoryBase> kids;
            try { kids = store.GetContents(open.Pop()); }
            catch (InventoryException) { continue; }
            foreach (var n in kids)
            {
                if (n is InventoryFolder f)
                {
                    if (f.UUID == trash || !seen.Add(f.UUID)) continue;
                    folders.Add(f);
                    open.Push(f.UUID);
                }
                else if (n is InventoryItem it)
                {
                    // (the viewer's LLGiveable: a link's type and rights are its original's)
                    var target = it.AssetType == AssetType.Link && store.TryGetValue<InventoryItem>(it.AssetUUID, out var t) && t != null ? t : it;
                    bool worn1 = target.AssetType is AssetType.Object or AssetType.Bodypart or AssetType.Clothing && wornIds.Contains(target.UUID);
                    if (worn1 || (target.Permissions.OwnerMask & PermissionMask.Transfer) == 0) { kept++; continue; }
                    items.Add(it);
                    if ((target.Permissions.OwnerMask & PermissionMask.Copy) == 0) noCopy++;
                }
            }
        }
        return new FolderGive(root, folders, items, kept, noCopy);
    }

    /// <summary>Why a folder can't be given, in words, or null (also the confirmation gate's refusal, so the person
    /// isn't asked first).</summary>
    public static string? GiveRefusal(FolderGive g)
    {
        if (g.Count > MaxGive)
            return $"{g.Folder.Name} holds {g.Count} things, counting the {g.Folders.Count} folders inside it, and Second Life gives at most "
                   + $"{MaxGive} at once; give the folders inside it one at a time, or move some of what's in it out first";
        if (g.Count == 0)
            return g.Kept > 0 ? $"nothing in {g.Folder.Name} can be given: what's in it is no-transfer or worn" : $"{g.Folder.Name} is empty";
        return null;
    }

    /// <summary>The confirmation gate's check for TesselAgent give of a folder, from the store as it stands.</summary>
    public string? GiveRefusal(JsonObject a) =>
        _s.Client is not null && a.Uuid("folder_id") is { } fid && PlanFolderGive(fid) is { } g ? GiveRefusal(g) : null;

    /// <summary>A folder offered to someone: its folders fetched if they aren't yet (the viewer refuses an incomplete
    /// folder and asks again in a minute; this waits up to 30 s), refused in words as GiveRefusal says, else the offer
    /// sent as the viewer sends it. What went, in numbers.</summary>
    public async Task<JsonObject> GiveFolderAsync(UUID folderId, UUID to, CancellationToken ct)
    {
        var c = C;
        if (_s.Inventory.Node(folderId) is not InventoryFolder) throw new LeapError("no such folder");
        bool whole;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            try { whole = await _s.Inventory.LoadSubtreeAsync(folderId, wait.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { whole = false; }
        }
        var g = PlanFolderGive(folderId) ?? throw new LeapError("no such folder");
        if (!whole) throw new LeapError($"not all of {g.Folder.Name}'s folders came from the grid just now, so it wasn't given; try again in a minute");
        if (GiveRefusal(g) is { } no) throw new LeapError(no);
        // the viewer's bucket: the folder, its folders, then the items, each a type byte and its id
        var bucket = new byte[17 * (g.Count + 1)];
        int at = 0;
        void Put(AssetType type, UUID id)
        {
            bucket[at++] = (byte)type;
            Buffer.BlockCopy(id.GetBytes(), 0, bucket, at, 16);
            at += 16;
        }
        Put(AssetType.Folder, folderId);
        foreach (var f in g.Folders) Put(AssetType.Folder, f.UUID);
        foreach (var it in g.Items)
            Put(it.AssetType == AssetType.Link && c.Inventory.Store?.TryGetValue<InventoryItem>(it.AssetUUID, out var t) == true && t != null ? t.AssetType : it.AssetType, it.UUID);
        c.Self.InstantMessage(c.Self.Name, to, g.Folder.Name, UUID.Random(), InstantMessageDialog.InventoryOffered,
            InstantMessageOnline.Online, c.Self.SimPosition, c.Network.CurrentSim?.ID ?? UUID.Zero, bucket);
        // no-copy things leave at once (the viewer forgets them as it gives)
        if (g.NoCopy > 0 && c.Inventory.Store is { } store)
            foreach (var it in g.Items)
                if ((it.Permissions.OwnerMask & PermissionMask.Copy) == 0 && it.AssetType != AssetType.Link) store.RemoveNodeFor(it);
        return new JsonObject { ["items"] = g.Items.Count, ["folders"] = g.Folders.Count, ["kept"] = g.Kept, ["no_copy"] = g.NoCopy };
    }
}
