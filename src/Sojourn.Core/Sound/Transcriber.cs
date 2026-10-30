namespace Sojourn.Core.Sound;

/// <summary>Speech to text for sound assets that contain speech. One per process, shared by every resident; each
/// asset is transcribed at most once (<see cref="SoundCatalog.SpeechAsync"/>).</summary>
public abstract class Transcriber
{
    public static Transcriber? Shared { get; set; }

    /// <summary>Text, or null when there are no words.</summary>
    public abstract Task<string?> TranscribeAsync(float[] samples, int rate);
}
