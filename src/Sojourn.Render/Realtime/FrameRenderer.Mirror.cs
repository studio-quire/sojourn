// Mirrors: prims set as box reflection probes with the mirror flag (PRIM_REFLECTION_PROBE ... MIRROR), drawn as the
// viewer's hero probe draws them (llheroprobemanager.cpp, class3 reflectionProbeF.glsl tapHeroProbe; LGPL 2.1, Linden
// Research, Inc.): the nearest visible one each frame, its plane the prim's +Z face through its centre, its reflection
// blended into glossy surfaces inside its box and in front of its plane. The viewer renders a cube map from the camera
// mirrored in that plane, every face every frame at Ultra. In stereo one point can't serve two eyes, so each eye gets
// its own reflection: the frame's draw list rendered from that eye mirrored, only over the pixels the mirror covers,
// at a fraction of the eye's size (RenderQuality.MirrorScale). The draw list is built once for both: culling takes
// what the eyes see or what the mirror can show (a frustum from the mirrored camera through the mirror's outline).
using LibreMetaverse;
using Sojourn.Render.Frame;
using Sojourn.Render.World;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Realtime;

public sealed partial class FrameRenderer
{
    /// <summary>This frame's mirror: its plane (region frame of the view, kept where dot(n, p) + d >= 0), the box its
    /// probe covers, whether it shows avatars (the dynamic flag), and each eye's reflection (null where an eye can't see
    /// it).</summary>
    sealed class MirrorFrame
    {
        public required Vector4 Plane;
        public required Matrix4x4 ToUnitBox;
        public required Vector3 Center, Scale;
        public required Quaternion Rotation;
        public required bool Dynamic;
        public required Frustum Portal;
        public MirrorView?[] Views = Array.Empty<MirrorView?>();
    }

    MirrorFrame? _mirror;
    uint _loggedMirror;
    // the eyes' hero and the camera preview's, each kept until another scores clearly better
    (uint Id, ulong Region) _hero, _previewHero;

    /// <summary>Mirrors in view this frame (for tests and the view line): the probe's local id, or 0.</summary>
    public uint MirrorInView { get; private set; }

