using System.Text.Json.Nodes;
using LibreMetaverse;

namespace Sojourn.Access;

/// <summary>People and objects nearby: lists, nearest first, each item one sentence ("Kestrel, 4 m, two o'clock"),
/// with every action on the Applications key (or Shift+F10, or Enter). A list the person is in doesn't change under
/// them: it refreshes on F5, and by itself only while they're elsewhere.</summary>
public sealed partial class MainForm
{
    TabPage _peoplePage = null!, _objectsPage = null!;
    ListView _people = null!, _objects = null!;
    ContextMenuStrip _personMenu = null!, _objectMenu = null!;
    /// <summary>People nearby again in the chat area, a Tab from Say (Radegast keeps its nearby list beside chat, and a
    /// blind tester's habit reaches for it there): the same items, actions and refreshing as Alt+3's.</summary>
    ListView _chatPeople = null!;
    ContextMenuStrip _chatPersonMenu = null!;
    long _listsAt;

    static ListView List(string name, string accessibleName)
    {
        var l = new ReaderList
        {
            Name = name, AccessibleName = accessibleName, View = View.Details, HeaderStyle = ColumnHeaderStyle.None, FullRowSelect = true,
            MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill,
        };
        l.Columns.Add(accessibleName, 800);
        return l;
    }

    void BuildPeople()
    {
        _peoplePage = new TabPage("People nearby") { Name = "peoplePage", AccessibleName = "People nearby" };
        (_people, _personMenu) = PeopleList("people", "personMenu");
        BuildPeoplePage();
    }

