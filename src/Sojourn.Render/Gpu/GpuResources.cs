using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using Image = Silk.NET.Vulkan.Image;

namespace Sojourn.Render.Gpu;

public enum MemoryUse { GpuOnly, Upload, Readback }

public sealed unsafe class GpuBuffer : IDisposable
{
    readonly VulkanContext _vk;
    public Buffer Handle;
    public GpuAllocation Alloc;
    public ulong Size;

    public GpuBuffer(VulkanContext vk, ulong size, BufferUsageFlags usage, MemoryUse use)
    {
        _vk = vk;
        Size = size;
        var ci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage | BufferUsageFlags.ShaderDeviceAddressBit, SharingMode = SharingMode.Exclusive };
        VulkanContext.Check(vk.Api.CreateBuffer(vk.Device, in ci, null, out Handle), "vkCreateBuffer");
        vk.Api.GetBufferMemoryRequirements(vk.Device, Handle, out var req);
        var (reqd, pref) = use switch
        {
            MemoryUse.GpuOnly => (MemoryPropertyFlags.DeviceLocalBit, (MemoryPropertyFlags)0),
            MemoryUse.Upload => (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, (MemoryPropertyFlags)0),
            _ => (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPropertyFlags.HostCachedBit),
        };
        Alloc = vk.Allocator.Allocate(req, reqd, pref, linear: true);
        VulkanContext.Check(vk.Api.BindBufferMemory(vk.Device, Handle, Alloc.Memory, Alloc.Offset), "vkBindBufferMemory");
        var ai = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = Handle };
        Address = vk.Api.GetBufferDeviceAddress(vk.Device, in ai);
    }

    public ulong Address;

    public Span<byte> Mapped => Alloc.Mapped == 0 ? throw new InvalidOperationException("not mapped") : new Span<byte>((void*)Alloc.Mapped, (int)Size);
    public Span<T> MappedAs<T>() where T : unmanaged => new((void*)Alloc.Mapped, (int)(Size / (ulong)sizeof(T)));

    public void Dispose()
    {
        if (Handle.Handle == 0) return;
        _vk.Api.DestroyBuffer(_vk.Device, Handle, null);
        _vk.Allocator.Free(Alloc);
        Handle = default;
    }
}

public sealed unsafe class GpuImage : IDisposable
{
    readonly VulkanContext _vk;
    public Image Handle;
    public ImageView View;
    public GpuAllocation Alloc;
    public Format Format;
    /// <summary>Layers: how many this object covers (and its View shows); BaseLayer: the first (a layer view's own).</summary>
    public uint Width, Height, Mips, Layers, BaseLayer;
    public ImageAspectFlags Aspect;
    // each layer's layout, shared by an array image and its layer views (multiview targets: both eyes drawn at once,
    // then lit one at a time through views of one layer)
    readonly ImageLayout[] _layouts;
    readonly bool _ownsImage = true;
    /// <summary>The layout of every layer this object covers (they're kept alike unless a layer view moved its own).</summary>
    public ImageLayout Layout
    {
        get => _layouts[BaseLayer];
        set { for (uint l = BaseLayer; l < BaseLayer + Layers; l++) _layouts[l] = value; }
    }
    public ulong Bytes => _ownsImage ? Alloc.Size : 0;

    /// <summary>A 2D view of one layer of an array image: its own transitions, the array's memory.</summary>
    public GpuImage(GpuImage array, uint layer)
    {
        _vk = array._vk;
        Handle = array.Handle;
        Width = array.Width; Height = array.Height; Format = array.Format; Mips = array.Mips; Aspect = array.Aspect;
        Layers = 1; BaseLayer = layer;
        _layouts = array._layouts;
        _ownsImage = false;
        View = CreateView(ImageViewType.Type2D, 0, Mips, layer, 1);
    }

