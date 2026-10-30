using System.Collections.Concurrent;
using LibreMetaverse;
using Sojourn.Core.Agents;
using Sojourn.Core.World;
using NVector3 = System.Numerics.Vector3;

namespace Sojourn.Session.Audio;

/// <summary>
/// The world's sounds as the viewer plays them (llaudioengine.cpp, llaudiosourcevo.cpp, LLViewerObject::
/// setAttachedSound, process_sound_trigger and its neighbours in llviewermessage.cpp), on the native mixer's 32
/// channels.
///
/// Sources: an object's attached sound (llPlaySound, llLoopSound and their master/slave forms, queued with
/// llSetSoundQueueing, the gain changed by llAdjustSoundVolume, cut off past llSetSoundRadius, stopped by
/// llStopSound) follows the object as it moves; a triggered sound (llTriggerSound, gestures' sounds, collision
/// sounds) stays where it was triggered; UI sounds play centred. Each source's priority is its gain over its distance
/// squared (UI 1); at most 30 play at once (LL_MAX_AUDIO_CHANNELS), a new one taking the quietest's channel only if
/// it's louder. A sound waits for its asset (a one-shot gives up after 15 s, as the viewer's do), looped ones never
/// die on their own, a one-shot plays once. Not heard: sounds from people and objects on the block list, from beyond
/// a parcel's "sounds stay inside" line either way, from your own camera's region while teleporting.
///
/// Runs on its own thread at 50 Hz; LibreMetaverse's events only enqueue. Positions are global metres.
/// </summary>
public sealed class WorldSounds : IDisposable
{
    /// <summary>App: an app's own cues (Sojourn Access's walk cues, earcons and beacon), at <see cref="AppVolume"/> rather
    /// than the world's sounds volume, so turning a noisy place down leaves them alone.</summary>
    public enum Kind { Sfx, Ui, Ambient, App }

    sealed class Source
    {
        public UUID Id, Owner, Object, Parent;
        public bool Attached;                  // follows Object
        public ulong Region;                   // where Object was last seen / where the trigger was
        public Vector3d Fixed;                 // a trigger's place
        public bool Forced;                    // centred (UI), no distance
        public Kind Type;
        public float Gain = 1;
        public bool Loop, SyncMaster, SyncSlave, Queue;
        public UUID Current, Queued;
        public readonly HashSet<UUID> Preloads = new();
        public bool PlayedOnce;
        public DateTime Age = DateTime.UtcNow;
        public int Channel = -1, Generation, QueuedSent;
        public bool Muted, Corrupt;
        public float Cutoff;
        public float Priority;
        public Vector3d Pos, CutoffPos;
        public bool PosKnown, Hud;
        public DateTime MissingSince = DateTime.MaxValue;
    }

    readonly AgentSession _s;
    readonly SoundBank _bank;
    readonly Action<string> _log;
    readonly Thread _thread;
    readonly ConcurrentQueue<Action> _events = new();
    readonly Dictionary<UUID, Source> _sources = new();          // attached: by object id; others: their own id
    readonly ConcurrentDictionary<UUID, byte> _attachedIds = new();   // for the network threads' filter
    readonly Dictionary<UUID, (AttachedSoundEventArgs E, DateTime Until)> _postponed = new();
    readonly Source?[] _channels = new Source?[AudioNative.Channels];
    readonly int[] _states = new int[AudioNative.Channels * 4];
    volatile bool _stop;
    int _tick;

    // the listener, from the render loop
    readonly object _listenerLock = new();
    Vector3d _listener;
    bool _haveListener;

    // what the person set: a share of the viewer's levels (SFX, UI and ambient 0.5 at 100%)
    volatile float _soundsVolume = 1;
    float _rolloffSent = -1;
    DateTime _unmuteAt = DateTime.MaxValue;
    bool _teleporting;
    float _rolloff = 1;

    /// <summary>The viewer's cap on sounds playing at once (LL_MAX_AUDIO_CHANNELS).</summary>
    public const int MaxPlaying = 30;
    const float ViewerLevel = 0.5f;   // AudioLevelSFX, AudioLevelUI, AudioLevelAmbient

    public Func<Vector3d, bool>? CanHear { get; set; }   // the parcel rules (AudioWiring's parcel tracker)
    public Action<double>? Tick { get; set; }           // footsteps and the like, on this thread each tick

