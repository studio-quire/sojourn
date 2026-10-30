using System.Globalization;
using System.Text.RegularExpressions;

namespace Sojourn.Access;

/// <summary>
/// Where to put something, in the person's words (docs/access.md > M2, "place by words"): "a metre to my left", "2 m
/// ahead and half a metre up", "at two o'clock, 3 metres", "1.5 m north", "here" (at their feet), "in front of me",
/// "by 20 cm to the left" (from where the thing is now, for moving it). Distances in metres, centimetres or feet, as
/// numbers or a few words ("a", "half a", "two"); a direction without a distance is a metre. Directions are the
/// person's (ahead, back, left, right, a clock face) or the compass's; up and down raise or lower it from the surface
/// it rests on. Pure parsing; the caller turns it into a point with the avatar's position and heading.
/// </summary>
public static class PlaceWords
{
    /// <summary>What the words said: metres ahead, to the right, north and east (the person's and the compass's frames,
    /// added), up from the surface; Here: at the person's feet; Relative: from where the thing is now ("by ...");
    /// Horizontal: any of the first four was given.</summary>
    public sealed record Spot(double Ahead, double Right, double North, double East, double Up, bool Here, bool Relative)
    {
        public bool Horizontal => Here || Ahead != 0 || Right != 0 || North != 0 || East != 0;

        /// <summary>The horizontal offset in the region frame (x east, y north) for a compass heading (0 north, clockwise).</summary>
        public (double X, double Y) Offset(double heading)
        {
            double h = heading * Math.PI / 180;
            return (Ahead * Math.Sin(h) + Right * Math.Cos(h) + East, Ahead * Math.Cos(h) - Right * Math.Sin(h) + North);
        }
    }

