using LibreMetaverse;
using Sojourn.Render.Foliage;
using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;
using Sojourn.Render.Shadows;

namespace Sojourn.Render.World;

// Linden trees and grass as the viewer's deferred renderer draws them. LLDrawPoolTree (treeF.glsl) and
// LLDrawPoolGrass (diffuseAlphaMaskF.glsl) both write the G-buffer with texture x white, alpha cut at 0.5, no specular
// or environment, the HAS_ATMOS flag, back faces culled (every quad comes with a reversed twin). Both cast sun shadows
// through treeShadowF.glsl: trees cut at 0.5 (LLDrawPoolTree::beginShadowPass), grass at ALPHA_BLEND_CUTOFF 0.598
// (LLPipeline::generateSunShadow's "shadow alpha grass"); polygon offset 1/1 like every other caster. Probe captures
// include both (cubeSnapshot hides only avatars and particles).
public sealed partial class RegionScene
{
    const float TreeAlphaCutoff = 0.5f, TreeShadowAlphaCutoff = 0.5f;
    const float GrassAlphaCutoff = 0.5f, GrassShadowAlphaCutoff = 0.598f;

    static int TreeSpeciesOf(Primitive prim) => FoliageSpecies.Tree((int)prim.TreeSpecies).Id;
    static int GrassSpeciesOf(Primitive prim) => FoliageSpecies.Grass(prim.PrimData.State).Id;

    /// <summary>The camera that picks foliage LODs: a probe capture draws at the main camera's LODs, as the viewer does
    /// (its cube snapshots don't re-run LOD).</summary>
    static FoliageCamera LodCamera(ViewSetup v, Vector3 offset)
    {
        var lv = v.LodView ?? v;
        return new FoliageCamera(lv.Position - offset, lv.VFov, (int)lv.Width, (int)lv.Height);
    }

    /// <summary>Region-local box for the shadow fit: the viewer's spatial extents for trees, the built clump for grass.</summary>
    (Vector3 Min, Vector3 Max) FoliageExtents(SceneObject obj, Vector3 pos, Quaternion rot)
    {
        var scale = V(obj.Prim.Scale);
        if (obj.Kind == ShapeKind.Tree) return TreeBuilder.Extents(TreeSpeciesOf(obj.Prim), pos, rot, scale);
        if (obj.Grass is { Ready: true, Evicted: false } g) return (g.Min, g.Max);
        return GrassGuess(pos, scale);
    }

    // blades scatter normally around the object (sd 0.5 x scale) and stand up to ~1 m; until the clump exists
    static (Vector3 Min, Vector3 Max) GrassGuess(Vector3 pos, Vector3 scale)
    {
        float r = 1.5f * MathF.Max(scale.X, scale.Y) + 1f;
        return (pos - new Vector3(r, r, r), pos + new Vector3(r, r, r));
    }

    void AddTree(DrawCtx ctx, SceneObject obj, Vector3 pos, Quaternion rot, ShadowFrustum?[]? casters)
    {
        var (v, offset, opt) = (ctx.V, ctx.Offset, ctx.Opt);
        var prim = obj.Prim;
        int species = TreeSpeciesOf(prim);
        var scale = V(prim.Scale);
        // draw distance against the viewer's extents (what its spatial partition culls with)
        var (emin, emax) = TreeBuilder.Extents(species, pos, rot, scale);
        if (v.Beyond((emin + emax) * 0.5f + offset, Vector3.Distance(emin, emax) * 0.5f)) { ctx.Stats.Culled++; return; }
        int lod = TreeBuilder.ChooseLod(species, pos, rot, scale, LodCamera(v, offset), opt.TreeLodFactor, opt.LodFactor);
        if (lod >= TreeBuilder.LodCount) { ctx.Stats.Culled++; return; }   // the viewer stops drawing it

        var shape = _world.Geometry.RequestFoliage(new GeometryKey(ShapeKind.Tree, UUID.Zero, species, lod, Vector3.Zero),
            () => TreeBuilder.Build(species, lod));
        if (!shape.Ready) { if (!shape.Failed) ctx.Stats.Pending++; return; }

        // frustum and shadow culling against the real mesh: the viewer's extents box can clip the crown
        var model = TreeBuilder.ModelMatrix(pos + offset, rot, scale);
        var center = Vector3.Transform((shape.Min + shape.Max) * 0.5f, model);
        float radius = Vector3.Distance(shape.Min, shape.Max) * 0.5f * scale.Length() * 0.05f;
        AddFoliageDraw(ctx, prim, shape.Faces[0].Range, model, FoliageSpecies.Tree(species).Texture, TreeAlphaCutoff, TreeShadowAlphaCutoff,
            center, radius, casters);
    }

