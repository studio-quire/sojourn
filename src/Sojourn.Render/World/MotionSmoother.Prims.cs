using System.Diagnostics;
using LibreMetaverse;

namespace Sojourn.Render.World;

/// <summary>
/// Moving prims between the region's updates (vehicles, keyframed motion, physical things, llTargetOmega spin), as the
/// viewer's LLViewerObject::idleUpdate moves them, with corrections smoothed:
/// - Each update is a trajectory: its position carried on by its velocity and acceleration (interpolateLinearMotion:
///   (v + (dt - PHYSICS_TIMESTEP)/2 a) dt a step, so the velocity counts as the last physics step's average) and its
///   rotation turned by its angular velocity about the parent's axes (applyAngularVelocity), in the region's time (time
///   dilation). The region sends nothing while an object keeps to that path (a keyframe can run for seconds), so it runs
///   on as long as the region is heard from; only when the region has gone quiet for 2 s does it fade out by 3 s, as the
///   viewer's does when the circuit stops. Past the region's edge it runs on for at most a second (a crossing).
/// - A rotation the region didn't change keeps the spin gathered so far (llTargetOmega on a non-physical object is the
///   viewer's alone, so a colour change doesn't snap a wheel back); a new rotation, or a physical object's new angular
///   velocity, starts it afresh (LLViewerObject::processUpdateMessage's resetRot / resetRotTime).
/// - Where the viewer snaps to each update, here the difference between what was drawn (position, velocity, rotation)
///   and the new trajectory decays critically damped (<see cref="PrimSettle"/> a second), so position and speed stay
///   continuous and riding a vehicle the view and the world never step. A smoothstep blend over 0.18 s, as avatars have
///   (it was tried first), turned each update's few centimetres of disagreement into a speed change: replayed over a
///   logged ride (OpenSim's keyframed motion, updates every ~0.1 s, each 2-5 cm off its own velocity) the view's
///   acceleration was p95 73 m/s^2 against 15 this way (snapping, as the viewer does: 145). A sudden stop overshoots
///   less too (6% of the speed in metres against 9%). Jumps over 6 m snap.
/// The answer is worked out once a frame per prim, so the scene's placement, a seated avatar's and the headset's
/// seated view (VrPilot) agree exactly. Tracks exist only for prims that move or have just stopped.
/// </summary>
public sealed partial class MotionSmoother
{
    sealed class PrimTrack
    {
        // the region's last word (parent frame), and when it was seen
        public Vector3 Pos, Vel, Acc, Omega;
        public Quaternion Rot, RotIn;   // (RotIn as it came, to tell a new update by)
        public bool Physical;
        public long At;
        public Quaternion Spin0 = Quaternion.Identity;   // the spin gathered before At (kept across updates, see above)
        // what was drawn as the update came, less the update's trajectory then: decays away (critically damped)
        public Vector3 Err, ErrVel, ErrAng, ErrAngVel;   // (the rotation's as a rotation vector and its rate, parent frame)
        public long ErrAt;                               // the display time it was taken at, the first frame showing the update
        public long OutAt;                               // when the trajectory left the region (0: inside)
        // this frame's answer
        public Vector3 ShownPos, ShownVel;
        public Quaternion ShownRot = Quaternion.Identity;
        public long Frame = -1, Seen;
        public ulong Region;
    }

    readonly Dictionary<UUID, PrimTrack> _prims = new();
    readonly Dictionary<ulong, long> _heard = new();
    // the tracks are asked for from the scene workers at once (a view's draw list is built in parts: World/DrawPlan.cs),
    // a linkset's root by children in different parts: one at a time
    readonly object _primLock = new();

    /// <summary>SOJOURN_PRIM_MOTION=0: prims drawn where the last update put them (no dead reckoning), for comparing.</summary>
    public static readonly bool PrimMotionOff = System.Environment.GetEnvironmentVariable("SOJOURN_PRIM_MOTION") == "0";

    const float PhysicsTimestep = 1f / 45f;
    /// <summary>How fast a difference between what was drawn and an update decays (1/s; critically damped, ~85% gone
    /// in half a second).</summary>
    const float PrimSettle = 6f;
    static readonly bool PrimLog = System.Environment.GetEnvironmentVariable("SOJOURN_PRIM_LOG") == "1";

