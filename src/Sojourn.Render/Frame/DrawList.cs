using System.Numerics;
using Sojourn.Render.Gpu;

namespace Sojourn.Render.Frame;

public struct DrawCmd
{
    public MeshRange Range;
    public uint Instance;
    public float Depth;      // view distance, for alpha sorting (a blended face: its prim's, as the viewer's spatial group)
    public float SubDepth;   // a blended face's own key, ordering the faces of one prim (0 elsewhere)
    public Vector4 Bounds;   // world-space bounding sphere (xyz centre, w radius) for shadow caster culling
    /// <summary>A blended face's world box's half height about Bounds.Z, with a centimetre to spare (0: not known; the
    /// sphere's radius stands in): which side of the water it reaches (Renderer.Upload).</summary>
    public float HalfHeight;
}

/// <summary>Everything one view draws, produced by a scene for a camera. Instances and materials are indexed by
/// the draws; the renderer copies all of it into the frame arena.</summary>
public sealed class DrawList
{
    public readonly List<GpuInstance> Instances = new();
    public readonly List<GpuMaterial> Materials = new();
    public readonly List<GpuLight> Lights = new();
    public readonly List<GpuProjector> Projectors = new();
    public readonly List<DrawCmd> Opaque = new();          // deferred, back-face culled
    public readonly List<DrawCmd> OpaqueTwoSided = new();  // deferred, no culling (double-sided PBR)
    public readonly List<DrawCmd> Fullbright = new();      // forward after lighting (opaque / masked fullbright)
    public readonly List<DrawCmd> Alpha = new();           // forward, blended, sorted far to near
    public readonly List<DrawCmd> Glow = new();            // glow mask (opaque faces with glow)
    public readonly List<DrawCmd> AlphaGlow = new();       // glow mask for blended faces, after the alpha pass
    public readonly List<DrawCmd> Water = new();
    /// <summary>Water exclusion faces (World/RegionScene.WaterExclusion.cs), drawn into the water exclusion mask alone.</summary>
    public readonly List<DrawCmd> WaterExclusion = new();
    /// <summary>Property and ban lines' triangles (World/ParcelLines.cs), the main view's alone, drawn over the finished
    /// image (Frame/Renderer.ParcelLines.cs).</summary>
    public readonly List<World.ParcelLineVertex> ParcelLines = new();
    public int PropertyLineVertices;   // the first of ParcelLines are property lines', the rest ban walls'
    /// <summary>Beacons and highlights (World/Beacons.cs), the main view's alone, over the finished image (Frame/Renderer.Beacons.cs).</summary>
    public readonly World.BeaconDraws Beacons = new();
    /// <summary>Avatar hair, eyelashes and skirts (the avatar pool's post-deferred pass): blended, drawn in order
    /// after fullbright and glow, before water and alpha, each mesh in the viewer's 3-pass (large) or 2-pass way.</summary>
    public readonly List<AvatarAlphaCmd> AvatarAlpha = new();
    /// <summary>Blended rigged mesh faces: drawn unsorted, in the order added (avatar by avatar), with depth writes,
    /// before the other blended faces (LLDrawPoolAlpha's rigged pass).</summary>
    public readonly List<DrawCmd> RiggedAlpha = new();
    public readonly List<DrawCmd>[] ShadowCasters = { new(), new(), new(), new(), new(), new(), new() };   // sun cascades 0-3, projector shadows 4-5, the far sun cascade 6
    public readonly List<NameTag> NameTags = new();
    /// <summary>Particle quads (four corners each), sorted far to near, and their per-quad draw info.</summary>
    public readonly List<Particles.ParticleVertex> ParticleVertices = new();
    public readonly List<Particles.ParticleQuad> ParticleQuads = new();
    /// <summary>Skinned meshes to pose this frame (the skinning pre-pass) and their joint palettes.</summary>
    public readonly List<SkinJob> Skins = new();
    public readonly List<Matrix4x4> Palettes = new();
    /// <summary>Vertices the skinning pre-pass writes this frame (the frame's skinned vertex buffer).</summary>
    public uint SkinnedVertices;
    /// <summary>Flexible prims' simulated vertices (World/RegionScene.Flexi.cs), copied into the frame's skinned vertex
    /// buffer by the pre-pass through an identity palette.</summary>
    public readonly List<FlexiCopy> FlexiCopies = new();
    /// <summary>Avatars drawn as impostors (World/Avatars.Impostors.cs): the instances of their boxes, ray-cast into the
    /// G-buffer after the opaque faces; and the impostors captured this frame, each its own small G-buffer view.</summary>
    public readonly List<uint> Impostors = new();
    public readonly List<ImpostorCapture> Captures = new();
    readonly List<ImpostorCapture> _capturePool = new();
    /// <summary>The main view's list (or a segment of it): only it gets avatars' palettes (probe captures and HUDs leave
    /// worn rigged meshes out). Set by its owner; kept across Clear.</summary>
    public bool MainView;
    /// <summary>Avatars' palettes already in this list this frame (Avatars.RiggedPalette), by body, skin and kind.</summary>
    public readonly Dictionary<(object Body, object Share, bool Mirror), int> PaletteStarts = new();
    /// <summary>A segment (World/DrawPlan.cs): impostor captures it drew into, with their lists' lengths before, whose
    /// draws name the segment's instances until it's appended.</summary>
    public readonly List<(ImpostorCapture Capture, int Opaque, int TwoSided)> CaptureMarks = new();

