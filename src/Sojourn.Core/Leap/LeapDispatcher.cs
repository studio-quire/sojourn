using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Leap;

public delegate Task<JsonNode?> OpHandler(JsonObject args, CancellationToken ct);

/// <summary>Thrown by handlers for a viewer-style {error} reply.</summary>
public sealed class LeapError(string message) : Exception(message);

/// <summary>The session isn't logged in (the bridge's "viewer not connected", HTTP 503).</summary>
public sealed class NotConnected(string message) : Exception(message);

/// <summary>One emulated LEAP API (a viewer event pump with ops).</summary>
public abstract class LeapApi
{
    protected LeapApi(AgentSession session) => S = session;

    protected AgentSession S { get; }
    public abstract string Name { get; }
    public abstract string Desc { get; }
    /// <summary>Ops that work while logged out (most need a session).</summary>
    public virtual bool NeedsSession => true;
    /// <summary>Ops that go ahead while a confirmation waits for the person (the panel's own; see LeapDispatcher.Gate).</summary>
    public virtual bool RunsWhileHeld => false;

    readonly Dictionary<string, (string Desc, OpHandler Run)> _ops = new();
    public IReadOnlyDictionary<string, (string Desc, OpHandler Run)> Ops => _ops;

    protected void Op(string name, string desc, OpHandler run) => _ops[name] = (desc, Rlv(name, run));
    protected void Op(string name, string desc, Func<JsonObject, JsonNode?> run) =>
        _ops[name] = (desc, Rlv(name, (a, _) => Task.FromResult(run(a))));

    /// <summary>RLV's say over every op, for the dispatcher's callers and those that run a handler directly (a key
    /// press, a menu, dictation): refused with why (Rlv/RlvOps.cs), and what comes back with the names and places RLV
    /// hides replaced (docs/rlv.md).</summary>
    OpHandler Rlv(string op, OpHandler run) => async (a, ct) =>
    {
        if (!S.RlvActive || Sojourn.Core.Rlv.RlvEngine.InOwnRead) return await run(a, ct);
        if (Sojourn.Core.Rlv.RlvOps.Check(S, Name, op, a) is { } why)
            throw new LeapError(Sojourn.Core.Rlv.RlvOps.Quiet(Name, op) ? why : S.RlvRefused(why)!);
        return S.RlvFilterResult(await run(a, ct));
    };
}

/// <summary>Routes /cmd requests (pump, data with op) to the emulated APIs.</summary>
public sealed class LeapDispatcher
{
    readonly AgentSession _s;
    readonly Dictionary<string, LeapApi> _apis = new(StringComparer.Ordinal);

    public LeapDispatcher(AgentSession s)
    {
        _s = s;
        foreach (var api in Apis.All(s)) _apis[api.Name] = api;
    }

    public IReadOnlyDictionary<string, LeapApi> ApiMap => _apis;

    /// <summary>An API the host adds (the client's own surfaces).</summary>
    public void Register(LeapApi api) => _apis[api.Name] = api;

    /// <summary>
    /// The host's say over ops before they run, whoever calls them. Returns null when the op may run at once, else a
    /// function that asks: its task completes with null once the op may run, or with the reason it may not (answered
    /// as {error}). The client uses it to wait for the person's own confirmation of spending, deleting, giving,
    /// permission changes and new scripts. The gate may canonicalise the op's arguments (it sees the same object the
    /// op runs with), so what it describes is what runs.
    /// </summary>
    public Func<string, string, JsonObject, Func<Task<string?>>?>? Gate { get; set; }

    // While a gated op waits for the person, and until it's under way, every other op waits (except the panel's own
    // and the gate's lookups): the world the person is shown is the one the op acts on, and nothing can slip in
    // between (moving things into a folder being given, linking more onto an object being deleted, teleporting
    // before a parcel return). Ops already running when it was asked carry on.
    static readonly AsyncLocal<bool> InGate = new();
    static readonly TimeSpan HeldThroughStart = TimeSpan.FromSeconds(2);
    readonly object _holdLock = new();
    int _holds;
    TaskCompletionSource _unheld = Done();

