using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace Sojourn.Render.World;

/// <summary>
/// The GLTF (PBR) material id of each face, from the objects' render-material extra parameter (type 0x80,
/// the viewer's LLRenderMaterialParams: U8 count, then count x (U8 te index, UUID material)). LibreMetaverse 3.2.0
/// skips that parameter, so <c>TextureEntryFace.RenderMaterialID</c> stays zero on SL and OpenSim; this reads it
/// from the same object updates. Attach it to every GridClient that feeds a region before that client logs in;
/// the renderer looks faces up here when the texture entry has no material id. Thread-safe.
/// </summary>
public sealed class RenderMaterialTracker
{
    public static RenderMaterialTracker Shared { get; } = new();

    const ushort RenderMaterialParam = 0x80;
    readonly ConcurrentDictionary<(ulong Handle, uint LocalId), UUID[]> _ids = new();

    public void Attach(GridClient client)
    {
        client.Network.RegisterCallback(PacketType.ObjectUpdate, OnObjectUpdate, false);
        client.Network.RegisterCallback(PacketType.ObjectUpdateCompressed, OnObjectUpdateCompressed, false);
        client.Network.RegisterCallback(PacketType.KillObject, OnKill, false);
    }

    /// <summary>Material ids by texture entry index (zero where none), or null if the object has none.</summary>
    public UUID[]? Get(ulong regionHandle, uint localId) => _ids.TryGetValue((regionHandle, localId), out var ids) ? ids : null;

    void OnObjectUpdate(object? sender, PacketReceivedEventArgs e)
    {
        var p = (ObjectUpdatePacket)e.Packet;
        foreach (var b in p.ObjectData)
            Store(e.Simulator.Handle, b.ID, Parse(b.ExtraParams, 0));
    }

    void OnObjectUpdateCompressed(object? sender, PacketReceivedEventArgs e)
    {
        var p = (ObjectUpdateCompressedPacket)e.Packet;
        foreach (var b in p.ObjectData)
        {
            try
            {
                var d = b.Data;
                uint localId = Utils.BytesToUInt(d, 16);
                int i = ExtraParamsOffset(d);
                Store(e.Simulator.Handle, localId, Parse(d, i));
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { }
        }
    }

    void OnKill(object? sender, PacketReceivedEventArgs e)
    {
        foreach (var b in ((KillObjectPacket)e.Packet).ObjectData) _ids.TryRemove((e.Simulator.Handle, b.ID), out _);
    }

    void Store(ulong handle, uint localId, UUID[]? ids)
    {
        if (ids == null) _ids.TryRemove((handle, localId), out _);
        else _ids[(handle, localId)] = ids;
    }

    /// <summary>Where the extra parameters start in an ObjectUpdateCompressed block (LibreMetaverse's own walk).</summary>
    static int ExtraParamsOffset(byte[] d)
    {
        int i = 16 + 4 + 1 + 1 + 4 + 1 + 1 + 12 + 12 + 12;   // id, local id, pcode, state, crc, material, click, scale, pos, rot
        var flags = (CompressedFlags)Utils.BytesToUInt(d, i);
        i += 4 + 16;                                           // flags, owner
        if ((flags & CompressedFlags.HasAngularVelocity) != 0) i += 12;
        if ((flags & CompressedFlags.HasParent) != 0) i += 4;
        if ((flags & CompressedFlags.Tree) != 0) i += 1;
        else if ((flags & CompressedFlags.ScratchPad) != 0) i += 1 + d[i];
        if ((flags & CompressedFlags.HasText) != 0)
        {
            while (d[i] != 0) i++;
            i += 1 + 4;                                        // terminator, colour
        }
        if ((flags & CompressedFlags.MediaURL) != 0)
        {
            while (d[i] != 0) i++;
            i++;
        }
        if ((flags & CompressedFlags.HasParticles) != 0) i += 86;
        return i;
    }

    /// <summary>The render-material ids from an extra-parameters blob, or null if it has none.</summary>
    public static UUID[]? Parse(byte[] data, int pos)
    {
        if (data == null || pos >= data.Length) return null;
        int count = data[pos++];
        for (int k = 0; k < count && pos + 6 <= data.Length; k++)
        {
            ushort type = Utils.BytesToUInt16(data, pos);
            uint len = Utils.BytesToUInt(data, pos + 2);
            pos += 6;
            if (type == RenderMaterialParam && len >= 1 && pos + len <= data.Length)
            {
                int n = data[pos];
                if (n == 0 || len < 1 + n * 17) return null;
                int max = 0;
                for (int j = 0; j < n; j++) max = Math.Max(max, data[pos + 1 + j * 17]);
                var ids = new UUID[max + 1];
                for (int j = 0; j < n; j++)
                {
                    int o = pos + 1 + j * 17;
                    ids[data[o]] = new UUID(data, o + 1);
                }
                return ids;
            }
            pos += (int)len;
        }
        return null;
    }
}