    public void Clear()
    {
        Instances.Clear(); Materials.Clear(); Lights.Clear(); Projectors.Clear();
        Opaque.Clear(); OpaqueTwoSided.Clear(); Fullbright.Clear(); Alpha.Clear(); Glow.Clear(); AlphaGlow.Clear(); Water.Clear(); WaterExclusion.Clear(); ParcelLines.Clear(); PropertyLineVertices = 0; Beacons.Clear(); AvatarAlpha.Clear(); RiggedAlpha.Clear();
        foreach (var c in ShadowCasters) c.Clear();
        NameTags.Clear();
        ParticleVertices.Clear(); ParticleQuads.Clear();
        Skins.Clear(); Palettes.Clear(); SkinnedVertices = 0; FlexiCopies.Clear();
        Impostors.Clear();
        foreach (var c in Captures) { c.Opaque.Clear(); c.TwoSided.Clear(); _capturePool.Add(c); }
        Captures.Clear();
        PaletteStarts.Clear();
        CaptureMarks.Clear();
    }

    /// <summary>Append a segment drawn on its own (World/DrawPlan.cs): its instances, materials, palettes and skinned
    /// vertices go after this list's, and its draws (and those it put in impostor captures) are moved along to name
    /// them; slots of the persistent tables stay as they are.</summary>
    public void Append(DrawList s)
    {
        uint ib = (uint)Instances.Count, mb = (uint)Materials.Count, vb = SkinnedVertices;
        int pb = Palettes.Count;
        Materials.AddRange(s.Materials);
        foreach (var inst in s.Instances)
        {
            var x = inst;
            if ((x.Material & PersistentBit) == 0) x.Material += mb;
            Instances.Add(x);
        }
        Palettes.AddRange(s.Palettes);
        foreach (var j in s.Skins) Skins.Add(j with { Palette = j.Palette + pb, Dest = j.Dest + vb });
        foreach (var f in s.FlexiCopies) FlexiCopies.Add(f with { Palette = f.Palette + pb, Dest = f.Dest + vb });
        SkinnedVertices += s.SkinnedVertices;
        Lights.AddRange(s.Lights);
        Projectors.AddRange(s.Projectors);
        Moved(Opaque, s.Opaque, 0, ib, vb);
        Moved(OpaqueTwoSided, s.OpaqueTwoSided, 0, ib, vb);
        Moved(Fullbright, s.Fullbright, 0, ib, vb);
        Moved(Alpha, s.Alpha, 0, ib, vb);
        Moved(Glow, s.Glow, 0, ib, vb);
        Moved(AlphaGlow, s.AlphaGlow, 0, ib, vb);
        Moved(Water, s.Water, 0, ib, vb);
        Moved(WaterExclusion, s.WaterExclusion, 0, ib, vb);
        Moved(RiggedAlpha, s.RiggedAlpha, 0, ib, vb);
        for (int c = 0; c < ShadowCasters.Length; c++) Moved(ShadowCasters[c], s.ShadowCasters[c], 0, ib, vb);
        foreach (var (cap, o, t) in s.CaptureMarks)
        {
            Moved(cap.Opaque, null, o, ib, vb);
            Moved(cap.TwoSided, null, t, ib, vb);
        }
        if (s.NameTags.Count + s.ParticleQuads.Count + s.AvatarAlpha.Count + s.Impostors.Count + s.Captures.Count > 0)
            throw new InvalidOperationException("a draw list segment holds what only avatars and particles add");
    }

