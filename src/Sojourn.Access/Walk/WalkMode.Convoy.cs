using LibreMetaverse;
using Vector2 = System.Numerics.Vector2;

namespace Sojourn.Access.Walk;

/// <summary>
/// Driving after someone (docs/access-app.md > Walk mode > Driving after someone): //follow while at the wheel of a
/// vehicle steered by its keys holds those keys for the person, along the way the one ahead went, staying about
/// ConvoyGap behind, or as far as //follow NAME METRES says. Any arrow or Space takes it back. Not for what runs on rails or a throttle: the keys there don't
/// steer, and Up held opens a throttle all the way. Written by Claude Opus 5.5, 2026-11-04, after the blind tester asked
/// for vehicles following each other, a beacon over two cars' engines not having been enough.
/// </summary>
public sealed partial class WalkMode
{
    const float ConvoyGap = 10f, ConvoyCrumb = 2.5f, ConvoyFar = 60f, ConvoyJump = 100f, ConvoyDeadband = 0.12f, ConvoyBraking = 4f;
    const long ConvoyEvery = 100, ConvoySteerCycle = 300, ConvoyLostAfter = 6000, ConvoyStuckAfter = 6000;

    sealed class Convoy(string name, UUID id, float gap)
    {
        public readonly string Name = name;
        public readonly UUID Id = id;
        public readonly float Gap = gap;
        public readonly Queue<Vector2> Way = new();   // where they went, in the grid's metres, so a crossing moves nothing
        public Vector2? Last, PaceFrom;
        public float Pace;                            // how fast they're going, metres a second
        public long DecideAt, LostSince, StuckSince, LogAt, SteeredAt, PaceAt;
        public float YawOffset;                       // the way it travels against the way the seat faces, learned on straights
        public bool Fwd, Back, Left, Right, ToldWith, ToldFar;
    }

    Convoy? _convoy;

    public string? DrivingAfter => _convoy?.Name;

    bool ConvoyDown(K k) => _convoy is { } c && k switch
    {
        K.Fwd => c.Fwd,
        K.Back => c.Back,
        K.TurnL => c.Left,
        K.TurnR => c.Right,
        _ => false,
    };

    /// <summary>//follow while seated. Where the keys can't do it, a beacon on the person instead, saying why.</summary>
    public void DriveAfter(string name, UUID id, float? gap = null)
    {
        if (Me() is not { Sitting: true } b) { Line("You're not seated."); return; }
        string seat = b.SeatName ?? "your seat";
        string? why = (S.ControlsTaken & MoveKeys) == 0 ? $"{seat} hasn't taken your keys, so you're riding it, not driving it"
            : _railTrack != null ? $"{seat} runs on rails"
            : Throttle != null ? $"{seat} runs on a throttle"
            : null;
        if (why != null)
        {
            Line($"I can't drive after {name}: {why}. Here's a beacon on them instead.");
            BeaconOn(name, id);
            return;
        }
        _convoy = new Convoy(name, id, gap ?? ConvoyGap);
        Log($"convoy: driving {seat} after {name}");
        Line($"Driving {seat} after {name}, about {Bearings.Metres(_convoy.Gap)} behind. Any arrow key or Space takes it back.");
    }

    /// <summary>Lets go of the keys (the next Drive clears them), saying why if there's a why.</summary>
    public void EndConvoy(string? line)
    {
        if (_convoy is not { } c) return;
        _convoy = null;
        Log($"convoy: ended driving after {c.Name}: {line ?? "quietly"}");
        if (line != null) Line(line);
    }

    static float Wrap(float a) => MathF.Atan2(MathF.Sin(a), MathF.Cos(a));

