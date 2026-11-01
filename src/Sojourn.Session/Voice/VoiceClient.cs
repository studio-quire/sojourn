using System.Collections.Concurrent;
using System.Net;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Sojourn.Core.Agents;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Sojourn.Session.Voice;

public enum VoiceMode { PushToTalk, OpenMic }

/// <summary>Where the person hears voice from (the viewer's VoiceEarLocation): the camera (in the headset, the head),
/// the avatar, or the avatar's place with the camera's (head's) direction.</summary>
public enum VoiceEar { Camera, Avatar, Mixed }

/// <summary>A call someone is making to you, waiting for your answer (60 s). Kind: p2p, group (a group of yours) or
/// conference (ad-hoc); SessionName the group's or conference's name.</summary>
public sealed record IncomingCall(UUID Session, UUID From, string Name, string Kind, DateTime At, string? SessionName = null)
{
    public bool P2P => Kind == "p2p";
}

/// <summary>Someone in the voice channel, as the server reports them.</summary>
public sealed record VoicePeer(UUID Id, UUID Region, float Power, bool Speaking, bool ModeratorMuted);

/// <summary>
/// SL voice for the person in the client: which channel to be in (the estate's, with connections to the
/// neighbouring regions within 100 m; a parcel's own; none), the connections' state machines, who's in it and
/// speaking, the poses sent to the server's mixer, and the mic: push-to-talk or open mic with a mute, never while
/// the assistant's button is held (the shim's gate), never during a teleport. The viewer's llvoicewebrtc.cpp is
/// the model; VoiceProtocol has the wire formats. Runs on its own thread at the viewer's 100 ms tick, woken at once
/// by native events and by the mic's buttons.
/// </summary>
public sealed class VoiceClient : IDisposable
{
    readonly AgentSession _session;
    readonly Action<string> _log;
    readonly Thread _thread;
    readonly ConcurrentQueue<Action> _queue = new();
    readonly Dictionary<long, VoiceConnection> _byPc = new();
    volatile bool _stop;
    public string Ice { get; }

    Session? _current, _next;
    readonly List<Session> _closing = new();

    // the mic
    volatile bool _pttHeld, _muted = true, _hidden, _enabled = true;
    volatile VoiceMode _mode = VoiceMode.PushToTalk;
    bool _micOpen, _tracksOn;
    DateTime _tracksOffAt = DateTime.MaxValue;
    DateTime _unhideAt = DateTime.MaxValue;

    // poses: the listener from the render loop (camera, or the headset's head), global metres, SL frame
    readonly object _poseLock = new();
    Vector3d _listenerPos;
    Quaternion _listenerRot = Quaternion.Identity;
    DateTime _listenerAt;
    Vector3d _sentSpeaker, _sentListener;
    Quaternion _sentSpeakerRot, _sentListenerRot;
    bool _poseDirty = true;

    // per-peer choices for my own mix, re-sent whenever someone joins
    readonly ConcurrentDictionary<UUID, bool> _peerMutes = new();
    readonly ConcurrentDictionary<UUID, float> _peerGains = new();

    DateTime _parcelAskedAt;
    bool _movedLocal;
    string? _neighboursLogged;
    DateTime _regionSince = DateTime.UtcNow, _poseLoggedAt, _dataLoggedAt;
    static readonly bool PoseLog = Environment.GetEnvironmentVariable("SOJOURN_VOICE_LOG") == "1";
    public string Status { get; private set; } = "starting";
    public bool Available { get; }
    public bool MicOpen => _micOpen;
    /// <summary>The capture device as it last opened; null while there's none (unplugged, nothing to open: the shim
    /// sends silence, as a muted mic, until one appears).</summary>
    public string? MicDevice { get; private set; }
    /// <summary>The output device as it last opened; null while there's none (the mix runs on, unheard).</summary>
    public string? SpeakerDevice { get; private set; }
    /// <summary>Raised on the voice thread when the shim's devices change (it follows Windows' itself): mic (else the
    /// output), what ("open": opened, or moved to another device such as the new default; "lost": the device in use
    /// went; "failed": nothing to open; "silent": open but no audio, or taking none, for a second) and the device's name
    /// (for "failed", what was asked for).</summary>
    public event Action<bool, string, string?>? DeviceChanged;
    public bool ModeratorMuted { get; private set; }
    /// <summary>The region or parcel you're on has voice off (and no call is up): nothing you say goes anywhere.</summary>
    public bool OffHere { get; private set; }
    public string? Channel => _current?.Channel;
    public UUID CurrentRegionId { get; private set; }
    volatile IReadOnlyList<VoicePeer> _peers = Array.Empty<VoicePeer>();
    public IReadOnlyList<VoicePeer> Peers => _peers;
    /// <summary>Raised on the voice thread when the status line changes (for the console log).</summary>
    public event Action<string>? StatusChanged;
    /// <summary>Raised when the mic opens or closes toward residents, or open mic's mute changes.</summary>
    public event Action? MicChanged;