    const uint PersistentBit = PersistentTable<GpuInstance>.Bit;

    /// <summary>Draws moved along by a segment's bases: appended from <paramref name="from"/>, or (null) this list's own
    /// from <paramref name="start"/> on, in place.</summary>
    static void Moved(List<DrawCmd> into, List<DrawCmd>? from, int start, uint ib, uint vb)
    {
        if (from != null)
        {
            start = into.Count;
            into.AddRange(from);
        }
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(into);
        for (int i = start; i < span.Length; i++)
        {
            ref var d = ref span[i];
            if ((d.Instance & PersistentBit) == 0) d.Instance += ib;
            if ((d.Range.Arena & MeshRange.Skinned) != 0) d.Range = d.Range with { FirstVertex = d.Range.FirstVertex + vb };
        }
    }

    /// <summary>A capture of one impostor this frame: draws added to it are rendered once, into its atlas tile, with
    /// its own orthographic view.</summary>
    public ImpostorCapture AddCapture(ImpostorTile tile)
    {
        ImpostorCapture c;
        if (_capturePool.Count > 0) { c = _capturePool[^1]; _capturePool.RemoveAt(_capturePool.Count - 1); }
        else c = new ImpostorCapture();
        c.Tile = tile;
        Captures.Add(c);
        return c;
    }

    /// <summary>Queue a skinned mesh: <paramref name="source"/> holds its bind-pose vertices (geometry pool, at
    /// device address <paramref name="sourceAddress"/>) and indices, <paramref name="weights"/> the device address of
    /// its per-vertex joints/weights, <paramref name="paletteStart"/> its joint matrices (bind pose -> region, from
    /// <see cref="AddPalette"/>). Returns the range to draw: posed vertices this frame, the source's indices.</summary>
    public MeshRange AddSkinned(MeshRange source, ulong sourceAddress, ulong weights, int paletteStart)
    {
        uint dst = SkinnedVertices;
        SkinnedVertices += source.VertexCount;
        Skins.Add(new SkinJob(sourceAddress, weights, paletteStart, dst, source.VertexCount));
        return new MeshRange(source.Arena | MeshRange.Skinned, dst, source.VertexCount, source.FirstIndex, source.IndexCount);
    }

    /// <summary>Queue a flexible prim's face: its vertices this frame (kept as they are until the frame is recorded) and
    /// its rest shape's range, whose indices they're drawn with. Returns the range to draw.</summary>
    public MeshRange AddFlexi(MeshVertex[] vertices, MeshRange rest)
    {
        uint dst = SkinnedVertices;
        SkinnedVertices += (uint)vertices.Length;
        int pal = Palettes.Count;
        Palettes.Add(Matrix4x4.Identity);
        FlexiCopies.Add(new FlexiCopy(vertices, pal, dst));
        return new MeshRange(rest.Arena | MeshRange.Skinned, dst, (uint)vertices.Length, rest.FirstIndex, rest.IndexCount);
    }

    /// <summary>Queue a skinned mesh whose bind-pose vertices are made this frame (an avatar's head while its lips move:
    /// World/Avatars.LipSync.cs): the first <paramref name="source"/>.VertexCount of <paramref name="vertices"/> (kept as
    /// they are until the frame is recorded) go through the arena with the flexible prims', skinned by
    /// <paramref name="weights"/> (a device address) and the palette. Returns the range to draw, as AddSkinned.</summary>
    public MeshRange AddSkinnedCopy(MeshVertex[] vertices, MeshRange source, ulong weights, int paletteStart)
    {
        uint dst = SkinnedVertices;
        SkinnedVertices += source.VertexCount;
        FlexiCopies.Add(new FlexiCopy(vertices, paletteStart, dst, weights, (int)source.VertexCount));
        return new MeshRange(source.Arena | MeshRange.Skinned, dst, source.VertexCount, source.FirstIndex, source.IndexCount);
    }

