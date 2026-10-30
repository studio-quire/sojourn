using System.Collections.Concurrent;
using SkiaSharp;

namespace Sojourn.Session.Map;

/// <summary>
/// The world map's tiles as the viewer fetches them (llworldmipmap.cpp): JPEGs from the grid's map server (the login's
/// map-server-url; Second Life's own as the fallback), `map-{level}-{x}-{y}-objects.jpg`, level 1 a region to a
/// 256-pixel tile, each level up twice the regions a side (8 the most), x and y the grid coordinates of the tile's
/// south-west region, rounded down to the level's step. Kept in memory (the most recently used 192), four fetches at a
/// time, nearest the board's middle first. A tile the server hasn't got (no region there: water) is remembered as
/// missing for a while.
/// </summary>
public sealed class MapTiles : IDisposable
{
    public const int Levels = 8;
    const int Keep = 192, AtOnce = 4;
    static readonly TimeSpan MissingFor = TimeSpan.FromMinutes(10);

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly ConcurrentDictionary<(int L, int X, int Y), Entry> _tiles = new();
    readonly Func<string?> _server;
    readonly SemaphoreSlim _slots = new(AtOnce, AtOnce);
    long _use;
    bool _disposed;

    sealed class Entry
    {
        public SKImage? Image;
        public DateTime MissingSince = DateTime.MinValue;
        public bool Fetching;
        public long Used;
    }

    /// <summary>A tile arrived (the board redraws).</summary>
    public event Action? Arrived;

    public MapTiles(Func<string?> server)
    {
        _server = server;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Sojourn (Second Life viewer)");
    }

    public static string DefaultServer => "https://map.secondlife.com/";

    /// <summary>The level whose tiles show at about their own pixels for this many board pixels to a region
    /// (LLWorldMipmap::scaleToLevel).</summary>
    public static int LevelFor(float pixelsPerRegion)
    {
        if (pixelsPerRegion <= 1e-3f) return Levels;
        int level = (int)MathF.Floor(MathF.Log2(256f / pixelsPerRegion) + 1);
        return Math.Clamp(level, 1, Levels);
    }

    /// <summary>The tile covering a region's grid coordinates at a level: its south-west region.</summary>
    public static (int X, int Y) TileOf(int gx, int gy, int level)
    {
        int step = 1 << (level - 1);
        return (gx - gx % step, gy - gy % step);
    }

    /// <summary>The tile if it's here; asked for if not (priority: lower first). Null while it comes, or missing.</summary>
    public SKImage? Get(int level, int x, int y, float priority)
    {
        var key = (level, x, y);
        var e = _tiles.GetOrAdd(key, _ => new Entry());
        e.Used = Interlocked.Increment(ref _use);
        if (e.Image != null) return e.Image;
        if (e.Fetching || DateTime.UtcNow - e.MissingSince < MissingFor) return null;
        e.Fetching = true;
        _ = FetchAsync(key, e, priority);
        return null;
    }

    /// <summary>The best tile there is for a region at a level or coarser (a coarser one while the finer comes), and
    /// the part of it that's this region's (0-1 from the top left).</summary>
    public (SKImage Image, SKRect Part)? Best(int level, int gx, int gy)
    {
        for (int l = level; l <= Levels; l++)
        {
            var (tx, ty) = TileOf(gx, gy, l);
            if (!_tiles.TryGetValue((l, tx, ty), out var e) || e.Image == null) continue;
            int step = 1 << (l - 1);
            float u0 = (gx - tx) / (float)step, v0 = 1 - (gy - ty + 1) / (float)step;
            return (e.Image, new SKRect(u0, v0, u0 + 1f / step, v0 + 1f / step));
        }
        return null;
    }

    async Task FetchAsync((int L, int X, int Y) key, Entry e, float priority)
    {
        try
        {
            // nearer ones first: a short wait for the farther, so the middle's start before them
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(priority, 0, 1) * 120));
            await _slots.WaitAsync();
            try
            {
                if (_disposed) return;
                var server = _server() is { Length: > 0 } s ? s : DefaultServer;
                if (!server.EndsWith('/')) server += "/";
                using var r = await _http.GetAsync($"{server}map-{key.L}-{key.X}-{key.Y}-objects.jpg");
                if (!r.IsSuccessStatusCode) { e.MissingSince = DateTime.UtcNow; LastError = $"{key}: {(int)r.StatusCode}"; return; }
                var bytes = await r.Content.ReadAsByteArrayAsync();
                var img = SKImage.FromEncodedData(bytes);
                if (img == null) { e.MissingSince = DateTime.UtcNow; return; }
                e.Image = img.ToRasterImage(true) ?? img;
                if (!ReferenceEquals(e.Image, img)) img.Dispose();
                Trim();
            }
            finally { _slots.Release(); }
            Arrived?.Invoke();
        }
        catch (Exception x)
        {
            LastError = $"{key}: {x.Message}";
            e.MissingSince = DateTime.UtcNow - MissingFor + TimeSpan.FromSeconds(30);   // a network failure: try again soon
        }
        finally { e.Fetching = false; }
    }

    void Trim()
    {
        var held = _tiles.Where(kv => kv.Value.Image != null).ToList();
        if (held.Count <= Keep) return;
        foreach (var kv in held.OrderBy(kv => kv.Value.Used).Take(held.Count - Keep))
            if (_tiles.TryRemove(kv.Key, out var gone))
            {
                // the painter may be drawing it: let it go after a moment
                var img = gone.Image;
                _ = Task.Delay(2000).ContinueWith(_ => img?.Dispose());
            }
    }

    /// <summary>The last tile that failed, and why (for the API's state and tests).</summary>
    public string? LastError { get; private set; }

    public int Count => _tiles.Count(kv => kv.Value.Image != null);

    public void Dispose()
    {
        _disposed = true;
        _http.Dispose();
    }
}
