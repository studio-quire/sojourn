using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;
using Vector2 = System.Numerics.Vector2;

namespace Sojourn.Access.Walk;

/// <summary>
/// Stopping a vehicle by its controls (docs/access-app.md > Walk mode > Stopping a vehicle): Space in walk mode or
/// //stop while seated on something moving, and by itself when the vehicle says its track has ended ("sensor : No
/// further track guide detected", "no_sensor : No Guide found": a blind tester's freight train ran off the end of its
/// track and derailed or stranded her, 2026-11-01). It does what she would, through the same flags the arrows send: the
/// back control pressed, for as long as is safe; if that didn't slow the vehicle (a throttle train, whose Down press
/// takes its throttle a step down), Down tapped instead. A vehicle that says its throttle (WalkMode.Throttle.cs) is
/// tapped by what it says, until it reads 0 and never below; one that doesn't, by the count of the person's Up taps,
/// half at once and the rest one at a time, each once the last one's effect shows, on past the count while each slows
/// it. It must never drive the vehicle backwards, and a car's back control is often reverse thrust that outruns the
/// region's news of it, so a press is timed rather than held until nearly still (below). The flags are held apart from
/// the arrows' (leaving walk mode, another window or a notice doesn't let go of them); an arrow pressed meanwhile hands
/// the controls back. Written by Claude Opus 5.5, 2026-11-01; presses timed, and taps by the throttle said and by
/// their effect, 2026-11-02.
/// </summary>
public sealed partial class WalkMode
{
    /// <summary>The phrases that mean the track runs out (as well as the person's own, walk_track_end_phrases), matched
    /// within an object's line, ignoring case, underscores and spacing.</summary>
    public static readonly string[] TrackEndPhrases = { "no further track", "no guide found", "track guide" };

    const long StopHoldJudge = 1200;     // the first press, then this long: slowed by under 15%, it's a throttle: taps
    const long StopTapDown = 150, StopTapUp = 150;
    const long StopLimit = 10_000, StopLimitMost = 25_000;   // 10 s, longer while taps are still taking effect
    const float StillSpeed = 0.5f;       // moving, for Space to stop it; a press lets go under it
    const float NearStill = 0.2f;        // "Stopped." only under it: a train at its lowest throttle creeps at 0.5 m/s
    const int MostTaps = 20;             // 100% at 5% a tap, the blind tester's train
    const int MostExtra = 20;            // taps past the count, while each slows it

    // The visitor's kart, held in reverse thrust, lost 9.7 m/s in about half a second and went on backing; the region's
    // news comes every 60-125 ms and shows a press only ~150 ms after it, and a let-go takes as long again to act, so a
    // hold let go when the news said nearly still backed it in most runs, by up to 1.7 m (2026-11-02). So: the first
    // press lasts, without news of its effect, only as long as a car this strong would take to lose the speed; news of
    // strong slowing ends it while the speed it will have when the let-go acts keeps a margin; news of gentle slowing
    // (a gentle brake) holds it on, a lease at a time. A car's later presses are pulses as long as the slowing seen so
    // far says leaves the margin, or none: it coasts. A thread of its own lets go when a press's time is up, whatever
    // the window's thread is doing.
    const float BlindDecel = 30f;        // m/s² assumed of a press until news shows otherwise
    const long MinBlind = 150, MaxBlind = 400;
    const long React = 200;              // ms from a velocity seen to a let-go acting on the vehicle
    const float Margin = 0.8f;           // m/s to spare when the let-go acts: the rest it coasts
    const long Lease = 150;              // ms a press held on by news lasts unless the next tick renews it
    const long Fresh = 300;              // ms: older news holds nothing on
    const long MinPulse = 100, MaxPulse = 1000, Observe = 800;

    sealed class VehicleStop
    {
        public long Start, PhaseAt, NextAt, ReleasedAt, PressAt, PressLen, PressUntil, LapsedAt, CoastAt, Deadline;
        public float StartSpeed, LastSpeed, PressV, Decel, CoastSpeed, StartAlong;
        public Vector2 Dir;
        public volatile bool Down;
        public bool Taps, TapDown, Released, Car, Judged, Measured, HeldUp, UpTap;
        public int TapsLeft, Edges;
        // tapping by what the vehicle says of its throttle: the taps sent, the last one's reading and whether it shows
        public bool Reading, LastTapShown;
        public int TapCount, Unshown, Corrections;
        public long LastTapAt, CorrectedAt, ReadAt;
        public float StartReading, TapReading, LastReading;
        public readonly List<long> TapTimes = new();
        // tapping one at a time by the effect (no throttle said): taps left by the count, then past it, and the last
        // one's speed when it went
        public bool Checking, CheckBegun;
        public int CheckLeft, Extra, NoEffect, CheckN;
        public long CheckAt;
        public float CheckV, Drop1, Drop2;
        public readonly List<(long T, float Along)> Samples = new();
        // the region's last news of the seat (seen when its velocity or position changed), and the one before
        public long SampleAt, PrevAt;
        public float SampleAlong, PrevAlong;
        public System.Numerics.Vector3 SampleVel, SamplePos;
        public string Why = "";
    }