    public int AddPalette(ReadOnlySpan<Matrix4x4> palette)
    {
        int pal = Palettes.Count;
        foreach (var m in palette) Palettes.Add(m);
        return pal;
    }

    public int TotalDraws => Opaque.Count + OpaqueTwoSided.Count + Fullbright.Count + Alpha.Count + Glow.Count + AlphaGlow.Count + Water.Count + WaterExclusion.Count
        + RiggedAlpha.Count + ShadowCasters.Sum(c => c.Count) + Impostors.Count + Captures.Sum(c => c.Opaque.Count + c.TwoSided.Count);

    public uint AddInstance(in GpuInstance inst)
    {
        Instances.Add(inst);
        return (uint)(Instances.Count - 1);
    }

    public uint AddMaterial(in GpuMaterial m)
    {
        Materials.Add(m);
        return (uint)(Materials.Count - 1);
    }
}

/// <summary>One impostor's capture this frame (see <see cref="DrawList.AddCapture"/>): its tile, view, and draws, the
/// opaque ones back-face culled.</summary>
public sealed class ImpostorCapture
{
    public ImpostorTile Tile;
    public Matrix4x4 View, Proj;
    public Vector3 Center;
    public readonly List<DrawCmd> Opaque = new(), TwoSided = new();
}

/// <summary>A blended avatar mesh; <paramref name="Large"/>: pixel area above 64x64 (LLViewerJoint's 3-pass hair).</summary>
public readonly record struct AvatarAlphaCmd(DrawCmd Cmd, bool Large);

/// <summary>A flexible prim's face to copy into the frame's skinned vertex buffer at <paramref name="Dest"/> (or any
/// vertices made this frame: <paramref name="Weights"/>, their skin weights' device address, 0 for one identity joint;
/// <paramref name="Count"/>, how many of the vertices, -1 for all).</summary>
public readonly record struct FlexiCopy(MeshVertex[] Vertices, int Palette, uint Dest, ulong Weights = 0, int Count = -1)
{
    public int Used => Count < 0 ? Vertices.Length : Count;
}

/// <summary>One skinning dispatch: source vertices and weights (device addresses), palette start in the draw
/// list's palettes, first vertex in the frame's skinned vertex buffer, vertex count.</summary>
public readonly record struct SkinJob(ulong Source, ulong Weights, int Palette, uint Dest, uint Count);

/// <summary>Where the viewer would put an avatar's name tag this frame; Id and Head (the drawn head, mHead) for the
/// client's own tags (Realtime/Overlay.cs OverlayTag).</summary>
public readonly record struct NameTag(Vector3 Position, string Text, uint Color, LibreMetaverse.UUID Id = default, Vector3 Head = default);

