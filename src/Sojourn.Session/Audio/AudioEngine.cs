using Sojourn.Session.Voice;

namespace Sojourn.Session.Audio;

/// <summary>
/// The one audio engine (sojourn_voice.dll) and its devices, shared by voice chat and world audio: whichever starts
/// first opens them. Voice wants the mic as well; world audio alone opens only the speaker, and if voice comes later
/// the devices reopen with the mic.
///
/// Devices: the person's choice (<see cref="Chosen"/>, Settings menu > sound) when it's connected, else the headset's
/// pair in VR (Link's "Oculus Virtual Audio Device") and the system's defaults on the desktop; a new choice reopens
/// them at once. The shim follows Windows from there (audio_io.cpp): the default as it moves, a device unplugged and
/// plugged back, nothing there at all; nothing here waits on a device.
/// SOJOURN_VOICE_MIC / SOJOURN_VOICE_SPEAKER name others (part of a name); for tests SOJOURN_VOICE_MIC_FILE plays a
/// WAV as the mic (once; loop:PATH repeats) and SOJOURN_VOICE_OUT_FILE writes what's heard to a WAV, the latter on a
/// local grid only: other residents' voices are never recorded. SOJOURN_TEST_REFUSE_SPEAKER / SOJOURN_TEST_DEFAULT_SPEAKER
/// (part of a name) make an output refuse to start, and "the default" open another, for the switch test.
/// </summary>
public static class AudioEngine
{
    static readonly object Lock = new();
    static bool _inited, _ok, _started, _withMic, _headset;
    static string? _error, _loginUri;
    static Action<string>? _log;

    /// <summary>The person's chosen microphone and speakers by name (null: automatic), for the headset (true) or the
    /// desktop.</summary>
    public static Func<bool, (string? Mic, string? Speakers)>? Chosen { get; set; }

    public static bool Headset => _headset;

    static readonly (long At, List<Device> List)?[] _lists = new (long, List<Device>)?[2];

    /// <summary>A device Windows has: its name, whether it's the default, and its form (Windows' EndpointFormFactor).</summary>
    public sealed record Device(string Name, bool Default, int Form)
    {
        /// <summary>What it is, in words ("" when Windows doesn't say): an output to a TV or monitor, or a digital
        /// output, is told apart from speakers and headphones (the blind tester stepped from her headset onto her TV's
        /// HDMI output, which Windows names "4 - 2D HD LG TV (...)", and heard nothing, 2026-11-01).</summary>
        public string Kind => Form switch
        {
            1 => "speakers", 3 => "headphones", 5 => "a headset", 6 => "a handset", 2 => "a line output", 4 => "a microphone",
            9 => "a TV or monitor, over HDMI or DisplayPort", 8 => "a digital output (S/PDIF)", 7 => "a digital output",
            0 => "a device on the network", _ => "",
        };

        /// <summary>Where it comes in the lists: speakers, headphones and headsets first, outputs to displays and
        /// digital outputs last.</summary>
        public int Rank => Form switch { 1 or 3 or 4 or 5 or 6 => 0, 0 or 7 or 8 or 9 => 2, _ => 1 };
    }

    /// <summary>The devices Windows has (active ones), likely ones first (Device.Rank), Windows' order within that, the
    /// default marked; asked again after 2 s at most (the menu asks as it draws).</summary>
    public static List<Device> List(bool capture)
    {
        long now = Environment.TickCount64;
        lock (_lists)
            if (_lists[capture ? 1 : 0] is { } had && now - had.At < 2000) return had.List;
        var list = new List<Device>();
        if (!Init(out _)) return list;
        try
        {
            foreach (var d in System.Text.Json.Nodes.JsonNode.Parse(VoiceNative.Devices(capture))!.AsArray())
                if ((string?)d!["name"] is { Length: > 0 } n) list.Add(new(n, (bool?)d["default"] == true, (int?)d["form"] ?? 10));
        }
        catch { }
        list = list.OrderBy(d => d.Rank).ToList();   // stable: Windows' order within a rank
        string? changed = null;
        lock (_lists)
        {
            _lists[capture ? 1 : 0] = (now, list);
            // what Windows has, logged when it changes: a TV's HDMI output comes and goes with the TV
            var line = Line(list);
            if (_logged[capture ? 1 : 0] is { } was && was != line) changed = line;
            _logged[capture ? 1 : 0] = line;
        }
        if (changed != null) _log?.Invoke($"audio: {(capture ? "microphones" : "outputs")} now: {changed}");
        return list;
    }

