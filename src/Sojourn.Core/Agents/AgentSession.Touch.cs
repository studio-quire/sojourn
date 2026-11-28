using LibreMetaverse;
using LibreMetaverse.Packets;
using Microsoft.Extensions.Logging;

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
        if (RlvActive && prim != null && RlvRefused(RlvTouch(prim.ID)) != null) return;
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

    // a touch held for as long as the person holds a key (Sojourn Access's Enter on a part, as a mouse button held on it):
    // requestTouch and SojournHud.press with until_key_up hold their press as long as the key was down
    volatile TouchPress _touchPress = TouchPress.Over();

    /// <summary>The touch key went down at <paramref name="at"/> (Environment.TickCount64 ms, when the key happened, not
    /// when it was handled): a touch with until_key_up started after this goes by this press. A press still open is let go.</summary>
    public void TouchKeyDown(long at)
    {
        var old = _touchPress;
        _touchPress = new TouchPress(at);
        old.Release(at);
    }

    /// <summary>The key came up at <paramref name="at"/>, or the window lost the keyboard: the press is over. Seen more than
    /// once (the app's keyboard hook as it happens, its window's messages later), the first counts.</summary>
    public void TouchKeyUp(long at) => _touchPress.Release(at);

    /// <summary>The touch key's press now: a touch with until_key_up takes it as it starts.</summary>
    public TouchPress CurrentTouchPress => _touchPress;

    /// <summary>One press of the touch key, held until <see cref="Release"/>: when it went down and came up, so a touch
    /// that reaches the region late (its surface looked for first, on a slow PC) is still held as long as the key was.</summary>
    public sealed class TouchPress(long down)
    {
        long _up;
        readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Down { get; } = down;
        /// <summary>0 while the key is down.</summary>
        public long Up => Interlocked.Read(ref _up);
        public Task Released => _released.Task;

        /// <summary>The key came up at <paramref name="at"/>; a release from before this press went down (a late one of
        /// the press before) is passed over.</summary>
        public void Release(long at)
        {
            if (at < Down) return;
            if (Interlocked.CompareExchange(ref _up, Math.Max(at, Down + 1), 0) == 0) _released.TrySetResult();
        }

        internal static TouchPress Over()
        {
            var p = new TouchPress(0);
            p.Release(1);
            return p;
        }

        /// <summary>After the press reached the region at <paramref name="pressedAt"/>: waits until it's as long as the key
        /// was held (at most <paramref name="capMs"/>), off the window's thread so a busy window doesn't lengthen it.</summary>
        public async Task HoldAsync(long pressedAt, int capMs)
        {
            await Task.WhenAny(Released, Task.Delay(capMs)).ConfigureAwait(false);
            long length = Up > 0 ? Up - Down : capMs;
            long left = Math.Min(length, capMs) - (Environment.TickCount64 - pressedAt);
            if (left > 0) await Task.Delay((int)left).ConfigureAwait(false);
        }
    }

    /// <summary>Sit on a prim of a region, at a point of it (the viewer sends the click's offset from the prim, in its
    /// frame, so a seat without a sit target seats you near where you clicked).</summary>
    public void SendSit(ulong region, UUID id, Vector3 offset)
    {
        if (RlvActive && RlvRefused(Rlv.CanSit(id, _rlvHost().Object(id)?.Position)) != null) return;
        SendSitUnchecked(region, id, offset);
    }

    /// <summary>A sit RLV has already allowed (its own @sit force).</summary>
    internal void SendSitUnchecked(ulong region, UUID id, Vector3 offset)
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
        LogControls(e.Simulator, p);
    }

    // which region took or let go of what, in the log: a vehicle's keys read as let go at a crossing on Second Life
    // (the blind tester's scooter into Berbera, 2026-11-08) and the log couldn't say which region's word did it.
    // At most 30 lines a minute: a script taking its controls on a timer would otherwise fill it
    long _controlsLogMinute;
    int _controlsLogged;

    void LogControls(Simulator? sim, ScriptControlChangePacket p)
    {
        long minute = Environment.TickCount64 / 60_000;
        if (minute != _controlsLogMinute) { _controlsLogMinute = minute; _controlsLogged = 0; }
        if (++_controlsLogged > 30) return;
        string Counts(int[] c)
        {
            lock (_kept) return string.Join(" ", Enumerable.Range(0, c.Length).Where(i => c[i] > 0).Select(i => $"{(ScriptControlChange)(1u << i)}{(c[i] > 1 ? $"x{c[i]}" : "")}"));
        }
        var what = string.Join("; ", p.Data.Select(d => $"{(d.TakeControls ? "took" : "let go of")} {(ScriptControlChange)d.Controls}{(d.PassToAgent ? " (passed on)" : "")}"));
        string region = sim?.Name is { Length: > 0 } n ? n : sim?.IPEndPoint.ToString() ?? "?";
        bool current = Client?.Network.CurrentSim is { } cur && ReferenceEquals(cur, sim);
        Log.LogInformation("controls: {Region}{Current} {What}; held now: {Kept}{Passed}", region, current ? "" : " (not the current region)", what,
            Counts(_kept) is { Length: > 0 } k ? k : "none kept", Counts(_passed) is { Length: > 0 } ps ? $", passed on {ps}" : "");
    }

    void ResetControlsTaken()
    {
        lock (_kept) { Array.Clear(_kept); Array.Clear(_passed); }
    }

    /// <summary>Release Keys (Safety.ReleaseControls): every count back to none.</summary>
    internal void ForgetControlsTaken() => ResetControlsTaken();

    /// <summary>After a teleport, what's still held, in the log (nothing when nothing is).</summary>
    void LogControlsKept()
    {
        if (ControlsTaken == ScriptControlChange.None) return;
        Log.LogInformation("controls: still held after the teleport: kept {Kept}, passed on {Passed}", ControlsKept, Held(_passed));
    }
}
