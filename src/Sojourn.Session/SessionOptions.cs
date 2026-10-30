namespace Sojourn.Session;

/// <summary>What a session host needs to start (<see cref="SessionHost"/>): the login, the agent API's port, the base dir
/// for state, caches and logs, and which of the assistant, voice, world audio and media to run. The VR client's command
/// line (SessionOptions) is one of these; defaults suit the local grid (tools/grid.py) and its test account.</summary>
public record SessionOptions
{
    public string LoginUri { get; init; } = "http://127.0.0.1:9700/";
    public string First { get; init; } = "Sojourn";
    public string Last { get; init; } = "Pilot";
    public string? Password { get; init; } = "sojourn-local";
    public string? PasswordFile { get; init; }
    /// <summary>"last", "home", or "uri:Region&amp;x&amp;y&amp;z".</summary>
    public string Start { get; init; } = "uri:Atelier&128&118&26";
    /// <summary>--start was given (the login screen's account keeps its own start otherwise).</summary>
    public bool StartGiven { get; init; }
    public float DrawDistance { get; init; } = 128;
    public int BridgePort { get; init; } = 9720;
    /// <summary>The agent API on BridgePort (StartAllAsync; off for an app with no assistant or tools to serve, since
    /// any program on the PC could read the session through it).</summary>
    public bool Api { get; init; } = true;
    public string BaseDir { get; init; } = DefaultBaseDir();
    /// <summary>Headset and assistant settings given on the command line, applied over the saved ones
    /// (local/client/vr-settings.json).</summary>
    public IReadOnlyList<Action<Control.VrSettings>> VrOverrides { get; init; } = Array.Empty<Action<Control.VrSettings>>();
    /// <summary>Start the assistant (assistant/main.py, its Python side) with the client.</summary>
    public bool Assistant { get; init; } = true;
    /// <summary>Dev mode (docs/dev-mode.md): no Claude of the client's own; what the person says or types goes to the
    /// Claude Code session that started the client (tools/dev.py watch), and its replies come back to the panel.</summary>
    public bool Dev { get; init; }
    /// <summary>Voice chat (Voice/VoiceClient.cs); its mode (push-to-talk or open mic); --voice-unmuted starts open mic
    /// unmuted (tests: a peer playing a file).</summary>
    public bool Voice { get; init; } = true;
    public bool VoiceUnmuted { get; init; }
    /// <summary>World audio (Audio/): objects' and gestures' sounds, footsteps, UI sounds and the parcel's music.</summary>
    public bool Sounds { get; init; } = true;
    /// <summary>Web pages and video on faces, parcel media and offered pages (Media/): --no-media leaves them out.</summary>
    public bool Media { get; init; } = true;

    protected static string DefaultBaseDir()
    {
        // local/client beside the repo's src/ when run from a build output folder; else beside the executable
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Sojourn.Client"))) dir = dir.Parent;
        return dir != null ? Path.Combine(dir.FullName, "local", "client") : Path.Combine(AppContext.BaseDirectory, "data");
    }
}
