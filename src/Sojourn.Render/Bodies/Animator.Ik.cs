// The viewer's procedural IK motions on an avatar: LLEditingMotion (llcharacter/lleditingmotion.cpp: the left arm
// reaching toward what a received PointAt names) and LLKeyframeStandMotion (llcharacter/llkeyframestandmotion.cpp:
// the default stands' feet on the ground), both through LLJointSolverRP3 (JointSolverRP3.cs); and the local resets
// (LLVOAvatar::resetAnimations, Firestorm's Resync Animations). LGPL 2.1, Linden Research, Inc.
using System.Numerics;

namespace Sojourn.Render.Bodies;

/// <summary>Where the ground is under a point (the viewer's LLVOAvatar::getGround: the land or the avatar's foot plane,
/// whichever is met first going down), in the same frame as the point, with its normal.</summary>
public delegate (Vector3 Pos, Vector3 Normal) FootGround(Vector3 point);

public sealed partial class AvatarAnimator
{
    // --- the editing motion ------------------------------------------------------------------------------------------

    /// <summary>What a received PointAt names, in the pose's frame (World/Avatars.Effects.cs), set before each Evaluate;
    /// null with none. While it's set the left arm reaches for it.</summary>
    public Vector3? PointAt { get; set; }

    /// <summary>The avatar's pixel area last frame: the editing motion runs only at 500 or more
    /// (MIN_REQUIRED_PIXEL_AREA_EDITING).</summary>
    public float PixelArea { get; set; } = float.MaxValue;

    const int EditPriority = 2;              // EDITING_PRIORITY, LLJoint::HIGH_PRIORITY
    const float EditEaseOut = 0.5f;          // EDITING_EASEOUT_DURATION (ease in: none)
    const float EditMinArea = 500f;
    const float EditLagHalfLife = 0.1f;      // TARGET_LAG_HALF_LIFE
    static readonly Vector3 EditWristOffset = new(0, 0.2f, 0);
    static readonly Vector3 EditPole = new(-1, 1, 0);
    static readonly Vector3 EditBAxis = new(-0.682683f, 0, -0.730714f);
    static readonly Vector3 EditPlane = Vector3.Normalize(new Vector3(1, 1, 0));
    /// <summary>LLEditingMotion::sHandPose (HAND_POSE_RELAXED_R) at sHandPosePriority 3.</summary>
    const int EditHandPose = 7, EditHandPriority = 3;

    bool _editing, _editBased;
    Quaternion _editBaseShoulder, _editBaseElbow;
    double _editStop = double.NaN, _editNow = double.NaN;
    long _editOrder;
    Vector3 _lastSelect;
    float _editW;
    Quaternion _qEditShoulder = Quaternion.Identity, _qEditElbow = Quaternion.Identity;
    int _shoulderL = -2, _elbowL, _wristL, _collarL, _torso;

