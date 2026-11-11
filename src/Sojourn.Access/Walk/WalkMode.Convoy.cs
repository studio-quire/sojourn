using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.World;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>
/// Driving after someone (docs/access-app.md > Walk mode > Driving after someone): //follow while at the wheel of a
/// vehicle steered by its keys holds those keys for the person, along the way the one ahead went, staying about
/// ConvoyGap behind, or as far as //follow NAME METRES says. Any arrow or Space takes it back. Not for what runs on rails or a throttle: the keys there don't
/// steer, and Up held opens a throttle all the way. Written by Claude Opus 5.5, 2026-11-04, after the blind tester asked
/// for vehicles following each other, a beacon over two cars' engines not having been enough. Losing sight of them
/// (2026-11-08, after her scooter trip lost her partner just over a border): it drives on where they went, a little
/// way, and picks them up again.
/// </summary>
public sealed partial class WalkMode
{
    const float ConvoyGap = 10f, ConvoyCrumb = 2.5f, ConvoyFar = 60f, ConvoyJump = 100f, ConvoyDeadband = 0.12f, ConvoyBraking = 2.5f;
    const long ConvoyEvery = 100, ConvoySteerCycle = 300, ConvoyLostAfter = 6000, ConvoyStuckAfter = 6000;
    // the keys reading as untaken: around a crossing the region left and the one entered each send their word on the
    // vehicle script's keys, in either order, so only 10 s after a crossing counts, and elsewhere 2 s of it
    const long ConvoyCrossingGrace = 10_000, ConvoyKeysGrace = 2000;
    // Lost sight of them: on along their way at their pace (10 m/s at most), to 25 m past the border their way meets
    // within 40 m of where they were last seen, else 15 m past it; said once it's lasted 1.5 s (a moment's gap says
    // nothing); given up 20 s after they were last seen, or 4 s after coming to rest at the end of the way on, either
    // only once 4 s have passed since her own last crossing (the region entered names who's in it within a second or two)
    const long ConvoySayLost = 1500, ConvoySearchFor = 20_000, ConvoyRestAtEnd = 4000, ConvoyAfterCrossing = 4000;
    const float ConvoySearchPace = 10f, ConvoyPastBorder = 25f, ConvoyPastPoint = 15f, ConvoyBorderAhead = 40f, ConvoyBorderBehind = 30f;
    // Down and Up for speed only ever in presses of a tenth of a second, let go by a thread of their own (a busy window
    // thread can't stretch one), with time between for the region's news of them (it shows a press ~150 ms on): a
    // vehicle's Down is often reverse thrust (the visitor's kart loses 9.7 m/s in half a second of it), and held until the
    // news said slow it drove her scooter backwards at 10 m/s round her partner for half a minute (her ride of
    // 2026-11-09). None under ConvoyBrakeFloor going forward, nor for ConvoyAfterBacking after a backing was stopped (its
    // Up presses could tip it forward into Down again, rocking). Going backward faster than ConvoyBacking, Up presses
    // alone (no steering: what steers forward circles in reverse), and if that hasn't stopped it in ConvoyBackingFor, it's
    // let go of, said. A turn of over ConvoySharp (radians) slows to ConvoyTurnPace first, as a driver brakes for a
    // U-turn. ConvoyBraking, the slowing the room to stop is reckoned with, allows for presses only part of the time.
    const float ConvoyBrakeFloor = 2f, ConvoyBacking = 0.8f, ConvoySharp = 1.6f, ConvoyTurnPace = 3f, ConvoyMaxOffset = 1.75f;
    const long ConvoyBackingFor = 4000, ConvoyPressFor = 100, ConvoyAfterBacking = 1500;
    // Speeds by the seat's moves as well as the region's word (her ride of 2026-11-11: the word swung 0.4 to 12.3 m/s
    // and four times said backward while she covered 4-6 m every 2 s): its velocity over ConvoyMoveWindow by where the
    // region put it. Backward by the word is taken only if the moves over ConvoyQuickWindow don't say forward faster
    // than ConvoyMovesOn (over the longer window a real reverse showed 0.3-0.6 s late: the review of 8700ca8).
    const long ConvoyMoveWindow = 500, ConvoyQuickWindow = 250;
    const float ConvoyMovesOn = 0.5f;
    // What a Down press takes off, learned from each (ConvoyKick): the speed before against the lowest in the
    // ConvoyKickShows after it (what it loses by itself meanwhile, Up let go, counting too: on the safe side), the stop's
    // BlindDecel for a press's time until one's been seen. A press goes only while that leaves the stop's Margin, and the
    // next only once its effect is in. On 2026-11-11, 84 of the 85 moments her scooter's word read backward came within a
    // couple of seconds of the drive's own Down presses (the review of 8700ca8): a press most likely kicked it into
    // reverse. (Presses shortened to leave the margin, as the stop's pulses are, were tried: the test's kart loses about
    // 5 m/s² by itself with no key, which a 59 ms press's measure counted as 100 m/s² of braking, so none went where one was
    // needed)
    const long ConvoyKickShows = 450;
    // The seat not sent over a crossing for longer than ConvoyDoubtTrust (6 s into Rivula that day, as her scooter ran on
    // 70 m and past her partner): where she is by the region's map list, which names her. Her log's whole seconds put
    // the seat 0-1 s after 19 of that day's 20 crossings, which is up to 2 s, so 1.5 s keeps most of them out. A
    // list place further than ConvoyListOff plus ConvoyListOffPace a second from where she'd be by now isn't taken; a
    // pace by the lists is good to ConvoyListError metres over their time apart (whole metres). Someone seen
    // implausibly far away is taken as a jump only once that has lasted ConvoyJumpFor (her partner's crossing into
    // Astarte read as one).
    const long ConvoyDoubtTrust = 1500, ConvoyJumpFor = 2000;
    const float ConvoyListOff = 5f, ConvoyListOffPace = 15f, ConvoyListError = 1.5f;

    /// <summary>What driving after someone has learned of the vehicle under a seat, kept from one //follow to the next on
    /// it (she started it again a dozen times on 2026-11-11): the way it travels against the way the seat faces, and
    /// what a Down press does to it. The stop reads the first (WalkMode.Stop.cs).</summary>
    sealed class SeatLearned(string? seat)
    {
        public readonly string? Seat = seat;
        public float Offset;                          // radians, learned on straights with Up held
        public int OffsetSeen;                        // times it was learned from
        public float Kick = BlindDecel * ConvoyPressFor / 1000f;   // m/s a Down press takes off, as seen after each
        public int Kicks;                             // presses seen
    }

    SeatLearned? _seatLearned;

    /// <summary>The way this seat's vehicle goes forward as driving after someone learned it (radians from the way the
    /// seat faces), or null: not learned, or another seat.</summary>
    float? LearnedFacing(Body b) => _seatLearned is { OffsetSeen: >= 10 } l && l.Seat == b.SeatName ? l.Offset : null;

