using LibreMetaverse;
using Sojourn.Render.World;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Control;

/// <summary>
/// The own avatar's local collision model, so the headset's walk meets walls and steps where the region's avatar will
/// (the user's session of 2026-10-26: the view walked through walls the avatar was stopped at, and onto a boat the
/// avatar couldn't step up on). The walk is the client's own (VrPilot.Walk), so it has to know the world's solid shapes
/// itself rather than wait a round trip for the region's word:
/// - Walls: rays along the walk at three heights above the feet (just over a step, the waist, the chest) find the
///   nearest solid surface met from outside (RegionScene.CollideRay: drawn triangles' front faces, phantom and still
///   loading prims left out, other standing avatars as a box). Prims count by their physics shapes as the region knows
///   them (PhysicsShapes asks it for their types ahead of the walk; a mesh's hull or decomposition, a child with
///   physics shape none left out). The head stops <see cref="Radius"/> short of it and the rest of the step slides
///   along it, as the avatar's capsule does.
/// - Steps and floors: the surface under the head, read locally on prims as on terrain (VrPilot.GroundUnder), so the
///   view steps up onto a kerb or a deck as the head goes over it, up to <see cref="StepHeight"/>; anything taller is a
///   wall to the low ray.
/// - Where the model and the region disagree, the region wins quickly: when it stops the avatar where the model saw
///   nothing (a mesh whose physics shape is its hull, a doorway it won't fit), the walk is pulled back onto the avatar
///   over a fifth of a second and a wall is remembered there for a few seconds (Refused); when it lets the avatar
///   through something the model called solid (a phantom it didn't know, physics shape none), that prim stops counting
///   (Passable).
/// </summary>
public sealed partial class VrPilot
{
    /// <summary>The renderer's collision ray (FrameRenderer.CollideRay); without it the walk is as before.</summary>
    public Func<ulong, Vector3, Vector3, float, uint, Func<Primitive, CollideAs>, CollideHit?>? Collider { get; set; }

    /// <summary>The prims' physics shape types around the avatar, asked of the region ahead of the walk.</summary>
    PhysicsShapes? _physics;

    /// <summary>How close the head comes to a wall, metres (the avatar's capsule is about this wide around its middle,
    /// and the eyes are near the middle: the view stops where the body does).</summary>
    const float Radius = 0.32f;
    /// <summary>The tallest step the avatar walks up without a jump: learned from what the region does (it climbed a
    /// taller one, or stopped at a lower one), starting at OpenSim's (it climbs 0.3 m and stops at 0.45).</summary>
    float StepHeight = 0.4f;
    /// <summary>A step up to StepHeight plus this is climbed, and the low wall ray runs at that height, so every step is
    /// one or the other.</summary>
    const float StepSlack = 0.03f;
    /// <summary>A surface steeper than this (its normal's height under it) is a wall, not a floor.</summary>
    const float WallNormalZ = 0.6f;

    readonly Dictionary<uint, double> _passable = new();     // prims the region let the avatar through, until when
    readonly List<(Vector3 At, Vector3 Normal, double Until)> _refused = new();   // walls the region stopped the avatar at
    uint _blockedBy;          // the prim the walk last stopped at, where (in the walk's own frame) and facing which way
    Vector3 _blockedAt, _blockedNormal;
    double _blockedSince = -1;
    Vector3 _pullBack;        // what's left of pulling the walk back onto the avatar after a refusal
    Vector3 _pullVel;
    readonly Queue<(double At, Vector3 Raw, Vector3 Dir)> _trail = new();   // the region's positions while walking

    CollideAs Solid(Primitive p)
    {
        if (_passable.TryGetValue(p.LocalID, out var until) && until > _clock.Elapsed.TotalSeconds) return CollideAs.Passable;
        if (((byte)p.PrimData.PathCurve & 0x80) != 0) return CollideAs.Passable;   // flexible prims (their path's flag) are phantom
        var flags = _session.State.PrimFlagsOf(p);
        if (p.ParentID != 0 && _session.Region is { } w && w.Prims.TryGetValue(p.ParentID, out var root))
        {
            if (_passable.TryGetValue(root.LocalID, out var ru) && ru > _clock.Elapsed.TotalSeconds) return CollideAs.Passable;
            flags |= _session.State.PrimFlagsOf(root);
        }
        if ((flags & PrimFlags.Phantom) != 0) return CollideAs.Passable;
        if (PhysicsShapesOff) return CollideAs.Prim;
        _physics ??= new PhysicsShapes(_session) { Say = s => Say?.Invoke(s) };
        return _physics.Of(p) is { } t ? (CollideAs)(byte)t : CollideAs.Unknown;
    }

