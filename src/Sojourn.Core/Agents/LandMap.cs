using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Land seen from above: the person's land holdings (the viewer's My Land Holdings: PlacesQuery for parcels I own, and
/// my groups' land contributions; a group's land as its Land &amp; L$ tab asks), and the world map's items
/// (MapItemRequest: land for sale, events, telehubs, how many people are where), each placed in its region with a
/// `go_to` the go tool takes. The map board to come (batch B8) reads <see cref="ItemsAsync"/>. Claude Opus 5.5,
/// 2026-10-28 (batch B5).
/// </summary>
public sealed class LandMap
{
    readonly AgentSession _s;
    public LandMap(AgentSession s) => _s = s;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");
    readonly SemaphoreSlim _places = new(1, 1);

    static string GoTo(string region, double gx, double gy, double gz = 0) =>
        $"{region} {Math.Floor(gx % 256)},{Math.Floor(gy % 256)},{Math.Max(0, Math.Floor(gz))}";

    /// <summary>PlacesQuery's replies for one query (parcels I or a group own), gathered until the grid is quiet.</summary>
    async Task<List<DirectoryManager.PlacesSearchData>> OwnedAsync(UUID group, CancellationToken ct)
    {
        var c = C;
        await _places.WaitAsync(ct);
        try
        {
            var got = new List<DirectoryManager.PlacesSearchData>();
            var transaction = UUID.Random();
            var last = DateTime.MaxValue;
            // the reply carries the query id over the event queue and the transaction id over UDP
            void On(object? _, PlacesReplyEventArgs e)
            {
                if (e.QueryID != transaction && e.QueryID != group) return;
                lock (got) { got.AddRange(e.MatchedPlaces.Where(p => !string.IsNullOrEmpty(p.Name) || p.ActualArea > 0)); last = DateTime.UtcNow; }
            }
            c.Directory.PlacesReply += On;
            try
            {
                c.Directory.StartPlacesSearch(group == UUID.Zero ? DirectoryManager.DirFindFlags.AgentOwned : DirectoryManager.DirFindFlags.GroupOwned,
                    ParcelCategory.Any, "", "", group, transaction);
                var end = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                while (DateTime.UtcNow < end)
                {
                    await Task.Delay(150, ct);
                    lock (got) if (last != DateTime.MaxValue && DateTime.UtcNow - last > TimeSpan.FromSeconds(1.2)) break;
                }
            }
            finally { c.Directory.PlacesReply -= On; }
            lock (got) return got.ToList();
        }
        finally { _places.Release(); }
    }

    static JsonObject Place(DirectoryManager.PlacesSearchData p) => new()
    {
        ["name"] = p.Name, ["region"] = p.SimName, ["area"] = p.ActualArea, ["billable_area"] = p.BillableArea,
        ["go_to"] = GoTo(p.SimName, p.GlobalX, p.GlobalY, p.GlobalZ),
    };

    /// <summary>My Land Holdings: parcels I own, what my groups' land takes from my allowance; with `group`, that
    /// group's land instead.</summary>
    public async Task<JsonObject> HoldingsAsync(JsonObject a, CancellationToken ct)
    {
        if (_s.Parcels.GroupArg(a) is { } gid)
        {
            var land = await OwnedAsync(gid, ct);
            return new JsonObject
            {
                ["group"] = _s.State.Groups.TryGetValue(gid, out var g) ? g.Name : gid.ToString(),
                ["parcels"] = new JsonArray(land.Select(p => (JsonNode)Place(p)).ToArray()), ["total_area"] = land.Sum(p => p.ActualArea),
            };
        }
        var mine = await OwnedAsync(UUID.Zero, ct);
        var contributions = new JsonArray();
        foreach (var g in _s.State.Groups.Values.Where(g => g.Contribution > 0).OrderBy(g => g.Name))
            contributions.Add(new JsonObject { ["group"] = g.Name, ["area"] = g.Contribution });
        return new JsonObject
        {
            ["parcels"] = new JsonArray(mine.Select(p => (JsonNode)Place(p)).ToArray()),
            ["total_area"] = mine.Sum(p => p.ActualArea), ["billable_area"] = mine.Sum(p => p.BillableArea),
            ["group_contributions"] = contributions,
        };
    }

