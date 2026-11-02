using System.Text;
using System.Text.RegularExpressions;
using Sojourn.Session.Assistant;
using Sojourn.Session.Control;
using Sojourn.Session.Release;

namespace Sojourn.Client.Control;

/// <summary>
/// The settings menu's last section, about (TPV policy 1.g: the version shown, accurate source information): Sojourn's
/// name and version, the disclaimer, who made it, its licence and source, support; then report a problem (a page: what
/// a report holds and where it goes, review what's sent in the reader, send to Studio Quire, or save it for GitHub), the third-party notices in the reader, crash reports (ask, always, never: the launcher's) and, in the
/// advanced menu, the statistics' hardware ids. The words come from SojournRelease (Release/ReleaseApi.cs), so the
/// assistant tells the person the same. Written by Claude Opus 5.5, 2026-11-01.
/// </summary>
public sealed class AboutMenu
{
    readonly ReleaseApi _release;
    readonly Reader _reader;
    readonly SettingsApi _api;
    readonly Action _changed;
    readonly Action<string> _say;
    string? _outcome, _saved;
    volatile bool _busy;

    public const string ReportGroup = "report a problem";

    public AboutMenu(ReleaseApi release, Reader reader, SettingsApi api, Action changed, Action<string> say)
    {
        _release = release;
        _reader = reader;
        _api = api;
        _changed = changed;
        _say = say;
    }

    /// <summary>The words above the rows: the section's own, or the report page's.</summary>
    public IReadOnlyList<string> Text(string? group) =>
        group == ReportGroup ? ReportLines(ProviderKeys.RelayToken() != null) : Lines(_release.Version, ReleaseApi.Commit);

    public static string[] Lines(string version, string? commit) => new[]
    {
        $"Sojourn {version}" + (commit is { } c ? $" · {c}" : ""),
        ReleaseApi.Disclaimer,
        ReleaseApi.Credit,
        Lang.F("Free software under the GNU LGPL 2.1; the source is at {0}. Parts of the renderer are ported from the Second Life viewer (Copyright Linden Research, Inc.).", ReleaseApi.Source),
        ReleaseApi.Support,
    };

    public static string[] ReportLines(bool token) => new[]
    {
        "A report of this session", ProblemReport.WhatsSent(token),
        "To say what went wrong, ask the assistant to report a problem: your words go with it. " + ProblemReport.GitHubWords,
    };

    public List<(MenuRow Row, Action<int> Act)> Rows()
    {
        var rows = new List<(MenuRow, Action<int>)>
        {
            MenuRow.Group(ReportGroup, ReportRows),
            (new MenuRow("third-party notices", "read", false), _ => Notices()),
        };
        if (_api.ChoiceRow("crash_reports", "crash reports", VrSettings.CrashReportChoices) is { } crash) rows.Add(crash);
        if (_api.SwitchRow("stats_hardware_ids", "MAC and disk serial in statistics") is { } ids) rows.Add(ids);
        return rows;
    }

    List<(MenuRow Row, Action<int> Act)> ReportRows() => new()
    {
        (new MenuRow("review what's sent", "read", false), _ => Review()),
        (new MenuRow("send to Studio Quire", _busy ? "…" : "send", false, Note: _outcome), _ => Go()),
        (new MenuRow("save for GitHub", "save", false, Note: _saved), _ => Save()),
    };

    void Save() => Task.Run(() =>
    {
        _saved = _release.Make("", out var why) is { } r ? _release.SaveForGitHub(r) : $"Can't be made: {why}.";
        _say("about: " + _saved);
        _changed();
    });

    void Review() => Task.Run(() =>
    {
        if (_release.Make("", out var why) is not { } r) { Done($"Can't be made: {why}."); return; }
        _reader.Show(new ReaderDoc(ReaderKind.Notecard, Lang.F("What a report sends: {0} lines", r.Log.Lines),new[] { new ReaderPart(null, r.Log.Text.TrimEnd('\n')) }), atEnd: false);
    });

    void Go()
    {
        if (_busy) return;
        _busy = true;
        _outcome = null;
        Task.Run(async () =>
        {
            try
            {
                if (_release.Make("", out var why) is not { } r) { Done($"Can't be made: {why}."); return; }
                var (_, said) = await _release.SendAsync(r);
                Done(said);
            }
            catch (Exception e) { Done($"Not done: {e.Message}"); }
        });
    }

    void Done(string said)
    {
        _busy = false;
        _outcome = said;
        _say("about: " + said);
        _changed();
    }

    static ViewerStats.Gpu? _gpu;

    /// <summary>The GPU as the statistics report names it: the device, its maker, the driver's version in the maker's
    /// own numbering, video memory, and the Vulkan version it offers.</summary>
    public static ViewerStats.Gpu Gpu(Sojourn.Render.Gpu.VulkanContext vk)
    {
        if (_gpu is { } known) return known;
        var p = vk.Properties;
        uint d = p.DriverVersion, api = p.ApiVersion;
        var (vendor, driver) = p.VendorID switch
        {
            0x10DE => ("NVIDIA", $"{d >> 22}.{(d >> 14) & 0xFF:00}"),   // as NVIDIA numbers its drivers: 610.88
            0x8086 => ("Intel", $"{d >> 14}.{d & 0x3FFF}"),
            0x1002 => ("AMD", $"{d >> 22}.{(d >> 12) & 0x3FF}.{d & 0xFFF}"),
            var v => ($"0x{v:X4}", $"{d >> 22}.{(d >> 12) & 0x3FF}.{d & 0xFFF}"),
        };
        return _gpu = new ViewerStats.Gpu(vk.DeviceName, vendor, driver, (int)(vk.DeviceLocalHeapBytes / (1024 * 1024)),
            $"{api >> 22}.{(api >> 12) & 0x3FF}.{api & 0xFFF}");
    }

    /// <summary>THIRD_PARTY.md, embedded in the build (Sojourn.Client.csproj), as plain paragraphs in the reader.</summary>
    void Notices()
    {
        string text;
        using (var s = typeof(AboutMenu).Assembly.GetManifestResourceStream("THIRD_PARTY.md"))
            text = s is null ? Lang.F("This build doesn't carry its notices; they're in THIRD_PARTY.md at {0}.", ReleaseApi.Source)
                : Plain(new StreamReader(s, Encoding.UTF8).ReadToEnd());
        _reader.Show(new ReaderDoc(ReaderKind.Notecard, Lang.T("Third-party notices"), ReaderApi.Paragraphs(text)), atEnd: false);
    }

    /// <summary>Markdown read as text: no #, **, backticks or table rules; a table's rows as "a: b"; a paragraph's
    /// hard-wrapped lines joined (list items and rows keep their own lines).</summary>
    static string Plain(string md)
    {
        var lines = new List<string>();
        bool joinable = false;   // the last line is running prose a wrapped line continues
        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (Regex.IsMatch(line, @"^\|[\s\-|:]+\|$")) continue;   // a table's rule
            bool own = false;
            if (line.StartsWith('|'))
            {
                var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells is ["Package", "Licence"]) continue;   // its header
                line = string.Join(": ", cells);
                own = true;
            }
            bool heading = line.StartsWith('#');
            line = Regex.Replace(line, @"^#+\s*", "").Replace("**", "").Replace("`", "");
            if (line.Length == 0) { lines.Add(""); joinable = false; continue; }
            if (joinable && !own && !heading && !line.StartsWith("- ")) lines[^1] += " " + line.Trim();
            else lines.Add(line);
            joinable = !own && !heading;
        }
        return string.Join('\n', lines);
    }
}
