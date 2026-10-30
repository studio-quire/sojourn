using System.Numerics;
using System.Runtime.InteropServices;
using LibreMetaverse;
using Sojourn.Render.Frame;
using Sojourn.Render.World;

namespace Sojourn.Render.Particles;

/// <summary>A particle quad corner as the particle shaders read it (32 bytes; mirrors ParticleVertex in
/// Shaders/particle.vert).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ParticleVertex
{
    public Vector3 Pos;
    public Vector2 Uv;
    public uint Color;       // rgba8 (LLColor4U: the particle colour, sRGB-space)
    public uint GlowFlags;   // glow (8 bits) | flags << 8
    public int Texture;      // bindless slot

    public const uint FlagEmissive = 1, FlagNoMinAlpha = 2;
}

/// <summary>Per-quad draw info: distance from the head's centre (sorting), blend factors (LL_PART_BF_*), glow; whether it's a
/// plain quad facing the camera (not a ribbon or turned to its velocity), which a mirror turns to face its own.</summary>
public readonly record struct ParticleQuad(float Depth, byte BlendSrc, byte BlendDst, bool Glow, bool Facing = false)
{
    public int BlendKey => BlendSrc << 4 | BlendDst;
}

/// <summary>
/// Builds a view's particles: watches every region's sources, works out the viewer's global throttle for this camera,
/// regenerates the particles alive now and turns them into camera-facing quads (or ribbon segments), culled and sorted
/// far to near like LLVOPartGroup / LLParticlePartition. Render thread only.
/// </summary>
public sealed class ParticleBuilder
{
    public ParticleSettings Settings { get; set; } = new();
    /// <summary>The viewer's default particle image (pixiesmall), served from the viewer's own file.</summary>
    public static readonly UUID DefaultTexture = new("8b5fec65-8d8d-9dc5-cda8-8fdf2716e361");

    readonly ParticleSim.FrameContext _ctx = new();
    readonly List<SourceFrame> _frames = new();
    readonly List<IAssetSource> _frameAssets = new();
    readonly List<int> _texSlots = new();
    readonly Dictionary<UUID, int> _texIndex = new();
    readonly List<int> _order = new();
    public int LastParticles { get; private set; }
    public int LastSources { get; private set; }
    public double LastMs { get; private set; }

    static readonly IAssetSource LocalTextures = new LocalTextureSource();

    /// <summary>The person's safety tools (Client/Control/SafetyWiring.cs): no particles at all (the viewer's Rendering
    /// Types > Particles), and sources left out by owner and object (a blocked owner's, as LLViewerPartSim's update skips
    /// a muted one's).</summary>
    public static volatile bool Off;
    public static Func<UUID, UUID, bool>? Hidden;
    /// <summary>Particles in the last view built (any builder), for SojournSafety particles.</summary>
    public static volatile int Drawn;
    /// <summary>Asked for (CountNext), the next view's particles by the prim they came from (SojournSafety particles).</summary>
    public static volatile bool CountNext;
    public static volatile IReadOnlyDictionary<UUID, int>? Counts;
    /// <summary>Particles made elsewhere for this frame, in the view's frame (others' beams and spirals:
    /// World/Avatars.Effects.cs), drawn with these in the viewer's default particle image.</summary>
    public Func<IReadOnlyList<Particle>>? Extra { get; set; }

    /// <summary>Add this view's particles to the draw list; returns how many of their textures are still loading.</summary>
    public int Build(ViewSetup v, IReadOnlyCollection<RegionScene> all, Func<RegionScene, Vector3> offsetOf, DrawList dl, Frustum frustum,
        TextureStore textures)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        dl.ParticleVertices.Clear();
        dl.ParticleQuads.Clear();
        LastParticles = 0; LastSources = 0;
        Drawn = 0;
        if (Settings.MaxParticles <= 0 || Off) return 0;
        double now = EffectClock.Now;
        double fdt = 1.0 / Math.Max(1.0, Settings.ViewerFps);
        var cam = v.Position;
        float pmr = v.Height / (2f * MathF.Tan(v.VFov * 0.5f));   // LLViewerCamera::getPixelMeterRatio

