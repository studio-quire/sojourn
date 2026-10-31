using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sojourn.Session.Control;

/// <summary>Where the eyes are in the headset: the person's own height, or the avatar's.</summary>
public enum EyeHeightMode { Real, Avatar }

/// <summary>How the headset puts you back where your avatar really is, when walking it there can't (blocked, flying):
/// a jump to the spot (Snap: a straight cut, no black since the user dropped the blink, 2026-10-28; "jump" in the menu
/// and the API), or a gentle slide (user, 2026-10-25: make it the person's choice).</summary>
public enum CorrectionStyle { Snap, Slide }

public enum MovementMode { Predicted, Server }

/// <summary>Which model takes the assistant's requests first (assistant/brain.py MODES): Quick is Haiku and Sonnet is
/// Sonnet, each handing multi-step jobs on to Opus; Opus takes everything itself.</summary>
public enum AssistantModel { Quick, Sonnet, Opus }

/// <summary>How hard each of the assistant's models thinks (Claude Code's effort), by model; opus's covers it as the
/// planner too. Medium for all by default (the user, 2026-10-27). The sidecar takes a change live (assistant/brain.py
/// set_efforts): the session comes back up at it for its next turn.</summary>
public sealed class AssistantEfforts
{
    public static readonly string[] Models = { "haiku", "sonnet", "opus" };
    public static readonly string[] Levels = { "low", "medium", "high", "xhigh", "max" };
    const string Default = "medium";

    public string Haiku { get; set; } = Default;
    public string Sonnet { get; set; } = Default;
    public string Opus { get; set; } = Default;

    /// <summary>The model's effort (a value the file had that isn't a level reads as the default).</summary>
    public string Get(string model)
    {
        var e = model switch { "haiku" => Haiku, "sonnet" => Sonnet, "opus" => Opus, _ => null };
        return Levels.Contains(e) ? e! : Default;
    }

    public void Set(string model, string effort)
    {
        if (!Levels.Contains(effort)) throw new ArgumentException($"effort is one of {string.Join(", ", Levels)}");
        switch (model)
        {
            case "haiku": Haiku = effort; break;
            case "sonnet": Sonnet = effort; break;
            case "opus": Opus = effort; break;
            default: throw new ArgumentException($"model is one of {string.Join(", ", Models)}");
        }
    }

    /// <summary>The models the menu offers an effort for in a mode: the one that takes requests first, and opus.</summary>
    public static string[] InMode(AssistantModel m) => m switch
    {
        AssistantModel.Quick => new[] { "haiku", "opus" },
        AssistantModel.Sonnet => new[] { "sonnet", "opus" },
        _ => new[] { "opus" },
    };

    public System.Text.Json.Nodes.JsonObject ToJson()
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var m in Models) o[m] = Get(m);
        return o;
    }
}

/// <summary>Tracking on your own avatar in the headset (the hands now; the head too wherever it comes to be shown):
/// Auto, the default, gives way to animations from things around you (a dance machine, a pose ball, a seat's own sit,
/// a dance the assistant played) while they play, and tracking comes back after; On always tracks; Off leaves your
/// avatar to its animations. Claude Opus 5.5, 2026-10-26, after the user's arms didn't dance on a dance machine.</summary>
public enum HandTracking { Auto, On, Off }

/// <summary>A pose in the headset's tracking space (the room), and which space that was: a place kept in one space means
/// nothing in another.</summary>
public sealed class RoomPlace
{
    public float[] Pos { get; set; } = new float[3];
    public float[] Rot { get; set; } = { 0, 0, 0, 1 };
    public string Space { get; set; } = "";

    public static RoomPlace From(System.Numerics.Vector3 p, System.Numerics.Quaternion r, string space) =>
        new() { Pos = new[] { p.X, p.Y, p.Z }, Rot = new[] { r.X, r.Y, r.Z, r.W }, Space = space };

    public (System.Numerics.Vector3 Pos, System.Numerics.Quaternion Rot)? In(string space) =>
        Space == space && Pos is { Length: 3 } && Rot is { Length: 4 } && Pos.All(float.IsFinite) && Rot.All(float.IsFinite)
            ? (new(Pos[0], Pos[1], Pos[2]), System.Numerics.Quaternion.Normalize(new(Rot[0], Rot[1], Rot[2], Rot[3])))
            : null;
}

