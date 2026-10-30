using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using Sojourn.Render.Gpu;
using Vk = Silk.NET.Vulkan.Vk;
using VkInstance = Silk.NET.Vulkan.Instance;
using VkPhysicalDevice = Silk.NET.Vulkan.PhysicalDevice;
using VkDevice = Silk.NET.Vulkan.Device;
using VkInstanceCreateInfo = Silk.NET.Vulkan.InstanceCreateInfo;
using VkDeviceCreateInfo = Silk.NET.Vulkan.DeviceCreateInfo;

namespace Sojourn.Client.Xr;

// OpenXR's Session, not the Sojourn.Session namespace (which a bare name in Sojourn.* finds first)
using Session = Silk.NET.OpenXR.Session;

/// <summary>
/// The OpenXR instance and the headset (system), before there's a session. It also makes the renderer's Vulkan
/// instance and device (XR_KHR_vulkan_enable2): the runtime adds the extensions it needs and picks the GPU the headset
/// is on. <see cref="TryCreate"/> returns null when there's no runtime or no headset, and the client stays on the
/// desktop.
/// </summary>
public sealed unsafe class XrRuntime : IVulkanCreator, IDisposable
{
    public XR Api { get; }
    public Instance Instance { get; }
    public ulong SystemId { get; }
    public string RuntimeName { get; }
    public string SystemName { get; }
    public KhrVulkanEnable2 VulkanEnable { get; }
    /// <summary>XR_KHR_win32_convert_performance_counter_time: display times as Stopwatch ticks.</summary>
    public KhrWin32ConvertPerformanceCounterTime? TimeConvert { get; }
    public HashSet<string> Extensions { get; }
    readonly Action<string> _log;
    readonly PfnVoidFunction _getInstanceProcAddr;

