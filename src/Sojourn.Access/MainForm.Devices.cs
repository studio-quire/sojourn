using System.Text.Json.Nodes;
using Sojourn.Core;
using Sojourn.Session.Audio;

namespace Sojourn.Access;

/// <summary>
/// Where sound comes out and the mic voice hears: the session's own choice (SojournSettings' speakers and microphone,
/// the VR client's), here as Alt+0 rows stepping through the default, then each device as Windows names it, and
/// Control+Shift+O to move the sound to the next device (the blind tester switches between her headset and speakers
/// in Radegast, 2026-10-31). A change takes effect at once (AudioEngine.Reopen). The shim follows Windows by itself
/// (audio_io.cpp: the default as it moves, a device unplugged and plugged back, none there at all); what it did is
/// said once it settles, in one line: after her headset was unplugged, 0.1.0.707 went silent until she logged in again.
/// </summary>
public sealed partial class MainForm
{
    // the device rows' setting names (SojournSettings, section sound; "microphone" only while voice owns the engine)
    static bool DeviceRow(string name) => name is "speakers" or "microphone";

    /// <summary>A device row's value as said: "the default (Speakers (Realtek))", or the device's own name.</summary>
    static string DeviceWords(string name, string value)
    {
        bool mic = name == "microphone";
        if (value is "auto" or "" or "automatic")
            return AudioEngine.List(mic).FirstOrDefault(d => d.Default).Name is { Length: > 0 } now ? $"the default ({now})" : "the default";
        bool there = AudioEngine.List(mic).Any(d => d.Name == value);
        return there ? value : $"{value}, not connected now (the default until it is)";
    }

    /// <summary>Enter, Space, Right (or back with Shift, Left) on a device row: the default, then each device, round.</summary>
    async Task NextDeviceAsync(string name, int dir)
    {
        bool mic = name == "microphone";
        var one = (await SettingsOp(new JsonObject { ["name"] = name }, "get"))["settings"]?[name] as JsonObject;
        var now = one?["value"]?.ToString() ?? "auto";
        var next = Step(mic, now, dir);
        var r = await SettingsOp(new JsonObject { [name] = next }, "set");
        if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Not changed: {e}"); return; }
        await RefreshSettings();
        Reader?.Answer($"{Shown.First(s => s.Name == name).Label}: {DeviceWords(name, next)}.");
    }

    static string Step(bool mic, string now, int dir)
    {
        var names = new List<string> { "auto" };
        names.AddRange(AudioEngine.List(mic).Select(d => d.Name));
        int i = names.IndexOf(now);
        return names[((i < 0 ? 0 : i) + (dir < 0 ? -1 : 1) + names.Count) % names.Count];
    }

    /// <summary>Control+Shift+O: the sound out of the next device (after the last, back to the default), said.</summary>
    async Task NextOutputAsync()
    {
        if (Host?.Audio == null && Host?.Voice?.Voice == null) { Answer(SoundWords.Missing(Host) ?? "No sound in this session."); return; }
        var one = (await SettingsOp(new JsonObject { ["name"] = "speakers" }, "get"))["settings"]?["speakers"] as JsonObject;
        if (one == null) { Answer("No sound in this session."); return; }
        var next = Step(false, one["value"]?.ToString() ?? "auto", 1);
        var r = await SettingsOp(new JsonObject { ["speakers"] = next }, "set");
        if (PersonOps.Error(r) is { } e) { Answer($"Not changed: {e}"); return; }
        Reader?.Answer($"Sound on {DeviceWords("speakers", next)}.", keep: false);
        if (_areas.SelectedTab == _settingsPage) _ = RefreshSettings();
    }

    // --- what the shim did, said once it settles (voice reads its device events; without voice it still recovers) ---

    System.Windows.Forms.Timer? _deviceSettle;
    string? _speakerSaid, _micSaid;
    bool _speakerGone, _micGone;

    void StartDeviceWords()
    {
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