/// <summary>
/// The headset's comfort and movement settings, and the assistant's (set on the desktop too): the settings menu
/// (left controller's menu button, F2 on the desktop) changes them, and they're kept per machine in local/client/vr-settings.json.
/// Command-line options apply over them for a session (and are kept if the menu then saves).
/// </summary>
public sealed class VrSettings
{
    /// <summary>Playing seated: the avatar stands at its own eye height whatever the person's, and following in the room
    /// waits for a bigger lean.</summary>
    public bool Seated { get; set; }
    public bool SmoothTurn { get; set; } = true;
    /// <summary>Smooth turning at full stick, degrees a second (less stick turns proportionally slower).</summary>
    public float TurnSpeed { get; set; } = 180;
    public float SnapAngle { get; set; } = 30;
    /// <summary>Standing: the eyes at the person's own height (the avatar's feet on the real floor) or the avatar's.</summary>
    public EyeHeightMode EyeHeight { get; set; } = EyeHeightMode.Real;
    /// <summary>The left stick moves where the left hand points instead of where the head looks.</summary>
    public bool HandRelative { get; set; }
    /// <summary>How the view moves through the world (the user, 2026-10-27; docs/vr.md > The rig): server (the default,
    /// the user's pick after trying both: better than predicted for now) rides the region's avatar as drawn, a round
    /// trip behind the stick but never anywhere the region doesn't put you; predicted walks at once on a local model of
    /// walls and floors, the region's word putting it right.</summary>
    public MovementMode Movement { get; set; } = MovementMode.Server;
    /// <summary>When the view has to be put back on the avatar (VrPilot): a jump (the default) or a slide.</summary>
    public CorrectionStyle Correction { get; set; } = CorrectionStyle.Snap;
    /// <summary>Before that, teleport the avatar to where you see yourself, where that's sure to land there (the user's
    /// fail-safe, 2026-10-26; VrPilot.Teleport.cs); the jump or slide is then only the fallback.</summary>
    public bool TeleportBack { get; set; } = true;
    /// <summary>The left trigger held on open ground teleports you there (Control/PointTeleport.cs).</summary>
    public bool PointTeleport { get; set; } = true;
    /// <summary>Everything Sojourn draws over the world is hidden (X, Tab; the user's ask, 2026-10-28): the panel, menus,
    /// feed, cards, name tags, wrist tokens, boards, the pointer. Only a confirmation still shows (SettingsMenu.SetHidden).</summary>
    public bool InterfaceHidden { get; set; }
    /// <summary>The settings menu shows every row (on), or only what a person reaches for by hand (off, the default;
    /// Control/MenuGuide.cs marks which). Only the menu changes: every setting still works, and the assistant reaches all.</summary>
    public bool AdvancedMenu { get; set; }
    /// <summary>The right hand's pointer in the headset (Control/HandPointer.cs): on, it touches, sits and presses menus
    /// and is "that" for the assistant; off (the default), nothing is pointed at.</summary>
    public bool Pointer { get; set; }
    /// <summary>Looking down shows your own avatar, its arms following the controllers.</summary>
    public bool ShowBody { get; set; } = true;
    public HandTracking Tracking { get; set; } = HandTracking.Auto;
    /// <summary>What others see of where you look (Control/Gaze.cs): your head turning that way, toward the person
    /// you look at, or nothing.</summary>
    public GazeSharing Gaze { get; set; } = GazeSharing.EyeContact;
    /// <summary>Whose models the assistant runs on (assistant/provider.py; docs/assistant.md > Switching it on): "claude"
    /// (the Claude subscription Claude Code is signed in to here, the default), "anthropic" (the person's API key),
    /// "openrouter" (their OpenRouter key) or "relay" (a personal token for Studio Quire's relay, for people the studio
    /// covers). The keys themselves are never here: Assistant/ProviderKeys.cs.</summary>
    public string AssistantProvider { get; set; } = "claude";
    public static readonly string[] Providers = { "claude", "anthropic", "openrouter", "relay" };
    /// <summary>The providers a person can choose in this build: without "claude" when packaged (ProviderKeys.ClaudeOffered).</summary>
    public static string[] OfferedProviders { get; } = Sojourn.Session.Assistant.ProviderKeys.ClaudeOffered ? Providers : Providers[1..];
    /// <summary>With OpenRouter: "claude" (Claude's three models there, by the assistant's model setting), or any OpenRouter
    /// model id, which then takes every request itself; the choices offered: Assistant/OpenRouterCatalog.cs.</summary>
    public string OpenRouterModel { get; set; } = "claude";
    /// <summary>With OpenRouter: only providers that don't collect the person's data (OpenRouter's provider preference
    /// data_collection: "deny", added to each request by provider.py's proxy). On by default (the user, 2026-10-29).</summary>
    public bool OpenRouterDenyDataCollection { get; set; } = true;
    /// <summary>The assistant's sessions under Claude Code's auto mode (its safety classifier reviewing each action), the
    /// default; off: no classifier calls at all and only the tools that are safe without a reviewer (assistant/harness.py
    /// MANUAL_TOOLS; docs/assistant.md > Auto mode off). The person's own choice, in the launcher and Sojourn Access.</summary>
    public bool AssistantAutoMode { get; set; } = true;
    public AssistantModel AssistantModel { get; set; } = AssistantModel.Quick;
    public AssistantEfforts AssistantEffort { get; set; } = new();
    /// <summary>The assistant reads its replies out loud as well as showing them (off: the panel only).</summary>
    public bool SpokenReplies { get; set; }
    /// <summary>Which voice speaks them (one of the sidecar's Kokoro voices; null: its default).</summary>
    public string? AssistantVoice { get; set; }
    /// <summary>A name the person asked the assistant to use, and whether it's in use (off keeps it for later).</summary>
    public string? AssistantName { get; set; }
    public bool UseAssistantName { get; set; }
    /// <summary>A character for the assistant in the person's words, and whether it's in use (off: plain).</summary>
    public string? AssistantCharacter { get; set; }
    public bool UseAssistantCharacter { get; set; }
    /// <summary>The assistant's touches and menu answers on objects the person doesn't own wait for their press.</summary>
    public bool ConfirmTouches { get; set; } = true;
    /// <summary>The assistant keeps a memory (notes in local/assistant/home) between sittings; off: nothing's loaded or
    /// kept. The person's own switch.</summary>
    public bool AssistantMemory { get; set; } = true;
    /// <summary>What hears the person (assistant/ears.py): "parakeet" (Parakeet TDT and a name pass; the default, the
    /// user's pick 2026-10-27: quicker, so a slip is quicker to say again) or "whisper" (small.en prompted with the names
    /// around them; other languages), both on the CPU, or "soniox" (the cloud, the person's opt-in, only with their key
    /// kept encrypted beside the provider keys, Assistant/ProviderKeys.cs; Whisper stands in without it or when it's slow).</summary>
    public string Hearing { get; set; } = "parakeet";
    public static readonly string[] Hearings = { "whisper", "parakeet", "soniox" };
    /// <summary>Whether the person has put a Soniox key in place (the menu offers soniox only then).</summary>
    public static bool SonioxKey() => Sojourn.Session.Assistant.ProviderKeys.HasSoniox();
    /// <summary>Voice chat with residents (Voice/VoiceClient.cs): on or off; push-to-talk (the default, user
    /// 2026-10-25) or open mic with a mute (starts muted); how loud residents are, 1 = the viewer's own level.</summary>
    public bool VoiceEnabled { get; set; } = true;
    public global::Sojourn.Session.Voice.VoiceMode VoiceMode { get; set; } = global::Sojourn.Session.Voice.VoiceMode.PushToTalk;
    public float VoiceVolume { get; set; } = 1;
    public static readonly float[] VoiceVolumes = { 0.25f, 0.5f, 0.75f, 1, 1.25f, 1.5f, 2 };
    /// <summary>Sound (Audio/): the volume of everything heard (voice and spoken replies too), world sounds' and the
    /// parcel music's as a share of the viewer's own levels (1: its defaults, 0.5 and 0.3), and music on or off.</summary>
    public float AudioVolume { get; set; } = 1;
    /// <summary>The speakers and microphone by their Windows names, chosen apart for the desktop and the headset
    /// (null: automatic, the system's defaults on the desktop and Link's Oculus device in the headset).</summary>
    public string? Speakers { get; set; }
    public string? Microphone { get; set; }
    public string? HeadsetSpeakers { get; set; }
    public string? HeadsetMicrophone { get; set; }
    public float SoundsVolume { get; set; } = 1;
    public float MusicVolume { get; set; } = 1;
    public bool Music { get; set; } = true;
    public static readonly float[] AudioVolumes = { 0, 0.25f, 0.5f, 0.75f, 1, 1.25f, 1.5f, 2 };
    /// <summary>Media (Media/): what plays by itself (the person's own by default: their objects, HUDs and land; all
    /// that asks to, as the viewer; or none until started), its volume as a share of the viewer's level (1: its 0.3),
    /// and whether a face's sound comes from where the face is.</summary>
    public global::Sojourn.Session.Media.MediaAutoPlay MediaAutoPlay { get; set; } = global::Sojourn.Session.Media.MediaAutoPlay.Mine;
    public float MediaVolume { get; set; } = 1;
    public bool MediaPlaced { get; set; } = true;
    /// <summary>How the panel and this menu look (null: the glass preset).</summary>
    public Assistant.PanelLook? PanelLook { get; set; }
    /// <summary>Adaptive ink (Assistant/AdaptiveInk.cs): the look's ink turns light over dark places and dark over bright
    /// ones, for clear glass without the glow; off by default.</summary>
    public bool AdaptiveInk { get; set; }
    /// <summary>How big the headset's panel, menu, feed, cards, reader and HUD board are (Assistant/UiSize.cs; 1: as
    /// designed, 0.7-1.2).</summary>
    public float UiSize { get; set; } = 1;
    /// <summary>Names over people, and other people's chat, IMs and notices on screen (Social/).</summary>
    public bool ShowNames { get; set; } = true;
    public bool ShowChat { get; set; } = true;
    /// <summary>Objects' floating text (llSetText) over them (Social/FloatingText.cs).</summary>
    public bool ShowHoverText { get; set; } = true;
    /// <summary>Where the person put each menu in their room (Assistant/MenuPlaces.cs), by the placement's name (panel,
    /// menu, feed, reader, hudBoard, page, keyboard), kept between sessions; a menu not here follows. Forgotten when the
    /// headset recentres or the menus are brought back.</summary>
    public Dictionary<string, RoomPlace> MenuPlaces { get; set; } = new();
    /// <summary>The panel's and the HUD board's places as kept before every menu had one (read once into MenuPlaces).</summary>
    public RoomPlace? PanelPlace { get; set; }
    public RoomPlace? HudBoardPlace { get; set; }
    /// <summary>How loud each resident's voice is to you, by agent id (only those not at 1), and whose voices you muted
    /// for yourself (SojournVoice peerVolume, peerMute): kept between sessions, as the viewer keeps its per-speaker
    /// volumes.</summary>
    public Dictionary<string, float> PeerVolumes { get; set; } = new();
    public List<string> PeerVoiceMutes { get; set; } = new();
    /// <summary>Incognito's automatic triggers (Private/Triggers.cs): people it comes on with, places (parcels) it
    /// comes on at, and whether an IM opened on the feed turns it on. Never on the agent API (they name people and places).</summary>
    public List<Private.AlwaysPerson> PrivatePeople { get; set; } = new();
    public List<Private.AlwaysPlace> PrivatePlaces { get; set; } = new();
    public bool PrivateWhileIm { get; set; }
    /// <summary>Incognito's "let it see": the assistant sees and helps with specifics while it's on (nothing kept all the
    /// same). Off by default.</summary>
    public bool IncognitoSees { get; set; }
    /// <summary>The person's own screen reader (Access/, Sojourn.Core/Access): whether what arrives is read out through
    /// it (auto: while one runs) and which kinds.</summary>
    public Sojourn.Core.Access.AnnounceSettings ScreenReader { get; set; } = new();

