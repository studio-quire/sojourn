using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sojourn.Core.Config;

/// <summary>One resident: who logs in where, and where its endpoint and state live.</summary>
public sealed class AgentConfig
{
    /// <summary>Short id used in URLs and folder names (e.g. "ada"). Defaults to first name, lowercased.</summary>
    public string Name { get; set; } = "";
    public string First { get; set; } = "";
    public string Last { get; set; } = "Resident";
    /// <summary>Plain password, or "$1$" + md5 hex as the viewer sends it. Prefer PasswordFile.</summary>
    public string? Password { get; set; }
    /// <summary>File holding the password (first line), so config files can be shared without secrets.</summary>
    public string? PasswordFile { get; set; }
    /// <summary>Grid name from HostConfig.Grids, or a login URI.</summary>
    public string Grid { get; set; } = "local";
    /// <summary>"last", "home", or a SLURL-style "uri:Region&amp;x&amp;y&amp;z".</summary>
    public string Start { get; set; } = "last";
    /// <summary>Where the very first login goes (a "uri:..." start), before the grid knows a last position for the
    /// avatar; later logins use Start. Remembered by a "first_login" file in the state dir.</summary>
    public string? FirstStart { get; set; }
    /// <summary>The agent's HTTP port. 0 = the next free one in the host's range.</summary>
    public int Port { get; set; }
    /// <summary>Folder for events.jsonl and the agent's own state; point it at a toolkit's .world to share it.</summary>
    public string? StateDir { get; set; }
    /// <summary>Base32 TOTP secret for grids with MFA (Second Life). Without it, an MFA challenge waits for /mfa.</summary>
    public string? MfaSecret { get; set; }
    /// <summary>Log in when the host starts (or when added).</summary>
    public bool AutoLogin { get; set; } = true;
    /// <summary>Log back in after an unexpected disconnect.</summary>
    public bool Reconnect { get; set; } = true;
    /// <summary>Draw distance sent to the region (the interest list); the viewer's default is 128.</summary>
    public float DrawDistance { get; set; } = 128;
    /// <summary>Dress a resident that wears only the grid's defaults in a generated look, and give it an active Clap
    /// gesture, at its first login (grids without server baking only). Null = no (the 40 run's residents start with
    /// nothing, the user's decision 2026-10-16); set true to hand out looks.</summary>
    public bool? AutoLook { get; set; }

    public string LoginName => $"{First} {Last}";
}

public sealed class GridConfig
{
    public string LoginUri { get; set; } = "";
    /// <summary>Asset cache folder for this grid; shared by every agent on it.</summary>
    public string? CacheDir { get; set; }
}

public sealed class HostConfig
{
    /// <summary>Address the agent endpoints and admin API bind to: 127.0.0.1 (default) or 0.0.0.0 for a LAN.</summary>
    public string Bind { get; set; } = "127.0.0.1";
    public int AdminPort { get; set; } = 9300;
    /// <summary>Agent ports come from this range; the whole range is bound at start, so agents can come and go.</summary>
    public int PortRangeStart { get; set; } = 9301;
    public int PortRangeEnd { get; set; } = 9349;
    /// <summary>Logins in flight at once. A login (and, on OpenSim, the avatar bake that follows) is the heaviest
    /// moment of a session; staggering them keeps memory and region load smooth when many agents start.</summary>
    public int MaxConcurrentLogins { get; set; } = 3;
    /// <summary>Optional token required from non-loopback clients (Authorization: Bearer, or ?token=); set it when
    /// binding beyond localhost. Requests from this machine never need it.</summary>
    public string? ApiToken { get; set; }
    /// <summary>Optional key for everything on this machine too (the lab's run token: its residents are local processes
    /// and mustn't reach each other's endpoints or the admin API). When set, the admin API and every agent port want
    /// either this key (X-World-Token / X-Estate-Token / X-Lab-Key header, ?key=, or the lab's cookie) or the agent's own
    /// key in X-Agent-Key, HMAC-SHA256(admin_key, "name:port") as hex, first 32 characters, which opens only that
    /// agent's endpoint, its registration and its own /agents/{name}/ actions (Access.cs). Bakes stay open (viewers
    /// fetch them). Null = off, as for dev grids and Tessel-style use.</summary>
    public string? AdminKey { get; set; }
    /// <summary>Root for per-agent state folders (relative to the config file).</summary>
    public string StateRoot { get; set; } = "state";
    /// <summary>Root for caches (relative to the config file).</summary>
    public string CacheRoot { get; set; } = "cache";
    /// <summary>A skin, hair or eyes wearable without textures of its own bakes to a flat tint of its colour sliders
    /// (no built-in face, shading, hair grain or iris), so bodies are made from nothing (Appearance/PlainBodies.cs).
    /// Off by default: the baker's built-in layers, as before.</summary>
    public bool PlainBodies { get; set; }
    /// <summary>Viewer channel and version sent at login. Real grids expect a real-looking pair.</summary>
    public string Channel { get; set; } = "Sojourn";
    public string Version { get; set; } = "0.1.0.0";
    public Dictionary<string, GridConfig> Grids { get; set; } = new()
    {
        ["local"] = new GridConfig { LoginUri = "http://127.0.0.1:9400/" },
        ["agni"] = new GridConfig { LoginUri = "https://login.agni.lindenlab.com/cgi-bin/login.cgi" },
        ["aditi"] = new GridConfig { LoginUri = "https://login.aditi.lindenlab.com/cgi-bin/login.cgi" },
    };
    public List<AgentConfig> Agents { get; set; } = new();

    /// <summary>Ports of the range actually bound at start (others were in use).</summary>
    [JsonIgnore] public HashSet<int> UsablePorts { get; } = new();

    [JsonIgnore] public string BaseDir { get; set; } = Directory.GetCurrentDirectory();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static HostConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        var cfg = File.Exists(full)
            ? JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(full), JsonOptions) ?? new HostConfig()
            : new HostConfig();
        cfg.BaseDir = Path.GetDirectoryName(full)!;
        return cfg;
    }

    public string Resolve(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(BaseDir, path));

    public string LoginUriFor(AgentConfig a) =>
        Grids.TryGetValue(a.Grid, out var g) ? g.LoginUri : a.Grid;

    /// <summary>A stable key for "which grid": agents on the same grid share world and asset cache.</summary>
    public string GridKeyFor(AgentConfig a)
    {
        if (Grids.ContainsKey(a.Grid)) return a.Grid;
        var uri = new Uri(a.Grid);
        return $"{uri.Host}_{uri.Port}";
    }

    public string CacheDirFor(AgentConfig a)
    {
        var key = GridKeyFor(a);
        if (Grids.TryGetValue(key, out var g) && g.CacheDir is not null) return Resolve(g.CacheDir);
        return Path.Combine(Resolve(CacheRoot), key);
    }

    public string StateDirFor(AgentConfig a) =>
        a.StateDir is not null ? Resolve(a.StateDir) : Path.Combine(Resolve(StateRoot), a.Name);
}
