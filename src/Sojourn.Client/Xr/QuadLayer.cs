using System.Numerics;
using Silk.NET.OpenXR;
using Sojourn.Render.Gpu;
using Sojourn.Render.Realtime;
using VkImage = Silk.NET.Vulkan.Image;
using VkImageLayout = Silk.NET.Vulkan.ImageLayout;
using VkImageAspectFlags = Silk.NET.Vulkan.ImageAspectFlags;
using VkCommandBuffer = Silk.NET.Vulkan.CommandBuffer;
using Quaternion = System.Numerics.Quaternion;

namespace Sojourn.Client.Xr;

// OpenXR's Session, not the Sojourn.Session namespace (which a bare name in Sojourn.* finds first)
using Session = Silk.NET.OpenXR.Session;

/// <summary>
/// A picture shown as an OpenXR quad layer over the scene (the assistant's panel, the chat feed): its own swapchain,
/// which the compositor samples straight onto the lenses, so text stays sharper than it would drawn into the eye
/// images. A new picture (by its version) is copied in on the next frame; the layer keeps the last one meanwhile.
/// Fit: the swapchain is made as big as the pictures need (up to maxW x maxH), made again when one outgrows it, rather
/// than at the largest size from the start (the assistant's elements: several small layers).
/// Two threads: the main thread sets what the next frame shows (<see cref="Set"/>, <see cref="SetImage"/>: the pending
/// spec, which XrSession.Commit copies into that frame), the render thread shows a frame's (<see cref="Use"/>, then
/// <see cref="Record"/> and <see cref="Layer"/>).
/// For the PC's mirror window, while it shows the menus, the layer keeps a copy of its picture too (<see cref="Mirror"/>,
/// Render's LayerMirror): made from the same staging when a picture changes, the renderer's image each frame.
/// Pieces: where another menu passes through this one (MenuOrder), it goes in parts, each its own quad layer from a
/// swapchain of its own: the picture copied in and the other side of the line cleared (Render's LayerCut), the whole
/// layer not submitted meanwhile. A CPU picture is kept on the GPU for it too (the source), so the parts are made from
/// it each frame without uploading again; the parts' swapchains and the source go once unused for a while.
/// </summary>
sealed unsafe class QuadLayer(XR xr, VulkanContext vk, Session session, long format, string name, int maxW, int maxH, bool fit = false) : IDisposable
{
    int _cw, _ch;
    Swapchain _chain;
    VkImage[] _images = [];
    readonly GpuBuffer?[] _staging = new GpuBuffer?[2];
    long _shown = -1;   // the picture's version in the swapchain (0: the renderer's image), -1: none
    bool _held;
    volatile int _w, _h;
    MirrorPicture? _mirror;
    bool _mirrorFresh;   // the renderer's image: copied this frame

    /// <summary>What a layer shows in a frame: a picture, or an image the renderer draws (both null: hidden), where.</summary>
    public readonly record struct Spec(Sojourn.Render.Realtime.OverlayImage? Image, Func<GpuImage?>? Gpu, Posef Pose, float WidthM, bool GpuLayer)
    {
        public static Spec At(Sojourn.Render.Realtime.OverlayImage? image, Func<GpuImage?>? gpu, Vector3 position, Quaternion orientation,
            float widthMetres, bool gpuLayer) =>
            new(image, gpu, new Posef(new Quaternionf(orientation.X, orientation.Y, orientation.Z, orientation.W), new Vector3f(position.X, position.Y, position.Z)),
                widthMetres, gpuLayer);
    }

    Spec _pending;   // main thread: what the next frame is to show
    Spec _spec;      // render thread: what this frame shows

    /// <summary>What the next frame is to show (main thread; XrSession.Commit copies it into the frame).</summary>
    public Spec Pending => _pending;

    /// <summary>Show this picture (null hides it) at a pose in the play space, this wide in metres. Main thread.</summary>
    public void Set(Sojourn.Render.Realtime.OverlayImage? image, Vector3 position, Quaternion orientation, float widthMetres) =>
        _pending = Spec.At(image, null, position, orientation, widthMetres, false);

