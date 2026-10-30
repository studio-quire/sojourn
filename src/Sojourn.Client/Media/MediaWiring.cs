using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Silk.NET.Input;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Media;

/// <summary>
/// Media's hookup to the client, kept out of Program.cs (docs/media.md): the manager started with its helpers' runtime
/// and profile, the region's parcel media events, offered pages opened once the person pressed "Go to page", the
/// settings (the menu's sound section, SojournSettings), the face frames worked out on the main thread, the ears each
/// frame, the hands and the up-close view on the desktop and in the headset, SojournMedia on the agent API and the
/// confirmation rule for the assistant's presses on other people's pages.
/// Written by Claude Opus 5.5, 2026-10-26.
/// </summary>
public sealed class MediaWiring : IDisposable, Sojourn.Client.Hud.HudView.IMedia
{
    public readonly MediaManager Manager;
    public readonly MediaInput Input;
    public readonly PageView View;
    readonly AgentSession _s;
    readonly FrameRenderer _r;
    readonly VrSettings _st;
    readonly Action<string> _say;
    readonly MediaApi _api;
    readonly Confirmations _confirm;

    MediaWiring(AgentSession s, FrameRenderer r, AssistantHost assistant, VrSettings st, MediaManager m, Action<string> say)
    {
        _s = s;
        _r = r;
        _st = st;
        _say = say;
        Manager = m;
        Input = new MediaInput(m, s, r);
        View = new PageView(m, Input, () => assistant.Panel.Look, say);
        Input.Zoom = i => View.Open(i);
        _confirm = assistant.Confirmations;
        _api = new MediaApi(s, this);
        Apply();
    }

    public static MediaWiring? Start(AgentSession session, ClientOptions opt, FrameRenderer renderer, AssistantHost assistant, VrSettings st,
        bool soundOn, ILogger log)
    {
        void Say(string m) => log.LogInformation("{Msg}", m);
        if (!opt.Media) { Say("media: off (--no-media)"); return null; }
        string root = Sidecar.Root ?? AppContext.BaseDirectory;
        string runtime = Path.Combine(root, "local", "media", "runtime");
        if (!File.Exists(Path.Combine(runtime, "web", "sojourn_web.exe")))
            Say($"media: the helpers aren't built ({runtime}): python tools/media.py fetch, then build");
        string profile = Path.Combine(opt.BaseDir, "media", "web-profile");
        Directory.CreateDirectory(profile);
        // the local grid's own pages and test site are on this machine; on any other grid, pages may not reach it or the LAN
        bool privateOk = Uri.TryCreate(opt.LoginUri, UriKind.Absolute, out var login) && login.IsLoopback;
        renderer.Media.Log = Say;
        var m = new MediaManager(session, renderer.Media, runtime, profile, privateOk, Say)
        {
            SoundOn = soundOn,
            DrawDistance = () => session.Camera.FarClip,
        };
        var w = new MediaWiring(session, renderer, assistant, st, m, Say);
        w.LoadPrefs(opt.BaseDir);
        if (Environment.GetEnvironmentVariable("SOJOURN_MEDIA_DEBUG") == "1") MediaInput.Debug = Say;

        var hooked = new System.Runtime.CompilerServices.ConditionalWeakTable<LibreMetaverse.GridClient, object>();
        void Hook(LibreMetaverse.GridClient c)
        {
            if (hooked.TryGetValue(c, out _)) return;
            hooked.Add(c, new object());
            m.Hook(c);
        }
        lock (AgentSession.ClientHooks) AgentSession.ClientHooks.Add(Hook);
        if (session.Client is { } now) Hook(now);

        // llLoadURL's "Go to page", pressed by the person (the assistant's answer waits for their confirmation)
        session.OpenWebPage = offer =>
        {
            Say($"media: opening the page {MediaManager.Short(offer.Url)} offered by {offer.ObjectName}");
            w.View.Open(m.Locked(() => m.OpenOffer(offer)));
        };
        m.ZoomRequested += key => { if (m.Item(key) is { } i) w.View.Open(i); };
        assistant.Confirmations.OtherTouches = (pump, op, a) => pump == w._api.Name ? w._api.Confirm(op, a) : null;
        // a face's media settings changed for everyone: who may use or control it (and its whitelist) is a permission;
        // taking media off a face deletes it
        assistant.Confirmations.Cases.Add(new Confirmations.Case(
            (pump, op, a) => pump != w._api.Name ? null : op switch
            {
                "remove_media" => "delete",
                "configure" when MediaFaceSettings.ChangesPermissions(a) => "permissions",
                _ => null,
            },
            (_, op, a) => w._api.Settings.DescribeAsync(op, a)));
        session.Api.Register(w._api);
        Say($"media: on (pages {(privateOk ? "may" : "may not")} reach this machine and the LAN; sound {(soundOn ? "mixed" : "off: no audio engine")})");
        return w;
    }

