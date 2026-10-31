using Sojourn.Core.Access;
using Sojourn.Session.Social;

namespace Sojourn.Access;

/// <summary>
/// Lines cut off by typing. NVDA cancels all its speech at each key typed into a box ("speech interrupt for typed
/// characters", on by default; its source clears every priority's queue, so nothing sent to it survives), and other
/// readers do the same, so a line that arrives while the person types in the Say box is lost at the next key. One
/// whose speaking time a typed key fell in is said again once the keys have rested, those cut together as one, each
/// only once; not if Control alone silenced it on purpose.
/// </summary>
public sealed partial class MainForm
{
    sealed record Spoken(string Text, AnnounceKind Kind, long At, long Ends);

    readonly List<Spoken> _spoken = new();
    readonly List<Spoken> _cut = new();
    readonly object _spokenLock = new();
    long _spokenEnds;

    const long KeysRest = 700;

    /// <summary>A line just shaped for the reader (the announcer's watch thread), about to go into its queue.</summary>
    void LineSaid(FeedEntry e, string text)
    {
        if (Reader is not { Active: true } r || Host?.Assistant?.Confirmations.Pending == true) return;   // held lines are read later
        var kind = e.Style == FeedStyle.Object ? AnnounceKind.Objects
            : e.Kind == FeedKind.Nearby ? AnnounceKind.Nearby : e.Kind == FeedKind.Group ? AnnounceKind.Group : AnnounceKind.Ims;
        if (!r.Settings.Wants(kind)) return;
        long now = Environment.TickCount64;
        lock (_spokenLock)
        {
            // the reader's queue: it starts once what's before it has been said
            long starts = Math.Max(now, _spokenEnds);
            _spokenEnds = starts + Speaking(text);
            _spoken.Add(new Spoken(text, kind, now, _spokenEnds));
        }
    }

    static string Ended(string line) => line.Length == 0 || line[^1] is '.' or '!' or '?' ? line : line + ".";

    /// <summary>About as long as a quick screen reader voice takes over it.</summary>
    static long Speaking(string text) => Math.Clamp(text.Length * 40L, 800, 8000);

    /// <summary>On the window's tick.</summary>
    void LinesCutOff()
    {
        long now = Environment.TickCount64, typed = RealInput.LastTypedAt;
        string? again = null;
        AnnounceKind kind = AnnounceKind.Nearby;
        lock (_spokenLock)
        {
            if (_spoken.Count == 0 && _cut.Count == 0) return;
            bool cut = false;
            for (int i = 0; i < _spoken.Count; i++)
            {
                var s = _spoken[i];
                if (typed >= s.At && typed < s.Ends) { _cut.Add(s); _spoken.RemoveAt(i--); cut = true; }
                else if (now > s.Ends) _spoken.RemoveAt(i--);
            }
            // the reader's queue was emptied by that key
            if (cut) _spokenEnds = typed;
            _cut.RemoveAll(s => RealInput.SilencedAt > s.At);
            if (_cut.Count == 0 || now - RealInput.LastKeyAt < KeysRest) return;
            again = string.Join(" ", _cut.Select(s => Ended(s.Text.TrimEnd())));
            kind = _cut[0].Kind;
            _cut.Clear();
        }
        Reader?.Say(kind, again, keep: false);
    }
}
