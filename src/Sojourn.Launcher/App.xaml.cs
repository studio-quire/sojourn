using System.Windows;
using Sojourn.Session.Login;

namespace Sojourn.Launcher;

/// <summary>
/// Command line: <c>--base-dir DIR</c> (the client's base dir: local/main in the repo, %LOCALAPPDATA%\Sojourn
/// packaged), <c>--desktop</c> (the client without the headset), <c>--dev</c> (this launch's assistant is the person's own
/// agent, as the "your agent" choice but unsaved: the client starts in dev mode, docs/dev-mode.md; the window says so,
/// and picking another provider ends it), <c>--shots DIR</c> (the window's states
/// drawn to PNGs offscreen, then quit; with <c>--lang CODE</c> in that language, English by default, and
/// <c>--pseudo long|cjk</c> in a made-up one for checking the layout, Shots.cs), and after <c>--</c> arguments passed on to the client (tests: --no-assistant,
/// --minutes 1).
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? baseDir = null, shots = null, lang = null, pseudo = null;
        bool desktop = false, dev = false;
        var extra = new List<string>();
        var args = e.Args;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--base-dir" when i + 1 < args.Length: baseDir = args[++i]; break;
                case "--desktop": desktop = true; break;
                case "--dev": dev = true; break;
                case "--shots" when i + 1 < args.Length: shots = args[++i]; break;
                case "--lang" when i + 1 < args.Length: lang = args[++i]; break;
                case "--pseudo" when i + 1 < args.Length: pseudo = args[++i]; break;
                case "--": extra.AddRange(args[(i + 1)..]); i = args.Length; break;
            }
        }
        var places = new Places(baseDir);
        if (shots != null)
        {
            Shots.Write(shots, places, lang, pseudo);
            Shutdown();
            return;
        }
        // one launcher per base dir: a second start hands its wish to the open one (OneLauncher.cs)
        LauncherView? view = null;
        Window? window = null;
        if (!OneLauncher.Claim(places, dev, wish => Dispatcher.BeginInvoke(() =>
            {
                if (view == null || window == null) return;
                if (wish.Contains("dev")) view.AgentFromSwitch();
                if (wish.Contains("front"))
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Show();
                    window.Activate();
                }
            })))
        {
            Shutdown(OneLauncher.HandedOver);
            return;
        }
        // the person's language, the client's setting too (Words.cs; the globe in the foot changes it)
        Lang.Use(new SettingsFile(places.SettingsPath).UiLanguage);
        view = new LauncherView(places, new LoginStore(places.BaseDir), desktop, extra) { Dev = dev };
        MainWindow = window = new LauncherWindow(view);
        RealPress.Start(MainWindow);   // before the window's first message
        MainWindow.Show();
    }
}
