// Stills: pictures rendered from a camera of their own, beside the person's frames, without moving the view (the
// viewer's snapshots at a size bigger than the window, from a placed camera, and the six faces of a 360). Each is an
// extra frame on the render thread just before the person's next one, recorded into a frame slot of its own and waited
// for there and then: a short hitch at the person's request, nothing carried into their next frame.
//
// What a second view in a frame could disturb, and how it doesn't: the person's environment (the still is drawn under
// the sky they see, as the viewer's snapshots are: the fader and the jump detector aren't stepped), dynamic exposure
// (held at the person's value, so tiles and cube faces match), the shadow maps (sized as the person's last frame, so
// nothing is reallocated; the kept far cascade is chosen by geometry and stays right either way), the load governor
// and the GPU profiler (neither sees the still), the scene budget, probe captures (not run for a still), the eyes'
// mirror (a still chooses its own from its camera and renders its reflection as the eyes' are, at the picture's size,
// into an image of its own let go after), the view line's counts. Pictures bigger than a tile (2048) are drawn as tiles of one image, every tile an eye of the
// same frame (one cull, one shadow fit, one exposure), each with a margin so screen-space passes (SSAO, glow, SMAA)
// meet across the seams, and copied straight into one image as they finish.
// Claude Opus 5.5, 2026-10-29 (batch B8, snapshots).
using System.Collections.Concurrent;
using System.Diagnostics;
using LibreMetaverse;
using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;
using Sojourn.Render.World;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Realtime;

/// <summary>One picture of a still: a camera in the frame of the person's region (it may stand over a neighbour), the
/// image's size and vertical field of view.</summary>
public sealed record StillView
{
    public required Vector3 Position { get; init; }
    public required Vector3 Forward { get; init; }
    public Vector3 Up { get; init; } = Vector3.UnitZ;
    public float VerticalFov { get; init; } = 1.047197551f;
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>As the person sees it: their own body as drawn for them (the headset's, head hidden) and whoever they
    /// hide. Otherwise their avatar is drawn whole, as others see it.</summary>
    public bool AsSeen { get; init; }
    /// <summary>Every avatar left out (the viewer's 360CaptureHideAvatars).</summary>
    public bool HideAvatars { get; init; }
    /// <summary>The person's own avatar left out (a 360 from their eyes: their body seen from inside isn't a picture).</summary>
    public bool HideSelf { get; init; }
    /// <summary>The tangent of half the field of view, when the picture is square and a little wider than its field
    /// says (a cube face with a border, for sampling across its edges); 0: from <see cref="VerticalFov"/>.</summary>
    public float SquareTangent { get; init; }
}

public sealed record StillPicture(byte[] Rgba, int Width, int Height);

/// <summary>What a still job gives back: its pictures, what was still loading for them at the shot, and how long it
/// waited for things to load first.</summary>
public sealed record StillResult(IReadOnlyList<StillPicture> Pictures, int Pending, double SettledSeconds, double RenderMs, int Tiles);

/// <summary>A request for pictures. <see cref="Views"/> runs on the render thread with the person's frame about to be
/// drawn (their camera, region and hidden avatars), when the job is taken, again while it settles and at the shot, so
/// cameras placed relative to the person follow where they are; null or empty fails the job, and later calls must give
/// as many views (the last call's are the pictures).</summary>
public sealed class StillJob
{
    public required Func<FrameRequest, IReadOnlyList<StillView>?> Views { get; init; }
    /// <summary>Up to this long, things in the pictures' views are asked for and waited on (what each view still waits
    /// for counted every few frames) until nothing is, or nothing more arrives for two seconds.</summary>
    public double SettleSeconds { get; init; }
    internal readonly TaskCompletionSource<StillResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<StillResult> Result => Done.Task;
    public void Cancel(string why) => Done.TrySetException(new InvalidOperationException(why));
}

public sealed partial class FrameRenderer
{
    /// <summary>The longest side of a still drawn whole; bigger pictures are tiles of this size at most. A set of
    /// targets this size is ~380 MB; the housekeeping trims it a minute after the last still.</summary>
    public const int StillTile = 2048;
    /// <summary>The biggest picture (the viewer's MAX_SNAPSHOT_IMAGE_SIZE).</summary>
    public const int StillMax = 7680;
    const int TileMargin = 64;

