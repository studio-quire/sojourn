namespace Sojourn.Access.Walk;

/// <summary>
/// The sounds a person can give someone's IMs or a group's chat (Sound/ImSounds.cs), made here as the rest are. Each is
/// a family of its own, apart from the earcons' bells (what's addressed to you: the usual IM sound is two bells rising),
/// wood (menus, groups), flute (friends) and breath (people coming and going), and from walk mode's cues: a wave of
/// noise, high chime tubes, a bird's whistle, plucked strings, a steel pan, low thumps, an insect's pulsed trill, brass,
/// a music box's comb, jingles. None is two notes rising, so none is taken for the usual IM sound. Each under half a
/// second; their loudness matched to the usual IM sound's (K-weighted, the loudest 400 ms, within a decibel or so).
/// Written by Claude Opus 5.5, 2026-11-01.
/// </summary>
public sealed partial class CueSounds
{
    static float[]? Own(Cue cue, Random rnd) => cue switch
    {
        // a swell of surf that brightens as it breaks and draws back, its foam fizzing on after the crest
        Cue.ImWave => Gain(Wave(rnd), 0.44f),
        // five small chime tubes, high (above every bell here) and out of step, as a breeze moves them
        Cue.ImChime => Gain(Mix(Len(0.48), At(0, Tube(2349, 0.48, 1f)), At(0.06, Tube(3136, 0.42, 0.75f)), At(0.13, Tube(2093, 0.35, 0.9f)),
            At(0.19, Tube(2794, 0.29, 0.7f)), At(0.25, Tube(2637, 0.23, 0.8f))), 0.27f),
        // two quick rising chirps and a falling call
        Cue.ImBird => Gain(Mix(Len(0.42), At(0, Chirp(2500, 3900, 0.075)), At(0.13, Chirp(2700, 4100, 0.075)), At(0.27, Chirp(3700, 2500, 0.13))), 0.17f),
        // a harp's glissando up five strings
        Cue.ImHarp => Gain(Mix(Len(0.48), At(0, Pluck(rnd, 262, 0.48)), At(0.04, Pluck(rnd, 330, 0.44)), At(0.08, Pluck(rnd, 392, 0.4)),
            At(0.12, Pluck(rnd, 523, 0.36)), At(0.16, Pluck(rnd, 659, 0.32))), 0.46f),
        // a steel pan: a note struck with a grace note above it, then again, softer (not the menu's same, same, higher)
        Cue.ImSteelDrum => Gain(Mix(Len(0.48), At(0, Pan(1046, 0.03, 0.35f)), At(0.03, Pan(784, 0.19)), At(0.22, Pan(784, 0.26, 0.6f))), 0.34f),
        // a heart: lub, dub
        Cue.ImHeartbeat => Gain(Mix(Len(0.4), At(0, Thump(rnd, 190, 1f)), At(0.17, Thump(rnd, 220, 0.75f))), 0.85f),
        // a cricket: three short pulsed trills
        Cue.ImCricket => Gain(Mix(Len(0.44), At(0, Trill(4400)), At(0.16, Trill(4400)), At(0.32, Trill(4400))), 0.23f),
        // a soft horn: one fifth swelling and fading, brighter as it swells
        Cue.ImHorn => Gain(Mix(Len(0.46), Brass(262, 0.46), Brass(392, 0.46)), 0.35f),
        // a music box: four tiny notes, up and back (below the chime, above the IM's bells)
        Cue.ImMusicBox => Gain(Mix(Len(0.46), At(0, Comb(1047, 0.46)), At(0.08, Comb(1319, 0.38)), At(0.16, Comb(1568, 0.3)), At(0.28, Comb(1319, 0.18))), 0.26f),
        // sleigh bells: two shakes
        Cue.ImSleighBells => Gain(Mix(Len(0.42), At(0, Jingle(rnd, 0.2)), At(0.2, Jingle(rnd, 0.22))), 0.43f),
        _ => null,
    };

    static float[] Gain(float[] pcm, float peak)
    {
        float max = 1e-6f;
        foreach (var x in pcm) max = Math.Max(max, Math.Abs(x));
        for (int i = 0; i < pcm.Length; i++) pcm[i] *= peak / max;
        return pcm;
    }

