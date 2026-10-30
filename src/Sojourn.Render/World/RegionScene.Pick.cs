using LibreMetaverse;
using Sojourn.Render.Foliage;
using Sojourn.Render.Frame;
using Sojourn.Render.Geometry;
using LVector3 = LibreMetaverse.Vector3;
using LQuaternion = LibreMetaverse.Quaternion;

namespace Sojourn.Render.World;

public enum PickKind { Object, Avatar, Ground }

/// <summary>What a ray hit first: a prim (with its linkset's root, and the avatar wearing it if it's an attachment),
/// an avatar, or the ground. Point and distance in the frame of the region the ray was cast in. A prim hit on its
/// triangles carries where on its surface (<see cref="Surface"/>), as the viewer's pick does for touches.</summary>
public readonly record struct PickHit(PickKind Kind, ulong Region, UUID Id, UUID RootId, UUID WornBy, Vector3 Point, float Distance,
    PickSurface? Surface = null, uint LocalId = 0);

/// <summary>
/// Where on a prim a ray landed, as the viewer's LLPickInfo::getSurfaceInfo has it for ObjectGrab's SurfaceInfo
/// (what scripts read with llDetectedTouchFace/ST/UV/Normal/Binormal): the face (texture entry index), the surface
/// coordinates (the volume's own texture coordinates, 0-1 across a face: ST), the texture coordinates (those through
/// the face's planar mapping, repeats, offset and rotation: UV), and the normal and binormal in the region's frame.
/// </summary>
public readonly record struct PickSurface(int Face, Vector2 ST, Vector2 UV, Vector3 Normal, Vector3 Binormal);

public sealed partial class RegionScene
{
    TerrainHeights? _pickHeights;
    long _pickHeightsAt;

    /// <summary>
    /// The nearest prim or avatar along a ray (this region's frame; dir normalised) closer than <paramref name="best"/>.
    /// Prims are hit on their drawn triangles (the LOD drawn last; a box until their geometry is in), Linden trees and
    /// grass as the viewer hits them (a tetrahedron about the trunk, the blades), avatars on a box
    /// around the body. HUD attachments and the avatar <paramref name="skipAvatar"/> or <paramref name="skipAgent"/> (and
    /// what it wears) don't count. Render thread, between frames.
    /// </summary>
    public PickHit? Raycast(Vector3 origin, Vector3 dir, ref float best, uint skipAvatar, bool ground, UUID skipAgent = default)
    {
        // the agent id names the same avatar in any region, whatever its local id there now
        if (skipAgent != UUID.Zero)
        {
            uint here = 0;
            foreach (var av in Source.Avatars.Values)
                if (av.ID == skipAgent) { here = av.LocalID; break; }
            skipAvatar = here;
        }
        PickHit? hit = HitPrims(origin, dir, ref best, skipAvatar, null);

        foreach (var av in Source.Avatars.Values)
        {
            if (av.LocalID == skipAvatar || (skipAgent != UUID.Zero && av.ID == skipAgent)) continue;
            var p = new Vector3(av.Position.X, av.Position.Y, av.Position.Z);
            if (av.ParentID != 0)
            {
                if (!_objects.TryGetValue(av.ParentID, out var seat) || !WorldTransform(seat.Prim, out var sp, out var sr)) continue;
                p = sp + Vector3.Transform(p, sr);
            }
            float h = av.Scale.Z is > 0.5f and < 4f ? av.Scale.Z : 1.9f;
            var half = new Vector3(0.32f, 0.32f, h / 2);
            if (Slab(origin, dir, p - half, p + half, best, out float t) && t > 0)
            {
                best = t;
                hit = new PickHit(PickKind.Avatar, Source.Handle, av.ID, av.ID, UUID.Zero, origin + dir * t, t, LocalId: av.LocalID);
            }
        }

        if (ground && Ground(origin, dir, best) is { } g)
        {
            best = g;
            hit = new PickHit(PickKind.Ground, Source.Handle, UUID.Zero, UUID.Zero, UUID.Zero, origin + dir * g, g);
        }
        return hit;
    }

    /// <summary>The nearest of the prims <paramref name="only"/> accepts along a ray (nothing else blocks it): where a
    /// touch on a known object lands. Render thread, between frames.</summary>
    public PickHit? RaycastPrims(Vector3 origin, Vector3 dir, ref float best, Func<Primitive, bool> only) =>
        HitPrims(origin, dir, ref best, 0, only);

    /// <summary>Where a prim is drawn this frame (attachments on their wearer's pose), in this region's frame.</summary>
    public Vector3? DrawnPosition(uint localId) =>
        _objects.TryGetValue(localId, out var obj) && Transform(obj, out var pos, out _) ? pos : null;

