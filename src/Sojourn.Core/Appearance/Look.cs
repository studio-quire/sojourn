using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Imaging;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Appearance;

/// <summary>A worn item as the Current Outfit folder links it.</summary>
public sealed record WornItem(UUID ItemId, UUID AssetId, WearableType Type, AssetType AssetType, UUID LinkId, string Name, InventoryItem Item)
{
    public bool IsWearable => AssetType is AssetType.Bodypart or AssetType.Clothing;
}

/// <summary>
/// One resident's appearance. LibreMetaverse's automatic appearance is off (it rebakes all eleven bakes at every
/// login and teleport); this drives it instead:
/// - grids without server baking (OpenSim): read the outfit from the Current Outfit folder, composite the bakes
///   with LibreMetaverse's baker only when the outfit changed (<see cref="BakeCache"/>), upload, AgentSetAppearance;
/// - Second Life (server baking): keep the Current Outfit folder right and ask the region to bake (UpdateAvatarAppearance
///   with the COF version), as the viewer does; rez the outfit's attachments once at login.
/// </summary>
public sealed class Look
{
    readonly AgentSession _s;
    readonly SemaphoreSlim _gate = new(1);
    static readonly ConcurrentDictionary<(string, UUID), Wearable> Parsed = new();   // wearable assets never change
    uint _serial;
    readonly UUID[] _sentBakes = new UUID[AppearanceManager.BAKED_TEXTURE_COUNT];
    readonly UUID[] _sentCacheIds = new UUID[AppearanceManager.BAKED_TEXTURE_COUNT];
    CancellationTokenSource? _pending;
    readonly Dictionary<WearableType, (WornItem W, Wearable A)> _bodyParts = new();
    int _lastCofRequested = -1, _lastCofReceived = -1;
    bool _serverBakedGrid;   // a region of this login baked on the server: the grid does (Second Life)

    public string Status { get; private set; } = "not sent yet";
    public DateTime? SentAt { get; private set; }
    public int BakedHere { get; private set; }      // bakes composited by this resident since the host started
    public int ReusedHere { get; private set; }     // bakes taken from the cache
    public IReadOnlyList<WornItem> Worn { get; private set; } = Array.Empty<WornItem>();
    /// <summary>Outfit links whose item is gone (deleted elsewhere); outfit changes clear them.</summary>
    public IReadOnlyList<UUID> BrokenLinks { get; private set; } = Array.Empty<UUID>();

    public Look(AgentSession s) => _s = s;

    (Primitive.TextureEntry Te, byte[] Params)? _selfLook;

    /// <summary>OpenSim sends an avatar's appearance to every other agent but not to the avatar itself, so a resident
    /// alone in a region would see itself (and render, in the shared world) as a cloud. Put what was sent onto my own
    /// avatar object, as an AvatarAppearance from the region would.</summary>
    void ShowSelf(GridClient c)
    {
        if (_selfLook is not { } look || c.Network.CurrentSim is not { } sim) return;
        foreach (var av in sim.ObjectsAvatars.Values)
        {
            if (av is null || av.ID != c.Self.AgentID) continue;
            lock (av)
            {
                av.Textures = look.Te;
                av.VisualParameters = look.Params.ToArray();
            }
        }
    }

    BakeCache Cache => _s.World.Bakes(_s.GridKey);
    public static bool ServerBaking(GridClient c) => c.Appearance.ServerBakingRegion();

    public void Wire(GridClient c)
    {
        c.Settings.Agent.SendAppearance = false;
        // an update of my own avatar object may come with the region's older texture entry; keep my sent look on it
        c.Objects.AvatarUpdate += (_, e) => _s.Guard("look", () => { if (e.Avatar.ID == c.Self.AgentID) ShowSelf(c); });
        c.Appearance.RebakeAvatarRequested += (_, e) => _s.Guard("rebake", () =>
        {
            _s.Log.LogInformation("region asked for a rebake ({Texture})", e.TextureID);
            Schedule(c, TimeSpan.FromSeconds(1), reupload: true, "the region asked for it");
        });
        c.Network.SimChanged += (_, _) => _s.Guard("look", () =>
        {
            _regionAt = DateTime.UtcNow;
            // CSB: the region keeps the appearance across a crossing/teleport; resend the same bakes so a region that
            // lacks them asks for a rebake. SSB: nothing (as the viewer). A region whose handshake hasn't come yet says
            // neither (its protocols are still 0, as on Second Life after a crossing into a neighbour that never shook
            // hands): the grid's way is the one it showed before.
            if (!ServerBaking(c) && !_serverBakedGrid && SentAt is not null) Schedule(c, TimeSpan.FromSeconds(3), reupload: false, "new region");
        });
    }

