namespace Sojourn.Session;

/// <summary>
/// Where an installed Sojourn keeps the person's things: %LOCALAPPDATA%\Sojourn (accounts and keys under secrets\,
/// settings, the assistant's memory, caches, logs), never beside the program, which an update replaces. Runs from the
/// repo use local/ instead (each caller's own rule). SOJOURN_DATA puts it elsewhere, for the package's check
/// (tools/package.py), which runs an installed copy as a person would without touching this PC's real folder; the
/// assistant's Python side follows the same variable (assistant/provider.py, harness.py). Linked into the launcher.
/// </summary>
public static class DataFolder
{
    public static string Root =>
        Environment.GetEnvironmentVariable("SOJOURN_DATA") is { Length: > 0 } d
            ? Path.GetFullPath(d)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sojourn");
}