    sealed class Convoy(string name, UUID id, float gap, SeatLearned learned)
    {
        public readonly string Name = name;
        public readonly UUID Id = id;
        public readonly float Gap = gap;
        public readonly SeatLearned Learned = learned;
        public readonly Queue<Vector2> Way = new();   // where they went, in the grid's metres, so a crossing moves nothing
        public int Guesses;                           // the last this many of Way are the way on guessed while they're lost
        public Vector2? Last, PaceFrom;
        public float Pace;                            // how fast they're going, metres a second
        public long DecideAt, LostSince, StuckSince, LogAt, SteeredAt, PaceAt, KeysLostSince;
        public long CrossedAt = long.MinValue / 2;
        public string? Region, CrossedInto, Logged;
        public bool Fwd, Back, Left, Right, ToldWith, ToldFar, Doubt;
        public long PressAt, BackAt, BackingSince, BackedAt;   // the last press begun, the last Down, backing since, a backing stopped
        public bool Backing;                          // backing up without being asked, Up pressed to stop it (logged once)
        public volatile bool PressDown, PressUp;      // a press under way, until PressUntil (let go by ConvoyPresses' thread)
        public long PressUntil;
        public int DownPresses, UpPresses, HeldBack;  // since the last log line (HeldBack: ticks a Down was due but too strong)
        // a Down press's effect being measured (ConvoyKick): the press (0: none), and the speed before it and the lowest
        // since, by the region's word and by its moves over a quarter second
        public long KickAt;
        public float KickSaid, KickMinSaid, KickMoved, KickMinMoved;
        public bool KickMovesKnown;
        // the last time they were seen: where (the grid's metres), how high, when, in which region, how, and the velocity
        // their copy reported (a copy left frozen where they crossed out keeps the last it had)
        public Vector2? Seen, SeenVel, Raw;
        public float SeenZ;
        public long SeenAt, RawAt;
        public RegionWorld? SeenIn;
        public string SeenHow = "";
        public readonly List<Vector2> Trail = new();  // where they've been lately, a metre or more apart, the newest last
        public Search? Lost;
        public bool Disputed;                         // the region's word says backward, the seat's moves forward (logged once)
        public long FarSince;                         // seen implausibly far from where they were, since
        // where she was at the last tick the seat was known, when, and the way she was driving, for a crossing whose seat
        // isn't sent for a while; then her place on the region's map list, the one before, the list a Down went for, a
        // list not taken, and her pace by the lists (MapPaceAt the list it's from; -1: as before the crossing) and how far
        // off it could be
        public Vector2? LastMe, MapMe, MapFrom;
        public Vector2 Heading;
        public float LastPace, MapPace, MapPaceErr;
        public bool MapTold;
        public long LastMeAt, DoubtSince, MapAt, MapFromAt, MapPressedFor, MapOffAt, MapPaceAt, TestDoubtUntil;
    }

    /// <summary>Driving on where they went while they can't be seen.</summary>
    sealed class Search
    {
        public long At;             // when they were last seen
        public Vector2 From, Dir;   // where, and the way they were heading (zero: not known, or standing)
        public float Z, Pace, Length, Laid;
        public bool Border, Told;
        public long RestSince;
        public string? Stop;        // what shortened the way on (water, a drop)
        public int Id;
    }

    int _searches;

    /// <summary>The session's log as well as the walk log: what driving after someone did is in a log she sends (on
    /// 0.1.0.996 it was only in SOJOURN_WALK_LOG's, so her motorbike trip of 2026-11-08 couldn't show why it went astray).</summary>
    void ConvoyLog(string line)
    {
        Log($"convoy: {line}");
        S.Log.LogInformation("convoy: {Line}", line);
    }

    long _blindFrom, _blindUntil;

    /// <summary>The scripted test's (//blindconvoy, with SOJOURN_TEST_CONVOY_BLIND=1): the one driven after goes unseen
    /// from that many seconds on, for so many, as a region that hides them would leave them.</summary>
    public void BlindConvoy(double after, double secs)
    {
        _blindFrom = Environment.TickCount64 + (long)(after * 1000);
        _blindUntil = _blindFrom + (long)(secs * 1000);
    }

    long _farFrom, _farUntil, _glitchEvery, _glitchAt, _testDoubt;

    /// <summary>The scripted test's (//farconvoy): the one driven after is seen 150 m east of where they are from that many
    /// seconds on, for so many, as a crossing's moment once put her partner.</summary>
    public void FarConvoy(double after, double secs)
    {
        _farFrom = Environment.TickCount64 + (long)(after * 1000);
        _farUntil = _farFrom + (long)(secs * 1000);
    }

    /// <summary>The scripted test's (//glitchconvoy): every so many seconds the region's word on the seat's velocity reads
    /// reversed for one of the convoy's ticks (0: never), as single words read backward on her scooter.</summary>
    public void GlitchConvoy(double every) { _glitchEvery = (long)(every * 1000); _glitchAt = Environment.TickCount64; }

    /// <summary>The scripted test's (//doubtconvoy): at the next crossing the seat counts as not sent for that many seconds,
    /// as into Rivula on 2026-11-11.</summary>
    public void DoubtConvoy(double secs) => _testDoubt = (long)(secs * 1000);

    Convoy? _convoy;

    public string? DrivingAfter => _convoy?.Name;

    bool ConvoyDown(K k) => _convoy is { } c && k switch
    {
        K.Fwd => c.Fwd || c.PressUp && Environment.TickCount64 < Volatile.Read(ref c.PressUntil),
        K.Back => c.PressDown && Environment.TickCount64 < Volatile.Read(ref c.PressUntil),
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
        if (_seatLearned?.Seat != b.SeatName) _seatLearned = new SeatLearned(b.SeatName);
        _convoy = new Convoy(name, id, gap ?? ConvoyGap, _seatLearned!);
        // the seat's name on a line of its own: a log sent leaves out any line repeating words someone wrote, and on
        // 2026-11-08 that took every line naming her scooter (its name was most likely in an IM she was sent)
        ConvoyLog($"driving after {name}, {_convoy.Gap:0.#} m behind, in {b.RegionName}");
        ConvoyLog($"the seat driven: {seat}");
        Line($"Driving {seat} after {name}, about {Bearings.Metres(_convoy.Gap)} behind. Any arrow key or Space takes it back.");
        var mine = _convoy;
        new Thread(() => ConvoyPresses(mine)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "convoy presses" }.Start();
    }

    /// <summary>Starts a press of Down (or Up), sent by this tick's Drive and let go by ConvoyPresses.</summary>
    static void ConvoyPress(Convoy c, bool up, long now)
    {
        Volatile.Write(ref c.PressUntil, now + ConvoyPressFor);
        if (up) { c.PressUp = true; c.UpPresses++; } else { c.PressDown = true; c.DownPresses++; c.BackAt = now; }
        c.PressAt = now;
    }

