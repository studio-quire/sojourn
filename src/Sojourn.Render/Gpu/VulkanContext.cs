using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Sojourn.Render.Gpu;

/// <summary>How to bring the device up: headless (snapshots), or presenting to a window surface.</summary>
public sealed record VulkanSetup
{
    public bool Validation { get; init; }
    /// <summary>Instance extensions the window system needs (VK_KHR_surface and the platform's).</summary>
    public IReadOnlyList<string> InstanceExtensions { get; init; } = [];
    /// <summary>Made right after the instance; the chosen queue family must be able to present to it.</summary>
    public Func<Instance, SurfaceKHR>? CreateSurface { get; init; }
    /// <summary>Someone else makes the instance and device from our create infos and picks the GPU (an OpenXR
    /// runtime, XR_KHR_vulkan_enable2: it adds the extensions it needs and chooses the GPU the headset is on).</summary>
    public IVulkanCreator? Creator { get; init; }
    public Action<string>? Log { get; init; }
}

public unsafe interface IVulkanCreator
{
    Instance CreateInstance(Vk vk, InstanceCreateInfo* info);
    PhysicalDevice PickPhysicalDevice(Vk vk, Instance instance);
    Device CreateDevice(Vk vk, PhysicalDevice physicalDevice, DeviceCreateInfo* info);
}

/// <summary>
/// Vulkan 1.3 device with one graphics queue, used only from the render thread; optionally presenting to a window
/// surface (VK_KHR_swapchain). Requires dynamic rendering, synchronization2 and descriptor indexing (bindless
/// textures); enables multiview for stereo.
/// </summary>
public sealed unsafe class VulkanContext : IDisposable
{
    public Vk Api { get; }
    public Instance Instance { get; }
    public PhysicalDevice PhysicalDevice { get; }
    public Device Device { get; }
    public Queue Queue { get; }
    /// <summary>Held by whatever uses <see cref="Queue"/> (submits, waits, presents, and an OpenXR runtime's calls that
    /// may touch it: Vulkan wants access to a queue externally synchronised), since the headset's frames are ended on a
    /// thread of their own (Sojourn.Client's XrSession, overlap). Also what keeps a device-wide wait from running inside
    /// one of those.</summary>
    public readonly object QueueLock = new();
    public uint QueueFamily { get; }
    public PhysicalDeviceProperties Properties { get; }
    public PhysicalDeviceMemoryProperties MemoryProperties { get; }
    public string DeviceName { get; }
    public bool HasMemoryBudget { get; }
    public bool Validation { get; }
    public GpuAllocator Allocator { get; }
    public SurfaceKHR Surface { get; }
    public KhrSurface? SurfaceApi { get; }

    readonly CommandPool _oneShotPool;
    readonly Fence _oneShotFence;
    ExtDebugUtils? _debugUtils;
    DebugUtilsMessengerEXT _messenger;
    DebugUtilsMessengerCallbackFunctionEXT? _debugCallback;
    public Action<string>? Log { get; set; }

    public VulkanContext(bool validation = false, Action<string>? log = null) : this(new VulkanSetup { Validation = validation, Log = log }) { }

