using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Release;

/// <summary>
/// The viewer statistics report the TPV policy asks viewers built on LL's source not to omit (2.h; docs/tpv-policy.md >
/// Ported viewer code): as the official viewer's <c>send_viewer_stats</c> (llviewerstats.cpp), an LLSD POST to the
/// region's ViewerStats capability once a region is up after each login, then every 300 s, and at logout. It holds
/// this session and this PC only: how long Sojourn has run, its frame rate, the region's ping, regions visited, memory,
/// how many avatars are in view (a count), the OS, CPU, RAM and GPU; never anything about other residents. What we
/// don't measure is left out rather than made up (download totals, packet counts, distance travelled, the UI's use);
/// the viewer's preferences at quit aren't sent (ours aren't the viewer's). The MAC address and the C: volume's serial
/// (hashed, as the viewer sends them) go only with the person's setting <c>stats_hardware_ids</c>, off by default (the
/// user's call, 2026-11-01). A region without the cap (OpenSim usually) is skipped, saying so
/// once. Written by Claude Opus 5.5, 2026-11-01.
/// </summary>
public sealed class ViewerStats : IDisposable
{
    public const double Period = 300;   // SEND_STATS_PERIOD

    readonly AgentSession _s;
    readonly Func<bool> _hardwareIds;
    readonly ILogger _log;
    readonly CancellationTokenSource _stop = new();
    readonly DateTime _started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    readonly HashSet<ulong> _regions = new();
    double _fpsSum;
    int _fpsSamples;
    bool _saidNoCap;

    /// <summary>The app's frame rate now (frames a second; 0 or less: unknown), sampled every few seconds.</summary>
    public Func<float>? Fps { get; set; }
    /// <summary>How many avatars the app draws now; without it, those in the region within the draw distance.</summary>
    public Func<int>? AgentsInView { get; set; }
    /// <summary>The GPU, when the app renders.</summary>
    public Func<Gpu?>? Graphics { get; set; }
    public sealed record Gpu(string Name, string Vendor, string Driver, int VramMb, string VulkanApi);

    /// <summary>The last report sent (for tests: SojournRelease stats).</summary>
    public OSDMap? Last { get; private set; }
    public DateTime? LastAt { get; private set; }
    public string LastOutcome { get; private set; } = "not sent yet";

    public ViewerStats(AgentSession s, Func<bool> hardwareIds, ILogger log)
    {
        _s = s;
        _hardwareIds = hardwareIds;
        _log = log;
        s.BeforeLogout.Add(FinalAsync);
        _ = Task.Run(LoopAsync);
    }