    /// <summary>How a sound reached us: a message the antispam counts (a trigger or an attached sound), a preload (counted
    /// apart), or an object's update restating the sound it has (not counted).</summary>
    public enum Arrival { Message, Preload, Update }

    /// <summary>The person's safety tools (Control/SafetyWiring.cs, Core's Safety): true refuses a sound before it plays
    /// (blacklisted, or its source flooding). Sound, object, owner, how it came. On this thread.</summary>
    public Func<UUID, UUID, UUID, Arrival, bool>? Refuse { get; set; }

    public WorldSounds(AgentSession s, SoundBank bank, Action<string> log)
    {
        _s = s;
        _bank = bank;
        _log = log;
        AudioNative.sv_world_set(1, 1);
        _thread = new Thread(Run) { Name = "audio", IsBackground = true };
        _thread.Start();
    }

    public float SoundsVolume { get => _soundsVolume; set => _soundsVolume = Math.Max(0, value); }
    /// <summary>The level of <see cref="Kind.App"/> sounds (1: as made).</summary>
    public float AppVolume { get => _appVolume; set => _appVolume = Math.Max(0, value); }
    float _appVolume = 1;
    public int Playing => _channels.Count(c => c != null);

    /// <summary>Where every source is, heard or not (global metres; not UI or HUD sounds), refreshed at 10 Hz while
    /// <see cref="WantPlaces"/>: the sound beacons (Beacons/BeaconsWiring.cs), as the viewer walks its audio engine's sources.</summary>
    public volatile Vector3d[] Places = [];
    public volatile bool WantPlaces;

    /// <summary>The listener: the camera on the desktop, the head in the headset (region-local, SL's frame).</summary>
    public void SetListener(ulong region, NVector3 pos, NVector3 forward, NVector3 up)
    {
        if (region == 0 || forward.LengthSquared() < 1e-6f) return;
        var g = Global(region, new Vector3(pos.X, pos.Y, pos.Z));
        AudioNative.sv_listener(g.X, g.Y, g.Z, forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z);
        lock (_listenerLock)
        {
            _listener = g;
            _haveListener = true;
        }
    }

    Vector3d Listener() { lock (_listenerLock) return _listener; }
    public Vector3d Ear => Listener();

    /// <summary>The camera under water: the viewer's rolloff goes from 1 to 5.</summary>
    public void SetUnderwater(bool under) => _rolloff = under ? 5 : 1;

    public static Vector3d Global(ulong handle, Vector3 local)
    {
        Utils.LongToUInts(handle, out var x, out var y);
        return new Vector3d(x + local.X, y + local.Y, local.Z);
    }

    // ---- hooks on the network threads: enqueue only ----

    public void Hook(GridClient c)
    {
        c.Sound.SoundTrigger += (_, e) => _events.Enqueue(() => OnTrigger(e));
        c.Sound.AttachedSound += (_, e) => _events.Enqueue(() => OnAttached(e));
        c.Sound.AttachedSoundGainChange += (_, e) => _events.Enqueue(() =>
        {
            if (_sources.TryGetValue(e.ObjectID, out var src)) src.Gain = Math.Clamp(e.Gain, 0, 1);
        });
        c.Sound.PreloadSound += (_, e) => _events.Enqueue(() => OnPreload(e));
        c.Objects.ObjectUpdate += (_, e) =>
        {
            var p = e.Prim;
            if (p == null) return;
            if (p.Sound == UUID.Zero && !_attachedIds.ContainsKey(p.ID) && !(e.IsNew && _postponedIds.ContainsKey(p.ID))) return;
            ulong handle = e.Simulator.Handle;
            UUID sound = p.Sound, owner = p.OwnerID, id = p.ID;
            float gain = p.SoundGain, radius = p.SoundRadius;
            var flags = p.SoundFlags;
            _events.Enqueue(() => OnPrim(handle, id, sound, owner, gain, radius, flags));
        };
        c.Self.TeleportProgress += (_, e) => _events.Enqueue(() =>
        {
            switch (e.Status)
            {
                case TeleportStatus.Start:
                case TeleportStatus.Progress:
                    _teleporting = true;
                    _unmuteAt = DateTime.MaxValue;
                    break;
                case TeleportStatus.Finished:
                case TeleportStatus.Failed:
                case TeleportStatus.Cancelled:
                    _unmuteAt = DateTime.UtcNow.AddSeconds(1);
                    break;
            }
        });
    }

