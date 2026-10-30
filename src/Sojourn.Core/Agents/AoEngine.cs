using LibreMetaverse;

namespace Sojourn.Core.Agents;

/// <summary>
/// A client-side animation overrider, as Firestorm's (aoengine.cpp, aoset.cpp, master of 2026-10): sets of states,
/// each state a list of steps (an animation, or a group of animations played together) and tracks (extra animations
/// cycling on their own), keyed to the default motions the region plays on my avatar (stand, walk, sit, fly...). When
/// the region starts one of those motions, the state's animation is started instead (by AgentAnimation, so everyone
/// sees it); when it stops, the state's animations stop. Pure logic: no GridClient here. What it sends goes out through
/// <see cref="Send"/>; what it needs to know about the world comes in through the delegates; time comes from
/// <see cref="Tick"/>. The session's half (reading the inventory folders, the region's animation list, the switches)
/// is <see cref="AoDriver"/>; the folder format is <see cref="AoFolders"/>. Ported by Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class AoEngine
{
    /// <summary>Start (true) or stop (false) an animation on my avatar, in order.</summary>
    public required Action<UUID, bool> Send { get; init; }
    /// <summary>Whether I sit on an object (not the ground).</summary>
    public Func<bool> SittingOnObject { get; set; } = () => false;
    /// <summary>Whether the object I sit on plays an animation of its own on me (an unworn prim of its linkset; smart sit).</summary>
    public Func<bool> SeatAnimates { get; set; } = () => false;
    public Random Rng { get; set; } = new();

    readonly List<AoSet> _sets = new();
    public IReadOnlyList<AoSet> Sets => _sets;
    public AoSet? Current { get; private set; }
    public AoSet? Default { get; private set; }

    bool _enabled;
    bool _stands = true;
    bool _mouselook;
    bool _underwater;
    UUID _lastMotion = AoMotions.Stand;
    UUID _lastOverridden = AoMotions.Stand;
    UUID _ignoreStopOnce;
    UUID _transition;

    public bool Enabled => _enabled;
    public bool Stands => _stands;
    public bool UnderWater => _underwater;
    public UUID LastMotion => _lastMotion;

    // timers, on Tick's clock (seconds)
    double _now;
    Timer? _cycle;                      // the current set's state cycle
    readonly List<(AoState State, int Track, Timer T)> _trackTimers = new();
    readonly List<(AoState State, int Track, Timer T)> _alwaysTimers = new();
    AoState? _activeTracks;
    AoState? _activeAlways;
    int _sitCancelTicks;
    double _sitCancelNext;

    sealed class Timer(double period, double now)
    {
        public double Period = period;
        public double Due = now + period;
    }

    public const int AlwaysTrackSteps = -1;

    // ---------- the sets ----------

    /// <summary>Replaces the sets (a reload). Stops whatever this engine had playing first. The default set (its :**
    /// flag) is chosen, else the one named <paramref name="remembered"/>, else the first.</summary>
    public void Load(IEnumerable<AoSet> sets, string? remembered)
    {
        bool was = _enabled;
        if (was) Enable(false);
        StopTracks();
        StopAlways();
        _sets.Clear();
        _sets.AddRange(sets);
        Default = _sets.FirstOrDefault(s => s.IsDefault);
        Current = Default ?? _sets.FirstOrDefault(s => s.Name == remembered) ?? _sets.FirstOrDefault();
        _lastOverridden = AoMotions.Stand;
        if (was) Enable(true);
    }

    public AoSet? SetByName(string name) =>
        _sets.FirstOrDefault(s => s.Name == name) ?? _sets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Select(AoSet set)
    {
        if (_enabled && Current != null)
        {
            StopTracks();
            StopAlways();
            if (StateForMotion(_lastOverridden) is { } st)
            {
                StopCurrent(st);
                _cycle = null;
            }
        }
        Current = set;
        if (_enabled)
        {
            StartAlways();
            var id = Override(_lastMotion, true);
            if (id != UUID.Zero) Send(id, true);
        }
    }

    public void SetDefault(AoSet? set)
    {
        Default = set;
        foreach (var s in _sets) s.IsDefault = s == set;
    }

    // ---------- the switches ----------

    public void EnableStands(bool on)
    {
        bool was = _stands;
        _stands = on;
        // turned off while a stand of ours plays: it stops and the region's stand comes back (Firestorm only declines
        // the next stand, leaving the playing one on until the motion changes)
        if (was && !on && _enabled && Current is { } set
            && (_lastMotion == AoMotions.Stand || _lastMotion == AoMotions.TurnLeft || _lastMotion == AoMotions.TurnRight))
        {
            bool any = false;
            foreach (var k in new[] { AoStateKind.Standing, AoStateKind.TurningLeft, AoStateKind.TurningRight })
                if (set.State(k).Playing.Count > 0) { StopCurrent(set.State(k)); any = true; }
            StopTracks();
            _cycle = null;
            if (any) Send(AoMotions.Stand, true);
            return;
        }
        Enable(_enabled);
    }

    public void Enable(bool on)
    {
        _enabled = on;
        if (Current is not { } set) return;
        var state = StateForMotion(_lastMotion);
        if (on)
        {
            StartAlways();
            if (state is not { Steps.Count: > 0 }) return;
            // don't stop the underlying sits when coming back on
            if (_lastOverridden != AoMotions.SitGround && _lastOverridden != AoMotions.SitGroundConstrained && _lastOverridden != AoMotions.Sit)
                Send(_lastOverridden, false);
            var anim = Override(_lastMotion, true);
            if (anim == UUID.Zero) return;
            if (_lastMotion == AoMotions.Stand)
            {
                if (!_stands) return;
                StopStandVariants();
            }
            else if (_lastMotion == AoMotions.Walk)
            {
                Send(AoMotions.WalkNew, false);
                Send(AoMotions.FemaleWalk, false);
                Send(AoMotions.FemaleWalkNew, false);
            }
            else if (_lastMotion == AoMotions.Run)
            {
                Send(AoMotions.RunNew, false);
                Send(AoMotions.FemaleRunNew, false);
            }
            else if (_lastMotion == AoMotions.Sit)
            {
                StopSitVariants();
                Send(AoMotions.SitGeneric, true);
            }
            Send(anim, true);
            // stopping the Linden motion just now mustn't stop the override when the region echoes it
            _ignoreStopOnce = _lastMotion;
        }
        else
        {
            if (_lastOverridden == AoMotions.Sit) Send(AoMotions.SitGeneric, false);
            StopTracks();
            StopAlways();
            foreach (var st in set.States)
                if (st.Playing.Count > 0) StopCurrent(st);
            if (_lastOverridden != AoMotions.Sit || !Foreign()) Send(_lastMotion, true);
            _cycle = null;
        }
    }

    /// <summary>In mouselook (the desktop's first person), a set with "no stands in mouselook" (:DM) stands still.</summary>
    public void InMouselook(bool on)
    {
        if (_mouselook == on) return;
        _mouselook = on;
        if (Current is not { MouselookStandDisable: true } set || !_enabled || _lastMotion != AoMotions.Stand) return;
        if (on)
        {
            var st = set.State(AoStateKind.Standing);
            if (st.Playing.Count > 0) StopCurrent(st);
            StopTracks();
            Send(AoMotions.Stand, true);
        }
        else
        {
            StopStandVariants();
            var id = Override(AoMotions.Stand, true);
            if (id != UUID.Zero) Send(id, true);
        }
    }

    public void SetSitOverride(AoSet set, bool on)
    {
        set.SitOverride = on;
        if (Current != set || _lastMotion != AoMotions.Sit || !_enabled) return;
        if (on)
        {
            StopSitVariants();
            Send(AoMotions.SitGeneric, true);
        }
        else
        {
            Send(AoMotions.SitGeneric, false);
            var st = set.State(AoStateKind.Sitting);
            if (st.Playing.Count > 0) StopCurrent(st);
            StopTracks();
            if (!Foreign()) Send(AoMotions.Sit, true);
        }
    }

    public void SetSmart(AoSet set, bool on)
    {
        set.Smart = on;
        if (_enabled && on && SittingOnObject()) StartSitCancel();
    }

    public void SetMouselookStandDisable(AoSet set, bool on)
    {
        set.MouselookStandDisable = on;
        if (Current != set || !_enabled) return;
        bool ml = _mouselook;
        _mouselook = !ml;
        InMouselook(ml);
    }

    public void SetCycle(AoState state, bool on)
    {
        bool was = state.Cycle;
        state.Cycle = on;
        if (was && !on)
        {
            state.Current = 0;
            SyncPlayingStep(state);
        }
        if (state == _activeAlways) RefreshAlwaysTimers();
    }

    public void SetRandom(AoState state, bool on) => state.Random = on;

    public void SetCycleTime(AoState state, float seconds)
    {
        state.CycleTime = seconds;
        if (state == _activeAlways) RefreshAlwaysTimers();
    }

    // ---------- the region's motions ----------

    /// <summary>Firestorm's override(): the region started (or stopped) a motion on me. Returns the animation the
    /// caller starts (or stops) in its place, or zero to let the motion be. Other animations of the state are sent here.</summary>
    public UUID Override(UUID motion, bool start)
    {
        if (!_enabled)
        {
            if (start && Current != null && StateForMotion(motion) is { } s)
            {
                SetLast(motion);
                if (s.Steps.Count > 0) SetLastOverridden(motion);
            }
            return UUID.Zero;
        }
        if (_sets.Count == 0 || Current is not { } set) return UUID.Zero;

        if (!start && motion == _ignoreStopOnce)
        {
            _ignoreStopOnce = UUID.Zero;
            if (motion == AoMotions.Sit) Send(AoMotions.SitGeneric, false);
            return UUID.Zero;
        }

        var state = StateForMotion(motion);
        if (state is null) return UUID.Zero;

        // stray animations of other states, cleaned up (typing plays beside the others)
        if (motion != AoMotions.Type)
            foreach (var k in CleanupStates)
            {
                var other = set.State(k);
                if (other != state && other.Playing.Count > 0) StopCurrent(other);
            }

        UUID animation;
        _cycle = null;
        if (start)
        {
            if (motion != AoMotions.Type) StopTracks();
            SetLast(motion);
            if (set.MouselookStandDisable && motion == AoMotions.Stand && _mouselook) return UUID.Zero;
            if (!_stands && (motion == AoMotions.Stand || motion == AoMotions.TurnRight || motion == AoMotions.TurnLeft)) return UUID.Zero;
            if (!set.SitOverride && motion == AoMotions.Sit) return UUID.Zero;
            // scripted seats that sit you with the ground sit: theirs
            if ((motion == AoMotions.SitGround || motion == AoMotions.SitGroundConstrained) && SittingOnObject()) return UUID.Zero;
            if (state.Steps.Count > 0) SetLastOverridden(motion);
            if (motion != AoMotions.Type) set.Motion = motion;

            var animations = AnimationsForState(state);
            if (state.Playing.Count > 0) StopCurrent(state);
            state.Playing.AddRange(animations);
            animation = animations.Count > 0 ? animations[0] : UUID.Zero;
            StartCycleTimer(state);

            if (motion == AoMotions.Sit)
            {
                // the generic sit under the override covers its loop points without looping back into SIT
                Send(AoMotions.SitGeneric, true);
                if (set.Smart) StartSitCancel();
            }
            else if (AoMotions.Transient(motion))
            {
                // the viewer needs the Linden motion to know the state: play ours beside it
                foreach (var id in animations) Send(id, true);
                return UUID.Zero;
            }
            for (int i = 1; i < animations.Count; i++) Send(animations[i], true);
            if (motion != AoMotions.Type) StartTracks(state);
        }
        else
        {
            if (motion == _transition)
            {
                _transition = UUID.Zero;
                return UUID.Zero;
            }
            _transition = UUID.Zero;
            animation = state.Playing.Count > 0 ? state.Playing[0] : UUID.Zero;
            if (motion == AoMotions.Type)
            {
                StopCurrent(state, skipFirst: true);
                if (StateForMotion(_lastMotion) is { } previous) StartCycleTimer(previous);
                return animation;
            }
            if (motion == AoMotions.Sit) StopSitVariants();
            if (motion != set.Motion)
            {
                StopCurrent(state, skipFirst: true);
                return animation;
            }
            set.Motion = UUID.Zero;
            StopTracks();
            if (AoMotions.Transient(motion))
            {
                StopCurrent(state);
                StartCycleTimer(state);
                return UUID.Zero;
            }
            StopCurrent(state, skipFirst: true);
        }
        return animation;
    }

    static readonly AoStateKind[] CleanupStates =
    {
        AoStateKind.Standing, AoStateKind.Walking, AoStateKind.Running, AoStateKind.Sitting, AoStateKind.SittingOnGround,
        AoStateKind.Crouching, AoStateKind.CrouchWalking, AoStateKind.Falling, AoStateKind.FlyingDown, AoStateKind.FlyingUp,
        AoStateKind.Flying, AoStateKind.FlyingSlow, AoStateKind.Hovering, AoStateKind.Jumping, AoStateKind.TurningRight,
        AoStateKind.TurningLeft, AoStateKind.Floating, AoStateKind.SwimmingForward, AoStateKind.SwimmingUp, AoStateKind.SwimmingDown,
    };

    void SetLast(UUID m) { if (m != AoMotions.Type) _lastMotion = m; }
    void SetLastOverridden(UUID m) { if (m != AoMotions.Type) _lastOverridden = m; }

    /// <summary>Under the water of the region (not a prim's): flying is swimming, where the set has swimming.</summary>
    public void BelowWater(bool under)
    {
        if (_underwater == under) return;
        var mapped = MapSwimming(_lastMotion);
        if (mapped is not { Steps.Count: > 0 })
        {
            _underwater = under;
            return;
        }
        var id = Override(_lastMotion, false);
        if (id == UUID.Zero) id = _lastMotion;
        Send(id, false);
        if (!_underwater) _transition = id;
        _underwater = under;
        id = Override(_lastMotion, true);
        if (id == UUID.Zero) id = _lastMotion;
        Send(id, true);
    }

    AoState? MapSwimming(UUID motion)
    {
        if (Current is not { } set) return null;
        AoStateKind? k = motion == AoMotions.Hover ? AoStateKind.Floating
            : motion == AoMotions.Fly ? AoStateKind.SwimmingForward
            : motion == AoMotions.HoverUp ? AoStateKind.SwimmingUp
            : motion == AoMotions.HoverDown ? AoStateKind.SwimmingDown : null;
        return k is { } kind ? set.State(kind) : null;
    }

    /// <summary>The state a motion maps to in the current set, flying mapped to swimming under water.</summary>
    public AoState? StateForMotion(UUID motion)
    {
        if (Current is not { } set) return null;
        var dflt = set.StateByMotion(motion);
        if (!_underwater) return dflt;
        var mapped = MapSwimming(motion);
        return mapped is { Steps.Count: > 0 } ? mapped : dflt;
    }

    /// <summary>The region changed: the motion's override asked again (sits only when the override is really playing).</summary>
    public UUID? OnRegionChange()
    {
        if (!_enabled || Current is not { } set) return null;
        ReassertAlways(null);
        if (_lastMotion == AoMotions.Sit)
        {
            if (!set.SitOverride || _lastOverridden != AoMotions.Sit) return null;
            if (set.State(AoStateKind.Sitting).Playing.Count == 0) return null;
        }
        return _lastMotion;
    }

    // ---------- cycling ----------

    public enum CycleMode { Any, Next, Previous }

    public void Cycle(CycleMode mode, bool resetTimer = false)
    {
        if (!_enabled || Current is not { } set) return;
        if (_lastMotion == AoMotions.Sit && !set.SitOverride) return;
        if (_lastMotion == AoMotions.Stand && set.MouselookStandDisable && _mouselook) return;
        var state = StateForMotion(_lastMotion);
        if (state is null || state.Steps.Count == 0) return;
        if (!state.Cycle && mode == CycleMode.Any) return;
        var old = state.Playing.ToList();
        List<UUID> animations;
        if (mode == CycleMode.Any) animations = AnimationsForState(state);
        else
        {
            if (mode == CycleMode.Previous) state.Current = state.Current == 0 ? state.Steps.Count - 1 : state.Current - 1;
            else state.Current = (state.Current + 1) % state.Steps.Count;
            animations = AnimationsForStep(state.Steps[state.Current]);
        }
        if (animations.SequenceEqual(old)) return;
        state.Playing.Clear();
        state.Playing.AddRange(animations);
        foreach (var id in animations) if (!old.Contains(id)) Send(id, true);
        foreach (var id in old) if (!animations.Contains(id)) Send(id, false);
        if (resetTimer && _cycle != null) _cycle.Due = _now + _cycle.Period;
    }

    /// <summary>A particular step of the state playing now (the AO window's double-click).</summary>
    public void Play(int step)
    {
        if (!_enabled || StateForMotion(_lastMotion) is not { } state || step < 0 || step >= state.Steps.Count) return;
        state.Current = step;
        SyncPlayingStep(state);
    }

    void SyncPlayingStep(AoState state)
    {
        if (!IsPlaying(state) || state.Steps.Count == 0) return;
        if (state.Current >= state.Steps.Count) state.Current = 0;
        var animations = AnimationsForStep(state.Steps[state.Current]);
        if (animations.Count == 0) return;
        if (Swap(state, animations))
        {
            if (state == _activeAlways) RefreshAlwaysTimers();
            else if (_cycle != null) _cycle.Due = _now + _cycle.Period;
        }
    }

    bool IsPlaying(AoState state)
    {
        if (!_enabled || Current == null || state.Playing.Count == 0) return false;
        return state.Kind == AoStateKind.Always ? state == _activeAlways : state == StateForMotion(Current.Motion);
    }

    bool Swap(AoState state, List<UUID> animations)
    {
        var old = state.Playing.ToList();
        if (animations.SequenceEqual(old)) return false;
        state.Playing.Clear();
        state.Playing.AddRange(animations);
        foreach (var id in animations) if (!old.Contains(id)) Send(id, true);
        foreach (var id in old) if (!animations.Contains(id)) Send(id, false);
        return true;
    }

    List<UUID> AnimationsForState(AoState state)
    {
        int n = state.Steps.Count;
        if (n == 0) return new List<UUID>();
        if (state.Cycle)
        {
            if (state.Random) state.Current = Rng.Next(n);
            else state.Current = (state.Current + 1) % n;
        }
        if (state.Current >= n) state.Current = 0;
        return AnimationsForStep(state.Steps[state.Current]);
    }

    static List<UUID> AnimationsForStep(AoStep step) =>
        step.Members.Select(m => m.AssetId).Where(id => id != UUID.Zero).ToList();

    UUID AnimationForTrack(AoTrack track)
    {
        int n = track.Animations.Count;
        if (n == 0) return UUID.Zero;
        if (track.Cycle)
        {
            if (track.Random) track.Current = Rng.Next(n);
            else track.Current = (track.Current + 1) % n;
        }
        if (track.Current >= n) track.Current = 0;
        return track.Animations[track.Current].AssetId;
    }

    void StartCycleTimer(AoState state)
    {
        _cycle = state.CycleTime > 0 ? new Timer(state.CycleTime, _now) : null;
    }

    // ---------- tracks and the Always state ----------

    void StartTracks(AoState state)
    {
        StopTracks();
        if (!_enabled) return;
        for (int i = 0; i < state.Tracks.Count; i++)
        {
            var t = state.Tracks[i];
            if (t.Animations.Count == 0) continue;
            t.CurrentId = AnimationForTrack(t);
            if (t.CurrentId == UUID.Zero) continue;
            Send(t.CurrentId, true);
            if (t.Cycle && t.CycleTime > 0 && t.Animations.Count > 1) _trackTimers.Add((state, i, new Timer(t.CycleTime, _now)));
        }
        _activeTracks = state;
    }

    void StopTracks()
    {
        _trackTimers.Clear();
        if (_activeTracks is not { } state) return;
        foreach (var t in state.Tracks)
            if (t.CurrentId != UUID.Zero)
            {
                Send(t.CurrentId, false);
                t.CurrentId = UUID.Zero;
            }
        _activeTracks = null;
    }

    void StartAlways()
    {
        var state = _enabled && Current != null ? Current.State(AoStateKind.Always) : null;
        if (state != null && state == _activeAlways) return;
        StopAlways();
        if (state is null || (state.Steps.Count == 0 && state.Tracks.Count == 0)) return;
        if (state.Steps.Count > 0)
        {
            var animations = AnimationsForState(state);
            state.Playing.Clear();
            state.Playing.AddRange(animations);
            foreach (var id in animations) Send(id, true);
        }
        foreach (var t in state.Tracks)
        {
            if (t.Animations.Count == 0) continue;
            t.CurrentId = AnimationForTrack(t);
            if (t.CurrentId != UUID.Zero) Send(t.CurrentId, true);
        }
        _activeAlways = state;
        RefreshAlwaysTimers();
    }

    void StopAlways()
    {
        _alwaysTimers.Clear();
        if (_activeAlways is not { } state) return;
        StopCurrent(state);
        foreach (var t in state.Tracks)
            if (t.CurrentId != UUID.Zero)
            {
                Send(t.CurrentId, false);
                t.CurrentId = UUID.Zero;
            }
        _activeAlways = null;
    }

    void RefreshAlwaysTimers()
    {
        _alwaysTimers.Clear();
        if (_activeAlways is not { } state) return;
        if (state.Cycle && state.CycleTime > 0 && state.Steps.Count > 1) _alwaysTimers.Add((state, AlwaysTrackSteps, new Timer(state.CycleTime, _now)));
        for (int i = 0; i < state.Tracks.Count; i++)
        {
            var t = state.Tracks[i];
            if (t.Cycle && t.CycleTime > 0 && t.Animations.Count > 1) _alwaysTimers.Add((state, i, new Timer(t.CycleTime, _now)));
        }
    }

    /// <summary>Always's animations started again where they're missing from what plays (null: all of them).</summary>
    public void ReassertAlways(Func<UUID, bool>? playing)
    {
        if (!_enabled || _activeAlways is not { } state) return;
        foreach (var id in state.Playing)
            if (id != UUID.Zero && (playing is null || !playing(id))) Send(id, true);
        foreach (var t in state.Tracks)
            if (t.CurrentId != UUID.Zero && (playing is null || !playing(t.CurrentId))) Send(t.CurrentId, true);
    }

    void TrackTimeout(AoState state, int track)
    {
        if (!_enabled || Current == null) return;
        if (track == AlwaysTrackSteps)
        {
            if (state == _activeAlways && state.Steps.Count > 0) Swap(state, AnimationsForState(state));
            return;
        }
        if (track < 0 || track >= state.Tracks.Count) return;
        var t = state.Tracks[track];
        var old = t.CurrentId;
        var now = AnimationForTrack(t);
        if (now == old) return;
        t.CurrentId = now;
        if (now != UUID.Zero) Send(now, true);
        if (old != UUID.Zero) Send(old, false);
    }

    // ---------- smart sit ----------

    void StartSitCancel()
    {
        _sitCancelTicks = 10;
        _sitCancelNext = _now + 0.1;
    }

    bool Foreign() => Current is { Smart: true } && SittingOnObject() && SeatAnimates();

    void CheckSitCancel()
    {
        if (!Foreign() || Current is not { } set) return;
        var st = set.State(AoStateKind.Sitting);
        if (st.Playing.Count == 0) return;
        StopCurrent(st);
        Send(AoMotions.SitGeneric, false);
        _sitCancelTicks = 0;
        _cycle = null;
        StopTracks();
    }

    // ---------- time ----------

    /// <summary>The clock (seconds, any origin; ticked at 10 Hz or so).</summary>
    public void Tick(double now)
    {
        _now = now;
        if (_sitCancelTicks > 0 && now >= _sitCancelNext)
        {
            _sitCancelTicks--;
            _sitCancelNext = now + 0.1;
            CheckSitCancel();
        }
        if (_cycle is { } c && now >= c.Due)
        {
            c.Due = now + c.Period;
            if (_enabled) Cycle(CycleMode.Any);
        }
        foreach (var (state, track, t) in _trackTimers.ToList())
            if (now >= t.Due) { t.Due = now + t.Period; TrackTimeout(state, track); }
        foreach (var (state, track, t) in _alwaysTimers.ToList())
            if (now >= t.Due) { t.Due = now + t.Period; TrackTimeout(state, track); }
    }

    // ---------- sending ----------

    void StopCurrent(AoState state, bool skipFirst = false)
    {
        for (int i = skipFirst ? 1 : 0; i < state.Playing.Count; i++)
            if (state.Playing[i] != UUID.Zero) Send(state.Playing[i], false);
        state.Playing.Clear();
    }

    void StopStandVariants()
    {
        Send(AoMotions.Stand1, false);
        Send(AoMotions.Stand2, false);
        Send(AoMotions.Stand3, false);
        Send(AoMotions.Stand4, false);
    }

    void StopSitVariants()
    {
        Send(AoMotions.SitFemale, false);
        Send(AoMotions.SitGeneric, false);
        if (SittingOnObject()) return;   // scripted seats that sit you with the ground sit
        Send(AoMotions.SitGround, false);
        Send(AoMotions.SitGroundConstrained, false);
    }

    /// <summary>Everything this engine has playing (for status and checks).</summary>
    public List<UUID> Playing()
    {
        var o = new List<UUID>();
        if (Current is { } set)
            foreach (var st in set.States)
            {
                o.AddRange(st.Playing);
                foreach (var t in st.Tracks) if (t.CurrentId != UUID.Zero) o.Add(t.CurrentId);
            }
        return o.Distinct().ToList();
    }
}

