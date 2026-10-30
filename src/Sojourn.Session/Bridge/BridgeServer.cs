using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Bridge;

/// <summary>
/// The agent API for the person's session, on loopback: Tessel's bridge HTTP API (slbridge.py) over Multitude's
/// LEAP emulation, so the same tools (and phase 3's assistant) can drive this client.
///   GET /status, POST /cmd {pump, data, reply?, timeout?}, POST /listen {pump}, POST /event {kind, data},
///   GET /events?since=&amp;wait=, POST /rpc {method, params}
/// and for the assistant's sidecar, when voice chat's audio engine is up (Voice/AssistantMic.cs):
///   GET /mic/info, GET /mic?wait= (the last utterance, 16 kHz mono int16 as base64, taken once),
///   GET /mic/live?from= (the utterance being collected, from sample `from` on, for a streaming recogniser; not taken),
///   POST /play {rate, pcm} (mono int16 base64, played locally), POST /play/clear, GET /play (ms still queued)
/// </summary>
/// <summary>Sound to and from the assistant's sidecar, provided by voice chat's engine.</summary>
public interface IClientAudio
{
    JsonObject Info();
    Task<JsonObject?> TakeUtteranceAsync(TimeSpan wait, CancellationToken ct);
    /// <summary>The utterance so far from sample <paramref name="from"/>: {pcm, total, collecting} (collecting false
    /// once it's finished, which TakeUtteranceAsync then hands over as usual).</summary>
    JsonObject Live(int from);
    void Play(short[] pcm, int rate);
    void ClearPlay();
    int QueuedMs();
}

public sealed class BridgeServer : IAsyncDisposable
{
    /// <summary>The client's audio for the assistant, once voice is up (null: the sidecar uses its own mic).</summary>
    public static IClientAudio? Audio { get; set; }

    /// <summary>Event kinds only the client logs, refused on POST /event (dev mode: the person's words reach the driving
    /// session as `sojourn` and `dev` events, so nothing else may pass itself off as them). Empty otherwise.</summary>
    public static IReadOnlySet<string> OwnKinds { get; set; } = new HashSet<string>();

    readonly WebApplication _app;
    public string Url { get; }

