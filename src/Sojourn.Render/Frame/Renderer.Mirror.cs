using System.Numerics;
using System.Runtime.InteropServices;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

/// <summary>One eye's reflection in a mirror (Realtime/FrameRenderer.Mirror.cs): the view from the eye mirrored in the
/// mirror's plane, the targets it renders through, and the part of them the mirror can show (pixels).</summary>
public sealed class MirrorView
{
    public required ViewSetup View;
    public required RenderTargets Targets;
    public Rect2D Area;
    /// <summary>A dynamic mirror shows particles (the viewer's cube snapshots of dynamic probes take them).</summary>
    public bool Particles;
    /// <summary>The reflection image it goes into: the eye's (0, 1), a still's or the camera preview's.</summary>
    public int Image;
}

/// <summary>A mirror view's blended faces, avatar alpha and particles in the order its mirrored camera sees them.</summary>
sealed class MirrorDraws
{
    public List<Renderer.IndirectBatch>? Alpha;
    public List<AvatarAlphaCmd>? AvatarAlpha;
    public List<Renderer.AlphaStep>? Steps;
    public ulong ParticleVertices;
    public readonly List<Particles.ParticleQuad> Quads = new();
}

/// <summary>
/// A mirror's reflection, recorded just before the eye that shows it: the frame's draw list rendered from the mirrored
/// eye as a cube snapshot face would be (the viewer's hero probe faces: no SSAO, no stars, particles only if dynamic; clipped at the
/// mirror's plane in the vertex shaders), only over the mirror's pixels, its blended faces sorted again from the mirrored
/// eye; then copied with a few mips into the image the eye's lighting samples (probes.glsl tapHeroProbe). An image an
/// eye: with multiview both eyes' reflections are rendered before the eyes, whose alpha pass samples both at once. A
/// still's views and the camera preview take theirs the same way, into images of their own.
/// </summary>
public sealed unsafe partial class Renderer
{
    /// <summary>The reflection images beyond the eyes' two: a still's (made for it and let go after) and the camera
    /// preview's (kept while the camera is out).</summary>
    public const int StillMirror = 2, PreviewMirror = 3;
    readonly GpuImage?[] _mirrorImages = new GpuImage?[4];
    readonly int[] _mirrorSlots = { -1, -1, -1, -1 };
    bool _mirrorPass;
    Rect2D? _area;
    readonly MirrorDraws _eyesMirrorDraws = new(), _previewMirrorDraws = new();
    MirrorDraws _md = null!;   // the mirror pass's, while it records

    /// <summary>Mip levels of the reflection image: the viewer only blends the hero probe into surfaces of gloss 0.75 and
    /// up, which sample its first quarter of mips.</summary>
    const uint MirrorMips = 5;

    /// <summary>A reflection image (an eye's, a still's, the preview's) at this size (made or remade on demand); its bindless slot.</summary>
    public int EnsureMirrorImage(uint width, uint height, int index)
    {
        if (_mirrorImages[index] is { } m && m.Width == width && m.Height == height) return _mirrorSlots[index];
        if (_mirrorImages[index] != null) _vk.WaitIdle();
        _mirrorImages[index]?.Dispose();
        var img = _mirrorImages[index] = new GpuImage(_vk, width, height, RenderTargets.HdrFormat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit, mips: MirrorMips, dedicated: true);
        if (_mirrorSlots[index] < 0) _mirrorSlots[index] = _bindless.AllocTexture();
        _bindless.Write(_mirrorSlots[index], img.View, _bindless.Clamp);
        return _mirrorSlots[index];
    }

    /// <summary>Let a reflection image go (its slot kept for the next); the GPU must be done with it.</summary>
    public void ReleaseMirrorImage(int index)
    {
        _mirrorImages[index]?.Dispose();
        _mirrorImages[index] = null;
    }

    public ulong MirrorBytes => _mirrorImages.Aggregate(0UL, (s, m) => s + (m?.Bytes ?? 0));

