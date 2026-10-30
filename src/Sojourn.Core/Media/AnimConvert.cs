using System.Globalization;
using System.Numerics;

namespace Sojourn.Core.Media;

public enum BvhUnits { Inches, Centimeters, Meters }

/// <summary>
/// Upload settings, as in the viewer's animation upload floater. Defaults are the viewer's.
/// </summary>
public sealed record AnimOptions
{
    /// <summary>0-6. The floater offers 0-4 (default 2); 5 and 6 are valid in the format and widely used.</summary>
    public int Priority { get; init; } = 2;
    public bool Loop { get; init; }
    /// <summary>Loop in/out as a percentage of the duration (the floater's spinners). Used only when <see cref="Loop"/>.</summary>
    public double LoopInPercent { get; init; }
    public double LoopOutPercent { get; init; } = 100;
    /// <summary>Loop in/out in seconds; when set, these win over the percentages.</summary>
    public double? LoopInSeconds { get; init; }
    public double? LoopOutSeconds { get; init; }
    /// <summary>Seconds. Without looping, ease in + out longer than the animation are scaled down to fit (as the viewer does).</summary>
    public double EaseIn { get; init; } = 0.3;
    public double EaseOut { get; init; } = 0.3;
    /// <summary>Hand pose: floater label ("Relaxed", "Fist Left"), internal name ("Hands_Fist_L") or index 0-13. Null = Relaxed.</summary>
    public string? HandPose { get; init; }
    /// <summary>Facial expression: floater label ("Smile", "Big Smile") or internal name ("express_toothsmile"). Null/"" = none.</summary>
    public string? Emote { get; init; }
    /// <summary>Units of the BVH's position channels. The viewer always assumes inches (Poser, DAZ, Bandits, QAvimator).</summary>
    public BvhUnits PositionUnits { get; init; } = BvhUnits.Inches;
    /// <summary>
    /// Keep position channels on joints other than the hip. The viewer does (SL-318), which wrecks the
    /// avatar's proportions when a BVH carries offsets on every joint (most Mixamo/Blender exports);
    /// off by default, so only the hip moves.
    /// </summary>
    public bool JointPositions { get; init; }
    /// <summary>Extra BVH name -> SL bone mappings, checked first. Map to "" to leave a joint out (its rotation still folds into mapped children).</summary>
    public IReadOnlyDictionary<string, string>? JointAliases { get; init; }
}

public sealed record AnimResult(byte[] Data, AnimFile Anim, IReadOnlyList<string> Warnings);

/// <summary>
/// BVH -> SL animation asset, a port of the viewer's LLBVHLoader (load, translate, optimize,
/// serialize) as the upload floater uses it:
/// - frame 0 is the reference frame and isn't played; the last frame is where the one before it
///   interpolates to, so duration = (frames - 2) * frame time (at least one frame time);
/// - BVH is Y-up, Z-forward; SL is Z-up, X-forward: rotations are conjugated by that change of frame;
/// - the hip's rotation and position are relative to frame 0; other joints' rotations are absolute
///   (relative to the BVH rest pose, which should be SL's T-pose);
/// - positions are inches unless <see cref="AnimOptions.PositionUnits"/> says otherwise, clamped to +-5 m;
/// - keys that linear interpolation reproduces are dropped, with the viewer's thresholds, and joints
///   that never move away from frame 0 are left out.
/// Differences, all forgiving: brace/line layout is free; joints the viewer would reject (unknown
/// names) are dropped with a warning instead, and unmapped joints between mapped ones fold their
/// rotation into their mapped children; more source rigs' names are recognised (<see cref="BvhSkeleton"/>);
/// non-hip positions are off by default (<see cref="AnimOptions.JointPositions"/>); a joint that moves
/// only in the last frame is kept (the viewer drops it).
/// </summary>
public static class AnimConvert
{
    const float InchesToMeters = 0.02540005f;
    const float PositionKeyframeThresholdSq = 0.03f * 0.03f;
    const float RotationKeyframeThreshold = 0.01f;
    const float PositionMotionThresholdSq = 0.001f * 0.001f;
    const float RotationMotionThreshold = 0.001f;

