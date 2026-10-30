using Silk.NET.Vulkan;

namespace Sojourn.Render.Gpu;

/// <summary>Per-vertex joints and weights of skinned meshes, read by the skinning pre-pass (skin.comp).</summary>
public struct SkinWeight
{
    public uint Joints;       // four palette indices, one byte each (joint 0 in the low byte)
    public uint W01, W23;     // four unorm16 weights (packUnorm2x16 order: low half first)
    public uint Pad;

    /// <summary>Joints and weights (summing to 1) packed for the pre-pass. The rounded weights are made to sum to exactly
    /// 65535, the largest taking up the rounding: the pre-pass blends region-space matrices, so a sum off by a few
    /// 65535ths scaled every blended vertex toward the region's origin, centimetres at a skybox's height (vertices
    /// dripping down at a 2 km club, 2026-10-28).</summary>
    public static SkinWeight From(ReadOnlySpan<int> joints, ReadOnlySpan<float> weights)
    {
        Span<uint> j = stackalloc uint[4];
        Span<uint> w = stackalloc uint[4];
        int largest = 0;
        for (int i = 0; i < 4 && i < joints.Length; i++)
        {
            j[i] = (uint)Math.Clamp(joints[i], 0, 255);
            w[i] = (uint)Math.Clamp(MathF.Round(weights[i] * 65535f), 0, 65535);
            if (w[i] > w[largest]) largest = i;
        }
        uint others = 0;
        for (int i = 0; i < 4; i++) if (i != largest) others += w[i];
        w[largest] = others >= 65535 ? 0 : 65535 - others;
        return new SkinWeight
        {
            Joints = j[0] | j[1] << 8 | j[2] << 16 | j[3] << 24,
            W01 = w[0] | w[1] << 16,
            W23 = w[2] | w[3] << 16,
        };
    }
}

/// <summary>Storage arenas for skinning inputs (bind-pose vertices, weights), read by the skinning pre-pass through
/// device addresses; allocated like the geometry pool's arenas.</summary>
public sealed class StoragePool(VulkanContext vk, Uploader up, ulong stride, ulong arenaBytes = 32UL << 20) : IDisposable
{
    readonly List<(GpuBuffer Buffer, RangeAllocator Alloc)> _arenas = new();

    public ulong Bytes => (ulong)_arenas.Count * arenaBytes;
    public ulong UsedBytes => _arenas.Aggregate(0UL, (s, a) => s + a.Alloc.Used * stride);

    /// <summary>Upload elements (their size must be the pool's stride); returns their device address and where
    /// they live for <see cref="Free"/>.</summary>
    public StorageRange Add<T>(ReadOnlySpan<T> items) where T : unmanaged
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(items);
        ulong count = (ulong)bytes.Length / stride;
        if ((ulong)bytes.Length > arenaBytes) throw new InvalidOperationException("storage block larger than an arena");
        for (int pass = 0; pass < 2; pass++)
        {
            for (int ai = 0; ai < _arenas.Count; ai++)
            {
                var (buf, alloc) = _arenas[ai];
                if (!alloc.TryAlloc(count, 1, out var off)) continue;
                up.CopyToBuffer(bytes, buf.Handle, off * stride);
                return new StorageRange(buf.Address + off * stride, ai, off, count);
            }
            _arenas.Add((new GpuBuffer(vk, arenaBytes, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryUse.GpuOnly),
                new RangeAllocator(arenaBytes / stride)));
        }
        throw new InvalidOperationException("storage allocation failed");
    }

    public void Free(StorageRange r)
    {
        if (r.Count == 0) return;
        _arenas[r.Arena].Alloc.Free(r.Offset, r.Count);
    }

    public void Dispose()
    {
        foreach (var (b, _) in _arenas) b.Dispose();
        _arenas.Clear();
    }
}

public readonly record struct StorageRange(ulong Address, int Arena, ulong Offset, ulong Count);