    readonly ConcurrentDictionary<UUID, byte> _postponedIds = new();

    /// <summary>What's playing or looping now (sound, object, owner, looping), from this thread. Any thread.</summary>
    public Task<List<(UUID Sound, UUID Object, UUID Owner, bool Loop)>> PlayingAsync()
    {
        var done = new TaskCompletionSource<List<(UUID, UUID, UUID, bool)>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _events.Enqueue(() => done.TrySetResult(_sources.Values.Where(s => s.Current != UUID.Zero && !s.Forced)
            .Select(s => (s.Current, s.Object, s.Owner, s.Loop)).ToList()));
        return done.Task;
    }

    /// <summary>Stop what's playing now that the person has just blocked (sound, object, owner). Any thread.</summary>
    public void Silence(Func<UUID, UUID, UUID, bool> which) => _events.Enqueue(() =>
    {
        foreach (var src in _sources.Values.ToList())
            if (src.Current != UUID.Zero && which(src.Current, src.Object, src.Owner)) Remove(src);
    });

    // ---- what the client itself plays ----

    /// <summary>A sound at a place (footsteps, typing: LLAudioEngine::triggerSound with a position), or centred
    /// (a UI sound, place null). Any thread.</summary>
    public void Trigger(UUID sound, UUID owner, float gain, Kind type, Vector3d? at)
    {
        if (sound == UUID.Zero) return;
        _events.Enqueue(() =>
        {
            if (_teleporting && at != null) return;
            var src = new Source
            {
                Id = UUID.Random(), Owner = owner, Gain = Math.Clamp(gain, 0, 1), Type = type,
                Forced = at == null, Fixed = at ?? default, PosKnown = true,
            };
            src.Pos = src.Fixed;
            _sources[src.Id] = src;
            _bank.Want(sound, 1);
            Play(src, sound);
        });
    }

    // ---- the viewer's message handlers ----

    void OnTrigger(SoundTriggerEventArgs e)
    {
        if (e.SoundID == UUID.Zero || _teleporting) return;
        var pos = Global(e.RegionHandle, e.Position);
        if (CanHear != null && !CanHear(pos)) return;
        if (_s.Mutes.IsMuted(e.OwnerID) || _s.Mutes.IsMuted(e.ObjectID)) return;
        if (e.ParentID != UUID.Zero && _s.Mutes.IsMuted(e.ParentID)) return;
        if (Refuse?.Invoke(e.SoundID, e.ObjectID, e.OwnerID, Arrival.Message) == true) return;
        // gesture sounds (object = owner) and collision sounds both play: the viewer's EnableGestureSounds and
        // EnableCollisionSounds default on
        var src = new Source
        {
            Id = UUID.Random(), Owner = e.OwnerID, Object = e.ObjectID, Parent = e.ParentID, Gain = Math.Clamp(e.Gain, 0, 1),
            Type = Kind.Sfx, Fixed = pos, Pos = pos, PosKnown = true, Region = e.RegionHandle,
        };
        _sources[src.Id] = src;
        UpdatePriority(src);
        _bank.Want(e.SoundID, src.Priority);
        Play(src, e.SoundID);
    }

    void OnAttached(AttachedSoundEventArgs e)
    {
        if (e.SoundID != UUID.Zero && Refuse?.Invoke(e.SoundID, e.ObjectID, e.OwnerID, Arrival.Message) == true) return;
        if (FindPrim(e.Simulator.Handle, e.ObjectID).Prim is null)
        {
            // the object hasn't arrived yet: kept for 15 s (the viewer's PostponedSoundData), or forgotten on a stop
            if (e.SoundID != UUID.Zero) { _postponed[e.ObjectID] = (e, DateTime.UtcNow.AddSeconds(15)); _postponedIds[e.ObjectID] = 0; }
            else { _postponed.Remove(e.ObjectID); _postponedIds.TryRemove(e.ObjectID, out _); }
            return;
        }
        SetAttached(e.Simulator.Handle, e.ObjectID, e.SoundID, e.OwnerID, e.Gain, e.Flags, null);
    }

    void OnPreload(PreloadSoundEventArgs e)
    {
        if (_s.Mutes.IsMuted(e.ObjectID) || _s.Mutes.IsMuted(e.OwnerID)) return;
        if (Refuse?.Invoke(e.SoundID, e.ObjectID, e.OwnerID, Arrival.Preload) == true) return;
        if (FindPrim(e.Simulator.Handle, e.ObjectID).Prim is null) return;
        var src = AttachedSource(e.Simulator.Handle, e.ObjectID, e.OwnerID);
        src.Preloads.Add(e.SoundID);
        _bank.Want(e.SoundID);
    }

