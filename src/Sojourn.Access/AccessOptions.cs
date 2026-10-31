using System.Globalization;

namespace Sojourn.Access;

/// <summary>
/// The command line. Without --autologin the login form asks; with it (tests), the account given here logs in at once.
///   SojournAccess [--grid URI] [--user "First Last"] [--password P] [--start last|home|Region/x/y/z|uri:Region&amp;x&amp;y&amp;z]
///       [--port 9725] [--api|--no-api] [--base DIR] [--no-assistant] [--no-voice] [--no-sounds] [--reader auto|on|off] [--autologin] [--no-render]
///       [--local]
/// --local: the local grid (tools/grid.py) offered at the login and chosen; a packaged build otherwise offers Second Life.
/// --no-render: no renderer out of sight (no pictures for the assistant, touches without a surface, no faces listed);
/// walk mode's rays come from the collision model, which needs no GPU, either way.
/// --no-voice: no voice chat this run (it's on by default, push to talk on F4; --voice, its old opt-in, still parses).
/// </summary>
public sealed record AccessOptions
{
    public const string LocalGrid = "http://127.0.0.1:9700/";

    public string? Grid { get; init; }
    public string? User { get; init; }
    public string? Password { get; init; }
    public string? Start { get; init; }
    public int Port { get; init; } = 9725;
    public string BaseDir { get; init; } = DefaultBaseDir();
    /// <summary>The assistant: on where its Python side is (the repo, or bundled in a packaged build); it does nothing
    /// until a provider that works is chosen in Alt+0, and everything else works without one.</summary>
    public bool Assistant { get; init; } = AssistantInBuild;
    /// <summary>The agent API on Port: on in the repo (tests, the assistant, dev tools); off in a packaged build unless
    /// --api, since any program on her PC could read her chat through it.</summary>
    public bool? ApiAsked { get; init; }
    public bool Api => ApiAsked ?? (!Packaged || Assistant);
    public bool Voice { get; init; } = true;
    public bool Sounds { get; init; } = true;
    public string? Reader { get; init; }
    public bool AutoLogin { get; init; }
    public bool Render { get; init; } = true;
    /// <summary>--local: the local grid at the login (a packaged build lists only Second Life and other grids typed in).</summary>
    public bool Local { get; init; }

    /// <summary>The repo this runs from (src/Sojourn.Access above the program), or null: a packaged build.</summary>
    public static string? RepoRoot { get; } = FindRepo();
    public static bool Packaged => RepoRoot == null;
    /// <summary>The assistant's Python side: above the program in the repo, beside it in a packaged build that bundles
    /// it (tools/access.py package).</summary>
    public static bool AssistantInBuild { get; } = Sojourn.Session.Assistant.Sidecar.Available;
    /// <summary>The local grid on the login form: always in the repo; in a packaged build when asked for.</summary>
    public bool LocalOffered => !Packaged || Local;

    public static AccessOptions Parse(string[] args)
    {
        var o = new AccessOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            o = args[i] switch
            {
                "--grid" => o with { Grid = Next() },
                "--user" => o with { User = Next() },
                "--password" => o with { Password = Next() },
                "--start" => o with { Start = Next() },
                "--port" => o with { Port = int.Parse(Next() ?? "9725", CultureInfo.InvariantCulture) },
                "--base" => o with { BaseDir = Path.GetFullPath(Next() ?? o.BaseDir) },
                "--no-assistant" => o with { Assistant = false },
                "--api" => o with { ApiAsked = true },
                "--no-api" => o with { ApiAsked = false },
                "--voice" => o with { Voice = true },
                "--no-voice" => o with { Voice = false },
                "--no-sounds" => o with { Sounds = false },
                "--reader" => o with { Reader = Next() },
                "--autologin" => o with { AutoLogin = true },
                "--no-render" => o with { Render = false },
                "--local" => o with { Local = true, Grid = o.Grid ?? LocalGrid },
                _ => o,
            };
        }
        return o;
    }

    /// <summary>local/access in the repo when run from a build folder; a packaged build keeps the person's data in their
    /// own profile (%LOCALAPPDATA%\Sojourn Access), never beside the program.</summary>
    static string DefaultBaseDir() => RepoRoot != null
        ? Path.Combine(RepoRoot, "local", "access")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sojourn Access");

    static string? FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Sojourn.Access"))) return dir.FullName;
        return null;
    }

    /// <summary>"First Last", or a single name (Second Life's "Resident").</summary>
    public static (string First, string Last) SplitName(string user)
    {
        var parts = user.Trim().Split(new[] { ' ', '.' }, 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => ("", ""),
            1 => (parts[0], "Resident"),
            _ => (parts[0], parts[1].Trim()),
        };
    }

    /// <summary>Second Life's own grid (the TPV policy's must-dos are shared with the VR client: docs/tpv-policy.md).</summary>
    public const string SecondLife = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";

    /// <summary>A grid as typed: Second Life's by name, else the login URI itself.</summary>
    public static string GridFor(string typed)
    {
        var t = typed.Trim();
        return t.ToLowerInvariant() is "agni" or "second life" or "secondlife" or "sl" ? SecondLife : t;
    }

    /// <summary>Why a grid can't be logged in to, or null.</summary>
    public static string? Refused(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            return "That isn't a grid's login address (it starts with http:// or https://).";
        return null;
    }

    /// <summary>The session's start: last, home, or a place in any of the forms <see cref="Places.Parse"/> takes.</summary>
    public static string StartFor(string? typed)
    {
        var t = (typed ?? "").Trim();
        if (t.Length == 0 || t.Equals("last", StringComparison.OrdinalIgnoreCase)) return "last";
        if (t.Equals("home", StringComparison.OrdinalIgnoreCase)) return "home";
        if (t.StartsWith("uri:", StringComparison.OrdinalIgnoreCase)) return t;
        return Places.Parse(t) is { } p
            ? string.Create(CultureInfo.InvariantCulture, $"uri:{p.Region}&{(int)p.X}&{(int)p.Y}&{(int)p.Z}")
            : $"uri:{t}&128&128&30";
    }
}
