using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Microsoft.Extensions.Logging;
using Sojourn.Core;
using Sojourn.Core.Agents;
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;

namespace Sojourn.Session.Building;

/// <summary>
/// Building by hand (docs/building.md > By hand): the person grabs a thing, moves and turns it, and lets go. This is
/// the part any host shares: whether they may move it, what goes to the region while it's held and when it's let go,
/// the undo step, and what came of it. The VR client's hands and the desktop's mouse drive it
/// (Client/Building/HandBuild.cs); the assistant turns it on and hands a thing over through SojournHandBuild.
///
/// As the viewer's move tool: the whole linked set moves (MultipleObjectUpdate with position, rotation and the linked
/// flag on the root, as LLSelectMgr sends with "edit linked parts" off), the object is selected while held (the region
/// holds a selected physical object still), and a last update goes on release. The official viewer today moves the
/// selection locally while dragging and sends on mouse up; its translate tool still carries "throttle updates to 10 per
/// second" from when it sent while dragging, which is what this does, so the region (and anyone watching) sees the move
/// as it happens and can refuse it early. Moving needs modify rights, or move rights on your own; another person's
/// object only once it was chosen on purpose (the thing's card, the assistant). RLV's @edit family holds. Nothing here
/// spends. Claude Opus 5.5, 2026-11-02.
/// </summary>
public sealed class HandMove
{
    readonly AgentSession _s;
    readonly object _lock = new();

    public HandMove(AgentSession s) => _s = s;

    /// <summary>Building by hand is on: the trigger grabs rather than touches.</summary>
    public bool On { get; private set; }

    /// <summary>The thing chosen on purpose (by the card's move key or the assistant), which may be someone else's.</summary>
    public UUID Chosen { get; private set; }

    /// <summary>On, off, chosen, held or settled: the host redraws.</summary>
    public event Action? Changed;

    public void Start(UUID chosen, string by)
    {
        lock (_lock)
        {
            On = true;
            if (chosen != UUID.Zero) Chosen = chosen;
        }
        _s.Log.LogInformation("building by hand: on ({By}{What})", by, chosen != UUID.Zero ? $", {Name(chosen)} chosen" : "");
        Changed?.Invoke();
    }

    public void Stop(string why)
    {
        if (!On) return;
        if (_grab is { } g) End(g, g.Pos, g.Rot);
        lock (_lock)
        {
            On = false;
            Chosen = UUID.Zero;
        }
        _s.Log.LogInformation("building by hand: off ({Why})", why);
        Changed?.Invoke();
    }

    string Name(UUID id) =>
        _s.Region?.PrimById(id) is { } p && _s.Region is { } w && _s.Objects.RootOf(w, p).Properties?.Name is { Length: > 0 } n ? n : "an object";

    // --- may they? -------------------------------------------------------------------------------------------------

    /// <summary>The root of the thing a prim belongs to, if the person may move it by hand now; else why not, in plain
    /// words for the readout (English: the host translates). chosen: picked on purpose, so another person's
    /// modifiable object is allowed. tell: an RLV refusal goes on the feed too (a press, not a hover).</summary>
    public (Primitive? Root, string? Why) May(ulong region, UUID prim, bool chosen, bool tell = false)
    {
        if (_s.Client is not { Network.CurrentSim: { } sim } c) return (null, "not in a region");
        if (region != sim.Handle) return (null, "it's in the next region: go over to it first");
        if (_s.Region is not { } w || w.PrimById(prim) is not { } p) return (null, "it's gone");
        if (_s.Objects.IsAttachment(w, p, out _, out var root)) return (null, "worn things aren't moved by hand yet");
        if (w.Avatars.ContainsKey(root.LocalID)) return (null, "that's someone");
        var flags = _s.State.PrimFlagsOf(root);
        bool mine = (flags & PrimFlags.ObjectYouOwner) != 0;
        bool modify = (flags & PrimFlags.ObjectModify) != 0;
        bool move = (flags & PrimFlags.ObjectMove) != 0;
        if (!modify && !(mine && move)) return (null, mine ? "it's locked" : "you may not modify it");
        if (!mine && !chosen && root.ID != Chosen) return (null, "it isn't yours: choose it on its card first");
        if (_s.RlvActive && _s.Rlv.CanEdit(root.ID, false) is { } rlv) return (null, tell ? _s.RlvRefused(rlv) : rlv);
        return (root, null);
    }

    // --- holding it ---------------------------------------------------------------------------------------------------

    /// <summary>One grab: the root held, where it was and where it's been put.</summary>
    public sealed class Grab
    {
        public required Primitive Root { get; init; }
        public required ulong Region { get; init; }
        public required string Name { get; set; }
        public required SVector3 From { get; init; }
        public required SQuaternion FromRot { get; init; }
        public SVector3 Pos;
        public SQuaternion Rot;
        internal SVector3 SentPos;
        internal SQuaternion SentRot;
        internal long SentAt;
        internal bool WasSelected;
        internal Action<string> Record = _ => { };
    }

