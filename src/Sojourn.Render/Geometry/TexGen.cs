// Ported from the Second Life viewer: indra/newview/llface.cpp (planarProjection, xform, the texture coordinate
// part of LLFace::getGeometryVolume), indra/llprimitive/llprimitive.cpp (TE offset/rotation dequantisation) and
// app_settings/shaders/class1/deferred/textureUtilV.glsl (texture_transform for PBR faces).
// Copyright (C) 2010, 2023, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
using System.Numerics;

namespace Sojourn.Render.Geometry;

public static class TexGen
{
    // LLTextureEntry::getTexGen values (media flags & 0x06). Only planar changes anything in the viewer;
    // spherical and cylindrical fall through to the default UVs.
    public const byte TexGenDefault = 0x00;
    public const byte TexGenPlanar = 0x02;
    public const byte TexGenSpherical = 0x04;
    public const byte TexGenCylindrical = 0x06;

    // planarProjection(tc, normal, center, vec). normal: the face's unit normal (VolumeFace.Normals; the viewer
    // normalises them before planar texgen). scaledPosition: VolumeFace.Positions[i] * object scale. The face
    // centre argument is unused by the viewer, so it isn't taken here. The basis is picked per vertex from the
    // normal (|n.x| >= 0.5, else the sign of n.y), with no special case for spheres or sculpts: curved faces get
    // seams where a vertex normal crosses those thresholds.
    public static Vector2 Planar(Vector3 normal, Vector3 scaledPosition)
    {
        Vector3 binormal;
        float d = normal.X;
        if (d >= 0.5f || d <= -0.5f)
            binormal = d < 0 ? new Vector3(0, -1, 0) : new Vector3(0, 1, 0);
        else
            binormal = normal.Y > 0 ? new Vector3(-1, 0, 0) : new Vector3(1, 0, 0);
        Vector3 tangent = Vector3.Cross(binormal, normal);
        return new Vector2(
            1.0f + (Vector3.Dot(binormal, scaledPosition) * 2 - 0.5f),
            -(Vector3.Dot(tangent, scaledPosition) * 2 - 0.5f));
    }

    // The viewer's xform(): rotate about (0.5, 0.5), then scale (repeats), then offset. Applied on the CPU when
    // the vertex buffer is built (getGeometryVolume), after planar texgen if any.
    public static Vector2 Transform(Vector2 tc, float rotation, float offsetS, float offsetT, float scaleS, float scaleT)
        => Transform(tc, MathF.Cos(rotation), MathF.Sin(rotation), offsetS, offsetT, scaleS, scaleT);

    private static Vector2 Transform(Vector2 tc, float cosAng, float sinAng, float offS, float offT, float magS, float magT)
    {
        float s = tc.X - 0.5f;
        float t = tc.Y - 0.5f;
        float temp = s;
        s = s * cosAng + t * sinAng;
        t = -temp * sinAng + t * cosAng;
        s *= magS;
        t *= magT;
        s += offS + 0.5f;
        t += offT + 0.5f;
        return new Vector2(s, t);
    }

    // The viewer skips the transform when cos(r)==1, sin(r)==0, offsets 0 and repeats 1.
    public static bool IsIdentity(float rotation, float offsetS, float offsetT, float scaleS, float scaleT) =>
        MathF.Cos(rotation) == 1f && MathF.Sin(rotation) == 0f && offsetS == 0f && offsetT == 0f && scaleS == 1f && scaleT == 1f;

    // Same transform as an affine matrix for a shader: Vector2.Transform(tc, m) == Transform(tc, ...).
    public static Matrix3x2 TransformMatrix(float rotation, float offsetS, float offsetT, float scaleS, float scaleT)
    {
        float c = MathF.Cos(rotation), s = MathF.Sin(rotation);
        return new Matrix3x2(
            c * scaleS, -s * scaleT,
            s * scaleS, c * scaleT,
            -0.5f * (c + s) * scaleS + offsetS + 0.5f,
            -0.5f * (c - s) * scaleT + offsetT + 0.5f);
    }

    // UVs for the diffuse channel of a Blinn-Phong (non-PBR) face, as getGeometryVolume writes them:
    // default or planar texgen, then the TE transform. Normal/specular maps use the same code with the
    // material's own repeats/offset/rotation. Texture animation (llSetTextureAnim) replaces the TE transform
    // with its own matrix and isn't handled here.
    public static void ComputeUVs(VolumeFace face, Vector3 objectScale, byte texGen,
        float rotation, float offsetS, float offsetT, float scaleS, float scaleT, Span<Vector2> dst)
    {
        bool planar = (texGen & 0x06) == TexGenPlanar;
        bool xform = !IsIdentity(rotation, offsetS, offsetT, scaleS, scaleT);
        float c = MathF.Cos(rotation), s = MathF.Sin(rotation);
        for (int i = 0; i < face.Positions.Length; i++)
        {
            Vector2 tc = planar ? Planar(face.Normals[i], face.Positions[i] * objectScale) : face.TexCoords[i];
            dst[i] = xform ? Transform(tc, c, s, offsetS, offsetT, scaleS, scaleT) : tc;
        }
    }

    // PBR (GLTF material) faces: the vertex buffer gets the volume UVs (or planar ones) with no TE transform;
    // the material's KHR_texture_transform runs in the vertex shader instead, wrapped in a v flip because SL UVs
    // are right-handed and glTF's left-handed (texture_transform in textureUtilV.glsl).
    public static void ComputePbrBaseUVs(VolumeFace face, Vector3 objectScale, byte texGen, Span<Vector2> dst)
    {
        bool planar = (texGen & 0x06) == TexGenPlanar;
        for (int i = 0; i < face.Positions.Length; i++)
            dst[i] = planar ? Planar(face.Normals[i], face.Positions[i] * objectScale) : face.TexCoords[i];
    }

    public static Vector2 PbrTextureTransform(Vector2 tc, Vector2 scale, float rotation, Vector2 offset)
    {
        tc.Y = 1.0f - tc.Y;
        tc *= scale;
        float c = MathF.Cos(rotation), s = MathF.Sin(rotation);
        tc = new Vector2(c * tc.X + s * tc.Y, -s * tc.X + c * tc.Y) + offset;
        tc.Y = 1.0f - tc.Y;
        return tc;
    }

    // TE wire values -> floats exactly as LLPrimitive::unpackTEMessage does it.
    public static float DequantizeOffset(short v) => v / (float)0x7FFF;
    public static float DequantizeRotation(short v) => (v / (float)0x08000) * (2f * LLMath.Pi);
}
