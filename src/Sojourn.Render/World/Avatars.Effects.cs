// Other people's viewer effects, as every viewer shows them: point-at and edit beams (the LLHUDEffectSpiral family,
// newview/llhudeffecttrail.cpp, drawn by LLViewerPartSourceBeam and LLViewerPartSourceSpiral, llviewerpartsource.cpp,
// with addHUDEffect's settings, llhudobject.cpp), PointAt moving the pointer's left arm (llhudeffectpointat.cpp into
// LLEditingMotion, Bodies/Animator.Ik.cs) and ResetSkeleton (llhudeffectresetskeleton.cpp). LibreMetaverse raises
// neither the beams' colour nor ResetSkeleton, so the packet is read here. LGPL 2.1, Linden Research, Inc.
using System.Collections.Concurrent;
using System.Diagnostics;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Render.Particles;

namespace Sojourn.Render.World;

/// <summary>Viewer effects from other people, as the region relays them (the network thread in, the render thread out):
/// beams by effect id, each avatar's PointAt, and skeleton resets asked for by their owners.</summary>
public sealed class ViewerEffects
{
    public static ViewerEffects Shared { get; } = new();

    public const byte Beam = 7, Point = 9, Sphere = 11, Spiral = 12, Edit = 13, PointAtType = 15, ResetSkeleton = 19;
    // EPointAtType
    public const byte PointNone = 0, PointClear = 3;

    /// <summary>One of the spiral family: who and what it joins (or a global position), its colour, and its life.</summary>
    public sealed class BeamFx
    {
        public UUID Id, Source, Target;
        public byte Type;
        public Vector3d Pos;
        public Vector4 Color;
        public double Start, Kill;
    }

    /// <summary>A PointAt: what it names (zero: the offset is a global position, else an offset in the target's frame).</summary>
    public readonly record struct PointAtFx(UUID Target, Vector3d Offset, byte Type);

    readonly ConcurrentDictionary<UUID, BeamFx> _beams = new();
    readonly ConcurrentDictionary<UUID, PointAtFx> _pointAt = new();
    readonly ConcurrentQueue<(UUID Avatar, bool Animations)> _resets = new();

    /// <summary>ShowSelectionBeam (on by default): beams are drawn at all.</summary>
    public volatile bool ShowBeams = true;

    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void Attach(GridClient client) =>
        client.Network.RegisterCallback(PacketType.ViewerEffect, (_, e) =>
        {
            try { On(client, (ViewerEffectPacket)e.Packet); }
            catch (Exception) { /* a malformed block: the rest of the session goes on */ }
        });

    void On(GridClient client, ViewerEffectPacket p)
    {
        var self = client.Self.AgentID;
        double now = Now;
        foreach (var b in p.Effect)
        {
            var data = b.TypeData;
            switch (b.Type)
            {
                case Beam or Point or Sphere or Spiral or Edit:
                {
                    // LLHUDEffectSpiral::unpackData: source, target, a global position; a re-sent effect lives on
                    if (data is not { Length: 56 }) break;
                    var src = new UUID(data, 0);
                    if (src == self) break;   // our own come from us
                    var fx = _beams.GetOrAdd(b.ID, id => new BeamFx { Id = id, Start = now });
                    fx.Type = b.Type;
                    fx.Source = src;
                    fx.Target = new UUID(data, 16);
                    fx.Pos = new Vector3d(data, 32);
                    var c = b.Color is { Length: >= 4 } col ? col : [255, 255, 255, 255];
                    fx.Color = new Vector4(c[0] / 255f, c[1] / 255f, c[2] / 255f, c[3] / 255f);
                    fx.Kill = now + b.Duration;   // triggerLocal: its kill time from now
                    break;
                }
                case PointAtType:
                {
                    if (data is not { Length: 57 }) break;
                    var src = new UUID(data, 0);
                    if (src == self) break;
                    byte type = data[56];
                    if (type is PointNone or PointClear) _pointAt.TryRemove(src, out _);
                    else _pointAt[src] = new PointAtFx(new UUID(data, 16), new Vector3d(data, 32), type);
                    break;
                }
                case ResetSkeleton:
                {
                    // only the owner of an avatar resets its skeleton like this (the target defaults to the sender)
                    if (data is not { Length: 17 }) break;
                    var target = new UUID(data, 0);
                    if (target == UUID.Zero) target = b.AgentID;
                    if (target != b.AgentID) break;
                    _resets.Enqueue((target, (data[16] & 1) != 0));
                    break;
                }
            }
        }
    }

