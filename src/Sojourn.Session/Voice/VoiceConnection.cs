using System.Net;
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace Sojourn.Session.Voice;

/// <summary>
/// One peer connection to SL's voice server for one channel through one region: the viewer's
/// LLVoiceWebRTCConnection state machine (connectionStateMachine), stepped on VoiceClient's thread every tick and
/// on every native event. Caps calls run as tasks whose results come back through the client's queue.
/// </summary>
public sealed class VoiceConnection
{
    public enum St { Start, WaitOffer, Provisioning, WaitConnected, WaitData, Up, Retry, Disconnect, WaitLogout, Exit, Closed }

    public readonly string Channel;
    public readonly UUID RegionId;
    public readonly int? ParcelLocalId;
    /// <summary>For non-spatial channels (calls): the credentials from ChatSessionRequest.</summary>
    public readonly string? Credentials;
    public bool Spatial => Credentials == null;

    public St State { get; private set; } = St.Start;
    public long Pc { get; private set; }
    public bool Primary { get; private set; }
    public bool ShuttingDown { get; private set; }
    public string? ServerVersion { get; set; }
    /// <summary>The last failure: 409 channel full, 401 locked, or something else; null while fine.</summary>
    public string? Problem { get; private set; }
    public DateTime UpSince { get; private set; }

    readonly VoiceClient _client;
    string? _offer;
    OSD? _viewerSession;
    bool _dataOpen, _answered, _gathered, _signalling;
    readonly List<(string, string, int)> _candidates = new();
    float _retryWait = 0.5f + Random.Shared.NextSingle();
    DateTime _retryAt, _startedAt;
    /// <summary>A connection not up this long after starting gives up and retries (a request that never answers,
    /// ICE that never completes).</summary>
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    bool _sendEnabled, _capWaitLogged;

    public VoiceConnection(VoiceClient client, string channel, UUID region, int? parcelLocalId = null, string? credentials = null)
    {
        _client = client;
        Channel = channel;
        RegionId = region;
        ParcelLocalId = parcelLocalId;
        Credentials = credentials;
    }

    public override string ToString() => $"{Channel}@{_client.RegionName(RegionId)}";

    public void ShutDown() => ShuttingDown = true;

    /// <summary>SOJOURN_VOICE_LOG=1: log every change of which connection the mic goes out on (tests).</summary>
    static readonly bool SendLog = Environment.GetEnvironmentVariable("SOJOURN_VOICE_LOG") == "1";

    /// <summary>Whether the mic goes out on this connection: the primary spatial one (or a call), when unmuted.</summary>
    public void SetSending(bool on)
    {
        if (Pc == 0 || _sendEnabled == on) return;
        _sendEnabled = on;
        VoiceNative.sv_pc_mute_send(Pc, on ? 0 : 1);
        if (SendLog) _client.Log($"voice {this}: {(on ? "sending" : "not sending")}");
    }

    public void Send(string json)
    {
        if (State == St.Up && Pc != 0) VoiceNative.sv_pc_send(Pc, json);
    }

    public void OnEvent(NativeEvent e)
    {
        switch (e.Kind)
        {
            case NativeEventKind.Offer:
                _offer = e.A;
                if (State == St.WaitOffer) State = St.Provisioning;
                break;
            case NativeEventKind.Candidate:
                if (e.A != null) _candidates.Add((e.A, e.B ?? "", e.N));
                break;
            case NativeEventKind.Gathered:
                _gathered = true;
                break;
            case NativeEventKind.State:
                // 2 connected; 3 disconnected and 4 failed come back as Renegotiate from the shim
                if (e.N == 2 && State == St.WaitConnected) Established();
                break;
            case NativeEventKind.DataOpen:
                _dataOpen = true;
                break;
            case NativeEventKind.DataClosed:
                _dataOpen = false;
                break;
            case NativeEventKind.Data:
                if (e.A != null && !ShuttingDown) _client.OnData(this, e.A);
                break;
            case NativeEventKind.Renegotiate:
                if (State is not (St.Disconnect or St.WaitLogout or St.Exit or St.Closed)) Fail("renegotiate");
                break;
            case NativeEventKind.Error:
                _client.Log($"voice {this}: {e.A}");
                break;
        }
    }

    void Established()
    {
        // spatial connections come up muted and unflagged; the client sets primary and sending each tick
        Primary = false;
        _sendEnabled = false;
        VoiceNative.sv_pc_mute_send(Pc, 1);
        State = St.WaitData;
        _client.OnEstablished(this);
    }

    void Fail(string why)
    {
        Problem ??= why;
        _client.Log($"voice {this}: {why}; retrying in {_retryWait:0.0} s");
        _retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(_retryWait);
        State = St.Retry;
    }

