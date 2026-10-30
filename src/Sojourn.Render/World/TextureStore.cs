using System.Collections.Concurrent;
using CoreJ2K;
using CoreJ2K.Configuration;
using LibreMetaverse;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.World;

public enum TexState { Loading, Ready, Missing }

/// <summary>One grid texture as the renderer holds it. Fields written by workers are published through State.</summary>
public sealed class SceneTexture
{
    public UUID Id { get; }
    volatile TexState _state = TexState.Loading;
    public TexState State { get => _state; internal set => _state = value; }
    public int Slot { get; internal set; } = -1;          // bindless slot when resident
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public int Components { get; internal set; }
    /// <summary>Has an alpha channel with any value below 255 (the viewer's "has alpha" for blend decisions).</summary>
    public bool HasAlpha { get; internal set; }
    /// <summary>The viewer's LLImageGL::analyzeAlphaData verdict: alpha is close enough to binary to render as a mask.</summary>
    public bool IsAlphaMask { get; internal set; }
    /// <summary>Decoded RGBA8 kept for CPU users (sculpt maps); null otherwise.</summary>
    public byte[]? Rgba { get; internal set; }
    /// <summary>A fifth J2C component (avatar bakes' morph mask), rows bottom-up, full size; kept when asked, until
    /// sampled into <see cref="Mask"/>.</summary>
    public byte[]? Aux { get; internal set; }
    public int AuxWidth { get; internal set; }
    public int AuxHeight { get; internal set; }
    /// <summary>A body bake's morph mask, sampled from <see cref="Aux"/> (which then goes) by the avatar renderer.</summary>
    internal Bodies.MorphMask? Mask;
    /// <summary>The discard level resident (0 = full size; each level halves both sides), -1 while none is.</summary>
    public int Discard { get; internal set; } = -1;
    internal GpuImage? Image;
    internal byte[]? Pending;      // decoded RGBA8 level 0 waiting for upload (Width x Height x 4 of it)
    internal bool PendingPooled;   // Pending is rented from the shared array pool: back there once uploaded
    internal int PendingWidth, PendingHeight;
    internal int PendingDiscard;
    /// <summary>When a view last drew it (<see cref="Wanted"/>).</summary>
    internal long LastUsed;
    /// <summary>When it was last part of something within a view's draw distance that wasn't drawn (behind the person,
    /// a worn thing between impostor captures, a HUD with its board away: <see cref="KeepNear"/>, every 10 s or so).</summary>
    internal long NearAt;
    /// <summary>The later of the two: textures are let go longest unseen first, so what's far or left behind goes
    /// before what's only behind the person.</summary>
    internal long Seen => Math.Max(LastUsed, NearAt);
    internal void KeepNear(long now) { if (now > NearAt) NearAt = now; }
    internal bool KeepCpu;
    internal bool KeepAux;
    internal IAssetSource? Source;
    internal int Attempts;
    /// <summary>For a derived bump map: the texture it is generated from, and the bump code (1-17).</summary>
    internal UUID SourceId;
    internal int BumpCode;
    public ulong GpuBytes => Image?.Bytes ?? 0;

    // streaming (ranged sources): what the views want and what's known of the file
    /// <summary>The largest screen area (pixels) a view asked for it with lately; infinity: full size.</summary>
    internal float Area;
    internal long AreaAt;
    internal int FullWidth, FullHeight, Levels = -1;
    internal bool Complete;
    internal int Busy;             // 1 while a fetch or decode runs
    internal int WholeLoad;        // whole loads (not the scheduler's): 1 started (or its result waiting), 2 failed
    internal int Failures;
    internal long RetryAt;
    internal int ShrinkQueued;     // 1 while in the store's queue of textures to bring down a level or more

    internal SceneTexture(UUID id) => Id = id;

    /// <summary>A view used it now, covering about <paramref name="area"/> pixels of screen (infinity: wants it all).</summary>
    internal void Wanted(long now, float area = float.PositiveInfinity)
    {
        LastUsed = now;
        // the largest area over about the last second: a texture seen near, then far, comes down only slowly
        if (area >= Area || now - AreaAt > 1000) { Area = area; AreaAt = now; }
    }
}

/// <summary>
/// Global texture cache shared by every region and view: fetch through an asset source, decode J2C on worker threads
/// (bounded), upload with a GPU-generated mip chain on the render thread, register in the bindless table, and let go
/// of textures longest unseen: over the VRAM budget, when the bindless table runs low, and after minutes neither drawn
/// nor near a view (<see cref="Reclaim"/>; docs/performance.md > Texture slots).
///
/// Textures from a ranged source (<see cref="IRangedAssetSource"/>: the grid's) stream the viewer's way: each one is
/// fetched as far as the largest screen area a view has lately asked for it with needs (LLTextureFetch's byte ranges
/// by discard level, J2c.DataSize), decoded at that level, and refined when a view wants more. A scheduler hands out
/// a fixed number of fetches at once, the textures covering the most screen first, so what's in front of you comes
/// before what's far away or behind you, and textures nobody has asked for in a few seconds aren't fetched at all
/// (leaving a region leaves its queue behind). What's fetched stays on disk, so a later visit decodes from there.
/// </summary>
public sealed class TextureStore : IDisposable
{
    readonly ConcurrentDictionary<UUID, SceneTexture> _textures = new();
    readonly ConcurrentQueue<SceneTexture> _ready = new();
    /// <summary>Decoded avatar bakes (whole loads kept with their morph mask) waiting for upload: ahead of the rest.</summary>
    readonly ConcurrentQueue<SceneTexture> _readyBakes = new();
    /// <summary>Decoded streamed textures with nothing on screen yet (first looks): after the bakes, ahead of sharper
    /// copies of what's already there.</summary>
    readonly ConcurrentQueue<SceneTexture> _readyFirst = new();
    readonly ConcurrentQueue<SceneTexture> _shrink = new();
    readonly SemaphoreSlim _decodeGate;
    readonly CancellationTokenSource _cts = new();
    public int MaxDimension { get; set; } = 2048;
    public ulong ResidentBytes { get; private set; }

