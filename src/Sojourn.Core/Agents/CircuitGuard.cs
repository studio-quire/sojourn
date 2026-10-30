using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// Opening a circuit to a region (at login, on a teleport, a crossing or a neighbour) begins with UseCircuitCode, and
/// LibreMetaverse 3.2.0 sends it once: it resends unacknowledged packets only from timers it starts after the region's
/// handshake, so a lost UseCircuitCode, or one the region dropped because it wasn't expecting the agent yet, leaves it
/// waiting the full 60 s for an ACK that never comes, the teleport stuck between regions. On the main grid's way out,
/// 2026-10-28, the region sent the agent home and the circuit to home never answered ("Failed to get ACK for
/// UseCircuitCode packet" a minute later; the session got back only by logging in again). The viewer sends it
/// reliably, resent until acknowledged, like any reliable message.
/// So, from each new circuit's start, this sends UseCircuitCode again until the region acknowledges one: after 1 s,
/// then every 1.5 s, for up to 55 s (LibreMetaverse's own wait is 60). After that LibreMetaverse's timers take over
/// (CompleteAgentMovement and the rest are resent as usual once the handshake has come). A duplicate on a circuit that's
/// up is harmless: OpenSim acknowledges it and does nothing (checked 2026-10-28), as the viewer's message system does.
/// Checked on the local grid with the destination's datagrams lost for its first 1.2 s: without this the teleport never
/// arrived (the main grid's "Failed to get ACK" a minute on), with it it arrived 3.1 s after the request. Reads
/// LibreMetaverse's private acknowledgement flag by reflection: recheck on an upgrade (it logs a warning if it's gone).
/// For testing: SOJOURN_CIRCUIT_LOSS=PORT:MS sends a new circuit's datagrams to that UDP port nowhere for its first MS ms
/// (a lost UseCircuitCode, or with MS over 30000 a destination that never answers: AgentSession logs in again);
/// SOJOURN_CIRCUIT_GUARD=0 turns the resending off, for comparing.
/// Written by Claude Opus 5.5, 2026-10-28.
/// </summary>
public static class CircuitGuard
{
    static readonly FieldInfo? AckField = typeof(Simulator).GetField("GotUseCircuitCodeAck", BindingFlags.Instance | BindingFlags.NonPublic);
    static bool _warned;

    public static void Attach(GridClient c, ILogger log)
    {
        if (AckField?.FieldType != typeof(ManualResetEventSlim))
        {
            if (!_warned) log.LogWarning("circuit guard: LibreMetaverse has no GotUseCircuitCodeAck; UseCircuitCode isn't resent");
            _warned = true;
            return;
        }
        bool on = Environment.GetEnvironmentVariable("SOJOURN_CIRCUIT_GUARD") != "0";
        var loss = Environment.GetEnvironmentVariable("SOJOURN_CIRCUIT_LOSS")?.Split(':');
        c.Network.SimConnecting += (_, e) =>
        {
            if (e.Simulator is not { } sim) return;
            if (loss is [var port, var ms] && sim.IPEndPoint.Port.ToString() == port && int.TryParse(ms, out var lossMs)) Lose(sim, lossMs, log);
            if (on) _ = Task.Run(() => WatchAsync(c, sim, log));
        };
    }

    /// <summary>SOJOURN_CIRCUIT_LOSS: the circuit's datagrams go to a port nobody listens on for a while.</summary>
    static void Lose(Simulator sim, int ms, ILogger log)
    {
        try
        {
            var circuit = typeof(Simulator).GetProperty("Circuit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sim)!;
            var field = circuit.GetType().BaseType!.GetField("<RemoteEndPoint>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var real = (System.Net.IPEndPoint)field.GetValue(circuit)!;
            field.SetValue(circuit, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9));
            log.LogWarning("circuit to {Ep}: datagrams lost for {Ms} ms (SOJOURN_CIRCUIT_LOSS)", real, ms);
            _ = Task.Delay(ms).ContinueWith(_ => field.SetValue(circuit, real));
        }
        catch (Exception e) { log.LogWarning("SOJOURN_CIRCUIT_LOSS: {Msg}", e.Message); }
    }

    static async Task WatchAsync(GridClient c, Simulator sim, ILogger log)
    {
        try
        {
            var ack = (ManualResetEventSlim)AckField!.GetValue(sim)!;
            var start = DateTime.UtcNow;
            int sent = 0;
            await Task.Delay(1000);
            while (!ack.IsSet && DateTime.UtcNow - start < TimeSpan.FromSeconds(55))
            {
                if (!sim.Connected || !c.Network.Connected || !c.Network.Simulators.Contains(sim)) break;
                sim.SendPacket(new UseCircuitCodePacket
                {
                    CircuitCode = { Code = c.Network.CircuitCode, ID = c.Self.AgentID, SessionID = c.Self.SessionID },
                });
                if (++sent == 1) log.LogInformation("circuit to {Region}: no answer to UseCircuitCode in 1 s; sending it again", sim);
                await Task.Delay(1500);
            }
            if (sent > 0)
                log.LogInformation("circuit to {Region}: {Outcome} after {Sent} more UseCircuitCode", sim, ack.IsSet ? "answered" : "still no answer", sent);
        }
        catch (Exception e) { log.LogDebug(e, "circuit guard"); }
    }
}
