using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Assets;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Appearance;

/// <summary>
/// What a new resident on our grids gets besides a look: a "Clap" gesture (the clap animation and a clap sound), active.
/// Second Life residents have the Library's gestures; an OpenSim library has none, and claps are a little language
/// among residents (Tessel's `clap` tool plays them in rhythms, the ears hear them as <c>heard_rhythm</c>). The clap
/// sound is made here (a short filtered noise burst), uploaded once per grid and shared by every resident's gesture.
/// Once per resident (<c>kit.json</c> in its state dir), grids without server baking only, like the look.
/// </summary>
public sealed class StarterKit
{
    readonly AgentSession _s;
    static readonly SemaphoreSlim Gate = new(1);
    public StarterKit(AgentSession s) => _s = s;

    string Marker => Path.Combine(_s.StateDir, "kit.json");

    public async Task GiveIfNeededAsync(GridClient c, CancellationToken ct)
    {
        if (File.Exists(Marker)) return;
        var made = new JsonObject { ["at"] = Json.UnixNow() };
        if (!await HasClapAsync(c, ct))
        {
            var sound = await ClapSoundAsync(c, ct);
            var g = new AssetGesture { Trigger = "/clap", ReplaceWith = "" };
            g.Sequence.Add(new GestureStepAnimation { Name = "clap", ID = Animations.CLAP, AnimationStart = true });
            g.Sequence.Add(new GestureStepSound { Name = "clap", ID = sound });
            g.Sequence.Add(new GestureStepEOF());
            g.Encode();
            var tx = UUID.Random();
            var asset = await c.Assets.RequestUploadAsync(AssetType.Gesture, g.AssetData, false, tx, ct);
            var folder = c.Inventory.FindFolderForType(FolderType.Gesture);
            var item = await c.Inventory.CreateItemAsync(folder, "Clap", "a clap (the clap animation and sound)", AssetType.Gesture, tx,
                InventoryType.Gesture, PermissionMask.All, ct) ?? throw new InvalidOperationException("the gesture item wasn't created");
            c.Self.ActivateGesture(item.UUID, item.AssetUUID != UUID.Zero ? item.AssetUUID : asset);
            _s.Ears.Learn(sound, "clap");
            made["clap_gesture"] = item.UUID.ToString();
            _s.Log.LogInformation("starter kit: Clap gesture {Item}", item.UUID);
        }
        await File.WriteAllTextAsync(Marker, made.ToJsonString(), ct);
    }

    /// <summary>A resident with the kit knows its clap gesture's sound by name (the gesture's step names it).</summary>
    public static void LearnNames(AgentSession s)
    {
        try
        {
            var file = Path.Combine(s.Host.CacheDirFor(s.Config), "starter.json");
            if (!File.Exists(Path.Combine(s.StateDir, "kit.json")) || !File.Exists(file)) return;
            if (JsonNode.Parse(File.ReadAllText(file))?["clap_sound"]?.GetValue<string>() is { } id && UUID.TryParse(id, out var sound))
                s.Ears.Learn(sound, "clap");
        }
        catch (Exception e) { s.Log.LogDebug(e, "starter kit names"); }
    }

    async Task<bool> HasClapAsync(GridClient c, CancellationToken ct)
    {
        foreach (var itemId in c.Self.ActiveGestures.Keys)
            if (await _s.Inventory.ItemAsync(itemId, ct) is { } it && it.Name.Contains("clap", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The grid's shared clap sound: made and uploaded by the first resident who needs it.</summary>
    async Task<UUID> ClapSoundAsync(GridClient c, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var file = Path.Combine(_s.Host.CacheDirFor(_s.Config), "starter.json");
            var o = File.Exists(file) ? JsonNode.Parse(await File.ReadAllTextAsync(file, ct)) as JsonObject ?? new() : new JsonObject();
            if (o["clap_sound"]?.GetValue<string>() is { } s && UUID.TryParse(s, out var known)) return known;
            var (ogg, _) = Media.SoundConvert.WavToVorbis(ClapWav());
            var id = await c.Assets.RequestUploadAsync(AssetType.Sound, ogg, false, UUID.Random(), ct);
            if (id == UUID.Zero) throw new InvalidOperationException("the clap sound upload failed");
            o["clap_sound"] = id.ToString();
            await File.WriteAllTextAsync(file, o.ToJsonString(), ct);
            return id;
        }
        finally { Gate.Release(); }
    }

    /// <summary>A hand clap: a sharp band-limited noise burst (~1-3 kHz) with a short room tail, 44.1 kHz mono.</summary>
    static byte[] ClapWav()
    {
        const int rate = 44100;
        var n = (int)(0.25 * rate);
        var rng = new Random(7);
        var pcm = new short[n];
        double lp = 0, hp = 0, prev = 0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)rate;
            var env = t < 0.002 ? t / 0.002 : Math.Exp(-(t - 0.002) / 0.012) + 0.08 * Math.Exp(-(t - 0.002) / 0.07);
            var x = rng.NextDouble() * 2 - 1;
            lp += 0.45 * (x - lp);              // low-pass
            hp = 0.92 * (hp + lp - prev);       // high-pass
            prev = lp;
            pcm[i] = (short)Math.Clamp(hp * env * 1.8 * 32767, -32767, 32767);
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        foreach (var v in pcm) w.Write(v);
        return ms.ToArray();
    }
}
