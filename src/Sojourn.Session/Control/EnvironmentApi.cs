using System.Numerics;
using System.Text.Json.Nodes;
using LibreMetaverse.StructuredData;
using UUID = LibreMetaverse.UUID;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Render.Environment;
using Sojourn.Render.Realtime;

namespace Sojourn.Session.Control;

/// <summary>
/// SojournEnvironment: the sky the view is drawn with right now (where it comes from: the region's day, the parcel's
/// own, the person's; the settings at this moment and altitude, what the renderer made of them, and the haze they give,
/// so a place's own look can be told from ours), and the person's own environment over every place's, as the viewer's
/// World > Environment and My Environments set it (Render/Environment/PersonalEnvironment.cs): the fixed skies, a sky,
/// water or day cycle from their inventory or the Library, the time of day over any of them (Personal Lighting's sun and
/// moon, or the day cycle at a time: PersonalEnvironment.SetTime), back to the shared one, clouds paused. Seen only by
/// them. Claude Opus 5.5, 2026-10-28 (the personal environment: batch B3a); the time of day 2026-10-29.
/// </summary>
public sealed class EnvironmentApi : LeapApi
{
    readonly FrameRenderer _renderer;
    readonly EnvironmentOps _ops;
    readonly Action<string>? _setSky;

    public override string Name => "SojournEnvironment";
    public override string Desc => "the sky and haze the view is drawn with, and the person's own environment: fixed skies, "
        + "skies, waters and day cycles from inventory or the Library, the time of day (Personal Lighting's sun and moon, or a "
        + "day cycle at a time), back to the shared one, clouds paused";
    public override bool NeedsSession => false;

    /// <summary>The viewer's World > Environment presets: the `sky` setting's names (and legacy noon, op only).</summary>
    public static readonly (string Name, UUID Id)[] Presets =
    {
        ("sunrise", DefaultSettingsAssets.SunriseId), ("noon", DefaultSettingsAssets.MiddayId), ("legacy noon", DefaultSettingsAssets.LegacyMiddayId),
        ("sunset", DefaultSettingsAssets.SunsetId), ("midnight", DefaultSettingsAssets.MidnightId),
    };

    /// <param name="setSky">Puts the `sky` setting to a preset's name ("region" for the shared environment), so the
    /// menu's row agrees.</param>
    public EnvironmentApi(AgentSession s, FrameRenderer renderer, Action<string>? setSky = null) : base(s)
    {
        _renderer = renderer;
        _ops = new EnvironmentOps(s);
        _setSky = setSky;
        Op("get", "-> {source, parcel, track, sky: the EEP values in use, render: what the renderer made of them, haze: how much "
            + "of a surface shows at a distance and the colour far things fade to, personal: the person's own environment, if any}", _ => Get());
        Op("mine", "{kind?: sky|water|daycycle, name?: part of it, library?: true} -> {settings: [{item_id, name, kind, library}], "
            + "partial?: true}: the skies, waters and day cycles in the inventory and the Library (My Environments); partial while "
            + "some folders are still loading (asking again a few seconds later has more)", async (a, ct) =>
        {
            var (list, partial) = await _ops.ListAsync(a.Str("kind"), a.Str("name"), a.Bool("library") ?? true, ct);
            var o = new JsonObject { ["settings"] = list };
            if (partial) o["partial"] = true;
            return o;
        });
        Op("use", "{preset: sunrise|noon|legacy noon|sunset|midnight|shared} or {item_id | name} (a sky, water or day cycle from mine) "
            + "or {asset_id} -> {applied, kind}: the person's own environment, seen only by them (a sky or water replaces that part "
            + "and keeps the other; a day cycle replaces both; shared goes back to the place's own)", async (a, ct) => await UseAsync(a, ct));
        Op("shared", "-> {personal}: back to the place's own environment (World > Environment > Use Shared Environment)", _ =>
        {
            Shared();
            return new JsonObject { ["personal"] = "shared" };
        });
        Op("time", "{hours: 0-24 or \"H:MM\" (6 sunrise, 12 noon, 18 sunset, 0 midnight; golden hour about 7 or 17.3, blue "
            + "hour about 5.4 or 18.4) | later: hours (negative earlier) | sun_azimuth + sun_elevation, moon_azimuth + moon_elevation "
            + "(degrees, the viewer's Personal Lighting: azimuth 0 east, 90 south, 180 west, 270 north), by?: sun|day, run?: bool} -> "
            + "{hours, clock, by, sky or day, running, note?}: the person's own time of day over whatever sky is in use, seen only by "
            + "them, held still. by sun (World > Environment > Personal Lighting) moves only the sun and moon on the sky as it is, "
            + "its colours kept; by day shows the day cycle (theirs, else the parcel's or region's) where its sun stands at that "
            + "time, colours and all (the day cycle editor's scrubber). Left out, by is sun for a fixed sky (a preset, an item, a sun "
            + "already moved) and day while a day cycle shows. run: true lets the day cycle go on from there. shared goes back to "
            + "the place's own; use a preset again for its own sun", async (a, ct) => await TimeAsync(a, ct));
        Op("clouds", "{paused: bool} -> {paused}: hold the clouds still (World > Environment > Pause Clouds; until the client "
            + "restarts, as the viewer's)", a =>
        {
            _renderer.Personal.CloudsPaused = a.Bool("paused") ?? throw new LeapError("give paused: true or false");
            return new JsonObject { ["paused"] = _renderer.Personal.CloudsPaused };
        });
    }

