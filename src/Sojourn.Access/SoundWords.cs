using Sojourn.Session;
using Sojourn.Session.Audio;

namespace Sojourn.Access;

/// <summary>Why there's no sound engine, in a person's words. sojourn_voice.dll (world audio, earcons, walk mode's
/// sounds, voice chat) imports Media Foundation, which Windows' N editions leave out unless the Media Feature Pack is
/// added, so that's the likely reason on a PC that has the DLL.</summary>
static class SoundWords
{
    /// <summary>Null when the engine is up, or when sounds were turned off on purpose (--no-sounds).</summary>
    public static string? Missing(SessionHost? host)
    {
        if (host == null || !host.Options.Sounds || AudioEngine.Init(out _)) return null;
        return File.Exists(Path.Combine(Environment.SystemDirectory, "mfplat.dll"))
            ? "Sounds can't play on this PC: Sojourn Access's sound engine didn't start."
            : "Sounds can't play on this PC: Windows' media features are missing. README.txt says how to add them.";
    }
}
