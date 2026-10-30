using System.Numerics;
using Sojourn.Client.Assistant;
using Sojourn.Render.Realtime;

namespace Sojourn.Client.Control;

/// <summary>
/// The settings menu (the left controller's menu button; on the desktop Esc, once nothing else takes it, or F2), in
/// sections: moving (the headset's posture, eye height, turning, your body) and the assistant (its panel, model and effort, look, spoken replies, name and character once
/// asked for, and whether its touches on other people's things wait for it). Controls say what they are (switches,
/// segments, swatches); the controls nothing on screen shows are behind the help key. Drawn in the panel's look on the
/// same quad layer (the assistant's panel gives way while it's open; a confirmation takes the layer back). Point with
/// the right controller and pull the trigger. Every change is saved at once (local/client/vr-settings.json).
/// </summary>
public sealed class SettingsMenu : ISettingsMenu
{
    readonly VrSettings _s;
    readonly string _path;
    readonly VrPilot _pilot;
    readonly AssistantHost _assistant;
    readonly Panel _panel;
    readonly Action<string> _say;
    long _version = 1;
    int _hover = -1;
    bool _drawing;
    long _drawnKey = -1;
    int _drawnInk = -1;
    float _scale;
    sealed record Drawn(OverlayImage Image, Panel.Raster Raster, string View);   // View: the section, group and page drawn
    volatile Drawn? _latest;   // written by the draw task, whole
    Drawn? _shown;             // what Frame handed out this frame (main thread): keys and presses go by it
    long _imageVersion;
    (KeyKind Kind, int Index, long Version)? _down;
    int _section;
    string? _group;     // the open group's label (in the section on screen), and the page it was opened from
    int _groupFrom;

    public bool Open { get; private set; }

    /// <summary>The draw distance as it stands, and setting it (the session's, which the agent API changes too).</summary>
    public Func<float>? DrawDistance { get; set; }
    /// <summary>The renderer's crowd limits (the view section's full avatars and complexity limit).</summary>
    public Sojourn.Render.World.AvatarLimits? Avatars { get; set; }
    public Action<float>? SetDrawDistance { get; set; }
    /// <summary>The account's maturity preference (null before login) and the most it allows; changing it asks the grid.</summary>
    public Func<(string? Now, string Max)>? Maturity { get; set; }
    public Action<string>? SetMaturity { get; set; }
    /// <summary>The renderer's live quality (shadows, reflections, detail), which the view section changes.</summary>
    public global::Sojourn.Render.Realtime.RenderQuality? Quality { get; set; }

    /// <summary>Something the menu shows changed elsewhere (the agent API): draw it again.</summary>
    public void Changed() => Interlocked.Increment(ref _version);

    /// <summary>Voice chat, when the client has it: its section of the menu, and the saved settings put to it.</summary>
    public global::Sojourn.Session.Voice.VoiceClient? Voice
    {
        get => _voice;
        set
        {
            _voice = value;
            if (value == null) return;
            value.Mode = _s.VoiceMode;
            value.Enabled = _s.VoiceEnabled;
            value.SetVolume(_s.VoiceVolume);
            value.RestorePeerChoices(
                _s.PeerVolumes.Where(kv => LibreMetaverse.UUID.TryParse(kv.Key, out _)).Select(kv => (LibreMetaverse.UUID.Parse(kv.Key), kv.Value)),
                _s.PeerVoiceMutes.Where(id => LibreMetaverse.UUID.TryParse(id, out _)).Select(LibreMetaverse.UUID.Parse));
            value.PeerChoicesChanged += () =>
            {
                _s.PeerVolumes = value.PeerVolumes.ToDictionary(p => p.Id.ToString(), p => p.Volume);
                _s.PeerVoiceMutes = value.PeerMuted.Select(id => id.ToString()).ToList();
                Save();
            };
            value.StatusChanged += _ => Interlocked.Increment(ref _version);
            value.MicChanged += () => Interlocked.Increment(ref _version);
        }
    }
    global::Sojourn.Session.Voice.VoiceClient? _voice;

    /// <summary>World audio's section (Audio/AudioWiring.cs), when the client has it.</summary>
    public Func<List<(MenuRow Row, Action<int> Act)>>? SoundRows { get; set; }
    /// <summary>Incognito's section (Private/PrivateControls.cs): the switch, let it see, and its automatic triggers.</summary>
    public Func<List<(MenuRow Row, Action<int> Act)>>? PrivateRows { get; set; }
    /// <summary>The screen reader's section (Access/AccessWiring.cs): reading out, and what's read.</summary>
    public Func<List<(MenuRow Row, Action<int> Act)>>? ReaderRows { get; set; }
    /// <summary>How the person shows to others (People/PeopleWiring.cs): Do Not Disturb, away, hover height, typing.</summary>
    public Func<List<(MenuRow Row, Action<int> Act)>>? PeopleRows { get; set; }
    /// <summary>Safety and comfort (Control/SafetyWiring.cs): the vignette, keep me in place, aim, particles, antispam, release keys.</summary>
    public Func<List<(MenuRow Row, Action<int> Act)>>? SafetyRows { get; set; }
    /// <summary>The headset's menus' placements: while any is put somewhere, the view section has a key bringing them all back.</summary>
    public MenuPlaces? Places { get; set; }
    /// <summary>SojournSettings: rows for settings other parts register (the social layer, the AO, avatars, the map,
    /// beacons, land lines, voice's hearing place) are made from it by name, and a press sets them as a caller would.</summary>
    public SettingsApi? Settings
    {
        get => _api;
        set { _api = value; if (value != null) value.Refused = why => _say("menu: " + why); }
    }
    SettingsApi? _api;

    public SettingsMenu(VrSettings settings, string path, VrPilot pilot, AssistantHost assistant, Action<string> say)
    {
        _s = settings;
        _path = path;
        _pilot = pilot;
        _assistant = assistant;
        _panel = assistant.Panel;
        _say = say;
        _panel.Suppressed = settings.InterfaceHidden;
        settings.AssistantEffort ??= new();   // "assistantEffort": null in the file
        assistant.SetModel(settings.AssistantModel, announce: false);
        assistant.ChangeModel = m => SetAssistantModel(m, announce: true);
        assistant.Settings = settings;
        assistant.SettingsChanged = () => { Save(); Interlocked.Increment(ref _version); };
        assistant.Confirmations.ConfirmTouches = () => _s.ConfirmTouches;
        _panel.Look = settings.PanelLook = PanelLook.Restore(settings.PanelLook);
        assistant.ChangeLook = look => { _s.PanelLook = look; _panel.Look = look; Save(); Interlocked.Increment(ref _version); };
        assistant.ProviderStatusChanged = () => Interlocked.Increment(ref _version);
    }

    /// <summary>With OpenRouter, which model answers (the person's own choice, never the agent API's): saved, and the
    /// sidecar checks it and starts its sessions again. Whose models answer, the keys, data collection and auto mode are
    /// chosen in the launcher (Sojourn.Launcher), not here.</summary>
    void SetOpenRouterModel(string model)
    {
        _s.OpenRouterModel = model;
        _assistant.UseKey("openrouter");   // (saves, redraws and tells the sidecar)
        _say($"openrouter model: {model}");
    }

    /// <summary>The assistant's model, from the menu, a key (F8 cycles), the console or the agent API: saved, and the
    /// sidecar switches at once.</summary>
    public void SetAssistantModel(AssistantModel m, bool announce)
    {
        _s.AssistantModel = m;
        _assistant.SetModel(m, announce);
        Save();
        Interlocked.Increment(ref _version);
        _say("assistant model: " + AssistantHost.Describe(m));
    }

