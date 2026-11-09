using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;

namespace Sojourn.Access;

/// <summary>
/// The people area's other lists (Alt+3, under People nearby): Friends, online first ("Kestrel, online"), and My
/// groups ("Builders, active"). Enter on a friend opens an IM, on a group its chat; the Applications key has the rest:
/// a profile (read in its own window, line by line), teleports, pay (confirmed), friendship ended (confirmed), a
/// group's details, wearing its tag, leaving it (the grid asks in Notices). Profiles open from any list of people.
/// </summary>
public sealed partial class MainForm
{
    ListView _friends = null!, _groups = null!;
    ContextMenuStrip _friendMenu = null!, _groupMenu = null!;

    sealed record Friend(UUID Id, string Name, bool Online);
    sealed record MyGroup(UUID Id, string Name);
    static readonly object NoGroups = new();
    static readonly object NoTagRow = new();

    /// <summary>The people page: nearby, friends and groups, each labelled (Tab moves between them).</summary>
    void BuildPeoplePage()
    {
        _friends = List("friends", "Friends");
        _groups = List("groups", "My groups");
        _friendMenu = PersonMenu("friendMenu", () => _friends.FocusedItem?.Tag is Friend f ? (f.Id, f.Name) : null, enterIms: true);
        _groupMenu = new ContextMenuStrip { Name = "groupMenu", AccessibleName = "Actions" };
        void GroupItem(string text, Action<MyGroup> act) =>
            _groupMenu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => { if (_groups.FocusedItem?.Tag is MyGroup g) act(g); }));
        GroupItem("Group &chat", g => StartGroupChat(g));
        GroupItem("&About the group", g => _ = GroupInfoAsync(g.Id, g.Name));
        GroupItem("&Wear its tag", g => _ = ActivateGroupAsync(g.Id, g.Name));
        // (its own item, not the group's: also the No group tag row's)
        var noTagItem = new ToolStripMenuItem("&No group tag", null, (_, _) => _ = ActivateGroupAsync(UUID.Zero, "no group"));
        _groupMenu.Items.Add(noTagItem);
        GroupItem("Choose your &title...", g => _ = ChooseTitleAsync(g));
        GroupItem("&Members...", g => _ = GroupPeopleAsync(g, chat: false));
        GroupItem("Who's in its c&hat...", g => _ = GroupPeopleAsync(g, chat: true));
        GroupItem("N&otices...", g => _ = GroupNoticesAsync(g));
        GroupItem("N&ew notice...", g => NewNotice(g));
        GroupItem("&Invite someone...", g => _ = InviteSomeoneAsync(g));
        GroupItem("&Banned residents...", g => _ = GroupBansAsync(g));
        GroupItem("Chat lo&g", g => ChatLogOf(g.Id.ToString(), g.Name));
        GroupItem("&Receive its notices", g => _ = GroupSwitchAsync(g, "notices"));
        var noticesItem = (ToolStripMenuItem)_groupMenu.Items[^1];
        GroupItem("List it in your &profile", g => _ = GroupSwitchAsync(g, "list_in_profile"));
        var listedItem = (ToolStripMenuItem)_groupMenu.Items[^1];
        SoundItem(_groupMenu, () => _groups.FocusedItem?.Tag is MyGroup g ? (g.Id, g.Name) : null, group: true);
        GroupItem("&Leave", g => _ = LeaveGroupAsync(g));
        _groupMenu.Items.Add(new ToolStripSeparator());
        // (the list's own, so it's there on "In no groups." too, alone)
        var newGroup = new ToolStripMenuItem("New gro&up...", null, (_, _) => NewGroup());
        _groupMenu.Items.Add(newGroup);
        _groupMenu.Opening += (_, _) =>
        {
            bool group = _groups.FocusedItem?.Tag is MyGroup, noTag = _groups.FocusedItem?.Tag == NoTagRow;
            foreach (ToolStripItem item in _groupMenu.Items) item.Visible = group || item == newGroup || (noTag && item == noTagItem);
            if (_groups.FocusedItem?.Tag is not MyGroup g || Host?.Session.State.Groups.TryGetValue(g.Id, out var now) != true) return;
            noticesItem.Checked = now.AcceptNotices;
            listedItem.Checked = now.ListInProfile;
        };
        KeysOn(_groupMenu, ("Group &chat", "Enter"));
        MenuOn(_friends, _friendMenu);
        MenuOn(_groups, _groupMenu);
        _friends.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; RefreshFriendsAndGroups(said: true, into: _friends); }
            else if (e.KeyCode == Keys.Enter && _friends.FocusedItem?.Tag is Friend f) { e.Handled = e.SuppressKeyPress = true; StartIm(f.Id, f.Name); }
        };
        _groups.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; RefreshFriendsAndGroups(said: true, into: _groups); }
            else if (e.KeyCode == Keys.Enter && _groups.FocusedItem?.Tag is MyGroup g) { e.Handled = e.SuppressKeyPress = true; StartGroupChat(g); }
            else if (e.KeyCode == Keys.Enter && _groups.FocusedItem?.Tag == NoTagRow) { e.Handled = e.SuppressKeyPress = true; _ = ActivateGroupAsync(UUID.Zero, "no group"); }
        };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        t.Controls.Add(new Label { Text = "&Nearby", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 0);
        t.Controls.Add(_people, 1, 0);
        t.Controls.Add(new Label { Text = "&Friends", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 1);
        t.Controls.Add(_friends, 1, 1);
        t.Controls.Add(new Label { Text = "&Groups", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 2);
        t.Controls.Add(_groups, 1, 2);
        _people.TabIndex = 0; _friends.TabIndex = 1; _groups.TabIndex = 2;
        _peoplePage.Controls.Add(t);
    }

    /// <summary>Alt+2, Radegast's Friends key: the people area with the focus on Friends (its first friend read, online
    /// first); pressed there again, read again.</summary>
    void GoFriends()
    {
        bool there = _areas.SelectedTab == _peoplePage && _friends.Focused;
        _areas.SelectedTab = _peoplePage;   // (arriving from elsewhere refreshes the area's lists)
        if (there) RefreshFriendsAndGroups(said: false, into: _friends);
        _friends.Focus();
        FocusChanged();
    }

    /// <summary>Friends (online first, then by name) and my groups (the one whose tag is worn said so), into the lists the
    /// person isn't in, and into `into` even if they are.</summary>
    void RefreshFriendsAndGroups(bool said, ListView? into = null)
    {
        if (_ops?.Client is not { } c) return;
        var friends = c.Friends.FriendList.Values
            .Select(f => new Friend(f.UUID, Host!.Session.Names.Complete(f.UUID, string.IsNullOrEmpty(f.Name) ? "a friend" : f.Name), f.IsOnline))
            .OrderByDescending(f => f.Online).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (!_friends.Focused || into == _friends)
            Fill(_friends, friends.Select(f => ($"{f.Name}, {(f.Online ? "online" : "offline")}", f.Id.ToString(), (object)f)).ToList(), "No friends yet.");
        var groups = Host!.Session.State.Groups.Values.Select(g => new MyGroup(g.ID, g.Name))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (!_groups.Focused || into == _groups)
        {
            // the official viewer's None row first (the blind tester looked for it, 2026-11-08: the menu's No group tag
            // was on every group's menu, where nobody would look), Enter on it wearing no tag
            var rows = groups.Select(g => ($"{g.Name}{(g.Id == c.Self.ActiveGroup ? ", tag worn" : "")}", g.Id.ToString(), (object)g)).ToList();
            if (rows.Count > 0) rows.Insert(0, (c.Self.ActiveGroup == UUID.Zero ? "No group tag, as now" : "No group tag", "none", NoTagRow));
            Fill(_groups, rows, "In no groups. The Applications key: New group.");
            // (a tag, so its Applications key opens the menu, with New group alone)
            if (groups.Count == 0 && _groups.Items.Count == 1) _groups.Items[0].Tag = NoGroups;
        }
        if (said) Reader?.Answer($"{friends.Count(f => f.Online)} of {friends.Count} friends online; {groups.Count} groups.", keep: false);
    }

    void StartGroupChat(MyGroup g)
    {
        var cv = Open(g.Id.ToString(), g.Name, group: true, UUID.Zero);
        _conversations.SelectedTab = cv.Page;
        GoArea(_conversationsPage);
    }

    async Task ActivateGroupAsync(UUID id, string name)
    {
        var r = await _ops.DoAsync("TesselAgent", "activateGroup", new JsonObject { ["group_id"] = id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't change: {e}" : id == UUID.Zero ? "No group tag now." : $"Wearing {name}'s tag.");
        await Task.Delay(1000);
        RefreshFriendsAndGroups(said: false);
    }

    async Task LeaveGroupAsync(MyGroup g)
    {
        var r = await _ops.DoAsync("TesselAgent", "leaveGroup", new JsonObject { ["group_id"] = g.Id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Can't leave: {e}" : $"Leaving {g.Name}: its question is in Notices, Control+N.");
    }

    async Task EndFriendshipAsync(Friend f)
    {
        if (await ConfirmedAsync("SojournPeople", "removeFriend", new JsonObject { ["to"] = f.Id.ToString() }) is not { } r) return;
        Answer(PersonOps.Error(r) is { } e ? $"Still friends: {e}" : $"No longer friends with {f.Name}.");
        RefreshFriendsAndGroups(said: false);
    }

    /// <summary>Pay someone: the amount asked, then the person's confirmation (Notices), then what the grid said.</summary>
    async Task PayAsync(UUID id, string name, int? given = null)
    {
        var text = given?.ToString() ?? Dialogs.Ask(this, $"Pay {name}", "Amount in L$");
        if (text == null) { Reader?.Answer("Not paid.", keep: false); return; }
        if (!int.TryParse(text.Trim().TrimStart('L', '$').Trim(), out var amount) || amount <= 0) { Reader?.Answer($"{text} isn't an amount.", keep: false); return; }
        if (await ConfirmedAsync("TesselAgent", "pay", new JsonObject { ["to"] = id.ToString(), ["amount"] = amount }) is not { } r) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not paid: {e}"); return; }
        Answer(r["balance"] is { } b ? $"Paid {name} L${amount}. You have L${b}." : $"Paying {name} L${amount}; the grid hasn't confirmed yet.");
    }

    /// <summary>Someone's profile in its own window: a read-only text to read line by line, their picks, her notes on them
    /// (saved as the window closes, as Radegast's on leaving the box), with IM, pay and close. Her own is her own window.</summary>
    /// <param name="toNotes">The focus starts in the notes (a person's Notes item, //notes).</param>
    async Task ProfileAsync(UUID id, string name, bool toNotes = false)
    {
        if (id == _ops.Client?.Self.AgentID) { await MyProfileAsync(); return; }
        var notesRead = _ops.ReadAsync("SojournPeople", "notes", new JsonObject { ["to"] = id.ToString() });
        var r = await _ops.ReadAsync("TesselAgent", "profile", new JsonObject { ["id"] = id.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read {name}'s profile: {e}"); return; }
        var lines = new List<string> { r.Str("name") ?? name };
        if (DisplayNameLine(id) is { } written) lines.Add(written);
        // (a link in someone's words: in Control+Shift+K's list once this window closes)
        NoteLinks($"{r.Str("about")} {r.Str("first_life")}", r.Str("name") ?? name);
        if (r.Str("born") is { Length: > 0 } born) lines.Add($"Born {born}.");
        if (r.Str("partner") is { Length: > 0 } partner) lines.Add($"Partner: {partner}.");
        if (r.Str("about") is { Length: > 0 } about) { lines.Add(""); lines.Add("About:"); lines.Add(about.Trim()); }
        if (r["interests"] is JsonObject i)
            foreach (var (k, label) in new[] { ("want_to", "Wants to"), ("skills", "Skills"), ("languages", "Languages") })
                if (i.Str(k) is { Length: > 0 } v) lines.Add($"{label}: {v.Trim()}");
        var picks = r["picks"] as JsonArray;
        if (picks is { Count: > 0 })
            lines.Add("Picks: " + string.Join(", ", picks.OfType<JsonObject>().Select(p => p.Str("name"))) + ".");
        if (r["groups"] is JsonArray { Count: > 0 } theirGroups)
            lines.Add("Groups: " + string.Join(", ", theirGroups.OfType<JsonObject>().Select(g => g.Str("title") is { Length: > 0 } title ? $"{g.Str("name")}, as {title}" : g.Str("name"))) + ".");
        if (r.Str("first_life") is { Length: > 0 } fl) { lines.Add(""); lines.Add("First life:"); lines.Add(fl.Trim()); }
        if (r.Str("url") is { Length: > 0 } url) lines.Add($"Web: {url}");
        var shownName = r.Str("name") ?? name;
        var form = new Form
        {
            Name = "profile", Text = $"Profile: {shownName}", AccessibleName = $"Profile: {shownName}", Size = new Size(620, 480),
            StartPosition = FormStartPosition.CenterParent, KeyPreview = true, ShowInTaskbar = false,
        };
        var text = new TextBox
        {
            Name = "profileText", AccessibleName = "Profile", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, BackColor = SystemColors.Window, Text = FriendlyLinks(string.Join("\r\n", lines.Select(l => l.Replace("\r\n", "\n").Replace("\n", "\r\n")))),
        };
        var im = new Button { Name = "profileIm", Text = "Send &IM", AutoSize = true };
        var pay = new Button { Name = "profilePay", Text = "P&ay...", AutoSize = true };
        var give = new Button { Name = "profileGive", Text = "&Give what you copied", AutoSize = true };
        var close = new Button { Name = "profileClose", Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var more = new Button { Name = "profileActions", Text = "A&ctions...", AutoSize = true };
        // its pictures described by the assistant (Control+D here too; again for more)
        var describe = new Button { Name = "profileDescribe", Text = "&Describe pictures", AutoSize = true };
        var pictures = new Subject(id.ToString(), "profile", $"{shownName}'s profile pictures", $"Describe {shownName}'s profile pictures");
        describe.Click += (_, _) => _ = DescribeAsync(pictures);
        // the same as every list's: the window closes, then it goes
        var actions = new ContextMenuStrip { Name = "profileMenu", AccessibleName = "Actions" };
        void ShowActions(Control on)
        {
            actions.Items.Clear();
            AddPersonItems(actions, id, shownName, profile: false, first: form.Close);
            actions.Show(on, new Point(10, Math.Min(on.Height, 40)));
        }
        more.Click += (_, _) => ShowActions(more);
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Apps || e.KeyData == (Keys.Shift | Keys.F10))
            {
                e.Handled = e.SuppressKeyPress = true;
                ShowActions(text);
                return;
            }
            if (e.KeyData != (Keys.Control | Keys.D)) return;
            e.Handled = e.SuppressKeyPress = true;
            _ = DescribeAsync(pictures);
        };
        im.Click += (_, _) => { form.Close(); StartIm(id, shownName); };
        pay.Click += (_, _) => { form.Close(); _ = PayAsync(id, shownName); };
        give.Click += (_, _) => { form.Close(); _ = GiveCopiedAsync(id, shownName); };
        close.Click += (_, _) => form.Close();
        form.CancelButton = close;
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        row.Controls.AddRange(new Control[] { im, pay, give, describe, more, close });
        // the notes come from the grid in their own time: read-only until they have, so nothing typed can be overwritten
        var notes = new LineBox
        {
            Name = "profileNotes", AccessibleName = $"Your notes on {shownName}, only you see them", Multiline = true, AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, MaxLength = 0, ReadOnly = true, BackColor = SystemColors.Window,
        };
        string? notesWere = null;
        void NotesIn(JsonObject n)
        {
            if (notes.IsDisposed) return;
            if (PersonOps.Error(n) != null) { notes.Text = "Your notes couldn't be read just now."; return; }
            notesWere = (n.Str("notes") ?? "").TrimEnd().ReplaceLineEndings("\r\n");
            notes.Text = notesWere;
            notes.ReadOnly = false;
            if (notes.Focused) notes.SelectionStart = notes.TextLength;
        }
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int rows = 0, tab = 0;
        void Row(string? label, Control c, RowStyle style)
        {
            table.RowStyles.Add(style);
            if (label != null) table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, TabIndex = tab++ }, 0, rows);
            c.TabIndex = tab++;
            table.Controls.Add(c, label != null ? 1 : 0, rows);
            if (label == null) table.SetColumnSpan(c, 2);
            rows++;
        }
        Row(null, text, new RowStyle(SizeType.Percent, 100));
        if (picks is { Count: > 0 }) Row("Pic&ks", PicksList(form, id, picks, mine: false), new RowStyle(SizeType.Absolute, 90));
        Row("&Notes", notes, new RowStyle(SizeType.Absolute, 80));
        Row(null, row, new RowStyle(SizeType.AutoSize));
        form.Controls.Add(table);
        form.ActiveControl = toNotes ? notes : text;
        form.Shown += async (_, _) =>
        {
            text.SelectionStart = 0;
            text.SelectionLength = 0;
            NotesIn(await notesRead);
        };
        form.ShowDialog(this);
        actions.Dispose();
        var typed = notes.Text.TrimEnd();
        if (notesWere != null && typed != notesWere) await SaveNotesAsync(id, shownName, typed.ReplaceLineEndings("\n"));
        form.Dispose();
    }

    /// <summary>Her notes on someone: typing over them is her own edit, as a notecard's is; clearing what was there goes
    /// through her confirmation, as a delete.</summary>
    async Task SaveNotesAsync(UUID id, string name, string text)
    {
        var args = new JsonObject { ["to"] = id.ToString(), ["text"] = text };
        var r = text.Length == 0 ? await ConfirmedAsync("SojournPeople", "setNotes", args) : await _ops.DoAsync("SojournPeople", "setNotes", args);
        if (r == null) return;
        Answer(PersonOps.Error(r) is { } e ? $"Your notes on {name} weren't saved: {e}." : text.Length == 0 ? $"Your notes on {name} are cleared." : $"Your notes on {name} are saved.");
    }
}