    public VoiceClient(AgentSession session, string loginUri, bool headset, Action<string> log)
    {
        _session = session;
        _log = log;
        Ice = VoiceProtocol.IceServers(loginUri);
        // libwebrtc's own warnings are chatty (every adapter's failed STUN ping): only its errors, and the shim's warnings
        VoiceNative.Log = (level, msg) => { if (level >= 3 || (level >= 2 && !msg.StartsWith("webrtc:"))) _log(msg); };
        Available = Audio.AudioEngine.Init(out var error);
        if (!Available)
        {
            Status = error ?? "no voice engine";
            _log($"voice: {Status}");
            _thread = new Thread(() => { }) { IsBackground = true };
            return;
        }
        VoiceNative.sv_set_mute(1, 0);
        // tests: SOJOURN_VOICE_TEST_ECHO=delay_ms,gain feeds what's played back into the mic (an echo to cancel);
        // SOJOURN_VOICE_APM=off turns the audio processing off (a control run)
        if (Environment.GetEnvironmentVariable("SOJOURN_VOICE_TEST_ECHO") is { Length: > 0 } echo && echo.Split(',') is [var d, var gn])
            VoiceNative.sv_test_echo(int.Parse(d), float.Parse(gn, System.Globalization.CultureInfo.InvariantCulture));
        if (Environment.GetEnvironmentVariable("SOJOURN_VOICE_APM") == "off") VoiceNative.sv_set_processing(0, 0, 0, 0);
        Audio.AudioEngine.StartDevices(loginUri, headset, mic: true, _log);
        _thread = new Thread(Run) { Name = "voice", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    // the devices (the headset's pair, the defaults, test files): Audio/AudioEngine.cs

    // ---- the mic: called from any thread ----

    public VoiceMode Mode
    {
        get => _mode;
        set { _mode = value; if (value == VoiceMode.OpenMic) _muted = true; Poke(); }
    }
    public bool Enabled { get => _enabled; set { _enabled = value; Poke(); } }
    public bool Muted => _muted;
    public void PushToTalk(bool held) { if (_pttHeld == held) return; _pttHeld = held; Poke(); }
    /// <summary>Open mic's mute. Muting is anyone's (the assistant may); unmuting is the person's own press.</summary>
    public void SetMuted(bool muted) { if (_muted == muted) return; _muted = muted; Poke(); MicChanged?.Invoke(); }
    public void ToggleMute() => SetMuted(!_muted);
    /// <summary>The assistant's button: while held, the mic goes to the assistant and nothing to residents; on release
    /// a 250 ms tail goes on to the assistant (the shim's gate).</summary>
    public void AssistantGate(bool open)
    {
        if (Available) VoiceNative.sv_set_gate(open ? 1 : 0, 250);
    }
    /// <summary>How loud residents are, as a share of the viewer's own level: its AudioLevelVoice (0.5 by default)
    /// scales the server's mix (llvieweraudio.cpp, LLWebRTCPeerConnectionImpl::setReceiveVolume) as AudioLevelSFX does
    /// world sounds, so 1 here keeps the viewer's balance between voices and the world's sounds.</summary>
    public void SetVolume(float v) { if (Available) VoiceNative.sv_set_voice_volume(Math.Clamp(v, 0, 2) * ViewerLevel); }
    const float ViewerLevel = 0.5f;
    /// <summary>A resident's voice muted or unmuted in the person's hearing: on the account's own block list (SL's
    /// mute list with its voice flag, as the viewer's Mute Voice) when there's one, else kept on this machine.</summary>
    public void SetPeerMute(UUID id, bool mute)
    {
        if (ListMute != null)
        {
            ListMute(id, mute);
            _peerMutes.TryRemove(id, out _);
        }
        else _peerMutes[id] = mute;
        Post(() => SendPeerChoices(id));
        PeerChoicesChanged?.Invoke();
    }
    public void SetPeerVolume(UUID id, float v) { _peerGains[id] = Math.Clamp(v, 0, 2); Post(() => SendPeerChoices(id)); PeerChoicesChanged?.Invoke(); }

    /// <summary>The account's block list: whether someone's voice is muted there (a voice mute or a full block), and
    /// muting or unmuting their voice on it (Voice/VoiceWiring hooks AgentSession.Mutes).</summary>
    public Func<UUID, bool>? ListMuted { get; set; }
    public Action<UUID, bool>? ListMute { get; set; }

    /// <summary>The block list changed (here, from the grid, or a block): voice mutes follow it.</summary>
    public void MuteListChanged() => Post(ResendMutes);

    /// <summary>The person's choices per resident (volumes other than 1, voice mutes), which the settings keep between
    /// sessions as the viewer keeps its per-speaker volumes. Voice mutes live on the block list since 2026-10-29; the
    /// ones kept here from before move there at the first login (MoveLocalMutes).</summary>
    public event Action? PeerChoicesChanged;
    public IEnumerable<(UUID Id, float Volume)> PeerVolumes => _peerGains.Where(kv => Math.Abs(kv.Value - 1) > 0.001f).Select(kv => (kv.Key, kv.Value));
    public IEnumerable<UUID> PeerMuted => _peerMutes.Where(kv => kv.Value).Select(kv => kv.Key);
    public float PeerVolume(UUID id) => _peerGains.TryGetValue(id, out var v) ? v : 1;
    public bool PeerIsMuted(UUID id) => (_peerMutes.TryGetValue(id, out var m) && m) || (ListMuted?.Invoke(id) ?? false);

    /// <summary>Voice mutes kept on this machine (from before the block list carried them) moved onto it, once logged in.</summary>
    public void MoveLocalMutes()
    {
        if (ListMute == null) return;
        var local = PeerMuted.ToList();
        if (local.Count == 0) return;
        foreach (var id in local)
        {
            ListMute(id, true);
            _peerMutes.TryRemove(id, out _);
        }
        _log($"voice: {local.Count} voice mute{(local.Count == 1 ? "" : "s")} kept on this machine moved to the block list");
        PeerChoicesChanged?.Invoke();
    }

    /// <summary>What was last told the servers about each resident's mute in my mix.</summary>
    readonly Dictionary<UUID, bool> _sentMutes = new();

    void ResendMutes()
    {
        var known = AllSessions().SelectMany(s => s.Peers.Keys).Where(id => id != Self).Distinct().ToList();
        var changed = new Dictionary<UUID, bool>();
        foreach (var id in known)
        {
            bool now = PeerIsMuted(id);
            if ((_sentMutes.TryGetValue(id, out var was) && was) != now) changed[id] = _sentMutes[id] = now;
        }
        if (VoiceProtocol.MuteAndGain(changed, new Dictionary<UUID, float>()) is not { } msg) return;
        foreach (var s in AllSessions())
            foreach (var conn in s.Connections) conn.Send(msg);
    }

    /// <summary>Where the listener is (the viewer's VoiceEarLocation): the camera by default (the head in the headset).</summary>
    public volatile VoiceEar Ear = VoiceEar.Camera;

    /// <summary>Saved choices, at start: each reaches the server as that resident joins voice (OnParticipants).</summary>
    public void RestorePeerChoices(IEnumerable<(UUID Id, float Volume)> volumes, IEnumerable<UUID> muted)
    {
        foreach (var (id, v) in volumes) _peerGains[id] = Math.Clamp(v, 0, 2);
        foreach (var id in muted) _peerMutes[id] = true;
    }

    /// <summary>The listener's pose for this frame: the camera on the desktop, the head in the headset (region-local
    /// position, forward and up in SL's frame).</summary>
    public void SetListener(ulong region, NVector3 position, NVector3 forward, NVector3 up)
    {
        if (region == 0 || forward.LengthSquared() < 1e-6f) return;
        var origin = VoiceProtocol.RegionOrigin(region);
        var x = NVector3.Normalize(forward);
        var z = up.LengthSquared() > 1e-6f ? NVector3.Normalize(up - x * NVector3.Dot(up, x)) : NVector3.UnitZ;
        if (z.LengthSquared() < 1e-6f || float.IsNaN(z.X)) z = NVector3.UnitZ;
        var y = NVector3.Cross(z, x);
        var m = new System.Numerics.Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        var q = NQuaternion.Normalize(NQuaternion.CreateFromRotationMatrix(m));
        lock (_poseLock)
        {
            _listenerPos = new Vector3d(origin.X + position.X, origin.Y + position.Y, position.Z);
            _listenerRot = new Quaternion(q.X, q.Y, q.Z, q.W);
            _listenerAt = DateTime.UtcNow;
        }
    }

    /// <summary>Hooks a (re)created client: teleports hide voice, as the viewer's do.</summary>
    public void Hook(GridClient c)
    {
        c.Network.RegisterEventCallback("ChatterBoxInvitation", (_, msg, _) =>
        {
            if (msg is not LibreMetaverse.Messages.Linden.ChatterBoxInvitationMessage m || !m.Voice) return;
            if (m.FromAgentID == c.Self.AgentID) return;   // the viewer ignores invites from itself
            // the viewer's three kinds (LLIMMgr::inviteToSession): P2P, a group of mine, else an ad-hoc conference
            bool p2p = !m.GroupIM && m.IMSessionID == P2PSession(c.Self.AgentID, m.FromAgentID);
            string kind = p2p ? "p2p" : m.GroupIM || _session.State.Groups.ContainsKey(m.IMSessionID) ? "group" : "conference";
            var call = new IncomingCall(m.IMSessionID, m.FromAgentID, m.FromAgentName ?? m.FromAgentID.ToString(), kind, DateTime.UtcNow,
                kind == "group" && _session.State.Groups.TryGetValue(m.IMSessionID, out var g) ? g.Name : null);
            Post(() => Ring(call));
        });
        // an ad-hoc conference this client started: its real session id arrives here, then its voice channel is asked for
        c.Self.GroupChatJoined += (_, e) => Post(() =>
        {
            if (!_conferences.TryGetValue(e.TmpSessionID, out var pending)) return;
            _conferences.Remove(e.TmpSessionID);
            if (!e.Success || e.SessionID == UUID.Zero)
            {
                _calling = false;
                _log($"voice: the conference with {pending.Name} didn't start");
                return;
            }
            JoinCall(e.SessionID, pending.Name, p2p: false, pending.ByPerson);
        });
        c.Self.TeleportProgress += (_, e) =>
        {
            switch (e.Status)
            {
                case TeleportStatus.Start:
                case TeleportStatus.Progress:
                    _hidden = true;
                    _unhideAt = DateTime.MaxValue;
                    Poke();
                    break;
                case TeleportStatus.Finished:
                case TeleportStatus.Failed:
                case TeleportStatus.Cancelled:
                    _unhideAt = DateTime.UtcNow.AddMilliseconds(VoiceProtocol.HiddenRestoreMs);
                    Poke();
                    break;
            }
        };
    }

    void Poke() => VoiceNative.Wake.Set();
    void Post(Action a) { _queue.Enqueue(a); Poke(); }
    public void Log(string m) => _log(m);

    // ---- the thread ----

    void Run()
    {
        var next = DateTime.UtcNow;
        while (!_stop)
        {
            VoiceNative.Wake.WaitOne(Math.Max(0, (int)(next - DateTime.UtcNow).TotalMilliseconds));
            try
            {
                while (_queue.TryDequeue(out var a)) a();
                while (VoiceNative.Events.TryDequeue(out var e)) Dispatch(e);
                ApplyMic();
                if (DateTime.UtcNow >= next)
                {
                    next = DateTime.UtcNow + VoiceProtocol.Tick;
                    Tick();
                }
            }
            catch (Exception ex) { _log($"voice: {ex.GetType().Name}: {ex.Message}"); }
        }
        // leave politely: each connection's logout, then close (bounded)
        foreach (var s in AllSessions()) s.ShutDown();
        var until = DateTime.UtcNow.AddSeconds(2);
        while (AllSessions().Any() && DateTime.UtcNow < until)
        {
            while (_queue.TryDequeue(out var a)) a();
            while (VoiceNative.Events.TryDequeue(out var e)) Dispatch(e);
            StepSessions();
            Thread.Sleep(20);
        }
    }

    IEnumerable<Session> AllSessions()
    {
        if (_current != null) yield return _current;
        if (_next != null) yield return _next;
        foreach (var s in _closing) yield return s;
    }

    void Dispatch(NativeEvent e)
    {
        if (e.Kind == NativeEventKind.Device)
        {
            bool mic = e.N == 0;
            string what = e.A ?? "";
            if (what == "open") { if (mic) MicDevice = e.B; else SpeakerDevice = e.B; }
            else if (what is "lost" or "failed") { if (mic) MicDevice = null; else SpeakerDevice = null; }
            _log($"voice: {(mic ? "mic" : "speaker")} {what}: {e.B}");
            if (mic && what is "silent" or "failed" or "lost") SetStatus($"the mic ({e.B}) is {what}");
            try { DeviceChanged?.Invoke(mic, what, e.B); } catch (Exception ex) { _log($"voice: device handler: {ex.Message}"); }
            return;
        }
        if (_byPc.TryGetValue(e.Pc, out var conn)) conn.OnEvent(e);
    }

    internal void Attach(VoiceConnection c) => _byPc[c.Pc] = c;
    internal void Detach(VoiceConnection c) => _byPc.Remove(c.Pc);

    void SetStatus(string s)
    {
        if (s == Status) return;
        Status = s;
        StatusChanged?.Invoke(s);
    }

    void Tick()
    {
        // a P2P call ends when the other side has left (hang up on last leave)
        if (_current is { P2P: true, ShuttingDown: false } call && DateTime.UtcNow - call.AloneSince > TimeSpan.FromSeconds(2))
            EndCall("they hung up");
        if (_incoming is { } inc && DateTime.UtcNow - inc.At > TimeSpan.FromSeconds(60)) SetIncoming(null);
        if (_hidden && DateTime.UtcNow >= _unhideAt) { _hidden = false; _unhideAt = DateTime.MaxValue; _poseDirty = true; }
        if (!_movedLocal && ListMute != null && _session.Online) { _movedLocal = true; MoveLocalMutes(); }
        ChooseChannel();
        StepSessions();
        if (!_hidden) SendPose();
        UpdateStatus();
    }

    // ---- channels ----

    sealed class Session
    {
        public readonly string Channel;
        public readonly bool Estate;
        public readonly UUID Region;
        public readonly int? Parcel;
        public readonly string? Credentials;
        public readonly List<VoiceConnection> Connections = new();
        public readonly Dictionary<UUID, VoicePeer> Peers = new();
        public bool ShuttingDown;
        /// <summary>A call (P2P, group or ad-hoc): its IM session, who with, and whether anyone else has been in it.</summary>
        public UUID CallSession;
        public string? CallWith;
        public bool P2P, SomeoneJoined;
        public DateTime AloneSince = DateTime.MaxValue;
        public Session(string channel, bool estate, UUID region, int? parcel = null, string? credentials = null)
        {
            Channel = channel; Estate = estate; Region = region; Parcel = parcel; Credentials = credentials;
        }
        public void ShutDown()
        {
            ShuttingDown = true;
            foreach (var c in Connections) c.ShutDown();
        }
    }

    /// <summary>What the viewer's voiceConnectionCoro decides: none if the region or parcel has voice off; a parcel's own
    /// channel; or the estate's. A new channel takes over once its connection to this region is up.</summary>
    void ChooseChannel()
    {
        var c = _session.Client;
        if (!_enabled || c == null || !_session.Online || c.Network.CurrentSim is not { } sim)
        {
            LeaveAll(_enabled ? "not in world" : "off");
            return;
        }
        if (CurrentRegionId != sim.RegionID) _regionSince = DateTime.UtcNow;
        CurrentRegionId = sim.RegionID;
        // a call (or one being set up) takes the place of spatial voice until it ends, as in the viewer: nearby
        // voice stays until the call's channel is up (StepSessions swaps them)
        if (_calling || _current?.CallWith != null || _next?.CallWith != null) { OffHere = false; return; }
        if (!RegionHasVoice(sim))
        {
            OffHere = true;
            LeaveAll("voice is off in this region");
            return;
        }
        var parcel = AgentParcel(c, sim);
        // a parcel's own channel keeps its talk to the parcel: give its properties a moment to arrive before
        // settling on the estate's (the viewer goes straight to the estate's while it doesn't know the parcel)
        if (parcel == null && _current == null && _next == null && DateTime.UtcNow - _regionSince < TimeSpan.FromSeconds(3))
        {
            SetStatus("waiting for the parcel");
            return;
        }
        OffHere = false;
        string channel = VoiceProtocol.EstateChannel;
        int? parcelId = null;
        if (parcel != null)
        {
            if ((parcel.Flags & ParcelFlags.AllowVoiceChat) == 0)
            {
                OffHere = true;
                LeaveAll("voice is off on this parcel");
                return;
            }
            if ((parcel.Flags & ParcelFlags.UseEstateVoiceChan) == 0)
            {
                channel = $"{sim.RegionID}-{parcel.LocalID}";
                parcelId = parcel.LocalID;
            }
        }
        if (_current?.Channel == channel && !_current.ShuttingDown) { _next = Retire(_next); return; }
        if (_next?.Channel == channel) return;
        _next = Retire(_next);
        _next = new Session(channel, parcelId == null, sim.RegionID, parcelId);
        _next.Connections.Add(new VoiceConnection(this, channel, sim.RegionID, parcelId));
        _log($"voice: joining {(parcelId == null ? "the estate channel" : $"parcel {parcelId}'s channel")}");
    }

    Session? Retire(Session? s)
    {
        if (s == null) return null;
        s.ShutDown();
        _closing.Add(s);
        return null;
    }

    void LeaveAll(string why)
    {
        _current = Retire(_current);
        _next = Retire(_next);
        SetStatus(why);
    }

    bool RegionHasVoice(Simulator sim)
    {
        if ((sim.Flags & RegionFlags.AllowVoice) == 0) return false;
        // the viewer also wants SimulatorFeatures' VoiceServerType to say webrtc
        var f = sim.Features;
        if (f == null || !f.Has("VoiceServerType")) return f == null || !FeaturesLoaded(f);
        return f.Get("VoiceServerType").AsString() == VoiceProtocol.ServerType;
    }

    static bool FeaturesLoaded(SimulatorFeatures f) => f.Has("MeshRezEnabled") || f.Has("PhysicsMaterialsEnabled") || f.Has("MaxMaterialsPerTransaction");

    Parcel? AgentParcel(GridClient c, Simulator sim)
    {
        var pos = c.Self.SimPosition;
        int local = 0;
        try { local = c.Parcels.GetParcelLocalID(sim, pos); } catch { }
        if (local != 0 && sim.Parcels.TryGetValue(local, out var p)) return p;
        if (DateTime.UtcNow - _parcelAskedAt > TimeSpan.FromSeconds(3))
        {
            _parcelAskedAt = DateTime.UtcNow;
            c.Parcels.RequestParcelProperties(sim, pos.Y + 1, pos.X + 1, pos.Y - 1, pos.X - 1, 0, false);
        }
        // the parcel isn't known yet: the region's estate channel until it is (the viewer waits on the parcel)
        return null;
    }

    void StepSessions()
    {
        if (_current is { Estate: true, ShuttingDown: false } cur) UpdateNeighbours(cur);
        foreach (var s in AllSessions().ToList())
        {
            s.Connections.RemoveAll(conn => !conn.Step());
            if (s.ShuttingDown && s.Connections.Count == 0) _closing.Remove(s);
        }
        // the next channel takes over once its connection through this region is up
        if (_next != null && _next.Connections.Any(x => (x.RegionId == CurrentRegionId || !x.Spatial) && x.State == VoiceConnection.St.Up))
        {
            _current = Retire(_current);
            _current = _next;
            _next = null;
            PublishPeers();
        }
        if (_current is { ShuttingDown: false } now && now.Connections.Count == 0)
            now.Connections.Add(new VoiceConnection(this, now.Channel, CurrentRegionId, now.Parcel, now.Credentials));
    }

    /// <summary>Estate voice connects to the region you're in and to every region within 100 m of you (eight compass
    /// points), each with WebRTC voice; the mic goes out only through the one you're in.</summary>
    void UpdateNeighbours(Session s)
    {
        var c = _session.Client;
        if (c == null) return;
        var want = new HashSet<UUID> { CurrentRegionId };
        var me = c.Self.GlobalPosition;
        Simulator[] sims;
        lock (c.Network.Simulators) sims = c.Network.Simulators.ToArray();
        float reach = 2 * VoiceProtocol.MaxAudioDist;
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            double px = me.X + reach * Math.Cos(a), py = me.Y + reach * Math.Sin(a);
            foreach (var sim in sims)
            {
                var o = VoiceProtocol.RegionOrigin(sim.Handle);
                uint sx = sim.SizeX == 0 ? 256 : sim.SizeX, sy = sim.SizeY == 0 ? 256 : sim.SizeY;
                if (px >= o.X && px < o.X + sx && py >= o.Y && py < o.Y + sy && sim.RegionID != UUID.Zero && RegionHasVoice(sim))
                    want.Add(sim.RegionID);
            }
        }
        var key = string.Join(",", want.OrderBy(x => x.ToString()));
        if (key != _neighboursLogged)
        {
            _neighboursLogged = key;
            _log("voice: regions in reach: " + string.Join(", ", want.Select(RegionName)) + "; known: " +
                 string.Join(", ", sims.Select(x => $"{x.Name} {x.Handle >> 40},{(x.Handle & 0xffffffff) >> 8} {x.IPEndPoint} ({(x.RegionID == UUID.Zero ? "no id" : RegionHasVoice(x) ? "voice" : $"no voice, flags {x.Flags}")}{(x.Connected ? "" : ", not connected")})")));
        }
        foreach (var conn in s.Connections)
            if (!want.Contains(conn.RegionId)) conn.ShutDown();
        foreach (var id in want)
            if (!s.Connections.Any(x => x.RegionId == id && !x.ShuttingDown))
                s.Connections.Add(new VoiceConnection(this, s.Channel, id));
        s.Connections.RemoveAll(x => x.ShuttingDown && x.State == VoiceConnection.St.Closed);
    }

    // ---- the mic ----

    void ApplyMic()
    {
        bool want = Available && _enabled && !_hidden && !ModeratorMuted && (_mode == VoiceMode.PushToTalk ? _pttHeld : !_muted);
        if (want != _micOpen)
        {
            _micOpen = want;
            if (PoseLog) _log(want ? "voice: mic live" : "voice: mic closed");
            MicChanged?.Invoke();
            if (want)
            {
                _tracksOn = true;
                _tracksOffAt = DateTime.MaxValue;
                VoiceNative.sv_set_mute(0, 20);
            }
            else
            {
                // fade, then stop sending (the viewer disables the track after its fade)
                int fade = _hidden ? 0 : _mode == VoiceMode.PushToTalk ? 120 : 300;
                VoiceNative.sv_set_mute(1, fade);
                _tracksOffAt = DateTime.UtcNow.AddMilliseconds(fade + 100);
            }
        }
        if (_tracksOn && DateTime.UtcNow >= _tracksOffAt) { _tracksOn = false; _tracksOffAt = DateTime.MaxValue; }
        foreach (var s in AllSessions())
            foreach (var conn in s.Connections)
                conn.SetSending(_tracksOn && s == _current && conn.Primary && conn.State == VoiceConnection.St.Up);
    }

    // ---- poses ----

    void SendPose()
    {
        var c = _session.Client;
        if (c == null || c.Network.CurrentSim == null) return;
        var speaker = c.Self.GlobalPosition + new Vector3d(0, 0, 1);   // the viewer raises it to head height
        var speakerRot = c.Self.SimRotation;
        Vector3d listener;
        Quaternion listenerRot;
        lock (_poseLock)
        {
            bool fresh = DateTime.UtcNow - _listenerAt < TimeSpan.FromSeconds(1);
            listener = fresh ? _listenerPos : speaker;
            listenerRot = fresh ? _listenerRot : speakerRot;
        }
        // the viewer's ear locations (LLWebRTCVoiceClient::updatePosition): the camera's (here the head's) place and
        // direction, the avatar's, or the avatar's place with the camera's direction
        switch (Ear)
        {
            case VoiceEar.Avatar: listener = speaker; listenerRot = speakerRot; break;
            case VoiceEar.Mixed: listener = speaker; break;
        }
        // the listener stays within 50 m of the avatar (the server tethers too)
        var off = listener - speaker;
        double d = off.Length();
        if (d > VoiceProtocol.MaxAudioDist) listener = speaker + off * (VoiceProtocol.MaxAudioDist / d);

        static bool Moved(Vector3d a, Vector3d b) => Vector3d.DistanceSquared(a, b) > 0.01;
        // a rotation over 4 degrees (MINUSCULE_ANGLE_COS)
        static bool Turned(Quaternion a, Quaternion b) => Math.Abs(Quaternion.Dot(a, b)) < Math.Cos(0.5 * 4 * Math.PI / 180);
        bool dirty = _poseDirty || Moved(speaker, _sentSpeaker) || Moved(listener, _sentListener) ||
                     Turned(speakerRot, _sentSpeakerRot) || Turned(listenerRot, _sentListenerRot);
        if (!dirty) return;
        var json = VoiceProtocol.Pose(speaker, speakerRot, listener, listenerRot);
        int sent = 0;
        foreach (var s in AllSessions())
            foreach (var conn in s.Connections)
                if (conn.Spatial && conn.State == VoiceConnection.St.Up) { conn.Send(json); sent++; }
        if (sent > 0 && PoseLog && DateTime.UtcNow - _poseLoggedAt > TimeSpan.FromSeconds(1))
        {
            _poseLoggedAt = DateTime.UtcNow;
            _log("voice pose: " + json);
        }
        if (sent > 0)
        {
            _sentSpeaker = speaker; _sentListener = listener; _sentSpeakerRot = speakerRot; _sentListenerRot = listenerRot;
            _poseDirty = false;
        }
    }

    // ---- what the connections report ----

    internal void OnEstablished(VoiceConnection c) => _log($"voice: {c} connected");

    internal void OnUp(VoiceConnection c)
    {
        _poseDirty = true;
        SendPose();
    }

    internal void OnClosed(VoiceConnection c)
    {
        foreach (var s in AllSessions())
            if (s.Connections.Contains(c))
            {
                // the people that connection reported go with it
                foreach (var id in s.Peers.Where(p => p.Value.Region == c.RegionId && p.Key != Self).Select(p => p.Key).ToList())
                    s.Peers.Remove(id);
                if (s == _current) PublishPeers();
            }
    }

    UUID Self => _session.Client?.Self.AgentID ?? UUID.Zero;

    internal void OnData(VoiceConnection conn, string json)
    {
        var s = AllSessions().FirstOrDefault(x => x.Connections.Contains(conn));
        if (s == null) return;
        if (PoseLog && DateTime.UtcNow - _dataLoggedAt > TimeSpan.FromSeconds(2))
        {
            _dataLoggedAt = DateTime.UtcNow;
            _log($"voice data from {conn}: {(json.Length > 300 ? json[..300] : json)}");
        }
        bool primaryRegion = conn.Primary || (conn.Spatial && conn.RegionId == CurrentRegionId);
        var newcomers = new List<UUID>();
        foreach (var u in VoiceProtocol.Parse(json))
        {
            if (u.Version != null && u.Id == Self) conn.ServerVersion = u.Version;
            s.Peers.TryGetValue(u.Id, out var p);
            // joins only count from a person's own region's server (neighbours report them too)
            if (p == null && u.Joined && (u.JoinPrimary || !conn.Spatial))
            {
                p = new VoicePeer(u.Id, conn.RegionId, 0, false, false);
                newcomers.Add(u.Id);
            }
            if (p == null) continue;
            if (u.Left)
            {
                if (u.Id != Self) s.Peers.Remove(u.Id);
                continue;
            }
            p = p with
            {
                Power = u.Power ?? p.Power,
                Speaking = u.Speaking ?? p.Speaking,
                ModeratorMuted = u.ModeratorMuted is { } mm && (!conn.Spatial || primaryRegion || u.JoinPrimary) ? mm : p.ModeratorMuted,
            };
            s.Peers[u.Id] = p;
            if (u.Id == Self && s == _current && p.ModeratorMuted != ModeratorMuted)
            {
                ModeratorMuted = p.ModeratorMuted;
                _log(ModeratorMuted ? "voice: a moderator muted you" : "voice: a moderator unmuted you");
            }
        }
        // people joining get my mutes (the block list's and any kept here) and volumes for them
        var mutes = newcomers.Where(PeerIsMuted).ToDictionary(id => id, _ => true);
        foreach (var id in mutes.Keys) _sentMutes[id] = true;
        var gains = newcomers.Where(_peerGains.ContainsKey).ToDictionary(id => id, id => _peerGains[id]);
        if (VoiceProtocol.MuteAndGain(mutes, gains) is { } msg) conn.Send(msg);
        if (s.CallWith != null)
        {
            int others = s.Peers.Keys.Count(id => id != Self);
            if (others > 0) { s.SomeoneJoined = true; s.AloneSince = DateTime.MaxValue; }
            else if (s.SomeoneJoined && s.AloneSince == DateTime.MaxValue) s.AloneSince = DateTime.UtcNow;
        }
        if (s == _current) PublishPeers();
    }

    void SendPeerChoices(UUID id)
    {
        bool muted = PeerIsMuted(id);
        _sentMutes[id] = muted;
        var mutes = new Dictionary<UUID, bool> { [id] = muted };
        var gains = _peerGains.TryGetValue(id, out var g) ? new Dictionary<UUID, float> { [id] = g } : new();
        if (VoiceProtocol.MuteAndGain(mutes, gains) is not { } msg) return;
        foreach (var s in AllSessions())
            foreach (var conn in s.Connections) conn.Send(msg);
    }

    void PublishPeers() => _peers = _current?.Peers.Values.ToList() ?? (IReadOnlyList<VoicePeer>)Array.Empty<VoicePeer>();

    void UpdateStatus()
    {
        if (_current == null && _next == null) return;   // LeaveAll set it
        var s = _current ?? _next!;
        var primary = s.CallWith != null ? s.Connections.FirstOrDefault() : s.Connections.FirstOrDefault(x => x.RegionId == CurrentRegionId);
        string where = s.CallWith != null ? $"call with {s.CallWith}" : s.Estate ? "nearby voice" : "parcel voice";
        if (_current == null || primary == null || primary.State != VoiceConnection.St.Up)
            SetStatus(primary?.Problem is { } p ? $"{where}: {p}" : $"{where}: connecting");
        else
        {
            int others = _current.Peers.Keys.Count(id => id != Self);
            SetStatus($"{where}: connected, {others} other{(others == 1 ? "" : "s")}");
        }
        if (_current == null) PublishPeers();
    }

    // ---- calls (P2P, group, ad-hoc): the viewer's WebRTC way, an ad-hoc channel from ChatSessionRequest ----

    volatile IncomingCall? _incoming;
    volatile bool _calling;
    public IncomingCall? Incoming => _incoming;
    /// <summary>Raised when a call starts ringing or stops (answered, declined, given up).</summary>
    public event Action<IncomingCall?>? IncomingChanged;
    public string? CallWith => _current?.CallWith ?? _next?.CallWith;

    void SetIncoming(IncomingCall? c)
    {
        _incoming = c;
        IncomingChanged?.Invoke(c);
    }

    /// <summary>The person's switches for calls (Voice/CallRejects.cs): a reason to turn this one away, or null to ring.
    /// Voice thread.</summary>
    public Func<IncomingCall, string?>? Screen { get; set; }
    /// <summary>A call turned away without ringing, and why (its decline already sent).</summary>
    public event Action<IncomingCall, string>? TurnedAway;

    /// <summary>A voice invitation: rung on the panel, or declined at once when the person's switches turn it away
    /// (the viewer declines those silently: processCallResponse(1)).</summary>
    void Ring(IncomingCall call)
    {
        if (_current?.CallSession == call.Session || _next?.CallSession == call.Session) return;   // already in it
        if (_incoming?.Session == call.Session) return;
        if (Screen?.Invoke(call) is { } why)
        {
            SendDecline(call);
            _log($"voice: turned away {call.Name}'s {(call.P2P ? "call" : call.Kind + " call")} ({why})");
            TurnedAway?.Invoke(call, why);
            return;
        }
        _log($"voice: {call.Name} is calling{(call.P2P ? "" : $" ({call.Kind} call{(call.SessionName is { } n ? $": {n}" : "")})")}");
        SetIncoming(call);
    }

    void SendDecline(IncomingCall inc)
    {
        if (Cap(CurrentRegionId, "ChatSessionRequest") is { } cap)
            Http(cap, new OSDMap { ["method"] = inc.P2P ? "decline p2p voice" : "decline invitation", ["session-id"] = inc.Session }, (_, _) => { });
    }

    // an ad-hoc conference being started: its temporary session id -> who it's with
    readonly Dictionary<UUID, (string Name, bool ByPerson)> _conferences = new();

    /// <summary>Starts an ad-hoc voice conference with several residents (the viewer's startAdhocCall): ChatSessionRequest's
    /// "start conference" with a temporary session id; once the region answers with the real one, its channel ("call"),
    /// which rings the others. The mic starts muted in open mic, as the viewer's ad-hoc calls do.</summary>
    public void Conference(IReadOnlyList<UUID> ids, string name, bool byPerson) => Post(() =>
    {
        var cap = Cap(CurrentRegionId, "ChatSessionRequest");
        if (_session.Client == null || cap == null) { _log("voice: can't start a conference: no ChatSessionRequest here"); return; }
        var temp = UUID.Random();
        _conferences[temp] = (name, byPerson);
        _calling = true;
        _log($"voice: starting a conference with {name}");
        Http(cap, new OSDMap
        {
            ["method"] = "start conference", ["session-id"] = temp,
            ["params"] = new OSDArray(ids.Select(id => (OSD)OSD.FromUUID(id)).ToList()),
            ["alt_params"] = new OSDMap { ["voice_server_type"] = VoiceProtocol.ServerType },
        }, (status, reply) =>
        {
            if ((int)status is >= 200 and < 300)
            {
                // the session's start reply should follow on the event queue; give up on it after a while
                _ = Task.Delay(20000).ContinueWith(_ => Post(() =>
                {
                    if (_conferences.Remove(temp)) { _calling = false; _log($"voice: the conference with {name} never started"); }
                }));
                return;
            }
            _conferences.Remove(temp);
            _calling = false;
            _log($"voice: the conference with {name} didn't start ({(int)status})");
        });
    });

    /// <summary>The call up now (or being joined): its IM session, and whether it's one to one.</summary>
    public (UUID Session, bool P2P, string With)? CallInfo =>
        (_current?.CallWith != null ? _current : _next?.CallWith != null ? _next : null) is { } s ? (s.CallSession, s.P2P, s.CallWith!) : null;

    /// <summary>In nearby (spatial) voice now, and on which parcel's own channel if it's one (null: the estate's).</summary>
    public (bool Spatial, int? Parcel) Where => _current is { CallWith: null } s ? (true, s.Parcel) : (false, null);

    /// <summary>The IM session of a P2P chat: the two agents' ids XORed (LLIMMgr::computeSessionID).</summary>
    public static UUID P2PSession(UUID a, UUID b)
    {
        var x = a.GetBytes();
        var y = b.GetBytes();
        for (int i = 0; i < 16; i++) x[i] ^= y[i];
        return new UUID(x, 0);
    }

    /// <summary>Calls someone. Their client rings; you're in the call (and out of nearby voice) once the channel is
    /// up. byPerson: the person's own doing (open mic then starts unmuted, as the viewer's P2P calls do); the
    /// assistant's call leaves the mic as it is.</summary>
    public void Call(UUID other, string name, bool byPerson) => Post(() =>
    {
        var c = _session.Client;
        var cap = Cap(CurrentRegionId, "ChatSessionRequest");
        if (c == null || cap == null) { _log("voice: can't call: no ChatSessionRequest here"); return; }
        var session = P2PSession(c.Self.AgentID, other);
        _calling = true;
        _log($"voice: calling {name}");
        Http(cap, new OSDMap
        {
            ["method"] = "start p2p voice", ["session-id"] = session, ["params"] = other,
            ["alt_params"] = new OSDMap { ["voice_server_type"] = VoiceProtocol.ServerType },
        }, (status, _) =>
        {
            if ((int)status is < 200 or >= 300) { _calling = false; _log($"voice: the call to {name} didn't start ({(int)status})"); return; }
            JoinCall(session, name, p2p: true, byPerson);
        });
    });

    /// <summary>Starts (or joins) a group's voice call: the group's chat session, then its channel. The mic starts
    /// muted in open mic, as the viewer's group calls do.</summary>
    public void CallGroup(UUID group, string name) => Post(() =>
    {
        if (_session.Client is not { } c) return;
        _calling = true;
        c.Self.RequestJoinGroupChat(group);
        _log($"voice: calling the group {name}");
        _ = Task.Delay(1500).ContinueWith(_ => Post(() => JoinCall(group, name, p2p: false, byPerson: false)));
    });

    /// <summary>Answers the call that's ringing: the person's own press only (the panel's key), never the assistant.</summary>
    public void Accept() => Post(() =>
    {
        if (_incoming is not { } inc) return;
        SetIncoming(null);
        var cap = Cap(CurrentRegionId, "ChatSessionRequest");
        if (cap == null) return;
        _calling = true;
        // the viewer accepts the invitation for every voice invite, then asks for the channel ("call")
        Http(cap, new OSDMap { ["method"] = "accept invitation", ["session-id"] = inc.Session }, (_, _) =>
            JoinCall(inc.Session, inc.Name, inc.P2P, byPerson: true));
    });

    /// <summary>Declines the call that's ringing (the person or the assistant).</summary>
    public void Decline() => Post(() =>
    {
        if (_incoming is not { } inc) return;
        SetIncoming(null);
        SendDecline(inc);
        _log($"voice: declined {inc.Name}'s call");
    });

    /// <summary>Leaves the call (anyone may): back to nearby voice.</summary>
    public void Hangup() => Post(() => EndCall("hung up"));

    void EndCall(string why)
    {
        _calling = false;
        foreach (var s in new[] { _current, _next })
            if (s?.CallWith is { } with)
            {
                _log($"voice: call with {with} ended ({why})");
                s.ShutDown();
                _closing.Add(s);
                if (s == _current) _current = null;
                if (s == _next) _next = null;
            }
        PublishPeers();
    }

    void JoinCall(UUID session, string name, bool p2p, bool byPerson)
    {
        var cap = Cap(CurrentRegionId, "ChatSessionRequest");
        if (cap == null) { _calling = false; return; }
        Http(cap, new OSDMap
        {
            ["method"] = "call", ["session-id"] = session,
            ["alt_params"] = new OSDMap { ["preferred_voice_server_type"] = VoiceProtocol.ServerType },
        }, (status, reply) =>
        {
            var creds = (reply as OSDMap)?["voice_credentials"] as OSDMap;
            string? uri = creds?["channel_uri"].AsString(), secret = creds?["channel_credentials"].AsString();
            if ((int)status is < 200 or >= 300 || string.IsNullOrEmpty(uri))
            {
                _calling = false;
                _log($"voice: no channel for the call with {name} ({((int)status == 403 ? "not allowed" : $"{(int)status}")})");
                return;
            }
            StartCallSession(uri, secret ?? "", session, name, p2p, byPerson);
        });
    }

    void StartCallSession(string uri, string secret, UUID session, string name, bool p2p, bool byPerson)
    {
        _next = Retire(_next);
        var s = new Session(uri, estate: false, CurrentRegionId, null, secret) { CallSession = session, CallWith = name, P2P = p2p };
        s.Connections.Add(new VoiceConnection(this, uri, CurrentRegionId, null, secret));
        _next = s;
        _calling = false;
        _log($"voice: joining the call with {name}");
        // the viewer's P2P calls start with the mic on, group calls with it off; only the person's own call or
        // answer opens it (the assistant never does)
        if (p2p && byPerson && _mode == VoiceMode.OpenMic) SetMuted(false);
        if (!p2p && _mode == VoiceMode.OpenMic) SetMuted(true);
    }

    /// <summary>Tests on a local grid: join a non-spatial channel directly, as a call would once the grid answers "call"
    /// with credentials (OpenSim 0.9.3.1 doesn't: it answers 400, so real calls are tried on the main grid).</summary>
    public void TestRing(UUID from, string name) =>
        Post(() => SetIncoming(new IncomingCall(P2PSession(Self, from), from, name, "p2p", DateTime.UtcNow)));

    /// <summary>Tests on a local grid: an invitation as the region would bring it (p2p, group or conference), through
    /// the person's switches (Screen) before it rings.</summary>
    public void TestInvite(UUID from, string name, string kind, UUID session) =>
        Post(() => Ring(new IncomingCall(kind == "p2p" ? P2PSession(Self, from) : session, from, name, kind, DateTime.UtcNow)));

    public void TestCall(string channel, string name) => Post(() => StartCallSession(channel, "test", UUID.Random(), name, p2p: true, byPerson: false));

    // ---- caps and HTTP for the connections ----

    internal Uri? Cap(UUID region, string name)
    {
        var c = _session.Client;
        if (c == null) return null;
        Simulator? sim;
        lock (c.Network.Simulators) sim = c.Network.Simulators.FirstOrDefault(x => x.RegionID == region);
        return sim?.Caps?.CapabilityURI(name);
    }

    internal string CapDiag(UUID region)
    {
        var c = _session.Client;
        if (c == null) return "no client";
        Simulator[] sims;
        lock (c.Network.Simulators) sims = c.Network.Simulators.Where(x => x.RegionID == region).ToArray();
        if (sims.Length == 0) return "no simulator with that id";
        return string.Join("; ", sims.Select(x => $"{x.Name} connected={x.Connected} caps={(x.Caps == null ? "none" : $"seed {x.Caps.SeedCapsURI}, {x.Caps.Capabilities()?.Count() ?? -1} caps")}"));
    }

    internal string RegionName(UUID region)
    {
        var c = _session.Client;
        if (c == null) return region.ToString();
        lock (c.Network.Simulators) return c.Network.Simulators.FirstOrDefault(x => x.RegionID == region)?.Name ?? region.ToString()[..8];
    }

    /// <summary>A caps POST off the voice thread; its result comes back onto it.</summary>
    internal void Http(Uri cap, OSD body, Action<HttpStatusCode, OSD?> done)
    {
        var c = _session.Client;
        if (c == null) { Post(() => done(0, null)); return; }
        _ = Task.Run(async () =>
        {
            HttpStatusCode status = 0;
            OSD? reply = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                (status, reply) = await VoiceProtocol.PostAsync(c.HttpCapsClient, cap, body, cts.Token);
            }
            catch (Exception e) { _log($"voice: {cap.AbsolutePath}: {e.Message}"); }
            Post(() => done(status, reply));
        });
    }

    public string StatsJson() => Available ? VoiceNative.Stats() : "{}";

    public void Dispose()
    {
        if (!Available) return;
        _stop = true;
        Poke();
        if (_thread.IsAlive) _thread.Join(3000);
        _log("voice stats: " + VoiceNative.Stats());
        VoiceNative.sv_audio_stop();
        VoiceNative.sv_shutdown();
    }
}
