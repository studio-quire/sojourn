// Avatar physics, ported from the Second Life viewer: indra/newview/llphysicsmotion.cpp (LLPhysicsMotionController,
// LLPhysicsMotion::onUpdate, setParamValue). LGPL 2.1, Linden Research, Inc.
using LibreMetaverse;
using Sojourn.Render.Bodies;
using Sojourn.Render.Gpu;

namespace Sojourn.Render.World;

public sealed partial class Avatars
{
    // A physics wearable's breast, belly and butt movement. Six motions, each one driven param in [0, 1]: a spring
    // toward where the shape puts it, pushed by the acceleration of its joint (mChest, mPelvis) along one direction in
    // the joint's frame, by gravity, damping and drag, with the wearable's mass, gain, spring, damping, drag and maximum
    // effect. The driven params move collision volumes (LEFT_PEC, RIGHT_PEC, BELLY, BUTT: what fitted mesh bodies are
    // weighted to) and morph the system body. The volumes are moved every frame after the pose (leaves of the
    // skeleton, so only their own matrices change); the system body's vertices are morphed and uploaded again only while
    // a part they show moves, at most 30 times a second an avatar and two avatars a frame. With no physics wearable
    // (OpenSim sends no physics params) the maximum effects are 0 and nothing moves, as in the viewer.

    /// <summary>LLPhysicsMotionController's motions: driver, joint, direction, and the controller params by role.</summary>
    static readonly (string Driver, string Joint, Vector3 Dir, string Part, string Axis)[] PhysicsMotions =
    {
        ("Breast_Physics_InOut_Controller", "mChest", new(-1, 0, 0), "Breast", "InOut"),
        ("Breast_Physics_UpDown_Controller", "mChest", new(0, 0, 1), "Breast", "UpDown"),
        ("Breast_Physics_LeftRight_Controller", "mChest", new(0, -1, 0), "Breast", "LeftRight"),
        ("Butt_Physics_UpDown_Controller", "mPelvis", new(0, 0, -1), "Butt", "UpDown"),
        ("Butt_Physics_LeftRight_Controller", "mPelvis", new(0, -1, 0), "Butt", "LeftRight"),
        ("Belly_Physics_UpDown_Controller", "mPelvis", new(0, 0, -1), "Belly", "UpDown"),
    };

    const float PhysicsStepMax = 0.05f;   // TIME_ITERATION_STEP_MAX
    const float PhysicsLodFactor = 1f;    // RenderAvatarPhysicsLODFactor
    /// <summary>SOJOURN_TEST_AVATAR_PHYSICS=1: every avatar moves as if it wore a lively physics wearable (OpenSim sends
    /// none), for testing on the local grid.</summary>
    static readonly bool PhysicsTest = System.Environment.GetEnvironmentVariable("SOJOURN_TEST_AVATAR_PHYSICS") == "1";

    sealed class PhysicsMotion
    {
        public required int Driver, Joint;
        public required Vector3 Dir;
        public required int[] Driven;
        // Mass, Gravity, Spring, Gain, Damping, Drag, MaxEffect: param ids (-1: the controller's default)
        public required int[] Controls;
        public float Position, VelocityJoint, AccelerationJoint, Velocity, PositionLastUpdate;
        public Vector3 PositionWorld;
        public double LastTime;
        public bool Seen;
    }

    static readonly float[] ControlDefaults = { 0.2f, 0f, 0.1f, 10f, 0.05f, 0.15f, 0.1f };   // initDefaultController
    static readonly float[] TestControls = { 0.4f, 0f, 0.3f, 25f, 0.1f, 0.15f, 1f };

    sealed class BodyPhysics
    {
        public required PhysicsMotion[] Motions;
        /// <summary>Driven params' weights as physics sets them (param id -> weight).</summary>
        public readonly Dictionary<int, float> Weights = new();
        /// <summary>The weights the system body was last uploaded with, and when.</summary>
        public readonly Dictionary<int, float> Uploaded = new();
        public long UploadedAt;
        public MeshVertex[] Staging = Array.Empty<MeshVertex>();
        public BodyShape? For;
    }

    int _physicsUploadsFrame;
    long _physicsUploadFrameTick = -1;

