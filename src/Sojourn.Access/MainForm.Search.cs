using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Access;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// Search (Alt+5): words, what to look for (people, places, events, groups, classifieds), for places, events and
/// classifieds a category and for events a day, Enter, and the results as a list with the focus on the first ("4 found.").
/// A page of the grid's results at a time: a last row lists the next where the grid has more. Enter on a result does the
/// obvious thing (an IM to a person, a teleport to a place, an event or a classified, a group's details); the Applications
/// key lists the rest (a person's profile, teleport offer, friendship; a place's about; joining a group, behind its own
/// question in Notices).
/// </summary>
public sealed partial class MainForm
{
    TabPage _searchPage = null!;
    TextBox _searchWords = null!;
    ComboBox _searchKind = null!, _searchCategory = null!, _searchDay = null!;
    Label _searchCategoryLabel = null!, _searchDayLabel = null!;
    ListView _results = null!;
    ContextMenuStrip _resultMenu = null!;

    sealed record Found(string Kind, string Name, UUID Id, string? GoTo, string About);

    static readonly string[] SearchKinds = { "People", "Places", "Events", "Groups", "Classifieds" };
    static readonly (string Words, string When)[] SearchDays =
    {
        ("On now and later today", "now"), ("All of today", "today"), ("Tomorrow", "tomorrow"), ("In 2 days", "2"), ("In 3 days", "3"),
        ("In 4 days", "4"), ("In 5 days", "5"), ("In 6 days", "6"), ("In 7 days", "7"),
    };

    /// <summary>What the grid sends at a time: a full page means there may be more. (Second Life's directory: 100.)</summary>
    const int SearchPage = 100;
    /// <summary>A page of groups, the kind a run can fill on the local grid, smaller for the test of the last row.</summary>
    static readonly int GroupsPage = int.TryParse(Environment.GetEnvironmentVariable("SOJOURN_TEST_GROUPS_PAGE"), out var gp) ? gp : SearchPage;

    sealed record MoreResults;
    sealed record SearchRun(string Kind, string Words, string? Category, string? When, List<Found> Found, HashSet<Found> Seen)
    {
        public int Next;
    }
    SearchRun? _lastSearch;

