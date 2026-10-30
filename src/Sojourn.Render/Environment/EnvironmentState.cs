using System.Numerics;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.StructuredData;
using UUID = LibreMetaverse.UUID;

namespace Sojourn.Render.Environment;

public enum EnvironmentSource
{
    /// <summary>LLEnvironment's ENV_DEFAULT: the built-in sky and water (before or without any region data).</summary>
    BuiltinDefault,
    Region,
    Parcel,
    /// <summary>A viewer-side override, ENV_LOCAL (e.g. World > Environment > Midday).</summary>
    Local,
}

/// <summary>
/// One environment selection (LLEnvironment::DayInstance): either a day cycle with its length and
/// offset, or fixed sky and/or water (the NO_ANIMATE flags).
/// </summary>
public sealed class EnvironmentLayer
{
    public DayCycle? Day { get; init; }
    public SkySettings? FixedSky { get; init; }
    public WaterSettings? FixedWater { get; init; }
    public double DayLength { get; init; } = DayCycle.DefaultDayLength;
    public double DayOffset { get; init; } = DayCycle.DefaultDayOffset;

    public static EnvironmentLayer FromDay(DayCycle day, double dayLength = DayCycle.DefaultDayLength,
        double dayOffset = DayCycle.DefaultDayOffset) =>
        new() { Day = day, DayLength = dayLength, DayOffset = dayOffset };

    public static EnvironmentLayer FromFixed(SkySettings? sky, WaterSettings? water = null) =>
        new() { FixedSky = sky, FixedWater = water };
}

/// <summary>The environment the viewer would be showing at one instant.</summary>
public sealed class EnvironmentSnapshot
{
    /// <summary>The blended sky (its BlendFactor and Next*TextureId describe the keyframe blend).</summary>
    public required SkySettings Sky { get; init; }
    public required WaterSettings Water { get; init; }
    public EnvironmentSource SkySource { get; init; }
    public EnvironmentSource WaterSource { get; init; }
    /// <summary>The layers the sky and water came from (null: the built-in default), telling a change of what's shown
    /// from time moving on (Environment/EnvironmentFader.cs).</summary>
    public EnvironmentLayer? SkyLayer { get; init; }
    public EnvironmentLayer? WaterLayer { get; init; }
    /// <summary>Sky track asked for by altitude (1-4) and the one actually used (nearest non-empty below).</summary>
    public int RequestedSkyTrack { get; init; } = 1;
    public int SkyTrack { get; init; } = 1;
    /// <summary>Keyframe sample, or null for a fixed sky/water.</summary>
    public TrackSample? SkySample { get; init; }
    public TrackSample? WaterSample { get; init; }
    /// <summary>
    /// LLEnvironment::getCloudScrollDelta, wrapped to [0,1) (the cloud noise texture repeats, so this is
    /// exact). Zero when the current scroll rate is exactly zero, as the viewer resets it.
    /// </summary>
    public Vector2 CloudScrollDelta { get; init; }
    public DateTime Utc { get; init; }
    /// <summary>Unix seconds of <see cref="Utc"/>.</summary>
    public double UnixSeconds { get; init; }
}

/// <summary>
/// The region/parcel/override environments of one agent's viewpoint, as LLEnvironment keeps them,
/// fed from the ExtEnvironment (EEP) or EnvironmentSettings (legacy WindLight) capability.
/// Evaluate() gives the blended sky and water at a UTC time and agent altitude.
/// </summary>
public sealed class EnvironmentState
{
    public EnvironmentLayer? Local { get; set; }
    public EnvironmentLayer? Parcel { get; set; }
    public EnvironmentLayer? Region { get; set; }

    /// <summary>mTrackAltitudes: [0] is always 0; [1..3] the region's track_altitudes.</summary>
    public float[] TrackAltitudes { get; set; } = [0f, 0f, 0f, 0f];

    /// <summary>Validation and parsing notes from the last Apply* calls.</summary>
    public List<string> Problems { get; } = new();

    private static readonly Lazy<DayCycle> SlDefaultDay = new(() =>
        DayCycle.FromLlsd((OSDMap)SettingsLlsd.Parse(DefaultSettingsAssets.DefaultDay)));

    /// <summary>The grid default day cycle (asset 5646d39e...), what a region without its own day shows.</summary>
    public static DayCycle DefaultRegionDay => SlDefaultDay.Value;

    /// <summary>A fixed sky from the embedded stock assets (e.g. DefaultSettingsAssets.MiddayId for "noon").</summary>
    public static SkySettings? KnownSky(UUID assetId)
    {
        var text = DefaultSettingsAssets.Find(assetId);
        return text is null ? null : SkySettings.FromLlsd((OSDMap)SettingsLlsd.Parse(text));
    }

    /// <summary>
    /// Viewer-side override like World > Environment > Midday: a fixed sky in ENV_LOCAL
    /// (water keeps following the region). Pass null to clear.
    /// </summary>
    public void SetLocalSky(SkySettings? sky) => Local = sky is null ? null : EnvironmentLayer.FromFixed(sky);

    // ---------------------------------------------------------------- feeding

