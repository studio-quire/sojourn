using Sojourn.Access.Walk;

namespace Sojourn.Access.Sound;

/// <summary>
/// A sound of its own for someone's IMs, or a group's chat, in place of the usual IM (or group) sound, so who wrote can
/// be told before the words: a blind tester's wish, 2026-11-01, after chasing down which IM a sound was for. Chosen by
/// ear from a list (a person's or group's Applications key, a conversation's, or //imsound), kept by key in
/// access-settings.json (AccessSettings.ImSoundFor), played by Earcons at the IM's (or group chat's) level. The sounds
/// are made in Walk/CueSounds.Own.cs. Written by Claude Opus 5.5, 2026-11-01.
/// </summary>
public static class ImSounds
{
    /// <summary>The sounds by the names she chooses them by, in the order listed.</summary>
    public static readonly (string Name, CueSounds.Cue Cue)[] All =
    {
        ("bird call", CueSounds.Cue.ImBird),
        ("cricket", CueSounds.Cue.ImCricket),
        ("harp", CueSounds.Cue.ImHarp),
        ("heartbeat", CueSounds.Cue.ImHeartbeat),
        ("horn", CueSounds.Cue.ImHorn),
        ("music box", CueSounds.Cue.ImMusicBox),
        ("ocean wave", CueSounds.Cue.ImWave),
        ("sleigh bells", CueSounds.Cue.ImSleighBells),
        ("steel drum", CueSounds.Cue.ImSteelDrum),
        ("wind chime", CueSounds.Cue.ImChime),
    };

    public static CueSounds.Cue? CueOf(string? name) => name == null ? null : All.FirstOrDefault(s => s.Name == name) is { Name: not null } s ? s.Cue : null;

    /// <summary>A sound named in words (any case, extra spaces, "the" before it): its name; "" for the usual sound; null
    /// for neither.</summary>
    public static string? Find(string words)
    {
        var w = string.Join(' ', words.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (w.StartsWith("the ")) w = w[4..];
        // the usual one: "usual", "the usual IM sound", "default"
        if (w.StartsWith("usual") || w == "default") return "";
        return All.FirstOrDefault(s => s.Name == w).Name;
    }

    public static string Names => string.Join(", ", All.Select(s => s.Name));
}
