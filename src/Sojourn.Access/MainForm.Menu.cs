using System.Text.Json.Nodes;
using Sojourn.Core;

namespace Sojourn.Access;

/// <summary>
/// The menu bar (Alt or F10, then the arrows; Alt+W, V, R, H open its menus): for finding out what exists, each item
/// showing its key where it has one (ShortcutKeyDisplayString, which UI Automation gives screen readers as the item's
/// accelerator key). Nothing here is a new way of doing things: every item runs the same keys, `//` commands and ops as
/// typing would, so what asks for a confirmation still does. The keys themselves stay in ProcessCmdKey, never the
/// menu's (a menu item's ShortcutKeys would take them from every control). The menus' letters stay clear of the areas'
/// own (inventory's Search and Show, people's Nearby, Friends and Groups, places' Landmarks, History, Go to region and
/// Regions around, search's For and Look for).
/// </summary>
public sealed partial class MainForm
{
    readonly MenuStrip _menu = new() { Name = "menu", AccessibleName = "Menu bar", TabStop = false };
    ToolStripMenuItem _sitItem = null!, _flyItem = null!, _awayItem = null!, _dndItem = null!, _incognitoItem = null!,
        _micItem = null!, _muteItem = null!, _timesItem = null!, _recentItem = null!;

    void BuildMenu()
    {
        var world = Top("&World",
            Item("Teleport &home", "Control+Shift+H", () => _ = HomeAsync()),
            Item("Teleport to the SLurl on the &clipboard", "Control+G", TeleportClipboard),
            Item("Set home h&ere", null, () => _ = SetHomeAsync()),
            Item("&Landmark here", null, () => _ = LandmarkHereAsync()),
            new ToolStripSeparator(),
            _sitItem = Item("&Sit on the ground", null, () => _ = SitOrStandAsync()),
            _flyItem = Item("&Fly", null, ToggleFly),
            Later("Wal&k mode", "F3", ToggleWalk),
            _gesturesItem = new ToolStripMenuItem("&Gestures") { Name = "menuGestures", ShortcutKeyDisplayString = "Control+Shift+G" },
            new ToolStripSeparator(),
            _awayItem = Item("&Away", null, () => _ = PresenceAsync("away")),
            _dndItem = Item("&Do not disturb", null, () => _ = PresenceAsync("do_not_disturb")),
            _incognitoItem = Item("&Incognito", "Control+Shift+I", ToggleIncognito),
            new ToolStripSeparator(),
            Item("&Region info", "Control+Shift+1", () => _ = _cmd.RunAsync("//siminfo")),
            Item("&Parcels in this region", null, () => _ = _cmd.RunAsync("//parcelinfo")),
            Item("S&tatus", null, () => _ = _cmd.RunAsync("//status")),
            Item("What you're &wearing", "Control+Shift+W", () => _ = OutfitListAsync()),
            Item("Descri&be where you are", null, () => _ = _cmd.RunAsync("//describe")),
            Item("Describe &me", "Control+Shift+D", () => DescribeKey(me: true)),
            new ToolStripSeparator(),
            Item("Who's in &voice", "Control+Shift+V", SayVoice),
            _micItem = Item("&Open mic", "Shift+F4", ToggleVoiceMode),
            Item("Sound o&ut of the next device", "Control+Shift+O", () => _ = NextOutputAsync()));
        world.DropDownOpening += (_, _) => WorldShown();
        _gesturesItem.DropDownItems.Add(new ToolStripMenuItem("(none yet)"));   // a submenu needs an item to open
        _gesturesItem.DropDownOpening += (_, _) => FillGestures(_gesturesItem.DropDownItems);

        var view = Top("&View",
            Later("Nearby &chat", "Alt+1", () => GoArea(_chatPage)),
            Later("&Friends", "Alt+2", GoFriends),
            Later("&People", "Alt+3", () => GoArea(_peoplePage)),
            Later("&Inventory", "Alt+4", () => GoInventory(second: false)),
            Later("Inventory, s&econd pane", "Alt+Shift+4", () => GoInventory(second: true)),
            Later("&Search", "Alt+5", () => GoArea(_searchPage)),
            Later("P&laces", "Alt+6", () => GoArea(_placesPage)),
            Later("O&bjects", "Alt+7", () => GoArea(_objectsPage)),
            Later("&Notices", "Alt+8", () => GoArea(_noticesPage)),
            Later("The &assistant", "Alt+9", () => GoArea(_assistantPage)),
            Later("Se&ttings", "Alt+0", () => GoArea(_settingsPage)),
            new ToolStripSeparator(),
            Later("The ne&west notice", "Control+N", NewestNotice),
            Later("C&onversations", null, () => GoArea(_conversationsPage)),
            Later("Ne&xt conversation", "Control+Tab", () => Ring(+1)),
            Later("Previous con&versation", "Control+Shift+Tab", () => Ring(-1)),
            Later("Close this conversation", "Control+W", CloseConversation));

        _recentItem = new ToolStripMenuItem("Recent messa&ges") { Name = "menuRecent" };
        _recentItem.DropDownItems.Add(new ToolStripMenuItem("(none yet)"));   // a submenu needs an item to open
        _recentItem.DropDownOpening += (_, _) => FillRecent();
        var reading = Top("&Read-out",
            _muteItem = Item("&Mute all read-out", "F6", MuteAll),
            Item("Mute &this conversation", "Shift+F6", MuteSource),
            _timesItem = Item("T&imes in the histories", "Control+Shift+T", ToggleTimestamps),
            Item("&Arrivals and departures", "Control+Shift+A", () => _ = ToggleArrivalsAsync()),
            new ToolStripSeparator(),
            _recentItem,
            Item("&Back through what was read", "Control+[", () => Reader?.Older()),
            Item("&Forward", "Control+]", () => Reader?.Newer()),
            Item("The &oldest", "Control+Shift+[", () => Reader?.Oldest()),
            Item("The &newest", "Control+R", () => Reader?.Repeat()),
            Item("Mar&k this line", "Control+Shift+M", () => { if (Reader is { } r) r.Answer(r.Mark(), keep: false); }),
            Item("&Copy from the mark", "Control+Shift+C", CopyFromMark),
            Item("Copy the assistant's last ans&wer", "Control+Shift+L", CopyLastAnswer));
        reading.DropDownOpening += (_, _) =>
        {
            _muteItem.Checked = _set.MuteAll;
            _timesItem.Checked = _set.Timestamps;
        };

        Top("&Help",
            Later("&Keys", "F1", () => ShowKeys(walk: false)),
            Item("&Slash commands", "//help", () => _ = _cmd.RunAsync("//help")),
            Later("Send a &log to Studio Quire", "//sendlog", SendLog),
            Item("&About Sojourn Access", "//about", () => _ = _cmd.RunAsync("//about")));

        MainMenuStrip = _menu;
    }

