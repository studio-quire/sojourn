using System.Numerics;
using LibreMetaverse;

namespace Sojourn.Render.Particles;

// Particle systems as the Second Life viewer runs them (llviewerpartsource.cpp LLViewerPartSourceScript::update,
// llviewerpartsim.cpp LLViewerPartGroup::updateParticles / LLViewerPartSim, llpartdata.h; LGPL 2.1, Linden Research,
// Inc.), recast as a function of time: every particle alive at the moment of a render is regenerated from its burst
// (seeded by source, epoch, burst and index) and moved forward to its age, so a still shot shows what a viewer that
// had been watching would show then, and successive frames of the spotlight agree with each other.

/// <summary>LLPartData flags (PSYS_PART_*_MASK).</summary>
public static class PartFlags
{
    public const uint InterpColor = 0x01, InterpScale = 0x02, Bounce = 0x04, Wind = 0x08, FollowSrc = 0x10,
        FollowVelocity = 0x20, TargetPos = 0x40, TargetLinear = 0x80, Emissive = 0x100, Beam = 0x200, Ribbon = 0x400;
}

/// <summary>LLPartSysData patterns and flags.</summary>
public static class PartPattern
{
    public const byte Drop = 0x01, Explode = 0x02, Angle = 0x04, AngleCone = 0x08, AngleConeEmpty = 0x10;
    public const uint UseNewAngle = 0x02;
}

/// <summary>Tunables, defaulting to the reference viewer (High-Ultra preset).</summary>
public sealed record ParticleSettings
{
    /// <summary>RenderMaxPartCount (featuretable High-Ultra: 4096; Ultra 8192; 0 turns particles off).</summary>
    public int MaxParticles { get; init; } = 4096;
    /// <summary>The frame rate of the viewer being imitated. Emission is per frame in the viewer (a burst rate under
    /// the frame time gives one or more bursts per frame, never fractions), so density depends on it a little.</summary>
    public double ViewerFps { get; init; } = 60;
}

/// <summary>One live particle at the moment of a render (region frame of the view, i.e. offsets applied).</summary>
public struct Particle
{
    public Vector3 Pos, Vel, Axis;
    public Vector4 Color;         // LLColor4 (sRGB-space values; clamped when packed)
    public Vector2 Scale;
    public float Glow;            // 0..1, quantized like the viewer's U8
    public uint Flags;
    public int Texture;           // index into the frame's texture list
    public byte BlendSrc, BlendDst;
    public int Source;            // source index in this frame
    public long Order;            // emission order within its source (ribbon chains)
    public Vector4 StartColor;    // ribbon ends without a parent use the start colour and glow
    public float StartGlow, StartScaleX;
}

/// <summary>Where a source is and what it points at, for one frame.</summary>
public struct SourceFrame
{
    public ParticleSource Source;
    public Vector3 Pos;           // current position (view's frame)
    public Quaternion Rot;
    public Vector3 Offset;        // region -> view frame offset (pose history is in region frame)
    public Vector3 Target;        // target position (the source itself without a target)
    public Vector3 Wind;          // region wind at the source (m/s, viewer-scaled)
    public Func<Vector3, Vector3>? WindAt;   // region wind at a view-frame position
}

public static class ParticleSim
{
    const float Gravity = -9.80665f;   // unused by scripts (accel is explicit) but kept for clarity
    const double MaxWindow = 60;       // never look further back than this, whatever a packed max age says

    /// <summary>The viewer's burst schedule at a fixed frame time: bursts happen on frames 0, m, 2m, ...; frame 0
    /// gives <c>First</c> bursts (first_run forces one), later ones <c>N</c>.</summary>
    public readonly record struct Schedule(int M, int N, int First, double Fdt)
    {
        public double BurstsPerSecond => N / (M * Fdt);
    }

