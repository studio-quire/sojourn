namespace Sojourn.Render.World;

public sealed partial class Avatars
{
    /// <summary>Where a rigged mesh worn by an avatar posed last frame is, for pointing at it: its bind-space box
    /// carried rigidly by one joint of its skin (the pelvis when the skin has it, else its first), as an axis-aligned
    /// box in the region's frame. Null when the wearer wasn't posed (an impostor, a jelly doll, out of view). Its
    /// vertices are in the avatar's bind space, not the attachment prim's unit space: scaled by the prim and placed at
    /// the attachment point, the box was metres to tens of metres off (the session of 2026-10-27: someone's worn mesh
    /// was hit 72 m away, 46 m above them).</summary>
    public (Vector3 Min, Vector3 Max)? RiggedBounds(ulong region, uint avatarLocalId, ShapeGeometry shape)
    {
        if (shape.InverseBind == null || shape.Skin == null || shape.Min.X > shape.Max.X) return null;
        if (!_posed.TryGetValue((region, avatarLocalId), out var p)) return null;
        var share = ShareOf(shape);
        if (share.Map.Length == 0) return null;
        int pelvis = _cd.Skeleton["mPelvis"];
        int j = Math.Max(0, Array.IndexOf(share.Map, pelvis));
        var m = share.InverseBind[j] * p.Body.Pose.World[share.Map[j]];
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3((c & 1) != 0 ? shape.Max.X : shape.Min.X, (c & 2) != 0 ? shape.Max.Y : shape.Min.Y,
                (c & 4) != 0 ? shape.Max.Z : shape.Min.Z);
            var w = Vector3.Transform(corner, m) - p.Offset;
            lo = Vector3.Min(lo, w);
            hi = Vector3.Max(hi, w);
        }
        // never further out than the wearer's animated extents allow (a stray vertex, a broken bind)
        var reach = new Vector3(MathF.Max(1f, p.Body.Diagonal));
        var centre = p.Body.Center - p.Offset;
        lo = Vector3.Max(lo, centre - reach);
        hi = Vector3.Min(hi, centre + reach);
        return lo.X <= hi.X && lo.Y <= hi.Y && lo.Z <= hi.Z ? (lo, hi) : null;
    }
}
