using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// A group's settings and roles, in a window of their own (docs/access-app.md > Groups > Settings and roles): the
/// official viewer's General tab's group settings and its Roles tab, laid out as About Land is, a settings list over a
/// list of tabs (General, then each role). A role's tab has its name, title, description, whether its members are shown
/// and who has it, then its abilities, a switch each under the viewer's sets. Every change is sent as she presses it, as
/// the viewer's boxes and checkboxes are, and said once the grid shows it; who may join, deleting a role and the two
/// abilities the viewer warns about (giving any role, giving abilities) wait for her confirmation. Claude Opus 5.5,
/// 2026-11-09.
/// </summary>
public sealed partial class MainForm
{
    readonly Dictionary<UUID, GroupSettingsWindow> _groupSettings = new();

    /// <summary>The group menu's Settings and roles: its window, or the one open already brought forward.</summary>
    void GroupSettings(MyGroup g)
    {
        if (_groupSettings.TryGetValue(g.Id, out var open) && !open.Form.IsDisposed) { open.Form.Activate(); return; }
        var w = new GroupSettingsWindow(this, g);
        _groupSettings[g.Id] = w;
        _ = GuardedAsync(w.OpenAsync, $"{g.Name}'s settings");
    }

    sealed class GroupSettingsWindow
    {
        readonly MainForm _m;
        readonly MyGroup _g;
        public Form Form { get; }
        readonly ListView _rows;
        readonly ComboBox _tab;
        JsonObject _profile = new();
        List<JsonObject> _roles = new();
        bool _tabOurs;

        PersonOps Ops => _m._ops;
        UUID Me => Ops.Client?.Self.AgentID ?? UUID.Zero;
        void Say(string text) => _m.Answer(text);
        bool PostLinks => Ops.Client?.Network.CurrentSim?.Features?.Has("GroupChatPostLinksEnabled") == true;

        /// <summary>The role whose tab is shown, or null on General.</summary>
        JsonObject? Role => _tab.SelectedIndex > 0 && _tab.SelectedIndex - 1 < _roles.Count ? _roles[_tab.SelectedIndex - 1] : null;
        string RoleName(JsonObject r) => r.Str("name") ?? "a role";
        bool Owners(JsonObject r) => r.Str("role_id") == _profile.Str("owner_role");

        public GroupSettingsWindow(MainForm m, MyGroup g)
        {
            _m = m;
            _g = g;
            var title = $"Settings and roles of {g.Name}";
            Form = new Form
            {
                Name = "groupSettings", Text = title, AccessibleName = title, Size = new Size(640, 560),
                StartPosition = FormStartPosition.CenterParent, KeyPreview = true, ShowInTaskbar = false,
            };
            _rows = List("groupRows", "Settings");
            _tab = new ComboBox { Name = "groupTab", AccessibleName = "Tab", DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            var newRole = new Button { Name = "groupNewRole", Text = "&New role...", AutoSize = true };
            var close = new Button { Name = "groupSettingsClose", Text = "Close", AutoSize = true };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = "&Settings", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, TabIndex = 0 }, 0, 0);
            t.Controls.Add(_rows, 1, 0);
            t.Controls.Add(new Label { Text = "&Tab", AutoSize = true, Anchor = AnchorStyles.Left, TabIndex = 2 }, 0, 1);
            t.Controls.Add(_tab, 1, 1);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.AddRange(new Control[] { newRole, close });
            t.Controls.Add(buttons, 1, 2);
            _rows.TabIndex = 1; _tab.TabIndex = 3; buttons.TabIndex = 4;
            Form.Controls.Add(t);

