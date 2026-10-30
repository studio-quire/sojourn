using System.Numerics;
using Sojourn.Render.Frame;
using Sojourn.Render.Shadows;

namespace Sojourn.Render.Realtime;

// The coarse far sun cascade (docs/rendering.md > Where shadows end): fitted with a margin and kept for a few frames
// while what it must cover still lies inside its map, so it's drawn only now and then. Written by Claude Opus 5.5,
// 2026-10-29.
public sealed partial class FrameRenderer
{
    /// <summary>SOJOURN_FAR_SHADOWS=N: the far cascade's map size over the setting (0: none).</summary>
    static readonly int? EnvFarShadows = int.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_FAR_SHADOWS"), out var n) ? n : null;
    /// <summary>SOJOURN_SHADOW_REACH=F: the governor's shadow trim held at F (tests and pictures).</summary>
    static readonly float? EnvShadowReach = float.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_SHADOW_REACH"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : null;
    /// <summary>The far map is drawn again at least this often (frames), for moving casters; SOJOURN_FAR_REFRESH=N.</summary>
    static readonly int FarRefresh = int.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_FAR_REFRESH"), out var f) && f > 0 ? f : 8;
    /// <summary>Casters smaller than this many texels across (bounding radius) in the far map stay out of it.</summary>
    static readonly float FarCasterTexels = float.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_FAR_CASTER_TEXELS"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 1f;

    /// <summary>SOJOURN_AB=farshadows: the far cascade on and off by turns (Renderer.AbSwitches).</summary>
    public static bool AbFarOff;

    // the far map as last drawn: region -> map, the frame, the renderer's drawing count it'll be, region, sun, size
    Matrix4x4 _farWorld, _farViewProj;
    long _farFrame, _farStamp = -1, _frameCount;
    ulong _farRegion;
    Vector3 _farSun;
    int _farSize;
    bool _planningStill;
    long _farKept, _farFresh;

    int FarShadowSize => AbFarOff ? 0 : EnvFarShadows ?? Quality.FarShadowSize;

    /// <summary>The far cascade for this frame's view: kept from an earlier frame when it can be, else drawn afresh with
    /// its casters gathered (casters[6]).</summary>
    void FarShadow(ViewSetup v, SunShadowSetup setup, ShadowFrustum?[] casters)
    {
        v.FarShadowKept = false;
        v.FarCasterTexels = 0;
        v.Sky.shadow_far = default;
        if (setup.FarClip.W <= 0) return;
        if (!_planningStill) _frameCount++;   // frames with a far cascade (SOJOURN_AB=farshadows: its own side's)
        var far = setup.Far;
        var sun = v.SunUp ? v.SunDirRegion : v.MoonDirRegion;
        v.Sky.shadow_far = setup.FarClip;
        v.Sky.shadow_far_res = new Vector2(setup.FarMapSize);
        v.FarCasterTexels = FarCasterTexels;
        if (!_planningStill && _farStamp == _gpu.Renderer.FarShadowDrawn && _frameCount - _farFrame < FarRefresh
            && _farRegion == v.RegionHandle && _farSize == setup.FarMapSize && Vector3.Dot(_farSun, sun) >= 0.999999f
            && Inside(far.ReceiverPoints, _farWorld))
        {
            Matrix4x4.Invert(v.View, out var toWorld);
            v.Sky.shadow_matrix6 = toWorld * _farWorld;
            v.Sky.shadow_viewproj6 = _farViewProj;
            v.FarShadowKept = true;
            _farKept++;
        }
        else
        {
            v.Sky.shadow_matrix6 = far.ShadowMatrix;
            v.Sky.shadow_viewproj6 = far.LightViewProj;
            casters[ViewSetup.FarCascade] = far.CullFrustum;
            if (_planningStill) { _farStamp = -1; return; }   // a still's own map: the next frame draws the person's again
            (_farWorld, _farViewProj) = (v.View * far.ShadowMatrix, far.LightViewProj);
            (_farFrame, _farStamp, _farRegion, _farSun, _farSize) = (_frameCount, _gpu.Renderer.FarShadowDrawn + 1, v.RegionHandle, sun, setup.FarMapSize);
            _farFresh++;
        }
        if ((_farKept + _farFresh) % 2700 == 0)
            _opt.Log?.Invoke($"far shadows: drawn {_farFresh}, kept {_farKept} ({100.0 * _farKept / (_farKept + _farFresh):0}% of frames)");
    }

    /// <summary>Every receiver (region space) inside a map's box (region -> map texture coordinates and depth).</summary>
    static bool Inside(Vector3[] points, Matrix4x4 world)
    {
        if (points.Length == 0) return false;
        const float eps = 1e-4f;
        foreach (var p in points)
        {
            var q = Vector4.Transform(new Vector4(p, 1), world);
            if (q.W <= 0) return false;
            float u = q.X / q.W, w = q.Y / q.W, z = q.Z / q.W;
            if (u < -eps || u > 1 + eps || w < -eps || w > 1 + eps || z < -eps || z > 1 + eps) return false;
        }
        return true;
    }
}