    void BuildSearch()
    {
        _searchPage = new TabPage("Search") { Name = "searchPage", AccessibleName = "Search" };
        _searchWords = new LineBox { Name = "searchWords", AccessibleName = "Search for", Dock = DockStyle.Fill };
        _searchKind = new ComboBox { Name = "searchKind", AccessibleName = "Look for", DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        _searchKind.Items.AddRange(SearchKinds);
        _searchKind.SelectedIndex = 0;
        _results = List("results", "Results");
        _resultMenu = new ContextMenuStrip { Name = "resultMenu", AccessibleName = "Actions" };
        _searchCategory = new ComboBox { Name = "searchCategory", AccessibleName = "Category", DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        _searchDay = new ComboBox { Name = "searchDay", AccessibleName = "Day", DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        _searchDay.Items.AddRange(SearchDays.Select(d => (object)d.Words).ToArray());
        _searchDay.SelectedIndex = 0;
        _searchCategoryLabel = new Label { Text = "&Category", AutoSize = true, Anchor = AnchorStyles.Left };
        _searchDayLabel = new Label { Text = "D&ay", AutoSize = true, Anchor = AnchorStyles.Left };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(new Label { Text = "Search &for", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        t.Controls.Add(_searchWords, 1, 0);
        t.Controls.Add(new Label { Text = "&Look for", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        t.Controls.Add(_searchKind, 3, 0);
        t.Controls.Add(_searchCategoryLabel, 0, 1);
        t.Controls.Add(_searchCategory, 1, 1);
        t.Controls.Add(_searchDayLabel, 2, 1);
        t.Controls.Add(_searchDay, 3, 1);
        t.Controls.Add(_results, 0, 2);
        t.SetColumnSpan(_results, 4);
        _searchWords.TabIndex = 0; _searchKind.TabIndex = 1; _searchCategory.TabIndex = 2; _searchDay.TabIndex = 3; _results.TabIndex = 4;
        _searchPage.Controls.Add(t);
        _searchKind.SelectedIndexChanged += (_, _) => SearchKindShown();
        SearchKindShown();

        _searchWords.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            _ = SearchAsync();
        };
        foreach (var box in new[] { _searchKind, _searchCategory, _searchDay })
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                _ = SearchAsync();
            };
        _results.KeyDown += (_, e) =>
        {
            if (_results.FocusedItem?.Tag is MoreResults && e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; _ = SearchAsync(more: true); return; }
            if (_results.FocusedItem?.Tag is not Found f) return;
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; ResultActions(f).FirstOrDefault()?.Do(); }
            else if (e.KeyCode == Keys.Apps || e.KeyData == (Keys.Shift | Keys.F10)) { e.Handled = e.SuppressKeyPress = true; ShowResultMenu(f); }
        };
        _results.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && _results.FocusedItem?.Tag is Found f) ShowResultMenu(f); };
        Fill(_results, new(), "Type words, choose what to look for, then Enter.");
    }

    /// <summary>Category and day only where the grid's search has them; a hidden box isn't a Tab stop.</summary>
    void SearchKindShown()
    {
        var kind = SearchKinds[Math.Max(0, _searchKind.SelectedIndex)];
        var names = kind switch
        {
            "Places" => SearchOps.PlaceCategories, "Events" => SearchOps.EventCategories, "Classifieds" => SearchOps.ClassifiedCategories, _ => null,
        };
        _searchCategory.Items.Clear();
        if (names != null)
        {
            _searchCategory.Items.AddRange(names.Select(n => (object)(char.ToUpperInvariant(n[0]) + n[1..])).ToArray());
            _searchCategory.SelectedIndex = 0;
        }
        _searchCategory.Visible = _searchCategoryLabel.Visible = names != null;
        _searchDay.Visible = _searchDayLabel.Visible = kind == "Events";
    }

    static string NothingFound(string words) => words.Length > 0 ? $"Nothing found for {words}." : "Nothing found.";

    bool _searching;

    async Task SearchAsync(bool more = false)
    {
        var last = more ? _lastSearch : null;
        if (more && last == null) return;
        var words = last?.Words ?? _searchWords.Text.Trim();
        var kind = last?.Kind ?? SearchKinds[Math.Max(0, _searchKind.SelectedIndex)];
        if (!more && words.Length < (kind is "People" or "Groups" ? 2 : kind == "Events" ? 0 : 3))
        {
            Reader?.Answer(kind == "Events" ? "Searching events needs no words; Enter lists what's on now." : $"Type at least {(kind is "People" or "Groups" ? 2 : 3)} letters.", keep: false);
            if (kind != "Events") return;
        }
        if (_searching || Host?.Session.Online != true) return;
        _searching = true;
        Reader?.Answer(more ? "Looking for more." : $"Searching {kind.ToLowerInvariant()}.", keep: false);
        try
        {
            var run = last ?? new SearchRun(kind, words,
                _searchCategory.Visible && _searchCategory.SelectedIndex > 0 ? _searchCategory.Text.ToLowerInvariant() : null,
                _searchDay.Visible ? SearchDays[Math.Max(0, _searchDay.SelectedIndex)].When : null, new(), new());
            _lastSearch = run;
            var (page, error, full) = await SearchPageAsync(run);
            // (a grid that ignores where to start sends the same again: nothing new, so no more)
            var fresh = page.Where(run.Seen.Add).ToList();
            run.Found.AddRange(fresh);
            run.Next += page.Count;
            full = full && fresh.Count > 0;
            var rows = run.Found.Select((f, i) => (f.Name, $"r{i}", (object)f)).ToList();
            if (full) rows.Add(("More results: Enter lists the next.", "more", new MoreResults()));
            Fill(_results, rows, error != null ? $"The search didn't work: {error}" : NothingFound(words));
            if (more && fresh.Count > 0 && _results.Items[$"r{run.Found.Count - fresh.Count}"] is { } firstNew)
            {
                firstNew.Selected = true;
                ReaderList.MakeCurrent(firstNew);
                firstNew.EnsureVisible();
            }
            _results.Focus();
            var plenty = full ? ", and there are more: the last row lists them" : "";
            Reader?.Answer(error != null ? $"The search didn't work: {error}"
                : more ? (fresh.Count == 0 ? "No more." : $"{fresh.Count} more, {run.Found.Count} in all{plenty}.")
                : run.Found.Count == 0 ? NothingFound(words) : $"{run.Found.Count} found{plenty}.", keep: false);
        }
        finally { _searching = false; }
    }

    /// <summary>One page: what's in it (as many as the grid sent, for where the next starts), any error, whether it was full.</summary>
    async Task<(List<Found> Page, string? Error, bool Full)> SearchPageAsync(SearchRun run)
    {
        var (kind, words) = (run.Kind, run.Words);
        JsonObject Paged(string? text = null)
        {
            var args = new JsonObject { ["limit"] = SearchPage };
            if (text is { Length: > 0 }) args["text"] = text;
            if (run.Category != null) args["category"] = run.Category;
            if (run.Next > 0) args["start"] = run.Next;
            return args;
        }
        var found = new List<Found>();
        string? error = null;
        bool full = false;
        switch (kind)
        {
            case "People":
            {
                // (the avatar picker sends all it has at once)
                var r = await _ops.ReadAsync("TesselAgent", "findAvatar", new JsonObject { ["name"] = words, ["limit"] = SearchPage });
                error = PersonOps.Error(r);
                    foreach (var p in (r["people"] as JsonArray ?? new()).OfType<JsonObject>())
                        if (UUID.TryParse(p.Str("id"), out var id))
                        {
                            var shown = p.Str("display_name") is { Length: > 0 } dn ? dn : p.Str("legacy_name") ?? "someone";
                            var legacy = p.Str("legacy_name") ?? "";
                            found.Add(new("person", legacy.Length > 0 && !legacy.Equals(shown, StringComparison.OrdinalIgnoreCase) ? $"{shown} ({legacy})" : shown, id, null, ""));
                        }
                break;
            }
            case "Places":
            {
                var r = await _ops.ReadAsync("SojournSearch", "places", Paged(words));
                error = PersonOps.Error(r);
                full = r.Int("found", 0) >= SearchPage;
                foreach (var p in (r["places"] as JsonArray ?? new()).OfType<JsonObject>())
                {
                    var traffic = Json.AsDouble(p["traffic"]) is { } tr && tr > 0 ? $", traffic {tr:0}" : "";
                    found.Add(new("place", $"{p.Str("name")}, in {p.Str("region")}{traffic}", UUID.Zero, p.Str("go_to"), p.Str("about") ?? ""));
                }
                break;
            }
            case "Events":
            {
                var args = Paged(words);
                if (run.When != null) args["when"] = run.When;
                var r = await _ops.ReadAsync("SojournSearch", "events", args);
                error = PersonOps.Error(r);
                full = r.Int("found", 0) >= SearchPage;
                foreach (var p in (r["events"] as JsonArray ?? new()).OfType<JsonObject>())
                {
                    var when = p["on_now"]?.GetValue<bool>() == true ? "on now" : $"starts {p.Str("starts")}";
                    found.Add(new("event", $"{p.Str("name")}, {when}, in {p.Str("region")}", UUID.Zero, p.Str("go_to"), p.Str("about") ?? ""));
                }
                break;
            }
            case "Groups":
            {
                var args = new JsonObject { ["text"] = words };
                if (run.Next > 0) args["start"] = run.Next;
                var r = await _ops.ReadAsync("TesselGroups", "search", args);
                error = PersonOps.Error(r);
                foreach (var g in (r["groups"] as JsonArray ?? new()).OfType<JsonObject>())
                    if (UUID.TryParse(g.Str("group_id"), out var id))
                    {
                        int members = (int)(Json.AsDouble(g["members"]) ?? 0);
                        found.Add(new("group", $"{g.Str("name")}, {(members == 1 ? "1 member" : $"{members} members")}", id, null, ""));
                    }
                full = found.Count >= GroupsPage;
                break;
            }
            default:
            {
                // (the grid's classifieds search has no pages)
                var args = Paged(words);
                args.Remove("start");
                var r = await _ops.ReadAsync("SojournSearch", "classifieds", args);
                error = PersonOps.Error(r);
                foreach (var p in (r["classifieds"] as JsonArray ?? new()).OfType<JsonObject>())
                    found.Add(new("classified", $"{p.Str("name")}, in {p.Str("region")}", UUID.Zero, p.Str("go_to"), p.Str("about") ?? ""));
                break;
            }
        }
        return (found, error, full);
    }

    void ShowResultMenu(Found f)
    {
        _resultMenu.Items.Clear();
        if (f.Kind == "person") AddPersonItems(_resultMenu, f.Id, PersonNameOf(f), enterIms: true);
        else
            foreach (var a in ResultActions(f))
            {
                var act = a;
                _resultMenu.Items.Add(new ToolStripMenuItem(a.Text, null, (_, _) => act.Do()));
            }
        var r = _results.FocusedItem?.Bounds ?? Rectangle.Empty;
        _resultMenu.Show(_results, new Point(r.Left + 10, r.Bottom));
    }

    /// <summary>A person found, as "Shown (legacy name)" reads in the list, by the name they show.</summary>
    static string PersonNameOf(Found f) => f.Name.Split(" (")[0];

    List<InvAction> ResultActions(Found f)
    {
        var list = new List<InvAction>();
        switch (f.Kind)
        {
            case "person":
                list.AddRange(PersonActions(f.Id, PersonNameOf(f)));
                break;
            case "group":
                list.Add(new("&About the group", () => _ = GroupInfoAsync(f.Id, f.Name)));
                list.Add(new("&Join", () => _ = JoinGroupAsync(f.Id, f.Name)));
                break;
            default:
                if (f.GoTo != null && RegionPlace(f.GoTo) is { } p) list.Add(new("&Teleport", () => _ = _cmd.GoToAsync(p)));
                list.Add(new("&About", () => Answer(f.About.Length > 0 ? f.About : $"Nothing more said about {f.Name}.",
                    f.About.Length > 0 ? LineFrom.People : LineFrom.App)));
                break;
        }
        return list;
    }

    async Task PersonIdOpAsync(string op, UUID id, string done)
    {
        var r = await _ops.DoAsync("TesselAgent", op, new JsonObject { ["to"] = id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't go: {e}" : done);
    }

    async Task GroupInfoAsync(UUID id, string name)
    {
        var r = await _ops.ReadAsync("TesselGroups", "profile", new JsonObject { ["group_id"] = id.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read {name}: {e}"); return; }
        int members = (int)(Json.AsDouble(r["members"]) ?? 0);
        var fee = Json.AsDouble(r["fee"]) is { } f && f > 0 ? $"joining costs L${f:0}" : "joining is free";
        var open = r["open_enrollment"]?.GetValue<bool>() == true ? $"anyone may join, {fee}" : "by invitation";
        var mine = r["member"]?.GetValue<bool>() == true ? " You're a member." : "";
        if (r["member"]?.GetValue<bool>() == true && await WornTitleAsync(id) is { } title) mine += $" Your title: {title}.";
        var founder = r.Str("founder") is { Length: > 0 } fd ? $" Founded by {fd}." : "";
        var charter = r.Str("charter") is { Length: > 0 } ch ? $" Charter: {ch.Trim()}" : "";
        Answer($"{r.Str("name") ?? name}: {(members == 1 ? "1 member" : $"{members} members")}, {open}.{founder}{mine}{charter}",
            charter.Length > 0 ? LineFrom.People : LineFrom.App);
    }

    async Task JoinGroupAsync(UUID id, string name)
    {
        // the grid's own question (the fee, or none) comes to Notices: the person answers it there
        var r = await _ops.DoAsync("TesselAgent", "joinGroup", new JsonObject { ["group_id"] = id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Can't join: {e}" : $"Joining {name.Split(", ")[0]}: its question is in Notices, Control+N.");
    }
}
