using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Leap;

/// <summary>The stock viewer's LLAppearance API (llappearancelistener.cpp): outfits and wearing by item id.</summary>
public sealed class LLAppearance : SessionApi
{
    public override string Name => "LLAppearance";
    public override string Desc => "API to wear a specified outfit and wear/remove individual items";

    public LLAppearance(AgentSession s) : base(s)
    {
        Op("wearOutfit", "{folder_id | folder_name, append=false}", async (a, ct) =>
        {
            if (!a.Has("folder_id") && !a.Has("folder_name")) return Json.Error("Either [folder_id] or [folder_name] is required");
            var folder = a.Uuid("folder_id") ?? await FindOutfitAsync(a.Str("folder_name")!, ct);
            if (folder is null) return Json.Error("Failed to wear outfit");
            await S.Wardrobe.WearOutfitAsync(C, folder.Value, a.Bool("append", false), ct);
            return new JsonObject();
        });
        Op("wearItems", "{items_id, replace}", async (a, ct) =>
        {
            var items = new List<InventoryItem>();
            foreach (var id in Ids(a))
                items.Add(await S.Inventory.ItemAsync(id, ct) ?? throw new LeapError($"no item {id} in my inventory (nothing was put on)"));
            if (items.Count == 0) throw new LeapError("need items_id");
            await S.Wardrobe.WearAsync(C, items, a.Bool("replace", false), ct);
            return new JsonObject();
        });
        Op("detachItems", "{items_id}", async (a, ct) =>
        {
            await S.Wardrobe.TakeOffAsync(C, Ids(a), ct);
            return new JsonObject();
        });
        Op("getOutfitsList", "-> {outfits: {id: name}}", async (_, ct) => await S.Wardrobe.OutfitsAsync(ct));
        Op("getOutfitItems", "{outfit_id} -> {items: {id: {name, wearable_type, is_worn}}}", async (a, ct) =>
        {
            try { return await S.Wardrobe.OutfitItemsAsync(a.Uuid("outfit_id") ?? UUID.Zero, ct); }
            catch (LeapError e) { return Json.Error(e.Message); }
        });
    }

    static List<UUID> Ids(JsonObject a) =>
        a["items_id"] is JsonArray ? a.UuidList("items_id") : a.Uuid("items_id") is { } one ? new List<UUID> { one } : new List<UUID>();

    async Task<UUID?> FindOutfitAsync(string name, CancellationToken ct)
    {
        var outfits = await S.Wardrobe.OutfitsAsync(ct);
        foreach (var (id, n) in (JsonObject)outfits["outfits"]!)
            if (string.Equals(n?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)) return UUID.Parse(id);
        return null;
    }
}
