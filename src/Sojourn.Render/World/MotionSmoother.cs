using System.Diagnostics;
using LibreMetaverse;

namespace Sojourn.Render.World;

/// <summary>
/// Where to draw moving avatars between the region's updates. Each update gives a trajectory, its position moving on
/// with its velocity (dead reckoning, for as long as the viewer's LLViewerObject extrapolates: fully for 2 s, phased
/// out over the next second), predicted to the moment the frame will be shown. A new update doesn't snap or blend the
/// drawing onto the new trajectory: what was drawn (position and velocity), less the new trajectory, at the display
/// time of the first frame that shows the update, decays critically damped (<see cref="Settle"/> a second), as moving
/// prims do (MotionSmoother.Prims.cs). So the drawn position and its speed stay continuous and an update's few
/// centimetres of disagreement become a small, slow correction instead of a speed change. Jumps (teleports, crossings,
/// sitting) snap. Where no region lies beyond a region's edge the drawing doesn't run on past it (the viewer's
/// clipToVisibleRegions; the region bounces an avatar back a metre inside), see <see cref="Edge"/>.
/// One per renderer. Each frame's answer is computed once per avatar, so the camera (which asks first for the
/// person's own avatar) and the drawing agree exactly.
/// The 0.18 s smoothstep blend this replaced (SOJOURN_AVATAR_BLEND=1 brings it back for comparing) made the headset's
/// server movement wobble: replayed over the own avatar's updates from a main-grid sitting (2026-10-28, ~250 s of
/// running), the view's acceleration was median 9.3, p95 50 m/s^2 with 18 backward frames; this way 2.6, 20 and 4
/// (Claude Opus 5.5, 2026-10-28; docs/vr.md > The rig > Movement "server").
/// </summary>
public sealed partial class MotionSmoother
{
    sealed class Track
    {
        public Vector3 Raw, RawVel, Shown, ShownVel;
        public long RawAt, Frame, Seen;
        // what was drawn as the update was first shown, less its trajectory then, and that display time: decays away
        public Vector3 Err, ErrVel;
        public long ErrAt;
        // (the blend, SOJOURN_AVATAR_BLEND=1)
        public Vector3 From, FromVel;
        public long ShownAt, BlendAt;
        public ulong Region;
    }

    readonly Dictionary<UUID, Track> _tracks = new();
    long _frame, _frameAt, _lastSweep;
    float _dt;

    /// <summary>SOJOURN_MOTION_LEGACY=1: the old smoothing (easing toward each dead-reckoned position at 18/s) and no
    /// predicted stick walk in the headset, for comparing the two (tools/perfrun.py, docs/vr.md).</summary>
    public static readonly bool Legacy = System.Environment.GetEnvironmentVariable("SOJOURN_MOTION_LEGACY") == "1";

    /// <summary>SOJOURN_AVATAR_BLEND=1: each update blended in over <see cref="BlendSeconds"/> (smoothstep), as before
    /// 2026-10-28, for comparing.</summary>
    static readonly bool Blend = System.Environment.GetEnvironmentVariable("SOJOURN_AVATAR_BLEND") == "1";

    /// <summary>How long a correction takes to blend in, with SOJOURN_AVATAR_BLEND=1.</summary>
    public float BlendSeconds { get; set; } = 0.18f;

    /// <summary>How fast a difference between what was drawn and an update decays (1/s; critically damped, ~85% gone
    /// in half a second), the prims' rate. Replayed over the main-grid sitting, 4 was a little smoother still (p95
    /// 16 m/s^2) but drew the avatar further off the region's path on turns (p95 75 cm against 52).</summary>
    const float Settle = 6f;

    /// <summary>How far inside an edge with no region beyond it the region puts an avatar that walks out over it (the
    /// main grid, 2026-10-28: 1.000 m each time, keeping its velocity).</summary>
    const float EdgeInset = 1f;

    /// <summary>Whether a known region (one the session has a circuit to) lies at a point, given in a region's own
    /// coordinates (it may be outside it). Null: every edge is open.</summary>
    public Func<ulong, Vector2, bool>? RegionAt { get; set; }

    /// <summary>For replaying logged updates offline: the clock (Stopwatch ticks) the avatars' tracks read.</summary>
    public Func<long>? Clock { get; set; }
    long Now() => Clock?.Invoke() ?? Stopwatch.GetTimestamp();

    /// <summary>Start a frame. <paramref name="displayAt"/>: when it will be on screen (Stopwatch ticks; a headset's
    /// predicted display time), so positions are dead-reckoned to that moment; null = now.</summary>
    public void BeginFrame(long? displayAt = null)
    {
        long now = Now(), at = displayAt ?? now;
        _dt = _frameAt == 0 ? 0 : Math.Clamp((float)((at - _frameAt) / (double)Stopwatch.Frequency), 0f, 0.1f);
        _frameAt = at;
        _frame++;
        if (now - _lastSweep > 10 * Stopwatch.Frequency)
        {
            _lastSweep = now;
            foreach (var id in _tracks.Where(kv => now - kv.Value.Seen > 30 * Stopwatch.Frequency).Select(kv => kv.Key).ToList()) _tracks.Remove(id);
            SweepPrims(now);
        }
    }