    public VulkanContext(VulkanSetup setup)
    {
        Log = setup.Log;
        Api = Vk.GetApi();
        bool validation = setup.Validation;

        var layers = new List<string>();
        var instExts = new List<string>(setup.InstanceExtensions);
        if (validation && HasLayer("VK_LAYER_KHRONOS_validation"))
        {
            layers.Add("VK_LAYER_KHRONOS_validation");
            instExts.Add(ExtDebugUtils.ExtensionName);
            Validation = true;
        }

        var appName = (byte*)SilkMarshal.StringToPtr("Sojourn.Render");
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appName,
            ApplicationVersion = 1,
            PEngineName = appName,
            EngineVersion = 1,
            ApiVersion = Vk.Version13,
        };
        var layerPtrs = (byte**)SilkMarshal.StringArrayToPtr(layers);
        var extPtrs = (byte**)SilkMarshal.StringArrayToPtr(instExts);
        var ici = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledLayerCount = (uint)layers.Count,
            PpEnabledLayerNames = layerPtrs,
            EnabledExtensionCount = (uint)instExts.Count,
            PpEnabledExtensionNames = extPtrs,
        };
        Instance instance;
        if (setup.Creator is { } creator)
        {
            instance = creator.CreateInstance(Api, &ici);
            Api.CurrentInstance = instance;   // Silk loads instance functions through this
        }
        else Check(Api.CreateInstance(in ici, null, out instance), "vkCreateInstance");
        Instance = instance;
        SilkMarshal.Free((nint)layerPtrs);
        SilkMarshal.Free((nint)extPtrs);
        SilkMarshal.Free((nint)appName);

        if (Validation) SetupDebugMessenger();
        if (setup.CreateSurface != null)
        {
            Surface = setup.CreateSurface(Instance);
            if (!Api.TryGetInstanceExtension(Instance, out KhrSurface ks)) throw new InvalidOperationException("VK_KHR_surface unavailable");
            SurfaceApi = ks;
        }

        PhysicalDeviceProperties props;
        if (setup.Creator != null)
        {
            PhysicalDevice = setup.Creator.PickPhysicalDevice(Api, Instance);
            Api.GetPhysicalDeviceProperties(PhysicalDevice, out props);
            if (props.ApiVersion < Vk.Version13) throw new InvalidOperationException("the headset's GPU has no Vulkan 1.3");
        }
        else PhysicalDevice = PickPhysicalDevice(out props);
        Properties = props;
        DeviceName = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "?";
        Api.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memProps);
        MemoryProperties = memProps;

        QueueFamily = FindGraphicsQueueFamily();
        HasMemoryBudget = HasDeviceExtension("VK_EXT_memory_budget");

        var devExts = new List<string>();
        if (HasMemoryBudget) devExts.Add("VK_EXT_memory_budget");
        if (SurfaceApi != null) devExts.Add(KhrSwapchain.ExtensionName);

        float priority = 1f;
        var qci = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var f13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true,
            Maintenance4 = true,
        };
        var f12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = &f13,
            DescriptorIndexing = true,
            RuntimeDescriptorArray = true,
            ShaderSampledImageArrayNonUniformIndexing = true,
            DescriptorBindingPartiallyBound = true,
            DescriptorBindingVariableDescriptorCount = true,
            DescriptorBindingSampledImageUpdateAfterBind = true,
            DescriptorBindingUpdateUnusedWhilePending = true,
            ScalarBlockLayout = true,
            HostQueryReset = true,
            TimelineSemaphore = true,
            BufferDeviceAddress = true,
            ShaderInt8 = false,
        };
        var f11 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = &f12,
            ShaderDrawParameters = true,
            Multiview = true,
        };
        var f = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &f11,
            Features = new PhysicalDeviceFeatures
            {
                SamplerAnisotropy = true,
                TextureCompressionBC = true,
                DepthClamp = true,
                MultiDrawIndirect = true,
                DrawIndirectFirstInstance = true,
                ShaderInt16 = false,
                ShaderInt64 = true,
                FragmentStoresAndAtomics = true,
                IndependentBlend = true,
                ImageCubeArray = true,
                ShaderClipDistance = true,   // a mirror's reflection is clipped at its plane
            },
        };
        var devExtPtrs = (byte**)SilkMarshal.StringArrayToPtr(devExts);
        var dci = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &f,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &qci,
            EnabledExtensionCount = (uint)devExts.Count,
            PpEnabledExtensionNames = devExtPtrs,
        };
        Device device;
        if (setup.Creator != null)
        {
            device = setup.Creator.CreateDevice(Api, PhysicalDevice, &dci);
            Api.CurrentDevice = device;
        }
        else Check(Api.CreateDevice(PhysicalDevice, in dci, null, out device), "vkCreateDevice");
        SilkMarshal.Free((nint)devExtPtrs);
        Device = device;
        Api.GetDeviceQueue(Device, QueueFamily, 0, out var queue);
        Queue = queue;

        var cpci = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = QueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit | CommandPoolCreateFlags.TransientBit,
        };
        Check(Api.CreateCommandPool(Device, in cpci, null, out _oneShotPool), "vkCreateCommandPool");
        var fci = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(Api.CreateFence(Device, in fci, null, out _oneShotFence), "vkCreateFence");

        Allocator = new GpuAllocator(this);
    }

    bool HasLayer(string name)
    {
        uint count = 0;
        Api.EnumerateInstanceLayerProperties(ref count, null);
        var arr = new LayerProperties[count];
        fixed (LayerProperties* p = arr) Api.EnumerateInstanceLayerProperties(ref count, p);
        foreach (var l in arr)
            if (SilkMarshal.PtrToString((nint)l.LayerName) == name) return true;
        return false;
    }

    bool HasDeviceExtension(string name)
    {
        uint count = 0;
        Api.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, ref count, null);
        var arr = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = arr) Api.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, ref count, p);
        foreach (var e in arr)
            if (SilkMarshal.PtrToString((nint)e.ExtensionName) == name) return true;
        return false;
    }

    void SetupDebugMessenger()
    {
        if (!Api.TryGetInstanceExtension(Instance, out ExtDebugUtils du)) return;
        _debugUtils = du;
        _debugCallback = (severity, types, data, user) =>
        {
            var msg = SilkMarshal.PtrToString((nint)data->PMessage);
            Log?.Invoke($"[vk {severity}] {msg}");
            return Vk.False;
        };
        var ci = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                          DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(_debugCallback),
        };
        du.CreateDebugUtilsMessenger(Instance, in ci, null, out _messenger);
    }

    PhysicalDevice PickPhysicalDevice(out PhysicalDeviceProperties chosenProps)
    {
        uint count = 0;
        Api.EnumeratePhysicalDevices(Instance, ref count, null);
        if (count == 0) throw new InvalidOperationException("No Vulkan devices");
        var devs = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devs) Api.EnumeratePhysicalDevices(Instance, ref count, p);
        PhysicalDevice best = default;
        chosenProps = default;
        int bestScore = int.MinValue;
        foreach (var d in devs)
        {
            Api.GetPhysicalDeviceProperties(d, out var pr);
            if (pr.ApiVersion < Vk.Version13) continue;
            int score = pr.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 1000,
                PhysicalDeviceType.IntegratedGpu => 100,
                _ => 0,
            };
            if (score > bestScore) { bestScore = score; best = d; chosenProps = pr; }
        }
        if (bestScore == int.MinValue) throw new InvalidOperationException("No Vulkan 1.3 device");
        return best;
    }

    uint FindGraphicsQueueFamily()
    {
        uint count = 0;
        Api.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref count, null);
        var fams = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = fams) Api.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref count, p);
        for (uint i = 0; i < count; i++)
        {
            if ((fams[i].QueueFlags & (QueueFlags.GraphicsBit | QueueFlags.ComputeBit)) != (QueueFlags.GraphicsBit | QueueFlags.ComputeBit)) continue;
            if (SurfaceApi != null)
            {
                SurfaceApi.GetPhysicalDeviceSurfaceSupport(PhysicalDevice, i, Surface, out var present);
                if (!present) continue;
            }
            return i;
        }
        throw new InvalidOperationException("No graphics queue");
    }

    /// <summary>Records and runs a command buffer synchronously (uploads, setup). Render thread only.</summary>
    public void Submit(Action<CommandBuffer> record)
    {
        var cmd = BeginCommands(_oneShotPool);
        record(cmd);
        EndAndWait(cmd, _oneShotFence);
        Api.FreeCommandBuffers(Device, _oneShotPool, 1, in cmd);
    }

    /// <summary>A new command buffer, begun. The caller frees it (or destroys its pool): resetting the pool doesn't.</summary>
    public CommandBuffer BeginCommands(CommandPool pool)
    {
        var cmd = AllocateCommandBuffer(pool);
        Begin(cmd);
        return cmd;
    }

    public CommandBuffer AllocateCommandBuffer(CommandPool pool)
    {
        var ai = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Check(Api.AllocateCommandBuffers(Device, in ai, out var cmd), "vkAllocateCommandBuffers");
        return cmd;
    }

    public void Begin(CommandBuffer cmd)
    {
        var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(Api.BeginCommandBuffer(cmd, in bi), "vkBeginCommandBuffer");
    }

    public void EndAndWait(CommandBuffer cmd, Fence fence)
    {
        Check(Api.EndCommandBuffer(cmd), "vkEndCommandBuffer");
        var si = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        Check(Api.ResetFences(Device, 1, in fence), "vkResetFences");
        lock (QueueLock) Check(Api.QueueSubmit(Queue, 1, in si, fence), "vkQueueSubmit");
        Check(Api.WaitForFences(Device, 1, in fence, true, ulong.MaxValue), "vkWaitForFences");
    }

    public CommandPool CreateCommandPool()
    {
        var cpci = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = QueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Check(Api.CreateCommandPool(Device, in cpci, null, out var pool), "vkCreateCommandPool");
        return pool;
    }

    public Fence CreateFence(bool signaled = false)
    {
        var fci = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = signaled ? FenceCreateFlags.SignaledBit : 0 };
        Check(Api.CreateFence(Device, in fci, null, out var fence), "vkCreateFence");
        return fence;
    }

    /// <summary>The device-local heaps' size, bytes (the card's video memory).</summary>
    public ulong DeviceLocalHeapBytes
    {
        get
        {
            ulong total = 0;
            var mp = MemoryProperties;
            for (int i = 0; i < mp.MemoryHeapCount; i++)
                if ((mp.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) != 0) total += mp.MemoryHeaps[i].Size;
            return total;
        }
    }

    /// <summary>Device-local heap usage and budget in bytes (whole process view from VK_EXT_memory_budget).</summary>
    public (ulong Usage, ulong Budget) DeviceLocalBudget()
    {
        if (!HasMemoryBudget) return (Allocator.TotalAllocated, 0);
        var budget = new PhysicalDeviceMemoryBudgetPropertiesEXT { SType = StructureType.PhysicalDeviceMemoryBudgetPropertiesExt };
        var mp2 = new PhysicalDeviceMemoryProperties2 { SType = StructureType.PhysicalDeviceMemoryProperties2, PNext = &budget };
        Api.GetPhysicalDeviceMemoryProperties2(PhysicalDevice, &mp2);
        ulong usage = 0, b = 0;
        for (int i = 0; i < mp2.MemoryProperties.MemoryHeapCount; i++)
        {
            if ((mp2.MemoryProperties.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) == 0) continue;
            usage += budget.HeapUsage[i];
            b += budget.HeapBudget[i];
        }
        return (usage, b);
    }

    public static void Check(Result r, string what)
    {
        if (r != Result.Success) throw new VulkanException(what, r);
    }

    public void WaitIdle() { lock (QueueLock) Api.DeviceWaitIdle(Device); }

    public void Dispose()
    {
        Api.DeviceWaitIdle(Device);
        Allocator.Dispose();
        Api.DestroyFence(Device, _oneShotFence, null);
        Api.DestroyCommandPool(Device, _oneShotPool, null);
        Api.DestroyDevice(Device, null);
        if (SurfaceApi != null) SurfaceApi.DestroySurface(Instance, Surface, null);
        if (_debugUtils != null) _debugUtils.DestroyDebugUtilsMessenger(Instance, _messenger, null);
        Api.DestroyInstance(Instance, null);
    }
}

public sealed class VulkanException(string what, Result result) : Exception($"{what} failed: {result}")
{
    public Result Result { get; } = result;
}
