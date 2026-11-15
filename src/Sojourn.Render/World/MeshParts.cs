using LibreMetaverse;
using LibreMetaverse.Rendering;
using LibreMetaverse.StructuredData;

namespace Sojourn.Render.World;

/// <summary>
/// A mesh decoded for drawing by LibreMetaverse (MeshFoundry.GenerateFacetedMeshMesh) from a copy of its asset that
/// holds only the parts that decode reads: the LOD asked for and the skin. The decode inflates every part the asset
/// names (four LODs, the physics, the skin) whichever LOD is wanted; on a Second Life cache that was 61% of all it
/// allocated drawing each mesh at its highest LOD (4.0 of 6.6 GB over 4,765 meshes, `tools/decodecheck`), and more at the
/// lower LODs far things are drawn at, most of it arrays on the large object heap. Where the trimmed copy doesn't decode
/// (no such LOD: the decode would fall back to another), the whole asset is decoded as before, so what's drawn is the
/// same; the one difference is an asset whose other parts are corrupt or too big, which the whole decode refuses and
/// this draws. Claude Opus 5.5, 2026-11-14.
/// </summary>
public static class MeshParts
{
    public static FacetedMesh? Decode(MeshFoundry mesher, Primitive prim, byte[] data, DetailLevel lod)
    {
        if (Trim(data, LodKey(lod)) is { } trimmed && mesher.GenerateFacetedMeshMesh(prim, trimmed, lod) is { } fm) return fm;
        return mesher.GenerateFacetedMeshMesh(prim, data, lod);
    }

    /// <summary>TryDecodeFromAsset's LOD keys.</summary>
    public static string LodKey(DetailLevel lod) => lod switch
    {
        DetailLevel.High => "medium_lod",
        DetailLevel.Medium => "low_lod",
        DetailLevel.Low => "lowest_lod",
        _ => "high_lod",
    };

    /// <summary>The asset with only <paramref name="lodKey"/> and the skin among its parts (the header's other entries as
    /// they were), or null if it hasn't that LOD or anything about it is out of the ordinary (the whole decode then).</summary>
    public static byte[]? Trim(byte[] data, string lodKey)
    {
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            if (OSDParser.DeserializeLLSDBinary(ms) is not OSDMap header) return null;
            long start = ms.Position;
            var keep = new OSDMap();
            var parts = new List<(int At, int Size)>();
            int at = 0;
            bool hasLod = false;
            foreach (var key in header.Keys)
            {
                var v = header[key];
                // (as AssetMesh.Decode reads a header: a map with integer offset and size is a part)
                if (v is not OSDMap m || !m.TryGetValue("offset", out var o) || !m.TryGetValue("size", out var sz)
                    || o.Type != OSDType.Integer || sz.Type != OSDType.Integer) { keep[key] = v; continue; }
                long offset = o.AsInteger(), size = sz.AsInteger();
                if (offset < 0 || size == 0) { keep[key] = v; continue; }
                if (key != lodKey && key != "skin") continue;
                if (size < 0 || start + offset + size > data.Length) return null;
                keep[key] = new OSDMap { ["offset"] = OSD.FromInteger(at), ["size"] = OSD.FromInteger((int)size) };
                parts.Add(((int)(start + offset), (int)size));
                at += (int)size;
                if (key == lodKey) hasLod = true;
            }
            if (!hasLod) return null;
            var head = OSDParser.SerializeLLSDBinary(keep, prependHeader: false);
            var o2 = new byte[head.Length + at];
            Buffer.BlockCopy(head, 0, o2, 0, head.Length);
            int w = head.Length;
            foreach (var (from, size) in parts) { Buffer.BlockCopy(data, from, o2, w, size); w += size; }
            return o2;
        }
        catch (Exception) { return null; }
    }
}
