using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sojourn.Session.Assistant;
using Sojourn.Session.Control;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using NVector3 = System.Numerics.Vector3;

namespace Sojourn.Session.Audio;

/// <summary>
/// World audio's hookup to the client, kept out of Program.cs: the engine and the speaker (shared with voice, which
/// opens the mic too), the sounds, the music, the UI cues; the listener each frame; the settings (the menu's sound
/// section, SojournSettings) and SojournAudio on the agent API.
/// </summary>
public sealed class AudioWiring : IDisposable
{
    public readonly WorldSounds Sounds;
    public readonly MusicStream Music;
    readonly SoundBank _bank;
    readonly ParcelSound _parcel;
    /// <summary>The sound assets (an app's own sounds go in with SoundBank.AddLocal).</summary>
    public SoundBank Bank => _bank;
    /// <summary>Whether the own avatar's footsteps play as the viewer's (on by default); an app that plays its own
    /// (Sojourn Access's walk cues, by surface) turns these off.</summary>
    public bool OwnFootsteps { get => _cues.OwnSteps; set => _cues.OwnSteps = value; }
    /// <summary>The viewer's new-conversation sound for IMs (on by default); an app with its own (Sojourn Access's
    /// earcons) turns it off.</summary>
    public bool ConversationSound { get => _cues.NewConversation; set => _cues.NewConversation = value; }
    readonly Cues _cues;
    readonly AgentSession _session;
    readonly VrSettings _st;
    readonly bool _voiceOwnsEngine;
    readonly Action<string> _say;
    int _refresh;

    AudioWiring(AgentSession session, VrSettings st, bool voiceOwnsEngine, Action<string> say)
    {
        _session = session;
        _st = st;
        _voiceOwnsEngine = voiceOwnsEngine;
        _say = say;
        _bank = new SoundBank(session, say);
        Sounds = new WorldSounds(session, _bank, say);
        Music = new MusicStream(say);
        _parcel = new ParcelSound(session);
        Sounds.CanHear = _parcel.CanHear;
        if (float.TryParse(Environment.GetEnvironmentVariable("SOJOURN_TEST_SWITCH_CHIME"), System.Globalization.CultureInfo.InvariantCulture, out var chime))
            SwitchChime = () => chime;   // tests: the chime and its check without Sojourn Access
        _cues = new Cues(session, Sounds, _parcel.CanHear);
        Sounds.Tick = dt =>
        {
            _cues.Tick(dt);
            if (++_refresh % 25 != 0) return;   // twice a second: the parcel and its stream
            _parcel.Refresh();
            if (_parcel.MusicUrl is { } url) Music.Want(url);
        };
        Apply();
    }

    public static AudioWiring? Start(AgentSession session, SessionOptions opt, bool headset, Voice.VoiceClient? voice, VrSettings st, ILogger log)
    {
        if (!opt.Sounds) return null;
        void Say(string m) => log.LogInformation("{Msg}", m);
        if (!AudioEngine.Init(out var error))
        {
            Say($"audio: off ({error})");
            return null;
        }
        AudioEngine.StartDevices(opt.LoginUri, headset, mic: false, Say);
        Say($"audio: outputs: {AudioEngine.DeviceLine(false)}; microphones: {AudioEngine.DeviceLine(true)}");
        var a = new AudioWiring(session, st, voice is { Available: true }, Say);
        var hooked = new System.Runtime.CompilerServices.ConditionalWeakTable<LibreMetaverse.GridClient, object>();
        void Hook(LibreMetaverse.GridClient c)
        {
            if (hooked.TryGetValue(c, out _)) return;
            hooked.Add(c, new object());
            a.Sounds.Hook(c);
            a._cues.Hook(c);
            c.Self.TeleportProgress += (_, e) =>
                a.Music.Ducked = e.Status is LibreMetaverse.TeleportStatus.Start or LibreMetaverse.TeleportStatus.Progress;
        }
        lock (AgentSession.ClientHooks) AgentSession.ClientHooks.Add(Hook);
        if (session.Client is { } now) Hook(now);
        if (voice is { Available: true }) voice.IncomingChanged += call => { if (call != null) a._cues.Ringing(); };
        session.Api.Register(new AudioApi(session, a));
        return a;
    }

    /// <summary>The saved settings put to the engine: the device's volume (everything), world sounds, music.</summary>
    public void Apply()
    {
        Voice.VoiceNative.sv_set_output_volume(Math.Clamp(_st.AudioVolume, 0, 2));
        Sounds.SoundsVolume = _st.SoundsVolume;
        Music.Volume = _st.MusicVolume;
        Music.On = _st.Music;
    }

    // ---- the listener ----