    // ---- settings ----

    void Apply()
    {
        Manager.Mode = _st.MediaAutoPlay;
        Manager.VolumeSetting = Math.Clamp(_st.MediaVolume, 0, 2);
        Manager.Placed = _st.MediaPlaced;
    }

    static readonly string[] Modes = ["yours", "all", "off"];

    static string Pct(float v) => $"{v * 100:0}%";

    static float Step(float now, int dir)
    {
        var v = VrSettings.AudioVolumes;
        int i = Array.FindIndex(v, x => x >= now - 0.01f);
        if (i < 0) i = v.Length - 1;
        else if (v[i] > now + 0.01f && dir > 0) i--;
        return v[Math.Clamp(i + (dir == 0 ? 1 : dir), 0, v.Length - 1)];
    }

    /// <summary>Two rows after world audio's in the sound section, and three settings for any caller.</summary>
    public void Settle(SettingsMenu menu, SettingsApi api)
    {
        var before = menu.SoundRows;
        menu.SoundRows = () =>
        {
            var rows = before?.Invoke() ?? [];
            rows.Add((MenuRow.Choice("media", Modes, (int)_st.MediaAutoPlay), i => { _st.MediaAutoPlay = (MediaAutoPlay)Math.Clamp(i, 0, 2); Apply(); }));
            rows.Add((new MenuRow("media volume", Pct(_st.MediaVolume), true), d => { _st.MediaVolume = Step(_st.MediaVolume, d); Apply(); }));
            // whose faces take the first click, each step including those before it (B10 suggested trimming these for release)
            rows.Add((new MenuRow("first click", Manager.FirstClicks == FirstClick.Huds ? "HUDs" : FirstClickNames[(int)Manager.FirstClicks], true), d =>
            {
                int i = Math.Clamp((int)Manager.FirstClicks + (d == 0 ? 1 : d), 0, FirstClickNames.Length - 1);
                Manager.FirstClicks = (FirstClick)i;
                _prefs.Set("media_first_click", FirstClickNames[i]);
            }));
            rows.Add((MenuRow.Switch("zoom to media", Manager.AutoZoom), _ =>
            {
                Manager.AutoZoom = !Manager.AutoZoom;
                _prefs.Set("media_auto_zoom", Manager.AutoZoom);
            }));
            return rows;
        };
        Manager.Changed += menu.Changed;
        api.Add("media_autoplay", new("sound", "web pages and video on objects and the parcel that play by themselves: mine (the person's own objects, "
            + "HUDs and land), all (whatever asks to, as the viewer does), off (only what's started); anything can still be started by a touch",
            "mine, all, off", () => JsonValue.Create(_st.MediaAutoPlay switch { MediaAutoPlay.On => "all", MediaAutoPlay.Off => "off", _ => "mine" }),
            v =>
            {
                MediaAutoPlay? mode = v.ToString().Trim().ToLowerInvariant() switch
                {
                    "mine" or "yours" or "own" => MediaAutoPlay.Mine, "all" or "on" => MediaAutoPlay.On, "off" or "none" => MediaAutoPlay.Off, _ => null,
                };
                if (mode == null) return "media_autoplay: mine, all or off";
                _st.MediaAutoPlay = mode.Value;
                Apply();
                return null;
            }));
        api.Add("media_volume", new("sound", "web pages' and video's sound (1: the viewer's own level)", "0-2",
            () => JsonValue.Create(MathF.Round(_st.MediaVolume, 2)), SettingsApi.Number("media_volume", 0, 2, "", f => { _st.MediaVolume = f; Apply(); })));
        api.Add("media_placed", new("sound", "a face's sound comes from where the face is (off: from straight ahead, as parcel media)", "on, off",
            () => JsonValue.Create(_st.MediaPlaced ? "on" : "off"), SettingsApi.Switch("media_placed", on => { _st.MediaPlaced = on; Apply(); })));
        // the viewer's MediaFirstClickInteract and auto-zoom (batch B10): kept per machine with voice's hearing place
        api.Add("media_first_click", new("sound", "whose pages and video in the world take the person's first click (when the face asks for it, "
            + "PRIM_MEDIA_FIRST_CLICK_INTERACT); otherwise the first click only focuses it, as in the viewer. Each choice includes those before it: "
            + "none, huds, own, friends, group, land (the land owner's: both viewers' default), any (any face that asks), all (every face, asked or not)",
            string.Join(", ", FirstClickNames), () => JsonValue.Create(FirstClickNames[(int)Manager.FirstClicks]),
            SettingsApi.Choice("media_first_click", FirstClickNames, i =>
            {
                Manager.FirstClicks = (FirstClick)i;
                _prefs.Set("media_first_click", FirstClickNames[i]);
            })));
        api.Add("media_auto_zoom", new("sound", "a face that asks to be zoomed to (PRIM_MEDIA_AUTO_ZOOM) comes up close when the person's click "
            + "focuses it (the viewer moves its camera; here the page comes up close, the view never moves)", "on, off",
            () => JsonValue.Create(Manager.AutoZoom ? "on" : "off"), SettingsApi.Switch("media_auto_zoom", on =>
            {
                Manager.AutoZoom = on;
                _prefs.Set("media_auto_zoom", on);
            })));
    }

