using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;
using Sojourn.Core.Config;
using Sojourn.Render.Realtime;
using Sojourn.Session.Access;
using Sojourn.Session.Assistant;
using Sojourn.Session.Collision;
using Sojourn.Session.Audio;
using Sojourn.Session.Bridge;
using Sojourn.Session.Building;
using Sojourn.Session.Capture;
using Sojourn.Session.Control;
using Sojourn.Session.Inventory;
using Sojourn.Session.Land;
using Sojourn.Session.Offscreen;
using Sojourn.Session.People;
using Sojourn.Session.Private;
using Sojourn.Session.Release;
using Sojourn.Session.Social;
using Sojourn.Session.Voice;

namespace Sojourn.Session;

/// <summary>
/// One person's session, for an app of ours to build its interface on: logging and the session log, the core's
/// registry with the one agent, the person's saved settings, the agent API, the assistant (host, sidecar,
/// confirmations), voice, world audio, the feed's wiring, incognito, people, land, capture, the screen reader and
/// SojournSettings. What the app supplies comes in at the steps: whether it's in a headset, its settings menu
/// (<see cref="ISettingsMenu"/>, or none), its views' hooks (bringing notices back onto its feed, its dictation, what
/// it shows), its window for the screen reader, and a renderer when it has one.
///
/// The steps run in the order <see cref="StartAll"/> gives; the VR client (Program.cs) calls them one by one between
/// its own window, headset and drawn menus, in that same order (the order SojournSettings lists its settings in, and
/// event handlers run in). An app with no views of its own calls StartAll. Main thread for all of it.
/// </summary>
public sealed class SessionHost : IAsyncDisposable
{
    /// <summary>The options as they stand: an app with a login screen sets them again once the person has chosen
    /// (the grid, the name and the start come from the choice).</summary>
    public SessionOptions Options { get; set; }
    public ILoggerFactory Logs { get; }
    public ILogger Log { get; }
    /// <summary>This run's console log in the base dir's logs/ (null if it couldn't be opened).</summary>
    public string? SessionLogPath { get; }
    public HostConfig Config { get; }
    public AgentConfig Agent { get; }
    public ConsoleChat Chat { get; }
    public AgentRegistry Registry { get; }

    public AgentSession Session { get; private set; } = null!;
    /// <summary>The person's settings (vr-settings.json in the base dir: every account's, the menu's and the API's).</summary>
    public VrSettings Settings { get; private set; } = null!;
    public string SettingsPath { get; private set; } = "";
    public BridgeServer? Bridge { get; private set; }
    /// <summary>Uploads cost L$ on Second Life, not on our local grid.</summary>
    public bool UploadsCost { get; private set; }
    public AssistantHost Assistant { get; private set; } = null!;
    public VoiceWiring? Voice { get; private set; }
    public Sidecar? Sidecar { get; private set; }
    /// <summary>Dev mode's note for tools/dev.py (local/dev/PORT.json), withdrawn at the end.</summary>
    public string? DevFile { get; private set; }
    public AudioWiring? Audio { get; private set; }
    public SocialFeed Feed { get; private set; } = null!;
    public PrivateWiring Privacy { get; private set; } = null!;
    public PeopleWiring People { get; private set; } = null!;
    public LandWiring Land { get; private set; } = null!;
    public SocialWiring Social { get; private set; } = null!;
    public Recorder Recorder { get; private set; } = null!;
    public CaptureApi Capture { get; private set; } = null!;
    public ReportApi Reports { get; private set; } = null!;
    public AccessWiring ScreenReader { get; private set; } = null!;
    public SettingsApi SettingsApi { get; private set; } = null!;
    /// <summary>The renderer out of sight, for an app that shows no view (<see cref="StartOffscreen"/>).</summary>
    public OffscreenRenderer? Offscreen { get; private set; }

    /// <param name="renderHooks">The renderer's trackers (materials, probes, animations, particles, look-at) attach to
    /// the session's clients: an app that draws the world, or will.</param>
    /// <summary>Which app this is, for the assistant (AssistantHost.App): null the VR client, "access" Sojourn Access. Set
    /// before the assistant starts.</summary>
    public string? App { get; set; }