    // video memory (Realtime/LoadGovernor.cs sets these): textures held at what they have, and how far those on screen
    // are brought down, the smallest on screen first
    /// <summary>No sharper copies of what's resident; first looks a level down; what's out of view shrinks.</summary>
    public volatile bool HoldGrowth;
    /// <summary>0-13: extra discard levels for textures on screen by their screen area (<see cref="Extra"/>).</summary>
    public volatile int Pressure;
    /// <summary>Decodes at once (of the threads given), set lower while the frame is over its time.</summary>
    public volatile int MaxDecodes = 4;
    int _decodingNow;
    /// <summary>What the store did to stay in memory, for the governor's line.</summary>
    public long Shrunk, ShrunkBytes, Evictions, DroppedUploads;
    /// <summary>Textures let go for having gone unseen <see cref="IdleMs"/> (and their bytes), for slots running low,
    /// and frames an upload or shrink waited for a slot; for the governor's line.</summary>
    public long IdleEvictions, IdleBytes, SlotEvictions, SlotWaits;
    public int Decoding => _inflight;
    int _inflight;
    public Action<string>? Log { get; set; }

    /// <summary>Decoded pixels waiting for their upload (each texture's <see cref="SceneTexture.Pending"/>), in bytes.</summary>
    public long PendingBytes => Interlocked.Read(ref _pendingBytes);
    long _pendingBytes;
    /// <summary>Streamed textures aren't decoded while this much is waiting for upload: under lag the uploads go at a
    /// quarter or less, and decoding ahead of them kept every texture of a busy place in memory as pixels (gigabytes on
    /// the main grid, 2026-10-28). SOJOURN_DECODED_CAP_MB for tests.</summary>
    public static readonly long PendingCap =
        (long.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_DECODED_CAP_MB"), out long cap) && cap > 0 ? cap : 256) << 20;
    /// <summary>Streamed textures fetched and waiting for a decode thread (or decoding), at most.</summary>
    const int MaxToDecode = 16;
    int _toDecode;
    /// <summary>The upload budget a frame last had (<see cref="ProcessUploads"/>): what fits goes without waiting for a
    /// forced turn.</summary>
    long UploadBudget => Volatile.Read(ref _uploadBudget);
    long _uploadBudget = 6L << 20;

    /// <summary>Its pixels at a discard level, as decoded (0 while its size isn't known: counted as small).</summary>
    static long DecodedBytes(SceneTexture t, int level) =>
        t.FullWidth <= 0 ? 0 : (long)Math.Max(1, t.FullWidth >> level) * Math.Max(1, t.FullHeight >> level) * 4;

    /// <summary>Fetches from ranged sources at once (the viewer's texture fetch: 8 connections to a host).</summary>
    public int MaxFetches { get; set; } = 8;
    int _fetching;
    /// <summary>Counters for the console and tests: fetches done, bytes fetched, decodes, failures.</summary>
    public long Fetches, FetchedBytes, Decodes, Failures;
    public int Fetching => Volatile.Read(ref _fetching);
    /// <summary>Textures views wanted lately that aren't yet at the level they want.</summary>
    public int Waiting { get; private set; }

    public TextureStore(int decodeThreads = 4)
    {
        _decodeGate = new SemaphoreSlim(decodeThreads);
        _ = Task.Run(Schedule);
    }

    /// <summary>
    /// Only what's kept as data loads (sculpt maps: their pixels shape a prim); nothing else is fetched, decoded or
    /// uploaded, and what's resident is let go at the next idle passes (<see cref="Reclaim"/>). For a view nobody sees
    /// (Sojourn Access with nothing to look at its pictures: Sojourn.Session/Offscreen/OffscreenRenderer.cs), set through
    /// RenderQuality.Textures. Entries are still made and marked wanted as usual, so turning it off again loads what's
    /// in view as if it had just come into it: streamed ones by the scheduler, whole ones (bakes, bump maps) started then.
    /// </summary>
    public bool DataOnly
    {
        get => _dataOnly;
        set
        {
            if (_dataOnly == value) return;
            _dataOnly = value;
            if (value) return;
            List<SceneTexture> held;
            lock (_deferred) { held = _deferred.ToList(); _deferred.Clear(); }
            foreach (var t in held)
                if (t.State != TexState.Ready && !t.KeepCpu) StartLoad(t);
        }
    }
    volatile bool _dataOnly;
    /// <summary>Whole loads asked for while <see cref="DataOnly"/>: started when it's turned off.</summary>
    readonly HashSet<SceneTexture> _deferred = new();

    /// <summary>A whole load (not the scheduler's), or held for later while only data loads.</summary>
    void LoadWhole(SceneTexture tex)
    {
        if (_dataOnly && !tex.KeepCpu)
        {
            Volatile.Write(ref tex.WholeLoad, 1);   // (a whole-load source asking again doesn't start it either)
            lock (_deferred) _deferred.Add(tex);
            return;
        }
        StartLoad(tex);
    }

    public int Count => _textures.Count;

    /// <summary>Get (and start loading if new) a texture, wanted at full size. keepCpu keeps decoded pixels (sculpt maps).</summary>
    public SceneTexture Request(UUID id, IAssetSource source, bool keepCpu = false, bool keepAux = false) =>
        Request(id, source, float.PositiveInfinity, keepCpu, keepAux);

    /// <summary>Get (and start loading if new) a texture a view shows over about <paramref name="area"/> pixels.</summary>
    public SceneTexture Request(UUID id, IAssetSource source, float area, bool keepCpu = false, bool keepAux = false)
    {
        var tex = _textures.GetOrAdd(id, i => new SceneTexture(i));
        tex.Wanted(System.Environment.TickCount64, area);
        if (keepAux && !tex.KeepAux)
        {
            tex.KeepAux = true;
            if (tex.State == TexState.Ready && tex.Aux == null && tex.Components >= 5) Restart(tex, source);
        }
        if (keepCpu && !tex.KeepCpu)
        {
            tex.KeepCpu = true;
            if (tex.State == TexState.Ready && tex.Rgba == null) Restart(tex, source);
        }
        // An avatar bake can be asked for first by a bakes-on-mesh face, through the grid's ranged source (which doesn't
        // serve server bakes), and then by its wearer's body through the bake source with KeepAux, which takes it off
        // the scheduler: nothing loaded it and the wearer stayed a cloud for good. A whole-load source asking for one
        // not loaded yet takes over and loads it whole, once (2026-10-28, main grid: most avatars in a crowd).
        if (source is not IRangedAssetSource && tex.BumpCode == 0 && tex.Source != null && tex.State != TexState.Ready
            && Volatile.Read(ref tex.WholeLoad) == 0 && Volatile.Read(ref tex.Busy) == 0)
        {
            lock (tex)
            {
                if (tex.Source is IRangedAssetSource) tex.Source = source;
                if (!Ranged(tex) && tex.State != TexState.Ready && Interlocked.CompareExchange(ref tex.WholeLoad, 1, 0) == 0)
                {
                    tex.State = TexState.Loading;
                    LoadWhole(tex);
                }
            }
        }
        if (tex.Source == null)
        {
            lock (tex)
            {
                if (tex.Source == null)
                {
                    tex.Source = source;
                    // ranged sources are the scheduler's; anything else loads whole, at once
                    if (!Ranged(tex)) LoadWhole(tex);
                }
            }
        }
        return tex;
    }

    public bool TryGet(UUID id, out SceneTexture tex) => _textures.TryGetValue(id, out tex!);

    static bool Ranged(SceneTexture t) => t.Source is IRangedAssetSource && !t.KeepAux;

    /// <summary>
    /// The normal map a legacy bump code samples: for codes 3-17 generated from the preset's texture
    /// (<paramref name="textureId"/> = its UUID), for 1 (brightness) and 2 (darkness) from the face's diffuse texture.
    /// </summary>
    public SceneTexture RequestBump(UUID textureId, int code, IAssetSource source)
    {
        var bytes = textureId.GetBytes();
        bytes[0] ^= 0xB5; bytes[15] ^= (byte)(0x40 | code);   // a key no grid asset will have
        var tex = _textures.GetOrAdd(new UUID(bytes, 0), i => new SceneTexture(i) { SourceId = textureId, BumpCode = code });
        // as wanted as the face's own texture (it was asked for just before); a preset's: middling
        tex.Wanted(System.Environment.TickCount64, code <= 2 && _textures.TryGetValue(textureId, out var diffuse) ? diffuse.Area : 1e5f);
        if (tex.Source == null)
        {
            lock (tex)
            {
                if (tex.Source == null)
                {
                    tex.Source = source;
                    // from the grid: the scheduler's, while it's wanted (whole loads went to LibreMetaverse's download
                    // queue, every bumped face's diffuse in full, never cancelled: ahead of every mesh after a while)
                    if (!Ranged(tex)) LoadWhole(tex);
                }
            }
        }
        return tex;
    }

    void Restart(SceneTexture tex, IAssetSource source)
    {
        tex.Source = source;
        if (Ranged(tex)) return;   // the scheduler decodes it again at full size (KeepCpu wants all of it)
        LoadWhole(tex);
    }

    // --- whole textures (bakes, bump maps, sources without ranges) ---------------------------------------------

    void StartLoad(SceneTexture tex)
    {
        Interlocked.Increment(ref _inflight);
        Volatile.Write(ref tex.WholeLoad, 1);
        _ = Task.Run(async () =>
        {
            try
            {
                var data = await tex.Source!.GetTextureAsync(tex.BumpCode != 0 ? tex.SourceId : tex.Id, _cts.Token).ConfigureAwait(false);
                if (data == null) { tex.State = TexState.Missing; Volatile.Write(ref tex.WholeLoad, 2); return; }
                await EnterDecode().ConfigureAwait(false);
                try { Decode(tex, data, 0, -1); }
                finally { ExitDecode(); }
                (tex.KeepAux ? _readyBakes : _ready).Enqueue(tex);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Log?.Invoke($"texture {tex.Id}: {e.GetType().Name}: {e.Message}");
                tex.State = TexState.Missing;
                Volatile.Write(ref tex.WholeLoad, 2);
            }
            finally { Interlocked.Decrement(ref _inflight); }
        });
    }

    // --- streaming -------------------------------------------------------------------------------------------

    /// <summary>The level a texture is wanted at now: what its screen area needs, less what video memory allows.</summary>
    int Want(SceneTexture t) => Want(t, System.Environment.TickCount64);

    /// <summary>Testing (SOJOURN_TEX_FULL=1): every texture wanted at full size wherever it's seen, as if you stood inside
    /// every object: video memory fills the way a busy SL place's did, on the local grid's smaller scenes.</summary>
    static readonly bool FullForTest = System.Environment.GetEnvironmentVariable("SOJOURN_TEX_FULL") == "1";

    int Want(SceneTexture t, long now)
    {
        int want = J2c.WantedLevel(t.FullWidth, t.FullHeight, t.KeepCpu || FullForTest ? float.PositiveInfinity : t.Area, t.Levels, MaxDimension);
        if (t.KeepCpu) return want;
        int extra = Extra(t, now);
        if (extra == 0) return want;
        int cap = Math.Min(J2c.MaxDiscard, t.Levels >= 0 ? t.Levels : J2c.MaxDiscard);
        return Math.Max(want, Math.Min(want + extra, cap));
    }

    /// <summary>The level a streamed texture is fetched and decoded at next: what's wanted, except for one with nothing
    /// on screen yet whose wanted size wouldn't fit a frame's upload budget. That one first comes in small enough to go
    /// up at once (a quarter of the budget: 256x256 at the governor's last level) and is sharpened afterwards like any
    /// other. It used to wait for a forced upload (one every 90 frames under steady lag), and with the decoded cap full
    /// it wasn't decoded at all, so a big texture new to a busy place stayed the grey placeholder for minutes: Missing
    /// Melody's rain walls (alpha 0.5 faces, near-transparent streak texture) drawn as grey sheets, 2026-10-28.</summary>
    int StreamLevel(SceneTexture t, long now)
    {
        int want = Want(t, now);
        if (t.Discard >= 0 || t.KeepCpu || t.BumpCode != 0) return want;
        int w = t.FullWidth > 0 ? t.FullWidth : 1024, h = t.FullHeight > 0 ? t.FullHeight : 1024;   // Stream's guess before the header
        static long Bytes(int w, int h, int level) => (long)Math.Max(1, w >> level) * Math.Max(1, h >> level) * 4;
        long budget = UploadBudget;
        if (Bytes(w, h, want) <= budget) return want;
        int cap = Math.Min(J2c.MaxDiscard, t.Levels >= 0 ? t.Levels : J2c.MaxDiscard);
        int level = want;
        while (level < cap && Bytes(w, h, level) > budget / 4) level++;
        return level;
    }

    /// <summary>Levels below what a texture's screen area wants, to stay in video memory. Out of view for 10 s while
    /// held, or 2 s over the target: three (a 64th of the memory; it sharpens again once looked at and there's room). On screen, by pressure p and the
    /// area's size s = log4(area / 256 px) (16x16 px 0, 64x64 2, 256x256 4, 1024x1024 6, nearer than its radius 8):
    /// ceil((p - s) / 2) levels, at most three, so the smallest on screen go first and the largest last.</summary>
    int Extra(SceneTexture t, long now)
    {
        int p = Pressure;
        bool hold = HoldGrowth;
        if (p == 0 && !hold) return 0;
        if (now - t.LastUsed > (p > 0 ? 2000 : 10_000)) return 3;
        int e = 0;
        if (p > 0)
        {
            double s = float.IsPositiveInfinity(t.Area) ? 8 : Math.Log(Math.Max(t.Area, 1f) / 256.0, 4);
            e = Math.Clamp((int)Math.Ceiling((p - s) / 2), 0, 3);
        }
        if (hold && t.Discard < 0) e = Math.Max(e, 1);   // a first look while held: a level down, sharpened later
        return e;
    }

    // (not while a decode waits for its upload, unless video memory now wants it smaller: the scheduler decoded those
    // again every pass until the upload came, under lag several times over, each pass a file read and a decode)
    bool NeedsWork(SceneTexture t, long now) =>
        t.Busy == 0 && t.Source != null && t.State != TexState.Missing && now - t.LastUsed < 3000 && now >= t.RetryAt && Ranged(t)
        && (!_dataOnly || t.KeepCpu)
        && (t.Pending == null || Want(t, now) > t.PendingDiscard)
        && (t.Discard < 0 || (!HoldGrowth && Want(t, now) < t.Discard) || (t.KeepCpu && t.Rgba == null));

    /// <summary>Resident sharper than video memory allows now: the render thread brings it down (<see cref="ProcessShrinks"/>).</summary>
    bool Shrinkable(SceneTexture t, long now) =>
        t.Image != null && t.Discard >= 0 && !t.KeepCpu && Ranged(t) && Want(t, now) > t.Discard && t.Image.Mips > 1;

    async Task EnterDecode()
    {
        while (true)
        {
            await _decodeGate.WaitAsync(_cts.Token).ConfigureAwait(false);
            if (Interlocked.Increment(ref _decodingNow) <= Math.Max(1, MaxDecodes)) return;
            Interlocked.Decrement(ref _decodingNow);
            _decodeGate.Release();
            await Task.Delay(10, _cts.Token).ConfigureAwait(false);
        }
    }

    void ExitDecode()
    {
        Interlocked.Decrement(ref _decodingNow);
        _decodeGate.Release();
    }

    /// <summary>Every 20 ms: the textures views want more of, most screen first, a fetch each while slots are free.</summary>
    async Task Schedule()
    {
        var picks = new List<(SceneTexture T, float Score)>();
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(20, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                long now = System.Environment.TickCount64;
                picks.Clear();
                int waiting = 0;
                // the dictionary's own enumerator: Values copies all of them under every lock, 50 times a second
                bool shedding = HoldGrowth || Pressure > 0;
                foreach (var kv in _textures)
                {
                    var t = kv.Value;
                    if (shedding && t.ShrinkQueued == 0 && Shrinkable(t, now) && Interlocked.CompareExchange(ref t.ShrinkQueued, 1, 0) == 0)
                        _shrink.Enqueue(t);
                    if (!NeedsWork(t, now)) continue;
                    waiting++;
                    // nothing at all yet counts more than a sharper copy of what's already there
                    // a bump map comes after its face's first look: it adds relief to something already there
                    float score = MathF.Min(t.Area, 1e9f) * (t.Discard < 0 && t.BumpCode == 0 ? 16f : 1f);
                    picks.Add((t, score));
                }
                Waiting = waiting;
                int free = MaxFetches - Volatile.Read(ref _fetching);
                // decoded pixels pile up while uploads lag, and fetched files while decodes do (one thread under lag:
                // ~500 streams were seen waiting with their bytes): nothing new starts past the hard cap or with the
                // decoders' queue full, and past the cap only what a frame's upload budget takes (under lag the big
                // ones go one every few dozen frames; the cap full of them kept every small one waiting behind)
                long pendingNow = PendingBytes;
                if (free <= 0 || picks.Count == 0 || pendingNow >= 2 * PendingCap || Volatile.Read(ref _toDecode) >= MaxToDecode) continue;
                picks.Sort((a, b) => b.Score.CompareTo(a.Score));
                foreach (var (t, _) in picks)
                {
                    if (free <= 0) break;
                    if (pendingNow >= PendingCap && DecodedBytes(t, StreamLevel(t, now)) > UploadBudget) continue;
                    if (Interlocked.CompareExchange(ref t.Busy, 1, 0) != 0) continue;
                    free--;
                    Interlocked.Increment(ref _fetching);
                    _ = Task.Run(() => Stream(t));
                }
            }
            catch (Exception e) { Log?.Invoke($"texture scheduler: {e.Message}"); }
        }
    }

