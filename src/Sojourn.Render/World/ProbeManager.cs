using System.Runtime.InteropServices;
using LibreMetaverse;
using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;

namespace Sojourn.Render.World;

public enum ProbeKind { Default, Terrain, Object, Manual }

/// <summary>A prim's reflection probe params (ExtraParams type 0x90, LLReflectionProbeParams).</summary>
public readonly record struct ReflectionProbeParams(float Ambiance, float ClipDistance, byte Flags)
{
    public const byte FlagBoxVolume = 0x01, FlagDynamic = 0x02, FlagMirror = 0x04;
    public bool Box => (Flags & FlagBoxVolume) != 0;

    /// <summary>LLReflectionProbeParams::unpack: F32 ambiance, F32 clip distance, U8 flags (9 bytes).</summary>
    public static ReflectionProbeParams? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9) return null;
        float amb = BitConverter.ToSingle(data[..4]);
        float clip = BitConverter.ToSingle(data[4..8]);
        if (!float.IsFinite(amb) || !float.IsFinite(clip)) return null;
        return new ReflectionProbeParams(Math.Clamp(amb, 0f, 100f), Math.Clamp(clip, 0f, 1024f), data[8]);
    }
}

/// <summary>Where a probe is: the viewer's LLReflectionMap placement (origin and radius in its region's coordinates).</summary>
public sealed class ProbeSite
{
    public required ProbeKind Kind;
    public required ulong Region;
    public Vector3 Origin;
    public float Radius;
    public float NearClip = 1f;
    public bool Box;
    public Matrix4x4 ToUnitBox = Matrix4x4.Identity;   // region -> [-1,1]^3 (box probes)
    public float Ambiance;
    /// <summary>Bumped when the placement moves; captures of an older placement are stale.</summary>
    public long Placement;
    public bool Alive = true;
    internal long SerialCache, SerialCacheAt = -1;
    /// <summary>refIndex.w: 0 automatic, 1 manual sphere, -1 manual box.</summary>
    public int Priority => Kind != ProbeKind.Manual ? 0 : Box ? -1 : 1;

    public void Place(Vector3 origin, float radius, float nearClip)
    {
        if (Vector3.DistanceSquared(origin, Origin) > 0.25f * 0.25f || MathF.Abs(radius - Radius) > 0.25f) Placement++;
        Origin = origin;
        Radius = radius;
        NearClip = MathF.Max(nearClip, 0.1f);   // MINIMUM_NEAR_CLIP
    }
}

/// <summary>The sky a capture was lit by, to tell when the light has moved on enough to recapture.</summary>
public readonly record struct ProbeLighting(Vector3 LightDir, bool SunUp, Vector3 Sunlight, Vector3 Ambient, Vector3 BlueHorizon, Vector3 BlueDensity, float Ambiance)
{
    public static ProbeLighting From(ViewSetup v)
    {
        var s = v.SkyU;
        return new(Vector3.Normalize(v.SunUp ? v.SunDirRegion : v.MoonDirRegion), v.SunUp, s.SunlightColor, s.AmbientColor, s.BlueHorizon, s.BlueDensity,
            s.ReflectionProbeAmbiance);
    }

    /// <summary>How far the light has moved: 1 = about 3 degrees of sun or a 5% change of a sky colour.</summary>
    public float Delta(in ProbeLighting o)
    {
        if (SunUp != o.SunUp) return 1000f;
        float ang = MathF.Acos(Math.Clamp(Vector3.Dot(LightDir, o.LightDir), -1f, 1f)) * (180f / MathF.PI) / 3f;
        static float Col(Vector3 a, Vector3 b)
        {
            float m = MathF.Max(MathF.Max(MathF.Max(a.X, a.Y), MathF.Max(a.Z, b.X)), MathF.Max(MathF.Max(b.Y, b.Z), 0.05f));
            var d = Vector3.Abs(a - b);
            return MathF.Max(d.X, MathF.Max(d.Y, d.Z)) / m / 0.05f;
        }
        float amb = MathF.Abs(Ambiance - o.Ambiance) / MathF.Max(MathF.Max(Ambiance, o.Ambiance), 0.05f) / 0.05f;
        return MathF.Max(MathF.Max(ang, amb), MathF.Max(MathF.Max(Col(Sunlight, o.Sunlight), Col(Ambient, o.Ambient)),
            MathF.Max(Col(BlueHorizon, o.BlueHorizon), Col(BlueDensity, o.BlueDensity))));
    }
}

/// <summary>One probe site lit by one sky: its maps (when resident) and what they were captured under.</summary>
public sealed class ProbeEntry
{
    public required ProbeSite Site;
    public required long Sky;
    public ProbeMaps? Maps;
    public bool Complete;              // irradiance and radiance captured
    public ProbeLighting Light;
    public long SceneSerial, Placement;
    public bool Provisional;           // captured while assets in range were still loading
    public long CapturedAt, LastUsed;
    public int Captures;
    public long FirstAttempt;          // when a capture first waited for its assets (0: not waiting)
    public long NotBefore;             // requeued captures wait a little before the next try
}

/// <summary>A probe capture to record: the entry, the view whose sky lights it, and the per-capture GPU data.</summary>
public sealed class ProbeCapture
{
    public required ProbeEntry Entry;
    public required ViewSetup Env;         // the requesting view (sky, water, LOD camera), in its own region
    public required RegionScene Scene;     // the probe's region
    public Vector3 Center;                 // probe origin, probe region coordinates
    public long SceneSerial;
    public bool WasComplete;               // its maps held a valid capture before this one
    public bool Blocking;                  // a render waits for it
    public required DrawList Draws;        // from the manager's pool (each capture's list is thousands of entries long)
    public int Pending;
    public bool Shadows;
    public Matrix4x4 ShadowLightViewProj, ShadowTex;   // region -> caster clip; region -> shadow texture
    public Renderer.FrameDraws? Uploaded;
    public readonly Renderer.ViewData[] IrradianceViews = new Renderer.ViewData[6];
    public readonly Renderer.ViewData[] RadianceViews = new Renderer.ViewData[6];
    public readonly ViewSetup[] IrradianceSetups = new ViewSetup[6];
    public readonly ViewSetup[] RadianceSetups = new ViewSetup[6];
}

