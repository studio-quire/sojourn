using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Access;

namespace Sojourn.Access;

/// <summary>
/// Search (Alt+5): words, what to look for (people, places, events, groups, classifieds), Enter, and the results as a
/// list with the focus on the first ("4 found."). Enter on a result does the obvious thing (an IM to a person, a
/// teleport to a place, an event or a classified, a group's details); the Applications key lists the rest (a person's
/// profile, teleport offer, friendship; a place's about; joining a group, behind its own question in Notices).
/// </summary>
public sealed partial class MainForm
{
    TabPage _searchPage = null!;
    TextBox _searchWords = null!;
    ComboBox _searchKind = null!;
    ListView _results = null!;
    ContextMenuStrip _resultMenu = null!;

    sealed record Found(string Kind, string Name, UUID Id, string? GoTo, string About);

    static readonly string[] SearchKinds = { "People", "Places", "Events", "Groups", "Classifieds" };

    void BuildSearch()
    {
        _searchPage = new TabPage("Search") { Name = "searchPage", AccessibleName = "Search" };
        _searchWords = new LineBox { Name = "searchWords", AccessibleName = "Search for", Dock = DockStyle.Fill };
        _searchKind = new ComboBox { Name = "searchKind", AccessibleName = "Look for", DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        _searchKind.Items.AddRange(SearchKinds);
        _searchKind.SelectedIndex = 0;
        _results = List("results", "Results");
        _resultMenu = new ContextMenuStrip { Name = "resultMenu", AccessibleName = "Actions" };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(new Label { Text = "Search &for", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        t.Controls.Add(_searchWords, 1, 0);
        t.Controls.Add(new Label { Text = "&Look for", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        t.Controls.Add(_searchKind, 3, 0);
        t.Controls.Add(_results, 0, 1);
        t.SetColumnSpan(_results, 4);
        _searchWords.TabIndex = 0; _searchKind.TabIndex = 1; _results.TabIndex = 2;
        _searchPage.Controls.Add(t);

        _searchWords.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            _ = SearchAsync();
        };
        _searchKind.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            _ = SearchAsync();
        };
        _results.KeyDown += (_, e) =>
        {
            if (_results.FocusedItem?.Tag is not Found f) return;
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; ResultActions(f).FirstOrDefault()?.Do(); }
            else if (e.KeyCode == Keys.Apps || e.KeyData == (Keys.Shift | Keys.F10)) { e.Handled = e.SuppressKeyPress = true; ShowResultMenu(f); }
        };
        _results.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && _results.FocusedItem?.Tag is Found f) ShowResultMenu(f); };
        Fill(_results, new(), "Type words, choose what to look for, then Enter.");
    }

    bool _searching;

