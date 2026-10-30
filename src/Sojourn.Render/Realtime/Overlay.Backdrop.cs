using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Realtime;

/// <summary>
/// The world behind the overlay, small, for choosing ink by it (the client's Assistant/AdaptiveInk.cs): the first eye's
/// image as the frost's deepest mip (each texel the average of a patch a few degrees across; 32x34 for a Quest 3 eye,
/// 30x16 for a 1080p window), as linear luminance row by row from the top. With the view-projection and the eye image's
/// size it was taken with, and the scene it was taken under, so a sheet's patch is found where the overlay placed it
/// (its corners, or its pixels when flat). Number counts the samples.
/// </summary>
public sealed record BackdropSample(float[] Luminance, int Width, int Height, Matrix4x4 ViewProj, Vector2 Target, OverlayScene Scene, long Number);

public sealed unsafe partial class Overlay
{
    /// <summary>The latest sample of the world behind (<see cref="OverlayScene.SampleBackdrop"/>), published by its
    /// frame's retirement once the GPU is done with it: nothing waits for it.</summary>
    public BackdropSample? Sampled => _sampled;
    volatile BackdropSample? _sampled;
    GpuBuffer?[]? _sampleBuffers;   // a readback buffer per frame slot
    int _sampleTick;
    long _samples;
    bool _sampleThisEye;
    // every third frame: the choice it feeds is smoothed over a quarter second anyway
    const int SampleEvery = 3;

    static readonly float[] Linear = Enumerable.Range(0, 256).Select(i =>
    {
        float s = i / 255f;
        return s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
    }).ToArray();

    /// <summary>A sample is due this eye: the first eye, asked for, every few frames. It needs the backdrop's mips,
    /// which a frosted sheet builds anyway; without one they're built for it.</summary>
    void WantSample(FrameResources fr, GpuImage target, int eye, OverlayScene scene)
    {
        _sampleThisEye = eye == 0 && scene.SampleBackdrop && _sampleTick++ % SampleEvery == 0;
        if (_sampleThisEye) NeedBackdrop(fr, target);
    }

    /// <summary>The backdrop's deepest mip (shader-readable, left so) copied to the slot's readback buffer; read and
    /// published when the frame is done.</summary>
    void RecordSample(FrameResources fr, CommandBuffer cmd, int slot, OverlayScene scene, Matrix4x4 viewProj, GpuImage target)
    {
        var api = _vk.Api;
        var b = _backdrop;
        if (b.Layout != ImageLayout.ShaderReadOnlyOptimal || b.Mips == 0) return;
        uint mip = b.Mips - 1;
        int w = (int)Math.Max(1, b.Width >> (int)mip), h = (int)Math.Max(1, b.Height >> (int)mip);
        ulong bytes = (ulong)(w * h * 4);
        _sampleBuffers ??= new GpuBuffer?[_sets.Length];
        if (_sampleBuffers[slot] is not { } buf || buf.Size < bytes)
        {
            if (_sampleBuffers[slot] is { } old) fr.Retire.Add(old.Dispose);
            _sampleBuffers[slot] = buf = new GpuBuffer(_vk, Math.Max(bytes, 16384), BufferUsageFlags.TransferDstBit, MemoryUse.Readback);
        }
        GpuImage.Barrier(_vk, cmd, b.Handle, b.Aspect, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal, mip, 1, 0, 1);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip, 0, 1),
            ImageExtent = new Extent3D((uint)w, (uint)h, 1),
        };
        api.CmdCopyImageToBuffer(cmd, b.Handle, ImageLayout.TransferSrcOptimal, buf.Handle, 1, in copy);
        GpuImage.Barrier(_vk, cmd, b.Handle, b.Aspect, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, mip, 1, 0, 1);
        var hb = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit, DstAccessMask = AccessFlags2.HostReadBit,
        };
        var di = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &hb };
        api.CmdPipelineBarrier2(cmd, in di);
        var size = new Vector2(target.Width, target.Height);
        fr.Retire.Add(() =>
        {
            var px = new ReadOnlySpan<byte>((void*)buf.Alloc.Mapped, w * h * 4);
            var lum = new float[w * h];
            for (int i = 0; i < lum.Length; i++)
                lum[i] = 0.2126f * Linear[px[i * 4]] + 0.7152f * Linear[px[i * 4 + 1]] + 0.0722f * Linear[px[i * 4 + 2]];
            _sampled = new BackdropSample(lum, w, h, viewProj, size, scene, Interlocked.Increment(ref _samples));
        });
    }

    void DisposeSamples()
    {
        if (_sampleBuffers == null) return;
        foreach (var b in _sampleBuffers) b?.Dispose();
    }
}
