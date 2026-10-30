using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Sojourn.Core;

/// <summary>Named numbers from components that hold memory the GC doesn't see or that could grow (GPU allocator
/// blocks, queues, caches), shown under "native" in the host's /health.</summary>
public static class MemoryProbes
{
    static readonly ConcurrentDictionary<string, Func<double>> Probes = new();

    public static void Register(string name, Func<double> read) => Probes[name] = read;

    public static JsonObject Snapshot()
    {
        var o = new JsonObject();
        foreach (var (name, read) in Probes.OrderBy(p => p.Key))
        {
            try { o[name] = Math.Round(read(), 1); }
            catch (Exception) { o[name] = null; }
        }
        return o;
    }
}
