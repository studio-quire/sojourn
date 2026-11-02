using System.Windows;
using Sojourn.Session.Login;

namespace Sojourn.Launcher;

/// <summary>
/// Command line: <c>--base-dir DIR</c> (the client's base dir: local/main in the repo, %LOCALAPPDATA%\Sojourn
/// packaged), <c>--desktop</c> (the client without the headset), <c>--dev</c> (the client in dev mode: the person's own
/// Claude Code session is its assistant, docs/dev-mode.md; the window says so), <c>--shots DIR</c> (the window's states
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
        if (dev) extra.Insert(0, "--dev");
        var places = new Places(baseDir);
        if (shots != null)
        {
            Shots.Write(shots, places, lang, pseudo);
            Shutdown();
            return;
        }
        // the person's language, the client's setting too (Words.cs; the globe in the foot changes it)
        Lang.Use(new SettingsFile(places.SettingsPath).UiLanguage);
        var view = new LauncherView(places, new LoginStore(places.BaseDir), desktop, extra) { Dev = dev };
        MainWindow = new LauncherWindow(view);
        RealPress.Start(MainWindow);   // before the window's first message
        MainWindow.Show();
    }
}
