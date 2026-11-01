using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.World;

namespace Sojourn.Core.Agents;

/// <summary>
/// TesselObjects' view of things: object descriptions in the viewer's shape (tesselobjects.cpp describe), and
/// property fetching. Properties are fetched once per region for every agent there (they live on the shared prim);
/// flags are this agent's own (mine/mod/copy differ per observer).
/// </summary>
public sealed class ObjectViews
{
    readonly AgentSession _s;
    readonly SemaphoreSlim _throttle = new(1, 1);
    DateTime _windowStart = DateTime.UtcNow;
    int _sentInWindow;

    public ObjectViews(AgentSession s) => _s = s;

    public static readonly string[] AttachPointNames =
    {
        "Default", "Chest", "Skull", "Left Shoulder", "Right Shoulder", "Left Hand", "Right Hand", "Left Foot",
        "Right Foot", "Spine", "Pelvis", "Mouth", "Chin", "Left Ear", "Right Ear", "Left Eyeball", "Right Eyeball",
        "Nose", "R Upper Arm", "R Forearm", "L Upper Arm", "L Forearm", "Right Hip", "R Upper Leg", "R Lower Leg",
        "Left Hip", "L Upper Leg", "L Lower Leg", "Stomach", "Left Pec", "Right Pec", "HUD Center 2", "HUD Top Right",
        "HUD Top", "HUD Top Left", "HUD Center", "HUD Bottom Left", "HUD Bottom", "HUD Bottom Right", "Neck",
        "Avatar Center", "Left Ring Finger", "Right Ring Finger", "Tail Base", "Tail Tip", "Left Wing", "Right Wing",
        "Jaw", "Alt Left Ear", "Alt Right Ear", "Alt Left Eye", "Alt Right Eye", "Tongue", "Groin", "Left Hind Foot",
        "Right Hind Foot",
    };

    public static string AttachPointName(int id) => id >= 0 && id < AttachPointNames.Length ? AttachPointNames[id] : id.ToString();

    public static int? AttachPointId(string name)
    {
        if (int.TryParse(name, out var n)) return n;
        // the viewer's names ("Left Shoulder", "Center 2" for the HUD point) and LibreMetaverse's ("LeftShoulder",
        // "HUD Center 2"): compare without spaces, case or a "HUD " prefix
        static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var want = Norm(name);
        // plain words for the points whose SL names aren't the obvious ones
        want = want switch { "head" or "hat" => "skull", "face" => "chin", "back" => "spine", "belly" => "stomach", "hips" => "pelvis", _ => want };
        for (var i = 0; i < AttachPointNames.Length; i++)
            if (Norm(AttachPointNames[i]) == want) return i;
        for (var i = 0; i < AttachPointNames.Length; i++)
            if (AttachPointNames[i].StartsWith("HUD ", StringComparison.OrdinalIgnoreCase) && Norm(AttachPointNames[i][4..]) == want) return i;
        static string Short(string s) => Norm(s).Replace("right", "r").Replace("left", "l");
        for (var i = 0; i < AttachPointNames.Length; i++)
            if (Short(AttachPointNames[i]) == Short(name)) return i;
        return null;
    }

    static readonly string[] ClickNames = { "touch", "sit", "buy", "pay", "open", "play", "open_media", "zoom", "disabled" };
    public static string? ClickActionName(ClickAction a) => a == ClickAction.Touch ? null : (int)a < ClickNames.Length ? ClickNames[(int)a] : "touch";
    public static ClickAction? ClickActionFrom(string name)
    {
        var i = Array.IndexOf(ClickNames, name.ToLowerInvariant());
        return i < 0 ? null : (ClickAction)i;
    }