    BodyPhysics? MakePhysics()
    {
        var cd = _cd;
        var motions = new List<PhysicsMotion>();
        foreach (var (driver, joint, dir, part, axis) in PhysicsMotions)
        {
            if (!cd.ParamIdByName.TryGetValue(driver, out int did) || !cd.Skeleton.TryGet(joint, out int j)) return null;
            int Id(string name) => cd.ParamIdByName.TryGetValue(name, out int id) ? id : -1;
            motions.Add(new PhysicsMotion
            {
                Driver = did, Joint = j, Dir = dir,
                Driven = cd.ParamsById[did][0].Driven.Select(d => d.Id).Where(cd.ParamsById.ContainsKey).ToArray(),
                Controls = new[]
                {
                    Id($"{part}_Physics_Mass"), Id($"{part}_Physics_Gravity"), Id($"{part}_Physics_{axis}_Spring"),
                    Id($"{part}_Physics_{axis}_Gain"), Id($"{part}_Physics_{axis}_Damping"), Id($"{part}_Physics_Drag"),
                    Id($"{part}_Physics_{axis}_Max_Effect"),
                },
            });
        }
        return new BodyPhysics { Motions = motions.ToArray() };
    }

    /// <summary>After the body is posed this frame: physics stepped, its collision volumes moved, and the system body's
    /// morphs uploaded again when they've moved enough. Off: whatever it had moved is put back.</summary>
    void ApplyPhysics(Body body, BodyShape shape, Avatar av, double now)
    {
        if (!(_world.Quality?.AvatarPhysics ?? true))
        {
            if (body.Physics is { } was && was.Uploaded.Count > 0) Upload(body);
            body.Physics = null;
            return;
        }
        var ph = body.Physics ??= MakePhysics();
        if (ph == null) return;
        if (!ReferenceEquals(ph.For, shape))
        {
            // a new shape is uploaded without physics (Upload): what's on the GPU now is at rest
            ph.For = shape;
            ph.Uploaded.Clear();
        }
        var pose = body.Pose;
        foreach (var m in ph.Motions) StepMotion(m, ph, shape, pose, now);
        if (PhysicsTest) LogPhysics(av, ph, now);

        // the driven params' change from the shape's own weights, by sex as the shape applies them
        int sex = shape.Male ? 2 : 1;
        var sk = _cd.Skeleton;
        bool meshMoved = false;
        foreach (var (id, w) in ph.Weights)
        {
            foreach (var p in _cd.ParamsById[id])
            {
                if (p.Kind != ParamKind.Morph || (p.Sex & sex) == 0) continue;
                float dw = w - (shape.Weights.TryGetValue(id, out var sw) ? sw : p.Default);
                if (dw == 0) continue;
                foreach (var vm in p.Volumes)
                {
                    int j = vm.Joint;
                    if (j < 0 || sk.Joints[j].Parent < 0) continue;
                    int parent = sk.Joints[j].Parent;
                    // a collision volume is a leaf: moving it changes its own matrices alone
                    pose.WorldPos[j] += Vector3.Transform(vm.Pos * dw * pose.Scale[parent], pose.WorldRot[parent]);
                    var scale = pose.Scale[j] + vm.Scale * dw;
                    pose.World[j] = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(pose.WorldRot[j]) * Matrix4x4.CreateTranslation(pose.WorldPos[j]);
                }
                if (p.Morph != null && p.Mesh != null && ShowsMesh(body, av, p.Mesh.Type)
                    && MathF.Abs(w - (ph.Uploaded.TryGetValue(id, out var uw) ? uw : sw)) > 0.004f * MathF.Max(1f, p.Max - p.Min))
                    meshMoved = true;
            }
        }
        if (!meshMoved) return;
        long tick = System.Environment.TickCount64;
        if (tick - ph.UploadedAt < 33) return;
        if (_physicsUploadFrameTick != _frameTick) (_physicsUploadFrameTick, _physicsUploadsFrame) = (_frameTick, 0);
        if (_physicsUploadsFrame >= 2) return;
        _physicsUploadsFrame++;
        ph.UploadedAt = tick;
        UploadPhysics(body, shape, ph, sex);
    }

