using Velopack;

namespace Sojourn.Launcher;

/// <summary>
/// Sojourn.exe's start. Velopack's hooks come first: installing, updating and uninstalling run this program with a
/// hook's argument, and Run() handles it and exits before any window (docs/launcher.md > The package). An update is
/// never applied by itself at start: only the person's press installs one (Updates.cs). Then the WPF app.
/// </summary>
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // only where Velopack installed it (its Update.exe above current\) or calls a hook: a run from the repo or a
        // plain folder leaves Velopack's shared log alone
        if (Updates.InstalledHere || args.Any(a => a.StartsWith("--veloapp-")))
            VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                .OnBeforeUninstallFastCallback(_ => Updates.TidyForUninstall())
                .Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