    /// <summary>The region reported changes to its objects just now (the scene's change queue had some): it's alive.</summary>
    public void Heard(ulong region) { lock (_primLock) _heard[region] = Stopwatch.GetTimestamp(); }

    static bool HasMotion(Primitive p) => p.Velocity != LibreMetaverse.Vector3.Zero || p.Acceleration != LibreMetaverse.Vector3.Zero ||
                                          p.AngularVelocity != LibreMetaverse.Vector3.Zero;

    /// <summary>Whether a prim's pose this frame may differ from its last update's (it moves, or has a track): a quick
    /// test before asking <see cref="Prim"/>.</summary>
    public bool Tracks(Primitive p)
    {
        if (PrimMotionOff) return false;
        if (HasMotion(p)) return true;
        lock (_primLock) return _prims.ContainsKey(p.ID);
    }

    /// <summary>Whether a prim needs placing every frame: it moves, or it has just stopped and is still blending in.</summary>
    public bool PrimMoving(Primitive p)
    {
        lock (_primLock) return PrimMovingLocked(p);
    }

    bool PrimMovingLocked(Primitive p)
    {
        if (PrimMotionOff) return false;
        if (HasMotion(p)) return true;
        if (!_prims.TryGetValue(p.ID, out var t)) return false;
        // stopped: done once what was drawn has settled onto it
        return t.Vel != Vector3.Zero || t.Acc != Vector3.Zero || t.Omega != Vector3.Zero || V(p.Position) != t.Pos ||
               Q(p.Rotation) != t.RotIn || !Settled(t, _frameAt);
    }

    /// <summary>Where to draw a prim this frame, in its parent's frame (the region's for a root): its last update carried
    /// on to the frame's display time. A prim that has never moved is where the region put it.</summary>
    public (Vector3 Pos, Quaternion Rot) Prim(Primitive p, ulong region, float dilation = 1f, float regionSize = 256f)
    {
        lock (_primLock) return PrimLocked(p, region, dilation, regionSize);
    }

    (Vector3 Pos, Quaternion Rot) PrimLocked(Primitive p, ulong region, float dilation, float regionSize)
    {
        var rawPos = V(p.Position);
        var rotIn = Q(p.Rotation);
        var rawRot = Quaternion.Normalize(rotIn);
        if (PrimMotionOff) return (rawPos, rawRot);
        bool moving = HasMotion(p);
        if (!_prims.TryGetValue(p.ID, out var t))
        {
            if (!moving) return (rawPos, rawRot);
            _prims[p.ID] = t = new PrimTrack { Frame = -1 };
        }
        long now = Stopwatch.GetTimestamp();
        t.Seen = now;
        if (t.Frame == _frame && t.Region == region) return (t.ShownPos, t.ShownRot);

        var vel = V(p.Velocity);
        var acc = V(p.Acceleration);
        var omega = V(p.AngularVelocity);
        bool physical = (p.Flags & PrimFlags.Physics) != 0;
        bool fresh = t.Frame < 0 || t.Region != region;
        if (fresh || rawPos != t.Pos || rotIn != t.RotIn || vel != t.Vel || acc != t.Acc || omega != t.Omega)
        {
            // a new trajectory, seen now: what's drawn at this frame's display time, less where the new one has it then,
            // is where its difference decays from (taken at the update's arrival, the first frame shown ~50 ms on in the
            // headset jolted by the decay's curvature over those 50 ms: avatars, replayed, p99 acceleration 143 -> 40)
            Vector3 shownPos = rawPos, shownVel = vel, shownOmega = Vector3.Zero;
            var shownRot = rawRot;
            if (!fresh)
            {
                (shownPos, shownVel, shownRot) = Evaluate(t, _frameAt, dilation, regionSize);
                // how fast what was drawn was turning (OpenSim's keyframed motion turns with no angular velocity given, in
                // steps: carrying the turn on keeps the view's turning rate continuous through each)
                long h = Stopwatch.Frequency / 100;
                var (_, _, before) = Evaluate(t, _frameAt - h, dilation, regionSize);
                shownOmega = Log(Quaternion.Concatenate(Quaternion.Inverse(before), shownRot)) * 100f;
                // the spin gathered so far stays unless the region turned it (or a physical object's spin changed)
                bool reset = rotIn != t.RotIn || (physical && omega != t.Omega);
                t.Spin0 = reset ? Quaternion.Identity : Quaternion.Normalize(Quaternion.Concatenate(t.Spin0, Spin(t.Omega, Tau(t, now, dilation))));
            }
            t.Pos = rawPos;
            t.Rot = rawRot;
            t.RotIn = rotIn;
            t.Vel = vel;
            t.Acc = acc;
            t.Omega = omega;
            t.Physical = physical;
            t.At = now;
            t.OutAt = 0;
            t.Region = region;
            // the update's own trajectory at that display time (its spin kept)
            (t.Err, t.ErrVel, t.ErrAng, t.ErrAngVel) = (Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);
            t.ErrAt = _frameAt;
            var (bp, bv, br) = Evaluate(t, _frameAt, dilation, regionSize);
            if (!fresh && Vector3.Distance(shownPos, bp) <= 6f)
                (t.Err, t.ErrVel, t.ErrAng, t.ErrAngVel) = (shownPos - bp, shownVel - bv,
                    Log(Quaternion.Concatenate(Quaternion.Inverse(br), shownRot)), shownOmega - omega * Math.Clamp(dilation <= 0 ? 1f : dilation, 0.05f, 1f));
        }
        var (pos, v, rot) = Evaluate(t, _frameAt, dilation, regionSize);
        t.ShownPos = pos;
        t.ShownVel = v;
        t.ShownRot = rot;
        t.Region = region;
        t.Frame = _frame;
        // stopped and blended in: nothing left to carry on (unless it keeps a spin the region never saw)
        if (!moving && !PrimMovingLocked(p) && t.Spin0 == Quaternion.Identity) _prims.Remove(p.ID);
        return (pos, rot);
    }

