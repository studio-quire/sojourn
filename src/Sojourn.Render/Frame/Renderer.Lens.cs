using System.Numerics;
using System.Runtime.InteropServices;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

// The lenses' hidden area (XR_KHR_visibility_mask): pixels of an eye's image the person never sees. Per eye, a mask of
// them (R8) is drawn from the runtime's hidden-triangle mesh whenever the mesh, the eye's size or its projection
// changes, and the per-pixel passes skip what it marks (lensHidden() in common.glsl: the sun-light map and its blur, sun
// and ambient, local lights, projectors, haze, fullbright, glow, blended faces, particles, water). It's drawn so that
// nothing the person sees changes:
// - the mesh is scaled out from the eye's axis until its edge is LensMargin pixels inside the hidden area, so every
//   visible pixel's neighbours (the light map's blur, SMAA, CAS, glow) are still lit;
// - the G-buffer is drawn whole (SSAO samples depth up to hundreds of pixels away);
// - the part of the image the luminance pass samples for dynamic exposure, and the glow's reach around it, is never
//   marked (luminance.frag reads the middle 60% of the image, nudged down);
// - frames that save a picture or video (the eye's whole image) don't use it.
// Skipped pixels end black (the sun and ambient pass clears the scene). Written by Claude Opus 5.5, 2026-10-27.
public sealed unsafe partial class Renderer
{
    /// <summary>How far inside the hidden area the mask's edge stays, in pixels: more than the post passes and the
    /// light map's blur reach (the glow's ~50 px is the farthest).</summary>
    public const float LensMargin = 96;
    /// <summary>SOJOURN_LENS_MASK=0 renders the hidden area like the rest (for comparing).</summary>
    public static bool LensMask = System.Environment.GetEnvironmentVariable("SOJOURN_LENS_MASK") != "0";

    Pipeline _lensMask;

    void CreateLens() => _lensMask = Make(new GraphicsPipelineDesc
    {
        Vertex = _shaders.Get("lensmask.vert", ShaderStage.Vertex), Fragment = _shaders.Get("lensmask.frag", ShaderStage.Fragment),
        ColorFormats = new[] { RenderTargets.LensMaskFormat }, DepthTest = false, DepthWrite = false, Cull = CullModeFlags.None,
        VertexInput = false, Layout = Layout,
    });

    /// <summary>Whether this eye renders with a lens mask this frame (the mask image made, to be drawn if stale).</summary>
    bool UsesLens(ViewSetup v, RenderTargets rt)
    {
        if (!LensMask || v.LensHidden is not { Length: >= 3 } mesh || !Plausible(mesh, v.Proj)) return false;
        rt.EnsureLensMask();
        return true;
    }

    /// <summary>Where the renderer's notes go (FrameRenderer sets it).</summary>
    public Action<string>? Log;
    readonly Dictionary<Vector2[], (Matrix4x4 Proj, bool Ok)> _plausible = new(ReferenceEqualityComparer.Instance);

    /// <summary>A guard against a mesh in other coordinates than the eye's z = -1 plane (which could black out what the
    /// person sees): a hidden area covers the image's edges, so a mesh over any of its middle half, or over more than 45%
    /// of it, isn't used. Checked once a mesh and projection, on a 64 x 64 grid.</summary>
    bool Plausible(Vector2[] mesh, Matrix4x4 proj)
    {
        if (_plausible.TryGetValue(mesh, out var known) && known.Proj == proj) return known.Ok;
        if (_plausible.Count > 16) _plausible.Clear();
        int covered = 0, central = 0;
        const int n = 64;
        for (int yi = 0; yi < n; yi++)
            for (int xi = 0; xi < n; xi++)
            {
                float nx = (xi + 0.5f) / n * 2 - 1, ny = (yi + 0.5f) / n * 2 - 1;
                // NDC back to the z = -1 plane (clip = (x, y, -1, 1) * Proj, w = 1)
                var t = new Vector2((nx + proj.M31) / proj.M11, (ny + proj.M32) / proj.M22);
                if (!InsideAny(mesh, t)) continue;
                covered++;
                if (MathF.Abs(nx) < 0.5f && MathF.Abs(ny) < 0.5f) central++;
            }
        bool ok = central == 0 && covered <= 0.45f * n * n;
        _plausible[mesh] = (proj, ok);
        Log?.Invoke(ok ? $"lens mask: the hidden area covers {100f * covered / (n * n):F0}% of an eye"
                       : $"lens mask: not used, the runtime's hidden area doesn't look like one ({100f * covered / (n * n):F0}% of the eye, {central} of its middle's samples)");
        return ok;
    }

