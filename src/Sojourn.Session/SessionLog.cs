using System.Text;

namespace Sojourn.Session;

/// <summary>
/// Everything the console shows (the client's log, chat, LibreMetaverse's warnings) also goes to a file per session,
/// <c>logs/session-YYYYMMDD-HHMMSS.log</c> in the base dir, so a session's story survives the console closing. The
/// newest ten are kept. Native code writing to stdout (the OpenXR runtime) bypasses it.
/// While <see cref="Paused"/> (private mode: Private/PrivateWiring.cs) nothing is written, to the console or the file:
/// what the client logs can name people, places and what was said.
/// The file marks whose words its lines carry, so that a log sent to the studio can leave out other people's exactly
/// (LogShare.cs, docs/access-app.md > Sending a log): it starts with a <c>~log</c> line naming its format, and what's
/// said and read out and the names learned are records of their own, <c>HH:mm:ss ~tag {json}</c> (<see cref="Record"/>,
/// LogWords.cs), never plain lines; the console shows chat as before (<see cref="ToConsole"/>).
/// </summary>
public static class SessionLog
{
    /// <summary>True while the console and the file take nothing.</summary>
    public static Func<bool>? Paused { get; set; }

    /// <summary>The file's format, in its first line: 2, the records whose words they carry.</summary>
    public const int Format = 2;

    static TextWriter? _console, _file;

    /// <summary>A line written whatever <see cref="Paused"/> says (private mode's own start and end).</summary>
    public static void Note(string line)
    {
        try { (_console ?? Console.Out).WriteLine(line); } catch (Exception) { }
        try { _file?.WriteLine(line); } catch (Exception) { }
    }

    /// <summary>A record in the file alone (not the console): <c>HH:mm:ss ~tag {json}</c>, on one line. Nothing while
    /// <see cref="Paused"/>.</summary>
    public static void Record(string tag, System.Text.Json.Nodes.JsonObject o)
    {
        if (_file is null || Paused?.Invoke() == true) return;
        try { _file.WriteLine($"{DateTime.Now:HH:mm:ss} ~{tag} {o.ToJsonString(LogShare.Json)}"); } catch (Exception) { }
    }

    /// <summary>A line on the console alone (chat as it comes: the file has it as a record). Nothing while
    /// <see cref="Paused"/>.</summary>
    public static void ToConsole(string line)
    {
        if (Paused?.Invoke() == true) return;
        try { (_console ?? Console.Out).WriteLine(line); } catch (Exception) { }
    }

    public static string? Start(string baseDir)
    {
        try
        {
            var dir = Path.Combine(baseDir, "logs");
            Directory.CreateDirectory(dir);
            foreach (var old in new DirectoryInfo(dir).GetFiles("session-*.log").OrderByDescending(f => f.Name).Skip(9))
                try { old.Delete(); } catch (IOException) { }
            var path = Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            var sync = TextWriter.Synchronized(file);
            sync.WriteLine($"{DateTime.Now:HH:mm:ss} ~log {{\"v\":{Format}}}");
            (_console, _file) = (Console.Out, sync);
            Console.SetOut(new Tee(Console.Out, sync));
            Console.SetError(new Tee(Console.Error, sync));
            return path;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"session log: {e.Message}");
            return null;
        }
    }

    /// <summary>The console as it was, and the file in whole lines: the console logger writes a line in pieces (its time
    /// stamp, the coloured level, the rest), and a record written whole from another thread between them took the stamp,
    /// which hid it from Send a log's filter as a record (about 1 in 100 in a test run, 2026-11-08). So what goes to the
    /// file waits, per thread, for its line's end.</summary>
    sealed class Tee(TextWriter a, TextWriter b) : TextWriter
    {
        readonly ThreadLocal<StringBuilder> _line = new(() => new StringBuilder());
        public override Encoding Encoding => a.Encoding;
        readonly ThreadLocal<bool> _cut = new();
        public override void Write(char value) => Take(value.ToString());
        public override void Write(string? value) => Take(value);
        public override void Write(char[] buffer, int index, int count) => Take(new string(buffer, index, count));
        public override void WriteLine(string? value) => Take(value + NewLine);
        public override void Flush() { a.Flush(); Safe(b.Flush); }

        /// <summary>A piece of a line from this thread: nothing of it while paused, and a line the pause began or ended
        /// inside goes whole, the pieces on either side of the pause too. Kept, a time stamp written just before a spell
        /// of incognito began stood in front of the first line after it ("08:05:42 08:05:44 info: ...", no record to
        /// Send a log's filter: a test run's log, 2026-11-10), and the end of a line begun inside one would have been
        /// logged after it.</summary>
        void Take(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (Paused?.Invoke() == true)
            {
                _line.Value!.Clear();
                _cut.Value = s[^1] != '\n';
                return;
            }
            if (_cut.Value)
            {
                int end = s.IndexOf('\n');
                if (end < 0) return;
                _cut.Value = false;
                s = s[(end + 1)..];
                if (s.Length == 0) return;
            }
            a.Write(s);
            ToFile(s);
        }

        void ToFile(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            var line = _line.Value!;
            line.Append(s);
            int end = -1;
            for (int i = line.Length - 1; i >= 0; i--) if (line[i] == '\n') { end = i; break; }
            if (end < 0) return;
            var whole = line.ToString(0, end + 1);
            line.Remove(0, end + 1);
            Safe(() => b.Write(whole));
        }

        // a full disk or a closed file never takes the console down with it
        static void Safe(Action act) { try { act(); } catch (Exception) { } }
    }
}
