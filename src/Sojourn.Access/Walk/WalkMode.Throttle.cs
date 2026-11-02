using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace Sojourn.Access.Walk;

/// <summary>
/// The throttle as the vehicle itself says it (docs/access-app.md > Stopping a vehicle): "Throttle 40%" in the floating
/// text of the seat's linkset (the blind tester's partner modded her train to show it), or a line from the seat
/// ("throttle 30"); and how far one press moves it, learned from changes that followed a single press. A stop taps Down
/// by this when it's there, until it reads 0 and never below. Lines win over the text once the seat has said one: a
/// line comes as the throttle changes, the text on the script's own timer, so it lags. Written by Claude Opus 5.5,
/// 2026-11-02.
/// </summary>
public sealed partial class WalkMode
{
    // "Throttle 40%", "throttle -5", "THROTTLE: 30 %": the word, a few characters, then the number
    static readonly Regex ThrottleRx = new(@"\bthrottle\b[^\d\n]{0,6}?([+-]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The throttle as last said: its value, when (a line, or the text changing, the throttle with it or not: what
    /// was sent a moment before then shows in it), and whether a line (not the text) said it.</summary>
    readonly record struct ThrottleSeen(float Value, long At, bool Line);

    ThrottleSeen? _thr;
    string? _thrText;
    bool _thrLines;
    long _thrTextAt;
    readonly List<long> _thrTextGaps = new();
    // presses of Up (+1) and Down (-1) sent while seated, the person's and the stop's, the last 20 s
    readonly List<(long T, int Sign)> _edges = new();
    // how far one press moved it: changes that followed exactly one press, the last few; the stop's own, once seen
    readonly List<float> _stepsSeen = new();
    float? _stepOwn;
    readonly ConcurrentQueue<(UUID Source, string Name, float Value)> _thrLinesHeard = new();

    /// <summary>What the vehicle says its throttle is, or null when it says nothing (the status op).</summary>
    public float? Throttle => _thr?.Value;

    /// <summary>How far one press moves the throttle, as seen; the stop's own single taps win, else the largest seen
    /// (too large only taps fewer at once).</summary>
    float? ThrottleStep => _stepOwn ?? (_stepsSeen.Count > 0 ? _stepsSeen.Max() : null);

    /// <summary>How often the seat's text changes when it's changing (it's set on the script's timer, with the speed
    /// beside the throttle): the shortest of the last few gaps.</summary>
    long TextGap => _thrTextGaps.Count == 0 ? 1000 : Math.Clamp(_thrTextGaps.Min(), 250, 3000);

    public static float? ThrottleIn(string? text)
    {
        if (string.IsNullOrEmpty(text) || ThrottleRx.Match(text) is not { Success: true } m) return null;
        return float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    void ThrottleReset()
    {
        (_thr, _thrText, _thrLines, _thrTextAt, _stepOwn) = (null, null, false, 0, null);
        _thrTextGaps.Clear();
        _edges.Clear();
        _stepsSeen.Clear();
    }

    /// <summary>A press of Up or Down sent while seated (the person's, or the stop's tap).</summary>
    void ThrottleEdge(int sign, long now)
    {
        _edges.Add((now, sign));
        while (_edges.Count > 0 && now - _edges[0].T > 20_000) _edges.RemoveAt(0);
    }

    /// <summary>Each tick while seated: the seat's text, and the seat's lines heard.</summary>
    void ThrottleTick(Body b, long now)
    {
        while (_thrLinesHeard.TryDequeue(out var h))
        {
            bool seat = S.Region?.PrimById(h.Source) is { } p && b.Linkset?.Contains(p.LocalID) == true;
            bool named = b.SeatName is { } n && h.Name.Trim().Equals(n.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!b.Sitting || !(seat || named)) continue;
            _thrLines = true;
            ThrottleChanged(h.Value, now, line: true);
        }
        if (!b.Sitting || b.Linkset is not { } set || S.Region is not { } w) return;
        // the part showing a throttle (the root's first); any change of its text is a fresh reading, the same throttle
        // or not (it's set on the script's timer, with the speed beside it)
        string? text = null;
        float shown = 0;
        bool root = false;
        foreach (var id in set)
            if (w.Prims.TryGetValue(id, out var p) && ThrottleIn(p.Text) is { } v && (text == null || p.ParentID == 0 && !root))
                (text, shown, root) = (p.Text, v, p.ParentID == 0);
        if (text == null || text == _thrText) return;
        if (_thrText != null)
        {
            _thrTextGaps.Add(now - _thrTextAt);
            if (_thrTextGaps.Count > 6) _thrTextGaps.RemoveAt(0);
        }
        (_thrText, _thrTextAt) = (text, now);
        if (!_thrLines) ThrottleChanged(shown, now, line: false);
    }

    void ThrottleChanged(float value, long now, bool line)
    {
        var was = _thr;
        _thr = new ThrottleSeen(value, now, line);
        if (was is not { } p || value == p.Value) return;
        // the presses since the last change (from a moment before it, for the text, which may have missed one just
        // before it), sent a moment before this one: exactly one, the way it moved, and it's one press's step
        long before = line ? 0 : 300;
        int n = 0, sign = 0;
        long at = 0;
        foreach (var e in _edges)
            if (e.T > p.At - before && e.T <= now - 30) (n, sign, at) = (n + 1, e.Sign, e.T);
        if (n != 1 || Math.Sign(value - p.Value) != sign) return;
        float step = Math.Abs(value - p.Value);
        if (_vstop is { } s && s.TapTimes.Contains(at)) _stepOwn = step;
        _stepsSeen.Add(step);
        if (_stepsSeen.Count > 4) _stepsSeen.RemoveAt(0);
        Log($"throttle step {step:0.##} ({(line ? "a line" : "the text")}: {p.Value:0.##} to {value:0.##})");
    }
}
