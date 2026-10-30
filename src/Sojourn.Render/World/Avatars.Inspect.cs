// One avatar as the renderer has it, for diagnosis (SojournAvatars inspect): what its shape, bakes, attachments and their
// meshes and materials, the joint overrides they bring and its animations are, and how each is drawn. Enough to tell
// from a single call why someone looks wrong, and to rebuild them on the local grid. Written by Claude Opus 5.5.
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Render.Bodies;

namespace Sojourn.Render.World;

/// <summary>A request for one avatar's report, answered on the render thread (<see cref="Avatars"/> serves it with the
/// status it publishes).</summary>
public sealed class AvatarInspection
{
    sealed class Ask
    {
        public required UUID Id;
        public readonly TaskCompletionSource<JsonObject?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    volatile Ask? _ask;

    /// <summary>The report for the avatar (null if it isn't drawn nearby), from the next frame; null after the timeout too.</summary>
    public async Task<JsonObject?> Get(UUID id, TimeSpan timeout)
    {
        var ask = new Ask { Id = id };
        _ask = ask;
        var done = await Task.WhenAny(ask.Done.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return done == ask.Done.Task ? ask.Done.Task.Result : null;
    }

    internal UUID? Pending => _ask?.Id;

    internal void Answer(JsonObject? report)
    {
        var ask = Interlocked.Exchange(ref _ask, null);
        ask?.Done.TrySetResult(report);
    }
}

public sealed partial class Avatars
{
    /// <summary>Answer a waiting inspection from this frame's entries (render thread).</summary>
    void ServeInspection()
    {
        var inspection = Limits.Inspection;
        if (inspection.Pending is not { } id) return;
        JsonObject? report = null;
        for (int i = 0; i < _entryCount; i++)
            if (!_entries[i].Clone && _entries[i].Av.ID == id) { report = Inspect(_entries[i]); break; }
        inspection.Answer(report);
    }