    public void Listen(CameraPose? pose)
    {
        if (pose is { } p) Listen(p.Region, p.Position, p.Forward, NVector3.UnitZ);
    }

    public void Listen(VrView? view)
    {
        if (view is { } v) Listen(v.Region, v.Position, v.Forward, v.Up);
    }

    void Listen(ulong region, NVector3 pos, NVector3 forward, NVector3 up)
    {
        Sounds.SetListener(region, pos, forward, up);
        if (_session.Client?.Network.CurrentSim is { } sim && sim.Handle == region) Sounds.SetUnderwater(pos.Z < sim.WaterHeight);
    }

    // ---- settings: the menu's sound section and SojournSettings ----

    static string Pct(float v) => $"{v * 100:0}%";

    static float Step(float now, int dir)
    {
        var v = VrSettings.AudioVolumes;
        if (dir == 0) dir = 1;
        int i = Array.FindIndex(v, x => x >= now - 0.01f);
        if (i < 0) i = v.Length - 1;
        else if (v[i] > now + 0.01f && dir > 0) i--;
        return v[Math.Clamp(i + dir, 0, v.Length - 1)];
    }

    string MusicNote()
    {
        if (!_st.Music) return Music.ParcelUrl.Length > 0 ? "this parcel has music" : "";
        if (Music.Url == null) return Music.ParcelUrl.Length > 0 ? "" : "none on this parcel";
        return Music.State switch
        {
            "playing" => Music.Title ?? "playing",
            "connecting" or "buffering" or "reconnecting" => Music.State + "...",
            var s => s,
        };
    }

    public void Settle(ISettingsMenu? menu, SettingsApi api)
    {
        if (menu != null) Music.Changed += menu.Changed;
        if (menu != null) menu.SoundRows = () =>
        {
            var rows = new List<(MenuRow, Action<int>)>
            {
                (new MenuRow("volume", Pct(_st.AudioVolume), true), d => { _st.AudioVolume = Step(_st.AudioVolume, d); Apply(); }),
                (new MenuRow("sounds", Pct(_st.SoundsVolume), true), d => { _st.SoundsVolume = Step(_st.SoundsVolume, d); Apply(); }),
                (MenuRow.Switch("music", _st.Music, note: MusicNote()), _ => { _st.Music = !_st.Music; Apply(); }),
            };
            if (_st.Music)
                rows.Add((new MenuRow("music volume", Pct(_st.MusicVolume), true), d => { _st.MusicVolume = Step(_st.MusicVolume, d); Apply(); }));
            rows.Add((new MenuRow("speakers", DeviceLabel(false), true), d => SetDevice(false, Cycle(false, d))));
            if (_voiceOwnsEngine) rows.Add((new MenuRow("microphone", DeviceLabel(true), true), d => SetDevice(true, Cycle(true, d))));
            return rows;
        };
        static JsonNode N(float f) => JsonValue.Create(MathF.Round(f, 2));
        api.Add("volume", new("sound", "the volume of everything heard: world sounds, music, voice and spoken replies (1: as it comes)", "0-2",
            () => N(_st.AudioVolume), SettingsApi.Number("volume", 0, 2, "", f => { _st.AudioVolume = f; Apply(); })));
        api.Add("sounds_volume", new("sound", "objects', gestures' and other world sounds, footsteps and alert sounds (1: the viewer's own level)", "0-2",
            () => N(_st.SoundsVolume), SettingsApi.Number("sounds_volume", 0, 2, "", f => { _st.SoundsVolume = f; Apply(); })));
        api.Add("music", new("sound", "the parcel's music stream", "on, off",
            () => JsonValue.Create(_st.Music ? "on" : "off"), SettingsApi.Switch("music", on => { _st.Music = on; Apply(); })));
        api.Add("music_volume", new("sound", "the parcel's music (1: the viewer's own level)", "0-2",
            () => N(_st.MusicVolume), SettingsApi.Number("music_volume", 0, 2, "", f => { _st.MusicVolume = f; Apply(); })));
        api.Add("speakers", new("sound", "where everything is heard, for the desktop or the headset (whichever is in use): auto (the system's default, "
            + "the headset's own in the headset) or a device by its name (part of it will do; SojournAudio devices lists them)", "auto, a device's name",
            () => JsonValue.Create(Device(false) ?? "auto"), v => SetDeviceByWords(false, v.ToString()))
            { SetAsync = (v, ct) => SetDeviceCheckedAsync(false, v.ToString(), ct) });
        if (_voiceOwnsEngine)
            api.Add("microphone", new("sound", "the mic voice chat and the assistant hear, for the desktop or the headset: auto or a device by its name",
                "auto, a device's name", () => JsonValue.Create(Device(true) ?? "auto"), v => SetDeviceByWords(true, v.ToString()))
                { SetAsync = (v, ct) => SetDeviceCheckedAsync(true, v.ToString(), ct) });
    }

