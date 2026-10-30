// Where a running Sojourn allocates: reads a .nettrace holding GCAllocationTick events with stacks (about one per
// 100 KB allocated, naming the type and carrying the allocating stack) and prints the bytes by type, by the first
// frame in our own code (or LibreMetaverse's), and by short stacks. Written by Claude Opus 5.5, 2026-10-26.
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args.Length < 1) { Console.Error.WriteLine("alloctrace FILE.nettrace [--top N] [--depth D] [--thread SUBSTRING]"); return 1; }
int top = args.Contains("--top") ? int.Parse(args[Array.IndexOf(args, "--top") + 1]) : 30;
int depth = args.Contains("--depth") ? int.Parse(args[Array.IndexOf(args, "--depth") + 1]) : 5;

string etlx = TraceLog.CreateFromEventPipeDataFile(args[0]);
using var log = new TraceLog(etlx);
var byType = new Dictionary<string, double>();
var bySite = new Dictionary<string, double>();
var byStack = new Dictionary<string, double>();
var byThread = new Dictionary<int, double>();
double total = 0, start = double.MaxValue, end = 0;

static bool Ours(string m) =>
    m.StartsWith("Sojourn") || m.StartsWith("OpenMetaverse") || m.StartsWith("LibreMetaverse") || m.StartsWith("Program") ||
    m.StartsWith("<Program>");

static string Short(string m)
{
    int p = m.IndexOf('(');
    if (p > 0) m = m[..p];
    return m.Length > 110 ? m[^110..] : m;
}

foreach (var ev in log.Events)
{
    if (ev is not GCAllocationTickTraceData a) continue;
    double bytes = a.AllocationAmount64 > 0 ? a.AllocationAmount64 : a.AllocationAmount;
    total += bytes;
    start = Math.Min(start, ev.TimeStampRelativeMSec);
    end = Math.Max(end, ev.TimeStampRelativeMSec);
    string type = a.TypeName ?? "?";
    byType[type] = byType.GetValueOrDefault(type) + bytes;
    byThread[ev.ThreadID] = byThread.GetValueOrDefault(ev.ThreadID) + bytes;
    var frames = new List<string>();
    for (var s = ev.CallStack(); s != null && frames.Count < 40; s = s.Caller)
    {
        var name = s.CodeAddress.FullMethodName;
        frames.Add(string.IsNullOrEmpty(name) ? "?" + s.CodeAddress.ModuleName : Short(name));
    }
    int first = frames.FindIndex(Ours);
    string site = first >= 0 ? frames[first] : frames.FirstOrDefault(f => !f.StartsWith("?")) ?? "?";
    bySite[$"{site}  [{type}]"] = bySite.GetValueOrDefault($"{site}  [{type}]") + bytes;
    var from = first >= 0 ? first : 0;
    string stack = string.Join("  <  ", frames.Skip(Math.Max(0, from - 1)).Take(depth));
    byStack[$"[{type}] {stack}"] = byStack.GetValueOrDefault($"[{type}] {stack}") + bytes;
}

double secs = Math.Max(0.001, (end - start) / 1000);
Console.WriteLine($"{total / (1 << 20):F0} MB sampled over {secs:F1} s: {total / (1 << 20) / secs:F0} MB/s");
void Print(string title, Dictionary<string, double> d)
{
    Console.WriteLine(title);
    foreach (var (k, v) in d.OrderByDescending(kv => kv.Value).Take(top))
        Console.WriteLine($"  {100 * v / total,5:F1}%  {v / (1 << 20) / secs,7:F1} MB/s  {k}");
}
Print("by type:", byType);
Print("by thread:", byThread.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));
Print("by site (first frame in our code or LibreMetaverse):", bySite);
Print("by stack:", byStack);
return 0;