    /// <summary>Whether a Down press may go at this speed: what one is learned to take off leaves Margin.</summary>
    static bool ConvoyMayPress(Convoy c, float v) => v - c.Learned.Kick >= Margin;

    /// <summary>A Down press, its effect measured from the speeds now (ConvoyKick).</summary>
    static void ConvoyBrake(Convoy c, long now, float said, float? quick)
    {
        (c.KickAt, c.KickSaid, c.KickMinSaid) = (now, said, said);
        (c.KickMoved, c.KickMinMoved, c.KickMovesKnown) = (quick ?? 0, quick ?? 0, quick != null);
        ConvoyPress(c, up: false, now);
    }

    /// <summary>What the last Down press took off, once its effect is in the news (ConvoyKickShows on): the speed before
    /// it against the lowest since, by the region's word where the seat's moves over a quarter second show at least a
    /// quarter of that drop (a word read wrong is no measure; a real kick shows in where the seat goes), else by the moves
    /// (which lag the word: slowing already, they start higher). A word backward while the moves go on forward, which
    /// isn't taken for backing either, doesn't count (the test's reversed word, landing in one, made a press of the kart
    /// take 11 m/s off), nor does a measure a crossing came in. A harder one is taken at once, a gentler one a third of
    /// the way.</summary>
    void ConvoyKick(Convoy c, long now, float said, float? quick)
    {
        if (c.KickAt == 0) return;
        if (!(said < -ConvoyBacking && quick > ConvoyMovesOn)) c.KickMinSaid = MathF.Min(c.KickMinSaid, said);
        if (c.KickMovesKnown && quick is { } q) c.KickMinMoved = MathF.Min(c.KickMinMoved, q);
        if (now - c.KickAt < ConvoyKickShows) return;
        long pressed = c.KickAt;
        c.KickAt = 0;
        if (c.CrossedAt >= pressed) return;
        float bySaid = c.KickSaid - c.KickMinSaid, byMoves = c.KickMoved - c.KickMinMoved;
        float drop = MathF.Max(0f, !c.KickMovesKnown || byMoves >= bySaid / 4 ? bySaid : byMoves);
        var l = c.Learned;
        float was = l.Kick;
        l.Kick = drop > l.Kick ? drop : l.Kick + (drop - l.Kick) / 3;
        l.Kicks++;
        if (l.Kicks == 1 || drop > was * 1.2f)
            ConvoyLog($"a Down took {drop:0.0} m/s off (the region's word {c.KickSaid:0.0} to {c.KickMinSaid:0.0}"
                + $"{(c.KickMovesKnown ? $", its moves {c.KickMoved:0.0} to {c.KickMinMoved:0.0}" : "")}): one now taken to take {l.Kick:0.0} off");
    }

    /// <summary>A convoy's own thread: lets go of a press when its time is up, as the stop's does, whatever the window's
    /// thread is doing (a stall there would otherwise hold Down on).</summary>
    void ConvoyPresses(Convoy c)
    {
        while (_convoy == c)
        {
            if ((c.PressDown || c.PressUp) && Environment.TickCount64 >= Volatile.Read(ref c.PressUntil))
                lock (_controlsLock)
                {
                    bool up = c.PressUp;
                    c.PressDown = c.PressUp = false;
                    if (_convoy == c && S.Client is { Network.Connected: true } cl)
                    {
                        var mv = cl.Self.Movement;
                        if (!up) { mv.AtNeg = false; mv.FastAt = mv.AtPos; }
                        else if (!c.Fwd) { mv.AtPos = false; mv.FastAt = mv.AtNeg; }
                        mv.SendUpdate(true);
                    }
                }
            Thread.Sleep(5);
        }
    }

    /// <summary>Lets go of the keys (the next Drive clears them), saying why if there's a why.</summary>
    public void EndConvoy(string? line)
    {
        if (_convoy is not { } c) return;
        _convoy = null;
        ConvoyLog($"ended driving after {c.Name}: {line ?? "quietly"}");
        if (line != null) Line(line);
    }

    static float Wrap(float a) => MathF.Atan2(MathF.Sin(a), MathF.Cos(a));