    void ConvoyTick(Body b, long now)
    {
        if (_convoy is not { } c || now < c.DecideAt) return;
        c.DecideAt = now + ConvoyEvery;
        if (!b.Sitting && !SeatInDoubt(b)) { EndConvoy($"You're off your seat, so no longer driving after {c.Name}."); return; }
        if ((S.ControlsTaken & MoveKeys) == 0 && !SeatInDoubt(b)) { EndConvoy($"The vehicle let go of your keys, so no longer driving after {c.Name}."); return; }

        Utils.LongToUInts(b.Region, out var gx, out var gy);
        if (PosOf(c.Id) is not { } p)
        {
            c.Fwd = c.Back = c.Left = c.Right = false;
            if (c.LostSince == 0) c.LostSince = now;
            else if (now - c.LostSince > ConvoyLostAfter) { EndConvoy($"Lost {c.Name}, so I've let go of the keys."); RequestStop(); }
            return;
        }
        c.LostSince = 0;
        var them = new Vector2(gx + p.X, gy + p.Y);
        var me = Global(b);
        if (c.Last is { } last && Vector2.Distance(last, them) > ConvoyJump)
        {
            EndConvoy($"{c.Name} jumped away, a teleport most likely, so I've let go of the keys.");
            RequestStop();
            return;
        }
        if (c.Last is not { } was || Vector2.Distance(was, them) >= ConvoyCrumb) { c.Way.Enqueue(them); c.Last = them; }

        float speed = Speed(b), reach = MathF.Max(5f, speed);
        while (c.Way.Count > 1 && Vector2.Distance(c.Way.Peek(), me) < reach) c.Way.Dequeue();
        var aim = c.Way.Count > 0 ? c.Way.Peek() : them;
        float dist = Vector2.Distance(them, me);

        // steered by the way it faces, not the way it's going: travel lags the wheel, and steering by it overshoots into a spin
        if (speed > 2f && now - c.SteeredAt > 500)
            c.YawOffset += 0.3f * Wrap(MathF.Atan2(b.Vel.Y, b.Vel.X) - b.Yaw - c.YawOffset);
        float err = Wrap(MathF.Atan2(aim.Y - me.Y, aim.X - me.X) - b.Yaw - c.YawOffset);   // positive is to the left
        // their pace over half a second or more (positions come in steps), and the room it takes to come down to it
        if (c.PaceFrom is not { } from) { c.PaceFrom = them; c.PaceAt = now; }
        else if (now - c.PaceAt >= 500)
        {
            c.Pace = Vector2.Distance(from, them) * 1000f / (now - c.PaceAt);
            c.PaceFrom = them;
            c.PaceAt = now;
        }
        float closing = MathF.Max(0f, speed - c.Pace), room = closing * closing / (2 * ConvoyBraking) + closing * 0.3f;
        bool close = dist < c.Gap;
        c.Fwd = dist > c.Gap + room && !(MathF.Abs(err) > 1f && speed > 6f);
        c.Back = closing > 1f && dist < c.Gap + room * 0.6f;
        bool steering = c.Fwd || speed > 0.5f;
        // the keys held for a share of each cycle, by how far off it is: a held key turns as far as the vehicle's script likes
        bool turning = steering && MathF.Abs(err) > ConvoyDeadband
            && now % ConvoySteerCycle < ConvoySteerCycle * Math.Clamp(MathF.Abs(err) / 0.5f, 0.3f, 1f);
        c.Left = turning && err > 0;
        c.Right = turning && err < 0;
        if (c.Left || c.Right) c.SteeredAt = now;

        if (c.Fwd && speed < 0.3f)
        {
            if (c.StuckSince == 0) c.StuckSince = now;
            else if (now - c.StuckSince > ConvoyStuckAfter) { EndConvoy($"{b.SeatName ?? "The vehicle"} isn't moving, so I've let go of the keys."); return; }
        }
        else c.StuckSince = 0;

        if (_set.WalkText("follow"))
        {
            if (close && speed < 0.5f && !c.ToldWith) { c.ToldWith = true; Line($"With {c.Name}, {Bearings.Metres(dist)} away."); }
            if (dist > c.Gap * 2) c.ToldWith = false;
            if (dist > ConvoyFar && !c.ToldFar) { c.ToldFar = true; Line($"{c.Name} is getting away, {Bearings.Metres(dist)} off."); }
            if (dist < ConvoyFar / 2) c.ToldFar = false;
        }
        if (now - c.LogAt > 2000)
        {
            c.LogAt = now;
            Log($"convoy: {dist:0.0} m from {c.Name} at {speed:0.0} m/s, aim {err * 180 / MathF.PI:0} degrees off, {c.Way.Count} points ahead,"
                + $" keys {(c.Fwd ? "up " : "")}{(c.Back ? "down " : "")}{(c.Left ? "left " : "")}{(c.Right ? "right" : "")}");
        }
    }
}
