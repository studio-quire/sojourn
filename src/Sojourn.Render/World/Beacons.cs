using LibreMetaverse;
using Sojourn.Render.Frame;

namespace Sojourn.Render.World;

/// <summary>The viewer's beacon kinds (View > Beacons and Highlights; its floater's checkboxes).</summary>
[Flags]
public enum BeaconKinds
{
    None = 0,
    Scripted = 1,    // scriptsbeacon: linkset roots with a script
    Touch = 2,       // scripttouchbeacon: roots with a script that takes touches (the two are exclusive, as the floater keeps them)
    Physical = 4,    // physicalbeacon
    Sound = 8,       // soundsbeacon
    Particles = 16,  // particlesbeacon
    Media = 32,      // moapbeacon: media on a face
}

/// <summary>How a highlighted face is drawn (Frame/Renderer.Beacons.cs): a beacon's highlight, flat red at half (the
/// viewer's renderHighlights); Highlight Transparent's blended faces red, masked ones blue, invisiprims green, each times
/// the viewer's smoke image (LLDrawPoolAlpha::renderDebugAlpha).</summary>
public enum FaceTint { Beacon, Blended, Masked, Invisible }

/// <summary>A frame's beacons and highlights for the main view, drawn over each eye's finished image.</summary>
public sealed class BeaconDraws
{
    /// <summary>Line and column triangles: the first <see cref="OverVertices"/> over everything, the rest tested
    /// against the eye's depth.</summary>
    public readonly List<ParcelLineVertex> Lines = new();
    public int OverVertices;
    /// <summary>Faces to highlight, by <see cref="FaceTint"/>; their instances are in the main list's.</summary>
    public readonly List<DrawCmd>[] Faces = { new(), new(), new(), new() };
    /// <summary>The smoke image's bindless slot (-1: not here yet, the tint alone).</summary>
    public int Smoke = -1;

    public int FaceCount => Faces[0].Count + Faces[1].Count + Faces[2].Count + Faces[3].Count;

    public void Clear()
    {
        Lines.Clear();
        OverVertices = 0;
        foreach (var f in Faces) f.Clear();
        Smoke = -1;
    }
}

