using System.Text;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.ImportExport;
using LibreMetaverse.Imaging.Skia;
using LibreMetaverse.Materials;
using LibreMetaverse.StructuredData;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Materials on faces, as the edit floater's Texture tab has them: PBR (glTF) materials applied from inventory, their
/// per-face overrides (base colour, metallic, roughness, emissive, alpha, double sided, each map and its transform),
/// and taken off (the ModifyMaterialParams capability, LLGLTFMaterialList); the legacy (Blinn-Phong) normal and
/// specular maps and diffuse alpha mode (the RenderMaterials capability, LLMaterialMgr::put); a face's materials read
/// back; and Upload > Material (a glTF file's material, its textures uploaded first). Through TesselBuild modify's keys
/// pbr, normal, specular and alpha_mode, so `build` takes them per part. Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class MaterialOps
{
    readonly AgentSession _s;
    public MaterialOps(AgentSession s) => _s = s;

    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    /// <summary>How many faces a prim has (the viewer's getNumTEs): set by the client from the renderer's volumes.</summary>
    public static Func<Primitive, int?>? FacesOf { get; set; }

    readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, int> _meshFaces = new();

    static bool IsMesh(Primitive p) => p.Sculpt is { Type: SculptType.Mesh } sc && sc.SculptTexture != UUID.Zero;

    /// <summary>A mesh has as many faces as its asset's high LOD has submeshes (up to 8); until that's been read, 8.</summary>
    public int FaceCount(Primitive p)
    {
        if (IsMesh(p)) return _meshFaces.TryGetValue(p.Sculpt!.SculptTexture, out var mf) ? mf : 8;
        if (FacesOf?.Invoke(p) is { } n && n > 0) return Math.Min(n, 32);
        // without the renderer: what the texture entry has faces for (at least the default)
        var te = p.Textures;
        int last = -1;
        if (te is not null) for (int i = 0; i < te.FaceTextures.Length; i++) if (te.FaceTextures[i] is not null) last = i;
        return Math.Max(1, last + 1);
    }

    public async Task<int> FaceCountAsync(Primitive p, CancellationToken ct)
    {
        if (!IsMesh(p)) return FaceCount(p);
        var id = p.Sculpt!.SculptTexture;
        if (_meshFaces.TryGetValue(id, out var have)) return have;
        try
        {
            var mesh = await _s.World.MeshAsync(_s.GridKey, id, ct);
            if (mesh is not null && mesh.MeshData.Count == 0) mesh.Decode();
            foreach (var lod in new[] { "high_lod", "medium_lod", "low_lod", "lowest_lod" })
                if (mesh?.MeshData.ContainsKey(lod) == true && mesh.MeshData[lod] is OSDArray subs && subs.Count > 0)
                    return _meshFaces[id] = Math.Min(subs.Count, 8);
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        return 8;
    }

    /// <summary>What a modify call asks of the materials, worked out before anything is sent.</summary>
    public sealed class MaterialPlan
    {
        public required int[] Faces { get; set; }
        /// <summary>Every face (not one asked for): a mesh's are counted from its asset before sending.</summary>
        public bool All;
        public List<PbrUpdate>? Pbr;
        /// <summary>The face's legacy material as it is (null: none) and its colour's alpha, to the one it gets (null: none).</summary>
        public Func<LegacyMaterial?, float, LegacyMaterial?>? Legacy;
    }

    static readonly string[] PbrKeys =
    {
        "material", "base_color", "base_color_texture", "metallic", "roughness", "metallic_roughness_texture", "normal_texture",
        "emissive", "emissive_texture", "alpha_mode", "alpha_cutoff", "double_sided", "repeats", "offset", "rotation", "reset",
    };

    static readonly string[] MapKeys = { "texture", "repeats", "offset", "rotation" };
    static readonly string[] SpecularKeys = { "texture", "repeats", "offset", "rotation", "color", "glossiness", "environment" };

    public MaterialPlan? Plan(Primitive p, JsonObject a)
    {
        if (!a.Has("pbr") && !a.Has("normal") && !a.Has("specular") && !a.Has("alpha_mode") && !a.Has("alpha_cutoff")) return null;
        int[] faces = a.Num("face") is { } f ? new[] { (int)f } : Enumerable.Range(0, FaceCount(p)).ToArray();
        var plan = new MaterialPlan { Faces = faces, All = !a.Has("face") };
        if (a.Has("pbr")) plan.Pbr = PbrPlan(p, a["pbr"], faces);
        if (a.Has("normal") || a.Has("specular") || a.Has("alpha_mode") || a.Has("alpha_cutoff")) plan.Legacy = LegacyPlan(a);
        return plan;
    }

    // ---------- PBR ----------

    /// <summary>One ModifyMaterialParams entry: a face's material (asset; null: keep it, modify only its override; zero:
    /// take it off) and its override as glTF JSON ("" clears it), as LLGLTFMaterialList's queueApply and queueModify.</summary>
    public sealed record PbrUpdate(UUID Object, int Side, UUID? Asset, string Json);

    List<PbrUpdate> PbrPlan(Primitive p, JsonNode? v, int[] faces)
    {
        var updates = new List<PbrUpdate>();
        if (v is JsonValue bv && (bv.TryGetValue<bool>(out var on) && !on || bv.TryGetValue<string>(out var s) && s.Trim().ToLowerInvariant() is "none" or "off"))
        {
            // taken off: the null material, as the viewer's Remove (blank) does; its overrides go with it
            foreach (var f in faces) updates.Add(new PbrUpdate(p.ID, f, UUID.Zero, ""));
            return updates;
        }
        JsonObject o = v is JsonValue sv && sv.TryGetValue<string>(out _) ? new JsonObject { ["material"] = v.DeepClone() }
            : v as JsonObject ?? throw new LeapError("pbr: a material (an inventory item or asset id), {material?, base_color?, metallic?, roughness?, "
                + "emissive?, alpha_mode?, alpha_cutoff?, double_sided?, base_color_texture?, normal_texture?, metallic_roughness_texture?, "
                + "emissive_texture?, repeats?, offset?, rotation?, reset?}, or false");
        foreach (var (k, _) in o)
            if (!PbrKeys.Contains(k)) throw new LeapError($"pbr: no key {k} ({string.Join(", ", PbrKeys)})");
        UUID? asset = o.Has("material") ? MaterialAsset(o.Str("material")) : null;
        var current = Overrides(p);
        var have = BuildUndo.RenderMaterialsOf?.Invoke(p.RegionHandle, p.LocalID) ?? Array.Empty<UUID>();
        bool anyOverride = o.Select(kv => kv.Key).Any(k => k is not ("material" or "reset"));
        if (asset is null && !anyOverride && !o.Bool("reset", false)) throw new LeapError("pbr: nothing to change (give a material, overrides, or reset: true)");
        foreach (var f in faces)
        {
            if (asset is null && (f >= have.Length || have[f] == UUID.Zero) && anyOverride)
                throw new LeapError($"face {f} has no PBR material to override: give pbr.material too");
            // a new material starts its overrides afresh but keeps their texture transforms (queueApply's setBaseMaterial);
            // otherwise changes go onto the face's override as it is (the live material editor's way)
            AssetMaterial ov;
            if (o.Bool("reset", false)) ov = new AssetMaterial();
            else if (current.TryGetValue(f, out var had))
            {
                ov = OverrideFromJson(had.ToJson());
                if (asset is not null) ov.SetBaseMaterial();
            }
            else ov = new AssetMaterial();
            if (anyOverride) ApplyOverrides(ov, o);
            updates.Add(new PbrUpdate(p.ID, f, asset, IsEmpty(ov) ? "" : ov.ToJson()));
        }
        return updates;
    }

    Dictionary<int, AssetMaterial> Overrides(Primitive p)
    {
        if (_s.Client?.Network.Simulators.FirstOrDefault(x => x.Handle == p.RegionHandle) is { } sim
            && sim.GLTFMaterialOverrides.TryGetValue(p.LocalID, out var e))
            return e.FaceOverrides.ToDictionary(kv => kv.Key, kv => kv.Value);
        return new Dictionary<int, AssetMaterial>();
    }

    /// <summary>An override that changes nothing (sent as "", which clears the face's).</summary>
    static bool IsEmpty(AssetMaterial m)
    {
        var d = AssetMaterial.Default;
        for (int i = 0; i < AssetMaterial.TEXTURE_COUNT; i++)
            if (m.TextureIds[i] != UUID.Zero || !m.TextureTransforms[i].IsDefault) return false;
        return m.BaseColorFactor == d.BaseColorFactor && m.EmissiveFactor == d.EmissiveFactor && m.MetallicFactor == d.MetallicFactor
            && m.RoughnessFactor == d.RoughnessFactor && m.AlphaCutoff == d.AlphaCutoff && m.AlphaMode == d.AlphaMode && m.DoubleSided == d.DoubleSided
            && !m.OverrideAlphaMode && !m.OverrideDoubleSided;
    }

    /// <summary>ModifyMaterialParams: an LLSD array of {object_id, side, asset_id?, gltf_json}, at most 255 a request; the
    /// region answers {success, message}.</summary>
    public async Task SendPbrAsync(GridClient c, Simulator sim, IReadOnlyList<PbrUpdate> updates, CancellationToken ct)
    {
        var cap = sim.Caps?.CapabilityURI("ModifyMaterialParams") ?? throw new LeapError("this region has no ModifyMaterialParams capability (PBR materials)");
        for (int at = 0; at < updates.Count; at += 255)
        {
            var arr = new OSDArray();
            foreach (var u in updates.Skip(at).Take(255))
            {
                var m = new OSDMap { ["object_id"] = OSD.FromUUID(u.Object), ["side"] = OSD.FromInteger(u.Side), ["gltf_json"] = OSD.FromString(u.Json) };
                if (u.Asset is { } id) m["asset_id"] = OSD.FromUUID(id);
                arr.Add(m);
            }
            var (resp, body) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, arr, ct);
            if (!resp.IsSuccessStatusCode) throw new LeapError($"the region refused the PBR material ({(int)resp.StatusCode})");
            if (body is { Length: > 0 } && OSDParser.Deserialize(body) is OSDMap r && r.ContainsKey("success") && !r["success"].AsBoolean())
                throw new LeapError($"the region refused the PBR material: {r["message"].AsString()}");
        }
    }

    public static AssetMaterial OverrideFromJson(string json)
    {
        var m = new AssetMaterial(UUID.Zero, Encoding.UTF8.GetBytes(json));
        m.Decode();
        return m;
    }

    /// <summary>A material item of mine (or an asset id) to its asset id, as the viewer's material picker.</summary>
    UUID MaterialAsset(string? raw)
    {
        if (!UUID.TryParse(raw ?? "", out var id)) throw new LeapError($"pbr material: {raw} isn't an id (give a material's inventory item id or asset id)");
        if (_s.Inventory.Node(id) is InventoryItem item)
        {
            if (item.AssetType == AssetType.Link && _s.Inventory.Node(item.AssetUUID) is InventoryItem t) item = t;
            if (item.AssetType != AssetType.Material) throw new LeapError($"pbr material: {item.Name} is a {AgentInventory.AssetTypeLabel(item.AssetType)}, not a material");
            if (item.AssetUUID == UUID.Zero) throw new LeapError($"pbr material: {item.Name} hides its asset (no-modify materials can't be applied by id)");
            return item.AssetUUID;
        }
        return id;
    }

    void ApplyOverrides(AssetMaterial m, JsonObject o)
    {
        if (o.Nums("base_color") is { Length: >= 3 } bc)
            m.SetBaseColorFactor(new Color4((float)bc[0], (float)bc[1], (float)bc[2], bc.Length > 3 ? (float)bc[3] : 1), true);
        if (o.Num("metallic") is { } me) m.SetMetallicFactor((float)Math.Clamp(me, 0, 1), true);
        if (o.Num("roughness") is { } ro) m.SetRoughnessFactor((float)Math.Clamp(ro, 0, 1), true);
        if (o.Nums("emissive") is { Length: >= 3 } em) m.SetEmissiveFactor(new Vector3((float)em[0], (float)em[1], (float)em[2]), true);
        if (o.Str("alpha_mode") is { } am)
            m.SetAlphaMode(am.Trim().ToLowerInvariant() switch
            {
                "opaque" or "none" => GltfAlphaMode.Opaque, "blend" => GltfAlphaMode.Blend, "mask" => GltfAlphaMode.Mask,
                _ => throw new LeapError("pbr alpha_mode: opaque, blend or mask"),
            }, true);
        if (o.Num("alpha_cutoff") is { } ac) m.SetAlphaCutoff((float)ac, true);
        if (o.Bool("double_sided") is { } ds) m.SetDoubleSided(ds, true);
        void Tex(string key, int slot)
        {
            if (!o.Has(key)) return;
            if (o[key] is JsonValue fv && fv.TryGetValue<bool>(out var b) && !b) { m.SetTextureId(slot, UUID.Zero, true); return; }
            m.SetTextureId(slot, TextureAssetOf(o.Str(key), $"pbr {key}"), true);
        }
        Tex("base_color_texture", AssetMaterial.TEXTURE_BASE_COLOR);
        Tex("normal_texture", AssetMaterial.TEXTURE_NORMAL);
        Tex("metallic_roughness_texture", AssetMaterial.TEXTURE_METALLIC_ROUGHNESS);
        Tex("emissive_texture", AssetMaterial.TEXTURE_EMISSIVE);
        // one transform for every map, as the Texture tab's PBR transforms set "all maps" by default
        for (int slot = 0; slot < AssetMaterial.TEXTURE_COUNT; slot++)
        {
            if (o.Nums("repeats") is { Length: >= 1 } rep) m.SetTextureScale(slot, new Vector2((float)rep[0], (float)(rep.Length > 1 ? rep[1] : rep[0])));
            if (o.Nums("offset") is { Length: >= 1 } off) m.SetTextureOffset(slot, new Vector2((float)off[0], (float)(off.Length > 1 ? off[1] : off[0])));
            if (o.Num("rotation") is { } rot) m.SetTextureRotation(slot, (float)(rot * Math.PI / 180));
        }
    }

    /// <summary>As TesselBuild's `texture`: an asset id, a texture item of mine, or blank, plywood or transparent.</summary>
    UUID TextureAssetOf(string? raw, string key) => _s.Build.TextureAsset(raw, key);

    // ---------- legacy materials (normal and specular maps) ----------

    /// <summary>{texture | false, repeats?, offset?, rotation? (degrees)} for normal; specular also color, glossiness (0-255, the
    /// viewer's exponent), environment (0-255); alpha_mode: none, blend, mask, emissive (and alpha_cutoff 0-255 with mask).</summary>
    Func<LegacyMaterial?, float, LegacyMaterial?> LegacyPlan(JsonObject a)
    {
        JsonObject? Map(string key, string[] allowed)
        {
            if (!a.Has(key)) return null;
            if (a[key] is JsonValue v && v.TryGetValue<bool>(out var on) && !on) return new JsonObject { ["texture"] = false };
            if (a[key] is not JsonObject o) throw new LeapError($"{key}: give {{{string.Join(", ", allowed)}}} or false");
            foreach (var (k, _) in o) if (!allowed.Contains(k)) throw new LeapError($"{key}: no key {k} ({string.Join(", ", allowed)})");
            return o;
        }
        var normal = Map("normal", MapKeys);
        var spec = Map("specular", SpecularKeys);
        UUID? Tex(JsonObject? o, string key) => o is null || !o.Has("texture") ? null
            : o["texture"] is JsonValue fv && fv.TryGetValue<bool>(out var b) && !b ? UUID.Zero : TextureAssetOf(o.Str("texture"), key);
        var nTex = Tex(normal, "normal texture");
        var sTex = Tex(spec, "specular texture");
        LegacyMaterialAlphaMode? mode = a.Str("alpha_mode") is { } am ? am.Trim().ToLowerInvariant() switch
        {
            "none" or "opaque" => LegacyMaterialAlphaMode.None, "blend" or "blending" => LegacyMaterialAlphaMode.Blend,
            "mask" or "masking" => LegacyMaterialAlphaMode.Mask, "emissive" => LegacyMaterialAlphaMode.Emissive,
            _ => throw new LeapError("alpha_mode: none, blend, mask or emissive"),
        } : null;
        if (a.Has("alpha_mode") && a.Has("pbr")) throw new LeapError("alpha_mode is the legacy material's; a PBR material's is pbr.alpha_mode");
        return (cur, faceAlpha) =>
        {
            var m = cur is null ? Fresh(faceAlpha) : Copy(cur);
            void Transform(JsonObject? o, Action<double, double> rep, Action<double, double> off, Action<double> rot)
            {
                if (o is null) return;
                if (o.Nums("repeats") is { Length: >= 1 } r) rep(r[0], r.Length > 1 ? r[1] : r[0]);
                if (o.Nums("offset") is { Length: >= 1 } of) off(of[0], of.Length > 1 ? of[1] : of[0]);
                if (o.Num("rotation") is { } ro) rot(ro * Math.PI / 180);
            }
            if (nTex is { } n) m.NormalMap = n;
            Transform(normal, (x, y) => { m.NormalMapRepeatX = x; m.NormalMapRepeatY = y; }, (x, y) => { m.NormalMapOffsetX = x; m.NormalMapOffsetY = y; }, r => m.NormalMapRotation = r);
            if (sTex is { } sp) m.SpecularMap = sp;
            Transform(spec, (x, y) => { m.SpecularMapRepeatX = x; m.SpecularMapRepeatY = y; }, (x, y) => { m.SpecularMapOffsetX = x; m.SpecularMapOffsetY = y; }, r => m.SpecularMapRotation = r);
            if (spec?.Nums("color") is { Length: >= 3 } col)
                m.SpecularColor = new Color4((float)col[0], (float)col[1], (float)col[2], col.Length > 3 ? (float)col[3] : 1);
            if (spec?.Num("glossiness") is { } g) m.SpecularExponent = (byte)Math.Clamp(Math.Round(g), 0, 255);
            if (spec?.Num("environment") is { } env) m.EnvironmentIntensity = (byte)Math.Clamp(Math.Round(env), 0, 255);
            if (mode is { } md) m.DiffuseAlphaMode = md;
            if (a.Num("alpha_cutoff") is { } cut) m.AlphaMaskCutoff = (byte)Math.Clamp(Math.Round(cut), 0, 255);
            // nothing left that a legacy material carries: take it off the face, as the viewer does
            bool empty = m.NormalMap == UUID.Zero && m.SpecularMap == UUID.Zero
                && (m.DiffuseAlphaMode == LegacyMaterialAlphaMode.None && faceAlpha >= 0.999f
                    || m.DiffuseAlphaMode is LegacyMaterialAlphaMode.Blend or LegacyMaterialAlphaMode.Default && faceAlpha < 0.999f);
            return empty && mode is null ? null : m;
        };
    }

    /// <summary>LLMaterial's defaults (repeats 1, specular white at exponent 51, no environment), but the alpha mode the
    /// Texture tab would show for the face: blending where it's see-through, none where it's opaque (LLMaterial's own
    /// default, blend, would send an opaque face to the alpha pass; the panel goes by the texture's alpha, which isn't
    /// known here, so the face's colour alpha stands in).</summary>
    static LegacyMaterial Fresh(float faceAlpha) => new()
    {
        NormalMapRepeatX = 1, NormalMapRepeatY = 1, SpecularMapRepeatX = 1, SpecularMapRepeatY = 1,
        SpecularColor = Color4.White, SpecularExponent = 51, EnvironmentIntensity = 0, AlphaMaskCutoff = 0,
        DiffuseAlphaMode = faceAlpha < 0.999f ? LegacyMaterialAlphaMode.Blend : LegacyMaterialAlphaMode.None,
    };

    static LegacyMaterial Copy(LegacyMaterial c) => new()
    {
        NormalMap = c.NormalMap, NormalMapOffsetX = c.NormalMapOffsetX, NormalMapOffsetY = c.NormalMapOffsetY,
        NormalMapRepeatX = c.NormalMapRepeatX, NormalMapRepeatY = c.NormalMapRepeatY, NormalMapRotation = c.NormalMapRotation,
        SpecularMap = c.SpecularMap, SpecularMapOffsetX = c.SpecularMapOffsetX, SpecularMapOffsetY = c.SpecularMapOffsetY,
        SpecularMapRepeatX = c.SpecularMapRepeatX, SpecularMapRepeatY = c.SpecularMapRepeatY, SpecularMapRotation = c.SpecularMapRotation,
        SpecularColor = c.SpecularColor, SpecularExponent = c.SpecularExponent, EnvironmentIntensity = c.EnvironmentIntensity,
        AlphaMaskCutoff = c.AlphaMaskCutoff, DiffuseAlphaMode = c.DiffuseAlphaMode,
    };

    /// <summary>LLMaterial::asLLSD: offsets, repeats and rotation as integers x10000, the colour as four bytes.</summary>
    static OSDMap Llsd(LegacyMaterial m)
    {
        static OSD I(double v) => OSD.FromInteger((int)Math.Round(v * 10000));
        var c = m.SpecularColor;
        return new OSDMap
        {
            ["NormMap"] = OSD.FromUUID(m.NormalMap), ["NormOffsetX"] = I(m.NormalMapOffsetX), ["NormOffsetY"] = I(m.NormalMapOffsetY),
            ["NormRepeatX"] = I(m.NormalMapRepeatX), ["NormRepeatY"] = I(m.NormalMapRepeatY), ["NormRotation"] = I(m.NormalMapRotation),
            ["SpecMap"] = OSD.FromUUID(m.SpecularMap), ["SpecOffsetX"] = I(m.SpecularMapOffsetX), ["SpecOffsetY"] = I(m.SpecularMapOffsetY),
            ["SpecRepeatX"] = I(m.SpecularMapRepeatX), ["SpecRepeatY"] = I(m.SpecularMapRepeatY), ["SpecRotation"] = I(m.SpecularMapRotation),
            ["SpecColor"] = new OSDArray(new List<OSD>
            {
                OSD.FromInteger((int)Math.Round(c.R * 255)), OSD.FromInteger((int)Math.Round(c.G * 255)),
                OSD.FromInteger((int)Math.Round(c.B * 255)), OSD.FromInteger((int)Math.Round(c.A * 255)),
            }),
            ["SpecExp"] = OSD.FromInteger(m.SpecularExponent), ["EnvIntensity"] = OSD.FromInteger(m.EnvironmentIntensity),
            ["AlphaMaskCutoff"] = OSD.FromInteger(m.AlphaMaskCutoff), ["DiffuseAlphaMode"] = OSD.FromInteger((int)m.DiffuseAlphaMode),
        };
    }

    // ---------- sending ----------

    public async Task SendAsync(Primitive p, MaterialPlan plan, CancellationToken ct)
    {
        var c = C;
        var sim = c.Network.CurrentSim ?? throw new LeapError("no region");
        if (plan.All && IsMesh(p))
        {
            var n = await FaceCountAsync(p, ct);
            plan.Faces = plan.Faces.Where(f => f < n).ToArray();
            plan.Pbr = plan.Pbr?.Where(u => u.Side < n).ToList();
        }
        if (plan.Legacy is { } legacy)
        {
            var cap = sim.Caps?.CapabilityURI("RenderMaterials") ?? throw new LeapError("this region has no RenderMaterials capability (legacy materials)");
            var current = await LegacyOfAsync(c, sim, p, plan.Faces, ct);
            var faces = new OSDArray();
            foreach (var f in plan.Faces)
            {
                var face = new OSDMap { ["Face"] = OSD.FromInteger(f), ["ID"] = OSD.FromInteger((int)p.LocalID) };
                var alpha = p.Textures?.GetFace((uint)f)?.RGBA.A ?? 1f;
                if (legacy(current.TryGetValue(f, out var had) ? had : null, alpha) is { } m) face["Material"] = Llsd(m);
                faces.Add(face);
            }
            var body = new OSDMap { ["Zipped"] = OSD.FromBinary(Helpers.ZCompressOSD(new OSDMap { ["FullMaterialsPerFace"] = faces })) };
            var (resp, _) = await c.HttpCapsClient.PutAsync(cap, OSDFormat.Xml, body, ct);
            if (!resp.IsSuccessStatusCode) throw new LeapError($"the region refused the material ({(int)resp.StatusCode})");
        }
        if (plan.Pbr is { Count: > 0 } pbr) await SendPbrAsync(c, sim, pbr, ct);
    }

    /// <summary>The faces' legacy materials as they are (by the material ids in the texture entry).</summary>
    async Task<Dictionary<int, LegacyMaterial>> LegacyOfAsync(GridClient c, Simulator sim, Primitive p, int[] faces, CancellationToken ct)
    {
        var ids = new Dictionary<int, UUID>();
        foreach (var f in faces)
            if (p.Textures?.GetFace((uint)f) is { MaterialID: var mid } && mid != UUID.Zero) ids[f] = mid;
        if (ids.Count == 0) return new();
        var got = (await c.Objects.RequestMaterialsAsync(sim, ids.Values.Distinct(), ct)).ToDictionary(m => m.ID);
        return ids.Where(kv => got.ContainsKey(kv.Value)).ToDictionary(kv => kv.Key, kv => got[kv.Value]);
    }

    // ---------- reading ----------

    public async Task<JsonObject> ReadAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var w = _s.Region ?? throw new LeapError("no region");
        var p = w.PrimById(a.Uuid("id") ?? throw new LeapError("need id")) ?? throw new LeapError("object not found in this region");
        var sim = c.Network.CurrentSim!;
        int[] faces = a.Num("face") is { } f ? new[] { (int)f } : Enumerable.Range(0, await FaceCountAsync(p, ct)).ToArray();
        var pbr = BuildUndo.RenderMaterialsOf?.Invoke(w.Handle, p.LocalID) ?? Array.Empty<UUID>();
        var ov = Overrides(p);
        Dictionary<int, LegacyMaterial> legacy = new();
        try { legacy = await LegacyOfAsync(c, sim, p, faces, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { }
        var arr = new JsonArray();
        foreach (var face in faces)
        {
            var o = new JsonObject { ["face"] = face };
            if (face < pbr.Length && pbr[face] != UUID.Zero)
            {
                var po = new JsonObject { ["material_id"] = pbr[face].ToString() };
                if (ov.TryGetValue(face, out var m)) po["overrides"] = JsonNode.Parse(m.ToJson());
                o["pbr"] = po;
            }
            if (legacy.TryGetValue(face, out var lm))
                o["legacy"] = new JsonObject
                {
                    ["material_id"] = lm.ID.ToString(),
                    ["normal"] = lm.NormalMap == UUID.Zero ? null : new JsonObject
                    {
                        ["texture"] = lm.NormalMap.ToString(), ["repeats"] = new JsonArray(Json.R(lm.NormalMapRepeatX, 3), Json.R(lm.NormalMapRepeatY, 3)),
                        ["offset"] = new JsonArray(Json.R(lm.NormalMapOffsetX, 3), Json.R(lm.NormalMapOffsetY, 3)), ["rotation"] = Json.R(lm.NormalMapRotation * 180 / Math.PI, 2),
                    },
                    ["specular"] = lm.SpecularMap == UUID.Zero && lm.SpecularExponent == 0 ? null : new JsonObject
                    {
                        ["texture"] = lm.SpecularMap.ToString(), ["repeats"] = new JsonArray(Json.R(lm.SpecularMapRepeatX, 3), Json.R(lm.SpecularMapRepeatY, 3)),
                        ["offset"] = new JsonArray(Json.R(lm.SpecularMapOffsetX, 3), Json.R(lm.SpecularMapOffsetY, 3)), ["rotation"] = Json.R(lm.SpecularMapRotation * 180 / Math.PI, 2),
                        ["color"] = Json.Arr(lm.SpecularColor), ["glossiness"] = lm.SpecularExponent, ["environment"] = lm.EnvironmentIntensity,
                    },
                    ["alpha_mode"] = lm.DiffuseAlphaMode.ToString().ToLowerInvariant(), ["alpha_cutoff"] = lm.AlphaMaskCutoff,
                };
            arr.Add(o);
        }
        var r = new JsonObject { ["id"] = p.ID.ToString(), ["faces"] = arr };
        if (p.LightMap is { LightTexture: var lt } li && lt != UUID.Zero)
            r["projector"] = new JsonObject
            {
                ["texture"] = lt.ToString(), ["fov"] = Json.R(li.Params.X * 180 / Math.PI, 2), ["focus"] = Json.R(li.Params.Y, 3), ["ambiance"] = Json.R(li.Params.Z, 3),
            };
        return r;
    }

    // ---------- Upload > Material ----------

    public async Task<JsonObject> UploadAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var path = a.Str("path") ?? throw new LeapError("need path (a .gltf or .glb)");
        if (!File.Exists(path)) throw new LeapError($"no such file: {path}");
        if (Path.GetExtension(path).ToLowerInvariant() is not (".gltf" or ".glb")) throw new LeapError("uploadMaterial: give a .gltf or .glb");
        var loader = new GltfLoader(new SkiaTextureCodec()) { LoadPbrTextures = true };
        var loaded = loader.LoadWithDiagnostics(path, true);
        var mats = loaded.Prims.SelectMany(pr => pr.Faces).Select(f => f.Material?.Pbr).OfType<ModelPbrMaterial>().Distinct().ToList();
        if (mats.Count == 0)
            throw new LeapError("no glTF material in that file" + (loaded.HasErrors ? ": " + string.Join("; ", loaded.Diagnostics.Select(d => d.ToString()).Take(3)) : ""));
        ModelPbrMaterial pick;
        if (a.Num("material") is { } idx) pick = idx >= 0 && idx < mats.Count ? mats[(int)idx] : throw new LeapError($"the file has {mats.Count} materials (0-{mats.Count - 1})");
        else if (a.Str("material") is { } name)
            pick = mats.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new LeapError($"no material {name} in the file: {string.Join(", ", mats.Select(m => m.Name))}");
        else pick = mats[0];
        var textures = new[] { pick.BaseColorTexture, pick.NormalTexture, pick.MetallicRoughnessTexture, pick.EmissiveTexture }
            .Where(t => t is { Data.Length: > 0 }).Distinct().Count();
        var each = c.Settings.UploadCost;
        var o = new JsonObject
        {
            ["name"] = a.Str("name") ?? (string.IsNullOrEmpty(pick.Name) ? Path.GetFileNameWithoutExtension(path) : pick.Name),
            ["materials_in_file"] = Json.Strings(mats.Select(m => m.Name)), ["textures"] = textures, ["cost"] = textures * each,
        };
        if (a.Bool("quote", false)) return o;
        var d = _s.Ownership.DefaultsFor("materials");
        var perms = new Permissions((uint)PermissionMask.All, (uint)d.EveryoneMask, (uint)d.GroupMask, (uint)d.NextMask, (uint)PermissionMask.All);
        var r = await c.Inventory.CreateMaterialAsync(pick, perms, o.Str("name"), a.Str("desc") ?? "", cancellationToken: ct);
        if (!r.Success) throw new LeapError($"the material upload failed: {r.Status}{(r.Error is null ? "" : ": " + r.Error.Message)}");
        o["item_id"] = r.ItemID.ToString();
        o["asset_id"] = r.AssetID.ToString();
        o["charged"] = textures * each;
        if (r.Diagnostics.Count > 0) o["log"] = string.Join("\n", r.Diagnostics.Select(x => x.ToString()));
        return o;
    }
}
