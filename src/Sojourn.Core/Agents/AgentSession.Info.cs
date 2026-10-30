using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

public sealed partial class AgentSession
{
    public LeapDispatcher Api { get; }
    public AgentState State { get; }
    public Scripts.ScriptEditor Scripts { get; }

    /// <summary>Pumps someone asked /listen for; we emit our own fixed set, the rest are just reported back.</summary>
    public ConcurrentBag<string> Listened { get; } = new();

    public IEnumerable<string> ListeningPumps() =>
        LeapDispatcher.EventPumps.Concat(Listened).Distinct().OrderBy(x => x, StringComparer.Ordinal);

    /// <summary>The bridge's /status.</summary>
    public JsonObject StatusJson() => new()
    {
        ["connected"] = Online,
        ["connected_at"] = ConnectedAt,
        ["seq"] = Events.Seq,
        ["listening"] = Json.Strings(ListeningPumps()),
        ["tessel"] = true,
        ["host"] = "sojourn",
        ["agent"] = Config.Name,
        ["status"] = Status.ToString().ToLowerInvariant(),
        ["status_message"] = StatusMessage,
        ["appearance"] = Online ? Look.Status : "",
    };

    /// <summary>The admin API's view of this agent.</summary>
    public JsonObject Summary()
    {
        var c = Client;
        var sim = c?.Network.CurrentSim;
        var o = new JsonObject
        {
            ["name"] = Config.Name,
            ["login"] = Config.LoginName,
            ["grid"] = GridKey,
            ["port"] = Config.Port,
            ["status"] = Status.ToString().ToLowerInvariant(),
            ["message"] = StatusMessage,
            ["online"] = Online,
            ["agent_id"] = AgentId == default ? null : AgentId.ToString(),
            ["logins"] = LoginCount,
            ["seq"] = Events.Seq,
            ["state_dir"] = StateDir,
        };
        if (Online && c is not null && sim is not null)
        {
            o["region"] = sim.Name;
            o["pos"] = Json.Arr(c.Self.SimPosition);
        }
        o["last_words"] = State.LastWords;
        o["appearance"] = Look.Status;
        return o;
    }
}
