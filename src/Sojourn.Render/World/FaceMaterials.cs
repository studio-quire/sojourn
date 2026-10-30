using System.Numerics;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Materials;
using Sojourn.Render.Frame;

namespace Sojourn.Render.World;

/// <summary>WaterExclusion: a face drawn only into the water exclusion mask (World/RegionScene.WaterExclusion.cs).</summary>
public enum DrawPass : byte { None, Opaque, Fullbright, Alpha, WaterExclusion }

public readonly record struct FaceClass(DrawPass Pass, uint Flags, float AlphaCutoff, bool Glow);

/// <summary>
/// Which pass each face goes to and the material the shaders read. The classification is a port of the viewer's
/// LLPipeline::getPoolTypeFromTE, LLFace::canRenderAsMask and LLVolumeGeometryManager::genDrawInfo's
/// registerFace logic (newview/pipeline.cpp, llface.cpp, llvovolume.cpp; LGPL 2.1, Linden Research, Inc.),
/// collapsed onto our passes: the viewer's simple/bump/shiny/material/PBR pools all write the G-buffer.
/// </summary>
public static class FaceMaterials
{
    static readonly float[] ShininessToAlpha = { 0f, 63f / 255f, 127f / 255f, 191f / 255f };   // SHININESS_TO_ALPHA as the U8 vertex alpha holds it

    public static FaceClass Classify(Primitive.TextureEntryFace te, SceneTexture? diffuse, LegacyMaterial? mat, AssetMaterial? gltf,
        bool rigged = false)
    {
        float glow = te.Glow;
        var color = te.RGBA;

        // getPoolTypeFromTE
        bool colorAlpha = color.A < 0.999f;
        bool alphaPool = colorAlpha;
        if (diffuse is { State: TexState.Ready })
            alphaPool = alphaPool || diffuse.Components == 4 || diffuse.Components == 2;
        if (alphaPool && mat != null)
        {
            alphaPool = mat.DiffuseAlphaMode switch
            {
                LegacyMaterialAlphaMode.Blend => true,
                _ => colorAlpha,
            };
        }
        if (gltf != null && gltf.AlphaMode == GltfAlphaMode.Blend) alphaPool = true;
        if (gltf != null && gltf.AlphaMode != GltfAlphaMode.Blend) alphaPool = false;   // GLTF ignores TE alpha

        bool fullbright = te.Fullbright;
        bool blinnPhongTransparent = color.A < 0.999f;
        bool isAlpha = alphaPool;
        if (gltf == null) isAlpha |= blinnPhongTransparent;
        bool glowPass = !isAlpha && glow > 0f;

        if (gltf != null)
        {
            uint f = MaterialFlags.Pbr | (gltf.DoubleSided ? MaterialFlags.DoubleSided : 0);
            return gltf.AlphaMode switch
            {
                GltfAlphaMode.Blend => new FaceClass(DrawPass.Alpha, f | MaterialFlags.AlphaBlend, 0, glow > 0),
                GltfAlphaMode.Mask => new FaceClass(DrawPass.Opaque, f | MaterialFlags.AlphaMask, gltf.AlphaCutoff, glowPass),
                _ => new FaceClass(DrawPass.Opaque, f, 0, glowPass),
            };
        }

        if (mat != null)
        {
            uint f = MaterialFlags.HasLegacyMaterial | (te.Fullbright ? MaterialFlags.Fullbright : 0);
            float cutoff = mat.AlphaMaskCutoff / 255f;
            if (te.Fullbright)
            {
                if (mat.DiffuseAlphaMode == LegacyMaterialAlphaMode.Mask)
                    return color.A >= 0.999f
                        ? new FaceClass(DrawPass.Fullbright, f | MaterialFlags.AlphaMask, cutoff, glowPass)
                        : new FaceClass(DrawPass.Alpha, f | MaterialFlags.AlphaBlend, 0, glow > 0);
                if (isAlpha) return new FaceClass(DrawPass.Alpha, f | MaterialFlags.AlphaBlend, 0, glow > 0);
                if (mat.EnvironmentIntensity == 0 && te.Shiny == Shininess.None)
                    return color.A >= 0.999f
                        ? new FaceClass(DrawPass.Fullbright, f, 0, glowPass)
                        : new FaceClass(DrawPass.Alpha, f | MaterialFlags.AlphaBlend, 0, glow > 0);
                // material pass with fullbright: deferred, emissive = 1
            }
            else if (blinnPhongTransparent)
                return new FaceClass(DrawPass.Alpha, f | MaterialFlags.AlphaBlend, 0, glow > 0);

            // material pass (getShaderMask -> pass[]): alpha mode decides; a material claiming blend on an
            // opaque face is treated as none (the viewer's own HACK)
            var mode = mat.DiffuseAlphaMode;
            if (mode == LegacyMaterialAlphaMode.Blend && !isAlpha) mode = LegacyMaterialAlphaMode.None;
            return mode switch
            {
                LegacyMaterialAlphaMode.Blend => new FaceClass(DrawPass.Alpha, f | MaterialFlags.AlphaBlend, 0, glow > 0),
                LegacyMaterialAlphaMode.Mask => new FaceClass(DrawPass.Opaque, f | MaterialFlags.AlphaMask, cutoff, glowPass),
                LegacyMaterialAlphaMode.Emissive => new FaceClass(DrawPass.Opaque, f | MaterialFlags.AlphaEmissive, 0, glowPass),
                _ => new FaceClass(DrawPass.Opaque, f, 0, glowPass),
            };
        }

        if (isAlpha)
        {
            if (color.A <= 0f) return new FaceClass(DrawPass.None, 0, 0, false);   // PASS_ALPHA_INVISIBLE
            if (!rigged && CanRenderAsMask(te, diffuse))   // "never auto alpha-mask rigged faces" (LLFace::canRenderAsMask)
            {
                // auto alpha mask (RenderAutoMaskAlphaDeferred / NonDeferred both default on)
                return new FaceClass(te.Fullbright ? DrawPass.Fullbright : DrawPass.Opaque,
                    MaterialFlags.AlphaMask | (te.Fullbright ? MaterialFlags.Fullbright : 0), 0.5f, false);
            }
            return new FaceClass(DrawPass.Alpha, MaterialFlags.AlphaBlend | (te.Fullbright ? MaterialFlags.Fullbright : 0), 0, glow > 0);
        }

        uint shiny = ShinyIndex(te);
        if (fullbright) return new FaceClass(DrawPass.Fullbright, MaterialFlags.Fullbright | (shiny << MaterialFlags.ShinyShift), 0, glowPass);
        return new FaceClass(DrawPass.Opaque, shiny << MaterialFlags.ShinyShift, 0, glowPass);
    }

