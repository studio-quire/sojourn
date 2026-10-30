// heapwho FILE.gcdump [TOP]: what holds a managed heap.
// A breadth-first spanning tree from the roots gives every object one parent (the first that reaches it); retained
// bytes are summed up that tree (an approximation of dominators, PerfView's way). Printed: the types retaining most,
// and arrays of 80 KB or more grouped by the chain of their three nearest holders' types.
using Graphs;
using Microsoft.Diagnostics.Utilities;

var dump = new GCHeapDump(args[0]);
int top = args.Length > 1 ? int.Parse(args[1]) : 40;
var g = dump.MemoryGraph;
int n = (int)g.NodeIndexLimit;
var parent = new int[n];
Array.Fill(parent, -1);
var order = new List<int>(n);
var node = g.AllocNodeStorage();
var child = g.AllocNodeStorage();
var type = g.AllocTypeNodeStorage();
var q = new Queue<int>();
int root = (int)g.RootIndex;
parent[root] = root;
q.Enqueue(root);
while (q.Count > 0)
{
    int i = q.Dequeue();
    order.Add(i);
    g.GetNode((NodeIndex)i, node);
    for (var c = node.GetFirstChildIndex(); c != NodeIndex.Invalid; c = node.GetNextChildIndex())
    {
        int ci = (int)c;
        if (parent[ci] != -1) continue;
        parent[ci] = i;
        q.Enqueue(ci);
    }
}
var size = new long[n];
var typeOf = new int[n];
var names = new Dictionary<int, string>();
string Name(int t)
{
    if (!names.TryGetValue(t, out var s)) { g.GetType((NodeTypeIndex)t, type); names[t] = s = type.Name; }
    return s;
}
foreach (int i in order)
{
    g.GetNode((NodeIndex)i, node);
    size[i] = node.Size;
    typeOf[i] = (int)node.TypeIndex;
}
var retained = (long[])size.Clone();
for (int k = order.Count - 1; k > 0; k--)
{
    int i = order[k];
    if (parent[i] != i) retained[parent[i]] += retained[i];
}
long total = order.Sum(i => size[i]);
Console.WriteLine($"{order.Count:N0} objects reachable, {total / 1048576.0:N0} MB");

// retained by type, counting only the outermost object of a type on each path (no double counting of nested ones)
var byType = new Dictionary<int, (long Bytes, int Count)>();
foreach (int i in order)
{
    if (i == root) continue;
    int t = typeOf[i];
    bool nested = false;
    for (int p = parent[i]; p != root && p != parent[p]; p = parent[p])
        if (typeOf[p] == t) { nested = true; break; }
    if (nested) continue;
    var (b, c) = byType.GetValueOrDefault(t);
    byType[t] = (b + retained[i], c + 1);
}
Console.WriteLine($"\nretained by type (top {top}):");
foreach (var (t, (b, c)) in byType.OrderByDescending(kv => kv.Value.Bytes).Take(top))
    Console.WriteLine($"  {b / 1048576.0,9:N1} MB {c,9:N0}  {Name(t)}");

// large arrays by their holders
var chains = new Dictionary<string, (long Bytes, int Count)>();
foreach (int i in order)
{
    if (size[i] < 80_000) continue;
    var parts = new List<string> { Name(typeOf[i]) };
    for (int p = parent[i], k = 0; k < 4 && p != root; p = parent[p], k++) parts.Add(Name(typeOf[p]));
    string key = string.Join(" <- ", parts);
    var (b, c) = chains.GetValueOrDefault(key);
    chains[key] = (b + size[i], c + 1);
}
Console.WriteLine($"\nobjects of 80 KB or more, by holders (top {top}):");
foreach (var (k, (b, c)) in chains.OrderByDescending(kv => kv.Value.Bytes).Take(top))
    Console.WriteLine($"  {b / 1048576.0,9:N1} MB {c,7:N0}  {k}");
