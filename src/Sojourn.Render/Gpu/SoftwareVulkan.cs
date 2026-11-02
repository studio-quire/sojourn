using System.Runtime.InteropServices;

namespace Sojourn.Render.Gpu;

/// <summary>
/// A Vulkan driver that draws on the processor (Mesa's lavapipe, from the mesa-dist-win builds), for a PC whose
/// graphics can't run the renderer (an old driver with Vulkan 1.1 or 1.2, a VM, Microsoft's basic display adapter).
/// Not installed on the PC: the Vulkan loader is pointed at it for this process alone, by its manifest
/// (VK_DRIVER_FILES; VK_ICD_FILENAMES for loaders older than 1.3.207), only while an instance is made, so the
/// hardware's drivers aren't loaded beside it and nothing started from the app inherits it.
///
/// Where it is: SOJOURN_SOFTWARE_VULKAN (the manifest, or its folder), else lavapipe\ beside the program (a package:
/// tools/access.py fetch-lavapipe and package). Claude Opus 5.5, 2026-10-31.
/// </summary>
public static class SoftwareVulkan
{
    public const string Manifest = "lvp_icd.x86_64.json";
    public const string Folder = "lavapipe";

    /// <summary>The driver's manifest, or null if this build has none.</summary>
    public static string? Find()
    {
        var given = System.Environment.GetEnvironmentVariable("SOJOURN_SOFTWARE_VULKAN");
        foreach (var p in new[]
                 {
                     given, given != null ? Path.Combine(given, Manifest) : null,
                     Path.Combine(AppContext.BaseDirectory, Folder, Manifest),
                 })
            if (!string.IsNullOrEmpty(p) && File.Exists(p) && p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "vulkan_lvp.dll")))
                return Path.GetFullPath(p);
        return null;
    }

    static readonly string[] Pointing = ["VK_DRIVER_FILES", "VK_ICD_FILENAMES"];
    // LLVM's optimisation passes off for the shaders it compiles (Mesa's GALLIVM_PERF=nopt): the driver compiles each
    // shader, and each texture's sampling, as it's first drawn, and keeps nothing between sessions (Mesa's shader cache
    // isn't built on Windows; lavapipe's pipeline cache stores nothing), so a session's first pictures were mostly
    // compiling. Measured (tools/firstpicture.py, 2026-11-01): a session's compiling about halved (6.8 s to 3.0 on the
    // perf grid's Place One), and pictures once compiled as quick as before (the driver's own rasteriser and memory
    // traffic are most of a frame). SOJOURN_SOFTWARE_OPT=1 keeps the optimiser (comparing); a GALLIVM_PERF already set
    // is left alone.
    const string Perf = "GALLIVM_PERF";
    // filters that would hide it too (a test hides the hardware's drivers with VK_LOADER_DRIVERS_DISABLE=*), and an
    // addition that would bring a hardware driver back beside it
    static readonly string[] Cleared = ["VK_LOADER_DRIVERS_DISABLE", "VK_LOADER_DRIVERS_SELECT", "VK_ADD_DRIVER_FILES"];

    /// <summary>The loader pointed at the software driver alone until disposed (around making the instance: the loader
    /// reads its drivers then), and the driver's compiler told to skip optimising (<see cref="Perf"/>). The environment
    /// is the process's, so this is held as briefly as that.</summary>
    public static IDisposable Use(string manifest)
    {
        var saved = Pointing.Concat(Cleared).Append(Perf).ToDictionary(n => n, System.Environment.GetEnvironmentVariable);
        foreach (var n in Pointing) System.Environment.SetEnvironmentVariable(n, manifest);
        foreach (var n in Cleared) System.Environment.SetEnvironmentVariable(n, null);
        // (read when the driver's screen is made, inside the instance and device: Mesa asks Windows for it each time)
        if (saved[Perf] == null && System.Environment.GetEnvironmentVariable("SOJOURN_SOFTWARE_OPT") != "1")
            System.Environment.SetEnvironmentVariable(Perf, "nopt");
        return new Restore(saved);
    }

    /// <summary>The driver's own threads (its rasterisers, one per logical processor, and its queue's) put below normal
    /// priority, as the thread that asks for its frames is: a screen reader and the app's own window come first while it
    /// draws (Windows doesn't pass a thread's priority on to the threads it starts). Found by where they started, inside
    /// the driver's DLL; those already lowered are skipped. Returns how many were lowered now.</summary>
    public static int LowerThreads()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        nint dll = GetModuleHandleW("vulkan_lvp.dll");
        if (dll == 0 || !K32GetModuleInformation(GetCurrentProcess(), dll, out var info, (uint)Marshal.SizeOf<ModuleInfo>())) return 0;
        nint from = info.Base, to = info.Base + (nint)info.Size;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        int n = 0;
        foreach (System.Diagnostics.ProcessThread t in self.Threads)
        {
            // (ProcessThread.StartAddress is where the system started it, ntdll's, for every thread: the Win32 one is asked)
            nint h = OpenThread(QueryInformation | SetLimited, false, (uint)t.Id);
            if (h == 0) continue;   // (gone meanwhile)
            try
            {
                if (NtQueryInformationThread(h, ThreadQuerySetWin32StartAddress, out nint start, nint.Size, 0) != 0) continue;
                if (start < from || start >= to || GetThreadPriority(h) != 0) continue;
                if (SetThreadPriority(h, -1)) n++;   // THREAD_PRIORITY_BELOW_NORMAL
            }
            finally { CloseHandle(h); }
        }
        return n;
    }

    const uint QueryInformation = 0x0040, SetLimited = 0x0400;   // (the start address needs the full query right)
    const int ThreadQuerySetWin32StartAddress = 9;
    [StructLayout(LayoutKind.Sequential)] struct ModuleInfo { public nint Base; public uint Size; public nint Entry; }
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32")] static extern nint GetCurrentProcess();
    [DllImport("kernel32")] static extern bool K32GetModuleInformation(nint process, nint module, out ModuleInfo info, uint size);
    [DllImport("kernel32")] static extern nint OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32")] static extern int GetThreadPriority(nint h);
    [DllImport("kernel32")] static extern bool SetThreadPriority(nint h, int priority);
    [DllImport("ntdll")] static extern int NtQueryInformationThread(nint h, int cls, out nint info, int size, nint returned);

    sealed class Restore(Dictionary<string, string?> saved) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (n, v) in saved) System.Environment.SetEnvironmentVariable(n, v);
        }
    }
}

/// <summary>No GPU here can run the renderer: none found, none with Vulkan 1.3, or the one chosen lacks a feature it
/// needs. Unlike a lost device, trying again won't help; drawing on the processor may (<see cref="SoftwareVulkan"/>).</summary>
public sealed class NoUsableGpuException(string message) : InvalidOperationException(message);
