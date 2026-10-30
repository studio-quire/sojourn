using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Realtime;

/// <summary>A frame for a video: the first eye as the person sees it (without the overlay), scaled on the GPU to at
/// most <paramref name="MaxWidth"/> pixels wide (even sizes, as encoders want), handed over as RGBA8 once the GPU is
/// done with the frame; with <paramref name="Height"/> given, exactly MaxWidth by Height. Deliver runs on the thread pool;
/// frames can arrive out of order.</summary>
public sealed record VideoCapture(int MaxWidth, Action<byte[], int, int> Deliver, int Height = 0);

/// <summary>Frames for the assistant's recordings (the client's Assistant/Recorder.cs). The eye image is blitted down
/// on the GPU before the readback, so a recording costs a small copy a frame rather than a full eye's (2064x2208 in a
/// Quest 3 is 18 MB) and the CPU's resize of it, which would change the very frame timing a recording of a flicker is
/// for. Claude Opus 5.5, 2026-10-26.</summary>
public sealed partial class FrameRenderer
{
    GpuImage? _videoImage;
    readonly Dictionary<FrameResources, GpuBuffer> _videoReadback = new();

    /// <summary>In the eye's present step: the image is TransferSrcOptimal, and stays so.</summary>
    unsafe void RecordVideo(FrameResources fr, CommandBuffer cmd, GpuImage img, VideoCapture v)
    {
        var vk = _gpu.Vk;
        var api = vk.Api;
        uint w = Math.Max(16u, Math.Min(img.Width, (uint)Math.Max(16, v.MaxWidth))) & ~1u;
        uint h = Math.Max(16u, (uint)MathF.Round(img.Height * (w / (float)img.Width))) & ~1u;
        if (v.Height > 0) (w, h) = ((uint)v.MaxWidth & ~1u, (uint)v.Height & ~1u);
        if (_videoImage is not { } vi || vi.Width != w || vi.Height != h || vi.Format != img.Format)
        {
            // the old one is retired with this frame: earlier frames that used it are done by then (one queue)
            if (_videoImage is { } old) fr.Retire.Add(old.Dispose);
            _videoImage = vi = new GpuImage(vk, w, h, img.Format, ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit);
        }
        ulong bytes = (ulong)w * h * 4;
        if (!_videoReadback.TryGetValue(fr, out var rb) || rb.Size < bytes)
        {
            if (rb is not null) rb.Dispose();   // this slot's last frame is done (its fence was waited on to reuse the slot)
            _videoReadback[fr] = rb = new GpuBuffer(vk, bytes, BufferUsageFlags.TransferDstBit, MemoryUse.Readback);
        }
        GpuImage.Barrier(vk, cmd, vi.Handle, vi.Aspect, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, 1, 0, 1);
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        blit.SrcOffsets[1] = new Offset3D((int)img.Width, (int)img.Height, 1);
        blit.DstOffsets[1] = new Offset3D((int)w, (int)h, 1);
        api.CmdBlitImage(cmd, img.Handle, ImageLayout.TransferSrcOptimal, vi.Handle, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
        GpuImage.Barrier(vk, cmd, vi.Handle, vi.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, 0, 1, 0, 1);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(w, h, 1),
        };
        api.CmdCopyImageToBuffer(cmd, vi.Handle, ImageLayout.TransferSrcOptimal, rb.Handle, 1, in copy);
        var hb = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit, DstAccessMask = AccessFlags2.HostReadBit,
        };
        var di = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &hb };
        api.CmdPipelineBarrier2(cmd, in di);
        int iw = (int)w, ih = (int)h;
        var buffer = rb;
        fr.Retire.Add(() =>
        {
            var pixels = new byte[iw * ih * 4];
            new ReadOnlySpan<byte>((void*)buffer.Alloc.Mapped, pixels.Length).CopyTo(pixels);
            _ = Task.Run(() => v.Deliver(pixels, iw, ih));
        });
    }

    void DisposeVideo()
    {
        _videoImage?.Dispose();
        foreach (var b in _videoReadback.Values) b.Dispose();
        _videoReadback.Clear();
    }
}
