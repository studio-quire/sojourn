// Crowds: how many avatars are drawn whole, the rest as impostors, and the viewer's complexity limit with its jelly
// dolls. Rules from newview/llvoavatar.cpp (calculateUpdateRenderComplexity, accountRenderComplexityForObject,
// calculateBodyPartsComplexity, isTooComplex, isVisuallyMuted, calcMutedAVColor), llvovolume.cpp (getRenderCost,
// getTextureCost), llmeshrepository.cpp (LLMeshCostData) and llvolume.cpp (getLoDTriangleCounts). LGPL 2.1, Linden
// Research, Inc. The impostors themselves are ours (Frame/Renderer.Impostors.cs): the viewer's are flat cards, which a
// headset shows as cardboard; ours keep their depth for each eye.
using System.Collections.Concurrent;
using LibreMetaverse;
using Sojourn.Render.Frame;
using Sojourn.Render.Bodies;
using Sojourn.Render.Geometry;
using Sojourn.Render.Gpu;

namespace Sojourn.Render.World;

/// <summary>The person's choice for one avatar (the viewer's Render Normally / Always Render Fully / Do Not Render).</summary>
public enum AvatarRender : byte { Normal, Always, Never }

/// <summary>One avatar as drawn lately, for the agent API: its complexity (and whether every attachment's cost is known
/// yet), how it's drawn (full, impostor, jelly, checking, loading) and the person's choice for it.</summary>
public readonly record struct AvatarStatus(UUID Id, string Name, float Distance, int Complexity, bool Known, float AttachmentArea,
    string Drawn, AvatarRender Setting);

/// <summary>The crowd settings the renderer reads every frame (the view section's avatar rows, SojournSettings).</summary>
public sealed class AvatarLimits
{
    /// <summary>RenderAvatarMaxComplexity: over it an avatar is a jelly doll; 0, no limit (and no surface area limit).
    /// The viewer's High-Ultra and Ultra presets: 350,000.</summary>
    public volatile int MaxComplexity = 350_000;
    /// <summary>RenderAvatarMaxNonImpostors: the nearest this many avatars are drawn whole, the rest as impostors; 0,
    /// all whole. The viewer's High-Ultra and Ultra presets: 16.</summary>
    public volatile int MaxFull = 16;
    /// <summary>RenderAutoMuteSurfaceAreaLimit (m²): attachments covering more are muted with the complexity limit.</summary>
    public volatile float MaxAttachmentArea = 1000f;
    /// <summary>Impostor captures a frame at most (each draws one avatar into its tile once).</summary>
    public volatile int CapturesPerFrame = 4;
    /// <summary>How often impostors' poses are brought up to date, as a share of their usual rate (the load governor
    /// lowers it while frames run long: far avatars move in coarser steps; Realtime/LoadGovernor.cs).</summary>
    public volatile float RefreshScale = 1f;
    public const float MinImpostorHz = 8f;
    /// <summary>Avatars further than this (metres) are impostors even within <see cref="MaxFull"/> (the load governor's
    /// trim while frames run long); an impostor comes back whole only nearer than 87% of it. Infinity: none. Not for
    /// those the person asked to see whole ("always"), nor when every avatar is to be whole (MaxFull 0).</summary>
    public volatile float ImpostorBeyond = float.PositiveInfinity;
    /// <summary>The person's own avatar: never limited.</summary>
    public UUID Self;
    public readonly ConcurrentDictionary<UUID, AvatarRender> Overrides = new();
    /// <summary>Every avatar drawn lately, refreshed twice a second.</summary>
    public volatile IReadOnlyList<AvatarStatus> Status = Array.Empty<AvatarStatus>();
    /// <summary>One avatar's full report on request (Avatars.Inspect.cs).</summary>
    public readonly AvatarInspection Inspection = new();
    /// <summary>Reset skeleton, refresh attachments, resync animations (Avatars.Reset.cs).</summary>
    public readonly AvatarFixes Fixes = new();
}

public sealed partial class Avatars
{
    public AvatarLimits Limits { get; } = new();

    /// <summary>What a worn object of an avatar does this frame (RegionScene asks, attachment by attachment): drawn,
    /// drawn into the avatar's impostor capture, only its lights (an impostor between captures), or nothing (a jelly doll,
    /// or not posed).</summary>
    public enum Worn : byte { None, Full, Capture, Lights }

