using Sojourn.Render.Geometry;

namespace Sojourn.Render.Collision;

/// <summary>
/// A shape's drawn surface for the collision model: its faces' vertices (as built or decoded, a seam's twice) and three
/// indices a triangle (16-bit while there are few enough corners), in the order the flat list of three corners a
/// triangle had (Flatten, the mesh decode), so rays meet the same triangles in the same order. Kept so rather than flat: a Second Life mesh without a hull of its
/// own held 131 KB of flat triangles on average, under half of that once indexed (`tools/decodecheck` over a Second Life
/// cache, 2026-11-14), and most of the big ones sat on the large object heap. Claude Opus 5.5, 2026-11-14.
/// </summary>
public sealed class DrawnTriangles
{
    public readonly Vector3[] Corners;
    readonly ushort[]? _short;
    readonly int[]? _int;

    /// <summary>Corners in the flat list: three a triangle.</summary>
    public int Count { get; }

    DrawnTriangles(Vector3[] corners, ushort[]? s, int[]? i, int count) => (Corners, _short, _int, Count) = (corners, s, i, count);

    public Vector3 this[int i] => Corners[_short != null ? _short[i] : _int![i]];

    public long Bytes => Corners.Length * 12L + (_short != null ? _short.Length * 2L : _int!.Length * 4L);
    /// <summary>The part of it in arrays of 85 KB or more (the large object heap's).</summary>
    public long LargeBytes => (Corners.Length * 12L >= 85_000 ? Corners.Length * 12L : 0) +
                              (_short != null ? (_short.Length * 2L >= 85_000 ? _short.Length * 2L : 0) : (_int!.Length * 4L >= 85_000 ? _int.Length * 4L : 0));

    /// <summary>The flat list (three corners a triangle), made afresh.</summary>
    public Vector3[] Flat()
    {
        var o = new Vector3[Count];
        for (int i = 0; i < o.Length; i++) o[i] = this[i];
        return o;
    }

    /// <summary>The box round the corners the triangles use.</summary>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (int i = 0; i < Count; i++) { var p = this[i]; lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        return (lo, hi);
    }

    /// <summary>Indices into a given number of corners, 16-bit when they fit; filled by the caller.</summary>
    internal readonly struct Index
    {
        public readonly ushort[]? Short;
        public readonly int[]? Int;
        public Index(int corners, int count)
        {
            Short = corners <= 65536 ? new ushort[count] : null;
            Int = Short == null ? new int[count] : null;
        }
        public void Set(int i, int corner) { if (Short != null) Short[i] = (ushort)corner; else Int![i] = corner; }
    }

    internal static DrawnTriangles? Of(Vector3[] corners, Index index, int count) =>
        count == 0 ? null : new DrawnTriangles(corners, index.Short, index.Int, count);

    /// <summary>Volume faces (a prim's or sculpt's), as Flatten listed them: each face's whole triangles in order.</summary>
    public static DrawnTriangles? Of(VolumeFace[] faces)
    {
        int corners = 0, count = 0;
        foreach (var f in faces)
        {
            int n = f.Indices.Length / 3 * 3;
            if (n == 0) continue;
            corners += f.Positions.Length;
            count += n;
        }
        if (count == 0) return null;
        var c = new Vector3[corners];
        var idx = new Index(corners, count);
        int cAt = 0, iAt = 0;
        foreach (var f in faces)
        {
            int n = f.Indices.Length / 3 * 3;
            if (n == 0) continue;
            Array.Copy(f.Positions, 0, c, cAt, f.Positions.Length);
            for (int i = 0; i < n; i++)
            {
                int v = (int)f.Indices[i];
                if ((uint)v >= (uint)f.Positions.Length) throw new InvalidDataException("an index past its face's corners");
                idx.Set(iAt++, cAt + v);
            }
            cAt += f.Positions.Length;
        }
        return Of(c, idx, count);
    }

    /// <summary>A flat list (three corners a triangle) indexed: equal corners (bit for bit) shared.</summary>
    public static DrawnTriangles? FromFlat(Vector3[] flat)
    {
        if (flat.Length == 0) return null;
        var seen = new Dictionary<(int, int, int), int>();
        var corners = new List<Vector3>();
        var at = new int[flat.Length];
        for (int i = 0; i < flat.Length; i++)
        {
            var p = flat[i];
            var key = (BitConverter.SingleToInt32Bits(p.X), BitConverter.SingleToInt32Bits(p.Y), BitConverter.SingleToInt32Bits(p.Z));
            if (!seen.TryGetValue(key, out int k)) { seen[key] = k = corners.Count; corners.Add(p); }
            at[i] = k;
        }
        var idx = new Index(corners.Count, flat.Length);
        for (int i = 0; i < at.Length; i++) idx.Set(i, at[i]);
        return Of(corners.ToArray(), idx, flat.Length);
    }
}
