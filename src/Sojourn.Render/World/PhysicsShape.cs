using System.Runtime.CompilerServices;
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace Sojourn.Render.World;

/// <summary>How a prim counts for the local collision model (RegionScene.CollideRay): its physics shape type as the
/// region gave it (LibreMetaverse's PhysicsShapeType: prim, none, convex hull), not known yet, or passable altogether
/// (phantom, flexible, or the region let the avatar through it).</summary>
public enum CollideAs : byte { Prim = 0, None = 1, ConvexHull = 2, Unknown = 3, Passable = 4 }

/// <summary>
/// A mesh asset's physics, as the region collides with it (the viewer draws the same in its physics-shape view,
/// LLSpatialPartition renderPhysicsShape): the base hull (physics_convex's BoundingVerts), the decomposition's hulls
/// (its HullList and Positions, when the uploader analysed it) and the physics model (physics_mesh, a LOD block's
/// triangles). All in the mesh's unit space, as triangle lists wound counter-clockwise seen from outside. The blocks are
/// kept compressed as they came and decoded the first time a collision ray needs them.
/// </summary>
public sealed class MeshPhysics
{
    byte[]? _convex, _mesh;
    bool _decoded;
    Vector3[]? _hull, _decomposition, _triangles;

    /// <summary>The asset's physics blocks, from its header (null: none worth keeping).</summary>
    public static MeshPhysics? FromAsset(byte[] data)
    {
        using var ms = new MemoryStream(data);
        if (OSDParser.DeserializeLLSDBinary(ms) is not OSDMap header) return null;
        long start = ms.Position;
        byte[]? Block(string name)
        {
            if (header[name] is not OSDMap m) return null;
            int size = m["size"].AsInteger();
            long offset = m["offset"].AsInteger();
            if (size <= 0) return null;
            if (offset < 0 || start + offset + size > data.Length) return null;
            return data.AsSpan((int)(start + offset), size).ToArray();
        }
        var p = new MeshPhysics { _convex = Block("physics_convex"), _mesh = Block("physics_mesh") };
        return p._convex == null && p._mesh == null ? null : p;
    }

    /// <summary>The hull the region uses for a convex hull shape (and for thin meshes with no decomposition).</summary>
    public Vector3[]? Hull { get { Decode(); return _hull; } }
    /// <summary>The decomposition's hulls, together.</summary>
    public Vector3[]? Decomposition { get { Decode(); return _decomposition; } }
    /// <summary>The physics model's triangles.</summary>
    public Vector3[]? Triangles { get { Decode(); return _triangles; } }
    /// <summary>Uploaded with a physics model (decomposed or not): the uploader made it a prim shape
    /// (LLMeshUploadThread::wholeModelToLLSD), else a convex hull.</summary>
    public bool UploadedAsPrim { get { Decode(); return _decomposition != null || _triangles != null; } }

    void Decode()
    {
        if (_decoded) return;
        lock (this)
        {
            if (_decoded) return;
            try { DecodeConvex(); } catch { }
            try { DecodeMesh(); } catch { }
            _convex = _mesh = null;
            _decoded = true;
        }
    }

    void DecodeConvex()
    {
        if (_convex == null || Helpers.DecompressOSD(_convex) is not OSDMap block) return;
        var min = block.ContainsKey("Min") ? V(block["Min"].AsVector3()) : new Vector3(-0.5f);
        var max = block.ContainsKey("Max") ? V(block["Max"].AsVector3()) : new Vector3(0.5f);
        if (block.ContainsKey("BoundingVerts"))
        {
            var pts = Points(block["BoundingVerts"].AsBinary(), 0, int.MaxValue, min, max);
            var tris = ConvexHull.Triangles(pts);
            if (tris.Length > 0) _hull = tris;
        }
        if (block.ContainsKey("HullList") && block.ContainsKey("Positions"))
        {
            var list = block["HullList"].AsBinary();
            var pos = block["Positions"].AsBinary();
            var all = new List<Vector3>();
            int at = 0;
            foreach (byte n in list)
            {
                int count = n == 0 ? 256 : n;
                all.AddRange(ConvexHull.Triangles(Points(pos, at, count, min, max)));
                at += count;
            }
            if (all.Count > 0) _decomposition = all.ToArray();
        }
    }

