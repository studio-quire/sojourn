using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

/// <summary>The handheld camera's preview this frame (Realtime/FrameRenderer.Preview.cs): its view and the targets it
/// renders through, whose Output the overlay then draws on the camera's panel.</summary>
public sealed class PreviewView
{
    public required ViewSetup View;
    public required RenderTargets Targets;
    /// <summary>A mirror in the camera's view: its reflection from the camera, rendered first through the same targets.</summary>
    public MirrorView? Mirror;
}

/// <summary>
/// The camera's preview, recorded after the shadows and before the mirrors and eyes: the frame's draw list from the
/// camera, shaded and finished as a still is (the eyes' shadow maps and probe list; SSAO, glow and SMAA), but with the
/// exposure as it stands, so the eyes' own isn't moved by it. Its Output is left shader-readable for the overlay.
/// Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed unsafe partial class Renderer
{
    bool _previewPass;

    void RecordPreview(FrameResources fr, FrameDraws draws, ViewData vd, PreviewView pv)
    {
        var cmd = fr.Cmd;
        bool suspended = Profiler.Suspended;
        Profiler.Suspended = true;   // its passes are timed as one block, apart from the eyes'
        _previewPass = true;
        try
        {
            RecordScene(cmd, draws, vd, pv.View, pv.Targets, shadows: false, cubeSnapshot: false);
            RecordPost(fr, pv.View, pv.Targets, (c, img) => img.Transition(c, ImageLayout.ShaderReadOnlyOptimal));
        }
        finally
        {
            _previewPass = false;
            Profiler.Suspended = suspended;
        }
        Profiler.Mark("preview");
    }
}
