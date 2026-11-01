using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Config;
using Sojourn.Core.Events;
using Sojourn.Core.World;

namespace Sojourn.Core.Agents;

public enum AgentStatus { Offline, LoggingIn, Online, Reconnecting, MfaRequired, TermsRequired, Failed, Stopping }

/// <summary>
/// One resident: a LibreMetaverse session that logs in, stays in world (reconnecting after drops), and logs out
/// cleanly. Each login attempt gets a fresh GridClient, so a wedged session can't poison the next one. Everything
/// that runs on LibreMetaverse's threads is guarded: an exception in one agent never reaches the process.
/// </summary>
public sealed partial class AgentSession : IAsyncDisposable
{
    public AgentConfig Config { get; }
    public HostConfig Host { get; }
    public WorldHub World { get; }
    public EventLog Events { get; }
    /// <summary>Private mode: what callers of the agent API may learn while it's on (PrivateMode.cs).</summary>
    public PrivateMode Private { get; } = new();
    public string StateDir { get; }
    public string GridKey { get; }
    public ILogger Log { get; }

    volatile GridClient? _client;
    public GridClient? Client => _client;

    public AgentStatus Status { get; private set; } = AgentStatus.Offline;
    string _statusMessage = "";
    /// <summary>While online, where I am now (kept current across teleports and crossings); otherwise the last news.</summary>
    public string StatusMessage
    {
        get => Status == AgentStatus.Online && _client?.Network.CurrentSim?.Name is { Length: > 0 } region ? $"in {region}" : _statusMessage;
        private set => _statusMessage = value;
    }
    public double? ConnectedAt { get; private set; }
    public UUID AgentId { get; private set; }
    public int LoginCount { get; private set; }
    public DateTime? LastDisconnect { get; private set; }

    readonly CancellationTokenSource _life = new();
    readonly SemaphoreSlim _wake = new(0);
    volatile bool _wantOnline;
    Task? _loop;
    TaskCompletionSource<string>? _mfaToken;
    TaskCompletionSource<bool>? _termsAnswer;
    // Set only by the person accepting in the client itself, for the next login; never by the agent API.
    bool _agreeToTos, _readCritical;
    TaskCompletionSource<DisconnectedEventArgs>? _disconnected;

    /// <summary>
    /// The grid wants new Terms of Service ("tos") or a critical message ("critical") accepted before login.
    /// Args: kind, the grid's message. Answer with <see cref="AnswerTerms"/>; only the human's own UI may call it.
    /// </summary>
    public event Action<string, string>? TermsRequested;

    /// <summary>The question <see cref="TermsRequested"/> raised, while it waits (for a UI attached after it fired).</summary>
    public (string Kind, string Message)? PendingTerms { get; private set; }

    /// <summary>The grid asks for a multi-factor token. Answer with <see cref="SubmitMfa"/> from the human's own UI.</summary>
    public event Action? MfaRequested;

    /// <summary>How many times this login has asked for a token (2 and on: the last one didn't work).</summary>
    public int MfaAsks { get; private set; }

    /// <summary>Keep the grid's mfa_hash after a login, so the next skips the token: the viewer's "remember this
    /// computer for 30 days". On by default (the console's /mfa); the login sheet sets it from the person's switch.</summary>
    public bool KeepMfaHash { get; set; } = true;

    /// <summary>Why the last login failed (the grid's reason key and message, or ours), until one succeeds or another
    /// is asked for; for the login sheet's plain words.</summary>
    public (string Key, string Message)? LoginFailure { get; private set; }

    public AgentSession(AgentConfig config, HostConfig host, WorldHub world, ILoggerFactory logs)
    {
        Config = config;
        Host = host;
        World = world;
        GridKey = host.GridKeyFor(config);
        StateDir = host.StateDirFor(config);
        Directory.CreateDirectory(StateDir);
        Log = logs.CreateLogger($"agent.{config.Name}");
        Events = new EventLog(Path.Combine(StateDir, "events.jsonl")) { Spell = () => Private.Spell, Sees = () => Private.Sees, PrivateKinds = PrivateMode.OwnKinds };
        Api = new Leap.LeapDispatcher(this);
        State = new AgentState(this);
        Scripts = new Scripts.ScriptEditor(this);
    }

