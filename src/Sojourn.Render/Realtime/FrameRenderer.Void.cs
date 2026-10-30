using System.Diagnostics;
using System.Numerics;
using Silk.NET.Vulkan;
using Sojourn.Render.Frame;

namespace Sojourn.Render.Realtime;

/// <summary>
/// A frame with no region to draw but something over it: the login screen and its sheets (Sojourn.Client/Login), the
/// pointer's beam in the headset before the world is there. Each eye cleared to a quiet slate and the overlay drawn
/// over it as it would be over the world (sheets with their frost, beams, dots), so the person sees what they press.
/// Without an overlay a frame with no region stays the small dark clear it always was (<see cref="Clear"/>).
/// Written by Claude Opus 5.5, 2026-10-28, for batch R's login.
/// </summary>
public sealed partial class FrameRenderer
{
    /// <summary>The void's colour (gamma-encoded): a dusk slate, dark enough to rest the eyes in a headset, light
    /// enough that a glass sheet's frost has something to show.</summary>
    static readonly ClearColorValue VoidColor = new(0.085f, 0.105f, 0.135f, 1f);

    FrameStats Void(FrameRequest req, FrameResources fr, int slot, IFrameTarget target, double waitMs, long t0)
    {
        WorldReleased?.Invoke();
        var overlay = req.Overlay!;
        var center = new ViewSetup
        {
            Width = (uint)Math.Clamp(req.Width, 16, 8192),
            Height = (uint)Math.Clamp(req.Height, 16, 8192),
            Position = req.Position,
            RegionHandle = req.RegionHandle,
            VFov = req.VerticalFov,
        };
        var dir = req.Forward.LengthSquared() > 1e-8f ? Vector3.Normalize(req.Forward) : Vector3.UnitX;
        var up = req.Up;
        if (MathF.Abs(Vector3.Dot(dir, Vector3.Normalize(up))) > 0.999f) up = Vector3.UnitY;
        center.View = ViewSetup.LookAt(req.Position, req.Position + dir, up);
        float aspect = (float)center.Width / center.Height;
        if (req.Eyes.Any(e => e.Asymmetric))
        {
            var (h, vt) = (req.Eyes.Max(e => MathF.Max(-e.TanLeft, e.TanRight)), req.Eyes.Max(e => MathF.Max(e.TanUp, -e.TanDown)));
            center.VFov = 2 * MathF.Atan(vt);
            aspect = h / vt;
            center.Near = ViewSetup.HeadsetNear;
        }
        center.Proj = ViewSetup.ReverseZInfinite(center.VFov, aspect, center.Near);
        var eyes = BuildEyes(req, center);

        if (!target.Acquire(slot, out var ready, out var done)) return new FrameStats(waitMs, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);
        fr.BeginCommands();
        var cmd = fr.Cmd;
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        for (int eye = 0; eye < eyes.Length; eye++)
        {
            // one eye at a time through the same image: each is copied out (RecordPresent) before the next is drawn
            var img = _gpu.Targets(eyes[eye].Width, eyes[eye].Height).Output;
            img.Transition(cmd, ImageLayout.TransferDstOptimal);
            var color = VoidColor;
            _gpu.Vk.Api.CmdClearColorImage(cmd, img.Handle, ImageLayout.TransferDstOptimal, in color, 1, in range);
            img.Transition(cmd, ImageLayout.TransferSrcOptimal);
            _overlay.Record(fr, slot, cmd, img, eye, eyes[eye], overlay, head: center.Position);
            if (req.CapturePath is { } path)
                RecordCapture(fr, cmd, img, eye, eyes.Length, eye == 0 ? path : Path.ChangeExtension(path, null) + $"-eye{eye}.png");
            target.RecordPresent(cmd, eye, eyes.Length, img);
        }
        target.RecordFinish(cmd);
        ulong mark = _gpu.Uploader.Record(cmd);
        Submit(fr, cmd, ready, done);
        var uploader = _gpu.Uploader;
        fr.Retire.Add(() => uploader.Release(mark));
        target.Present();
        _slot = (slot + 1) % _gpu.Frames.Length;
        return new FrameStats(waitMs, 0, 0, 0, 0, Stopwatch.GetElapsedTime(t0).TotalMilliseconds, 0, 0, 0, 0, true);
    }
}