    /// <summary>A model's effort (haiku, sonnet or opus; opus's covers the planner), from the menu or the agent API:
    /// saved, and the sidecar brings that model's session back up at it for its next turn.</summary>
    public void SetAssistantEffort(string model, string effort)
    {
        _s.AssistantEffort.Set(model, effort);
        _assistant.EffortsChanged();
        Save();
        Interlocked.Increment(ref _version);
        _say($"{model} effort: {effort}");
    }

    public void CycleAssistantModel() => SetAssistantModel(NextModel(_s.AssistantModel), announce: true);

    static AssistantModel NextModel(AssistantModel m) => (AssistantModel)(((int)m + 1) % Enum.GetValues<AssistantModel>().Length);

    public void Toggle()
    {
        Open = !Open; _down = null; _forgetArmed = false; _restOn = Nowhere; _tip = null; Used(); Interlocked.Increment(ref _version);
        _say(Open ? "settings menu open" : "settings menu closed");
    }

    /// <summary>Esc on the desktop with the menu open: its help put away, else out of a group to the page it was opened
    /// from, else the menu closed.</summary>
    public void Back()
    {
        if (!Open) return;
        Used();
        _restOn = Nowhere;
        _tip = null;
        if (_help) _help = false;
        else if (_group != null)
        {
            _say($"settings menu: back from {_group}");
            _page = _groupFrom;
            _group = null;
            _forgetArmed = false;
        }
        else { Close(); return; }
        Interlocked.Increment(ref _version);
    }

    long _usedAt = Environment.TickCount64;
    /// <summary>The person is using it (the pointer on it, a press).</summary>
    public void Used() => _usedAt = Environment.TickCount64;
    /// <summary>Seconds since it was opened or last used: one left alone that long closes by itself in the headset
    /// (it can sit out of sight to one side, and the assistant's panel waits while it's open).</summary>
    public double IdleSeconds => (Environment.TickCount64 - _usedAt) / 1000.0;

    bool _forgetArmed, _forgetWasArmed;
    public void Close() { if (Open) Toggle(); }

    /// <summary>Everything of ours out of sight (VrSettings.InterfaceHidden): the panel, this menu, the feed, cards,
    /// tags, tokens, boards, the pointer. Only a confirmation shows. What was open stays open, out of sight, and comes
    /// back as it was; the panel comes up with it (a reply that waited, or the last one).</summary>
    public bool Hidden => _s.InterfaceHidden;

    public void SetHidden(bool hidden)
    {
        if (_s.InterfaceHidden == hidden) return;
        _s.InterfaceHidden = hidden;
        _panel.Suppressed = hidden;
        Save();
        Interlocked.Increment(ref _version);
        _say(hidden ? "interface hidden (X or Tab shows it)" : "interface shown");
    }

    /// <summary>The X button, Tab.</summary>
    public void ToggleHidden() => SetHidden(!_s.InterfaceHidden);

    /// <summary>The menu button, Esc or F2: while the interface is hidden, it comes back with the menu open (an open menu out
    /// of sight would only be in the way).</summary>
    public void Summon()
    {
        if (_s.InterfaceHidden) { SetHidden(false); if (!Open) Toggle(); }
        else Toggle();
    }

    void Save() => _s.Save(_path, _say);

    static string Deg(float d) => d % 1 == 0 ? $"{d:0}" : $"{d:0.#}";

    /// <summary>The menu's sections (tabs along the top), each with its rows as they stand and what a press does (by the
    /// row's control, <see cref="MenuRow"/>: 0 a switch or the row itself, -1 and +1 a stepper, i an option or swatch).
    /// A section too long to take in at once keeps some rows in groups, each opening a page of its own (the header then
    /// a way back). Moving is the headset's alone, so the desktop doesn't show it. Unless the person turned on
    /// "advanced" (or all: the tests' presses and pictures), only the rows MenuGuide marks simple, and the sections
    /// that have any.</summary>
    List<(string Name, List<(MenuRow Row, Action<int> Act)> Rows)> Sections(bool all = false)
    {
        var sections = AllSections();
        if (all || _s.AdvancedMenu) return sections;
        return sections.Select(x => (x.Name, x.Rows.Where(r => MenuGuide.Simple(x.Name, r.Row)).ToList())).Where(x => x.Item2.Count > 0).ToList();
    }

    /// <summary>The advanced menu on or off, from its row or the agent API: the menu opens on the assistant's section,
    /// which both have.</summary>
    public void SetAdvanced(bool on)
    {
        _s.AdvancedMenu = on;
        _section = Math.Max(0, Sections().FindIndex(x => x.Name == "assistant"));
        _group = null;
        _page = 0;
        Interlocked.Increment(ref _version);
    }

    List<(string Name, List<(MenuRow Row, Action<int> Act)> Rows)> AllSections()
    {
        var sections = new List<(string, List<(MenuRow, Action<int>)>)>();
        if (_assistant.InHeadset) sections.Add(("moving", MovingRows()));
        sections.Add(("assistant", AssistantRows()));
        if (PrivateRows != null) sections.Add(("incognito", PrivateRows()));
        sections.Add(("you", YouRows()));
        sections.Add(("social", SocialRows()));
        if (_voice is { Available: true }) sections.Add(("voice", VoiceRows()));
        if (SoundRows != null) sections.Add(("sound", SoundRows()));
        if (ReaderRows != null) sections.Add(("screen reader", ReaderRows()));   // the screen reader's (the tabs are crowded)
        if (SafetyRows != null) sections.Add(("safety", SafetyRows()));
        if (DrawDistance != null) sections.Add(("view", ViewRows()));
        return sections;
    }

    List<(MenuRow Row, Action<int> Act)> MovingRows()
    {
        var rows = new List<(MenuRow, Action<int>)>
        {
            (MenuRow.Choice("posture", new[] { "standing", "seated" }, _s.Seated ? 1 : 0),
                i => { _s.Seated = i == 1; _pilot.HeightsChanged(); _say(_s.Seated ? "seated: sit up straight, then press Y to set the height" : "standing"); }),
        };
        // seated always uses the avatar's eye height: the choice only exists standing
        if (!_s.Seated)
            rows.Add((MenuRow.Choice("eye height", new[] { "your own", "the avatar's" }, _s.EyeHeight == EyeHeightMode.Real ? 0 : 1),
                i => { _s.EyeHeight = i == 0 ? EyeHeightMode.Real : EyeHeightMode.Avatar; _pilot.HeightsChanged(); }));
        rows.Add((MenuRow.Choice("turning", new[] { "smooth", "snap" }, _s.SmoothTurn ? 0 : 1), i => _s.SmoothTurn = i == 0));
        rows.Add(_s.SmoothTurn
            ? (new MenuRow("turn speed", $"{Deg(_s.TurnSpeed)}°/s", true), d => _s.TurnSpeed = Step(VrSettings.TurnSpeeds, _s.TurnSpeed, d))
            : (new MenuRow("snap angle", $"{Deg(_s.SnapAngle)}°", true), d => _s.SnapAngle = Step(VrSettings.SnapAngles, _s.SnapAngle, d)));
        rows.Add((MenuRow.Choice("moving toward", new[] { "where you look", "left hand" }, _s.HandRelative ? 1 : 0), i => _s.HandRelative = i == 1));
        rows.Add((MenuRow.Choice("movement", new[] { "predicted", "server" }, _s.Movement == MovementMode.Server ? 1 : 0),
            i => _s.Movement = i == 1 ? MovementMode.Server : MovementMode.Predicted));
        rows.Add((MenuRow.Choice("putting you back", new[] { "teleport", "jump", "slide" }, _s.TeleportBack ? 0 : _s.Correction == CorrectionStyle.Snap ? 1 : 2),
            i => { _s.TeleportBack = i == 0; if (i > 0) _s.Correction = i == 1 ? CorrectionStyle.Snap : CorrectionStyle.Slide; }));
        rows.Add((MenuRow.Switch("point to teleport", _s.PointTeleport), _ => _s.PointTeleport = !_s.PointTeleport));
        rows.Add((MenuRow.Switch("look around", _pilot.Spectating), _ => _pilot.Spectating = !_pilot.Spectating));
        rows.Add((MenuRow.Switch("your body", _s.ShowBody), _ => _s.ShowBody = !_s.ShowBody));
        if (_s.ShowBody)
            rows.Add((MenuRow.Choice("tracking", new[] { "auto", "on", "off" }, (int)_s.Tracking), i => _s.Tracking = (HandTracking)i));
        rows.Add((MenuRow.Switch("pointer", _s.Pointer), _ => _s.Pointer = !_s.Pointer));
        // what others see of where you look (Control/Gaze.cs)
        rows.Add((MenuRow.Choice("your gaze", new[] { "eye contact", "direction", "off" }, (int)_s.Gaze), i => _s.Gaze = (GazeSharing)i));
        return rows;
    }

