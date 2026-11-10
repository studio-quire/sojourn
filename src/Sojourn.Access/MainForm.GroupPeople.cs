using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// Running a group (docs/access-app.md > Groups): its members, and who's in its chat now, each a list in a window of its
/// own, narrowed by a name typed, where Enter or the Applications key on someone has what the group lets her do to them
/// (their roles, eject, ban; in the chat, stop or allow their text) above the person's own actions. The group's banned
/// residents, on Second Life. Every change waits for her confirmation in Notices, as the official viewer asks before an
/// eject or a ban, and the focus comes back to the window after. Claude Opus 5.5, 2026-11-08.
/// </summary>
public sealed partial class MainForm
{
    sealed record GroupPerson(UUID Id, string Name, string Words, bool Owner, bool TextStopped, string Title = "", string Online = "");

    readonly Dictionary<(UUID Group, bool Chat), Form> _groupWindows = new();

    const string BansNotHere = "Group bans aren't on this grid: Second Life keeps them in a service this grid doesn't have.";

    /// <summary>Why her roles don't let her do it, or null when they do (or it can't be told).</summary>
    string? Lacks(MyGroup g, GroupPowers any, string doing) =>
        Host?.Session.State.Groups.TryGetValue(g.Id, out var grp) == true && (grp.Powers & any) == 0 ? $"Your roles in {g.Name} don't let you {doing}." : null;

    static string MenuWords(string s) => s.Replace("&", "&&");

    static string PeopleCount(int n) => n == 1 ? "1 person" : $"{n} people";

    /// <summary>Her confirmation, from a window of hers when there is one: Notices takes it, and the window has the focus back after.</summary>
    Task<JsonObject?> ConfirmedFromAsync(Form? from, string pump, string op, JsonObject args) =>
        from is { IsDisposed: false } ? ConfirmedCallAsync(_ops.GatedAsync(pump, op, args), args, from) : ConfirmedAsync(pump, op, args);