    /// <summary>Noise through a low pass that opens as the swell rises and closes as it falls, with a hiss of foam from
    /// the crest on: a wave, apart from the radar's breath (a fixed band without highs, rising or falling, never both).</summary>
    static float[] Wave(Random rnd)
    {
        const double secs = 0.48, crest = 0.42;
        var o = new float[N(secs)];
        double lp1 = 0, lp2 = 0, hiss = 0, hpIn = 0, hpOut = 0;
        double rc = 1 / (2 * Math.PI * 140), keepHp = rc / (rc + 1.0 / Rate);
        for (int i = 0; i < o.Length; i++)
        {
            double t = i / (double)Rate, u = t / secs;
            double env = u < crest ? Math.Pow(Math.Sin(Math.PI / 2 * u / crest), 2) : Math.Pow(Math.Cos(Math.PI / 2 * (u - crest) / (1 - crest)), 2);
            double cut = 280 + 2400 * Math.Pow(env, 1.6);
            double a = 1 - Math.Exp(-2 * Math.PI * cut / Rate);
            double x = rnd.NextDouble() * 2 - 1;
            lp1 += a * (x - lp1);
            lp2 += a * (lp1 - lp2);
            hiss += 0.35 * (x - hiss);   // what's left above ~3 kHz once this low pass is taken away
            hpOut = keepHp * (hpOut + lp2 - hpIn);   // nothing under ~140 Hz: surf, not rumble
            hpIn = lp2;
            double foam = (u < crest ? Math.Pow(u / crest, 4) : Math.Exp(-(u - crest) * secs / 0.1)) * Math.Min(1, (1 - u) * secs / 0.02);
            o[i] = (float)(hpOut * env * 2.2 + (x - hiss) * foam * 0.4);
        }
        return o;
    }

    /// <summary>A chime tube: a free bar's partials (1, 2.76, 5.40), the higher dying sooner.</summary>
    static float[] Tube(double hz, double secs, float amp) => Mix(Len(secs), Tone(hz, secs, 0.001, 0.09, amp),
        Tone(hz * 2.756, secs, 0.001, 0.05, amp * 0.4f), Tone(hz * 5.404, secs * 0.5, 0.001, 0.025, amp * 0.16f));

    /// <summary>A bird's chirp: a whistle sliding between two pitches with a slight trill, swelling and fading.</summary>
    static float[] Chirp(double from, double to, double secs)
    {
        var o = new float[N(secs)];
        double phase = 0;
        for (int i = 0; i < o.Length; i++)
        {
            double t = i / (double)Rate, u = t / secs;
            double f = from * Math.Pow(to / from, Math.Sin(Math.PI / 2 * u)) * (1 + 0.02 * Math.Sin(2 * Math.PI * 38 * t));
            phase += 2 * Math.PI * f / Rate;
            o[i] = (float)(Math.Pow(Math.Sin(Math.PI * u), 1.5) * (Math.Sin(phase) + 0.08 * Math.Sin(2 * phase)));
        }
        return o;
    }

    /// <summary>A plucked string (Karplus-Strong): a loop a period long, losing its highs as it rings, started from the
    /// string's shape when plucked by a finger a fifth of the way along (a triangle: soft, warm harmonics), a breath of
    /// noise in it.</summary>
    static float[] Pluck(Random rnd, double hz, double secs, double tau = 0.3)
    {
        var o = new float[N(secs)];
        int p = Math.Max(2, (int)Math.Round(Rate / hz - 0.5));
        var loop = new double[p];
        double mean = 0;
        for (int i = 0; i < p; i++)
        {
            double u = i / (double)p;
            loop[i] = (u < 0.2 ? u / 0.2 : (1 - u) / 0.8) + 0.06 * (rnd.NextDouble() * 2 - 1);
            mean += loop[i] / p;
        }
        for (int i = 0; i < p; i++) loop[i] -= mean;
        double keep = Math.Exp(-1 / (hz * tau));
        for (int i = 0, at = 0; i < o.Length; i++)
        {
            int next = (at + 1) % p;
            double now = loop[at];
            loop[at] = keep * 0.5 * (now + loop[next]);
            double t = i / (double)Rate;
            o[i] = (float)(now * Math.Min(1, t / 0.0015) * Math.Min(1, (secs - t) / 0.01));
            at = next;
        }
        return Gain(o, 1f);
    }

    /// <summary>A steel pan's note: near-harmonic partials, the highest dying first, its pitch settling up from a
    /// little flat in the first 20 ms.</summary>
    static float[] Pan(double hz, double secs, float amp = 1f)
    {
        var o = new float[N(secs)];
        (double Ratio, double Amp, double Decay)[] partials = { (1, 1, 0.16), (2.0, 0.55, 0.09), (3.01, 0.22, 0.05), (4.2, 0.08, 0.025) };
        var phase = new double[partials.Length];
        for (int i = 0; i < o.Length; i++)
        {
            double t = i / (double)Rate, bend = 1 - 0.025 * Math.Exp(-t / 0.007);
            double y = 0;
            for (int k = 0; k < partials.Length; k++)
            {
                phase[k] += 2 * Math.PI * hz * partials[k].Ratio * bend / Rate;
                y += partials[k].Amp * Math.Exp(-t / partials[k].Decay) * Math.Sin(phase[k]);
            }
            o[i] = (float)(amp * y * Math.Min(1, t / 0.002) * Math.Min(1, (secs - t) / 0.01));
        }
        return o;
    }

