// Second Life keyframe animations, decoded and sampled as the viewer does: llcharacter/llkeyframemotion.cpp
// (deserialize, ScaleCurve/RotationCurve/PositionCurve::getValue, onUpdate looping), llmath/llquantize.h
// (U16_to_F32), llmath/llquaternion.cpp (unpackFromVector3, nlerp). LGPL 2.1, Linden Research, Inc.
using System.Numerics;
using System.Text;

namespace Sojourn.Render.Bodies;

public sealed class JointMotion
{
    public required string Name;
    public int Joint = -1;              // skeleton index, -1 if unknown (kept as a dummy by the viewer)
    public int Priority;                // resolved: the joint's own, or the motion's base priority
    public float[] RotTimes = Array.Empty<float>();
    public Quaternion[] Rots = Array.Empty<Quaternion>();
    public float[] PosTimes = Array.Empty<float>();
    public Vector3[] Positions = Array.Empty<Vector3>();
}

/// <summary>A constraint record (LLKeyframeMotion::JointConstraintSharedData as stored in the asset).</summary>
public sealed class ConstraintDef
{
    public int ChainLength, Type;          // type 0 point, 1 plane
    public required string SourceVolume, TargetVolume;
    public Vector3 SourceOffset, TargetOffset, TargetDir;
    public float EaseInStart, EaseInStop, EaseOutStart, EaseOutStop;
    /// <summary>Resolved at decode: the source/target collision volumes (-1 target: GROUND) and the chain, from the
    /// source volume's bone up ChainLength ancestors (all must be animated by the motion, or it fails to load).</summary>
    public int SourceJoint, TargetJoint = -1;
    public int[] Chain = Array.Empty<int>();
}

public sealed class KeyframeMotion
{
    public ConstraintDef[] Constraints = Array.Empty<ConstraintDef>();
    public int BasePriority, MaxPriority;
    public float Duration, LoopIn, LoopOut, EaseIn, EaseOut;
    public bool Loop;
    public string Emote = "";
    public int HandPose;
    public JointMotion[] Joints = Array.Empty<JointMotion>();

    public const float MaxPelvisOffset = 5f;   // LL_MAX_PELVIS_OFFSET

    public static KeyframeMotion Decode(byte[] data, SkeletonDef sk)
    {
        using var br = new BinaryReader(new MemoryStream(data));
        int version = br.ReadUInt16(), sub = br.ReadUInt16();
        if (version != 1 || sub != 0) throw new InvalidDataException($"animation version {version}.{sub} not supported");
        var m = new KeyframeMotion();
        int basePri = br.ReadInt32();
        m.MaxPriority = 0;   // LOW_PRIORITY
        if (basePri >= 7) { basePri = 6; m.MaxPriority = 6; }
        else if (basePri < -1) throw new InvalidDataException("bad base priority");
        m.BasePriority = basePri;
        m.Duration = br.ReadSingle();
        if (!(m.Duration >= 0 && m.Duration <= 60f)) throw new InvalidDataException("bad duration");
        m.Emote = ReadString(br);
        m.LoopIn = br.ReadSingle();
        m.LoopOut = br.ReadSingle();
        m.Loop = br.ReadInt32() != 0;
        m.EaseIn = br.ReadSingle();
        m.EaseOut = br.ReadSingle();
        m.HandPose = (int)br.ReadUInt32();
        int nj = (int)br.ReadUInt32();
        if (nj < 1 || nj > 216) throw new InvalidDataException("bad joint count");
        var joints = new List<JointMotion>(nj);
        for (int j = 0; j < nj; j++)
        {
            var jm = new JointMotion { Name = ReadString(br) };
            jm.Joint = sk.TryGet(jm.Name, out var ji) ? ji : -1;
            int pri = br.ReadInt32();
            if (pri < -1) throw new InvalidDataException("bad joint priority");
            if (pri != -1 && pri > m.MaxPriority) m.MaxPriority = pri;
            jm.Priority = pri == -1 ? m.BasePriority : pri;

            int nr = br.ReadInt32();
            if (nr < 0) throw new InvalidDataException("bad rotation key count");
            var rk = new SortedDictionary<float, Quaternion>();
            for (int k = 0; k < nr; k++)
            {
                float t = U16ToF32(br.ReadUInt16(), 0, m.Duration);
                var v = new Vector3(U16ToF32(br.ReadUInt16(), -1, 1), U16ToF32(br.ReadUInt16(), -1, 1), U16ToF32(br.ReadUInt16(), -1, 1));
                float w2 = 1f - v.LengthSquared();
                rk[t] = Quaternion.Normalize(new Quaternion(v, w2 > 0 ? MathF.Sqrt(w2) : 0));
            }
            jm.RotTimes = rk.Keys.ToArray();
            jm.Rots = rk.Values.ToArray();

            int np = br.ReadInt32();
            if (np < 0) throw new InvalidDataException("bad position key count");
            var pk = new SortedDictionary<float, Vector3>();
            for (int k = 0; k < np; k++)
            {
                float t = U16ToF32(br.ReadUInt16(), 0, m.Duration);
                pk[t] = new Vector3(U16ToF32(br.ReadUInt16(), -MaxPelvisOffset, MaxPelvisOffset),
                    U16ToF32(br.ReadUInt16(), -MaxPelvisOffset, MaxPelvisOffset), U16ToF32(br.ReadUInt16(), -MaxPelvisOffset, MaxPelvisOffset));
            }
            jm.PosTimes = pk.Keys.ToArray();
            jm.Positions = pk.Values.ToArray();
            // "mScreen" and "mRoot" are refused by the viewer
            if (jm.Name is "mScreen" or "mRoot") continue;
            joints.Add(jm);
        }
        m.Joints = joints.ToArray();
        if (br.BaseStream.Position + 4 <= br.BaseStream.Length)
        {
            int nc = br.ReadInt32();
            if (nc is > 0 and <= 10)
            {
                var cs = new List<ConstraintDef>(nc);
                for (int c = 0; c < nc; c++)
                {
                    int chain = br.ReadByte(), type = br.ReadByte();
                    var src = Fixed(br, 16);
                    var so = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    var tgt = Fixed(br, 16);
                    var to = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    var td = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    var cd = new ConstraintDef
                    {
                        ChainLength = chain, Type = type, SourceVolume = src, SourceOffset = so, TargetVolume = tgt, TargetOffset = to,
                        TargetDir = td, EaseInStart = br.ReadSingle(), EaseInStop = br.ReadSingle(), EaseOutStart = br.ReadSingle(), EaseOutStop = br.ReadSingle(),
                    };
                    ResolveConstraint(cd, m, sk);
                    cs.Add(cd);
                }
                m.Constraints = cs.ToArray();
            }
        }
        return m;
    }

