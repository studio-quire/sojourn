using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;
using Sojourn.Render.World;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Realtime;

/// <summary>
/// The person's HUDs in a frame (Realtime/Hud.cs): one draw list in HUD space, drawn once into the screen image (and a
/// shot when the assistant asked for one) at the start of the first eye's present, then laid over each eye (the desktop,
/// or the headset preview). The headset's layer copies the screen image (<see cref="HudImage"/>).
/// </summary>
public sealed partial class FrameRenderer
{
    readonly DrawList[] _hudLists = { new(), new(), new() };
    GpuImage? _hudColor, _hudDepth;
    int _hudSlot = -1;

    /// <summary>A HUD label's picture on the GPU, kept while requests carry it (keyed by the picture object).</summary>
    sealed class LabelTex
    {
        public required GpuImage Image;
        public required int Slot;
        public long Version = long.MinValue;
        public long Used;
    }
    readonly Dictionary<OverlayImage, LabelTex> _hudLabels = new(ReferenceEqualityComparer.Instance);
    long _hudFrame;

    /// <summary>The HUDs the last frame placed (its request's region and aspect); empty when it had none. Any thread.</summary>
    public HudsPlaced Huds { get; private set; } = HudsPlaced.None;

    /// <summary>The screen image the last frame drew (shader-readable, or wherever its last user left it), for the
    /// headset's layer; null when it drew none.</summary>
    public GpuImage? HudImage { get; private set; }

    sealed class HudPlan
    {
        public required HudRequest Req;
        public required DrawList List;
        public readonly List<(ViewSetup View, GpuImage Color, GpuImage Depth)> Views = new();
        public Renderer.FrameDraws? Draws;
        public Renderer.ViewData[] Data = Array.Empty<Renderer.ViewData>();
        public ulong[] Corners = Array.Empty<ulong>();
        public GpuImage? ShotColor, ShotDepth;
        public bool Screen;
        public readonly List<(LabelTex Tex, HudLabel Label)> Labels = new();
        /// <summary>Per view, per label: the device address of its four clip corners.</summary>
        public ulong[][] LabelCorners = Array.Empty<ulong[]>();
        public ulong Bytes => Renderer.ArenaBytes(List, Views.Count) + 1024 + (ulong)(Labels.Count * Views.Count * 80);
    }