    JsonObject Inspect(Entry e)
    {
        var av = e.Av;
        var scene = e.Scene;
        var body = _bodies.GetValueOrDefault(av.ID);
        var sk = _cd.Skeleton;
        var r = new JsonObject
        {
            ["id"] = av.ID.ToString(), ["name"] = av.Name ?? "", ["distance"] = Math.Round(e.Dist, 2), ["drawn"] = e.Self ? "full" : e.Drawn,
            ["complexity"] = body?.Cost ?? 0, ["complexity_known"] = body?.CostKnown ?? false,
        };

        // the shape: the visual params as sent (for a rebuild), and what they make of the body
        var vp = av.VisualParameters;
        r["visual_params"] = vp == null ? null : new JsonObject { ["count"] = vp.Length, ["base64"] = Convert.ToBase64String(vp) };
        if (body?.Shape is { } shape)
            r["shape"] = new JsonObject
            {
                ["male"] = shape.Male, ["height"] = Math.Round(shape.BodyHeight, 3), ["hover"] = Math.Round(shape.Hover, 3),
                ["animated_morphs"] = body.ShapeKey != body.LookKey,
            };

        // bakes: which the avatar has and whether they're here; the system meshes its attachments hide
        var bakes = new JsonObject();
        foreach (var (te, label) in new[] { (TeHead, "head"), (TeUpper, "upper"), (TeLower, "lower"), (TeEyes, "eyes"), (TeSkirt, "skirt"), (TeHair, "hair") })
        {
            var bid = BakeId(av, te);
            string state = !Defined(bid) ? "none" : bid == ImgInvisible ? "invisible"
                : _world.Textures.TryGet(bid, out var t) ? $"{t.State.ToString().ToLowerInvariant()} {t.Width}x{t.Height}{(t.HasAlpha ? " alpha" : "")}{(t.Aux != null || t.Mask != null ? " mask" : "")}"
                : "not asked for";
            bakes[label] = $"{bid} {state}";
        }
        r["bakes"] = bakes;
        if (body != null)
        {
            var hidden = new JsonArray();
            for (int mi = 0; mi < MeshOrder.Length; mi++)
                if ((body.Hidden & (1 << mi)) != 0) hidden.Add(MeshOrder[mi].Type);
            r["system_meshes_hidden"] = hidden;
        }

        // attachments: what the region says is worn against what has arrived, and each prim as drawn
        var listed = new JsonArray();
        List<Avatar.Attachment> simList;
        lock (av) simList = av.Attachments?.ToList() ?? new();
        var present = scene.AttachmentsOf(av.LocalID);
        foreach (var a in simList) listed.Add($"{a.AttachmentID} point {a.AttachmentPoint}");
        r["attachments_listed"] = listed;
        r["attachments_here"] = present.Count(o => o.Prim.ParentID == av.LocalID);
        var prims = new JsonArray();
        var overrides = scene.Source.HasSimulator ? scene.Source.Simulator.GLTFMaterialOverrides : null;
        foreach (var obj in present)
        {
            var p = obj.Prim;
            var o = new JsonObject
            {
                ["local_id"] = p.LocalID, ["id"] = p.ID.ToString(), ["name"] = p.Properties?.Name, ["point"] = (int)p.PrimData.AttachmentPoint,
                ["root"] = p.ParentID == av.LocalID, ["kind"] = obj.Kind.ToString(),
            };
            if (obj.Kind == ShapeKind.Mesh)
            {
                o["mesh"] = obj.Asset.ToString();
                o["animesh"] = obj.Animesh != 0;
                o["lods"] = string.Concat(obj.Lods.Select(l => l == null ? "-" : l.Ready ? (l.Skin != null ? "S" : "R") : l.Failed ? "F" : l.Evicted ? "E" : "."));
                if (obj.Faces?.Shape is { } drawn) o["lod_drawn"] = drawn.Key.Lod;
                if (obj.Lods.FirstOrDefault(l => l is { Ready: true, Skin: not null }) is { Skin: { } skin } rigged)
                    o["skin"] = $"{skin.JointNames.Length} joints, {skin.JointNames.Count(n => n.StartsWith("mFace"))} face"
                                + (rigged.AltInverseBind != null ? ", joint positions" : "") + (skin.LockScaleIfJointPosition ? ", scale locked" : "")
                                + (skin.PelvisOffset != 0 ? $", pelvis offset {skin.PelvisOffset:F3}" : "")
                                + (skin.JointNames.Any(n => sk[n] < 0) ? ", unknown joints " + string.Join(" ", skin.JointNames.Where(n => sk[n] < 0)) : "");
            }
            // each face's texture (bakes on mesh marked), and the GLTF material and override's base colour
            var faces = new JsonArray();
            var renderMats = scene.Source.RenderMaterialIds(p.LocalID);
            var ov = overrides != null && overrides.TryGetValue(p.LocalID, out var ovs) ? ovs : null;
            int nFaces = Math.Max(p.Textures?.FaceTextures?.Count(f => f != null) ?? 0, renderMats?.Length ?? 0);
            for (int f = 0; f < Math.Max(nFaces, obj.Faces?.Shape?.Faces.Length ?? 0) && f < 32; f++)
            {
                var te = p.Textures?.GetFace((uint)f);
                if (te == null) continue;
                string s = Tag(te.TextureID);
                if (te.RGBA.A < 1) s += $" alpha {te.RGBA.A:F2}";
                if (renderMats != null && f < renderMats.Length && renderMats[f] != UUID.Zero)
                {
                    s += $" gltf {renderMats[f]}";
                    if (_world.Materials.Gltf(renderMats[f], scene.Source.Assets, out _) is { TextureIds: { Length: > 0 } ids }) s += $" base {Tag(ids[0])}";
                }
                if (ov?.FaceOverrides is { } fo && fo.TryGetValue(f, out var om) && om.TextureIds is { Length: > 0 } oids && oids[0] != UUID.Zero)
                    s += $" override base {Tag(oids[0])}";
                faces.Add($"{f}: {s}");
            }
            o["faces"] = faces;
            prims.Add(o);
        }
        r["attachment_prims"] = prims;

        // joints the worn meshes move away from where the shape puts them
        if (body is { RestPos: { } rest, WornShape: { } ws })
        {
            var moved = new JsonArray();
            for (int j = 0; j < rest.Length; j++)
                if (Vector3.Distance(rest[j], ws.JointPos[j]) > 1e-4f)
                    moved.Add($"{sk.Joints[j].Name} {Fmt(ws.JointPos[j])} -> {Fmt(rest[j])}");
            r["joint_overrides"] = moved;
            r["pelvis_fixup"] = Math.Round(body.PelvisFixup, 3);
        }
        if (body != null) r["animations"] = body.Anim.Describe(_motions, scene.Source.Assets);
        return r;
    }

    static string Tag(UUID id) => RegionScene.BakeOnMeshIds.TryGetValue(id, out int te) ? $"{id} (bake on mesh {te})" : id.ToString();

    static string Fmt(Vector3 v) => $"<{v.X:F3}, {v.Y:F3}, {v.Z:F3}>";
}
