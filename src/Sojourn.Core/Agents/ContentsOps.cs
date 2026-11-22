using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Core.Leap;
using Sojourn.Core.Scripts;

namespace Sojourn.Core.Agents;

/// <summary>
/// An object's contents (task inventory), as the viewer's build floater Content tab does it: list, drop an item in
/// from my inventory (UpdateTaskInventory; scripts via RezScript, running), copy an item out to my inventory
/// (MoveTaskInventory), delete one (RemoveTaskInventory), a script's running state and reset. Written by Claude
/// Opus 5.5, 2026-10-16. The region checks permissions as for the viewer: I need to be able to modify the prim
/// (or it allows drops, llAllowInventoryDrop, for non-scripts); a no-copy item moves in rather than copies.
/// </summary>
public sealed class ContentsOps
{
    readonly AgentSession _s;
    public ContentsOps(AgentSession s) => _s = s;
    /// <summary>Drops whose outcome wasn't seen (prim, item) and when: another of the same waits for `again`.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<(UUID, UUID), DateTime> _unsure = new();
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    Primitive Prim(JsonObject a)
    {
        var id = PrimId(a) ?? throw new LeapError("need id (the prim)");
        var w = _s.Region ?? throw new LeapError("no region");
        return w.PrimById(id) ?? throw new LeapError($"object not found in this region: {id}");
    }

    async Task<List<InventoryItem>> ListAsync(Primitive prim, CancellationToken ct)
    {
        try { return (await _s.Scripts.TaskInventoryAsync(prim, ct)).OfType<InventoryItem>().ToList(); }
        catch (ScriptEditor.RpcFail e) { throw new LeapError(e.Message); }
    }

    JsonObject ItemJson(InventoryItem it) => new()
    {
        ["item_id"] = it.UUID.ToString(), ["name"] = it.Name, ["desc"] = it.Description,
        ["type"] = AgentInventory.AssetTypeLabel(it.AssetType), ["asset_type"] = AgentInventory.AssetName(it.AssetType),
        ["perms"] = InventoryOps.PermsJson(it.Permissions), ["creator_id"] = it.CreatorID.ToString(),
        ["owner_id"] = it.OwnerID.ToString(),
    };

    public async Task<JsonObject> ContentsAsync(JsonObject a, CancellationToken ct)
    {
        var prim = Prim(a);
        var items = await ListAsync(prim, ct);
        var arr = new JsonArray();
        foreach (var it in items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            var j = ItemJson(it);
            var (how, why) = Out(prim, it);
            j["out"] = how switch { CopyOut.Copy => "copy", CopyOut.Move => "move", _ => "no" };
            if (why != null) j["out_why"] = why;
            if (it.AssetType == AssetType.LSLText && a.Bool("running", true)) j["running"] = await _s.Scripts.RunningAsync(prim.ID, it.UUID, ct);
            arr.Add(j);
        }
        return new JsonObject { ["id"] = prim.ID.ToString(), ["name"] = prim.Properties?.Name ?? "", ["items"] = arr };
    }

    /// <summary>The prim an op's args name, as the ops read them.</summary>
    public static UUID? PrimId(JsonObject a) => a.Uuid("id") ?? a.Uuid("object_id");

    /// <summary>The prim and the item inside it that an op's args name, resolved as the ops resolve them (for the
    /// confirmation gate's description).</summary>
    public async Task<(Primitive Prim, InventoryItem Item)> ResolveAsync(JsonObject a, CancellationToken ct)
    {
        var prim = Prim(a);
        return (prim, await TaskItemAsync(prim, a, ct));
    }

    async Task<InventoryItem> TaskItemAsync(Primitive prim, JsonObject a, CancellationToken ct)
    {
        var items = await ListAsync(prim, ct);
        if (a.Uuid("item_id") is { } id)
            return items.FirstOrDefault(i => i.UUID == id) ?? throw new LeapError($"no item {id} in that object");
        var name = a.Str("name") ?? throw new LeapError("need item_id or name (of an item in the object)");
        var exact = items.Where(i => i.Name == name).ToList();
        if (exact.Count == 0) exact = items.Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count switch
        {
            1 => exact[0],
            0 => throw new LeapError($"no item named {name} in that object"),
            _ => throw new LeapError($"{exact.Count} items named {name}; give item_id"),
        };
    }

