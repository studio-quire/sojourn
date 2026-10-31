using LibreMetaverse;
using LibreMetaverse.Assets;
using Sojourn.Core.World;
using Sojourn.Render.World;

namespace Sojourn.Render.Integration;

/// <summary>
/// A region as the host's shared world model has it (Sojourn.Core RegionWorld): the shared object and avatar
/// dictionaries every session in the region writes into, the heightmap assembled from all sessions' land patches,
/// whichever session's Simulator is live for metadata, and the grid's shared asset cache.
/// </summary>
public sealed class WorldRegionSource : IDisposable
{
    readonly RegionWorld _world;
    long _terrainVersion;
    readonly System.Collections.Concurrent.ConcurrentQueue<(byte, uint)> _changes = new();
    public RegionSource Source { get; }

    public WorldRegionSource(WorldHub hub, RegionWorld world)
    {
        _world = world;
        world.Changed += OnChanged;
        bool anyHeight = world.Heightmap.Any(h => !float.IsNaN(h));
        if (anyHeight) _terrainVersion = 1;
        var assets = new HubAssetSource(hub, world.GridKey);
        Source = new RegionSource
        {
            Handle = world.Handle,
            Prims = world.Prims,
            Avatars = world.Avatars,
            LiveSimulator = () => world.Simulator,
            TerrainVersion = () => Interlocked.Read(ref _terrainVersion),
            Heights = Heights,
            Assets = assets,
            Changes = _changes,
            ReflectionProbes = ReflectionProbeTracker.Shared.For(world.Handle),
            FetchEnvironment = async ct =>
                world.Simulator is { } sim ? await RegionSource.FetchExtEnvironment(sim.Client, sim, ct).ConfigureAwait(false) : null,
            FetchParcelEnvironment = async (parcel, ct) =>
                world.Simulator is { } sim ? await RegionSource.FetchExtEnvironment(sim.Client, sim, ct, parcel).ConfigureAwait(false) : null,
            AgentParcel = () => world.Simulator is { } sim ? RegionSource.AgentParcelIn(sim.Client, sim) : 0,
            FetchLegacyMaterials = async (ids, ct) =>
                world.Simulator is { } sim ? await RegionSource.FetchMaterials(sim.Client, sim, ids, ct).ConfigureAwait(false) : MaterialsReply.NotAnswered,
        };
    }

    public RegionWorld World => _world;

    void OnChanged(RegionWorld w, WorldChange change, uint localId)
    {
        switch (change)
        {
            case WorldChange.Terrain: Interlocked.Increment(ref _terrainVersion); break;
            case WorldChange.Added or WorldChange.Updated or WorldChange.Moved: _changes.Enqueue((RegionSource.ChangeTouched, localId)); break;
            case WorldChange.Removed: _changes.Enqueue((RegionSource.ChangeRemoved, localId)); break;
        }
    }

    TerrainHeights? Heights()
    {
        var src = _world.Heightmap;
        var data = new float[src.Length];
        bool any = false;
        for (int i = 0; i < src.Length; i++)
        {
            float h = src[i];
            if (float.IsNaN(h)) h = 0;   // patch not arrived yet: flat, like the viewer before it hears about it
            else any = true;
            data[i] = h;
        }
        return any ? new TerrainHeights(data, (int)_world.SizeX) : null;
    }

    public void Dispose() => _world.Changed -= OnChanged;
}

/// <summary>Assets through the host's per-grid shared cache (one download for every agent and the renderer); avatar
/// bakes from our residents' bake cache, else the grid's appearance service (server-side baking), else the region.</summary>
sealed partial class HubAssetSource(WorldHub hub, string gridKey) : IAssetSource, IBakeSource, IRangedAssetSource
{
    public async Task<byte[]?> GetBakeAsync(UUID avatar, int textureEntry, UUID texture, CancellationToken ct)
    {
        if (hub.Bakes(gridKey).ByTexture(texture) is { Length: > 0 } ours) return ours;
        if (hub.ClientFor(gridKey) is { } c && !string.IsNullOrEmpty(c.Network.AgentAppearanceServiceURL) && BakeName(textureEntry) is { } name
            && await c.Assets.RequestServerBakedImageAsync(avatar, texture, name, ct).ConfigureAwait(false) is { AssetData.Length: > 0 } sb)
            return sb.AssetData;
        return await GetTextureAsync(texture, ct).ConfigureAwait(false);
    }

    // the appearance service's names for the bake slots (the viewer's LLAvatarAppearanceDictionary)
    static string? BakeName(int te) => te switch
    {
        8 => "head", 9 => "upper", 10 => "lower", 11 => "eyes", 19 => "skirt", 20 => "hair",
        40 => "leftarm", 41 => "leftleg", 42 => "aux1", 43 => "aux2", 44 => "aux3", _ => null,
    };


    public async Task<byte[]?> GetTextureAsync(UUID id, CancellationToken ct) =>
        (await hub.TextureAsync(gridKey, id, ct).ConfigureAwait(false))?.AssetData is { Length: > 0 } d ? d : null;

    public async Task<byte[]?> GetMeshAsync(UUID id, CancellationToken ct) =>
        (await hub.MeshAsync(gridKey, id, ct).ConfigureAwait(false))?.AssetData is { Length: > 0 } d ? d : null;

    public async Task<byte[]?> GetAssetAsync(UUID id, AssetType type, CancellationToken ct)
    {
        // GLTF materials: LibreMetaverse 3.2.0 asks with the wrong query and can't unwrap OpenSim's LLSD (see FetchMaterialAsync)
        if (type == AssetType.Material)
            return hub.ClientFor(gridKey) is { } c ? await LmvAssetSource.FetchMaterialAsync(c, id, ct).ConfigureAwait(false) : null;
        return (await hub.AssetAsync(gridKey, id, type, ct).ConfigureAwait(false))?.AssetData is { Length: > 0 } d ? d : null;
    }
}
