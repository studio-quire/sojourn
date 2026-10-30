using System.Collections.Concurrent;
using System.Net;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Messages.Linden;

namespace Sojourn.Core.World;

/// <summary>
/// All shared world state in the process: one <see cref="RegionWorld"/> per (grid, region handle), fed by every
/// agent session in that region, and one asset cache folder per grid. Sessions register their GridClient here
/// before login (<see cref="Bind"/>).
/// </summary>
public sealed class WorldHub
{
    readonly ConcurrentDictionary<(string Grid, ulong Handle), RegionWorld> _regions = new();
    readonly object _lock = new();
    readonly ConcurrentDictionary<GridClient, string> _clients = new();
    readonly ConcurrentDictionary<string, NameCache> _names = new();

    // Region handles by endpoint, learned from every simulator that came with one and from EnableSimulator. A
    // neighbour can arrive without one: LibreMetaverse connects on EstablishAgentCommunication with handle 0 when that
    // event beats EnableSimulator, then skips EnableSimulator because the endpoint is already known. Such a
    // simulator stays out of the shared world (own dictionaries, terrain buffered) until its handle is known.
    readonly Dictionary<(string Grid, IPEndPoint Ep), (ulong Handle, uint SizeX, uint SizeY)> _known = new();
    readonly Dictionary<Simulator, Unplaced> _unplaced = new(ReferenceEqualityComparer.Instance);

    sealed class Unplaced(string gridKey)
    {
        public readonly string GridKey = gridKey;
        public readonly List<(int X, int Y, int Size, float[] Heights)> Patches = new();
    }

    /// <summary>The grid's shared avatar-name cache.</summary>
    public NameCache Names(string gridKey) => _names.GetOrAdd(gridKey, _ => new NameCache());

    readonly ConcurrentDictionary<string, Sound.SoundCatalog> _sounds = new();
    readonly ConcurrentDictionary<string, string> _cacheDirs = new();
    readonly ConcurrentDictionary<string, Appearance.BakeCache> _bakes = new();

    /// <summary>The grid's client-side bake cache (in its asset cache folder).</summary>
    public Appearance.BakeCache Bakes(string gridKey) => _bakes.GetOrAdd(gridKey, g =>
        new Appearance.BakeCache(Path.Combine(_cacheDirs.TryGetValue(g, out var d) ? d : Path.Combine("cache", g), "bakes")));

    public IEnumerable<string> GridKeys => _cacheDirs.Keys;

    /// <summary>The grid's asset cache folder (shared by its clients), once one has registered.</summary>
    public string? CacheDir(string gridKey) => _cacheDirs.TryGetValue(gridKey, out var d) ? d : null;

    /// <summary>The grid's shared sound facts: names, length and loudness, transcripts.</summary>
    public Sound.SoundCatalog Sounds(string gridKey) => _sounds.GetOrAdd(gridKey, g => new Sound.SoundCatalog(this, g));
    readonly ConcurrentDictionary<(string, UUID, int), Task<object?>> _inflight = new();

    /// <summary>Raised when a region world is created (first agent arrives) or dropped (last agent leaves).</summary>
    public event Action<RegionWorld, bool>? RegionChanged;

    public IReadOnlyCollection<RegionWorld> Regions => _regions.Values.ToArray();

    public RegionWorld? Region(string gridKey, ulong handle) =>
        _regions.TryGetValue((gridKey, handle), out var r) ? r : null;