    internal ICollection<BeamFx> Beams => _beams.Values;
    internal void Forget(BeamFx fx) => _beams.TryRemove(KeyValuePair.Create(fx.Id, fx));
    public bool TryPointAt(UUID source, out PointAtFx fx) => _pointAt.TryGetValue(source, out fx);
    internal bool AnyPointAt => !_pointAt.IsEmpty;
    internal IEnumerable<UUID> PointAtSources => _pointAt.Keys;
    internal void ForgetPointAt(UUID source) => _pointAt.TryRemove(source, out _);
    internal bool TryReset(out (UUID Avatar, bool Animations) r) => _resets.TryDequeue(out r);
}

public sealed partial class Avatars
{
    long _pointSweep;

    /// <summary>What this avatar points at, for its animator's editing motion (LLHUDEffectPointAt::calcTargetPosition):
    /// an avatar's head plus the offset in its pelvis's frame, an object's position plus the offset in its frame, or a
    /// global position; in the view's frame. Null with no PointAt or a target that isn't here.</summary>
    Vector3? PointAtFor(RegionScene scene, Vector3 offset, Avatar av, Body body, long tick)
    {
        var fx = ViewerEffects.Shared;
        if (fx.AnyPointAt && tick - _pointSweep > 2000)
        {
            // an effect dies with its avatar
            _pointSweep = tick;
            foreach (var id in fx.PointAtSources.ToList())
                if (!_lookScenes.Any(s => s.Scene.Source.Avatars.Values.Any(a => a.ID == id))) fx.ForgetPointAt(id);
        }
        if (!fx.TryPointAt(av.ID, out var p)) return null;
        var local = new Vector3((float)p.Offset.X, (float)p.Offset.Y, (float)p.Offset.Z);
        Vector3 target;
        if (p.Target == UUID.Zero) target = FromGlobal(scene, offset, p.Offset);
        else if (_bodies.TryGetValue(p.Target, out var b) && b.Shape != null && tick - b.LastUsed < 1000
                 && _cd.Skeleton.TryGet("mHead", out int head) && _cd.Skeleton.TryGet("mPelvis", out int pelvis))
            target = b.Pose.WorldPos[head] + Vector3.Transform(local, b.Pose.WorldRot[pelvis]);
        else if (AvatarHead(p.Target, tick) is { } other) target = other.Head + Vector3.Transform(local, other.Rot);
        else if (FindObject(p.Target) is { } obj) target = obj.Pos + Vector3.Transform(local, obj.Rot);
        else return null;
        return float.IsFinite(target.X + target.Y + target.Z) ? target : null;
    }

    /// <summary>A global position in the view's frame, by the region the avatar is in and its offset.</summary>
    static Vector3 FromGlobal(RegionScene scene, Vector3 offset, Vector3d global)
    {
        Utils.LongToUInts(scene.Source.Handle, out uint rx, out uint ry);
        return new Vector3((float)(global.X - rx), (float)(global.Y - ry), (float)global.Z) + offset;
    }

    // --- beams ---------------------------------------------------------------------------------------------------------

    readonly List<Particle> _beamParts = new();
    long _beamFrame = -1;
    int _wristLeft = -2;

    /// <summary>This frame's beam and spiral particles (ParticleBuilder adds them to the frame's own), in the view's frame.</summary>
    public IReadOnlyList<Particle> BeamParticles()
    {
        if (_beamFrame != _frameTick) BuildBeams();
        return _beamParts;
    }

    // LLViewerPartSourceBeam::update and LLViewerPartSourceSpiral::update: a particle every RATE seconds while the effect
    // lives; a beam's live half a second, a spiral's a second
    const double BeamRate = 0.025;
    const float BeamAge = 0.5f, SpiralAge = 1f;

