using Sojourn.Render.Frame;
using Sojourn.Render.Shadows;

namespace Sojourn.Render.World;

/// <summary>The scene's share of a view built in parts (World/DrawPlan.cs): the same work as BuildDrawList, cut where
/// it can run at once on several threads. Each part draws different objects (a column's, a wearer's) into a segment of
/// its own with a context of its own; the stores they share are safe for that (textures, geometry, materials, the
/// persistent tables, Avatars' palettes per segment).</summary>
public sealed partial class RegionScene
{
    /// <summary>Add this scene's parts for a view to <paramref name="plan"/>, in the order BuildDrawList draws.</summary>
    public void Plan(DrawPlan plan, SceneHooks hooks, ViewSetup v, DrawList view, Vector3 offset, ViewOptions opt, Frustum frustum,
        ShadowFrustum?[]? casters = null, bool objects = true, bool water = true)
    {
        LastUsed = System.Environment.TickCount64;
        var axis = Vector3.Normalize(new Vector3(-v.View.M13, -v.View.M23, -v.View.M33));
        var assets = Source.Assets;
        var sim = Source.Simulator;
        DrawCtx Ctx() => new(hooks, v, plan.Segment(view), offset, opt, frustum, casters, axis, assets, sim);
        if (objects)
        {
            var lights = plan.NewPart(DrawPlan.PartKind.Lights, this);
            lights.Ctx = Ctx();
            // the columns in view or reaching a shadow map, in runs of about PartObjects objects (culling the rest here)
            DrawPlan.Part? run = null;
            int inRun = 0;
            foreach (var cell in Columns())
            {
                if (cell.Stale) cell.Refit();
                var rmin = cell.ReachMin + offset;
                var rmax = cell.ReachMax + offset;
                if (BoxBeyond(v, rmin, rmax)) { lights.Ctx.Stats.Culled += cell.Objects.Count; continue; }
                var rc = (rmin + rmax) * 0.5f;
                float rr = Vector3.Distance(rmin, rmax) * 0.5f;
                bool seen = frustum.Sphere(rc, rr);
                int cellCasters = CasterMask(casters, rmin, rmax);
                if (!seen && cellCasters == 0) { lights.Ctx.Stats.Culled += cell.Objects.Count; cell.KeepNear(); continue; }
                if (run == null || inRun >= DrawPlan.PartObjects)
                {
                    run = plan.NewPart(DrawPlan.PartKind.Columns, this);
                    run.Ctx = Ctx();
                    inRun = 0;
                }
                run.Cells.Add((cell, cellCasters, seen && frustum.Contains(rc, rr)));
                inRun += cell.Objects.Count;
            }
            // the wearers, each wholly in one part (their palettes and impostor captures are theirs)
            if (hooks.AttachmentFrame != null)
            {
                run = null;
                foreach (var (owner, list) in _attachments)
                {
                    if (run == null || inRun >= DrawPlan.PartObjects)
                    {
                        run = plan.NewPart(DrawPlan.PartKind.Attachments, this);
                        run.Ctx = Ctx();
                        inRun = 0;
                    }
                    run.Owners.Add(owner);
                    inRun += list.Count * 4;   // worn things cost more than most (rigged, many faces)
                }
            }
        }
        var terrain = plan.NewPart(DrawPlan.PartKind.Terrain, this);
        terrain.Ctx = Ctx();
        terrain.Water = water;
    }

    /// <summary>Draw one part of a plan into its segment (any thread but for terrain parts: the render thread).</summary>
    internal void DrawPart(DrawPlan.Part p)
    {
        var ctx = p.Ctx;
        var hooks = ctx.Hooks;
        switch (p.Kind)
        {
            case DrawPlan.PartKind.Lights:
                foreach (var obj in _lights) AddLightOf(ctx, obj, obj.WPos, obj.WRot);
                break;
            case DrawPlan.PartKind.Columns:
                foreach (var (cell, casters, inView) in p.Cells)
                {
                    ctx.CellCasters = casters;
                    ctx.CellInView = inView;
                    var list = cell.Objects;
                    for (int i = 0; i < list.Count; i++)
                        if (!list[i].Skip) DrawObject(ctx, list[i], list[i].WPos, list[i].WRot);
                }
                ctx.CellCasters = -1;
                ctx.CellInView = false;
                break;
            case DrawPlan.PartKind.Attachments:
                foreach (var owner in p.Owners)
                    if (_attachments.TryGetValue(owner, out var worn)) DrawWorn(ctx, owner, worn);
                break;
            case DrawPlan.PartKind.Terrain:
                var v = ctx.V;
                Terrain.BuildDrawList(v, ctx.Dl, ctx.Offset, ctx.Frustum, ref ctx.Stats, ctx.Casters);
                if (ctx.Offset == Vector3.Zero && v.HasWater && p.Water) AddWater(v, ctx.Dl, ref ctx.Stats);
                if (ctx.Offset == Vector3.Zero) SkyTextures(v, ref ctx.Stats);
                break;
        }
    }
}
