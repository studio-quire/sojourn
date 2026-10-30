using Sojourn.Render.World;

namespace Sojourn.Render.Realtime;

/// <summary>Web pages and video on faces (docs/media.md): the client's media code adds surfaces here and puts them on
/// faces; the frame's start uploads their new frames.</summary>
public sealed partial class FrameRenderer
{
    public Media.MediaSurfaces Media => _world.Media;

    /// <summary>Where a prim's face is (RegionScene.FaceFrame), in its region's frame. Render thread, between frames.</summary>
    public MediaFaceFrame? MediaFace(ulong regionHandle, uint localId, int face)
    {
        CheckWorld();
        return _world.Regions.FirstOrDefault(r => r.Source.Handle == regionHandle)?.FaceFrame(localId, face);
    }
}
