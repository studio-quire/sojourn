using LibreMetaverse;
using Sojourn.Render.Geometry;

namespace Sojourn.Render.World;

/// <summary>
/// Moving objects (MotionSmoother.Prims.cs): the index places objects when the region reports a change, so anything
/// that moves between reports (a vehicle, keyframed motion, a spinning sign) is placed again every frame here, its
/// linkset with it, where the smoother says it is at the frame's display time. Only their placement and cull bounds
/// move per frame; the probe scan's volume follows the region's updates as before.
/// </summary>
public sealed partial class RegionScene
{
    readonly HashSet<SceneObject> _moving = new();
    readonly List<SceneObject> _movingNow = new();

    float Dilation => Source.HasSimulator && Source.Simulator.Stats.Dilation is > 0 and var d ? d : 1f;
    internal float RegionSize => Source.HasSimulator && Source.Simulator.SizeX > 0 ? Source.Simulator.SizeX : 256f;

    /// <summary>A prim's pose relative to its parent (the region's for a root) this frame, carried on between updates.</summary>
    (Vector3 Pos, Quaternion Rot) Local(Primitive p) =>
        _world.Motion.Tracks(p) ? _world.Motion.Prim(p, Source.Handle, Dilation, RegionSize) : (V(p.Position), Q(p.Rotation));

    /// <summary>After placing a world object: keep placing it every frame while it moves.</summary>
    void NoteMotion(SceneObject obj)
    {
        if (obj.Placed && obj.Owner == 0 && _world.Motion.PrimMoving(obj.Prim)) _moving.Add(obj);
    }

    /// <summary>Each frame, after the region's changes: everything moving placed where it is now.</summary>
    void MoveMoving()
    {
        if (_moving.Count == 0) return;
        _movingNow.Clear();
        _movingNow.AddRange(_moving);
        foreach (var obj in _movingNow)
        {
            if (!obj.Placed || obj.Owner != 0 || !_objects.TryGetValue(obj.LocalId, out var still) || !ReferenceEquals(still, obj))
            {
                _moving.Remove(obj);
                continue;
            }
            bool moving = _world.Motion.PrimMoving(obj.Prim);
            // a child moves with its root: placed there if the root moves too
            var prim = obj.Prim;
            if (prim.ParentID == 0) Move(obj, Local(prim));
            else if (_objects.TryGetValue(prim.ParentID, out var root) && root.Placed)
            {
                if (!_moving.Contains(root))
                {
                    var (lp, lr) = Local(prim);
                    Move(obj, (root.WPos + Vector3.Transform(lp, root.WRot), Quaternion.Normalize(Quaternion.Concatenate(lr, root.WRot))));
                }
            }
            if (!moving) _moving.Remove(obj);   // (placed where it settled, just now)
        }
    }

    void Move(SceneObject obj, (Vector3 Pos, Quaternion Rot) at)
    {
        bool moved = at.Pos != obj.WPos;   // (only turning, as a spinning sign does: its cull sphere stays)
        obj.WPos = at.Pos;
        obj.WRot = at.Rot;
        if (moved)
        {
            MoveBounds(obj);
            if (!obj.Skip) Reindex(obj);
        }
        if (!_children.TryGetValue(obj.LocalId, out var children)) return;
        foreach (var c in children)
        {
            if (c.Owner != 0 || !c.Placed) continue;
            var (lp, lr) = Local(c.Prim);
            Move(c, (at.Pos + Vector3.Transform(lp, at.Rot), Quaternion.Normalize(Quaternion.Concatenate(lr, at.Rot))));
        }
    }

    /// <summary>Bounds' cull sphere and extents for a moved object (the probe volume waits for the region's update).</summary>
    void MoveBounds(SceneObject obj)
    {
        if (obj.Kind is ShapeKind.Tree or ShapeKind.Grass) { Bounds(obj); return; }
        var s = obj.Prim.Scale;
        float r = 0.5f * new Vector3(s.X, s.Y, s.Z).Length();
        obj.BoxMin = obj.WPos - new Vector3(r);
        obj.BoxMax = obj.WPos + new Vector3(r);
        obj.CullCenter = obj.WPos;
        obj.CullRadius = r;
    }
}
