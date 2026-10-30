using Silk.NET.Vulkan;

namespace Sojourn.Render.Gpu;

/// <summary>First-fit range allocator over [0, capacity) with coalescing frees.</summary>
public sealed class RangeAllocator
{
    readonly List<(ulong Off, ulong Size)> _free = new();
    public ulong Capacity { get; }
    public ulong Used { get; private set; }

    public RangeAllocator(ulong capacity)
    {
        Capacity = capacity;
        _free.Add((0, capacity));
    }

    public bool TryAlloc(ulong size, ulong align, out ulong offset)
    {
        for (int i = 0; i < _free.Count; i++)
        {
            var (fo, fs) = _free[i];
            ulong a = (fo + align - 1) / align * align;
            ulong pad = a - fo;
            if (fs < pad + size) continue;
            _free.RemoveAt(i);
            if (pad > 0) _free.Insert(i++, (fo, pad));
            if (fs - pad - size > 0) _free.Insert(i, (a + size, fs - pad - size));
            Used += size;
            offset = a;
            return true;
        }
        offset = 0;
        return false;
    }

    public void Free(ulong offset, ulong size)
    {
        Used -= size;
        int i = 0;
        while (i < _free.Count && _free[i].Off < offset) i++;
        _free.Insert(i, (offset, size));
        if (i + 1 < _free.Count && _free[i].Off + _free[i].Size == _free[i + 1].Off)
        {
            _free[i] = (_free[i].Off, _free[i].Size + _free[i + 1].Size);
            _free.RemoveAt(i + 1);
        }
        if (i > 0 && _free[i - 1].Off + _free[i - 1].Size == _free[i].Off)
        {
            _free[i - 1] = (_free[i - 1].Off, _free[i - 1].Size + _free[i].Size);
            _free.RemoveAt(i);
        }
    }
}

/// <summary>Where one mesh's vertices and indices live: arena, first vertex, first index, counts.</summary>
public readonly record struct MeshRange(int Arena, uint FirstVertex, uint VertexCount, uint FirstIndex, uint IndexCount)
{
    public bool IsEmpty => IndexCount == 0;
    /// <summary>Arena flag: vertices are in the frame's skinned vertex buffer (posed this frame), indices in the arena.</summary>
    public const int Skinned = 1 << 16;
}

/// <summary>
/// All scene geometry lives in a few big vertex/index arenas (48-byte vertices, 32-bit indices). Draws bucket by
/// arena so multi-draw-indirect can cover a whole arena with one call. A renderer with little to hold (the one out of
/// sight) takes smaller arenas (<see cref="GeometryPool(VulkanContext, Uploader, double)"/>); a mesh bigger than one
/// gets an arena of its own size.
/// </summary>
public sealed class GeometryPool : IDisposable
{
    public const ulong VertexArenaBytes = 192UL << 20;   // 4M vertices
    public const ulong IndexArenaBytes = 96UL << 20;     // 24M indices

    public sealed class Arena(GpuBuffer vb, GpuBuffer ib)
    {
        public GpuBuffer Vertices = vb, Indices = ib;
        public RangeAllocator VAlloc = new(vb.Size / PipelineBuilder.VertexStride);
        public RangeAllocator IAlloc = new(ib.Size / 4);
        public ulong Bytes => Vertices.Size + Indices.Size;
    }

    readonly VulkanContext _vk;
    readonly Uploader _up;
    readonly ulong _vertexBytes, _indexBytes;
    public readonly List<Arena> Arenas = new();
    public ulong Bytes => Arenas.Aggregate(0UL, (s, a) => s + a.Bytes);
    public ulong UsedBytes => Arenas.Aggregate(0UL, (s, a) => s + a.VAlloc.Used * PipelineBuilder.VertexStride + a.IAlloc.Used * 4);

    /// <param name="scale">each arena's size as a share of the usual (1: 4M vertices, 24M indices)</param>
    public GeometryPool(VulkanContext vk, Uploader up, double scale = 1)
    {
        _vk = vk;
        _up = up;
        scale = Math.Clamp(scale, 1.0 / 64, 1);
        _vertexBytes = (ulong)(VertexArenaBytes / PipelineBuilder.VertexStride * scale) * PipelineBuilder.VertexStride;
        _indexBytes = (ulong)(IndexArenaBytes / 4 * scale) * 4;
    }

    Arena NewArena(ulong vertexBytes, ulong indexBytes)
    {
        var vb = new GpuBuffer(_vk, Math.Max(vertexBytes, _vertexBytes), BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryUse.GpuOnly);
        var ib = new GpuBuffer(_vk, Math.Max(indexBytes, _indexBytes), BufferUsageFlags.IndexBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryUse.GpuOnly);
        var a = new Arena(vb, ib);
        Arenas.Add(a);
        return a;
    }

    /// <summary>Allocate and queue the upload of one mesh. vertices: packed MeshVertex data.</summary>
    public MeshRange Add(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices)
    {
        if (indices.Length == 0 || vertices.Length == 0) return default;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int ai = 0; ai < Arenas.Count; ai++)
            {
                var a = Arenas[ai];
                if (!a.VAlloc.TryAlloc((ulong)vertices.Length, 1, out var v)) continue;
                if (!a.IAlloc.TryAlloc((ulong)indices.Length, 1, out var i)) { a.VAlloc.Free(v, (ulong)vertices.Length); continue; }
                _up.CopyToBuffer(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertices), a.Vertices.Handle, v * PipelineBuilder.VertexStride);
                _up.CopyToBuffer(System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices), a.Indices.Handle, i * 4);
                return new MeshRange(ai, (uint)v, (uint)vertices.Length, (uint)i, (uint)indices.Length);
            }
            ulong vbytes = (ulong)vertices.Length * PipelineBuilder.VertexStride, ibytes = (ulong)indices.Length * 4;
            if (vbytes > VertexArenaBytes || ibytes > IndexArenaBytes) throw new InvalidOperationException("mesh larger than an arena");
            NewArena(vbytes, ibytes);
        }
        throw new InvalidOperationException("geometry arena allocation failed");
    }

    public void Free(MeshRange r)
    {
        if (r.IsEmpty) return;
        var a = Arenas[r.Arena];
        a.VAlloc.Free(r.FirstVertex, r.VertexCount);
        a.IAlloc.Free(r.FirstIndex, r.IndexCount);
    }

    public void Dispose()
    {
        foreach (var a in Arenas) { a.Vertices.Dispose(); a.Indices.Dispose(); }
        Arenas.Clear();
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public struct MeshVertex
{
    public System.Numerics.Vector3 Position;
    public System.Numerics.Vector3 Normal;
    public System.Numerics.Vector4 Tangent;
    public System.Numerics.Vector2 Uv;
}