    static bool InsideAny(Vector2[] tris, Vector2 p)
    {
        for (int i = 0; i + 2 < tris.Length; i += 3)
        {
            Vector2 a = tris[i], b = tris[i + 1], c = tris[i + 2];
            float d1 = Cross(p - a, b - a), d2 = Cross(p - b, c - b), d3 = Cross(p - c, a - c);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            if (!(neg && pos)) return true;
        }
        return false;
        static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;
    }

    static bool LensStale(ViewSetup v, RenderTargets rt) => !ReferenceEquals(rt.LensMesh, v.LensHidden) || rt.LensProj != v.Proj;

    static ulong LensArenaBytes(IReadOnlyList<Eye> eyes) =>
        (ulong)eyes.Sum(e => (e.View.LensHidden?.Length ?? 0) * sizeof(Vector2) + 256);

    readonly record struct LensDraw(int Eye, ulong Vertices, uint Count, float Scale, Vector4 Keep);
    readonly List<LensDraw> _lensDraws = new();

    /// <summary>The eyes' stale lens masks' vertices into the arena (before it's flushed to the GPU).</summary>
    void PrepareLensMasks(FrameArena arena, IReadOnlyList<Eye> eyes)
    {
        _lensDraws.Clear();
        for (int i = 0; i < eyes.Count; i++)
        {
            var (v, rt) = eyes[i];
            if (!UsesLens(v, rt) || !LensStale(v, rt)) continue;
            var mesh = v.LensHidden!;
            // scaled out from the eye's axis: the mesh's nearest vertex to it moves LensMargin pixels outward
            float rMin = float.MaxValue;
            foreach (var p in mesh) rMin = MathF.Min(rMin, p.Length());
            // pixels per unit of tangent at the image (the projection's x and y scales are 2 / tangent span)
            float ppt = MathF.Min(v.Proj.M11 * rt.Width, MathF.Abs(v.Proj.M22) * rt.Height) * 0.5f;
            float k = 1f + LensMargin / MathF.Max(rMin * ppt, 1f);
            ulong verts = arena.Device.Address + arena.Write<Vector2>(mesh);
            // never marked: the luminance pass's window (luminance.frag: tc * 0.6 + 0.2, v + 0.1) and the margin around it
            var keep = new Vector4(0.2f * rt.Width - LensMargin, 0.3f * rt.Height - LensMargin, 0.8f * rt.Width + LensMargin, 0.9f * rt.Height + LensMargin);
            _lensDraws.Add(new LensDraw(i, verts, (uint)(mesh.Length / 3 * 3), k, keep));
        }
    }

    /// <summary>Draw them (once the arena is on the GPU, before any pass reads them).</summary>
    void RecordLensMasks(CommandBuffer cmd, IReadOnlyList<Eye> eyes, ViewData[] vds)
    {
        foreach (var d in _lensDraws)
        {
            var (v, rt) = eyes[d.Eye];
            var img = rt.LensMaskImage!;
            img.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
            BeginRendering(cmd, rt.Width, rt.Height, new[] { img }, null, clearColor: true, clearDepth: false);
            _push = default;
            _push.View = vds[d.Eye].View;
            _push.Instances = d.Vertices;
            _push.V0 = new Vector4(d.Scale, 0, 0, 0);
            _push.V1 = d.Keep;
            Bind(cmd, _lensMask);
            Push(cmd);
            _vk.Api.CmdDraw(cmd, d.Count, 1, 0, 0);
            _vk.Api.CmdEndRendering(cmd);
            img.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
            rt.LensMesh = v.LensHidden;
            rt.LensProj = v.Proj;
        }
        _lensDraws.Clear();
    }

    void DisposeLens() => _vk.Api.DestroyPipeline(_vk.Device, _lensMask, null);
}
