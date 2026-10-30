using LibreMetaverse;
using LibreMetaverse.Packets;

namespace Sojourn.Core.Agents;

/// <summary>Where on an object a touch lands, as ObjectGrab's SurfaceInfo carries it (what scripts read with
/// llDetectedTouchFace, ST, UV, Pos, Normal, Binormal). Position, normal and binormal in the agent's region frame, as
/// the viewer sends them. <see cref="None"/> is the viewer's "no surface" (face -1, coordinates -1).</summary>
public readonly record struct TouchSurface(int Face, Vector2 ST, Vector2 UV, Vector3 Position, Vector3 Normal, Vector3 Binormal)
{
    public static TouchSurface None => new(-1, new Vector2(-1, -1), new Vector2(-1, -1), Vector3.Zero, Vector3.Zero, Vector3.Zero);
}

/// <summary>A prim hit on its surface: its region, local id, id, and where.</summary>
public readonly record struct TouchPoint(ulong Region, uint LocalId, UUID Id, TouchSurface Surface);

/// <summary>
/// Touching as the viewer does (LLToolGrab for a non-physical object): ObjectGrab when the button goes down,
/// ObjectGrabUpdate while it's held and the point moves, ObjectDeGrab when it comes up, each with where on the
/// surface, sent to the object's own region. And what scripts have taken of the controls (ScriptControlChange).
/// </summary>
public sealed partial class AgentSession
{
    /// <summary>The client's way to find where on an object a touch lands: the prim (a linkset's root or any of its
    /// prims), a point on it in the agent's region frame or null (where the person last pointed at it, else its middle
    /// as seen from the eye) -> the prim the ray reaches and where. Null when nothing can be worked out (no renderer, not
    /// drawn yet): the touch then goes without a surface, as the viewer's does when its pick misses.</summary>
    public Func<UUID, Vector3?, CancellationToken, Task<TouchPoint?>>? FindTouchSurface { get; set; }

    /// <summary>Where a touch on one face of a prim lands (the prim given, not its linkset; face: its texture entry
    /// index), for touching parts and faces by number (Sojourn Access, Session's Offscreen/). Null when it can't be
    /// worked out; requestTouch then goes by <see cref="FindTouchSurface"/> with that face number.</summary>
    public Func<UUID, int, CancellationToken, Task<TouchPoint?>>? FindFaceSurface { get; set; }

    Simulator? SimFor(GridClient c, ulong region) =>
        region == 0 ? c.Network.CurrentSim : c.Network.Simulators.FirstOrDefault(s => s.Handle == region) ?? c.Network.CurrentSim;

    /// <summary>The viewer's grab offset: the root's centre from the prim's, in the root's frame (zero on a root).</summary>
    static Vector3 GrabOffset(Primitive? p) => p is { ParentID: not 0 } ? -p.Position : Vector3.Zero;

    static Vector3 V3(Vector2 v) => new(v.X, v.Y, 0);

    public void SendGrab(ulong region, uint localId, TouchSurface s)
    {
        if (Client is not { } c || SimFor(c, region) is not { } sim) return;
        sim.ObjectsPrimitives.TryGetValue(localId, out var prim);
        c.Network.SendPacket(new ObjectGrabPacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            ObjectData = { LocalID = localId, GrabOffset = GrabOffset(prim) },
            SurfaceInfo = new[]
            {
                new ObjectGrabPacket.SurfaceInfoBlock
                {
                    UVCoord = V3(s.UV), STCoord = V3(s.ST), FaceIndex = s.Face, Position = s.Position, Normal = s.Normal, Binormal = s.Binormal,
                },
            },
        }, sim);
    }

    /// <summary>The point held moved (a drag across the surface). grabPosition: the dragged point in the object's region.</summary>
    public void SendGrabUpdate(ulong region, UUID id, Vector3 offsetInitial, Vector3 grabPosition, uint msSinceLast, TouchSurface s)
    {
        if (Client is not { } c || SimFor(c, region) is not { } sim) return;
        c.Network.SendPacket(new ObjectGrabUpdatePacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            ObjectData = { ObjectID = id, GrabOffsetInitial = offsetInitial, GrabPosition = grabPosition, TimeSinceLast = msSinceLast },
            SurfaceInfo = new[]
            {
                new ObjectGrabUpdatePacket.SurfaceInfoBlock
                {
                    UVCoord = V3(s.UV), STCoord = V3(s.ST), FaceIndex = s.Face, Position = s.Position, Normal = s.Normal, Binormal = s.Binormal,
                },
            },
        }, sim);
    }

    public void SendDeGrab(ulong region, uint localId, TouchSurface s)
    {
        if (Client is not { } c || SimFor(c, region) is not { } sim) return;
        c.Network.SendPacket(new ObjectDeGrabPacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            ObjectData = { LocalID = localId },
            SurfaceInfo = new[]
            {
                new ObjectDeGrabPacket.SurfaceInfoBlock
                {
                    UVCoord = V3(s.UV), STCoord = V3(s.ST), FaceIndex = s.Face, Position = s.Position, Normal = s.Normal, Binormal = s.Binormal,
                },
            },
        }, sim);
    }

    /// <summary>Sit on a prim of a region, at a point of it (the viewer sends the click's offset from the prim, in its
    /// frame, so a seat without a sit target seats you near where you clicked).</summary>
    public void SendSit(ulong region, UUID id, Vector3 offset)
    {
        if (Client is not { } c || SimFor(c, region) is not { } sim) return;
        c.Network.SendPacket(new AgentRequestSitPacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            TargetObject = { TargetID = id, Offset = offset },
        }, sim);
    }

    // --- controls scripts have taken ---------------------------------------------------------------------------------

    // the viewer's counts per control bit (LLAgent::mControlsTakenCount and mControlsTakenPassedOnCount): each take
    // adds one, each release takes one away
    readonly int[] _kept = new int[32], _passed = new int[32];

    /// <summary>Controls scripts hold now (llTakeControls), as ScriptControlChange flags (the AgentUpdate control bits).</summary>
    public ScriptControlChange ControlsTaken => Held(_kept) | Held(_passed);

    /// <summary>Those taken without passing them on: the avatar doesn't act on them itself (a vehicle, a game).</summary>
    public ScriptControlChange ControlsKept => Held(_kept);

    ScriptControlChange Held(int[] counts)
    {
        uint f = 0;
        lock (_kept)
            for (int i = 0; i < counts.Length; i++) if (counts[i] > 0) f |= 1u << i;
        return (ScriptControlChange)f;
    }

    /// <summary>LLAgent::processScriptControlChange: a block per take or release.</summary>
    void OnScriptControl(PacketReceivedEventArgs e)
    {
        if (e.Packet is not ScriptControlChangePacket p) return;
        lock (_kept)
            foreach (var d in p.Data)
            {
                var counts = d.PassToAgent ? _passed : _kept;
                for (int i = 0; i < counts.Length; i++)
                    if ((d.Controls & (1u << i)) != 0) counts[i] = Math.Max(0, counts[i] + (d.TakeControls ? 1 : -1));
            }
    }

    void ResetControlsTaken()
    {
        lock (_kept) { Array.Clear(_kept); Array.Clear(_passed); }
    }
}