    /// <summary>Hand poses by index (LLHandMotion gHandPoseNames) with the floater's labels.</summary>
    public static readonly IReadOnlyList<(string Label, string Name)> HandPoses =
    [
        ("Spread", ""), ("Relaxed", "Hands_Relaxed"), ("Point Both", "Hands_Point"), ("Fist", "Hands_Fist"),
        ("Relaxed Left", "Hands_Relaxed_L"), ("Point Left", "Hands_Point_L"), ("Fist Left", "Hands_Fist_L"),
        ("Relaxed Right", "Hands_Relaxed_R"), ("Point Right", "Hands_Point_R"), ("Fist Right", "Hands_Fist_R"),
        ("Salute Right", "Hands_Salute_R"), ("Typing", "Hands_Typing"), ("Peace Right", "Hands_Peace_R"),
        ("Spread Right", "Hands_Spread_R"),
    ];

    /// <summary>The floater's expressions: label -> name stored in the asset.</summary>
    public static readonly IReadOnlyDictionary<string, string> Emotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Aaaaah"] = "express_open_mouth", ["Afraid"] = "express_afraid_emote", ["Angry"] = "express_anger_emote",
        ["Big Smile"] = "express_toothsmile", ["Bored"] = "express_bored_emote", ["Cry"] = "express_cry_emote",
        ["Disdain"] = "express_disdain", ["Embarrassed"] = "express_embarrassed_emote", ["Frown"] = "express_frown",
        ["Kiss"] = "express_kiss", ["Laugh"] = "express_laugh_emote", ["Plllppt"] = "express_tongue_out",
        ["Repulsed"] = "express_repulsed_emote", ["Sad"] = "express_sad_emote", ["Shrug"] = "express_shrug_emote",
        ["Smile"] = "express_smile", ["Surprise"] = "express_surprise_emote", ["Wink"] = "express_wink_emote",
        ["Worry"] = "express_worry_emote",
    };

    public static byte[] BvhToAnim(string bvh, AnimOptions? options = null) => Convert(bvh, options).Data;

    public static AnimResult Convert(string bvh, AnimOptions? options = null)
    {
        var o = options ?? new AnimOptions();
        var warnings = new List<string>();
        int handPose = ResolveHandPose(o.HandPose);
        string emote = ResolveEmote(o.Emote);
        if (o.Priority is < 0 or > 6)
            throw new MediaFormatException($"Priority {o.Priority} is out of range; use 0-6 (the viewer offers 0-4, default 2).");

        var doc = Bvh.Parse(bvh, o.PositionUnits switch
        {
            BvhUnits.Centimeters => 0.01f / InchesToMeters,
            BvhUnits.Meters => 1f / InchesToMeters,
            _ => 1f,
        }, warnings);

        float duration = MathF.Max(doc.FrameCount - 2, 1) * doc.FrameTime;
        if (duration > AnimFile.MaxDuration)
            throw new MediaFormatException(
                $"The animation is {duration:0.##} s long ({doc.FrameCount} frames at {doc.FrameTime:0.####} s); SL's limit is {AnimFile.MaxDuration:0} s. " +
                "Trim frames or raise the frame time.");

        var tracks = MapJoints(doc, o, warnings);
        var joints = new List<AnimJoint>();
        foreach (var t in tracks)
        {
            if (!Optimize(t)) continue;
            joints.Add(Serialize(t, doc, duration, o.Priority));
        }
        if (joints.Count == 0)
            throw new MediaFormatException(
                "No joint moves relative to frame 0. SL treats the first frame as a reference pose and doesn't play it, " +
                "so a still pose needs two frames: a reference (e.g. the T-pose) and then the pose.");

        float loopIn = 0, loopOut = duration;
        if (o.Loop)
        {
            loopIn = (float)(o.LoopInSeconds ?? o.LoopInPercent / 100.0 * duration);
            loopOut = (float)(o.LoopOutSeconds ?? o.LoopOutPercent / 100.0 * duration);
            loopIn = Math.Clamp(loopIn, 0, duration);
            loopOut = Math.Clamp(loopOut, loopIn, duration);
        }
        float easeIn = MathF.Max((float)o.EaseIn, 0), easeOut = MathF.Max((float)o.EaseOut, 0);
        if (!o.Loop && easeIn + easeOut > duration && duration != 0)
        {
            float factor = duration / (easeIn + easeOut);
            easeIn *= factor;
            easeOut *= factor;
        }

        var anim = new AnimFile
        {
            Priority = o.Priority, Duration = duration, EmoteName = emote,
            LoopIn = loopIn, LoopOut = loopOut, Loop = o.Loop, EaseIn = easeIn, EaseOut = easeOut,
            HandPose = handPose, Joints = joints,
        };
        return new AnimResult(anim.ToBytes(), anim, warnings);
    }

    static int ResolveHandPose(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 1;
        s = s.Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < HandPoses.Count)
            return i;
        for (int k = 0; k < HandPoses.Count; k++)
            if (string.Equals(HandPoses[k].Label, s, StringComparison.OrdinalIgnoreCase) ||
                (HandPoses[k].Name != "" && string.Equals(HandPoses[k].Name, s, StringComparison.OrdinalIgnoreCase)))
                return k;
        throw new MediaFormatException(
            $"Unknown hand pose \"{s}\". Use one of: {string.Join(", ", HandPoses.Select(h => h.Label))} (or 0-{HandPoses.Count - 1}).");
    }

    static string ResolveEmote(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Trim() is "[None]" or "None" or "none") return "";
        s = s.Trim();
        if (Emotes.TryGetValue(s, out var name)) return name;
        var byName = Emotes.Values.FirstOrDefault(v => string.Equals(v, s, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;
        throw new MediaFormatException($"Unknown expression \"{s}\". Use one of: {string.Join(", ", Emotes.Keys)}.");
    }

    // One output joint: per-frame rotation (BVH frame, unmapped ancestors folded in) and position (inches).
    sealed class Track
    {
        public required string Name;
        public required int ChildTreeMaxDepth;
        public required bool IsPelvis;
        public required bool HasPositions;
        public required Quaternion[] Rot;
        public required Vector3[] Pos;
        public bool[] IgnoreRot = [];
        public bool[] IgnorePos = [];
    }

    static List<Track> MapJoints(Bvh doc, AnimOptions o, List<string> warnings)
    {
        var mapped = new string?[doc.Joints.Count];
        var used = new HashSet<string>();
        for (int i = 0; i < doc.Joints.Count; i++)
        {
            var j = doc.Joints[i];
            if (j.Parent == Bvh.Detached) continue;
            string? bone = BvhSkeleton.Map(j.Name, o.JointAliases);
            if (bone == "") continue;
            if (bone != null && !BvhSkeleton.IsBone(bone))
                throw new MediaFormatException($"Joint alias \"{j.Name}\" -> \"{bone}\" isn't an SL bone.");
            if (bone != null && !used.Add(bone))
            {
                warnings.Add($"\"{j.Name}\" maps to {bone} like an earlier joint; treated as unmapped.");
                bone = null;
            }
            mapped[i] = bone;
        }
        if (mapped[0] != "mPelvis")
            throw new MediaFormatException(
                $"The root joint \"{doc.Joints[0].Name}\" isn't the hip. SL needs the BVH's ROOT to be the hip " +
                "(named hip, Hips, mPelvis or similar); rename it or re-export with the hip as root.");

        var tracks = new List<Track>();
        var folded = new HashSet<int>();
        int frames = doc.FrameCount;
        for (int i = 0; i < doc.Joints.Count; i++)
        {
            if (mapped[i] == null) continue;
            var j = doc.Joints[i];
            var chain = new List<int>();
            for (int p = j.Parent; p >= 0 && mapped[p] == null; p = doc.Joints[p].Parent) chain.Add(p);
            folded.UnionWith(chain);

            var rot = new Quaternion[frames];
            for (int f = 0; f < frames; f++)
            {
                // Viewer order (row vectors, left applied first): own rotation, then each unmapped
                // ancestor's, nearest first. In System.Numerics that's the reverse product.
                var q = doc.Rot(i, f);
                foreach (int p in chain) q = doc.Rot(p, f) * q;
                rot[f] = q;
            }
            bool pelvis = i == 0;
            bool hasPos = j.HasPosition && (pelvis || o.JointPositions);
            var pos = new Vector3[frames];
            if (hasPos)
                for (int f = 0; f < frames; f++) pos[f] = doc.Pos(i, f);
            if (chain.Count > 0)
                warnings.Add($"{mapped[i]} ({j.Name}): folded in the rotation of unmapped {string.Join(", ", chain.Select(p => doc.Joints[p].Name))}.");
            tracks.Add(new Track
            {
                Name = mapped[i]!, ChildTreeMaxDepth = j.ChildTreeMaxDepth, IsPelvis = pelvis,
                HasPositions = hasPos, Rot = rot, Pos = pos,
            });
        }
        var dropped = Enumerable.Range(0, doc.Joints.Count)
            .Where(i => mapped[i] == null && !folded.Contains(i)).Select(i => doc.Joints[i].Name).ToList();
        if (dropped.Count > 0)
            warnings.Add($"Dropped joints with no SL bone: {string.Join(", ", dropped)}.");
        return tracks;
    }

    static Vector3 XAxisBy(Quaternion q) => Vector3.Transform(Vector3.UnitX, q);
    static Vector3 YAxisBy(Quaternion q) => Vector3.Transform(Vector3.UnitY, q);

    static float RotDelta(Quaternion a, Quaternion b) =>
        Vector3.Distance(XAxisBy(a), XAxisBy(b)) + Vector3.Distance(YAxisBy(a), YAxisBy(b));

    // LLQuaternion lerp(t, p, q): componentwise, normalized, no hemisphere flip.
    static Quaternion Lerp(float t, Quaternion p, Quaternion q) => Quaternion.Normalize(
        new Quaternion(t * q.X + (1 - t) * p.X, t * q.Y + (1 - t) * p.Y, t * q.Z + (1 - t) * p.Z, t * q.W + (1 - t) * p.W));

    /// <summary>LLBVHLoader::optimize for one joint. Returns false if the joint has no motion.</summary>
    static bool Optimize(Track t)
    {
        int n = t.Rot.Length;
        t.IgnoreRot = new bool[n];
        t.IgnorePos = new bool[n];
        bool posChanged = false, rotChanged = false;
        if (n == 0) return false;

        var firstPos = t.Pos[0];
        var firstRot = t.Rot[0];
        int start = 0;
        if (n == 1) rotChanged = true;
        else { t.IgnorePos[0] = t.IgnoreRot[0] = true; start = 1; }

        int prev = start, lastGoodPos = start, lastGoodRot = start;
        int numPosConsidered = 2, numRotConsidered = 2;
        float rotThreshold = RotationKeyframeThreshold / MathF.Max(t.ChildTreeMaxDepth * 0.33f, 1f);
        double diffMax = 0;
        int kiMax = start;

        for (int ki = start; ki < n; ki++)
        {
            if (prev == lastGoodPos)
            {
                if (Vector3.DistanceSquared(t.Pos[prev], firstPos) > PositionMotionThresholdSq) posChanged = true;
            }
            else
            {
                var test = t.Pos[prev];
                var lastGood = t.Pos[lastGoodPos];
                var current = t.Pos[ki];
                var interp = current + (lastGood - current) * (1f / numPosConsidered);
                if (Vector3.DistanceSquared(current, firstPos) > PositionMotionThresholdSq) posChanged = true;
                if (Vector3.DistanceSquared(interp, test) < PositionKeyframeThresholdSq)
                {
                    t.IgnorePos[prev] = true;
                    numPosConsidered++;
                }
                else
                {
                    numPosConsidered = 2;
                    lastGoodPos = prev;
                }
            }

            if (prev == lastGoodRot)
            {
                if (RotDelta(firstRot, t.Rot[prev]) > RotationMotionThreshold) rotChanged = true;
            }
            else
            {
                var test = t.Rot[prev];
                var interp = Lerp(1f / numRotConsidered, t.Rot[ki], t.Rot[lastGoodRot]);
                if (RotDelta(firstRot, test) > RotationMotionThreshold) rotChanged = true;
                float rotTest = RotDelta(interp, test);
                if (rotTest >= rotThreshold || (ki + 1 == n && numRotConsidered > 2))
                {
                    numRotConsidered = 2;
                    lastGoodRot = prev;
                    if (diffMax > 0)
                    {
                        t.IgnoreRot[kiMax] = false;
                        diffMax = 0;
                    }
                }
                else
                {
                    t.IgnoreRot[prev] = true;
                    numRotConsidered++;
                    if (rotTest > diffMax) { diffMax = rotTest; kiMax = ki; }
                }
            }
            prev = ki;
        }
        // The viewer never tests the last frame for motion, so a joint that moves only in the last
        // frame is dropped there even though that frame is played (as the end of the interpolation).
        if (n > 1)
        {
            if (Vector3.DistanceSquared(t.Pos[n - 1], firstPos) > PositionMotionThresholdSq) posChanged = true;
            if (RotDelta(firstRot, t.Rot[n - 1]) > RotationMotionThreshold) rotChanged = true;
        }
        return posChanged || rotChanged;
    }

    // BVH (x right, y up, z forward) -> SL (y left, z up, x forward): the viewer's frame matrix
    // rows (0,1,0) (0,0,1) (1,0,0), a 120 degree turn about (1,1,1).
    static readonly Quaternion FrameRot = new(0.5f, 0.5f, 0.5f, 0.5f);

    /// <summary>LLBVHLoader::serialize for one joint.</summary>
    static AnimJoint Serialize(Track t, Bvh doc, float duration, int priority)
    {
        var frameInv = Quaternion.Conjugate(FrameRot);
        var firstInv = t.IsPelvis ? Quaternion.Conjugate(t.Rot[0]) : Quaternion.Identity;
        float Time(int frame) => MathF.Max(frame - 1, 0) * doc.FrameTime;

        var rots = new List<AnimRotationKey>();
        for (int f = 0; f < t.Rot.Length; f++)
        {
            if (t.IgnoreRot[f]) continue;
            // Viewer: frameRotInv * inRot * ~first_frame_rot * frameRot (left applied first).
            var outRot = FrameRot * firstInv * t.Rot[f] * frameInv;
            rots.Add(new(Time(f), outRot));
        }

        var poss = new List<AnimPositionKey>();
        if (t.HasPositions)
        {
            var relKey = t.IsPelvis ? t.Pos[0] : Vector3.Zero;
            for (int f = 0; f < t.Pos.Length; f++)
            {
                if (t.IgnorePos[f]) continue;
                var p = Vector3.Transform(Vector3.Transform(t.Pos[f] - relKey, firstInv), FrameRot) * InchesToMeters;
                p = Vector3.Clamp(p, new Vector3(-AnimFile.MaxPelvisOffset), new Vector3(AnimFile.MaxPelvisOffset));
                poss.Add(new(Time(f), p));
            }
        }
        return new AnimJoint(t.Name, priority, rots, poss);
    }
}