    // ---- the devices: chosen apart for the desktop and the headset ----

    string? Device(bool mic) => (AudioEngine.Headset, mic) switch
    {
        (true, true) => _st.HeadsetMicrophone, (true, false) => _st.HeadsetSpeakers, (false, true) => _st.Microphone, _ => _st.Speakers,
    };

    void SetDevice(bool mic, string? name)
    {
        switch (AudioEngine.Headset, mic)
        {
            case (true, true): _st.HeadsetMicrophone = name; break;
            case (true, false): _st.HeadsetSpeakers = name; break;
            case (false, true): _st.Microphone = name; break;
            default: _st.Speakers = name; break;
        }
        AudioEngine.Reopen();
    }

    /// <summary>The menu's value: automatic, or the chosen device by the part of its name that tells it apart (Windows
    /// names them "Speakers (the hardware)": the hardware, unless another device has the same).</summary>
    string DeviceLabel(bool mic)
    {
        if (Device(mic) is not { } n) return "automatic";
        static string Inner(string name)
        {
            int open = name.IndexOf('('), close = name.LastIndexOf(')');
            return open > 0 && close > open + 1 ? name[(open + 1)..close] : name;
        }
        var inner = Inner(n);
        return AudioEngine.List(mic).Count(d => Inner(d.Name) == inner) > 1 ? n : inner;
    }

    /// <summary>Automatic, then each device Windows has, round (the stepper's either way).</summary>
    string? Cycle(bool mic, int dir)
    {
        var names = new List<string?> { null };
        names.AddRange(AudioEngine.List(mic).Select(d => (string?)d.Name));
        int i = names.IndexOf(Device(mic) is { } n ? AudioEngine.Find(mic, n) ?? n : null);
        return names[((i < 0 ? 0 : i) + (dir < 0 ? -1 : 1) + names.Count) % names.Count];
    }

    /// <summary>The setting's change through the agent API (Sojourn Access's rows and Control+Shift+O, the assistant):
    /// the device chosen, then checked (AudioEngine.CheckAsync); one that doesn't start puts the choice back as it was
    /// and is refused with why, so "set" never answers for a device that isn't playing.</summary>
    async Task<string?> SetDeviceCheckedAsync(bool mic, string words, CancellationToken ct)
    {
        var before = Device(mic);
        if (SetDeviceByWords(mic, words) is { } refused) return refused;
        var check = await AudioEngine.CheckAsync(mic, ct);
        LastNote = null;
        if (check.Ok && !mic && check.Device != null && SwitchChime?.Invoke() is > 0 and var gain)
        {
            // what Windows passes on to the device, while a chime plays there: sound sent but none reaching the
            // device (and no mute or volume to explain it) means it isn't playing, whatever Windows said
            var probe = AudioEngine.ProbeAsync(700);
            AudioEngine.Chime(gain);
            var quiet = AudioEngine.State(false)?.Quiet;
            if (await probe is var (heard, sent))
            {
                _say($"audio: {check.Device}: {heard:0.0} dBFS reached it, {sent:0.0} sent{(quiet != null ? $" ({quiet})" : "")}");
                if (sent > -70 && heard < sent - 40 && quiet == null)
                    check = new(false, check.Device, "Windows passed none of the sound on to it");
            }
            LastNote = quiet;
        }
        if (check.Ok)
        {
            _say($"audio: {(mic ? "microphone" : "speakers")} now {check.Device ?? Device(mic) ?? "the default"}");
            return null;
        }
        var chosen = Device(mic) ?? "the default device";
        _say($"audio: {chosen} didn't start ({check.Why}); back to {before ?? "the default"}");
        SetDevice(mic, before);
        var back = await AudioEngine.CheckAsync(mic, CancellationToken.None);
        var now = back.Ok ? back.Device ?? before ?? "the default device" : null;
        return $"{chosen} didn't start: {check.Why}. " +
               (now != null ? $"{(mic ? "The microphone stays" : "Sound stays")} on {now}." : $"Going back didn't start either: {back.Why}.");
    }

    /// <summary>The gain of a chime played on a newly chosen output, which also lets the change be checked by what
    /// reaches the device (null or 0: none; Sojourn Access gives its own sounds' volume).</summary>
    public Func<float>? SwitchChime { get; set; }

    /// <summary>After an output change that went through: why it may still be silent (Windows has it muted, say), or
    /// null.</summary>
    public string? LastNote { get; private set; }