    static readonly string?[] _logged = new string?[2];

    static string Line(List<Device> list) => list.Count == 0 ? "none" : string.Join("; ", list.Select(d =>
        d.Name + (d.Kind.Length > 0 || d.Default ? $" ({string.Join(", ", new[] { d.Kind, d.Default ? "the default" : "" }.Where(x => x.Length > 0))})" : "")));

    /// <summary>The devices in a line for the log: what each is, the default marked.</summary>
    public static string DeviceLine(bool capture) => Line(List(capture));

    /// <summary>The devices again with the person's choice (after they changed it), if they're open.</summary>
    public static void Reopen()
    {
        lock (Lock)
        {
            if (!_ok || !_started || _loginUri == null) return;
            var (capture, speaker) = Devices(_loginUri, _headset);
            if (!_withMic && !capture.StartsWith("file:") && !capture.StartsWith("loop:")) capture = "none";
            VoiceNative.sv_audio_start(capture, speaker);
            (_asked[0], _asked[1]) = (capture, speaker);
            _log?.Invoke($"audio: now {(capture == "none" ? "no mic" : "mic " + Describe(capture))}, speaker {Describe(speaker)}");
        }
    }

    /// <summary>Loads and starts the engine (once); false and why if the DLL isn't there or won't start.</summary>
    public static bool Init(out string? error)
    {
        lock (Lock)
        {
            if (!_inited)
            {
                _inited = true;
                // the shim's warnings too (a device opened, what it took: the person's log tells a silent one from
                // there), into the session's log once StartDevices gives it; libwebrtc's own only as errors
                VoiceNative.Log ??= (level, msg) =>
                {
                    if (level < 2 || (level < 3 && msg.StartsWith("webrtc:"))) return;
                    if (_log is { } log) log("audio: " + msg); else Console.Error.WriteLine("audio: " + msg);
                };
                _ok = VoiceNative.Init(out _error);
                // tests (tools/audio.py switch): an output that refuses to start, and what "the default" opens
                if (_ok && Environment.GetEnvironmentVariable("SOJOURN_TEST_REFUSE_SPEAKER") is { Length: > 0 } refuse)
                    VoiceNative.sv_test_device(1, 4, "name:" + refuse);
                if (_ok && Environment.GetEnvironmentVariable("SOJOURN_TEST_DEFAULT_SPEAKER") is { Length: > 0 } fake)
                    VoiceNative.sv_test_device(1, 3, "name:" + fake);
            }
            error = _error;
            return _ok;
        }
    }

    /// <summary>Opens the devices if they aren't open, or reopens them with the mic when it's wanted now.</summary>
    public static void StartDevices(string loginUri, bool headset, bool mic, Action<string> log)
    {
        lock (Lock)
        {
            if (!_ok || (_started && (_withMic || !mic))) return;
            (_loginUri, _headset, _log) = (loginUri, headset, log);
            var (capture, speaker) = Devices(loginUri, headset);
            if (!mic && !capture.StartsWith("file:") && !capture.StartsWith("loop:")) capture = "none";
            VoiceNative.sv_audio_start(capture, speaker);
            (_asked[0], _asked[1]) = (capture, speaker);
            _started = true;
            _withMic = mic;
            log($"audio: {(capture == "none" ? "no mic" : "mic " + Describe(capture))}, speaker {Describe(speaker)}");
        }
    }

    static string Describe(string spec) => spec == "" ? "the default device" : spec;

    // the spec each direction was last given (0 the mic, 1 the speaker), for CheckAsync
    static readonly string?[] _asked = new string?[2];

