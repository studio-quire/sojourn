using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Session.Assistant;
using Sojourn.Session.Control;

namespace Sojourn.Session.Release;

/// <summary>
/// SojournRelease: what the TPV policy asks a viewer to say about itself (1.g's version display, 1.c's disclaimer, 3.b's
/// source), for the assistant to tell the person and for the menu's about section to show; the viewer statistics
/// report as it would go (2.h, <see cref="ViewerStats"/>); and a problem report, which waits for the person's own press
/// (the confirmation shows what goes and where) before it's sent to Studio Quire. Nothing here
/// touches the login or credentials (docs/tpv-policy.md).
/// </summary>
public sealed class ReleaseApi : LeapApi
{
    /// <summary>The disclaimer TPVs show (TPV Policy 1.c).</summary>
    public const string Disclaimer = "This software is not provided or supported by Linden Lab, the makers of Second Life.";
    public const string Licence = "Parts of Sojourn's renderer are ported from the Second Life viewer's source (LGPL 2.1, "
        + "Copyright Linden Research, Inc.); it uses LibreMetaverse (BSD).";
    public const string Source = "github.com/studio-quire/sojourn";
    public const string Support = "There's no formal support. Report a problem from the menu (about › report a problem), "
        + "or as an issue at github.com/studio-quire/sojourn/issues.";
    /// <summary>Who made it (the studio's CLAUDE.md > Credit: the models, and the person by their actual role).</summary>
    public const string Credit = "Made by Claude models (Anthropic's AI) at Studio Quire, from Shoalstone's idea and direction.";

    public override string Name => "SojournRelease";
    public override string Desc => "about Sojourn itself: its name, version, disclaimer, licence, source and support; the viewer "
        + "statistics it sends Linden Lab; a problem report to Studio Quire (sent only at the person's press)";
    public override bool NeedsSession => false;

    /// <summary>The statistics report, once the session host has started it.</summary>
    public ViewerStats? Stats { get; set; }
    /// <summary>Where the app keeps its logs and reports, and this run's log.</summary>
    public string BaseDir { get; set; } = "";
    public string? LogPath { get; set; }
    /// <summary>The version sent at login.</summary>
    public string Version => S.Host.Version;
    public static string? Commit { get; } = ProblemReport.CommitOf(Assembly.GetEntryAssembly() ?? typeof(ReleaseApi).Assembly);

    // a report as the person was asked about it, for the op that runs once they've said yes
    readonly ConditionalWeakTable<JsonObject, ProblemReport.Report> _asked = new();

    public ReleaseApi(AgentSession s) : base(s)
    {
        Op("about", "-> {name, channel, version, commit, disclaimer, licence, source, support, credit}: what the menu's about "
            + "section shows; the channel and version are the ones sent at login", _ => About(s));
        Op("stats", "{send?} -> {outcome, last_sent, report}: the viewer statistics report as it would go to the region (sent "
            + "after login, every 300 s and at logout; send: true sends one now). No other residents' data; hardware ids "
            + "only with stats_hardware_ids", async (a, ct) =>
        {
            if (Stats is not { } st) throw new LeapError("the statistics report isn't running in this app");
            if (a["send"]?.GetValue<bool>() == true)
            {
                if (s.Client is not { } c || !s.Online) throw new LeapError("not in a region");
                await st.SendAsync(c, ct);
            }
            return new JsonObject
            {
                ["outcome"] = st.LastOutcome,
                ["last_sent"] = st.LastAt?.ToString("u"),
                ["report"] = JsonNode.Parse(LibreMetaverse.StructuredData.OSDParser.SerializeJsonString(st.Preview(), preserveDefaults: true)),
            };
        });
        Op("reportProblem", "{note} -> {sent | saved, said}: a report of a problem with Sojourn: this session's log (other "
            + "people's words left out, their names stand-ins) and note, the person's own words about what went wrong. "
            + "The person confirms on the panel first; it goes to Studio Quire's relay, with their Studio Quire token if they "
            + "have one, else without (its daily limits). Saving it for a GitHub issue instead is the menu's (about > report "
            + "a problem)", async (a, ct) =>
        {
            if (!_asked.TryGetValue(a, out var r)) r = Make(a.Str("note") ?? "", out var why) ?? throw new LeapError(why!);
            var (sent, said) = await SendAsync(r, ct);
            return new JsonObject { ["sent"] = sent, ["said"] = said };
        });
    }

    public static JsonObject About(AgentSession s) => new()
    {
        ["name"] = "Sojourn",
        ["channel"] = s.Host.Channel,
        ["version"] = s.Host.Version,
        ["commit"] = Commit,
        ["disclaimer"] = Disclaimer,
        ["licence"] = Licence,
        ["source"] = Source,
        ["support"] = Support,
        ["credit"] = Credit,
    };