/// <summary>The probes one view samples (or one capture pass), ready to write into the frame arena.</summary>
public sealed class ProbeList
{
    public ProbeEntry? Default;
    public ProbeEntry? DefaultEntry;   // the view's own default probe (Default may be a nearby stand-in)
    public readonly HashSet<ProbeEntry> NoRadiance = new();   // listed for irradiance only (captured this frame)
    public readonly List<(ProbeEntry Entry, Vector3 Offset)> Local = new();
    public int Missing;                // probes in view not captured yet under this sky
    public int Stale;
}

/// <summary>
/// Reflection probes as LLReflectionMapManager places and blends them, shared by every view. Placement follows the
/// viewer at RenderReflectionProbeLevel 3: an 8x8 grid of terrain/water probes per region (32 m cells, 2 m above
/// land or water, radius 27.7 m); one automatic probe per 32 m volume-octree node holding prims (autoAdjustOrigin:
/// nudged by rays to the node's corners, at least 2 m + radius/2 above land, radius at least 8 m); manual probes
/// (prims with reflection probe params); and the default probe 64 m above the camera (sky, water, clouds, terrain
/// within its 64 m far clip). A view samples the probes whose spheres meet its frustum within draw distance,
/// nearest first (the viewer's 256 cube slots go to the closest).
///
/// Captures are cached per (site, sky) and shared by every agent and the spotlight; the sky is a fixed sky's id or
/// the viewing region's environment. A capture goes stale (still used, recaptured in the background) when the sun
/// has moved about 3 degrees or a sky colour 5%, when objects within its 64 m far clip changed, or when it was taken
/// with assets still loading; it is unusable (renders wait for a new one, bounded) when it never was captured
/// under this sky or the light moved 8 times that. The viewer instead refreshes round-robin, one face per frame
/// (12 frames per probe, so a full pass over 256 probes takes about a minute at 60 fps), and drops every probe when
/// the sky's keyframe changes; a still render here sees the settled state.
/// </summary>
public sealed class ProbeManager : IDisposable
{
    sealed class RegionProbes
    {
        public required RegionScene Scene;
        public ProbeSite[] Terrain = [];
        public long TerrainRevision = long.MinValue;
        public float WaterHeight = float.NaN;
        public readonly Dictionary<(int, int, int), ProbeSite> Objects = new();
        public readonly Dictionary<uint, ProbeSite> Manual = new();
        public readonly Dictionary<(int, int), long> Changes = new();   // 32 m xy cell -> last change serial
        public long WholeChange;                                          // terrain or water changes
        public long NextScan;
        public bool Scanned;
        public long ScanVersion = -1;   // the scene's ProbeVersion at the last object scan
    }

    public const float ProbeSpacing = 32f;
    public static readonly float TerrainRadius = MathF.Sqrt(16f * 16f * 3f);   // 27.7128
    public const float CaptureFar = 64f;              // RenderReflectionProbeDrawDistance
    public const float DefaultHeight = 64f, DefaultRadius = 4096f;
    public const float RefreshDelta = 1f, InvalidDelta = 8f;
    public const int MaxViewProbes = 255;             // RenderReflectionProbeCount 256, less the default probe

    readonly ProbeSystem _system;
    readonly RenderWorld _world;
    readonly Dictionary<ulong, RegionProbes> _regions = new();
    readonly Dictionary<(ulong, int, int, int), ProbeSite> _defaults = new();
    readonly Dictionary<(ProbeSite, long), ProbeEntry> _entries = new();
    readonly Dictionary<ProbeEntry, (ViewSetup Env, RegionScene Scene, float Priority, bool Blocking)> _queue = new();
    readonly List<ProbeVolume> _volumes = new();
    readonly List<ManualProbeObject> _manual = new();
    long _changeSerial = 1;
    int _resident;

    /// <summary>RenderReflectionProbeLevel: 0 default probe only, 1 + manual, 2 + terrain/water, 3 + objects.</summary>
    public int Level = 3;
    /// <summary>Resident probe maps (VRAM: ProbeSystem.BytesPerProbe each); least recently used are reused first.</summary>
    public int MaxResident = 512;
    /// <summary>A probe isn't recaptured for staleness more often than this (ms).</summary>
    public long MinRefreshMs = 5_000;
    public long ScanIntervalMs = 500;
    public Func<ulong, Func<uint, ReflectionProbeParams?>?>? ProbeParams;
    public ProbeSystem System => _system;
    public int Resident => _resident;
    public int Queued => _queue.Count;
    public ulong Bytes => (ulong)_resident * _system.BytesPerProbe;
    public long CapturesDone { get; private set; }

    public ProbeManager(ProbeSystem system, RenderWorld world)
    {
        _system = system;
        _world = world;
    }

    // --- placement ---------------------------------------------------------------------------------

