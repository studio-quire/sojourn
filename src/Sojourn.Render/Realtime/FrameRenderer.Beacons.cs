using Sojourn.Render.World;

namespace Sojourn.Render.Realtime;

public sealed partial class FrameRenderer
{
    /// <summary>Beacons, highlights, Highlight Transparent and the tracking beacon's column (World/Beacons.cs; settings
    /// thread-safe).</summary>
    public Beacons Beacons => _world.Beacons;
}
