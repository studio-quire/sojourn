// Samples a running process's threads (suspend, read the instruction pointer, resume) every few milliseconds and says
// where the busy ones were: for the view drawn on the processor (Mesa's lavapipe), how much was LLVM compiling shaders,
// running the code it made, the rasteriser, or the app. Symbols from vulkan_lvp.pdb (local/mesa-*-pdb) by DbgHelp.
// No ETW, so no elevation. Leaf only: a sample counts where the thread was, not who called it.
//
//   lvpsample PID SECONDS [--every MS] [--symbols DIR] [--top N] [--csv FILE]
//
// Written by Claude Opus 5.5, 2026-11-01, for the first picture after login on a slow PC.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

int pid = int.Parse(args[0]);
double seconds = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
string Opt(string name, string dflt) => Array.IndexOf(args, name) is var i and >= 0 ? args[i + 1] : dflt;
int everyMs = int.Parse(Opt("--every", "5"));
string symDir = Opt("--symbols", "");
int top = int.Parse(Opt("--top", "40"));
string? csv = Opt("--csv", "") is { Length: > 0 } c ? c : null;

var proc = Native.OpenProcess(0x1F0FFF, false, (uint)pid);
if (proc == 0) { Console.WriteLine($"can't open process {pid}"); return 1; }
Native.SymSetOptions(0x2 | 0x4 | 0x200000);   // undecorated, deferred loads, no prompts... (UNDNAME, DEFERRED_LOADS, FAVOR_COMPRESSED off)
if (!Native.SymInitializeW(proc, string.IsNullOrEmpty(symDir) ? null : symDir, true))
    Console.WriteLine($"SymInitialize failed ({Marshal.GetLastWin32Error()}): no symbols");

var modules = Modules(proc);
(string Name, long Base, long End)? ModuleOf(long a) => modules.FirstOrDefault(m => a >= m.Base && a < m.End) is { Name: not null } m ? m : null;

var threadKind = new Dictionary<int, string>();
var handles = new Dictionary<int, nint>();
var leaf = new Dictionary<long, int>();
var perKind = new Dictionary<(string Thread, string Where), int>();
var timeline = new List<(double T, Dictionary<string, int> Counts)>();
var sw = Stopwatch.StartNew();
long samples = 0, busy = 0;
var ctx = Marshal.AllocHGlobal(1232 + 16);
var aligned = (ctx + 15) & ~(nint)15;
var window = new Dictionary<string, int>();
double windowAt = 0;
var pending = new List<(int Tid, long Ip)>();

while (sw.Elapsed.TotalSeconds < seconds)
{
    long t0 = sw.ElapsedTicks;
    Process p;
    try { p = Process.GetProcessById(pid); } catch { break; }
    pending.Clear();
    foreach (ProcessThread t in p.Threads)
    {
        if (!handles.TryGetValue(t.Id, out var h))
        {
            h = Native.OpenThread(0x0002 | 0x0008 | 0x0040, false, (uint)t.Id);   // SUSPEND_RESUME, GET_CONTEXT, QUERY_INFORMATION
            if (h == 0) continue;
            handles[t.Id] = h;
            threadKind[t.Id] = Native.NtQueryInformationThread(h, 9, out nint start, nint.Size, 0) == 0 && ModuleOf(start) is { } sm
                ? sm.Name.Equals("vulkan_lvp.dll", StringComparison.OrdinalIgnoreCase) ? "lavapipe" : sm.Name : "?";
        }
        if (Native.SuspendThread(h) == uint.MaxValue) continue;
        Marshal.WriteInt32(aligned, 0x30, 0x100001);   // ContextFlags = CONTEXT_CONTROL
        bool ok = Native.GetThreadContext(h, aligned);
        Native.ResumeThread(h);
        if (ok) pending.Add((t.Id, Marshal.ReadInt64(aligned, 0xF8)));   // Rip
    }
    foreach (var (tid, ip) in pending)
    {
        samples++;
        string where = Where(ip, out bool idle);
        if (idle) continue;
        busy++;
        leaf[ip] = leaf.GetValueOrDefault(ip) + 1;
        var kind = threadKind.GetValueOrDefault(tid, "?");
        perKind[(kind, Category(where))] = perKind.GetValueOrDefault((kind, Category(where))) + 1;
        window[Category(where)] = window.GetValueOrDefault(Category(where)) + 1;
    }
    double now = sw.Elapsed.TotalSeconds;
    if (now - windowAt >= 1)
    {
        timeline.Add((Math.Round(windowAt, 1), window));
        window = new Dictionary<string, int>();
        windowAt = now;
    }
    long spent = (sw.ElapsedTicks - t0) * 1000 / Stopwatch.Frequency;
    if (spent < everyMs) Thread.Sleep(everyMs - (int)spent);
}
foreach (var h in handles.Values) Native.CloseHandle(h);

double per = busy == 0 ? 0 : 100.0 / busy;
Console.WriteLine($"{samples} thread samples over {sw.Elapsed.TotalSeconds:0.0} s, {busy} busy (every {everyMs} ms)");
Console.WriteLine("busy, by thread and where:");
foreach (var ((thread, where), n) in perKind.OrderByDescending(kv => kv.Value).Take(25))
    Console.WriteLine($"  {n * per,5:0.0}%  {thread,-14} {where}");
Console.WriteLine("busy, by where:");
foreach (var g in perKind.GroupBy(kv => kv.Key.Where).Select(g => (g.Key, N: g.Sum(x => x.Value))).OrderByDescending(x => x.N))
    Console.WriteLine($"  {g.N * per,5:0.0}%  {g.Key}");