    readonly Dictionary<(ulong Region, uint LocalId), (Worn Mode, ImpostorCapture? Cap)> _worn = new();

    public (Worn Mode, ImpostorCapture? Cap) WornOf(ulong region, uint avatarLocalId) =>
        _worn.TryGetValue((region, avatarLocalId), out var w) ? w : (Worn.None, null);

    long _statusAt;
    int _captured;
    public int ImpostorCount { get; private set; }
    public int JellyCount { get; private set; }
    public int CapturesLastFrame => _captured;

    sealed class Entry
    {
        public RegionScene Scene = null!;
        public Vector3 Offset, Pos;
        public Avatar Av = null!;
        public Quaternion Rot;
        public bool Seated, Visible, Clone, Self;
        public int Casters;
        public float Dist, Score;
        public Draw How;
        public string Drawn = "full";
        public Body? Body;
    }

    readonly List<Entry> _entries = new();
    int _entryCount;

    Entry NextEntry()
    {
        if (_entryCount == _entries.Count) _entries.Add(new Entry());
        var e = _entries[_entryCount++];
        (e.Body, e.Clone, e.Self, e.Score, e.How, e.Drawn) = (null, false, false, 0, Draw.Full, "full");
        return e;
    }

    /// <summary>Decide how each avatar is drawn this frame: jelly dolls by complexity and the person's choices, the
    /// nearest <see cref="AvatarLimits.MaxFull"/> whole, the rest impostors, a few of them captured afresh.</summary>
    void Plan(ViewSetup v, DrawList dl, long tick)
    {
        var lim = Limits;
        int maxFull = lim.MaxFull;
        int maxCost = lim.MaxComplexity;
        var lodView = v.LodView ?? v;
        float focal = lodView.Height * 0.5f / MathF.Tan(lodView.VFov * 0.5f);
        var ranked = _ranked;
        ranked.Clear();
        int jellies = 0;
        for (int i = 0; i < _entryCount; i++)
        {
            var e = _entries[i];
            if (e.Clone) continue;
            var av = e.Av;
            var setting = lim.Overrides.TryGetValue(av.ID, out var o) ? o : AvatarRender.Normal;
            float px = 2.2f / MathF.Max(e.Dist, 0.5f) * focal;
            // an avatar still being judged is a cloud until every worn mesh's header is in: its meshes go ahead of
            // scenery the same size on screen
            var (cost, known, area) = Complexity(e.Scene, av, MathF.PI * px * px * 0.25f * (e.Self ? 1 : 4), tick);
            // the person's own avatar is judged (the viewer shows its own complexity) but never limited
            if (e.Self) continue;
            if (setting == AvatarRender.Never
                || (setting != AvatarRender.Always && maxCost > 0 && known && (cost > maxCost || (lim.MaxAttachmentArea > 0 && area > lim.MaxAttachmentArea))))
            {
                e.How = Draw.Jelly;
                e.Drawn = "jelly";
                jellies++;
                continue;
            }
            if (setting != AvatarRender.Always && maxCost > 0 && !known)
            {
                // its attachments are judged before any of them is built: a crasher can't get as far as being drawn
                e.Drawn = "checking";
                e.How = Draw.Cloud;
                continue;
            }
            if (StillArriving(e.Scene, av, tick))
            {
                e.Drawn = "loading";
                e.How = Draw.Cloud;
                continue;
            }
            e.Body = _bodies.GetValueOrDefault(av.ID);   // before ranking: Rank reads whether it was an impostor
            ranked.Add(e);
        }
        JellyCount = jellies;
        _jelly.Clear();
        for (int i = 0; i < _entryCount; i++)
            if (_entries[i].How == Draw.Jelly) _jelly.Add((_entries[i].Scene.Source.Handle, _entries[i].Av.LocalID));

        // the nearest stay whole; an impostor ranks a little farther than it is, so the one at the boundary doesn't flicker
        ranked.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
        // under lag, the far ones too, each with the same 15% band (the governor's trim)
        float beyond = lim.ImpostorBeyond;
        bool Far(Entry e) => maxFull > 0 && Rank(e) > beyond && !(lim.Overrides.TryGetValue(e.Av.ID, out var s) && s == AvatarRender.Always);
        bool anyFar = false;
        if (!float.IsPositiveInfinity(beyond))
            foreach (var e in ranked) anyFar |= Far(e);
        var atlas = maxFull > 0 && (ranked.Count > maxFull || anyFar) ? _world.Gpu.Renderer.Atlas : null;
        var due = _due;
        due.Clear();
        int impostors = 0;
        for (int i = 0; i < ranked.Count; i++)
        {
            var e = ranked[i];
            var body = _bodies.GetValueOrDefault(e.Av.ID);
            e.Body = body;
            // whole: within the count, very near, or nothing to show an impostor of yet
            if (atlas == null || (i < maxFull && !(anyFar && Far(e))) || e.Dist < 2.5f || body?.Shape == null)
            {
                if (body != null) body.WasImpostor = false;
                continue;
            }
            // beyond the count and out of view: nothing (an impostor casts no shadow)
            if (!e.Visible)
            {
                e.How = Draw.Cloud;
                e.Drawn = "impostor";
                body.WasImpostor = true;
                continue;
            }
            impostors++;
            body.WasImpostor = true;
            e.Drawn = "impostor";
            float px = 2.2f / MathF.Max(e.Dist, 0.5f) * focal;
            int cls = ImpostorAtlas.ClassFor(px);
            if (!body.Captured || body.Tile.IsEmpty || body.Tile.Class > cls || body.Tile.Class < cls - 1)
                e.Score = 1000 - e.Dist;   // no capture, or one at the wrong size: first, nearest first
            else
            {
                var dir = Vector3.Normalize(v.Position - (e.Pos + body.BoxFromRoot));
                float angle = MathF.Acos(Math.Clamp(Vector3.Dot(dir, body.CapDir), -1, 1)) * (180f / MathF.PI);
                // the larger on screen, the more often its pose is brought up to date (4 to 30 times a second); never
                // under 8 for the governor: at a quarter rate far dancers hopped about once a second, which the user
                // saw as them teleporting (Totally 80s, 2026-10-27). CapturesPerFrame still caps the work.
                float hz = MathF.Max(Math.Clamp(px / 10f, 4f, 30f) * Math.Clamp(lim.RefreshScale, 0.1f, 1f), AvatarLimits.MinImpostorHz);
                float age = (tick - body.CapturedAt) / 1000f * hz;
                float near = MathF.Abs(e.Dist - body.CapDist) / MathF.Max(body.CapDist, 0.5f) / 0.15f;
                e.Score = MathF.Max(age, MathF.Max(angle / 2f, near));
            }
            e.How = body.Captured && !body.Tile.IsEmpty ? Draw.Stale : Draw.Full;
            if (e.Score >= 1) due.Add(e);
        }
        ImpostorCount = impostors;
        due.Sort((a, b) => b.Score.CompareTo(a.Score));
        _captured = 0;
        for (int i = 0; i < due.Count && _captured < lim.CapturesPerFrame && atlas != null; i++)
        {
            var e = due[i];
            var body = e.Body!;
            float px = 2.2f / MathF.Max(e.Dist, 0.5f) * focal;
            int cls = ImpostorAtlas.ClassFor(px);
            if (body.Tile.IsEmpty || body.Tile.Class != cls)
            {
                if (!body.Tile.IsEmpty) atlas.Free(body.Tile);
                body.Tile = default;
                body.Captured = false;
                if (!atlas.Allocate(cls, out var tile)) { e.How = Draw.Full; e.Drawn = "full"; continue; }
                body.Tile = tile;
            }
            e.How = Draw.Capture;
            _captures[e] = dl.AddCapture(body.Tile);
            _captured++;
        }
        // impostors no longer: their tiles go back
        if (_world.Gpu.Renderer.HasAtlas)
            foreach (var b in _bodies.Values)
                if (!b.WasImpostor && !b.Tile.IsEmpty)
                {
                    _world.Gpu.Renderer.Atlas.Free(b.Tile);
                    b.Tile = default;
                    b.Captured = false;
                }
    }