    /// <summary>World > Environment's presets and Use Shared Environment, as UI.call World.EnvSettings takes them
    /// (sunrise, noon, legacy noon, sunset, midnight, region; pause_clouds toggles); null when done, else why not.</summary>
    public string? EnvSettings(string? parameter)
    {
        var p = (parameter ?? "").Trim().ToLowerInvariant().Replace('_', ' ');
        if (p == "pause clouds") { _renderer.Personal.CloudsPaused = !_renderer.Personal.CloudsPaused; return null; }
        if (p is "region" or "shared") { Shared(); return null; }
        if (Preset(p)) return null;
        return $"no environment \"{parameter}\": sunrise, noon, legacy noon, sunset, midnight, region or pause_clouds";
    }

    void Shared()
    {
        _renderer.Personal.Clear();
        _setSky?.Invoke("region");
    }

    bool Preset(string name)
    {
        var (n, id) = Presets.FirstOrDefault(x => x.Name == name);
        if (n == null || EnvironmentState.KnownSky(id) is not { } sky) return false;
        _renderer.Personal.ApplySky(sky, "preset");
        if (n != "legacy noon") _setSky?.Invoke(n);   // (the setting has no legacy noon; it keeps its value)
        return true;
    }

    async Task<JsonNode?> UseAsync(JsonObject a, CancellationToken ct)
    {
        if (a.Str("preset") is { } preset)
        {
            var p = preset.Trim().ToLowerInvariant().Replace('_', ' ');
            if (p is "shared" or "region") Shared();
            else if (!Preset(p)) throw new LeapError($"no preset \"{preset}\": sunrise, noon, legacy noon, sunset, midnight or shared");
            return new JsonObject { ["applied"] = p is "region" ? "shared" : p, ["kind"] = "preset" };
        }
        var (name, kind, data) = await _ops.FetchAsync(a.Uuid("item_id"), a.Str("name"), a.Uuid("asset_id"), ct);
        if (data.Length == 0) throw new LeapError($"{name}'s settings came back empty");
        if (SettingsLlsd.Parse(data) is not OSDMap map) throw new LeapError($"{name} isn't a settings asset");
        var problems = new List<string>();
        var type = map.ContainsKey("type") ? map["type"].AsString() : kind;
        if (a.Uuid("asset_id") != null && map.ContainsKey("name") && map["name"].AsString() is { Length: > 0 } own) name = own;
        switch (type)
        {
            case "sky":
                var sky = SkySettings.FromLlsd(map, problems) ?? throw new LeapError($"{name}: not a sky the client can read ({string.Join("; ", problems)})");
                if (string.IsNullOrEmpty(sky.Name)) sky.Name = name;
                _renderer.Personal.ApplySky(sky, "item");
                break;
            case "water":
                var water = WaterSettings.FromLlsd(map, problems) ?? throw new LeapError($"{name}: not a water the client can read ({string.Join("; ", problems)})");
                if (string.IsNullOrEmpty(water.Name)) water.Name = name;
                _renderer.Personal.ApplyWater(water, "item");
                break;
            case "daycycle":
                var day = DayCycle.FromLlsd(map, problems);
                if (!day.IsUsable) throw new LeapError($"{name}: a day cycle without a water or ground-level sky track ({string.Join("; ", problems)})");
                _renderer.Personal.ApplyDay(day, name, "item");
                break;
            default:
                throw new LeapError($"{name} is a \"{type}\" settings asset, not a sky, water or day cycle");
        }
        var o = new JsonObject { ["applied"] = name, ["kind"] = type };
        if (problems.Count > 0) o["notes"] = new JsonArray(problems.Select(x => (JsonNode)x).ToArray());
        return o;
    }

