using System.Text.Json.Nodes;
using Sojourn.Core;
using Sojourn.Session.Assistant;

namespace Sojourn.Access;

/// <summary>Settings (Alt+0): the ones that matter here, from SojournSettings (the same the assistant changes), each a
/// list item "Nearby chat read out: on"; Enter, Space or Right moves it to its next value, Shift with them or Left to
/// the one before, said back (a sound's row plays it
/// at its new level, so each can be learned; a volume given as a number steps through a few levels).</summary>
public sealed partial class MainForm
{
    TabPage _settingsPage = null!;
    ListView _settingsList = null!;

    // each row leads with what it's about, so first-letter navigation finds it and the useful word comes first ("Sound
    // for ..." on eleven rows, "Walk mode, ..." on twenty, hid both)
    static readonly (string Name, string Label)[] Shown =
    {
        ("screen_reader", "Screen reader read-out"),
        ("announce_nearby", "Nearby chat read out"),
        ("announce_objects", "Objects' chat read out"),
        ("announce_ims", "IMs read out"),
        ("announce_group", "Group chat read out"),
        ("announce_notices", "Notices read out"),
        ("announce_replies", "The assistant's replies read out"),
        ("access_describing", "Describing model (economy or best)"),
        ("announce_music", "Music titles read out"),
        ("access_read_mine", "My own lines read back"),
        ("access_mute_all", "Mute all read-out (F6)"),
        ("access_timestamps", "Times in the histories"),
        ("access_answers_without_key", "Answers without a physical key"),
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
        ("access_sounds_volume", "Sojourn Access's own sounds, volume"),
        ("sounds_volume", "World's sounds, volume"),
        ("music", "Parcel's music"),
        ("music_volume", "Music volume"),
        ("volume", "Overall volume"),
        ("radar_chat_range", "Chat range arrivals and departures"),
        ("radar_draw_distance", "Draw distance arrivals and departures"),
        ("radar_region", "Region arrivals and departures"),
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
        ("walk_marks", "Marks: doors, seats and touchable things (walk mode, O)"),
        ("walk_marks_volume", "Marks' volume"),
        ("walk_follow", "Following someone: their stops and turns"),
        ("walk_distance", "Distance walked, said when you pause (walk mode)"),
        ("walk_vehicle", "Driving: what's ahead and the speed"),
        ("walk_vehicle_volume", "Driving sounds' volume"),
        ("walk_turn_step", "Degrees a tap of Left or Right turns (walk mode)"),
        ("walk_turn_rate", "Degrees a second Left or Right held turns (walk mode)"),
        ("walk_teleport_fallback", "Teleport when a walk finds no way around"),
        ("incognito", "Incognito (Control+Shift+I)"),
        ("incognito_sees", "Assistant sees during incognito"),
        ("incognito_while_im", "While you're in an IM, incognito"),
        ("incognito_here", "This parcel always incognito"),
        ("voice_chat", "Voice chat"),
        ("voice_mode", "Push to talk or open mic"),
        ("voice_volume", "Residents' voices, volume"),
        ("access_talk_key", "Talk key"),
        ("access_voice_speakers", "Who starts speaking in voice, said"),
    };

    void BuildSettings()
    {
        _settingsPage = new TabPage("Settings") { Name = "settingsPage", AccessibleName = "Settings" };
        _settingsList = List("settings", "Settings");
        _settingsList.KeyDown += (_, e) =>
        {
            var item = _settingsList.SelectedItems.Count > 0 ? _settingsList.SelectedItems[0] : _settingsList.FocusedItem;
            if (item?.Tag is string own && own.StartsWith('@') && ProviderKey(own, e)) return;
            if (e.KeyCode is not (Keys.Enter or Keys.Space or Keys.Left or Keys.Right) || e.Control || e.Alt || item?.Tag is not string name) return;
            e.Handled = e.SuppressKeyPress = true;
            // incognito's off, and letting it see mid-spell, are the person's own key here, judged as it's handled
            if (IncognitoRow(name) || AnswersRow(name)) return;
            _ = NextValueAsync(name, e.Shift || e.KeyCode == Keys.Left ? -1 : 1);
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
        if (first) Looking(_settingsList, "Settings", true);
        try
        {
            var all = (await SettingsOp(new JsonObject(), "get"))["settings"] as JsonObject ?? new();
            var items = new List<(string, string, object)>();
            foreach (var (name, label) in Shown)
                if (all[name] is JsonObject s) items.Add(($"{label}: {ValueWords(name, Value(s["value"]))}", name, name));
            if (items.Count > 0) items.InsertRange(0, ProviderItems());
            Fill(_settingsList, items, "No settings (not logged in?).");
        }
        finally { if (first) Looking(_settingsList, "Settings", false); }
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
        _ => "Studio Quire's relay, with a token from the studio",
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
        items.Add(($"Auto mode for the assistant: {(st.AssistantAutoMode ? "on" : "off, no shell, web or helpers")}", "@auto", "@auto"));
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
            said = st.AssistantAutoMode ? "Auto mode on." : "Auto mode off: the assistant has no shell, web or helpers.";
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
        JsonValue b when b.TryGetValue<bool>(out var on) => on ? "on" : "off",
        null => "none",
        _ => v.ToString(),
    };

    /// <summary>A value as its row says it: the setting's own word, or what it means where the word alone doesn't say
    /// (a walk cue's "both" is a sound and a line; hello is Windows Hello; no underscores read out).</summary>
    static string ValueWords(string name, string value) => (name, value) switch
    {
        (_, "both") when name.StartsWith("walk_") => "sound and text",
        ("access_answers_without_key", "refuse") => "refused",
        ("access_answers_without_key", "hello") => "Windows Hello",
        ("access_talk_key", "f4" or "f12") => value.ToUpperInvariant(),
        _ => value.Replace('_', ' '),
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
        if (values.Length < 2) { Reader?.Answer($"{ValueWords(name, now)}. This one is changed by asking the assistant.", keep: false); return; }
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
        Reader?.Answer($"{label}: {ValueWords(name, next)}.");
        _earcons?.Preview(name);
    }
}
