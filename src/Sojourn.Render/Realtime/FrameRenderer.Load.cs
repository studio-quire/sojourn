using LibreMetaverse;
using Sojourn.Render.World;

namespace Sojourn.Render.Realtime;

/// <summary>
/// How far a person's or a thing's look had arrived in the last frame (Sojourn Access's describing, docs/access-app.md >
/// Describing): a person's bakes and how they were drawn, and each thing they wear, or an object's linkset, by its
/// faces' colour textures. A face whose texture hasn't loaded is drawn plain grey (or its material's own colour, often
/// white), which a describer must never take for the thing's colour; a picture waits for its subject's look before it's
/// taken, and says what still hadn't arrived. Claude Opus 5.5, 2026-11-01.
/// </summary>
/// <param name="Drawn">a person: "full", "impostor", "jelly" (a plain stand-in), "cloud" (their appearance or a body bake
/// not in yet: nothing of their look drawn), or null (not among the frame's avatars); an object: null</param>
/// <param name="Parts">a person's worn things (each linkset by its root), an object's linkset as one</param>
public sealed record LookLoad(bool Person, string? Drawn, int Bakes, int BakesLoading, int BakesMissing, IReadOnlyList<PartLoad> Parts)
{
    /// <summary>What a picture still waits for: bakes, shapes and colour textures on their way, a person drawn as a
    /// cloud, and (unless memory holds sharper copies back) what's blurred. Missing textures never come.</summary>
    public int Waiting(bool blurryToo) => BakesLoading + (Drawn == "cloud" ? 1 : 0)
        + Parts.Sum(p => p.ShapesWaiting + p.Loading + (blurryToo ? p.Blurry : 0));

    public int Faces => Parts.Sum(p => p.Faces);
    public int Loading => Parts.Sum(p => p.Loading);
}

public sealed partial class FrameRenderer
{
    /// <summary>The look of a person (avatar id) or an object (any prim's id: its linkset) as the last frame drew it,
    /// from the region given first, else any; null when no region has it. Only textures asked for since
    /// <paramref name="since"/> (Environment.TickCount64; default the last 2 s) are judged: on the processor a drawn frame
    /// alone can take longer. Render thread, between frames.</summary>
    public LookLoad? LoadOf(ulong region, UUID id, long since = 0)
    {
        if (id == UUID.Zero) return null;
        if (since == 0) since = System.Environment.TickCount64 - 2000;
        var first = _world.Region(region);
        foreach (var scene in first != null ? _world.Regions.Where(s => s != first).Prepend(first) : _world.Regions)
        {
            foreach (var av in scene.Source.Avatars.Values)
            {
                if (av.ID != id) continue;
                var (drawn, bakes, loading, missing) = _world.Avatars.BakesOf(id);
                // each worn thing by its root (HUDs left out: nobody else sees them)
                var parts = scene.AttachmentsOf(av.LocalID)
                    .GroupBy(o => o.Prim.ParentID == av.LocalID ? o : scene.Parent(o) ?? o)
                    .Where(g => (int)g.Key.Prim.PrimData.AttachmentPoint is < 31 or > 38)
                    .Select(g => scene.LoadOf(g.Key.Prim.ID, g, av.LocalID, since))
                    .ToList();
                return new LookLoad(true, drawn, bakes, loading, missing, parts);
            }
            if (scene.ObjectById(id) is { } obj)
            {
                // its linkset's root (a worn thing's parent beyond that is its wearer, not a prim)
                var root = obj;
                for (int i = 0; i < 4 && scene.Parent(root) is { } up; i++) root = up;
                uint owner = scene.AttachmentOwner(root.Prim);
                return new LookLoad(false, null, 0, 0, 0, [scene.LoadOf(root.Prim.ID, scene.LinksetOf(root), owner, since)]);
            }
        }
        return null;
    }

    /// <summary>Whether memory has sharper copies held back now (a blurry texture won't sharpen until it eases).</summary>
    public bool TexturesHeld => _world.Textures.HoldGrowth;
}
