using System.Collections.Concurrent;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Map;

/// <summary>A thing on the map: global metres (x east, y north).</summary>
public readonly record struct MapMark(MarkKind Kind, double X, double Y, string? Name = null, int Count = 0, int Price = 0, int Area = 0, UUID Id = default);

public enum MarkKind { Person, Friend, Crowd, LandForSale, Event, Telehub }

/// <summary>A region known to the map (MapBlocks): its grid corner in metres and name.</summary>
public readonly record struct MapRegion(ulong Handle, double X, double Y, string Name, string Rating, int Size);

/// <summary>
/// What the map shows besides its tiles, gathered as the viewer's world map gathers it (llworldmap.cpp): regions' names
/// and ratings by MapBlocks for the area in view; land for sale, events and telehubs grid-wide (MapItemRequest, once
/// the board opens and every five minutes while it's up); how many people are in each region in view, a region at a
/// time and at most once a minute each (AGENTS_UPDATE_TIMER); friends who let you see them on the map (FindAgent,
/// every 30 s while it's up); and, close up, everyone in the regions around you by their coarse positions
/// (CoarseLocationUpdate), as the mini-map (llnetmap.cpp) draws them. Asked for only while something shows the map.
/// </summary>
public sealed class WorldMap
{
    readonly AgentSession _s;
    readonly ConcurrentDictionary<long, DateTime> _blocksAsked = new();
    readonly ConcurrentDictionary<ulong, DateTime> _peopleAsked = new();
    readonly ConcurrentDictionary<ulong, GridRegion> _regions = new();
    readonly ConcurrentDictionary<ulong, List<MapMark>> _people = new();
    readonly ConcurrentDictionary<UUID, (ulong Handle, Vector3 At, DateTime When)> _friends = new();
    volatile List<MapMark> _items = new();
    DateTime _itemsAsked = DateTime.MinValue, _friendsAsked = DateTime.MinValue;
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<GridClient, object> _hooked = new();
    int _itemsBusy;

    /// <summary>Something new arrived (the board redraws).</summary>
    public event Action? Changed;

    public WorldMap(AgentSession s) => _s = s;

    GridClient? C => _s.Client;

    void Hook(GridClient c)
    {
        if (_hooked.TryGetValue(c, out _)) return;
        _hooked.Add(c, new object());
        c.Friends.FriendFoundReply += (_, e) =>
        {
            if (e.RegionHandle == 0) { _friends.TryRemove(e.AgentID, out var _gone); return; }
            _friends[e.AgentID] = (e.RegionHandle, e.Location, DateTime.UtcNow);
            Changed?.Invoke();
        };
        // (LibreMetaverse keeps only the regions it was asked for by name or handle; MapBlockReply's are kept here)
        c.Grid.GridRegion += (_, e) =>
        {
            if (e.Region.RegionHandle == 0 || string.IsNullOrEmpty(e.Region.Name)) return;
            _regions[e.Region.RegionHandle] = e.Region;
            Changed?.Invoke();
        };
    }

