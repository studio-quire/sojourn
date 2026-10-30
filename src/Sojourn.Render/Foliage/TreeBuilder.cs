// Ported from the Second Life viewer: indra/newview/llvotree.cpp (LLVOTree::updateGeometry, updateMesh,
// genBranchPipeline, appendMesh, calcNumVerts, idleUpdate, setPixelAreaAndAngle, updateSpatialExtents) and the
// LOD helpers it calls (LLFace::calcImportanceToCamera / adjustPixelArea, LLVolumeLODGroup::getDetailFromTan).
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
namespace Sojourn.Render.Foliage;

/// <summary>Camera inputs for the viewer's foliage LOD rules. Positions in the same frame as the objects'.</summary>
/// <param name="Position">The head's centre: detail goes by distance from it alone, never by where the view points
/// (docs/rendering.md > Distances from the head).</param>
/// <param name="VerticalFov">LLCamera::getView(), radians (viewer default 1.047).</param>
public readonly record struct FoliageCamera(Vector3 Position, float VerticalFov, int ViewportWidth, int ViewportHeight);

/// <summary>
/// Linden trees (PCode Tree / NewTree). The viewer builds each tree from a species' reference parts (a noisy
/// tapered cylinder per LOD and a crossed leaf quad pair), instanced down a recursive branch pipeline. Wind is
/// gone from the current viewer (mTrunkBend stays zero), so the pose is static.
///
/// Usage: <c>Build(species, lod)</c> gives the shared tree-space mesh for that species and LOD; draw it with
/// <c>ModelMatrix(position, rotation, scale)</c> (region-local, like the viewer's mesh, which it bakes in region
/// coordinates and draws with the region's render matrix). <c>Build(species, lod, baseMatrix)</c> bakes a matrix
/// in exactly as the viewer does, for comparisons.
/// </summary>
public static class TreeBuilder
{
    public const int LodCount = 4;
    /// <summary>ChooseLod result meaning "not drawn" (mTrunkLOD >= sMAX_NUM_TREE_LOD_LEVELS).</summary>
    public const int Hidden = 4;

    private static readonly int[] LodSlices = [10, 5, 4, 3];                               // sLODSlices
    private static readonly float[] LodAngles = [30f, 20f, 15f, FoliageMath.AlmostZero];    // sLODAngles

    private const float LeafLeft = 0.52f, LeafRight = 0.98f, LeafTop = 1.0f, LeafBottom = 0.52f, LeafWidth = 1f;
    private const float Srr3 = 0.577350269f; // sqrt(1/3)
    private const float Srr2 = 0.707106781f; // sqrt(1/2)

    private sealed record Part(Vector3[] Positions, Vector3[] Normals, Vector2[] TexCoords, ushort[] Indices);

    private static readonly Part Leaf = BuildLeaf();
    private static readonly Lazy<Part>[,] Cylinders = MakeCylinderCache();
    private static readonly Lazy<FoliageMesh>[,] Meshes = MakeMeshCache();

    // ---- public API -------------------------------------------------------------------------------------------

