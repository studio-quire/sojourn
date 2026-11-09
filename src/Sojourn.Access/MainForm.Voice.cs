using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core;
using Sojourn.Session.Control;
using Sojourn.Session.Voice;

namespace Sojourn.Access;

/// <summary>
/// Voice chat's keys in Sojourn Access (Session's Voice/, as the VR client's; docs/voice.md): push-to-talk, the
/// default, holds the talk key (F4 unless changed: access_talk_key) while speaking; with an open mic the same key mutes
/// and unmutes. Going live takes the person's own key (a program's never opens the mic; muting is anyone's), and a key
/// held when the window loses the focus lets go. Shift+talk key switches between push-to-talk and an open mic.
/// Control+Shift+V says who's in voice and who's speaking; with access_voice_speakers on, someone starting to speak is
/// said as they start (at most once in ten seconds each). Control with the talk key: who's in voice as a menu, to act on
/// one or everyone, or to reconnect. A call ringing is said, and `//voice answer` or `//voice decline` answers it. Voice chat on or off and its volume are in Alt+0 (not where it's heard from: Access hears from the
/// avatar's head, having no camera).
/// </summary>
public sealed partial class MainForm
{
    bool _talkHeld;
    readonly Dictionary<UUID, (bool Speaking, long SaidAt)> _speakers = new();

    VoiceClient? Voice => Host?.Voice?.Voice is { Available: true } v ? v : null;

    Keys TalkKey => (_set.TalkKey ?? "f4") switch
    {
        "f12" => Keys.F12,
        "scroll_lock" => Keys.Scroll,
        "pause" => Keys.Pause,
        _ => Keys.F4,
    };

    void StartVoiceKeys()
    {
        var st = Host!.Settings;
        if (Host.Voice?.Voice is not { } v) return;
        // the saved choices (the VR client's settings menu puts them to voice; Access has none)
        v.Enabled = st.VoiceEnabled;
        v.Mode = st.VoiceMode;
        v.SetVolume(st.VoiceVolume);
        v.IncomingChanged += call => BeginInvoke(() =>
        {
            if (call == null) return;
            var line = $"{call.Name} is calling{(call.Kind == "group" ? $" {call.SessionName ?? "a group"}" : call.Kind == "conference" ? " you into a conference" : "")}, in voice: //voice answer, or //voice decline.";
            // F6 mutes what arrives, a call as an IM; it's still in the history
            if (_set.MuteAll) AppendChat(line);
            else Announce(line);
        });
    }

    /// <summary>The talk key went down (ProcessCmdKey): push-to-talk held, or open mic's mute toggled. Only the person's
    /// own key opens the mic.</summary>
    bool TalkDown(Keys keyData)
    {
        if (keyData == (Keys.Shift | TalkKey)) { ToggleVoiceMode(); return true; }
        // Control with it: who's in voice, to act on (Control turns Scroll Lock and Pause into Break)
        if (keyData == (Keys.Control | TalkKey) || keyData == (Keys.Control | Keys.Cancel) && TalkKey is Keys.Scroll or Keys.Pause)
        {
            ShowVoicePeople();
            return true;
        }
        if (keyData != TalkKey) return false;
        var v = Voice;
        if (v == null) { if (!_talkHeld) Reader?.Answer(VoiceOff(), keep: false); _talkHeld = true; return true; }
        if (_talkHeld) return true;   // the key's repeat
        _talkHeld = true;
        if (v.Mode == VoiceMode.OpenMic)
        {
            if (v.Muted && !RealInput.Person) { Reader?.Answer(RealInput.Why, keep: false); return true; }
            v.ToggleMute();
            _earcons?.Mic(open: !v.Muted);
            Reader?.Answer(v.Muted ? "Mic muted." : _micGone ? "Mic live, but there's no microphone: nobody hears you until one is plugged in." : "Mic live.", keep: false);
            return true;
        }
        if (!RealInput.Person) { Reader?.Answer(RealInput.Why, keep: false); return true; }
        if (!v.Enabled) { Reader?.Answer("Voice chat is off: Alt+0, the Voice group, turns it on.", keep: false); return true; }
        if (v.OffHere) Reader?.Answer("Voice is off here: nobody hears you.", keep: false);
        else if (_micGone) Reader?.Answer("No microphone: nobody hears you until one is plugged in.", keep: false);
        Host!.Voice!.KeyDown();
        _micOpen = true;
        _earcons?.Mic(open: true);
        return true;
    }