    /// <summary>The HUDs' draw list and views for this frame, or null when there's nothing to draw.</summary>
    HudPlan? PlanHud(FrameRequest req, int slot)
    {
        HudImage = null;
        if (req.Hud is not { } h || _world.Region(h.Region) is not { } scene || !scene.Source.HasSimulator)
        {
            Huds = HudsPlaced.None;
            return null;
        }
        float aspect = h.Aspect;
        (Vector3, Quaternion)? Point(int p) => Hud.PointFrame(p, aspect);
        var objects = scene.HudObjects(h.Self, Point);
        Huds = new HudsPlaced(h.Region, aspect, objects);
        if (objects.Count == 0) return null;
        bool screen = h.Width > 0 && h.Height > 0;
        if (!screen && h.Shot == null) return null;

        var dl = _hudLists[slot % _hudLists.Length];
        dl.Clear();
        float px = Math.Max(screen ? h.Height : 0, h.Shot is { } s ? s.Height / (2 * s.HalfHeight) : 0);
        scene.BuildHudDrawList(new ViewSetup { Position = new Vector3(-1, 0, 0), DrawDistance = 1e6f, HasWater = false, PlanarSort = true }, dl, h.Self, Point, px,
            View with { MinPixels = 0, MinShadowPixels = 0 });
        float minX = objects.Min(o => o.Min.X), maxX = objects.Max(o => o.Max.X);

        var plan = new HudPlan { Req = h, List = dl, Screen = screen };
        if (screen)
        {
            var format = Renderer.HudFormat(h.Linear);
            if (_hudColor == null || _hudColor.Width != h.Width || _hudColor.Height != h.Height || _hudColor.Format != format)
            {
                var (oldColor, oldDepth, oldSlot) = (_hudColor, _hudDepth, _hudSlot);
                if (oldColor != null) _world.RetireLater(() => { oldColor.Dispose(); oldDepth?.Dispose(); if (oldSlot >= 0) _gpu.Bindless.FreeTexture(oldSlot); });
                _hudColor = new GpuImage(_gpu.Vk, (uint)h.Width, (uint)h.Height, format,
                    ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                _hudDepth = new GpuImage(_gpu.Vk, (uint)h.Width, (uint)h.Height, RenderTargets.DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit);
                _hudSlot = _gpu.Bindless.AllocTexture();
                _gpu.Bindless.Write(_hudSlot, _hudColor.View, _gpu.Bindless.ClampLinearNoMip);
            }
            plan.Views.Add((Hud.View(Vector2.Zero, 0.5f, h.Width, h.Height, minX, maxX), _hudColor, _hudDepth!));
            HudImage = _hudColor;
        }
        if (h.Shot is { } shot)
        {
            plan.ShotColor = new GpuImage(_gpu.Vk, (uint)shot.Width, (uint)shot.Height, Renderer.HudFormat(false),
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit);
            plan.ShotDepth = new GpuImage(_gpu.Vk, (uint)shot.Width, (uint)shot.Height, RenderTargets.DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit);
            plan.Views.Add((Hud.View(shot.Center, shot.HalfHeight, shot.Width, shot.Height, minX, maxX), plan.ShotColor, plan.ShotDepth));
        }
        PlanLabels(plan, h);
        return plan;
    }

    /// <summary>The request's labels as textures: new pictures get an image and a slot, ones no longer asked for go a
    /// few frames on (a frame in flight may still sample them).</summary>
    void PlanLabels(HudPlan plan, HudRequest h)
    {
        _hudFrame++;
        foreach (var label in h.Labels ?? Array.Empty<HudLabel>())
        {
            var pic = label.Picture;
            if (pic.Width <= 0 || pic.Height <= 0 || pic.Rgba.Length < pic.Width * pic.Height * 4) continue;
            if (!_hudLabels.TryGetValue(pic, out var tex) || tex.Image.Width != pic.Width || tex.Image.Height != pic.Height)
            {
                if (tex != null) RetireLabel(tex);
                tex = new LabelTex
                {
                    Image = new GpuImage(_gpu.Vk, (uint)pic.Width, (uint)pic.Height, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit),
                    Slot = _gpu.Bindless.AllocTexture(),
                };
                _gpu.Bindless.Write(tex.Slot, tex.Image.View, _gpu.Bindless.ClampLinearNoMip);
                _hudLabels[pic] = tex;
            }
            tex.Used = _hudFrame;
            plan.Labels.Add((tex, label));
        }
        if (_hudLabels.Count > plan.Labels.Count)
            foreach (var (pic, tex) in _hudLabels.Where(kv => kv.Value.Used < _hudFrame - 4).ToList())
            {
                _hudLabels.Remove(pic);
                RetireLabel(tex);
            }
    }

    void RetireLabel(LabelTex tex)
    {
        var (img, slot) = (tex.Image, tex.Slot);
        _world.RetireLater(() => { img.Dispose(); _gpu.Bindless.FreeTexture(slot); });
    }

    /// <summary>The plan's draws, views and each eye's quad into the frame arena (before it's flushed).</summary>
    void PrepareHud(FrameArena arena, HudPlan plan, ViewSetup[] eyes)
    {
        var r = _gpu.Renderer;
        plan.Draws = r.Upload(arena, plan.List);
        plan.Data = plan.Views.Select(v => r.WriteView(arena, v.View, v.View.Width, v.View.Height)).ToArray();
        plan.LabelCorners = new ulong[plan.Views.Count][];
        Span<Vector4> q = stackalloc Vector4[4];
        for (int i = 0; i < plan.Views.Count; i++)
        {
            var vp = plan.Views[i].View.View * plan.Views[i].View.Proj;
            var addr = plan.LabelCorners[i] = new ulong[plan.Labels.Count];
            for (int j = 0; j < plan.Labels.Count; j++)
            {
                var (mid, half) = (plan.Labels[j].Label.Center, plan.Labels[j].Label.Size / 2);
                // HUD space: y to the left, z up; corners top left, top right, bottom right, bottom left
                q[0] = Vector4.Transform(new Vector4(0, mid.X + half.X, mid.Y + half.Y, 1), vp);
                q[1] = Vector4.Transform(new Vector4(0, mid.X - half.X, mid.Y + half.Y, 1), vp);
                q[2] = Vector4.Transform(new Vector4(0, mid.X - half.X, mid.Y - half.Y, 1), vp);
                q[3] = Vector4.Transform(new Vector4(0, mid.X + half.X, mid.Y - half.Y, 1), vp);
                addr[j] = arena.Device.Address + arena.Write<Vector4>(q);
            }
        }
        if (!plan.Screen) return;
        plan.Corners = new ulong[eyes.Length];
        Span<Vector4> c = stackalloc Vector4[4];
        for (int i = 0; i < eyes.Length; i++)
        {
            if (plan.Req.Corners is { Length: 4 } k)
            {
                var vp = eyes[i].View * eyes[i].Proj;
                for (int j = 0; j < 4; j++) c[j] = Vector4.Transform(new Vector4(k[j], 1), vp);
                if (c[0].W <= 0.02f || c[1].W <= 0.02f || c[2].W <= 0.02f || c[3].W <= 0.02f) continue;   // behind the eye
            }
            else if (plan.Req.OverEyes)
            {
                c[0] = new(-1, -1, 0, 1); c[1] = new(1, -1, 0, 1); c[2] = new(1, 1, 0, 1); c[3] = new(-1, 1, 0, 1);
            }
            else continue;
            plan.Corners[i] = arena.Device.Address + arena.Write<Vector4>(c);
        }
    }

    /// <summary>In an eye's present: the first draws the plan's views (and reads the shot back), then each eye gets the
    /// screen image laid over it where the request wants it.</summary>
    void RecordHud(FrameResources fr, CommandBuffer cmd, GpuImage target, int eye, HudPlan plan)
    {
        var r = _gpu.Renderer;
        if (eye == 0 && plan.Draws is { } draws)
        {
            UploadLabels(fr, cmd, plan);
            var slots = plan.Labels.Select(l => l.Tex.Slot).ToArray();
            for (int i = 0; i < plan.Views.Count; i++)
            {
                var (_, color, depth) = plan.Views[i];
                r.RecordHud(cmd, draws, plan.Data[i], color, depth);
                if (slots.Length > 0) r.RecordHudLabels(cmd, color, slots, plan.LabelCorners[i]);
            }
            if (plan.Screen) _hudColor!.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
            if (plan.ShotColor is { } shot && plan.Req.Shot is { } want) ReadShot(fr, cmd, shot, plan.ShotDepth!, want);
        }
        if (plan.Screen && eye < plan.Corners.Length && plan.Corners[eye] != 0)
        {
            _hudColor!.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
            r.RecordHudCompose(cmd, target, _hudSlot, plan.Req.Linear, plan.Corners[eye]);
        }
    }

    /// <summary>Copy the labels whose pictures changed into their textures.</summary>
    void UploadLabels(FrameResources fr, CommandBuffer cmd, HudPlan plan)
    {
        foreach (var (tex, label) in plan.Labels)
        {
            var pic = label.Picture;
            if (tex.Version == pic.Version) continue;
            int bytes = pic.Width * pic.Height * 4;
            var st = new GpuBuffer(_gpu.Vk, (ulong)bytes, BufferUsageFlags.TransferSrcBit, MemoryUse.Upload);
            pic.Rgba.AsSpan(0, bytes).CopyTo(st.Mapped);
            tex.Image.Transition(cmd, ImageLayout.TransferDstOptimal);
            var copy = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D((uint)pic.Width, (uint)pic.Height, 1),
            };
            _gpu.Vk.Api.CmdCopyBufferToImage(cmd, st.Handle, tex.Image.Handle, ImageLayout.TransferDstOptimal, 1, in copy);
            tex.Image.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
            tex.Version = pic.Version;
            fr.Retire.Add(st.Dispose);
        }
    }

