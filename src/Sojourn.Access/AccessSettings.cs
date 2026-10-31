using System.Text.Json;
using System.Text.Json.Nodes;
using Sojourn.Session.Control;

namespace Sojourn.Access;

/// <summary>
/// Sojourn Access's own choices, kept in access-settings.json in its base dir (the session's own settings stay in
/// vr-settings.json there, apart from the VR client's): the last account shown on the login form (never a password),
/// read-out muted all or per source, timestamps in the histories. The switches are on SojournSettings (section
/// "access") so the assistant can change them when asked, as every other setting; nothing here touches the VR
/// client's menu.
/// </summary>
public sealed class AccessSettings
{
    public string? LastGrid { get; set; }
    public string? LastUser { get; set; }
    public string? LastStart { get; set; }
    /// <summary>No incoming lines read out (the NVDA add-on's F6); answers to keys still are.</summary>
    public bool MuteAll { get; set; }
    /// <summary>Sources not read out: "nearby", or a conversation's session id (the add-on's Shift+F6).</summary>
    public List<string> Muted { get; set; } = new();
    /// <summary>[hh:mm] before each line in the histories (off: Radegast's users find them tiring to hear).</summary>
    public bool Timestamps { get; set; }
    /// <summary>How confirmations, notices' keys, the ToS and typed answers take an answer that doesn't come from a key on
    /// a physical keyboard (a braille keyboard, Dragon, Voice Access, an on-screen keyboard, switch access): "refuse", or
    /// "hello", asking Windows Hello (face, fingerprint or PIN) and then taking it (Hello.cs). Physical keys answer either way.</summary>
    public string AnswersWithoutKey { get; set; } = "refuse";
    public bool ConfirmByHello => AnswersWithoutKey == "hello";
    public static readonly string[] AnswersChoices = { "refuse", "hello" };

    public void SetAnswersWithoutKey(string value)
    {
        AnswersWithoutKey = value;
        Save();
    }

    /// <summary>Voice chat's talk key (MainForm.Voice.cs): f4 (null), f12, scroll_lock or pause.</summary>
    public string? TalkKey { get; set; }
    /// <summary>Someone starting to speak in voice said as they start.</summary>
    public bool VoiceSpeakers { get; set; }

    /// <summary>Frames a second the view out of sight draws between pictures (Session's OffscreenRenderer); 0: only when
    /// a picture, a touch or a collision ray needs one.</summary>
    public double ViewRate { get; set; } = 2;
    /// <summary>Those frames' longer side, pixels.</summary>
    public int ViewSize { get; set; } = 640;

    /// <summary>Walk mode's cues (Walk/WalkMode.cs): for each, how it reaches the person ("both", "text", "sound" or
    /// "off") under walk_NAME, and how loud its sound is ("quiet", "medium", "loud", "full") under walk_NAME_volume;
    /// plus walk_turn_step, walk_turn_rate, walk_teleport_fallback. Kept only where changed from the defaults.</summary>
    public Dictionary<string, string> Walk { get; set; } = new();

    /// <summary>The cues and their defaults: restrained, since only a blind tester can judge the sound design.</summary>
    public static readonly (string Name, string Mode, string Label)[] WalkCues =
    {
        ("steps", "sound", "footsteps by surface (text: the surface when it changes)"),
        ("walls", "both", "a warning before you bump into something"),
        ("openings", "both", "openings and doors beside you as you pass"),
        ("edges", "both", "a drop or water ahead"),
        ("turns", "text", "where you face after a turn"),
        ("sonar", "both", "the sonar key's sweep (S in walk mode)"),
        ("follow", "text", "following someone: when they stop, turn away or go out of reach"),
        ("vehicle", "both", "driving: what's ahead and the speed when it changes a lot"),
        ("beacon", "both", "a beacon on where you're heading (a //go, walk to, a follow, the assistant's walk, or one you put on "
            + "someone or something; text: reaching it, or losing it)"),
        ("marks", "both", "doors, seats and touchable things around, sounding from where they are (O in walk mode)"),
    };
    public static readonly string[] WalkModes = { "both", "text", "sound", "off" };
    public static readonly string[] WalkLevels = { "quiet", "medium", "loud", "full" };
    static readonly float[] LevelGains = { 0.2f, 0.45f, 0.7f, 1f };
    /// <summary>Cues quieter than medium unless changed: the beacon repeats all through a walk.</summary>
    static readonly Dictionary<string, string> WalkDefaultLevels = new() { ["beacon"] = "quiet" };