/// <summary>Camera and environment for one view.</summary>
public sealed class ViewSetup
{
    public uint Width, Height;
    public ulong RegionHandle;
    public Vector3 Position;
    public Matrix4x4 View;            // region -> GL eye space (x right, y up, -z forward)
    public Matrix4x4 Proj;            // eye -> Vulkan clip, reverse-Z infinite
    public float VFov, Near = 0.1f, DrawDistance = 128f;
    /// <summary>While the load governor has brought the draw distance in (Realtime/LoadGovernor.cs): objects come into
    /// reach only within this; those already in reach stay out to <see cref="DrawDistance"/> (and the edge band), a band
    /// a few metres wide, so nothing at the edge blinks in and out as the head moves (RegionScene.DrawObject).</summary>
    public float NewDistance = float.PositiveInfinity;
    /// <summary>The headset eyes' near plane: people lean in close to things, and the viewer's 0.1 m cut them off at
    /// the face. Reverse-Z in a float depth buffer keeps the same relative precision at any distance whatever the near
    /// plane is. XrSession tells the compositor the same (its depth layers' farZ).</summary>
    public const float HeadsetNear = 0.02f;
    /// <summary>The field of view objects' detail levels are chosen by (VolumeLod.CalcLod's camera angle), when not the
    /// view's own: a headset's lenses see ~100 degrees at more pixels a degree than a desktop's 60, and the viewer's
    /// zoom compensation would have dropped rigged meshes to a coarser level from ~19 m instead of ~30 (detail 2.5).</summary>
    public float? LodFov;
    /// <summary>The projectors holding the two projector shadow maps (region handle, prim local id), or zeros.</summary>
    public (ulong Region, uint LocalId)[] ShadowProjectors = new (ulong, uint)[2];
    /// <summary>The view whose camera picks LODs when it isn't this one (probe faces use the main view's).</summary>
    public ViewSetup? LodView;
    public GpuSky Sky;
    public float WaterHeight = 20f;
    public bool HasWater = true;
    public float Time;
    public Vector3 SunDirRegion = Vector3.UnitZ;   // toward the light, region space
    public Vector3 MoonDirRegion = -Vector3.UnitZ;
    public uint ShadowMapWidth, ShadowMapHeight, SpotShadowMapSize;
    /// <summary>Sun cascades' casters smaller than this many map texels (bounding radius) are left out of that cascade
    /// (0: none left out; RenderQuality.MinCasterTexels), measured across the light along these two directions.</summary>
    public float MinCasterTexels;
    public Vector3 CasterAcross1, CasterAcross2;
    /// <summary>With the far cascades fitted larger (RenderQuality.FarCascadeMargin): what cascades 2 and 3 must hold
    /// this frame (their receivers as fitted without the margin, region space), for Renderer.Cascades' reuse test.</summary>
    public Vector3[]? FarReceivers2, FarReceivers3;

    /// <summary>The far sun cascade's map is kept from an earlier frame (FrameRenderer.FarShadow): not drawn this frame.</summary>
    public bool FarShadowKept;
    /// <summary>Casters smaller than this many texels in the far cascade's map are left out of it (always: it's coarse).</summary>
    public float FarCasterTexels;
    public const int FarCascade = 6;

    /// <summary>The sun cascades (bits 0-3 of a caster mask, and the far one's, 6) in whose map a sphere is smaller than
    /// <see cref="MinCasterTexels"/> (the far one's: <see cref="FarCasterTexels"/>).</summary>
    public int TinyCasters(Vector3 center, float radius, int mask)
    {
        int tiny = 0;
        for (int c = 0; c <= FarCascade; c++)
        {
            if ((mask & (1 << c)) == 0 || c is 4 or 5) continue;
            float half = 0.5f * (c == FarCascade ? Sky.shadow_far_res.X : Math.Max(ShadowMapWidth, ShadowMapHeight));
            float floor = c == FarCascade ? FarCasterTexels : MinCasterTexels;
            var m = c switch { 0 => Sky.shadow_viewproj0, 1 => Sky.shadow_viewproj1, 2 => Sky.shadow_viewproj2, 3 => Sky.shadow_viewproj3, _ => Sky.shadow_viewproj6 };
            var p = Vector4.Transform(new Vector4(center, 1), m);
            if (p.W <= 1e-6f) continue;
            var a = Vector4.Transform(new Vector4(center + CasterAcross1 * radius, 1), m);
            var b = Vector4.Transform(new Vector4(center + CasterAcross2 * radius, 1), m);
            if (a.W <= 1e-6f || b.W <= 1e-6f) continue;
            var pc = new Vector2(p.X, p.Y) / p.W;
            float r = MathF.Max(Vector2.Distance(new Vector2(a.X, a.Y) / a.W, pc), Vector2.Distance(new Vector2(b.X, b.Y) / b.W, pc));
            if (r * half < floor) tiny |= 1 << c;
        }
        return tiny;
    }
    /// <summary>The evaluated environment, kept so probe face views can rebuild their uniforms.</summary>
    public Sojourn.Render.Environment.SkyUniforms SkyU, SkyIrradianceU;
    public Sojourn.Render.Environment.WaterUniforms WaterU;
    public DateTime EnvTime;
    public LibreMetaverse.UUID? LocalSky;
    /// <summary>The person's own environment's version while one is shown (0: the place's alone), keying what's captured
    /// under it (Environment/PersonalEnvironment.cs).</summary>
    public long LocalKey;
    public bool SunUp = true;
    public Vector4 ClearColor = new(0, 0, 0, 1);
    public LibreMetaverse.UUID WaterNormalA, WaterNormalB;
    /// <summary>Bindless slots of the water normal maps (-1 while loading); set by the scene.</summary>
    public int WaterNormalSlotA = -1, WaterNormalSlotB = -1;
    public LibreMetaverse.UUID CloudNoiseA, CloudNoiseB;
    public int CloudNoiseSlotA = -1, CloudNoiseSlotB = -1;
    /// <summary>Probe captures: the far clip culls against the six faces' far planes together (a cube of half-size
    /// DrawDistance around the probe) instead of a sphere.</summary>
    public bool CubeCull;
    /// <summary>A mirror's reflection: geometry behind the mirror is clipped (region space, kept where dot(xyz, p) +
    /// w >= 0), and the view draws what's flagged for mirrors (the own body's head).</summary>
    public Vector4 ClipPlane = new(0, 0, 0, 1);
    public uint Mask = ViewMask.Main;
    /// <summary>Blended faces sort along the view axis (the viewer's way) instead of by distance from
    /// <see cref="Position"/>: a HUD's, whose pieces stack by their offset along it.</summary>
    public bool PlanarSort;
    /// <summary>The lenses' hidden area for this eye (XR_KHR_visibility_mask): triangles of points on the eye's z = -1
    /// plane (tangents); null: none (Renderer.Lens.cs).</summary>
    public System.Numerics.Vector2[]? LensHidden;