    /// <summary>LLEditingMotion::onUpdate on last frame's pose (the viewer's motions read their joints' world transforms
    /// before this frame's are made): a chain from the collar through shoulder, elbow and wrist (20 cm past it), the
    /// target folded in front of the body by the edit plane, solved with the viewer's pole and elbow axis, and eased a
    /// little toward last frame's rotations. Started at once (no ease in) when a PointAt arrives, eased out over half a
    /// second when it ends, toward the last point.</summary>
    void Edit(SkeletonDef sk, SkeletonPose pose, double now)
    {
        float dt = double.IsNaN(_editNow) ? 0 : (float)Math.Clamp(now - _editNow, 0, 0.1);
        _editNow = now;
        bool want = PointAt != null && PixelArea >= EditMinArea;
        if (want)
        {
            if (!_editing) { _editing = true; _editOrder = ++_order; }
            _editStop = double.NaN;
            _lastSelect = PointAt!.Value;
            _editW = 1;
        }
        else
        {
            if (_editing) { _editing = false; _editStop = now; }
            float u = double.IsNaN(_editStop) ? 0 : 1 - (float)(now - _editStop) / EditEaseOut;
            if (u <= 0) { _editW = 0; _editStop = double.NaN; return; }
            _editW = CubicStep(u);
        }
        if (_shoulderL == -2)
        {
            _shoulderL = sk["mShoulderLeft"];
            _elbowL = sk["mElbowLeft"];
            _wristL = sk["mWristLeft"];
            _torso = sk["mTorso"];
            _collarL = _shoulderL >= 0 ? sk.Joints[_shoulderL].Parent : -1;
        }
        if (_shoulderL < 0 || _elbowL < 0 || _wristL < 0 || _torso < 0 || _collarL < 0) { _editW = 0; return; }
        // onInitialize, the first time the motion runs on this avatar: the solver's base rotations are the arm's then
        // (its elbow axis is chosen for an arm bent as the stands hold it; a straight one bends the viewer's way too)
        if (!_editBased) (_editBaseShoulder, _editBaseElbow, _editBased) = (pose.Rot[_shoulderL], pose.Rot[_elbowL], true);

        var chain = new Rp3Chain
        {
            ParentPos = pose.WorldPos[_collarL], ParentRot = pose.WorldRot[_collarL], ParentScale = Vector3.One,
            PosA = pose.Pos[_shoulderL], PosB = pose.Pos[_elbowL], PosC = pose.Pos[_wristL] + EditWristOffset,
            ScaleA = Vector3.One, ScaleB = Vector3.One,
            BaseA = _editBaseShoulder, BaseB = _editBaseElbow,
        };
        var target = _lastSelect - chain.ParentPos;
        float dist = target.Length();
        if (!(dist > 1e-5f) || !float.IsFinite(dist)) target = Vector3.One;
        else
        {
            target /= dist;
            // what's behind the arm's side of the body is folded in front of it, and raised the further behind it is
            var plane = Vector3.Transform(EditPlane, pose.WorldRot[_torso]);
            float dot = Vector3.Dot(plane, target);
            if (dot < 0)
            {
                target += plane * (dot * 2f);
                target.Z += Math.Clamp(dot / -1f, 0, 1) * 5f;
                target = Vector3.Normalize(target);
            }
            target *= dist;
        }
        var goal = target + chain.ParentPos;
        if (goal == Vector3.Zero) return;
        var (shoulder, elbow) = JointSolverRP3.Solve(chain, goal, EditPole, EditBAxis);
        float amt = Interpolant(EditLagHalfLife, dt);
        _qEditShoulder = Quaternion.Normalize(Quaternion.Slerp(shoulder, pose.Rot[_shoulderL], amt));
        _qEditElbow = Quaternion.Normalize(Quaternion.Slerp(elbow, pose.Rot[_elbowL], amt));
    }

    /// <summary>The editing motion's joint states for the blend: shoulder and elbow as solved, the wrist and torso at
    /// rest (its torso state is added and never set), at high priority and its weight.</summary>
    void AddEditStates()
    {
        if (_editW <= 0 || _shoulderL < 0) return;
        void S(int j, Quaternion r) =>
            _states[j].Add(new JointState { Priority = EditPriority, Order = _editOrder, Weight = _editW, Rot = r });
        S(_shoulderL, _qEditShoulder);
        S(_elbowL, _qEditElbow);
        S(_wristL, Quaternion.Identity);
        S(_torso, Quaternion.Identity);
    }

    // --- the standing motion's feet ----------------------------------------------------------------------------------

    /// <summary>The ground under the feet for the standing motions (null: none, as for an animated object or the own
    /// body in the headset, whose feet the rig puts down), and where this frame's root (mRoot) is, in the pose's frame.</summary>
    public FootGround? Ground { get; set; }
    public Vector3 Root { get; set; }

    sealed class FootState
    {
        public int Frame;
        public Quaternion HipL, KneeL, HipR, KneeR;
    }

    static readonly Vector3 LegPole = Vector3.UnitX;
    static readonly Vector3 LegAxisL = new(0.05f, 1, 0), LegAxisR = new(-0.05f, 1, 0);
    int _pelvisJ = -2, _hipL, _kneeL, _ankleL, _hipR, _kneeR, _ankleR;