    /// <summary>The tree-space mesh for a species and LOD (0 best .. 3), cached and shared; don't mutate it.</summary>
    public static FoliageMesh Build(int species, int lod)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lod);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lod, LodCount);
        return Meshes[FoliageSpecies.Tree(species).Id, lod].Value;
    }

    /// <summary>
    /// Builds the mesh with the branch recursion starting from <paramref name="baseMatrix"/> (row-vector,
    /// v * M). With <see cref="ModelMatrix"/> as the base this reproduces the viewer's baked region-space buffer.
    /// </summary>
    public static FoliageMesh Build(int species, int lod, in Matrix4x4 baseMatrix)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lod);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lod, LodCount);
        var sp = FoliageSpecies.Tree(species);
        var cyl = Cylinders[sp.Id, lod].Value;

        // calcNumVerts
        int nv = 0, ni = 0;
        CountParts(sp.Depth, sp.TrunkDepth, (int)sp.Branches, ref nv, ref ni, cyl);

        var b = new MeshBuffer(nv, ni);
        // updateMesh: droop = mDroop + 25 * (1 - |mTrunkBend|), and mTrunkBend is always zero now.
        float droop = sp.Droop + 25f * (1f - 0f);
        GenBranchPipeline(b, sp, cyl, baseMatrix, sp.Depth, sp.TrunkDepth, 1.0f, sp.Twist, droop, sp.Branches);

        return new FoliageMesh
        {
            Positions = b.Positions,
            Normals = b.Normals,
            TexCoords = b.TexCoords,
            Indices = b.Indices,
            Texture = sp.Texture,
            AlphaCutoff = 0.5f,       // LLDrawPoolTree::beginDeferredPass setMinimumAlpha(0.5)
            ShadowAlphaCutoff = 0.5f, // LLDrawPoolTree::beginShadowPass setMinimumAlpha(0.5)
        };
    }

    /// <summary>
    /// The matrix the viewer applies to a tree (updateMesh): uniform scale |scale| * 0.05, a 90 degree turn about Z,
    /// the object's rotation, then its position less 0.1 m in Z ("plants tree underground"). Row-vector (v * M).
    /// Pass the region-local position for region-local output (the viewer's frame), or any other frame's.
    /// </summary>
    public static Matrix4x4 ModelMatrix(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var trans = FoliageMath.Translation(position.X, position.Y, position.Z - 0.1f); // trans_mat *= identity
        // mTrunkBend is zero, so the bend quaternion is LLQuaternion(0, (0,0,0)) = identity.
        var bend = FoliageMath.AxisAngle(0f, 0f, 0f, 0f);
        var rot = FoliageMath.Mul(FoliageMath.Mul(bend, FoliageMath.AxisAngle(90f * FoliageMath.DegToRad, 0f, 0f, 1f)), rotation);
        var rotMat = FoliageMath.Mul(FoliageMath.Rotation(rot), trans);
        float radius = FoliageMath.Length(scale) * 0.05f;
        return FoliageMath.Mul(FoliageMath.Scale(radius, radius, radius), rotMat);
    }

    /// <summary>
    /// The viewer's tree LOD (LLVOTree::setPixelAreaAndAngle + idleUpdate) for a still camera: 0 = best .. 3, or
    /// <see cref="Hidden"/>. <paramref name="treeLodFactor"/> is RenderTreeLODFactor (0.5 on the stock
    /// High/HighUltra presets, 1.0 on Ultra, 0 on Low = no trees or grass at all); <paramref name="volumeLodFactor"/>
    /// is RenderVolumeLODFactor (1.75 on HighUltra), which only enters through sDistanceFactor. Positions in any one
    /// frame (the viewer uses agent space).
    /// </summary>
    public static int ChooseLod(int species, Vector3 position, Quaternion rotation, Vector3 scale, in FoliageCamera camera,
        float treeLodFactor = 0.5f, float volumeLodFactor = 1.75f)
    {
        var sp = FoliageSpecies.Tree(species);
        float appAngle = AppAngle(sp, position, scale, camera);

        // idleUpdate
        float scaleLen = FoliageMath.Length(scale);
        float distanceFactor = 1f - volumeLodFactor * 0.1f;                    // LLVOVolume::sDistanceFactor
        float distWrtCamera = FoliageMath.Round(FoliageMath.Length(PositionGroup(sp, position, rotation, scale) - camera.Position), 0.01f);
        float distance = distWrtCamera * distanceFactor * (FoliageMath.Pi / 3f);
        float sz = sp.BillboardScale * sp.BillboardRatio * scaleLen;

        int trunkLod = Hidden;
        float angle = appAngle * treeLodFactor;
        for (int j = 0; j < LodCount; j++)
        {
            if (angle > LodAngles[j])
            {
                trunkLod = j;
                break;
            }
        }

        float tanAngle = (treeLodFactor * 64 * sz) / distance;
        int curDetail = DetailFromTan(FoliageMath.Round(tanAngle, 0.01f));
        trunkLod = Math.Max(trunkLod, LodCount - curDetail - 1);
        return Math.Min(trunkLod, Hidden);
    }

    /// <summary>
    /// The viewer's spatial extents for a tree (updateSpatialExtents): a cube of half-size
    /// billboard_scale * billboard_ratio * |scale| * 0.025 around a centre lifted that far along the object's Z.
    /// Coarse; the real mesh may poke out of it.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) Extents(int species, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var sp = FoliageSpecies.Tree(species);
        float sz = sp.BillboardScale * sp.BillboardRatio * FoliageMath.Length(scale) * 0.05f * 0.5f;
        var center = PositionGroup(sp, position, rotation, scale);
        var size = new Vector3(sz, sz, sz);
        return (center - size, center + size);
    }

    // ---- LOD helpers ------------------------------------------------------------------------------------------

    private static Vector3 PositionGroup(TreeSpecies sp, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        float radius = FoliageMath.Length(scale) * 0.05f;
        float sz = sp.BillboardScale * sp.BillboardRatio * radius * 0.5f;
        return position + FoliageMath.Rotate(new Vector3(0f, 0f, sz), rotation);
    }

    // LLVOTree::setPixelAreaAndAngle, with a still camera (LLFace importance ignores fast camera motion), and every tree
    // taken as straight ahead: the viewer's importance falls off the view's axis, which in a headset changed a near
    // tree's detail as the head turned (docs/rendering.md > Distances from the head).
    private static float AppAngle(TreeSpecies sp, Vector3 center, Vector3 scale, in FoliageCamera cam)
    {
        Vector3 lookAt = center - cam.Position;
        float dist = FoliageMath.Length(lookAt);
        const float cosAngleToViewDir = 1f;
        float radius = FoliageMath.Length(scale) * 0.5f;
        float range = dist - radius;
        float maxScale = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));

        float appAngle = range < FoliageMath.AlmostZero ? 180f : MathF.Atan2(maxScale, range) * FoliageMath.RadToDeg;

        float maxScaleBb = sp.BillboardScale * maxScale;
        float area = maxScaleBb * (maxScaleBb * sp.BillboardRatio);
        float pixelsPerMeter = cam.ViewportHeight / (MathF.Tan(cam.VerticalFov) * dist);
        float pixelArea = pixelsPerMeter * pixelsPerMeter * area;

        float aspect = (float)cam.ViewportWidth / cam.ViewportHeight;
        float importance = ImportanceToCamera(cosAngleToViewDir, dist, MathF.Cos(0.5f * cam.VerticalFov * MathF.Max(1f, aspect)));
        pixelArea = AdjustPixelArea(importance, pixelArea);
        int screenPixelArea = (int)(cam.ViewportHeight * (cam.ViewportHeight * aspect));
        if (pixelArea > screenPixelArea)
            appAngle = 180f;
        return appAngle;
    }

    private static readonly float[,] ImportanceOverDistance = { { 16.1f, 1.0f }, { 32.1f, 0.5f }, { 48.1f, 0.2f }, { 96.1f, 0.05f } };
    private static readonly float[,] ImportanceOverAngle = { { 0.985f, 1.0f }, { 0.94f, 0.8f }, { 0.866f, 0.64f }, { 0.0f, 0.36f } };

    // LLFace::calcImportanceToCamera (camera speeds taken as zero)
    private static float ImportanceToCamera(float cosAngleToViewDir, float dist, float cosHalfFov)
    {
        const int levels = 4;
        if (!(cosAngleToViewDir > cosHalfFov && dist < ImportanceOverDistance[levels - 1, 0]))
            return 0f;
        int i;
        for (i = 0; i < levels && dist > ImportanceOverDistance[i, 0]; ++i) { }
        i = Math.Min(i, levels - 1);
        float distFactor = ImportanceOverDistance[i, 1];
        for (i = 0; i < levels && cosAngleToViewDir < ImportanceOverAngle[i, 0]; ++i) { }
        i = Math.Min(i, levels - 1);
        return distFactor * ImportanceOverAngle[i, 1];
    }

    // LLFace::adjustPixelArea (sMaxSmallImageSize 64*64, sMinLargeImageSize 256*256)
    private static float AdjustPixelArea(float importance, float pixelArea)
    {
        if (pixelArea > 64 * 64)
        {
            if (importance < 0.05f)
                pixelArea = MathF.Min(pixelArea * 0.5f, 128f * 128f);
            else if (pixelArea > 65536 && importance < 0.3f)
                pixelArea = 65536;
        }
        return pixelArea;
    }

    // LLVolumeLODGroup::getDetailFromTan, thresholds 0.03 * {1, 2, 8, 100}
    private static int DetailFromTan(float tanAngle)
    {
        ReadOnlySpan<float> thresholds = [0.03f, 2 * 0.03f, 8 * 0.03f, 100 * 0.03f];
        for (int i = 0; i < LodCount - 1; i++)
            if (tanAngle <= thresholds[i])
                return i;
        return LodCount - 1;
    }

    // ---- geometry ---------------------------------------------------------------------------------------------

    private sealed class MeshBuffer(int nv, int ni)
    {
        public readonly Vector3[] Positions = new Vector3[nv];
        public readonly Vector3[] Normals = new Vector3[nv];
        public readonly Vector2[] TexCoords = new Vector2[nv];
        public readonly uint[] Indices = new uint[ni];
        public int V, I;
    }

    private static void CountParts(int depth, int trunkDepth, int branches, ref int nv, ref int ni, Part cyl)
    {
        if (depth > 0)
        {
            nv += cyl.Positions.Length;
            ni += cyl.Indices.Length;
            for (int i = 0; i < branches; i++)
                CountParts(depth - 1, 0, branches, ref nv, ref ni, cyl);
            if (trunkDepth != 0)
                CountParts(depth, trunkDepth - 1, branches, ref nv, ref ni, cyl);
        }
        else
        {
            nv += Leaf.Positions.Length;
            ni += Leaf.Indices.Length;
        }
    }

    // LLVOTree::genBranchPipeline, stop_level 0.
    private static void GenBranchPipeline(MeshBuffer b, TreeSpecies sp, Part cyl, in Matrix4x4 matrix, int depth,
        int trunkDepth, float scale, float twist, float droop, float branches)
    {
        bool trunk = trunkDepth != 0 || scale == 1f;
        float length = trunk ? sp.TrunkLength : sp.BranchLength;
        float aspect = trunk ? sp.TrunkAspect : sp.BranchAspect;
        float constantTwist = 360f / branches;

        if (depth > 0)
        {
            float width = scale * length * aspect;
            var scaleMat = FoliageMath.Mul(FoliageMath.Scale(width, width, scale * length), matrix);
            AppendMesh(b, cyl, scaleMat);

            for (int i = 0; i < (int)branches; i++)
            {
                var transMat = FoliageMath.Mul(FoliageMath.Translation(0f, 0f, scale * length), matrix);
                var rot = FoliageMath.Mul(FoliageMath.Mul(
                        FoliageMath.AxisAngle(20f * FoliageMath.DegToRad, 0f, 0f, 1f),
                        FoliageMath.AxisAngle(droop * FoliageMath.DegToRad, 0f, 1f, 0f)),
                    FoliageMath.AxisAngle(((constantTwist + ((i % 2 == 0) ? twist : -twist)) * i) * FoliageMath.DegToRad, 0f, 0f, 1f));
                var rotMat = FoliageMath.Mul(FoliageMath.Rotation(rot), transMat);
                GenBranchPipeline(b, sp, cyl, rotMat, depth - 1, 0, scale * sp.ScaleStep, twist, droop, branches);
            }

            if (trunkDepth != 0)
            {
                var transMat = FoliageMath.Mul(FoliageMath.Translation(0f, 0f, scale * length), matrix);
                // LLMatrix4(angle, vec) = initRotation(LLQuaternion(angle, vec)) with a cleared translation row
                var rotMat = FoliageMath.Mul(FoliageMath.Rotation(FoliageMath.AxisAngle(70.5f * FoliageMath.DegToRad, 0f, 0f, 1f)), transMat);
                GenBranchPipeline(b, sp, cyl, rotMat, depth, trunkDepth - 1, scale * sp.ScaleStep, twist, droop, branches);
            }
        }
        else
        {
            float s = scale * sp.LeafScale;
            var scaleMat = FoliageMath.Mul(FoliageMath.Scale(s, s, s), matrix);
            AppendMesh(b, Leaf, scaleMat);
        }
    }

    // LLVOTree::appendMesh
    private static void AppendMesh(MeshBuffer b, Part part, in Matrix4x4 matrix)
    {
        var normMat = FoliageMath.NormalMatrix(matrix);
        int start = b.V;
        for (int i = 0; i < part.Positions.Length; i++)
        {
            b.Positions[b.V] = FoliageMath.Transform(part.Positions[i], matrix);
            b.Normals[b.V] = FoliageMath.Normalize(FoliageMath.Transform(part.Normals[i], normMat));
            b.TexCoords[b.V] = part.TexCoords[i];
            b.V++;
        }
        foreach (ushort idx in part.Indices)
            b.Indices[b.I++] = (uint)(idx + start);
    }

    private static Lazy<Part>[,] MakeCylinderCache()
    {
        var c = new Lazy<Part>[FoliageSpecies.Trees.Count, LodCount];
        for (int s = 0; s < c.GetLength(0); s++)
            for (int l = 0; l < LodCount; l++)
            {
                int ss = s, ll = l;
                c[s, l] = new Lazy<Part>(() => BuildCylinder(FoliageSpecies.Trees[ss], ll));
            }
        return c;
    }

    private static Lazy<FoliageMesh>[,] MakeMeshCache()
    {
        var c = new Lazy<FoliageMesh>[FoliageSpecies.Trees.Count, LodCount];
        for (int s = 0; s < c.GetLength(0); s++)
            for (int l = 0; l < LodCount; l++)
            {
                int ss = s, ll = l;
                c[s, l] = new Lazy<FoliageMesh>(() => Build(ss, ll, Matrix4x4.Identity));
            }
        return c;
    }

    // updateGeometry: the two leaf quads (each with a reversed-winding twin), 16 vertices, 24 indices.
    private static Part BuildLeaf()
    {
        const float h = 0.5f * LeafWidth;
        Vector3[] p =
        [
            new(-h, 0f, 0f), new(h, 0f, 1f), new(-h, 0f, 1f), new(h, 0f, 0f),
            new(-h, 0f, 0f), new(h, 0f, 1f), new(-h, 0f, 1f), new(h, 0f, 0f),
            new(0f, -h, 0f), new(0f, h, 1f), new(0f, -h, 1f), new(0f, h, 0f),
            new(0f, -h, 0f), new(0f, h, 1f), new(0f, -h, 1f), new(0f, h, 0f),
        ];
        Vector3[] n =
        [
            new(-Srr2, -Srr2, 0f), new(Srr3, -Srr3, Srr3), new(-Srr3, -Srr3, Srr3), new(Srr2, -Srr2, 0f),
            new(-Srr2, Srr2, 0f), new(Srr3, Srr3, Srr3), new(-Srr3, Srr3, Srr3), new(Srr2, Srr2, 0f),
            new(Srr2, -Srr2, 0f), new(Srr3, Srr3, Srr3), new(Srr3, -Srr3, Srr3), new(Srr2, Srr2, 0f),
            new(-Srr2, -Srr2, 0f), new(-Srr3, Srr3, Srr3), new(-Srr3, -Srr3, Srr3), new(-Srr2, Srr2, 0f),
        ];
        var lb = new Vector2(LeafLeft, LeafBottom);
        var rt = new Vector2(LeafRight, LeafTop);
        var lt = new Vector2(LeafLeft, LeafTop);
        var rb = new Vector2(LeafRight, LeafBottom);
        Vector2[] t = [lb, rt, lt, rb, lb, rt, lt, rb, lb, rt, lt, rb, lb, rt, lt, rb];
        ushort[] i = [0, 1, 2, 0, 3, 1, 4, 6, 5, 4, 5, 7, 8, 9, 10, 8, 11, 9, 12, 14, 13, 12, 13, 15];
        return new Part(p, n, t, i);
    }

    // updateGeometry: the species' branch cylinder for one LOD, slices x slices vertices, capped by collapsing the
    // first and last rings (z -0.1 and 1.1) to the axis, radius roughened by turbulence3.
    private static Part BuildCylinder(TreeSpecies sp, int lod)
    {
        int slices = LodSlices[lod];
        const float baseRadius = 0.65f;
        float topRadius = baseRadius * sp.Taper;
        float angleInc = 360f / (slices - 1);
        float zInc = 1f;
        if (slices > 3)
            zInc = 1f / (slices - 3);

        float noiseScale = sp.NoiseMag;
        const float capNudge = 0.1f;
        const int fractalDepth = 5;
        float nvecScale = 1f * sp.NoiseScale;
        float nvecScaleZ = 4f * sp.NoiseScale;
        float texZRepeat = sp.RepeatZ;
        const float height = 1f;

        var p = new Vector3[slices * slices];
        var n = new Vector3[slices * slices];
        var t = new Vector2[slices * slices];
        int v = 0;
        for (int i = 0; i < slices; i++)
        {
            float z, r0;
            if (i == 0)
            {
                z = -capNudge;
                r0 = 0f;
            }
            else if (i == slices - 1)
            {
                z = 1f + capNudge;
                r0 = 0f;
            }
            else
            {
                z = (i - 1) * zInc;
                r0 = baseRadius + (topRadius - baseRadius) * z;
            }

            for (int j = 0; j < slices; j++)
            {
                float angle = (slices - 1 == j) ? 0f : j * angleInc;
                float nangle = angle;

                float x1 = MathF.Cos(angle * FoliageMath.DegToRad);
                float y1 = MathF.Sin(angle * FoliageMath.DegToRad);
                float startRadius = r0 * (1f + 1.2f * MathF.Abs(z - 0.66f * height) / height);
                float nx = MathF.Cos(nangle * FoliageMath.DegToRad) * startRadius * nvecScale;
                float ny = MathF.Sin(nangle * FoliageMath.DegToRad) * startRadius * nvecScale;
                float nz = z * nvecScaleZ;
                float radius = startRadius + FoliageNoise.Turbulence3(nx, ny, nz, fractalDepth) * noiseScale;

                // "Not 0.5 for slight slop factor to avoid edges on leaves"
                var tc = (slices - 1 == j)
                    ? new Vector2(0.490f, (1f - z / 2f) * texZRepeat)
                    : new Vector2((angle / 360f) * 0.5f, (1f - z / 2f) * texZRepeat);

                p[v] = new Vector3(x1 * radius, y1 * radius, z);
                n[v] = new Vector3(x1, y1, 0f);
                t[v] = tc;
                v++;
            }
        }

        var idx = new ushort[(slices - 1) * (slices - 1) * 6];
        int k = 0;
        for (int i = 0; i < slices - 1; i++)
        {
            for (int j = 0; j < slices - 1; j++)
            {
                int x1o = j + 1;
                idx[k++] = (ushort)(j + i * slices);
                idx[k++] = (ushort)(x1o + (i + 1) * slices);
                idx[k++] = (ushort)(j + (i + 1) * slices);
                idx[k++] = (ushort)(j + i * slices);
                idx[k++] = (ushort)(x1o + i * slices);
                idx[k++] = (ushort)(x1o + (i + 1) * slices);
            }
        }
        return new Part(p, n, t, idx);
    }
}
