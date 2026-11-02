using System.Collections.Concurrent;
using LibreMetaverse;
using Sojourn.Client.Control;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Sojourn.Session.Hud;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Hud;

/// <summary>
/// The person's HUD attachments (points 31-38), as the viewer has them: drawn over the view on the desktop (Alt+Shift+H
/// hides them), in the headset on a board the person calls up (<see cref="HudBoard"/>); touched where the pointer lands
/// on them, with the same surface data a click in the viewer sends (face, ST, UV, and position, normal and binormal in
/// HUD space); and pictured for the assistant, which presses them by position on a picture or by a prim's name
/// (Sojourn.Session's HudApi, over this as its <see cref="IHudScreen"/>). The renderer places and draws them (Sojourn.Render
/// Realtime/Hud.cs); this keeps what's worn, the person's press and the assistant's pictures. Main thread, except what
/// goes through <see cref="OnMain"/>.
/// </summary>
public sealed class HudView : IHudScreen
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
    public PickHit? PickPart(HudPart part) => Huds.PickPart((yz, only) => PickAtHud(yz, only), part);

    public static TouchSurface Surface(PickHit h) => Huds.Surface(h);

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

    // --- for SojournHud (IHudScreen) ----------------------------------------------------------------------------------

    /// <summary>The headset's board while there's a headset (SojournHud's show).</summary>
    public Func<HudBoard?>? Board { get; set; }

    public bool Shown => Board?.Invoke()?.Shown ?? DesktopShown;

    public void Show(bool on)
    {
        if (Board?.Invoke() is { } board) board.Show(on); else DesktopShown = on;
    }

    public async Task<T> WithHudsAsync<T>(Func<HudsPlaced, HudPicker, T> f, T fallback, CancellationToken ct)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnMain(() =>
        {
            try { done.TrySetResult(f(_r.Huds, (yz, only) => PickAtHud(yz, only))); }
            catch (Exception e) { done.TrySetException(e); }
        });
        try { return await done.Task.WaitAsync(TimeSpan.FromSeconds(2), ct); }
        catch (TimeoutException) { return fallback; }
    }

    readonly List<HudPicture> _pictures = new();
    int _pictureCount;

    public HudPicture? Picture(int? number)
    {
        lock (_pictures) return number is { } n ? _pictures.LastOrDefault(p => p.Number == n) : _pictures.LastOrDefault();
    }

    /// <summary>A picture of the HUD screen (hud null) or of one HUD, over a dark grey (HUDs are made to sit over a
    /// scene): a PNG. HudPicture.For frames it.</summary>
    public Task<(HudPicture Picture, byte[] Png)?> PictureAsync(HudObject? hud, bool transparent, CancellationToken ct)
    {
        var done = new TaskCompletionSource<(HudPicture, byte[])?>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnMain(() =>
        {
            var (region, aspect, objects) = _r.Huds;
            if (objects.Count == 0) { done.TrySetResult(null); return; }
            var (center, hh, w, h) = HudPicture.For(hud, aspect);
            var pic = new HudPicture(Interlocked.Increment(ref _pictureCount), center, hh, w, h, aspect, region);
            _shotWanted = new HudShot(center, hh, w, h, (rgba, pw, ph) =>
            {
                try { done.TrySetResult((pic, HudPicture.Png(rgba, pw, ph, transparent))); }
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

    static async Task<(HudPicture, byte[])?> WithTimeout(Task<(HudPicture, byte[])?> t, CancellationToken ct)
    {
        try { return await t.WaitAsync(TimeSpan.FromSeconds(5), ct); }
        catch (TimeoutException) { return null; }
    }
}