    /// <summary>Drops items from my inventory into the prim and waits until the region lists them.</summary>
    public async Task<JsonObject> AddAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var prim = Prim(a);
        var ids = a.UuidList("item_ids");
        if (a.Uuid("item_id") is { } one && !ids.Contains(one)) ids.Add(one);
        if (ids.Count == 0) throw new LeapError("need item_id or item_ids (from my inventory)");
        var running = a.Bool("running", true);
        var items = new List<InventoryItem>();
        foreach (var id in ids)
        {
            var item = await _s.Inventory.ItemAsync(id, ct) ?? throw new LeapError($"no inventory item {id}");
            if (item.AssetType == AssetType.Link && _s.Inventory.Node(item.AssetUUID) is InventoryItem t) item = t;
            if (item.AssetType is AssetType.Link or AssetType.LinkFolder) throw new LeapError($"{item.Name} is a link to something missing");
            if (_s.InventoryOps.InLibrary(item.UUID)) throw new LeapError($"{item.Name} is in the Library: copy it to my inventory first (TesselInventory copy)");
            if (_s.Inventory.IsWorn(item)) throw new LeapError($"{item.Name} is worn; take it off first");
            items.Add(item);
        }
        // A drop whose outcome the last call couldn't see may well have landed (on the main grid a self-removing script
        // came through three times, 2026-10-26): another of the same item into the same prim waits for `again`.
        foreach (var item in items)
            if (_unsure.TryGetValue((prim.ID, item.UUID), out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(5) && a.Bool("again") != true)
                throw new LeapError($"{item.Name} was dropped into it {(DateTime.UtcNow - at).TotalSeconds:0} s ago and may have landed "
                    + "(a script that removes itself never shows in the contents): check contents or what it said, and pass again: true "
                    + "only if another copy is really wanted");

        // Selected, as the Content tab has it: the region then sends its properties, with the contents' serial number,
        // whenever they change. That says a drop landed even when the item is gone again before any listing shows it.
        short? serial = null;
        var serialMoved = new SemaphoreSlim(0);
        void OnProps(object? _, ObjectPropertiesEventArgs e)
        {
            if (e.Properties.ObjectID != prim.ID) return;
            serial = e.Properties.InventorySerial;
            serialMoved.Release();
        }
        c.Objects.ObjectProperties += OnProps;
        var sim = c.Network.CurrentSim!;
        bool wasSelected = _s.State.Selected.ContainsKey(prim.LocalID);
        c.Objects.SelectObject(sim, prim.LocalID, false);
        var wanted = items.ToList();
        var added = new JsonArray();
        short? serial0;
        int landed = 0;
        try
        {
            var before = (await ListAsync(prim, ct)).Select(i => i.UUID).ToHashSet();
            await serialMoved.WaitAsync(TimeSpan.FromSeconds(3), ct);
            serial0 = serial;
            foreach (var item in items)
            {
                if (item.AssetType == AssetType.LSLText) c.Inventory.CopyScriptToTask(prim.LocalID, item, running, sim);
                else c.Inventory.UpdateTaskInventory(prim.LocalID, item, sim);
                await Task.Delay(150, ct);
            }
            // the region may rename a clash ("Name 1"); match new items by type, then by name
            var end = DateTime.UtcNow + TimeSpan.FromSeconds(a.Num("timeout", 15));
            var claimed = new HashSet<UUID>();
            while (wanted.Count > 0 && DateTime.UtcNow < end)
            {
                // list when the serial moves, or every 2 s if the region doesn't send it
                await serialMoved.WaitAsync(serial0 is null ? TimeSpan.FromMilliseconds(600) : TimeSpan.FromSeconds(2), ct);
                while (serialMoved.CurrentCount > 0) serialMoved.Wait(0);
                if (serial0 is { } s0 && serial is { } s1) landed = (short)(s1 - s0);
                var now = (await ListAsync(prim, ct)).Where(i => !before.Contains(i.UUID) && !claimed.Contains(i.UUID)).ToList();
                foreach (var w in wanted.ToList())
                {
                    var hit = now.FirstOrDefault(i => i.AssetType == w.AssetType && i.Name == w.Name)
                              ?? now.FirstOrDefault(i => i.AssetType == w.AssetType && i.Name.StartsWith(w.Name, StringComparison.Ordinal));
                    if (hit is null) continue;
                    claimed.Add(hit.UUID);
                    now.Remove(hit);
                    wanted.Remove(w);
                    var j = ItemJson(hit);
                    j["from_item"] = w.UUID.ToString();
                    if ((w.Permissions.OwnerMask & PermissionMask.Copy) == 0) j["moved"] = true;  // no-copy: it left my inventory
                    added.Add(j);
                }
                // every drop changed the contents (each moves the serial once, more if a script then removed itself) but
                // some aren't listed: they came and went
                if (wanted.Count > 0 && landed >= items.Count) break;
            }
        }
        finally
        {
            c.Objects.ObjectProperties -= OnProps;
            if (!wasSelected && !_s.State.Selected.ContainsKey(prim.LocalID)) c.Objects.DeselectObject(sim, prim.LocalID);
        }
        if (wanted.Count > 0 && landed >= items.Count)
        {
            foreach (var w in wanted)
                added.Add(new JsonObject
                {
                    ["from_item"] = w.UUID.ToString(), ["name"] = w.Name, ["gone_again"] = true,
                    ["note"] = "it went in (the contents changed), then left them: a script that removes itself once it has run, or the object's own script taking it",
                });
            wanted.Clear();
        }
        // A no-copy item that isn't listed may have been taken at once by the object's script (a drop box, a board
        // taking deliveries, a hearth eating fuel): if it's gone from my inventory too, the drop went through.
        foreach (var w in wanted.ToList())
        {
            if ((w.Permissions.OwnerMask & PermissionMask.Copy) != 0) continue;
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(TimeSpan.FromSeconds(4));
            if (await c.Inventory.FetchItemAsync(w.UUID, c.Self.AgentID, quick.Token) is not null) continue;
            if (ct.IsCancellationRequested) break;
            wanted.Remove(w);
            c.Inventory.Store?.RemoveNodeFor(w);
            added.Add(new JsonObject { ["from_item"] = w.UUID.ToString(), ["name"] = w.Name, ["moved"] = true, ["taken_by_object"] = true });
        }
        foreach (var it in items) if (!wanted.Contains(it)) _unsure.TryRemove((prim.ID, it.UUID), out _);
        var o = new JsonObject { ["id"] = prim.ID.ToString(), ["added"] = added };
        if (wanted.Count > 0)
        {
            var mine = (S(prim) & PrimFlags.ObjectModify) != 0;
            var why = mine
                ? "the region didn't list it in time; it may still have gone in (look at contents, or for what a script in it said, before adding it again)"
                : "I can't modify that object, and it doesn't take drops from me (scripts never drop into others' objects)";
            if (mine) foreach (var w in wanted) _unsure[(prim.ID, w.UUID)] = DateTime.UtcNow;
            o["missing"] = new JsonArray(wanted.Select(w => (JsonNode)new JsonObject { ["item_id"] = w.UUID.ToString(), ["name"] = w.Name }).ToArray());
            if (added.Count == 0) throw new LeapError($"nothing was added: {why}");
            o["note"] = why;
        }
        return o;
    }

