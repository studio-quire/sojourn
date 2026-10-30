// Posing an avatar at one moment from its signalled animations, the way the Second Life viewer's motion controller
// would have it posed then: llcharacter/llmotioncontroller.cpp (activation, ease in/out weights, newest first),
// llcharacter/llpose.cpp (LLJointStateBlender: priorities, top-down weight fill), llkeyframemotion.cpp (hand pose
// publication), newview/llvoavatar.cpp (remapMotionID, the default motions every avatar runs: head and eye look-at
// at rest, breathing, pelvis fix), newview/llemote.cpp (expression morphs). LGPL 2.1, Linden Research, Inc.
using System.Collections.Concurrent;
using System.Numerics;
using LibreMetaverse;
using Sojourn.Render.World;

namespace Sojourn.Render.Bodies;

/// <summary>Decoded animations by asset id, shared by every avatar (fetched through any region's asset source). Kept
/// while used: those no avatar has played for <see cref="IdleMs"/> go, and the least lately used past a minute idle
/// while the decoded total is over <see cref="Budget"/> (every animation seen all session was kept: 44 MB for ~630
/// after a 20-minute tour, 2026-10-28; dance places cycle through hundreds). One wanted again loads again (from disk).</summary>
public sealed class MotionLibrary(SkeletonDef skeleton, Action<string>? log = null)
{
    readonly ConcurrentDictionary<UUID, Entry> _motions = new();

    sealed class Entry
    {
        public volatile KeyframeMotion? Motion;
        public volatile bool Done;
        public long FailedAt;
        public long LastUsed = System.Environment.TickCount64;
        public long Bytes;
    }

    public const long IdleMs = 10 * 60_000;
    public static long Budget = 64L << 20;
    long _sweptAt = System.Environment.TickCount64, _bytes;

    static long BytesOf(KeyframeMotion m)
    {
        long b = 200;
        foreach (var j in m.Joints) b += 120 + j.RotTimes.Length * 4L + j.Rots.Length * 16L + j.PosTimes.Length * 4L + j.Positions.Length * 12L;
        return b;
    }

    void Sweep(long now)
    {
        if (now - Interlocked.Read(ref _sweptAt) < 30_000) return;
        Interlocked.Exchange(ref _sweptAt, now);
        List<(UUID Id, Entry E)>? lru = null;
        foreach (var (id, e) in _motions)
        {
            if (!e.Done) continue;
            long idle = now - Volatile.Read(ref e.LastUsed);
            if (idle > IdleMs) Remove(id, e);
            else if (idle > 60_000) (lru ??= new()).Add((id, e));
        }
        if (lru == null || Interlocked.Read(ref _bytes) <= Budget) return;
        lru.Sort((a, b) => Volatile.Read(ref a.E.LastUsed).CompareTo(Volatile.Read(ref b.E.LastUsed)));
        foreach (var (id, e) in lru)
        {
            if (Interlocked.Read(ref _bytes) <= Budget) break;
            Remove(id, e);
        }
    }

    void Remove(UUID id, Entry e)
    {
        if (_motions.TryRemove(KeyValuePair.Create(id, e))) Interlocked.Add(ref _bytes, -e.Bytes);
    }

    static readonly bool Debug = System.Environment.GetEnvironmentVariable("SOJOURN_AVATAR_DEBUG") == "1";

    /// <summary>The motion if decoded; null while loading (<paramref name="loading"/>) or if it failed (a failed
    /// fetch is tried again after a few seconds; a motion that doesn't decode stays failed).</summary>
    public KeyframeMotion? Get(UUID id, IAssetSource source, out bool loading)
    {
        long now = System.Environment.TickCount64;
        Sweep(now);
        if (_motions.TryGetValue(id, out var old) && old.FailedAt != 0 && now - old.FailedAt > 5000)
            Remove(id, old);
        var e = _motions.GetOrAdd(id, key =>
        {
            var n = new Entry();
            _ = Task.Run(async () =>
            {
                try
                {
                    var data = await source.GetAssetAsync(id, AssetType.Animation, CancellationToken.None).ConfigureAwait(false);
                    if (data == null)
                    {
                        log?.Invoke($"animation {id}: not available");
                        n.FailedAt = System.Environment.TickCount64;
                        return;
                    }
                    var m = KeyframeMotion.Decode(data, skeleton);
                    n.Bytes = BytesOf(m);
                    Interlocked.Add(ref _bytes, n.Bytes);
                    n.Motion = m;
                    if (Debug)
                        log?.Invoke($"animation {id}: " + string.Join(" ", n.Motion.Joints.Select(j =>
                            $"{j.Name}[p{j.Priority} r{j.Rots.Length} t{j.Positions.Length}]")) + $" constraints {n.Motion.Constraints.Length}");
                }
                catch (Exception ex) { log?.Invoke($"animation {id}: {ex.GetType().Name}: {ex.Message}"); }
                finally { n.Done = true; }
            });
            return n;
        });
        Volatile.Write(ref e.LastUsed, now);
        loading = !e.Done;
        return e.Motion;
    }
}