    /// <summary>Show an image the renderer draws each frame (the HUD board's screen; the swapchain's format, copied bit
    /// for bit), asked for when the frame is recorded; null hides it, as does a frame that drew none. Main thread.</summary>
    public void SetImage(Func<GpuImage?>? image, Vector3 position, Quaternion orientation, float widthMetres) =>
        _pending = Spec.At(null, image, position, orientation, widthMetres, true);

    /// <summary>This frame's spec (render thread, as the frame begins).</summary>
    public void Use(in Spec spec)
    {
        if (spec.GpuLayer && spec.Gpu == null) _shown = -1;
        _spec = spec;
        _pieceCount = 0;
    }

    // --- pieces --------------------------------------------------------------------------------------------------

    sealed class Piece
    {
        public Swapchain Chain;
        public VkImage[] Images = [];
        public Silk.NET.Vulkan.ImageView[] Views = [];
        public int W, H;
        public bool Held, Ready;
        public MenuClip Clip;
    }

    readonly List<Piece> _pieces = new();
    int _pieceCount;
    int _piecesIdle;
    const int PiecesKept = 300;   // frames unused before the parts' swapchains and the source go (~3 s)
    GpuImage? _source;            // a CPU picture on the GPU, while there are parts to make from it
    long _sourceVersion = -1;
    readonly long[] _staged = [-1, -1];   // the picture version each staging buffer holds

    /// <summary>The parts this frame shows (none: the whole layer): each the clip of the picture it keeps. Render
    /// thread, after <see cref="Use"/> and before <see cref="Record"/>.</summary>
    public void UsePieces(ReadOnlySpan<MenuClip> clips)
    {
        _pieceCount = 0;
        foreach (var c in clips)
        {
            if (_pieceCount == _pieces.Count) _pieces.Add(new Piece());
            var p = _pieces[_pieceCount++];
            p.Clip = c;
            p.Ready = false;
        }
    }

    /// <summary>Every part was made this frame (in <see cref="Record"/>); if not, the layer goes whole.</summary>
    public bool PiecesReady
    {
        get
        {
            for (int i = 0; i < _pieceCount; i++) if (!_pieces[i].Ready) return false;
            return _pieceCount > 0;
        }
    }

    /// <summary>How the frame's order submits this layer: piece -1 the whole of it, else that part; the whole layer
    /// stands in for its parts (at the first's place) when they weren't made. Whether it shows.</summary>
    public bool Submits(int piece, out bool whole)
    {
        whole = piece < 0 || !PiecesReady;
        if (whole) return Visible && piece <= 0;
        return Visible;
    }

