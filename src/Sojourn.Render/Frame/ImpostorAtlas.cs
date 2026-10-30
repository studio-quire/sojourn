using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

/// <summary>A tile of the impostor atlas: its size class, index within the class, and pixel rectangle (half as wide as
/// tall: avatars are tall).</summary>
public readonly record struct ImpostorTile(int Class, int Index, int X, int Y, int W, int H)
{
    public bool IsEmpty => W == 0;
}

/// <summary>
/// Where avatar impostors live (World/Avatars.Impostors.cs): one atlas with the G-buffer's own formats (diffuse,
/// specular, normal, emissive) and a depth, so an impostor is written into the scene's G-buffer pixel by pixel and lit
/// with everything else. Cut into tiles of three heights: 16 of 256x512 (an avatar a few metres away in the headset),
/// 32 of 128x256 and 128 of 64x128. A tile is chosen by the avatar's height on screen; when a size runs out the next
/// smaller one is taken. Made on first use (~117 MB), so places without crowds pay nothing.
/// </summary>
public sealed class ImpostorAtlas : IDisposable
{
    public const int Size = 2048;
    static readonly (int H, int Y0, int Rows)[] Classes = { (512, 0, 2), (256, 1024, 2), (128, 1536, 4) };

    readonly VulkanContext _vk;
    readonly Bindless _bindless;
    public GpuImage Diffuse, Specular, Normal, Emissive, Depth;
    public int DiffuseSlot, SpecularSlot, NormalSlot, EmissiveSlot, DepthSlot;
    readonly Stack<int>[] _free;

    public ImpostorAtlas(VulkanContext vk, Bindless bindless)
    {
        _vk = vk;
        _bindless = bindless;
        const ImageUsageFlags rt = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit;
        Diffuse = new GpuImage(vk, Size, Size, RenderTargets.DiffuseFormat, rt, dedicated: true);
        Specular = new GpuImage(vk, Size, Size, RenderTargets.SpecularFormat, rt, dedicated: true);
        Normal = new GpuImage(vk, Size, Size, RenderTargets.NormalFormat, rt, dedicated: true);
        Emissive = new GpuImage(vk, Size, Size, RenderTargets.EmissiveFormat, rt, dedicated: true);
        Depth = new GpuImage(vk, Size, Size, RenderTargets.DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, dedicated: true);
        DiffuseSlot = Register(Diffuse); SpecularSlot = Register(Specular); NormalSlot = Register(Normal);
        EmissiveSlot = Register(Emissive); DepthSlot = Register(Depth);
        _free = Classes.Select(c => new Stack<int>(Enumerable.Range(0, Count(c)).Reverse())).ToArray();
    }

    public ulong Bytes => Diffuse.Bytes + Specular.Bytes + Normal.Bytes + Emissive.Bytes + Depth.Bytes;

    int Register(GpuImage img)
    {
        int slot = _bindless.AllocTexture();
        _bindless.Write(slot, img.View, _bindless.Nearest);
        return slot;
    }

    static int Count((int H, int Y0, int Rows) c) => Size / (c.H / 2) * c.Rows;

    /// <summary>The tile height that suits an impostor about <paramref name="pixels"/> tall on screen: the smallest
    /// at least as tall, at most 512.</summary>
    public static int ClassFor(float pixels)
    {
        for (int c = Classes.Length - 1; c >= 0; c--)
            if (Classes[c].H >= pixels) return c;
        return 0;
    }

    public static int HeightOf(int cls) => Classes[cls].H;

    /// <summary>A free tile of this class, or the next smaller one with room; false if none is left.</summary>
    public bool Allocate(int cls, out ImpostorTile tile)
    {
        for (int c = cls; c < Classes.Length; c++)
            if (_free[c].TryPop(out int i))
            {
                var (h, y0, _) = Classes[c];
                int w = h / 2, across = Size / w;
                tile = new ImpostorTile(c, i, (i % across) * w, y0 + (i / across) * h, w, h);
                return true;
            }
        tile = default;
        return false;
    }

    public void Free(ImpostorTile tile)
    {
        if (!tile.IsEmpty) _free[tile.Class].Push(tile.Index);
    }

    public int FreeTiles => _free.Sum(f => f.Count);

    public void Dispose()
    {
        foreach (var s in new[] { DiffuseSlot, SpecularSlot, NormalSlot, EmissiveSlot, DepthSlot }) _bindless.FreeTexture(s);
        foreach (var i in new[] { Diffuse, Specular, Normal, Emissive, Depth }) i.Dispose();
    }
}
