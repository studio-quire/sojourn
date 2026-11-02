using System.Text.Json.Nodes;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Access.Walk;

/// <summary>SojournWalk: walk mode's state for the agent API (tests, and the assistant asked "what's around me"), and
/// the sonar's words on asking. Moving is the person's keys' (and the existing autopilot ops'), never this API's.</summary>
public sealed class WalkApi : LeapApi
{
    readonly WalkMode _walk;
    readonly Func<Func<Task>, Task> _onWindow;

    public override string Name => "SojournWalk";
    public override string Desc => "Sojourn Access's walk mode: its state, the cues heard, and the sonar's description of what's around";

    /// <param name="onWindow">Runs on the window's thread (walk mode lives there).</param>
    public WalkApi(AgentSession s, WalkMode walk, Func<Func<Task>, Task> onWindow) : base(s)
    {
        _walk = walk;
        _onWindow = onWindow;
        Op("status", "-> {on, held, surface, cues_played, last_line, last_cue, sound}: whether walk mode has the keys, what it last said and played", _ => Status());
        Op("sweep", "-> {words, distances}: the sonar sweep (as S in walk mode: walls on four sides, openings by clock face, doors near), said to the person too",
            async (_, ct) =>
            {
                await _onWindow(() => _walk.SweepAsync());
                return new JsonObject
                {
                    ["words"] = _walk.LastLine ?? "",
                    ["distances"] = _walk.LastSweep is { } d ? new JsonArray(d.Select(x => (JsonNode?)(double.IsFinite(x) ? Math.Round(x, 2) : null)).ToArray()) : null,
                };
            });
    }

    JsonNode Status() => new JsonObject
    {
        ["on"] = _walk.On, ["surface"] = _walk.Surface, ["cues_played"] = _walk.Sounds.Played, ["last_line"] = _walk.LastLine,
        ["last_cue"] = _walk.Sounds.Last is { } l ? l.Cue.ToString() : null,
        ["last_cue_ms_ago"] = _walk.Sounds.Last is { } la ? Environment.TickCount64 - la.At : null,
        ["walked"] = _walk.Walked.Last is { } w ? Math.Round(w, 2) : null, ["walked_all"] = Math.Round(_walk.Walked.All, 2),
        ["pos"] = _walk.Me() is { } b ? new JsonArray(Math.Round(b.Pos.X, 2), Math.Round(b.Pos.Y, 2), Math.Round(b.Pos.Z, 2)) : null,
        ["gaps"] = _walk.LastGaps is { } gs ? new JsonArray(gs.Select(g => (JsonNode?)new JsonObject
        {
            ["bearing"] = Math.Round(g.Bearing * 180 / Math.PI, 1), ["distance"] = Math.Round(g.Distance, 2), ["width"] = Math.Round(g.Width, 2),
        }).ToArray()) : null,
        ["stopping"] = _walk.Stopping, ["throttle_taps"] = _walk.ThrottleTaps,
        ["sound"] = _walk.Sounds.Available, ["lines"] = new JsonArray(_walk.RecentLines.Select(x => (JsonNode?)x).ToArray()),
        // M4: the cues sent lately with where they were placed (degrees clockwise from facing, metres from the head),
        // the beacon's target and the marks last sounded
        ["recent"] = new JsonArray(_walk.Sounds.Recent.Select(c => (JsonNode?)new JsonObject
        {
            ["cue"] = c.Cue.ToString(), ["ms_ago"] = Environment.TickCount64 - c.At, ["level"] = Math.Round(c.Level, 3),
            ["bearing"] = c.Bearing, ["distance"] = c.Distance,
        }).ToArray()),
        ["beacon"] = _walk.BeaconTarget() is { } t ? new JsonObject
        {
            ["name"] = t.Name, ["by_hand"] = t.ByHand,
            ["pos"] = t.Where() is { } p ? new JsonArray(Math.Round(p.X, 2), Math.Round(p.Y, 2), Math.Round(p.Z, 2)) : null,
        } : null,
        // rail switches the last look found (Walk/WalkMode.Switches.cs), nearest first
        ["switches"] = new JsonArray(_walk.Switches.Take(8).Select(s => (JsonNode?)new JsonObject
        {
            ["id"] = s.Id.ToString(), ["stand"] = s.Stand, ["status"] = s.Setting?.Status,
            ["pos"] = new JsonArray(Math.Round(s.Pos.X, 2), Math.Round(s.Pos.Y, 2), Math.Round(s.Pos.Z, 2)),
        }).ToArray()),
        ["marks"] = _walk.LastMarks is { } ms ? new JsonArray(ms.Select(m => (JsonNode?)new JsonObject
        {
            ["name"] = m.Name, ["mark"] = m.Mark, ["bearing"] = Math.Round(m.Bearing, 1), ["distance"] = Math.Round(m.Dist, 2),
        }).ToArray()) : null,
    };
}
