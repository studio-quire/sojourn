using System.Collections.Concurrent;
using LibreMetaverse;

namespace Sojourn.Core.World;

public sealed class PropsMeta
{
    public DateTime FetchedAt;
    public DateTime RequestedAt;
    public bool Full;
    /// <summary>Creation time (unix seconds) from the raw ObjectProperties (LibreMetaverse truncates the microseconds).</summary>
    public long Created;
}

public enum WorldChange { Added, Updated, Moved, Removed, Properties, Terrain }

/// <summary>
/// The shared view of one region: every agent session in the region writes into the same object dictionaries,
/// so objects and avatars exist once in memory, as LibreMetaverse's own <see cref="Primitive"/> and
/// <see cref="Avatar"/> instances. See docs/interfaces.md for the contract the renderer builds on.
///
/// How: when a session connects to a simulator, <see cref="WorldHub"/> swaps that <see cref="Simulator"/>'s
/// ObjectsPrimitives / ObjectsAvatars / GlobalToLocalID for this region's shared dictionaries before the first
/// packet arrives. LibreMetaverse never clears those dictionaries itself (only KillObject removes entries), so a
/// session leaving the region doesn't disturb the others; the region's world is dropped when its last session leaves.
/// </summary>
public sealed class RegionWorld
{
    public string GridKey { get; }
    public ulong Handle { get; }

    /// <summary>Objects by local id (prims, trees, grass; attachments too, parented to their avatar's local id).</summary>
    public ConcurrentDictionary<uint, Primitive> Prims { get; } = new();
    /// <summary>Avatars by local id, the observing agents included.</summary>
    public ConcurrentDictionary<uint, Avatar> Avatars { get; } = new();
    public ConcurrentDictionary<UUID, uint> GlobalToLocal { get; } = new();

    /// <summary>When each object's properties were last fetched (by any agent) and whether in full (from a selection).
    /// The properties themselves are on the prim (<see cref="Primitive.Properties"/>).</summary>
    public ConcurrentDictionary<UUID, PropsMeta> PropsMeta { get; } = new();

    /// <summary>Every avatar in the region with its coarse (map) position, from CoarseLocationUpdate.</summary>
    public IReadOnlyDictionary<UUID, Vector3> Coarse { get; internal set; } = new Dictionary<UUID, Vector3>();

    /// <summary>Terrain heights, row-major [y * SizeX + x], metres; NaN where no patch has arrived yet.</summary>
    public float[] Heightmap { get; private set; }

    readonly object _simLock = new();
    readonly List<Simulator> _sims = new();
    // the same list as an array, replaced (never changed) under the lock: the renderer asks for the simulator many times
    // a frame, and a lock shared with the network threads made it wait (Monitor's slow path in profiles)
    volatile Simulator[] _simArray = [];
    long _version;

    internal RegionWorld(string gridKey, ulong handle, uint sizeX, uint sizeY)
    {
        GridKey = gridKey;
        Handle = handle;
        SizeX = sizeX == 0 ? 256 : sizeX;
        SizeY = sizeY == 0 ? 256 : sizeY;
        Heightmap = NewHeightmap(SizeX, SizeY);
    }

    public uint SizeX { get; private set; }
    public uint SizeY { get; private set; }

    /// <summary>Region name, once a session's handshake has told it.</summary>
    public string Name => Simulator?.Name ?? "";

    /// <summary>Bumped on every change; a renderer can cache against it.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Raised after an object, avatar or terrain change, from the network thread that saw it.
    /// Handlers must be quick and must not throw.</summary>
    public event Action<RegionWorld, WorldChange, uint>? Changed;

    /// <summary>A live simulator connection to this region (any session's), for region metadata: water height,
    /// terrain textures and height ranges, region flags, parcels. Null if none is connected right now.</summary>
    public Simulator? Simulator
    {
        get
        {
            var sims = _simArray;
            foreach (var s in sims) if (s.Connected) return s;
            return sims.Length > 0 ? sims[0] : null;
        }
    }

    public int SessionCount => _simArray.Length;

    public IReadOnlyList<Simulator> Simulators => _simArray;

