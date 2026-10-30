// Ported from the Second Life viewer: indra/newview/lldrawpoolterrain.cpp, llvlcomposition.cpp (material type),
// indra/llcommon/indra_constants.cpp, app_settings/settings.xml and featuretable.txt defaults.
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
namespace Sojourn.Render.TerrainGen;

/// <summary>Which terrain shader the viewer picks for a region (LLTerrainMaterials::Type).</summary>
public enum TerrainMaterialType
{
    Texture,
    Pbr,
}

/// <summary>
/// The uniforms and settings LLDrawPoolTerrain binds, for writing the terrain shaders.
/// </summary>
public static class TerrainShading
{
    /// <summary>RenderTerrainScale default (metres per detail-texture repeat).</summary>
    public const float RenderTerrainScale = 12f;
    /// <summary>RenderTerrainPBRScale default (metres per PBR material repeat, before the material's own transform).</summary>
    public const float RenderTerrainPbrScale = 8f;
    /// <summary>RenderTerrainPBRTriplanarBlendFactor default (TERRAIN_TRIPLANAR_BLEND_FACTOR).</summary>
    public const float RenderTerrainPbrTriplanarBlendFactor = 8f;
    /// <summary>
    /// RenderTerrainPBRPlanarSampleCount (TERRAIN_PLANAR_TEXTURE_SAMPLE_COUNT): 1 = planar xy, 3 = triplanar.
    /// featuretable: Low through MidHigh 1, High/HighUltra/Ultra 3; settings.xml 3.
    /// </summary>
    public const int RenderTerrainPbrPlanarSampleCountHigh = 3;
    /// <summary>
    /// RenderTerrainPBRDetail (TERRAIN_PBR_DETAIL), cumulative: -4 base colour only, -3 + metallic-roughness,
    /// -2 + normal map, -1 + occlusion, 0 + emissive. featuretable: Mid and above 0, LowMid -1, Low -4.
    /// </summary>
    public const int RenderTerrainPbrDetailHigh = 0;

    /// <summary>The viewer's default detail textures (dirt, grass, mountain, rock) before RegionInfo arrives.</summary>
    public static readonly Guid[] DefaultDetailTextures =
    [
        new("0bc58228-74a0-7e83-89bc-5c23464bcec5"), // TERRAIN_DIRT_DETAIL
        new("63338ede-0037-c4fd-855b-015d77112fc8"), // TERRAIN_GRASS_DETAIL
        new("303cd381-8560-7579-23f1-f0a880799740"), // TERRAIN_MOUNTAIN_DETAIL
        new("53a2f406-4895-1d13-d541-d2e3b86bc19c"), // TERRAIN_ROCK_DETAIL
    ];

    /// <summary>
    /// LLDrawPoolTerrain::renderFullShaderTextures: object_plane_s/t for the classic shader's
    /// texgen_object (detail UV = (dot(vec4(pos,1), s), dot(vec4(pos,1), t)), then texture_matrix0, which is
    /// identity here). The offset comes from the *agent's* region origin and is applied to every region's
    /// region-local positions, so detail textures are continuous across the agent's region but jump at the
    /// edges of neighbouring regions, as in the viewer.
    /// </summary>
    public static (Vector4 PlaneS, Vector4 PlaneT) DetailTexGen(double agentRegionOriginGlobalX, double agentRegionOriginGlobalY,
        float renderTerrainScale = RenderTerrainScale)
    {
        float detailScale = 1f / renderTerrainScale; // sDetailScale
        float offsetX = (float)(Fmod(agentRegionOriginGlobalX, 1.0 / detailScale) * detailScale);
        float offsetY = (float)(Fmod(agentRegionOriginGlobalY, 1.0 / detailScale) * detailScale);
        return (new Vector4(detailScale, 0f, 0f, offsetX), new Vector4(0f, detailScale, 0f, offsetY));
    }

    private static double Fmod(double a, double b) => a % b;

    /// <summary>
    /// LLTerrainMaterials::getMaterialType: classic textures if any of the four detail assets loads as a
    /// texture, else PBR if any loads as a GLTF material, else textures. ("Ready" = the asset fetched and,
    /// for textures, decoded to at least 128 px or its full size.) The same four TerrainDetail IDs are tried as both.
    /// </summary>
    public static TerrainMaterialType ChooseMaterialType(ReadOnlySpan<bool> textureReady, ReadOnlySpan<bool> materialReady)
    {
        bool anyTexture = false, anyMaterial = false;
        foreach (bool b in textureReady) anyTexture |= b;
        foreach (bool b in materialReady) anyMaterial |= b;
        return anyTexture || !anyMaterial ? TerrainMaterialType.Texture : TerrainMaterialType.Pbr;
    }

    /// <summary>
    /// renderFullShaderPBR: the uniform vec4[5] terrain_texture_transforms. Per material, its base-colour
    /// KHR_texture_transform (scale.xy, rotation, offset.xy) with scale multiplied by 1/RenderTerrainPBRScale,
    /// packed tightly (5 floats each, 20 total). Missing materials use the identity transform.
    /// </summary>
    public static float[] PackPbrTextureTransforms(ReadOnlySpan<PbrTextureTransform> transforms, float renderTerrainPbrScale = RenderTerrainPbrScale)
    {
        float pbrDetailScale = 1f / renderTerrainPbrScale;
        var packed = new float[20];
        for (int i = 0; i < 4; i++)
        {
            var t = i < transforms.Length ? transforms[i] : PbrTextureTransform.Identity;
            packed[i * 5 + 0] = t.ScaleX * pbrDetailScale;
            packed[i * 5 + 1] = t.ScaleY * pbrDetailScale;
            packed[i * 5 + 2] = t.Rotation;
            packed[i * 5 + 3] = t.OffsetX;
            packed[i * 5 + 4] = t.OffsetY;
        }
        return packed;
    }

    /// <summary>
    /// minimum_alphas for the PBR shader: alphaCutoff / baseColor.a for MASK materials, else -0.
    /// </summary>
    public static float PbrMinimumAlpha(bool alphaModeMask, float alphaCutoff, float baseColorAlpha)
        => alphaModeMask ? alphaCutoff / baseColorAlpha : -0.0f;
}

/// <summary>A GLTF KHR_texture_transform (LLGLTFMaterial::TextureTransform).</summary>
public readonly record struct PbrTextureTransform(float ScaleX, float ScaleY, float Rotation, float OffsetX, float OffsetY)
{
    public static PbrTextureTransform Identity => new(1f, 1f, 0f, 0f, 0f);
}