    static readonly string[] FirstClickNames = ["none", "huds", "own", "friends", "group", "land", "any", "all"];
    LocalPrefs _prefs = null!;

    void LoadPrefs(string baseDir)
    {
        _prefs = new LocalPrefs(baseDir);
        int i = Array.IndexOf(FirstClickNames, _prefs.Text("media_first_click") ?? "");
        Manager.FirstClicks = i >= 0 ? (FirstClick)i : FirstClick.Land;
        Manager.AutoZoom = _prefs.Flag("media_auto_zoom", true);
    }

    // ---- each frame (main thread) ----

    long _lastFrames;
    readonly Dictionary<string, DateTime> _faultAt = new();

    /// <summary>Media's part of a frame, kept from ending the person's session if it fails (logged once a minute a
    /// place): the frame goes on without it.</summary>
    T Guard<T>(string where, T fallback, Func<T> f)
    {
        try { return f(); }
        catch (Exception e)
        {
            if (!_faultAt.TryGetValue(where, out var at) || DateTime.UtcNow - at > TimeSpan.FromMinutes(1))
            {
                _faultAt[where] = DateTime.UtcNow;
                _say($"media: {where} failed: {e}");
            }
            return fallback;
        }
    }

    void Guard(string where, Action f) => Guard(where, 0, () => { f(); return 0; });

    public void Tick(CameraPose? pose)
    {
        if (pose is { } p) Guard("tick", () => Tick(p.Region, p.Position, p.Forward));
    }

    public void Tick(VrView? view)
    {
        if (view is { } v) Guard("tick", () => Tick(v.Region, v.Position, v.Forward));
    }

    /// <summary>The ears, and ten times a second the places of a few playing faces (the oldest first, each refreshed
    /// about once a second): their sound comes from there and the interest estimate measures from there.</summary>
    void Tick(ulong region, Vector3 pos, Vector3 forward)
    {
        Manager.Listen(region, pos, forward);
        long now = Environment.TickCount64;
        if (now - _lastFrames < 100) return;
        _lastFrames = now;
        var stale = DateTime.UtcNow - TimeSpan.FromSeconds(1);
        var want = Manager.Locked(() => Manager.ItemsUnlocked
            .Where(i => i.Kind == MediaItemKind.Prim && i.Player != null && i.FrameAt < stale)
            .OrderBy(i => i.FrameAt).Take(3).ToList());
        if (want.Count == 0) return;
        var got = want.Select(i => (i, f: _r.MediaFace(i.Region, i.LocalId, i.Face))).ToList();
        Manager.Locked(() =>
        {
            foreach (var (i, f) in got)
                Manager.SetFrame(i, f is { } x ? new FaceFrame(x.Centre, x.Normal, x.Right, x.Up, x.Width, x.Height) : null);
        });
    }

