// Linden binary mesh (.llm) reader, as the Second Life viewer reads it: llappearance/llpolymesh.cpp
// (LLPolyMeshSharedData::loadMesh, setupLOD) and llpolymorph.cpp (LLPolyMorphData::loadBinary), plus the morph clones
// made at load (llpolyskeletaldistortion.cpp clone*). LGPL 2.1, Linden Research, Inc.
using System.Numerics;
using System.Text;

namespace Sojourn.Render.Bodies;

public sealed class LlmMorph
{
    public required string Name;
    public int[] Indices = Array.Empty<int>();
    public Vector3[] Coords = Array.Empty<Vector3>(), Normals = Array.Empty<Vector3>(), Binormals = Array.Empty<Vector3>();
    public Vector2[] TexCoords = Array.Empty<Vector2>();

    public LlmMorph Clone(string name) => new()
    {
        Name = name, Indices = Indices, Coords = (Vector3[])Coords.Clone(), Normals = (Vector3[])Normals.Clone(),
        Binormals = (Vector3[])Binormals.Clone(), TexCoords = (Vector2[])TexCoords.Clone(),
    };
}

public sealed class LlmMesh
{
    public bool HasWeights;
    public Vector3 Position, Scale = Vector3.One;
    public Quaternion Rotation = Quaternion.Identity;
    /// <summary>Base vertices (LOD meshes share their reference's arrays and use a prefix of them).</summary>
    public Vector3[] Coords = Array.Empty<Vector3>(), Normals = Array.Empty<Vector3>(), Binormals = Array.Empty<Vector3>();
    public Vector2[] TexCoords = Array.Empty<Vector2>();
    public float[] Weights = Array.Empty<float>();
    public int NumVertices;            // this mesh's vertex count (a prefix of the reference's for LODs)
    public uint[] Indices = Array.Empty<uint>();
    public string[] JointNames = Array.Empty<string>();
    public readonly Dictionary<string, LlmMorph> Morphs = new(StringComparer.Ordinal);
    /// <summary>Seam vertices: src -> dst, whose UV generateMask reads instead of src's.</summary>
    public readonly Dictionary<int, int> SharedVerts = new();
    public LlmMesh? Reference;

