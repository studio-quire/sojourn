using LibreMetaverse;
using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;

namespace Sojourn.Render.World;

/// <summary>
/// Water exclusion surfaces (the viewer's since 7.1.12; "Hide water" in the build floater's texture tab): an invisiprim
/// face hides the water where it's in front of the water's surface (boat hulls, basements, pools below sea level). A
/// face is one when (llvovolume.cpp genDrawInfo, PASS_INVISIBLE) its texture is one of the two invisiprim alpha
/// gradients, its colour's alpha is at least 0.999, it has neither a legacy material nor a glTF one (the panel's "Hide
/// water" box clears them), and it isn't on an attachment (HUDs included) or a rigged mesh. Such faces are drawn into the
/// water exclusion mask alone (Frame/Renderer.WaterMask.cs); other invisiprim faces are drawn nowhere, as before.
/// Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed partial class RegionScene
{
    static readonly FaceClass WaterExclusionClass = new(DrawPass.WaterExclusion, 0, 0f, false);
    static readonly GpuMaterial WaterExclusionMaterial = new()
    {
        Diffuse = GpuTexRef.None, Normal = GpuTexRef.None, Specular = GpuTexRef.None, Emissive = GpuTexRef.None,
    };

    static bool Invisiprim(Primitive.TextureEntryFace te) => te.TextureID == InvisiprimA || te.TextureID == InvisiprimB;

    /// <summary>An invisiprim face's class: the water exclusion mask's, or none.</summary>
    static void ClassifyInvisiprim(SceneObject obj, Primitive.TextureEntryFace te, FaceGeometry face, UUID[]? renderMaterials,
        out GpuMaterial mat, out FaceClass fc)
    {
        mat = WaterExclusionMaterial;
        bool gltf = te.RenderMaterialID != UUID.Zero
            || renderMaterials != null && face.TeIndex < renderMaterials.Length && renderMaterials[face.TeIndex] != UUID.Zero;
        bool excludes = obj.Owner == 0 && face.Weights.Count == 0 && te.RGBA.A >= 0.999f && te.MaterialID == UUID.Zero && !gltf;
        fc = excludes ? WaterExclusionClass : default;
    }
}
