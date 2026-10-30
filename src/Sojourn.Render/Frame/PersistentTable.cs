using Silk.NET.Vulkan;
using Sojourn.Render.Gpu;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Sojourn.Render.Frame;

/// <summary>
/// Instances or materials kept on the GPU across frames (docs/performance.md > Persistent instances and materials): a
/// device-local table of T, a slot each, written only when what's in a slot changes. Writes made while a frame's draw
/// list is built go into that frame's arena and are copied into place at the start of its commands, after the earlier
/// frames' reads and before anything of this frame draws. Draws (and instances' material indices) name a slot of the
/// table with <see cref="Bit"/> set; without it an index is the frame's own (the arena's), as before. A slot let go is
/// given out again only <see cref="FreeDelay"/> frames on, so a draw list kept over several frames (a probe capture's,
/// rebuilt every six) never draws a slot handed to something else. Alloc, Write and Release may come from the scene
/// workers building a view's parts at once (locked; they come only as objects are first drawn, move or change);
/// Record is the render thread's, with nothing building.
/// </summary>
public sealed unsafe class PersistentTable<T> : IDisposable where T : unmanaged
{
    public const uint Bit = 0x40000000u;
    const int FreeDelay = 16;

    readonly VulkanContext _vk;
    readonly Action<Action> _retire;
    GpuBuffer _buffer;
    int _capacity, _next;
    readonly Stack<int> _free = new();
    readonly Queue<(long Frame, int Slot)> _released = new();
    long _frame;
    // this frame's writes (the last of a slot's wins); the buffer whose contents are still to be copied over after growing
    readonly List<(int Slot, T Value)> _writes = new();
    readonly Dictionary<int, int> _writeAt = new();
    GpuBuffer? _old;
    int _oldUsed;
    // what each slot holds, for the CPU's own reads (Renderer.SplitCasters reads materials' flags)
    T[] _cpu;

    public PersistentTable(VulkanContext vk, int capacity, Action<Action> retire)
    {
        _vk = vk;
        _retire = retire;
        _capacity = capacity;
        _buffer = Create(capacity);
        _cpu = new T[capacity];
    }

    /// <summary>What a slot holds (as last written). Render thread, with nothing building.</summary>
    public ref readonly T this[int slot] => ref _cpu[slot];

    GpuBuffer Create(int capacity) =>
        new(_vk, (ulong)capacity * (ulong)sizeof(T), BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit, MemoryUse.GpuOnly);

    /// <summary>The table's device address (the push block's: it moves only when the table grows, between frames).</summary>
    public ulong Address => _buffer.Address;
    /// <summary>Slots in use, and the table's size in bytes.</summary>
    public int Live => _next - _free.Count - _released.Count;
    public ulong Bytes => _buffer.Size;
    /// <summary>Slots written this frame so far (the arena bytes <see cref="Record"/> needs: <see cref="PendingBytes"/>).</summary>
    public int Writes => _writes.Count;
    public ulong PendingBytes => (ulong)(_writes.Count * sizeof(T)) + 512;

    readonly object _lock = new();

    public int Alloc()
    {
        lock (_lock)
        {
            if (_free.Count > 0) return _free.Pop();
            if (_next == _capacity) Grow();
            return _next++;
        }
    }

    /// <summary>Let a slot go (it's reused <see cref="FreeDelay"/> frames on).</summary>
    public void Release(int slot)
    {
        if (slot < 0) return;
        lock (_lock) _released.Enqueue((_frame, slot));
    }

    public void Write(int slot, in T value)
    {
        lock (_lock)
        {
            _cpu[slot] = value;
            if (_writeAt.TryGetValue(slot, out int i)) _writes[i] = (slot, value);
            else
            {
                _writeAt[slot] = _writes.Count;
                _writes.Add((slot, value));
            }
        }
    }

    void Grow()
    {
        int capacity = _capacity * 2;
        var bigger = Create(capacity);
        if (_old == null)
        {
            // the GPU's copy of what's been written so far is in the current buffer: copied over at the next Record
            (_old, _oldUsed) = (_buffer, _next);
        }
        else
        {
            // grown twice before a frame recorded: the one in between never held anything
            var unused = _buffer;
            _retire(unused.Dispose);
        }
        _buffer = bigger;
        _capacity = capacity;
        Array.Resize(ref _cpu, capacity);
    }

    /// <summary>At the start of a frame's commands, before anything draws: a grown table's old contents, then this
    /// frame's writes, from the arena's upload side. The arena must have room for <see cref="PendingBytes"/>.</summary>
    public void Record(CommandBuffer cmd, FrameArena arena)
    {
        _frame++;
        while (_released.Count > 0 && _released.Peek().Frame <= _frame - FreeDelay) _free.Push(_released.Dequeue().Slot);
        if (_old == null && _writes.Count == 0) return;
        var api = _vk.Api;
        // the earlier frames' draws are done reading what's overwritten (one queue: the barrier covers everything before)
        var war = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit, SrcAccessMask = AccessFlags2.ShaderReadBit | AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.CopyBit, DstAccessMask = AccessFlags2.TransferWriteBit | AccessFlags2.TransferReadBit,
        };
        var di = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &war };
        api.CmdPipelineBarrier2(cmd, in di);
        if (_old != null)
        {
            if (_oldUsed > 0)
            {
                var all = new BufferCopy(0, 0, (ulong)_oldUsed * (ulong)sizeof(T));
                api.CmdCopyBuffer(cmd, _old.Handle, _buffer.Handle, 1, in all);
            }
            var old = _old;
            _retire(old.Dispose);
            _old = null;
            if (_writes.Count > 0)
            {
                // the copy lands before the writes over it
                var waw = new MemoryBarrier2
                {
                    SType = StructureType.MemoryBarrier2,
                    SrcStageMask = PipelineStageFlags2.CopyBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
                    DstStageMask = PipelineStageFlags2.CopyBit, DstAccessMask = AccessFlags2.TransferWriteBit,
                };
                var dw = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &waw };
                api.CmdPipelineBarrier2(cmd, in dw);
            }
        }
        if (_writes.Count > 0)
        {
            // in slot order, so neighbours go as one region
            _writes.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
            var values = arena.Alloc<T>(_writes.Count, out ulong offset);
            var regions = new BufferCopy[_writes.Count];
            int nr = 0;
            ulong size = (ulong)sizeof(T);
            for (int i = 0; i < _writes.Count; i++)
            {
                values[i] = _writes[i].Value;
                ulong src = offset + (ulong)i * size, dst = (ulong)_writes[i].Slot * size;
                if (nr > 0 && regions[nr - 1].SrcOffset + regions[nr - 1].Size == src && regions[nr - 1].DstOffset + regions[nr - 1].Size == dst)
                    regions[nr - 1].Size += size;
                else regions[nr++] = new BufferCopy(src, dst, size);
            }
            fixed (BufferCopy* r = regions) api.CmdCopyBuffer(cmd, arena.Upload.Handle, _buffer.Handle, (uint)nr, r);
            _writes.Clear();
            _writeAt.Clear();
        }
        var raw = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit, DstAccessMask = AccessFlags2.ShaderReadBit,
        };
        var dr = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &raw };
        api.CmdPipelineBarrier2(cmd, in dr);
    }

    public void Dispose()
    {
        _old?.Dispose();
        _buffer.Dispose();
    }
}
