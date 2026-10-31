using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using LibreMetaverse;
using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Render.Realtime;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Showing;

/// <summary>
/// What the assistant has put on screen to show the person something (SojournShow on the agent API): cards and labels,
/// each with an anchor (with the person, or out in the world) and blocks of content. This holds them, enforces the
/// caps (<see cref="Caps"/>: how many, how big, how often they change), keeps their moving parts current (timers,
/// pointers, maps), draws them off the main thread when they change and packs them into one atlas for the overlay.
/// Where they go on screen is <see cref="ShownView"/>'s business.
///
/// The person never operates anything here: an element can only be dismissed (its close key), which takes it away and
/// tells the assistant; anything else goes through the assistant. Thread-safe: the API changes it, the main thread
/// reads it each frame, drawing runs on the thread pool.
/// </summary>
public sealed class Shown : IShowing
{
    readonly AgentSession _s;
    readonly Func<PanelLook> _look;
    readonly object _lock = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    public double Now => _clock.Elapsed.TotalSeconds;

    sealed class Element(ElementSpec spec, double now)
    {
        public ElementSpec Spec = spec;
        public int Version;
        public double Changed = now;
        public double? DoneAt;
        public bool Unseen;       // anchored on something not around (gone, another region)
        public double TopAt = -1; // when the linkset's height was last worked out
        public float Top;         // a thing's top above its position (metres)
        public bool Sealed;       // holds a picture from an incognito spell: goes when the spell ends or its seeing changes
    }

    readonly List<Element> _elements = new();   // in the order they were first shown
    long _version;
    DateTime _stretch = DateTime.UtcNow;         // when incognito last came on, went off or changed its seeing

    public Shown(AgentSession session, Func<PanelLook> look)
    {
        _s = session;
        _look = look;
        _s.Private.Changed += (_, _, _) => SpellChanged();
        _s.Private.SeesChanged += _ => SpellChanged();
    }

    /// <summary>Changes whenever an element is added, changed or removed.</summary>
    public long Version => Interlocked.Read(ref _version);
    public int Count { get { lock (_lock) return _elements.Count; } }
    /// <summary>Something's anchored on an avatar (the renderer then reports where heads are: FrameRequest.ShowNames).</summary>
    public bool WantsHeads { get { lock (_lock) return _elements.Any(e => e.Spec.At.Kind == AnchorKind.Thing); } }

    void Send(JsonObject data) => _s.Events.Log("sojourn", data);

    ulong Here => _s.Client?.Network.CurrentSim?.Handle ?? 0;

    // --- the API's side -----------------------------------------------------------------------------------------------

    /// <summary>Show an element, or change one already shown (the fields given replace those it had).</summary>
    public JsonObject Show(JsonObject a)
    {
        var id = ElementParse.Id(a);
        double now = Now;
        // parsed outside the lock: a picture lists its folder and reads its header, and the frame waits on the lock
        ElementSpec? old;
        DateTime stretch;
        lock (_lock) (old, stretch) = (_elements.FirstOrDefault(e => e.Spec.Id == id)?.Spec, _stretch);
        var parsed = ElementParse.Parse(a, old, Here, now, Picture);
        lock (_lock)
        {
            // incognito changed meanwhile: what was allowed then may not be now
            if (_stretch != stretch) throw new LeapError("incognito changed just then: show it again");
            var have = _elements.FirstOrDefault(e => e.Spec.Id == id);
            var spec = Named(parsed);
            if (have != null)
            {
                have.Spec = spec;
                have.Version++;
                have.Changed = now;
                have.DoneAt = null;
                have.TopAt = -1;
            }
            else _elements.Add(have = new Element(spec, now));
            // the view taken during a spell, or a picture taken in one, isn't kept past it (nor shown to the next kind)
            have.Sealed |= spec.Blocks.OfType<ImageBlock>().Any(b => b.IsPicture ? Pictures.Unseen(b.Source) : b.Source == "view" && _s.Private.On);
            foreach (var b in spec.Blocks.OfType<ImageBlock>()) WantImage(b);
            Interlocked.Increment(ref _version);
            return Describe(have);
        }
    }