    void ConvoyTick(Body b, long now)
    {
        if (_convoy is not { } c || now < c.DecideAt) return;
        c.DecideAt = now + ConvoyEvery;
        bool doubt = SeatInDoubt(b);
        if (!b.Sitting && !doubt) { EndConvoy($"You're off your seat, so no longer driving after {c.Name}."); return; }
        if (c.Region != null && b.RegionName != c.Region)
        {
            c.CrossedAt = now;
            c.CrossedInto = b.RegionName;
            string keys = (S.ControlsTaken & MoveKeys) != 0 ? "taken" : "not taken";
            ConvoyLog($"crossed into {b.RegionName}{(doubt ? ", the seat not sent yet" : "")}; the keys {keys}");
            if (_testDoubt > 0) { c.TestDoubtUntil = now + _testDoubt; _testDoubt = 0; }
        }
        c.Region = b.RegionName;
        doubt |= now < c.TestDoubtUntil;
        bool crossing = doubt || now - c.CrossedAt < ConvoyCrossingGrace;
        // 0.1.0.996 ended at the first tick of a crossing that read the keys untaken (her scooter into Berbera,
        // 2026-11-08), and with the old region's word and the new one's crossing on the way, a moment of that is expected
        if ((S.ControlsTaken & MoveKeys) == 0)
        {
            if (c.KeysLostSince == 0)
            {
                c.KeysLostSince = now;
                string since = crossing ? $", {(now - c.CrossedAt) / 1000.0:0.0} s after crossing into {c.CrossedInto}" : "";
                ConvoyLog($"the keys read as not taken{since}");
            }
            else if (!crossing && now - c.KeysLostSince > ConvoyKeysGrace)
            {
                bool atCrossing = c.CrossedInto != null && c.KeysLostSince - c.CrossedAt < ConvoyCrossingGrace;
                EndConvoy(atCrossing
                    ? $"The vehicle let go of your keys at the crossing into {c.CrossedInto}, so no longer driving after {c.Name}. Sitting on it again usually gives them back."
                    : $"The vehicle let go of your keys, so no longer driving after {c.Name}.");
                return;
            }
        }
        else if (c.KeysLostSince != 0)
        {
            ConvoyLog($"the keys are taken again, after {now - c.KeysLostSince} ms");
            c.KeysLostSince = 0;
        }
        // until the region has sent her avatar and its seat, where she is may be in the frame of the region she left
        // (0.1.0.996 said "getting away, 289 metres off" at that crossing, and 276 and 226 on 2026-11-05): nothing is
        // decided, and Up stays as it was, as a driver holds the throttle over a border, the wheel straight and no brake
        // (a Down or a turn held for the crossing's second or two would be her scooter backing or swerving: the review of
        // 2026-11-09). For longer than a crossing's moment, ConvoyByMap: where she is by the region's map list
        if (doubt != c.Doubt)
        {
            c.Doubt = doubt;
            if (doubt)
            {
                (c.DoubtSince, c.MapAt, c.MapPressedFor, c.MapMe, c.MapFrom, c.MapFromAt, c.MapTold) = (now, 0, 0, null, c.LastMe, c.LastMeAt, false);
                (c.MapPace, c.MapPaceAt, c.MapPaceErr, c.MapOffAt) = (c.LastPace, -1, 0, 0);
                c.KickAt = 0;   // (a press's effect can't be seen till the seat is sent)
                ConvoyLog("the seat not sent yet: Up held as it was, nothing else");
            }
            else if (now - c.DoubtSince > ConvoyDoubtTrust) ConvoyLog($"the seat sent after {(now - c.DoubtSince) / 1000.0:0.0} s");
        }
        if (doubt)
        {
            c.Left = c.Right = false;
            c.FarSince = 0;   // (her own crossing between two far sightings isn't "on end")
            if (c.Backing) { c.Backing = false; c.BackingSince = 0; }
            if (now - c.DoubtSince >= ConvoyDoubtTrust) ConvoyByMap(c, b, now);
            return;
        }

        Utils.LongToUInts(b.Region, out var gx, out var gy);
        var me = Global(b);
        // its speed over the ground by its moves, where the region's news of its place is recent: over half a second for
        // the room to slow, over a quarter for whether it's backing and what a Down press did
        var moved = SeatVelocity(ConvoyMoveWindow);
        var quick = SeatVelocity(ConvoyQuickWindow);
        // (the test's glitch only while its moves are known and go on forward: what's tested is a word read backward
        // against them, and one landing as it starts off or just after a crossing is rightly taken for backing)
        if (_glitchEvery > 0 && now - _glitchAt >= _glitchEvery && quick is { } gq
            && Vector2.Dot(gq, new Vector2(MathF.Cos(b.Yaw + c.Learned.Offset), MathF.Sin(b.Yaw + c.Learned.Offset))) > 1f)
        {
            _glitchAt = now;
            b = b with { Vel = -b.Vel };
            ConvoyLog("(the test's) the region's word on the velocity reversed for a tick");
        }
        // and by the region's word
        float speed = Speed(b);
        float ground = moved?.Length() ?? speed, reach = MathF.Max(5f, ground);
        Vector2 them;
        var sight = ConvoySees(c, now);
        if (sight == null) c.FarSince = 0;   // (a jump is seen too far 2 s on end: a tick without them starts it again)
        if (sight is { } far0 && ConvoyTooFar(c, far0, gx, gy, now) is { } far)
        {
            // further from where they were than they could have gone: a teleport, or a crossing's moment (her partner
            // riding into Astarte on 2026-11-11 was taken for a jump and the drive let go, while 33 s later he was 33 m on,
            // waiting). Unseen meanwhile, so it drives on where they went, and a jump once it has lasted
            if (c.FarSince == 0)
            {
                c.FarSince = now;
                var off = S.FrameOffset(far0.Region);
                ConvoyLog($"{c.Name} seen {far:0} m from where they were, by {far0.How} in {far0.Region.Name} {far0.Pos.X - off.X:0},{far0.Pos.Y - off.Y:0}: not taken yet; {InView(c.Id)}");
            }
            if (now - c.FarSince >= ConvoyJumpFor)
            {
                EndConvoy($"{c.Name} jumped away, a teleport most likely, so I've let go of the keys.");
                RequestStop();
                return;
            }
            sight = null;
        }
        else if (sight != null && c.FarSince != 0)
        {
            ConvoyLog($"{c.Name} seen where they could be again, after {now - c.FarSince} ms");
            c.FarSince = 0;
        }
        if (sight is { } s)
        {
            // a region sends a moving avatar's place only now and then (when its velocity changes, OpenSim; when carrying
            // it on by its velocity would be out, Second Life): a copy is carried on by the velocity it last reported, as a
            // viewer draws it, for up to a second and a half since its place last changed
            var raw = new Vector2(gx + s.Pos.X, gy + s.Pos.Y);
            if (c.Raw != raw) { c.Raw = raw; c.RawAt = now; }
            them = raw;
            if (s.Vel is { } vel && vel.Length() >= 0.5f) them += vel * MathF.Min(1.5f, (now - c.RawAt) / 1000f);
            if (c.LostSince != 0) ConvoyLog($"{c.Name} seen after {now - c.LostSince} ms");
            c.LostSince = 0;
            if (c.Lost is { } lost)
            {
                // seen again (far from where they could have got to since is taken above)
                float off = Vector2.Distance(lost.From, them);
                ConvoyLog($"{c.Name} seen again after {now - lost.At} ms, by {s.How} in {s.Region.Name} {s.Pos.X - S.FrameOffset(s.Region).X:0},{s.Pos.Y - S.FrameOffset(s.Region).Y:0}, {off:0} m from where they were last seen, {Vector2.Distance(them, me):0} m off");
                if (lost.Told) Line($"Found {c.Name} again, {Bearings.Metres(Vector2.Distance(them, me))} away.");
                // the way on was a guess: what's left of it goes, and from what's left of theirs it's straight to them
                var kept = c.Way.Take(c.Way.Count - c.Guesses).ToList();
                c.Way.Clear();
                foreach (var p in kept) c.Way.Enqueue(p);
                c.Guesses = 0;
                c.Last = null;
                c.PaceFrom = null;
                c.Trail.Clear();
                c.Lost = null;
            }
            c.Seen = them;
            c.SeenZ = s.Pos.Z;
            c.SeenAt = now;
            c.SeenIn = s.Region;
            c.SeenHow = s.How;
            c.SeenVel = s.Vel;
            if (c.Trail.Count == 0 || Vector2.Distance(c.Trail[^1], them) >= 1f) { c.Trail.Add(them); if (c.Trail.Count > 12) c.Trail.RemoveAt(0); }
            if (c.Last is not { } was || Vector2.Distance(was, them) >= ConvoyCrumb) { c.Way.Enqueue(them); c.Last = them; }
            // their pace over half a second or more (positions come in steps), and the room it takes to come down to it
            if (c.PaceFrom is not { } from) { c.PaceFrom = them; c.PaceAt = now; }
            else if (now - c.PaceAt >= 500)
            {
                c.Pace = Vector2.Distance(from, them) * 1000f / (now - c.PaceAt);
                c.PaceFrom = them;
                c.PaceAt = now;
            }
        }
        else if (c.Seen is null)
        {
            // never seen since it started (it found them a moment before): let go after a while, as before there was a way on
            c.Fwd = c.Back = c.Left = c.Right = false;
            if (c.LostSince == 0) { c.LostSince = now; ConvoyLog($"{c.Name} not seen ({InView(c.Id)})"); }
            else if (now - c.LostSince > ConvoyLostAfter) { EndConvoy($"Lost {c.Name}, so I've let go of the keys."); RequestStop(); }
            return;
        }
        else
        {
            var lost = c.Lost ??= LoseSight(c, b, now);
            // where they'd be by now along the way on, and points along it to steer by
            float along = MathF.Min(lost.Length, lost.Pace * (now - lost.At) / 1000f);
            them = lost.From + lost.Dir * along;
            while (lost.Laid + ConvoyCrumb <= along) { lost.Laid += ConvoyCrumb; c.Way.Enqueue(lost.From + lost.Dir * lost.Laid); c.Guesses++; }
            c.Pace = along < lost.Length ? lost.Pace : 0;
            if (!lost.Told && now - lost.At >= ConvoySayLost)
            {
                lost.Told = true;
                Line($"Lost sight of {c.Name}{(lost.Border ? " at the border" : "")}; going {(lost.Pace > 0 ? "where they went" : "to where they were")}.");
            }
            bool settled = now - c.CrossedAt > ConvoyAfterCrossing;
            if (along >= lost.Length && ground < 0.5f) { if (lost.RestSince == 0) lost.RestSince = now; }
            else lost.RestSince = 0;
            if (settled && (now - lost.At > ConvoySearchFor || lost.RestSince != 0 && now - lost.RestSince > ConvoyRestAtEnd))
            {
                ConvoyLog($"searched {(now - lost.At) / 1000.0:0.0} s, {along:0} m along the way on of {lost.Length:0}: {InView(c.Id)}");
                EndConvoy($"Couldn't find {c.Name} again, so I've let go of the keys.");
                RequestStop();
                return;
            }
        }
        bool guessing = c.Lost != null;

        // points she's come within reach of, or gone past to one side (the next no further than it), are behind her
        while (c.Way.Count > 1)
        {
            var head = c.Way.Peek();
            float dHead = Vector2.Distance(head, me);
            if (dHead >= reach && Vector2.Distance(c.Way.ElementAt(1), me) > dHead) break;
            c.Way.Dequeue();
            c.Guesses = Math.Min(c.Guesses, c.Way.Count);
        }
        var aim = c.Way.Count > 0 ? c.Way.Peek() : them;
        float dist = Vector2.Distance(them, me);
        // close behind them the way runs out within the reach: aim on past them the way they're heading, not at a point a
        // metre or two ahead, where every half-metre to one side read as a sharp turn and swung the keys (a 3 m gap at
        // 11 m/s on her scooter trip of 2026-11-08 left the aim 0.5 to 3 m ahead; her steering weaved)
        if (Vector2.Distance(aim, me) < reach)
            aim = dist < reach && ConvoyHeading(c) is { } heading ? them + heading * (reach - dist) : them;

        // steered by the way it faces, not the way it's going: travel lags the wheel, and steering by it overshoots into a spin.
        // The offset (a seat turned on its vehicle) learned only while Up drives it and no Down has lately: learned while
        // it backed, it took the back for the front; and only while its moves go the way the region's word says (a word
        // read wrong would teach a wrong offset)
        var said = new Vector2(b.Vel.X, b.Vel.Y);
        bool agree = moved is not { } m0 || m0.Length() > 2f && Vector2.Dot(Vector2.Normalize(m0), Vector2.Normalize(said)) > 0.9f;
        var l = c.Learned;
        if (speed > 2f && agree && now - c.SteeredAt > 500 && c.Fwd && !c.Backing && now - c.BackAt > 1000)
        {
            l.Offset = Math.Clamp(l.Offset + 0.3f * Wrap(MathF.Atan2(b.Vel.Y, b.Vel.X) - b.Yaw - l.Offset), -ConvoyMaxOffset, ConvoyMaxOffset);
            if (++l.OffsetSeen == 10) ConvoyLog($"the way it goes forward learned: {l.Offset * 180 / MathF.PI:0} degrees from the way the seat faces");
        }
        float err = Wrap(MathF.Atan2(aim.Y - me.Y, aim.X - me.X) - b.Yaw - l.Offset);   // positive is to the left
        // how fast it goes the way it's driven, backward below 0 (speed alone read her scooter's backing as closing on him),
        // by the region's word and by its moves. One word can be far off what the seat does: on her ride of 2026-11-11 the
        // words 2 s apart went 6.8, 9.8, 12.3, 0.4, 7.7 m/s, and four said backward, while it went 4-6 m every 2 s (most
        // likely her scooter kicked by the drive's own Down presses, the moves over 2 s smoothing them out). So the
        // room to slow is reckoned by its moves, a Down goes only while both say forward, and Up is held only while
        // whichever is faster leaves room
        var drive = new Vector2(MathF.Cos(b.Yaw + l.Offset), MathF.Sin(b.Yaw + l.Offset));
        float fwdSaid = Vector2.Dot(said, drive);
        float? fwdMoved = moved is { } m1 ? Vector2.Dot(m1, drive) : null;
        float? fwdQuick = quick is { } q1 ? Vector2.Dot(q1, drive) : null;
        float fwd = fwdMoved ?? fwdSaid, fwdLow = MathF.Min(fwd, fwdSaid), fwdHigh = MathF.Max(fwd, fwdSaid);
        float closing = MathF.Max(0f, fwd - c.Pace), room = Room(closing), roomUp = Room(MathF.Max(0f, fwdHigh - c.Pace));
        bool close = dist < c.Gap;
        // (no faster than a turn this sharp can be made at)
        float most = MathF.Abs(err) > ConvoySharp ? ConvoyTurnPace : MathF.Abs(err) > 1f ? 6f : float.MaxValue;
        ConvoyKick(c, now, fwdSaid, fwdQuick);
        // backward by the region's word, unless its moves over the last quarter second say it's going on forward: a word
        // read backward alone pressed Up, and on her scooter one Up press most likely set it going full ahead (at 10:00:17
        // that day, just before the crossing it ran on through). While they disagree it isn't steered either: steering
        // that's right going forward circles in reverse
        bool saidBack = fwdSaid < -ConvoyBacking || c.Backing && fwdSaid < -0.3f, movesOn = fwdQuick > ConvoyMovesOn;
        if (saidBack && movesOn && !c.Disputed) ConvoyLog($"the region's word is backward at {-fwdSaid:0.0} m/s, its moves forward at {fwdQuick:0.0}: not taken for backing");
        c.Disputed = saidBack && movesOn;
        bool backing = saidBack && !movesOn;
        if (backing != c.Backing)
        {
            c.Backing = backing;
            string moves = fwdQuick is { } fm ? $" (by its moves {fm:0.0})" : "";
            ConvoyLog(backing ? $"going backward at {-fwdSaid:0.0} m/s{moves}: Up to stop it" : $"no longer going backward, after {now - c.BackingSince} ms");
            c.BackingSince = backing ? now : 0;
            if (!backing) c.BackedAt = now;
        }
        if (backing && now - c.BackingSince > ConvoyBackingFor)
        {
            EndConvoy($"{b.SeatName ?? "The vehicle"} is going backward and Up didn't stop it, so I've let go of the keys.");
            return;
        }
        bool slower = !backing && fwdLow > ConvoyBrakeFloor && now - c.BackedAt > ConvoyAfterBacking
            && (closing > 1f && dist < c.Gap + room * 0.6f || fwd > most + 2f);
        // a press, then a wait for its news: longer the slower it goes. A Down only once the last one's effect is in, and only
        // as long as what one takes off leaves a margin (held back: it coasts with Up let go)
        long wait = MathF.Abs(fwdLow) > 6f ? 250 : MathF.Abs(fwdLow) > 3f ? 350 : 450;
        bool press = now - c.PressAt >= wait;
        bool due = slower && press && c.KickAt == 0;
        c.Back = due && ConvoyMayPress(c, fwdLow);
        if (due && !c.Back) c.HeldBack++;
        c.Fwd = !backing && dist > c.Gap + roomUp && fwdHigh < most;
        if (c.Back) ConvoyBrake(c, now, fwdSaid, fwdQuick);
        else if (backing && press) ConvoyPress(c, up: true, now);
        (c.LastMe, c.LastMeAt, c.Heading, c.LastPace) = (me, now, drive, fwd);
        bool steering = !backing && !c.Disputed && (c.Fwd || speed > 0.5f || ground > 0.5f);
        // the keys held for a share of each cycle, by how far off it is: a held key turns as far as the vehicle's script likes
        bool turning = steering && MathF.Abs(err) > ConvoyDeadband
            && now % ConvoySteerCycle < ConvoySteerCycle * Math.Clamp(MathF.Abs(err) / 0.5f, 0.3f, 1f);
        c.Left = turning && err > 0;
        c.Right = turning && err < 0;
        if (c.Left || c.Right) c.SteeredAt = now;

        // (not while the keys read as untaken: that has its own end, and a vehicle not driven doesn't move). By its moves
        // over 2 s where it has them: against a table on 2026-11-11 the word read 0.5 to 0.8 m/s now and then, each
        // starting the 6 s again, and Up was held 34 s
        if (c.Fwd && (SeatSpeed(2000) ?? speed) < 0.3f && c.KeysLostSince == 0)
        {
            if (c.StuckSince == 0) c.StuckSince = now;
            else if (now - c.StuckSince > ConvoyStuckAfter) { EndConvoy($"{b.SeatName ?? "The vehicle"} isn't moving, so I've let go of the keys."); return; }
        }
        else c.StuckSince = 0;

        if (_set.WalkText("follow") && !guessing)
        {
            if (close && ground < 0.5f && !c.ToldWith) { c.ToldWith = true; Line($"With {c.Name}, {Bearings.Metres(dist)} away."); }
            if (dist > c.Gap * 2) c.ToldWith = false;
            if (dist > ConvoyFar && !c.ToldFar) { c.ToldFar = true; Line($"{c.Name} is getting away, {Bearings.Metres(dist)} off."); }
            if (dist < ConvoyFar / 2) c.ToldFar = false;
        }
        if (now - c.LogAt > 2000)
        {
            string keys = $"{(c.Fwd ? "up " : "")}{(c.Left ? "left " : "")}{(c.Right ? "right" : "")}".TrimEnd();
            string presses = (c.DownPresses > 0 ? $", {c.DownPresses} of Down" : "") + (c.UpPresses > 0 ? $", {c.UpPresses} of Up" : "")
                + (c.HeldBack > 0 ? $", Down held back {c.HeldBack * ConvoyEvery / 1000.0:0.0} s (one would take {l.Kick:0.0} m/s off)" : "");
            string seen = guessing ? "where they'd be by now (not seen)" : $"{c.Name}, by {c.SeenHow} in {c.SeenIn?.Name}";
            string moves = fwdMoved is { } fm ? $", by its moves {MathF.Abs(fm):0.0}{(fm < -0.3f ? " backward" : "")}" : "";
            var state = $"{dist:0.0} m from {seen} (their pace {c.Pace:0.0} m/s) at {speed:0.0} m/s{(fwdSaid < -0.3f ? " backward" : "")}{moves}, in {b.RegionName} {b.Pos.X:0},{b.Pos.Y:0},"
                + $" aim {err * 180 / MathF.PI:0} degrees off ({Vector2.Distance(aim, me):0} m on), {c.Way.Count} points ahead, keys {(keys.Length > 0 ? keys : "none")}{presses}";
            // (every 2 s while anything changes; waiting still behind them, every 15 s)
            if (state != c.Logged || now - c.LogAt > 15_000) { ConvoyLog(state); c.Logged = state; c.LogAt = now; c.DownPresses = c.UpPresses = c.HeldBack = 0; }
        }
    }