    async Task<JsonNode?> TimeAsync(JsonObject a, CancellationToken ct)
    {
        double? hours = null;
        if (a.Str("hours") is { } h) hours = ParseHours(h) ?? throw new LeapError($"hours \"{h}\": a number 0-24 or H:MM");
        (double, double)? Pair(string az, string el) => (a.Num(az), a.Num(el)) switch
        {
            (null, null) => null,
            ({ } x, { } y) => (x, y),
            _ => throw new LeapError($"give {az} and {el} together"),
        };
        var ask = new PersonalEnvironment.TimeAsk(hours, a.Num("later"), Pair("sun_azimuth", "sun_elevation"),
            Pair("moon_azimuth", "moon_elevation"), a.Str("by"), a.Bool("run"));
        var done = await SetTimeAsync(ask, ct);
        if (done == null) return new JsonObject { ["asked"] = true, ["note"] = "no frame drawn for a while: it takes effect with the next" };
        var o = new JsonObject
        {
            ["hours"] = Math.Round(done.Hours, 2), ["clock"] = TimeOfDay.Clock(done.Hours), ["by"] = done.By,
            [done.Running ? "day" : "sky"] = done.Name, ["running"] = done.Running,
        };
        if (done.Note != null) o["note"] = done.Note;
        return o;
    }