    static readonly Dictionary<string, double> Numbers = new()
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["half a"] = 0.5, ["half"] = 0.5, ["a couple of"] = 2, ["a few"] = 3,
        ["a quarter of a"] = 0.25, ["quarter of a"] = 0.25,
    };

    static readonly string[] Hours = { "twelve", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven" };

    static readonly Regex Amount = new(
        @"^(?<n>\d+(?:[.,]\d+)?|a quarter of a|quarter of a|a couple of|a few|half a|half|an?|one|two|three|four|five|six|seven|eight|nine|ten)\s*" +
        @"(?<u>metres?|meters?|m|centimetres?|centimeters?|cm|feet|foot|ft)?\b\s*",
        RegexOptions.Compiled);

    /// <summary>The words as a spot, or the reason they couldn't be read (said to the person as it is).</summary>
    public static (Spot? Spot, string? Error) Parse(string text)
    {
        var t = text.Trim().ToLowerInvariant().TrimEnd('.', '!');
        t = Regex.Replace(t, @"^(?:put it|move it|place it|rez it|set it down|put|move|place)\s+", "");
        if (t.Length == 0) return (null, "Say where: a metre to my left, 2 metres ahead, here.");
        if (t is "here" or "at my feet" or "where i am" or "where i'm standing" or "where i stand" or "on the ground here")
            return (new Spot(0, 0, 0, 0, 0, true, false), null);
        bool relative = false;
        if (t.StartsWith("by ")) { relative = true; t = t[3..]; }
        double ahead = 0, right = 0, north = 0, east = 0, up = 0;
        bool any = false;
        // the last clause's direction, when it came without a distance ("at two o'clock, 3 metres": the amount after it)
        (double A, double R, double N, double E, double U)? bare = null;
        foreach (var raw in Regex.Split(t, @"\s*(?:,|\band then\b|\band\b|\bthen\b)\s*"))
        {
            var c = raw.Trim();
            if (c.Length == 0 || c is "on the ground" or "on the floor" or "down on the ground") continue;
            if (c is "in front of me" or "in front") { ahead += 1; any = true; bare = (1, 0, 0, 0, 0); continue; }
            if (c is "behind me") { ahead -= 1; any = true; bare = (-1, 0, 0, 0, 0); continue; }
            double d = 1;
            bool given = false;
            var m = Amount.Match(c);
            if (m.Success && m.Length > 0 && (m.Groups["u"].Success || !Regex.IsMatch(c[m.Length..], @"^o'?\s*clock")))
            {
                var n = m.Groups["n"].Value;
                d = Numbers.TryGetValue(n, out var w) ? w : double.Parse(n.Replace(',', '.'), CultureInfo.InvariantCulture);
                d *= m.Groups["u"].Value switch
                {
                    "cm" or "centimetre" or "centimetres" or "centimeter" or "centimeters" => 0.01,
                    "feet" or "foot" or "ft" => 0.3048,
                    _ => 1,
                };
                c = c[m.Length..].Trim();
                given = true;
            }
            if (d > 64) return (null, $"{raw.Trim()}: that's too far (64 metres at most).");
            c = Regex.Replace(c, @"^(?:to the|to my|to your|on my|on the|my|of me|at|toward(?:s)? the|toward(?:s)?|towards my)\s+", "");
            c = Regex.Replace(c, @"\s+(?:of me|from me|of here|side|hand side)$", "").Trim();
            if (c.Length == 0)
            {
                if (!given) continue;
                // an amount alone: the distance for the direction said just before it
                if (bare is not { } b) return (null, $"{raw.Trim()}: which way? Ahead, back, left, right, up, down, a clock face or the compass.");
                ahead += b.A * (d - 1); right += b.R * (d - 1); north += b.N * (d - 1); east += b.E * (d - 1); up += b.U * (d - 1);
                bare = null;
                continue;
            }
            (double A, double R, double N, double E, double U) unit;
            switch (c)
            {
                case "ahead" or "forward" or "forwards" or "front" or "in front" or "straight ahead" or "further": unit = (1, 0, 0, 0, 0); break;
                case "back" or "backward" or "backwards" or "behind" or "behind me" or "nearer" or "closer": unit = (-1, 0, 0, 0, 0); break;
                case "left": unit = (0, -1, 0, 0, 0); break;
                case "right": unit = (0, 1, 0, 0, 0); break;
                case "up" or "higher" or "above" or "up in the air" or "high": unit = (0, 0, 0, 0, 1); break;
                case "down" or "lower" or "below": unit = (0, 0, 0, 0, -1); break;
                default:
                    var clock = Regex.Match(c, @"^(?<h>\d{1,2}|" + string.Join("|", Hours) + @")\s*o'?\s*clock$");
                    if (clock.Success)
                    {
                        var hv = clock.Groups["h"].Value;
                        int hour = int.TryParse(hv, out var hn) ? hn % 12 : Array.IndexOf(Hours, hv);
                        double rad = hour * 30 * Math.PI / 180;
                        unit = (Math.Cos(rad), Math.Sin(rad), 0, 0, 0);
                        break;
                    }
                    if (Bearings.ParsePoint(c) is { } compass)
                    {
                        double rad = compass * Math.PI / 180;
                        unit = (0, 0, Math.Cos(rad), Math.Sin(rad), 0);
                        break;
                    }
                    return (null, $"{raw.Trim()}: not a direction I know. Ahead, back, left, right, up, down, two o'clock, north.");
            }
            ahead += unit.A * d; right += unit.R * d; north += unit.N * d; east += unit.E * d; up += unit.U * d;
            bare = given ? null : unit;
            any = true;
        }
        if (!any) return (null, "Say where: a metre to my left, 2 metres ahead, here.");
        return (new Spot(Math.Round(ahead, 3), Math.Round(right, 3), Math.Round(north, 3), Math.Round(east, 3), Math.Round(up, 3), false, relative), null);
    }

    /// <summary>The spot said back in the person's terms ("1 metre to your left, half a metre up").</summary>
    public static string Say(Spot s)
    {
        if (s.Here) return "at your feet";
        var parts = new List<string>();
        static string M(double m) => m < 1 && m > 0 ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(m * 100)} centimetres") : Bearings.MetresTenths(m);
        if (s.Ahead != 0) parts.Add(s.Ahead > 0 ? $"{M(s.Ahead)} ahead" : $"{M(-s.Ahead)} back");
        if (s.Right != 0) parts.Add(s.Right > 0 ? $"{M(s.Right)} to your right" : $"{M(-s.Right)} to your left");
        if (s.North != 0) parts.Add(s.North > 0 ? $"{M(s.North)} north" : $"{M(-s.North)} south");
        if (s.East != 0) parts.Add(s.East > 0 ? $"{M(s.East)} east" : $"{M(-s.East)} west");
        if (s.Up != 0) parts.Add(s.Up > 0 ? $"{M(s.Up)} up" : $"{M(-s.Up)} down");
        return (s.Relative ? "moved " : "") + string.Join(", ", parts);
    }
}