    // LLKeyframeMotion::deserialize's constraint setup: collision volumes by name, the chain up from the source's bone
    static void ResolveConstraint(ConstraintDef c, KeyframeMotion m, SkeletonDef sk)
    {
        if (c.ChainLength > m.Joints.Length) throw new InvalidDataException("constraint chain longer than the motion");
        int Volume(string name)
        {
            int j = sk[name];
            if (j < 0 || sk.Joints[j].IsBone) throw new InvalidDataException($"constraint volume {name} unknown");
            return j;
        }
        c.SourceJoint = Volume(c.SourceVolume);
        c.TargetJoint = c.TargetVolume == "GROUND" ? -1 : Volume(c.TargetVolume);
        var chain = new int[c.ChainLength + 1];
        int joint = c.SourceJoint;
        for (int i = 0; i <= c.ChainLength; i++)
        {
            joint = sk.Joints[joint].Parent;
            if (joint < 0 || !m.Joints.Any(jm => jm.Joint == joint)) throw new InvalidDataException("constraint chain joint not animated");
            chain[i] = joint;
        }
        c.Chain = chain;
    }

    /// <summary>The motion's local time at <paramref name="t"/> seconds since activation (onUpdate's looping).</summary>
    public float LocalTime(float t)
    {
        if (!Loop) return t;
        if (Duration == 0) return 0;
        if (t > LoopOut) t = LoopOut == LoopIn ? LoopOut : LoopIn + (t - LoopOut) % (LoopOut - LoopIn);
        return t;
    }

    public static Quaternion SampleRot(JointMotion jm, float t)
    {
        var times = jm.RotTimes;
        if (times.Length == 0) return Quaternion.Identity;
        int i = Upper(times, t);
        if (i == 0) return jm.Rots[0];
        if (i >= times.Length) return jm.Rots[^1];
        float u = (t - times[i - 1]) / (times[i] - times[i - 1]);
        return Nlerp(u, jm.Rots[i - 1], jm.Rots[i]);
    }

    public static Vector3 SamplePos(JointMotion jm, float t)
    {
        var times = jm.PosTimes;
        int i = Upper(times, t);
        if (i == 0) return jm.Positions[0];
        if (i >= times.Length) return jm.Positions[^1];
        float u = (t - times[i - 1]) / (times[i] - times[i - 1]);
        return Vector3.Lerp(jm.Positions[i - 1], jm.Positions[i], u);
    }

    // first key with time > t
    static int Upper(float[] times, float t)
    {
        int lo = 0, hi = times.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (times[mid] <= t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>LL nlerp(t, a, b): normalized lerp, slerp when the quaternions point apart.</summary>
    public static Quaternion Nlerp(float t, Quaternion a, Quaternion b)
    {
        float dot = Quaternion.Dot(a, b);
        if (dot < 0) return Quaternion.Slerp(a, b, t);
        return Quaternion.Normalize(new Quaternion(
            a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t));
    }

    public static float U16ToF32(ushort v, float lower, float upper)
    {
        float val = v * (1f / 65535f) * (upper - lower) + lower;
        if (MathF.Abs(val) < (upper - lower) / 65535f) val = 0f;
        return val;
    }

    static string Fixed(BinaryReader br, int n)
    {
        var b = br.ReadBytes(n);
        int z = Array.IndexOf(b, (byte)0);
        return Encoding.ASCII.GetString(b, 0, z < 0 ? b.Length : z);
    }

    static string ReadString(BinaryReader br)
    {
        var sb = new StringBuilder();
        byte b;
        while ((b = br.ReadByte()) != 0) sb.Append((char)b);
        return sb.ToString();
    }
}