    static readonly string[] SkyLabels = { "region", "dawn", "noon", "dusk", "night" };   // VrSettings.Skies, shortly
    static readonly float[] Resolutions = { 0, 0.6f, 0.7f, 0.8f, 0.9f, 1 };   // 0: following the GPU

    List<(MenuRow Row, Action<int> Act)> ViewRows()
    {
        var rows = new List<(MenuRow, Action<int>)>();
        // how big the headset's sheets are, this menu included (Assistant/UiSize.cs)
        if (_assistant.InHeadset) rows.Add((UiSize.Row(_s), i => UiSize.Set(_s, UiSize.Steps[i])));
        // while any menu is put somewhere: every one back to following, in front (X held does the same)
        if (_assistant.InHeadset && Places is { AnyPut: true } places) rows.Add((new MenuRow("menu positions", "reset", false), _ => places.Reset()));
        rows.Add((new MenuRow("draw distance", $"{DrawDistance!():0} m", true), d => SetDrawDistance?.Invoke(Step(VrSettings.DrawDistances, DrawDistance!(), d))));
        // the launcher's presets (GraphicsPresets): settings that match none show as "yours", left alone until one is picked
        var preset = GraphicsPresets.Match(_s.AsJson());
        var presets = preset == GraphicsPresets.Yours ? GraphicsPresets.Names.Append(GraphicsPresets.Yours).ToArray() : GraphicsPresets.Names;
        rows.Add((MenuRow.Choice("quality", presets, Array.IndexOf(presets, preset)), i => { if (i < GraphicsPresets.Names.Length) ApplyPreset(GraphicsPresets.Names[i]); }));
        rows.Add(MenuRow.Group("graphics", GraphicsRows));
        rows.Add(MenuRow.Group("avatars", AvatarRows));
        // pressed again, a sky is put back as it was (its own sun, after the time of day moved it)
        rows.Add((MenuRow.Choice("sky", SkyLabels, Math.Max(0, Array.IndexOf(VrSettings.Skies, _s.Sky))),
            i => { if (_s.Sky == VrSettings.Skies[i]) Sojourn.Core.Leap.UIApi.EnvSettings?.Invoke(_s.Sky); else _s.Sky = VrSettings.Skies[i]; }));
        // the sun's time over whatever sky is in use, half an hour a step (Control/EnvironmentWiring.cs); the row reads the
        // time again once a frame has applied it
        if (_api is { } env && env.Has("time_of_day"))
            rows.Add((new MenuRow("time of day", env.Text("time_of_day") ?? "", true), d =>
            {
                env.Change("time_of_day", System.Text.Json.Nodes.JsonValue.Create(d < 0 ? "-0.5" : "+0.5"));
                _ = Task.Delay(150).ContinueWith(_ => Changed());
            }));
        // the location line at the wrist and its lag meter, as one row (the meter is the line's end)
        if (_api is { } api && api.Has("location_line") && api.Has("lag_meter"))
        {
            int at = !api.IsOn("location_line") ? 0 : api.IsOn("lag_meter") ? 2 : 1;
            rows.Add((MenuRow.Choice("location line", new[] { "off", "on", "+ lag" }, at), i =>
            {
                SetSwitch(api, "location_line", i > 0);
                if (i > 0) SetSwitch(api, "lag_meter", i == 2);
            }));
        }
        if (_api?.Has("beacons") == true) rows.Add(MenuRow.Group("beacons", BeaconRows, _api.IsOn("beacons") ? "on" : ""));
        if (_api?.Has("property_lines") == true)
            rows.Add(MenuRow.Group("parcel lines", () =>
            {
                var lines = new List<(MenuRow Row, Action<int> Act)>();
                if (_api.SwitchRow("property_lines", "property lines") is { } p) lines.Add(p);
                if (_api.ChoiceRow("ban_lines", "ban lines", new[] { "hide", "collision", "proximity" }) is { } b) lines.Add(b);
                return lines;
            }));
        if (Maturity?.Invoke() is ({ } rating, var max))
        {
            var options = Sojourn.Core.Agents.AgentSession.Maturities.Take(Array.IndexOf(Sojourn.Core.Agents.AgentSession.Maturities, max) + 1).ToArray();
            if (options.Length > 1)
                rows.Add((MenuRow.Choice("maturity", options, Math.Max(0, Array.IndexOf(options, rating))), i => SetMaturity?.Invoke(options[i])));
        }
        if (_assistant.InHeadset) rows.Add(MenuRow.Group("menus", MenuRows));
        return rows;
    }

    /// <summary>A graphics preset's settings, each set as its own row or the agent API would set it (live, and saved).</summary>
    void ApplyPreset(string name)
    {
        foreach (var (key, value) in GraphicsPresets.Values[name])
        {
            if (key == "resolution") { _s.Resolution = GraphicsPresets.Number(value) is { } r ? (float)r : null; continue; }
            var setting = key switch
            {
                "drawDistance" => "draw_distance", "shadowCap" => "shadow_resolution", "mirrorResolution" => "mirror_resolution",
                "farShadows" => "far_shadows", "fullAvatars" => "full_avatars", _ => key,
            };
            if (value is null || _api?.Has(setting) != true) continue;
            _api.Change(setting, value.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? System.Text.Json.Nodes.JsonValue.Create(value.GetValue<bool>() ? "on" : "off")
                : System.Text.Json.Nodes.JsonValue.Create(GraphicsPresets.Number(value) ?? 0));
        }
        _say($"graphics: {name}");
    }

    static void SetSwitch(SettingsApi api, string name, bool on) => api.Change(name, System.Text.Json.Nodes.JsonValue.Create(on ? "on" : "off"));