    /// <summary>Whether the session means to be in world: logging in or back in by itself after a drop. False once the
    /// person logged out or a login failed for good (wrong password, terms declined).</summary>
    public bool WantsOnline => _wantOnline;

    /// <summary>True while logged in and connected to a region.</summary>
    public bool Online => Status == AgentStatus.Online && _client is { } c && c.Network.Connected && c.Network.CurrentSim is not null;

    /// <summary>Starts the session's supervising loop (idempotent). Logs in if <paramref name="login"/>.</summary>
    public void Start(bool login)
    {
        _loop ??= Task.Run(RunAsync);
        if (login) RequestLogin();
    }

    public void RequestLogin()
    {
        LoginFailure = null;
        _wantOnline = true;
        _wake.Release();
    }

    /// <summary>Logs out and stays out until asked to log in again.</summary>
    public async Task LogoutAsync()
    {
        _wantOnline = false;
        _mfaToken?.TrySetCanceled();
        _termsAnswer?.TrySetCanceled();
        var c = _client;
        if (c is not null) await LogoutClientAsync(c);
        _wake.Release();
    }

    public void SubmitMfa(string token) => _mfaToken?.TrySetResult(token.Trim());

    public void AnswerTerms(bool accept) => _termsAnswer?.TrySetResult(accept);