    /// <summary>Regions within range of a point (region coordinates of <paramref name="from"/>), with offsets.</summary>
    public List<(RegionScene Scene, Vector3 Offset)> ScenesNear(RegionScene from, Vector3 pos, float range)
    {
        var list = new List<(RegionScene, Vector3)> { (from, Vector3.Zero) };
        Utils.LongToUInts(from.Source.Handle, out uint rx, out uint ry);
        foreach (var other in _world.Regions)
        {
            if (other == from || !other.Source.HasSimulator) continue;
            Utils.LongToUInts(other.Source.Handle, out uint ox, out uint oy);
            var offset = new Vector3((float)((long)ox - rx), (float)((long)oy - ry), 0);
            var size = new Vector3(other.Source.Simulator.SizeX, other.Source.Simulator.SizeY, 0);
            var c = Vector3.Clamp(new Vector3(pos.X, pos.Y, 0), offset, offset + size);
            if (Vector2.Distance(new Vector2(c.X, c.Y), new Vector2(pos.X, pos.Y)) > range) continue;
            list.Add((other, offset));
        }
        return list;
    }

    RegionProbes Probes(RegionScene scene)
    {
        if (!_regions.TryGetValue(scene.Source.Handle, out var rp) || rp.Scene != scene)
            _regions[scene.Source.Handle] = rp = new RegionProbes { Scene = scene };
        return rp;
    }

    /// <summary>Keep a region's probe sites current (throttled): terrain grid on terrain/water changes, object and
    /// manual probes and the change grid from an object scan.</summary>
    void Update(RegionScene scene, long now)
    {
        var rp = Probes(scene);
        if (rp.Scanned && now < rp.NextScan) return;
        rp.NextScan = now + ScanIntervalMs;
        rp.Scanned = true;
        ulong handle = scene.Source.Handle;
        var sim = scene.Source.Simulator;

        // LLViewerRegion::updateReflectionProbes: one probe per 32 m cell, 2 m above max(water, land)
        float water = sim.WaterHeight;
        if (scene.Terrain.Revision != rp.TerrainRevision || water != rp.WaterHeight)
        {
            rp.TerrainRevision = scene.Terrain.Revision;
            rp.WaterHeight = water;
            rp.WholeChange = ++_changeSerial;
            int nx = Math.Max(1, (int)(sim.SizeX / ProbeSpacing)), ny = Math.Max(1, (int)(sim.SizeY / ProbeSpacing));
            if (rp.Terrain.Length != nx * ny)
            {
                foreach (var s in rp.Terrain) Kill(s);
                rp.Terrain = new ProbeSite[nx * ny];
                for (int i = 0; i < rp.Terrain.Length; i++) rp.Terrain[i] = new ProbeSite { Kind = ProbeKind.Terrain, Region = handle };
            }
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    float x = i * ProbeSpacing + ProbeSpacing * 0.5f, y = j * ProbeSpacing + ProbeSpacing * 0.5f;
                    float z = MathF.Max(water, scene.TerrainHeight(x, y)) + 2f;
                    rp.Terrain[j * nx + i].Place(new Vector3(x, y, z), TerrainRadius, 1f);
                }
        }

