namespace Sojourn.Core.Media;

/// <summary>
/// BVH joint names -> SL skeleton bones. The base table is the viewer's: every bone in
/// avatar_skeleton.xml under its own name, "avatar_" + name, and the Poser names in its
/// "aliases" attribute (hip, abdomen, chest, lCollar, ...). Added on top: Mixamo/CMU-style
/// names, DAZ Genesis bend bones, Poser fingers and toes.
/// </summary>
internal static class BvhSkeleton
{
    // avatar_skeleton.xml, in file order (133 bones).
    static readonly string[] Bones =
    [
        "mPelvis", "mSpine1", "mSpine2", "mTorso", "mSpine3", "mSpine4", "mChest", "mNeck", "mHead", "mSkull",
        "mEyeRight", "mEyeLeft", "mFaceRoot", "mFaceEyeAltRight", "mFaceEyeAltLeft", "mFaceForeheadLeft",
        "mFaceForeheadRight", "mFaceEyebrowOuterLeft", "mFaceEyebrowCenterLeft", "mFaceEyebrowInnerLeft",
        "mFaceEyebrowOuterRight", "mFaceEyebrowCenterRight", "mFaceEyebrowInnerRight", "mFaceEyeLidUpperLeft",
        "mFaceEyeLidLowerLeft", "mFaceEyeLidUpperRight", "mFaceEyeLidLowerRight", "mFaceEar1Left", "mFaceEar2Left",
        "mFaceEar1Right", "mFaceEar2Right", "mFaceNoseLeft", "mFaceNoseCenter", "mFaceNoseRight",
        "mFaceCheekLowerLeft", "mFaceCheekUpperLeft", "mFaceCheekLowerRight", "mFaceCheekUpperRight", "mFaceJaw",
        "mFaceChin", "mFaceTeethLower", "mFaceLipLowerLeft", "mFaceLipLowerRight", "mFaceLipLowerCenter",
        "mFaceTongueBase", "mFaceTongueTip", "mFaceJawShaper", "mFaceForeheadCenter", "mFaceNoseBase",
        "mFaceTeethUpper", "mFaceLipUpperLeft", "mFaceLipUpperRight", "mFaceLipCornerLeft", "mFaceLipCornerRight",
        "mFaceLipUpperCenter", "mFaceEyecornerInnerLeft", "mFaceEyecornerInnerRight", "mFaceNoseBridge",
        "mCollarLeft", "mShoulderLeft", "mElbowLeft", "mWristLeft",
        "mHandMiddle1Left", "mHandMiddle2Left", "mHandMiddle3Left", "mHandIndex1Left", "mHandIndex2Left",
        "mHandIndex3Left", "mHandRing1Left", "mHandRing2Left", "mHandRing3Left", "mHandPinky1Left",
        "mHandPinky2Left", "mHandPinky3Left", "mHandThumb1Left", "mHandThumb2Left", "mHandThumb3Left",
        "mCollarRight", "mShoulderRight", "mElbowRight", "mWristRight",
        "mHandMiddle1Right", "mHandMiddle2Right", "mHandMiddle3Right", "mHandIndex1Right", "mHandIndex2Right",
        "mHandIndex3Right", "mHandRing1Right", "mHandRing2Right", "mHandRing3Right", "mHandPinky1Right",
        "mHandPinky2Right", "mHandPinky3Right", "mHandThumb1Right", "mHandThumb2Right", "mHandThumb3Right",
        "mWingsRoot", "mWing1Left", "mWing2Left", "mWing3Left", "mWing4Left", "mWing4FanLeft",
        "mWing1Right", "mWing2Right", "mWing3Right", "mWing4Right", "mWing4FanRight",
        "mHipRight", "mKneeRight", "mAnkleRight", "mFootRight", "mToeRight",
        "mHipLeft", "mKneeLeft", "mAnkleLeft", "mFootLeft", "mToeLeft",
        "mTail1", "mTail2", "mTail3", "mTail4", "mTail5", "mTail6", "mGroin",
        "mHindLimbsRoot", "mHindLimb1Left", "mHindLimb2Left", "mHindLimb3Left", "mHindLimb4Left",
        "mHindLimb1Right", "mHindLimb2Right", "mHindLimb3Right", "mHindLimb4Right",
    ];

