using LibreMetaverse;
using Sojourn.Session.Audio;

namespace Sojourn.Access.Walk;

/// <summary>
/// Walk mode's sounds, made here from scratch (no recordings): footfalls by surface, a warning before a bump, an
/// opening beside you, a door, a drop or water ahead, the sonar's pings, a turn's tick. They go into world audio's
/// bank as local sounds (SoundBank.AddLocal) and play through its mixer like any world sound: centred, or placed a
/// metre from the head in the direction they're about (so the spherical-head model puts an opening on the left in the
/// left ear), at the person's level for each cue. Each step sound has three takes, a little apart in pitch and grain,
/// played in turn so a walk doesn't sound like a machine. Every cue also has a text line elsewhere (WalkMode): sound is
/// never the only channel. Since M4 also the earcons for what arrives (Sound/Earcons.cs: bells for what's addressed to
/// the person, wood for menus and groups, a soft flute for friends, a breath of noise for people coming and going, a
/// pluck and a blip for nearby chat), the beacon on where a walk is heading (Beacon.cs) and the marks for seats and
/// touchable things (O in walk mode; doors keep the knock). All play as world audio's App kind, at Sojourn Access's
/// own volume rather than the world's sounds volume. Written by Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class CueSounds
{
    public enum Cue
    {
        StepGround, StepWood, StepStone, StepMetal, StepGlass, StepSoft, StepWater,
        Wall, Blocked, Opening, Door, Edge, Water, PingNear, PingMid, PingFar, PingOpen, Gap, Turn, ModeOn, ModeOff,
        // M4 (appended: a cue's id in the bank is its number)
        EarNearby, EarObject, EarIm, EarGroup, EarMention, EarMenu, EarOffer, EarFriendOn, EarFriendOff, EarArrive, EarLeave,
        EarNotice, EarSent, Beacon, BeaconAhead, BeaconReached, MarkSeat, MarkTouch,
        // push-to-talk's mic opening and closing
        MicOn, MicOff,
    }

    const int Rate = 48000;
    const int Takes = 3;
    readonly AudioWiring? _audio;
    readonly Func<UUID> _self;
    readonly Dictionary<(Cue, int), UUID> _ids = new();
    readonly int[] _take = new int[Enum.GetValues<Cue>().Length];
    long _played;

    /// <summary>How many cue sounds have been sent to the mixer (for the status op and tests).</summary>
    public long Played => Interlocked.Read(ref _played);
    public bool Available => _audio != null;
    /// <summary>The last cue sent and when (Environment.TickCount64).</summary>
    public (Cue Cue, long At)? Last { get; private set; }

    /// <summary>One cue sent: when, at what level, and where it was placed relative to the person (degrees clockwise
    /// from where they face, metres from the head; null: centred).</summary>
    public readonly record struct Sent(Cue Cue, long At, float Level, double? Bearing, double? Distance);
    readonly Queue<Sent> _recent = new();
    /// <summary>The last 60 cues sent, oldest first (the status op, tests).</summary>
    public Sent[] Recent { get { lock (_recent) return _recent.ToArray(); } }
    /// <summary>For that record: a global position as (bearing clockwise from facing, distance from the head).</summary>
    public Func<Vector3d, (double Bearing, double Distance)?>? Relative { get; set; }

    public CueSounds(AudioWiring? audio, Func<UUID> self)
    {
        _audio = audio;
        _self = self;
        if (audio == null) return;
        foreach (var cue in Enum.GetValues<Cue>())
        {
            int takes = IsStep(cue) ? Takes : 1;
            for (int t = 0; t < takes; t++)
            {
                // ids of our own, stable across runs: a namespace prefix, the cue and the take
                var id = new UUID($"5a0c5e00-ca11-4a1c-9000-{(int)cue:x4}{t:x8}");
                audio.Bank.AddLocal(id, Make(cue, t), Rate);
                _ids[(cue, t)] = id;
            }
        }
    }

    static bool IsStep(Cue c) => c <= Cue.StepWater;

    /// <summary>The sound's id in the bank (WorldSounds' sources list it so).</summary>
    public UUID IdOf(Cue c) => _ids.TryGetValue((c, 0), out var id) ? id : UUID.Zero;

    /// <summary>Plays a cue at a level (0-1): centred (<paramref name="at"/> null), or at a global position.</summary>
    public void Play(Cue cue, float level, Vector3d? at)
    {
        if (_audio == null || level <= 0) return;
        int take = IsStep(cue) ? _take[(int)cue]++ % Takes : 0;
        if (!_ids.TryGetValue((cue, take), out var id)) return;
        _audio.Sounds.Trigger(id, _self(), Math.Clamp(level, 0, 1), WorldSounds.Kind.App, at);
        Interlocked.Increment(ref _played);
        long now = Environment.TickCount64;
        Last = (cue, now);
        var rel = at is { } g ? Relative?.Invoke(g) : null;
        lock (_recent)
        {
            _recent.Enqueue(new Sent(cue, now, level, rel?.Bearing, rel?.Distance));
            while (_recent.Count > 60) _recent.Dequeue();
        }
    }

    // --- making them ---------------------------------------------------------------------------------------------------

    static float[] Make(Cue cue, int take)
    {
        var rnd = new Random(1000 + (int)cue * 17 + take * 101);
        float pitch = 1f + (take - 1) * 0.06f;   // the takes a little apart
        return cue switch
        {
            Cue.StepGround => Mix(Len(0.12), Noise(rnd, 0.11, 0.004, 0.06, lowPass: 700 * pitch, amp: 0.55f), Tone(85 * pitch, 0.09, 0.002, 0.035, 0.35f)),
            Cue.StepWood => Mix(Len(0.14), Tone(150 * pitch, 0.12, 0.001, 0.045, 0.5f), Tone(430 * pitch, 0.08, 0.001, 0.025, 0.25f),
                Noise(rnd, 0.012, 0.0005, 0.004, lowPass: 4000, amp: 0.3f)),
            Cue.StepStone => Mix(Len(0.08), Noise(rnd, 0.05, 0.0005, 0.012, highPass: 1800 * pitch, amp: 0.55f), Tone(760 * pitch, 0.04, 0.0005, 0.012, 0.2f)),
            Cue.StepMetal => Mix(Len(0.32), Tone(920 * pitch, 0.3, 0.001, 0.09, 0.16f), Tone(2310 * pitch, 0.25, 0.001, 0.06, 0.1f),
                Tone(3720 * pitch, 0.15, 0.001, 0.035, 0.07f), Noise(rnd, 0.01, 0.0005, 0.003, highPass: 2000, amp: 0.3f)),
            Cue.StepGlass => Mix(Len(0.16), Tone(2650 * pitch, 0.14, 0.0005, 0.04, 0.16f), Tone(4150 * pitch, 0.1, 0.0005, 0.025, 0.1f),
                Noise(rnd, 0.008, 0.0005, 0.002, highPass: 3000, amp: 0.25f)),
            Cue.StepSoft => Mix(Len(0.1), Noise(rnd, 0.08, 0.004, 0.03, lowPass: 380 * pitch, amp: 0.6f), Tone(105 * pitch, 0.08, 0.003, 0.03, 0.35f)),
            Cue.StepWater => Splash(rnd, pitch),
            Cue.Wall => Mix(Len(0.2), At(0, Tone(1000, 0.07, 0.004, 0.03, 0.4f)), At(0.09, Tone(750, 0.08, 0.004, 0.035, 0.4f))),
            Cue.Blocked => Mix(Len(0.16), Tone(110, 0.15, 0.002, 0.05, 0.5f), Noise(rnd, 0.06, 0.001, 0.02, lowPass: 500, amp: 0.4f)),
            Cue.Opening => Mix(Len(0.22), At(0, Tone(660, 0.09, 0.008, 0.05, 0.3f)), At(0.09, Tone(990, 0.12, 0.008, 0.06, 0.3f))),
            Cue.Door => Mix(Len(0.3), At(0, Knock(rnd)), At(0.14, Knock(rnd))),
            Cue.Edge => Glide(700, 250, 0.28, 0.4f),
            Cue.Water => Mix(Len(0.4), At(0, Glide(300, 620, 0.09, 0.3f)), At(0.12, Glide(320, 680, 0.09, 0.3f)), At(0.24, Glide(290, 600, 0.1, 0.3f))),
            Cue.PingNear => Tone(1320, 0.07, 0.002, 0.022, 0.35f),
            Cue.PingMid => Tone(880, 0.08, 0.002, 0.028, 0.35f),
            Cue.PingFar => Tone(587, 0.09, 0.002, 0.034, 0.35f),
            Cue.PingOpen => Noise(rnd, 0.08, 0.02, 0.04, lowPass: 1500, amp: 0.18f),
            Cue.Gap => Mix(Len(0.3), At(0, Tone(784, 0.1, 0.006, 0.05, 0.3f)), At(0.08, Tone(1046, 0.1, 0.006, 0.05, 0.3f)), At(0.16, Tone(1318, 0.12, 0.006, 0.06, 0.3f))),
            Cue.Turn => Noise(rnd, 0.012, 0.0005, 0.003, highPass: 2500, amp: 0.25f),
            Cue.ModeOn => Mix(Len(0.24), At(0, Tone(523, 0.1, 0.006, 0.05, 0.3f)), At(0.1, Tone(784, 0.12, 0.006, 0.06, 0.3f))),
            Cue.ModeOff => Mix(Len(0.24), At(0, Tone(784, 0.1, 0.006, 0.05, 0.3f)), At(0.1, Tone(523, 0.12, 0.006, 0.06, 0.3f))),
            // earcons: what's addressed to the person rings a bell, groups and menus are wood
            Cue.EarNearby => Marimba(392, 0.12, 0.05, 0.35f),
            Cue.EarObject => Mix(Len(0.07), Tone(1175, 0.06, 0.001, 0.015, 0.22f), Tone(3525, 0.04, 0.001, 0.008, 0.06f)),
            Cue.EarIm => Mix(Len(0.45), At(0, Bell(587, 0.3, 0.09, 0.28f)), At(0.1, Bell(988, 0.34, 0.1, 0.28f))),
            Cue.EarGroup => Mix(Len(0.3), At(0, Marimba(698, 0.14, 0.05, 0.3f)), At(0.09, Marimba(587, 0.18, 0.06, 0.3f))),
            Cue.EarMention => Mix(Len(0.5), At(0, Bell(587, 0.14, 0.05, 0.26f)), At(0.08, Bell(988, 0.14, 0.05, 0.26f)),
                At(0.16, Bell(587, 0.14, 0.05, 0.26f)), At(0.24, Bell(988, 0.25, 0.08, 0.26f))),
            Cue.EarMenu => Mix(Len(0.38), At(0, Marimba(440, 0.1, 0.04, 0.32f)), At(0.1, Marimba(440, 0.1, 0.04, 0.32f)),
                At(0.2, Marimba(659, 0.18, 0.07, 0.34f))),
            Cue.EarOffer => Mix(Len(0.42), At(0, Bell(784, 0.2, 0.07, 0.24f)), At(0.07, Bell(659, 0.2, 0.07, 0.24f)),
                At(0.14, Bell(523, 0.28, 0.09, 0.24f))),
            Cue.EarFriendOn => Mix(Len(0.34), At(0, Flute(392, 0.15, 0.22f)), At(0.14, Flute(523, 0.2, 0.22f))),
            Cue.EarFriendOff => Mix(Len(0.34), At(0, Flute(523, 0.15, 0.22f)), At(0.14, Flute(392, 0.2, 0.22f))),
            Cue.EarArrive => Swell(rnd, 0.3, rising: true),
            Cue.EarLeave => Swell(rnd, 0.3, rising: false),
            Cue.EarNotice => Bell(880, 0.4, 0.12, 0.24f),
            Cue.EarSent => Marimba(1318, 0.05, 0.012, 0.15f),
            // the beacon: a soft pip, with a fifth above it while it's straight ahead; a small flourish on reaching it
            Cue.Beacon => Mix(Len(0.17), Tone(698, 0.16, 0.005, 0.06, 0.3f), Tone(1396, 0.1, 0.005, 0.03, 0.06f)),
            Cue.BeaconAhead => Mix(Len(0.17), Tone(698, 0.16, 0.005, 0.06, 0.26f), Tone(1046, 0.16, 0.005, 0.06, 0.2f),
                Tone(1396, 0.1, 0.005, 0.03, 0.06f)),
            Cue.BeaconReached => Mix(Len(0.45), At(0, Bell(698, 0.3, 0.1, 0.2f)), At(0, Bell(1046, 0.3, 0.1, 0.16f)), At(0.1, Bell(1396, 0.33, 0.1, 0.18f))),
            // marks: a seat sinks (two low notes falling), a touchable thing taps
            Cue.MarkSeat => Mix(Len(0.3), At(0, Marimba(262, 0.14, 0.06, 0.4f, bright: false)), At(0.1, Marimba(196, 0.2, 0.08, 0.4f, bright: false))),
            Cue.MarkTouch => Bell(1568, 0.1, 0.03, 0.22f),
            // the mic: a short click, higher opening than closing (as a radio's key)
            Cue.MicOn => Mix(Len(0.04), Tone(1760, 0.035, 0.0005, 0.007, 0.28f), Noise(rnd, 0.006, 0.0002, 0.0015, highPass: 3000, amp: 0.2f)),
            Cue.MicOff => Mix(Len(0.04), Tone(1175, 0.035, 0.0005, 0.007, 0.28f), Noise(rnd, 0.006, 0.0002, 0.0015, highPass: 2000, amp: 0.2f)),
            _ => Tone(440, 0.05, 0.002, 0.02, 0.2f),
        };
    }

    static int N(double secs) => Math.Max(1, (int)(secs * Rate));
    static float[] Len(double secs) => new float[N(secs)];

    /// <summary>A sine with a quick attack and an exponential decay (time constant <paramref name="decay"/>).</summary>
    static float[] Tone(double hz, double secs, double attack, double decay, float amp)
    {
        var o = new float[N(secs)];
        for (int i = 0; i < o.Length; i++)
        {
            double t = i / (double)Rate;
            o[i] = (float)(amp * Env(t, secs, attack, decay) * Math.Sin(2 * Math.PI * hz * t));
        }
        return o;
    }

    /// <summary>A sine sliding from one pitch to another (log), fading in and out.</summary>
    static float[] Glide(double from, double to, double secs, float amp)
    {
        var o = new float[N(secs)];
        double phase = 0;
        for (int i = 0; i < o.Length; i++)
        {
            double t = i / (double)Rate, f = from * Math.Pow(to / from, t / secs);
            phase += 2 * Math.PI * f / Rate;
            double env = Math.Min(1, t / 0.01) * Math.Min(1, (secs - t) / 0.04);
            o[i] = (float)(amp * env * Math.Sin(phase));
        }
        return o;
    }

    /// <summary>A burst of noise, one-pole filtered (low pass, high pass, or both: a band).</summary>
    static float[] Noise(Random rnd, double secs, double attack, double decay, double lowPass = 0, double highPass = 0, float amp = 0.5f)
    {
        var o = new float[N(secs)];
        double lp = 0, hpIn = 0, hpOut = 0;
        double a = lowPass > 0 ? 1 - Math.Exp(-2 * Math.PI * lowPass / Rate) : 1;
        double rc = highPass > 0 ? 1 / (2 * Math.PI * highPass) : 0, dt = 1.0 / Rate, b = highPass > 0 ? rc / (rc + dt) : 0;
        float peak = 1e-6f;
        for (int i = 0; i < o.Length; i++)
        {
            double x = rnd.NextDouble() * 2 - 1;
            lp += a * (x - lp);
            double y = lp;
            if (highPass > 0) { hpOut = b * (hpOut + y - hpIn); hpIn = y; y = hpOut; }
            o[i] = (float)(y * Env(i / (double)Rate, secs, attack, decay));
            peak = Math.Max(peak, Math.Abs(o[i]));
        }
        for (int i = 0; i < o.Length; i++) o[i] *= amp / peak;
        return o;
    }

    static double Env(double t, double secs, double attack, double decay)
    {
        double up = attack > 0 ? Math.Min(1, t / attack) : 1;
        double tail = Math.Min(1, (secs - t) / 0.005);   // no click at the end
        return up * Math.Exp(-Math.Max(0, t - attack) / decay) * Math.Max(0, tail);
    }

    /// <summary>A soft bell: the note, with an octave and a higher partial above it dying sooner.</summary>
    static float[] Bell(double hz, double secs, double decay, float amp) => Mix(Len(secs), Tone(hz, secs, 0.002, decay, amp),
        Tone(hz * 2, secs, 0.002, decay * 0.45, amp * 0.3f), Tone(hz * 2.76, secs * 0.6, 0.002, decay * 0.22, amp * 0.12f));

    /// <summary>A wooden bar: the note with a quick fourth-harmonic knock at its start (none when not bright).</summary>
    static float[] Marimba(double hz, double secs, double decay, float amp, bool bright = true) => bright
        ? Mix(Len(secs), Tone(hz, secs, 0.001, decay, amp), Tone(hz * 4, secs * 0.4, 0.0005, 0.008, amp * 0.25f))
        : Tone(hz, secs, 0.002, decay, amp);

    /// <summary>A breathy note: a slow attack, a slight vibrato, a gentle fade.</summary>
    static float[] Flute(double hz, double secs, float amp)
    {
        var o = new float[N(secs)];
        double phase = 0;
        for (int i = 0; i < o.Length; i++)
        {
            double t = i / (double)Rate;
            phase += 2 * Math.PI * hz * (1 + 0.006 * Math.Sin(2 * Math.PI * 5.5 * t)) / Rate;
            double env = Math.Min(1, t / 0.03) * Math.Min(1, (secs - t) / 0.06);
            o[i] = (float)(amp * env * (Math.Sin(phase) + 0.12 * Math.Sin(2 * phase)));
        }
        return o;
    }

    /// <summary>A breath of filtered noise swelling in (someone arriving) or fading out (leaving).</summary>
    static float[] Swell(Random rnd, double secs, bool rising)
    {
        var o = Noise(rnd, secs, 0, 1e9, lowPass: 1100, highPass: 250, amp: 1f);
        for (int i = 0; i < o.Length; i++)
        {
            double t = i / (double)o.Length;
            double env = rising ? Math.Pow(t, 1.5) * Math.Min(1, (1 - t) / 0.12) : Math.Min(1, t / 0.08) * Math.Pow(1 - t, 1.5);
            o[i] *= (float)(0.24 * env);
        }
        return o;
    }

    static float[] Knock(Random rnd) => Mix(Len(0.1), Tone(210, 0.09, 0.0008, 0.03, 0.45f), Tone(560, 0.06, 0.0008, 0.018, 0.2f),
        Noise(rnd, 0.01, 0.0003, 0.003, lowPass: 3500, amp: 0.3f));

    static float[] Splash(Random rnd, float pitch)
    {
        // noise through a band that sweeps up, with a ragged envelope
        var o = new float[N(0.22)];
        double lpA = 0, lpB = 0;
        float peak = 1e-6f;
        for (int i = 0; i < o.Length; i++)
        {
            double t = i / (double)Rate;
            double centre = (500 + 1400 * t / 0.22) * pitch;
            double a = 1 - Math.Exp(-2 * Math.PI * centre * 1.6 / Rate), b = 1 - Math.Exp(-2 * Math.PI * centre / 1.6 / Rate);
            double x = rnd.NextDouble() * 2 - 1;
            lpA += a * (x - lpA);
            lpB += b * (x - lpB);
            double env = Math.Min(1, t / 0.01) * Math.Exp(-t / 0.08) * (0.6 + 0.4 * Math.Abs(Math.Sin(t * 90 + rnd.NextDouble() * 0.3)));
            o[i] = (float)((lpA - lpB) * env);
            peak = Math.Max(peak, Math.Abs(o[i]));
        }
        for (int i = 0; i < o.Length; i++) o[i] *= 0.5f / peak;
        return o;
    }

    /// <summary>A part placed at a time offset (for <see cref="Mix"/>).</summary>
    static (double At, float[] Pcm) At(double secs, float[] pcm) => (secs, pcm);

    static float[] Mix(float[] into, params float[][] parts) => Mix(into, parts.Select(p => (0.0, p)).ToArray());

    static float[] Mix(float[] into, params (double At, float[] Pcm)[] parts)
    {
        foreach (var (at, pcm) in parts)
        {
            int off = (int)(at * Rate);
            for (int i = 0; i < pcm.Length && off + i < into.Length; i++) into[off + i] += pcm[i];
        }
        return into;
    }
}