    /// <summary>After login: dress a new resident (local grids), then send the appearance; on SL rez the outfit's attachments.</summary>
    public void OnLoggedIn(GridClient c)
    {
        SentAt = null;   // the login send comes first; region changes resend after it
        _serverBakedGrid = false;
        Array.Clear(_sentBakes);
        Array.Clear(_sentCacheIds);
        _lastCofRequested = _lastCofReceived = -1;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);
                // the outfit links resolve from the inventory, which loads in the background after login
                if (_s.Inventory.Loading is { } loading) await Task.WhenAny(loading, Task.Delay(TimeSpan.FromSeconds(60)));
                if (_s.Client != c) return;
                RestoreActiveGestures(c);
                StarterKit.LearnNames(_s);
                if (!ServerBaking(c) && (_s.Config.AutoLook ?? false))
                {
                    await new LookGenerator(_s).DressIfNeededAsync(c, CancellationToken.None);
                    try { await new StarterKit(_s).GiveIfNeededAsync(c, CancellationToken.None); }
                    catch (Exception e) { _s.Log.LogWarning(e, "starter kit"); }
                }
                await SendAsync(c, reupload: true, "login", CancellationToken.None);
                if (ServerBaking(c)) await RezOutfitAttachmentsAsync(c, CancellationToken.None);
            }
            catch (Exception e) { _s.Log.LogWarning(e, "appearance at login"); Status = $"failed at login: {e.Message}"; }
        });
    }

    /// <summary>LibreMetaverse 3.2.0 keeps the login response's active gestures (its "gestures" list, which the official
    /// viewer activates) but never makes them active, and OpenSim marks an active gesture's item with flag 1: both go
    /// into the active list once the inventory has loaded, and their assets are read so their triggers work.</summary>
    void RestoreActiveGestures(GridClient c)
    {
        if (c.Network.LoginResponseData?.Gestures is { } atLogin)
            foreach (var (item, asset) in atLogin)
                if (item != UUID.Zero && asset != UUID.Zero) c.Self.ActiveGestures[item] = asset;
        try { RestoreFlaggedGestures(c); }
        finally { _s.Gestures.WarmTriggers(); }
    }

    void RestoreFlaggedGestures(GridClient c)
    {
        var store = c.Inventory.Store;
        if (store?.RootFolder is not { } root) return;
        var stack = new Stack<UUID>();
        stack.Push(root.UUID);
        var seen = 0;
        while (stack.Count > 0 && seen++ < 20000)
            foreach (var n in store.GetContents(stack.Pop()))
            {
                if (n is InventoryFolder f) { if (f.PreferredType != FolderType.Trash) stack.Push(f.UUID); }
                else if (n is InventoryItem { AssetType: AssetType.Gesture } g && (g.Flags & 1) != 0) c.Self.ActiveGestures[g.UUID] = g.AssetUUID;
            }
    }

    /// <summary>Sends the appearance after a short delay, coalescing bursts (several wear changes in a row).</summary>
    public void Schedule(GridClient c, TimeSpan delay, bool reupload, string why)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _pending, cts)?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
                if (_s.Client == c) await SendAsync(c, reupload, why, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { _s.Log.LogWarning(e, "appearance"); Status = $"failed: {e.Message}"; }
        });
    }

    // ---------- the outfit ----------

    public async Task<UUID> CofAsync(GridClient c, CancellationToken ct)
    {
        var cof = c.Inventory.FindFolderForType(FolderType.CurrentOutfit);
        if (cof == UUID.Zero || cof == c.Inventory.Store?.RootFolder?.UUID)
        {
            var root = c.Inventory.Store?.RootFolder?.UUID ?? throw new InvalidOperationException("no inventory");
            cof = c.Inventory.CreateFolder(root, "Current Outfit", FolderType.CurrentOutfit);
            await Task.Delay(500, ct);
        }
        return cof;
    }

    /// <summary>The Current Outfit folder's links, resolved (fetched fresh: other code may have changed it).</summary>
    public async Task<List<WornItem>> ReadWornAsync(GridClient c, CancellationToken ct)
    {
        var cof = await CofAsync(c, ct);
        // OpenSim answers a COF fetch with the links *and* their target items (so viewers have them): keep only what
        // sits in the folder, or a "link" id could be a real item's. The store isn't purged by a fetch, so links
        // removed since (here or elsewhere) are dropped from it by hand.
        var fresh = await c.Inventory.FolderContentsAsync(cof, c.Self.AgentID, false, true, InventorySortOrder.ByDate, ct);
        var contents = fresh.OfType<InventoryItem>().Where(i => i.ParentUUID == cof).Cast<InventoryBase>().ToList();
        if (c.Inventory.Store is { } store)
            foreach (var stale in store.GetContents(cof).OfType<InventoryItem>().Where(x => contents.All(e => e.UUID != x.UUID)).ToList())
                store.RemoveNodeFor(stale);
        var list = new List<WornItem>();
        var seen = new HashSet<UUID>();
        var broken = new List<UUID>();
        foreach (var link in contents.OfType<InventoryItem>().OrderBy(i => i.CreationDate))
        {
            var target = link.AssetType == AssetType.Link ? link.AssetUUID : link.UUID;
            if (!seen.Add(target)) continue;
            InventoryItem? item = link;
            if (link.AssetType == AssetType.Link)
            {
                // once the inventory has loaded, the store is the truth (a fetch for a deleted item never answers)
                if (c.Inventory.Store?.TryGetValue<InventoryItem>(target, out var known) == true) item = known;
                else if (_s.Inventory.Loading is { IsCompleted: true }) item = null;
                else
                {
                    using var one = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    one.CancelAfter(TimeSpan.FromSeconds(5));
                    try { item = await _s.Inventory.ItemAsync(target, one.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { item = null; }
                }
            }
            if (item is null) { if (link.AssetType == AssetType.Link) broken.Add(link.UUID); continue; }
            var type = item.AssetType is AssetType.Bodypart or AssetType.Clothing ? (WearableType)(item.Flags & 0xFF) : WearableType.Invalid;
            list.Add(new WornItem(item.UUID, item.AssetUUID, type, item.AssetType, link.UUID, item.Name, item));
        }
        // the viewer orders layers by the link description ("@N"); keep COF (creation) order otherwise
        Worn = list;
        BrokenLinks = broken;
        return list;
    }

    /// <summary>Removes outfit links, and only links: anything else in the list is left alone (deleting a link's
    /// target would delete the resident's item).</summary>
    public async Task UnlinkAsync(GridClient c, IEnumerable<UUID> linkIds, CancellationToken ct)
    {
        var links = linkIds.Distinct().Where(id => c.Inventory.Store?.TryGetValue<InventoryItem>(id, out var it) == true
                                                   && it.AssetType is AssetType.Link).ToList();
        if (links.Count == 0) return;
        await c.Inventory.RemoveItemsAsync(links, ct);
        await Task.Delay(400, ct);   // UDP; let the region apply it before the folder is fetched again
    }

    public async Task<Wearable?> WearableAsync(UUID asset, AssetType type, CancellationToken ct)
    {
        if (Parsed.TryGetValue((_s.GridKey, asset), out var w)) return w;
        var a = await _s.World.AssetAsync(_s.GridKey, asset, type, ct);
        if (a?.AssetData is not { Length: > 0 } data) return null;
        try { w = Wearable.Parse(data); }
        catch (FormatException) { return null; }
        if (Parsed.Count > 5000) Parsed.Clear();
        Parsed[(_s.GridKey, asset)] = w;
        return w;
    }

    public void Remember(UUID asset, Wearable w) => Parsed[(_s.GridKey, asset)] = w;

    // ---------- sending ----------

    public async Task SendAsync(GridClient c, bool reupload, string why, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (ServerBaking(c) || _serverBakedGrid)
            {
                _serverBakedGrid = true;
                await SendServerBakedAsync(c, ct);
            }
            else await SendClientBakedAsync(c, reupload, ct);
            SentAt = DateTime.UtcNow;
            _s.Log.LogInformation("appearance sent ({Why}): {Status}", why, Status);
        }
        finally { _gate.Release(); }
    }

    async Task SendClientBakedAsync(GridClient c, bool reupload, CancellationToken ct)
    {
        var worn = await ReadWornAsync(c, ct);
        var wearables = new List<(WornItem W, Wearable A)>();
        foreach (var w in worn.Where(w => w.IsWearable && w.Type != WearableType.Invalid)
                     .OrderBy(w => Slots.IsBodyPart(w.Type) ? 0 : 1))
            if (await WearableAsync(w.AssetId, w.AssetType, ct) is { } a) wearables.Add((w, a));
        // Body parts can't be taken off. A read that lacks one (a fetch racing a change on the region) keeps the last
        // one rather than sending a default body.
        foreach (var t in new[] { WearableType.Shape, WearableType.Skin, WearableType.Hair, WearableType.Eyes })
        {
            var have = wearables.FirstOrDefault(x => x.W.Type == t);
            if (have.W is not null) _bodyParts[t] = have;
            else if (_bodyParts.TryGetValue(t, out var last))
            {
                _s.Log.LogWarning("the outfit read lacked a {Type}; keeping {Name}", t, last.W.Name);
                wearables.Insert(0, last);
            }
        }

        // Without all four body parts the viewer doesn't bake, and others see a cloud; the same here (an account made
        // without default wearables starts that way, and stops being a cloud once it wears a shape, skin, hair and eyes).
        var missing = new[] { WearableType.Shape, WearableType.Skin, WearableType.Hair, WearableType.Eyes }
            .Where(t => wearables.All(x => x.W.Type != t)).ToList();
        if (missing.Count > 0)
        {
            Status = "no body yet, so a cloud to others: missing " + string.Join(", ", missing.Select(t => t.ToString().ToLowerInvariant()));
            return;
        }

        var textures = new AppearanceManager.TextureData[(int)AvatarTextureIndex.NumberOfEntries];
        for (var i = 0; i < textures.Length; i++) textures[i] = new AppearanceManager.TextureData { TextureIndex = (AvatarTextureIndex)i };
        foreach (var (w, a) in wearables)
            AppearanceManager.DecodeWearableParams(ToLmv(w, a), ref textures);

        var types = wearables.Select(x => x.W.Type).ToHashSet();
        var needed = new List<BakeType> { BakeType.Head, BakeType.UpperBody, BakeType.LowerBody, BakeType.Eyes, BakeType.Hair };
        if (types.Contains(WearableType.Skirt)) needed.Add(BakeType.Skirt);
        if (types.Contains(WearableType.Universal))
            needed.AddRange(new[] { BakeType.BakedLeftArm, BakeType.BakedLeftLeg, BakeType.BakedAux1, BakeType.BakedAux2, BakeType.BakedAux3 });

        var cacheIds = new UUID[AppearanceManager.BAKED_TEXTURE_COUNT];
        for (var i = 0; i < cacheIds.Length; i++) cacheIds[i] = CacheId(i, wearables.Select(x => x.W));

        var te = new Primitive.TextureEntry(AppearanceManager.DEFAULT_AVATAR_TEXTURE);
        int baked = 0, reused = 0;
        foreach (var bake in needed)
        {
            var i = (int)bake;
            var index = AppearanceManager.BakeTypeToAgentTextureIndex(bake);
            UUID id;
            if (!reupload && _sentCacheIds[i] == cacheIds[i] && _sentBakes[i] != UUID.Zero) id = _sentBakes[i];
            else
            {
                var bytes = Cache.Get(cacheIds[i]);
                if (bytes is null)
                {
                    bytes = await BakeAsync(c, bake, textures, types, ct);
                    if (bytes is null) throw new InvalidOperationException($"the {bake} bake failed");
                    if (cacheIds[i] != UUID.Zero) Cache.Put(cacheIds[i], bytes);
                    baked++;
                }
                else reused++;
                id = await UploadAsync(c, bytes, ct);
                Cache.Uploaded(id, cacheIds[i]);
            }
            _sentBakes[i] = id;
            _sentCacheIds[i] = cacheIds[i];
            te.CreateFace((uint)index).TextureID = id;
        }
        BakedHere += baked;
        ReusedHere += reused;

        var set = AppearancePacket(c, wearables.Select(x => x.A).ToList(), te, cacheIds);
        c.Network.SendPacket(set);
        c.Appearance.MyTextures = te;
        _selfLook = (te, set.VisualParam.Select(b => b.ParamValue).ToArray());
        ShowSelf(c);
        SendIsNowWearing(c, wearables.Select(x => x.W).ToList());
        Status = $"client-baked: {needed.Count} bakes ({baked} composited, {reused} from the cache), {wearables.Count} wearables";
    }

    static AppearanceManager.WearableData ToLmv(WornItem w, Wearable a)
    {
        AssetWearable asset = a.IsBodyPart ? new AssetBodypart() : new AssetClothing();
        asset.WearableType = a.Type;
        foreach (var (k, v) in a.Params) asset.Params[k] = v;
        // LibreMetaverse's baker tints jackets and tattoos from the driven colour params; the asset holds the drivers
        foreach (var (from, to) in new[] { (834, 809), (835, 810), (836, 811), (1071, 1062), (1072, 1063), (1073, 1064) })
            if (a.Params.TryGetValue(from, out var dv)) asset.Params[to] = dv;
        foreach (var (k, v) in a.Textures) asset.Textures[(AvatarTextureIndex)k] = v;
        return new AppearanceManager.WearableData
        {
            ItemID = w.ItemId, AssetID = w.AssetId, WearableType = w.Type, AssetType = w.AssetType, Asset = asset,
        };
    }

    /// <summary>The protocol's bake cache id: XOR of the asset ids of the worn wearables that feed this bake, then the
    /// bake's hash (zero when nothing feeds it). Universal wearables feed every bake, as in the viewer.</summary>
    public static UUID CacheId(int bakedIndex, IEnumerable<WornItem> worn)
    {
        var map = AppearanceManager.WEARABLE_BAKE_MAP[bakedIndex];
        var hash = UUID.Zero;
        foreach (var w in worn)
            if (map.Contains(w.Type) || w.Type == WearableType.Universal) hash ^= w.AssetId;
        return hash == UUID.Zero ? hash : hash ^ AppearanceManager.BAKED_TEXTURE_HASH[bakedIndex];
    }

    async Task<byte[]?> BakeAsync(GridClient c, BakeType bake, AppearanceManager.TextureData[] textures, HashSet<WearableType> types, CancellationToken ct)
    {
        await BakeCache.Oven.WaitAsync(ct);
        var indices = AppearanceManager.BakeTypeToTextures(bake)
            .Where(i => i != AvatarTextureIndex.Skirt || types.Contains(WearableType.Skirt)).ToList();
        var plain = _s.Host.PlainBodies;
        if (plain) PlainBodies.Install();
        try
        {
            foreach (var index in indices)
            {
                var td = textures[(int)index];
                if (plain && (td.TextureID == UUID.Zero || PlainBodies.IsStockDefault(td.TextureID)))
                {
                    // nothing of the resident's own: no built-in hair grain or iris either (PlainBodies)
                    td.Texture = null;
                    td.TextureID = UUID.Zero;
                    continue;
                }
                if (td.TextureID == UUID.Zero || td.Texture is not null) continue;
                var tex = await _s.World.TextureAsync(_s.GridKey, td.TextureID, ct);
                if (tex?.AssetData is not { Length: > 0 } data) { _s.Log.LogWarning("bake {Bake}: texture {Id} unavailable", bake, td.TextureID); continue; }
                var copy = new AssetTexture(td.TextureID, data);   // the hub may hand the same object to other sessions
                if (copy.Decode()) td.Texture = copy;
            }
            var oven = new Baker(bake);
            foreach (var index in indices)
            {
                textures[(int)index].TextureIndex = index;
                oven.AddTexture(textures[(int)index]);
            }
            await Task.Run(() => oven.Bake(), ct);
            if (oven.BakedTexture?.Image is not { } img) return null;
            // head, upper and lower carry the morph mask as a fifth component (BakeEncoder)
            return BakeEncoder.Encode(img, withMask: bake is BakeType.Head or BakeType.UpperBody or BakeType.LowerBody);
        }
        finally
        {
            foreach (var index in indices) textures[(int)index].Texture = null;
            BakeCache.Oven.Release();
        }
    }

    static async Task<UUID> UploadAsync(GridClient c, byte[] bytes, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var id = await c.Assets.RequestUploadBakedTextureAsync(bytes, cts.Token);
                if (id != UUID.Zero) return id;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(1000 * (attempt + 1), ct);
        }
        throw new InvalidOperationException("the region didn't take the bake upload (UploadBakedTexture)");
    }

    /// <summary>AgentSetAppearance as the client-baking viewers sent it: transmitted params, bakes only in the
    /// texture entry, the bake cache ids, and the size from the shape.</summary>
    AgentSetAppearancePacket AppearancePacket(GridClient c, List<Wearable> wearables, Primitive.TextureEntry te, UUID[] cacheIds)
    {
        var set = new AgentSetAppearancePacket();
        set.AgentData.AgentID = c.Self.AgentID;
        set.AgentData.SessionID = c.Self.SessionID;
        set.AgentData.SerialNum = ++_serial;
        var ids = VisualParams.Group0ParamIds;
        set.VisualParam = new AgentSetAppearancePacket.VisualParamBlock[ids.Length];
        var values = new Dictionary<int, float>();
        for (var k = 0; k < ids.Length; k++)
        {
            var vp = VisualParams.Params[ids[k]];
            var v = vp.DefaultValue;
            foreach (var w in wearables)
                if (w.Params.TryGetValue(vp.ParamID, out var x)) { v = x; break; }
            values[vp.ParamID] = v;
            set.VisualParam[k] = new AgentSetAppearancePacket.VisualParamBlock { ParamValue = Utils.FloatToByte(v, vp.MinValue, vp.MaxValue) };
        }
        c.Appearance.MyVisualParameters = set.VisualParam.Select(b => b.ParamValue).ToArray();
        set.ObjectData.TextureEntry = te.GetBytes();
        set.WearableData = new AgentSetAppearancePacket.WearableDataBlock[AppearanceManager.BAKED_TEXTURE_COUNT];
        for (var i = 0; i < set.WearableData.Length; i++)
            set.WearableData[i] = new AgentSetAppearancePacket.WearableDataBlock
            {
                TextureIndex = AppearanceManager.BakeIndexToTextureIndex[i], CacheID = cacheIds[i],
            };
        float P(int id, float d) => values.TryGetValue(id, out var v) ? v : d;
        var height = 1.706 + P(692, 0) * .1918 + P(842, 0) * .0375 + P(33, 0) * .12022 + P(682, .5f) * .01117
                     + P(756, 0) * .038 + P(198, 0) * .08 + P(503, 0) * .07;
        set.AgentData.Size = new Vector3(0.45f, 0.6f, (float)height);
        return set;
    }

    /// <summary>Tells the region what's worn (it stores it with the avatar for the next login).</summary>
    static void SendIsNowWearing(GridClient c, List<WornItem> worn)
    {
        var blocks = new List<AgentIsNowWearingPacket.WearableDataBlock>();
        foreach (var w in worn.Where(w => w.IsWearable && w.Type != WearableType.Invalid))
            blocks.Add(new AgentIsNowWearingPacket.WearableDataBlock { WearableType = (byte)w.Type, ItemID = w.ItemId });
        for (var t = 0; t <= (int)WearableType.Universal; t++)
            if (!blocks.Any(b => b.WearableType == t))
                blocks.Add(new AgentIsNowWearingPacket.WearableDataBlock { WearableType = (byte)t, ItemID = UUID.Zero });
        var p = new AgentIsNowWearingPacket { WearableData = blocks.ToArray() };
        p.AgentData.AgentID = c.Self.AgentID;
        p.AgentData.SessionID = c.Self.SessionID;
        c.Network.SendPacket(p);
    }

    // ---------- server baking (Second Life) ----------

    async Task SendServerBakedAsync(GridClient c, CancellationToken ct)
    {
        var cap = c.Network.CurrentSim?.Caps?.CapabilityURI("UpdateAvatarAppearance");
        if (cap is null) { Status = "server baking, but the region has no UpdateAvatarAppearance cap"; return; }
        await ReadWornAsync(c, ct);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var version = await CofVersionAsync(c, ct);
            if (version < 0) { Status = "server baking: couldn't read the outfit folder's version"; return; }
            if (_lastCofReceived >= version) { Status = $"server-baked (outfit version {version})"; return; }
            _lastCofRequested = version;
            var (_, data) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, new OSDMap { ["cof_version"] = version }, ct);
            var res = data is { Length: > 0 } ? OSDParser.Deserialize(data) as OSDMap : null;
            if (res is not null && res["success"].AsBoolean())
            {
                _lastCofReceived = res["cof_version"].AsInteger();
                // the answer is the appearance itself (as LibreMetaverse's own request applies it): onto my avatar now
                var textures = (res["textures"] as OSDArray)?.Select(t => t.AsUUID()).ToArray();
                var vps = res.ContainsKey("visual_params") ? res["visual_params"].AsBinary() : null;
                _s.World.OwnAppearance(_s.GridKey, c.Self.AgentID, textures, vps);
                Status = $"server-baked (outfit version {_lastCofReceived})";
                return;
            }
            var expected = res?["expected"].AsInteger() ?? 0;
            _s.Log.LogInformation("UpdateAvatarAppearance: {Error} (expected {Expected}, sent {Version})", res?["error"].AsString(), expected, version);
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1) - 1), ct);
        }
        Status = "server baking: the region didn't accept the outfit version";
    }

    async Task<int> CofVersionAsync(GridClient c, CancellationToken ct)
    {
        var cof = await CofAsync(c, ct);
        await c.Inventory.FolderContentsAsync(cof, c.Self.AgentID, true, true, InventorySortOrder.ByDate, ct);
        return c.Inventory.Store?.TryGetValue<InventoryFolder>(cof, out var f) == true ? f.Version : -1;
    }

    DateTime _regionAt = DateTime.UtcNow;

    /// <summary>Second Life doesn't keep attachments across logins: the viewer rezzes the outfit's attachment links.</summary>
    async Task RezOutfitAttachmentsAsync(GridClient c, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        var worn = await ReadWornAsync(c, ct);
        // what's attached is only known from the avatar as the current region has it: without it (between regions,
        // or not arrived yet) every worn object would look missing and be attached again. A region just arrived in
        // (a teleport home straight after logging in, while a big inventory still loads) has the avatar before its
        // attachments, so it's judged only once the region has been current a while: an object attached again while
        // it's still worn drops off and comes back.
        World.RegionWorld? w = null;
        Avatar? me = null;
        for (int i = 0; i < 300 && (me is null || DateTime.UtcNow - _regionAt < TimeSpan.FromSeconds(10)); i++)
        {
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(1), ct);
            w = _s.Region;
            me = w?.AvatarById(c.Self.AgentID);
        }
        if (w is null || me is null || DateTime.UtcNow - _regionAt < TimeSpan.FromSeconds(10)) return;
        var attached = w.Prims.Values.Where(p => p.ParentID == me.LocalID).Select(AgentInventory.AttachItemId).ToHashSet();
        var missing = worn.Where(x => x.AssetType == AssetType.Object && !attached.Contains(x.ItemId)).Select(x => x.Item).ToList();
        if (missing.Count > 0) c.Appearance.AddAttachments(missing, removeExistingFirst: false, replace: false);
    }
}