    /// <summary>A soft low thump, its pitch falling as it fades, with its octave and twelfth (pitched up enough for a
    /// laptop's speakers), and a muffled touch of noise at the start.</summary>
    static float[] Thump(Random rnd, double hz, float amp)
    {
        const double secs = 0.17;
        var o = new float[N(secs)];
        double phase = 0;
        for (int i = 0; i < o.Length; i++)
        {
            double t = i / (double)Rate, f = hz * (0.6 + 0.4 * Math.Exp(-t / 0.04));
            phase += 2 * Math.PI * f / Rate;
            double env = Math.Min(1, t / 0.006) * Math.Exp(-t / 0.045) * Math.Min(1, (secs - t) / 0.01);
            o[i] = (float)(amp * env * (Math.Sin(phase) + 0.7 * Math.Sin(2 * phase) + 0.3 * Math.Sin(3 * phase)));
        }
        return Mix(o, Noise(rnd, 0.03, 0.002, 0.008, lowPass: 700, highPass: 150, amp: 0.2f * amp));
    }

    /// <summary>A cricket's trill: a high tone in five quick pulses.</summary>
    static float[] Trill(double hz)
    {
        const double secs = 0.11, pulse = 0.022;
        var o = new float[N(secs)];
        for (int i = 0; i < o.Length; i++)
        {
            double t = i / (double)Rate, inPulse = t % pulse / pulse;
            double env = inPulse < 0.6 ? Math.Pow(Math.Sin(Math.PI * inPulse / 0.6), 2) : 0;
            o[i] = (float)(env * (Math.Sin(2 * Math.PI * hz * t) + 0.15 * Math.Sin(2 * Math.PI * hz * 2 * t)));
        }
        return o;
    }

    /// <summary>A brass note: harmonics falling away more steeply when soft, so it brightens as it swells; a slow
    /// rise, a slight scoop up to pitch, a gentle fall.</summary>
    static float[] Brass(double hz, double secs)
    {
        var o = new float[N(secs)];
        var phase = new double[10];
        for (int i = 0; i < o.Length; i++)
        {
            double t = i / (double)Rate;
            double env = Math.Min(1, Math.Pow(t / 0.09, 1.5)) * Math.Min(1, Math.Pow((secs - t) / 0.2, 1.3));
            double scoop = 1 - 0.012 * Math.Exp(-t / 0.03);
            double y = 0;
            for (int n = 1; n <= phase.Length; n++)
            {
                phase[n - 1] += 2 * Math.PI * hz * n * scoop / Rate;
                y += Math.Pow(n, -(2.6 - 1.3 * env)) * Math.Sin(phase[n - 1]);
            }
            o[i] = (float)(env * y);
        }
        return o;
    }

    /// <summary>A music box's tooth: almost a pure tone, with a high tick from its sixth-and-a-quarter partial.</summary>
    static float[] Comb(double hz, double secs) => Mix(Len(secs), Tone(hz, secs, 0.0008, 0.11, 1f), Tone(hz * 6.27, secs * 0.3, 0.0005, 0.008, 0.18f));

    /// <summary>One shake of sleigh bells: a dozen small bells struck within 60 ms, each a few inharmonic high partials
    /// dying fast, over a breath of high noise.</summary>
    static float[] Jingle(Random rnd, double secs)
    {
        var o = Noise(rnd, secs, 0.004, 0.03, highPass: 5000, amp: 0.25f);
        for (int b = 0; b < 12; b++)
        {
            double at = rnd.NextDouble() * 0.06, hz = 3200 + rnd.NextDouble() * 2800;
            float amp = (float)(0.35 + rnd.NextDouble() * 0.4);
            var bell = Mix(Len(secs - at), Tone(hz, secs - at, 0.0005, 0.03, amp), Tone(hz * 1.47, secs - at, 0.0005, 0.018, amp * 0.5f),
                Tone(hz * 2.09, (secs - at) * 0.5, 0.0005, 0.01, amp * 0.3f));
            Mix(o, At(at, bell));
        }
        return o;
    }
}
