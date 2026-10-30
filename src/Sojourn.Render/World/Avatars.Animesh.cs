using LibreMetaverse;
using Sojourn.Render.Bodies;
using Sojourn.Render.Frame;

namespace Sojourn.Render.World;

/// <summary>
/// Animated objects (animesh): an object whose root has the Animated Mesh flag gets a skeleton of its own, as the
/// viewer gives it a control avatar (llcontrolavatar.cpp): no body drawn, the default shape, playing what its prims
/// signal (ObjectAnimation, <see cref="ObjectAnimations"/>), and its rigged meshes skinned to it through the same path
/// as worn rigged meshes (RegionScene.AddRigged, keyed by the root's local id instead of a wearer's). Placed as
/// matchVolumeTransform does: in the world, mRoot at the root prim's position, turned by the prim's rotation and the
/// root mesh's bind shape rotation, and kept within 3 m of the prim (getNewConstraintFixups, from the last frame's
/// extents); worn, at the attachment point of the wearer as posed this frame, the root prim's offset and rotation on
/// it. Joint positions come from the rigged meshes' joint overrides, as for an avatar. One over 64 m across is scaled
/// down to 64 m about its pelvis (the scale fixup, 2026-10-29). Not done: name tags and the impostor state of a wearer.
/// Claude Opus 5.5, 2026-10-26.
/// </summary>
public sealed partial class Avatars
{
    sealed class Rig
    {
        public required Body Body;
        public Vector3 Fixup, BoxMin, BoxMax;
        public bool HasBox;
        public float Scale = 1, MeshSpan;   // the scale fixup, and its rigged meshes' largest span in bind space
        public int OverridesKey = int.MinValue;
        public List<Animation>? Merged;
        public int MergedKey;
    }

    const float MaxLegalOffset = 3f;   // LLControlAvatar::MAX_LEGAL_OFFSET
    const float MaxLegalSize = 64f;    // LLControlAvatar::MAX_LEGAL_SIZE (AnimatedObjectsMaxLegalSize)
    readonly Dictionary<UUID, Rig> _rigs = new();
    BodyShape? _rigShape;
    long _lastRigSweep;

