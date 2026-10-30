// Ported from the Second Life viewer: indra/newview/llvosurfacepatch.cpp (geometry, gen_terrain_tangents),
// llsurfacepatch.cpp (eval), llmath/llvolume.cpp (LLCalculateTangentArray).
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
using System.Runtime.InteropServices;

namespace Sojourn.Render.TerrainGen;

/// <summary>
/// One terrain vertex as LLVOSurfacePatch fills the vertex buffer (minus texcoord0, the parcel-ownership
/// overlay coordinate, which is Position.xy / 257 for an SL region).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TerrainVertex
{
    /// <summary>Region-local metres (x east, y north, z height). The viewer draws with the region's model matrix.</summary>
    public Vector3 Position;
    public Vector3 Normal;
    /// <summary>
    /// texcoord1: x = composition (0-3, which detail layers blend), y = noise in [0,1] that picks the alpha-ramp
    /// row. The classic and the PBR (heightmap-with-noise) terrain shaders both read it.
    /// </summary>
    public Vector2 TexCoord1;
}

/// <summary>Where a patch's geometry sits in the mesh and the strides it was built with.</summary>
public readonly record struct TerrainPatchRange(int Px, int Py, int FirstVertex, int VertexCount, int FirstIndex, int IndexCount,
    int Stride, int NorthStride, int EastStride, Vector3 BoundsMin, Vector3 BoundsMax);

public sealed class TerrainMesh
{
    public required TerrainVertex[] Vertices { get; init; }
    /// <summary>Triangle list, counter-clockwise seen from above.</summary>
    public required uint[] Indices { get; init; }
    /// <summary>PBR terrain only: LLCalculateTangentArray tangents (w = bitangent sign); null otherwise.</summary>
    public Vector4[]? Tangents { get; init; }
    public required TerrainPatchRange[] Patches { get; init; }
}

public static class TerrainMeshBuilder
{
    /// <summary>
    /// The whole region's terrain as the viewer builds it for the given strides. <paramref name="patchStride"/>
    /// (px, py) returns 1, 2, 4, 8 or 16; it is also asked for (PatchesPerEdge, py) and (px, PatchesPerEdge)
    /// when an east/north neighbour region is connected, because each patch stitches its north and east
    /// edges to its neighbours' strides (see <see cref="TerrainLod.CameraStrides"/>). Neighbouring strides must
    /// differ by at most a factor of 2, as the viewer assumes (distance-based strides always do).
    /// </summary>
    public static TerrainMesh BuildRegion(TerrainInput input, Func<int, int, int> patchStride,
        TerrainNormalMode normalMode = TerrainNormalMode.Classic, bool tangents = false)
        => Build(new TerrainSurface(input, normalMode), patchStride, tangents);

    public static TerrainMesh Build(TerrainSurface surface, Func<int, int, int> patchStride, bool tangents = false)
    {
        int n = surface.PatchesPerEdge;
        var verts = new List<TerrainVertex>(surface.GridsPerEdge * surface.GridsPerEdge);
        var indices = new List<uint>(surface.Width * surface.Width * 6);
        var ranges = new TerrainPatchRange[n * n];

        for (int py = 0; py < n; py++)
        {
            for (int px = 0; px < n; px++)
            {
                var patch = surface.Patches[px + py * n];
                int stride = patchStride(px, py);
                int northStride = patch.Neighbors[TerrainDirection.North] != null ? patchStride(px, py + 1) : stride;
                int eastStride = patch.Neighbors[TerrainDirection.East] != null ? patchStride(px + 1, py) : stride;
                ValidateStride(stride);
                ValidateStride(northStride);
                ValidateStride(eastStride);

                int firstVertex = verts.Count, firstIndex = indices.Count;
                var b = new PatchGeometry(surface, patch, verts, indices);
                b.Main(stride);
                b.North(stride, northStride);
                b.East(stride, eastStride);

                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                for (int v = firstVertex; v < verts.Count; v++)
                {
                    min = Vector3.Min(min, verts[v].Position);
                    max = Vector3.Max(max, verts[v].Position);
                }
                ranges[px + py * n] = new TerrainPatchRange(px, py, firstVertex, verts.Count - firstVertex, firstIndex,
                    indices.Count - firstIndex, stride, northStride, eastStride, min, max);
            }
        }

        var vertexArray = verts.ToArray();
        var indexArray = indices.ToArray();
        Vector4[]? tangentArray = null;
        if (tangents)
        {
            tangentArray = new Vector4[vertexArray.Length];
            float regionWidth = surface.Width * surface.MetersPerGrid; // LLViewerRegion::getWidth
            foreach (var r in ranges)
                CalculateTangents(vertexArray, indexArray, r, regionWidth, tangentArray);
        }

        return new TerrainMesh { Vertices = vertexArray, Indices = indexArray, Tangents = tangentArray, Patches = ranges };
    }

