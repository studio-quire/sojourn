namespace Sojourn.Render.World;

/// <summary>What the avatars ask of a region's worn objects (World/Avatars.Reset.cs, the particles).</summary>
public sealed partial class RegionScene
{
    /// <summary>Refresh Attachments' drawing half: every object the avatar wears resolves its faces afresh next frame.</summary>
    internal void RefreshWorn(uint avatarLocalId)
    {
        foreach (var o in AttachmentsOf(avatarLocalId))
            if (o.Faces != null) o.Faces.Outdated = true;
    }

    /// <summary>A prim worn by an avatar drawn as a jelly doll (or one the person chose not to render): its particles
    /// aren't drawn, as its attachments aren't.</summary>
    internal bool OnJellyDoll(uint localId)
    {
        if (!Source.Prims.TryGetValue(localId, out var prim)) return false;
        uint wearer = AttachmentOwner(prim);
        return wearer != 0 && _world.Avatars.IsJelly(Source.Handle, wearer);
    }
}
