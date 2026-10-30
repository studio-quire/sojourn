using LibreMetaverse;
using Sojourn.Render.Frame;

namespace Sojourn.Render.World;

/// <summary>A HUD the person wears: its root prim, its attachment point, its extent in HUD space and its prims' (the
/// root first).</summary>
public sealed record HudObject(uint LocalId, UUID Id, int Point, Vector3 Min, Vector3 Max, IReadOnlyList<HudPart> Parts);

/// <summary>One prim of a HUD: its box in HUD space.</summary>
public readonly record struct HudPart(uint LocalId, UUID Id, Vector3 Min, Vector3 Max);

/// <summary>
/// The person's HUD attachments (points 31-38). They live in HUD space, the viewer's: x into the screen, y to the left,
/// z up, the screen one unit tall and the camera's aspect wide, centred on the origin; each point's frame comes from
/// the caller (Realtime/Hud.cs). The world's draw lists and picks never place them (their AttachmentFrame gives HUD
/// points no frame); these do, placing them by the HUD's frames given for the call.
/// </summary>
public sealed partial class RegionScene
{
    /// <summary>The person's HUDs into a draw list for a HUD view (an orthographic camera looking along +x). The other
    /// hooks are the main view's (<see cref="Hooks"/>).</summary>
    public RegionDrawStats BuildHudDrawList(ViewSetup v, DrawList dl, uint self, Func<int, (Vector3 Pos, Quaternion Rot)?> point, float pxPerUnit, ViewOptions opt)
    {
        if (!_attachments.TryGetValue(self, out var worn)) return new RegionDrawStats();
        Func<uint, int, (Vector3 Pos, Quaternion Rot)?> frame = (av, p) => av == self ? point(p) : null;
        var ctx = new DrawCtx(Hooks, v, dl, Vector3.Zero, opt, Frustum.All, null, Vector3.UnitX, Source.Assets, Source.Simulator)
        {
            HudPx = Math.Max(pxPerUnit, 1e-3f),
        };
        foreach (var obj in worn)
            if (!obj.Skip && AttachmentTransform(obj.Prim, frame, out var pos, out var rot))
                DrawObject(ctx, obj, pos, rot);
        ctx.Finish(_world);
        return ctx.Stats;
    }

    /// <summary>The HUDs worn, each linkset's box in HUD space (its prims' boxes, by scale and rotation).</summary>
    public List<HudObject> HudObjects(uint self, Func<int, (Vector3 Pos, Quaternion Rot)?> point)
    {
        var list = new List<HudObject>();
        if (!_attachments.TryGetValue(self, out var worn)) return list;
        Func<uint, int, (Vector3 Pos, Quaternion Rot)?> frame = (av, p) => av == self ? point(p) : null;
        var boxes = new Dictionary<uint, (Vector3 Min, Vector3 Max, List<HudPart> Parts, Primitive Root)>();
        foreach (var obj in worn)
        {
            if (!AttachmentTransform(obj.Prim, frame, out var pos, out var rot)) continue;
            var root = obj.Prim.ParentID != 0 && _objects.TryGetValue(obj.Prim.ParentID, out var r) && r.Prim.ParentID == self ? r.Prim : obj.Prim;
            var half = new Vector3(obj.Prim.Scale.X, obj.Prim.Scale.Y, obj.Prim.Scale.Z) * 0.5f;
            var m = Matrix4x4.CreateFromQuaternion(rot);
            var ext = new Vector3(
                MathF.Abs(m.M11) * half.X + MathF.Abs(m.M21) * half.Y + MathF.Abs(m.M31) * half.Z,
                MathF.Abs(m.M12) * half.X + MathF.Abs(m.M22) * half.Y + MathF.Abs(m.M32) * half.Z,
                MathF.Abs(m.M13) * half.X + MathF.Abs(m.M23) * half.Y + MathF.Abs(m.M33) * half.Z);
            var b = boxes.TryGetValue(root.LocalID, out var have) ? have : (new Vector3(float.MaxValue), new Vector3(float.MinValue), new List<HudPart>(), root);
            var part = new HudPart(obj.Prim.LocalID, obj.Prim.ID, pos - ext, pos + ext);
            if (obj.Prim == root) b.Item3.Insert(0, part); else b.Item3.Add(part);
            boxes[root.LocalID] = (Vector3.Min(b.Item1, pos - ext), Vector3.Max(b.Item2, pos + ext), b.Item3, root);
        }
        foreach (var (id, b) in boxes)
            list.Add(new HudObject(id, b.Root.ID, (int)b.Root.PrimData.AttachmentPoint, b.Min, b.Max, b.Parts));
        return list;
    }

    /// <summary>The nearest HUD prim along a ray in HUD space (dir normalised, usually +x through a point on the
    /// screen), with where on its surface, as the world's picks: normal and binormal in HUD space.</summary>
    public PickHit? HudPick(uint self, Func<int, (Vector3 Pos, Quaternion Rot)?> point, Vector3 origin, Vector3 dir, float maxT,
        Func<Primitive, bool>? only = null)
    {
        if (!_attachments.TryGetValue(self, out var worn)) return null;
        float best = maxT;
        return HitPrims(origin, dir, ref best, 0, only, worn, (av, p) => av == self ? point(p) : null);
    }
}