    /// <summary>0..3: LibreMetaverse keeps shininess in the TE byte's top two bits (Low = 0x40 .. High = 0xC0).</summary>
    public static uint ShinyIndex(Primitive.TextureEntryFace te) => ((uint)te.Shiny >> 6) & 3;

    // LLFace::canRenderAsMask (non-GLTF, non-rigged, no blend material, opaque TE colour, no glow, mask-like alpha)
    static bool CanRenderAsMask(Primitive.TextureEntryFace te, SceneTexture? diffuse) =>
        te.RGBA.A == 1.0f && te.Glow == 0f && diffuse is { State: TexState.Ready, IsAlphaMask: true };

    public static GpuTexRef TeRef(SceneTexture? tex, float repeatU, float repeatV, float offsetU, float offsetV, float rotation) => new()
    {
        Index = tex is { State: TexState.Ready } t ? t.Slot : -1,
        Scale = new Vector2(repeatU, repeatV),
        Offset = new Vector2(offsetU, offsetV),
        Rotation = rotation,
    };

    public static GpuTexRef KhrRef(SceneTexture? tex, in GltfTextureTransform x) => new()
    {
        Index = tex is { State: TexState.Ready } t ? t.Slot : -1,
        Scale = new Vector2(x.Scale.X, x.Scale.Y),
        Offset = new Vector2(x.Offset.X, x.Offset.Y),
        Rotation = x.Rotation,
    };

    /// <summary>
    /// The bump code whose normal map an opaque, non-fullbright legacy face samples, or 0: the viewer's
    /// use_legacy_bump (code 1-17, no LLMaterial normal map) for faces it draws in PASS_BUMP. Fullbright faces get
    /// a post-deferred emboss pass instead (not drawn here) and alpha faces none.
    /// </summary>
    public static int LegacyBump(Primitive.TextureEntryFace te, FaceClass fc, LegacyMaterial? mat)
    {
        int code = (int)te.Bump & 0x1f;
        if (code is 0 or >= 18 || fc.Pass != DrawPass.Opaque || (fc.Flags & MaterialFlags.Fullbright) != 0) return 0;
        if (mat != null && mat.NormalMap != UUID.Zero) return 0;
        return code;
    }

