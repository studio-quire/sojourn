namespace Sojourn.Render.Realtime;

/// <summary>
/// Keeps loading from dragging the frame down or overfilling video memory (Claude Opus 5.5, 2026-10-27, after the GPU
/// paged itself into a device loss at a busy place; docs/performance.md > The load governor). It never changes how
/// what's loaded looks, and loading may slow down but never stop (the user's rules): it works on how fast things load
/// and unload.
///
/// The frame (half-second windows, the mean of each frame's dearer of GPU and render-thread CPU against the display's
/// period): over the period, uploads, face resolving and decoding go at half speed; over 1.4 periods at a quarter;
/// over 1.8 (lagging too much: the runtime is about to show every third frame) new objects beyond 16 m are held and let
/// in at a rate, largest on screen first, 32 a second doubling every 4 s the hold lasts: busy places can run at 2
/// periods for good (MagnetiS, 2026-10-27), and a hold that doesn't bring the frame down is the place, not its loading.
/// From 1.4 periods, what's far or unseen is brought up to date less often too: impostors' poses (far avatars),
/// reflection probe captures. A person can turn this part off (the <c>load_governor</c> setting).
///
/// Two levers change what's drawn far away (the user's approval, 2026-10-27), only after frames have run over the
/// period for 1.5 s, a step every half second while they still do: shadows reach less far (the last cascade's far end,
/// 4% of it a step, down to half; never off, map sizes and the person's settings untouched), and avatars beyond a
/// distance become impostors (from 48 m in by 2 m a step, never nearer than 20 m; each avatar with its own 15% band, so
/// nobody flips at the boundary). They come back after 5 s under 0.85 periods, at half the speed they went, and for a
/// minute after the last cut no further than a step past where it stopped lagging.
///
/// Never oscillating (frames sometimes on time and sometimes late showed as jitter, 2026-10-27): a level rises only
/// after a second over its threshold (at once only when the GPU is paging, three periods a frame) and falls only after
/// 4 s under 0.8 of it; the levels change how fast things load, never what's drawn, and an object held back is one
/// never drawn, so nothing drawn is taken away by them (once let in, an object is never held again).
///
/// Video memory (four times a second, VK_EXT_memory_budget: what this process uses against what Windows budgets
/// it, less our own allocator's free space): within 512 MB of the target (the budget less a headroom for the
/// headset's compositor and Link's encoder), textures stop growing (no sharper copies, first looks a level down) and
/// what's out of view shrinks; over the target, textures on screen come down a level at a time, the smallest on screen
/// first (TextureStore.Pressure), and what's long out of view is evicted. Pressure eases a step only 20 s after it last
/// rose and a GB under the target, then every 4 s, so textures don't go sharp and soft and sharp. Always on: it's what
/// keeps the driver from paging. If the pressure is at its top and still over for 1.5 s, the draw distance comes in
/// from its far end, 4 m a window, to half the person's (48 m at least); it goes back out after 5 s with textures free
/// to grow, 2 m a window, and for a minute after its last cut no further than 4 m past where it stopped. At its edge a
/// band of 6 m keeps what's drawn drawn (ViewSetup.NewDistance). Slow frames alone don't move it: far new objects
/// already wait, so a cut only took away what was drawn past it and kept anything beyond from ever loading (it sat at
/// its floor for a whole session at MagnetiS).
///
/// Loading's GPU work goes in the frame's spare GPU time (2026-10-28, after arriving at Mirromere kept the GPU over its
/// period in every frame for minutes; docs/performance.md > The load governor > Loading in the GPU's spare time): each
/// frame's timestamps say what the scene costs without its uploads and probe captures, and the upload budget and the
/// probe faces a frame are held to what the period leaves after the scene (90% of it; never less than 2% of the period,
/// so loading slows but never stops, and a little saved up so a big texture or mesh goes in a frame that has room). The
/// levels above still cap them. The costs (bytes per GPU ms, a probe face's ms) are learnt from the timestamps.
/// </summary>
public sealed class LoadGovernor
{
    /// <summary>The frame side (the person's setting). Memory is governed regardless.</summary>
    public volatile bool Enabled = true;

