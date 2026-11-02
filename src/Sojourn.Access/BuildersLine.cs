using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sojourn.Session;

namespace Sojourn.Access;

/// <summary>
/// The builders' line on Studio Quire's relay (tools/relay/README.md, /relay/messages): a tester's messages to the
/// Claude sessions building Sojourn Access and their replies, with the person's Studio Quire token. Only what the
/// person typed goes; nothing else of the session. The window's side is MainForm.Builders.cs. Written by Claude Opus
/// 5.5, 2026-11-01.
/// </summary>
sealed class BuildersLine
{
    public sealed record Message(long Id, DateTime At, bool Studio, string By, string Text);

    /// <summary>The relay's answer: its status (0 if it couldn't be reached) and its own sentence if it gave one.</summary>
    public sealed record Answer(int Status, string? Said, JsonObject? Body)
    {
        public bool Ok => Status is >= 200 and < 300;
    }

    static string Relay => Environment.GetEnvironmentVariable("SOJOURN_RELAY") is { Length: > 0 } u ? u.TrimEnd('/') : LogSend.RelayUrl;

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly string _agent = $"SojournAccess/{AppInfo.Version}";

    /// <summary>The conversation after an id, oldest first (null when the relay didn't answer with it).</summary>
    public async Task<(List<Message>? Got, Answer A)> FetchAsync(string token, long since, CancellationToken ct)
    {
        var a = await CallAsync(HttpMethod.Get, $"/relay/messages?since={since}", token, null, ct);
        if (!a.Ok || a.Body?["messages"] is not JsonArray arr) return (null, a);
        var got = new List<Message>();
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            try
            {
                got.Add(new Message(o["id"]!.GetValue<long>(), DateTimeOffset.FromUnixTimeMilliseconds(o["at"]!.GetValue<long>()).LocalDateTime,
                    o["from"]?.GetValue<string>() == "studio", o["by"]?.GetValue<string>() ?? "", o["text"]?.GetValue<string>() ?? ""));
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or NullReferenceException) { }
        }
        return (got, a);
    }

    /// <summary>The person's message; its id is in the answer's body.</summary>
    public Task<Answer> SendAsync(string token, string text, CancellationToken ct) =>
        CallAsync(HttpMethod.Post, "/relay/messages", token, new JsonObject { ["text"] = text }, ct);

    async Task<Answer> CallAsync(HttpMethod method, string path, string token, JsonObject? body, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, Relay + path);
            req.Headers.Add("x-api-key", token);
            req.Headers.UserAgent.ParseAdd(_agent);
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
            using var res = await _http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            JsonObject? o = null;
            try { o = JsonNode.Parse(text) as JsonObject; }
            catch (JsonException) { }
            string? said = null;
            try { said = o?["error"]?["message"]?.GetValue<string>(); }
            catch (InvalidOperationException) { }
            return new Answer((int)res.StatusCode, said, o);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return new Answer(0, e is TaskCanceledException ? "no answer in time" : e.Message, null);
        }
    }

    /// <summary>A short stand-in for a token, so what's kept knows whose conversation it was without keeping the token.</summary>
    public static string Mark(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];
}

/// <summary>What the app keeps of the builders' line, in builders.json in its base dir: whose conversation (a token's
/// mark), the newest reply already said, and whether the person has heard the tab's introduction.</summary>
sealed class BuildersState
{
    public string? Token { get; set; }
    public long Seen { get; set; }
    public bool Introduced { get; set; }

    string _path = "";

    public static BuildersState Load(string baseDir)
    {
        var path = Path.Combine(baseDir, "builders.json");
        BuildersState? s = null;
        try { s = JsonSerializer.Deserialize<BuildersState>(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        s ??= new BuildersState();
        s._path = path;
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(this)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