    /// <summary>Pose every animated object in reach (after the avatars: a worn one sits on its wearer's pose).</summary>
    int PoseAnimesh(IReadOnlyList<(RegionScene Scene, Vector3 Offset)> scenes, ViewSetup v, double now, long tick)
    {
        int pending = 0;
        foreach (var (scene, offset) in scenes)
        {
            var src = scene.Source;
            foreach (var (root, wearer) in scene.AnimeshRoots())
            {
                var prim = root.Prim;
                Vector3 pos;
                Quaternion rot;
                if (wearer != 0)
                {
                    if (AttachmentFrame(src.Handle, wearer, (int)prim.PrimData.AttachmentPoint) is not { } f) continue;
                    pos = f.Pos + Vector3.Transform(V(prim.Position), f.Rot);
                    rot = Quaternion.Normalize(Quaternion.Concatenate(Q(prim.Rotation), f.Rot));
                }
                else
                {
                    if (!root.Placed) continue;
                    (pos, rot) = (root.WPos, root.WRot);
                }
                pos += offset;
                if (Vector3.Distance(pos, v.Position) - MaxLegalOffset - 2 > v.DrawDistance) continue;

                if (!_rigs.TryGetValue(prim.ID, out var rig))
                {
                    _rigs[prim.ID] = rig = new Rig { Body = new Body { Id = prim.ID, Anim = new AvatarAnimator(now), Pose = new SkeletonPose(_cd.Skeleton) } };
                    // nothing signalled yet: what comes later starts then (else the first list counts as long playing,
                    // and a one-shot started after we arrived would never show)
                    if (Signaled(scene, root, rig) == null) rig.Body.Anim.Sync(new List<Animation>(), now);
                }
                var body = rig.Body;
                body.LastUsed = tick;
                var shape = _rigShape ??= new BodyShape(_cd, null);
                Overrides(scene, root, rig, shape);
                if (body.Anim.Sync(Signaled(scene, root, rig), now) && Debug)
                    _world.Log?.Invoke($"animesh {prim.ID}: {body.Anim.Describe(_motions, src.Assets)}");
                body.Anim.Evaluate(shape, body.Pose, _motions, src.Assets, now, ref pending, body.RestPos, body.RestScale);
                var rootRot = wearer != 0 ? rot : Quaternion.Normalize(Quaternion.Concatenate(BindRotation(scene, root), rot));
                if (wearer == 0 && rig.HasBox) rig.Fixup = ConstraintFixup(pos, rig.BoxMin - rig.Fixup, rig.BoxMax - rig.Fixup);
                else if (wearer != 0) rig.Fixup = Vector3.Zero;
                if (rig.HasBox) rig.Scale = ScaleFixup(rig);
                if (rig.Scale != 1)
                    for (int j = 0; j < body.Pose.Scale.Length; j++) body.Pose.Scale[j] *= rig.Scale;
                body.HiddenJoints = null;
                body.Pose.Compose(pos + rig.Fixup, rootRot);

                // the animated extents (the joints, padded as an avatar's), for culling, rigged LOD and the fixup
                var pose = body.Pose;
                var bmin = pose.WorldPos[0];
                var bmax = bmin;
                for (int j = 0; j < _cd.Skeleton.NumBones; j++)
                    if (_cd.Skeleton.Joints[j].IsBase) { bmin = Vector3.Min(bmin, pose.WorldPos[j]); bmax = Vector3.Max(bmax, pose.WorldPos[j]); }
                (rig.BoxMin, rig.BoxMax, rig.HasBox) = (bmin, bmax, true);
                bmin -= new Vector3(0.25f);
                bmax += new Vector3(0.25f);
                body.Center = (bmin + bmax) * 0.5f;
                body.Diagonal = (bmax - bmin).Length();
                _posed[(src.Handle, root.LocalId)] = (body, offset);
            }
        }
        if (tick - _lastRigSweep > 10_000)
        {
            _lastRigSweep = tick;
            foreach (var id in _rigs.Where(kv => tick - kv.Value.Body.LastUsed > 60_000).Select(kv => kv.Key).ToList()) _rigs.Remove(id);
        }
        return pending;
    }

    /// <summary>point_to_box_offset and getNewConstraintFixups: how far to move the skeleton so its box (as it'd be
    /// without a fixup) comes within the legal offset of the object's position.</summary>
    static Vector3 ConstraintFixup(Vector3 pos, Vector3 min, Vector3 max)
    {
        var toPos = pos - Vector3.Clamp(pos, min, max);
        float d = toPos.Length();
        return d > MaxLegalOffset ? (d - MaxLegalOffset) / d * toPos : Vector3.Zero;
    }

    /// <summary>getNewConstraintFixups' scale: an animated object bigger than 64 m across (by last frame's box with this
    /// fixup taken out, and the viewer's padding of a quarter metre a side) is scaled to 64 m, every joint's scale
    /// times the fixup as setGlobalScale's recursiveScaleJoint does from the pelvis (worn ones too). The viewer's box
    /// stretches by each joint's rigged vertices; here the joints' box or the rigged meshes' largest span in bind space,
    /// whichever is larger (so a mesh built big on a small skeleton counts).</summary>
    static float ScaleFixup(Rig rig)
    {
        var d = rig.BoxMax - rig.BoxMin + new Vector3(0.5f);
        float size = MathF.Max(MathF.Max(d.X, d.Y), d.Z);
        size = MathF.Max(size, rig.MeshSpan * rig.Scale);
        if (!(size > 0) || !float.IsFinite(size)) return 1;
        return size / rig.Scale > MaxLegalSize ? rig.Scale * MaxLegalSize / size : 1;
    }