    sealed class Arrival
    {
        public int Present = -1;
        public long Since;
        public bool Shown;
    }

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<Body, Arrival> _arrivals = new();

    /// <summary>LLVOAvatar::updateIsFullyLoaded's wait for attachments: until the avatar is first shown it stays a cloud
    /// while attachments its appearance lists haven't arrived, for up to a minute after the last one came
    /// (MAX_ATTACHMENT_WAIT_TIME_SEC). Without it the system body showed in the meantime, its head shaped by sliders meant
    /// for the mesh head still on its way, which bakes on mesh would hide.</summary>
    bool StillArriving(RegionScene scene, Avatar av, long tick)
    {
        if (!_bodies.TryGetValue(av.ID, out var body)) return false;
        var arrival = _arrivals.GetOrCreateValue(body);
        // the attachment list comes with the appearance: before it, nothing to wait for yet (a cloud anyway)
        if (arrival.Shown || av.VisualParameters is not { Length: > 1 }) return false;
        // counted, as the viewer counts them (getAttachmentCount against mSimAttachments.size())
        int listed, present = 0;
        lock (av) listed = av.Attachments?.Count ?? 0;
        if (listed > 0)
            foreach (var obj in scene.AttachmentsOf(av.LocalID))
                if (obj.Prim.ParentID == av.LocalID) present++;
        if (present >= listed) return !(arrival.Shown = true);
        if (present != arrival.Present) (arrival.Present, arrival.Since) = (present, tick);
        if (tick - arrival.Since > 60_000) return !(arrival.Shown = true);
        return true;
    }