/// <summary>
/// Beacons and highlights as the official viewer draws them (pipeline.cpp's render*Beacons, postSort and
/// renderHighlights; llglsandbox.cpp's renderObjectBeacons; lldrawpoolalpha.cpp's renderDebugAlpha; lltracker.cpp):
/// - <b>Beacons</b> (<see cref="Lines"/>, the floater's Beacons): at each object of a kind turned on, a cross of lines
///   2 m each way across and 50 m up and down, drawn over everything at an eighth of its colour's alpha, and a small
///   cross of 0.5 m and a 10 cm wire cube where it's seen (red for scripts, green physical, light blue particles, white
///   media, yellow sounds), DebugBeaconLineWidth pixels wide. Sound sources are world audio's, heard or not, as the
///   viewer walks its audio engine's sources.
/// - <b>Highlights</b> (<see cref="Highlights"/>): the faces of those objects red at half (scripted ones' root prim alone).
/// - The sun and moon beacons: a line 50 m from the avatar toward each, with a cross at each end.
/// - <b>Highlight Transparent</b> (<see cref="Transparent"/>, Ctrl+Alt+T): every blended face drawn in view red, masked
///   ones blue, invisiprims green, fully transparent faces (never drawn) red, times the smoke image in their own
///   texture coordinates; trees, avatars' own bodies and HUDs aren't the alpha pool's and aren't tinted.
/// - <b>The tracking beacon</b> (<see cref="Track"/>, LLTracker): a column 2 m wide fading to its edges, red above the
///   place up to 5020 m, blue below it down to 0 (MapTrackColor, MapTrackColorUnder), tested against the scene, more
///   faded far away. Its label and when it ends are the client's.
/// Per the viewer the floater must be open for any of it but the tracker; here <see cref="On"/> stands for that (as
/// Firestorm, which draws them with the floater shut, keeps it as the toggles). Distances and facing are from the head,
/// so both eyes see the same lines (docs/rendering.md > Beacons and highlights). Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class Beacons
{
    // the settings (any thread)
    public volatile bool On;
    volatile int _kinds = (int)(BeaconKinds.Touch | BeaconKinds.Physical);
    public BeaconKinds Kinds { get => (BeaconKinds)_kinds; set => _kinds = (int)value; }
    public volatile bool Lines;
    public volatile bool Highlights = true;
    public volatile bool Sun, Moon;
    public volatile bool Transparent;
    float _width = 1;
    /// <summary>The lines' width in pixels of the view (DebugBeaconLineWidth, 1-?: the floater's slider goes to 5).</summary>
    public float Width { get => Volatile.Read(ref _width); set => Volatile.Write(ref _width, Math.Clamp(value, 1, 16)); }

    /// <summary>A tracked place or person: where (global metres) and whether to draw the column (the client ends it).</summary>
    public sealed record TrackMark(Vector3d Global);
    public volatile TrackMark? Track;

    /// <summary>World audio's sources (global metres), for the sound beacons (Sojourn.Client sets it).</summary>
    public volatile Vector3d[] SoundSources = [];
    /// <summary>The person's avatar, for the sun and moon beacons' start.</summary>
    public uint SelfLocalId;
    /// <summary>Prim flags as the session sees them (they can differ per observer); null: the prim's own.</summary>
    public Func<Primitive, PrimFlags>? FlagsOf { get; set; }

    /// <summary>The viewer's smoke image (IMG_SMOKE), Highlight Transparent's texture.</summary>
    public static readonly UUID SmokeImage = new("b4ba225c-373f-446d-9f7e-6cb7b5cf9b3d");

    // pipeline.cpp's colours (debug beacons are drawn with linearColor4 over the gamma-encoded image)
    static readonly Vector4 ScriptColor = new(1, 0, 0, 0.5f), PhysicalColor = new(0, 1, 0, 0.5f), MediaColor = new(1, 1, 1, 0.5f),
        ParticleColor = new(0.5f, 0.5f, 1, 0.5f), SoundColor = new(1, 1, 0, 0.5f), SunColor = new(1, 0.5f, 0, 0.5f), MoonColor = new(1, 0, 0.8f, 0.5f);

    public bool Wanted => Transparent || Track != null || On && (Sun || Moon || Kinds != BeaconKinds.None && (Lines || Highlights));

    /// <summary>This frame's beacons and highlights into the main view's list, after its draws are built (render
    /// thread): <paramref name="cull"/> is the view's frustum, <paramref name="offset"/> the regions' offsets.</summary>
    public void Emit(RenderWorld world, ulong viewRegion, ViewSetup v, Frustum cull, DrawList dl, Func<ulong, ulong, Vector3> offset)
    {
        var into = dl.Beacons;
        if (!Wanted) return;
        var scan = new BeaconScan(this, world, v, cull, dl);
        bool objects = On && Kinds != BeaconKinds.None && (Lines || Highlights);
        if (objects || Transparent)
        {
            if (Transparent) TintDrawn(world, dl, into);
            foreach (var scene in world.Regions)
                if (scene.Source.HasSimulator) scene.Beacons(scan, offset(viewRegion, scene.Source.Handle), objects);
            if (Transparent)
            {
                var smoke = world.Region(viewRegion) is { } here ? world.Textures.Request(SmokeImage, here.Source.Assets) : null;
                into.Smoke = smoke is { State: TexState.Ready, Slot: >= 0 } ? smoke.Slot : -1;
            }
        }
        if (On && Lines && (Kinds & BeaconKinds.Sound) != 0)
        {
            Utils.LongToUInts(viewRegion, out uint ox, out uint oy);
            foreach (var g in SoundSources)
                scan.Beacon(new Vector3((float)(g.X - ox), (float)(g.Y - oy), (float)g.Z), SoundColor);
        }
        if (On && (Sun || Moon))
        {
            var from = world.Avatars.Extents(viewRegion, SelfLocalId)?.Center ?? v.Position;
            if (Sun) scan.SkyBeacon(from, v.SunDirRegion, SunColor);
            if (Moon) scan.SkyBeacon(from, v.MoonDirRegion, MoonColor);
        }
        into.OverVertices = scan.Over.Count;
        into.Lines.AddRange(scan.Over);
        into.Lines.AddRange(scan.Tested);
        if (Track is { } t) TrackColumn(into.Lines, t, viewRegion, v);
    }

    /// <summary>Highlight Transparent over what the frame draws: its blended faces (rigged ones too), and its masked
    /// faces among the opaque and fullbright ones, by their material.</summary>
    static void TintDrawn(RenderWorld world, DrawList dl, BeaconDraws into)
    {
        into.Faces[(int)FaceTint.Blended].AddRange(dl.Alpha);
        into.Faces[(int)FaceTint.Blended].AddRange(dl.RiggedAlpha);
        var masked = into.Faces[(int)FaceTint.Masked];
        const uint NotAlphaPool = MaterialFlags.Foliage | MaterialFlags.Avatar | MaterialFlags.Terrain | MaterialFlags.Water | MaterialFlags.Sky;
        foreach (var list in new[] { dl.Opaque, dl.OpaqueTwoSided, dl.Fullbright })
            foreach (var d in list)
                if (MaterialOf(world, dl, d.Instance) is { } m && (m.Flags & MaterialFlags.AlphaModeMask) == MaterialFlags.AlphaMask && (m.Flags & NotAlphaPool) == 0)
                    masked.Add(d);
    }

    static GpuMaterial? MaterialOf(RenderWorld world, DrawList dl, uint instance)
    {
        const uint Bit = PersistentTable<GpuInstance>.Bit;
        uint mat;
        if ((instance & Bit) != 0)
        {
            if (world.StaticInstances is not { } si) return null;
            mat = si[(int)(instance & ~Bit)].Material;
        }
        else if (instance < dl.Instances.Count) mat = dl.Instances[(int)instance].Material;
        else return null;
        if ((mat & Bit) != 0) return world.StaticMaterials is { } sm ? sm[(int)(mat & ~Bit)] : null;
        return mat < dl.Materials.Count ? dl.Materials[(int)mat] : null;
    }

    // --- the tracking beacon (lltracker.cpp renderBeacon, drawBeacon) ----------------------------------------------------

    const float TrackTop = 5020, FadeDist = 3;

    void TrackColumn(List<ParcelLineVertex> into, TrackMark t, ulong viewRegion, ViewSetup v)
    {
        Utils.LongToUInts(viewRegion, out uint ox, out uint oy);
        var at = new Vector3((float)(t.Global.X - ox), (float)(t.Global.Y - oy), (float)t.Global.Z);
        float dist = Vector3.Distance(at, v.Position);
        // fogged toward the sky's fog colour the further it is (as far as the draw distance), never under 0.2 nor
        // over 0.5 alpha, fading within 6 m (the viewer mixes in its sky's fog colour; here a light haze grey)
        float far = MathF.Max(v.DrawDistance, 1);
        float frac = dist > 0.99f * far ? 0.4f : 1 - 0.6f * dist / far;
        var fog = new Vector4(0.62f, 0.68f, 0.75f, 1);
        float alpha = Math.Clamp((dist - FadeDist) / FadeDist, 0.2f, 0.5f);
        var above = new Vector4(1, 0, 0, 1) * frac + fog * (1 - frac);
        var below = new Vector4(0, 0, 1, 1) * frac + fog * (1 - frac);
        above.W = below.W = alpha;
        // a ribbon across the camera's left axis, flat (no z), 1 m each side, its edges clear
        var right = new Vector3(v.View.M11, v.View.M21, 0);
        right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);
        Column(into, new Vector3(at.X, at.Y, at.Z), TrackTop, right, above);
        Column(into, new Vector3(at.X, at.Y, 0), MathF.Max(at.Z, 0), right, below);
    }

    static void Column(List<ParcelLineVertex> into, Vector3 bottom, float height, Vector3 side, Vector4 c)
    {
        if (height <= 0) return;
        uint mid = Pack(c), edge = Pack(c with { W = 0 });
        var top = bottom + new Vector3(0, 0, height);
        void V(Vector3 p, uint col) => into.Add(new ParcelLineVertex { Pos = p, Color = col, Tex = -1 });
        foreach (var s in new[] { -side, side })
        {
            V(bottom + s, edge); V(top, mid); V(top + s, edge);
            V(bottom + s, edge); V(bottom, mid); V(top, mid);
        }
    }

    internal static uint Pack(Vector4 c)
    {
        static uint B(float x) => (uint)Math.Clamp((int)MathF.Round(x * 255f), 0, 255);
        return B(c.X) | B(c.Y) << 8 | B(c.Z) << 16 | B(c.W) << 24;
    }

    internal static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
}

