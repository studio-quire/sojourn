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

    /// <summary>The people page: nearby, friends and groups, each labelled (Tab moves between them).</summary>
    void BuildPeoplePage()
    {
        _friends = List("friends", "Friends");
        _groups = List("groups", "My groups");
        _friendMenu = new ContextMenuStrip { Name = "friendMenu", AccessibleName = "Actions" };
        _groupMenu = new ContextMenuStrip { Name = "groupMenu", AccessibleName = "Actions" };
        void FriendItem(string text, Action<Friend> act) =>
            _friendMenu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => { if (_friends.FocusedItem?.Tag is Friend f) act(f); }));
        FriendItem("Send &IM", f => StartIm(f.Id, f.Name));
        FriendItem("&Profile", f => _ = ProfileAsync(f.Id, f.Name));
        FriendItem("&Describe", f => _ = DescribeAsync(Of(f)));
        FriendItem("Offer &teleport", f => _ = PersonIdOpAsync("offerTeleport", f.Id, $"Teleport offered to {f.Name}."));
        FriendItem("&Request teleport", f => _ = PersonIdOpAsync("requestTeleport", f.Id, $"Asked {f.Name} for a teleport."));
        FriendItem("P&ay...", f => _ = PayAsync(f.Id, f.Name));
        FriendItem("&End friendship", f => _ = EndFriendshipAsync(f));
        void GroupItem(string text, Action<MyGroup> act) =>
            _groupMenu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => { if (_groups.FocusedItem?.Tag is MyGroup g) act(g); }));
        GroupItem("Group &chat", g => StartGroupChat(g));
        GroupItem("&About the group", g => _ = GroupInfoAsync(g.Id, g.Name));
        GroupItem("&Wear its tag", g => _ = ActivateGroupAsync(g.Id, g.Name));
        GroupItem("&No group tag", g => _ = ActivateGroupAsync(UUID.Zero, "no group"));
        GroupItem("&Leave", g => _ = LeaveGroupAsync(g));
        MenuOn(_friends, _friendMenu);
        MenuOn(_groups, _groupMenu);
        _friends.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; RefreshFriendsAndGroups(said: true); }
            else if (e.KeyCode == Keys.Enter && _friends.FocusedItem?.Tag is Friend f) { e.Handled = e.SuppressKeyPress = true; StartIm(f.Id, f.Name); }
        };
        _groups.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; RefreshFriendsAndGroups(said: true); }
            else if (e.KeyCode == Keys.Enter && _groups.FocusedItem?.Tag is MyGroup g) { e.Handled = e.SuppressKeyPress = true; StartGroupChat(g); }
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

    /// <summary>Friends (online first, then by name) and my groups (the one whose tag is worn said so).</summary>
    void RefreshFriendsAndGroups(bool said)
    {
        if (_ops?.Client is not { } c) return;
        var friends = c.Friends.FriendList.Values
            .Select(f => new Friend(f.UUID, Host!.Session.Names.Complete(f.UUID, string.IsNullOrEmpty(f.Name) ? "a friend" : f.Name), f.IsOnline))
            .OrderByDescending(f => f.Online).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (!_friends.Focused)
            Fill(_friends, friends.Select(f => ($"{f.Name}, {(f.Online ? "online" : "offline")}", f.Id.ToString(), (object)f)).ToList(), "No friends yet.");
        var groups = Host!.Session.State.Groups.Values.Select(g => new MyGroup(g.ID, g.Name))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (!_groups.Focused)
            Fill(_groups, groups.Select(g => ($"{g.Name}{(g.Id == c.Self.ActiveGroup ? ", tag worn" : "")}", g.Id.ToString(), (object)g)).ToList(), "In no groups.");
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
    async Task PayAsync(UUID id, string name)
    {
        var text = Dialogs.Ask(this, $"Pay {name}", "Amount in L$");
        if (text == null) { Reader?.Answer("Not paid.", keep: false); return; }
        if (!int.TryParse(text.Trim().TrimStart('L', '$').Trim(), out var amount) || amount <= 0) { Reader?.Answer($"{text} isn't an amount.", keep: false); return; }
        if (await ConfirmedAsync("TesselAgent", "pay", new JsonObject { ["to"] = id.ToString(), ["amount"] = amount }) is not { } r) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not paid: {e}"); return; }
        Answer(r["balance"] is { } b ? $"Paid {name} L${amount}. You have L${b}." : $"Paying {name} L${amount}; the grid hasn't confirmed yet.");
    }

    /// <summary>Someone's profile in its own window: a read-only text to read line by line, with IM, pay and close.</summary>
    async Task ProfileAsync(UUID id, string name)
    {
        var r = await _ops.ReadAsync("TesselAgent", "profile", new JsonObject { ["id"] = id.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read {name}'s profile: {e}"); return; }
        var lines = new List<string> { r.Str("name") ?? name };
        if (r.Str("born") is { Length: > 0 } born) lines.Add($"Born {born}.");
        if (r.Str("partner") is { Length: > 0 } partner) lines.Add($"Partner: {partner}.");
        if (r.Str("about") is { Length: > 0 } about) { lines.Add(""); lines.Add("About:"); lines.Add(about.Trim()); }
        if (r["interests"] is JsonObject i)
            foreach (var (k, label) in new[] { ("want_to", "Wants to"), ("skills", "Skills"), ("languages", "Languages") })
                if (i.Str(k) is { Length: > 0 } v) lines.Add($"{label}: {v.Trim()}");
        if (r["picks"] is JsonArray picks && picks.Count > 0)
            lines.Add("Picks: " + string.Join(", ", picks.OfType<JsonObject>().Select(p => p.Str("name"))) + ".");
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
            Dock = DockStyle.Fill, BackColor = SystemColors.Window, Text = string.Join("\r\n", lines.Select(l => l.Replace("\r\n", "\n").Replace("\n", "\r\n"))),
        };
        var im = new Button { Name = "profileIm", Text = "Send &IM", AutoSize = true };
        var pay = new Button { Name = "profilePay", Text = "P&ay...", AutoSize = true };
        var close = new Button { Name = "profileClose", Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        // its pictures described by the assistant (Control+D here too; again for more)
        var describe = new Button { Name = "profileDescribe", Text = "&Describe pictures", AutoSize = true };
        var pictures = new Subject(id.ToString(), "profile", $"{shownName}'s profile pictures", $"Describe {shownName}'s profile pictures");
        describe.Click += (_, _) => _ = DescribeAsync(pictures);
        form.KeyDown += (_, e) =>
        {
            if (e.KeyData != (Keys.Control | Keys.D)) return;
            e.Handled = e.SuppressKeyPress = true;
            _ = DescribeAsync(pictures);
        };
        im.Click += (_, _) => { form.Close(); StartIm(id, shownName); };
        pay.Click += (_, _) => { form.Close(); _ = PayAsync(id, shownName); };
        close.Click += (_, _) => form.Close();
        form.CancelButton = close;
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        row.Controls.AddRange(new Control[] { im, pay, describe, close });
        form.Controls.Add(text);
        form.Controls.Add(row);
        form.ActiveControl = text;
        form.Shown += (_, _) => { text.SelectionStart = 0; text.SelectionLength = 0; };
        form.ShowDialog(this);
    }
}