    static float[] NewHeightmap(uint sx, uint sy)
    {
        var h = new float[sx * sy];
        Array.Fill(h, float.NaN);
        return h;
    }

    internal void Attach(Simulator sim)
    {
        lock (_simLock)
        {
            // Simulator.Equals compares endpoints, so two agents' connections to one region are "equal": compare references.
            if (_sims.Any(x => ReferenceEquals(x, sim))) return;
            if (sim.SizeX != 0 && (sim.SizeX != SizeX || sim.SizeY != SizeY))
            {
                SizeX = sim.SizeX;
                SizeY = sim.SizeY;
                Heightmap = NewHeightmap(SizeX, SizeY);
            }
            _sims.Add(sim);
            _simArray = _sims.ToArray();
        }
        sim.ObjectsPrimitives = Prims;
        sim.ObjectsAvatars = Avatars;
        sim.GlobalToLocalID = GlobalToLocal;
    }

    /// <summary>Returns true when that was the last session here.</summary>
    internal bool Detach(Simulator sim)
    {
        // Late packets on the departing connection (a kill, an update) must not touch the shared state.
        sim.ObjectsPrimitives = new ConcurrentDictionary<uint, Primitive>();
        sim.ObjectsAvatars = new ConcurrentDictionary<uint, Avatar>();
        sim.GlobalToLocalID = new ConcurrentDictionary<UUID, uint>();
        lock (_simLock)
        {
            _sims.RemoveAll(x => ReferenceEquals(x, sim));
            _simArray = _sims.ToArray();
            return _sims.Count == 0;
        }
    }

    internal void SetPatch(int px, int py, int patchSize, float[] heights)
    {
        var map = Heightmap;
        for (var y = 0; y < patchSize; y++)
        {
            var gy = py * patchSize + y;
            if (gy >= SizeY) break;
            for (var x = 0; x < patchSize; x++)
            {
                var gx = px * patchSize + x;
                if (gx >= SizeX) break;
                map[gy * SizeX + gx] = heights[y * patchSize + x];
            }
        }
        Raise(WorldChange.Terrain, (uint)(py << 16 | px));
    }

    /// <summary>Terrain height at a region point (nearest sample), or null if unknown.</summary>
    public float? TerrainHeight(float x, float y)
    {
        var ix = (int)Math.Clamp(x, 0, SizeX - 1);
        var iy = (int)Math.Clamp(y, 0, SizeY - 1);
        var h = Heightmap[iy * SizeX + ix];
        return float.IsNaN(h) ? null : h;
    }

    // A copy of an avatar is dropped as a ghost once the region's own list has left it out for AbsentGrace, or another
    // region's has named it (root there) for as long, but never in its first ArrivalGrace here: one that has just come
    // in over a border can be missing from this region's list, and still on the one it left, for a few seconds of the
    // handover. Dropped wrongly it doesn't come back (LibreMetaverse remakes it without its id from the next terse
    // update, and one seated gets none), and on 0.1.0.996 a driver ahead vanished that way, as far as the log could tell
    // (docs/access-app.md > Driving after someone).
    readonly ConcurrentDictionary<uint, DateTime> _absentSince = new();
    readonly ConcurrentDictionary<uint, (ulong From, DateTime Since)> _elsewhereSince = new();
    readonly ConcurrentDictionary<uint, DateTime> _firstSeen = new();
    readonly ConcurrentDictionary<UUID, (long At, string Why)> _dropped = new();
    static readonly TimeSpan AbsentGrace = TimeSpan.FromSeconds(4), ArrivalGrace = TimeSpan.FromSeconds(12);

    /// <summary>When the last CoarseLocationUpdate came (Environment.TickCount64; 0: none yet).</summary>
    public long CoarseAt { get; private set; }

    /// <summary>OpenSim lists at most 60 (SceneGraph.GetCoarseLocations); a list that long may have left people out.</summary>
    public const int ListCap = 60;

    /// <summary>The region's map list can be taken to name everyone in it: it has come, isn't empty (an empty one says
    /// nothing reliable) and isn't as long as a list can be.</summary>
    public bool ListComplete => Coarse.Count is > 0 and < ListCap;