    Grab? _grab;

    public Grab? Holding => _grab;

    /// <summary>What came of the last move: where it went, and whether the region kept it there.</summary>
    public sealed record Outcome(UUID Id, string Name, SVector3 From, SVector3 To, SQuaternion FromRot, SQuaternion ToRot, string Result, DateTime At)
    {
        public float Moved => SVector3.Distance(From, To);
        public float Turned => Degrees(FromRot, ToRot);
    }

    public Outcome? Last { get; private set; }

    /// <summary>Takes hold of a root (after <see cref="May"/>): selects it, notes its placement for undo.</summary>
    public Grab Begin(Primitive root)
    {
        if (_grab is { } old) End(old, old.Pos, old.Rot);
        var c = _s.Client ?? throw new InvalidOperationException("not connected");
        var sim = c.Network.CurrentSim!;
        var w = _s.Region!;
        var pos = N(w.RegionPosition(root));
        var rot = N(w.RegionRotation(root));
        var g = new Grab
        {
            Root = root, Region = sim.Handle, Name = root.Properties?.Name is { Length: > 0 } n ? n : "an object",
            From = pos, FromRot = rot, Pos = pos, Rot = rot, SentPos = pos, SentRot = rot,
            WasSelected = _s.State.Selected.ContainsKey(root.LocalID),
            Record = _s.Undo.BeforeHandMove(root),
        };
        if (!g.WasSelected) c.Objects.SelectObject(sim, root.LocalID, false);
        _grab = g;
        _s.Log.LogInformation("building by hand: holding {Name} at {Pos}", g.Name, F(pos));
        Changed?.Invoke();
        return g;
    }

    /// <summary>Where the hand has it now: clamped to what the region allows (inside this region, under its height
    /// limit), sent at most 10 times a second when it has changed. Returns the pose to draw.</summary>
    public (SVector3 Pos, SQuaternion Rot) Move(Grab g, SVector3 pos, SQuaternion rot)
    {
        (g.Pos, g.Rot) = (Clamp(pos), SQuaternion.Normalize(rot));
        long now = Environment.TickCount64;
        // (a grab ended meanwhile, by Stop from another thread, sends nothing more)
        if (ReferenceEquals(_grab, g) && now - g.SentAt >= 100 && (SVector3.Distance(g.Pos, g.SentPos) > 0.0005f || Degrees(g.Rot, g.SentRot) > 0.05f))
            Send(g, now);
        return (g.Pos, g.Rot);
    }

    /// <summary>Let go: the last update, the selection let go, one undo step if it moved, then a check that the region
    /// kept it there (<see cref="Last"/> says).</summary>
    public void End(Grab g, SVector3 pos, SQuaternion rot)
    {
        if (!ReferenceEquals(_grab, g)) return;
        _grab = null;
        (g.Pos, g.Rot) = (Clamp(pos), SQuaternion.Normalize(rot));
        bool moved = SVector3.Distance(g.Pos, g.From) > 0.0005f || Degrees(g.Rot, g.FromRot) > 0.05f;
        if (g.Root.Properties?.Name is { Length: > 0 } n) g.Name = n;
        if (moved)
        {
            Send(g, Environment.TickCount64);
            g.Record($"moved {g.Name} by hand");
        }
        if (_s.Client is { Network.CurrentSim: { } sim } c && sim.Handle == g.Region && !g.WasSelected && !_s.State.Selected.ContainsKey(g.Root.LocalID))
            c.Objects.DeselectObject(sim, g.Root.LocalID);
        if (!moved)
        {
            Changed?.Invoke();
            return;
        }
        var o = new Outcome(g.Root.ID, g.Name, g.From, g.Pos, g.FromRot, g.Rot, "moving", DateTime.UtcNow);
        Last = o;
        _s.Log.LogInformation("building by hand: let go of {Name} at {Pos}, {Moved:0.00} m and {Turned:0.#} deg from where it was",
            g.Name, F(g.Pos), o.Moved, o.Turned);
        Changed?.Invoke();
        _ = Task.Run(() => SettleAsync(g, o));
    }