    readonly ConcurrentQueue<StillJob> _stills = new();
    StillJob? _still;
    IReadOnlyList<StillView>? _stillViews;
    long _stillSince, _stillBuilt, _stillImproved;
    int[]? _stillPending;
    int _stillFrames, _stillBest = int.MaxValue;
    FrameResources? _stillFrame;
    DrawList? _stillList;
    BuiltProbeList? _stillProbes;
    // while a still is drawn: the shadow maps' screen (the person's last), and its environment (the person's)
    (int W, int H)? _stillShadowScreen;
    (int W, int H) _lastShadowScreen = (1280, 720);
    bool _stillEnv;
    long _lastLocalKey;

    /// <summary>Queue pictures from cameras of their own (any thread). Taken at the start of a frame that has a region to
    /// draw; one job at a time.</summary>
    public StillJob Still(StillJob job)
    {
        _stills.Enqueue(job);
        return job;
    }

    /// <summary>Where an avatar is drawn in a region as others see it (render thread: a still's
    /// <see cref="StillJob.Views"/>); null if it isn't there.</summary>
    public (Vector3 Pos, Quaternion Rot, bool Seated)? AvatarPlaced(ulong region, UUID id) =>
        _world.Region(region) is { } scene ? _world.Avatars.Placed(scene, id) : null;