    // ---- the desktop ----

    /// <summary>The page shown up close and the pointer on pages in the world, before the world's touch. mouse: in the
    /// window, 0-1 (the HUD view's). blocked: the menu, a confirmation or the panel has the mouse. hud: the HUD prim
    /// under the mouse, which is in front of the world (media on a HUD face is clicked as the viewer does: its page
    /// takes the click, not the HUD's touch).</summary>
    public OverlayScene? Desktop(OverlayScene? scene, CameraPose pose, Vector2? mouse, int ew, int eh, PickHit? hit, bool blocked, bool confirming,
        PickHit? hud = null) =>
        Guard("desktop", scene, () =>
        {
            var s = View.Desktop(scene, mouse is { } m ? m * new Vector2(ew, eh) : null, ew, eh, confirming);
            Input.DesktopFrame(hud ?? hit, blocked || View.SheetUp);
            return hud != null ? s : Input.DesktopCue(s, pose.Position, hit, pose.Region);   // (a HUD hit's point is in HUD space)
        });

    /// <summary>The pointer is on media a click would act on, or the sheet is up: the world's cue waits.</summary>
    public bool DesktopOver => Input.DesktopOver || View.SheetUp;
    public bool TypingInPage => Input.TypingInPage;
    /// <summary>A left click for the up-close sheet (on it, or outside it to put it away).</summary>
    public bool SheetDown() => Guard("click", false, View.DesktopDown);
    /// <summary>A left click in the world, for media under the pointer.</summary>
    public bool WorldDown() => Guard("click", false, Input.DesktopDown);
    public void DesktopUp() => Guard("click", () => { if (!View.DesktopUp()) Input.DesktopUp(); });
    public bool Wheel(float notches) => Guard("wheel", false, () => View.DesktopWheel(notches) || Input.DesktopWheel(notches));
    public bool Char(char ch) => Guard("keys", false, () => Input.Char(ch));

    /// <summary>A key on the desktop: typing into a page's focused field; Esc puts the up-close view away; Z shows what's
    /// under the pointer up close (or puts it away). True if media took it.</summary>
    public bool Key(Key key, bool down, IKeyboard kb) => Guard("keys", false, () => KeyIn(key, down, kb));

    bool KeyIn(Key key, bool down, IKeyboard kb)
    {
        bool shift = kb.IsKeyPressed(Silk.NET.Input.Key.ShiftLeft) || kb.IsKeyPressed(Silk.NET.Input.Key.ShiftRight);
        bool ctrl = kb.IsKeyPressed(Silk.NET.Input.Key.ControlLeft) || kb.IsKeyPressed(Silk.NET.Input.Key.ControlRight);
        bool alt = kb.IsKeyPressed(Silk.NET.Input.Key.AltLeft) || kb.IsKeyPressed(Silk.NET.Input.Key.AltRight);
        if (Input.Key(key, down, shift, ctrl, alt)) return true;
        if (!down || ctrl || alt) return false;
        if (key == Silk.NET.Input.Key.Escape && View.Shown) { View.Close(); return true; }
        // Esc takes the focus off a face in the world (LLViewerMediaFocus), as it does the up-close view
        if (key == Silk.NET.Input.Key.Escape && Manager.Locked(() => Manager.Focused != null)) { Manager.Locked(Manager.ClearFocus); return true; }
        if (key == Silk.NET.Input.Key.Z)
        {
            if (View.Shown) { View.Close(); return true; }
            if (Input.UnderPointer is { } i) { View.Open(i); return true; }
        }
        return false;
    }

    // ---- the headset ----

    /// <summary>The page board, before the HUD board (which hides while this is up). Same arguments as HudBoard.Frame.</summary>
    public void HeadsetBoard(XrControllers input, Vector3 head, Quaternion headRot, VrPilot pilot, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel,
        bool blocked, bool hidden, bool focused, float dt) => Guard("board", () => View.HeadsetFrame(input, head, headRot, pilot, panel, blocked, hidden, focused, dt));

    public bool BoardUp => View.Shown;
    public bool RightOnBoard => View.RightOnBoard;

