using System.Globalization;
using System.Numerics;

namespace Sojourn.Access.Walk;

/// <summary>
/// Reading a ring of horizontal collision rays around the person (the sonar key, S in walk mode): what's on each side,
/// and the openings in the walls around (a doorway, a gap between two things, a corridor's mouth). Pure geometry, in
/// the person's frame: the avatar at the origin, ahead along +X, angles counter-clockwise (left positive), as the
/// rays were cast. Written by Claude Opus 5.5, 2026-10-29.
/// </summary>
public static class Sonar
{
    /// <summary>An opening: its middle's bearing (radians, counter-clockwise from ahead), how far its middle is, how
    /// wide it is (metres).</summary>
    public readonly record struct Gap(double Bearing, double Distance, double Width, int A, int B);

    /// <summary>
    /// Openings in a ring of <paramref name="d"/> distances (one per direction, evenly around from ahead,
    /// counter-clockwise; infinity where nothing was met): two neighbouring hits with the rays between them all passing
    /// the line joining the hits by at least <see cref="Through"/> m, the hits 0.45-4 m apart and within 12 m. A ray
    /// that slips between two touching wall parts doesn't count (the hits are centimetres apart).
    /// </summary>
    public static List<Gap> Gaps(double[] d)
    {
        int n = d.Length;
        var gaps = new List<Gap>();
        var hits = Enumerable.Range(0, n).Where(i => double.IsFinite(d[i])).ToList();
        if (hits.Count < 2) return gaps;
        Vector2 P(int i) => Dir(i, n) * (float)d[i];
        for (int k = 0; k < hits.Count; k++)
        {
            int a = hits[k], b = hits[(k + 1) % hits.Count];
            int span = ((b - a) % n + n) % n;
            if (span < 2 || span > n / 3) continue;
            if (Math.Max(d[a], d[b]) > 12) continue;
            var pa = P(a);
            var pb = P(b);
            var e = pb - pa;
            double w = e.Length();
            if (w < 0.45 || w > 4) continue;
            bool through = true;
            for (int s = 1; s < span && through; s++)
            {
                int j = (a + s) % n;
                var u = Dir(j, n);
                double den = Cross(u, e);
                if (Math.Abs(den) < 1e-6) { through = false; break; }
                double t = Cross(pa, e) / den;   // where ray j crosses the line through the two hits
                if (t <= 0 || (double.IsFinite(d[j]) && d[j] < t + Through)) through = false;
            }
            if (!through) continue;
            gaps.Add(Make(pa, pb, a, b));
        }
        return gaps;
    }

    static Gap Make(Vector2 pa, Vector2 pb, int a, int b)
    {
        var mid = (pa + pb) / 2;
        return new Gap(Math.Atan2(mid.Y, mid.X), mid.Length(), (pb - pa).Length(), a, b);
    }

    /// <summary>The sub-rays <see cref="Refine"/> casts for each gap's two edges: <see cref="Sub"/> between the edge's
    /// hit and the first ray through it, at each side (bearings, radians counter-clockwise from ahead).</summary>
    public static IEnumerable<double> RefineBearings(IReadOnlyList<Gap> gaps, int n)
    {
        double step = 2 * Math.PI / n;
        foreach (var g in gaps)
        {
            for (int s = 1; s < Sub; s++) yield return g.A * step + s * step / Sub;
            for (int s = 1; s < Sub; s++) yield return g.B * step - s * step / Sub;
        }
    }

    public const int Sub = 10;

    /// <summary>Each gap's edges moved to the last sub-ray that still met the same thing (within half a metre of the
    /// edge's hit), so a gap seen at a slant isn't measured a whole ray's step too wide. <paramref name="sub"/>: the
    /// sub-rays' distances in <see cref="RefineBearings"/>' order.</summary>
    public static List<Gap> Refine(double[] d, IReadOnlyList<Gap> gaps, IReadOnlyList<double> sub)
    {
        int n = d.Length, per = Sub - 1, k = 0;
        double step = 2 * Math.PI / n;
        var o = new List<Gap>();
        foreach (var g in gaps)
        {
            Vector2 Edge(int hit, int at, int dir)
            {
                double bestB = hit * step, bestD = d[hit];
                for (int s = 1; s < Sub; s++)
                {
                    double ds = sub[at + s - 1];
                    if (!double.IsFinite(ds) || Math.Abs(ds - bestD) > 0.5) break;
                    bestB = hit * step + dir * s * step / Sub;
                    bestD = ds;
                }
                return new Vector2((float)(Math.Cos(bestB) * bestD), (float)(Math.Sin(bestB) * bestD));
            }
            var pa = Edge(g.A, k, +1);
            var pb = Edge(g.B, k + per, -1);
            k += 2 * per;
            o.Add(Make(pa, pb, g.A, g.B));
        }
        return o;
    }

    /// <summary>How far past the line between a gap's edges every ray between them has to reach for it to be an opening.</summary>
    public const double Through = 0.75;

    static Vector2 Dir(int i, int n)
    {
        double a = i * 2 * Math.PI / n;
        return new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
    }

    static double Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>The nearest hit within <paramref name="halfWidth"/> radians of a bearing (counter-clockwise from ahead).</summary>
    public static double Nearest(double[] d, double bearing, double halfWidth)
    {
        int n = d.Length;
        double best = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            double a = i * 2 * Math.PI / n;
            double off = Math.Abs(Math.IEEERemainder(a - bearing, 2 * Math.PI));
            if (off <= halfWidth + 1e-9) best = Math.Min(best, d[i]);
        }
        return best;
    }

    /// <summary>A bearing (radians counter-clockwise from ahead) as a clock face: "two o'clock".</summary>
    public static string Clock(double bearing) => Bearings.Clock(-bearing * 180 / Math.PI);

    /// <summary>
    /// The sweep's words: walls on the four sides (or open), then the openings by clock face, nearest first (at most
    /// three): "Walls: ahead 3 metres, left 1 metre. Open: right, behind. Opening at ten o'clock, 2 metres away, 1.2 metres wide."
    /// <paramref name="range"/>: the rays' reach (beyond it is "open").
    /// </summary>
    public static string Describe(double[] d, double range, IReadOnlyList<Gap>? gaps = null)
    {
        var sides = new (string Name, double Bearing)[] { ("ahead", 0), ("right", -Math.PI / 2), ("behind", Math.PI), ("left", Math.PI / 2) };
        var walls = new List<string>();
        var open = new List<string>();
        foreach (var (name, b) in sides)
        {
            double near = Nearest(d, b, 5 * Math.PI / 180);
            if (double.IsFinite(near) && near < range) walls.Add($"{name} {Metres(near)}");
            else open.Add(name);
        }
        var parts = new List<string>();
        if (walls.Count > 0) parts.Add($"Walls: {string.Join(", ", walls)}.");
        if (open.Count > 0) parts.Add(open.Count == 4 ? $"Nothing within {Bearings.Metres(range)}." : $"Open: {string.Join(", ", open)}.");
        foreach (var g in (gaps ?? Gaps(d)).Where(g => g.Width >= 0.45).OrderBy(g => g.Distance).Take(3))
            parts.Add($"{(g.Width < 0.7 ? "Narrow opening" : "Opening")} at {Clock(g.Bearing)}, {Metres(g.Distance)} away, {Metres(g.Width, wide: true)} wide.");
        return string.Join(" ", parts);
    }

    /// <summary>Metres in words: "3 metres", "under a metre", and for widths "1.2 metres".</summary>
    public static string Metres(double m, bool wide = false) =>
        wide ? Bearings.MetresTenths(m) : Bearings.Metres(m);
}
