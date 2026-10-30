using System.Reflection;
using LibreMetaverse;
using Sojourn.Render.Bodies;
using Sojourn.Render.World;
using V3 = System.Numerics.Vector3;
using Q = System.Numerics.Quaternion;

static partial class Tool
{
    static readonly string Repo = FindRepo();

    static string FindRepo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "src", "Sojourn.Client"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }

    /// <summary>The logic, no grid: the viewer's IK solver, the editing motion a PointAt starts, the default stands'
    /// feet, lip sync's amplitudes and the head's lip morphs, animesh's scale fixup.</summary>
    public static int Check(string outDir)
    {
        int fails = 0;
        void Ok(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) fails++; }
        var animDir = Path.Combine(Repo, "local", "grid", "bin", "assets", "AnimationsAssetSet");
        if (!Directory.Exists(animDir)) { Console.WriteLine($"no local grid's animations at {animDir}"); return 1; }

        // the solver: from a chain bent past its fallback, goals within reach are reached and those beyond pointed at
        // (it gives up within ~2.5 degrees of its pole vector, leaving the base pose, as the viewer's does)
        var rng = new Random(7);
        var axis = V3.Normalize(new V3(-0.682683f, 0, -0.730714f));
        int inReach = 0, reached = 0, outReach = 0, pointed = 0;
        for (int i = 0; i < 2000; i++)
        {
            var chain = new Rp3Chain
            {
                ParentPos = new V3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()),
                ParentRot = Q.Normalize(new Q(rng.NextSingle() - .5f, rng.NextSingle() - .5f, rng.NextSingle() - .5f, rng.NextSingle() - .5f)),
                ParentScale = V3.One, PosA = new V3(0, 0.1f, 0), PosB = new V3(0, 0.3f, 0), PosC = new V3(0, 0.25f, 0),
                ScaleA = V3.One, ScaleB = V3.One, BaseA = Q.Identity, BaseB = Q.CreateFromAxisAngle(axis, 0.8f),
            };
            var a0 = chain.World(Q.Identity, Q.Identity).A;
            var dir = V3.Normalize(new V3(rng.NextSingle() - .5f, rng.NextSingle() - .5f, rng.NextSingle() - .5f));
            float reach = 0.1f + rng.NextSingle() * 0.7f;
            var goal = a0 + dir * reach;
            var (ra, rb) = JointSolverRP3.Solve(chain, goal, new V3(-1, 1, 0), axis);
            var c = chain.World(ra, rb).C;
            if (reach < 0.54f && reach > 0.06f) { inReach++; if (V3.Distance(c, goal) < 0.002f) reached++; }
            else if (reach >= 0.56f) { outReach++; if (MathF.Acos(Math.Clamp(V3.Dot(V3.Normalize(c - a0), dir), -1, 1)) < 0.01f) pointed++; }
        }
        Ok(reached >= inReach * 0.99, $"solver: {reached} of {inReach} goals in reach met within 2 mm");
        Ok(pointed >= outReach * 0.99, $"solver: {pointed} of {outReach} goals out of reach pointed at within 0.6 deg");

        // the editing motion on an avatar standing with the default stand
        var cd = CharacterData.Load(Path.Combine(outDir, "Character"));
        var sk = cd.Skeleton;
        var shape = new BodyShape(cd, null);
        var lib = new MotionLibrary(sk);
        var assets = new FileAssets(animDir);
        var root = new V3(128, 128, 25);
        var rot = Q.CreateFromAxisAngle(V3.UnitZ, 0.7f);
        var stand = new UUID("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f");
        int pending = 0;
        double t = 0;
        AvatarAnimator anim = null!;
        SkeletonPose pose = null!;
        void Frame() { t += 1 / 90.0; pending = 0; anim.Evaluate(shape, pose, lib, assets, t, ref pending); pose.Compose(root, rot); }
        void Standing()
        {
            anim = new AvatarAnimator(0);
            pose = new SkeletonPose(sk);
            t = 0;
            anim.Sync(new List<Animation> { new Animation { AnimationID = stand, AnimationSequence = 1 } }, 0);
            for (int i = 0; i < 400 && (i < 5 || pending > 0); i++) { Frame(); if (pending > 0) Thread.Sleep(5); }
            for (int i = 0; i < 5; i++) Frame();
        }
        Standing();
        int sh = sk["mShoulderLeft"], el = sk["mElbowLeft"], wr = sk["mWristLeft"];
        V3 Tip() => pose.WorldPos[wr] + V3.Transform(new V3(0, 0.2f, 0) * pose.Scale[el], pose.WorldRot[el]);
        var restTip = Tip();
        var fwd = V3.Transform(V3.UnitX, rot);
        var left = V3.Transform(V3.UnitY, rot);
        var target = pose.WorldPos[sh] + fwd * 0.45f + left * 0.15f + new V3(0, 0, 0.1f);
        for (int i = 0; i < 60; i++) { anim.PointAt = target; Frame(); }
        float d0 = V3.Distance(restTip, target), d1 = V3.Distance(Tip(), target);
        Ok(d1 < d0 * 0.3f, $"editing: the left hand goes to what's pointed at in front ({d0:F2} m at rest -> {d1 * 100:F0} cm; the viewer's solver is approximate there)");
        var behind = pose.WorldPos[sh] - fwd * 0.4f + left * 0.1f;
        for (int i = 0; i < 60; i++) { anim.PointAt = behind; Frame(); }
        float raised = Tip().Z - pose.WorldPos[sh].Z;
        Ok(raised > 0.2f, $"editing: a point behind is folded by the edit plane and raised ({raised:F2} m above the shoulder)");
        anim.PointAt = null;
        for (int i = 0; i < 20; i++) Frame();
        float mid = V3.Distance(Tip(), restTip);
        for (int i = 0; i < 60; i++) Frame();
        float w = (float)typeof(AvatarAnimator).GetField("_editW", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(anim)!;
        Ok(mid > 0.02f && w == 0, $"editing: eased out over half a second ({mid * 100:F0} cm from the stand at 0.22 s, weight {w} at 0.9 s)");

        // the default stand's feet
        Standing();
        int al = sk["mAnkleLeft"], ar = sk["mAnkleRight"], kl = sk["mKneeLeft"];
        float restAnkle = (pose.WorldPos[al].Z + pose.WorldPos[ar].Z) / 2;
        var kneeRest = pose.Rot[kl];
        float groundZ = restAnkle + 0.06f;
        anim.Root = root;
        anim.Ground = p => (new V3(p.X, p.Y, groundZ), V3.UnitZ);
        for (int i = 0; i < 10; i++) Frame();
        float errL = pose.WorldPos[al].Z - groundZ, errR = pose.WorldPos[ar].Z - groundZ;
        float bend = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Q.Dot(kneeRest, pose.Rot[kl])), 0, 1)) * 57.3f;
        Ok(MathF.Abs(errL) < 0.01f && MathF.Abs(errR) < 0.01f, $"stand: ankles on ground 6 cm higher ({errL * 100:F1}, {errR * 100:F1} cm; the knee {bend:F0} deg more bent)");
        var n = V3.Normalize(new V3(MathF.Sin(0.25f), 0, MathF.Cos(0.25f)));
        anim.Ground = p => (new V3(p.X, p.Y, groundZ), n);
        for (int i = 0; i < 5; i++) Frame();
        float off = MathF.Acos(Math.Clamp(V3.Dot(V3.Transform(V3.UnitZ, pose.WorldRot[al]), n), -1, 1)) * 57.3f;
        Ok(off < 0.5f, $"stand: the ankle lies on a 14 deg slope ({off:F2} deg off)");
        anim.Ground = p => (new V3(p.X, p.Y, restAnkle - 0.3f), V3.UnitZ);
        for (int i = 0; i < 5; i++) Frame();
        Ok(pose.WorldPos[al].Z > restAnkle - 0.3f, $"stand: ground out of reach, the leg reaches toward it ({(pose.WorldPos[al].Z - restAnkle) * 100:F1} cm)");
        anim.Ground = null;
        for (int i = 0; i < 3; i++) Frame();
        Ok(MathF.Abs(pose.WorldPos[al].Z - restAnkle) < 0.002f, "stand: nothing without a ground (animesh, the own body in the headset)");

        // lip sync: Avatars.OohAah's amplitudes, by reflection
        var avT = typeof(Avatars);
        var stT = avT.GetNestedType("LipState", BindingFlags.NonPublic)!;
        var oa = avT.GetMethod("OohAah", BindingFlags.NonPublic | BindingFlags.Static)!;
        var st = Activator.CreateInstance(stT, true);
        float maxJump = 0, prevO = 0, prevA = 0, maxO = 0, maxA = 0;
        bool closed = false;
        for (int i = 0; i <= 270; i++)
        {
            float? lvl = i < 180 ? (i % 20 < 10 ? 0.7f : 0.5f) : null;   // power jumping every 0.11 s, then silence
            var call = new object?[] { st, lvl, i / 90.0, 0f, 0f };
            bool on = (bool)oa.Invoke(null, call)!;
            float o = (float)call[3]!, a = (float)call[4]!;
            if (i > 0) maxJump = MathF.Max(maxJump, MathF.Max(MathF.Abs(o - prevO), MathF.Abs(a - prevA)));
            (prevO, prevA) = (o, a);
            maxO = MathF.Max(maxO, o);
            maxA = MathF.Max(maxA, a);
            if (i == 270) closed = !on && o == 0 && a == 0;
        }
        Ok(maxO > 0.3f && maxA > 0.3f, $"lip sync: mouths open with a voice (ooh to {maxO:F2}, aah to {maxA:F2})");
        Ok(maxJump < 0.25f, $"lip sync: smooth at 90 Hz (largest step a frame {maxJump:F2}; the viewer's whole steps reach 0.99)");
        Ok(closed, "lip sync: closed again a second after the voice stops");
        var head = cd.Meshes["headMesh"][0].Owner;
        var lips = head.Morphs.Where(p => p.Name.StartsWith("Lipsync_", StringComparison.OrdinalIgnoreCase) && p.Morph != null).ToList();
        Ok(lips.Count == 2, $"lip sync: the head's morphs ({string.Join(", ", lips.Select(p => $"{p.Name} {p.Morph!.Indices.Length} of {head.Mesh.NumVertices} vertices"))})");

        // animesh's scale fixup
        var rigT = avT.GetNestedType("Rig", BindingFlags.NonPublic)!;
        var fix = avT.GetMethod("ScaleFixup", BindingFlags.NonPublic | BindingFlags.Static)!;
        float Fixup(V3 min, V3 max, float meshSpan, float scale)
        {
            var rig = Activator.CreateInstance(rigT, true)!;
            rigT.GetProperty("Body")?.SetValue(rig, null);
            rigT.GetField("BoxMin")!.SetValue(rig, min);
            rigT.GetField("BoxMax")!.SetValue(rig, max);
            rigT.GetField("MeshSpan")!.SetValue(rig, meshSpan);
            rigT.GetField("Scale")!.SetValue(rig, scale);
            return (float)fix.Invoke(null, [rig])!;
        }
        float s1 = Fixup(V3.Zero, new V3(2, 1, 99.5f), 0, 1);
        float s2 = Fixup(V3.Zero, new V3(2 * s1, s1, 99.5f * s1), 0, s1);   // drawn at that scale: holds
        float s3 = Fixup(V3.Zero, new V3(1, 1, 2), 128, 1);
        float s4 = Fixup(V3.Zero, new V3(1, 1, 2), 20, 1);
        Ok(MathF.Abs(s1 - 0.64f) < 0.001f && MathF.Abs(s2 - s1) < 0.01f, $"animesh: 100 m across scaled to 64 m (x{s1:F3}, held at x{s2:F3})");
        Ok(MathF.Abs(s3 - 0.5f) < 0.001f && s4 == 1, $"animesh: a 128 m mesh on a small skeleton too (x{s3:F3}); under 64 m untouched (x{s4})");

        Console.WriteLine(fails == 0 ? "all passed" : $"{fails} failed");
        return fails;
    }

    sealed class FileAssets(string dir) : IAssetSource
    {
        public Task<byte[]?> GetTextureAsync(UUID id, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task<byte[]?> GetMeshAsync(UUID id, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task<byte[]?> GetAssetAsync(UUID id, AssetType type, CancellationToken ct)
        {
            var p = Path.Combine(dir, id + ".dat");
            return Task.FromResult(File.Exists(p) ? File.ReadAllBytes(p) : null);
        }
    }
}