    /// <summary>The one driven after, where a convoy goes by: as <see cref="See"/>, but a copy of them that has been a
    /// while in a region whose list leaves them out doesn't count, since that region says they're gone from it (a copy
    /// left frozen where they crossed out, until it's dropped as a ghost, would have the vehicle drive to the border and
    /// stop there). One just come into such a region does: the handover that brought it can take a few seconds.</summary>
    Sighting? ConvoySees(Convoy c, long now)
    {
        if (now >= _blindFrom && now < _blindUntil) return null;
        if (See(c.Id) is not { Rank: < 4 } s) return null;
        return now >= _farFrom && now < _farUntil ? s with { Pos = s.Pos + new Vector3(150, 0, 0) } : s;
    }

    /// <summary>The room it takes to come down to their pace, closing on them this fast: ConvoyBraking's slowing, and
    /// 0.3 s before it starts.</summary>
    static float Room(float closing) => closing * closing / (2 * ConvoyBraking) + closing * 0.3f;

    /// <summary>How far a sighting is from where they were, when that's further than they could have gone since (a
    /// teleport, or a crossing's moment putting them where they aren't); null when it's where they could be.</summary>
    static float? ConvoyTooFar(Convoy c, Sighting s, uint gx, uint gy, long now)
    {
        var at = new Vector2(gx + s.Pos.X, gy + s.Pos.Y);
        if (c.Lost is { } lost)
        {
            float off = Vector2.Distance(lost.From, at);
            return off > ConvoyJump + MathF.Max(lost.Pace, 5f) * (now - lost.At) / 1000f ? off : null;
        }
        return c.Last is { } last && Vector2.Distance(last, at) > ConvoyJump ? Vector2.Distance(last, at) : null;
    }