    /// <summary>The view's graphics: the renderer's quality, the eyes' resolution, what the regions send.</summary>
    List<(MenuRow Row, Action<int> Act)> GraphicsRows()
    {
        var rows = new List<(MenuRow, Action<int>)>();
        if (Quality is { } q)
        {
            rows.Add((MenuRow.Switch("shadows", q.Shadows), _ => q.Shadows = _s.Shadows = !q.Shadows));
            rows.Add((MenuRow.Switch("reflections", q.Reflections), _ => q.Reflections = _s.Reflections = !q.Reflections));
            rows.Add((MenuRow.Switch("mirrors", q.Mirrors), _ => q.Mirrors = _s.Mirrors = !q.Mirrors));
            rows.Add((MenuRow.Switch("flexible prims", q.Flexi), _ => q.Flexi = _s.Flexi = !q.Flexi));
            rows.Add((new MenuRow("detail", $"{q.LodFactor:0.##}", true), d => q.LodFactor = _s.Detail = Step(VrSettings.Details, q.LodFactor, d)));
            rows.Add((MenuRow.Switch("load governor", q.LoadGovernor), _ => q.LoadGovernor = _s.LoadGovernor = !q.LoadGovernor));
            rows.Add((new MenuRow("see-through layers", q.AlphaLayers == 0 ? "all" : $"{q.AlphaLayers}", true),
                d => q.AlphaLayers = _s.AlphaLayers = (int)Step(VrSettings.AlphaLayerSteps.Select(x => (float)x).ToArray(), q.AlphaLayers, d)));
            rows.Add((MenuRow.Choice("see-through light", new[] { "full", "simple" }, q.SimpleAlpha ? 1 : 0), i => q.SimpleAlpha = _s.SimpleAlpha = i == 1));
            // GPU options to judge (docs/rendering.md > GPU options to judge)
            rows.Add((MenuRow.Choice("shadow & ssao size", new[] { "full", "half" }, q.HalfSsao ? 1 : 0), i => q.HalfSsao = _s.HalfSsao = i == 1));
            rows.Add((new MenuRow("far shadow margin", q.FarCascadeMargin <= 0 ? "none" : $"{q.FarCascadeMargin * 100:0}%", true),
                d => q.FarCascadeMargin = _s.FarShadowMargin = Step(VrSettings.FarShadowMargins, q.FarCascadeMargin, d)));
            rows.Add((MenuRow.Choice("far shadows", new[] { "off", "on", "sharper" }, Array.IndexOf(VrSettings.FarShadowSizes, q.FarShadowSize) is var fi and >= 0 ? fi : 1),
                i => q.FarShadowSize = _s.FarShadows = VrSettings.FarShadowSizes[i]));
            rows.Add((new MenuRow("small shadow casters", q.MinCasterTexels <= 0 ? "all" : $"skip < {q.MinCasterTexels:0.#} tx", true),
                d => q.MinCasterTexels = _s.MinCasterTexels = Step(VrSettings.CasterTexelSteps, q.MinCasterTexels, d)));
            rows.Add((MenuRow.Choice("g-buffer (on restart)", new[] { "full", "narrow" }, _s.NarrowGBuffer ? 1 : 0), i => _s.NarrowGBuffer = i == 1));
        }
        if (_assistant.InHeadset)
        {
            rows.Add((new MenuRow("resolution", _s.Resolution is { } r ? $"{r * 100:0}%" : "auto", true),
                d => { float next = Step(Resolutions, _s.Resolution ?? 0, d); _s.Resolution = next < 0.5f ? null : next; }));
            rows.Add((MenuRow.Switch("depth reprojection", _s.ReprojectionDepth), _ => _s.ReprojectionDepth = !_s.ReprojectionDepth));
        }
        // the regions' interest list in 360 mode (what's behind arrives sooner, at more traffic)
        if (_api?.SwitchRow("interest_360", "load all around") is { } all) rows.Add(all);
        if (_assistant.InHeadset && Sojourn.Client.Xr.LagHook.Testing)
        {
            rows.Add((new MenuRow("test lag", Sojourn.Client.Xr.LagHook.Cpu == "off" ? "off" : Sojourn.Client.Xr.LagHook.Cpu + " ms", true), d =>
            {
                var steps = Sojourn.Client.Xr.LagHook.Steps;
                int i = Math.Max(0, Array.IndexOf(steps, Sojourn.Client.Xr.LagHook.Cpu));
                Sojourn.Client.Xr.LagHook.Cpu = steps[Math.Clamp(i + d, 0, steps.Length - 1)];
            }));
            rows.Add((MenuRow.Switch("desktop mirror", Sojourn.Client.Xr.LagHook.Mirror), _ => Sojourn.Client.Xr.LagHook.Mirror = !Sojourn.Client.Xr.LagHook.Mirror));
        }
        return rows;
    }

    /// <summary>Other people's avatars: how many whole, the complexity limit, physics, lips and beams.</summary>
    List<(MenuRow Row, Action<int> Act)> AvatarRows()
    {
        var rows = new List<(MenuRow, Action<int>)>();
        if (Avatars is { } av)
        {
            rows.Add((new MenuRow("full avatars", av.MaxFull == 0 ? "all" : $"{av.MaxFull}", true),
                d => av.MaxFull = _s.FullAvatars = (int)Step(VrSettings.FullAvatarSteps.Select(x => (float)x).ToArray(), av.MaxFull, d)));
            rows.Add((new MenuRow("complexity limit", av.MaxComplexity == 0 ? "none" : $"{av.MaxComplexity / 1000}k", true),
                d => av.MaxComplexity = _s.AvatarComplexity = (int)Step(VrSettings.ComplexitySteps.Select(x => (float)x).ToArray(), av.MaxComplexity, d)));
        }
        if (Quality is { } q)
            rows.Add((MenuRow.Switch("avatar physics", q.AvatarPhysics), _ => q.AvatarPhysics = _s.AvatarPhysics = !q.AvatarPhysics));
        if (_api?.SwitchRow("lip_sync", "lip sync") is { } lips) rows.Add(lips);
        if (_api?.SwitchRow("selection_beams", "selection beams") is { } beams) rows.Add(beams);
        return rows;
    }

    static readonly float[] BeaconWidths = { 1, 2, 3, 4, 6, 8, 12, 16 };

    /// <summary>The viewer's Beacons and Highlights: the switch, then (while it's on) what gets them and how they're
    /// drawn; Highlight Transparent and the tracking beacon either way.</summary>
    List<(MenuRow Row, Action<int> Act)> BeaconRows()
    {
        var rows = new List<(MenuRow, Action<int>)>();
        if (_api is not { } api) return rows;
        void Add((MenuRow, Action<int>)? r) { if (r is { } x) rows.Add(x); }
        Add(api.SwitchRow("beacons", "beacons"));
        if (api.IsOn("beacons"))
        {
            Add(api.SwitchRow("beacons_scripted", "scripted"));
            Add(api.SwitchRow("beacons_touch", "touch"));
            Add(api.SwitchRow("beacons_physical", "physical"));
            Add(api.SwitchRow("beacons_sounds", "sounds"));
            Add(api.SwitchRow("beacons_particles", "particles"));
            Add(api.SwitchRow("beacons_media", "media"));
            Add(api.SwitchRow("beacon_lines", "lines"));
            if (api.IsOn("beacon_lines")) Add(api.NumberRow("beacon_width", "line width", BeaconWidths, f => $"{f:0} px"));
            Add(api.SwitchRow("beacon_highlights", "highlights"));
            Add(api.SwitchRow("sun_beacon", "sun"));
            Add(api.SwitchRow("moon_beacon", "moon"));
        }
        Add(api.SwitchRow("highlight_transparent", "highlight transparent"));
        Add(api.SwitchRow("tracking_beacon", "tracking beacon"));
        Add(api.SwitchRow("tracking_beep", "tracking beep"));
        return rows;
    }

