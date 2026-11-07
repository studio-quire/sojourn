using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sojourn.Core;
using Sojourn.Core.Access;

namespace Sojourn.Access;

/// <summary>
/// Test builds are folders, nothing replaces the old one, and it's easy to go on running it: the blind tester was on an
/// older build without knowing on 2026-11-05 and again on 2026-11-07, saving a notecard on the build that lost what's
/// embedded, a day after being told to update. So once after logging in a packaged build asks GitHub for the newest
/// release on its front door and says so if there's a newer one, the page in the history and Control+Shift+K's list.
/// GitHub sees the request as any download would; nothing of the person's goes with it. Silent when it can't tell.
/// SOJOURN_TEST_LATEST stands in for GitHub's answer. Written by Claude Opus 5.5, 2026-11-07.
/// </summary>
public sealed partial class MainForm
{
    const string ReleasesApi = "https://api.github.com/repos/studio-quire/sojourn-access/releases/latest";
    bool _askedNewest;

    async Task SayIfNewerBuildAsync()
    {
        if (_askedNewest) return;
        _askedNewest = true;
        var test = Environment.GetEnvironmentVariable("SOJOURN_TEST_LATEST");
        if (AppInfo.Version == "0.1.0.0" && test == null) return;   // a build in the repo
        await Task.Delay(TimeSpan.FromSeconds(10));   // the arrival and what came with it heard first
        string? latest = test, page = null;
        if (latest == null)
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SojournAccess");
                var o = JsonNode.Parse(await http.GetStringAsync(ReleasesApi)) as JsonObject;
                latest = o?.Str("tag_name")?.TrimStart('v');
                page = o?.Str("html_url");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return; }
        if (!Version.TryParse(latest, out var newest) || !Version.TryParse(AppInfo.Version, out var mine) || newest <= mine || IsDisposed) return;
        page ??= $"https://github.com/studio-quire/sojourn-access/releases/tag/v{latest}";
        var line = $"You're on Sojourn Access {AppInfo.Version}, and {latest} is out: {page}";
        Host?.Log.LogInformation("build: {Line}", line);
        NoteLinks(line, "");
        AppendChat(line);
        Reader?.Say(AnnounceKind.Notices, $"You're on Sojourn Access {AppInfo.Version}, and {latest} is out. Control+Shift+K has its link.", from: LineFrom.App);
    }
}
