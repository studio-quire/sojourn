using System.Numerics;
using System.Text;

namespace Sojourn.Core.Media;

/// <summary>A rotation key: time in seconds, joint-local rotation in SL's frame (Z up, X forward).</summary>
public readonly record struct AnimRotationKey(float Time, Quaternion Rotation);

/// <summary>A position key: time in seconds, offset in metres in SL's frame.</summary>
public readonly record struct AnimPositionKey(float Time, Vector3 Position);

public sealed record AnimJoint(
    string Name,
    int Priority,
    IReadOnlyList<AnimRotationKey> RotationKeys,
    IReadOnlyList<AnimPositionKey> PositionKeys);

/// <summary>
/// An SL animation asset (LLKeyframeMotion, version 1.0): the binary the viewer's animation
/// upload produces. <see cref="ToBytes"/> writes it the way LLKeyframeMotion::serialize does
/// (including its quantization); <see cref="Parse"/> reads it the way LLKeyframeMotion::deserialize does.
/// Constraints are read and skipped; the writer always writes none.
/// </summary>
public sealed record AnimFile
{
    public const float MaxDuration = 60f;      // MAX_ANIM_DURATION (llbvhconsts.h)
    public const float MaxPelvisOffset = 5f;   // LL_MAX_PELVIS_OFFSET: position keys are quantized over +-5 m
    public const int MaxJoints = 216;          // LL_CHARACTER_MAX_ANIMATED_JOINTS

    public int Priority { get; init; } = 2;
    public float Duration { get; init; }
    /// <summary>Internal emote name, e.g. "express_smile" ("" = none). See <see cref="AnimConvert.Emotes"/>.</summary>
    public string EmoteName { get; init; } = "";
    public float LoopIn { get; init; }
    public float LoopOut { get; init; }
    public bool Loop { get; init; }
    public float EaseIn { get; init; } = 0.3f;
    public float EaseOut { get; init; } = 0.3f;
    /// <summary>Index into the viewer's hand poses (0 Spread, 1 Relaxed, ...). See <see cref="AnimConvert.HandPoses"/>.</summary>
    public int HandPose { get; init; } = 1;
    public IReadOnlyList<AnimJoint> Joints { get; init; } = [];
    /// <summary>Constraint count found when parsing (the writer writes 0).</summary>
    public int ConstraintCount { get; init; }

    public byte[] ToBytes()
    {
        var w = new BinaryWriter(new MemoryStream(), Encoding.UTF8);
        w.Write((ushort)1);
        w.Write((ushort)0);
        w.Write(Priority);
        w.Write(Duration);
        WriteCString(w, EmoteName);
        w.Write(LoopIn);
        w.Write(LoopOut);
        w.Write(Loop ? 1 : 0);
        w.Write(EaseIn);
        w.Write(EaseOut);
        w.Write((uint)HandPose);
        w.Write((uint)Joints.Count);
        foreach (var j in Joints)
        {
            WriteCString(w, j.Name);
            w.Write(j.Priority);

            // LLKeyframeMotion keeps keys in a map keyed by time; mirror that so a reader never sees
            // two keys at one (quantized) time.
            var rot = new SortedDictionary<ushort, (ushort, ushort, ushort)>();
            foreach (var k in j.RotationKeys)
            {
                var v = PackQuaternion(k.Rotation);
                rot[Quant.F32ToU16(k.Time, 0f, Duration)] = (
                    Quant.F32ToU16(Quant.Quantize16(v.X, -1f, 1f), -1f, 1f),
                    Quant.F32ToU16(Quant.Quantize16(v.Y, -1f, 1f), -1f, 1f),
                    Quant.F32ToU16(Quant.Quantize16(v.Z, -1f, 1f), -1f, 1f));
            }
            WriteKeys(w, rot);

            var pos = new SortedDictionary<ushort, (ushort, ushort, ushort)>();
            const float P = MaxPelvisOffset;
            foreach (var k in j.PositionKeys)
            {
                pos[Quant.F32ToU16(k.Time, 0f, Duration)] = (
                    Quant.F32ToU16(Quant.Quantize16(k.Position.X, -P, P), -P, P),
                    Quant.F32ToU16(Quant.Quantize16(k.Position.Y, -P, P), -P, P),
                    Quant.F32ToU16(Quant.Quantize16(k.Position.Z, -P, P), -P, P));
            }
            WriteKeys(w, pos);
        }
        w.Write(0); // constraints
        w.Flush();
        return ((MemoryStream)w.BaseStream).ToArray();
    }