    /// <summary>LLKeyframeStandMotion::onUpdate for one of the default stands (stand, stand 1-4, crouch): after the
    /// keyframes are sampled, each leg from the hip is solved so its ankle reaches the ground under where the key pose
    /// puts it, from the leg's base pose (its key pose two updates after it started, as setupJoints takes it), and the
    /// ankle turned to the ground's slope. The viewer tracks the ground every frame (its lock once settled is never
    /// set); its first two updates leave the key pose alone.</summary>
    void StandIk(SkeletonDef sk, SkeletonPose pose, Sample smp)
    {
        if (Ground is not { } ground || Root == Vector3.Zero) return;
        if (_pelvisJ == -2)
        {
            _pelvisJ = sk["mPelvis"];
            (_hipL, _kneeL, _ankleL) = (sk["mHipLeft"], sk["mKneeLeft"], sk["mAnkleLeft"]);
            (_hipR, _kneeR, _ankleR) = (sk["mHipRight"], sk["mKneeRight"], sk["mAnkleRight"]);
        }
        if (_pelvisJ < 0 || _hipL < 0 || _kneeL < 0 || _ankleL < 0 || _hipR < 0 || _kneeR < 0 || _ankleR < 0) return;
        var rots = smp.Rots;
        // the motion must hold every joint the viewer's looks for (else onInitialize fails and nothing is solved)
        if (rots[_hipL] == null || rots[_kneeL] == null || rots[_ankleL] == null || rots[_hipR] == null || rots[_kneeR] == null || rots[_ankleR] == null) return;
        var st = smp.A.Foot ??= new FootState();
        if (st.Frame < 2) { st.Frame++; return; }
        if (st.Frame == 2)
        {
            (st.HipL, st.KneeL, st.HipR, st.KneeR) = (rots[_hipL]!.Value, rots[_kneeL]!.Value, rots[_hipR]!.Value, rots[_kneeR]!.Value);
            st.Frame++;
        }
        // the pelvis: the root's position plus the motion's own pelvis offset (unturned, as there), last frame's rotation
        var pelvisPos = Root + (smp.Poss[_pelvisJ] ?? Vector3.Zero);
        var pelvisRot = pose.WorldRot[_pelvisJ];
        Leg(_hipL, _kneeL, _ankleL, st.HipL, st.KneeL, LegAxisL);
        Leg(_hipR, _kneeR, _ankleR, st.HipR, st.KneeR, LegAxisR);

        void Leg(int hip, int knee, int ankle, Quaternion baseHip, Quaternion baseKnee, Vector3 axis)
        {
            var chain = new Rp3Chain
            {
                ParentPos = pelvisPos, ParentRot = pelvisRot, ParentScale = Vector3.One,
                PosA = pose.Pos[hip], PosB = pose.Pos[knee], PosC = pose.Pos[ankle],
                ScaleA = _restScale[hip], ScaleB = _restScale[knee],
                BaseA = baseHip, BaseB = baseKnee,
            };
            var keyAnkle = chain.World(rots[hip]!.Value, rots[knee]!.Value).C;
            var (at, normal) = ground(keyAnkle);
            if (!float.IsFinite(at.X + at.Y + at.Z)) return;
            var (hipRot, kneeRot) = JointSolverRP3.Solve(chain, at, LegPole, axis);
            var kneeW = chain.World(hipRot, kneeRot).Bw;
            var ankleW = Quaternion.Normalize(Quaternion.Concatenate(rots[ankle]!.Value, kneeW));
            // the ankle's forward kept, its up the ground's normal
            var up = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            var dir = Vector3.Transform(Vector3.UnitX, ankleW);
            var left = Vector3.Cross(up, dir);
            rots[hip] = hipRot;
            rots[knee] = kneeRot;
            if (left.LengthSquared() < 1e-10f) return;
            left = Vector3.Normalize(left);
            dir = Vector3.Cross(left, up);
            rots[ankle] = Quaternion.Normalize(Quaternion.Concatenate(Basis(dir, left), Quaternion.Inverse(kneeW)));
        }
    }

    // --- local resets ------------------------------------------------------------------------------------------------

    /// <summary>LLVOAvatar::resetAnimations (Reset Skeleton And Animations, for someone else): every motion flushed, so
    /// what the region lists starts again from now at the next look.</summary>
    public void ResetAnimations()
    {
        _active.Clear();
        _listRef = null;
        _synced = true;
        _editing = _editBased = false;
        _editW = 0;
        _editStop = double.NaN;
        PointAt = null;
    }

    /// <summary>Firestorm's Resync Animations: every playing animation stopped and started again now, in the order they
    /// were playing, so everyone dancing the same dance is in step.</summary>
    public void Resync(double now)
    {
        foreach (var a in _active.Values.OrderBy(a => a.Order))
        {
            a.Start = now;
            a.Order = ++_order;
            a.Foot = null;
        }
    }
}
