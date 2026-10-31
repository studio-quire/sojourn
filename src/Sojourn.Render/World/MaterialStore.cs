using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Materials;
using LibreMetaverse.StructuredData;

namespace Sojourn.Render.World;

/// <summary>
/// Legacy LLMaterials (by material id, fetched in batches through a region's RenderMaterials cap) and GLTF material
/// assets (by asset id). Both are looked up per face at draw time; a missing entry starts a fetch and the face
/// draws without it until it arrives, like the viewer. A lookup says whether the material is still on its way
/// (waiting) or isn't coming (none, or the fetch failed: tried again after a while), so a face isn't counted as
/// loading forever. <see cref="Version"/> moves whenever one arrives, so cached faces that waited for one re-resolve.
/// </summary>
public sealed class MaterialStore
{
    readonly ConcurrentDictionary<UUID, LegacyMaterial?> _legacy = new();
    readonly ConcurrentDictionary<UUID, byte> _legacyPending = new();
    readonly ConcurrentDictionary<UUID, AssetMaterial?> _gltf = new();
    readonly ConcurrentDictionary<UUID, byte> _gltfPending = new();
    readonly ConcurrentDictionary<UUID, long> _retryAt = new();
    readonly ConcurrentQueue<(UUID, RegionSource)> _legacyQueue = new();
    public Action<string>? Log { get; set; }
    int _flushScheduled;
    long _version;

    /// <summary>Bumped whenever a material arrives (or is known not to exist).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>A failed fetch is tried again after 2 s, doubling each time it fails again, up to this long. The first
    /// asks on arriving in a region often fail (its capabilities not up yet, or the grid's rate limit): a flat 30 s
    /// left Eden Valley's PBR terrain grey for its first half minute (2026-10-28).</summary>
    public static TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(30);
    const long FirstRetryMs = 2000;
    const int EmptyAnswersBeforeNone = 6;   // 2+4+8+16+30 s of asking
    /// <summary>Failures after which a material waiting for its next try no longer counts as loading (it's still
    /// tried again whenever a face using it is resolved after its wait).</summary>
    const int WaitingFailures = 8;

    readonly ConcurrentDictionary<UUID, int> _failures = new();

    /// <summary>Whether it may be asked for now; if not, <paramref name="waiting"/> says whether faces should count it as
    /// still on its way (a retry due, its faces resolved again then).</summary>
    bool MayRetry(UUID id, out bool waiting)
    {
        waiting = false;
        if (!_retryAt.TryGetValue(id, out long at)) return true;
        if (System.Environment.TickCount64 < at)
        {
            waiting = _failures.GetValueOrDefault(id) < WaitingFailures;
            return false;
        }
        _retryAt.TryRemove(id, out _);
        return true;
    }

    /// <summary>Asked again after a wait; the version moves when it's due, so the faces that waited on it (cached with
    /// the version they saw) are resolved again and ask. Without that, a face resolved during the wait was cached as
    /// having no material and never asked again.</summary>
    void Failed(UUID id)
    {
        int n = _failures.AddOrUpdate(id, 1, (_, f) => f + 1);
        long wait = Math.Min((long)RetryAfter.TotalMilliseconds, FirstRetryMs << Math.Min(n - 1, 10));
        _retryAt[id] = System.Environment.TickCount64 + wait;
        _ = Task.Delay(TimeSpan.FromMilliseconds(wait + 50)).ContinueWith(_ => Interlocked.Increment(ref _version), TaskScheduler.Default);
    }

    public LegacyMaterial? Legacy(UUID id, RegionSource region, out bool waiting)
    {
        waiting = false;
        if (id == UUID.Zero) return null;
        if (_legacy.TryGetValue(id, out var m)) return m;
        if (_legacyPending.ContainsKey(id)) { waiting = true; return null; }
        if (!MayRetry(id, out waiting)) return null;
        if (_legacyPending.TryAdd(id, 0))
        {
            _legacyQueue.Enqueue((id, region));
            if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
                _ = Task.Run(async () => { await Task.Delay(100).ConfigureAwait(false); FlushLegacy(); });
        }
        waiting = true;
        return null;
    }

    /// <summary>A region's legacy asks, paced as the viewer paces them (RegionSource.MaterialsPacing: by default 50 ids
    /// an ask, one ask a second). Unpaced, every 100 ms flush was an ask, and SL answered most of a busy place's with its
    /// rate limit (2026-10-31). Held weakly by region, so a region left behind isn't kept.</summary>
    sealed class Pump
    {
        public readonly List<UUID> Waiting = new();
        public bool Running;
        public int Slow = 1;   // the pace's divisor, doubled by each rate-limit reply (to 16), halved by each answer
    }
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<RegionSource, Pump> _pumps = new();
    const int SlowestPace = 16;