    public static Schedule ScheduleFor(float burstRate, double fdt)
    {
        double r = Math.Max(0.01, burstRate);   // LLPartSysData::unpackSystem clamps to 0.01
        // frames until the accumulated time exceeds the rate (the viewer's `dt_update > mBurstRate`)
        int m = 1;
        while (m * fdt <= r + 1e-9 && m < 100000) m++;
        int Count(double d, bool first)
        {
            int n = 0;
            d = Math.Min(d, Math.Max(1.0, 10.0 * r));   // max_time
            while (d > r + 1e-9 || first) { first = false; n++; d -= r; if (n > 10000) break; }
            return n;
        }
        return new Schedule(m, Count(m * fdt, false), Count(fdt, true), fdt);
    }

    /// <summary>Particles per second a source emits before throttling, and how long they live.</summary>
    public static double EmissionRate(in Primitive.ParticleSystem ps, double fdt) =>
        ScheduleFor(ps.BurstRate, fdt).BurstsPerSecond * ps.BurstPartCount;

    /// <summary>LLViewerPartSourceScript's per-camera throttle: the largest distance its particles stay a pixel wide
    /// (max_dist), and the fraction it skips (limited_rate), or null when it emits nothing for this camera.</summary>
    public static double? LimitedRate(in Primitive.ParticleSystem ps, float dist, float pixelMeterRatio, double refRate)
    {
        float maxShortSide = MathF.Max(
            MathF.Max(MathF.Min(ps.PartStartScaleX, ps.PartStartScaleY), MathF.Min(ps.PartEndScaleX, ps.PartEndScaleY)),
            MathF.Min((ps.PartStartScaleX + ps.PartEndScaleX) / 2, (ps.PartStartScaleY + ps.PartEndScaleY) / 2));
        float maxDist = maxShortSide * pixelMeterRatio;
        if (maxDist < 0.25f) return null;   // under a pixel wide at 25 cm
        double limited = 0;
        if (dist - maxDist > 0)
        {
            double travel = (dist - maxDist) * Math.Min(refRate, 1.0);
            if (travel > ps.PartMaxAge - 0.2) return null;
            limited = travel / ps.PartMaxAge;
        }
        return limited;
    }

    // --- deterministic randomness ----------------------------------------------------------------

    static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    struct Rng
    {
        ulong _s;
        public Rng(ulong seed) => _s = Mix(seed);
        public float Next() { _s = Mix(_s); return (_s >> 40) * (1f / (1 << 24)); }   // [0, 1)
        public float Next(float max) => Next() * max;
    }

    public static ulong SourceSeed(UUID id, double epoch)
    {
        var b = id.GetBytes();
        ulong a = BitConverter.ToUInt64(b, 0), c = BitConverter.ToUInt64(b, 8);
        return Mix(a ^ Mix(c ^ (ulong)BitConverter.DoubleToInt64Bits(Math.Round(epoch, 3))));
    }

    // --- generation -------------------------------------------------------------------------------

    /// <summary>Per-frame state shared by every source of a view.</summary>
    public sealed class FrameContext
    {
        public double Now;
        public double Fdt;
        public Vector3 Camera;
        public float PixelMeterRatio;
        public double BurstKeep = 1;     // LLViewerPartSim::sParticleBurstRate
        public double RefRate = 0.03125; // sParticleAdaptiveRate
        public long Budget = 400_000;    // candidate particles examined this frame (runaway sources)
        public readonly List<Particle> Out = new();
        public Func<UUID, int>? TextureIndex;
        /// <summary>Stepped particles' state where the last frame left them, by their seed; forgotten once not seen for
        /// a frame (<see cref="EndFrame"/>).</summary>
        public readonly Dictionary<ulong, StepState> Steps = new();
        public long Frame;
        readonly List<ulong> _stale = new();

        public void EndFrame()
        {
            foreach (var (k, st) in Steps) if (st.Frame != Frame) _stale.Add(k);
            foreach (var k in _stale) Steps.Remove(k);
            _stale.Clear();
            Frame++;
        }
    }

    public readonly record struct StepState(int Steps, Vector3 Pos, Vector3 Vel, float Last, uint Flags, long Frame);