    static readonly (string Alias, string Bone)[] ViewerAliases =
    [
        ("hip", "mPelvis"), ("abdomen", "mTorso"), ("chest", "mChest"), ("neck", "mNeck"), ("head", "mHead"),
        ("figureHair", "mSkull"),
        ("lCollar", "mCollarLeft"), ("lShldr", "mShoulderLeft"), ("lForeArm", "mElbowLeft"), ("lHand", "mWristLeft"),
        ("rCollar", "mCollarRight"), ("rShldr", "mShoulderRight"), ("rForeArm", "mElbowRight"), ("rHand", "mWristRight"),
        ("lThigh", "mHipLeft"), ("lShin", "mKneeLeft"), ("lFoot", "mAnkleLeft"),
        ("rThigh", "mHipRight"), ("rShin", "mKneeRight"), ("rFoot", "mAnkleRight"),
    ];

    // Not in the viewer. Joints left unmapped between mapped ones are folded into their mapped
    // children (see AnimConvert), so e.g. Mixamo's Spine1 still bends the chest.
    static readonly (string Alias, string Bone)[] ExtraAliases =
    [
        // Mixamo / CMU / most game rigs (a "prefix:" like "mixamorig:" is stripped before lookup)
        ("Hips", "mPelvis"), ("Pelvis", "mPelvis"), ("Spine", "mTorso"), ("Spine2", "mChest"),
        ("Neck", "mNeck"), ("Head", "mHead"),
        ("LeftShoulder", "mCollarLeft"), ("LeftArm", "mShoulderLeft"), ("LeftForeArm", "mElbowLeft"), ("LeftHand", "mWristLeft"),
        ("RightShoulder", "mCollarRight"), ("RightArm", "mShoulderRight"), ("RightForeArm", "mElbowRight"), ("RightHand", "mWristRight"),
        ("LeftUpLeg", "mHipLeft"), ("LeftLeg", "mKneeLeft"), ("LeftFoot", "mAnkleLeft"), ("LeftToeBase", "mFootLeft"), ("LeftToe_End", "mToeLeft"),
        ("RightUpLeg", "mHipRight"), ("RightLeg", "mKneeRight"), ("RightFoot", "mAnkleRight"), ("RightToeBase", "mFootRight"), ("RightToe_End", "mToeRight"),
        // DAZ Genesis 3/8 (the twist and upper/lower halves fold into their children)
        ("abdomenLower", "mTorso"), ("chestLower", "mChest"), ("neckLower", "mNeck"),
        ("lShldrBend", "mShoulderLeft"), ("lForearmBend", "mElbowLeft"), ("lThighBend", "mHipLeft"),
        ("rShldrBend", "mShoulderRight"), ("rForearmBend", "mElbowRight"), ("rThighBend", "mHipRight"),
        // Poser toes
        ("lToe", "mFootLeft"), ("rToe", "mFootRight"),
    ];

    static readonly Dictionary<string, string> Exact = Build();
    static readonly Dictionary<string, string> Folded = FoldCase(Exact);

    static Dictionary<string, string> FoldCase(Dictionary<string, string> exact)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in exact) map.TryAdd(k, v);
        return map;
    }

    static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in Bones) { map[b] = b; map["avatar_" + b] = b; }
        foreach (var (a, b) in ViewerAliases) map[a] = b;
        foreach (var (a, b) in ExtraAliases) map.TryAdd(a, b);

        // Fingers: Mixamo LeftHandIndex1, Poser lIndex1 / lMid1 / lPinky1.
        string[] mixamo = ["Thumb", "Index", "Middle", "Ring", "Pinky"];
        string[] poser = ["Thumb", "Index", "Mid", "Ring", "Pinky"];
        for (int f = 0; f < 5; f++)
            for (int n = 1; n <= 3; n++)
                foreach (var (side, l, s) in new[] { ("Left", "Left", "l"), ("Right", "Right", "r") })
                {
                    string bone = $"mHand{mixamo[f]}{n}{side}";
                    map.TryAdd($"{l}Hand{mixamo[f]}{n}", bone);
                    map.TryAdd($"{s}{poser[f]}{n}", bone);
                }
        return map;
    }

    public static bool IsBone(string name) => Exact.TryGetValue(name, out var b) && b == name;

    /// <summary>The SL bone for a BVH joint name, or null. Extra aliases win over the built-in table.</summary>
    public static string? Map(string name, IReadOnlyDictionary<string, string>? extra)
    {
        if (extra != null && extra.TryGetValue(name, out var e)) return e;
        if (Exact.TryGetValue(name, out var b)) return b;
        int colon = name.LastIndexOf(':');
        if (colon >= 0 && colon < name.Length - 1)
        {
            var bare = name[(colon + 1)..];
            if (extra != null && extra.TryGetValue(bare, out e)) return e;
            if (Exact.TryGetValue(bare, out b)) return b;
            name = bare;
        }
        return Folded.TryGetValue(name, out b) ? b : null;
    }
}
