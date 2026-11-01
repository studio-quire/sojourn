using LibreMetaverse;
using Sojourn.Render.World;
using Sojourn.Session.Collision;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>Walk mode's rays while moving (ten a second at most, one batch in flight): ahead along the way the avatar
/// goes (a warning before a bump), to both sides (openings and doors passed), under the feet (the surface for
/// footsteps) and down a little ahead (a drop, water).</summary>
public sealed partial class WalkMode
{
    bool _probing;
    long _probeAt;
    sealed class Side { public bool Closed; public double ClosedFor; public long ToldAt; }
    readonly Side _left = new(), _right = new();
    readonly Dictionary<uint, long> _doorsTold = new();
    string? _warnedKey;
    long _warnedAt, _clearSince, _edgeAt, _waterAt;
    float _lastSpeedTold;
    long _speedToldAt;

    /// <summary>The way the avatar is going: its velocity's, else the held keys' (relative to the facing), else ahead.</summary>
    float MoveYaw(Body b)
    {
        if (Speed(b) > 0.5f) return MathF.Atan2(b.Vel.Y, b.Vel.X);
        float yaw = b.Sitting ? b.Yaw : _yaw;
        int fwd = Get(K.Fwd) - Get(K.Back), side = Get(K.SideR) - Get(K.SideL);
        if (fwd == 0 && side == 0) return yaw;
        return yaw + MathF.Atan2(-side, fwd);
    }

    void Probe(Body b, long now)
    {
        if (!_rays.Ready || _probing) return;
        bool moving = Speed(b) > 0.3f || HeldMove;
        if (now - _probeAt < (moving ? 100 : 1000)) return;
        if (!moving && !On && !b.Sitting) { _probeAt = now; return; }
        _probeAt = now;
        float knee = b.Sitting ? b.Pos.Z - 0.2f : b.Feet + 0.5f, chest = b.Sitting ? b.Pos.Z + 0.6f : b.Feet + 1.3f;
        float speed = Speed(b), moveYaw = MoveYaw(b);
        float look = b.Sitting ? Math.Clamp(speed * 1.5f + 2f, 3f, 30f) : Math.Clamp(speed * 1.0f + 1.2f, 1.8f, 6f);
        Vector3 At(float z) => new(b.Pos.X, b.Pos.Y, z);
        var rays = new List<(Vector3, Vector3, float)>
        {
            (At(knee), Dir(moveYaw), look), (At(chest), Dir(moveYaw), look),
            (At(knee), Dir(b.Yaw + MathF.PI / 2), 6), (At(chest), Dir(b.Yaw + MathF.PI / 2), 6),
            (At(knee), Dir(b.Yaw - MathF.PI / 2), 6), (At(chest), Dir(b.Yaw - MathF.PI / 2), 6),
            (At(chest), -Vector3.UnitZ, 2.6f),
            (At(chest) + Dir(moveYaw) * 1.5f, -Vector3.UnitZ, 15f),
        };
        _probing = true;
        _ = Run();

        async Task Run()
        {
            long asked = Environment.TickCount64;
            try
            {
                var got = await _rays.CollideManyAsync(rays, b.Linkset);
                if (WalkLog != null && Environment.TickCount64 - asked > 300) Log($"probe late: {Environment.TickCount64 - asked} ms ({(got == null ? "no answer" : "answered")})");
                if (got is { } batch && batch.Region == b.Region) Read(b, batch.Hits, chest, look, moveYaw, Environment.TickCount64);
            }
            catch (Exception) { /* a ray lost (a region left meanwhile): the next probe asks again */ }
            finally { _probing = false; }
        }
    }

    static CollideHit? Nearer(CollideHit? a, CollideHit? b) => a is not { } x ? b : b is not { } y ? a : x.Distance <= y.Distance ? a : b;