    /// <summary>At the start of a frame (render thread, the world this thread's): the still in progress settles for
    /// one more frame or is drawn. Returns its milliseconds, kept out of the frame's own.</summary>
    double StepStills(FrameRequest req, RegionScene region)
    {
        long t0 = Stopwatch.GetTimestamp();
        while (_still == null && _stills.TryDequeue(out var next))
        {
            if (next.Done.Task.IsCompleted) continue;
            try
            {
                var views = next.Views(req);
                if (views is not { Count: > 0 }) { next.Cancel("no picture to take"); continue; }
                (_still, _stillViews, _stillSince, _stillImproved, _stillFrames, _stillBest) = (next, views, t0, t0, 0, int.MaxValue);
                _stillPending = new int[views.Count];
                Array.Fill(_stillPending, -1);
            }
            catch (Exception e) { next.Done.TrySetException(e); }
        }
        if (_still is not { } job || _stillViews is not { } list) return 0;
        if (job.Done.Task.IsCompleted) { EndStill(); return 0; }
        try
        {
            double settled = Stopwatch.GetElapsedTime(_stillSince).TotalSeconds;
            if (settled < job.SettleSeconds && !Settled())
            {
                // every third frame, one of the views built without drawing: what it shows is asked for and counted
                if (_stillFrames++ % 3 == 0)
                {
                    list = Replace(job, req, list);
                    int i = (int)(_stillBuilt++ % list.Count);
                    _stillPending![i] = WithStill(() => PlanStill(req, region, list[i], out _, out _, out _, out _).Pending);
                    int sum = _stillPending.Sum(p => Math.Max(0, p));
                    if (sum < _stillBest) (_stillBest, _stillImproved) = (sum, Stopwatch.GetTimestamp());
                }
                return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            list = Replace(job, req, list);
            var pictures = new List<StillPicture>(list.Count);
            int pending = 0, tiles = 0;
            long tr = Stopwatch.GetTimestamp();
            var made = new HashSet<(uint, uint)>();
            try
            {
                foreach (var view in list)
                {
                    var (pic, p, n) = WithStill(() => RenderStill(req, region, view, made));
                    pictures.Add(pic);
                    pending += p;
                    tiles += n;
                }
            }
            finally
            {
                // sizes the person's frames don't use go again at once, rather than holding their targets for a minute
                foreach (var (tw, th) in made) _gpu.ReleaseTargets(tw, th);
                _stillFrame?.Wait();
                _gpu.Renderer.ReleaseMirrorImage(Renderer.StillMirror);
            }
            job.Done.TrySetResult(new StillResult(pictures, pending, Math.Round(settled, 2), Math.Round(Stopwatch.GetElapsedTime(tr).TotalMilliseconds, 1), tiles));
        }
        catch (VulkanException) { job.Cancel("the GPU was lost"); EndStill(); throw; }
        catch (Exception e) { job.Done.TrySetException(e); }
        EndStill();
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    // settled: every view counted at least once, and nothing waiting, or nothing more arrived for two seconds
    bool Settled() => _stillPending!.All(p => p >= 0)
        && (_stillPending!.All(p => p == 0) || Stopwatch.GetElapsedTime(_stillImproved).TotalSeconds > 2);

    void EndStill() => (_still, _stillViews, _stillPending) = (null, null, null);

    /// <summary>The job's cameras placed again for this frame: settling takes seconds, and a camera around the person
    /// placed when the job was taken was left where they had been, facing where they had faced (2026-10-31, selfies in
    /// the headset missing the avatar). Kept as they were if placing fails or changes how many there are.</summary>
    IReadOnlyList<StillView> Replace(StillJob job, FrameRequest req, IReadOnlyList<StillView> list)
    {
        try
        {
            if (job.Views(req) is { } views && views.Count == list.Count) return _stillViews = views;
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>Run a still's part with the person's per-frame state put back after it.</summary>
    T WithStill<T>(Func<T> part)
    {
        var (env, lastGlobal, jumped) = (LastEnvironment, _lastGlobal, _jumpedAt);
        var (objects, culled, draws, phases, mirror, avatarMirror) = (_lastObjects, _lastCulled, _lastDraws, _phases, _mirror, _world.Avatars.Mirror);
        _stillShadowScreen = _lastShadowScreen;
        _stillEnv = LastEnvironment != null;
        _mirror = null;
        _world.Avatars.Mirror = MirrorAvatars.None;
        try { return part(); }
        finally
        {
            (LastEnvironment, _lastGlobal, _jumpedAt) = (env, lastGlobal, jumped);
            (_lastObjects, _lastCulled, _lastDraws, _phases, _mirror, _world.Avatars.Mirror) = (objects, culled, draws, phases, mirror, avatarMirror);
            _stillShadowScreen = null;
            _stillEnv = false;
        }
    }

    /// <summary>A still's frame: the request, its views and its draw list built (what's missing asked for).</summary>
    (FrameRequest Req, ViewSetup Center, ViewSetup[] Eyes, int Pending) PlanStill(FrameRequest person, RegionScene region, StillView sv,
        out (int X, int Y, int W, int H)[] cores, out int tileW, out int tileH, out int margin)
    {
        int w = Math.Clamp(sv.Width, 16, StillMax), h = Math.Clamp(sv.Height, 16, StillMax);
        IReadOnlyCollection<UUID>? hide = sv.AsSeen ? person.HideAvatars : person.HideAvatars?.Where(id => id != AvatarLimits.Self).ToHashSet();
        if (sv.HideSelf) hide = (hide ?? []).Append(AvatarLimits.Self).ToHashSet();
        if (sv.HideAvatars) hide = _world.Regions.SelectMany(r => r.Source.Avatars.Values.Select(a => a.ID)).Concat(hide ?? []).ToHashSet();
        float tv = sv.SquareTangent > 0 ? sv.SquareTangent : MathF.Tan(sv.VerticalFov / 2), th = tv * w / h;
        IReadOnlyList<EyeSpec> eyes;
        if (w <= StillTile && h <= StillTile)
        {
            // one eye: the camera itself, or a square with its own tangent
            eyes = sv.SquareTangent > 0 ? new[] { new EyeSpec(Vector3.Zero, Quaternion.Identity, -th, th, tv, -tv) } : FrameRequest.Mono;
            cores = new[] { (0, 0, w, h) };
            (tileW, tileH, margin) = (w, h, 0);
        }
        else
        {
            // tiles: each an eye of the one image, its frustum the image's cut down to the tile and its margin
            int core = StillTile - 2 * TileMargin;
            int nx = (w + core - 1) / core, ny = (h + core - 1) / core;
            int cw = (w + nx - 1) / nx, ch = (h + ny - 1) / ny;
            (tileW, tileH, margin) = (cw + 2 * TileMargin, ch + 2 * TileMargin, TileMargin);
            var specs = new List<EyeSpec>(nx * ny);
            var rects = new List<(int, int, int, int)>(nx * ny);
            for (int ty = 0; ty < ny; ty++)
                for (int tx = 0; tx < nx; tx++)
                {
                    int x0 = tx * cw, y0 = ty * ch;
                    float l = -th + 2 * th * (x0 - TileMargin) / w, r = -th + 2 * th * (x0 - TileMargin + tileW) / w;
                    float u = tv - 2 * tv * (y0 - TileMargin) / h, d = tv - 2 * tv * (y0 - TileMargin + tileH) / h;
                    specs.Add(new EyeSpec(Vector3.Zero, Quaternion.Identity, l, r, u, d));
                    rects.Add((x0, y0, Math.Min(cw, w - x0), Math.Min(ch, h - y0)));
                }
            eyes = specs;
            cores = rects.ToArray();
        }
        var req = new FrameRequest
        {
            RegionHandle = person.RegionHandle, Position = sv.Position, Forward = sv.Forward, Up = sv.Up, VerticalFov = 2 * MathF.Atan(tv),
            Width = w, Height = h, Eyes = eyes, DrawDistance = person.DrawDistance, LocalSky = person.LocalSky, Time = person.Time,
            HideAvatars = hide is { Count: > 0 } ? hide : null, Self = !sv.AsSeen ? null : person.Self,
        };
        var center = BuildView(req, region);
        var views = BuildEyes(req, center);
        if (eyes.Count > 1 || eyes[0].Asymmetric)
            foreach (var e in views) (e.Width, e.Height) = ((uint)tileW, (uint)tileH);
        var cull = CullFrustum(req, center, views);
        // a mirror in the picture (chosen afresh from its camera), and what it shows culled in (FrameRenderer.Mirror.cs)
        (uint, ulong) fresh = default;
        _mirror = PlanMirror(center, region, cull, ref fresh, person: false);
        if (_mirror != null) cull = Frustum.Either(cull, _mirror.Portal);
        _world.Avatars.Mirror = _mirror == null ? MirrorAvatars.None : _mirror.Dynamic ? MirrorAvatars.Shown : MirrorAvatars.Hidden;
        var dl = _stillList ??= new DrawList();
        dl.Clear();
        _planningStill = true;   // its own far shadow map, never kept (FrameRenderer.FarShadow.cs)
        int pending;
        try { pending = BuildDrawList(center, region, dl, req, cull, out _); }
        finally { _planningStill = false; }
        return (req, center, views, pending);
    }

    /// <summary>Draw one still and read it back: the picture, what it still waited for, and its tile count.</summary>
    unsafe (StillPicture Picture, int Pending, int Tiles) RenderStill(FrameRequest person, RegionScene region, StillView sv, HashSet<(uint, uint)> made)
    {
        var (req, center, eyes, pending) = PlanStill(person, region, sv, out var cores, out int tileW, out int tileH, out int m);
        var dl = _stillList!;
        ShareShadows(center, eyes);
        // its reflection at the picture's size, through the picture's own targets before each tile draws there
        if (_mirror != null) FinishMirror(_mirror, center, eyes, Renderer.StillMirror);
        var mirrorViews = _mirror?.Views.Where(m => m != null).Select(m => m!.View).ToArray() ?? [];
        BuiltProbeList? probeList = null;
        if (_probes != null && Quality.Reflections)
        {
            // the probes around the camera as they are: none captured for it
            var plan = _probes.Plan(center, region, SkyKey(center), blocking: false);
            probeList = ProbeManager.Build(plan, center.SkyU.ReflectionProbeAmbiance, false, center.Position + new Vector3(0, 0, ProbeManager.DefaultHeight),
                into: _stillProbes ??= new BuiltProbeList { Probes = new() });
        }
        else foreach (var e in eyes.Concat(mirrorViews)) e.Sky.probes_on = 0;

        int w = (int)center.Width, h = (int)center.Height;
        if (!_gpu.HasTargets((uint)tileW, (uint)tileH)) made.Add(((uint)tileW, (uint)tileH));
        var targets = eyes.Select(e => new Renderer.Eye(e, _gpu.Targets(e.Width, e.Height))).ToArray();
        var fr = _stillFrame ??= new FrameResources(_gpu.Vk);
        fr.Wait();
        using var readback = new GpuBuffer(_gpu.Vk, (ulong)w * (ulong)h * 4, BufferUsageFlags.TransferDstBit, MemoryUse.Readback);
        var r = _gpu.Renderer;
        fr.BeginCommands();
        if (center.Sky.shadows_on != 0) r.EnsureShadowMaps(center.ShadowMapWidth, center.ShadowMapHeight);
        if (center.Sky.shadow_far.W > 0) r.EnsureFarShadowMap((uint)center.Sky.shadow_far_res.X);
        if (center.Sky.spot_shadows != 0) r.EnsureSpotShadowMaps(center.SpotShadowMapSize);
        r.BeginFrame(fr);
        (r.AlphaLayers, r.SimpleAlpha, r.HalfLight) = (Quality.AlphaLayers, Quality.SimpleAlpha, Quality.HalfSsao);
        // (the profiler off: the load governor weighs the GPU by the person's frames alone)
        bool profiling = PassProfiler.Enabled, held = r.HoldExposure;
        (PassProfiler.Enabled, r.HoldExposure) = (false, true);
        ulong mark;
        try
        {
            mark = r.Record(fr, targets, dl, _gpu.Uploader,
                prepareViews: arena =>
                {
                    if (probeList != null) foreach (var e in eyes.Concat(mirrorViews)) probeList.Write(arena, ref e.Sky);
                    return 0;
                },
                extraArenaBytes: (probeList?.Bytes ?? 0) * (ulong)(eyes.Length + mirrorViews.Length) + 128 * (ulong)eyes.Length,
                present: (cmd, eye, img) =>
                {
                    var (x0, y0, cw, ch) = cores[eye];
                    var copy = new BufferImageCopy
                    {
                        BufferOffset = ((ulong)y0 * (ulong)w + (ulong)x0) * 4, BufferRowLength = (uint)w,
                        ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                        ImageOffset = new Offset3D(m, m, 0), ImageExtent = new Extent3D((uint)cw, (uint)ch, 1),
                    };
                    _gpu.Vk.Api.CmdCopyImageToBuffer(cmd, img.Handle, ImageLayout.TransferSrcOptimal, readback.Handle, 1, in copy);
                },
                mirrors: _mirror?.Views);
            var hb = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.CopyBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.HostBit, DstAccessMask = AccessFlags2.HostReadBit,
            };
            var di = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &hb };
            _gpu.Vk.Api.CmdPipelineBarrier2(fr.Cmd, in di);
        }
        finally { (PassProfiler.Enabled, r.HoldExposure) = (profiling, held); }
        Submit(fr, fr.Cmd, default, default);
        var up = _gpu.Uploader;
        fr.Retire.Add(() => up.Release(mark));
        fr.Wait();   // the hitch: this picture's GPU work, before the person's frame goes on
        var pixels = new byte[w * h * 4];
        new ReadOnlySpan<byte>((void*)readback.Alloc.Mapped, pixels.Length).CopyTo(pixels);
        return (new StillPicture(pixels, w, h), pending, eyes.Length);
    }
}
