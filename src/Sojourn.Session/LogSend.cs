using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sojourn.Session;

/// <summary>
/// A filtered log (LogShare.cs) on its way to Studio Quire through its relay (tools/relay/README.md, POST /relay/log),
/// with the person's Studio Quire token: packed as gzipped JSON under the relay's limit, then posted. Shared by Sojourn
/// Access's Send a log (SendLog.cs) and the VR client's problem and crash reports (Release/ProblemReport.cs, the
/// launcher's CrashReport.cs). Depends only on .NET and LogShare.cs, so the launcher links it. Each caller says the
/// outcome in its own words. Written by Claude Opus 5.5, 2026-11-01.
/// </summary>
public static class LogSend
{
    public const string RelayUrl = "https://relay.studioquire.org";
    /// <summary>Compressed, under the relay's limit (tools/relay/worker.js LOG_MAX).</summary>
    public const int MaxBytes = 1_400_000;

    static string Relay => Environment.GetEnvironmentVariable("SOJOURN_RELAY") is { Length: > 0 } u ? u.TrimEnd('/') : RelayUrl;
    /// <summary>Where logs go with a token; SOJOURN_RELAY overrides the relay (a local wrangler dev, for tests).</summary>
    public static string Url => Relay + "/relay/log";
    /// <summary>Where Sojourn's problem and crash reports go without one (the relay's limits: tools/relay/README.md).</summary>
    public static string ReportUrl => Relay + "/relay/report";

    /// <summary>
    /// The log's text with whatever goes beside it (doc: the JSON for a given text, the text under "log") as gzipped
    /// JSON. A log too long for the relay loses lines from its middle (the start and the end tell most), saying so in
    /// the text. Cut: whether it did.
    /// </summary>
    public static (byte[] Body, bool Cut) Pack(string text, Func<string, JsonObject> doc)
    {
        var lines = text.Split('\n');
        int keep = lines.Length;
        while (true)
        {
            var t = keep >= lines.Length ? text
                : string.Join('\n', lines.Take(keep / 2)) + $"\n... {lines.Length - keep} lines left out here to fit ...\n" + string.Join('\n', lines.Skip(lines.Length - keep / 2));
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                gz.Write(Encoding.UTF8.GetBytes(doc(t).ToJsonString(LogShare.Json)));
            if (ms.Length <= MaxBytes || keep < 200) return (ms.ToArray(), keep < lines.Length);
            keep = (int)(keep * 0.7);
        }
    }

    /// <summary>The relay's answer: its status (0 if it couldn't be reached), its own message if it gave one, and what
    /// went wrong on the way if nothing came back (Unreached: the exception's words, or "no answer in time").</summary>
    public sealed record Answer(int Status, string? Message, string? Unreached, JsonObject? Body)
    {
        public bool Ok => Status is >= 200 and < 300;
    }

    /// <summary>Posts a packed log with the token (never logged), or without one (null) as a Sojourn report to the
    /// relay's anonymous endpoint. userAgent: the app and its version.</summary>
    public static async Task<Answer> PostAsync(byte[] body, string? token, string userAgent, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            using var req = new HttpRequestMessage(HttpMethod.Post, token != null ? Url : ReportUrl) { Content = new ByteArrayContent(body) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
            if (token != null) req.Headers.Add("x-api-key", token);
            req.Headers.UserAgent.ParseAdd(userAgent);
            using var res = await http.SendAsync(req, ct);
            var answer = await res.Content.ReadAsStringAsync(ct);
            JsonObject? o = null;
            try { o = JsonNode.Parse(answer) as JsonObject; }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { }
            string? message = null;
            try { message = o?["error"]?["message"]?.GetValue<string>(); }
            catch (Exception e) when (e is InvalidOperationException or FormatException) { }
            return new Answer((int)res.StatusCode, message, null, o);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return new Answer(0, null, e is TaskCanceledException ? "no answer in time" : e.Message, null);
        }
    }
}