    XrRuntime(XR api, Instance instance, ulong systemId, HashSet<string> extensions, Action<string> log)
    {
        Api = api;
        Instance = instance;
        SystemId = systemId;
        Extensions = extensions;
        _log = log;

        var ip = new InstanceProperties { Type = StructureType.InstanceProperties };
        Api.GetInstanceProperties(instance, &ip);
        RuntimeName = $"{Str(ip.RuntimeName, 128)} {ip.RuntimeVersion >> 48}.{(ip.RuntimeVersion >> 32) & 0xffff}.{ip.RuntimeVersion & 0xffffffff}";
        var sp = new SystemProperties { Type = StructureType.SystemProperties };
        Api.GetSystemProperties(instance, systemId, &sp);
        SystemName = Str(sp.SystemName, 256);

        if (!Api.TryGetInstanceExtension(null, instance, out KhrVulkanEnable2 ve)) throw new XrException("XR_KHR_vulkan_enable2 functions", Result.ErrorExtensionNotPresent);
        VulkanEnable = ve;
        if (extensions.Contains(KhrWin32ConvertPerformanceCounterTime.ExtensionName) &&
            Api.TryGetInstanceExtension(null, instance, out KhrWin32ConvertPerformanceCounterTime tc)) TimeConvert = tc;

        var req = new GraphicsRequirementsVulkanKHR { Type = StructureType.GraphicsRequirementsVulkanKhr };
        Check(VulkanEnable.GetVulkanGraphicsRequirements2(instance, systemId, &req), "xrGetVulkanGraphicsRequirements2KHR");
        _log($"OpenXR: {RuntimeName}, {SystemName}; Vulkan {Ver(req.MinApiVersionSupported)} to {Ver(req.MaxApiVersionSupported)}");

        var vulkan = NativeLibrary.Load("vulkan-1.dll");
        _getInstanceProcAddr = new PfnVoidFunction((delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(vulkan, "vkGetInstanceProcAddr"));
    }

    static string Ver(ulong v) => $"{v >> 48}.{(v >> 32) & 0xffff}";

    /// <summary>The runtime and a headset, or null (logged why). Gives up after <paramref name="timeout"/>: a runtime
    /// whose service isn't running can take long to say so.</summary>
    public static XrRuntime? TryCreate(Action<string> log, bool validation, TimeSpan timeout)
    {
        var task = Task.Run(() =>
        {
            try { return Create(log, validation); }
            catch (DllNotFoundException) { log("OpenXR: no loader (openxr_loader.dll); staying on the desktop"); }
            catch (XrException e) { log($"OpenXR: {e.Message}; staying on the desktop"); }
            catch (Exception e) { log($"OpenXR: {e.GetType().Name}: {e.Message}; staying on the desktop"); }
            return null;
        });
        if (task.Wait(timeout)) return task.Result;
        log($"OpenXR: no answer from the runtime in {timeout.TotalSeconds:F0} s; staying on the desktop");
        task.ContinueWith(t => t.Result?.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
        return null;
    }

    static XrRuntime? Create(Action<string> log, bool validation)
    {
        var api = XR.GetApi();
        var available = EnumerateExtensions(api);
        if (!available.Contains(KhrVulkanEnable2.ExtensionName))
            throw new XrException("the runtime has no XR_KHR_vulkan_enable2", Result.ErrorExtensionNotPresent);
        var wanted = new[]
        {
            KhrVulkanEnable2.ExtensionName,
            KhrWin32ConvertPerformanceCounterTime.ExtensionName,
            "XR_EXT_local_floor",
            "XR_FB_display_refresh_rate",
            "XR_KHR_composition_layer_depth",
            XrLensMask.Extension,
            "XR_META_performance_metrics",
        };
        var enabled = wanted.Where(available.Contains).ToHashSet();
        // what else the runtime offers (performance metrics, frame synthesis controls), for later sessions to see
        log($"OpenXR: the runtime offers {string.Join(", ", available.OrderBy(e => e))}");
        var layers = new List<string>();
        if (validation && EnumerateLayers(api).Contains("XR_APILAYER_LUNARG_core_validation")) layers.Add("XR_APILAYER_LUNARG_core_validation");

        var ci = new InstanceCreateInfo { Type = StructureType.InstanceCreateInfo };
        Put(ci.ApplicationInfo.ApplicationName, 128, "Sojourn");
        Put(ci.ApplicationInfo.EngineName, 128, "Sojourn");
        ci.ApplicationInfo.ApplicationVersion = 1;
        ci.ApplicationInfo.EngineVersion = 1;
        ci.ApplicationInfo.ApiVersion = 1UL << 48;   // OpenXR 1.0: every runtime speaks it
        var extPtrs = (byte**)SilkMarshal.StringArrayToPtr(enabled.ToArray());
        var layerPtrs = (byte**)SilkMarshal.StringArrayToPtr(layers);
        ci.EnabledExtensionCount = (uint)enabled.Count;
        ci.EnabledExtensionNames = extPtrs;
        ci.EnabledApiLayerCount = (uint)layers.Count;
        ci.EnabledApiLayerNames = layerPtrs;
        Instance instance;
        var r = api.CreateInstance(&ci, &instance);
        SilkMarshal.Free((nint)extPtrs);
        SilkMarshal.Free((nint)layerPtrs);
        Check(r, "xrCreateInstance");
        if (layers.Count > 0) log("OpenXR: core validation layer on");

        var gi = new SystemGetInfo { Type = StructureType.SystemGetInfo, FormFactor = FormFactor.HeadMountedDisplay };
        ulong systemId;
        r = api.GetSystem(instance, &gi, &systemId);
        if (r != Result.Success)
        {
            api.DestroyInstance(instance);
            throw new XrException(r == Result.ErrorFormFactorUnavailable ? "no headset connected" : "xrGetSystem", r);
        }
        try { return new XrRuntime(api, instance, systemId, enabled, log); }
        catch
        {
            api.DestroyInstance(instance);
            throw;
        }
    }

    static HashSet<string> EnumerateExtensions(XR api)
    {
        uint n = 0;
        Check(api.EnumerateInstanceExtensionProperties((byte*)null, 0, &n, null), "xrEnumerateInstanceExtensionProperties");
        var props = new ExtensionProperties[n];
        for (int i = 0; i < n; i++) props[i].Type = StructureType.ExtensionProperties;
        fixed (ExtensionProperties* p = props) Check(api.EnumerateInstanceExtensionProperties((byte*)null, n, &n, p), "xrEnumerateInstanceExtensionProperties");
        return props.Select(e => Str(e.ExtensionName, 128)).ToHashSet();
    }

    static HashSet<string> EnumerateLayers(XR api)
    {
        uint n = 0;
        if (api.EnumerateApiLayerProperties(0, &n, null) != Result.Success || n == 0) return [];
        var props = new ApiLayerProperties[n];
        for (int i = 0; i < n; i++) props[i].Type = StructureType.ApiLayerProperties;
        fixed (ApiLayerProperties* p = props) api.EnumerateApiLayerProperties(n, &n, p);
        return props.Select(l => Str(l.LayerName, 256)).ToHashSet();
    }

    // --- IVulkanCreator: the renderer's instance and device, through the runtime ---------------------------------

    public VkInstance CreateInstance(Vk vk, VkInstanceCreateInfo* info)
    {
        var ci = new VulkanInstanceCreateInfoKHR
        {
            Type = StructureType.VulkanInstanceCreateInfoKhr,
            SystemId = SystemId,
            PfnGetInstanceProcAddr = _getInstanceProcAddr,
            VulkanCreateInfo = info,
        };
        VkHandle handle;
        uint vkResult;
        Check(VulkanEnable.CreateVulkanInstance(Instance, &ci, &handle, &vkResult), "xrCreateVulkanInstanceKHR");
        if (vkResult != 0) throw new XrException($"vkCreateInstance through the runtime: VkResult {(int)vkResult}", Result.ErrorRuntimeFailure);
        return new VkInstance(handle.Handle);
    }

    public VkPhysicalDevice PickPhysicalDevice(Vk vk, VkInstance instance)
    {
        var gi = new VulkanGraphicsDeviceGetInfoKHR
        {
            Type = StructureType.VulkanGraphicsDeviceGetInfoKhr,
            SystemId = SystemId,
            VulkanInstance = new VkHandle(instance.Handle),
        };
        VkHandle handle;
        Check(VulkanEnable.GetVulkanGraphicsDevice2(Instance, &gi, &handle), "xrGetVulkanGraphicsDevice2KHR");
        return new VkPhysicalDevice(handle.Handle);
    }

    public VkDevice CreateDevice(Vk vk, VkPhysicalDevice physicalDevice, VkDeviceCreateInfo* info)
    {
        var ci = new VulkanDeviceCreateInfoKHR
        {
            Type = StructureType.VulkanDeviceCreateInfoKhr,
            SystemId = SystemId,
            PfnGetInstanceProcAddr = _getInstanceProcAddr,
            VulkanPhysicalDevice = new VkHandle(physicalDevice.Handle),
            VulkanCreateInfo = info,
        };
        VkHandle handle;
        uint vkResult;
        Check(VulkanEnable.CreateVulkanDevice(Instance, &ci, &handle, &vkResult), "xrCreateVulkanDeviceKHR");
        if (vkResult != 0) throw new XrException($"vkCreateDevice through the runtime: VkResult {(int)vkResult}", Result.ErrorRuntimeFailure);
        return new VkDevice(handle.Handle);
    }

    // --- helpers -------------------------------------------------------------------------------------------------

    /// <summary>An XrTime as Stopwatch ticks (QueryPerformanceCounter), or null without the extension.</summary>
    public long? ToStopwatch(long xrTime)
    {
        if (TimeConvert is not { } tc) return null;
        long qpc;
        return tc.ConvertTimeToWin32PerformanceCounter(Instance, xrTime, &qpc) == Result.Success ? qpc : null;
    }

    public ulong Path(string path)
    {
        ulong p;
        Check(Api.StringToPath(Instance, path, &p), $"xrStringToPath {path}");
        return p;
    }

    public static void Check(Result r, string what)
    {
        if ((int)r < 0) throw new XrException(what, r);
    }

    public static string Str(byte* p, int cap)
    {
        int n = 0;
        while (n < cap && p[n] != 0) n++;
        return Encoding.UTF8.GetString(p, n);
    }

    public static void Put(byte* dst, int cap, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        int n = Math.Min(bytes.Length, cap - 1);
        for (int i = 0; i < n; i++) dst[i] = bytes[i];
        dst[n] = 0;
    }

    public void Dispose() => Api.DestroyInstance(Instance);
}

public sealed class XrException(string what, Result result) : Exception($"{what}: {result}")
{
    public Result Result { get; } = result;
}
