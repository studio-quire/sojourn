using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Appearance;

/// <summary>
/// A resident changing its own look: new wearables, edits (shape sliders, skin, tints, textures it uploaded), wearing
/// and taking off, attachments, outfits. Same wire steps as the viewer (asset upload + CreateInventoryItem with the
/// transaction id; edits as a new asset and an item update; Current Outfit folder links), then the appearance is resent
/// (<see cref="Look"/>): client-baked on OpenSim, server-baked on Second Life.
/// </summary>
public sealed class Wardrobe
{
    readonly AgentSession _s;
    readonly Dictionary<UUID, Wearable> _unsaved = new();   // item id -> edits made with save=false

    public Wardrobe(AgentSession s) => _s = s;

    Look Look => _s.Look;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    // ---------- listing ----------

    public async Task<JsonObject> ListAsync(CancellationToken ct)
    {
        var c = C;
        var worn = await Look.ReadWornAsync(c, ct);
        var list = new JsonArray();
        var index = new Dictionary<WearableType, int>();
        foreach (var w in worn.Where(w => w.IsWearable && w.Type != WearableType.Invalid))
        {
            index[w.Type] = index.TryGetValue(w.Type, out var i) ? i + 1 : 0;
            list.Add(await DescribeAsync(w, index[w.Type], ct));
        }
        return new JsonObject { ["bakes_on_mesh"] = Look.ServerBaking(c), ["wearables"] = list };
    }

    async Task<JsonObject> DescribeAsync(WornItem w, int index, CancellationToken ct)
    {
        var unsaved = _unsaved.TryGetValue(w.ItemId, out var edited);
        var a = edited ?? await Look.WearableAsync(w.AssetId, w.AssetType, ct);
        var tex = new JsonObject();
        foreach (var slot in Slots.For(w.Type))
            tex[slot.Name] = (a?.Textures.TryGetValue((int)slot.Index, out var id) == true ? id : slot.Default).ToString();
        var color = new JsonArray(1.0, 1.0, 1.0);
        if (a is not null && Slots.TintParams(w.Type) is { } tp)
            color = new JsonArray(tp.Select(p => (JsonNode)Math.Round(a.Params.TryGetValue(p, out var v) ? v : Default(p), 3)).ToArray());
        return new JsonObject
        {
            ["type"] = Slots.TypeName(w.Type), ["index"] = index, ["item_id"] = w.ItemId.ToString(), ["asset_id"] = w.AssetId.ToString(),
            ["name"] = w.Name, ["modifiable"] = (w.Item.Permissions.OwnerMask & PermissionMask.Modify) != 0, ["unsaved"] = unsaved,
            ["textures"] = tex, ["color"] = color,
        };
    }

    static float Default(int param) => VisualParams.Params.TryGetValue(param, out var p) ? p.DefaultValue : 0;

    // ---------- create ----------

    /// <summary>Uploads the asset and creates the inventory item from the transaction (the viewer's createWearable).</summary>
    public async Task<InventoryItem> CreateItemAsync(GridClient c, Wearable w, UUID? folder, CancellationToken ct)
    {
        var tx = UUID.Random();
        var assetId = await c.Assets.RequestUploadAsync(w.AssetType, w.Encode(), false, tx, ct);
        if (assetId == UUID.Zero) throw new LeapError("the asset upload failed");
        var folderId = folder is { } f && f != UUID.Zero ? f
            : c.Inventory.FindFolderForType(w.IsBodyPart ? FolderType.BodyPart : FolderType.Clothing);
        var item = await c.Inventory.CreateItemAsync(folderId, w.Name, w.Description, w.AssetType, tx, InventoryType.Wearable,
            w.Type, (PermissionMask)w.NextOwnerMask, ct) ?? throw new LeapError("the region didn't create the item");
        Look.Remember(item.AssetUUID != UUID.Zero ? item.AssetUUID : assetId, w);
        return item;
    }

