using System.Globalization;
using System.Text;

namespace Sojourn.Core.Agents;

/// <summary>
/// A chat line or IM too long for the grid, in parts that fit: Second Life cuts both at 1023 bytes of UTF-8 (the
/// viewer's MAX_MSG_BUF_SIZE less the terminator), so a longer one goes as several, each at most <see cref="MaxBytes"/>
/// (a blind tester's pasted description lost its end in an IM, 2026-11-02). A part ends at a sentence's end where one
/// falls in its second half, else at a clause's (a comma, semicolon, colon or dash), else between words, else between
/// characters, never inside one (whole grapheme clusters: a surrogate pair, an emoji's sequence, a letter's accents).
/// An emote stays one: every part after the first starts "/me ".
/// </summary>
public static class ChatSplit
{
    /// <summary>The grid's 1023 bytes, less room to spare.</summary>
    public const int MaxBytes = 1000;

    public static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>The text as it goes: itself alone when it fits.</summary>
    public static List<string> Split(string text, int maxBytes = MaxBytes)
    {
        if (Bytes(text) <= maxBytes) return [text];
        var prefix = IsEmote(text) ? "/me " : "";
        var parts = new List<string>();
        var rest = text.Trim();
        while (rest.Length > 0)
        {
            var more = parts.Count > 0 ? prefix : "";
            int budget = maxBytes - Bytes(more);
            if (Bytes(rest) <= budget) { parts.Add(more + rest); break; }
            int cut = Cut(rest, Fit(rest, budget));
            var piece = rest[..cut].TrimEnd();
            if (piece.Length > 0) parts.Add(more + piece);
            rest = rest[cut..].TrimStart();
        }
        return parts;
    }

    /// <summary>Between parts, so they arrive in order.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(600);

    /// <summary>Sends the text, in parts if it's too long, each in order with <see cref="Gap"/> between; a line sent
    /// while another's parts are going waits its turn. Once begun, every part goes. The number of parts.</summary>
    public static async Task<int> SendAsync(SemaphoreSlim turn, string text, Action<string> send, CancellationToken ct)
    {
        var parts = Split(text);
        await turn.WaitAsync(ct);
        try
        {
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) await Task.Delay(Gap, CancellationToken.None);
                send(parts[i]);
            }
        }
        finally { turn.Release(); }
        return parts.Count;
    }

    public static bool IsEmote(string s) => s.StartsWith("/me ", StringComparison.Ordinal) || s.StartsWith("/me'", StringComparison.Ordinal);

    /// <summary>How many characters from the start fit in the budget, ending on a whole grapheme cluster (or, for a
    /// cluster longer than the budget by itself, a whole code point).</summary>
    static int Fit(string s, int budget)
    {
        int bytes = 0, fit = 0;
        for (int i = 0; i < s.Length;)
        {
            int len = StringInfo.GetNextTextElementLength(s, i);
            bytes += Encoding.UTF8.GetByteCount(s.AsSpan(i, len));
            if (bytes > budget) break;
            i += len;
            fit = i;
        }
        if (fit > 0) return fit;
        bytes = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if ((bytes += r.Utf8SequenceLength) > budget) break;
            fit += r.Utf16SequenceLength;
        }
        return Math.Max(fit, 1);
    }

    /// <summary>Where to end the part within the first <paramref name="fit"/> characters.</summary>
    static int Cut(string s, int fit)
    {
        int half = fit / 2;
        for (int j = fit; j > half; j--) if (SentenceEndsAt(s, j)) return j;
        for (int j = fit; j > half; j--) if (ClauseEndsAt(s, j)) return j;
        for (int j = fit; j > 0; j--) if (char.IsWhiteSpace(s[j])) return j;
        return fit;
    }

    const string Closers = ")]}\"'”’»";

    /// <summary>A sentence ends just before index j: . ! ? or … (then any closing quotes or brackets), a space, and next
    /// no lowercase letter ("e.g. this" isn't an end); or a full-width 。！？, which needs no space.</summary>
    static bool SentenceEndsAt(string s, int j)
    {
        if (j <= 0 || j > s.Length) return false;
        if ("。！？".Contains(s[j - 1])) return true;
        if (j == s.Length || !char.IsWhiteSpace(s[j])) return false;
        int k = j - 1;
        while (k > 0 && Closers.Contains(s[k])) k--;
        if (!".!?…".Contains(s[k])) return false;
        int n = j;
        while (n < s.Length && char.IsWhiteSpace(s[n])) n++;
        return n == s.Length || !char.IsLower(s[n]);
    }

    static bool ClauseEndsAt(string s, int j)
    {
        if (j <= 0 || j > s.Length) return false;
        if ("、，；：".Contains(s[j - 1])) return true;
        return j < s.Length && char.IsWhiteSpace(s[j]) && ",;:–—".Contains(s[j - 1]);
    }
}
