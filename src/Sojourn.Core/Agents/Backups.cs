using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Firestorm's Backup and Import Linkset (fsfloaterexport.cpp, fsfloaterimport.cpp, fsexportperms.cpp) for what the
/// person made themselves. The rules are Firestorm's: on Second Life a prim goes in only if the person owns it and
/// created it, and a texture only if their inventory holds an item of it that they created (one that fails becomes
/// plywood, as there); meshes never go into a backup on Second Life. On OpenSim the grid's ExportSupported decides
/// (creator or the export permission; without it, the owner with full permissions). The file is Firestorm's .oxp
/// (format 2: zlib over binary LLSD, prims by Firestorm's keys, textures as J2C), so either viewer can restore it.
/// Backups go into Documents\Sojourn\Backups only; nothing here takes a path to write. Contents are listed but not
/// kept yet. Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class Backups
{
    readonly AgentSession _s;
    public Backups(AgentSession s) => _s = s;

    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sojourn", "Backups");

    static readonly UUID Plywood = new("89556747-24cb-43ed-920b-47caed15465f");

    // ---------- the rules ----------

    enum Policy { SecondLife, Allowed, Undefined, Denied }

    Policy GridPolicy()
    {
        var f = C.Network.CurrentSim?.Features;
        if (f?.Get("OpenSimExtras") is not OSDMap extras) return Policy.SecondLife;
        if (!extras.ContainsKey("ExportSupported")) return Policy.Undefined;
        return extras["ExportSupported"].AsBoolean() ? Policy.Allowed : Policy.Denied;
    }

    const PermissionMask Full = PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Transfer;
    const PermissionMask Export = (PermissionMask)(1 << 16);

    bool MayExport(Policy policy, UUID creator, UUID owner, PermissionMask ownerMask, PermissionMask everyone)
    {
        var me = C.Self.AgentID;
        return policy switch
        {
            Policy.SecondLife or Policy.Denied => owner == me && creator == me,
            Policy.Allowed => owner == me && (creator == me || (everyone & Export) != 0),
            _ => owner == me && (creator == me || (ownerMask & Full) == Full),
        };
    }

    /// <summary>Asset ids to items of mine with them (Trash included), for canExportAsset's "an item you made".</summary>
    Dictionary<UUID, List<InventoryItem>> ItemsByAsset()
    {
        var by = new Dictionary<UUID, List<InventoryItem>>();
        if (C.Inventory.Store is not { } store) return by;
        var stack = new Stack<UUID>();
        if (store.RootFolder is not { } top) return by;
        stack.Push(top.UUID);
        while (stack.Count > 0)
        {
            var f = stack.Pop();
            if (!store.Contains(f)) continue;
            foreach (var n in store.GetContents(f))
                if (n is InventoryFolder sub) stack.Push(sub.UUID);
                else if (n is InventoryItem it && it.AssetUUID != UUID.Zero)
                    (by.TryGetValue(it.AssetUUID, out var l) ? l : by[it.AssetUUID] = new List<InventoryItem>()).Add(it);
        }
        return by;
    }

    bool TextureOk(Policy policy, UUID tex, Dictionary<UUID, List<InventoryItem>> items) =>
        tex == UUID.Zero || tex == Plywood
        || items.TryGetValue(tex, out var its) && its.Any(i => MayExport(policy, i.CreatorID, i.OwnerID, i.Permissions.OwnerMask, i.Permissions.EveryoneMask));

    static IEnumerable<UUID> TexturesOf(Primitive p)
    {
        if (p.Textures is { } te)
        {
            if (te.DefaultTexture is { } d) yield return d.TextureID;
            foreach (var f in te.FaceTextures) if (f is not null) yield return f.TextureID;
        }
        if (p.Sculpt is { } sc && sc.Type != SculptType.Mesh && sc.SculptTexture != UUID.Zero) yield return sc.SculptTexture;
        if (p.LightMap is { LightTexture: var lt } && lt != UUID.Zero) yield return lt;
    }

    sealed record Verdict(Primitive Root, List<Primitive> Prims, List<string> Refused, HashSet<UUID> Mine, HashSet<UUID> NotMine, Policy Policy);

    async Task<Verdict> JudgeAsync(JsonObject a, CancellationToken ct)
    {
        var w = _s.Region ?? throw new LeapError("no region");
        var p = w.PrimById(a.Uuid("id") ?? throw new LeapError("need id")) ?? throw new LeapError("object not found in this region");
        var root = _s.Objects.RootOf(w, p);
        var prims = _s.Objects.Linkset(w, root);
        // the creator comes with the full properties (the edit tool's selection)
        await _s.Objects.FetchFullAsync(w, new[] { root }, TimeSpan.FromSeconds(6), ct);
        await _s.Objects.FetchAsync(w, prims, TimeSpan.FromSeconds(6), ct);
        var policy = GridPolicy();
        var refused = new List<string>();
        foreach (var x in prims)
        {
            var n = x.Properties?.Name ?? x.ID.ToString();
            if (x.Properties is not { } pr || pr.CreatorID == UUID.Zero) { refused.Add($"{n}: its creator didn't arrive (try again)"); continue; }
            if (!MayExport(policy, pr.CreatorID, pr.OwnerID, pr.Permissions.OwnerMask, pr.Permissions.EveryoneMask))
                refused.Add(policy is Policy.SecondLife or Policy.Denied ? $"{n}: made by someone else (only what you made yourself can be backed up)" : $"{n}: not yours to export on this grid");
            else if (x.Sculpt is { Type: SculptType.Mesh } && policy == Policy.SecondLife)
                refused.Add($"{n}: a mesh (Firestorm keeps meshes out of backups on Second Life)");
        }
        var items = ItemsByAsset();
        var mine = new HashSet<UUID>();
        var notMine = new HashSet<UUID>();
        foreach (var t in prims.SelectMany(TexturesOf).Distinct())
            (TextureOk(policy, t, items) ? mine : notMine).Add(t);
        // a sculpt map that isn't theirs leaves the prim out, as Firestorm's canExportNode
        foreach (var x in prims)
            if (x.Sculpt is { } sc && sc.Type != SculptType.Mesh && notMine.Contains(sc.SculptTexture))
                refused.Add($"{x.Properties?.Name ?? x.ID.ToString()}: its sculpt map was made by someone else");
        return new Verdict(root, prims, refused, mine, notMine, policy);
    }

    public async Task<JsonObject> CheckAsync(JsonObject a, CancellationToken ct)
    {
        var v = await JudgeAsync(a, ct);
        var o = new JsonObject
        {
            ["exportable"] = v.Refused.Count == 0, ["name"] = v.Root.Properties?.Name ?? "", ["prims"] = v.Prims.Count,
            ["textures"] = new JsonObject { ["mine"] = v.Mine.Count(t => t != UUID.Zero && t != Plywood), ["not_mine"] = v.NotMine.Count },
            ["rules"] = v.Policy == Policy.SecondLife ? "Second Life: only what you made yourself" : $"this grid: {v.Policy.ToString().ToLowerInvariant()}",
        };
        if (v.Refused.Count > 0) o["why"] = Json.Strings(v.Refused);
        if (v.NotMine.Count > 0) o["note"] = "textures made by others become plywood in the backup";
        return o;
    }

    // ---------- writing a backup ----------

    static OSD U32(uint v) => OSD.FromBinary(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
    static uint U32Of(byte[] b) => b.Length < 4 ? 0 : (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
    static OSD Vec(Vector3 v) => new OSDArray(new List<OSD> { OSD.FromReal(v.X), OSD.FromReal(v.Y), OSD.FromReal(v.Z) });
    static OSD Col(Color4 c) => new OSDArray(new List<OSD> { OSD.FromReal(c.R), OSD.FromReal(c.G), OSD.FromReal(c.B), OSD.FromReal(c.A) });

    static OSDMap Te(Primitive.TextureEntryFace f, UUID tex) => new()
    {
        ["imageid"] = OSD.FromUUID(tex), ["colors"] = Col(f.RGBA), ["scales"] = OSD.FromReal(f.RepeatU), ["scalet"] = OSD.FromReal(f.RepeatV),
        ["offsets"] = OSD.FromReal(f.OffsetU), ["offsett"] = OSD.FromReal(f.OffsetV), ["imagerot"] = OSD.FromReal(f.Rotation),
        ["bump"] = OSD.FromInteger((byte)f.Bump | (byte)f.Shiny), ["fullbright"] = OSD.FromInteger(f.Fullbright ? 1 : 0),
        ["media_flags"] = OSD.FromInteger((f.MediaFlags ? 1 : 0) | (f.TexMapType == MappingType.Planar ? 2 : 0)), ["glow"] = OSD.FromReal(f.Glow),
    };

    OSDMap PrimLlsd(Primitive p, Primitive root, int faces, Verdict v)
    {
        var d = p.PrimData;
        var pr = p.Properties!;
        var prim = new OSDMap();
        if (p.LocalID != root.LocalID) prim["parent"] = OSD.FromUUID(root.ID);
        prim["position"] = Vec(p.Position);
        prim["scale"] = Vec(p.Scale);
        prim["rotation"] = new OSDArray(new List<OSD> { OSD.FromReal(p.Rotation.X), OSD.FromReal(p.Rotation.Y), OSD.FromReal(p.Rotation.Z), OSD.FromReal(p.Rotation.W) });
        prim["flags"] = U32((uint)_s.State.PrimFlagsOf(p));
        prim["volume"] = new OSDMap
        {
            ["path"] = new OSDMap
            {
                ["curve"] = OSD.FromInteger((byte)d.PathCurve), ["begin"] = OSD.FromReal(d.PathBegin), ["end"] = OSD.FromReal(d.PathEnd),
                ["scale_x"] = OSD.FromReal(d.PathScaleX), ["scale_y"] = OSD.FromReal(d.PathScaleY), ["shear_x"] = OSD.FromReal(d.PathShearX),
                ["shear_y"] = OSD.FromReal(d.PathShearY), ["twist"] = OSD.FromReal(d.PathTwist), ["twist_begin"] = OSD.FromReal(d.PathTwistBegin),
                ["radius_offset"] = OSD.FromReal(d.PathRadiusOffset), ["taper_x"] = OSD.FromReal(d.PathTaperX), ["taper_y"] = OSD.FromReal(d.PathTaperY),
                ["revolutions"] = OSD.FromReal(d.PathRevolutions), ["skew"] = OSD.FromReal(d.PathSkew),
            },
            ["profile"] = new OSDMap
            {
                ["curve"] = OSD.FromInteger(d.profileCurve), ["begin"] = OSD.FromReal(d.ProfileBegin), ["end"] = OSD.FromReal(d.ProfileEnd),
                ["hollow"] = OSD.FromReal(d.ProfileHollow),
            },
        };
        prim["material"] = OSD.FromInteger((int)d.Material);
        prim["clickaction"] = OSD.FromInteger((int)p.ClickAction);
        if (p.Sculpt is { } sc && sc.SculptTexture != UUID.Zero)
            prim["sculpt"] = new OSDMap { ["texture"] = OSD.FromUUID(sc.SculptTexture), ["type"] = OSD.FromInteger((byte)sc.Type) };
        if (p.Flexible is { } fx && ((byte)d.PathCurve & 0xf0) == 0x80)
            prim["flexible"] = new OSDMap
            {
                ["air_friction"] = OSD.FromReal(fx.Drag), ["gravity"] = OSD.FromReal(fx.Gravity), ["simulate_lod"] = OSD.FromInteger(fx.Softness),
                ["tension"] = OSD.FromReal(fx.Tension), ["user_force"] = Vec(fx.Force), ["wind_sensitivity"] = OSD.FromReal(fx.Wind),
            };
        if (p.Light is { Intensity: > 0 } l)
            prim["light"] = new OSDMap
            {
                ["color"] = Col(new Color4(l.Color.R, l.Color.G, l.Color.B, l.Intensity)), ["radius"] = OSD.FromReal(l.Radius),
                ["falloff"] = OSD.FromReal(l.Falloff), ["cutoff"] = OSD.FromReal(l.Cutoff),
            };
        if (p.LightMap is { LightTexture: var lt } lm && lt != UUID.Zero)
            prim["light_texture"] = new OSDMap { ["texture"] = OSD.FromUUID(v.Mine.Contains(lt) ? lt : UUID.Zero), ["params"] = Vec(lm.Params) };
        var te = new OSDArray();
        if (p.Textures is { } tex)
            for (int i = 0; i < faces; i++)
            {
                var f = tex.GetFace((uint)i);
                te.Add(Te(f, v.Mine.Contains(f.TextureID) ? f.TextureID : Plywood));
            }
        prim["texture"] = te;
        if (p.PhysicsProps is { } ph)
            prim["ExtraPhysics"] = new OSDMap
            {
                ["PhysicsShapeType"] = OSD.FromInteger((int)ph.PhysicsShapeType), ["Density"] = OSD.FromReal(ph.Density),
                ["Friction"] = OSD.FromReal(ph.Friction), ["Restitution"] = OSD.FromReal(ph.Restitution), ["GravityMultiplier"] = OSD.FromReal(ph.GravityMultiplier),
            };
        prim["name"] = OSD.FromString(pr.Name ?? "");
        prim["description"] = OSD.FromString(pr.Description ?? "");
        prim["creation_date"] = OSD.FromBinary(Utils.UInt64ToBytesBig((ulong)Utils.DateTimeToUnixTime(pr.CreationDate) * 1_000_000UL));
        prim["creator_id"] = OSD.FromUUID(pr.CreatorID);
        prim["owner_id"] = OSD.FromUUID(pr.OwnerID);
        prim["group_id"] = OSD.FromUUID(pr.GroupID);
        prim["last_owner_id"] = OSD.FromUUID(pr.LastOwnerID);
        prim["base_mask"] = U32((uint)pr.Permissions.BaseMask);
        prim["owner_mask"] = U32((uint)pr.Permissions.OwnerMask);
        prim["group_mask"] = U32((uint)pr.Permissions.GroupMask);
        prim["everyone_mask"] = U32((uint)pr.Permissions.EveryoneMask);
        prim["next_owner_mask"] = U32((uint)pr.Permissions.NextOwnerMask);
        prim["sale_info"] = new OSDMap { ["sale_type"] = OSD.FromString(SaleWord(pr.SaleType)), ["sale_price"] = OSD.FromInteger(pr.SalePrice) };
        prim["touch_name"] = OSD.FromString(pr.TouchName ?? "");
        prim["sit_name"] = OSD.FromString(pr.SitName ?? "");
        return prim;
    }

    static string SaleWord(SaleType t) => t switch { SaleType.Original => "orig", SaleType.Copy => "copy", SaleType.Contents => "cntn", _ => "not" };
    static SaleType SaleFrom(string w) => w switch { "orig" => SaleType.Original, "copy" => SaleType.Copy, "cntn" => SaleType.Contents, _ => SaleType.Not };

    static string SafeName(string n)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(n.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return s.Length == 0 ? "object" : s.Length > 60 ? s[..60] : s;
    }

    public async Task<JsonObject> BackupAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var v = await JudgeAsync(a, ct);
        if (v.Refused.Count > 0) throw new LeapError("it can't be backed up: " + string.Join("; ", v.Refused.Take(5)) + (v.Refused.Count > 5 ? $" (and {v.Refused.Count - 5} more)" : ""));
        var prims = new OSDMap();
        var linkset = new OSDArray { OSD.FromUUID(v.Root.ID) };
        foreach (var p in v.Prims)
        {
            if (p.LocalID != v.Root.LocalID) linkset.Add(OSD.FromUUID(p.ID));
            prims[p.ID.ToString()] = PrimLlsd(p, v.Root, _s.Materials.FaceCount(p), v);
        }
        var assets = new OSDMap();
        var items = ItemsByAsset();
        var failed = new List<string>();
        foreach (var t in v.Mine.Where(t => t != UUID.Zero && t != Plywood))
        {
            var tex = await _s.World.TextureAsync(_s.GridKey, t, ct);
            if (tex?.AssetData is not { Length: > 0 } data) { failed.Add(t.ToString()); continue; }
            var it = items.TryGetValue(t, out var l) ? l[0] : null;
            assets[t.ToString()] = new OSDMap
            {
                ["name"] = OSD.FromString(it?.Name ?? ""), ["description"] = OSD.FromString(it?.Description ?? ""),
                ["type"] = OSD.FromString("texture"), ["data"] = OSD.FromBinary(data),
            };
        }
        var who = _s.Names.Legacy(c.Self.AgentID) ?? c.Self.Name;
        var manifest = new OSDMap
        {
            ["format_version"] = OSD.FromInteger(2), ["client"] = OSD.FromString("Sojourn"),
            ["creation_date"] = OSD.FromString(DateTime.UtcNow.ToString("yyyy-MM-dd")), ["author"] = OSD.FromString(who),
            ["grid"] = OSD.FromString(c.Network.CurrentSim?.Features?.Get("OpenSimExtras") is OSDMap ex && ex.ContainsKey("GridName") ? ex["GridName"].AsString() : _s.GridKey),
            ["linkset"] = new OSDArray { linkset }, ["prim"] = prims, ["inventory"] = new OSDMap(), ["asset"] = assets,
        };
        Directory.CreateDirectory(Dir);
        var name = SafeName(a.Str("name") ?? v.Root.Properties?.Name ?? "object");
        var path = Path.Combine(Dir, $"{name} {DateTime.Now:yyyy-MM-dd HHmmss}.oxp");
        await File.WriteAllBytesAsync(path, Helpers.ZCompressOSD(manifest), ct);
        var o = new JsonObject { ["path"] = path, ["prims"] = v.Prims.Count, ["textures"] = assets.Count };
        var left = new JsonArray();
        if (v.NotMine.Count > 0) left.Add($"{v.NotMine.Count} texture{(v.NotMine.Count == 1 ? "" : "s")} made by others (plywood instead)");
        if (failed.Count > 0) left.Add($"{failed.Count} texture{(failed.Count == 1 ? "" : "s")} that didn't download");
        if (v.Prims.Any(p => (_s.State.PrimFlagsOf(p) & PrimFlags.Scripted) != 0 || p.Properties?.InventorySerial > 0)) left.Add("the objects' contents (not kept in Sojourn's backups yet)");
        if (left.Count > 0) o["left_out"] = left;
        return o;
    }

    public JsonObject List()
    {
        var arr = new JsonArray();
        if (Directory.Exists(Dir))
            foreach (var f in new DirectoryInfo(Dir).GetFiles("*.oxp").OrderByDescending(f => f.LastWriteTimeUtc))
                arr.Add(new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(f.Name), ["path"] = f.FullName, ["size"] = f.Length, ["made"] = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") });
        return new JsonObject { ["backups"] = arr, ["folder"] = Dir };
    }

    // ---------- restoring ----------

    public string PathOf(JsonObject a)
    {
        if (a.Str("name") is { } n)
        {
            var p = Path.Combine(Dir, n.EndsWith(".oxp", StringComparison.OrdinalIgnoreCase) ? n : n + ".oxp");
            if (File.Exists(p)) return p;
            var hit = Directory.Exists(Dir) ? new DirectoryInfo(Dir).GetFiles("*.oxp").Where(f => f.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() : null;
            return hit?.FullName ?? throw new LeapError($"no backup called {n} (backups lists them)");
        }
        var path = a.Str("path") ?? throw new LeapError("need name (one of backups) or path");
        if (!File.Exists(path)) throw new LeapError($"no such file: {path}");
        return path;
    }

    public static OSDMap Read(string path)
    {
        try
        {
            var m = Helpers.ZDecompressOSD(File.ReadAllBytes(path)) as OSDMap ?? throw new LeapError("not a backup (.oxp) file");
            if (m["format_version"].AsInteger() > 2) throw new LeapError("a newer backup format than this reads (2)");
            return m;
        }
        catch (Exception e) when (e is not LeapError) { throw new LeapError($"can't read {Path.GetFileName(path)}: {e.Message}"); }
    }

    /// <summary>The textures a restore would upload: those this grid doesn't have (looked up by id), or all in the
    /// backup when it came from another grid.</summary>
    public async Task<List<UUID>> MissingAsync(OSDMap m, CancellationToken ct)
    {
        var assets = m["asset"] as OSDMap ?? new OSDMap();
        var missing = new List<UUID>();
        foreach (var id in assets.Keys)
        {
            if (assets[id] is not OSDMap am || am["type"].AsString() != "texture") continue;
            var uid = new UUID(id);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            AssetTexture? have = null;
            try { have = await _s.World.TextureAsync(_s.GridKey, uid, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            if (have?.AssetData is not { Length: > 0 }) missing.Add(uid);
        }
        return missing;
    }

    public async Task<JsonObject> RestoreAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var path = PathOf(a);
        var m = Read(path);
        var missing = await MissingAsync(m, ct);
        var each = c.Settings.UploadCost;
        var prims = m["prim"] as OSDMap ?? throw new LeapError("the backup has no prims");
        var sets = (m["linkset"] as OSDArray ?? new OSDArray()).OfType<OSDArray>().ToList();
        var o = new JsonObject
        {
            ["name"] = Path.GetFileNameWithoutExtension(path), ["prims"] = prims.Count, ["linksets"] = sets.Count,
            ["uploads"] = missing.Count, ["cost"] = missing.Count * each,
        };
        if (a.Bool("quote", false)) return o;
        // textures this grid lacks go up again (as Firestorm's import), and the prims use the new ids
        var remap = new Dictionary<UUID, UUID>();
        var assets = m["asset"] as OSDMap ?? new OSDMap();
        foreach (var id in missing)
        {
            var am = (OSDMap)assets[id.ToString()];
            var d = _s.Ownership.DefaultsFor("uploads");
            var perms = new Permissions((uint)PermissionMask.All, (uint)d.EveryoneMask, (uint)d.GroupMask, (uint)d.NextMask, (uint)PermissionMask.All);
            var name = am["name"].AsString() is { Length: > 0 } n ? n : $"restored texture {id.ToString()[..8]}";
            var r = await c.Inventory.CreateItemFromAssetAsync(am["data"].AsBinary(), name, am["description"].AsString(), AssetType.Texture,
                InventoryType.Texture, _s.Inventory.FolderFor(FolderType.Texture), perms, ct);
            if (!r.Success) throw new LeapError($"uploading a texture failed: {r.Status} ({remap.Count} uploaded before it)");
            remap[id] = r.AssetID;
        }
        UUID Map(UUID t) => remap.TryGetValue(t, out var n) ? n : t;
        var at = a.Vec3("pos") ?? (c.Self.SimPosition + Vector3.UnitX * c.Self.SimRotation * 2);
        var made = new JsonArray();
        using (_s.Undo.Batch("restored a backup"))
            foreach (var set in sets)
            {
                var ids = set.Select(x => x.AsUUID()).ToList();
                if (ids.Count == 0 || prims[ids[0].ToString()] is not OSDMap rootData) continue;
                var rootPos = Vector3.Zero;
                var madeIds = new List<UUID>();
                for (int i = 0; i < ids.Count; i++)
                {
                    if (prims[ids[i].ToString()] is not OSDMap pd) continue;
                    var local = pd["position"].AsVector3();
                    var rot = pd["rotation"].AsQuaternion();
                    var rootRot = rootData["rotation"].AsQuaternion();
                    // a child's place is its root's frame; the region's for the root
                    var pos = i == 0 ? at : at + local * rootRot;
                    var rr = i == 0 ? rot : rot * rootRot;
                    if (i == 0) rootPos = at;
                    var made1 = await _s.Build.CreateAsync(new JsonObject { ["pos"] = Json.Arr(pos), ["scale"] = Json.Arr(pd["scale"].AsVector3()), ["rot"] = Json.Arr(rr) }, ct);
                    var id = made1.Uuid("id")!.Value;
                    madeIds.Add(id);
                    var w = _s.Region!;
                    if (w.PrimById(id) is { } prim) Apply(c, prim, pd, Map);
                }
                if (madeIds.Count > 1) await _s.Build.LinkAsync(new JsonObject { ["ids"] = Json.Ids(madeIds) }, ct);
                made.Add(madeIds[0].ToString());
                _ = rootPos;
            }
        o["root_ids"] = made;
        o["uploaded"] = remap.Count;
        o["charged"] = remap.Count * each;
        return o;
    }

    /// <summary>A prim's shape, faces and settings from the backup's LLSD (Firestorm's keys), sent as the build tools do.</summary>
    void Apply(GridClient c, Primitive p, OSDMap pd, Func<UUID, UUID> map)
    {
        var sim = c.Network.CurrentSim!;
        var o = c.Objects;
        if (pd["volume"] is OSDMap vol && vol["path"] is OSDMap path && vol["profile"] is OSDMap prof)
        {
            var d = new Primitive.ConstructionData(p.PrimData)
            {
                PathCurve = (PathCurve)path["curve"].AsInteger(), PathBegin = (float)path["begin"].AsReal(), PathEnd = (float)path["end"].AsReal(),
                PathScaleX = (float)path["scale_x"].AsReal(), PathScaleY = (float)path["scale_y"].AsReal(),
                PathShearX = (float)path["shear_x"].AsReal(), PathShearY = (float)path["shear_y"].AsReal(),
                PathTwist = (float)path["twist"].AsReal(), PathTwistBegin = (float)path["twist_begin"].AsReal(),
                PathRadiusOffset = (float)path["radius_offset"].AsReal(), PathTaperX = (float)path["taper_x"].AsReal(), PathTaperY = (float)path["taper_y"].AsReal(),
                PathRevolutions = (float)path["revolutions"].AsReal(), PathSkew = (float)path["skew"].AsReal(),
                profileCurve = (byte)prof["curve"].AsInteger(), ProfileBegin = (float)prof["begin"].AsReal(), ProfileEnd = (float)prof["end"].AsReal(),
                ProfileHollow = (float)prof["hollow"].AsReal(),
            };
            o.SetShape(sim, p.LocalID, d);
        }
        if (pd.ContainsKey("material")) o.SetMaterial(sim, p.LocalID, (Material)pd["material"].AsInteger());
        if (pd["texture"] is OSDArray tes && tes.Count > 0)
        {
            Primitive.TextureEntryFace Face(OSDMap f, Primitive.TextureEntryFace into)
            {
                into.TextureID = map(f["imageid"].AsUUID());
                into.RGBA = f["colors"].AsColor4();
                into.RepeatU = (float)f["scales"].AsReal(); into.RepeatV = (float)f["scalet"].AsReal();
                into.OffsetU = (float)f["offsets"].AsReal(); into.OffsetV = (float)f["offsett"].AsReal();
                into.Rotation = (float)f["imagerot"].AsReal();
                var bump = (byte)f["bump"].AsInteger();
                into.Bump = (Bumpiness)(bump & 0x1f); into.Shiny = (Shininess)(bump & 0xc0);
                into.Fullbright = f["fullbright"].AsInteger() != 0;
                var mf = f["media_flags"].AsInteger();
                into.MediaFlags = (mf & 1) != 0; into.TexMapType = (mf & 2) != 0 ? MappingType.Planar : MappingType.Default;
                into.Glow = (float)f["glow"].AsReal();
                return into;
            }
            var te = new Primitive.TextureEntry(map(((OSDMap)tes[0])["imageid"].AsUUID()));
            Face((OSDMap)tes[0], te.DefaultTexture!);
            for (int i = 0; i < tes.Count; i++) Face((OSDMap)tes[i], te.CreateFace((uint)i));
            o.SetTextures(sim, p.LocalID, te);
        }
        if (pd["sculpt"] is OSDMap sc && sc["texture"].AsUUID() != UUID.Zero)
            o.SetSculpt(sim, p.LocalID, new Primitive.SculptData { SculptTexture = map(sc["texture"].AsUUID()), Type = (SculptType)sc["type"].AsInteger() });
        if (pd["flexible"] is OSDMap fx)
            o.SetFlexible(sim, p.LocalID, new Primitive.FlexibleData
            {
                Drag = (float)fx["air_friction"].AsReal(), Gravity = (float)fx["gravity"].AsReal(), Softness = fx["simulate_lod"].AsInteger(),
                Tension = (float)fx["tension"].AsReal(), Force = fx["user_force"].AsVector3(), Wind = (float)fx["wind_sensitivity"].AsReal(),
            });
        if (pd["light"] is OSDMap li)
        {
            var col = li["color"].AsColor4();
            o.SetLight(sim, p.LocalID, new Primitive.LightData
            {
                Color = new Color4(col.R, col.G, col.B, 1), Intensity = col.A, Radius = (float)li["radius"].AsReal(),
                Falloff = (float)li["falloff"].AsReal(), Cutoff = (float)li["cutoff"].AsReal(),
            });
        }
        if (pd["light_texture"] is OSDMap lt && lt["texture"].AsUUID() != UUID.Zero)
            BuildOps.SendProjector(c, sim, p.LocalID, new Primitive.LightImage { LightTexture = map(lt["texture"].AsUUID()), Params = lt["params"].AsVector3() });
        if (pd.ContainsKey("clickaction"))
            c.Network.SendPacket(new LibreMetaverse.Packets.ObjectClickActionPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                ObjectData = new[] { new LibreMetaverse.Packets.ObjectClickActionPacket.ObjectDataBlock { ObjectLocalID = p.LocalID, ClickAction = (byte)pd["clickaction"].AsInteger() } },
            }, sim);
        if (pd.ContainsKey("flags"))
        {
            var fl = (PrimFlags)U32Of(pd["flags"].AsBinary());
            // never temporary: a restored build that deletes itself within a minute would be no restore
            BuildOps.SendFlags(c, sim, p.LocalID, (fl & PrimFlags.Physics) != 0, false, (fl & PrimFlags.Phantom) != 0, (fl & PrimFlags.CastShadows) != 0);
        }
        if (pd["name"].AsString() is { Length: > 0 } name) o.SetName(sim, p.LocalID, name);
        if (pd["description"].AsString() is { Length: > 0 } desc) o.SetDescription(sim, p.LocalID, desc);
        if (pd["sale_info"] is OSDMap sale && SaleFrom(sale["sale_type"].AsString()) is var st && st != SaleType.Not)
            _s.Log.LogDebug("restore: {Name} was for sale ({Type}); left not for sale", pd["name"].AsString(), st);
    }
}