/// <summary>The default motions' ids (llanimationstates.cpp) and how Firestorm's states map to them.</summary>
public static class AoMotions
{
    public static readonly UUID Stand = new("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f");
    public static readonly UUID Stand1 = new("15468e00-3400-bb66-cecc-646d7c14458e");
    public static readonly UUID Stand2 = new("370f3a20-6ca6-9971-848c-9a01bc42ae3c");
    public static readonly UUID Stand3 = new("42b46214-4b44-79ae-deb8-0df61424ff4b");
    public static readonly UUID Stand4 = new("f22fed8b-a5ed-2c93-64d5-bdd8b93c889f");
    public static readonly UUID Walk = new("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0");
    public static readonly UUID WalkNew = new("33339176-7ddc-9397-94a4-bf3403cbc8f5");
    public static readonly UUID FemaleWalk = new("f5fc7433-043d-e819-8298-f519a119b688");
    public static readonly UUID FemaleWalkNew = new("d60c41d2-7c24-7074-d3fa-6101cea22a51");
    public static readonly UUID Run = new("05ddbff8-aaa9-92a1-2b74-8fe77a29b445");
    public static readonly UUID RunNew = new("1ab1b236-cd08-21e6-0cbc-0d923fc6eca2");
    public static readonly UUID FemaleRunNew = new("85995026-eade-5d78-d364-94a64512cb66");
    public static readonly UUID Sit = new("1a5fe8ac-a804-8a5d-7cbd-56bd83184568");
    public static readonly UUID SitFemale = new("b1709c8d-ecd3-54a1-4f28-d55ac0840782");
    public static readonly UUID SitGeneric = new("245f3c54-f1c0-bf2e-811f-46d8eeb386e7");
    public static readonly UUID SitGround = new("1c7600d6-661f-b87b-efe2-d7421eb93c86");
    public static readonly UUID SitGroundConstrained = new("1a2bd58e-87ff-0df8-0b4c-53e047b0bb6e");
    public static readonly UUID Crouch = new("201f3fdf-cb1f-dbec-201f-7333e328ae7c");
    public static readonly UUID CrouchWalk = new("47f5f6fb-22e5-ae44-f871-73aaaf4a6022");
    public static readonly UUID Land = new("7a17b059-12b2-41b1-570a-186368b6aa6f");
    public static readonly UUID MediumLand = new("f4f00d6e-b9fe-9292-f4cb-0ae06ea58d57");
    public static readonly UUID StandUp = new("3da1d753-028a-5446-24f3-9c9b856d9422");
    public static readonly UUID FallDown = new("666307d9-a860-572d-6fd4-c3ab8865c094");
    public static readonly UUID HoverDown = new("20f063ea-8306-2562-0b07-5c853b37b31e");
    public static readonly UUID HoverUp = new("62c5de58-cb33-5743-3d07-9e4cd4352864");
    public static readonly UUID Fly = new("aec4610c-757f-bc4e-c092-c6e9caf18daf");
    public static readonly UUID FlySlow = new("2b5a38b2-5e00-3a97-a495-4c826bc443e6");
    public static readonly UUID Hover = new("4ae8016b-31b9-03bb-c401-b1ea941db41d");
    public static readonly UUID Jump = new("2305bd75-1ca9-b03b-1faa-b176b8a8c49e");
    public static readonly UUID PreJump = new("7a4e87fe-de39-6fcb-6223-024b00893244");
    public static readonly UUID TurnRight = new("2d6daa51-3192-6794-8e2e-a15f8338ec30");
    public static readonly UUID TurnLeft = new("56e0ba0d-4a9f-7f27-6117-32f2ebbf6135");
    public static readonly UUID Type = new("c541c47f-e0c0-058b-ad1a-d6ae3a4584d9");