    public BridgeServer(AgentSession session, int port, ILoggerFactory logs)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port);
            k.Limits.MaxRequestBodySize = 50_000_000;
        });
        _app = builder.Build();
        Url = $"http://127.0.0.1:{port}/";
        Map(_app, session);
    }

    public Task StartAsync() => _app.StartAsync();

    static IResult JsonResult(JsonNode? node, int status = 200) =>
        Results.Content(Json.Serialize(node), "application/json", null, status);

    static async Task<JsonObject> Body(HttpRequest req)
    {
        try { return await JsonNode.ParseAsync(req.Body) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    static void Map(WebApplication app, AgentSession a)
    {
        // Loopback still lets in web pages the person has open: a page can POST here as text/plain without asking
        // (cross-site request forgery), and a hostname rebound to 127.0.0.1 can read replies (DNS rebinding). Our own
        // callers send JSON, name the host as 127.0.0.1 or localhost, and aren't browsers (no Origin header).
        app.Use(async (ctx, next) =>
        {
            var req = ctx.Request;
            bool hostOk = req.Host.Host is "127.0.0.1" or "localhost" or "[::1]";
            bool jsonOk = !HttpMethods.IsPost(req.Method) || (req.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? false);
            if (!hostOk || req.Headers.ContainsKey("Origin") || !jsonOk)
            {
                ctx.Response.StatusCode = !hostOk || req.Headers.ContainsKey("Origin") ? 403 : 415;
                await ctx.Response.WriteAsync(!jsonOk && hostOk ? "POST application/json" : "forbidden");
                return;
            }
            await next();
        });

        app.MapGet("/status", () => JsonResult(a.StatusJson()));

        app.MapPost("/cmd", async (HttpRequest req, CancellationToken ct) =>
        {
            var body = await Body(req);
            var pump = body.Str("pump") ?? "";
            var data = body["data"] as JsonObject ?? new JsonObject();
            body.Remove("data");
            var reply = body.Bool("reply", true);
            var timeout = TimeSpan.FromSeconds(body.Num("timeout", 15));
            try { return JsonResult(await a.Api.CallAsync(pump, data, reply, timeout, ct)); }
            catch (TimeoutException) { return JsonResult(Json.Error("timeout"), 504); }
            catch (NotConnected e) { return Results.Text(e.Message, "text/plain", null, 503); }
        });

        app.MapPost("/listen", async (HttpRequest req) =>
        {
            var body = await Body(req);
            if (body.Str("pump") is { } pump) a.Listened.Add(pump);
            return JsonResult(new JsonObject { ["listening"] = Json.Strings(a.ListeningPumps()) });
        });

        app.MapPost("/event", async (HttpRequest req) =>
        {
            var body = await Body(req);
            var data = body["data"];
            body.Remove("data");
            if (OwnKinds.Contains(body.Str("kind") ?? "event")) return JsonResult(Json.Error("the client's own kind of event"), 403);
            // incognito, only the person's own hand and voice speak for them ("incognito off" posted by some process isn't)
            if (a.Private.On && PrivateMode.OwnKinds.Contains(body.Str("kind") ?? "event"))
                return JsonResult(Json.Error("incognito is on: the client's own kind of event"), 403);
            return JsonResult(new JsonObject { ["seq"] = a.Events.Log(body.Str("kind") ?? "event", data ?? new JsonObject()) });
        });

        // incognito seals what's logged while it's on (Events/EventLog.cs)
        app.MapGet("/events", async (long? since, double? wait, CancellationToken ct) =>
            Results.Content(await a.Events.GetVisibleJsonAsync(since ?? 0, TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, 600)), ct), "application/json"));

        app.MapPost("/rpc", async (HttpRequest req, CancellationToken ct) =>
        {
            var body = await Body(req);
            if (a.Private.On) return JsonResult(Json.Error("incognito is on: the script editor waits until it's off"), 403);
            var (status, result) = await a.Scripts.RpcAsync(body.Str("method") ?? "", body["params"] as JsonObject,
                TimeSpan.FromSeconds(body.Num("timeout", 30)), ct);
            return JsonResult(result, status);
        });

        app.MapGet("/mic/info", () => JsonResult(Audio?.Info() ?? new JsonObject { ["available"] = false }));

        app.MapGet("/mic", async (double? wait, CancellationToken ct) =>
        {
            if (Audio is not { } audio) return JsonResult(Json.Error("no client mic"), 404);
            var u = await audio.TakeUtteranceAsync(TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, 10)), ct);
            return u != null ? JsonResult(u) : JsonResult(new JsonObject { ["ms"] = 0 });
        });

        app.MapGet("/mic/live", (int? from) =>
            Audio is { } audio ? JsonResult(audio.Live(Math.Max(0, from ?? 0))) : JsonResult(Json.Error("no client mic"), 404));

        app.MapPost("/play", async (HttpRequest req) =>
        {
            if (Audio is not { } audio) return JsonResult(Json.Error("no client audio"), 404);
            var body = await Body(req);
            var bytes = Convert.FromBase64String(body.Str("pcm") ?? "");
            var pcm = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
            audio.Play(pcm, (int)body.Num("rate", 24000));
            return JsonResult(new JsonObject { ["queued_ms"] = audio.QueuedMs() });
        });

        app.MapPost("/play/clear", () =>
        {
            Audio?.ClearPlay();
            return JsonResult(new JsonObject { ["queued_ms"] = 0 });
        });

        app.MapGet("/play", () => JsonResult(new JsonObject { ["queued_ms"] = Audio?.QueuedMs() ?? 0 }));
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(); } catch { }
        await _app.DisposeAsync();
    }
}
