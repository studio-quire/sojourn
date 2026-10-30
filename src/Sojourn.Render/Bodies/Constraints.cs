// Keyframe animation constraints (the small IK solver that puts a sitting woman's hands on her knees and keeps her
// knees together), as llcharacter/llkeyframemotion.cpp applyConstraint does it, in its steady state for a single
// frame: lengths from the keyframe pose, reach weight 0 or 1, middle joints started at their kinematic positions,
// the 20-iteration spring relaxation, then rotations root first and the end joint kept at its keyframe world
// orientation. Helpers from llmath (LLQuaternion::shortestArc, clamp_rescale). LGPL 2.1, Linden Research, Inc.
using System.Numerics;

namespace Sojourn.Render.Bodies;

public static class Constraints
{
    const float JointLengthK = 0.7f;
    const int MaxIterations = 20, MinIterationCount = 2;
    const float MinAccelerationSquared = 0.0005f * 0.0005f;

    /// <summary>Apply one constraint of a motion at local time <paramref name="t"/>. <paramref name="tmp"/> holds the
    /// blended pose (local transforms) with the root at the origin; <paramref name="rots"/>/<paramref name="poss"/>
    /// are this motion's sampled joint states, updated in place (rotations only where the motion has rotation keys).
    /// <paramref name="groundZ"/>: the ground under the avatar in the root frame.</summary>
    public static void Apply(ConstraintDef c, float t, SkeletonPose tmp, Quaternion?[] rots, Vector3?[] poss, float groundZ)
    {
        if (t < c.EaseInStart || t > c.EaseOutStop) return;
        int n = c.ChainLength;
        var chain = c.Chain;
        var saved = new Quaternion[n + 1];
        for (int k = 0; k <= n; k++)
        {
            saved[k] = tmp.Rot[chain[k]];
            tmp.Rot[chain[k]] = rots[chain[k]] ?? Quaternion.Identity;
        }
        tmp.Compose(Vector3.Zero, Quaternion.Identity);
        try
        {
            var rootPos = tmp.WorldPos[chain[n]];
            var src = VolumePos(tmp, c.SourceJoint, c.SourceOffset);
            Vector3 target = c.TargetJoint >= 0 ? VolumePos(tmp, c.TargetJoint, c.TargetOffset)
                                                : new Vector3(src.X, src.Y, groundZ) + c.TargetOffset;
            if (c.Type == 1)
            {
                var norm = c.TargetJoint >= 0 ? target - tmp.WorldPos[c.TargetJoint] : Vector3.UnitZ;
                if (norm == Vector3.Zero) norm = Vector3.Transform(-c.SourceOffset, tmp.WorldRot[c.SourceJoint]);
                norm = Vector3.Normalize(norm);
                target = src + norm * Vector3.Dot(target - src, norm);
            }

            // lengths from the keyframe pose (initializeConstraint, quirks included)
            var lengths = new float[Math.Max(n, 1)];
            var fractions = new float[Math.Max(n, 1)];
            float total = 0;
            if (n >= 1)
            {
                lengths[0] = Vector3.Distance(tmp.WorldPos[chain[1]], src);
                total = lengths[0];
                for (int j = 1; j < n; j++)
                {
                    lengths[j] = Vector3.Distance(tmp.WorldPos[chain[j]], tmp.WorldPos[chain[j + 1]]);
                    total += lengths[j];
                }
                for (int j = 1; j < n; j++) fractions[j] = lengths[j] / total;
            }
            total += Vector3.Distance(src, tmp.WorldPos[chain[0]]);

            float reach = n != 0 && Vector3.DistanceSquared(rootPos, target) * 0.95f > total * total ? 0f : 1f;
            float ease = c.EaseOutStop == 0 ? 1 : MathF.Min(ClampRescale(t, c.EaseInStart, c.EaseInStop, 0, 1),
                                                             ClampRescale(t, c.EaseOutStart, c.EaseOutStop, 1, 0));
            float weight = reach * ease;
            var d = target - src;

            if (n >= 1)
            {
                var endRotWorld = tmp.WorldRot[chain[0]];
                var p = new Vector3[n + 1];
                p[0] = Vector3.Lerp(src, target, weight);
                p[n] = rootPos;
                for (int j = 1; j < n; j++) p[j] = tmp.WorldPos[chain[j]] + d * fractions[j];
                var vel = new Vector3[n];
                for (int it = 0; it < MaxIterations; it++)
                {
                    int finished = 0;
                    for (int j = 1; j < n; j++)
                    {
                        var a = (p[j - 1] - p[j]) * ((Vector3.Distance(p[j], p[j - 1]) - lengths[j - 1]) * JointLengthK);
                        a += (p[j + 1] - p[j]) * ((Vector3.Distance(p[j + 1], p[j]) - lengths[j]) * JointLengthK);
                        if (a.LengthSquared() < MinAccelerationSquared) finished++;
                        vel[j - 1] *= 0.7f;
                        p[j] += vel[j - 1] + a * 0.5f;
                        vel[j - 1] += a;
                    }
                    if (it >= MinIterationCount && finished == n - 1) break;
                }

                for (int j = n; j >= 1; j--)
                {
                    int joint = chain[j];
                    int parent = tmp.Def.Joints[joint].Parent;
                    var parentRot = parent >= 0 ? tmp.WorldRot[parent] : Quaternion.Identity;
                    var curRot = tmp.WorldRot[joint];
                    var targetAt = p[j - 1] - p[j];
                    var currentAt = j == 1 ? VolumePos(tmp, c.SourceJoint, c.SourceOffset) - tmp.WorldPos[joint]
                                           : Vector3.Transform(tmp.Pos[chain[j - 1]], curRot);
                    var fixup = ShortestArc(currentAt, targetAt);
                    var local = Quaternion.Normalize(Quaternion.Inverse(parentRot) * fixup * curRot);
                    if (weight != 1f) local = KeyframeMotion.Nlerp(weight, rots[joint] ?? Quaternion.Identity, local);
                    Set(joint, local);
                }
                {
                    int end = chain[0];
                    var endLocal = Quaternion.Normalize(Quaternion.Inverse(tmp.WorldRot[chain[1]]) * endRotWorld);
                    if (weight != 1f) endLocal = KeyframeMotion.Nlerp(weight, rots[end] ?? Quaternion.Identity, endLocal);
                    Set(end, endLocal);
                }
            }
            else if (poss[chain[0]] != null)
            {
                // chain length 0 moves the joint itself (the pelvis)
                int joint = chain[0];
                int parent = tmp.Def.Joints[joint].Parent;
                var parentRot = parent >= 0 ? tmp.WorldRot[parent] : Quaternion.Identity;
                poss[joint] = tmp.Pos[joint] + Vector3.Transform(d * weight, Quaternion.Inverse(parentRot));
            }

            void Set(int joint, Quaternion local)
            {
                // the blender only reads a joint state's rotation if the motion keys it
                if (rots[joint] != null) rots[joint] = local;
                tmp.Rot[joint] = local;
                tmp.Compose(Vector3.Zero, Quaternion.Identity);
            }
        }
        finally
        {
            for (int k = 0; k <= n; k++) tmp.Rot[chain[k]] = saved[k];
        }
    }

