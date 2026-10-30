// An avatar's shape from its visual params, as the Second Life viewer computes it: weights from the appearance
// message (llvoavatar.cpp parseAppearanceMessage, llquantize.h U8_to_F32), driven params (lldriverparam.cpp
// getDrivenWeight), skeletal distortion (llpolyskeletaldistortion.cpp apply), morph targets and volume morphs
// (llpolymorph.cpp LLPolyMorphTarget::apply, applyMask, generateMask), body size (llavatarappearance.cpp
// computeBodySize). LGPL 2.1, Linden Research, Inc.
//
// The viewer applies params incrementally from an all-zero start; everything here is linear in the weights, so the
// closed form (base + sum of effective weight x delta) gives the same result.
using System.Numerics;

namespace Sojourn.Render.Bodies;

/// <summary>Morphed vertex arrays of one mesh section (LOD0 meshes; LODs that reference it use a prefix).</summary>
public sealed class MorphedMesh
{
    public required MeshDef Def;
    public required Vector3[] Coords, Normals;
    public required Vector2[] TexCoords;
    /// <summary>The head's normals before they're normalised (the morphs' sum), for lip sync's morphs on top of them
    /// (World/Avatars.LipSync.cs); null for the other meshes.</summary>
    public Vector3[]? ScaledNormals;
}

public sealed class BodyShape
{
    public readonly CharacterData Data;
    public readonly Dictionary<int, float> Weights = new();
    public bool Male;
    public Vector3[] JointPos, JointScale;
    public float PelvisToFoot, BodyHeight, Hover;
    public readonly Dictionary<MeshDef, MorphedMesh> Morphed = new();