    // --- frame ---
    static readonly double[] Thresholds = { 1.0, 1.4, 1.8 };
    public int FrameLevel { get; private set; }
    /// <summary>The draw distance's cap, metres (infinity: none).</summary>
    public float DrawCap { get; private set; } = float.PositiveInfinity;
    public double LastRatio { get; private set; }
    public double LastCostMs { get; private set; }
    public double PeriodMs { get; private set; } = 1000.0 / 60;
    double _gpuSum, _cpuSum;
    int _gpuN, _cpuN, _hot, _calm, _ddCalm, _ddOver, _levelBefore;
    float _ceiling = float.PositiveInfinity;
    long _lastCutAt, _holdAt;
    long _windowAt = System.Environment.TickCount64;
    float _person = 128;

    // --- trims: what's drawn far away, while frames stay over the period ---
    /// <summary>How far shadows reach, as a share of where the viewer's cascades would end (the last cascade's far end;
    /// FrameRenderer, Shadows/SunShadowSetup.cs).</summary>
    public float ShadowReach { get; private set; } = 1f;
    /// <summary>Avatars further than this (metres) are impostors whatever the count (World/Avatars.Crowd.cs); infinity:
    /// none by distance.</summary>
    public float ImpostorBeyond { get; private set; } = float.PositiveInfinity;
    /// <summary>Where the shadows' cascades end now and would end untrimmed (metres; FrameRenderer reports them).</summary>
    public float ShadowFar, ShadowFullFar;
    /// <summary>The coarse far cascade carries shadows on past <see cref="ShadowFar"/> (FrameRenderer.FarShadow).</summary>
    public bool ShadowCoarse;
    const double TrimOver = 1.05, TrimCalm = 0.85;
    const float ReachFloor = 0.5f, ReachStep = 0.04f, ImpostorStart = 48f, ImpostorFloor = 20f, ImpostorStep = 2f;
    int _trimOver, _trimCalm;
    long _lastTrimAt = long.MinValue / 2;
    float _reachCeiling = 1f, _impostorCeiling = float.PositiveInfinity, _minReach = 1f, _minImpostor = float.PositiveInfinity;
    bool Trimmed => ShadowReach < 1f || !float.IsPositiveInfinity(ImpostorBeyond);

