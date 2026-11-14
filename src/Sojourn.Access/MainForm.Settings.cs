using System.Text.Json.Nodes;
using Sojourn.Core;
using Sojourn.Session.Assistant;

namespace Sojourn.Access;

/// <summary>Settings (Alt+0): the ones that matter here, from SojournSettings (the same the assistant changes), in named
/// groups: one list showing a group at a time, its first row a heading saying which group and of how many, Page Down and
/// Page Up to the next and the one before, and the groups a Tab away as a list, as the build editor's and About Land's tabs
/// are. Each row is a list item "Nearby chat read out: on"; Enter, Space or Right moves it to its next value, Shift with them
/// or Left to the one before, said back (a sound's row plays it at its new level, so each can be learned; a volume given
/// as a number steps through a few levels).</summary>
public sealed partial class MainForm
{
    TabPage _settingsPage = null!;
    ListView _settingsList = null!;
    ComboBox _settingsGroup = null!;
    Dictionary<string, string> _settingsRows = new();   // each row's words by its setting, as last read
    List<string> _settingsUnplaced = new();

    /// <summary>Holds: what its heading says is in it, so a group's found without going through it. A row with no label has
    /// its words in another group: it belongs in both, as someone could look in either.</summary>
    sealed record SettingsGroup(string Name, string Holds, (string Name, string? Label)[] Rows);