    public SessionHost(SessionOptions options, bool renderHooks = true)
    {
        Options = options;
        Directory.CreateDirectory(options.BaseDir);
        SessionLogPath = SessionLog.Start(options.BaseDir);
        Logs = LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
            b.SetMinimumLevel(LogLevel.Information);
            // SOJOURN_LMV_LOG=Information (or Debug, Warning) shows LibreMetaverse's own log
            b.AddFilter("LibreMetaverse", Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("SOJOURN_LMV_LOG"), out var lmv) ? lmv : LogLevel.Error);
        });
        LibreMetaverse.Logger.SetLoggerFactory(Logs, "LibreMetaverse");
        Log = Logs.CreateLogger("sojourn");

        // the session: Multitude's core with one agent, its world model and the LEAP/bridge API
        Config = new HostConfig
        {
            StateRoot = "state", CacheRoot = "cache", Channel = "Sojourn", Version = "0.1.0.0", MaxConcurrentLogins = 1,
            BaseDir = options.BaseDir,
            Grids = new() { ["grid"] = new GridConfig { LoginUri = options.LoginUri } },
        };
        Agent = new AgentConfig
        {
            Name = "pilot", First = options.First, Last = options.Last, Password = options.Password, PasswordFile = options.PasswordFile,
            Grid = "grid", Start = options.Start, AutoLogin = true, Reconnect = true, DrawDistance = options.DrawDistance,
        };
        if (renderHooks)
        {
            AgentSession.ClientHooks.Add(Sojourn.Render.World.RenderMaterialTracker.Shared.Attach);
            AgentSession.ClientHooks.Add(Sojourn.Render.World.ReflectionProbeTracker.Shared.Attach);
            AgentSession.ClientHooks.Add(Sojourn.Render.World.ObjectAnimations.Shared.Attach);
            AgentSession.ClientHooks.Add(Sojourn.Render.Particles.ParticleSystemTracker.Shared.Attach);
            AgentSession.ClientHooks.Add(Sojourn.Render.World.LookAtTracker.Shared.Attach);   // others' look-at turns their heads
        }
        Chat = new ConsoleChat();
        AgentSession.ClientHooks.Add(Chat.Attach);
        Registry = new AgentRegistry(Config, Logs, configPath: null);
    }

    void Info(string m) => Log.LogInformation("{Msg}", m);
    void Warn(string m) => Log.LogWarning("{Msg}", m);

    /// <summary>The agent joins the registry and logs in (AutoLogin); a login screen's choice is in <see cref="Agent"/>
    /// and <see cref="Config"/> by now.</summary>
    public AgentSession AddSession() => Session = Registry.Add(Agent, persist: false);

    /// <summary>The person's settings: saved per machine, the options' overrides on top for this session.</summary>
    public void LoadSettings()
    {
        SettingsPath = VrSettings.PathIn(Options.BaseDir);
        Settings = VrSettings.Load(SettingsPath, Warn);
        var st = Settings;
        // a packaged build offers no Claude account: a key or token already kept takes its place, else none is chosen
        if (Sojourn.Session.Assistant.ProviderKeys.FallbackFor(st.AssistantProvider) is { } kept)
        {
            Info($"assistant: the provider {st.AssistantProvider} isn't offered in this build; using the {kept} kept");
            st.AssistantProvider = kept;
            SaveSettings();
        }
        AudioEngine.Chosen = hs => hs ? (st.HeadsetMicrophone, st.HeadsetSpeakers) : (st.Microphone, st.Speakers);
        foreach (var change in Options.VrOverrides) change(Settings);
    }

    /// <summary>Saves the settings (the menu's and the API's changes), warning if it can't.</summary>
    public void SaveSettings() => Settings.Save(SettingsPath, Warn);

    /// <summary>The draw distance the settings keep, and the console's chat.</summary>
    public void Begin()
    {
        // the draw distance is the session's (LLViewerControl RenderFarClip), so the menu, the agent API and the
        // renderer agree
        Session.Camera.SetFarClip(Settings.DrawDistance);
        Chat.Session = Session;
        Chat.Start();
    }

    /// <summary>The agent API on the options' port (loopback); without it, no assistant's sidecar.</summary>
    public async Task StartApiAsync()
    {
        // dev mode (docs/dev-mode.md): the session that started the app stands in for the assistant; the person's words
        // reach it as `sojourn` and `dev` events, which nothing else may post
        if (Options.Dev) BridgeServer.OwnKinds = new HashSet<string> { "sojourn", "dev" };
        try
        {
            Bridge = new BridgeServer(Session, Options.BridgePort, Logs);
            await Bridge.StartAsync();
            Log.LogInformation("agent API on {Url}", Bridge.Url);
        }
        catch (Exception e) { Log.LogWarning("agent API not started on port {Port}: {Msg}", Options.BridgePort, e.Message); Bridge = null; }
    }

    /// <summary>The assistant's host: its panel's state, confirmations on the op gate (for every caller), the console's
    /// /ask. pictures: the app renders the view, so snapshot ops are served by it (LiveSnapshots, taken by its frame
    /// loop); without, they refuse.</summary>
    public void StartAssistant(bool inHeadset, bool pictures)
    {
        UploadsCost = !(Options.LoginUri.Contains("127.0.0.1") || Options.LoginUri.Contains("localhost"));
        Assistant = new AssistantHost(Session, UploadsCost)
        {
            InHeadset = inHeadset,
            App = App,
            Settings = Settings,   // the person's (the provider, memory, hearing...); the VR client's menu sets the same
            SettingsChanged = SaveSettings,
            DevToken = Options.Dev ? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) : null,
        };
        Session.Api.Register(new AssistantApi(Session, Assistant));
        Session.Api.Gate = Assistant.Confirmations.Check;
        if (pictures) Sojourn.Core.Rendering.Snapshots.Provider = Assistant.Snapshots;
        Chat.Ask = Assistant.Request;
    }

    /// <summary>Voice chat (Voice/): push-to-talk by default, never while the assistant's button is held. The app binds
    /// its buttons and settles its menu (VoiceWiring.Settle).</summary>
    public void StartVoice(bool inHeadset) => Voice = VoiceWiring.Start(Session, Options, inHeadset, () => Assistant.Typing, Log);

    /// <summary>The assistant's Python side (assistant/main.py), when the options ask and the agent API is up; dev
    /// mode's note for tools/dev.py. args: the app's command line, for the note.</summary>
    public void StartSidecar(bool inHeadset, string[] args)
    {
        Sidecar.DataDir = Options.BaseDir;
        Sidecar = Options.Assistant && Bridge != null
            ? Sidecar.Start(Options.BridgePort, inHeadset, AssistantHost.ModelName(Settings.AssistantModel), Log, Assistant.DevToken) : null;
        if (Sidecar is { } sc) Assistant.SidecarLaunched(() => sc.Running);
        DevFile = Options.Dev && Bridge != null ? DevMode.Announce(Options, args, SessionLogPath, inHeadset, Log) : null;
    }

    /// <summary>World audio (Audio/): objects' sounds, gestures, footsteps, UI sounds, the parcel's music.</summary>
    public void StartAudio(bool inHeadset) => Audio = AudioWiring.Start(Session, Options, inHeadset, Voice?.Voice, Settings, Log);

    /// <summary>Incognito (Private/): what callers of the agent API may learn while it's on. feed: the app's feed
    /// (the VR client's SocialView makes it; <see cref="StartAll"/> makes one).</summary>
    public void StartPrivate(SocialFeed feed)
    {
        Feed = feed;
        Privacy = PrivateWiring.Start(Session, Assistant, feed, Log);
    }

    /// <summary>People and me (People/): friends' confirmations, away and Do Not Disturb, typing, hover height.
    /// recall: brings waiting notices back onto the app's feed; dictation: the app's, if it has one.</summary>
    public void StartPeople(Action recall, IDictation? dictation) => People = PeopleWiring.Start(Session, Assistant, Feed, recall, dictation, Log);

    /// <summary>Land and region (Land/): their confirmations, and with a renderer the land outlined and the brush.</summary>
    public void StartLand(FrameRenderer? renderer) => Land = LandWiring.Start(Session, Assistant, renderer, Log);

    /// <summary>The social layer (Social/SocialWiring.cs): keyword alerts and mentions, radar reports, translation's
    /// plumbing, the reader's links. settings: the social settings the app's views read too.</summary>
    public void StartSocial(SocialSettings settings, Reader? reader) =>
        Social = SocialWiring.Start(Session, Feed, settings, reader, Audio?.Sounds, SettingsPath, Log);

    /// <summary>Pictures and short videos of the view saved for the person (CaptureApi, Recorder), and with a renderer
    /// bigger and placed pictures and 360s (Capture/); abuse reports; the About ops. shown: what shows the recording's
    /// card, if the app shows cards.</summary>
    public void StartCapture(IShowing? shown, FrameRenderer? renderer)
    {
        Recorder = new Recorder(Info);
        Capture = new CaptureApi(Session, Assistant.Snapshots, Recorder, shown);
        Session.Api.Register(Capture);
        if (renderer != null) CaptureWiring.Start(Session, Assistant, renderer, Capture, UploadsCost, Log);
        Reports = new ReportApi(Session, Assistant.Snapshots);
        Session.Api.Register(Reports);
        Session.Api.Register(new ReleaseApi(Session));   // About: channel, version, disclaimer (docs/tpv-policy.md)
        Assistant.Confirmations.Report = a => Reports.Find(a) is { } d ? ReportApi.Describe(d) : null;
    }

    /// <summary>The person's own screen reader (Access/; the shared part in Sojourn.Core/Access): what arrives read out
    /// through it. hwnd: the app's window (UIA's voice speaks from it); keyName: the key answering a notice's button i;
    /// voice and greeting: an app's own UIA voice and first words (AccessWiring.Start).</summary>
    public void StartScreenReader(nint hwnd, Func<bool> inHeadset, Func<int, string?>? keyName,
        Sojourn.Core.Access.IReaderVoice? voice = null, string? greeting = null) =>
        ScreenReader = AccessWiring.Start(Session, Settings, Assistant, Feed, Audio?.Music, hwnd, inHeadset, keyName, Log, voice, greeting);

    /// <summary>SojournSettings, saving the settings after any change; changed: the app's menu drawn again. Registered
    /// on the API by <see cref="RegisterSettings"/> once every part has added its own.</summary>
    public SettingsApi MakeSettingsApi(Action? changed) => SettingsApi = new SettingsApi(Session, () => { SaveSettings(); changed?.Invoke(); });

    /// <summary>Avatars, inventory and building: their ops, settings and confirmations.</summary>
    public void StartAvatarsInventoryBuilding()
    {
        // avatars (Control/AvatarsWiring.cs): others' beams and PointAt received, lip sync from voice, their settings
        AvatarsWiring.Start(Session, Voice?.Voice, SettingsApi, SettingsPath, Log);
        // inventory and the avatar's own (Inventory/InventoryWiring.cs): thumbnails, favourites, protected folders, the AO, the pose stand
        InventoryWiring.Start(Session, Assistant, SettingsApi, Log);
        // building (Building/BuildWiring.cs): SojournBuild, its confirmations, PBR ids and face counts from the renderer
        BuildWiring.Start(Session, Assistant, UploadsCost, Log);
    }

    /// <summary>The screen reader's, people's and the social layer's settings (and their menu sections, if the app
    /// has a menu).</summary>
    public void SettleLate(ISettingsMenu? menu)
    {
        ScreenReader.Settle(menu, SettingsApi);
        People.Settle(menu, SettingsApi);
        Social.Settle(SettingsApi);
    }

    public void RegisterSettings() => Session.Api.Register(SettingsApi);

    /// <summary>
    /// Every step, for an app with no views of its own (no feed sheet, menu, cards or dictation; no renderer yet):
    /// the console host in src/Sojourn.SessionProbe, and Sojourn Access to start with. The VR client calls the same
    /// steps one by one around its window and headset.
    /// </summary>
    public async Task StartAllAsync(string[] args, nint hwnd = 0, Sojourn.Core.Access.IReaderVoice? voice = null, string? greeting = null)
    {
        AddSession();
        LoadSettings();
        Begin();
        if (Options.Api) await StartApiAsync();
        StartAssistant(inHeadset: false, pictures: false);
        StartVoice(inHeadset: false);
        StartSidecar(inHeadset: false, args);
        Voice?.Settle(null, Assistant);
        StartAudio(inHeadset: false);
        var feed = new SocialFeed(Session);
        StartPrivate(feed);
        StartPeople(feed.Recall, null);
        StartLand(null);
        StartSocial(new SocialSettings(), null);
        StartCapture(null, null);
        StartScreenReader(hwnd, () => false, null, voice, greeting);
        MakeSettingsApi(null);
        Audio?.Settle(null, SettingsApi);
        Voice?.Settle(SettingsApi);
        StartAvatarsInventoryBuilding();
        SettleLate(null);
        RegisterSettings();
    }

    /// <summary>The local collision model without a GPU (<see cref="StartCollision"/>).</summary>
    public LocalCollision? Collision { get; private set; }

    /// <summary>
    /// The local collision model without a GPU (Collision/LocalCollision.cs), for an app that shows no view: what's solid
    /// around the person, from the world's prims and their physics shapes, on any PC (SojournCollision). Before
    /// <see cref="StartOffscreen"/>, which then shares its physics shape types.
    /// </summary>
    public LocalCollision StartCollision()
    {
        var clog = Logs.CreateLogger("collision");
        var model = Collision = new LocalCollision(Session, m => clog.LogInformation("{Msg}", m));
        Session.Api.Register(new CollisionApi(Session, model));
        return model;
    }

    /// <summary>
    /// The renderer out of sight (Offscreen/OffscreenRenderer.cs), for an app that shows no view but wants pictures for the
    /// assistant's look and the snapshot ops, and touches landing on faces (its own collision model answers SojournView's
    /// rays, for comparing with <see cref="Collision"/>'s). Its own thread starts the GPU (a failure there leaves the app
    /// as it was, without these). The session must have been made with renderHooks. configure: its rates and sizes,
    /// before it starts.
    /// </summary>
    public OffscreenRenderer StartOffscreen(Action<OffscreenRenderer>? configure = null)
    {
        var rlog = Logs.CreateLogger("render");
        // unseen (no textures, avatars, shadows...) unless an assistant that may ask for pictures is up or starting
        var view = Offscreen = new OffscreenRenderer(Session, Assistant.Snapshots, () => Assistant.Panel.Read(p => p.Working),
            () => Assistant.SidecarAlive || Sidecar?.Running == true,
            Path.Combine(Options.BaseDir, "cache", "render"), m => rlog.LogInformation("{Msg}", m), Collision?.Physics);
        configure?.Invoke(view);
        Sojourn.Core.Rendering.Snapshots.Provider = Assistant.Snapshots;
        Session.FindTouchSurface = view.FindTouchAsync;
        Session.FindFaceSurface = view.FindFaceAsync;
        Session.Api.Register(new ViewApi(Session, view));
        view.Start();
        return view;
    }

    /// <summary>What describing needs (Offscreen/DescribeApi.cs, SojournDescribe): pictures aimed at a subject from the
    /// renderer out of sight, when there is one; textures, profile pictures and the facts without.</summary>
    public DescribeApi? Describe { get; private set; }

    public DescribeApi StartDescribe(OffscreenRenderer? view)
    {
        Describe = new DescribeApi(Session, view);
        Session.Api.Register(Describe);
        return Describe;
    }

    /// <summary>The main thread's share of each frame or tick: settings changes the API asked for.</summary>
    public void Tick() => SettingsApi?.Drain();

    /// <summary>The end, for an app with no ordering of its own: SL's logout, the sidecar, then world audio before voice
    /// (its sound goes through voice's engine), the screen reader, people and land, and the agent API last (its port is
    /// the next run's). The VR client runs its own (Program.cs), around its headset and window.</summary>
    public async Task ShutdownAsync()
    {
        void Step(string what, Action act)
        {
            try { act(); } catch (Exception e) { Log.LogWarning("shutting down, {What}: {Msg}", what, e.Message); }
        }
        DevMode.Withdraw(DevFile);
        Step("the view out of sight", () => Offscreen?.Dispose());
        Step("the collision model", () => Collision?.Dispose());
        await Task.WhenAll(
            Task.Run(() => Step("SL's logout", () => Session?.LogoutAsync().GetAwaiter().GetResult())),
            Task.Run(() => Step("the assistant", () => Sidecar?.Dispose())),
            Task.Run(() =>
            {
                Step("world audio", () => Audio?.Dispose());
                Step("voice", () => Voice?.Voice.Dispose());
                Step("the screen reader", () => ScreenReader?.Dispose());
                Step("people", () => People?.Dispose());
                Step("land", () => Land?.Dispose());
            }));
        if (Bridge != null) await Bridge.DisposeAsync();
        Bridge = null;
    }

    public async ValueTask DisposeAsync()
    {
        await Registry.DisposeAsync();
        Logs.Dispose();
    }
}
