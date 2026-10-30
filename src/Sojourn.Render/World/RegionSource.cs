using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Materials;
using LibreMetaverse.StructuredData;

namespace Sojourn.Render.World;

/// <summary>
/// Where the renderer gets bytes for assets. Phase 1's shared asset cache implements this; LmvAssetSource is the
/// default over one GridClient. Implementations must be thread-safe; the renderer calls them from worker threads.
/// </summary>
public interface IAssetSource
{
    /// <summary>A texture's full encoded data (J2C as stored by the grid), or null if unavailable.</summary>
    Task<byte[]?> GetTextureAsync(UUID id, CancellationToken ct);
    /// <summary>A mesh asset's raw data, or null.</summary>
    Task<byte[]?> GetMeshAsync(UUID id, CancellationToken ct);
    /// <summary>Any other asset (GLTF materials), or null.</summary>
    Task<byte[]?> GetAssetAsync(UUID id, AssetType type, CancellationToken ct);
}

/// <summary>The region's height grid ([y*Width+x], metres).</summary>
public sealed record TerrainHeights(float[] Data, int Width);

/// <summary>
/// One region as the renderer sees it: its objects and avatars (LibreMetaverse types, shared and read concurrently),
/// its height grid, a live Simulator for metadata (terrain textures and heights, water height, GLTF overrides),
/// an asset source and the region's environment. Over one GridClient (<see cref="FromClient"/>) or over the
/// host's shared world model (Integration/WorldRegionSource), where sessions come and go under it.
/// </summary>
public sealed class RegionSource
{
    public required ulong Handle { get; init; }
    public required IReadOnlyDictionary<uint, Primitive> Prims { get; init; }
    public required IReadOnlyDictionary<uint, Avatar> Avatars { get; init; }
    /// <summary>A live Simulator for the region (metadata only; never sent through). Null while none is connected.</summary>
    public required Func<Simulator?> LiveSimulator { get; init; }
    /// <summary>Changes whenever the terrain does; 0 while there is none. Polled every frame, so keep it cheap.</summary>
    public required Func<long> TerrainVersion { get; init; }
    /// <summary>The height grid, or null until terrain has arrived. Called when TerrainVersion changes.</summary>
    public required Func<TerrainHeights?> Heights { get; init; }
    public required IAssetSource Assets { get; init; }
    /// <summary>Objects that changed (local ids, as the region reported them), which the scene applies each frame
    /// instead of rescanning every object (RegionScene.Index.cs). Null: no reports, so it rescans.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<(byte Kind, uint LocalId)>? Changes { get; init; }
    public const byte ChangeTouched = 0, ChangeRemoved = 1;
    Simulator? _lastSim;

    /// <summary>The live Simulator, or the last one seen (its metadata stays valid after its session leaves).</summary>
    public Simulator Simulator
    {
        get
        {
            var s = LiveSimulator();
            if (s != null) _lastSim = s;
            return _lastSim ?? throw new InvalidOperationException($"region {Handle} has no simulator yet");
        }
    }
    public bool HasSimulator => LiveSimulator() != null || _lastSim != null;
    public string Name => (LiveSimulator() ?? _lastSim)?.Name is { Length: > 0 } n ? n : $"region {Handle}";
    /// <summary>Fetches the region's raw ExtEnvironment capability response (EEP). Null = not available (yet);
    /// the renderer retries. A region that answers with no environment gets the viewer's default day.</summary>
    public Func<CancellationToken, Task<OSDMap?>>? FetchEnvironment { get; init; }
    /// <summary>A parcel's own environment (ExtEnvironment?parcelid=), as the viewer asks for the agent's parcel.</summary>
    public Func<int, CancellationToken, Task<OSDMap?>>? FetchParcelEnvironment { get; init; }
    /// <summary>The local id of the parcel the agent stands in, if it's in this region and the parcel is known; else 0.</summary>
    public Func<int>? AgentParcel { get; init; }
    /// <summary>GLTF material ids by texture entry index for an object (the render-material extra parameter that
    /// LibreMetaverse drops). Defaults to <see cref="RenderMaterialTracker.Shared"/>.</summary>
    public Func<uint, UUID[]?>? RenderMaterials { get; init; }
    public UUID[]? RenderMaterialIds(uint localId) => RenderMaterials != null ? RenderMaterials(localId) : RenderMaterialTracker.Shared.Get(Handle, localId);
    /// <summary>Fetches legacy (LLMaterial) material definitions by id through the region's RenderMaterials cap.</summary>
    public Func<IReadOnlyCollection<UUID>, CancellationToken, Task<IEnumerable<LegacyMaterial>>>? FetchLegacyMaterials { get; init; }
    /// <summary>Manual reflection probe params by prim local id (ExtraParams 0x90, see ReflectionProbeTracker); null: none known.</summary>
    public Func<uint, ReflectionProbeParams?>? ReflectionProbes { get; init; }

