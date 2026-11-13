using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core;
using Sojourn.Core.Access;

namespace Sojourn.Access;

/// <summary>
/// Links (docs/access-app.md > Links): Enter on a history's line opens the link on it, and Control+Shift+K or //links lists
/// the last ones seen in chat, IMs and notices, whoever said them. A web page opens in the person's browser, a place
/// teleports, a person's link is their profile, a group's its details, and the other app links as OpenAppLinkAsync says; nothing else opens, whatever a line's link asks
/// for. On the way to the screen reader and the histories a Second Life link reads as what it is rather than its thirty-six
/// characters of id (FriendlyLinks), with what the words stood for remembered so they still open; the logs keep the link
/// itself. Written by Claude Opus 5.5, 2026-11-04, after the blind tester asked for clickable links.
/// </summary>
public sealed partial class MainForm
{
    const int LinksKept = 50;
    static readonly Regex LinkIn = new(@"\b(?:https?://|secondlife://|hop://)[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PlaceLink = new(@"^(?:secondlife://|hop://|https?://(?:maps|slurl)\.secondlife\.com/secondlife/|https?://(?:www\.)?slurl\.com/secondlife/)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PersonLink = new(@"^secondlife:///app/agent/(?<id>[0-9a-f-]{36})(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex GroupLink = new(@"^secondlife:///app/group/(?<id>[0-9a-f-]{36})(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex OtherAppLink = new(@"^secondlife:///app/(?<kind>[a-z_]+)(?:/(?<rest>[^?]*))?(?:\?(?<query>.*))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>The words a link reads as, and the link they stand for.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _linkWords = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, string> _groupNames = new();

    sealed record SeenLink(string Url, string From);
    readonly List<SeenLink> _links = new();   // the newest last

    static IEnumerable<string> LinksIn(string text) =>
        LinkIn.Matches(text).Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'', '"'));

    /// <summary>The first link in some text opened (a notecard's Control+K); false with none.</summary>
    bool OpenFirstLink(string text)
    {
        if (LinkOn(text) is not { } url) return false;
        OpenLink(url);
        return true;
    }

    /// <summary>The first link in some text, as itself or as the words it reads as.</summary>
    string? LinkOn(string text) => LinksIn(text).FirstOrDefault()
        ?? _linkWords.Where(w => text.Contains(w.Key, StringComparison.Ordinal)).OrderBy(w => text.IndexOf(w.Key, StringComparison.Ordinal)).Select(w => w.Value).FirstOrDefault();

