using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Sojourn.Session.Assistant;

/// <summary>
/// Dev mode (--dev, docs/dev-mode.md): the Claude Code session that started the client stands in for the assistant.
/// The sidecar still hears (Whisper) and speaks; what the person says, types, picks or points at goes out as `dev`
/// events (SojournAssistant dev, with the sidecar's token), which tools/dev.py watch turns into lines for the session,
/// and the session answers on the panel (SojournAssistant say, panel). Here: a note in local/dev/PORT.json saying
/// where this client is (pid, port, base dir, log, its command line), so tools/dev.py finds it whoever started it.
/// </summary>
static class DevMode
{
    public static string? Announce(SessionOptions opt, string[] args, string? sessionLog, bool vr, ILogger log)
    {
        log.LogInformation("dev mode: no assistant of its own; the person's requests go to the session driving the client (tools/dev.py watch)");
        if (Sidecar.Root is not { } root) return null;
        var path = Path.Combine(root, "local", "dev", $"{opt.BridgePort}.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var note = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["port"] = opt.BridgePort,
                ["api"] = $"http://127.0.0.1:{opt.BridgePort}",
                ["base_dir"] = opt.BaseDir,
                ["log"] = sessionLog,
                ["vr"] = vr,
                ["grid"] = opt.LoginUri,
                ["account"] = $"{opt.First} {opt.Last}",
                ["assistant"] = opt.Assistant,
                // (a password given on the command line stays out)
                ["args"] = new JsonArray(args.Select((a, i) => (JsonNode?)(i > 0 && args[i - 1] == "--password" ? "(hidden)" : a)).ToArray()),
                ["started"] = DateTimeOffset.Now.ToString("o"),
            };
            File.WriteAllText(path, note.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("dev mode: couldn't write {Path}: {Msg}", path, e.Message);
            return null;
        }
    }

    public static void Withdraw(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