    /// <summary>The members (chat: who's in its chat now) in a window of their own; the same group's open already comes forward.</summary>
    async Task GroupPeopleAsync(MyGroup g, bool chat)
    {
        if (_groupWindows.TryGetValue((g.Id, chat), out var open) && !open.IsDisposed) { open.Activate(); return; }
        Reader?.Answer(chat ? $"Asking who's in {g.Name}'s chat." : $"Asking for {g.Name}'s members.", keep: false);
        var (people, why) = await LoadGroupPeopleAsync(g, chat);
        if (people == null) { Answer(why ?? "Couldn't read them."); return; }
        if (_groupWindows.TryGetValue((g.Id, chat), out open) && !open.IsDisposed) { open.Activate(); return; }

        var title = chat ? $"In {g.Name}'s chat" : $"Members of {g.Name}";
        var listName = chat ? "In the chat" : "Members";
        var form = new Form
        {
            Name = chat ? "groupChatPeople" : "groupMembers", Text = title, AccessibleName = title, Size = new Size(600, 520),
            StartPosition = FormStartPosition.CenterParent, KeyPreview = true, ShowInTaskbar = false,
        };
        var find = new LineBox { Name = "groupFind", AccessibleName = "Find a name", Dock = DockStyle.Fill };
        var list = List("groupPeople", listName);
        var close = new Button { Name = "groupPeopleClose", Text = "Close", AutoSize = true };
        // (Radegast's Save member list; the members' window only)
        var save = chat ? null : new Button { Name = "groupSaveList", Text = "&Save the list...", AutoSize = true };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = "&Find", AutoSize = true, Anchor = AnchorStyles.Left, TabIndex = 0 }, 0, 0);
        t.Controls.Add(find, 1, 0);
        t.Controls.Add(new Label { Text = "&" + listName, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, TabIndex = 2 }, 0, 1);
        t.Controls.Add(list, 1, 1);
        if (save != null)
        {
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.AddRange(new Control[] { save, close });
            t.Controls.Add(buttons, 1, 2);
            buttons.TabIndex = 4;
        }
        else t.Controls.Add(close, 1, 2);
        find.TabIndex = 1; list.TabIndex = 3; close.TabIndex = 4;
        form.Controls.Add(t);

        void Refill()
        {
            var w = find.Text.Trim();
            var shown = people.Where(p => w.Length == 0 || p.Words.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
            Fill(list, shown.Select(p => (p.Words, p.Id.ToString(), (object)p)).ToList(), w.Length == 0 ? (chat ? "Nobody in the chat." : "No members.") : "Nobody matches.");
            list.AccessibleName = w.Length == 0 ? $"{listName}, {(chat ? PeopleCount(people.Count) : Count(people.Count, "member"))}" : $"{listName}, {shown.Count} of {people.Count}";
        }
        // gone: someone the grid has just said went, whom its list may still name for a moment
        async Task ReloadAsync(bool said, UUID? gone = null)
        {
            var (again, no) = await LoadGroupPeopleAsync(g, chat);
            if (form.IsDisposed) return;
            if (again == null) { Reader?.Answer(no ?? "Couldn't read them again.", keep: false); return; }
            people = gone is { } id ? again.Where(p => p.Id != id).ToList() : again;
            Refill();
            if (said) Reader?.Answer($"{list.AccessibleName}.", keep: false);
        }
        Refill();
        find.TextChanged += (_, _) => Refill();
        find.KeyDown += (_, e) => { if (e.KeyData == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; list.Focus(); } };

        var menu = new ContextMenuStrip { Name = "groupPersonMenu", AccessibleName = "Actions" };
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            if (list.FocusedItem?.Tag is GroupPerson p) AddGroupPersonItems(menu, form, g, p, chat, gone => ReloadAsync(false, gone));
            e.Cancel = menu.Items.Count == 0;
        };
        MenuOn(list, menu);
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; _ = ReloadAsync(true); }
            else if (e.KeyData == Keys.Enter && list.FocusedItem?.Tag is GroupPerson) { e.Handled = e.SuppressKeyPress = true; ShowListMenu(list, menu); }
        };
        list.Enter += (_, _) => { if (list.FocusedItem == null && list.Items.Count > 0) list.Items[0].Focused = true; };
        close.Click += (_, _) => form.Close();
        if (save != null) save.Click += (_, _) => SaveMembers(form, g, people);
        form.CancelButton = close;
        form.KeyDown += (_, e) =>
        {
            if (e.KeyData != Keys.F1) return;
            e.Handled = e.SuppressKeyPress = true;
            Reader?.Answer((chat ? "Who's in the group's chat now." : "The group's members.")
                + " Enter or the Applications key on someone: what you can do, their roles, eject and ban above their own actions. Type in Find to narrow the list, "
                + "Enter there goes to it. F5 reads it again; Escape closes.", keep: false);
        };
        form.FormClosed += (_, _) =>
        {
            _groupWindows.Remove((g.Id, chat));
            menu.Dispose();
            if (!IsDisposed) Activate();
        };
        form.ActiveControl = list;
        form.Shown += (_, _) => Reader?.Answer(chat
            ? $"{g.Name}'s chat: {PeopleCount(people.Count)} in it now. Enter on someone for what you can do; F1 for keys."
            : $"{g.Name}: {Count(people.Count, "member")}. Enter on someone for what you can do; Alt+F finds one by name; F1 for keys.", keep: false);
        _groupWindows[(g.Id, chat)] = form;
        form.Show(this);
    }

    /// <summary>Save the list (Radegast's Save member list): the members as a CSV file, by Windows' own Save window (the run's
    /// SOJOURN_TEST_FILES folder in its place in tests), a row each with their title, whether they own it, and when they
    /// were last on, as the list has them; said where it went.</summary>
    void SaveMembers(Form form, MyGroup g, List<GroupPerson> people)
    {
        var bad = Path.GetInvalidFileNameChars();
        var file = new string($"{g.Name} members {DateTime.Now:yyyy-MM-dd}.csv".Select(c => bad.Contains(c) ? '_' : c).ToArray());
        string path;
        if (Environment.GetEnvironmentVariable("SOJOURN_TEST_FILES") is { Length: > 0 } test) path = Path.Combine(test, file);
        else
        {
            using var d = new SaveFileDialog { Title = $"Save the members of {g.Name}", FileName = file, Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*", DefaultExt = "csv", AddExtension = true, OverwritePrompt = true };
            if (d.ShowDialog(form) != DialogResult.OK) { Reader?.Answer("The list wasn't saved.", keep: false); return; }
            path = d.FileName;
        }
        static string Cell(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var lines = new List<string> { "Name,Title,Owner,Last on" };
        lines.AddRange(people.Select(p => string.Join(",", Cell(p.Name), Cell(p.Title), p.Owner ? "yes" : "no", Cell(p.Online))));
        try { File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(true)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Reader?.Answer($"Couldn't save the list: {e.Message}", keep: false); return; }
        Answer($"Saved the {Count(people.Count, "member")} of {g.Name} to {Path.GetFileName(path)} in {Path.GetFileName(Path.GetDirectoryName(path))}.");
    }

    /// <summary>The group's members (owners first, each with their title and whether they're on), or who's in its chat now
    /// (joining it first, as the viewer's chat window does when it opens), or why not.</summary>
    async Task<(List<GroupPerson>? People, string? Why)> LoadGroupPeopleAsync(MyGroup g, bool chat)
    {
        var me = _ops.Client?.Self.AgentID ?? UUID.Zero;
        string Mine(UUID id, string words) => id == me ? $"{words}, you" : words;
        if (!chat)
        {
            var r = await _ops.ReadAsync("TesselGroups", "members", new JsonObject { ["group_id"] = g.Id.ToString() });
            if (PersonOps.Error(r) is { } e) return (null, $"Couldn't read {g.Name}'s members: {e}");
            var people = new List<GroupPerson>();
            foreach (var m in (r["members"] as JsonArray ?? new()).OfType<JsonObject>())
            {
                if (!UUID.TryParse(m.Str("id"), out var id)) continue;
                var name = m.Str("name") ?? "someone";
                bool owner = m["owner"]?.GetValue<bool>() == true;
                var words = new List<string> { name };
                if (owner) words.Add("an owner");
                if (m.Str("title") is { Length: > 0 } title) words.Add(title);
                if (m.Str("online") is { Length: > 0 } on) words.Add(on);
                people.Add(new GroupPerson(id, name, Mine(id, string.Join(", ", words)), owner, false, m.Str("title") ?? "", m.Str("online") ?? ""));
            }
            return (people, null);
        }
        var c = await _ops.DoAsync("SojournPeople", "groupChat", new JsonObject { ["group_id"] = g.Id.ToString(), ["join"] = true });
        if (PersonOps.Error(c) is { } ce) return (null, $"Couldn't see who's in {g.Name}'s chat: {ce}");
        if (c["chat_on"]?.GetValue<bool>() == false) return (null, $"You've turned off {g.Name}'s chat, so who's in it can't be seen.");
        var inChat = new List<GroupPerson>();
        foreach (var m in (c["members"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            if (!UUID.TryParse(m.Str("id"), out var id)) continue;
            var name = Host?.Session.Names.Complete(id, m.Str("name") ?? "someone") ?? m.Str("name") ?? "someone";
            bool stopped = m["text_stopped"]?.GetValue<bool>() == true;
            var words = new List<string> { name };
            if (m["moderator"]?.GetValue<bool>() == true) words.Add("a moderator");
            if (stopped) words.Add("text stopped");
            inChat.Add(new GroupPerson(id, name, Mine(id, string.Join(", ", words)), false, stopped));
        }
        return (inChat.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(), null);
    }

    /// <summary>What the group lets her do to someone, then their own actions (the window closing first, as a profile's
    /// does), the letters kept apart by Mnemonics.</summary>
    void AddGroupPersonItems(ContextMenuStrip menu, Form form, MyGroup g, GroupPerson p, bool chat, Func<UUID?, Task> reload)
    {
        bool me = p.Id == _ops.Client?.Self.AgentID;
        void Add(string text, Func<Task> act) => menu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => _ = act()));
        if (chat && !me && _ops.Client is { } c && Host?.Session.GroupChat.IModerate(c, g.Id) == true)
            Add(p.TextStopped ? "Let them te&xt in the chat again" : "Stop their te&xt in the chat", () => ModerateTextAsync(form, g, p, reload));
        Add("&Roles...", () => MemberRolesAsync(form, g, p));
        if (me)
        {
            Add("Your &profile", () => { form.Close(); return MyProfileAsync(); });
            return;
        }
        Add($"&Eject from {MenuWords(g.Name)}", () => EjectMemberAsync(form, g, p, reload));
        Add($"&Ban from {MenuWords(g.Name)}", () => BanAsync(form, g, p.Id, p.Name, p.Owner, reload));
        menu.Items.Add(new ToolStripSeparator());
        AddPersonItems(menu, p.Id, p.Name, first: form.Close);
    }

    async Task ModerateTextAsync(Form form, MyGroup g, GroupPerson p, Func<UUID?, Task> reload)
    {
        bool allow = p.TextStopped;
        var r = await ConfirmedFromAsync(form, "SojournPeople", "moderateGroupChat", new JsonObject { ["group_id"] = g.Id.ToString(), ["to"] = p.Id.ToString(), ["text"] = allow });
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not changed: {e}"); return; }
        Answer(allow ? $"{p.Name} can type in {g.Name}'s chat again." : $"{p.Name}'s text is stopped in {g.Name}'s chat: nobody there sees what they type.");
        await reload(null);
    }

    /// <summary>Someone's roles there: every role, theirs said so; Enter gives one or takes it away, through her confirmation,
    /// and what the grid shows after is said (it ignores a change her roles don't allow without a word).</summary>
    async Task MemberRolesAsync(Form form, MyGroup g, GroupPerson p)
    {
        var args = new JsonObject { ["group_id"] = g.Id.ToString() };
        var r = await _ops.ReadAsync("TesselGroups", "roles", args);
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read {g.Name}'s roles: {e}"); return; }
        var roles = (r["roles"] as JsonArray ?? new()).OfType<JsonObject>().ToList();
        var profile = await _ops.ReadAsync("TesselGroups", "profile", args);
        var ownersRole = profile.Str("owner_role");
        var me = _ops.Client?.Self.AgentID.ToString();
        bool Everyone(JsonObject role) => role["everyone"]?.GetValue<bool>() == true;
        bool In(JsonObject role, string? who) => (role["members"] as JsonArray ?? new()).OfType<JsonObject>().Any(m => m.Str("id") == who);
        bool Has(JsonObject role) => Everyone(role) || In(role, p.Id.ToString());
        var words = roles.Select(x => $"{x.Str("name")}{(Everyone(x) ? ", everyone has it" : Has(x) ? ", has it" : "")}").ToList();
        if (form.IsDisposed) return;
        if (Dialogs.Choose(form, $"{p.Name}'s roles in {g.Name}", "Roles", words, 0) is not { } pick) { Reader?.Answer("Roles left as they were.", keep: false); return; }
        var role = roles[pick];
        var name = role.Str("name") ?? "that role";
        if (Everyone(role)) { Answer($"Everyone in a group has the role {name}; it can't be given or taken away."); return; }
        bool has = Has(role);
        var powers = Host?.Session.State.Groups.TryGetValue(g.Id, out var grp) == true ? grp.Powers : GroupPowers.None;
        if (!has && (powers & GroupPowers.AssignMember) == 0)
        {
            if ((powers & GroupPowers.AssignMemberLimited) == 0) { Answer($"Your roles in {g.Name} don't let you give roles."); return; }
            if (!In(role, me)) { Answer($"Your roles in {g.Name} let you give only roles you have yourself, and {name} isn't one of them."); return; }
        }
        if (has && role.Str("role_id") == ownersRole && p.Id.ToString() != me) { Answer($"Nobody can take an owner off {name} but the owner themselves."); return; }
        if (has && Lacks(g, GroupPowers.RemoveMember, "take roles away") is { } no) { Answer(no); return; }
        var change = new JsonObject { ["group_id"] = g.Id.ToString(), ["role_id"] = role.Str("role_id"), ["member"] = p.Id.ToString() };
        var done = await ConfirmedFromAsync(form, "TesselGroups", has ? "unassignRole" : "assignRole", change);
        if (done == null) return;
        if (PersonOps.Error(done) is { } de) { Answer($"Not changed: {de}"); return; }
        // what the grid shows now, as it may ignore a change without a word
        bool? now = null;
        for (int i = 0; i < 8 && now != !has; i++)
        {
            await Task.Delay(500);
            var again = await _ops.ReadAsync("TesselGroups", "roles", new JsonObject { ["group_id"] = g.Id.ToString() });
            if ((again["roles"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => x.Str("role_id") == role.Str("role_id")) is { } shown) now = In(shown, p.Id.ToString());
        }
        Answer(now != !has ? $"Asked; {g.Name}'s roles don't show the change yet."
            : has ? $"{p.Name} doesn't have the role {name} now." : $"{p.Name} has the role {name} now.");
    }

    async Task EjectMemberAsync(Form form, MyGroup g, GroupPerson p, Func<UUID?, Task> reload)
    {
        if (p.Owner) { Answer($"{p.Name} is an owner of {g.Name}: owners can't be ejected, only leave by themselves."); return; }
        if (Lacks(g, GroupPowers.Eject, "eject members") is { } no) { Answer(no); return; }
        var r = await ConfirmedFromAsync(form, "TesselGroups", "eject", new JsonObject { ["group_id"] = g.Id.ToString(), ["member"] = p.Id.ToString() });
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"{p.Name} wasn't ejected: {e}"); return; }
        Answer($"Ejected {p.Name} from {g.Name}.");
        await reload(p.Id);
    }

    /// <summary>Banned from joining, and ejected if a member, as the viewer's Ban does; Second Life only.</summary>
    async Task BanAsync(Form? form, MyGroup g, UUID id, string name, bool owner, Func<UUID?, Task>? reload)
    {
        if (!GroupOps.BansHere(_ops.Client)) { Answer(BansNotHere); return; }
        if (owner) { Answer($"{name} is an owner of {g.Name}: owners can't be banned."); return; }
        if (Lacks(g, GroupPowers.GroupBanAccess, "ban people") is { } no) { Answer(no); return; }
        var r = await ConfirmedFromAsync(form, "TesselGroups", "ban", new JsonObject { ["group_id"] = g.Id.ToString(), ["member"] = id.ToString() });
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"{name} wasn't banned: {e}"); return; }
        Answer((r["ejected"] as JsonArray)?.Count > 0 ? $"Banned {name} from {g.Name}, and ejected them." : $"Banned {name} from {g.Name}.");
        if (reload != null) await reload((r["ejected"] as JsonArray)?.Count > 0 ? id : null);
    }

    /// <summary>Banned residents (the viewer's tab): who and since when; Enter on one lifts the ban, the first row bans someone
    /// else. Second Life only.</summary>
    async Task GroupBansAsync(MyGroup g)
    {
        if (!GroupOps.BansHere(_ops.Client)) { Answer(BansNotHere); return; }
        var r = await _ops.ReadAsync("TesselGroups", "bans", new JsonObject { ["group_id"] = g.Id.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read who's banned from {g.Name}: {e}"); return; }
        var bans = (r["bans"] as JsonArray ?? new()).OfType<JsonObject>().ToList();
        static string Since(JsonObject b) => DateTime.TryParse(b.Str("since"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? $", banned {d.ToLocalTime().ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}" : "";
        var words = new List<string> { "Ban someone..." };
        words.AddRange(bans.Select(b => $"{b.Str("name")}{Since(b)}"));
        var heading = bans.Count == 0 ? $"Banned from {g.Name}: nobody" : $"Banned from {g.Name}: {PeopleCount(bans.Count)}";
        if (Dialogs.Choose(this, heading, "Banned residents", words, bans.Count > 0 ? 1 : 0) is not { } pick) { Reader?.Answer("Nothing changed.", keep: false); return; }
        if (pick == 0)
        {
            var who = Dialogs.Pick(this, $"Ban from {g.Name}", await PeopleToPickAsync(), FindSomeoneAsync);
            if (who == null) { Reader?.Answer("Nobody banned.", keep: false); return; }
            await BanAsync(null, g, who.Id, who.Name, owner: false, reload: null);
            return;
        }
        var ban = bans[pick - 1];
        var name = ban.Str("name") ?? "them";
        if (Lacks(g, GroupPowers.GroupBanAccess, "lift bans") is { } no) { Answer(no); return; }
        var lifted = await ConfirmedAsync("TesselGroups", "unban", new JsonObject { ["group_id"] = g.Id.ToString(), ["member"] = ban.Str("id") });
        if (lifted == null) return;
        Answer(PersonOps.Error(lifted) is { } le ? $"The ban on {name} wasn't lifted: {le}" : $"{name} isn't banned from {g.Name} now.");
    }

    /// <summary>People nearby, then friends (online first), for a person picker; anyone else by typing their name.</summary>
    async Task<List<Dialogs.Someone>> PeopleToPickAsync()
    {
        var people = new List<Dialogs.Someone>();
        foreach (var x in await _ops.PeopleAsync(96)) people.Add(new(x.Id, x.Name, _ops.Where(x.Pos) ?? "nearby"));
        if (_ops.Client is { } c)
            foreach (var f in c.Friends.FriendList.Values.OrderByDescending(f => f.IsOnline))
                if (people.All(x => x.Id != f.UUID))
                    people.Add(new(f.UUID, Host!.Session.Names.Complete(f.UUID, string.IsNullOrEmpty(f.Name) ? "a friend" : f.Name), f.IsOnline ? "friend, online" : "friend"));
        return people;
    }
}
