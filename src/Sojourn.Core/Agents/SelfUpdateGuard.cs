using System.Diagnostics;
using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// Keeps other objects' movement off the agent's own position. LibreMetaverse 3.2.0 (ObjectManager's
/// ImprovedTerseObjectUpdateHandler) moves Client.Self by any terse update whose local id is the agent's, from any
/// region: local ids are only unique within a region (OpenSim starts each region's at a random base, so there they
/// practically never meet; SL's simulators number their own), so an object in a neighbour (every region the agent has a
/// child circuit to sends its moving things), or in the old region after a teleport or crossing, that happens to have
/// the agent's id moved the agent's position, velocity and rotation there, until the next update of its own put it
/// back (in the simulator with the ids made to meet, SOJOURN_SELF_ID_COLLIDE: the view cut to a falling prim in the
/// neighbour and followed it down).
/// After a teleport the id is still the old region's until the new region's full update for the avatar comes (WorldHub's
/// SelfIn), and an object in the new region with that id did the same. Its full updates were already checked (the
/// current region only, and by the avatar's own UUID).
/// So the handler is replaced: an update is the agent's only from the current region and only where that region has
/// the agent's own avatar under that id. A packet holding a block that isn't goes to LibreMetaverse without it, and the
/// block updates its own object as LibreMetaverse would (decoded the same way, the TerseObjectUpdate event raised for
/// it), never the agent. By reflection on LibreMetaverse's private handler, event field and helpers: recheck them on an
/// upgrade (it logs a warning and leaves LibreMetaverse's handler alone if they're gone).
/// SOJOURN_SELF_GUARD=0 leaves LibreMetaverse's handler in place, for comparing.
/// Written by Claude Opus 5.5, 2026-10-28.
/// </summary>
public static class SelfUpdateGuard
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    static readonly MethodInfo? Handler = typeof(ObjectManager).GetMethod("ImprovedTerseObjectUpdateHandler", Any);
    static readonly FieldInfo? TerseEvent = typeof(ObjectManager).GetField("m_TerseObjectUpdate", Any);
    static readonly MethodInfo? GetAvatar = typeof(ObjectManager).GetMethod("GetAvatar", Any, [typeof(Simulator), typeof(uint), typeof(UUID)]);
    static readonly MethodInfo? GetPrimitive = typeof(ObjectManager).GetMethod("GetPrimitive", Any, [typeof(Simulator), typeof(uint), typeof(UUID)]);
    static readonly MethodInfo? GltfOverride = typeof(ObjectManager).GetMethod("ApplyCachedGLTFMaterialOverride", Any);
    static bool _warned;

    public static void Attach(GridClient c, ILogger log)
    {
        bool off = Environment.GetEnvironmentVariable("SOJOURN_SELF_GUARD") == "0";
        (uint First, uint Last) collide = Environment.GetEnvironmentVariable("SOJOURN_SELF_ID_COLLIDE")?.Split('-') is { } cr
            && uint.TryParse(cr[0], out var c0) ? (c0, cr.Length > 1 && uint.TryParse(cr[1], out var c1) ? c1 : c0) : (0, 0);
        if (off && collide.First == 0) return;
        if (Handler == null || TerseEvent == null || GetAvatar == null || GetPrimitive == null)
        {
            if (!_warned) log.LogWarning("self update guard: LibreMetaverse's terse update handler isn't as expected; its own stays (other regions' objects can move the agent)");
            _warned = true;
            return;
        }
        var original = (EventHandler<PacketReceivedEventArgs>)Delegate.CreateDelegate(typeof(EventHandler<PacketReceivedEventArgs>), c.Objects, Handler);
        c.Network.UnregisterCallback(PacketType.ImprovedTerseObjectUpdate, original);
        // synchronous, as LibreMetaverse's was: on the packet thread, in order with the full updates
        c.Network.RegisterCallback(PacketType.ImprovedTerseObjectUpdate, (sender, e) =>
        {
            long arrived = Stopwatch.GetTimestamp();
            try
            {
                if (collide.First != 0) Collide(c, e, collide);
                if (off) original(sender, e);
                else Terse(c, original, sender, e, log, arrived);
            }
            catch (Exception ex) { log.LogDebug(ex, "self update guard"); }
        }, false);
        if (off) return;
        // (LibreMetaverse moves Client.Self by a full update only from the current region, by the avatar's own UUID)
        c.Objects.AvatarUpdate += (_, e) =>
        {
            if (e.Avatar.ID == c.Self.AgentID && e.Avatar.ParentID == 0 && ReferenceEquals(e.Simulator, c.Network.CurrentSim))
                Moved(c, e.Simulator, e.Avatar.Position, Stopwatch.GetTimestamp());
        };
    }

    /// <summary>For testing (SOJOURN_SELF_ID_COLLIDE=LOCALID or FIRST-LAST): OpenSim starts each region's local ids at a
    /// random base, so a neighbour's object never shares the agent's id there as it can on SL; terse updates for those
    /// objects from any region but the current one are given the agent's local id, as if their region had numbered them
    /// so. With SOJOURN_SELF_GUARD=0 they then reach LibreMetaverse's handler as they are.</summary>
    static void Collide(GridClient c, PacketReceivedEventArgs e, (uint First, uint Last) ids)
    {
        uint self = c.Self.LocalID;
        if (self == 0 || ReferenceEquals(e.Simulator, c.Network.CurrentSim)) return;
        foreach (var b in ((ImprovedTerseObjectUpdatePacket)e.Packet).ObjectData)
            if (IdOf(b) is var id && id >= ids.First && id <= ids.Last) Utils.UIntToBytes(self, b.Data, 0);
    }

    /// <summary>Whether an update for <paramref name="localId"/> from <paramref name="sim"/> is the agent's own.</summary>
    static bool Ours(GridClient c, Simulator? sim, uint localId) =>
        sim != null && ReferenceEquals(sim, c.Network.CurrentSim)
        && (!c.Settings.World.TrackObjects || sim.ObjectsAvatars.TryGetValue(localId, out var a) && a.ID == c.Self.AgentID);

    static void Terse(GridClient c, EventHandler<PacketReceivedEventArgs> original, object? sender, PacketReceivedEventArgs e, ILogger log, long arrived)
    {
        var terse = (ImprovedTerseObjectUpdatePacket)e.Packet;
        uint self = c.Self.LocalID;
        if (self == 0 || !terse.ObjectData.Any(b => IdOf(b) == self))
        {
            original(sender, e);
            return;
        }
        if (Ours(c, e.Simulator, self))
        {
            original(sender, e);
            foreach (var b in terse.ObjectData)
                if (IdOf(b) == self && PositionOf(b) is { } p) Moved(c, e.Simulator, p, arrived);
            return;
        }
        var others = terse.ObjectData.Where(b => IdOf(b) != self).ToArray();
        if (others.Length > 0)
            original(sender, new PacketReceivedEventArgs(new ImprovedTerseObjectUpdatePacket { Header = terse.Header, RegionData = terse.RegionData, ObjectData = others }, e.Simulator));
        foreach (var b in terse.ObjectData)
            if (IdOf(b) == self) Apply(c, e.Simulator, b, terse.RegionData.TimeDilation);
        Note(log, e.Simulator, self);
    }

    static uint IdOf(ImprovedTerseObjectUpdatePacket.ObjectDataBlock b) => b.Data is { Length: >= 4 } d ? Utils.BytesToUInt(d, 0) : 0;

    /// <summary>A terse block's position (after the id, state, the avatar flag and an avatar's collision plane).</summary>
    static Vector3? PositionOf(ImprovedTerseObjectUpdatePacket.ObjectDataBlock b)
    {
        var d = b.Data;
        if (d is not { Length: >= 18 }) return null;
        int at = d[5] != 0 ? 22 : 6;
        return d.Length >= at + 12 ? new Vector3(d, at) : null;
    }

    // The last update that moved the agent (terse here, full ones by ObjectManager.AvatarUpdate): from which region, when
    // (UTC, and Stopwatch ticks as its packet came in) and where to. The region's arrival reply (AgentMovementComplete)
    // carries where the region placed the agent as it took it in; the avatar's own updates only start after that, so one
    // heard before the reply is newer than it.
    sealed class Last { public ulong Handle; public DateTime At; public long Ticks; public Vector3 Pos; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GridClient, Last> LastSelf = new();

    static void Moved(GridClient c, Simulator sim, Vector3 pos, long ticks)
    {
        var l = LastSelf.GetValue(c, _ => new Last());
        lock (l) (l.Handle, l.At, l.Ticks, l.Pos) = (sim.Handle, DateTime.UtcNow, ticks, pos);
    }

    /// <summary>When the agent's last update came in (Stopwatch ticks, as its packet arrived) and the position it gave,
    /// so the drawing can date it by its arrival rather than by the first frame that sees it, up to a frame later
    /// (MotionSmoother.Position). Null before any (or with SOJOURN_SELF_GUARD=0).</summary>
    public static (long Ticks, Vector3 Pos)? LastArrival(GridClient c)
    {
        if (!LastSelf.TryGetValue(c, out var l)) return null;
        lock (l) return l.Ticks == 0 ? null : (l.Ticks, l.Pos);
    }

    /// <summary>
    /// For an arrival reply from <paramref name="handle"/> (TeleportWatch's): the position of the agent's last update
    /// from that region, if it's current and has sent one since <paramref name="since"/> (the teleport's TeleportFinish),
    /// so the reply is the older word; null otherwise. Only updates this guard let through count (none with
    /// SOJOURN_SELF_GUARD=0).
    /// </summary>
    public static Vector3? NewerThanReply(GridClient c, ulong handle, DateTime since)
    {
        if (!LastSelf.TryGetValue(c, out var l)) return null;
        Vector3 pos;
        lock (l)
        {
            if (l.Handle != handle || l.At <= since) return null;
            pos = l.Pos;
        }
        if (c.Network.CurrentSim?.Handle != handle || c.Self.SittingOn != 0) return null;
        return pos;
    }

    /// <summary>One block, to its own object only: LibreMetaverse's decoding and updating, without Client.Self.</summary>
    static void Apply(GridClient c, Simulator sim, ImprovedTerseObjectUpdatePacket.ObjectDataBlock block, ushort dilation)
    {
        var d = block.Data;
        int pos = 4;
        var u = new ObjectMovementUpdate { LocalID = Utils.BytesToUInt(d, 0), State = d[pos++], Avatar = d[pos++] != 0 };
        if (u.Avatar) { u.CollisionPlane = new Vector4(d, pos); pos += 16; }
        u.Position = new Vector3(d, pos); pos += 12;
        u.Velocity = new Vector3(Utils.UInt16ToFloat(d, pos, -128f, 128f), Utils.UInt16ToFloat(d, pos + 2, -128f, 128f), Utils.UInt16ToFloat(d, pos + 4, -128f, 128f));
        pos += 6;
        u.Acceleration = new Vector3(Utils.UInt16ToFloat(d, pos, -64f, 64f), Utils.UInt16ToFloat(d, pos + 2, -64f, 64f), Utils.UInt16ToFloat(d, pos + 4, -64f, 64f));
        pos += 6;
        u.Rotation = new Quaternion(Utils.UInt16ToFloat(d, pos, -1f, 1f), Utils.UInt16ToFloat(d, pos + 2, -1f, 1f),
            Utils.UInt16ToFloat(d, pos + 4, -1f, 1f), Utils.UInt16ToFloat(d, pos + 6, -1f, 1f));
        pos += 8;
        u.AngularVelocity = new Vector3(Utils.UInt16ToFloat(d, pos, -64f, 64f), Utils.UInt16ToFloat(d, pos + 2, -64f, 64f), Utils.UInt16ToFloat(d, pos + 4, -64f, 64f));
        // (a terse update's texture entry carries 4 bytes before it, as LibreMetaverse reads it)
        if (block.TextureEntry.Length != 0) u.Textures = new Primitive.TextureEntry(block.TextureEntry, 4, block.TextureEntry.Length - 4);

        bool track = c.Settings.World.TrackObjects;
        var obj = !track ? null : (Primitive?)(u.Avatar ? GetAvatar!.Invoke(c.Objects, [sim, u.LocalID, UUID.Zero]) : GetPrimitive!.Invoke(c.Objects, [sim, u.LocalID, UUID.Zero]));
        // the event before the object changes, on the thread pool, as LibreMetaverse raises it
        if (TerseEvent!.GetValue(c.Objects) is EventHandler<TerseObjectUpdateEventArgs> handler)
            ThreadPool.QueueUserWorkItem(_ => handler(c.Objects, new TerseObjectUpdateEventArgs(sim, obj!, u, dilation)));
        if (obj is null) return;
        obj.Position = u.Position;
        obj.Rotation = u.Rotation;
        obj.Velocity = u.Velocity;
        obj.CollisionPlane = u.CollisionPlane;
        obj.Acceleration = u.Acceleration;
        obj.AngularVelocity = u.AngularVelocity;
        obj.PrimData.State = u.State;
        if (u.Textures is not null)
        {
            obj.Textures = u.Textures;
            GltfOverride?.Invoke(GltfOverride.IsStatic ? null : c.Objects, [sim, obj]);
        }
    }

    // each region and id once in the console: how often this happens is worth knowing on the main grid
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, uint), byte> Noted = new();

    static void Note(ILogger log, Simulator sim, uint id)
    {
        if (Noted.TryAdd((sim.Handle, id), 0))
            log.LogInformation("self update guard: an update in {Region} for object {Id}, the agent's own local id, kept off the agent ({Why})",
                sim.Name, id, sim.Client.Network.CurrentSim == sim ? "the id is another region's" : "not the current region");
    }
}
