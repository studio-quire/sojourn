using System.Collections.Concurrent;
using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Assets;
using Sojourn.Core.World;

namespace Sojourn.Core.Sound;

/// <summary>What we know about a sound asset: its length and loudness (decoded once per grid), and a name if any
/// resident has seen one for it.</summary>
public sealed record SoundInfo(double Duration, double RmsDb, double PeakDb);

/// <summary>
/// Per grid, shared by every resident: sound asset facts (decoded once, sound assets never change), the well-known
/// sound names (residents learn others from their own inventories, <see cref="Ears.Learn"/>), and transcripts of spoken sounds (<see cref="Transcriber"/>).
/// </summary>
public sealed class SoundCatalog
{
    readonly WorldHub _hub;
    readonly string _grid;
    readonly Dictionary<UUID, string> _names;
    readonly ConcurrentDictionary<UUID, Task<SoundInfo?>> _info = new();
    readonly ConcurrentDictionary<UUID, Task<string?>> _speech = new();
    const int MaxInfo = 20_000;   // a few hundred bytes each

    /// <summary>Ogg Vorbis -> mono samples and rate (set at startup; null means loudness and length stay unknown).</summary>
    public static Func<byte[], (float[] Samples, int Rate)>? Decoder = data =>
    {
        var d = Media.SoundConvert.DecodeVorbis(data);
        return (d.Samples, d.SampleRate);
    };

    public SoundCatalog(WorldHub hub, string grid)
    {
        _hub = hub;
        _grid = grid;
        _names = WellKnown.Value;
    }

    public string? Name(UUID id) => _names.TryGetValue(id, out var n) ? n : null;

    public bool IsCollision(UUID id) => CollisionIds.Value.Contains(id);

    /// <summary>Length and loudness, fetched and decoded once; null if the asset can't be had or decoded.</summary>
    public Task<SoundInfo?> InfoAsync(UUID id)
    {
        if (Decoder is null || id == UUID.Zero) return Task.FromResult<SoundInfo?>(null);
        if (_info.Count > MaxInfo) _info.Clear();
        return _info.GetOrAdd(id, _ => Task.Run(async () =>
        {
            try
            {
                var pcm = await SamplesAsync(id);
                if (pcm is null || pcm.Value.Rate <= 0) return null;
                var (s, rate) = pcm.Value;
                double sum = 0, peak = 0;
                foreach (var x in s) { sum += x * x; peak = Math.Max(peak, Math.Abs(x)); }
                var rms = s.Length > 0 ? Math.Sqrt(sum / s.Length) : 0;
                return new SoundInfo(Math.Round(s.Length / (double)rate, 2), Db(rms), Db(peak));
            }
            catch { return null; }
        }));
    }

    /// <summary>The asset's samples (mono), for the transcriber.</summary>
    public async Task<(float[] Samples, int Rate)?> SamplesAsync(UUID id)
    {
        if (Decoder is null) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var asset = await _hub.AssetAsync(_grid, id, AssetType.Sound, cts.Token);
        if (asset?.AssetData is not { Length: > 0 } data) return null;
        return Decoder(data);
    }

    /// <summary>What a spoken sound says, transcribed once per asset (null: no transcriber, not speech, or failed).</summary>
    public Task<string?> SpeechAsync(UUID id, Transcriber? t)
    {
        if (t is null) return Task.FromResult<string?>(null);
        return _speech.GetOrAdd(id, _ => Task.Run(async () =>
        {
            var pcm = await SamplesAsync(id);
            return pcm is null ? null : await t.TranscribeAsync(pcm.Value.Samples, pcm.Value.Rate);
        }));
    }

    public static double Db(double amplitude) => Math.Round(20 * Math.Log10(Math.Max(amplitude, 1e-6)), 1);

    // ---------- well-known sounds ----------

    static readonly Lazy<HashSet<UUID>> CollisionIds = new(() =>
        WellKnown.Value.Where(kv => kv.Value.StartsWith("collision", StringComparison.Ordinal)).Select(kv => kv.Key).ToHashSet());

    static readonly Lazy<Dictionary<UUID, string>> WellKnown = new(() =>
    {
        var d = new Dictionary<UUID, string>();
        // LibreMetaverse's table of the viewer's built-in sounds (UI ones included; they don't come from the world)
        foreach (var f in typeof(Sounds).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.GetValue(null) is UUID id)
                d[id] = f.Name.ToLowerInvariant().Replace('_', ' ');
        // The viewer's collision table (sound_ids.cpp) as Second Life sends it
        string[] mats = { "stone", "metal", "glass", "wood", "flesh", "plastic", "rubber" };
        foreach (var (id, name) in new[]
        {
            ("be582e5d-b123-41a2-a150-454c39e961c8", "collision"),
            ("212b6d1e-8d9c-4986-b3aa-f3c6df8d987d", "collision: rubber"),
            ("d55c7f3c-e1c3-4ddc-9eff-9ef805d9190e", "collision: plastic"),
            ("2d8c6f51-149e-4e23-8413-93a379b42b67", "collision: flesh"),
            ("6f00669f-15e0-4793-a63e-c03f62fee43a", "collision: wood"),
            ("85cda060-b393-48e6-81c8-2cfdfb275351", "collision: glass"),
            ("d1375446-1c4d-470b-9135-30132433b678", "collision: metal"),
            ("e8af4a28-aa83-4310-a7c4-c047e15ea0df", "footsteps"),
            ("5e191c7b-8996-9ced-a177-b2ac32bfea06", "typing"),
        })
            d[new UUID(id)] = name;
        // OpenSim's collision sounds (CollisionSounds.cs): be7295c0-a158-11e1-b3dd-080{a}20{b}c9a66, a <= b over the
        // materials, and ...0807200c9a66 for terrain
        for (var a = 0; a < mats.Length; a++)
            for (var b = a; b < mats.Length; b++)
                d[new UUID($"be7295c0-a158-11e1-b3dd-080{a}20{b}c9a66")] = a == b ? $"collision: {mats[a]}" : $"collision: {mats[a]} on {mats[b]}";
        d[new UUID("be7295c0-a158-11e1-b3dd-0807200c9a66")] = "collision: on the ground";
        return d;
    });
}