    /// <summary>SOJOURN_PHYSICS_SHAPES=0: every prim on its drawn triangles, as before physics shapes (for comparing).</summary>
    static readonly bool PhysicsShapesOff = Environment.GetEnvironmentVariable("SOJOURN_PHYSICS_SHAPES") == "0";

    /// <summary>Each frame: the prims around the avatar get their physics shape types asked for.</summary>
    void PhysicsAround(ulong region, Vector3 avatar)
    {
        if (PhysicsShapesOff || Collider == null) return;
        _physics ??= new PhysicsShapes(_session) { Say = s => Say?.Invoke(s) };
        _physics.Around(region, avatar);
    }

    /// <summary>As <see cref="Solid"/>, for what's stood on: prims the region's avatar was found standing under (it
    /// landed lower than their tops, under the head) don't hold the view up for a while, though they stay walls.</summary>
    CollideAs SolidFloor(Primitive p) =>
        _notFloor.TryGetValue(p.LocalID, out var until) && until > _clock.Elapsed.TotalSeconds ? CollideAs.Passable : Solid(p);

    /// <summary>As <see cref="SolidFloor"/>, leaving out prims whose physics shape the region hasn't told us yet (a mesh
    /// plant counted by its hull until then): the walk doesn't climb onto those ahead of the avatar.</summary>
    CollideAs KnownFloor(Primitive p)
    {
        var how = SolidFloor(p);
        return how == CollideAs.Unknown && _physics is { Unsupported: false } ? CollideAs.Passable : how;
    }

    readonly Dictionary<uint, double> _notFloor = new();   // prims the region had the avatar standing under, until when

    /// <summary>For the console: what the model took a prim for (mesh or prim, its physics shape as the region said).</summary>
    string ShapeOf(uint localId)
    {
        if (_session.Region is not { } w || !w.Prims.TryGetValue(localId, out var p)) return "not loaded";
        string kind = p.Sculpt is { Type: SculptType.Mesh } ? "mesh" : p.Sculpt is { Type: not SculptType.None } ? "sculpt" : "prim";
        string type = PhysicsShapesOff ? "physics shapes off" : _physics?.Of(p) is { } t ? $"physics shape {t}" : "physics shape not known yet";
        return $"{kind}, {type}";
    }

    CollideHit? Cast(ulong region, uint self, Vector3 from, Vector3 dir, float length, Func<Primitive, CollideAs>? how = null)
    {
        how ??= Solid;
        if (TimeRays == null) return Collider?.Invoke(region, from, dir, length, self, how);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var hit = Collider?.Invoke(region, from, dir, length, self, how);
        TimeRays[0] += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        TimeRays[1]++;
        return hit;
    }

    static readonly double[]? TimeRays = Environment.GetEnvironmentVariable("SOJOURN_TIME_RAYS") == "1" ? new double[2] : null;
    double _timeRaysAt;

    void ReportRays(double now, long frames)
    {
        if (TimeRays == null || now - _timeRaysAt < 5) return;
        Say?.Invoke($"collision rays: {TimeRays[1] / Math.Max(1, frames):F1} a frame, {TimeRays[0] / Math.Max(1, frames):F3} ms a frame");
        TimeRays[0] = TimeRays[1] = 0;
        _timeRaysAt = now;
    }

    /// <summary>A step of the walk with walls in the way taken out: the head at <paramref name="head"/> (flat), the feet at
    /// <paramref name="feetZ"/>, the body <paramref name="height"/> tall. Moves up to <see cref="Radius"/> short of the
    /// nearest wall along the step, then slides the rest along it (checked once more). Also the region's remembered
    /// refusals, against the walk's own position.</summary>
    Vector3 Sweep(ulong region, uint self, Vector3 head, Vector3 walkAt, float feetZ, float height, Vector3 step, double now, bool slide = true)
    {
        float len = step.Length();
        if (len < 1e-6f || Collider == null) return step;
        var dir = step / len;
        float allowed = len;
        Vector3 wall = default, wallAt = default;
        uint by = 0;
        foreach (float h in new[] { StepHeight + StepSlack, MathF.Min(0.9f, height * 0.5f), MathF.Max(0.6f, height - 0.25f) })
        {
            var from = new Vector3(head.X, head.Y, feetZ + h);
            if (Cast(region, self, from, dir, len + Radius + 0.05f) is not { } hit) continue;
            if (MathF.Abs(hit.Normal.Z) > WallNormalZ) continue;   // a ramp or a slope underfoot: the floor's business
            float free = MathF.Max(0, hit.Distance - Radius);
            // facing it head on, the whole free distance; at a slant, a little more along the wall
            if (free < allowed) { allowed = free; wall = Flat(hit.Normal); by = hit.LocalId; wallAt = Flat(from + dir * hit.Distance); }
        }
        foreach (var (at, n, until) in _refused)
        {
            if (until < now) continue;
            float into = -Vector3.Dot(dir, n);
            if (into <= 0) continue;
            float free = MathF.Max(0, Vector3.Dot(walkAt - at, n)) / into;
            if (free < allowed) { allowed = free; wall = n; by = 0; }
        }
        if (allowed >= len) { if (slide) _blockedBy = 0; return step; }
        if (slide)
        {
            _blockedBy = by;
            if (by != 0) (_blockedAt, _blockedNormal) = (wallAt + (walkAt - head), wall == Vector3.Zero ? -dir : Vector3.Normalize(wall));
        }
        var moved = dir * allowed;
        if (!slide || wall == Vector3.Zero) return moved;
        // the rest along the wall (its horizontal normal)
        wall = Vector3.Normalize(wall);
        var rest = step - moved;
        rest -= wall * Vector3.Dot(rest, wall);
        return moved + Sweep(region, self, head + moved, walkAt + moved, feetZ, height, rest, now, slide: false);
    }

