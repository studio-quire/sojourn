using System.Net;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.Packets;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// What a teleport is doing, read from the protocol itself. LibreMetaverse 3.2.0's own teleport status misses what a
/// person needs to hear:
/// - A request is answered by TeleportStart, TeleportLocal or TeleportFailed (the message template says so); a refusal
///   comes without a TeleportStart before it. LibreMetaverse drops a TeleportFailed while its status is None or
///   Finished, and its TeleportAsync sets None before sending, so every refusal of that kind went unheard and its
///   GoHomeAsync waited out 40 s ("the region didn't send us home").
/// - It reports arriving only once the new region's circuit is up. When that circuit never answers (the way out on the
///   main grid, 2026-10-28: the region sent the agent home, the UseCircuitCode to the destination got no ACK for 60 s,
///   CircuitGuard's notes) nothing says so, and the session sat between regions until the old one let go.
/// - Its landmark teleport (home) refuses to start while a stuck teleport's status still says Progress.
/// So this listens to the raw messages (UDP TeleportStart, Progress, Failed, Cancel, Local; the event queue's
/// TeleportFailed and TeleportFinish, through the queue's catch-all handler, which runs on its own task: LibreMetaverse's
/// TeleportFinish handler blocks while it connects), logs each step, raises the person's notice for every refusal,
/// lets a caller send a request and await what came of it (<see cref="GoAsync"/>), and says when an arrival has stalled
/// (<see cref="Stalled"/>: AgentSession logs in again, since the grid has already moved the agent).
/// Written by Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class TeleportWatch
{
    public enum Outcome { Arrived, Refused, Stalled, NoAnswer }

    /// <param name="Why">the grid's reason in plain words (Refused), or what didn't happen</param>
    /// <param name="Region">where it went or was going, if known</param>
    public sealed record Result(Outcome Outcome, string? Why, string? Region);

    /// <summary>How long after the grid's TeleportFinish the new region may take to take the agent in.</summary>
    public static readonly TimeSpan ArrivalWait = TimeSpan.FromSeconds(30);
    /// <summary>How long a request may go without any answer (not even TeleportStart).</summary>
    public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(15);
    /// <summary>How long a started teleport may go without TeleportFinish or TeleportFailed.</summary>
    public static readonly TimeSpan StartedWait = TimeSpan.FromSeconds(45);

    readonly AgentSession _s;
    readonly object _lock = new();
    DateTime _startedAt, _failedAt, _cancelledAt, _localAt, _finishAt;
    string? _failWhy, _lastProgress;
    ulong _finishHandle;
    IPEndPoint? _finishEp;
    bool _arrivalPending;
    // the arrival reply (AgentMovementComplete): for which region and when, and whether the last TeleportFinish's
    // region has had it since (Arriving)
    ulong _completeHandle;
    DateTime _completeAt;
    bool _finishArrived;
    int _quiet;   // GoAsync callers that tell the person themselves

    internal TeleportWatch(AgentSession s) => _s = s;

    internal void Wire(GridClient c)
    {
        lock (_lock)
        {
            _startedAt = _failedAt = _cancelledAt = _localAt = _finishAt = _completeAt = default;
            _arrivalPending = false;
            _finishArrived = false;
            _completeHandle = 0;
        }
        c.Network.RegisterCallback(PacketType.TeleportStart, (_, _) => Started(), false);
        c.Network.RegisterCallback(PacketType.TeleportProgress, (_, e) =>
            Progress(Utils.BytesToString(((TeleportProgressPacket)e.Packet).Info.Message)), false);
        c.Network.RegisterCallback(PacketType.TeleportFailed, (_, e) =>
        {
            var p = (TeleportFailedPacket)e.Packet;
            var key = p.AlertInfo is { Length: > 0 } ai ? Utils.BytesToString(ai[0].Message) : null;
            Failed(Utils.BytesToString(p.Info.Reason), key);
        }, false);
        c.Network.RegisterCallback(PacketType.TeleportCancel, (_, _) => Cancelled(), false);
        c.Network.RegisterCallback(PacketType.TeleportLocal, (_, _) => Local(), false);
        WireReply(c);
        c.Network.RegisterEventCallback(string.Empty, (_, msg, _) =>
        {
            if (msg is TeleportFinishMessage f) Finish(c, f.RegionHandle, new IPEndPoint(f.IP, f.Port));
            else if (msg is TeleportFailedMessage m) Failed(m.Reason, m.MessageKey);
        });
    }

    static readonly System.Reflection.MethodInfo? LmvReply =
        typeof(AgentManager).GetMethod("MovementCompleteHandler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// The arrival reply (AgentMovementComplete). LibreMetaverse's handler moves Client.Self to the position it carries,
    /// where the region placed the agent as it took it in. The avatar's own updates from that region only start after
    /// the reply, so one heard before it (the reply lost and resent, or held back for testing) is newer: then the reply
    /// carries that position instead, before LibreMetaverse applies it (setting it back afterwards left a moment in
    /// which the old one could be read: a fall from a high landing point read as 4 m up and back, two cuts in the
    /// headset). So LibreMetaverse's private handler is replaced by one that does that, calls it, then notes the
    /// arrival (by reflection: recheck on an upgrade; without it, the reply is only noted).
    /// For testing on the local grid, where a region answers CompleteAgentMovement at once, SOJOURN_ARRIVAL_DELAY_MS
    /// holds every reply that long first, as a slow main-grid round trip does: the new region is current meanwhile and
    /// the position isn't yet its own (VrPilot's arrival hold). (The region's own updates aren't held back with it, so
    /// the reply is always older than they are there.)
    /// </summary>
    void WireReply(GridClient c)
    {
        // (heard after LibreMetaverse's handler, on the same task: by then Client.Self has the new position)
        void Complete(AgentMovementCompletePacket p)
        {
            lock (_lock) (_completeHandle, _completeAt) = (p.Data.RegionHandle, DateTime.UtcNow);
        }
        if (LmvReply == null)
        {
            c.Network.RegisterCallback(PacketType.AgentMovementComplete, (_, e) => Complete((AgentMovementCompletePacket)e.Packet), false);
            return;
        }
        var lmv = (EventHandler<PacketReceivedEventArgs>)Delegate.CreateDelegate(typeof(EventHandler<PacketReceivedEventArgs>), c.Self, LmvReply);
        c.Network.UnregisterCallback(PacketType.AgentMovementComplete, lmv);
        void Reply(object? sender, PacketReceivedEventArgs e)
        {
            var p = (AgentMovementCompletePacket)e.Packet;
            var handle = p.Data.RegionHandle;
            DateTime finishAt;
            lock (_lock) finishAt = _finishHandle == handle ? _finishAt : default;
            if (finishAt != default && SelfUpdateGuard.NewerThanReply(c, handle, finishAt) is { } newer
                && Vector3.Distance(p.Data.Position, newer) is var off && off > 0.05f)
            {
                _s.Log.LogInformation("teleport: the arrival reply came after the region's own updates; kept theirs ({Off:F1} m apart)", off);
                p.Data.Position = newer;
            }
            lmv(sender, e);
            Complete(p);
        }
        int delay = int.TryParse(Environment.GetEnvironmentVariable("SOJOURN_ARRIVAL_DELAY_MS"), out int ms) ? ms : 0;
        // (on the thread pool, as LibreMetaverse's was: it connects neighbours and cleans up as it goes)
        c.Network.RegisterCallback(PacketType.AgentMovementComplete, delay <= 0 ? Reply
            : (sender, e) => _ = Task.Delay(delay).ContinueWith(_ => Reply(sender, e)), true);
    }

    void Started()
    {
        lock (_lock) { _startedAt = DateTime.UtcNow; _lastProgress = null; }
        _s.Log.LogInformation("teleport: started");
    }

    void Progress(string msg)
    {
        lock (_lock)
        {
            _startedAt = DateTime.UtcNow;   // still going
            if (msg == _lastProgress) return;
            _lastProgress = msg;
        }
        _s.Log.LogInformation("teleport: {Msg}", msg);
    }

    void Failed(string? reason, string? key)
    {
        var why = Plain(reason, key);
        bool quiet;
        lock (_lock)
        {
            // the same refusal can come both ways (UDP and the event queue)
            if (DateTime.UtcNow - _failedAt < TimeSpan.FromSeconds(1) && _failWhy == why) return;
            _failedAt = DateTime.UtcNow;
            _failWhy = why;
            quiet = _quiet > 0;
        }
        _s.Log.LogInformation("teleport refused: {Why}{Raw}", why,
            reason == why && string.IsNullOrEmpty(key) ? "" : $" ({string.Join(", ", new[] { reason, key }.Where(x => !string.IsNullOrEmpty(x)))})");
        if (!quiet) _s.TeleportFailed(why);
        else _s.TeleportFailedHeard();
    }

    void Cancelled()
    {
        lock (_lock) _cancelledAt = DateTime.UtcNow;
        _s.Log.LogInformation("teleport: cancelled");
    }

    void Local()
    {
        lock (_lock) _localAt = DateTime.UtcNow;
        _s.Log.LogInformation("teleport: within the region");
    }

    void Finish(GridClient c, ulong handle, IPEndPoint ep)
    {
        lock (_lock) { _finishAt = DateTime.UtcNow; _finishHandle = handle; _finishEp = ep; _arrivalPending = true; _finishArrived = false; }
        Utils.LongToUInts(handle, out var x, out var y);
        _s.Log.LogInformation("teleport: the grid sent us to {Region} ({Ep})", NameOf(c, handle) ?? $"{x / 256},{y / 256}", ep);
    }

    static string? NameOf(GridClient c, ulong handle)
    {
        var sim = c.Network.Simulators.ToArray().FirstOrDefault(s => s.Handle == handle);
        if (sim is { Name.Length: > 0 }) return sim.Name;
        foreach (var r in c.Grid.RegionsReadOnly.Values)
            if (r.RegionHandle == handle && !string.IsNullOrEmpty(r.Name)) return r.Name;
        return null;
    }

    bool ArrivedAt(GridClient c, ulong handle) =>
        c.Network.CurrentSim is { } cur && cur.Handle == handle && cur.AgentMovementComplete && cur.HandshakeComplete;

    /// <summary>
    /// The region the grid has sent the agent to (its last TeleportFinish) while the agent hasn't arrived there yet:
    /// that region isn't current, or its arrival reply (AgentMovementComplete) hasn't come since. Null otherwise, and
    /// once <see cref="ArrivalWait"/> and a little more have passed (the session logs in again by then). From the
    /// TeleportFinish on, the agent's position isn't to be trusted: LibreMetaverse makes the new region current a
    /// moment after it sends CompleteAgentMovement, so the reply can move the position into the new region's
    /// coordinates while the old region is still current, or come a round trip after the new one is current with the
    /// old region's position still there (the headset holds the view meanwhile: VrPilot's arrival).
    /// </summary>
    // (the TeleportFinish is heard on the event queue's own task; the reply comes at least LibreMetaverse's 500 ms pause
    // before CompleteAgentMovement later, so one a little before the finish was heard is still this arrival's)
    static readonly TimeSpan FinishSlack = TimeSpan.FromMilliseconds(300);

    public ulong? Arriving(GridClient c)
    {
        lock (_lock)
        {
            if (_finishAt == default || _finishArrived) return null;
            if (_completeHandle == _finishHandle && _completeAt >= _finishAt - FinishSlack && c.Network.CurrentSim?.Handle == _finishHandle)
            {
                _finishArrived = true;
                return null;
            }
            if (DateTime.UtcNow - _finishAt > ArrivalWait + TimeSpan.FromSeconds(15)) return null;
            return _finishHandle;
        }
    }

    /// <summary>The region the grid last sent the agent to (TeleportFinish) and when (UTC); 0 before any.</summary>
    public (ulong Handle, DateTime At) LastFinish
    {
        get { lock (_lock) return (_finishHandle, _finishAt); }
    }

    /// <summary>The grid sent the agent to another region over <see cref="ArrivalWait"/> ago and it hasn't arrived:
    /// the circuit to the new region never came up. The old region has let the agent go, so only a new login helps.</summary>
    public string? Stalled(GridClient c)
    {
        ulong handle;
        DateTime at;
        lock (_lock)
        {
            if (!_arrivalPending) return null;
            handle = _finishHandle;
            at = _finishAt;
        }
        if (ArrivedAt(c, handle))
        {
            lock (_lock) if (_finishAt == at) _arrivalPending = false;
            return null;
        }
        if (DateTime.UtcNow - at < ArrivalWait) return null;
        lock (_lock) _arrivalPending = false;
        Utils.LongToUInts(handle, out var x, out var y);
        return NameOf(c, handle) ?? $"the region at {x / 256},{y / 256}";
    }

    /// <summary>
    /// Sends a teleport request (<paramref name="send"/>) and waits for what came of it, from the protocol: arrived (in
    /// the region, or a teleport within it), refused (with the grid's reason in plain words), stalled (the grid sent the
    /// agent on and the new region never took it in), or no answer. <paramref name="quiet"/>: the caller tells the person
    /// about a refusal itself (the usual "Teleport failed" notice isn't raised meanwhile).
    /// </summary>
    public async Task<Result> GoAsync(GridClient c, Action send, string what, bool quiet = false, CancellationToken ct = default)
    {
        var asked = DateTime.UtcNow;
        if (quiet) lock (_lock) _quiet++;
        try
        {
            _s.Log.LogInformation("teleport: asked ({What}, from {Region})", what, c.Network.CurrentSim?.Name);
            send();
            while (true)
            {
                await Task.Delay(100, ct);
                var now = DateTime.UtcNow;
                DateTime started, failed, cancelled, local, finish;
                string? why;
                ulong handle;
                lock (_lock) (started, failed, cancelled, local, finish, why, handle) = (_startedAt, _failedAt, _cancelledAt, _localAt, _finishAt, _failWhy, _finishHandle);
                if (failed >= asked) return Done(new Result(Outcome.Refused, why, null));
                if (cancelled >= asked) return Done(new Result(Outcome.Refused, "the teleport was cancelled", null));
                if (local >= asked) return Done(new Result(Outcome.Arrived, null, c.Network.CurrentSim?.Name));
                if (finish >= asked)
                {
                    if (ArrivedAt(c, handle))
                    {
                        lock (_lock) if (_finishAt == finish) _arrivalPending = false;
                        return Done(new Result(Outcome.Arrived, null, c.Network.CurrentSim?.Name));
                    }
                    if (now - finish > ArrivalWait) return Done(new Result(Outcome.Stalled, "the region it went to never answered", NameOf(c, handle)));
                    continue;
                }
                if (started >= asked ? now - started > StartedWait : now - asked > AnswerWait)
                    return Done(new Result(Outcome.NoAnswer, started >= asked ? "it started but never finished" : "no answer from the region", null));
            }
        }
        finally
        {
            if (quiet) lock (_lock) _quiet--;
        }

        Result Done(Result r)
        {
            _s.Log.LogInformation("teleport ({What}): {Outcome}{Region}{Why} after {Secs:F1} s", what, r.Outcome.ToString().ToLowerInvariant(),
                r.Region is { } reg ? $" in {reg}" : "", r.Why is { } w ? $": {w}" : "", (DateTime.UtcNow - asked).TotalSeconds);
            return r;
        }
    }

    /// <summary>
    /// A teleport refusal in plain words. Second Life sends keys (the viewer's teleport_strings.xml and notifications
    /// names) where OpenSim sends sentences; a key we don't know is said as what it is.
    /// </summary>
    public static string Plain(string? reason, string? key = null)
    {
        foreach (var k in new[] { reason, key })
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            var t = k.Trim();
            if (Known.TryGetValue(t, out var plain)) return plain;
        }
        var text = !string.IsNullOrWhiteSpace(reason) ? reason!.Trim() : key?.Trim();
        if (string.IsNullOrEmpty(text)) return "the region gave no reason";
        // a bare key (no spaces): not a sentence anyone should read as one
        return text.Contains(' ') ? text.TrimEnd('.') : $"the region refused it ({text})";
    }

    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["no_host"] = "the place isn't available right now (the region may be down or restarting)",
        ["no_inventory_host"] = "the inventory system isn't available right now",
        ["invalid_tport"] = "the grid couldn't process the teleport (logging in again may help)",
        ["invalid_region_handoff"] = "the grid couldn't hand you over to the region",
        ["blocked_tport"] = "teleports are blocked for a moment; try again shortly",
        ["nolandmark_tport"] = "the grid couldn't find that place",
        ["timeout_tport"] = "the grid couldn't finish the teleport in time",
        ["expired_tport"] = "the grid couldn't finish the teleport in time",
        ["expired_region_handoff"] = "the region hand-over timed out",
        ["noaccess_tport"] = "you don't have access to that place",
        ["missing_attach_tport"] = "your attachments haven't finished arriving; try again in a few seconds",
        ["too_many_uploads_tport"] = "the region is too busy right now",
        ["NoHelpIslandTP"] = "you can't teleport back to the welcome island",
        ["RegionEntryAccessBlocked"] = "the place's maturity rating is above what your settings allow",
        ["TeleportEntryAccessBlocked"] = "the place's maturity rating is above what your settings allow",
        ["RegionEntryAccessBlocked_AdultsOnlyContent"] = "the place is rated Adult, which your settings don't allow",
        ["TeleportEntryAccessBlocked_AdultsOnlyContent"] = "the place is rated Adult, which your settings don't allow",
        ["Could not teleport closer to destination"] = "the grid couldn't get you closer to that place",
    };
}
