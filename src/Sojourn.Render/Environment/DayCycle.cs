using System.Numerics;
using LibreMetaverse.StructuredData;
using UUID = LibreMetaverse.UUID;

namespace Sojourn.Render.Environment;

/// <summary>Where a blended value sits on a track: the bounding keyframes and the blend between them.</summary>
public readonly record struct TrackSample(float Position, float StartKey, float EndKey, float BlendFactor);

/// <summary>
/// An EEP day cycle (LLSettingsDay, llinventory/llsettingsdaycycle.cpp): track 0 is water, tracks
/// 1-4 are skies for rising altitude bands. Evaluation reproduces LLTrackBlenderLoopingTime in
/// newview/llenvironment.cpp (how a DayInstance blends keyframes for a given clock time).
/// </summary>
public sealed class DayCycle
{
    public const int TrackWater = 0;
    public const int TrackGroundLevel = 1;
    public const int TrackMax = 5;
    public const int FrameMax = 56;
    public const float FrameSlopFactor = 0.02501f;
    private const float MultisliderIncrement = 0.005f;

    public const double MinimumDayLength = 14400;
    public const double DefaultDayLength = 14400;  // 4 hours
    public const double MaximumDayLength = 604800;
    public const double DefaultDayOffset = 57600;  // +16 h == -8 h (SLT)

    /// <summary>The grid's default day cycle asset the viewer loads when a region has none.</summary>
    public static readonly UUID DefaultAssetId = new("5646d39e-d3d7-6aff-ed71-30fc87d64a91");

    public string Name { get; set; } = "";
    public UUID AssetId { get; set; }

    private readonly SortedList<float, WaterSettings> _water = new();
    private readonly SortedList<float, SkySettings>[] _sky =
        [new(), new(), new(), new()];

    public IReadOnlyList<KeyValuePair<float, WaterSettings>> WaterTrack => Pairs(_water);

    /// <summary>Sky track 1..4 (keyframe position, sky), sorted by position.</summary>
    public IReadOnlyList<KeyValuePair<float, SkySettings>> GetSkyTrack(int track) =>
        track is >= 1 and < TrackMax ? Pairs(_sky[track - 1]) : [];

    private static List<KeyValuePair<float, T>> Pairs<T>(SortedList<float, T> l) => l.ToList();

    public bool IsTrackEmpty(int track) =>
        track == TrackWater ? _water.Count == 0 : track is >= 1 and < TrackMax ? _sky[track - 1].Count == 0 : true;

    /// <summary>What LLEnvironment::recordEnvironment demands of a region/parcel day: water and ground sky.</summary>
    public bool IsUsable => !IsTrackEmpty(TrackWater) && !IsTrackEmpty(TrackGroundLevel);

    public void SetWater(float keyframe, WaterSettings water) => _water[Math.Clamp(keyframe, 0f, 1f)] = water;

    public void SetSky(int track, float keyframe, SkySettings sky)
    {
        if (track is < 1 or >= TrackMax) throw new ArgumentOutOfRangeException(nameof(track));
        _sky[track - 1][Math.Clamp(keyframe, 0f, 1f)] = sky;
    }

    /// <summary>LLTrackBlenderLoopingTime::selectTrackNumber: the requested track or the nearest non-empty one below.</summary>
    public int SelectSkyTrack(int requested)
    {
        if (requested == TrackWater)
            return TrackWater;
        for (int test = Math.Min(requested, TrackMax - 1); test > 0; --test)
        {
            if (_sky[test - 1].Count > 0)
                return test;
        }
        return 1;
    }

    // ---------------------------------------------------------------- evaluation

    /// <summary>llenvironment.cpp convert_time_to_position.</summary>
    public static float TimeToPosition(double seconds, double dayLength)
    {
        if (dayLength == 0)
            return 1f;
        float position = (float)((seconds % dayLength) / dayLength);
        return Math.Clamp(position, 0f, 1f);
    }

    // llenvironment.cpp get_wrapping_distance (returns 1 for equal keys: a full loop)
    private static float WrappingDistance(float begin, float end) =>
        begin < end ? end - begin : begin > end ? 1f - (begin - end) : 1f;