            _tab.SelectedIndexChanged += (_, _) => { if (!_tabOurs) Show(said: false); };
            _rows.KeyDown += (_, e) => RowKey(e);
            _rows.Enter += (_, _) => { if (_rows.FocusedItem == null && _rows.Items.Count > 0) _rows.Items[0].Focused = true; };
            newRole.Click += (_, _) => _ = _m.GuardedAsync(NewRoleAsync, "a new role");
            close.Click += (_, _) => Form.Close();
            Form.CancelButton = close;
            Form.KeyDown += (_, e) =>
            {
                if (e.KeyData == Keys.F1) { e.Handled = e.SuppressKeyPress = true; _m.Reader?.Answer(Keys1, keep: false); }
                else if (e.KeyData is (Keys.Control | Keys.PageDown) or (Keys.Control | Keys.PageUp) && _tab.Items.Count > 0)
                {
                    // (as a tab control's own keys: the next tab or the one before, round the ends)
                    e.Handled = e.SuppressKeyPress = true;
                    int n = _tab.Items.Count, i = (_tab.SelectedIndex + (e.KeyCode == Keys.PageDown ? 1 : -1) + n) % n;
                    Choose(i);
                    Show(said: true);
                }
            };
            Form.FormClosed += (_, _) =>
            {
                if (_m._groupSettings.TryGetValue(_g.Id, out var w) && w == this) _m._groupSettings.Remove(_g.Id);
                if (!_m.IsDisposed) _m.Activate();
            };
            Form.ActiveControl = _rows;
        }

        const string Keys1 = "The group's settings and its roles. In the list, Enter changes a setting: a switch turns, a choice opens its list, anything "
            + "else asks for it typed; Right and Left step it; Page Down and Page Up go set by set through a role's abilities. Tab: the tabs, General "
            + "then each role, the arrows going through them; Control+Page Down and Page Up do it from anywhere. New role, Alt+N. F5 reads it all "
            + "again; Escape closes.";

        public async Task OpenAsync()
        {
            _m.Reader?.Answer($"Asking for {_g.Name}'s settings and roles.", keep: false);
            if (!await LoadAsync()) { Form.Dispose(); _m._groupSettings.Remove(_g.Id); return; }
            Show(said: false);
            Form.Shown += (_, _) => _m.Reader?.Answer($"{_rows.AccessibleName}. Tab for the tabs: General, then each of its {_roles.Count} roles; "
                + "New role, Alt+N. F1 for keys.", keep: false);
            Form.Show(_m);
        }

        /// <summary>Its profile and roles read again; false (said) when they didn't come.</summary>
        async Task<bool> LoadAsync()
        {
            var args = new JsonObject { ["group_id"] = _g.Id.ToString() };
            var profile = Ops.ReadAsync("TesselGroups", "profile", args.DeepClone().AsObject());
            var roles = Ops.ReadAsync("TesselGroups", "roles", args);
            var p = await profile;
            var r = await roles;
            if (PersonOps.Error(p) is { } pe) { Say($"Couldn't read {_g.Name}'s settings: {pe}"); return false; }
            if (PersonOps.Error(r) is { } re) { Say($"Couldn't read {_g.Name}'s roles: {re}"); return false; }
            _profile = p;
            SetRoles(r);
            return true;
        }

        /// <summary>The roles in the viewer's order (Everyone, Owners, then by name), and the tabs named for them, the one shown kept.</summary>
        void SetRoles(JsonObject r)
        {
            var keep = Role?.Str("role_id");
            var owner = _profile.Str("owner_role");
            _roles = (r["roles"] as JsonArray ?? new()).OfType<JsonObject>()
                .OrderBy(x => GroupRows.IsEveryone(x) ? 0 : x.Str("role_id") == owner ? 1 : 2).ThenBy(x => x.Str("name"), StringComparer.OrdinalIgnoreCase).ToList();
            var names = new[] { "General" }.Concat(_roles.Select(x => RoleName(x))).ToArray();
            int at = keep == null ? Math.Max(0, _tab.SelectedIndex) : _roles.FindIndex(x => x.Str("role_id") == keep) + 1;
            _tabOurs = true;
            try
            {
                if (!_tab.Items.Cast<string>().SequenceEqual(names)) { _tab.Items.Clear(); _tab.Items.AddRange(names); }
                _tab.SelectedIndex = Math.Clamp(at, 0, names.Length - 1);
            }
            finally { _tabOurs = false; }
        }

        void Choose(int index)
        {
            _tabOurs = true;
            try { _tab.SelectedIndex = index; }
            finally { _tabOurs = false; }
        }

        List<BuildRow> Rows() => Role is { } role ? GroupRows.Role(role, _profile, Me, PostLinks) : GroupRows.General(_profile, _roles);