        // every source the "viewer" knows (all regions), placed this frame
        _frames.Clear();
        _frameAssets.Clear();
        foreach (var scene in all)
        {
            scene.Effects.Observe(scene.Source.Handle, scene.Source.Prims, now);
            if (scene.Effects.Sources.Count == 0) continue;
            var offset = offsetOf(scene);
            var sim = scene.Source.HasSimulator ? scene.Source.Simulator : null;
            var hidden = Hidden;
            foreach (var src in scene.Effects.Sources.Values)
            {
                if (hidden != null && hidden(src.Owner, src.Id)) continue;
                if (scene.OnJellyDoll(src.LocalId)) continue;   // worn by a jelly doll: its attachments aren't drawn
                if (!src.Gone && scene.TryWorldTransform(src.LocalId, out var p, out var r)) src.AddPose(now, p, r);
                if (src.Poses.Count == 0) continue;
                var pose = src.Poses[^1];
                var pos = pose.Pos + offset;
                // the viewer's mTargetPosAgent: the target object's position, else the source's own
                var target = pos;
                if (src.History.LastOrDefault(e => e.Ps != null).Ps is { } tps && tps.Target != UUID.Zero && scene.FindPosition(tps.Target) is { } tp)
                    target = tp + offset;
                var wind = RegionWind.At(sim, pose.Pos);
                var off = offset;
                _frameAssets.Add(scene.Source.Assets);
                _frames.Add(new SourceFrame
                {
                    Source = src, Pos = pos, Rot = pose.Rot, Offset = offset, Target = target, Wind = wind,
                    WindAt = sim == null ? null : p2 => RegionWind.At(sim, p2 - off),
                });
            }
        }
        var extra = Extra?.Invoke();
        if (_frames.Count == 0 && extra is not { Count: > 0 }) { LastMs = Ms(t0); return 0; }

        // LLViewerPartSim's adaptive rates for this camera: total expected particles decide the burst keep rate
        // (sParticleBurstRate settles at 0.9 * max / total) and whether the distance throttle runs hot
        double max = Settings.MaxParticles;
        _ctx.Camera = cam;
        _ctx.PixelMeterRatio = pmr;
        double e1 = Expected(0.03125);
        double refRate = 0.03125, expected = e1;
        if (e1 > 0.875 * max) { refRate = 1.0; expected = Expected(1.0); }
        _ctx.Now = now;
        _ctx.Fdt = fdt;
        _ctx.RefRate = refRate;
        _ctx.BurstKeep = expected > 0.9 * max ? 0.9 * max / expected : 1.0;
        _ctx.Budget = 400_000;
        _ctx.Out.Clear();
        _texSlots.Clear();
        _texIndex.Clear();
        int pending = 0;
        int TextureIndex(UUID id)
        {
            if (id == UUID.Zero) id = DefaultTexture;
            if (_texIndex.TryGetValue(id, out int i)) return i;
            var t = id == DefaultTexture ? textures.Request(id, LocalTextures) : textures.Request(id, _assetsForTextures!);
            if (t.State == TexState.Loading) pending++;
            int slot = t.State == TexState.Ready ? t.Slot : Gpu.Bindless.Grey;
            _texSlots.Add(slot);
            _texIndex[id] = _texSlots.Count - 1;
            return _texSlots.Count - 1;
        }
        _ctx.TextureIndex = TextureIndex;

        for (int s = 0; s < _frames.Count; s++)
        {
            var sf = _frames[s];
            if (!InReach(sf, cam, v.DrawDistance, now)) continue;
            _assetsForTextures = _frameAssets[s];
            LastSources++;
            ParticleSim.Generate(sf, s, _ctx);
        }
        _ctx.EndFrame();
        _assetsForTextures = null;
        var parts = _ctx.Out;
        if (extra is { Count: > 0 })
        {
            int tex = TextureIndex(DefaultTexture);
            foreach (var x in extra) parts.Add(x with { Texture = tex, Source = -1 });
        }
        LastParticles = parts.Count;
        Drawn = parts.Count;
        if (CountNext)
        {
            var counts = new Dictionary<UUID, int>();
            foreach (var pt in parts)
            {
                if (pt.Source < 0) continue;
                var id = _frames[pt.Source].Source.Id;
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
            Counts = counts;
            CountNext = false;
        }
        if (parts.Count == 0) { LastMs = Ms(t0); return pending; }

        BuildQuads(v, parts, dl, frustum, (float)max);
        LastMs = Ms(t0);
        return pending;
    }

    IAssetSource? _assetsForTextures;

    static double Ms(long t0) => System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

    double Expected(double refRate)
    {
        double total = 0;
        double fdt = 1.0 / Math.Max(1.0, Settings.ViewerFps);
        foreach (var sf in _frames)
        {
            if (sf.Source.Last is not { Ps: { } ps }) continue;
            if (ps.BurstPartCount == 0 || ps.PartMaxAge <= 0) continue;
            float dist = Vector3.Distance(sf.Pos, _ctx.Camera);
            if (ParticleSim.LimitedRate(ps, dist, _ctx.PixelMeterRatio, refRate) is not { } limited) continue;
            total += ParticleSim.EmissionRate(ps, fdt) * Math.Min(ps.PartMaxAge, 30) * (1 - limited);
        }
        return total;
    }