    /// <summary>One step for a streamed texture: what's on disk, then the bytes its wanted level needs, then a decode
    /// at that level (or as near it as the bytes allow).</summary>
    async Task Stream(SceneTexture t)
    {
        bool fetchSlot = true;
        try
        {
            var src = (IRangedAssetSource)t.Source!;
            var file = t.BumpCode != 0 ? t.SourceId : t.Id;   // a bump map is made from its source's file, at the level its face wants (the viewer's from the diffuse as loaded)
            var (data, complete) = src.CachedTexture(file);
            data ??= Array.Empty<byte>();
            if (complete) t.Complete = true;
            if (t.Levels < 0 && J2c.Header(data, out int w0, out int h0, out _, out int l0)) (t.FullWidth, t.FullHeight, t.Levels) = (w0, h0, l0);

            int want = StreamLevel(t, System.Environment.TickCount64);
            if (!t.Complete)
            {
                // the viewer's rule: the whole file for the full size; else the estimate for the level (until the header
                // is in, as if the texture were 1024 square, the usual size)
                int need = want == 0 ? int.MaxValue : J2c.DataSize(t.FullWidth > 0 ? t.FullWidth : 1024, t.FullHeight > 0 ? t.FullHeight : 1024, want);
                if (data.Length < need)
                {
                    int ask = need == int.MaxValue ? -1 : need - data.Length;
                    var r = await src.TextureRangeAsync(file, data.Length, ask, _cts.Token).ConfigureAwait(false);
                    switch (r.Status)
                    {
                        case FetchStatus.NotFound:
                            t.State = TexState.Missing;
                            return;
                        case FetchStatus.Unavailable:
                            t.RetryAt = System.Environment.TickCount64 + 1000;
                            return;
                        case FetchStatus.Retry:
                            Interlocked.Increment(ref Failures);
                            if (++t.Failures > 8) { t.State = TexState.Missing; Log?.Invoke($"texture {t.Id}: gave up after {t.Failures} failed fetches"); return; }
                            t.RetryAt = System.Environment.TickCount64 + (long)Math.Max(r.RetryAfter.TotalMilliseconds, Math.Min(60_000, 500 << Math.Min(t.Failures, 7)));
                            return;
                    }
                    var got = r.Data ?? Array.Empty<byte>();
                    Interlocked.Increment(ref Fetches);
                    Interlocked.Add(ref FetchedBytes, got.Length);
                    t.Failures = 0;
                    var joined = new byte[data.Length + got.Length];
                    data.CopyTo(joined, 0);
                    got.CopyTo(joined, data.Length);
                    data = joined;
                    t.Complete = r.ToEnd || (r.Total > 0 && data.Length >= r.Total) || (ask > 0 && got.Length < ask);
                    src.StoreTexture(file, data, t.Complete);
                    if (t.Levels < 0 && J2c.Header(data, out int w1, out int h1, out _, out int l1)) (t.FullWidth, t.FullHeight, t.Levels) = (w1, h1, l1);
                }
            }
            Interlocked.Decrement(ref _fetching);
            fetchSlot = false;

            if (t.Levels < 0 || data.Length == 0)
            {
                // not even a header: a corrupt or empty asset
                t.State = TexState.Missing;
                return;
            }
            want = StreamLevel(t, System.Environment.TickCount64);
            int level = J2c.LevelFor(t.FullWidth, t.FullHeight, data.Length, t.Complete, t.Levels);
            if (level < 0) { t.RetryAt = System.Environment.TickCount64 + 200; return; }   // too little yet: the next pass fetches on
            level = Math.Max(level, want);
            if (t.Discard >= 0 && level >= t.Discard && !(t.KeepCpu && t.Rgba == null)) return;   // nothing better than what's resident
            // (a fetch that found it big past the cap: its bytes are on disk, decoded once the uploads have caught up)
            if (PendingBytes >= 2 * PendingCap || PendingBytes >= PendingCap && DecodedBytes(t, level) > UploadBudget)
            {
                t.RetryAt = System.Environment.TickCount64 + 250;
                return;
            }
            Interlocked.Increment(ref _toDecode);
            try
            {
                await EnterDecode().ConfigureAwait(false);
                try { Decode(t, data, level, t.Levels); }
                finally { ExitDecode(); }
            }
            finally { Interlocked.Decrement(ref _toDecode); }
            Interlocked.Increment(ref Decodes);
            (t.Discard < 0 ? _readyFirst : _ready).Enqueue(t);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Interlocked.Increment(ref Failures);
            Log?.Invoke($"texture {t.Id}: {e.GetType().Name}: {e.Message}");
            if (++t.Failures > 3) t.State = TexState.Missing;
            else t.RetryAt = System.Environment.TickCount64 + 2000;
        }
        finally
        {
            if (fetchSlot) Interlocked.Decrement(ref _fetching);
            Volatile.Write(ref t.Busy, 0);
        }
    }

