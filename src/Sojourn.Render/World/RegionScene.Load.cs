using LibreMetaverse;

namespace Sojourn.Render.World;

/// <summary>One linkset of a subject (an object, or one thing a person wears) as last drawn, for describing: its prims,
/// those whose shape hadn't arrived (not drawn at all), and its faces in view by their colour texture (a diffuse, or a
/// GLTF material's base colour): still loading (drawn plain grey, or the material's own colour, often white), missing
/// (never to come), or two levels or more blurrier than the picture wants.</summary>
public sealed record PartLoad(UUID Root, int Prims, int ShapesWaiting, int Faces, int Loading, int Missing, int Blurry);

public sealed partial class RegionScene
{
    /// <summary>A prim by its id (render thread).</summary>
    internal SceneObject? ObjectById(UUID id)
    {
        if (_byId != null && _byId.TryGetValue(id, out uint lid) && _objects.TryGetValue(lid, out var o) && o.Prim.ID == id) return o;
        foreach (var x in _objects.Values)
            if (x.Prim.ID == id) return x;
        return null;
    }

    /// <summary>A linkset's root and its children.</summary>
    internal IEnumerable<SceneObject> LinksetOf(SceneObject root) =>
        _children.TryGetValue(root.LocalId, out var kids) ? kids.Prepend(root) : [root];

    internal SceneObject? Parent(SceneObject o) => o.Prim.ParentID != 0 && _objects.TryGetValue(o.Prim.ParentID, out var p) ? p : null;

    /// <summary>How far the linkset's look has arrived (render thread, after a frame that drew it). Only faces whose
    /// textures a view asked for since <paramref name="since"/> (a TickCount64) count, so a part never in view isn't
    /// judged; <paramref name="owner"/> is the wearer for something worn (bakes on mesh show the wearer's bakes).</summary>
    internal PartLoad LoadOf(UUID rootId, IEnumerable<SceneObject> prims, uint owner, long since)
    {
        var textures = _world.Textures;
        var overrides = Source.HasSimulator ? Source.Simulator.GLTFMaterialOverrides : null;
        int n = 0, shapes = 0, faces = 0, loading = 0, missing = 0, blurry = 0;
        foreach (var obj in prims)
        {
            if (obj.Skip) continue;
            n++;
            var shape = obj.Faces?.Shape is { Ready: true, Evicted: false } drawn ? drawn
                : obj.Lods.LastOrDefault(l => l is { Ready: true, Evicted: false });
            // its faces being resolved off the frame (World/FaceResolver.cs): not drawn yet
            if (shape == null || obj.Faces == null && obj.Resolving != null)
            {
                // asked for and on its way (one never asked for wasn't in view)
                if (shape != null || obj.Lods.Any(l => l is { Ready: false, Failed: false, Evicted: false })) shapes++;
                continue;
            }
            var prim = obj.Prim;
            var mats = Source.RenderMaterialIds(prim.LocalID);
            var ov = overrides != null && overrides.TryGetValue(prim.LocalID, out var o) ? o : null;
            foreach (var face in shape.Faces)
            {
                if (face.Range.IsEmpty) continue;
                var te = prim.Textures?.GetFace((uint)face.TeIndex);
                if (te == null || Invisiprim(te) || te.RGBA.A <= 0.004f) continue;
                UUID id = te.TextureID;
                var matId = te.RenderMaterialID != UUID.Zero ? te.RenderMaterialID
                    : mats != null && face.TeIndex < mats.Length ? mats[face.TeIndex] : UUID.Zero;
                if (matId != UUID.Zero)
                {
                    var gltf = _world.Materials.Gltf(matId, Source.Assets, out bool waiting);
                    if (gltf == null && waiting) { faces++; loading++; continue; }
                    if (gltf != null)
                    {
                        id = gltf.TextureIds is { Length: > 0 } ids ? ids[0] : UUID.Zero;
                        if (ov?.FaceOverrides is { } fo && fo.TryGetValue(face.TeIndex, out var om) && om.TextureIds is { Length: > 0 } oids && oids[0] != UUID.Zero)
                            id = oids[0];
                    }
                }
                id = BakeOnMesh(id, owner);
                if (id == UUID.Zero) continue;   // a plain colour: nothing to load
                // only what a view asked for lately (not in view, never asked: not judged)
                if (!textures.TryGet(id, out var t) || t.LastUsed < since) continue;
                faces++;
                if (t.State == TexState.Loading) loading++;
                else if (t.State == TexState.Missing) missing++;
                else if (textures.LevelsShort(t) >= 2) blurry++;
            }
        }
        return new PartLoad(rootId, n, shapes, faces, loading, missing, blurry);
    }
}
