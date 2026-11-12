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
/// Control alone silenced on purpose, and none once someone's newer line has been read since the last key: the talk has
/// moved on, and an older line after it only sounds like a repeat (47 of 67 in the blind tester's trivia log of
/// 2026-11-04 came within a second of another line). Her own line read back doesn't count: a line that came as she
/// finished typing would be lost. Alt+0 turns it all off (access_read_cut). (Taking any key within a slower voice's time as a cut, her own lines included,
/// and saying them again at 0.7 s pauses, between words, had a busy game's chat say lines she'd heard over again.)
/// </summary>
public sealed partial class MainForm
{
    sealed record Spoken(string Text, AnnounceKind Kind, long At, long Ends, LineFrom From);

    readonly List<Spoken> _spoken = new();
    readonly List<Spoken> _cut = new();
    readonly object _spokenLock = new();
    long _spokenEnds, _othersAt;

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
            if (!l.Again) NoteWords(l.Text, l.At, again: false, notice: l.Kind == AnnounceKind.Notices);
            if (l.Again || l.From == LineFrom.Me
                || l.Kind is not (AnnounceKind.Nearby or AnnounceKind.Objects or AnnounceKind.Ims or AnnounceKind.Group)) return;
            _othersAt = l.At;
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
            if (_othersAt > typed || !_set.ReadCut) { _cut.Clear(); return; }
            // words a notice said again a moment ago (a HUD's dialog and its chat line carry the same) aren't said twice
            _cut.RemoveAll(s => WordsCovered(s.Text, s.At, long.MaxValue) != null);
            if (_cut.Count == 0) return;
            foreach (var s in _cut) NoteWords(s.Text, now, again: true, notice: false);
            again = Again(_cut);
            kind = _cut[^1].Kind;
            from = _cut.Select(s => s.From).Aggregate(LineSources.Join);
            _cut.Clear();
        }
        if (!_set.MuteAll) reader?.Say(kind, again, keep: false, from, again: true);
    }

    // --- the same words twice ----------------------------------------------------------------------------------------

    /// <summary>Words lately handed to the reader or said again (their letters and digits), for not saying them again:
    /// a HUD often says its dialog's words in chat too, a second on, and a notice cut off and said again had them a
    /// third time and the chat line's own saying again a fourth (the blind tester's freight deliveries, 2026-11-11).</summary>
    readonly List<(string Words, long At, bool Again, bool Notice)> _words = new();

    void NoteWords(string text, long at, bool again, bool notice)
    {
        lock (_spokenLock)
        {
            _words.RemoveAll(w => at - w.At > 30_000);
            if (LetterWords(text) is { Length: > 0 } words) _words.Add((words, at, again, notice));
        }
    }

    /// <summary>A line said again now: its words noted, so the other way of saying lines again doesn't say them too.</summary>
    void SaidAgain(string text) => NoteWords(text, Environment.TickCount64, again: true, notice: true);

    /// <summary>Why a line cut off at <paramref name="cutAt"/> (said at <paramref name="at"/>) needn't be said again, or
    /// null: its words said again since it was said, or read in another line (not a notice) handed to the reader after
    /// the cut and heard out. One cut in turn doesn't count: the lines' own way may not say it again (Alt+0's switch off,
    /// the talk moved on), so this one is said, and that way then leaves the same words out.</summary>
    string? WordsCovered(string text, long at, long cutAt)
    {
        var words = LetterWords(text);
        if (words.Length == 0) return null;
        lock (_spokenLock)
        {
            foreach (var w in _words)
            {
                if (!SameWords(words, w.Words)) continue;
                if (w.Again && w.At >= at) return "said again already";
                // and nothing pressed or moved since it went to the reader: any key stops the reader mid-line, not only one
                // typed into a box (which is all _cut records), so a line a later arrow cut wasn't heard out either
                if (!w.Again && !w.Notice && w.At > cutAt && Math.Max(RealInput.LastKeyAt, _focusMovedAt) < w.At
                    && !_cut.Any(c => c.At == w.At && SameWords(LetterWords(c.Text), w.Words)))
                    return "read after the cut, in another line";
            }
        }
        return null;
    }

    /// <summary>Lower-case letters and digits, one space between runs: "TASK COMPLETE ======. Job" and "TASK COMPLETE
    /// ====== Job" are the same words.</summary>
    static string LetterWords(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The same words, a notice's own "Control N to answer." after them aside; not a short line within a long one
    /// (both start with the object's name, so a short status line would have stood for a longer menu and lost its rest).</summary>
    static bool SameWords(string a, string b)
    {
        if (a == b) return true;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 24 && longer.Length - shorter.Length <= HintLength && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    static int HintLength => LetterWords(Sojourn.Session.Lang.T("Control N to answer.")).Length + 1;   // (AccessWiring adds it, translated)

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
