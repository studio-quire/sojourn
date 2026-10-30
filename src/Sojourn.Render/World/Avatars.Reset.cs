// Local fixes for avatars drawn wrong: the viewer's Reset Skeleton (and Animations) (newview/llvoavatar.cpp
// resetSkeleton, resetAnimations; llviewermenu.cpp's handlers), Firestorm's Resync Animations (FSToolsResyncAnimations)
// and the drawing half of Refresh Attachments. LGPL 2.1, Linden Research, Inc.
using System.Collections.Concurrent;
using LibreMetaverse;

namespace Sojourn.Render.World;

/// <summary>Fixes asked for from any thread (SojournAvatars, or a ResetSkeleton effect from an avatar's owner), done on
/// the render thread at the next frame.</summary>
public sealed class AvatarFixes
{
    public enum Kind : byte { Skeleton, SkeletonAndAnimations, Refresh, Resync }

    readonly ConcurrentQueue<(Kind Kind, UUID Id)> _queue = new();

    /// <summary>Reset Skeleton: the avatar's shape, joint overrides from what it wears and physics worked out afresh
    /// (and with <paramref name="animations"/>, every animation started again from now).</summary>
    public void ResetSkeleton(UUID id, bool animations) => _queue.Enqueue((animations ? Kind.SkeletonAndAnimations : Kind.Skeleton, id));
    /// <summary>The avatar's worn objects resolved afresh (faces, textures, joint overrides), with its skeleton.</summary>
    public void Refresh(UUID id) => _queue.Enqueue((Kind.Refresh, id));
    /// <summary>Every avatar's and animated object's animations restarted together.</summary>
    public void Resync() => _queue.Enqueue((Kind.Resync, UUID.Zero));

    internal bool TryTake(out (Kind Kind, UUID Id) fix) => _queue.TryDequeue(out fix);
}

public sealed partial class Avatars
{
    /// <summary>The fixes waiting (the person's, and owners' resets of their own avatars received as effects).</summary>
    void ApplyFixes(IReadOnlyList<(RegionScene Scene, Vector3 Offset)> scenes)
    {
        double now = Now;
        while (Limits.Fixes.TryTake(out var f)) Apply(f.Kind, f.Id);
        while (ViewerEffects.Shared.TryReset(out var r)) Apply(r.Animations ? AvatarFixes.Kind.SkeletonAndAnimations : AvatarFixes.Kind.Skeleton, r.Avatar);

        void Apply(AvatarFixes.Kind kind, UUID id)
        {
            if (kind == AvatarFixes.Kind.Resync)
            {
                foreach (var (a, _) in _animators.Values) a.Resync(now);
                foreach (var rig in _rigs.Values) rig.Body.Anim.Resync(now);
                _world.Log?.Invoke("avatars: animations resynced");
                return;
            }
            if (kind == AvatarFixes.Kind.SkeletonAndAnimations && _animators.TryGetValue(id, out var an)) an.Anim.ResetAnimations();
            if (_bodies.TryGetValue(id, out var body)) ResetBody(body);
            if (kind == AvatarFixes.Kind.Refresh)
                foreach (var (scene, _) in scenes)
                    foreach (var av in scene.Source.Avatars.Values)
                        if (av.ID == id) scene.RefreshWorn(av.LocalID);
            _world.Log?.Invoke($"avatars: {id} {kind switch { AvatarFixes.Kind.Refresh => "refreshed", AvatarFixes.Kind.Skeleton => "skeleton reset", _ => "skeleton and animations reset" }}");
        }
    }

    /// <summary>resetSkeleton's local half: the skeleton built again from the shape (the visual params as last received),
    /// the attachments' joint overrides applied afresh, physics from rest, and its impostor captured again.</summary>
    void ResetBody(Body body)
    {
        body.Shape = null;
        body.RestPos = null;
        body.RestScale = null;
        body.WornKey = 0;
        body.WornShape = null;
        body.Physics = null;
        body.Captured = false;
        body.CostKey = int.MinValue;
        body.Lip = null;
        body.SelfAt = 0;
    }
}
