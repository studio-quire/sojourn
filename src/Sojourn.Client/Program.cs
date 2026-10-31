using System.Diagnostics;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using Sojourn.Client;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Voice;
using Sojourn.Client.Window;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Core.Config;
using Sojourn.Core.World;
using Sojourn.Render;
using Sojourn.Render.Environment;
using Sojourn.Render.Frame;
using Sojourn.Render.Integration;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Vector2 = System.Numerics.Vector2;

// Sojourn: a real-time Second Life client, VR-first. One person logs in; the world renders continuously in a window,
// or in a headset (--vr, OpenXR) with the eyes mirrored to the window. See docs/client.md and docs/vr.md.
var opt = ClientOptions.Parse(args);
DeviceLoss.WaitForPrevious();   // restarted after a lost GPU: the old run lets go of the headset first
Sojourn.Client.Login.LauncherAnswers.Start(opt.LauncherAnswers);   // before anything is started from here (it isn't inherited)
if (opt.LoginShots != null)
{
    Sojourn.Client.Login.LoginSheet.Shots(opt.LoginShots);
    return 0;
}
if (opt.PanelShots != null)
{
    PanelShots.Write(opt.PanelShots, Path.Combine(opt.BaseDir, "latest.png"));
    return 0;
}
// --- the session (Sojourn.Session's SessionHost): logs, Multitude's core with one agent, its world model, the LEAP/bridge
// API, the assistant, voice, audio and the rest; its steps run below in its order, between the window's and headset's
await using var sessionHost = new SessionHost(opt);
var sessionLog = sessionHost.SessionLogPath;
LaunchState.Started(sessionLog);   // the launcher's view of this run (LaunchState.cs)
var logs = sessionHost.Logs;
var log = sessionHost.Log;
var rlog = logs.CreateLogger("render");
var host = sessionHost.Config;
var agentCfg = sessionHost.Agent;
var chat = sessionHost.Chat;
var registry = sessionHost.Registry;

// --- the window and the renderer ------------------------------------------------------------------------------
var wopt = WindowOptions.DefaultVulkan with
{
    Size = new Vector2D<int>(opt.WindowWidth, opt.WindowHeight),
    Title = "Sojourn",
    VSync = false,
    IsVisible = !Sojourn.Client.Window.Background.On,
};
using var window = Silk.NET.Windowing.Window.Create(wopt);
window.Initialize();
if (Sojourn.Client.Window.Background.On) Sojourn.Client.Window.Background.ShowWithoutFocus(window);
if (window.VkSurface is null) throw new PlatformNotSupportedException("no Vulkan surface for this window");

// the headset, when asked for and there: its runtime makes the Vulkan instance and device (and picks the GPU)
void XrLog(string m) => log.LogInformation("{Msg}", m);
var xr = opt.Vr ? XrRuntime.TryCreate(XrLog, opt.Validation, TimeSpan.FromSeconds(opt.XrWait)) : null;
FrameRenderer MakeRenderer(XrRuntime? creator)
{
    // the G-buffer's formats are fixed for the process: the narrow ones if the saved settings ask (applies on restart)
    if (VrSettings.Load(VrSettings.PathIn(opt.BaseDir)).NarrowGBuffer) Sojourn.Render.Frame.RenderTargets.Narrow = true;
    unsafe
    {
        var exts = window.VkSurface!.GetRequiredExtensions(out uint extCount);
        var instanceExts = SilkMarshal.PtrToStringArray((nint)exts, (int)extCount);
        var surface = window.VkSurface;
        return new FrameRenderer(new FrameRendererOptions
        {
            CacheDirectory = Path.Combine(opt.BaseDir, "cache", "render"),
            ShadowResolutionCap = opt.ShadowCap,
            Vulkan = new Sojourn.Render.Gpu.VulkanSetup
            {
                Validation = opt.Validation,
                InstanceExtensions = instanceExts,
                CreateSurface = instance => surface.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface(),
                Creator = creator,
            },
            Log = m => rlog.LogInformation("{Msg}", m),
        });
    }
}
FrameRenderer renderer;
try { renderer = MakeRenderer(xr); }
catch (Exception e) when (xr != null)
{
    log.LogWarning("OpenXR: the runtime couldn't make the Vulkan device ({Msg}); staying on the desktop", e.Message);
    xr.Dispose();
    xr = null;
    renderer = MakeRenderer(null);
}
log.LogInformation("renderer up on {Device}{Xr}", renderer.DeviceName, xr != null ? " (device made by the OpenXR runtime)" : "");
PassProfiler.Enabled = true;

XrSession? headset = null;
if (xr != null)
{
    try { headset = new XrSession(xr, renderer.Vk, XrLog, opt.XrScale ?? 1f, opt.Hz); }
    catch (Exception e)
    {
        log.LogWarning("OpenXR: no session ({Msg}); staying on the desktop", e.Message);
        xr.Dispose();
        xr = null;
    }
}
// the lenses' hidden area, which the renderer doesn't shade (Xr/XrLensMask.cs, Render/Frame/Renderer.Lens.cs)
var lensMask = headset != null && xr != null ? new Sojourn.Client.Xr.XrLensMask(xr, headset.Handle, XrLog) : null;