    /// <summary>The GPU material for a legacy face (with or without an LLMaterial); <paramref name="bump"/> is the
    /// legacy bump normal map when <see cref="LegacyBump"/> gave a code.</summary>
    public static GpuMaterial Legacy(Primitive.TextureEntryFace te, FaceClass fc, SceneTexture? diffuse, LegacyMaterial? mat,
        SceneTexture? normal, SceneTexture? specular, SceneTexture? bump = null)
    {
        var c = te.RGBA;
        var m = new GpuMaterial
        {
            Color = new Vector4(c.R, c.G, c.B, c.A),
            Diffuse = TeRef(diffuse ?? null, te.RepeatU, te.RepeatV, te.OffsetU, te.OffsetV, te.Rotation),
            Normal = GpuTexRef.None,
            Specular = GpuTexRef.None,
            Emissive = GpuTexRef.None,
            Glow = te.Glow,
            AlphaCutoff = fc.AlphaCutoff,
            Flags = fc.Flags,
            Bump = (uint)te.Bump,
        };
        if (diffuse is { State: not TexState.Ready }) m.Diffuse.Index = Bindless_Grey;   // still loading: the viewer shows grey
        if (bump != null)
        {
            // bumpV/bumpF: the normal map shares the diffuse UVs; the G-buffer gets spec = gloss = env = the vertex
            // alpha, which holds the shininess unless an LLMaterial has a specular map (then the TE alpha). bumpF
            // ignores the rest of a material.
            m.Normal = TeRef(bump, te.RepeatU, te.RepeatV, te.OffsetU, te.OffsetV, te.Rotation);
            float s = mat != null && mat.SpecularMap != UUID.Zero ? c.A : ShininessToAlpha[ShinyIndex(te)];
            m.SpecColor = new Vector4(s, s, s, s);
            m.EnvIntensity = s;
            m.Flags &= ~MaterialFlags.HasLegacyMaterial;
        }
        else if (mat != null)
        {
            // LLMaterial: spec colour, glossiness from the exponent, env intensity; maps carry their own transforms
            m.SpecColor = new Vector4(mat.SpecularColor.R, mat.SpecularColor.G, mat.SpecularColor.B, mat.SpecularExponent / 255f);
            m.EnvIntensity = mat.EnvironmentIntensity / 255f;
            if (normal != null)
                m.Normal = TeRef(normal, (float)mat.NormalMapRepeatX, (float)mat.NormalMapRepeatY, (float)mat.NormalMapOffsetX, (float)mat.NormalMapOffsetY, (float)mat.NormalMapRotation);
            if (specular != null)
                m.Specular = TeRef(specular, (float)mat.SpecularMapRepeatX, (float)mat.SpecularMapRepeatY, (float)mat.SpecularMapOffsetX, (float)mat.SpecularMapOffsetY, (float)mat.SpecularMapRotation);
            if (normal is { State: not TexState.Ready }) m.Normal.Index = -1;
            if (specular is { State: not TexState.Ready }) m.Specular.Index = -1;
        }
        else
        {
            // shininess rides in vertex alpha for opaque legacy faces (LLFace SHININESS_TO_ALPHA)
            float s = ShininessToAlpha[ShinyIndex(te)];
            m.SpecColor = new Vector4(s, s, s, s);
            m.EnvIntensity = s;
        }
        return m;
    }

    const int Bindless_Grey = Gpu.Bindless.Grey;

    /// <summary>The GPU material for a GLTF/PBR face (base material with the face's override applied).</summary>
    public static GpuMaterial Pbr(Primitive.TextureEntryFace te, FaceClass fc, AssetMaterial gltf, SceneTexture?[] tex)
    {
        var bc = gltf.BaseColorFactor;
        var x = gltf.TextureTransforms ?? new GltfTextureTransform[AssetMaterial.TEXTURE_COUNT];
        GltfTextureTransform X(int i) => i < x.Length ? x[i] : GltfTextureTransform.Default;
        var m = new GpuMaterial
        {
            Color = new Vector4(bc.R, bc.G, bc.B, bc.A),
            SpecColor = new Vector4(gltf.EmissiveFactor.X, gltf.EmissiveFactor.Y, gltf.EmissiveFactor.Z, 0),
            Diffuse = KhrRef(tex[0], X(0)),
            Normal = KhrRef(tex[1], X(1)),
            Specular = KhrRef(tex[2], X(2)),
            Emissive = KhrRef(tex[3], X(3)),
            Glow = te.Glow,
            AlphaCutoff = fc.AlphaCutoff,
            Metallic = gltf.MetallicFactor,
            Roughness = gltf.RoughnessFactor,
            Flags = fc.Flags,
        };
        return m;
    }
}