    /// <summary>A prim's pose in the region's frame this frame: a root's own, a child's carried by its root's (both
    /// moved as above). False if its root isn't known.</summary>
    public bool PrimWorld(Func<uint, Primitive?> lookup, Primitive p, ulong region, out Vector3 pos, out Quaternion rot,
        float dilation = 1f, float regionSize = 256f)
    {
        (pos, rot) = Prim(p, region, dilation, regionSize);
        if (p.ParentID == 0) return true;
        if (lookup(p.ParentID) is not { ParentID: 0 } root) return false;
        var (rp, rr) = Prim(root, region, dilation, regionSize);
        pos = rp + Vector3.Transform(pos, rr);
        rot = Quaternion.Normalize(Quaternion.Concatenate(rot, rr));
        return true;
    }

    /// <summary>A seated avatar's root (pelvis) and rotation this frame, on its seat as the scene draws it (the person's
    /// own view rides this: VrPilot, Pilot). Null if it isn't seated or the seat isn't known.</summary>
    public (Vector3 Pos, Quaternion Rot)? Seated(Func<uint, Primitive?> lookup, Avatar av, ulong region, float dilation = 1f, float regionSize = 256f)
    {
        if (av.ParentID == 0 || lookup(av.ParentID) is not { } seat) return null;
        if (!PrimWorld(lookup, seat, region, out var sp, out var sr, dilation, regionSize)) return null;
        return Avatars.SeatedOn(sp, sr, av);
    }

    float Age(PrimTrack t, long at) => MathF.Max(0f, (float)((at - t.At) / (double)Stopwatch.Frequency));
    /// <summary>How long the difference has been decaying at <paramref name="at"/>.</summary>
    static float ErrAge(PrimTrack t, long at) => MathF.Max(0f, (float)((at - t.ErrAt) / (double)Stopwatch.Frequency));

    /// <summary>The trajectory's own time since the update: the region's (dilated), faded out once the region has gone
    /// quiet (LLViewerObject::interpolateLinearMotion's phase-out, from 2 s to 3 s).</summary>
    float Tau(PrimTrack t, long at, float dilation)
    {
        float age = Age(t, at);
        bool quiet = !_heard.TryGetValue(t.Region, out long heard) || (at - heard) / (double)Stopwatch.Frequency > 2.0;
        if (quiet && _heard.ContainsKey(t.Region)) age = age <= 2f ? age : age <= 3f ? 2f + (age - 2f) - (age - 2f) * (age - 2f) / 2f : 2.5f;
        return age * Math.Clamp(dilation <= 0 ? 1f : dilation, 0.05f, 1f);
    }

