using LibreMetaverse;
using Sojourn.Session.Control;
using Sojourn.Session.Voice;

namespace Sojourn.Access;

/// <summary>
/// Voice chat's keys in Sojourn Access (Session's Voice/, as the VR client's; docs/voice.md): push-to-talk, the
/// default, holds the talk key (F4 unless changed: access_talk_key) while speaking; with an open mic the same key mutes
/// and unmutes. Going live takes the person's own key (a program's never opens the mic; muting is anyone's), and a key
/// held when the window loses the focus lets go. Shift+talk key switches between push-to-talk and an open mic.
/// Control+Shift+V says who's in voice and who's speaking; with access_voice_speakers on, someone starting to speak is
/// said as they start (at most once in ten seconds each). A call ringing is said, and `//voice answer` or `//voice
/// decline` answers it. Voice chat on or off and its volume are in Alt+0 (not where it's heard from: Access hears from the
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
            Reader?.Answer(v.Muted ? "Mic muted." : "Mic live.", keep: false);
            return true;
        }
        if (!RealInput.Person) { Reader?.Answer(RealInput.Why, keep: false); return true; }
        if (!v.Enabled) { Reader?.Answer("Voice chat is off: Alt+0 turns it on.", keep: false); return true; }
        if (v.OffHere) Reader?.Answer("Voice is off here: nobody hears you.", keep: false);
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
        : Host.Voice.Voice.Available ? "Voice chat is off: Alt+0 turns it on."
        : SoundWords.Missing(Host) is { } why ? $"No voice chat. {why}" : $"Voice chat can't run here: {Host.Voice.Voice.Status}.";

    /// <summary>Control+Shift+V and `//voice`: who's in voice and who's speaking now, your mic, where you are.</summary>
    void SayVoice()
    {
        if (Voice is not { } v) { Answer(VoiceOff()); return; }
        if (!v.Enabled) { Answer("Voice chat is off: Alt+0 turns it on."); return; }
        var me = _ops.Client?.Self.AgentID ?? UUID.Zero;
        var peers = v.Peers.Where(p => p.Id != me).ToList();
        string Name(UUID id) => Host!.Session.Names.DisplayName(id) ?? Host.Session.Names.Legacy(id) ?? "someone";
        var speaking = peers.Where(p => p.Speaking).Select(p => Name(p.Id)).ToList();
        var quiet = peers.Where(p => !p.Speaking).Select(p => Name(p.Id)).OrderBy(n => n).ToList();
        var where = v.CallInfo is { } call ? $"In a call with {call.With}." : v.OffHere ? "Voice is off here." : "Nearby voice.";
        var mic = v.ModeratorMuted ? "A moderator muted you." : v.Mode == VoiceMode.OpenMic ? (v.Muted ? "Open mic, muted." : "Open mic, live.")
            : $"Push to talk ({KeyName}).";
        var who = peers.Count == 0 ? "Nobody else in voice." : (speaking.Count > 0 ? $"Speaking: {string.Join(", ", speaking)}. " : "Nobody speaking. ")
            + (quiet.Count > 0 ? $"Also in voice: {string.Join(", ", quiet)}." : "");
        Answer($"{where} {who.Trim()} {mic}");
    }

    /// <summary>`//voice [answer | decline | who]`.</summary>
    Task VoiceWordsAsync(string words)
    {
        var w = words.Trim().ToLowerInvariant();
        var v = Voice;
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
        return Task.CompletedTask;
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
