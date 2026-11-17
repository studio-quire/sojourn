using LibreMetaverse;
using LibreMetaverse.Assets;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// A notecard's, script's or landmark's bytes from the region by transfer of the item (the viewer's getInvItemAsset), from
/// her inventory (no task) or a thing's contents (its prim as the task), with what came logged. Where Second Life keeps the
/// asset's id from her it sends a zero id, and LibreMetaverse would answer from, and store into, its cache under the zero
/// id, whoever's item it was (the viewer skips its cache for a zero id): that entry is dropped before and after, one such
/// read at a time so another's can't land between. The blind tester's scripts opened blank on Second Life (2026-11-15,
/// from her inventory; 2026-11-16, from a part of her train) with no error.
/// </summary>
static class TextAssets
{
    static readonly SemaphoreSlim HiddenGate = new(1, 1);

    internal readonly record struct Read(byte[]? Data, int NulAt)
    {
        public bool Arrived => Data != null;
        /// <summary>A NUL anywhere but at the end: something else's bytes, which an edit box shows as nothing.</summary>
        public bool NotText => NulAt >= 0;
    }

    /// <param name="assetId">The item's asset id, or the one our own last save made (the region's listing can lag it).</param>
    /// <param name="where">For the log: "" for her inventory, " in Zephyr" for a thing's contents.</param>
    internal static async Task<Read> FetchAsync(AgentSession s, InventoryItem item, UUID assetId, UUID taskId, UUID owner,
        AssetType type, string kind, string where, CancellationToken ct)
    {
        var c = s.Client ?? throw new InvalidOperationException("not logged in");
        bool hidden = assetId == UUID.Zero;
        bool cached = !hidden && c.Assets.Cache.HasAsset(assetId);
        Asset? asset;
        if (hidden) await HiddenGate.WaitAsync(ct);
        try
        {
            if (hidden) ForgetZero(c);
            try { asset = await c.Assets.RequestInventoryAssetAsync(assetId, item.UUID, taskId, owner, type, true, UUID.Random(), ct); }
            finally { if (hidden) ForgetZero(c); }
        }
        finally { if (hidden) HiddenGate.Release(); }
        var data = asset?.AssetData;
        int len = data?.Length ?? 0;
        while (len > 0 && data![len - 1] == 0) len--;
        int nul = data == null ? -1 : Array.IndexOf(data, (byte)0, 0, len);
        s.Log.LogInformation("{Area}: {Kind} {Name}{Where} read: asset {Asset}, {Bytes} bytes{Nul}{Cached}",
            taskId == UUID.Zero ? "inventory" : "contents", kind, item.Name, where, hidden ? "id kept from us" : "id known",
            asset == null ? "none" : (data?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            nul >= 0 ? $", a NUL at {nul}" : "", cached ? ", from the cache" : "");
        return new Read(asset == null ? null : data ?? Array.Empty<byte>(), nul);
    }

    static void ForgetZero(GridClient c)
    {
        try { if (c.Assets.Cache.AssetFileName(UUID.Zero) is { } f) File.Delete(f); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