    public static LlmMesh Load(string path, LlmMesh? reference = null)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        var header = Encoding.ASCII.GetString(br.ReadBytes(24)).TrimEnd('\0');
        if (!header.StartsWith("Linden Binary Mesh 1.0")) throw new InvalidDataException($"{path}: not a Linden binary mesh");
        var m = new LlmMesh { Reference = reference };
        bool isLod = reference != null;
        byte hasWeights = br.ReadByte();
        br.ReadByte();   // hasDetailTexCoords: read and never stored by the viewer
        m.Position = ReadVec3(br);
        var rotDeg = ReadVec3(br);
        br.ReadByte();   // rotation order, forced to XYZ
        m.Rotation = SkeletonDef.MayaQ(rotDeg);
        m.Scale = ReadVec3(br);
        if (isLod)
        {
            m.HasWeights = reference!.HasWeights;
            m.Coords = reference.Coords; m.Normals = reference.Normals; m.Binormals = reference.Binormals;
            m.TexCoords = reference.TexCoords; m.Weights = reference.Weights;
            m.JointNames = reference.JointNames;
        }
        else
        {
            m.HasWeights = hasWeights > 0;
            int n = br.ReadUInt16();
            m.NumVertices = n;
            m.Coords = new Vector3[n]; m.Normals = new Vector3[n]; m.Binormals = new Vector3[n]; m.TexCoords = new Vector2[n];
            for (int i = 0; i < n; i++) m.Coords[i] = ReadVec3(br);
            for (int i = 0; i < n; i++) m.Normals[i] = ReadVec3(br);
            for (int i = 0; i < n; i++) m.Binormals[i] = ReadVec3(br);
            for (int i = 0; i < n; i++) m.TexCoords[i] = new Vector2(br.ReadSingle(), br.ReadSingle());
            if (m.HasWeights)
            {
                m.Weights = new float[n];
                for (int i = 0; i < n; i++) m.Weights[i] = br.ReadSingle();
            }
        }
        int faces = br.ReadUInt16();
        m.Indices = new uint[faces * 3];
        int maxIndex = 0;
        for (int i = 0; i < faces * 3; i++)
        {
            int v = br.ReadInt16();
            m.Indices[i] = (uint)v;
            maxIndex = Math.Max(maxIndex, v);
        }
        if (isLod)
        {
            m.NumVertices = maxIndex + 1;
            return m;
        }
        if (m.HasWeights)
        {
            int nj = br.ReadUInt16();
            m.JointNames = new string[nj];
            for (int i = 0; i < nj; i++) m.JointNames[i] = ReadName(br);
        }
        while (br.BaseStream.Position + 64 <= br.BaseStream.Length)
        {
            var name = ReadName(br);
            if (name == "End Morphs") break;
            int count = br.ReadInt32();
            var mo = new LlmMorph
            {
                Name = name, Indices = new int[count], Coords = new Vector3[count], Normals = new Vector3[count],
                Binormals = new Vector3[count], TexCoords = new Vector2[count],
            };
            for (int i = 0; i < count; i++)
            {
                mo.Indices[i] = (int)br.ReadUInt32();
                mo.Coords[i] = ReadVec3(br);
                mo.Normals[i] = ReadVec3(br);
                mo.Binormals[i] = ReadVec3(br);
                mo.TexCoords[i] = new Vector2(br.ReadSingle(), br.ReadSingle());
            }
            m.Morphs[name] = mo;
            AddClones(m, mo);
        }
        if (br.BaseStream.Position + 4 <= br.BaseStream.Length)
        {
            int remaps = br.ReadInt32();
            for (int i = 0; i < remaps && br.BaseStream.Position + 8 <= br.BaseStream.Length; i++)
            {
                int src = br.ReadInt32(), dst = br.ReadInt32();
                m.SharedVerts[src] = dst;
            }
        }
        return m;
    }

    // LLPolyMeshSharedData::loadMesh: the physics morphs are made from existing ones at load
    static void AddClones(LlmMesh m, LlmMorph mo)
    {
        switch (mo.Name)
        {
            case "Breast_Female_Cleavage":
            {
                var lr = mo.Clone("Breast_Physics_LeftRight_Driven");
                for (int i = 0; i < lr.Coords.Length; i++)
                {
                    // clone_morph_param_cleavage: x0.75, with y negated where the delta points to -y
                    var sc = mo.Coords[i].Y < 0 ? new Vector3(0.75f, -0.75f, 0.75f) : new Vector3(0.75f);
                    lr.Coords[i] = mo.Coords[i] * sc;
                    lr.Normals[i] = mo.Normals[i] * sc;
                    lr.Binormals[i] = mo.Binormals[i] * sc;
                }
                m.Morphs[lr.Name] = lr;
                var io = mo.Clone("Breast_Physics_InOut_Driven");
                m.Morphs[io.Name] = io;
                break;
            }
            case "Breast_Gravity":
                m.Morphs["Breast_Physics_UpDown_Driven"] = mo.Clone("Breast_Physics_UpDown_Driven");
                break;
            case "Big_Belly_Torso": Duplicate(m, mo, "Belly_Physics_Torso_UpDown_Driven", new Vector3(0, 0, 0.05f)); break;
            case "Big_Belly_Legs": Duplicate(m, mo, "Belly_Physics_Legs_UpDown_Driven", new Vector3(0, 0, 0.05f)); break;
            case "skirt_belly": Duplicate(m, mo, "Belly_Physics_Skirt_UpDown_Driven", new Vector3(0, 0, 0.05f)); break;
            case "Small_Butt":
                Duplicate(m, mo, "Butt_Physics_UpDown_Driven", new Vector3(0, 0, 0.05f));
                Duplicate(m, mo, "Butt_Physics_LeftRight_Driven", new Vector3(0, 0.03f, 0));
                break;
        }
    }

    // cloneMorphParamDuplicate / cloneMorphParamDirection: every delta replaced by a fixed displacement
    static void Duplicate(LlmMesh m, LlmMorph mo, string name, Vector3 dir)
    {
        var c = mo.Clone(name);
        for (int i = 0; i < c.Coords.Length; i++)
        {
            c.Coords[i] = dir;
            c.Normals[i] = Vector3.Zero;
            c.Binormals[i] = Vector3.Zero;
        }
        m.Morphs[name] = c;
    }

    static Vector3 ReadVec3(BinaryReader br) => new(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

    static string ReadName(BinaryReader br)
    {
        var b = br.ReadBytes(64);
        int z = Array.IndexOf(b, (byte)0);
        return Encoding.ASCII.GetString(b, 0, z < 0 ? b.Length : z);
    }
}
