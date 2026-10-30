namespace Sojourn.Render.Shadows;

/// <summary>
/// The scene input the shadow fit needs: LLPipeline::getVisibleExtents, i.e. the union of the
/// bounding boxes of everything (of the shadow render types) that touches a frustum.
/// The fit is only as tight as these boxes; they bound the receivers each cascade must cover.
/// </summary>
public interface ISceneExtents
{
    /// <summary>False when nothing touches the frustum (no receivers).</summary>
    bool TryGetVisibleExtents(ShadowFrustum frustum, out Vector3 min, out Vector3 max);

    /// <summary>How near and how far from <paramref name="eye"/> receivers lie, in every direction, counting only those
    /// within <paramref name="reach"/> (the farthest capped at it): the cascade splits by distance from the head. False
    /// when there are none (or the source can't say).</summary>
    bool TryGetRadialRange(Vector3 eye, float reach, out float near, out float far) { near = far = 0; return false; }
}

/// <summary>
/// ISceneExtents over a flat list of region-space boxes, each tested with LLCamera::AABBInFrustum
/// (all six planes). The viewer tests octree groups rather than single objects, so its boxes are
/// somewhat coarser; per-object boxes give a slightly tighter (sharper) fit.
///
/// What the viewer counts (the shadow render-type mask): prims/meshes, avatars, trees, grass,
/// terrain patches (16x16 m, z extent at least 1 m), and water: each region's water object is
/// a 256x256x0 box at water height centred on the region; edge/void water beyond the regions is
/// 512 m tall boxes from water height up (they stretch 2048 m out), which inflate the fit when
/// a split reaches past the region edge.
/// </summary>
public sealed class AabbSceneExtents : ISceneExtents
{
    readonly List<(Vector3 Min, Vector3 Max)> _boxes = [];

    public AabbSceneExtents() { }

    public AabbSceneExtents(IEnumerable<(Vector3 Min, Vector3 Max)> boxes) => _boxes.AddRange(boxes);

    public void Add(Vector3 min, Vector3 max) => _boxes.Add((min, max));

    public void Clear() => _boxes.Clear();

    public int Count => _boxes.Count;

    public bool TryGetRadialRange(Vector3 eye, float reach, out float near, out float far)
    {
        near = float.PositiveInfinity;
        far = 0;
        foreach (var (bmin, bmax) in _boxes)
        {
            float n = Vector3.Distance(Vector3.Clamp(eye, bmin, bmax), eye);
            if (n > reach) continue;
            near = MathF.Min(near, n);
            far = MathF.Max(far, MathF.Min(Vector3.Max(Vector3.Abs(bmin - eye), Vector3.Abs(bmax - eye)).Length(), reach));
        }
        return far > 0;
    }

    public bool TryGetVisibleExtents(ShadowFrustum frustum, out Vector3 min, out Vector3 max)
    {
        const float X = 65536f;
        min = new Vector3(X);
        max = new Vector3(-X);
        bool any = false;
        foreach (var (bmin, bmax) in _boxes)
        {
            if (frustum.AabbInFrustum((bmin + bmax) * 0.5f, (bmax - bmin) * 0.5f) > 0)
            {
                any = true;
                ShadowMath.UpdateMinMax(ref min, ref max, bmin);
                ShadowMath.UpdateMinMax(ref min, ref max, bmax);
            }
        }
        return any;
    }
}
