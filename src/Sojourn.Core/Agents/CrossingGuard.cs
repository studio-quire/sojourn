using System.Collections.Concurrent;
using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// Makes region crossings hold, around two things LibreMetaverse 3.2.0 gets wrong when the agent moves into a region
/// it already has a (child) circuit to, which is every walk or flight across a border:
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
/// </summary>
public sealed class CrossingGuard
{
    readonly GridClient _c;
    readonly ILogger _log;
    readonly ConcurrentDictionary<Simulator, long> _completeAt = new();
    readonly ConcurrentDictionary<Simulator, bool> _handshaken = new();

    static readonly MethodInfo? Transition = typeof(AgentManager).GetMethod("TransitionCrossingStateAsync", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo? Current = typeof(AgentManager).GetField("_currentCrossing", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo? Handshake = typeof(Simulator).GetField("handshakeComplete", BindingFlags.Instance | BindingFlags.NonPublic);

    CrossingGuard(GridClient c, ILogger log)
    {
        _c = c;
        _log = log;
    }

    public static void Attach(GridClient c, ILogger log)
    {
        var g = new CrossingGuard(c, log);
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
        };
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