    public string WalkMode(string cue) => Walk.TryGetValue(cue, out var v) ? v : WalkCues.FirstOrDefault(c => c.Name == cue).Mode ?? "both";
    public bool WalkText(string cue) => WalkMode(cue) is "both" or "text";
    public bool WalkSound(string cue) => WalkMode(cue) is "both" or "sound";
    public string WalkLevelName(string cue) => Walk.TryGetValue(cue + "_volume", out var v) ? v : WalkDefaultLevels.GetValueOrDefault(cue, "medium");
    public float WalkLevel(string cue) => WalkSound(cue) ? LevelGains[Math.Max(0, Array.IndexOf(WalkLevels, WalkLevelName(cue)))] : 0;

    /// <summary>The earcons (Sound/Earcons.cs): a sound per kind of thing arriving, so kinds can be told apart before
    /// (or without) the words, each off or at a level under earcon_KIND. Kept only where changed from the defaults.</summary>
    public Dictionary<string, string> Earcons { get; set; } = new();

    /// <summary>The kinds and their defaults: on (medium) only for what's addressed to the person and waits on them, as
    /// the viewer sounds a new IM and Radegast a notice; the rest off, since the screen reader says them all anyway.</summary>
    public static readonly (string Name, string Level, string Label)[] EarconKinds =
    {
        ("im", "medium", "an IM (not in the conversation you're in)"),
        ("mention", "medium", "a line naming you or holding one of your keywords, in any chat"),
        ("menu", "medium", "a script menu, text box, permission or experience question waiting for you"),
        ("offer", "medium", "an offer: a teleport, friendship, an item, a place, a group, a web page"),
        ("group", "off", "group chat (not in the one you're in)"),
        ("nearby", "off", "people's nearby chat, from where they are"),
        ("objects", "off", "what objects say nearby, from where they are"),
        ("friends", "off", "a friend coming online (rising) or going offline (falling)"),
        ("radar", "off", "someone arriving or leaving, when those are read out (Control+Shift+A), from where they are"),
        ("notice", "off", "other notices: group notices, payments, the region's messages"),
        ("sent", "off", "your own line sent, nearby or in a conversation"),
        ("mic", "medium", "the mic opening and closing in voice chat (the talk key held and let go, an open mic unmuted and muted)"),
    };
    public static readonly string[] EarconLevels = { "off", "quiet", "medium", "loud", "full" };

    public string EarconLevelName(string kind) => Earcons.TryGetValue(kind, out var v) ? v : EarconKinds.FirstOrDefault(k => k.Name == kind).Level ?? "off";
    /// <summary>The earcon's gain (0-1), 0 when off.</summary>
    public float EarconLevel(string kind) => Array.IndexOf(EarconLevels, EarconLevelName(kind)) is var i and > 0 ? LevelGains[i - 1] : 0;

