using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// The person's environment settings items (EEP skies, waters and day cycles), for the personal environment
/// (SojournEnvironment): found in their inventory and the Library as the viewer's My Environments floater lists them,
/// and their settings fetched (the ViewerAsset capability, else the inventory transfer). Applying them is the
/// renderer's. Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class EnvironmentOps
{
    readonly AgentSession _s;
    public EnvironmentOps(AgentSession s) => _s = s;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    /// <summary>LLSettingsType::fromInventoryFlags: an item's settings kind is the low byte of its flags.</summary>
    public static string KindOf(InventoryItem it) => (it.Flags & 0xFF) switch { 0 => "sky", 1 => "water", 2 => "daycycle", _ => "settings" };

    /// <summary>How long a listing waits for the inventory and the Library to finish loading before it answers with
    /// what has arrived (they're fetched at login, so usually it doesn't wait at all).</summary>
    public static readonly TimeSpan ListWait = TimeSpan.FromSeconds(5);

    /// <summary>Settings items in the inventory (the Library's too with <paramref name="library"/>), by kind and a part
    /// of their name; Partial when a folder hadn't arrived within <see cref="ListWait"/> (the fetch runs on).</summary>
    public async Task<(JsonArray List, bool Partial)> ListAsync(string? kind, string? name, bool library, CancellationToken ct)
    {
        var c = C;
        var roots = new List<(UUID Root, bool Library)>();
        if (c.Inventory.Store?.RootFolder is { } root) roots.Add((root.UUID, false));
        if (library && _s.InventoryOps.LibraryRoot != UUID.Zero) roots.Add((_s.InventoryOps.LibraryRoot, true));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(ListWait);
        var whole = await Task.WhenAll(roots.Select(r => LoadedAsync(r.Root, wait.Token)));
        ct.ThrowIfCancellationRequested();
        var found = new List<(InventoryItem It, string Kind, bool Library)>();
        foreach (var (r, lib) in roots)
            foreach (var it in _s.Inventory.ItemsInStore(r))
            {
                if (it.AssetType != AssetType.Settings) continue;
                var k = KindOf(it);
                if (kind != null && !k.Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;
                if (name != null && !it.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                found.Add((it, k, lib));
            }
        var list = new JsonArray();
        foreach (var (it, k, lib) in found.OrderBy(f => f.Library).ThenBy(f => f.It.Name, StringComparer.OrdinalIgnoreCase))
            list.Add(new JsonObject { ["item_id"] = it.UUID.ToString(), ["name"] = it.Name, ["kind"] = k, ["library"] = lib });
        return (list, whole.Contains(false));
    }

    async Task<bool> LoadedAsync(UUID root, CancellationToken ct)
    {
        try { return await _s.Inventory.LoadSubtreeAsync(root, ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>A settings item's settings (its asset's LLSD bytes), by item id, or by name (the closest match among
    /// the inventory's, then the Library's); or a settings asset by its id.</summary>
    public async Task<(string Name, string Kind, byte[] Data)> FetchAsync(UUID? itemId, string? name, UUID? assetId, CancellationToken ct)
    {
        var c = C;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        if (assetId is { } aid)
            return (aid.ToString(), "settings", await ViewerAssetAsync(c, aid, cts.Token) ?? throw new LeapError($"the settings asset {aid} didn't arrive"));
        InventoryItem? item = null;
        if (itemId is { } iid) item = await _s.Inventory.ItemAsync(iid, cts.Token) ?? throw new LeapError($"no item {iid}");
        else if (name != null)
        {
            var (mine, partial) = await ListAsync(null, name, library: true, cts.Token);
            var pick = mine.OfType<JsonObject>()
                .OrderBy(j => j["name"]!.GetValue<string>().Equals(name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(j => j["library"]!.GetValue<bool>() ? 1 : 0)
                .FirstOrDefault() ?? throw new LeapError(partial
                    ? $"no sky, water or day cycle called \"{name}\" yet: the inventory or the Library is still loading; ask again in a few seconds"
                    : $"no sky, water or day cycle called \"{name}\" in the inventory or the Library (mine lists them)");
            item = _s.Inventory.Node(UUID.Parse(pick["item_id"]!.GetValue<string>())) as InventoryItem;
        }
        if (item is not { AssetType: AssetType.Settings }) throw new LeapError(item == null ? "give item_id, name or asset_id" : $"{item.Name} isn't a sky, water or day cycle");
        if (c.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset") != null)
            return (item.Name, KindOf(item), await ViewerAssetAsync(c, item.AssetUUID, cts.Token) ?? throw new LeapError($"{item.Name}'s settings didn't arrive"));
        var owner = _s.InventoryOps.InLibrary(item.UUID) ? _s.InventoryOps.LibraryOwner : item.OwnerID;
        var got = await c.Assets.RequestInventoryAssetAsync(item.AssetUUID, item.UUID, UUID.Zero, owner, AssetType.Settings, true, UUID.Random(), cts.Token)
                  ?? throw new LeapError($"{item.Name}'s settings didn't arrive");
        return (item.Name, KindOf(item), got.AssetData ?? Array.Empty<byte>());
    }

    /// <summary>A settings asset from the region's ViewerAsset capability (settings_id, as the viewer asks; LibreMetaverse
    /// 3.2.0 names the type "invalid" there and its request never finishes).</summary>
    static async Task<byte[]?> ViewerAssetAsync(GridClient c, UUID id, CancellationToken ct)
    {
        var cap = c.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset") ?? throw new LeapError("this region has no ViewerAsset capability");
        var (response, data) = await c.HttpCapsClient.GetAsync(new Uri($"{cap}?settings_id={id}"), ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode && data is { Length: > 0 } ? data : null;
    }
}