    /// <summary>Could any of this source's particles be within the draw distance now?</summary>
    static bool InReach(in SourceFrame sf, Vector3 cam, float drawDistance, double now)
    {
        float reach = 0;
        foreach (var e in sf.Source.History)
        {
            if (e.Ps is not { } ps) continue;
            float age = MathF.Min(ps.PartMaxAge, 60);
            float speed = MathF.Max(MathF.Abs(ps.BurstSpeedMin), MathF.Abs(ps.BurstSpeedMax));
            var acc = new Vector3(ps.PartAcceleration.X, ps.PartAcceleration.Y, ps.PartAcceleration.Z).Length();
            float r = ps.BurstRadius + speed * age + 0.5f * acc * age * age + 4f;
            if (((uint)ps.PartDataFlags & PartFlags.Wind) != 0) r += (sf.Wind.Length() + 10f) * age;
            if (((uint)ps.PartDataFlags & (PartFlags.TargetPos | PartFlags.TargetLinear)) != 0) r = MathF.Max(r, Vector3.Distance(sf.Pos, sf.Target) + 4f);
            reach = MathF.Max(reach, r);
        }
        if (sf.Source.Poses.Count > 1)
        {
            // a moving source left particles along its path
            var first = sf.Source.PoseAt(now - 30).Pos + sf.Offset;
            reach += Vector3.Distance(first, sf.Pos);
        }
        return Vector3.Distance(sf.Pos, cam) - reach < drawDistance;
    }

