namespace Sojourn.Session.Assistant;

/// <summary>
/// What a frame shows over its first eye image, for a picture of what the person sees, menus and all (LiveSnapshots'
/// showui pictures). The app lays its own menus over the eye; the VR client's is Assistant/SeenShots.cs (SeenView).
/// </summary>
public interface ISeenView
{
    /// <summary>The menus laid over the eye, by name, in the order they're laid.</summary>
    IReadOnlyList<string> LayerNames { get; }
    /// <summary>Menus up but left out of the picture, by name, with why.</summary>
    IReadOnlyList<string> LeftOut { get; }
    /// <summary>The eye image (RGBA8, w x h) with the menus over it; the image itself when there are none.</summary>
    byte[] Compose(byte[] rgba, int w, int h);
    /// <summary>A close-up of the named menu (its own pixels or about), or null with why.</summary>
    (byte[] Rgba, int W, int H)? CloseUp(byte[] rgba, int w, int h, string name, out string? error);
}
