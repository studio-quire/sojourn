using System.Diagnostics;
using LibreMetaverse;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Sojourn.Session.Hud;

namespace Sojourn.Session.Offscreen;

/// <summary>
/// The person's HUDs out of sight (docs/access-app.md > HUDs out of sight): never drawn between pictures. Placed on a
/// 16:9 screen (most HUDs are laid out on a monitor that shape; the VR client's headset board is the same) and their
/// draw list built only when something asks (a pick, a face list, a press: FrameRenderer.RenderHud planned, nothing
/// drawn, so it costs no GPU and works unseen); drawn only for a picture (SojournHud look, describing a HUD), which,
/// like any picture, makes the view whole again (textures) and waits for what's loading. Claude Opus 5.5, 2026-11-01.
/// </summary>
public sealed partial class OffscreenRenderer : IHudScreen
{
    /// <summary>The HUD screen's width over its height out of sight.</summary>
    public const float HudAspect = 16f / 9f;

    readonly List<HudPicture> _hudPictures = new();
    int _hudPictureCount;

    static HudRequest HudFor(Simulator sim) => new() { Region = sim.Handle, Self = sim.Client.Self.LocalID, Aspect = HudAspect };

    /// <summary>A job on the render thread taking any result (the world's jobs take structs).</summary>
    Task<T> OnHuds<T>(Func<FrameRenderer, Action<T>, bool> step, T fallback, TimeSpan timeout, CancellationToken ct)
    {
        if (_stop || _gaveUp) return Task.FromResult(fallback);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Enqueue(new WorldJob
        {
            Step = r => step(r, v => tcs.TrySetResult(v)),
            Fail = () => tcs.TrySetResult(fallback),
            Deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency),
        });
        _wake.Set();
        return tcs.Task.WaitAsync(timeout + TimeSpan.FromSeconds(1), ct).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : fallback, CancellationToken.None);
    }

    public Task<T> WithHudsAsync<T>(Func<HudsPlaced, HudPicker, T> f, T fallback, CancellationToken ct)
    {
        long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5 / 2;
        return OnHuds<T>((r, done) =>
        {
            if (_s.Client is not { Network.CurrentSim: { } sim }) { done(fallback); return true; }
            r.RenderHud(HudFor(sim), planOnly: true);
            var placed = r.Huds;
            // a part whose shape hasn't arrived yet (a mesh HUD just worn) is waited for a little: picks land on its
            // surface once it's built, not its box
            if (Stopwatch.GetTimestamp() < until && placed.Objects.Any(h => h.Parts.Any(p => !r.HasTriangles(placed.Region, p.LocalId)))) return false;
            uint self = sim.Client.Self.LocalID;
            done(f(placed, (yz, only) => r.HudPick(placed.Region, self, placed.Aspect, yz, only)));
            return true;
        }, fallback, TimeSpan.FromSeconds(4), ct);
    }

    public HudPicture? Picture(int? number)
    {
        lock (_hudPictures) return number is { } n ? _hudPictures.LastOrDefault(p => p.Number == n) : _hudPictures.LastOrDefault();
    }

    /// <summary>A picture of the HUDs: drawn whole from now on (as any picture makes the view), settled until what's
    /// in it has loaded (at most the settling time; on the processor only planned meanwhile), then drawn once.</summary>
    public async Task<(HudPicture Picture, byte[] Png)?> PictureAsync(HudObject? hud, bool transparent, CancellationToken ct)
    {
        var shot = new TaskCompletionSource<(HudPicture, byte[] Rgba, int W, int H)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = OnHuds<bool>((r, done) =>
        {
            if (_s.Client is not { Network.CurrentSim: { } sim }) { shot.TrySetResult(null); done(false); return true; }
            Seen(r);
            var req = HudFor(sim);
            long t0 = Stopwatch.GetTimestamp(), improved = t0;
            int best = int.MaxValue;
            while (!_stop)
            {
                int pending = r.RenderHud(req, planOnly: true);
                if (r.Huds.Objects.Count == 0) { shot.TrySetResult(null); done(false); return true; }
                if (pending < best) (best, improved) = (pending, Stopwatch.GetTimestamp());
                if (pending == 0 || Stopwatch.GetElapsedTime(t0).TotalSeconds >= SettleSeconds || Stopwatch.GetElapsedTime(improved).TotalSeconds > 1) break;
                _wake.WaitOne(50);
            }
            var placed = r.Huds;
            // the HUD asked for, as now placed (by id: it may have moved since it was asked about)
            var one = hud is { } want ? placed.Objects.FirstOrDefault(o => o.Id == want.Id) : null;
            if (hud != null && one == null) { shot.TrySetResult(null); done(false); return true; }
            var (center, hh, w, h) = HudPicture.For(one, placed.Aspect);
            var pic = new HudPicture(Interlocked.Increment(ref _hudPictureCount), center, hh, w, h, placed.Aspect, placed.Region);
            long d0 = Stopwatch.GetTimestamp();
            r.RenderHud(req with { Shot = new HudShot(center, hh, w, h, (rgba, pw, ph) => shot.TrySetResult((pic, rgba, pw, ph))) }, planOnly: false);
            r.WaitIdle();   // the shot's read back now
            _pictures++;
            if (r.Software)
            {
                LowerSoftwareThreads();
                _log($"the view out of sight on the processor: a HUD picture, {w}x{h}, settled in {Stopwatch.GetElapsedTime(t0, d0).TotalMilliseconds:0} ms, " +
                     $"drawn in {Stopwatch.GetElapsedTime(d0).TotalMilliseconds:0} ms");
            }
            lock (_hudPictures)
            {
                _hudPictures.Add(pic);
                if (_hudPictures.Count > 8) _hudPictures.RemoveAt(0);
            }
            done(true);
            return true;
        }, false, Software ? PictureWait(SettleSeconds) : TimeSpan.FromSeconds(SettleSeconds + 8), ct);
        if (!await queued) { shot.TrySetResult(null); return null; }
        try
        {
            if (await shot.Task.WaitAsync(TimeSpan.FromSeconds(5), ct) is not { } got) return null;
            return (got.Item1, HudPicture.Png(got.Rgba, got.W, got.H, transparent));
        }
        catch (TimeoutException) { return null; }
    }

    // HUDs out of sight show nowhere
    bool IHudScreen.Shown => false;
    void IHudScreen.Show(bool on) { }
}