    public static string ShapeName(Primitive p)
    {
        // any sculpt parameter with a map, type none included, is a sculpt (LLVOVolume::isSculpted)
        if (p.Sculpt is { } sc && sc.SculptTexture != UUID.Zero)
            return sc.Type == SculptType.Mesh ? "mesh" : "sculpt";
        var d = p.PrimData;
        var profile = d.profileCurve & 0x0f;
        var line = ((byte)d.PathCurve & 0xf0) is 0x10 or 0x80;   // a flexible path is a line that bends
        return profile switch
        {
            1 => line ? "box" : "tube",
            0 => line ? "cylinder" : "torus",
            3 => line ? "prism" : "ring",
            5 => line ? "half-circle" : "sphere",
            _ => "other",
        };
    }

    public static string PermLetters(PermissionMask m)
    {
        var s = "";
        if ((m & PermissionMask.Modify) != 0) s += "M";
        if ((m & PermissionMask.Copy) != 0) s += "C";
        if ((m & PermissionMask.Transfer) != 0) s += "T";
        if ((m & PermissionMask.Move) != 0) s += "V";
        return s.Length == 0 ? "-" : s;
    }

    public static PermissionMask PermMask(string letters)
    {
        var m = PermissionMask.None;
        foreach (var ch in letters.ToUpperInvariant())
            m |= ch switch { 'M' => PermissionMask.Modify, 'C' => PermissionMask.Copy, 'T' => PermissionMask.Transfer, 'V' => PermissionMask.Move, _ => 0 };
        return m;
    }

    static readonly Dictionary<SaleType, string> SaleNames = new()
    {
        [SaleType.Original] = "original", [SaleType.Copy] = "copy", [SaleType.Contents] = "contents",
    };

    public static SaleType? SaleTypeFrom(string name) => name.ToLowerInvariant() switch
    {
        "original" or "orig" => SaleType.Original,
        "copy" => SaleType.Copy,
        "contents" or "cntn" => SaleType.Contents,
        "not" or "none" => SaleType.Not,
        _ => null,
    };

    /// <summary>The cached-properties part of a description (also the object_properties event body).</summary>
    public static JsonObject PropsJson(Primitive.ObjectProperties p, bool full, long created = 0)
    {
        var o = new JsonObject
        {
            ["name"] = p.Name,
            ["desc"] = p.Description,
            ["owner_id"] = p.OwnerID.ToString(),
            ["perms"] = new JsonObject
            {
                ["base"] = PermLetters(p.Permissions.BaseMask),
                ["owner"] = PermLetters(p.Permissions.OwnerMask),
                ["group"] = PermLetters(p.Permissions.GroupMask),
                ["everyone"] = PermLetters(p.Permissions.EveryoneMask),
                ["next"] = PermLetters(p.Permissions.NextOwnerMask),
            },
        };
        if (p.GroupID != UUID.Zero) o["group_id"] = p.GroupID.ToString();
        if (p.LastOwnerID != UUID.Zero && p.LastOwnerID != p.OwnerID) o["last_owner_id"] = p.LastOwnerID.ToString();
        if (p.SaleType != SaleType.Not && SaleNames.TryGetValue(p.SaleType, out var st))
            o["sale"] = new JsonObject { ["type"] = st, ["price"] = p.SalePrice };
        if (full)
        {
            o["creator_id"] = p.CreatorID.ToString();
            o["created"] = created;
            if (!string.IsNullOrEmpty(p.TouchName)) o["touch_name"] = p.TouchName;
            if (!string.IsNullOrEmpty(p.SitName)) o["sit_name"] = p.SitName;
            o["full"] = true;
        }
        return o;
    }

    public Primitive RootOf(RegionWorld w, Primitive p)
    {
        var cur = p;
        for (var i = 0; i < 64 && cur.ParentID != 0 && w.Prims.TryGetValue(cur.ParentID, out var parent); i++) cur = parent;
        return cur;
    }

    public bool IsAttachment(RegionWorld w, Primitive p, out Avatar? wearer, out Primitive root)
    {
        root = RootOf(w, p);
        wearer = root.ParentID != 0 && w.Avatars.TryGetValue(root.ParentID, out var av) ? av : null;
        return wearer is not null || root.IsAttachment;
    }

