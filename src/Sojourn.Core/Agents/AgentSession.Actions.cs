using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

public sealed partial class AgentSession
{
    UploadOps? _uploads;
    public UploadOps Uploads => _uploads ??= new UploadOps(this);

    /// <summary>Teleport by region name (a local teleport if it's this region): whether it arrived, and the reason if
    /// not. Progress arrives as LLTeleport events; a failure reaches the person as CouldNotTeleportReason, including
    /// one the grid never reports itself (a region name it doesn't know).</summary>
    public async Task<(bool Ok, string Reason)> TeleportAsync(string region, Vector3 pos)
    {
        var c = Client;
        if (c is null) return (false, "not connected");
        var asked = DateTime.UtcNow;
        var from = c.Network.CurrentSim;
        try
        {
            if (await c.Self.TeleportAsync(region, pos))
            {
                await ArrivedAsync(c, from);
                return (true, "");
            }
            // LibreMetaverse sometimes reports a local teleport failed ("Teleport started", "Teleport finished") as it
            // arrives (seen on the local grid, 2026-10-29): in that region, at the place asked within 3 s, it arrived
            if (string.Equals(from?.Name, region, StringComparison.OrdinalIgnoreCase))
                for (int i = 0; i < 30; i++)
                {
                    if (ReferenceEquals(c.Network.CurrentSim, from)
                        && Vector2.Distance(new Vector2(c.Self.SimPosition.X, c.Self.SimPosition.Y), new Vector2(pos.X, pos.Y)) < 5)
                    {
                        Log.LogInformation("teleport to {Region} reported failed ({Msg}) but arrived", region, c.Self.TeleportMessage);
                        return (true, "");
                    }
                    await Task.Delay(100);
                }
            var why = string.IsNullOrWhiteSpace(c.Self.TeleportMessage) ? $"couldn't reach {region}" : c.Self.TeleportMessage;
            Log.LogInformation("teleport to {Region} failed: {Msg}", region, why);
            if (_teleportFailNoticed < asked) TeleportFailed(why);
            return (false, why);
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "teleport");
            TeleportFailed(e.Message);
            return (false, e.Message);
        }
    }

    /// <summary>LibreMetaverse finishes a teleport to another region on TeleportFinish, before the new region's
    /// AgentMovementComplete gives the agent its position there; until then Self.SimPosition is the old region's (or
    /// whatever an update in between said: "MagnetiS 80,85,2006" once read back as [-0.9, 212.7, 15.6]). Wait for it,
    /// so whoever asked reads where the agent actually is.</summary>
    static async Task ArrivedAsync(GridClient c, Simulator? from)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var sim = c.Network.CurrentSim;
            if (sim is null || ReferenceEquals(sim, from)) return;   // a local teleport: its reply carried the position
            if (sim.AgentMovementComplete) return;
            await Task.Delay(50);
        }
    }

    DateTime _teleportFailNoticed;

    TeleportWatch? _teleports;
    /// <summary>Teleports read from the protocol: each step logged, refusals noticed, requests awaited, stalled
    /// arrivals found (TeleportWatch).</summary>
    public TeleportWatch Teleports => _teleports ??= new TeleportWatch(this);

    /// <summary>A refusal someone else is telling the person about (the way out): no notice, but none from
    /// LibreMetaverse's own failure event for it either.</summary>
    internal void TeleportFailedHeard() => _teleportFailNoticed = DateTime.UtcNow;

    internal void TeleportFailed(string why)
    {
        _teleportFailNoticed = DateTime.UtcNow;
        Events.Log("LLTeleport", new JsonObject { ["success"] = false });
        Notify.Add("CouldNotTeleportReason", "alertmodal", $"Teleport failed.\n{why}", Notifications.Ok,
            substitutions: new JsonObject { ["REASON"] = why });
    }

    /// <summary>Avatars hidden by TesselAgent hideAvatars right now, in every region in view.</summary>
    public IEnumerable<UUID> HiddenAvatars()
    {
        var st = State.Settings;
        var mode = st.TryGetValue("_hide_mode", out var m) ? m!.GetValue<string>() : "off";
        if (mode == "off") return Array.Empty<UUID>();
        static HashSet<UUID> Ids(JsonNode? n) =>
            (n as JsonArray)?.Select(x => UUID.TryParse(x?.GetValue<string>(), out var id) ? id : UUID.Zero).ToHashSet() ?? new();
        var ids = Ids(st.TryGetValue("_hide_ids", out var i) ? i : null);
        ids.Remove(AgentId);   // one's own body is the view's own business (first person, the headset's body setting)
        if (mode == "list") return ids;
        var except = Ids(st.TryGetValue("_hide_except", out var e) ? e : null);
        return VisibleRegions().SelectMany(w => w.Avatars.Values).Select(a => a.ID)
            .Where(id => id != AgentId && !except.Contains(id)).Distinct().ToList();
    }

    /// <summary>Who the person's view leaves out this frame: their own avatar when it isn't drawn (first person, or no
    /// body in the headset) and anyone hidden by hideAvatars. Null when that's nobody.</summary>
    public IReadOnlyCollection<UUID>? HiddenInView(bool hideSelf)
    {
        var hidden = HiddenAvatars();
        if (hideSelf && Client is { } c) hidden = hidden.Append(c.Self.AgentID);
        var set = hidden.ToHashSet();
        return set.Count == 0 ? null : set;
    }
}