    /// <summary>
    /// A full ExtEnvironment GET response ({"environment": {...}, "success": ...}). The environment's
    /// parcel_id says whether it is the region's (-1) or a parcel's.
    /// </summary>
    public void ApplyCapabilityResponse(OSDMap response)
    {
        Problems.Clear();   // refetched every few seconds: keep only the latest answer's notes
        if (response["environment"] is OSDMap env)
            ApplyEnvironment(env);
        else
            Problems.Add("capability response without an environment map");
    }

    /// <summary>
    /// The "environment" map of an ExtEnvironment response (LLEnvironment::EnvironmentInfo::extract
    /// followed by recordEnvironment). Prefer this over <see cref="ApplyMessage"/>: LibreMetaverse's
    /// EnvironmentData drops track_altitudes.
    /// </summary>
    public void ApplyEnvironment(OSDMap environment)
    {
        int parcelId = SettingsReader.Present(environment, "parcel_id") ? environment["parcel_id"].AsInteger() : -1;
        // the same answer as the one the region's or parcel's layer was made from keeps that layer (asked again every
        // few seconds; a new layer would read as a change and cross-fade)
        string signature = OSDParser.SerializeLLSDNotation(environment);
        var made = parcelId == -1 ? _regionMade : _parcelMade;
        if (made.Signature == signature && made.Layer != null && ReferenceEquals(made.Layer, parcelId == -1 ? Region : Parcel)) return;
        // EnvironmentInfo::mAltitudes starts as zeros; recordEnvironment copies it whether or not the
        // message carried track_altitudes (all zeros sends anyone above z=0 to the highest track).
        var altitudes = new float[4];
        if (environment["track_altitudes"] is OSDArray alts)
        {
            for (int i = 0; i < 3; i++)
                altitudes[i + 1] = i < alts.Count ? (float)alts[i].AsReal() : 0f;
        }

        DayCycle? day = null;
        double length = -1, offset = -1;
        if (environment["day_cycle"] is OSDMap dayMap)
        {
            day = DayCycle.FromLlsd(dayMap, Problems);
            length = SettingsReader.Present(environment, "day_length") ? environment["day_length"].AsInteger() : -1;
            offset = SettingsReader.Present(environment, "day_offset") ? environment["day_offset"].AsInteger() : -1;
        }
        Record(parcelId, day, length, offset, altitudes);
        if (parcelId == -1) _regionMade = (signature, Region);
        else _parcelMade = (signature, Parcel);
    }

    /// <summary>
    /// A LibreMetaverse ExtEnvironmentMessage. Its parser drops track_altitudes, so the current
    /// <see cref="TrackAltitudes"/> are kept; multi-track days need <see cref="ApplyEnvironment"/>.
    /// </summary>
    public void ApplyMessage(ExtEnvironmentMessage message)
    {
        var data = message.Environment;
        DayCycle? day = null;
        if (data?.DayCycle is OSDMap dayMap)
            day = DayCycle.FromLlsd(dayMap, Problems);
        Record(message.ParcelId, day, data?.DayLength ?? -1, data?.DayOffset ?? -1, null);
    }

    (string? Signature, EnvironmentLayer? Layer) _regionMade, _parcelMade;

    // LLEnvironment::recordEnvironment
    private void Record(int parcelId, DayCycle? day, double length, double offset, float[]? altitudes)
    {
        if (parcelId == -1)
        {
            if (day is null || !day.IsUsable)
            {
                if (day is not null)
                    Problems.Add("invalid region day cycle; using the default day");
                // clearEnvironment(ENV_PARCEL) + setEnvironment(ENV_REGION, LLSettingsDay::GetDefaultAssetId())
                Parcel = null;
                Region = EnvironmentLayer.FromDay(DefaultRegionDay);
            }
            else
            {
                if (altitudes is not null)
                    TrackAltitudes = altitudes;
                Region = EnvironmentLayer.FromDay(day, length, offset);
            }
        }
        else
        {
            if (day is null || !day.IsUsable)
            {
                if (day is not null)
                    Problems.Add($"invalid day cycle for parcel {parcelId}");
                Parcel = null;
            }
            else
            {
                Parcel = EnvironmentLayer.FromDay(day, length, offset);
            }
        }
    }

    /// <summary>
    /// The legacy EnvironmentSettings capability answer: an array [ {regionID, messageID}, day cycle
    /// [[pos, name]...], {name: WindLight sky}, WindLight water ] (EnvironmentInfo::extractLegacy).
    /// LibreMetaverse's LegacyEnvironmentMessage cannot hold this (it expects a map), so pass the
    /// parsed OSD. Returns false if it is not that shape.
    /// </summary>
    public bool ApplyLegacyEnvironment(OSD legacy)
    {
        if (legacy is not OSDArray arr || arr.Count < 4 || arr[0] is not OSDMap head || !SettingsReader.Present(head, "regionID"))
        {
            Problems.Add("invalid legacy environment");
            return false;
        }
        var day = DayCycle.FromLegacyMessage(arr[1], arr[2] as OSDMap ?? new OSDMap(), arr[3] as OSDMap ?? new OSDMap(), Problems);
        // extractLegacy's altitudes {0, 10001, 10002, 10003} are written one slot too far, so slot 1 stays 0.
        Record(-1, day, DayCycle.DefaultDayLength, DayCycle.DefaultDayOffset, [0f, 0f, 10001f, 10002f]);
        return true;
    }

