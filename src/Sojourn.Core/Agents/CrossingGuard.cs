using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.Packets;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// Makes region crossings hold, around what LibreMetaverse 3.2.0 gets wrong when the agent moves into a region it
/// already has a (child) circuit to, which is every walk, flight or ride across a border:
///
/// 1. Its crossing state machine (AgentManager.RegionCrossing.cs) calls Network.Connect, which sends
///    CompleteAgentMovement, makes the region current (SimChanged, caps reconnect) and only then waits for the reply. A
///    region that answers quickly (always on a local grid, possible on the main grid) has its AgentMovementComplete
///    dropped. 30 s later the machine's timeout "recovers": the old region becomes current again with a
///    CompleteAgentMovement of its own (OpenSim makes the agent root there, past its edge), and the new circuit is
///    dropped without a close, so the new region times the root agent out after 60 s and the session logs in afresh
///    at the start location. The guard takes the missed step itself: the machine's own Completed transition, by
///    reflection (RegionCrossed fires as usual); without that (another LibreMetaverse) it asks the region again, and
///    the reply then finds the machine waiting.
/// 2. Connect also clears the circuit's handshake flag and waits for a RegionHandshake that the region sent once, when
///    the circuit opened, and doesn't send again. Until the flag is back, LibreMetaverse sends no AgentUpdate at all
///    (SendManualUpdate), so the region keeps the last controls (a flight carries on over the water) and the session's
///    watchdog, which wants a handshaken region, logs in afresh after 30 s. Once the region has answered the
///    CompleteAgentMovement, the guard restores the flag for a circuit that did complete its handshake.
/// 3. Before any of that, Connect sends UseCircuitCode again on the circuit it already has and blocks, for up to 60 s
///    (the login timeout), until the region acknowledges it; only then does CompleteAgentMovement go out. The viewer
///    never sends UseCircuitCode on a crossing (process_crossed_region sends CompleteAgentMovement and nothing else).
///    On the main grid, 2026-11-01, a region didn't acknowledge it (nor LibreMetaverse's three resends) while the
///    blind tester rode a train over its border: the crossing waited the whole minute, the region never got the
///    agent, the train's script stopped with "Driver missing!" and the session lost its region and logged in again.
///    So for a region whose circuit is already up, the guard lets the wait go at once (the acknowledgement's own
///    event, by reflection), and CompleteAgentMovement follows within milliseconds, as from the viewer. The same for a
///    teleport's TeleportFinish into a neighbour already connected (the same Connect). Recheck on an upgrade.
/// 4. A crossing that arrives while the machine is still busy with the last one (a vehicle over a corner, two
///    borders seconds apart, the first region's reply slow) is ignored by it, so the agent never moves into the
///    region the vehicle carried it to. The guard ends the stale crossing and lets the new one begin, as the viewer
///    simply answers the newest CrossedRegion.
/// A crossing that fails anyway (no answer for 30 s) is logged and told to the person, with what usually follows.
/// For testing: SOJOURN_CROSSING_GUARD=0 turns 3 and 4 off; with it off, SOJOURN_CROSSING_NOACK=MS holds each crossing MS
/// ms before it connects, as the unacknowledged wait did, so what that does to a seated crossing shows on a local grid.
/// Written by Claude Opus 5.5 (1 and 2 2026-10-21; 3 and 4 2026-11-01).
/// </summary>
public sealed class CrossingGuard
{
    readonly GridClient _c;
    readonly ILogger _log;
    readonly ConcurrentDictionary<Simulator, long> _completeAt = new();
    readonly ConcurrentDictionary<Simulator, bool> _handshaken = new();
    readonly ConcurrentDictionary<Simulator, bool> _up = new();   // Connect returned for it: a circuit that's up
    readonly Action<string>? _tell;
    volatile Target? _target;                                     // the newest crossing the grid asked for

    sealed record Target(IPEndPoint Ep, ulong Handle);

    static readonly bool On = Environment.GetEnvironmentVariable("SOJOURN_CROSSING_GUARD") != "0";
    static readonly int NoAckMs = int.TryParse(Environment.GetEnvironmentVariable("SOJOURN_CROSSING_NOACK"), out var ms) ? ms : 0;

