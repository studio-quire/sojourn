// The system avatar's definition (avatar_lad.xml + avatar_skeleton.xml + *.llm), loaded once per process the way
// the Second Life viewer reads it: llappearance/llavatarappearance.cpp (LLAvatarAppearance::initClass, parseXml*,
// LLAvatarXmlInfo), llvisualparam.cpp, llpolymorph.cpp (LLPolyMorphTargetInfo), llpolyskeletaldistortion.cpp,
// lldriverparam.cpp, llavatarappearancedefines.cpp (mesh -> bake). LGPL 2.1, Linden Research, Inc.
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Sojourn.Render.Bodies;

public enum ParamKind { Other, Morph, Skeleton, Driver }

public sealed class VolumeMorph
{
    public required string Volume;
    public int Joint = -1;
    public Vector3 Scale, Pos;
}

public sealed class BoneDistortion
{
    public required int Joint;
    public Vector3 Scale;
    public Vector3? Offset;
}

public sealed record DrivenRef(int Id, float Min1, float Max1, float Max2, float Min2);

/// <summary>One &lt;param&gt; element. Several elements may share an id (the viewer chains them): all get the weight.</summary>
public sealed class ParamDef
{
    public int Id, Group;
    public required string Name;
    public float Min, Max, Default;
    public int Sex = 3;                         // 1 female, 2 male, 3 both (LLVisualParamInfo)
    public ParamKind Kind;
    public MeshDef? Mesh;                       // morphs: the mesh section it sits in
    public LlmMorph? Morph;
    public bool Masked;
    public bool InvertMask;
    public readonly List<VolumeMorph> Volumes = new();
    public readonly List<BoneDistortion> Bones = new();
    public readonly List<DrivenRef> Driven = new();
}

/// <summary>A &lt;mesh&gt; entry: one LOD of one avatar mesh.</summary>
public sealed class MeshDef
{
    public required string Type;
    public int Lod;
    public required string File;
    public float MinPixelArea;                  // min_pixel_width squared (0.1 when absent)
    public required LlmMesh Mesh;
    /// <summary>The section holding this LOD's vertices and morphs: itself, or the LOD0 mesh it references.</summary>
    public MeshDef Owner = null!;
    public readonly List<ParamDef> Morphs = new();
    /// <summary>Joint render data (llavatarjointmesh.cpp setupJoint): palette slots in weight order. -1 is mRoot.</summary>
    public int[] PaletteJoints = Array.Empty<int>();
}

public sealed class AttachmentPointDef
{
    public int Id;
    public required string Name;
    public int Joint;
    public Vector3 Position;
    public Quaternion Rotation;
    public bool Hud;
}

public sealed class CharacterData
{
    static readonly Lazy<CharacterData> _shared = new(() => Load(Path.Combine(AppContext.BaseDirectory, "Character")));
    public static CharacterData Shared => _shared.Value;

    public SkeletonDef Skeleton = null!;
    public readonly List<ParamDef> Params = new();
    public readonly Dictionary<int, List<ParamDef>> ParamsById = new();
    /// <summary>Ids in wire order: group 0 or 3, ascending (the AvatarAppearance visual param block).</summary>
    public int[] Transmitted = Array.Empty<int>();
    public readonly Dictionary<int, (float Min, float Max, float Default)> Ranges = new();
    public readonly Dictionary<string, List<MeshDef>> Meshes = new();      // type -> LODs in file order
    public readonly Dictionary<int, AttachmentPointDef> AttachmentPoints = new();
    public readonly Dictionary<string, int> ParamIdByName = new(StringComparer.OrdinalIgnoreCase);

    public const int MaleParam = 80, HoverParam = 11001;

