// Avatars in a mirror (Realtime/FrameRenderer.Mirror.cs). The viewer's hero probe shows avatars only when the mirror
// is dynamic (cubeSnapshot hides RENDER_TYPE_AVATAR otherwise); a static mirror leaves every avatar and what it wears
// to the eyes. The own body in the headset is drawn headless for the eyes, so a mirror that shows avatars gets it a
// second time, whole: the same pose with the head and what's worn on it put back.
using Sojourn.Render.Bodies;
using Sojourn.Render.Frame;
using Sojourn.Render.Geometry;

namespace Sojourn.Render.World;

public enum MirrorAvatars : byte { None, Hidden, Shown }

public sealed partial class Avatars
{
    /// <summary>Whether a mirror is in view this frame and what it does with avatars; set by the renderer before
    /// <see cref="AddAvatars"/>.</summary>
    public MirrorAvatars Mirror { get; set; }

    /// <summary>What avatar instances draw in: the eyes alone while a mirror leaves avatars out.</summary>
    uint AvatarInstanceFlags => Mirror == MirrorAvatars.Hidden ? InstanceFlags.MainOnly : 0;

    /// <summary>The own body's mirror pose: the pose just made for the eyes with the head grown back (PoseSelf shrinks it to
    /// a point), or none when no mirror shows avatars.</summary>
    void PoseMirrorSelf(Body body, Vector3 rootPos, Quaternion rootRot, float rootScale)
    {
        if (Mirror != MirrorAvatars.Shown || body.HiddenJoints is not { } hidden)
        {
            body.MirrorPose = null;
            return;
        }
        var pose = body.Pose;
        var mp = body.MirrorPose ??= new SkeletonPose(_cd.Skeleton);
        int n = pose.Scale.Length;
        Array.Copy(pose.Pos, mp.Pos, n);
        Array.Copy(pose.Rot, mp.Rot, n);
        Array.Copy(pose.Scale, mp.Scale, n);
        for (int i = 0; i < n; i++)
            if (hidden[i]) mp.Scale[i] *= 1000f;
        mp.Compose(rootPos, rootRot, rootScale);
    }

    /// <summary>A rigged mesh's palette on the own body's mirror pose (see <see cref="RiggedPalette"/>), or -1.</summary>
    public int MirrorPalette(ulong region, uint avatarLocalId, ShapeGeometry shape, DrawList dl)
    {
        if (!dl.MainView || shape.InverseBind == null || shape.Skin == null) return -1;
        if (!_posed.TryGetValue((region, avatarLocalId), out var p) || p.Body.MirrorPose is not { } mp) return -1;
        var share = ShareOf(shape);
        if (dl.PaletteStarts.TryGetValue((p.Body, share, true), out int start)) return start;
        start = WritePalette(dl, share, mp.World);
        dl.PaletteStarts[(p.Body, share, true)] = start;
        return start;
    }

    /// <summary>An attachment point's frame on the own body's mirror pose (see <see cref="AttachmentFrame"/>), or null.</summary>
    public (Vector3 Pos, Quaternion Rot)? MirrorAttachmentFrame(ulong region, uint avatarLocalId, int point)
    {
        if (!_posed.TryGetValue((region, avatarLocalId), out var p) || p.Body.MirrorPose is not { } pose) return null;
        if (!_cd.AttachmentPoints.TryGetValue(point, out var ap) && !_cd.AttachmentPoints.TryGetValue(1, out ap)) return null;
        if (ap.Hud) return null;
        int j = ap.Joint;
        var wr = Quaternion.Normalize(Quaternion.Concatenate(ap.Rotation, pose.WorldRot[j]));
        var wp = pose.WorldPos[j] + Vector3.Transform(pose.Scale[j] * ap.Position, pose.WorldRot[j]);
        return (wp - p.Offset, wr);
    }

    /// <summary>The own avatar's local id in a region while a mirror shows it whole (its attachments are drawn twice), or 0.</summary>
    public uint MirrorSelfIn(ulong region)
    {
        if (Mirror != MirrorAvatars.Shown) return 0;
        foreach (var ((r, id), (body, _)) in _posed)
            if (r == region && body.MirrorPose != null) return id;
        return 0;
    }
}
