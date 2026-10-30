using System.Net;
using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// Connects the neighbouring regions (child agents) in place of LibreMetaverse 3.2.0's own handlers, which lose one.
/// A neighbour comes as EnableSimulator (its endpoint, handle and size) and EstablishAgentCommunication (its seed
/// capability) on the current region's event queue, usually in one batch; LibreMetaverse raises each event on a task
/// of its own, so the two handlers run at once. Each looks the endpoint up, and when both miss (the other hasn't added
/// its Simulator yet) both connect: two Simulators, two UDP sockets, two UseCircuitCodes for one circuit. OpenSim then
/// sends DisableSimulator to both and the neighbour is gone for the session (about one login in five on the local grid,
/// traced 2026-10-26 with a probe logging both handlers' threads). The second handler's connect also dropped the seed
/// (Connect ignores it for a non-default region) and used handle 0.
/// So the guard takes both events off LibreMetaverse (by unregistering its handlers) and decides under one lock: a
/// neighbour is connected once, with its handle, off the event's task; its seed is set when the circuit is up. A seed
/// for a region nobody is connecting waits 1.5 s for its EnableSimulator (or a crossing or teleport's own connect)
/// before connecting with what it has, as LibreMetaverse did. A check over the next half minute still connects one
/// that was announced and is missing (once) and sets missing caps. A region that closes its child agent (the circuit
/// ends) is forgotten, so nothing deliberately closed comes back. Written by Claude Opus 5.5, 2026-10-25, the race
/// found and the handlers replaced 2026-10-26.
/// </summary>
public sealed class ChildCapsGuard
{
    readonly GridClient _c;
    readonly ILogger _log;
    readonly object _gate = new();

    sealed class Announced
    {
        public ulong Handle;
        public uint SizeX = 256, SizeY = 256;
        public Uri? Seed;
        public DateTime At = DateTime.UtcNow;
        public bool Connecting, Reconnected;
    }
    readonly Dictionary<IPEndPoint, Announced> _known = new();
    static readonly bool Trace = Environment.GetEnvironmentVariable("SOJOURN_NEIGHBOUR_LOG") == "1";

    ChildCapsGuard(GridClient c, ILogger log)
    {
        _c = c;
        _log = log;
    }

    public static void Attach(GridClient c, ILogger log)
    {
        var g = new ChildCapsGuard(c, log);
        bool replaced = Unregister(c, c.Network, "EnableSimulator", "EnableSimulatorHandler", log)
                      & Unregister(c, c.Self, "EstablishAgentCommunication", "EstablishAgentCommunicationEventHandler", log);
        c.Network.RegisterEventCallback("EstablishAgentCommunication", (_, msg, _) =>
        {
            if (msg is not EstablishAgentCommunicationMessage m || m.SeedCapability is null || m.Address is null) return;
            g.Seed(new IPEndPoint(m.Address, m.Port), m.SeedCapability, replaced);
        });
        c.Network.RegisterEventCallback("EnableSimulator", (_, msg, _) =>
        {
            if (msg is not EnableSimulatorMessage m) return;
            foreach (var b in m.Simulators) g.Enable(new IPEndPoint(b.IP, b.Port), b.RegionHandle, b.RegionSizeX, b.RegionSizeY, replaced);
        });
        c.Network.SimDisconnected += (_, e) =>
        {
            if (e.Simulator?.IPEndPoint is { } ep) lock (g._gate) g._known.Remove(ep);
        };
        c.Network.Disconnected += (_, _) => { lock (g._gate) g._known.Clear(); };
    }

    /// <summary>Take one of LibreMetaverse's event handlers off (its delegate is equal to one made from the same method
    /// and object). False, logged, if 3.2.0's method isn't there: then its handler stays and the check below is all.</summary>
    static bool Unregister(GridClient c, object owner, string evt, string method, ILogger log)
    {
        var mi = owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (mi == null)
        {
            log.LogWarning("neighbour guard: LibreMetaverse has no {Method}; its {Event} handler stays", method, evt);
            return false;
        }
        c.Network.UnregisterEventCallback(evt, (Caps.EventQueueCallback)Delegate.CreateDelegate(typeof(Caps.EventQueueCallback), owner, mi));
        return true;
    }

    void Enable(IPEndPoint ep, ulong handle, uint sizeX, uint sizeY, bool ours)
    {
        if (!_c.Settings.Agent.MultipleSims) return;
        bool connect;
        lock (_gate)
        {
            var a = Get(ep);
            a.Handle = handle;
            if (sizeX > 0) a.SizeX = sizeX;
            if (sizeY > 0) a.SizeY = sizeY;
            a.At = DateTime.UtcNow;
            var sim = _c.Network.FindSimulator(ep);
            if (sim != null && sim.Handle == 0 && handle != 0) sim.Handle = handle;
            connect = ours && sim == null && !a.Connecting;
            if (connect) a.Connecting = true;
        }
        if (Trace) _log.LogInformation("neighbour guard: enable {Ep}{Connect}", ep, connect ? ", connecting" : "");
        if (connect) _ = Task.Run(() => Connect(ep));
        _ = CheckLater();
    }

