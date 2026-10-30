// The avatar skeleton (avatar_skeleton.xml) and its joint transforms, as the Second Life viewer builds and composes
// them: llappearance/llavatarappearance.cpp (LLAvatarBoneInfo::parseXml, setupBone, buildSkeleton),
// llcharacter/lljoint.cpp, llmath/xform.cpp (LLXformMatrix::updateMatrix). LGPL 2.1, Linden Research, Inc.
//
// Conventions (System.Numerics, row vectors): a joint's world rotation is Concatenate(local, parentWorld)
// (LL's `local * parentWorld`); its world position is parentWorldPos + rotate(parentLocalScale * localPos,
// parentWorldRot): a parent's scale moves only its direct children (setScaleChildOffset), never grandchildren;
// a joint's own scale only scales what is skinned to it. World matrix = S(localScale) * R(worldRot) * T(worldPos).
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Sojourn.Render.Bodies;

public sealed class JointDef
{
    public required string Name;
    public required int Index;
    public int Parent = -1;            // -1: hangs off mRoot
    public bool IsBone;                // bone or collision volume
    public bool IsBase;                // support="base" (the system body mesh skins to these only)
    public Vector3 Pos, Scale = Vector3.One, Pivot, End;
    public Quaternion Rot = Quaternion.Identity;
    public string[] Aliases = Array.Empty<string>();
    public readonly List<int> Children = new();
}

public sealed class SkeletonDef
{
    /// <summary>Bones in file order (depth first, 0..NumBones-1), then collision volumes in file order.</summary>
    public readonly List<JointDef> Joints = new();
    public int NumBones { get; private set; }
    readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

    public int this[string name] => _byName.TryGetValue(name, out var i) ? i : -1;
    public bool TryGet(string name, out int index) => _byName.TryGetValue(name, out index);

    public static SkeletonDef Load(string path)
    {
        var doc = XDocument.Load(path);
        var def = new SkeletonDef();
        var root = doc.Root!;
        var volumes = new List<(XElement El, int Parent)>();
        void Walk(XElement el, int parent)
        {
            foreach (var c in el.Elements())
            {
                if (c.Name.LocalName == "bone")
                {
                    var j = Read(c, def.Joints.Count, parent, isBone: true);
                    def.Joints.Add(j);
                    if (parent >= 0) def.Joints[parent].Children.Add(j.Index);
                    Walk(c, j.Index);
                }
                else if (c.Name.LocalName == "collision_volume") volumes.Add((c, parent));
            }
        }
        Walk(root, -1);
        def.NumBones = def.Joints.Count;
        foreach (var (el, parent) in volumes)
        {
            var j = Read(el, def.Joints.Count, parent, isBone: false);
            def.Joints.Add(j);
            if (parent >= 0) def.Joints[parent].Children.Add(j.Index);
        }
        foreach (var j in def.Joints)
        {
            def._byName[j.Name] = j.Index;
        }
        foreach (var j in def.Joints)
            foreach (var a in j.Aliases) def._byName.TryAdd(a, j.Index);
        return def;
    }

    static JointDef Read(XElement el, int index, int parent, bool isBone)
    {
        var j = new JointDef
        {
            Name = (string)el.Attribute("name")!,
            Index = index,
            Parent = parent,
            IsBone = isBone,
            IsBase = ((string?)el.Attribute("support") ?? "base") == "base",
            Pos = Vec(el, "pos"),
            Scale = Vec(el, "scale"),
            End = el.Attribute("end") != null ? Vec(el, "end") : Vector3.Zero,
            Pivot = isBone ? Vec(el, "pivot") : Vector3.Zero,
            Aliases = ((string?)el.Attribute("aliases") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
        };
        var r = Vec(el, "rot");
        j.Rot = MayaQ(r);
        return j;
    }

    /// <summary>mayaQ(x, y, z, XYZ) in degrees: rotate about X, then Y, then Z (fixed axes).</summary>
    public static Quaternion MayaQ(Vector3 degrees)
    {
        const float d2r = MathF.PI / 180f;
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees.X * d2r);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees.Y * d2r);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees.Z * d2r);
        return Quaternion.Concatenate(Quaternion.Concatenate(qx, qy), qz);
    }

    public static Vector3 Vec(XElement el, string attr) => ParseVec((string?)el.Attribute(attr) ?? "0 0 0");

    public static Vector3 ParseVec(string s)
    {
        var p = s.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        float F(int i) => i < p.Length ? float.Parse(p[i], CultureInfo.InvariantCulture) : 0f;
        return new Vector3(F(0), F(1), F(2));
    }
}

/// <summary>One avatar's skeleton state: local transforms (rest + shape + animation) and the composed world
/// transforms, in the avatar's root frame or region space depending on the root given to <see cref="Compose"/>.</summary>
public sealed class SkeletonPose
{
    public readonly SkeletonDef Def;
    public readonly Vector3[] Pos, Scale, WorldPos;
    public readonly Quaternion[] Rot, WorldRot;
    public readonly Matrix4x4[] World;

    public SkeletonPose(SkeletonDef def)
    {
        Def = def;
        int n = def.Joints.Count;
        Pos = new Vector3[n]; Scale = new Vector3[n]; WorldPos = new Vector3[n];
        Rot = new Quaternion[n]; WorldRot = new Quaternion[n];
        World = new Matrix4x4[n];
    }

    /// <summary>World transforms from the local ones; mRoot (the pelvis's parent) at rootPos/rootRot with unit scale
    /// (or <paramref name="rootScale"/>, which scales only the offsets of mRoot's children, as a joint's scale does).</summary>
    public void Compose(Vector3 rootPos, Quaternion rootRot, float rootScale = 1)
    {
        var joints = Def.Joints;
        // file order is parent-before-child for bones; collision volumes come after all bones
        for (int i = 0; i < joints.Count; i++)
        {
            int p = joints[i].Parent;
            Vector3 pp, ps; Quaternion pr;
            if (p < 0) { pp = rootPos; pr = rootRot; ps = new Vector3(rootScale); }
            else { pp = WorldPos[p]; pr = WorldRot[p]; ps = Scale[p]; }
            WorldPos[i] = pp + Vector3.Transform(Pos[i] * ps, pr);
            WorldRot[i] = Quaternion.Normalize(Quaternion.Concatenate(Rot[i], pr));
            World[i] = Matrix4x4.CreateScale(Scale[i]) * Matrix4x4.CreateFromQuaternion(WorldRot[i]) * Matrix4x4.CreateTranslation(WorldPos[i]);
        }
    }
}