    async Task LoopAsync()
    {
        GridClient? sentFor = null;
        var due = DateTime.MaxValue;
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (Fps?.Invoke() is > 0 and var f) { _fpsSum += f; _fpsSamples++; }
                var c = _s.Client;
                if (c?.Network.CurrentSim is { } sim) _regions.Add(sim.Handle);
                if (c == null || !_s.Online) continue;
                // a new login: at once (once its region's capabilities are in), then every 300 s
                if (!ReferenceEquals(c, sentFor))
                {
                    if (c.Network.CurrentSim?.Caps?.CapabilityURI("EventQueueGet") is null) continue;   // the seed's answer isn't in yet
                    sentFor = c;
                    due = DateTime.UtcNow;
                }
                if (DateTime.UtcNow < due) continue;
                due = DateTime.UtcNow.AddSeconds(Period);
                await SendAsync(c, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogWarning("viewer stats: {Msg}", e.Message); }
        }
    }

    static Uri? Cap(GridClient c) => c.Network.CurrentSim?.Caps?.CapabilityURI("ViewerStats");

    /// <summary>At logout (AgentSession.BeforeLogout), as the viewer sends at quit: a last report, at most 3 s.</summary>
    async Task FinalAsync(GridClient c)
    {
        if (_stop.IsCancellationRequested || Cap(c) is null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await SendAsync(c, cts.Token); }
        catch (Exception e) { _log.LogDebug("viewer stats at logout: {Msg}", e.Message); }
    }

    /// <summary>Sends the report now (the timer's, logout's, and the API's for tests); the outcome.</summary>
    public async Task<string> SendAsync(GridClient c, CancellationToken ct)
    {
        if (Cap(c) is not { } cap)
        {
            if (!_saidNoCap) _log.LogInformation("viewer stats: the region has no ViewerStats capability; not sent");
            _saidNoCap = true;
            return LastOutcome = "the region has no ViewerStats capability";
        }
        var body = Build(c);
        var (resp, _) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, body, ct).ConfigureAwait(false);
        var code = resp is null ? 0 : (int)resp.StatusCode;
        Last = body;
        LastAt = DateTime.UtcNow;
        LastOutcome = code is >= 200 and < 300 ? $"sent ({code})" : $"the region answered {code}";
        _log.LogInformation("viewer stats: {Outcome}", LastOutcome);
        return LastOutcome;
    }

    /// <summary>The report as it would go now (without the session id, for showing).</summary>
    public OSDMap Preview() => Build(_s.Client, withSession: false);

    OSDMap Build(GridClient? c, bool withSession = true)
    {
        double run = (DateTime.UtcNow - _started).TotalSeconds;
        var agent = new OSDMap
        {
            ["start_time"] = OSD.FromInteger((int)new DateTimeOffset(_started).ToUnixTimeSeconds()),
            // the first report's run time is 0 if it comes before half a period (the data warehouse drops those)
            ["run_time"] = OSD.FromReal(run < Period / 2 ? 0 : run),
            ["version"] = OSD.FromString($"{_s.Host.Channel} {_s.Host.Version}"),
            ["language"] = OSD.FromString("en"),
            ["regions_visited"] = OSD.FromInteger(_regions.Count),
            ["mem_use"] = OSD.FromReal(Environment.WorkingSet / 1024.0),
        };
        if (_fpsSamples > 0) agent["fps"] = OSD.FromReal(_fpsSum / _fpsSamples);
        if (c?.Network.CurrentSim is { } sim)
        {
            agent["ping"] = OSD.FromReal(sim.Stats.LastLag);
            agent["agents_in_view"] = OSD.FromInteger(AgentsInView?.Invoke() ?? InView(c, sim));
        }

        var system = new OSDMap
        {
            ["os"] = OSD.FromString(RuntimeInformation.OSDescription),
            ["cpu"] = OSD.FromString(Cpu().Name),
            ["cpu_mhz"] = OSD.FromInteger(Cpu().Mhz),
            ["address_size"] = OSD.FromInteger(IntPtr.Size * 8),
            ["os_bitness"] = OSD.FromInteger(Environment.Is64BitOperatingSystem ? 64 : 32),
            ["hardware_concurrency"] = OSD.FromInteger(Environment.ProcessorCount),
        };
        if (Memory() is { } mem)
        {
            system["ram"] = OSD.FromInteger((int)(mem.Total / 1024));
            system["ram_avail_mb"] = OSD.FromInteger((int)(mem.Available / (1024 * 1024)));
        }
        if (Graphics?.Invoke() is { } g)
        {
            system["gpu"] = OSD.FromString($"{g.Name} (Vulkan {g.VulkanApi})");
            system["gpu_vendor"] = OSD.FromString(g.Vendor);
            system["gpu_version"] = OSD.FromString(g.Driver);
            system["gpu_vram_mb"] = OSD.FromInteger(g.VramMb);
        }
        if (_hardwareIds())
        {
            if (Mac() is { } mac) system["mac_address"] = OSD.FromString(mac);
            system["serial_number"] = OSD.FromString(Serial());
        }

        var misc = new OSDMap
        {
            ["string_1"] = OSD.FromString(Graphics?.Invoke() is not null ? "Vulkan driver is detected" : "Unused"),
            ["string_2"] = OSD.FromString("Unused"),
            ["int_1"] = OSD.FromInteger(0),
            ["int_2"] = OSD.FromInteger(0),
        };
        if (Graphics?.Invoke() is { } gv) misc["VulkanMaxApiVersion"] = OSD.FromString(gv.VulkanApi);

        var body = new OSDMap
        {
            ["agent"] = agent,
            ["system"] = system,
            ["stats"] = new OSDMap { ["misc"] = misc },
            ["misc"] = new OSDMap { ["Version"] = OSD.FromBoolean(true) },
        };
        if (withSession && c != null) body["session_id"] = OSD.FromUUID(c.Self.SessionID);
        return body;
    }

    int InView(GridClient c, Simulator sim)
    {
        float far = _s.Camera.FarClip;
        var me = c.Self.SimPosition;
        int n = 0;
        foreach (var a in sim.ObjectsAvatars.Values)
            if (a.ID != c.Self.AgentID && Vector3.Distance(a.Position, me) <= far) n++;
        return n;
    }

    // ---- the PC: what the viewer's system block holds ----

    static (string Name, int Mhz)? _cpu;
    static (string Name, int Mhz) Cpu() => _cpu ??= ReadCpu();
    static (string, int) ReadCpu()
    {
        if (!OperatingSystem.IsWindows()) return (RuntimeInformation.ProcessArchitecture.ToString(), 0);
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (((k?.GetValue("ProcessorNameString") as string) ?? RuntimeInformation.ProcessArchitecture.ToString()).Trim(),
            k?.GetValue("~MHz") is int mhz ? mhz : 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);

    static (ulong Total, ulong Available)? Memory()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref m) ? (m.TotalPhys, m.AvailPhys) : null;
    }

    /// <summary>The first network adapter's MAC, as the viewer formats it (LLUUID::getNodeID: 00-11-22-33-44-55).</summary>
    static string? Mac()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n => n.GetPhysicalAddress().GetAddressBytes())
                .FirstOrDefault(b => b.Length == 6);
            return nic is null ? null : string.Join("-", nic.Select(b => b.ToString("x2")));
        }
        catch (NetworkInformationException) { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformationW(string root, IntPtr name, int nameSize, out uint serial, IntPtr maxComponent, IntPtr flags, IntPtr fs, int fsSize);

    /// <summary>The viewer's serial number (LLAppViewerWin32::generateSerialNumber): the MD5 of C:'s volume serial.</summary>
    static string Serial()
    {
        if (!OperatingSystem.IsWindows() || !GetVolumeInformationW(@"C:\", IntPtr.Zero, 0, out var serial, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0)) return "";
        return Convert.ToHexString(MD5.HashData(BitConverter.GetBytes(serial))).ToLowerInvariant();
    }

    public void Dispose()
    {
        _s.BeforeLogout.Remove(FinalAsync);
        _stop.Cancel();
    }
}
