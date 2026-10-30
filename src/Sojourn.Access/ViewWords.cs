using Sojourn.Session.Offscreen;

namespace Sojourn.Access;

/// <summary>What the person hears when something leans on the renderer out of sight and it isn't there. Their words
/// for it: "the 3D view" (they may well know what graphics are; "out of sight" is ours).</summary>
static class ViewWords
{
    const string What = "the 3D view that tells Sojourn Access what's around you";

    /// <summary>Why the view isn't there, as one sentence.</summary>
    public static string Down(OffscreenRenderer? v) => v switch
    {
        null => "The 3D view is off (started with --no-render): nothing tells Sojourn Access what's around you.",
        { Running: true } => "The 3D view is running.",
        { NeverRan: true } => $"This PC's graphics can't run {What}.",
        { State: "stopped" } => $"{Cap(What)} stopped after failing three times; restarting Sojourn Access tries it again.",
        _ => $"{Cap(What)} is starting; try again in a moment.",
    };

    /// <summary>Given up for this session (no driver, or three failures), not merely starting again.</summary>
    public static bool Gone(OffscreenRenderer? v) => v is null or { Running: false, State: "stopped" };

    static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
