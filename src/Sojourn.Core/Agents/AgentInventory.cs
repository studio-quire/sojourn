using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// The agent's inventory as LLInventory shows it: loaded in the background at login (the viewer does the same),
/// with the viewer's short type names in replies. Lookups that miss trigger a fetch instead of failing.
/// </summary>
public sealed class AgentInventory
{
    readonly AgentSession _s;
    readonly ConcurrentDictionary<UUID, byte> _loaded = new();
    public Task? Loading { get; private set; }

    public AgentInventory(AgentSession s) => _s = s;

    GridClient C => _s.Client ?? throw new Leap.NotConnected("viewer not connected");

    // LLAssetType::lookup names, by asset type number
    static readonly Dictionary<int, string> AssetNames = new()
    {
        [0] = "texture", [1] = "sound", [2] = "callcard", [3] = "landmark", [4] = "script", [5] = "clothing",
        [6] = "object", [7] = "notecard", [8] = "category", [10] = "lsltext", [11] = "lslbyte", [12] = "txtr_tga",
        [13] = "bodypart", [17] = "snd_wav", [18] = "img_tga", [19] = "jpeg", [20] = "animatn", [21] = "gesture",
        [22] = "simstate", [24] = "link", [25] = "link_f", [49] = "mesh", [56] = "settings", [57] = "material",
    };

    // LLInventoryType::lookup names
    static readonly Dictionary<int, string> InvNames = new()
    {
        [0] = "texture", [1] = "sound", [2] = "callcard", [3] = "landmark", [6] = "object", [7] = "notecard",
        [8] = "category", [9] = "root", [10] = "script", [15] = "snapshot", [17] = "attach", [18] = "wearable",
        [19] = "animation", [20] = "gesture", [22] = "mesh", [25] = "settings", [26] = "material",
    };

    // LLFolderType::lookup names
    static readonly Dictionary<string, FolderType> FolderNames = new()
    {
        ["texture"] = FolderType.Texture, ["sound"] = FolderType.Sound, ["callcard"] = FolderType.CallingCard,
        ["landmark"] = FolderType.Landmark, ["clothing"] = FolderType.Clothing, ["object"] = FolderType.Object,
        ["notecard"] = FolderType.Notecard, ["root_inv"] = FolderType.Root, ["lsltext"] = FolderType.LSLText,
        ["bodypart"] = FolderType.BodyPart, ["trash"] = FolderType.Trash, ["snapshots"] = FolderType.Snapshot,
        ["lstndfnd"] = FolderType.LostAndFound, ["animatn"] = FolderType.Animation, ["gesture"] = FolderType.Gesture,
        ["favorite"] = FolderType.Favorites, ["current"] = FolderType.CurrentOutfit, ["outfit"] = FolderType.Outfit,
        ["my_otfts"] = FolderType.MyOutfits, ["mesh"] = FolderType.Mesh, ["inbox"] = FolderType.Inbox,
        ["outbox"] = FolderType.Outbox, ["basic_root"] = FolderType.BasicRoot, ["settings"] = FolderType.Settings,
        ["material"] = FolderType.Material,
    };

    public static string AssetName(AssetType t) => AssetNames.TryGetValue((int)t, out var n) ? n : ((int)t).ToString();
    public static string InvName(InventoryType t) => InvNames.TryGetValue((int)t, out var n) ? n : ((int)t).ToString();

    public static string FolderTypeName(FolderType t)
    {
        if (t == FolderType.None) return "-1";
        foreach (var (name, ft) in FolderNames) if (ft == t) return name;
        return ((int)t).ToString();
    }

    /// <summary>The word notifications use for a type ("object", "notecard", ...).</summary>
    public static string AssetTypeLabel(AssetType t) => t switch
    {
        AssetType.Object => "object", AssetType.Notecard => "notecard", AssetType.Texture => "texture",
        AssetType.Sound => "sound", AssetType.Landmark => "landmark", AssetType.Clothing => "clothing",
        AssetType.Bodypart => "body part", AssetType.Animation => "animation", AssetType.Gesture => "gesture",
        AssetType.LSLText => "script", AssetType.CallingCard => "calling card", AssetType.Folder => "folder",
        _ => "item",
    };

