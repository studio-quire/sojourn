using System.Text.Json.Nodes;
using Sojourn.Core;

namespace Sojourn.Access;

/// <summary>Settings (Alt+0): the ones that matter here, from SojournSettings (the same the assistant changes), each a
/// list item "Read out nearby chat: on"; Enter or Space moves it to its next value, said back (a sound's row plays it
/// at its new level, so each can be learned; a volume given as a number steps through a few levels).</summary>
public sealed partial class MainForm
{
    TabPage _settingsPage = null!;
    ListView _settingsList = null!;

    static readonly (string Name, string Label)[] Shown =
    {
        ("screen_reader", "Read out through the screen reader"),
        ("announce_nearby", "Read out nearby chat"),
        ("announce_objects", "Read out what objects say"),
        ("announce_ims", "Read out IMs"),
        ("announce_group", "Read out group chat"),
        ("announce_notices", "Read out notices"),
        ("announce_replies", "Read out the assistant"),
        ("announce_music", "Read out music titles"),
        ("access_mute_all", "Everything muted (F6)"),
        ("access_timestamps", "Times in the histories"),
        ("access_answers_without_key", "Answers without a physical key"),
        ("earcon_im", "Sound for an IM"),
        ("earcon_mention", "Sound for a mention of you or a keyword"),
        ("earcon_menu", "Sound for a script menu or question"),
        ("earcon_offer", "Sound for an offer"),
        ("earcon_group", "Sound for group chat"),
        ("earcon_nearby", "Sound for nearby chat, from the speaker"),
        ("earcon_objects", "Sound for objects' chat, from the object"),
        ("earcon_friends", "Sound for friends online and offline"),
        ("earcon_radar", "Sound for arrivals and departures, from the person"),
        ("earcon_notice", "Sound for other notices"),
        ("earcon_sent", "Sound for your own line sent"),
        ("access_sounds_volume", "Volume of Sojourn Access's own sounds"),
        ("sounds_volume", "Volume of the world's sounds"),
        ("music", "The parcel's music"),
        ("music_volume", "Volume of the music"),
        ("volume", "Volume of everything"),
        ("radar_chat_range", "Arrivals and departures, chat range"),
        ("radar_draw_distance", "Arrivals and departures, draw distance"),
        ("radar_region", "Arrivals and departures, the region"),
        ("walk_steps", "Walk mode, footsteps"),
        ("walk_steps_volume", "Walk mode, footsteps' volume"),
        ("walk_walls", "Walk mode, warning before a bump"),
        ("walk_walls_volume", "Walk mode, bump warning's volume"),
        ("walk_openings", "Walk mode, openings and doors passed"),
        ("walk_openings_volume", "Walk mode, openings' volume"),
        ("walk_edges", "Walk mode, a drop or water ahead"),
        ("walk_edges_volume", "Walk mode, drops' volume"),
        ("walk_turns", "Walk mode, where you face after turning"),
        ("walk_turns_volume", "Walk mode, turning's volume"),
        ("walk_sonar", "Walk mode, the sonar sweep"),
        ("walk_sonar_volume", "Walk mode, the sonar's volume"),
        ("walk_beacon", "Walk mode, the beacon on where you're heading"),
        ("walk_beacon_volume", "Walk mode, the beacon's volume"),
        ("walk_marks", "Walk mode, doors, seats and touchable things (O)"),
        ("walk_marks_volume", "Walk mode, their sounds' volume"),
        ("walk_follow", "Following someone, their stops and turns"),
        ("walk_vehicle", "Driving, what's ahead and the speed"),
        ("walk_vehicle_volume", "Driving, its sounds' volume"),
        ("walk_turn_step", "Walk mode, degrees a tap turns"),
        ("walk_turn_rate", "Walk mode, degrees a second held"),
        ("walk_teleport_fallback", "A walk with no way around teleports"),
        ("incognito", "Incognito (Control+Shift+I)"),
        ("incognito_sees", "Incognito: the assistant sees"),
        ("incognito_while_im", "Incognito while you're in an IM"),
        ("incognito_here", "Incognito always on this parcel"),
        ("voice_chat", "Voice chat"),
        ("voice_mode", "Voice: push to talk or open mic"),
        ("voice_volume", "Voice: how loud residents are"),
        ("voice_hear_from", "Voice: heard from"),
        ("access_talk_key", "Voice: the talk key"),
        ("access_voice_speakers", "Voice: say who starts speaking"),
    };

    void BuildSettings()
    {
        _settingsPage = new TabPage("Settings") { Name = "settingsPage", AccessibleName = "Settings" };
        _settingsList = List("settings", "Settings");
        _settingsList.KeyDown += (_, e) =>
        {
            var item = _settingsList.SelectedItems.Count > 0 ? _settingsList.SelectedItems[0] : _settingsList.FocusedItem;
            if (e.KeyCode is not (Keys.Enter or Keys.Space) || e.Control || e.Alt || item?.Tag is not string name) return;
            e.Handled = e.SuppressKeyPress = true;
            // incognito's off, and letting it see mid-spell, are the person's own key here, judged as it's handled
            if (IncognitoRow(name) || AnswersRow(name)) return;
            _ = NextValueAsync(name, e.Shift ? -1 : 1);
        };
        _settingsPage.Controls.Add(_settingsList);
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
            Reader?.Answer($"{label}: {value}.");
        }
    }

    // SojournSettings' get and set run their changes on the main thread (Host.Tick drains them), so they're called off it
    // (Task.Run carries the person's input along: a set runs as theirs only from their key)
    Task<JsonObject> SettingsOp(JsonObject args, string op) =>
        Task.Run(() => op == "get" ? _ops.ReadAsync("SojournSettings", op, args) : _ops.DoAsync("SojournSettings", op, args));

    async Task RefreshSettings()
    {
        bool first = _settingsList.Items.Count == 0;
        if (first) Looking(_settingsList, "Settings", true);
        try
        {
            var all = (await SettingsOp(new JsonObject(), "get"))["settings"] as JsonObject ?? new();
            var items = new List<(string, string, object)>();
            foreach (var (name, label) in Shown)
                if (all[name] is JsonObject s) items.Add(($"{label}: {Value(s["value"])}", name, name));
            Fill(_settingsList, items, "No settings (not logged in?).");
        }
        finally { if (first) Looking(_settingsList, "Settings", false); }
    }

    static string Value(JsonNode? v) => v switch
    {
        JsonValue b when b.TryGetValue<bool>(out var on) => on ? "on" : "off",
        null => "none",
        _ => v.ToString(),
    };

    /// <summary>The setting's next value (dir 1: Enter or Space) or the one before (-1: with Shift), round the ends.</summary>
    async Task NextValueAsync(string name, int dir = 1)
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
        if (values.Length < 2) { Reader?.Answer($"{now}. This one is changed by asking the assistant.", keep: false); return; }
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
        JsonNode value = isSwitch ? JsonValue.Create(next == "on")
            : isNumber ? JsonValue.Create(double.Parse(next, System.Globalization.CultureInfo.InvariantCulture)) : JsonValue.Create(next);
        var r = await SettingsOp(new JsonObject { [name] = value }, "set");
        if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Not changed: {e}"); return; }
        await RefreshSettings();
        var label = Shown.FirstOrDefault(s => s.Name == name).Label ?? name;
        Reader?.Answer($"{label}: {next}.");
        _earcons?.Preview(name);
    }
}