    /// <summary>The headset's menus against the world: over walls or hidden by them, how, and on the PC's window.</summary>
    List<(MenuRow Row, Action<int> Act)> MenuRows()
    {
        var rows = new List<(MenuRow, Action<int>)>
        {
            (MenuRow.Switch("menus over walls", _s.MenusOverWalls), _ => _s.MenusOverWalls = !_s.MenusOverWalls),
        };
        if (!_s.MenusOverWalls)
        {
            rows.Add((MenuRow.Choice("wall test", VrSettings.MenuWallTests, Math.Max(0, Array.IndexOf(VrSettings.MenuWallTests, _s.MenuWallTest))),
                i => _s.MenuWallTest = VrSettings.MenuWallTests[i]));
            rows.Add((new MenuRow("frost bias", _s.MenuFrostBias == 0 ? "none" : $"{_s.MenuFrostBias * 100:+0;-0}%", true),
                d => _s.MenuFrostBias = Step(VrSettings.MenuFrostBiases, _s.MenuFrostBias, d)));
        }
        rows.Add((MenuRow.Switch("menus on desktop", _s.MirrorMenus), _ => _s.MirrorMenus = !_s.MirrorMenus));
        return rows;
    }

    // --- you and social ---------------------------------------------------------------------------------------------

    /// <summary>How the person shows and who reaches them (People/PeopleWiring.cs), the AO, and fixes for their own
    /// avatar drawn wrong.</summary>
    List<(MenuRow Row, Action<int> Act)> YouRows()
    {
        var rows = PeopleRows?.Invoke() ?? new();
        if (_api is not { } api) return rows;
        if (api.SwitchRow("photo_camera", "camera") is { } camera) rows.Add(camera);
        if (api.Has("ao")) rows.Add(MenuRow.Group("AO", AoRows, api.IsOn("ao") ? "on" : ""));
        // SojournAvatars' fixes, as the person's own press (the assistant's own calls go through the dispatcher)
        rows.Add(Fix("skeleton", "reset", "resetSkeleton"));
        rows.Add(Fix("attachments", "refresh", "refreshAttachments"));
        rows.Add(Fix("animations", "resync", "resyncAnimations"));
        return rows;
    }

    /// <summary>The AO (Inventory/InventoryWiring.cs, Agents/AoDriver.cs): on, which set, stands, the collars' switches,
    /// its folder locked.</summary>
    List<(MenuRow Row, Action<int> Act)> AoRows()
    {
        var rows = new List<(MenuRow, Action<int>)>();
        if (_api is not { } api) return rows;
        void Add((MenuRow, Action<int>)? r) { if (r is { } x) rows.Add(x); }
        Add(api.SwitchRow("ao", "AO"));
        var ao = api.Session.Ao;
        var (sets, current) = ao.Run(e => (e.Sets.Select(x => x.Name).ToArray(), e.Current?.Name));
        if (sets.Length > 0)
            rows.Add((new MenuRow("set", Short(current ?? "", 22), true), d =>
            {
                int i = Array.IndexOf(sets, current);
                try { ao.Select(ao.SetNamed(sets[((i < 0 ? 0 : i) + (d == 0 ? 1 : d) + sets.Length) % sets.Length])); }
                catch (Sojourn.Core.Leap.LeapError e) { _say("menu: " + e.Message); }
            }));
        Add(api.SwitchRow("ao_stands", "stands"));
        Add(api.SwitchRow("ao_opencollar", "OpenCollar"));
        Add(api.SwitchRow("ao_lockmeister", "LockMeister"));
        Add(api.SwitchRow("ao_lock_folders", "lock its folder"));
        return rows;
    }

    static string Short(string s, int n) => s.Length > n ? s[..(n - 1)].TrimEnd() + "…" : s;

    // a fix's key while it runs and just after: "…", then "done" or "failed" for a moment
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _fixing = new();

    (MenuRow Row, Action<int> Act) Fix(string label, string verb, string op) =>
        (new MenuRow(label, _fixing.TryGetValue(op, out var now) ? now : verb, false), press =>
        {
            if (_fixing.ContainsKey(op) || _api?.Session.Api.ApiMap.GetValueOrDefault("SojournAvatars") is not { } avatars
                || !avatars.Ops.TryGetValue(op, out var handler)) return;
            _fixing[op] = "…";
            _ = Task.Run(async () =>
            {
                string shown = "done";
                try
                {
                    var r = await handler.Run(new System.Text.Json.Nodes.JsonObject(), CancellationToken.None);
                    if (r is System.Text.Json.Nodes.JsonObject o && o["error"] is { } error) { shown = "failed"; _say($"menu: {op}: {error}"); }
                }
                catch (Exception e) { shown = "failed"; _say($"menu: {op}: {e.Message}"); }
                _fixing[op] = shown;
                Changed();
                await Task.Delay(2500);
                _fixing.TryRemove(KeyValuePair.Create(op, shown));
                Changed();
            });
        });

    static readonly float[] TipDelays = { 0.3f, 0.7f, 1.5f };
    static readonly float[] RadarAges = { 0, 1, 3, 7, 14, 30, 60, 90, 180, 365 };