    static TaskCompletionSource Done()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    void Hold()
    {
        lock (_holdLock) if (_holds++ == 0) _unheld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    void Release()
    {
        TaskCompletionSource? free = null;
        lock (_holdLock) if (--_holds == 0) free = _unheld;
        free?.TrySetResult();
    }

    async Task WaitUnheldAsync(CancellationToken ct)
    {
        while (true)
        {
            Task t;
            lock (_holdLock) t = _unheld.Task;
            if (t.IsCompleted) return;
            await t.WaitAsync(ct);
        }
    }

    /// <summary>Holds other ops, asks, and if allowed starts the op (null: only asking, for <see cref="ConfirmAsync"/>).</summary>
    async Task<(string? Refusal, Task<JsonNode?>? Run)> GatedAsync(Func<Task<string?>> ask, Func<Task<JsonNode?>>? start, CancellationToken ct)
    {
        Hold();
        try
        {
            Task<string?> asking;
            InGate.Value = true;
            try { asking = ask(); }
            finally { InGate.Value = false; }
            if (await asking.WaitAsync(ct) is { } refusal) return (refusal, null);
            if (start is null) return (null, null);
            var run = start();
            await Task.WhenAny(run, Task.Delay(HeldThroughStart, CancellationToken.None));
            return (null, run);
        }
        finally { Release(); }
    }

    /// <summary>The gate for a caller outside the ops (the external editor's /rpc): waits as an op would and asks.
    /// Null: go ahead.</summary>
    public async Task<string?> ConfirmAsync(string pump, string op, JsonObject data, CancellationToken ct)
    {
        if (_s.Private.On) return "incognito is on";
        if (!InGate.Value) await WaitUnheldAsync(ct);
        if (Gate?.Invoke(pump, op, data) is not { } ask) return null;
        return (await GatedAsync(ask, null, ct)).Refusal;
    }

    /// <summary>Pumps the bridge reports as listened to (events we emit under these kinds).</summary>
    public static readonly string[] EventPumps = { "LLChat", "LLTeleport", "LLAutopilot", "ClaudeNotify", "TesselEvents" };

    public async Task<JsonNode?> CallAsync(string pump, JsonObject data, bool reply, TimeSpan timeout, CancellationToken ct)
    {
        if (pump is "_command" or "LLLeap" or "LEAP") return Command(data);
        if (!_apis.TryGetValue(pump, out var api))
            return reply ? Json.Error($"{pump}: no such API in this host (sojourn emulates the viewer's APIs; `apis` lists them)") : new JsonObject { ["posted"] = true };
        var op = data.Str("op") ?? "";
        if (!api.Ops.TryGetValue(op, out var handler))
            return reply ? Json.Error($"{pump} has no op {op}") : new JsonObject { ["posted"] = true };
        if (api.NeedsSession && !_s.Online) throw new NotConnected("viewer not connected");

        // incognito (Agents/PrivateMode.cs), blind: only the allowlist runs, its handles become ids, and the caller gets
        // back what the op's rule lets through; seeing, everything runs but for the few rules that still hold
        PrivateMode.Rule? priv = null;
        bool blind = _s.Private.IsBlind;
        if (blind)
        {
            if (_s.Private.Admit(pump, op, data, out var rule) is { } no) return reply ? Json.Error(no) : new JsonObject { ["posted"] = true };
            priv = rule;
        }
        else if (_s.Private.On && _s.Private.AdmitSeeing(pump, op, data) is { } no)
            return reply ? Json.Error(no) : new JsonObject { ["posted"] = true };
        JsonNode? Out(JsonNode? r) => priv != null ? PrivateMode.Reply(priv, r) : blind ? PrivateMode.Plain(r) : r;

        if (!api.RunsWhileHeld && !InGate.Value) await WaitUnheldAsync(ct);
        if (Gate?.Invoke(pump, op, data) is { } ask)
        {
            if (!reply)
            {
                _ = Task.Run(() => GatedAsync(ask, () => Run(pump, op, handler.Run, data, CancellationToken.None), CancellationToken.None));
                return new JsonObject { ["posted"] = true };
            }
            // the wait for the person isn't the op's time: the timeout starts once it's allowed
            var (refusal, started) = await GatedAsync(ask, () => Run(pump, op, handler.Run, data, ct), ct);
            // a refusal can name what it was about; blind, only that it didn't happen
            if (refusal is not null) return Json.Error(blind ? "not done: the person didn't confirm it" : refusal);
            return Out(await started!.WaitAsync(timeout, ct));
        }

        var run = Run(pump, op, handler.Run, data, ct);
        if (!reply)
        {
            _ = run; // runs on; failures are logged
            return new JsonObject { ["posted"] = true };
        }
        return Out(await run.WaitAsync(timeout, ct));
    }

    async Task<JsonNode?> Run(string pump, string op, OpHandler h, JsonObject data, CancellationToken ct)
    {
        try
        {
            return await h(data, ct) ?? new JsonObject();
        }
        catch (LeapError e) { return Json.Error(e.Message); }
        catch (NotConnected) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            _s.Log.LogWarning(e, "{Pump}.{Op} failed", pump, op);
            return Json.Error($"{pump}.{op}: {e.GetType().Name}: {e.Message}");
        }
    }

    JsonNode Command(JsonObject data)
    {
        var op = data.Str("op");
        if (op == "getAPIs")
        {
            var all = new JsonObject();
            foreach (var (name, api) in _apis) all[name] = new JsonObject { ["desc"] = api.Desc, ["key"] = "op" };
            return all;
        }
        if (op == "getAPI")
        {
            var name = data.Str("api") ?? "";
            if (!_apis.TryGetValue(name, out var api)) return new JsonObject();
            var ops = new JsonObject();
            foreach (var (o, (desc, _)) in api.Ops) ops[o] = new JsonObject { ["desc"] = desc };
            return new JsonObject { ["name"] = name, ["desc"] = api.Desc, ["key"] = "op", ["ops"] = ops };
        }
        return Json.Error($"_command has no op {op}");
    }
}