    /// <summary>Build a shape. <paramref name="animated"/>: weights of animated morphs (hand poses, expressions)
    /// that override the defaults. <paramref name="masks"/>: the head/upper/lower bakes' morph masks (aux
    /// channel, bottom-up rows) by body region ("head", "upper_body", "lower_body"); masked morphs wait for theirs.</summary>
    public BodyShape(CharacterData data, byte[]? visualParams, IReadOnlyDictionary<int, float>? animated = null,
        IReadOnlyDictionary<string, MorphMask>? masks = null)
    {
        Data = data;
        foreach (var (id, r) in data.Ranges) Weights[id] = r.Default;
        if (visualParams != null)
            for (int i = 0; i < visualParams.Length && i < data.Transmitted.Length; i++)
            {
                int id = data.Transmitted[i];
                var (min, max, _) = data.Ranges[id];
                Weights[id] = U8ToF32(visualParams[i], min, max);
            }
        if (animated != null)
            foreach (var (id, w) in animated) Weights[id] = w;
        foreach (var id in Weights.Keys.ToList())
        {
            var (min, max, _) = data.Ranges[id];
            Weights[id] = Math.Clamp(Weights[id], min, max);
        }
        // drivers in ascending id order set their driven params at once (LLDriverParam::setWeight)
        foreach (var (id, list) in data.ParamsById.OrderBy(kv => kv.Key))
        {
            var drv = list[0];
            if (drv.Kind != ParamKind.Driver) continue;
            float w = Weights[id];
            foreach (var d in drv.Driven)
            {
                if (!data.Ranges.TryGetValue(d.Id, out var dr)) continue;
                Weights[d.Id] = DrivenWeight(d, w, drv.Min, drv.Max, dr.Min, dr.Max);
            }
        }
        Male = Weights.TryGetValue(CharacterData.MaleParam, out var male) && male > 0.5f;
        Hover = Weights.TryGetValue(CharacterData.HoverParam, out var hv) ? hv : 0;

        var sk = data.Skeleton;
        int n = sk.Joints.Count;
        JointPos = new Vector3[n];
        JointScale = new Vector3[n];
        for (int i = 0; i < n; i++) { JointPos[i] = sk.Joints[i].Pos; JointScale[i] = sk.Joints[i].Scale; }
        // the pelvis sits at mRoot (buildCharacter / the pelvis-fix motion); its XML pos is only its skin pivot
        if (sk.TryGet("mPelvis", out var pelvis)) JointPos[pelvis] = Vector3.Zero;

        int sex = Male ? 2 : 1;
        float Effective(ParamDef p) => (p.Sex & sex) != 0 ? Weights[p.Id] : p.Default;

        foreach (var p in data.Params)
        {
            if (p.Kind != ParamKind.Skeleton) continue;
            float w = Effective(p);
            if (w == 0) continue;
            foreach (var b in p.Bones)
            {
                JointScale[b.Joint] += w * b.Scale;
                if (b.Offset is { } off) JointPos[b.Joint] += w * off;
                // collision volumes inherit the scale change, relative to their own default scale
                foreach (var c in sk.Joints[b.Joint].Children)
                    if (!sk.Joints[c].IsBone) JointScale[c] += w * sk.Joints[c].Scale * b.Scale;
            }
        }

        foreach (var (type, lods) in data.Meshes)
            foreach (var md in lods)
            {
                if (md.Owner != md) continue;
                var m = md.Mesh;
                var coords = (Vector3[])m.Coords.Clone();
                var scaled = (Vector3[])m.Normals.Clone();
                var uvs = (Vector2[])m.TexCoords.Clone();
                foreach (var p in md.Morphs)
                {
                    float w = Effective(p);
                    if (w == 0) continue;
                    float[]? maskW = null;
                    if (p.Masked)
                    {
                        if (masks == null || !masks.TryGetValue(RegionOf(type), out var mask)) continue;   // waits for its mask
                        if ((maskW = mask.Weights(p, m)) == null) continue;
                    }
                    foreach (var vm in p.Volumes)
                        if (vm.Joint >= 0) { JointScale[vm.Joint] += vm.Scale * w; JointPos[vm.Joint] += vm.Pos * w; }
                    var mo = p.Morph;
                    if (mo == null) continue;
                    for (int k = 0; k < mo.Indices.Length; k++)
                    {
                        int v = mo.Indices[k];
                        if (v >= coords.Length) continue;
                        float f = w * (maskW?[k] ?? 1f);
                        coords[v] += mo.Coords[k] * f;
                        scaled[v] += mo.Normals[k] * (f * 0.65f);   // NORMAL_SOFTEN_FACTOR
                        uvs[v] += mo.TexCoords[k] * f;
                    }
                }
                var normals = new Vector3[scaled.Length];
                for (int i = 0; i < scaled.Length; i++)
                    normals[i] = scaled[i].LengthSquared() > 0 ? Vector3.Normalize(scaled[i]) : Vector3.UnitZ;
                Morphed[md] = new MorphedMesh { Def = md, Coords = coords, Normals = normals, TexCoords = uvs, ScaledNormals = type == "headMesh" ? scaled : null };
            }

        (PelvisToFoot, BodyHeight) = BodySize(sk, JointPos, JointScale);
    }

    /// <summary>computeBodySize: pelvis-to-foot (left leg) and body height from local joint positions and scales,
    /// the formula as the viewer writes it.</summary>
    public static (float PelvisToFoot, float Height) BodySize(SkeletonDef sk, Vector3[] pos, Vector3[] scale)
    {
        Vector3 P(string j) => sk.TryGet(j, out var i) ? pos[i] : Vector3.Zero;
        Vector3 S(string j) => sk.TryGet(j, out var i) ? scale[i] : Vector3.One;
        float ptf = P("mHipLeft").Z * S("mPelvis").Z - P("mKneeLeft").Z * S("mHipLeft").Z
                    - P("mAnkleLeft").Z * S("mKneeLeft").Z - P("mFootLeft").Z * S("mAnkleLeft").Z;
        float h = ptf + MathF.Sqrt(2) * (P("mSkull").Z * S("mHead").Z) + P("mHead").Z * S("mNeck").Z
                  + P("mNeck").Z * S("mChest").Z + P("mChest").Z * S("mTorso").Z + P("mTorso").Z * S("mPelvis").Z;
        return (ptf, h);
    }