// dynamic resolution, unless a fixed --xr-scale was given: the swapchain is the recommended size, the eyes start near
// the size known to hold 90 Hz here and follow the GPU from there
EyeScale? eyeScale = null;
if (headset != null && opt.XrScale == null)
{
    // SOJOURN_EYE_START=F starts elsewhere (testing the size changes)
    float start = float.TryParse(Environment.GetEnvironmentVariable("SOJOURN_EYE_START"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var es)
        ? es : EyeScale.StartFor(headset.RecommendedWidth, headset.RecommendedHeight, 0.6f, 1f);
    eyeScale = new EyeScale(start, 0.6f, 1f);
    headset.SetRenderScale(eyeScale.Scale);
    log.LogInformation("OpenXR: eyes start at {W}x{H} (x{S:0.##}) and follow the GPU", headset.RenderWidth, headset.RenderHeight, eyeScale.Scale);
}

// the render thread (FramePipe.cs): this thread simulates each frame and hands it over; the world (scenes, avatars,
// what picks read) is this thread's between a frame's scene being built and the next post. SOJOURN_RENDER_THREAD=0
// renders on this thread instead, as the bench does
var pipe = new FramePipe(threaded: !opt.Bench && Environment.GetEnvironmentVariable("SOJOURN_RENDER_THREAD") != "0");
renderer.WorldReleased = pipe.ReleaseWorld;
renderer.OwnsWorld = () => pipe.WorldOwner == Environment.CurrentManagedThreadId;
if (headset != null) headset.BeforeEnd = pipe.Drain;
if (pipe.Threaded) log.LogInformation("rendering on a thread of its own");
// the window's size as this thread last read it (GLFW is this thread's; the render thread rebuilds the swapchain by it)
int fbWidth = window.FramebufferSize.X, fbHeight = window.FramebufferSize.Y;
// in the headset the window mirrors the eyes and must never wait for the monitor's vsync
var presenter = new Presenter(renderer.Vk, renderer.FramesInFlight, () => (fbWidth, fbHeight)) { Vsync = opt.Vsync && headset == null };
window.FramebufferResize += _ => presenter.Invalidate();
var mirror = headset != null ? new MirrorTarget(headset, presenter, renderer.LayerMirror) : null;
// menus another passes through go in parts (Xr/MenuOrder.cs); SOJOURN_MENU_PARTS=0 orders whole layers only, to compare
if (headset != null && Environment.GetEnvironmentVariable("SOJOURN_MENU_PARTS") != "0") headset.Cutter = renderer.LayerCut;

// --- the login screen (--login; Login/): an account, grid and start picked before the session exists ---------------
Sojourn.Client.Login.LoginSheet? login = null;
Silk.NET.Input.IInputContext? loginInput = null;
if (opt.Login)
{
    login = Sojourn.Client.Login.LoginStart.Prepare(opt, host, m => log.LogInformation("{Msg}", m), out var choice);
    if (choice == null)
    {
        loginInput = window.CreateInput();
        LaunchState.Set("form", headset != null);
        try
        {
            choice = new Sojourn.Client.Login.LoginScreen
            {
                Window = window, Input = loginInput, Renderer = renderer, Presenter = presenter, Pipe = pipe, Headset = () => headset, Mirror = () => mirror,
                Framebuffer = (w, h) => (fbWidth, fbHeight) = (w, h), Settings = VrSettings.Load(VrSettings.PathIn(opt.BaseDir)), Sheet = login,
                Log = m => log.LogInformation("{Msg}", m),
                Shots = opt.ShotEvery is { } se ? (se, Path.Combine(opt.BaseDir, "shots")) : null,
                HeadsetGone = () =>
                {
                    log.LogInformation("the headset session ended; carrying on on the desktop");
                    pipe.Drain();
                    renderer.WaitIdle();
                    headset?.Dispose();
                    headset = null;
                    mirror = null;
                    xr?.Dispose();
                    xr = null;
                    presenter.Vsync = opt.Vsync;
                    presenter.Blank = false;
                },
            }.Run();
        }
        catch (Exception e) when (DeviceLoss.Is(e))
        {
            log.LogError("the GPU was lost on the login screen ({Msg}); starting again", e.Message);
            var again = DeviceLoss.Relaunch(args, null);
            again.Environment[DeviceLoss.AfterVar] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            System.Diagnostics.Process.Start(again);
            if (headset != null) DeviceLoss.LetGo(() =>
            {
                try { pipe.Drain(); } catch (Exception) { }
                headset?.LetGo();
                xr?.Dispose();
            }, log);
            return DeviceLoss.ExitCode;
        }
    }
    if (choice == null)
    {
        pipe.Drain();
        pipe.Dispose();
        renderer.WaitIdle();
        headset?.Dispose();
        loginInput?.Dispose();
        presenter.Dispose();
        renderer.Dispose();
        xr?.Dispose();
        return 0;
    }
    opt = Sojourn.Client.Login.LoginStart.Apply(choice, opt, host, agentCfg);
    sessionHost.Options = opt;
}

// regions reach the renderer as the session's world model learns them (neighbours too: it draws across borders); one
// the headset's view is still in when the session lets it go stays drawn until the view leaves it (Sojourn.Session/RegionFeed.cs)
var regionFeed = new RegionFeed(registry.World, renderer);

var session = sessionHost.AddSession();
// the login's progress, the grid's ToS and MFA questions and failures, in the panel's place until the first arrival
login ??= new Sojourn.Client.Login.LoginSheet(null, () => PanelLook.InUse, m => log.LogInformation("{Msg}", m));
login.Attach(session);
var pilot = new Pilot(session, renderer.Motion);
// the headset's settings: saved per machine, the command line's on top for this session
sessionHost.LoadSettings();
var vrSettings = sessionHost.Settings;
var vrSettingsPath = sessionHost.SettingsPath;
MetaAsw.RecoverLeftOver(m => log.LogInformation("{Msg}", m));   // a run that ended without putting ASW back
if (headset != null) log.LogInformation(LagHook.WaitGpu ? "OpenXR: each frame's GPU work finishes before its images go to the runtime (SOJOURN_XR_WAIT_GPU=0 turns it off)"
    : "OpenXR: NOT waiting on each frame's GPU work before its images go to the runtime (SOJOURN_XR_WAIT_GPU=0); the view may shake under load");
if (headset != null) log.LogInformation(headset.Overlap
    ? "OpenXR: frames overlap: each is ended on a thread of its own while the next is recorded, the next one's GPU work submitted once it has ended (SOJOURN_XR_OVERLAP=0 turns it off)"
    : "OpenXR: frames in turn: each is ended before the next is recorded (SOJOURN_XR_OVERLAP=0)");
if (headset != null && xr != null) MetaAsw.Begin(xr.RuntimeName, m => log.LogInformation("{Msg}", m));
var vrPilot = new VrPilot(session, renderer.Motion, vrSettings) { Say = m => log.LogInformation("{Msg}", m), Collider = renderer.CollideRay };
if (headset != null) regionFeed.ViewRegion = () => vrPilot.Region;
// where you look, shown to others as the viewer's look-at (Control/Gaze.cs)
var gaze = new Gaze(session, vrSettings) { Say = m => log.LogInformation("{Msg}", m) };
if (sessionLog != null) log.LogInformation("this session's log: {Path}", sessionLog);
// the field of view is the session's (LLViewerControl CameraAngle), so the menu, the agent API and the renderer agree;
// the headset's lenses own it there
if (opt.FovDegrees != 60) session.State.Settings["CameraAngle"] = opt.FovDegrees * Math.PI / 180;
session.SettingRefusal = key => key == "CameraAngle" && headset != null ? "in the headset the lenses set the field of view" : null;
session.InHeadset = () => headset != null;
sessionHost.Begin();   // the saved draw distance (the session's too), the console's chat

// the agent API (dev mode: only the client posts the person's words, docs/dev-mode.md)
await sessionHost.StartApiAsync();
var bridge = sessionHost.Bridge;

// --- the assistant: panel, pointer, push-to-talk, confirmations (the client's half; assistant/ is the rest) ------
sessionHost.StartAssistant(inHeadset: headset != null, pictures: true);
bool uploadsCost = sessionHost.UploadsCost;
var assistant = sessionHost.Assistant;
// the person's own touch and sit (the left trigger, a left click in the world), and where every touch lands
var direct = new DirectTouch(session, renderer, assistant.Pointer);
var pointTeleport = new PointTeleport(vrSettings, renderer, direct);
session.FindTouchSurface = direct.FindAsync;
// voice chat (Voice/): push-to-talk by default, never while the assistant's button is held
sessionHost.StartVoice(headset != null);
var voiceWiring = sessionHost.Voice;
sessionHost.StartSidecar(headset != null, args);   // the assistant's Python side; dev mode's note
var sidecar = sessionHost.Sidecar;
var devFile = sessionHost.DevFile;
DeviceLoss.Announce(opt, session, assistant, log);   // the last run lost the GPU: say so once in world
var wayOut = new Sojourn.Session.Control.WayOut(session, log);
var standKey = new Sojourn.Client.Control.StandKey(session);
var settingsMenu = new SettingsMenu(vrSettings, vrSettingsPath, vrPilot, assistant, m => log.LogInformation("{Msg}", m));
// adaptive ink (Assistant/AdaptiveInk.cs, the person's switch): the look's sheets take light or dark ink by what's behind
var adaptiveInk = new AdaptiveInk(() => vrSettings.AdaptiveInk);
assistant.Panel.Adaptive = adaptiveInk;
// the headset's one pointer, on the right hand: off by default, A toggles it (Control/HandPointer.cs)
var hands = new HandPointer(vrSettings, () => { vrSettings.Save(vrSettingsPath, m => log.LogWarning("{Msg}", m)); settingsMenu.Changed(); },
    m => log.LogInformation("{Msg}", m));
voiceWiring?.Settle(settingsMenu, assistant);
// world audio (Audio/): objects' sounds, gestures, footsteps, UI sounds, the parcel's music
sessionHost.StartAudio(headset != null);
var worldAudio = sessionHost.Audio;
// other people's names and words on screen (Social/): information only, nothing to press
var social = new Sojourn.Client.Social.SocialView(session, vrSettings, () => assistant.Panel.Look, voiceWiring?.Voice)
{
    FeedLook = () => assistant.Panel.LookFor(InkSheet.Feed),
};
if (voiceWiring != null) voiceWiring.Tagged = social.Tags.Tagged;
// private mode (Private/): what callers of the agent API may learn while it's on
sessionHost.StartPrivate(social.Feed);
var privacy = sessionHost.Privacy;
// ...and in the person's hands: the switch, the token, its automatic triggers, dictation (Private/PrivateControls.cs)
var privateUi = Sojourn.Client.Private.PrivateControls.Start(session, privacy, assistant, social, settingsMenu, vrSettings,
    () => vrSettings.Save(vrSettingsPath, m => log.LogWarning("{Msg}", m)), headset != null);
// what the assistant shows the person (Showing/): cards and labels, information only (a close key at most)
var showing = new Sojourn.Client.Showing.ShownView(new Sojourn.Client.Showing.Shown(session, () => assistant.Panel.LookFor(InkSheet.Cards)), () => assistant.Panel.Look);
session.Api.Register(new Sojourn.Client.Showing.ShowApi(session, showing.Shown));
showing.Shown.InterfaceHidden = () => vrSettings.InterfaceHidden;   // (the assistant's context says so: nothing it shows is seen)
// the reader (Assistant/Reader.cs): notecards, the session's replies, long text; and the bell for waiting notices
var reader = Sojourn.Client.Assistant.ReaderWiring.Start(session, assistant, social, log);
// people and me (People/): friends' confirmations, away and Do Not Disturb, typing, hover height, places offered
sessionHost.StartPeople(social.Recall, privateUi.Dictation);
var people = sessionHost.People;
// land and region (Land/LandWiring.cs): their confirmations, the land outlined, the terraform brush
sessionHost.StartLand(renderer);
var land = sessionHost.Land;
var bell = new Sojourn.Client.Social.NoticeBell(social);
session.Api.Register(new Sojourn.Client.Social.FeedApi(session, social));
// the social layer (Social/SocialWiring.cs): keyword alerts and mentions, radar reports, translation's plumbing, the reader's links
sessionHost.StartSocial(social.Settings, reader.Reader);
var socialWiring = sessionHost.Social;
// pictures and short videos of the view saved for the person (Assistant/CaptureApi.cs, Recorder.cs); bigger pictures,
// placed cameras, 360s, to inventory, by email (Capture/CaptureWiring.cs); abuse reports; About (docs/tpv-policy.md)
sessionHost.StartCapture(showing.Shown, renderer);
var recorder = sessionHost.Recorder;
// pictures by hand: a camera at the left forearm in the headset, Ctrl+` on the desktop (Control/Shutter.cs)
var shutter = new Sojourn.Client.Control.Shutter(session, () => worldAudio?.Sounds, m => log.LogInformation("{Msg}", m));
// the person's HUD attachments (Hud/): over the view on the desktop, on a board in the headset; pictures and presses for the assistant
var hudView = new Sojourn.Client.Hud.HudView(session, renderer, direct);
var hudBoard = new Sojourn.Client.Hud.HudBoard(hudView, () => assistant.Panel.Look, m => log.LogInformation("{Msg}", m));
// the map board and the location line at the wrist, SojournMap (Map/MapWiring.cs, docs/map.md)
var map = Sojourn.Client.Map.MapWiring.Start(session, () => assistant.Panel.LookFor(InkSheet.Cards), vrSettingsPath, log);
// a thing's card: what can be done with it, from a still hold of the pointer's trigger on it or a right click (Things/ThingCard.cs)
var things = new Sojourn.Client.Things.ThingCard(session, assistant, () => assistant.Panel.LookFor(InkSheet.Cards), m => log.LogInformation("{Msg}", m));
direct.LongPress = (hit, _, _) => things.Show(hit, vrPilot.Region, direct.Classify(hit) == DirectAct.Touch);
direct.Holding = things.Prefetch;
(long At, Vector2 Px, PickHit? Hit, ulong Region)? deskRightDown = null;   // a right click in the world opens the card
session.Api.Register(new Sojourn.Session.Control.AvatarsApi(session, renderer.AvatarLimits));
// the sky in use and the person's own, parcel lines, the interest list (Control/EnvironmentWiring.cs)
var environment = Sojourn.Client.Control.EnvironmentWiring.Start(session, renderer, vrSettings,
    () => vrSettings.Save(vrSettingsPath, m => log.LogWarning("{Msg}", m)), log);
session.Api.Register(new Sojourn.Client.Hud.HudApi(session, hudView, () => headset != null ? hudBoard : null));
assistant.Pointer.HudUnder = () => headset == null ? hudView.DesktopUnder : null;
// web pages and video on faces, parcel media and offered pages (Media/): docs/media.md
var media = Sojourn.Client.Media.MediaWiring.Start(session, opt, renderer, assistant, vrSettings, worldAudio != null || voiceWiring?.Voice.Available == true, log);
hudView.Media = media;   // a HUD face's page takes the board's presses
// safety and comfort (Control/SafetyWiring.cs): derendering, the blacklist, antispam, keep me in place, the vignette, aiming
var safety = Sojourn.Client.Control.SafetyWiring.Start(session, vrSettings, headset != null, worldAudio, vrPilot, renderer, log);
// beacons, highlights, Highlight Transparent and the tracking beacon (Beacons/BeaconsWiring.cs)
var beacons = Sojourn.Client.Beacons.BeaconsWiring.Start(session, renderer, social, worldAudio?.Sounds, vrSettingsPath, log);
// the headset's keyboard: only when asked for, from a page board's keyboard key or the panel's, or when the assistant
// asks the person to check a word by typing
var keyboard = new HeadsetKeyboard(() => assistant.Panel.Look, m => log.LogInformation("{Msg}", m));
// the right stick on what the right ray is on, where that scrolls or pages (Control/SheetStick.cs)
var sheetStick = new SheetStick();
if (headset != null)
{
    assistant.Panel.Update(p => p.KeyboardKey = true);
    assistant.KeyboardRequested += () => keyboard.Toggle(assistant.KeyTarget());
    assistant.KeyboardWanted += keyboard.Show;   // the assistant asking them to check a word, its guess already typed
    login.ShowKeyboard = keyboard.Show;   // a field of the login sheet pressed (the MFA code, a password again)
    if (media != null)
    {
        media.View.KeyboardRequested += keyboard.Toggle;
        media.View.KeyboardOn = i => keyboard.Shown && keyboard.IsFor(i);
    }
}
settingsMenu.DrawDistance = () => session.Camera.FarClip;
settingsMenu.SetDrawDistance = m => session.Camera.SetFarClip(m);
settingsMenu.Maturity = () => (session.Maturity, session.MaturityMax);
settingsMenu.SetMaturity = m => _ = Task.Run(async () =>
{
    if (await session.SetMaturityAsync(m, CancellationToken.None) is { } why) log.LogWarning("maturity: {Why}", why);
    else log.LogInformation("maturity: {M}", m);
    settingsMenu.Changed();
});
session.SettingChanged += key =>
{
    if (key != "RenderFarClip") return;
    vrSettings.DrawDistance = session.Camera.FarClip;
    vrSettings.Save(vrSettingsPath, m => log.LogWarning("{Msg}", m));
    settingsMenu.Changed();
    log.LogInformation("draw distance {M:0} m", vrSettings.DrawDistance);
};
chat.SetModel = name =>
{
    if (Enum.TryParse<AssistantModel>(name, ignoreCase: true, out var m) && Enum.IsDefined(m)) settingsMenu.SetAssistantModel(m, announce: true);
    else Console.WriteLine($"assistant model: {AssistantHost.ModelName(assistant.Model)} ({AssistantHost.Describe(assistant.Model)}); /model quick|sonnet|opus changes it");
};
// the person's own screen reader (Access/; shared part in Sojourn.Core/Access): what arrives read out through it, keys for what waits
sessionHost.StartScreenReader(window.Native?.Win32?.Hwnd ?? 0, () => headset != null, Sojourn.Client.Access.ReaderKeys.KeyName);
var screenReader = sessionHost.ScreenReader;
screenReader.Actions.ConfirmListKey = "Control R reads the whole list.";   // (ReaderActions.Repeat)
var readerKeys = new Sojourn.Client.Access.ReaderKeys(screenReader.Announcer, screenReader.Actions);

// --- input ------------------------------------------------------------------------------------------------------
var input = loginInput ?? window.CreateInput();
var kb = input.Keyboards.FirstOrDefault();
var mouse = input.Mice.FirstOrDefault();
bool steering = false, stereo = opt.Stereo, showStats = true, focused = true;
int panelKeyUnderMouse = -1;
bool deskOnPanel = false, deskOnFeed = false;
// a left click in the world: not on the panel or a key, nothing waiting for the person, no menu
// and a hand on the mouse (RealInput): the assistant can run programs here, and a posted click mustn't touch for it
// the settings menu open and in sight (open, it stays open while the interface is hidden)
bool MenuShown() => settingsMenu.Open && !settingsMenu.Hidden;
bool DeskWorldClick() => DeskInWorld() && RealInput.LeftButton();
bool DeskInWorld() => !MenuShown() && !assistant.Confirmations.Pending && !deskOnPanel && !deskOnFeed && panelKeyUnderMouse < 0 && showing.Shown.HoverClose == null
    && !privateUi.DesktopOver && !map.DesktopOver(DeskPx()) && !things.DesktopOver(DeskPx());
Vector2 mouseAt = mouse?.Position ?? default;
// the mouse in the eye image's pixels (the desktop overlay's), from the window's
Vector2 DeskPx() => window.Size.X > 0 && window.Size.Y > 0 ? mouseAt * new Vector2((float)window.FramebufferSize.X / window.Size.X, (float)window.FramebufferSize.Y / window.Size.Y) : mouseAt;
string? captureNext = null;
double nextShot = opt.ShotEvery ?? double.MaxValue;
Vector2 lastMouse = mouse?.Position ?? default;
void SetSteering(bool on)
{
    steering = on;
    if (mouse != null && !Sojourn.Client.Window.Background.On) mouse.Cursor.CursorMode = on ? CursorMode.Raw : CursorMode.Normal;
    if (mouse != null) lastMouse = mouse.Position;
}
bool escHeld = false;
bool vrTalkGrip = false;   // the headset's right grip is down (HeadsetAssistant), so T's release or focus doesn't end its talk
// Esc on the desktop, once typing (to the assistant or in a page), the screen reader's keys and a dictated draft have
// had theirs: the mouse comes free, and what waits or is open goes, one a press, the nearest first; only when nothing
// takes it does the settings menu open. With the interface hidden (Tab), it comes back with the menu open.
void DeskEscape()
{
    SetSteering(false);
    if (reader.Reader.Lists(assistant.Panel.Read(p => p.Confirm))) { reader.Reader.Close(); return; }   // its list, then it
    if (assistant.Confirmations.Pending) { assistant.DeclineConfirm(); return; }
    if (!settingsMenu.Hidden)
    {
        if (assistant.PointAsked) { assistant.CancelPointAsk(); return; }
        if (settingsMenu.Open) { settingsMenu.Back(); return; }   // help, then out of a group, then closed
        if (media?.Escape() == true) return;                       // the up-close sheet, then a face's focus
        if (reader.Reader.Open) { reader.Reader.Close(); return; }
        if (map.World.Shown) { map.World.Show(false); return; }
        if (social.CloseOpened()) return;                          // the feed's lines opened in full
    }
    settingsMenu.Summon();
}
if (kb != null)
{
    kb.KeyChar += (_, ch) => { if (login.TakesKeys) { login.Char(ch); return; } if (assistant.Typing) assistant.Type(ch); else if (headset == null) media?.Char(ch); };
    kb.KeyUp += (_, key, _) => { if (key == Key.Escape) escHeld = false; readerKeys.KeyUp(key); privateUi.KeyUp(key); if (headset == null && media?.Key(key, false, kb) == true) return; if (key == Key.T && !vrTalkGrip) assistant.TalkRelease(); };
    kb.KeyDown += (_, key, _) =>
    {
        // Esc does one thing a press: a held one's repeats don't go on to the next layer (DeskEscape)
        if (key == Key.Escape) { if (escHeld) return; escHeld = true; }
        // the login sheet's fields and keys first (Enter answers the grid only from a hand on the keyboard)
        if (login.Active && login.Key(key, kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight),
                kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight), () => kb.ClipboardText,
                real: key is not (Key.Enter or Key.KeypadEnter) || Sojourn.Session.Private.RealKeys.Down(Sojourn.Session.Private.RealKeys.Return)))
            return;
        if (assistant.Typing)
        {
            switch (key)
            {
                case Key.Enter or Key.KeypadEnter: assistant.SubmitTyping(); break;
                case Key.Escape: assistant.CancelTyping(); break;
                case Key.Backspace: assistant.Backspace(); break;
            }
            return;
        }
        if (headset == null && media?.Key(key, true, kb) == true) return;   // typing in a page, Esc, Z (up close)
        if (readerKeys.KeyDown(key, kb)) return;   // with a screen reader: confirmations, notices, hearing again (Access/ReaderKeys.cs)
        // the interface hidden (Tab): Enter brings it back rather than typing or sending a draft out of sight
        if (settingsMenu.Hidden && key is Key.Enter or Key.KeypadEnter) { settingsMenu.SetHidden(false); return; }
        if (privateUi.KeyDown(key)) return;   // P private, Y dictation, a draft's Enter and Esc
        if (key == Key.Escape && headset == null) { DeskEscape(); return; }
        switch (key)
        {
            // while private, only a hand on the keyboard opens the mic to the assistant (not a key a program posts)
            // the viewer's View > Highlight Transparent
            case Key.T when (kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight)) && (kb.IsKeyPressed(Key.AltLeft) || kb.IsKeyPressed(Key.AltRight)):
                beacons.ToggleTransparent();
                return;
            case Key.T: if (!session.Private.On || Sojourn.Session.Private.RealKeys.Down(0x54)) assistant.TalkPress(); return;
            case Key.Tab: settingsMenu.ToggleHidden(); return;
            // the viewer's Snapshot to Disk
            case Key.GraveAccent when kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight):
                if (sessionHost.Capture is { } cap) shutter.Desktop(cap);
                return;
            case Key.F8: settingsMenu.CycleAssistantModel(); return;
            case Key.F2: if (headset == null) settingsMenu.Summon(); return;   // (the menu's old key, kept)
            // the viewer's Teleport Home, as the person's way out: stand up and go home (Control/WayOut.cs)
            case Key.H when (kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight)) && (kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight)):
                wayOut.Go("Ctrl+Shift+H");
                return;
            case Key.Enter or Key.KeypadEnter: SetSteering(false); assistant.StartTyping(); return;
            case >= Key.Number1 and <= Key.Number9: if (!settingsMenu.Hidden) assistant.PickNumber(key - Key.Number0); return;   // (no picking choices out of sight)
        }
        // in the headset the keys stand in for the controllers (VrPilot); only the tool keys work here
        if (headset != null && key is not (Key.F3 or Key.F6 or Key.F12 or Key.R)) return;
        switch (key)
        {
            case Key.R: if (headset != null) vrPilot.Recentre(); break;
            case Key.F: case Key.Home: pilot.ToggleFly(); break;
            case Key.V: case Key.M:
                pilot.ToggleMode();
                SetSteering(pilot.Mode == CameraMode.FirstPerson);
                break;
            case Key.F3: showStats = !showStats; break;
            case Key.F5: stereo = !stereo; break;
            case Key.F6: Console.WriteLine(renderer.Profiler.Report()); break;
            case Key.F7: presenter.Vsync = !presenter.Vsync; break;
            case Key.F12:
                captureNext = Path.Combine(opt.BaseDir, "shots", $"{DateTime.Now:yyyyMMdd-HHmmss}.png");
                if (headset != null) presenter.CaptureNext = Path.ChangeExtension(captureNext, null) + "-mirror.png";   // and the window as it shows
                break;
            case Key.H when (kb.IsKeyPressed(Key.AltLeft) || kb.IsKeyPressed(Key.AltRight)) && (kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight)):
                hudView.DesktopShown = !hudView.DesktopShown;   // the viewer's Show HUD Attachments
                break;
        }
    };
}
if (mouse != null)
{
    mouse.MouseDown += (_, b) =>
    {
        if (headset == null && b == MouseButton.Left && login.Active) { login.MouseDown(RealInput.LeftButton()); return; }
        // a thing's card first: a click on it is its, one elsewhere puts it away and goes on
        if (headset == null && b == MouseButton.Left && RealInput.LeftButton() && things.DesktopDown(DeskPx())) return;
        if (headset == null && b == MouseButton.Right && DeskInWorld())
            deskRightDown = (Environment.TickCount64, mouse.Position, assistant.Pointer.CurrentHit, assistant.Pointer.Ray.Region);
        if (headset == null && b == MouseButton.Left && !MenuShown() && !assistant.Confirmations.Pending && RealInput.LeftButton() && map.DesktopDown(DeskPx())) return;   // the location line, the map board (a hand on the mouse)
        if (headset == null) privateUi.MouseDown(b);
        if (headset == null && b == MouseButton.Right && pilot.Mode == CameraMode.ThirdPerson) SetSteering(true);
        if (headset == null && b == MouseButton.Left)
        {
            if (MenuShown()) settingsMenu.PressDown(panelKeyUnderMouse); else if (reader.DeskOn) reader.DeskDown(); else { assistant.PressDown(panelKeyUnderMouse); showing.PressDown(); social.PressDown(); }
        }
        // asked to point, a click in the world is the answer
        if (headset == null && b == MouseButton.Left && DeskWorldClick() && assistant.PointAsked && !settingsMenu.Hidden) { if (assistant.Pointer.Current is { } pt) assistant.Pointed(pt); }
        else if (headset == null && b == MouseButton.Left && DeskWorldClick() && (settingsMenu.Hidden || media?.SheetDown() != true) && media?.WorldDown() != true && !hudView.DesktopDown())
            direct.DesktopDown(mouselook: pilot.Mode == CameraMode.FirstPerson);
    };
    mouse.MouseUp += (_, b) =>
    {
        if (headset == null && b == MouseButton.Left && login.Active) { login.MouseUp(); return; }
        if (b == MouseButton.Right && pilot.Mode == CameraMode.ThirdPerson) SetSteering(false);
        // a right click (not a drag that steers) on a thing: its card at the click, as the viewer's right click
        if (headset == null && b == MouseButton.Right && deskRightDown is { } rd && Environment.TickCount64 - rd.At < 400
            && Vector2.Distance(rd.Px, mouse.Position) < 6 && rd.Hit is { Kind: PickKind.Object } rh && DeskInWorld())
            things.DesktopShow(rh, rd.Region, direct.Classify(rh) == DirectAct.Touch, DeskPx());
        if (b == MouseButton.Right) deskRightDown = null;
        if (headset == null && b == MouseButton.Left) things.DesktopUp(DeskPx());
        if (headset == null && b == MouseButton.Left)
        {
            if (MenuShown()) settingsMenu.PressUp(panelKeyUnderMouse); else { reader.DeskUp(); assistant.PressUp(panelKeyUnderMouse); showing.PressUp(); social.PressUp(); }
        }
        if (headset == null && b == MouseButton.Left) { map.DesktopUp(DeskPx()); media?.DesktopUp(); hudView.Release(); direct.DesktopUp(); privateUi.MouseUp(b); }
    };
    mouse.Scroll += (_, w) =>
    {
        if (headset == null && map.DesktopWheel(DeskPx(), w.Y)) return;
        if (headset == null && reader.DeskOn) reader.Reader.Wheel(w.Y);
        else if (headset == null && deskOnFeed) social.Wheel(w.Y);
        else if (headset != null || media?.Wheel(w.Y) != true) pilot.Zoom(w.Y);
    };
    // the pointer follows the cursor as the window hears of it (messages, not a poll of the system cursor)
    mouse.MouseMove += (_, p) => { mouseAt = p; if (headset == null) { map.DesktopMove(DeskPx()); things.DesktopMove(DeskPx()); } };
}
window.FocusChanged += f => { focused = f; if (!f) { pilot.Release(); SetSteering(false); if (!vrTalkGrip) assistant.TalkLost(); } };
voiceWiring?.Bind(kb, mouse);
window.FocusChanged += f => { if (!f) voiceWiring?.Release(); };
window.FocusChanged += readerKeys.Focused;   // a key held when the window comes to the front doesn't answer (Access/ReaderKeys.cs)

