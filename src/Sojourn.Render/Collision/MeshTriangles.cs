using System.Buffers;
using System.IO.Compression;
using LibreMetaverse;
using LibreMetaverse.Rendering;
using LibreMetaverse.StructuredData;
using LVector2 = LibreMetaverse.Vector2;
using LVector3 = LibreMetaverse.Vector3;

namespace Sojourn.Render.Collision;

/// <summary>
/// A mesh asset's drawn triangles at its highest LOD for the collision model, the same as LibreMetaverse's decode
/// (MeshFoundry.GenerateFacetedMeshMesh at DetailLevel.Highest, then each face's triangles in order) gives them. That
/// decode inflates every part of the asset (four LODs, the physics, the skin) and builds each vertex with its normal and
/// UVs in growing lists; the collision model did it for every mesh without a hull of its own that it passed, three
/// quarters of what an arrival allocated on the perf grid (byte, vertex and Vector3 arrays, the types a tester's rides
/// allocate most), much of it on the large object heap. Here only the high LOD's block is inflated (into a pooled
/// buffer) and only positions are read, kept as corners and indices (<see cref="DrawnTriangles"/>). Its checks are the
/// decode's own (faces, sizes, finite numbers, indices in range, mirrored and inverted sculpt flags); anything it can't
/// be sure of comes back null and the caller uses LibreMetaverse's decode, which then gives the answer exactly: no high
/// LOD (its decode falls back to another), a skin (rigged meshes check their joints first), anything malformed in the
/// block. What can differ: an asset LibreMetaverse's decode refuses whole for its other parts (corrupt, or past its
/// limits across all parts: 64 parts, 64 MB inflated, the decoded values' budget shared between them) while the high LOD
/// is sound and within the limits one part has; this reads it. No real mesh comes near the limits. `tools/decodecheck` compares the two. Claude Opus 5.5, 2026-11-14.
/// </summary>
public static class MeshTriangles
{
    /// <summary>What the decode gives (null: no triangles).</summary>
    public sealed record Decided(DrawnTriangles? Triangles);