    /// <summary>Distance from the agent, metres (both in the agent's region frame).</summary>
    public double Dist(RegionWorld w, Primitive p)
    {
        var me = _s.Client!.Self.SimPosition;
        return Vector3.Distance(w.RegionPosition(p) + _s.FrameOffset(w), me);
    }

    /// <summary>A prim in any region this agent sees, with its world.</summary>
    public (RegionWorld World, Primitive Prim)? FindAnywhere(UUID id)
    {
        foreach (var w in _s.VisibleRegions())
            if (w.PrimById(id) is { } p) return (w, p);
        return null;
    }

    /// <summary>A prim's face settings as TesselBuild modify's keys name them: the default face as "all", and each face
    /// that differs from it by number (only what differs). So a build can be checked by numbers (2026-10-26: a colour
    /// and a shininess given at create were thought lost on the main grid, and nothing could say).</summary>
    /// <summary>A prim's reflection probe as the renderer read it from the region's updates (LibreMetaverse skips that
    /// extra param), for TesselObjects get full: set by the client.</summary>
    public static Func<ulong, uint, JsonObject?>? ProbeOf { get; set; }

    /// <summary>A sculpt's map and stitching, or a mesh's asset, for TesselObjects get full: what the renderer builds the
    /// shape from (2026-10-29: a sculpt drawn as streaks couldn't be looked into without its map).</summary>
    public static JsonObject? ShapeAssetJson(Primitive p)
    {
        if (p.Sculpt is not { } sc || sc.SculptTexture == UUID.Zero) return null;
        if (sc.Type == SculptType.Mesh) return new JsonObject { ["mesh"] = sc.SculptTexture.ToString() };
        return new JsonObject
        {
            ["sculpt_map"] = sc.SculptTexture.ToString(),
            ["stitching"] = sc.Type.ToString().ToLowerInvariant(),
            ["invert"] = sc.Invert,
            ["mirror"] = sc.Mirror,
        };
    }

    /// <summary>llSetTextureAnim as the prim carries it (it replaces the faces' repeats, offset or rotation while it runs).</summary>
    public static JsonObject? TextureAnimJson(Primitive p)
    {
        var ta = p.TextureAnim;
        if ((ta.Flags & Primitive.TextureAnimMode.ANIM_ON) == 0) return null;
        return new JsonObject
        {
            ["flags"] = ta.Flags.ToString().ToLowerInvariant(),
            ["face"] = ta.Face == 255 ? "all" : ta.Face,
            ["size"] = new JsonArray((int)ta.SizeX, (int)ta.SizeY),
            ["start"] = Json.R(ta.Start, 3), ["length"] = Json.R(ta.Length, 3), ["rate"] = Json.R(ta.Rate, 3),
        };
    }

    public static JsonObject? FacesJson(Primitive p)
    {
        if (p.Textures is not { DefaultTexture: { } def } te) return null;
        static JsonObject Face(Primitive.TextureEntryFace f) => new()
        {
            ["color"] = new JsonArray(Json.R(f.RGBA.R, 3), Json.R(f.RGBA.G, 3), Json.R(f.RGBA.B, 3)), ["alpha"] = Json.R(f.RGBA.A, 3),
            ["texture"] = f.TextureID.ToString(),
            ["shiny"] = f.Shiny switch { Shininess.Low => "low", Shininess.Medium => "medium", Shininess.High => "high", _ => "none" },
            ["glow"] = Json.R(f.Glow, 3), ["fullbright"] = f.Fullbright, ["bump"] = f.Bump.ToString().ToLowerInvariant(),
            ["repeats"] = new JsonArray(Json.R(f.RepeatU, 3), Json.R(f.RepeatV, 3)),
            ["tex_offset"] = new JsonArray(Json.R(f.OffsetU, 3), Json.R(f.OffsetV, 3)),
            ["tex_rotation"] = Json.R(f.Rotation * 180 / Math.PI, 2),
            ["texgen"] = f.TexMapType == MappingType.Planar ? "planar" : "default",
        };
        var all = Face(def);
        var o = new JsonObject { ["all"] = all };
        int n = Math.Min(te.FaceTextures.Length, 9);
        for (int i = 0; i < n; i++)
        {
            if (te.FaceTextures[i] is not { } f) continue;
            var face = Face(f);
            var diff = new JsonObject();
            foreach (var (k, v) in face)
                if (!JsonNode.DeepEquals(v, all[k])) diff[k] = v?.DeepClone();
            if (diff.Count > 0) o[i.ToString()] = diff;
        }
        return o;
    }