    /// <summary>Firestorm's state names (the ZHAO names first, alternates after), in AoStateKind's order.</summary>
    public static readonly string[][] Names =
    {
        new[] { "Standing", "Stand.1", "Stand.2", "Stand.3" }, new[] { "Walking", "Walk.N" }, new[] { "Running" },
        new[] { "Sitting", "Sit.N" }, new[] { "Sitting On Ground", "Sit.G" }, new[] { "Crouching", "Crouch" },
        new[] { "Crouch Walking", "Walk.C" }, new[] { "Landing", "Land.N" }, new[] { "Soft Landing" },
        new[] { "Standing Up", "Stand.U" }, new[] { "Falling" }, new[] { "Flying Down", "Hover.D" }, new[] { "Flying Up", "Hover.U" },
        new[] { "Flying", "Fly.N" }, new[] { "Flying Slow" }, new[] { "Hovering", "Hover.N" }, new[] { "Jumping", "Jump.N" },
        new[] { "Pre Jumping", "Jump.P" }, new[] { "Turning Right", "Turn.R" }, new[] { "Turning Left", "Turn.L" },
        new[] { "Typing" }, new[] { "Floating", "Swim.H" }, new[] { "Swimming Forward", "Swim.N" }, new[] { "Swimming Up", "Swim.U" },
        new[] { "Swimming Down", "Swim.D" }, new[] { "Always" },
    };