    /// <summary>How far you see, metres (the viewer's RenderFarClip; also how far the region sends objects). The
    /// agent API's LLViewerControl RenderFarClip is the same setting.</summary>
    public float DrawDistance { get; set; } = 128;
    public static readonly float[] DrawDistances = { 32, 48, 64, 96, 128, 192, 256, 384, 512 };
    /// <summary>The view's quality (the menu's view section, SojournSettings): shadows, their maps' longest side (null:
    /// the command line's or the viewer's rule), reflection probes, the LOD factor, a fixed sky (region: the region's
    /// own), and in the headset the eyes' resolution (null: following the GPU, else a fixed scale of the runtime's size).</summary>
    public bool Shadows { get; set; } = true;
    public int? ShadowCap { get; set; }
    public bool Reflections { get; set; } = true;
    /// <summary>Mirrors (prims made mirrors with PRIM_REFLECTION_PROBE's mirror flag), and how sharp their reflection is:
    /// rendered at this fraction of each eye's size (Render/Realtime/FrameRenderer.Mirror.cs).</summary>
    public bool Mirrors { get; set; } = true;
    public float MirrorResolution { get; set; } = 0.5f;
    /// <summary>Flexible prims simulated (old-style hair, skirts, capes, flags swaying and hanging) and avatar physics
    /// (breast, belly and butt movement from physics wearables), as the viewer has them; both on by default.</summary>
    public bool Flexi { get; set; } = true;
    public bool AvatarPhysics { get; set; } = true;
    public float Detail { get; set; } = 1.75f;
    public static readonly float[] Details = { 0.5f, 1, 1.5f, 1.75f, 2, 2.5f, 3, 4 };
    public string Sky { get; set; } = "region";
    public static readonly string[] Skies = { "region", "sunrise", "noon", "sunset", "midnight" };
    public float? Resolution { get; set; } = 1;   // full size by default (user, 2026-10-26)
    /// <summary>The load governor (Render/Realtime/LoadGovernor.cs): while frames run long, loading slows (far new
    /// objects coming in at a rate, largest first), never stopping. Off leaves loading at full speed; video memory is
    /// governed either way.</summary>
    public bool LoadGovernor { get; set; } = true;
    /// <summary>See-through surfaces (Render/Frame/Renderer.Layers.cs): how many blended layers a pixel shades at most
    /// (0: all; fewer where the nearer ones hide what's behind), and simpler lighting for them (a small change in look).</summary>
    public int AlphaLayers { get; set; }
    public static readonly int[] AlphaLayerSteps = { 0, 4, 8, 16 };
    /// Simple is the default (the user, 2026-10-27, Luane's World: no difference they could see; there it took the
    /// blended pass from 11-17 ms to 4.4-5 ms and the GPU frame from ~29-38 to ~21-23).
    public bool SimpleAlpha { get; set; } = true;
    /// <summary>GPU options to judge in the headset (docs/rendering.md > GPU options to judge; the user's call to try them,
    /// 2026-10-27), all off by default: the sun-light map (sun shadow, SSAO) at half size; the far sun cascades fitted
    /// this much larger so one can be kept a frame while moving; casters smaller than this many texels in a cascade's
    /// map left out of it.</summary>
    public bool HalfSsao { get; set; }
    public float FarShadowMargin { get; set; }
    public static readonly float[] FarShadowMargins = { 0, 0.05f, 0.1f, 0.2f };
    public float MinCasterTexels { get; set; }
    public static readonly float[] CasterTexelSteps = { 0, 0.5f, 1, 2 };
    /// <summary>The coarse far sun cascade's map size (0: none, shadows end as in the viewer): shadows carried on past the
    /// full-detail ones and the load governor's trim, so they never end in a ring around you (docs/rendering.md > Where
    /// shadows end; Claude Opus 5.5, 2026-10-29).</summary>
    public int FarShadows { get; set; } = 1024;
    public static readonly int[] FarShadowSizes = { 0, 1024, 2048 };
    /// <summary>The narrow G-buffer formats (Render/Frame/RenderTargets.cs Narrow): read when the renderer is made, so a
    /// change applies on the next start.</summary>
    public bool NarrowGBuffer { get; set; }
    /// <summary>The eyes' depth handed to the compositor for positional reprojection (Xr/XrSession.cs DepthLayers).
    /// See-through faces write no depth, so it corrects them by what's behind them. Off by default since 2026-10-29: the
    /// user saw no difference on or off at half rate, and given depth Meta's compositor also tests the menus' quads
    /// against each other inside a patch around the view, getting their order wrong (docs/vr.md > Late frames).</summary>
    public bool ReprojectionDepth { get; set; }
    /// <summary>The headset's menus always show over the world, walls never hiding them (their footprints carved out of
    /// the depth the compositor gets, their frost untested; Render/Realtime/Overlay.Walls.cs). Off (the user's default,
    /// 2026-10-28: menus belong to the world), a wall nearer than a menu hides it, as Meta's compositor does.</summary>
    public bool MenusOverWalls { get; set; }
    /// <summary>With walls hiding menus, how: exact (the default: we hide them, drawing what's in front of a menu over it
    /// on the hands' layer, the compositor kept from hiding them, their frost tested exactly), or the compositor's own test
    /// left, the frost copying it: radial (distance from the eye), head (from between the eyes). Times 1 + MenuFrostBias
    /// for the frost (positive: walls hide it more readily). Saved as menuWallTest (menuFrostTest held the old modes).</summary>
    public string MenuWallTest { get; set; } = "exact";
    public static readonly string[] MenuWallTests = { "exact", "radial", "head" };
    public float MenuFrostBias { get; set; }
    public static readonly float[] MenuFrostBiases = { -0.2f, -0.1f, -0.05f, -0.02f, 0, 0.02f, 0.05f, 0.1f, 0.2f };
    /// <summary>In the headset, the PC's window shows its menus over the mirrored eyes as the headset does
    /// (Window/MirrorTarget.cs); off, the view alone. Incognito blanks the window either way.</summary>
    public bool MirrorMenus { get; set; } = true;
    /// <summary>Crowds (Render/World/Avatars.Crowd.cs): how many of the nearest avatars are drawn whole, the rest as
    /// impostors (0: all whole), and the rendering complexity over which an avatar is a jelly doll (0: no limit). The
    /// viewer's High-Ultra: 16 and 350,000.</summary>
    public int FullAvatars { get; set; } = 16;
    public int AvatarComplexity { get; set; } = 350_000;
    public static readonly int[] FullAvatarSteps = { 0, 4, 8, 12, 16, 24, 32, 48 };
    /// <summary>Safety and comfort (Control/SafetyWiring.cs, menu > safety): the comfort vignette when something else
    /// moves you (or any artificial motion) and how strong; keep me in place; antispam; particles at all; the aim mode
    /// for weapons and mouselook games, and whether the right hand aims rather than the head; which bumps the feed tells.</summary>
    public Vignette ComfortVignette { get; set; } = Vignette.Off;   // comfort aids are options (user, 2026-10-27)
    public float VignetteStrength { get; set; } = 0.7f;
    public bool KeepInPlace { get; set; }
    public bool Antispam { get; set; }   // off, as in Firestorm (user, 2026-10-27)
    public bool Particles { get; set; } = true;
    public AimMode Aim { get; set; } = AimMode.Auto;
    public bool AimWithHand { get; set; }
    public string BumpNotices { get; set; } = "pushes";
    /// <summary>Parcel lines (World/ParcelLines.cs, Control/EnvironmentWiring.cs): ban lines as the viewer's ShowBanLines
    /// (hide, collision: for 10 s after the region refuses you, the default, proximity: while it reports you near), and
    /// property lines (ShowPropertyLines, off by default). The region's interest list in 360 mode (off: the viewer's
    /// default, what's in front first).</summary>
    public string BanLines { get; set; } = "collision";
    public bool PropertyLines { get; set; }
    public bool Interest360 { get; set; }
    public static readonly int[] ComplexitySteps = { 0, 80_000, 150_000, 250_000, 350_000, 500_000, 1_000_000 };

    public static readonly float[] TurnSpeeds = { 60, 90, 120, 150, 180, 240, 300, 360 };
    public static readonly float[] SnapAngles = { 15, 22.5f, 30, 45, 60, 90 };

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string PathIn(string baseDir) => Path.Combine(baseDir, "vr-settings.json");

    /// <summary>The settings as the file has them (for GraphicsPresets.Match).</summary>
    public System.Text.Json.Nodes.JsonObject AsJson() => (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(this, Json)!;

    public static VrSettings Load(string path, Action<string>? log = null)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<VrSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception e) { log?.Invoke($"settings: {path} unreadable ({e.Message}); using defaults"); }
        return new();
    }

    static readonly object SaveLock = new();   // the menu saves on the main thread, the agent API and console off it

    public void Save(string path, Action<string>? log = null)
    {
        try
        {
            lock (SaveLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch (Exception e) { log?.Invoke($"settings: couldn't save {path} ({e.Message})"); }
    }
}