    /// <summary>A People nearby list with its actions on the Applications key (or Shift+F10, or Enter), F5 refreshing it.</summary>
    (ListView, ContextMenuStrip) PeopleList(string name, string menuName)
    {
        var list = List(name, "People nearby");
        var menu = new ContextMenuStrip { Name = menuName, AccessibleName = "Actions" };
        void Item(string text, Action<PersonOps.Person> act) =>
            menu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => { if (list.FocusedItem?.Tag is PersonOps.Person p) act(p); }));
        Item("Send &IM", p => StartIm(p.Id, p.Name));
        Item("&Walk to", p => _ = _cmd.WalkAsync(p.Pos, p.Name, 1.5, $"//go tp person {p.Name}"));
        Item("&Face", p => _ = _cmd.FacePointAsync(p.Pos, p.Name));
        Item("W&here", p => Answer($"{p.Name} is {_ops.Where(p.Pos, full: true)}."));
        Item("&Describe", p => _ = DescribeAsync(Of(p)));
        Item("F&ollow", p => _cmd.Follow(p));
        Item("&Beacon", p => _walk?.BeaconOn(p.Name, p.Id));
        Item("&Profile", p => _ = ProfileAsync(p.Id, p.Name));
        Item("P&ay...", p => _ = PayAsync(p.Id, p.Name));
        Item("&Give what you copied", p => _ = GiveCopiedAsync(p.Id, p.Name));
        Item("Offer &teleport", p => _ = PersonOpAsync("offerTeleport", p, $"Teleport offered to {p.Name}."));
        Item("&Request teleport", p => _ = PersonOpAsync("requestTeleport", p, $"Asked {p.Name} for a teleport."));
        Item("Always in&cognito with them", p => IncognitoWith(p.Id, p.Name));
        KeysOn(menu, ("&Describe", "Control+D"));
        MenuOn(list, menu);
        list.KeyDown += (_, e) => ListKey(list, menu, e, asked => RefreshPeople(asked, list));
        return (list, menu);
    }

    /// <summary>The People nearby list the focus is in, if it's in one.</summary>
    ListView? PeopleFocused => _people.Focused ? _people : _chatPeople.Focused ? _chatPeople : null;

    async Task PersonOpAsync(string op, PersonOps.Person p, string done)
    {
        var r = await _ops.DoAsync("TesselAgent", op, new JsonObject { ["to"] = p.Id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't go: {e}" : done);
    }

    void BuildObjects()
    {
        _objectsPage = new TabPage("Objects nearby") { Name = "objectsPage", AccessibleName = "Objects nearby" };
        _objects = List("objects", "Objects nearby");
        _objectMenu = new ContextMenuStrip { Name = "objectMenu", AccessibleName = "Actions" };
        void Item(string text, Action<PersonOps.Thing> act) =>
            _objectMenu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => { if (_objects.FocusedItem?.Tag is PersonOps.Thing t) act(t); }));
        Item("&Touch", t => _ = ThingOpAsync("requestTouch", t, $"Touched {t.Name}."));
        Item("&Sit", t => _ = ThingOpAsync("requestSit", t, $"Sitting on {t.Name}."));
        Item("&Walk to", t => _ = _cmd.WalkAsync(t.Pos, t.Name, 1.5, $"//go tp object {t.Name}"));
        Item("&Beacon", t => _walk?.BeaconOn(t.Name, t.Id));
        Item("&Face", t => _ = _cmd.FacePointAsync(t.Pos, t.Name));
        Item("&Describe", t => _ = DescribeAsync(Of(t)));
        Item("W&here", t => Answer(t.Hud ? $"{t.Name} is worn {t.Text}." : $"{t.Name} is {_ops.Where(t.Pos, full: true)}.{(string.IsNullOrWhiteSpace(t.Text) ? "" : $" Its sign says: {t.Text}")}"));
        Item("&Parts", _ => _parts.Focus());
        Item("&Move...", t => _ = MoveByWordsAsync(t));
        // the person's own things back out of the world (the blind tester found no way to delete what she'd rezzed, 2026-11-01)
        Item("Ta&ke into your inventory", t => _ = TakeThingAsync(t));
        Item("De&lete", t => _ = DeleteThingAsync(t));
        // F5's refresh, here too so it can be found (the blind tester found no way to refresh, 2026-10-31)
        _objectMenu.Items.Add(new ToolStripMenuItem("&Refresh", null, (_, _) => _ = RefreshObjects(true)));
        // a HUD is touched, described and its parts listed; the rest is for things in the world
        _objectMenu.Opening += (_, _) =>
        {
            var sel = _objects.FocusedItem?.Tag as PersonOps.Thing;
            bool hud = sel is { Hud: true }, mine = sel is { Mine: true };
            foreach (ToolStripItem i in _objectMenu.Items)
                i.Visible = hud ? i.Text is "&Touch" or "&Describe" or "W&here" or "&Parts" or "&Refresh"
                    : mine || i.Text is not ("Ta&ke into your inventory" or "De&lete");
        };
        KeysOn(_objectMenu, ("&Describe", "Control+D"), ("&Parts", "Tab"), ("De&lete", "Delete"), ("&Refresh", "F5"));
        MenuOn(_objects, _objectMenu);
        _objects.KeyDown += (_, e) => ListKey(_objects, _objectMenu, e, RefreshObjects);
        _objects.KeyDown += (_, e) =>
        {
            if (e.KeyData != Keys.Delete || _objects.FocusedItem?.Tag is not PersonOps.Thing t) return;
            e.Handled = e.SuppressKeyPress = true;
            if (t.Hud) Answer($"{t.Name} is worn: take it off in the inventory (Alt+4).");
            else _ = DeleteThingAsync(t);
        };
        BuildParts();
    }

    /// <summary>One of the person's things in the world into their inventory's Objects folder, as the viewer's Take.</summary>
    async Task TakeThingAsync(PersonOps.Thing t)
    {
        if (!t.Mine) { Answer($"{t.Name} isn't yours to take."); return; }
        var r = await _ops.DoAsync("TesselBuild", "take", new JsonObject { ["ids"] = new JsonArray(t.Id.ToString()) });
        Answer(PersonOps.Error(r) is { } e ? $"Not taken: {e}" : $"Took {t.Name} into your inventory.");
        await Task.Delay(500);
        _ = RefreshObjects(false);
    }

    /// <summary>One of the person's things in the world deleted into their Trash, after their confirmation (Notices).</summary>
    async Task DeleteThingAsync(PersonOps.Thing t)
    {
        if (!t.Mine) { Answer($"{t.Name} isn't yours to delete."); return; }
        if (await ConfirmedAsync("TesselBuild", "delete", new JsonObject { ["ids"] = new JsonArray(t.Id.ToString()) }) is not { } r) return;
        Answer(PersonOps.Error(r) is { } e ? $"Not deleted: {e}" : $"Deleted {t.Name} (in the Trash).");
        await Task.Delay(500);
        _ = RefreshObjects(false);
    }

    async Task ThingOpAsync(string op, PersonOps.Thing t, string done)
    {
        var r = await _ops.DoAsync("LLAgent", op, new JsonObject { ["obj_uuid"] = t.Id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't work: {e}" : done);
        if (op == "requestTouch" && PersonOps.Error(r) is null && !t.Hud) _route?.Touched(t.Name);   // (a route being recorded keeps it)
    }

    void ListKey(ListView list, ContextMenuStrip menu, KeyEventArgs e, Func<bool, Task> refresh)
    {
        if (e.KeyCode == Keys.F5) { e.Handled = true; _ = refresh(true); }
        else if (e.KeyCode == Keys.Enter && list.FocusedItem?.Tag != null)
        {
            e.Handled = e.SuppressKeyPress = true;
            ShowListMenu(list, menu);
        }
    }

    /// <summary>A list's actions on the Applications key, Shift+F10 or a right click: shown on its key's press, not by
    /// the control's ContextMenuStrip (which opens on WM_CONTEXTMENU, a key's release, and never for posted keys).</summary>
    static void MenuOn(ListView list, ContextMenuStrip menu)
    {
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Apps && e.KeyData != (Keys.Shift | Keys.F10)) return;
            e.Handled = e.SuppressKeyPress = true;
            ShowListMenu(list, menu);
        };
        list.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowListMenu(list, menu); };
    }

    static void ShowListMenu(ListView list, ContextMenuStrip menu)
    {
        if (list.FocusedItem?.Tag == null) return;
        var r = list.FocusedItem.Bounds;
        menu.Show(list, new Point(r.Left + 10, r.Bottom));
    }

    void AreaShown()
    {
        if (_areas.SelectedTab == _peoplePage) { _ = RefreshPeople(false, _people); RefreshFriendsAndGroups(said: false); }
        else if (_areas.SelectedTab == _chatPage) _ = RefreshPeople(false);
        else if (_areas.SelectedTab == _objectsPage) _ = RefreshObjects(false);
        else if (_areas.SelectedTab == _inventoryPage) InventoryShown();
        else if (_areas.SelectedTab == _placesPage) _ = RefreshPlacesAsync(said: false);
        else if (_areas.SelectedTab == _settingsPage && Host?.Session.Online == true) _ = RefreshSettings();
    }

    void AutoRefreshLists()
    {
        if (Environment.TickCount64 - _listsAt < 3000) return;
        _listsAt = Environment.TickCount64;
        if (_areas.SelectedTab == _peoplePage && !_people.Focused || _areas.SelectedTab == _chatPage && !_chatPeople.Focused) _ = RefreshPeople(false);
        if (_areas.SelectedTab == _peoplePage) RefreshFriendsAndGroups(said: false);
        if (_areas.SelectedTab == _objectsPage && !_objects.Focused && !_objectsBusy) _ = RefreshObjects(false);
    }

    bool _peopleBusy, _objectsBusy;
    bool? _objectsAgain;   // asked for while a refresh was under way (Show changed, F5): once it ends, again (said if any asked)

    /// <summary>Tests: the first fill of People nearby and Objects nearby held back this long (SOJOURN_TEST_LIST_DELAY_MS),
    /// as on a busy Second Life region, where the objects list waits up to 3 s for names: arriving before it fills.</summary>
    static readonly int ListDelayMs = int.TryParse(Environment.GetEnvironmentVariable("SOJOURN_TEST_LIST_DELAY_MS"), out var ms) ? ms : 0;
    readonly HashSet<ListView> _delayed = new();
    Task ListDelay(ListView list) => ListDelayMs > 0 && _delayed.Add(list) ? Task.Delay(ListDelayMs) : Task.CompletedTask;

    /// <summary>A list not filled yet says so in its name, so arriving in it before it fills isn't silence ("Objects
    /// nearby, looking"); its first item is then read as the focus moves to it.</summary>
    static void Looking(ListView list, string name, bool looking) => list.AccessibleName = looking ? $"{name}, looking" : name;

    (bool Asked, ListView? Into)? _peopleAgain;   // asked for while a refresh was under way: once it ends, again

    /// <summary>People nearby into the lists showing that the person isn't in (Alt+3's, the chat area's), and into
    /// `into` even if they are (F5 there, or the area's key pressed again: read again).</summary>
    async Task RefreshPeople(bool asked, ListView? into = null)
    {
        if (Host?.Session.Online != true) return;
        if (_peopleBusy)
        {
            // (arriving in it empty while another list's refresh runs: still not silence)
            if (into is { Items.Count: 0 }) Looking(into, "People nearby", true);
            if (into != null || asked || _peopleAgain == null) _peopleAgain = (asked || _peopleAgain?.Asked == true, into ?? _peopleAgain?.Into);
            return;
        }
        var lists = new[] { (_people, _peoplePage), (_chatPeople, _chatPage) }
            .Where(l => l.Item1 == into || _areas.SelectedTab == l.Item2 && !l.Item1.Focused).Select(l => l.Item1).ToList();
        if (lists.Count == 0) return;
        _peopleBusy = true;
        var first = lists.Where(l => l.Items.Count == 0).ToList();
        foreach (var l in first) Looking(l, "People nearby", true);
        try
        {
            var people = await _ops.PeopleAsync(96);
            var items = people.Select(p => (Text: $"{p.Name}, {_ops.Where(p.Pos)}", Key: p.Id.ToString(), Tag: (object)p)).ToList();
            foreach (var l in lists)
            {
                await ListDelay(l);
                // (it may have taken the focus meanwhile: then it stays as it is, unless asked for or still empty)
                if (l == into || !l.Focused || l.Items.Count == 0) Fill(l, items, "Nobody nearby.");
            }
            if (asked) Reader?.Answer(people.Count == 0 ? "Nobody nearby." : $"{people.Count} {(people.Count == 1 ? "person" : "people")}.", keep: false);
        }
        finally
        {
            _peopleBusy = false;
            foreach (var l in first) Looking(l, "People nearby", false);
        }
        if (_peopleAgain is { } again) { _peopleAgain = null; await RefreshPeople(again.Asked, again.Into); }
    }

    async Task RefreshObjects(bool asked)
    {
        if (Host?.Session.Online != true) return;
        // (Show changed while a refresh waits up to 3 s for names: a list the person is in never refreshes by itself)
        if (_objectsBusy) { _objectsAgain = asked || _objectsAgain == true; return; }
        _objectsBusy = true;
        bool first = _objects.Items.Count == 0;
        if (first) Looking(_objects, "Objects nearby", true);
        try
        {
            bool worn = ShowingWorn;
            // scripted things' names asked again (F5 all of them, else those over 5 s old): a script renaming its
            // object reaches only those who have it selected, so a list holding names for minutes missed renames
            var things = worn ? await WornAsync() : await _ops.ObjectsAsync(30, 60, renew: asked ? 0 : 5);
            await ListDelay(_objects);
            Fill(_objects, things.Select(t => (Text: t.Hud ? $"{t.Name}, {t.Text}" : $"{t.Name}, {_ops.Where(t.Pos)}{(t.Sit ? ", can sit" : t.Touch ? ", touchable" : "")}",
                Key: t.Id.ToString(), Tag: (object)t)).ToList(), worn ? "Nothing worn here yet." : "Nothing nearby.");
            if (first) Looking(_objects, "Objects nearby", false);
            if (asked) Reader?.Answer(things.Count == 0 ? (worn ? "Nothing worn here yet." : "Nothing nearby.")
                : worn ? WornCount(things) : $"{things.Count} objects.", keep: false);
            // the parts below follow the object selected, unless the person is in them
            // (or the same one renamed: "Parts of" its new name)
            if (_objects.FocusedItem?.Tag is PersonOps.Thing sel && (sel.Id != _partsOf || _parts.AccessibleName != $"Parts of {sel.Name}")
                && !_parts.Focused && !_faces.Focused) _ = RefreshPartsAsync(sel);
            else if (things.Count == 0 && !_parts.Focused && !_faces.Focused) { _partsOf = UUID.Zero; Fill(_parts, new(), "No object selected."); Fill(_faces, new(), "No part selected."); }
        }
        finally
        {
            _objectsBusy = false;
            if (first) Looking(_objects, "Objects nearby", false);
        }
        if (_objectsAgain is { } again) { _objectsAgain = null; await RefreshObjects(again); }
    }

    /// <summary>The list's items replaced, the one the person was on still focused (by key), or a line saying it's empty.</summary>
    static void Fill(ListView list, List<(string Text, string Key, object Tag)> items, string empty)
    {
        // the same words as shown: only what the actions use is renewed (where people are), so a list beside the one
        // the person is in (the chat area's, while they type) isn't emptied and refilled every few seconds for nothing
        if (!list.Focused && items.Count > 0 && list.Items.Count == items.Count
            && items.Select((it, i) => list.Items[i].Name == it.Key && list.Items[i].Text == it.Text).All(x => x))
        {
            for (int i = 0; i < items.Count; i++) list.Items[i].Tag = items[i].Tag;
            return;
        }
        var focusedKey = list.FocusedItem?.Name;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            if (items.Count == 0) list.Items.Add(new ListViewItem(empty) { Name = "" });
            foreach (var (text, key, tag) in items) list.Items.Add(new ListViewItem(text) { Name = key, Tag = tag });
            var keep = (focusedKey != null ? list.Items[focusedKey] : null) ?? list.Items[0];
            keep.Selected = true;
            ReaderList.MakeCurrent(keep);   // announced only in the list the person is in
        }
        finally { list.EndUpdate(); }
    }
}
