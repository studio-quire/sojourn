using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Map;

/// <summary>Where the person is, as the viewer's location bar says it (lllocationinputctrl.cpp refreshParcelIcons, the
/// rules from llviewerparcelmgr.cpp allowAgent*): the region and its rating, the parcel, what's blocked there, and
/// health where damage is on.</summary>
public sealed record Location(string Region, ulong Handle, Vector3 Pos, string Rating, string? Parcel, int ParcelId,
    bool NoFly, bool NoBuild, bool NoPush, bool NoVoice, bool NoScripts, bool Damage, int Health, bool HidesAvatars, int? ForSale)
{
    /// <summary>What changes the line's picture (the position only by the metre the line doesn't show).</summary>
    public string Key => $"{Region}|{Rating}|{Parcel}|{NoFly}{NoBuild}{NoPush}{NoVoice}{NoScripts}{Damage}{HidesAvatars}|{(Damage ? Health : 0)}|{ForSale}";

    public JsonObject ToJson() => new()
    {
        ["region"] = Region, ["position"] = new JsonArray((int)Pos.X, (int)Pos.Y, (int)Pos.Z), ["rating"] = Rating,
        ["parcel"] = Parcel, ["parcel_id"] = ParcelId == 0 ? null : ParcelId,
        ["no_fly"] = NoFly, ["no_build"] = NoBuild, ["no_push"] = NoPush, ["no_voice"] = NoVoice, ["no_scripts"] = NoScripts,
        ["damage"] = Damage, ["health"] = Damage ? Health : null, ["avatars_hidden_from_outside"] = HidesAvatars,
        ["for_sale"] = ForSale,
    };
}

/// <summary>How things are running, the viewer's Lag Meter in three parts (llfloaterlagmeter.cpp, its thresholds from
/// floater_lagmeter.xml), each 0 fine, 1 warning, 2 critical: this client's frame rate, the network (ping to the
/// region; LibreMetaverse keeps no packet loss), the region's frame time.</summary>
public sealed record Stats(float Fps, float TargetFps, float PingMs, int MissedPings, float Dilation, float SimFps, float PhysicsFps,
    float FrameMs, float ScriptMs, float PhysicsMs, float NetMs, float AgentMs, float ImageMs, int Agents, int ChildAgents, int Objects,
    int ScriptedObjects, int ActiveScripts, int ScriptEventsPerSecond)
{
    public int Client => TargetFps > 30
        ? Fps >= TargetFps * 0.9f ? 0 : Fps >= TargetFps * 0.6f ? 1 : 2    // a headset: frames missed against its refresh
        : Fps >= 15 ? 0 : Fps >= 10 ? 1 : 2;
    public int Network => PingMs >= 600 || MissedPings > 2 ? 2 : PingMs >= 300 ? 1 : 0;
    public int Server => FrameMs >= 1000f / 20 || Dilation is > 0 and < 0.75f ? 2 : FrameMs >= 1000f / 30 || Dilation is > 0 and < 0.95f ? 1 : 0;

    /// <summary>The meter's three levels, for redrawing only when one changes.</summary>
    public string Key => $"{Client}{Network}{Server}";

    /// <summary>What's slowest on the region, as the lag meter names it (a part over 20 ms of its frame).</summary>
    public string? RegionCause => PhysicsMs > 20 ? "physics" : ScriptMs > 20 ? "scripts" : NetMs > 20 ? "network" : AgentMs > 20 ? "avatars" : ImageMs > 20 ? "images" : null;

    static string Level(int l) => l switch { 0 => "fine", 1 => "slow", _ => "very slow" };

    public JsonObject ToJson() => new()
    {
        ["client"] = new JsonObject { ["state"] = Level(Client), ["fps"] = MathF.Round(Fps), ["target_fps"] = MathF.Round(TargetFps) },
        ["network"] = new JsonObject { ["state"] = Level(Network), ["ping_ms"] = MathF.Round(PingMs), ["missed_pings"] = MissedPings },
        ["region"] = new JsonObject
        {
            ["state"] = Level(Server), ["time_dilation"] = MathF.Round(Dilation, 3), ["fps"] = MathF.Round(SimFps), ["physics_fps"] = MathF.Round(PhysicsFps),
            ["frame_ms"] = MathF.Round(FrameMs, 1), ["scripts_ms"] = MathF.Round(ScriptMs, 1), ["physics_ms"] = MathF.Round(PhysicsMs, 1),
            ["net_ms"] = MathF.Round(NetMs, 1), ["agents_ms"] = MathF.Round(AgentMs, 1), ["images_ms"] = MathF.Round(ImageMs, 1),
            ["slowest"] = RegionCause, ["people"] = Agents, ["people_nearby"] = ChildAgents, ["objects"] = Objects,
            ["scripted_objects"] = ScriptedObjects, ["active_scripts"] = ActiveScripts, ["script_events_per_second"] = ScriptEventsPerSecond,
        },
        ["verdict"] = Verdict,
    };

    /// <summary>"Is it me or the region?" in a few words.</summary>
    public string Verdict => (Client, Network, Server) switch
    {
        (0, 0, 0) => "all running well",
        _ => string.Join("; ", new[]
        {
            Client > 0 ? $"this computer is {Level(Client)} ({Fps:0} fps)" : null,
            Network > 0 ? $"the connection is {Level(Network)} ({PingMs:0} ms ping)" : null,
            Server > 0 ? $"the region is {Level(Server)} (time dilation {Dilation:0.00}{(RegionCause is { } c ? ", " + c : "")})" : null,
        }.Where(s => s != null)),
    };
}

