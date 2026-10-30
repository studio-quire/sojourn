using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sojourn.Client.Control;
using Sojourn.Client.Social;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;

namespace Sojourn.Client.Beacons;

/// <summary>The person's beacon choices, kept in beacons-settings.json beside vr-settings.json. The viewer's defaults:
/// the floater shut (no beacons until asked), and when open its highlights on touch-scripted and physical objects;
/// the tracking beacon on. Highlight Transparent isn't kept (the viewer's isn't either).</summary>
public sealed class BeaconSettings
{
    public bool Beacons { get; set; }
    public bool Lines { get; set; }
    public bool Highlights { get; set; } = true;
    public bool Scripted { get; set; }
    public bool Touch { get; set; } = true;
    public bool Physical { get; set; } = true;
    public bool Sounds { get; set; }
    public bool Particles { get; set; }
    public bool Media { get; set; }
    public float Width { get; set; } = 1;
    public bool Sun { get; set; }
    public bool Moon { get; set; }
    public bool TrackingBeacon { get; set; } = true;
    public bool TrackingBeep { get; set; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static BeaconSettings Load(string path, Action<string>? warn = null)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<BeaconSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception e) { warn?.Invoke($"beacon settings: {e.Message} (defaults used)"); }
        return new();
    }

    public void Save(string path, Action<string>? warn = null)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, Json)); }
        catch (Exception e) { warn?.Invoke($"beacon settings: {e.Message}"); }
    }
}