    /// <summary>What a direction is doing (the shim's sv_device_state): the spec it was given, its state ("open",
    /// "silent", "failed", "lost", "starting", "file", "none"), the device open, why the device asked for isn't the one
    /// open, whether it's on the default meanwhile, how much sound the device has taken (or given) since it opened;
    /// for the output, Windows' volume and mute for the device and for this app on it (-1: not known).</summary>
    public sealed record DeviceState(string Spec, string State, string Name, string Error, bool Fallback, int MovedMs,
        float Volume = -1, bool Muted = false, float AppVolume = -1, bool AppMuted = false)
    {
        /// <summary>Why the device may be silent though it's playing, in words; null when nothing says so.</summary>
        public string? Quiet => Muted ? "Windows has it muted"
            : AppMuted ? "this app is muted on it in Windows' volume mixer"
            : Volume is >= 0 and < 0.05f ? $"Windows has its volume at {Math.Round(Volume * 100)}%"
            : AppVolume is >= 0 and < 0.05f ? $"this app's volume on it in Windows' volume mixer is {Math.Round(AppVolume * 100)}%"
            : null;
    }

    /// <summary>The direction's state; null from a shim without the report, or before the engine starts.</summary>
    public static DeviceState? State(bool capture)
    {
        if (!_ok || VoiceNative.DeviceState(capture) is not { } json) return null;
        try
        {
            var o = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            return new DeviceState((string?)o["spec"] ?? "", (string?)o["state"] ?? "", (string?)o["name"] ?? "",
                (string?)o["error"] ?? "", (bool?)o["fallback"] == true, (int?)o["moved_ms"] ?? 0,
                (float?)o["volume"] ?? -1, (bool?)o["muted"] == true, (float?)o["app_volume"] ?? -1, (bool?)o["app_muted"] == true);
        }
        catch { return null; }
    }

    /// <summary>How a change of device went: the device open (null where it can't be told: a test's file, an older
    /// shim), or why it didn't happen.</summary>
    public sealed record DeviceCheck(bool Ok, string? Device, string? Why);

    /// <summary>After a change of device (Reopen): waits, 3 s at most, until the direction has opened what it was
    /// asked for and, for the speaker, the device is taking sound. A device that won't start leaves the shim on the
    /// default meanwhile, which is not what was asked: that's a failure here, with the reason in words.</summary>
    public static async Task<DeviceCheck> CheckAsync(bool capture, CancellationToken ct = default)
    {
        string? asked;
        lock (Lock) asked = _asked[capture ? 0 : 1];
        if (asked == null) return new(true, null, null);
        long deadline = Environment.TickCount64 + 3000;
        DeviceState? st;
        for (;;)
        {
            st = State(capture);
            if (st == null) return new(true, null, null);
            if (st.Spec == asked)
            {
                if (st.State is "file" or "none") return new(true, null, null);
                if (st.State == "open" && st.Fallback) return new(false, st.Name, Reason(st.Error));
                // 50 ms taken: the device's clock is running, so what's mixed reaches it
                if (st.State == "open" && (capture || st.MovedMs >= 50)) return new(true, st.Name, null);
            }
            if (Environment.TickCount64 > deadline) break;
            await Task.Delay(50, ct);
        }
        string why = st.Spec != asked ? "it didn't start within 3 seconds" : st.State switch
        {
            "silent" or "open" => "it takes no sound",
            "failed" or "lost" => Reason(st.Error),
            _ => "it didn't start within 3 seconds",
        };
        return new(false, null, why);
    }

    /// <summary>What reaches the open output over ms (Windows' loopback of it) against what was sent meanwhile, mean
    /// power in dBFS; null if it can't be told (an older shim, no output open, loopback refused).</summary>
    public static async Task<(double Heard, double Sent)?> ProbeAsync(int ms)
    {
        if (!_ok) return null;
        var json = await Task.Run(() => VoiceNative.OutputProbe(ms));
        try
        {
            var o = json == null ? null : System.Text.Json.Nodes.JsonNode.Parse(json);
            return o?["heard_db"] is { } h && o["sent_db"] is { } s ? ((double)h, (double)s) : null;
        }
        catch { return null; }
    }

    /// <summary>A short, soft two-note chime on the output (the shim's local stream 7), so a person who changed it
    /// hears where sound comes out now; gain 1 is about -20 dBFS.</summary>
    public static unsafe void Chime(float gain)
    {
        if (!_ok || gain <= 0) return;
        const int rate = 48000, note = rate * 12 / 100;
        var pcm = new short[note * 2];
        for (int i = 0; i < pcm.Length; i++)
        {
            int k = i % note;
            double f = i < note ? 660 : 990, env = Math.Min(1, Math.Min(k / 480.0, (note - k) / 2400.0));
            pcm[i] = (short)(Math.Sin(2 * Math.PI * f * i / rate) * env * Math.Min(gain, 2) * 0.1 * 32767);
        }
        fixed (short* p = pcm) VoiceNative.sv_play(7, p, pcm.Length, rate, 1);
    }

