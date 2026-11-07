namespace Sojourn.Access;

/// <summary>
/// What a mistyped command word most likely was: a known word with the space left out ("texturesnow" as "texture snow"), or
/// one a slip or two away ("hollo", "buiild"). For the build box and // commands (the blind tester's building, 2026-11-06:
/// much of her trouble was typing something wrong and correcting it). Written by Claude Opus 5.5.
/// </summary>
public static class Typos
{
    /// <summary>The corrected words, or null when nothing is close enough to be worth saying.</summary>
    public static string? Closest(string typed, IEnumerable<string> known)
    {
        var word = typed.Trim().ToLowerInvariant();
        if (word.Length < 3) return null;
        var words = known.Select(k => k.ToLowerInvariant()).Distinct().ToList();
        // the space left out: the longest known word it starts with
        if (words.Where(k => k.Length >= 4 && word.Length > k.Length && word.StartsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault() is { } head)
            return $"{head} {typed.Trim()[head.Length..]}";
        string? best = null;
        int least = word.Length <= 4 ? 2 : 3;   // (under this: one slip for a short word, two for a longer one)
        foreach (var k in words)
        {
            if (Math.Abs(k.Length - word.Length) >= least) continue;
            int d = Distance(word, k);
            if (d < least) { least = d; best = k; }
        }
        return best;
    }

    /// <summary>Slips between two words: a letter wrong, missing, extra, or two swapped.</summary>
    static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}