    /// <summary>How far past the draw distance something already drawn stays (RegionScene.DrawObject): a band, so
    /// leaning in the headset doesn't blink what sits on the edge.</summary>
    public const float EdgeBand = 2f;

    /// <summary>True when a sphere (centre, radius) lies wholly beyond the draw distance, measured from
    /// <see cref="Position"/>: in the headset the head's centre (the centre view), so the draw distance is a sphere
    /// about the head rather than a plane in front of it.</summary>
    public bool Beyond(Vector3 center, float radius)
    {
        if (!CubeCull) return Vector3.Distance(center, Position) - radius > DrawDistance;
        var d = Vector3.Abs(center - Position);
        return MathF.Max(d.X, MathF.Max(d.Y, d.Z)) - radius > DrawDistance;
    }

    public static Matrix4x4 LookAt(Vector3 eye, Vector3 target, Vector3 up)
    {
        // GL-style: eye space x right, y up, looking down -z (System.Numerics row-vector convention)
        return Matrix4x4.CreateLookAt(eye, target, up);
    }

    /// <summary>A copy for another eye of the same frame (shares <see cref="ShadowProjectors"/>).</summary>
    public ViewSetup CloneForEye() => (ViewSetup)MemberwiseClone();

    /// <summary>Asymmetric reverse-Z infinite perspective (a headset eye): tangents of the half-angles as OpenXR
    /// gives them (left and down negative).</summary>
    public static Matrix4x4 ReverseZInfinite(float tanLeft, float tanRight, float tanUp, float tanDown, float near)
    {
        float w = tanRight - tanLeft, h = tanUp - tanDown;
        return new Matrix4x4(
            2 / w, 0, 0, 0,
            0, -2 / h, 0, 0,
            (tanRight + tanLeft) / w, -(tanUp + tanDown) / h, 0, -1,
            0, 0, near, 0);
    }

    /// <summary>Reverse-Z infinite perspective into Vulkan clip space (y down, depth 1 at near, 0 at infinity).</summary>
    public static Matrix4x4 ReverseZInfinite(float vfov, float aspect, float near)
    {
        float f = 1f / MathF.Tan(vfov / 2);
        // row-vector convention: clip = eye * P
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, -f, 0, 0,
            0, 0, 0, -1,
            0, 0, near, 0);
    }
}
