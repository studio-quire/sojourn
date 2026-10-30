using System.Net;
using System.Net.Http.Headers;
using LibreMetaverse;
using Sojourn.Core.World;
using Sojourn.Render.World;

namespace Sojourn.Render.Integration;

/// <summary>
/// Textures by byte range over the region's ViewerAsset (or GetTexture) capability, as the viewer's LLTextureFetch
/// asks for them (HTTP Range by discard level), through a connection pool of their own, kept in the grid's asset cache
/// folder: whole files as LibreMetaverse keeps them (its own downloads and ours share them), prefixes under partial/.
/// For tests, SOJOURN_FETCH_LATENCY_MS adds a round trip's delay to each request and SOJOURN_FETCH_KBPS caps the
/// bytes a second across all of them, so the local grid can stand in for a far one.
/// </summary>
sealed partial class HubAssetSource
{
    // The viewer's texture fetch: 8 connections to a host (AP_TEXTURE, llappcorehttp.cpp). The client keeps at most
    // TextureStore.MaxFetches requests in flight, so the pool never queues.
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 8,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.None,
    }) { Timeout = TimeSpan.FromSeconds(60) };

    static readonly int LatencyMs = int.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_FETCH_LATENCY_MS"), out var l) ? l : 0;
    static readonly int Kbps = int.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_FETCH_KBPS"), out var k) ? k : 0;
    static readonly object Bucket = new();
    static double _bucketFreeAt;

    string? Dir => hub.CacheDir(gridKey);

    public (byte[]? Data, bool Complete) CachedTexture(UUID id)
    {
        if (Dir is not { } dir) return (null, false);
        try
        {
            var whole = Path.Combine(dir, id.ToString());
            if (File.Exists(whole)) return (File.ReadAllBytes(whole), true);
            var part = Path.Combine(dir, "partial", id.ToString());
            if (File.Exists(part)) return (File.ReadAllBytes(part), false);
        }
        catch (IOException) { }
        return (null, false);
    }

    public void StoreTexture(UUID id, byte[] data, bool complete)
    {
        if (Dir is not { } dir || data.Length == 0) return;
        try
        {
            var path = complete ? Path.Combine(dir, id.ToString()) : Path.Combine(dir, "partial", id.ToString());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + "." + System.Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
            if (complete) File.Delete(Path.Combine(dir, "partial", id.ToString()));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task<RangeFetch> TextureRangeAsync(UUID id, int offset, int length, CancellationToken ct)
    {
        var cap = hub.ClientFor(gridKey)?.Network.CurrentSim?.Caps?.GetTextureCapURI();
        if (cap == null) return new RangeFetch(FetchStatus.Unavailable, null, -1, false);
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri($"{cap}?texture_id={id}"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/x-j2c"));
        req.Headers.Range = new RangeHeaderValue(offset, length < 0 ? null : offset + length - 1L);
        try
        {
            if (LatencyMs > 0) await Task.Delay(LatencyMs, ct).ConfigureAwait(false);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            switch (resp.StatusCode)
            {
                case HttpStatusCode.NotFound or HttpStatusCode.Gone:
                    return new RangeFetch(FetchStatus.NotFound, null, -1, false);
                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    // asked from its end: there's no more
                    return new RangeFetch(FetchStatus.Ok, Array.Empty<byte>(), offset, true);
                case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable:
                    return new RangeFetch(FetchStatus.Retry, null, -1, false, resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2));
            }
            if (!resp.IsSuccessStatusCode) return new RangeFetch(FetchStatus.Retry, null, -1, false, TimeSpan.FromSeconds(2));
            var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            await Throttle(body.Length, ct).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.PartialContent)
            {
                // the whole file, the range ignored (a server without ranges): what we asked for is from offset on
                var rest = offset <= 0 ? body : offset >= body.Length ? Array.Empty<byte>() : body[offset..];
                return new RangeFetch(FetchStatus.Ok, rest, body.Length, true);
            }
            var cr = resp.Content.Headers.ContentRange;
            int total = cr?.Length is { } t ? (int)t : -1;
            bool toEnd = cr?.To is { } to && total > 0 ? to + 1 >= total : (length > 0 && body.Length < length);
            return new RangeFetch(FetchStatus.Ok, body, total, toEnd);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // timeouts, resets, DNS: try again later
            return new RangeFetch(FetchStatus.Retry, null, -1, false, TimeSpan.FromSeconds(2));
        }
    }

    // Meshes: the viewer's mesh fetch has connections of its own (AP_MESH2), as here; through LibreMetaverse's download
    // queue they waited behind every bake, sound and whole texture asked for, in order, never cancelled.
    static readonly HttpClient MeshHttp = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 8,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.None,
    }) { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Meshes downloaded (not read from disk), their bytes and the time they took, for the console.</summary>
    public static long MeshDownloads, MeshDownloadedBytes, MeshDownloadMs;

    public async Task<RangeFetch> MeshAsync(UUID id, CancellationToken ct)
    {
        if (Dir is { } dir)
            try
            {
                var path = Path.Combine(dir, id.ToString());
                if (File.Exists(path) && await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) is { Length: > 0 } cached)
                    return new RangeFetch(FetchStatus.Ok, cached, cached.Length, true);
            }
            catch (IOException) { }
        var cap = hub.ClientFor(gridKey)?.Network.CurrentSim?.Caps?.GetMeshCapURI();
        if (cap == null) return new RangeFetch(FetchStatus.Unavailable, null, -1, false);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (LatencyMs > 0) await Task.Delay(LatencyMs, ct).ConfigureAwait(false);
            using var resp = await MeshHttp.GetAsync(new Uri($"{cap}?mesh_id={id}"), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            switch (resp.StatusCode)
            {
                case HttpStatusCode.NotFound or HttpStatusCode.Gone:
                    return new RangeFetch(FetchStatus.NotFound, null, -1, false);
                case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable:
                    return new RangeFetch(FetchStatus.Retry, null, -1, false, resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2));
            }
            if (!resp.IsSuccessStatusCode) return new RangeFetch(FetchStatus.Retry, null, -1, false, TimeSpan.FromSeconds(2));
            var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            await Throttle(body.Length, ct).ConfigureAwait(false);
            if (body.Length == 0) return new RangeFetch(FetchStatus.Retry, null, -1, false, TimeSpan.FromSeconds(2));
            Interlocked.Increment(ref MeshDownloads);
            Interlocked.Add(ref MeshDownloadedBytes, body.Length);
            Interlocked.Add(ref MeshDownloadMs, (long)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            StoreWhole(id, body);
            return new RangeFetch(FetchStatus.Ok, body, body.Length, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new RangeFetch(FetchStatus.Retry, null, -1, false, TimeSpan.FromSeconds(2));
        }
    }

    // --- the console's word on downloads (GeometryCache.DescribeExtra) ------------------------------------------------

    static WorldHub? _reportHub;
    static string? _reportGrid;
    static long _reportedAt, _reportedMeshes, _reportedMeshBytes, _reportedMeshMs;
    readonly int _hooked = Hook(hub, gridKey);

    static int Hook(WorldHub hub, string grid)
    {
        (_reportHub, _reportGrid) = (hub, grid);
        GeometryCache.DescribeExtra ??= Report;
        StartPruning(hub.CacheDir(grid));
        return 0;
    }

    static string Report()
    {
        long now = System.Environment.TickCount64;
        long n = Interlocked.Read(ref MeshDownloads), bytes = Interlocked.Read(ref MeshDownloadedBytes), ms = Interlocked.Read(ref MeshDownloadMs);
        long dn = n - _reportedMeshes;
        double secs = Math.Max(1, (now - _reportedAt) / 1000.0);
        var line = $", {dn} downloaded ({(bytes - _reportedMeshBytes) / secs / 1024:F0} KB/s{(dn > 0 ? $", {(ms - _reportedMeshMs) / dn} ms each" : "")})";
        (_reportedAt, _reportedMeshes, _reportedMeshBytes, _reportedMeshMs) = (now, n, bytes, ms);
        if (LmvQueue() is { } q) line += $"; LibreMetaverse downloads {q.Queued} queued, {q.Active} active";
        line += "; " + Memory();
        return line;
    }

    /// <summary>The process's memory for the console: the working set, and the managed heap as the last collection
    /// left it (live and fragmented: free space between live objects, which only a compacting collection gives
    /// back), the last full collection's survivors, and what the heap has committed.</summary>
    static string Memory()
    {
        var any = GC.GetGCMemoryInfo(GCKind.Any);
        GCMemoryInfo bg = GC.GetGCMemoryInfo(GCKind.Background), fb = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        var full = bg.Index > fb.Index ? bg : fb;
        long Gen(GCMemoryInfo m, int g) => m.GenerationInfo.Length > g ? m.GenerationInfo[g].SizeAfterBytes : 0;
        return $"memory {System.Environment.WorkingSet >> 20} MB (heap {any.HeapSizeBytes >> 20} MB, {any.FragmentedBytes >> 20} fragmented, " +
               $"{any.TotalCommittedBytes >> 20} committed; last full collection left gen2 {Gen(full, 2) >> 20}, large {Gen(full, 3) >> 20})";
    }

    static System.Reflection.FieldInfo? _dmField, _queueField, _activeField;

    /// <summary>LibreMetaverse's download queue (bakes, sounds, animations, whole textures; by reflection on its private
    /// fields, so nothing if a version renames them).</summary>
    static (int Queued, int Active)? LmvQueue()
    {
        try
        {
            if (_reportHub?.ClientFor(_reportGrid!)?.Assets is not { } assets) return null;
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            _dmField ??= assets.GetType().GetField("HttpDownloads", F);
            if (_dmField?.GetValue(assets) is not { } dm) return null;
            _queueField ??= dm.GetType().GetField("queue", F);
            _activeField ??= dm.GetType().GetField("activeDownloads", F);
            int Count(object? o) => o is System.Collections.ICollection c ? c.Count : o?.GetType().GetProperty("Count")?.GetValue(o) as int? ?? -1;
            return (Count(_queueField?.GetValue(dm)), Count(_activeField?.GetValue(dm)));
        }
        catch { return null; }
    }

    // --- keeping the cache folder's size -------------------------------------------------------------------------------
    // LibreMetaverse prunes the whole files it and we keep (Settings.AssetCache.MaxSize, every 5 minutes; WorldHub
    // raises it from 1 GB, which an hour in Second Life went through several times over), not partial/. That's ours.

    static readonly HashSet<string> Pruning = new();
    public static long PartialMaxBytes { get; set; } = 2L << 30;

    static void StartPruning(string? dir)
    {
        if (dir == null) return;
        lock (Pruning) if (!Pruning.Add(dir)) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { PrunePartial(Path.Combine(dir, "partial"), PartialMaxBytes); }
                catch (Exception) { }
                await Task.Delay(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            }
        });
    }

    /// <summary>The oldest written prefixes out until the folder is under three quarters of its cap.</summary>
    static void PrunePartial(string dir, long cap)
    {
        if (!Directory.Exists(dir)) return;
        var files = new DirectoryInfo(dir).EnumerateFiles().Select(f => (f.FullName, f.Length, f.LastWriteTimeUtc)).ToList();
        long total = files.Sum(f => f.Length);
        if (total <= cap) return;
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= cap * 3 / 4) break;
            try { File.Delete(f.FullName); total -= f.Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    void StoreWhole(UUID id, byte[] data)
    {
        if (Dir is not { } dir) return;
        try
        {
            var path = Path.Combine(dir, id.ToString());
            var tmp = path + "." + System.Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The test bandwidth cap: one shared bucket, so concurrent fetches share it as a real link would.</summary>
    static async Task Throttle(int bytes, CancellationToken ct)
    {
        if (Kbps <= 0) return;
        double now = System.Environment.TickCount64, until;
        lock (Bucket)
        {
            _bucketFreeAt = Math.Max(_bucketFreeAt, now) + bytes * 8.0 / Kbps;
            until = _bucketFreeAt;
        }
        if (until > now) await Task.Delay(TimeSpan.FromMilliseconds(until - now), ct).ConfigureAwait(false);
    }
}