// --- every setting the person can change, for any caller of the agent API (the assistant too): SettingsApi.cs ------
var settingsApi = sessionHost.MakeSettingsApi(settingsMenu.Changed);   // (saves the settings after any change; the menu redrawn)
settingsApi.AddStandard(vrSettings, vrPilot, assistant, settingsMenu, renderer, () => settingsMenu.Voice, headset != null);
UiSize.Start(vrSettings, settingsApi);   // how big the headset's sheets are (menu > view > size)
worldAudio?.Settle(settingsMenu, settingsApi);
media?.Settle(settingsMenu, settingsApi);
voiceWiring?.Settle(settingsApi);   // the call switches and where voice is heard from (Voice/VoiceWiring.cs)
safety.Settle(settingsMenu, settingsApi);
environment.Settle(settingsApi);
beacons.Settle(settingsApi);
// avatars (others' beams and PointAt, lip sync from voice), inventory and the avatar's own (thumbnails, favourites,
// protected folders, the AO, the pose stand), building (SojournBuild and its confirmations)
sessionHost.StartAvatarsInventoryBuilding();
SettingsStandard.ApplySaved(vrSettings, renderer);
settingsMenu.Quality = renderer.Quality;
settingsMenu.Avatars = renderer.AvatarLimits;
settingsApi.Add("sky", new("view", "a fixed sky instead of the region's own day", string.Join(", ", VrSettings.Skies),
    () => vrSettings.Sky, SettingsApi.Choice("sky", VrSettings.Skies, i => vrSettings.Sky = VrSettings.Skies[i])));
settingsApi.Add("resolution", new("view", "in the headset, the eyes' resolution: auto follows the GPU, a number fixes it (of the headset's own size)", "auto, 0.6-1",
    () => vrSettings.Resolution is { } r ? r : "auto",
    v => v.ToString() == "auto" ? SetResolution(null) : SettingsApi.Number("resolution", 0.6f, 1f, "", f => SetResolution(f))(v)));
// the camera for pictures by hand (Control/Shutter.cs): taken out by the menu (you > camera) or the assistant, no token on the arm
if (headset != null)
{
    settingsApi.Add("photo_camera", new("you", "in the headset, the photo camera out: the two hands held up apart in front of you are opposite corners of the picture, and while they frame it the right trigger takes it (otherwise the trigger is as ever; not kept between sessions)", "on, off",
        () => shutter.Out, SettingsApi.Switch("photo_camera", on => shutter.Out = on)));
    settingsApi.Add("photo_ratio", new("you", "in the headset, the photo camera's shape: free (the hands' rectangle, the default) or a W:H it keeps (16:9, square, 4:5...), the hands setting its size by their diagonal (not kept between sessions)", "free, square, W:H (1:3 to 3:1)",
        () => shutter.Ratio, v => shutter.SetRatio(v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString())));
    settingsApi.Add("photo_pivot", new("you", "in the headset, how far below the eyes the photo camera tilts from: it's aimed from there through its centre, so hands held at that height take a level picture", "0-0.5 m",
        () => shutter.Pivot, SettingsApi.Number("photo_pivot", 0f, 0.5f, "", f => shutter.Pivot = f)));
}
// the eyes' depth handed to the headset's compositor for reprojecting (on by default; menu > view > depth reprojection)
settingsApi.Add("reprojection_depth", new("view", "in the headset, the eyes' depth goes to the compositor so late frames are reprojected by head position too (off by default: no visible difference, and with it the compositor misorders overlapping menus in a patch around the view, so it's held back while any two overlap); off, by rotation only", "on, off",
    () => vrSettings.ReprojectionDepth, v => headset is not { HasDepthLayers: true } ? "reprojection_depth: the headset's runtime has no depth layers" : SettingsApi.Switch("reprojection_depth", on => vrSettings.ReprojectionDepth = on)(v)));
// walls and the headset's menus (Render/Realtime/Overlay.Walls.cs; menu > view > menus over walls, frost test, frost bias)
settingsApi.Add("menus_over_walls", new("view", "in the headset, the menus (panel, settings, chat, cards, boards, keyboard, reader) always show over the world; off (the default), a wall or anything nearer than a menu hides it, as in the world", "on, off",
    () => vrSettings.MenusOverWalls, SettingsApi.Switch("menus_over_walls", on => vrSettings.MenusOverWalls = on)));
settingsApi.Add("menu_frost_test", new("view", "with walls hiding menus, how: exact (the default: Sojourn hides them itself, drawing whatever is in front of a menu over it, so a wall hides a menu and its glass blur only where it's really in front), or the headset compositor's own test (patchy over Link), the blur copying its apparent error: radial (by distance from the eye), head (from between the eyes)", string.Join(", ", VrSettings.MenuWallTests),
    () => vrSettings.MenuWallTest, SettingsApi.Choice("menu_frost_test", VrSettings.MenuWallTests, i => vrSettings.MenuWallTest = VrSettings.MenuWallTests[i])));
settingsApi.Add("menu_frost_bias", new("view", "with walls hiding menus, the blur under a menu is tested as if this share farther away (0.05: 5%); positive, walls hide the blur more readily, negative, less", "-0.3 to 0.3",
    () => vrSettings.MenuFrostBias, SettingsApi.Number("menu_frost_bias", -0.3f, 0.3f, "", f => vrSettings.MenuFrostBias = f)));
// the headset's menus over its mirror in the PC's window (on by default; menu > view > menus on desktop)
settingsApi.Add("mirror_menus", new("view", "in the headset, the PC's window shows the menus (panel, settings, chat, cards, boards, keyboard, reader) over the view as the headset does; off, the view alone (incognito blanks the window either way)", "on, off",
    () => vrSettings.MirrorMenus, SettingsApi.Switch("mirror_menus", on => vrSettings.MirrorMenus = on)));
settingsApi.Add("field_of_view", new("view", "the desktop view's vertical field of view (the headset's lenses set their own)", "20-120 degrees",
    () => MathF.Round(session.Camera.Fov * 180 / MathF.PI),
    v => headset != null ? "field_of_view: in the headset the lenses set it" : SettingsApi.Number("field_of_view", 20, 120, " deg", f => session.State.Settings["CameraAngle"] = f * Math.PI / 180)(v)));
settingsApi.Add("vsync", new("desktop", "the window waits for the monitor's refresh (F7)", "on, off",
    () => presenter.Vsync, v => headset != null ? "vsync: the headset paces the frames" : SettingsApi.Switch("vsync", on => presenter.Vsync = on)(v)));