    /// <summary>The triangles in unit-scale object space (<see cref="Decided.Triangles"/> null: none); null: not decided
    /// here.</summary>
    public static Decided? Highest(Primitive prim, byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            if (OSDParser.DeserializeLLSDBinary(ms) is not OSDMap header) return null;
            long start = ms.Position;
            if (header.ContainsKey("skin")) return null;
            if (!header.TryGetValue("high_lod", out var lodOsd) || lodOsd is not OSDMap part) return null;
            if (!part.TryGetValue("offset", out var off) || !part.TryGetValue("size", out var sz)
                || off.Type != OSDType.Integer || sz.Type != OSDType.Integer) return null;
            long offset = off.AsInteger(), size = sz.AsInteger();
            if (offset < 0 || size <= 0 || start + offset + size > data.Length) return null;
            if (Inflate(data, (int)(start + offset), (int)size) is not OSDArray faces) return null;
            return Triangles(prim, faces);
        }
        catch (Exception) { return null; }
    }

    static Decided? Triangles(Primitive prim, OSDArray faces)
    {
        if (faces.Count > FacetedMesh.MaxFaces) return null;
        // first the faces' checks and sizes (any failure: LibreMetaverse's decode, which fails the same way or falls back)
        var blocks = new List<(byte[] Pos, byte[] Tris, LVector3 Min, LVector3 Max)>(faces.Count);
        for (int i = 0; i < faces.Count; i++)
        {
            if (faces[i] is not OSDMap f) continue;
            if (f.TryGetValue("NoGeometry", out var ng))
            {
                if (ng is not OSDBoolean b) return null;
                if (b.AsBoolean()) continue;
            }
            LVector3 max = new(0.5f, 0.5f, 0.5f), min = new(-0.5f, -0.5f, -0.5f);
            if (f.TryGetValue("PositionDomain", out var pd) && pd is OSDMap dom) (max, min) = (Get(dom, "Max").AsVector3(), Get(dom, "Min").AsVector3());
            byte[] pos = Get(f, "Position").AsBinary(), tris = Get(f, "TriangleList").AsBinary();
            FacetedMesh.ValidateSubMeshBlocks(pos.Length, tris.Length);
            if (!Finite(min) || !Finite(max)) return null;
            if (f.TryGetValue("TexCoord0", out _))
            {
                if (Get(f, "TexCoord0Domain") is not OSDMap uv) return null;
                if (!Finite(Get(uv, "Max").AsVector2()) || !Finite(Get(uv, "Min").AsVector2())) return null;
            }
            if (f.TryGetValue("TexCoord1", out _) && f.TryGetValue("TexCoord1Domain", out var uv1) && uv1 is OSDMap uv1m
                && (!Finite(Get(uv1m, "Max").AsVector2()) || !Finite(Get(uv1m, "Min").AsVector2()))) return null;
            if (f.TryGetValue("NormalizedScale", out var ns) && !Finite(ns.AsVector3())) return null;
            int vertices = pos.Length / 6;
            for (int k = 0; k < tris.Length; k += 6)
                if (U16(tris, k) >= vertices || U16(tris, k + 2) >= vertices || U16(tris, k + 4) >= vertices) return null;
            blocks.Add((pos, tris, min, max));
        }
        bool mirror = prim.Sculpt is { Mirror: true }, invert = prim.Sculpt is { Invert: true };
        bool swap = mirror ^ invert;
        int corners = 0, count = 0;
        foreach (var (pos, tris, _, _) in blocks)
        {
            if (tris.Length == 0) continue;
            corners += pos.Length / 6;
            count += tris.Length / 6 * 3;
        }
        var c = new Vector3[corners];
        var idx = new DrawnTriangles.Index(corners, count);
        int cAt = 0, iAt = 0;
        foreach (var (pos, tris, min, max) in blocks)
        {
            // every vertex, as the decode checks each (a face without triangles too), though only these are kept
            int n = pos.Length / 6;
            if (tris.Length == 0)
            {
                for (int v = 0; v < n; v++) if (!Corner(pos, v, min, max, mirror, out _)) return null;
                continue;
            }
            for (int v = 0; v < n; v++) if (!Corner(pos, v, min, max, mirror, out c[cAt + v])) return null;
            for (int k = 0; k < tris.Length; k += 6)
            {
                int a = U16(tris, k), b = U16(tris, k + 2), cc = U16(tris, k + 4);
                if (swap) (b, cc) = (cc, b);
                idx.Set(iAt++, cAt + a);
                idx.Set(iAt++, cAt + b);
                idx.Set(iAt++, cAt + cc);
            }
            cAt += n;
        }
        return new Decided(DrawnTriangles.Of(c, idx, count));
    }

    /// <summary>A vertex's position as LibreMetaverse quantises it (Utils.UInt16ToFloat over the face's domain), X negated
    /// when mirrored; false if it isn't finite (the decode refuses the mesh).</summary>
    static bool Corner(byte[] pos, int v, LVector3 min, LVector3 max, bool mirror, out Vector3 p)
    {
        int j = v * 6;
        float x = Utils.UInt16ToFloat(U16(pos, j), min.X, max.X), y = Utils.UInt16ToFloat(U16(pos, j + 2), min.Y, max.Y),
              z = Utils.UInt16ToFloat(U16(pos, j + 4), min.Z, max.Z);
        p = new Vector3(mirror ? -x : x, y, z);
        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
    }

    static ushort U16(byte[] b, int at) => (ushort)(b[at] | b[at + 1] << 8);
    static OSD Get(OSDMap m, string key) => m.TryGetValue(key, out var v) ? v : new OSD();
    static bool Finite(LVector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static bool Finite(LVector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

    /// <summary>A part's zlib data inflated and read as binary LLSD, as LibreMetaverse's Helpers.DecompressOSD does it
    /// (the two-byte header skipped, at most AssetMesh.MaxInflatedPartBytes), through a pooled buffer rather than a
    /// growing stream.</summary>
    static OSD Inflate(byte[] data, int at, int length)
    {
        if (length < 2) throw new InvalidDataException("no zlib header");
        int most = LibreMetaverse.Assets.AssetMesh.MaxInflatedPartBytes + 1;
        var buf = Pool.Rent(Math.Min(Math.Max(64 << 10, length * 6), most));
        try
        {
            int len = 0;
            using (var input = new MemoryStream(data, at + 2, length - 2, writable: false))
            using (var z = new DeflateStream(input, CompressionMode.Decompress))
            {
                int n;
                while ((n = z.Read(buf, len, buf.Length - len)) > 0)
                {
                    len += n;
                    if (len > LibreMetaverse.Assets.AssetMesh.MaxInflatedPartBytes) throw new InvalidDataException("inflates too far");
                    if (len == buf.Length)
                    {
                        var more = Pool.Rent(Math.Min(buf.Length * 2, most));
                        Buffer.BlockCopy(buf, 0, more, 0, len);
                        Pool.Return(buf);
                        buf = more;
                    }
                }
            }
            // (LibreMetaverse's for the whole asset, which this one part can't have more of)
            int budget = LibreMetaverse.Assets.AssetMesh.MaxDecodedElements;
            using var ms = new MemoryStream(buf, 0, len, writable: false);
            return OSDParser.DeserializeLLSDBinary(ms, ref budget);
        }
        finally { Pool.Return(buf); }
    }

    // the inflate buffers: up to 4 MB kept, two of each size, all threads' together (the shared pool keeps them per
    // thread, on the large object heap, uncounted); bigger ones made and dropped
    static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(4 << 20, 2);
}
