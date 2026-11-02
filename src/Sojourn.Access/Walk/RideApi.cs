using System.Text.Json.Nodes;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Access.Walk;

/// <summary>SojournRide: a ride recorded in Sojourn Access (RideRecorder), for the describer to tell (`get`, asked by
/// the sidecar only when the person asks for it told) and for tests (`status`). Starting and stopping are the person's
/// keys and words, never this API's.</summary>
public sealed class RideApi : LeapApi
{
    readonly RideRecorder _rec;
    readonly Func<Func<Task>, Task> _onWindow;

    public override string Name => "SojournRide";
    public override string Desc => "Sojourn Access's ride recording: the ride recorded (or recording), to be told";

    /// <param name="onWindow">Runs on the window's thread (the recorder lives there).</param>
    public RideApi(AgentSession s, RideRecorder rec, Func<Func<Task>, Task> onWindow) : base(s)
    {
        _rec = rec;
        _onWindow = onWindow;
        Op("status", "-> {recording, ride?: {seconds, moments, pictures, taken, skipped, thinned, last_picture_ms, ended, summary, names_passed}}",
            async (_, _) => await OnWindow(Status));
        Op("get", "{detail: brief | rich} -> {id, ride: {riding, began_in, length, ended, pictures, moments: [{at, region, speed_m_s, going, height_m, "
            + "climb_m_s?, from_start_m, riding?, it_shows?, near?, said?, picture?: {label, jpeg, width, height, seen}}]}}: the ride recording now or "
            + "the last one, pictures chosen evenly for brief (all for rich)",
            async (a, _) =>
            {
                bool rich = a.Str("detail") is "rich" or "more";
                return await OnWindow(() => _rec.Latest is { } r ? RideRecorder.ForTelling(r, rich)
                    : throw new LeapError("no ride recorded this session: C in walk mode, or //ride, records one"));
            });
    }

    async Task<JsonNode?> OnWindow(Func<JsonNode> f)
    {
        JsonNode? got = null;
        await _onWindow(() => { got = f(); return Task.CompletedTask; });
        return got;
    }

    JsonNode Status()
    {
        var o = new JsonObject { ["recording"] = _rec.On };
        if (_rec.Latest is not { } r) return o;
        var passed = new List<string>();
        foreach (var m in r.Moments)
            foreach (var t in m.Around ?? new())
                if (!passed.Contains(t.Name)) passed.Add(t.Name);
        o["ride"] = new JsonObject
        {
            ["seconds"] = Math.Round(r.Ms / 1000.0, 1), ["moments"] = r.Moments.Count, ["pictures"] = r.Pictures, ["taken"] = r.Taken,
            ["skipped"] = r.Skipped, ["thinned"] = r.Thinned, ["last_picture_ms"] = r.LastPictureMs, ["ended"] = r.Ended,
            ["private"] = r.Private, ["summary"] = RideRecorder.Summary(r),
            ["names_passed"] = new JsonArray(passed.Select(x => (JsonNode?)x).ToArray()),
            ["shows"] = new JsonArray(r.Moments.Where(m => m.Shows != null).Select(m => (JsonNode?)m.Shows).ToArray()),
            ["said"] = new JsonArray(r.Moments.SelectMany(m => m.Said).Select(x => (JsonNode?)x).ToArray()),
        };
        return o;
    }
}