    /// <summary>One step. Returns false once closed for good (the client drops it).</summary>
    public bool Step()
    {
        if (!ShuttingDown) Trickle();
        if (State is St.WaitOffer or St.Provisioning or St.WaitConnected or St.WaitData && !ShuttingDown &&
            DateTime.UtcNow - _startedAt > ConnectTimeout)
            Fail($"not connected after {ConnectTimeout.TotalSeconds:0} s (stuck {State})");
        switch (State)
        {
            case St.Start:
                if (ShuttingDown) { State = St.Closed; break; }
                _offer = null; _viewerSession = null; _answered = _gathered = _dataOpen = false;
                _candidates.Clear();
                _startedAt = DateTime.UtcNow;
                Pc = VoiceNative.sv_pc_new(_client.Ice);
                if (Pc <= 0) { Pc = 0; Fail("no peer connection"); break; }
                _client.Attach(this);
                State = St.WaitOffer;
                break;
            case St.WaitOffer:
                if (ShuttingDown) State = St.Exit;
                break;
            case St.Provisioning:
                if (ShuttingDown) { State = St.Exit; break; }
                Provision();
                break;
            case St.WaitConnected:
                if (ShuttingDown) State = St.Disconnect;
                break;
            case St.WaitData:
                if (ShuttingDown) { State = St.Disconnect; break; }
                if (_dataOpen)
                {
                    State = St.Up;
                    UpSince = DateTime.UtcNow;
                    Problem = null;
                    _retryWait = 0.5f + Random.Shared.NextSingle();
                    VoiceNative.sv_pc_send(Pc, VoiceProtocol.Join(Primary));
                    _client.OnUp(this);
                }
                break;
            case St.Up:
                if (ShuttingDown) { State = St.Disconnect; break; }
                bool primary = !Spatial || RegionId == _client.CurrentRegionId;
                if (primary != Primary)
                {
                    Primary = primary;
                    VoiceNative.sv_pc_send(Pc, VoiceProtocol.Join(Primary));
                    _client.Log($"voice {this}: {(Primary ? "primary (the region you're in)" : "a neighbour now")}");
                }
                break;
            case St.Retry:
                if (DateTime.UtcNow >= _retryAt || ShuttingDown)
                {
                    // back off, by a random bit so everyone doesn't come back at once
                    if (_retryWait < VoiceProtocol.MaxRetrySeconds) _retryWait += 0.5f + Random.Shared.NextSingle();
                    State = St.Disconnect;
                }
                break;
            case St.Disconnect:
                if (_viewerSession != null && _client.Cap(RegionId, "ProvisionVoiceAccountRequest") is { } cap)
                {
                    State = St.WaitLogout;
                    var body = VoiceProtocol.Logout(_viewerSession);
                    _client.Http(cap, body, (_, _) => { if (State == St.WaitLogout) State = St.Exit; });
                }
                else State = St.Exit;
                break;
            case St.WaitLogout:
                break;
            case St.Exit:
                if (Pc != 0)
                {
                    _client.Detach(this);
                    VoiceNative.sv_pc_close(Pc);
                    Pc = 0;
                }
                _client.OnClosed(this);
                State = St.Closed;
                break;
            case St.Closed:
                if (!ShuttingDown) State = St.Start;
                else return false;
                break;
        }
        return true;
    }

    void Provision()
    {
        var cap = _client.Cap(RegionId, "ProvisionVoiceAccountRequest");
        if (cap == null)
        {
            // the region's caps aren't in yet: try again next tick
            if (!_capWaitLogged && DateTime.UtcNow - _startedAt > TimeSpan.FromSeconds(5))
            {
                _capWaitLogged = true;
                _client.Log($"voice {this}: waiting for its caps: {_client.CapDiag(RegionId)}");
            }
            return;
        }
        var body = Spatial
            ? VoiceProtocol.ProvisionSpatial(_offer!, ParcelLocalId)
            : VoiceProtocol.ProvisionMultiAgent(_offer!, Channel, Credentials!);
        State = St.WaitConnected;
        long pc = Pc;
        _client.Http(cap, body, (status, reply) =>
        {
            if (pc != Pc || State != St.WaitConnected) return;
            if (status == HttpStatusCode.OK && VoiceProtocol.ParseProvisionReply(reply) is { } answer)
            {
                _viewerSession = answer.Session;
                _answered = true;
                VoiceNative.sv_pc_answer(Pc, answer.Sdp);
                return;
            }
            Problem = status switch
            {
                HttpStatusCode.Conflict => "channel full",
                HttpStatusCode.Unauthorized => "channel locked",
                _ => $"provisioning failed ({(int)status})",
            };
            Fail(Problem);
        });
    }

    // Candidates go up after the answer, a batch at a time, then "completed" once gathering is done (the viewer's
    // processIceUpdates). A failed POST restarts the connection.
    void Trickle()
    {
        if (!_answered || _signalling || _viewerSession == null) return;
        if (_candidates.Count == 0 && !_gathered) return;
        var cap = _client.Cap(RegionId, "VoiceSignalingRequest");
        if (cap == null) return;
        OSDMap body;
        if (_candidates.Count > 0)
        {
            body = VoiceProtocol.Candidates(_viewerSession, _candidates);
            _candidates.Clear();
        }
        else
        {
            body = VoiceProtocol.CandidatesComplete(_viewerSession);
            _gathered = false;
        }
        _signalling = true;
        long pc = Pc;
        _client.Http(cap, body, (status, _) =>
        {
            _signalling = false;
            if (pc == Pc && (int)status is < 200 or >= 300 && State is St.WaitConnected or St.WaitData or St.Up)
                Fail($"trickle failed ({(int)status})");
        });
    }
}
