using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Sojourn.Session;

// Sojourn.SessionProbe: one person's session from Sojourn.Session alone, in a console: no window, no headset, and no
// renderer unless --render (the renderer out of sight, Offscreen/: then it also takes a picture). It logs in, serves the agent API, and through that API (as any caller would) answers SojournSettings, says
// its line in local chat every few seconds and listens in the session's events for the line it expects; then it logs out.
// Exit code 0 when everything asked for happened. tools/probe.py runs two of them at each other on the local grid.
//
//   dotnet Sojourn.SessionProbe.dll [--first F --last L --password P] [--grid URI] [--start uri:Region&x&y&z]
//       [--port 9760] [--base DIR] [--say TEXT] [--expect TEXT] [--seconds 120] [--voice] [--sounds] [--render]

var a = Args.Parse(args);
var opt = new SessionOptions
{
    LoginUri = a.Get("--grid") ?? "http://127.0.0.1:9700/",
    First = a.Get("--first") ?? "Sojourn",
    Last = a.Get("--last") ?? "Probe",
    Password = a.Get("--password") ?? "sojourn-local",
    Start = a.Get("--start") ?? "uri:Atelier&128&118&26",
    BridgePort = int.Parse(a.Get("--port") ?? "9760", CultureInfo.InvariantCulture),
    BaseDir = Path.GetFullPath(a.Get("--base") ?? Path.Combine(RepoLocal(), "probe")),
    Assistant = false,   // no sidecar: the probe is about the session, not Claude
    Voice = a.Has("--voice"),
    Sounds = a.Has("--sounds"),
    Media = false,
    // a screen reader running on this PC (NVDA for another test) would turn reader mode on; the probe stays quiet
    VrOverrides = [s => s.ScreenReader.Mode = "off"],
};
string? say = a.Get("--say"), expect = a.Get("--expect");
var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(double.Parse(a.Get("--seconds") ?? "120", CultureInfo.InvariantCulture));

bool render = a.Has("--render");
await using var host = new SessionHost(opt, renderHooks: render);
await host.StartAllAsync(args);
if (render) host.StartOffscreen();
if (host.Bridge == null) { Console.Error.WriteLine("probe: no agent API; stopping"); await host.ShutdownAsync(); return 2; }

var results = new List<(string What, bool Ok, string Detail)>();
var checks = Task.Run(() => Checks(opt.BridgePort));
// the main thread is the session's: SojournSettings' changes and reads run here (SessionHost.Tick)
while (!checks.IsCompleted && !host.Chat.QuitRequested)
{
    host.Tick();
    Thread.Sleep(20);
}
try { await checks; } catch (Exception e) { results.Add(("checks", false, e.Message)); }
await host.ShutdownAsync();

bool all = results.Count > 0 && results.All(r => r.Ok);
foreach (var (what, ok, detail) in results) Console.WriteLine($"probe: {(ok ? "PASS" : "FAIL")} {what}: {detail}");
Console.WriteLine($"probe: {(all ? "all passed" : "failed")}");
return all ? 0 : 1;