    /// <summary>Decode a codestream (whole, or a prefix) at a discard level (0: full size). <paramref name="levels"/>:
    /// its decomposition levels (-1: unknown, decode at full size).</summary>
    void Decode(SceneTexture tex, byte[] data, int discard, int levels)
    {
        // OpenJPEG when it's there (fast, little garbage); CoreJ2K otherwise, or for what OpenJPEG won't take
        int reduce = levels >= 0 && discard > 0 ? Math.Min(discard, levels) : 0;
        byte[][] comps;
        int w, h, nc;
        bool pooledComps = false;
        if (OpenJpeg.Decode(data, reduce, pooled: true) is { } native)
        {
            (comps, w, h) = native;
            nc = comps.Length;
            pooledComps = true;
        }
        else
        {
            var cfg = new J2KDecoderConfiguration().WithMaxDegreeOfParallelism(1);
            if (reduce > 0) cfg = cfg.WithResolutionLevel(Math.Max(0, levels - reduce));
            using var img = cfg.Decode(data).Image;
            (w, h, nc) = (img.Width, img.Height, img.NumberOfComponents);
            comps = new byte[nc][];
            for (int c = 0; c < nc; c++)
            {
                comps[c] = new byte[w * h];
                img.ToComponentBytes(c, comps[c]);
            }
        }
        // stored bottom-up like the viewer's decoders: row 0 is the bottom of the picture, so SL UV (0,0) is the
        // image's bottom-left and v needs no flipping anywhere downstream
        // the pixels go to the GPU and are dropped: rented, unless they're kept (sculpt maps) or made into a bump map
        bool pooled = !tex.KeepCpu && tex.BumpCode == 0;
        var rgba = pooled ? System.Buffers.ArrayPool<byte>.Shared.Rent(w * h * 4) : new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int dst = ((h - 1 - i / w) * w + i % w) * 4;
            byte r, g, b, a = 255;
            switch (nc)
            {
                case 1: r = g = b = comps[0][i]; break;
                case 2: r = g = b = comps[0][i]; a = comps[1][i]; break;
                case 3: r = comps[0][i]; g = comps[1][i]; b = comps[2][i]; break;
                default: r = comps[0][i]; g = comps[1][i]; b = comps[2][i]; a = comps[3][i]; break;
            }
            rgba[dst] = r; rgba[dst + 1] = g; rgba[dst + 2] = b; rgba[dst + 3] = a;
        }
        if (tex.KeepAux && nc >= 5)
        {
            var aux = new byte[w * h];
            for (int y = 0; y < h; y++) Array.Copy(comps[4], y * w, aux, (h - 1 - y) * w, w);
            tex.AuxWidth = w;
            tex.AuxHeight = h;
            tex.Aux = aux;
        }
        if (pooledComps) OpenJpeg.Return(comps);
        // the viewer clamps to its max texture size by discarding levels
        int extra = 0;
        while (w > MaxDimension || h > MaxDimension)
        {
            var halved = Halve(rgba, w, h);
            if (pooled) { System.Buffers.ArrayPool<byte>.Shared.Return(rgba); pooled = false; }
            rgba = halved;
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            extra++;
        }
        if (tex.BumpCode != 0)
        {
            rgba = tex.BumpCode <= 2 ? BumpMaps.FromBrightness(rgba, w, h, tex.BumpCode == 2) : BumpMaps.FromHeight(rgba, w, h, nc);
            nc = 4;
        }
        bool hasAlpha = false;
        if (tex.BumpCode == 0 && (nc == 2 || nc >= 4))
            for (int i = 3, end = w * h * 4; i < end; i += 4)
                if (rgba[i] != 255) { hasAlpha = true; break; }
        tex.Width = w;
        tex.Height = h;
        tex.Components = nc;
        tex.HasAlpha = hasAlpha;
        // an alpha channel that's all 255 is a mask to the viewer too (analyzeAlphaData: alphatotal == 255 * length), so
        // a face with such a texture renders opaque with depth, not blended (meshes exported as RGBA showed their own
        // back faces through their front, sorted per face without depth)
        tex.IsAlphaMask = tex.BumpCode == 0 && (nc == 2 || nc >= 4) && (!hasAlpha || AnalyzeAlphaMask(rgba, w, h));
        tex.PendingDiscard = discard + extra;
        // handed over under the texture's lock: a decode can finish again before the render thread took the last one
        lock (tex)
        {
            TakePending(tex);
            tex.PendingPooled = pooled;
            (tex.PendingWidth, tex.PendingHeight) = (w, h);
            tex.Pending = rgba;
            Interlocked.Add(ref _pendingBytes, (long)w * h * 4);
        }
        if (tex.KeepCpu) tex.Rgba = rgba;
    }

    /// <summary>Under the texture's lock: its decoded pixels taken off it (null if none), out of the waiting count.</summary>
    byte[]? TakePending(SceneTexture tex, bool release = true)
    {
        if (tex.Pending is not { } px) return null;
        tex.Pending = null;
        Interlocked.Add(ref _pendingBytes, -(long)tex.PendingWidth * tex.PendingHeight * 4);
        if (release && tex.PendingPooled) System.Buffers.ArrayPool<byte>.Shared.Return(px);
        return px;
    }

    public static byte[] HalveRgba(byte[] src, int w, int h) => Halve(src, w, h);

    static byte[] Halve(byte[] src, int w, int h)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                for (int c = 0; c < 4; c++)
                {
                    int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1);
                    int y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                    int s = src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c];
                    dst[(y * nw + x) * 4 + c] = (byte)((s + 2) / 4);
                }
        return dst;
    }

    /// <summary>Port of the viewer's LLImageGL::analyzeAlphaData (llrender/llimagegl.cpp, LGPL 2.1).</summary>
    public static bool AnalyzeAlphaMask(byte[] rgba, int w, int h)
    {
        uint length = (uint)(w * h);
        uint alphatotal = 0;
        var sample = new uint[16];
        const int stride = 4;
        if (w >= 2 && h >= 2 && w % 2 == 0 && h % 2 == 0)
        {
            int rowstart = 3;
            for (int y = 0; y < h; y += 2)
            {
                int current = rowstart;
                for (int x = 0; x < w; x += 2)
                {
                    uint s1 = rgba[current];
                    alphatotal += s1;
                    uint s2 = rgba[current + w * stride];
                    alphatotal += s2;
                    current += stride;
                    uint s3 = rgba[current];
                    alphatotal += s3;
                    uint s4 = rgba[current + w * stride];
                    alphatotal += s4;
                    current += stride;
                    ++sample[s1 / 16];
                    ++sample[s2 / 16];
                    ++sample[s3 / 16];
                    ++sample[s4 / 16];
                    uint asum = s1 + s2 + s3 + s4;
                    alphatotal += asum;
                    sample[asum / (16 * 4)] += 4;
                }
                rowstart += 2 * w * stride;
            }
            length *= 2;
        }
        else
        {
            for (int i = 0; i < length; i++)
            {
                uint s1 = rgba[i * 4 + 3];
                alphatotal += s1;
                ++sample[s1 / 16];
            }
        }
        uint midrangetotal = 0;
        for (int i = 2; i < 13; i++) midrangetotal += sample[i];
        uint lowerhalftotal = 0;
        for (int i = 0; i < 8; i++) lowerhalftotal += sample[i];
        uint upperhalftotal = 0;
        for (int i = 8; i < 16; i++) upperhalftotal += sample[i];
        if (midrangetotal > length / 48 ||
            (lowerhalftotal == length && alphatotal != 0) ||
            (upperhalftotal == length && alphatotal != 255 * length))
            return false;
        return true;
    }

    /// <summary>Render thread: create GPU images for decoded textures (up to a byte budget per call: uploads and mip
    /// generation are frame time) and record their uploads + mip generation. Returns how many became resident.
    /// <paramref name="forceOne"/>: one goes even over the budgets (so loading never stops); <paramref name="forceBake"/>:
    /// one avatar bake does (a wearer is a cloud until its bakes are up). <paramref name="streamBudget"/>: the budget
    /// streaming plans first looks and decodes by (0: <paramref name="maxBytes"/>), steadier than a frame's share of
    /// the GPU's spare time.</summary>
    public int ProcessUploads(VulkanContext vk, Bindless bindless, Uploader up, ulong maxBytes = 12UL << 20, double maxMs = 1.5,
        bool forceOne = true, bool forceBake = true, ulong streamBudget = 0)
    {
        int n = 0;
        ulong bytes = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long now = System.Environment.TickCount64;
        int skipped = 0;
        Volatile.Write(ref _uploadBudget, (long)(streamBudget > 0 ? streamBudget : maxBytes));
        bool bakesBlocked = false, firstBlocked = false;
        // A byte and a time budget a frame, with one upload over them when forced. Under steady lag (the governor's
        // budget 1.5 MB, forced every 90 frames) the forced one used to be only the queue's head, so textures bigger
        // than the budget (a 1024 square is 4 MB, a 2048 bake 16) waited for good behind the small ones: the over-budget
        // one is now the first that doesn't fit, wherever it is, and bakes have their own queue, ahead of the rest.
        // First looks (StreamLevel) come next, sized to fit the budget: one that doesn't fit what's left waits for the
        // next frame rather than taking the forced turn, which stays for the sharper copies too big for any frame.
        while (true)
        {
            bool inBudget = bytes < maxBytes && System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds < maxMs;
            if (!inBudget && !forceOne && !(forceBake && !bakesBlocked && !_readyBakes.IsEmpty)) break;
            SceneTexture? next = null;
            bool bake = !bakesBlocked && _readyBakes.TryDequeue(out next);
            bool first = !bake && !firstBlocked && _readyFirst.TryDequeue(out next);
            if (!bake && !first && !_ready.TryDequeue(out next)) break;
            var tex = next!;
            var queue = bake ? _readyBakes : first ? _readyFirst : _ready;
            ulong size = (ulong)tex.PendingWidth * (uint)tex.PendingHeight * 4;
            if (!inBudget || bytes + size > maxBytes)
            {
                if (bake && forceBake) forceBake = false;
                else if (first && size <= maxBytes) { queue.Enqueue(tex); firstBlocked = true; continue; }
                else if (forceOne) forceOne = false;
                else
                {
                    // too big for what's left of this frame's budget: to the back of its queue, so the smaller ones
                    // behind it still go
                    queue.Enqueue(tex);
                    if (bake) { bakesBlocked = true; continue; }
                    if (first) { firstBlocked = true; continue; }
                    if (++skipped >= 16) break;
                    continue;
                }
            }
            // a new slot (the old one may still be in a frame in flight): with the table low, the upload goes back in the
            // queue and waits while Reclaim lets go of what's longest unseen
            if (!bindless.TryAllocTexture(SlotReserve, out int slot))
            {
                queue.Enqueue(tex);
                SlotWaits++;
                break;
            }
            byte[]? px;
            bool pooled;
            uint w, h;
            lock (tex) { (pooled, w, h) = (tex.PendingPooled, (uint)tex.PendingWidth, (uint)tex.PendingHeight); px = TakePending(tex, release: false); }
            // (a slot never written to or sampled goes straight back)
            if (px == null) { bindless.FreeTexture(slot); continue; }
            // a sharper copy of something resident that video memory has no room for now (decoded before the hold or
            // the pressure came): dropped; the scheduler asks again when there's room
            if (tex.Image != null && tex.PendingDiscard < tex.Discard && !tex.KeepCpu && Ranged(tex) && (HoldGrowth || tex.PendingDiscard < Want(tex, now)))
            {
                if (pooled) System.Buffers.ArrayPool<byte>.Shared.Return(px);
                bindless.FreeTexture(slot);
                DroppedUploads++;
                continue;
            }
            uint mips = (uint)Math.Floor(Math.Log2(Math.Max(w, h))) + 1;
            var img = new GpuImage(vk, w, h, Format.R8G8B8A8Unorm,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit, mips);
            up.CopyToImageWithMips(img, px.AsSpan(0, (int)(w * h * 4)));
            if (pooled) System.Buffers.ArrayPool<byte>.Shared.Return(px);   // copied into the staging buffer
            bindless.Write(slot, img.View, bindless.Repeat);
            if (tex.Image != null)
            {
                ResidentBytes -= tex.Image.Bytes;
                RetireImage(tex.Image, tex.Slot);
            }
            tex.Image = img;
            tex.Slot = slot;
            tex.Discard = tex.PendingDiscard;
            ResidentBytes += img.Bytes;
            tex.State = TexState.Ready;
            bytes += w * h * 4;
            n++;
        }
        return n;
    }

    /// <summary>Called with images to free once no in-flight frame uses them (set by the owner, who calls
    /// <see cref="SlotFreed"/> as each slot goes back to the table).</summary>
    public Action<GpuImage, int>? Retire { get; set; }

    // --- slots ---------------------------------------------------------------------------------------------------

    /// <summary>Slots world textures leave free for the renderer's own images (targets, mirrors, HUD, media).</summary>
    public const int SlotReserve = 1024;
    /// <summary>Unseen this long (neither drawn nor near a view), a texture is let go whatever the memory: 3 minutes
    /// (SOJOURN_TEXTURE_IDLE_MS=N for tests, at least 10 s).</summary>
    public static readonly long IdleMs =
        long.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_TEXTURE_IDLE_MS"), out long idle) && idle >= 10_000 ? idle : 180_000;
    /// <summary>At most this many idle textures let go a pass (every 2 s: a place left behind drains over a minute or so
    /// rather than freeing thousands of images in one frame).</summary>
    const int IdlePerPass = 256;
    /// <summary>Slots let go at most a frame for a low table (the rest the next frames, counting those on their way).</summary>
    const int SlotsPerFrame = 1024;
    int _retiringSlots;
    long _slotLogAt;

    void RetireImage(GpuImage img, int slot)
    {
        if (Retire == null) return;
        if (slot >= 0) Interlocked.Increment(ref _retiringSlots);
        Retire(img, slot);
    }

    /// <summary>A retired texture's slot went back to the table (the owner's <see cref="Retire"/>).</summary>
    public void SlotFreed() => Interlocked.Decrement(ref _retiringSlots);

    /// <summary>Slots world textures can still take, counting those on their way back.</summary>
    int SlotsLeft(Bindless b) => b.TexturesFree - SlotReserve + Volatile.Read(ref _retiringSlots);

    // (the times are taken once: views on other threads can mark a texture used mid-sort)
    readonly List<(long Seen, SceneTexture T)> _reclaim = new();

    /// <summary>Render thread, under the face resolvers' write lock. Every frame (<paramref name="idle"/> false) lets go
    /// of textures when the slot table runs low: longest unseen first (left behind, out of draw distance, then behind
    /// the person), none drawn in the last 2 s, until a sixteenth of the table is free again counting slots on their
    /// way back. Every 2 s (<paramref name="idle"/>) also those unseen <see cref="IdleMs"/>, a few hundred a pass, and
    /// while only data loads (<see cref="DataOnly"/>) every one not kept as data, drawn or not. Let go textures load
    /// again when next wanted (streamed ones from disk). Returns how many went.</summary>
    public int Reclaim(Bindless bindless, bool idle)
    {
        int max = (int)bindless.MaxTextures - SlotReserve;
        int low = Math.Max(512, max / 16), high = Math.Max(1024, max / 8);
        int left = SlotsLeft(bindless);
        int need = left < low ? Math.Min(SlotsPerFrame, high - left) : 0;
        if (need <= 0 && !idle) return 0;
        long now = System.Environment.TickCount64;
        bool dataOnly = _dataOnly && idle;
        _reclaim.Clear();
        foreach (var kv in _textures)
        {
            var t = kv.Value;
            if (t.Image == null) continue;
            // (unwanted: counted as unseen at least the idle time, so they all go, a pass's worth at a time)
            if (dataOnly && !t.KeepCpu) { _reclaim.Add((Math.Min(t.Seen, now - IdleMs), t)); continue; }
            if (now - t.LastUsed < 2000) continue;
            long seen = t.Seen;
            if (need > 0 || now - seen >= IdleMs) _reclaim.Add((seen, t));
        }
        if (_reclaim.Count == 0)
        {
            if (need > 0) SlotLog(now, $"texture slots low: {bindless.TexturesUsed} of {bindless.MaxTextures} taken, nothing unseen to let go");
            return 0;
        }
        _reclaim.Sort((a, b) => a.Seen.CompareTo(b.Seen));
        int n = 0, idleN = 0, slotN = 0;
        long oldest = now - _reclaim[0].Seen;
        foreach (var (seen, t) in _reclaim)
        {
            if (idle && idleN < IdlePerPass && now - seen >= IdleMs)
            {
                IdleBytes += (long)t.Image!.Bytes;
                idleN++;
            }
            else if (slotN < need) slotN++;
            else break;
            Drop(t);
            n++;
        }
        IdleEvictions += idleN;
        SlotEvictions += slotN;
        if (slotN > 0)
            SlotLog(now, $"texture slots low: {bindless.TexturesUsed} of {bindless.MaxTextures} taken ({SlotReserve} kept for the renderer); " +
                         $"let go of {slotN} textures unseen longest (from {oldest / 1000} s ago)");
        return n;
    }

    void SlotLog(long now, string line)
    {
        if (now - _slotLogAt < 5000) return;
        _slotLogAt = now;
        Log?.Invoke(line);
    }

    /// <summary>A texture's image and slot let go (to come back from its source when next wanted).</summary>
    void Drop(SceneTexture tex)
    {
        ResidentBytes -= tex.Image!.Bytes;
        RetireImage(tex.Image, tex.Slot);
        tex.Image = null;
        tex.Slot = -1;
        tex.Discard = -1;
        tex.State = TexState.Loading;
        if (!Ranged(tex)) tex.Source = null;   // next Request reloads it (streamed ones: the scheduler, from disk)
        // what was kept on the CPU goes too (a bake's morph mask, a sculpt map's pixels, a sharper copy waiting): the
        // entry stays for good, so these were kept for every avatar and sculpt seen all session; a reload decodes them
        lock (tex) TakePending(tex);
        tex.Aux = null;
        tex.Mask = null;
        tex.Rgba = null;
    }

    /// <summary>Render thread, each frame while video memory is short: bring textures the scheduler found sharper than
    /// allowed down to their level, on the GPU (the image's own smaller mips copied into a smaller image; no decode),
    /// at most <paramref name="max"/> a frame. Frees memory at once (the old image three frames on).</summary>
    public unsafe int ProcessShrinks(VulkanContext vk, Bindless bindless, Uploader up, int max = 48)
    {
        int n = 0;
        long now = System.Environment.TickCount64;
        while (n < max && _shrink.TryDequeue(out var tex))
        {
            Volatile.Write(ref tex.ShrinkQueued, 0);
            if (!(HoldGrowth || Pressure > 0) || !Shrinkable(tex, now) || tex.Pending != null) continue;
            var old = tex.Image!;
            int k = Math.Min(Want(tex, now) - tex.Discard, (int)old.Mips - 1);
            if (k <= 0) continue;
            // (no slot: the scheduler queues it again once the table has room)
            if (!bindless.TryAllocTexture(SlotReserve, out int slot)) { SlotWaits++; break; }
            uint w = Math.Max(1, old.Width >> k), h = Math.Max(1, old.Height >> k), mips = old.Mips - (uint)k;
            var img = new GpuImage(vk, w, h, Format.R8G8B8A8Unorm,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit, mips);
            uint from = (uint)k;
            up.Record(cmd =>
            {
                var api = vk.Api;
                GpuImage.Barrier(vk, cmd, old.Handle, old.Aspect, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal, from, mips, 0, 1);
                GpuImage.Barrier(vk, cmd, img.Handle, img.Aspect, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, mips, 0, 1);
                var regions = stackalloc ImageCopy[(int)mips];
                for (uint m = 0; m < mips; m++)
                    regions[m] = new ImageCopy
                    {
                        SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, from + m, 0, 1),
                        DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m, 0, 1),
                        Extent = new Extent3D(Math.Max(1, w >> (int)m), Math.Max(1, h >> (int)m), 1),
                    };
                api.CmdCopyImage(cmd, old.Handle, ImageLayout.TransferSrcOptimal, img.Handle, ImageLayout.TransferDstOptimal, mips, regions);
                GpuImage.Barrier(vk, cmd, img.Handle, img.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, 0, mips, 0, 1);
                // a face drawn as it was for a frame or two still samples the old one
                GpuImage.Barrier(vk, cmd, old.Handle, old.Aspect, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, from, mips, 0, 1);
            });
            img.Layout = ImageLayout.ShaderReadOnlyOptimal;
            bindless.Write(slot, img.View, bindless.Repeat);
            ShrunkBytes += (long)(old.Bytes - img.Bytes);
            ResidentBytes = ResidentBytes - old.Bytes + img.Bytes;
            RetireImage(old, tex.Slot);
            tex.Image = img;
            tex.Slot = slot;
            tex.Discard += k;
            (tex.Width, tex.Height) = ((int)w, (int)h);
            Shrunk++;
            n++;
        }
        return n;
    }

    readonly List<(long Seen, SceneTexture T)> _evictable = new();

    /// <summary>While video memory is over its target: evict what no view has drawn for <paramref name="idleMs"/>,
    /// longest unseen first (what's far or left behind before what's only behind the person), up to
    /// <paramref name="maxBytes"/> a call. Evicted textures reload on next request (streamed ones from disk).</summary>
    public int Evict(long idleMs = 5000, ulong maxBytes = 512UL << 20)
    {
        int n = 0;
        ulong freed = 0;
        // never evict what the current views use; render thread, every 2 s: no copy of the whole dictionary
        long now = System.Environment.TickCount64;
        _evictable.Clear();
        foreach (var kv in _textures)
            if (kv.Value.Image != null && now - kv.Value.LastUsed >= idleMs) _evictable.Add((kv.Value.Seen, kv.Value));
        _evictable.Sort((a, b) => a.Seen.CompareTo(b.Seen));
        foreach (var (_, tex) in _evictable)
        {
            if (freed >= maxBytes) break;
            freed += tex.Image!.Bytes;
            Evictions++;
            Drop(tex);
            n++;
        }
        return n;
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var t in _textures.Values) t.Image?.Dispose();
    }
}
