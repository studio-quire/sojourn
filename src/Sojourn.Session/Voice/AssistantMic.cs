using System.Text.Json.Nodes;

namespace Sojourn.Session.Voice;

/// <summary>
/// The assistant's ears and mouth through voice's audio engine, so one capture serves both and one echo canceller
/// covers everything played: while the assistant's button is held (plus the shim's 250 ms tail and 300 ms pre-roll)
/// the person's own mic, after echo cancellation and noise suppression, goes here and never to residents; the
/// assistant's spoken replies come back to be played in the device mix (local only, never into a voice channel). The
/// sidecar reaches both through the agent API (Bridge/BridgeServer.cs: GET /mic, POST /play). Nothing of other
/// residents' voices is in it: the tap is the mic alone.
/// </summary>
public sealed class AssistantMic : Bridge.IClientAudio, IDisposable
{
    const int Rate = 16000;
    readonly VoiceClient _voice;
    readonly Thread _thread;
    volatile bool _stop, _gate;
    readonly object _lock = new();
    List<short>? _collecting;
    DateTime _closedAt = DateTime.MaxValue;
    (short[] Pcm, DateTime At)? _ready;
    readonly SemaphoreSlim _readySignal = new(0);
    /// <summary>How long after the button comes up the tap still runs (the shim's tail) and a margin to drain it.</summary>
    static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(250 + 80);

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
                _collecting = new List<short>(Rate * 8);
                _ready = null;
                _closedAt = DateTime.MaxValue;
            }
            else if (!open && _gate) _closedAt = DateTime.UtcNow;
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
                if (_collecting != null && !_gate && DateTime.UtcNow - _closedAt > Tail)
                {
                    _ready = (_collecting.ToArray(), DateTime.UtcNow);
                    _collecting = null;
                    _readySignal.Release();
                }
            }
        }
    }

    // ---- Bridge.IClientAudio ----

    public JsonObject Info() => new()
    {
        ["available"] = _voice.Available, ["rate"] = Rate, ["device"] = _voice.MicDevice,
        ["processing"] = "AEC3, noise suppression, AGC2, high-pass (voice chat's audio engine)",
    };

    public async Task<JsonObject?> TakeUtteranceAsync(TimeSpan wait, CancellationToken ct)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            lock (_lock)
            {
                // a finished utterance from the last 10 s, taken once
                if (_ready is { } r && DateTime.UtcNow - r.At < TimeSpan.FromSeconds(10))
                {
                    _ready = null;
                    var bytes = new byte[r.Pcm.Length * 2];
                    Buffer.BlockCopy(r.Pcm, 0, bytes, 0, bytes.Length);
                    return new JsonObject { ["rate"] = Rate, ["ms"] = r.Pcm.Length * 1000 / Rate, ["pcm"] = Convert.ToBase64String(bytes) };
                }
                if (_collecting == null && _ready == null && DateTime.UtcNow > until) return null;
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
            var pcm = _collecting?.ToArray() ?? (_ready is { } r && DateTime.UtcNow - r.At < TimeSpan.FromSeconds(10) ? r.Pcm : Array.Empty<short>());
            int n = Math.Max(0, pcm.Length - from);
            var bytes = new byte[n * 2];
            if (n > 0) Buffer.BlockCopy(pcm, from * 2, bytes, 0, bytes.Length);
            return new JsonObject { ["rate"] = Rate, ["total"] = pcm.Length, ["collecting"] = _collecting != null, ["pcm"] = Convert.ToBase64String(bytes) };
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