    async Task SearchAsync()
    {
        var words = _searchWords.Text.Trim();
        var kind = SearchKinds[Math.Max(0, _searchKind.SelectedIndex)];
        if (words.Length < (kind is "People" or "Groups" ? 2 : kind == "Events" ? 0 : 3))
        {
            Reader?.Answer(kind == "Events" ? "Searching events needs no words; Enter lists what's on now." : $"Type at least {(kind is "People" or "Groups" ? 2 : 3)} letters.", keep: false);
            if (kind != "Events") return;
        }
        if (_searching || Host?.Session.Online != true) return;
        _searching = true;
        Reader?.Answer($"Searching {kind.ToLowerInvariant()}.", keep: false);
        try
        {
            var found = new List<Found>();
            string? error = null;
            switch (kind)
            {
                case "People":
                {
                    var r = await _ops.ReadAsync("TesselAgent", "findAvatar", new JsonObject { ["name"] = words, ["limit"] = 30 });
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
                    var r = await _ops.ReadAsync("SojournSearch", "places", new JsonObject { ["text"] = words, ["limit"] = 30 });
                    error = PersonOps.Error(r);
                    foreach (var p in (r["places"] as JsonArray ?? new()).OfType<JsonObject>())
                    {
                        var traffic = Json.AsDouble(p["traffic"]) is { } tr && tr > 0 ? $", traffic {tr:0}" : "";
                        found.Add(new("place", $"{p.Str("name")}, in {p.Str("region")}{traffic}", UUID.Zero, p.Str("go_to"), p.Str("about") ?? ""));
                    }
                    break;
                }
                case "Events":
                {
                    var args = new JsonObject { ["limit"] = 30 };
                    if (words.Length > 0) args["text"] = words;
                    var r = await _ops.ReadAsync("SojournSearch", "events", args);
                    error = PersonOps.Error(r);
                    foreach (var p in (r["events"] as JsonArray ?? new()).OfType<JsonObject>())
                    {
                        var when = p["on_now"]?.GetValue<bool>() == true ? "on now" : $"starts {p.Str("starts")}";
                        found.Add(new("event", $"{p.Str("name")}, {when}, in {p.Str("region")}", UUID.Zero, p.Str("go_to"), p.Str("about") ?? ""));
                    }
                    break;
                }
                case "Groups":
                {
                    var r = await _ops.ReadAsync("TesselGroups", "search", new JsonObject { ["text"] = words });
                    error = PersonOps.Error(r);
                    foreach (var g in (r["groups"] as JsonArray ?? new()).OfType<JsonObject>())
                        if (UUID.TryParse(g.Str("group_id"), out var id))
                        {
                            int members = (int)(Json.AsDouble(g["members"]) ?? 0);
                            found.Add(new("group", $"{g.Str("name")}, {(members == 1 ? "1 member" : $"{members} members")}", id, null, ""));
                        }
                    break;
                }
                default:
                {
                    var r = await _ops.ReadAsync("SojournSearch", "classifieds", new JsonObject { ["text"] = words, ["limit"] = 30 });
                    error = PersonOps.Error(r);
                    foreach (var p in (r["classifieds"] as JsonArray ?? new()).OfType<JsonObject>())
                        found.Add(new("classified", $"{p.Str("name")}, in {p.Str("region")}", UUID.Zero, p.Str("go_to"), p.Str("about") ?? ""));
                    break;
                }
            }
            Fill(_results, found.Select((f, i) => (f.Name, $"r{i}", (object)f)).ToList(), error != null ? $"The search didn't work: {error}" : $"Nothing found for {words}.");
            _results.Focus();
            Reader?.Answer(error != null ? $"The search didn't work: {error}" : found.Count == 0 ? $"Nothing found for {words}." : $"{found.Count} found.", keep: false);
        }
        finally { _searching = false; }
    }

    void ShowResultMenu(Found f)
    {
        _resultMenu.Items.Clear();
        foreach (var a in ResultActions(f))
        {
            var act = a;
            _resultMenu.Items.Add(new ToolStripMenuItem(a.Text, null, (_, _) => act.Do()));
        }
        var r = _results.FocusedItem?.Bounds ?? Rectangle.Empty;
        _resultMenu.Show(_results, new Point(r.Left + 10, r.Bottom));
    }

    List<InvAction> ResultActions(Found f)
    {
        var list = new List<InvAction>();
        switch (f.Kind)
        {
            case "person":
                list.Add(new("Send &IM", () => StartIm(f.Id, f.Name.Split(" (")[0])));
                list.Add(new("&Profile", () => _ = ProfileAsync(f.Id, f.Name)));
                list.Add(new("Offer &teleport", () => _ = PersonIdOpAsync("offerTeleport", f.Id, $"Teleport offered to {f.Name}.")));
                list.Add(new("&Request teleport", () => _ = PersonIdOpAsync("requestTeleport", f.Id, $"Asked {f.Name} for a teleport.")));
                list.Add(new("Offer &friendship", () => _ = PersonIdOpAsync("offerFriendship", f.Id, $"Friendship offered to {f.Name}.")));
                list.Add(new("P&ay...", () => _ = PayAsync(f.Id, f.Name)));
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
        var charter = r.Str("charter") is { Length: > 0 } ch ? $" Charter: {ch.Trim()}" : "";
        Answer($"{r.Str("name") ?? name}: {(members == 1 ? "1 member" : $"{members} members")}, {open}.{mine}{charter}",
            charter.Length > 0 ? LineFrom.People : LineFrom.App);
    }

    async Task JoinGroupAsync(UUID id, string name)
    {
        // the grid's own question (the fee, or none) comes to Notices: the person answers it there
        var r = await _ops.DoAsync("TesselAgent", "joinGroup", new JsonObject { ["group_id"] = id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Can't join: {e}" : $"Joining {name.Split(", ")[0]}: its question is in Notices, Control+N.");
    }
}
