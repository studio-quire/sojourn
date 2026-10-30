using Silk.NET.Vulkan;

namespace Sojourn.Render.Gpu;

/// <summary>
/// The global descriptor set (set 0): one big update-after-bind array of combined image samplers that every
/// texture and sampled render target registers into, plus a small cube-map array. Shaders index it with
/// nonuniformEXT. Slot 0..3 hold fallbacks (white, black, flat normal, default "missing" grey).
///
/// The texture table is as big as the device allows up to <see cref="TextureCeiling"/>: shaders get slots packed 16 bits
/// to a push constant with 0xFFFF for "none", so no slot may reach 0xFFFF. A full table doesn't throw for world
/// textures: they ask with <see cref="TryAllocTexture"/>, leaving a reserve for the renderer's own images, and wait
/// while the texture store lets go of what's longest unseen (World/TextureStore.cs).
/// </summary>
public sealed unsafe class Bindless : IDisposable
{
    /// <summary>The most texture slots there can be (slot 0xFFFF means "none" in packed push constants).</summary>
    public const uint TextureCeiling = 0xFFFF;
    /// <summary>The texture table's size on this device (at most <see cref="TextureCeiling"/>).</summary>
    public readonly uint MaxTextures;
    public const uint MaxCubes = 2048;   // two per resident reflection probe (radiance, irradiance)
    public const uint MaxShadowMaps = 8;
    public const int White = 0, Black = 1, FlatNormal = 2, Grey = 3;

    readonly VulkanContext _vk;
    public DescriptorSetLayout Layout;
    public DescriptorPool Pool;
    public DescriptorSet Set;
    public Sampler Repeat, Clamp, Nearest, Shadow, ClampLinearNoMip, NearestRepeat;
    readonly Stack<int> _freeTex = new();
    readonly Stack<int> _freeCube = new();
    int _nextTex = 0, _nextCube = 0;
    readonly object _lock = new();

