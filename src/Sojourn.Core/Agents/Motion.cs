using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>One 10 Hz clock for every agent in the process (no per-agent timers or threads).</summary>
public static class HostClock
{
    static readonly ConcurrentDictionary<object, Action<double>> Subscribers = new();
    static readonly object Gate = new();
    static Task? _loop;

    public static void Subscribe(object owner, Action<double> tick)
    {
        Subscribers[owner] = tick;
        lock (Gate) _loop ??= Task.Run(Loop);
    }

    public static void Unsubscribe(object owner) => Subscribers.TryRemove(owner, out _);

    static async Task Loop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var last = DateTime.UtcNow;
        while (await timer.WaitForNextTickAsync())
        {
            var now = DateTime.UtcNow;
            var dt = Math.Min(0.5, (now - last).TotalSeconds);
            last = now;
            foreach (var (_, tick) in Subscribers)
            {
                try { tick(dt); } catch { /* each subscriber guards itself; never let one stop the clock */ }
            }
        }
    }
}

/// <summary>
/// Moving the body the way the viewer does: held control flags in AgentUpdate (TesselAgent move/stop), a
/// client-side autopilot (LLAgent startAutoPilot), the body's facing, and the camera the region sees.
/// </summary>
public sealed class Motion
{
    readonly AgentSession _s;
    public Motion(AgentSession s) => _s = s;

    sealed class Move
    {
        public int Id;
        public DateTime Start;
        public double Secs;
        public double Forward, Strafe, Up, Turn;
        public bool Run, RunWasOn;
        public Vector3d StartPos;      // global, so a move across a region border measures right
        public TaskCompletionSource<JsonObject> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class Pilot
    {
        public Vector3 Target;          // region coordinates of the agent's region when started
        public ulong Handle;
        public double Stop;
        public DateTime LastProgress;
        public double BestDist;
        public bool Flying;
        public Queue<Vector3>? Legs;    // a route's waypoints after the current target (Router)
        public double FinalStop;
        public bool Planning;
        public DateTime PlanSince;
    }

    /// <summary>
    /// Paths around what's in the way for the autopilot (LLAgent startAutoPilot, the assistant's walks): set by an app
    /// that can plan one (Sojourn Access, on its collision model); null (the VR client) walks straight as before. Given
    /// the avatar's position and the target in its region's frame, it returns the waypoints to walk through (the last
    /// the target or the nearest free spot to it), or null to go straight. The walk waits up to 1.5 s for it, then
    /// each waypoint is passed within 0.9 m and the last met at the stop distance asked. Written by Claude Opus 5.5,
    /// 2026-10-29.
    /// </summary>
    public Func<GridClient, Vector3, Vector3, Task<IReadOnlyList<Vector3>?>>? Router { get; set; }

    Move? _move;
    Pilot? _pilot;
    int _nextId;
    double _sinceCamera;
    readonly object _lock = new();

    public bool Moving => _move is not null || _pilot is not null;

    /// <summary>Where the autopilot is walking in the end (its route's last point; region coordinates in that region),
    /// or null when it isn't walking: an app's beacon on the destination (Sojourn Access).</summary>
    public (ulong Region, Vector3 Target)? Destination
    {
        get { lock (_lock) return _pilot is { } p ? (p.Handle, p.Legs is { Count: > 0 } legs ? legs.Last() : p.Target) : null; }
    }

    /// <summary>
    /// The autopilot steers by <see cref="Position"/>, the avatar dead-reckoned from its last update, instead of the
    /// position last heard. Regions send the agent's own position when its velocity changes or it drifts from where a
    /// viewer would predict it (OpenSim: gaps of 1.5 s at a steady walk), so a host with no frame loop of its own
    /// (Sojourn Access) walked up to 2.3 m past the target with a 1.5 m stop distance; dead reckoning, it ends 1.1-1.2 m
    /// short, every time (tools/access.py test). Off by default: the VR client's walks stay as they were. Written by
    /// Claude Opus 5.5, 2026-10-29.
    /// </summary>
    public bool DeadReckon { get; set; }