    public JsonObject Describe(RegionWorld w, Primitive p, bool children = false)
    {
        var flags = _s.State.PrimFlagsOf(p);
        var root = RootOf(w, p);
        var isRoot = ReferenceEquals(root, p) || root.LocalID == p.LocalID;
        var pos = w.RegionPosition(p);
        var o = new JsonObject
        {
            ["id"] = p.ID.ToString(),
            ["local_id"] = p.LocalID,
        };
        if (!isRoot) o["root_id"] = root.ID.ToString();
        if (p.PrimData.PCode is PCode.Tree or PCode.NewTree) o["kind"] = "tree";
        else if (p.PrimData.PCode == PCode.Grass) o["kind"] = "grass";
        else if (p.PrimData.PCode != PCode.Prim) o["kind"] = (int)p.PrimData.PCode;
        o["pos"] = Json.Arr(pos);
        o["rot"] = Json.Arr(w.RegionRotation(p));
        o["scale"] = Json.Arr(p.Scale);
        // the motion its last update carried, when there is any: zero while it visibly moves means it's moved by
        // script in jumps (llSetPos and the like), which every viewer draws as steps
        if (p.Velocity != Vector3.Zero) o["vel"] = Json.Arr(p.Velocity);
        if (p.Acceleration != Vector3.Zero) o["acc"] = Json.Arr(p.Acceleration);
        if (p.AngularVelocity != Vector3.Zero) o["omega"] = Json.Arr(p.AngularVelocity);
        if (p.PrimData.PCode == PCode.Prim) o["shape"] = ShapeName(p);
        if (p.PrimData.PCode == PCode.Prim) o["material"] = p.PrimData.Material.ToString().ToLowerInvariant();
        if (p.Flexible is { } fx && ((byte)p.PrimData.PathCurve & 0xf0) == 0x80)
            o["flexi"] = new JsonObject
            {
                ["softness"] = fx.Softness, ["gravity"] = Json.R(fx.Gravity, 2), ["drag"] = Json.R(fx.Drag, 2), ["wind"] = Json.R(fx.Wind, 2),
                ["tension"] = Json.R(fx.Tension, 2), ["force"] = Json.Arr(fx.Force),
            };
        o["dist"] = Json.R(Vector3.Distance(pos + _s.FrameOffset(w), _s.Client!.Self.SimPosition), 3);
        if (isRoot)
        {
            var linkset = Linkset(w, p);
            o["prims"] = linkset.Count;
            var seated = w.Avatars.Values.Where(a => a.ParentID != 0 && linkset.Any(l => l.LocalID == a.ParentID)).Select(a => a.ID).ToList();
            if (seated.Count > 0) o["seated"] = Json.Ids(seated);
        }
        if (!string.IsNullOrEmpty(p.Text))
        {
            o["text"] = p.Text;
            o["text_color"] = Json.Arr(p.TextColor);
        }
        var fl = new JsonArray();
        void F(PrimFlags f, string n) { if ((flags & f) != 0) fl.Add(n); }
        F(PrimFlags.Touch, "touch"); F(PrimFlags.Money, "pay"); F(PrimFlags.Scripted, "scripted");
        F(PrimFlags.ObjectYouOwner, "mine"); F(PrimFlags.ObjectModify, "mod"); F(PrimFlags.ObjectCopy, "copy");
        F(PrimFlags.ObjectMove, "move"); F(PrimFlags.Phantom, "phantom"); F(PrimFlags.Physics, "physics");
        F(PrimFlags.TemporaryOnRez, "temp"); F(PrimFlags.AllowInventoryDrop, "drop");
        o["flags"] = fl;
        if (p.IsAnimatedObject) o["animated"] = true;
        if (ClickActionName(p.ClickAction) is { } ca) o["click_action"] = ca;
        if (IsAttachment(w, p, out var wearer, out var r) && wearer is not null)
        {
            var point = (int)r.PrimData.AttachmentPoint;
            o["attached_to"] = wearer.ID.ToString();
            o["attach_point"] = AttachPointName(point);
            if (point is >= 31 and <= 38) o["hud"] = true;
        }
        if (p.Properties is { } props && props.ObjectID == p.ID)
        {
            var full = w.PropsMeta.TryGetValue(p.ID, out var meta) && meta.Full;
            foreach (var (k, v) in PropsJson(props, full, meta?.Created ?? 0)) o[k] = v?.DeepClone();
        }
        else if (p.OwnerID != UUID.Zero) o["owner_id"] = p.OwnerID.ToString();
        if (children && isRoot)
        {
            var kids = new JsonArray();
            foreach (var c in Linkset(w, p).Where(c => c.LocalID != p.LocalID)) kids.Add(Describe(w, c));
            o["children"] = kids;
        }
        return o;
    }