    /// <summary>The motion each state overrides (the swimming states share flying's: they're mapped under water;
    /// Always has none).</summary>
    public static readonly UUID[] Remap =
    {
        Stand, Walk, Run, Sit, SitGroundConstrained, Crouch, CrouchWalk, Land, MediumLand, StandUp, FallDown, HoverDown, HoverUp,
        Fly, FlySlow, Hover, Jump, PreJump, TurnRight, TurnLeft, Type, Hover, Fly, HoverUp, HoverDown, UUID.Zero,
    };

    /// <summary>Played beside the Linden motion rather than instead of it: the viewer needs it to know the state.</summary>
    public static bool Transient(UUID m) =>
        m == SitGround || m == SitGroundConstrained || m == PreJump || m == StandUp || m == Land || m == MediumLand;

    public static AoStateKind? KindByName(string name)
    {
        for (int i = 0; i < Names.Length; i++)
            foreach (var n in Names[i])
                if (n == name) return (AoStateKind)i;
        for (int i = 0; i < Names.Length; i++)
            foreach (var n in Names[i])
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return (AoStateKind)i;
        return null;
    }
}

public enum AoStateKind
{
    Standing, Walking, Running, Sitting, SittingOnGround, Crouching, CrouchWalking, Landing, SoftLanding, StandingUp, Falling,
    FlyingDown, FlyingUp, Flying, FlyingSlow, Hovering, Jumping, PreJumping, TurningRight, TurningLeft, Typing, Floating,
    SwimmingForward, SwimmingUp, SwimmingDown, Always,
}

