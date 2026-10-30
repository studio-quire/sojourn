using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Core.Leap;
using Sojourn.Core.World;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// TesselBuild against the protocol: the build tools' messages on single objects, no selection needed
/// (tesselobjects.cpp's create/modify/duplicate/rez/link/unlink/take/delete/returnObjects).
/// </summary>
public sealed class BuildOps
{
    readonly AgentSession _s;
    public BuildOps(AgentSession s) => _s = s;

    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");
    Simulator Sim => C.Network.CurrentSim ?? throw new LeapError("no region");
    RegionWorld W => _s.Region ?? throw new LeapError("no region");

    static readonly HashSet<string> Stripped = new() { "op", "reply", "reqid", "id", "ids", "item_id", "offset", "timeout" };
    static readonly HashSet<string> CreateOwn = new() { "shape", "pos", "scale", "rot", "rot_euler" };

    // ---------- shapes ----------

    static readonly Dictionary<string, (byte Profile, PathCurve Path)> Shapes = new()
    {
        ["box"] = (0x01, PathCurve.Line),
        ["cylinder"] = (0x00, PathCurve.Line),
        ["prism"] = (0x03, PathCurve.Line),
        ["sphere"] = (0x05, PathCurve.Circle),
        ["torus"] = (0x00, PathCurve.Circle),
        ["tube"] = (0x01, PathCurve.Circle),
        ["ring"] = (0x03, PathCurve.Circle),
        ["cone"] = (0x00, PathCurve.Line),
        ["pyramid"] = (0x01, PathCurve.Line),
    };

    static Primitive.ConstructionData ResetShape(string shape)
    {
        if (!Shapes.TryGetValue(shape, out var s)) throw new LeapError($"unknown shape {shape}");
        var d = new Primitive.ConstructionData
        {
            PCode = PCode.Prim,
            Material = Material.Wood,
            profileCurve = s.Profile,
            PathCurve = s.Path,
            ProfileBegin = 0, ProfileEnd = 1, ProfileHollow = 0,
            PathBegin = 0, PathEnd = 1,
            PathScaleX = 1, PathScaleY = 1,
            PathShearX = 0, PathShearY = 0,
            PathTwist = 0, PathTwistBegin = 0,
            PathTaperX = 0, PathTaperY = 0,
            PathRadiusOffset = 0, PathRevolutions = 1, PathSkew = 0,
        };
        var circular = s.Path != PathCurve.Line;
        if (circular && shape != "sphere") { d.PathScaleX = 1; d.PathScaleY = 0.25f; }
        if (shape is "cone" or "pyramid") { d.PathScaleX = 0; d.PathScaleY = 0; }
        return d;
    }

    static Primitive.ConstructionData Copy(Primitive.ConstructionData d) => new(d);

    static float Clamp(double v, double lo, double hi) => (float)Math.Clamp(v, lo, hi);

    static double[] Pair(JsonObject a, string key)
    {
        var v = a.Nums(key) ?? throw new LeapError($"{key}: give a number or [x, y]");
        return v.Length == 1 ? new[] { v[0], v[0] } : v;
    }

    /// <summary>Applies shape keys to <paramref name="d"/>; returns true if anything changed.</summary>
    static bool ApplyShape(JsonObject a, ref Primitive.ConstructionData d)
    {
        var changed = false;
        if (a.Str("shape") is { } shape) { d = ResetShape(shape.ToLowerInvariant()); changed = true; }
        var circular = ((byte)d.PathCurve & 0xf0) != 0x10;
        var sphere = (d.profileCurve & 0x0f) == 5 && circular;
        if (a.Has("profile_cut"))
        {
            var p = Pair(a, "profile_cut");
            var (b, e) = Cut(p[0], p[1]);
            d.ProfileBegin = b; d.ProfileEnd = e; changed = true;
        }
        if (a.Has("path_cut"))
        {
            var p = Pair(a, "path_cut");
            var (b, e) = Cut(p[0], p[1]);
            d.PathBegin = b; d.PathEnd = e; changed = true;
        }
        if (a.Num("hollow") is { } hollow)
        {
            var squareHoleInRound = (d.profileCurve & 0xf0) == 0x20 && (d.profileCurve & 0x0f) is 0 or 3 or 5;
            d.ProfileHollow = Clamp(hollow, 0, squareHoleInRound ? 0.7 : 0.95);
            changed = true;
        }
        if (a.Has("twist"))
        {
            var p = Pair(a, "twist");
            d.PathTwistBegin = Clamp(p[0], -1, 1);
            d.PathTwist = Clamp(p[1], -1, 1);
            changed = true;
        }
        if (a.Has("top_shear"))
        {
            var p = Pair(a, "top_shear");
            d.PathShearX = Clamp(p[0], -0.5, 0.5);
            d.PathShearY = Clamp(p[1], -0.5, 0.5);
            changed = true;
        }
        if (a.Has("hole_size"))
        {
            var p = Pair(a, "hole_size");
            if (circular && !sphere) { d.PathScaleX = Clamp(p[0], 0.05, 1); d.PathScaleY = Clamp(p[1], 0.05, 0.5); }
            else { d.PathScaleX = Clamp(p[0], 0, 2); d.PathScaleY = Clamp(p[1], 0, 2); }
            changed = true;
        }
        if (a.Has("taper"))
        {
            var p = Pair(a, "taper");
            if (!circular)
            {
                // the edit panel's taper on a straight path is the top size: ratio = 1 - taper
                d.PathScaleX = Clamp(1 - Math.Clamp(p[0], -1, 1), 0, 2);
                d.PathScaleY = Clamp(1 - Math.Clamp(p[1], -1, 1), 0, 2);
            }
            else
            {
                d.PathTaperX = Clamp(p[0], -1, 1);
                d.PathTaperY = Clamp(p[1], -1, 1);
            }
            changed = true;
        }
        if (a.Num("radius_offset") is { } ro)
        {
            d.PathRadiusOffset = circular && !sphere ? Clamp(ro, -1, 1) : 0;
            changed = true;
        }
        if (a.Num("revolutions") is { } rev) { d.PathRevolutions = Clamp(rev, 1, 4); changed = true; }
        if (a.Num("skew") is { } skew) { d.PathSkew = Clamp(skew, -0.95, 0.95); changed = true; }
        return changed;
    }

    static (float, float) Cut(double b, double e)
    {
        var bb = Math.Clamp(b, 0, 0.98);
        var ee = Math.Clamp(e, 0.02, 1);
        if (ee - bb < 0.02) ee = Math.Min(1, bb + 0.02);
        return ((float)bb, (float)ee);
    }