    readonly List<Entry> _ranked = new(), _due = new();
    readonly HashSet<(ulong Region, uint LocalId)> _jelly = new();

    /// <summary>Drawn as a jelly doll this frame (its worn particles aren't drawn either: RegionScene.OnJellyDoll).</summary>
    public bool IsJelly(ulong region, uint avatarLocalId) => _jelly.Count > 0 && _jelly.Contains((region, avatarLocalId));
    readonly Dictionary<Entry, ImpostorCapture> _captures = new();

    static float Rank(Entry e) => e.Body?.WasImpostor == true ? e.Dist * 1.15f : e.Dist;

    /// <summary>For the view line: impostors and jelly dolls, when there are any.</summary>
    public string Describe()
    {
        // what the worn meshes cost the frame: meshes posed and the palettes they needed (shared by skin), and body
        // shapes rebuilt (each morphs and uploads a whole body) since the last line
        string worn = _lastRigged == 0 && _shapesBuilt == 0 ? "" : $"; worn meshes {_lastRigged} a frame, {_lastPalettes} palettes, {_shapesBuilt} shapes built";
        _shapesBuilt = 0;
        return ImpostorCount == 0 && JellyCount == 0 && worn == "" ? "" : $", avatars: {ImpostorCount} impostors ({_captured} captured last frame), {JellyCount} jelly dolls{worn}";
    }

    void PublishStatus(long tick)
    {
        ServeInspection();
        if (tick - _statusAt < 500) return;
        _statusAt = tick;
        var list = new List<AvatarStatus>(_entryCount);
        for (int i = 0; i < _entryCount; i++)
        {
            var e = _entries[i];
            if (e.Clone) continue;
            var b = _bodies.GetValueOrDefault(e.Av.ID);
            list.Add(new AvatarStatus(e.Av.ID, e.Av.Name ?? "", e.Dist, b?.Cost ?? 0, b?.CostKnown ?? false, b?.CostArea ?? 0,
                e.Self ? "full" : e.Drawn, Limits.Overrides.TryGetValue(e.Av.ID, out var o) ? o : AvatarRender.Normal));
        }
        Limits.Status = list;
    }

    // --- impostors -----------------------------------------------------------------------------------------------

    /// <summary>Set up this frame's capture of an avatar: an orthographic box along the line from the camera around its
    /// posed joints (padded for what's worn), half as wide as tall to fit the tile. The view is a translation only, so
    /// the G-buffer it writes holds world-space normals.</summary>
    void BeginCapture(Body body, Vector3 root, Vector3 center, Vector3 half, ViewSetup v, ImpostorCapture cap)
    {
        half += new Vector3(0.25f, 0.25f, 0.15f);
        var f = v.Position - center;
        float dist = f.Length();
        f = dist > 1e-4f ? f / dist : Vector3.UnitX;
        var r = Vector3.Cross(Vector3.UnitZ, f);
        if (r.LengthSquared() < 1e-6f) r = new Vector3(v.View.M11, v.View.M21, v.View.M31);
        r = Vector3.Normalize(r);
        var u = Vector3.Cross(f, r);
        static float Extent(Vector3 h, Vector3 axis) => MathF.Abs(h.X * axis.X) + MathF.Abs(h.Y * axis.Y) + MathF.Abs(h.Z * axis.Z);
        float w2 = MathF.Max(Extent(half, r), Extent(half, u) * 0.5f);
        float h2 = w2 * 2;
        float d = Extent(half, f);
        cap.View = Matrix4x4.CreateTranslation(-center);
        var axes = new Matrix4x4(r.X, u.X, f.X, 0, r.Y, u.Y, f.Y, 0, r.Z, u.Z, f.Z, 0, 0, 0, 0, 1);
        var ortho = new Matrix4x4(1 / w2, 0, 0, 0, 0, -1 / h2, 0, 0, 0, 0, 1 / (2 * d), 0, 0, 0, 0.5f, 1);
        cap.Proj = axes * ortho;
        cap.Center = center + f * d;
        body.BoxFromRoot = center - root;
        body.BoxX = r * w2;
        body.BoxY = u * h2;
        body.BoxZ = f * d;
        body.CapDir = f;
        body.CapDist = dist;
        body.CapturedAt = System.Environment.TickCount64;
        body.Captured = true;
    }