    /// <summary>The avatar's position in its region: with <see cref="DeadReckon"/>, the last update carried on by its
    /// velocity since that update arrived (at most 3 s); otherwise, and sitting, the position as last heard.</summary>
    public Vector3 Position(GridClient c)
    {
        var raw = c.Self.SimPosition;
        if (!DeadReckon || c.Self.SittingOn != 0 || SelfUpdateGuard.LastArrival(c) is not { } last || last.Pos != raw) return raw;
        var secs = Math.Clamp((System.Diagnostics.Stopwatch.GetTimestamp() - last.Ticks) / (double)System.Diagnostics.Stopwatch.Frequency, 0, 3);
        return raw + c.Self.Velocity * (float)secs;
    }

    /// <summary>Compass heading of the body: 0 = north (+Y), 90 = east, clockwise.</summary>
    public static int Heading(Quaternion q)
    {
        var fwd = Vector3.UnitX * q;
        var yaw = Math.Atan2(fwd.Y, fwd.X) * 180 / Math.PI;
        return (int)Math.Round(((90 - yaw) % 360 + 360) % 360) % 360;
    }

    static Quaternion FromHeading(double bearing)
    {
        var yaw = (90 - bearing) * Math.PI / 180;
        return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)yaw);
    }

    public JsonObject Where(GridClient c)
    {
        var o = new JsonObject
        {
            ["pos"] = Json.Arr(c.Self.SimPosition),
            ["heading"] = Heading(c.Self.Movement.BodyRotation),
            ["sitting"] = c.Self.SittingOn != 0,
            ["flying"] = c.Self.Movement.Fly,
        };
        if (c.Network.CurrentSim?.Name is { Length: > 0 } r) o["region"] = r;
        return o;
    }

    // ---------- TesselAgent move / stop ----------

    public async Task<JsonObject> StartMoveAsync(GridClient c, JsonObject a, CancellationToken ct)
    {
        var secs = a.Num("secs", 1);
        if (secs <= 0 || secs > 600) throw new Leap.LeapError("secs must be in (0, 600]");
        if (a.Bool("fly") is { } fly) { c.Self.Fly(fly); c.Self.Movement.Fly = fly; }
        if (a.Num("face") is { } face) c.Self.Movement.BodyRotation = FromHeading(face);
        Move m;
        Move? replaced;
        lock (_lock)
        {
            replaced = _move;
            m = new Move
            {
                Id = ++_nextId, Start = DateTime.UtcNow, Secs = secs,
                Forward = Math.Clamp(a.Num("forward", 0), -1, 1), Strafe = Math.Clamp(a.Num("strafe", 0), -1, 1),
                Up = Math.Clamp(a.Num("up", 0), -1, 1), Turn = Math.Clamp(a.Num("turn", 0), -720, 720),
                Run = a.Bool("run", false), StartPos = c.Self.GlobalPosition,
            };
            _move = m;
            _pilot = null;
        }
        if (replaced is not null) Finish(c, replaced, "replaced");
        if (m.Run)
        {
            m.RunWasOn = c.Self.Movement.AlwaysRun;
            c.Self.Movement.AlwaysRun = true;
        }
        ApplyControls(c, m, 0);
        var start = Where(c);
        start["id"] = m.Id;
        start["secs"] = secs;
        if (!a.Bool("wait", false)) return start;
        try { return await m.Done.Task.WaitAsync(TimeSpan.FromSeconds(secs + 5), ct); }
        catch (TimeoutException) { return new JsonObject(); }
    }

    public JsonObject Stop(GridClient c)
    {
        Move? m;
        lock (_lock) { m = _move; _move = null; }
        if (m is null)
        {
            var r = Where(c);
            r["was_moving"] = false;
            return r;
        }
        var done = Finish(c, m, "stop");
        done["was_moving"] = true;
        return done;
    }

    JsonObject Finish(GridClient c, Move m, string reason)
    {
        ClearControls(c);
        if (m.Run && !m.RunWasOn) c.Self.Movement.AlwaysRun = false;
        var ev = Where(c);
        ev["event"] = "move_done";
        ev["id"] = m.Id;
        ev["reason"] = reason;
        ev["secs"] = Math.Round((DateTime.UtcNow - m.Start).TotalSeconds, 2);
        ev["moved"] = Json.R(Vector3d.Distance(m.StartPos, c.Self.GlobalPosition), 2);
        _s.Events.Log("TesselEvents", ev);
        var reply = (JsonObject)ev.DeepClone();
        reply.Remove("event");
        m.Done.TrySetResult(reply);
        return reply;
    }

    static void ClearControls(GridClient c)
    {
        var mv = c.Self.Movement;
        mv.AtPos = mv.AtNeg = mv.LeftPos = mv.LeftNeg = mv.UpPos = mv.UpNeg = false;
        mv.FastAt = mv.FastLeft = mv.FastUp = false;
        mv.NudgeAtPos = mv.NudgeAtNeg = mv.NudgeLeftPos = mv.NudgeLeftNeg = mv.NudgeUpPos = mv.NudgeUpNeg = false;
        mv.YawPos = mv.YawNeg = mv.TurnLeft = mv.TurnRight = false;
        mv.SendUpdate(true);
    }

    static void ApplyControls(GridClient c, Move m, double dt)
    {
        var mv = c.Self.Movement;
        var f = m.Forward;
        mv.AtPos = f >= 0.5; mv.AtNeg = f <= -0.5; mv.FastAt = Math.Abs(f) >= 0.5;
        mv.NudgeAtPos = f > 0 && f < 0.5; mv.NudgeAtNeg = f < 0 && f > -0.5;
        var s = m.Strafe;   // + = right = LEFT_NEG
        mv.LeftNeg = s >= 0.5; mv.LeftPos = s <= -0.5; mv.FastLeft = Math.Abs(s) >= 0.5;
        mv.NudgeLeftNeg = s > 0 && s < 0.5; mv.NudgeLeftPos = s < 0 && s > -0.5;
        mv.UpPos = m.Up > 0; mv.UpNeg = m.Up < 0; mv.FastUp = m.Up != 0;
        mv.YawNeg = m.Turn > 0; mv.YawPos = m.Turn < 0;
        mv.TurnRight = m.Turn > 0; mv.TurnLeft = m.Turn < 0;
        if (m.Turn != 0 && dt > 0)
            mv.BodyRotation = Quaternion.Normalize(mv.BodyRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(-m.Turn * dt * Math.PI / 180)));
        mv.HeadRotation = mv.BodyRotation;
        mv.SendUpdate(false);
    }

    // ---------- LLAgent startAutoPilot ----------

    public void StartAutoPilot(GridClient c, Vector3d targetGlobal, double stopDistance, bool? allowFlying) =>
        StartAutoPilot(c, targetGlobal, stopDistance, allowFlying, route: true);

    /// <param name="route">Ask the <see cref="Router"/> for a way around (false: straight, for a caller steering it itself).</param>
    public void StartAutoPilot(GridClient c, Vector3d targetGlobal, double stopDistance, bool? allowFlying, bool route)
    {
        var sim = c.Network.CurrentSim;
        if (sim is null) return;
        Utils.LongToUInts(sim.Handle, out var gx, out var gy);
        var target = new Vector3((float)(targetGlobal.X - gx), (float)(targetGlobal.Y - gy), (float)targetGlobal.Z);
        if (allowFlying is { } fl) { c.Self.Fly(fl); c.Self.Movement.Fly = fl; }
        var dist = Vector3.Distance(Flat(Position(c)), Flat(target));
        var stop = stopDistance > 0 ? stopDistance : Math.Max(0.5, Math.Sqrt(dist));
        Pilot? old;
        Move? oldMove;
        lock (_lock)
        {
            old = _pilot;
            oldMove = _move;
            _move = null;
            _pilot = new Pilot
            {
                Target = target, Handle = sim.Handle, Stop = stop, LastProgress = DateTime.UtcNow, BestDist = dist,
                Flying = c.Self.Movement.Fly,
            };
        }
        if (old is not null) _s.Events.Log("LLAutopilot", new JsonObject { ["success"] = false });
        if (oldMove is not null) Finish(c, oldMove, "replaced");
        if (route && Router is { } router && !c.Self.Movement.Fly) Plan(c, router, target);
    }

    void Plan(GridClient c, Func<GridClient, Vector3, Vector3, Task<IReadOnlyList<Vector3>?>> route, Vector3 target)
    {
        Pilot? p;
        lock (_lock) { p = _pilot; if (p is null) return; p.Planning = true; p.PlanSince = DateTime.UtcNow; }
        _ = Task.Run(async () =>
        {
            IReadOnlyList<Vector3>? legs = null;
            try { legs = await route(c, Position(c), target); }
            catch (Exception) { /* no plan: straight */ }
            lock (_lock)
            {
                if (_pilot != p) return;
                p.Planning = false;
                if (legs is not { Count: > 1 }) return;
                p.Legs = new Queue<Vector3>(legs.Skip(1));
                p.FinalStop = p.Stop;
                p.Target = legs[0];
                p.Stop = 0.5;
                p.BestDist = Vector3.Distance(Flat(Position(c)), Flat(p.Target));
                p.LastProgress = DateTime.UtcNow;
            }
        });
    }

    /// <summary>A moving target (following someone): the walk goes on toward a new point, its progress counted afresh.
    /// False when no autopilot is walking or it's following a route.</summary>
    public bool RetargetAutoPilot(GridClient c, Vector3d targetGlobal, double stopDistance)
    {
        lock (_lock)
        {
            if (_pilot is not { Legs: null, Planning: false } p || c.Network.CurrentSim is not { } sim || sim.Handle != p.Handle) return false;
            Utils.LongToUInts(sim.Handle, out var gx, out var gy);
            p.Target = new Vector3((float)(targetGlobal.X - gx), (float)(targetGlobal.Y - gy), (float)targetGlobal.Z);
            p.Stop = stopDistance;
            p.BestDist = Vector3.Distance(Flat(Position(c)), Flat(p.Target));
            p.LastProgress = DateTime.UtcNow;
            return true;
        }
    }

    public void StopAutoPilot(GridClient c, bool success)
    {
        Pilot? p;
        lock (_lock) { p = _pilot; _pilot = null; }
        if (p is null) return;
        ClearControls(c);
        _s.Events.Log("LLAutopilot", new JsonObject { ["success"] = success });
    }

    static Vector3 Flat(Vector3 v) => new(v.X, v.Y, 0);

    void TickPilot(GridClient c, Pilot p)
    {
        if (c.Network.CurrentSim?.Handle != p.Handle) { StopAutoPilot(c, false); return; }
        var here = Position(c);
        var to = Flat(p.Target) - Flat(here);
        var dist = to.Length();
        // dead reckoning, it lets go a tick's walk early: the avatar coasts ~0.5 m after the controls are released
        var lead = DeadReckon ? Flat(c.Self.Velocity).Length() * 0.2f : 0;
        if (p.Planning && (DateTime.UtcNow - p.PlanSince).TotalSeconds < 1.5)
        {
            // the route is being worked out: stand still meanwhile
            p.LastProgress = DateTime.UtcNow;
            if (c.Self.Movement.AtPos) { c.Self.Movement.AtPos = false; c.Self.Movement.SendUpdate(false); }
            return;
        }
        if (dist <= p.Stop + lead && p.Legs is { Count: > 0 } legs)
        {
            lock (_lock)
            {
                p.Target = legs.Dequeue();
                if (legs.Count == 0) { p.Stop = p.FinalStop; p.Legs = null; }
                p.BestDist = Vector3.Distance(Flat(here), Flat(p.Target));
                p.LastProgress = DateTime.UtcNow;
            }
            return;
        }
        if (dist <= p.Stop + lead) { StopAutoPilot(c, true); return; }
        if (dist < p.BestDist - 0.1) { p.BestDist = dist; p.LastProgress = DateTime.UtcNow; }
        var patience = p.Flying ? 2.5 : 1.5;
        if ((DateTime.UtcNow - p.LastProgress).TotalSeconds > patience + 1.0) { StopAutoPilot(c, false); return; }
        var mv = c.Self.Movement;
        var yaw = (float)Math.Atan2(to.Y, to.X);
        mv.BodyRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw);
        mv.HeadRotation = mv.BodyRotation;
        mv.AtPos = true;
        mv.FastAt = dist > 4;
        if (p.Flying)
        {
            var dz = p.Target.Z - here.Z;
            mv.UpPos = dz > 1; mv.UpNeg = dz < -1;
        }
        mv.SendUpdate(false);
    }

    // ---------- the clock ----------

    public void Tick(GridClient c, double dt)
    {
        Move? m;
        Pilot? p;
        lock (_lock) { m = _move; p = _pilot; }
        if (m is not null)
        {
            if ((DateTime.UtcNow - m.Start).TotalSeconds >= m.Secs)
            {
                lock (_lock) if (_move == m) _move = null;
                Finish(c, m, "time");
            }
            else ApplyControls(c, m, dt);
        }
        else if (p is not null) TickPilot(c, p);

        _sinceCamera += dt;
        if (m is not null || p is not null || _sinceCamera >= 1.0)
        {
            _sinceCamera = 0;
            _s.Camera.Update(c);
        }
    }
}