    string? SetDeviceByWords(bool mic, string words)
    {
        words = words.Trim();
        if (words.Length == 0 || words.Equals("auto", StringComparison.OrdinalIgnoreCase) || words.Equals("automatic", StringComparison.OrdinalIgnoreCase)
            || words.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            SetDevice(mic, null);
            return null;
        }
        var all = AudioEngine.List(mic);
        var hit = all.Where(d => d.Name.Equals(words, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count == 0) hit = all.Where(d => d.Name.Contains(words, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count != 1)
            return $"{(mic ? "microphone" : "speakers")}: {(hit.Count == 0 ? "no device" : "several devices")} named like that; " +
                   $"they are: {string.Join("; ", (hit.Count == 0 ? all : hit).Select(d => d.Name))}";
        SetDevice(mic, hit[0].Name);
        return null;
    }

    /// <summary>The devices there are and which are in use, for SojournAudio.</summary>
    public JsonObject DevicesStatus()
    {
        JsonArray Of(bool mic) => new(AudioEngine.List(mic).Select(d =>
            (JsonNode)new JsonObject { ["name"] = d.Name, ["default"] = d.Default, ["kind"] = d.Kind }).ToArray());
        return new JsonObject
        {
            ["for"] = AudioEngine.Headset ? "the headset" : "the desktop",
            ["speakers"] = Of(false), ["microphones"] = Of(true),
            ["chosen_speakers"] = Device(false) ?? "auto", ["chosen_microphone"] = Device(true) ?? "auto",
        };
    }

    /// <summary>What's playing, for SojournAudio.</summary>
    public JsonObject Status()
    {
        var names = _session.Names;
        var sounds = new JsonArray();
        foreach (var (sound, obj, owner, loop, dist, type) in Sounds.Snapshot())
        {
            var o = new JsonObject { ["sound_id"] = sound.ToString(), ["kind"] = type.ToString().ToLowerInvariant() };
            if (_session.Ears.Name(sound) is { } n) o["name"] = n;
            if (obj != LibreMetaverse.UUID.Zero) o["object_id"] = obj.ToString();
            if (owner != LibreMetaverse.UUID.Zero) { o["owner_id"] = owner.ToString(); if (names.Legacy(owner) is { } on) o["owner"] = on; }
            if (loop) o["loop"] = true;
            o["dist"] = dist;
            sounds.Add(o);
        }
        var music = new JsonObject { ["on"] = _st.Music, ["state"] = Music.State };
        if (Music.ParcelUrl.Length > 0) music["parcel_url"] = Music.ParcelUrl;
        if (Music.Url is { } u) music["url"] = u;
        if (Music.Title is { } t) music["title"] = t;
        if (Music.Format is { } f) music["format"] = f;
        if (_parcel.ParcelName is { } pn) music["parcel"] = pn;
        return new JsonObject { ["sounds"] = sounds, ["playing"] = sounds.Count, ["music"] = music, ["mixer"] = JsonNode.Parse(AudioNative.Stats()),
            ["engine"] = JsonNode.Parse(Voice.VoiceNative.Stats()) };
    }

    public void Dispose()
    {
        _say("audio stats: " + AudioNative.Stats());
        Music.Dispose();
        Sounds.Dispose();
        _bank.Dispose();
        if (!_voiceOwnsEngine)
        {
            Voice.VoiceNative.sv_audio_stop();
            Voice.VoiceNative.sv_shutdown();
        }
    }
}

/// <summary>SojournAudio: what the person is hearing: the world's sounds playing now (nearest, loudest first) and the
/// parcel's music stream (its URL, the song's title as the station sends it, its state).</summary>
public sealed class AudioApi : LeapApi
{
    public override string Name => "SojournAudio";
    public override string Desc => "What the person hears: world sounds playing now and the parcel's music stream (its song title); volumes are in SojournSettings";
    public override bool NeedsSession => false;

    public AudioApi(AgentSession s, AudioWiring audio) : base(s)
    {
        Op("status", "-> {sounds: [{sound_id, name?, kind, object_id?, owner?, loop?, dist}], music: {on, state, parcel_url?, url?, title?, format?}}",
            _ => audio.Status());
        Op("sources", "-> {sources: [...]}: every sound source tracked, playing or waiting (muted, cutoff, distance, channel)",
            _ => new JsonObject { ["sources"] = audio.Sounds.Sources() });
        Op("devices", "-> {for, speakers: [{name, default, kind}], microphones: [...], chosen_speakers, chosen_microphone}: the audio "
            + "devices Windows has (kind: speakers, headphones, a headset, a TV or monitor, ...), likely ones first; choose with "
            + "SojournSettings speakers / microphone", _ => audio.DevicesStatus());
    }
}