    /// <summary>A time change, waited for (null when no frame came to apply it within a few seconds: it's applied with
    /// the next); a refusal comes out as a LeapError.</summary>
    async Task<PersonalEnvironment.TimeDone?> SetTimeAsync(PersonalEnvironment.TimeAsk ask, CancellationToken ct)
    {
        var task = _renderer.Personal.SetTime(ask);
        try { return await task.WaitAsync(TimeSpan.FromSeconds(5), ct); }
        catch (TimeoutException) { return null; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { throw new LeapError(e.Message); }
    }

    /// <summary>The `time_of_day` setting's value: the sun's time as shown ("7:22").</summary>
    public string? TimeNow => _renderer.LastEnvironment is { } seen ? TimeOfDay.Clock(TimeOfDay.HoursOf(seen.Snapshot.Sky.SunDirection)) : null;

    /// <summary>The `time_of_day` setting changed (and the menu's row): "H:MM" or hours, "+h" / "-h" later or earlier,
    /// "region" (or "shared") back to the place's own; applied with the next frame. Null when done, else why not.</summary>
    public string? SetTimeOfDay(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v is "region" or "shared" or "place") { Shared(); return null; }
        PersonalEnvironment.TimeAsk ask;
        if (v.StartsWith('+') || v.StartsWith('-'))
        {
            if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var later))
                return "time_of_day: +h or -h for later or earlier";
            ask = new(Later: later);
        }
        else if (ParseHours(v) is { } hours) ask = new(Hours: hours);
        else return "time_of_day is H:MM or hours 0-24, +h / -h, or region";
        var task = _renderer.Personal.SetTime(ask);
        if (task.IsFaulted) return task.Exception!.InnerException!.Message;
        _ = task.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);   // (a refusal found on the render thread is only the menu's)
        return null;
    }

    /// <summary>Hours from "7.5", "7:30" or "19:05"; null if neither, or outside 0-24.</summary>
    static double? ParseHours(string s)
    {
        s = s.Trim();
        double h;
        if (s.Split(':') is [var hh, var mm] && int.TryParse(hh, out var hi) && int.TryParse(mm, out var mi) && mi is >= 0 and < 60) h = hi + mi / 60.0;
        else if (!double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out h)) return null;
        return h is >= 0 and <= 24 ? h : null;
    }

    JsonNode? Get()
    {
        if (_renderer.LastEnvironment is not { } seen) return new JsonObject { ["error"] = "no frame drawn yet" };
        var snap = seen.Snapshot;
        var sky = snap.Sky;
        var u = seen.Sky;
        static JsonArray V(Vector3 v) => new(R(v.X), R(v.Y), R(v.Z));
        static double R(float f) => Math.Round(f, 6);

        var haze = new JsonObject();
        foreach (int d in new[] { 10, 25, 50, 100, 200, 400 })
            haze[$"{d}m"] = R(Transmittance(sky, d).X);   // atten.r, the share of the surface left (hazeF's alpha)

        var personal = _renderer.Personal.Current;
        return new JsonObject
        {
            ["personal"] = personal.Sky == null && personal.Water == null && personal.Day == null ? null : new JsonObject
            {
                ["sky"] = personal.Sky, ["water"] = personal.Water, ["day"] = personal.Day, ["from"] = personal.From,
            },
            ["clouds_paused"] = personal.CloudsPaused,
            ["time"] = Time(sky),
            ["region"] = seen.Region.ToString(),
            ["position"] = V(seen.Position),
            ["parcel"] = seen.Parcel,
            ["sky_source"] = snap.SkySource.ToString().ToLowerInvariant(),
            ["water_source"] = snap.WaterSource.ToString().ToLowerInvariant(),
            ["track"] = new JsonObject { ["asked"] = snap.RequestedSkyTrack, ["used"] = snap.SkyTrack },
            ["keyframe"] = snap.SkySample is { } k ? new JsonObject
            {
                ["position"] = R(k.Position), ["from"] = R(k.StartKey), ["to"] = R(k.EndKey), ["blend"] = R(k.BlendFactor),
            } : "fixed",
            ["sky"] = new JsonObject
            {
                ["name"] = sky.Name,
                ["asset"] = sky.AssetId.ToString(),
                ["legacy"] = sky.CanAutoAdjust,
                ["reflection_probe_ambiance"] = R(sky.ReflectionProbeAmbiance),
                ["gamma"] = R(sky.Gamma),
                ["haze_density"] = R(sky.HazeDensity),
                ["haze_horizon"] = R(sky.HazeHorizon),
                ["density_multiplier"] = R(sky.DensityMultiplier),
                ["distance_multiplier"] = R(sky.DistanceMultiplier),
                ["blue_density"] = V(sky.BlueDensity),
                ["blue_horizon"] = V(sky.BlueHorizon),
                ["ambient"] = V(sky.AmbientColor),
                ["sunlight_color"] = V(sky.SunlightColor),
                ["max_y"] = R(sky.MaxY),
                ["glow"] = V(sky.Glow),
                ["cloud_shadow"] = R(sky.CloudShadow),
                ["cloud_scale"] = R(sky.CloudScale),
                ["star_brightness"] = R(sky.StarBrightness),
                ["sun_up"] = sky.IsSunUp,
                ["moon_up"] = sky.IsMoonUp,
                ["hdr_min"] = R(sky.HdrMin), ["hdr_max"] = R(sky.HdrMax), ["hdr_offset"] = R(sky.HdrOffset),
                ["tonemap_mix"] = R(sky.TonemapMixValue),
            },
            ["render"] = new JsonObject
            {
                ["classic_mode"] = u.ClassicMode,
                ["sky_hdr_scale"] = R(u.SkyHdrScale),
                ["probe_ambiance"] = R(u.ReflectionProbeAmbiance),
                ["no_post"] = u.NoPost,
                ["legacy_gamma"] = u.LegacyGammaNoPost,
                // what the viewer's own window would use (dynamic exposure between these, then the tonemap at this mix);
                // with no_post we use neither
                ["viewer_exposure_range"] = new JsonArray(R(u.DynamicExposureParams.Y), R(u.DynamicExposureParams.Z)),
                ["viewer_tonemap_mix"] = R(u.TonemapMix),
                ["ambient_color"] = V(u.AmbientColor),
                ["sunlight_deferred"] = V(u.DeferredSunlightColor),
            },
            ["haze"] = new JsonObject
            {
                ["surface_left"] = haze,
                ["far_colour_linear"] = V(FarHaze(sky, u)),
            },
        };
    }

    /// <summary>The time of day the sun stands at, and the sun and moon as the viewer's Personal Lighting shows them.</summary>
    static JsonObject Time(SkySettings sky)
    {
        double hours = TimeOfDay.HoursOf(sky.SunDirection);
        static JsonObject AzEl(Vector3 d)
        {
            var (az, el) = TimeOfDay.AzimuthElevationOf(d);
            return new JsonObject { ["azimuth"] = Math.Round(az, 1) % 360, ["elevation"] = Math.Round(el, 1) };
        }
        return new JsonObject
        {
            ["hours"] = Math.Round(hours, 2), ["clock"] = TimeOfDay.Clock(hours),
            ["sun"] = AzEl(sky.SunDirection), ["moon"] = AzEl(sky.MoonDirection),
        };
    }

    // atmosphericsFuncs.glsl calcAtmosphericVars: exp(-(blue_density + haze_density) * density_multiplier * distance * distance_multiplier)
    static Vector3 Transmittance(SkySettings sky, float distance)
    {
        var combined = Vector3.Max(sky.BlueDensity + new Vector3(sky.HazeDensity), new Vector3(1e-6f));
        return EnvMath.Exp(-combined * (distance * sky.DensityMultiplier * sky.DistanceMultiplier));
    }

    // what fully hazed things fade to, looking level and square to the sun (calcAtmosphericVars' additive with
    // combined_haze -> 0, then hazeF: srgb_to_linear(additive * 2) * sky_hdr_scale), before exposure and tonemapping
    static Vector3 FarHaze(SkySettings sky, SkyUniforms u)
    {
        var combined = Vector3.Max(u.BlueDensity + new Vector3(u.HazeDensity), new Vector3(1e-6f));
        var blueWeight = u.BlueDensity / combined;
        var hazeWeight = new Vector3(u.HazeDensity) / combined;
        var lightAtten = (u.BlueDensity + new Vector3(u.HazeDensity * 0.25f)) * (u.DensityMultiplier * u.MaxY);
        float above = 1f / MathF.Max(1e-6f, u.LightNorm.Y);
        var sunlight = (u.SunUpFactor == 1 ? u.SunlightColor : u.MoonlightColor) * EnvMath.Exp(-lightAtten * above);
        // haze_glow square to the light: 1 - 0, times glow.x, to the power glow.z
        float glow = Math.Clamp(MathF.Pow(u.Glow.X, u.Glow.Z), -100000f, 100000f) + 0.25f;
        glow *= u.SunMoonGlowFactor;
        var amb = u.AmbientColor + (Vector3.One - u.AmbientColor) * u.CloudShadow * 0.5f;
        var cs = sunlight * (1f - u.CloudShadow);
        var additive = u.BlueHorizon * blueWeight * (cs + amb) + new Vector3(u.HazeHorizon) * hazeWeight * (cs * glow + amb);
        additive = Vector3.Min(additive, new Vector3(10f));
        return EnvMath.SrgbToLinear(additive * 2f) * u.SkyHdrScale;
    }
}