    void DecodeMesh()
    {
        if (_mesh == null || Helpers.DecompressOSD(_mesh) is not OSDArray faces) return;
        var tris = new List<Vector3>();
        foreach (var f in faces.OfType<OSDMap>())
        {
            if (f.ContainsKey("NoGeometry") || !f.ContainsKey("Position") || !f.ContainsKey("TriangleList")) continue;
            var min = new Vector3(-0.5f);
            var max = new Vector3(0.5f);
            if (f["PositionDomain"] is OSDMap d) { min = V(d["Min"].AsVector3()); max = V(d["Max"].AsVector3()); }
            var pts = Points(f["Position"].AsBinary(), 0, int.MaxValue, min, max);
            var idx = f["TriangleList"].AsBinary();
            for (int i = 0; i + 5 < idx.Length; i += 6)
            {
                int a = idx[i] | idx[i + 1] << 8, b = idx[i + 2] | idx[i + 3] << 8, c = idx[i + 4] | idx[i + 5] << 8;
                if (a >= pts.Count || b >= pts.Count || c >= pts.Count) continue;
                tris.Add(pts[a]); tris.Add(pts[b]); tris.Add(pts[c]);
            }
        }
        if (tris.Count > 0) _triangles = tris.ToArray();
    }

    /// <summary>Points quantised in 16 bits across [min, max], from <paramref name="first"/> (a point index).</summary>
    static List<Vector3> Points(byte[] b, int first, int count, Vector3 min, Vector3 max)
    {
        var pts = new List<Vector3>();
        var span = max - min;
        for (int i = first * 6, n = 0; i + 5 < b.Length && n < count; i += 6, n++)
        {
            float x = (b[i] | b[i + 1] << 8) / 65535f, y = (b[i + 2] | b[i + 3] << 8) / 65535f, z = (b[i + 4] | b[i + 5] << 8) / 65535f;
            pts.Add(min + new Vector3(x, y, z) * span);
        }
        return pts;
    }