    /// <summary>The report of this session's log, filtered; null with why.</summary>
    public ProblemReport.Report? Make(string note, out string? why)
    {
        if (ProblemReport.Latest(BaseDir, LogPath) is not { } log) { why = "there's no log this version can send"; return null; }
        bool current = LogPath != null && string.Equals(Path.GetFullPath(log.Path), Path.GetFullPath(LogPath), StringComparison.OrdinalIgnoreCase);
        return ProblemReport.Make(log, note, null, S.Host.Version, Commit, current, out why);
    }

    /// <summary>Sent to the relay, with the person's Studio Quire token if one is kept, else without.</summary>
    public async Task<(bool Sent, string Said)> SendAsync(ProblemReport.Report r, CancellationToken ct = default)
    {
        var token = ProviderKeys.RelayToken();
        var (sent, said) = await ProblemReport.SendAsync(r, token, ct);
        S.Log.LogInformation("problem report: {Outcome}{How} ({Lines} lines, {Out} with words left out)", sent ? "sent" : said,
            token != null ? " with the token" : " without a token", r.Log.Lines, r.Log.LeftOutLines);
        return (sent, said);
    }

    /// <summary>Saved for a GitHub issue, shown in Explorer, the issues page opened; what to tell the person.</summary>
    public string SaveForGitHub(ProblemReport.Report r)
    {
        var path = ProblemReport.Save(r, BaseDir);
        ProblemReport.OpenForGitHub(path);
        S.Log.LogInformation("problem report: saved for GitHub ({File})", Path.GetFileName(path));
        return $"Saved as {Path.GetFileName(path)}, shown on the PC; GitHub's issues page is open to attach it.";
    }

    /// <summary>The confirmation's case (Confirmations.Cases): a problem report waits for the person, who's shown what
    /// goes and where, with the log's lines behind its list key.</summary>
    public Confirmations.Case Confirmation() => new(
        (pump, op, _) => pump == Name && op == "reportProblem" ? "report" : null,
        (_, _, a) =>
        {
            var note = a.Str("note") ?? "";
            if (Make(note, out var why) is not { } r) return Task.FromResult((Lang.T("Report a problem to Studio Quire?"), Lang.F("It can't be made: {0}.", why)));
            _asked.AddOrUpdate(a, r);
            bool token = ProviderKeys.RelayToken() != null;
            var lines = r.Log.Text.Split('\n');
            if (lines.Length > 4000) lines = lines.Take(2000).Append(Lang.F("... {0} lines not shown here ...", lines.Length - 4000)).Concat(lines.TakeLast(2000)).ToArray();
            Confirmations.Listed(lines, 0, "lines of the log");   // (behind the confirmation's list key)
            var detail = (note.Length > 0 ? Lang.F("Your note: “{0}”.", note) : Lang.T("No note.")) + " "
                + Lang.F(token
                    ? "This session's log goes with it to Studio Quire's relay, relay.studioquire.org, with your token, kept 30 days: {0} lines, other people's words left out and their names made stand-ins."
                    : "This session's log goes with it to Studio Quire's relay, relay.studioquire.org, kept 30 days: {0} lines, other people's words left out and their names made stand-ins; your IP address isn't kept.",
                    r.Log.Lines);
            return Task.FromResult((Lang.T("Send a problem report to Studio Quire?"), detail));
        });

    /// <summary>Its settings, with SojournSettings (both apps: the statistics' hardware ids; the VR client's launcher:
    /// crash reports).</summary>
    public static void Settle(SettingsApi api, VrSettings st, bool launcher)
    {
        api.Add("stats_hardware_ids", new("about", "the viewer statistics report Sojourn sends Linden Lab (as every viewer does) "
            + "also carries this PC's MAC address and a hash of its C: drive's serial number, as the official viewer's does; "
            + "off (the default) leaves them out", "on, off",
            () => st.StatsHardwareIds, SettingsApi.Switch("stats_hardware_ids", on => st.StatsHardwareIds = on)));
        if (launcher)
            api.Add("crash_reports", new("about", "when Sojourn crashes, the launcher asks whether to send a report to Studio "
                + "Quire (ask, the default), sends it without asking (always), or only says it stopped (never). A report holds "
                + "the session's log with other people's words left out", string.Join(", ", VrSettings.CrashReportChoices),
                () => st.CrashReports, SettingsApi.Choice("crash_reports", VrSettings.CrashReportChoices, i => st.CrashReports = VrSettings.CrashReportChoices[i])));
    }
}