    /// <summary>The seat not sent for longer than a crossing's moment usually takes (into Rivula on 2026-11-11 it was 6 s,
    /// and nothing was decided meanwhile while her scooter ran on 70 m, past her partner, who had stopped just over the
    /// border): where she is by the map list of the region she's in, which names her in whole metres (about once a second
    /// on Second Life; seconds apart on the local grid; whether Second Life's has a seated avatar's real place mid-handover
    /// is unknown, so a place implausibly far from where she'd be by now isn't taken), else where she was last known,
    /// carried on the way she was driving at her pace: by the lists once two places 0.7 s or more apart show it, the one
    /// before the crossing until then. Within the gap and the room to slow, or past them, Up is let go. A Down only
    /// against where they're seen now, at a pace sure to be over ConvoyBrakeFloor (the lists' pace less what their whole
    /// metres could make of it), going faster than them by more than 1 m/s within the room or past them, where what a
    /// press takes off leaves the margin at that pace (ConvoyMayPress), and once for each new pace by the lists (the one from before the
    /// crossing counting once), so each one's effect is in the next before another. The wheel stays straight: which way
    /// the seat faces isn't known till it's sent.</summary>
    void ConvoyByMap(Convoy c, Body b, long now)
    {
        if (S.Region is not { } w || S.Client is not { } cl || c.LastMe is not { } last) return;
        var (from, since) = c.MapMe is { } m0 ? (m0, c.MapAt) : (last, c.LastMeAt);
        // (a list from before the crossing, as a neighbour's, doesn't name her)
        bool fresh = false, first = !c.MapTold;
        if (w.CoarseAt > c.CrossedAt && w.CoarseAt != c.MapAt && w.CoarseAt != c.MapOffAt && w.Coarse.TryGetValue(cl.Self.AgentID, out var mine))
        {
            Utils.LongToUInts(w.Handle, out var wx, out var wy);
            var at = new Vector2(wx + mine.X, wy + mine.Y);
            // (further from where she'd be by now than she could be: a seated avatar's place mid-handover, or her own teleport)
            float secs = (w.CoarseAt - since) / 1000f;
            float off = Vector2.Distance(at, from + c.Heading * MathF.Max(0, c.MapPace) * MathF.Min(3f, secs));
            if (off > ConvoyListOff + ConvoyListOffPace * secs)
            {
                c.MapOffAt = w.CoarseAt;
                ConvoyLog($"{w.Name}'s map has me {off:0} m from where I'd be by now: not taken");
            }
            else
            {
                if (c.MapMe is { } before) (c.MapFrom, c.MapFromAt) = (before, c.MapAt);
                (c.MapMe, c.MapAt, fresh) = (at, w.CoarseAt, true);
                // (a list under 0.7 s after the one before keeps the pace the lists last gave: the one from before the crossing
                // could press Down again at a pace she no longer has)
                if (c.MapFrom is { } mf && c.MapAt - c.MapFromAt >= 700)
                    (c.MapPace, c.MapPaceAt, c.MapPaceErr) = (Vector2.Dot(at - mf, c.Heading) * 1000f / (c.MapAt - c.MapFromAt), c.MapAt,
                        ConvoyListError * 1000f / (c.MapAt - c.MapFromAt));
            }
        }
        bool byList = c.MapPaceAt > 0;
        float pace = c.MapPace, paceLow = pace - c.MapPaceErr;
        (from, since) = c.MapMe is { } m ? (m, c.MapAt) : (last, c.LastMeAt);
        var me = from + c.Heading * MathF.Max(0, pace) * MathF.Min(3f, (now - since) / 1000f);
        c.MapTold = true;
        Utils.LongToUInts(b.Region, out var gx, out var gy);
        Vector2 them;
        bool current = false;
        if (ConvoySees(c, now) is { } s && ConvoyTooFar(c, s, gx, gy, now) == null)
        {
            them = new Vector2(gx + s.Pos.X, gy + s.Pos.Y);
            current = true;
            if (c.PaceFrom is not { } pf) (c.PaceFrom, c.PaceAt) = (them, now);
            else if (now - c.PaceAt >= 500) (c.Pace, c.PaceFrom, c.PaceAt) = (Vector2.Distance(pf, them) * 1000f / (now - c.PaceAt), them, now);
        }
        // (lost before her crossing: where the search has them by now, as it drives on there; their last place is behind)
        else if (c.Lost is { } lost) them = lost.From + lost.Dir * MathF.Min(lost.Length, lost.Pace * (now - lost.At) / 1000f);
        else if (c.Seen is { } seen) them = seen;
        else return;
        float dist = Vector2.Distance(them, me), closing = MathF.Max(0f, pace - c.Pace), room = Room(closing);
        // (past them: behind her the way she was going, by more than the list's metre)
        bool past = Vector2.Dot(them - me, c.Heading) < -1f;
        if (past || dist <= c.Gap + room) c.Fwd = false;
        long word = c.MapPaceAt;
        bool brake = current && paceLow > ConvoyBrakeFloor && ConvoyMayPress(c, paceLow) && (past || paceLow - c.Pace > 1f && dist < c.Gap + room * 0.6f)
            && c.MapPressedFor != word;
        if (brake) { c.MapPressedFor = word; ConvoyPress(c, up: false, now); }
        if (fresh || brake || first)
            ConvoyLog($"the seat not sent for {(now - c.DoubtSince) / 1000.0:0.0} s: by {(c.MapMe != null ? $"{w.Name}'s map" : "where I was")} {dist:0} m from {c.Name}"
                + $"{(current ? "" : " (where they were)")}{(past ? ", past them" : "")}, going {pace:0.0} m/s{(byList ? $" (at least {paceLow:0.0})" : " (as before the crossing)")};"
                + $" keys {(c.Fwd ? "up" : "none")}{(brake ? ", a Down" : "")}");
    }