    public FolderType? FolderTypeFrom(string name) => FolderNames.TryGetValue(name, out var t) ? t : null;

    public UUID FolderFor(FolderType t)
    {
        var c = C;
        if (t == FolderType.Root) return c.Inventory.Store?.RootFolder?.UUID ?? UUID.Zero;
        return c.Inventory.FindFolderForType(t);
    }

    // ---------- loading ----------

    /// <summary>The inventory, then the Library, fetched in the background at login, as the viewer's background fetch
    /// does.</summary>
    public void StartLoading(GridClient c, CancellationToken ct)
    {
        // a new login is a new store: nothing in it is loaded yet
        _life = ct;
        _loaded.Clear();
        lock (_walks) _walks.Clear();
        Loading = Task.Run(async () =>
        {
            try
            {
                var root = c.Inventory.Store?.RootFolder;
                if (root is null) return;
                var whole = false;
                // right after login the root fetch can come back empty (seen on OpenSim): try again a few times
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    whole = await LoadSubtreeAsync(root.UUID, ct);
                    if (_loaded.Count > 1) break;
                    _loaded.Clear();
                    await Task.Delay(TimeSpan.FromSeconds(2 + 2 * attempt), ct);
                }
                var mine = _loaded.Count;
                _s.Log.LogInformation("inventory loaded: {Folders} folders{Partial}", mine, whole ? "" : " (some didn't answer)");
                // the Library after it, without holding up what waits for Loading (the outfit, the AO, gestures)
                var lib = _s.InventoryOps.LibraryRoot;
                if (lib == UUID.Zero) return;
                _ = LoadSubtreeAsync(lib, ct).ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        _s.Log.LogInformation("library loaded: {Folders} folders{Partial}", _loaded.Count - mine, t.Result ? "" : " (some didn't answer)");
                    else _ = t.Exception;
                }, TaskScheduler.Default);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { _s.Log.LogWarning(e, "inventory load"); }
        }, ct);
    }

    // ---------- fetching a subtree ----------
    //
    // Folders are fetched as the viewer's background fetch does over FetchInventoryDescendents2 / FetchLibDescendents2:
    // several folders a request, a few requests at once, each folder's subfolders queued as soon as they're known
    // (the login skeleton already names most). One folder a request, one at a time, took minutes for the Library's
    // ~500 folders on Second Life (2026-10-29), longer than any caller waits.

    const int FoldersPerRequest = 10, RequestsAtOnce = 4;
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    readonly Dictionary<UUID, Task<bool>> _walks = new();
    CancellationToken _life;

    /// <summary>Everything of mine asked of the grid again (the viewer has no such button; Radegast's Reload Inventory): how
    /// stale folders get mended, both ways. LibreMetaverse's fetch only adds and updates (a folder now empty isn't even
    /// looked at, and a folder it knows keeps its old parent), so what was deleted or moved in another viewer, or by the
    /// grid, stayed where it had been until the next login. Here each folder's reply is taken as all it holds
    /// (FetchExactAsync), and at the end of the walk what no reply listed where the store had it goes. True when every
    /// folder answered. The Library is left as it is. One at a time; asking again meanwhile waits for the one going.</summary>
    public Task<bool> ReloadAsync(CancellationToken ct)
    {
        if (C.Inventory.Store?.RootFolder is not { } root) return Task.FromResult(false);
        Task<bool> walk;
        lock (_walks)
        {
            if (_reloading is { IsCompleted: false } going) walk = going;
            else
            {
                foreach (var id in _loaded.Keys.Where(id => !_s.InventoryOps.InLibrary(id)).ToList()) _loaded.TryRemove(id, out _);
                _walks[root.UUID] = _reloading = walk = Task.Run(() => WalkAsync(root.UUID, _life, exact: true));
            }
        }
        return walk.WaitAsync(ct);
    }

    Task<bool>? _reloading;

    /// <summary>What the last reload dropped (things the grid no longer had where the store had them), for its caller's words.</summary>
    public int LastReloadDropped { get; private set; }

    /// <summary>Every folder under <paramref name="root"/> (itself included) fetched into the store; true when all of
    /// them answered. One walk per root at a time, shared by whoever asks, and it runs on for the session when a caller
    /// stops waiting (<paramref name="ct"/> only ends the wait), so asking again picks up where it got to.</summary>
    public Task<bool> LoadSubtreeAsync(UUID root, CancellationToken ct)
    {
        Task<bool> walk;
        lock (_walks)
        {
            if (!_walks.TryGetValue(root, out walk!) || walk.IsCompleted)
                _walks[root] = walk = Task.Run(() => WalkAsync(root, _life));
        }
        return walk.WaitAsync(ct);
    }

    async Task<bool> WalkAsync(UUID root, CancellationToken ct, bool exact = false)
    {
        var c = C;
        var store = c.Inventory.Store;
        if (store is null) return false;
        var lib = _s.InventoryOps.InLibrary(root);
        var owner = lib ? _s.InventoryOps.LibraryOwner : c.Self.AgentID;
        exact &= !lib;
        var unlisted = new List<(UUID Id, UUID From)>();
        int partial = 0;

        var seen = new HashSet<UUID> { root };
        var open = new Queue<UUID>();
        open.Enqueue(root);
        var want = new List<UUID>();
        var retried = new HashSet<UUID>();
        var running = new List<Task<(List<UUID> Asked, HashSet<UUID> Got)>>();
        var whole = true;
        while (!ct.IsCancellationRequested)
        {
            while (open.TryDequeue(out var f))
            {
                if (!_loaded.ContainsKey(f)) want.Add(f);
                foreach (var sub in Subfolders(store, f)) if (seen.Add(sub)) open.Enqueue(sub);
            }
            while (want.Count > 0 && running.Count < RequestsAtOnce)
            {
                var n = Math.Min(FoldersPerRequest, want.Count);
                var batch = want.GetRange(0, n);
                running.Add(exact ? FetchExactAsync(c, owner, batch, unlisted, () => Interlocked.Increment(ref partial), ct)
                    : FetchFoldersAsync(c, lib, owner, batch, ct));
                want.RemoveRange(0, n);
            }
            if (running.Count == 0) break;
            var done = await Task.WhenAny(running);
            running.Remove(done);
            var (asked, got) = await done;
            if (!ReferenceEquals(_s.Client, c)) return false;   // logged out or in again: another store
            foreach (var f in asked)
            {
                if (got.Contains(f))
                {
                    _loaded[f] = 1;
                    foreach (var sub in Subfolders(store, f)) if (seen.Add(sub)) open.Enqueue(sub);
                }
                else if (retried.Add(f)) want.Add(f);
                else whole = false;
            }
        }
        if (exact && !ct.IsCancellationRequested && ReferenceEquals(_s.Client, c))
        {
            // what a reply left out goes, unless a later reply put it somewhere else meanwhile (a thing moved, its new
            // folder read after its old one), or it went elsewhere by our own hand since
            var dropped = new List<string>();
            lock (unlisted)
                foreach (var (id, from) in unlisted)
                    if (store.TryGetValue(id, out var n) && n != null && n.ParentUUID == from)
                    {
                        store.RemoveNodeFor(n);
                        dropped.Add(n.Name);
                    }
            LastReloadDropped = dropped.Count;
            _s.Log.LogInformation("inventory read again: {Folders} folders, {Dropped} things the grid no longer had dropped{Names}{Partial}{Short}",
                _loaded.Count, dropped.Count, dropped.Count > 0 ? $" ({string.Join(", ", dropped.Take(8))}{(dropped.Count > 8 ? ", ..." : "")})" : "",
                whole ? "" : " (some folders didn't answer)", partial > 0 ? $"; {partial} folders' replies held fewer than they counted, left as they were" : "");
        }
        return whole && !ct.IsCancellationRequested;
    }

    /// <summary>One request for a few folders' contents, as FetchFoldersAsync asks, but read here rather than by
    /// LibreMetaverse, so each folder's reply is known as all it holds: its folders and items put in the store where the
    /// reply says they are (a folder the store knew moved if its parent changed), and whatever the store had in it before
    /// asking that the reply left out noted in <paramref name="unlisted"/> (things that came in while asking aren't the
    /// reply's to judge). A reply listing fewer than its own count of what's in the folder is left at that: nothing of it
    /// noted. Which folders answered.</summary>
    async Task<(List<UUID> Asked, HashSet<UUID> Got)> FetchExactAsync(GridClient c, UUID owner, List<UUID> batch,
        List<(UUID Id, UUID From)> unlisted, Action shortReply, CancellationToken ct)
    {
        var got = new HashSet<UUID>();
        var cap = c.Network.CurrentSim?.Caps?.CapabilityURI("FetchInventoryDescendents2");
        var store = c.Inventory.Store;
        if (cap is null || store is null) return (batch, got);
        var before = batch.ToDictionary(f => f, f => Contents(store, f).Select(n => n.UUID).ToList());
        var asked = new OSDArray();
        foreach (var f in batch)
            asked.Add(new OSDMap
            {
                ["folder_id"] = OSD.FromUUID(f), ["owner_id"] = OSD.FromUUID(owner), ["fetch_folders"] = OSD.FromBoolean(true),
                ["fetch_items"] = OSD.FromBoolean(true), ["sort_order"] = OSD.FromInteger((int)InventorySortOrder.ByDate),
            });
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            var (_, data) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, new OSDMap { ["folders"] = asked }, cts.Token);
            if (data is not { Length: > 0 } || OSDParser.Deserialize(data) is not OSDMap reply || reply["folders"] is not OSDArray folders)
                return (batch, got);
            foreach (var fo in folders.OfType<OSDMap>())
            {
                var id = fo["folder_id"].AsUUID();
                if (!before.TryGetValue(id, out var had)) continue;
                var listed = new HashSet<UUID>();
                var cats = fo["categories"] as OSDArray ?? new OSDArray();
                var items = fo["items"] as OSDArray ?? new OSDArray();
                foreach (var cat in cats.OfType<OSDMap>())
                {
                    var cid = cat.ContainsKey("category_id") ? cat["category_id"].AsUUID() : cat["folder_id"].AsUUID();
                    listed.Add(cid);
                    bool known = store.TryGetValue<InventoryFolder>(cid, out var folder) && folder != null;
                    if (!known) folder = new InventoryFolder(cid);
                    folder!.OwnerID = cat["agent_id"].AsUUID();
                    folder.Name = cat["name"].AsString();
                    folder.Version = cat["version"].AsInteger();
                    folder.PreferredType = (FolderType)cat["type_default"].AsInteger();
                    if (!known || folder.ParentUUID != id)
                    {
                        folder.ParentUUID = id;
                        store.UpdateNodeFor(folder);
                    }
                }
                foreach (var it in items)
                {
                    var item = InventoryItem.FromOSD(it);
                    listed.Add(item.UUID);
                    store.UpdateNodeFor(item);
                    if (item.AssetType == AssetType.Sound) _s.Ears.Learn(item.AssetUUID, item.Name);
                }
                if (store.TryGetValue<InventoryFolder>(id, out var self) && self != null)
                {
                    self.DescendentCount = fo["descendents"].AsInteger();
                    self.Version = fo["version"].AsInteger();
                }
                if (store.TryGetNodeFor(id, out var node)) node.NeedsUpdate = false;
                if (fo["descendents"].AsInteger() > listed.Count) shortReply();
                else
                    lock (unlisted)
                        foreach (var n in had)
                            if (!listed.Contains(n)) unlisted.Add((n, id));
                got.Add(id);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _s.Log.LogDebug(e, "inventory folders read again {Folder} (+{More})", batch[0], batch.Count - 1); }
        return (batch, got);
    }

    /// <summary>One request for a few folders' contents; which of them answered (LibreMetaverse reports each folder in
    /// the reply through FolderUpdated, and swallows failures).</summary>
    async Task<(List<UUID> Asked, HashSet<UUID> Got)> FetchFoldersAsync(GridClient c, bool library, UUID owner, List<UUID> batch,
        CancellationToken ct)
    {
        var got = new HashSet<UUID>();
        // the region's own, asked each time: a teleport mid-walk changes it
        var cap = c.Network.CurrentSim?.Caps?.CapabilityURI(library ? "FetchLibDescendents2" : "FetchInventoryDescendents2");
        if (cap is null) return (batch, got);
        var asked = batch.ToHashSet();
        void Updated(object? _, FolderUpdatedEventArgs e)
        {
            if (e.Success && asked.Contains(e.FolderID)) lock (got) got.Add(e.FolderID);
        }
        c.Inventory.FolderUpdated += Updated;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            var folders = batch.Select(id => new InventoryFolder(id) { OwnerID = owner }).ToList();
            var contents = await c.Inventory.RequestFolderContentsAsync(folders, cap, true, true, InventorySortOrder.ByDate, cts.Token);
            if (!library)
                foreach (var it in contents.OfType<InventoryItem>())
                    if (it.AssetType == AssetType.Sound) _s.Ears.Learn(it.AssetUUID, it.Name);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _s.Log.LogDebug(e, "inventory folders {Folder} (+{More})", batch[0], batch.Count - 1); }
        finally { c.Inventory.FolderUpdated -= Updated; }
        lock (got) return (batch, got.ToHashSet());
    }

    /// <summary>A folder's contents in the store (empty when the store doesn't know it).</summary>
    static List<InventoryBase> Contents(LibreMetaverse.Inventory store, UUID folder)
    {
        try { return store.GetContents(folder); }
        catch (InventoryException) { return new List<InventoryBase>(); }
    }

    static IEnumerable<UUID> Subfolders(LibreMetaverse.Inventory store, UUID folder) =>
        Contents(store, folder).OfType<InventoryFolder>().Select(f => f.UUID);

    /// <summary>The items under a folder as far as the store has them (Trash skipped, nothing fetched).</summary>
    public IEnumerable<InventoryItem> ItemsInStore(UUID folder)
    {
        var store = C.Inventory.Store;
        if (store is null) yield break;
        var trash = FolderFor(FolderType.Trash);
        var seen = new HashSet<UUID>();
        var stack = new Stack<UUID>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var f = stack.Pop();
            if (!seen.Add(f)) continue;
            foreach (var n in Contents(store, f))
            {
                if (n is InventoryFolder sub) { if (sub.UUID != trash) stack.Push(sub.UUID); }
                else if (n is InventoryItem it) yield return it;
            }
        }
    }

    /// <summary>A folder's contents fetched (once) from the inventory service; Library folders from the Library's owner.</summary>
    public Task EnsureLoadedAsync(UUID folder, CancellationToken ct) => EnsureFolderAsync(folder, ct);

    /// <summary>Makes sure a node's folder is loaded, so an item id from a listing resolves in the store.</summary>
    public async Task EnsureParentLoadedAsync(UUID id, CancellationToken ct)
    {
        if (Node(id) is { } n) { if (n is InventoryFolder) await EnsureFolderAsync(id, ct); return; }
        await ItemAsync(id, ct);
    }

    async Task EnsureFolderAsync(UUID folder, CancellationToken ct)
    {
        if (_loaded.ContainsKey(folder)) return;
        var c = C;
        try
        {
            var owner = _s.InventoryOps.InLibrary(folder) ? _s.InventoryOps.LibraryOwner : c.Self.AgentID;
            await c.Inventory.FolderContentsAsync(folder, owner, true, true, InventorySortOrder.ByDate, ct);
            _loaded[folder] = 1;
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    public async Task<InventoryItem?> ItemAsync(UUID id, CancellationToken ct)
    {
        var c = C;
        if (c.Inventory.Store?.TryGetValue<InventoryItem>(id, out var item) == true) return item;
        try { return await c.Inventory.FetchItemAsync(id, c.Self.AgentID, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public InventoryBase? Node(UUID id) =>
        C.Inventory.Store?.TryGetValue(id, out var n) == true ? n : null;

    /// <summary>Worn: one of my attachments came from this item, or the outfit folder links to it.</summary>
    /// <summary>Attached now: one of my attachments in the current region came from this item.</summary>
    public bool IsAttached(InventoryItem item)
    {
        var c = C;
        var w = _s.Region;
        if (w is null || w.Avatars.Values.FirstOrDefault(a => a.ID == c.Self.AgentID) is not { } me) return false;
        foreach (var p in w.Prims.Values)
            if (p.ParentID == me.LocalID && AttachItemId(p) == item.UUID) return true;
        return false;
    }

    public bool IsWorn(InventoryItem item)
    {
        var c = C;
        var w = _s.Region;
        if (w is not null && w.Avatars.Values.FirstOrDefault(a => a.ID == c.Self.AgentID) is { } me)
        {
            foreach (var p in w.Prims.Values)
                if (p.ParentID == me.LocalID && AttachItemId(p) == item.UUID) return true;
        }
        var cof = c.Inventory.FindFolderForType(FolderType.CurrentOutfit);
        return c.Inventory.Store?.GetContents(cof).OfType<InventoryItem>().Any(l => l.AssetUUID == item.UUID) == true;
    }

    public static UUID AttachItemId(Primitive p)
    {
        foreach (var nv in p.NameValues)
            if (nv.Name == "AttachItemID" && UUID.TryParse(nv.Value?.ToString(), out var id)) return id;
        return UUID.Zero;
    }

    // ---------- the viewer's maps ----------

    public JsonObject ItemJson(InventoryItem it)
    {
        var target = it;
        var isLink = it.AssetType is AssetType.Link or AssetType.LinkFolder;
        if (isLink && Node(it.AssetUUID) is InventoryItem t) target = t;
        return new JsonObject
        {
            ["id"] = it.UUID.ToString(),
            ["name"] = target.Name,
            ["parent_id"] = it.ParentUUID.ToString(),
            ["desc"] = target.Description,
            ["inv_type"] = InvName(target.InventoryType),
            ["asset_type"] = AssetName(target.AssetType),
            ["creation_date"] = new DateTimeOffset(target.CreationDate.ToUniversalTime()).ToUnixTimeSeconds(),
            ["asset_id"] = target.AssetUUID.ToString(),
            ["is_link"] = isLink,
            ["linked_id"] = isLink ? it.AssetUUID.ToString() : UUID.Zero.ToString(),
        };
    }

    public static JsonObject FolderJson(InventoryFolder f) => new()
    {
        ["id"] = f.UUID.ToString(),
        ["name"] = f.Name,
        ["parent_id"] = f.ParentUUID.ToString(),
        ["type"] = FolderTypeName(f.PreferredType),
    };

    public JsonObject ItemsInfo(IEnumerable<UUID> ids)
    {
        var items = new JsonObject();
        var cats = new JsonObject();
        foreach (var id in ids)
        {
            switch (Node(id))
            {
                case InventoryFolder f: cats[id.ToString()] = FolderJson(f); break;
                case InventoryItem i: items[id.ToString()] = ItemJson(i); break;
            }
        }
        var o = new JsonObject();
        if (items.Count > 0) o["items"] = items;
        if (cats.Count > 0) o["categories"] = cats;
        return o;
    }

    public async Task<JsonObject> DirectDescendantsAsync(UUID folder, CancellationToken ct)
    {
        await EnsureFolderAsync(folder, ct);
        if (Node(folder) is not InventoryFolder) return Json.Error($"Folder \"{folder}\" was not found");
        var items = new JsonObject();
        var cats = new JsonObject();
        foreach (var n in C.Inventory.Store!.GetContents(folder))
        {
            if (n is InventoryFolder f) cats[f.UUID.ToString()] = FolderJson(f);
            else if (n is InventoryItem i) items[i.UUID.ToString()] = ItemJson(i);
        }
        var o = new JsonObject();
        if (items.Count > 0) o["items"] = items;
        if (cats.Count > 0) o["categories"] = cats;
        return o;
    }

    /// <summary>LLInventory collectDescendantsIf: depth-first under a folder, Trash skipped. The subtree is fetched
    /// first (LoadSubtreeAsync); "partial": true when some of its folders didn't answer.</summary>
    public async Task<JsonObject> CollectAsync(UUID folder, string? name, string? desc, string? type, string? filterLinks, int limit, CancellationToken ct)
    {
        var store = C.Inventory.Store;
        var items = new JsonObject();
        var cats = new JsonObject();
        if (store is null) return new JsonObject();
        var whole = await LoadSubtreeAsync(folder, ct);
        var trash = FolderFor(FolderType.Trash);
        var stack = new Stack<UUID>();
        stack.Push(folder);
        var count = 0;
        while (stack.Count > 0 && (limit <= 0 || count < limit))
        {
            var f = stack.Pop();
            foreach (var n in Contents(store, f))
            {
                if (n is InventoryFolder sub)
                {
                    if (sub.UUID == trash) continue;
                    stack.Push(sub.UUID);
                    if ((name is null || sub.Name.Contains(name)) && desc is null && type is null && filterLinks != "ONLY_LINKS"
                        && (limit <= 0 || count < limit))
                    {
                        cats[sub.UUID.ToString()] = FolderJson(sub);
                        count++;
                    }
                }
                else if (n is InventoryItem it)
                {
                    var isLink = it.AssetType is AssetType.Link or AssetType.LinkFolder;
                    if (filterLinks == "EXCLUDE_LINKS" && isLink) continue;
                    if (filterLinks == "ONLY_LINKS" && !isLink) continue;
                    var j = ItemJson(it);
                    // (an item may have no description: a notecard an object gave, on OpenSim)
                    if (!Match(j["name"]?.GetValue<string>() ?? "", j["desc"]?.GetValue<string>() ?? "", name, desc, true)) continue;
                    if (type is not null && j["asset_type"]!.GetValue<string>() != type) continue;
                    items[it.UUID.ToString()] = j;
                    if (limit > 0 && ++count >= limit) break;
                }
            }
        }
        var o = new JsonObject();
        if (items.Count > 0) o["items"] = items;
        if (cats.Count > 0) o["categories"] = cats;
        if (!whole) o["partial"] = true;
        return o;
    }

    static bool Match(string n, string d, string? name, string? desc, bool _) =>
        (name is null || n.Contains(name)) && (desc is null || d.Contains(desc));

    // ---------- offers ----------

    /// <summary>Accepts (into the type's folder) or declines an inventory offer, as the viewer's buttons do.</summary>
    public static void AnswerOffer(GridClient c, InstantMessage im, AssetType type, UUID objectId, bool keep)
    {
        var dialog = im.Dialog switch
        {
            InstantMessageDialog.TaskInventoryOffered => keep ? InstantMessageDialog.TaskInventoryAccepted : InstantMessageDialog.TaskInventoryDeclined,
            // (a notice's own IM, as it came or asked from the archive: its session is the notice, its sender the group)
            InstantMessageDialog.GroupNotice or InstantMessageDialog.GroupNoticeRequested
                => keep ? InstantMessageDialog.GroupNoticeInventoryAccepted : InstantMessageDialog.GroupNoticeInventoryDeclined,
            _ => keep ? InstantMessageDialog.InventoryAccepted : InstantMessageDialog.InventoryDeclined,
        };
        var folder = keep ? c.Inventory.FindFolderForType(type) : UUID.Zero;
        var pkt = new ImprovedInstantMessagePacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            MessageBlock =
            {
                FromGroup = false, ToAgentID = im.FromAgentID, Offline = 0, ID = im.IMSessionID, Timestamp = 0,
                FromAgentName = Utils.StringToBytes(c.Self.Name), Message = Utils.EmptyBytes, ParentEstateID = 0,
                RegionID = UUID.Zero, Position = c.Self.SimPosition, Dialog = (byte)dialog,
                BinaryBucket = keep ? folder.GetBytes() : Utils.EmptyBytes,
            },
        };
        c.Network.SendPacket(pkt, c.Network.CurrentSim);
        if (keep && objectId != UUID.Zero) c.Inventory.RequestFetchInventory(objectId, c.Self.AgentID);
        if (keep) _ = Task.Run(async () =>
        {
            // the new item lands in the folder: refresh it so lookups by name find it
            await Task.Delay(1500);
            try { await c.Inventory.FolderContentsAsync(folder, c.Self.AgentID, true, true, InventorySortOrder.ByDate); } catch { }
        });
    }
}
