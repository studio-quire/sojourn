using Sojourn.Render.Gpu;
using Sojourn.Render.Realtime;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Image = Silk.NET.Vulkan.Image;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Sojourn.Client.Window;

/// <summary>
/// The window's swapchain as a frame target: each finished eye image is blitted into the acquired swapchain image
/// (one eye fills it; two sit side by side, aspect kept). Rebuilt when the window resizes or the surface goes stale.
/// Until the frame finishes (<see cref="RecordFinish"/>) the image stays open for drawing over the eyes (the headset's
/// menus, Window/MirrorTarget.cs): where each eye went is <see cref="Cell"/>.
/// </summary>
public sealed unsafe class Presenter : IFrameTarget, IDisposable
{
    readonly VulkanContext _vk;
    readonly KhrSwapchain _api;
    readonly Func<(int W, int H)> _size;
    SwapchainKHR _swapchain;
    Image[] _images = [];
    ImageView[] _views = [];
    Semaphore[] _acquired;
    // the same acquire signals a fence too, so a frame can tell whether the window's image was free yet when it was
    // submitted: if not, the GPU waits for it before the submission's copies (in the headset, the eyes' work with it)
    Fence[] _acquiredFence;
    bool[] _fenceUsed;
    int _slot;
    int _notReady, _presented;
    Semaphore[] _rendered = [];   // per swapchain image: a present may still wait on it while another slot runs
    uint _index;
    volatile bool _stale = true;   // (set by the main thread's resize and F7 too)
    public const Format SurfaceFormat = Format.B8G8R8A8Unorm;   // the renderer's output is already gamma-encoded

    public uint Width { get; private set; }
    public uint Height { get; private set; }
    /// <summary>Vsync (FIFO) caps the frame rate at the display's; off (mailbox, else immediate) for measuring.</summary>
    public bool Vsync { get => _vsync; set { if (_vsync != value) { _vsync = value; _stale = true; } } }
    bool _vsync = true;
    public PresentModeKHR Mode { get; private set; }
    // this frame's: blank (Blank as the frame began), how many eyes went in and where, the image open (blitted into,
    // not yet made ready to present) and its layout
    bool _blank, _open;
    int _eyes;
    readonly Rect2D[] _cells = new Rect2D[2];
    ImageLayout _layout;
    bool _canCapture;
    GpuBuffer? _captureBuffer;
    string? _captureTo;

    public Presenter(VulkanContext vk, int slots, Func<(int W, int H)> size)
    {
        _vk = vk;
        _size = size;
        if (!vk.Api.TryGetDeviceExtension(vk.Instance, vk.Device, out _api)) throw new InvalidOperationException("VK_KHR_swapchain unavailable");
        _acquired = Enumerable.Range(0, slots).Select(_ => CreateSemaphore()).ToArray();
        _acquiredFence = Enumerable.Range(0, slots).Select(_ =>
        {
            var fi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            VulkanContext.Check(_vk.Api.CreateFence(_vk.Device, in fi, null, out var f), "vkCreateFence");
            return f;
        }).ToArray();
        _fenceUsed = new bool[slots];
    }

    public void Invalidate() => _stale = true;

    Semaphore CreateSemaphore()
    {
        var ci = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        VulkanContext.Check(_vk.Api.CreateSemaphore(_vk.Device, in ci, null, out var s), "vkCreateSemaphore");
        return s;
    }

    bool Rebuild()
    {
        var (w, h) = _size();
        if (w <= 0 || h <= 0) return false;
        _vk.WaitIdle();
        var surf = _vk.SurfaceApi!;
        surf.GetPhysicalDeviceSurfaceCapabilities(_vk.PhysicalDevice, _vk.Surface, out var caps);
        var extent = caps.CurrentExtent.Width != uint.MaxValue ? caps.CurrentExtent
            : new Extent2D((uint)Math.Clamp(w, (int)caps.MinImageExtent.Width, (int)caps.MaxImageExtent.Width),
                           (uint)Math.Clamp(h, (int)caps.MinImageExtent.Height, (int)caps.MaxImageExtent.Height));
        if (extent.Width == 0 || extent.Height == 0) return false;

        uint modeCount = 0;
        surf.GetPhysicalDeviceSurfacePresentModes(_vk.PhysicalDevice, _vk.Surface, ref modeCount, null);
        var modes = new PresentModeKHR[modeCount];
        fixed (PresentModeKHR* p = modes) surf.GetPhysicalDeviceSurfacePresentModes(_vk.PhysicalDevice, _vk.Surface, ref modeCount, p);
        Mode = _vsync ? PresentModeKHR.FifoKhr
            : modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr
            : modes.Contains(PresentModeKHR.ImmediateKhr) ? PresentModeKHR.ImmediateKhr : PresentModeKHR.FifoKhr;

        _canCapture = (caps.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0;
        uint count = Math.Max(caps.MinImageCount + 1, 3);
        if (caps.MaxImageCount > 0) count = Math.Min(count, caps.MaxImageCount);
        var old = _swapchain;
        var ci = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _vk.Surface,
            MinImageCount = count,
            ImageFormat = SurfaceFormat,
            ImageColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            // (and read back where the surface allows it: CaptureNext)
            ImageUsage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit | (_canCapture ? ImageUsageFlags.TransferSrcBit : 0),
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = Mode,
            Clipped = true,
            OldSwapchain = old,
        };
        VulkanContext.Check(_api.CreateSwapchain(_vk.Device, in ci, null, out _swapchain), "vkCreateSwapchainKHR");
        if (old.Handle != 0) _api.DestroySwapchain(_vk.Device, old, null);

        uint n = 0;
        _api.GetSwapchainImages(_vk.Device, _swapchain, ref n, null);
        _images = new Image[n];
        fixed (Image* p = _images) _api.GetSwapchainImages(_vk.Device, _swapchain, ref n, p);
        foreach (var v in _views) _vk.Api.DestroyImageView(_vk.Device, v, null);
        _views = _images.Select(img =>
        {
            var vci = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo, Image = img, ViewType = ImageViewType.Type2D, Format = SurfaceFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            VulkanContext.Check(_vk.Api.CreateImageView(_vk.Device, in vci, null, out var view), "window image view");
            return view;
        }).ToArray();
        foreach (var s in _rendered) _vk.Api.DestroySemaphore(_vk.Device, s, null);
        _rendered = Enumerable.Range(0, (int)n).Select(_ => CreateSemaphore()).ToArray();
        Width = extent.Width;
        Height = extent.Height;
        _stale = false;
        return true;
    }

