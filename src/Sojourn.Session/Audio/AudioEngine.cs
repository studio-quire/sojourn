using Sojourn.Session.Voice;

namespace Sojourn.Session.Audio;

/// <summary>
/// The one audio engine (sojourn_voice.dll) and its devices, shared by voice chat and world audio: whichever starts
/// first opens them. Voice wants the mic as well; world audio alone opens only the speaker, and if voice comes later
/// the devices reopen with the mic.
///
/// Devices: the person's choice (<see cref="Chosen"/>, Settings menu > sound) when it's connected, else the headset's
/// pair in VR (Link's "Oculus Virtual Audio Device") and the system's defaults on the desktop; a new choice reopens
/// them at once. SOJOURN_VOICE_MIC / SOJOURN_VOICE_SPEAKER name others (part of a name); for tests SOJOURN_VOICE_MIC_FILE plays a
/// WAV as the mic (once; loop:PATH repeats) and SOJOURN_VOICE_OUT_FILE writes what's heard to a WAV, the latter on a
/// local grid only: other residents' voices are never recorded.
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

    static readonly (long At, List<(string Name, bool Default)> List)?[] _lists = new (long, List<(string, bool)>)?[2];

    /// <summary>The devices Windows has (active ones), by name, the default marked; asked again after 2 s at most (the
    /// menu asks as it draws).</summary>
    public static List<(string Name, bool Default)> List(bool capture)
    {
        long now = Environment.TickCount64;
        lock (_lists)
            if (_lists[capture ? 1 : 0] is { } had && now - had.At < 2000) return had.List;
        var list = new List<(string, bool)>();
        if (!Init(out _)) return list;
        try
        {
            foreach (var d in System.Text.Json.Nodes.JsonNode.Parse(VoiceNative.Devices(capture))!.AsArray())
                if ((string?)d!["name"] is { Length: > 0 } n) list.Add((n, (bool?)d["default"] == true));
        }
        catch { }
        lock (_lists) _lists[capture ? 1 : 0] = (now, list);
        return list;
    }

    /// <summary>The devices again with the person's choice (after they changed it), if they're open.</summary>
    public static void Reopen()
    {
        lock (Lock)
        {
            if (!_ok || !_started || _loginUri == null) return;
            var (capture, speaker) = Devices(_loginUri, _headset);
            if (!_withMic && !capture.StartsWith("file:") && !capture.StartsWith("loop:")) capture = "none";
            VoiceNative.sv_audio_start(capture, speaker);
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
                VoiceNative.Log ??= (level, msg) => { if (level >= 3) Console.Error.WriteLine("audio: " + msg); };
                _ok = VoiceNative.Init(out _error);
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
            _started = true;
            _withMic = mic;
            log($"audio: {(capture == "none" ? "no mic" : "mic " + Describe(capture))}, speaker {Describe(speaker)}");
        }
    }

    static string Describe(string spec) => spec == "" ? "the default device" : spec;

    static (string Mic, string Speaker) Devices(string loginUri, bool headset)
    {
        string mic = headset ? "name:Oculus Virtual Audio Device" : "", speaker = mic;
        if (Chosen?.Invoke(headset) is var (cm, cs))
        {
            // a device that isn't connected now (unplugged, Link off) leaves it automatic rather than silent
            if (cm != null) { if (List(true).Any(d => d.Name == cm)) mic = "name:" + cm; else _log?.Invoke($"audio: the chosen microphone ({cm}) isn't connected"); }
            if (cs != null) { if (List(false).Any(d => d.Name == cs)) speaker = "name:" + cs; else _log?.Invoke($"audio: the chosen speakers ({cs}) aren't connected"); }
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
