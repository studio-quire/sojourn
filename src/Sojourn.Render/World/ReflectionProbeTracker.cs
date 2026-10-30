using System.Collections.Concurrent;
using System.Text;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace Sojourn.Render.World;

/// <summary>
/// Manual reflection probe params per prim, read from object updates: LibreMetaverse skips ExtraParams type 0x90
/// (LLReflectionProbeParams), so this listens to ObjectUpdate and ObjectUpdateCompressed packets itself and keeps
/// what it finds per simulator. Register before the region's objects arrive (objects already known are only seen
/// again when they change).
/// </summary>
public sealed class ReflectionProbeTracker
{
    const ushort ParamsReflectionProbe = 0x90;
    readonly ConcurrentDictionary<(ulong Region, uint LocalId), ReflectionProbeParams> _probes = new();

    /// <summary>One tracker for every session in the process (the host attaches it to each new client).</summary>
    public static ReflectionProbeTracker Shared { get; } = new();

    public ReflectionProbeTracker() { }

    public ReflectionProbeTracker(GridClient client) => Attach(client);

    /// <summary>Listen to a client's object updates; attach before it logs in so no update is missed.</summary>
    public void Attach(GridClient client)
    {
        client.Network.RegisterCallback(PacketType.ObjectUpdate, OnUpdate);
        client.Network.RegisterCallback(PacketType.ObjectUpdateCompressed, OnCompressed);
        client.Network.RegisterCallback(PacketType.KillObject, OnKill);
    }

    public ReflectionProbeParams? Get(ulong region, uint localId) => _probes.TryGetValue((region, localId), out var p) ? p : null;

    public Func<uint, ReflectionProbeParams?> For(ulong region) => id => Get(region, id);

    void Set(ulong region, uint id, ReflectionProbeParams? p)
    {
        if (p is { } v) _probes[(region, id)] = v;
        else _probes.TryRemove((region, id), out _);
    }

    void OnUpdate(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdatePacket u) return;
        foreach (var b in u.ObjectData)
            Set(e.Simulator.Handle, b.ID, FromExtraParams(b.ExtraParams, 0, out _));
    }

    void OnKill(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not KillObjectPacket k) return;
        foreach (var b in k.ObjectData) _probes.TryRemove((e.Simulator.Handle, b.ID), out _);
    }

    /// <summary>ExtraParams block: U8 count, then (U16 type, U32 size, data) each.</summary>
    public static ReflectionProbeParams? FromExtraParams(byte[]? data, int pos, out int end)
    {
        end = pos;
        if (data == null || pos >= data.Length) return null;
        int n = data[pos++];
        ReflectionProbeParams? found = null;
        for (int i = 0; i < n && pos + 6 <= data.Length; i++)
        {
            ushort type = BitConverter.ToUInt16(data, pos);
            int size = BitConverter.ToInt32(data, pos + 2);
            pos += 6;
            if (size < 0 || pos + size > data.Length) return found;
            if (type == ParamsReflectionProbe) found = ReflectionProbeParams.Parse(data.AsSpan(pos, size));
            pos += size;
        }
        end = pos;
        return found;
    }

    // LLViewerObject::processUpdateMessage, OUT_FULL_COMPRESSED: walk to the extra params
    void OnCompressed(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdateCompressedPacket u) return;
        foreach (var b in u.ObjectData)
        {
            var d = b.Data;
            try
            {
                int pos = 16;                                   // FullID
                uint localId = BitConverter.ToUInt32(d, pos); pos += 4;
                pos += 1 + 1 + 4 + 1 + 1;                       // PCode, State, CRC, Material, ClickAction
                pos += 12 * 3;                                  // Scale, Position, Rotation
                uint flags = BitConverter.ToUInt32(d, pos); pos += 4;
                pos += 16;                                      // OwnerID
                if ((flags & 0x80) != 0) pos += 12;             // angular velocity
                if ((flags & 0x20) != 0) pos += 4;              // parent
                if ((flags & 0x2) != 0) pos += 1;               // tree species
                else if ((flags & 0x1) != 0)                    // scratch pad: U32 size, then sized binary
                {
                    pos += 4;
                    pos += 4 + BitConverter.ToInt32(d, pos);
                }
                if ((flags & 0x4) != 0) { pos = SkipString(d, pos); pos += 4; }   // hover text + colour
                if ((flags & 0x200) != 0) pos = SkipString(d, pos);              // media URL
                if ((flags & 0x8) != 0) pos += 86;                                // legacy particle system
                Set(e.Simulator.Handle, localId, FromExtraParams(d, pos, out _));
            }
            catch (ArgumentException) { }
            catch (IndexOutOfRangeException) { }
        }
    }

    static int SkipString(byte[] d, int pos)
    {
        int z = Array.IndexOf(d, (byte)0, pos);
        return z < 0 ? d.Length : z + 1;
    }
}