    private static (int before, int after) Bounds(IList<float> keys, float key)
    {
        int n = keys.Count;
        int lb = 0;
        while (lb < n && keys[lb] < key) lb++;
        int before;
        if (lb == n) before = n - 1;
        else if (keys[lb] > key) before = lb == 0 ? n - 1 : lb - 1;
        else before = lb;

        int ub = 0;
        while (ub < n && keys[ub] <= key) ub++;
        int after = ub == n ? 0 : ub;
        return (before, after);
    }

    private static TrackSample Sample(IList<float> keys, float position)
    {
        var (b, a) = Bounds(keys, position);
        float start = keys[b], end = keys[a];
        float span = WrappingDistance(start, end);
        float p = position < start ? position + 1f : position;
        float blendf = (p - start) / span;
        // initializeTarget clamps to [0, 0.999]; LLSettingsBlender::setBlendFactor then to [0, 1] as F32.
        blendf = Math.Clamp(blendf, 0f, 0.999f);
        return new TrackSample(position, start, end, blendf);
    }

    /// <summary>
    /// The sky on <paramref name="track"/> (already resolved with <see cref="SelectSkyTrack"/>) at
    /// <paramref name="adjustedSeconds"/> = unix seconds + day offset. Null if the track is empty.
    /// </summary>
    public SkySettings? EvaluateSky(int track, double adjustedSeconds, double dayLength, out TrackSample sample)
    {
        sample = default;
        if (track is < 1 or >= TrackMax) return null;
        var list = _sky[track - 1];
        if (list.Count == 0) return null;
        float position = TimeToPosition(adjustedSeconds, dayLength);
        sample = Sample(list.Keys, position);
        var start = list[sample.StartKey];
        var end = list[sample.EndKey];
        return SkySettings.Blend(start, end, sample.BlendFactor);
    }

    /// <summary>The sun's rotation on a sky track at a day position, as <see cref="EvaluateSky"/> blends it, without
    /// blending the rest (the time-of-day search: Environment/TimeOfDay.cs). Null if the track is empty.</summary>
    public Quaternion? SunRotationAt(int track, float position)
    {
        if (track is < 1 or >= TrackMax) return null;
        var list = _sky[track - 1];
        if (list.Count == 0) return null;
        var sample = Sample(list.Keys, position);
        return EnvMath.LLSlerp(sample.BlendFactor, list[sample.StartKey].SunRotation, list[sample.EndKey].SunRotation);
    }

    public WaterSettings? EvaluateWater(double adjustedSeconds, double dayLength, out TrackSample sample)
    {
        sample = default;
        if (_water.Count == 0) return null;
        float position = TimeToPosition(adjustedSeconds, dayLength);
        sample = Sample(_water.Keys, position);
        return WaterSettings.Blend(_water[sample.StartKey], _water[sample.EndKey], sample.BlendFactor);
    }