    /// <summary>Whether the region kept it where it was put: its own updates within 3 s (2 cm and a degree, the
    /// region rounds), else where it has it.</summary>
    async Task SettleAsync(Grab g, Outcome o)
    {
        string result = "the region didn't say";
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            if (_s.World.Region(_s.GridKey, g.Region) is not { } w || w.PrimById(g.Root.ID) is not { } p)
            {
                result = "gone";
                break;
            }
            var at = N(w.RegionPosition(p));
            var rot = N(w.RegionRotation(p));
            if (SVector3.Distance(at, o.To) < 0.02f && Degrees(rot, o.ToRot) < 1f) { result = "placed"; break; }
            if (i == 29)
            {
                bool back = SVector3.Distance(at, o.From) < 0.02f;
                result = back ? "refused" : "moved elsewhere";
                o = o with { To = at, ToRot = rot };
            }
        }
        if (Last?.At != o.At) return;   // a newer move since
        Last = o with { Result = result };
        if (result != "placed") _s.Log.LogInformation("building by hand: {Name}: {Result} (now at {Pos})", o.Name, result, F(o.To));
        Changed?.Invoke();
    }

    void Send(Grab g, long now)
    {
        if (_s.Client is not { Network.CurrentSim: { } sim } c || sim.Handle != g.Region) return;
        var data = new List<byte>(24);
        data.AddRange(L(g.Pos).GetBytes());
        data.AddRange(L(g.Rot).GetBytes());
        c.Network.SendPacket(new MultipleObjectUpdatePacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            ObjectData = new[]
            {
                new MultipleObjectUpdatePacket.ObjectDataBlock
                {
                    ObjectLocalID = g.Root.LocalID, Type = (byte)(UpdateType.Position | UpdateType.Rotation | UpdateType.Linked), Data = data.ToArray(),
                },
            },
        }, sim);
        (g.SentPos, g.SentRot, g.SentAt) = (g.Pos, g.Rot, now);
    }

    /// <summary>Inside the region (the viewer clips to the regions it can see; here the one you're in) and under the
    /// height objects may go (the viewer's MAX_OBJECT_Z, 4096 m).</summary>
    SVector3 Clamp(SVector3 p)
    {
        float size = _s.Client?.Network.CurrentSim is { SizeX: > 0 } sim ? sim.SizeX : 256f;
        return new SVector3(Math.Clamp(p.X, 0, size - 0.001f), Math.Clamp(p.Y, 0, size - 0.001f), Math.Clamp(p.Z, 0, 4096f));
    }

    /// <summary>Whether a pose would be clamped (the readout says it's at the edge).</summary>
    public bool AtEdge(SVector3 p) => SVector3.Distance(Clamp(p), p) > 1e-4f;

    // --- for the assistant (SojournHandBuild state) ----------------------------------------------------------------------

    public JsonObject State()
    {
        var o = new JsonObject { ["on"] = On };
        if (Chosen != UUID.Zero) o["chosen"] = new JsonObject { ["id"] = Chosen.ToString(), ["name"] = Name(Chosen) };
        if (_grab is { } g) o["holding"] = new JsonObject { ["id"] = g.Root.ID.ToString(), ["name"] = g.Name, ["pos"] = Json3(g.Pos), ["rot_euler"] = Json3(Euler(g.Rot)) };
        if (Last is { } l)
            o["last"] = new JsonObject
            {
                ["id"] = l.Id.ToString(), ["name"] = l.Name, ["from"] = Json3(l.From), ["to"] = Json3(l.To),
                ["rot_euler"] = Json3(Euler(l.ToRot)), ["moved_m"] = Math.Round(l.Moved, 3), ["turned_deg"] = Math.Round(l.Turned, 1),
                ["result"] = l.Result, ["ago_s"] = Math.Round((DateTime.UtcNow - l.At).TotalSeconds),
            };
        return o;
    }

    static JsonArray Json3(SVector3 v) => new(Math.Round(v.X, 3), Math.Round(v.Y, 3), Math.Round(v.Z, 3));

    // --- maths -------------------------------------------------------------------------------------------------------

    /// <summary>The angle between two rotations, degrees.</summary>
    public static float Degrees(SQuaternion a, SQuaternion b)
    {
        float d = MathF.Abs(SQuaternion.Dot(SQuaternion.Normalize(a), SQuaternion.Normalize(b)));
        return 2 * MathF.Acos(Math.Min(1, d)) * 180 / MathF.PI;
    }

    /// <summary>A rotation as the edit floater's X, Y, Z degrees (LLQuaternion::getEulerAngles: roll, pitch, yaw
    /// applied in that order), each in [0, 360).</summary>
    public static SVector3 Euler(SQuaternion q)
    {
        L(q).GetEulerAngles(out float roll, out float pitch, out float yaw);
        static float D(float r) { float d = r * 180 / MathF.PI % 360; return d < 0 ? d + 360 : d; }
        return new SVector3(D(roll), D(pitch), D(yaw));
    }

    static string F(SVector3 v) => $"{v.X:0.00}, {v.Y:0.00}, {v.Z:0.00}";
    static SVector3 N(Vector3 v) => new(v.X, v.Y, v.Z);
    static SQuaternion N(Quaternion q) => SQuaternion.Normalize(new SQuaternion(q.X, q.Y, q.Z, q.W));
    static Vector3 L(SVector3 v) => new(v.X, v.Y, v.Z);
    static Quaternion L(SQuaternion q) => new(q.X, q.Y, q.Z, q.W);
}
