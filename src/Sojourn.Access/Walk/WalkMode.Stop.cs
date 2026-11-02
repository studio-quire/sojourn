using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LibreMetaverse;
using Sojourn.Core.Agents;
using Vector2 = System.Numerics.Vector2;

namespace Sojourn.Access.Walk;

/// <summary>
/// Stopping a vehicle by its controls (docs/access-app.md > Walk mode > Stopping a vehicle): Space in walk mode or
/// //stop while seated on something moving, and by itself when the vehicle says its track has ended ("sensor : No
/// further track guide detected", "no_sensor : No Guide found": a blind tester's freight train ran off the end of its
/// track and derailed or stranded her, 2026-11-01). It does what she would: the back control, held, through the same
/// flags the arrows send; if holding it doesn't slow the vehicle within a second (a throttle train, whose Down press
/// takes its throttle a step down), Down tapped instead, as many times as she tapped Up on it (her train's throttle goes
/// 5% a tap, so 20 at most); and it lets go as soon as the vehicle is nearly still, or starts back, so it never drives it
/// backwards. The flags are held apart from the arrows' (leaving walk mode, another window or a notice doesn't let go
/// of them); an arrow pressed meanwhile hands the controls back. Written by Claude Opus 5.5, 2026-11-01.
/// </summary>
public sealed partial class WalkMode
{
    /// <summary>The phrases that mean the track runs out (as well as the person's own, walk_track_end_phrases), matched
    /// within an object's line, ignoring case, underscores and spacing.</summary>
    public static readonly string[] TrackEndPhrases = { "no further track", "no guide found", "track guide" };

    const long StopHoldJudge = 1200;     // holding the back control this long without slowing on: taps instead
    const long StopTapDown = 150, StopTapUp = 150;
    const long StopLimit = 10_000;
    const float StillSpeed = 0.5f;
    const int MostTaps = 20;             // 100% at 5% a tap, the blind tester's train

    sealed class VehicleStop
    {
        public long Start, PhaseAt, NextAt, ReleasedAt;
        public float StartSpeed, LastSpeed;
        public Vector2 Dir;
        public bool Taps, Down, Released;
        public int TapsLeft, Edges;
        public string Why = "";
    }

    VehicleStop? _vstop;
    volatile string? _stopAsked;
    int _throttleTaps;
    bool _wasSitting;
    long _trackEndAt;

    /// <summary>A stop under way ("hold", "taps", "settling") or null (the status op).</summary>
    public string? Stopping => _vstop is not { } v ? null : v.Released ? "settling" : v.Taps ? "taps" : "hold";
    /// <summary>Up taps less Down taps while seated on this seat, 0-20: how far a throttle has been opened by the arrows.</summary>
    public int ThrottleTaps => _throttleTaps;

    /// <summary>//stop: the same as Space in walk mode, from any thread (it happens on the next tick).</summary>
    public void RequestStop() => _stopAsked = "//stop";

    /// <summary>Space in walk mode and //stop: seated on something moving, it's stopped by its controls; otherwise (or
    /// as well) everything the app was walking stops.</summary>
    void StopKey(Body? b)
    {
        if (b is { Sitting: true } seated && Moving(seated))
        {
            StopOthers?.Invoke();
            ReleaseAll();
            if (_vstop != null) { Line("Still stopping."); return; }
            StartVehicleStop(seated, "manual");
            Line("Stopping.");
            return;
        }
        StopAll();
    }

    bool Moving(Body b) => Speed(b) > StillSpeed || (SeatSpeed(2000) ?? 0) > StillSpeed;

    void StartVehicleStop(Body b, string why)
    {
        long now = Environment.TickCount64;
        var dir = SeatMove(1000) is { } m && m.Length() > 0.3f ? Vector2.Normalize(m) : new Vector2(b.Vel.X, b.Vel.Y) is { } v && v.Length() > 0.1f ? Vector2.Normalize(v) : Vector2.Zero;
        _vstop = new VehicleStop
        {
            Start = now, PhaseAt = now, Why = why, Down = true, Dir = dir, Edges = 1,   // holding is a press
            // from positions, not the region's word on the velocity (a train's swung 19, 6, 13, 8 m/s, 2026-11-01)
            StartSpeed = SeatSpeed(2000) ?? Speed(b),
        };
        _vstop.LastSpeed = _vstop.StartSpeed;
        Log($"stop {why}: speed {_vstop.StartSpeed:F2}, throttle taps {_throttleTaps}");
    }

    /// <summary>Each tick, before Drive: the seat's track, the track-end lines heard, a stop asked for, and a stop's
    /// next press or release.</summary>
    void StopTick(Body b, long now)
    {
        if (b.Sitting != _wasSitting) { _throttleTaps = 0; _seatTrack.Clear(); _wasSitting = b.Sitting; }
        if (b.Sitting)
        {
            var g = new Vector2((b.Region >> 32) + b.Pos.X, (b.Region & 0xFFFFFFFF) + b.Pos.Y);
            if (_seatTrack.Count == 0 || _seatTrack.Last().P != g || now - _seatTrack.Last().T > 250) _seatTrack.Add((now, g));
            while (_seatTrack.Count > 0 && now - _seatTrack[0].T > 3000) _seatTrack.RemoveAt(0);
        }
        while (_heardLines.TryDequeue(out var h)) TrackEndHeard(b, h, now);
        if (Interlocked.Exchange(ref _stopAsked, null) != null) StopKey(b);
        if (_vstop is { } s) StopStep(b, s, now);
    }