    /// <summary>The way they're heading, from where they've been over the last 6 m or so (else what their copy last
    /// reported); null when not known or standing.</summary>
    static Vector2? ConvoyHeading(Convoy c)
    {
        if (c.Lost is { } lost) return lost.Dir == Vector2.Zero ? null : lost.Dir;
        if (c.Trail.Count >= 2)
        {
            var last = c.Trail[^1];
            Vector2 from = c.Trail[0];
            for (int i = c.Trail.Count - 2; i >= 0; i--) if (Vector2.Distance(c.Trail[i], last) >= 6f) { from = c.Trail[i]; break; }
            if (Vector2.Distance(from, last) >= 2f) return Vector2.Normalize(last - from);
        }
        return c.SeenVel is { } v && v.Length() >= 0.5f ? Vector2.Normalize(v) : null;
    }

    /// <summary>They've gone from sight: from where they were last seen, on the way they were heading at their pace,
    /// across the border if that's where they were going. Logged with what each region in view had of them.</summary>
    Search LoseSight(Convoy c, Body b, long now)
    {
        var from = c.Seen!.Value;
        var dir = ConvoyHeading(c) ?? Vector2.Zero;
        // a copy left where they crossed out keeps the speed it last reported, while where it is stops changing
        float reported = c.SeenVel?.Length() ?? 0;
        float pace = reported >= 1f ? MathF.Max(reported, c.Pace) : c.Pace;
        pace = dir == Vector2.Zero || pace < 1f ? 0 : MathF.Min(pace, ConvoySearchPace);
        float ahead = float.PositiveInfinity, behind = float.PositiveInfinity;
        string where = "";
        if (c.SeenIn is { } w)
        {
            Utils.LongToUInts(w.Handle, out var wx, out var wy);
            where = $" in {w.Name} {from.X - wx:0},{from.Y - wy:0}";
            if (dir != Vector2.Zero)
            {
                ahead = ToEdge(from, dir, wx, wy, w.SizeX, w.SizeY);
                behind = ToEdge(from, -dir, wx, wy, w.SizeX, w.SizeY);
            }
        }
        var lost = new Search
        {
            // (how high: a copy's, else hers, on the same road; the map's heights come in 4 m steps)
            At = c.SeenAt, From = from, Dir = dir, Z = c.SeenHow == "copy" ? c.SeenZ : b.Pos.Z, Pace = pace, Id = ++_searches,
            Border = ahead <= ConvoyBorderAhead || behind <= ConvoyBorderBehind,
            Length = pace == 0 ? 0 : ahead <= ConvoyBorderAhead ? ahead + ConvoyPastBorder : ConvoyPastPoint,
        };
        string heading = dir == Vector2.Zero ? "heading not known" : $"heading {Bearings.Point(Bearings.Compass(0, 0, dir.X, dir.Y))}";
        ConvoyLog($"lost sight of {c.Name}: last seen {(now - c.SeenAt) / 1000.0:0.0} s ago{where}, by {c.SeenHow}, {heading} at {pace:0.0} m/s"
            + $"{(float.IsFinite(ahead) ? $", the border {ahead:0} m on" : "")}; {InView(c.Id)}");
        ConvoyLog(lost.Length > 0 ? $"going on {lost.Length:0} m past where they were last seen{(ahead <= ConvoyBorderAhead ? $", {ConvoyPastBorder:0} m over the border" : "")}"
            : "going to where they were last seen");
        if (lost.Length > 0) _ = CheckWayOnAsync(lost, b);
        return lost;
    }

