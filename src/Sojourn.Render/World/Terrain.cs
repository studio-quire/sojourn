using LibreMetaverse;
using LibreMetaverse.Assets;
using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;
using Sojourn.Render.TerrainGen;
using Sojourn.Render.Shadows;

namespace Sojourn.Render.World;

/// <summary>
/// The region's land surface, as the viewer builds and shades it (TerrainGen/ port): the composition and normals
/// once per terrain change, the patch geometry per camera stride pattern (cached), and either classic four-texture
/// shading or PBR terrain (the four terrain slots naming GLTF materials), both through the alpha ramp. Surfaces and
/// meshes are built on the thread pool (each takes 10-30 ms, a dropped frame or two in a headset); until a new one is
/// ready the last one is drawn.
/// </summary>
public sealed class Terrain
{
    readonly RenderWorld _world;
    readonly RegionSource _source;
    long _version;
    bool _dirty = true;
    long _changedAt;
    TerrainSurface? _surface;
    readonly Dictionary<ulong, (MeshRange Range, long Used)> _meshes = new();
    Task<TerrainSurface>? _surfaceBuild;
    (ulong Key, TerrainSurface For, Task<(MeshVertex[] Verts, uint[] Indices)> Build)? _meshBuild;
    MeshRange? _lastMesh;      // drawn while the camera's own stride pattern builds
    MeshRange? _mainMesh;      // what the main view drew last: probe captures and mirrors draw it too
    string _shown = "";        // the last detail assets and type logged
    readonly List<MeshRange> _stale = new();   // the previous surface's meshes, until the new one has a mesh
    public bool HasData => _surface != null;
    /// <summary>The current land surface (immutable; replaced on each terrain change) and a counter of those changes.</summary>
    public TerrainSurface? Surface => _surface;
    public int Revision { get; private set; }
    public UUID[] Detail = new UUID[4];
    static int _rampSlot = -1;
    static GpuImage? _ramp;

    /// <summary>RenderTerrainLODFactor as the Mid-and-above presets set it (the viewer squares it).</summary>
    public static float LodFactor = 2.0f;
    /// <summary>RenderTerrainPBRPlanarSampleCount: 3 (triplanar) as the viewer's High presets and up, 1 (flat) below.</summary>
    public static int PbrPlanarSamples = TerrainShading.RenderTerrainPbrPlanarSampleCountHigh;

    public Terrain(RenderWorld world, RegionSource source)
    {
        _world = world;
        _source = source;
    }

    public void Sync()
    {
        if (!_source.HasSimulator) return;
        var sim = _source.Simulator;
        long version = _source.TerrainVersion();
        if (version == 0) return;
        if (version != _version) { _version = version; _dirty = true; _changedAt = System.Environment.TickCount64; }
        Detail[0] = sim.TerrainDetail0; Detail[1] = sim.TerrainDetail1; Detail[2] = sim.TerrainDetail2; Detail[3] = sim.TerrainDetail3;
        if (_surfaceBuild is { IsCompleted: true } done)
        {
            _surfaceBuild = null;
            if (done.IsCompletedSuccessfully)
            {
                _surface = done.Result;
                Revision++;
                // the old surface's meshes stay drawable until the new surface has one
                _stale.AddRange(_meshes.Values.Select(m => m.Range));
                _meshes.Clear();
            }
            else _world.Log?.Invoke($"terrain {_source.Handle}: {done.Exception?.InnerException?.Message}");
        }
        // Patches stream in: rebuild at once the first time, once they settle, and every so often while they keep coming.
        // Waiting for them to settle alone left a region whose patches trickle in for tens of seconds (a neighbour's,
        // over its child circuit) on its first surface, nearly all of it at height 0 under the water, its trees
        // standing on nothing (Mirromere, looking east with a 256 m draw distance, 2026-10-28).
        long now = System.Environment.TickCount64;
        // while a terraform stroke goes the land is rebuilt as it moves (HurryUntil)
        bool hurry = now < Interlocked.Read(ref HurryUntil);
        bool settled = now - _changedAt > (hurry ? 100 : 500);
        if (_dirty && _surfaceBuild == null && (_surface == null || settled || now - _builtAt > (hurry ? 250 : StreamingRebuildMs)))
        {
            if (settled && _streamedFrom != 0)
            {
                _world.Log?.Invoke($"terrain {_source.Name}: all in, {(now - _streamedFrom) / 1000.0:0.0} s after its first patches ({_rebuilds} surfaces meanwhile)");
                _streamedFrom = 0;
            }
            else if (!settled && _streamedFrom == 0) _streamedFrom = _changedAt;
            Rebuild();
        }
    }

