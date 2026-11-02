namespace Sojourn.Core.Media;

/// <summary>
/// A sound longer than Second Life's 30 seconds cut into consecutive clips that a scripted player plays in order. Cuts
/// fall exactly every clip length (equal clips, for players that start each on a timer), or at the quietest moment in
/// the two seconds before each mark (for players that wait for each clip to end; between a harp's notes rather than
/// through one). Every clip is at most the clip length, and gets a few milliseconds' fade at each cut so it doesn't
/// click. Works on mono at Second Life's 44.1 kHz. Claude Opus 5.5, 2026-11-01.
/// </summary>
public static class SoundClips
{
    /// <summary>Just under the limit, so no clip is refused for a rounding.</summary>
    public const double DefaultSeconds = 29.9;
    public const double MinSeconds = 1;
    public const double FadeMs = 5;
    /// <summary>How far before each mark a quiet cut may fall.</summary>
    public const double QuietWindowSeconds = 2;
    const double FrameMs = 10;

    /// <summary>Where each clip starts and how many samples it has.</summary>
    public readonly record struct Clip(int Start, int Length);

    public static List<Clip> Plan(float[] mono, int rate, double clipSeconds, bool quiet)
    {
        int max = Math.Max(1, (int)Math.Floor(Math.Clamp(clipSeconds, MinSeconds, SoundConvert.MaxSeconds) * rate));
        var clips = new List<Clip>();
        int at = 0;
        while (mono.Length - at > max)
        {
            int cut = at + max;
            if (quiet) cut = Quietest(mono, rate, Math.Max(at + max / 2, cut - (int)(QuietWindowSeconds * rate)), cut);
            clips.Add(new Clip(at, cut - at));
            at = cut;
        }
        if (mono.Length > at) clips.Add(new Clip(at, mono.Length - at));
        return clips;
    }

    /// <summary>The quietest point in [from, to]: the 10 ms frame with the least energy (an earlier one counted a little
    /// louder, up to a quarter at the window's start, keeping clips long), and within it the sample nearest zero.</summary>
    static int Quietest(float[] x, int rate, int from, int to)
    {
        int frame = Math.Max(1, (int)(rate * FrameMs / 1000));
        int best = to;
        double bestEnergy = double.MaxValue;
        for (int end = to; end - frame >= from; end -= frame / 2)
        {
            double e = 0;
            for (int i = end - frame; i < end; i++) e += (double)x[i] * x[i];
            e *= 1 + 0.25 * (to - end) / Math.Max(1, to - from);
            if (e < bestEnergy) { bestEnergy = e; best = end; }
        }
        // (to < x.Length: there's always sound after a cut)
        int cut = best;
        for (int i = best - 1; i >= Math.Max(from, best - frame); i--)
            if (Math.Abs(x[i]) < Math.Abs(x[cut])) cut = i;
        return cut;
    }

    /// <summary>One clip's samples, faded in and out over a few milliseconds (none where the sound itself starts or ends,
    /// which the recording already shaped).</summary>
    public static float[] Cut(float[] mono, Clip c, int rate, bool fadeIn, bool fadeOut)
    {
        var s = mono.AsSpan(c.Start, c.Length).ToArray();
        int n = Math.Min(s.Length / 2, (int)(rate * FadeMs / 1000));
        for (int i = 0; i < n; i++)
        {
            // a raised-cosine ramp
            float g = (float)(0.5 - 0.5 * Math.Cos(Math.PI * (i + 0.5) / n));
            if (fadeIn) s[i] *= g;
            if (fadeOut) s[^(i + 1)] *= g;
        }
        return s;
    }

    /// <summary>The clips' names in order: "Harp song 1" to "Harp song 5", numbered with leading zeros from ten clips on
    /// ("Harp song 01"), since a player script finds its clips in the object in name order. The base is cut so each
    /// name fits Second Life's 63 characters.</summary>
    public static List<string> Names(string baseName, int count, int maxLength = 63)
    {
        int digits = count.ToString().Length;
        var b = baseName.Trim();
        if (b.Length + 1 + digits > maxLength) b = b[..(maxLength - 1 - digits)].TrimEnd();
        return Enumerable.Range(1, count).Select(i => $"{b} {i.ToString().PadLeft(digits, '0')}").ToList();
    }
}