    /// <summary>How far from a point along a direction to the edge of a region (its corner and size in the grid's metres).</summary>
    static float ToEdge(Vector2 p, Vector2 d, uint x0, uint y0, uint sx, uint sy)
    {
        float t = float.PositiveInfinity;
        if (d.X > 1e-4f) t = MathF.Min(t, (x0 + sx - p.X) / d.X);
        else if (d.X < -1e-4f) t = MathF.Min(t, (x0 - p.X) / d.X);
        if (d.Y > 1e-4f) t = MathF.Min(t, (y0 + sy - p.Y) / d.Y);
        else if (d.Y < -1e-4f) t = MathF.Min(t, (y0 - p.Y) / d.Y);
        return MathF.Max(0, t);
    }

    /// <summary>The way on stops short of open water or a drop: a ray down every 4 m along it (the collision model, which
    /// has the regions around), the floor the higher of what it meets and the ground, wet below that region's water. A
    /// point in no region in view, or with nothing known under it, tells nothing and stops nothing.</summary>
    async Task CheckWayOnAsync(Search lost, Body b)
    {
        if (!_rays.Ready) return;
        Utils.LongToUInts(b.Region, out var gx, out var gy);
        float top = lost.Z + 12f;
        var points = new List<Vector2>();
        var rays = new List<(Vector3, Vector3, float)>();
        for (float k = 4f; k <= lost.Length; k += 4f)
        {
            var g = lost.From + lost.Dir * k;
            points.Add(g);
            rays.Add((new Vector3(g.X - gx, g.Y - gy, top), -Vector3.UnitZ, 60f));
        }
        if (points.Count == 0) return;
        Sojourn.Session.Collision.RayBatch? got;
        try { got = await _rays.CollideManyAsync(rays, b.Linkset); }
        catch (Exception) { return; }
        if (got is not { } batch || batch.Region != b.Region || _convoy?.Lost is not { } current || current.Id != lost.Id) return;
        float? before = lost.Z - 1f;   // about where they stood or sat
        for (int i = 0; i < points.Count; i++)
        {
            if (RegionAt(points[i]) is not { } w) { before = null; continue; }   // not in view: nothing to tell
            Utils.LongToUInts(w.Handle, out var wx, out var wy);
            float ground = w.TerrainHeight(points[i].X - wx, points[i].Y - wy) ?? float.NegativeInfinity;
            float prim = batch.Hits[i] is { } h ? top - h.Distance : float.NegativeInfinity;
            float floor = MathF.Max(ground, prim);
            if (!float.IsFinite(floor)) { before = null; continue; }
            // (water only when they were above it: under the sea, wet is the way)
            string? why = w.Simulator is { } sim && sim.WaterHeight > floor + 0.3f && sim.WaterHeight < lost.Z ? "water"
                : before is { } f && f - floor > 3f ? "a drop" : null;
            before = floor;
            if (why == null) continue;
            float keep = MathF.Max(0, 4f * (i + 1) - 6f);
            if (keep >= lost.Length) return;
            lost.Length = keep;
            lost.Stop = why;
            ConvoyLog($"the way on meets {why} {4f * (i + 1):0} m past where they were last seen: going no further than {keep:0} m past it");
            return;
        }
    }

    /// <summary>The region in view a point (the grid's metres) is in.</summary>
    RegionWorld? RegionAt(Vector2 g)
    {
        foreach (var w in S.VisibleRegions())
        {
            Utils.LongToUInts(w.Handle, out var wx, out var wy);
            if (g.X >= wx && g.X < wx + w.SizeX && g.Y >= wy && g.Y < wy + w.SizeY) return w;
        }
        return null;
    }

    /// <summary>For the log: what each region in view has of someone (its copy and whether the seat it's on is there, its
    /// map list and how old, a copy dropped as a ghost lately).</summary>
    string InView(UUID id)
    {
        long now = Environment.TickCount64;
        var parts = new List<string>();
        foreach (var w in S.VisibleRegions())
        {
            string list = w.CoarseAt == 0 ? "no list yet"
                : $"a list of {w.Coarse.Count} {(w.Coarse.ContainsKey(id) ? "naming them" : "without them")}, {(now - w.CoarseAt) / 1000.0:0.0} s old";
            string copy = w.AvatarById(id) is { } av ? (w.TryRegionPosition(av, out _) ? "a copy" : "a copy on a seat not here") : "no copy";
            string dropped = w.Dropped(id) is { } d ? $", a copy dropped {(now - d.At) / 1000.0:0.0} s ago ({d.Why})" : "";
            parts.Add($"{w.Name}: {copy}, {list}{dropped}");
        }
        return parts.Count == 0 ? "no regions in view" : "in view: " + string.Join("; ", parts);
    }
}
