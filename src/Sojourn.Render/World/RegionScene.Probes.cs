using LibreMetaverse;
using Sojourn.Render.Geometry;
using LVector3 = LibreMetaverse.Vector3;

namespace Sojourn.Render.World;

/// <summary>A volume prim as the viewer's volume octree sees it, for automatic probe placement.</summary>
public readonly record struct ProbeVolume(Vector3 Center, Vector3 Min, Vector3 Max, float BinRadius, Matrix4x4 ToUnitBox, bool Pickable);

/// <summary>A prim flagged as a manual reflection probe (its world transform and params).</summary>
public readonly record struct ManualProbeObject(uint LocalId, Vector3 Position, Quaternion Rotation, Vector3 Scale, ReflectionProbeParams Params);

public sealed partial class RegionScene
{
    // lineSegmentIntersect(pick_transparent = false): invisiprims and wholly transparent prims aren't hit
    static bool Pickable(Primitive prim)
    {
        var t = prim.Textures;
        if (t == null) return true;
        bool Opaque(Primitive.TextureEntryFace? f) => f != null && f.RGBA.A > 0.001f && f.TextureID != InvisiprimA && f.TextureID != InvisiprimB;
        if (t.FaceTextures != null)
            foreach (var f in t.FaceTextures)
                if (f != null && Opaque(f)) return true;
        return Opaque(t.DefaultTexture);
    }

    public float TerrainHeight(float x, float y) =>
        Terrain.Surface is { } s ? s.ResolveHeightRegion(Math.Clamp(x, 0, s.Width - 0.001f), Math.Clamp(y, 0, s.Width - 0.001f)) : 0f;
}