    /// <summary>The arena the mirror's blended faces need (their indirect commands, sorted again) and its views.</summary>
    public static ulong MirrorArenaBytes(DrawList dl, int views) =>
        (ulong)(dl.Alpha.Count * sizeof(DrawIndexedIndirect) + views * (sizeof(GpuView) + sizeof(GpuSky) + 1024) + 1024
                + (dl.ParticleQuads.Count > 0 ? dl.ParticleVertices.Count * sizeof(Particles.ParticleVertex) + dl.Alpha.Count * sizeof(DrawIndexedIndirect) + 512 : 0));

    /// <summary>The mirror views' data in the frame arena (by eye; empty where an eye has none), and the mirror's
    /// draw order.</summary>
    ViewData[] WriteMirrorViews(FrameArena arena, DrawList dl, IReadOnlyList<MirrorView?> mirrors)
    {
        var vds = new ViewData[mirrors.Count];
        MirrorView? first = null;
        for (int i = 0; i < mirrors.Count; i++)
            if (mirrors[i] is { } m)
            {
                vds[i] = WriteView(arena, m.View, m.Targets.Width, m.Targets.Height);
                first ??= m;
            }
        if (first != null) PrepareMirrorDraws(arena, dl, first.View, first.Particles, _eyesMirrorDraws);
        return vds;
    }

    /// <summary>The camera preview's mirror view in the frame arena, and its own draw order (its mirrored camera isn't
    /// the eyes').</summary>
    ViewData WritePreviewMirror(FrameArena arena, DrawList dl, MirrorView m)
    {
        var vd = WriteView(arena, m.View, m.Targets.Width, m.Targets.Height);
        PrepareMirrorDraws(arena, dl, m.View, m.Particles, _previewMirrorDraws);
        return vd;
    }

    /// <summary>Blended faces and avatar alpha in the order the mirrored eye sees them: far to near from where it stands
    /// (faces by their centres' distance, which the draws keep in Bounds), ties in the order drawn; a dynamic mirror's particles
    /// among them.</summary>
    void PrepareMirrorDraws(FrameArena arena, DrawList dl, ViewSetup mirror, bool particles, MirrorDraws md)
    {
        var cam = mirror.Position;
        var alpha = new List<DrawCmd>(dl.Alpha.Count);
        foreach (var d in dl.Alpha)
            alpha.Add(d with { Depth = Vector3.Distance(new Vector3(d.Bounds.X, d.Bounds.Y, d.Bounds.Z), cam), SubDepth = 0 });
        md.Alpha = BuildBatches(arena, alpha, sort: true, paths: dl);
        md.AvatarAlpha = dl.AvatarAlpha.Select((a, i) => (a, i))
            .OrderByDescending(x => Vector3.Distance(new Vector3(x.a.Cmd.Bounds.X, x.a.Cmd.Bounds.Y, x.a.Cmd.Bounds.Z), cam)).ThenBy(x => x.i)
            .Select(x => x.a).ToList();
        md.Steps = null;
        if (particles && dl.ParticleQuads.Count > 0)
        {
            MirrorParticles(dl, cam, md.Quads);
            md.ParticleVertices = arena.Device.Address + arena.Write<Particles.ParticleVertex>(CollectionsMarshal.AsSpan(_mirrorVerts));
            md.Steps = InterleaveAlpha(arena, dl, alpha, md.Quads);   // alpha was sorted far to near from the mirror above
        }
    }

    // a dynamic mirror's particles: the frame's quads turned to face the mirrored eye and sorted from it, interleaved with
    // its blended faces
    readonly List<Particles.ParticleVertex> _mirrorVerts = new();
    readonly List<int> _mirrorOrder = new();

