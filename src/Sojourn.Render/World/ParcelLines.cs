using System.Numerics;
using System.Runtime.InteropServices;
using LibreMetaverse;

namespace Sojourn.Render.World;

/// <summary>A vertex of the parcel lines' triangles (Shaders/parcel_lines.vert): where, its texture coordinate, colour
/// (RGBA8, as the viewer's gGL.color4) and texture slot (-1: none).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ParcelLineVertex
{
    public Vector3 Pos;
    public Vector2 Uv;
    public uint Color;
    public int Tex;
}

/// <summary>
/// Property lines and ban lines, as the viewer draws them over the finished image (render_hud_elements, after
/// renderFinalize; depth-tested against the scene). Ported from the Second Life viewer (LGPL 2.1, Linden Research,
/// Inc.):
/// - Property lines (LLViewerParcelOverlay::updatePropertyLines, addPropertyLine, renderPropertyLines; ShowPropertyLines,
///   off by default): from each region's parcel overlay (the ParcelOverlay message, 4 m cells), a 6.25 cm strip along
///   every parcel edge that isn't public land, following the ground in 1 m steps, in the owner's colour (yours green,
///   your group's teal, others' red, for sale orange, auction purple, 40% opaque), kept above water while you are,
///   within 256 m and lifted toward you by 1 cm a metre of your height over the ground.
/// - Ban lines (LLViewerParcelMgr::renderCollisionSegments; ShowBanLines): the outline of the parcels a region reports
///   you'd collide with (ParcelProperties with a collision sequence id, its bitmap), walls of yellow tape (green L$
///   tape for a parcel that sells passes and you're not on its list) 50 m high, 5000 m where you're banned or the
///   region blocks flying over, more opaque the nearer you are (the viewer's asymmetric falloff, kept). Shown for 10 s
///   after the region refuses you ("Cannot enter parcel"; the default), or for 1 s after each report of being near
///   (proximity), or never.
/// Distances that decide what's drawn are from the head's centre (docs/rendering.md > Distances from the head): the
/// viewer measures the ban walls' fade from the avatar and culls property lines behind the camera's plane.
/// Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed partial class ParcelLines
{
    public enum BanMode { Hide, Collision, Proximity }

    /// <summary>The settings (any thread): property lines shown; when ban lines are.</summary>
    public volatile bool ShowProperty;
    volatile int _banMode = (int)BanMode.Collision;
    public BanMode Bans { get => (BanMode)_banMode; set => _banMode = (int)value; }

    // llparcel.h
    const float GridStep = 4f;
    const byte ColorMask = 0x07, Public = 0, Owned = 1, Group = 2, Self = 3, ForSale = 4, Auction = 5;
    const byte WestLine = 0x40, SouthLine = 0x80;
    const float LineWidth = 0.0625f;
    // ban lines: llparcel.h PARCEL_HEIGHT, BAN_HEIGHT; llviewerparcelmgr.cpp's draw times
    const float ParcelHeight = 50f, BanHeight = 5000f;
    const double OnCollisionSeconds = 10, OnProximitySeconds = 1;
    const ulong BlockFlyover = 1UL << 27;   // REGION_FLAGS_BLOCK_FLYOVER (LibreMetaverse names it AbuseEmailToEstateOwner)
    public const int CollisionNotInGroup = -20000, CollisionBanned = -30000, CollisionNotOnList = -40000;   // llparcel.h *_SEQ_ID

    // colors.xml PropertyColor*: RGBA8 as gGL.color4ubv takes them
    static readonly uint[] Colors = new uint[8];
    static ParcelLines()
    {
        static uint C(float r, float g, float b, float a) =>
            (uint)MathF.Round(r * 255) | (uint)MathF.Round(g * 255) << 8 | (uint)MathF.Round(b * 255) << 16 | (uint)MathF.Round(a * 255) << 24;
        Colors[Self] = C(0, 1, 0, 0.4f);
        Colors[Owned] = C(1, 0, 0, 0.4f);
        Colors[Group] = C(0, 0.72f, 0.72f, 0.4f);
        Colors[ForSale] = C(1, 0.5f, 0, 0.4f);
        Colors[Auction] = C(0.5f, 0, 1, 0.4f);
    }

    // the viewer's ban tape (skins/default/textures/world/NoEntryLines.png and NoEntryPassLines.png, shipped beside the
    // renderer as J2C), served under ids of our own
    public static readonly UUID BanTexture = new("6d2f3e1a-5b0c-4c41-9a51-0b7a6f1e3001");
    public static readonly UUID PassTexture = new("6d2f3e1a-5b0c-4c41-9a51-0b7a6f1e3002");
    sealed class LocalSource : IAssetSource
    {
        static string PathOf(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Textures", name);
        public Task<byte[]?> GetTextureAsync(UUID id, CancellationToken ct) => Task.FromResult(
            id == BanTexture && File.Exists(PathOf("NoEntryLines.j2c")) ? File.ReadAllBytes(PathOf("NoEntryLines.j2c"))
            : id == PassTexture && File.Exists(PathOf("NoEntryPassLines.j2c")) ? File.ReadAllBytes(PathOf("NoEntryPassLines.j2c")) : null);
        public Task<byte[]?> GetMeshAsync(UUID id, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task<byte[]?> GetAssetAsync(UUID id, AssetType type, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    }
    readonly LocalSource _local = new();

    // --- ban lines' input (network threads) ---------------------------------------------------------------------

    sealed record Collision(ulong Region, byte[] Bitmap, int Kind, bool UsePass);
    volatile Collision? _collision;
    long _showUntil;   // Stopwatch ticks

    /// <summary>A region's ParcelProperties with a collision sequence id: the parcels you'd collide with
    /// (processParcelProperties: their bitmap's outline; a proximity reset of the timer).</summary>
    public void Collided(ulong region, byte[] bitmap, int sequenceId, bool usePassList)
    {
        _collision = new Collision(region, bitmap, sequenceId, usePassList);
        if (Bans == BanMode.Proximity) ShowFor(OnProximitySeconds);
    }

    /// <summary>The region refused you a parcel (an alert with "Cannot enter parcel", or an access-blocked notice).</summary>
    public void Refused()
    {
        if (Bans == BanMode.Collision) ShowFor(OnCollisionSeconds);
    }

    /// <summary>Ban lines up for this long from now (unless hidden).</summary>
    public void ShowFor(double seconds) =>
        Interlocked.Exchange(ref _showUntil, System.Diagnostics.Stopwatch.GetTimestamp() + (long)(seconds * System.Diagnostics.Stopwatch.Frequency));

    /// <summary>Whether ban lines are up now, and for which region (for SojournEnvironment's answers and tests).</summary>
    public (bool Shown, ulong Region, int Kind) BanState =>
        (_collision is { } c && Bans != BanMode.Hide && System.Diagnostics.Stopwatch.GetTimestamp() < Interlocked.Read(ref _showUntil), _collision?.Region ?? 0, _collision?.Kind ?? 0);

    // --- property lines, per region (render thread) ----------------------------------------------------------------

    sealed class RegionLines
    {
        public byte[] Overlay = Array.Empty<byte>();
        public long TerrainVersion = -1;
        public float Water = float.NaN;
        public readonly List<(Vector3[] Strip, uint Color)> Edges = new();
    }
    readonly Dictionary<ulong, RegionLines> _regions = new();

    /// <summary>This frame's lines (render thread), in the coordinates of <paramref name="viewRegion"/>: property lines of
    /// every region within reach, then ban walls; <paramref name="offset"/> gives a region's offset from the view's.
    /// Returns how many of the vertices are property lines'.</summary>
    public int Emit(RenderWorld world, ulong viewRegion, Vector3 head, List<ParcelLineVertex> into, Func<ulong, ulong, Vector3> offset)
    {
        var view = world.Region(viewRegion);
        if (ShowProperty && view != null)
        {
            // the camera's height over the ground, for the lift toward it (renderPropertyLines)
            float ground = view.TerrainHeight(head.X, head.Y);
            float lift = 0.01f * Math.Clamp(head.Z - ground, 0f, 100f);
            float waterView = view.Source.Simulator.WaterHeight;
            foreach (var scene in world.Regions)
            {
                if (!scene.Source.HasSimulator) continue;
                var off = offset(viewRegion, scene.Source.Handle);
                var local = head - off;
                float size = scene.Source.Simulator.SizeX;
                if (local.X < -256 || local.Y < -256 || local.X > size + 256 || local.Y > size + 256) continue;
                var lines = Lines(scene);
                float waterZ = scene.Source.Simulator.WaterHeight + 0.01f;
                bool clampToWater = head.Z >= waterView + 0.01f;   // under water, lines show below it too
                foreach (var (strip, color) in lines.Edges)
                {
                    var mid = strip[strip.Length >> 1];
                    if (Vector2.DistanceSquared(new(mid.X, mid.Y), new(local.X, local.Y)) > 256f * 256f) continue;
                    for (int i = 0; i < strip.Length; i++)
                    {
                        var p = strip[i];
                        if (clampToWater && p.Z < waterZ) p.Z = waterZ;
                        p += off;
                        var toward = head - p;
                        float d = toward.Length();
                        if (d > 1e-4f) p += toward * (lift / d);
                        p.Z += 0.01f;
                        Strip(into, i, new ParcelLineVertex { Pos = p, Color = color, Tex = -1 });
                    }
                }
            }
        }
        EmitSelection(world, viewRegion, into, offset);   // ParcelLines.Selection.cs
        int property = into.Count;
        EmitBans(world, viewRegion, head, into, offset);
        return property;
    }

    /// <summary>A triangle strip's vertex <paramref name="i"/> as a triangle list (the strip's triangles in order).</summary>
    static void Strip(List<ParcelLineVertex> into, int i, ParcelLineVertex v)
    {
        if (i >= 3)
        {
            // a new triangle from the strip's last two vertices
            var a = into[^2];
            var b = into[^1];
            into.Add(a);
            into.Add(b);
        }
        into.Add(v);
    }

    RegionLines Lines(RegionScene scene)
    {
        var sim = scene.Source.Simulator;
        if (!_regions.TryGetValue(scene.Source.Handle, out var r)) _regions[scene.Source.Handle] = r = new RegionLines();
        long terrain = scene.Source.TerrainVersion();
        var overlay = sim.ParcelOverlay;
        if (r.TerrainVersion == terrain && r.Water == sim.WaterHeight && overlay.AsSpan().SequenceEqual(r.Overlay)) return r;
        r.Overlay = overlay.ToArray();
        r.TerrainVersion = terrain;
        r.Water = sim.WaterHeight;
        r.Edges.Clear();
        int grids = Math.Min((int)(sim.SizeX / GridStep), (int)MathF.Sqrt(overlay.Length));
        float water = sim.WaterHeight;
        for (int row = 0; row < grids; row++)
            for (int col = 0; col < grids; col++)
            {
                byte cell = overlay[row * grids + col];
                int kind = cell & ColorMask;
                if (kind is not (Self or Group or Owned or ForSale or Auction)) continue;
                uint color = Colors[kind];
                float left = col * GridStep, right = left + GridStep, bottom = row * GridStep, top = bottom + GridStep;
                if ((cell & WestLine) != 0) r.Edges.Add((Edge(scene, water, left, bottom, 0, 1, LineWidth, 0), color));
                if (col == grids - 1 || (overlay[row * grids + col + 1] & WestLine) != 0) r.Edges.Add((Edge(scene, water, right, bottom, 0, 1, -LineWidth, 0), color));
                if ((cell & SouthLine) != 0) r.Edges.Add((Edge(scene, water, left, bottom, 1, 0, 0, LineWidth), color));
                if (row == grids - 1 || (overlay[(row + 1) * grids + col] & SouthLine) != 0) r.Edges.Add((Edge(scene, water, left, top, 1, 0, 0, -LineWidth), color));
            }
        return r;
    }

    /// <summary>LLViewerParcelOverlay::addPropertyLine: a strip along one 4 m edge, its vertices on the ground, split
    /// where it crosses the water's height.</summary>
    static Vector3[] Edge(RegionScene scene, float waterZ, float startX, float startY, float dx, float dy, float tickDx, float tickDy)
    {
        var v = new List<Vector3>(16);
        float outX = startX, outY = startY, inX = startX + tickDx, inY = startY + tickDy, outZ, inZ;
        float H(float x, float y) => scene.TerrainHeight(x, y);
        void Split(Vector3 start, float x, float y, float z, float part) =>
            v.Add(new Vector3(start.X + (x - start.X) * part, start.Y + (y - start.Y) * part, start.Z + (z - start.Z) * part));
        void CheckForSplit()
        {
            var lastOut = v[^1];
            float z0 = lastOut.Z, z1 = outZ;
            if ((z0 >= waterZ && z1 >= waterZ) || (z0 < waterZ && z1 < waterZ)) return;
            float part = (waterZ - z0) / (z1 - z0);
            var lastIn = v[^2];
            Split(lastIn, inX, inY, inZ, part);
            Split(lastOut, outX, outY, outZ, part);
        }

        outZ = H(outX, outY);
        v.Add(new Vector3(outX, outY, outZ));
        inX += dx * LineWidth; inY += dy * LineWidth;
        outX += dx * LineWidth; outY += dy * LineWidth;
        inZ = H(inX, inY);
        outZ = H(outX, outY);
        v.Add(new Vector3(inX, inY, inZ));
        v.Add(new Vector3(outX, outY, outZ));
        inX += dx * (dx - LineWidth); inY += dy * (dy - LineWidth);
        outX += dx * (dx - LineWidth); outY += dy * (dy - LineWidth);
        for (int i = 1; i < (int)GridStep; i++)
        {
            inZ = H(inX, inY);
            outZ = H(outX, outY);
            CheckForSplit();
            v.Add(new Vector3(inX, inY, inZ));
            v.Add(new Vector3(outX, outY, outZ));
            inX += dx; inY += dy;
            outX += dx; outY += dy;
        }
        inX -= dx * LineWidth; inY -= dy * LineWidth;
        outX -= dx * LineWidth; outY -= dy * LineWidth;
        inZ = H(inX, inY);
        outZ = H(outX, outY);
        CheckForSplit();
        v.Add(new Vector3(inX, inY, inZ));
        v.Add(new Vector3(outX, outY, outZ));
        outX += dx * LineWidth; outY += dy * LineWidth;
        outZ = H(outX, outY);
        v.Add(new Vector3(outX, outY, outZ));
        return v.ToArray();
    }

    // --- ban walls (render thread) ---------------------------------------------------------------------------------

    void EmitBans(RenderWorld world, ulong viewRegion, Vector3 head, List<ParcelLineVertex> into, Func<ulong, ulong, Vector3> offset)
    {
        var (shown, _, _) = BanState;
        if (!shown || _collision is not { } c || world.Region(c.Region) is not { } scene || !scene.Source.HasSimulator) return;
        var sim = scene.Source.Simulator;
        bool pass = c.UsePass && c.Kind == CollisionNotOnList;
        var tex = world.Textures.Request(pass ? PassTexture : BanTexture, _local);
        if (tex.State != TexState.Ready) return;   // (no untextured flash while the tape loads)
        int slot = tex.Slot;
        float height = c.Kind == CollisionBanned || ((ulong)sim.Flags & BlockFlyover) != 0 ? BanHeight : ParcelHeight;
        var off = offset(viewRegion, c.Region);
        var pos = head - off;   // (the viewer's gAgent.getPositionAgent)
        int per = Math.Max(1, (int)(sim.SizeX / GridStep));
        int stride = per + 1;
        if (c.Bitmap.Length * 8 < per * per) return;
        var segments = new byte[stride * stride];
        // writeSegmentsFromBitmap: each set cell toggles its four edges, leaving the outline
        const byte South = 1, West = 2;
        for (int y = 0; y < per; y++)
            for (int x = 0; x < per; x++)
            {
                if ((c.Bitmap[(x + y * per) / 8] & (1 << ((x + y * per) % 8))) == 0) continue;
                int o = x + y * stride;
                segments[o] ^= South; segments[o + stride] ^= South;
                segments[o] ^= West; segments[o + 1] ^= West;
            }
        const float MaxAlpha = 0.95f, DistOffset = 5f, MinDistSq = 25f, MaxDistSq = 169f;
        float Alpha(float dist) => Math.Clamp(dist < MinDistSq ? MaxAlpha : dist > MaxDistSq ? 0f : 30f / dist, 0f, MaxAlpha);
        float width = sim.SizeX;
        for (int y = 0; y < stride; y++)
            for (int x = 0; x < stride; x++)
            {
                byte m = segments[x + y * stride];
                if ((m & South) != 0)
                {
                    float x1 = x * GridStep, y1 = y * GridStep, x2 = x1 + GridStep, y2 = y1;
                    float ddy = pos.Y - y1 + DistOffset;
                    float ddx = pos.X < x1 ? pos.X - x1 : pos.X > x2 ? pos.X - x2 : 0;
                    float a = Alpha(ddx * ddx + ddy * ddy);
                    if (a > 0) Wall(scene, into, off, width, x1 + 0.1f, y1 + 0.1f, x2 + 0.1f, y2 + 0.1f, height, pos.Y - y1 < 0 ? Dir.South : Dir.North, a, slot);
                }
                if ((m & West) != 0)
                {
                    float x1 = x * GridStep, y1 = y * GridStep, x2 = x1, y2 = y1 + GridStep;
                    float ddx = pos.X - x1 + DistOffset;
                    float ddy = pos.Y < y1 ? pos.Y - y1 : pos.Y > y2 ? pos.Y - y2 : 0;
                    float a = Alpha(ddx * ddx + ddy * ddy);
                    if (a > 0) Wall(scene, into, off, width, x1 + 0.1f, y1 + 0.1f, x2 + 0.1f, y2 + 0.1f, height, pos.X - x1 > 0 ? Dir.West : Dir.East, a, slot);
                }
            }
    }

    enum Dir { North, South, East, West }

    /// <summary>LLViewerParcelMgr::renderOneSegment (a wall, height >= 1): two triangles from the ground up, the top
    /// stepped to the higher end, tape coordinates half a texture a metre.</summary>
    static void Wall(RegionScene scene, List<ParcelLineVertex> into, Vector3 off, float regionWidth, float x1, float y1, float x2, float y2,
        float height, Dir dir, float alpha, int tex)
    {
        float border = regionWidth - 0.1f;
        float z1 = scene.TerrainHeight(MathF.Min(x1, border), MathF.Min(y1, border));
        float z2 = scene.TerrainHeight(MathF.Min(x2, border), MathF.Min(y2, border));
        float t1 = dir switch { Dir.West => y1, Dir.South => x1, Dir.East => y2, _ => x2 };
        float t2 = dir switch { Dir.West => y2, Dir.South => x2, Dir.East => y1, _ => x1 };
        float z = MathF.Max(z2 + height, z1 + height);
        uint color = 0x00FFFFFFu | (uint)MathF.Round(alpha * 255) << 24;
        void V(float x, float y, float zz, float s) =>
            into.Add(new ParcelLineVertex { Pos = new Vector3(x, y, zz) + off, Uv = new Vector2(s * 0.5f + 0.5f, zz * 0.5f), Color = color, Tex = tex });
        V(x1, y1, z1, t1); V(x2, y2, z2, t2); V(x2, y2, z, t2);
        V(x1, y1, z1, t1); V(x2, y2, z, t2); V(x1, y1, z, t1);
    }

    /// <summary>A region gone: its property lines' cache with it.</summary>
    public void Forget(ulong region) => _regions.Remove(region);
}
