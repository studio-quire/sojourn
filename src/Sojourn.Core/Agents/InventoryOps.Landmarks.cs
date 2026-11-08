using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// A landmark's About, as the official viewer's landmark panel (LLPanelLandmarkInfo over LLPanelPlaceInfo): where it
/// points, then the place there, asked of the grid as the viewer asks it (the RemoteParcelRequest capability turns the
/// region and spot into the parcel's id, ParcelInfoRequest gives its name, description, owner, area, traffic and
/// rating), with the landmark's own notes (its description) and date. The reply's flags are read from the packet
/// itself, since LibreMetaverse keeps only "mature": 0x1 moderate, 0x2 adult, 0x4 owned by a group, as the viewer reads
/// them. Claude Opus 5.5, 2026-11-07.
/// </summary>
public sealed partial class InventoryOps
{
    public async Task<JsonObject> LandmarkAboutAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var item = await ItemAsync(a, ct);
        var o = await LandmarkAsync(a, ct);
        if (item.Description is { Length: > 0 } d && d != "(No Description)") o["notes"] = d;
        o["created"] = item.CreationDate.ToString("yyyy-MM-dd");
        if (!UUID.TryParse(o.Str("region_id"), out var regionId) || !ulong.TryParse(o.Str("region_handle"), out var handle) || o["pos"] is not JsonArray pa)
            return o;
        var pos = new Vector3((float)(Json.AsDouble(pa[0]) ?? 0), (float)(Json.AsDouble(pa[1]) ?? 0), (float)(Json.AsDouble(pa[2]) ?? 0));
        UUID parcelId;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(10));
            try { parcelId = await c.Parcels.RequestRemoteParcelIDAsync(pos, handle, regionId, wait.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { parcelId = UUID.Zero; }
        }
        if (parcelId == UUID.Zero) { o["parcel_unknown"] = "the grid didn't say which parcel is there"; return o; }
        var tcs = new TaskCompletionSource<ParcelInfoReplyPacket.DataBlock>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReply(object? _, PacketReceivedEventArgs e)
        {
            if (e.Packet is ParcelInfoReplyPacket p && p.Data.ParcelID == parcelId) tcs.TrySetResult(p.Data);
        }
        c.Network.RegisterCallback(PacketType.ParcelInfoReply, OnReply);
        ParcelInfoReplyPacket.DataBlock info;
        try
        {
            c.Parcels.RequestParcelInfo(parcelId);
            info = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (TimeoutException) { o["parcel_unknown"] = "the grid didn't answer about the parcel"; return o; }
        finally { c.Network.UnregisterCallback(PacketType.ParcelInfoReply, OnReply); }
        var parcel = new JsonObject
        {
            ["parcel_id"] = parcelId.ToString(),
            ["name"] = Utils.BytesToString(info.Name),
            ["rating"] = (info.Flags & 0x2) != 0 ? "Adult" : (info.Flags & 0x1) != 0 ? "Moderate" : "General",
            ["area"] = info.ActualArea,
            ["traffic"] = (int)info.Dwell,
        };
        if (Utils.BytesToString(info.Desc) is { Length: > 0 } pd) parcel["desc"] = pd;
        if (info.OwnerID != UUID.Zero)
        {
            parcel["owner_id"] = info.OwnerID.ToString();
            parcel["group_owned"] = (info.Flags & 0x4) != 0;
        }
        if (info.SalePrice > 0) parcel["for_sale"] = info.SalePrice;
        if (Utils.BytesToString(info.SimName) is { Length: > 0 } sim) o["region"] = sim;
        o["parcel"] = parcel;
        return o;
    }
}