    long _builtAt, _streamedFrom;
    int _rebuilds;
    /// <summary>How often the surface is rebuilt while patches keep arriving (each is 10-17 ms on the thread pool, and a
    /// new patch mesh after it).</summary>
    const long StreamingRebuildMs = 1500;
    /// <summary>Until then (Environment.TickCount64), a changed surface is rebuilt within a quarter second: a terraform
    /// stroke the person is watching (Agents/ParcelOps.Terraform.cs, through Client/Land/LandWiring.cs).</summary>
    public static long HurryUntil;

    void Rebuild()
    {
        _dirty = false;
        _builtAt = System.Environment.TickCount64;
        _rebuilds = _streamedFrom != 0 ? _rebuilds + 1 : 0;
        if (_source.Heights() is not { } heights) return;
        var input = TerrainInput.FromHeights(heights, _source.Simulator);
        _surfaceBuild = Task.Run(() => new TerrainSurface(input, TerrainNormalMode.Classic));
    }

    /// <summary>Give back this region's terrain geometry (the region left the renderer).</summary>
    public void Release()
    {
        foreach (var m in _meshes.Values) _world.RetireRange(m.Range);
        foreach (var r in _stale) _world.RetireRange(r);
        _meshes.Clear();
        _stale.Clear();
        _surface = null;
        _lastMesh = null;
        _mainMesh = null;
        _meshBuild = null;      // a build still running is dropped when it finishes
        _surfaceBuild = null;
    }

    TerrainSurface? _keyFor;
    Vector3 _keyAt;
    int[]? _keyStrides;
    ulong _key;

    /// <summary>The mesh for the camera's stride pattern, or while that builds the last one drawn (null: none yet). The
    /// main view's alone: the key cache, the build slot and the last mesh are its, so nothing else's camera can reset
    /// them (see <see cref="BuildDrawList"/>).</summary>
    MeshRange? MeshFor(Vector3 cameraRegion)
    {
        var surface = _surface!;
        int n = surface.PatchesPerEdge;
        // the stride pattern decides the geometry; hash it to reuse meshes across nearby cameras and agents. It only
        // changes as the camera moves by metres, so it's worked out again once it has moved half a metre
        int[] strides;
        ulong key;
        if (ReferenceEquals(surface, _keyFor) && Vector3.DistanceSquared(cameraRegion, _keyAt) < 0.25f) (strides, key) = (_keyStrides!, _key);
        else
        {
            var lazy = TerrainLod.CameraStrides(surface, cameraRegion, LodFactor);
            strides = new int[n * n];
            key = 1469598103934665603UL;
            for (int py = 0; py <= n; py++)
                for (int px = 0; px <= n; px++)
                {
                    if (px == n && py == n) continue;
                    int s = px < n && py < n ? strides[py * n + px] = lazy(px, py) : 0;
                    key = (key ^ (ulong)s) * 1099511628211UL;
                }
            (_keyFor, _keyAt, _keyStrides, _key) = (surface, cameraRegion, strides, key);
        }
        long now = System.Environment.TickCount64;

        // a finished build: into the geometry pool (render thread), kept with the others
        if (_meshBuild is { Build.IsCompleted: true } built)
        {
            _meshBuild = null;
            if (built.Build.IsCompletedSuccessfully && ReferenceEquals(built.For, surface) && !_meshes.ContainsKey(built.Key))
            {
                var (verts, indices) = built.Build.Result;
                var range = _world.Gpu.GeometryPool.Add(verts, indices);
                // keep a handful of stride patterns per region
                if (_meshes.Count >= 8)
                {
                    var oldest = _meshes.OrderBy(kv => kv.Value.Used).First();
                    _world.RetireRange(oldest.Value.Range);
                    _meshes.Remove(oldest.Key);
                }
                _meshes[built.Key] = (range, now);
                foreach (var r in _stale) _world.RetireRange(r);
                _stale.Clear();
            }
        }
        if (_meshes.TryGetValue(key, out var cached))
        {
            _meshes[key] = (cached.Range, now);
            return _lastMesh = cached.Range;
        }
        if (_meshBuild == null)
            _meshBuild = (key, surface, Task.Run(() =>
            {
                // tangents always (PBR terrain's normal maps need them; classic shading ignores them), so a region
                // switching between the two keeps its meshes
                var mesh = TerrainMeshBuilder.Build(surface, (px, py) => strides[py * n + px], tangents: true);
                var verts = new MeshVertex[mesh.Vertices.Length];
                for (int i = 0; i < verts.Length; i++)
                {
                    var tv = mesh.Vertices[i];
                    verts[i] = new MeshVertex { Position = tv.Position, Normal = tv.Normal, Tangent = mesh.Tangents![i], Uv = tv.TexCoord1 };
                }
                return (verts, mesh.Indices);
            }));
        // the last mesh, if it's still this surface's; otherwise the most recent one of this surface
        if (_lastMesh is { } last && (_meshes.Values.Any(m => m.Range.Equals(last)) || _stale.Contains(last))) return last;
        return _lastMesh = _meshes.Count > 0 ? _meshes.Values.MaxBy(m => m.Used).Range : null;
    }

