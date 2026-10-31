using System.Collections.Concurrent;
using LibreMetaverse;
using Sojourn.Client.Control;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Hud;

/// <summary>
/// The person's HUD attachments (points 31-38), as the viewer has them: drawn over the view on the desktop (Alt+Shift+H
/// hides them), in the headset on a board the person calls up (<see cref="HudBoard"/>); touched where the pointer lands
/// on them, with the same surface data a click in the viewer sends (face, ST, UV, and position, normal and binormal in
/// HUD space); and pictured for the assistant, which presses them by position on a picture or by a prim's name
/// (<see cref="HudApi"/>). The renderer places and draws them (Sojourn.Render Realtime/Hud.cs); this keeps what's worn,
/// the person's press and the assistant's pictures. Main thread, except what goes through <see cref="OnMain"/>.
/// </summary>
public sealed class HudView
{
    /// <summary>The headset board's HUD screen: 16:9, as most of the monitors HUDs are laid out on.</summary>
    public const float BoardAspect = 16f / 9f;
    public const int BoardWidth = 1920, BoardHeight = 1080;

    readonly AgentSession _s;
    readonly FrameRenderer _r;
    readonly DirectTouch _touch;
    readonly ConcurrentQueue<Action> _main = new();
    readonly HudText _text;

    public HudView(AgentSession session, FrameRenderer renderer, DirectTouch touch)
    {
        _s = session;
        _r = renderer;
        _touch = touch;
        _text = new HudText(session);
        touch.FindHud = FindHud;
    }

    /// <summary>Media on HUD faces (Media/MediaWiring): a press on the headset's board where a HUD face has media goes
    /// to its page, as the viewer's click does, rather than the HUD's touch. hand: 0 left, 1 right; dir: the hand's
    /// ray (for telling a tap from a drag). The desktop's clicks reach it through media's own desktop path.</summary>
    public interface IMedia
    {
        /// <summary>A press on a HUD hit: true if media took it (it starts, or a gesture on its page begins).</summary>
        bool Down(int hand, PickHit hit, Vector3 dir);
        void Held(int hand, PickHit? hit, Vector3 dir);
        void Up(int hand, PickHit? hit);
        void Cancel(int hand);
        /// <summary>How far that hand's still hold is toward bringing the page up close (0-1), while it is one.</summary>
        float? Filling(int hand);
        /// <summary>Media a press would act on is under this hit (the board's cue).</summary>
        bool On(PickHit? hit);
    }

    public IMedia? Media { get; set; }

    /// <summary>Desktop: HUDs drawn (the viewer's View > Show HUD Attachments, Alt+Shift+H).</summary>
    public bool DesktopShown { get; set; } = true;

    /// <summary>What the last frame placed: the region, the screen's aspect, the HUDs.</summary>
    public HudsPlaced Worn => _r.Huds;
    public bool Any => _r.Huds.Objects.Count > 0;

    /// <summary>Run on the main thread at the next frame (the renderer's picks and the grab state live there).</summary>
    public void OnMain(Action a) => _main.Enqueue(a);

    // --- each frame --------------------------------------------------------------------------------------------------

    HudShot? _shotWanted;

    /// <summary>Main thread, at the start of each frame.</summary>
    public void Drain()
    {
        while (_main.TryDequeue(out var a)) a();
    }

    (ulong Region, uint Self)? Me => _s.Client is { Network.CurrentSim: { } sim } c ? (sim.Handle, c.Self.LocalID) : null;

    /// <summary>The desktop's request: the HUDs over the whole view, as the viewer draws them. hidden: not this frame
    /// (a confirmation waits: nothing an object made sits beside it).</summary>
    public HudRequest? Desktop(int width, int height, bool hidden)
    {
        if (Pressing) Hold();
        if (Me is not { } me) return null;
        var shot = TakeShot();
        bool shown = DesktopShown && !hidden;
        return new HudRequest
        {
            Region = me.Region, Self = me.Self, Aspect = (float)width / height,
            Width = shown ? width : 0, Height = shown ? height : 0, OverEyes = true, Shot = shot,
            Labels = shown || shot != null ? _text.Labels(_r.Huds, height, 1f) : null,
        };
    }