        /// <summary>The list for the tab chosen (a row that's still the same row keeps its place as its words change).</summary>
        void Show(bool said)
        {
            if (Form.IsDisposed) return;
            var rows = Rows();
            _rows.AccessibleName = Role is { } role ? $"Role {RoleName(role)} of {_g.Name}" : $"General settings of {_g.Name}";
            RowsInto(_rows, rows.Select(r => (r.Words, r.Key, (object)r)).ToList(), "None.");
            if (said) _m.Reader?.Answer($"{_rows.AccessibleName}: {rows.Count} rows.", keep: false);
        }

        BuildRow? Now(string key) => Rows().FirstOrDefault(r => r.Key == key);

        void RowKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; _ = _m.GuardedAsync(async () => { if (await LoadAsync()) { Show(said: false); Say($"{_rows.AccessibleName}, read again."); } }, "reading again"); return; }
            if (e.Control || e.Alt || _rows.FocusedItem?.Tag is not BuildRow r) return;
            switch (e.KeyCode)
            {
                case Keys.Enter: e.Handled = e.SuppressKeyPress = true; _ = _m.GuardedAsync(() => EnterAsync(r), r.Label); break;
                case Keys.Space or Keys.Right: e.Handled = e.SuppressKeyPress = true; _ = _m.GuardedAsync(() => StepAsync(r, e.Shift ? -1 : 1), r.Label); break;
                case Keys.Left: e.Handled = e.SuppressKeyPress = true; _ = _m.GuardedAsync(() => StepAsync(r, -1), r.Label); break;
                case Keys.PageDown or Keys.PageUp:
                    e.Handled = e.SuppressKeyPress = true;
                    Heading(e.KeyCode == Keys.PageDown ? 1 : -1);
                    break;
            }
        }

        /// <summary>Page Down and Up: the next heading (an ability set) or the one before, said as the focus lands on it.</summary>
        void Heading(int dir)
        {
            int at = _rows.FocusedItem?.Index ?? -1;
            for (int i = at + dir; i >= 0 && i < _rows.Items.Count; i += dir)
                if (_rows.Items[i].Tag is BuildRow { Key: var k } && k.StartsWith("h.", StringComparison.Ordinal))
                {
                    _rows.Items[i].Selected = true;
                    _rows.Items[i].Focused = true;
                    _rows.Items[i].EnsureVisible();
                    ReaderList.MakeCurrent(_rows.Items[i]);
                    _m.Reader?.Answer(_rows.Items[i].Text + ".", keep: false);
                    return;
                }
            _m.Reader?.Answer(dir > 0 ? "No more sets after this." : "No sets before this.", keep: false);
        }

        async Task EnterAsync(BuildRow r)
        {
            if (r.Key == "r.delete") { await DeleteRoleAsync(); return; }
            switch (r.Kind)
            {
                case RowKind.Info: Say(r.Words + "."); return;
                case RowKind.Switch: await ApplyAsync(r, r.Value > 0 ? "off" : "on"); return;
                case RowKind.Choice: ChoiceMenu(r); return;
            }
            if (Refusal(r) is { } no) { Say(no); return; }
            var typed = r.Key == "g.charter"
                ? Dialogs.AskLines(Form, "Charter", "Charter (Enter starts a new line; Control+Enter saves it)", r.Now)
                : Dialogs.Ask(Form, r.Label, r.Ask, r.Now, allowEmpty: r.Key is "r.title" or "r.desc");
            if (typed == null) { _m.Reader?.Answer("Not changed.", keep: false); return; }
            await ApplyAsync(r, typed);
        }

        void ChoiceMenu(BuildRow r)
        {
            var menu = new ContextMenuStrip { Name = "choiceMenu", AccessibleName = r.Label };
            for (int i = 0; i < r.Choices!.Length; i++)
            {
                var name = r.Choices[i];
                menu.Items.Add(new ToolStripMenuItem(name, null, (_, _) => _ = _m.GuardedAsync(() => ApplyAsync(r, name), r.Label)) { Checked = i == r.Chosen });
            }
            menu.Closed += (_, _) => Form.BeginInvoke(menu.Dispose);
            var at = _rows.FocusedItem?.Bounds ?? default;
            menu.Show(_rows, new Point(at.Left + 10, at.Bottom));
            Mnemonics.Fix(menu);
            if (r.Chosen >= 0 && r.Chosen < menu.Items.Count) menu.Items[r.Chosen].Select();
        }

        async Task StepAsync(BuildRow r, int dir)
        {
            switch (r.Kind)
            {
                case RowKind.Switch: await ApplyAsync(r, r.Value > 0 ? "off" : "on"); return;
                case RowKind.Choice when r.Choices is { Length: > 0 } ch:
                    await ApplyAsync(r, ch[r.Chosen < 0 ? 0 : ((r.Chosen + dir) % ch.Length + ch.Length) % ch.Length]);
                    return;
                case RowKind.Number when r.Step > 0:
                    await ApplyAsync(r, $"{Math.Max(0, Math.Round((r.Value + dir * r.Step) / r.Step) * r.Step)}");
                    return;
                default:
                    Say($"{r.Label}: {(r.Kind == RowKind.Info ? "read from the grid" : "Enter to type it")}.");
                    return;
            }
        }

        /// <summary>Why this row can't be changed (the role it is, or her roles' powers), said before anything's asked.</summary>
        string? Refusal(BuildRow r)
        {
            if (r.Key.StartsWith("g.", StringComparison.Ordinal))
                return GroupRows.PowerOf(r.Key) is var p and not GroupPowers.None
                    ? _m.Lacks(_g, p, p == GroupPowers.ChangeOptions ? "change who may join or its cost" : "change the charter, Show in search or the maturity") : null;
            if (Role is not { } role) return null;
            bool everyone = GroupRows.IsEveryone(role), owners = Owners(role);
            return r.Key switch
            {
                "r.name" when everyone || owners => $"The {RoleName(role)} role's name can't be changed.",
                "r.desc" when everyone => "The Everyone role's description can't be changed.",
                "r.name" or "r.title" or "r.desc" or "r.shown" => _m.Lacks(_g, GroupPowers.RoleProperties, "change roles' names, titles and descriptions"),
                _ when r.Key.StartsWith("a.", StringComparison.Ordinal) => _m.Lacks(_g, GroupPowers.ChangeActions, "change roles' abilities"),
                _ => null,
            };
        }

        async Task ApplyAsync(BuildRow r, string value)
        {
            if (Refusal(r) is { } no) { Say(no); return; }
            if (r.Key.StartsWith("g.", StringComparison.Ordinal)) await GeneralAsync(r, value);
            else if (Role is { } role) await RoleAsync(r, role, value);
        }

        async Task GeneralAsync(BuildRow r, string value)
        {
            var (args, confirm, _, error) = GroupRows.GeneralChange(r.Key, value);
            if (args == null) { Say(error ?? "Not changed."); return; }
            var before = r.Words;
            args["group_id"] = _g.Id.ToString();
            var res = confirm ? await _m.ConfirmedFromAsync(Form, "TesselGroups", "update", args) : await Ops.DoAsync("TesselGroups", "update", args);
            if (res == null || Form.IsDisposed) return;
            if (PersonOps.Error(res) is { } e) { Say($"Not changed: {e}"); return; }
            _profile = res;
            Show(said: false);
            var after = Now(r.Key)?.Words;
            Say(after == null ? "Changed." : after == before ? $"Already so: {Dot(after)}" : Dot(after));
        }

        /// <summary>A row's words as a sentence: a full stop unless they end in one ("Charter: Come in." not "..").</summary>
        static string Dot(string s) => s.Length > 0 && s[^1] is '.' or '!' or '?' ? s : s + ".";

        /// <summary>One of a role's settings or abilities: TesselGroups updateRole with the whole role as it would be, then
        /// said as the grid shows it (it ignores a change her roles don't allow without a word).</summary>
        async Task RoleAsync(BuildRow r, JsonObject role, string value)
        {
            var rid = role.Str("role_id");
            ulong bits = GroupRows.Bits(role), want = bits;
            var args = new JsonObject { ["group_id"] = _g.Id.ToString(), ["role_id"] = rid };
            bool confirm = false;
            string? also = null;
            switch (r.Key)
            {
                case "r.name":
                {
                    var name = value.Trim();
                    if (name.Length == 0) { Say("A role needs a name."); return; }
                    if (GroupRows.Bytes(name) > GroupRows.RoleNameBytes) { Say($"A role's name is at most {GroupRows.RoleNameBytes} bytes, about as many letters; this one is {GroupRows.Bytes(name)}."); return; }
                    if (_roles.Any(x => x != role && string.Equals(x.Str("name"), name, StringComparison.OrdinalIgnoreCase))) { Say($"{_g.Name} has a role called {name} already."); return; }
                    args["name"] = name;
                    break;
                }
                case "r.title":
                {
                    var title = value.Trim();
                    if (GroupRows.Bytes(title) > GroupRows.RoleTitleBytes) { Say($"A title is at most {GroupRows.RoleTitleBytes} bytes, about as many letters; this one is {GroupRows.Bytes(title)}."); return; }
                    args["title"] = title;
                    break;
                }
                case "r.desc":
                {
                    var desc = value.Trim();
                    if (desc.Length > GroupRows.RoleDescChars) { Say($"A role's description is at most {GroupRows.RoleDescChars} characters; this one is {desc.Length}."); return; }
                    args["desc"] = desc;
                    break;
                }
                case "r.shown":
                    if (GroupRows.On(value) is not { } shown) { Say("Members shown outside the group: on or off."); return; }
                    want = shown ? bits | GroupAbilities.MembersShown : bits & ~GroupAbilities.MembersShown;
                    break;
                default:
                {
                    if (!r.Key.StartsWith("a.", StringComparison.Ordinal) || !int.TryParse(r.Key[2..], out var bit) || GroupAbilities.Of(bit) is not { } ability) { Say($"{r.Label}: read from the grid."); return; }
                    if (Owners(role)) { Say("The Owners role has every ability, always."); return; }
                    if (GroupRows.On(value) is not { } on) { Say($"{ability.Words}: on or off."); return; }
                    ulong ban = 1UL << GroupAbilities.BanBit;
                    if (on)
                    {
                        want = bits | ability.Mask;
                        // as the viewer: the ban list comes with ejecting and taking members off roles
                        if (bit == GroupAbilities.BanBit && (bits & ((1UL << GroupAbilities.EjectBit) | (1UL << GroupAbilities.RemoveFromRolesBit))) != ((1UL << GroupAbilities.EjectBit) | (1UL << GroupAbilities.RemoveFromRolesBit)))
                        {
                            want |= (1UL << GroupAbilities.EjectBit) | (1UL << GroupAbilities.RemoveFromRolesBit);
                            also = $" With it, as Second Life ties them: {GroupAbilities.Of(GroupAbilities.EjectBit)!.Words}, and {GroupAbilities.Of(GroupAbilities.RemoveFromRolesBit)!.Words}.";
                        }
                        confirm = GroupAbilities.Warning(bit) != null && (bits & ability.Mask) == 0;
                    }
                    else
                    {
                        if (bit is GroupAbilities.EjectBit or GroupAbilities.RemoveFromRolesBit && (bits & ban) != 0)
                        {
                            Say($"{ability.Words} comes with {GroupAbilities.Of(GroupAbilities.BanBit)!.Words}: turn that off first.");
                            return;
                        }
                        want = bits & ~ability.Mask;
                        if (bit == GroupAbilities.BanBit && (bits & ban) != 0)
                            also = $" {GroupAbilities.Of(GroupAbilities.EjectBit)!.Words}, and {GroupAbilities.Of(GroupAbilities.RemoveFromRolesBit)!.Words}, stay on: turn them off too if you meant to.";
                    }
                    break;
                }
            }
            if (want != bits) args["powers"] = want.ToString();
            if (args.Count == 2)
            {
                Say($"Already so: {r.Words}.");
                return;
            }
            var before = r.Words;
            var res = confirm ? await _m.ConfirmedFromAsync(Form, "TesselGroups", "updateRole", args) : await Ops.DoAsync("TesselGroups", "updateRole", args);
            if (res == null || Form.IsDisposed) return;
            if (PersonOps.Error(res) is { } e) { Say($"Not changed: {e}"); return; }
            bool shows = await ShownAsync(rid, x =>
                (args.Str("name") is not { } n || x.Str("name") == n) && (args.Str("title") is not { } t || x.Str("title") == t)
                && (args.Str("desc") is not { } d || x.Str("desc") == d) && GroupRows.Bits(x) == want);
            if (Form.IsDisposed) return;
            Show(said: false);
            if (!shows) { Say($"Asked; {_g.Name}'s roles don't show the change yet."); return; }
            var after = Now(r.Key)?.Words;
            Say((after == null ? "Changed." : after == before ? $"Already so: {Dot(after)}" : Dot(after)) + (also ?? ""));
        }

        /// <summary>The roles read again until the role shows what was asked (or for 4 s); whether it does.</summary>
        async Task<bool> ShownAsync(string? rid, Func<JsonObject, bool> done)
        {
            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(500);
                var again = await Ops.ReadAsync("TesselGroups", "roles", new JsonObject { ["group_id"] = _g.Id.ToString() });
                if (PersonOps.Error(again) != null) continue;
                var role = (again["roles"] as JsonArray ?? new()).OfType<JsonObject>().FirstOrDefault(x => x.Str("role_id") == rid);
                if (role != null && done(role) || rid != null && role == null && done(new JsonObject()))
                {
                    if (!Form.IsDisposed) SetRoles(again);
                    return true;
                }
                if (i == 7 && !Form.IsDisposed) SetRoles(again);
            }
            return false;
        }

        /// <summary>New role (Alt+N): its name asked, made with no abilities, its tab shown.</summary>
        async Task NewRoleAsync()
        {
            if (_m.Lacks(_g, GroupPowers.CreateRole, "create roles") is { } no) { Say(no); return; }
            var name = Dialogs.Ask(Form, "New role", $"The new role's name, at most {GroupRows.RoleNameBytes} letters")?.Trim();
            if (string.IsNullOrEmpty(name)) { _m.Reader?.Answer("No role made.", keep: false); return; }
            if (GroupRows.Bytes(name) > GroupRows.RoleNameBytes) { Say($"A role's name is at most {GroupRows.RoleNameBytes} bytes, about as many letters; this one is {GroupRows.Bytes(name)}."); return; }
            if (_roles.Any(x => string.Equals(x.Str("name"), name, StringComparison.OrdinalIgnoreCase))) { Say($"{_g.Name} has a role called {name} already."); return; }
            var r = await Ops.DoAsync("TesselGroups", "createRole", new JsonObject { ["group_id"] = _g.Id.ToString(), ["name"] = name, ["title"] = name });
            if (Form.IsDisposed) return;
            if (PersonOps.Error(r) is { } e) { Say($"No role made: {e}"); return; }
            var again = await Ops.ReadAsync("TesselGroups", "roles", new JsonObject { ["group_id"] = _g.Id.ToString() });
            if (PersonOps.Error(again) == null) SetRoles(again);
            int at = _roles.FindIndex(x => x.Str("role_id") == r.Str("role_id")) + 1;
            if (at > 0) Choose(at);
            Show(said: false);
            _rows.Focus();
            if (_rows.Items.Count > 0) { _rows.Items[0].Selected = true; _rows.Items[0].Focused = true; }
            Say($"Made the role {name}, with its title {name} and no abilities yet: its tab is showing.");
        }

        /// <summary>Delete this role: through her confirmation, then General shown.</summary>
        async Task DeleteRoleAsync()
        {
            if (Role is not { } role) return;
            if (GroupRows.IsEveryone(role) || Owners(role)) { Say($"The {RoleName(role)} role can't be deleted."); return; }
            if (_m.Lacks(_g, GroupPowers.DeleteRole, "delete roles") is { } no) { Say(no); return; }
            var name = RoleName(role);
            var rid = role.Str("role_id");
            var r = await _m.ConfirmedFromAsync(Form, "TesselGroups", "deleteRole", new JsonObject { ["group_id"] = _g.Id.ToString(), ["role_id"] = rid });
            if (r == null || Form.IsDisposed) return;
            if (PersonOps.Error(r) is { } e) { Say($"Not deleted: {e}"); return; }
            Choose(0);
            bool gone = await ShownAsync(rid, x => x.Str("role_id") == null);
            if (Form.IsDisposed) return;
            Show(said: false);
            Say(gone ? $"Deleted the role {name}." : $"Asked; {_g.Name}'s roles still show {name}.");
        }
    }
}
