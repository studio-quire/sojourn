// Output shape mirrors LLVolumeFace from the Second Life viewer (indra/llmath/llvolume.h).
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
using System.Numerics;

namespace Sojourn.Render.Geometry;

public sealed class VolumeFace
{
    // TextureEntry face this is drawn with. For prims and sculpts this is the volume face index
    // (LLFace::setTEOffset(i)), so faces shift when cuts/hollow add or remove profile faces.
    public int TeIndex;

    // LLFaceID bit of the profile face (VolumeConsts.Face*): which part of the shape this is.
    public ushort FaceId;
    public VolumeFaceType Type;

    // Object space at unit scale (multiply by the prim's scale), same values as the viewer's LLVolumeFace.
    public Vector3[] Positions = Array.Empty<Vector3>();
    // Unit length (the viewer's state once createTangents has run, which it does before planar texgen,
    // bump or any tangent-carrying vertex buffer).
    public Vector3[] Normals = Array.Empty<Vector3>();
    // LLCalculateTangentArray output; w = bitangent sign (bitangent = cross(normal, tangent) * w).
    public Vector4[] Tangents = Array.Empty<Vector4>();
    // The volume's own UVs, before any TextureEntry transform or planar texgen.
    public Vector2[] TexCoords = Array.Empty<Vector2>();
    public uint[] Indices = Array.Empty<uint>();
    // Face extents and centre (LLVolumeFace::mExtents / mCenter), unit scale.
    public Vector3 Min, Max, Center;
}

[Flags]
public enum VolumeFaceType : ushort
{
    // LLVolumeFace type mask bits
    Single = 0x0001,
    Cap = 0x0002,
    End = 0x0004,
    Side = 0x0008,
    Inner = 0x0010,
    Outer = 0x0020,
    Hollow = 0x0040,
    Open = 0x0080,
    Flat = 0x0100,
    Top = 0x0200,
    Bottom = 0x0400,
}
