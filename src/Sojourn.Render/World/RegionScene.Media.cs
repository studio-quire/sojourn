using LibreMetaverse;
using Sojourn.Render.Geometry;
using Sojourn.Render.Media;

namespace Sojourn.Render.World;

/// <summary>A face's place for media (RegionScene.FaceFrame): middle, normal, the page's right and up, and its size.</summary>
public readonly record struct MediaFaceFrame(Vector3 Centre, Vector3 Normal, Vector3 Right, Vector3 Up, float Width, float Height);

// Media on faces (docs/media.md): a face with a media surface samples the surface's texture instead of its own once
// the first frame is in; until then it keeps its own. Prim media is looked up by the prim and face, parcel media by
// the texture the face shows.
public sealed partial class RegionScene
{
    /// <summary>The media texture for this face, or null for its own. Either way the surface is recorded so the face
    /// resolves again when the surface's texture changes state or slot, and the surface hears it was drawn. The prim
    /// is the context's MediaPrim (0: none, as for rigged meshes), its surfaces go in its MediaUsed.</summary>
    SceneTexture? MediaTexture(DrawCtx ctx, UUID textureId, int teIndex)
    {
        var media = _world.Media;
        if (!media.Any) return null;
        var s = media.Lookup(Source.Handle, ctx.MediaPrim, teIndex, textureId);
        if (s == null) return null;
        if (ctx.Deps != null) { ctx.Deps.Add(s.Texture); ctx.DepFactors.Add(0f); }
        (ctx.MediaUsed ??= new()).Add(s);
        MediaSurfaces.Seen(s, ctx.ObjArea, ctx.MediaDist);
        return s.Texture.State == TexState.Ready ? s.Texture : null;
    }

    /// <summary>
    /// Where a face is, as a page on it is laid out: its middle, its normal (out of the face), the way the texture's u
    /// grows (the page's left to right) and v (bottom to top) across it, and its size along those, in this region's
    /// frame. From the triangles drawn last; null until the prim's geometry is in. Render thread, between frames.
    /// </summary>
    public MediaFaceFrame? FaceFrame(uint localId, int teIndex)
    {
        if (!_objects.TryGetValue(localId, out var obj) || !Transform(obj, out var pos, out var rot)) return null;
        var prim = obj.Prim;
        var scale = new Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z);
        var shape = obj.Faces?.Shape ?? obj.Lods.FirstOrDefault(l => l is { Ready: true, Evicted: false });
        if (shape == null) return null;
        var te = teIndex is >= 0 and < 32 ? prim.Textures?.GetFace((uint)teIndex) : null;
        Vector3 normal = default, du = default, dv = default, sum = default;
        float area = 0;
        var points = new List<Vector3>();
        foreach (var f in shape.Faces)
        {
            if (f.TeIndex != teIndex || f.PickPositions is not { } p || f.PickIndices is not { } idx || f.PickUvs is not { } uvs) continue;
            Vector3 W(uint i) => pos + Vector3.Transform(p[i] * scale, rot);
            Vector2 T(uint i) => te != null ? TexGen.Transform(uvs[i], te.Rotation, te.OffsetU, te.OffsetV, te.RepeatU, te.RepeatV) : uvs[i];
            for (int k = 0; k + 2 < idx.Length; k += 3)
            {
                Vector3 a = W(idx[k]), b = W(idx[k + 1]), c = W(idx[k + 2]);
                var cross = Vector3.Cross(b - a, c - a);
                float ar = cross.Length() / 2;
                if (ar < 1e-8f) continue;
                normal += cross / 2;
                sum += (a + b + c) / 3 * ar;
                area += ar;
                // the triangle's dP/du and dP/dv, weighted by its area
                Vector2 ta = T(idx[k]), tb = T(idx[k + 1]), tc = T(idx[k + 2]);
                Vector2 d1 = tb - ta, d2 = tc - ta;
                float det = d1.X * d2.Y - d2.X * d1.Y;
                if (MathF.Abs(det) > 1e-10f)
                {
                    du += ((b - a) * d2.Y - (c - a) * d1.Y) / det * ar;
                    dv += ((c - a) * d1.X - (b - a) * d2.X) / det * ar;
                }
                points.Add(a); points.Add(b); points.Add(c);
            }
        }
        if (area <= 0 || normal.LengthSquared() < 1e-12f) return null;
        var n = Vector3.Normalize(normal);
        var centre = sum / area;
        var right = du - n * Vector3.Dot(n, du);
        right = right.LengthSquared() > 1e-12f ? Vector3.Normalize(right) : Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, n) is { } x && x.LengthSquared() > 1e-6f ? x : Vector3.UnitX);
        var up = Vector3.Normalize(Vector3.Cross(n, right));
        if (Vector3.Dot(up, dv) < 0) up = -up;
        float r0 = float.MaxValue, r1 = float.MinValue, u0 = float.MaxValue, u1 = float.MinValue;
        foreach (var q in points)
        {
            float rx = Vector3.Dot(q - centre, right), uy = Vector3.Dot(q - centre, up);
            r0 = MathF.Min(r0, rx); r1 = MathF.Max(r1, rx); u0 = MathF.Min(u0, uy); u1 = MathF.Max(u1, uy);
        }
        centre += right * ((r0 + r1) / 2) + up * ((u0 + u1) / 2);
        return new MediaFaceFrame(centre, n, right, up, r1 - r0, u1 - u0);
    }

    /// <summary>A surface came or went on this object's faces: they're resolved afresh next time it's drawn.</summary>
    internal void InvalidateMedia(uint localId)
    {
        if (_objects.TryGetValue(localId, out var o) && o.Faces != null) o.Faces.Outdated = true;
    }

    /// <summary>A surface came or went for this texture (parcel media): every object showing it resolves afresh.</summary>
    internal void InvalidateMedia(UUID texture)
    {
        foreach (var o in _objects.Values)
            if (o.Faces is { } f)
                foreach (var t in f.Textures)
                    if (t.Id == texture) { f.Outdated = true; break; }
    }
}