    /// <summary>The menu's parts made from the picture (src, TransferSrcOptimal, left so): each one acquired, the
    /// picture copied in, the rest cleared.</summary>
    void RecordPieces(VkCommandBuffer cmd, GpuImage src, int w, int h, LayerCut cut)
    {
        var vkFormat = (Silk.NET.Vulkan.Format)format;
        for (int i = 0; i < _pieceCount; i++)
        {
            var p = _pieces[i];
            if (p.Chain.Handle != 0 && (p.W != w || p.H != h)) DestroyPiece(p);   // (not held: released last frame)
            if (p.Chain.Handle == 0) CreatePiece(p, w, h, cut);
            var ai = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
            uint index;
            lock (vk.QueueLock) XrRuntime.Check(xr.AcquireSwapchainImage(p.Chain, &ai, &index), $"xrAcquireSwapchainImage ({name} part)");
            var wi = new SwapchainImageWaitInfo { Type = StructureType.SwapchainImageWaitInfo, Timeout = long.MaxValue };
            XrRuntime.Check(xr.WaitSwapchainImage(p.Chain, &wi), $"xrWaitSwapchainImage ({name} part)");
            p.Held = true;
            var img = p.Images[index];
            GpuImage.Barrier(vk, cmd, img, VkImageAspectFlags.ColorBit, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.TransferDstOptimal, 0, 1, 0, 1);
            var copy = new Silk.NET.Vulkan.ImageCopy
            {
                SrcSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(VkImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(VkImageAspectFlags.ColorBit, 0, 0, 1),
                Extent = new Silk.NET.Vulkan.Extent3D((uint)w, (uint)h, 1),
            };
            vk.Api.CmdCopyImage(cmd, src.Handle, VkImageLayout.TransferSrcOptimal, img, VkImageLayout.TransferDstOptimal, 1, in copy);
            GpuImage.Barrier(vk, cmd, img, VkImageAspectFlags.ColorBit, VkImageLayout.TransferDstOptimal, VkImageLayout.ColorAttachmentOptimal, 0, 1, 0, 1);
            cut.Record(cmd, p.Views[index], vkFormat, (uint)w, (uint)h, p.Clip);
            p.Ready = true;
        }
    }

    void CreatePiece(Piece p, int w, int h, LayerCut cut)
    {
        var ci = new SwapchainCreateInfo
        {
            Type = StructureType.SwapchainCreateInfo,
            UsageFlags = SwapchainUsageFlags.TransferDstBit | SwapchainUsageFlags.SampledBit | SwapchainUsageFlags.ColorAttachmentBit,
            Format = format,
            SampleCount = 1, Width = (uint)w, Height = (uint)h, FaceCount = 1, ArraySize = 1, MipCount = 1,
        };
        Swapchain sc;
        XrRuntime.Check(xr.CreateSwapchain(session, &ci, &sc), $"xrCreateSwapchain ({name} part)");
        p.Chain = sc;
        (p.W, p.H) = (w, h);
        uint n = 0;
        XrRuntime.Check(xr.EnumerateSwapchainImages(sc, 0, &n, null), "xrEnumerateSwapchainImages");
        var imgs = new SwapchainImageVulkanKHR[n];
        for (int i = 0; i < n; i++) imgs[i].Type = StructureType.SwapchainImageVulkanKhr;
        fixed (SwapchainImageVulkanKHR* ptr = imgs)
            XrRuntime.Check(xr.EnumerateSwapchainImages(sc, n, &n, (SwapchainImageBaseHeader*)ptr), "xrEnumerateSwapchainImages");
        p.Images = imgs.Select(i => new VkImage(i.Image)).ToArray();
        p.Views = p.Images.Select(i => cut.ViewOf(i, (Silk.NET.Vulkan.Format)format)).ToArray();
    }

    void DestroyPiece(Piece p)
    {
        foreach (var v in p.Views) if (v.Handle != 0) vk.Api.DestroyImageView(vk.Device, v, null);
        p.Views = [];
        p.Images = [];
        if (p.Chain.Handle != 0) xr.DestroySwapchain(p.Chain);
        p.Chain = default;
        p.Ready = p.Held = false;
    }

    /// <summary>Unused for a while: the parts' swapchains and the source let go (none held: released at the last frame's
    /// end, after its GPU work).</summary>
    void IdlePieces()
    {
        if (_pieceCount > 0) { _piecesIdle = 0; return; }
        if (++_piecesIdle != PiecesKept) return;
        foreach (var p in _pieces) DestroyPiece(p);
        _source?.Dispose();
        _source = null;
        _sourceVersion = -1;
    }

    /// <summary>The CPU picture on the GPU, w x h of it, current (left TransferSrcOptimal): from this frame's staging
    /// if it holds the picture, else staged now.</summary>
    GpuImage Source(VkCommandBuffer cmd, int slot, Sojourn.Render.Realtime.OverlayImage pic, int w, int h)
    {
        if (_source != null && (_source.Width != w || _source.Height != h))
        {
            _source.Dispose();   // (the frames that read it are done: each frame's GPU work ends before the next records)
            _source = null;
        }
        _source ??= new GpuImage(vk, (uint)w, (uint)h, (Silk.NET.Vulkan.Format)format,
            Silk.NET.Vulkan.ImageUsageFlags.TransferDstBit | Silk.NET.Vulkan.ImageUsageFlags.TransferSrcBit);
        if (_sourceVersion != pic.Version)
        {
            ulong bytes = (ulong)pic.Width * (ulong)h * 4;
            var st = _staging[slot % 2];
            if (_staged[slot % 2] != pic.Version || st == null)
            {
                if (st == null || st.Size < bytes)
                {
                    st?.Dispose();
                    st = _staging[slot % 2] = new GpuBuffer(vk, Math.Max(bytes, 4UL << 20), Silk.NET.Vulkan.BufferUsageFlags.TransferSrcBit, MemoryUse.Upload);
                }
                pic.Rgba.AsSpan(0, (int)bytes).CopyTo(st.Mapped);
                _staged[slot % 2] = pic.Version;
            }
            _source.Transition(cmd, VkImageLayout.TransferDstOptimal);
            var copy = new Silk.NET.Vulkan.BufferImageCopy
            {
                BufferRowLength = (uint)pic.Width,
                ImageSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(VkImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Silk.NET.Vulkan.Extent3D((uint)w, (uint)h, 1),
            };
            vk.Api.CmdCopyBufferToImage(cmd, st.Handle, _source.Handle, VkImageLayout.TransferDstOptimal, 1, in copy);
            _sourceVersion = pic.Version;
        }
        _source.Transition(cmd, VkImageLayout.TransferSrcOptimal);
        return _source;
    }

    /// <summary>Shows in this frame: a picture, or the renderer's image as <see cref="Record"/> last found it (asked
    /// there, on the render thread, not again where the frame is ended: that's another thread with overlap, and the
    /// renderer may be replacing the image for the next frame meanwhile).</summary>
    public bool Visible => (_spec.Image != null || (_spec.Gpu != null && _gpuDrawn)) && _shown >= 0 && _w > 0;
    bool _gpuDrawn;

    void RecordImage(VkCommandBuffer cmd, GpuImage src, LayerMirror? mirror, LayerCut? cut)
    {
        if (_chain.Handle != 0 && (src.Width != _cw || src.Height != _ch))
        {
            xr.DestroySwapchain(_chain);
            _chain = default;
        }
        if (_chain.Handle == 0) Create((int)src.Width, (int)src.Height);
        var ai = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
        uint index;
        lock (vk.QueueLock) XrRuntime.Check(xr.AcquireSwapchainImage(_chain, &ai, &index), $"xrAcquireSwapchainImage ({name})");
        var wi = new SwapchainImageWaitInfo { Type = StructureType.SwapchainImageWaitInfo, Timeout = long.MaxValue };
        XrRuntime.Check(xr.WaitSwapchainImage(_chain, &wi), $"xrWaitSwapchainImage ({name})");
        _held = true;
        var img = _images[index];
        var was = src.Layout;
        src.Transition(cmd, VkImageLayout.TransferSrcOptimal);
        GpuImage.Barrier(vk, cmd, img, VkImageAspectFlags.ColorBit, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.TransferDstOptimal, 0, 1, 0, 1);
        var copy = new Silk.NET.Vulkan.ImageCopy
        {
            SrcSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(VkImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(VkImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Silk.NET.Vulkan.Extent3D(src.Width, src.Height, 1),
        };
        vk.Api.CmdCopyImage(cmd, src.Handle, VkImageLayout.TransferSrcOptimal, img, VkImageLayout.TransferDstOptimal, 1, in copy);
        GpuImage.Barrier(vk, cmd, img, VkImageAspectFlags.ColorBit, VkImageLayout.TransferDstOptimal, VkImageLayout.ColorAttachmentOptimal, 0, 1, 0, 1);
        if (mirror != null)
        {
            mirror.Copy(cmd, _mirror ??= mirror.NewPicture(), src);
            _mirrorFresh = true;
        }
        _shown = 0;
        (_w, _h) = ((int)src.Width, (int)src.Height);
        if (_pieceCount > 0 && cut != null) RecordPieces(cmd, src, _w, _h, cut);
        if (was != VkImageLayout.Undefined) src.Transition(cmd, was);
    }

    /// <summary>Copy a new picture into the next swapchain image (held until <see cref="Release"/>), and into the
    /// mirror's copy while it shows the menus (mirror non-null); then this frame's parts, if it goes in parts (cut: the
    /// pass that clears them; without it none are made, and the layer shows whole).</summary>
    public void Record(VkCommandBuffer cmd, int slot, LayerMirror? mirror, LayerCut? cut = null)
    {
        _mirrorFresh = false;
        IdlePieces();
        if (cut == null) _pieceCount = 0;
        if (_spec.Gpu is { } gpu)
        {
            var src = gpu();
            _gpuDrawn = src != null;
            if (src != null) RecordImage(cmd, src, mirror, cut);
            return;
        }
        RecordPicture(cmd, slot, mirror);
        if (_pieceCount > 0 && _shown >= 0 && _w > 0 && _spec.Image is { } pic && pic.Version == _shown)
            RecordPieces(cmd, Source(cmd, slot, pic, _w, _h), _w, _h, cut!);
    }

    void RecordPicture(VkCommandBuffer cmd, int slot, LayerMirror? mirror)
    {
        if (_spec.Image is not { } want || want.Width <= 0 || want.Height <= 0) return;
        if (want.Version == _shown)
        {
            // unchanged, but the mirror's copy is older (it was off when the picture came)
            if (mirror != null && (_mirror == null || _mirror.Version != _shown)) MirrorOnly(cmd, slot, want, mirror);
            return;
        }
        if (_chain.Handle != 0 && fit && (want.Width > _cw || want.Height > _ch))
        {
            // outgrown: not held (released at the end of the last frame), so it can go
            xr.DestroySwapchain(_chain);
            _chain = default;
        }
        if (_chain.Handle == 0) Create(fit ? Math.Min(maxW, (want.Width + 127) / 128 * 128) : maxW, fit ? Math.Min(maxH, (want.Height + 127) / 128 * 128) : maxH);
        int w = Math.Min(want.Width, _cw), h = Math.Min(want.Height, _ch);
        ulong bytes = (ulong)want.Width * (ulong)h * 4;
        var st = _staging[slot % 2];
        if (st == null || st.Size < bytes)
        {
            st?.Dispose();   // its slot's last frame is done (the renderer waited for it)
            st = _staging[slot % 2] = new GpuBuffer(vk, Math.Max(bytes, 4UL << 20), Silk.NET.Vulkan.BufferUsageFlags.TransferSrcBit, MemoryUse.Upload);
        }
        want.Rgba.AsSpan(0, (int)bytes).CopyTo(st.Mapped);
        _staged[slot % 2] = want.Version;

        var ai = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
        uint index;
        lock (vk.QueueLock) XrRuntime.Check(xr.AcquireSwapchainImage(_chain, &ai, &index), $"xrAcquireSwapchainImage ({name})");
        var wi = new SwapchainImageWaitInfo { Type = StructureType.SwapchainImageWaitInfo, Timeout = long.MaxValue };
        XrRuntime.Check(xr.WaitSwapchainImage(_chain, &wi), $"xrWaitSwapchainImage ({name})");
        _held = true;
        var img = _images[index];
        GpuImage.Barrier(vk, cmd, img, VkImageAspectFlags.ColorBit, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.TransferDstOptimal, 0, 1, 0, 1);
        // clear first: a shorter picture than last time mustn't keep the old bottom
        var clear = new Silk.NET.Vulkan.ClearColorValue(0f, 0f, 0f, 0f);
        var range = new Silk.NET.Vulkan.ImageSubresourceRange(VkImageAspectFlags.ColorBit, 0, 1, 0, 1);
        vk.Api.CmdClearColorImage(cmd, img, VkImageLayout.TransferDstOptimal, in clear, 1, in range);
        var copy = new Silk.NET.Vulkan.BufferImageCopy
        {
            BufferRowLength = (uint)want.Width,
            ImageSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(VkImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Silk.NET.Vulkan.Extent3D((uint)w, (uint)h, 1),
        };
        vk.Api.CmdCopyBufferToImage(cmd, st.Handle, img, VkImageLayout.TransferDstOptimal, 1, in copy);
        GpuImage.Barrier(vk, cmd, img, VkImageAspectFlags.ColorBit, VkImageLayout.TransferDstOptimal, VkImageLayout.ColorAttachmentOptimal, 0, 1, 0, 1);
        if (mirror != null)
        {
            mirror.Upload(cmd, _mirror ??= mirror.NewPicture(), st.Handle, want.Width, w, h);
            _mirror.Version = want.Version;
        }
        _shown = want.Version;
        (_w, _h) = (w, h);
    }

    /// <summary>The shown picture into the mirror's copy alone (the slot's staging is free: nothing new this frame).</summary>
    void MirrorOnly(VkCommandBuffer cmd, int slot, Sojourn.Render.Realtime.OverlayImage pic, LayerMirror mirror)
    {
        int w = Math.Min(pic.Width, _cw), h = Math.Min(pic.Height, _ch);
        ulong bytes = (ulong)pic.Width * (ulong)h * 4;
        var st = _staging[slot % 2];
        if (st == null || st.Size < bytes)
        {
            st?.Dispose();
            st = _staging[slot % 2] = new GpuBuffer(vk, Math.Max(bytes, 4UL << 20), Silk.NET.Vulkan.BufferUsageFlags.TransferSrcBit, MemoryUse.Upload);
        }
        pic.Rgba.AsSpan(0, (int)bytes).CopyTo(st.Mapped);
        _staged[slot % 2] = pic.Version;
        mirror.Upload(cmd, _mirror ??= mirror.NewPicture(), st.Handle, pic.Width, w, h);
        _mirror.Version = pic.Version;
    }

    /// <summary>The picture the mirror window shows for this layer this frame and the layer's corners in the play space
    /// (top left, top right, bottom right, bottom left), when it's up to date; render thread, after <see cref="Record"/>.</summary>
    public MirrorQuad? Mirror(int piece = -1)
    {
        if (piece >= 0) return piece < _pieceCount && Mirror() is { } whole ? whole with { Clip = _pieces[piece].Clip } : null;
        if (!Visible || _mirror is not { Ready: true } m || _spec.WidthM <= 0 || _w <= 0) return null;
        if (_spec.GpuLayer ? !_mirrorFresh : m.Version != _shown) return null;
        var pose = _spec.Pose;
        var q = new Quaternion(pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W);
        var c = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
        var r = Vector3.Transform(Vector3.UnitX, q) * (_spec.WidthM / 2);
        var u = Vector3.Transform(Vector3.UnitY, q) * (_spec.WidthM * _h / _w / 2);
        return new MirrorQuad(m, c - r + u, c + r + u, c + r - u, c - r - u);
    }

    void Create(int cw, int ch)
    {
        (_cw, _ch) = (cw, ch);
        var ci = new SwapchainCreateInfo
        {
            Type = StructureType.SwapchainCreateInfo,
            UsageFlags = SwapchainUsageFlags.TransferDstBit | SwapchainUsageFlags.SampledBit | SwapchainUsageFlags.ColorAttachmentBit,
            Format = format,
            SampleCount = 1, Width = (uint)cw, Height = (uint)ch, FaceCount = 1, ArraySize = 1, MipCount = 1,
        };
        Swapchain sc;
        XrRuntime.Check(xr.CreateSwapchain(session, &ci, &sc), $"xrCreateSwapchain ({name})");
        _chain = sc;
        uint n = 0;
        XrRuntime.Check(xr.EnumerateSwapchainImages(sc, 0, &n, null), "xrEnumerateSwapchainImages");
        var imgs = new SwapchainImageVulkanKHR[n];
        for (int i = 0; i < n; i++) imgs[i].Type = StructureType.SwapchainImageVulkanKhr;
        fixed (SwapchainImageVulkanKHR* p = imgs)
            XrRuntime.Check(xr.EnumerateSwapchainImages(sc, n, &n, (SwapchainImageBaseHeader*)p), "xrEnumerateSwapchainImages");
        _images = imgs.Select(i => new VkImage(i.Image)).ToArray();
    }

    public void Release()
    {
        var ri = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
        foreach (var p in _pieces)
            if (p.Held)
            {
                xr.ReleaseSwapchainImage(p.Chain, &ri);
                p.Held = false;
            }
        if (!_held) return;
        xr.ReleaseSwapchainImage(_chain, &ri);
        _held = false;
    }

    /// <summary>Part i's layer: as the whole one's, from the part's swapchain.</summary>
    public CompositionLayerQuad PieceLayer(int i, Space space)
    {
        var l = Layer(space);
        var p = _pieces[i];
        l.SubImage = new SwapchainSubImage { Swapchain = p.Chain, ImageRect = new Rect2Di(new Offset2Di(0, 0), new Extent2Di(p.W, p.H)) };
        return l;
    }

    public CompositionLayerQuad Layer(Space space) => new()
    {
        Type = StructureType.CompositionLayerQuad,
        LayerFlags = CompositionLayerFlags.BlendTextureSourceAlphaBit,
        Space = space,
        EyeVisibility = EyeVisibility.Both,
        SubImage = new SwapchainSubImage
        {
            Swapchain = _chain,
            ImageRect = new Rect2Di(new Offset2Di(0, 0), new Extent2Di(_w, _h)),
        },
        Pose = _spec.Pose,
        Size = new Extent2Df(_spec.WidthM, _w > 0 ? _spec.WidthM * _h / _w : 0),
    };

    /// <summary>Where the layer will be next frame, if it shows: its corners in the play space (top left, top right,
    /// bottom right, bottom left), for the hands drawn over it. Main thread, from the pending spec.</summary>
    public Vector3[]? Corners() => CornersOf(_pending, maxW, maxH, _w, _h, _shown >= 0);

    /// <summary>A spec's corners: a new picture is taken at its own size (it's copied in when the frame is recorded,
    /// after this is asked), the renderer's image at the size last shown.</summary>
    public static Vector3[]? CornersOf(in Spec spec, int maxW, int maxH, int shownW = 0, int shownH = 0, bool shown = false)
    {
        int w, h;
        if (spec.Image is { Width: > 0, Height: > 0 } want) (w, h) = (Math.Min(want.Width, maxW), Math.Min(want.Height, maxH));
        else if (spec.Gpu?.Invoke() != null && shown && shownW > 0) (w, h) = (shownW, shownH);
        else return null;
        if (spec.WidthM <= 0) return null;
        var pose = spec.Pose;
        var q = new Quaternion(pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W);
        var c = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
        var r = Vector3.Transform(Vector3.UnitX, q) * (spec.WidthM / 2);
        var u = Vector3.Transform(Vector3.UnitY, q) * (spec.WidthM * h / w / 2);
        return [c - r + u, c + r + u, c + r - u, c - r - u];
    }

    /// <summary>The layer as a menu to order (MenuOrder), as this frame's spec shows it (null: nothing to show). Render
    /// thread, after <see cref="Use"/>.</summary>
    public MenuOrder.Menu? AsMenu(int stack = 0, bool top = false) =>
        CornersOf(_spec, maxW, maxH, _w, _h, _shown >= 0) is { } c ? MenuOf(c, stack, top) : null;

    /// <summary>A layer's corners (top left, top right, bottom right, bottom left) as a menu to order.</summary>
    public static MenuOrder.Menu MenuOf(Vector3[] c, int stack = 0, bool top = false) =>
        new((c[0] + c[2]) * 0.5f, (c[1] - c[0]) * 0.5f, (c[0] - c[3]) * 0.5f, stack, top);

    /// <summary>The largest picture it shows (bigger ones are cropped to it).</summary>
    public int MaxW => maxW;
    public int MaxH => maxH;

    public void Dispose()
    {
        foreach (var p in _pieces) DestroyPiece(p);
        _source?.Dispose();
        if (_chain.Handle != 0) xr.DestroySwapchain(_chain);
        foreach (var st in _staging) st?.Dispose();
        _mirror?.Dispose();
    }
}