    // ---------------------------------------------------------------- evaluation

    /// <summary>LLEnvironment::calculateSkyTrackForAltitude (the viewer uses the agent's z, not the camera's).</summary>
    public static int CalculateSkyTrackForAltitude(IReadOnlyList<float> altitudes, double altitude)
    {
        int idx = 0;
        while (idx < altitudes.Count && altitude > altitudes[idx]) idx++;
        if (idx == 0) return 1;
        if (idx == altitudes.Count) return 4;
        return Math.Min(idx, 4);
    }

    /// <summary>
    /// The sky and water at <paramref name="utc"/> for an agent at <paramref name="agentAltitude"/>
    /// metres (region z). Steady state: the viewer's cross-fades on environment changes and altitude-track switches
    /// are the renderer's (Environment/EnvironmentFader.cs).
    /// </summary>
    public EnvironmentSnapshot Evaluate(DateTime utc, double agentAltitude = 0) => Evaluate(utc, agentAltitude, Local);

    /// <summary><see cref="Evaluate(DateTime, double)"/> with <paramref name="local"/> in place of <see cref="Local"/>
    /// (the person's own environment, which is the same over every region).</summary>
    public EnvironmentSnapshot Evaluate(DateTime utc, double agentAltitude, EnvironmentLayer? local)
    {
        double unix = (utc.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;
        int requested = CalculateSkyTrackForAltitude(TrackAltitudes, agentAltitude);

        SkySettings? sky = null;
        WaterSettings? water = null;
        var skySource = EnvironmentSource.BuiltinDefault;
        var waterSource = EnvironmentSource.BuiltinDefault;
        TrackSample? skySample = null, waterSample = null;
        EnvironmentLayer? skyLayer = null, waterLayer = null;
        int usedTrack = 1;
        Vector2 scroll = Vector2.Zero;
        bool scrollSet = false;

        foreach (var (layer, source) in Layers(local))
        {
            double adjusted = unix + layer.DayOffset;
            if (sky is null)
            {
                if (layer.FixedSky is not null)
                {
                    sky = layer.FixedSky.Clone();
                    skySource = source;
                    skyLayer = layer;
                    var rate = sky.CloudScrollRate;
                    scroll = Wrap(new Vector2((float)(rate.X * (unix / 100.0) % 1.0), (float)(rate.Y * (unix / 100.0) % 1.0)));
                    scrollSet = true;
                }
                else if (layer.Day is not null)
                {
                    int track = layer.Day.SelectSkyTrack(requested);
                    var s = layer.Day.EvaluateSky(track, adjusted, layer.DayLength, out var sample);
                    if (s is not null)
                    {
                        sky = s;
                        skySource = source;
                        skyLayer = layer;
                        skySample = sample;
                        usedTrack = track;
                        scroll = layer.Day.IntegrateCloudScroll(track, adjusted, layer.DayLength);
                        scrollSet = true;
                    }
                }
            }
            if (water is null)
            {
                if (layer.FixedWater is not null)
                {
                    water = layer.FixedWater.Clone();
                    waterSource = source;
                    waterLayer = layer;
                }
                else if (layer.Day is not null)
                {
                    var w = layer.Day.EvaluateWater(adjusted, layer.DayLength, out var sample);
                    if (w is not null)
                    {
                        water = w;
                        waterSource = source;
                        waterLayer = layer;
                        waterSample = sample;
                    }
                }
            }
            if (sky is not null && water is not null)
                break;
        }

        sky ??= SkySettings.CreateDefault();
        water ??= WaterSettings.CreateDefault();
        if (!scrollSet)
        {
            var rate = sky.CloudScrollRate;
            scroll = Wrap(new Vector2((float)(rate.X * (unix / 100.0) % 1.0), (float)(rate.Y * (unix / 100.0) % 1.0)));
        }
        if (sky.CloudScrollRate == Vector2.Zero)
            scroll = Vector2.Zero;

        return new EnvironmentSnapshot
        {
            Sky = sky,
            Water = water,
            SkySource = skySource,
            WaterSource = waterSource,
            SkyLayer = skyLayer,
            WaterLayer = waterLayer,
            RequestedSkyTrack = requested,
            SkyTrack = usedTrack,
            SkySample = skySample,
            WaterSample = waterSample,
            CloudScrollDelta = scroll,
            Utc = utc,
            UnixSeconds = unix,
        };
    }

    private IEnumerable<(EnvironmentLayer, EnvironmentSource)> Layers(EnvironmentLayer? local)
    {
        if (local is not null) yield return (local, EnvironmentSource.Local);
        if (Parcel is not null) yield return (Parcel, EnvironmentSource.Parcel);
        if (Region is not null) yield return (Region, EnvironmentSource.Region);
    }

    private static Vector2 Wrap(Vector2 v) => new(v.X - MathF.Floor(v.X), v.Y - MathF.Floor(v.Y));
}