public static class Where
{
    static DateTime _asked;

    /// <summary>Where the agent is now, or null before a region. The parcel is the region's (its map from ParcelOverlay
    /// and its properties); while those haven't come, the parcel's parts are unknown and the region's rules alone show.</summary>
    public static Location? Now(AgentSession s)
    {
        if (s.Client is not { Network.CurrentSim: { } sim } c) return null;
        var pos = c.Self.SimPosition;
        Parcel? parcel = null;
        try
        {
            int local = c.Parcels.GetParcelLocalID(sim, pos);
            if (local != 0 && sim.Parcels.TryGetValue(local, out var p)) parcel = p;
        }
        catch { }
        if (parcel == null && DateTime.UtcNow - _asked > TimeSpan.FromSeconds(3))
        {
            // not known yet (just arrived): asked for here, as voice asks (VoiceClient.AgentParcel)
            _asked = DateTime.UtcNow;
            try { c.Parcels.RequestParcelProperties(sim, pos.Y + 1, pos.X + 1, pos.Y - 1, pos.X - 1, 0, false); } catch { }
        }
        var f = sim.Flags;
        bool regionVoice = (f & RegionFlags.AllowVoice) != 0;
        bool noFly = (f & RegionFlags.NoFly) != 0 || parcel is { } p1 && (p1.Flags & ParcelFlags.AllowFly) == 0;
        bool noPush = (f & RegionFlags.RestrictPushObject) != 0 || parcel is { } p2 && (p2.Flags & ParcelFlags.RestrictPushObject) != 0;
        // anyone may build: the viewer's own rights (owner, group) don't clear the icon (EXT-4610)
        bool noBuild = parcel is { } p3 && (p3.Flags & ParcelFlags.CreateObjects) == 0;
        bool noVoice = !regionVoice || parcel is { } p4 && (p4.Flags & ParcelFlags.AllowVoiceChat) == 0;
        bool noScripts = (f & (RegionFlags.SkipScripts | RegionFlags.EstateSkipScripts)) != 0 || parcel is { } p5 && (p5.Flags & ParcelFlags.AllowOtherScripts) == 0;
        bool damage = (f & RegionFlags.AllowDamage) != 0 || parcel is { } p6 && (p6.Flags & ParcelFlags.AllowDamage) != 0;
        int? sale = parcel is { } p7 && (p7.Flags & ParcelFlags.ForSale) != 0 && (p7.AuthBuyerID == UUID.Zero || p7.AuthBuyerID == c.Self.AgentID) ? p7.SalePrice : null;
        return new Location(sim.Name, sim.Handle, pos, Rating(sim.Access), parcel?.Name, parcel?.LocalID ?? 0,
            noFly, noBuild, noPush, noVoice, noScripts, damage, (int)MathF.Round(c.Self.Health), parcel != null && !parcel.SeeAVs, sale);
    }

    public static string Rating(SimAccess a) => a switch
    {
        SimAccess.PG => "general",
        SimAccess.Mature => "moderate",
        SimAccess.Adult => "adult",
        _ => "unknown",
    };

    /// <summary>The region's statistics now (SimStats, about once a second) with this client's frame rate.</summary>
    public static Stats? StatsNow(AgentSession s, float fps, float targetFps)
    {
        if (s.Client is not { Network.CurrentSim: { } sim }) return null;
        var st = sim.Stats;
        return new Stats(fps, targetFps, st.LastLag, st.MissedPings, st.Dilation, st.FPS, st.PhysicsFPS, st.FrameTime, st.ScriptTime,
            st.PhysicsTime, st.NetTime, st.AgentTime, st.ImageTime, st.Agents, st.ChildAgents, st.Objects, st.ScriptedObjects, st.ActiveScripts, st.LSLIPS);
    }
}
