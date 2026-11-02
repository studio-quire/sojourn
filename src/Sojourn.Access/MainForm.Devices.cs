using System.Text.Json.Nodes;
using Sojourn.Core;
using Sojourn.Session.Audio;

namespace Sojourn.Access;

/// <summary>
/// Where sound comes out and the mic voice hears: the session's own choice (SojournSettings' speakers and microphone,
/// the VR client's). Alt+0's rows step through the default, then each device (Space, Right; back with Left), or Enter
/// opens the list of them to choose from; Control+Shift+O moves the sound to the next device (the blind tester switches
/// between her headset and speakers in Radegast, 2026-10-31); World > Sound output has the same list. Each device is
/// said with what it is and where it stands ("Speakers (Realtek(R) Audio), speakers, 3 of 4"), likely ones first:
/// stepping from her headset in 0.1.0.737 landed on her TV's HDMI output, which Windows names "4 - 2D HD LG TV (2- AMD
/// High Definition Audio Device)", and she heard nothing (2026-11-01). A change takes effect at once
/// (AudioEngine.Reopen) and is said only once the device is taking sound (AudioWiring's check); one that doesn't start
/// is put back, and said so with why. The shim follows Windows by itself (audio_io.cpp: the default as it moves, a
/// device unplugged and plugged back, none there at all); what it did is said once it settles, in one line: after her
/// headset was unplugged, 0.1.0.707 went silent until she logged in again.
/// </summary>
public sealed partial class MainForm
{
    // the device rows' setting names (SojournSettings, section sound; "microphone" only while voice owns the engine)
    static bool DeviceRow(string name) => name is "speakers" or "microphone";

    static bool Auto(string value) => value is "auto" or "" or "automatic";

    /// <summary>A device row's value as said: "the default (Speakers (3- EPOS PC 8 USB), a headset)", or the device's
    /// own name and what it is ("4 - 2D HD LG TV (...), a TV or monitor, over HDMI or DisplayPort").</summary>
    static string DeviceWords(string name, string value)
    {
        bool mic = name == "microphone";
        var list = AudioEngine.List(mic);
        static string With(AudioEngine.Device d) => d.Kind.Length > 0 ? $"{d.Name}, {d.Kind}" : d.Name;
        if (Auto(value))
            return list.FirstOrDefault(d => d.Default) is { } now ? $"the default ({With(now)})" : "the default";
        return AudioEngine.Find(mic, value) is { } there ? With(list.First(d => d.Name == there))
            : $"{value}, not connected now (the default until it is)";
    }

    /// <summary>The choices, in the order stepped and listed: "auto" (the default), then each device's name.</summary>
    static List<string> DeviceChoices(bool mic)
    {
        var names = new List<string> { "auto" };
        names.AddRange(AudioEngine.List(mic).Select(d => d.Name));
        return names;
    }

    static int DeviceIndex(bool mic, List<string> names, string value) =>
        names.IndexOf(Auto(value) ? "auto" : AudioEngine.Find(mic, value) ?? value);

    /// <summary>Where a value stands among the choices: ", 3 of 4" (nothing for a device not connected now).</summary>
    static string DevicePlace(bool mic, string value)
    {
        var names = DeviceChoices(mic);
        int i = DeviceIndex(mic, names, value);
        return i < 0 ? "" : $", {i + 1} of {names.Count}";
    }

    async Task<string> DeviceValue(string name) =>
        ((await SettingsOp(new JsonObject { ["name"] = name }, "get"))["settings"]?[name] as JsonObject)?["value"]?.ToString() ?? "auto";

    /// <summary>Space, Right (or back with Shift, Left) on a device row: the default, then each device, round.</summary>
    async Task NextDeviceAsync(string name, int dir)
    {
        bool mic = name == "microphone";
        await ChooseDeviceAsync(name, Step(mic, await DeviceValue(name), dir));
    }

    /// <summary>A device chosen (stepped to, or from the list): set, checked, the row refreshed, and said.</summary>
    async Task ChooseDeviceAsync(string name, string value)
    {
        bool mic = name == "microphone";
        var r = await Switching(mic, () => SettingsOp(new JsonObject { [name] = value }, "set"));
        if (_areas.SelectedTab == _settingsPage) await RefreshSettings();
        if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Not changed: {e}"); return; }
        var note = !mic && Host?.Audio?.LastNote is { } q ? $" But {q}." : "";
        Reader?.Answer(name == "speakers" ? $"Sound on {DeviceWords(name, value)}{DevicePlace(mic, value)}.{note}"
            : $"{Shown.First(s => s.Name == name).Label}: {DeviceWords(name, value)}{DevicePlace(mic, value)}.");
    }

    static string Step(bool mic, string now, int dir)
    {
        var names = DeviceChoices(mic);
        int i = DeviceIndex(mic, names, now);
        return names[((i < 0 ? 0 : i) + (dir < 0 ? -1 : 1) + names.Count) % names.Count];
    }

    /// <summary>The choices as menu items (the default, then each device with what it is), the one in use checked;
    /// choosing one sets it as ChooseDeviceAsync does.</summary>
    ToolStripItem[] DeviceItems(string name, string now)
    {
        bool mic = name == "microphone";
        var names = DeviceChoices(mic);
        int at = DeviceIndex(mic, names, now);
        return names.Select((v, i) => (ToolStripItem)new ToolStripMenuItem(DeviceWords(name, v).Replace("&", "&&"), null,
            (_, _) => _ = ChooseDeviceAsync(name, v)) { Checked = i == at, Name = $"device{i}" }).ToArray();
    }

