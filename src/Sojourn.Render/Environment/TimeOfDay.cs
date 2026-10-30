using System.Numerics;

namespace Sojourn.Render.Environment;

/// <summary>
/// The time of day a sky's sun stands at, and the sun and moon placed at one, for the person's own lighting
/// (PersonalEnvironment.SetTime; the viewer's Personal Lighting and the day cycle editor's scrubber).
/// Hours read as WindLight's time of day and SL's default day cycle have them: the sun on a great circle through the
/// zenith, rising at 6 over the eastern horizon, highest at 12, setting at 18 in the west, lowest at 0, the moon opposite
/// (llsettingssky.cpp convertFromLegacy: sun_angle along east_angle, the moon at azimuth + pi, -altitude; the default
/// day's keyframes are exactly this with east angle 0). A sky's path keeps its own east angle (a sun off the east-west
/// line stays on its side). Azimuth and elevation are the viewer's Personal Lighting fields
/// (LLVirtualTrackball::getAzimuthAndElevation, LLFloaterEnvironmentAdjust::onSunAzimElevChanged): azimuth 0 east, 90
/// south, 180 west, 270 north, elevation above the horizon. Claude Opus 5.5, 2026-10-29.
/// </summary>
public static class TimeOfDay
{
    const double Deg = Math.PI / 180;

    /// <summary>Hours 0-24 from where the sun stands (SL region space: +X east, +Y north, +Z up).</summary>
    public static double HoursOf(Vector3 sun)
    {
        double r = Math.Sqrt(sun.X * (double)sun.X + sun.Y * (double)sun.Y);
        double along = sun.X >= 0 || r < 1e-6 ? r : -r;   // the rising (eastern) side, or the setting side
        double theta = Math.Atan2(sun.Z, along) / Deg;     // degrees along the path from the rising horizon
        if (theta < 0) theta += 360;
        return Wrap(6 + theta / 15);
    }

    /// <summary>The path's east angle (radians, counter-clockwise from east): the horizontal direction the sun rises
    /// toward, folded into the eastern half; 0 for a sun straight up or down.</summary>
    public static float EastAngle(Vector3 sun)
    {
        if (sun.X * sun.X + sun.Y * sun.Y < 1e-8f) return 0;
        return sun.X >= 0 ? MathF.Atan2(sun.Y, sun.X) : MathF.Atan2(-sun.Y, -sun.X);
    }

    /// <summary>The sun and moon at <paramref name="hours"/> on a path at <paramref name="eastAngle"/>: turned about
    /// north-south from the rising point, then about up, so with east angle 0 they're the default day's own rotations.</summary>
    public static (Quaternion Sun, Quaternion Moon) AtHours(double hours, float eastAngle)
    {
        float theta = (float)((hours - 6) * 15 * Deg);
        var az = EnvMath.AngleAxis(eastAngle, Vector3.UnitZ);
        return (Quaternion.Concatenate(EnvMath.AngleAxis(-theta, Vector3.UnitY), az),
                Quaternion.Concatenate(EnvMath.AngleAxis(-theta - MathF.PI, Vector3.UnitY), az));
    }

    /// <summary>The viewer's Personal Lighting azimuth and elevation (degrees) as a sun or moon rotation.</summary>
    public static Quaternion FromAzimuthElevation(double azimuth, double elevation)
    {
        float e = (float)(elevation * Deg);
        if (MathF.Abs(e) < 1e-6f) e = 1e-6f;   // as the viewer's (F_APPROXIMATELY_ZERO)
        return Quaternion.Concatenate(EnvMath.AngleAxis(-e, Vector3.UnitY), EnvMath.AngleAxis((float)(2 * Math.PI - azimuth * Deg), Vector3.UnitZ));
    }

    /// <summary>A direction's azimuth and elevation (degrees) as the viewer's Personal Lighting shows them.</summary>
    public static (double Azimuth, double Elevation) AzimuthElevationOf(Vector3 dir)
    {
        double az = Math.Abs(dir.X) > 1e-6 || Math.Abs(dir.Y) > 1e-6 ? Math.Atan2(dir.X, dir.Y) : 0;
        az -= Math.PI / 2;
        if (az < 0) az += 2 * Math.PI;
        return (az / Deg, Math.Asin(Math.Clamp(dir.Z, -1f, 1f)) / Deg);
    }

    /// <summary>Hours wrapped into [0, 24).</summary>
    public static double Wrap(double hours) => ((hours % 24) + 24) % 24;

    /// <summary>The shorter way round between two times of day, in hours (0-12).</summary>
    public static double Apart(double a, double b)
    {
        double d = Math.Abs(Wrap(a) - Wrap(b));
        return Math.Min(d, 24 - d);
    }

    /// <summary>"7:05" for 7.08 hours.</summary>
    public static string Clock(double hours)
    {
        int minutes = (int)Math.Round(Wrap(hours) * 60) % (24 * 60);
        return $"{minutes / 60}:{minutes % 60:00}";
    }

    /// <summary>Where on a day cycle's track its sun stands at <paramref name="hours"/> (sampled every minute of a day
    /// read as 24 hours; of several such places, the one nearest <paramref name="near"/>), or null when its sun never
    /// comes within half an hour of it (a day whose sun stays put, or keeps to another path).</summary>
    public static float? DayPositionFor(DayCycle day, int track, double hours, float near)
    {
        const int Samples = 1440;
        const double Slack = 2.0 / 60;
        var apart = new double[Samples];
        double best = double.MaxValue;
        for (int i = 0; i < Samples; i++)
        {
            if (day.SunRotationAt(track, i / (float)Samples) is not { } q) return null;
            apart[i] = Apart(HoursOf(EnvMath.Rotate(Vector3.UnitX, q)), hours);
            best = Math.Min(best, apart[i]);
        }
        if (best > 0.5) return null;
        // the nearest place to `near` where the sun passes close, then the closest sample around it
        int pick = -1;
        float pickAway = float.MaxValue;
        for (int i = 0; i < Samples; i++)
        {
            if (apart[i] > best + Slack) continue;
            float away = MathF.Abs(i / (float)Samples - near);
            away = MathF.Min(away, 1 - away);
            if (away < pickAway) (pick, pickAway) = (i, away);
        }
        int closest = pick;
        for (int k = -30; k <= 30; k++)
        {
            int i = ((pick + k) % Samples + Samples) % Samples;
            if (apart[i] < apart[closest]) closest = i;
        }
        return closest / (float)Samples;
    }
}