    /// <summary>LLJoint::getVolumePos: the offset in the volume's frame, scaled by its own scale.</summary>
    static Vector3 VolumePos(SkeletonPose pose, int volume, Vector3 offset) =>
        pose.WorldPos[volume] + Vector3.Transform(offset * pose.Scale[volume], pose.WorldRot[volume]);

    /// <summary>LLQuaternion::shortestArc(a, b): the rotation taking direction a to direction b.</summary>
    static Quaternion ShortestArc(Vector3 a, Vector3 b)
    {
        float ab = Vector3.Dot(a, b);
        var c = Vector3.Cross(a, b);
        float cc = c.LengthSquared();
        if (ab * ab + cc == 0) return Quaternion.Identity;
        if (cc > 0)
        {
            float s = MathF.Sqrt(ab * ab + cc) + ab;
            float m = 1f / MathF.Sqrt(cc + s * s);
            return new Quaternion(c.X * m, c.Y * m, c.Z * m, s * m);
        }
        if (ab < 0)
        {
            var d = a - b;
            float m = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
            return m > 1e-6f ? new Quaternion(-d.Y / m, d.X / m, 0, 0) : new Quaternion(1, 0, 0, 0);
        }
        return Quaternion.Identity;
    }

    static float ClampRescale(float x, float x1, float x2, float y1, float y2)
    {
        float ramp = x1 == x2 ? 0 : (x - x1) / (x2 - x1);
        ramp = Math.Clamp(ramp, 0, 1);
        return y1 + (y2 - y1) * ramp;
    }
}