/// <summary>One avatar's motion state: when each signalled animation started (as this process saw it), and the pose
/// it gives at a moment.</summary>
public sealed partial class AvatarAnimator
{
    sealed class Active
    {
        public UUID Id;
        public int Sequence;
        public double Start;
        public long Order;
        public FootState? Foot;   // a standing motion's foot IK (Animator.Ik.cs)
    }

    object? _listRef;
    readonly Dictionary<UUID, Active> _active = new();
    long _order;
    readonly double _born;
    bool _synced;

    /// <summary>When nothing says otherwise, how long ago the animations found at the first look started: long
    /// enough that sits have settled and one-shot gestures are over.</summary>
    const double UnknownAge = 3600;

    public AvatarAnimator(double now) => _born = now;

    /// <summary>Follow the avatar's animation list (LibreMetaverse replaces the list on every AvatarAnimation).
    /// Called often (the renderer polls every avatar about ten times a second), so start times are close to when
    /// the region signalled them; the list found at the first look is taken as already playing.</summary>
    public bool Sync(List<Animation>? anims, double now)
    {
        if (ReferenceEquals(anims, _listRef)) return false;
        _listRef = anims;
        var seen = new HashSet<UUID>();
        if (anims != null)
        {
            Animation[] copy;
            lock (anims) copy = anims.ToArray();
            foreach (var a in copy)
            {
                seen.Add(a.AnimationID);
                if (_active.TryGetValue(a.AnimationID, out var cur) && cur.Sequence == a.AnimationSequence) continue;
                _active[a.AnimationID] = new Active
                {
                    Id = a.AnimationID, Sequence = a.AnimationSequence, Start = _synced ? now : now - UnknownAge, Order = ++_order,
                };
            }
        }
        _synced = true;
        foreach (var id in _active.Keys.Where(k => !seen.Contains(k)).ToList()) _active.Remove(id);
        return true;
    }

    public string Describe(MotionLibrary lib, IAssetSource source) => string.Join("; ", _active.Values.Select(a =>
    {
        var m = lib.Get(a.Id, source, out bool loading);
        return $"{a.Id.ToString()[..8]} {(m == null ? (loading ? "loading" : "failed") : $"pri {m.BasePriority} dur {m.Duration:F2} loop {m.Loop} joints {m.Joints.Length} ease {m.EaseIn:F2}/{m.EaseOut:F2}")}";
    }));

    /// <summary>LLVOAvatar::remapMotionID with UseNewWalkRun on (the default).</summary>
    static UUID Remap(UUID id, bool male)
    {
        if (!male)
        {
            if (id == Anim.Walk) return Anim.FemaleWalkNew;
            if (id == Anim.Run) return Anim.FemaleRunNew;
            if (id == Anim.Sit) return Anim.SitFemale;
            return id;
        }
        if (id == Anim.Walk) return Anim.WalkNew;
        if (id == Anim.Run) return Anim.RunNew;
        if (id == Anim.SitFemale) return Anim.Sit;
        return id;
    }

    struct JointState
    {
        public int Priority;
        public long Order;
        public float Weight;
        public Quaternion? Rot;
        public Vector3? Pos;
    }

    readonly List<JointState>[] _states = Enumerable.Range(0, 256).Select(_ => new List<JointState>(4)).ToArray();