    public RegionWorld? RegionByName(string gridKey, string name) =>
        _regions.Values.FirstOrDefault(r => r.GridKey == gridKey && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Hooks a session's client into the shared world. Call once per GridClient, before login. <paramref name="log"/>: what's said about the session's own avatar.</summary>
    public void Bind(GridClient client, string gridKey, Action<string>? log = null)
    {
        _clients[client] = gridKey;
        _cacheDirs.TryAdd(gridKey, client.Settings.AssetCache.Dir);
        // LibreMetaverse prunes its cache folder to 0.9 of this every 5 minutes, oldest first; at its 1 GB an hour in
        // Second Life (2026-10-27) kept only the last ten minutes' meshes and whole textures
        client.Settings.AssetCache.MaxSize = Math.Max(client.Settings.AssetCache.MaxSize, 4L << 30);
        client.Network.SimConnecting += (_, e) => Guard(() =>
        {
            Attach(gridKey, e.Simulator);
            _ = VerifyLater(gridKey, e.Simulator);
        });
        client.Network.RegisterEventCallback("EnableSimulator", (_, msg, _) => Guard(() =>
        {
            foreach (var b in ((EnableSimulatorMessage)msg).Simulators)
                Learn(gridKey, new IPEndPoint(b.IP, b.Port), b.RegionHandle, b.RegionSizeX, b.RegionSizeY);
        }));
        client.Network.RegisterEventCallback("CrossedRegion", (_, msg, _) => Guard(() =>
        {
            var m = (CrossedRegionMessage)msg;
            Learn(gridKey, new IPEndPoint(m.IP, m.Port), m.RegionHandle, m.RegionSizeX, m.RegionSizeY);
        }));
        client.Network.RegisterEventCallback("TeleportFinish", (_, msg, _) => Guard(() =>
        {
            var m = (TeleportFinishMessage)msg;
            Learn(gridKey, new IPEndPoint(m.IP, m.Port), m.RegionHandle, m.RegionSizeX, m.RegionSizeY);
        }));
        client.Network.SimDisconnected += (_, e) => Guard(() => Detach(gridKey, e.Simulator));
        client.Network.Disconnected += (_, _) => Guard(() =>
        {
            foreach (var r in _regions.Values.Where(r => r.GridKey == gridKey))
                foreach (var s in r.Simulators.Where(s => ReferenceEquals(s.Client, client)))
                    Detach(gridKey, s);
            lock (_lock)
                foreach (var s in _unplaced.Keys.Where(s => ReferenceEquals(s.Client, client)).ToArray())
                    _unplaced.Remove(s);
        });
        client.Objects.ObjectUpdate += (_, e) => Guard(() =>
            WorldOf(gridKey, e.Simulator)?.Raise(e.IsNew ? WorldChange.Added : WorldChange.Updated, e.Prim.LocalID));
        client.Objects.TerseObjectUpdate += (_, e) => Guard(() =>
            WorldOf(gridKey, e.Simulator)?.Raise(WorldChange.Moved, e.Update.LocalID));
        client.Objects.KillObjects += (_, e) => Guard(() =>
        {
            var w = WorldOf(gridKey, e.Simulator);
            if (w is null) return;
            foreach (var id in e.ObjectLocalIDs) w.Raise(WorldChange.Removed, id);
        });
        client.Objects.ObjectProperties += (_, e) => Guard(() =>
        {
            var w = WorldOf(gridKey, e.Simulator);
            if (w is not null && w.GlobalToLocal.TryGetValue(e.Properties.ObjectID, out var local))
                w.Raise(WorldChange.Properties, local);
        });
        var names = Names(gridKey);
        client.Avatars.UUIDNameReply += (_, e) => Guard(() => { foreach (var (id, n) in e.Names) names.SetLegacy(id, n); });
        client.Avatars.DisplayNameUpdate += (_, e) => Guard(() => names.SetDisplay(e.DisplayName));
        client.Objects.AvatarUpdate += (_, e) => Guard(() =>
        {
            names.SetLegacy(e.Avatar.ID, e.Avatar.Name);
            RestoreAppearance(e.Avatar);
            if (e.Avatar.ID == client.Self.AgentID) SelfIn(gridKey, client, e.Simulator, log);
        });
        client.Network.SimChanged += (_, _) => Guard(() => SelfIn(gridKey, client, client.Network.CurrentSim, log));
        client.Avatars.AvatarAppearance += (_, e) => Guard(() => RememberAppearance(gridKey, e));
        client.Self.ChatFromSimulator += (_, e) => Guard(() =>
        {
            if (e.SourceType == ChatSourceType.Agent) names.SetLegacy(e.SourceID, e.FromName);
        });
        client.Self.IM += (_, e) => Guard(() =>
        {
            if (e.IM.Dialog is InstantMessageDialog.MessageFromAgent or InstantMessageDialog.FriendshipOffered
                or InstantMessageDialog.RequestTeleport or InstantMessageDialog.InventoryOffered)
                names.SetLegacy(e.IM.FromAgentID, e.IM.FromAgentName);
        });
        client.Grid.CoarseLocationUpdate += (_, e) => Guard(() =>
        {
            if (WorldOf(gridKey, e.Simulator) is not { } w) return;
            w.SetCoarse(e.Positions);
            if (e.Positions.Count == 0) return;
            foreach (var other in _regions.Values)
                if (other != w && other.GridKey == gridKey) other.RootElsewhere(e.Positions);
        });
        client.Network.RegisterCallback(LibreMetaverse.Packets.PacketType.ObjectProperties, (_, e) => Guard(() =>
        {
            if (WorldOf(gridKey, e.Simulator) is not { } w) return;
            foreach (var d in ((LibreMetaverse.Packets.ObjectPropertiesPacket)e.Packet).ObjectData)
                w.PropsMeta.GetOrAdd(d.ObjectID, _ => new PropsMeta()).Created = (long)(d.CreationDate / 1_000_000UL);
        }), false);
        client.Terrain.LandPatchReceived += (_, e) => Guard(() =>
        {
            if (WorldOf(gridKey, e.Simulator) is { } w) { w.SetPatch(e.X, e.Y, e.PatchSize, e.HeightMap); return; }
            lock (_lock)
                if (_unplaced.TryGetValue(e.Simulator, out var u)) u.Patches.Add((e.X, e.Y, e.PatchSize, e.HeightMap));
        });
    }

    /// <summary>Forget a client (after it's disposed).</summary>
    public void Unbind(GridClient client) => _clients.TryRemove(client, out _);

    /// <summary>Any logged-in client on a grid, preferring one in <paramref name="handle"/>'s region.</summary>
    public GridClient? ClientFor(string gridKey, ulong handle = 0)
    {
        GridClient? any = null;
        foreach (var (c, g) in _clients)
        {
            if (g != gridKey || !c.Network.Connected || c.Network.CurrentSim is null) continue;
            if (handle == 0 || c.Network.CurrentSim.Handle == handle) return c;
            any ??= c;
        }
        return any;
    }

    // ---------- shared assets ----------
    // Every client on a grid uses the same disk cache folder (Settings.AssetCache.Dir), so an asset fetched by one
    // agent is on disk for all. These calls also share in-flight downloads, so N agents (or a renderer) asking for
    // the same texture at once cost one download.

    /// <summary>A texture (J2K) by asset id, fetched through any agent on the grid.</summary>
    public async Task<AssetTexture?> TextureAsync(string gridKey, UUID id, CancellationToken ct = default) =>
        await Shared(gridKey, id, -1, async c => (object?)await c.Assets.RequestImageAsync(id, ImageType.Normal, ct)) as AssetTexture;

    /// <summary>A mesh asset by id.</summary>
    public async Task<AssetMesh?> MeshAsync(string gridKey, UUID id, CancellationToken ct = default) =>
        await Shared(gridKey, id, -2, async c => (object?)await c.Assets.RequestMeshAsync(id, ct)) as AssetMesh;

    /// <summary>Any other asset (sounds, animations, notecards readable without an item, ...).</summary>
    public async Task<Asset?> AssetAsync(string gridKey, UUID id, AssetType type, CancellationToken ct = default) =>
        await Shared(gridKey, id, (int)type, async c => (object?)await c.Assets.RequestAssetAsync(id, type, true, ct)) as Asset;

    async Task<object?> Shared(string gridKey, UUID id, int kind, Func<GridClient, Task<object?>> fetch)
    {
        var key = (gridKey, id, kind);
        var task = _inflight.GetOrAdd(key, _ => Task.Run(async () =>
        {
            try
            {
                var c = ClientFor(gridKey);
                return c is null ? null : await fetch(c);
            }
            finally { _inflight.TryRemove(key, out Task<object?>? _); }
        }));
        return await task;
    }

    RegionWorld? WorldOf(string gridKey, Simulator? sim) =>
        sim is null || sim.Handle == 0 ? null : _regions.TryGetValue((gridKey, sim.Handle), out var r) ? r : null;

    void Attach(string gridKey, Simulator sim)
    {
        RegionWorld world;
        bool created = false;
        lock (_lock)
        {
            if (sim.Handle == 0)
            {
                if (!_known.TryGetValue((gridKey, sim.IPEndPoint), out var k))
                {
                    _unplaced[sim] = new Unplaced(gridKey);
                    return;
                }
                Place(sim, k);
            }
            else _known[(gridKey, sim.IPEndPoint)] = (sim.Handle, sim.SizeX, sim.SizeY);
            if (!_regions.TryGetValue((gridKey, sim.Handle), out world!))
            {
                world = new RegionWorld(gridKey, sim.Handle, sim.SizeX, sim.SizeY);
                _regions[(gridKey, sim.Handle)] = world;
                created = true;
            }
            world.Attach(sim);
        }
        if (created) RegionChanged?.Invoke(world, true);
    }

    /// <summary>LibreMetaverse drops a connection attempt that fails without any event (two racing connects to one
    /// neighbour, from EnableSimulator and EstablishAgentCommunication, end that way), so check back once and let go
    /// of a simulator its client no longer has.</summary>
    async Task VerifyLater(string gridKey, Simulator sim)
    {
        await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Guard(() =>
        {
            // by reference in the client's list: FindSimulator matches endpoints, so with two Simulators for one region
            // (the race above) it names the other one, and the one still in use would be cut off from the shared world
            var net = sim.Client.Network;
            if (ReferenceEquals(net.CurrentSim, sim)) return;
            Simulator[] held;
            try { held = net.Simulators.ToArray(); }
            catch (ArgumentException) { return; }   // the list changed under us; it's checked again on disconnect
            if (!held.Any(s => ReferenceEquals(s, sim))) Detach(gridKey, sim);
        });
    }

    static readonly System.Reflection.FieldInfo? SelfLocalId =
        typeof(AgentManager).GetField("localID", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>The agent's own avatar as its current region has it. The viewer keeps one object for its avatar, moved
    /// from region to region; LibreMetaverse keeps a copy per region and two things go wrong at a crossing (or a
    /// teleport to a neighbour). It learns the avatar's local id only from a full update that arrives once the region
    /// is current, and on the main grid that update can come first: the id then stays the old region's, so terse
    /// updates no longer move Client.Self and the old region's kill of the old copy is skipped as "our own avatar".
    /// And a kill that's skipped or never sent (OpenSim sends none to the crossing agent) leaves the old copy in the
    /// old region for good when nobody else is there to fill its coarse list. Both copies were then drawn as the
    /// person's own body, sharing its pose, its worn state and its dead reckoning (2026-10-27: the body gone on the
    /// main grid after walking from Mirromere into Calas Galadhon; on the local grid a crossing soon after login left
    /// a copy at the border). So once the current region has the avatar: its local id is the one LibreMetaverse
    /// tracks, and every other region's copy goes, with what it wears.</summary>
    void SelfIn(string gridKey, GridClient c, Simulator? sim, Action<string>? log)
    {
        var current = c.Network.CurrentSim;
        if (sim is null || current is null || sim.Handle != current.Handle || WorldOf(gridKey, sim) is not { } here) return;
        if (here.AvatarById(c.Self.AgentID) is not { } me) return;
        if (c.Self.LocalID != me.LocalID && SelfLocalId != null)
        {
            log?.Invoke($"own avatar in {here.Name}: its local id was {c.Self.LocalID} (another region's), now {me.LocalID}");
            SelfLocalId.SetValue(c.Self, me.LocalID);
            if (me.ParentID == 0) c.Self.RelativePosition = me.Position;
        }
        foreach (var w in _regions.Values)
            if (w != here && w.GridKey == gridKey && w.RemoveAvatarsById(c.Self.AgentID) > 0)
                log?.Invoke($"own avatar in {here.Name}: dropped the copy left in {w.Name}");
    }

    static void Place(Simulator sim, (ulong Handle, uint SizeX, uint SizeY) k)
    {
        sim.Handle = k.Handle;
        if (k.SizeX != 0) { sim.SizeX = k.SizeX; sim.SizeY = k.SizeY; }
    }

    /// <summary>A region's handle from EnableSimulator; places a simulator that connected without one.</summary>
    void Learn(string gridKey, IPEndPoint ep, ulong handle, uint sizeX, uint sizeY)
    {
        if (handle == 0) return;
        List<(Simulator Sim, Unplaced U)> late = new();
        lock (_lock)
        {
            _known[(gridKey, ep)] = (handle, sizeX, sizeY);
            foreach (var (sim, u) in _unplaced)
                if (u.GridKey == gridKey && sim.IPEndPoint.Equals(ep)) late.Add((sim, u));
            foreach (var (sim, _) in late)
            {
                _unplaced.Remove(sim);
                Place(sim, (handle, sizeX, sizeY));
            }
        }
        foreach (var (sim, u) in late) AttachLate(gridKey, sim, u);
    }

    /// <summary>Joins a simulator to the shared world after it has already received objects and terrain on its own.</summary>
    void AttachLate(string gridKey, Simulator sim, Unplaced u)
    {
        var (prims, avatars, ids) = (sim.ObjectsPrimitives, sim.ObjectsAvatars, sim.GlobalToLocalID);
        Attach(gridKey, sim);   // swaps the shared dictionaries in; what arrived before is copied over below
        if (WorldOf(gridKey, sim) is not { } w) return;
        foreach (var (id, local) in ids) w.GlobalToLocal.TryAdd(id, local);
        foreach (var (local, a) in avatars) if (w.Avatars.TryAdd(local, a)) w.Raise(WorldChange.Added, local);
        foreach (var (local, p) in prims) if (w.Prims.TryAdd(local, p)) w.Raise(WorldChange.Added, local);
        foreach (var (x, y, size, heights) in u.Patches) w.SetPatch(x, y, size, heights);
    }

    void Detach(string gridKey, Simulator sim)
    {
        RegionWorld? dropped = null;
        lock (_lock)
        {
            if (_unplaced.Remove(sim)) return;
            if (_regions.TryGetValue((gridKey, sim.Handle), out var world) && world.Detach(sim))
            {
                // Nobody is left to receive kills, so whatever is in here would go stale; the next arrival
                // gets a full resend from the region anyway.
                _regions.TryRemove((gridKey, sim.Handle), out _);
                dropped = world;
            }
        }
        if (dropped is not null) RegionChanged?.Invoke(dropped, false);
    }

    // Each avatar's last AvatarAppearance. An avatar's object can be made again (or updated) after its appearance came,
    // which leaves it without bakes: on the local grid's NPCs several in forty stayed clouds for good. The viewer keeps
    // an avatar's appearance across its object updates; so do we.
    readonly ConcurrentDictionary<UUID, (Primitive.TextureEntryFace? Default, Primitive.TextureEntryFace?[] Faces, byte[] Params)> _appearances = new();

    // An appearance reaches the copy of the avatar in the region that sent it; a copy in another region (the avatar
    // crossed, or the message came over the old region's circuit after it did) gets it here. LibreMetaverse drops the
    // agent's own appearance when its outfit version isn't newer than the last it took, from whichever region, so the
    // copy a crossing makes would otherwise keep whatever its object update carried.
    void RememberAppearance(string gridKey, AvatarAppearanceEventArgs e)
    {
        if (e.FaceTextures is not { Length: > 0 } faces) return;
        var vps = e.VisualParams?.ToArray() ?? Array.Empty<byte>();
        // a texture-only update (Second Life's after a rebake: new bakes, one placeholder parameter) keeps the shape the
        // avatar had, as the viewer's processAvatarAppearance does; LibreMetaverse has just written the placeholder over
        // it, which left the person's own avatar a cloud with its bakes never asked for (2026-10-27, outfit version 405)
        if (vps.Length <= 1 && _appearances.TryGetValue(e.AvatarID, out var before) && before.Params.Length > 1) vps = before.Params;
        var entry = (e.DefaultTexture, faces.ToArray(), vps);
        _appearances[e.AvatarID] = entry;
        foreach (var w in _regions.Values)
        {
            if (w.GridKey != gridKey) continue;
            foreach (var av in w.Avatars.Values)
                if (av.ID == e.AvatarID && !ReferenceEquals(w.Avatars, e.Simulator.ObjectsAvatars)) Apply(av, entry, force: true);
        }
        foreach (var av in e.Simulator.ObjectsAvatars.Values)
            if (av.ID == e.AvatarID) Apply(av, entry, force: false);
        if (_appearances.Count > 4096) _appearances.Clear();
    }

    /// <summary>The agent's own appearance as the appearance service answered a server-baked request (its visual params
    /// and, once baked, its bakes): remembered and put on every copy of its avatar, so the avatar never depends on an
    /// AvatarAppearance for itself, which LibreMetaverse drops when the outfit version isn't newer.</summary>
    public void OwnAppearance(string gridKey, UUID avatar, UUID[]? textures, byte[]? visualParams)
    {
        _appearances.TryGetValue(avatar, out var before);
        var vps = visualParams is { Length: > 1 } ? visualParams : before.Params ?? Array.Empty<byte>();
        bool baked = textures is { Length: > 10 } && new[] { 8, 9, 10 }.All(i => textures[i] != UUID.Zero && textures[i] != ImgDefaultAvatar);
        Primitive.TextureEntryFace? def = before.Default;
        var faces = before.Faces;
        if (baked)
        {
            faces = new Primitive.TextureEntryFace?[textures!.Length];
            for (int i = 0; i < textures.Length; i++)
                if (textures[i] != UUID.Zero) faces[i] = new Primitive.TextureEntryFace(null) { TextureID = textures[i] };
            def = null;
        }
        if (faces == null || vps.Length <= 1) return;   // nothing to show yet
        var entry = (def, faces, vps);
        _appearances[avatar] = entry;
        foreach (var w in _regions.Values)
        {
            if (w.GridKey != gridKey) continue;
            foreach (var av in w.Avatars.Values)
                if (av.ID == avatar) Apply(av, entry, force: baked);
        }
    }

    /// <summary>A full object update replaces an avatar's textures with what it carries, which can be the default
    /// avatar texture in the bake slots (OpenSim always; a new region's copy): those don't count as bakes.</summary>
    void RestoreAppearance(Avatar av)
    {
        if (!_appearances.TryGetValue(av.ID, out var entry)) return;
        if (!HasBakes(av) || av.VisualParameters is not { Length: > 1 }) Apply(av, entry, force: false);
    }

    static readonly UUID ImgDefaultAvatar = new("c228d1cf-4b5d-4ba8-84f4-899a0796aa97");
    static readonly UUID ImgDefault = new("d2114404-dd59-4a4d-8e6c-49359e91bbf0");

    /// <summary>Whether the head, upper or lower body slot names a real bake (the renderer's test for an appearance).</summary>
    static bool HasBakes(Avatar av)
    {
        var t = av.Textures;
        if (t?.FaceTextures is not { Length: > 10 } ft) return false;
        foreach (int te in new[] { 8, 9, 10 })
            if ((ft[te]?.TextureID ?? t.DefaultTexture?.TextureID) is { } id && id != UUID.Zero && id != ImgDefaultAvatar && id != ImgDefault)
                return true;
        return false;
    }

    static void Apply(Avatar av, (Primitive.TextureEntryFace? Default, Primitive.TextureEntryFace?[] Faces, byte[] Params) e, bool force)
    {
        lock (av)
        {
            if (force || !HasBakes(av))
            {
                var te = new Primitive.TextureEntry(e.Default?.TextureID ?? UUID.Zero);
                for (int i = 0; i < e.Faces.Length; i++)
                    if (e.Faces[i] is { } f) te.CreateFace((uint)i).TextureID = f.TextureID;
                av.Textures = te;
            }
            if (e.Params.Length > 1 && (force || av.VisualParameters is not { Length: > 1 })) av.VisualParameters = e.Params;
        }
    }

    static void Guard(Action a)
    {
        try { a(); } catch { /* world bookkeeping must never take down a network thread */ }
    }
}