public sealed class AoAnimation
{
    public string Name { get; set; } = "";
    /// <summary>The inventory item (usually a link) in the state's folder; zero for a notecard set's.</summary>
    public UUID ItemId { get; set; }
    public UUID AssetId { get; set; }
    public int Sort { get; set; } = -1;
}

/// <summary>One step of a state: an animation, or a group of them (a subfolder, "name:OR&lt;order&gt;") played together.</summary>
public sealed class AoStep
{
    public string Name { get; set; } = "";
    public UUID Id { get; set; }
    public int Sort { get; set; } = -1;
    public bool IsGroup { get; set; }
    public List<AoAnimation> Members { get; } = new();
}

/// <summary>A track: a subfolder "name:TR[:CT&lt;s&gt;][:CY][:RN]" of animations played alongside the state's own.</summary>
public sealed class AoTrack
{
    public string Name { get; set; } = "";
    public UUID FolderId { get; set; }
    public bool Cycle { get; set; }
    public bool Random { get; set; }
    public float CycleTime { get; set; }
    public List<AoAnimation> Animations { get; } = new();
    public int Current { get; set; }
    public UUID CurrentId { get; set; }
}

public sealed class AoState
{
    public AoStateKind Kind { get; init; }
    public string Name => AoMotions.Names[(int)Kind][0];
    public UUID Motion => AoMotions.Remap[(int)Kind];
    public UUID FolderId { get; set; }
    public bool Cycle { get; set; }
    public bool Random { get; set; }
    public float CycleTime { get; set; }
    public List<AoStep> Steps { get; } = new();
    public List<AoTrack> Tracks { get; } = new();
    public int Current { get; set; }
    /// <summary>What the engine has playing for this state now.</summary>
    public List<UUID> Playing { get; } = new();
}

public sealed class AoSet
{
    public string Name { get; set; } = "";
    /// <summary>The set's folder (a Firestorm-format set), or the folder a ZHAO-II notecard set was read from.</summary>
    public UUID FolderId { get; set; }
    /// <summary>Read from a ZHAO-II notecard in place (its flags are kept by Sojourn, not in folder names).</summary>
    public bool FromNotecard { get; set; }
    public bool SitOverride { get; set; }
    public bool Smart { get; set; }
    public bool MouselookStandDisable { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>The motion the set is overriding now.</summary>
    public UUID Motion { get; set; }
    public AoState[] States { get; } = Enumerable.Range(0, AoMotions.Names.Length).Select(i => new AoState { Kind = (AoStateKind)i }).ToArray();

    public AoState State(AoStateKind k) => States[(int)k];

    public AoState? StateByMotion(UUID motion)
    {
        if (motion == UUID.Zero) return null;
        if (motion == AoMotions.SitGround) motion = AoMotions.SitGroundConstrained;
        foreach (var s in States) if (s.Motion == motion) return s;
        return null;
    }
}