    ToolStripMenuItem Top(string text, params ToolStripItem[] items)
    {
        var top = new ToolStripMenuItem(text) { Name = "menu" + text.Replace("&", "").Replace("-", "") };
        top.DropDownItems.AddRange(items);
        _menu.Items.Add(top);
        return top;
    }

    /// <summary>An item run as it's pressed, so the press is the person's own for what it does (incognito off, an
    /// open mic, an op without the assistant's confirmations).</summary>
    static ToolStripMenuItem Item(string text, string? key, Action act) =>
        new(text, null, (_, _) => act()) { ShortcutKeyDisplayString = key };

    /// <summary>An item that moves the focus: run once the menu has closed and put the focus back where it was (it would
    /// undo the move), as our own move for the person.</summary>
    ToolStripMenuItem Later(string text, string? key, Action act) =>
        Item(text, key, () => BeginInvoke(() => RealInput.Ours(act)));

    /// <summary>Each item's key shown on a list's own menu (by its text), as the menu bar's.</summary>
    static void KeysOn(ToolStrip menu, params (string Text, string Key)[] keys)
    {
        foreach (var i in menu.Items.OfType<ToolStripMenuItem>())
            if (keys.FirstOrDefault(k => k.Text == i.Text).Key is { } key) i.ShortcutKeyDisplayString = key;
    }