    // --- memory ---
    public const int MaxPressure = 13;
    public ulong Usage { get; private set; }
    public ulong Budget { get; private set; }
    public ulong Target { get; private set; }
    public ulong Effective { get; private set; }
    /// <summary>Textures stop growing (TextureStore.HoldGrowth).</summary>
    public bool HoldGrowth { get; private set; }
    /// <summary>How far textures on screen are brought down (TextureStore.Pressure, 0-13).</summary>
    public int Pressure { get; private set; }
    public bool OverTarget { get; private set; }
    long _memAt, _easeAt;
    /// <summary>Testing: a budget this many MB at most (SOJOURN_VRAM_BUDGET_MB), to see shedding without filling the card.</summary>
    public static readonly ulong TestBudget = ulong.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_VRAM_BUDGET_MB"), out var mb) ? mb << 20 : 0;
    /// <summary>Testing only (SOJOURN_VRAM_GOVERNOR=0): video memory measured and logged but not governed, for a before.</summary>
    static readonly bool MemoryOff = System.Environment.GetEnvironmentVariable("SOJOURN_VRAM_GOVERNOR") == "0";
    /// <summary>Testing (SOJOURN_GOVERNOR_PERIOD_MS=N): the frame judged against this period instead of the display's, to
    /// make the governor act on the desktop.</summary>
    static readonly double TestPeriod = double.TryParse(System.Environment.GetEnvironmentVariable("SOJOURN_GOVERNOR_PERIOD_MS"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tp) ? tp : 0;

    // --- what it did, for the 10 s line ---
    public long Shrunk, ShrunkBytes, Evicted, HeldUploads, HeldObjects, AdmittedObjects;
    /// <summary>Textures let go for going unseen for minutes (and their bytes), for the slot table running low, and
    /// frames an upload waited for a slot (World/TextureStore.Reclaim); the table's slots taken and its size.</summary>
    public long IdleEvicted, IdleBytes, SlotEvicted, SlotWaits;
    public int SlotsUsed { get; private set; }
    public int SlotsMax { get; private set; }
    int _maxLevel, _maxPressure, _maxSlots;

    /// <summary>The texture table now (each frame).</summary>
    public void Slots(int used, int max)
    {
        (SlotsUsed, SlotsMax) = (used, max);
        _maxSlots = Math.Max(_maxSlots, used);
    }
    float _minCap = float.PositiveInfinity;
    double _worstRatio;
    public Action<string>? Log { get; set; }

    // --- the levers, read by the loaders each frame ---
    int Level => Enabled ? FrameLevel : 0;
    public ulong TextureUploadBytes => Level switch { 0 => 12UL << 20, 1 => 6UL << 20, 2 => 3UL << 20, _ => 3UL << 19 };
    public double TextureUploadMs => Level switch { 0 => 1.5, 1 => 0.75, 2 => 0.4, _ => 0.25 };
    public ulong GeometryUploadBytes => Level switch { 0 => 8UL << 20, 1 => 4UL << 20, 2 => 2UL << 20, _ => 1UL << 20 };
    public double GeometryUploadMs => Level switch { 0 => 1.0, 1 => 0.5, 2 => 0.3, _ => 0.2 };
    public double ResolveMs => Level switch { 0 => 2.0, 1 => 1.5, 2 => 1.0, _ => 0.5 };
    public int Decodes => Level switch { 0 => 4, 1 => 3, 2 => 2, _ => 1 };
    public int Fetches => Level switch { 0 or 1 => 8, 2 => 6, _ => 4 };
    /// <summary>A texture or mesh bigger than the frame's upload budget still goes, so loading never stops: one a frame
    /// normally, every 8th frame at level 2, every 90th at the top (a hitch a second at most, not a beat).</summary>
    public int ForcedUploadEvery => Level switch { 0 or 1 => 1, 2 => 8, _ => 90 };
    /// <summary>New objects (never drawn yet) further than this from the eye are held (metres to their edge), and let in
    /// at <see cref="AdmitPerSecond"/>.</summary>
    public float NewObjectRadius => Level >= 3 ? 16f : float.PositiveInfinity;
    /// <summary>Held new objects let in a second, largest on screen first (RenderWorld.Admit), so a place always fills
    /// in: 32 at first, doubling every 4 s the hold lasts (frames still slow with loading held back mean the place
    /// itself is heavy, not its loading; in 20 s it's a thousand a second). Infinity: nothing held.</summary>
    public double AdmitPerSecond => Level >= 3
        ? 32 * Math.Pow(2, Math.Min(20, (System.Environment.TickCount64 - _holdAt) / 4000.0))
        : double.PositiveInfinity;
    /// <summary>Probe faces this frame (the capture in progress waits a frame when none fit the GPU's spare time,
    /// never more than a few in a row), and how much longer stale probes wait to be captured again.</summary>
    public int ProbeFaces(int normal)
    {
        int n = Level >= 3 ? 1 : normal;
        if (!Paced) return n;
        int fit = (int)(Math.Max(0, _tokens) / _faceMs);
        if (fit < 1 && ++_faceWait < ProbeWaitMost) return 0;
        _faceWait = 0;
        n = Math.Clamp(fit, 1, n);
        _tokens -= n * _faceMs;
        return n;
    }
    public double ProbeRefreshScale => Level >= 2 ? 10 : 1;
    /// <summary>Impostors (far avatars) captured a frame at most, and their poses' refresh rate as a share of the usual.</summary>
    public int ImpostorCaptures => Level switch { 0 or 1 => 4, 2 => 2, _ => 1 };
    public float ImpostorRefresh => Level switch { 0 or 1 => 1f, 2 => 0.5f, _ => 0.25f };

    // --- loading in the GPU's spare time ---
    /// <summary>Testing only (SOJOURN_GPU_PACING=0): loading's GPU work held by the levels alone, for a before.</summary>
    static readonly bool PacingOff = System.Environment.GetEnvironmentVariable("SOJOURN_GPU_PACING") == "0";
    // of the period, loading fits what the scene leaves of this share (the headset's compositor and Link's encoder use
    // the GPU too); at least the floor share of the period a frame (so loading stretches a frame by a few percent at
    // most, and never stops), at most the cap, and a turn's worth saved up for a big upload
    const double PacedShare = 0.9, FloorShare = 0.02, CapMs = 2.0, BucketMs = 1.0;
    const int ProbeWaitMost = 8;
    double _sceneGpu, _uploadMbPerMs = 15, _faceMs = 0.4, _tokens;
    int _gpuSeen, _faceWait;
    double _lineLoad, _lineSpare, _lineScene;
    int _lineLoadN, _lineSpareN;
    bool Paced => Enabled && !PacingOff && _gpuSeen >= 30;

    /// <summary>A frame's loading begins (render thread, before its uploads): it may spend on the GPU what the period
    /// leaves after the scene's own GPU time, and what earlier frames saved up.</summary>
    public void BeginLoading()
    {
        // (the floor capped too: a period over 100 ms, SOJOURN_GOVERNOR_PERIOD_MS=1000, put it past the cap and threw)
        double spare = Math.Clamp(PeriodMs * PacedShare - _sceneGpu, Math.Min(PeriodMs * FloorShare, CapMs), CapMs);
        _tokens = Math.Min(_tokens + spare, Math.Max(spare, BucketMs));
        _lineSpare += spare;
        _lineSpareN++;
    }

    /// <summary>An upload budget (bytes) within the GPU time loading has left this frame.</summary>
    public ulong UploadBudget(ulong levelBytes) => !Paced ? levelBytes
        : Math.Min(levelBytes, (ulong)(Math.Max(0, _tokens) * _uploadMbPerMs * 1048576));

    /// <summary>Whether the level's turn for an upload bigger than the budget may go this frame: with pacing, once a
    /// turn's worth of GPU time is saved up, so a big one fills a frame's spare time rather than stretching it.</summary>
    public bool MayForce => !Paced || _tokens >= BucketMs * 0.75;

    /// <summary>What the frame's uploads staged (bytes): their GPU time is spent.</summary>
    public void Uploaded(ulong bytes)
    {
        if (Paced) _tokens -= bytes / (1048576.0 * _uploadMbPerMs);
    }

    /// <summary>The draw distance to use for a person's setting.</summary>
    public float DrawDistance(float person) => MathF.Min(person, DrawCap);

    /// <summary>A frame's GPU time (from the profiler, a frame or two late).</summary>
    public void Gpu(Frame.GpuFrame f)
    {
        _gpuSum += f.Ms;
        _gpuN++;
        _gpuSeen++;
        // the scene's own GPU time: up at once (half way a frame), down slowly, so loading fits its dearer frames
        double scene = Math.Max(0, f.Ms - f.LoadMs);
        _sceneGpu += (scene - _sceneGpu) * (scene > _sceneGpu ? 0.5 : 0.03);
        if (f.UploadBytes >= 1 << 20 && f.UploadMs > 0.01)
            _uploadMbPerMs = Math.Clamp(_uploadMbPerMs + (f.UploadBytes / 1048576.0 / f.UploadMs - _uploadMbPerMs) * 0.2, 2, 100);
        if (f.ProbeFaces > 0 && f.ProbeMs > 0)
            _faceMs = Math.Clamp(_faceMs + (f.ProbeMs / f.ProbeFaces - _faceMs) * 0.2, 0.02, 5);
        _lineLoad += f.LoadMs;
        _lineScene += scene;
        _lineLoadN++;
    }

    /// <summary>A frame's render-thread CPU, the display period it has (ms), and the person's draw distance.</summary>
    public void Frame(double cpuMs, double periodMs, float personDrawDistance)
    {
        _cpuSum += cpuMs;
        _cpuN++;
        PeriodMs = TestPeriod > 0 ? TestPeriod : periodMs;
        periodMs = PeriodMs;
        _person = personDrawDistance;
        long now = System.Environment.TickCount64;
        if (now - _windowAt < 500) return;
        _windowAt = now;
        double gpu = _gpuN > 0 ? _gpuSum / _gpuN : 0, cpu = _cpuN > 0 ? _cpuSum / _cpuN : 0;
        _gpuSum = _cpuSum = 0;
        _gpuN = _cpuN = 0;
        LastCostMs = Math.Max(gpu, cpu);
        double ratio = LastRatio = LastCostMs / Math.Max(1, periodMs);
        _worstRatio = Math.Max(_worstRatio, ratio);

        // levels: up one after a second over the next threshold (straight to the top when the GPU is paging: three
        // periods a frame), down one after 4 s under 0.8 of the current one's
        int want = 0;
        while (want < Thresholds.Length && ratio > Thresholds[want]) want++;
        _hot = want > FrameLevel ? _hot + 1 : 0;
        if (ratio > 3) { FrameLevel = 3; _calm = 0; }
        else if (_hot >= 2) { FrameLevel++; _hot = 0; _calm = 0; }
        else if (FrameLevel > 0 && ratio < Thresholds[FrameLevel - 1] * 0.8) { if (++_calm >= 8) { FrameLevel--; _calm = 0; } }
        else _calm = 0;
        if (!Enabled) FrameLevel = 0;
        _maxLevel = Math.Max(_maxLevel, FrameLevel);

        if (FrameLevel >= 3 && _levelBefore < 3) _holdAt = now;
        _levelBefore = FrameLevel;

        // the draw distance: in from the far end only when video memory has nothing left to give for 1.5 s, out again
        // after 5 s with textures free to grow, more slowly than it came in and, for a minute after the last cut, no
        // further than just past where it stopped. Not for slow frames (2026-10-27, MagnetiS: at 20-25 ms a frame it sat
        // at its floor all session): far new objects already wait at level 3, so a cut only took away what was drawn
        // past it and stopped anything beyond it from ever loading
        float floor = MathF.Min(_person, MathF.Max(48f, _person * 0.5f));
        bool memoryOut = OverTarget && Pressure >= MaxPressure;
        _ddOver = memoryOut ? _ddOver + 1 : 0;
        if (_ddOver >= 3)
        {
            float from = float.IsPositiveInfinity(DrawCap) ? _person : DrawCap;
            DrawCap = MathF.Max(floor, MathF.Min(from, _person) - 4f);
            _ceiling = DrawCap + 4f;
            _lastCutAt = now;
            _ddCalm = 0;
        }
        else if (!float.IsPositiveInfinity(DrawCap) && !HoldGrowth)
        {
            float limit = now - _lastCutAt < 60_000 ? _ceiling : float.PositiveInfinity;
            if (++_ddCalm >= 10 && DrawCap < limit)
            {
                DrawCap = MathF.Min(DrawCap + 2f, limit);
                if (DrawCap >= _person) DrawCap = float.PositiveInfinity;
            }
        }
        else _ddCalm = 0;
        _minCap = MathF.Min(_minCap, DrawCap);
        Trim(ratio, now);
    }

    /// <summary>The trims, a half-second window at a time: in a step after 1.5 s over the period and every window it
    /// stays over; out half a step a window after 5 s well under, no further than just past the last cut for a minute.</summary>
    void Trim(double ratio, long now)
    {
        _trimOver = Enabled && ratio > TrimOver ? _trimOver + 1 : 0;
        if (!Enabled)
        {
            (ShadowReach, ImpostorBeyond, _trimCalm) = (1f, float.PositiveInfinity, 0);
        }
        else if (_trimOver >= 3)
        {
            ShadowReach = MathF.Max(ReachFloor, ShadowReach - ReachStep);
            ImpostorBeyond = float.IsPositiveInfinity(ImpostorBeyond) ? ImpostorStart : MathF.Max(ImpostorFloor, ImpostorBeyond - ImpostorStep);
            (_reachCeiling, _impostorCeiling) = (ShadowReach + ReachStep, ImpostorBeyond + ImpostorStep);
            _lastTrimAt = now;
            _trimCalm = 0;
        }
        else if (Trimmed && ratio < TrimCalm)
        {
            if (++_trimCalm >= 10)
            {
                bool held = now - _lastTrimAt < 60_000;
                ShadowReach = MathF.Min(ShadowReach + ReachStep / 2, held ? _reachCeiling : 1f);
                if (ShadowReach > 0.999f) ShadowReach = 1f;
                if (!float.IsPositiveInfinity(ImpostorBeyond))
                {
                    ImpostorBeyond = MathF.Min(ImpostorBeyond + ImpostorStep / 2, held ? _impostorCeiling : float.PositiveInfinity);
                    if (ImpostorBeyond > ImpostorStart) ImpostorBeyond = float.PositiveInfinity;
                }
            }
        }
        else _trimCalm = 0;
        _minReach = MathF.Min(_minReach, ShadowReach);
        _minImpostor = MathF.Min(_minImpostor, ImpostorBeyond);
    }

    /// <summary>Video memory now: what the process uses and is budgeted (bytes; budget 0 when unknown: then 85% of the
    /// heap), and the free space inside our own allocator's blocks (ours to reuse before the process grows).</summary>
    public void Memory(ulong usage, ulong budget, ulong heap, ulong slack)
    {
        long now = System.Environment.TickCount64;
        if (now - _memAt < 250) return;
        _memAt = now;
        if (budget == 0) budget = heap / 100 * 85;
        if (TestBudget > 0) budget = Math.Min(budget, TestBudget);
        Usage = usage;
        Budget = budget;
        // the headset's compositor and Link's encoder live in other processes; a new set of render targets or a
        // burst of uploads lands between two looks
        ulong headroom = Math.Max(1UL << 30, budget / 10);
        if (TestBudget > 0) headroom = budget / 10;
        Target = budget > headroom ? budget - headroom : budget / 2;
        Effective = usage > slack ? usage - slack : 0;
        if (MemoryOff) return;
        // margins: 512 MB to hold, 768 to let go, a GB to ease (a twentieth, a fourteenth and a tenth of a small test target)
        ulong hold = Math.Min(512UL << 20, Target / 20), release = hold + Math.Min(256UL << 20, Target / 40), ease = Math.Min(1UL << 30, Target / 10);
        if (Effective + hold > Target) HoldGrowth = true;
        else if (Effective + release < Target) HoldGrowth = false;
        // the free space in our blocks is ours to reuse, but it's still in use as Windows counts: past half the headroom
        // the real use counts too
        OverTarget = Effective > Target || usage > Target + headroom / 2;
        if (OverTarget)
        {
            // past the budget itself paging has begun: two steps at a time
            Pressure = Math.Min(MaxPressure, Pressure + (usage > budget ? 2 : 1));
            _easeAt = now + 20_000;
        }
        else if (Pressure > 0 && Effective + ease < Target && now >= _easeAt)
        {
            Pressure--;
            _easeAt = now + 4000;
        }
        else if (Effective + ease >= Target) _easeAt = Math.Max(_easeAt, now + 4000);
        _maxPressure = Math.Max(_maxPressure, Pressure);
    }

    static string Gb(ulong b) => $"{b / (double)(1UL << 30):F1}";

    /// <summary>The 10 s line: what the frame and memory were and what was done about them since the last one.</summary>
    public string TakeLine(string breakdown)
    {
        var parts = new List<string>
        {
            $"frame {LastCostMs:F1} ms of {PeriodMs:F1} (worst x{_worstRatio:F1})" +
            (!Enabled ? ", frame side off" : _maxLevel == 0 ? "" : $", loading level {FrameLevel} (max {_maxLevel}: {Describe(_maxLevel)})"),
        };
        if (!float.IsPositiveInfinity(_minCap) || !float.IsPositiveInfinity(DrawCap))
            parts.Add($"draw distance {(float.IsPositiveInfinity(DrawCap) ? _person : DrawCap):0} m of {_person:0} (least {(float.IsPositiveInfinity(_minCap) ? _person : _minCap):0})");
        if (HeldObjects > 0 || AdmittedObjects > 0)
            parts.Add($"{HeldObjects} new objects held (object-frames), {AdmittedObjects} let in" +
                      (Level >= 3 ? $" (now {Math.Min(AdmitPerSecond, 99999):0} a second)" : ""));
        if (_lineLoadN > 0 && _lineLoad > 0.001 * _lineLoadN)
            parts.Add($"loading on the GPU {_lineLoad / _lineLoadN:F2} ms a frame (scene {_lineScene / _lineLoadN:F1}" +
                      (Paced && _lineSpareN > 0 ? $", spare {_lineSpare / _lineSpareN:F2}; uploads {_uploadMbPerMs:F0} MB a ms, a probe face {_faceMs:F2} ms)" : ")"));
        _lineLoad = _lineScene = _lineSpare = 0;
        _lineLoadN = _lineSpareN = 0;
        if (_minReach < 1f || ShadowReach < 1f)
            parts.Add($"{(ShadowCoarse ? "full-detail shadows" : "shadows")} reach x{ShadowReach:0.00} ({ShadowFar:0} m of {ShadowFullFar:0}; least x{_minReach:0.00})" +
                      (ShadowCoarse ? ", coarse beyond" : ""));
        if (!float.IsPositiveInfinity(_minImpostor) || !float.IsPositiveInfinity(ImpostorBeyond))
            parts.Add($"avatars past {(float.IsPositiveInfinity(ImpostorBeyond) ? "-" : $"{ImpostorBeyond:0} m")} as impostors (nearest {_minImpostor:0} m)");
        parts.Add($"vram {Gb(Effective)} GB in use of {Gb(Budget)} (target {Gb(Target)}; {breakdown})" +
                  (HoldGrowth ? ", textures held" : "") + (_maxPressure > 0 ? $", texture pressure {Pressure} (max {_maxPressure})" : ""));
        if (Shrunk > 0 || Evicted > 0 || HeldUploads > 0)
            parts.Add($"textures shrunk {Shrunk} ({ShrunkBytes >> 20} MB), evicted {Evicted}, sharper copies dropped {HeldUploads}");
        parts.Add($"texture slots {SlotsUsed} of {SlotsMax} (most {_maxSlots})" +
                  (IdleEvicted > 0 ? $", {IdleEvicted} unseen {World.TextureStore.IdleMs / 1000} s let go ({IdleBytes >> 20} MB)" : "") +
                  (SlotEvicted > 0 ? $", {SlotEvicted} let go for slots" : "") +
                  (SlotWaits > 0 ? $", uploads waited for a slot {SlotWaits} times" : ""));
        Shrunk = ShrunkBytes = Evicted = HeldUploads = HeldObjects = AdmittedObjects = 0;
        IdleEvicted = IdleBytes = SlotEvicted = SlotWaits = 0;
        _maxSlots = SlotsUsed;
        _maxLevel = FrameLevel;
        _maxPressure = Pressure;
        _minCap = DrawCap;
        _worstRatio = LastRatio;
        (_minReach, _minImpostor) = (ShadowReach, ImpostorBeyond);
        return "governor: " + string.Join("; ", parts);
    }

    static string Describe(int level) => level switch
    {
        1 => "loading at half speed",
        2 => "loading at a quarter, far avatars and reflections refreshed half as often",
        _ => "new objects beyond 16 m let in largest first, loading slowest, far avatars and reflections refreshed a quarter as often",
    };
}
