namespace Sojourn.Render.Realtime;

public sealed partial class FrameRenderer
{
    /// <summary>RLV's vision spheres for the frames to come (Frame/Renderer.Spheres.cs; the client's Rlv/RlvView.cs sets
    /// them each frame): drawn in every eye, from each eye's own depth.</summary>
    public IReadOnlyList<Sojourn.Render.Frame.Renderer.SphereFx>? VisionSpheres
    {
        get => _gpu.Renderer.Spheres;
        set => _gpu.Renderer.Spheres = value;
    }
}