settingsApi.Add("stereo", new("desktop", "two eyes side by side in the window (F5)", "on, off",
    () => stereo, v => headset != null ? "stereo: the headset shows both eyes already" : SettingsApi.Switch("stereo", on => stereo = on)(v)));
settingsApi.Add("stats", new("desktop", "frame rate and timings in the window's title (F3)", "on, off", () => showStats, SettingsApi.Switch("stats", on => showStats = on)));
settingsApi.Add("camera", new("desktop", "first person (the mouse looks) or third person (V)", "first_person, third_person",
    () => pilot.Mode == CameraMode.FirstPerson ? "first_person" : "third_person",
    v => headset != null ? "camera: in the headset your head is the camera" : SettingsApi.Choice("camera", ["first_person", "third_person"], i =>
    {
        pilot.Mode = i == 0 ? CameraMode.FirstPerson : CameraMode.ThirdPerson;
        SetSteering(pilot.Mode == CameraMode.FirstPerson);
    })(v)));
privateUi.AddSettings(settingsApi);
sessionHost.SettleLate(settingsMenu);   // the screen reader's, people's and the social layer's settings and menu sections
map.Settle(settingsApi);
if (login.FromScreen)
{
    // log out, back to the login screen: at the foot of the "you" section
    var youRows = settingsMenu.PeopleRows;
    settingsMenu.PeopleRows = () => { var rows = youRows?.Invoke() ?? new(); rows.Add(login.LogOutRow()); return rows; };
}
sessionHost.RegisterSettings();
string? SetResolution(float? scale) { vrSettings.Resolution = scale; return null; }

// --- the loop ---------------------------------------------------------------------------------------------------
// This thread is the frame: ahead of the decoders, the network and other programs for a core (a frame it loses its
// core in shows as an older frame in the headset), and the collector kept to background collections of the old
// generation rather than blocking ones while it can.
Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
UUID? localSky = SkyFor(vrSettings.Sky);
static UUID? SkyFor(string? name) => name?.ToLowerInvariant() switch
{
    "sunrise" => DefaultSettingsAssets.SunriseId,
    "noon" => DefaultSettingsAssets.MiddayId,
    "sunset" => DefaultSettingsAssets.SunsetId,
    "midnight" => DefaultSettingsAssets.MidnightId,
    _ => null,
};
var bench = opt.Bench ? new Bench(opt, renderer, session, log) : null;
// SOJOURN_FRAME_LOG=path: per frame, time, frame ms (in the headset: display time step), CPU ms, fence-wait ms, probe
// captures, assets pending, then the CPU ms of scene, avatars, probes, recording, and the rest (mostly acquiring the image),
// then the garbage collections since the last line (gen0, gen1, gen2: a gen1 counts in gen0 too) and their pause ms,
// then the main thread's CPU ms simulating the frame (input, the pilot, panels, name tags, voice, audio, media: in the
// headset from xrWaitFrame's return) and the render thread's around the renderer (xrBeginFrame; xrEndFrame without an
// image), then how long the main thread waited for the world before it (the render thread still building the last
// frame's scene, or the mailbox full; FramePipe.cs), then in the headset how far ahead of its display time xrWaitFrame
// woke the main thread for the frame (0 on the desktop). The CPU ms above are the render thread's.
using var frameLog = DiagLog.Open("SOJOURN_FRAME_LOG", autoFlush: false);
float fov = opt.FovDegrees * MathF.PI / 180f;
var clock = Stopwatch.StartNew();
double last = 0, statsAt = 0, gpuLogAt = 0;
long lastDisplay = 0;
double consoleStatsAt = 0;
int vrGpuOver = 0, vrGpuFrames = 0;   // frames over the display period on the GPU, for the 10 s pacing line
// every menu in the headset follows the head until the person puts it somewhere (Assistant/PanelPlacement.cs): the
// panel and the settings menu share a layer but each has its own placement; where they were put is kept between
// sessions in the tracking space they were put in, and X held, the menu's key or the assistant brings them all back
// (Assistant/MenuPlaces.cs)
var vrPlacement = new PanelPlacement("panel");
var menuPlacement = new PanelPlacement("menu");
MenuPlaces? menuPlaces = null;
if (headset != null)
{
    menuPlaces = new MenuPlaces(vrSettings, headset.SpaceName, () => vrSettings.Save(vrSettingsPath, m => log.LogWarning("{Msg}", m)),
        m => log.LogInformation("{Msg}", m));
    foreach (var p in new[] { vrPlacement, menuPlacement, social.Placement, reader.Placement, hudBoard.Placement, keyboard.Placement }) menuPlaces.Add(p);
    if (media != null) menuPlaces.Add(media.View.Placement);
    menuPlaces.Add(map.World.Placement);
    menuPlaces.Add(map.Mini.Placement);
    showing.Places = menuPlaces;
    settingsMenu.Places = menuPlaces;
    menuPlaces.PlacesChanged += settingsMenu.Changed;   // (its reset key shows while any is put somewhere)
    assistant.ResetMenus = menuPlaces.Reset;
}
bool vrPanelWasVisible = false, vrTriggerDown = false, vrMenuShown = false, vrConfirmShown = false, vrRayOnPanel = false;
bool vrWorldHand = false;   // the pointer is out in the world freely this frame: the world's touch and sit have the hand
var vrPanelPosBefore = System.Numerics.Vector3.Zero;
(System.Numerics.Vector3, System.Numerics.Quaternion, Vector2)? vrPanelPlane = null;   // the panel's layer this frame (the left ray stops there)
bool vrShownDown = false;
if (headset != null) assistant.BringPanel = () => (vrMenuShown ? menuPlacement : vrPlacement).BringToFront();
XrControllers vrButtonsBefore = default;
double vrMenuHeldSince = -1;   // the left menu button: a short press opens the settings, held it's the way out
double vrXHeldSince = -1;      // X: a tap hides or shows the interface, held it brings every menu back
bool panelInEyes = Environment.GetEnvironmentVariable("SOJOURN_PANEL_IN_EYES") == "1";
var selfTest = Environment.GetEnvironmentVariable("SOJOURN_SELF_TEST") is { Length: > 0 } stMode ? stMode : null;
var vrScript = VrScript.FromEnvironment(m => log.LogInformation("{Msg}", m));
if (vrScript != null) (vrScript.Hud, vrScript.Media, vrScript.Pilot) = (hudBoard, media, vrPilot);
if (vrScript != null) (vrScript.Keyboard, vrScript.Assistant, vrScript.PanelPlane) = (keyboard, assistant, () => vrPanelPlane);
if (vrScript != null) vrScript.Hands = hands;
if (vrScript != null) vrScript.Map = map;
if (vrScript != null) vrScript.Camera = shutter;
if (vrScript != null) vrScript.PinPanel = (at, rot) => { vrPlacement.Pin = (at, rot); vrPlacement.Place(); };
if (vrScript != null) vrScript.Shot = name =>
{
    captureNext = Path.Combine(opt.BaseDir, "shots", name + ".png");
    if (headset != null) presenter.CaptureNext = Path.Combine(opt.BaseDir, "shots", name + "-mirror.png");   // the window, menus and all
};
var handsCheck = HandsCheck.FromEnvironment(m => log.LogInformation("{Msg}", m));
var window1s = new FrameWindow();
long renderedAt = 0;   // the render thread's last desktop frame (Stopwatch ticks)
var rendered = new System.Collections.Concurrent.ConcurrentQueue<(FrameStats Stats, double At, double Dt, double Before, double After, double Waited, double WakeLead)>();
var quitAt = opt.Minutes is { } min ? TimeSpan.FromMinutes(min) : (TimeSpan?)null;
// the frame loop (not indented under the try: other sessions edit it); a lost GPU ends it and DeviceLoss starts
// Sojourn again where the person was: from here, or from DeviceLoss's watch when the loop is held where a failure
// can't reach it (inside Meta's xrWaitFrame after a driver reset)
Task<int> RestartAfterLoss(Exception e) => DeviceLoss.Restart(e, log, opt, args, session, async () =>
{
    sidecar?.Dispose();
    DevMode.Withdraw(devFile);
    media?.Dispose();
    worldAudio?.Dispose();
    voiceWiring?.Voice.Dispose();
    MetaAsw.End();
    if (bridge != null) await bridge.DisposeAsync();   // its port is the next run's
}, letGo: headset == null ? null : () =>
{
    // the headset's session ends before this process does, so the next run's isn't taken out with it (DeviceLoss.cs)
    try { pipe.Drain(TimeSpan.FromSeconds(1)); } catch (Exception) { }   // (a held render thread isn't waited for)
    try { renderer.WaitIdle(); } catch (Exception) { }
    headset?.LetGo();
    xr?.Dispose();
});
DeviceLoss.Guard(pipe, RestartAfterLoss, log);
try {
while (!window.IsClosing && !chat.QuitRequested && !(bench?.Done ?? false) && !login.Leaving)
{
    DeviceLoss.Beat();   // the loop turns (DeviceLoss's watch)
    pipe.TakeWorld();   // the last frame's scene is built: the world is this thread's until the next post
    TakeRendered();
    long loopStart = Stopwatch.GetTimestamp();
    window.DoEvents();
    LaunchState.Tick(login, session, headset != null);
    Sojourn.Client.Login.LauncherAnswers.Apply(login);
    (fbWidth, fbHeight) = (window.FramebufferSize.X, window.FramebufferSize.Y);
    if (window.IsClosing) break;
    settingsApi.Drain();
    privateUi.Dictation.Frame();
    direct.Drain();
    hudView.Drain();
    if (session.Client is { } me) renderer.AvatarLimits.Self = me.Self.AgentID;   // never limited or an impostor
    localSky = SkyFor(vrSettings.Sky);
    double now = clock.Elapsed.TotalSeconds, dt = Math.Min(0.1, now - last);
    last = now;
    if (quitAt is { } q && clock.Elapsed > q) break;
    adaptiveInk.Update(renderer.Backdrop, assistant.Panel.Look, headset != null ? reader.Corners()?.Select(vrPilot.RoomToRegion).ToArray() : null, now);

    if (headset != null)
    {
        headset.PollEvents();
        if (headset.Exiting)
        {
            log.LogInformation("the headset session ended; carrying on on the desktop");
            assistant.InHeadset = false;
            vrPilot.Release();
            MetaAsw.End();
            pipe.Drain();
            renderer.WaitIdle();
            headset.Dispose();
            headset = null;
            mirror = null;
            xr?.Dispose();
            xr = null;
            presenter.Vsync = opt.Vsync;
            presenter.Blank = false;   // the window is the view now
            continue;
        }
        DeviceLoss.Watch(headset.State);   // a run restarted after a loss, kept out of view: once more (DeviceLoss.cs)
        if (!headset.Running)
        {
            pipe.Post(renderer.Tick);
            Thread.Sleep(10);
            continue;
        }
        headset.DepthLayers = vrSettings.ReprojectionDepth;
        headset.WaitGpuBeforeRelease = LagHook.WaitGpu;   // the jitter fix; off only for diagnosis (Xr/LagHook.cs)
        mirror!.WindowOn = LagHook.Mirror;
        mirror.Menus = vrSettings.MirrorMenus;
        presenter.Blank = session.Private.On;   // incognito: the PC's monitor doesn't show the view (the headset does)
        HeadsetFrame(headset, mirror!);
        continue;
    }

    renderer.Motion.BeginFrame();
    if (steering && mouse != null)
    {
        var p = mouse.Position;
        if (Sojourn.Client.Window.Background.Foreground(window)) pilot.Look(p - lastMouse);
        lastMouse = p;
    }
    var keys = window.IsVisible && !assistant.Typing && media?.TypingInPage != true ? kb : null;
    fov = session.Camera.Fov;   // CameraAngle: the desktop view follows it
    var pose = pilot.Update(keys, dt, fov);
    if (pose is { } gp && pilot.Mode == CameraMode.FirstPerson) gaze.Frame(gp.Region, gp.Position, gp.Forward, renderer.Heads, now, (float)dt); else gaze.Idle(now);
    voiceWiring?.DesktopFrame(pose, assistant.Talking || privateUi.Dictation.Held);
    worldAudio?.Listen(pose);
    media?.Tick(pose);

    var fb = window.FramebufferSize;
    if (fb.X <= 0 || fb.Y <= 0) { pipe.Post(renderer.Tick); Thread.Sleep(20); continue; }
    var eyes = stereo ? (opt.QuestEyes ? QuestEyes(opt.Ipd) : FrameRequest.Stereo(opt.Ipd)) : FrameRequest.Mono;
    var (ew, eh) = opt.EyeSize ?? (stereo ? (fb.X / 2, fb.Y) : (fb.X, fb.Y));
    FrameRequest req;
    if (bench?.Camera(now) is { } bc)
        req = new FrameRequest
        {
            RegionHandle = bc.Region, Position = bc.Position, Forward = bc.Forward, Width = ew, Height = eh, Eyes = eyes,
            DrawDistance = opt.DrawDistance, LocalSky = localSky, VerticalFov = fov, Time = Bench.FixedTime,
        };
    else if (pose is { } cam)
        req = new FrameRequest
        {
            RegionHandle = cam.Region, Position = cam.Position, Forward = cam.Forward, Width = ew, Height = eh, Eyes = eyes,
            DrawDistance = session.Camera.FarClip, LocalSky = localSky, VerticalFov = fov,
            HideAvatars = session.HiddenInView(cam.HideSelf),
        };
    else
        req = new FrameRequest { RegionHandle = 0, Position = default, Forward = System.Numerics.Vector3.UnitX, Width = ew, Height = eh };

    if (selfTest != null && pose is { } stCam && SelfTest(stCam.Region, now) is { } st) req = req with { Position = st.Camera, Forward = st.Forward, Self = st.Body, HideAvatars = null };
    if (opt.ShotEvery is { } every && now >= nextShot)
    {
        nextShot = now + every;
        captureNext = Path.Combine(opt.BaseDir, "shots", $"t{now:0000.00}.png");
    }
    if (bench?.CaptureNow(opt.BaseDir) is { } benchShot) captureNext = benchShot;
    if (captureNext != null) { req = req with { CapturePath = captureNext }; captureNext = null; }
    hudView.Mouse = !steering && window.Size.X > 0 && window.Size.Y > 0 ? mouseAt / new Vector2(window.Size.X, window.Size.Y) : null;
    req = req with { CaptureTo = BothCaptures(assistant.Snapshots.Take(), showing.Shown.Take()), VideoTo = recorder.Take(), Overlay = DesktopAssistant(pose, ew, eh),
        Hud = hudView.Desktop(ew, eh, assistant.Confirmations.Pending) };
    // the interface hidden (Tab, VrSettings.InterfaceHidden): nothing of ours over the view but a confirmation; the
    // feed and the cards go as they do for a confirmation, name tags by SocialView (objects' floating text and the
    // HUDs, the world's, stay)
    bool deskHidden = settingsMenu.Hidden;
    if (pose is { } scam)
    {
        bool confirming = assistant.Panel.Read(p => p.Confirm != null) || deskHidden;
        req = req with { ShowNames = social.NamesOn || showing.Shown.WantsHeads || gaze.WantsHeads, Overlay = social.Desktop(req.Overlay, renderer, scam, fov, ew, eh, Math.Clamp(eh / 820f, 0.8f, 1.6f),
            assistant.Pointer.Current, confirming, (float)dt) };
        req = req with { Overlay = showing.Desktop(req.Overlay, scam, fov, ew, eh, Math.Clamp(eh / 820f, 0.8f, 1.6f), renderer.Heads, social.NamesOn, confirming) };
        req = req with { Overlay = map.Desktop(req.Overlay, Math.Clamp(eh / 820f, 0.8f, 1.6f), confirming,
            things.Desktop(Math.Clamp(eh / 820f, 0.8f, 1.6f), ew, eh, confirming, cut: false)) };
    }
    if (pose is { } vcam && !deskHidden) req = req with { Overlay = voiceWiring?.Decorate(req.Overlay, vcam.Region, vcam.Position, vcam.Forward, null, fov, eh) ?? req.Overlay };
    // (incognito's token, media's cues and the world touch's cue still follow the mouse while hidden, drawing nothing)
    if (pose is { } pcam && privateUi.Desktop(req.Overlay, pcam.Position, pcam.Forward, fov, ew, eh, steering || deskHidden ? null : mouseAt, window.Size, stereo) is var po && !deskHidden)
        req = req with { Overlay = po };
    if (pose is { } mcam && media != null && media.Desktop(req.Overlay, mcam, hudView.Mouse, ew, eh, assistant.Pointer.CurrentHit,
        MenuShown() || assistant.Confirmations.Pending || deskOnPanel, assistant.Confirmations.Pending || deskHidden, hudView.DesktopUnder) is var mo && !deskHidden)
        req = req with { Overlay = mo };
    if (pose is not null && direct.Desktop(req.Overlay, MenuShown() || assistant.Confirmations.Pending || deskOnPanel || media?.DesktopOver == true) is var dto && !deskHidden)
        req = req with { Overlay = dto };
    if (adaptiveInk.On && req.Overlay is { } ao) req = req with { Overlay = ao with { SampleBackdrop = true } };
    // a picture of what the person sees, menus and all (Assistant/SeenShots.cs): here they're in the eye image already
    if (assistant.Snapshots.SeenWaiting && pose != null) req = req with { SeenTo = assistant.Snapshots.TakeSeen(SeenView.Desktop(req.Overlay)) };
    double deskBefore = Stopwatch.GetElapsedTime(loopStart).TotalMilliseconds;
    var deskReq = req;
    var deskAt = now;
    double deskWaited = pipe.WaitedMs;
    pipe.Post(() =>
    {
        var s = renderer.RenderFrame(deskReq, presenter);
        // the frame's time is the render thread's, present to present (the main thread's loop runs ahead of it)
        long at = Stopwatch.GetTimestamp();
        double frameDt = renderedAt == 0 ? dt : Math.Min(0.1, (at - renderedAt) / (double)Stopwatch.Frequency);
        renderedAt = at;
        Rendered(s, deskAt, frameDt, deskBefore, 0, deskWaited, 0);
    });
    TakeRendered();

    if (now - statsAt >= 1.0)
    {
        statsAt = now;
        var gpu = bench == null ? renderer.Profiler.TakeWindow() : default;   // the bench takes its own windows
        var w = window1s.Take();
        (map.Fps, map.TargetFps) = ((float)w.Fps, 0);
        if (gpu.Frames > 0 && now - gpuLogAt >= 10)
        {
            // the GPU's side of the view line, for timed runs (tools/perfrun.py): its frame and the costliest passes
            gpuLogAt = now;
            log.LogInformation("gpu: {Ms:F1} ms a frame; {Passes}", gpu.FrameMs,
                string.Join(", ", gpu.Passes.OrderByDescending(p => p.Ms).Take(8).Select(p => $"{p.Pass} {p.Ms:F2}")));
        }
        if (showStats)
        {
            var where = session.Private.On ? "" : session.Client is { Network.CurrentSim: { } sim } cl ? $"{sim.Name} {cl.Self.SimPosition.X:F0},{cl.Self.SimPosition.Y:F0},{cl.Self.SimPosition.Z:F0}" : session.StatusMessage;
            // OBS captures Tessel's viewer by the window title "Second Life": never let a region name put it in ours
            window.Title = ($"Sojourn  {w.Fps:F0} fps  cpu {w.CpuMs:F1} ms  gpu {gpu.FrameMs:F1} ms  {ew}x{eh}{(stereo ? " x2" : "")}  " +
                            $"{w.Draws} draws  {(pilot.Flying ? "flying  " : "")}{pilot.Mode}  {where}").Replace("Second Life", "SL", StringComparison.OrdinalIgnoreCase);
        }
        else window.Title = "Sojourn";
    }
    if (!presenter.Vsync && opt.FpsCap > 0)
    {
        double next = now + 1.0 / opt.FpsCap - (clock.Elapsed.TotalSeconds - now);
        while (clock.Elapsed.TotalSeconds < next - 0.002) Thread.Sleep(1);
    }
}
} catch (Exception e) when (DeviceLoss.Is(e))
{
    return await RestartAfterLoss(e);
}
DeviceLoss.Unguard();

