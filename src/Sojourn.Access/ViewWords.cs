using Sojourn.Session.Offscreen;

namespace Sojourn.Access;

/// <summary>What the person hears when something leans on the renderer out of sight and it isn't there (a part's faces,
/// pictures). Their words for it: "the 3D view" (they may well know what graphics are; "out of sight" is ours). Walk
/// mode never needs it: its rays come from the collision model, which needs no GPU.</summary>
static class ViewWords
{
    const string What = "the 3D view that faces and pictures need";

    /// <summary>Why the view isn't there, as one sentence.</summary>
    public static string Down(OffscreenRenderer? v) => v switch
    {
        null => "The 3D view is off (started with --no-render).",
        { Running: true } => "The 3D view is running.",
        { NeverRan: true } => $"This PC's graphics can't run {What}.",
        { State: "stopped" } => $"{Cap(What)} stopped after failing three times; restarting Sojourn Access tries it again.",
        _ => $"{Cap(What)} is starting; try again in a moment.",
    };

    /// <summary>Given up for this session (no driver, or three failures), not merely starting again.</summary>
    public static bool Gone(OffscreenRenderer? v) => v is null or { Running: false, State: "stopped" };

    static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
