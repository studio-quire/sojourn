using System.Windows;
using Sojourn.Session.Login;

namespace Sojourn.Launcher;

/// <summary>
/// Command line: <c>--base-dir DIR</c> (the client's base dir: local/main in the repo, %LOCALAPPDATA%\Sojourn
/// packaged), <c>--desktop</c> (the client without the headset), <c>--shots DIR</c> (the window's states drawn to PNGs
/// offscreen, then quit), and after <c>--</c> arguments passed on to the client (tests: --no-assistant, --minutes 1).
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? baseDir = null, shots = null;
        bool desktop = false;
        var extra = new List<string>();
        var args = e.Args;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--base-dir" when i + 1 < args.Length: baseDir = args[++i]; break;
                case "--desktop": desktop = true; break;
                case "--shots" when i + 1 < args.Length: shots = args[++i]; break;
                case "--": extra.AddRange(args[(i + 1)..]); i = args.Length; break;
            }
        }
        var places = new Places(baseDir);
        if (shots != null)
        {
            Shots.Write(shots, places);
            Shutdown();
            return;
        }
        var view = new LauncherView(places, new LoginStore(places.BaseDir), desktop, extra);
        MainWindow = new LauncherWindow(view);
        MainWindow.Show();
    }
}