    void Seed(IPEndPoint ep, Uri seed, bool ours)
    {
        if (!_c.Settings.Agent.MultipleSims) return;
        Simulator? set = null;
        bool wait = false;
        lock (_gate)
        {
            var a = Get(ep);
            a.Seed = seed;
            a.At = DateTime.UtcNow;
            if (a.Connecting) { }   // its connect sets the seed once the circuit is up
            else if (_c.Network.FindSimulator(ep) is { } sim) set = sim;
            else if (ours) { a.Connecting = true; wait = true; }
        }
        if (Trace) _log.LogInformation("neighbour guard: seed for {Ep}{What}", ep, set != null ? ", setting" : wait ? ", waiting for its circuit" : "");
        if (ours && set != null) set.SetSeedCaps(seed);
        if (wait) _ = SeedFirst(ep);
        _ = CheckLater();
    }

    /// <summary>A seed for a region nobody is connecting: its EnableSimulator (or a crossing's or teleport's connect)
    /// is usually a moment behind; then connect it ourselves.</summary>
    async Task SeedFirst(IPEndPoint ep)
    {
        await Task.Delay(1500);
        Simulator? sim;
        Uri? seed;
        lock (_gate)
        {
            if (!_known.TryGetValue(ep, out var a)) return;
            seed = a.Seed;
            sim = _c.Network.FindSimulator(ep);
            if (sim != null) a.Connecting = false;
        }
        if (sim != null)
        {
            if (seed != null) sim.SetSeedCaps(seed);
            return;
        }
        Connect(ep);
    }

    /// <summary>Connect one neighbour (blocks until its handshake or LibreMetaverse gives up), then set its seed.</summary>
    void Connect(IPEndPoint ep)
    {
        Announced? a;
        lock (_gate) if (!_known.TryGetValue(ep, out a)) return;
        Simulator? sim = null;
        try
        {
            if (Trace) _log.LogInformation("neighbour guard: connecting {Ep} handle {Handle}", ep, a.Handle);
            sim = _c.Network.Connect(ep, a.Handle, false, null, a.SizeX, a.SizeY);
        }
        catch (Exception e) { _log.LogInformation("neighbour {Ep}: connect failed: {Msg}", ep, e.Message); }
        Uri? seed;
        lock (_gate)
        {
            a.Connecting = false;
            seed = a.Seed;
        }
        if (sim == null) _log.LogInformation("neighbour {Ep} didn't connect", ep);
        else if (seed != null) sim.SetSeedCaps(seed);
    }

    Announced Get(IPEndPoint ep)
    {
        if (!_known.TryGetValue(ep, out var a)) _known[ep] = a = new Announced();
        return a;
    }

    async Task CheckLater()
    {
        try
        {
            foreach (var wait in new[] { 2, 4, 6, 10, 10 })
            {
                await Task.Delay(TimeSpan.FromSeconds(wait));
                Check();
            }
        }
        catch (Exception e) { _log.LogDebug("neighbour guard: {Msg}", e.Message); }
    }

    void Check()
    {
        if (!_c.Network.Connected || _c.Network.CurrentSim is not { } current) return;
        var fixCaps = new List<(Simulator Sim, Uri Seed)>();
        var reconnect = new List<IPEndPoint>();
        lock (_gate)
        {
            foreach (var (ep, a) in _known)
            {
                if (ep.Equals(current.IPEndPoint) || a.Connecting || DateTime.UtcNow - a.At > TimeSpan.FromSeconds(35)) continue;
                var sim = _c.Network.FindSimulator(ep);
                if (Trace) _log.LogInformation("neighbour guard: {Ep} sim={Sim} connected={Connected} caps={Caps}", ep, sim?.Name, sim?.Connected, sim?.Caps != null);
                if (sim is { Connected: true })
                {
                    if (sim.Caps == null && a.Seed != null) fixCaps.Add((sim, a.Seed));
                    continue;
                }
                // announced with a seed, still no circuit a few seconds on: connect it, once
                if (a.Seed == null || a.Handle == 0 || a.Reconnected || DateTime.UtcNow - a.At < TimeSpan.FromSeconds(4)) continue;
                a.Reconnected = a.Connecting = true;
                reconnect.Add(ep);
            }
        }
        foreach (var (sim, seed) in fixCaps)
        {
            _log.LogInformation("neighbour {Region} had no caps; setting them", sim.Name);
            sim.SetSeedCaps(seed, false);
        }
        foreach (var ep in reconnect)
        {
            _log.LogInformation("neighbour {Endpoint} was announced but never connected; connecting it", ep);
            _ = Task.Run(() => Connect(ep));
        }
    }
}
