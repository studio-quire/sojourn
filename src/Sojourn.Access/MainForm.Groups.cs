using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Access;

namespace Sojourn.Access;

/// <summary>
/// What's on a group's menu past its chat and tag (docs/access-app.md > Groups): which title she wears there, half of the
/// "group tag" task blind residents hand to a sighted person; its notices, read from the archive, an attachment taken;
/// whether she gets them; inviting someone, in a role; a new group. Its members and who's in its chat are
/// MainForm.GroupPeople.cs, a new notice MainForm.GroupNotice.cs. Claude Opus 5.5, 2026-11-07, 2026-11-08.
/// </summary>
public sealed partial class MainForm
{
    static string TitleWords(JsonObject t) => t.Str("title") is { Length: > 0 } w ? w : "no title";

    /// <summary>The titles she can wear there, the one worn saying so; Enter wears one.</summary>
    async Task ChooseTitleAsync(MyGroup g)
    {
        var r = await _ops.ReadAsync("TesselGroups", "titles", new JsonObject { ["group_id"] = g.Id.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read your titles in {g.Name}: {e}"); return; }
        var titles = (r["titles"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        if (titles.Count == 0) { Answer($"{g.Name} gave no titles for you."); return; }
        if (titles.Count == 1) { Answer($"You have one title in {g.Name}: {TitleWords(titles[0])}."); return; }
        int worn = titles.FindIndex(t => t["selected"]?.GetValue<bool>() == true);
        var words = titles.Select((t, i) => i == worn ? $"{TitleWords(t)}, worn" : TitleWords(t)).ToList();
        if (Dialogs.Choose(this, $"Your title in {g.Name}", "Titles", words, Math.Max(worn, 0)) is not { } pick) { Reader?.Answer("Title left as it was.", keep: false); return; }
        if (pick == worn) { Answer($"You're wearing {TitleWords(titles[pick])} already."); return; }
        var set = await _ops.DoAsync("TesselGroups", "title", new JsonObject { ["group_id"] = g.Id.ToString(), ["role_id"] = titles[pick].Str("role_id") });
        if (PersonOps.Error(set) is { } se) { Answer($"Title not changed: {se}"); return; }
        var showing = _ops.Client?.Self.ActiveGroup == g.Id ? "" : " It shows once you wear this group's tag.";
        Answer($"Your title in {g.Name} is {TitleWords(titles[pick])} now.{showing}");
    }

    /// <summary>The title worn in a group, or null when that can't be told.</summary>
    async Task<string?> WornTitleAsync(UUID group)
    {
        var r = await _ops.ReadAsync("TesselGroups", "titles", new JsonObject { ["group_id"] = group.ToString() });
        return (r["titles"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(t => t["selected"]?.GetValue<bool>() == true) is { } worn ? TitleWords(worn) : null;
    }

    /// <summary>The notices its archive holds, newest first; Enter reads one in a window of its own, with Take the attachment
    /// when it has one, Escape there coming back to the list.</summary>
    async Task GroupNoticesAsync(MyGroup g)
    {
        var r = await _ops.ReadAsync("TesselGroups", "notices", new JsonObject { ["group_id"] = g.Id.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read {g.Name}'s notices: {e}"); return; }
        var notices = (r["notices"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        if (notices.Count == 0) { Answer($"{g.Name} has no notices in its archive."); return; }
        static string Date(JsonObject n) => Json.AsDouble(n["time"]) is { } t and > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)t).LocalDateTime.ToString("d MMMM", CultureInfo.InvariantCulture) : "";
        var words = notices.Select(n => string.Join(", ", new[] { n.Str("subject") ?? "no subject", n.Str("from") is { Length: > 0 } who ? $"from {who}" : "", Date(n),
            n.Str("attachment") is { Length: > 0 } a ? $"with a {a} attached" : "" }.Where(w => w.Length > 0))).ToList();
        int at = 0;
        while (Dialogs.Choose(this, $"{g.Name}'s notices", "Notices", words, at) is { } pick)
        {
            at = pick;
            var id = notices[pick].Str("notice_id");
            var read = await _ops.ReadAsync("TesselGroups", "noticeRead", new JsonObject { ["notice_id"] = id });
            if (PersonOps.Error(read) is { } re) { Answer($"Couldn't read that notice: {re}"); return; }
            var text = $"{words[pick]}\n\n{read.Str("message")}";
            if (read["attachment"] is JsonObject att)
            {
                var name = att.Str("name") is { Length: > 0 } n ? n : "something";
                ShowText(read.Str("subject") is { Length: > 0 } s1 ? s1 : "A notice", $"{text}\n\nAttached: {name}, {att.Str("type")}. Take the attachment puts it in your inventory.",
                    ("&Take the attachment", () => _ = TakeNoticeAttachmentAsync(id, name)));
            }
            else ShowText(read.Str("subject") is { Length: > 0 } subject ? subject : "A notice", text);
        }
    }

    async Task TakeNoticeAttachmentAsync(string? noticeId, string name)
    {
        var r = await _ops.DoAsync("TesselGroups", "noticeTake", new JsonObject { ["notice_id"] = noticeId });
        Answer(PersonOps.Error(r) is { } e ? $"Couldn't take {name}: {e}"
            : r.Str("folder") is { Length: > 0 } folder ? $"Taking {name} into your {folder} folder." : $"Taking {name} into your inventory.");
    }

    /// <summary>Whether she gets its notices, whether her profile lists it: said back as they now are.</summary>
    async Task GroupSwitchAsync(MyGroup g, string which)
    {
        if (Host?.Session.State.Groups.TryGetValue(g.Id, out var now) != true) return;
        bool on = !(which == "notices" ? now.AcceptNotices : now.ListInProfile);
        var r = await _ops.DoAsync("TesselGroups", "membership", new JsonObject { ["group_id"] = g.Id.ToString(), [which] = on });
        Answer(PersonOps.Error(r) is { } e ? $"Not changed: {e}"
            : which == "notices" ? (on ? $"You'll get {g.Name}'s notices." : $"You won't get {g.Name}'s notices now; they stay in its archive.")
            : on ? $"Your profile lists {g.Name}." : $"Your profile doesn't list {g.Name} now.");
    }

    /// <summary>Someone invited: the groups she may invite to, as the viewer's picker lists them; they're asked in their own
    /// viewer.</summary>
    async Task InviteAsync(UUID id, string name)
    {
        var groups = Host?.Session.State.Groups.Values.Where(g => (g.Powers & GroupPowers.Invite) != 0).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (groups is not { Count: > 0 }) { Answer("None of your groups lets you invite people."); return; }
        await Task.Yield();
        if (Dialogs.Choose(this, $"Invite {name} to", "Groups", groups.Select(g => g.Name).ToList(), 0) is not { } pick) { Reader?.Answer("Nobody invited.", keep: false); return; }
        await InviteToAsync(new MyGroup(groups[pick].ID, groups[pick].Name), id, name);
    }

    /// <summary>The group's Invite someone: a person picked (nearby, friends, or anyone by name), then the role.</summary>
    async Task InviteSomeoneAsync(MyGroup g)
    {
        if (Lacks(g, GroupPowers.Invite, "invite people") is { } no) { Answer(no); return; }
        var who = Dialogs.Pick(this, $"Invite to {g.Name}", await PeopleToPickAsync(), FindSomeoneAsync);
        if (who == null) { Reader?.Answer("Nobody invited.", keep: false); return; }
        await InviteToAsync(g, who.Id, who.Name);
    }

    /// <summary>Invited in a role, as the viewer's invite asks: Everyone first, then the roles hers let her give (any with the
    /// power to assign them, else only those she has); none but Everyone asks nothing. A role past Everyone can carry
    /// powers, so it waits for her confirmation.</summary>
    async Task InviteToAsync(MyGroup g, UUID id, string name)
    {
        var args = new JsonObject { ["group_id"] = g.Id.ToString(), ["to"] = id.ToString() };
        var powers = Host?.Session.State.Groups.TryGetValue(g.Id, out var grp) == true ? grp.Powers : GroupPowers.None;
        string? roleName = null;
        if ((powers & (GroupPowers.AssignMember | GroupPowers.AssignMemberLimited)) != 0)
        {
            var r = await _ops.ReadAsync("TesselGroups", "roles", new JsonObject { ["group_id"] = g.Id.ToString() });
            var me = _ops.Client?.Self.AgentID.ToString();
            var roles = (r["roles"] as JsonArray ?? new()).OfType<JsonObject>()
                .Where(x => x["everyone"]?.GetValue<bool>() != true)
                .Where(x => (powers & GroupPowers.AssignMember) != 0 || (x["members"] as JsonArray ?? new()).OfType<JsonObject>().Any(m => m.Str("id") == me))
                .ToList();
            if (roles.Count > 0)
            {
                var words = new List<string> { "Everyone, the role every member has" };
                words.AddRange(roles.Select(x => x.Str("name") ?? "a role"));
                if (Dialogs.Choose(this, $"Invite {name} to {g.Name} as", "Roles", words, 0) is not { } pick) { Reader?.Answer("Nobody invited.", keep: false); return; }
                if (pick > 0) { args["role_id"] = roles[pick - 1].Str("role_id"); roleName = roles[pick - 1].Str("name"); }
            }
        }
        var done = roleName == null ? await _ops.DoAsync("TesselGroups", "invite", args) : await ConfirmedAsync("TesselGroups", "invite", args);
        if (done == null) return;
        Answer(PersonOps.Error(done) is { } e ? $"Not invited: {e}" : roleName == null ? $"Invited {name} to {g.Name}." : $"Invited {name} to {g.Name}, in the role {roleName}.");
    }

    /// <summary>New group (the viewer's Create group): its name, charter, whether anyone may join and whether search lists it;
    /// on Second Life it costs L$100, so it waits for her confirmation as a payment does. Not modal, so the confirmation in
    /// Notices comes back here.</summary>
    void NewGroup()
    {
        if (_newGroup is { IsDisposed: false } open) { open.Activate(); return; }
        var form = new Form
        {
            Name = "newGroup", Text = "New group", AccessibleName = "New group", Size = new Size(560, 380),
            AccessibleDescription = "On Second Life a new group costs L$100, confirmed in Notices.",
            StartPosition = FormStartPosition.CenterParent, KeyPreview = true, ShowInTaskbar = false,
        };
        var name = new LineBox { Name = "groupName", AccessibleName = "Group name, 4 to 35 letters", Dock = DockStyle.Fill, MaxLength = 0 };
        var charter = new LineBox
        {
            Name = "groupCharter", AccessibleName = "Charter", Dock = DockStyle.Fill, MaxLength = 0, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical,
        };
        var open2 = new CheckBox { Name = "groupOpen", Text = "&Anyone can join", AutoSize = true };
        var listed = new CheckBox { Name = "groupListed", Text = "Show in &search", AutoSize = true, Checked = true };
        var create = new Button { Name = "groupCreate", Text = "C&reate", AutoSize = true };
        var cancel = new Button { Name = "groupCancel", Text = "Cancel", AutoSize = true };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int row = 0, tab = 0;
        void Row(string label, Control c, RowStyle style)
        {
            t.RowStyles.Add(style);
            if (label.Length > 0) t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, TabIndex = tab++ }, 0, row);
            c.TabIndex = tab++;
            t.Controls.Add(c, 1, row++);
        }
        Row("&Name", name, new RowStyle(SizeType.AutoSize));
        Row("&Charter", charter, new RowStyle(SizeType.Percent, 100));
        Row("", open2, new RowStyle(SizeType.AutoSize));
        Row("", listed, new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { create, cancel });
        Row("", buttons, new RowStyle(SizeType.AutoSize));
        form.Controls.Add(t);

        bool creating = false;
        async Task CreateAsync()
        {
            if (creating) return;
            var words = name.Text.Trim();
            if (words.Length is < 4 or > 35) { name.Focus(); Reader?.Answer($"A group's name is 4 to 35 letters{(words.Length > 0 ? $"; this one is {words.Length}" : "")}.", keep: false); return; }
            var args = new JsonObject
            {
                ["name"] = words, ["charter"] = charter.Text.TrimEnd().ReplaceLineEndings("\n"), ["open_enrollment"] = open2.Checked, ["show_in_list"] = listed.Checked,
            };
            creating = true;
            try
            {
                var r = await ConfirmedFromAsync(form, "TesselGroups", "create", args);
                if (r == null || form.IsDisposed) return;
                if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Not created: {e}", keep: false); return; }
                form.Close();
                Answer($"Created the group {words}; you're its owner, and it's in My groups.");
                await Task.Delay(1500);
                RefreshFriendsAndGroups(said: false);
            }
            finally { creating = false; }
        }
        name.KeyDown += (_, e) => { if (e.KeyData == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; _ = CreateAsync(); } };
        create.Click += (_, _) => _ = CreateAsync();
        cancel.Click += (_, _) => form.Close();
        form.CancelButton = cancel;
        form.KeyDown += (_, e) =>
        {
            if (e.KeyData != Keys.F1) return;
            e.Handled = e.SuppressKeyPress = true;
            Reader?.Answer("The group's name, 4 to 35 letters; its charter, where Enter starts a new line; whether anyone can join without an invitation; whether search lists it. "
                + "Create, Alt+R, then confirm in Notices: on Second Life a new group costs L$100. Escape cancels.", keep: false);
        };
        form.FormClosed += (_, _) =>
        {
            _newGroup = null;
            if (!IsDisposed) Activate();
        };
        form.ActiveControl = name;
        form.Shown += (_, _) => Reader?.Answer("New group: its name first. On Second Life a new group costs L$100, confirmed in Notices. F1 for keys.", keep: false);
        _newGroup = form;
        form.Show(this);
    }

    Form? _newGroup;
}
