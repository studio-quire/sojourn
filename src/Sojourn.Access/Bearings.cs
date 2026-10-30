using System.Globalization;
using System.Text.RegularExpressions;

namespace Sojourn.Access;

/// <summary>
/// Where things are, in words relative to the person: distance in metres, a clock face from the avatar's facing
/// ("two o'clock"), height when it matters, the compass on asking. Compass bearings here are 0 = north, clockwise;
/// Second Life's own degrees (as Radegast's //face and //status, and ScriptAble's Guide) are 0 = east, counter-clockwise.
/// </summary>
public static class Bearings
{
    static readonly string[] Hours = { "twelve", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven" };

    static readonly string[] Points =
    {
        "north", "north-northeast", "northeast", "east-northeast", "east", "east-southeast", "southeast", "south-southeast",
        "south", "south-southwest", "southwest", "west-southwest", "west", "west-northwest", "northwest", "north-northwest",
    };

    static readonly string[] Abbrev = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };

    public static double Norm(double deg) => (deg % 360 + 360) % 360;

    /// <summary>The compass bearing from one point to another on the ground (0 = north, +Y; 90 = east, +X).</summary>
    public static double Compass(double fromX, double fromY, double toX, double toY) =>
        Norm(90 - Math.Atan2(toY - fromY, toX - fromX) * 180 / Math.PI);

    /// <summary>A clock face for a bearing relative to the facing: 0 is twelve o'clock, 90 three o'clock.</summary>
    public static string Clock(double relative) => $"{Hours[(int)Math.Round(Norm(relative) / 30) % 12]} o'clock";

    public static string Point(double compass) => Points[(int)Math.Round(Norm(compass) / 22.5) % 16];

    /// <summary>A compass word or abbreviation (N, NNE, north, north-east, northeast...) as a bearing, or null.</summary>
    public static double? ParsePoint(string word)
    {
        var w = word.Trim().ToLowerInvariant().Replace(" ", "-");
        for (int i = 0; i < 16; i++)
            if (w == Abbrev[i].ToLowerInvariant() || w == Points[i] || w == Points[i].Replace("-", "") || w == Spelled(Points[i])) return i * 22.5;
        return null;
    }

    // "north-east" as well as "northeast"
    static string Spelled(string p) => Regex.Replace(p, "^(north|south)(east|west)$", "$1-$2");

    /// <summary>Second Life's degrees (0 = east, counter-clockwise) to a compass bearing and back.</summary>
    public static double FromSl(double sl) => Norm(90 - sl);
    public static int ToSl(double compass) => (int)Math.Round(Norm(90 - compass)) % 360;

    /// <summary>A distance for speech: "under a metre", "1 metre", "5 metres" (a screen reader reads "5 m" as "five M").</summary>
    public static string Metres(double m) => m < 1 ? "under a metre" : Words(Math.Round(m));

    /// <summary>To a tenth: "1.4 metres", "2 metres".</summary>
    public static string MetresTenths(double m) => Words(Math.Round(m, 1));

    static string Words(double m) => m == 1 ? "1 metre" : string.Create(CultureInfo.InvariantCulture, $"{m:0.#} metres");

    /// <summary>"4 metres, two o'clock", with height where it's more than a couple of metres.</summary>
    public static string Relative(double dx, double dy, double dz, double heading)
    {
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double flat = Math.Sqrt(dx * dx + dy * dy);
        var s = flat < 0.5 ? Metres(d) + (dz > 0 ? ", above you" : ", below you") : $"{Metres(d)}, {Clock(Compass(0, 0, dx, dy) - heading)}";
        if (flat >= 0.5 && Math.Abs(dz) >= 2) s += string.Create(CultureInfo.InvariantCulture, $", {Metres(Math.Abs(dz))} {(dz > 0 ? "above" : "below")}");
        return s;
    }

    /// <summary>The longer form for //find: clock face, compass point and level.</summary>
    public static string Full(double dx, double dy, double dz, double heading)
    {
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double flat = Math.Sqrt(dx * dx + dy * dy);
        if (flat < 0.5) return $"{Metres(d)} {(dz > 0 ? "above" : "below")} you";
        double compass = Compass(0, 0, dx, dy);
        var level = Math.Abs(dz) < 2 ? "at your level" : $"{Metres(Math.Abs(dz))} {(dz > 0 ? "above" : "below")} you";
        return $"{Metres(d)} away, {Clock(compass - heading)}, to the {Point(compass)}, {level}";
    }
}

/// <summary>A place to go: a region and a point in it.</summary>
public sealed record Place(string Region, double X, double Y, double Z)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Region} {Math.Round(X):0}, {Math.Round(Y):0}, {Math.Round(Z):0}");
}

public static class Places
{
    static readonly Regex Slurl = new(
        @"^(?:secondlife:///app/(?:teleport|region)/|secondlife://(?:/)?|https?://(?:maps|slurl)\.secondlife\.com/secondlife/|https?://slurl\.com/secondlife/|hop://[^/]+/)?" +
        @"(?<region>[^/]+?)(?:/(?<x>-?[\d.]+)(?:/(?<y>-?[\d.]+)(?:/(?<z>-?[\d.]+))?)?)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>An SLurl (secondlife://, maps.secondlife.com, hop://), "Region/x/y/z", or "uri:Region&amp;x&amp;y&amp;z";
    /// coordinates default to the middle of the region. Null when it isn't one.</summary>
    public static Place? Parse(string text)
    {
        var t = text.Trim().Trim('"', '<', '>');
        if (t.Length == 0 || t.Contains('\n')) return null;
        if (t.StartsWith("uri:", StringComparison.OrdinalIgnoreCase))
        {
            var p = t[4..].Split('&');
            return new Place(p[0], N(p.ElementAtOrDefault(1), 128), N(p.ElementAtOrDefault(2), 128), N(p.ElementAtOrDefault(3), 30));
        }
        bool linked = t.Contains("://");
        var m = Slurl.Match(t);
        if (!m.Success) return null;
        var region = Uri.UnescapeDataString(m.Groups["region"].Value.Replace('+', ' ')).Trim();
        // a bare word is only a place when it carries coordinates (a region name alone could be anything typed)
        if (region.Length == 0 || (!linked && !m.Groups["x"].Success)) return null;
        return new Place(region, N(m.Groups["x"].Value, 128), N(m.Groups["y"].Value, 128), N(m.Groups["z"].Value, 30));
    }

    static double N(string? s, double dflt) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : dflt;
}