    /// <summary>The root mesh's bind shape rotation (MATCH_BIND_SHAPE, MAINT-8671), identity until its skin has loaded.</summary>
    static Quaternion BindRotation(RegionScene scene, SceneObject root)
    {
        foreach (var l in root.Lods)
            if (l is { Ready: true, Skin: { } skin } && skin.BindShapeMatrix is { Length: >= 16 } m)
            {
                var bsm = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                return Matrix4x4.Decompose(bsm, out _, out var r, out _) ? Quaternion.Normalize(r) : Quaternion.Identity;
            }
        return Quaternion.Identity;
    }

    /// <summary>Joint positions from the linkset's rigged meshes (addAttachmentOverridesForObject, as WornState does
    /// for an avatar's), worked out again only when its loaded shapes change.</summary>
    void Overrides(RegionScene scene, SceneObject root, Rig rig, BodyShape shape)
    {
        var rigged = scene.AnimeshShapesOf(root).OrderBy(x => x.Mesh.ToString()).ToList();
        var h = new HashCode();
        foreach (var (_, geo) in rigged) h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(geo));
        int key = h.ToHashCode();
        var body = rig.Body;
        if (key == rig.OverridesKey && body.RestPos != null) return;
        rig.OverridesKey = key;
        rig.MeshSpan = 0;
        foreach (var (_, geo) in rigged)
            if (geo.Max.X >= geo.Min.X)
            {
                var span = geo.Max - geo.Min;
                rig.MeshSpan = MathF.Max(rig.MeshSpan, MathF.Max(MathF.Max(span.X, span.Y), span.Z));
            }
        var sk = _cd.Skeleton;
        int n = sk.Joints.Count;
        body.RestPos ??= new Vector3[n];
        body.RestScale ??= new Vector3[n];
        Array.Copy(shape.JointPos, body.RestPos, n);
        Array.Copy(shape.JointScale, body.RestScale, n);
        foreach (var (_, geo) in rigged)
        {
            var skin = geo.Skin!;
            if (geo.AltInverseBind is not { } alt) continue;
            for (int j = 0; j < alt.Length && j < skin.JointNames.Length; j++)
            {
                int joint = sk[skin.JointNames[j]];
                if (joint < 0) continue;
                var p = alt[j].Translation;
                if (Vector3.Distance(p, sk.Joints[joint].Pos) <= 0.0001f) continue;
                body.RestPos[joint] = p;
                if (skin.LockScaleIfJointPosition) body.RestScale[joint] = sk.Joints[joint].Scale;
            }
        }
    }

    /// <summary>What the linkset's prims signal, together (LLControlAvatar::updateAnimations: each animation once, at
    /// the larger sequence). The root's own list when only it signals; a new list only when one of them changed.</summary>
    static List<Animation>? Signaled(RegionScene scene, SceneObject root, Rig rig)
    {
        List<Animation>? only = null;
        int lists = 0;
        var h = new HashCode();
        foreach (var id in scene.AnimeshPartIds(root))
        {
            if (ObjectAnimations.Shared.Get(id) is not { } l) continue;
            h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(l));
            only = l;
            lists++;
        }
        if (lists <= 1) return only;
        int key = h.ToHashCode();
        if (rig.Merged != null && key == rig.MergedKey) return rig.Merged;
        var merged = new Dictionary<UUID, Animation>();
        foreach (var id in scene.AnimeshPartIds(root))
            if (ObjectAnimations.Shared.Get(id) is { } l)
                foreach (var a in l.ToArray())
                    if (!merged.TryGetValue(a.AnimationID, out var cur) || cur.AnimationSequence < a.AnimationSequence) merged[a.AnimationID] = a;
        (rig.Merged, rig.MergedKey) = (merged.Values.ToList(), key);
        return rig.Merged;
    }
}