/// <summary>One frame's gathering of beacons (render thread): the lines' two passes, and highlighted faces' instances
/// in the main list.</summary>
internal sealed class BeaconScan
{
    public readonly Beacons B;
    public readonly RenderWorld World;
    public readonly ViewSetup View;
    public readonly Frustum Cull;
    public readonly DrawList Dl;
    public readonly List<ParcelLineVertex> Over = new(), Tested = new();
    public readonly BeaconKinds Kinds;
    public readonly bool Lines, Highlights, Transparent;
    readonly float _pxAngle, _width;
    readonly Vector3 _head;

    public BeaconScan(Beacons b, RenderWorld world, ViewSetup v, Frustum cull, DrawList dl)
    {
        (B, World, View, Cull, Dl) = (b, world, v, cull, dl);
        Kinds = b.Kinds;
        (Lines, Highlights, Transparent) = (b.Lines, b.Highlights, b.Transparent);
        _head = v.Position;
        _pxAngle = v.VFov / MathF.Max(1, v.Height);
        // under 1.5 pixels a thin quad leaves gaps where a GL line wouldn't
        _width = MathF.Max(b.Width, 1.5f);
    }

    public bool InView(Vector3 at, float radius) =>
        Vector3.Distance(at, _head) - radius <= View.DrawDistance && Cull.Sphere(at, radius);

