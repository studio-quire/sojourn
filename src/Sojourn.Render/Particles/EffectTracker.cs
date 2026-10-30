using System.Diagnostics;
using System.Numerics;
using LibreMetaverse;

namespace Sojourn.Render.Particles;

/// <summary>Seconds on a monotonic clock: the time base for particle sources and texture animations.</summary>
public static class EffectClock
{
    static readonly long Origin = Stopwatch.GetTimestamp();
    public static double Now => (Stopwatch.GetTimestamp() - Origin) / (double)Stopwatch.Frequency;
}

/// <summary>One particle system a prim had from <see cref="Since"/> (null: none from then on). Particles copy their
/// parameters at birth, so the history decides what each live particle looks like. <see cref="EpochStart"/> is when
/// the viewer's source timer last started (LLViewerPartSourceScript: a new source, or a changed max/start age).</summary>
public readonly record struct PsEntry(double Since, Primitive.ParticleSystem? Ps, double EpochStart);

/// <summary>A prim's particle source as a viewer that has been watching all along would have it.</summary>
public sealed class ParticleSource
{
    public uint LocalId;
    public UUID Id;
    public UUID Owner;
    public readonly List<PsEntry> History = new();
    /// <summary>The prim is gone (killed or out of the region): its particles live out their ages at the last pose.</summary>
    public bool Gone;
    /// <summary>Where the source was over the last half minute (region frame), for particles born while it moved.</summary>
    public readonly List<PoseSample> Poses = new();

    public PsEntry? Last => History.Count > 0 ? History[^1] : null;
    public bool On => History.Count > 0 && History[^1].Ps != null;

    public void AddPose(double t, Vector3 pos, Quaternion rot)
    {
        if (Poses.Count > 0)
        {
            var p = Poses[^1];
            if (t <= p.T) { Poses[^1] = new PoseSample(p.T, pos, rot); return; }
            // keep a sample per second while still, every observation while moving
            if (Vector3.DistanceSquared(p.Pos, pos) < 1e-4f && MathF.Abs(Quaternion.Dot(p.Rot, rot)) > 0.99999f && t - p.T < 1.0)
                return;
        }
        Poses.Add(new PoseSample(t, pos, rot));
        if (Poses.Count > 400) Poses.RemoveRange(0, Poses.Count - 400);
    }

    /// <summary>The source's pose at time t (linear between samples, the nearest outside them).</summary>
    public PoseSample PoseAt(double t)
    {
        if (Poses.Count == 0) return default;
        if (t >= Poses[^1].T) return Poses[^1];
        if (t <= Poses[0].T) return Poses[0];
        int lo = 0, hi = Poses.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (Poses[mid].T <= t) lo = mid; else hi = mid;
        }
        var a = Poses[lo]; var b = Poses[hi];
        float f = (float)((t - a.T) / Math.Max(1e-9, b.T - a.T));
        return new PoseSample(t, Vector3.Lerp(a.Pos, b.Pos, f), Quaternion.Slerp(a.Rot, b.Rot, f));
    }
}

public readonly record struct PoseSample(double T, Vector3 Pos, Quaternion Rot);

/// <summary>A prim's texture animation and when its timer started (LLViewerTextureAnim::reset).</summary>
public sealed class TextureAnimState
{
    public Primitive.TextureAnimation Anim;
    public double Start;
    public object? TexturesRef;
}

/// <summary>
/// Watches one region's prims for particle systems and texture animations and timestamps their changes, so a render
/// at any moment can reproduce what a viewer that had been there all along would show. Run often (the render loop
/// calls it a few times a second, and every render): the timestamps are as good as the observation rate.
/// Render thread only.
/// </summary>
public sealed class EffectTracker
{
    /// <summary>How long particles can live (llpartdata: PSYS_PART_MAX_AGE is clamped to 30 s) plus slack.</summary>
    public const double HistorySeconds = 32;
    /// <summary>A source seen for the first time (renderer start, a region coming into view) is taken to have been
    /// running long enough to be steady: as long as a particle lives.</summary>
    public const double PrewarmSeconds = 31;

    readonly Dictionary<uint, ParticleSource> _sources = new();
    readonly Dictionary<uint, TextureAnimState> _anims = new();
    readonly Dictionary<uint, UUID> _known = new();
    readonly HashSet<uint> _seen = new();
    bool _started;
    public double LastObserved { get; private set; } = double.NegativeInfinity;

    public IReadOnlyDictionary<uint, ParticleSource> Sources => _sources;

    public TextureAnimState? Anim(uint localId) => _anims.TryGetValue(localId, out var a) ? a : null;

    public static bool IsOn(in Primitive.ParticleSystem ps) => ps.CRC != 0;

    /// <param name="handle">The region's handle (particle systems come from <see cref="ParticleSystemTracker"/>
    /// when it has seen the object: LibreMetaverse misreads them from compressed updates).</param>
    public void Observe(ulong handle, IReadOnlyDictionary<uint, Primitive> prims, double now)
    {
        if (Incremental && _started)
        {
            // the scene tells of each change (ObserveOne, Forget): only the old history needs pruning now and then
            if (now - LastObserved >= 0.5) { LastObserved = now; Prune(now); }
            return;
        }
        if (now - LastObserved < 0.02) return;   // several views in one frame
        LastObserved = now;
        bool first = !_started;
        _started = true;
        _seen.Clear();
        foreach (var prim in prims.Values)
        {
            uint id = prim.LocalID;
            _seen.Add(id);
            bool firstSight = first || !_known.TryGetValue(id, out var knownId) || knownId != prim.ID;
            if (firstSight)
            {
                _known[id] = prim.ID;
                if (_sources.TryGetValue(id, out var stale) && stale.Id != prim.ID) { _sources.Remove(id); }
                if (_anims.TryGetValue(id, out var staleAnim)) _anims.Remove(id);
            }
            var ps = ParticleSystemTracker.Shared.TryGet(handle, id, out var tracked) ? tracked ?? default : prim.ParticleSys;
            ObserveParticles(prim, ps, now, firstSight);
            ObserveAnim(prim, now);
        }
        // prims that left: their sources stop; their particles fade out where they were
        if (_known.Count > _seen.Count)
            foreach (var id in _known.Keys.Where(k => !_seen.Contains(k)).ToList())
            {
                _known.Remove(id);
                _anims.Remove(id);
                if (_sources.TryGetValue(id, out var s) && !s.Gone)
                {
                    s.Gone = true;
                    if (s.On) s.History.Add(new PsEntry(now, null, s.History[^1].EpochStart));
                }
            }
        Prune(now);
    }