    void Read(Body b, CollideHit?[] h, float chest, float look, float moveYaw, long now)
    {
        float speed = Speed(b);
        if (WalkLog != null)
            Log(FormattableString.Invariant($"probe pos {b.Pos.X:F2},{b.Pos.Y:F2},{b.Pos.Z:F2} vel {b.Vel.X:F2},{b.Vel.Y:F2} reported {S.Client?.Self.Velocity} held {HeldMove} rays ")
                + string.Join(" ", h.Select(x => x is { } y ? y.Distance.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "-")));
        bool moving = speed > 0.3f || HeldMove;
        var w = S.Region;
        var sim = S.Client?.Network.CurrentSim;

        // the surface underfoot: the higher of a prim's top and the ground
        if (!b.Sitting)
        {
            float terrain = w?.TerrainHeight(b.Pos.X, b.Pos.Y) ?? float.NegativeInfinity;
            float primTop = h[6] is { } under ? chest - under.Distance : float.NegativeInfinity;
            float floor = Math.Max(terrain, primTop);
            if (b.Feet - floor < 0.6f)
            {
                _surface = sim != null && sim.WaterHeight > floor + 0.15f ? "water"
                    : primTop >= terrain - 0.05f && h[6] is { } u && w != null && w.Prims.TryGetValue(u.LocalId, out var p) ? Material(p) : "ground";
                _surfaceAt = now;
            }
            // a drop or water a step and a half ahead
            if (moving && !b.Flying && On)
            {
                var ahead = new Vector3(b.Pos.X, b.Pos.Y, 0) + Dir(moveYaw) * 1.5f;
                float aheadTerrain = w?.TerrainHeight(ahead.X, ahead.Y) ?? float.NegativeInfinity;
                float aheadPrim = h[7] is { } f ? chest - f.Distance : float.NegativeInfinity;
                float aheadFloor = Math.Max(aheadTerrain, aheadPrim);
                float drop = floor - aheadFloor;
                if (float.IsFinite(aheadFloor) && drop > 1.5f && now - _edgeAt > 4000)
                {
                    _edgeAt = now;
                    Cue("edges", CueSounds.Cue.Edge, $"Drop ahead, {Bearings.Metres(drop)} down.", b, moveYaw);
                }
                else if (sim != null && sim.WaterHeight > aheadFloor + 0.3f && sim.WaterHeight <= floor + 0.1f && now - _waterAt > 6000)
                {
                    _waterAt = now;
                    Cue("edges", CueSounds.Cue.Water, "Water ahead.", b, moveYaw);
                }
            }
        }

        // a warning before a bump (walking or driving)
        string cue = b.Sitting ? "vehicle" : "walls";
        if (moving && (On || b.Sitting) && Nearer(h[0], h[1]) is { } hit && hit.Distance <= look)
        {
            var key = (hit.Avatar ? "a" : "p") + RootId(hit);
            if (key != _warnedKey || now - _warnedAt > 4000)
            {
                _warnedKey = key;
                _warnedAt = now;
                var name = Obstacle(hit);
                bool door = IsDoor(name);
                Cue(cue, door ? CueSounds.Cue.Door : CueSounds.Cue.Wall, $"{name}, {Bearings.Metres(hit.Distance)}.", b, moveYaw);
            }
            _clearSince = 0;
        }
        else if (_warnedKey != null)
        {
            // clear for a while: the same thing warns again when it's next ahead
            if (_clearSince == 0) _clearSince = now;
            else if (now - _clearSince > 1500) _warnedKey = null;
        }

        // openings and doors passed, on foot in walk mode
        if (On && !b.Sitting && speed > 0.5f)
        {
            double travelled = speed * 0.1;
            Pass(b, _left, Nearer(h[2], h[3]), +1, travelled, now);
            Pass(b, _right, Nearer(h[4], h[5]), -1, travelled, now);
        }
        else if (!moving) { _left.Closed = _right.Closed = false; }

        // driving: the speed said once it settles at a new one. A vehicle's reported velocity swings from update to update
        // (a train read 19, 6, 13, 8 metres a second three seconds apart), so it's the distance travelled over 2 s, said
        // after holding within 15% for 2 s, and only when it differs from the last said by 3 m/s and a third.
        if (b.Sitting && TrackedSpeed(b, now) is float s)
        {
            if (Math.Abs(s - _settling) > Math.Max(1f, _settling * 0.15f)) { _settling = s; _settlingSince = now; }
            else if (now - _settlingSince > 2000 && now - _speedToldAt > 4000
                && (s < 0.3f ? _lastSpeedTold >= 1f : Math.Abs(s - _lastSpeedTold) > Math.Max(3f, _lastSpeedTold * 0.35f)))
            {
                _lastSpeedTold = s;
                _speedToldAt = now;
                if (_set.WalkSpeed) Line(s < 0.3f ? "Stopped." : $"{SpeedWords(s)}.");
            }
        }
        else if (!b.Sitting && _track.Count > 0) { _track.Clear(); _lastSpeedTold = _settling = 0; }
    }