    /// <summary>Pose the skeleton's local transforms at <paramref name="now"/>, starting from the shape's rest
    /// pose. Returns the animated morph weights (hand pose, expressions) by param id; counts motions still loading.</summary>
    public Dictionary<int, float> Evaluate(BodyShape shape, SkeletonPose pose, MotionLibrary lib, IAssetSource source, double now, ref int pending,
        Vector3[]? restPos = null, Vector3[]? restScale = null)
    {
        _restPos = restPos ?? shape.JointPos;
        _restScale = restScale ?? shape.JointScale;
        var cd = shape.Data;
        var sk = cd.Skeleton;
        int n = sk.Joints.Count;
        var morphs = new Dictionary<int, float>();
        int handPose = -1, handPri = int.MinValue;
        var emotes = _emotes;
        emotes.Clear();
        // the editing motion (a received PointAt): its arm, and its hand pose unless a motion of priority 3 or more
        // publishes one after it (Animator.Ik.cs)
        Edit(sk, pose, now);
        if (_editW > 0) (handPose, handPri) = (EditHandPose, EditHandPriority);

        // newest first (llmotioncontroller pushes activations to the front)
        var samples = _samples;
        samples.Clear();
        _byOrder.Clear();
        _byOrder.AddRange(_active.Values);
        _byOrder.Sort(static (a, b) => b.Order.CompareTo(a.Order));
        int pooled = 0;
        foreach (var a in _byOrder)
        {
            float t = (float)Math.Min(now - a.Start, UnknownAge);
            var id = Remap(a.Id, shape.Male);
            if (Anim.Expressions.TryGetValue(id, out var exprName))
            {
                float ew = EmoteWeight(t);
                if (ew > 0) emotes.Add((exprName, ew));
                continue;
            }
            if (Anim.Procedural.Contains(id)) continue;
            var m = lib.Get(id, source, out bool loading);
            if (m == null) { if (loading) pending++; continue; }
            float w = MotionWeight(m, t, out bool finished);
            if (finished) continue;
            float lt = m.LocalTime(t);
            if (!m.Loop) lt = Math.Min(lt, m.Duration);
            // the per-joint arrays are the animator's own, reused frame to frame (an avatar a frame each allocated them)
            if (pooled == _sampleArrays.Count) _sampleArrays.Add((new Quaternion?[n], new Vector3?[n]));
            var (rots, poss) = _sampleArrays[pooled];
            if (rots.Length < n) _sampleArrays[pooled] = (rots, poss) = (new Quaternion?[n], new Vector3?[n]);
            else { Array.Clear(rots, 0, n); Array.Clear(poss, 0, n); }
            pooled++;
            var smp = new Sample(a, m, w, lt, rots, poss);
            foreach (var jm in m.Joints)
            {
                if (jm.Joint < 0 || jm.Joint >= n) continue;
                if (jm.Rots.Length > 0) smp.Rots[jm.Joint] = KeyframeMotion.SampleRot(jm, lt);
                if (jm.Positions.Length > 0) smp.Poss[jm.Joint] = KeyframeMotion.SamplePos(jm, lt);
            }
            if (Anim.StandIk.Contains(id)) StandIk(sk, pose, smp);   // the default stands' feet on the ground
            samples.Add(smp);
            // LLKeyframeMotion::applyKeyframes publishes its hand pose when its top joint priority is at least the
            // current one (newest first, so the oldest of equals wins)
            if (handPri == int.MinValue || m.MaxPriority >= handPri) { handPose = m.HandPose; handPri = m.MaxPriority; }
            if (m.Emote.Length > 0 && Anim.ExpressionByName.TryGetValue(m.Emote.ToLowerInvariant(), out var en))
            {
                float ew = EmoteWeight(t);
                if (ew > 0) emotes.Add((en, ew));
            }
        }

        float life = (float)(now - _born);
        Look(sk, pose, now);
        Blend(sk, shape, pose, samples, life);

        // constraints: each motion's IK runs on the blended pose with its own chain joints at its keyframes, unless
        // a newer motion already drives one of those joints at its priority or above; then blend again
        bool constrained = false;
        for (int i = 0; i < samples.Count; i++)
        {
            var smp = samples[i];
            if (smp.M.Constraints.Length == 0) continue;
            _tmp ??= new SkeletonPose(sk);
            foreach (var c in smp.M.Constraints)
            {
                bool masked = false;
                for (int k = 0; k < i && !masked; k++)
                    foreach (int j in c.Chain)
                        if (samples[k].Rots[j] != null && JointPriority(samples[k].M, j) >= smp.M.BasePriority) { masked = true; break; }
                if (masked) continue;
                Array.Copy(pose.Pos, _tmp.Pos, n);
                Array.Copy(pose.Rot, _tmp.Rot, n);
                Array.Copy(pose.Scale, _tmp.Scale, n);
                Constraints.Apply(c, smp.T, _tmp, smp.Rots, smp.Poss, -shape.PelvisToFoot);
                constrained = true;
            }
        }
        if (constrained) Blend(sk, shape, pose, samples, life);

        // LLHandMotion: the published pose's morph at full weight, relaxed when nobody asks
        if (handPose < 0) handPose = 1;
        if (handPose > 0 && handPose < Anim.HandPoses.Length && cd.ParamIdByName.TryGetValue(Anim.HandPoses[handPose], out var hid))
            morphs[hid] = 1f;
        // LLEmote: the expression's morph from min to max with the motion's weight, cross-faded against
        // Express_Closed_Mouth
        if (emotes.Count > 0)
        {
            float closed = 1;
            foreach (var (name, ew) in emotes)
            {
                if (!cd.ParamIdByName.TryGetValue(name, out var eid)) continue;
                var r = cd.Ranges[eid];
                morphs[eid] = r.Min + ew * (r.Max - r.Min);
                closed = MathF.Min(closed, 1 - ew);
            }
            if (cd.ParamIdByName.TryGetValue("Express_Closed_Mouth", out var cid))
            {
                var r = cd.Ranges[cid];
                morphs[cid] = r.Min + closed * (r.Max - r.Min);
            }
        }
        return morphs;
    }

