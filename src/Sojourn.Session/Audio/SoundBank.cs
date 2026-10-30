using System.Collections.Concurrent;
using LibreMetaverse;
using NVorbis;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Audio;

/// <summary>
/// Sound assets, ready to play: fetched from the grid (the session's asset path, cached on disk by LibreMetaverse),
/// decoded from Ogg Vorbis and handed to the native mixer, which resamples them to 48 kHz once. Decoding runs on one
/// below-normal thread, nearest-wanted first. What's held is bounded (the viewer keeps 40 buffers; here up to
/// <see cref="MaxBytes"/> of decoded audio, the longest-unused going first). A sound that can't be fetched or
/// decoded is left alone for two minutes before it's tried again.
/// </summary>
public sealed class SoundBank : IDisposable
{
    public enum State { Fetching, Ready, Failed }

    public sealed class Entry
    {
        public State State = State.Fetching;
        public int Native;           // sv_buf_load's id
        public long Bytes;
        public double Seconds;
        public DateTime LastWanted = DateTime.UtcNow, FailedAt;
        public float Priority;       // the highest a source has wanted it at, for the decode queue
        public bool Local;           // made by the client itself (AddLocal): never fetched, never let go
    }

    const long MaxBytes = 160L << 20;   // ~14 minutes of 48 kHz mono float
    readonly AgentSession _s;
    readonly Action<string> _log;
    readonly ConcurrentDictionary<UUID, Entry> _entries = new();
    readonly SemaphoreSlim _fetches = new(4);
    readonly List<(UUID Id, byte[] Data)> _decodeQueue = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _decoder;
    volatile bool _stop;
    long _held;

    public SoundBank(AgentSession s, Action<string> log)
    {
        _s = s;
        _log = log;
        _decoder = new Thread(DecodeLoop) { Name = "audio-decode", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _decoder.Start();
    }

    public long HeldBytes => Interlocked.Read(ref _held);
    public int Count => _entries.Count;

    /// <summary>The sound's entry, asking for it if it's new (or failed long enough ago). Any thread.</summary>
    public Entry Want(UUID id, float priority = 0)
    {
        var e = _entries.GetOrAdd(id, _ => new Entry { State = State.Failed, FailedAt = DateTime.MinValue });
        e.LastWanted = DateTime.UtcNow;
        if (priority > e.Priority) e.Priority = priority;
        if (e.State == State.Failed && DateTime.UtcNow - e.FailedAt > TimeSpan.FromMinutes(2))
        {
            lock (e)
            {
                if (e.State == State.Failed && DateTime.UtcNow - e.FailedAt > TimeSpan.FromMinutes(2))
                {
                    e.State = State.Fetching;
                    _ = FetchAsync(id, e);
                }
            }
        }
        return e;
    }

    /// <summary>A sound the client makes itself (Sojourn Access's walking cues), under an id of its own: ready at once,
    /// played like any other (WorldSounds.Trigger), never fetched or let go. Mono, any rate. Any thread.</summary>
    public unsafe void AddLocal(UUID id, float[] pcm, int rate)
    {
        if (pcm.Length == 0 || _entries.ContainsKey(id)) return;
        int native;
        fixed (float* p = pcm) native = AudioNative.sv_buf_load(p, pcm.Length, rate, 1);
        if (native <= 0) { _log($"audio: the mixer refused the local sound {id}"); return; }
        _entries[id] = new Entry
        {
            State = State.Ready, Native = native, Local = true, Seconds = pcm.Length / (double)rate,
            Bytes = (long)Math.Ceiling(pcm.Length * 48000.0 / rate) * 4,
        };
    }

    /// <summary>The native buffer for a sound that's ready, else 0 (and it's asked for).</summary>
    public int Ready(UUID id, float priority = 0)
    {
        var e = Want(id, priority);
        return e.State == State.Ready ? e.Native : 0;
    }

    async Task FetchAsync(UUID id, Entry e)
    {
        await _fetches.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var asset = await _s.World.AssetAsync(_s.GridKey, id, AssetType.Sound, cts.Token);
            if (asset?.AssetData is not { Length: > 0 } data) { Fail(e, id, "not found"); return; }
            lock (_decodeQueue) _decodeQueue.Add((id, data));
            _wake.Set();
        }
        catch (Exception x) { Fail(e, id, x.Message); }
        finally { _fetches.Release(); }
    }