    /// <summary>Other people on screen: names, chat and floating text (the client's), then the social layer's
    /// (Social/SocialWiring.cs): bubbles, tags, hover tips, alerts, the radar.</summary>
    List<(MenuRow Row, Action<int> Act)> SocialRows()
    {
        var rows = new List<(MenuRow, Action<int>)>
        {
            (MenuRow.Switch("name tags", _s.ShowNames), _ => _s.ShowNames = !_s.ShowNames),
            (MenuRow.Switch("chat feed", _s.ShowChat), _ => _s.ShowChat = !_s.ShowChat),
            (MenuRow.Switch("hover text", _s.ShowHoverText), _ => _s.ShowHoverText = !_s.ShowHoverText),
        };
        if (_api is not { } api) return rows;
        void Add((MenuRow, Action<int>)? r) { if (r is { } x) rows.Add(x); }
        Add(api.SwitchRow("chat_bubbles", "chat bubbles"));
        Add(api.SwitchRow("tags_through_walls", "tags through walls"));
        Add(api.SwitchRow("tag_group_titles", "group titles"));
        Add(api.SwitchRow("tag_friend_colour", "friend colour"));
        // hover tips and their delay as one row: off, or on after a short, the viewer's or a long rest
        if (api.Has("hover_tips") && api.Has("hover_tip_delay"))
        {
            float delay = float.TryParse(api.Text("hover_tip_delay"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0.7f;
            int at = !api.IsOn("hover_tips") ? 0 : 1 + Array.IndexOf(TipDelays, TipDelays.MinBy(t => MathF.Abs(t - delay)));
            rows.Add((MenuRow.Choice("hover tips", new[] { "off" }.Concat(TipDelays.Select(t => $"{t:0.#} s")).ToArray(), at), i =>
            {
                SetSwitch(api, "hover_tips", i > 0);
                if (i > 0) api.Change("hover_tip_delay", System.Text.Json.Nodes.JsonValue.Create((double)TipDelays[i - 1]));
            }));
        }
        if (api.Has("mention_alerts"))
            rows.Add(MenuRow.Group("alerts", () =>
            {
                var alerts = new List<(MenuRow Row, Action<int> Act)>();
                void A((MenuRow, Action<int>)? r) { if (r is { } x) alerts.Add(x); }
                A(api.SwitchRow("mention_alerts", "mentions"));
                A(api.SwitchRow("keyword_alerts", "keywords"));
                A(api.SwitchRow("alert_sound", "sound"));
                return alerts;
            }));
        if (api.Has("radar_chat_range"))
            rows.Add(MenuRow.Group("radar", () =>
            {
                var radar = new List<(MenuRow Row, Action<int> Act)>();
                void R((MenuRow, Action<int>)? r) { if (r is { } x) radar.Add(x); }
                var modes = Sojourn.Session.Social.SocialSettings.RadarModes;
                R(api.ChoiceRow("radar_chat_range", "chat range", modes));
                R(api.ChoiceRow("radar_draw_distance", "draw distance", modes));
                R(api.ChoiceRow("radar_region", "region", modes));
                R(api.NumberRow("radar_age_alert", "new accounts", RadarAges, f => f <= 0 ? "off" : f == 1 ? "≤ 1 day" : $"≤ {f:0} days"));
                return radar;
            }, RadarOn(api) ? "on" : ""));
        return rows;
    }

    static bool RadarOn(SettingsApi api) =>
        new[] { "radar_chat_range", "radar_draw_distance", "radar_region" }.Any(n => api.Text(n) is { } m && m != "off")
        || api.Text("radar_age_alert") is not (null or "0");

    static readonly string[] ModelNames = { "haiku", "sonnet", "opus" };

    /// <summary>The provider's state from the sidecar when it isn't working (no key, not signed in, refused...), for the
    /// first model row's note; null when it works or isn't known yet.</summary>
    string? ProviderTrouble()
    {
        var st = _assistant.ProviderStatus;
        if (st?["kind"]?.GetValue<string>() != _s.AssistantProvider) return null;
        return st["state"]?.GetValue<string>() is "ready" or "offline" or "checking" ? null : st["short"]?.GetValue<string>();
    }

    /// <summary>
    /// OpenRouter's model, when it's the provider (docs/assistant.md > Switching it on): Claude's three first, then
    /// each of OpenRouter's families' current model by its tag (Assistant/OpenRouterCatalog.cs, read live); one typed in
    /// the launcher shows as its id. Under the label, its name (for Claude's three, their tags), or the trouble. The
    /// provider itself, its key and its data setting are the launcher's.
    /// </summary>
    List<(MenuRow Row, Action<int> Act)> OpenRouterRows()
    {
        OpenRouterCatalog.Refresh();
        var models = new List<string> { OpenRouterCatalog.Claude };
        models.AddRange(OpenRouterCatalog.Quick.Select(m => m.Id));
        int at = models.IndexOf(_s.OpenRouterModel);
        if (at < 0) { models.Add(_s.OpenRouterModel); at = models.Count - 1; }   // one typed in the launcher
        var id = models[at];
        string value = id == OpenRouterCatalog.Claude ? "Claude" : id;
        string? note = id == OpenRouterCatalog.Claude ? OpenRouterCatalog.ClaudeTagLine : OpenRouterCatalog.NameOf(id);
        return new List<(MenuRow, Action<int>)>
        {
            (new MenuRow("openrouter model", value, true, Note: ProviderTrouble() ?? note),
                d => SetOpenRouterModel(models[(at + (d < 0 ? -1 : 1) + models.Count) % models.Count])),
        };
    }

    List<(MenuRow Row, Action<int> Act)> AssistantRows()
    {
        var looks = PanelLook.Presets.ToList();
        int lookAt = looks.FindIndex(p => p.Preset == _panel.Look.Preset);
        if (lookAt < 0) { looks.Add(_panel.Look); lookAt = looks.Count - 1; }   // the person's own, as the last swatch
        var rows = new List<(MenuRow, Action<int>)>
        {
            (MenuRow.Choice("model", ModelNames, (int)_s.AssistantModel), i => SetAssistantModel((AssistantModel)i, announce: false)),
            (new MenuRow("look", _panel.Look.Preset, false) { Looks = looks, Selected = lookAt },
                i => { if (i < PanelLook.Presets.Count) SetLook(PanelLook.Presets[i]); }),
            (MenuRow.Switch("adaptive ink", _s.AdaptiveInk), _ => _s.AdaptiveInk = !_s.AdaptiveInk),
            (MenuRow.Switch("spoken replies", _s.SpokenReplies), _ => { _s.SpokenReplies = !_s.SpokenReplies; _assistant.PrefsChanged(); }),
            (MenuRow.Switch("memory", _s.AssistantMemory), _ => { _s.AssistantMemory = !_s.AssistantMemory; _assistant.PrefsChanged(); }),
        };
        // under the model: the effort of each model the mode uses (all three on the agent API)
        var levels = AssistantEfforts.Levels;
        rows.InsertRange(2, AssistantEfforts.InMode(_s.AssistantModel).Select(m => ((MenuRow, Action<int>))(
            MenuRow.Choice($"{m} effort", levels, Array.IndexOf(levels, _s.AssistantEffort.Get(m))), i => SetAssistantEffort(m, levels[i]))));
        // the model rows follow the provider chosen in the launcher: Claude's (account or key) as they are; with
        // OpenRouter its own choices, and Claude's model and effort rows only for Claude through it (another maker's
        // model takes everything itself)
        if (_s.AssistantProvider == "openrouter")
        {
            if (_s.OpenRouterModel != "claude") rows.RemoveAll(r => r.Item1.Label == "model" || r.Item1.Label.EndsWith(" effort"));
            rows.InsertRange(0, OpenRouterRows());
        }
        else if (ProviderTrouble() is { } trouble) rows[0] = (rows[0].Item1 with { Note = trouble }, rows[0].Item2);
        // soniox (the cloud) only once the person has put its key in place
        var hearings = VrSettings.SonioxKey() || _s.Hearing == "soniox" ? VrSettings.Hearings : VrSettings.Hearings[..2];
        rows.Add((MenuRow.Choice("hearing", hearings, Math.Max(0, Array.IndexOf(hearings, _s.Hearing))),
            i => { _s.Hearing = hearings[i]; _assistant.PrefsChanged(); }));
        // what it remembers, cleared by the person's own two presses (the second within this menu, nothing between)
        if (AssistantHost.MemoryKept())
            rows.Add((new MenuRow("forget memory", _forgetArmed ? "sure?" : "clear", false),
                _ => { if (_forgetWasArmed) _assistant.ClearMemory(); else _forgetArmed = true; }));
        // a name and a character exist once the person has asked for one; here they're switched on and off
        if (_s.AssistantName is { } name)
            rows.Add((MenuRow.Switch($"called {name}", _s.UseAssistantName), _ => { _s.UseAssistantName = !_s.UseAssistantName; _assistant.PrefsChanged(); }));
        if (_s.AssistantCharacter is { } character)
            rows.Add((MenuRow.Switch("its character", _s.UseAssistantCharacter, note: Quote(character)),
                _ => { _s.UseAssistantCharacter = !_s.UseAssistantCharacter; _assistant.PrefsChanged(); }));
        rows.Add((MenuRow.Switch("ask before touching others' things", _s.ConfirmTouches), _ => _s.ConfirmTouches = !_s.ConfirmTouches));
        // every row, or only what's reached for by hand (MenuGuide)
        rows.Add((MenuRow.Switch("advanced menu", _s.AdvancedMenu), _ => SetAdvanced(!_s.AdvancedMenu)));
        return rows;
    }

    List<(MenuRow Row, Action<int> Act)> VoiceRows()
    {
        var v = _voice!;
        var rows = new List<(MenuRow, Action<int>)>
        {
            (MenuRow.Switch("voice chat", _s.VoiceEnabled, note: v.Status), _ => { _s.VoiceEnabled = !_s.VoiceEnabled; v.Enabled = _s.VoiceEnabled; }),
            (MenuRow.Choice("talking", new[] { "hold to talk", "open mic" }, _s.VoiceMode == global::Sojourn.Session.Voice.VoiceMode.OpenMic ? 1 : 0),
                i => { _s.VoiceMode = i == 1 ? global::Sojourn.Session.Voice.VoiceMode.OpenMic : global::Sojourn.Session.Voice.VoiceMode.PushToTalk; v.Mode = _s.VoiceMode; }),
        };
        // unmuting here is the person's own press, like the button
        if (_s.VoiceMode == global::Sojourn.Session.Voice.VoiceMode.OpenMic)
            rows.Add((MenuRow.Switch("mic on", !v.Muted), _ => v.ToggleMute()));
        rows.Add((new MenuRow("volume", $"{_s.VoiceVolume * 100:0}%", true), d => { _s.VoiceVolume = Step(VrSettings.VoiceVolumes, _s.VoiceVolume, d); v.SetVolume(_s.VoiceVolume); }));
        // where voice is heard from (Voice/VoiceWiring.cs): the viewer's camera is the head in the headset
        if (_api?.ChoiceRow("voice_hear_from", "hear from", new[] { "camera", "avatar", "mixed" },
                _assistant.InHeadset ? new[] { "head", "avatar", "mixed" } : null) is { } ears)
            rows.Add(ears);
        return rows;
    }

    void SetLook(PanelLook look) { _s.PanelLook = look; _panel.Look = look; }

    static string Quote(string s) => "“" + (s.Length > 48 ? s[..48].TrimEnd() + "..." : s) + "”";

    static float Step(float[] values, float now, int dir)
    {
        if (dir == 0) dir = 1;
        int i = Array.FindIndex(values, v => v >= now - 0.01f);
        if (i < 0) i = values.Length - 1;
        else if (values[i] > now + 0.01f && dir > 0) i--;   // between two: + goes to the upper one
        return values[Math.Clamp(i + dir, 0, values.Length - 1)];
    }

    // behind the help key: the controls that nothing on screen shows
    static readonly string[] HeadsetHelp =
    {
        "hold the right grip and speak to the assistant",
        "A: a quick press turns the pointer on or off, held it shows while held · the right trigger touches, sits and presses",
        "X hides everything and brings it back, held: every menu back in front · Y recentres · this menu: the left menu button",
        "the right stick up jumps · pointing at something that scrolls, it scrolls it",
        "drag any menu anywhere, drop it on its outline to have it follow",
        "seated: sit up straight, then press Y to set your height",
        "voice: hold the left grip to talk · open mic: the right stick's click mutes",
        "both grips held: dictate words for people, written here and never heard by the assistant",
        "you > camera (or ask): with the camera out, the right trigger takes pictures",
    };

    static readonly string[] DesktopHelp =
    {
        "hold T and speak to the assistant, or press Enter to type",
        "Esc closes whatever is open, then opens this (and goes back out of it)",
        "Tab hides everything and brings it back · F8 changes the model",
        "voice: hold B or the middle mouse button to talk · open mic: B mutes",
        "P: incognito · hold Y: dictate words for people, never heard by the assistant · Ctrl+`: a picture",
    };

    bool _help;

    int _page;          // the section's page of rows (UiSize: a smaller menu fits more a page)
    float _maxHeight, _wide = 1;

    /// <summary>
    /// Tests (SojournSettings menuShots, under SOJOURN_TEST_MENU=1): on the main thread, every section's rows and every
    /// group's as they stand; the result draws each page of each, as the headset's menu at the current size, into a
    /// folder (menu-&lt;section&gt;[-&lt;group&gt;]-&lt;page&gt;.png) and names them.
    /// </summary>
    public Func<string, List<string>> Shots()
    {
        var sections = Sections(all: true);
        var tabs = sections.Select(x => x.Name).ToList();
        var pages = new List<(string Name, int Section, List<MenuRow> Rows, string? Group)>();
        for (int i = 0; i < sections.Count; i++)
        {
            pages.Add((sections[i].Name.Replace(' ', '_'), i, sections[i].Rows.Select(r => r.Row).ToList(), null));
            foreach (var g in sections[i].Rows.Where(r => r.Row.Rows != null))
                pages.Add(($"{sections[i].Name.Replace(' ', '_')}-{g.Row.Label.ToLowerInvariant().Replace(' ', '_')}", i, g.Row.Rows!().Select(r => r.Row).ToList(), g.Row.Label));
        }
        var look = _panel.Look;
        float maxHeight = 44 / UiSize.DegPerPx, wide = UiSize.Wide;
        return dir =>
        {
            Directory.CreateDirectory(dir);
            var files = new List<string>();
            foreach (var (name, section, rows, group) in pages)
                for (int page = 0; page < 8; page++)
                {
                    var r = Panel.DrawMenu(look, tabs, section, rows, null, -1, 1.5f, 1, page, maxHeight, wide, group: group);
                    string file = $"menu-{name}-{page + 1}";
                    PanelShots.ShotOver(dir, file, r, null, 1.5f);
                    files.Add(file + ".png");
                    if (!r.Keys.Any(k => k.Kind == KeyKind.Page && k.Index == page + 1)) break;
                }
            return files;
        };
    }

    /// <summary>Tests (SojournSettings menuPress): a row pressed as PressUp would (part: 0 the row or a switch, -1 and 1
    /// a stepper, 3 + i option i), saved; the row's value after, or why not.</summary>
    public string Press(string section, string? group, string label, int part)
    {
        var rows = Sections(all: true).FirstOrDefault(x => x.Name == section).Rows ?? throw new Sojourn.Core.Leap.LeapError($"no section {section}");
        if (group != null)
            rows = rows.FirstOrDefault(r => r.Row.Rows != null && r.Row.Label == group).Row?.Rows?.Invoke() ?? throw new Sojourn.Core.Leap.LeapError($"no group {group}");
        var (row, act) = rows.FirstOrDefault(r => r.Row.Label == label);
        if (row == null) throw new Sojourn.Core.Leap.LeapError($"no row {label} (rows: {string.Join(", ", rows.Select(r => r.Row.Label))})");
        act(part >= 3 ? part - 3 : part);
        Interlocked.Increment(ref _version);
        Save();
        var again = (group == null ? Sections(all: true).First(x => x.Name == section).Rows
            : Sections(all: true).First(x => x.Name == section).Rows.First(r => r.Row.Label == group).Row.Rows!()).FirstOrDefault(r => r.Row.Label == label).Row;
        return again?.Value ?? "";
    }

    /// <summary>The rows on screen: the section's, or its open group's (a group no longer there closes).</summary>
    List<(MenuRow Row, Action<int> Act)> RowsOf(List<(MenuRow Row, Action<int> Act)> section)
    {
        if (_group != null && section.FirstOrDefault(r => r.Row.Rows != null && r.Row.Label == _group).Row?.Rows is { } open) return open();
        _group = null;
        return section;
    }

    /// <summary>The menu's picture; maxHeight: the tallest it may be in logical px (a longer section pages).</summary>
    public (OverlayImage Image, Panel.Raster Raster)? Frame(float scale, float maxHeight = float.MaxValue, float wide = 1)
    {
        if (!Open) return null;
        long key = _version * 64 + _hover + 1;
        int ink = _panel.Adaptive?.StepOf(InkSheet.Panel) ?? -1;   // adaptive ink's choice for the panel's layer
        var tipNow = _tip;
        if (!_drawing && (key != _drawnKey || ink != _drawnInk || scale != _scale || maxHeight != _maxHeight || wide != _wide || tipNow != _drawnTip))
        {
            _drawing = true;
            _drawnKey = key;
            _drawnTip = tipNow;
            _drawnInk = ink;
            _scale = scale;
            _maxHeight = maxHeight;
            _wide = wide;
            int page = _page;
            var sections = Sections();
            _section = Math.Clamp(_section, 0, sections.Count - 1);
            var tabs = sections.Select(x => x.Name).ToList();
            int section = _section;
            var rows = RowsOf(sections[section].Rows).Select(r => r.Row).ToList();
            string? group = _group;
            string view = $"{section}/{group}/{page}";
            // the tip, if it's for what's drawn now (a press can have moved the menu on since)
            (int, string)? tip = tipNow is { } t && t.View == view ? (t.Spot, t.Text) : null;
            var help = _help ? (_assistant.InHeadset ? HeadsetHelp : DesktopHelp) : null;
            var look = _panel.LookFor(InkSheet.Panel);
            int hover = _hover;
            long version = _version;
            Task.Run(() =>
            {
                try
                {
                    var r = Panel.DrawMenu(look, tabs, section, rows, help, hover, scale, version, page, maxHeight, wide, group: group, tip: tip);
                    _latest = new Drawn(new OverlayImage(r.Rgba, r.Width, r.Height, Interlocked.Increment(ref _imageVersion), r.Frost, r.Edge), r, view);
                }
                finally { _drawing = false; }
            });
        }
        _shown = _latest;
        return _shown is { } d ? (d.Image, d.Raster) : null;
    }

    /// <summary>The key at a point (logical panel coordinates) of the drawn menu, or -1.</summary>
    public int KeyAt(Vector2 logical)
    {
        if (_shown is not { } l) return -1;
        var keys = l.Raster.Keys;
        for (int i = 0; i < keys.Count; i++)
            if (keys[i].Rect.Contains(logical.X, logical.Y)) return i;
        return -1;
    }

    /// <summary>Each frame (main thread): the key under the pointer, and where it is on the menu (logical px; null: not
    /// on it), for the tips.</summary>
    public void Hover(int key, Vector2? at = null)
    {
        _hover = key;
        Rest(at);
    }

    // the tips: resting the pointer on a row (or tab) for TipDelayMs shows what it does (MenuGuide); it goes as soon as
    // the pointer leaves it. Crossing rows never shows one, so nothing flickers.
    const long TipDelayMs = 500;
    static readonly (string View, int Spot) Nowhere = ("", int.MinValue);
    (string View, int Spot) _restOn = Nowhere;
    long _restSince;
    (string View, int Spot, string Text)? _tip, _drawnTip;

    void Rest(Vector2? at)
    {
        var on = Nowhere;
        if (at is { } p && _shown is { Raster.Spots: { } spots } d)
            foreach (var (target, rect) in spots)
                if (rect.Contains(p.X, p.Y)) { on = (d.View, target); break; }
        long now = Environment.TickCount64;
        if (on != _restOn) { _restOn = on; _restSince = now; _tip = null; return; }
        if (_tip != null || on == Nowhere || now - _restSince < TipDelayMs) return;
        _restSince = long.MaxValue / 2;   // asked once per rest, whether or not it has a tip
        if (TipFor(on.Spot) is { } text) _tip = (on.View, on.Spot, text);
    }

    /// <summary>What a row on screen (its index) or tab (-1 - its index) does.</summary>
    string? TipFor(int spot)
    {
        var sections = Sections();
        if (sections.Count == 0) return null;
        int section = Math.Clamp(_section, 0, sections.Count - 1);
        if (spot < 0) return -1 - spot < sections.Count ? MenuGuide.Tip(sections[-1 - spot].Name, null, null) : null;
        var rows = RowsOf(sections[section].Rows);
        return spot < rows.Count ? MenuGuide.Tip(sections[section].Name, _group, rows[spot].Row) : null;
    }

    /// <summary>The section on screen goes a page of rows at a time (its ‹ › keys are drawn).</summary>
    public bool Paged => Open && _shown is { } l && l.Raster.Keys.Any(k => k.Kind == KeyKind.Page);

    /// <summary>The right stick's flick (Control/SheetStick): the page its ‹ or › key would turn to (+1 next, -1 back),
    /// where that key is drawn; only on a drawing of the menu as it is now, so a second flick waits for the redraw.</summary>
    public bool Turn(int dir)
    {
        if (!Open || _shown is not { } l || l.Raster.Version != Interlocked.Read(ref _version)) return false;
        // › sits right of the page dots, ‹ left (a page held over from a larger size may be past the last one drawn)
        foreach (var k in l.Raster.Keys)
            if (k.Kind == KeyKind.Page && (k.Rect.MidX > l.Raster.LogicalWidth / 2) == (dir > 0))
            {
                _page = k.Index;
                Used();
                Interlocked.Increment(ref _version);
                return true;
            }
        return false;
    }

    public void PressDown(int key)
    {
        _down = null;
        if (key >= 0) Used();
        _restOn = Nowhere;   // a press puts the tip away; resting on after it brings it back
        _tip = null;
        if (_shown is not { } l || key < 0 || key >= l.Raster.Keys.Count) return;
        var k = l.Raster.Keys[key];
        _down = (k.Kind, k.Index, l.Raster.Version);
    }

    public void PressUp(int key)
    {
        var down = _down;
        _down = null;
        if (down is not { } d || _shown is not { } l || key < 0 || key >= l.Raster.Keys.Count) return;
        var k = l.Raster.Keys[key];
        // the same key of the same drawing it went down on, and the menu as it was drawn (rows can come and go)
        if (k.Kind != d.Kind || k.Index != d.Index || l.Raster.Version != d.Version) return;
        if (k.Kind == KeyKind.Hide) { Close(); return; }
        if (k.Kind == KeyKind.Help) { _help = !_help; Interlocked.Increment(ref _version); return; }
        if (l.Raster.Version != _version) return;   // the menu changed under the press
        if (k.Kind == KeyKind.Tab)
        {
            // a section's tab, or the way back out of a group (to the page it was opened from)
            _page = _group != null && k.Index == _section ? _groupFrom : 0;
            _section = k.Index;
            _group = null;
            _forgetArmed = false;
            Interlocked.Increment(ref _version);
            return;
        }
        if (k.Kind == KeyKind.Page) { _page = k.Index; Interlocked.Increment(ref _version); return; }
        if (k.Kind != KeyKind.Menu) return;
        var sections = Sections();
        if (_section >= sections.Count) return;
        var rows = RowsOf(sections[_section].Rows);
        int row = k.Index / 16, part = k.Index % 16;
        if (row >= rows.Count || !rows[row].Row.Enabled) return;
        if (rows[row].Row.Rows != null)
        {
            _groupFrom = _page;
            _group = rows[row].Row.Label;
            _page = 0;
            Interlocked.Increment(ref _version);
            return;
        }
        _forgetWasArmed = _forgetArmed;
        _forgetArmed = false;   // any press but a forget row's second disarms it
        rows[row].Act(part >= 3 ? part - 3 : part == 1 ? -1 : part == 2 ? 1 : 0);
        Interlocked.Increment(ref _version);
        Save();
    }
}