    public GpuImage(VulkanContext vk, uint width, uint height, Format format, ImageUsageFlags usage,
        uint mips = 1, uint layers = 1, bool cube = false, bool dedicated = false)
    {
        _vk = vk;
        Width = width; Height = height; Format = format; Mips = mips; Layers = layers;
        _layouts = new ImageLayout[layers];
        Aspect = IsDepth(format) ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
        var ci = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = mips,
            ArrayLayers = layers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            Flags = cube ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
        };
        VulkanContext.Check(vk.Api.CreateImage(vk.Device, in ci, null, out Handle), "vkCreateImage");
        vk.Api.GetImageMemoryRequirements(vk.Device, Handle, out var req);
        Alloc = vk.Allocator.Allocate(req, MemoryPropertyFlags.DeviceLocalBit, 0, linear: false, dedicated);
        VulkanContext.Check(vk.Api.BindImageMemory(vk.Device, Handle, Alloc.Memory, Alloc.Offset), "vkBindImageMemory");
        var viewType = cube ? (layers > 6 ? ImageViewType.TypeCubeArray : ImageViewType.TypeCube)
                            : layers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D;
        View = CreateView(viewType, 0, mips, 0, layers);
    }

    public ImageView CreateView(ImageViewType type, uint baseMip, uint mipCount, uint baseLayer, uint layerCount)
    {
        var vci = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Handle,
            ViewType = type,
            Format = Format,
            SubresourceRange = new ImageSubresourceRange(Aspect, baseMip, mipCount, baseLayer, layerCount),
        };
        VulkanContext.Check(_vk.Api.CreateImageView(_vk.Device, in vci, null, out var view), "vkCreateImageView");
        return view;
    }

    public static bool IsDepth(Format f) => f is Format.D32Sfloat or Format.D24UnormS8Uint or Format.D16Unorm or Format.D32SfloatS8Uint;

    /// <summary>Layout transition of every layer this object covers, with a conservative all-commands barrier (one for
    /// them all when they're alike, else a layer at a time).</summary>
    public void Transition(CommandBuffer cmd, ImageLayout to)
    {
        uint end = BaseLayer + Layers;
        bool alike = true;
        for (uint l = BaseLayer + 1; l < end; l++) alike &= _layouts[l] == _layouts[BaseLayer];
        if (alike)
        {
            if (_layouts[BaseLayer] == to) return;
            Barrier(_vk, cmd, Handle, Aspect, _layouts[BaseLayer], to, 0, Mips, BaseLayer, Layers, PreciseBarriers);
        }
        else
            for (uint l = BaseLayer; l < end; l++)
                if (_layouts[l] != to) Barrier(_vk, cmd, Handle, Aspect, _layouts[l], to, 0, Mips, l, 1, PreciseBarriers);
        Layout = to;
    }

    /// <summary>Transitions of the renderer's own images wait only for the stages that last used them and hold back only
    /// those that use them next (from the two layouts), instead of every command (SOJOURN_PRECISE_BARRIERS=1).</summary>
    public static bool PreciseBarriers = System.Environment.GetEnvironmentVariable("SOJOURN_PRECISE_BARRIERS") == "1";

    const PipelineStageFlags2 ShaderStages = PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit;
    const PipelineStageFlags2 DepthStages = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;

    /// <summary>The stages and accesses an image in this layout is used by; as a barrier's source, reads need no access
    /// (only the stages to finish). Unknown layouts: everything.</summary>
    static (PipelineStageFlags2 Stage, AccessFlags2 Src, AccessFlags2 Dst) Use(ImageLayout layout) => layout switch
    {
        ImageLayout.Undefined => (PipelineStageFlags2.None, 0, 0),
        ImageLayout.ColorAttachmentOptimal => (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
            AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit),
        ImageLayout.DepthAttachmentOptimal or ImageLayout.DepthStencilAttachmentOptimal => (DepthStages, AccessFlags2.DepthStencilAttachmentWriteBit,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit),
        ImageLayout.ShaderReadOnlyOptimal => (ShaderStages, 0, AccessFlags2.ShaderSampledReadBit),
        ImageLayout.TransferSrcOptimal => (PipelineStageFlags2.AllTransferBit, 0, AccessFlags2.TransferReadBit),
        ImageLayout.TransferDstOptimal => (PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit, AccessFlags2.TransferWriteBit),
        _ => (PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit),
    };

    public static void Barrier(VulkanContext vk, CommandBuffer cmd, Image image, ImageAspectFlags aspect, ImageLayout from, ImageLayout to,
        uint baseMip, uint mipCount, uint baseLayer, uint layerCount, bool precise = false)
    {
        var (srcStage, srcAccess, _) = precise ? Use(from) : (PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit, default(AccessFlags2));
        var (dstStage, _, dstAccess) = precise ? Use(to) : (PipelineStageFlags2.AllCommandsBit, default(AccessFlags2), AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);
        var b = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, baseMip, mipCount, baseLayer, layerCount),
        };
        var di = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &b };
        vk.Api.CmdPipelineBarrier2(cmd, in di);
    }

    public static void MemoryBarrier(VulkanContext vk, CommandBuffer cmd)
    {
        var b = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
        };
        var di = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &b };
        vk.Api.CmdPipelineBarrier2(cmd, in di);
    }

    public void Dispose()
    {
        if (Handle.Handle == 0) return;
        _vk.Api.DestroyImageView(_vk.Device, View, null);
        if (_ownsImage)
        {
            _vk.Api.DestroyImage(_vk.Device, Handle, null);
            _vk.Allocator.Free(Alloc);
        }
        Handle = default;
    }
}