    /// <summary>Desktop: the mouse over the view (0-1 across, 0-1 down), or null while it steers or is elsewhere.</summary>
    public Vector2? Mouse { get; set; }

    /// <summary>Desktop: the HUD prim under the mouse, when HUDs are drawn.</summary>
    public PickHit? DesktopUnder => DesktopShown && Mouse is { } m && m.X is >= 0 and <= 1 && m.Y is >= 0 and <= 1 ? PickAt(m) : null;

    /// <summary>Desktop: a left click; true if it was on a HUD (it's the HUD's, not the world's).</summary>
    public bool DesktopDown()
    {
        if (DesktopUnder is not { } h) return false;
        Press(h, () => Mouse);
        return true;
    }

    /// <summary>The headset's request: the board's screen image when it shows (linear, for its sRGB layer), and the
    /// board drawn into the eyes too at corners (for checking without a headset).</summary>
    public HudRequest? Headset(bool board, Vector3[]? corners)
    {
        if (Pressing) Hold();
        if (Me is not { } me) return null;
        var shot = TakeShot();
        return new HudRequest
        {
            Region = me.Region, Self = me.Self, Aspect = BoardAspect,
            Width = board ? BoardWidth : 0, Height = board ? BoardHeight : 0, Linear = true, Corners = board ? corners : null, Shot = shot,
            Labels = board || shot != null ? _text.Labels(_r.Huds, BoardHeight, 4f / 3) : null,
        };
    }

    HudShot? TakeShot()
    {
        var s = _shotWanted;
        _shotWanted = null;
        return s;
    }

    // --- picking -----------------------------------------------------------------------------------------------------

    /// <summary>The HUD prim under a point of the screen (0-1 across, 0-1 down), on the last frame's screen.</summary>
    public PickHit? PickAt(Vector2 uv, Func<Primitive, bool>? only = null)
    {
        var (region, aspect, objects) = _r.Huds;
        if (objects.Count == 0 || Me is not { } me || me.Region != region) return null;
        var p = Sojourn.Render.Realtime.Hud.FromScreen(uv, aspect);
        return _r.HudPick(region, me.Self, aspect, new Vector2(p.Y, p.Z), only);
    }

    /// <summary>The HUD prim at a point of HUD space (y, z).</summary>
    public PickHit? PickAtHud(Vector2 yz, Func<Primitive, bool>? only = null)
    {
        var (region, aspect, objects) = _r.Huds;
        if (objects.Count == 0 || Me is not { } me || me.Region != region) return null;
        return _r.HudPick(region, me.Self, aspect, yz, only);
    }

    /// <summary>What a press on a HUD prim does: a touch if it (or its root) takes touches; HUDs have no click actions.</summary>
    public bool Touchable(PickHit? hit) => _touch.Classify(hit) == DirectAct.Touch;

    /// <summary>A part of a worn HUD by prim id, and the HUD it belongs to.</summary>
    public (HudObject Hud, HudPart Part)? PartOf(UUID prim)
    {
        foreach (var h in _r.Huds.Objects)
            foreach (var p in h.Parts)
                if (p.Id == prim) return (h, p);
        return null;
    }

    /// <summary>Where a touch on a HUD prim lands for requestTouch (the agent API): the middle of the part as seen on
    /// the screen, a few other points of it if that misses (a ring, a cut-out); null for anything not on a HUD.</summary>
    TouchPoint? FindHud(UUID prim)
    {
        if (PartOf(prim) is not { } found) return null;
        var hit = PickPart(found.Part);
        return hit is { } h ? new TouchPoint(h.Region, h.LocalId, h.Id, Surface(h)) : new TouchPoint(_r.Huds.Region, found.Part.LocalId, prim, TouchSurface.None);
    }

    /// <summary>A pick on one part alone, at its middle or, failing that, a few points across it.</summary>
    public PickHit? PickPart(HudPart part)
    {
        var lo = new Vector2(part.Min.Y, part.Min.Z);
        var hi = new Vector2(part.Max.Y, part.Max.Z);
        foreach (var f in new[] { (0.5f, 0.5f), (0.3f, 0.3f), (0.7f, 0.3f), (0.3f, 0.7f), (0.7f, 0.7f), (0.5f, 0.2f), (0.5f, 0.8f), (0.2f, 0.5f), (0.8f, 0.5f) })
        {
            var at = new Vector2(lo.X + (hi.X - lo.X) * f.Item1, lo.Y + (hi.Y - lo.Y) * f.Item2);
            if (PickAtHud(at, p => p.ID == part.Id) is { } h) return h;
        }
        return null;
    }