    readonly List<SceneObject> _pickFound = new();

    /// <param name="hud">the person's HUDs (RegionScene.Hud.cs): candidates given, placed by <paramref name="hudFrame"/></param>
    PickHit? HitPrims(Vector3 origin, Vector3 dir, ref float best, uint skipAvatar, Func<Primitive, bool>? only,
        IEnumerable<SceneObject>? hud = null, Func<uint, int, (Vector3 Pos, Quaternion Rot)?>? hudFrame = null)
    {
        PickHit? hit = null;
        (FaceGeometry Face, int Tri, float U, float V, Primitive Prim, Vector3 Scale, Quaternion Rot)? onFace = null;
        foreach (var obj in hud ?? RayCandidates(origin, dir, best, _pickFound))
        {
            if (obj.Skip) continue;
            var prim = obj.Prim;
            if (only != null && !only(prim)) continue;
            uint wearer = obj.Owner;
            if (hud == null && wearer != 0 && (wearer == skipAvatar || IsHud(prim))) continue;
            if (hud == null && wearer != 0 && (obj.Faces?.Shape ?? obj.Lods.FirstOrDefault(l => l is { Ready: true, Evicted: false })) is { Skin: not null } rigged)
            {
                // skinned on the GPU: its box as the wearer's pose carries it (no triangles kept), or nothing
                if (_world.Avatars.RiggedBounds(Source.Handle, wearer, rigged) is not { } rb || !Slab(origin, dir, rb.Min, rb.Max, best, out float tr) || tr <= 0) continue;
                best = tr;
                hit = new PickHit(PickKind.Object, Source.Handle, prim.ID, RootOf(prim)?.ID ?? prim.ID,
                    Source.Avatars.TryGetValue(wearer, out var rav) ? rav.ID : UUID.Zero, origin + dir * tr, tr, LocalId: prim.LocalID);
                onFace = null;
                continue;
            }
            if (!Transform(obj, out var pos, out var rot, hudFrame)) continue;
            var scale = new Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z);
            if (scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) continue;
            if (hud == null && obj.Kind is ShapeKind.Tree or ShapeKind.Grass)
            {
                // Linden plants as the viewer picks them, not by the object's box (a tree's spans metres of open air
                // around the trunk, and took the pointer from whatever stood behind or beside it)
                if ((obj.Kind == ShapeKind.Tree ? TreeHit(prim, pos, rot, scale, origin, dir, best) : GrassHit(obj, origin, dir, best)) is not { } tp)
                    continue;
                best = tp;
                hit = new PickHit(PickKind.Object, Source.Handle, prim.ID, RootOf(prim)?.ID ?? prim.ID, UUID.Zero, origin + dir * tp, tp, LocalId: prim.LocalID);
                onFace = null;
                continue;
            }

            // into the shape's unit space; t stays in world metres since the direction isn't renormalised
            var inv = Quaternion.Conjugate(rot);
            var lo = Vector3.Transform(origin - pos, inv) / scale;
            var ld = Vector3.Transform(dir, inv) / scale;
            var shape = obj.Faces?.Shape ?? obj.Lods.FirstOrDefault(l => l is { Ready: true, Evicted: false });
            var min = shape?.Min ?? new Vector3(-0.5f);
            var max = shape?.Max ?? new Vector3(0.5f);
            if (min.X > max.X) (min, max) = (new Vector3(-0.5f), new Vector3(0.5f));
            if (!Slab(lo, ld, min, max, best, out float tBox)) continue;

            float t = float.MaxValue;
            bool triangles = false;
            FaceGeometry? hf = null;
            int hi = 0;
            float hu = 0, hv = 0;
            if (shape != null)
                foreach (var f in shape.Faces)
                {
                    if (f.PickPositions is not { } p || f.PickIndices is not { } idx) continue;
                    triangles = true;
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                        if (Triangle(lo, ld, p[idx[i]], p[idx[i + 1]], p[idx[i + 2]], out float tt, out float u, out float v) && tt < t)
                        {
                            t = tt; hf = f; hi = i; hu = u; hv = v;
                        }
                }
            if (!triangles) t = tBox;   // no triangles kept (still loading): the box
            if (t <= 0 || t >= best) continue;
            best = t;
            var root = RootOf(prim);
            var wornBy = wearer != 0 && Source.Avatars.TryGetValue(wearer, out var wav) ? wav.ID : UUID.Zero;
            hit = new PickHit(PickKind.Object, Source.Handle, prim.ID, root?.ID ?? prim.ID, wornBy, origin + dir * t, t, LocalId: prim.LocalID);
            onFace = hf != null ? (hf, hi, hu, hv, prim, scale, rot) : null;
        }
        if (hit is { } h && onFace is { } of)
            hit = h with { Surface = SurfaceOf(of.Face, of.Tri, of.U, of.V, of.Prim, of.Scale, of.Rot, dir) };
        return hit;
    }

    // ray_tetrahedron's shape (llmath/raytrace.cpp): regular, of unit height, about its centroid, the apex up
    static readonly Vector3[] TreeTetra =
    {
        new(0, 0, 0.75f), new(0.70710677f, 0, -0.25f), new(-0.35355339f, 0.61237244f, -0.25f), new(-0.35355339f, -0.61237244f, -0.25f),
    };

    /// <summary>A Linden tree as LLVOTree::lineSegmentIntersect picks it: a tetrahedron about the trunk, built from the
    /// viewer's spatial extents (TreeBuilder.Extents) lowered by a quarter of their diagonal, a quarter of their width
    /// across (at most 1 m) and their full height. Region frame; the distance if it's before <paramref name="best"/>.</summary>
    static float? TreeHit(Primitive prim, Vector3 pos, Quaternion rot, Vector3 scale, Vector3 origin, Vector3 dir, float best)
    {
        var (lo, hi) = TreeBuilder.Extents(TreeSpeciesOf(prim), pos, rot, scale);
        var size = hi - lo;
        var centre = (lo + hi) * 0.5f - Vector3.Transform(new Vector3(0, 0, size.Length() * 0.25f), rot);
        size = new Vector3(MathF.Min(size.X * 0.25f, 1f), MathF.Min(size.Y * 0.25f, 1f), size.Z);
        Span<Vector3> p = stackalloc Vector3[4];
        for (int i = 0; i < 4; i++) p[i] = centre + Vector3.Transform(TreeTetra[i] * size, rot);
        float t = float.MaxValue;
        if (Triangle(origin, dir, p[0], p[1], p[2], out float t0, out _, out _)) t = MathF.Min(t, t0);
        if (Triangle(origin, dir, p[0], p[2], p[3], out float t1, out _, out _)) t = MathF.Min(t, t1);
        if (Triangle(origin, dir, p[0], p[3], p[1], out float t2, out _, out _)) t = MathF.Min(t, t2);
        if (Triangle(origin, dir, p[1], p[3], p[2], out float t3, out _, out _)) t = MathF.Min(t, t3);
        return t < best ? t : null;
    }

    /// <summary>Linden grass as LLVOGrass::lineSegmentIntersect picks it: on its blades, those drawn last (nothing until
    /// they're built). Region frame.</summary>
    static float? GrassHit(SceneObject obj, Vector3 origin, Vector3 dir, float best)
    {
        if (obj.Grass is not { Ready: true, Evicted: false, Faces: [{ PickPositions: { } p, PickIndices: { } idx }, ..] }) return null;
        int n = Math.Min(idx.Length, obj.GrassBlades * 12);   // 12 indices a blade, in blade order
        float t = float.MaxValue;
        for (int i = 0; i + 2 < n; i += 3)
            if (Triangle(origin, dir, p[idx[i]], p[idx[i + 1]], p[idx[i + 2]], out float tt, out _, out _) && tt < t) t = tt;
        return t < best ? t : null;
    }

    /// <summary>The surface data of a hit on a face's triangle (Möller-Trumbore's u, v: the weights of its second and
    /// third corners), as LLPickInfo::getSurfaceInfo and LLFace::surfaceToTexture work it out.</summary>
    static PickSurface? SurfaceOf(FaceGeometry f, int tri, float u, float v, Primitive prim, Vector3 scale, Quaternion rot, Vector3 dir)
    {
        if (f.PickPositions is not { } pos || f.PickIndices is not { } idx || f.PickUvs is not { } uvs) return null;
        uint i0 = idx[tri], i1 = idx[tri + 1], i2 = idx[tri + 2];
        var sts = f.PickSt ?? uvs;
        float w = 1 - u - v;
        var st = sts[i0] * w + sts[i1] * u + sts[i2] * v;
        // the texture coordinates: planar faces keep the planar projection in their UVs; then the face's transform
        var tc = uvs[i0] * w + uvs[i1] * u + uvs[i2] * v;
        if (f.TeIndex is >= 0 and < 32 && prim.Textures?.GetFace((uint)f.TeIndex) is { } te)
            tc = TexGen.Transform(tc, te.Rotation, te.OffsetU, te.OffsetV, te.RepeatU, te.RepeatV);

        // the normal: the triangle's, out of the unit shape (normals scale by the inverse), facing the ray
        Vector3 p0 = pos[i0], p1 = pos[i1], p2 = pos[i2];
        var e1 = p1 - p0;
        var e2 = p2 - p0;
        var n = Vector3.Cross(e1, e2) / scale;
        if (n.LengthSquared() < 1e-20f) return null;
        n = Vector3.Normalize(Vector3.Transform(n, rot));
        if (Vector3.Dot(n, dir) > 0) n = -n;
        // the binormal: the way t (the surface's second coordinate) grows, across the normal
        var d1 = sts[i1] - sts[i0];
        var d2 = sts[i2] - sts[i0];
        float det = d1.X * d2.Y - d2.X * d1.Y;
        Vector3 b = MathF.Abs(det) > 1e-12f ? (e2 * d1.X - e1 * d2.X) / det : e2;
        b = Vector3.Transform(b * scale, rot);
        b -= n * Vector3.Dot(n, b);
        if (b.LengthSquared() < 1e-12f) b = Vector3.Cross(n, MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
        return new PickSurface(f.TeIndex, st, tc, n, Vector3.Normalize(b));
    }

    Primitive? RootOf(Primitive prim)
    {
        var p = prim;
        for (int i = 0; i < 4 && p.ParentID != 0 && _objects.TryGetValue(p.ParentID, out var parent); i++) p = parent.Prim;
        return p;
    }

    internal static bool IsHud(Primitive prim)
    {
        int point = (int)prim.PrimData.AttachmentPoint;
        return point is >= 31 and <= 38;
    }

    /// <summary>Where the ray first goes under the terrain, within <paramref name="maxT"/> (marching, then halving).</summary>
    float? Ground(Vector3 o, Vector3 d, float maxT)
    {
        long now = System.Environment.TickCount64;
        if (_pickHeights == null || now - _pickHeightsAt > 2000)
        {
            _pickHeights = Source.Heights();
            _pickHeightsAt = now;
        }
        if (_pickHeights is not { } hm || hm.Width < 2) return null;
        float Height(Vector3 p)
        {
            float x = Math.Clamp(p.X, 0, hm.Width - 1.001f), y = Math.Clamp(p.Y, 0, hm.Width - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            int w = hm.Width;
            float a = hm.Data[y0 * w + x0], b = hm.Data[y0 * w + x0 + 1], c = hm.Data[(y0 + 1) * w + x0], e = hm.Data[(y0 + 1) * w + x0 + 1];
            return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + e * fx) * fy;
        }
        bool Inside(Vector3 p) => p.X >= 0 && p.Y >= 0 && p.X < hm.Width && p.Y < hm.Width;
        const float step = 0.5f;
        float prev = 0;
        for (float t = step; t < MathF.Min(maxT, 512); t += step)
        {
            var p = o + d * t;
            if (Inside(p) && p.Z < Height(p))
            {
                float lo = prev, hi = t;
                for (int i = 0; i < 12; i++)
                {
                    float mid = (lo + hi) / 2;
                    var q = o + d * mid;
                    if (q.Z < Height(q)) hi = mid; else lo = mid;
                }
                return hi;
            }
            prev = t;
        }
        return null;
    }

    /// <summary>Ray against an axis-aligned box: the entry distance (0 inside), if it's before <paramref name="maxT"/>.</summary>
    static bool Slab(Vector3 o, Vector3 d, Vector3 min, Vector3 max, float maxT, out float tEnter)
    {
        float t0 = 0, t1 = maxT;
        for (int a = 0; a < 3; a++)
        {
            float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z, da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            float mn = a == 0 ? min.X : a == 1 ? min.Y : min.Z, mx = a == 0 ? max.X : a == 1 ? max.Y : max.Z;
            if (MathF.Abs(da) < 1e-12f)
            {
                if (oa < mn || oa > mx) { tEnter = 0; return false; }
                continue;
            }
            float inv = 1 / da, ta = (mn - oa) * inv, tb = (mx - oa) * inv;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta);
            t1 = MathF.Min(t1, tb);
            if (t0 > t1) { tEnter = 0; return false; }
        }
        tEnter = t0;
        return true;
    }

    /// <summary>Möller-Trumbore, both sides; u and v weigh the second and third corners.</summary>
    static bool Triangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t, out float u, out float v)
    {
        t = 0; v = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        u = 0;
        if (MathF.Abs(det) < 1e-14f) return false;
        float inv = 1 / det;
        var s = o - a;
        u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1);
        v = Vector3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 1e-5f;
    }
}