    VehicleStop? _vstop;
    readonly object _controlsLock = new();
    volatile string? _stopAsked;
    int _throttleTaps;
    bool _wasSitting;
    long _trackEndAt;

    /// <summary>A stop under way ("hold", "pulses", "taps", "checking" one at a time, "reading" the throttle it says,
    /// "settling") or null (the status op).</summary>
    public string? Stopping => _vstop is not { } v ? null : v.Reading ? "reading" : v.Released ? "settling" : v.Checking ? "checking"
        : v.Taps ? "taps" : v.Car ? "pulses" : "hold";
    /// <summary>Up taps less Down taps while seated on this seat, 0-20: how far a throttle has been opened by the arrows.</summary>
    public int ThrottleTaps => _throttleTaps;

    /// <summary>//stop: the same as Space in walk mode, from any thread (it happens on the next tick).</summary>
    public void RequestStop() => _stopAsked = "//stop";

    /// <summary>Space in walk mode and //stop: seated on something moving, it's stopped by its controls; otherwise (or
    /// as well) everything the app was walking stops.</summary>
    void StopKey(Body? b)
    {
        if (_convoy is { } convoy) EndConvoy($"The wheel's yours: no longer driving after {convoy.Name}.");
        if (b is { Sitting: true } seated && Moving(seated))
        {
            bool heldUp = DrivenByHolding();
            StopOthers?.Invoke();
            ReleaseAll();
            if (_vstop != null) { Line("Still stopping."); return; }
            StartVehicleStop(seated, "manual", heldUp);
            Line("Stopping.");
            return;
        }
        StopAll();
    }

    // moving enough to stop by its controls: a train at its lowest throttle creeps at 0.5 m/s
    bool Moving(Body b) => Speed(b) > 0.3f || (SeatSpeed(2000) ?? 0) > 0.3f;

