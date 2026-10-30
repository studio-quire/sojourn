using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// SL's own search for the assistant: places, events and classifieds, as the viewers' legacy search asks for them
/// (Firestorm's search floater: DirPlacesQuery, DirFindQuery with DateEvents, DirClassifiedQuery), each result filled in
/// with where it is from a second ask (ParcelInfoRequest, EventInfoRequest, ClassifiedInfoRequest) so the assistant can
/// say what it is and take the person there. Only ratings the account goes to are asked for. Replies come in several
/// packets; they're gathered until the grid goes quiet. Claude Opus 5.5, 2026-10-26.
/// </summary>
public sealed class SearchOps
{
    readonly AgentSession _s;
    public SearchOps(AgentSession s) => _s = s;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    const int DefaultLimit = 8, MaxLimit = 20;
    static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.2), Overall = TimeSpan.FromSeconds(8), Detail = TimeSpan.FromSeconds(4);

    public static readonly string[] PlaceCategories =
        { "any", "linden", "adult", "arts", "business", "educational", "gaming", "hangout", "newcomer", "park", "residential", "shopping", "stage", "other" };
    public static readonly string[] EventCategories =
        { "any", "discussion", "sports", "live music", "commercial", "nightlife", "games", "pageants", "education", "arts", "charity", "miscellaneous" };
    public static readonly string[] ClassifiedCategories =
        { "any", "shopping", "land rental", "property rental", "special attraction", "new products", "employment", "wanted", "service", "personal" };

    /// <summary>The ratings the account goes to (its maturity preference), as the viewer's search includes them.</summary>
    DirectoryManager.DirFindFlags Ratings() => DirectoryManager.DirFindFlags.IncludePG | (_s.Maturity switch
    {
        "adult" => DirectoryManager.DirFindFlags.IncludeMature | DirectoryManager.DirFindFlags.IncludeAdult,
        "moderate" => DirectoryManager.DirFindFlags.IncludeMature,
        _ => 0,
    });

    static int Limit(JsonObject a) => Math.Clamp(a.Int("limit", DefaultLimit), 1, MaxLimit);

    static string Text(JsonObject a, bool required)
    {
        var t = (a.Str("text") ?? "").Trim();
        // SL's search ignores shorter words, as the viewers say
        if ((required || t.Length > 0) && t.Length < 3) throw new LeapError("text: 3 characters or more");
        return t;
    }

    static int Category(JsonObject a, string[] names, string what)
    {
        var c = (a.Str("category") ?? "any").Trim().ToLowerInvariant();
        int i = Array.IndexOf(names, c);
        return i >= 0 ? i : throw new LeapError($"{what} category: {string.Join(", ", names)}");
    }

    /// <summary>Every reply to one query, gathered until the grid has been quiet a moment (empty if it never answers).</summary>
    static async Task<List<T>> GatherAsync<TArgs, T>(Action<EventHandler<TArgs>> add, Action<EventHandler<TArgs>> remove,
        Func<TArgs, IEnumerable<T>?> ours, Action send, CancellationToken ct)
    {
        var got = new List<T>();
        var last = DateTime.MaxValue;
        void On(object? _, TArgs e)
        {
            if (ours(e) is not { } items) return;
            lock (got) { got.AddRange(items); last = DateTime.UtcNow; }
        }
        add(On);
        try
        {
            send();
            var end = DateTime.UtcNow + Overall;
            while (DateTime.UtcNow < end)
            {
                await Task.Delay(150, ct);
                lock (got) if (last != DateTime.MaxValue && DateTime.UtcNow - last > Quiet) break;
            }
        }
        finally { remove(On); }
        lock (got) return got.ToList();
    }

    /// <summary>A second ask for each result's details, in parallel; a result whose details don't come keeps what the
    /// search gave.</summary>
    static async Task<TDetail?[]> DetailsAsync<TKey, TArgs, TDetail>(IReadOnlyList<TKey> keys, Action<EventHandler<TArgs>> add,
        Action<EventHandler<TArgs>> remove, Func<TArgs, (TKey Key, TDetail Value)> read, Action<TKey> ask, CancellationToken ct)
        where TKey : notnull where TDetail : struct
    {
        var waiting = keys.Distinct().ToDictionary(k => k, _ => new TaskCompletionSource<TDetail>(TaskCreationOptions.RunContinuationsAsynchronously));
        void On(object? _, TArgs e)
        {
            var (k, v) = read(e);
            if (waiting.TryGetValue(k, out var t)) t.TrySetResult(v);
        }
        add(On);
        try
        {
            foreach (var k in waiting.Keys) ask(k);
            var all = Task.WhenAll(waiting.Values.Select(t => t.Task));
            try { await all.WaitAsync(Detail, ct); } catch (TimeoutException) { }
        }
        finally { remove(On); }
        return keys.Select(k => waiting[k].Task.IsCompletedSuccessfully ? (TDetail?)waiting[k].Task.Result : null).ToArray();
    }

    /// <summary>Where something is, as the go tool takes it: "Region x,y,z" (SL's regions are 256 m, so the region-local
    /// position is the global one's remainder).</summary>
    static string? GoTo(string? region, double gx, double gy, double gz) =>
        string.IsNullOrEmpty(region) ? null : $"{region} {Math.Floor(gx % 256)},{Math.Floor(gy % 256)},{Math.Max(0, Math.Floor(gz))}";

    static string Short(string? s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n].TrimEnd() + "...";

    public async Task<JsonObject> PlacesAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var text = Text(a, required: true);
        var cat = Category(a, PlaceCategories, "places");
        var flags = DirectoryManager.DirFindFlags.DwellSort | Ratings();
        if (a.Bool("for_sale", false)) flags |= DirectoryManager.DirFindFlags.ForSale;
        UUID q = UUID.Zero;
        var found = await GatherAsync<DirPlacesReplyEventArgs, DirectoryManager.DirectoryParcel>(
            h => c.Directory.DirPlacesReply += h, h => c.Directory.DirPlacesReply -= h,
            e => e.QueryID == q ? e.MatchedParcels.Where(p => p.ID != UUID.Zero) : null,
            () => q = c.Directory.StartDirPlacesSearch(text, flags, cat == 0 ? ParcelCategory.Any : (ParcelCategory)(sbyte)cat, a.Int("start", 0)), ct);
        var top = found.OrderByDescending(p => p.Dwell).Take(Limit(a)).ToList();
        var info = await DetailsAsync<UUID, ParcelInfoReplyEventArgs, ParcelInfo>(top.Select(p => p.ID).ToList(),
            h => c.Parcels.ParcelInfoReply += h, h => c.Parcels.ParcelInfoReply -= h, e => (e.Parcel.ID, e.Parcel), id => c.Parcels.RequestParcelInfo(id), ct);
        var arr = new JsonArray();
        for (int i = 0; i < top.Count; i++)
        {
            var p = top[i];
            var o = new JsonObject { ["name"] = p.Name, ["traffic"] = (int)p.Dwell };
            if (p.ForSale) o["for_sale"] = p.SalePrice;
            if (info[i] is { } pi)
            {
                o["region"] = pi.SimName;
                o["go_to"] = GoTo(pi.SimName, pi.GlobalX, pi.GlobalY, pi.GlobalZ);
                if (pi.Description is { Length: > 0 } d) o["about"] = Short(d, 240);
                o["moderate_or_adult"] = pi.Mature;
            }
            arr.Add(o);
        }
        return new JsonObject { ["places"] = arr, ["found"] = found.Count };
    }

    public async Task<JsonObject> EventsAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var text = Text(a, required: false);
        int cat = Category(a, EventCategories, "events");
        // "u": on now and later today; a number: that many days from today (SL time)
        var day = (a.Str("when") ?? "now").Trim().ToLowerInvariant() switch
        {
            "now" or "upcoming" or "" => "u",
            "today" => "0",
            "tomorrow" => "1",
            var d when int.TryParse(d, out var n) && n is >= 0 and <= 14 => n.ToString(),
            _ => throw new LeapError("when: now (on now and later today), today, tomorrow, or a number of days from today (0-14)"),
        };
        var catValue = cat == 0 ? DirectoryManager.EventCategories.All : (DirectoryManager.EventCategories)(17 + cat + (cat >= 4 ? 1 : 0));
        var flags = DirectoryManager.DirFindFlags.DateEvents | Ratings();
        UUID q = UUID.Zero;
        var found = await GatherAsync<DirEventsReplyEventArgs, DirectoryManager.EventsSearchData>(
            h => c.Directory.DirEventsReply += h, h => c.Directory.DirEventsReply -= h,
            e => e.QueryID == q ? e.MatchedEvents.Where(x => x.ID != 0) : null,
            () => q = c.Directory.StartEventsSearch(text, flags, day, (uint)a.Int("start", 0), catValue), ct);
        var top = found.OrderBy(x => x.Time).Take(Limit(a)).ToList();
        var info = await DetailsAsync<uint, EventInfoReplyEventArgs, DirectoryManager.EventInfo>(top.Select(x => x.ID).ToList(),
            h => c.Directory.EventInfoReply += h, h => c.Directory.EventInfoReply -= h, e => (e.MatchedEvent.ID, e.MatchedEvent),
            id => c.Directory.EventInfoRequest(id), ct);
        var now = DateTimeOffset.UtcNow;
        var arr = new JsonArray();
        for (int i = 0; i < top.Count; i++)
        {
            var x = top[i];
            var starts = DateTimeOffset.FromUnixTimeSeconds(x.Time);
            var o = new JsonObject { ["name"] = x.Name, ["starts"] = starts.ToLocalTime().ToString("ddd d MMM HH:mm") };
            if (info[i] is { } ei)
            {
                var ends = starts.AddMinutes(ei.Duration);
                if (starts <= now && now < ends) o["on_now"] = true;
                o["minutes"] = ei.Duration;
                o["region"] = ei.SimName;
                o["go_to"] = GoTo(ei.SimName, ei.GlobalPos.X, ei.GlobalPos.Y, ei.GlobalPos.Z);
                if (ei.Desc is { Length: > 0 } d) o["about"] = Short(d, 240);
                o["category"] = ei.Category.ToString();
                if (ei.Cover > 0) o["cover_charge"] = ei.Amount;
                o["rating"] = ei.Flags switch { DirectoryManager.EventFlags.Adult => "adult", DirectoryManager.EventFlags.Mature => "moderate", _ => "general" };
            }
            arr.Add(o);
        }
        return new JsonObject { ["events"] = arr, ["found"] = found.Count };
    }

    public async Task<JsonObject> ClassifiedsAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var text = Text(a, required: true);
        int cat = Category(a, ClassifiedCategories, "classifieds");
        var r = Ratings();
        var flags = DirectoryManager.ClassifiedQueryFlags.PG
            | ((r & DirectoryManager.DirFindFlags.IncludeMature) != 0 ? DirectoryManager.ClassifiedQueryFlags.Mature : 0)
            | ((r & DirectoryManager.DirFindFlags.IncludeAdult) != 0 ? DirectoryManager.ClassifiedQueryFlags.Adult : 0);
        // LibreMetaverse's reply doesn't carry the query id: one classifieds search at a time
        await _classifieds.WaitAsync(ct);
        List<DirectoryManager.Classified> found;
        try
        {
            found = await GatherAsync<DirClassifiedsReplyEventArgs, DirectoryManager.Classified>(
                h => c.Directory.DirClassifiedsReply += h, h => c.Directory.DirClassifiedsReply -= h,
                e => e.Classifieds.Where(x => x.ID != UUID.Zero),
                () => c.Directory.StartClassifiedSearch(text, (DirectoryManager.ClassifiedCategories)cat, flags), ct);
        }
        finally { _classifieds.Release(); }
        var top = found.Take(Limit(a)).ToList();
        var info = await DetailsAsync<UUID, ClassifiedInfoReplyEventArgs, ClassifiedAd>(top.Select(x => x.ID).ToList(),
            h => c.Avatars.ClassifiedInfoReply += h, h => c.Avatars.ClassifiedInfoReply -= h, e => (e.ClassifiedID, e.Classified),
            id => c.Avatars.RequestClassifiedInfo(id), ct);
        var arr = new JsonArray();
        for (int i = 0; i < top.Count; i++)
        {
            var x = top[i];
            var o = new JsonObject { ["name"] = x.Name };
            if (info[i] is { } ci)
            {
                o["region"] = ci.SimName;
                if (ci.ParcelName is { Length: > 0 } pn) o["parcel"] = pn;
                o["go_to"] = GoTo(ci.SimName, ci.Position.X, ci.Position.Y, ci.Position.Z);
                if (ci.Desc is { Length: > 0 } d) o["about"] = Short(d, 240);
            }
            arr.Add(o);
        }
        return new JsonObject { ["classifieds"] = arr, ["found"] = found.Count };
    }

    readonly SemaphoreSlim _classifieds = new(1, 1);
    readonly SemaphoreSlim _land = new(1, 1);

    public static readonly string[] LandTypes = { "any", "mainland", "estate", "auction" };
    public static readonly string[] LandSorts = { "price_per_m", "price", "area", "name" };

    /// <summary>Land for sale (the legacy search's Land Sales tab, DirLandQuery): mainland, estates or auctions, under a
    /// price, over an area, cheapest per square metre first by default; each with where it is. Batch B5, 2026-10-28.</summary>
    public async Task<JsonObject> LandAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var type = (a.Str("type") ?? "any").Trim().ToLowerInvariant() switch
        {
            "any" or "" => DirectoryManager.SearchTypeFlags.Any,
            "mainland" => DirectoryManager.SearchTypeFlags.Mainland,
            "estate" => DirectoryManager.SearchTypeFlags.Estate,
            "auction" => DirectoryManager.SearchTypeFlags.Auction,
            _ => throw new LeapError($"type: {string.Join(" | ", LandTypes)}"),
        };
        var sort = (a.Str("sort") ?? "price_per_m").Trim().ToLowerInvariant();
        var flags = Ratings() | sort switch
        {
            "price_per_m" => DirectoryManager.DirFindFlags.PerMeterSort | DirectoryManager.DirFindFlags.SortAsc,
            "price" => DirectoryManager.DirFindFlags.PricesSort | DirectoryManager.DirFindFlags.SortAsc,
            "area" => DirectoryManager.DirFindFlags.AreaSort,
            "name" => DirectoryManager.DirFindFlags.NameSort | DirectoryManager.DirFindFlags.SortAsc,
            _ => throw new LeapError($"sort: {string.Join(" | ", LandSorts)}"),
        };
        int price = a.Int("max_price", 0), area = a.Int("min_area", 0);
        if (price > 0) flags |= DirectoryManager.DirFindFlags.LimitByPrice;
        if (area > 0) flags |= DirectoryManager.DirFindFlags.LimitByArea;
        // the reply doesn't carry the query id: one land search at a time
        await _land.WaitAsync(ct);
        List<DirectoryManager.DirectoryParcel> found;
        try
        {
            found = await GatherAsync<DirLandReplyEventArgs, DirectoryManager.DirectoryParcel>(
                h => c.Directory.DirLandReply += h, h => c.Directory.DirLandReply -= h,
                e => e.DirParcels.Where(p => p.ID != UUID.Zero),
                () => c.Directory.StartLandSearch(flags, type, price, area, a.Int("start", 0)), ct);
        }
        finally { _land.Release(); }
        var top = found.Take(Limit(a)).ToList();
        var info = await DetailsAsync<UUID, ParcelInfoReplyEventArgs, ParcelInfo>(top.Select(p => p.ID).ToList(),
            h => c.Parcels.ParcelInfoReply += h, h => c.Parcels.ParcelInfoReply -= h, e => (e.Parcel.ID, e.Parcel), id => c.Parcels.RequestParcelInfo(id), ct);
        var arr = new JsonArray();
        for (int i = 0; i < top.Count; i++)
        {
            var p = top[i];
            var o = new JsonObject { ["name"] = p.Name, ["price"] = p.SalePrice, ["area"] = p.ActualArea };
            if (p.ActualArea > 0) o["price_per_m"] = Math.Round(p.SalePrice / (double)p.ActualArea, 2);
            if (p.Auction) o["auction"] = true;
            if (info[i] is { } pi)
            {
                o["region"] = pi.SimName;
                o["go_to"] = GoTo(pi.SimName, pi.GlobalX, pi.GlobalY, pi.GlobalZ);
                if (pi.Description is { Length: > 0 } d) o["about"] = Short(d, 240);
                o["moderate_or_adult"] = pi.Mature;
            }
            arr.Add(o);
        }
        return new JsonObject { ["land"] = arr, ["found"] = found.Count };
    }
}