    PrimFlags S(Primitive p) => _s.State.PrimFlagsOf(p);

    enum CopyOut { Copy, Move, No }

    /// <summary>What taking an item out into my inventory does, by the viewer's rule (move_inv_category_world_to_agent and
    /// LLFolderBridge's drop from an object, both on LLPermissions): it's copied when I may copy it and have it given to
    /// me; failing that, from an object of mine it moves (a no-copy item leaves the object); otherwise the region won't
    /// give it, and why is said. Copying goes by my rights on the item: the owner's when it's mine, else the group's (my
    /// active group, if it's the item's) or everyone's.</summary>
    (CopyOut How, string? Why) Out(Primitive prim, InventoryItem it)
    {
        var c = C;
        var me = c.Self.AgentID;
        var p = it.Permissions;
        bool objectMine = (S(prim) & PrimFlags.ObjectYouOwner) != 0;
        bool mine = it.OwnerID == me || it.OwnerID == UUID.Zero && objectMine;
        bool copy = mine ? (p.OwnerMask & PermissionMask.Copy) != 0
            : (p.EveryoneMask & PermissionMask.Copy) != 0
              || it.GroupID != UUID.Zero && it.GroupID == c.Self.ActiveGroup && (p.GroupMask & PermissionMask.Copy) != 0;
        bool transfer = mine || (p.OwnerMask & PermissionMask.Transfer) != 0;
        if (copy && transfer) return (CopyOut.Copy, null);
        if (objectMine) return (CopyOut.Move, null);
        return (CopyOut.No, !copy ? "its owner hasn't let you copy it" : "it's no-transfer, so its owner can't give it to you");
    }