    /// <summary>An object's beacon (renderObjectBeacons): the long cross over everything at a quarter of its alpha, the
    /// short cross and the cube where seen.</summary>
    public void Beacon(Vector3 at, Vector4 color)
    {
        var lin = new Vector4(Beacons.Linear(color.X), Beacons.Linear(color.Y), Beacons.Linear(color.Z), color.W);
        uint faint = Beacons.Pack(lin with { W = lin.W * 0.25f }), full = Beacons.Pack(lin);
        Cross(Over, at, 2, 2, 50, faint);
        Cube(Over, at, 0.1f, faint);
        Cross(Tested, at, 0.5f, 0.5f, 0.5f, full);
        Cube(Tested, at, 0.1f, full);
    }

    /// <summary>LLSky::renderSunMoonBeacons: 50 m from the avatar toward it, a small cross at the start, a big one at the end.</summary>
    public void SkyBeacon(Vector3 from, Vector3 dir, Vector4 color)
    {
        if (dir.LengthSquared() < 1e-6f) return;
        var to = from + Vector3.Normalize(dir) * 50;
        uint c = Beacons.Pack(color with { W = color.W * 0.5f });
        Cross(Over, from, 0.5f, 0.5f, 0.5f, c);
        Cross(Over, to, 2, 2, 2, c);
        Line(Over, from, to, c);
    }

    void Cross(List<ParcelLineVertex> into, Vector3 c, float dx, float dy, float dz, uint color)
    {
        Line(into, c - new Vector3(dx, 0, 0), c + new Vector3(dx, 0, 0), color);
        Line(into, c - new Vector3(0, dy, 0), c + new Vector3(0, dy, 0), color);
        Line(into, c - new Vector3(0, 0, dz), c + new Vector3(0, 0, dz), color);
    }

