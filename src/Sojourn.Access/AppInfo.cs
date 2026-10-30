using System.Reflection;

namespace Sojourn.Access;

/// <summary>
/// Which build this is. A packaged build (`tools/access.py package`) is stamped 0.1.0.N, N the repo's commit count, with
/// the commit in the informational version; a build in the repo is 0.1.0.0, as the VR client's. The version is what
/// the login sends (the TPV policy wants each build identifiable: docs/tpv-policy.md > Channel and version), shown on
/// the login form and said by //about.
/// </summary>
static class AppInfo
{
    public static string Version { get; } = Find().Version;
    /// <summary>The commit it was built from (short), or null.</summary>
    public static string? Commit { get; } = Find().Commit;

    static (string Version, string? Commit) Find()
    {
        var asm = typeof(AppInfo).Assembly;
        var v = asm.GetName().Version;
        var version = v is { Major: 0 } ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}.{Math.Max(0, v.Revision)}" : "0.1.0.0";
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plus = info?.IndexOf('+') ?? -1;
        string? commit = plus >= 0 ? info![(plus + 1)..] : null;
        if (commit is { Length: 40 } c) commit = c[..7];   // the SDK's full hash in a build in the repo
        return (version, string.IsNullOrEmpty(commit) ? null : commit);
    }

    public static string About(string dataDir) =>
        $"Sojourn Access {Version}. Made by Claude models at Studio Quire; an early test build. Its source: github.com/studio-quire/sojourn (each release's notes name the commit). "
        + $"{Sojourn.Session.Release.ReleaseApi.Disclaimer} Your settings and logs are in {dataDir}.";
}