/// <summary>A parsed BVH: hierarchy plus per-frame channel values.</summary>
internal sealed class Bvh
{
    public sealed class Joint
    {
        public required string Name;
        public required int Parent;
        public int ChildTreeMaxDepth;
        public int[] PosAxis = [];   // per position channel: 0/1/2 = X/Y/Z
        public int[] RotAxis = [];   // per rotation channel, in channel order
        public int[] ChannelKinds = []; // 0 = position, 1 = rotation, per channel
        public int ChannelStart;
        public bool HasPosition => PosAxis.Length > 0;
    }

    public const int Detached = -2;
    public List<Joint> Joints { get; } = [];
    public int FrameCount { get; private set; }
    public float FrameTime { get; private set; }
    int channelCount;
    float[] data = [];
    float posScale = 1f;

    public Quaternion Rot(int joint, int frame)
    {
        var j = Joints[joint];
        var q = Quaternion.Identity;
        int c = 0;
        // BVH channel order Zrotation Xrotation Yrotation means R = Rz * Rx * Ry (Y applied first);
        // the viewer's bvhStringToOrder + mayaQ does the same.
        for (int k = 0; k < j.ChannelKinds.Length; k++)
        {
            if (j.ChannelKinds[k] != 1) continue;
            float deg = data[frame * channelCount + j.ChannelStart + k];
            var axis = j.RotAxis[c++] switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
            q *= Quaternion.CreateFromAxisAngle(axis, deg * (MathF.PI / 180f));
        }
        return q;
    }