    int RampSlot()
    {
        if (_rampSlot >= 0) return _rampSlot;
        var alpha = TerrainAlphaRamp.DecodeAlpha();   // 256x256, row 0 = v 0 (bottom), like our textures
        var rgba = new byte[alpha.Length * 4];
        for (int i = 0; i < alpha.Length; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = rgba[i * 4 + 3] = alpha[i]; }
        var vk = _world.Gpu.Vk;
        _ramp = new GpuImage(vk, 256, 256, Silk.NET.Vulkan.Format.R8G8B8A8Unorm,
            Silk.NET.Vulkan.ImageUsageFlags.SampledBit | Silk.NET.Vulkan.ImageUsageFlags.TransferDstBit | Silk.NET.Vulkan.ImageUsageFlags.TransferSrcBit, 9);
        _world.Gpu.Uploader.CopyToImageWithMips(_ramp, rgba);
        _rampSlot = _world.Gpu.Bindless.AllocTexture();
        _world.Gpu.Bindless.Write(_rampSlot, _ramp.View, _world.Gpu.Bindless.Clamp);
        return _rampSlot;
    }

    /// <summary>Free the shared alpha ramp (device teardown).</summary>
    public static void DisposeShared(Bindless bindless)
    {
        if (_rampSlot >= 0) bindless.FreeTexture(_rampSlot);
        _ramp?.Dispose();
        _ramp = null;
        _rampSlot = -1;
    }

    public void AddExtents(AabbSceneExtents ext, Vector3 offset)
    {
        if (_surface == null) return;
        int n = _surface.PatchesPerEdge;
        for (int py = 0; py < n; py++)
            for (int px = 0; px < n; px++)
            {
                var b = _surface.GetPatchBounds(px, py);
                float maxZ = MathF.Max(b.MaxZ, b.MinZ + 1f);
                var c = b.Center + offset;
                ext.Add(new Vector3(c.X - 8, c.Y - 8, b.MinZ), new Vector3(c.X + 8, c.Y + 8, maxZ));
            }
    }