    /// <summary>The map is up and shows this rectangle of global metres: ask for what it lacks there. Any thread.</summary>
    public void Want(double west, double south, double east, double north, bool people)
    {
        if (C is not { } c) return;
        Hook(c);
        var now = DateTime.UtcNow;
        // regions' names: MapBlocks in 16-region squares, each asked at most every five minutes
        int gx0 = (int)Math.Floor(west / 256), gy0 = (int)Math.Floor(south / 256), gx1 = (int)Math.Floor(east / 256), gy1 = (int)Math.Floor(north / 256);
        for (int bx = gx0 >> 4; bx <= gx1 >> 4; bx++)
            for (int by = gy0 >> 4; by <= gy1 >> 4; by++)
            {
                long key = ((long)bx << 32) | (uint)by;
                if (_blocksAsked.TryGetValue(key, out var at) && now - at < TimeSpan.FromMinutes(5)) continue;
                if (bx < 0 || by < 0) continue;
                _blocksAsked[key] = now;
                try { c.Grid.RequestMapBlocks(GridLayerType.Objects, (ushort)(bx << 4), (ushort)(by << 4), (ushort)((bx << 4) + 15), (ushort)((by << 4) + 15), false); }
                catch { }
            }
        // land for sale, events, telehubs: grid-wide
        if (now - _itemsAsked > TimeSpan.FromMinutes(5) && Interlocked.Exchange(ref _itemsBusy, 1) == 0)
        {
            _itemsAsked = now;
            _ = Task.Run(ItemsAsync);
        }
        // friends on the map
        if (now - _friendsAsked > TimeSpan.FromSeconds(30))
        {
            _friendsAsked = now;
            foreach (var f in c.Friends.FriendList.Values)
                if (f.IsOnline && f.CanSeeThemOnMap)
                    try { c.Friends.MapFriend(f.UUID); } catch { }
        }
        // how many people where: the regions in view, when few enough are
        if (people && (gx1 - gx0 + 1) * (gy1 - gy0 + 1) <= 64)
            for (int gx = gx0; gx <= gx1; gx++)
                for (int gy = gy0; gy <= gy1; gy++)
                {
                    if (gx < 0 || gy < 0) continue;
                    ulong h = Utils.UIntsToLong((uint)gx * 256, (uint)gy * 256);
                    if (!TryRegion(c, h, out _)) continue;
                    if (_peopleAsked.TryGetValue(h, out var pa) && now - pa < TimeSpan.FromSeconds(60)) continue;
                    _peopleAsked[h] = now;
                    _ = Task.Run(() => PeopleAsync(h));
                }
    }

    async Task ItemsAsync()
    {
        try
        {
            var all = new List<MapMark>();
            foreach (var type in new[] { GridItemType.LandForSale, GridItemType.AdultLandForSale, GridItemType.PgEvent, GridItemType.MatureEvent, GridItemType.AdultEvent, GridItemType.Telehub })
            {
                List<MapItem> got;
                try { got = await _s.LandMap.ItemsAsync(type, 0, CancellationToken.None); }
                catch { continue; }
                foreach (var i in got)
                    all.Add(i switch
                    {
                        MapLandForSale l => new MapMark(MarkKind.LandForSale, i.GlobalX, i.GlobalY, l.Name, Price: l.Price, Area: l.Size, Id: l.ID),
                        MapAdultLandForSale l => new MapMark(MarkKind.LandForSale, i.GlobalX, i.GlobalY, l.Name, Price: l.Price, Area: l.Size, Id: l.ID),
                        MapPGEvent e => new MapMark(MarkKind.Event, i.GlobalX, i.GlobalY, e.Description),
                        MapMatureEvent e => new MapMark(MarkKind.Event, i.GlobalX, i.GlobalY, e.Description),
                        MapAdultEvent e => new MapMark(MarkKind.Event, i.GlobalX, i.GlobalY, e.Description),
                        MapTelehub t => new MapMark(MarkKind.Telehub, i.GlobalX, i.GlobalY, "telehub"),
                        _ => new MapMark(MarkKind.Telehub, i.GlobalX, i.GlobalY),
                    });
                _items = new List<MapMark>(all);
                Changed?.Invoke();
            }
        }
        finally { Interlocked.Exchange(ref _itemsBusy, 0); }
    }

    async Task PeopleAsync(ulong handle)
    {
        try
        {
            var got = await _s.LandMap.ItemsAsync(GridItemType.AgentLocations, handle, CancellationToken.None);
            _people[handle] = got.OfType<MapAgentLocation>().Where(a => a.AvatarCount > 0)
                .Select(a => new MapMark(MarkKind.Crowd, a.GlobalX, a.GlobalY, Count: a.AvatarCount)).ToList();
            Changed?.Invoke();
        }
        catch { }
    }

    IEnumerable<GridRegion> Known(GridClient c)
    {
        foreach (var r in _regions.Values) yield return r;
        foreach (var (h, r) in c.Grid.RegionsByHandleReadOnly) if (!_regions.ContainsKey(h)) yield return r;
    }