// --- shutdown -----------------------------------------------------------------------------------------------------
// The person is out at once: the render thread drained, the headset's session ends and the window goes, then the slow
// goodbyes (SL's logout up to 10 s, voice ~2 s, Meta's ASW restored by a process ~2 s, the sidecar) run side by side.
// They used to run one after another behind the last frame, the headset showing it frozen (the user, 2026-10-28).
log.LogInformation("shutting down");
LaunchState.Set("ending", headset != null);
var shutdownClock = Stopwatch.StartNew();
void Step(string what, Action act)
{
    long t0 = shutdownClock.ElapsedMilliseconds;
    try { act(); } catch (Exception e) { log.LogWarning("shutting down, {What}: {Msg}", what, e.Message); }
    long ms = shutdownClock.ElapsedMilliseconds - t0;
    if (ms >= 200) log.LogInformation("shutting down: {What} took {Ms} ms", what, ms);
}
bool wasHeadset = headset != null;
Step("the render thread", () => { try { pipe.Drain(); } finally { pipe.Dispose(); } });
Step("the headset", () => { renderer.WaitIdle(); headset?.Dispose(); headset = null; });   // the session: out of the app
Step("the window", () => window.IsVisible = false);
if (wasHeadset) vrPilot.Release(); else pilot.Release();
DevMode.Withdraw(devFile);
await Task.WhenAll(
    Task.Run(() => Step("SL's logout", () => session.LogoutAsync().GetAwaiter().GetResult())),
    Task.Run(() => Step("the assistant", () => sidecar?.Dispose())),
    Task.Run(() => Step("Meta's ASW", MetaAsw.End)),
    Task.Run(() =>
    {
        // in this order: media's and world audio's sound go through voice's engine
        Step("media", () => media?.Dispose());
        Step("world audio", () => worldAudio?.Dispose());
        Step("voice", () => voiceWiring?.Voice.Dispose());
        Step("the screen reader", screenReader.Dispose);
        Step("people", people.Dispose);
        Step("land", land.Dispose);
        Step("map", map.Dispose);
    }));
bench?.Finish();
if (bridge != null) await bridge.DisposeAsync();
Step("the renderer", () => { renderer.WaitIdle(); presenter.Dispose(); renderer.Dispose(); xr?.Dispose(); });   // the runtime last: Vulkan came through it
input.Dispose();
window.Reset();
log.LogInformation("shut down in {Ms} ms", shutdownClock.ElapsedMilliseconds);
// logged out, Back or Cancel on the login sheet: Sojourn again, at the login screen (Login/LoginRestart.cs)
if (login.Leaving) Sojourn.Client.Login.LoginRestart.ToLoginScreen(args, login.Account
    ?? new Sojourn.Session.Login.SavedAccount { Grid = opt.LoginUri, First = opt.First, Last = opt.Last, Start = opt.Start }, log);
return 0;