    public JsonObject Remove(JsonObject a)
    {
        lock (_lock)
        {
            if (a.Bool("all", false) || a.Str("id") == "all")
            {
                int n = _elements.Count;
                _elements.Clear();
                Interlocked.Increment(ref _version);
                return new JsonObject { ["removed"] = n };
            }
            var id = ElementParse.Id(a);
            int gone = _elements.RemoveAll(e => e.Spec.Id == id);
            if (gone == 0) throw new LeapError($"nothing shown is called {id}");
            Interlocked.Increment(ref _version);
            return new JsonObject { ["removed"] = id };
        }
    }

    /// <summary>The person has hidden the interface (X, Tab; VrSettings.InterfaceHidden): nothing shown is seen until
    /// they show it again.</summary>
    public Func<bool>? InterfaceHidden { get; set; }

    public JsonObject List()
    {
        lock (_lock)
        {
            var o = new JsonObject { ["shown"] = new JsonArray(_elements.Select(e => (JsonNode)Describe(e)).ToArray()) };
            if (InterfaceHidden?.Invoke() == true) o["interface_hidden"] = true;
            return o;
        }
    }

    /// <summary>A line for the assistant's context: what's on screen now (null when nothing is).</summary>
    public string? Summary()
    {
        lock (_lock)
        {
            if (_elements.Count == 0) return null;
            return string.Join("; ", _elements.Select(e => $"{e.Spec.Id} ({(e.Spec.At.Kind == AnchorKind.Thing && NameOf(e.Spec.At.Where.Thing) is { } n ? "on " + n : e.Spec.At.Describe())}: {string.Join(", ", e.Spec.Blocks.Select(b => b.Kind).Distinct())}{(e.Unseen ? ", not in view" : "")})"));
        }
    }

    JsonObject Describe(Element e)
    {
        var o = new JsonObject
        {
            ["id"] = e.Spec.Id,
            ["at"] = e.Spec.At.Kind == AnchorKind.Thing && NameOf(e.Spec.At.Where.Thing) is { } name ? $"on {name}" : e.Spec.At.Describe(),
            ["blocks"] = new JsonArray(e.Spec.Blocks.Select(b => (JsonNode)b.Kind).ToArray()),
            ["age_s"] = Math.Round(Now - e.Changed),
        };
        if (e.Spec.Title != null) o["title"] = e.Spec.Title;
        if (e.Spec.ForSeconds is { } f) o["goes_in_s"] = Math.Max(0, Math.Round(f - (Now - e.Changed)));
        if (e.Unseen) o["not_in_view"] = true;
        if (e.DoneAt != null) o["timer_done"] = true;
        return o;
    }

    /// <summary>The person pressed its close key: it goes, and the assistant hears of it.</summary>
    public void Dismiss(string id)
    {
        lock (_lock)
            if (_elements.RemoveAll(e => e.Spec.Id == id) == 0) return;
        Interlocked.Increment(ref _version);
        Send(new JsonObject { ["what"] = "shown_dismissed", ["id"] = id });
    }

    // --- pictures in image blocks -------------------------------------------------------------------------------------

    /// <summary>A saved picture for an image block, if it may be shown now. Incognito's rule is that the assistant never
    /// sees pictures taken in a spell, and a shown card can be seen (a look with the menus), so: blind, only the last
    /// picture (any: it can't look at all, and naming files would let it probe for them); letting it see, those taken in
    /// incognito only from since it could see; out of a spell, none taken in one. Cards holding them go when the spell
    /// ends or its seeing changes (<see cref="SpellChanged"/>).</summary>
    ImageBlock Picture(string words)
    {
        var p = _s.Private;
        bool last = Pictures.IsLast(words);
        if (p.IsBlind && !last) throw new LeapError("while incognito, only the last picture taken: {\"image\": \"last\"}");
        var path = Pictures.Resolve(words);
        if (!p.IsBlind && Pictures.Unseen(path))
        {
            if (!p.On)
                throw new LeapError($"{Path.GetFileName(path)} was taken in incognito: those show only during incognito, so you never see them; the person can open it from Pictures\\Sojourn");
            DateTime since;
            lock (_lock) since = _stretch;
            var t = File.GetLastWriteTimeUtc(path);
            if (t < since || t > DateTime.UtcNow.AddMinutes(1))
                throw new LeapError($"{Path.GetFileName(path)} was taken in incognito before you could see; it isn't shown while you can");
        }
        var (w, h) = Pictures.SizeOf(path);
        return new ImageBlock(path, w, h);
    }

