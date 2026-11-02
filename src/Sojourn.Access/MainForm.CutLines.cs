using Sojourn.Core.Access;

namespace Sojourn.Access;

/// <summary>
/// Lines cut off by typing. NVDA cancels all its speech at each key typed into a box ("speech interrupt for typed
/// characters", and for Enter, both on by default; its source clears every priority's queue, so nothing sent to it
/// survives), and other readers do the same, so a line that arrives while the person types is lost at the next key.
/// Such a line is said again once the keys have rested a second: only one a typed key came into before it could have
/// been heard out (still queued behind others, or within its speaking time at a quick voice, and on NVDA 2026.3 and
/// later not after the reader was seen falling quiet), never her own lines (she wrote them) or a line already said
/// again; those cut in one stretch of typing as one, the newest three at most, after how many there were; none that
/// Control alone silenced on purpose. (Taking any key within a slower voice's time as a cut, her own lines included,
/// and saying them again at 0.7 s pauses, between words, had a busy game's chat say lines she'd heard over again.)
/// </summary>
public sealed partial class MainForm
{
    sealed record Spoken(string Text, AnnounceKind Kind, long At, long Ends, LineFrom From);

    readonly List<Spoken> _spoken = new();
    readonly List<Spoken> _cut = new();
    readonly object _spokenLock = new();
    long _spokenEnds;

    /// <summary>A pause in typing rather than the gap between two words, before what was cut is said again.</summary>
    const long KeysRest = 1000;
    /// <summary>At most this many lines said again, and about this many characters: the newest, the rest counted.</summary>
    const int AgainLines = 3, AgainChars = 500;

    /// <summary>An arrival just handed to the reader (the announcer's thread).</summary>
    void Spoke(Announcer.SpokenLine l)
    {
        lock (_spokenLock)
        {
            // the reader's queue: each line starts once what's before it has been said
            long starts = Math.Max(l.At, _spokenEnds);
            _spokenEnds = starts + Speaking(l.Text);
            if (l.Again || l.From == LineFrom.Me
                || l.Kind is not (AnnounceKind.Nearby or AnnounceKind.Objects or AnnounceKind.Ims or AnnounceKind.Group)) return;
            _spoken.Add(new Spoken(l.Text, l.Kind, l.At, _spokenEnds, l.From));
        }
    }

    static string Ended(string line) => line.Length == 0 || line[^1] is '.' or '!' or '?' ? line : line + ".";

    /// <summary>About as long as a quick screen reader voice takes over it (some 330 words a minute): a key after that
    /// leaves it heard, so a guess wrong this way loses a line to review rather than saying one twice.</summary>
    static long Speaking(string text) => Math.Clamp(text.Length * 30L, 500, 10_000);

    /// <summary>On the window's tick.</summary>
    void LinesCutOff()
    {
        long now = Environment.TickCount64, typed = RealInput.LastTypedAt;
        var reader = Reader;
        string? again = null;
        AnnounceKind kind = AnnounceKind.Nearby;
        LineFrom from = LineFrom.People;
        lock (_spokenLock)
        {
            if (_spoken.Count == 0 && _cut.Count == 0) return;
            bool cut = false;
            for (int i = 0; i < _spoken.Count; i++)
            {
                var s = _spoken[i];
                // the first key typed after it went to the reader, the one that cut it if any did
                long key = typed > s.At ? RealInput.FirstTypedAfter(s.At) : 0;
                if (key == 0 && typed > s.At) key = typed;
                // the reader seen quiet after it had a moment to start it, before any key: heard out (NVDA 2026.3+)
                bool heard = reader?.QuietBetween(s.At + 250, key > 0 ? key : now) == true;
                if (key > 0 && key < s.Ends && !heard) { _cut.Add(s); _spoken.RemoveAt(i--); cut = true; }
                else if (heard || key > 0 || now > s.Ends) _spoken.RemoveAt(i--);
            }
            // the reader's queue was emptied by that key
            if (cut) _spokenEnds = typed;
            _cut.RemoveAll(s => RealInput.SilencedAt > s.At);
            if (_cut.Count == 0 || now - RealInput.LastKeyAt < KeysRest) return;
            again = Again(_cut);
            kind = _cut[^1].Kind;
            from = _cut.Select(s => s.From).Aggregate(LineSources.Join);
            _cut.Clear();
        }
        if (!_set.MuteAll) reader?.Say(kind, again, keep: false, from, again: true);
    }

    /// <summary>The words for lines said again: all of them in the order they came, or past three lines or 500
    /// characters the newest that fit, after how many there were (the review keys have them all).</summary>
    static string Again(List<Spoken> cut)
    {
        var said = new List<string>();
        int chars = 0;
        for (int i = cut.Count - 1; i >= 0 && said.Count < AgainLines; i--)
        {
            var line = Ended(cut[i].Text.TrimEnd());
            if (said.Count > 0 && chars + line.Length > AgainChars) break;
            said.Insert(0, line);
            chars += line.Length + 1;
        }
        var words = string.Join(" ", said);
        if (said.Count == cut.Count) return words;
        return said.Count == 1 ? $"{cut.Count} messages while you typed, the last: {words}"
            : $"{cut.Count} messages while you typed, the last {said.Count}: {words}";
    }
}