    /// <summary>The root and every prim linked to it (not avatars), root first.</summary>
    public List<Primitive> Linkset(RegionWorld w, Primitive root)
    {
        var list = new List<Primitive> { root };
        foreach (var p in w.Prims.Values)
            if (p.ParentID == root.LocalID && p.LocalID != root.LocalID) list.Add(p);
        return list;
    }

    public bool Listable(RegionWorld w, Primitive p, bool attachments)
    {
        if (p.ParentID != 0)
        {
            // children are never listed; attachments are roots parented to an avatar
            if (!w.Avatars.TryGetValue(p.ParentID, out var av)) return false;
            if (!attachments) return false;
            if (av.ID == _s.AgentId) return false;
            if ((int)p.PrimData.AttachmentPoint is >= 31 and <= 38) return false;
        }
        // an attachment with no wearer: a copy a crossing left behind (worn things are parented to their avatar)
        else if (p.IsAttachment) return false;
        return p.PrimData.PCode is PCode.Prim or PCode.Tree or PCode.NewTree or PCode.Grass;
    }

    bool Fresh(RegionWorld w, Primitive p, bool full) =>
        p.Properties is { } props && props.ObjectID == p.ID && w.PropsMeta.TryGetValue(p.ID, out var m)
        && DateTime.UtcNow - m.FetchedAt < TimeSpan.FromMinutes(5) && (!full || m.Full);

