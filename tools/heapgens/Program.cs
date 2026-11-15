// heapgens PID [TOP]: what each part of a running process's managed heap holds, exactly (ClrMD, the process suspended
// while it's read). Every object on the heap is classed by where it sits (gen0, gen1, gen2, the large object heap, the
// pinned heap) and as live (reachable from the roots, breadth first, as a collection would find it), dead (garbage not
// yet collected) or free (the GC's own free blocks: fragmentation). Printed: those totals per part with what the
// segments have committed, the large object heap's live and dead objects by type, its free blocks by size, gen2's
// live objects by type, and the large object heap's live objects by the chain of types holding them (nearest first).
// A gcdump just before (tools/heapwho.py) forces a full collection, so dead is then near nothing and free is what the
// collection left. Written by Claude Opus 5.5, 2026-11-14.
using Microsoft.Diagnostics.Runtime;

int pid = int.Parse(args[0]);
int top = args.Length > 1 && int.TryParse(args[1], out var t1) ? t1 : 30;

using var dt = DataTarget.AttachToProcess(pid, suspend: true);
using var runtime = dt.ClrVersions[0].CreateRuntime();
var heap = runtime.Heap;

string Part(ClrSegment s, ulong addr) => s.Kind switch
{
    GCSegmentKind.Large => "large",
    GCSegmentKind.Pinned => "pinned",
    GCSegmentKind.Frozen => "frozen",
    GCSegmentKind.Generation0 => "gen0",
    GCSegmentKind.Generation1 => "gen1",
    GCSegmentKind.Generation2 => "gen2",
    _ => s.GetGeneration(addr) switch
    {
        Generation.Generation0 => "gen0",
        Generation.Generation1 => "gen1",
        Generation.Generation2 => "gen2",
        Generation.Large => "large",
        Generation.Pinned => "pinned",
        Generation.Frozen => "frozen",
        _ => "?",
    },
};

// committed memory per part (a region's kind; ephemeral segments, pre-regions, counted as gen0)
var committed = new Dictionary<string, long>();
foreach (var s in heap.Segments)
{
    string k = s.Kind switch
    {
        GCSegmentKind.Large => "large", GCSegmentKind.Pinned => "pinned", GCSegmentKind.Frozen => "frozen",
        GCSegmentKind.Generation1 => "gen1", GCSegmentKind.Generation2 => "gen2", _ => "gen0",
    };
    committed[k] = committed.GetValueOrDefault(k) + (long)s.CommittedMemory.Length;
}

// live: breadth first from the roots, each object's first finder kept as its holder
var parent = new Dictionary<ulong, ulong>(1 << 20);
var q = new Queue<ulong>();
foreach (var r in heap.EnumerateRoots())
{
    ulong a = r.Object.Address;
    if (a == 0 || parent.ContainsKey(a)) continue;
    parent[a] = 0;
    q.Enqueue(a);
}
while (q.Count > 0)
{
    ulong a = q.Dequeue();
    var o = heap.GetObject(a);
    if (!o.IsValid) continue;
    foreach (ulong c in o.EnumerateReferenceAddresses(carefully: false, considerDependantHandles: true))
    {
        if (c == 0 || parent.ContainsKey(c)) continue;
        parent[c] = a;
        q.Enqueue(c);
    }
}

var totals = new Dictionary<string, (long Live, long Dead, long Free, long LiveCount)>();
var lohLive = new Dictionary<string, (long Bytes, int Count)>();
var lohDead = new Dictionary<string, (long Bytes, int Count)>();
var gen2Live = new Dictionary<string, (long Bytes, int Count)>();
var lohFree = new long[5];   // free blocks under 64 KB, under 1 MB, under 4 MB, under 16 MB, larger
var lohFreeBytes = new long[5];
var lohLiveAddrs = new List<(ulong Addr, long Size, string Type)>();
void Add(Dictionary<string, (long, int)> d, string k, long b) { var (x, n) = d.GetValueOrDefault(k); d[k] = (x + b, n + 1); }

foreach (var s in heap.Segments)
{
    foreach (var o in s.EnumerateObjects())
    {
        if (!o.IsValid) continue;
        long size = (long)o.Size;
        string part = Part(s, o.Address);
        var (live, dead, free, lc) = totals.GetValueOrDefault(part);
        string type = o.Type?.Name ?? "?";
        if (o.IsFree)
        {
            free += size;
            if (part == "large")
            {
                int b = size < 64 << 10 ? 0 : size < 1 << 20 ? 1 : size < 4 << 20 ? 2 : size < 16 << 20 ? 3 : 4;
                lohFree[b]++;
                lohFreeBytes[b] += size;
            }
        }
        else if (parent.ContainsKey(o.Address))
        {
            live += size;
            lc++;
            if (part == "large") { Add(lohLive, type, size); lohLiveAddrs.Add((o.Address, size, type)); }
            else if (part == "gen2") Add(gen2Live, type, size);
        }
        else
        {
            dead += size;
            if (part == "large") Add(lohDead, type, size);
        }
        totals[part] = (live, dead, free, lc);
    }
}

static string MB(long b) => $"{b / 1048576.0,8:N1}";
Console.WriteLine($"process {pid}: {parent.Count:N0} objects reachable");
Console.WriteLine("part       committed     live     dead     free   (MB; live objects)");
foreach (var part in new[] { "gen0", "gen1", "gen2", "large", "pinned", "frozen" })
{
    var (live, dead, free, lc) = totals.GetValueOrDefault(part);
    Console.WriteLine($"{part,-8} {MB(committed.GetValueOrDefault(part))} {MB(live)} {MB(dead)} {MB(free)}   ({lc:N0})");
}

void Table(string title, Dictionary<string, (long Bytes, int Count)> d)
{
    Console.WriteLine($"\n{title} (top {top}):");
    foreach (var (k, (b, c)) in d.OrderByDescending(kv => kv.Value.Bytes).Take(top))
        Console.WriteLine($"  {MB(b)} MB {c,8:N0}  {k}");
}
Table("large object heap, live, by type", lohLive);
Table("large object heap, dead (not yet collected), by type", lohDead);
Console.WriteLine("\nlarge object heap, free blocks: " + string.Join(", ",
    new[] { "<64 KB", "64 KB-1 MB", "1-4 MB", "4-16 MB", ">16 MB" }.Select((n, i) => $"{n} {lohFree[i]:N0} ({lohFreeBytes[i] / 1048576.0:N1} MB)")));
Table("gen2, live, by type (own size)", gen2Live);

string TypeAt(ulong a) => heap.GetObjectType(a)?.Name ?? "?";
var chains = new Dictionary<string, (long Bytes, int Count)>();
foreach (var (addr, size, type) in lohLiveAddrs)
{
    var parts = new List<string> { type };
    ulong p = parent[addr];
    for (int k = 0; k < 5 && p != 0; k++, p = parent.GetValueOrDefault(p)) parts.Add(TypeAt(p));
    if (p == 0) parts.Add("(root)");
    var key = string.Join(" <- ", parts);
    var (b, c) = chains.GetValueOrDefault(key);
    chains[key] = (b + size, c + 1);
}
Table("large object heap, live, by holders", chains);