    void Fail(Entry e, UUID id, string why)
    {
        e.State = State.Failed;
        e.FailedAt = DateTime.UtcNow;
        _log($"audio: sound {id} not playable ({why})");
    }

    void DecodeLoop()
    {
        while (!_stop)
        {
            _wake.WaitOne(500);
            for (;;)
            {
                (UUID Id, byte[] Data) job;
                lock (_decodeQueue)
                {
                    if (_decodeQueue.Count == 0) break;
                    int best = 0;
                    for (int i = 1; i < _decodeQueue.Count; i++)
                        if (Pri(_decodeQueue[i].Id) > Pri(_decodeQueue[best].Id)) best = i;
                    job = _decodeQueue[best];
                    _decodeQueue.RemoveAt(best);
                }
                if (!_entries.TryGetValue(job.Id, out var e)) continue;
                try { Load(job.Id, job.Data, e); }
                catch (Exception x) { Fail(e, job.Id, x.Message); }
            }
        }
    }

    float Pri(UUID id) => _entries.TryGetValue(id, out var e) ? e.Priority : 0;

    unsafe void Load(UUID id, byte[] ogg, Entry e)
    {
        using var reader = new VorbisReader(new MemoryStream(ogg), closeOnDispose: true);
        int ch = reader.Channels, rate = reader.SampleRate;
        if (ch < 1 || rate <= 0) throw new InvalidDataException("no audio");
        var pcm = new List<float>(reader.TotalSamples is > 0 and < 48000 * 60 ? (int)reader.TotalSamples * ch : 1 << 16);
        var buf = new float[4096 * ch];
        int read;
        while ((read = reader.ReadSamples(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i++) pcm.Add(buf[i]);
        // more than two channels (never uploaded through the viewer, but possible): the first two
        int keep = Math.Min(ch, 2);
        float[] data;
        if (keep == ch) data = pcm.ToArray();
        else
        {
            int frames0 = pcm.Count / ch;
            data = new float[frames0 * keep];
            for (int f = 0; f < frames0; f++) for (int c = 0; c < keep; c++) data[f * keep + c] = pcm[f * ch + c];
        }
        int frames = data.Length / keep;
        if (frames == 0) throw new InvalidDataException("empty");
        int native;
        fixed (float* p = data) native = AudioNative.sv_buf_load(p, frames, rate, keep);
        if (native <= 0) throw new InvalidDataException("the mixer refused it");
        long bytes = (long)Math.Ceiling(frames * 48000.0 / rate) * keep * 4;
        e.Native = native;
        e.Bytes = bytes;
        e.Seconds = frames / (double)rate;
        e.State = State.Ready;
        Interlocked.Add(ref _held, bytes);
        Trim(id);
    }

    /// <summary>Over the cap: the sounds not wanted for longest go (never one wanted in the last 30 s, as the viewer's
    /// buffers are kept while in use).</summary>
    void Trim(UUID keep)
    {
        if (HeldBytes <= MaxBytes) return;
        var now = DateTime.UtcNow;
        foreach (var (id, e) in _entries.Where(kv => kv.Value.State == State.Ready && !kv.Value.Local && kv.Key != keep).OrderBy(kv => kv.Value.LastWanted).ToList())
        {
            if (HeldBytes <= MaxBytes * 0.8) break;
            if (now - e.LastWanted < TimeSpan.FromSeconds(30)) break;
            Drop(id, e);
        }
    }

    void Drop(UUID id, Entry e)
    {
        if (!_entries.TryRemove(id, out _)) return;
        AudioNative.sv_buf_free(e.Native);   // channels still playing it keep it until they're done
        Interlocked.Add(ref _held, -e.Bytes);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
    }
}