    /// <summary>The hero mirror (LLHeroProbeManager::update): one at a time, among box probes flagged as mirrors within
    /// the draw distance and in view. The viewer takes the nearest (and wants each pick lower along the camera's up axis
    /// than the last). A headset sees ~100 degrees, where the nearest is often a mirror at the edge of sight while the
    /// one looked at stays a plain shiny surface; so here the distance counts more the further off the line of sight
    /// a mirror is (tripled at 90 degrees). Also for a head that's never still: a mirror seen from behind isn't a
    /// candidate (it would reflect only what its plane clips away), and the last frame's mirror stays until another
    /// scores 10% better, so two side by side don't trade places frame to frame. Null when there's none or mirrors are
    /// off. The eyes' (<paramref name="person"/>: logged, and <see cref="MirrorInView"/>), or another view's: a still's
    /// (a fresh choice from its camera) or the camera preview's, with a hold of their own.</summary>
    MirrorFrame? PlanMirror(ViewSetup center, RegionScene region, Frustum frustum, ref (uint Id, ulong Region) held, bool person)
    {
        if (person) MirrorInView = 0;
        if (!Quality.Mirrors || _probes == null || !Quality.Reflections) return null;
        Utils.LongToUInts(region.Source.Handle, out uint rx, out uint ry);
        var cam = center.Position;
        var fwd = Vector3.Normalize(new Vector3(-center.View.M13, -center.View.M23, -center.View.M33));
        float bestScore = float.MaxValue, keptScore = 0;
        ManualProbeObject? best = null, kept = null;
        Vector3 bestOffset = default, keptOffset = default;
        ulong bestRegion = 0;
        foreach (var scene in _world.Regions)
        {
            if (!scene.Source.HasSimulator) continue;
            Utils.LongToUInts(scene.Source.Handle, out uint ox, out uint oy);
            var offset = new Vector3((float)((long)ox - rx), (float)((long)oy - ry), 0);
            foreach (var m in scene.Mirrors())
            {
                if (!m.Params.Box) continue;
                var p = m.Position + offset;
                float dist = Vector3.Distance(cam, p);
                if (dist > center.DrawDistance) continue;
                bool visible = frustum.Sphere(p, m.Scale.Length());
                var n0 = Vector3.Transform(Vector3.UnitZ, m.Rotation);
                if (!visible || Vector3.Dot(n0, cam - p) <= 0.01f) continue;
                float off = dist > 1e-3f ? 1 - Vector3.Dot(fwd, (p - cam) / dist) : 0;   // 0 ahead, 1 at 90 degrees
                float score = dist * (1 + 2 * Math.Clamp(off, 0, 1));
                if (m.LocalId == held.Id && scene.Source.Handle == held.Region) (kept, keptOffset, keptScore) = (m, offset, score);
                if (score < bestScore) (best, bestOffset, bestScore, bestRegion) = (m, offset, score, scene.Source.Handle);
            }
        }
        if (kept is { } k && best is { } b0 && b0.LocalId != k.LocalId && bestScore > keptScore * 0.9f)
            (best, bestOffset, bestRegion) = (k, keptOffset, held.Region);
        held = best is { } h0 ? (h0.LocalId, bestRegion) : (0u, 0UL);
        if (person && (best?.LocalId ?? 0) != _loggedMirror)
        {
            _loggedMirror = best?.LocalId ?? 0;
            _opt.Log?.Invoke(best is { } b ? $"mirror: {b.LocalId} at {b.Position}, {((b.Params.Flags & ReflectionProbeParams.FlagDynamic) != 0 ? "dynamic" : "static")}" : "mirror: none in view");
        }
        if (best is not { } hero) return null;
        var pos = hero.Position + bestOffset;
        var n = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, hero.Rotation));
        var plane = new Vector4(n, -Vector3.Dot(n, pos));
        var box = Matrix4x4.CreateScale(hero.Scale * 0.5f) * Matrix4x4.CreateFromQuaternion(hero.Rotation) * Matrix4x4.CreateTranslation(pos);
        Matrix4x4.Invert(box, out var toUnit);
        if (person) MirrorInView = hero.LocalId;
        return new MirrorFrame
        {
            Plane = plane, ToUnitBox = toUnit, Center = pos, Scale = hero.Scale, Rotation = hero.Rotation,
            Dynamic = (hero.Params.Flags & ReflectionProbeParams.FlagDynamic) != 0,
            Portal = Portal(Reflect(cam, plane), pos, hero.Rotation, hero.Scale, plane),
        };
    }

    static Vector3 Reflect(Vector3 p, Vector4 plane) =>
        p - 2 * (Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), p) + plane.W) * new Vector3(plane.X, plane.Y, plane.Z);

    /// <summary>Reflection across a plane, as a row-vector matrix (p' = p - 2 (n.p + d) n).</summary>
    static Matrix4x4 Reflection(Vector4 plane)
    {
        float a = plane.X, b = plane.Y, c = plane.Z, d = plane.W;
        return new Matrix4x4(
            1 - 2 * a * a, -2 * a * b, -2 * a * c, 0,
            -2 * a * b, 1 - 2 * b * b, -2 * b * c, 0,
            -2 * a * c, -2 * b * c, 1 - 2 * c * c, 0,
            -2 * d * a, -2 * d * b, -2 * d * c, 1);
    }

    /// <summary>What the mirror can show: the volume from the mirrored camera through the probe box's outline in the
    /// plane (padded for the eyes' offsets), in front of the plane.</summary>
    static Frustum Portal(Vector3 apex, Vector3 center, Quaternion rot, Vector3 scale, Vector4 plane)
    {
        var x = Vector3.Transform(Vector3.UnitX, rot) * (scale.X * 0.5f + 0.1f);
        var y = Vector3.Transform(Vector3.UnitY, rot) * (scale.Y * 0.5f + 0.1f);
        Span<Vector3> c = stackalloc Vector3[] { center - x - y, center + x - y, center + x + y, center - x + y };
        var planes = new Vector4[5];
        for (int i = 0; i < 4; i++)
        {
            var nrm = Vector3.Cross(c[i] - apex, c[(i + 1) % 4] - apex);
            if (nrm.LengthSquared() < 1e-12f) return Frustum.FromPlanes(new[] { plane });
            nrm = Vector3.Normalize(nrm);
            if (Vector3.Dot(nrm, center - apex) < 0) nrm = -nrm;
            planes[i] = new Vector4(nrm, -Vector3.Dot(nrm, apex));
        }
        planes[4] = plane;
        return Frustum.FromPlanes(planes);
    }

    /// <summary>Each eye's reflection view, once shadows are shared (their skies take the centre view's maps), and the
    /// eyes' skies told where to find it. An eye the mirror doesn't cover gets none. A still's views or the camera
    /// preview (<paramref name="image"/> theirs) take their reflection at their own size, through their own targets
    /// (<paramref name="targets"/>, or the shared set of their size): it's rendered and copied out before the view
    /// itself draws there.</summary>
    void FinishMirror(MirrorFrame m, ViewSetup center, ViewSetup[] eyes, int image = -1, RenderTargets? targets = null)
    {
        var refl = Reflection(m.Plane);
        var flipX = Matrix4x4.CreateScale(-1, 1, 1);
        var views = new MirrorView?[eyes.Length];
        uint w = eyes.Max(e => e.Width), h = eyes.Max(e => e.Height);
        if (image < 0)
        {
            // the eyes: one size for both (each its own image), in steps of 128 pixels, so the headset's resolution
            // following the GPU doesn't remake the targets and image each time it moves (the projection, not the
            // pixel count, decides what a texel shows: no distortion)
            float scale = Math.Clamp(Quality.MirrorScale, 0.25f, 1f);
            static uint Round(float x) => Math.Max(128u, (uint)MathF.Ceiling(x / 128f) * 128u);
            (w, h) = (Round(w * scale), Round(h * scale));
        }
        var slots = new int[eyes.Length];
        for (int i = 0; i < eyes.Length; i++)
        {
            var e = eyes[i];
            if (Area(m, e, w, h) is not { } area) continue;
            int index = image >= 0 ? image : Math.Min(i, 1);
            slots[i] = _gpu.Renderer.EnsureMirrorImage(w, h, index);
            // the eye mirrored in the plane, its x turned round so it stays a rotation (the image comes out flipped:
            // a pixel at u finds its reflection at 1 - u); the same projection with left and right swapped
            var v = e.CloneForEye();
            v.ShadowProjectors = e.ShadowProjectors;
            v.Width = w; v.Height = h;
            v.View = refl * e.View * flipX;
            v.Proj = flipX * e.Proj * flipX;
            v.Position = Reflect(e.Position, m.Plane);
            v.ClipPlane = m.Plane;
            v.Mask = ViewMask.Mirror;
            v.LodView = center;
            v.Sky = GpuSky.Build(center.SkyU, center.WaterU, v.View, center.WaterHeight, v.Position);
            views[i] = new MirrorView { View = v, Targets = targets ?? _gpu.Targets(w, h), Area = area, Particles = m.Dynamic, Image = index };
        }
        var live = views.Where(x => x != null).Select(x => x!.View).ToArray();
        ShareShadows(center, live);
        foreach (var v in live)
        {
            v.Sky.ssao_on = 0;   // cube snapshots skip SSAO (renderDeferredLighting)
            v.Sky.hero_on = 0;
        }
        for (int i = 0; i < eyes.Length; i++)
        {
            ref var s = ref eyes[i].Sky;
            s.hero_on = views[i] != null ? 1 : 0;
            if (views[i] == null) continue;
            s.hero_tex = slots[i];
            s.hero_box = m.ToUnitBox;
            s.hero_plane = m.Plane;
            s.hero_mips = MathF.Log2(Math.Min(w, h));
        }
        m.Views = views;
    }

    /// <summary>The pixels of an eye's reflection image the mirror's box can cover (its corners projected, the image's
    /// x flipped, padded for the mips), or null when it's off screen. A corner behind the eye takes the whole image.</summary>
    static Rect2D? Area(MirrorFrame m, ViewSetup eye, uint w, uint h)
    {
        var vp = eye.View * eye.Proj;
        float u0 = 1, u1 = 0, v0 = 1, v1 = 0;
        var half = m.Scale * 0.5f;
        for (int k = 0; k < 8; k++)
        {
            var local = new Vector3((k & 1) != 0 ? half.X : -half.X, (k & 2) != 0 ? half.Y : -half.Y, (k & 4) != 0 ? half.Z : -half.Z);
            var c = Vector4.Transform(new Vector4(m.Center + Vector3.Transform(local, m.Rotation), 1), vp);
            if (c.W <= 1e-3f) return new Rect2D(new Offset2D(0, 0), new Extent2D(w, h));
            float u = (c.X / c.W + 1) * 0.5f, v = (c.Y / c.W + 1) * 0.5f;
            (u0, u1, v0, v1) = (MathF.Min(u0, u), MathF.Max(u1, u), MathF.Min(v0, v), MathF.Max(v1, v));
        }
        if (u1 < 0 || u0 > 1 || v1 < 0 || v0 > 1) return null;
        // a speck in the distance isn't worth a reflection's fixed cost (the whole draw list again)
        if ((Math.Min(u1, 1) - Math.Max(u0, 0)) * w * (Math.Min(v1, 1) - Math.Max(v0, 0)) * h < 256) return null;
        const int pad = 16;
        int x0 = Math.Clamp((int)MathF.Floor((1 - u1) * w) - pad, 0, (int)w), x1 = Math.Clamp((int)MathF.Ceiling((1 - u0) * w) + pad, 0, (int)w);
        int y0 = Math.Clamp((int)MathF.Floor(v0 * h) - pad, 0, (int)h), y1 = Math.Clamp((int)MathF.Ceiling(v1 * h) + pad, 0, (int)h);
        if (x1 <= x0 || y1 <= y0) return null;
        return new Rect2D(new Offset2D(x0, y0), new Extent2D((uint)(x1 - x0), (uint)(y1 - y0)));
    }

    /// <summary>A scene's hooks with this frame's mirror's part (worn objects' flags, the own body's mirror copies), as
    /// the avatars were told it (the eyes' mirror, else the preview's).</summary>
    SceneHooks MirrorHooks(SceneHooks hooks, ulong handle)
    {
        var mode = _world.Avatars.Mirror;
        uint self = mode == MirrorAvatars.Shown ? _world.Avatars.MirrorSelfIn(handle) : 0;
        return hooks with
        {
            AvatarFlags = mode == MirrorAvatars.Hidden ? InstanceFlags.MainOnly : 0,
            MirrorSelf = self,
            MirrorPalette = self != 0 ? (av, shape, list) => _world.Avatars.MirrorPalette(handle, av, shape, list) : null,
            MirrorAttachmentFrame = self != 0 ? (av, point) => _world.Avatars.MirrorAttachmentFrame(handle, av, point) : null,
        };
    }
}