    // SOJOURN_TEST_AVATAR_PHYSICS: each driven param's range over the last few seconds, in the log
    readonly Dictionary<(UUID, int), (float Min, float Max)> _physicsSeen = new();
    double _physicsLogAt;

    void LogPhysics(Avatar av, BodyPhysics ph, double now)
    {
        foreach (var (id, w) in ph.Weights)
            _physicsSeen[(av.ID, id)] = _physicsSeen.TryGetValue((av.ID, id), out var r) ? (MathF.Min(r.Min, w), MathF.Max(r.Max, w)) : (w, w);
        if (now - _physicsLogAt < 3) return;
        _physicsLogAt = now;
        _world.Log?.Invoke("avatar physics: " + string.Join(", ", _physicsSeen.Select(kv =>
            $"{kv.Key.Item1.ToString()[..8]} {_cd.ParamsById[kv.Key.Item2][0].Name} {kv.Value.Min:0.###}..{kv.Value.Max:0.###}")));
        _physicsSeen.Clear();
    }

    /// <summary>Whether a system mesh section is drawn on this avatar (not hidden by what it wears, its bake visible).</summary>
    bool ShowsMesh(Body body, Avatar av, string type)
    {
        for (int mi = 0; mi < MeshOrder.Length; mi++)
            if (MeshOrder[mi].Type == type)
                return (body.Hidden & (1 << mi)) == 0 && Visible(BakeId(av, MeshOrder[mi].Te));
        return false;
    }

    /// <summary>LLPhysicsMotion::onUpdate: the driven params' weights for this moment.</summary>
    void StepMotion(PhysicsMotion m, BodyPhysics ph, BodyShape shape, SkeletonPose pose, double now)
    {
        float Control(int i) =>
            PhysicsTest ? TestControls[i] : m.Controls[i] >= 0 && shape.Weights.TryGetValue(m.Controls[i], out var w) ? w : ControlDefaults[i];
        var jointPos = pose.WorldPos[m.Joint];
        var jointRot = pose.WorldRot[m.Joint];
        if (!m.Seen)
        {
            // (the viewer starts from the origin, a jolt as each avatar appears; here from where the joint is)
            m.Seen = true;
            m.PositionWorld = jointPos;
            m.Position = 0.5f;
            m.PositionLastUpdate = 0.5f;
        }
        if (m.LastTime == 0 || m.LastTime >= now) { m.LastTime = now; return; }
        float timeDelta = (float)(now - m.LastTime);
        if (timeDelta > 1.0f) { m.LastTime = now; m.PositionWorld = jointPos; return; }   // under 1 fps: not worth it

        float mass = Control(0), gravity = Control(1), spring = Control(2), gain = Control(3), damping = Control(4), drag = Control(5), maxEffect = Control(6);
        var dp = _cd.ParamsById[m.Driver][0];
        float driverWeight = shape.Weights.TryGetValue(m.Driver, out var dwt) ? dwt : dp.Default;
        float positionUser = (driverWeight - dp.Min) / (dp.Max - dp.Min);

        Vector3 dirWorld = Vector3.Normalize(Vector3.Transform(m.Dir, jointRot));
        float ToLocal(Vector3 world) => Vector3.Dot(world, dirWorld);

        // velocity and acceleration in param space (positions x100, time x30, as the viewer has them)
        const float jointLocalFactor = 30f;
        float velocityJoint = ToLocal((jointPos - m.PositionWorld) * 100f) / (timeDelta * jointLocalFactor);
        const float smoothing = 3f;
        float accelerationJoint = (velocityJoint - m.VelocityJoint) / (timeDelta * jointLocalFactor) * (1f / smoothing)
                                  + m.AccelerationJoint * (smoothing - 1f) / smoothing;

        int steps = (int)(timeDelta / PhysicsStepMax) + 1;
        float step = timeDelta / steps;
        for (int i = 0; i < steps; i++)
        {
            float positionCurrent = Math.Clamp(m.Position, 0f, 1f);
            if (maxEffect == 0 && positionCurrent == positionUser) break;
            float forceSpring = -(positionCurrent - positionUser) * spring;
            float forceAccel = gain * (accelerationJoint * mass);
            float forceGravity = ToLocal(Vector3.UnitZ) * gravity * mass;
            float forceDamping = -damping * m.Velocity;
            float forceDrag = (float)(0.5 * drag * velocityJoint * velocityJoint * (velocityJoint >= 0 ? 1 : -1));
            float forceNet = forceAccel + forceGravity + forceSpring + forceDamping + forceDrag;
            float accelerationNew = forceNet / mass;
            float velocityNew = Math.Clamp(m.Velocity + accelerationNew * step, -100f, 100f);
            float positionNew = positionCurrent + velocityNew * step;
            if (maxEffect == 0) positionNew = positionUser;
            if ((positionNew < 0 && velocityNew < 0) || (positionNew > 1 && velocityNew > 0)) velocityNew = 0;
            if (float.IsNaN(m.Position) || float.IsNaN(m.Velocity) || float.IsNaN(positionNew))
            {
                positionNew = 0;
                m.Velocity = m.VelocityJoint = m.AccelerationJoint = m.Position = 0;
                m.PositionWorld = jointPos;
            }
            float clamped = Math.Clamp(positionNew, 0f, 1f);
            // setParamValue: [0, 1] into the middle maxEffect of each driven param's range
            float minVal = 0.5f - maxEffect / 2f, maxVal = 0.5f + maxEffect / 2f;
            float rescaled = minVal + (maxVal - minVal) * clamped;
            foreach (int id in m.Driven)
            {
                var (lo, hi, _) = _cd.Ranges[id];
                ph.Weights[id] = lo + (hi - lo) * rescaled;
            }
            if (MathF.Abs(m.PositionLastUpdate - clamped) > (1.0001f - PhysicsLodFactor) * 0.4f) m.PositionLastUpdate = positionNew;
            m.Velocity = velocityNew;
            m.AccelerationJoint = accelerationJoint;
            m.Position = positionNew;
        }
        m.LastTime = now;
        m.PositionWorld = jointPos;
        m.VelocityJoint = velocityJoint;
    }

