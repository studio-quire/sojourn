using System.Text.Json.Nodes;

namespace Sojourn.Session.Voice;

/// <summary>
/// The assistant's ears and mouth through voice's audio engine, so one capture serves both and one echo canceller
/// covers everything played: while the assistant's button is held (plus the shim's 250 ms tail and 300 ms pre-roll)
/// the person's own mic, after echo cancellation and noise suppression, goes here and never to residents; the
/// assistant's spoken replies come back to be played in the device mix (local only, never into a voice channel). The
/// sidecar reaches both through the agent API (Bridge/BridgeServer.cs: GET /mic, POST /play). Nothing of other
/// residents' voices is in it: the tap is the mic alone.
/// Finished utterances wait here for a couple of minutes until the sidecar takes them, each by when its press went
/// down and came up (the talk events' times): one said while the sidecar is still starting is heard once it's ready,
/// and a late request never takes a newer press's words.
/// </summary>
public sealed class AssistantMic : Bridge.IClientAudio, IDisposable
{
    const int Rate = 16000;
    readonly VoiceClient _voice;
    readonly Thread _thread;
    volatile bool _stop, _gate;
    readonly object _lock = new();
    List<short>? _collecting;
    double _openedAt, _closedUnix;
    DateTime _closedAt = DateTime.MaxValue;
    readonly List<(short[] Pcm, double OpenedAt, double ClosedAt, DateTime At)> _done = new();   // finished, not yet taken; oldest first
    short[]? _cutOff;   // an utterance Cut while the gate stayed open: what Live shows, finished, until the next one ends
    readonly SemaphoreSlim _readySignal = new(0);
    /// <summary>How long after the button comes up the tap still runs (the shim's tail) and a margin to drain it.</summary>
    static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(250 + 80);
    /// <summary>How long a finished utterance waits for the sidecar (it may be loading hearing), and how many.</summary>
    static readonly TimeSpan Keep = TimeSpan.FromMinutes(2);
    const int KeepAtMost = 8;
    /// <summary>A request without the press's times takes only the newest, and only this fresh (as it always did).</summary>
    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);

    public AssistantMic(VoiceClient voice)
    {
        _voice = voice;
        VoiceNative.sv_set_tap(1);
        _thread = new Thread(Run) { Name = "assistant mic", IsBackground = true };
        _thread.Start();
    }

    /// <summary>The assistant's button, from the frame loop.</summary>
    public void Gate(bool open)
    {
        lock (_lock)
        {
            if (open && !_gate)
            {
                _cutOff = null;
                if (_collecting != null) Finish();   // pressed again within the last one's tail: it ends here
                _collecting = new List<short>(Rate * 8);
                _openedAt = Sojourn.Core.Json.UnixNow();
                _closedAt = DateTime.MaxValue;
            }
            else if (!open && _gate) { _closedAt = DateTime.UtcNow; _closedUnix = Sojourn.Core.Json.UnixNow(); }
            _gate = open;
        }
        _voice.AssistantGate(open);
    }

    unsafe void Run()
    {
        var buf = new short[Rate / 10];
        while (!_stop)
        {
            Thread.Sleep(20);
            int n;
            fixed (short* p = buf) n = VoiceNative.sv_tap_read(p, buf.Length);
            lock (_lock)
            {
                if (_collecting != null && n > 0) _collecting.AddRange(buf.AsSpan(0, n));
                if (_collecting != null && !_gate && DateTime.UtcNow - _closedAt > Tail) Finish();
            }
        }
    }

    /// <summary>A request latched open by a tap (AssistantHost.TalkPress) ends now and the mic passes to something else in
    /// this same frame: dictation (<paramref name="reopen"/>: the gate stays open and dictation's utterance starts here)
    /// or voice's push-to-talk (the frame loop closes the gate next). The request ends here without the release's tail,
    /// so none of the words that follow run on into it, and a stream still reading it (Live) sees it end.</summary>
    public void Cut(bool reopen)
    {
        lock (_lock)
        {
            if (!_gate || _collecting == null) return;
            // closed a millisecond back: the events' times are whole milliseconds, and the talk_up and the next press's
            // down logged in this same one must find this utterance ending before that press (OfPress)
            double now = Sojourn.Core.Json.UnixNow();
            _closedUnix = now - 0.001;
            Finish();
            _cutOff = _done[^1].Pcm;
            if (!reopen) return;
            _collecting = new List<short>(Rate * 8);
            _openedAt = now;
            _closedAt = DateTime.MaxValue;
        }
    }

    void Finish()
    {
        _cutOff = null;
        _done.Add((_collecting!.ToArray(), _openedAt, _closedUnix, DateTime.UtcNow));
        _collecting = null;
        _done.RemoveAll(u => DateTime.UtcNow - u.At > Keep);
        if (_done.Count > KeepAtMost) _done.RemoveRange(0, _done.Count - KeepAtMost);
        _readySignal.Release();
    }

    /// <summary>Whether an utterance whose gate was open from <paramref name="opened"/> to <paramref name="closed"/> holds
    /// the press that went down at <paramref name="down"/> and up at <paramref name="up"/>: the gate follows the press's
    /// events within a frame, so the one before closed before this press went down, and the next opened after it came up.</summary>
    static bool OfPress(double opened, double closed, double down, double up) => closed >= down && opened <= up + 0.5;

    // ---- Bridge.IClientAudio ----

    public JsonObject Info() => new()
    {
        ["available"] = _voice.Available, ["rate"] = Rate, ["device"] = _voice.MicDevice,
        ["processing"] = "AEC3, noise suppression, AGC2, high-pass (voice chat's audio engine)",
    };

    public async Task<JsonObject?> TakeUtteranceAsync(TimeSpan wait, double? down, double? up, CancellationToken ct)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            lock (_lock)
            {
                _done.RemoveAll(u => DateTime.UtcNow - u.At > Keep);
                // the press asked for (the earliest that fits it), or without its times the newest from the last 10 s; taken once
                int i = down is { } d && up is { } u0
                    ? _done.FindIndex(u => OfPress(u.OpenedAt, u.ClosedAt, d, u0))
                    : _done.Count > 0 && DateTime.UtcNow - _done[^1].At < Fresh ? _done.Count - 1 : -1;
                if (i >= 0)
                {
                    var r = _done[i];
                    if (down == null) _done.Clear(); else _done.RemoveAt(i);
                    var bytes = new byte[r.Pcm.Length * 2];
                    Buffer.BlockCopy(r.Pcm, 0, bytes, 0, bytes.Length);
                    return new JsonObject { ["rate"] = Rate, ["ms"] = r.Pcm.Length * 1000 / Rate, ["pcm"] = Convert.ToBase64String(bytes) };
                }
                if (down is { } d1 && up is { } u1)
                {
                    // nothing of that press is still coming (none collecting for it, and it's past the frame its gate
                    // would have opened in): gone, or the press never opened the gate; no point waiting
                    bool coming = _collecting != null && OfPress(_openedAt, _gate ? double.MaxValue : _closedUnix, d1, u1);
                    if (!coming && Sojourn.Core.Json.UnixNow() - u1 > 0.5) return null;
                }
                else if (_collecting == null && DateTime.UtcNow > until) return null;
            }
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            await _readySignal.WaitAsync(left < TimeSpan.FromMilliseconds(100) ? left : TimeSpan.FromMilliseconds(100), ct);
        }
    }

    public JsonObject Live(int from)
    {
        lock (_lock)
        {
            // collecting, or just finished and not yet taken (the last of it, up to the release's tail)
            // (a request Cut short shows as finished, not running on into the dictation that took the mic from it)
            var pcm = _cutOff ?? _collecting?.ToArray() ?? (_done.Count > 0 && DateTime.UtcNow - _done[^1].At < Fresh ? _done[^1].Pcm : Array.Empty<short>());
            int n = Math.Max(0, pcm.Length - from);
            var bytes = new byte[n * 2];
            if (n > 0) Buffer.BlockCopy(pcm, from * 2, bytes, 0, bytes.Length);
            return new JsonObject { ["rate"] = Rate, ["total"] = pcm.Length, ["collecting"] = _cutOff == null && _collecting != null, ["pcm"] = Convert.ToBase64String(bytes) };
        }
    }

    public unsafe void Play(short[] pcm, int rate)
    {
        fixed (short* p = pcm) VoiceNative.sv_play(0, p, pcm.Length, rate, 1);
    }

    public void ClearPlay() => VoiceNative.sv_play_clear(0);

    public int QueuedMs() => VoiceNative.sv_play_queued_ms(0);

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
    }
}