    /// <summary>A region source over one GridClient's view of a simulator.</summary>
    /// Needs GridClient.Settings.World.StoreLandPatches for terrain.
    public static RegionSource FromClient(GridClient client, Simulator sim, IAssetSource? assets = null, ReflectionProbeTracker? probes = null) => new()
    {
        Handle = sim.Handle,
        Prims = sim.ObjectsPrimitives,
        Avatars = sim.ObjectsAvatars,
        LiveSimulator = () => sim,
        TerrainVersion = () => PatchVersion(sim),
        Heights = () => HeightsFromPatches(sim),
        Assets = assets ?? new LmvAssetSource(client),
        FetchEnvironment = ct => FetchExtEnvironment(client, sim, ct),
        FetchParcelEnvironment = (parcel, ct) => FetchExtEnvironment(client, sim, ct, parcel),
        AgentParcel = () => AgentParcelIn(client, sim),
        FetchLegacyMaterials = (ids, ct) => client.Objects.RequestMaterialsAsync(sim, ids, ct),
        ReflectionProbes = probes?.For(sim.Handle),
    };

    /// <summary>GET the region's ExtEnvironment capability (EEP settings), or a parcel's (its local id), or null if it
    /// can't be had right now.</summary>
    public static async Task<OSDMap?> FetchExtEnvironment(GridClient client, Simulator sim, CancellationToken ct, int parcel = -1)
    {
        var cap = sim.Caps?.CapabilityURI("ExtEnvironment");
        if (cap == null) return null;
        if (parcel > 0) cap = new Uri(cap + "?parcelid=" + parcel);
        var (response, data) = await client.HttpCapsClient.GetAsync(cap, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || data == null) return null;
        return Sojourn.Render.Environment.SettingsLlsd.Parse(data) as OSDMap;   // LMV's notation reader trips on empty arrays
    }

    /// <summary>The parcel the client's agent stands in, by the region's parcel map (LLViewerParcelMgr's agent parcel),
    /// when the agent is in this region; 0 when it isn't or the map hasn't arrived.</summary>
    public static int AgentParcelIn(GridClient client, Simulator sim)
    {
        if (client.Network.CurrentSim != sim) return 0;
        try { return client.Parcels.GetParcelLocalID(sim, client.Self.SimPosition); }
        catch { return 0; }
    }

    /// <summary>A version for a Simulator's stored land patches, following patch object identity.</summary>
    public static long PatchVersion(Simulator sim)
    {
        var patches = sim.Terrain;
        if (patches == null) return 0;
        long version = 0;
        for (int i = 0; i < patches.Length; i++)
            if (patches[i]?.Data != null)
                version = version * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(patches[i]) + i + 1;
        return version;
    }

    /// <summary>Heights from a Simulator's stored land patches.</summary>
    public static TerrainHeights? HeightsFromPatches(Simulator sim)
    {
        var list = new List<(int X, int Y, float[] Data)>();
        foreach (var p in sim.Terrain ?? [])
            if (p?.Data is { } d) list.Add((p.X, p.Y, d));
        if (list.Count == 0) return null;
        int width = (int)sim.SizeX;
        return new TerrainHeights(Sojourn.Render.TerrainGen.TerrainInput.HeightsFromPatches(width, list), width);
    }
}
/// <summary>Asset source over a GridClient's AssetManager (HTTP caps with LibreMetaverse's own disk cache).</summary>
public sealed class LmvAssetSource(GridClient client) : IAssetSource
{
    public async Task<byte[]?> GetTextureAsync(UUID id, CancellationToken ct)
    {
        var tex = await client.Assets.RequestImageAsync(id, ImageType.Normal, ct).ConfigureAwait(false);
        return tex?.AssetData is { Length: > 0 } d ? d : null;
    }

    public async Task<byte[]?> GetMeshAsync(UUID id, CancellationToken ct)
    {
        var mesh = await client.Assets.RequestMeshAsync(id, ct).ConfigureAwait(false);
        return mesh?.AssetData is { Length: > 0 } d ? d : null;
    }

    public async Task<byte[]?> GetAssetAsync(UUID id, AssetType type, CancellationToken ct)
    {
        if (type == AssetType.Material) return await FetchMaterialAsync(client, id, ct).ConfigureAwait(false);
        var a = await client.Assets.RequestAssetAsync(id, type, true, ct).ConfigureAwait(false);
        return a?.AssetData is { Length: > 0 } d ? d : null;
    }

    /// <summary>A GLTF material asset over the ViewerAsset capability (<c>?material_id=</c>, as the viewer asks).
    /// LibreMetaverse 3.2.0 has no name for the material type and asks for <c>invalid_id</c>, which grids refuse.</summary>
    public static async Task<byte[]?> FetchMaterialAsync(GridClient client, UUID id, CancellationToken ct)
    {
        var cap = client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset");
        if (cap == null) return null;
        var (response, data) = await client.HttpCapsClient.GetAsync(new Uri($"{cap}?material_id={id}"), ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode && data is { Length: > 0 } ? data : null;
    }
}
