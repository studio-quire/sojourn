using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Sojourn.Render.Gpu;

/// <summary>
/// Batches CPU-to-GPU copies (mesh data, texture mip chains) through a staging ring buffer. Copies staged since
/// the last Record are recorded at the start of the next submitted command buffer; the staging space they use is
/// released when that frame's fence signals (frames complete in submission order). Render thread only.
/// </summary>
public sealed unsafe class Uploader : IDisposable
{
    readonly VulkanContext _vk;
    GpuBuffer _ring;
    ulong _head, _tail;          // write position, oldest byte still owned by an in-flight frame
    bool _full;                  // head caught up with tail
    ulong _generation;           // bumped by FlushNow so marks from before it are ignored
    readonly List<Action<CommandBuffer, Buffer>> _ops = new();
    public ulong PendingBytes { get; private set; }
    /// <summary>What the last <see cref="Record"/> put in its command buffer (staged bytes), for weighing its GPU time.</summary>
    public ulong RecordedBytes { get; private set; }
    public bool HasPending => _ops.Count > 0;

    public Uploader(VulkanContext vk, ulong size = 64UL << 20)
    {
        _vk = vk;
        _ring = new GpuBuffer(vk, size, BufferUsageFlags.TransferSrcBit, MemoryUse.Upload);
    }