    internal static string RegionOf(string meshType) => meshType switch
    {
        "headMesh" => "head",
        "upperBodyMesh" => "upper_body",
        "lowerBodyMesh" => "lower_body",
        _ => "",
    };

    /// <summary>U8_to_F32: zero snaps to 0 within one step.</summary>
    public static float U8ToF32(byte v, float lower, float upper)
    {
        float delta = upper - lower;
        float val = v * (1f / 255f) * delta + lower;
        if (MathF.Abs(val) < delta * (1f / 255f)) val = 0f;
        return val;
    }

    static float DrivenWeight(DrivenRef d, float w, float driverMin, float driverMax, float dMin, float dMax)
    {
        if (w <= d.Min1) return d.Min1 == d.Max1 && d.Min1 <= driverMin ? dMax : dMin;
        if (w <= d.Max1) return dMin + (w - d.Min1) / (d.Max1 - d.Min1) * (dMax - dMin);
        if (w <= d.Max2) return dMax;
        if (w <= d.Min2) return dMax + (w - d.Max2) / (d.Min2 - d.Max2) * (dMin - dMax);
        return d.Max2 >= driverMax ? dMax : dMin;
    }
}

/// <summary>A bake's morph mask: its fifth (aux) J2C component (rows bottom-up like the viewer's LLImageRaw) sampled for
/// every masked morph of its body region's meshes when it's made (seven morphs in avatar_lad, a few thousand weights),
/// so the component itself, a megabyte (4 for a 2048 bake) a bake, isn't kept: it was, for every bake drawn
/// (2026-10-28, ~240 MB in a gcdump at a busy place).</summary>
public sealed class MorphMask
{
    readonly Dictionary<(ParamDef, LlmMesh), float[]> _weights = new();

    /// <summary><paramref name="region"/>: "head", "upper_body" or "lower_body", the bake's.</summary>
    public MorphMask(CharacterData data, string region, byte[] aux, int width, int height)
    {
        // the meshes and morphs BodyShape applies masks to, all of them
        foreach (var (type, lods) in data.Meshes)
        {
            if (BodyShape.RegionOf(type) != region) continue;
            foreach (var md in lods)
            {
                if (md.Owner != md) continue;
                foreach (var p in md.Morphs)
                    if (p.Masked && p.Morph != null) _weights[(p, md.Mesh)] = Sample(p, md.Mesh, aux, width, height);
            }
        }
    }

    /// <summary>The mask's weight for each of the morph's vertices (null if it isn't one of the region's masked morphs).</summary>
    public float[]? Weights(ParamDef p, LlmMesh mesh) => _weights.GetValueOrDefault((p, mesh));

    /// <summary>LLPolyMorphTarget::generateMask: per morph vertex, the mask at its UV (a seam vertex reads its twin's UV).</summary>
    static float[] Sample(ParamDef p, LlmMesh mesh, byte[] aux, int width, int height)
    {
        var mo = p.Morph!;
        var w = new float[mo.Indices.Length];
        for (int k = 0; k < w.Length; k++)
        {
            int v = mo.Indices[k];
            int src = mesh.SharedVerts.TryGetValue(v, out var twin) ? twin : v;
            var uv = mesh.TexCoords[Math.Min(src, mesh.TexCoords.Length - 1)];
            int s = Math.Clamp((int)(uv.X * (width - 1)), 0, width - 1);
            int t = Math.Clamp((int)(uv.Y * (height - 1)), 0, height - 1);
            float m = aux[t * width + s] / 255f;
            w[k] = p.InvertMask ? 1 - m : m;
        }
        return w;
    }
}