    async Task RunAsync()
    {
        var failures = 0;
        while (!_life.IsCancellationRequested)
        {
            if (!_wantOnline)
            {
                SetStatus(AgentStatus.Offline, "logged out");
                try { await _wake.WaitAsync(_life.Token); } catch (OperationCanceledException) { break; }
                continue;
            }
            try
            {
                var outcome = await OneLoginAsync(failures > 0);
                if (outcome == LoginOutcome.Fatal) { _wantOnline = false; failures = 0; continue; }
                if (outcome == LoginOutcome.WasOnline) failures = 0; else failures++;
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "session loop");
                failures++;
            }
            if (!_wantOnline || _life.IsCancellationRequested) continue;
            if (!Config.Reconnect && failures == 0) { _wantOnline = false; continue; }
            // Back off: 5 s, 10 s, 20 s ... capped at 5 min. A region restart usually clears within a minute.
            var delay = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Max(0, failures - 1))));
            SetStatus(AgentStatus.Reconnecting, $"{StatusMessage}; retrying in {delay.TotalSeconds:0} s");
            // A timed wait, so a backoff that runs out leaves no waiter behind. (WhenAny(Delay, WaitAsync) did: the
            // stale waiters, oldest first, swallowed later RequestLogin wakes, so a login request didn't cut a backoff short.)
            try { await _wake.WaitAsync(delay, _life.Token); }
            catch (OperationCanceledException) { }
        }
    }

    enum LoginOutcome { WasOnline, Failed, Fatal }

    static SemaphoreSlim? _loginGate;
    static SemaphoreSlim LoginGate(HostConfig h) =>
        LazyInitializer.EnsureInitialized(ref _loginGate, () => new SemaphoreSlim(Math.Max(1, h.MaxConcurrentLogins)));

    async Task<LoginOutcome> OneLoginAsync(bool isRetry)
    {
        SetStatus(AgentStatus.LoggingIn, isRetry ? "logging in again" : "logging in");
        var client = NewClient();
        _client = client;
        _disconnected = new TaskCompletionSource<DisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = _disconnected;
        client.Network.Disconnected += (_, e) => disconnected.TrySetResult(e);
        var gate = LoginGate(Host);
        var gated = false;
        try
        {
            await gate.WaitAsync(_life.Token);
            gated = true;
            var token = "";
            // (tests: SOJOURN_FAKE_LOGIN_ASK=tos|critical|mfa makes the first login answer as a grid asking that would,
            // without asking the grid, so the login sheet can be tried on a grid that never asks)
            var fake = TakeFakeAsk();
            var ok = fake == null && await client.Network.LoginAsync(LoginParams(client, token), _life.Token);
            while (!ok && (fake?.Key ?? client.Network.LoginErrorKey) is "mfa_challenge" or "tos" or "critical")
            {
                var key = fake?.Key ?? client.Network.LoginErrorKey;
                var message = fake?.Message ?? client.Network.LoginMessage ?? "";
                fake = null;
                if (gated) { gate.Release(); gated = false; }   // don't hold other logins while a person answers
                if (key == "mfa_challenge")
                {
                    var t = await MfaTokenAsync();
                    if (t is null)
                    {
                        LoginFailure = ("mfa_challenge", "no token given");
                        SetStatus(AgentStatus.Failed, "MFA needed: no secret configured and no token given");
                        return LoginOutcome.Fatal;
                    }
                    token = t;
                }
                else if (!await TermsAsync(key, message))
                {
                    LoginFailure = (key, "declined");
                    SetStatus(AgentStatus.Failed, key == "tos" ? "Terms of Service not accepted" : "critical message not accepted");
                    return LoginOutcome.Fatal;
                }
                await gate.WaitAsync(_life.Token);
                gated = true;
                await DisposeClientAsync(client);
                client = NewClient();
                _client = client;
                client.Network.Disconnected += (_, e) => disconnected.TrySetResult(e);
                ok = await client.Network.LoginAsync(LoginParams(client, token), _life.Token);
            }
            if (!ok)
            {
                var key = client.Network.LoginErrorKey;
                var msg = client.Network.LoginMessage;
                Log.LogWarning("login failed: {Key} {Msg}", key, msg);
                LoginFailure = (key ?? "", msg ?? "");
                SetStatus(AgentStatus.Failed, $"login failed: {msg} ({key})");
                Events.Log("bridge", new JsonObject { ["msg"] = "login failed", ["reason"] = key, ["message"] = msg });
                await DisposeClientAsync(client);
                _client = null;
                // Wrong credentials or account trouble won't fix themselves, but grids also answer "key" while a region
                // restarts ("Error connecting to the desired location"); so only give up on an account that has never
                // logged in here. "presence" (already logged in) clears within a minute.
                return LoginCount == 0 && key is "key" or "update" or "tos" or "critical" ? LoginOutcome.Fatal : LoginOutcome.Failed;
            }

            // A login service can answer while its regions are still starting: insist on a real region handshake.
            var arrived = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < arrived && !InRegion(client) && !disconnected.Task.IsCompleted)
                await Task.Delay(250, _life.Token);
            if (!InRegion(client))
            {
                LoginFailure = ("no region", "");
                SetStatus(AgentStatus.Failed, "logged in, but no region answered");
                Events.Log("bridge", new JsonObject { ["msg"] = "login failed", ["reason"] = "no region" });
                return LoginOutcome.Failed;
            }

            // an acceptance covers the login it was given for; a later new ToS gets asked about again
            _agreeToTos = _readCritical = false;
            LoginFailure = null;
            MfaAsks = 0;
            if (KeepMfaHash) SaveMfaHash(client); else ForgetMfaHash();   // (not remembered: the old one was stale anyway)
            try { if (!File.Exists(FirstLoginFile)) File.WriteAllText(FirstLoginFile, DateTime.UtcNow.ToString("o")); } catch { }
            // hold the gate a few seconds more: the avatar bake right after login is the heavy part
            _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => gate.Release());
            gated = false;
            AgentId = client.Self.AgentID;
            LoginCount++;
            ConnectedAt = Json.UnixNow();
            SetStatus(AgentStatus.Online, $"in {client.Network.CurrentSim?.Name}");
            Events.Log("bridge", new JsonObject
            {
                ["msg"] = "viewer connected",
                ["pid"] = Environment.ProcessId,
                ["agent_id"] = AgentId.ToString(),
                ["region"] = client.Network.CurrentSim?.Name,
            });
            OnLoggedIn(client);

            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = _life.Token.Register(() => stop.TrySetResult());
            var watchdog = WatchdogAsync(client);
            var which = await Task.WhenAny(disconnected.Task, stop.Task, WaitForLogoutRequestAsync(), watchdog);
            if (which == disconnected.Task && _wantOnline)
            {
                var e = disconnected.Task.Result;
                Log.LogWarning("disconnected: {Reason} {Msg}", e.Reason, e.Message);
                StatusMessage = $"disconnected ({e.Reason}{(string.IsNullOrEmpty(e.Message) ? "" : ": " + e.Message)})";
                if (e.Reason != NetworkManager.DisconnectType.ClientInitiated)
                    Notify.Add("Disconnected", "notify", "Second Life disconnected you" +
                        (string.IsNullOrWhiteSpace(e.Message) ? "." : $": {e.Message.Trim().TrimEnd('.')}.") + " Logging in again by itself.", Notifications.None);
            }
            else if (which == watchdog && _wantOnline)
            {
                // Out of every region, but the grid may still count the session as logged in, and then refuses the next
                // login for a minute or so ("presence"). A logout through whatever circuit still answers ends it now;
                // one that doesn't answer costs a few seconds.
                await LogoutForRetryAsync(client);
            }
            return LoginOutcome.WasOnline;
        }
        finally
        {
            if (gated) gate.Release();
            var wasOnline = ConnectedAt is not null;
            OnLoggedOut();
            if (_client == client) _client = null;
            await DisposeClientAsync(client);
            if (wasOnline)
            {
                ConnectedAt = null;
                LastDisconnect = DateTime.UtcNow;
                Events.Log("bridge", new JsonObject { ["msg"] = "viewer disconnected", ["reason"] = StatusMessage });
            }
        }
    }

    static bool InRegion(GridClient c) =>
        c.Network.Connected && c.Network.CurrentSim is { } sim && sim.Connected && sim.HandshakeComplete && !string.IsNullOrEmpty(sim.Name);

    /// <summary>Completes if the session stays out of a region for 30 s (a dead circuit LibreMetaverse didn't notice).</summary>
    async Task WatchdogAsync(GridClient c)
    {
        var badSince = (DateTime?)null;
        while (!_life.IsCancellationRequested && _client == c)
        {
            try { await Task.Delay(2000, _life.Token); } catch (OperationCanceledException) { return; }
            // the grid moved the agent on and the new region never took it in: the old one has let go of it, so
            // waiting for the old circuit to drop (and 30 s more) only keeps the person between regions longer
            if (Teleports.Stalled(c) is { } where)
            {
                Log.LogWarning("the teleport to {Region} never arrived (the region didn't answer); logging in again", where);
                StatusMessage = $"the teleport to {where} never arrived";
                Notify.Add("TeleportStalled", "notify",
                    $"The teleport to {where} didn't get through: the region there didn't answer. Logging in again.", Notifications.None);
                return;
            }
            if (InRegion(c)) { badSince = null; continue; }
            badSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - badSince > TimeSpan.FromSeconds(30))
            {
                Log.LogWarning("no region for 30 s; reconnecting");
                StatusMessage = "lost the region (watchdog)";
                Notify.Add("RegionLost", "notify",
                    "Lost the connection: no region has answered for half a minute. Logging in again by itself; this can take a minute or two.",
                    Notifications.None);
                return;
            }
        }
        // the loop ending without trouble (logout) must not look like a watchdog trip
        await Task.Delay(Timeout.Infinite, _life.Token).ContinueWith(_ => { });
    }

    async Task WaitForLogoutRequestAsync()
    {
        while (_wantOnline && !_life.IsCancellationRequested)
        {
            try { await Task.Delay(500, _life.Token); } catch (OperationCanceledException) { return; }
        }
    }

    async Task<string?> MfaTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(Config.MfaSecret)) return Totp.Now(Config.MfaSecret!);
        _mfaToken = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        MfaAsks++;
        SetStatus(AgentStatus.MfaRequired, "the grid asks for a multi-factor token; enter it in the client");
        Events.Log("bridge", new JsonObject { ["msg"] = "mfa required" });
        MfaRequested?.Invoke();
        try { return await _mfaToken.Task.WaitAsync(TimeSpan.FromMinutes(15), _life.Token); }
        catch { return null; }
    }

    /// <summary>
    /// Like the official viewer: logins go out without agreeing, and a "tos"/"critical" refusal waits for the person
    /// to read and accept in the client's own UI before retrying with the flag set. The agent API sees only that
    /// login is waiting on the person.
    /// </summary>
    async Task<bool> TermsAsync(string kind, string message)
    {
        _termsAnswer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetStatus(AgentStatus.TermsRequired, kind == "tos"
            ? "the grid asks you to accept updated Terms of Service; answer in the client"
            : "the grid has a message you must read and accept; answer in the client");
        Events.Log("bridge", new JsonObject { ["msg"] = "terms required", ["kind"] = kind });
        PendingTerms = (kind, message);
        TermsRequested?.Invoke(kind, message);
        bool accepted;
        try { accepted = await _termsAnswer.Task.WaitAsync(_life.Token); }
        catch { return false; }
        finally { PendingTerms = null; }
        if (accepted) { if (kind == "tos") _agreeToTos = true; else _readCritical = true; }
        return accepted;
    }

    GridClient NewClient()
    {
        var c = new GridClient();
        var s = c.Settings;
        s.AssetCache.Dir = Host.CacheDirFor(Config);
        Directory.CreateDirectory(s.AssetCache.Dir);
        // Shared world: every session in a region writes the same prim objects, so the per-client
        // interpolation timer would advance them once per session. Positions come from updates instead.
        s.World.UseInterpolationTimer = false;
        s.World.CachePrimitives = false;   // LMV's own pool keys on a handle that is still 0 when it's built
        s.Parcel.PoolParcelData = false;
        s.Parcel.AlwaysRequestDwell = false;
        s.Parcel.AlwaysRequestAcl = false;
        // Neighbour connections (child agents): a region only lets an avatar cross its border when the client has
        // set up the neighbour. They also feed the neighbours' shared worlds, so a renderer can see across borders.
        s.Agent.MultipleSims = true;
        s.Connection.MfaEnabled = true;
        c.Self.Movement.Camera.Far = Config.DrawDistance;
        World.Bind(c, GridKey, m => Log.LogInformation("{Msg}", m));
        Xfer = new XferFetch();
        Xfer.Attach(c);
        // the region taking items out of inventory itself (a module spending stone, say): the viewer drops them
        // from its inventory view; LibreMetaverse doesn't, so the listing would keep showing them
        c.Network.RegisterCallback(global::LibreMetaverse.Packets.PacketType.RemoveInventoryItem, (_, e) =>
        {
            if (e.Packet is not global::LibreMetaverse.Packets.RemoveInventoryItemPacket rp || c.Inventory.Store is not { } store) return;
            foreach (var d in rp.InventoryData)
                if (store.Contains(d.ItemID)) store.RemoveNodeFor(store[d.ItemID]);
        }, false);
        WireEvents(c);
        foreach (var hook in ClientHooks) hook(c);
        return c;
    }

    /// <summary>Run on every new GridClient before it logs in (e.g. the renderer's packet listeners). Add at startup.</summary>
    public static readonly List<Action<GridClient>> ClientHooks = new();

    LoginParams LoginParams(GridClient c, string token)
    {
        var p = c.Network.DefaultLoginParams(Config.First, Config.Last, Password(), Host.Channel, Host.Version);
        p.URI = Host.LoginUriFor(Config);
        p.Start = !string.IsNullOrEmpty(Config.FirstStart) && !File.Exists(FirstLoginFile) ? Config.FirstStart : Config.Start;
        p.MfaEnabled = true;
        p.Token = token;
        p.MfaHash = ReadMfaHash();
        p.Timeout = 60_000;
        p.Author = "sojourn";
        // LibreMetaverse defaults both to true, silently accepting on the person's behalf
        p.AgreeToTos = _agreeToTos;
        p.ReadCritical = _readCritical;
        return p;
    }

    string Password()
    {
        if (!string.IsNullOrEmpty(Config.PasswordFile))
            return File.ReadLines(Host.Resolve(Config.PasswordFile)).First().Trim();
        return Config.Password ?? "";
    }

    string MfaHashFile => Path.Combine(StateDir, "mfa_hash");
    string FirstLoginFile => Path.Combine(StateDir, "first_login");

    string ReadMfaHash()
    {
        try { return File.Exists(MfaHashFile) ? File.ReadAllText(MfaHashFile).Trim() : ""; } catch { return ""; }
    }

    void ForgetMfaHash()
    {
        try { File.Delete(MfaHashFile); } catch { }
    }

    /// <summary>Tests: the reason SOJOURN_FAKE_LOGIN_ASK names (tos, critical, mfa), once a process.</summary>
    static int _fakeAsked;
    static (string Key, string Message)? TakeFakeAsk()
    {
        var ask = Environment.GetEnvironmentVariable("SOJOURN_FAKE_LOGIN_ASK");
        if (string.IsNullOrEmpty(ask) || Interlocked.Exchange(ref _fakeAsked, 1) == 1) return null;
        return ask switch
        {
            "tos" => ("tos", "(a test) The Terms of Service have changed. Please read them at https://lindenlab.com/tos and agree to go on."),
            "critical" => ("critical", "(a test) A message from the grid you must read before going on: the region you last visited is closed for maintenance."),
            "mfa" => ("mfa_challenge", "(a test) Multifactor authentication required"),
            _ => null,
        };
    }

    void SaveMfaHash(GridClient c)
    {
        var h = c.Network.LoginResponseData?.MfaHash;
        if (string.IsNullOrEmpty(h)) return;
        try { File.WriteAllText(MfaHashFile, h); } catch { }
    }

    async Task LogoutClientAsync(GridClient c)
    {
        SetStatus(AgentStatus.Stopping, "logging out");
        try
        {
            if (c.Network.Connected)
                await c.Network.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { Log.LogDebug(ex, "logout"); }
        _disconnected?.TrySetResult(new DisconnectedEventArgs(NetworkManager.DisconnectType.ClientInitiated, "logout"));
    }

    async Task LogoutForRetryAsync(GridClient c)
    {
        try
        {
            if (c.Network.Connected && c.Network.CurrentSim is { Connected: true })
            {
                await c.Network.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(4));
                Log.LogInformation("logged out of what was left of the session, so the next login isn't refused as still logged in");
            }
        }
        catch (Exception ex) { Log.LogDebug(ex, "logout before logging in again"); }
    }

    async Task DisposeClientAsync(GridClient c)
    {
        World.Unbind(c);
        try
        {
            if (c.Network.Connected)
                await c.Network.ShutdownAsync(NetworkManager.DisconnectType.ClientInitiated, "").WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { }
        try { await c.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }

    void SetStatus(AgentStatus s, string msg)
    {
        Status = s;
        StatusMessage = msg;
    }

    public async ValueTask DisposeAsync()
    {
        _wantOnline = false;
        var c = _client;
        if (c is not null) await LogoutClientAsync(c);
        _life.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        }
        State.Dispose();
        Events.Dispose();
    }

    /// <summary>Runs <paramref name="a"/> and swallows (logs) anything it throws; for LibreMetaverse callbacks.</summary>
    internal void Guard(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.LogWarning(ex, "{What} handler failed", what); }
    }
}

/// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits), as authenticator apps compute for Second Life's MFA.</summary>
public static class Totp
{
    public static string Now(string base32Secret, DateTimeOffset? at = null)
    {
        var key = Base32(base32Secret);
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var hash = HMACSHA1.HashData(key, msg);
        var off = hash[^1] & 0x0f;
        var bin = (hash[off] & 0x7f) << 24 | hash[off + 1] << 16 | hash[off + 2] << 8 | hash[off + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    static byte[] Base32(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = new StringBuilder();
        foreach (var ch in s.ToUpperInvariant()) if (alphabet.Contains(ch)) clean.Append(ch);
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in clean.ToString())
        {
            buffer = buffer << 5 | alphabet.IndexOf(ch);
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return bytes.ToArray();
    }
}