    public void BuildDrawList(ViewSetup v, DrawList dl, Vector3 offset, Frustum frustum, ref RegionDrawStats stats, ShadowFrustum?[]? casters = null)
    {
        if (_surface == null) return;
        // Patch strides follow the main view's camera, the head's centre in the headset: a sphere around it (each
        // patch's distance less its radius), so turning the head never changes them. Probe captures and mirrors draw
        // whatever mesh the main view chose (LLSurfacePatch updates only in main frames). They used to work strides
        // out from their own LOD camera, which for a capture is the camera when it was asked for, often seconds or a
        // teleport stale, and shared the main view's key cache, build slot and last mesh: interleaved with the main
        // view, they reset its cache and handed it their mesh while its own built, so the ground could flip between
        // its near and far detail (Eden Valley, 2026-10-28).
        MeshRange? range;
        if (v.LodView == null) range = _mainMesh = MeshFor(v.Position - offset);
        else if ((range = MainMesh()) == null) { stats.Pending++; return; }   // a capture waits for the ground
        if (range is not { } drawn) return;

        uint m = AddMaterials(v, dl, ref stats);
        uint inst = dl.AddInstance(new GpuInstance
        {
            Model = Matrix4x4.CreateTranslation(offset),
            N0 = new Vector4(1, 0, 0, 0), N1 = new Vector4(0, 1, 0, 0), N2 = new Vector4(0, 0, 1, 0), Material = m,
        });
        var cmd = new DrawCmd { Range = drawn, Instance = inst, Bounds = new Vector4(offset + new Vector3(128, 128, 0), 400) };
        dl.Opaque.Add(cmd);
        if (casters != null)
            for (int c = 0; c < casters.Length; c++)
                if (casters[c] != null) dl.ShadowCasters[c].Add(cmd);   // terrain casts sun and projector shadows
    }

    /// <summary>The main view's mesh, if it's still held (not retired).</summary>
    MeshRange? MainMesh()
    {
        if (_mainMesh is not { } m) return null;
        foreach (var x in _meshes.Values) if (x.Range.Equals(m)) return m;
        return _stale.Contains(m) ? m : null;
    }

    /// <summary>
    /// The terrain's material, and for PBR terrain the four slots' after it. LLTerrainMaterials::getMaterialType: the
    /// four detail ids are tried as textures and as GLTF material assets; textures win if any loads, else materials
    /// if any loads, else textures (grey until they come). Materials are only asked for while no texture has loaded,
    /// so a classic region doesn't keep asking for materials that aren't there.
    /// </summary>
    uint AddMaterials(ViewSetup v, DrawList dl, ref RegionDrawStats stats)
    {
        var assets = _source.Assets;
        var tex = new SceneTexture?[4];
        var textureReady = new bool[4];
        for (int i = 0; i < 4; i++)
        {
            var id = Detail[i] != UUID.Zero ? Detail[i] : new UUID(TerrainShading.DefaultDetailTextures[i]);
            tex[i] = _world.Textures.Request(id, assets);
            textureReady[i] = tex[i]!.State == TexState.Ready;
        }
        bool anyTexture = textureReady.Any(b => b);
        var mats = new AssetMaterial?[4];
        var materialReady = new bool[4];
        bool materialsWaiting = false;
        if (!anyTexture)
            for (int i = 0; i < 4; i++)
            {
                if (Detail[i] == UUID.Zero) continue;
                mats[i] = _world.Materials.Gltf(Detail[i], assets, out bool waiting);
                materialsWaiting |= waiting;
                materialReady[i] = mats[i] != null;
            }
        var type = TerrainShading.ChooseMaterialType(textureReady, materialReady);
        Shown(type, mats);

        if (type == TerrainMaterialType.Texture)
        {
            // what's still coming holds up probe captures: the textures, or materials that may yet decide it's PBR
            for (int i = 0; i < 4; i++) if (tex[i]!.State == TexState.Loading) stats.Pending++;
            if (materialsWaiting) stats.Pending++;
            // detail texgen offsets come from the agent's region origin and apply to every region (seams at edges, as in the viewer)
            var origin = TerrainInput.OriginFromHandle(v.RegionHandle);
            var (planeS, planeT) = TerrainShading.DetailTexGen(origin.X, origin.Y);
            return dl.AddMaterial(new GpuMaterial
            {
                Color = Vector4.One,
                Diffuse = FaceMaterials.TeRef(tex[0], 1, 1, 0, 0, 0),
                Normal = FaceMaterials.TeRef(tex[1], 1, 1, 0, 0, 0),
                Specular = FaceMaterials.TeRef(tex[2], 1, 1, 0, 0, 0),
                Emissive = FaceMaterials.TeRef(tex[3], 1, 1, 0, 0, 0),
                Flags = MaterialFlags.Terrain,
                Metallic = planeS.W, Roughness = planeT.W,   // detail texgen offsets
                AlphaCutoff = planeS.X,                      // detail scale (1 / RenderTerrainScale)
                Bump = (uint)RampSlot(),
            });
        }

        // PBR (Shaders/terrain_pbr.glsl): the terrain's material, then the four slots' in order
        if (materialsWaiting) stats.Pending++;
        var none = new GpuTexRef { Index = -1, Scale = Vector2.One };
        uint first = dl.AddMaterial(new GpuMaterial
        {
            Color = Vector4.One,
            Diffuse = none, Normal = none, Specular = none, Emissive = none,
            Flags = MaterialFlags.Terrain | MaterialFlags.TerrainPbr,
            EnvIntensity = PbrPlanarSamples,
            Bump = (uint)RampSlot(),
        });
        for (int i = 0; i < 4; i++) dl.AddMaterial(PbrSlot(mats[i], assets, ref stats));
        return first;
    }