    public static TouchSurface Surface(PickHit h) => h.Surface is { } s
        ? new TouchSurface(s.Face, L(s.ST), L(s.UV), L(h.Point), L(s.Normal), L(s.Binormal))
        : TouchSurface.None with { Position = L(h.Point) };

    static LibreMetaverse.Vector3 L(Vector3 v) => new(v.X, v.Y, v.Z);
    static LibreMetaverse.Vector2 L(Vector2 v) => new(v.X, v.Y);

    // --- the person's press ------------------------------------------------------------------------------------------

    sealed class Down
    {
        public required PickHit Hit;
        public required Func<Vector2?> Where;   // the press's point on the screen now (0-1), or null off it
        public TouchSurface Last;
        public long At, Sent;
        public bool Grabbed;
    }
    Down? _down;

    /// <summary>A press is held on a HUD (by the mouse or either hand).</summary>
    public bool Pressing => _down != null;

    /// <summary>The button goes down on a HUD prim (null: on the board or screen but on no HUD, which presses nothing).
    /// <paramref name="where"/> says where the press is on the screen while it's held. As the viewer: the left button for
    /// scripts on every press, and a touch on a prim that takes touches unless a script has taken that button.</summary>
    public void Press(PickHit? hit, Func<Vector2?> where)
    {
        if (_down != null || hit is not { } h) return;
        _touch.Button(true);
        var d = new Down { Hit = h, Where = where, At = Environment.TickCount64 };
        d.Sent = d.At;
        if (!_touch.ButtonTaken(false) && Touchable(h))
        {
            d.Last = Surface(h);
            _s.SendGrab(h.Region, h.LocalId, d.Last);
            d.Grabbed = true;
        }
        _down = d;
    }

    /// <summary>While held: the touch follows the point across the prim (ObjectGrabUpdate, as the viewer's drag of a
    /// non-physical object), at most 20 times a second and only when something changed.</summary>
    public void Hold()
    {
        _touch.Button(true);
        if (_down is not { Grabbed: true } d) return;
        long now = Environment.TickCount64;
        if (now - d.Sent < 50) return;
        var s = SurfaceNow(d);
        if (s == d.Last) return;
        _s.SendGrabUpdate(d.Hit.Region, d.Hit.Id, LibreMetaverse.Vector3.Zero, s.Position, (uint)Math.Min(now - d.At, uint.MaxValue), s);
        d.Last = s;
        d.At = d.Sent = now;
    }

    public void Release()
    {
        if (_down is not { } d) return;
        if (d.Grabbed) _s.SendDeGrab(d.Hit.Region, d.Hit.LocalId, SurfaceNow(d));
        _touch.Button(false);
        _down = null;
    }

    TouchSurface SurfaceNow(Down d) =>
        d.Where() is { } uv && PickAt(uv, p => p.ID == d.Hit.Id) is { } h ? Surface(h) : TouchSurface.None;

    // --- pictures for the assistant ----------------------------------------------------------------------------------

    /// <summary>A picture of a window of HUD space: its middle (y, z), half height, size in pixels.</summary>
    public sealed record Picture(int Number, Vector2 Center, float HalfHeight, int Width, int Height, float Aspect, ulong Region)
    {
        public float HalfWidth => HalfHeight * Width / Height;

        /// <summary>A pixel of the picture in HUD space (y, z).</summary>
        public Vector2 ToHud(float x, float y) =>
            new(Center.X - (2 * x / Width - 1) * HalfWidth, Center.Y - (2 * y / Height - 1) * HalfHeight);

