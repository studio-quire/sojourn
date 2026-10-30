using Silk.NET.Vulkan;

namespace Sojourn.Render.Gpu;

public readonly record struct GpuAllocation(DeviceMemory Memory, ulong Offset, ulong Size, int Block, int Type, nint Mapped)
{
    public bool IsNull => Memory.Handle == 0;
}

/// <summary>
/// Block sub-allocator. Each memory type gets 64 MiB blocks carved with a first-fit free list; requests above a
/// quarter block get their own dedicated allocation. Linear (buffer) and optimal (image) resources use separate
/// pools so bufferImageGranularity never matters. Host-visible blocks are persistently mapped.
/// </summary>
public sealed unsafe class GpuAllocator : IDisposable
{
    const ulong BlockSize = 64UL << 20;

    sealed class Block
    {
        public DeviceMemory Memory;
        public ulong Size;
        public nint Mapped;
        public bool Dedicated;
        public bool Linear;
        public int Type;
        public List<(ulong Offset, ulong Size)> Free = new();
        public ulong Used;
    }

    readonly VulkanContext _vk;
    readonly List<Block?> _blocks = new();
    readonly object _lock = new();
    public ulong TotalAllocated { get; private set; }
    public ulong TotalUsed { get; private set; }
    /// <summary>Of TotalAllocated, the host-visible (persistently mapped) part.</summary>
    public ulong HostVisibleAllocated { get; private set; }
    public int Blocks { get { lock (_lock) return _blocks.Count(b => b != null); } }

    public GpuAllocator(VulkanContext vk) => _vk = vk;

    public int FindType(uint typeBits, MemoryPropertyFlags required, MemoryPropertyFlags preferred = 0)
    {
        var mp = _vk.MemoryProperties;
        for (int pass = 0; pass < 2; pass++)
        {
            var want = pass == 0 ? required | preferred : required;
            for (int i = 0; i < mp.MemoryTypeCount; i++)
                if ((typeBits & (1u << i)) != 0 && (mp.MemoryTypes[i].PropertyFlags & want) == want)
                    return i;
        }
        throw new InvalidOperationException($"No memory type for bits {typeBits:x} flags {required}");
    }

    public GpuAllocation Allocate(MemoryRequirements req, MemoryPropertyFlags required, MemoryPropertyFlags preferred, bool linear, bool dedicated = false)
    {
        int type = FindType(req.MemoryTypeBits, required, preferred);
        bool hostVisible = (_vk.MemoryProperties.MemoryTypes[type].PropertyFlags & MemoryPropertyFlags.HostVisibleBit) != 0;
        lock (_lock)
        {
            if (dedicated || req.Size > BlockSize / 4)
            {
                var b = NewBlock(type, req.Size, hostVisible, linear, true);
                b.Used = req.Size;
                TotalUsed += req.Size;
                return new GpuAllocation(b.Memory, 0, req.Size, _blocks.IndexOf(b), type, b.Mapped);
            }
            for (int bi = 0; bi < _blocks.Count; bi++)
            {
                var b = _blocks[bi];
                if (b == null || b.Dedicated || b.Type != type || b.Linear != linear) continue;
                if (TryCarve(b, req.Size, req.Alignment, out var off))
                    return new GpuAllocation(b.Memory, off, req.Size, bi, type, b.Mapped == 0 ? 0 : b.Mapped + (nint)off);
            }
            var nb = NewBlock(type, BlockSize, hostVisible, linear, false);
            if (!TryCarve(nb, req.Size, req.Alignment, out var o)) throw new InvalidOperationException("carve failed");
            return new GpuAllocation(nb.Memory, o, req.Size, _blocks.IndexOf(nb), type, nb.Mapped == 0 ? 0 : nb.Mapped + (nint)o);
        }
    }

    bool TryCarve(Block b, ulong size, ulong align, out ulong offset)
    {
        for (int i = 0; i < b.Free.Count; i++)
        {
            var (fo, fs) = b.Free[i];
            ulong aligned = (fo + align - 1) / align * align;
            ulong pad = aligned - fo;
            if (fs < pad + size) continue;
            b.Free.RemoveAt(i);
            if (pad > 0) b.Free.Insert(i++, (fo, pad));
            ulong rest = fs - pad - size;
            if (rest > 0) b.Free.Insert(i, (aligned + size, rest));
            b.Used += size;
            TotalUsed += size;
            offset = aligned;
            return true;
        }
        offset = 0;
        return false;
    }

    Block NewBlock(int type, ulong size, bool hostVisible, bool linear, bool dedicated)
    {
        var flagsInfo = new MemoryAllocateFlagsInfo { SType = StructureType.MemoryAllocateFlagsInfo, Flags = linear ? MemoryAllocateFlags.DeviceAddressBit : 0 };
        var ai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, PNext = &flagsInfo, AllocationSize = size, MemoryTypeIndex = (uint)type };
        var r = _vk.Api.AllocateMemory(_vk.Device, in ai, null, out var mem);
        if (r != Result.Success) throw new VulkanException($"vkAllocateMemory({size >> 20} MiB)", r);
        var b = new Block { Memory = mem, Size = size, Type = type, Dedicated = dedicated, Linear = linear };
        if (!dedicated) b.Free.Add((0, size));
        if (hostVisible)
        {
            void* p;
            VulkanContext.Check(_vk.Api.MapMemory(_vk.Device, mem, 0, size, 0, &p), "vkMapMemory");
            b.Mapped = (nint)p;
        }
        int slot = _blocks.IndexOf(null);
        if (slot < 0) _blocks.Add(b); else _blocks[slot] = b;
        TotalAllocated += size;
        if (hostVisible) HostVisibleAllocated += size;
        return b;
    }

    public void Free(GpuAllocation a)
    {
        if (a.IsNull) return;
        lock (_lock)
        {
            var b = _blocks[a.Block] ?? throw new InvalidOperationException("double free");
            TotalUsed -= a.Size;
            b.Used -= a.Size;
            if (b.Dedicated)
            {
                Release(a.Block, b);
                return;
            }
            // insert sorted, coalesce with neighbours
            int i = 0;
            while (i < b.Free.Count && b.Free[i].Offset < a.Offset) i++;
            b.Free.Insert(i, (a.Offset, a.Size));
            if (i + 1 < b.Free.Count && b.Free[i].Offset + b.Free[i].Size == b.Free[i + 1].Offset)
            {
                b.Free[i] = (b.Free[i].Offset, b.Free[i].Size + b.Free[i + 1].Size);
                b.Free.RemoveAt(i + 1);
            }
            if (i > 0 && b.Free[i - 1].Offset + b.Free[i - 1].Size == b.Free[i].Offset)
            {
                b.Free[i - 1] = (b.Free[i - 1].Offset, b.Free[i - 1].Size + b.Free[i].Size);
                b.Free.RemoveAt(i);
            }
            // keep one empty block per type around; release the rest
            if (b.Used == 0 && _blocks.Count(x => x != null && !x.Dedicated && x.Type == b.Type && x.Linear == b.Linear) > 1)
                Release(a.Block, b);
        }
    }

    void Release(int index, Block b)
    {
        if (b.Mapped != 0)
        {
            _vk.Api.UnmapMemory(_vk.Device, b.Memory);
            HostVisibleAllocated -= b.Size;
        }
        _vk.Api.FreeMemory(_vk.Device, b.Memory, null);
        TotalAllocated -= b.Size;
        _blocks[index] = null;
    }

    public void Dispose()
    {
        lock (_lock)
            for (int i = 0; i < _blocks.Count; i++)
                if (_blocks[i] is { } b) Release(i, b);
    }
}
