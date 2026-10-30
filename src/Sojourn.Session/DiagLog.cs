using System.Text;

/// <summary>Diagnostic logs named by environment variables (SOJOURN_VR_LOG, SOJOURN_FRAME_LOG, ...). Opening one must
/// never take the client down: after a lost GPU the client starts again while the old process may still hold the file
/// (2026-10-28: the restart died on vr.csv "being used by another process"), so a held file falls back to one named
/// with this process's id, and anything else to no log.</summary>
static class DiagLog
{
    public static StreamWriter? Open(string variable, bool autoFlush = true)
    {
        if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } path) return null;
        foreach (var p in new[] { path, WithPid(path) })
        {
            try
            {
                return new StreamWriter(new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = autoFlush };
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    static string WithPid(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}-{Environment.ProcessId}{Path.GetExtension(path)}");
}