    readonly Queue<(long T, Vector3 P)> _track = new();
    float _settling;
    long _settlingSince;

    /// <summary>Ground speed over the last 2 s from global positions (a region crossing doesn't jump), once there's 1 s of track.</summary>
    float? TrackedSpeed(Body b, long now)
    {
        var g = new Vector3((b.Region >> 32) + b.Pos.X, (b.Region & 0xFFFFFFFF) + b.Pos.Y, 0);
        _track.Enqueue((now, g));
        while (now - _track.Peek().T > 2000) _track.Dequeue();
        var (t0, p0) = _track.Peek();
        if (now - t0 < 1000) return null;
        return Vector3.Distance(g, p0) * 1000f / (now - t0);
    }

    void Pass(Body b, Side s, CollideHit? hit, int side, double travelled, long now)
    {
        double d = hit?.Distance ?? double.PositiveInfinity;
        string where = side > 0 ? "left" : "right";
        float yaw = b.Yaw + side * MathF.PI / 2;
        if (hit is { Avatar: false } dh && dh.Distance < 3)
        {
            var name = Obstacle(dh);
            uint root = RootId(dh);
            if (IsDoor(name) && (!_doorsTold.TryGetValue(root, out var at) || now - at > 30_000))
            {
                _doorsTold[root] = now;
                Cue("openings", CueSounds.Cue.Door, $"{name} on the {where}.", b, yaw);
            }
        }
        if (d < 3.0)
        {
            if (!s.Closed) { s.Closed = true; s.ClosedFor = 0; }
            else s.ClosedFor += travelled;
        }
        else if (d > 4.5)
        {
            if (s.Closed && s.ClosedFor >= 1.0 && now - s.ToldAt > 1500)
            {
                s.ToldAt = now;
                Cue("openings", CueSounds.Cue.Opening, $"Opening on the {where}.", b, yaw);
            }
            s.Closed = false;
        }
    }

    uint RootId(CollideHit h)
    {
        if (h.Avatar || S.Region is not { } w || !w.Prims.TryGetValue(h.LocalId, out var p)) return h.LocalId;
        return S.Objects.RootOf(w, p).LocalID;
    }

    static readonly HashSet<string> Unnamed = new(StringComparer.OrdinalIgnoreCase) { "", "Object", "Primitive", "Prim", "Mesh" };

    /// <summary>What a ray met, in a word or a name: the person's name, the part's or the object's own name where it has
    /// one, else "Wall" for something upright and "Something" otherwise.</summary>
    string Obstacle(CollideHit h)
    {
        var w = S.Region;
        if (h.Avatar) return w != null && w.Avatars.TryGetValue(h.LocalId, out var av) && av.Name is { Length: > 0 } n ? n : "Someone";
        if (w != null && w.Prims.TryGetValue(h.LocalId, out var p))
        {
            if (p.Properties?.Name is { } own && !Unnamed.Contains(own.Trim())) return own.Trim();
            var root = S.Objects.RootOf(w, p);
            if (root.Properties?.Name is { } rn && !Unnamed.Contains(rn.Trim())) return rn.Trim();
            RequestNames(w, p, root);
        }
        return MathF.Abs(h.Normal.Z) < 0.5f ? "Wall" : "Something";
    }

    readonly HashSet<uint> _askedNames = new();

    /// <summary>Names aren't sent with objects: asked once for what the rays meet (the part and its object), so the
    /// next warning can use them.</summary>
    void RequestNames(Sojourn.Core.World.RegionWorld w, Primitive part, Primitive root)
    {
        if (!_askedNames.Add(part.LocalID)) return;
        var both = part.LocalID == root.LocalID ? new[] { root } : new[] { part, root };
        _ = S.Objects.FetchAsync(w, both, TimeSpan.FromSeconds(3), CancellationToken.None);
    }

    static bool IsDoor(string name) => name.Contains("door", StringComparison.OrdinalIgnoreCase) || name.Contains("gate", StringComparison.OrdinalIgnoreCase);

    static string SpeedWords(float speed) => $"{(speed < 1 ? Bearings.MetresTenths(speed) : Bearings.Metres(speed))} a second";
}