    /// <summary>Copies an item out of the prim into my inventory ("Copy to inventory"; a no-copy item in an object of mine
    /// moves, said as moved), into the folder for its type unless folder_id says; the reply names the folder.</summary>
    public async Task<JsonObject> CopyOutAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var prim = Prim(a);
        var item = await TaskItemAsync(prim, a, ct);
        var (how, why) = Out(prim, item);
        if (how == CopyOut.No) throw new LeapError($"{why}, so {item.Name} can't come out");
        var folder = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(InventoryOps.FolderForAsset(item.AssetType));
        await _s.Inventory.EnsureLoadedAsync(folder, ct);
        var before = c.Inventory.Store!.GetContents(folder).Select(n => n.UUID).ToHashSet();
        var tcs = new TaskCompletionSource<UUID>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnItem(object? _, ItemReceivedEventArgs e)
        {
            if (e.Item.ParentUUID == folder && !before.Contains(e.Item.UUID) && e.Item.AssetType == item.AssetType) tcs.TrySetResult(e.Item.UUID);
        }
        void OnTask(object? _, TaskItemReceivedEventArgs e)
        {
            if (e.FolderID == folder && !before.Contains(e.ItemID)) tcs.TrySetResult(e.ItemID);
        }
        c.Inventory.ItemReceived += OnItem;
        c.Inventory.TaskItemReceived += OnTask;
        try
        {
            c.Inventory.MoveTaskInventory(prim.LocalID, item.UUID, folder, c.Network.CurrentSim!);
            UUID got;
            try { got = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(a.Num("timeout", 15)), ct); }
            catch (TimeoutException)
            {
                // the reply may have slipped past the events: look in the folder
                var contents = await c.Inventory.FolderContentsAsync(folder, c.Self.AgentID, true, true, InventorySortOrder.ByDate, ct);
                got = contents.OfType<InventoryItem>().FirstOrDefault(i => !before.Contains(i.UUID) && i.AssetType == item.AssetType)?.UUID
                      ?? throw new LeapError($"{item.Name} didn't arrive in my inventory (it needs copy or transfer rights for me)");
            }
            var mine = await _s.Inventory.ItemAsync(got, ct);
            var o = mine is null ? new JsonObject { ["id"] = got.ToString() } : _s.Inventory.ItemJson(mine);
            o["item_id"] = got.ToString();
            o["from"] = item.UUID.ToString();
            o["folder_id"] = folder.ToString();
            if (_s.Inventory.Node(folder) is InventoryFolder f) o["folder"] = f.Name;
            if (how == CopyOut.Move) o["moved"] = true;
            return o;
        }
        finally
        {
            c.Inventory.ItemReceived -= OnItem;
            c.Inventory.TaskItemReceived -= OnTask;
        }
    }

    /// <summary>
    /// Everything inside a prim into my inventory at once, as the viewer's Copy to Inventory (its Open window:
    /// LLFloaterOpenObject::moveToInventory, then move_inv_category_world_to_agent): a new folder named for the prim at
    /// the top of the inventory (or folder_id), every item sent together and the replies waited for together. Departures
    /// from the viewer's: a no-copy item in an object of mine stays inside unless move: true (the viewer moves those after
    /// a warning, taking them out of the object); an item its rule refuses isn't sent but listed with why (the viewer
    /// sends the whole list once any item passes, and refuses all of it when the first fails); with nothing to copy no
    /// folder is made (the viewer makes it first and says none could be). The region answers each item that came with an
    /// UpdateCreateInventoryItem into the folder and says nothing of one it refused, so what hasn't come once none has
    /// for `timeout` seconds (10) is read from the folder on the grid and the rest listed as missing.
    /// </summary>
    public async Task<JsonObject> CopyAllOutAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var prim = Prim(a);
        var items = await ListAsync(prim, ct);
        var name = a.Str("name") is { Length: > 0 } n ? n.Trim() : prim.Properties?.Name is { Length: > 0 } pn ? pn : "Object";
        bool move = a.Bool("move", false);
        var send = new List<(InventoryItem It, CopyOut How)>();
        var left = new JsonArray();
        var refused = new JsonArray();
        foreach (var it in items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (how, why) = Out(prim, it);
            if (how == CopyOut.Copy || how == CopyOut.Move && move) send.Add((it, how));
            else if (how == CopyOut.Move) left.Add(Brief(it, "no-copy: copying it would move it out of the object"));
            else refused.Add(Brief(it, why!));
        }
        var o = new JsonObject
        {
            ["id"] = prim.ID.ToString(), ["name"] = prim.Properties?.Name ?? "", ["count"] = items.Count,
            ["left"] = left, ["refused"] = refused,
        };
        if (send.Count == 0)
        {
            o["copied"] = new JsonArray();
            o["moved"] = new JsonArray();
            return o;
        }

        UUID folder;
        HashSet<UUID> before;
        if (a.Uuid("folder_id") is { } given)
        {
            if (_s.Inventory.Node(given) is not InventoryFolder) throw new LeapError("folder_id isn't a folder of mine");
            folder = given;
            await _s.Inventory.EnsureLoadedAsync(folder, ct);
            before = c.Inventory.Store!.GetContents(folder).Select(x => x.UUID).ToHashSet();
        }
        else
        {
            var root = _s.Inventory.FolderFor(FolderType.Root);
            var sameName = c.Inventory.Store!.GetContents(root).OfType<InventoryFolder>().Count(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (sameName > 0) o["others_called_that"] = sameName;
            folder = c.Inventory.CreateFolder(root, name);
            before = new HashSet<UUID>();
            // what's moved into a folder before the inventory service has it is lost (Radegast's Open waits 500 ms for it)
            if (!await FolderReadyAsync(folder, ct))
                throw new LeapError($"the inventory didn't make the folder {name} for them in time, so nothing was copied: try again");
        }

        var arrived = new System.Collections.Concurrent.ConcurrentDictionary<UUID, byte>();
        var came = new SemaphoreSlim(0);
        void Got(UUID id, UUID into)
        {
            if (into == folder && !before.Contains(id) && arrived.TryAdd(id, 0)) came.Release();
        }
        void OnItem(object? _, ItemReceivedEventArgs e) => Got(e.Item.UUID, e.Item.ParentUUID);
        void OnTask(object? _, TaskItemReceivedEventArgs e) => Got(e.ItemID, e.FolderID);
        c.Inventory.ItemReceived += OnItem;
        c.Inventory.TaskItemReceived += OnTask;
        try
        {
            var sim = c.Network.CurrentSim!;
            foreach (var (it, _) in send) c.Inventory.MoveTaskInventory(prim.LocalID, it.UUID, folder, sim);
            var quiet = TimeSpan.FromSeconds(a.Num("timeout", 10));
            var end = DateTime.UtcNow + TimeSpan.FromSeconds(60) + TimeSpan.FromMilliseconds(100 * send.Count);
            while (arrived.Count < send.Count && DateTime.UtcNow < end)
                if (!await came.WaitAsync(quiet, ct)) break;
        }
        finally
        {
            c.Inventory.ItemReceived -= OnItem;
            c.Inventory.TaskItemReceived -= OnTask;
        }
        // replies that slipped past the events: the folder as the grid has it
        if (arrived.Count < send.Count)
            foreach (var got in (await c.Inventory.FolderContentsAsync(folder, c.Self.AgentID, false, true, InventorySortOrder.ByDate, ct)).OfType<InventoryItem>())
                if (!before.Contains(got.UUID)) arrived.TryAdd(got.UUID, 0);

        // which came, by type and name (the copy keeps both)
        var waiting = send.ToList();
        var copied = new JsonArray();
        var moved = new JsonArray();
        foreach (var id in arrived.Keys)
        {
            if (_s.Inventory.Node(id) is not InventoryItem got) continue;
            int at = waiting.FindIndex(w => w.It.AssetType == got.AssetType && w.It.Name == got.Name);
            if (at < 0) at = waiting.FindIndex(w => w.It.AssetType == got.AssetType);
            if (at < 0) continue;
            var (from, how) = waiting[at];
            waiting.RemoveAt(at);
            var j = Brief(from);
            j["item_id"] = id.ToString();
            j["from"] = from.UUID.ToString();
            (how == CopyOut.Move ? moved : copied).Add(j);
        }
        o["folder_id"] = folder.ToString();
        o["folder"] = (_s.Inventory.Node(folder) as InventoryFolder)?.Name ?? name;
        o["made"] = a.Uuid("folder_id") is null;
        o["copied"] = copied;
        o["moved"] = moved;
        if (waiting.Count > 0)
            o["missing"] = new JsonArray(waiting.Select(w => (JsonNode)Brief(w.It)).ToArray());
        return o;
    }

    static JsonObject Brief(InventoryItem it, string? why = null)
    {
        var j = new JsonObject { ["name"] = it.Name, ["type"] = AgentInventory.AssetTypeLabel(it.AssetType) };
        if (why != null) j["why"] = why;
        return j;
    }

    /// <summary>Waits until the inventory service lists a folder just made (FetchInventoryDescendents2 answers it rather than
    /// among its bad folders), up to 8 s; without that capability, Radegast's 500 ms.</summary>
    async Task<bool> FolderReadyAsync(UUID folder, CancellationToken ct)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (true)
        {
            var known = await _s.Inventory.FolderOnGridAsync(folder, ct);
            if (known is null) { await Task.Delay(500, ct); return true; }
            if (known == true) return true;
            if (DateTime.UtcNow > end) return false;
            await Task.Delay(250, ct);
        }
    }

    public async Task<JsonObject> RemoveAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var prim = Prim(a);
        var item = await TaskItemAsync(prim, a, ct);
        c.Inventory.RemoveTaskInventory(prim.LocalID, item.UUID, c.Network.CurrentSim!);
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(500, ct);
            if ((await ListAsync(prim, ct)).All(i => i.UUID != item.UUID))
                return new JsonObject { ["removed"] = item.UUID.ToString(), ["name"] = item.Name };
        }
        throw new LeapError($"{item.Name} is still there (removing needs modify rights on the object)");
    }

    /// <summary>A script's running state; set it with running, restart it with reset.</summary>
    public async Task<JsonObject> ScriptAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var prim = Prim(a);
        var item = await TaskItemAsync(prim, a, ct);
        if (item.AssetType != AssetType.LSLText) throw new LeapError($"{item.Name} isn't a script");
        if (a.Bool("running") is { } run)
        {
            c.Inventory.RequestSetScriptRunning(prim.ID, item.UUID, run);
            await Task.Delay(400, ct);
        }
        if (a.Bool("reset", false))
        {
            c.Network.SendPacket(new ScriptResetPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                Script = { ObjectID = prim.ID, ItemID = item.UUID },
            }, c.Network.CurrentSim);
            await Task.Delay(300, ct);
        }
        return new JsonObject
        {
            ["item_id"] = item.UUID.ToString(), ["name"] = item.Name,
            ["running"] = await _s.Scripts.RunningAsync(prim.ID, item.UUID, ct),
        };
    }
}
