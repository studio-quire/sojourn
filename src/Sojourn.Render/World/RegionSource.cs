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

/// <summary>A RenderMaterials ask's outcome: the materials the region answered with (Answered: those it didn't name
/// don't exist), or no answer (no capability yet, a network error: asked again after a wait), or the grid's rate
/// limit (asked again, the region's asks slowed).</summary>
public readonly record struct MaterialsReply(IReadOnlyList<LegacyMaterial> Materials, bool Answered, bool Limited)
{
    public static readonly MaterialsReply NotAnswered = new([], false, false);
    public static readonly MaterialsReply RateLimited = new([], false, true);
}

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
    /// <summary>Fetches legacy (LLMaterial) material definitions by id through the region's RenderMaterials cap
    /// (<see cref="FetchMaterials"/>), paced by MaterialStore as the viewer paces them.</summary>
    public Func<IReadOnlyCollection<UUID>, CancellationToken, Task<MaterialsReply>>? FetchLegacyMaterials { get; init; }
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
        FetchLegacyMaterials = (ids, ct) => FetchMaterials(client, sim, ids, ct),
        ReflectionProbes = probes?.For(sim.Handle),
    };

    /// <summary>POST ids to the region's RenderMaterials cap (LLMaterialMgr::processGetQueue: a zipped LLSD array of
    /// ids, the answer zipped the same way). Our own rather than LibreMetaverse's RequestMaterialsAsync, which logs the
    /// grid's rate-limit reply ("cap invocation rate ...", not LLSD) as a parse failure and returns nothing, so the
    /// store couldn't tell "slow down" from "these don't exist" (2026-10-31: 282 such replies in one sitting).</summary>
    public static async Task<MaterialsReply> FetchMaterials(GridClient client, Simulator sim, IReadOnlyCollection<UUID> ids, CancellationToken ct)
    {
        var cap = sim.Caps?.CapabilityURI("RenderMaterials");
        if (cap == null || ids.Count == 0) return MaterialsReply.NotAnswered;
        var array = new OSDArray();
        foreach (var id in ids) array.Add(id);
        var request = new OSDMap { ["Zipped"] = Helpers.ZCompressOSD(array) };
        try
        {
            var (resp, data) = await client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, request, ct).ConfigureAwait(false);
            int status = resp != null ? (int)resp.StatusCode : 0;
            if (status is 429 or 503 || IsRateLimit(data)) return MaterialsReply.RateLimited;
            if (data == null || data.Length == 0 || status >= 400) return MaterialsReply.NotAnswered;
            if (OSDParser.Deserialize(data) is not OSDMap map || !map.ContainsKey("Zipped")) return MaterialsReply.NotAnswered;
            var list = new List<LegacyMaterial>();
            if (Helpers.ZDecompressOSD(map["Zipped"].AsBinary()) is OSDArray mats)
                foreach (var e in mats)
                    if (e is OSDMap m) list.Add(new LegacyMaterial(m));
            return new MaterialsReply(list, true, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return MaterialsReply.NotAnswered; }
    }

    static bool IsRateLimit(byte[]? data)
    {
        if (data is not { Length: > 0 }) return false;
        var head = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 64)).TrimStart();
        return head.StartsWith("cap invocation rate", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How the viewer paces RenderMaterials asks to a region (LLViewerRegion::getMaxMaterialsPerTransaction,
    /// resetMaterialsCapThrottle): the simulator's MaxMaterialsPerTransaction ids an ask (50 if it doesn't say) and
    /// RenderMaterialsCapability asks a second (1 if it doesn't say, or says 0).</summary>
    public static (int PerAsk, double PerSecond) MaterialsPacing(Simulator? sim)
    {
        int perAsk = 50;
        double perSecond = 1;
        try
        {
            if (sim?.Features is { } f)
            {
                if (f.Get("MaxMaterialsPerTransaction") is { Type: OSDType.Integer } n && n.AsInteger() > 0) perAsk = n.AsInteger();
                if (f.Get("RenderMaterialsCapability") is { Type: OSDType.Real or OSDType.Integer } r && r.AsReal() > 0) perSecond = r.AsReal();
            }
        }
        catch (Exception) { }
        return (perAsk, perSecond);
    }

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
