// The viewer's two-bone IK solver, as llcharacter/lljointsolverrp3.cpp (LLJointSolverRP3::solve) has it, for the
// editing motion's left arm and the standing motion's legs (Animator.Ik.cs). LGPL 2.1, Linden Research, Inc.
using System.Numerics;

namespace Sojourn.Render.Bodies;

/// <summary>A chain of three joints under a parent, as LLJointSolverRP3 sees it: the parent's world position, rotation and
/// scale; A's, B's and C's local positions (offsets from their parents, scaled by the parent's scale as LLJoint does);
/// A's and B's scales; and A's and B's base rotations (setupJoints: the solve starts from these each time).</summary>
public struct Rp3Chain
{
    public Vector3 ParentPos, ParentScale;
    public Quaternion ParentRot;
    public Vector3 PosA, PosB, PosC, ScaleA, ScaleB;
    public Quaternion BaseA, BaseB;

    /// <summary>World positions of A, B and C and world rotations of A and B for local rotations a and b.</summary>
    public readonly (Vector3 A, Vector3 B, Vector3 C, Quaternion Aw, Quaternion Bw) World(Quaternion a, Quaternion b)
    {
        var aw = Quaternion.Normalize(Quaternion.Concatenate(a, ParentRot));
        var pa = ParentPos + Vector3.Transform(PosA * ParentScale, ParentRot);
        var bw = Quaternion.Normalize(Quaternion.Concatenate(b, aw));
        var pb = pa + Vector3.Transform(PosB * ScaleA, aw);
        var pc = pb + Vector3.Transform(PosC * ScaleB, bw);
        return (pa, pb, pc, aw, bw);
    }
}

public static class JointSolverRP3
{
    /// <summary>LLJointSolverRP3::solve: A's and B's new local rotations so C reaches <paramref name="goal"/>, the chain's
    /// plane through the pole vector (in the parent's frame), B bending about <paramref name="bAxis"/> (in B's frame)
    /// when given; no twist. Where the viewer gives up (a singular plane) the base rotations come back unchanged.</summary>
    public static (Quaternion A, Quaternion B) Solve(in Rp3Chain c, Vector3 goal, Vector3 pole, Vector3? bAxis)
    {
        var (aPos, bPos, cPos, aw, bw) = c.World(c.BaseA, c.BaseB);
        var poleVec = Vector3.Transform(Vector3.Normalize(pole), c.ParentRot);
        var ab = bPos - aPos;
        var bc = cPos - bPos;
        var ag = goal - aPos;
        float abLen = ab.Length(), bcLen = bc.Length(), agLen = ag.Length();
        if (abLen < 1e-6f || bcLen < 1e-6f) return (c.BaseA, c.BaseB);

        Vector3 abcNorm;
        if (bAxis is { } axis) abcNorm = Vector3.Transform(Vector3.Normalize(axis), bw);
        else if (Parallel(ab, bc))
        {
            if (Parallel(poleVec, ab))
            {
                if (Parallel(poleVec, ag)) return (c.BaseA, c.BaseB);
                abcNorm = Vector3.Cross(poleVec, ag);
            }
            else abcNorm = Vector3.Cross(poleVec, ab);
        }
        else abcNorm = Vector3.Cross(ab, bc);

        // B's bend: the angle between A->B and B->C that puts C at the goal's distance
        float abbcAng = AngleBetween(ab, bc);
        var abbcOrtho = Vector3.Cross(ab, bc);
        if (abbcOrtho.LengthSquared() < 0.001f) abbcOrtho = Vector3.Cross(poleVec, ab);
        if (abbcOrtho.LengthSquared() < 1e-12f) return (c.BaseA, c.BaseB);
        abbcOrtho = Vector3.Normalize(abbcOrtho);
        float cosTheta = Math.Clamp((agLen * agLen - abLen * abLen - bcLen * bcLen) / (2f * abLen * bcLen), -1f, 1f);
        float theta = MathF.Acos(cosTheta);
        var bRot = Quaternion.CreateFromAxisAngle(abbcOrtho, theta - abbcAng);

        // then the whole chain turned so A->C lies along A->G
        bc = Vector3.Transform(bc, bRot);
        var ac = ab + bc;
        var cgRot = ShortestArc(ac, ag);
        ab = Vector3.Transform(ab, cgRot);
        bc = Vector3.Transform(bc, cgRot);
        abcNorm = Vector3.Transform(abcNorm, cgRot);

        // and about A->G so the chain's plane holds the pole vector
        if (Parallel(ag, poleVec)) return (c.BaseA, c.BaseB);
        var apgNorm = Vector3.Normalize(Vector3.Cross(poleVec, ag));
        if (bAxis == null)
        {
            if (!Parallel(ab, bc)) abcNorm = Vector3.Cross(ab, bc);
            abcNorm = Vector3.Normalize(abcNorm);
        }
        Quaternion pRot = Quaternion.Identity;
        if (Parallel(abcNorm, apgNorm))
        {
            if (Vector3.Dot(abcNorm, apgNorm) < 0) pRot = Quaternion.CreateFromAxisAngle(Vector3.Normalize(ag), MathF.PI);
        }
        else pRot = ShortestArc(abcNorm, apgNorm);
        var aRot = Quaternion.Concatenate(cgRot, pRot);

        // B's world rotation first (under A at its base), then A's
        var bWorld = Quaternion.Normalize(Quaternion.Concatenate(bw, bRot));
        var bLocal = Quaternion.Normalize(Quaternion.Concatenate(bWorld, Quaternion.Inverse(aw)));
        var aWorld = Quaternion.Normalize(Quaternion.Concatenate(aw, aRot));
        var aLocal = Quaternion.Normalize(Quaternion.Concatenate(aWorld, Quaternion.Inverse(c.ParentRot)));
        return (aLocal, bLocal);
    }

    /// <summary>are_parallel(a, b, 0.001).</summary>
    public static bool Parallel(Vector3 a, Vector3 b, float epsilon = 0.001f)
    {
        float la = a.Length(), lb = b.Length();
        if (la < 1e-12f || lb < 1e-12f) return true;
        return 1f - MathF.Abs(Vector3.Dot(a / la, b / lb)) < epsilon;
    }

    /// <summary>angle_between: radians between two directions.</summary>
    public static float AngleBetween(Vector3 a, Vector3 b)
    {
        float la = a.Length(), lb = b.Length();
        if (la < 1e-12f || lb < 1e-12f) return 0;
        return MathF.Acos(Math.Clamp(Vector3.Dot(a / la, b / lb), -1f, 1f));
    }

    /// <summary>LLQuaternion::shortestArc: the smallest rotation taking a's direction to b's.</summary>
    public static Quaternion ShortestArc(Vector3 a, Vector3 b)
    {
        float la = a.Length(), lb = b.Length();
        if (la < 1e-12f || lb < 1e-12f) return Quaternion.Identity;
        a /= la;
        b /= lb;
        float dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        if (dot > 0.999999f) return Quaternion.Identity;
        if (dot < -0.999999f)
        {
            // opposite: half a turn about any axis at right angles to a
            var ortho = MathF.Abs(a.X) < 0.9f ? Vector3.Cross(a, Vector3.UnitX) : Vector3.Cross(a, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(ortho), MathF.PI);
        }
        var axis = Vector3.Normalize(Vector3.Cross(a, b));
        return Quaternion.CreateFromAxisAngle(axis, MathF.Acos(dot));
    }
}
