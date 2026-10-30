// The handheld camera's preview (Sojourn.Client's Control/Shutter.cs): while the hands frame a picture, the panel
// between them shows what the shutter would take, live, as a phone's screen does. An extra view inside the person's own
// frame rather than a still of its own: a still builds a second draw list on the render thread and waits for the GPU,
// a hitch every frame; this reuses the frame's draw list (culled for the preview's view as well), shadow maps and probe
// list, and costs only GPU time, and only on frames that ask for it. It's drawn as the snapshot will be (a camera at the
// panel's centre along its facing, the snapshot's near plane and field of view, full shading and post), into a fixed
// square target the panel's shape is stretched over, so the hands moving never remake anything; the exposure is the
// eyes' as it stands. The overlay draws its output at the panel's corners (Overlay.cs). Claude Opus 5.5, 2026-10-29.
using Sojourn.Render.Frame;

namespace Sojourn.Render.Realtime;

public sealed partial class FrameRenderer
{
    /// <summary>The preview's target, a side: about as many pixels as a panel held at arm's length covers in a Quest 3
    /// eye (~40 degrees at ~20 px a degree).</summary>
    public const int PreviewSize = 768;
    /// <summary>The preview's targets go this long after the camera was last out (a standby scene keeps them).</summary>
    const long PreviewKeepMs = 30_000;

    RenderTargets? _previewTargets;
    long _previewUsed;

    /// <summary>This frame's preview view, if the overlay frames one; null otherwise (nothing drawn). A standby (the camera
    /// out, the hands not framing) makes or keeps the targets without drawing, so framing never waits on them.</summary>
    PreviewView? PlanPreview(FrameRequest req, ViewSetup center, FrameResources fr)
    {
        long now = System.Environment.TickCount64;
        var p = req.Overlay?.Preview;
        if (p == null)
        {
            // let go once the camera has been away a while, after this frame (no wait on the GPU here)
            if (_previewTargets is { } old && now - _previewUsed > PreviewKeepMs)
            {
                _previewTargets = null;
                fr.Retire.Add(old.Dispose);
                fr.Retire.Add(() => _gpu.Renderer.ReleaseMirrorImage(Renderer.PreviewMirror));
            }
            return null;
        }
        _previewUsed = now;
        _previewTargets ??= new RenderTargets(_gpu.Vk, _gpu.Bindless, PreviewSize, PreviewSize);
        if (p.Standby) return null;

        var fwd = Vector3.Normalize(p.Forward);
        var up = p.Up;
        if (MathF.Abs(Vector3.Dot(fwd, Vector3.Normalize(up))) > 0.999f) up = Vector3.UnitY;
        var v = center.CloneForEye();
        v.Width = v.Height = PreviewSize;
        v.Position = p.At;
        v.View = ViewSetup.LookAt(p.At, p.At + fwd, up);
        // the snapshot's camera: a mono view's near plane (ViewSetup's default, not the headset's 2 cm), the panel's
        // tangents over the square target
        v.Near = 0.1f;
        v.VFov = 2 * MathF.Atan(p.TanV);
        v.Proj = ViewSetup.ReverseZInfinite(-p.TanH, p.TanH, p.TanV, -p.TanV, v.Near);
        v.Sky = GpuSky.Build(center.SkyU, center.WaterU, v.View, center.WaterHeight, v.Position);
        v.LensHidden = null;
        v.Mask = ViewMask.Main;   // the own body as the eyes have it, headless
        v.ClipPlane = new Vector4(0, 0, 0, 1);
        return new PreviewView { View = v, Targets = _previewTargets };
    }

    void DisposePreview() => _previewTargets?.Dispose();
}