    /// <summary>A CoarseLocationUpdate: every root agent in the region. In-view avatars missing from it for a few
    /// seconds have left (a teleport out sometimes leaves no KillObject that reaches the shared world, and the
    /// ghost then shows in `people` and `look`), so they and their attachments are dropped.</summary>
    internal void SetCoarse(IReadOnlyDictionary<UUID, Vector3> positions)
    {
        Coarse = positions;
        CoarseAt = Environment.TickCount64;
        Forget(_absentSince);
        Forget(_firstSeen);
        foreach (var local in _elsewhereSince.Keys) if (!Avatars.ContainsKey(local)) _elsewhereSince.TryRemove(local, out _);
        if (!ListComplete) return;  // an empty update says nothing reliable, nor one cut at the cap about who's missing
        var now = DateTime.UtcNow;
        foreach (var (local, av) in Avatars)
        {
            var first = _firstSeen.GetOrAdd(local, now);
            if (positions.ContainsKey(av.ID)) { _absentSince.TryRemove(local, out _); _elsewhereSince.TryRemove(local, out _); continue; }
            var since = _absentSince.GetOrAdd(local, now);
            if (now - since < AbsentGrace || now - first < ArrivalGrace) continue;
            RemoveAvatar(local, "left out of its region's list");
        }
    }

    void Forget(ConcurrentDictionary<uint, DateTime> stamps)
    {
        foreach (var local in stamps.Keys) if (!Avatars.ContainsKey(local)) stamps.TryRemove(local, out _);
    }

    /// <summary>Another region's CoarseLocationUpdate: the avatars it lists are root there, so a copy of one of them
    /// here that this region's own list doesn't name for a few seconds is a ghost an avatar left behind crossing out
    /// (the kill never reached us). Covers the case <see cref="SetCoarse"/> can't: a region whose list is empty,
    /// because nobody else is in it.</summary>
    internal void RootElsewhere(IReadOnlyDictionary<UUID, Vector3> there, ulong from)
    {
        var now = DateTime.UtcNow;
        foreach (var (local, av) in Avatars)
        {
            var first = _firstSeen.GetOrAdd(local, now);
            if (!there.ContainsKey(av.ID) || Coarse.ContainsKey(av.ID))
            {
                // (only that region's word takes back what its word started: another region not naming them says nothing)
                if (_elsewhereSince.TryGetValue(local, out var was) && (was.From == from || Coarse.ContainsKey(av.ID))) _elsewhereSince.TryRemove(local, out _);
                continue;
            }
            var since = _elsewhereSince.GetOrAdd(local, (from, now)).Since;
            if (now - since >= AbsentGrace && now - first >= ArrivalGrace) RemoveAvatar(local, "named by another region's list");
        }
    }

    /// <summary>The copy came into this region within the last ArrivalGrace (or since the last map list), so its region's
    /// list leaving it out may only be the handover still going on, not a copy left behind.</summary>
    public bool JustArrived(Avatar av) => !_firstSeen.TryGetValue(av.LocalID, out var t) || DateTime.UtcNow - t < ArrivalGrace;

    /// <summary>When and why a copy of this avatar here was last dropped as a ghost (within the last two minutes).</summary>
    public (long At, string Why)? Dropped(UUID id) =>
        _dropped.TryGetValue(id, out var d) && Environment.TickCount64 - d.At < 120_000 ? d : null;

    /// <summary>Drop every copy of an avatar here (by its agent id), with what it wears.</summary>
    internal int RemoveAvatarsById(UUID id)
    {
        int n = 0;
        foreach (var (local, av) in Avatars)
            if (av.ID == id && RemoveAvatar(local, "the own avatar, now in another region")) n++;
        return n;
    }