    sealed record Sample(Active A, KeyframeMotion M, float W, float T, Quaternion?[] Rots, Vector3?[] Poss);

    readonly List<Sample> _samples = new();
    readonly List<(string Name, float Weight)> _emotes = new();
    readonly List<Active> _byOrder = new();
    readonly List<(Quaternion?[] Rots, Vector3?[] Poss)> _sampleArrays = new();

    SkeletonPose? _tmp;
    Vector3[] _restPos = Array.Empty<Vector3>(), _restScale = Array.Empty<Vector3>();

    static int JointPriority(KeyframeMotion m, int joint)
    {
        foreach (var jm in m.Joints) if (jm.Joint == joint) return jm.Priority;
        return m.BasePriority;
    }

    void Blend(SkeletonDef sk, BodyShape shape, SkeletonPose pose, List<Sample> samples, float life)
    {
        int n = sk.Joints.Count;
        for (int i = 0; i < n; i++)
        {
            pose.Pos[i] = _restPos[i];
            pose.Scale[i] = _restScale[i];
            pose.Rot[i] = sk.Joints[i].Rot;
            _states[i].Clear();
        }
        foreach (var smp in samples)
            foreach (var jm in smp.M.Joints)
            {
                int j = jm.Joint;
                if (j < 0 || j >= n || (smp.Rots[j] == null && smp.Poss[j] == null)) continue;
                _states[j].Add(new JointState { Priority = jm.Priority, Order = smp.A.Order, Weight = smp.W, Rot = smp.Rots[j], Pos = smp.Poss[j] });
            }

        // the motions every avatar runs from birth (oldest: they lose priority ties)
        const int Medium = 1, Low = 0;
        void Proc(string joint, int pri, Quaternion? rot, Vector3? pos)
        {
            if (!sk.TryGet(joint, out var j)) return;
            _states[j].Add(new JointState { Priority = pri, Order = -1, Weight = 1, Rot = rot, Pos = pos });
        }
        // head and eyes toward the look-at point, straight ahead with none (LLHeadRotMotion, LLEyeMotion: Look)
        Proc("mTorso", Medium, _qTorso, null);
        Proc("mNeck", Medium, _qNeck, null);
        Proc("mHead", Medium, _qHead, null);
        Proc("mEyeLeft", Medium, _qEyeL, null);
        Proc("mEyeRight", Medium, _qEyeR, null);
        Proc("mFaceEyeAltLeft", Medium, _qEyeL, null);
        Proc("mFaceEyeAltRight", Medium, _qEyeR, null);
        // LLBreatheMotionRot: sin(t) * 0.05 rad about Y on the chest
        Proc("mChest", Medium, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Sin(life) * 0.05f), null);
        // LLPelvisFixMotion
        Proc("mPelvis", Low, null, Vector3.Zero);
        AddEditStates();   // LLEditingMotion, at high priority (Animator.Ik.cs)