    /// <summary>All of Sojourn Access's own sounds (walk cues, earcons, the beacon) together, apart from the world's sounds
    /// volume (world audio's App kind): 1 as made.</summary>
    public double SoundsVolume { get; set; } = 1;
    public static readonly string[] SoundsVolumes = { "25%", "50%", "75%", "100%", "150%", "200%" };
    public string SoundsVolumeName => $"{Math.Round(SoundsVolume * 100)}%";
    /// <summary>Degrees a tap of Left or Right turns (0: no steps, held turning only).</summary>
    public double WalkTurnStep => Walk.TryGetValue("turn_step", out var v) ? (v == "smooth" ? 0 : double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)) : 22.5;
    /// <summary>Degrees a second while Left or Right is held.</summary>
    public double WalkTurnRate => Walk.TryGetValue("turn_rate", out var v) ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : 90;
    /// <summary>A walk with no way around what's in the way ends in a teleport there (where the region allows it).</summary>
    public bool WalkTeleportFallback => !Walk.TryGetValue("teleport_fallback", out var v) || v == "on";

    /// <summary>Which of Access's own defaults for the session's settings (vr-settings.json, shared code with the VR client,
    /// whose defaults suit a person looking, not listening) have been put in place: 0 none yet, 1 the parcel's music at
    /// 0.15 and the world's sounds at 0.5 of the viewer's levels, under the screen reader (the user's first run on Second
    /// Life, 2026-10-29: Firestorm Social Island's music drowned NVDA until turned down to 0.15).</summary>
    public int SessionDefaults { get; set; }
    public const float MusicDefault = 0.15f, SoundsDefault = 0.5f;

    /// <summary>Puts in place the session defaults not yet applied, once each, then keeps them in the session's settings
    /// file. A value is changed only while it's still the VR client's default: one the person chose is theirs (and once
    /// applied, a later choice of the old default is theirs too).</summary>
    public void ApplySessionDefaults(VrSettings s, string vrSettingsPath)
    {
        if (SessionDefaults >= 1) return;
        var vr = new VrSettings();
        if (s.MusicVolume == vr.MusicVolume) s.MusicVolume = MusicDefault;
        if (s.SoundsVolume == vr.SoundsVolume) s.SoundsVolume = SoundsDefault;
        s.Save(vrSettingsPath);
        SessionDefaults = 1;
        Save();
    }

    string _path = "";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AccessSettings Load(string baseDir)
    {
        var path = Path.Combine(baseDir, "access-settings.json");
        AccessSettings s;
        try { s = File.Exists(path) ? JsonSerializer.Deserialize<AccessSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception) { s = new(); }
        s._path = path;
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception) { /* a setting not kept isn't worth stopping for */ }
    }

    public bool IsMuted(string source) { lock (Muted) return Muted.Contains(source); }

    /// <summary>Mutes or unmutes a source; true if it's muted now.</summary>
    public bool ToggleMuted(string source)
    {
        bool now;
        lock (Muted) { now = !Muted.Remove(source); if (now) Muted.Add(source); }
        Save();
        return now;
    }

    /// <summary>The switches on SojournSettings; changed: the app shows them again. view: the renderer out of sight,
    /// if one runs (its rate and size apply at once).</summary>
    public void Register(SettingsApi api, Action changed, Sojourn.Session.Offscreen.OffscreenRenderer? view = null,
        Sojourn.Session.Audio.WorldSounds? audio = null)
    {
        if (view != null)
        {
            (view.IdleHz, view.IdleSize) = (ViewRate, ViewSize);
            api.Add("access_view_rate", new SettingsApi.Setting("access", "Sojourn Access: frames a second the view out of sight draws "
                + "between pictures (it keeps touches on surfaces and the collision model current; more while the assistant works); "
                + "0 draws only when something needs it, for a slow PC", "0-10", () => ViewRate,
                SettingsApi.Number("access_view_rate", 0, 10, "", v => { view.IdleHz = ViewRate = Math.Round(v, 1); Save(); changed(); })));
            api.Add("access_view_size", new SettingsApi.Setting("access", "Sojourn Access: the longer side of those frames, pixels "
                + "(pictures for the assistant are drawn at their own size)", "256-1600", () => ViewSize,
                SettingsApi.Number("access_view_size", 256, 1600, " px", v => { view.IdleSize = ViewSize = (int)Math.Round(v); Save(); changed(); })));
        }
        foreach (var (cue, _, label) in WalkCues)
        {
            var name = cue;
            api.Add($"walk_{name}", new SettingsApi.Setting("access", $"Sojourn Access walk mode: {label}; how it reaches you: "
                + "both (a sound and a line for the screen reader), text, sound, or off", string.Join(", ", WalkModes), () => WalkMode(name),
                SettingsApi.Choice($"walk_{name}", WalkModes, i => { Walk[name] = WalkModes[i]; Save(); changed(); })));
            api.Add($"walk_{name}_volume", new SettingsApi.Setting("access", $"Sojourn Access walk mode: how loud the sound for {label} is",
                string.Join(", ", WalkLevels), () => WalkLevelName(name),
                SettingsApi.Choice($"walk_{name}_volume", WalkLevels, i => { Walk[name + "_volume"] = WalkLevels[i]; Save(); changed(); })));
        }
        foreach (var (kind, _, label) in EarconKinds)
        {
            var name = kind;
            api.Add($"earcon_{name}", new SettingsApi.Setting("access", $"Sojourn Access: the sound for {label} (the screen reader still "
                + "reads it as set by announce_*; F6 mutes the words, not these): off or how loud", string.Join(", ", EarconLevels),
                () => EarconLevelName(name), SettingsApi.Choice($"earcon_{name}", EarconLevels, i => { Earcons[name] = EarconLevels[i]; Save(); changed(); })));
        }
        if (audio != null) audio.AppVolume = (float)SoundsVolume;
        api.Add("access_sounds_volume", new SettingsApi.Setting("access", "Sojourn Access: the volume of its own sounds (walk cues, earcons, "
            + "the beacon) apart from the world's (sounds_volume) and music (music_volume)", string.Join(", ", SoundsVolumes),
            () => SoundsVolumeName, SettingsApi.Choice("access_sounds_volume", SoundsVolumes, i =>
            {
                SoundsVolume = double.Parse(SoundsVolumes[i].TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture) / 100;
                if (audio != null) audio.AppVolume = (float)SoundsVolume;
                Save();
                changed();
            })));
        string[] steps = { "smooth", "10", "15", "22.5", "30", "45", "90" }, rates = { "45", "90", "135", "180" };
        api.Add("walk_turn_step", new SettingsApi.Setting("access", "Sojourn Access walk mode: degrees a tap of Left or Right turns "
            + "(smooth: only held turning); held, it turns smoothly", string.Join(", ", steps),
            () => Walk.TryGetValue("turn_step", out var v) ? v : "22.5", SettingsApi.Choice("walk_turn_step", steps, i => { Walk["turn_step"] = steps[i]; Save(); changed(); })));
        api.Add("walk_turn_rate", new SettingsApi.Setting("access", "Sojourn Access walk mode: degrees a second while Left or Right is held",
            string.Join(", ", rates), () => Walk.TryGetValue("turn_rate", out var v) ? v : "90",
            SettingsApi.Choice("walk_turn_rate", rates, i => { Walk["turn_rate"] = rates[i]; Save(); changed(); })));
        api.Add("walk_teleport_fallback", new SettingsApi.Setting("access", "Sojourn Access: a walk (//go, walk to, the assistant's) "
            + "with no way around what's in the way teleports there instead, where the region allows it", "on, off", () => WalkTeleportFallback,
            SettingsApi.Switch("walk_teleport_fallback", on => { Walk["teleport_fallback"] = on ? "on" : "off"; Save(); changed(); })));
        // a safeguard against programs on this PC, the assistant's among them: any caller may set it back to refuse, and
        // only the person lowers it to hello, in Alt+0 by their own key or Windows Hello (MainForm.AnswersRow)
        api.Add("access_answers_without_key", new SettingsApi.Setting("access", "Sojourn Access: a confirmation, a notice's key, the ToS "
            + "or a typed answer that doesn't come from a key on a physical keyboard (a braille keyboard, dictation, an on-screen "
            + "keyboard, switch access): refuse it, or hello, asking Windows Hello (face, fingerprint or PIN) first. A physical key "
            + "answers either way. Only the person changes it to hello (Alt+0)", string.Join(", ", AnswersChoices), () => AnswersWithoutKey,
            v =>
            {
                int pick = -1;
                if (SettingsApi.Choice("access_answers_without_key", AnswersChoices, i => pick = i)(v) is { } bad) return bad;
                if (AnswersChoices[pick] == "hello" && AnswersWithoutKey != "hello")
                    return "access_answers_without_key: hello lowers a safeguard against programs, so the person turns it on themselves (Alt+0)";
                SetAnswersWithoutKey(AnswersChoices[pick]);
                changed();
                return null;
            }));
        api.Add("access_mute_all", new SettingsApi.Setting("access", "Sojourn Access: incoming lines not read out through the screen reader "
            + "(answers to the person's keys still are; F6)", "on, off", () => MuteAll,
            SettingsApi.Switch("access_mute_all", on => { MuteAll = on; Save(); changed(); })));
        api.Add("access_timestamps", new SettingsApi.Setting("access", "Sojourn Access: a time before each line in the chat histories",
            "on, off", () => Timestamps, SettingsApi.Switch("access_timestamps", on => { Timestamps = on; Save(); changed(); })));
        api.Add("access_muted_sources", new SettingsApi.Setting("access", "Sojourn Access: conversations not read out (Shift+F6 in one); "
            + "set [] to read them all again", "a list", () => new JsonArray(Muted.Select(m => (JsonNode)m).ToArray()), v =>
            {
                if (v is not JsonArray a) return "access_muted_sources is a list (only [] clears it)";
                if (a.Count > 0) return "only [] (read them all again); a conversation is muted from inside it with Shift+F6";
                lock (Muted) Muted.Clear();
                Save();
                changed();
                return null;
            }));
    }
}