    void StartVehicleStop(Body b, string why, bool heldUp)
    {
        long now = Environment.TickCount64;
        var dir = SeatMove(1000) is { } m && m.Length() > 0.3f ? Vector2.Normalize(m) : new Vector2(b.Vel.X, b.Vel.Y) is { } v && v.Length() > 0.1f ? Vector2.Normalize(v) : Vector2.Zero;
        var s = new VehicleStop
        {
            Start = now, PhaseAt = now, Why = why, Dir = dir, HeldUp = heldUp, Deadline = now + StopLimit,
            // from positions, not the region's word on the velocity (a train's swung 19, 6, 13, 8 m/s, 2026-11-01)
            StartSpeed = SeatSpeed(2000) ?? Speed(b),
            SampleAt = now, PrevAt = now, SampleVel = b.Vel, SamplePos = b.Pos,
        };
        s.LastSpeed = s.StartSpeed;
        s.SampleAlong = s.PrevAlong = s.StartAlong = Along(b, s);
        s.Samples.Add((now, s.SampleAlong));
        _vstop = s;
        Log($"stop {why}: speed {s.StartSpeed:F2}, throttle taps {_throttleTaps}{(heldUp ? ", Up held" : "")}"
            + (_thr is { } r ? $", it says its throttle is {r.Value:0.##} ({(r.Line ? "a line" : "its text")}), a step {ThrottleStep?.ToString("0.##") ?? "unknown"}" : ""));
        // a vehicle that says its throttle is tapped by what it says (not a car driven by holding Up: its throttle
        // falls back by itself as Up's let go); the first tap on the next tick
        if (_thr is { } said && !heldUp) (s.Reading, s.Taps, s.StartReading, s.LastReading) = (true, true, said.Value, said.Value);
        else Press(s, now, 0, s.SampleAlong);
        new Thread(() => Watch(s)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "vehicle stop" }.Start();
    }

    /// <summary>The region's word on the velocity along the way it was going (positions lag: they're judged over a
    /// stretch).</summary>
    static float Along(Body b, VehicleStop s)
    {
        var vel = new Vector2(b.Vel.X, b.Vel.Y);
        return s.Dir != Vector2.Zero ? Vector2.Dot(vel, s.Dir) : vel.Length();
    }

    /// <summary>Each tick, before Drive: the seat's track, the track-end lines heard, a stop asked for, and a stop's
    /// next press or release.</summary>
    void StopTick(Body b, long now)
    {
        b = TestLagged(b, now);
        if (b.Sitting != _wasSitting) { _throttleTaps = 0; _fwdHeldFor = 0; _seatTrack.Clear(); ThrottleReset(); _wasSitting = b.Sitting; }
        if (b.Sitting)
        {
            var g = new Vector2((b.Region >> 32) + b.Pos.X, (b.Region & 0xFFFFFFFF) + b.Pos.Y);
            if (_seatTrack.Count == 0 || _seatTrack.Last().P != g || now - _seatTrack.Last().T > 250) _seatTrack.Add((now, g));
            while (_seatTrack.Count > 0 && now - _seatTrack[0].T > 3000) _seatTrack.RemoveAt(0);
        }
        ThrottleTick(b, now);
        while (_heardLines.TryDequeue(out var h)) TrackEndHeard(b, h, now);
        if (Interlocked.Exchange(ref _stopAsked, null) != null) StopKey(b);
        if (_vstop is { } s)
        {
            if (TestStall > 0 && _testRng.Next(3) == 0) Thread.Sleep(_testRng.Next(TestStall));
            StopStep(b, s, now);
        }
    }

    // test switches (tools/access.py's flake checks): SOJOURN_TEST_STOP_LAG=ms, the stop sees the seat's position and
    // velocity that much late, as from a busy region; SOJOURN_TEST_STOP_STALL=ms, while stopping, a tick in three holds
    // the window's thread up to that long, as on a busy PC
    static readonly int TestLag = int.TryParse(Environment.GetEnvironmentVariable("SOJOURN_TEST_STOP_LAG"), out var lag) ? lag : 0;
    static readonly int TestStall = int.TryParse(Environment.GetEnvironmentVariable("SOJOURN_TEST_STOP_STALL"), out var stall) ? stall : 0;
    readonly Random _testRng = new();
    readonly Queue<(long T, System.Numerics.Vector3 Pos, System.Numerics.Vector3 Vel)> _testSeen = new();
    (System.Numerics.Vector3 Pos, System.Numerics.Vector3 Vel)? _testShown;

    Body TestLagged(Body b, long now)
    {
        if (TestLag <= 0) return b;
        _testSeen.Enqueue((now, b.Pos, b.Vel));
        while (_testSeen.Count > 0 && now - _testSeen.Peek().T >= TestLag) { var x = _testSeen.Dequeue(); _testShown = (x.Pos, x.Vel); }
        return _testShown is { } v ? b with { Pos = v.Pos, Vel = v.Vel } : b;
    }

    void StopStep(Body b, VehicleStop s, long now)
    {
        if (!b.Sitting) { EndStop(s, "You're no longer seated."); return; }
        float reported = Speed(b), shortSpeed = SeatSpeed(700) ?? reported;
        float along = Along(b, s);
        bool fresh = b.Vel != s.SampleVel || b.Pos != s.SamplePos;
        if (fresh)
        {
            (s.PrevAt, s.PrevAlong) = (s.SampleAt, s.SampleAlong);
            (s.SampleAt, s.SampleAlong, s.SampleVel, s.SamplePos) = (now, along, b.Vel, b.Pos);
            s.Samples.Add((now, along));
            while (s.Samples.Count > 2 && now - s.Samples[1].T > 3000) s.Samples.RemoveAt(0);
            Log($"stop +{now - s.Start} along {along:F2} short {shortSpeed:F2} {(s.Released ? "released" : s.Down ? "down" : "up")}");
        }
        // moving back the way it came: let go at once (a throttle taken past zero into reverse, or Back as reverse);
        // tapping by the throttle it says, a throttle below 0 is put back first
        bool putBack = s.Reading && (_thr is { Value: < 0 } || now - s.CorrectedAt < 3000);
        if (s.Dir != Vector2.Zero && now - s.Start > 300 && -along > 0.3f && !putBack)
        {
            if (s.Down) lock (_controlsLock) s.Down = false;
            EndStop(s, s.Released || s.Taps ? $"Let go, but it's moving backward, at {SpeedWords(-along)}."
                : $"Let go: it began moving backward, at {SpeedWords(-along)}.");
            return;
        }
        if (now > s.Deadline)
        {
            if (s.Down) lock (_controlsLock) s.Down = false;
            float left = SeatSpeed(2000) ?? reported;
            EndStop(s, IsNearStill(along, reported) ? "Stopped."
                : s.Reading && _thr is { Value: <= 0 } ? $"Couldn't stop it: its throttle is at 0, but it's still moving at {SpeedWords(left)}."
                : $"Couldn't stop it: it's still moving at {SpeedWords(left)}.");
            return;
        }
        if (!s.Down && !s.Released && !s.Taps)
        {
            // its time ran out: the stop's own thread, which keeps the time whatever the window's thread is doing, let go
            s.Released = true;
            s.ReleasedAt = Volatile.Read(ref s.LapsedAt);
            Log($"stop +{now - s.Start}: let go at its time after {s.ReleasedAt - s.PressAt} ms");
        }
        if (s.Reading) ReadStep(s, now, along, reported);
        else if (s.Released) Settle(s, now, along, shortSpeed, reported);
        else if (s.Taps) TapStep(s, now, along, reported);
        else PressStep(s, now, along, reported, fresh);
    }

    /// <summary>Nearly still enough to say "Stopped.": under 0.2 m/s over the last 2 s by where the seat went, and by the
    /// region's velocity now (a train at its lowest throttle creeps on at 0.5 m/s).</summary>
    bool IsNearStill(float along, float reported) => (SeatSpeed(2000) ?? reported) < NearStill && Math.Abs(along) < 0.3f && reported < 0.3f;

    /// <summary>A press under way: the first, or a car's pulse.</summary>
    void PressStep(VehicleStop s, long now, float along, float reported, bool fresh)
    {
        long held = now - s.PressAt;
        // how hard it's slowing, from the news since the press began: overall, and between the last two samples
        if (fresh && s.SampleAt > s.PressAt)
        {
            float overall = (s.PressV - along) / Math.Max(0.05f, held / 1000f);
            float recent = s.PrevAt > s.PressAt && s.SampleAt - s.PrevAt >= 30 ? (s.PrevAlong - along) * 1000f / (s.SampleAt - s.PrevAt) : 0;
            s.Decel = Math.Max(s.Decel, Math.Max(overall, recent));
        }
        float predicted = s.SampleAlong - s.Decel * (React + now - s.SampleAt) / 1000f;
        bool still = along < StillSpeed && reported < 1f;
        long blind = BlindLen(s.PressV);
        // news that the press is slowing it gently: recent, lower than when it began by more than a velocity's wobble, and
        // not hard (hard slowing is never held past the blind length: with news 150 ms late, a press so held backed a hair)
        bool news = now - s.SampleAt <= Fresh && s.SampleAt - s.PressAt >= 100 && s.PressV - s.SampleAlong >= Math.Max(0.3f, 0.03f * s.PressV)
            && s.Decel < BlindDecel / 3;
        string? why = still ? "nearly still"
            : s.PressLen > 0 ? (held >= s.PressLen ? "the pulse done" : null)
            : predicted < Margin ? $"{predicted:F1} m/s as it acts"
            : held >= blind && !news ? "no news of slowing" : null;
        if (why != null)
        {
            if (s.PressLen == 0 && !still && s.PressV - along >= 0.15f * s.PressV) s.Car = true;   // a brake, or reverse thrust
            LetGo(s, now, why);
            return;
        }
        if (s.PressLen == 0) Volatile.Write(ref s.PressUntil, news ? Math.Max(s.PressAt + blind, now + Lease) : s.PressAt + blind);
        if (!s.Car && !s.Judged && now - s.PhaseAt >= StopHoldJudge) Judge(s, now, reported);
    }

    static long BlindLen(float v) => Math.Clamp((long)(v / BlindDecel * 1000), MinBlind, MaxBlind);

    /// <summary>Let go: settling, coasting, or between a car's pulses.</summary>
    void Settle(VehicleStop s, long now, float along, float shortSpeed, float reported)
    {
        // said stopped once the longer measure agrees
        if (IsNearStill(along, reported)) { EndStop(s, "Stopped."); return; }
        if (!s.Car)
        {
            if (!s.Judged && now - s.PhaseAt >= StopHoldJudge) Judge(s, now, reported);
            return;
        }
        // a car: once the last press's effect is in the news, its slowing per second pressed (on the high side: it
        // coasted meanwhile too), and another pulse as long as that leaves the margin, or none (it coasts)
        if (now - s.ReleasedAt < Observe || s.SampleAt - s.ReleasedAt < 300 || now - s.SampleAt > Fresh) return;
        float v = Math.Min(along, shortSpeed);
        if (!s.Measured)
        {
            s.Measured = true;
            float pressed = Math.Max(MinPulse, s.ReleasedAt - s.PressAt) / 1000f;
            s.Decel = Math.Max(s.Decel, (s.PressV - v) / pressed);
            Log($"stop +{now - s.Start}: {s.PressV:F2} to {v:F2} m/s for {pressed:F2} s pressed: slowing {s.Decel:F1} m/s²");
        }
        long len = (long)(600 * (v - Margin) / Math.Max(s.Decel, 0.5f));
        if (len >= MinPulse) { Press(s, now, Math.Min(len, MaxPulse), v); return; }
        // no pulse is safe, so it coasts; not slowing over 1.2 s (and Up wasn't held to drive it), it's a throttle after
        // all, each press having taken it a step down (taken for a car when one press slowed it 15%, or a swinging
        // velocity looked like hard slowing): taps, by what's left of the count, then while each slows it
        if (s.CoastAt == 0) (s.CoastAt, s.CoastSpeed) = (now, SeatSpeed(StopHoldJudge) ?? reported);
        else if (now - s.CoastAt >= StopHoldJudge)
        {
            float coasted = SeatSpeed(StopHoldJudge) ?? reported;
            int left = Math.Max(0, _throttleTaps - s.Edges);
            if (coasted > s.CoastSpeed * 0.9f && !s.HeldUp)
            {
                Log($"stop: coasting doesn't slow it ({coasted:F2} after {s.CoastSpeed:F2}): a throttle, {left} taps by the count");
                BeginTaps(s, now, left);
                return;
            }
            (s.CoastAt, s.CoastSpeed) = (now, coasted);
        }
    }

    /// <summary>The first press, 1.2 s on: slowed by 15% or more (over the whole stretch: a moving seat's position can
    /// come only once a second; and by the region's velocity), a car's brake or reverse thrust (pulses); not, a throttle, which the press took a step
    /// down and no more (taps), unless the count of Up taps is spent while the person drove by holding Up (a gentle
    /// brake the region's news was too slow to show: pulses).</summary>
    void Judge(VehicleStop s, long now, float reported)
    {
        s.Judged = true;
        float held = SeatSpeed(StopHoldJudge) ?? reported;
        int left = Math.Max(0, _throttleTaps - s.Edges);   // the first press was one; none counted: unknown
        // slowed by both measures: positions alone, over a train's one step down, made 13% look like 17% (2026-11-02)
        bool slowed = held <= s.LastSpeed * 0.85f && (s.Dir == Vector2.Zero || s.SampleAlong <= s.PressV * 0.85f);
        if (slowed || (_throttleTaps > 0 && left == 0 && s.HeldUp))
        {
            s.Car = true;
            Log($"stop: a brake ({held:F2} after {s.LastSpeed:F2}): pulses");
            return;
        }
        Log($"stop: the press didn't slow it ({held:F2} after {s.LastSpeed:F2}): {left} taps by the count");
        BeginTaps(s, now, left);
    }

    /// <summary>Down tapped by the count of the person's Up taps: half at once (Up taps that never reached the script
    /// leave the count over, and the rest at once would drive it into reverse), the rest one at a time once the last
    /// one's effect shows, then on past the count while each slows it (a tap that didn't reach the script, or a
    /// throttle opened some other way, leaves the count short).</summary>
    void BeginTaps(VehicleStop s, long now, int left)
    {
        if (s.Down) lock (_controlsLock) s.Down = false;
        (s.Taps, s.Released, s.Car) = (true, false, false);
        s.TapsLeft = left / 2;
        s.CheckLeft = left - s.TapsLeft;
        s.NextAt = now + StopTapUp;
    }

    void TapStep(VehicleStop s, long now, float along, float reported)
    {
        if (TapGap(s, now)) return;
        if (s.TapsLeft > 0 && along >= 0.3f) { s.TapsLeft--; Tap(s, now, up: false, 0); return; }
        s.TapsLeft = 0;
        CheckStep(s, now, along, reported);
    }

    /// <summary>Taps one at a time by their effect: each once the speed along the way has held steady (a steady speed
    /// forward is a throttle still open, for a vehicle whose speed follows its throttle; a falling one may be coasting
    /// on with the throttle closed, and a tap then would be reverse), and only while each slows it. Once two in a row
    /// took about the same off (within a quarter), the speed left says how many steps are left: half of them go at once
    /// (by the larger of the two, so fewer), and the next look comes after them.</summary>
    void CheckStep(VehicleStop s, long now, float along, float reported)
    {
        if (!s.Checking)
        {
            s.Checking = true;
            s.Deadline = Math.Max(s.Deadline, Math.Min(s.Start + StopLimitMost, now + StopLimit));
            Log($"stop +{now - s.Start}: one at a time now, {s.CheckLeft} by the count, then up to {MostExtra} more while each slows it");
        }
        if (IsNearStill(along, reported)) { EndStop(s, "Stopped."); return; }
        if (!Steady(s, now, out float v) || v < NearStill) return;
        if (s.CheckAt > 0)
        {
            bool slowed = s.CheckV - v >= Math.Max(0.08f, 0.03f * s.CheckV);
            s.NoEffect = slowed ? 0 : s.NoEffect + 1;
            (s.Drop1, s.Drop2) = slowed ? ((s.CheckV - v) / Math.Max(1, s.CheckN), s.Drop1) : (0, 0);
            // taking effect: time for the next (each takes 1.5 s or so to show)
            if (slowed) s.Deadline = Math.Max(s.Deadline, Math.Min(s.Start + StopLimitMost, now + 4000));
            Log($"stop +{now - s.Start}: {(s.CheckN > 1 ? $"{s.CheckN} taps" : "the tap")} took it from {s.CheckV:F2} to {v:F2} m/s{(slowed ? "" : ": no effect")}");
            s.CheckAt = 0;
        }
        else if (!s.CheckBegun)
        {
            // the presses so far must have slowed it for more to follow; not, one more try (a press may have been lost)
            bool slowed = s.StartAlong - v >= Math.Max(0.08f, 0.03f * s.StartAlong);
            s.NoEffect = slowed ? 0 : 1;
            Log($"stop +{now - s.Start}: steady at {v:F2} m/s, from {s.StartAlong:F2} at the start{(slowed ? "" : ": not slowed")}");
        }
        s.CheckBegun = true;
        if (s.NoEffect >= 2) { EndStop(s, $"Couldn't stop it: Down doesn't slow it any more; it's moving at {SpeedWords(v)}."); return; }
        int n = 1;
        if (s.Drop1 > 0 && s.Drop2 > 0 && Math.Abs(s.Drop1 - s.Drop2) <= 0.25f * Math.Max(s.Drop1, s.Drop2))
            n = Math.Max(1, (int)(v / Math.Max(s.Drop1, s.Drop2) / 2));
        int fromCount = Math.Min(n, s.CheckLeft), extra = Math.Min(n - fromCount, MostExtra - s.Extra);
        n = fromCount + extra;
        if (n == 0) return;   // the deadline says the rest
        (s.CheckLeft, s.Extra) = (s.CheckLeft - fromCount, s.Extra + extra);
        (s.CheckV, s.CheckAt, s.CheckN) = (v, now, n);
        s.TapsLeft = n - 1;   // the rest of a batch, a tap every 0.3 s (TapStep)
        Tap(s, now, up: false, 0);
    }

    /// <summary>The speed along the way has held within a little over the last 0.7 s, a while after the last tap (no news
    /// meanwhile is the same speed: the region sends a moving thing's news as it changes).</summary>
    static bool Steady(VehicleStop s, long now, out float v)
    {
        v = s.SampleAlong;
        if (now - s.LastTapAt < 800 || now - s.Start < 800) return false;
        float lo = AlongAt(s, now - 700), hi = lo;
        foreach (var (t, a) in s.Samples)
            if (t > now - 700) (lo, hi) = (Math.Min(lo, a), Math.Max(hi, a));
        return hi - lo <= Math.Max(0.06f, 0.03f * Math.Abs(v));
    }

    static float AlongAt(VehicleStop s, long t)
    {
        float a = s.Samples[0].Along;
        foreach (var x in s.Samples)
        {
            if (x.T > t) break;
            a = x.Along;
        }
        return a;
    }

    /// <summary>Taps by what the vehicle says of its throttle: Down while it reads over 0, as many on the way at once as
    /// leave a step to spare should one more be on the way than it seems, the last only once the earlier ones show and a
    /// while has passed; never below 0 (put back up to 0 if it got there). Three taps in a row it doesn't show, and
    /// what it says isn't followed: one at a time by their effect instead.</summary>
    void ReadStep(VehicleStop s, long now, float along, float reported)
    {
        if (TapGap(s, now)) return;
        if (_thr is not { } r) { s.Reading = false; BeginTaps(s, now, 0); return; }
        long margin = r.Line ? 30 : 300;                                   // a press shows in a reading this long after it
        long wait = r.Line ? 1000 : Math.Max(2000, 2 * TextGap);           // ... or never
        long quiet = r.Line ? 400 : Math.Clamp(TextGap * 3 / 2, 1000, 3000);
        if (r.At != s.ReadAt)
        {
            if (r.Value < s.LastReading) s.Deadline = Math.Max(s.Deadline, Math.Min(s.Start + StopLimitMost, now + 6000));
            (s.ReadAt, s.LastReading) = (r.At, r.Value);
        }
        if (s.LastTapAt > 0 && !s.LastTapShown && (r.At - margin >= s.LastTapAt || now - s.LastTapAt >= wait))
        {
            s.LastTapShown = true;
            bool moved = s.UpTap ? r.Value > s.TapReading : r.Value < s.TapReading;
            s.Unshown = moved ? 0 : s.Unshown + 1;
            if (!moved) Log($"stop +{now - s.Start}: the tap doesn't show ({r.Value:0.##})");
        }
        if (s.Unshown >= 3)
        {
            Log($"stop +{now - s.Start}: its throttle ({r.Value:0.##}) doesn't follow the taps: one at a time by their effect");
            s.Reading = false;
            BeginTaps(s, now, 0);
            return;
        }
        int pending = s.TapTimes.Count(t => t > r.At - margin && now - t < wait);
        float? step = ThrottleStep;
        float val = r.Value;
        if (val < 0 && s.StartReading >= 0)
        {
            // below 0 by the stop's taps: back up to 0, one at a time
            if (pending == 0 && now - s.LastTapAt >= quiet && s.Corrections < 3)
            {
                s.Corrections++;
                s.CorrectedAt = now;
                Tap(s, now, up: true, val);
            }
            return;
        }
        bool closed = val <= 0 || step is { } least && val < least * 0.99f;
        if (closed)
        {
            if (pending == 0 && IsNearStill(along, reported)) EndStop(s, "Stopped.");
            return;
        }
        if (s.TapCount >= MostTaps + MostExtra) return;   // the deadline says the rest
        bool go = pending == 0
            ? step is { } st && val >= 2 * st * 0.99f || now - s.LastTapAt >= quiet
            : step is { } st2 && val - st2 * (pending + 1) >= st2 * 0.99f;
        if (go) Tap(s, now, up: false, val);
    }

    /// <summary>A tap under way: down for its time, then up for its gap. True while it lasts.</summary>
    bool TapGap(VehicleStop s, long now)
    {
        // a tap the stop's own thread let go (the window's thread was busy): its gap from then
        if (s.TapDown && !s.Down) { s.TapDown = false; s.NextAt = Math.Max(s.NextAt, Volatile.Read(ref s.LapsedAt) + StopTapUp); }
        if (now < s.NextAt) return true;
        if (!s.Down) return false;
        lock (_controlsLock) s.Down = false;
        s.TapDown = false;
        s.NextAt = now + StopTapUp;
        return true;
    }

    /// <summary>A tap of Down (or Up, putting a throttle taken below 0 back): held long enough to reach the script.</summary>
    void Tap(VehicleStop s, long now, bool up, float reading)
    {
        s.UpTap = up;
        s.Edges += up ? -1 : 1;
        s.TapCount++;
        s.TapTimes.Add(now);
        (s.LastTapAt, s.LastTapShown, s.TapReading) = (now, false, reading);
        ThrottleEdge(up ? 1 : -1, now);
        s.NextAt = now + StopTapDown;
        Volatile.Write(ref s.PressUntil, now + StopTapDown + 100);
        s.TapDown = s.Down = true;
        Log($"stop +{now - s.Start}: tap {(up ? "Up" : "Down")}{(s.Reading ? $" at {reading:0.##}" : "")}");
    }

    /// <summary>Press the back control: for len ms (a car's pulse), or with len 0 (the first press) as long as is safe.</summary>
    void Press(VehicleStop s, long now, long len, float v)
    {
        (s.PressAt, s.PressLen, s.PressV, s.Measured, s.Released, s.CoastAt, s.UpTap) = (now, len, v, false, false, 0, false);
        Volatile.Write(ref s.PressUntil, now + (len > 0 ? len : BlindLen(v)));
        s.Edges++;
        s.TapTimes.Add(now);
        s.LastTapAt = now;
        ThrottleEdge(-1, now);
        s.Down = true;
        Log($"stop +{now - s.Start}: press at {v:F2} m/s, {(len > 0 ? $"{len} ms" : $"{BlindLen(v)} ms unless the news says on")}");
    }

    void LetGo(VehicleStop s, long now, string why)
    {
        lock (_controlsLock) s.Down = false;
        s.Released = true;
        s.ReleasedAt = now;
        Log($"stop +{now - s.Start}: let go after {now - s.PressAt} ms ({why})");
    }

    /// <summary>The stop's own thread: lets go of a press whose time is up, sending the flags without it at once, so a
    /// busy window's thread can't hold the back control on past what was judged safe.</summary>
    void Watch(VehicleStop s)
    {
        while (_vstop == s)
        {
            if (s.Down && Environment.TickCount64 >= Volatile.Read(ref s.PressUntil))
                lock (_controlsLock)
                {
                    if (s.Down && _vstop == s)
                    {
                        s.Down = false;
                        Volatile.Write(ref s.LapsedAt, Environment.TickCount64);
                        if (S.Client is { Network.Connected: true } c)
                        {
                            var mv = c.Self.Movement;
                            if (s.UpTap) { mv.AtPos = false; mv.FastAt = mv.AtNeg; }
                            else { mv.AtNeg = false; mv.FastAt = mv.AtPos; }
                            mv.SendUpdate(true);
                        }
                    }
                }
            Thread.Sleep(5);
        }
    }

    void EndStop(VehicleStop s, string line)
    {
        _vstop = null;
        _throttleTaps = Math.Max(0, _throttleTaps - s.Edges);
        // what it says beats the count
        if (_thr is { } r && (r.Value <= 0 || ThrottleStep > 0))
            _throttleTaps = r.Value <= 0 ? 0 : Math.Clamp((int)MathF.Round(r.Value / ThrottleStep!.Value), 0, MostTaps);
        // the speed announcer won't say "Stopped." again for this stop
        _lastSpeedTold = 0;
        _speedToldAt = Environment.TickCount64;
        Log($"stop ended ({s.Why}, {s.Edges} presses): {line}");
        Line(line);
    }

    /// <summary>An arrow pressed during a stop: the controls are the person's again.</summary>
    void CancelStop()
    {
        if (_vstop is not { } s) return;
        _vstop = null;
        _throttleTaps = Math.Max(0, _throttleTaps - s.Edges);
        Line("Stop cancelled: the keys are yours.");
    }

    /// <summary>The back control as the stop holds it (Down(K.Back) reads it); Up, a tap putting a throttle back to 0.</summary>
    bool StopHolding => _vstop is { Down: true, Released: false, UpTap: false };
    bool StopHoldingUp => _vstop is { Down: true, Released: false, UpTap: true };

    /// <summary>A movement key pressed while seated: Up opens a throttle a step, Down closes it (counted 0-20).</summary>
    void CountThrottle(K key)
    {
        if (key == K.Fwd) _throttleTaps = Math.Min(MostTaps, _throttleTaps + 1);
        else if (key == K.Back) _throttleTaps = Math.Max(0, _throttleTaps - 1);
        ThrottleEdge(key == K.Fwd ? 1 : -1, Environment.TickCount64);
    }

    long _fwdSince, _fwdHeldFor;

    /// <summary>Up held a second or more, now or its last press: driven by holding, as a car is, not by taps.</summary>
    bool DrivenByHolding() => _k[(int)K.Fwd] && Environment.TickCount64 - _fwdSince >= 1000 || _fwdHeldFor >= 1000;

    // --- the seat's own track: ground speed from positions, apart from the speed announcer's ---------------------------

    readonly List<(long T, Vector2 P)> _seatTrack = new();

    /// <summary>How far the seat moved over about the last <paramref name="ms"/> (global metres), once the track is two
    /// thirds that long.</summary>
    Vector2? SeatMove(long ms)
    {
        if (_seatTrack.Count < 2) return null;
        var (t1, p1) = _seatTrack[^1];
        long now = Environment.TickCount64;
        int i = _seatTrack.FindIndex(x => now - x.T <= ms);
        if (i < 0 || i >= _seatTrack.Count - 1) i = _seatTrack.Count - 2;
        var (t0, p0) = _seatTrack[i];
        if (now - t0 < ms * 2 / 3) return null;
        return (p1 - p0) * (ms / 1000f) / Math.Max(0.05f, (now - t0) / 1000f);
    }

    float? SeatSpeed(long ms) => SeatMove(ms) is { } m ? m.Length() * 1000f / ms : null;

    // --- what the seat says ----------------------------------------------------------------------------------------------

    readonly record struct HeardLine(UUID Source, UUID Owner, string Name, string Text, bool OwnerOnly);
    readonly ConcurrentQueue<HeardLine> _heardLines = new();
    readonly ConditionalWeakTable<GridClient, object> _chatHooked = new();

    /// <summary>Objects' lines that hold a track-end phrase, queued from the network's thread for the tick.</summary>
    void HookChat()
    {
        lock (AgentSession.ClientHooks) AgentSession.ClientHooks.Add(HookChat);
        if (S.Client is { } c) HookChat(c);
    }

    void HookChat(GridClient c)
    {
        lock (_chatHooked)
        {
            if (_chatHooked.TryGetValue(c, out _)) return;
            _chatHooked.Add(c, new object());
        }
        c.Self.ChatFromSimulator += (_, e) =>
        {
            if (e.SourceType != ChatSourceType.Object || e.Type is ChatType.StartTyping or ChatType.StopTyping or ChatType.Debug) return;
            if (ThrottleIn(e.Message) is { } thr) _thrLinesHeard.Enqueue((e.SourceID, e.FromName ?? "", thr));
            if (!IsTrackEnd(e.Message)) return;
            _heardLines.Enqueue(new HeardLine(e.SourceID, e.OwnerID, e.FromName ?? "", e.Message, e.Type == ChatType.OwnerSay));
        };
        c.Self.IM += (_, e) =>
        {
            if (e.IM.Dialog != InstantMessageDialog.MessageFromObject || !IsTrackEnd(e.IM.Message)) return;
            _heardLines.Enqueue(new HeardLine(e.IM.IMSessionID, e.IM.FromAgentID, e.IM.FromAgentName ?? "", e.IM.Message, true));
        };
    }

    bool IsTrackEnd(string? text)
    {
        if (string.IsNullOrEmpty(text) || !_set.WalkTrackEnd) return false;
        var t = Plain(text);
        return TrackEndPhrases.Any(p => t.Contains(p)) || _set.TrackEndPhrases.Any(p => Plain(p) is { Length: > 0 } q && t.Contains(q));
    }

    static string Plain(string s) => string.Join(' ', s.ToLowerInvariant().Replace('_', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A track-end line: from the seat (a part of its linkset, or something with its name) or one of the person's
    /// own objects (a train's HUD, a locomotive apart from the car she sits in), while she's seated and moving.</summary>
    void TrackEndHeard(Body b, HeardLine h, long now)
    {
        bool mine = S.Client is { } c && h.Owner == c.Self.AgentID;
        bool seat = S.Region?.PrimById(h.Source) is { } p && b.Linkset?.Contains(p.LocalID) == true;
        bool named = b.SeatName is { } n && h.Name.Trim().Equals(n.Trim(), StringComparison.OrdinalIgnoreCase);
        string? skip = !b.Sitting ? "not seated" : !(seat || named || mine) ? "not from the seat or her own things"
            : !Moving(b) ? $"not moving ({Speed(b):0.0} m/s, {SeatSpeed(2000) ?? 0:0.0} over 2 s)" : _vstop != null ? "already stopping"
            : now - _trackEndAt < 3000 ? "one just acted on" : null;
        var heard = $"track end heard from {h.Name} ({(seat ? "the seat" : named ? "the seat's name" : mine ? "mine" : "elsewhere")}, "
            + $"{(h.OwnerOnly ? "to me" : "aloud")}): {h.Text}; {skip ?? "stopping it"}";
        Log(heard);
        // in the session log too: her train's "No Guide found" on 0.1.0.996 (2026-11-08) brought no "Track ends", and
        // only the walk log could have said why (most likely it had already stopped itself)
        S.Log.LogInformation("{Line}", heard);
        if (skip != null) return;
        _trackEndAt = now;
        bool heldUp = DrivenByHolding();
        ReleaseAll();
        StartVehicleStop(b, "track end", heldUp);
        Sounds.Play(CueSounds.Cue.TrackEnd, 0.7f, null);   // urgent and rare: whatever the other cues are set to
        Line("Track ends: stopping.");
    }
}