    public async Task<JsonObject> CreateAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var type = Slots.Parse(a.Str("type")) ?? throw new LeapError($"unknown wearable type {a.Str("type")}; types: shape skin hair eyes shirt pants shoes socks jacket gloves undershirt underpants skirt alpha tattoo physics universal");
        var name = a.Str("name") ?? "New " + char.ToUpperInvariant(Slots.TypeName(type)[0]) + Slots.TypeName(type)[1..];
        var w = Wearable.New(type, name, c.Self.AgentID);
        w.Description = a.Str("desc") ?? "";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(a.Num("timeout", 30)));
        var item = await CreateItemAsync(c, w, a.Uuid("folder_id"), cts.Token);
        var worn = false;
        if (a.Bool("wear", true))
        {
            await WearAsync(c, new[] { item }, a.Bool("replace", false), cts.Token);
            worn = true;
        }
        return new JsonObject { ["item_id"] = item.UUID.ToString(), ["worn"] = worn };
    }

    // ---------- edit ----------

    public async Task<JsonObject> EditAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var id = a.Uuid("item_id") ?? throw new LeapError("need item_id (a worn wearable)");
        var worn = (await Look.ReadWornAsync(c, ct)).FirstOrDefault(w => w.ItemId == id && w.IsWearable)
                   ?? throw new LeapError("that wearable isn't worn (editWearable edits worn wearables)");
        if ((worn.Item.Permissions.OwnerMask & PermissionMask.Modify) == 0) throw new LeapError("no modify permission on that wearable");
        var current = _unsaved.TryGetValue(id, out var u) ? u : await Look.WearableAsync(worn.AssetId, worn.AssetType, ct)
                      ?? throw new LeapError("couldn't load the wearable's asset");
        var w = Wearable.Parse(current.Encode());   // a copy

        var textures = new Dictionary<string, JsonNode?>();
        if (a["textures"] is JsonObject to) foreach (var (k, v) in to) textures[k] = v;
        if (a.Str("slot") is { } slotName) textures[slotName] = a["texture_id"];
        foreach (var (key, v) in textures)
        {
            var slot = Slots.Find(w.Type, key) ?? throw new LeapError(
                $"{Slots.TypeName(w.Type)} has no texture slot {key}; slots: {string.Join(" ", Slots.For(w.Type).Select(s => s.Name))}");
            w.Textures[(int)slot.Index] = await TextureIdAsync(v, slot.Default, ct);
        }
        if (a.Nums("color") is { } col)
        {
            var tp = Slots.TintParams(w.Type) ?? throw new LeapError($"{Slots.TypeName(w.Type)} has no tint");
            if (col.Length < 3) throw new LeapError("color: [r, g, b], 0-1");
            for (var i = 0; i < 3; i++) w.Params[tp[i]] = (float)Math.Clamp(col[i], 0, 1);
        }
        if (a["params"] is JsonObject po)
            foreach (var (k, v) in po)
            {
                var p = FindParam(w.Type, k);
                var value = Json.AsDouble(v) ?? throw new LeapError($"param {k}: give a number");
                w.Params[p.ParamID] = (float)Math.Clamp(value, p.MinValue, p.MaxValue);
            }
        if (a.Str("name") is { } newName) w.Name = newName;

        var saved = false;
        if (a.Bool("save", true))
        {
            await SaveAsync(c, worn, w, ct);
            _unsaved.Remove(id);
            saved = true;
        }
        else _unsaved[id] = w;
        var after = (await Look.ReadWornAsync(c, ct)).FirstOrDefault(x => x.ItemId == id) ?? worn;
        var index = Look.Worn.Where(x => x.Type == after.Type).ToList().FindIndex(x => x.ItemId == id);
        var o = await DescribeAsync(after, Math.Max(0, index), ct);
        o["saved"] = saved;
        return o;
    }

    async Task<UUID> TextureIdAsync(JsonNode? v, UUID dflt, CancellationToken ct)
    {
        if (v is null) return dflt;
        var s = v.GetValue<string>();
        if (!UUID.TryParse(s, out var id)) throw new LeapError($"not a texture id: {s}");
        if (id == UUID.Zero) return dflt;
        // a texture inventory item stands for its asset
        await Task.CompletedTask;
        return _s.Inventory.Node(id) is InventoryItem { AssetType: AssetType.Texture } it ? it.AssetUUID : id;
    }

    /// <summary>A visual param of this wearable type by id, name (avatar_lad) or label, spaces and underscores alike.</summary>
    public static VisualParam FindParam(WearableType type, string key)
    {
        var ps = Wearable.VisualParamsFor(type).ToList();
        string Norm(string? x) => (x ?? "").Replace('_', ' ').Trim().ToLowerInvariant();
        var i = int.TryParse(key, out var pid) ? ps.FindIndex(x => x.ParamID == pid) : ps.FindIndex(x => Norm(x.Name) == Norm(key));
        if (i < 0) i = ps.FindIndex(x => Norm(x.Label) == Norm(key));
        if (i >= 0) return ps[i];
        // every word of the key in the name or label ("hair length" finds a param labelled "Length" named hair_...)
        var words = Norm(key).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = ps.Where(x => words.All(w => (Norm(x.Name) + " " + Norm(x.Label)).Contains(w))).ToList();
        if (hits.Count == 1) return hits[0];
        var near = (hits.Count > 0 ? hits : ps.Where(x => words.Any(w => (Norm(x.Name) + " " + Norm(x.Label)).Contains(w))))
            .Take(8).Select(x => $"{x.Name} ({x.Label})");
        var hint = string.Join(", ", near);
        throw new LeapError($"{Slots.TypeName(type)} has no param {key}{(hint.Length > 0 ? $"; close: {hint}" : "")} (`wearableParams {{type}}` lists them)");
    }

    /// <summary>The appearance editor's Save: a new asset, the item updated to it, the outfit link refreshed, resend.</summary>
    async Task SaveAsync(GridClient c, WornItem worn, Wearable w, CancellationToken ct)
    {
        var tx = UUID.Random();
        w.Owner = c.Self.AgentID;
        w.Group = UUID.Zero;
        var assetId = await c.Assets.RequestUploadAsync(w.AssetType, w.Encode(), false, tx, ct);
        if (assetId == UUID.Zero) throw new LeapError("the asset upload failed");
        var item = worn.Item;
        item.TransactionID = tx;
        item.Name = w.Name;
        c.Inventory.RequestUpdateItems(new List<InventoryItem> { item }, tx);
        item.AssetUUID = assetId;
        Look.Remember(assetId, w);
        // relink so the outfit folder's version moves (server baking keys on it)
        await _s.Look.UnlinkAsync(c, new[] { worn.LinkId }, ct);
        await LinkAsync(c, item, ct);
        await Task.Delay(500, ct);   // let the region apply the item update before the outfit is read again
        Look.Schedule(c, TimeSpan.FromMilliseconds(300), reupload: false, "wearable saved");
    }

    public JsonObject Params(JsonObject a)
    {
        var type = Slots.Parse(a.Str("type")) ?? throw new LeapError("need type");
        var list = new JsonArray();
        foreach (var p in Wearable.VisualParamsFor(type).OrderBy(p => p.ParamID))
            list.Add(new JsonObject
            {
                ["id"] = p.ParamID, ["name"] = p.Name, ["label"] = p.Label, ["min"] = p.MinValue, ["max"] = p.MaxValue,
                ["default"] = p.DefaultValue, ["label_min"] = p.LabelMin, ["label_max"] = p.LabelMax,
                ["tint"] = Slots.TintParams(type)?.Contains(p.ParamID) == true,
            });
        return new JsonObject
        {
            ["type"] = Slots.TypeName(type), ["params"] = list,
            ["slots"] = new JsonArray(Slots.For(type).Select(s => (JsonNode)s.Name).ToArray()),
        };
    }

    // ---------- wearing ----------

    /// <summary>An RLV lock's refusal (docs/rlv.md), for every caller: the person's, the assistant's and RLV's own.</summary>
    void Rlv(Func<Sojourn.Core.Rlv.RlvEngine, string?> check)
    {
        if (_s.RlvActive && check(_s.Rlv) is { } why) throw new LeapError(_s.RlvRefused(why)!);
    }

    async Task LinkAsync(GridClient c, InventoryItem item, CancellationToken ct)
    {
        var cof = await Look.CofAsync(c, ct);
        await c.Inventory.CreateLinkAsync(cof, item.UUID, item.Name, item.Description, item.InventoryType, UUID.Random(), ct);
    }

    /// <summary>Wears wearables (body parts replace their type; clothing adds a layer unless replace) and attaches
    /// objects (added to the point, or replacing what's there with replace), as the viewer's wearItemsOnAvatar.</summary>
    public async Task WearAsync(GridClient c, IEnumerable<InventoryItem> items, bool replace, CancellationToken ct)
    {
        var worn = await Look.ReadWornAsync(c, ct);
        var unlink = new List<UUID>();
        var link = new List<InventoryItem>();
        var wearablesChanged = false;
        foreach (var item in items)
        {
            if (worn.Any(w => w.ItemId == item.UUID)) continue;
            // RLV's locks (docs/rlv.md): an object goes where it last was (its flags' low byte), replacing what's there
            if (item.AssetType == AssetType.Object) Rlv(r => r.CanAttach(item.UUID, (int)(item.Flags & 0xFF), add: !replace));
            else if (item.AssetType is AssetType.Bodypart or AssetType.Clothing)
                Rlv(r => r.CanWear(item.UUID, (int)(item.Flags & 0xFF), add: !replace && !Slots.IsBodyPart((WearableType)(item.Flags & 0xFF))));
            if (item.AssetType is AssetType.Bodypart or AssetType.Clothing)
            {
                var type = (WearableType)(item.Flags & 0xFF);
                if (Slots.IsBodyPart(type) || replace)
                    unlink.AddRange(worn.Where(w => w.IsWearable && w.Type == type).Select(w => w.LinkId));
                link.Add(item);
                wearablesChanged = true;
            }
            else if (item.AssetType == AssetType.Object)
            {
                c.Appearance.Attach(item, AttachmentPoint.Default, replace);
                link.Add(item);
            }
            else throw new LeapError($"{item.Name} can't be worn ({AgentInventory.AssetName(item.AssetType)})");
        }
        unlink.AddRange(Look.BrokenLinks);
        unlink.RemoveAll(id => worn.Any(w => w.ItemId == id));   // never the items themselves
        if (unlink.Count > 0) await _s.Look.UnlinkAsync(c, unlink, ct);
        foreach (var it in link) await LinkAsync(c, it, ct);
        if (wearablesChanged) Look.Schedule(c, TimeSpan.FromMilliseconds(800), reupload: false, "wearing");
    }

    /// <summary>Takes items off: wearables (not body parts) leave the outfit; attachments detach.</summary>
    public async Task TakeOffAsync(GridClient c, IEnumerable<UUID> itemIds, CancellationToken ct)
    {
        var worn = await Look.ReadWornAsync(c, ct);
        var unlink = new List<UUID>();
        var wearablesChanged = false;
        var ids = itemIds.ToList();
        foreach (var id in ids)
            if (!worn.Any(x => x.ItemId == id) && !Attached(c, id))
                throw new LeapError($"{(_s.Inventory.Node(id) as InventoryItem)?.Name ?? id.ToString()} isn't worn (nothing was taken off)");
        foreach (var id in ids) Rlv(r => r.CanTakeOff(id));
        foreach (var id in ids)
        {
            var w = worn.FirstOrDefault(x => x.ItemId == id);
            if (w is not null && w.IsWearable)
            {
                if (Slots.IsBodyPart(w.Type)) throw new LeapError($"{w.Name} is a body part; wear another {Slots.TypeName(w.Type)} to replace it");
                unlink.Add(w.LinkId);
                _unsaved.Remove(id);
                wearablesChanged = true;
                continue;
            }
            c.Appearance.Detach(id);
            if (w is not null) unlink.Add(w.LinkId);
        }
        if (unlink.Count > 0) await _s.Look.UnlinkAsync(c, unlink, ct);
        if (wearablesChanged) Look.Schedule(c, TimeSpan.FromMilliseconds(800), reupload: false, "taking off");
    }

    /// <summary>An attachment of that item on me, as the region shows it (worn from elsewhere, it may have no outfit link).</summary>
    bool Attached(GridClient c, UUID itemId)
    {
        var w = _s.Region;
        var me = w?.AvatarById(c.Self.AgentID);
        return me is not null && w!.Prims.Values.Any(p => p.ParentID == me.LocalID && AgentInventory.AttachItemId(p) == itemId);
    }

    /// <summary>Attach (TesselAgent attach) with the outfit link the viewer keeps for attachments.</summary>
    public async Task AttachAsync(GridClient c, InventoryItem item, AttachmentPoint point, bool replace, CancellationToken ct)
    {
        Rlv(r => r.CanAttach(item.UUID, point == AttachmentPoint.Default ? (int)(item.Flags & 0xFF) : (int)point, add: !replace));
        c.Appearance.Attach(item, point, replace);
        var worn = await Look.ReadWornAsync(c, ct);
        if (replace && point != AttachmentPoint.Default)
        {
            // what was on that point comes off; drop its outfit link too
            var w = _s.Region;
            var me = w?.AvatarById(c.Self.AgentID);
            if (w is not null && me is not null)
            {
                var gone = w.Prims.Values.Where(p => p.ParentID == me.LocalID && (int)p.PrimData.AttachmentPoint == (int)point)
                    .Select(AgentInventory.AttachItemId).ToHashSet();
                var stale = worn.Where(x => gone.Contains(x.ItemId)).Select(x => x.LinkId).ToList();
                if (stale.Count > 0) await _s.Look.UnlinkAsync(c, stale, ct);
            }
        }
        if (!worn.Any(x => x.ItemId == item.UUID)) await LinkAsync(c, item, ct);
    }

    public async Task DetachAsync(GridClient c, UUID itemId, CancellationToken ct)
    {
        Rlv(r => r.CanTakeOff(itemId));
        c.Appearance.Detach(itemId);
        var worn = await Look.ReadWornAsync(c, ct);
        var links = worn.Where(x => x.ItemId == itemId).Select(x => x.LinkId).ToList();
        if (links.Count > 0) await _s.Look.UnlinkAsync(c, links, ct);
    }

    // ---------- outfits ----------

    async Task<List<InventoryItem>> FolderItemsAsync(GridClient c, UUID folder, CancellationToken ct)
    {
        var contents = await c.Inventory.FolderContentsAsync(folder, c.Self.AgentID, false, true, InventorySortOrder.ByName, ct)
                       ?? new List<InventoryBase>();
        var items = new List<InventoryItem>();
        foreach (var it in contents.OfType<InventoryItem>())
        {
            var target = it.AssetType is AssetType.Link ? await _s.Inventory.ItemAsync(it.AssetUUID, ct) : it;
            if (target is not null && target.AssetType is AssetType.Bodypart or AssetType.Clothing or AssetType.Object) items.Add(target);
        }
        return items;
    }

    /// <summary>LLAppearance wearOutfit: the folder's items become the outfit (append adds them instead).</summary>
    public async Task WearOutfitAsync(GridClient c, UUID folder, bool append, CancellationToken ct)
    {
        var items = await FolderItemsAsync(c, folder, ct);
        if (items.Count == 0) throw new LeapError("that folder has nothing wearable");
        if (!append)
        {
            var keep = items.Select(i => i.UUID).ToHashSet();
            var newTypes = items.Where(i => i.AssetType is AssetType.Bodypart).Select(i => (WearableType)(i.Flags & 0xFF)).ToHashSet();
            var worn = await Look.ReadWornAsync(c, ct);
            var off = worn.Where(w => !keep.Contains(w.ItemId) &&
                                      (w.AssetType is AssetType.Clothing or AssetType.Object ||
                                       (w.AssetType is AssetType.Bodypart && newTypes.Contains(w.Type)))).ToList();
            // what RLV keeps on stays on, as the viewer's replace-outfit leaves locked things worn
            if (_s.RlvActive) off = off.Where(w => w.AssetType == AssetType.Bodypart || _s.Rlv.CanTakeOff(w.ItemId) == null).ToList();
            foreach (var w in off.Where(w => w.AssetType == AssetType.Object)) c.Appearance.Detach(w.ItemId);
            if (off.Count > 0) await _s.Look.UnlinkAsync(c, off.Select(w => w.LinkId), ct);
        }
        await WearAsync(c, items, replace: false, ct);
        Look.Schedule(c, TimeSpan.FromMilliseconds(800), reupload: false, "outfit");
    }

    /// <summary>The viewer's "Save outfit as": a folder in My Outfits with links to everything worn.</summary>
    public async Task<JsonObject> SaveOutfitAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var name = a.Str("name") ?? throw new LeapError("need name");
        var parent = a.Uuid("folder_id") ?? c.Inventory.FindFolderForType(FolderType.MyOutfits);
        if (parent == UUID.Zero || parent == c.Inventory.Store?.RootFolder?.UUID)
            parent = c.Inventory.CreateFolder(c.Inventory.Store!.RootFolder!.UUID, "My Outfits", FolderType.MyOutfits);
        var worn = await Look.ReadWornAsync(c, ct);
        var folder = c.Inventory.CreateFolder(parent, name, FolderType.Outfit);
        await Task.Delay(500, ct);
        foreach (var w in worn)
            await c.Inventory.CreateLinkAsync(folder, w.ItemId, w.Name, w.Item.Description, w.Item.InventoryType, UUID.Random(), ct);
        return new JsonObject { ["folder_id"] = folder.ToString(), ["items"] = worn.Count };
    }

    public async Task<JsonObject> OutfitsAsync(CancellationToken ct)
    {
        var c = C;
        var root = c.Inventory.FindFolderForType(FolderType.MyOutfits);
        var outfits = new JsonObject();
        if (root != UUID.Zero && root != c.Inventory.Store?.RootFolder?.UUID)
        {
            var contents = await c.Inventory.FolderContentsAsync(root, c.Self.AgentID, true, false, InventorySortOrder.ByName, ct) ?? new();
            foreach (var f in contents.OfType<InventoryFolder>())
                if (f.PreferredType == FolderType.Outfit) outfits[f.UUID.ToString()] = f.Name;
        }
        return new JsonObject { ["outfits"] = outfits };
    }

    public async Task<JsonObject> OutfitItemsAsync(UUID outfit, CancellationToken ct)
    {
        var c = C;
        if (_s.Inventory.Node(outfit) is not InventoryFolder { PreferredType: FolderType.Outfit })
            throw new LeapError($"Couldn't find outfit {outfit}");
        var worn = (await Look.ReadWornAsync(c, ct)).Select(w => w.ItemId).ToHashSet();
        var items = new JsonObject();
        foreach (var it in await FolderItemsAsync(c, outfit, ct))
            items[it.UUID.ToString()] = new JsonObject
            {
                ["name"] = it.Name,
                ["wearable_type"] = it.AssetType is AssetType.Bodypart or AssetType.Clothing ? Slots.TypeName((WearableType)(it.Flags & 0xFF)) : "invalid",
                ["is_worn"] = worn.Contains(it.UUID),
            };
        return new JsonObject { ["items"] = items };
    }
}