    // each group's first letter is its own, so the groups' list goes to any by a letter; what's changed most in a group
    // comes first. Each row leads with what it's about, so first-letter navigation finds it and the useful word comes
    // first ("Sound for ..." on eleven rows, "Walk mode, ..." on twenty, hid both)
    static readonly SettingsGroup[] SettingsGroups =
    {
        new("Reading out", "what's said aloud, and what isn't", new (string, string?)[]
        {
            ("access_mute_all", "Mute all read-out (F6)"),
            ("announce_nearby", "Nearby chat read out"),
            ("announce_objects", "Objects' chat read out"),
            ("announce_ims", "IMs read out"),
            ("announce_group", "Group chat read out"),
            ("announce_notices", "Notices read out"),
            ("announce_replies", "The assistant's replies read out"),
            ("announce_music", "Music titles read out"),
            ("access_read_mine", "My own lines read back"),
            ("access_read_cut", "Lines cut off by my typing said again"),
            ("access_names", "People's names"),
            ("access_script_errors", "Script errors from my objects read out"),
            ("money_notices", "L$ received and spent, said"),
            ("radar_chat_range", null),
            ("radar_draw_distance", null),
            ("radar_region", null),
            ("access_voice_speakers", null),
            ("screen_reader", "Screen reader read-out"),
        }),
        new("Sounds", "volumes, the parcel's music, where sound comes out, and the sound for each kind of thing", new (string, string?)[]
        {
            ("volume", "Overall volume"),
            ("music_volume", "Music volume"),
            ("sounds_volume", "World's sounds, volume"),
            ("voice_volume", null),
            ("access_sounds_volume", "Sojourn Access's own sounds, volume"),
            ("music", "Parcel's music"),
            ("speakers", "Sound output: speakers or headphones (Control+Shift+O)"),
            ("earcon_im", "IM sound"),
            ("earcon_mention", "Mention sound (your name or a keyword)"),
            ("earcon_menu", "Menu sound (a script menu or question)"),
            ("earcon_offer", "Offer sound"),
            ("earcon_group", "Group chat sound"),
            ("earcon_nearby", "Nearby chat sound, from whoever spoke"),
            ("earcon_objects", "Objects' chat sound, from the object"),
            ("earcon_friends", "Friends online and offline sound"),
            ("earcon_radar", "Arrivals and departures sound, from the person"),
            ("earcon_notice", "Other notices sound"),
            ("earcon_sent", "Your own line sent, sound"),
            ("earcon_mic", "Mic opening and closing, sound"),
            ("earcon_deleted", "Deleting something, sound"),
        }),
        new("Walking", "walk mode's cues and their volumes, how far a turn goes, always running, and walking round people", new (string, string?)[]
        {
            ("walk_steps", "Footsteps (walk mode)"),
            ("walk_steps_volume", "Footsteps' volume"),
            ("walk_walls", "Bump warning (walk mode)"),
            ("walk_walls_volume", "Bump warning's volume"),
            ("walk_openings", "Openings and doors passed (walk mode)"),
            ("walk_openings_volume", "Openings' volume"),
            ("walk_edges", "Drops and water ahead (walk mode)"),
            ("walk_edges_volume", "Drops' volume"),
            ("walk_turns", "Turns: where you face after one (walk mode)"),
            ("walk_turns_volume", "Turns' volume"),
            ("walk_sonar", "Sonar sweep (walk mode, S)"),
            ("walk_sonar_volume", "Sonar's volume"),
            ("walk_beacon", "Beacon on where you're heading"),
            ("walk_beacon_volume", "Beacon's volume"),
            ("walk_beacon_duck", "Beacon: the world's sounds while it sounds"),
            ("walk_marks", "Marks: doors, seats and touchable things (walk mode, O)"),
            ("walk_marks_volume", "Marks' volume"),
            ("walk_route", "Route recording's points (walk mode, R)"),
            ("walk_route_volume", "Route points' volume"),
            ("walk_follow", "Following someone: their stops and turns"),
            ("walk_follow_volume", "Following's volume"),
            ("walk_distance", "Distance walked, said when you pause (walk mode)"),
            ("walk_distance_volume", "Distance walked's volume"),
            ("walk_turn_step", "Degrees a tap of Left or Right turns (walk mode)"),
            ("walk_turn_rate", "Degrees a second Left or Right held turns (walk mode)"),
            ("walk_teleport_fallback", "Teleport when a walk finds no way around"),
            // (Radegast's World > Always Run)
            ("walk_always_run", "Always run: walking, //go and following"),
            ("walk_round_people", "Walk round people standing in the way (//go, Walk to)"),
        }),
        new("Driving and trains", "your speed, what's ahead, rail switches and the track's end", new (string, string?)[]
        {
            ("walk_speed", "Speed while driving, said when it changes (V asks)"),
            ("walk_vehicle", "Obstacles ahead while driving"),
            ("walk_vehicle_volume", "Driving sounds' volume"),
            ("walk_switch", "Rail switches ahead while riding (walk mode, W)"),
            ("walk_switch_volume", "Rail switches' volume"),
            // (the blind tester's train ran off the end of its track, 2026-11-01)
            ("walk_track_end", "Track's end: stop the vehicle when it says the track ends"),
        }),
        new("People and offers", "how names are said, who can reach you, what's turned away or kept, auto responses, arrivals and departures", new (string, string?)[]
        {
            ("access_names", null),
            ("radar_chat_range", "Chat range arrivals and departures"),
            ("radar_draw_distance", "Draw distance arrivals and departures"),
            ("radar_region", "Region arrivals and departures"),
            ("radar_age_alert", "New accounts arriving, up to this many days old"),
            ("friends_only_ims", "Only friends and groups can IM or call you"),
            // (Radegast's auto-accept, which the blind tester was used to and missed, 2026-11-01)
            ("auto_accept_inventory", "Items given to you, kept without asking"),
            // (Radegast's, the blind tester asked for it, 2026-11-03)
            ("auto_grant_animation", "Animation permission, given without asking"),
            ("reject_teleports", "Teleport offers turned away"),
            ("@teleports", "Reply to teleport offers turned away"),
            ("reject_friendships", "Friendship offers turned away"),
            ("@friendships", "Reply to friendship offers turned away"),
            ("reject_group_invites", "Group invitations turned away"),
            ("reject_p2p_calls", "One-to-one voice calls turned away"),
            ("reject_group_calls", "Group voice calls turned away"),
            ("reject_conference_calls", "Conference voice calls turned away"),
            ("@dnd", "Auto response in Do not disturb"),
            ("away_after", "Away after this many minutes without you"),
            ("@away", "Auto response while away"),
        }),
        new("Chat", "mentions and keywords, translation, typing, times in the histories, and chat logs", new (string, string?)[]
        {
            ("mention_alerts", "Mentions of you, an alert"),
            ("keyword_alerts", "Keywords, an alert"),
            ("@keywords", "Keywords that count as a mention"),
            ("translate_chat", "Translate other people's lines"),
            ("translator", "Translator"),
            ("translate_to", "Translated into, a language code"),
            ("typing_indicator", "Others told when you're writing"),
            ("typing_animation", null),
            ("access_timestamps", "Times in the histories"),
            ("access_chat_logs", "Chat logs kept on this PC"),
            ("access_read_mine", null),
            ("access_read_cut", null),
        }),
        new("Voice", "voice chat, how you talk, the microphone, and who you hear", new (string, string?)[]
        {
            ("voice_chat", "Voice chat"),
            ("voice_mode", "Push to talk or open mic"),
            ("access_talk_key", "Talk key"),
            ("microphone", "Microphone for voice"),
            ("speakers", null),
            ("voice_volume", "Residents' voices, volume"),
            ("voice_hear_from", "Voice heard from"),
            ("access_voice_speakers", "Who starts speaking in voice, said"),
            ("earcon_mic", null),
            ("reject_p2p_calls", null),
            ("reject_group_calls", null),
            ("reject_conference_calls", null),
        }),
        new("Assistant", "its provider and key, web and programs, and the describing model", new (string, string?)[]
        {
            // (their words are ProviderItems')
            ("@provider", null),
            ("@key", null),
            ("@model", null),
            ("@data", null),
            ("@auto", null),
            ("access_describing", "Describing model (economy or best)"),
            ("announce_replies", null),
        }),
        new("Incognito", "whether it's on, what the assistant sees, and when it starts by itself", new (string, string?)[]
        {
            ("incognito", "Incognito (Control+Shift+I)"),
            ("incognito_sees", "Assistant sees during incognito"),
            ("incognito_while_im", "While you're in an IM, incognito"),
            ("incognito_here", "This parcel always incognito"),
        }),
        new("My avatar", "hover height, animations, auto-sit and RLV", new (string, string?)[]
        {
            ("hover_height", "Hover height, metres"),
            // (Radegast's AutoSit, and its issue #156's asks: a seat in several places, a key)
            ("access_autosit", "Auto-sit: sat back on your seat when you log in or teleport there (Control+Shift+S)"),
            ("access_autosit_seats", "Auto-sit seats"),
            ("ao", "Animation overrider"),
            ("ao_stands", "Animation overrider's stands"),
            ("typing_animation", "Typing animation on your avatar"),
            ("auto_grant_animation", null),
            ("rlv", "RLV, scripts controlling what you wear and do"),
        }),
        new("General", "region ratings, how far the people and objects nearby lists reach, the inventory's order, messages with the builders, and answers without a keyboard", new (string, string?)[]
        {
            ("maturity", "Highest rating of region you go into"),
            ("access_objects_range", "How far Objects nearby lists, metres"),
            ("access_people_range", "How far People nearby lists, metres"),
            // (Radegast's Sort by date)
            ("access_inventory_sort", "Inventory order in each folder: by name or newest first"),
            ("access_builders", "Messages with the builders (Control+Shift+B)"),
            // (the blind tester asked what "answers without a physical key: refused" meant, 2026-10-31: the row says it plainly)
            ("access_answers_without_key", "Answers from a braille display, dictation or on-screen keyboard"),
            ("access_view_rate", "3D view, frames a second (0: only when needed, for a slow PC)"),
        }),
        new("Everything", "every setting, A to Z", Array.Empty<(string, string?)>()),
    };