    private static void ValidateStride(int s)
    {
        if (s != 1 && s != 2 && s != 4 && s != 8 && s != 16)
            throw new ArgumentOutOfRangeException(nameof(s), s, "Patch strides are 1, 2, 4, 8 or 16.");
    }

    // LLVOSurfacePatch::getGeomSizes{Main,North,East}: vertex and index counts for a patch's three parts.
    public static (int Vertices, int Indices) GeomSizes(int stride, int northStride, int eastStride)
    {
        const int patchSize = TerrainSurface.GridsPerPatchEdge;
        int nv = 0, ni = 0;
        int vertSize = patchSize / stride;
        if (vertSize >= 2)
        {
            nv += vertSize * vertSize;
            ni += 6 * (vertSize - 1) * (vertSize - 1);
        }
        foreach (int other in new[] { northStride, eastStride })
        {
            int length = patchSize / stride;
            if (other == stride) { nv += 2 * length + 1; ni += length * 6 - 3; }
            else if (other > stride) { nv += length + length / 2 + 1; ni += (length / 2) * 9 - 3; }
            else { length = patchSize / other; nv += length + length / 2 + 1; ni += 9 * (length / 2) - 3; }
        }
        return (nv, ni);
    }

    private readonly struct PatchGeometry(TerrainSurface surface, SurfacePatch patch, List<TerrainVertex> verts, List<uint> indices)
    {
        private const int PatchSize = TerrainSurface.GridsPerPatchEdge;

        // eval's noise scale (1 / (4.9215 * 7)) * 0.2222222222. MSVC folds it at higher precision to 0x3bd35e89,
        // one ulp below the float-by-float result (0x3bd35e8a); this is the constant in the compiled viewer.
        private static readonly float NoiseXyScaleInv = BitConverter.Int32BitsToSingle(0x3bd35e89);

        // LLSurfacePatch::eval
        private void Eval(int x, int y)
        {
            int g = surface.GridsPerEdge;
            int offset = patch.DataOffset + x + y * g;
            float mpg = surface.MetersPerGrid;

            var v = new TerrainVertex
            {
                Normal = surface.Norm[offset],
                Position = new Vector3(patch.OriginRegionX + x * mpg, patch.OriginRegionY + y * mpg, surface.Z[offset]),
            };

            v.TexCoord1.X = surface.CompositionXY((int)MathF.Floor(patch.OriginRegionX) + x, (int)MathF.Floor(patch.OriginRegionY) + y);

            float vx = (float)((float)(patch.OriginGlobalX + x) * NoiseXyScaleInv % 256f);
            float vy = (float)((float)(patch.OriginGlobalY + y) * NoiseXyScaleInv % 256f);
            float rand = TerrainNoise.Noise2(vx, vy) * 0.75f + 0.5f;
            v.TexCoord1.Y = Math.Clamp(rand, 0f, 1f);

            verts.Add(v);
        }

        private void Index(int i) => indices.Add((uint)i);

        // LLVOSurfacePatch::updateMainGeometry
        public void Main(int renderStride)
        {
            int vertSize = PatchSize / renderStride;
            if (vertSize < 2)
                return;
            int baseIndex = verts.Count;

            for (int j = 0; j < vertSize; j++)
                for (int i = 0; i < vertSize; i++)
                    Eval(i * renderStride, j * renderStride);

            for (int j = 0; j < vertSize - 1; j++)
            {
                if (j % 2 != 0)
                {
                    for (int i = vertSize - 1; i > 0; i--)
                    {
                        Index(baseIndex + (i - 1) + j * vertSize);
                        Index(baseIndex + i + (j + 1) * vertSize);
                        Index(baseIndex + (i - 1) + (j + 1) * vertSize);

                        Index(baseIndex + (i - 1) + j * vertSize);
                        Index(baseIndex + i + j * vertSize);
                        Index(baseIndex + i + (j + 1) * vertSize);
                    }
                }
                else
                {
                    for (int i = 0; i < vertSize - 1; i++)
                    {
                        Index(baseIndex + i + j * vertSize);
                        Index(baseIndex + (i + 1) + (j + 1) * vertSize);
                        Index(baseIndex + i + (j + 1) * vertSize);

                        Index(baseIndex + i + j * vertSize);
                        Index(baseIndex + (i + 1) + j * vertSize);
                        Index(baseIndex + (i + 1) + (j + 1) * vertSize);
                    }
                }
            }
        }

        // LLVOSurfacePatch::updateNorthGeometry
        public void North(int renderStride, int northStride)
        {
            int length = PatchSize / renderStride;
            int halfLength = length / 2;
            int o = verts.Count;

            if (northStride == renderStride)
            {
                for (int i = 0; i < length; i++)
                    Eval(i * renderStride, 16 - renderStride);
                for (int i = 0; i <= length; i++)
                    Eval(i * renderStride, 16);

                for (int i = 0; i < length; i++)
                {
                    Index(o + i);
                    Index(o + length + i + 1);
                    Index(o + length + i);
                    if (i != length - 1)
                    {
                        Index(o + i);
                        Index(o + i + 1);
                        Index(o + length + i + 1);
                    }
                }
            }
            else if (northStride > renderStride)
            {
                // north stride is longer (fewer vertices)
                for (int i = 0; i < length; i++)
                    Eval(i * renderStride, 16 - renderStride);
                for (int i = 0; i <= length; i += 2)
                    Eval(i * renderStride, 16);

                for (int i = 0; i < length; i++)
                {
                    if (i % 2 == 0)
                    {
                        Index(o + i);
                        Index(o + i + 1);
                        Index(o + length + i / 2);

                        Index(o + i + 1);
                        Index(o + length + i / 2 + 1);
                        Index(o + length + i / 2);
                    }
                    else if (i < length - 1)
                    {
                        Index(o + i);
                        Index(o + i + 1);
                        Index(o + length + i / 2 + 1);
                    }
                }
            }
            else
            {
                // north stride is shorter (more vertices)
                length = PatchSize / northStride;
                halfLength = length / 2;
                for (int i = 0; i < length; i += 2)
                    Eval(i * northStride, 16 - renderStride);
                for (int i = 0; i <= length; i++)
                    Eval(i * northStride, 16);

                for (int i = 0; i < length; i++)
                {
                    if (i % 2 == 0)
                    {
                        Index(o + halfLength + i);
                        Index(o + i / 2);
                        Index(o + halfLength + i + 1);
                    }
                    else if (i < length - 2)
                    {
                        Index(o + halfLength + i);
                        Index(o + i / 2);
                        Index(o + i / 2 + 1);

                        Index(o + halfLength + i);
                        Index(o + i / 2 + 1);
                        Index(o + halfLength + i + 1);
                    }
                    else
                    {
                        Index(o + halfLength + i);
                        Index(o + i / 2);
                        Index(o + halfLength + i + 1);
                    }
                }
            }
        }

        // LLVOSurfacePatch::updateEastGeometry
        public void East(int renderStride, int eastStride)
        {
            int length = PatchSize / renderStride;
            int halfLength = length / 2;
            int o = verts.Count;

            if (eastStride == renderStride)
            {
                for (int i = 0; i < length; i++)
                    Eval(16 - renderStride, i * renderStride);
                for (int i = 0; i <= length; i++)
                    Eval(16, i * renderStride);

                for (int i = 0; i < length; i++)
                {
                    Index(o + i);
                    Index(o + length + i);
                    Index(o + length + i + 1);
                    if (i != length - 1)
                    {
                        Index(o + i);
                        Index(o + length + i + 1);
                        Index(o + i + 1);
                    }
                }
            }
            else if (eastStride > renderStride)
            {
                // east stride is longer (fewer vertices)
                for (int i = 0; i < length; i++)
                    Eval(16 - renderStride, i * renderStride);
                for (int i = 0; i <= length; i += 2)
                    Eval(16, i * renderStride);

                for (int i = 0; i < length; i++)
                {
                    if (i % 2 == 0)
                    {
                        Index(o + i);
                        Index(o + length + i / 2);
                        Index(o + i + 1);

                        Index(o + i + 1);
                        Index(o + length + i / 2);
                        Index(o + length + i / 2 + 1);
                    }
                    else if (i < length - 1)
                    {
                        Index(o + i);
                        Index(o + length + i / 2 + 1);
                        Index(o + i + 1);
                    }
                }
            }
            else
            {
                // east stride is shorter (more vertices)
                length = PatchSize / eastStride;
                halfLength = length / 2;
                for (int i = 0; i < length; i += 2)
                    Eval(16 - renderStride, i * eastStride);
                for (int i = 0; i <= length; i++)
                    Eval(16, i * eastStride);

                for (int i = 0; i < length; i++)
                {
                    if (i % 2 == 0)
                    {
                        Index(o + halfLength + i);
                        Index(o + halfLength + i + 1);
                        Index(o + i / 2);
                    }
                    else if (i < length - 2)
                    {
                        Index(o + halfLength + i);
                        Index(o + i / 2 + 1);
                        Index(o + i / 2);

                        Index(o + halfLength + i);
                        Index(o + halfLength + i + 1);
                        Index(o + i / 2 + 1);
                    }
                    else
                    {
                        Index(o + halfLength + i);
                        Index(o + halfLength + i + 1);
                        Index(o + i / 2);
                    }
                }
            }
        }
    }

