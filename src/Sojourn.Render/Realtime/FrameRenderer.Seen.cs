using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Realtime;

/// <summary>The first eye as it goes to the display (FrameRequest.SeenTo; dev.py shot's picture of what the person
/// sees). Read back into a buffer of its own, which no later frame touches until a worker has copied it out, so the
/// render thread records a copy command and nothing more: no 18 MB memcpy on the frame's path as the shared readback
/// slots have. One in flight; another asked for meanwhile (a second shot within a few frames) gets a buffer for the
/// once, copied out on the render thread as the other captures are. Claude Opus 5.5, 2026-10-28.</summary>
public sealed partial class FrameRenderer
{
    GpuBuffer? _seen;
    int _seenBusy;   // 1 from the copy's recording until the worker has taken the pixels

    void RecordSeen(FrameResources fr, CommandBuffer cmd, GpuImage img, Action<byte[], int, int> to)
    {
        var vk = _gpu.Vk;
        int w = (int)img.Width, h = (int)img.Height;
        ulong bytes = (ulong)w * (ulong)h * 4;
        if (Interlocked.CompareExchange(ref _seenBusy, 1, 0) != 0)
        {
            var once = new GpuBuffer(vk, bytes, BufferUsageFlags.TransferDstBit, MemoryUse.Readback);
            RecordReadback(cmd, img, once);
            fr.Retire.Add(() =>
            {
                var pixels = CopyOut(once, bytes);
                once.Dispose();
                _ = Task.Run(() => to(pixels, w, h));
            });
            return;
        }
        if (_seen == null || _seen.Size < bytes)
        {
            _seen?.Dispose();   // its last copy was taken (not busy), and its frame long done
            _seen = new GpuBuffer(vk, bytes, BufferUsageFlags.TransferDstBit, MemoryUse.Readback);
        }
        var rb = _seen;
        RecordReadback(cmd, img, rb);
        fr.Retire.Add(() => _ = Task.Run(() =>
        {
            byte[] pixels;
            try { pixels = CopyOut(rb, bytes); }
            finally { Volatile.Write(ref _seenBusy, 0); }
            to(pixels, w, h);
        }));
    }

    unsafe void RecordReadback(CommandBuffer cmd, GpuImage img, GpuBuffer into)
    {
        var api = _gpu.Vk.Api;
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(img.Width, img.Height, 1),
        };
        api.CmdCopyImageToBuffer(cmd, img.Handle, ImageLayout.TransferSrcOptimal, into.Handle, 1, in copy);
        var hb = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit, DstAccessMask = AccessFlags2.HostReadBit,
        };
        var di = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &hb };
        api.CmdPipelineBarrier2(cmd, in di);
    }

    static unsafe byte[] CopyOut(GpuBuffer from, ulong bytes)
    {
        var pixels = new byte[(int)bytes];
        new ReadOnlySpan<byte>((void*)from.Alloc.Mapped, pixels.Length).CopyTo(pixels);
        return pixels;
    }

    void DisposeSeen()
    {
        // a worker still copying out keeps it (the process is ending)
        if (Interlocked.CompareExchange(ref _seenBusy, 1, 0) == 0) _seen?.Dispose();
        _seen = null;
    }
}