    /// <summary>Drop an avatar's track: it's drawn afresh where it's next reported, with nothing blended from before
    /// (the person's own avatar after a teleport to another region, VrPilot's arrival).</summary>
    public void Forget(UUID id) => _tracks.Remove(id);

    /// <summary>When the frame being drawn will be shown (Stopwatch ticks).</summary>
    public long FrameAt => _frameAt;

    /// <summary>How a position in one region's frame reads in a neighbour's (null: not a neighbour, or unknown).</summary>
    static Vector3? CrossingOffset(ulong from, ulong to)
    {
        if (from == 0 || to == 0) return null;
        Utils.LongToUInts(from, out uint fx, out uint fy);
        Utils.LongToUInts(to, out uint tx, out uint ty);
        var off = new Vector3((float)((long)fx - tx), (float)((long)fy - ty), 0);
        return off.Length() is > 0 and <= 2048 ? off : null;
    }

    static float Reach(float age) => age <= 2f ? age : age <= 3f ? 2f + (age - 2f) - (age - 2f) * (age - 2f) / 2f : 2.5f;
    static float DReach(float age) => age <= 2f ? 1f : age <= 3f ? 3f - age : 0f;

    /// <summary>The position to draw an avatar at this frame (region frame), from its latest reported position and
    /// velocity. <paramref name="arrivedAt"/>: when the update came off the network (Stopwatch ticks), if known (the
    /// own avatar's, SelfUpdateGuard); otherwise it's dated by the first frame that sees it, up to a frame late.
    /// <paramref name="snap"/>: drawn on its new trajectory at once, even this frame (a jump the caller has judged).</summary>
    public Vector3 Position(UUID id, ulong region, Vector3 raw, Vector3 velocity, float regionSize = 256f, long? arrivedAt = null, bool snap = false)
    {
        long now = Now();
        if (snap) _tracks.Remove(id);
        if (!_tracks.TryGetValue(id, out var t))
            _tracks[id] = t = new Track { Raw = raw, RawVel = velocity, From = raw, FromVel = velocity, Shown = raw, ShownVel = velocity, RawAt = now, Region = region, Frame = -1 };
        t.Seen = now;
        if (t.Frame == _frame && t.Region == region) return t.Shown;
        if (t.Region != region && t.Frame >= 0 && CrossingOffset(t.Region, region) is { } off)
        {
            // a crossing into a neighbour: the same track in the new region's coordinates, so it carries on instead of
            // snapping (OpenSim hands a walking avatar over ~1 m off where it was heading: a snap, behind a blink in
            // the headset's server movement, 2026-10-27)
            t.Raw += off; t.From += off; t.Shown += off;
            t.Region = region;
        }
        bool fresh = t.Region != region || t.Frame < 0;
        if (Blend || Legacy) return Blended(t, region, raw, velocity, now, fresh);
        if (fresh || raw != t.Raw || velocity != t.RawVel)
        {
            // a new trajectory: what's drawn at this frame's display time, less where the new one has it then, decays
            // from then (from the update's arrival it jolted at the first frame shown: frames are drawn ~50 ms ahead)
            var (sp, sv) = fresh ? (raw, velocity) : Evaluate(t, regionSize, _frameAt);
            t.Raw = raw;
            t.RawVel = velocity;
            t.RawAt = arrivedAt is { } a && a <= now && now - a < Stopwatch.Frequency / 4 ? a : now;
            var (bp, bv) = Base(t, regionSize, _frameAt);
            (t.Err, t.ErrVel) = fresh || Vector3.Distance(sp, bp) > 6f ? (Vector3.Zero, Vector3.Zero) : (sp - bp, sv - bv);
            t.ErrAt = _frameAt;
        }
        (t.Shown, t.ShownVel) = Evaluate(t, regionSize, _frameAt);
        t.ShownAt = _frameAt;
        t.Region = region;
        t.Frame = _frame;
        return t.Shown;
    }

    /// <summary>The update's trajectory at display time <paramref name="at"/>: dead-reckoned, kept off edges with no
    /// region beyond.</summary>
    (Vector3 Pos, Vector3 Vel) Base(Track t, float regionSize, long at)
    {
        float age = MathF.Max(0f, (float)((at - t.RawAt) / (double)Stopwatch.Frequency));
        var pos = t.Raw + t.RawVel * Reach(age);
        var vel = t.RawVel * DReach(age);
        if (RegionAt != null) Edge(t, regionSize, ref pos, ref vel);
        return (pos, vel);
    }

    (Vector3 Pos, Vector3 Vel) Evaluate(Track t, float regionSize, long at)
    {
        var (pos, vel) = Base(t, regionSize, at);
        if (t.Err == Vector3.Zero && t.ErrVel == Vector3.Zero) return (pos, vel);
        float age = MathF.Max(0f, (float)((at - t.ErrAt) / (double)Stopwatch.Frequency));
        if (age * Settle > 9f) { t.Err = t.ErrVel = Vector3.Zero; return (pos, vel); }
        // e(t) = (e + (e' + w e) t) exp(-w t)
        float w = Settle, k = MathF.Exp(-w * age);
        var c = t.ErrVel + w * t.Err;
        return (pos + (t.Err + c * age) * k, vel + (t.ErrVel - w * c * age) * k);
    }

