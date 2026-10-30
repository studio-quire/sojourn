using System.Diagnostics;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Window;
using Sojourn.Client.Xr;
using Sojourn.Render.Realtime;

namespace Sojourn.Client.Login;

/// <summary>
/// The login screen, before there's a session (--login): the window and the headset are up, the renderer draws the
/// void with the login sheet over it (FrameRenderer.Void.cs), and nothing else runs (no assistant, no voice, no agent
/// API) until the person has chosen and pressed Log in. Each account and grid has its own state dir, so the session
/// is made only then; logging out or Back starts Sojourn again into this screen (LoginRestart).
/// Written by Claude Opus 5.5, 2026-10-28 (batch R).
/// </summary>
public sealed class LoginScreen
{
    public required IWindow Window { get; init; }
    public required IInputContext Input { get; init; }
    public required FrameRenderer Renderer { get; init; }
    public required Presenter Presenter { get; init; }
    public required FramePipe Pipe { get; init; }
    public required Func<XrSession?> Headset { get; init; }
    public required Func<MirrorTarget?> Mirror { get; init; }
    /// <summary>The headset's session ended: let go of it as the main loop does and carry on on the desktop.</summary>
    public required Action HeadsetGone { get; init; }
    /// <summary>The window's framebuffer size as this thread read it (the presenter's).</summary>
    public required Action<int, int> Framebuffer { get; init; }
    public required VrSettings Settings { get; init; }
    public required LoginSheet Sheet { get; init; }
    public required Action<string> Log { get; init; }
    /// <summary>--shot-every: a picture of the screen every so many seconds into this folder (tests).</summary>
    public (double Every, string Dir)? Shots { get; init; }

    /// <summary>Runs until the person logs in (their choice), or closes the window or quits (null).</summary>
    public LoginChoice? Run()
    {
        var kb = Input.Keyboards.FirstOrDefault();
        var mouse = Input.Mice.FirstOrDefault();
        var keyboard = new HeadsetKeyboard(() => PanelLook.InUse, Log);
        Sheet.ShowKeyboard = t => { if (Headset() != null) keyboard.Show(t); };
        Vector2 mouseAt = mouse?.Position ?? default;
        void OnChar(IKeyboard _, char ch) => Sheet.Char(ch);
        void OnKey(IKeyboard k, Key key, int _)
        {
            bool shift = k.IsKeyPressed(Key.ShiftLeft) || k.IsKeyPressed(Key.ShiftRight);
            bool ctrl = k.IsKeyPressed(Key.ControlLeft) || k.IsKeyPressed(Key.ControlRight);
            Sheet.Key(key, shift, ctrl, () => k.ClipboardText, real: true);
        }
        void OnDown(IMouse _, MouseButton b) { if (b == MouseButton.Left && Headset() == null) Sheet.MouseDown(true); }
        void OnUp(IMouse _, MouseButton b) { if (b == MouseButton.Left && Headset() == null) Sheet.MouseUp(); }
        void OnMove(IMouse _, Vector2 p) => mouseAt = p;
        if (kb != null) { kb.KeyChar += OnChar; kb.KeyDown += OnKey; }
        if (mouse != null) { mouse.MouseDown += OnDown; mouse.MouseUp += OnUp; mouse.MouseMove += OnMove; }
        Log("login screen up");
        var clock = Stopwatch.StartNew();
        long lastDisplay = 0;
        double nextShot = 0;
        string? Shot()
        {
            if (Shots is not { } sh || clock.Elapsed.TotalSeconds < nextShot) return null;
            nextShot = clock.Elapsed.TotalSeconds + sh.Every;
            return Path.Combine(sh.Dir, $"login-{clock.Elapsed.TotalSeconds:0000.0}.png");
        }
        try
        {
            while (!Window.IsClosing && Sheet.Chosen == null && !Sheet.Quit)
            {
                Pipe.TakeWorld();
                Window.DoEvents();
                var fb = Window.FramebufferSize;
                Framebuffer(fb.X, fb.Y);
                if (Window.IsClosing) break;
                if (Headset() is { } hs)
                {
                    hs.PollEvents();
                    if (hs.Exiting) { HeadsetGone(); keyboard.Close(); continue; }
                    if (!hs.Running) { Pipe.Post(Renderer.Tick); Thread.Sleep(10); continue; }
                    var xf = hs.WaitFrame();
                    double dt = lastDisplay == 0 ? xf.Period : Math.Clamp((xf.DisplayTime - lastDisplay) / 1e9, 0, 0.1);
                    lastDisplay = xf.DisplayTime;
                    if (hs.Recentred) { Sheet.Recentred(); keyboard.Recentred(); hs.Recentred = false; }
                    var head = (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f;
                    xf.Input = keyboard.Frame(xf.Input, head, xf.Eyes[0].Orientation, hidden: false, hs.Focused, (float)dt);
                    var kl = keyboard.Layer(false);
                    if (kl is { } k) hs.SetKeyboard(k.Image, k.Pos, k.Rot, k.Width); else hs.SetKeyboard(null, default, Quaternion.Identity, 0);
                    var overlay = Sheet.Headset(hs, xf, xf.Input, VoidView.ToRegion, (float)dt);
                    overlay = keyboard.Decorate(overlay, VoidView.ToRegion);
                    if (Mirror() is { } m) m.Menus = Settings.MirrorMenus;
                    var req = xf.ShouldRender ? VoidView.Headset(xf, overlay) with { CapturePath = Shot() } : null;
                    hs.Commit(xf);
                    var target = Mirror();
                    Pipe.Post(() =>
                    {
                        hs.BeginFrame(xf);
                        try
                        {
                            if (req != null && target != null) Renderer.RenderFrame(req, target);
                            hs.EndFrameIfOpen();
                        }
                        catch { hs.Abandon(); throw; }
                    });
                    continue;
                }
                if (fb.X <= 0 || fb.Y <= 0) { Pipe.Post(Renderer.Tick); Thread.Sleep(20); continue; }
                var size = Window.Size;
                Vector2? mouseEye = size.X > 0 && size.Y > 0 ? new Vector2(mouseAt.X / size.X * fb.X, mouseAt.Y / size.Y * fb.Y) : null;
                var deskReq = VoidView.Desktop(fb.X, fb.Y, Sheet.Desktop(mouseEye, fb.X, fb.Y)) with { CapturePath = Shot() };
                double t0 = clock.Elapsed.TotalSeconds;
                Pipe.Post(() => Renderer.RenderFrame(deskReq, Presenter));
                // without vsync, no faster than the monitor would be: a form doesn't need more
                if (!Presenter.Vsync) while (clock.Elapsed.TotalSeconds < t0 + 1 / 60.0 - 0.002) Thread.Sleep(1);
            }
        }
        finally
        {
            if (kb != null) { kb.KeyChar -= OnChar; kb.KeyDown -= OnKey; }
            if (mouse != null) { mouse.MouseDown -= OnDown; mouse.MouseUp -= OnUp; mouse.MouseMove -= OnMove; }
            keyboard.Close();
            Headset()?.SetKeyboard(null, default, Quaternion.Identity, 0);
            Sheet.ShowKeyboard = null;
        }
        Log(Sheet.Chosen != null ? "login screen: logging in" : "login screen closed");
        return Sheet.Chosen;
    }
}
