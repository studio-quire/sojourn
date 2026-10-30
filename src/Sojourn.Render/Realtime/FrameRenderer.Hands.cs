namespace Sojourn.Render.Realtime;

public sealed partial class FrameRenderer
{
    /// <summary>The person's own avatar's local id in a scene's region, while the headset draws it (0 otherwise): what it
    /// wears is marked as its body is (InstanceFlags.Self, for the hands over the menus).</summary>
    static uint SelfIn(World.RegionScene scene, SelfBody? self)
    {
        if (self == null) return 0;
        foreach (var av in scene.Source.Avatars.Values)
            if (av.ID == self.Id) return av.LocalID;
        return 0;
    }
}