    /// <summary>
    /// An edge with no known region beyond it (LLViewerObject::interpolateLinearMotion clips its prediction to the
    /// regions it knows and stops there). The region lets an avatar walk on out over such an edge for a moment, then puts
    /// it back 1 m inside, still moving: at an island's edge on the main grid (2026-10-28) the drawing ran ~1.4 m past
    /// the edge and was pulled back 2-2.4 m every ~0.45 s while the person held the stick, and the headset's view, riding
    /// it in server movement, cut back each time (12 cuts in 8 s). So moving outward, an avatar is drawn no further out
    /// than the metre inside where the region will put it, and otherwise not past the edge.
    /// </summary>
    void Edge(Track t, float size, ref Vector3 pos, ref Vector3 vel)
    {
        const float Out = 0.1f;   // m/s: moving outward
        var p = new Vector2(Math.Clamp(pos.X, 0.5f, size - 0.5f), Math.Clamp(pos.Y, 0.5f, size - 0.5f));
        bool Open(float x, float y) => RegionAt!(t.Region, new Vector2(x, y));
        // (the region's own word far outside is a crossing still in the old coordinates, not an edge)
        if (pos.X < EdgeInset && t.Raw.X > -8 && !Open(-0.5f, p.Y))
        {
            if (t.RawVel.X < -Out) { pos.X = EdgeInset; vel.X = 0; }
            else if (pos.X < 0) { pos.X = 0; vel.X = MathF.Max(vel.X, 0); }
        }
        else if (pos.X > size - EdgeInset && t.Raw.X < size + 8 && !Open(size + 0.5f, p.Y))
        {
            if (t.RawVel.X > Out) { pos.X = size - EdgeInset; vel.X = 0; }
            else if (pos.X > size) { pos.X = size; vel.X = MathF.Min(vel.X, 0); }
        }
        if (pos.Y < EdgeInset && t.Raw.Y > -8 && !Open(p.X, -0.5f))
        {
            if (t.RawVel.Y < -Out) { pos.Y = EdgeInset; vel.Y = 0; }
            else if (pos.Y < 0) { pos.Y = 0; vel.Y = MathF.Max(vel.Y, 0); }
        }
        else if (pos.Y > size - EdgeInset && t.Raw.Y < size + 8 && !Open(p.X, size + 0.5f))
        {
            if (t.RawVel.Y > Out) { pos.Y = size - EdgeInset; vel.Y = 0; }
            else if (pos.Y > size) { pos.Y = size; vel.Y = MathF.Min(vel.Y, 0); }
        }
    }

    /// <summary>The smoothing before 2026-10-28 (SOJOURN_AVATAR_BLEND=1: a smoothstep blend from the old trajectory to
    /// the new over <see cref="BlendSeconds"/>) and the one before that (SOJOURN_MOTION_LEGACY=1), for comparing.</summary>
    Vector3 Blended(Track t, ulong region, Vector3 raw, Vector3 velocity, long now, bool fresh)
    {
        float SinceUpdate() => MathF.Max(0f, (float)((_frameAt - t.RawAt) / (double)Stopwatch.Frequency));
        if (raw != t.Raw || velocity != t.RawVel)
        {
            t.From = t.Shown + t.ShownVel * (float)((now - t.ShownAt) / (double)Stopwatch.Frequency);
            t.FromVel = t.ShownVel;
            t.Raw = raw;
            t.RawVel = velocity;
            t.RawAt = now;
            t.BlendAt = _frameAt;
        }
        float age = SinceUpdate();
        var target = raw + velocity * Reach(age);
        float dReach = DReach(age);
        Vector3 shown, shownVel;
        if (fresh || Vector3.Distance(t.Shown, target) > 6f)
        {
            shown = target;
            shownVel = velocity * dReach;
            t.From = raw;
            t.FromVel = velocity;
        }
        else if (Legacy)
        {
            shown = t.Shown + (target - t.Shown) * (1f - MathF.Exp(-_dt * 18f));
            shownVel = velocity;
        }
        else
        {
            float x = Math.Clamp((float)((_frameAt - t.BlendAt) / (double)Stopwatch.Frequency) / BlendSeconds, 0f, 1f);
            float a = x * x * (3 - 2 * x), da = x < 1f ? 6 * x * (1 - x) / BlendSeconds : 0f;
            var old = t.From + t.FromVel * Reach(age);
            shown = old + (target - old) * a;
            shownVel = t.FromVel * dReach * (1 - a) + velocity * dReach * a + (target - old) * da;
        }
        t.ShownVel = shownVel;
        t.Shown = shown;
        t.ShownAt = _frameAt;
        t.Region = region;
        t.Frame = _frame;
        return t.Shown;
    }
}