    /// <summary>Second Life's links in a line as what they are: a group by name (once it's known; asked for the next time), a
    /// place by its region, the rest by their kind. Web pages stay as they are, being what a screen reader user expects to
    /// hear. Any thread.</summary>
    string FriendlyLinks(string text)
    {
        if (!text.Contains("secondlife", StringComparison.OrdinalIgnoreCase) && !text.Contains("slurl.com", StringComparison.OrdinalIgnoreCase)) return text;
        return LinkIn.Replace(text, m =>
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'', '"');
            var after = m.Value[url.Length..];
            if (WordsFor(url) is not { } words) return m.Value;
            // (one at a time: this runs on the reader's thread and the window's, and two links mustn't take the same words)
            lock (_linkWords)
            {
                // two places in one region are told apart by where
                if (_linkWords.TryGetValue(words, out var had) && had != url && Places.Parse(url) is { } p) words = $"{p.Region} at {p.X:0}, {p.Y:0} (a place)";
                // words still standing for another link are numbered, "(an event 2)", so Enter on a line opens that line's own
                // link: two events' links had both read "an event", and the newest opened from either (now that they teleport).
                // The same thing in another form (a profile's /about and /inspect) keeps its words.
                var plain = words;
                for (int n = 2; _linkWords.TryGetValue(words, out var other) && LinkKey(other) != LinkKey(url); n++)
                    words = plain.EndsWith(')') ? $"{plain[..^1]} {n})" : $"{plain} {n}";
                if (_linkWords.Count > 1000) _linkWords.Clear();
                _linkWords[words] = url;
            }
            return words + after;
        });
    }

    /// <summary>What a link points at, for telling two links apart: an app link by its kind and id (its action and query
    /// aside), anything else as written.</summary>
    static string LinkKey(string url) => OtherAppLink.Match(url) is { Success: true } m
        ? $"{m.Groups["kind"].Value.ToLowerInvariant()}/{m.Groups["rest"].Value.Split('/', 2)[0].ToLowerInvariant()}" : url;

    string? WordsFor(string url)
    {
        if (PersonLink.Match(url) is { Success: true } person && UUID.TryParse(person.Groups["id"].Value, out var who))
            return Host?.Session.Names.DisplayName(who) is { Length: > 0 } name ? $"{name} (a person)" : "someone (a person)";
        if (GroupLink.Match(url) is { Success: true } group && UUID.TryParse(group.Groups["id"].Value, out var gid))
            return GroupNameKnown(gid) is { Length: > 0 } g ? $"{g} (a group)" : "(a group)";
        if (PlaceLink.IsMatch(url) && Places.Parse(url) is { } place) return $"{place.Region} (a place)";
        if (OtherAppLink.Match(url) is not { Success: true } app) return null;
        var named = System.Text.RegularExpressions.Regex.Match(app.Groups["query"].Value, @"(?:^|&)name=(?<n>[^&]+)") is { Success: true } q ? Uri.UnescapeDataString(q.Groups["n"].Value.Replace('+', ' ')) : null;
        return app.Groups["kind"].Value.ToLowerInvariant() switch
        {
            // (in brackets: Enter on a line finds a link by its words, and "a parcel" alone is in anyone's sentence)
            "parcel" => "(a parcel)",
            "event" => "(an event)",
            "classified" => "(a classified ad)",
            "landmark" => "(a landmark)",
            "inventory" => named != null ? $"{named} (an item)" : "(an item)",
            "objectim" => named != null ? $"{named} (an object)" : "(an object)",
            _ => "(a link for viewers)",
        };
    }

    /// <summary>A group's name if it's one of hers or has been read; if not, read now for the next time it's said.</summary>
    string? GroupNameKnown(UUID id)
    {
        if (Host?.Session.State.Groups.TryGetValue(id, out var mine) == true) return mine.Name;
        if (_groupNames.TryGetValue(id, out var name)) return name;
        _groupNames[id] = "";
        _ = Task.Run(async () =>
        {
            var r = await _ops.ReadAsync("TesselGroups", "profile", new JsonObject { ["group_id"] = id.ToString() });
            if (r.Str("name") is { Length: > 0 } n) _groupNames[id] = n;
        });
        return null;
    }

    void NoteLinks(string text, string from)
    {
        foreach (var url in LinksIn(text))
        {
            _links.RemoveAll(l => l.Url == url);
            _links.Add(new SeenLink(url, from));
        }
        if (_links.Count > LinksKept) _links.RemoveRange(0, _links.Count - LinksKept);
    }

    string LinkWords(string url)
    {
        if (PersonLink.IsMatch(url)) return "a person's profile";
        if (GroupLink.Match(url) is { Success: true } group && UUID.TryParse(group.Groups["id"].Value, out var gid))
            return GroupNameKnown(gid) is { Length: > 0 } g ? $"{g}, a group" : "a group";
        if (AppLinkKind(url) is not null)
            return WordsFor(url) is { } words && words != "(a link for viewers)" ? (words.StartsWith('(') ? words[1..^1] : words) : "something only other viewers open";
        if (PlaceLink.IsMatch(url)) return Places.Parse(url) is { } place ? $"{place.Region}, a place" : "something only a viewer can open";
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Host}{u.AbsolutePath.TrimEnd('/')}, a web page" : url;
    }

    /// <summary>Enter in a history: the first link on the line the caret is on. False with none, so Enter goes on as usual.</summary>
    bool OpenLinkOn(TextBox box)
    {
        var text = box.Text;
        int at = Math.Min(box.SelectionStart, text.Length);
        int from = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1, to = text.IndexOf('\n', at);
        var line = text[from..(to < 0 ? text.Length : to)];
        if (LinkOn(line) is not { } url) return false;
        OpenLink(url);
        return true;
    }

    void OpenLink(string url)
    {
        if (PersonLink.Match(url) is { Success: true } person)
        {
            var id = new UUID(person.Groups["id"].Value);
            _ = ProfileAsync(id, id.ToString());
            return;
        }
        if (GroupLink.Match(url) is { Success: true } group && UUID.TryParse(group.Groups["id"].Value, out var gid))
        {
            _ = GroupInfoAsync(gid, GroupNameKnown(gid) is { Length: > 0 } g ? g : "That group");
            return;
        }
        if (AppLinkKind(url) is { } app)
        {
            _ = OpenAppLinkAsync(url, app.Kind, app.Path, app.Query);
            return;
        }
        if (PlaceLink.IsMatch(url))
        {
            if (Places.Parse(url) is { } place) _ = _cmd.GoToAsync(place);
            else NotOpened(url, "That link is for something Sojourn Access can't open.");
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            NotOpened(url, "That link is for something Sojourn Access can't open.");
            return;
        }
        Answer($"Opening {u.Host} in your browser.", LineFrom.People);
        if (Environment.GetEnvironmentVariable("SOJOURN_NO_BROWSER") == "1") return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Answer($"Couldn't open it: {e.Message}"); }
    }

    /// <summary>A secondlife:///app link other than a place's (app/teleport, region and worldmap are places), split up.</summary>
    static (string Kind, string Path, string Query)? AppLinkKind(string url) =>
        OtherAppLink.Match(url) is { Success: true } m && m.Groups["kind"].Value.ToLowerInvariant() is var kind && kind is not ("teleport" or "region" or "worldmap")
            ? (kind, m.Groups["rest"].Value, m.Groups["query"].Value) : null;

    /// <summary>Second Life's other app links (the blind tester's partner sent her a place she couldn't open, 2026-11-11). A
    /// parcel, an event or a classified is asked where it is and teleported to, as the viewer's panels for them offer; a
    /// landmark or an item opens only from the inventory it's in, so one of hers is used and anyone else's is said as such;
    /// an object's says where it is; the rest are said as what they'd have a viewer do.</summary>
    async Task OpenAppLinkAsync(string url, string kind, string rest, string query)
    {
        var id = rest.Split('/', 2)[0];
        string? Param(string key) => Regex.Match(query, $@"(?:^|&){key}=(?<v>[^&]*)") is { Success: true } m
            ? Uri.UnescapeDataString(m.Groups["v"].Value.Replace('+', ' ')) : null;
        switch (kind)
        {
            case "parcel" or "event" or "classified":
            {
                var what = kind == "classified" ? "classified ad" : kind;
                // (asked of the grid, up to 4 s and the network's time: said meanwhile, and a second Enter doesn't ask again)
                if (_linkLooking != null) { Answer($"Still looking up {_linkLooking}."); return; }
                _linkLooking = $"where that {what} is";
                JsonObject r;
                Answer($"Looking up where that {what} is.");
                try { r = await _ops.ReadAsync("SojournSearch", "where", new JsonObject { ["kind"] = kind, ["id"] = id }); }
                finally { _linkLooking = null; }
                if (PersonOps.Error(r) is { } e) { NotOpened(url, $"Couldn't find where that {what} is: {e}"); return; }
                if (r.Str("region") is not { Length: > 0 } region || r["pos"] is not JsonArray pos || pos.Count < 3)
                {
                    NotOpened(url, $"Couldn't find where that {what} is.");
                    return;
                }
                var when = r.Str("starts") is not { } starts ? "" : r["on_now"] is not null ? " on now" : r["ended"] is not null ? $" that was on {starts}" : $" starting {starts}";
                // one line with the teleport's, its name first: said apart, the teleport's line would cut it off unheard
                var label = (r.Str("name") is { Length: > 0 } n ? $"{n}, " : "") + $"{(kind == "event" ? "an" : "a")} {what}{when}";
                await _cmd.GoToAsync(new Place(region, Json.AsDouble(pos[0]) ?? 128, Json.AsDouble(pos[1]) ?? 128, Json.AsDouble(pos[2]) ?? 30), label);
                return;
            }
            case "landmark":
            {
                // hers if her inventory has it, read from the service when its folder isn't loaded yet (a link to someone
                // else's waits for that until it's sure: said meanwhile)
                InventoryItem? mark = null;
                if (UUID.TryParse(id, out var lm) && Store is { } store)
                {
                    if (store.Contains(lm)) mark = store[lm] as InventoryItem;
                    else
                    {
                        if (_linkLooking != null) { Answer($"Still looking up {_linkLooking}."); return; }
                        _linkLooking = "that landmark in your inventory";
                        Answer("Looking for that landmark in your inventory.");
                        try { mark = await MineAsync(lm); }
                        finally { _linkLooking = null; }
                    }
                }
                if (mark is { AssetType: AssetType.Landmark })
                {
                    _ = _cmd.TeleportLandmarkAsync(mark.UUID, mark.Name);
                    return;
                }
                NotOpened(url, "That's a landmark in someone else's inventory, so only their viewer can open it. They can give you the landmark itself, or send its place as an SLurl.");
                return;
            }
            case "inventory":
            {
                if (UUID.TryParse(id, out var item) && Store is { } store && store.Contains(item) && store[item] is { } mine)
                {
                    var folder = store.Contains(mine.ParentUUID) ? store[mine.ParentUUID].Name : null;
                    Answer($"{mine.Name} is in your inventory{(folder != null ? $", in {folder}" : "")}.");
                    return;
                }
                // (its name is the sender's words, so the log leaves it out as it does theirs)
                NotOpened(url, $"That's {(Param("name") is { Length: > 0 } name ? name : "something")} in someone else's inventory, so only their viewer can open it. They can give it to you.",
                    LineFrom.People);
                return;
            }
            case "agent" when UUID.TryParse(id, out var who):
                _ = ProfileAsync(who, who.ToString());
                return;
            case "objectim":
            {
                var name = Param("name") is { Length: > 0 } n ? n : "That object";
                if (Param("slurl") is { } at && Places.Parse(at) is { } p)
                    Answer($"{name}, an object, in {p}. //tp {p.Region}/{p.X:0}/{p.Y:0}/{p.Z:0} goes there.", LineFrom.People);
                else NotOpened(url, $"{name}, an object; the link doesn't say where it is.", LineFrom.People);
                return;
            }
        }
        NotOpened(url, kind switch
        {
            "maptrackavatar" => "That link is for tracking someone on the map, which Sojourn Access doesn't open.",
            "sharewithavatar" => "That link is for sharing something with someone, which Sojourn Access doesn't open.",
            "wear_folder" => "That link is for wearing a folder, which Sojourn Access doesn't open.",
            "search" => "That link is for a search in the viewer, which Sojourn Access doesn't open. Alt+5 searches.",
            _ => "That link is for something only other viewers open.",
        });
    }

    /// <summary>Said, and the link's shape in the log (not its words, which are someone's), so a log says what kind it was.</summary>
    void NotOpened(string url, string words, LineFrom from = LineFrom.App)
    {
        Host?.Log.LogInformation("link not opened: {Shape}", LinkShape(url));
        Answer(words, from);
    }

    /// <summary>What a link's Enter is looking up meanwhile ("where that event is"), or null.</summary>
    string? _linkLooking;

    /// <summary>An item of hers by id, asked of the inventory service (a link to her own landmark in a folder not loaded
    /// yet); null if it isn't hers or doesn't come within 2.5 s (the service answers nothing for another's: the wait runs
    /// out).</summary>
    async Task<InventoryItem?> MineAsync(UUID id)
    {
        if (_ops.Client is not { } c) return null;
        using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
        try { return await c.Inventory.FetchItemAsync(id, c.Self.AgentID, quick.Token); }
        catch (Exception e) when (e is OperationCanceledException or System.Net.Http.HttpRequestException or InvalidOperationException) { return null; }
    }

    /// <summary>"secondlife:///app/landmark/[id]/[word]", "https://maps.secondlife.com/secondlife/[word]/[n]/[n]/[n]?[title]":
    /// the scheme, Second Life's hosts and app kinds, then each part only as a number, an id or a word, and the query's
    /// keys without their values.</summary>
    static string LinkShape(string url)
    {
        int s = url.IndexOf("://", StringComparison.Ordinal);
        if (s < 0) return $"[{url.Length} characters, no scheme]";
        var scheme = url[..s].ToLowerInvariant();
        var after = url[(s + 3)..];
        int q = after.IndexOfAny(['?', '#']);
        var path = q < 0 ? after : after[..q];
        var keys = q < 0 ? "" : string.Join(",", after[(q + 1)..].Split('&').Select(kv => kv.Split('=')[0]).Where(k => Regex.IsMatch(k, @"^[A-Za-z_]{1,20}$")));
        var parts = path.Split('/');
        string Part(string x) => x.Length == 0 ? "" : UUID.TryParse(x, out _) ? "[id]" : Regex.IsMatch(x, @"^-?[\d.]+$") ? "[n]" : "[word]";
        var keep = 0;
        if (scheme == "secondlife" && parts.Length > 2 && parts[0].Length == 0 && parts[1].Equals("app", StringComparison.OrdinalIgnoreCase)) keep = 3;
        else if (scheme is "http" or "https"
                 && (parts[0].EndsWith("secondlife.com", StringComparison.OrdinalIgnoreCase) || parts[0].EndsWith("slurl.com", StringComparison.OrdinalIgnoreCase)))
            keep = Math.Min(2, parts.Length);
        var shape = string.Join("/", parts.Select((x, i) => i < keep ? x.ToLowerInvariant() : Part(x)));
        return $"{scheme}://{shape}{(keys.Length > 0 ? $"?[{keys}]" : q >= 0 ? "?" : "")}";
    }

    void ShowLinks()
    {
        if (_links.Count == 0) { Answer("No links yet. Links in chat, IMs and notices are listed here."); return; }
        var links = Enumerable.Reverse(_links).ToList();
        using var form = new Form
        {
            Name = "links", Text = "Links", AccessibleName = "Links", Size = new Size(560, 400),
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false,
        };
        var list = new ListBox { Name = "linksList", AccessibleName = "Links, the newest first. Enter opens one.", Dock = DockStyle.Fill };
        list.Items.AddRange(links.Select(l => (object)(l.From.Length > 0 ? $"{LinkWords(l.Url)}, from {l.From}" : LinkWords(l.Url))).ToArray());
        list.SelectedIndex = 0;
        var open = new Button { Name = "linksOpen", Text = "Open", AutoSize = true, DialogResult = DialogResult.OK };
        var close = new Button { Name = "linksClose", Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        form.AcceptButton = open;
        form.CancelButton = close;
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        row.Controls.Add(open);
        row.Controls.Add(close);
        form.Controls.Add(list);
        form.Controls.Add(row);
        form.ActiveControl = list;
        if (form.ShowDialog(this) == DialogResult.OK && list.SelectedIndex >= 0) OpenLink(links[list.SelectedIndex].Url);
    }
}