    public static AnimFile Parse(byte[] data)
    {
        var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        try
        {
            ushort version = r.ReadUInt16(), sub = r.ReadUInt16();
            if (version != 1 || sub != 0)
                throw new MediaFormatException($"Animation version {version}.{sub} isn't 1.0.");
            int priority = r.ReadInt32();
            float duration = r.ReadSingle();
            if (!float.IsFinite(duration) || duration > MaxDuration)
                throw new MediaFormatException($"Animation duration {duration} is invalid (max {MaxDuration} s).");
            string emote = ReadCString(r);
            float loopIn = r.ReadSingle(), loopOut = r.ReadSingle();
            bool loop = r.ReadInt32() != 0;
            float easeIn = r.ReadSingle(), easeOut = r.ReadSingle();
            uint hand = r.ReadUInt32();
            uint count = r.ReadUInt32();
            if (count == 0 || count > MaxJoints)
                throw new MediaFormatException($"Animation has {count} joints (must be 1..{MaxJoints}).");
            var joints = new List<AnimJoint>();
            const float P = MaxPelvisOffset;
            for (int i = 0; i < count; i++)
            {
                string name = ReadCString(r);
                int jp = r.ReadInt32();
                int nr = r.ReadInt32();
                if (nr < 0 || nr > data.Length) throw new MediaFormatException("Bad rotation key count.");
                var rots = new List<AnimRotationKey>(nr);
                for (int k = 0; k < nr; k++)
                {
                    float t = Quant.U16ToF32(r.ReadUInt16(), 0f, duration);
                    var v = new Vector3(Quant.U16ToF32(r.ReadUInt16(), -1f, 1f),
                                        Quant.U16ToF32(r.ReadUInt16(), -1f, 1f),
                                        Quant.U16ToF32(r.ReadUInt16(), -1f, 1f));
                    rots.Add(new(t, UnpackQuaternion(v)));
                }
                int np = r.ReadInt32();
                if (np < 0 || np > data.Length) throw new MediaFormatException("Bad position key count.");
                var poss = new List<AnimPositionKey>(np);
                for (int k = 0; k < np; k++)
                {
                    float t = Quant.U16ToF32(r.ReadUInt16(), 0f, duration);
                    var v = new Vector3(Quant.U16ToF32(r.ReadUInt16(), -P, P),
                                        Quant.U16ToF32(r.ReadUInt16(), -P, P),
                                        Quant.U16ToF32(r.ReadUInt16(), -P, P));
                    poss.Add(new(t, v));
                }
                joints.Add(new AnimJoint(name, jp, rots, poss));
            }
            int constraints = r.ReadInt32();
            return new AnimFile
            {
                Priority = priority, Duration = duration, EmoteName = emote,
                LoopIn = loopIn, LoopOut = loopOut, Loop = loop, EaseIn = easeIn, EaseOut = easeOut,
                HandPose = (int)hand, Joints = joints, ConstraintCount = constraints,
            };
        }
        catch (EndOfStreamException)
        {
            throw new MediaFormatException("Animation data ends early.");
        }
    }

    // LLQuaternion::packToVector3: drop w, flipping the sign so w >= 0.
    internal static Vector3 PackQuaternion(Quaternion q)
    {
        float mag = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        float x = q.X, y = q.Y, z = q.Z;
        if (mag > 1e-6f) { x /= mag; y /= mag; z /= mag; }
        return q.W >= 0 ? new Vector3(x, y, z) : new Vector3(-x, -y, -z);
    }

    internal static Quaternion UnpackQuaternion(Vector3 v)
    {
        float t = 1f - v.LengthSquared();
        return new Quaternion(v, t > 0 ? MathF.Sqrt(t) : 0f);
    }

    static void WriteKeys(BinaryWriter w, SortedDictionary<ushort, (ushort X, ushort Y, ushort Z)> keys)
    {
        w.Write(keys.Count);
        foreach (var (t, v) in keys)
        {
            w.Write(t);
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }
    }

    static void WriteCString(BinaryWriter w, string s)
    {
        w.Write(Encoding.UTF8.GetBytes(s));
        w.Write((byte)0);
    }

    static string ReadCString(BinaryReader r)
    {
        var bytes = new List<byte>();
        for (byte b; (b = r.ReadByte()) != 0;) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

/// <summary>The viewer's 16-bit quantization (llquantize.h, LLVector3::quantize16).</summary>
internal static class Quant
{
    const float U16Max = 65535f;
    const float OoU16Max = 1f / U16Max;

    public static ushort F32ToU16(float val, float lower, float upper)
    {
        val = Math.Clamp(val, lower, upper);
        val -= lower;
        val /= upper - lower;
        return (ushort)MathF.Floor(val * U16Max);
    }

    public static float U16ToF32(ushort ival, float lower, float upper)
    {
        float val = ival * OoU16Max;
        float delta = upper - lower;
        val *= delta;
        val += lower;
        if (MathF.Abs(val) < delta * OoU16Max) val = 0f; // zeros come through as zero
        return val;
    }

    public static float Quantize16(float v, float lower, float upper) => U16ToF32(F32ToU16(v, lower, upper), lower, upper);
}