    /// <summary>The group's Land &amp; L$ tab, "your contribution": how much of my land allowance the group may use.</summary>
    public async Task<JsonObject> ContributeAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = _s.Parcels.GroupArg(a) ?? throw new LeapError("need group (an id or a group of mine)");
        if (!_s.State.Groups.ContainsKey(gid)) throw new LeapError("I'm not in that group");
        int area = a.Int("area", -1);
        if (area < 0) throw new LeapError("need area (m², 0 for none)");
        c.Groups.SetGroupContribution(gid, area);
        await Task.Delay(1500, ct);
        int now = _s.State.Groups.TryGetValue(gid, out var g) ? g.Contribution : -1;
        var o = new JsonObject { ["group"] = g.Name, ["contribution"] = now };
        if (now != area) o["note"] = "the grid hasn't confirmed it yet (it comes with the group's next update)";
        return o;
    }

    public static readonly string[] ItemTypes = { "land_for_sale", "adult_land_for_sale", "events", "moderate_events", "adult_events", "telehubs", "people" };

    static GridItemType TypeOf(string t) => t switch
    {
        "land_for_sale" or "land" => GridItemType.LandForSale,
        "adult_land_for_sale" => GridItemType.AdultLandForSale,
        "events" or "general_events" => GridItemType.PgEvent,
        "moderate_events" => GridItemType.MatureEvent,
        "adult_events" => GridItemType.AdultEvent,
        "telehubs" => GridItemType.Telehub,
        "people" => GridItemType.AgentLocations,
        _ => throw new LeapError($"type: {string.Join(" | ", ItemTypes)}"),
    };

    /// <summary>The map's items of a kind (the viewer asks the whole grid for most, a region at a time for people),
    /// nearest first.</summary>
    public async Task<List<MapItem>> ItemsAsync(GridItemType type, ulong regionHandle, CancellationToken ct)
    {
        var c = C;
        var got = new List<MapItem>();
        var last = DateTime.MaxValue;
        void On(object? _, GridItemsEventArgs e)
        {
            if (e.Type != type) return;
            lock (got) { got.AddRange(e.Items); last = DateTime.UtcNow; }
        }
        c.Grid.GridItems += On;
        try
        {
            c.Grid.RequestMapItems(regionHandle, type, GridLayerType.Objects);
            var end = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTime.UtcNow < end)
            {
                await Task.Delay(150, ct);
                lock (got) if (last != DateTime.MaxValue && DateTime.UtcNow - last > TimeSpan.FromSeconds(1)) break;
            }
        }
        finally { c.Grid.GridItems -= On; }
        lock (got) return got.ToList();
    }

    public async Task<JsonObject> MapItemsAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var sim = c.Network.CurrentSim ?? throw new LeapError("no region");
        var kind = (a.Str("type") ?? "land_for_sale").Trim().ToLowerInvariant();
        var type = TypeOf(kind);
        ulong handle = 0;
        if (a.Str("region") is { Length: > 0 } name)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var r = await c.Grid.GetGridRegionAsync(name, GridLayerType.Objects, cts.Token) ?? throw new LeapError($"no region called {name}");
            handle = r.RegionHandle;
        }
        // people are counted a region at a time (the viewer asks each region in view)
        else if (type == GridItemType.AgentLocations) handle = sim.Handle;
        var items = await ItemsAsync(type, handle, ct);
        Utils.LongToUInts(sim.Handle, out var mx, out var my);
        var me = new Vector2(mx + c.Self.SimPosition.X, my + c.Self.SimPosition.Y);
        int limit = Math.Clamp(a.Int("limit", 20), 1, 100);
        var near = items.OrderBy(i => Vector2.Distance(new Vector2(i.GlobalX, i.GlobalY), me)).Take(limit).ToList();
        var names = new Dictionary<ulong, string>();
        foreach (var h in near.Select(i => Utils.UIntsToLong(i.GlobalX / 256 * 256, i.GlobalY / 256 * 256)).Distinct())
        {
            if (c.Grid.RegionsByHandleReadOnly.TryGetValue(h, out var known) && known.Name is { Length: > 0 }) { names[h] = known.Name; continue; }
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                if (await c.Grid.GetGridRegionAsync(h, GridLayerType.Objects, cts.Token) is { } r && r.Name is { Length: > 0 }) names[h] = r.Name;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        var arr = new JsonArray();
        foreach (var i in near)
        {
            var h = Utils.UIntsToLong(i.GlobalX / 256 * 256, i.GlobalY / 256 * 256);
            var region = names.TryGetValue(h, out var n) ? n : null;
            var o = new JsonObject();
            switch (i)
            {
                case MapLandForSale l: o["name"] = l.Name; o["area"] = l.Size; o["price"] = l.Price; o["parcel_id"] = l.ID.ToString(); break;
                case MapAdultLandForSale l: o["name"] = l.Name; o["area"] = l.Size; o["price"] = l.Price; o["parcel_id"] = l.ID.ToString(); break;
                case MapPGEvent e: o["name"] = e.Description; break;
                case MapMatureEvent e: o["name"] = e.Description; break;
                case MapAdultEvent e: o["name"] = e.Description; break;
                case MapAgentLocation p: o["people"] = p.AvatarCount; break;
            }
            o["region"] = region;
            o["at"] = new JsonArray((int)i.LocalX, (int)i.LocalY);
            if (region != null) o["go_to"] = GoTo(region, i.GlobalX, i.GlobalY, type == GridItemType.AgentLocations ? 0 : 0);
            o["distance_m"] = (int)Vector2.Distance(new Vector2(i.GlobalX, i.GlobalY), me);
            arr.Add(o);
        }
        return new JsonObject { ["type"] = kind, ["items"] = arr, ["found"] = items.Count };
    }
}