    public bool Acquire(int slot, out Semaphore ready, out Semaphore done)
    {
        ready = done = default;
        if (_stale && !Rebuild()) return false;
        var fence = _acquiredFence[slot];
        if (_fenceUsed[slot])
        {
            _vk.Api.WaitForFences(_vk.Device, 1, in fence, true, 1_000_000_000);
            _vk.Api.ResetFences(_vk.Device, 1, in fence);
            _fenceUsed[slot] = false;
        }
        var r = _api.AcquireNextImage(_vk.Device, _swapchain, ulong.MaxValue, _acquired[slot], fence, ref _index);
        if (r == Result.ErrorOutOfDateKhr)
        {
            _stale = true;
            return false;
        }
        if (r == Result.SuboptimalKhr) _stale = true;
        else VulkanContext.Check(r, "vkAcquireNextImageKHR");
        _fenceUsed[slot] = true;
        _slot = slot;
        _blank = Blank;
        _eyes = 0;
        _open = false;
        ready = _acquired[slot];
        done = _rendered[_index];
        return true;
    }

    /// <summary>The window shows nothing of the view, only a dark fill (the headset's mirror while incognito is on, so
    /// the PC's monitor doesn't show what the person sees). Set by the main thread, read by the render thread as each
    /// frame begins.</summary>
    public volatile bool Blank;

    /// <summary>This frame shows the view (it isn't <see cref="Blank"/>). Render thread, after Acquire.</summary>
    public bool ShowsView => !_blank;
    /// <summary>How many eyes this frame put into the window (two: the headset's, side by side).</summary>
    public int Eyes => _eyes;
    /// <summary>Where an eye's image went in the window this frame.</summary>
    public Rect2D Cell(int eye) => _cells[eye];
    /// <summary>The acquired image's view, for drawing over the eyes (<see cref="ToAttachment"/> first).</summary>
    public ImageView View => _views[_index];

    /// <summary>The next frame's window image is saved as a PNG here, as the window shows it (a test's picture of the
    /// headset's mirror). Any thread.</summary>
    public volatile string? CaptureNext;