    /// <summary>Append every particle of one source alive at ctx.Now.</summary>
    public static void Generate(in SourceFrame sf, int sourceIndex, FrameContext ctx)
    {
        var hist = sf.Source.History;
        if (hist.Count == 0) return;
        double now = ctx.Now, fdt = ctx.Fdt;
        float camDist = Vector3.Distance(sf.Pos, ctx.Camera);
        for (int e = 0; e < hist.Count; e++)
        {
            if (hist[e].Ps is not { } ps) continue;
            double since = hist[e].Since;
            double until = e + 1 < hist.Count ? hist[e + 1].Since : now;
            if (until > now) until = now;
            double epoch = hist[e].EpochStart;
            if (ps.BurstPartCount == 0 || ps.PartMaxAge <= 0) continue;
            double partAge = Math.Min(ps.PartMaxAge, MaxWindow);
            // the newest particles are a frame old (sources update before groups in a frame)
            double tbMin = Math.Max(since, now + fdt - partAge);
            if (tbMin > until) continue;
            if (LimitedRate(ps, camDist, ctx.PixelMeterRatio, ctx.RefRate) is not { } limited) continue;
            double skip = Math.Max(1.0 - ctx.BurstKeep, limited);
            if (skip >= 1.0) continue;
            var sch = ScheduleFor(ps.BurstRate, fdt);
            ulong seed = SourceSeed(sf.Source.Id, epoch);
            int tex = ctx.TextureIndex?.Invoke(ps.Texture) ?? 0;

            // burst frames f (counted from the epoch) with f*fdt in [tbMin, until]
            long fFirst = (long)Math.Ceiling((tbMin - epoch) / fdt - 1e-9);
            long fLast = (long)Math.Floor((until - epoch) / fdt + 1e-9);
            if (fFirst < 0) fFirst = 0;
            // a source with a max age stops once it has run that long (startAge counts time before we saw it)
            if (ps.MaxAge > 0)
            {
                long fDie = (long)Math.Floor((ps.MaxAge - ps.StartAge) / fdt) - sch.M - 1;
                if (fDie < fLast) fLast = fDie;
            }
            if (fLast < fFirst) continue;
            long jFirst = (fFirst + sch.M - 1) / sch.M, jLast = fLast / sch.M;
            if (since > epoch + 1e-6 && jFirst == 0) jFirst = 1;   // a parameter change: no first-run burst
            float omega = new Vector3(ps.AngularVelocity.X, ps.AngularVelocity.Y, ps.AngularVelocity.Z).Length();
            var omegaAxis = omega > 0 ? Vector3.Normalize(new Vector3(ps.AngularVelocity.X, ps.AngularVelocity.Y, ps.AngularVelocity.Z)) : Vector3.UnitZ;
            for (long j = jFirst; j <= jLast; j++)
            {
                long f = j * sch.M;
                double tb = epoch + f * fdt;
                double age = now - tb + fdt;
                if (age > partAge || age < 0) continue;
                int bursts = j == 0 ? sch.First : sch.N;
                long kBase = j == 0 ? 0 : sch.First + (j - 1) * sch.N;
                var pose = sf.Source.Poses.Count > 0 ? sf.Source.PoseAt(tb) : new PoseSample(tb, sf.Pos - sf.Offset, sf.Rot);
                var birthPos = pose.Pos + sf.Offset;
                for (int b = 0; b < bursts; b++)
                {
                    long k = kBase + b;
                    // each burst iteration turns the emitter by one frame's worth of angular velocity
                    var spin = omega > 0 ? Quaternion.CreateFromAxisAngle(omegaAxis, (float)((k + 1) * fdt * omega)) : Quaternion.Identity;
                    for (int i = 0; i < ps.BurstPartCount; i++)
                    {
                        if (--ctx.Budget < 0) return;
                        ulong key = seed ^ ((ulong)k * 0x100000001B3UL) ^ ((ulong)i << 48);
                        var rng = new Rng(key);
                        // emission order, the same from frame to frame: the burst's frame on the clock, burst, index
                        long thisOrder = ((long)Math.Round(tb / fdt) << 20) + (Math.Min(b, 4095) << 8) + i;
                        if (rng.Next() < skip) continue;
                        Emit(ps, sf, birthPos, pose.Rot, spin, age, ref rng, key, tex, sourceIndex, thisOrder, ctx);
                    }
                }
            }
        }
    }

