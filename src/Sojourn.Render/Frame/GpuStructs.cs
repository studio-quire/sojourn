using System.Numerics;
using System.Runtime.InteropServices;

namespace Sojourn.Render.Frame;

// Mirrors of the GLSL structs in Shaders/common.glsl (scalar block layout: tightly packed, 4-byte aligned).

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuView
{
    public Matrix4x4 View, Proj, ViewProj, InvView, InvProj, InvViewProj;
    public Vector4 CameraPos;     // xyz region coords, w = near
    public Vector4 Viewport;      // w, h, 1/w, 1/h
    public Vector4 SunDir;        // region space, toward the light; w = 1 if sun, 0 if moon
    public Vector4 FarClip;       // x = draw distance, y = water height, z = time (s), w = unused
    public Vector4 ClipPlane;     // region space: clipped where dot(xyz, p) + w < 0 (a mirror's reflection); (0,0,0,1) none
    public uint Mask;             // ViewMask: which instances it draws (InstanceFlags)
    public uint TilesLo, TilesHi, TileCols;   // the eye's light tiles (Renderer.Tiles.cs), 0 for none
    public uint LensMask;                     // the eye's lens mask's slot + 1 (Renderer.Lens.cs), 0 for none
    public uint LayersLo, LayersHi, LayerMin; // the eye's see-through layer records and the cap's least share of a layer (Renderer.Layers.cs), 0 for none
}

/// <summary>Kinds of view (GpuView.Mask), and the instances only one kind draws (GpuInstance.Flags): the person's own
/// body is drawn headless for their eyes, whole in a mirror.</summary>
public static class ViewMask
{
    public const uint Main = 1, Mirror = 2;
}

public static class InstanceFlags
{
    public const uint MainOnly = ViewMask.Main, MirrorOnly = ViewMask.Mirror;
    /// <summary>The person's own body (and what it wears) as the headset's eyes draw it: marked in the G-buffer so the
    /// hands can be drawn over the menus (Realtime/Overlay.Hands.cs). Not a view kind.</summary>
    public const uint Self = 4;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuInstance
{
    public Matrix4x4 Model;       // object (unit-scale volume space scaled) -> region
    public Vector4 N0, N1, N2;    // rows of the normal matrix (inverse transpose of the model's 3x3)
    public uint Material;
    public uint ObjectId;
    public uint Flags;
    public float Glow;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuTexRef
{
    public int Index;             // bindless slot; -1 = none
    public float Rotation;        // radians
    public Vector2 Scale;         // repeats
    public Vector2 Offset;
    public static GpuTexRef None => new() { Index = -1, Scale = Vector2.One };
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuMaterial
{
    public Vector4 Color;         // legacy: TE color (sRGB rgb, alpha); PBR: base color factor (linear)
    public Vector4 SpecColor;     // legacy: specular color rgb (sRGB) + glossiness (0..1); PBR: emissive factor rgb
    public GpuTexRef Diffuse;     // legacy diffuse / PBR base color
    public GpuTexRef Normal;      // legacy normal map / PBR normal
    public GpuTexRef Specular;    // legacy specular map / PBR metallic-roughness
    public GpuTexRef Emissive;    // PBR emissive
    public float Glow;
    public float EnvIntensity;
    public float AlphaCutoff;
    public float Metallic;
    public float Roughness;
    public uint Flags;
    public uint Bump;             // legacy bump preset (0 none, 1 brightness, 2 darkness, 3+ library bumps)
    public float ShadowAlphaCutoff; // Foliage: minimum_alpha of its shadow pass (treeShadowF)
}

public static class MaterialFlags
{
    public const uint AlphaNone = 0, AlphaBlend = 1, AlphaMask = 2, AlphaEmissive = 3, AlphaModeMask = 3;
    public const uint Fullbright = 1 << 2;
    public const uint Pbr = 1 << 3;
    public const uint DoubleSided = 1 << 4;
    public const int ShinyShift = 5;                 // 2 bits
    public const uint Media = 1 << 7;
    public const uint HasLegacyMaterial = 1 << 8;
    public const uint Terrain = 1 << 9;
    public const uint Water = 1 << 10;
    public const uint Avatar = 1 << 11;
    public const uint Sky = 1 << 12;
    public const uint Foliage = 1 << 13;           // Linden tree/grass: exact cutoffs (no mask bias), own shadow cutoff
    public const uint TerrainPbr = 1 << 14;        // with Terrain: PBR terrain, its four materials the next ones in the list
}

/// <summary>A projector (spot light with a texture) in region space; mirrors Projector in projectors.frag.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuProjector
{
    public Matrix4x4 RegionToTc;      // region -> projector texture space (row-vector convention)
    public Vector4 CenterSize;        // light position, radius * 1.5
    public Vector4 ColorFalloff;      // linear rgb * intensity, falloff * 0.5
    public Vector4 OriginRange;       // frustum apex, far - near
    public Vector4 NormalFocus;       // projection direction (proj_n), focus
    public Vector4 PlaneAmbiance;     // a point just past the near plane (proj_p), ambiance
    public int Texture;               // bindless slot, -1 = white
    public float Lod;                 // log2(texture width)
    public int ShadowIndex;           // -1, or which of the two projector shadow maps
    public float ShadowFade;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuLight
{
    public Vector4 PositionRadius;    // region xyz, radius
    public Vector4 ColorFalloff;      // linear rgb * intensity, falloff
    public Vector4 DirectionCutoff;   // spot direction (region), cos cutoff; w <= -2 for point lights
    public Vector4 Params;            // x = projector texture slot (-1 none), y = fov, z = focus, w = ambiance
}

[StructLayout(LayoutKind.Sequential)]
public struct DrawIndexedIndirect
{
    public uint IndexCount, InstanceCount, FirstIndex;
    public int VertexOffset;
    public uint FirstInstance;
}
