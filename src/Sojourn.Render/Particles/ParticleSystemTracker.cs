using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace Sojourn.Render.Particles;

/// <summary>
/// Each object's particle system as the simulator last sent it. LibreMetaverse 3.2.0 reads PSBlock correctly from
/// full ObjectUpdates but not from ObjectUpdateCompressed, which OpenSim (and SL) send for objects unchanged for a
/// while: it parses a legacy block with the wrong length (so it comes out empty), ignores the new-format block
/// (CompressedFlags 0x400, after the texture animation) and never clears a system that went away. This reads the
/// same packets the way the viewer does (LLViewerObject::processUpdateMessage, LLVOVolume: legacy block in the
/// middle, new block at the end). Attach it to every GridClient that feeds a region before that client logs in.
/// Thread-safe.
/// </summary>
public sealed class ParticleSystemTracker
{
    public static ParticleSystemTracker Shared { get; } = new();

    // null value: the object has no particle system
    readonly ConcurrentDictionary<(ulong Handle, uint LocalId), Primitive.ParticleSystem?> _ps = new();

    public void Attach(GridClient client)
    {
        client.Network.RegisterCallback(PacketType.ObjectUpdate, OnObjectUpdate, false);
        client.Network.RegisterCallback(PacketType.ObjectUpdateCompressed, OnObjectUpdateCompressed, false);
        client.Network.RegisterCallback(PacketType.KillObject, OnKill, false);
    }

    /// <summary>The object's particle system if this tracker has seen an update for it (null inside: none).</summary>
    public bool TryGet(ulong handle, uint localId, out Primitive.ParticleSystem? ps) => _ps.TryGetValue((handle, localId), out ps);

    void OnObjectUpdate(object? sender, PacketReceivedEventArgs e)
    {
        var p = (ObjectUpdatePacket)e.Packet;
        foreach (var b in p.ObjectData)
            Store(e.Simulator.Handle, b.ID, Decode(b.PSBlock, 0, b.PSBlock?.Length ?? 0));
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
                Store(e.Simulator.Handle, localId, FromCompressed(d));
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { }
        }
    }

    void OnKill(object? sender, PacketReceivedEventArgs e)
    {
        foreach (var b in ((KillObjectPacket)e.Packet).ObjectData) _ps.TryRemove((e.Simulator.Handle, b.ID), out _);
    }

    void Store(ulong handle, uint localId, Primitive.ParticleSystem? ps) => _ps[(handle, localId)] = ps;

    /// <summary>A region let go of (no circuit to it left): its objects' entries go, as no kills will come for them. They
    /// were kept for good, an entry for every object of every region seen (2026-10-28: 46 MB in a gcdump after a tour);
    /// coming back, the region sends every object again.</summary>
    public void ForgetRegion(ulong handle)
    {
        foreach (var key in _ps.Keys)
            if (key.Handle == handle) _ps.TryRemove(key, out _);
    }

    /// <summary>A PSBlock as LLPartSysData::unpackBlock reads it: empty, too big or a zero CRC is no system.</summary>
    public static Primitive.ParticleSystem? Decode(byte[]? data, int pos, int size)
    {
        if (data == null || size <= 0 || size > Primitive.ParticleSystem.MaxDataBlockSize || pos + size > data.Length) return null;
        var block = new byte[size];
        Buffer.BlockCopy(data, pos, block, 0, size);
        var ps = new Primitive.ParticleSystem(block, 0);
        return ps.CRC == 0 ? null : ps;
    }

    /// <summary>The particle system in an ObjectUpdateCompressed block (the walk LibreMetaverse's own parser does,
    /// with the viewer's handling of both particle formats).</summary>
    public static Primitive.ParticleSystem? FromCompressed(byte[] d)
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
        Primitive.ParticleSystem? ps = null;
        if ((flags & CompressedFlags.HasParticles) != 0)
        {
            ps = Decode(d, i, Primitive.ParticleSystem.LegacyDataBlockSize);
            i += Primitive.ParticleSystem.LegacyDataBlockSize;
        }
        if ((flags & CompressedFlags.HasParticlesNew) == 0) return ps;

        // the new-format block is the last thing in the update: walk past extra params, sound, name values,
        // shape, texture entry and texture animation
        int count = d[i++];
        for (int k = 0; k < count; k++)
        {
            uint len = Utils.BytesToUInt(d, i + 2);
            i += 6 + (int)len;
        }
        if ((flags & CompressedFlags.HasSound) != 0) i += 16 + 4 + 1 + 4;
        if ((flags & CompressedFlags.HasNameValues) != 0)
        {
            while (d[i] != 0) i++;
            i++;
        }
        i += 16 + 7;                                           // path and profile parameters
        int te = (int)Utils.BytesToUInt(d, i);
        i += 4 + te;
        if ((flags & CompressedFlags.TextureAnimation) != 0)
        {
            int ta = (int)Utils.BytesToUInt(d, i);
            i += 4 + ta;
        }
        return Decode(d, i, d.Length - i) ?? ps;
    }
}