    /// <summary>Incognito came on, went off, or changed whether the assistant sees: what holds a picture from the spell
    /// goes, so it's neither kept nor seen by the next kind.</summary>
    void SpellChanged()
    {
        int gone;
        lock (_lock)
        {
            _stretch = DateTime.UtcNow;
            gone = _elements.RemoveAll(e => e.Sealed);
        }
        if (gone > 0) Interlocked.Increment(ref _version);
    }

    readonly ConditionalWeakTable<ImageBlock, SKImage> _images = new();
    readonly ConditionalWeakTable<ImageBlock, object> _asked = new();
    readonly List<ImageBlock> _viewWanted = new();
    int _imagesReady;

    void WantImage(ImageBlock b)
    {
        if (_asked.TryGetValue(b, out _)) return;
        _asked.Add(b, new object());
        if (b.Source == "view") { lock (_viewWanted) _viewWanted.Add(b); return; }
        if (b.IsPicture)
        {
            _ = Task.Run(() =>
            {
                try { if (Pictures.Decode(b.Source) is { } img) Ready(b, img); }
                catch { }
            });
            return;
        }
        if (!UUID.TryParse(b.Source, out var id) || _s.Client is not { } c) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var tex = await c.Assets.RequestImageAsync(id, ImageType.Normal, cts.Token);
                if (tex?.AssetData is not { Length: > 0 } data) return;
                // a quarter of the full size is plenty for a card, and quick to decode
                var dec = Sojourn.Render.World.OpenJpeg.Decode(data, data.Length > 200_000 ? 2 : 1) ?? Sojourn.Render.World.OpenJpeg.Decode(data, 0);
                if (dec is not { } d) return;
                var img = FromPlanes(d.Planes, d.Width, d.Height);
                if (img != null) Ready(b, img);
            }
            catch { }
        });
    }

    void Ready(ImageBlock b, SKImage img)
    {
        _images.AddOrUpdate(b, img);
        Interlocked.Increment(ref _imagesReady);
    }

    static SKImage? FromPlanes(byte[][] planes, int w, int h)
    {
        if (w <= 0 || h <= 0 || planes.Length == 0) return null;
        var rgba = new byte[w * h * 4];
        for (int i = 0, n = w * h; i < n; i++)
        {
            byte r = planes[0][i], g = planes.Length >= 3 ? planes[1][i] : r, bl = planes.Length >= 3 ? planes[2][i] : r;
            byte al = planes.Length == 4 ? planes[3][i] : planes.Length == 2 ? planes[1][i] : (byte)255;
            // premultiplied
            rgba[i * 4] = (byte)(r * al / 255); rgba[i * 4 + 1] = (byte)(g * al / 255); rgba[i * 4 + 2] = (byte)(bl * al / 255); rgba[i * 4 + 3] = al;
        }
        return SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), rgba, w * 4);
    }

    /// <summary>For the frame about to render: a capture of the view when an image block asked for it (taken before the
    /// overlay, so it never shows the panel or the elements themselves).</summary>
    public Action<byte[], int, int>? Take()
    {
        List<ImageBlock> want;
        lock (_viewWanted)
        {
            if (_viewWanted.Count == 0) return null;
            want = _viewWanted.ToList();
            _viewWanted.Clear();
        }
        return (rgba, w, h) =>
        {
            try
            {
                using var src = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
                System.Runtime.InteropServices.Marshal.Copy(rgba, 0, src.GetPixels(), rgba.Length);
                float k = Math.Min(1f, 720f / w);
                using var scaled = src.Resize(new SKImageInfo(Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k)), SKColorType.Rgba8888, SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell));
                var img = SKImage.FromBitmap(scaled);
                foreach (var b in want) Ready(b, img);
            }
            catch { }
        };
    }

    SKImage? ImageFor(ImageBlock b) => _images.TryGetValue(b, out var img) ? img : null;

    // --- every frame --------------------------------------------------------------------------------------------------

    /// <summary>Where the person is this frame: the region the frame is drawn in, the eye in it, and the way they face.</summary>
    public readonly record struct Viewer(ulong Region, Vector3 Eye, Vector3 Forward);

    /// <summary>A drawn element: its picture for its own layer (the headset), where it is in the atlas, and what it was
    /// drawn from.</summary>
    public sealed record Placed(string Id, ElementSpec Spec, ElementPainter.Drawn Drawn, OverlayImage Image, SKRectI InAtlas, float AtlasScale);

    public sealed record Built(OverlayImage? Atlas, IReadOnlyList<Placed> Items, string Key);

    /// <summary>The scales to draw at: an element's own picture (the headset's layers, the desktop) and, for the
    /// headset's cards on their own layers, the atlas copy that only gives their frost (and the in-eyes preview).</summary>
    public readonly record struct Scales(float WithYou, float World, float AtlasWithYou, float Wide = 1);

    volatile Built? _built;
    bool _building;
    double _builtAt;
    string? _hoverClose;
    public string? HoverClose { get => _hoverClose; set => _hoverClose = value; }

    /// <summary>This frame's elements, drawn: the last finished drawing (one is started off the main thread when what
    /// they'd show has changed: the content, a timer's second, which way something lies, the look, the scale).</summary>
    public Built? Frame(Viewer v, Scales scales)
    {
        double now = Now;
        List<(Element E, BlockLive[] Live, string Key)> want;
        lock (_lock)
        {
            // timers that ran out; elements whose time is up
            foreach (var e in _elements.ToList())
            {
                if (e.Spec.ForSeconds is { } f && now - e.Changed > f) { _elements.Remove(e); Interlocked.Increment(ref _version); continue; }
                if (e.DoneAt is { } done && now - done > 60 && e.Spec.ForSeconds == null && e.Spec.Blocks.All(b => b is TimerBlock or TextBlock))
                { _elements.Remove(e); Interlocked.Increment(ref _version); }
            }
            want = new();
            foreach (var e in _elements)
            {
                var live = Live(e, v, now);
                want.Add((e, live, $"{e.Spec.Id}:{e.Version}/{string.Join("/", live.Select(l => l.Key))}"));
            }
        }
        var look = _look();
        string key = $"{RuntimeHelpers.GetHashCode(look)}|{scales}|{_hoverClose}|{Volatile.Read(ref _imagesReady)}|{string.Join("|", want.Select(w => w.Key))}";
        var have = _built;
        // live parts redraw at most 8 times a second; a change of content at once
        bool contentChanged = have == null || StripLive(have.Key) != StripLive(key);
        if (key != have?.Key && !_building && (contentChanged || now - _builtAt > 0.125))
        {
            _building = true;
            _builtAt = now;
            var specs = want.Select(w => (w.E.Spec, w.Live)).ToList();
            var hover = _hoverClose;
            Task.Run(() =>
            {
                try { _built = Build(specs, look, scales, hover, key); }
                catch { }
                finally { _building = false; }
            });
        }
        return have != null && want.Count == 0 ? null : have;
    }

    static string StripLive(string key) => string.Join("|", key.Split('|').Select(p => p.Split('/')[0]));

    BlockLive[] Live(Element e, Viewer v, double now)
    {
        var blocks = e.Spec.Blocks;
        var live = new BlockLive[blocks.Count];
        var fwd = new Vector3(v.Forward.X, v.Forward.Y, 0);
        float heading = fwd.LengthSquared() > 1e-6f ? MathF.Atan2(fwd.Y, fwd.X) : 0;
        e.Unseen = e.Spec.At.Kind == AnchorKind.Thing && Resolve(e.Spec.At.Where, v.Region) == null;
        // how far and which way from where they stand (the avatar; the desktop's camera can be metres behind it), against
        // the way the view faces
        var from = v.Eye;
        if (_s.Client is { Network.CurrentSim: { } sim } client && sim.Handle == v.Region)
        {
            var me = client.Self.SimPosition;
            from = new Vector3(me.X, me.Y, me.Z + 0.6f);
        }
        for (int i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case TimerBlock t when t.Up:
                    live[i] = new BlockLive(Seconds: (int)Math.Floor(now - t.Started));
                    break;
                case TimerBlock t:
                {
                    double left = t.Seconds - (now - t.Started);
                    live[i] = new BlockLive(Seconds: (int)Math.Ceiling(Math.Max(0, left)), Done: left <= 0);
                    if (left <= 0 && e.DoneAt == null)
                    {
                        e.DoneAt = now;
                        Send(new JsonObject { ["what"] = "shown_done", ["id"] = e.Spec.Id, ["title"] = e.Spec.Title });
                    }
                    break;
                }
                case TowardBlock tw:
                    if (Resolve(tw.To, v.Region) is { } p0 && (float.IsNaN(p0.Z) ? p0 with { Z = from.Z } : p0) is var p)
                    {
                        var d = p - from;
                        float dist = d.Length();
                        float bearing = (heading - MathF.Atan2(d.Y, d.X)) * 180 / MathF.PI;   // clockwise from ahead
                        int q = (int)MathF.Round(Wrap(bearing) / 5) * 5;
                        int m = dist < 10 ? (int)MathF.Round(dist) : dist < 100 ? (int)MathF.Round(dist / 2) * 2 : (int)MathF.Round(dist / 10) * 10;
                        live[i] = new BlockLive(Bearing: q, Distance: m, Known: true);
                    }
                    break;
                case MapBlock mb:
                {
                    var rel = mb.Points.Select(pt => Resolve(pt.At, v.Region) is { } p ? (Vector3?)((float.IsNaN(p.Z) ? p with { Z = from.Z } : p) - from) : null).ToList();
                    float far = rel.Where(r => r != null).Select(r => new System.Numerics.Vector2(r!.Value.X, r.Value.Y).Length()).DefaultIfEmpty(0).Max();
                    int radius = NiceRadius(far * 1.15f);
                    float c = MathF.Cos(heading), s = MathF.Sin(heading);
                    var marks = rel.Select(r =>
                    {
                        if (r is not { } d) return (0, 0, false);
                        // track up: ahead is up, right is right
                        float ahead = d.X * c + d.Y * s, right = d.X * s - d.Y * c;
                        return ((int)MathF.Round(right / radius * 100) * 10, (int)MathF.Round(ahead / radius * 100) * 10, true);
                    }).ToArray();
                    int north = (int)MathF.Round(Wrap((heading - MathF.PI / 2) * 180 / MathF.PI) / 5) * 5;
                    live[i] = new BlockLive(Marks: marks, MapRadius: radius, North: north);
                    break;
                }
            }
        }
        return live;
    }

    static int NiceRadius(float r)
    {
        foreach (var n in new[] { 10, 20, 30, 50, 75, 100, 150, 200, 300, 500, 1000, 2000 }) if (r <= n) return n;
        return 5000;
    }

    static float Wrap(float deg)
    {
        while (deg > 180) deg -= 360;
        while (deg <= -180) deg += 360;
        return deg;
    }

    Built Build(List<(ElementSpec Spec, BlockLive[] Live)> specs, PanelLook look, Scales scales, string? hover, string key)
    {
        var drawn = new List<(ElementSpec Spec, ElementPainter.Drawn D, ElementPainter.Drawn A, float AtlasScale)>();
        foreach (var (spec, live) in specs)
        {
            bool world = spec.At.InWorld;
            float scale = world ? scales.World : scales.WithYou;
            float wide = world ? 1 : scales.Wide;
            var d = ElementPainter.Draw(spec, live, ImageFor, look, scale, hover == spec.Id, wide, layer: !world && GlassLight.Layers);
            // the atlas copy: the same picture, or a smaller one where it only gives the frost
            var a = !world && scales.AtlasWithYou < scale - 0.01f ? ElementPainter.Draw(spec, live, ImageFor, look, scales.AtlasWithYou, hover == spec.Id, wide) : d;
            drawn.Add((spec, d, a, world ? scale : Math.Min(scale, scales.AtlasWithYou)));
        }
        // shelves, tallest first
        // as wide as the widest, or about square when there's a lot (nothing caps how much is shown, and an image has
        // a side limit on the GPU, 16384 here)
        double area = drawn.Sum(d => (double)(d.A.Width + 2) * (d.A.Height + 2));
        int width = Math.Clamp(Math.Max(drawn.Select(d => d.A.Width).DefaultIfEmpty(1).Max(), (int)Math.Ceiling(Math.Sqrt(area) * 1.1)), 256, 16384);
        int x = 0, y = 0, row = 0;
        var at = new Dictionary<int, SKRectI>();
        foreach (var (d, i) in drawn.Select((d, i) => (d, i)).OrderByDescending(p => p.d.A.Height))
        {
            if (x + d.A.Width > width) { x = 0; y += row + 2; row = 0; }
            at[i] = new SKRectI(x, y, x + d.A.Width, y + d.A.Height);
            x += d.A.Width + 2;
            row = Math.Max(row, d.A.Height);
        }
        int height = Math.Max(1, y + row);
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < drawn.Count; i++)
        {
            var a = drawn[i].A;
            var r = at[i];
            for (int yy = 0; yy < a.Height; yy++)
                Buffer.BlockCopy(a.Rgba, yy * a.Width * 4, rgba, ((r.Top + yy) * width + r.Left) * 4, a.Width * 4);
        }
        long v = Interlocked.Increment(ref _imageVersion);
        var atlas = drawn.Count == 0 ? null : new OverlayImage(rgba, width, height, v);
        var items = drawn.Select((d, i) => new Placed(d.Spec.Id, d.Spec, d.D,
            new OverlayImage(d.D.Rgba, d.D.Width, d.D.Height, Interlocked.Increment(ref _imageVersion), d.D.Frost, d.D.Edge), at[i], d.AtlasScale)).ToList();
        return new Built(atlas, items, key);
    }

    long _imageVersion;

    // --- where things are ---------------------------------------------------------------------------------------------

    /// <summary>A target's position in the frame's region (null: not around). A point given without a height sits at
    /// the person's feet level (NaN z: the caller fills it in).</summary>
    public Vector3? Resolve(Target t, ulong frame)
    {
        if (frame == 0) return null;
        Utils.LongToUInts(frame, out uint fx, out uint fy);
        if (!t.IsThing) return new Vector3((float)(t.Global.X - fx), (float)(t.Global.Y - fy), (float)t.Global.Z);
        foreach (var w in _s.VisibleRegions())
        {
            Primitive? p = w.PrimById(t.Thing) ?? w.AvatarById(t.Thing);
            if (p == null) continue;
            var pos = w.RegionPosition(p);
            Utils.LongToUInts(w.Handle, out uint wx, out uint wy);
            return new Vector3(pos.X + ((float)((long)wx - fx)), pos.Y + ((float)((long)wy - fy)), pos.Z);
        }
        return null;
    }

    /// <summary>Map places and arrows given by id without words get the thing's own name.</summary>
    ElementSpec Named(ElementSpec spec)
    {
        string? Short(Target t) => t.IsThing && NameOf(t.Thing) is { Length: > 0 } n ? (n.Length > 20 ? n[..19] + "…" : n) : null;
        var blocks = spec.Blocks.Select(b => b switch
        {
            MapBlock m when m.Points.Any(p => p.Label == null) => m with { Points = m.Points.Select(p => (p.At, p.Label ?? Short(p.At))).ToList() },
            TowardBlock t when t.Label == null && spec.Title == null => t with { Label = Short(t.To) },
            _ => b,
        }).ToList();
        return spec with { Blocks = blocks };
    }

    /// <summary>What a thing is called: a person's display name, an object's name (null if not known).</summary>
    string? NameOf(UUID id)
    {
        foreach (var w in _s.VisibleRegions())
        {
            if (w.AvatarById(id) is { } av) return _s.Names.DisplayName(id) ?? av.Name;
            if (w.PrimById(id) is { } p) return p.Properties?.Name;
        }
        return null;
    }

    /// <summary>Is the thing an avatar (its card sits over its head, above the name tag)?</summary>
    public bool IsAvatar(UUID id)
    {
        foreach (var w in _s.VisibleRegions()) if (w.AvatarById(id) != null) return true;
        return false;
    }

    /// <summary>How far above its position a thing's top is: its linkset's highest point (worked out every few
    /// seconds).</summary>
    public float TopOf(string id, UUID thing)
    {
        Element? e;
        lock (_lock) e = _elements.FirstOrDefault(x => x.Spec.Id == id);
        if (e == null) return 0.5f;
        double now = Now;
        if (e.TopAt >= 0 && now - e.TopAt < 3) return e.Top;
        e.TopAt = now;
        float top = 0.5f;
        foreach (var w in _s.VisibleRegions())
        {
            if (w.PrimById(thing) is not { } p) continue;
            var root = _s.Objects.RootOf(w, p);
            var rootPos = w.RegionPosition(root);
            float hi = float.MinValue;
            foreach (var part in _s.Objects.Linkset(w, root))
            {
                var pp = w.RegionPosition(part);
                hi = Math.Max(hi, pp.Z + part.Scale.Length() * 0.5f);
            }
            var at = w.RegionPosition(p);
            top = hi > float.MinValue ? Math.Clamp(hi - at.Z, 0.05f, 30) : p.Scale.Z / 2;
            break;
        }
        e.Top = top;
        return top;
    }
}