    /// <summary>The impostor's box into the frame: an instance whose rows are its half axes and centre, a material with
    /// the atlas's slots and its tile.</summary>
    void AddImpostor(Avatar av, Body body, Vector3 root, ViewSetup v, DrawList dl)
    {
        if (!body.Captured || body.Tile.IsEmpty) return;
        var atlas = _world.Gpu.Renderer.Atlas;
        var c = root + body.BoxFromRoot;
        uint mat = dl.AddMaterial(new GpuMaterial
        {
            Color = Vector4.One,
            Diffuse = new GpuTexRef { Index = atlas.DiffuseSlot, Offset = new Vector2(body.Tile.X, body.Tile.Y), Scale = new Vector2(body.Tile.W, body.Tile.H) },
            Specular = new GpuTexRef { Index = atlas.SpecularSlot, Scale = Vector2.One },
            Normal = new GpuTexRef { Index = atlas.NormalSlot, Scale = Vector2.One },
            Emissive = new GpuTexRef { Index = atlas.EmissiveSlot, Scale = Vector2.One },
            Bump = (uint)atlas.DepthSlot,
            Flags = MaterialFlags.Avatar,
        });
        var x = body.BoxX; var y = body.BoxY; var z = body.BoxZ;
        dl.Impostors.Add(dl.AddInstance(new GpuInstance
        {
            Model = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, c.X, c.Y, c.Z, 1),
            Material = mat, ObjectId = av.LocalID,
        }));
    }

    /// <summary>LLVOAvatar::calcMutedAVColor with COLORIZE_JELLYDOLLS: a colour from the first byte of the avatar's id
    /// along red, magenta, blue, cyan, green, yellow and back to red; light grey for those the person chose not to render.</summary>
    Vector4 JellyColor(UUID id)
    {
        if (Limits.Overrides.TryGetValue(id, out var o) && o == AvatarRender.Never) return new Vector4(0.4f, 0.4f, 0.4f, 1);
        ReadOnlySpan<Vector3> spectrum = stackalloc Vector3[]
        {
            new(1, 0, 0), new(1, 0, 1), new(0, 0, 1), new(0, 1, 1), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0),
        };
        float s = id.GetBytes()[0] / 256f * (spectrum.Length - 1);
        int i = (int)s;
        var c = Vector3.Lerp(spectrum[i], spectrum[Math.Min(i + 1, spectrum.Length - 1)], s - i);
        return new Vector4(c, 1);
    }

    // --- complexity ----------------------------------------------------------------------------------------------

    /// <summary>The avatar's rendering complexity as the viewer computes it: 200 for each visible bake, then each
    /// attachment's cost (its prims' volume costs and its unique textures', clamped to 1,000,000), and the attachments'
    /// scaled surface area. Known once every mesh's header has arrived; until then the meshes are fetched without being
    /// built. Recomputed when what's worn changes, and twice a second while incomplete.</summary>
    (int Cost, bool Known, float Area) Complexity(RegionScene scene, Avatar av, float area, long tick)
    {
        if (!_bodies.TryGetValue(av.ID, out var body))
        {
            body = new Body { Id = av.ID, Anim = Animator(av.ID, Now), Pose = new SkeletonPose(_cd.Skeleton) };
            _bodies[av.ID] = body;
        }
        int key = WornKeyCost(scene, av);
        if (key == body.CostKey && (body.CostKnown || tick - body.CostAt < 500)) return (body.Cost, body.CostKnown, body.CostArea);
        body.CostKey = key;
        body.CostAt = tick;

        int total = 0;
        foreach (int te in new[] { TeHead, TeUpper, TeLower, TeEyes, TeSkirt, TeHair })
            if (Visible(BakeId(av, te))) total += 200;
        bool known = true;
        float surface = 0;
        var textures = _costTextures;
        foreach (var root in scene.AttachmentsOf(av.LocalID))
        {
            // each attachment's linkset: its root (on the avatar) and the prims linked to it
            if (root.Prim.ParentID != av.LocalID) continue;
            int point = (int)root.Prim.PrimData.AttachmentPoint;
            if (point is >= 31 and <= 38) continue;   // HUDs
            textures.Clear();
            float volume = 0;
            foreach (var part in scene.AttachmentsOf(av.LocalID))
            {
                if (part != root && part.Prim.ParentID != root.LocalId) continue;
                volume += PrimCost(scene, part, textures, area, ref known);
                var sc = part.Prim.Scale;
                surface += 6f * MathF.Max(sc.X, MathF.Max(sc.Y, sc.Z));
            }
            float texCost = 0;
            foreach (var id in textures) texCost += TextureCost(id);
            total += (int)Math.Clamp(volume + texCost, 0, 1.0e6f);
        }
        if (Debug && known && total != body.Cost) _world.Log?.Invoke($"complexity {av.Name}: {total}, attachments {surface:F0} m2");
        (body.Cost, body.CostKnown, body.CostArea) = (total, known, surface);
        return (total, known, surface);
    }

    readonly HashSet<UUID> _costTextures = new();

    /// <summary>Changes with the avatar's attachments and their shapes and textures (not with rigged LODs arriving).</summary>
    static int WornKeyCost(RegionScene scene, Avatar av)
    {
        var h = new HashCode();
        foreach (var obj in scene.AttachmentsOf(av.LocalID))
        {
            h.Add(obj.LocalId);
            h.Add(obj.Asset);
            h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Prim.Textures));
        }
        h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(av.Textures));
        return h.ToHashCode();
    }

    /// <summary>LLVOVolume::getRenderCost for one prim (its textures into the set).</summary>
    float PrimCost(RegionScene scene, SceneObject obj, HashSet<UUID> textures, float area, ref bool known)
    {
        var prim = obj.Prim;
        if (obj.Skip) return 0;
        var scale = prim.Scale;
        float radius = 0.5f * MathF.Sqrt(scale.X * scale.X + scale.Y * scale.Y + scale.Z * scale.Z);
        float[]? tris;
        bool rigged = false;
        if (obj.Kind == ShapeKind.Mesh)
        {
            tris = _world.Geometry.MeshCost(obj.Asset, scene.Source.Assets, area);
            if (tris == null) { known = false; return 0; }
            if (tris.Length == 0) return 0;   // "something went wrong - user should know their content isn't render-free"
            rigged = _world.Geometry.MeshRigged(obj.Asset);
        }
        else tris = VolumeTris(obj);
        float triangles = RadiusWeightedTris(tris, radius);
        if (triangles <= 0) triangles = 4;
        if (obj.Kind == ShapeKind.Sculpt) textures.Add(obj.Asset);

        bool alpha = false, bump = false, shiny = false, glow = false, animtex = false, planar = false;
        int media = 0;
        var t = prim.Textures;
        if (t != null)
        {
            void Face(Primitive.TextureEntryFace? f)
            {
                if (f == null) return;
                textures.Add(f.TextureID);
                if (f.RGBA.A < 0.999f || (_world.Textures.TryGet(f.TextureID, out var st) && st.HasAlpha && !st.IsAlphaMask)) alpha = true;
                if (f.Bump != Bumpiness.None) bump = true;
                if (f.Shiny != Shininess.None) shiny = true;
                if (f.Glow > 0) glow = true;
                if (f.TexMapType == MappingType.Planar) planar = true;
                if (f.MediaFlags) media++;
            }
            Face(t.DefaultTexture);
            if (t.FaceTextures != null)
                for (int i = 0; i < 8 && i < t.FaceTextures.Length; i++) Face(t.FaceTextures[i]);
            if (prim.TextureAnim.Flags.HasFlag(Primitive.TextureAnimMode.ANIM_ON)) animtex = true;
        }
        float shame = MathF.Max(triangles * 5f, 2f);
        if (animtex) shame *= 4f;
        if (alpha) shame *= 4f;
        if (glow) shame *= 1.5f;
        if (bump) shame *= 1.25f;
        if (shiny) shame *= 1.6f;
        if (rigged) shame *= 1.2f;
        if (prim.Flexible != null) shame *= 5f;
        if (prim.ParticleSys.PartMaxAge > 0 && prim.ParticleSys.BurstRate > 0)
        {
            var ps = prim.ParticleSys;
            float count = MathF.Min(2048, ps.BurstPartCount * MathF.Ceiling(ps.PartMaxAge / ps.BurstRate));
            float size = (MathF.Max(ps.PartStartScaleX, ps.PartEndScaleX) + MathF.Max(ps.PartStartScaleY, ps.PartEndScaleY)) / 2f;
            shame += count * size;
        }
        if (prim.Light is { Intensity: > 0 }) shame += 500;
        shame += media * 1500;
        return shame;
    }

    /// <summary>LLMeshCostData::getRadiusWeightedTris: the LODs' triangles weighted by the share of a region-sized disc
    /// over which each would be drawn.</summary>
    static float RadiusWeightedTris(float[] tris, float radius)
    {
        if (tris.Length < 4) return 0;
        const float maxDistance = 512f, maxArea = 102944f, minArea = 1f;
        float dlowest = MathF.Min(radius / 0.03f, maxDistance), dlow = MathF.Min(radius / 0.06f, maxDistance), dmid = MathF.Min(radius / 0.24f, maxDistance);
        float high = MathF.Min(MathF.PI * dmid * dmid, maxArea), mid = MathF.Min(MathF.PI * dlow * dlow, maxArea);
        float low = MathF.Min(MathF.PI * dlowest * dlowest, maxArea), lowest = maxArea;
        lowest -= low; low -= mid; mid -= high;
        high = Math.Clamp(high, minArea, maxArea); mid = Math.Clamp(mid, minArea, maxArea);
        low = Math.Clamp(low, minArea, maxArea); lowest = Math.Clamp(lowest, minArea, maxArea);
        float sum = high + mid + low + lowest;
        return (tris[3] * high + tris[2] * mid + tris[1] * low + tris[0] * lowest) / sum;
    }

    readonly Dictionary<int, float[]> _volumeTris = new();

    /// <summary>LLVOVolume::getCostData for a prim: LLVolume::getLoDTriangleCounts, as header sizes of 10 bytes a
    /// triangle, through the mesh estimate (cached by shape).</summary>
    float[] VolumeTris(SceneObject obj)
    {
        if (_volumeTris.TryGetValue(obj.ShapeHash, out var tris)) return tris;
        var p = VolumeParams.FromConstructionData(obj.Prim.PrimData);
        tris = new float[4];
        float[] detail = { 1f, 1.5f, 2.5f, 4f };
        for (int i = 0; i < 4; i++)
        {
            var path = new VolumePath();
            path.Generate(p, detail[i], 0);
            var profile = new VolumeProfile();
            profile.Generate(p, false, detail[i], 0);
            int pathPoints = Math.Max(2, path.Points.Count), profilePoints = Math.Max(2, profile.Points.Count);
            int count = (profilePoints - 1) * 2 * (pathPoints - 1) + profilePoints * 2;
            tris[i] = MathF.Max(count * 10 - 384f, 16f) / 16f;
        }
        if (_volumeTris.Count > 4096) _volumeTris.Clear();
        return _volumeTris[obj.ShapeHash] = tris;
    }

    /// <summary>LLVOVolume::getTextureCost: 256 plus 16 for every 128 pixels of its full width and height (a texture not
    /// yet known counts as 512 square).</summary>
    float TextureCost(UUID id)
    {
        if (id == UUID.Zero) return 0;
        int w = 512, h = 512;
        if (_world.Textures.TryGet(id, out var t))
        {
            if (t.FullWidth > 0) (w, h) = (t.FullWidth, t.FullHeight);
            else if (t.Width > 0) (w, h) = (t.Width << Math.Max(0, t.Discard), t.Height << Math.Max(0, t.Discard));
        }
        return 256 + 16f * (h / 128f + w / 128f);
    }
}