    static Vector3 V(LibreMetaverse.Vector3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>
/// What the region collides with, shared by both local collision models (the renderer's, RegionScene.CollideRay, and
/// the one without a GPU, Collision/CollisionWorld.cs) so they give the same answers.
/// </summary>
public static class CollisionRule
{
    /// <summary>
    /// What the region collides with for a prim, where that isn't its drawn triangles (null: they are), after the
    /// viewer's LLPhysicsShapeBuilderUtil::determinePhysicsShape and renderPhysicsShape, in the shape's unit space:
    /// - A mesh with a convex hull shape: the asset's hull (or the drawn mesh's, without one).
    /// - A mesh with a prim shape: its decomposition's hulls if it has them; else its physics model, unless it's under
    ///   0.5 m in some dimension (the region's thin-triangle rule makes that the hull); else its hull. While its type
    ///   isn't known, as uploaded: a prim shape if the asset has a physics model, a hull if not.
    /// - A prim or sculpt with a convex hull shape: the hull of its drawn shape. With a prim shape, its drawn triangles
    ///   (the region's own simplifications, boxes, spheres and cylinders, are the same surfaces).
    /// Before the mesh's data has arrived (<paramref name="physics"/> null), its drawn triangles.
    /// </summary>
    /// <param name="hullOfDrawn">the convex hull of the drawn shape, asked for only when it's the answer</param>
    public static Vector3[]? Triangles(bool mesh, MeshPhysics? physics, CollideAs how, Vector3 scale, Func<Vector3[]> hullOfDrawn)
    {
        if (mesh)
        {
            if (physics is not { } phys) return how == CollideAs.ConvexHull ? NonEmpty(hullOfDrawn()) : null;
            bool prim = how == CollideAs.Prim || how == CollideAs.Unknown && phys.UploadedAsPrim;
            if (prim)
            {
                if (phys.Decomposition is { } d) return d;
                bool thin = scale.X < 0.5f || scale.Y < 0.5f || scale.Z < 0.5f;
                if (!thin && phys.Triangles is { } t) return t;
            }
            return phys.Hull ?? NonEmpty(hullOfDrawn());
        }
        return how == CollideAs.ConvexHull ? NonEmpty(hullOfDrawn()) : null;
    }

    static Vector3[]? NonEmpty(Vector3[] tris) => tris.Length > 0 ? tris : null;
}

/// <summary>Ray tests both local collision models share.</summary>
public static class CollideMath
{
    /// <summary>A standing avatar as the collision models count it: a box around the body (half a metre square, its
    /// height), met from outside (a ray starting inside walks out); seated avatars are their seat's. The distance and
    /// the face's outward normal.</summary>
    public static bool Avatar(Avatar av, Vector3 origin, Vector3 dir, float maxT, out float t, out Vector3 normal)
    {
        t = 0;
        normal = default;
        if (av.ParentID != 0) return false;
        var p = new Vector3(av.Position.X, av.Position.Y, av.Position.Z);
        float h = av.Scale.Z is > 0.5f and < 4f ? av.Scale.Z : 1.9f;
        var half = new Vector3(0.25f, 0.25f, h / 2);
        var o = origin - p;
        if (MathF.Abs(o.X) < half.X && MathF.Abs(o.Y) < half.Y && MathF.Abs(o.Z) < half.Z) return false;
        if (!RegionScene.Slab(origin, dir, p - half, p + half, maxT, out t) || t <= 0) return false;
        var at = origin + dir * t - p;
        normal = MathF.Abs(at.X / half.X) > MathF.Abs(at.Y / half.Y) ? new Vector3(MathF.Sign(at.X), 0, 0) : new Vector3(0, MathF.Sign(at.Y), 0);
        if (MathF.Abs(at.Z) > half.Z - 1e-3f) normal = new Vector3(0, 0, MathF.Sign(at.Z));
        return true;
    }
}

/// <summary>Convex hulls of point sets (incremental; the sets here are a few hundred points): triangles wound
/// counter-clockwise seen from outside, as a flat list. Flat or degenerate sets give none.</summary>
public static class ConvexHull
{
    static readonly ConditionalWeakTable<ShapeGeometry, Vector3[]> _ofShapes = new();

    /// <summary>The hull of a drawn shape's positions (a prim or sculpt with a convex hull physics shape, or a mesh whose
    /// asset has no hull), worked out once per shape.</summary>
    public static Vector3[] Of(ShapeGeometry shape) => _ofShapes.GetValue(shape, s =>
    {
        var pts = new List<Vector3>();
        foreach (var f in s.Faces) if (f.PickPositions is { } p) pts.AddRange(p);
        return Triangles(pts);
    });

    public static Vector3[] Triangles(IReadOnlyList<Vector3> input)
    {
        // thin out duplicates (prims' seams repeat every vertex)
        var pts = input.Distinct().ToList();
        if (pts.Count < 4) return Array.Empty<Vector3>();
        // The incremental build below is O(points x faces): a big mesh without a hull of its own (a house, 100k+
        // vertices) froze the frame loop for minutes when walked into (2026-10-27). Past a few hundred points, keep
        // only each direction's farthest point: an inner hull within a few cm of the true one at collision sizes.
        if (pts.Count > MaxPoints) pts = Extremes(pts);
        var lo = pts[0]; var hi = pts[0];
        foreach (var p in pts) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        float eps = MathF.Max(1e-6f, Vector3.Distance(lo, hi) * 1e-5f);

        // a first tetrahedron from extreme points
        int i0 = 0;
        for (int i = 1; i < pts.Count; i++) if (pts[i].X < pts[i0].X) i0 = i;
        int i1 = Farthest(pts, i => Vector3.DistanceSquared(pts[i], pts[i0]));
        var axis = pts[i1] - pts[i0];
        if (axis.Length() < eps) return Array.Empty<Vector3>();
        int i2 = Farthest(pts, i => Vector3.Cross(pts[i] - pts[i0], axis).LengthSquared());
        var n0 = Vector3.Cross(pts[i1] - pts[i0], pts[i2] - pts[i0]);
        if (n0.Length() < eps * eps) return Array.Empty<Vector3>();
        n0 = Vector3.Normalize(n0);
        int i3 = Farthest(pts, i => MathF.Abs(Vector3.Dot(pts[i] - pts[i0], n0)));
        if (MathF.Abs(Vector3.Dot(pts[i3] - pts[i0], n0)) < eps) return Array.Empty<Vector3>();   // flat

        var faces = new List<(int A, int B, int C, Vector3 N, float D)>();
        void AddFace(int a, int b, int c)
        {
            var n = Vector3.Normalize(Vector3.Cross(pts[b] - pts[a], pts[c] - pts[a]));
            faces.Add((a, b, c, n, Vector3.Dot(n, pts[a])));
        }
        var inside = (pts[i0] + pts[i1] + pts[i2] + pts[i3]) / 4;
        foreach (var (a, b, c) in new[] { (i0, i1, i2), (i0, i3, i1), (i1, i3, i2), (i2, i3, i0) })
        {
            var n = Vector3.Cross(pts[b] - pts[a], pts[c] - pts[a]);
            if (Vector3.Dot(n, inside - pts[a]) > 0) AddFace(a, c, b); else AddFace(a, b, c);
        }

        var visible = new List<int>();
        var edges = new HashSet<(int, int)>();
        for (int p = 0; p < pts.Count; p++)
        {
            if (p == i0 || p == i1 || p == i2 || p == i3) continue;
            visible.Clear();
            for (int f = 0; f < faces.Count; f++)
                if (Vector3.Dot(faces[f].N, pts[p]) - faces[f].D > eps) visible.Add(f);
            if (visible.Count == 0) continue;
            edges.Clear();
            foreach (int f in visible)
            {
                var (a, b, c, _, _) = faces[f];
                edges.Add((a, b)); edges.Add((b, c)); edges.Add((c, a));
            }
            // the horizon: visible faces' edges whose other side isn't visible
            var horizon = edges.Where(e => !edges.Contains((e.Item2, e.Item1))).ToList();
            for (int k = visible.Count - 1; k >= 0; k--) faces.RemoveAt(visible[k]);
            foreach (var (a, b) in horizon) AddFace(a, b, p);
        }
        var tris = new Vector3[faces.Count * 3];
        for (int f = 0; f < faces.Count; f++)
        {
            tris[f * 3] = pts[faces[f].A];
            tris[f * 3 + 1] = pts[faces[f].B];
            tris[f * 3 + 2] = pts[faces[f].C];
        }
        return tris;
    }

    const int MaxPoints = 256;

    // 258 directions: a Fibonacci sphere plus the six axes (boxes' faces stay exact)
    static readonly Vector3[] _directions = MakeDirections(252);

    static Vector3[] MakeDirections(int n)
    {
        var d = new List<Vector3> { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            float z = 1 - (2 * i + 1f) / n, r = MathF.Sqrt(1 - z * z), a = golden * i;
            d.Add(new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z));
        }
        return d.ToArray();
    }

    static List<Vector3> Extremes(List<Vector3> pts)
    {
        var dirs = _directions;
        var best = new int[dirs.Length];
        var bestDot = new float[dirs.Length];
        Array.Fill(bestDot, float.MinValue);
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            for (int k = 0; k < dirs.Length; k++)
            {
                float s = Vector3.Dot(p, dirs[k]);
                if (s > bestDot[k]) { bestDot[k] = s; best[k] = i; }
            }
        }
        return best.Distinct().Select(i => pts[i]).ToList();
    }

    static int Farthest(List<Vector3> pts, Func<int, float> score)
    {
        int best = 0;
        float bs = float.MinValue;
        for (int i = 0; i < pts.Count; i++)
        {
            float s = score(i);
            if (s > bs) { bs = s; best = i; }
        }
        return best;
    }
}