    public Bindless(VulkanContext vk)
    {
        _vk = vk;
        MaxTextures = TableSize(vk);
        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        bindings[0] = new DescriptorSetLayoutBinding(0, DescriptorType.CombinedImageSampler, MaxTextures, ShaderStageFlags.All);
        bindings[1] = new DescriptorSetLayoutBinding(1, DescriptorType.CombinedImageSampler, MaxCubes, ShaderStageFlags.All);
        bindings[2] = new DescriptorSetLayoutBinding(2, DescriptorType.CombinedImageSampler, MaxShadowMaps, ShaderStageFlags.All);
        var flags = stackalloc DescriptorBindingFlags[3];
        flags[0] = flags[1] = flags[2] = DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit | DescriptorBindingFlags.UpdateUnusedWhilePendingBit;
        var flagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 3,
            PBindingFlags = flags,
        };
        var lci = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            PNext = &flagsInfo,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 3,
            PBindings = bindings,
        };
        VulkanContext.Check(vk.Api.CreateDescriptorSetLayout(vk.Device, in lci, null, out Layout), "bindless layout");

        var sizes = stackalloc DescriptorPoolSize[1];
        sizes[0] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, MaxTextures + MaxCubes + MaxShadowMaps);
        var pci = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = sizes,
        };
        VulkanContext.Check(vk.Api.CreateDescriptorPool(vk.Device, in pci, null, out Pool), "bindless pool");
        var layout = Layout;
        var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = Pool, DescriptorSetCount = 1, PSetLayouts = &layout };
        VulkanContext.Check(vk.Api.AllocateDescriptorSets(vk.Device, in ai, out Set), "bindless set");

        // RenderAnisotropicLevel (High-Ultra: 4), which LLImageGL textures get (TFO_ANISOTROPIC)
        float maxAniso = Math.Min(4f, vk.Properties.Limits.MaxSamplerAnisotropy);
        Repeat = MakeSampler(Filter.Linear, SamplerAddressMode.Repeat, true, maxAniso);
        Clamp = MakeSampler(Filter.Linear, SamplerAddressMode.ClampToEdge, true, maxAniso);
        ClampLinearNoMip = MakeSampler(Filter.Linear, SamplerAddressMode.ClampToEdge, false, 0);
        Nearest = MakeSampler(Filter.Nearest, SamplerAddressMode.ClampToEdge, false, 0);
        NearestRepeat = MakeSampler(Filter.Nearest, SamplerAddressMode.Repeat, false, 0);
        var sci = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear, MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToBorder, AddressModeV = SamplerAddressMode.ClampToBorder, AddressModeW = SamplerAddressMode.ClampToBorder,
            BorderColor = BorderColor.FloatOpaqueWhite,
            CompareEnable = true, CompareOp = CompareOp.LessOrEqual,
            MaxLod = 0,
        };
        VulkanContext.Check(vk.Api.CreateSampler(vk.Device, in sci, null, out Shadow), "shadow sampler");
    }

    Sampler MakeSampler(Filter filter, SamplerAddressMode mode, bool mips, float aniso)
    {
        var sci = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter, MinFilter = filter,
            MipmapMode = mips ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
            AddressModeU = mode, AddressModeV = mode, AddressModeW = mode,
            AnisotropyEnable = aniso > 1,
            MaxAnisotropy = aniso,
            MinLod = 0,
            MaxLod = mips ? Vk.LodClampNone : 0,
        };
        VulkanContext.Check(_vk.Api.CreateSampler(_vk.Device, in sci, null, out var s), "vkCreateSampler");
        return s;
    }

    /// <summary>The table's size: what the device allows a stage and a set of update-after-bind samplers and sampled
    /// images, less the cube and shadow arrays and room for other sets' samplers, at most <see cref="TextureCeiling"/>
    /// (and never less than the 16384 it always was).</summary>
    static uint TableSize(VulkanContext vk)
    {
        var p12 = new PhysicalDeviceVulkan12Properties { SType = StructureType.PhysicalDeviceVulkan12Properties };
        var p2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &p12 };
        vk.Api.GetPhysicalDeviceProperties2(vk.PhysicalDevice, &p2);
        uint limit = Math.Min(
            Math.Min(p12.MaxPerStageDescriptorUpdateAfterBindSampledImages, p12.MaxPerStageDescriptorUpdateAfterBindSamplers),
            Math.Min(p12.MaxDescriptorSetUpdateAfterBindSampledImages, p12.MaxDescriptorSetUpdateAfterBindSamplers));
        limit = Math.Min(limit, p12.MaxPerStageUpdateAfterBindResources);
        uint others = MaxCubes + MaxShadowMaps + 1024;
        uint size = Math.Clamp(limit > others ? limit - others : 0, 16384, TextureCeiling);
        // testing: a small table (SOJOURN_TEXTURE_SLOTS=N, at least 2048) runs low on the local grid's scenes
        if (uint.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_TEXTURE_SLOTS"), out uint test) && test >= 2048)
            size = Math.Min(size, test);
        return size;
    }

    /// <summary>Texture slots taken (allocated and not yet freed).</summary>
    public int TexturesUsed { get { lock (_lock) return _nextTex - _freeTex.Count; } }
    /// <summary>Texture slots free.</summary>
    public int TexturesFree => (int)MaxTextures - TexturesUsed;
    public int CubesUsed { get { lock (_lock) return _nextCube - _freeCube.Count; } }

    /// <summary>For the renderer's own images (targets, mirrors, HUD, media, LUTs: a few dozen, kept clear of world
    /// textures by their reserve). Throws only if the whole table is taken.</summary>
    public int AllocTexture() =>
        TryAllocTexture(0, out int slot) ? slot : throw new InvalidOperationException("bindless texture table full");

    /// <summary>A slot, if more than <paramref name="keepFree"/> would still be free after it; false otherwise (the
    /// caller waits for slots to be let go).</summary>
    public bool TryAllocTexture(int keepFree, out int slot)
    {
        lock (_lock)
        {
            slot = -1;
            if (MaxTextures - (_nextTex - _freeTex.Count) <= keepFree) return false;
            slot = _freeTex.Count > 0 ? _freeTex.Pop() : _nextTex++;
            return true;
        }
    }

    public int AllocCube() => TryAllocCube(out int slot) ? slot : throw new InvalidOperationException("bindless cube table full");

    public bool TryAllocCube(out int slot)
    {
        lock (_lock)
        {
            slot = -1;
            if (_freeCube.Count > 0) slot = _freeCube.Pop();
            else if (_nextCube < MaxCubes) slot = _nextCube++;
            return slot >= 0;
        }
    }

    public void FreeTexture(int slot) { lock (_lock) _freeTex.Push(slot); }
    public void FreeCube(int slot) { lock (_lock) _freeCube.Push(slot); }

    /// <summary>Shadow maps live in their own small array (binding 2) with the depth-compare sampler.</summary>
    public void WriteShadowMap(int index, ImageView view) => Write(index, view, Shadow, ImageLayout.ShaderReadOnlyOptimal, binding: 2);

    public void Write(int slot, ImageView view, Sampler sampler, ImageLayout layout = ImageLayout.ShaderReadOnlyOptimal, bool cube = false, uint? binding = null)
    {
        var info = new DescriptorImageInfo(sampler, view, layout);
        var w = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = Set,
            DstBinding = binding ?? (cube ? 1u : 0u),
            DstArrayElement = (uint)slot,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info,
        };
        _vk.Api.UpdateDescriptorSets(_vk.Device, 1, in w, 0, null);
    }

    public void Dispose()
    {
        foreach (var s in new[] { Repeat, Clamp, Nearest, Shadow, ClampLinearNoMip, NearestRepeat }) _vk.Api.DestroySampler(_vk.Device, s, null);
        _vk.Api.DestroyDescriptorPool(_vk.Device, Pool, null);
        _vk.Api.DestroyDescriptorSetLayout(_vk.Device, Layout, null);
    }
}