    /// <summary>The highest surface at (x, y) no higher than <paramref name="top"/> and at most <paramref name="depth"/>
    /// below it: a prim's top face (one shallow enough to stand on) or the terrain.</summary>
    float? Support(Simulator sim, uint self, float x, float y, float top, float depth, bool knownOnly = false)
    {
        float? best = null;
        if (TerrainAt(sim, x, y) is { } t && t <= top + 0.02f && t >= top - depth) best = t;
        if (Cast(sim.Handle, self, new Vector3(x, y, top), -Vector3.UnitZ, depth, knownOnly ? KnownFloor : SolidFloor) is { } hit && hit.Normal.Z >= WallNormalZ)
        {
            float z = top - hit.Distance;
            if (best is not { } b || z > b) best = z;
        }
        return best;
    }

    /// <summary>As <see cref="Support"/>, with the prim it is (0: the terrain), other avatars left out.</summary>
    (float Z, uint Id)? FloorAt(Simulator sim, uint self, float x, float y, float top, float depth)
    {
        (float Z, uint Id)? best = null;
        if (TerrainAt(sim, x, y) is { } t && t <= top + 0.02f && t >= top - depth) best = (t, 0);
        if (Cast(sim.Handle, self, new Vector3(x, y, top), -Vector3.UnitZ, depth, SolidFloor) is { Avatar: false } hit && hit.Normal.Z >= WallNormalZ)
        {
            float z = top - hit.Distance;
            if (best is not { } b || z > b.Z) best = (z, hit.LocalId);
        }
        return best;
    }

    // --- the view's feet: never down through a floor ---------------------------------------------------------------

    float _viewFeet;          // where the view's feet were last frame (the floor without the lift)
    float? _onRegion;         // this frame: the region's avatar landed and still under the head, its feet here (GroundUnder)
    float? _walkLevel;        // this frame: walking, the region's avatar level near the head, its feet here
    double _highSince, _highSeen = -1;   // since when the view has been held well over that, and when it last was
    float _steadyZ;           // the region's height for the avatar, and since when it has held there with no velocity
    double _steadySince;
    float _levelZ;            // the same for its height alone (walking)
    double _levelSince;
    float _releasedAt;        // a surface the region has the avatar under, not stood on until then
    double _releasedUntil = -1;

    /// <summary>
    /// The floor as the view will have it (with the lift), kept from going down through any surface the local model
    /// knows (the user's session of 2026-10-27: the view sank into the ground for a moment after every fall, as the
    /// drawn avatar runs on past the floor until the region's word arrives, and the spring carried on after it). Each
    /// frame the view's feet move down, a ray sweeps from where they were to where they'd be, under the head (and under
    /// the avatar while off the model: flying, jumping, falling), so no frame's step, however long, passes a thin floor;
    /// a surface met holds them there. Where the region has its avatar under that surface by more than a drawn shape
    /// could explain (through it, half a metre down, at the same place), the region wins: the view goes on down, and a
    /// prim stops counting for two minutes, as for walls.
    /// </summary>
    float Underfoot(Simulator sim, uint self, Vector3 headFlat, Vector3 avatar, Vector3 raw, float footDepth, float floor, double now)
    {
        float result = HoldUp(sim, self, headFlat, avatar, raw, footDepth, floor, now);
        // the floor jumped over 3 m (Floor) and that stands: a cut of the view
        if (_snapFrom is { } from && MathF.Abs(result - from) > 1f) Cut();
        _snapFrom = null;
        return result;
    }

