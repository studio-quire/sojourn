namespace Sojourn.Core.Media;

/// <summary>
/// An input file can't be converted for upload. The message says what's wrong and,
/// where there's a fix, what to change; it's written to be shown to whoever supplied the file.
/// </summary>
public sealed class MediaFormatException(string message) : Exception(message);