/// <summary>
/// The camera: the viewer's default follow camera behind the avatar, or a pose set by LLAgent setCameraParams.
/// Its pose goes to the region in every AgentUpdate (the interest list follows it, as with a real viewer) and is
/// what a snapshot renders.
/// </summary>
public sealed class CameraControl
{
    readonly AgentSession _s;
    public CameraControl(AgentSession s) => _s = s;

    public float Fov => _s.State.Settings.TryGetValue("CameraAngle", out var v) && Json.AsDouble(v) is { } d ? (float)d : 1.047f;

    public float FarClip => _s.State.Settings.TryGetValue("RenderFarClip", out var v) && Json.AsDouble(v) is { } d ? (float)d : _s.Config.DrawDistance;

    /// <summary>The draw distance's range (metres): the viewer's own slider stops at 512.</summary>
    public const float MinFarClip = 16, MaxFarClip = 512;

    /// <summary>Set the draw distance (what the renderer draws to and how far the region sends objects).</summary>
    public float SetFarClip(float metres)
    {
        metres = Math.Clamp(metres, MinFarClip, MaxFarClip);
        _s.State.Settings["RenderFarClip"] = (double)metres;
        if (_s.Client is { } c) Update(c);
        _s.OnSettingChanged("RenderFarClip");
        return metres;
    }