    void AddGrass(DrawCtx ctx, SceneObject obj, Vector3 pos, ShadowFrustum?[]? casters)
    {
        var (v, offset, opt) = (ctx.V, ctx.Offset, ctx.Opt);
        var prim = obj.Prim;
        var scale = V(prim.Scale);
        var (gmin, gmax) = obj.Grass is { Ready: true, Evicted: false } built ? (built.Min, built.Max) : GrassGuess(pos, scale);
        if (v.Beyond((gmin + gmax) * 0.5f + offset, Vector3.Distance(gmin, gmax) * 0.5f)) { ctx.Stats.Culled++; return; }
        // LLVOGrass::updateLOD: blade count from scale over distance, powers of two up to 32. Blade i never depends on
        // the count, so one 32-blade mesh per object serves every count as an index prefix.
        int blades = GrassBuilder.ChooseBladeCount(scale, Vector3.Distance(pos, LodCamera(v, offset).Position), opt.TreeLodFactor);
        obj.GrassBlades = blades;
        if (blades == 0) return;
        if (Terrain.Surface is not { } surface) { ctx.Stats.Pending++; return; }

        int species = GrassSpeciesOf(prim);
        var at = new Vector3(pos.X, pos.Y, 0);   // blades sit on the terrain; object Z and rotation don't matter
        var key = new GeometryKey(ShapeKind.Grass, prim.ID, species, Terrain.Revision, scale, at);
        if (obj.GrassKey != key)
        {
            ReleaseGrass(obj);
            obj.GrassKey = key;
        }
        var shape = obj.Grass = _world.Geometry.RequestFoliage(key,
            () => GrassBuilder.Build(species, GrassBladeTable.MaxBlades, at, scale, surface.ResolveHeightRegion));
        if (!shape.Ready) { if (!shape.Failed) ctx.Stats.Pending++; return; }

        var range = shape.Faces[0].Range with { IndexCount = (uint)(blades * 12) };
        var center = (shape.Min + shape.Max) * 0.5f + offset;
        float radius = Vector3.Distance(shape.Min, shape.Max) * 0.5f;
        AddFoliageDraw(ctx, prim, range, Matrix4x4.CreateTranslation(offset), FoliageSpecies.Grass(species).Texture, GrassAlphaCutoff, GrassShadowAlphaCutoff,
            center, radius, casters);
    }

    void AddFoliageDraw(DrawCtx ctx, Primitive prim, MeshRange range, in Matrix4x4 model, UUID texture, float alphaCutoff, float shadowAlphaCutoff,
        Vector3 center, float radius, ShadowFrustum?[]? casters)
    {
        if (range.IsEmpty) return;
        var (v, dl) = (ctx.V, ctx.Dl);
        bool visible = ctx.Frustum.Sphere(center, radius);
        int casterMask = 0;
        if (casters != null)
        {
            var bmin = center - new Vector3(radius);
            var bmax = center + new Vector3(radius);
            for (int c = 0; c < casters.Length; c++)
                if (casters[c] is { } cf && cf.IntersectsAabb(bmin, bmax)) casterMask |= 1 << c;
            if (shadowAlphaCutoff == GrassShadowAlphaCutoff) casterMask &= ~(1 << ViewSetup.FarCascade);   // blades: below the far map's texels
        }
        if (!visible && casterMask == 0)
        {
            ctx.Stats.Culled++;
            if (_world.Textures.TryGet(texture, out var near)) near.KeepNear(System.Environment.TickCount64);   // out of view: near
            return;
        }

        var tex = _world.Textures.Request(texture, Source.Assets);
        if (tex.State == TexState.Loading) { ctx.Stats.Pending++; return; }
        if (tex.State != TexState.Ready) return;

        Matrix4x4.Invert(model, out var inv);
        var nm = Matrix4x4.Transpose(inv);
        uint material = dl.AddMaterial(new GpuMaterial
        {
            Color = Vector4.One,
            SpecColor = Vector4.Zero,
            Diffuse = new GpuTexRef { Index = tex.Slot, Scale = Vector2.One },
            Normal = GpuTexRef.None, Specular = GpuTexRef.None, Emissive = GpuTexRef.None,
            AlphaCutoff = alphaCutoff,
            ShadowAlphaCutoff = shadowAlphaCutoff,
            Flags = MaterialFlags.AlphaMask | MaterialFlags.Foliage,
        });
        uint id = dl.AddInstance(new GpuInstance
        {
            Model = model,
            N0 = new Vector4(nm.M11, nm.M12, nm.M13, 0),
            N1 = new Vector4(nm.M21, nm.M22, nm.M23, 0),
            N2 = new Vector4(nm.M31, nm.M32, nm.M33, 0),
            Material = material,
            ObjectId = prim.LocalID,
        });
        var axis = Vector3.Normalize(new Vector3(-v.View.M13, -v.View.M23, -v.View.M33));
        var cmd = new DrawCmd { Range = range, Instance = id, Depth = Vector3.Dot(center - v.Position, axis), Bounds = new Vector4(center, radius) };
        if (visible) dl.Opaque.Add(cmd);
        for (int c = 0; c < dl.ShadowCasters.Length; c++)
            if ((casterMask & (1 << c)) != 0) dl.ShadowCasters[c].Add(cmd);
        ctx.Stats.Objects++;
    }
}