    void OnPrim(ulong handle, UUID id, UUID sound, UUID owner, float gain, float radius, SoundFlags flags)
    {
        if (_postponed.Remove(id, out var post))
        {
            _postponedIds.TryRemove(id, out _);
            if (DateTime.UtcNow < post.Until)
                SetAttached(handle, id, post.E.SoundID, post.E.OwnerID, post.E.Gain, post.E.Flags, null);
        }
        if (_sources.TryGetValue(id, out var have)) have.Cutoff = radius;
        // the update carries the object's sound state every time; SetAttached ignores a loop already playing
        SetAttached(handle, id, sound, owner, gain, flags, radius);
    }

    /// <summary>LLViewerObject::setAttachedSound.</summary>
    void SetAttached(ulong handle, UUID obj, UUID sound, UUID owner, float gain, SoundFlags flags, float? radius)
    {
        if (_s.Mutes.IsMuted(obj) || _s.Mutes.IsMuted(owner)) return;
        if (sound != UUID.Zero && Refuse?.Invoke(sound, obj, owner, Arrival.Update) == true) return;
        _sources.TryGetValue(obj, out var src);
        if (sound == UUID.Zero)
        {
            if (src == null) return;
            if (src.Loop && !PendingPreloads(src)) Remove(src);   // a loop goes away; a one-shot plays out
            else if ((flags & SoundFlags.Stop) != 0) Stop(src);
            return;
        }
        bool loop = (flags & SoundFlags.Loop) != 0;
        if (loop && src is { Loop: true } && src.Current == sound) return;   // already looping it
        if (src != null && Done(src)) { Remove(src); src = null; }
        if (src is { Muted: true } && src.Current == sound) return;
        src ??= AttachedSource(handle, obj, owner);
        if (radius is { } r) src.Cutoff = r;
        src.Gain = Math.Clamp(gain, 0, 1);
        src.Loop = loop;
        src.SyncMaster = (flags & SoundFlags.SyncMaster) != 0;
        src.SyncSlave = (flags & SoundFlags.SyncSlave) != 0;
        src.Queue = (flags & SoundFlags.Queue) != 0;
        if (!src.Queue) Stop(src);   // "stop any current sound first to avoid farts of doom"
        UpdatePosition(src);
        UpdateMute(src);   // the cutoff checked again: this update may carry a new radius
        UpdatePriority(src);
        _bank.Want(sound, src.Priority);
        Play(src, sound);
    }

    Source AttachedSource(ulong handle, UUID obj, UUID owner)
    {
        if (_sources.TryGetValue(obj, out var src)) return src;
        // "arbitrary low gain for a sound that's not playing", until a play sets it
        src = new Source { Id = obj, Object = obj, Owner = owner, Attached = true, Region = handle, Gain = 0.01f, Type = Kind.Sfx };
        _sources[obj] = src;
        _attachedIds[obj] = 0;
        UpdatePosition(src);
        return src;
    }

    bool PendingPreloads(Source s) => s.Preloads.Any(p => _bank.Want(p).State == SoundBank.State.Fetching);

    /// <summary>LLAudioSource::play and addAudioData(set_current).</summary>
    void Play(Source src, UUID sound)
    {
        src.Age = DateTime.UtcNow;
        src.PlayedOnce = false;
        if (src.Current == UUID.Zero)
        {
            src.Current = sound;
            if (src.Channel >= 0) StartOn(src, src.Channel);
        }
        else if (src.Queue)
        {
            if (src.Queued != UUID.Zero) return;                 // one queued at a time
            if (sound == src.Current && src.Loop) return;
            src.Queued = sound;
            src.QueuedSent = 0;
        }
        else if (src.Current != sound)
        {
            src.Current = sound;
            if (src.Channel >= 0) StartOn(src, src.Channel);
        }
        // a channel now if the sound is ready (the viewer's play sets one up at once), else once it is
        if (src.Channel < 0 && _haveListener && !src.Muted && src.PosKnown && (!_teleporting || src.Forced) && _bank.Ready(src.Current, src.Priority) > 0) TryAssign(src);
    }