async Task Checks(int port)
{
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(30) };
    async Task<JsonNode?> Cmd(string pump, string op, JsonObject? data = null)
    {
        data ??= new JsonObject();
        data["op"] = op;
        using var r = await http.PostAsJsonAsync("cmd", new JsonObject { ["pump"] = pump, ["data"] = data });
        return JsonNode.Parse(await r.Content.ReadAsStringAsync());
    }

    // logged in
    JsonNode? status = null;
    while (DateTime.UtcNow < deadline)
    {
        try { status = JsonNode.Parse(await http.GetStringAsync("status")); } catch (HttpRequestException) { }
        if (status?["connected"]?.GetValue<bool>() == true) break;
        await Task.Delay(500);
    }
    bool online = status?["connected"]?.GetValue<bool>() == true;
    results.Add(("logged in", online, online ? $"{opt.First} {opt.Last}, {status?["status_message"]}" : $"not connected: {status?.ToJsonString()}"));
    if (!online) return;

    // SojournSettings, through the agent API
    var settings = (await Cmd("SojournSettings", "get"))?["settings"] as JsonObject;
    var one = (await Cmd("SojournSettings", "get", new JsonObject { ["name"] = "screen_reader" }))?["settings"]?["screen_reader"]?["value"];
    results.Add(("SojournSettings get", settings is { Count: > 0 } && one?.ToString() == "off",
        settings is null ? "no answer" : $"{settings.Count} settings ({string.Join(", ", settings.Select(kv => kv.Key).Take(6))}, ...); screen_reader {one}"));

    // the renderer out of sight: running, and a picture through the snapshot op (as the assistant's look takes one)
    if (render)
    {
        JsonNode? view = null;
        while (DateTime.UtcNow < deadline)
        {
            view = await Cmd("SojournView", "status");
            if (view?["state"]?.ToString() is "running" && view["frames"]?.GetValue<long>() > 4) break;
            await Task.Delay(1000);
        }
        results.Add(("the view out of sight", view?["state"]?.ToString() == "running", view?.ToJsonString() ?? "no answer"));
        var path = Path.Combine(opt.BaseDir, "look.png");
        File.Delete(path);
        var shot = await Cmd("LLViewerWindow", "saveSnapshot", new JsonObject { ["filename"] = path, ["width"] = 1024, ["height"] = 1024 });
        results.Add(("a picture", File.Exists(path) && new FileInfo(path).Length > 10_000, $"{shot?.ToJsonString()}: {path}"));
    }

    // local chat: say our line every few seconds until we hear the one we expect, then twice more (the other may be later)
    if (say == null && expect == null) return;
    // (/events: a JSON array of the session's events after a seq, each with its own)
    static long LastSeq(JsonArray? evs, long since) =>
        evs?.Select(e => e?["seq"]?.GetValue<long>() ?? 0).DefaultIfEmpty(since).Max() ?? since;
    long since = LastSeq(JsonNode.Parse(await http.GetStringAsync("events?since=0&wait=0")) as JsonArray, 0);
    bool heard = expect == null, saidOk = say == null;
    int after = 0;
    string? heardLine = null;
    while (DateTime.UtcNow < deadline && (!heard || after < 2))
    {
        if (say != null)
        {
            var r = await Cmd("LLChatBar", "sendChat", new JsonObject { ["message"] = say });
            saidOk |= r is JsonObject o && !o.ContainsKey("error");
            if (heard) after++;
        }
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < until)
        {
            var evs = JsonNode.Parse(await http.GetStringAsync($"events?since={since}&wait=1")) as JsonArray;
            since = LastSeq(evs, since);
            foreach (var e in evs ?? [])
            {
                var text = e?.ToJsonString() ?? "";
                if (!heard && expect != null && text.Contains(expect, StringComparison.Ordinal)) { heard = true; heardLine = text; }
            }
        }
        if (say == null && heard) break;
    }
    if (say != null) results.Add(("said in local chat", saidOk, $"\"{say}\" through LLChatBar sendChat"));
    if (expect != null) results.Add(("heard local chat", heard, heard ? heardLine![..Math.Min(heardLine!.Length, 200)] : $"\"{expect}\" never came"));
}

static string RepoLocal()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Sojourn.Client"))) dir = dir.Parent;
    return dir != null ? Path.Combine(dir.FullName, "local") : Path.Combine(AppContext.BaseDirectory, "data");
}

sealed record Args(Dictionary<string, string?> Map)
{
    public static Args Parse(string[] args)
    {
        var map = new Dictionary<string, string?>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            bool flag = i + 1 >= args.Length || args[i + 1].StartsWith("--");
            map[args[i]] = flag ? null : args[++i];
        }
        return new Args(map);
    }
    public string? Get(string k) => Map.TryGetValue(k, out var v) ? v : null;
    public bool Has(string k) => Map.ContainsKey(k);
}
