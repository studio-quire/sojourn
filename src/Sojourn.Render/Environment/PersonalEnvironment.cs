using System.Collections.Concurrent;

namespace Sojourn.Render.Environment;

/// <summary>
/// The person's own environment over the place's: the viewer's ENV_LOCAL (World > Environment's fixed skies, "Use
/// shared environment", My Environments' "Apply only to myself"), the same over every region and seen only by them.
/// A sky, a water or a day cycle is applied as LLEnvironment::setEnvironment(ENV_LOCAL, settings) applies it: a day
/// replaces everything; a fixed sky or water replaces that part, the other part kept from what was there, or (nothing
/// local yet) frozen as it's shown at that moment, as the viewer copies the current environment's. Changes are asked
/// for from any thread and take effect on the render thread's next frame, instantly (the viewer's TRANSITION_INSTANT
/// for these). Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class PersonalEnvironment
{
    /// <summary>What's applied: each part's name and where it came from ("preset", "item"), null where the place's own
    /// shows; and whether the clouds are paused.</summary>
    public sealed record Status(string? Sky, string? Water, string? Day, string? From, bool CloudsPaused, long Version);

    readonly ConcurrentQueue<Action<EnvironmentSnapshot?, EnvironmentState?>> _asked = new();
    EnvironmentLayer? _layer;
    string? _sky, _water, _day, _from;
    long _version;
    volatile bool _cloudsPaused;

    /// <summary>The state as of the last frame (any thread).</summary>
    public Status Current { get; private set; } = new(null, null, null, null, false, 0);

    /// <summary>Clouds held still (the viewer's World > Environment > Pause Clouds; not kept between sessions, as
    /// there).</summary>
    public bool CloudsPaused
    {
        get => _cloudsPaused;
        set { _cloudsPaused = value; Current = Current with { CloudsPaused = value }; }
    }

    public void ApplySky(SkySettings sky, string from) => _asked.Enqueue((shown, _) =>
    {
        bool keepWater = _layer is { Day: not null } || _layer?.FixedWater != null;
        var water = _layer?.FixedWater ?? (_layer?.Day == null ? Frozen(shown?.Water, shown?.WaterSource) : null);
        _layer = new EnvironmentLayer
        {
            Day = _layer?.Day, FixedSky = sky, FixedWater = water,
            DayLength = _layer?.DayLength ?? DayCycle.DefaultDayLength, DayOffset = _layer?.DayOffset ?? DayCycle.DefaultDayOffset,
        };
        (_sky, _from) = (Named(sky.Name, "sky"), from);
        if (!keepWater) _water = water != null ? "as it was" : null;
        (_scrub, _baseName) = (null, null);
        Changed();
    });

    public void ApplyWater(WaterSettings water, string from) => _asked.Enqueue((shown, _) =>
    {
        bool keepSky = _layer is { Day: not null } || _layer?.FixedSky != null;
        var sky = _layer?.FixedSky ?? (_layer?.Day == null ? Frozen(shown?.Sky, shown?.SkySource) : null);
        _layer = new EnvironmentLayer
        {
            Day = _layer?.Day, FixedSky = sky, FixedWater = water,
            DayLength = _layer?.DayLength ?? DayCycle.DefaultDayLength, DayOffset = _layer?.DayOffset ?? DayCycle.DefaultDayOffset,
        };
        (_water, _from) = (Named(water.Name, "water"), from);
        if (!keepSky) _sky = sky != null ? "as it was" : null;
        if (_scrub != null) _scrub = _scrub with { OwnWater = false };
        Changed();
    });

    /// <summary>A day cycle at the default length and offset (or those of the day it replaces), as the viewer applies
    /// one locally.</summary>
    public void ApplyDay(DayCycle day, string name, string from) => _asked.Enqueue((_, _) =>
    {
        _layer = EnvironmentLayer.FromDay(day, _layer?.DayLength ?? DayCycle.DefaultDayLength, _layer?.DayOffset ?? DayCycle.DefaultDayOffset);
        (_day, _sky, _water, _from) = (Named(name, "day cycle"), null, null, from);
        (_scrub, _baseName) = (null, null);
        Changed();
    });

    /// <summary>Back to the shared environment (the place's own).</summary>
    public void Clear() => _asked.Enqueue((_, _) =>
    {
        (_scrub, _baseName) = (null, null);
        if (_layer == null) return;
        _layer = null;
        (_sky, _water, _day, _from) = (null, null, null, null);
        Changed();
    });

    /// <summary>A change of the time of day (<see cref="SetTime"/>): <paramref name="Hours"/> (0-24, 6 sunrise, 12 noon,
    /// 18 sunset), or <paramref name="Later"/> hours from now (negative: earlier), or the sun (and moon) by the viewer's
    /// azimuth and elevation in degrees; <paramref name="By"/> "sun" or "day" (null: what fits what's shown);
    /// <paramref name="Run"/> the day cycle going on from there (null: as it was).</summary>
    public sealed record TimeAsk(double? Hours = null, double? Later = null, (double Azimuth, double Elevation)? Sun = null,
        (double Azimuth, double Elevation)? Moon = null, string? By = null, bool? Run = null);

    /// <summary>What a time change did: the sun's time now, how ("sun" or "day"), the sky's name, whether a day cycle
    /// runs on, and a note when it couldn't do quite what was asked.</summary>
    public sealed record TimeDone(double Hours, string By, string Name, bool Running, string? Note);

    /// <summary>The day cycle a time change scrubbed (held at Position, or Running on from where it was put): the
    /// layer it came from, its name, the sky track, and whether the water is the day's too.</summary>
    sealed record Scrub(EnvironmentLayer Source, string Name, int Track, float Position, bool OwnWater, bool Running);

    Scrub? _scrub;
    string? _baseName;   // the sky the sun was moved on, for naming it again after another move

    /// <summary>
    /// The time of day, as the viewer's Personal Lighting and day cycle editor set it, over whatever is shown:
    /// - by sun (Personal Lighting): the sky as shown now, frozen, with only the sun and moon moved (to a time of day on
    ///   its own path, the moon opposite; or by azimuth and elevation). Its colours stay; the default for a fixed sky (a
    ///   preset, an item, a sun already moved).
    /// - by day (the editor's time scrubber): the day cycle shown (the person's own, the parcel's, the region's; for a
    ///   fixed sky, the place's) at the position where its sun stands at that time, colours and all; the default when a
    ///   day is showing. Held still, unless Run (or a day of their own already running): then the day goes on from there.
    /// Applied on the render thread's next frame; instant, as the viewer's.
    /// </summary>
    public Task<TimeDone> SetTime(TimeAsk ask)
    {
        var done = new TaskCompletionSource<TimeDone>(TaskCreationOptions.RunContinuationsAsynchronously);
        _asked.Enqueue((shown, place) =>
        {
            try { done.SetResult(Time(ask, shown, place)); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    TimeDone Time(TimeAsk ask, EnvironmentSnapshot? shown, EnvironmentState? place)
    {
        if (shown == null) throw new InvalidOperationException("no frame drawn yet");
        if (ask.Hours is { } given && (double.IsNaN(given) || given < 0 || given > 24)) throw new ArgumentException("hours go from 0 to 24");
        bool sunGiven = ask.Sun != null || ask.Moon != null;
        if (ask.Hours == null && ask.Later == null && !sunGiven) throw new ArgumentException("give hours, later, or the sun's azimuth and elevation");
        bool dayRuns = _layer is { Day: not null, FixedSky: null };
        string by = ask.By?.Trim().ToLowerInvariant() ?? (sunGiven ? "sun"
            : ask.Run == true || _scrub != null || dayRuns ? "day"
            : _layer?.FixedSky != null ? "sun"
            : shown.SkySample != null ? "day" : "sun");
        if (by == "sun") return BySun(ask, shown);
        if (by != "day") throw new ArgumentException("by is sun or day");
        if (sunGiven) throw new ArgumentException("the sun's azimuth and elevation go with by sun");
        return ByDay(ask, shown, place, dayRuns);
    }

    TimeDone BySun(TimeAsk ask, EnvironmentSnapshot shown)
    {
        if (ask.Run == true) throw new ArgumentException("by sun holds the sky still; a day cycle runs by day");
        var sky = (_layer?.FixedSky ?? shown.Sky).Clone();
        _baseName ??= _layer?.FixedSky != null ? _sky ?? Named(sky.Name, "sky") : ShownName(shown, "sky");
        var dir = sky.SunDirection;
        if (ask.Hours != null || ask.Later != null)
        {
            double hours = ask.Hours ?? TimeOfDay.HoursOf(dir) + ask.Later!.Value;
            (sky.SunRotation, sky.MoonRotation) = TimeOfDay.AtHours(hours, TimeOfDay.EastAngle(dir));
        }
        if (ask.Sun is { } sun) sky.SunRotation = TimeOfDay.FromAzimuthElevation(sun.Azimuth, sun.Elevation);
        if (ask.Moon is { } moon) sky.MoonRotation = TimeOfDay.FromAzimuthElevation(moon.Azimuth, moon.Elevation);
        var water = _layer?.FixedWater ?? Frozen(shown.Water, shown.WaterSource);
        bool waterWas = _layer?.FixedWater != null;
        _layer = new EnvironmentLayer { FixedSky = sky, FixedWater = water };
        double now = TimeOfDay.HoursOf(sky.SunDirection);
        var name = $"{_baseName}, sun at {TimeOfDay.Clock(now)}";
        (_sky, _day, _from, _scrub) = (name, null, "time", null);
        if (!waterWas) _water = water != null ? "as it was" : null;
        Changed();
        return new TimeDone(now, "sun", name, false, null);
    }

    TimeDone ByDay(TimeAsk ask, EnvironmentSnapshot shown, EnvironmentState? place, bool dayRuns)
    {
        // the day to scrub and where on it things stand now
        EnvironmentLayer source;
        string name;
        int track;
        float at;
        if (_scrub is { } s)
        {
            (source, name, track) = (s.Source, s.Name, s.Track);
            at = s.Running && _layer is { Day: not null } running ? DayCycle.TimeToPosition(shown.UnixSeconds + running.DayOffset, running.DayLength) : s.Position;
        }
        else
        {
            if (dayRuns) (source, name) = (_layer!, _day ?? "your day cycle");
            else if (shown.SkyLayer is { Day: not null, FixedSky: null } seen) (source, name) = (seen, ShownName(shown, "day"));
            else
            {
                EnvironmentLayer? parcel = null, region = null;
                if (place != null) lock (place) (parcel, region) = (place.Parcel, place.Region);
                (source, name) = parcel?.Day != null ? (parcel, "the parcel's day")
                    : region?.Day != null ? (region, "the region's day")
                    : (EnvironmentLayer.FromDay(EnvironmentState.DefaultRegionDay), "the default day");
            }
            track = ReferenceEquals(source, shown.SkyLayer) ? shown.SkyTrack : source.Day!.SelectSkyTrack(shown.RequestedSkyTrack);
            at = DayCycle.TimeToPosition(shown.UnixSeconds + source.DayOffset, source.DayLength);
        }
        var day = source.Day!;

        // where its sun stands at the time asked (later: from where it stands now), nearest where things are; else by the
        // day's own clock (a day whose sun stays put)
        string? note = null;
        double? sunNow = day.SunRotationAt(track, at) is { } q0 ? TimeOfDay.HoursOf(EnvMath.Rotate(System.Numerics.Vector3.UnitX, q0)) : null;
        double target = ask.Hours ?? (sunNow ?? at * 24) + ask.Later!.Value;
        float to;
        if (TimeOfDay.DayPositionFor(day, track, target, at) is { } found) to = found;
        else
        {
            float p = ask.Hours is { } hours ? (float)(hours / 24) : at + (float)(ask.Later!.Value / 24);
            to = p - MathF.Floor(p);
            note = "this day's sun doesn't pass there, so it went by the day's own clock";
        }

        bool run = ask.Run ?? (dayRuns || _scrub is { Running: true });
        bool ownWater = _scrub?.OwnWater ?? _layer?.FixedWater == null;
        var keptWater = ownWater ? null : _layer?.FixedWater;
        string keptWaterName = _water ?? "as it was";
        double sunAt = day.SunRotationAt(track, to) is { } q ? TimeOfDay.HoursOf(EnvMath.Rotate(System.Numerics.Vector3.UnitX, q)) : to * 24;
        string shownName;
        if (run)
        {
            double length = source.DayLength;
            double offset = to * length - shown.UnixSeconds % length;
            offset = (offset % length + length) % length;
            _layer = new EnvironmentLayer { Day = day, DayLength = length, DayOffset = offset, FixedWater = keptWater };
            shownName = $"{name} from {TimeOfDay.Clock(sunAt)}";
            (_day, _sky, _water) = (shownName, null, keptWater != null ? keptWaterName : null);
        }
        else
        {
            var sky = day.EvaluateSky(track, to, 1.0, out _) ?? shown.Sky.Clone();
            var water = keptWater ?? day.EvaluateWater(to, 1.0, out _) ?? shown.Water.Clone();
            _layer = new EnvironmentLayer { FixedSky = sky, FixedWater = water };
            shownName = $"{name} at {TimeOfDay.Clock(sunAt)}";
            (_day, _sky, _water) = (null, shownName, keptWater != null ? keptWaterName : shownName);
        }
        (_scrub, _baseName, _from) = (new Scrub(source, name, track, to, keptWater == null, run), null, "time");
        Changed();
        return new TimeDone(sunAt, "day", shownName, run, note);
    }

    /// <summary>What's shown, named by whose it is ("the region's sky").</summary>
    string ShownName(EnvironmentSnapshot shown, string what) => shown.SkySource switch
    {
        EnvironmentSource.Region => $"the region's {what}",
        EnvironmentSource.Parcel => $"the parcel's {what}",
        EnvironmentSource.Local => _day ?? _sky ?? $"your {what}",
        _ => $"the default {what}",
    };

    /// <summary>Render thread, once a frame before evaluating: what was asked for since, against what was last shown
    /// and the place's own environment (<paramref name="place"/>, for a day to scrub under a fixed sky); the layer to
    /// evaluate with and its version (for keying what's captured under it: reflection probes).</summary>
    public (EnvironmentLayer? Layer, long Version) Update(EnvironmentSnapshot? shown, EnvironmentState? place = null)
    {
        // one change a frame: what's asked after a change reads what was shown, so it waits for a frame drawn under that
        // change (a time asked right after "shared" goes by the place's day, not the sky just dropped)
        long start = _version;
        for (int n = _next.Count; n > 0 && _version == start; n--) _next.Dequeue()(shown, place);
        while (_version == start && _asked.TryDequeue(out var a)) a(shown, place);
        if (_version != start) while (_asked.TryDequeue(out var later)) _next.Enqueue(later);
        return (_layer, _version);
    }

    readonly Queue<Action<EnvironmentSnapshot?, EnvironmentState?>> _next = new();   // render thread only

    /// <summary>A part of what's shown, frozen, unless it's the built-in default (nothing loaded yet: that part then
    /// keeps following the place's).</summary>
    static T? Frozen<T>(T? shown, EnvironmentSource? source) where T : class =>
        shown != null && source is not (null or EnvironmentSource.BuiltinDefault) ? shown : null;

    static string Named(string name, string kind) => string.IsNullOrWhiteSpace(name) ? $"unnamed {kind}" : name;

    void Changed()
    {
        _version++;
        if (_layer?.FixedSky == null && _layer?.Day == null) _sky = null;
        if (_layer?.FixedWater == null && _layer?.Day == null) _water = null;
        Current = new Status(_sky, _water, _day, _from, _cloudsPaused, _version);
    }
}