    static Quaternion? RotFrom(JsonObject a, string quatKey, string eulerKey)
    {
        if (a.Nums(quatKey) is { Length: 4 } q) return Quaternion.Normalize(new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]));
        if (a.Nums(eulerKey) is { Length: 3 } e)
            return Quaternion.CreateFromEulers((float)(e[0] * Math.PI / 180), (float)(e[1] * Math.PI / 180), (float)(e[2] * Math.PI / 180));
        return null;
    }

    // ---------- modify ----------

    /// <summary>Which part of a prim each modify key changes, for undo (BuildUndo puts back only those).</summary>
    static string? PartOf(string key) => key switch
    {
        "pos" or "local_pos" or "rot" or "rot_euler" or "local_rot" or "local_rot_euler" => "place",
        "scale" => "scale",
        "shape" => "shape",
        "phantom" or "physics" or "temp" => "flags",
        "next_perms" or "everyone_perms" or "group_perms" => "perms",
        "reflection_probe" => "probe",
        "normal" or "specular" or "alpha_mode" or "alpha_cutoff" => "faces",
        _ when ShapeKeys.Contains(key) => "shape",
        _ when FaceKeys.Contains(key) || key == "face" => "faces",
        _ when ModifyKeys.Contains(key) => key,
        _ => null,
    };

    static IEnumerable<string> PartsOf(JsonObject a)
    {
        foreach (var (k, _) in a)
        {
            if (PartOf(k) is not { } part) continue;
            yield return part;
            // these change more than their own part: the path (flexible), and a probe makes the prim phantom and clear
            if (k == "flexi") yield return "shape";
            if (k == "reflection_probe") { yield return "flags"; yield return "faces"; }
            if (k == "physics_shape") yield return "flags";
        }
    }

    /// <summary>TesselBuild modify on one prim. Errors stop at the key that failed (earlier keys were sent). The keys
    /// that go through the region's capabilities rather than messages (PBR and legacy materials) come back as a task,
    /// which fails with the region's refusal; it's null when there are none.</summary>
    public Task? Modify(Primitive p, JsonObject a, bool record = true)
    {
        CheckKeys(a);
        RequireRights(p, a);
        ResolveTexture(a);
        // materials are worked out (and refused) before anything is sent, as the faces are
        var materials = _s.Materials.Plan(p, a);
        // the faces' new settings worked out before anything is sent, so a bad value refuses the whole call
        Primitive.TextureEntry? faceTe = null;
        if (FaceKeys.Any(a.Has))
        {
            faceTe = p.Textures is { } cur ? new Primitive.TextureEntry(cur) : new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            var faces = new List<Primitive.TextureEntryFace>();
            if (a.Num("face") is { } fnum)
            {
                var fi = (int)fnum;
                if (fi < 0 || fi >= 9) throw new LeapError("no such face");
                faces.Add(faceTe.CreateFace((uint)fi));
            }
            else
            {
                if (faceTe.DefaultTexture is not null) faces.Add(faceTe.DefaultTexture);
                faces.AddRange(faceTe.FaceTextures.Where(f => f is not null));
            }
            foreach (var f in faces) ApplyFace(a, f);
        }
        var probe = a.Has("reflection_probe") ? ProbeFrom(a["reflection_probe"]) : (ReflectionProbe?)null;
        var li = a.Has("projector") ? ProjectorFrom(a["projector"], p) : null;
        var w = W;
        var sim = Sim;
        var c = C;
        var root = _s.Objects.RootOf(w, p);
        var isRoot = root.LocalID == p.LocalID;
        if (record) _s.Undo.Edit(p, PartsOf(a), $"changed {string.Join(", ", a.Select(kv => kv.Key).Where(k => PartOf(k) is not null).Take(6))} on {p.Properties?.Name ?? "an object"}");

        // placement: one MultipleObjectUpdate with position and/or rotation
        Vector3? pos = null;
        Quaternion? rot = null;
        if (a.Vec3("pos") is { } rp)
            pos = isRoot ? rp : (rp - w.RegionPosition(root)) * Quaternion.Conjugate(w.RegionRotation(root));
        if (a.Vec3("local_pos") is { } lp)
        {
            if (isRoot) throw new LeapError("local_pos is for child prims");
            pos = lp;
        }
        if (RotFrom(a, "rot", "rot_euler") is { } rr)
            rot = isRoot ? rr : rr * Quaternion.Conjugate(w.RegionRotation(root));
        if (RotFrom(a, "local_rot", "local_rot_euler") is { } lr)
        {
            if (isRoot) throw new LeapError("local_rot is for child prims");
            rot = lr;
        }
        if (pos is not null || rot is not null)
        {
            byte type = 0;
            var data = new List<byte>();
            if (pos is { } pv) { type |= (byte)UpdateType.Position; data.AddRange(pv.GetBytes()); }
            if (rot is { } qv) { type |= (byte)UpdateType.Rotation; data.AddRange(qv.GetBytes()); }
            if (isRoot) type |= (byte)UpdateType.Linked;
            SendObjectUpdate(c, sim, p.LocalID, type, data.ToArray());
        }
        if (a.Vec3("scale") is { } scale)
        {
            // a child's size goes with its (local) position, as the viewer sends it: a scale alone on a linked child
            // once threw it ~177 m away on SL (Tessel's notes)
            var sc = Vector3.Clamp(scale, new Vector3(0.001f), new Vector3(256)).GetBytes();
            if (isRoot) SendObjectUpdate(c, sim, p.LocalID, (byte)UpdateType.Scale, sc);
            else SendObjectUpdate(c, sim, p.LocalID, (byte)(UpdateType.Position | UpdateType.Scale), (pos ?? p.Position).GetBytes().Concat(sc).ToArray());
        }

        // shape
        var d = Copy(p.PrimData);
        if (ApplyShape(a, ref d)) c.Objects.SetShape(sim, p.LocalID, d);

        // faces (with the prim's media URL, as the viewer's sendTEUpdate: an empty one could take a face's media off)
        if (faceTe is not null) c.Objects.SetTextures(sim, p.LocalID, faceTe, p.MediaURL ?? "");
        if (probe is { } pr) SetProbe(c, sim, p, pr, a);
        if (a.Bool("animated") is { } animated) SendAnimated(c, sim, p.LocalID, animated);
        if (a.Has("projector"))
        {
            SendProjector(c, sim, p.LocalID, li);
            if (li is not null && !a.Has("light") && p.Light is not { Intensity: > 0 })
                c.Objects.SetLight(sim, p.LocalID, new Primitive.LightData { Color = Color4.White, Intensity = 1, Radius = 10, Falloff = 0.75f, Cutoff = 0 });
        }

        // the edit floater's Features tab and sculpted prim type: each takes its settings, or false to turn it off
        if (a.Has("sculpt"))
        {
            if (Off(a, "sculpt")) c.Objects.SetExtraParamOff(sim, p.LocalID, ExtraParamType.Sculpt);
            else if (a["sculpt"] is JsonObject so)
            {
                var kind = (so.Str("type") ?? "sphere").ToLowerInvariant() switch
                {
                    "sphere" => SculptType.Sphere, "torus" => SculptType.Torus, "plane" => SculptType.Plane,
                    "cylinder" => SculptType.Cylinder,
                    var k => throw new LeapError($"sculpt type {k}: sphere, torus, plane or cylinder"),
                };
                var bits = (byte)kind | (so.Bool("mirror", false) ? (byte)SculptType.Mirror : 0) | (so.Bool("invert", false) ? (byte)SculptType.Invert : 0);
                var map = TextureAsset(so.Str("texture") ?? throw new LeapError("sculpt: give {texture, type?, mirror?, invert?}"), "sculpt");
                c.Objects.SetSculpt(sim, p.LocalID, new Primitive.SculptData { SculptTexture = map, Type = (SculptType)bits });
            }
            else throw new LeapError("sculpt: give {texture, type?, mirror?, invert?} or false");
        }
        if (a.Has("light"))
        {
            if (Off(a, "light")) c.Objects.SetExtraParamOff(sim, p.LocalID, ExtraParamType.Light);
            else if (a["light"] is JsonObject lo)
            {
                var col = lo.Nums("color") is { Length: >= 3 } lc ? new Color4((float)lc[0], (float)lc[1], (float)lc[2], 1) : Color4.White;
                c.Objects.SetLight(sim, p.LocalID, new Primitive.LightData
                {
                    Color = col, Intensity = Clamp(lo.Num("intensity", 1), 0, 1), Radius = Clamp(lo.Num("radius", 10), 0, 20),
                    Falloff = Clamp(lo.Num("falloff", 0.75), 0, 2), Cutoff = 0,
                });
            }
            else throw new LeapError("light: give {color?, intensity?, radius?, falloff?} or false");
        }
        if (a.Has("flexi"))
        {
            // the path made flexible with it, as the viewer's Flexible Path checkbox does (setIsFlexible): viewers draw a
            // prim as flexible by its path, and the parameters alone leave it rigid; only lines can be (canBeFlexible)
            bool flexOn = !Off(a, "flexi");
            if (flexOn && a["flexi"] is not JsonObject) throw new LeapError("flexi: give {softness?, gravity?, drag?, wind?, tension?, force?} or false");
            byte path = (byte)d.PathCurve;
            if (flexOn ? path == (byte)PathCurve.Line : path == (byte)PathCurve.Flexible)
            {
                d.PathCurve = flexOn ? PathCurve.Flexible : PathCurve.Line;
                c.Objects.SetShape(sim, p.LocalID, d);
            }
            if (!flexOn) c.Objects.SetExtraParamOff(sim, p.LocalID, ExtraParamType.Flexible);
            else if (a["flexi"] is JsonObject fo)
                c.Objects.SetFlexible(sim, p.LocalID, new Primitive.FlexibleData
                {
                    Softness = (int)Math.Clamp(fo.Num("softness", 2), 0, 3), Gravity = Clamp(fo.Num("gravity", 0.3), -10, 10),
                    Drag = Clamp(fo.Num("drag", 2), 0, 10), Wind = Clamp(fo.Num("wind", 0), 0, 10), Tension = Clamp(fo.Num("tension", 1), 0, 10),
                    Force = fo.Vec3("force") ?? Vector3.Zero,
                });
            else throw new LeapError("flexi: give {softness?, gravity?, drag?, wind?, tension?, force?} or false");
        }
        if (a.Str("name") is { } name) c.Objects.SetName(sim, p.LocalID, name);
        if (a.Str("desc") is { } desc) c.Objects.SetDescription(sim, p.LocalID, desc);
        if (a.Str("material") is { } mat)
        {
            // SL's prim materials (Edit > Features); on Lowmere stone, metal and glass are fireproof and cost stone/salvage
            if (!Enum.TryParse<Material>(mat.Trim(), true, out var m) || !Enum.IsDefined(m))
                throw new LeapError("material: stone, metal, glass, wood, flesh, plastic or rubber");
            c.Objects.SetMaterial(sim, p.LocalID, m);
        }
        if (a.Has("phantom") || a.Has("physics") || a.Has("temp"))
        {
            var fl = _s.State.PrimFlagsOf(p);
            SendFlags(c, sim, p.LocalID,
                a.Bool("physics") ?? (fl & PrimFlags.Physics) != 0,
                a.Bool("temp") ?? (fl & PrimFlags.TemporaryOnRez) != 0,
                a.Bool("phantom") ?? (fl & PrimFlags.Phantom) != 0,
                (fl & PrimFlags.CastShadows) != 0);
        }
        // the edit floater's Features tab: physics shape (none for most children of a build halves its land impact)
        if (a.Str("physics_shape") is { } ps)
        {
            var shape = ps.Trim().ToLowerInvariant() switch
            {
                "prim" => PhysicsShapeType.Prim, "none" => PhysicsShapeType.None, "convex" or "convex_hull" => PhysicsShapeType.ConvexHull,
                _ => throw new LeapError("physics_shape: prim, none or convex"),
            };
            var fl = _s.State.PrimFlagsOf(p);
            c.Objects.SetFlags(sim, p.LocalID, (fl & PrimFlags.Physics) != 0, (fl & PrimFlags.TemporaryOnRez) != 0,
                (fl & PrimFlags.Phantom) != 0, (fl & PrimFlags.CastShadows) != 0, shape, 1000f, 0.6f, 0.5f, 1f);
        }
        if (a.Has("sale"))
        {
            if (a["sale"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && !b)
                c.Objects.SetSaleInfo(sim, p.LocalID, SaleType.Not, 0);
            else if (a["sale"] is JsonObject so)
            {
                var tname = so.Str("type") ?? "original";
                var st = ObjectViews.SaleTypeFrom(tname) ?? throw new LeapError($"unknown sale type {tname}");
                c.Objects.SetSaleInfo(sim, p.LocalID, st, so.Int("price", 0));
            }
            else throw new LeapError("sale: give {type, price} or false");
        }
        if (a.Str("next_perms") is { } np)
        {
            var mask = ObjectViews.PermMask(np);
            var ids = new List<uint> { p.LocalID };
            c.Objects.SetPermissions(sim, ids, PermissionWho.NextOwner, mask, true);
            var clear = (PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Transfer) & ~mask;
            if (clear != 0) c.Objects.SetPermissions(sim, ids, PermissionWho.NextOwner, clear, false);
        }
        // the edit floater's General tab: "Anyone can: Move / Copy", "Share with group" (group: modify, copy, move)
        if (a.Str("everyone_perms") is { } ep) SetMask(c, sim, p, PermissionWho.Everyone, ObjectViews.PermMask(ep), PermissionMask.Copy | PermissionMask.Move);
        if (a.Str("group_perms") is { } gp) SetMask(c, sim, p, PermissionWho.Group, ObjectViews.PermMask(gp), PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Move);
        if (a.Str("click_action") is { } ca)
        {
            var action = ObjectViews.ClickActionFrom(ca) ?? throw new LeapError("unknown click_action");
            var pkt = new ObjectClickActionPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                ObjectData = new[] { new ObjectClickActionPacket.ObjectDataBlock { ObjectLocalID = p.LocalID, ClickAction = (byte)action } },
            };
            c.Network.SendPacket(pkt, sim);
        }
        return materials is null ? null : _s.Materials.SendAsync(p, materials, CancellationToken.None);
    }

    // ---------- projector (the Features tab's light texture: LLLightImageParams, extra param 0x40) ----------

    /// <summary>{texture, fov?, focus?, ambiance?} or false. fov in degrees here (the viewer's panel shows radians,
    /// 0-3); focus -20-20, ambiance 0-1, as the panel clamps them. A projector lights through a texture only while the
    /// prim is a light, so turning one on also turns the light on (at its defaults) unless it already is.</summary>
    Primitive.LightImage? ProjectorFrom(JsonNode? v, Primitive p)
    {
        if (v is JsonValue bv && bv.TryGetValue<bool>(out var on) && !on) return null;
        if (v is not JsonObject o) throw new LeapError("projector: give {texture, fov?, focus?, ambiance?} or false");
        foreach (var (k, _) in o)
            if (k is not ("texture" or "fov" or "focus" or "ambiance"))
                throw new LeapError($"projector: no key {k} (texture, fov, focus, ambiance)");
        var tex = TextureAsset(o.Str("texture") ?? throw new LeapError("projector: give the texture it casts"), "projector texture");
        // a new projector starts at LLLightImageParams' defaults: 90 degrees, focus 0, ambiance 0
        var cur = p.LightMap is { LightTexture: var t } lm && t != UUID.Zero ? lm.Params : new Vector3((float)(Math.PI / 2), 0, 0);
        double fov = o.Num("fov") is { } deg ? deg * Math.PI / 180 : cur.X;
        return new Primitive.LightImage
        {
            LightTexture = tex,
            Params = new Vector3(Clamp(fov, 0, 3), Clamp(o.Num("focus", cur.Y), -20, 20), Clamp(o.Num("ambiance", cur.Z), 0, 1)),
        };
    }

    internal static void SendProjector(GridClient c, Simulator sim, uint localId, Primitive.LightImage? li) =>
        c.Network.SendPacket(new ObjectExtraParamsPacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            ObjectData = new[] { new ObjectExtraParamsPacket.ObjectDataBlock
            {
                ObjectLocalID = localId, ParamType = (ushort)ExtraParamType.LightImage, ParamInUse = li is not null,
                ParamData = li?.GetBytes() ?? Array.Empty<byte>(), ParamSize = li is null ? 0u : 28u,
            } },
        }, sim);

    /// <summary>`texture` takes an asset id or a texture's inventory item id (what inventory listings show); an item
    /// id becomes its asset, as the viewer's texture picker does. Anything else is refused rather than ignored.</summary>
    void ResolveTexture(JsonObject a)
    {
        if (!a.Has("texture")) return;
        a["texture"] = TextureAsset(a.Str("texture"), "texture").ToString();
    }

    /// <summary>The viewer's own textures by name, as the texture picker's Blank / Default / Transparent buttons.</summary>
    static readonly Dictionary<string, UUID> NamedTextures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blank"] = new UUID("5748decc-f629-461c-9a36-a35a221fe21f"),
        ["plywood"] = new UUID("89556747-24cb-43ed-920b-47caed15465f"), ["default"] = new UUID("89556747-24cb-43ed-920b-47caed15465f"),
        ["transparent"] = new UUID("8dcd4a48-2d37-4909-9f78-f7a9eb4ef903"),
    };

    internal UUID TextureAsset(string? raw, string key)
    {
        if (raw is not null && NamedTextures.TryGetValue(raw.Trim(), out var named)) return named;
        if (!UUID.TryParse(raw, out var id)) throw new LeapError($"{key}: {raw} isn't an id (give a texture's asset id or inventory item id, or blank, plywood or transparent)");
        if (_s.Inventory.Node(id) is InventoryItem item)
        {
            if (item.AssetType == AssetType.Link && _s.Inventory.Node(item.AssetUUID) is InventoryItem t) item = t;
            if (item.AssetType != AssetType.Texture) throw new LeapError($"{key}: {item.Name} is a {AgentInventory.AssetTypeLabel(item.AssetType)}, not a texture");
            if (item.AssetUUID == UUID.Zero) throw new LeapError($"{key}: {item.Name} hides its asset (it needs full permissions to apply)");
            return item.AssetUUID;
        }
        return id;
    }

    static bool Off(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<bool>(out var on) && !on;

    static void SetMask(GridClient c, Simulator sim, Primitive p, PermissionWho who, PermissionMask want, PermissionMask settable)
    {
        var ids = new List<uint> { p.LocalID };
        if ((want & settable) != 0) c.Objects.SetPermissions(sim, ids, who, want & settable, true);
        if ((settable & ~want) != 0) c.Objects.SetPermissions(sim, ids, who, settable & ~want, false);
    }

    static void ApplyFace(JsonObject a, Primitive.TextureEntryFace f)
    {
        if (a.Nums("color") is { Length: >= 3 } col)
            f.RGBA = new Color4((float)col[0], (float)col[1], (float)col[2], col.Length > 3 ? (float)col[3] : f.RGBA.A);
        if (a.Num("alpha") is { } alpha) { var rgba = f.RGBA; f.RGBA = new Color4(rgba.R, rgba.G, rgba.B, (float)Math.Clamp(alpha, 0, 1)); }
        if (a.Uuid("texture") is { } tex) f.TextureID = tex;
        if (a.Nums("repeats") is { Length: >= 1 } rep) { f.RepeatU = (float)rep[0]; f.RepeatV = (float)(rep.Length > 1 ? rep[1] : rep[0]); }
        if (a.Num("glow") is { } glow) f.Glow = (float)Math.Clamp(glow, 0, 1);
        if (a.Bool("fullbright") is { } fb) f.Fullbright = fb;
        if (a.Has("shiny")) f.Shiny = ShinyFrom(a["shiny"]);
        if (a.Nums("tex_offset") is { Length: >= 1 } off) { f.OffsetU = (float)Math.Clamp(off[0], -1, 1); f.OffsetV = (float)Math.Clamp(off.Length > 1 ? off[1] : off[0], -1, 1); }
        if (a.Num("tex_rotation") is { } tr) f.Rotation = (float)(tr * Math.PI / 180);
        if (a.Str("bump") is { } bump)
        {
            // the viewer's bumpiness list: none, brightness, darkness, woodgrain, bark, bricks, checker, concrete, ...
            if (!Enum.TryParse<Bumpiness>(bump.Replace(" ", ""), true, out var b) || !Enum.IsDefined(b))
                throw new LeapError("bump: none, brightness, darkness, woodgrain, bark, bricks, checker, concrete, crustytile, cutstone, discs, gravel, petridish, siding, stonetile, stucco, suction, weave");
            f.Bump = b;
        }
        if (a.Str("texgen") is { } tg)
            f.TexMapType = tg.Trim().ToLowerInvariant() switch
            {
                "default" => MappingType.Default, "planar" => MappingType.Planar,
                _ => throw new LeapError("texgen: default or planar"),
            };
    }

    /// <summary>The edit floater's shininess: none, low, medium, high (or 0-3).</summary>
    static Shininess ShinyFrom(JsonNode? v)
    {
        if (Json.AsDouble(v) is { } n && n == Math.Floor(n) && n is >= 0 and <= 3) return (Shininess)((int)n << 6);
        return (v is JsonValue sv && sv.TryGetValue<string>(out var s) ? s.Trim().ToLowerInvariant() : "") switch
        {
            "none" or "off" => Shininess.None, "low" => Shininess.Low, "medium" => Shininess.Medium, "high" => Shininess.High,
            _ => throw new LeapError("shiny: none, low, medium or high (or 0-3)"),
        };
    }

    // ---------- reflection probes and mirrors (the edit floater's Features tab: Reflection Probe) ----------

    /// <summary>LLReflectionProbeParams: ambiance 0-100, clip distance 0-1024 m, flags (box volume, dynamic, mirror).</summary>
    readonly record struct ReflectionProbe(bool On, float Ambiance, float ClipDistance, byte Flags);

    static ReflectionProbe ProbeFrom(JsonNode? v)
    {
        if (v is JsonValue bv && bv.TryGetValue<bool>(out var on))
            return new ReflectionProbe(on, 0, 0, 0);
        if (v is not JsonObject o) throw new LeapError("reflection_probe: give {type?: sphere|box, ambiance?, clip_distance?, dynamic?, mirror?} or false");
        foreach (var (k, _) in o)
            if (k is not ("type" or "ambiance" or "clip_distance" or "dynamic" or "mirror"))
                throw new LeapError($"reflection_probe: no key {k} (type, ambiance, clip_distance, dynamic, mirror)");
        byte flags = 0;
        var type = (o.Str("type") ?? (o.Bool("mirror") == true ? "box" : "sphere")).Trim().ToLowerInvariant();
        if (type == "box") flags |= 0x01;
        else if (type != "sphere") throw new LeapError("reflection_probe type: sphere or box");
        if (o.Bool("dynamic") == true) flags |= 0x02;
        if (o.Bool("mirror") == true) flags |= 0x04;
        return new ReflectionProbe(true, Clamp(o.Num("ambiance", 0), 0, 100), Clamp(o.Num("clip_distance", 0), 0, 1024), flags);
    }

    /// <summary>Sends the probe's extra params (LibreMetaverse has no setter for type 0x90). Turning one on does what the
    /// viewer's Features tab does around it: the prim goes phantom and fully transparent (a probe is a volume that
    /// lights and reflects what's near it, not a surface), unless this call sets phantom or alpha itself.</summary>
    void SetProbe(GridClient c, Simulator sim, Primitive p, ReflectionProbe pr, JsonObject a)
    {
        SendProbeParams(c, sim, p.LocalID, pr);
        if (!pr.On) return;
        if (!a.Has("phantom") && !a.Has("physics") && !a.Has("temp"))
        {
            var fl = _s.State.PrimFlagsOf(p);
            SendFlags(c, sim, p.LocalID, (fl & PrimFlags.Physics) != 0, (fl & PrimFlags.TemporaryOnRez) != 0, true, (fl & PrimFlags.CastShadows) != 0);
        }
        if (!a.Has("alpha") && !a.Has("color"))
        {
            var te = p.Textures is { } cur ? new Primitive.TextureEntry(cur) : new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            foreach (var f in new[] { te.DefaultTexture }.Concat(te.FaceTextures).Where(f => f is not null))
                f!.RGBA = new Color4(f.RGBA.R, f.RGBA.G, f.RGBA.B, 0);
            c.Objects.SetTextures(sim, p.LocalID, te, p.MediaURL ?? "");
        }
    }

    static readonly string[] FaceKeys = { "color", "alpha", "texture", "repeats", "glow", "fullbright", "shiny", "tex_offset", "tex_rotation", "bump", "texgen" };

    /// <summary>The edit floater's physical, temporary and phantom boxes, sent as the viewer sends them
    /// (LLViewerObject::updateFlags): without the ExtraPhysics block, which it adds only when the physics shape or its
    /// values change. LibreMetaverse's SetFlags always adds one with default values; OpenSim then applies only that
    /// block (phantom never took on the local grid), and SL would take its default shape and density.</summary>
    internal static void SendFlags(GridClient c, Simulator sim, uint localId, bool physics, bool temp, bool phantom, bool shadows) =>
        c.Network.SendPacket(new ObjectFlagUpdatePacket
        {
            AgentData =
            {
                AgentID = c.Self.AgentID, SessionID = c.Self.SessionID, ObjectLocalID = localId,
                UsePhysics = physics, IsTemporary = temp, IsPhantom = phantom, CastsShadows = shadows,
            },
            ExtraPhysics = Array.Empty<ObjectFlagUpdatePacket.ExtraPhysicsBlock>(),
        }, sim);

    /// <summary>The Features tab's Animated Mesh box (extra param 0x70, LLExtendedMeshParams: its flags as a U32, bit 0
    /// animated): the object becomes animesh, its rigged meshes moving with its own skeleton and the animations its
    /// scripts start (llStartObjectAnimation). Set on the linkset's root.</summary>
    internal static void SendAnimated(GridClient c, Simulator sim, uint localId, bool on) =>
        c.Network.SendPacket(new ObjectExtraParamsPacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            ObjectData = new[] { new ObjectExtraParamsPacket.ObjectDataBlock
            {
                ObjectLocalID = localId, ParamType = 0x70, ParamInUse = true, ParamData = BitConverter.GetBytes(on ? 1u : 0u), ParamSize = 4,
            } },
        }, sim);

    /// <summary>A probe put back as TesselObjects get full reads it (undo), or taken off.</summary>
    internal static void SendProbe(GridClient c, Simulator sim, uint localId, JsonObject? probe)
    {
        var pr = probe is null ? new ReflectionProbe(false, 0, 0, 0) : ProbeFrom(probe);
        SendProbeParams(c, sim, localId, pr);
    }

    static void SendProbeParams(GridClient c, Simulator sim, uint localId, ReflectionProbe pr)
    {
        var data = new byte[9];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), pr.Ambiance);
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), pr.ClipDistance);
        data[8] = pr.Flags;
        c.Network.SendPacket(new ObjectExtraParamsPacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            ObjectData = new[] { new ObjectExtraParamsPacket.ObjectDataBlock
            {
                ObjectLocalID = localId, ParamType = 0x90, ParamInUse = pr.On, ParamData = pr.On ? data : Array.Empty<byte>(),
                ParamSize = pr.On ? 9u : 0u,
            } },
        }, sim);
    }

    static void SendObjectUpdate(GridClient c, Simulator sim, uint localId, byte type, byte[] data)
    {
        var pkt = new MultipleObjectUpdatePacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            ObjectData = new[] { new MultipleObjectUpdatePacket.ObjectDataBlock { ObjectLocalID = localId, Type = type, Data = data } },
        };
        c.Network.SendPacket(pkt, sim);
    }

    // ---------- create / duplicate / rez and the matcher ----------

    Vector3 Ahead(float metres = 2)
    {
        var c = C;
        var r = Vector3.UnitX * c.Self.SimRotation;
        var fwd = new Vector3(r.X, r.Y, 0);
        if (fwd.LengthSquared() < 1e-6) fwd = Vector3.UnitX;
        return c.Self.SimPosition + Vector3.Normalize(fwd) * metres;
    }

    bool Mine(Primitive p) => (_s.State.PrimFlagsOf(p) & PrimFlags.ObjectYouOwner) != 0;

    PendingCreate Expect(Vector3 want, bool moveToPos, JsonObject? modify)
    {
        var w = W;
        var before = w.Prims.Values
            .Where(p => p.ParentID == 0 && Mine(p) && Vector3.Distance(p.Position, want) < 1.5f)
            .Select(p => p.LocalID).ToHashSet();
        var pc = new PendingCreate { WantPos = want, Before = before, Asked = DateTime.UtcNow, MoveToPos = moveToPos, Modify = modify };
        lock (_s.State.PendingCreates) _s.State.PendingCreates.Add(pc);
        return pc;
    }

    static JsonObject? Rest(JsonObject a, IEnumerable<string> alsoStrip)
    {
        var strip = Stripped.Concat(alsoStrip).ToHashSet();
        var rest = new JsonObject();
        foreach (var (k, v) in a) if (!strip.Contains(k)) rest[k] = v?.DeepClone();
        return rest.Count == 0 ? null : rest;
    }

    static readonly string[] ShapeKeys = { "profile_cut", "path_cut", "hollow", "twist", "top_shear", "hole_size", "taper", "radius_offset", "revolutions", "skew" };

    /// <summary>What Modify reads (with the shape keys); anything else would be dropped without a word, so it's refused.</summary>
    static readonly string[] ModifyKeys =
    {
        "pos", "local_pos", "rot", "rot_euler", "local_rot", "local_rot_euler", "scale", "shape",
        "color", "alpha", "texture", "repeats", "glow", "fullbright", "shiny", "face", "tex_offset", "tex_rotation", "bump", "texgen",
        "sculpt", "light", "flexi", "name", "desc", "material", "phantom", "physics", "physics_shape", "temp", "sale",
        "next_perms", "everyone_perms", "group_perms", "click_action", "reflection_probe", "animated", "projector",
        // MaterialOps: PBR (glTF) materials and their overrides, the legacy normal and specular maps, the diffuse alpha mode
        "pbr", "normal", "specular", "alpha_mode", "alpha_cutoff",
    };

    /// <summary>Keys that take a number, or numbers: a value that isn't is refused rather than dropped without a word.</summary>
    static readonly string[] NumberKeys = { "alpha", "glow", "tex_rotation", "face", "hollow", "radius_offset", "revolutions", "skew" };
    static readonly string[] NumbersKeys = { "color", "repeats", "tex_offset", "scale", "pos", "local_pos", "rot", "rot_euler", "local_rot", "local_rot_euler" };

    static void CheckKeys(JsonObject a)
    {
        var unknown = a.Select(kv => kv.Key).Where(k => !Stripped.Contains(k) && !ModifyKeys.Contains(k) && !ShapeKeys.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new LeapError($"no such key {string.Join(", ", unknown)}; the keys are {string.Join(", ", ModifyKeys.Concat(ShapeKeys))}");
        foreach (var k in NumberKeys) if (a.Has(k) && a.Num(k) is null) throw new LeapError($"{k} takes a number");
        foreach (var k in NumbersKeys) if (a.Has(k) && a.Nums(k) is null) throw new LeapError($"{k} takes numbers, like [x, y, z]");
        if (a.Has("shiny")) ShinyFrom(a["shiny"]);
        if (a.Has("reflection_probe")) ProbeFrom(a["reflection_probe"]);
        if (a.Has("animated") && a.Bool("animated") is null) throw new LeapError("animated takes true or false");
    }

    /// <summary>The region ignores edits by someone without modify rights, without a word.</summary>
    void RequireModify(Primitive p, string what)
    {
        if ((_s.State.PrimFlagsOf(p) & PrimFlags.ObjectModify) == 0)
            throw new LeapError($"I can't {what} {p.Properties?.Name ?? "that object"}: I don't have modify rights on it");
    }

    static readonly string[] PlacementKeys = { "pos", "rot", "rot_euler", "local_pos", "local_rot", "local_rot_euler" };

    /// <summary>Moving needs move rights, a sale ownership, the rest modify rights: the edit floater's rules.</summary>
    void RequireRights(Primitive p, JsonObject a)
    {
        var f = _s.State.PrimFlagsOf(p);
        var keys = a.Select(kv => kv.Key).Where(k => !Stripped.Contains(k)).ToList();
        if (keys.Any(k => PlacementKeys.Contains(k)) && (f & (PrimFlags.ObjectMove | PrimFlags.ObjectModify)) == 0)
            throw new LeapError($"I can't move {p.Properties?.Name ?? "that object"}: I don't have move rights on it");
        if (keys.Contains("sale") && (f & PrimFlags.ObjectYouOwner) == 0)
            throw new LeapError($"only its owner can put {p.Properties?.Name ?? "that object"} up for sale");
        if (keys.Any(k => !PlacementKeys.Contains(k) && k != "sale")) RequireModify(p, "edit");
    }

    /// <summary>Waits (up to 4 s) for the region's updates to show a change.</summary>
    static async Task<bool> UntilAsync(Func<bool> done, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            if (done()) return true;
            await Task.Delay(200, ct);
        }
        return done();
    }

    public async Task<JsonObject> CreateAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var sim = Sim;
        CheckKeys(a);
        var shape = (a.Str("shape") ?? "box").ToLowerInvariant();
        var d = ResetShape(shape);
        var shapeArgs = new JsonObject();
        foreach (var k in ShapeKeys) if (a.Has(k)) shapeArgs[k] = a[k]!.DeepClone();
        ApplyShape(shapeArgs, ref d);
        var pos = a.Vec3("pos") ?? Ahead();
        var scale = a.Vec3("scale") ?? new Vector3(0.5f);
        var rot = RotFrom(a, "rot", "rot_euler")
                  ?? (d.PathCurve != PathCurve.Line ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(Math.PI / 2)) : Quaternion.Identity);
        var pending = Expect(pos, moveToPos: true, Rest(a, CreateOwn.Concat(ShapeKeys)));
        var group = c.Self.ActiveGroup;
        var pkt = new ObjectAddPacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID, GroupID = group },
            ObjectData =
            {
                PCode = (byte)PCode.Prim, Material = (byte)Material.Wood, AddFlags = (uint)PrimFlags.CreateSelected,
                PathCurve = (byte)d.PathCurve, ProfileCurve = d.profileCurve,
                PathBegin = Primitive.PackBeginCut(d.PathBegin), PathEnd = Primitive.PackEndCut(d.PathEnd),
                PathScaleX = Primitive.PackPathScale(d.PathScaleX), PathScaleY = Primitive.PackPathScale(d.PathScaleY),
                PathShearX = (byte)Primitive.PackPathShear(d.PathShearX), PathShearY = (byte)Primitive.PackPathShear(d.PathShearY),
                PathTwist = Primitive.PackPathTwist(d.PathTwist), PathTwistBegin = Primitive.PackPathTwist(d.PathTwistBegin),
                PathRadiusOffset = Primitive.PackPathTwist(d.PathRadiusOffset), PathTaperX = Primitive.PackPathTaper(d.PathTaperX),
                PathTaperY = Primitive.PackPathTaper(d.PathTaperY), PathRevolutions = Primitive.PackPathRevolutions(d.PathRevolutions),
                PathSkew = Primitive.PackPathTwist(d.PathSkew), ProfileBegin = Primitive.PackBeginCut(d.ProfileBegin),
                ProfileEnd = Primitive.PackEndCut(d.ProfileEnd), ProfileHollow = Primitive.PackProfileHollow(d.ProfileHollow),
                BypassRaycast = 1, RayStart = pos, RayEnd = pos, RayTargetID = UUID.Zero, RayEndIsIntersection = 1,
                Scale = scale, Rotation = rot, State = 0,
            },
        };
        c.Network.SendPacket(pkt, sim);
        return await Await(pending, ct, "timed out waiting for the new object");
    }

    public async Task<JsonObject> DuplicateAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var sim = Sim;
        var w = W;
        CheckKeys(a);
        var id = a.Uuid("id") ?? throw new LeapError("need id");
        var p = w.PrimById(id) ?? throw new LeapError("object not in view");
        var root = _s.Objects.RootOf(w, p);
        var offset = a.Vec3("offset") ?? new Vector3(0, 0, 0.5f);
        var pending = Expect(root.Position + offset, moveToPos: false, Rest(a, Array.Empty<string>()));
        var pkt = new ObjectDuplicatePacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID, GroupID = c.Self.ActiveGroup },
            SharedData = { Offset = offset, DuplicateFlags = (uint)PrimFlags.CreateSelected },
            ObjectData = new[] { new ObjectDuplicatePacket.ObjectDataBlock { ObjectLocalID = root.LocalID } },
        };
        c.Network.SendPacket(pkt, sim);
        return await Await(pending, ct, "timed out waiting for the copy");
    }

    public async Task<JsonObject> RezAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var sim = Sim;
        CheckKeys(a);
        var itemId = a.Uuid("item_id") ?? throw new LeapError("need item_id");
        var item = await _s.Inventory.ItemAsync(itemId, ct) ?? throw new LeapError("item not in inventory (or inventory not loaded)");
        if (item.AssetType != AssetType.Object) throw new LeapError("not an object item");
        if (_s.Inventory.IsWorn(item)) throw new LeapError("item is worn; detach it first");
        var pos = a.Vec3("pos") ?? Ahead();
        var pending = Expect(pos, moveToPos: true, Rest(a, new[] { "pos" }));
        var copyable = (item.Permissions.OwnerMask & PermissionMask.Copy) != 0;
        var pkt = new RezObjectPacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID, GroupID = c.Self.ActiveGroup },
            RezData =
            {
                FromTaskID = UUID.Zero, BypassRaycast = 1, RayStart = pos, RayEnd = pos, RayTargetID = UUID.Zero,
                RayEndIsIntersection = true, RezSelected = true, RemoveItem = !copyable, ItemFlags = (uint)item.Flags,
                GroupMask = (uint)item.Permissions.GroupMask, EveryoneMask = (uint)item.Permissions.EveryoneMask,
                NextOwnerMask = (uint)item.Permissions.NextOwnerMask,
            },
            InventoryData =
            {
                ItemID = item.UUID, FolderID = item.ParentUUID, CreatorID = item.CreatorID, OwnerID = item.OwnerID,
                GroupID = item.GroupID, BaseMask = (uint)item.Permissions.BaseMask, OwnerMask = (uint)item.Permissions.OwnerMask,
                GroupMask = (uint)item.Permissions.GroupMask, EveryoneMask = (uint)item.Permissions.EveryoneMask,
                NextOwnerMask = (uint)item.Permissions.NextOwnerMask, GroupOwned = item.GroupOwned, TransactionID = UUID.Zero,
                Type = (sbyte)item.AssetType, InvType = (sbyte)item.InventoryType, Flags = (uint)item.Flags,
                SaleType = (byte)item.SaleType, SalePrice = item.SalePrice, Name = Utils.StringToBytes(item.Name),
                Description = Utils.StringToBytes(item.Description), CreationDate = (int)Utils.DateTimeToUnixTime(item.CreationDate),
                CRC = InventoryManager.ItemCRC(item),
            },
        };
        c.Network.SendPacket(pkt, sim);
        if (copyable) return await Await(pending, ct, "timed out waiting for the rezzed object");
        // a no-copy item leaves my inventory when rezzed; the region doesn't say so, so the local view must forget it
        // (else it's listed twice and a second rez of it waits for nothing)
        try
        {
            var r = await Await(pending, ct, "timed out waiting for the rezzed object");
            c.Inventory.Store?.RemoveNodeFor(item);
            return r;
        }
        catch (LeapError)
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(TimeSpan.FromSeconds(4));
            if (await c.Inventory.FetchItemAsync(item.UUID, c.Self.AgentID, quick.Token) is null && !ct.IsCancellationRequested)
            {
                c.Inventory.Store?.RemoveNodeFor(item);
                throw new LeapError($"{item.Name} isn't in my inventory any more (rezzed or given away already)");
            }
            throw;
        }
    }

    async Task<JsonObject> Await(PendingCreate pending, CancellationToken ct, string timeoutMsg)
    {
        var since = DateTime.UtcNow.AddSeconds(-1);
        try
        {
            var prim = await pending.Done.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            var r = new JsonObject { ["id"] = prim.ID.ToString(), ["local_id"] = prim.LocalID };
            if (pending.ModifyError is { } err) r["modify_error"] = $"made, but its settings weren't all applied: {err}";
            return r;
        }
        catch (TimeoutException)
        {
            lock (_s.State.PendingCreates) _s.State.PendingCreates.Remove(pending);
            // a refusal arrives as an alert, not as an answer to the request
            throw new LeapError(_s.State.LastAlert is { } al && al.T >= since ? $"{timeoutMsg}; the region said: {al.Message}" : timeoutMsg);
        }
    }

    /// <summary>A full update with create-selected for one of my roots: give it to the oldest waiting request.</summary>
    internal void OnCreateSelected(GridClient c, Primitive p)
    {
        PendingCreate? claim = null;
        lock (_s.State.PendingCreates)
        {
            if (_s.State.Claimed.Contains(p.LocalID)) return;
            claim = _s.State.PendingCreates.FirstOrDefault();
            if (claim is not null) _s.State.PendingCreates.Remove(claim);
            _s.State.Claimed.Add(p.LocalID);
        }
        Claimed(c, p, claim);
    }

    /// <summary>The fallback (every 0.25 s): a new object of mine near where a request asked, older than 0.5 s.
    /// OpenSim duplicates don't carry create-selected.</summary>
    internal void Sweep(GridClient c)
    {
        var w = _s.Region;
        if (w is null) return;
        List<(PendingCreate, Primitive)> found = new();
        lock (_s.State.PendingCreates)
        {
            if (_s.State.PendingCreates.Count == 0) return;
            var now = DateTime.UtcNow;
            foreach (var pc in _s.State.PendingCreates.ToArray())
            {
                if (now - pc.Asked > TimeSpan.FromSeconds(15)) { _s.State.PendingCreates.Remove(pc); continue; }
                if (now - pc.Asked < TimeSpan.FromSeconds(0.5)) continue;
                var p = w.Prims.Values.FirstOrDefault(x => x.ParentID == 0 && Mine(x) && !x.IsAttachment
                    && !pc.Before.Contains(x.LocalID) && !_s.State.Claimed.Contains(x.LocalID)
                    && Vector3.Distance(x.Position, pc.WantPos) < 1.5f);
                if (p is null) continue;
                _s.State.PendingCreates.Remove(pc);
                _s.State.Claimed.Add(p.LocalID);
                found.Add((pc, p));
            }
        }
        foreach (var (pc, p) in found) Claimed(c, p, pc);
    }

    void Claimed(GridClient c, Primitive p, PendingCreate? pc)
    {
        // LibreMetaverse selects create-selected objects for their properties and never deselects; a region keeps
        // such an object selected by us, and on Second Life a selected physical object is frozen. Let go of it.
        if (!_s.State.Selected.ContainsKey(p.LocalID) && c.Network.CurrentSim is { } sim) c.Objects.DeselectObject(sim, p.LocalID);
        var ev = new JsonObject
        {
            ["event"] = "object_created", ["id"] = p.ID.ToString(), ["local_id"] = p.LocalID, ["pos"] = Json.Arr(p.Position),
        };
        Task? after = null;
        if (pc is not null)
        {
            ev["requested"] = true;
            _s.Undo.Created(p, $"made {p.Properties?.Name ?? "an object"}");
            var mod = pc.Modify?.DeepClone() as JsonObject ?? new JsonObject();
            if (pc.MoveToPos && Vector3.Distance(p.Position, pc.WantPos) > 0.01f) mod["pos"] = Json.Arr(pc.WantPos);
            if (mod.Count > 0)
            {
                try { after = Modify(p, mod, record: false); }
                catch (Exception e) { ev["modify_error"] = pc.ModifyError = e.Message; }
            }
        }
        _s.Events.Log("TesselEvents", ev);
        if (after is null) pc?.Done.TrySetResult(p);
        else
            // materials go through a capability: the request answers once they're applied (or refused)
            after.ContinueWith(t =>
            {
                if (t.Exception?.GetBaseException() is { } x) pc!.ModifyError = x.Message;
                pc!.Done.TrySetResult(p);
            }, TaskScheduler.Default);
    }

    // ---------- link, unlink, take, delete, return ----------

    List<Primitive> Roots(JsonObject a)
    {
        var w = W;
        var roots = new List<Primitive>();
        foreach (var id in a.UuidList("ids").Concat(a.Uuid("id") is { } one ? new[] { one } : Array.Empty<UUID>()))
        {
            var p = w.PrimById(id) ?? throw new LeapError($"object not found: {id}");
            var r = _s.Objects.RootOf(w, p);
            if (!roots.Any(x => x.LocalID == r.LocalID)) roots.Add(r);
        }
        return roots;
    }

    uint ParentOf(RegionWorld w, uint local) => w.Prims.TryGetValue(local, out var p) ? p.ParentID : uint.MaxValue;

    public async Task<JsonObject> LinkAsync(JsonObject a, CancellationToken ct)
    {
        var w = W;
        var roots = Roots(a);
        if (roots.Any(r => _s.Objects.IsAttachment(w, r, out _, out _))) throw new LeapError("can't link attachments");
        if (roots.Count < 2) throw new LeapError("need at least two objects");
        foreach (var r in roots) RequireModify(r, "link");
        var locals = roots.Select(r => r.LocalID).ToList();
        C.Objects.LinkPrims(Sim, locals);
        // linked: one of them is the root and the others are its children
        if (!await UntilAsync(() => locals.Count(l => ParentOf(w, l) == 0) == 1 && locals.All(l => ParentOf(w, l) is 0 || locals.Contains(ParentOf(w, l))), ct))
            throw new LeapError("the region didn't link them (they must all be mine to modify, and close enough together)");
        var root = locals.First(l => ParentOf(w, l) == 0);
        if (w.Prims.TryGetValue(root, out var rootPrim))
            _s.Undo.Linked(rootPrim.ID, locals.Select(l => w.Prims.TryGetValue(l, out var lp) ? lp.ID : UUID.Zero).Where(x => x != UUID.Zero));
        return new JsonObject { ["root_id"] = w.Prims.TryGetValue(root, out var rp) ? rp.ID.ToString() : null, ["linked"] = locals.Count };
    }

    public async Task<JsonObject> UnlinkAsync(JsonObject a, CancellationToken ct)
    {
        var w = W;
        var ids = new List<uint>();
        var asked = a.UuidList("ids");
        if (a.Uuid("id") is { } one && !asked.Contains(one)) asked.Add(one);
        if (asked.Count == 0) throw new LeapError("need id or ids");
        foreach (var id in asked)
        {
            var p = w.PrimById(id) ?? throw new LeapError($"object not found: {id}");
            RequireModify(_s.Objects.RootOf(w, p), "unlink");
            if (p.ParentID == 0) ids.AddRange(_s.Objects.Linkset(w, p).Select(x => x.LocalID));
            else ids.Add(p.LocalID);
        }
        ids = ids.Distinct().ToList();
        // for undo: each linkset's root as it was, and what left it
        var sets = ids.Where(w.Prims.ContainsKey).GroupBy(l => _s.Objects.RootOf(w, w.Prims[l]).ID)
            .Select(g => (Root: g.Key, Prims: g.Select(l => w.Prims[l].ID).ToList())).ToList();
        C.Objects.DelinkPrims(Sim, ids);
        if (!await UntilAsync(() => ids.All(l => ParentOf(w, l) is 0 or uint.MaxValue), ct))
            throw new LeapError("the region didn't unlink them (that needs modify rights on the object)");
        foreach (var (r, prims) in sets) _s.Undo.Unlinked(r, prims);
        return new JsonObject { ["unlinked"] = ids.Count };
    }

    public async Task<JsonObject> DeRezAsync(JsonObject a, DeRezDestination dest, CancellationToken ct)
    {
        var c = C;
        var roots = Roots(a);
        var folder = UUID.Zero;
        if (dest is DeRezDestination.AgentInventoryTake or DeRezDestination.AgentInventoryCopy)
            folder = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(FolderType.Object);
        else if (dest == DeRezDestination.TrashFolder)
            folder = _s.Inventory.FolderFor(FolderType.Trash);
        if (dest == DeRezDestination.TaskInventory)
        {
            // the viewer's Save Object Back to Object Contents: only for objects rezzed from an object's contents
            foreach (var r in roots)
                if (_s.Objects.IsAttachment(W, r, out _, out _)) throw new LeapError("a worn object can't be saved back into an object's contents");
                else if (r.Properties is not { FromTaskID: var from } || from == UUID.Zero)
                    throw new LeapError($"{r.Properties?.Name ?? "that object"} wasn't rezzed from an object's contents, so there's nothing to save it back into");
            folder = roots[0].Properties!.FromTaskID;
        }
        // several objects in one message become one item (the viewer's take); separate: an item each (Firestorm's
        // Take Copy / Take separately)
        bool separate = a.Bool("separate", false) && roots.Count > 1;
        DeRezObjectPacket Packet(IEnumerable<Primitive> rs, UUID dfolder) => new()
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            AgentBlock =
            {
                GroupID = c.Self.ActiveGroup, Destination = (byte)dest, DestinationID = dfolder, TransactionID = UUID.Random(),
                PacketCount = 1, PacketNumber = 0,
            },
            ObjectData = rs.Select(r => new DeRezObjectPacket.ObjectDataBlock { ObjectLocalID = r.LocalID }).ToArray(),
        };
        var w = W;
        var since = DateTime.UtcNow;
        if (dest == DeRezDestination.TaskInventory)
            foreach (var r in roots) c.Network.SendPacket(Packet(new[] { r }, r.Properties!.FromTaskID), Sim);
        else if (separate)
            foreach (var r in roots) c.Network.SendPacket(Packet(new[] { r }, folder), Sim);
        else c.Network.SendPacket(Packet(roots, folder), Sim);
        if (dest == DeRezDestination.TaskInventory) return new JsonObject { ["count"] = roots.Count, ["saved_into"] = folder.ToString(), ["requested"] = true };
        // a copy leaves the object where it is; the rest take it out of the world, so watch it go
        if (dest == DeRezDestination.AgentInventoryCopy) return new JsonObject { ["count"] = roots.Count, ["requested"] = true };
        var locals = roots.Select(r => r.LocalID).ToList();
        if (!await UntilAsync(() => locals.All(l => !w.Prims.ContainsKey(l)), ct))
        {
            var left = locals.Count(l => w.Prims.ContainsKey(l));
            var said = _s.State.LastAlert is { } al && al.T >= since ? $"; the region said: {al.Message}" : "";
            throw new LeapError($"{left} of {locals.Count} still in place: the region didn't take them (that needs them to be mine, or my land's for returning){said}");
        }
        return new JsonObject { ["count"] = roots.Count };
    }
}