    /// <summary>Reserve staging space; returns the span to fill and its offset.</summary>
    public Span<byte> Stage(ulong bytes, out ulong offset)
    {
        bytes = (bytes + 15) & ~15UL;
        if (bytes > _ring.Size)
        {
            // bigger than the whole ring: drain everything and grow
            FlushNow();
            ulong n = _ring.Size;
            while (n < bytes) n *= 2;
            _ring.Dispose();
            _ring = new GpuBuffer(_vk, n, BufferUsageFlags.TransferSrcBit, MemoryUse.Upload);
            _head = _tail = 0;
            _full = false;
        }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!_full)
            {
                if (_head >= _tail)
                {
                    if (_ring.Size - _head >= bytes) return Take(bytes, out offset);
                    if (_tail > bytes) { _head = 0; return Take(bytes, out offset); }   // wrap (skip the tail end)
                }
                else if (_tail - _head >= bytes) return Take(bytes, out offset);
            }
            FlushNow();   // ring exhausted: submit what's staged and wait for every frame
        }
        throw new InvalidOperationException("staging ring allocation failed");
    }

    Span<byte> Take(ulong bytes, out ulong offset)
    {
        offset = _head;
        _head += bytes;
        if (_head == _ring.Size) _head = 0;
        if (_head == _tail) _full = true;
        PendingBytes += bytes;
        return new Span<byte>((byte*)_ring.Alloc.Mapped + offset, (int)bytes);
    }

    public void CopyToBuffer(ReadOnlySpan<byte> data, Buffer dst, ulong dstOffset)
    {
        if (data.Length == 0) return;
        var span = Stage((ulong)data.Length, out var off);
        data.CopyTo(span);
        ulong size = (ulong)data.Length;
        _ops.Add((cmd, src) =>
        {
            var region = new BufferCopy(off, dstOffset, size);
            _vk.Api.CmdCopyBuffer(cmd, src, dst, 1, in region);
        });
    }

    /// <summary>Upload a full mip chain (mips[0] largest, tightly packed) into an image.</summary>
    public void CopyToImage(GpuImage image, IReadOnlyList<byte[]> mips, uint layer = 0)
    {
        var offsets = new ulong[mips.Count];
        for (int i = 0; i < mips.Count; i++)
        {
            var span = Stage((ulong)mips[i].Length, out offsets[i]);
            mips[i].CopyTo(span);
        }
        uint w = image.Width, h = image.Height;
        int count = mips.Count;
        _ops.Add((cmd, src) =>
        {
            GpuImage.Barrier(_vk, cmd, image.Handle, image.Aspect, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, image.Mips, layer, 1);
            var regions = stackalloc BufferImageCopy[count];
            uint mw = w, mh = h;
            for (int i = 0; i < count; i++)
            {
                regions[i] = new BufferImageCopy
                {
                    BufferOffset = offsets[i],
                    ImageSubresource = new ImageSubresourceLayers(image.Aspect, (uint)i, layer, 1),
                    ImageExtent = new Extent3D(mw, mh, 1),
                };
                mw = Math.Max(1, mw / 2); mh = Math.Max(1, mh / 2);
            }
            _vk.Api.CmdCopyBufferToImage(cmd, src, image.Handle, ImageLayout.TransferDstOptimal, (uint)count, regions);
            GpuImage.Barrier(_vk, cmd, image.Handle, image.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, 0, image.Mips, layer, 1);
        });
        image.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    /// <summary>Upload level 0 (tightly packed RGBA8) and build the rest of the chain with linear blits
    /// (box-filtered like GL's glGenerateMipmap, which the viewer relies on).</summary>
    public void CopyToImageWithMips(GpuImage image, byte[] level0) => CopyToImageWithMips(image, (ReadOnlySpan<byte>)level0);

    public void CopyToImageWithMips(GpuImage image, ReadOnlySpan<byte> level0)
    {
        var span = Stage((ulong)level0.Length, out var off);
        level0.CopyTo(span);
        _ops.Add((cmd, src) =>
        {
            var api = _vk.Api;
            GpuImage.Barrier(_vk, cmd, image.Handle, image.Aspect, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, image.Mips, 0, 1);
            var region = new BufferImageCopy
            {
                BufferOffset = off,
                ImageSubresource = new ImageSubresourceLayers(image.Aspect, 0, 0, 1),
                ImageExtent = new Extent3D(image.Width, image.Height, 1),
            };
            api.CmdCopyBufferToImage(cmd, src, image.Handle, ImageLayout.TransferDstOptimal, 1, in region);
            int mw = (int)image.Width, mh = (int)image.Height;
            for (uint m = 1; m < image.Mips; m++)
            {
                GpuImage.Barrier(_vk, cmd, image.Handle, image.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, m - 1, 1, 0, 1);
                int nw = Math.Max(1, mw / 2), nh = Math.Max(1, mh / 2);
                var blit = new ImageBlit
                {
                    SrcSubresource = new ImageSubresourceLayers(image.Aspect, m - 1, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(image.Aspect, m, 0, 1),
                };
                blit.SrcOffsets[1] = new Offset3D(mw, mh, 1);
                blit.DstOffsets[1] = new Offset3D(nw, nh, 1);
                api.CmdBlitImage(cmd, image.Handle, ImageLayout.TransferSrcOptimal, image.Handle, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
                GpuImage.Barrier(_vk, cmd, image.Handle, image.Aspect, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, m - 1, 1, 0, 1);
                mw = nw; mh = nh;
            }
            GpuImage.Barrier(_vk, cmd, image.Handle, image.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, image.Mips - 1, 1, 0, 1);
        });
        image.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    /// <summary>Any transfer work to record with the next frame's copies, in order (media textures' updates).</summary>
    public void Record(Action<CommandBuffer> op) => _ops.Add((cmd, _) => op(cmd));

    /// <summary>An op that copies from what <see cref="Stage"/> gave out: it's handed the staging buffer.</summary>
    public void RecordStaged(Action<CommandBuffer, Buffer> op) => _ops.Add(op);

    /// <summary>Record all pending copies into cmd. Returns the ring mark to Release when cmd's frame completes.</summary>
    public ulong Record(CommandBuffer cmd)
    {
        if (_ops.Count > 0)
        {
            foreach (var op in _ops) op(cmd, _ring.Handle);
            _ops.Clear();
            GpuImage.MemoryBarrier(_vk, cmd);
        }
        RecordedBytes = PendingBytes;
        PendingBytes = 0;
        return (_generation << 40) | _head;
    }

    /// <summary>A frame recorded at this mark has completed: its staging space is free again.</summary>
    public void Release(ulong mark)
    {
        if (mark >> 40 != _generation) return;
        _tail = mark & ((1UL << 40) - 1);
        _full = false;
    }

    /// <summary>Submit pending copies synchronously and wait for the device (setup, or when the ring runs out).</summary>
    public void FlushNow()
    {
        _vk.WaitIdle();   // every in-flight frame is done with its staging space
        if (_ops.Count > 0) _vk.Submit(cmd => Record(cmd));
        _generation++;
        _head = _tail = 0;
        _full = false;
        PendingBytes = 0;
    }

    public void Dispose() => _ring.Dispose();
}