/// <summary>
/// Beacons and highlights' hookup to the client (batch B8), kept out of Program.cs: the renderer's
/// <see cref="Render.World.Beacons"/> set from the person's choices (beacons-settings.json), world audio's sources for
/// the sound beacons, the session's prim flags, the tracking beacon (<see cref="Tracker"/>, its label through the name
/// tags' atlas), Ctrl+Alt+T for Highlight Transparent, the settings on SojournSettings and the agent API's
/// SojournBeacons. docs/rendering.md > Beacons and highlights. Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class BeaconsWiring : IDisposable
{
    readonly AgentSession _s;
    readonly FrameRenderer _renderer;
    readonly WorldSounds? _sounds;
    readonly ILogger _log;
    readonly string _path;
    readonly Timer _timer;
    public readonly BeaconSettings Settings;
    public readonly Tracker Tracker;

    Render.World.Beacons B => _renderer.Beacons;

    public static BeaconsWiring Start(AgentSession s, FrameRenderer renderer, SocialView social, WorldSounds? sounds, string vrSettingsPath,
        ILogger log) => new(s, renderer, social, sounds, vrSettingsPath, log);

    BeaconsWiring(AgentSession s, FrameRenderer renderer, SocialView social, WorldSounds? sounds, string vrSettingsPath, ILogger log)
    {
        _s = s;
        _renderer = renderer;
        _sounds = sounds;
        _log = log;
        _path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(vrSettingsPath)) ?? ".", "beacons-settings.json");
        Settings = BeaconSettings.Load(_path, m => log.LogWarning("{Msg}", m));
        Tracker = new Tracker(s, renderer, sounds);
        renderer.Beacons.FlagsOf = p => s.State.PrimFlagsOf(p);
        social.Marks = (region, eye) => Tracker.Label(region, eye, social.Tags);
        s.Api.Register(new BeaconsApi(s, this));
        Apply();
        _timer = new Timer(_ => Feed(), null, 1000, 100);
    }

    public void Dispose()
    {
        _timer.Dispose();
        Tracker.Dispose();
    }

    /// <summary>The settings to the renderer and the tracker.</summary>
    void Apply()
    {
        var st = Settings;
        var b = B;
        b.Kinds = (st.Scripted ? BeaconKinds.Scripted : 0) | (st.Touch ? BeaconKinds.Touch : 0) | (st.Physical ? BeaconKinds.Physical : 0)
                  | (st.Sounds ? BeaconKinds.Sound : 0) | (st.Particles ? BeaconKinds.Particles : 0) | (st.Media ? BeaconKinds.Media : 0);
        (b.Lines, b.Highlights, b.Width, b.Sun, b.Moon) = (st.Lines, st.Highlights, st.Width, st.Sun, st.Moon);
        b.On = st.Beacons;
        (Tracker.ShowBeacon, Tracker.Beep) = (st.TrackingBeacon, st.TrackingBeep);
        if (_sounds != null) _sounds.WantPlaces = st.Beacons && st.Lines && st.Sounds;
    }

    void Save() => Settings.Save(_path, m => _log.LogWarning("{Msg}", m));

    void Changed()
    {
        Apply();
        Save();
    }

    /// <summary>What changes by the moment: the sound sources' places and the avatar (10 Hz).</summary>
    void Feed()
    {
        try
        {
            if (_sounds != null) B.SoundSources = _sounds.WantPlaces ? _sounds.Places : [];
            if (_s.Client is { } c) B.SelfLocalId = c.Self.LocalID;
        }
        catch (Exception e) { _log.LogDebug(e, "beacons"); }
    }

    /// <summary>Highlight Transparent on or off (Ctrl+Alt+T, the viewer's View > Highlight Transparent).</summary>
    public bool ToggleTransparent()
    {
        B.Transparent = !B.Transparent;
        _log.LogInformation("highlight transparent {State}", B.Transparent ? "on" : "off");
        return B.Transparent;
    }

    public bool Transparent { get => B.Transparent; set => B.Transparent = value; }

    // --- the settings ---------------------------------------------------------------------------------------------------

    /// <summary>The floater's rules: touch-only and scripted aren't both on (the one turned on wins), and beacons and
    /// highlights aren't both off (turning one off with the other off turns the other on).</summary>
    public void Set(string what, bool on)
    {
        var st = Settings;
        switch (what)
        {
            case "beacons": st.Beacons = on; break;
            case "lines": st.Lines = on; if (!on && !st.Highlights) st.Highlights = true; break;
            case "highlights": st.Highlights = on; if (!on && !st.Lines) st.Lines = true; break;
            case "scripted": st.Scripted = on; if (on) st.Touch = false; break;
            case "touch": st.Touch = on; if (on) st.Scripted = false; break;
            case "physical": st.Physical = on; break;
            case "sounds": st.Sounds = on; break;
            case "particles": st.Particles = on; break;
            case "media": st.Media = on; break;
            case "sun": st.Sun = on; break;
            case "moon": st.Moon = on; break;
            case "tracking_beacon": st.TrackingBeacon = on; break;
            case "tracking_beep": st.TrackingBeep = on; break;
            default: throw new ArgumentException(what);
        }
        Changed();
    }

    public void SetWidth(float px)
    {
        Settings.Width = Math.Clamp(MathF.Round(px), 1, 16);
        Changed();
    }

    public void Settle(SettingsApi api)
    {
        string OnOff(bool b) => b ? "on" : "off";
        void Switch(string name, string what, string desc, Func<bool> get) =>
            api.Add(name, new("beacons", desc, "on, off", () => OnOff(get()), SettingsApi.Switch(name, on => Set(what, on))));
        var st = Settings;
        Switch("beacons", "beacons", "the viewer's Beacons and Highlights (its floater open; off by default): markers on the kinds of things "
            + "turned on below (beacons_scripted and the rest), as beacon_lines and beacon_highlights say", () => st.Beacons);
        Switch("beacon_lines", "lines", "beacons drawn as lines: a cross at each thing, 50 m up and down and seen through walls (off by default, "
            + "as the viewer's)", () => st.Lines);
        Switch("beacon_highlights", "highlights", "the things' faces tinted red (on by default); beacon_lines and this aren't both off", () => st.Highlights);
        Switch("beacons_scripted", "scripted", "beacons on scripted objects (red; off by default; turning it on turns beacons_touch off)", () => st.Scripted);
        Switch("beacons_touch", "touch", "beacons on scripted objects that take touches: where to click on a sign or a door (red; on by default; "
            + "turning it on turns beacons_scripted off)", () => st.Touch);
        Switch("beacons_physical", "physical", "beacons on physical objects (green; on by default)", () => st.Physical);
        Switch("beacons_sounds", "sounds", "beacons on sound sources, heard or not (yellow; off by default): who's making that noise", () => st.Sounds);
        Switch("beacons_particles", "particles", "beacons on particle sources (light blue; off by default)", () => st.Particles);
        Switch("beacons_media", "media", "beacons on faces with media (white; off by default)", () => st.Media);
        api.Add("beacon_width", new("beacons", "how wide beacon lines are, in pixels of the view (the viewer's 1 by default)", "1-16",
            () => JsonValue.Create(st.Width), SettingsApi.Number("beacon_width", 1, 16, " px", SetWidth)));
        Switch("sun_beacon", "sun", "a line toward the sun from the avatar (with beacons on; off by default)", () => st.Sun);
        Switch("moon_beacon", "moon", "a line toward the moon from the avatar (with beacons on; off by default)", () => st.Moon);
        api.Add("highlight_transparent", new("beacons", "the viewer's Highlight Transparent (Ctrl+Alt+T): every see-through face tinted, "
            + "blended ones red, masked blue, invisiprims green, invisible ones too; for this session (off at the start)", "on, off",
            () => OnOff(B.Transparent), SettingsApi.Switch("highlight_transparent", on => B.Transparent = on)));
        Switch("tracking_beacon", "tracking_beacon", "the tracking beacon's column of light and label over a place or person tracked "
            + "(SojournBeacons track, or the map; on by default)", () => st.TrackingBeacon);
        Switch("tracking_beep", "tracking_beep", "a beep while tracking, faster as it nears (Firestorm's tracker sound; off by default)", () => st.TrackingBeep);
    }
}