    void Cube(List<ParcelLineVertex> into, Vector3 c, float size, uint color)
    {
        float h = size / 2;
        for (int axis = 0; axis < 3; axis++)
            for (int i = 0; i < 4; i++)
            {
                float u = (i & 1) == 0 ? -h : h, w = (i & 2) == 0 ? -h : h;
                Vector3 a, b;
                switch (axis)
                {
                    case 0: a = new(-h, u, w); b = new(h, u, w); break;
                    case 1: a = new(u, -h, w); b = new(u, h, w); break;
                    default: a = new(u, w, -h); b = new(u, w, h); break;
                }
                Line(into, c + a, c + b, color);
            }
    }

    /// <summary>A line as a strip facing the head, as wide as <see cref="Beacons.Width"/> pixels wherever it is (split
    /// where it passes nearest the head and wherever its distance changes by a quarter, so the width holds along it).</summary>
    void Line(List<ParcelLineVertex> into, Vector3 a, Vector3 b, uint color)
    {
        var d = b - a;
        float len2 = d.LengthSquared();
        if (len2 < 1e-10f) return;
        float tNear = Math.Clamp(Vector3.Dot(_head - a, d) / len2, 0, 1);
        if (tNear > 0 && tNear < 1)
        {
            Piece(into, a, a + d * tNear, d, color);
            Piece(into, a + d * tNear, b, d, color);
        }
        else Piece(into, a, b, d, color);
    }

    void Piece(List<ParcelLineVertex> into, Vector3 a, Vector3 b, Vector3 dir, uint color)
    {
        float da = MathF.Max(Vector3.Distance(a, _head), 0.01f), db = MathF.Max(Vector3.Distance(b, _head), 0.01f);
        int n = Math.Clamp((int)MathF.Ceiling(MathF.Log(MathF.Max(da, db) / MathF.Min(da, db)) / MathF.Log(1.25f)), 1, 64);
        var prev = a;
        var prevSide = Side(a, dir);
        for (int i = 1; i <= n; i++)
        {
            var p = Vector3.Lerp(a, b, i / (float)n);
            var side = Side(p, dir);
            void V(Vector3 q) => into.Add(new ParcelLineVertex { Pos = q, Color = color, Tex = -1 });
            V(prev - prevSide); V(prev + prevSide); V(p + side);
            V(prev - prevSide); V(p + side); V(p - side);
            (prev, prevSide) = (p, side);
        }
    }

    Vector3 Side(Vector3 p, Vector3 dir)
    {
        var toward = p - _head;
        float dist = toward.Length();
        var s = Vector3.Cross(dir, toward);
        float l = s.Length();
        if (l < 1e-8f) return Vector3.Zero;   // seen end on
        return s / l * (0.5f * _width * _pxAngle * dist);
    }

    /// <summary>An object's faces highlighted with its drawn shape and where it's drawn (the frame's region frame).</summary>
    public void Faces(ShapeGeometry shape, Vector3 at, Quaternion rot, Vector3 scale, uint localId, FaceTint tint, Func<FaceGeometry, bool>? only = null)
    {
        uint inst = uint.MaxValue;
        var list = Dl.Beacons.Faces[(int)tint];
        foreach (var f in shape.Faces)
        {
            if (f.Range.IsEmpty || f.Weights.Count > 0 || only != null && !only(f)) continue;
            if (inst == uint.MaxValue) inst = Dl.AddInstance(Instance(at, rot, scale, localId));
            list.Add(new DrawCmd { Range = f.Range, Instance = inst });
        }
    }

    static GpuInstance Instance(Vector3 at, Quaternion rot, Vector3 scale, uint localId)
    {
        var model = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(at);
        Matrix4x4.Invert(model, out var inv);
        var nm = Matrix4x4.Transpose(inv);
        return new GpuInstance
        {
            Model = model,
            N0 = new Vector4(nm.M11, nm.M12, nm.M13, 0), N1 = new Vector4(nm.M21, nm.M22, nm.M23, 0), N2 = new Vector4(nm.M31, nm.M32, nm.M33, 0),
            ObjectId = localId,
        };
    }
}