    float HoldUp(Simulator sim, uint self, Vector3 headFlat, Vector3 avatar, Vector3 raw, float footDepth, float floor, double now)
    {
        float feet = floor - _lift, before = _viewFeet;
        _viewFeet = feet;
        if (_floorSnapped) { _floorSnapped = false; return floor; }   // (the floor was put somewhere new)
        if (feet >= before - 1e-4f || Collider == null) return floor;
        float over = MathF.Max(_hover, 0);   // (the view stands this far over a surface, as on the model)
        float top = before - over + 0.05f, depth = before - feet + 0.07f;
        var stand = FloorAt(sim, self, headFlat.X, headFlat.Y, top, depth);
        if (!_onModel && FloorAt(sim, self, avatar.X, avatar.Y, top, depth) is { } a && (stand is not { } s || a.Z > s.Z)) stand = a;
        if (stand is not { } st || st.Z + over <= feet) return floor;
        if (now < _releasedUntil && st.Z >= _releasedAt - 0.05f) return floor;
        if (_onRegion is { } rf && st.Z + over > rf + 0.03f)
        {
            // the region's avatar has landed and holds still under the head, lower than what holds the view up (the
            // user's session of 2026-10-27, Inarishima: after falls the view rested 0.1-0.5 m over where the avatar
            // stood, on a plant's hull, a mesh whose type wasn't known yet, a hull over a dip): the region is right
            float gap = st.Z + over - rf;
            // (standing a little under its top, the avatar is inside it: the model has it wrong there. Far under, it may
            // just be under a real floor, having fallen off beside it)
            if (st.Id != 0 && gap is > 0.15f and < 1.5f && !(_notFloor.TryGetValue(st.Id, out var nf) && nf > now))
            {
                _notFloor[st.Id] = now + 120;
                Say?.Invoke($"the region has you standing {gap:F2} m under the top of object {st.Id} ({ShapeOf(st.Id)}); not standing on it");
            }
            if (before - rf > 1f)
            {
                // a long way down (it fell off beside what the view stood on): a straight cut, not a fall of the view
                _viewFeet = rf;
                _floor = rf + _lift;
                _floorVel = 0;
                Cut();
                return _floor;
            }
            return floor;
        }
        // walking, held more than a step over the region's avatar while it keeps level near the head, for 0.4 s: no step
        // lead explains that (the session of 2026-10-27: after a jump the view walked 1.1 m over the avatar for 1.6 s,
        // on what it had landed on), so the region's level wins
        if (_walkLevel is { } wl && st.Z + over > wl + StepHeight + 0.1f)
        {
            if (now - _highSeen > 0.15) _highSince = now;   // (held so without a break)
            _highSeen = now;
            if (now - _highSince > 0.4) return floor;
        }
        float rawFeet = raw.Z - footDepth - _hover;
        // (just under it: falling through it. Far under, it may have fallen off beside it and only be under it now)
        if (rawFeet < st.Z - 0.5f && rawFeet > st.Z - 4f && FloorAt(sim, self, raw.X, raw.Y, st.Z + 0.05f, 0.15f) is { } r && MathF.Abs(r.Z - st.Z) < 0.1f)
        {
            _releasedAt = st.Z;
            _releasedUntil = now + 3;
            if (st.Id != 0) _passable[st.Id] = now + 120;
            Say?.Invoke(st.Id != 0 ? $"the region has you under object {st.Id}; going down through it" : "the region has you under the ground here; following it");
            return floor;
        }
        _viewFeet = st.Z + over;
        _floor = _viewFeet + _lift;
        if (_floorVel < 0) _floorVel = 0;
        return _floor;
    }

    // --- the region's word on it --------------------------------------------------------------------------------