    void StopStep(Body b, VehicleStop s, long now)
    {
        if (!b.Sitting) { EndStop(s, "You're no longer seated."); return; }
        float reported = Speed(b), shortSpeed = SeatSpeed(700) ?? reported;
        // the region's word on the velocity along the way it was going: positions lag (they're judged over a stretch),
        // and a car held in reverse thrust goes from 10 m/s to backing in under a second
        var vel = new Vector2(b.Vel.X, b.Vel.Y);
        float along = s.Dir != Vector2.Zero ? Vector2.Dot(vel, s.Dir) : reported;
        // moving back the way it came: let go at once (a throttle taken past zero into reverse, or Back as reverse)
        if (s.Dir != Vector2.Zero && now - s.Start > 300 && -along > 0.3f)
        {
            EndStop(s, s.Released ? $"Let go, but it's moving backward, at {SpeedWords(-along)}."
                : $"Let go: it began moving backward, at {SpeedWords(-along)}.");
            return;
        }
        if (now - s.Start > StopLimit)
        {
            float left = SeatSpeed(2000) ?? reported;
            EndStop(s, left < StillSpeed ? "Stopped." : $"Couldn't stop it: it's still moving at {SpeedWords(left)}.");
            return;
        }
        bool still = along < StillSpeed && reported < 1f;
        if (s.Released)
        {
            // let go: said stopped once the longer measure agrees; moving on again, pressing again (while taps remain)
            if ((SeatSpeed(2000) ?? reported) < StillSpeed) { EndStop(s, "Stopped."); return; }
            if (along > 1f && shortSpeed > 1f && now - s.ReleasedAt > 1000 && (!s.Taps || s.TapsLeft > 0))
            {
                s.Released = false;
                s.Down = !s.Taps;
                if (s.Down) s.Edges++;
                s.NextAt = now;
            }
            return;
        }
        if (still)
        {
            s.Released = true;
            s.ReleasedAt = now;
            if (s.Down) s.Down = false;
            return;
        }
        if (!s.Taps)
        {
            s.Down = true;
            // held long enough to tell, and again each time as long: still slowing (a car's brake, Back as reverse thrust)
            // keeps holding; not, it's a throttle (the press took it a step down, a low throttle's from 10% to 5%, and
            // then it held), so taps. Over the whole stretch: a moving seat's position can come only once a second.
            if (now - s.PhaseAt >= StopHoldJudge)
            {
                float held = SeatSpeed(StopHoldJudge) ?? reported;
                if (held > s.LastSpeed * 0.85f)
                {
                    s.Taps = true;
                    s.Down = false;
                    int cap = _throttleTaps > 0 ? _throttleTaps : MostTaps;
                    s.TapsLeft = Math.Max(0, cap - s.Edges);   // holding was a press already
                    s.NextAt = now + StopTapUp;
                    Log($"stop: holding didn't slow it ({held:F2} after {s.LastSpeed:F2}): {s.TapsLeft} taps");
                }
                s.LastSpeed = held;
                s.PhaseAt = now;
            }
            return;
        }
        if (now < s.NextAt) return;
        if (s.Down) { s.Down = false; s.NextAt = now + StopTapUp; }
        else if (s.TapsLeft > 0) { s.Down = true; s.TapsLeft--; s.Edges++; s.NextAt = now + StopTapDown; }
    }

    void EndStop(VehicleStop s, string line)
    {
        _vstop = null;
        _throttleTaps = Math.Max(0, _throttleTaps - s.Edges);
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

    /// <summary>The back control as the stop holds it (Down(K.Back) reads it).</summary>
    bool StopHolding => _vstop is { Down: true, Released: false };

    /// <summary>A movement key pressed while seated: Up opens a throttle a step, Down closes it (counted 0-20).</summary>
    void CountThrottle(K key)
    {
        if (key == K.Fwd) _throttleTaps = Math.Min(MostTaps, _throttleTaps + 1);
        else if (key == K.Back) _throttleTaps = Math.Max(0, _throttleTaps - 1);
    }

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
            if (e.SourceType != ChatSourceType.Object || e.Type is ChatType.StartTyping or ChatType.StopTyping || !IsTrackEnd(e.Message)) return;
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
        Log($"track end heard from {h.Name} ({(seat ? "the seat" : named ? "the seat's name" : mine ? "mine" : "elsewhere")}, "
            + $"{(h.OwnerOnly ? "to me" : "aloud")}): {h.Text}");
        if (!b.Sitting || !(seat || named || mine) || !Moving(b) || _vstop != null || now - _trackEndAt < 3000) return;
        _trackEndAt = now;
        ReleaseAll();
        StartVehicleStop(b, "track end");
        Sounds.Play(CueSounds.Cue.TrackEnd, 0.7f, null);   // urgent and rare: whatever the other cues are set to
        Line("Track ends: stopping.");
    }
}
