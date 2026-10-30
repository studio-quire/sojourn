using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace Sojourn.Core.Agents;

/// <summary>
/// Xfer downloads (task inventory files) that tolerate out-of-order packets. LibreMetaverse's own Xfer handler
/// runs on the thread pool, so two packets of one transfer can be handled at once or out of order; it drops the
/// "out of order" one and writes chunks at fixed offsets, which on a fast local region loses or garbles data (we
/// saw task inventory listings with bytes missing at packet boundaries, and transfers that never finished).
/// Here packets are buffered by number, each is confirmed, and the file is assembled in order at the end.
/// Transfers use our own ids, so LibreMetaverse's handler ignores them.
/// </summary>
public sealed class XferFetch
{
    sealed class Download
    {
        public readonly ConcurrentDictionary<uint, byte[]> Packets = new();
        public int Last = -1;
        public readonly TaskCompletionSource<byte[]> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    readonly ConcurrentDictionary<ulong, Download> _downloads = new();
    GridClient? _client;

    public void Attach(GridClient c)
    {
        _client = c;
        c.Network.RegisterCallback(PacketType.SendXferPacket, OnPacket, false);
        c.Network.RegisterCallback(PacketType.AbortXfer, OnAbort, false);
    }

    public async Task<byte[]> FetchAsync(string filename, CancellationToken ct)
    {
        var c = _client ?? throw new InvalidOperationException("not attached");
        var id = UUID.Random().GetULong();
        var d = new Download();
        _downloads[id] = d;
        try
        {
            c.Network.SendPacket(new RequestXferPacket
            {
                XferID =
                {
                    ID = id, Filename = Utils.StringToBytes(filename), FilePath = 4, DeleteOnCompletion = true,
                    UseBigPackets = false, VFileID = UUID.Zero, VFileType = -1,
                },
            });
            using var reg = ct.Register(() => d.Done.TrySetCanceled());
            return await d.Done.Task;
        }
        finally { _downloads.TryRemove(id, out _); }
    }

    void OnPacket(object? sender, PacketReceivedEventArgs e)
    {
        var p = (SendXferPacketPacket)e.Packet;
        if (!_downloads.TryGetValue(p.XferID.ID, out var d)) return;
        var num = p.XferID.Packet & 0x0FFFFFFF;
        d.Packets[num] = p.DataPacket.Data;
        if ((p.XferID.Packet & 0x80000000) != 0) d.Last = (int)num;
        _client?.Network.SendPacket(new ConfirmXferPacketPacket { XferID = { ID = p.XferID.ID, Packet = num } }, e.Simulator);
        if (d.Last < 0) return;
        for (uint i = 0; i <= (uint)d.Last; i++) if (!d.Packets.ContainsKey(i)) return;
        // packet 0 starts with the file size (S32, little endian); the rest is the data, in order
        var first = d.Packets[0];
        var size = first.Length >= 4 ? BitConverter.ToInt32(first, 0) : 0;
        using var ms = new MemoryStream(Math.Max(0, size));
        ms.Write(first, 4, Math.Max(0, first.Length - 4));
        for (uint i = 1; i <= (uint)d.Last; i++) ms.Write(d.Packets[i]);
        var data = ms.ToArray();
        if (size > 0 && data.Length > size) Array.Resize(ref data, size);
        d.Done.TrySetResult(data);
    }

    void OnAbort(object? sender, PacketReceivedEventArgs e)
    {
        var p = (AbortXferPacket)e.Packet;
        if (_downloads.TryGetValue(p.XferID.ID, out var d)) d.Done.TrySetException(new IOException($"transfer aborted ({p.XferID.Result})"));
    }
}