    static readonly SettingsGroup UnplacedGroup = SettingsGroups[^2];

    static readonly (string Name, string Label)[] Shown =
        SettingsGroups.SelectMany(g => g.Rows).Where(r => r.Label != null).Select(r => (r.Name, r.Label!)).ToArray();

    /// <summary>What isn't a row on purpose: what only shows on a screen, what has its own key, or a list kept by its own
    /// list. Anything registered and in neither table still gets a row, at the end of General and in Everything, by its
    /// setting's name, so a new setting can't end up reachable only by asking the assistant; the settings step fails until
    /// it's in one of them.</summary>
    static readonly HashSet<string> NotRows = new()
    {
        "access_im_sounds", "access_muted_sources", "access_view_size", "away", "do_not_disturb", "alert_sound", "ao_lock_folders",
        "ao_lockmeister", "ao_opencollar", "chat_bubbles", "crash_reports", "hover_tips", "hover_tip_delay", "lip_sync", "selection_beams",
        "stats_hardware_ids", "tag_friend_colour", "tag_group_titles", "tags_through_walls", "walk_track_end_phrases",
    };

    void BuildSettings()
    {
        _settingsPage = new TabPage("Settings") { Name = "settingsPage", AccessibleName = "Settings" };
        _settingsList = List("settings", $"{SettingsGroups[0].Name} settings");
        _settingsGroup = new ComboBox { Name = "settingsGroup", AccessibleName = "Group", DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        // (a closed list says only the item, so the item carries where it stands)
        _settingsGroup.Items.AddRange(SettingsGroups.Select((g, i) => (object)$"{g.Name}, {i + 1} of {SettingsGroups.Length}").ToArray());
        _settingsGroup.SelectedIndex = 0;
        _settingsGroup.SelectedIndexChanged += (_, _) => ShowSettingsGroup(atHeading: true);
        _settingsGroup.KeyDown += (_, e) =>
        {
            if (e.KeyData != Keys.Enter || _settingsGroup.DroppedDown) return;
            e.Handled = e.SuppressKeyPress = true;
            _settingsList.Focus();
        };
        _settingsList.KeyDown += (_, e) =>
        {
            if (e.KeyData is Keys.PageDown or Keys.PageUp)
            {
                e.Handled = e.SuppressKeyPress = true;
                NextSettingsGroup(e.KeyData == Keys.PageDown ? 1 : -1);
                return;
            }
            var item = _settingsList.SelectedItems.Count > 0 ? _settingsList.SelectedItems[0] : _settingsList.FocusedItem;
            if (item?.Name.StartsWith(HeadingRow) == true) { SettingsHeadingKey(e); return; }
            if (item?.Tag is string words && WordRows.Contains(words) && WordRowKey(words, e)) return;
            if (item?.Tag is string own && own.StartsWith('@') && ProviderKey(own, e)) return;
            if (item?.Tag is "access_autosit_seats" && AutoSitSeatsKey(e)) return;
            if (e.KeyCode is not (Keys.Enter or Keys.Space or Keys.Left or Keys.Right) || e.Control || e.Alt || item?.Tag is not string name) return;
            e.Handled = e.SuppressKeyPress = true;
            // incognito's off, and letting it see mid-spell, are the person's own key here, judged as it's handled
            if (IncognitoRow(name) || AnswersRow(name)) return;
            // a device row: Enter opens the list of devices (stepping onto the next one blind isn't choosing it)
            if (DeviceRow(name) && e.KeyCode == Keys.Enter && !e.Shift) { _ = DeviceListAsync(name, item); return; }
            if (DeviceRow(name)) { _ = NextDeviceAsync(name, e.Shift || e.KeyCode == Keys.Left ? -1 : 1); return; }
            _ = NextValueAsync(name, e.Shift || e.KeyCode == Keys.Left ? -1 : 1, typing: e.KeyCode is Keys.Enter or Keys.Space);
        };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(_settingsList, 0, 0);
        t.SetColumnSpan(_settingsList, 2);
        t.Controls.Add(new Label { Text = "Group", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        t.Controls.Add(_settingsGroup, 1, 1);
        _settingsList.TabIndex = 0; _settingsGroup.TabIndex = 1;
        _settingsPage.Controls.Add(t);
    }

    // --- the groups

    const string HeadingRow = "#";

    SettingsGroup ShownGroup => SettingsGroups[Math.Max(0, _settingsGroup.SelectedIndex)];
    string SettingsListName => $"{ShownGroup.Name} settings";
    string GroupWhere => $"{ShownGroup.Name}, group {Math.Max(0, _settingsGroup.SelectedIndex) + 1} of {SettingsGroups.Length}";

    /// <summary>The first group's heading says the keys too: it's what's heard on first coming here.</summary>
    string SettingsHeading() => $"{GroupWhere}: {ShownGroup.Holds}."
        + (ShownGroup == SettingsGroups[0] ? " Page Down: the next group. Tab: the list of groups." : "");

    IEnumerable<string> RowsOfGroup(SettingsGroup g)
    {
        if (g == SettingsGroups[^1]) return _settingsRows.OrderBy(r => r.Value, StringComparer.OrdinalIgnoreCase).Select(r => r.Key);
        var rows = g.Rows.Select(r => r.Name).Where(_settingsRows.ContainsKey);
        return g == UnplacedGroup ? rows.Concat(_settingsUnplaced) : rows;
    }

    /// <summary>The group chosen into the list, from what was last read: the row she's on kept, or at its heading.</summary>
    void ShowSettingsGroup(bool atHeading = false)
    {
        _settingsList.AccessibleName = SettingsListName;
        if (_settingsRows.Count == 0) { Fill(_settingsList, new(), "No settings (not logged in?)."); return; }
        var items = new List<(string, string, object)> { (SettingsHeading(), HeadingRow + ShownGroup.Name, null!) };
        items.AddRange(RowsOfGroup(ShownGroup).Select(name => (_settingsRows[name], name, (object)name)));
        if (atHeading) UiaRelease.Clear(_settingsList);   // (Fill keeps the row she's on, which can be in the next group too)
        Fill(_settingsList, items, "");
    }

    /// <summary>Page Down, Page Up: the next group or the one before, round the ends, at its heading. The focus stays in the
    /// list, on what's often the same position in it, and NVDA drops a focus event for the position it's on (its
    /// isDuplicateIAccessibleEvent), so the heading is said as well: if the row's read too it's the same words.</summary>
    void NextSettingsGroup(int dir)
    {
        _settingsGroup.SelectedIndex = (_settingsGroup.SelectedIndex + dir + SettingsGroups.Length) % SettingsGroups.Length;
        Reader?.Answer(SettingsHeading(), keep: false);
    }

    /// <summary>On a heading nothing's there to change, so Enter and Space go to the group's first setting and Left and Right
    /// to the other groups (Page Down and Page Up take Fn and arrows on many laptops).</summary>
    void SettingsHeadingKey(KeyEventArgs e)
    {
        if (e.Control || e.Alt || e.KeyCode is not (Keys.Enter or Keys.Space or Keys.Left or Keys.Right)) return;
        e.Handled = e.SuppressKeyPress = true;
        if (e.KeyCode is Keys.Left or Keys.Right) { NextSettingsGroup(e.KeyCode == Keys.Left ? -1 : 1); return; }
        if (_settingsList.Items.Count < 2) return;
        _settingsList.Items[1].Selected = true;
        _settingsList.Items[1].Focused = true;
    }

    /// <summary>Alt+0: Settings as she left it, the same group and row; pressed while there, where that is said (an area's
    /// key reads it again).</summary>
    void GoSettings()
    {
        bool there = _areas.SelectedTab == _settingsPage;
        GoArea(_settingsPage);
        if (!there) return;
        var row = _settingsList.FocusedItem;
        Reader?.Answer(row == null || row.Name.StartsWith(HeadingRow) ? SettingsHeading() : $"{GroupWhere}. {row.Text}.", keep: false);
    }

    /// <summary>Answers without a physical key: back to refuse by anything, to hello (a safeguard lowered) only by the
    /// person's own key or, for someone with no physical keyboard, Windows Hello; SojournSettings refuses that to callers.</summary>
    bool AnswersRow(string name)
    {
        if (name != "access_answers_without_key") return false;
        if (_set.ConfirmByHello) { Set("refuse"); return true; }
        if (RealInput.Confirms) Set("hello");
        else Hello.Instead(true, Handle, "let answers without a physical key go through Windows Hello", RealInput.Why, t => Reader?.Answer(t, keep: false), () => Set("hello"));
        return true;

        void Set(string value)
        {
            _set.SetAnswersWithoutKey(value);
            _ = RefreshSettings();
            var label = Shown.First(s => s.Name == name).Label;
            Reader?.Answer($"{label}: {ValueWords(name, value)}.");
        }
    }

    // SojournSettings' get and set run their changes on the main thread (Host.Tick drains them), so they're called off it
    // (Task.Run carries the person's input along: a set runs as theirs only from their key)
    Task<JsonObject> SettingsOp(JsonObject args, string op) =>
        Task.Run(() => op == "get" ? _ops.ReadAsync("SojournSettings", op, args) : _ops.DoAsync("SojournSettings", op, args));

    async Task RefreshSettings()
    {
        bool first = _settingsList.Items.Count == 0;
        if (first) Looking(_settingsList, SettingsListName, true);
        try
        {
            var all = (await SettingsOp(new JsonObject(), "get"))["settings"] as JsonObject ?? new();
            var words = await WordRowValuesAsync();
            var rows = new Dictionary<string, string>();
            foreach (var (name, label) in Shown)
                if (words.TryGetValue(name, out var said)) rows[name] = $"{label}: {said}";
                else if (all[name] is JsonObject s) rows[name] = $"{label}: {ValueWords(name, Value(s["value"]))}";
            var named = Shown.Select(r => r.Name).ToHashSet();
            var unplaced = new List<string>();
            foreach (var (name, node) in all.Where(kv => !named.Contains(kv.Key) && !NotRows.Contains(kv.Key)).OrderBy(kv => kv.Key))
                if (node is JsonObject s) { rows[name] = $"{name.Replace('_', ' ')}: {ValueWords(name, Value(s["value"]))}"; unplaced.Add(name); }
            if (rows.Count > 0)
                foreach (var (text, key, _) in ProviderItems()) rows[key] = text;
            (_settingsRows, _settingsUnplaced) = (rows, unplaced);
            ShowSettingsGroup();
        }
        finally { if (first) Looking(_settingsList, SettingsListName, false); }
    }

    // --- whose models the assistant runs on (docs/assistant.md > Switching it on): the person's own rows here, never the
    // agent API's. Its key is typed or pasted into a password box, kept encrypted for this Windows account
    // (ProviderKeys), and never read back or said.

    // a packaged build offers no Claude account (ProviderKeys.ClaudeOffered): its "claude" is none chosen yet
    static string ProviderLabel(string kind) => kind switch
    {
        "claude" => Sojourn.Session.Assistant.ProviderKeys.ClaudeOffered ? "your Claude account" : "none chosen yet",
        "anthropic" => "your Anthropic key",
        "openrouter" => "your OpenRouter key",
        _ => "Studio Quire, with a token from the studio",
    };
    bool _forgetKeyArmed;

    List<(string, string, object)> ProviderItems()
    {
        if (Host is not { } h || !AccessOptions.AssistantInBuild) return new();
        var st = h.Settings;
        var kind = st.AssistantProvider;
        var status = h.Assistant.ProviderStatus is { } p && p["kind"]?.GetValue<string>() == kind ? p["short"]?.GetValue<string>() : null;
        var items = new List<(string, string, object)>
        {
            ($"Provider for the assistant: {ProviderLabel(kind)}"
             + (status != null && Sojourn.Session.Control.VrSettings.OfferedProviders.Contains(kind) ? $", {status}" : ""), "@provider", "@provider"),
        };
        // the key's row with a Claude account (or none chosen) too: a key or token put in there switches to its provider
        var noun = kind == "relay" ? "Token" : "Key";
        items.Add((kind == "claude" ? (Sojourn.Session.Assistant.ProviderKeys.ClaudeOffered
                ? "Key or token for the assistant: none needed with a Claude account (Enter to put one in)"
                : "Key or token for the assistant: none (Enter to put it in)")
            : $"{noun} for the assistant: {(Sojourn.Session.Assistant.ProviderKeys.Has(kind) ? "kept (Enter to put in another, Delete to forget it)" : "none (Enter to put it in)")}",
            "@key", "@key"));
        if (kind == "openrouter")
        {
            items.Add(($"Model on OpenRouter: {ModelName(st.OpenRouterModel)} (Enter for the next, Control+Enter to type one)", "@model", "@model"));
            items.Add(($"No data collection on OpenRouter: {(st.OpenRouterDenyDataCollection ? "on, only providers that don't store or train on what's sent" : "off, any provider")}",
                "@data", "@data"));
        }
        items.Add(($"Web and programs for the assistant: {(st.AssistantAutoMode ? "on" : "off, it can't search the web or run programs on this PC")}", "@auto", "@auto"));
        return items;
    }

    /// <summary>OpenRouter's model as said: Claude's three with their tags, or a model's name and its tag
    /// (Sojourn.Session's OpenRouterCatalog), or an id typed that OpenRouter doesn't list, as it is.</summary>
    static string ModelName(string id) =>
        id == OpenRouterCatalog.Claude ? $"Claude Haiku, Sonnet and Opus ({OpenRouterCatalog.ClaudeTagLine.Replace(" ·", ",")})"
        : OpenRouterCatalog.NameOf(id) is { } name ? $"{name}, {id}" : id;

    /// <summary>A key on one of the provider rows: true if it was theirs.</summary>
    bool ProviderKey(string row, KeyEventArgs e)
    {
        if (Host is not { } h) return false;
        var st = h.Settings;
        var kind = st.AssistantProvider;
        // Left and Right step the rows that step (not the key's, which asks for one)
        bool enter = (e.KeyCode is Keys.Enter or Keys.Space || e.KeyCode is Keys.Left or Keys.Right && row != "@key" && !e.Control) && !e.Alt;
        bool back = e.Shift || e.KeyCode == Keys.Left;
        bool delete = e.KeyCode == Keys.Delete && row == "@key";
        if (!enter && !delete) return false;
        e.Handled = e.SuppressKeyPress = true;
        if (!delete) _forgetKeyArmed = false;
        string said;
        if (row == "@provider")
        {
            var all = Sojourn.Session.Control.VrSettings.OfferedProviders;
            int at = Array.IndexOf(all, kind);
            st.AssistantProvider = at < 0 ? all[back ? ^1 : 0] : all[(at + (back ? -1 : 1) + all.Length) % all.Length];
            said = $"Provider for the assistant: {ProviderLabel(st.AssistantProvider)}."
                   + (st.AssistantProvider != "claude" && !Sojourn.Session.Assistant.ProviderKeys.Has(st.AssistantProvider)
                       ? $" No {(st.AssistantProvider == "relay" ? "token" : "key")} yet: the next row puts it in." : "");
        }
        else if (delete)
        {
            if (!Sojourn.Session.Assistant.ProviderKeys.Has(kind)) return true;
            if (!_forgetKeyArmed) { _forgetKeyArmed = true; Reader?.Answer("Press Delete again to forget the key.", keep: false); return true; }
            _forgetKeyArmed = false;
            Sojourn.Session.Assistant.ProviderKeys.Forget(kind);
            said = $"{Sojourn.Session.Assistant.ProviderKeys.Thing(kind)} forgotten.";
        }
        else if (row == "@key")
        {
            var text = Dialogs.Ask(this, "Assistant key or token", "Studio Quire token, or Anthropic or OpenRouter key (paste with Control+V)", secret: true);
            if (text == null) { Reader?.Answer("Nothing kept.", keep: false); return true; }
            string? kept;
            try { kept = Sojourn.Session.Assistant.ProviderKeys.Keep(text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Reader?.Answer("Couldn't keep it."); return true; }
            if (kept == null) { Reader?.Answer("That isn't a Studio Quire token or an Anthropic or OpenRouter key. Nothing kept."); return true; }
            st.AssistantProvider = kept;
            said = $"{Sojourn.Session.Assistant.ProviderKeys.Thing(kept)} kept; the assistant uses it now.";
        }
        else if (row == "@data")
        {
            st.OpenRouterDenyDataCollection = !st.OpenRouterDenyDataCollection;
            said = st.OpenRouterDenyDataCollection ? "No data collection: on." : "No data collection: off; any OpenRouter provider may answer.";
        }
        else if (row == "@auto")
        {
            st.AssistantAutoMode = !st.AssistantAutoMode;
            said = st.AssistantAutoMode ? "Web and programs on: the assistant can search the web and run programs on this PC." : "Web and programs off: the assistant can't search the web or run programs on this PC.";
        }
        else
        {
            OpenRouterCatalog.Refresh();
            var models = new[] { OpenRouterCatalog.Claude }.Concat(OpenRouterCatalog.Quick.Select(m => m.Id)).ToArray();
            if (e.Control)
            {
                if (Dialogs.Ask(this, "OpenRouter model", "OpenRouter model id", st.OpenRouterModel) is not { } id) return true;
                st.OpenRouterModel = id.Trim() is { Length: > 0 } typed ? typed : OpenRouterCatalog.Claude;
            }
            else
            {
                int at = Array.IndexOf(models, st.OpenRouterModel);
                st.OpenRouterModel = models[((at < 0 ? 0 : at) + (back ? -1 : 1) + models.Length) % models.Length];
            }
            said = $"Model on OpenRouter: {ModelName(st.OpenRouterModel)}.";
        }
        h.SaveSettings();
        h.Assistant.PrefsChanged();
        _ = RefreshSettings();
        Reader?.Answer(said);
        return true;
    }

    static string Value(JsonNode? v) => v switch
    {
        JsonArray list => list.Count == 0 ? "none" : string.Join(", ", list.Select(x => x?.ToString())),
        JsonValue b when b.TryGetValue<bool>(out var on) => on ? "on" : "off",
        null => "none",
        _ => v.ToString(),
    };

    /// <summary>A value as its row says it: the setting's own word, or what it means where the word alone doesn't say
    /// (a walk cue's "both" is a sound and a line; hello is Windows Hello; no underscores read out).</summary>
    static string ValueWords(string name, string value) => (name, value) switch
    {
        (_, "both") when name.StartsWith("walk_") => "sound and text",
        ("access_answers_without_key", "refuse") => "refused. Confirmations, menus and replies take only a physical keyboard's "
            + "keys, since a program can send keys too; leave it if you use a keyboard",
        ("access_answers_without_key", "hello") => "through Windows Hello. Each such answer is asked again by face, fingerprint "
            + "or PIN; a physical keyboard's keys answer as before",
        ("access_talk_key", "f4" or "f12") => value.ToUpperInvariant(),
        ("auto_accept_inventory", "off") => "off, each offer waits in Notices",
        ("auto_accept_inventory", "viewer") => "notecards, textures and landmarks only",
        ("auto_accept_inventory", "all") => "everything, a notice saying what came",
        ("auto_grant_animation", "on") => "on: a script asking only to animate you is allowed, and said",
        ("auto_grant_animation", "off") => "off, each request waits in Notices",
        ("speakers" or "microphone", _) => DeviceWords(name, value),
        // (a display name in fancy Unicode letters or symbols reads as nothing or nonsense: gap 12)
        ("access_names", "plain") => "display name in plain letters",
        ("access_names", "both") => "display name and username",
        ("access_names", "written") => "display name as written",
        ("access_names", "username") => "username",
        _ => value.Replace('_', ' '),
    };

    /// <summary>The setting's next value (dir 1: Enter or Space) or the one before (-1: with Shift), round the ends. One
    /// with no steps (a number in a range, a word) is typed: Enter or Space asks for it.</summary>
    async Task NextValueAsync(string name, int dir = 1, bool typing = false)
    {
        var one = (await SettingsOp(new JsonObject { ["name"] = name }, "get"))["settings"]?[name] as JsonObject;
        if (one == null) return;
        var now = Value(one["value"]);
        var values = (one.Str("values") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // a volume as a number (0-2: world audio's): a few steps, quiet ones finer (the music under the screen reader)
        // (as its text: the session's settings give volumes as floats, which TryGetValue<double> doesn't read, so these
        // rows only ever said "changed by asking the assistant")
        double was = 0;
        bool isNumber = one["value"] is JsonValue num && double.TryParse(num.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out was) && (one.Str("values") ?? "") == "0-2";
        if (isNumber) values = new[] { "0", "0.15", "0.25", "0.5", "0.75", "1", "1.5", "2" };
        if (values.Length < 2)
        {
            if (typing) await TypedValueAsync(name, one, now);
            else Reader?.Answer($"{ValueWords(name, now)}. Enter types another.", keep: false);
            return;
        }
        string next;
        if (isNumber)
        {
            // the next step above (or below) the value as it is, which needn't be a step
            double V(string x) => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture);
            int at = dir > 0 ? Array.FindIndex(values, x => V(x) > was + 0.001) : Array.FindLastIndex(values, x => V(x) < was - 0.001);
            next = values[at >= 0 ? at : dir > 0 ? 0 : values.Length - 1];
        }
        else
        {
            int at = Array.IndexOf(values, now);
            next = values[at < 0 ? (dir > 0 ? 0 : values.Length - 1) : (at + dir + values.Length) % values.Length];
        }
        // a switch takes true or false; a choice (screen_reader: auto, on, off) takes its word; a number its value
        bool isSwitch = one["value"] is JsonValue cur && cur.TryGetValue<bool>(out _);
        // (choices that are numbers, away_after's minutes, go as numbers)
        bool numeric = one["value"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number;
        JsonNode value = isSwitch ? JsonValue.Create(next == "on")
            : isNumber || numeric ? JsonValue.Create(double.Parse(next, System.Globalization.CultureInfo.InvariantCulture)) : JsonValue.Create(next);
        var r = await SettingsOp(new JsonObject { [name] = value }, "set");
        if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Not changed: {e}"); return; }
        await RefreshSettings();
        var label = Shown.FirstOrDefault(s => s.Name == name).Label ?? name;
        Reader?.Answer($"{label}: {ValueWords(name, next)}.");
        _earcons?.Preview(name);
    }

    /// <summary>A setting typed: its range is in the question; a refusal is said in the setting's own words.</summary>
    async Task TypedValueAsync(string name, JsonObject one, string now)
    {
        var label = Shown.FirstOrDefault(s => s.Name == name).Label ?? name.Replace('_', ' ');
        var text = Dialogs.Ask(this, label, $"{label} ({one.Str("values")})", now == "none" ? "" : now, allowEmpty: true);
        if (text == null) { Reader?.Answer("Not changed.", keep: false); return; }
        JsonNode value = one["value"] is JsonArray
            ? new JsonArray(text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(w => (JsonNode)w!).ToArray())
            : double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? JsonValue.Create(n) : JsonValue.Create(text);
        var r = await SettingsOp(new JsonObject { [name] = value }, "set");
        if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Not changed: {e}"); return; }
        await RefreshSettings();
        Reader?.Answer($"{label}: {(text.Length == 0 ? "none" : ValueWords(name, text))}.");
    }

    // --- rows of words: what she'd want said for her, and her keywords, kept by their own ops

    static readonly HashSet<string> WordRows = new() { "@keywords", "@dnd", "@away", "@teleports", "@friendships" };

    async Task<Dictionary<string, string>> WordRowValuesAsync()
    {
        var keywords = await _ops.ReadAsync("SojournSocial", "status");
        var status = await _ops.ReadAsync("SojournPeople", "status");
        var rejects = await _ops.ReadAsync("SojournPeople", "rejects");
        var words = (keywords["keywords"]?["words"] as JsonArray)?.Select(w => w?.ToString() ?? "").ToList() ?? new();
        return new()
        {
            ["@keywords"] = words.Count == 0 ? "none" : string.Join(", ", words),
            ["@dnd"] = status.Str("dnd_response") ?? "",
            ["@away"] = status["away_response_on"]?.GetValue<bool>() == true ? status.Str("away_response") ?? "" : "off",
            ["@teleports"] = rejects.Str("teleports_response") ?? "",
            ["@friendships"] = rejects.Str("friendships_response") ?? "",
            ["access_autosit_seats"] = AutoSitSeatsRow(),
        };
    }

    bool WordRowKey(string row, KeyEventArgs e)
    {
        if (e.KeyCode is not (Keys.Enter or Keys.Space) || e.Control || e.Alt) return false;
        e.Handled = e.SuppressKeyPress = true;
        _ = AskWordRowAsync(row);
        return true;
    }

    async Task AskWordRowAsync(string row)
    {
        var label = Shown.First(s => s.Name == row).Label;
        var now = (await WordRowValuesAsync())[row];
        var question = row == "@keywords" ? "Words or phrases, commas between them" : row == "@away" ? "What it says; empty turns it off" : "What it says; empty goes back to the standard sentence";
        var text = Dialogs.Ask(this, label, question, now is "none" or "off" ? "" : now, allowEmpty: true);
        if (text == null) { Reader?.Answer("Not changed.", keep: false); return; }
        if (row == "@keywords")
        {
            var words = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var k = await _ops.DoAsync("SojournSocial", "keywords", new JsonObject { ["words"] = new JsonArray(words.Select(w => (JsonNode)w!).ToArray()) });
            if (PersonOps.Error(k) is { } ke) { Reader?.Answer($"Not changed: {ke}"); return; }
            await RefreshSettings();
            Reader?.Answer(words.Length == 0 ? "No keywords now." : $"Keywords: {string.Join(", ", words)}. The Keywords, an alert row turns their alerts on and off.");
            return;
        }
        var args = new JsonObject { ["kind"] = row[1..], ["text"] = text };
        if (row == "@away") args["on"] = text.Length > 0;
        // her own words sent for her: the exact text confirmed, as the op asks
        if (await ConfirmedAsync("SojournPeople", "setResponse", args) is not { } r) return;
        if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Not changed: {e}"); return; }
        await RefreshSettings();
        Reader?.Answer($"{label}: {(await WordRowValuesAsync())[row].TrimEnd('.')}.");
    }

    /// <summary>The grid's own preference; the VR client registers its copy among its own rows.</summary>
    void AddMaturitySetting(Sojourn.Session.Control.SettingsApi api)
    {
        var s = Host!.Session;
        api.Add("maturity", new Sojourn.Session.Control.SettingsApi.Setting("you", "the highest rating of region you go into, up to what the account allows (beyond it is refused with why); the grid keeps it with the account",
            string.Join(", ", Sojourn.Core.Agents.AgentSession.Maturities), () => JsonValue.Create(s.Maturity), _ => "maturity: changed through the grid")
            { SetAsync = (v, ct) => s.SetMaturityAsync(v.ToString().Trim().ToLowerInvariant(), ct) });
    }
}
