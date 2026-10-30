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
        _people = List("people", "People nearby");
        _personMenu = new ContextMenuStrip { Name = "personMenu", AccessibleName = "Actions" };
        void Item(string text, Action<PersonOps.Person> act) =>
            _personMenu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => { if (_people.FocusedItem?.Tag is PersonOps.Person p) act(p); }));
        Item("Send &IM", p => StartIm(p.Id, p.Name));
        Item("&Walk to", p => _ = _cmd.WalkAsync(p.Pos, p.Name, 1.5));
        Item("&Face", p => _ = _cmd.FacePointAsync(p.Pos, p.Name));
        Item("W&here", p => Answer($"{p.Name} is {_ops.Where(p.Pos, full: true)}."));
        Item("&Describe", p => _ = DescribeAsync(Of(p)));
        Item("F&ollow", p => _cmd.Follow(p));
        Item("&Beacon", p => _walk?.BeaconOn(p.Name, p.Id));
        Item("&Profile", p => _ = ProfileAsync(p.Id, p.Name));
        Item("P&ay...", p => _ = PayAsync(p.Id, p.Name));
        Item("Offer &teleport", p => _ = PersonOpAsync("offerTeleport", p, $"Teleport offered to {p.Name}."));
        Item("&Request teleport", p => _ = PersonOpAsync("requestTeleport", p, $"Asked {p.Name} for a teleport."));
        Item("Always in&cognito with them", p => IncognitoWith(p.Id, p.Name));
        MenuOn(_people, _personMenu);
        _people.KeyDown += (_, e) => ListKey(_people, _personMenu, e, RefreshPeople);
        BuildPeoplePage();
    }

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
        Item("&Walk to", t => _ = _cmd.WalkAsync(t.Pos, t.Name, 1.5));
        Item("&Beacon", t => _walk?.BeaconOn(t.Name, t.Id));
        Item("&Face", t => _ = _cmd.FacePointAsync(t.Pos, t.Name));
        Item("&Describe", t => _ = DescribeAsync(Of(t)));
        Item("W&here", t => Answer(t.Hud ? $"{t.Name} is worn {t.Text}." : $"{t.Name} is {_ops.Where(t.Pos, full: true)}.{(string.IsNullOrWhiteSpace(t.Text) ? "" : $" Its sign says: {t.Text}")}"));
        Item("&Parts", _ => _parts.Focus());
        Item("&Move...", t => _ = MoveByWordsAsync(t));
        // a HUD is touched, described and its parts listed; the rest is for things in the world
        _objectMenu.Opening += (_, _) =>
        {
            bool hud = _objects.FocusedItem?.Tag is PersonOps.Thing { Hud: true };
            foreach (ToolStripItem i in _objectMenu.Items)
                i.Visible = !hud || i.Text is "&Touch" or "&Describe" or "W&here" or "&Parts";
        };
        MenuOn(_objects, _objectMenu);
        _objects.KeyDown += (_, e) => ListKey(_objects, _objectMenu, e, RefreshObjects);
        BuildParts();
    }

    async Task ThingOpAsync(string op, PersonOps.Thing t, string done)
    {
        var r = await _ops.DoAsync("LLAgent", op, new JsonObject { ["obj_uuid"] = t.Id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't work: {e}" : done);
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
        if (_areas.SelectedTab == _peoplePage) { _ = RefreshPeople(false); RefreshFriendsAndGroups(said: false); }
        else if (_areas.SelectedTab == _objectsPage) _ = RefreshObjects(false);
        else if (_areas.SelectedTab == _inventoryPage) InventoryShown();
        else if (_areas.SelectedTab == _placesPage) _ = RefreshPlacesAsync(said: false);
        else if (_areas.SelectedTab == _settingsPage && Host?.Session.Online == true) _ = RefreshSettings();
    }

    void AutoRefreshLists()
    {
        if (Environment.TickCount64 - _listsAt < 3000) return;
        _listsAt = Environment.TickCount64;
        if (_areas.SelectedTab == _peoplePage && !_people.Focused) _ = RefreshPeople(false);
        if (_areas.SelectedTab == _peoplePage) RefreshFriendsAndGroups(said: false);
        if (_areas.SelectedTab == _objectsPage && !_objects.Focused) _ = RefreshObjects(false);
    }

    bool _peopleBusy, _objectsBusy;

    /// <summary>Tests: the first fill of People nearby and Objects nearby held back this long (SOJOURN_TEST_LIST_DELAY_MS),
    /// as on a busy Second Life region, where the objects list waits up to 3 s for names: arriving before it fills.</summary>
    static readonly int ListDelayMs = int.TryParse(Environment.GetEnvironmentVariable("SOJOURN_TEST_LIST_DELAY_MS"), out var ms) ? ms : 0;
    readonly HashSet<ListView> _delayed = new();
    Task ListDelay(ListView list) => ListDelayMs > 0 && _delayed.Add(list) ? Task.Delay(ListDelayMs) : Task.CompletedTask;

    /// <summary>A list not filled yet says so in its name, so arriving in it before it fills isn't silence ("Objects
    /// nearby, looking"); its first item is then read as the focus moves to it.</summary>
    static void Looking(ListView list, string name, bool looking) => list.AccessibleName = looking ? $"{name}, looking" : name;

    async Task RefreshPeople(bool asked)
    {
        if (_peopleBusy || Host?.Session.Online != true) return;
        _peopleBusy = true;
        bool first = _people.Items.Count == 0;
        if (first) Looking(_people, "People nearby", true);
        try
        {
            var people = await _ops.PeopleAsync(96);
            await ListDelay(_people);
            Fill(_people, people.Select(p => (Text: $"{p.Name}, {_ops.Where(p.Pos)}", Key: p.Id.ToString(), Tag: (object)p)).ToList(), "Nobody nearby.");
            if (asked) Reader?.Answer(people.Count == 0 ? "Nobody nearby." : $"{people.Count} {(people.Count == 1 ? "person" : "people")}.", keep: false);
        }
        finally
        {
            _peopleBusy = false;
            if (first) Looking(_people, "People nearby", false);
        }
    }

    async Task RefreshObjects(bool asked)
    {
        if (_objectsBusy || Host?.Session.Online != true) return;
        _objectsBusy = true;
        bool first = _objects.Items.Count == 0;
        if (first) Looking(_objects, "Objects nearby", true);
        try
        {
            bool huds = ShowingHuds;
            var things = huds ? await HudsAsync() : await _ops.ObjectsAsync(30, 60);
            await ListDelay(_objects);
            Fill(_objects, things.Select(t => (Text: t.Hud ? $"{t.Name}, {t.Text}" : $"{t.Name}, {_ops.Where(t.Pos)}{(t.Sit ? ", can sit" : t.Touch ? ", touchable" : "")}",
                Key: t.Id.ToString(), Tag: (object)t)).ToList(), huds ? "No HUDs worn." : "Nothing nearby.");
            if (first) Looking(_objects, "Objects nearby", false);
            if (asked) Reader?.Answer(things.Count == 0 ? (huds ? "No HUDs worn." : "Nothing nearby.")
                : huds ? (things.Count == 1 ? "1 HUD." : $"{things.Count} HUDs.") : $"{things.Count} objects.", keep: false);
            // the parts below follow the object selected, unless the person is in them
            if (_objects.FocusedItem?.Tag is PersonOps.Thing sel && sel.Id != _partsOf && !_parts.Focused && !_faces.Focused) _ = RefreshPartsAsync(sel);
            else if (things.Count == 0 && !_parts.Focused && !_faces.Focused) { _partsOf = UUID.Zero; Fill(_parts, new(), "No object selected."); Fill(_faces, new(), "No part selected."); }
        }
        finally
        {
            _objectsBusy = false;
            if (first) Looking(_objects, "Objects nearby", false);
        }
    }

    /// <summary>The list's items replaced, the one the person was on still focused (by key), or a line saying it's empty.</summary>
    static void Fill(ListView list, List<(string Text, string Key, object Tag)> items, string empty)
    {
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