    /// <summary>Enter on a device row: the list of devices to choose from, opened at the row on the one in use.</summary>
    async Task DeviceListAsync(string name, ListViewItem row)
    {
        var items = DeviceItems(name, await DeviceValue(name));
        var menu = new ContextMenuStrip { AccessibleName = Shown.First(s => s.Name == name).Label };
        menu.Items.AddRange(items);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_settingsList, new Point(row.Bounds.Left, row.Bounds.Bottom));
        (items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Checked) ?? items[0] as ToolStripMenuItem)?.Select();
    }

    /// <summary>World > Sound output: the next device (as Control+Shift+O), then the list to choose from.</summary>
    ToolStripMenuItem OutputMenu()
    {
        var m = new ToolStripMenuItem("Sound o&utput") { Name = "menuOutput" };
        var next = Item("&Next device", "Control+Shift+O", () => _ = NextOutputAsync());
        m.DropDownItems.Add(next);   // a submenu needs an item to open
        m.DropDownOpening += (_, _) =>
        {
            m.DropDownItems.Clear();
            m.DropDownItems.Add(next);
            if (Host?.Audio == null || Host.Settings is not { } st) return;
            m.DropDownItems.Add(new ToolStripSeparator());
            m.DropDownItems.AddRange(DeviceItems("speakers", st.Speakers ?? "auto"));
        };
        return m;
    }

    /// <summary>A change of device through the settings (checked there, up to 3 s, put back if it doesn't start), with
    /// the device events' own line held back meanwhile and told what's open after, so it isn't said twice.</summary>
    async Task<JsonObject> Switching(bool mic, Func<Task<JsonObject>> set)
    {
        _switching++;
        try { return await set(); }
        finally
        {
            _switching--;
            if (AudioEngine.State(mic) is { State: "open" } st)
            {
                if (mic) { _micSaid = st.Name; _micGone = false; }
                else { _speakerSaid = st.Name; _speakerGone = false; }
            }
        }
    }

    /// <summary>Control+Shift+O: the sound out of the next device (after the last, back to the default), said.</summary>
    async Task NextOutputAsync()
    {
        if (Host?.Audio == null && Host?.Voice?.Voice == null) { Answer(SoundWords.Missing(Host) ?? "No sound in this session."); return; }
        var one = (await SettingsOp(new JsonObject { ["name"] = "speakers" }, "get"))["settings"]?["speakers"] as JsonObject;
        if (one == null) { Answer("No sound in this session."); return; }
        await ChooseDeviceAsync("speakers", Step(false, one["value"]?.ToString() ?? "auto", 1));
    }

    // --- what the shim did, said once it settles (voice reads its device events; without voice it still recovers) ---

    System.Windows.Forms.Timer? _deviceSettle;
    string? _speakerSaid, _micSaid;
    bool _speakerGone, _micGone;
    int _switching;   // a change of device the person made is being checked: its own line says how it went

    void StartDeviceWords()
    {
        // a chime on a newly chosen output, at Access's own sounds' level: heard there, and checked by what reaches it
        if (Host?.Audio is { } audio) audio.SwitchChime = () => (float)_set.SoundsVolume;
        if (Host?.Voice?.Voice is not { } v) return;
        v.DeviceChanged += (mic, what, name) => BeginInvoke(() =>
        {
            if (what == "open") { if (mic) _micGone = false; else _speakerGone = false; }
            else if (what is "lost" or "failed") { if (mic) _micGone = true; else _speakerGone = true; }
            else return;
            // an unplug comes as four events in a few milliseconds: the line comes from where they end up
            _deviceSettle ??= new System.Windows.Forms.Timer { Interval = 1500 };
            _deviceSettle.Stop();
            _deviceSettle.Tick -= DeviceSettled;
            _deviceSettle.Tick += DeviceSettled;
            _deviceSettle.Start();
        });
    }

    void DeviceSettled(object? sender, EventArgs e)
    {
        _deviceSettle?.Stop();
        if (Host?.Voice?.Voice is not { } v) return;
        if (_switching > 0) { _deviceSettle?.Start(); return; }
        var lines = new List<string>();
        var speaker = _speakerGone ? null : v.SpeakerDevice;
        var mic = _micGone ? null : v.MicDevice;
        // the first opening at the start isn't news; a change after it is
        if (_speakerSaid != null || _speakerGone)
        {
            if (speaker == null && _speakerSaid != "") lines.Add("No speakers or headphones: Sojourn Access is silent until one is plugged in.");
            else if (speaker != null && speaker != _speakerSaid) lines.Add($"Sound now on {speaker}.");
        }
        if (_micSaid != null || _micGone)
        {
            if (mic == null && _micSaid != "") lines.Add("No microphone: voice chat hears nothing until one is plugged in.");
            else if (mic != null && mic != _micSaid) lines.Add($"Microphone now {mic}.");
        }
        _speakerSaid = speaker ?? "";
        _micSaid = mic ?? "";
        if (lines.Count > 0) Announce(string.Join(" ", lines));
    }
}
