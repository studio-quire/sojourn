using System.Text;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.StructuredData;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Inventory management the viewer does through its inventory window (sojourn's TesselInventory API): folders,
/// moving, renaming, permissions, trash, copying (the Library too), notecards and landmarks. Written by Claude Opus 5.5,
/// 2026-10-16. Protocol paths: LibreMetaverse picks AIS on Second Life and the UDP messages on OpenSim, except where
/// it doesn't wait for AIS's answer: moves, renames and gives are ours (InventoryOps.Service.cs, InventoryOps.Gives.cs).
/// </summary>
public sealed partial class InventoryOps
{
    readonly AgentSession _s;
    public InventoryOps(AgentSession s) => _s = s;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    // ---------- the Library ----------

    public UUID LibraryRoot => C.Inventory.Store?.LibraryFolder?.UUID ?? UUID.Zero;
    public UUID LibraryOwner => C.Network.LoginResponseData?.LibraryOwner ?? UUID.Zero;

    /// <summary>True when the node sits under the Library root (walks parents in the store).</summary>
    public bool InLibrary(UUID id)
    {
        var store = C.Inventory.Store;
        var lib = LibraryRoot;
        if (store is null || lib == UUID.Zero) return false;
        for (var i = 0; i < 64 && id != UUID.Zero; i++)
        {
            if (id == lib) return true;
            if (!store.TryGetValue(id, out var n)) return false;
            id = n.ParentUUID;
        }
        return false;
    }

    public JsonObject Library() => new()
    {
        ["root_id"] = LibraryRoot.ToString(),
        ["owner_id"] = LibraryOwner.ToString(),
    };

    // ---------- lookups ----------

    public async Task<InventoryItem> ItemAsync(JsonObject a, CancellationToken ct, string key = "item_id")
    {
        var id = a.Uuid(key) ?? a.Uuid("id") ?? throw new LeapError($"need {key}");
        var item = await _s.Inventory.ItemAsync(id, ct) ?? throw new LeapError($"no inventory item {id}");
        if (item.AssetType == AssetType.Link && _s.Inventory.Node(item.AssetUUID) is InventoryItem target) return target;
        return item;
    }

    InventoryBase NodeOf(UUID id) => _s.Inventory.Node(id) ?? throw new LeapError($"no inventory item or folder {id}");

    static readonly FolderType[] Protected =
    {
        FolderType.Root, FolderType.Trash, FolderType.LostAndFound, FolderType.CurrentOutfit, FolderType.MyOutfits,
        FolderType.Texture, FolderType.Sound, FolderType.CallingCard, FolderType.Landmark, FolderType.Clothing,
        FolderType.Object, FolderType.Notecard, FolderType.LSLText, FolderType.BodyPart, FolderType.Snapshot,
        FolderType.Animation, FolderType.Gesture, FolderType.Favorites, FolderType.Mesh, FolderType.Inbox,
        FolderType.Outbox, FolderType.Settings, FolderType.Material,
    };

    static bool IsSystem(InventoryFolder f) => Array.IndexOf(Protected, f.PreferredType) >= 0;

    // ---------- protected folders (Firestorm's FSProtectedFolders and LockAOFolders) ----------

    /// <summary>The folders the person protected (Firestorm's "Protect folder"): they can't be deleted, moved or renamed
    /// until unprotected. Kept per account.</summary>
    public HashSet<UUID> ProtectedFolders() => _s.InventoryStore.Ids("protected_folders");

    /// <summary>Why a node can't be moved, renamed or deleted (null: it can): a system folder, a protected folder, or a
    /// part of the AO's folder while it's locked.</summary>
    public string? Locked(InventoryBase n)
    {
        if (n is InventoryFolder f && IsSystem(f)) return $"{f.Name} is a system folder";
        if (n is InventoryFolder pf && ProtectedFolders().Contains(pf.UUID))
            return $"{pf.Name} is protected (SojournInventory unprotect lets it be changed)";
        if (_s.Ao.Protects(n.UUID))
            return $"{n.Name} is part of the AO's folder, locked as Firestorm locks it (the AO's own ops change it; the ao_lock_folders setting unlocks it)";
        return null;
    }

    /// <summary>For the confirmation gate: why TesselInventory's delete, move or rename would refuse these (so the
    /// person isn't asked first), or null.</summary>
    public string? Refusal(string op, JsonObject a)
    {
        if (_s.Client is null) return null;
        var ids = op switch
        {
            "delete" or "move" => a.UuidList("ids").Concat(a.Uuid("id") is { } one ? new[] { one } : Array.Empty<UUID>()).ToList(),
            "update" when a.Has("name") => (a.Uuid("id") ?? a.Uuid("item_id") ?? a.Uuid("folder_id")) is { } u ? new List<UUID> { u } : new(),
            _ => new List<UUID>(),
        };
        foreach (var id in ids)
            if (_s.Inventory.Node(id) is { } n && Locked(n) is { } why)
                return $"{why}; nothing was " + (op == "delete" ? "deleted" : op == "move" ? "moved" : "renamed");
        if (op == "delete" && WornRefusal(Worn(ids)) is { } worn) return $"{worn}; nothing was deleted";
        return null;
    }

    /// <summary>The items worn now: attached in this region (by the region's word), or linked from the outfit.</summary>
    HashSet<UUID> WornIds()
    {
        var c = C;
        var worn = new HashSet<UUID>();
        if (_s.Region is { } w && w.Avatars.Values.FirstOrDefault(a => a.ID == c.Self.AgentID) is { } me)
            foreach (var p in w.Prims.Values)
                if (p.ParentID == me.LocalID && AgentInventory.AttachItemId(p) is var aid && aid != UUID.Zero) worn.Add(aid);
        var cof = c.Inventory.FindFolderForType(FolderType.CurrentOutfit);
        if (c.Inventory.Store is { } store && cof != UUID.Zero)
            foreach (var l in store.GetContents(cof).OfType<InventoryItem>()) worn.Add(l.AssetUUID);
        return worn;
    }

    /// <summary>What deleting these would take that's worn, by the official viewer's and Firestorm's rule: the worn
    /// items among them and in their folders (the Trash aside); links don't count (deleting one takes nothing off),
    /// except the Current Outfit's own, which are what's worn.</summary>
    public (List<InventoryItem> BodyParts, List<InventoryItem> Others, List<InventoryItem> OutfitLinks) Worn(IEnumerable<UUID> ids)
    {
        var c = C;
        var store = c.Inventory.Store;
        var cof = c.Inventory.FindFolderForType(FolderType.CurrentOutfit);
        var trash = c.Inventory.FindFolderForType(FolderType.Trash);
        var worn = WornIds();

        var (body, others, links) = (new List<InventoryItem>(), new List<InventoryItem>(), new List<InventoryItem>());
        var seen = new HashSet<UUID>();
        void Visit(InventoryBase n, int depth)
        {
            if (!seen.Add(n.UUID) || depth > 64) return;
            if (n is InventoryFolder f)
            {
                if (store is null || f.UUID == trash || f.UUID == cof) return;
                foreach (var k in store.GetContents(f.UUID)) Visit(k, depth + 1);
            }
            else if (n is InventoryItem it)
            {
                if (it.AssetType is AssetType.Link or AssetType.LinkFolder)
                {
                    if (it.ParentUUID == cof && worn.Contains(it.AssetUUID)) links.Add(it);
                }
                else if (worn.Contains(it.UUID)) (it.AssetType == AssetType.Bodypart ? body : others).Add(it);
            }
        }
        foreach (var id in ids)
            if (_s.Inventory.Node(id) is { } n) Visit(n, 0);
        return (body, others, links);
    }

    /// <summary>Why worn things stop a delete (null: they don't): worn body parts are always replaced, never deleted
    /// (an avatar always wears a shape, skin, hair and eyes), and the Current Outfit's links come off by taking them
    /// off. Other worn items are taken off first.</summary>
    static string? WornRefusal((List<InventoryItem> BodyParts, List<InventoryItem> Others, List<InventoryItem> OutfitLinks) w)
    {
        if (w.BodyParts.Count > 0)
            return w.BodyParts.Count == 1
                ? $"{w.BodyParts[0].Name} is a body part you're wearing (you always wear a shape, skin, hair and eyes): wear other {Appearance.Slots.TypeName((WearableType)(w.BodyParts[0].Flags & 0xFF))} first"
                : $"{string.Join(", ", w.BodyParts.Select(b => b.Name))} are body parts you're wearing (you always wear a shape, skin, hair and eyes): wear others first";
        if (w.OutfitLinks.Count > 0)
            return $"{w.OutfitLinks[0].Name} is in your Current Outfit, worn: take it off instead";
        return null;
    }

    /// <summary>For a delete's confirmation: the worn items it takes off first, or null.</summary>
    public string? WornNote(JsonObject a)
    {
        if (_s.Client is null) return null;
        var ids = a.UuidList("ids");
        if (a.Uuid("id") is { } one) ids.Add(one);
        var others = Worn(ids).Others;
        if (others.Count == 0) return null;
        var names = others.Count <= 3 ? string.Join(", ", others.Select(o => o.Name)) : $"{others.Count} items";
        return $"Worn: {names}. Taken off first.";
    }

    public JsonObject Protect(JsonObject a, bool on)
    {
        var id = a.Uuid("folder_id") ?? a.Uuid("id") ?? throw new LeapError("need folder_id");
        if (NodeOf(id) is not InventoryFolder f) throw new LeapError($"{id} isn't a folder");
        RequireMine(id, on ? "protect" : "unprotect");
        if (on && IsSystem(f)) throw new LeapError($"{f.Name} is a system folder: always protected");
        var set = ProtectedFolders();
        if (on) set.Add(id); else set.Remove(id);
        _s.InventoryStore.SetIds("protected_folders", set);
        return ProtectedJson();
    }

    public JsonObject ProtectedJson()
    {
        var arr = new JsonArray();
        foreach (var id in ProtectedFolders())
            arr.Add(new JsonObject { ["id"] = id.ToString(), ["name"] = (_s.Inventory.Node(id) as InventoryFolder)?.Name ?? "(not loaded or gone)" });
        return new JsonObject { ["protected"] = arr };
    }

    public static FolderType FolderForAsset(AssetType t) => t switch
    {
        AssetType.Texture => FolderType.Texture,
        AssetType.Sound => FolderType.Sound,
        AssetType.CallingCard => FolderType.CallingCard,
        AssetType.Landmark => FolderType.Landmark,
        AssetType.Clothing => FolderType.Clothing,
        AssetType.Object => FolderType.Object,
        AssetType.Notecard => FolderType.Notecard,
        AssetType.LSLText or AssetType.LSLBytecode => FolderType.LSLText,
        AssetType.Bodypart => FolderType.BodyPart,
        AssetType.Animation => FolderType.Animation,
        AssetType.Gesture => FolderType.Gesture,
        AssetType.Mesh => FolderType.Mesh,
        AssetType.Settings => FolderType.Settings,
        AssetType.Material => FolderType.Material,
        _ => FolderType.Root,
    };

    void RequireMine(UUID id, string what)
    {
        if (InLibrary(id)) throw new LeapError($"{what}: that's in the Library, which is read-only; copy it first (TesselInventory copy)");
    }

    // ---------- folders and items ----------

    public JsonObject CreateFolder(JsonObject a)
    {
        var c = C;
        var name = a.Str("name");
        if (string.IsNullOrWhiteSpace(name)) throw new LeapError("need name");
        var parent = a.Uuid("parent_id") ?? _s.Inventory.FolderFor(FolderType.Root);
        if (NodeOf(parent) is not InventoryFolder) throw new LeapError("parent_id isn't a folder");
        RequireMine(parent, "createFolder");
        var id = c.Inventory.CreateFolder(parent, name.Trim());
        return new JsonObject { ["folder_id"] = id.ToString(), ["name"] = name.Trim(), ["parent_id"] = parent.ToString() };
    }

    public async Task<JsonObject> MoveAsync(JsonObject a, CancellationToken ct)
    {
        var dest = a.Uuid("folder_id") ?? throw new LeapError("need folder_id (where to)");
        if (NodeOf(dest) is not InventoryFolder) throw new LeapError("folder_id isn't a folder");
        RequireMine(dest, "move");
        var ids = a.UuidList("ids");
        if (a.Uuid("id") is { } one && !ids.Contains(one)) ids.Add(one);
        if (ids.Count == 0) throw new LeapError("need id or ids");
        var moved = new JsonArray();
        foreach (var id in ids)
        {
            RequireMine(id, "move");
            switch (NodeOf(id))
            {
                case InventoryFolder f:
                    if (Locked(f) is { } why) throw new LeapError($"{why}; it stays where it is" + Before(moved.Count, "moved"));
                    if (id == dest || IsUnder(dest, id)) throw new LeapError($"can't move {f.Name} into itself");
                    await Moved(f, dest, moved.Count, "moved", ct);
                    break;
                case InventoryItem it:
                    if (Locked(it) is { } whyItem) throw new LeapError($"{whyItem}; it stays where it is" + Before(moved.Count, "moved"));
                    await Moved(it, dest, moved.Count, "moved", ct);
                    break;
                default:
                    throw new LeapError($"no item or folder {id} in my inventory" + (moved.Count > 0 ? $" (the {moved.Count} before it were moved)" : ""));
            }
            moved.Add(id.ToString());
        }
        return new JsonObject { ["moved"] = moved, ["folder_id"] = dest.ToString() };
    }

    static string Before(int done, string verb) => done > 0 ? $" (the {done} before it were {verb})" : "";

    /// <summary>MoveNodeAsync, a refusal saying what had gone before it.</summary>
    async Task Moved(InventoryBase n, UUID dest, int before, string verb, CancellationToken ct)
    {
        try { await MoveNodeAsync(n, dest, ct); }
        catch (LeapError e) when (before > 0) { throw new LeapError(e.Message + Before(before, verb)); }
    }

    bool IsUnder(UUID node, UUID ancestor)
    {
        var store = C.Inventory.Store;
        for (var i = 0; i < 64 && node != UUID.Zero && store is not null; i++)
        {
            if (!store.TryGetValue(node, out var n)) return false;
            if (n.ParentUUID == ancestor) return true;
            node = n.ParentUUID;
        }
        return false;
    }

    /// <summary>Rename / redescribe an item or folder; set an item's next-owner (and everyone/group) permissions.</summary>
    public async Task<JsonObject> UpdateAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var id = a.Uuid("id") ?? a.Uuid("item_id") ?? a.Uuid("folder_id") ?? throw new LeapError("need id");
        RequireMine(id, "update");
        if (NodeOf(id) is InventoryFolder f)
        {
            if (a.Has("name") && Locked(f) is { } why) throw new LeapError($"{why}; it keeps its name");
            var name = a.Str("name") ?? throw new LeapError("a folder only has a name to change");
            await RenameFolderAsync(f, name, ct);
            return new JsonObject { ["folder_id"] = id.ToString(), ["name"] = name };
        }
        var item = await _s.Inventory.ItemAsync(id, ct) ?? throw new LeapError($"no inventory item {id}");
        if (a.Has("name") && _s.Ao.Protects(item.UUID) && Locked(item) is { } whyItem) throw new LeapError($"{whyItem}; it keeps its name");
        var (wasName, wasDesc, wasPerms) = (item.Name, item.Description, item.Permissions);
        if (a.Str("name") is { } n) item.Name = n;
        if (a.Str("desc") is { } d) item.Description = d;
        var p = item.Permissions;
        var owner = p.OwnerMask;
        PermissionMask Clip(PermissionMask m) => m & owner;  // can't give more than I have
        if (a.Str("next_perms") is { } np)
        {
            if ((owner & PermissionMask.Modify) == 0) throw new LeapError("next-owner permissions need modify rights on the item");
            p.NextOwnerMask = Clip(ObjectViews.PermMask(np) | PermissionMask.Move);
        }
        if (a.Bool("everyone_copy") is { } ec) p.EveryoneMask = ec ? Clip(PermissionMask.Copy) : p.EveryoneMask & ~PermissionMask.Copy;
        if (a.Str("group_perms") is { } gp) p.GroupMask = Clip(ObjectViews.PermMask(gp));
        item.Permissions = p;
        await UpdateItemOnGridAsync(item, () => (item.Name, item.Description, item.Permissions) = (wasName, wasDesc, wasPerms), ct);
        await Task.Delay(300, ct);
        return new JsonObject
        {
            ["item_id"] = item.UUID.ToString(), ["name"] = item.Name, ["desc"] = item.Description,
            ["perms"] = PermsJson(item.Permissions),
        };
    }

    public static JsonObject PermsJson(Permissions p) => new()
    {
        ["base"] = ObjectViews.PermLetters(p.BaseMask), ["owner"] = ObjectViews.PermLetters(p.OwnerMask),
        ["group"] = ObjectViews.PermLetters(p.GroupMask), ["everyone"] = ObjectViews.PermLetters(p.EveryoneMask),
        ["next"] = ObjectViews.PermLetters(p.NextOwnerMask),
    };

    /// <summary>Delete = to Trash (as the viewer does); purge = gone for good.</summary>
    public async Task<JsonObject> DeleteAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var ids = a.UuidList("ids");
        if (a.Uuid("id") is { } one && !ids.Contains(one)) ids.Add(one);
        if (ids.Count == 0) throw new LeapError("need id or ids");
        var purge = a.Bool("purge", false);
        var trash = _s.Inventory.FolderFor(FolderType.Trash);
        // every refusal before anything changes (taking off included)
        foreach (var id in ids)
        {
            RequireMine(id, "delete");
            if (Locked(NodeOf(id)) is { } why) throw new LeapError($"{why}; it can't be deleted");
        }
        // worn things as the official viewer has them: body parts refused, other worn items taken off first
        var worn = Worn(ids);
        if (WornRefusal(worn) is { } wornWhy) throw new LeapError($"{wornWhy}; nothing was deleted");
        if (worn.Others.Count > 0) await _s.Wardrobe.TakeOffAsync(c, worn.Others.Select(i => i.UUID), ct);
        var done = new JsonArray();
        foreach (var id in ids)
        {
            switch (NodeOf(id))
            {
                case InventoryFolder f:
                    if (Locked(f) is { } why) throw new LeapError($"{why}; it can't be deleted" + Before(done.Count, "deleted"));
                    if (purge) await Purged(f, c.Inventory.RemoveFolderAsync(id, ct), done.Count); else await Moved(f, trash, done.Count, "deleted", ct);
                    break;
                case InventoryItem it:
                    if (Locked(it) is { } whyItem) throw new LeapError($"{whyItem}; it can't be deleted" + Before(done.Count, "deleted"));
                    if (purge) await Purged(it, c.Inventory.RemoveItemAsync(id, ct), done.Count); else await Moved(it, trash, done.Count, "deleted", ct);
                    break;
                default:
                    throw new LeapError($"no item or folder {id} in my inventory" + (done.Count > 0 ? $" (the {done.Count} before it were deleted)" : ""));
            }
            done.Add(id.ToString());
        }
        return new JsonObject { [purge ? "purged" : "trashed"] = done };
    }

    /// <summary>A purge awaited: LibreMetaverse's RemoveItemAsync and RemoveFolderAsync only log the service's refusal,
    /// leaving the node in the store, so a node still there afterwards is one that wasn't deleted.</summary>
    async Task Purged(InventoryBase n, Task removing, int before)
    {
        await removing;
        if (C.Inventory.Store?.Contains(n.UUID) == true)
            throw new LeapError($"Second Life's inventory service didn't delete {n.Name}; it's still in the Trash" + Before(before, "deleted"));
    }

    public async Task<JsonObject> EmptyTrashAsync(CancellationToken ct)
    {
        // not LibreMetaverse's EmptyTrash: without the inventory service it names every item in one packet, which throws on
        // a Trash of any size, and with the service a failure is only logged. This asks for the folder's descendants to go
        // and looks at what's left, so a Trash that didn't empty isn't said to have. Having succeeded, LibreMetaverse drops
        // the Trash folder itself from the store too, after which looking for it by type finds the root (and everything
        // is taken to be in the Trash), so it's put back
        var c = C;
        var trash = _s.Inventory.FolderFor(FolderType.Trash);
        var store = c.Inventory.Store ?? throw new LeapError("the inventory isn't loaded yet");
        if (!store.TryGetValue<InventoryFolder>(trash, out var folder) || folder.PreferredType != FolderType.Trash) throw new LeapError("the Trash folder isn't known yet");
        await c.Inventory.RemoveDescendantsAsync(trash, ct);
        if (store.Contains(trash))
        {
            if (store.GetContents(trash).Count > 0) throw new LeapError("the grid didn't empty the Trash");
        }
        else store.UpdateNodeFor(folder);
        return new JsonObject();
    }

    /// <summary>Copy an item or a whole folder (mine or the Library's) into my inventory.</summary>
    public async Task<JsonObject> CopyAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var id = a.Uuid("id") ?? a.Uuid("item_id") ?? a.Uuid("folder_id") ?? throw new LeapError("need id");
        await _s.Inventory.EnsureParentLoadedAsync(id, ct);
        var node = NodeOf(id);
        var dest = a.Uuid("dest_id");
        if (dest is { } d) { if (NodeOf(d) is not InventoryFolder) throw new LeapError("dest_id isn't a folder"); RequireMine(d, "copy"); }
        if (node is InventoryFolder f)
        {
            var into = dest ?? _s.Inventory.FolderFor(FolderType.Root);
            var (folderId, count) = await CopyFolderAsync(f, into, a.Str("name") ?? f.Name, 0, ct);
            return new JsonObject { ["folder_id"] = folderId.ToString(), ["items"] = count };
        }
        var item = (InventoryItem)node;
        if ((item.Permissions.OwnerMask & PermissionMask.Copy) == 0 && !InLibrary(id))
            throw new LeapError($"{item.Name} is no-copy; move it instead");
        var folder = dest ?? _s.Inventory.FolderFor(FolderForAsset(item.AssetType));
        var copy = await CopyItemAsync(item, folder, a.Str("name") ?? item.Name, ct);
        var j = _s.Inventory.ItemJson(copy);
        j["item_id"] = copy.UUID.ToString();
        return j;
    }

    async Task<InventoryItem> CopyItemAsync(InventoryItem item, UUID folder, string name, CancellationToken ct)
    {
        var c = C;
        var owner = InLibrary(item.UUID) ? LibraryOwner : c.Self.AgentID;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var made = await c.Inventory.CopyItemAsync(item.UUID, folder, name, owner, cts.Token);
        return made as InventoryItem ?? throw new LeapError($"the copy of {item.Name} didn't arrive");
    }

    async Task<(UUID, int)> CopyFolderAsync(InventoryFolder src, UUID into, string name, int depth, CancellationToken ct)
    {
        if (depth > 16) throw new LeapError("folders nested too deep");
        var c = C;
        await _s.Inventory.EnsureLoadedAsync(src.UUID, ct);
        var made = c.Inventory.CreateFolder(into, name);
        var count = 0;
        foreach (var n in c.Inventory.Store!.GetContents(src.UUID).ToList())
        {
            if (n is InventoryFolder sub) count += (await CopyFolderAsync(sub, made, sub.Name, depth + 1, ct)).Item2;
            else if (n is InventoryItem it && (InLibrary(it.UUID) || (it.Permissions.OwnerMask & PermissionMask.Copy) != 0))
            {
                if (it.AssetType is AssetType.Link or AssetType.LinkFolder) continue;
                await CopyItemAsync(it, made, it.Name, ct);
                count++;
            }
        }
        return (made, count);
    }

    // ---------- notecards ----------

    public async Task<JsonObject> CreateNotecardAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var name = a.Str("name") ?? "New Note";
        var folder = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(FolderType.Notecard);
        RequireMine(folder, "createNotecard");
        // without next_perms, the person's Default Permissions for notecards (SojournBuild setDefaultPerms)
        var next = a.Str("next_perms") is { } np ? PermissionMask.Move | ObjectViews.PermMask(np) : _s.Ownership.DefaultsFor("notecards").NextMask;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var item = await c.Inventory.CreateItemAsync(folder, name, a.Str("desc") ?? "", AssetType.Notecard, UUID.Zero,
            InventoryType.Notecard, next, cts.Token) ?? throw new LeapError("the notecard item wasn't created");
        var text = a.Str("text") ?? "";
        var asset = await SaveNotecardAsync(item, NotecardFile.Asset(text), cts.Token);
        var j = _s.Inventory.ItemJson(item);
        j["asset_id"] = asset.ToString();
        j["item_id"] = item.UUID.ToString();
        return j;
    }

    async Task<UUID> SaveNotecardAsync(InventoryItem item, byte[] content, CancellationToken ct)
    {
        var c = C;
        var (ok, status, _, asset) = await c.Inventory.RequestUploadNotecardAssetAsync(content, item.UUID, ct);
        if (!ok) throw new LeapError($"saving the notecard failed: {status}");
        if (asset != UUID.Zero) item.AssetUUID = asset;
        return asset;
    }

    public async Task<JsonObject> WriteNotecardAsync(JsonObject a, CancellationToken ct)
    {
        var item = await ItemAsync(a, ct);
        RequireMine(item.UUID, "writeNotecard");
        if (item.AssetType != AssetType.Notecard) throw new LeapError($"{item.Name} isn't a notecard");
        if ((item.Permissions.OwnerMask & PermissionMask.Modify) == 0) throw new LeapError($"{item.Name} is no-modify");
        var text = a.Str("text") ?? throw new LeapError("need text");
        // read first, so what's embedded stays; if the card can't be read nothing is saved, rather than risk losing it
        NotecardFile was;
        try { was = item.AssetUUID == UUID.Zero ? NotecardFile.Empty : await ReadFileAsync(item, ct); }
        catch (Exception e) when (e is LeapError or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new LeapError($"couldn't read {item.Name} just now, and saving over it unread could lose what's embedded in it");
        }
        if (a.Bool("append", false)) text = was.RawText + text;
        var asset = await SaveNotecardAsync(item, was.Saved(text, out var moved), ct);
        var o = new JsonObject { ["item_id"] = item.UUID.ToString(), ["asset_id"] = asset.ToString(), ["length"] = text.Length };
        if (was.Items.Count > 0) o["embedded_kept"] = was.Items.Count;
        if (moved.Count > 0) o["moved_to_end"] = new JsonArray(moved.Select(m => (JsonNode)m).ToArray());
        return o;
    }

    public async Task<JsonObject> ReadNotecardAsync(JsonObject a, CancellationToken ct)
    {
        var item = await ItemAsync(a, ct);
        if (item.AssetType != AssetType.Notecard) throw new LeapError($"{item.Name} isn't a notecard");
        var file = await ReadFileAsync(item, ct);
        var o = new JsonObject { ["item_id"] = item.UUID.ToString(), ["name"] = item.Name, ["text"] = file.Readable };
        if (file.Items.Count > 0)
        {
            var arr = new JsonArray();
            for (int i = 0; i < file.Items.Count; i++)
            {
                var e = file.Items[i];
                arr.Add(new JsonObject { ["item_id"] = e.UUID.ToString(), ["name"] = e.Name, ["type"] = AgentInventory.AssetName(e.AssetType), ["kind"] = AgentInventory.AssetTypeLabel(e.AssetType), ["marker"] = file.Marker(i) });
            }
            o["embedded"] = arr;
        }
        return o;
    }

    /// <summary>Copies an item embedded in a notecard (a landmark in a welcome card, say) into my inventory.</summary>
    public async Task<JsonObject> CopyFromNotecardAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var card = await ItemAsync(a, ct);
        if (card.AssetType != AssetType.Notecard) throw new LeapError($"{card.Name} isn't a notecard");
        var embedded = (await ReadFileAsync(card, ct)).Items;
        var want = a.Uuid("embedded_id");
        var name = a.Str("name");
        var item = embedded.FirstOrDefault(e => e.UUID == want || (want is null && name is not null && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                   ?? throw new LeapError(embedded.Count == 0 ? $"{card.Name} has nothing embedded" : "no such embedded item (readNotecard lists them)");
        var folder = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(FolderForAsset(item.AssetType));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        // the capability as the viewer posts it: LibreMetaverse 3.2.0's call posts an empty body (its map, turned into a
        // string), which fails, then falls back on a UDP packet regions may not handle
        await _s.Inventory.EnsureLoadedAsync(folder, cts.Token);
        InventoryItem? New(IEnumerable<UUID> had) => c.Inventory.Store!.GetContents(folder).OfType<InventoryItem>()
            .FirstOrDefault(i => i.AssetType == item.AssetType && i.Name == item.Name && !had.Contains(i.UUID));
        var had = c.Inventory.Store!.GetContents(folder).Select(n => n.UUID).ToHashSet();
        InventoryItem? made = null;
        try
        {
            if (c.Network.CurrentSim?.Caps?.CapabilityURI("CopyInventoryFromNotecard") is { } cap)
            {
                var (resp, _) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, new OSDMap
                {
                    ["notecard-id"] = OSD.FromUUID(card.UUID), ["object-id"] = OSD.FromUUID(UUID.Zero), ["item-id"] = OSD.FromUUID(item.UUID),
                    ["folder-id"] = OSD.FromUUID(folder), ["callback-id"] = OSD.FromInteger(0),
                }, cts.Token);
                if (!resp.IsSuccessStatusCode) throw new LeapError($"{item.Name} didn't copy: the grid refused it (it needs copy and transfer rights)");
            }
            else _ = c.Inventory.RequestCopyItemFromNotecardAsync(UUID.Zero, card.UUID, folder, item.UUID, cts.Token);
            // it lands by BulkUpdateInventory, by UDP or the event queue, and only the store hears of both
            while ((made = New(had)) == null) await Task.Delay(200, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new LeapError($"{item.Name} didn't arrive (it needs copy and transfer rights)"); }
        var j = _s.Inventory.ItemJson(made);
        j["item_id"] = made.UUID.ToString();
        return j;
    }

    /// <summary>A notecard's text by its item (or a link to it): for the AO's ZHAO-II notecards.</summary>
    public async Task<(string Text, List<InventoryItem> Embedded)> NotecardTextAsync(UUID id, CancellationToken ct)
    {
        var item = await _s.Inventory.ItemAsync(id, ct) ?? throw new LeapError($"no inventory item {id}");
        if (item.AssetType == AssetType.Link && await _s.Inventory.ItemAsync(item.AssetUUID, ct) is { } target) item = target;
        if (item.AssetType != AssetType.Notecard) throw new LeapError($"{item.Name} isn't a notecard");
        var file = await ReadFileAsync(item, ct);
        return (file.Readable, file.Items);
    }

    async Task<NotecardFile> ReadFileAsync(InventoryItem item, CancellationToken ct)
    {
        var c = C;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var owner = InLibrary(item.UUID) ? LibraryOwner : item.OwnerID;
        var asset = await c.Assets.RequestInventoryAssetAsync(item.AssetUUID, item.UUID, UUID.Zero, owner, AssetType.Notecard, true, UUID.Random(), cts.Token)
                    ?? throw new LeapError("the notecard's text didn't arrive");
        return NotecardFile.Parse(asset.AssetData);
    }

    // ---------- scripts in my inventory ----------

    public async Task<JsonObject> CreateScriptAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var name = a.Str("name") ?? "New Script";
        var folder = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(FolderType.LSLText);
        RequireMine(folder, "createScript");
        var next = a.Str("next_perms") is { } np ? PermissionMask.Move | ObjectViews.PermMask(np) : _s.Ownership.DefaultsFor("scripts").NextMask;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var item = await c.Inventory.CreateItemAsync(folder, name, a.Str("desc") ?? "", AssetType.LSLText, UUID.Zero,
            InventoryType.LSL, next, cts.Token) ?? throw new LeapError("the script item wasn't created");
        var o = new JsonObject { ["item_id"] = item.UUID.ToString(), ["name"] = item.Name };
        if (a.Str("text") is { } text) foreach (var (k, v) in await SaveScriptAsync(item, text, a.Str("target") ?? "mono", cts.Token)) o[k] = v?.DeepClone();
        return o;
    }

    public async Task<JsonObject> WriteScriptAsync(JsonObject a, CancellationToken ct)
    {
        var item = await ItemAsync(a, ct);
        RequireMine(item.UUID, "writeScript");
        if (item.AssetType != AssetType.LSLText) throw new LeapError($"{item.Name} isn't a script");
        if ((item.Permissions.OwnerMask & PermissionMask.Modify) == 0) throw new LeapError($"{item.Name} is no-modify");
        var o = await SaveScriptAsync(item, a.Str("text") ?? throw new LeapError("need text"), a.Str("target") ?? "mono", ct);
        o["item_id"] = item.UUID.ToString();
        return o;
    }

    async Task<JsonObject> SaveScriptAsync(InventoryItem item, string text, string target, CancellationToken ct)
    {
        var c = C;
        if (target is "luau" or "lsl-luau")
        {
            // LibreMetaverse's save takes only Mono or LSL2: Lua's targets go through the editor's own (B6)
            try
            {
                var (ok, diags, asset) = await _s.Scripts.CompileAgentAsync(item.UUID, text, target, ct);
                if (asset != UUID.Zero) item.AssetUUID = asset;
                return new JsonObject { ["asset_id"] = asset.ToString(), ["compiled"] = ok, ["errors"] = diags, ["target"] = target };
            }
            catch (Sojourn.Core.Scripts.ScriptEditor.RpcFail e) { throw new LeapError($"saving the script failed: {e.Message}"); }
        }
        var r = await c.Inventory.RequestUpdateScriptAgentInventoryAsync(Encoding.UTF8.GetBytes(text), item.UUID, target != "lsl2", ct);
        if (!r.uploadSuccess) throw new LeapError($"saving the script failed: {r.uploadStatus}");
        if (r.assetID != UUID.Zero) item.AssetUUID = r.assetID;
        var errors = new JsonArray();
        foreach (var m in r.compileMessages ?? new List<string>()) if (!string.IsNullOrWhiteSpace(m)) errors.Add(m.Trim());
        // agent-inventory scripts only compile on some grids (OpenSim stores them; they compile once inside an object)
        var o = new JsonObject { ["asset_id"] = r.assetID.ToString(), ["compiled"] = r.compileSuccess, ["errors"] = errors };
        if (!r.compileSuccess && errors.Count == 0) o["note"] = "saved; this grid doesn't compile scripts in inventory, they compile once put into an object";
        return o;
    }

    public async Task<JsonObject> ReadScriptAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var item = await ItemAsync(a, ct);
        if (item.AssetType != AssetType.LSLText) throw new LeapError($"{item.Name} isn't a script");
        if ((item.Permissions.OwnerMask & (PermissionMask.Modify | PermissionMask.Copy)) != (PermissionMask.Modify | PermissionMask.Copy) && !InLibrary(item.UUID))
            throw new LeapError($"{item.Name}: reading a script needs modify and copy rights");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var owner = InLibrary(item.UUID) ? LibraryOwner : item.OwnerID;
        var asset = await c.Assets.RequestInventoryAssetAsync(item.AssetUUID, item.UUID, UUID.Zero, owner, AssetType.LSLText, true, UUID.Random(), cts.Token)
                    ?? throw new LeapError("the script's text didn't arrive");
        return new JsonObject
        {
            ["item_id"] = item.UUID.ToString(), ["name"] = item.Name,
            ["text"] = Encoding.UTF8.GetString(asset.AssetData ?? Array.Empty<byte>()).TrimEnd((char)0),
        };
    }

    // ---------- landmarks ----------

    /// <summary>A landmark of where I stand (the region makes the asset from my position, as for the viewer's
    /// Create Landmark). Named like the viewer: the parcel's name, else the region's.</summary>
    public async Task<JsonObject> CreateLandmarkAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var sim = c.Network.CurrentSim ?? throw new LeapError("no region");
        var pos = c.Self.SimPosition;
        var where = $"{sim.Name} ({(int)pos.X}, {(int)pos.Y}, {(int)pos.Z})";
        var local = c.Parcels.GetParcelLocalID(sim, pos);
        var parcel = local != 0 && sim.Parcels.TryGetValue(local, out var known) ? known.Name : null;
        var name = a.Str("name") ?? (string.IsNullOrWhiteSpace(parcel) ? sim.Name : parcel);
        var folder = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(FolderType.Landmark);
        RequireMine(folder, "createLandmark");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var item = await c.Inventory.CreateItemAsync(folder, name, a.Str("desc") ?? where, AssetType.Landmark, UUID.Zero,
            InventoryType.Landmark, PermissionMask.All, cts.Token) ?? throw new LeapError("the landmark wasn't created");
        var j = _s.Inventory.ItemJson(item);
        j["item_id"] = item.UUID.ToString();
        j["region"] = sim.Name;
        j["pos"] = Json.Arr(pos);
        return j;
    }

    /// <summary>Where a landmark points: region (name when the grid tells us), region-local position.</summary>
    public async Task<JsonObject> LandmarkAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var item = await ItemAsync(a, ct);
        if (item.AssetType != AssetType.Landmark) throw new LeapError($"{item.Name} isn't a landmark");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var owner = InLibrary(item.UUID) ? LibraryOwner : item.OwnerID;
        var asset = await c.Assets.RequestInventoryAssetAsync(item.AssetUUID, item.UUID, UUID.Zero, owner, AssetType.Landmark, true, UUID.Random(), cts.Token)
                    ?? throw new LeapError("the landmark didn't arrive");
        var (regionId, pos, handle) = ParseLandmark(Encoding.UTF8.GetString(asset.AssetData ?? Array.Empty<byte>()))
                                      ?? throw new LeapError("the landmark couldn't be read");
        var o = new JsonObject
        {
            ["item_id"] = item.UUID.ToString(), ["name"] = item.Name, ["region_id"] = regionId.ToString(),
            ["pos"] = Json.Arr(pos), ["asset_id"] = item.AssetUUID.ToString(),
        };
        if (handle != 0) o["region_handle"] = handle.ToString();
        if (await RegionNameAsync(regionId, cts.Token) is { } rn) { o["region"] = rn.Name; o["region_handle"] = rn.Handle.ToString(); }
        return o;
    }

    /// <summary>"Landmark version 2" text: region_id, local_pos, and (OpenSim, Second Life) region_handle, gatekeeper.</summary>
    public static (UUID Region, Vector3 Pos, ulong Handle)? ParseLandmark(string text)
    {
        if (!text.StartsWith("Landmark version", StringComparison.OrdinalIgnoreCase)) return null;
        UUID region = UUID.Zero;
        var pos = Vector3.Zero;
        ulong handle = 0;
        var gotPos = false;
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            switch (parts[0])
            {
                case "region_id": UUID.TryParse(parts[1], out region); break;
                case "region_handle": ulong.TryParse(parts[1], out handle); break;
                case "local_pos" when parts.Length >= 4
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out var x)
                    && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out var y)
                    && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, inv, out var z):
                    pos = new Vector3(x, y, z);
                    gotPos = true;
                    break;
            }
        }
        return region != UUID.Zero && gotPos ? (region, pos, handle) : null;
    }

    async Task<(string Name, ulong Handle)?> RegionNameAsync(UUID regionId, CancellationToken ct)
    {
        var c = C;
        foreach (var sim in c.Network.Simulators.ToArray())
            if (sim.ID == regionId) return (sim.Name, sim.Handle);
        var tcs = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReply(object? _, RegionHandleReplyEventArgs e) { if (e.RegionID == regionId) tcs.TrySetResult(e.RegionHandle); }
        c.Grid.RegionHandleReply += OnReply;
        try
        {
            c.Grid.RequestRegionHandle(regionId);
            var handle = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var region = await c.Grid.GetGridRegionAsync(handle, GridLayerType.Objects, ct);
            return (region?.Name ?? "", handle);
        }
        catch (TimeoutException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally { c.Grid.RegionHandleReply -= OnReply; }
    }

    /// <summary>Teleport by landmark, as the viewer's Teleport button on a landmark (TeleportLandmarkRequest).
    /// Progress and the outcome arrive as LLTeleport events, failures as CouldNotTeleportReason.</summary>
    public async Task<JsonObject> TeleportLandmarkAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        UUID asset;
        if (a.Uuid("asset_id") is { } aid) asset = aid;
        else
        {
            var item = await ItemAsync(a, ct);
            if (item.AssetType != AssetType.Landmark) throw new LeapError($"{item.Name} isn't a landmark");
            asset = item.AssetUUID;
        }
        c.Self.RequestTeleport(asset);
        return new JsonObject { ["requested"] = true, ["asset_id"] = asset.ToString() };
    }
}