    /// <summary>The region's scene reports each object that changed (RegionScene.Index.cs), so <see cref="Observe"/>
    /// needn't walk every object each frame (on a busy region that walk was a quarter of the render thread).</summary>
    public bool Incremental { get; set; }

    /// <summary>One object came or changed (incremental mode).</summary>
    public void ObserveOne(ulong handle, Primitive prim, double now)
    {
        _started = true;
        uint id = prim.LocalID;
        bool firstSight = !_known.TryGetValue(id, out var knownId) || knownId != prim.ID;
        if (firstSight)
        {
            _known[id] = prim.ID;
            if (_sources.TryGetValue(id, out var stale) && stale.Id != prim.ID) _sources.Remove(id);
            _anims.Remove(id);
        }
        var ps = ParticleSystemTracker.Shared.TryGet(handle, id, out var tracked) ? tracked ?? default : prim.ParticleSys;
        ObserveParticles(prim, ps, now, firstSight);
        ObserveAnim(prim, now);
    }

    /// <summary>One object left (incremental mode): its source stops, its particles fade out where they were.</summary>
    public void Forget(uint id, double now)
    {
        if (!_known.Remove(id)) return;
        _anims.Remove(id);
        if (_sources.TryGetValue(id, out var s) && !s.Gone)
        {
            s.Gone = true;
            if (s.On) s.History.Add(new PsEntry(now, null, s.History[^1].EpochStart));
        }
    }

    void ObserveParticles(Primitive prim, Primitive.ParticleSystem ps, double now, bool firstSight)
    {
        bool on = IsOn(ps);
        _sources.TryGetValue(prim.LocalID, out var src);
        if (src == null)
        {
            if (!on) return;
            src = new ParticleSource { LocalId = prim.LocalID, Id = prim.ID, Owner = prim.OwnerID };
            _sources[prim.LocalID] = src;
        }
        src.Gone = false;
        src.Owner = prim.OwnerID;
        var last = src.Last;
        if (last is not { Ps: { } lastPs })
        {
            if (!on) return;
            // a new source: if this is the first we've seen of the prim, it may have been going for any time
            double start = firstSight ? (ps.MaxAge > 0 ? now : now - PrewarmSeconds) : now;
            src.History.Add(new PsEntry(start, ps, start));
            return;
        }
        if (!on)
        {
            src.History.Add(new PsEntry(now, null, last.Value.EpochStart));
            return;
        }
        if (ps.Equals(lastPs)) return;
        // unpackPSS: an existing source keeps its timer unless a max age is set and max or start age changed
        bool reset = ps.MaxAge != 0 && (ps.MaxAge != lastPs.MaxAge || ps.StartAge != lastPs.StartAge);
        src.History.Add(new PsEntry(now, ps, reset ? now : last.Value.EpochStart));
    }

    void ObserveAnim(Primitive prim, double now)
    {
        var ta = prim.TextureAnim;
        bool on = (ta.Flags & Primitive.TextureAnimMode.ANIM_ON) != 0;
        _anims.TryGetValue(prim.LocalID, out var st);
        if (!on)
        {
            if (st != null) _anims.Remove(prim.LocalID);
            return;
        }
        if (st == null)
        {
            _anims[prim.LocalID] = new TextureAnimState { Anim = ta, Start = now, TexturesRef = prim.Textures };
            return;
        }
        // LLVOVolume::processUpdateMessage resets a frame-stepped animation on every full update that carries it;
        // a new TextureEntry object marks a full update here
        bool fullUpdate = !ReferenceEquals(st.TexturesRef, prim.Textures);
        if (!ta.Equals(st.Anim) || (fullUpdate && (ta.Flags & Primitive.TextureAnimMode.SMOOTH) == 0))
            st.Start = now;
        st.Anim = ta;
        st.TexturesRef = prim.Textures;
    }

    void Prune(double now)
    {
        double cutoff = now - HistorySeconds;
        List<uint>? dead = null;
        foreach (var (id, s) in _sources)
        {
            // keep one entry before the cutoff (it covers the start of the window)
            int drop = 0;
            while (drop + 1 < s.History.Count && s.History[drop + 1].Since < cutoff) drop++;
            if (drop > 0) s.History.RemoveRange(0, drop);
            if (s.Poses.Count > 2)
            {
                int pd = 0;
                while (pd + 2 < s.Poses.Count && s.Poses[pd + 1].T < cutoff) pd++;
                if (pd > 0) s.Poses.RemoveRange(0, pd);
            }
            if (!s.On && s.History.Count > 0 && s.History[^1].Since < cutoff) (dead ??= new()).Add(id);
            else if (s.History.Count == 0) (dead ??= new()).Add(id);
        }
        if (dead != null) foreach (var id in dead) _sources.Remove(id);
    }
}
