using LibreMetaverse;
using Sojourn.Core.Agents;
using Sojourn.Render.World;

namespace Sojourn.Session.Hud;

/// <summary>
/// Where requestTouch lands on one of the person's HUD prims, for an app whose touches have no pointer (Sojourn Access:
/// AgentSession.FindTouchSurface and FindFaceSurface, SessionHost.StartOffscreen): the part's middle as it shows on the
/// screen, or the middle of one of its faces as it shows, with the surface data a click there sends (llDetectedTouchFace,
/// ST and UV). The VR client's own touches find HUDs through its DirectTouch.
/// </summary>
public static class HudTouch
{
    /// <summary>Null when the prim isn't on a HUD drawn here, or (with a face) that face doesn't show.</summary>
    public static Task<TouchPoint?> FindAsync(IHudScreen screen, UUID prim, int? face, CancellationToken ct) =>
        screen.WithHudsAsync<TouchPoint?>((placed, pick) =>
        {
            if (Huds.PartOf(placed, prim) is not { } found) return null;
            if (face is { } f)
                return HudScan.Faces(pick, found.Part).FirstOrDefault(x => x.Face == f) is { } seen ? Huds.Point(seen.Hit) : null;
            return Huds.PickPart(pick, found.Part) is { } hit ? Huds.Point(hit) : null;
        }, null, ct);

    /// <summary>Touches routed: the person's HUD prims to the HUD screen, anything else to <paramref name="world"/>.</summary>
    public static Func<UUID, LibreMetaverse.Vector3?, CancellationToken, Task<TouchPoint?>> Touches(AgentSession s, IHudScreen screen,
        Func<UUID, LibreMetaverse.Vector3?, CancellationToken, Task<TouchPoint?>> world) =>
        (id, at, ct) => Huds.Mine(s, id, out _) ? FindAsync(screen, id, null, ct) : world(id, at, ct);

    public static Func<UUID, int, CancellationToken, Task<TouchPoint?>> Faces(AgentSession s, IHudScreen screen,
        Func<UUID, int, CancellationToken, Task<TouchPoint?>> world) =>
        (id, face, ct) => Huds.Mine(s, id, out _) ? FindAsync(screen, id, face, ct) : world(id, face, ct);
}