        // LLJointStateBlender::blendJointStates: up to six states, high priority first (newer first on ties); the
        // first full state sets the value, lower ones only fill the weight left over
        for (int j = 0; j < n; j++)
        {
            var list = _states[j];
            if (list.Count == 0) continue;
            list.Sort((x, y) => x.Priority != y.Priority ? y.Priority.CompareTo(x.Priority) : y.Order.CompareTo(x.Order));
            if (list.Count > 6) list.RemoveRange(6, list.Count - 6);
            float rsum = 0, psum = 0;
            Quaternion rot = Quaternion.Identity;
            Vector3 pos = Vector3.Zero;
            bool hasRot = false, hasPos = false;
            foreach (var st in list)
            {
                if (st.Weight <= 0) continue;
                if (st.Rot is { } r && rsum < 1)
                {
                    if (!hasRot) { rot = r; rsum = st.Weight; hasRot = true; }
                    else
                    {
                        float ns = MathF.Min(1, st.Weight + rsum);
                        rot = KeyframeMotion.Nlerp(rsum / ns, r, rot);
                        rsum = ns;
                    }
                }
                if (st.Pos is { } p && psum < 1)
                {
                    if (!hasPos) { pos = p; psum = st.Weight; hasPos = true; }
                    else
                    {
                        float ns = MathF.Min(1, st.Weight + psum);
                        pos = Vector3.Lerp(p, pos, psum / ns);
                        psum = ns;
                    }
                }
            }
            if (hasRot) pose.Rot[j] = rot;
            if (hasPos) pose.Pos[j] = pos;
        }
    }

    // --- LLHeadRotMotion and LLEyeMotion (llcharacter/llheadrotmotion.cpp) -------------------------------------------

    /// <summary>Where this avatar looks, from its LookAt viewer effect (World/Avatars.LookAt.cs), set before each
    /// Evaluate: the point relative to its head and the world's up, both in its root's frame; null without one.</summary>
    public (Vector3 Point, Vector3 Up)? LookAt { get; set; }

    (Vector3 Point, Vector3 Up) _looked;   // the last point: held while the motion eases out
    float _lookIn;                         // how far it has eased in, 0-1 (it starts and stops with the effect, 1 s each way)
    double _lookNow = double.NaN;
    Quaternion _lastHeadRot = Quaternion.Identity, _torsoRot = Quaternion.Identity;
    Quaternion _qTorso = Quaternion.Identity, _qNeck = Quaternion.Identity, _qHead = Quaternion.Identity,
        _qEyeL = Quaternion.Identity, _qEyeR = Quaternion.Identity;

    const float TorsoLag = 0.35f, NeckLag = 0.5f, HeadHalfLife = 0.15f, TorsoHalfLife = 0.27f;
    const float HeadLimit = MathF.PI / 2 * 0.8f, MinLookDistance = 0.3f, EyeLimit = MathF.PI / 2 * 0.3f;

    /// <summary>The torso, neck, head and eyes turned toward the look-at point, from last frame's pose (the viewer's
    /// motions read their joints' world rotations, last frame's), all in the root's frame. The viewer starts the head's
    /// motion when an effect arrives and stops it at a None, each eased over a second; the eyes ease with it here
    /// (without a point they look ahead; their jitter and blinks aren't drawn).</summary>
    void Look(SkeletonDef sk, SkeletonPose pose, double now)
    {
        float dt = double.IsNaN(_lookNow) ? 0 : (float)Math.Clamp(now - _lookNow, 0, 0.1);
        _lookNow = now;
        if (LookAt is { } l) _looked = l;
        _lookIn = Math.Clamp(_lookIn + (LookAt != null ? dt : -dt), 0, 1);
        _qTorso = _qNeck = _qHead = _qEyeL = _qEyeR = Quaternion.Identity;
        if (_lookIn <= 0 || !sk.TryGet("mPelvis", out int pelvis) || !sk.TryGet("mChest", out int chest) || !sk.TryGet("mHead", out int headJ)
            || !sk.TryGet("mEyeLeft", out int eyeL) || !sk.TryGet("mEyeRight", out int eyeR)) return;
        float w = CubicStep(_lookIn);
        Quaternion InRoot(int j)
        {
            var q = Quaternion.Identity;
            for (; j >= 0; j = sk.Joints[j].Parent) q = pose.Rot[j] * q;
            return q;
        }
        var (point, up) = _looked;
        float dist = point.Length();
        if (!(dist > 0)) return;

        // LLHeadRotMotion::onUpdate
        Quaternion target;
        if (dist < MinLookDistance) target = pose.Rot[pelvis];
        else
        {
            var at = point / dist;
            var left = Vector3.Cross(Vector3.UnitZ, at);
            if (left.LengthSquared() < 0.15f)
            {
                at = Vector3.Normalize(Vector3.Lerp(at, Vector3.UnitX, 0.4f));
                left = Vector3.Cross(Vector3.UnitZ, at);
            }
            target = Basis(at, Vector3.Normalize(left));
        }
        var headRot = Constrain(target, HeadLimit);
        var torsoTarget = KeyframeMotion.Nlerp(TorsoLag, Quaternion.Identity, headRot);
        _torsoRot = KeyframeMotion.Nlerp(Interpolant(TorsoHalfLife, dt), _torsoRot, torsoTarget);
        headRot = KeyframeMotion.Nlerp(Interpolant(HeadHalfLife, dt), _lastHeadRot, headRot);
        _lastHeadRot = headRot;
        // the neck and head share what's left after the chest (the neck's parent)
        var rel = Quaternion.Inverse(InRoot(chest)) * headRot;
        _qTorso = KeyframeMotion.Nlerp(w, Quaternion.Identity, _torsoRot);
        _qNeck = KeyframeMotion.Nlerp(w, Quaternion.Identity, KeyframeMotion.Nlerp(NeckLag, Quaternion.Identity, rel));
        _qHead = KeyframeMotion.Nlerp(w, Quaternion.Identity, KeyframeMotion.Nlerp(1 - NeckLag, Quaternion.Identity, rel));

        // LLEyeMotion::adjustEyeTarget (no jitter)
        var eyeAt = point / dist;
        var eyeLeft = Vector3.Cross(up, eyeAt);
        if (eyeLeft.LengthSquared() < 1e-6f) return;
        var eyeRot = Quaternion.Inverse(InRoot(headJ)) * Basis(eyeAt, Vector3.Normalize(eyeLeft));
        eyeRot = Constrain(NoRoll(eyeRot), EyeLimit);
        float iod = Vector3.Distance(pose.WorldPos[eyeL], pose.WorldPos[eyeR]);
        float vergence = -MathF.Atan2(iod / 2, dist) + 4f * MathF.PI / 180;   // 4 degrees: the fovea's offset from the pupil
        var vq = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, vergence);
        _qEyeL = KeyframeMotion.Nlerp(w, Quaternion.Identity, eyeRot * vq);
        _qEyeR = KeyframeMotion.Nlerp(w, Quaternion.Identity, eyeRot * Quaternion.Conjugate(vq));
    }

    /// <summary>LLSmoothInterpolation::getInterpolant: the share of the way to go this frame for a half-life.</summary>
    static float Interpolant(float halfLife, float dt) => 1 - MathF.Pow(2, -dt / halfLife);

    /// <summary>LLQuaternion(x_axis, y_axis, z_axis): the rotation taking X to at and Y to left (orthonormal here).</summary>
    static Quaternion Basis(Vector3 at, Vector3 left)
    {
        var up = Vector3.Cross(at, left);
        var m = new Matrix4x4(at.X, at.Y, at.Z, 0, left.X, left.Y, left.Z, 0, up.X, up.Y, up.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>LLQuaternion::constrain: no more than this angle, about the same axis.</summary>
    static Quaternion Constrain(Quaternion q, float radians)
    {
        float cosLim = MathF.Cos(radians / 2), sinLim = MathF.Sin(radians / 2);
        if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        if (q.W < cosLim)
        {
            float axis = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
            float k = axis > 0 ? sinLim / axis : 0;
            q = new Quaternion(q.X * k, q.Y * k, q.Z * k, cosLim);
        }
        return q;
    }

    /// <summary>LLQuaternion::getEulerAngles then setQuat(0, pitch, yaw): the roll taken out.</summary>
    static Quaternion NoRoll(Quaternion q)
    {
        float sx = 2 * (q.X * q.W - q.Y * q.Z), sy = 2 * (q.Y * q.W + q.X * q.Z);
        float ys = q.W * q.W - q.Y * q.Y, xz = q.X * q.X - q.Z * q.Z;
        float cx = ys - xz, cy = MathF.Sqrt(sx * sx + cx * cx);
        float pitch, yaw;
        if (cy > 0.000436f) { pitch = MathF.Atan2(sy, cy); yaw = MathF.Atan2(2 * (q.Z * q.W - q.X * q.Y), ys + xz); }
        else if (sy > 0) { pitch = MathF.PI / 2; yaw = 2 * MathF.Atan2(q.Z + q.X, q.W + q.Y); }
        else { pitch = -MathF.PI / 2; yaw = 2 * MathF.Atan2(q.Z - q.X, q.W - q.Y); }
        float sY = MathF.Sin(pitch / 2), cY = MathF.Cos(pitch / 2), sZ = MathF.Sin(yaw / 2), cZ = MathF.Cos(yaw / 2);
        return new Quaternion(sY * sZ, sY * cZ, cY * sZ, cY * cZ);
    }

    /// <summary>A keyframe motion's blend weight t seconds after activation: ease in, then (non-looping) the viewer
    /// stops it at duration - ease out and fades it over ease out.</summary>
    static float MotionWeight(KeyframeMotion m, float t, out bool finished)
    {
        finished = false;
        float w = m.EaseIn > 0 ? CubicStep(t / m.EaseIn) : 1;
        if (m.Loop) return w;
        float stop = MathF.Max(m.Duration - m.EaseOut, 0);
        if (t <= stop) return w;
        float residual = m.EaseIn > 0 ? CubicStep(stop / m.EaseIn) : 1;
        float u = m.EaseOut > 0 ? 1 - (t - stop) / m.EaseOut : 0;
        if (u <= 0) { finished = true; return 0; }
        return residual * CubicStep(u);
    }

    // LLEmote: ease in 0.3 s, hold, viewer stop at 1.4 s, ease out 1.4 s
    static float EmoteWeight(float t)
    {
        const float fadeIn = 0.3f, inTime = 1.1f, fadeOut = 1.4f;
        float stop = fadeIn + inTime;
        if (t <= stop) return CubicStep(t / fadeIn);
        float u = 1 - (t - stop) / fadeOut;
        return u <= 0 ? 0 : CubicStep(u);
    }

    static float CubicStep(float x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
}

/// <summary>Animation ids the viewer treats specially (llanimationstates.cpp, llvoavatar.cpp).</summary>
public static class Anim
{
    public static readonly UUID Stand = new("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f");
    public static readonly UUID Walk = new("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0");
    public static readonly UUID WalkNew = new("33339176-7ddc-9397-94a4-bf3403cbc8f5");
    public static readonly UUID FemaleWalkNew = new("d60c41d2-7c24-7074-d3fa-6101cea22a51");
    public static readonly UUID Run = new("05ddbff8-aaa9-92a1-2b74-8fe77a29b445");
    public static readonly UUID RunNew = new("1ab1b236-cd08-21e6-0cbc-0d923fc6eca2");
    public static readonly UUID FemaleRunNew = new("85995026-eade-5d78-d364-94a64512cb66");
    public static readonly UUID Sit = new("1a5fe8ac-a804-8a5d-7cbd-56bd83184568");
    public static readonly UUID SitFemale = new("b1709c8d-ecd3-54a1-4f28-d55ac0840782");

    /// <summary>The motions the viewer registers as LLKeyframeStandMotion (llvoavatar.cpp initInstance): stand, stand 1-4
    /// and crouch, whose feet it puts on the ground.</summary>
    public static readonly HashSet<UUID> StandIk = new[]
    {
        "2408fe9e-df1d-1d7d-f4ff-1384fa7b350f", "15468e00-3400-bb66-cecc-646d7c14458e", "370f3a20-6ca6-9971-848c-9a01bc42ae3c",
        "42b46214-4b44-79ae-deb8-0df61424ff4b", "f22fed8b-a5ed-2c93-64d5-bdd8b93c889f", "201f3fdf-cb1f-dbec-201f-7333e328ae7c",
    }.Select(s => new UUID(s)).ToHashSet();

    /// <summary>Motions the viewer runs itself (and DO_NOT_DISTURB's null motion): never fetched as assets.</summary>
    public static readonly HashSet<UUID> Procedural = new[]
    {
        "9aa8b0a6-0c6f-9518-c7c3-4f41f2c001ad", "4c5a103e-b830-2f1c-16bc-224aa0ad5bc8", "2a8eba1d-a7f8-5596-d44a-b4977bf8c8bb",
        "5c780ea8-1cd1-c463-a128-48c023f6fbea", "db95561f-f1b0-9f9a-7224-b12f71af126e", "ce986325-0ba7-6e6e-cc24-b17c4b795578",
        "e6e8d1dd-e643-fff7-b238-c6b4b056a68d", "0c5dd2a2-514d-8893-d44d-05beffad208b", "0e4896cb-fba4-926c-f355-8720189d5b55",
        "829bc85b-02fc-ec41-be2e-74cc6dd7215d", "7360e029-3cb8-ebc4-863e-212df440d987", "efcf670c-2d18-8128-973a-034ebc806b67",
    }.Select(s => new UUID(s)).ToHashSet();

    public static readonly string[] HandPoses =
    {
        "", "Hands_Relaxed", "Hands_Point", "Hands_Fist", "Hands_Relaxed_L", "Hands_Point_L", "Hands_Fist_L",
        "Hands_Relaxed_R", "Hands_Point_R", "Hands_Fist_R", "Hands_Salute_R", "Hands_Typing", "Hands_Peace_R", "Hands_Spread_R",
    };

    /// <summary>The EXPRESS_* animations (LLEmote): their visual param is named after the animation state.</summary>
    public static readonly Dictionary<UUID, string> Expressions = new (string Id, string Name)[]
    {
        ("aa2df84d-cf8f-7218-527b-424a52de766e", "express_afraid_emote"), ("1a03b575-9634-b62a-5767-3a679e81f4de", "express_anger_emote"),
        ("214aa6c1-ba6a-4578-f27c-ce7688f61d0d", "express_bored_emote"), ("d535471b-85bf-3b4d-a542-93bea4f59d33", "express_cry_emote"),
        ("d4416ff1-09d3-300f-4183-1b68a19b9fc1", "express_disdain"), ("0b8c8211-d78c-33e8-fa28-c51a9594e424", "express_embarrassed_emote"),
        ("fee3df48-fa3d-1015-1e26-a205810e3001", "express_frown"), ("1e8d90cc-a84e-e135-884c-7c82c8b03a14", "express_kiss"),
        ("62570842-0950-96f8-341c-809e65110823", "express_laugh_emote"), ("d63bc1f9-fc81-9625-a0c6-007176d82eb7", "express_open_mouth"),
        ("f76cda94-41d4-a229-2872-e0296e58afe1", "express_repulsed_emote"), ("eb6ebfb2-a4b3-a19c-d388-4dd5c03823f7", "express_sad_emote"),
        ("a351b1bc-cc94-aac2-7bea-a7e6ebad15ef", "express_shrug_emote"), ("b7c7c833-e3d3-c4e3-9fc0-131237446312", "express_smile"),
        ("728646d9-cc79-08b2-32d6-937f0a835c24", "express_surprise_emote"), ("835965c6-7f2f-bda2-5deb-2478737f91bf", "express_tongue_out"),
        ("b92ec1a5-e7ce-a76b-2b05-bcdb9311417e", "express_toothsmile"), ("da020525-4d94-59d6-23d7-81fdebf33148", "express_wink_emote"),
        ("9c05e5c7-6f07-6ca4-ed5a-b230390c3950", "express_worry_emote"),
    }.ToDictionary(e => new UUID(e.Id), e => e.Name);

    public static readonly Dictionary<string, string> ExpressionByName = Expressions.Values.ToDictionary(n => n, n => n);
}