    /// <summary>Each quad facing the mirrored eye as ParticleBuilder faces the eye (about the particle's centre, the
    /// world's up), keeping its size; ribbons and quads turned to their velocity stay as they are. Far to near.</summary>
    void MirrorParticles(DrawList dl, Vector3 cam, List<Particles.ParticleQuad> mirrorQuads)
    {
        var src = dl.ParticleVertices;
        var quads = dl.ParticleQuads;
        mirrorQuads.Clear();
        _mirrorVerts.Clear();
        _mirrorOrder.Clear();
        var depth = new float[quads.Count];
        for (int q = 0; q < quads.Count; q++)
        {
            var c = (src[4 * q].Pos + src[4 * q + 1].Pos + src[4 * q + 2].Pos + src[4 * q + 3].Pos) * 0.25f;
            depth[q] = Vector3.Distance(c, cam);
            _mirrorOrder.Add(q);
        }
        _mirrorOrder.Sort((a, b) => depth[b] != depth[a] ? depth[b].CompareTo(depth[a]) : a.CompareTo(b));
        foreach (int q in _mirrorOrder)
        {
            var v0 = src[4 * q]; var v1 = src[4 * q + 1]; var v2 = src[4 * q + 2]; var v3 = src[4 * q + 3];
            if (quads[q].Facing)
            {
                // corners: centre + up - right, - up - right, + up + right, - up + right
                var c = (v0.Pos + v3.Pos) * 0.5f;
                float halfRight = (v2.Pos - v0.Pos).Length() * 0.5f, halfUp = (v0.Pos - v1.Pos).Length() * 0.5f;
                var toPart = c - cam;
                var right = Vector3.Cross(toPart, Vector3.UnitZ);
                if (right.LengthSquared() < 1e-12f) right = Vector3.UnitX;
                right = Vector3.Normalize(right);
                var up = Vector3.Normalize(Vector3.Cross(right, toPart));
                right *= halfRight;
                up *= halfUp;
                v0.Pos = c + up - right; v1.Pos = c - up - right; v2.Pos = c + up + right; v3.Pos = c - up + right;
            }
            _mirrorVerts.Add(v0); _mirrorVerts.Add(v1); _mirrorVerts.Add(v2); _mirrorVerts.Add(v3);
            mirrorQuads.Add(quads[q] with { Depth = depth[q] });
        }
    }

    /// <summary>One view's reflection, into its reflection image.</summary>
    void RecordMirror(CommandBuffer cmd, FrameDraws draws, ViewData vd, MirrorView mv, MirrorDraws md)
    {
        var rt = mv.Targets;
        bool suspended = Profiler.Suspended;
        Profiler.Suspended = true;
        _mirrorPass = true;
        _md = md;
        _area = mv.Area;
        try { RecordScene(cmd, draws, vd, mv.View, rt, shadows: false, cubeSnapshot: true); }
        finally
        {
            _mirrorPass = false;
            _area = null;
            Profiler.Suspended = suspended;
        }
        CopyToMirrorImage(cmd, rt.Hdr, _mirrorImages[mv.Image]!);
        Profiler.Mark("mirror");
    }

    void CopyToMirrorImage(CommandBuffer cmd, GpuImage hdr, GpuImage img)
    {
        var api = _vk.Api;
        hdr.Transition(cmd, ImageLayout.TransferSrcOptimal);
        GpuImage.Barrier(_vk, cmd, img.Handle, img.Aspect, img.Layout, ImageLayout.TransferDstOptimal, 0, img.Mips, 0, 1);
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(img.Width, img.Height, 1),
        };
        api.CmdCopyImage(cmd, hdr.Handle, ImageLayout.TransferSrcOptimal, img.Handle, ImageLayout.TransferDstOptimal, 1, in region);
        int w = (int)img.Width, h = (int)img.Height;
        for (uint m = 1; m < img.Mips; m++)
        {
            GpuImage.Barrier(_vk, cmd, img.Handle, img.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, m - 1, 1, 0, 1);
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(img.Aspect, m - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(img.Aspect, m, 0, 1),
            };
            blit.SrcOffsets[1] = new Offset3D(w, h, 1);
            blit.DstOffsets[1] = new Offset3D(nw, nh, 1);
            api.CmdBlitImage(cmd, img.Handle, ImageLayout.TransferSrcOptimal, img.Handle, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
            w = nw; h = nh;
        }
        GpuImage.Barrier(_vk, cmd, img.Handle, img.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, img.Mips - 1, 1, 0, 1);
        GpuImage.Barrier(_vk, cmd, img.Handle, img.Aspect, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, 0, img.Mips, 0, 1);
        img.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    void DisposeMirror()
    {
        for (int i = 0; i < _mirrorImages.Length; i++)
        {
            _mirrorImages[i]?.Dispose();
            if (_mirrorSlots[i] >= 0) _bindless.FreeTexture(_mirrorSlots[i]);
        }
    }
}