    bool TryRegion(GridClient c, ulong h, out GridRegion r) => _regions.TryGetValue(h, out r) || c.Grid.RegionsByHandleReadOnly.TryGetValue(h, out r);

    /// <summary>A region's name by handle, if the map knows it.</summary>
    public string? NameOf(ulong handle) => C is { } c && TryRegion(c, handle, out var r) && r.Name is { Length: > 0 } n ? n : null;

    /// <summary>The regions the grid has told us of.</summary>
    public List<MapRegion> Regions()
    {
        var list = new List<MapRegion>();
        if (C is not { } c) return list;
        foreach (var r in Known(c))
        {
            if (string.IsNullOrEmpty(r.Name)) continue;
            Utils.LongToUInts(r.RegionHandle, out var x, out var y);
            list.Add(new MapRegion(r.RegionHandle, x, y, r.Name, Where.Rating((SimAccess)r.Access), 256));
        }
        return list;
    }

    /// <summary>The region at a point of global metres, if the map knows it.</summary>
    public MapRegion? RegionAt(double x, double y)
    {
        if (C is not { } c || x < 0 || y < 0) return null;
        ulong h = Utils.UIntsToLong((uint)(Math.Floor(x / 256) * 256), (uint)(Math.Floor(y / 256) * 256));
        if (!TryRegion(c, h, out var r) || string.IsNullOrEmpty(r.Name))
        {
            // a bigger region (var-region) whose corner is to the south-west: the region we're in knows its own size
            if (c.Network.CurrentSim is { } sim)
            {
                Utils.LongToUInts(sim.Handle, out var sx, out var sy);
                if (x >= sx && y >= sy && x < sx + sim.SizeX && y < sy + sim.SizeY) return new MapRegion(sim.Handle, sx, sy, sim.Name, Where.Rating(sim.Access), (int)sim.SizeX);
            }
            return null;
        }
        Utils.LongToUInts(h, out var rx, out var ry);
        return new MapRegion(h, rx, ry, r.Name, Where.Rating((SimAccess)r.Access), 256);
    }

    /// <summary>Everything to mark, as of now: the items, people (coarse positions close up, counts farther), friends.</summary>
    public List<MapMark> Marks(bool closeUp)
    {
        var marks = new List<MapMark>(_items);
        if (C is not { } c) return marks;
        var friends = c.Friends.FriendList;
        var me = c.Self.AgentID;
        var seen = new HashSet<UUID>();
        if (closeUp)
            foreach (var r in _s.World.Regions)
            {
                if (r.GridKey != _s.GridKey) continue;
                Utils.LongToUInts(r.Handle, out var rx, out var ry);
                foreach (var (id, p) in r.Coarse)
                {
                    if (id == me || !seen.Add(id)) continue;
                    bool friend = friends.ContainsKey(id);
                    marks.Add(new MapMark(friend ? MarkKind.Friend : MarkKind.Person, rx + p.X, ry + p.Y, friend ? friends[id].Name : null, Id: id));
                }
            }
        else
            foreach (var list in _people.Values) marks.AddRange(list);
        var stale = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        foreach (var (id, f) in _friends)
        {
            if (seen.Contains(id) || f.When < stale || !friends.TryGetValue(id, out var fi) || !fi.IsOnline) continue;
            Utils.LongToUInts(f.Handle, out var fx, out var fy);
            marks.Add(new MapMark(MarkKind.Friend, fx + f.At.X, fy + f.At.Y, fi.Name, Id: id));
        }
        return marks;
    }

    /// <summary>Where the agent is in global metres, and which way it faces (radians from east, counter-clockwise).</summary>
    public (double X, double Y, float Z, float Heading)? Me()
    {
        if (C is not { Network.CurrentSim: { } sim } c) return null;
        Utils.LongToUInts(sim.Handle, out var x, out var y);
        var p = c.Self.SimPosition;
        var fwd = Vector3.UnitX * c.Self.SimRotation;
        return (x + p.X, y + p.Y, p.Z, MathF.Atan2(fwd.Y, fwd.X));
    }
}