    public Vector3 Pos(int joint, int frame)
    {
        var j = Joints[joint];
        Span<float> v = stackalloc float[3];
        int c = 0;
        for (int k = 0; k < j.ChannelKinds.Length; k++)
        {
            if (j.ChannelKinds[k] != 0) continue;
            v[j.PosAxis[c++]] = data[frame * channelCount + j.ChannelStart + k] * posScale;
        }
        return new Vector3(v[0], v[1], v[2]);
    }

    /// <param name="posScale">Multiplier taking position channels to inches.</param>
    public static Bvh Parse(string text, float posScale, List<string> warnings)
    {
        var doc = new Bvh { posScale = posScale };
        var tok = new Tokens(text);
        if (!tok.TryNext(out var first) || !first.Equals("HIERARCHY", StringComparison.OrdinalIgnoreCase))
            throw new MediaFormatException("Not a BVH file: it should start with HIERARCHY.");

        var stack = new List<int>();
        int channels = 0;
        bool sawRoot = false;
        while (true)
        {
            if (!tok.TryNext(out var t)) throw new MediaFormatException("The BVH ends before its MOTION section.");
            if (t == "}")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            if (t.Equals("MOTION", StringComparison.OrdinalIgnoreCase)) break;
            if (t.Equals("End", StringComparison.OrdinalIgnoreCase))
            {
                tok.Expect("Site");
                tok.Expect("{");
                tok.Expect("OFFSET");
                for (int k = 0; k < 3; k++) tok.Float();
                tok.Expect("}");
                // End sites add no depth (the viewer starts their parent at 0).
                for (int d = 0, s = stack.Count - 1; s >= 0; s--, d++)
                    doc.Joints[stack[s]].ChildTreeMaxDepth = Math.Max(doc.Joints[stack[s]].ChildTreeMaxDepth, d);
                continue;
            }
            bool isRoot = t.Equals("ROOT", StringComparison.OrdinalIgnoreCase);
            if (!isRoot && !t.Equals("JOINT", StringComparison.OrdinalIgnoreCase))
                throw new MediaFormatException($"Unexpected \"{t}\" in the BVH hierarchy (line {tok.Line}); expected ROOT, JOINT, End Site or }}.");
            if (isRoot && sawRoot)
            {
                warnings.Add("The BVH has more than one ROOT; only the first hierarchy is used.");
            }
            sawRoot = true;
            string name = tok.Next("joint name");
            var joint = new Joint { Name = name, Parent = stack.Count > 0 ? stack[^1] : (doc.Joints.Count == 0 ? -1 : Detached) };
            for (int d = 1, s = stack.Count - 1; s >= 0; s--, d++)
                doc.Joints[stack[s]].ChildTreeMaxDepth = Math.Max(doc.Joints[stack[s]].ChildTreeMaxDepth, d);
            tok.Expect("{");
            tok.Expect("OFFSET");
            for (int k = 0; k < 3; k++) tok.Float();
            tok.Expect("CHANNELS");
            int n = tok.Int();
            if (n < 0 || n > 64) throw new MediaFormatException($"Joint {name} declares {n} channels (line {tok.Line}).");
            var kinds = new int[n];
            var posAxes = new List<int>();
            var rotAxes = new List<int>();
            for (int k = 0; k < n; k++)
            {
                var c = tok.Next("channel name");
                if (c.Length != 9 || (char.ToUpperInvariant(c[0]) is not ('X' or 'Y' or 'Z')))
                    throw new MediaFormatException($"Unknown channel \"{c}\" on joint {name} (line {tok.Line}).");
                int axis = char.ToUpperInvariant(c[0]) - 'X';
                string kind = c[1..].ToLowerInvariant();
                if (kind == "position") { kinds[k] = 0; posAxes.Add(axis); }
                else if (kind == "rotation") { kinds[k] = 1; rotAxes.Add(axis); }
                else throw new MediaFormatException($"Unknown channel \"{c}\" on joint {name} (line {tok.Line}).");
            }
            joint.ChannelKinds = kinds;
            joint.PosAxis = [.. posAxes];
            joint.RotAxis = [.. rotAxes];
            joint.ChannelStart = channels;
            channels += n;
            stack.Add(doc.Joints.Count);
            doc.Joints.Add(joint);
        }
        if (doc.Joints.Count == 0) throw new MediaFormatException("The BVH has no joints.");
        // Joints under extra ROOTs keep their channels but aren't used (parents precede children).
        for (int i = 0; i < doc.Joints.Count; i++)
            if (doc.Joints[i].Parent >= 0 && doc.Joints[doc.Joints[i].Parent].Parent == Detached)
                doc.Joints[i].Parent = Detached;

        tok.Expect("Frames:");
        int frames = tok.Int();
        tok.Expect("Frame");
        tok.Expect("Time:");
        float frameTime = tok.Float();
        if (frames < 1) throw new MediaFormatException("The BVH has no frames.");
        if (!(frameTime > 0) || !float.IsFinite(frameTime)) throw new MediaFormatException($"Frame Time {frameTime} isn't a positive number.");

        doc.channelCount = channels;
        var values = new List<float>((int)Math.Min((long)frames * channels, 1 << 22));
        while (values.Count < (long)frames * channels && tok.TryNext(out var v))
        {
            if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !float.IsFinite(x))
                throw new MediaFormatException($"Bad number \"{v}\" in the BVH motion data (line {tok.Line}).");
            values.Add(x);
        }
        int complete = channels == 0 ? frames : values.Count / channels;
        if (complete < frames)
        {
            if (complete < 1) throw new MediaFormatException($"The BVH declares {frames} frames but has no complete frame of motion data.");
            warnings.Add($"The BVH declares {frames} frames but only {complete} are complete; using those.");
            frames = complete;
        }
        doc.data = [.. values.Take(frames * channels)];
        doc.FrameCount = frames;
        doc.FrameTime = frameTime;
        return doc;
    }

    sealed class Tokens(string text)
    {
        int pos;
        public int Line { get; private set; } = 1;

        public bool TryNext(out string token)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            {
                if (text[pos] == '\n') Line++;
                pos++;
            }
            if (pos >= text.Length) { token = ""; return false; }
            int start = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos])) pos++;
            token = text[start..pos];
            return true;
        }

        public string Next(string what) =>
            TryNext(out var t) ? t : throw new MediaFormatException($"The BVH ends where a {what} was expected.");

        public void Expect(string word)
        {
            var t = Next($"\"{word}\"");
            // Tolerate "Frames:" split as "Frames :" and braces glued to names are handled by callers.
            if (word.EndsWith(':') && t.Equals(word[..^1], StringComparison.OrdinalIgnoreCase))
            {
                if (TryPeekColon()) return;
            }
            if (!t.Equals(word, StringComparison.OrdinalIgnoreCase))
                throw new MediaFormatException($"Expected \"{word}\" but found \"{t}\" (BVH line {Line}).");
        }

        bool TryPeekColon()
        {
            int save = pos, line = Line;
            if (TryNext(out var t) && t == ":") return true;
            pos = save; Line = line;
            return false;
        }

        public float Float()
        {
            var t = Next("number");
            return float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)
                ? f : throw new MediaFormatException($"Expected a number but found \"{t}\" (BVH line {Line}).");
        }

        public int Int()
        {
            var t = Next("integer");
            return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
                ? i : throw new MediaFormatException($"Expected an integer but found \"{t}\" (BVH line {Line}).");
        }
    }
}
