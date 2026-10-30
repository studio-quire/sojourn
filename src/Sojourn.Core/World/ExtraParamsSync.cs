using LibreMetaverse;
using LibreMetaverse.Packets;

namespace Sojourn.Core.World;

/// <summary>
/// LibreMetaverse fills a prim's extra parameters from each full update but never clears one the update no longer
/// carries, so a light a script turned off or a projector taken off stayed on the prim (drawn, read back, kept by
/// undo). The viewer resets each parameter the update leaves out (LLViewerObject's in-use flags). This clears the light
/// and the projector when the latest full or compressed update didn't carry them; whichever callback runs first,
/// what's present ends set and what's absent ends empty. Flexible and sculpt data are left (the renderer reads them
/// from worker threads after checking them, and both go with a path or shape change anyway). Attach before login. Claude Opus 5.5, 2026-10-29 (found by batch B6's projector check).
/// </summary>
public static class ExtraParamsSync
{
    public static void Attach(GridClient c)
    {
        c.Network.RegisterCallback(PacketType.ObjectUpdate, (_, e) =>
        {
            foreach (var b in ((ObjectUpdatePacket)e.Packet).ObjectData)
                if (b.PCode == (byte)PCode.Prim) Clear(e.Simulator, b.ID, b.ExtraParams, 0);
        }, false);
        c.Network.RegisterCallback(PacketType.ObjectUpdateCompressed, (_, e) =>
        {
            foreach (var b in ((ObjectUpdateCompressedPacket)e.Packet).ObjectData)
            {
                try
                {
                    var d = b.Data;
                    if (d.Length < 22 || d[20] != (byte)PCode.Prim) continue;
                    Clear(e.Simulator, Utils.BytesToUInt(d, 16), d, Offset(d));
                }
                catch (Exception x) when (x is IndexOutOfRangeException or ArgumentException) { }
            }
        }, false);
    }

    static void Clear(Simulator sim, uint localId, byte[] data, int pos)
    {
        if (data is null || pos >= data.Length) return;
        if (!sim.ObjectsPrimitives.TryGetValue(localId, out var p)) return;
        bool light = false, image = false;
        int count = data[pos++];
        for (int k = 0; k < count && pos + 6 <= data.Length; k++)
        {
            ushort type = Utils.BytesToUInt16(data, pos);
            uint len = Utils.BytesToUInt(data, pos + 2);
            pos += 6 + (int)len;
            switch (type)
            {
                case 0x20: light = true; break;
                case 0x40: image = true; break;
            }
        }
        if (!light) p.Light = null!;
        if (!image && p.LightMap is { LightTexture: var t } && t != UUID.Zero) p.LightMap = new Primitive.LightImage();
    }

    /// <summary>Where the extra parameters start in an ObjectUpdateCompressed block (as RenderMaterialTracker walks it).</summary>
    static int Offset(byte[] d)
    {
        int i = 16 + 4 + 1 + 1 + 4 + 1 + 1 + 12 + 12 + 12;
        var flags = (CompressedFlags)Utils.BytesToUInt(d, i);
        i += 4 + 16;
        if ((flags & CompressedFlags.HasAngularVelocity) != 0) i += 12;
        if ((flags & CompressedFlags.HasParent) != 0) i += 4;
        if ((flags & CompressedFlags.Tree) != 0) i += 1;
        else if ((flags & CompressedFlags.ScratchPad) != 0) i += 1 + d[i];
        if ((flags & CompressedFlags.HasText) != 0)
        {
            while (d[i] != 0) i++;
            i += 1 + 4;
        }
        if ((flags & CompressedFlags.MediaURL) != 0)
        {
            while (d[i] != 0) i++;
            i++;
        }
        if ((flags & CompressedFlags.HasParticles) != 0) i += 86;
        return i;
    }
}