    unsafe void ReadShot(FrameResources fr, CommandBuffer cmd, GpuImage img, GpuImage depth, HudShot want)
    {
        var api = _gpu.Vk.Api;
        img.Transition(cmd, ImageLayout.TransferSrcOptimal);
        ulong bytes = (ulong)img.Width * img.Height * 4;
        var rb = new GpuBuffer(_gpu.Vk, bytes, BufferUsageFlags.TransferDstBit, MemoryUse.Readback);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(img.Width, img.Height, 1),
        };
        api.CmdCopyImageToBuffer(cmd, img.Handle, ImageLayout.TransferSrcOptimal, rb.Handle, 1, in copy);
        var hb = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit, DstAccessMask = AccessFlags2.HostReadBit,
        };
        var di = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &hb };
        api.CmdPipelineBarrier2(cmd, in di);
        int w = (int)img.Width, h = (int)img.Height;
        fr.Retire.Add(() =>
        {
            var pixels = new byte[w * h * 4];
            new ReadOnlySpan<byte>((void*)rb.Alloc.Mapped, pixels.Length).CopyTo(pixels);
            rb.Dispose();
            img.Dispose();
            depth.Dispose();
            _ = Task.Run(() => want.Done(pixels, w, h));
        });
    }

    /// <summary>The nearest of the person's HUD prims under a point of the HUD screen (y, z in HUD space), as the
    /// viewer's pick: a ray along +x. Render thread, between frames.</summary>
    public PickHit? HudPick(ulong region, uint self, float aspect, Vector2 at, Func<LibreMetaverse.Primitive, bool>? only = null)
    {
        CheckWorld();
        if (_world.Region(region) is not { } scene) return null;
        (Vector3, Quaternion)? Point(int p) => Hud.PointFrame(p, aspect);
        return scene.HudPick(self, Point, new Vector3(-1000, at.X, at.Y), Vector3.UnitX, 2000, only);
    }

    void DisposeHud()
    {
        _hudColor?.Dispose();
        _hudDepth?.Dispose();
        foreach (var t in _hudLabels.Values) t.Image.Dispose();
        _hudLabels.Clear();
    }
}
