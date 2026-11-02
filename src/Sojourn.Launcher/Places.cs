using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Sojourn.Launcher;

/// <summary>
/// Where things are. In the repo (src/Sojourn.Client above the launcher): the client's Release build run by the .NET
/// SDK in %LOCALAPPDATA%\dotnet-sdk (its apphost finds only the machine's .NET 6), the person's own base dir
/// local/main (their headset settings, accounts in local/secrets/logins). Packaged: the launcher is Sojourn.exe and the
/// VR client SojournVR.exe beside it, data in %LOCALAPPDATA%\Sojourn (the client's login store goes there by itself).
/// Sojourn Access ships on its own and isn't started from here.
/// </summary>
sealed class Places
{
    /// <summary>The repo's root, when run from it; null: packaged.</summary>
    public string? Repo { get; }
    public string BaseDir { get; }
    readonly string _here = AppContext.BaseDirectory;

    public Places(string? baseDir)
    {
        for (var d = new DirectoryInfo(_here); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "src", "Sojourn.Client"))) { Repo = d.FullName; break; }
        BaseDir = Path.GetFullPath(baseDir ?? (Repo != null
            ? Path.Combine(Repo, "local", "main")
            : Sojourn.Session.DataFolder.Root));
    }

    public string SettingsPath => Path.Combine(BaseDir, "vr-settings.json");
    public string StatePath => Path.Combine(BaseDir, "launcher-state.json");
    public string ConsoleLog => Path.Combine(BaseDir, "logs", "launcher-console.log");

    static string Dotnet()
    {
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dotnet-sdk", "dotnet.exe");
        if (File.Exists(sdk)) return sdk;
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, "dotnet.exe")))
            return Path.Combine(root, "dotnet.exe");
        return "dotnet";
    }

    /// <summary>A program of ours: packaged, its exe beside this one; in the repo, its Release dll under dotnet. Null with
    /// why when it isn't there (not built).</summary>
    (ProcessStartInfo? Psi, string? Missing) Program(string exe, string repoDll)
    {
        if (Repo == null)
        {
            var path = Path.Combine(_here, exe);
            return File.Exists(path) ? (new ProcessStartInfo(path) { WorkingDirectory = _here }, null) : (null, $"{exe} isn't beside the launcher");
        }
        var dll = Path.Combine(Repo, repoDll);
        if (!File.Exists(dll)) return (null, $"not built: {Path.GetRelativePath(Repo, dll)}");
        var psi = new ProcessStartInfo(Dotnet()) { WorkingDirectory = Repo };
        psi.ArgumentList.Add(dll);
        return (psi, null);
    }

    public (ProcessStartInfo? Psi, string? Missing) Client() =>
        Program("SojournVR.exe", Path.Combine("src", "Sojourn.Client", "bin", "Release", "net10.0", "SojournVR.dll"));

    /// <summary>The version as the client sends it (0.1.0.N in a packaged build, stamped by the package; 0.1.0.0 in the repo).</summary>
    public static string Version
    {
        get
        {
            var v = typeof(Places).Assembly.GetName().Version;
            return v is { Major: 0 } ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}.{Math.Max(0, v.Revision)}" : "0.1.0.0";
        }
    }

    /// <summary>Started from a Claude Code session (tests): the window shows without taking focus and never takes it,
    /// as the client's Window/Background.cs.</summary>
    public static bool Background { get; } = Environment.GetEnvironmentVariable("SOJOURN_BACKGROUND") == "1"
        || Environment.GetEnvironmentVariable("CLAUDECODE") == "1" && Environment.GetEnvironmentVariable("SOJOURN_FOREGROUND") != "1";
}