    /// <summary>Every effect of the spiral family as its particle source draws it. With a source and a target (an object,
    /// or a global position): a beam, particles flowing from the source (an avatar's left wrist) to the target over half a
    /// second each, 10 cm across, turned along the beam, the effect's colour fading to 40% alpha; all of them gone when the
    /// effect ends, as setDead's null source kills them. With neither: a spiral, particles circling a metre round the
    /// source and rising a metre over their second, 25 cm across, fading out. The particles are worked out from when each
    /// would have been emitted (deterministic, no simulation), so they're the same whatever the frame rate.</summary>
    void BuildBeams()
    {
        _beamFrame = _frameTick;
        _beamParts.Clear();
        var fx = ViewerEffects.Shared;
        if (!fx.ShowBeams) return;
        double now = ViewerEffects.Now;
        long tick = System.Environment.TickCount64;
        if (_wristLeft == -2) _wristLeft = _cd.Skeleton["mWristLeft"];
        foreach (var b in fx.Beams)
        {
            bool spiral = b.Target == UUID.Zero && b.Pos == Vector3d.Zero;
            if (now > b.Kill + (spiral ? SpiralAge : 0) + 1) { fx.Forget(b); continue; }
            if (EffectSource(b.Source, tick, spiral) is not { } src) continue;
            if (spiral)
            {
                Spiral(b, src, now);
                continue;
            }
            if (now > b.Kill) continue;
            Vector3 dst;
            if (b.Target != UUID.Zero)
            {
                if (EffectTarget(b.Target, tick) is not { } t) continue;
                dst = t;
            }
            else if (GlobalToView(b.Pos) is { } g) dst = g;
            else continue;
            var dir = dst - src;
            var end = b.Color with { W = 0.4f };
            for (long k = (long)Math.Ceiling(Math.Max(0, now - BeamAge - b.Start) / BeamRate); ; k++)
            {
                double born = b.Start + k * BeamRate;
                if (born > now) break;
                float frac = (float)((now - born) / BeamAge);
                if (frac >= 1) continue;
                var color = b.Color * (1 - frac) + end * frac;
                _beamParts.Add(new Particle
                {
                    Pos = Vector3.Lerp(src, dst, frac), Vel = dir, Color = color, Scale = new Vector2(0.1f),
                    Flags = PartFlags.InterpColor | PartFlags.InterpScale | PartFlags.TargetPos | PartFlags.FollowVelocity,
                    BlendSrc = 7, BlendDst = 9, Source = -1, StartColor = b.Color, StartScaleX = 0.1f,
                });
            }
        }
    }

    void Spiral(ViewerEffects.BeamFx b, Vector3 src, double now)
    {
        var end = b.Color with { W = 0 };
        double last = Math.Min(now, b.Kill);
        for (long k = (long)Math.Ceiling(Math.Max(0, now - SpiralAge - b.Start) / BeamRate); ; k++)
        {
            double born = b.Start + k * BeamRate;
            if (born > last) break;
            float frac = (float)((now - born) / SpiralAge);
            if (frac >= 1) continue;
            // ll_frand(F_TWO_PI): a phase of its own, fixed per particle
            float param = (HashCode.Combine(b.Id, k) & 0xFFFFFF) / 16777216f * MathF.Tau;
            float a = MathF.Tau * frac + param;
            _beamParts.Add(new Particle
            {
                Pos = src + new Vector3(MathF.Sin(a), MathF.Cos(a), -0.5f + frac), Color = b.Color * (1 - frac) + end * frac,
                Scale = new Vector2(0.25f), Flags = PartFlags.InterpColor, BlendSrc = 7, BlendDst = 9, Source = -1,
                StartColor = b.Color, StartScaleX = 0.25f,
            });
        }
    }

    /// <summary>Where an effect starts: an avatar's left wrist as drawn (a spiral circles its root), else an object's
    /// position.</summary>
    Vector3? EffectSource(UUID id, long tick, bool spiral)
    {
        if (_bodies.TryGetValue(id, out var b) && b.Shape != null && tick - b.LastUsed < 1000 && _wristLeft >= 0 && b.HiddenJoints == null)
            return spiral ? b.Pose.WorldPos[0] : b.Pose.WorldPos[_wristLeft];   // (joint 0: mPelvis, at the root)
        foreach (var (scene, offset) in _lookScenes)
            foreach (var a in scene.Source.Avatars.Values)
                if (a.ID == id && Placement(scene, a, out var pos, out _, out _)) return pos + offset;
        return FindObject(id)?.Pos;
    }

    /// <summary>Where an effect ends on its target: an object's position, an avatar's root as drawn.</summary>
    Vector3? EffectTarget(UUID id, long tick)
    {
        if (FindObject(id) is { } o) return o.Pos;
        if (_bodies.TryGetValue(id, out var b) && b.Shape != null && tick - b.LastUsed < 1000) return b.Pose.WorldPos[0];
        foreach (var (scene, offset) in _lookScenes)
            foreach (var a in scene.Source.Avatars.Values)
                if (a.ID == id && Placement(scene, a, out var pos, out _, out _)) return pos + offset;
        return null;
    }

    Vector3? GlobalToView(Vector3d global)
    {
        if (_lookScenes.Count == 0) return null;
        var (scene, offset) = _lookScenes[0];
        return FromGlobal(scene, offset, global);
    }
}
