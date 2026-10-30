// Ported from the Second Life viewer: indra/newview/llvovolume.cpp (LLVOVolume::calcLOD, computeLODDetail),
// indra/llmath/llvolumemgr.cpp (LLVolumeLODGroup::getDetailFromTan), indra/llmath/llvolume.cpp (mLODScaleBias in
// LLVolume::generate), indra/newview/lldrawable.cpp (updateDistance) and indra/newview/llmeshrepository.cpp
// (getActualMeshLOD). Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
using System.Numerics;
using Primitive = LibreMetaverse.Primitive;

namespace Sojourn.Render.Geometry;

public static class VolumeLod
{
    // RenderVolumeLODFactor in the stock viewer's graphics presets (featuretable.txt): Low 1.125, LowMid 1.125,
    // Mid 1.25, MidHigh 1.375, High 1.5, HighUltra 1.75, Ultra 2.0. settings.xml default 1.0; clamped to 0.01..4.
    public const float LodFactorHigh = 1.5f;
    public const float LodFactorHighUltra = 1.75f;
    public const float LodFactorUltra = 2.0f;
    public const float MaxLodFactor = 4.0f;

    // CameraAngle default (vertical FOV, radians) == DEFAULT_FIELD_OF_VIEW (60 degrees).
    public const float DefaultFov = 1.047197551f;
    private const float DEFAULT_FIELD_OF_VIEW = 60f * (MathF.PI / 180f);

    // LLVolumeLODGroup::mDetailThresholds (BASE_THRESHOLD 0.03)
    private static readonly float[] DetailThresholds = { 0.03f, 2 * 0.03f, 8 * 0.03f, 100 * 0.03f };

    public static int DetailFromTan(float tanAngle)
    {
        for (int i = 0; i < 3; i++)
            if (tanAngle <= DetailThresholds[i])
                return i;
        return 3;
    }

    // LLVOVolume::computeLODDetail (RenderDynamicLOD on by default)
    public static int ComputeLodDetail(float distance, float radius, float lodFactor, bool dynamicLod = true)
    {
        if (dynamicLod)
        {
            float tanAngle = (lodFactor * radius) / distance;
            return DetailFromTan(LLMath.Round(tanAngle, 0.01f));
        }
        return Math.Clamp((int)(MathF.Sqrt(radius) * lodFactor * 4f), 0, 3);
    }

    // LLVolume::mLODScaleBias, set in LLVolume::generate. Note the full-byte compare of the profile curve:
    // a cylinder carrying hole-type bits doesn't get the cylinder bias.
    public static Vector3 LodScaleBias(VolumeParams p, bool isMesh = false)
    {
        if (!isMesh)
        {
            if (p.PathCurve == VolumeConsts.PathLine && p.ProfileCurve == VolumeConsts.ProfileCircle)
                return new Vector3(0.6f, 0.6f, 0.0f); // cylinders don't care about Z
            if (p.PathCurve == VolumeConsts.PathCircle)
                return new Vector3(0.6f, 0.6f, 0.6f);
        }
        return new Vector3(0.5f, 0.5f, 0.5f);
    }

    // Radius calcLOD uses for an unrigged prim, sculpt or mesh: |bias * scale|.
    public static float LodRadius(Vector3 objectScale, VolumeParams p, bool isMesh = false) =>
        (LodScaleBias(p, isMesh) * objectScale).Length();

    public static float LodRadius(Vector3 objectScale, Primitive.ConstructionData shape, bool isMesh = false) =>
        LodRadius(objectScale, VolumeParams.FromConstructionData(shape), isMesh);

    // Rigged mesh: radius from the avatar's animated extents (LLVOAvatar::getLastAnimExtents).
    // Control avatars (animesh) use half the diagonal; avatar attachments the whole diagonal (the "2x off" BinRadius legacy).
    public static float RiggedLodRadius(Vector3 animExtentsMin, Vector3 animExtentsMax, bool controlAvatar) =>
        controlAvatar ? (animExtentsMax - animExtentsMin).Length() * 0.5f : (animExtentsMax - animExtentsMin).Length();

    // LLDrawable::updateDistance for volumes: distance from the camera to the centre of the object's world AABB
    // (union of its faces' boxes, LLVOVolume::genBBoxes), rounded to 0.01. For rigged faces calcLOD uses the
    // avatar drawable's distance instead: camera to the centre of the avatar's animated extents.
    public static float CameraDistance(Vector3 boundsCenter, Vector3 cameraPos) =>
        LLMath.Round((boundsCenter - cameraPos).Length(), 0.01f);

    public static float RiggedCameraDistance(Vector3 animExtentsMin, Vector3 animExtentsMax, Vector3 cameraPos) =>
        LLMath.Round(((animExtentsMin + animExtentsMax) * 0.5f - cameraPos).Length(), 0.01f);

    // LLVOVolume::calcLOD: LOD 0..3 for a prim, sculpt or mesh. distance and radius as above (unrounded radius is
    // fine; it's rounded here). cameraFov is the CameraAngle setting (the default FOV, not the zoomed one).
    // Returns -1 where the viewer would leave the LOD unchanged (distance or radius <= 0).
    public static int CalcLod(float distance, float radius, float lodFactor = LodFactorHigh, float cameraFov = DefaultFov,
        bool ignoreFovZoom = false, bool isHud = false, bool dynamicLod = true)
    {
        if (distance <= 0f || radius <= 0f)
            return -1;
        if (isHud)
            return 3; // HUDs always show at highest detail

        lodFactor = Math.Clamp(lodFactor, 0.01f, MaxLodFactor);
        float distanceFactor = 1f - lodFactor * 0.1f; // sDistanceFactor

        distance *= distanceFactor;

        float rampDist = lodFactor * 2;
        if (distance < rampDist)
        {
            // Boost LOD when you're REALLY close
            distance *= 1.0f / rampDist;
            distance *= distance;
            distance *= rampDist;
        }

        distance *= MathF.PI / 3f;

        float adjustedFactor = lodFactor;
        if (!ignoreFovZoom)
            adjustedFactor *= DEFAULT_FIELD_OF_VIEW / cameraFov;

        return ComputeLodDetail(LLMath.Round(distance, 0.01f), LLMath.Round(radius, 0.01f), adjustedFactor, dynamicLod);
    }

    // Convenience: LOD -> the volume detail to build prims/sculpts with.
    public static float DetailFor(float distance, float radius, float lodFactor = LodFactorHigh, float cameraFov = DefaultFov) =>
        VolumeBuilder.DetailForLod(Math.Max(0, CalcLod(distance, radius, lodFactor, cameraFov)));

    // LLMeshRepository::getActualMeshLOD: the wanted LOD if the asset has it, else the next lower one that exists,
    // else the next higher; -1 if none (the viewer then draws the prim's own shape instead, as a proxy).
    // lodSizes = the mesh header's lowest_lod/low_lod/medium_lod/high_lod sizes (0 when absent).
    public static int ActualMeshLod(ReadOnlySpan<int> lodSizes, int lod, int meshVersion = 1)
    {
        lod = Math.Clamp(lod, 0, 3);
        if (meshVersion > 999) // MAX_MESH_VERSION
            return -1;
        if (lodSizes[lod] > 0)
            return lod;
        for (int i = lod - 1; i >= 0; --i)
            if (lodSizes[i] > 0)
                return i;
        for (int i = lod + 1; i < 4; ++i)
            if (lodSizes[i] > 0)
                return i;
        return -1;
    }
}