    /// <summary>The form's key up (KeyPreview): push-to-talk let go.</summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == TalkKey && _talkHeld) TalkUp();
        base.OnKeyUp(e);
    }

    void TalkUp()
    {
        _talkHeld = false;
        Host?.Voice?.KeyUp();
        MicClosed();
    }

    bool _micOpen;   // push to talk held open by the talk key

    void MicClosed()
    {
        if (!_micOpen) return;
        _micOpen = false;
        _earcons?.Mic(open: false);
    }

    /// <summary>The window lost the focus: a held talk key lets go (its key up may never come here).</summary>
    void VoiceRelease()
    {
        _talkHeld = false;
        Host?.Voice?.Release();
        MicClosed();
    }

    void ToggleVoiceMode()
    {
        if (Voice is not { } v) { Reader?.Answer(VoiceOff(), keep: false); return; }
        var st = Host!.Settings;
        bool wasLive = v.Mode == VoiceMode.OpenMic && !v.Muted;
        st.VoiceMode = v.Mode == VoiceMode.OpenMic ? VoiceMode.PushToTalk : VoiceMode.OpenMic;
        v.Mode = st.VoiceMode;
        Host.SaveSettings();
        if (wasLive) _earcons?.Mic(open: false);   // an open mic, live, back to push to talk: closed now
        Reader?.Answer(st.VoiceMode == VoiceMode.OpenMic ? $"Open mic, muted: {KeyName} unmutes." : $"Push to talk: hold {KeyName} to talk.", keep: false);
    }

    string KeyName => TalkKey switch { Keys.Scroll => "Scroll Lock", Keys.Pause => "Pause", Keys.F12 => "F12", _ => "F4" };

    string VoiceOff() => Host?.Voice == null ? "Voice chat isn't running (Sojourn Access was started without it)."
        : Host.Voice.Voice.Available ? "Voice chat is off: Alt+0, the Voice group, turns it on."
        : SoundWords.Missing(Host) is { } why ? $"No voice chat. {why}" : $"Voice chat can't run here: {Host.Voice.Voice.Status}.";

    string VoiceName(UUID id) => Host?.Session.Names is { } n ? n.DisplayName(id) ?? n.Legacy(id) ?? "someone" : "someone";

    /// <summary>Control+Shift+V and `//voice`: who's in voice and who's speaking now, your mic, where you are.</summary>
    void SayVoice()
    {
        if (Voice is not { } v) { Answer(VoiceOff()); return; }
        if (!v.Enabled) { Answer("Voice chat is off: Alt+0, the Voice group, turns it on."); return; }
        var me = _ops.Client?.Self.AgentID ?? UUID.Zero;
        var peers = v.Peers.Where(p => p.Id != me).ToList();
        string Name(UUID id) => VoiceName(id) + (v.PeerIsMuted(id) ? " (muted)" : "");
        var speaking = peers.Where(p => p.Speaking).Select(p => Name(p.Id)).ToList();
        var quiet = peers.Where(p => !p.Speaking).Select(p => Name(p.Id)).OrderBy(n => n).ToList();
        var where = v.CallInfo is { } call ? $"In a call with {call.With}." : v.OffHere ? "Voice is off here." : "Nearby voice.";
        var mic = v.ModeratorMuted ? "A moderator muted you." : v.Mode == VoiceMode.OpenMic ? (v.Muted ? "Open mic, muted." : "Open mic, live.")
            : $"Push to talk ({KeyName}).";
        var who = peers.Count == 0 ? "Nobody else in voice." : (speaking.Count > 0 ? $"Speaking: {string.Join(", ", speaking)}. " : "Nobody speaking. ")
            + (quiet.Count > 0 ? $"Also in voice: {string.Join(", ", quiet)}." : "");
        var spell = v.OthersMuted ? " Everyone else is muted for you, but those you've unmuted." : "";
        Answer($"{where} {who.Trim()}{spell} {mic}");
    }

    // --- who's in voice, to act on (Radegast's participant list) ----------------------------------------------------------

    readonly ContextMenuStrip _voiceMenu = new() { Name = "voiceMenu", AccessibleName = "People in voice" };

    /// <summary>Control with the talk key (Control+F4 unless changed), World > People in voice, `//voice people`: who's in
    /// voice as a menu where the focus is, each with their actions under them (the same as everywhere, Mute in voice and How
    /// loud in voice among them), then Mute everyone else, Unmute everyone and Reconnect voice. A menu rather than a list
    /// that stays: who's speaking changes by the second, and Control+Shift+V says it as it is.</summary>
    void ShowVoicePeople()
    {
        if (Voice == null) { Answer(VoiceOff()); return; }
        if (_ops?.Client is null) { Answer("Not in a region yet."); return; }
        FillVoicePeople(_voiceMenu.Items);
        var at = ActiveControl ?? (Control)this;
        _voiceMenu.Show(at, new Point(10, Math.Min(at.Height, 30)));
    }

    /// <summary>The people in voice by name, each said with whether they're speaking, muted (by you, or by a moderator) or
    /// at a loudness of their own; Right opens their actions. Then what acts on everyone, without letters, so a letter
    /// only ever finds a name.</summary>
    void FillVoicePeople(ToolStripItemCollection items)
    {
        items.Clear();
        if (Voice is not { } v) return;
        var me = _ops.Client?.Self.AgentID ?? UUID.Zero;
        var peers = v.Peers.Where(p => p.Id != me).Select(p => (Peer: p, Name: VoiceName(p.Id)))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (peers.Count == 0)
            items.Add(new ToolStripMenuItem(!v.Enabled ? "Voice chat is off: Alt+0's Voice group turns it on" : v.OffHere ? "Voice is off here" : "Nobody else in voice") { Enabled = false });
        int n = 0;
        foreach (var (p, name) in peers)
        {
            var words = new List<string> { name };
            if (p.Speaking) words.Add("speaking");
            if (v.PeerIsMuted(p.Id)) words.Add("muted");
            else if (p.ModeratorMuted) words.Add("muted by a moderator");
            if (v.PeerVolume(p.Id) is var vol && Math.Abs(vol - 1) > 0.001f) words.Add($"at {vol.ToString("0.##", CultureInfo.InvariantCulture)}");
            var item = new ToolStripMenuItem(string.Join(", ", words).Replace("&", "&&")) { Name = $"voicePerson{++n}" };
            var id = p.Id;
            item.DropDownItems.Add(new ToolStripMenuItem("(their actions)"));   // a submenu needs an item to open
            item.DropDownOpening += (_, _) => { item.DropDownItems.Clear(); AddPersonItems(item.DropDown, id, name); };
            item.DropDownOpened += (_, _) => Mnemonics.Fix(item.DropDown);
            items.Add(item);
        }
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Mute everyone else", null, (_, _) => _ = MuteOthersAsync(true))
            { Name = "voiceMuteOthers", ShortcutKeyDisplayString = "//voice mute all", Checked = v.OthersMuted });
        items.Add(new ToolStripMenuItem("Unmute everyone", null, (_, _) => _ = MuteOthersAsync(false))
            { Name = "voiceUnmuteAll", ShortcutKeyDisplayString = "//voice unmute all" });
        items.Add(new ToolStripMenuItem("Reconnect voice", null, (_, _) => _ = ReconnectVoiceAsync())
            { Name = "voiceReconnect", ShortcutKeyDisplayString = "//voice reconnect" });
    }

    /// <summary>Everyone else muted for her, for this session and people who join later too (Radegast's Mute all; not put
    /// on her block list, which a club's worth of names would fill), or everyone heard again (voice-only mutes of the
    /// people in voice now taken off too; a block stays a block).</summary>
    async Task MuteOthersAsync(bool mute)
    {
        if (Voice is not { } v) { Answer(VoiceOff()); return; }
        var me = _ops.Client?.Self.AgentID ?? UUID.Zero;
        int others = v.Peers.Count(p => p.Id != me);
        var r = await _ops.DoAsync("SojournVoice", "muteOthers", new JsonObject { ["mute"] = mute });
        if (PersonOps.Error(r) is { } e) { Answer($"Not changed: {e}"); return; }
        if (mute)
        {
            Answer($"{(others == 0 ? "Nobody else is in voice now; anyone who joins is muted" : others == 1 ? "The one other person in voice muted" : $"The {others} others in voice muted")}, "
                   + "for you alone, and anyone who joins too, until you unmute everyone or log out. Unmute in voice, on a person, lets one through.");
            return;
        }
        var still = (r["still_muted"] as JsonArray ?? new()).OfType<JsonObject>()
            .Select(o => UUID.TryParse(o.Str("id"), out var id) ? VoiceName(id) : o.Str("name") ?? "someone").ToList();
        Answer("Everyone in voice is heard again." + (still.Count == 0 ? ""
            : $" {Join(still)} {(still.Count == 1 ? "stays" : "stay")} silent: blocked, and unblocking is on their own menu."));
    }

    /// <summary>Voice's connections started afresh, then said when it's back (or that it's still trying by itself).</summary>
    async Task ReconnectVoiceAsync()
    {
        if (Voice is not { } v) { Answer(VoiceOff()); return; }
        if (!v.Enabled) { Answer("Voice chat is off: Alt+0, the Voice group, turns it on."); return; }
        var r = await _ops.DoAsync("SojournVoice", "reconnect");
        if (PersonOps.Error(r) is { } e) { Answer($"Not reconnected: {e}"); return; }
        if (r["reconnecting"]?.GetValue<bool>() != true)
        {
            Answer(v.OffHere ? "Voice is off here, so there's nothing to reconnect." : "Not in voice anywhere yet, so there's nothing to reconnect.");
            return;
        }
        Reader?.Answer("Reconnecting voice.", keep: false);
        await Task.Delay(1500);
        for (int i = 0; i < 40 && !v.Status.Contains(": connected", StringComparison.Ordinal); i++) await Task.Delay(500);
        Host?.Log.LogInformation("voice: after reconnecting, {Status}", v.Status);
        if (!v.Status.Contains(": connected", StringComparison.Ordinal)) { Answer("Voice hasn't come back yet. It keeps trying by itself."); return; }
        var me = _ops.Client?.Self.AgentID ?? UUID.Zero;
        int others = v.Peers.Count(p => p.Id != me);
        var where = v.CallInfo is { } call ? $"the call with {call.With}" : "nearby voice";
        Answer($"Voice is back: {where}, {(others == 0 ? "nobody else in it" : others == 1 ? "1 other person in it" : $"{others} others in it")}.");
    }

    /// <summary>One person's voice muted or unmuted, for her alone, and kept: the voice client's, on the account's block list
    /// with its voice flag as the viewers keep it where there is one.</summary>
    async Task VoiceMuteAsync(UUID id, string name, bool mute)
    {
        if (Voice == null) { Answer(VoiceOff()); return; }
        var r = await _ops.DoAsync("SojournVoice", "peerMute", new JsonObject { ["id"] = id.ToString(), ["mute"] = mute });
        Answer(PersonOps.Error(r) is { } e ? $"Not changed: {e}" : mute ? $"{name} muted in voice, for you alone. What they type still reaches you." : $"{name} unmuted in voice.");
    }

    /// <summary>One person's loudness for her: 0 to 2, 1 as they come.</summary>
    async Task VoiceVolumeAsync(UUID id, string name, string? given = null)
    {
        if (Voice is not { } v) { Answer(VoiceOff()); return; }
        var text = given ?? Dialogs.Ask(this, $"How loud {name} is in voice", "0 to 2; 1 is as they come", v.PeerVolume(id).ToString("0.##", CultureInfo.InvariantCulture));
        if (text == null) { Reader?.Answer("Not changed.", keep: false); return; }
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var volume) || volume is < 0 or > 2) { Answer($"{text} isn't from 0 to 2."); return; }
        var r = await _ops.DoAsync("SojournVoice", "peerVolume", new JsonObject { ["id"] = id.ToString(), ["volume"] = volume });
        Answer(PersonOps.Error(r) is { } e ? $"Not changed: {e}" : $"{name} at {volume.ToString("0.##", CultureInfo.InvariantCulture)} in voice, for you alone.");
    }

    /// <summary>`//voice [answer | decline | who | people | mute all | unmute all | reconnect | mute NAME | unmute NAME |
    /// volume NAME N]`.</summary>
    async Task VoiceWordsAsync(string words)
    {
        var w = string.Join(" ", words.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var v = Voice;
        switch (w)
        {
            case "people" or "list":
                ShowVoicePeople();
                return;
            case "mute all" or "muteall" or "mute everyone":
                await MuteOthersAsync(true);
                return;
            case "unmute all" or "unmuteall" or "unmute everyone":
                await MuteOthersAsync(false);
                return;
            case "reconnect":
                await ReconnectVoiceAsync();
                return;
        }
        foreach (var verb in new[] { "mute ", "unmute ", "volume " })
        {
            if (!w.StartsWith(verb, StringComparison.Ordinal)) continue;
            var rest = words.Trim()[verb.Length..].Trim();
            string? level = null;
            if (verb == "volume ")
            {
                int space = rest.LastIndexOf(' ');
                if (space < 0) { Answer("//voice volume NAME and a number from 0 to 2; 1 is as they come."); return; }
                level = rest[(space + 1)..];
                rest = rest[..space].Trim();
            }
            if (Voice == null) { Answer(VoiceOff()); return; }
            if (await FindByNameAsync(rest) is not { } who) return;
            if (level != null) await VoiceVolumeAsync(who.Id, who.Name, level);
            else await VoiceMuteAsync(who.Id, who.Name, verb == "mute ");
            return;
        }
        if (w is "answer" or "accept")
        {
            if (v?.Incoming is not { } call) Answer("Nobody is calling.");
            else { v.Accept(); Answer($"Answered {call.Name}."); }
        }
        else if (w is "decline" or "reject")
        {
            if (v?.Incoming is not { } call) Answer("Nobody is calling.");
            else { v.Decline(); Answer($"Declined {call.Name}'s call."); }
        }
        else SayVoice();
    }

    /// <summary>The slow tick: with access_voice_speakers on, someone starting to speak is said (each at most once in
    /// ten seconds).</summary>
    void WatchSpeakers()
    {
        if (!_set.VoiceSpeakers || Voice is not { Enabled: true } v) return;
        long now = Environment.TickCount64;
        var me = _ops.Client?.Self.AgentID ?? UUID.Zero;
        foreach (var p in v.Peers)
        {
            if (p.Id == me) continue;
            (bool Speaking, long SaidAt) was = _speakers.TryGetValue(p.Id, out var s) ? s : (false, long.MinValue / 2);
            if (p.Speaking && !was.Speaking && now - was.SaidAt > 10_000 && !_set.MuteAll)
            {
                Reader?.Say(Sojourn.Core.Access.AnnounceKind.Nearby, $"{Host!.Session.Names.DisplayName(p.Id) ?? "Someone"} speaking.", keep: false, Sojourn.Core.Access.LineFrom.App);
                _speakers[p.Id] = (true, now);
            }
            else _speakers[p.Id] = (p.Speaking, was.SaidAt);
        }
    }

    /// <summary>Voice's settings on SojournSettings, as the VR client has them (voice_chat, voice_mode, voice_volume),
    /// and Access's talk key and speakers said.</summary>
    void AddVoiceSettings(SettingsApi api)
    {
        var st = Host!.Settings;
        api.Add("voice_chat", new("voice", "voice chat with residents", "on, off",
            () => st.VoiceEnabled, SettingsApi.Switch("voice_chat", on =>
            {
                st.VoiceEnabled = on;
                if (Host.Voice?.Voice is { } v) v.Enabled = on;
            })));
        api.Add("voice_mode", new("voice", $"push to talk (hold the talk key, {KeyName}), or an open mic that starts muted (the talk key mutes and unmutes)",
            "push_to_talk, open_mic", () => st.VoiceMode == VoiceMode.OpenMic ? "open_mic" : "push_to_talk",
            SettingsApi.Choice("voice_mode", ["push_to_talk", "open_mic"], i =>
            {
                st.VoiceMode = i == 1 ? VoiceMode.OpenMic : VoiceMode.PushToTalk;
                if (Host.Voice?.Voice is { } v) v.Mode = st.VoiceMode;
            })));
        api.Add("voice_volume", new("voice", "how loud residents are (1: the viewer's own level)", "0-2",
            () => st.VoiceVolume, SettingsApi.Number("voice_volume", 0, 2, "", f => { st.VoiceVolume = f; Host.Voice?.Voice.SetVolume(f); })));
        string[] keys = { "f4", "f12", "scroll_lock", "pause" };
        api.Add("access_talk_key", new("access", "Sojourn Access: the talk key, held to talk (push to talk) or pressed to mute and unmute (open mic); "
            + "Shift with it switches between the two", string.Join(", ", keys), () => _set.TalkKey ?? "f4",
            SettingsApi.Choice("access_talk_key", keys, i => { _set.TalkKey = keys[i]; _set.Save(); })));
        api.Add("access_voice_speakers", new("access", "Sojourn Access: someone starting to speak in voice is said (at most once in ten "
            + "seconds each); off, Control+Shift+V says who's speaking when asked", "on, off", () => _set.VoiceSpeakers,
            SettingsApi.Switch("access_voice_speakers", on => { _set.VoiceSpeakers = on; _set.Save(); })));
    }
}