        /// <summary>A box of HUD space as the picture's pixels (left, top, width, height), or null if it's outside.</summary>
        public (int X, int Y, int W, int H)? Rect(Vector3 min, Vector3 max)
        {
            float x0 = (Center.X - max.Y) / HalfWidth * 0.5f + 0.5f, x1 = (Center.X - min.Y) / HalfWidth * 0.5f + 0.5f;
            float y0 = (Center.Y - max.Z) / HalfHeight * 0.5f + 0.5f, y1 = (Center.Y - min.Z) / HalfHeight * 0.5f + 0.5f;
            x0 = Math.Clamp(x0, 0, 1); x1 = Math.Clamp(x1, 0, 1); y0 = Math.Clamp(y0, 0, 1); y1 = Math.Clamp(y1, 0, 1);
            if (x1 - x0 < 1e-4f || y1 - y0 < 1e-4f) return null;
            return ((int)MathF.Round(x0 * Width), (int)MathF.Round(y0 * Height), (int)MathF.Round((x1 - x0) * Width), (int)MathF.Round((y1 - y0) * Height));
        }
    }

    readonly List<Picture> _pictures = new();
    int _pictureCount;

    public Picture? PictureNumbered(int n) { lock (_pictures) return _pictures.LastOrDefault(p => p.Number == n); }
    public Picture? LastPicture { get { lock (_pictures) return _pictures.LastOrDefault(); } }

    /// <summary>A picture of the HUD screen (hud null) or of one HUD, over a dark grey (HUDs are made to sit over a
    /// scene): a PNG. The window for one HUD is its box with a margin, sized so the long side is at most 1024 pixels and
    /// small HUDs come out bigger than they show.</summary>
    public Task<(Picture Picture, byte[] Png)?> PictureAsync(HudObject? hud, CancellationToken ct, bool transparent = false)
    {
        var done = new TaskCompletionSource<(Picture, byte[])?>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnMain(() =>
        {
            var (region, aspect, objects) = _r.Huds;
            if (objects.Count == 0) { done.TrySetResult(null); return; }
            Vector2 center;
            float hh;
            int w, h;
            if (hud is { } one)
            {
                float my = 0.04f + (one.Max.Y - one.Min.Y) * 0.08f, mz = 0.04f + (one.Max.Z - one.Min.Z) * 0.08f;
                float bw = one.Max.Y - one.Min.Y + 2 * my, bh = one.Max.Z - one.Min.Z + 2 * mz;
                center = new Vector2((one.Min.Y + one.Max.Y) / 2, (one.Min.Z + one.Max.Z) / 2);
                // small HUDs bigger than they show, and nothing past 1024 pixels a side
                float ppu = Math.Clamp(1024 / Math.Max(bw, bh), 900, 4000);
                ppu = Math.Min(ppu, 1024 / Math.Max(bw, bh));
                w = Math.Max(32, (int)MathF.Round(bw * ppu));
                h = Math.Max(32, (int)MathF.Round(bh * ppu));
                hh = bh / 2;
            }
            else
            {
                center = Vector2.Zero;
                hh = 0.5f;
                w = 1280;
                h = Math.Clamp((int)MathF.Round(1280 / aspect), 320, 1280);
            }
            var pic = new Picture(Interlocked.Increment(ref _pictureCount), center, hh, w, h, aspect, region);
            _shotWanted = new HudShot(center, hh, w, h, (rgba, pw, ph) =>
            {
                try { done.TrySetResult((pic, Png(rgba, pw, ph, transparent))); }
                catch (Exception e) { done.TrySetException(e); }
            });
            lock (_pictures)
            {
                _pictures.Add(pic);
                if (_pictures.Count > 8) _pictures.RemoveAt(0);
            }
        });
        return WithTimeout(done.Task, ct);
    }

    static async Task<(Picture, byte[])?> WithTimeout(Task<(Picture, byte[])?> t, CancellationToken ct)
    {
        try { return await t.WaitAsync(TimeSpan.FromSeconds(5), ct); }
        catch (TimeoutException) { return null; }
    }

    /// <summary>Premultiplied sRGB over a dark grey (or kept transparent), as a PNG.</summary>
    static byte[] Png(byte[] rgba, int w, int h, bool transparent)
    {
        if (transparent)
        {
            using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
            using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        const int grey = 46;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            for (int c = 0; c < 3; c++) rgba[i + c] = (byte)Math.Min(255, rgba[i + c] + grey * (255 - a) / 255);
            rgba[i + 3] = 255;
        }
        return Sojourn.Render.ImageCodec.EncodePng(rgba, w, h);
    }
}
