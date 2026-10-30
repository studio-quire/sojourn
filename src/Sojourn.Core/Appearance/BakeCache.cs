using System.Collections.Concurrent;
using LibreMetaverse;

namespace Sojourn.Core.Appearance;

/// <summary>
/// Client-side bakes (OpenSim and other grids without server baking), kept on disk per grid and shared by every
/// resident in the process. A bake is a pure function of the worn wearable assets that feed it (wearable edits make new
/// assets), so the protocol's own cache id (XOR of those asset ids and the bake's hash) is the key: a resident bakes
/// once per outfit change, and every later login just re-uploads the stored J2C (no decode, composite or encode).
/// Also remembers which uploaded texture id holds which bake, for the bake server real viewers fetch from.
/// </summary>
public sealed class BakeCache
{
    readonly string _dir;
    readonly ConcurrentDictionary<UUID, UUID> _byTexture = new();   // uploaded bake texture id -> cache id
    readonly object _indexLock = new();
    const string Version = "v4";   // bump if the baker's output changes
    /// <summary>Set when the baker's layers differ for the whole process (PlainBodies), so those bakes don't mix
    /// with ordinary ones in a shared cache folder.</summary>
    public static string Variant = "";

    /// <summary>One bake at a time in the whole process: a bake decodes every layer texture at full size and runs a
    /// J2K encode (~100 MB transient), so this is what bounds memory with many residents.</summary>
    public static readonly SemaphoreSlim Oven = new(1);

    public BakeCache(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
        var index = Path.Combine(_dir, "uploaded.txt");
        if (File.Exists(index))
            foreach (var line in File.ReadLines(index))
            {
                var parts = line.Split(' ');
                if (parts.Length == 2 && UUID.TryParse(parts[0], out var t) && UUID.TryParse(parts[1], out var k)) _byTexture[t] = k;
            }
    }

    string PathOf(UUID cacheId) => Path.Combine(_dir, $"{cacheId}-{Version}{(Variant.Length > 0 ? "-" + Variant : "")}.j2c");

    public byte[]? Get(UUID cacheId)
    {
        var p = PathOf(cacheId);
        try { return File.Exists(p) ? File.ReadAllBytes(p) : null; }
        catch (IOException) { return null; }
    }

    public void Put(UUID cacheId, byte[] j2c)
    {
        var p = PathOf(cacheId);
        var tmp = p + "." + Environment.CurrentManagedThreadId + ".tmp";
        File.WriteAllBytes(tmp, j2c);
        File.Move(tmp, p, overwrite: true);
    }

    public void Uploaded(UUID textureId, UUID cacheId)
    {
        if (!_byTexture.TryAdd(textureId, cacheId)) return;
        lock (_indexLock) File.AppendAllText(Path.Combine(_dir, "uploaded.txt"), $"{textureId} {cacheId}\n");
    }

    /// <summary>The J2C of an uploaded bake, by its texture id (what a viewer asks the appearance service for).</summary>
    public byte[]? ByTexture(UUID textureId) => _byTexture.TryGetValue(textureId, out var k) ? Get(k) : null;

    public (int Files, long Bytes) Stats()
    {
        var files = Directory.GetFiles(_dir, "*.j2c");
        return (files.Length, files.Sum(f => new FileInfo(f).Length));
    }
}