    /// <summary>While the stick walks: how far the region moved the avatar along the walk over the last 0.4 s against
    /// how far it should have, and what follows from that. Stopped by the region where the model saw no wall: pull the
    /// walk back onto it and remember a wall there. Let through where the model saw one: that prim stops counting.</summary>
    void Judge(Simulator sim, uint self, float feetZ, Vector3 raw, Vector3 rawVel, Vector3 dir, float speed, float since, double now)
    {
        ulong region = sim.Handle;
        // where the region has the avatar now: its last word carried on by its velocity (a region sends little while
        // an avatar walks steadily, leaving the rest to dead reckoning)
        var vel = Flat(rawVel);
        var flat = Flat(raw) + vel * (float)Math.Min(DeadReckonCap, now - _rawChangedAt);
        if (_trail.Count == 0 || Vector3.Distance(_trail.Last().Raw, flat) > 1e-4f) _trail.Enqueue((now, flat, dir));
        while (_trail.Count > 0 && now - _trail.Peek().At > 0.6) _trail.Dequeue();
        if (since < _latency + 0.6f || _trail.Count < 2) return;
        var (thenAt, thenRaw, thenDir) = _trail.Peek();
        double span = now - thenAt;
        if (span < 0.2 || Vector3.Dot(thenDir, dir) < 0.85f) return;   // (turning, the region's walk turns a round trip later)
        float progress = Vector3.Dot(flat - thenRaw, thenDir);
        float expected = speed * (float)span;
        float ahead = Vector3.Dot(_walkPos - flat, dir);

        if (_blockedBy != 0)
        {
            if (_blockedSince < 0) _blockedSince = now;
            // the region has the avatar through the surface the model stopped the walk at: past its plane, and its
            // path there crosses that prim (going round its end doesn't count)
            var (_, from, _) = _trail.Peek();
            if (Vector3.Dot(flat - _blockedAt, _blockedNormal) < -0.05f && Vector3.Dot(from - _blockedAt, _blockedNormal) > 0
                && Cast(region, self, new Vector3(from.X, from.Y, feetZ + 1f), Vector3.Normalize(flat - from), Vector3.Distance(flat, from)) is { } through
                && through.LocalId == _blockedBy
                // and the walk is straight behind it from there (not just round a corner)
                && Cast(region, self, new Vector3(flat.X, flat.Y, feetZ + 1f), Vector3.Normalize(_walkPos - flat), Vector3.Distance(_walkPos, flat)) is { } back
                && back.LocalId == _blockedBy)
            {
                _passable[_blockedBy] = now + 120;
                Say?.Invoke($"the region let you through object {_blockedBy}; walking on through it");
                _blockedBy = 0;
                _blockedSince = -1;
            }
            return;
        }
        _blockedSince = -1;
        if (progress < 0.3f * expected && Vector3.Dot(vel, dir) < 0.3f * speed && ahead > 0.25f && _pullBack == Vector3.Zero)
        {
            // the region holds the avatar here: back onto it, and a wall just ahead of it for a while. If what's ahead is
            // a step the model took for one it climbs, the region's step is lower than that
            if (Support(sim, self, flat.X + dir.X * 0.6f, flat.Y + dir.Y * 0.6f, feetZ + StepHeight + StepSlack, StepHeight + 0.3f) is { } ahead2
                && ahead2 - feetZ is > 0.1f and var rise && rise <= StepHeight + StepSlack)
            {
                StepHeight = MathF.Max(0.15f, rise - 0.03f);
                Say?.Invoke($"steps: the region climbs less than {rise:F2} m here; {StepHeight:F2} from now on");
            }
            _pullBack = dir * -ahead;
            _pullVel = Vector3.Zero;
            _refused.RemoveAll(r => r.Until < now);
            _refused.Add((flat + dir * 0.02f, -dir, now + 4));
            _trail.Clear();
        }
    }

    /// <summary>One frame of pulling back after a refusal: quick and critically damped (about 0.2 s).</summary>
    Vector3 PullBack(double dt)
    {
        if (_pullBack == Vector3.Zero) return Vector3.Zero;
        const float w = 18f;
        var moved = Vector3.Zero;
        for (double t = dt; t > 1e-6; t -= 1.0 / 240)
        {
            float h = (float)Math.Min(t, 1.0 / 240);
            _pullVel += (_pullBack * w * w - _pullVel * 2 * w) * h;
            var d = _pullVel * h;
            _pullBack -= d;
            moved += d;
        }
        if (_pullBack.Length() < 0.005f && _pullVel.Length() < 0.05f) { moved += _pullBack; _pullBack = Vector3.Zero; }
        return moved;
    }

    /// <summary>Whether a solid wall stands between two points (chest high above each's feet): putting the avatar
    /// somewhere by teleport mustn't take it through one.</summary>
    bool WallBetween(ulong region, uint self, Vector3 a, Vector3 b, float feetA, float feetB)
    {
        var from = new Vector3(a.X, a.Y, feetA + 1.2f);
        var to = new Vector3(b.X, b.Y, feetB + 1.2f);
        var d = to - from;
        float len = d.Length();
        if (len < 0.05f) return false;
        foreach (float lift in new[] { 0f, -0.6f })
            if (Cast(region, self, from + new Vector3(0, 0, lift), d / len, len) is { } hit && MathF.Abs(hit.Normal.Z) <= WallNormalZ) return true;
        return false;
    }
}