    /// <summary>The system body's vertices with the physics morphs' change from the shape applied, uploaded as Upload does.</summary>
    void UploadPhysics(Body body, BodyShape shape, BodyPhysics ph, int sex)
    {
        int total = 0;
        foreach (var (_, mm) in shape.Morphed) total += mm.Coords.Length;
        if (ph.Staging.Length < total) ph.Staging = new MeshVertex[total];
        var all = ph.Staging;
        Release(body);
        body.Offsets.Clear();
        int at = 0;
        foreach (var (md, mm) in shape.Morphed)
        {
            body.Offsets[md] = (uint)at;
            for (int i = 0; i < mm.Coords.Length; i++)
                all[at + i] = new MeshVertex { Position = mm.Coords[i], Normal = mm.Normals[i], Tangent = new Vector4(1, 0, 0, 1), Uv = mm.TexCoords[i] };
            foreach (var (id, w) in ph.Weights)
                foreach (var p in _cd.ParamsById[id])
                {
                    if (p.Morph is not { } mo || p.Mesh?.Owner != md || (p.Sex & sex) == 0) continue;
                    float dw = w - (shape.Weights.TryGetValue(id, out var sw) ? sw : p.Default);
                    if (dw == 0) continue;
                    for (int k = 0; k < mo.Indices.Length; k++)
                    {
                        int v = mo.Indices[k];
                        if (v >= mm.Coords.Length) continue;
                        ref var o = ref all[at + v];
                        o.Position += mo.Coords[k] * dw;
                        var n = o.Normal + mo.Normals[k] * (dw * 0.65f);   // NORMAL_SOFTEN_FACTOR
                        if (n.LengthSquared() > 0) o.Normal = Vector3.Normalize(n);
                        o.Uv += mo.TexCoords[k] * dw;
                    }
                }
            at += mm.Coords.Length;
        }
        body.Verts = _world.Gpu.SkinVertices.Add<MeshVertex>(all.AsSpan(0, total));
        ph.Uploaded.Clear();
        foreach (var (id, w) in ph.Weights) ph.Uploaded[id] = w;
    }
}