    /// <summary>
    /// Total cloud scroll (in cloud-texture UV units, before the viewer's /100 is applied it is
    /// rate*seconds) accumulated from adjusted time 0 to <paramref name="adjustedSeconds"/> on this
    /// track: the exact integral of the keyframe-blended cloud_scroll_rate, divided by 100 as
    /// LLEnvironment::updateCloudScroll does. The viewer integrates from login; integrating from a
    /// fixed origin gives a deterministic, continuous phase instead. Returned modulo 1.
    /// </summary>
    public Vector2 IntegrateCloudScroll(int track, double adjustedSeconds, double dayLength)
    {
        if (track is < 1 or >= TrackMax) return Vector2.Zero;
        var list = _sky[track - 1];
        if (list.Count == 0) return Vector2.Zero;
        var keys = list.Keys;

        if (dayLength <= 0)
        {
            var r = list.Values[0].CloudScrollRate;
            return Wrap(new Vector2d(r.X, r.Y) * (adjustedSeconds / 100.0));
        }

        // Within a span the blended rate is linear in day position, so the trapezoid rule over
        // intervals split at every keyframe is exact (the 0.999 blend clamp is ignored).
        Vector2d Integrate(double to)
        {
            var xs = new List<double> { 0.0 };
            foreach (var k in keys)
                if (k > 0f && k < to) xs.Add(k);
            xs.Add(to);

            var sum = new Vector2d(0, 0);
            for (int i = 0; i + 1 < xs.Count; i++)
            {
                double x0 = xs[i], x1 = xs[i + 1];
                if (x1 <= x0) continue;
                var (b, a) = Bounds(keys, (float)((x0 + x1) * 0.5));
                double start = keys[b];
                double span = WrappingDistance(keys[b], keys[a]);
                var r0 = list.Values[b].CloudScrollRate;
                var r1 = list.Values[a].CloudScrollRate;
                Vector2d RateAt(double x)
                {
                    double p = x < start ? x + 1.0 : x;
                    double f = (p - start) / span;
                    return new Vector2d(r0.X + (r1.X - r0.X) * f, r0.Y + (r1.Y - r0.Y) * f);
                }
                sum += (RateAt(x0) + RateAt(x1)) * (0.5 * (x1 - x0));
            }
            return sum;
        }

        double days = Math.Floor(adjustedSeconds / dayLength);
        double frac = (adjustedSeconds - days * dayLength) / dayLength;
        var perDay = Integrate(1.0);
        var partial = Integrate(frac);
        var total = (perDay * days + partial) * (dayLength / 100.0);
        return Wrap(total);
    }

    private static Vector2 Wrap(Vector2d v)
    {
        static float W(double x) => (float)(x - Math.Floor(x));
        return new Vector2(W(v.X), W(v.Y));
    }

    private readonly record struct Vector2d(double X, double Y)
    {
        public static Vector2d operator +(Vector2d a, Vector2d b) => new(a.X + b.X, a.Y + b.Y);
        public static Vector2d operator *(Vector2d a, double s) => new(a.X * s, a.Y * s);
    }

    // ---------------------------------------------------------------- building

    /// <summary>
    /// An EEP day cycle LLSD (the "day_cycle" of an ExtEnvironment message, or a daycycle settings
    /// asset): LLSettingsVODay::buildFromEnvironmentMessage + LLSettingsDay::initialize. Frames that
    /// fail validation are dropped as the viewer drops them; check <see cref="IsUsable"/>.
    /// </summary>
    public static DayCycle FromLlsd(OSDMap day, ICollection<string>? problems = null, bool fixFrameSlop = false)
    {
        var d = new DayCycle
        {
            Name = day["name"].AsString(),
            AssetId = day["asset_id"].AsUUID(),
        };

        var built = new Dictionary<string, object>();
        if (day["frames"] is OSDMap frames)
        {
            foreach (var name in frames.Keys)
            {
                var data = frames[name];
                if (data is not OSDMap fm)
                {
                    problems?.Add($"frame '{name}' is not a map");
                    continue;
                }
                string type = fm["type"].AsString();
                object? setting = type switch
                {
                    "sky" => SkySettings.FromLlsd(fm, problems),
                    "water" => WaterSettings.FromLlsd(fm, problems),
                    _ => null,
                };
                if (setting is null)
                {
                    problems?.Add($"frame '{name}' (type '{type}') dropped");
                    continue;
                }
                built[name] = setting;
            }
        }
        else
        {
            problems?.Add("day cycle has no frames map");
        }

        if (day["tracks"] is not OSDArray tracks)
        {
            problems?.Add("day cycle has no tracks array");
            return d;
        }

        for (int i = 0; i < tracks.Count && i < TrackMax; i++)
        {
            if (tracks[i] is not OSDArray track)
                continue;
            int kept = 0;
            foreach (var entry in track)
            {
                // validateDayCycleTrack: drop malformed entries and anything past FRAME_MAX
                if (entry is not OSDMap em) continue;
                if (!SettingsReader.Present(em, "key_keyframe")) continue;
                var kf = em["key_keyframe"];
                if (kf.Type is not (OSDType.Real or OSDType.Integer)) continue;
                if (!SettingsReader.Present(em, "key_name") && !SettingsReader.Present(em, "key_id")) continue;
                if (kept >= FrameMax) continue;
                kept++;

                float keyframe = Math.Clamp((float)kf.AsReal(), 0f, 1f);
                if (!SettingsReader.Present(em, "key_name"))
                    continue; // key_id references are not resolved by the viewer either
                string keyName = em["key_name"].AsString();
                if (!built.TryGetValue(keyName, out var setting))
                {
                    problems?.Add($"track {i} references missing frame '{keyName}'");
                    continue;
                }

                if (i == TrackWater)
                {
                    if (setting is not WaterSettings w)
                    {
                        problems?.Add($"water track references a sky frame '{keyName}'");
                        continue;
                    }
                    if (fixFrameSlop && d._water.Count > 0)
                        keyframe = FixSlop(d._water, keyframe);
                    d._water[keyframe] = w;
                }
                else
                {
                    if (setting is not SkySettings s)
                    {
                        problems?.Add($"sky track {i} references a water frame '{keyName}'");
                        continue;
                    }
                    var list = d._sky[i - 1];
                    if (fixFrameSlop && list.Count > 0)
                        keyframe = FixSlop(list, keyframe);
                    list[keyframe] = s;
                }
            }
        }

        if (!d.IsUsable)
            problems?.Add("day cycle lacks a water frame or a ground-level sky frame");
        return d;
    }

