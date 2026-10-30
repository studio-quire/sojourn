using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Imaging.Skia;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Inventory thumbnails and favourites, as the official viewer's 2023-2025 inventory has them: both live on the
/// inventory service (AIS3) beside the item or folder, as "thumbnail": {"asset_id"} and "favorite": {"toggled"}
/// (llinventory.cpp; the old fetch caps carry them under "metadata"). LibreMetaverse 3.2.0 parses neither, so they're
/// read here from AIS's own answers and changed by AIS PATCH, as the viewer's update_inventory_item/category do. A
/// thumbnail is a square texture 64-256 px (LLFloaterChangeItemThumbnail); larger or not square, it's cropped,
/// scaled and uploaded through InventoryThumbnailUpload as the viewer does. Second Life only for now (OpenSim has no
/// InventoryAPIv3 here; checked 2026-10-29). Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class InventoryMeta
{
    readonly AgentSession _s;
    readonly ConcurrentDictionary<UUID, (UUID Thumb, bool Fav)> _meta = new();
    volatile bool _swept;

    public InventoryMeta(AgentSession s) => _s = s;

    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    /// <summary>J2C to pixel planes (the client sets the renderer's OpenJPEG; LibreMetaverse's own decoder without it).</summary>
    public static Func<byte[], int, (byte[][] Planes, int Width, int Height)?>? Decoder { get; set; }

    Uri Cap()
    {
        var cap = C.Network.CurrentSim?.Caps?.CapabilityURI(InventoryAISClient.INVENTORY_CAP_NAME);
        return cap ?? throw new LeapError("this grid has no inventory service for thumbnails and favourites (InventoryAPIv3: Second Life has it)");
    }

    // ---------- reading ----------

    static (UUID Thumb, bool Fav) Read(OSDMap m)
    {
        var thumb = UUID.Zero;
        bool fav = false;
        OSDMap? meta = m["metadata"] as OSDMap;
        var t = m.ContainsKey("thumbnail") ? m["thumbnail"] as OSDMap : meta?["thumbnail"] as OSDMap;
        if (t != null && t.ContainsKey("asset_id")) thumb = t["asset_id"].AsUUID();
        var f = m.ContainsKey("favorite") ? m["favorite"] as OSDMap : meta?["favorite"] as OSDMap;
        if (f != null && f.ContainsKey("toggled")) fav = f["toggled"].AsBoolean();
        return (thumb, fav);
    }

    static UUID IdOf(OSDMap m) =>
        m.ContainsKey("item_id") ? m["item_id"].AsUUID() : m.ContainsKey("category_id") ? m["category_id"].AsUUID() : m.ContainsKey("folder_id") ? m["folder_id"].AsUUID() : UUID.Zero;

    /// <summary>Everything in an AIS answer (and its _embedded categories, items and links, recursively) into the cache.</summary>
    int Absorb(OSDMap m)
    {
        int n = 0;
        var id = IdOf(m);
        if (id != UUID.Zero) { _meta[id] = Read(m); n++; }
        if (m["_embedded"] is OSDMap emb)
            foreach (var key in new[] { "categories", "items", "links" })
                if (emb[key] is OSDMap group)
                    foreach (var k in group.Keys.ToList())
                        if (group[k] is OSDMap child) n += Absorb(child);
        return n;
    }

    async Task<(int Status, OSDMap? Body)> GetAsync(string path, CancellationToken ct)
    {
        var uri = new Uri($"{Cap()}/{path}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var (resp, data) = await C.HttpCapsClient.GetAsync(uri, cts.Token);
        int status = (int)resp.StatusCode;
        if (!resp.IsSuccessStatusCode || data is not { Length: > 0 }) return (status, null);
        return (status, OSDParser.Deserialize(data) as OSDMap);
    }

    /// <summary>An item's or folder's thumbnail and favourite flag, from the service.</summary>
    public async Task<(UUID Thumb, bool Fav)> OfAsync(UUID id, CancellationToken ct, bool fresh = false)
    {
        if (!fresh && _meta.TryGetValue(id, out var known)) return known;
        bool folder = _s.Inventory.Node(id) is InventoryFolder;
        var (status, body) = await GetAsync(folder ? $"category/{id}" : $"item/{id}", ct);
        if (body is null) throw new LeapError($"the inventory service didn't answer for {id} (HTTP {status})");
        Absorb(body);
        return _meta.TryGetValue(id, out var m) ? m : (UUID.Zero, false);
    }

    /// <summary>Every favourite in the inventory: the whole tree read from the service once a session (a subtree at a
    /// time; one the service finds too big is split by its folders), then kept current by our own changes.</summary>
    public async Task<JsonObject> FavoritesAsync(bool refresh, CancellationToken ct)
    {
        var root = _s.Inventory.FolderFor(FolderType.Root);
        if (!_swept || refresh)
        {
            await SweepAsync(root, 0, ct);
            _swept = true;
        }
        var arr = new JsonArray();
        var trash = _s.Inventory.FolderFor(FolderType.Trash);
        foreach (var (id, m) in _meta)
        {
            if (!m.Fav) continue;
            var node = _s.Inventory.Node(id);
            if (node != null && Under(node, trash)) continue;
            var o = node switch
            {
                InventoryFolder f => AgentInventory.FolderJson(f),
                InventoryItem i => _s.Inventory.ItemJson(i),
                _ => new JsonObject { ["id"] = id.ToString(), ["name"] = "(not loaded yet)" },
            };
            o["kind"] = node is InventoryFolder ? "folder" : "item";
            if (node != null) o["path"] = PathOf(node);
            if (m.Thumb != UUID.Zero) o["thumbnail_id"] = m.Thumb.ToString();
            arr.Add(o);
        }
        return new JsonObject { ["favorites"] = arr, ["count"] = arr.Count };
    }

    async Task SweepAsync(UUID folder, int depth, CancellationToken ct)
    {
        var (status, body) = await GetAsync($"category/{folder}/children?depth=50", ct);
        if (body != null) { Absorb(body); return; }
        if (status != 403 || depth > 8) { _s.Log.LogDebug("favourites: {Folder} answered {Status}", folder, status); return; }
        // too big for one answer: this folder alone, then each subfolder
        var (_, one) = await GetAsync($"category/{folder}/children?depth=0", ct);
        if (one != null) Absorb(one);
        await _s.Inventory.EnsureLoadedAsync(folder, ct);
        var subs = C.Inventory.Store?.GetContents(folder).OfType<InventoryFolder>().Select(f => f.UUID).ToList() ?? new();
        var gate = new SemaphoreSlim(4);
        await Task.WhenAll(subs.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try { await SweepAsync(id, depth + 1, ct); }
            finally { gate.Release(); }
        }));
    }

    bool Under(InventoryBase n, UUID ancestor)
    {
        var store = C.Inventory.Store;
        var id = n.ParentUUID;
        for (int i = 0; i < 64 && id != UUID.Zero && store != null; i++)
        {
            if (id == ancestor) return true;
            if (!store.TryGetValue(id, out var p)) return false;
            id = p.ParentUUID;
        }
        return false;
    }

    string PathOf(InventoryBase n)
    {
        var parts = new List<string>();
        var store = C.Inventory.Store;
        var id = n.ParentUUID;
        for (int i = 0; i < 32 && id != UUID.Zero && store != null && store.TryGetValue(id, out var p); i++)
        {
            if (p.ParentUUID == UUID.Zero) break;   // the root
            parts.Add(p.Name);
            id = p.ParentUUID;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    // ---------- changing ----------

    async Task PatchAsync(UUID id, OSDMap updates, CancellationToken ct)
    {
        Cap();
        bool folder = _s.Inventory.Node(id) is InventoryFolder;
        if (!folder && _s.Inventory.Node(id) is null && await _s.Inventory.ItemAsync(id, ct) is null) throw new LeapError($"no item or folder {id} in my inventory");
        bool ok = folder ? await C.AisClient.UpdateCategoryAsync(id, updates, ct) : await C.AisClient.UpdateItemAsync(id, updates, ct);
        if (!ok) throw new LeapError("the inventory service refused the change");
    }

    public async Task<JsonObject> SetFavoriteAsync(UUID id, bool on, CancellationToken ct)
    {
        if (_s.InventoryOps.InLibrary(id)) throw new LeapError("that's in the Library; copy it first");
        await PatchAsync(id, new OSDMap { ["favorite"] = new OSDMap { ["toggled"] = OSD.FromBoolean(on) } }, ct);
        var m = _meta.TryGetValue(id, out var k) ? k : (UUID.Zero, false);
        _meta[id] = (m.Item1, on);
        return new JsonObject { ["id"] = id.ToString(), ["favorite"] = on };
    }

    /// <summary>Thumbnails as the viewer's limits want them: square, 64 to 256.</summary>
    public const int ThumbMin = 64, ThumbMax = 256;

    /// <summary>Sets (or with zero, removes) an item's or folder's thumbnail to a texture already right for it.</summary>
    public async Task<JsonObject> SetThumbnailAsync(UUID id, UUID texture, CancellationToken ct)
    {
        if (_s.InventoryOps.InLibrary(id)) throw new LeapError("that's in the Library; copy it first");
        await PatchAsync(id, new OSDMap { ["thumbnail"] = new OSDMap { ["asset_id"] = OSD.FromUUID(texture) } }, ct);
        var m = _meta.TryGetValue(id, out var k) ? k : (UUID.Zero, false);
        _meta[id] = (texture, m.Item2);
        return new JsonObject { ["id"] = id.ToString(), ["thumbnail_id"] = texture == UUID.Zero ? null : texture.ToString() };
    }

    /// <summary>A picture made into a thumbnail: cropped to its middle square, at most 256 px, J2C, uploaded (the service
    /// sets it on the item or folder itself).</summary>
    public async Task<JsonObject> UploadThumbnailAsync(UUID id, SKBitmap picture, CancellationToken ct, string? note = null)
    {
        if (_s.InventoryOps.InLibrary(id)) throw new LeapError("that's in the Library; copy it first");
        int side = Math.Min(picture.Width, picture.Height);
        if (side < ThumbMin) throw new LeapError($"the picture is {picture.Width}x{picture.Height}; a thumbnail needs at least {ThumbMin}x{ThumbMin}");
        int target = Math.Min(side, ThumbMax);
        using var square = new SKBitmap(target, target, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(square))
        {
            var src = SKRect.Create((picture.Width - side) / 2f, (picture.Height - side) / 2f, side, side);
            using var img = SKImage.FromBitmap(picture);
            canvas.DrawImage(img, src, SKRect.Create(0, 0, target, target), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        var tex = new AssetTexture(SkiaTextureCodec.ToManagedImage(square));
        tex.Encode();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        if (C.Network.CurrentSim?.Caps?.CapabilityURI("InventoryThumbnailUpload") is null)
            throw new LeapError("this grid can't take thumbnails (no InventoryThumbnailUpload: Second Life has it)");
        var asset = await C.Inventory.UploadThumbnailAsync(id, UUID.Zero, tex.AssetData, cts.Token)
                    ?? throw new LeapError("the thumbnail upload didn't complete");
        var m = _meta.TryGetValue(id, out var k) ? k : (UUID.Zero, false);
        _meta[id] = (asset, m.Item2);
        var o = new JsonObject { ["id"] = id.ToString(), ["thumbnail_id"] = asset.ToString(), ["size"] = target };
        if (picture.Width != picture.Height) o["cropped"] = $"the middle {side}x{side} of {picture.Width}x{picture.Height}";
        if (note != null) o["from"] = note;
        return o;
    }

    // ---------- pictures ----------

    /// <summary>A texture as a picture (decoded, scaled to at most <paramref name="max"/>).</summary>
    public async Task<SKBitmap> TextureBitmapAsync(UUID texture, int max, CancellationToken ct)
    {
        var tex = await _s.World.TextureAsync(_s.GridKey, texture, ct) ?? throw new LeapError($"texture {texture} didn't arrive");
        var data = tex.AssetData ?? throw new LeapError($"texture {texture} is empty");
        var bmp = Decode(data, max) ?? throw new LeapError($"texture {texture} couldn't be decoded");
        if (Math.Max(bmp.Width, bmp.Height) <= max) return bmp;
        float k = (float)max / Math.Max(bmp.Width, bmp.Height);
        var scaled = bmp.Resize(new SKImageInfo(Math.Max(1, (int)(bmp.Width * k)), Math.Max(1, (int)(bmp.Height * k)), SKColorType.Rgba8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        bmp.Dispose();
        return scaled;
    }

    static SKBitmap? Decode(byte[] data, int max)
    {
        if (Decoder is { } d)
        {
            // OpenJPEG can skip resolution levels: big textures decoded near the size wanted
            int reduce = data.Length > 400_000 && max <= 512 ? 2 : data.Length > 120_000 && max <= 512 ? 1 : 0;
            if ((d(data, reduce) ?? d(data, 0)) is { } p) return FromPlanes(p.Planes, p.Width, p.Height);
        }
        var tex = new AssetTexture(UUID.Zero, data);
        if (!tex.Decode() || tex.Image is not { } img) return null;
        var planes = new List<byte[]>();
        if (img.Red != null) { planes.Add(img.Red); planes.Add(img.Green); planes.Add(img.Blue); }
        if (img.Alpha != null) planes.Add(img.Alpha);
        return planes.Count == 0 ? null : FromPlanes(planes.ToArray(), img.Width, img.Height);
    }

    static SKBitmap? FromPlanes(byte[][] planes, int w, int h)
    {
        if (w <= 0 || h <= 0 || planes.Length == 0) return null;
        var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var px = new byte[w * h * 4];
        for (int i = 0, n = w * h; i < n; i++)
        {
            byte r = planes[0][i], g = planes.Length >= 3 ? planes[1][i] : r, b = planes.Length >= 3 ? planes[2][i] : r;
            byte a = planes.Length == 4 ? planes[3][i] : planes.Length == 2 ? planes[1][i] : (byte)255;
            px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a;
        }
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        return bmp;
    }

    public static byte[] Png(SKBitmap bmp)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>What an item or folder looks like, as the viewer's gallery shows it: its thumbnail, else for a texture
    /// or snapshot the texture itself. Null when it has no picture.</summary>
    public async Task<(UUID Texture, string Source)?> PictureOfAsync(UUID id, CancellationToken ct)
    {
        try
        {
            var (thumb, _) = await OfAsync(id, ct);
            if (thumb != UUID.Zero) return (thumb, "thumbnail");
        }
        catch (LeapError) { }   // no inventory service: textures still have their own picture
        var node = _s.Inventory.Node(id) ?? await _s.Inventory.ItemAsync(id, ct);
        if (node is InventoryItem it)
        {
            if (it.AssetType == AssetType.Link && _s.Inventory.Node(it.AssetUUID) is InventoryItem t) it = t;
            if (it.AssetType == AssetType.Texture && it.AssetUUID != UUID.Zero) return (it.AssetUUID, "the texture itself");
        }
        return null;
    }
}