    static Quaternion Spin(Vector3 omega, float tau)
    {
        float w = omega.Length();
        if (w < 0.0032f) return Quaternion.Identity;   // the viewer's |omega|^2 > 0.00001
        return Quaternion.CreateFromAxisAngle(omega / w, w * tau);
    }

    /// <summary>What was drawn has come to the update's trajectory (the difference decayed to a few thousandths).</summary>
    bool Settled(PrimTrack t, long at) =>
        (t.Err == Vector3.Zero && t.ErrVel == Vector3.Zero && t.ErrAng == Vector3.Zero && t.ErrAngVel == Vector3.Zero) || ErrAge(t, at) * PrimSettle > 9f;

    /// <summary>Position, velocity and rotation drawn at <paramref name="at"/>: the update's trajectory, plus what was
    /// drawn as it came (less the trajectory then) decaying critically damped: e(t) = (e + (e' + w e) t) exp(-w t).</summary>
    (Vector3 Pos, Vector3 Vel, Quaternion Rot) Evaluate(PrimTrack t, long at, float dilation, float regionSize)
    {
        float tau = Tau(t, at, dilation);
        // a crossing: on for a second past the region's edge, then held (the viewer's sMaxRegionCrossingInterpolationTime)
        var newPos = Path(t.Pos, t.Vel, t.Acc, tau);
        if (newPos.X < 0 || newPos.Y < 0 || newPos.X > regionSize || newPos.Y > regionSize)
        {
            if (t.OutAt == 0) t.OutAt = at;
            float over = (float)((at - t.OutAt) / (double)Stopwatch.Frequency);
            if (over > 1f) { tau = MathF.Max(0f, tau - (over - 1f)); newPos = Path(t.Pos, t.Vel, t.Acc, tau); }
        }
        newPos.Z = MathF.Min(newPos.Z, 4096f);
        var newVel = t.Vel + t.Acc * tau;
        var newRot = Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Concatenate(t.Rot, t.Spin0), Spin(t.Omega, tau)));
        if (Settled(t, at)) return (newPos, newVel, newRot);
        float age = ErrAge(t, at), w = PrimSettle, k = MathF.Exp(-w * age);
        var c = t.ErrVel + w * t.Err;
        var e = (t.Err + c * age) * k;
        var ev = (t.ErrVel - w * c * age) * k;
        // the rotation's difference (a rotation vector in the parent's frame) the same way
        var ea = (t.ErrAng + (t.ErrAngVel + w * t.ErrAng) * age) * k;
        return (newPos + e, newVel + ev, Quaternion.Normalize(Quaternion.Concatenate(newRot, Exp(ea))));
    }

    /// <summary>A rotation as a rotation vector (axis times angle, the short way round), and back.</summary>
    static Vector3 Log(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0) q = -q;
        var v = new Vector3(q.X, q.Y, q.Z);
        float s = v.Length();
        if (s < 1e-7f) return v * 2f;
        return v / s * (2f * MathF.Atan2(s, q.W));
    }

    static Quaternion Exp(Vector3 r)
    {
        float a = r.Length();
        return a < 1e-7f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(r / a, a);
    }

    static Vector3 Path(Vector3 p, Vector3 v, Vector3 a, float tau) =>
        a == Vector3.Zero ? p + v * tau : p + (v - 0.5f * PhysicsTimestep * a) * tau + 0.5f * a * tau * tau;

    void SweepPrims(long now)
    {
        // (one keeping a spin is asked for only when the region updates it: kept longer)
        lock (_primLock)
            foreach (var id in _prims.Where(kv => now - kv.Value.Seen > (kv.Value.Spin0 == Quaternion.Identity ? 30 : 900) * Stopwatch.Frequency)
                         .Select(kv => kv.Key).ToList()) _prims.Remove(id);
    }

    static Vector3 V(LibreMetaverse.Vector3 v) => new(v.X, v.Y, v.Z);
    static Quaternion Q(LibreMetaverse.Quaternion q) => new(q.X, q.Y, q.Z, q.W);
}