    // llsettingsdaycycle.cpp get_wrapping_distance (returns 0 for equal keys)
    private static float DcWrappingDistance(float begin, float end) =>
        begin < end ? end - begin : begin > end ? 1f - (begin - end) : 0f;

    // LLSettingsDay::getSettingsNearKeyframe; returns the found key or null.
    private static float? NearKeyframe(IList<float> keys, float keyframe, float fudge)
    {
        if (keys.Count == 0) return null;
        float startframe = keyframe - fudge;
        if (startframe < 0f) startframe = 1f + startframe;

        int it = 0;
        while (it < keys.Count && keys[it] <= startframe) it++;
        if (it == keys.Count) it = 0;

        float dist = DcWrappingDistance(startframe, keys[it]);
        int next = it + 1;
        if (dist <= MultisliderIncrement && next < keys.Count)
            return keys[next];
        if (dist <= fudge * 2f)
            return keys[it];
        return null;
    }

    /// <summary>LLSettingsDay::initialize(validate_frames=true): nudge keyframes that crowd an existing one.</summary>
    private static float FixSlop<T>(SortedList<float, T> track, float keyframe)
    {
        var keys = track.Keys;
        float? near = NearKeyframe(keys, keyframe, FrameSlopFactor);
        if (near is not float found)
            return keyframe;

        float newFrame = keyframe;
        float totalShift = 0f;
        const float moveFactor = FrameSlopFactor + MultisliderIncrement;
        bool moveForward = true;
        if ((newFrame < found && (found - newFrame) <= FrameSlopFactor)
            || (newFrame > found && (newFrame - found) > FrameSlopFactor))
        {
            moveForward = false;
        }

        if (moveForward)
        {
            int iter = keys.IndexOf(found);
            newFrame = found;
            while (totalShift < 1f)
            {
                totalShift += moveFactor + (found >= newFrame ? found : found + 1f) - newFrame;
                newFrame = found + moveFactor;
                if (newFrame > 1f) newFrame--;

                iter++;
                if (iter == keys.Count) iter = 0;
                float k = keys[iter];
                if ((k >= newFrame - MultisliderIncrement && newFrame + FrameSlopFactor >= k)
                    || (k < newFrame && newFrame + FrameSlopFactor >= k + 1f))
                    found = k;
                else
                    break;
            }
        }
        else
        {
            int iter = keys.IndexOf(found); // reverse iteration: moving to lower indices
            newFrame = found;
            while (totalShift < 1f)
            {
                totalShift += moveFactor + newFrame - (found <= newFrame ? found : found - 1f);
                newFrame = found - moveFactor;
                if (newFrame < 0f) newFrame++;

                iter--;
                if (iter < 0) iter = keys.Count - 1;
                float k = keys[iter];
                if ((k <= newFrame + MultisliderIncrement && newFrame - FrameSlopFactor <= k)
                    || (k > newFrame && newFrame - FrameSlopFactor <= k - 1f))
                    found = k;
                else
                    break;
            }
        }

        return totalShift >= 1f ? keyframe : newFrame;
    }

