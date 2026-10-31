using SkiaSharp;
using Sojourn.Core.Agents;
using Sojourn.Session.Assistant;
using static Sojourn.Session.Assistant.Panel;

namespace Sojourn.Session.Social;

/// <summary>
/// SLurls in text the person reads (the feed's lines, the reader's notecards) as the viewer shows them: a link labelled
/// with the place, "Region (x, y, z)" (LLUrlEntrySLURL's label), in the look's accent and underlined; a press on it
/// offers the place on the feed with its Teleport key (Places.Offer), as the viewer's click opens the place. Text
/// without a SLurl goes the old way (Panel.Wrap). Claude Opus 5.5, 2026-10-28 (batch B4).
/// </summary>
public static class Links
{
    /// <summary>A word as laid out: its text and the link it belongs to (-1: plain text).</summary>
    public readonly record struct Word(string Text, int Link);

    /// <summary>Text cut into words, SLurls replaced by their labels; and the places, by link index.</summary>
    public sealed record Parsed(IReadOnlyList<IReadOnlyList<Word>> Paragraphs, IReadOnlyList<Places.Place> Places);

    public static bool Has(string? text) => !string.IsNullOrEmpty(text) && (text.Contains("secondlife:", StringComparison.OrdinalIgnoreCase)
        || text.Contains("maps.secondlife.com", StringComparison.OrdinalIgnoreCase) || text.Contains("slurl.com", StringComparison.OrdinalIgnoreCase));

    public static string Label(Places.Place p) => $"{p.Region} ({(int)p.Pos.X}, {(int)p.Pos.Y}, {(int)p.Pos.Z})";

    /// <summary>The text with each SLurl as its label (where it isn't pressable: chat bubbles, what's translated).</summary>
    public static string Labelled(string text)
    {
        if (!Has(text)) return text;
        var found = Places.Find(text);
        for (int i = found.Count - 1; i >= 0; i--) text = text[..found[i].Start] + Label(found[i].Place) + text[(found[i].Start + found[i].Length)..];
        return text;
    }

    /// <summary>Paragraphs (at line breaks) of words, each SLurl one link of its label's words.</summary>
    public static Parsed Parse(string text)
    {
        var places = new List<Places.Place>();
        var paras = new List<IReadOnlyList<Word>>();
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var words = new List<Word>();
            int at = 0;
            foreach (var (start, length, place) in Places.Find(para))
            {
                Plain(para[at..start], words);
                int link = places.FindIndex(p => Same(p, place));
                if (link < 0) { link = places.Count; places.Add(place); }
                foreach (var w in Label(place).Split(' ', StringSplitOptions.RemoveEmptyEntries)) words.Add(new Word(w, link));
                at = start + length;
            }
            Plain(para[at..], words);
            paras.Add(words);
        }
        return new Parsed(paras, places);
    }

    static bool Same(Places.Place a, Places.Place b) =>
        string.Equals(a.Region, b.Region, StringComparison.OrdinalIgnoreCase) && LibreMetaverse.Vector3.Distance(a.Pos, b.Pos) < 1;

    static void Plain(string s, List<Word> into)
    {
        foreach (var w in s.Split(' ', StringSplitOptions.RemoveEmptyEntries)) into.Add(new Word(w, -1));
    }

    /// <summary>Greedy wrap of words into lines (the first starting firstIndent in), a word too long for a line split
    /// across lines; past maxLines the last ends in "...", as Panel.Wrap.</summary>
    public static List<List<Word>> Wrap(Parsed text, SKFont font, float width, float firstIndent, int maxLines)
    {
        var lines = new List<List<Word>>();
        float space = Measure(font, " ");
        foreach (var para in text.Paragraphs)
        {
            var cur = new List<Word>();
            float curW = 0;
            float Room() => lines.Count == 0 ? width - firstIndent : width;
            foreach (var word in para)
            {
                float ww = Measure(font, word.Text);
                float next = cur.Count == 0 ? ww : curW + space + ww;
                if (next <= Room()) { cur.Add(word); curW = next; continue; }
                if (cur.Count > 0) { lines.Add(cur); cur = new List<Word>(); curW = 0; }
                var rest = word.Text;
                while (Measure(font, rest) > Room() && FallbackText.Before(rest, rest.Length) > 0)
                {
                    int n = FallbackText.Before(rest, rest.Length);
                    while (FallbackText.Before(rest, n) > 0 && Measure(font, rest[..n]) > Room()) n = FallbackText.Before(rest, n);
                    lines.Add(new List<Word> { new(rest[..n], word.Link) });
                    rest = rest[n..];
                }
                cur.Add(new Word(rest, word.Link));
                curW = Measure(font, rest);
            }
            lines.Add(cur);
        }
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            var last = lines[^1];
            // room for the "..." at the end, words dropped from the end until it fits
            while (last.Count > 0 && Measure(font, string.Join(' ', last.Select(w => w.Text)) + " ...") > width) last.RemoveAt(last.Count - 1);
            last.Add(new Word("...", -1));
        }
        return lines;
    }

    /// <summary>A line's width.</summary>
    public static float Width(IReadOnlyList<Word> line, SKFont font) => Measure(font, string.Join(' ', line.Select(w => w.Text)));

    /// <summary>Draws a line of words from x on the baseline y: plain words in ink, links in the accent and underlined
    /// (lit: the pointer's on it, a heavier line); returns each link's box (its words on this line, in the canvas's
    /// units) so a press can find it. A null canvas only measures.</summary>
    public static List<(SKRect Box, int Link)> Draw(SKCanvas? c, IReadOnlyList<Word> line, float x, float y, SKFont font, SKColor ink, SKColor accent,
        int lit = -1)
    {
        var boxes = new List<(SKRect, int)>();
        float space = Measure(font, " ");
        float lineH = font.Size;
        for (int i = 0; i < line.Count; i++)
        {
            var w = line[i];
            float ww = Measure(font, w.Text);
            if (c != null)
            {
                using var p = new SKPaint { Color = w.Link >= 0 ? accent : ink, IsAntialias = true };
                FallbackText.Draw(c, w.Text, x, y, font, p);
                if (w.Link >= 0)
                {
                    // the underline runs through the space to the next word of the same link
                    bool joined = i + 1 < line.Count && line[i + 1].Link == w.Link;
                    using var u = new SKPaint { Color = accent, IsAntialias = true, StrokeWidth = w.Link == lit ? 1.6f : 1 };
                    c.DrawLine(x, y + 2.5f, x + ww + (joined ? space : 0), y + 2.5f, u);
                }
            }
            if (w.Link >= 0)
            {
                var box = new SKRect(x - 2, y - lineH * 0.95f, x + ww + 2, y + lineH * 0.4f);
                if (boxes.Count > 0 && boxes[^1].Item2 == w.Link) boxes[^1] = (SKRect.Union(boxes[^1].Item1, box), w.Link);
                else boxes.Add((box, w.Link));
            }
            x += ww + space;
        }
        return boxes;
    }
}