    /// <summary>Camera position and focus, region coordinates.</summary>
    public (Vector3 Pos, Vector3 Focus) Pose(GridClient c) => _s.State.CameraOverride ?? FollowPose(c);

    /// <summary>The default follow camera, whether or not a camera override is set.</summary>
    public (Vector3 Pos, Vector3 Focus) FollowPose(GridClient c)
    {
        var me = c.Self.SimPosition;
        var r = Vector3.UnitX * c.Self.Movement.BodyRotation;
        var fwd = new Vector3(r.X, r.Y, 0);
        if (fwd.LengthSquared() < 1e-6) fwd = Vector3.UnitX;
        fwd = Vector3.Normalize(fwd);
        var head = me + new Vector3(0, 0, 0.6f);
        // the viewer's default third-person camera: ~3 m behind, a little above the head, looking just ahead
        return (head - fwd * 3.0f + new Vector3(0, 0, 0.75f), head + fwd * 1.0f);
    }

    public void Update(GridClient c)
    {
        var (pos, focus) = Pose(c);
        var cam = c.Self.Movement.Camera;
        cam.Far = FarClip;
        if (Vector3.DistanceSquared(pos, focus) > 1e-6) cam.LookAt(pos, focus);
    }

    public void ResetFor(Simulator sim)
    {
        // a new region: a camera pose given in the old region's coordinates no longer means anything there
        _s.State.CameraOverride = null;
    }
}