    void FlushLegacy()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        var batch = new List<(UUID Id, RegionSource Region)>();
        while (_legacyQueue.TryDequeue(out var e)) batch.Add(e);
        foreach (var group in batch.GroupBy(b => b.Region))
        {
            var pump = _pumps.GetValue(group.Key, _ => new Pump());
            bool start;
            lock (pump)
            {
                pump.Waiting.AddRange(group.Select(g => g.Id));
                start = !pump.Running;
                pump.Running = true;
            }
            var region = group.Key;
            if (start) _ = Task.Run(() => RunPump(region, pump));
        }
    }

    async Task RunPump(RegionSource region, Pump pump)
    {
        while (true)
        {
            var (perAsk, perSecond) = RegionSource.MaterialsPacing(region.LiveSimulator());
            List<UUID> ids;
            lock (pump)
            {
                if (pump.Waiting.Count == 0) { pump.Running = false; return; }
                int n = Math.Min(perAsk, pump.Waiting.Count);
                ids = pump.Waiting.GetRange(0, n);
                pump.Waiting.RemoveRange(0, n);
            }
            MaterialsReply reply;
            try
            {
                reply = region.FetchLegacyMaterials != null
                    ? await region.FetchLegacyMaterials(ids, CancellationToken.None).ConfigureAwait(false)
                    : MaterialsReply.NotAnswered;
            }
            catch (Exception e)
            {
                Log?.Invoke($"materials: {e.Message}");
                reply = MaterialsReply.NotAnswered;
            }
            if (reply.Limited)
            {
                // still pending, first in line again, and the region asked more slowly: nothing is lost to the limit
                int was, slow;
                lock (pump)
                {
                    pump.Waiting.InsertRange(0, ids);
                    was = pump.Slow;
                    slow = pump.Slow = Math.Min(pump.Slow * 2, SlowestPace);
                }
                if (slow != was) Log?.Invoke($"materials: {region.Name} is rate limiting RenderMaterials; an ask every {slow / perSecond:0.#} s");
            }
            else
            {
                lock (pump) pump.Slow = Math.Max(1, pump.Slow / 2);
                Settle(ids, reply);
            }
            int pace;
            lock (pump) pace = pump.Slow;
            await Task.Delay(TimeSpan.FromSeconds(pace / perSecond)).ConfigureAwait(false);
        }
    }

    void Settle(List<UUID> ids, MaterialsReply reply)
    {
        foreach (var m in reply.Materials) { _legacy[m.ID] = m; _failures.TryRemove(m.ID, out _); }
        // an answer's missing ids are none (as the viewer takes them); no answer (no capability yet, a region without
        // its simulator, a network error) is asked again, a few times, before they count as none. Taken as none at
        // once, an id lost in one region stayed none in every region for the session (the grid de-duplicates
        // materials, so a plain "alpha mode: none" is likely one id in many places): suspected for House of Legends'
        // blended ground over its mist (2026-10-28).
        foreach (var id in ids)
        {
            if (_legacy.ContainsKey(id)) continue;
            if (!reply.Answered && _failures.GetValueOrDefault(id) < EmptyAnswersBeforeNone) Failed(id);
            else _legacy.TryAdd(id, null);
        }
        Interlocked.Increment(ref _version);
        foreach (var id in ids) _legacyPending.TryRemove(id, out _);
    }

    /// <summary>A material asset is an LLSD map {version "1.1", type "GLTF 2.0", data: the GLTF JSON} in any LLSD
    /// serialisation (LLGLTFMaterialList's asset callback); LibreMetaverse's AssetMaterial decodes the bare JSON.</summary>
    static byte[]? UnwrapMaterialAsset(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;
        int i = 0;
        while (i < data.Length && char.IsWhiteSpace((char)data[i])) i++;
        if (i < data.Length && data[i] == '{') return data;   // bare GLTF JSON
        try
        {
            // LibreMetaverse's sniffing misses the spaceless binary header OpenSim writes
            var head = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 20)).Replace(" ", "");
            var osd = head.StartsWith("<?llsd/binary?>") ? OSDParser.DeserializeLLSDBinary(data) : OSDParser.Deserialize(data);
            if (osd is OSDMap map && map["data"] is { Type: OSDType.String } json)
                return System.Text.Encoding.UTF8.GetBytes(json.AsString());
        }
        catch (Exception) { }
        return null;
    }

    public AssetMaterial? Gltf(UUID id, IAssetSource assets, out bool waiting)
    {
        waiting = false;
        if (id == UUID.Zero) return null;
        if (_gltf.TryGetValue(id, out var m)) return m;
        if (_gltfPending.ContainsKey(id)) { waiting = true; return null; }
        if (!MayRetry(id, out waiting)) return null;
        if (_gltfPending.TryAdd(id, 0))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var raw = await assets.GetAssetAsync(id, AssetType.Material, CancellationToken.None).ConfigureAwait(false);
                    if (raw == null) { Failed(id); return; }   // not had now (no capability yet, a network error): later
                    var data = UnwrapMaterialAsset(raw);
                    AssetMaterial? mat = null;
                    if (data != null)
                    {
                        mat = new AssetMaterial(id, data);
                        if (!mat.Decode()) mat = null;
                    }
                    _gltf[id] = mat;   // had, and undecodable if null: that won't change
                    _failures.TryRemove(id, out _);
                    Interlocked.Increment(ref _version);
                }
                catch (Exception e)
                {
                    Log?.Invoke($"gltf material {id}: {e.Message}");
                    Failed(id);
                }
                finally { _gltfPending.TryRemove(id, out _); }
            });
        }
        waiting = true;
        return null;
    }
}