    /// <summary>The shim's reason a device isn't in use ("NAME: Initialize failed 0x8889000a", "NAME isn't connected"),
    /// in words, its code kept for the log.</summary>
    public static string Reason(string error)
    {
        var e = error.ToLowerInvariant();
        string? code = System.Text.RegularExpressions.Regex.Match(e, "0x[0-9a-f]{8}") is { Success: true } m ? m.Value : null;
        string words = code switch
        {
            "0x8889000a" => "another program is using it exclusively",
            "0x88890008" => "it refused the sound format",
            "0x88890004" => "it went away",
            "0x80070005" => "Windows refused access to it",
            not null => "its driver refused to play",
            _ when e.Contains("isn't connected") => "it isn't connected",
            _ when e.Contains("can't activate") => "Windows couldn't open it",
            _ when e.Length == 0 => "it didn't start",
            _ => "it wouldn't start",
        };
        return code != null ? $"{words}, error {code}" : words;
    }

    /// <summary>The device Windows has now that a saved name means: the same name, or the same apart from the number
    /// Windows gives a USB device by its port ("Speakers (3- EPOS PC 8 USB)" plugged in elsewhere is "Speakers (4- EPOS
    /// PC 8 USB)"); null if none.</summary>
    public static string? Find(bool capture, string name)
    {
        var list = List(capture);
        if (list.Any(d => d.Name == name)) return name;
        var loose = WithoutUsbNumber(name);
        return list.Where(d => WithoutUsbNumber(d.Name) == loose).Select(d => d.Name).FirstOrDefault();
    }

    static string WithoutUsbNumber(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"\((\d+)- ", "(");

    static (string Mic, string Speaker) Devices(string loginUri, bool headset)
    {
        string mic = headset ? "name:Oculus Virtual Audio Device" : "", speaker = mic;
        if (Chosen?.Invoke(headset) is var (cm, cs))
        {
            // A device that isn't connected now (unplugged, Link off) leaves it automatic rather than silent. On the
            // desktop automatic is the default, which the shim falls back to by itself while a device asked for is away,
            // and it goes back to that device when it's plugged in; in the headset automatic is the Oculus pair.
            string? Pick(string name, bool capture, string what)
            {
                if (Find(capture, name) is { } now) return "name:" + now;
                _log?.Invoke($"audio: the chosen {what} ({name}) {(capture ? "isn't" : "aren't")} connected{(headset ? "" : ": the default until then")}");
                return headset ? null : "name:" + name;
            }
            if (cm != null && Pick(cm, true, "microphone") is { } pm) mic = pm;
            if (cs != null && Pick(cs, false, "speakers") is { } ps) speaker = ps;
        }
        if (Environment.GetEnvironmentVariable("SOJOURN_VOICE_MIC") is { Length: > 0 } m) mic = "name:" + m;
        if (Environment.GetEnvironmentVariable("SOJOURN_VOICE_SPEAKER") is { Length: > 0 } s) speaker = "name:" + s;
        if (Environment.GetEnvironmentVariable("SOJOURN_VOICE_MIC_FILE") is { Length: > 0 } mf)
            mic = mf == "none" ? "none" : mf.StartsWith("loop:") ? mf : "file:" + Path.GetFullPath(mf);
        bool local = loginUri.Contains("127.0.0.1") || loginUri.Contains("localhost");
        if (local && Environment.GetEnvironmentVariable("SOJOURN_VOICE_OUT_FILE") is { Length: > 0 } of)
            speaker = "file:" + Path.GetFullPath(of);
        if (Environment.GetEnvironmentVariable("SOJOURN_VOICE_MIC_FILE") is { Length: > 0 } && Environment.GetEnvironmentVariable("SOJOURN_VOICE_OUT_FILE") is not { Length: > 0 })
            speaker = "none";   // a test peer playing a file: don't play the others into this machine's speakers
        return (mic, speaker);
    }
}