        // nothing in the region moved, came or went since the last scan: the object probes stand
        if (scene.ProbeVersion == rp.ScanVersion) return;
        rp.ScanVersion = scene.ProbeVersion;
        _volumes.Clear();
        _manual.Clear();
        scene.ScanForProbes(_volumes, _manual, (min, max) => MarkChanged(rp, min, max), ProbeParams?.Invoke(handle));
        PlaceObjectProbes(rp, scene, handle);
        PlaceManualProbes(rp, handle);
    }

    void MarkChanged(RegionProbes rp, Vector3 min, Vector3 max)
    {
        long serial = ++_changeSerial;
        int x0 = (int)MathF.Floor(min.X / ProbeSpacing), x1 = (int)MathF.Floor(max.X / ProbeSpacing);
        int y0 = (int)MathF.Floor(min.Y / ProbeSpacing), y1 = (int)MathF.Floor(max.Y / ProbeSpacing);
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096) { rp.WholeChange = serial; return; }
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                rp.Changes[(x, y)] = serial;
    }

    /// <summary>
    /// registerSpatialGroup + LLReflectionMap::autoAdjustOrigin. The viewer's volume octree grows from a unit root at
    /// the origin by doubling toward the first prim, so in region coordinates its 32 m nodes span [32k-1, 32k+31);
    /// a prim lives in the node whose half-size s has s &lt; bin radius &lt;= 2s, below the 32 m node when it is small.
    /// So a 32 m node exists where prims with bin radius &lt;= 32 have their centre, and its bounds are theirs.
    /// </summary>
    void PlaceObjectProbes(RegionProbes rp, RegionScene scene, ulong handle)
    {
        var cells = new Dictionary<(int, int, int), List<int>>();
        if (Level >= 3)
            for (int i = 0; i < _volumes.Count; i++)
            {
                var v = _volumes[i];
                if (v.BinRadius > 32f) continue;
                var key = ((int)MathF.Floor((v.Center.X + 1f) / ProbeSpacing), (int)MathF.Floor((v.Center.Y + 1f) / ProbeSpacing),
                    (int)MathF.Floor((v.Center.Z + 1f) / ProbeSpacing));
                if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
                list.Add(i);
            }
        foreach (var key in rp.Objects.Keys.Where(k => !cells.ContainsKey(k)).ToList())
        {
            Kill(rp.Objects[key]);
            rp.Objects.Remove(key);
        }
        Span<Vector3> corners = stackalloc Vector3[8];
        foreach (var (key, members) in cells)
        {
            var bmin = new Vector3(float.MaxValue);
            var bmax = new Vector3(float.MinValue);
            foreach (int i in members) { bmin = Vector3.Min(bmin, _volumes[i].Min); bmax = Vector3.Max(bmax, _volumes[i].Max); }
            var center = (bmin + bmax) * 0.5f;
            var half = (bmax - bmin) * 0.5f;
            var emin = new Vector3(float.MaxValue);
            var emax = new Vector3(float.MinValue);
            bool hit = false;
            for (int c = 0; c < 8; c++)
            {
                corners[c] = center + half * new Vector3((c & 1) != 0 ? 1 : -1, (c & 2) != 0 ? 1 : -1, (c & 4) != 0 ? 1 : -1);
                float t = Raycast(members, center, corners[c]);
                var p = corners[c];
                if (t < 1f) { p = center + (corners[c] - center) * t; hit = true; }
                emin = Vector3.Min(emin, p);
                emax = Vector3.Max(emax, p);
            }
            var origin = hit ? (emin + emax) * 0.5f : center;
            float height = scene.TerrainHeight(origin.X, origin.Y) + 2f;   // land only, not water
            origin.Z = MathF.Max(origin.Z, height);
            float r2 = 0f;
            for (int c = 0; c < 8; c++) r2 = MathF.Max(r2, Vector3.DistanceSquared(corners[c], origin));
            float radius = MathF.Max(MathF.Sqrt(r2), 8f);
            origin.Z = MathF.Max(origin.Z, height + radius * 0.5f);   // keep the near clip out of the ground
            if (!rp.Objects.TryGetValue(key, out var site))
                rp.Objects[key] = site = new ProbeSite { Kind = ProbeKind.Object, Region = handle };
            site.Place(origin, radius, radius * 0.5f);
        }
    }

    /// <summary>Nearest hit (segment parameter, 1 = none) of start..end with the members' prim boxes; stands in for
    /// lineSegmentIntersect against their triangles (exact for box prims).</summary>
    float Raycast(List<int> members, Vector3 start, Vector3 end)
    {
        float best = 1f;
        foreach (int i in members)
        {
            var v = _volumes[i];
            if (!v.Pickable) continue;
            var a = Vector3.Transform(start, v.ToUnitBox);
            var b = Vector3.Transform(end, v.ToUnitBox);
            var d = b - a;
            float t0 = 0f, t1 = best;
            bool miss = false;
            for (int k = 0; k < 3 && !miss; k++)
            {
                float o = k == 0 ? a.X : k == 1 ? a.Y : a.Z;
                float dd = k == 0 ? d.X : k == 1 ? d.Y : d.Z;
                if (MathF.Abs(dd) < 1e-9f) { if (o < -0.5f || o > 0.5f) miss = true; continue; }
                float ta = (-0.5f - o) / dd, tb = (0.5f - o) / dd;
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = MathF.Max(t0, ta);
                t1 = MathF.Min(t1, tb);
                if (t0 > t1) miss = true;
            }
            // a start inside the box isn't a hit (the viewer's ray-triangle test sees the far wall; ignore that box)
            if (!miss && t0 > 0f && t0 < best) best = t0;
        }
        return best;
    }

    void PlaceManualProbes(RegionProbes rp, ulong handle)
    {
        var seen = new HashSet<uint>();
        if (Level >= 1)
            foreach (var m in _manual)
            {
                seen.Add(m.LocalId);
                if (!rp.Manual.TryGetValue(m.LocalId, out var site))
                    rp.Manual[m.LocalId] = site = new ProbeSite { Kind = ProbeKind.Manual, Region = handle };
                bool box = m.Params.Box;
                // LLReflectionMapManager: box radius = |scale/2|, sphere radius = scale.x/2; near clip from the params
                float radius = box ? (m.Scale * 0.5f).Length() : m.Scale.X * 0.5f;
                if (site.Box != box) site.Placement++;
                site.Box = box;
                site.Ambiance = m.Params.Ambiance;
                site.Place(m.Position, radius, m.Params.ClipDistance);
                var model = Matrix4x4.CreateScale(m.Scale * 0.5f) * Matrix4x4.CreateFromQuaternion(m.Rotation) * Matrix4x4.CreateTranslation(m.Position);
                Matrix4x4.Invert(model, out site.ToUnitBox);
            }
        foreach (var id in rp.Manual.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Kill(rp.Manual[id]);
            rp.Manual.Remove(id);
        }
    }

    void Kill(ProbeSite site)
    {
        site.Alive = false;
        foreach (var key in _entries.Keys.Where(k => k.Item1 == site).ToList())
        {
            var e = _entries[key];
            _queue.Remove(e);
            Release(e);
            _entries.Remove(key);
        }
    }

    ProbeSite DefaultSite(ulong region, Vector3 cam)
    {
        // touch_default_probe: camera + 64 m; quantized so nearby cameras share it
        var key = (region, (int)MathF.Floor(cam.X / ProbeSpacing), (int)MathF.Floor(cam.Y / ProbeSpacing), (int)MathF.Floor((cam.Z + DefaultHeight) / 16f));
        if (!_defaults.TryGetValue(key, out var site))
        {
            site = new ProbeSite { Kind = ProbeKind.Default, Region = region };
            site.Place(new Vector3((key.Item2 + 0.5f) * ProbeSpacing, (key.Item3 + 0.5f) * ProbeSpacing, (key.Item4 + 0.5f) * 16f), DefaultRadius, 1f);
            _defaults[key] = site;
        }
        return site;
    }

    IEnumerable<ProbeSite> Sites(RegionProbes rp)
    {
        if (Level >= 2) foreach (var s in rp.Terrain) yield return s;
        if (Level >= 3) foreach (var s in rp.Objects.Values) yield return s;
        if (Level >= 1) foreach (var s in rp.Manual.Values) yield return s;
    }

    long ChangeSerialAround(ProbeSite site)
    {
        if (site.SerialCacheAt == _changeSerial) return site.SerialCache;
        long serial = 0;
        if (!_regions.TryGetValue(site.Region, out var home)) return 0;
        float range = CaptureFar * 1.733f;
        foreach (var (scene, offset) in ScenesNear(home.Scene, site.Origin, range))
        {
            var rp = Probes(scene);
            serial = Math.Max(serial, rp.WholeChange);
            if (site.Kind == ProbeKind.Default) continue;   // sky, water and terrain only
            var p = site.Origin - offset;
            int x0 = (int)MathF.Floor((p.X - range) / ProbeSpacing), x1 = (int)MathF.Floor((p.X + range) / ProbeSpacing);
            int y0 = (int)MathF.Floor((p.Y - range) / ProbeSpacing), y1 = (int)MathF.Floor((p.Y + range) / ProbeSpacing);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (rp.Changes.TryGetValue((x, y), out long s) && s > serial) serial = s;
        }
        site.SerialCache = serial;
        site.SerialCacheAt = _changeSerial;
        return serial;
    }

    // --- entries and their state ---------------------------------------------------------------------

    ProbeEntry Entry(ProbeSite site, long sky)
    {
        if (!_entries.TryGetValue((site, sky), out var e))
            _entries[(site, sky)] = e = new ProbeEntry { Site = site, Sky = sky };
        return e;
    }

    static bool Usable(ProbeEntry e, in ProbeLighting light) => e.Complete && e.Site.Alive && e.Light.Delta(light) <= InvalidDelta;

    bool NeedsRefresh(ProbeEntry e, in ProbeLighting light, long now, out bool urgent)
    {
        urgent = !Usable(e, light);
        if (urgent) return true;
        bool stale = e.Placement != e.Site.Placement || e.Provisional || e.Light.Delta(light) > RefreshDelta
                     || ChangeSerialAround(e.Site) > e.SceneSerial;
        return stale && now - e.CapturedAt >= MinRefreshMs;
    }

    void Request(ProbeEntry e, ViewSetup env, RegionScene scene, float priority, bool blocking)
    {
        if (_queue.TryGetValue(e, out var q))
        {
            if (q.Blocking && !blocking) return;
            if (q.Blocking == blocking && q.Priority <= priority) return;
        }
        _queue[e] = (env, scene, priority, blocking);
    }

    /// <summary>
    /// The probes a view samples (frustum-intersecting, within draw distance, nearest first as the viewer gives its
    /// 256 cube slots to the closest), requesting captures for those missing or stale. With <paramref name="blocking"/>
    /// missing captures are marked as holding up a render.
    /// </summary>
    public ProbeList Plan(ViewSetup v, RegionScene region, long sky, bool blocking)
    {
        long now = global::System.Environment.TickCount64;
        var light = ProbeLighting.From(v);
        var list = new ProbeList();
        var frustum = new Frustum(v.View * v.Proj);
        float range = v.DrawDistance + 64f;
        var scenes = ScenesNear(region, v.Position, range);
        foreach (var (scene, _) in scenes) Update(scene, now);
        Housekeeping(now);

        // the default probe
        var def = Entry(DefaultSite(region.Source.Handle, v.Position), sky);
        def.LastUsed = now;
        list.DefaultEntry = def;
        if (NeedsRefresh(def, light, now, out bool defUrgent)) Request(def, v, region, -1e6f, blocking && defUrgent);
        if (Usable(def, light)) list.Default = def;
        else
        {
            // a nearby default probe under this sky stands in until ours is captured
            list.Default = _entries.Values.Where(e => e.Site.Kind == ProbeKind.Default && e.Sky == sky && e.Site.Region == region.Source.Handle && Usable(e, light))
                .MinBy(e => Vector2.Distance(new Vector2(e.Site.Origin.X, e.Site.Origin.Y), new Vector2(v.Position.X, v.Position.Y)));
            if (list.Default != null) list.Default.LastUsed = now;
            list.Missing++;
        }

        var candidates = _candidates;
        candidates.Clear();
        foreach (var (scene, offset) in scenes)
            foreach (var site in Sites(Probes(scene)))
            {
                var c = site.Origin + offset;
                float score = Vector3.Distance(c, v.Position) - site.Radius;
                if (score > v.DrawDistance || !frustum.Sphere(c, site.Radius)) continue;
                candidates.Add((Entry(site, sky), offset, score));
            }
        candidates.Sort(static (a, b) => a.Score.CompareTo(b.Score));
        int cap = Math.Min(MaxViewProbes, MaxResident - 16);
        foreach (var (e, offset, score) in candidates)
        {
            if (list.Local.Count >= cap) break;
            e.LastUsed = now;
            if (NeedsRefresh(e, light, now, out bool urgent))
            {
                Request(e, v, region, score, blocking && urgent);
                if (urgent) list.Missing++; else list.Stale++;
            }
            if (Usable(e, light)) list.Local.Add((e, offset));
        }
        return list;
    }

    long _nextHousekeeping;
    readonly List<(ProbeEntry E, Vector3 Offset, float Score)> _candidates = new();

    /// <summary>Forget entries without maps unused for minutes, and default sites nobody uses.</summary>
    void Housekeeping(long now)
    {
        if (now < _nextHousekeeping) return;
        _nextHousekeeping = now + 10_000;
        foreach (var key in _entries.Where(kv => kv.Value.Maps == null && now - kv.Value.LastUsed > 300_000 && !_queue.ContainsKey(kv.Value))
                     .Select(kv => kv.Key).ToList())
            _entries.Remove(key);
        var used = _entries.Keys.Select(k => k.Item1).Where(s => s.Kind == ProbeKind.Default).ToHashSet();
        foreach (var key in _defaults.Where(kv => !used.Contains(kv.Value)).Select(kv => kv.Key).ToList())
            _defaults.Remove(key);
    }

    /// <summary>Drop requests that no longer hold anything up once their requester gave up waiting.</summary>
    public void Unblock()
    {
        foreach (var e in _queue.Keys.ToList())
        {
            var q = _queue[e];
            if (q.Blocking) _queue[e] = q with { Blocking = false };
        }
    }

    public bool HasWork(bool blockingOnly)
    {
        long now = global::System.Environment.TickCount64;
        return _queue.Any(kv => kv.Key.NotBefore <= now && (!blockingOnly || kv.Value.Blocking));
    }

    /// <summary>How long a capture may wait for the assets it sees before it's taken anyway (and refreshed later).</summary>
    public long CaptureSettleMs = 4_000;

    /// <summary>
    /// A capture whose draw list still waits for assets goes back in the queue (bounded by
    /// <see cref="CaptureSettleMs"/>): a probe baked with missing meshes or textures would outlive them. Returns true
    /// if it was put back.
    /// </summary>
    public bool Defer(ProbeCapture c, bool blocking)
    {
        long now = global::System.Environment.TickCount64;
        var e = c.Entry;
        if (c.Pending == 0) { e.FirstAttempt = 0; return false; }
        if (e.FirstAttempt == 0) e.FirstAttempt = now;
        if (now - e.FirstAttempt >= CaptureSettleMs) { e.FirstAttempt = 0; return false; }
        e.NotBefore = now + 50;
        Request(e, c.Env, c.Scene, -1e5f, blocking);
        Recycle(c);
        return true;
    }

    /// <summary>Take up to <paramref name="max"/> captures: those holding up renders first, then refreshes, nearest
    /// to their requesting camera first (default probes lead).</summary>
    public List<ProbeCapture> TakeBatch(int max, bool blockingOnly)
    {
        long now = global::System.Environment.TickCount64;
        var picked = _queue.Where(kv => kv.Key.NotBefore <= now && (!blockingOnly || kv.Value.Blocking))
            .OrderBy(kv => kv.Value.Blocking ? 0 : 1).ThenBy(kv => kv.Value.Priority).Take(max).ToList();
        var batch = new List<ProbeCapture>();
        foreach (var (e, q) in picked)
        {
            _queue.Remove(e);
            if (!e.Site.Alive) continue;
            if (!_regions.TryGetValue(e.Site.Region, out var rp)) continue;
            if (Make(e, q.Env, rp.Scene, picked.Select(p => p.Key)) is not { } c) continue;
            c.Blocking = q.Blocking;
            batch.Add(c);
        }
        return batch;
    }

    /// <summary>A capture of the entry, or null when no maps can be had for it now (it's asked for again later).</summary>
    ProbeCapture? Make(ProbeEntry e, ViewSetup env, RegionScene scene, IEnumerable<ProbeEntry> keep)
    {
        bool was = e.Complete && e.Maps != null;
        EnsureMaps(e, keep);
        if (e.Maps == null) return null;
        was &= e.Maps.Valid;
        return new ProbeCapture
        {
            Entry = e, Env = env, Scene = scene, Center = e.Site.Origin, SceneSerial = ChangeSerialAround(e.Site), WasComplete = was,
            Draws = _lists.TryPop(out var dl) ? dl : new DrawList(),
        };
    }

    // capture draw lists, kept for the next capture rather than grown afresh each time (their growth was most of what
    // the render thread allocated while a place filled in)
    readonly Stack<DrawList> _lists = new();

    void Recycle(ProbeCapture c)
    {
        c.Draws.Clear();
        if (_lists.Count < 4 && !_lists.Contains(c.Draws)) _lists.Push(c.Draws);
    }

    /// <summary>A capture given up part way (its probe died or lost its maps): its draw list back to the pool. An entry
    /// still alive is requested again by the next plan that sees it.</summary>
    public void Abandon(ProbeCapture c) => Recycle(c);

    /// <summary>Capture this entry now (in the caller's frame), whatever the queue says.</summary>
    public ProbeCapture? TakeNow(ProbeEntry e, ViewSetup env)
    {
        _queue.Remove(e);
        if (!e.Site.Alive || !_regions.TryGetValue(e.Site.Region, out var rp)) return null;
        return Make(e, env, rp.Scene, new[] { e });
    }

    /// <summary>Maps for an entry: at the cap, the longest unused resident probe's (not one of <paramref name="keep"/>);
    /// else a new pair, or the same reuse while the cube table has no two slots free (released probes' slots come back
    /// a few frames on). Left null when neither can be had.</summary>
    void EnsureMaps(ProbeEntry e, IEnumerable<ProbeEntry> keep)
    {
        if (e.Maps != null) return;
        if (_resident >= MaxResident && Reuse(e, keep)) return;
        if (_system.TryCreateMaps() is { } maps)
        {
            e.Maps = maps;
            _resident++;
            return;
        }
        Reuse(e, keep);
    }

    bool Reuse(ProbeEntry e, IEnumerable<ProbeEntry> keep)
    {
        var keepSet = keep.ToHashSet();
        var victim = _entries.Values.Where(x => x.Maps != null && !keepSet.Contains(x)).MinBy(x => x.LastUsed);
        if (victim == null) return false;
        // reuse its images: later submissions overwrite them after any frame still sampling them
        e.Maps = victim.Maps;
        victim.Maps = null;
        victim.Complete = false;
        e.Maps!.Valid = false;
        return true;
    }

    void Release(ProbeEntry e)
    {
        if (e.Maps == null) return;
        var maps = e.Maps;
        e.Maps = null;
        e.Complete = false;
        _resident--;
        RetireMaps?.Invoke(maps);
    }

    /// <summary>Record a capture as done (its commands are submitted; later frames see the result).</summary>
    public void Completed(ProbeCapture c)
    {
        var e = c.Entry;
        e.Complete = true;
        e.Light = ProbeLighting.From(c.Env);
        e.SceneSerial = c.SceneSerial;
        e.Placement = e.Site.Placement;
        e.Provisional = c.Pending > 0;
        e.CapturedAt = global::System.Environment.TickCount64;
        e.Captures++;
        CapturesDone++;
        Recycle(c);   // recorded: its draws are in the frame's arena now
    }

    /// <summary>Usable probes of a sky around a capture (for its passes to sample, as the viewer's captures sample
    /// the other probes' current maps), in the capture's region frame.</summary>
    public ProbeList Around(ProbeCapture c, IReadOnlyList<ProbeCapture> batch, bool radiancePass)
    {
        var list = new ProbeList();
        long sky = c.Entry.Sky;
        var light = ProbeLighting.From(c.Env);
        if (c.Entry.Site.Kind == ProbeKind.Default) return list;   // itself only (added by the caller)
        var fresh = new Dictionary<ProbeSite, ProbeCapture>();
        if (radiancePass)
            foreach (var b in batch) if (b.Entry.Sky == sky && b != c) fresh[b.Entry.Site] = b;
        float range = CaptureFar * 1.733f;
        foreach (var (scene, offset) in ScenesNear(c.Scene, c.Center, range + 40f))
            foreach (var site in Sites(Probes(scene)))
            {
                if (site == c.Entry.Site) continue;
                if (Vector3.Distance(site.Origin + offset, c.Center) - site.Radius > range) continue;
                if (fresh.TryGetValue(site, out var b))
                {
                    // irradiance captured earlier in this frame; radiance only if it had one before
                    list.Local.Add((b.Entry, offset));
                    if (!b.WasComplete) list.NoRadiance.Add(b.Entry);
                }
                else if (_entries.TryGetValue((site, sky), out var e) && Usable(e, light) && !batch.Any(x => x.Entry == e && !x.WasComplete))
                    list.Local.Add((e, offset));
            }
        // the default probe nearest the capture
        list.Default = _entries.Values.Where(e => e.Site.Kind == ProbeKind.Default && e.Sky == sky && e.Site.Region == c.Scene.Source.Handle && Usable(e, light))
            .MinBy(e => Vector2.Distance(new Vector2(e.Site.Origin.X, e.Site.Origin.Y), new Vector2(c.Center.X, c.Center.Y)));
        return list;
    }

    /// <summary>Set by the owner to free maps once no in-flight frame uses them.</summary>
    public Action<ProbeMaps>? RetireMaps { get; set; }

    /// <summary>Forget a region's probes (it left the renderer).</summary>
    public void RemoveRegion(ulong handle)
    {
        if (_regions.Remove(handle, out var rp))
        {
            foreach (var s in rp.Terrain) Kill(s);
            foreach (var s in rp.Objects.Values) Kill(s);
            foreach (var s in rp.Manual.Values) Kill(s);
        }
        foreach (var key in _defaults.Keys.Where(k => k.Item1 == handle).ToList())
        {
            Kill(_defaults[key]);
            _defaults.Remove(key);
        }
    }

    /// <summary>Counts for diagnostics: sites by kind and complete entries.</summary>
    public string Describe()
    {
        int terrain = _regions.Values.Sum(r => r.Terrain.Length), objects = _regions.Values.Sum(r => r.Objects.Count), manual = _regions.Values.Sum(r => r.Manual.Count);
        return $"sites: terrain {terrain}, object {objects}, manual {manual}, default {_defaults.Count}; entries {_entries.Count} " +
               $"(complete {_entries.Values.Count(e => e.Complete)}), resident {_resident} ({Bytes / (1024 * 1024)} MB), queued {_queue.Count}, captures {CapturesDone}";
    }

    // --- GPU list ------------------------------------------------------------------------------------

    const int MaxGrid = 48;

    /// <summary>
    /// Build a probe list and its lookup grid (CPU side; <see cref="BuiltProbeList.Write"/> puts it in the arena).
    /// Irradiance capture passes get the viewer's refParams for that pass (no probe ambiance, radiance at half
    /// strength); captures of a probe also list the probe itself (<paramref name="self"/>), its radiance only once it
    /// has one. The default probe weighs in from 64 m above the camera that looks (<paramref name="defaultOrigin"/>).
    /// </summary>
    public static BuiltProbeList Build(ProbeList list, float skyAmbiance, bool irradiancePass, Vector3 defaultOrigin,
        (ProbeEntry Entry, bool RadianceValid)? self = null, BuiltProbeList? into = null)
    {
        // into: a list to fill again (the main view's, written to the frame's arena before the next frame builds it)
        var probes = into?.Probes ?? new List<GpuProbe>(list.Local.Count + 2);
        probes.Clear();
        float ambScale = irradiancePass ? 0f : 1f, radScale = irradiancePass ? 0.5f : 1f;
        GpuProbe Make(ProbeEntry e, Vector3 offset, bool radianceValid)
        {
            var s = e.Site;
            var box = s.Box ? Matrix4x4.CreateTranslation(-offset) * s.ToUnitBox : Matrix4x4.Identity;
            return new GpuProbe
            {
                Sphere = new Vector4(s.Origin + offset, s.Radius),
                Box = box,
                Params = new Vector4(MathF.Max(skyAmbiance, s.Ambiance) * ambScale, radScale, 1f, 0f),
                Radiance = radianceValid ? e.Maps!.RadianceSlot : -1,
                Irradiance = e.Maps!.IrradianceSlot,
                Priority = s.Priority,
            };
        }
        bool hasDefault = false;
        if (self is { } sf && sf.Entry.Site.Kind == ProbeKind.Default && sf.Entry.Maps != null)
        {
            probes.Add(Make(sf.Entry, Vector3.Zero, sf.RadianceValid));
            hasDefault = true;
        }
        else if (list.Default is { Maps: not null } d)
        {
            probes.Add(Make(d, Vector3.Zero, true));
            hasDefault = true;
        }
        else probes.Add(default);
        if (hasDefault)
        {
            var p0 = probes[0];
            p0.Sphere = new Vector4(defaultOrigin, DefaultRadius);
            probes[0] = p0;
        }
        foreach (var (e, offset) in list.Local)
            if (e.Maps != null) probes.Add(Make(e, offset, !list.NoRadiance.Contains(e)));
        if (self is { } sl && sl.Entry.Site.Kind != ProbeKind.Default && sl.Entry.Maps != null)
            probes.Add(Make(sl.Entry, Vector3.Zero, sl.RadianceValid));

        // grid over the local probes' footprints
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        for (int i = 1; i < probes.Count; i++)
        {
            var (bmin, bmax) = Footprint(probes[i]);
            min = Vector2.Min(min, bmin);
            max = Vector2.Max(max, bmax);
        }
        var b = into ?? new BuiltProbeList { Probes = probes };
        b.HasDefault = hasDefault;
        b.Cells.Clear();
        (b.GridOrigin, b.Cell, b.GridW, b.GridH) = (default, 16f, 0, 0);
        if (probes.Count > 1)
        {
            var size = max - min;
            float cell = MathF.Max(16f, MathF.Max(size.X, size.Y) / MaxGrid);
            int gw = Math.Clamp((int)MathF.Ceiling(size.X / cell), 1, MaxGrid);
            int gh = Math.Clamp((int)MathF.Ceiling(size.Y / cell), 1, MaxGrid);
            // each cell's probes, in probe order: counted, then placed (no list per cell)
            int cells = gw * gh;
            var count = _gridCounts ??= new int[MaxGrid * MaxGrid];
            Array.Clear(count, 0, cells);
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    uint first = (uint)(cells + 1);
                    int total = 0;
                    for (int i = 0; i < cells; i++) { b.Cells.Add(first + (uint)total); int n = count[i]; count[i] = total; total += n; }
                    b.Cells.Add(first + (uint)total);
                    CollectionsMarshal.SetCount(b.Cells, cells + 1 + total);
                }
                var at = CollectionsMarshal.AsSpan(b.Cells);
                for (int i = 1; i < probes.Count; i++)
                {
                    var (bmin, bmax) = Footprint(probes[i]);
                    int x0 = Math.Clamp((int)MathF.Floor((bmin.X - min.X) / cell), 0, gw - 1), x1 = Math.Clamp((int)MathF.Floor((bmax.X - min.X) / cell), 0, gw - 1);
                    int y0 = Math.Clamp((int)MathF.Floor((bmin.Y - min.Y) / cell), 0, gh - 1), y1 = Math.Clamp((int)MathF.Floor((bmax.Y - min.Y) / cell), 0, gh - 1);
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                            if (pass == 0) count[y * gw + x]++;
                            else at[cells + 1 + count[y * gw + x]++] = (uint)i;
                }
            }
            b.GridOrigin = min;
            b.Cell = cell;
            b.GridW = gw;
            b.GridH = gh;
        }
        else b.Cells.Add(0);
        return b;
    }

    [ThreadStatic] static int[]? _gridCounts;

    static (Vector2 Min, Vector2 Max) Footprint(in GpuProbe p)
    {
        var c = new Vector2(p.Sphere.X, p.Sphere.Y);
        if (p.Priority >= 0) return (c - new Vector2(p.Sphere.W), c + new Vector2(p.Sphere.W));
        // box probe: the unit cube's corners back in region space
        Matrix4x4.Invert(p.Box, out var toRegion);
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var q = Vector3.Transform(new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1), toRegion);
            min = Vector2.Min(min, new Vector2(q.X, q.Y));
            max = Vector2.Max(max, new Vector2(q.X, q.Y));
        }
        return (min, max);
    }

    public void Dispose()
    {
        foreach (var e in _entries.Values) e.Maps?.Dispose();
        _entries.Clear();
        _system.Dispose();
    }
}