// One headset frame: wait for the runtime's slot, place the eyes and the avatar, and hand the frame to the render
// thread, which begins it, renders both eyes into the headset's swapchain (and the window) and ends it with the eye
// poses located here.
void HeadsetFrame(XrSession hs, MirrorTarget target)
{
    var xf = hs.WaitFrame();
    long frameStart = Stopwatch.GetTimestamp();
    FrameRequest? frameReq = null;
    double now = clock.Elapsed.TotalSeconds;
    double dt = lastDisplay == 0 ? xf.Period : Math.Clamp((xf.DisplayTime - lastDisplay) / 1e9, 0, 0.1);
    lastDisplay = xf.DisplayTime;
    renderer.Motion.BeginFrame(xf.DisplayAt);
    if (hs.Recentred) { vrPilot.Recentre(); menuPlaces?.Recentred(); showing.Recentred(); hudBoard.Recentred(); media?.View.Recentred(); keyboard.Recentred(); reader.Recentred(); map.Recentred(); hs.Recentred = false; }
    vrScript?.Apply(xf, session.Status == AgentStatus.Online, now);
    xf.Input = sheetStick.Take(xf.Input);   // a push on a sheet that scrolls is the sheet's: nothing turns, jumps or climbs
    xf.Input = hands.Buttons(xf.Input);   // A is the pointer's; the jump is the right stick up
    xf.Input = privateUi.Dictation.Headset(xf.Input);   // both grips dictate (each grip's own use waits)
    vrPilot.AvatarEyeHeight = renderer.SelfEyeHeight;
    vrPilot.SeatedEyes = renderer.SelfSeatedEyes;
    vrPilot.SeatedEyesLocal = renderer.SelfSeatedEyesLocal;
    var view = vrPilot.Update(xf, hs.FloorLevel, focused && window.IsVisible && !assistant.Typing ? kb : null, dt);
    menuPlaces?.Frame();   // every menu brought back (X held, the menu's key, the assistant), before they're placed
    regionFeed.LetGoOfKept();
    if (view is { Spectating: true }) gaze.Hold(now);
    else if (view is { } gv) gaze.Frame(gv.Region, gv.Position, gv.Forward, renderer.Heads, now, (float)dt);
    // a cut of the view to a new spot (a teleport, a put-back...; no black since the user dropped the blink, 2026-10-28):
    // each in the console, to match against what the person reports, and counted on the pacing line
    hs.ViewCut = vrPilot.TakeCut();
    if (hs.ViewCut)
        log.LogInformation("view cut: the view moved to a new spot at once ({How}, the avatar at {Speed:F1} m/s)",
            vrPilot.Flying ? "flying" : "on foot", session.Client?.Self.Velocity.Length() ?? 0);
    safety.Comfort.Frame(hs, vrPilot, xf.Input, dt);   // the comfort vignette (Control/Comfort.cs)
    // the HUD board first: a ray on it is the board's, not the panel's or the world's
    bool hudBlocked = MenuShown() || assistant.Confirmations.Pending;
    // the interface hidden (X, VrSettings.InterfaceHidden): the boards, keyboard, reader and forearm tokens are drawn
    // as for a confirmation (not at all) and take no ray; what was up stays up, out of sight, until X again. Moving,
    // the way out, talking, voice, aiming and pointing to teleport go on as usual
    bool uiHidden = settingsMenu.Hidden;
    bool uiBlocked = hudBlocked || uiHidden;
    // (and the keyboard before all of them: a ray on it is its own; nothing typed ever reaches a confirmation)
    xf.Input = keyboard.Frame(xf.Input, (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f, xf.Eyes[0].Orientation, uiBlocked, hs.Focused, (float)dt);
    var kbLayer = keyboard.Layer(uiBlocked);
    if (kbLayer is { } kl) hs.SetKeyboard(kl.Image, kl.Pos, kl.Rot, kl.Width); else hs.SetKeyboard(null, default, System.Numerics.Quaternion.Identity, 0);
    // (a page shown up close comes first, and the HUD board stands aside while it's up)
    // (the boards take the pointer's hand while it's on)
    var boardInput = HandPointer.Boards(xf.Input, hands.On && !uiHidden);
    media?.HeadsetBoard(boardInput, (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f, xf.Eyes[0].Orientation, vrPilot, vrPanelPlane, uiBlocked,
        assistant.Confirmations.Pending || uiHidden, hs.Focused, (float)dt);
    hudBoard.Frame(boardInput, (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f, xf.Eyes[0].Orientation, vrPilot, vrPanelPlane, uiBlocked,
        assistant.Confirmations.Pending || uiHidden || media?.BoardUp == true, hs.Focused, (float)dt);
    map.Headset(xf.Input, boardInput, (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f, xf.Eyes[0].Orientation, vrPanelPlane, uiBlocked,
        assistant.Confirmations.Pending || uiHidden, hs.Focused, (float)dt);
    standKey.Frame(xf.Input, vrPilot, uiBlocked, hs.Focused);
    privateUi.HeadsetFrame(xf.Input, vrPilot, uiBlocked, hs.Focused);
    bell.Frame(xf.Input, vrPilot, uiBlocked, hs.Focused);
    shutter.Frame(xf.Input, vrPilot, (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f, xf.Eyes[0].Orientation, uiBlocked, hs.Focused);
    // (a confirmation's own list shows above it, whatever else is hidden)
    reader.Headset(xf.Input, (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f, xf.Eyes[0].Orientation, vrPanelPlane,
        !reader.Reader.Lists(assistant.Panel.Read(p => p.Confirm)) && (assistant.Confirmations.Pending || uiHidden), hs.Focused, hs, (float)dt);
    things.Headset(boardInput, vrPilot, (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f, vrPanelPlane, hands.On, assistant.Confirmations.Pending || uiHidden, hs.Focused, hs.ViewCut);
    var overlay = HeadsetAssistant(hs, xf, (float)dt);
    // the world's own touch and sit: the pointer's hand, while it points out there freely (HeadsetAssistant decides)
    var worldInput = hands.World(xf.Input, vrWorldHand);
    bool leftOnMedia = media?.HeadsetLeft(worldInput, vrPilot, vrPanelPlane, uiBlocked, hs.Focused) == true;
    // (aiming, the trigger is the mouselook button and touches wait, as in the viewer's mouselook: Control/Aim.cs)
    // (and with the camera out and the hands framing, the trigger is its shutter: Control/Shutter.cs)
    overlay = direct.Headset(overlay, worldInput, vrPilot, vrPanelPlane, uiBlocked || leftOnMedia || vrPilot.Aiming || shutter.Framing, hs.Focused);
    // (hidden, the pointer isn't live, so the trigger is the shot's even with the pointer on; the crosshair isn't drawn)
    bool aimFree = !hudBlocked && !(assistant.PointAsked && !uiHidden) && (vrWorldHand || !hands.On || uiHidden) && !leftOnMedia
        && !(hudBoard.RightOnBoard || map.RightOn || media?.RightOnBoard == true || standKey.RightOn || bell.RightOn || reader.RightOn || privateUi.RightOn || shutter.RightOn || things.RightOn);
    shutter.Shoot(xf.Input, aimFree, hs.Focused);
    var aimed = safety.Aim?.Headset(overlay, xf.Input, vrPilot, aimFree && !shutter.Framing, hs.Focused) ?? overlay;
    if (!uiHidden) overlay = aimed;
    sheetStick.Frame(uiHidden ? null : SheetStick.Find(reader, vrRayOnPanel, settingsMenu, assistant, social, media, hudBoard, map, vrWorldHand, shutter), (float)dt);
    if (!uiHidden)
    {
        overlay = hudBoard.Decorate(overlay, vrPilot, () => assistant.Panel.Look, panelInEyes);
        overlay = keyboard.Decorate(overlay, vrPilot);
        overlay = standKey.Decorate(overlay, vrPilot, () => assistant.Panel.Look);
        overlay = privateUi.Headset(overlay, vrPilot);
        overlay = bell.Decorate(overlay, vrPilot, () => assistant.Panel.Look);
        overlay = shutter.Decorate(overlay, vrPilot, (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f, xf.Eyes[0].Orientation, assistant.Panel.Look);
        overlay = reader.Decorate(overlay, vrPilot.RoomToRegion, panelInEyes);
        overlay = things.Decorate(overlay, vrPilot.RoomToRegion);
    }
    // (pointing to teleport keeps its arc while hidden: it shows only while the left trigger is held for it, and a
    // teleport needs its spot seen)
    overlay = pointTeleport.Headset(overlay, xf.Input, vrPilot, session.Client?.Self.LocalID ?? 0, hudBlocked, hs.Focused);
    if (media != null) { if (!uiHidden) overlay = media.HeadsetCue(overlay, vrPilot); media.HeadsetLayers(hs, assistant.Confirmations.Pending || uiHidden); }
    // while a confirmation waits, nothing an object made sits beside it (nor while the interface is hidden: the board
    // around the HUDs is ours)
    var hudLayers = assistant.Confirmations.Pending || uiHidden ? null : hudBoard.Layers();
    if (hudLayers is { } hl) hs.SetHud(hl.Chrome.Image, hl.Chrome.Pos, hl.Chrome.Rot, hl.Chrome.Width, () => renderer.HudImage, hl.Screen.Pos, hl.Screen.Rot, hl.Screen.Width);
    else hs.SetHud(null, default, System.Numerics.Quaternion.Identity, 0, null, default, System.Numerics.Quaternion.Identity, 0);
    if (xf.ShouldRender)
    {
        var req = view is { } v
            ? new FrameRequest
            {
                RegionHandle = v.Region, Position = v.Position, Forward = v.Forward, Up = v.Up, Width = xf.Width, Height = xf.Height,
                // the lenses' hidden area per eye, which the renderer skips shading (Xr/XrLensMask.cs)
                Eyes = lensMask == null ? v.Eyes : v.Eyes.Select((e, i) => e with { Hidden = lensMask.Hidden(i) }).ToArray(),
                DrawDistance = session.Camera.FarClip, LocalSky = localSky,
                // your own body: drawn under your head with its head hidden (SelfBody), or not at all
                Self = v.Self,
                HideAvatars = session.HiddenInView(v.Self == null && !v.Spectating),
            }
            : Sojourn.Client.Login.VoidView.Headset(xf);   // no region yet: the void, by the headset's eyes (the login sheet over it)
        if (opt.ShotEvery is { } every && now >= nextShot)
        {
            nextShot = now + every;
            captureNext = Path.Combine(opt.BaseDir, "shots", $"t{now:0000.00}.png");
        }
        if (captureNext != null) { req = req with { CapturePath = captureNext }; captureNext = null; }
        req = req with
        {
            CaptureTo = BothCaptures(assistant.Snapshots.Take(), showing.Shown.Take()), VideoTo = recorder.Take(), Overlay = overlay,
            Hud = hudView.Headset(hudLayers != null, hudLayers != null && panelInEyes ? hudBoard.ScreenCorners().Select(vrPilot.RoomToRegion).ToArray() : null),
        };
        if (view is { } sview)
        {
            var headAt = (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f;
            // where the panel (or the settings menu on its layer) rests while it shows and follows: the feed and the
            // view cards keep their places beside it
            var panelRest = vrPanelWasVisible ? (vrMenuShown ? menuPlacement : vrPlacement).AsLeader : null;
            // (hidden: the feed and the cards go as for a confirmation; name tags by SocialView, floating text stays)
            bool confirming = assistant.Panel.Read(p => p.Confirm != null) || uiHidden;
            req = req with { ShowNames = social.NamesOn || showing.Shown.WantsHeads || gaze.WantsHeads, Overlay = social.Headset(req.Overlay, renderer, sview, headAt,
                xf.Eyes[0].Orientation, panelRest, vrPilot.RoomToRegion, hs, assistant.Pointer.Current, confirming, panelInEyes, (float)dt) };
            var grip = xf.Input.LeftHandPos is { } gp && xf.Input.LeftHand is { } gr ? (gp, gr) : ((System.Numerics.Vector3, System.Numerics.Quaternion)?)null;
            req = req with { Overlay = showing.Headset(req.Overlay, sview, headAt, xf.Eyes[0].Orientation, panelRest, vrPilot.RoomToRegion, hs, grip,
                renderer.Heads, social.NamesOn, confirming, panelInEyes, (float)dt) };
            // (with the camera's picture just taken, a few seconds over the panel it was taken through: Control/ShutterReview.cs)
            req = req with { Overlay = map.HeadsetScene(req.Overlay, hs, vrPilot.RoomToRegion, panelInEyes, shutter.Review.Layer(headAt, confirming), things.Layer()) };
        }
        if (view is { } vview && !uiHidden) req = req with { Overlay = voiceWiring?.Decorate(req.Overlay, vview.Region, vview.Position, vview.Forward, vrPilot.Watch(xf.Input)) ?? req.Overlay };
        if (handsCheck != null && view is { } hv && handsCheck.Frame(hv, xf.Input, vrPilot, now) is { } hc)
            req = req with
            {
                Position = hc.Camera, Forward = hc.Forward, Up = System.Numerics.Vector3.UnitZ,
                Overlay = hc.Marks is { } marks ? (req.Overlay ?? marks) with { Beams = marks.Beams, Dots = marks.Dots.Concat(req.Overlay?.Dots ?? []).ToArray() } : req.Overlay,
            };
        // the person's hands over the menus, where they're in front of them (Render's Overlay.Hands.cs)
        if (view != null && hs.MenuCorners(vrPilot.RoomToRegion) is { } menus)
        {
            var handsAt = new List<System.Numerics.Vector3>(2);
            if (xf.Input.LeftHandPos is { } lh) handsAt.Add(vrPilot.RoomToRegion(lh));
            if (xf.Input.RightHandPos is { } rh) handsAt.Add(vrPilot.RoomToRegion(rh));
            req = req with { Overlay = (req.Overlay ?? new OverlayScene()) with { Cut = new HandCut(menus, handsAt) } };
        }
        // walls and the menus: hidden where the world is in front (the compositor's depth corrected under them), or as
        // its own error hides them (the frost copying it), or always over (carved); Render/Realtime/Overlay.Walls.cs
        if (view != null && req.Overlay is { } wo)
            req = req with
            {
                Overlay = wo with
                {
                    MenusOverWalls = vrSettings.MenusOverWalls,
                    PanelOverWalls = hs.PanelOnTop,   // a confirmation is never covered (set this frame by HeadsetAssistant)
                    MenuDepthFix = vrSettings.MenuWallTest == "exact",
                    MenuDepth = vrSettings.MenuWallTest == "head" ? MenuDepth.RadialHead : MenuDepth.Radial,
                    MenuDepthBias = vrSettings.MenuFrostBias,
                },
            };
        // adaptive ink's look at the world behind the sheets (Assistant/AdaptiveInk.cs)
        if (adaptiveInk.On && req.Overlay is { } ao) req = req with { Overlay = ao with { SampleBackdrop = true } };
        frameReq = req;
    }
    // the ears and the faces playing media follow this frame's view (before the hand-over: they read the world)
    voiceWiring?.HeadsetFrame(view, xf.Input, assistant.Talking || privateUi.Dictation.Held);
    worldAudio?.Listen(view);
    media?.Tick(view);
    hs.Commit(xf);
    // a picture of what the person sees, menus and all: the frame's layers as committed, laid over its first eye
    // (Assistant/SeenShots.cs)
    if (frameReq != null && assistant.Snapshots.SeenWaiting) frameReq = frameReq with { SeenTo = assistant.Snapshots.TakeSeen(hs.Seen(xf)) };
    double before = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
    double frameNow = now, frameDt = dt, waited = pipe.WaitedMs, wakeLead = hs.WakeLeadMs;
    pipe.Post(() =>
    {
        long t0 = Stopwatch.GetTimestamp();
        hs.BeginFrame(xf);
        FrameStats? stats;
        try
        {
            LagHook.Frame();   // SOJOURN_LAG_CPU_MS: lag on purpose on the render thread, as a heavy scene would be (tools/lagtest.py)
            stats = frameReq != null ? renderer.RenderFrame(frameReq, target) : null;
            hs.EndFrameIfOpen();
        }
        catch
        {
            // the frame begun and ended anyway, so the main thread's next xrWaitFrame returns and meets the failure
            hs.Abandon();
            throw;
        }
        // the render thread's own time around the renderer: xrBeginFrame, and xrEndFrame when no image was rendered
        if (stats is { } s) Rendered(s, frameNow, frameDt, before, Stopwatch.GetElapsedTime(t0).TotalMilliseconds - s.TotalCpuMs, waited, wakeLead);
    });

    if (now - statsAt >= 1.0)
    {
        statsAt = now;
        var gpu = renderer.Profiler.TakeWindow();
        vrGpuOver += renderer.Profiler.WindowOver;
        vrGpuFrames += gpu.Frames;
        var w = window1s.Take();
        (map.Fps, map.TargetFps) = ((float)w.Fps, hs.RefreshRate);
        // the eyes' resolution: fixed (the settings) or following the GPU
        if (vrSettings.Resolution is { } fixedScale)
        {
            if (eyeScale != null || MathF.Abs(hs.RenderScale - fixedScale) > 0.005f) { eyeScale = null; hs.SetRenderScale(fixedScale); }
        }
        else eyeScale ??= new EyeScale(hs.RenderScale, 0.6f, 1f);
        if (eyeScale?.Update(now, gpu.FrameMs, 1000.0 / Math.Max(1, hs.RefreshRate)) is { } scale)
        {
            hs.SetRenderScale(scale);
            log.LogInformation("OpenXR: eyes now {W}x{H} (x{S:0.##}; gpu was {Gpu:F1} ms)", hs.RenderWidth, hs.RenderHeight, scale, gpu.FrameMs);
        }
        var where = session.Private.On ? "" : session.Client is { Network.CurrentSim: { } sim } cl ? $"{sim.Name} {cl.Self.SimPosition.X:F0},{cl.Self.SimPosition.Y:F0},{cl.Self.SimPosition.Z:F0}" : session.StatusMessage;
        // OBS captures Tessel's viewer by the window title "Second Life": never let a region name put it in ours
        window.Title = showStats
            ? ($"Sojourn VR  {w.Fps:F0}/{hs.RefreshRate:F0} fps  cpu {w.CpuMs:F1} ms  gpu {gpu.FrameMs:F1} ms  {hs.RenderWidth}x{hs.RenderHeight} x2  {w.Draws} draws  " +
               $"{(vrPilot.Flying ? "flying  " : "")}{(vrPilot.Running ? "running  " : "")}{hs.State}  {where}").Replace("Second Life", "SL", StringComparison.OrdinalIgnoreCase)
            : "Sojourn VR";
        if (now - consoleStatsAt >= 10)
        {
            consoleStatsAt = now;
            var (acqHs, acqWin) = target.TakeTargetTimes();
            var (winNotReady, winFrames) = presenter.TakeImageWaits();
            // the window's image not yet free at submit: the GPU waits for it before the frame's copies, the eyes' too
            log.LogInformation("VR {Fps:F0}/{Hz:F0} fps, cpu {Cpu:F1} ms (of it handing images over: headset {Hs:F1}, window {Win:F1}), gpu {Gpu:F1} ms, {Draws} draws, {State}; " +
                "mirror {Mirror}, its image not free at submit in {NotReady} of {WinFrames}",
                w.Fps, hs.RefreshRate, w.CpuMs, acqHs, acqWin, gpu.FrameMs, w.Draws, hs.State, target.WindowOn ? "on" : "off", winNotReady, winFrames);
            // what a late frame looks like in the headset depends on how late: the runtime reprojects it, and at a
            // steady half rate Meta's runtime synthesises every other frame (ASW), which smears fine detail
            // (the GPU's idle time between frames, last second: near 0 when frames run back to back on it)
            log.LogInformation("VR pacing: {Pacing}; gpu over the {Period:F1} ms period in {Over} of {Frames} frames, idle between frames {Idle:F2} ms; gpu passes: {Passes}",
                hs.TakePacing() ?? "no frames", renderer.Profiler.OverMs, vrGpuOver, vrGpuFrames, renderer.Profiler.WindowIdleMs,
                string.Join(", ", gpu.Passes.OrderByDescending(p => p.Ms).Take(8).Select(p => $"{p.Pass} {p.Ms:F2}")));
            if (hs.TakeRuntimeMetrics() is { } rm) log.LogInformation("VR runtime: {Metrics}", rm);
            vrGpuOver = vrGpuFrames = 0;
        }
        renderer.Profiler.OverMs = 1000.0 / Math.Max(1, hs.RefreshRate);
    }
}

// The assistant on the desktop: the mouse is the pointer (the screen's centre while steering), the panel sits at the
// top right of the image; Esc (or F2) puts the settings menu in its place (a confirmation takes it back).
OverlayScene? DesktopAssistant(CameraPose? pose, int ew, int eh)
{
    // the login sheet in the middle while it's up (Login/LoginSheet.View.cs): the panel and the pointer wait
    if (login.Active)
    {
        panelKeyUnderMouse = -1;
        deskOnPanel = true;
        assistant.Pointer.Clear();
        Vector2? loginMouse = window.Size.X > 0 && window.Size.Y > 0 ? new Vector2(mouseAt.X / window.Size.X * (stereo ? 2 : 1) % 1 * ew, mouseAt.Y / window.Size.Y * eh) : null;
        return login.Desktop(loginMouse, ew, eh);
    }
    float scale = Math.Clamp(eh / 820f, 0.8f, 1.6f);
    var panel = assistant.Frame(scale);
    if (assistant.Panel.Read(p => p.Confirm != null)) settingsMenu.Close();
    bool menu = MenuShown();
    // the reader, while it's open, in the panel's place (a confirmation takes it back, but for its own list; hidden with
    // the interface)
    var confirmWaits = assistant.Panel.Read(p => p.Confirm);
    var readerShown = reader.Reader.Lists(confirmWaits) || !menu && !settingsMenu.Hidden && confirmWaits == null ? reader.DeskFrame(scale) : null;
    var shown = menu ? settingsMenu.Frame(scale, (eh - 48) / scale) : readerShown ?? panel;
    panelKeyUnderMouse = -1;
    if (pose is not { } cam || mouse == null || window.Size.X <= 0 || window.Size.Y <= 0) { assistant.Pointer.Clear(); return null; }
    // the mouse in the eye image's pixels (the first eye's half when side by side)
    var m = mouseAt;
    float ex = m.X / window.Size.X * (stereo ? 2 : 1), ey = m.Y / window.Size.Y;
    if (ex > 1) ex -= 1;
    var mouseEye = new Vector2(ex * ew, ey * eh);
    var at = shown is { } s ? new Vector2(ew - s.Image.Width - 16, 16) : default;
    bool onPanel = shown is { } sp && !steering && mouseEye.X >= at.X && mouseEye.Y >= at.Y && mouseEye.X < at.X + sp.Image.Width && mouseEye.Y < at.Y + sp.Image.Height;
    deskOnPanel = onPanel;
    reader.DeskPoint(readerShown != null && onPanel ? (mouseEye - at) / shown!.Value.Raster.Scale : null);
    // the chat feed at the bottom left: a click opens a block in full (not while steering or on the panel)
    deskOnFeed = !onPanel && !steering && !menu && social.DesktopPoint(settingsMenu.Hidden ? null : mouseEye);
    if (onPanel)
    {
        if (!menu) assistant.Panel.Hold();
        var logical = (mouseEye - at) / shown!.Value.Raster.Scale;
        panelKeyUnderMouse = menu ? settingsMenu.KeyAt(logical) : readerShown != null ? -1 : assistant.KeyAt(logical);
    }
    if (menu) { settingsMenu.Hover(panelKeyUnderMouse, onPanel ? (mouseEye - at) / shown!.Value.Raster.Scale : null); assistant.Hover(-1); }
    else assistant.Hover(panelKeyUnderMouse);

    // the pointer's ray through the mouse (or the middle of the view while steering)
    var fwd = System.Numerics.Vector3.Normalize(cam.Forward);
    var right = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(fwd, System.Numerics.Vector3.UnitZ));
    var up = System.Numerics.Vector3.Cross(right, fwd);
    float t = MathF.Tan(fov / 2), aspect = (float)ew / eh;
    float nx = steering ? 0 : ex * 2 - 1, ny = steering ? 0 : 1 - ey * 2;
    var mouseRay = fwd + right * nx * t * aspect + up * ny * t;
    if (!onPanel && !deskOnFeed) assistant.Pointer.Update(renderer, cam.Region, cam.Position, mouseRay); else assistant.Pointer.Clear();
    if (!onPanel && !deskOnFeed && !menu && !steering && !settingsMenu.Hidden) showing.Point(cam.Position, System.Numerics.Vector3.Normalize(mouseRay), mouseEye); else showing.Clear();
    return shown is { } img ? new OverlayScene { Panel = img.Image, PanelAt = at } : null;
}

// The assistant and the settings menu in the headset: the right grip talks; the right controller is the one pointer
// (HandPointer: on by A, or by itself for a confirmation, the menu or the assistant asking to point), its beam and
// trigger on the panel's keys, the chat feed, the assistant's close keys, and out in the world ("that", and the touch
// and sit DirectTouch makes with it); X shows or hides the panel, the left menu button opens the settings. Panel and
// menu share one quad layer, placed by PanelPlacement: below the line of sight where you face when it appears, still
// while you look around it and carried along past that; while the menu is open it shows the menu, unless a
// confirmation waits (that always takes the layer, and re-arms whenever the layer moves).
OverlayScene? HeadsetAssistant(XrSession hs, XrFrame xf, float dt)
{
    // the login sheet in the menu's place while it's up (Login/LoginSheet.View.cs), the rays its own
    if (login.Active)
    {
        assistant.Pointer.Clear();
        vrRayOnPanel = false;
        vrPanelWasVisible = false;
        vrPanelPlane = null;
        return login.Headset(hs, xf, xf.Input, vrPilot.RoomToRegion, dt);
    }
    var input = xf.Input;
    double held = clock.Elapsed.TotalSeconds;
    if (input.Menu && !vrButtonsBefore.Menu) vrMenuHeldSince = held;
    if (input.Menu && vrMenuHeldSince >= 0 && held - vrMenuHeldSince >= Sojourn.Session.Control.WayOut.HoldSeconds)
    {
        vrMenuHeldSince = -1;
        settingsMenu.Close();
        wayOut.Go("held menu button");
    }
    // (the menu button with the interface hidden brings it back, the menu open)
    if (!input.Menu && vrButtonsBefore.Menu && vrMenuHeldSince >= 0) { vrMenuHeldSince = -1; settingsMenu.Summon(); }
    // X tapped hides everything of ours, or brings it all back as it was (the user's ask, 2026-10-28); shown again, a
    // panel put somewhere now out of view comes up in front (PanelPlacement.Place). Held 0.6 s (the user's call,
    // 2026-10-28), every menu comes back to following in front and the interface shows, whatever it was: so the tap's
    // toggle waits for the release, and a hold never toggles
    if (input.X && !vrButtonsBefore.X) vrXHeldSince = held;
    if (input.X && vrXHeldSince >= 0 && held - vrXHeldSince >= 0.6)
    {
        vrXHeldSince = -1;
        settingsMenu.SetHidden(false);
        menuPlaces?.Reset();
    }
    if (!input.X && vrButtonsBefore.X && vrXHeldSince >= 0) { vrXHeldSince = -1; settingsMenu.ToggleHidden(); }
    vrButtonsBefore = input;
    bool uiHidden = settingsMenu.Hidden;
    // what's in the middle of the view while the pointer's off (or out of sight): a hint for "that", looked for before
    // talk starts
    if ((!hands.On || uiHidden) && vrPilot.Region != 0)
        assistant.Pointer.Look(renderer, vrPilot.Region, vrPilot.RoomToRegion((xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f),
            vrPilot.RoomDirToRegion(System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ, xf.Eyes[0].Orientation)));
    // the right grip: held, push-to-talk; a quick tap leaves the mic open until the next tap (AssistantHost.TalkPress).
    // Out of the person's sight (the headset off, the system menu) a latched mic sends what it has, as a hold would.
    bool talkGrip = input.RightGrip > 0.6f || (vrTalkGrip && input.RightGrip >= 0.35f);
    if (talkGrip && !vrTalkGrip) assistant.TalkPress();
    else if (!talkGrip && vrTalkGrip && !(kb?.IsKeyPressed(Key.T) ?? false)) assistant.TalkRelease();
    vrTalkGrip = talkGrip;
    if (!hs.Focused && assistant.Latched) assistant.TalkUp();

    // at the person's size (UiSize): the same raster pixels a degree, and as many more lines a page as it's smaller
    assistant.Panel.Fit = UiSize.Fit;
    var panel = assistant.Frame(UiSize.Raster);
    var pending = assistant.Panel.Read(p => p.Confirm);
    bool confirming = pending != null;
    if (confirming) settingsMenu.Close();
    // the settings menu left alone a minute and a half closes by itself (it may sit forgotten to one side)
    // (and its time out of sight doesn't count)
    if (settingsMenu.Open && (vrRayOnPanel || uiHidden)) settingsMenu.Used();
    if (settingsMenu.Open && settingsMenu.IdleSeconds > 90) { settingsMenu.Close(); log.LogInformation("settings menu closed: unused for 90 s"); }
    bool menu = MenuShown();
    bool asked = assistant.PointAsked && !confirming && !uiHidden;   // hidden, the request to point waits with the panel
    // the right hand's ray: the person's pointer, or what needs it now (a confirmation, the menu, a request to point);
    // with the interface hidden, only a confirmation
    bool live = uiHidden ? confirming : hands.Live(confirming, menu, asked);
    // the menu at most 44 degrees tall (the room PanelPlacement leaves it, 4 above the eyes to 42 below): a longer
    // section pages, more rows a page at a smaller size
    var shown = menu ? settingsMenu.Frame(UiSize.Raster, 44 / UiSize.DegPerPx, UiSize.Wide) : panel;
    bool visible = shown != null;
    var head = (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f;
    if (menu != vrMenuShown) { assistant.PressDown(-1); settingsMenu.PressDown(-1); }   // a press doesn't carry across layers
    if (!hs.Focused)
    {
        // out of the person's sight (the system menu, the headset off): a confirmation arms only once seen again, and a
        // trigger already held when input comes back isn't a press
        pending?.MarkMoved();
        vrTriggerDown = true;
    }
    // the panel and the settings menu each have their own placement (each put somewhere apart, or following)
    var place = menu ? menuPlacement : vrPlacement;
    if (visible && (!vrPanelWasVisible || menu != vrMenuShown)) place.Place();
    else if (visible && confirming && !vrConfirmShown) vrPlacement.Recentre();
    vrPanelWasVisible = visible;
    vrMenuShown = menu;
    vrConfirmShown = confirming;
    if (menu || confirming || !visible || !live) vrPlacement.CancelGrab();   // a confirmation is never dragged
    if (!menu || !visible || !live) menuPlacement.CancelGrab();
    var mode = menu ? PanelPlacement.Mode.Menu : confirming ? PanelPlacement.Mode.Confirm : PanelPlacement.Mode.Panel;
    var (vrPanelPos, vrPanelRot, panelSize) = shown is { } sh
        ? place.Update(head, xf.Eyes[0].Orientation, mode, sh.Raster.LogicalWidth, sh.Raster.LogicalHeight, dt, frozen: vrRayOnPanel || (live && vrTriggerDown),
            reaching: place.HandNear(input.LeftHandPos, input.RightHandPos))
        : (default, System.Numerics.Quaternion.Identity, default);
    // a confirmation that moved (carried along, or put in front from elsewhere) hasn't been read where it is now: its
    // key arms afresh
    if (confirming && !menu && (vrPlacement.Gliding || System.Numerics.Vector3.Distance(vrPanelPos, vrPanelPosBefore) > 0.005f)) pending!.MarkMoved();
    vrPanelPosBefore = vrPanelPos;
    hs.SetPanel(shown?.Image, vrPanelPos, vrPanelRot, panelSize.X);
    hs.PanelOnTop = confirming;
    vrPanelPlane = shown != null ? (vrPanelPos, vrPanelRot, panelSize) : null;

    // the panel's place in the eye images: its frost goes there, under the headset's own layer. SOJOURN_PANEL_IN_EYES=1
    // draws the panel itself there too, where screenshots see it (for checking without a headset)
    System.Numerics.Vector3[]? corners = null;
    if (shown is { } pv && (panelInEyes || pv.Image.Frost > 0))
        corners = PanelPlacement.Corners(vrPanelPos, vrPanelRot, panelSize).Select(vrPilot.RoomToRegion).ToArray();
    OverlayScene? Frost() => corners != null ? new OverlayScene { Panel = shown?.Image, PanelCorners = corners, PanelContent = panelInEyes } : null;

    // the trigger's edges, followed whether or not the ray is live, so a pull already held when it comes up (a
    // confirmation arriving under it) is never a press
    bool pressed = input.RightTrigger > 0.6f, released = input.RightTrigger < 0.35f;
    bool down = pressed && !vrTriggerDown, up = released && vrTriggerDown;
    if (down) vrTriggerDown = true; else if (up) vrTriggerDown = false;
    vrWorldHand = false;
    if (!live || input.RightAim is not { } aimQ || input.RightAimPos is not { } aimP || vrPilot.Region == 0)
    {
        assistant.Pointer.Clear();
        assistant.Hover(-1);
        settingsMenu.Hover(-1);
        social.Point(null);
        showing.Clear();
        vrRayOnPanel = false;
        if (released && vrShownDown) { vrShownDown = false; showing.PressUp(); showing.Release(); }
        social.Placement.CancelGrab();   // (the panel's and the menu's were let go above)
        return Frost();
    }
    var dirXr = System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ, aimQ);
    // the panel first: where the ray meets its plane, in its own logical pixels
    int key = -1;
    Vector2? menuAt = null;   // where the ray is on the menu (its tips)
    float panelT = float.MaxValue;
    if (shown is { } sp)
    {
        var n = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, vrPanelRot);
        float denom = System.Numerics.Vector3.Dot(dirXr, n);
        if (denom < -1e-4f)
        {
            float tp = System.Numerics.Vector3.Dot(vrPanelPos - aimP, n) / denom;
            var p = aimP + dirXr * tp - vrPanelPos;
            float x = System.Numerics.Vector3.Dot(p, System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, vrPanelRot));
            float y = System.Numerics.Vector3.Dot(p, System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, vrPanelRot));
            if (tp > 0 && MathF.Abs(x) <= panelSize.X / 2 && MathF.Abs(y) <= panelSize.Y / 2)
            {
                panelT = tp;
                var logical = new Vector2((x / panelSize.X + 0.5f) * sp.Raster.LogicalWidth, (0.5f - y / panelSize.Y) * sp.Raster.LogicalHeight);
                key = menu ? settingsMenu.KeyAt(logical) : assistant.KeyAt(logical);
                menuAt = logical;
            }
        }
    }
    // then the chat feed beside it (hidden while a confirmation waits): a press opens a block in full
    float feedT = float.MaxValue;
    var feedAt = default(System.Numerics.Vector2);
    if (!menu && !confirming && social.Ray(aimP, dirXr) is { } fr && fr.T < panelT) { (feedT, feedAt) = (fr.T, fr.At); panelT = float.MaxValue; key = -1; }
    // then the cards shown with the person in view: the ray stops at them (a drag on one puts it somewhere)
    float cardT = float.MaxValue;
    if (!menu && !confirming && showing.CardRay(aimP, dirXr) is { } ct && ct < panelT && ct < feedT) { cardT = ct; panelT = feedT = float.MaxValue; key = -1; }
    bool onBoard = hudBoard.RightOnBoard || map.RightOn || media?.RightOnBoard == true || standKey.RightOn || bell.RightOn || reader.RightOn || privateUi.RightOn || shutter.RightOn || things.RightOn;
    if (onBoard) { panelT = feedT = cardT = float.MaxValue; key = -1; }   // the HUD board (or its token) or the page board has the ray
    vrRayOnPanel = panelT < float.MaxValue;
    bool onFeed = feedT < float.MaxValue, onCard = cardT < float.MaxValue;
    social.Point(onFeed ? feedAt : null);
    if ((vrRayOnPanel || vrPlacement.Dragging) && !menu) assistant.Panel.Hold();
    if (menu)
    {
        settingsMenu.Hover(key, vrRayOnPanel ? menuAt : null);
        assistant.Hover(-1);
        if (down)
        {
            settingsMenu.PressDown(key);
            // on the menu's body, not a key: it can be dragged somewhere too
            if (key < 0 && vrRayOnPanel && visible) menuPlacement.Grab(aimP, dirXr, panelT);
        }
        else if (up) { settingsMenu.PressUp(key); menuPlacement.Drop(); }
        else if (vrTriggerDown) menuPlacement.Drag(aimP, dirXr);
    }
    else
    {
        assistant.Hover(key);
        if (down)
        {
            assistant.PressDown(key);
            // the feed: a press on a block, or the start of a drag
            if (onFeed) social.Grab(aimP, dirXr, feedT); else social.PressDown();
            // on the panel's body, not a key: it can be dragged somewhere (a confirmation can't)
            if (key < 0 && vrRayOnPanel && !confirming && visible) vrPlacement.Grab(aimP, dirXr, panelT);
        }
        else if (up) { assistant.PressUp(key); social.Release(); vrPlacement.Drop(); showing.Release(); }
        else if (vrTriggerDown) { vrPlacement.Drag(aimP, dirXr); social.Drag(aimP, dirXr); showing.Drag(aimP, dirXr); }
    }

    var origin = vrPilot.RoomToRegion(aimP);
    var dir = vrPilot.RoomDirToRegion(dirXr);
    if (onBoard)
    {
        // on the HUD board: "that" is the HUD prim the ray is on; the board draws its own cue (the page board too)
        if (hudBoard.RightHit is { } onHud) assistant.Pointer.OnHud(onHud); else assistant.Pointer.Clear();
        showing.Clear();
        return Frost();
    }
    // "that": what the ray is on out in the world, while the person points (the pointer on, or asked to point)
    bool outThere = !menu && !vrRayOnPanel && !onFeed;
    // (hidden, the ray is up only for a confirmation: it points at nothing out there)
    bool pointing = !uiHidden && (hands.On || asked);
    var target = outThere && pointing && !onCard ? assistant.Pointer.Update(renderer, vrPilot.Region, origin, dir) : null;
    if (!(outThere && pointing && !onCard)) assistant.Pointer.Clear();
    // the assistant's elements: only their close keys take the trigger
    bool onClose = outThere && !uiHidden && showing.Point(origin, dir, null);
    if (!outThere || uiHidden) showing.Clear();
    if (!menu && !uiHidden && pressed && !vrShownDown)
    {
        vrShownDown = true;
        showing.PressDown();
        // a view card's body (not its close key): it may be dragged somewhere
        if (down && onCard && !onClose) showing.Grab(aimP, dirXr);
    }
    else if (released && vrShownDown) { vrShownDown = false; showing.PressUp(); showing.Release(); }
    // asked to point: a pull on something out there picks it, and the pick goes back to the assistant
    if (asked && down && outThere && !onClose && target != null) assistant.Pointed(target);
    // the world's own touch and sit (DirectTouch, media's pages) have the hand only while it points out there freely
    vrWorldHand = hands.On && !uiHidden && !asked && !confirming && outThere && !onClose && !onCard;
    var end = vrRayOnPanel ? origin + dir * panelT : onFeed ? origin + dir * feedT : onCard ? origin + dir * cardT
        : target is { } tg ? origin + dir * tg.Distance : origin + dir * 6;
    // in the look's light, bright enough to read as light over the world (PanelLook.Light)
    var beams = new List<OverlayBeam> { new(origin + dir * 0.04f, end, 0.004f, PanelLook.InUse.Light(0.75f)) };
    // dragging: a faint outline of its resting place, brighter when dropping it there would make it follow again
    beams.AddRange(place.Outline(vrPilot.RoomToRegion));
    var dots = target is { } hit
        ? new[] { new OverlayDot(end, Sojourn.Client.Control.PointerDot.Radius(hit.Distance, 0.008f), new System.Numerics.Vector4(0.95f, 0.97f, 0.98f, 0.9f)) }   // white, as the boards' (the user: yellow looked odd)
        : vrRayOnPanel || onFeed || onCard   // on a menu: a small dot where it lands (held with the beam, over the sheet)
            ? new[] { new OverlayDot(end, Math.Clamp(0.006f * (vrRayOnPanel ? panelT : onFeed ? feedT : cardT), 0.006f, 0.02f), PanelLook.InUse.Light(0.9f)) }
            : Array.Empty<OverlayDot>();
    return new OverlayScene { Beams = beams, Dots = dots, Panel = corners != null ? shown?.Image : null, PanelCorners = corners, PanelContent = panelInEyes };
}

// SOJOURN_SELF_TEST=orbit|down|front: the headset's own body (SelfBody) on the desktop, to check it without a headset:
// the hands trace circles in front of the chest, the eyes are scaled to 1.55 m; "orbit" circles a camera around the
// avatar at 2.2 m, "down" looks down from its eyes as a person in the headset would, "front" looks ahead from them.
(System.Numerics.Vector3 Camera, System.Numerics.Vector3 Forward, SelfBody Body)? SelfTest(ulong region, double t)
{
    if (session.Client is not { Network.CurrentSim: { } sim } c || sim.Handle != region) return null;
    var p = c.Self.SimPosition;
    var fwdL = LibreMetaverse.Vector3.UnitX * c.Self.SimRotation;
    float yaw = MathF.Atan2(fwdL.Y, fwdL.X);
    var fwd = new System.Numerics.Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0);
    var left = new System.Numerics.Vector3(-fwd.Y, fwd.X, 0);
    var up = System.Numerics.Vector3.UnitZ;
    float feet = p.Z - 0.95f;
    var head = new System.Numerics.Vector3(p.X, p.Y, feet + 1.55f);
    HandTarget HandAt(float side, float phase)
    {
        float a = (float)t * 1.3f + phase;
        var wrist = head + fwd * (0.32f + 0.12f * MathF.Cos(a)) + left * side * (0.2f + 0.08f * MathF.Sin(a)) - up * (0.42f - 0.15f * MathF.Sin(a));
        // fingers forward and a little in, palm facing in, thumb up (the controller's grip held level)
        var fingers = System.Numerics.Vector3.Normalize(fwd - left * side * 0.2f);
        var back = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(fingers, up) * side);
        var y = side > 0 ? fingers : -fingers;
        var x = System.Numerics.Vector3.Cross(y, back);
        var m = new System.Numerics.Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, back.X, back.Y, back.Z, 0, 0, 0, 0, 1);
        return new HandTarget(wrist, System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.CreateFromRotationMatrix(m)));
    }
    var body = new SelfBody { Id = c.Self.AgentID, Head = head, Yaw = yaw, EyeHeight = 1.55f, Left = HandAt(1, 0), Right = HandAt(-1, MathF.PI) };
    if (selfTest == "front") return (head, fwd, body);   // from the eyes, ahead: a mirror shows the body whole
    if (selfTest == "down")
    {
        var look = System.Numerics.Vector3.Normalize(fwd * 0.45f - up);
        return (head, look, body);
    }
    float orbit = (float)t * 0.35f;
    var around = new System.Numerics.Vector3(MathF.Cos(orbit), MathF.Sin(orbit), 0);
    var cam = head - up * 0.35f + around * 2.2f;
    return (cam, System.Numerics.Vector3.Normalize(head - up * 0.45f - cam), body);
}

// A Quest 3's eye frusta as its runtime reports them (approximately: outer 54, inner 42, up 47, down 49 degrees), so
// the headset's asymmetric path can be measured on the desktop.
static IReadOnlyList<EyeSpec> QuestEyes(float ipd)
{
    static float T(float deg) => MathF.Tan(deg * MathF.PI / 180f);
    return new[]
    {
        new EyeSpec(new System.Numerics.Vector3(-ipd / 2, 0, 0), System.Numerics.Quaternion.Identity, -T(54), T(42), T(47), -T(49)) { Hidden = Sojourn.Client.TestLens.Mesh },
        new EyeSpec(new System.Numerics.Vector3(ipd / 2, 0, 0), System.Numerics.Quaternion.Identity, -T(42), T(54), T(47), -T(49)) { Hidden = Sojourn.Client.TestLens.Mesh },
    };
}

// A frame the render thread finished: its numbers come back here, to be counted on this thread at its next turn.
void Rendered(FrameStats s, double at, double dt, double before, double after, double waited, double wakeLead) => rendered.Enqueue((s, at, dt, before, after, waited, wakeLead));

void TakeRendered()
{
    while (rendered.TryDequeue(out var r))
    {
        frameLog?.WriteLine(FrameLogLine(r.At, r.Dt, r.Stats, r.Before, r.After, r.Waited) + $",{r.WakeLead:F1}");
        window1s.Add(r.Stats, r.Dt);
        bench?.Frame(r.At, r.Stats);
    }
}

static Action<byte[], int, int>? BothCaptures(Action<byte[], int, int>? a, Action<byte[], int, int>? b) =>
    a == null ? b : b == null ? a : (rgba, w, h) => { a(rgba, w, h); b(rgba, w, h); };

static string FrameLogLine(double now, double dt, FrameStats s, double beforeMs, double afterMs, double waitedMs) =>
    $"{now:F4},{dt * 1000:F2},{s.TotalCpuMs - s.WaitMs:F2},{s.WaitMs:F2},{s.Captures},{s.Pending}," +
    $"{s.SceneMs:F2},{s.AvatarMs:F2},{s.ProbeMs:F2},{s.RecordMs:F2},{s.TotalCpuMs - s.WaitMs - s.SceneMs - s.ProbeMs - s.RecordMs:F2}," +
    GcSince.Take() + $",{beforeMs:F2},{afterMs:F2},{waitedMs:F2}";

/// <summary>The frame log's last columns: garbage collections (gen0, gen1, gen2) and GC pause ms since the last frame's
/// line, so a late frame can be told from one a collection held up.</summary>
static class GcSince
{
    static int _g0, _g1, _g2;
    static TimeSpan _paused;

    public static string Take()
    {
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var paused = GC.GetTotalPauseDuration();
        string line = $"{g0 - _g0},{g1 - _g1},{g2 - _g2},{(paused - _paused).TotalMilliseconds:F2}";
        (_g0, _g1, _g2, _paused) = (g0, g1, g2, paused);
        return line;
    }
}

/// <summary>Frame counters over a stats window.</summary>
sealed class FrameWindow
{
    int _frames;
    double _cpu, _time;
    int _draws;
    public void Add(FrameStats s, double dt)
    {
        if (!s.Presented) return;
        _frames++;
        _cpu += s.TotalCpuMs - s.WaitMs;
        _time += dt;
        _draws = s.Draws;
    }
    public (double Fps, double CpuMs, int Draws) Take()
    {
        var r = (_time > 0 ? _frames / _time : 0, _frames > 0 ? _cpu / _frames : 0, _draws);
        _frames = 0; _cpu = 0; _time = 0;
        return r;
    }
}