    public static CharacterData Load(string dir)
    {
        var cd = new CharacterData { Skeleton = SkeletonDef.Load(Path.Combine(dir, "avatar_skeleton.xml")) };
        var lad = XDocument.Load(Path.Combine(dir, "avatar_lad.xml")).Root!;
        var files = new Dictionary<string, LlmMesh>(StringComparer.OrdinalIgnoreCase);
        LlmMesh LoadFile(string name, LlmMesh? reference)
        {
            var key = reference == null ? name : name + "|" + reference.GetHashCode();
            if (!files.TryGetValue(key, out var m)) files[key] = m = LlmMesh.Load(Path.Combine(dir, name), reference);
            return m;
        }

        foreach (var el in lad.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "skeleton":
                    foreach (var ap in el.Elements("attachment_point"))
                    {
                        var joint = cd.Skeleton[(string)ap.Attribute("joint")!];
                        bool hud = ((string?)ap.Attribute("hud") ?? "") == "true";
                        // HUD points hang off mScreen, which the viewer makes in code (no skeleton joint): kept, jointless
                        if (joint < 0 && !hud) continue;
                        var def = new AttachmentPointDef
                        {
                            Id = (int)ap.Attribute("id")!,
                            Name = (string)ap.Attribute("name")!,
                            Joint = joint,
                            Position = SkeletonDef.Vec(ap, "position"),
                            Rotation = EulerQ(SkeletonDef.Vec(ap, "rotation")),
                            Hud = hud,
                        };
                        cd.AttachmentPoints[def.Id] = def;
                    }
                    foreach (var p in el.Elements("param")) cd.AddParam(p, null);
                    break;
                case "mesh":
                {
                    var type = (string)el.Attribute("type")!;
                    var file = (string)el.Attribute("file_name")!;
                    var refName = (string?)el.Attribute("reference");
                    if (!cd.Meshes.TryGetValue(type, out var lods)) cd.Meshes[type] = lods = new List<MeshDef>();
                    MeshDef? owner = refName != null ? lods.FirstOrDefault(l => string.Equals(l.File, refName, StringComparison.OrdinalIgnoreCase)) : null;
                    var mesh = LoadFile(file, owner?.Mesh);
                    float mpw = el.Attribute("min_pixel_width") is { } a ? F(a.Value) : -1;
                    var md = new MeshDef
                    {
                        Type = type, Lod = (int?)el.Attribute("lod") ?? 0, File = file, Mesh = mesh,
                        MinPixelArea = mpw < 0 ? 0.1f : mpw * mpw,
                    };
                    md.Owner = owner ?? md;
                    lods.Add(md);
                    foreach (var p in el.Elements("param")) cd.AddParam(p, md);
                    break;
                }
                case "global_color":
                case "layer_set":
                case "driver_parameters":
                    foreach (var p in el.Descendants("param")) cd.AddParam(p, null);
                    break;
                case "morph_masks":
                    foreach (var mk in el.Elements("mask"))
                    {
                        var name = (string)mk.Attribute("morph_name")!;
                        bool invert = ((string?)mk.Attribute("invert") ?? "") == "true";
                        foreach (var p in cd.Params.Where(p => p.Kind == ParamKind.Morph && p.Name == name))
                        {
                            p.Masked = true;
                            p.InvertMask = invert;
                        }
                    }
                    break;
            }
        }
        cd.Transmitted = cd.ParamsById.Where(kv => kv.Value[0].Group is 0 or 3).Select(kv => kv.Key).OrderBy(i => i).ToArray();
        foreach (var lods in cd.Meshes.Values)
            foreach (var md in lods)
                md.PaletteJoints = md.Mesh.HasWeights ? BuildPalette(cd.Skeleton, md.Owner.Mesh.JointNames) : Array.Empty<int>();
        return cd;
    }

    void AddParam(XElement el, MeshDef? mesh)
    {
        var p = new ParamDef
        {
            Id = (int)el.Attribute("id")!,
            Group = (int?)el.Attribute("group") ?? 0,
            Name = (string?)el.Attribute("name") ?? "",
            Min = el.Attribute("value_min") is { } mn ? F(mn.Value) : 0,
            Max = el.Attribute("value_max") is { } mx ? F(mx.Value) : 1,
            Default = el.Attribute("value_default") is { } df ? F(df.Value) : 0,
            Sex = ((string?)el.Attribute("sex")) switch { "male" => 2, "female" => 1, _ => 3 },
            Mesh = mesh,
        };
        if (el.Element("param_morph") is { } pm && mesh != null)
        {
            p.Kind = ParamKind.Morph;
            var morphs = mesh.Owner.Mesh.Morphs;
            if (!morphs.TryGetValue(p.Name, out p.Morph) && p.Name.EndsWith("_Driven"))
                morphs.TryGetValue(p.Name[..^"_Driven".Length], out p.Morph);
            foreach (var vm in pm.Elements("volume_morph"))
            {
                var name = (string)vm.Attribute("name")!;
                p.Volumes.Add(new VolumeMorph
                {
                    Volume = name, Joint = Skeleton[name],
                    Scale = SkeletonDef.Vec(vm, "scale"), Pos = SkeletonDef.Vec(vm, "pos"),
                });
            }
            mesh.Morphs.Add(p);
        }
        else if (el.Element("param_skeleton") is { } ps)
        {
            p.Kind = ParamKind.Skeleton;
            foreach (var b in ps.Elements("bone"))
            {
                int j = Skeleton[(string)b.Attribute("name")!];
                if (j < 0) continue;
                p.Bones.Add(new BoneDistortion
                {
                    Joint = j, Scale = SkeletonDef.Vec(b, "scale"),
                    Offset = b.Attribute("offset") != null ? SkeletonDef.Vec(b, "offset") : null,
                });
            }
        }
        else if (el.Element("param_driver") is { } pd)
        {
            p.Kind = ParamKind.Driver;
            foreach (var d in pd.Elements("driven"))
            {
                // LLDrivenEntryInfo defaults: min1 = driver min, max1 = max2 = min2 = driver max
                float min1 = d.Attribute("min1") is { } a1 ? F(a1.Value) : p.Min;
                float max1 = d.Attribute("max1") is { } a2 ? F(a2.Value) : p.Max;
                float max2 = d.Attribute("max2") is { } a3 ? F(a3.Value) : p.Max;
                float min2 = d.Attribute("min2") is { } a4 ? F(a4.Value) : p.Max;
                p.Driven.Add(new DrivenRef((int)d.Attribute("id")!, min1, max1, max2, min2));
            }
        }
        Params.Add(p);
        if (!ParamsById.TryGetValue(p.Id, out var list)) ParamsById[p.Id] = list = new List<ParamDef>();
        list.Add(p);
        Ranges.TryAdd(p.Id, (p.Min, p.Max, p.Default));
        if (p.Name.Length > 0) ParamIdByName.TryAdd(p.Name, p.Id);
    }

    /// <summary>LLAvatarJointMesh::setupJoint: walk the skeleton depth first; each skin joint J gets a slot, preceded
    /// by a slot for its nearest base ancestor unless the previous slot already is that ancestor. Weights index these
    /// slots (integer part), blending toward the next slot (fraction). -1 stands for mRoot.</summary>
    static int[] BuildPalette(SkeletonDef sk, string[] jointNames)
    {
        var skin = new HashSet<int>(jointNames.Select(n => sk[n]).Where(i => i >= 0));
        var slots = new List<int>();
        void Visit(int j)
        {
            if (skin.Contains(j))
            {
                int anc = BaseAncestor(sk, j);
                if (slots.Count == 0 || slots[^1] != anc) slots.Add(anc);
                slots.Add(j);
            }
            foreach (var c in sk.Joints[j].Children)
                if (sk.Joints[c].IsBone) Visit(c);
        }
        for (int i = 0; i < sk.NumBones; i++)
            if (sk.Joints[i].Parent < 0) Visit(i);
        return slots.ToArray();
    }

    public static int BaseAncestor(SkeletonDef sk, int j)
    {
        int p = sk.Joints[j].Parent;
        while (p >= 0 && !sk.Joints[p].IsBase) p = sk.Joints[p].Parent;
        return p;
    }

    /// <summary>Sum of pivots over the joint and its base ancestors (LLAvatarJointMesh totalSkinOffset); mRoot is 0.</summary>
    public static Vector3 TotalSkinOffset(SkeletonDef sk, int j)
    {
        var sum = Vector3.Zero;
        for (; j >= 0; j = sk.Joints[j].Parent)
            if (sk.Joints[j].IsBase) sum += sk.Joints[j].Pivot;
        return sum;
    }

    /// <summary>LLQuaternion::setQuat(roll, pitch, yaw) in degrees, as attachment points use it: the Hamilton
    /// product qx*qy*qz (rotate about Z first), the reverse of mayaQ's order.</summary>
    public static Quaternion EulerQ(Vector3 degrees)
    {
        const float d2r = MathF.PI / 180f;
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees.X * d2r);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees.Y * d2r);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees.Z * d2r);
        return qx * qy * qz;
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
}