/// <summary>A probe list ready for the arena (see <see cref="ProbeManager.Build"/>).</summary>
public sealed class BuiltProbeList
{
    public required List<GpuProbe> Probes;
    public bool HasDefault;
    public readonly List<uint> Cells = new();
    public Vector2 GridOrigin;
    public float Cell = 16f;
    public int GridW, GridH;

    public ulong Bytes => (ulong)(Probes.Count * Marshal.SizeOf<GpuProbe>() + Cells.Count * 4) + 1024;

    public void Write(FrameArena arena, ref GpuSky sky)
    {
        ulong baseAddr = arena.Device.Address;
        ulong listAddr = baseAddr + arena.Write<GpuProbe>(CollectionsMarshal.AsSpan(Probes));
        ulong cellAddr = baseAddr + arena.Write<uint>(CollectionsMarshal.AsSpan(Cells));
        sky.probes_on = 1;
        sky.probe_default = HasDefault ? 1 : 0;
        sky.probe_list_lo = (uint)listAddr;
        sky.probe_list_hi = (uint)(listAddr >> 32);
        sky.probe_cells_lo = (uint)cellAddr;
        sky.probe_cells_hi = (uint)(cellAddr >> 32);
        sky.probe_grid_origin = GridOrigin;
        sky.probe_grid_cell = Cell;
        sky.probe_grid_w = GridW;
        sky.probe_grid_h = GridH;
        sky.max_probe_lod = ProbeSystem.MaxProbeLod;
    }
}