    void BuildQuads(ViewSetup v, List<Particle> parts, DrawList dl, Frustum frustum, float maxParts)
    {
        var cam = v.Position;
        var camRight = Vector3.Normalize(new Vector3(v.View.M11, v.View.M21, v.View.M31));
        float maxArea = maxParts * 0.02f;   // MAX_PARTICLE_AREA_SCALE
        // ribbons: each particle joins the next one its source emitted (its "parent")
        var parent = new int[parts.Count];
        Array.Fill(parent, -1);
        bool anyRibbon = false;
        for (int i = 0; i < parts.Count; i++) if ((parts[i].Flags & PartFlags.Ribbon) != 0) { anyRibbon = true; break; }
        if (anyRibbon)
        {
            _order.Clear();
            for (int i = 0; i < parts.Count; i++) if ((parts[i].Flags & PartFlags.Ribbon) != 0) _order.Add(i);
            _order.Sort((a, b) => parts[a].Source != parts[b].Source ? parts[a].Source.CompareTo(parts[b].Source) : parts[a].Order.CompareTo(parts[b].Order));
            for (int n = 0; n + 1 < _order.Count; n++)
                if (parts[_order[n]].Source == parts[_order[n + 1]].Source) parent[_order[n]] = _order[n + 1];
        }

        // visible particles, far to near from the head's centre (LLParticlePartition sorts along the camera axis; here by
        // distance, so turning the head never reorders them: docs/rendering.md > Distances from the head)
        _order.Clear();
        var depth = new float[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var d = p.Pos - cam;
            float d2 = d.LengthSquared();
            float area = p.Scale.X * p.Scale.Y * (d2 > 1f ? 1f / d2 : 1f);
            if (area > maxArea) continue;
            if (d2 > 25f && area < 0.005f * 0.005f) continue;   // NEAR_PART_DIST_SQ / MIN_PART_AREA
            float radius = 0.5f * MathF.Max(p.Scale.X, p.Scale.Y);
            if ((p.Flags & PartFlags.Ribbon) != 0)
            {
                var pp = parent[i] >= 0 ? parts[parent[i]].Pos : _frames[p.Source].Pos;
                radius = MathF.Max(radius, Vector3.Distance(pp, p.Pos));
            }
            if (MathF.Sqrt(d2) - radius > v.DrawDistance) continue;
            if (!frustum.Sphere(p.Pos, radius)) continue;
            depth[i] = MathF.Sqrt(d2);
            _order.Add(i);
        }
        _order.Sort((a, b) => depth[b] != depth[a] ? depth[b].CompareTo(depth[a]) : a.CompareTo(b));

        var verts = dl.ParticleVertices;
        var quads = dl.ParticleQuads;
        foreach (int i in _order)
        {
            var p = parts[i];
            int slot = _texSlots.Count > p.Texture ? _texSlots[p.Texture] : Gpu.Bindless.White;
            uint flags = ((p.Flags & PartFlags.Emissive) != 0 ? ParticleVertex.FlagEmissive : 0)
                         | (p.BlendSrc != 7 && p.BlendDst != 7 ? ParticleVertex.FlagNoMinAlpha : 0);
            uint col = Pack(p.Color);
            uint glow = (uint)MathF.Round(p.Glow * 255f) | flags << 8;
            if ((p.Flags & PartFlags.Ribbon) != 0)
            {
                Vector3 ppos, paxis; float pscale; uint pcol, pglow;
                if (parent[i] >= 0)
                {
                    var q = parts[parent[i]];
                    ppos = q.Pos; paxis = q.Axis; pscale = q.Scale.X; pcol = Pack(q.Color);
                    pglow = (uint)MathF.Round(q.Glow * 255f) | flags << 8;
                }
                else
                {
                    var sf = _frames[p.Source];
                    ppos = sf.Pos; paxis = Vector3.Transform(Vector3.UnitZ, sf.Rot); pscale = p.StartScaleX;
                    pcol = Pack(p.StartColor); pglow = (uint)MathF.Round(p.StartGlow * 255f) | flags << 8;
                }
                var axis = p.Axis * (p.Scale.X * 0.5f);
                paxis *= pscale * 0.5f;
                verts.Add(new ParticleVertex { Pos = ppos + paxis, Uv = new Vector2(0, 1), Color = pcol, GlowFlags = pglow, Texture = slot });
                verts.Add(new ParticleVertex { Pos = ppos - paxis, Uv = new Vector2(0, 0), Color = pcol, GlowFlags = pglow, Texture = slot });
                verts.Add(new ParticleVertex { Pos = p.Pos + axis, Uv = new Vector2(1, 1), Color = col, GlowFlags = glow, Texture = slot });
                verts.Add(new ParticleVertex { Pos = p.Pos - axis, Uv = new Vector2(1, 0), Color = col, GlowFlags = glow, Texture = slot });
                quads.Add(new ParticleQuad(depth[i], p.BlendSrc, p.BlendDst, p.Glow > 0 || parent[i] >= 0 && parts[parent[i]].Glow > 0));
                continue;
            }
            var toPart = p.Pos - cam;
            var up = Vector3.UnitZ;
            var right = Vector3.Cross(toPart, up);
            if (right.LengthSquared() < 1e-12f) right = camRight;
            right = Vector3.Normalize(right);
            up = Vector3.Normalize(Vector3.Cross(right, toPart));
            if ((p.Flags & PartFlags.FollowVelocity) != 0 && p.Vel != Vector3.Zero)
            {
                var nv = Vector3.Normalize(p.Vel);
                var fr = new Vector2(Vector3.Dot(nv, right), Vector3.Dot(nv, up));
                if (fr.LengthSquared() > 1e-12f)
                {
                    fr = Vector2.Normalize(fr);
                    var newUp = up * fr.Y + right * fr.X;
                    var newRight = right * fr.Y - up * fr.X;
                    up = Vector3.Normalize(newUp);
                    right = Vector3.Normalize(newRight);
                }
            }
            right *= 0.5f * p.Scale.X;
            up *= 0.5f * p.Scale.Y;
            verts.Add(new ParticleVertex { Pos = p.Pos + up - right, Uv = new Vector2(0, 1), Color = col, GlowFlags = glow, Texture = slot });
            verts.Add(new ParticleVertex { Pos = p.Pos - up - right, Uv = new Vector2(0, 0), Color = col, GlowFlags = glow, Texture = slot });
            verts.Add(new ParticleVertex { Pos = p.Pos + up + right, Uv = new Vector2(1, 1), Color = col, GlowFlags = glow, Texture = slot });
            verts.Add(new ParticleVertex { Pos = p.Pos - up + right, Uv = new Vector2(1, 0), Color = col, GlowFlags = glow, Texture = slot });
            quads.Add(new ParticleQuad(depth[i], p.BlendSrc, p.BlendDst, p.Glow > 0, (p.Flags & PartFlags.FollowVelocity) == 0 || p.Vel == Vector3.Zero));
        }
    }

    /// <summary>LLColor4 -> LLColor4U: round and clamp each channel.</summary>
    static uint Pack(Vector4 c)
    {
        static uint B(float x) => (uint)Math.Clamp((int)MathF.Round(x * 255f), 0, 255);
        return B(c.X) | B(c.Y) << 8 | B(c.Z) << 16 | B(c.W) << 24;
    }
}

/// <summary>Serves the viewer's built-in particle image (skins/default/textures/pixiesmall.j2c, shipped beside the
/// renderer) as a texture asset.</summary>
sealed class LocalTextureSource : IAssetSource
{
    static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "Textures", "pixiesmall.j2c");
    public Task<byte[]?> GetTextureAsync(UUID id, CancellationToken ct) =>
        Task.FromResult(id == ParticleBuilder.DefaultTexture && File.Exists(Path) ? File.ReadAllBytes(Path) : null);
    public Task<byte[]?> GetMeshAsync(UUID id, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    public Task<byte[]?> GetAssetAsync(UUID id, LibreMetaverse.AssetType type, CancellationToken ct) => Task.FromResult<byte[]?>(null);
}