    /// <summary>LLAudioSource::stop: the sound off, its channel freed.</summary>
    void Stop(Source src)
    {
        Release(src);
        src.Current = UUID.Zero;
    }

    void Release(Source src)
    {
        if (src.Channel < 0) return;
        AudioNative.sv_ch_stop(src.Channel);
        _channels[src.Channel] = null;
        src.Channel = -1;
    }

    void Remove(Source src)
    {
        Release(src);
        _sources.Remove(src.Id);
        if (src.Attached) _attachedIds.TryRemove(src.Id, out _);
    }

    /// <summary>LLAudioSource::isDone.</summary>
    bool Done(Source s)
    {
        if (s.Corrupt) return true;
        if (s.Loop) return false;
        if (PendingPreloads(s)) return false;
        if (s.Queued != UUID.Zero) return false;
        var age = DateTime.UtcNow - s.Age;
        if (s.Channel < 0) return age > TimeSpan.FromSeconds(s.Muted ? 11 : 15) || s.PlayedOnce;
        return age > TimeSpan.FromSeconds(60);   // "arbitrarily cut off non-looped sounds when they're old"
    }

    // ---- places and loudness ----

    (RegionWorld? World, Primitive? Prim) FindPrim(ulong handle, UUID id)
    {
        var w = _s.World.Region(_s.GridKey, handle);
        if (w?.PrimById(id) is { } p) return (w, p);
        foreach (var r in _s.VisibleRegions())
            if (r.PrimById(id) is { } q) return (r, q);
        return (null, null);
    }

    void UpdatePosition(Source s)
    {
        if (!s.Attached) return;
        var (w, p) = FindPrim(s.Region, s.Object);
        if (w == null || p == null)
        {
            if (s.MissingSince == DateTime.MaxValue) s.MissingSince = DateTime.UtcNow;
            return;
        }
        s.MissingSince = DateTime.MaxValue;
        s.Region = w.Handle;
        var at = Global(w.Handle, w.RegionPosition(p));
        s.Pos = at;
        s.CutoffPos = at;
        s.Hud = false;
        if (_s.Objects.IsAttachment(w, p, out var wearer, out var root))
        {
            // the cutoff is measured from the wearer (LLAudioSourceVO::getPosGlobal); HUDs play at the ear
            if (wearer != null) s.CutoffPos = Global(w.Handle, w.RegionPosition(wearer));
            int point = (int)root.PrimData.AttachmentPoint;
            s.Hud = point >= 31 && point <= 38;
        }
        s.PosKnown = true;
    }

    void UpdateMute(Source s)
    {
        bool mute = false;
        if (s.Attached)
        {
            var ear = Listener();
            if (s.Cutoff > 0.1f && (s.CutoffPos - ear).Length() >= s.Cutoff) mute = true;
            else if (!s.Hud && CanHear != null && !CanHear(s.Pos) && !Riding(s)) mute = true;
            else if (_s.Mutes.IsMuted(s.Object) || _s.Mutes.IsMuted(s.Owner)) mute = true;
        }
        if (mute == s.Muted) return;
        s.Muted = mute;
        if (mute) Release(s);   // it comes back (from the start) when it's heard again, as the viewer's do
    }

    /// <summary>The agent riding the object: its sounds carry with them over a parcel line.</summary>
    bool Riding(Source s)
    {
        var c = _s.Client;
        if (c == null) return false;
        var w = _s.World.Region(_s.GridKey, s.Region);
        if (w == null || w.PrimById(s.Object) is not { } p) return false;
        var me = w.AvatarById(c.Self.AgentID);
        if (me == null || me.ParentID == 0) return false;
        uint Root(Primitive x)
        {
            var cur = x;
            for (int i = 0; i < 64 && cur.ParentID != 0 && w.Prims.TryGetValue(cur.ParentID, out var parent); i++) cur = parent;
            return cur.LocalID;
        }
        return w.Prims.TryGetValue(me.ParentID, out var seat) && Root(seat) == Root(p);
    }

    void UpdatePriority(Source s)
    {
        if (s.Forced || s.Hud) { s.Priority = 1; return; }
        if (s.Muted) { s.Priority = 0; return; }
        var d = s.Pos - Listener();
        double d2 = Math.Max(1, d.X * d.X + d.Y * d.Y + d.Z * d.Z);
        s.Priority = (float)(s.Gain / d2);
    }

    float TypeGain(Kind k) => ViewerLevel * (k == Kind.App ? _appVolume : _soundsVolume);

