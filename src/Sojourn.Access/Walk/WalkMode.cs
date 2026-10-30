using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Render.World;
using Sojourn.Session;
using Sojourn.Session.Control;
using Sojourn.Session.Offscreen;
using LQuaternion = LibreMetaverse.Quaternion;
using LVector3 = LibreMetaverse.Vector3;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>
/// Walk mode (docs/access-app.md > Walk mode): the arrows move the avatar through the region's own movement (the
/// agent's control flags, as Radegast's 3D window and every viewer send them), never a local simulation; the local
/// collision model (the renderer out of sight, M0b) only tells the person what's around: footsteps by surface, a
/// warning before a bump, openings and doors beside them as they pass, a drop or water ahead, a sonar sweep on a key,
/// the heading on a key. A blind tester asked for this, after the audio game Shades of Doom.
/// Every cue is a sound and a line for the screen reader, each switchable and with its own
/// level (AccessSettings.Walk): sound is never the only channel. Seated on something, or with a script holding the
/// movement keys, the same arrows drive it (a vehicle, a horse, a boat) with feedback on speed, heading and what's
/// ahead. Runs on the window's thread (its 50 ms tick and key events); rays go to the renderer's thread and come back
/// here. Written by Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed partial class WalkMode
{
    readonly SessionHost _host;
    readonly PersonOps _ops;
    readonly AccessSettings _set;
    readonly OffscreenRenderer? _view;
    readonly Action<string> _say;
    public readonly CueSounds Sounds;
    AgentSession S => _host.Session;

    /// <summary>The walk pad has the focus: the arrows walk.</summary>
    public bool On { get; private set; }
    /// <summary>Stops whatever the app is walking by itself (a //go, a follow, a route) when the person takes over.</summary>
    public Action? StopOthers { get; set; }

    /// <param name="say">A cue's line or an answer: read out through the screen reader (cutting in) and kept for review.</param>
    public WalkMode(SessionHost host, PersonOps ops, AccessSettings set, OffscreenRenderer? view, Action<string> say)
    {
        _host = host;
        _ops = ops;
        _set = set;
        _view = view;
        _say = say;
        Sounds = new CueSounds(host.Audio, () => S.Client?.Self.AgentID ?? UUID.Zero) { Relative = RelativeOf };
        ApplyFootsteps();
    }

    /// <summary>The own footsteps are ours (by surface) unless the steps cue is off or text only.</summary>
    public void ApplyFootsteps()
    {
        if (_host.Audio is { } a) a.OwnFootsteps = !_set.WalkSound("steps");
    }

    // --- where the body is -----------------------------------------------------------------------------------------

    /// <summary>The avatar now: region, middle (dead-reckoned standing; on its seat seated), feet, the way it faces
    /// (radians, region frame, 0 east, counter-clockwise), its velocity (the vehicle's, seated), and the seat's linkset.</summary>
    public readonly record struct Body(ulong Region, string RegionName, Vector3 Pos, float Feet, float Yaw, Vector3 Vel, bool Flying,
        bool Sitting, IReadOnlySet<uint>? Linkset, string? SeatName);

    public Body? Me()
    {
        if (S.Client is not { Network.CurrentSim: { } sim } c) return null;
        var w = S.Region;
        var av = w?.Avatars.Values.FirstOrDefault(a => a.ID == c.Self.AgentID);
        float half = av is { Scale.Z: > 1f and < 3.5f } ? av.Scale.Z / 2 : 0.95f;
        if (c.Self.SittingOn != 0 && w != null && av != null && w.Prims.TryGetValue(c.Self.SittingOn, out var seat))
        {
            var root = S.Objects.RootOf(w, seat);
            var set = new HashSet<uint> { root.LocalID };
            foreach (var p in w.Prims.Values) if (p.ParentID == root.LocalID) set.Add(p.LocalID);
            var at = w.RegionPosition(av);
            var rot = w.RegionRotation(av);
            var name = root.Properties?.Name is { Length: > 0 } n && n != "Object" ? n : null;
            var rv = V(root.Velocity);
            // a seat moved by its script (llSetPos, keyframes) has no velocity of its own: go by how it moves
            if (new Vector2(rv.X, rv.Y).Length() < 0.05f && Environment.TickCount64 - _rawAt < 800) rv = _estVel;
            return new Body(sim.Handle, sim.Name, V(at), at.Z - half, YawOf(rot), rv, false, true, set, name);
        }
        var pos = V(S.Motion.Position(c));
        var vel = V(c.Self.Velocity);
        long now = Environment.TickCount64;
        if (new Vector2(vel.X, vel.Y).Length() < 0.05f && now - _rawAt < 800 && _estVel.LengthSquared() > 0.01f)
        {
            // the region's word on the velocity says still while the position it sends moves on: go by the position
            vel = _estVel;
            pos = V(c.Self.SimPosition) + new Vector3(vel.X, vel.Y, 0) * Math.Min(1.5f, (now - _rawAt) / 1000f);
        }
        return new Body(sim.Handle, sim.Name, pos, pos.Z - half, YawOf(c.Self.Movement.BodyRotation), vel, c.Self.Movement.Fly, false, null, null);
    }

    LVector3 _raw;
    long _rawAt;
    Vector3 _estVel;
    static readonly StreamWriter? WalkLog = OpenLog();

    static StreamWriter? OpenLog()
    {
        // SOJOURN_WALK_LOG=path: a line per probe (positions, velocities, what the rays met), for tuning the cues
        if (Environment.GetEnvironmentVariable("SOJOURN_WALK_LOG") is not { Length: > 0 } path) return null;
        try { return new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }; }
        catch (Exception) { return null; }
    }

    public static void Log(string line) { if (WalkLog != null) lock (WalkLog) WalkLog.WriteLine($"{Environment.TickCount64} {line}"); }

    /// <summary>The velocity from the positions the region sends (each tick on the window's thread): a fallback for when
    /// its reported velocity says still while the avatar moves.</summary>
    void Track()
    {
        if (S.Client is not { } c) return;
        var raw = c.Self.SimPosition;
        if (c.Self.SittingOn != 0 && S.Region is { } w && w.Avatars.Values.FirstOrDefault(a => a.ID == c.Self.AgentID) is { } av) raw = w.RegionPosition(av);
        long now = Environment.TickCount64;
        if (raw == _raw) return;
        float dt = (now - _rawAt) / 1000f;
        var d = V(raw - _raw);
        _estVel = _rawAt != 0 && dt is > 0.03f and < 3f && d.Length() < 20 ? d / dt : Vector3.Zero;
        _raw = raw;
        _rawAt = now;
    }

    static Vector3 V(LVector3 v) => new(v.X, v.Y, v.Z);
    static float YawOf(LQuaternion q)
    {
        var f = LVector3.UnitX * q;
        return MathF.Atan2(f.Y, f.X);
    }

    static Vector3 Dir(float yaw) => new(MathF.Cos(yaw), MathF.Sin(yaw), 0);

    /// <summary>A compass bearing (0 north, clockwise) for a yaw.</summary>
    static double Compass(float yaw) => Bearings.Norm(90 - yaw * 180 / Math.PI);

    /// <summary>Where a cue about a direction plays: a metre from the head that way (global).</summary>
    Vector3d? Around(Body b, float yaw)
    {
        var head = b.Pos + new Vector3(0, 0, 0.7f) + Dir(yaw);
        Utils.LongToUInts(b.Region, out var gx, out var gy);
        return new Vector3d(gx + head.X, gy + head.Y, head.Z);
    }

    void Cue(string cue, CueSounds.Cue sound, string? text, Body? b = null, float? yaw = null)
    {
        if (_set.WalkSound(cue)) Sounds.Play(sound, _set.WalkLevel(cue), b is { } body && yaw is { } y ? Around(body, y) : null);
        if (text != null && _set.WalkText(cue)) Line(text);
    }

    /// <summary>A line for the screen reader (and the pad's own text, for anyone looking).</summary>
    void Line(string text)
    {
        LastLine = text;
        lock (_recent) { _recent.Enqueue(text); while (_recent.Count > 20) _recent.Dequeue(); }
        _say(text);
        Lines?.Invoke(text);
    }

    public string? LastLine { get; private set; }
    readonly Queue<string> _recent = new();
    /// <summary>The last twenty lines walk mode said (the status op).</summary>
    public string[] RecentLines { get { lock (_recent) return _recent.ToArray(); } }
    /// <summary>The surface underfoot as last probed (ground, water, wood, stone ...), or null.</summary>
    public string? Surface => _surface;
    public event Action<string>? Lines;

    // --- the tick --------------------------------------------------------------------------------------------------

    long _lastTick;

    /// <summary>The window's 50 ms tick: the listener, held keys to the region, footsteps, the cues' rays.</summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        double dt = _lastTick == 0 ? 0.05 : Math.Clamp((now - _lastTick) / 1000.0, 0, 0.25);
        _lastTick = now;
        Track();
        if (Me() is not { } b) return;
        Listen(b);
        Drive(b, dt, now);
        Steps(b, dt);
        if (_view != null) _view.Moving = On || Speed(b) > 0.3f;   // in walk mode the model is kept fresher
        Probe(b, now);
        Crossing(b, now);
        BeaconTick(b, now);
        ViewState();
    }

    /// <summary>World audio and voice hear from the avatar's head, facing where it faces (Access has no camera of its
    /// own; without this nothing placed in the world played at all).</summary>
    void Listen(Body b)
    {
        var pose = new CameraPose(b.Region, b.Pos + new Vector3(0, 0, 0.7f), Dir(b.Yaw), false);
        _host.Audio?.Listen(pose);
        _host.Voice?.Voice.SetListener(pose.Region, pose.Position, pose.Forward, Vector3.UnitZ);
    }

    bool _viewDown;

    /// <summary>The view out of sight down (a lost GPU, starting again in a few seconds) while walk mode has the keys:
    /// said, so silence isn't taken for open ground, and said again when the cues are back.</summary>
    void ViewState()
    {
        if (_view == null) return;
        bool down = !_view.Running;
        if (down == _viewDown) return;
        _viewDown = down;
        if (On) Line(down ? "Walk cues paused: the 3D view is starting again." : "Walk cues back.");
    }

    static float Speed(Body b) => new Vector2(b.Vel.X, b.Vel.Y).Length();

    // --- footsteps ---------------------------------------------------------------------------------------------------

    double _phase;
    string? _surface, _toldSurface;
    long _surfaceAt;

    void Steps(Body b, double dt)
    {
        float speed = Speed(b);
        if (b.Flying || b.Sitting || speed < 0.4f || Environment.TickCount64 - _surfaceAt > 1500 || _surface == null) { _phase = 0.5; return; }
        bool running = speed > 4.2f;
        // a footfall every half stride, as the viewer times them: ~0.5 s walking at 3.2 m/s, ~0.33 s running at 5.1
        double period = running ? 0.33 * Math.Clamp(5.1 / speed, 0.7, 1.5) : 0.5 * Math.Clamp(3.2 / speed, 0.7, 1.8);
        _phase += dt / period;
        if (_phase < 1) return;
        _phase -= Math.Floor(_phase);
        var sound = _surface switch
        {
            "wood" => CueSounds.Cue.StepWood, "stone" => CueSounds.Cue.StepStone, "metal" => CueSounds.Cue.StepMetal,
            "glass" => CueSounds.Cue.StepGlass, "water" => CueSounds.Cue.StepWater, "ground" => CueSounds.Cue.StepGround,
            _ => CueSounds.Cue.StepSoft,
        };
        // at the feet, a little ahead: placed, so they sit under the person rather than inside the head
        if (_set.WalkSound("steps"))
        {
            Utils.LongToUInts(b.Region, out var gx, out var gy);
            var feet = b.Pos + Dir(b.Yaw) * 0.3f;
            Sounds.Play(sound, _set.WalkLevel("steps"), new Vector3d(gx + feet.X, gy + feet.Y, b.Feet));
        }
        if (_surface != _toldSurface)
        {
            if (_toldSurface != null && _set.WalkText("steps")) Line($"On {_surface}.");
            _toldSurface = _surface;
        }
    }

    static string Material(Primitive p) => p.PrimData.Material switch
    {
        LibreMetaverse.Material.Stone => "stone", LibreMetaverse.Material.Metal => "metal", LibreMetaverse.Material.Glass => "glass",
        LibreMetaverse.Material.Wood => "wood", LibreMetaverse.Material.Flesh => "flesh", LibreMetaverse.Material.Plastic => "plastic",
        LibreMetaverse.Material.Rubber => "rubber", _ => "wood",
    };
}