Console.WriteLine("top leaf functions:");
foreach (var g in leaf.GroupBy(kv => Where(kv.Key, out _)).Select(g => (g.Key, N: g.Sum(x => x.Value))).OrderByDescending(x => x.N).Take(top))
    Console.WriteLine($"  {g.N * per,5:0.0}%  {g.Key}");
if (csv != null)
{
    var cats = timeline.SelectMany(t => t.Counts.Keys).Distinct().OrderBy(x => x).ToList();
    var sb = new StringBuilder("t," + string.Join(",", cats) + "\n");
    foreach (var (t, counts) in timeline) sb.Append(t.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + string.Join(",", cats.Select(k => counts.GetValueOrDefault(k))) + "\n");
    File.WriteAllText(csv, sb.ToString());
}
return 0;

string Where(long ip, out bool idle)
{
    idle = false;
    var m = ModuleOf(ip);
    if (m == null) return "(generated code: shaders llvmpipe made, or .NET's)";
    string? sym = Symbol(ip);
    // a system call's stub: in the kernel, nearly always waiting (a wait, a sleep, a blocking read, a message loop)
    if ((m.Value.Name.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase) || m.Value.Name.Equals("win32u.dll", StringComparison.OrdinalIgnoreCase))
        && sym != null && (sym.StartsWith("Nt") || sym.StartsWith("Zw")))
        idle = true;
    return $"{m.Value.Name}!{sym ?? "?"}";
}

static string Category(string where)
{
    if (where.StartsWith("(")) return "generated code";
    int bang = where.IndexOf('!');
    string mod = where[..bang], sym = where[(bang + 1)..];
    if (!mod.Equals("vulkan_lvp.dll", StringComparison.OrdinalIgnoreCase)) return mod;
    if (sym.StartsWith("llvm::") || sym.StartsWith("LLVM") || sym.Contains("::llvm::") || sym.StartsWith("`anonymous namespace'") || sym.StartsWith("std::")) return "lavapipe: LLVM compiling";
    if (sym.StartsWith("lp_build") || sym.StartsWith("lp_bld") || sym.StartsWith("gallivm") || sym.StartsWith("nir_") || sym.StartsWith("lp_nir") || sym.StartsWith("visit_")) return "lavapipe: making IR";
    if (sym.StartsWith("lp_rast") || sym.StartsWith("lp_setup") || sym.StartsWith("lp_scene") || sym.StartsWith("do_triangle") || sym.StartsWith("lp_linear") || sym.StartsWith("blit") || sym.StartsWith("util_format")) return "lavapipe: rasteriser and copies";
    if (sym.StartsWith("mem") || sym.StartsWith("_") || sym.Contains("malloc") || sym.Contains("free")) return "lavapipe: CRT (" + sym + ")";
    return "lavapipe: other";
}

string? Symbol(long ip)
{
    if (symCache.TryGetValue(ip, out var s)) return s;
    var buf = Marshal.AllocHGlobal(1024 * 2 + 88);
    try
    {
        Marshal.WriteInt32(buf, 0, 88);          // SizeOfStruct (SYMBOL_INFOW)
        Marshal.WriteInt32(buf, 80, 1000);       // MaxNameLen
        s = Native.SymFromAddrW(proc, ip, out _, buf) ? Marshal.PtrToStringUni(buf + 84) : null;
    }
    finally { Marshal.FreeHGlobal(buf); }
    symCache[ip] = s;
    return s;
}

static List<(string Name, long Base, long End)> Modules(nint proc)
{
    var list = new List<(string, long, long)>();
    var hs = new nint[4096];
    if (!Native.EnumProcessModulesEx(proc, hs, hs.Length * nint.Size, out int need, 3)) return list;
    for (int i = 0; i < Math.Min(hs.Length, need / nint.Size); i++)
    {
        var sb = new StringBuilder(260);
        Native.GetModuleBaseNameW(proc, hs[i], sb, 260);
        if (Native.GetModuleInformation(proc, hs[i], out var mi, Marshal.SizeOf<Native.ModuleInfo>()))
            list.Add((sb.ToString(), mi.Base, mi.Base + mi.Size));
    }
    return list;
}

partial class Program { static readonly Dictionary<long, string?> symCache = new(); }

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct ModuleInfo { public nint Base; public int Size; public nint Entry; }
    [DllImport("kernel32", SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32", SetLastError = true)] public static extern nint OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32")] public static extern uint SuspendThread(nint h);
    [DllImport("kernel32")] public static extern uint ResumeThread(nint h);
    [DllImport("kernel32")] public static extern bool GetThreadContext(nint h, nint ctx);
    [DllImport("kernel32")] public static extern bool CloseHandle(nint h);
    [DllImport("ntdll")] public static extern int NtQueryInformationThread(nint h, int cls, out nint info, int size, nint returned);
    [DllImport("psapi", SetLastError = true)] public static extern bool EnumProcessModulesEx(nint proc, [Out] nint[] mods, int cb, out int needed, uint filter);
    [DllImport("psapi", CharSet = CharSet.Unicode)] public static extern int GetModuleBaseNameW(nint proc, nint mod, StringBuilder name, int size);
    [DllImport("psapi")] public static extern bool GetModuleInformation(nint proc, nint mod, out ModuleInfo info, int size);
    [DllImport("dbghelp", SetLastError = true)] public static extern uint SymSetOptions(uint options);
    [DllImport("dbghelp", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool SymInitializeW(nint proc, string? searchPath, bool invade);
    [DllImport("dbghelp", SetLastError = true)] public static extern bool SymFromAddrW(nint proc, long addr, out long displacement, nint symbol);
}
