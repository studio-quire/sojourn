using System.Diagnostics;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.Packets;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// SOJOURN_SELF_TRACE=1: every word on the agent's own position as it arrives, for telling the region's timing from
/// LibreMetaverse's (crossings, teleports): the event queue's CrossedRegion, AgentMovementComplete, full and terse updates
/// for the agent's avatar from any region (whether it's current, and whether LibreMetaverse's local id matches), the
/// current region changing, and every change of Client.Self's position, velocity or local id seen by a 2 ms poll (which
/// catches writes no packet explains). Console lines start `self trace +MS:`. Diagnosis only.
/// </summary>
public static class SelfTrace
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SOJOURN_SELF_TRACE") == "1";
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Attach(GridClient c, ILogger log)
    {
        if (!On) return;
        void Say(string s) => log.LogInformation("self trace +{Ms}: {What}", Clock.ElapsedMilliseconds, s);
        string Name(Simulator? s) => s == null ? "?" : $"{s.Name}{(ReferenceEquals(s, c.Network.CurrentSim) ? "*" : "")}";
        c.Network.RegisterEventCallback("CrossedRegion", (_, msg, sim) =>
        {
            var m = (CrossedRegionMessage)msg;
            Say($"CrossedRegion from {Name(sim)} to {m.RegionHandle}: pos {m.Position}");
        });
        c.Network.RegisterCallback(PacketType.AgentMovementComplete, (_, e) =>
        {
            var p = (AgentMovementCompletePacket)e.Packet;
            Say($"AgentMovementComplete from {Name(e.Simulator)}: pos {p.Data.Position}");
        }, false);
        c.Network.RegisterCallback(PacketType.ObjectUpdate, (_, e) =>
        {
            foreach (var b in ((ObjectUpdatePacket)e.Packet).ObjectData)
                if (b.FullID == c.Self.AgentID && b.ObjectData is { Length: >= 60 } d)
                {
                    int at = d.Length == 76 ? 16 : 0;
                    Say($"full update from {Name(e.Simulator)} id {b.ID} (self id {c.Self.LocalID}): pos {new Vector3(d, at)} vel {new Vector3(d, at + 12)}");
                }
        }, false);
        c.Network.RegisterCallback(PacketType.ImprovedTerseObjectUpdate, (_, e) =>
        {
            foreach (var b in ((ImprovedTerseObjectUpdatePacket)e.Packet).ObjectData)
            {
                var d = b.Data;
                if (d is not { Length: >= 44 }) continue;
                uint id = Utils.BytesToUInt(d, 0);
                bool ours = e.Simulator?.ObjectsAvatars.TryGetValue(id, out var a) == true && a.ID == c.Self.AgentID;
                if (!ours && id != c.Self.LocalID) continue;
                int at = d[5] != 0 ? 22 : 6;
                var vel = new Vector3(Utils.UInt16ToFloat(d, at + 12, -128f, 128f), Utils.UInt16ToFloat(d, at + 14, -128f, 128f), Utils.UInt16ToFloat(d, at + 16, -128f, 128f));
                Say($"terse from {Name(e.Simulator)} id {id} (self id {c.Self.LocalID}{(ours ? "" : ", not the agent's avatar there")}): pos {new Vector3(d, at)} vel {vel}");
            }
        }, false);
        c.Network.SimChanged += (_, e) => Say($"current region {Name(e.PreviousSimulator)} -> {Name(c.Network.CurrentSim)} (crossing state {c.Self.GetCrossingState()})");
        var thread = new Thread(() =>
        {
            Vector3 pos = default, vel = default;
            uint id = 0;
            AgentManager.CrossingState state = default;
            while (true)
            {
                Thread.Sleep(2);
                var p = c.Self.RelativePosition;
                var v = c.Self.Velocity;
                var s = c.Self.GetCrossingState();
                if (p != pos || v != vel || c.Self.LocalID != id || s != state)
                {
                    (pos, vel, id, state) = (p, v, c.Self.LocalID, s);
                    Say($"poll: Self pos {p} vel {v} id {id} in {Name(c.Network.CurrentSim)} crossing {s}");
                }
            }
        }) { IsBackground = true, Name = "self trace" };
        thread.Start();
    }
}