    static readonly MethodInfo? Transition = typeof(AgentManager).GetMethod("TransitionCrossingStateAsync", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo? Current = typeof(AgentManager).GetField("_currentCrossing", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo? Handshake = typeof(Simulator).GetField("handshakeComplete", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo? AckField = typeof(Simulator).GetField("GotUseCircuitCodeAck", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo? StateLock = typeof(AgentManager).GetField("_stateLock", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly MethodInfo? LmvCrossed = typeof(AgentManager).GetMethod("CrossedRegionEventHandler", BindingFlags.Instance | BindingFlags.NonPublic);

    CrossingGuard(GridClient c, ILogger log, Action<string>? tell)
    {
        _c = c;
        _log = log;
        _tell = tell;
    }

    /// <summary><paramref name="tell"/>: the person's notice, for a crossing that failed.</summary>
    public static void Attach(GridClient c, ILogger log, Action<string>? tell = null)
    {
        var g = new CrossingGuard(c, log, tell);
        g.WrapCrossedRegion();
        // a teleport into a neighbour already connected meets the same wait (3); the catch-all handler runs on its own
        // task beside LibreMetaverse's, which is blocked in Connect meanwhile
        c.Network.RegisterEventCallback("", (name, msg, _) =>
        {
            if (name == "TeleportFinish" && msg is TeleportFinishMessage tf) g.Release(new IPEndPoint(tf.IP, tf.Port), "teleport");
        });
        c.Network.SimConnected += (_, e) => { if (e.Simulator != null) g._up[e.Simulator] = true; };
        c.Self.RegionCrossed += (_, e) => { if (e.NewSimulator == null) g.Failed(); };
        c.Network.RegisterCallback(PacketType.AgentMovementComplete, (_, e) =>
        {
            if (e.Simulator != null) g._completeAt[e.Simulator] = Environment.TickCount64;
        }, false);
        c.Network.RegisterCallback(PacketType.RegionHandshake, (_, e) =>
        {
            if (e.Simulator != null) g._handshaken[e.Simulator] = true;
        }, false);
        c.Network.SimChanged += (_, _) =>
        {
            // SimChanged fires inside Network.Connect: a crossing is still Connecting then (a login or teleport isn't)
            if (c.Network.CurrentSim is { } sim && c.Self.IsCrossing()) _ = g.WatchAsync(sim, Environment.TickCount64);
        };
        c.Network.SimDisconnected += (_, e) =>
        {
            g._completeAt.TryRemove(e.Simulator, out long _);
            g._handshaken.TryRemove(e.Simulator, out bool _);
            g._up.TryRemove(e.Simulator, out bool _);
        };
    }

    // ---------- 3 and 4: a crossing's start ----------

    /// <summary>LibreMetaverse's own CrossedRegion handler, called by ours once the guard has looked (4 has to come
    /// before the machine refuses the crossing as busy).</summary>
    void WrapCrossedRegion()
    {
        if (LmvCrossed == null)
        {
            _log.LogWarning("region crossing guard: LibreMetaverse has no CrossedRegionEventHandler; crossings start unguarded");
            return;
        }
        Caps.EventQueueCallback lmv;
        try { lmv = (Caps.EventQueueCallback)Delegate.CreateDelegate(typeof(Caps.EventQueueCallback), _c.Self, LmvCrossed); }
        catch (ArgumentException e) { _log.LogWarning("region crossing guard: {Msg}", e.Message); return; }
        _c.Network.UnregisterEventCallback("CrossedRegion", lmv);
        _c.Network.RegisterEventCallback("CrossedRegion", (name, msg, sim) =>
        {
            try
            {
                if (msg is CrossedRegionMessage m)
                {
                    var ep = new IPEndPoint(m.IP, m.Port);
                    _target = new Target(ep, m.RegionHandle);
                    if (On) Supersede(ep);
                    Release(ep, "crossing");
                }
            }
            catch (Exception e) { _log.LogWarning("region crossing guard: {Msg}", e.Message); }
            if (!On && NoAckMs > 0)
            {
                // (test) as LibreMetaverse's own wait did when the region didn't acknowledge: nothing goes out till then
                _log.LogWarning("crossing: held {Ms} ms before connecting (SOJOURN_CROSSING_NOACK)", NoAckMs);
                Thread.Sleep(NoAckMs);
            }
            lmv(name, msg, sim);
        });
    }

    /// <summary>4: a crossing still under way into another region than this one ends, so the machine takes this one.</summary>
    void Supersede(IPEndPoint ep)
    {
        if (!_c.Self.IsCrossing() || Current?.GetValue(_c.Self) is not { } crossing || StateLock?.GetValue(_c.Self) is not SemaphoreSlim gate) return;
        var t = crossing.GetType();
        if (t.GetField("EndPoint")?.GetValue(crossing) is IPEndPoint was && was.Equals(ep)) return;   // the same one again
        if (t.GetField("State") is not { } stateField || !gate.Wait(1000)) return;
        try
        {
            if (!_c.Self.IsCrossing()) return;
            var state = stateField.GetValue(crossing);
            stateField.SetValue(crossing, AgentManager.CrossingState.Failed);
            (t.GetField("WorkCts")?.GetValue(crossing) as CancellationTokenSource)?.Cancel();
            _log.LogInformation("crossing into {Ep} while the last one was still {State}: that one ended, this one begins", ep, state);
        }
        finally { gate.Release(); }
    }

    /// <summary>3: while Network.Connect waits for UseCircuitCode's acknowledgement on a circuit already up to the
    /// region at <paramref name="ep"/>, the wait is let go (CompleteAgentMovement goes out next). Ends once the region is
    /// current (Connect has returned) or after 10 s.</summary>
    void Release(IPEndPoint ep, string why)
    {
        if (!On) return;
        if (AckField == null || _c.Network.FindSimulator(ep) is not { } sim || AckField.GetValue(sim) is not ManualResetEventSlim ack) return;
        // up already: Connect returned for it once, or its region shook hands (a Simulator made for this crossing can't
        // have; its UseCircuitCode opens the circuit, and CircuitGuard sees to that one)
        if (!_up.ContainsKey(sim) && !_handshaken.ContainsKey(sim)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                long start = Environment.TickCount64;
                bool released = false;
                while (Environment.TickCount64 - start < 10_000 && _c.Network.Connected && !ReferenceEquals(_c.Network.CurrentSim, sim))
                {
                    if (!ack.IsSet)
                    {
                        ack.Set();
                        released = true;
                    }
                    await Task.Delay(5).ConfigureAwait(false);
                }
                if (released)
                    _log.LogInformation("{Why} into {Region}: didn't wait for the region to acknowledge UseCircuitCode again",
                        why, sim.Name is { Length: > 0 } n ? n : ep.ToString());
            }
            catch (Exception e) { _log.LogDebug(e, "crossing release"); }
        });
    }

    /// <summary>The machine gave up (30 s without the region taking the agent in): logged, and said with what tends to
    /// follow.</summary>
    void Failed()
    {
        string where = _target is { } t && _c.Network.FindSimulator(t.Ep) is { Name.Length: > 0 } s ? s.Name : "the next region";
        _log.LogWarning("the crossing into {Region} failed: {Details}", where, Details());
        _tell?.Invoke($"The crossing into {where} didn't go through: the region there didn't take you in. If you were riding " +
                      "something, it may have gone on without you. If this region stops answering too, Sojourn logs in again by itself.");
    }

    string Details()
    {
        try { return _c.Self.GetCrossingDetails(); } catch (Exception e) { return e.Message; }
    }

    bool Answered(Simulator sim, long since) => _completeAt.TryGetValue(sim, out long at) && at >= since - 5_000;

    async Task WatchAsync(Simulator sim, long changedAt)
    {
        try
        {
            var self = _c.Self;
            bool resent = false;
            long until = changedAt + 25_000;
            while (self.IsCrossing() && _c.Network.CurrentSim == sim && Environment.TickCount64 < until)
            {
                if (self.GetCrossingState() is AgentManager.CrossingState.WaitingForComplete)
                {
                    // a reply that came in time is being handled on its own; give that a moment
                    if (Answered(sim, changedAt))
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        if (self.GetCrossingState() is not AgentManager.CrossingState.WaitingForComplete) break;
                        if (await CompleteAsync())
                        {
                            _log.LogInformation("crossing into {Region}: its arrival came before LibreMetaverse listened for it; completed the crossing", sim.Name);
                            break;
                        }
                    }
                    if (!resent && Environment.TickCount64 - changedAt > 2_000)
                    {
                        _log.LogInformation("crossing into {Region}: no arrival after 2 s; asking again", sim.Name);
                        _completeAt.TryRemove(sim, out long _);
                        changedAt = Environment.TickCount64;
                        self.CompleteAgentMovement(sim);
                        resent = true;
                    }
                }
                await Task.Delay(20).ConfigureAwait(false);
            }

            // the circuit's handshake, once the region has taken the agent in
            while (_c.Network.CurrentSim == sim && !sim.HandshakeComplete && Environment.TickCount64 < until)
            {
                if (Answered(sim, changedAt) && _handshaken.ContainsKey(sim) && Handshake != null)
                {
                    await Task.Delay(250).ConfigureAwait(false);   // in case the region does send a fresh one
                    if (sim.HandshakeComplete) break;
                    Handshake.SetValue(sim, true);
                    _log.LogInformation("crossing into {Region}: the circuit's handshake is long done; agent updates resume", sim.Name);
                    break;
                }
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            _log.LogWarning("region crossing guard: {Msg}", e.Message);
        }
    }

    /// <summary>The state machine's own step to Completed (clears its timeout, raises RegionCrossed, goes idle).</summary>
    async Task<bool> CompleteAsync()
    {
        if (Transition == null || Current == null) return false;
        var crossing = Current.GetValue(_c.Self);
        if (crossing?.GetType().GetField("WorkCts")?.GetValue(crossing) is not CancellationTokenSource cts) return false;
        if (Transition.Invoke(_c.Self, new object[] { AgentManager.CrossingState.Completed, cts.Token }) is not Task<bool> t) return false;
        return await t.ConfigureAwait(false);
    }
}