    static void Emit(in Primitive.ParticleSystem ps, in SourceFrame sf, Vector3 srcPos, Quaternion srcRot, Quaternion spin,
        double ageD, ref Rng rng, ulong key, int tex, int sourceIndex, long order, FrameContext ctx)
    {
        uint flags = (uint)ps.PartDataFlags & 0xFFFF;
        float maxAge = ps.PartMaxAge;
        float age = (float)ageD;
        float radius = (flags & (PartFlags.FollowSrc | PartFlags.TargetLinear)) != 0 ? 0f : ps.BurstRadius;
        Vector3 p0 = srcPos, v0 = Vector3.Zero;
        byte pat = (byte)ps.Pattern;
        if ((pat & PartPattern.Drop) != 0) { }
        else if ((pat & PartPattern.Explode) != 0)
        {
            Vector3 dir; float mvs; int guard = 0;
            do
            {
                dir = new Vector3(rng.Next(2f) - 1f, rng.Next(2f) - 1f, rng.Next(2f) - 1f);
                mvs = dir.LengthSquared();
            } while ((mvs > 1f || mvs < 0.01f) && ++guard < 64);
            dir = Vector3.Normalize(dir);
            p0 += radius * dir;
            v0 = dir * (ps.BurstSpeedMin + rng.Next(ps.BurstSpeedMax - ps.BurstSpeedMin));
        }
        else if ((pat & (PartPattern.Angle | PartPattern.AngleCone)) != 0)
        {
            var dir = Vector3.UnitZ;
            float inner = ps.InnerAngle, outer = ps.OuterAngle;
            float angle = inner + rng.Next(outer - inner);
            if (rng.Next() < 0.5f) angle = -angle;
            dir = Vector3.Transform(dir, Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle));
            if ((pat & PartPattern.AngleCone) != 0)
                dir = Vector3.Transform(dir, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rng.Next(4 * MathF.PI)));
            if ((ps.PartFlags & PartPattern.UseNewAngle) == 0)
                dir = Vector3.Transform(dir, Quaternion.CreateFromAxisAngle(Vector3.UnitX, outer));   // deprecated angles
            dir = Vector3.Transform(dir, srcRot);
            dir = Vector3.Transform(dir, spin);
            p0 += radius * dir;
            v0 = dir * (ps.BurstSpeedMin + rng.Next(ps.BurstSpeedMax - ps.BurstSpeedMin));
        }

        var acc = new Vector3(ps.PartAcceleration.X, ps.PartAcceleration.Y, ps.PartAcceleration.Z);
        Vector3 pos, vel;
        bool follow = (flags & PartFlags.FollowSrc) != 0;
        if ((flags & PartFlags.TargetLinear) != 0)
        {
            float frac = age / maxAge;
            var delta = sf.Target - sf.Pos;
            pos = sf.Pos + frac * delta;
            vel = delta;
            if ((flags & PartFlags.Bounce) != 0 && pos.Z < sf.Pos.Z) pos.Z = 2 * sf.Pos.Z - pos.Z;
        }
        else if ((flags & (PartFlags.TargetPos | PartFlags.Bounce)) != 0)
        {
            (pos, vel) = Step(flags, follow ? Vector3.Zero : p0, v0, acc, age, maxAge, sf, follow, ctx, key);
            if (follow) pos += sf.Pos;
        }
        else
        {
            var start = follow ? Vector3.Zero : p0;
            if ((flags & PartFlags.Wind) != 0)
            {
                // v' = a + k (w - v), k = 0.1/s: the viewer's per-frame drag toward the wind, integrated
                const float k = 0.1f;
                var w = sf.WindAt?.Invoke(srcPos) ?? sf.Wind;
                var u = w + acc / k;
                float e = MathF.Exp(-k * age);
                vel = u + (v0 - u) * e;
                pos = start + u * age + (v0 - u) * ((1 - e) / k);
            }
            else
            {
                pos = start + v0 * age + 0.5f * acc * age * age;
                vel = v0 + acc * age;
            }
            if (follow) pos += sf.Pos;
        }

        float fracAge = age / maxAge;
        var sc = new Vector4(ps.PartStartColor.R, ps.PartStartColor.G, ps.PartStartColor.B, ps.PartStartColor.A);
        var ec = new Vector4(ps.PartEndColor.R, ps.PartEndColor.G, ps.PartEndColor.B, ps.PartEndColor.A);
        var color = (flags & PartFlags.InterpColor) != 0 ? sc * (1 - fracAge) + ec * fracAge : sc;
        var ss = new Vector2(ps.PartStartScaleX, ps.PartStartScaleY);
        var scale = (flags & PartFlags.InterpScale) != 0 ? ss * (1 - fracAge) + new Vector2(ps.PartEndScaleX, ps.PartEndScaleY) * fracAge : ss;
        float glow = MathF.Round(Math.Clamp(ps.PartStartGlow + (ps.PartEndGlow - ps.PartStartGlow) * fracAge, 0f, 1f) * 255f) / 255f;
        ctx.Out.Add(new Particle
        {
            Pos = pos, Vel = vel,
            Axis = Vector3.Transform(Vector3.UnitZ, srcRot),
            Color = color, Scale = scale, Glow = glow, Flags = flags,
            Texture = tex, BlendSrc = ps.BlendFuncSource, BlendDst = ps.BlendFuncDest,
            Source = sourceIndex, Order = order,
            StartColor = sc, StartGlow = MathF.Round(Math.Clamp(ps.PartStartGlow, 0f, 1f) * 255f) / 255f, StartScaleX = ps.PartStartScaleX,
        });
    }

    /// <summary>Frame-by-frame integration for the cases without a closed form (bounce, interpolation toward a
    /// target), exactly as updateParticles does it at the imitated frame rate. Each particle carries on from where the
    /// last frame left it (<see cref="FrameContext.Steps"/>), as the viewer's do, with the target and wind of each frame
    /// it's stepped in; from its birth only when it's new here (integrating every particle from birth every frame cost
    /// O(age) each: milliseconds a frame with a few thousand bouncing or homing particles).</summary>
    static (Vector3, Vector3) Step(uint flags, Vector3 pos, Vector3 vel, Vector3 acc, float age, float maxAge, in SourceFrame sf, bool follow,
        FrameContext ctx, ulong key)
    {
        float fdt = (float)ctx.Fdt;
        int steps = Math.Max(1, (int)MathF.Round(age / fdt));
        bool keep = steps <= 3600;
        if (!keep) { fdt = age / 3600; steps = 3600; }
        float last = 0;
        int from = 0;
        if (keep && ctx.Steps.TryGetValue(key, out var st) && st.Steps <= steps && st.Flags == flags)
            (from, pos, vel, last) = (st.Steps, st.Pos, st.Vel, st.Last);
        var srcZ = follow ? 0f : sf.Pos.Z;
        var target = follow ? sf.Target - sf.Pos : sf.Target;
        bool wind = (flags & PartFlags.Wind) != 0;
        for (int s = from; s < steps; s++)
        {
            if (wind)
            {
                var w = sf.WindAt?.Invoke(follow ? pos + sf.Pos : pos) ?? sf.Wind;
                vel *= 1f - 0.1f * fdt;
                vel += 0.1f * fdt * w;
            }
            if ((flags & PartFlags.TargetPos) != 0)
            {
                float remaining = maxAge - last;
                float step = Math.Clamp(fdt / MathF.Max(remaining, 1e-4f), 0f, 0.1f) * 5f;
                var delta = (target - pos) / MathF.Max(remaining, 1e-4f);
                vel *= 1f - step;
                vel += step * delta;
            }
            pos += fdt * vel + 0.5f * fdt * fdt * acc;
            vel += acc * fdt;
            if ((flags & PartFlags.Bounce) != 0)
            {
                float dz = pos.Z - srcZ;
                if (dz < 0) { pos.Z += -2f * dz; vel.Z *= -0.75f; }
            }
            last += fdt;
        }
        if (keep) ctx.Steps[key] = new StepState(steps, pos, vel, last, flags, ctx.Frame);
        return (pos, vel);
    }
}