    // ---- the thread ----

    void Run()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        while (!_stop)
        {
            Thread.Sleep(20);
            double now = clock.Elapsed.TotalSeconds, dt = now - last;
            last = now;
            try { Step(dt); }
            catch (Exception e) { _log($"audio: {e.Message}"); }
        }
    }

    void Step(double dt)
    {
        _tick++;
        while (_events.TryDequeue(out var a)) a();
        try { Tick?.Invoke(dt); } catch (Exception e) { _log($"audio: {e.Message}"); }

        // teleporting: the world's sounds stop (the viewer mutes behind its progress screen) until a second after
        // arriving; UI sounds (the teleport's own) still play
        if (_teleporting && DateTime.UtcNow >= _unmuteAt) { _teleporting = false; _unmuteAt = DateTime.MaxValue; }
        if (_teleporting)
            foreach (var c in _channels) if (c is { Forced: false }) Release(c);
        if (_rolloff != _rolloffSent || _tick % 50 == 0) { _rolloffSent = _rolloff; AudioNative.sv_world_set(1, _rolloff); }

        unsafe { fixed (int* p = _states) AudioNative.sv_ch_states(p, AudioNative.Channels); }
        for (int i = 0; i < AudioNative.Channels; i++)
        {
            var src = _channels[i];
            if (src == null) continue;
            int state = _states[4 * i], gen = _states[4 * i + 3];
            if (gen == src.Generation + 1 && src.Queued != UUID.Zero)
            {
                // the queued sound took over on the channel
                src.Current = src.Queued;
                src.Queued = UUID.Zero;
                src.Generation = gen;
                src.Age = DateTime.UtcNow;
            }
            if (state == 0)
            {
                _channels[i] = null;
                src.Channel = -1;
                if (!src.Loop) src.PlayedOnce = true;
            }
        }

        bool full = _tick % 5 == 0;   // everything at 10 Hz; what's playing at 50
        if (full && WantPlaces) Places = _sources.Values.Where(s => s.PosKnown && !s.Forced && !s.Hud).Select(s => s.Pos).ToArray();
        else if (full && Places.Length > 0) Places = [];
        var expired = new List<Source>();
        foreach (var src in _sources.Values)
        {
            if (!full && src.Channel < 0) continue;
            UpdatePosition(src);
            if (src.Attached && DateTime.UtcNow - src.MissingSince > TimeSpan.FromSeconds(1)) { expired.Add(src); continue; }
            UpdateMute(src);
            UpdatePriority(src);
            if (full && Done(src)) { expired.Add(src); continue; }
            if (src.Current != UUID.Zero && _bank.Want(src.Current, src.Priority) is { State: SoundBank.State.Failed } f &&
                f.FailedAt > src.Age) src.Corrupt = true;
            // a queued sound: straight on when nothing's playing, else after the current one
            if (src.Queued != UUID.Zero)
            {
                if (src.Channel < 0)
                {
                    src.Current = src.Queued;
                    src.Queued = UUID.Zero;
                    src.Age = DateTime.UtcNow;
                    src.PlayedOnce = false;
                }
                else if (src.QueuedSent != src.Generation && _bank.Ready(src.Queued, src.Priority) is var qb and > 0)
                {
                    AudioNative.sv_ch_queue(src.Channel, qb);
                    src.QueuedSent = src.Generation;
                }
            }
        }
        foreach (var s in expired) Remove(s);
        foreach (var (id, p) in _postponed.Where(kv => DateTime.UtcNow > kv.Value.Until).ToList())
        {
            _postponed.Remove(id);
            _postponedIds.TryRemove(id, out _);
        }
        if (full) Assign();

        for (int i = 0; i < AudioNative.Channels; i++)
        {
            var src = _channels[i];
            if (src == null) continue;
            var at = Place(src);
            AudioNative.sv_ch_set(i, src.Gain * TypeGain(src.Type), at.X, at.Y, at.Z, src.Loop ? 1 : 0);
        }
    }

    Vector3d Place(Source s) => s.Forced || s.Hud ? Listener() : s.Pos;

    /// <summary>Channels for the loudest waiting sources (LLAudioEngine::idle and getFreeChannel).</summary>
    void Assign()
    {
        if (!_haveListener) return;
        var waiting = _sources.Values
            .Where(s => s.Channel < 0 && !s.Muted && !s.PlayedOnce && s.Current != UUID.Zero && s.PosKnown && !s.Corrupt && (!_teleporting || s.Forced))
            .OrderByDescending(s => s.Priority).ToList();
        foreach (var src in waiting)
            if (_bank.Ready(src.Current, src.Priority) > 0 && !TryAssign(src)) break;   // the rest are quieter still
    }

    /// <summary>A free channel, or the quietest one's if this source is louder; false if every playing one is louder.</summary>
    bool TryAssign(Source src)
    {
        int free = -1, used = 0;
        for (int i = 0; i < AudioNative.Channels; i++)
        {
            if (_channels[i] == null) { if (free < 0) free = i; }
            else used++;
        }
        if (used >= MaxPlaying) free = -1;
        if (free < 0)
        {
            int min = -1;
            for (int i = 0; i < AudioNative.Channels; i++)
                if (_channels[i] is { } c && (min < 0 || c.Priority < _channels[min]!.Priority)) min = i;
            if (min < 0 || _channels[min]!.Priority >= src.Priority) return false;
            Release(_channels[min]!);
            free = min;
        }
        StartOn(src, free);
        return true;
    }

    void StartOn(Source src, int ch)
    {
        int buf = _bank.Ready(src.Current, src.Priority);
        if (buf <= 0)
        {
            if (src.Channel == ch) { AudioNative.sv_ch_stop(ch); _channels[ch] = null; src.Channel = -1; }
            return;
        }
        int master = -1;
        if (src.SyncSlave)
        {
            // "for now, just use a global sync master": the loudest master playing
            float best = -1;
            for (int i = 0; i < AudioNative.Channels; i++)
                if (_channels[i] is { SyncMaster: true } m && m != src && m.Priority > best) { best = m.Priority; master = i; }
        }
        var at = Place(src);
        int flags = (src.Loop ? AudioNative.FlagLoop : 0) | (src.Forced || src.Hud ? 0 : AudioNative.FlagSpatial);
        int gen = AudioNative.sv_ch_play(ch, buf, flags, src.Gain * TypeGain(src.Type), at.X, at.Y, at.Z, master);
        if (gen < 0) return;
        _channels[ch] = src;
        src.Channel = ch;
        src.Generation = gen;
        src.QueuedSent = 0;
    }

    /// <summary>What's playing now, loudest first, for the agent API.</summary>
    public List<(UUID Sound, UUID Object, UUID Owner, bool Loop, double Dist, Kind Type)> Snapshot()
    {
        var done = new TaskCompletionSource<List<(UUID, UUID, UUID, bool, double, Kind)>>();
        _events.Enqueue(() =>
        {
            var ear = Listener();
            done.TrySetResult(_channels.Where(c => c != null).Select(c => c!).OrderByDescending(c => c.Priority)
                .Select(c => (c.Current, c.Object, c.Owner, c.Loop, c.Forced || c.Hud ? 0 : Math.Round((c.Pos - ear).Length(), 1), c.Type)).ToList());
        });
        return done.Task.Wait(TimeSpan.FromSeconds(2)) ? done.Task.Result : new();
    }

    /// <summary>Every source tracked, playing or not (the agent API's sources op, for looking into what's heard).</summary>
    public System.Text.Json.Nodes.JsonArray Sources()
    {
        var done = new TaskCompletionSource<System.Text.Json.Nodes.JsonArray>();
        _events.Enqueue(() =>
        {
            var ear = Listener();
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var s in _sources.Values.OrderByDescending(x => x.Priority))
                arr.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["sound_id"] = s.Current.ToString(), ["object_id"] = s.Object.ToString(), ["attached"] = s.Attached,
                    ["channel"] = s.Channel, ["loop"] = s.Loop, ["gain"] = Math.Round(s.Gain, 3), ["muted"] = s.Muted,
                    ["cutoff"] = Math.Round(s.Cutoff, 2), ["dist"] = Math.Round((s.Pos - ear).Length(), 2),
                    ["priority"] = Math.Round(s.Priority, 5), ["queued"] = s.Queued == UUID.Zero ? null : s.Queued.ToString(),
                    ["played_once"] = s.PlayedOnce, ["hud"] = s.Hud,
                });
            done.TrySetResult(arr);
        });
        return done.Task.Wait(TimeSpan.FromSeconds(2)) ? done.Task.Result : new();
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(1000);
        for (int i = 0; i < AudioNative.Channels; i++) AudioNative.sv_ch_stop(i);
    }
}