    /// <summary>An avatar and everything attached to it (each attachment's whole linkset), as a kill would remove them.</summary>
    bool RemoveAvatar(uint local, string why)
    {
        _absentSince.TryRemove(local, out _);
        _elsewhereSince.TryRemove(local, out _);
        _firstSeen.TryRemove(local, out _);
        if (!Avatars.TryRemove(local, out var av)) return false;
        long t = Environment.TickCount64;
        _dropped[av.ID] = (t, why);
        if (_dropped.Count > 64) foreach (var (id, d) in _dropped) if (t - d.At > 120_000) _dropped.TryRemove(id, out _);
        if (GlobalToLocal.TryGetValue(av.ID, out var mapped) && mapped == local) GlobalToLocal.TryRemove(av.ID, out _);
        var parents = new HashSet<uint> { local };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (pl, prim) in Prims)
                if (parents.Contains(prim.ParentID) && parents.Add(pl)) grew = true;
        }
        parents.Remove(local);
        foreach (var pl in parents)
            if (Prims.TryRemove(pl, out var prim))
            {
                if (GlobalToLocal.TryGetValue(prim.ID, out var m) && m == pl) GlobalToLocal.TryRemove(prim.ID, out _);
                Raise(WorldChange.Removed, pl);
            }
        Raise(WorldChange.Removed, local);
        return true;
    }

    /// <summary>A prim and every prim under it (its linkset, or an attachment's), as a kill would remove them; avatars
    /// sitting on it stay. How many prims went.</summary>
    internal int RemoveObject(uint local)
    {
        if (!Prims.ContainsKey(local)) return 0;
        var gone = new HashSet<uint> { local };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (pl, prim) in Prims)
                if (gone.Contains(prim.ParentID) && gone.Add(pl)) grew = true;
        }
        int n = 0;
        foreach (var pl in gone)
            if (Prims.TryRemove(pl, out var prim))
            {
                if (GlobalToLocal.TryGetValue(prim.ID, out var m) && m == pl) GlobalToLocal.TryRemove(prim.ID, out _);
                Raise(WorldChange.Removed, pl);
                n++;
            }
        return n;
    }

    internal void Raise(WorldChange change, uint localId)
    {
        Interlocked.Increment(ref _version);
        var h = Changed;
        if (h is null) return;
        try { h(this, change, localId); }
        catch { /* a renderer bug must not break the network thread */ }
    }

    public Primitive? PrimById(UUID id) =>
        GlobalToLocal.TryGetValue(id, out var local) && Prims.TryGetValue(local, out var p) ? p : null;

    public Avatar? AvatarById(UUID id)
    {
        if (GlobalToLocal.TryGetValue(id, out var local) && Avatars.TryGetValue(local, out var a)) return a;
        foreach (var (_, av) in Avatars) if (av.ID == id) return av;   // (Values would copy them all first)
        return null;
    }

    /// <summary>A prim's position in region coordinates, walking up its link and attachment parents.</summary>
    public Vector3 RegionPosition(Primitive p)
    {
        TryRegionPosition(p, out var pos);
        return pos;
    }

    /// <summary>As <see cref="RegionPosition"/>, false when a parent on the way isn't here (a linkset's child that came
    /// before its root, as after a crossing): the position is then only its offset from that parent.</summary>
    public bool TryRegionPosition(Primitive p, out Vector3 pos)
    {
        pos = p.Position;
        var cur = p;
        for (var depth = 0; cur.ParentID != 0 && depth < 8; depth++)
        {
            if (Prims.TryGetValue(cur.ParentID, out var parent))
            {
                pos = parent.Position + pos * parent.Rotation;
                cur = parent;
            }
            else if (Avatars.TryGetValue(cur.ParentID, out var av))
            {
                pos = av.Position + pos * av.Rotation;
                cur = av;
            }
            else return false;
        }
        return true;
    }

    public Quaternion RegionRotation(Primitive p)
    {
        var rot = p.Rotation;
        var cur = p;
        for (var depth = 0; cur.ParentID != 0 && depth < 8; depth++)
        {
            if (Prims.TryGetValue(cur.ParentID, out var parent)) { rot = rot * parent.Rotation; cur = parent; }
            else if (Avatars.TryGetValue(cur.ParentID, out var av)) { rot = rot * av.Rotation; cur = av; }
            else break;
        }
        return rot;
    }
}
