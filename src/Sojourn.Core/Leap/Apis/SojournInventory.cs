using System.Text.Json.Nodes;
using LibreMetaverse;
using SkiaSharp;
using Sojourn.Core.Agents;
using Sojourn.Core.Rendering;

namespace Sojourn.Core.Leap;

/// <summary>Inventory, batch B7: thumbnails (seen and set), favourites, protected folders. Agents/InventoryMeta.cs,
/// InventoryOps.cs. Claude Opus 5.5, 2026-10-29.</summary>
public sealed class SojournInventory : SessionApi
{
    public override string Name => "SojournInventory";
    public override string Desc => "Inventory thumbnails (an item's picture, setting one), favourites, protected folders";

    public SojournInventory(AgentSession s) : base(s)
    {
        var meta = s.InventoryMeta;
        var inv = s.InventoryOps;

        Op("meta", "{id} -> {id, thumbnail_id?, favorite}: an item's or folder's thumbnail and favourite flag (Second Life's inventory service)",
            async (a, ct) =>
            {
                var id = Id(a);
                var (thumb, fav) = await meta.OfAsync(id, ct, fresh: a.Bool("fresh", false));
                return new JsonObject { ["id"] = id.ToString(), ["thumbnail_id"] = thumb == UUID.Zero ? null : thumb.ToString(), ["favorite"] = fav };
            });
        Op("picture", "{id, size=256 (32-1024)} -> {source: thumbnail | the texture itself, texture_id, width, height, png (base64)}: what an "
            + "item or folder looks like, as the viewer's gallery shows it (its thumbnail; a texture or snapshot's own image without one); "
            + "an error when it has none", async (a, ct) =>
            {
                var id = Id(a);
                var pic = await meta.PictureOfAsync(id, ct) ?? throw new LeapError("it has no thumbnail (and isn't a texture)");
                using var bmp = await meta.TextureBitmapAsync(pic.Texture, Math.Clamp(a.Int("size", 256), 32, 1024), ct);
                return new JsonObject
                {
                    ["source"] = pic.Source, ["texture_id"] = pic.Texture.ToString(), ["width"] = bmp.Width, ["height"] = bmp.Height,
                    ["png"] = Convert.ToBase64String(InventoryMeta.Png(bmp)),
                };
            });
        Op("thumbnail", "{id, texture_id? | item_id? (a texture of mine with copy and transfer) | path? (an image file) | view?: true (a picture of "
            + "the view now) | remove?: true} -> {id, thumbnail_id, size?, cropped?}: set an item's or folder's thumbnail as the viewer's Change "
            + "Thumbnail does. A square texture of 64-256 px is used as it is; anything else is cropped to its middle square, scaled to at most "
            + "256 and uploaded", (a, ct) => ThumbnailAsync(a, ct));
        Op("favorites", "{refresh?} -> {favorites: [{id, name, kind, path, ...}], count}: everything marked favourite (the viewer's Favorites "
            + "tab; the first call reads the whole inventory from the service)", (a, ct) => J(meta.FavoritesAsync(a.Bool("refresh", false), ct)));
        Op("favorite", "{id, on=true} -> {id, favorite}: mark (or unmark) an item or folder as a favourite", (a, ct) =>
            J(meta.SetFavoriteAsync(Id(a), a.Bool("on", true), ct)));
        Op("protected", "-> {protected: [{id, name}]}: folders the person protected (Firestorm's): they can't be deleted, moved or renamed "
            + "until unprotected (system folders always are)", _ => inv.ProtectedJson());
        Op("protect", "{folder_id} -> {protected}: protect a folder from deleting, moving and renaming", a => inv.Protect(a, true));
        Op("unprotect", "{folder_id} -> {protected}", a => inv.Protect(a, false));
    }

    static UUID Id(JsonObject a) => a.Uuid("id") ?? a.Uuid("item_id") ?? a.Uuid("folder_id") ?? throw new LeapError("need id");

    static async Task<JsonNode?> J(Task<JsonObject> t) => await t;

    async Task<JsonNode?> ThumbnailAsync(JsonObject a, CancellationToken ct)
    {
        var meta = S.InventoryMeta;
        var id = a.Uuid("id") ?? throw new LeapError("need id (the item or folder to give a thumbnail)");
        if (S.Inventory.Node(id) is null && await S.Inventory.ItemAsync(id, ct) is null) throw new LeapError($"no item or folder {id} in my inventory");
        if (a.Bool("remove", false)) return await meta.SetThumbnailAsync(id, UUID.Zero, ct);

        UUID? texture = a.Uuid("texture_id");
        string from = "";
        if (a.Uuid("item_id") is { } tid)
        {
            var it = await S.Inventory.ItemAsync(tid, ct) ?? throw new LeapError($"no inventory item {tid}");
            if (it.AssetType == AssetType.Link && await S.Inventory.ItemAsync(it.AssetUUID, ct) is { } t) it = t;
            if (it.AssetType != AssetType.Texture) throw new LeapError($"{it.Name} isn't a texture");
            var p = it.Permissions.OwnerMask;
            if ((p & PermissionMask.Copy) == 0 || (p & PermissionMask.Transfer) == 0) throw new LeapError($"{it.Name}: a thumbnail's texture needs copy and transfer rights (as the viewer asks)");
            texture = it.AssetUUID;
            from = it.Name;
        }
        if (texture is { } tex)
        {
            using var bmp = await meta.TextureBitmapAsync(tex, 4096, ct);
            if (bmp.Width == bmp.Height && bmp.Width is >= InventoryMeta.ThumbMin and <= InventoryMeta.ThumbMax)
            {
                var o = await meta.SetThumbnailAsync(id, tex, ct);
                if (from.Length > 0) o["from"] = from;
                return o;
            }
            return await meta.UploadThumbnailAsync(id, bmp, ct, from.Length > 0 ? from : tex.ToString());
        }
        if (a.Str("path") is { Length: > 0 } path)
        {
            if (!File.Exists(path)) throw new LeapError($"no file {path}");
            using var bmp = SKBitmap.Decode(path) ?? throw new LeapError($"can't read image {path}");
            return await meta.UploadThumbnailAsync(id, bmp, ct, Path.GetFileName(path));
        }
        if (a.Bool("view", false))
        {
            var provider = Snapshots.Provider ?? throw new LeapError(Snapshots.NotAvailable);
            var w = S.Region ?? throw new LeapError("not in a region yet");
            var file = Path.Combine(Path.GetTempPath(), $"sojourn-thumb-{Guid.NewGuid():N}.png");
            try
            {
                var r = await provider.RenderAsync(new SnapshotRequest
                {
                    Agent = S, World = w, CameraPos = default, Focus = default, FileName = file, Width = 1024, Height = 1024, Tool = "thumbnail",
                }, ct);
                if (!r.Ok) throw new LeapError(r.Error ?? "no picture");
                using var bmp = SKBitmap.Decode(file) ?? throw new LeapError("the view's picture couldn't be read");
                return await meta.UploadThumbnailAsync(id, bmp, ct, "the view");
            }
            finally { try { File.Delete(file); } catch { } }
        }
        throw new LeapError("need texture_id, item_id, path, view: true or remove: true");
    }
}