    // gen_terrain_tangents for one patch's vertices: texcoords = position.xy / region width, then
    // LLCalculateTangentArray. (Vertices of different patches never share triangles, so doing it per patch
    // matches the viewer's per-spatial-group pass.) normalize3fast is replaced by exact normalisation.
    private static void CalculateTangents(TerrainVertex[] verts, uint[] indices, TerrainPatchRange r, float regionWidth, Vector4[] tangent)
    {
        int first = r.FirstVertex, count = r.VertexCount;
        var tan1 = new Vector3[count];
        var tan2 = new Vector3[count];

        for (int a = r.FirstIndex; a < r.FirstIndex + r.IndexCount; a += 3)
        {
            int i1 = (int)indices[a] - first, i2 = (int)indices[a + 1] - first, i3 = (int)indices[a + 2] - first;
            Vector3 v1 = verts[first + i1].Position, v2 = verts[first + i2].Position, v3 = verts[first + i3].Position;
            float w1x = v1.X / regionWidth, w1y = v1.Y / regionWidth;
            float w2x = v2.X / regionWidth, w2y = v2.Y / regionWidth;
            float w3x = v3.X / regionWidth, w3y = v3.Y / regionWidth;

            float x1 = v2.X - v1.X, x2 = v3.X - v1.X;
            float y1 = v2.Y - v1.Y, y2 = v3.Y - v1.Y;
            float z1 = v2.Z - v1.Z, z2 = v3.Z - v1.Z;
            float s1 = w2x - w1x, s2 = w3x - w1x;
            float t1 = w2y - w1y, t2 = w3y - w1y;

            float rd = s1 * t2 - s2 * t1;
            float rr = (rd * rd) > 1.192092896e-07F ? 1.0f / rd : (rd > 0.0f ? 1024f : -1024f);

            var sdir = new Vector3((t2 * x1 - t1 * x2) * rr, (t2 * y1 - t1 * y2) * rr, (t2 * z1 - t1 * z2) * rr);
            var tdir = new Vector3((s1 * x2 - s2 * x1) * rr, (s1 * y2 - s2 * y1) * rr, (s1 * z2 - s2 * z1) * rr);

            tan1[i1] += sdir; tan1[i2] += sdir; tan1[i3] += sdir;
            tan2[i1] += tdir; tan2[i2] += tdir; tan2[i3] += tdir;
        }

        for (int a = 0; a < count; a++)
        {
            Vector3 nrm = verts[first + a].Normal;
            Vector3 t = tan1[a];
            var ncrosst = new Vector3(nrm.Y * t.Z - nrm.Z * t.Y, nrm.Z * t.X - nrm.X * t.Z, nrm.X * t.Y - nrm.Y * t.X);
            float ndt = (nrm.X * t.X + nrm.Y * t.Y) + nrm.Z * t.Z;
            var tsubn = new Vector3(t.X - nrm.X * ndt, t.Y - nrm.Y * ndt, t.Z - nrm.Z * ndt);
            float len2 = (tsubn.X * tsubn.X + tsubn.Y * tsubn.Y) + tsubn.Z * tsubn.Z;
            if (len2 > 0.00001f)
            {
                tsubn = SurfacePatch.NormVec(tsubn);
                float hd = (ncrosst.X * tan2[a].X + ncrosst.Y * tan2[a].Y) + ncrosst.Z * tan2[a].Z;
                tangent[first + a] = new Vector4(tsubn, hd < 0f ? -1f : 1f);
            }
            else
            {
                tangent[first + a] = new Vector4(0, 0, 1, 1); // degenerate, make up a value
            }
        }
    }
}