    public void RecordPresent(CommandBuffer cmd, int eye, int eyeCount, GpuImage output)
    {
        var img = _images[_index];
        if (eye == 0)
        {
            GpuImage.Barrier(_vk, cmd, img, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, 1, 0, 1);
            (_open, _layout) = (true, ImageLayout.TransferDstOptimal);
        }
        if (_blank)
        {
            if (eye == 0)
            {
                var dark = new ClearColorValue(0.07f, 0.075f, 0.085f, 1f);
                var all = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
                _vk.Api.CmdClearColorImage(cmd, img, ImageLayout.TransferDstOptimal, in dark, 1, in all);
            }
            return;
        }
        if (eye == 0)
        {
            if (eyeCount > 1)
            {
                var black = new ClearColorValue(0f, 0f, 0f, 1f);
                var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
                _vk.Api.CmdClearColorImage(cmd, img, ImageLayout.TransferDstOptimal, in black, 1, in range);
            }
        }
        // the eye's cell of the window, its image fitted inside with its aspect kept
        float cellW = (float)Width / eyeCount, cellH = Height;
        float scale = MathF.Min(cellW / output.Width, cellH / output.Height);
        if (eyeCount == 1) scale = 0;   // one eye: stretch to the window (the image is rendered at its size)
        int w = scale > 0 ? (int)(output.Width * scale) : (int)Width, h = scale > 0 ? (int)(output.Height * scale) : (int)Height;
        int x = (int)(cellW * eye + (cellW - w) / 2), y = (int)((cellH - h) / 2);
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        blit.SrcOffsets[1] = new Offset3D((int)output.Width, (int)output.Height, 1);
        blit.DstOffsets[0] = new Offset3D(x, y, 0);
        blit.DstOffsets[1] = new Offset3D(x + w, y + h, 1);
        _vk.Api.CmdBlitImage(cmd, output.Handle, ImageLayout.TransferSrcOptimal, img, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
        if (eye < _cells.Length) _cells[eye] = new Rect2D(new Offset2D(x, y), new Extent2D((uint)w, (uint)h));
        _eyes = Math.Max(_eyes, eye + 1);
    }

    /// <summary>The open image made ready to be drawn into (COLOR_ATTACHMENT_OPTIMAL) over the eyes.</summary>
    public void ToAttachment(CommandBuffer cmd)
    {
        if (!_open || _layout == ImageLayout.ColorAttachmentOptimal) return;
        GpuImage.Barrier(_vk, cmd, _images[_index], ImageAspectFlags.ColorBit, _layout, ImageLayout.ColorAttachmentOptimal, 0, 1, 0, 1);
        _layout = ImageLayout.ColorAttachmentOptimal;
    }

    /// <summary>The frame's image made ready to present (read back first when <see cref="CaptureNext"/> asks).</summary>
    public void RecordFinish(CommandBuffer cmd)
    {
        if (!_open) return;
        var img = _images[_index];
        if (CaptureNext is { } path && _canCapture)
        {
            CaptureNext = null;
            ulong bytes = (ulong)Width * Height * 4;
            if (_captureBuffer == null || _captureBuffer.Size < bytes)
            {
                _vk.WaitIdle();   // a test's picture: the last one's copy may still be in flight
                _captureBuffer?.Dispose();
                _captureBuffer = new GpuBuffer(_vk, bytes, BufferUsageFlags.TransferDstBit, MemoryUse.Readback);
            }
            GpuImage.Barrier(_vk, cmd, img, ImageAspectFlags.ColorBit, _layout, ImageLayout.TransferSrcOptimal, 0, 1, 0, 1);
            _layout = ImageLayout.TransferSrcOptimal;
            var copy = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(Width, Height, 1),
            };
            _vk.Api.CmdCopyImageToBuffer(cmd, img, ImageLayout.TransferSrcOptimal, _captureBuffer.Handle, 1, in copy);
            _captureTo = path;
        }
        GpuImage.Barrier(_vk, cmd, img, ImageAspectFlags.ColorBit, _layout, ImageLayout.PresentSrcKhr, 0, 1, 0, 1);
        _open = false;
    }

    /// <summary>After the frame's submission: the captured image (BGRA) waited for and written as a PNG off this thread.</summary>
    void SaveCapture()
    {
        if (_captureTo is not { } path || _captureBuffer is not { } buf) return;
        _captureTo = null;
        _vk.Api.QueueWaitIdle(_vk.Queue);
        int w = (int)Width, h = (int)Height;
        var px = new byte[w * h * 4];
        buf.Mapped[..px.Length].CopyTo(px);
        for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllBytes(path, Sojourn.Render.ImageCodec.EncodePng(px, w, h));
            }
            catch (IOException) { }
        });
    }

    /// <summary>Frames since the last call whose window image wasn't free yet when they were submitted (the GPU waited
    /// for it, or will), and frames presented.</summary>
    public (int NotReady, int Presented) TakeImageWaits()
    {
        var r = (_notReady, _presented);
        _notReady = _presented = 0;
        return r;
    }

    public void Present()
    {
        _presented++;
        if (_fenceUsed[_slot] && _vk.Api.GetFenceStatus(_vk.Device, _acquiredFence[_slot]) != Result.Success) _notReady++;
        var sc = _swapchain;
        var index = _index;
        var wait = _rendered[_index];
        var pi = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1, PWaitSemaphores = &wait,
            SwapchainCount = 1, PSwapchains = &sc, PImageIndices = &index,
        };
        var r = _api.QueuePresent(_vk.Queue, in pi);
        if (r is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr) _stale = true;
        else VulkanContext.Check(r, "vkQueuePresentKHR");
        SaveCapture();
    }

    public void Dispose()
    {
        _vk.WaitIdle();
        foreach (var s in _acquired) _vk.Api.DestroySemaphore(_vk.Device, s, null);
        foreach (var f in _acquiredFence) _vk.Api.DestroyFence(_vk.Device, f, null);
        foreach (var s in _rendered) _vk.Api.DestroySemaphore(_vk.Device, s, null);
        foreach (var v in _views) _vk.Api.DestroyImageView(_vk.Device, v, null);
        _captureBuffer?.Dispose();
        if (_swapchain.Handle != 0) _api.DestroySwapchain(_vk.Device, _swapchain, null);
    }
}