    /// <summary>Asks the region for properties of the prims that lack fresh ones (20/s), then waits until all are in
    /// or the timeout passes. Returns true if all are fresh.</summary>
    public async Task<bool> FetchAsync(RegionWorld w, IReadOnlyList<Primitive> prims, TimeSpan timeout, CancellationToken ct,
        bool force = false, Simulator? sim = null)
    {
        var c = _s.Client;
        sim ??= c?.Network.CurrentSim;
        if (c is null || sim is null) return false;
        var end = DateTime.UtcNow + timeout;
        foreach (var p in prims)
        {
            if (!force && Fresh(w, p, false)) continue;
            var meta = w.PropsMeta.GetOrAdd(p.ID, _ => new PropsMeta());
            if (!force && DateTime.UtcNow - meta.RequestedAt < TimeSpan.FromSeconds(5)) continue;
            meta.RequestedAt = DateTime.UtcNow;
            await ThrottleAsync(ct);
            c.Objects.RequestObjectPropertiesFamily(sim, p.ID);
        }
        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            if (prims.All(p => Fresh(w, p, false) || !w.Prims.ContainsKey(p.LocalID))) return true;
            await Task.Delay(100, ct);
        }
        return prims.All(p => Fresh(w, p, false));
    }

    /// <summary>Asks again for the prims' properties even if recent ones are known (a script may have renamed it: the
    /// region sends a rename only to those who have the object selected) and waits until each has answered, or is
    /// gone, or the timeout passes. Returns true if all answered.</summary>
    public async Task<bool> RenewAsync(RegionWorld w, IReadOnlyList<Primitive> prims, TimeSpan timeout, CancellationToken ct, Simulator? sim = null)
    {
        if (prims.Count == 0) return true;
        var asked = DateTime.UtcNow;
        await FetchAsync(w, prims, TimeSpan.Zero, ct, force: true, sim: sim);
        bool Answered(Primitive p) => !w.Prims.ContainsKey(p.LocalID) || w.PropsMeta.TryGetValue(p.ID, out var m) && m.FetchedAt >= asked;
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end && !ct.IsCancellationRequested && !prims.All(Answered)) await Task.Delay(100, ct);
        return prims.All(Answered);
    }

    /// <summary>When the prim's properties last came (MinValue if never).</summary>
    public static DateTime FetchedAt(RegionWorld w, Primitive p) =>
        p.Properties is { } props && props.ObjectID == p.ID && w.PropsMeta.TryGetValue(p.ID, out var m) ? m.FetchedAt : DateTime.MinValue;

    /// <summary>The local ids of the roots whose linkset holds a script (only a script renames an object by itself:
    /// llSetObjectName, or a child's PRIM_NAME on the root), from the region's scripted flags in one pass.</summary>
    public HashSet<uint> ScriptedRoots(RegionWorld w)
    {
        var roots = new HashSet<uint>();
        foreach (var p in w.Prims.Values)
            if ((_s.State.PrimFlagsOf(p) & PrimFlags.Scripted) != 0) roots.Add(p.ParentID != 0 && !w.Avatars.ContainsKey(p.ParentID) ? p.ParentID : p.LocalID);
        return roots;
    }

    /// <summary>Full properties (creator, dates, touch/sit names): briefly selects the roots, like the edit tool.</summary>
    public async Task<bool> FetchFullAsync(RegionWorld w, IReadOnlyList<Primitive> roots, TimeSpan timeout, CancellationToken ct)
    {
        var c = _s.Client;
        var sim = c?.Network.CurrentSim;
        if (c is null || sim is null) return false;
        var need = roots.Where(p => !Fresh(w, p, true)).Select(p => p.LocalID).ToArray();
        if (need.Length == 0) return true;
        c.Objects.SelectObjects(sim, need, automaticDeselect: false);
        try
        {
            var end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
            {
                if (roots.All(p => Fresh(w, p, true))) return true;
                await Task.Delay(100, ct);
            }
            return false;
        }
        finally
        {
            var keep = need.Where(id => _s.State.Selected.ContainsKey(id)).ToHashSet();
            var drop = need.Where(id => !keep.Contains(id)).ToArray();
            if (drop.Length > 0) c.Objects.DeselectObjects(sim, drop);
        }
    }

    async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            if (now - _windowStart >= TimeSpan.FromSeconds(1)) { _windowStart = now; _sentInWindow = 0; }
            if (++_sentInWindow > 20)
            {
                await Task.Delay(_windowStart + TimeSpan.FromSeconds(1) - now, ct);
                _windowStart = DateTime.UtcNow;
                _sentInWindow = 1;
            }
        }
        finally { _throttle.Release(); }
    }

    /// <summary>Finds a prim (any link) by full id in the agent's region.</summary>
    public Primitive? Find(RegionWorld w, UUID id) => w.PrimById(id);
}
