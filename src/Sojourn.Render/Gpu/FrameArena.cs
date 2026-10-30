using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Sojourn.Render.Gpu;

/// <summary>
/// Per-frame transient data: written by the CPU into a mapped upload buffer, then copied once at the start of the
/// frame's command buffer into a device-local mirror the shaders read. Bump allocation, reset every frame.
/// Grows (recreating both buffers) when a frame needs more.
/// </summary>
public sealed unsafe class FrameArena : IDisposable
{
    readonly VulkanContext _vk;
    public GpuBuffer Upload = null!;
    public GpuBuffer Device = null!;
    ulong _cursor;
    readonly ulong _align;
    public ulong Capacity => Upload.Size;
    public ulong Used => _cursor;
    public event Action? Recreated;

    const BufferUsageFlags DeviceUsage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.UniformBufferBit |
                                         BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.TransferDstBit |
                                         BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit;

    public FrameArena(VulkanContext vk, ulong size)
    {
        _vk = vk;
        _align = Math.Max(256, Math.Max(vk.Properties.Limits.MinStorageBufferOffsetAlignment, vk.Properties.Limits.MinUniformBufferOffsetAlignment));
        Create(size);
    }

    void Create(ulong size)
    {
        Upload = new GpuBuffer(_vk, size, BufferUsageFlags.TransferSrcBit, MemoryUse.Upload);
        Device = new GpuBuffer(_vk, size, DeviceUsage, MemoryUse.GpuOnly);
    }

    public void Reset() => _cursor = 0;

    /// <summary>Make sure the arena can hold this many bytes this frame. Call before any Alloc in a frame.</summary>
    public void Reserve(ulong bytes)
    {
        if (bytes <= Upload.Size) return;
        ulong n = Upload.Size;
        while (n < bytes) n *= 2;
        Upload.Dispose();
        Device.Dispose();
        Create(n);
        Recreated?.Invoke();
    }

    public Span<T> Alloc<T>(int count, out ulong offset) where T : unmanaged
    {
        ulong bytes = (ulong)(count * sizeof(T));
        ulong off = (_cursor + _align - 1) / _align * _align;
        if (off + bytes > Upload.Size) throw new InvalidOperationException($"frame arena overflow ({off + bytes} > {Upload.Size}); Reserve more");
        _cursor = off + bytes;
        offset = off;
        return new Span<T>((byte*)Upload.Alloc.Mapped + off, count);
    }

    public ulong Write<T>(in T value) where T : unmanaged
    {
        var span = Alloc<T>(1, out var off);
        span[0] = value;
        return off;
    }

    public ulong Write<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        var span = Alloc<T>(Math.Max(1, values.Length), out var off);
        values.CopyTo(span);
        return off;
    }

    /// <summary>Copy everything written so far to the device mirror and make it visible to all later stages.</summary>
    public void Flush(CommandBuffer cmd)
    {
        if (_cursor == 0) return;
        var region = new BufferCopy(0, 0, _cursor);
        _vk.Api.CmdCopyBuffer(cmd, Upload.Handle, Device.Handle, 1, in region);
        var b = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = AccessFlags2.ShaderReadBit | AccessFlags2.UniformReadBit | AccessFlags2.IndirectCommandReadBit |
                            AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit,
        };
        var di = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &b };
        _vk.Api.CmdPipelineBarrier2(cmd, in di);
    }

    public Buffer DeviceBuffer => Device.Handle;

    public void Dispose()
    {
        Upload.Dispose();
        Device.Dispose();
    }
}
