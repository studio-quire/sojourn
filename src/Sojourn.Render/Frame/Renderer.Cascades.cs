using System.Numerics;

namespace Sojourn.Render.Frame;

// The far sun cascades every other frame (FarCascadesHalf): cascades 2 and 3 take turns, each frame drawing one and
// sampling the other's map from the frame before, through that frame's world-to-map matrix. A map is reused only where
// that can't show: the same region, sun and map size, a cascade that has receivers both frames, and the box this
// frame's fresh map would cover (the split's receivers as fitted) inside the old map's. Casters that move are a frame late in those cascades only, and
// while the view moves the transition bands either side of a split may differ a little.
// Written by Claude Opus 5.5, 2026-10-27.
public sealed partial class Renderer
{
    /// <summary>SOJOURN_FAR_CASCADES_HALF=0 draws every cascade every frame.</summary>
    public static bool FarCascadesHalf = System.Environment.GetEnvironmentVariable("SOJOURN_FAR_CASCADES_HALF") != "0";

    /// <summary>The last fresh drawing of each sun cascade: its world-to-map matrix, the frame, region, sun and map size.</summary>
    readonly (Matrix4x4 World, long Frame, ulong Region, Vector3 Sun, uint Size)[] _cascadeDrawn = new (Matrix4x4, long, ulong, Vector3, uint)[4];
    long _cascadeFrame;
    readonly long[] _refused = new long[3];   // refusals: not the frame before, always lit, outside the old map
    /// <summary>The cascades this frame samples from an older map (bit c), and how many frames did.</summary>
    int _cascadesReused;
    public long CascadesReused, CascadeFrames;

    /// <summary>Before the views are written: which far cascade is kept from last frame, its matrices put in the eyes',
    /// mirrors' and the camera preview's skies.</summary>
    void PlanCascades(IReadOnlyList<Eye> eyes, IReadOnlyList<MirrorView?>? mirrors, ViewSetup? preview = null)
    {
        _cascadesReused = 0;
        _cascadeFrame++;
        var e0 = eyes[0].View;
        if (e0.Sky.shadows_on == 0) return;
        CascadeFrames++;
        int skip = FarCascadesHalf ? (_cascadeFrame % 2 == 0 ? 3 : 2) : -1;
        if (skip >= 0 && CanReuse(skip, eyes, mirrors))
        {
            var world = _cascadeDrawn[skip].World;
            foreach (var (v, _) in eyes) SetCascade(v, skip, world);
            if (mirrors != null) foreach (var m in mirrors) if (m != null) SetCascade(m.View, skip, world);
            if (preview != null) SetCascade(preview, skip, world);
            _cascadesReused = 1 << skip;
            CascadesReused++;
        }
        if (CascadeFrames % 900 == 0) Log?.Invoke($"far cascades: one kept from the frame before in {CascadesReused} of {CascadeFrames} frames (refused: {string.Join("/", _refused)})");
        // the fresh ones, for later frames
        for (int c = 0; c < 4; c++)
        {
            if ((_cascadesReused & (1 << c)) != 0) continue;
            var sm = Cascade(e0.Sky, c);
            _cascadeDrawn[c] = (e0.View * sm, _cascadeFrame, e0.RegionHandle, e0.SunUp ? e0.SunDirRegion : e0.MoonDirRegion, ShadowMapSize);
        }
    }

    bool CanReuse(int c, IReadOnlyList<Eye> eyes, IReadOnlyList<MirrorView?>? mirrors)
    {
        var d = _cascadeDrawn[c];
        var e0 = eyes[0].View;
        var sun = e0.SunUp ? e0.SunDirRegion : e0.MoonDirRegion;
        if (d.Frame != _cascadeFrame - 1 || d.Region != e0.RegionHandle || d.Size != ShadowMapSize || Vector3.Dot(d.Sun, sun) < 0.999999f) { _refused[0]++; return false; }
        if (AlwaysLit(d.World) || AlwaysLit(Cascade(e0.Sky, c))) { _refused[1]++; return false; }
        const float eps = 1e-4f;
        // fitted with a margin (RenderQuality.FarCascadeMargin): the receivers this frame needs, as fitted without it,
        // inside the old (grown) map, so it holds while the view moves a little
        if ((c == 2 ? e0.FarReceivers2 : e0.FarReceivers3) is { Length: > 0 } need)
        {
            foreach (var p in need)
            {
                var q = Vector4.Transform(new Vector4(p, 1), d.World);
                if (q.W <= 0) { _refused[2]++; return false; }
                float u = q.X / q.W, w = q.Y / q.W, z = q.Z / q.W;
                if (u < -eps || u > 1 + eps || w < -eps || w > 1 + eps || z < -eps || z > 1 + eps) { _refused[2]++; return false; }
            }
            return true;
        }
        // what this frame's fresh map would hold (its box: the split's receivers, as fitted) inside the old map's box (same
        // light, so the same casters toward it); a still view fits the same box both frames
        if (!Matrix4x4.Invert(e0.View * Cascade(e0.Sky, c), out var freshToWorld)) { _refused[2]++; return false; }
        var freshToOld = freshToWorld * d.World;
        for (int k = 0; k < 8; k++)
        {
            var q = Vector4.Transform(new Vector4(k & 1, (k >> 1) & 1, (k >> 2) & 1, 1), freshToOld);
            float u = q.X / q.W, w = q.Y / q.W, z = q.Z / q.W;
            if (u < -eps || u > 1 + eps || w < -eps || w > 1 + eps || z < -eps || z > 1 + eps) { _refused[2]++; return false; }
        }
        return true;
    }

    static bool AlwaysLit(Matrix4x4 m) => m.M11 == 0 && m.M22 == 0 && m.M33 == 0 && m.M12 == 0 && m.M21 == 0;

    static Matrix4x4 Cascade(in GpuSky s, int c) => c switch
    {
        0 => s.shadow_matrix0, 1 => s.shadow_matrix1, 2 => s.shadow_matrix2, _ => s.shadow_matrix3,
    };

    /// <summary>A view samples cascade c through a given world-to-map matrix (its eye space to the map).</summary>
    static void SetCascade(ViewSetup v, int c, Matrix4x4 world)
    {
        Matrix4x4.Invert(v.View, out var toWorld);
        var m = toWorld * world;
        if (c == 2) v.Sky.shadow_matrix2 = m; else v.Sky.shadow_matrix3 = m;
    }
}
