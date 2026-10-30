// Other people's look-at, as every viewer shows it: their LookAt viewer effects turn their heads (LLHUDEffectLookAt's
// calcTargetPosition feeding LLHeadRotMotion and LLEyeMotion, Bodies/Animator.cs). LGPL 2.1, Linden Research, Inc.
using System.Collections.Concurrent;
using LibreMetaverse;

namespace Sojourn.Render.World;

/// <summary>Each avatar's latest LookAt viewer effect, as the region relays them (any thread in, the render thread
/// out). An effect lasts until its sender sends another, a None clears it, and it goes with its avatar.</summary>
public sealed class LookAtTracker
{
    public static LookAtTracker Shared { get; } = new();

    /// <summary>The target (zero: none, the offset is then a global position), the offset, the ELookAtType.</summary>
    public readonly record struct Effect(UUID Target, Vector3d Offset, byte Type);

    public const byte None = 0, FreeLook = 3, Mouselook = 9;

    readonly ConcurrentDictionary<UUID, Effect> _bySource = new();

    public void Attach(GridClient client) =>
        client.Avatars.ViewerEffectLookAt += (_, e) =>
        {
            // our own come from us (the viewer ignores its own effect's echo)
            if (e.SourceID == client.Self.AgentID) return;
            byte type = (byte)e.LookType;
            if (type == None) _bySource.TryRemove(e.SourceID, out Effect _);
            else _bySource[e.SourceID] = new Effect(e.TargetID, e.TargetPosition, type);
        };

    public bool TryGet(UUID source, out Effect e) => _bySource.TryGetValue(source, out e);
    public bool Any => !_bySource.IsEmpty;
    internal IEnumerable<UUID> Sources => _bySource.Keys;
    internal void Forget(UUID source) => _bySource.TryRemove(source, out _);
}

public sealed partial class Avatars
{
    IReadOnlyList<(RegionScene Scene, Vector3 Offset)> _lookScenes = Array.Empty<(RegionScene, Vector3)>();
    long _lookSweep;

    /// <summary>The scenes this frame draws, for finding look-at targets (AddAvatars).</summary>
    void LookScenes(IReadOnlyList<(RegionScene Scene, Vector3 Offset)> scenes, long tick)
    {
        _lookScenes = scenes;
        var lt = LookAtTracker.Shared;
        if (!lt.Any || tick - _lookSweep < 2000) return;
        _lookSweep = tick;
        // an effect dies with its avatar (LLHUDEffectLookAt::update marks it dead with its source)
        foreach (var id in lt.Sources.ToList())
            if (!scenes.Any(s => s.Scene.Source.Avatars.Values.Any(a => a.ID == id))) lt.Forget(id);
    }

    /// <summary>Where this avatar looks, for its animator: the look-at point relative to its head and the world's up,
    /// in its root's frame (LLHUDEffectLookAt::calcTargetPosition, from last frame's pose as the viewer's joints are);
    /// null with no effect, a target that isn't here, or a body not yet posed.</summary>
    (Vector3 Point, Vector3 Up)? LookAtFor(RegionScene scene, Vector3 offset, Avatar av, Body body, long tick)
    {
        if (!LookAtTracker.Shared.TryGet(av.ID, out var fx)) return null;
        if (body.Shape == null || !_cd.Skeleton.TryGet("mHead", out int head) || !_cd.Skeleton.TryGet("mPelvis", out int pelvis)) return null;
        var pose = body.Pose;
        var headAt = pose.WorldPos[head];
        // mRoot: the pelvis hangs off it (SkeletonPose.Compose)
        var rootRot = Quaternion.Normalize(pose.WorldRot[pelvis] * Quaternion.Inverse(pose.Rot[pelvis]));
        var local = new Vector3((float)fx.Offset.X, (float)fx.Offset.Y, (float)fx.Offset.Z);
        Vector3 target;
        if (fx.Target == UUID.Zero)
        {
            // a global position
            Utils.LongToUInts(scene.Source.Handle, out uint rx, out uint ry);
            target = new Vector3((float)(fx.Offset.X - rx), (float)(fx.Offset.Y - ry), (float)fx.Offset.Z) + offset;
        }
        else if (AvatarHead(fx.Target, tick) is { } other)
        {
            // another avatar (or itself): its head, the offset absolute for mouselook and freelook, else in its frame
            bool absolute = fx.Type is LookAtTracker.Mouselook or LookAtTracker.FreeLook;
            target = other.Head + (absolute ? local : Vector3.Transform(local, other.Rot));
        }
        else if (FindObject(fx.Target) is { } obj) target = obj.Pos + Vector3.Transform(local, obj.Rot);
        else return null;
        var point = target - headAt;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return null;
        var toRoot = Quaternion.Conjugate(rootRot);
        return (Vector3.Transform(point, toRoot), Vector3.Transform(Vector3.UnitZ, toRoot));
    }

    /// <summary>An avatar's head (view frame) and root rotation as last drawn, else where the region has it.</summary>
    (Vector3 Head, Quaternion Rot)? AvatarHead(UUID id, long tick)
    {
        if (_bodies.TryGetValue(id, out var b) && b.Shape != null && tick - b.LastUsed < 1000
            && _cd.Skeleton.TryGet("mHead", out int head) && _cd.Skeleton.TryGet("mPelvis", out int pelvis))
            return (b.Pose.WorldPos[head], Quaternion.Normalize(b.Pose.WorldRot[pelvis] * Quaternion.Inverse(b.Pose.Rot[pelvis])));
        foreach (var (scene, offset) in _lookScenes)
            foreach (var a in scene.Source.Avatars.Values)
                if (a.ID == id && Placement(scene, a, out var pos, out var rot, out _))
                    return (pos + offset + new Vector3(0, 0, 0.5f), rot);   // about where a standing avatar's head is
        return null;
    }

    (Vector3 Pos, Quaternion Rot)? FindObject(UUID id)
    {
        foreach (var (scene, offset) in _lookScenes)
            if (scene.TryFindObject(id, out var pos, out var rot)) return (pos + offset, rot);
        return null;
    }
}