    // the right stick on a page (Control/SheetStick): up close, on a HUD face on the HUD board, out in the world
    public StickSheet? BoardSheet() => Guarded(Guard<StickSheet?>("stick", null, View.BoardSheet));
    public StickSheet? HudSheet(PickHit? hit) => hit == null ? null : Guarded(Guard<StickSheet?>("stick", null, () => Input.SheetAt(Input.At(hit))));
    public StickSheet? WorldSheet() => Guarded(Guard<StickSheet?>("stick", null, () => Input.SheetAt(Input.HandSpot)));
    StickSheet? Guarded(StickSheet? s) => s is { Scroll: { } f } ? s with { Scroll = (down, right) => Guard("stick", () => f(down, right)) } : s;

    /// <summary>The left hand on media in the world, before the world's touch: true when the world's touch waits (the
    /// hand is on a page, a video, media to start, or the page board).</summary>
    public bool HeadsetLeft(XrControllers input, VrPilot pilot, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel, bool blocked, bool focused) =>
        Guard("left hand", false, () =>
        {
            // on the board, the board has the hand; a press begun out in the world ends there
            Input.HeadsetLeft(input, pilot, panel, blocked, focused && !View.LeftOnBoard, i => View.Open(i));
            return Input.LeftOnMedia || View.LeftOnBoard;
        });

    // HUD faces on the headset's HUD board (HudView.IMedia): a double tap brings the page up close, as in the world
    bool Sojourn.Client.Hud.HudView.IMedia.Down(int hand, PickHit hit, Vector3 dir) => Guard("hud media", false, () => Input.HudDown(hand, hit, dir));
    void Sojourn.Client.Hud.HudView.IMedia.Held(int hand, PickHit? hit, Vector3 dir) => Guard("hud media", () => Input.HudHeld(hand, hit, dir));
    void Sojourn.Client.Hud.HudView.IMedia.Up(int hand, PickHit? hit) => Guard("hud media", () => { if (Input.HudUp(hand, hit) is { } i) View.Open(i); });
    void Sojourn.Client.Hud.HudView.IMedia.Cancel(int hand) => Guard("hud media", () => Input.HudCancel(hand));
    bool Sojourn.Client.Hud.HudView.IMedia.On(PickHit? hit) => Guard("hud media", false, () => Input.HudOn(hit));

    public OverlayScene? HeadsetCue(OverlayScene? scene, VrPilot pilot) => Guard("cues", scene, () =>
        View.Decorate(Input.HeadsetCue(scene, pilot.HeadInRegion is { } h && h.Region == pilot.Region ? h.Pos : Vector3.Zero), pilot));

    /// <summary>The board's two quad layers (none while a confirmation waits: nothing a page made sits beside it).</summary>
    public void HeadsetLayers(XrSession hs, bool confirming)
    {
        var layers = confirming ? null : Guard("board layers", null, View.Layers);
        if (layers is { } l)
            hs.SetPage(l.Chrome.Image, l.Chrome.Pos, l.Chrome.Rot, l.Chrome.Width, l.Screen.Image, l.Screen.Pos, l.Screen.Rot, l.Screen.Width);
        else hs.SetPage(null, default, Quaternion.Identity, 0, null, default, Quaternion.Identity, 0);
    }

    /// <summary>For the scripted test (Xr/VrScript.Media.cs): a point of a face's page (0-1 across and down) in its
    /// region, the face found by its object's name.</summary>
    public Vector3? FacePoint(string objectName, Vector2 uv) => Manager.Locked(() =>
        Manager.ItemsUnlocked.FirstOrDefault(i => i.Kind == MediaItemKind.Prim && i.ObjectName == objectName) is { Frame: { } f }
            ? f.Centre + f.Right * ((uv.X - 0.5f) * f.Width) + f.Up * ((0.5f - uv.Y) * f.Height) + f.Normal * 0.01f
            : (Vector3?)null);

    public void Dispose()
    {
        _s.OpenWebPage = null;
        _confirm.OtherTouches = null;
        _say("media: " + Manager.Locked(() => $"{Manager.ItemsUnlocked.Count} known, {Manager.ItemsUnlocked.Count(i => i.Playing)} playing") + " at exit");
        Manager.Dispose();
    }
}