    /// <summary>The World menu as things are now: sitting or standing, flying, away, busy, incognito, the mic.</summary>
    void WorldShown()
    {
        var c = _ops?.Client;
        bool sitting = c?.Self.SittingOn is > 0;
        _sitItem.Text = sitting ? "&Stand up" : "&Sit on the ground";
        _flyItem.Text = c?.Self.Movement.Fly == true ? "Stop &flying" : "&Fly";
        _flyItem.Enabled = !sitting;
        _awayItem.Checked = Host?.Session.Presence.Away == true;
        _dndItem.Checked = Host?.Session.Presence.DoNotDisturb == true;
        _incognitoItem.Checked = Host?.Session.Private.On == true;
        _micItem.Text = Voice?.Mode == Sojourn.Session.Voice.VoiceMode.OpenMic ? $"Push to talk (hold {KeyName}) instead of an &open mic" : $"&Open mic instead of push to talk (hold {KeyName})";
    }

    /// <summary>Recent messages: the last nine that arrived, newest first, each with its Control+number.</summary>
    void FillRecent()
    {
        _recentItem.DropDownItems.Clear();
        var lines = Reader?.RecentArrivals(9) ?? new();
        if (lines.Count == 0) { _recentItem.DropDownItems.Add(new ToolStripMenuItem("Nothing yet") { Enabled = false }); return; }
        for (int i = 0; i < lines.Count; i++)
        {
            int n = i + 1;
            var text = lines[i].Length > 90 ? lines[i][..87] + "..." : lines[i];
            _recentItem.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => Reader?.ArrivedAgo(n))
            {
                ShortcutKeyDisplayString = $"Control+{n}", Name = $"menuRecent{n}",
            });
        }
    }

    // --- what the World menu does that had no key -------------------------------------------------------------------

    async Task SetHomeAsync()
    {
        var r = await _ops.DoAsync("TesselAgent", "setHome");
        Answer(PersonOps.Error(r) is { } e ? $"Home not set: {e}." : r.Str("message") is { Length: > 0 } m ? m : "Home set here.");
    }

    async Task LandmarkHereAsync()
    {
        var r = await _ops.DoAsync("TesselInventory", "createLandmark");
        if (PersonOps.Error(r) is { } e) { Answer($"No landmark made: {e}."); return; }
        Answer($"Landmark {r.Str("name") ?? "made"}, in Landmarks.");
        RefreshInventory(Store?.RootFolder?.UUID ?? default);
    }

    Task SitOrStandAsync() => _cmd.RunAsync(_ops.Client?.Self.SittingOn is > 0 ? "//stand" : "//sit ground");

    void ToggleFly()
    {
        if (_walk == null) { Answer("Not in a region yet."); return; }
        _walk.ToggleFly();
    }

    /// <summary>Away or Do not disturb, on or off, through SojournSettings (the session's own switches).</summary>
    async Task PresenceAsync(string name)
    {
        if (Host?.Session.Presence is not { } p) return;
        bool on = name == "away" ? !p.Away : !p.DoNotDisturb;
        var r = await SettingsOp(new JsonObject { [name] = on }, "set");
        if (PersonOps.Error(r) is { } e) { Answer($"Not changed: {e}"); return; }
        Answer(name == "away" ? (on ? "Away." : "Not away.") : on ? "Do not disturb: offers are turned away and others see you busy." : "Do not disturb off.");
        if (_areas.SelectedTab == _settingsPage) _ = RefreshSettings();
    }

    /// <summary>Alt+4 (second: false) and Alt+Shift+4: to the inventory's first pane or its second (opened the first
    /// time); pressed in the pane the focus is already in, it's said again, as every Alt+number reads its area again.</summary>
    void GoInventory(bool second)
    {
        var pane = second ? _pane2 : _pane1;
        if (InInventory && pane.Tree.Focused)
        {
            var title = second ? "Inventory, second pane" : "Inventory";
            Reader?.Answer(pane.Tree.SelectedNode is { } n ? $"{title}: {n.Text}." : $"{title}.", keep: false);
            return;
        }
        if (!second) { _pane = _pane1; GoArea(_inventoryPage); return; }
        if (_areas.SelectedTab != _inventoryPage) { _areas.SelectedTab = _inventoryPage; FocusChanged(); }
        OpenSecondPane();
    }
}