    /// <summary>
    /// The pre-EEP region environment (EnvironmentSettings capability / legacy message):
    /// LLSettingsVODay::buildFromLegacyMessage. <paramref name="dayCycle"/> is the array of
    /// [position, sky name] pairs, <paramref name="skies"/> maps names to WindLight skies.
    /// </summary>
    public static DayCycle FromLegacyMessage(OSD dayCycle, OSDMap skies, OSDMap water, ICollection<string>? problems = null)
    {
        var frames = new OSDMap();
        foreach (var name in skies.Keys)
        {
            var legacySky = skies[name];
            string newName = "sky:" + name;
            OSDMap n = legacySky is OSDMap lm ? SkySettings.TranslateLegacyLlsd(lm) ?? new OSDMap() : new OSDMap();
            n["name"] = OSD.FromString(newName);
            frames[newName] = n;
        }

        var waterSettings = WaterSettings.TranslateLegacyLlsd(water) ?? new OSDMap();
        string waterName = "water:" + waterSettings["name"].AsString();
        waterSettings["name"] = OSD.FromString(waterName);
        frames[waterName] = waterSettings;

        var waterTrack = new OSDArray { new OSDMap { ["key_keyframe"] = OSD.FromReal(0.0), ["key_name"] = OSD.FromString(waterName) } };
        var skyTrack = new OSDArray();
        if (dayCycle is OSDArray entries)
        {
            foreach (var e in entries)
            {
                if (e is not OSDArray pair || pair.Count < 2) continue;
                skyTrack.Add(new OSDMap
                {
                    ["key_keyframe"] = OSD.FromReal(pair[0].AsReal()),
                    ["key_name"] = OSD.FromString("sky:" + pair[1].AsString()),
                });
            }
        }

        var day = new OSDMap
        {
            ["name"] = OSD.FromString("Region (legacy)"),
            ["tracks"] = new OSDArray { waterTrack, skyTrack },
            ["frames"] = frames,
            ["type"] = OSD.FromString("daycycle"),
        };
        return FromLlsd(day, problems, fixFrameSlop: true);
    }

    /// <summary>
    /// A WindLight day preset (app_settings/windlight/days/*.xml: [position, sky name] pairs) with its
    /// skies and water (LLSettingsVODay::buildFromLegacyPreset; no keyframe slop fixing).
    /// </summary>
    public static DayCycle FromLegacyPreset(OSDArray dayKeys, IReadOnlyDictionary<string, OSDMap> skies, OSDMap water,
        string name = "", ICollection<string>? problems = null)
    {
        var d = new DayCycle { Name = name };
        var w = WaterSettings.FromLegacy(water, "Default", problems);
        if (w is not null)
            d.SetWater(0f, w);
        var builtSkies = new Dictionary<string, SkySettings?>();
        foreach (var e in dayKeys)
        {
            if (e is not OSDArray pair || pair.Count < 2) continue;
            string skyName = pair[1].AsString();
            if (!builtSkies.TryGetValue(skyName, out var sky))
            {
                sky = skies.TryGetValue(skyName, out var lm) ? SkySettings.FromLegacy(lm, skyName, problems) : null;
                if (sky is null) problems?.Add($"legacy day '{name}': sky '{skyName}' unavailable");
                builtSkies[skyName] = sky;
            }
            if (sky is not null)
                d.SetSky(TrackGroundLevel, (float)pair[0].AsReal(), sky);
        }
        return d;
    }

    /// <summary>
    /// LLSettingsDay::defaults(): eight identical default frames of which (by an LLSD reuse bug) only
    /// the last, at 0.875, is referenced: a one-frame day with the built-in sky and water.
    /// </summary>
    public static DayCycle CreateBuiltinDefault()
    {
        var d = new DayCycle { Name = "_default_" };
        d.SetWater(0.875f, WaterSettings.CreateDefault());
        d.SetSky(TrackGroundLevel, 0.875f, SkySettings.CreateDefault());
        return d;
    }
}