    /// <summary>One PBR terrain slot (LLDrawPoolTerrain::renderFullShaderPBR): its maps, factors, base colour transform
    /// (scale divided by RenderTerrainPBRScale; every map of the slot uses it) and minimum alpha. A slot whose material
    /// didn't come draws GLTF's default material (white, metallic and roughness factors 1).</summary>
    GpuMaterial PbrSlot(AssetMaterial? g, IAssetSource assets, ref RegionDrawStats stats)
    {
        float detail = 1f / TerrainShading.RenderTerrainPbrScale;
        var none = new GpuTexRef { Index = -1, Scale = Vector2.One };
        if (g == null)
            return new GpuMaterial
            {
                Color = Vector4.One,
                Diffuse = none with { Scale = new Vector2(detail) }, Normal = none, Specular = none, Emissive = none,
                Metallic = 1f, Roughness = 1f, AlphaCutoff = -0f, Flags = MaterialFlags.Pbr,
            };
        var ids = g.TextureIds ?? Array.Empty<UUID>();
        var tex = new SceneTexture?[4];
        for (int i = 0; i < 4 && i < ids.Length; i++)
            if (ids[i] != UUID.Zero)
            {
                tex[i] = _world.Textures.Request(ids[i], assets);
                if (tex[i]!.State == TexState.Loading) stats.Pending++;
            }
        var x = g.TextureTransforms is { Length: > 0 } t ? t[0] : GltfTextureTransform.Default;
        var bc = g.BaseColorFactor;
        static GpuTexRef Ref(SceneTexture? s) => new() { Index = s is { State: TexState.Ready } r ? r.Slot : -1, Scale = Vector2.One };
        return new GpuMaterial
        {
            Color = new Vector4(bc.R, bc.G, bc.B, bc.A),
            SpecColor = new Vector4(g.EmissiveFactor.X, g.EmissiveFactor.Y, g.EmissiveFactor.Z, 0),
            Diffuse = Ref(tex[0]) with
            {
                Scale = new Vector2(x.Scale.X, x.Scale.Y) * detail, Offset = new Vector2(x.Offset.X, x.Offset.Y), Rotation = x.Rotation,
            },
            Normal = Ref(tex[1]),
            Specular = Ref(tex[2]),
            Emissive = Ref(tex[3]),
            Metallic = g.MetallicFactor,
            Roughness = g.RoughnessFactor,
            AlphaCutoff = TerrainShading.PbrMinimumAlpha(g.AlphaMode == GltfAlphaMode.Mask, g.AlphaCutoff, bc.A),
            Flags = MaterialFlags.Pbr,
        };
    }

    /// <summary>A log line whenever the region's terrain assets, or the way they're drawn, change.</summary>
    void Shown(TerrainMaterialType type, AssetMaterial?[] mats)
    {
        string kind = type == TerrainMaterialType.Pbr ? $"PBR materials ({mats.Count(x => x != null)} of 4 loaded)" : "textures";
        string key = $"{kind}: {string.Join(", ", Detail)}";
        if (key == _shown) return;
        _shown = key;
        _world.Log?.Invoke($"terrain {_source.Name}: drawn with {key}");
    }
}
