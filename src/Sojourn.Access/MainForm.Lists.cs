using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Access;

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

    bool _objectsAToZ;

    /// <summary>Whether she's sitting on this object or one of its parts.</summary>
    bool SeatedOn(UUID thing)
    {
        if (_ops.Client is not { } c || c.Self.SittingOn == 0 || Host?.Session.Region is not { } w) return false;
        if (!w.Prims.TryGetValue(c.Self.SittingOn, out var seat)) return false;
        return seat.ID == thing || (seat.ParentID != 0 && w.Prims.TryGetValue(seat.ParentID, out var root) && root.ID == thing);
    }

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
        var menu = PersonMenu(menuName, () => list.FocusedItem?.Tag is PersonOps.Person p ? (p.Id, p.Name) : null);
        MenuOn(list, menu);
        list.KeyDown += (_, e) => ListKey(list, menu, e, asked => RefreshPeople(asked, list));
        // people come in under her on "Nobody nearby." with no item focused (RefreshPeople): Down or Up reaches the first
        list.KeyDown += (_, e) =>
        {
            if (e.KeyData is not (Keys.Down or Keys.Up) || list.FocusedItem != null || list.Items.Count == 0) return;
            e.Handled = e.SuppressKeyPress = true;
            list.Items[0].Selected = true;
            list.Items[0].Focused = true;
        };
        return (list, menu);
    }

    /// <summary>The People nearby list the focus is in, if it's in one.</summary>
    ListView? PeopleFocused => _people.Focused ? _people : _chatPeople.Focused ? _chatPeople : null;

    void BuildObjects()
    {
        _objectsPage = new TabPage("Objects nearby") { Name = "objectsPage", AccessibleName = "Objects nearby" };
        _objects = List("objects", "Objects nearby");
        _objectMenu = new ContextMenuStrip { Name = "objectMenu", AccessibleName = "Actions" };
        void Item(string text, Action<PersonOps.Thing> act) =>
            _objectMenu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => { if (_objects.FocusedItem?.Tag is PersonOps.Thing t) act(t); }));
        Item("&Touch", t => _ = TouchThingAsync(t));
        Item("&Sit", t => _ = ThingOpAsync("requestSit", t, $"Sitting on {t.Name}."));
        var sitItem = _objectMenu.Items[^1];
        Item("&Stand up", t => _ = _cmd.RunAsync("//stand"));
        var standItem = _objectMenu.Items[^1];
        // as the viewer's pie menu offers them (ThingActions.For; the blind tester's tip jars, 2026-11-05)
        Item("P&ay...", t => _ = PayThingAsync(t.PayTo, t.Name));
        var payItem = _objectMenu.Items[^1];
        Item("B&uy", t => _ = BuyThingAsync(t.Id, t.Name));
        var buyItem = _objectMenu.Items[^1];
        Item("Take a &copy", t => _ = TakeCopyAsync(t.Id, t.Name));
        var copyItem = _objectMenu.Items[^1];
        Item("&Walk to", t => _ = _cmd.WalkAsync(t.Pos, t.Name, 1.5, $"//go tp object {t.Name}"));
        Item("&Beacon", t => _walk?.BeaconOn(t.Name, t.Id));
        Item("&Face", t => _ = _cmd.FacePointAsync(t.Pos, t.Name));
        Item("&Describe", t => _ = DescribeAsync(Of(t)));
        Item("Deta&ils", t => _ = ThingDetailsAsync(t.Id, t.Name));
        Item("W&here", t => Answer(t.Hud ? $"{t.Name} is worn {t.Text}."
            : t.WornBy != UUID.Zero ? $"{t.Name} is worn by {t.WornByName} on {t.Point}, {_ops.Where(t.Pos, full: true)}."
            : $"{t.Name} is {_ops.Where(t.Pos, full: true)}.{(string.IsNullOrWhiteSpace(t.Text) ? "" : $" Its sign says: {t.Text}")}"));
        Item("Co&ntents", t => _ = ContentsOfAsync(t.Id, onePart: false));
        var contentsItem = _objectMenu.Items[^1];
        Item("&Parts", _ => _parts.Focus());
        Item("&Move...", t => _ = MoveByWordsAsync(t));
        var moveItem = _objectMenu.Items[^1];
        Item("&Edit (build)", t => EditThing(t.Id));
        var editItem = _objectMenu.Items[^1];
        Item("Link to what I'm editin&g", t => _ = LinkThingAsync(t));
        var linkItem = _objectMenu.Items[^1];
        Item("Cop&y its settings to what I'm editing", t => _ = GuardedAsync(() => CopyToEditingAsync(t.Id), t.Name));
        var copySettings = _objectMenu.Items[^1];
        Item("Bl&ock", t => _ = BlockAsync(t.Id, t.Name, "object"));
        var blockItem = _objectMenu.Items[^1];
        var sortItem = new ToolStripMenuItem("Sort A to &Z", null, (_, _) =>
        {
            _objectsAToZ = !_objectsAToZ;
            Reader?.Answer(_objectsAToZ ? "A to Z." : "Nearest first.", keep: false);
            _ = RefreshObjects(true);
        });
        _objectMenu.Items.Add(sortItem);
        // what may leave the world together, each through her confirmation where it isn't hers, and well away from what's
        // pressed every day: Return has no letter of its own, so only the arrows reach it (the blind tester found no way to
        // delete what she'd rezzed, 2026-11-01)
        Item("Ta&ke into your inventory", t => _ = TakeThingAsync(t));
        var takeItem = _objectMenu.Items[^1];
        Item("De&lete", t => _ = DeleteThingAsync(t));
        var deleteItem = _objectMenu.Items[^1];
        Item("Return to its owner...", t => _ = ReturnThingAsync(t));
        var returnItem = _objectMenu.Items[^1];
        // what she wears, off again from where she found it (the blind tester couldn't take a HUD off here, 2026-11-02)
        Item("Take &off", t => _ = TakeOffWornAsync(t));
        var offItem = _objectMenu.Items[^1];
        // F5's refresh, here too so it can be found (the blind tester found no way to refresh, 2026-10-31)
        _objectMenu.Items.Add(new ToolStripMenuItem("&Refresh", null, (_, _) => _ = RefreshObjects(true)));
        // something worn is touched, described, its parts listed; what's in the world has the rest
        _objectMenu.Opening += (_, _) =>
        {
            var sel = _objects.FocusedItem?.Tag as PersonOps.Thing;
            bool mine = sel is { Hud: true }, others = sel != null && sel.WornBy != UUID.Zero, world = sel != null && !mine && !others;
            foreach (ToolStripItem i in _objectMenu.Items)
                i.Visible = world || i.Text is "&Touch" or "&Describe" or "Deta&ils" or "W&here" or "&Parts" or "&Refresh";
            offItem.Visible = mine;
            // on what she's sitting on, Stand up in its place
            bool seated = world && sel != null && SeatedOn(sel.Id);
            sitItem.Visible = world && !seated;
            standItem.Visible = seated;
            sortItem.Text = _objectsAToZ ? "Sort nearest first, &Z" : "Sort A to &Z";
            sortItem.Visible = true;
            contentsItem.Visible = moveItem.Visible = editItem.Visible = world;
            blockItem.Visible = world && sel is { Mine: false };
            takeItem.Visible = world && MayTake(sel!);
            deleteItem.Visible = world && MayDelete(sel!);
            returnItem.Visible = world && MayReturn(sel!);
            payItem.Visible = (world || others) && sel is { Money: true };
            buyItem.Visible = world && sel is { Mine: false, Sale: not null };
            copyItem.Visible = world && sel is { MayCopy: true };
            linkItem.Visible = copySettings.Visible = world && sel is { Mine: true } && LinkableNow(sel);
            // its own click action leads, as the viewer's left click does it
            _objectMenu.Items.Remove(payItem);
            _objectMenu.Items.Remove(buyItem);
            int after = _objectMenu.Items.IndexOf(_objectMenu.Items.OfType<ToolStripItem>().First(i => i.Text == "&Sit")) + 1;
            _objectMenu.Items.Insert(after, buyItem);
            _objectMenu.Items.Insert(after, payItem);
            if ((sel?.Click == "pay" ? payItem : sel?.Click == "buy" ? buyItem : null) is { Available: true } lead)   // (Visible reads false until the menu shows)
            {
                _objectMenu.Items.Remove(lead);
                _objectMenu.Items.Insert(0, lead);
            }
            if (sel != null)
                Host?.Log.LogInformation("objects: actions on {Name} ({Where}, left click {Click}): {Items}", sel.Name, mine ? "worn" : others ? "worn by someone" : sel.Mine ? "yours, nearby" : "nearby", sel.Click ?? "touch",
                    string.Join(", ", _objectMenu.Items.OfType<ToolStripItem>().Where(i => i.Available).Select(i => i.Text.Replace("&", ""))));
        };
        KeysOn(_objectMenu, ("&Describe", "Control+D"), ("Deta&ils", "Alt+Enter"), ("&Parts", "Tab"), ("&Edit (build)", "Control+B"), ("De&lete", "Delete"), ("Take &off", "Delete"), ("&Refresh", "F5"));
        MenuOn(_objects, _objectMenu);
        _objects.KeyDown += (_, e) => ListKey(_objects, _objectMenu, e, RefreshObjects);
        _objects.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _objects.FocusedItem?.Name == MoreRow) { e.Handled = e.SuppressKeyPress = true; MoreObjects(); return; }
            if (e.KeyData == (Keys.Alt | Keys.Enter) && _objects.FocusedItem?.Tag is PersonOps.Thing d) { e.Handled = e.SuppressKeyPress = true; _ = ThingDetailsAsync(d.Id, d.Name); return; }
            if (e.KeyData != Keys.Delete || _objects.FocusedItem?.Tag is not PersonOps.Thing t) return;
            e.Handled = e.SuppressKeyPress = true;
            if (t.Hud) _ = TakeOffWornAsync(t);
            else _ = DeleteThingAsync(t);
        };
        BuildParts();
    }

    /// <summary>A thing in the world into the person's inventory's Objects folder, as the viewer's Take: theirs at once,
    /// someone else's (the viewer offers it where they may change it and pass it on) after their confirmation.</summary>
    async Task TakeThingAsync(PersonOps.Thing t)
    {
        if (!MayTake(t)) { Answer($"{t.Name} isn't yours to take."); return; }
        var args = new JsonObject { ["ids"] = new JsonArray(t.Id.ToString()) };
        if ((t.Mine ? await _ops.DoAsync("TesselBuild", "take", args) : await ConfirmedAsync("TesselBuild", "take", args)) is not { } r) return;
        Answer(PersonOps.Error(r) is { } e ? $"Not taken: {e}" : $"Took {t.Name} into your inventory.");
        await Task.Delay(500);
        _ = RefreshObjects(false);
    }

    /// <summary>Something worn taken off, back into the inventory (Delete on it, or Take off): never deleted.</summary>
    async Task TakeOffWornAsync(PersonOps.Thing t)
    {
        var r = await _ops.DoAsync("TesselAgent", "detach", new JsonObject { ["object_id"] = t.Id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't take off {t.Name}: {e}" : $"Took off {t.Name}.");
        await Task.Delay(1000);
        _ = RefreshObjects(false);
    }

    /// <summary>A thing in the world deleted, after the person's confirmation (Notices): theirs goes to their Trash.</summary>
    async Task DeleteThingAsync(PersonOps.Thing t)
    {
        if (!MayDelete(t)) { Answer($"{t.Name} isn't yours to delete."); return; }
        if (await ConfirmedAsync("TesselBuild", "delete", new JsonObject { ["ids"] = new JsonArray(t.Id.ToString()) }) is not { } r) return;
        Answer(PersonOps.Error(r) is { } e ? $"Not deleted: {e}" : t.Mine ? $"Deleted {t.Name} (in the Trash)." : $"Deleted {t.Name}.");
        await Task.Delay(500);
        _ = RefreshObjects(false);
    }

    async Task ThingOpAsync(string op, PersonOps.Thing t, string done)
    {
        var r = await _ops.DoAsync("LLAgent", op, new JsonObject { ["obj_uuid"] = t.Id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't work: {e}" : done);
        if (op == "requestTouch" && PersonOps.Error(r) is null && !t.Hud && t.WornBy == UUID.Zero) _route?.Touched(t.Name);   // (a route being recorded keeps it)
    }

    void ListKey(ListView list, ContextMenuStrip menu, KeyEventArgs e, Func<bool, Task> refresh)
    {
        if (e.KeyCode == Keys.F5) { e.Handled = true; _ = refresh(true); }
        else if (e.KeyCode == Keys.Enter && !e.Alt && list.FocusedItem?.Tag != null)
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
        Mnemonics.Fix(menu);
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
        if (_areas.SelectedTab == _peoplePage && Renewable(_people) || _areas.SelectedTab == _chatPage && Renewable(_chatPeople)) _ = RefreshPeople(false);
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

    /// <summary>Only the line saying a list is empty ("Nobody nearby."): nothing in it to keep still under her.</summary>
    static bool OnlyEmptyLine(ListView list) => list.Items.Count == 1 && list.Items[0].Tag == null && list.Items[0].Name.Length == 0;

    /// <summary>A People nearby list kept current by itself: while she's elsewhere, or while it says nobody's there or
    /// holds nothing yet, her focus in it or not (tabbing in a moment after a teleport found "Nobody nearby." and it stayed
    /// so, 2026-11-08; tabbing in a second after logging in, before its first fill, left it empty and silent).</summary>
    static bool Renewable(ListView list) => !list.Focused || list.Items.Count == 0 || OnlyEmptyLine(list);

    /// <summary>People nearby into the lists showing that the person isn't in (Alt+3's, the chat area's) or that say
    /// nobody's there, and into `into` even if they are (F5 there, or the area's key pressed again: read again). People
    /// arriving in a list she's on "Nobody nearby." in come in under her without an item taking the focus (Down reaches
    /// the first), said once as an arrival ("2 people nearby now.").</summary>
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
            .Where(l => l.Item1 == into || _areas.SelectedTab == l.Item2 && Renewable(l.Item1)).Select(l => l.Item1).ToList();
        if (lists.Count == 0) return;
        _peopleBusy = true;
        var first = lists.Where(l => l.Items.Count == 0).ToList();
        foreach (var l in first) Looking(l, "People nearby", true);
        try
        {
            var people = await _ops.PeopleAsync(_set.PeopleRange);
            var items = people.Select(p => (Text: $"{p.Name}, {_ops.Where(p.Pos)}", Key: p.Id.ToString(), Tag: (object)p)).ToList();
            foreach (var l in lists)
            {
                await ListDelay(l);
                // (it may have taken the focus meanwhile: then it stays as it is, unless asked for, still empty, or
                // saying nobody's there)
                if (l != into && l.Focused && OnlyEmptyLine(l))
                {
                    if (items.Count == 0) continue;
                    Fill(l, items, "Nobody nearby.", under: true);
                    if (!asked) Reader?.Say(AnnounceKind.Notices, $"{items.Count} {(items.Count == 1 ? "person" : "people")} nearby now.", keep: false, from: LineFrom.App);
                }
                else if (l == into || !l.Focused || l.Items.Count == 0) Fill(l, items, "Nobody nearby.");
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
        if (first) Looking(_objects, ObjectsName, true);
        try
        {
            bool worn = ShowingWorn, others = ShowingOthers;
            var one = ShowingOne ? _wornBy : null;
            var filter = ObjectsFilter;
            // scripted things' names asked again (F5 all of them, else those over 5 s old): a script renaming its
            // object reaches only those who have it selected, so a list holding names for minutes missed renames
            var page = worn ? WornPage(await WornAsync(), filter)
                : one is { } by ? await _ops.NearbyAsync(512, ObjectsLimit, filter, scan: filter != null, wornBy: by.Id)   // (one person's: wherever they are)
                : await _ops.NearbyAsync(_set.ObjectsRange, ObjectsLimit, filter, renew: filter != null ? null : asked ? 0 : 5, scan: filter != null, others: others);
            // one person's things by name (all at their distance); what a friend pointed at among things nearby
            var things = one != null ? page.Things.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList() : await WithPointedAsync(page.Things);
            if (_objectsAToZ && !worn) things = things.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            await ListDelay(_objects);
            var items = things.Select(t => (Text: ObjectLine(t), Key: t.Id.ToString(), Tag: (object)t)).ToList();
            if (page.More > 0) items.Add(($"More: {page.More} further on. Enter lists the next {Math.Min(page.More, ObjectsPage)}.", MoreRow, null!));
            Fill(_objects, items, ObjectsEmpty(filter));
            LandAfterMore();
            if (_objectsAgain == null) _ = LandOnPointedAsync();   // (Control+Shift+P; a refresh still to come has the list as asked)
            _objects.AccessibleName = ObjectsName;
            if (asked) Reader?.Answer(ObjectsCount(things, page, filter), keep: false);
            // the parts below follow the object selected, unless the person is in them
            // (or the same one renamed: "Parts of" its new name)
            if (_objects.FocusedItem?.Tag is PersonOps.Thing sel && (sel.Id != _partsOf || _parts.AccessibleName != $"Parts of {sel.Name}")
                && !_parts.Focused && !_faces.Focused) _ = RefreshPartsAsync(sel);
            else if (things.Count == 0 && !_parts.Focused && !_faces.Focused) { _partsOf = UUID.Zero; Fill(_parts, new(), "No object selected."); Fill(_faces, new(), "No part selected."); }
        }
        finally
        {
            _objectsBusy = false;
            if (first) Looking(_objects, ObjectsName, false);
        }
        if (_objectsAgain is { } again) { _objectsAgain = null; await RefreshObjects(again); }
    }

    /// <summary>The list's items replaced, the one the person was on still focused (by key), or a line saying it's empty.
    /// <paramref name="under"/>: in under her, on the empty line in the list she's in, with no item made the focused one,
    /// so nothing's read until she moves (Down reaches the first): focusing one in the list she's in would read it.</summary>
    static void Fill(ListView list, List<(string Text, string Key, object Tag)> items, string empty, bool under = false)
    {
        // the same words as shown: only what the actions use is renewed (where people are), so a list beside the one
        // the person is in (the chat area's, while they type) isn't emptied and refilled every few seconds for nothing
        if (!list.Focused && items.Count > 0 && list.Items.Count == items.Count
            && items.Select((it, i) => list.Items[i].Name == it.Key && list.Items[i].Text == it.Text).All(x => x))
        {
            for (int i = 0; i < items.Count; i++) list.Items[i].Tag = items[i].Tag;
            // (left with none focused by people coming in under her, and she left without moving: the nearest, for her return)
            if (list.FocusedItem == null) { list.Items[0].Selected = true; ReaderList.MakeCurrent(list.Items[0]); }
            return;
        }
        // still empty: the line saying so left as it is (made again in the list she's in, it would be read again)
        if (items.Count == 0 && OnlyEmptyLine(list) && list.Items[0].Text == empty) return;
        var focusedKey = list.FocusedItem?.Name;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            if (items.Count == 0) list.Items.Add(new ListViewItem(empty) { Name = "" });
            foreach (var (text, key, tag) in items) list.Items.Add(new ListViewItem(text) { Name = key, Tag = tag });
            if (under && items.Count > 0) return;
            var keep = (focusedKey != null ? list.Items[focusedKey] : null) ?? list.Items[0];
            keep.Selected = true;
            ReaderList.MakeCurrent(keep);   // announced only in the list the person is in
        }
        finally { list.EndUpdate(); }
    }
}
