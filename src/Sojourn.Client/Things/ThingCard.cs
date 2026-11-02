using LibreMetaverse;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Sojourn.Session.Objects;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Things;

/// <summary>
/// The card of what can be done with a thing (the user's ask, 2026-10-31: the viewer's right click, which in Sojourn
/// otherwise went only through the assistant). It comes up from a still hold of the pointer's trigger on a thing in the
/// world (Control/DirectTouch.LongPress, a ring filling as on a page) or a right click on the desktop, and lists only
/// what applies to that thing (Session/Objects/ThingActions: sit, stand, touch, buy with its price, pay, take a copy,
/// take, open, about this, ask the assistant), each through the same ops and confirmations as ever.
///
/// Where: over the thing, not where the other menus sit (the user): in the headset its own quad layer on the line from
/// the eyes to the point pressed, a little in front of it (no nearer than 0.45 m, no further than 1.4 m, at the panel's
/// angular size), hanging from just above the point, turned to face you; then it stays put in the world, as a menu does
/// (the user, 2026-10-31). Desktop: at the click, flat over the view.
///
/// When it goes (the user: open it, forget about it, do something else, and it closes): its ×; a press anywhere off it;
/// a view cut (a teleport); moving 2 m from where it opened unless you've come at least a metre closer to the thing; the
/// thing gone from view; the pointer turned off. Turning the head alone leaves it. While a confirmation waits, or the
/// interface is hidden, it's out of sight and takes no ray.
///
/// While it's up the thing is "that" for the assistant whenever the pointer is on nothing else (Pointer.Pinned), so
/// talking with the grip means it too; "ask the assistant" is the talk key on the card: held, push-to-talk; tapped,
/// the mic stays open and the key reads "send" (the grip's tap works as well); the card goes once the request is sent.
/// Main thread. Claude Opus 5.5, 2026-10-31.
/// </summary>
public sealed class ThingCard
{
    readonly AgentSession _s;
    readonly ThingActions _actions;
    readonly Sojourn.Session.Assistant.AssistantHost _assistant;
    readonly Func<Sojourn.Session.Assistant.PanelLook> _look;
    readonly Action<string> _log;

    public ThingCard(AgentSession s, Sojourn.Session.Assistant.AssistantHost assistant, Func<Sojourn.Session.Assistant.PanelLook> look, Action<string> log)
    {
        _s = s;
        _actions = new ThingActions(s);
        _assistant = assistant;
        _look = look;
        _log = log;
    }

    // --- what it's about ---------------------------------------------------------------------------------------------

    ThingFacts? _facts;
    PickHit _hit;
    Global _point;                  // where it was pressed
    Global _openedAt;               // where the avatar was then
    double _distAtOpen;
    bool _tapTouches;
    List<ThingAction> _list = new();
    CardPage _page;
    bool _about;
    List<int>? _amounts;
    List<ThingActions.Content>? _contents;
    string? _note;
    bool _working, _asking;
    long _closeAt;                  // a done action's note shown, then it goes (0: not)
    long _version;
    CancellationTokenSource _cts = new();
    (UUID Id, Task<ThingFacts?> Facts)? _early;   // asked for while the ring filled

    public bool Open => _facts != null;

    readonly record struct Global(double X, double Y, double Z)
    {
        public static Global Of(ulong region, Vector3 p)
        {
            Utils.LongToUInts(region, out uint gx, out uint gy);
            return new Global(gx + (double)p.X, gy + (double)p.Y, p.Z);
        }
        public Vector3 In(ulong region)
        {
            Utils.LongToUInts(region, out uint gx, out uint gy);
            return new Vector3((float)(X - gx), (float)(Y - gy), (float)Z);
        }
        public double To(Global o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y) + (Z - o.Z) * (Z - o.Z));
    }

    Global? Me()
    {
        if (_s.Client is not { Network.CurrentSim: { } sim } c) return null;
        var p = c.Self.SimPosition;
        return Global.Of(sim.Handle, new Vector3(p.X, p.Y, p.Z));
    }

    /// <summary>A still hold has begun on a thing: its facts start coming, so the card opens with them.</summary>
    public void Prefetch(PickHit hit)
    {
        var id = hit.RootId != default ? hit.RootId : hit.Id;
        if (_early?.Id == id) return;
        if (_actions.Facts(hit.Region, hit.Id) is not { } f) { _early = null; return; }
        _early = (id, _actions.FetchAsync(f).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null));
    }

    /// <summary>Bring it up for the thing pressed. castRegion: the region the ray was cast in (the hit's point is in its
    /// frame). tapTouches: a plain press on it already touches it.</summary>
    public void Show(PickHit hit, ulong castRegion, bool tapTouches)
    {
        if (_actions.Facts(hit.Region, hit.Id) is not { } f) return;
        Close();
        _cts = new CancellationTokenSource();
        (_facts, _hit, _tapTouches) = (f, hit, tapTouches);
        _point = Global.Of(castRegion, hit.Point);
        var me = Me();
        (_openedAt, _meBefore) = (me ?? _point, me);
        _distAtOpen = me is { } m ? m.To(_point) : 0;
        (_page, _about, _amounts, _contents, _note, _working, _asking, _closeAt) = (CardPage.Main, false, null, null, null, false, false, 0);
        _list = _actions.For(f, tapTouches);
        _placed = null;
        _armed = false;   // the trigger that opened it is still held
        Pin();
        Changed();
        var ct = _cts.Token;
        var early = _early is { } e && e.Id == f.Root ? e.Facts : null;
        _early = null;
        _ = Task.Run(async () =>
        {
            var got = (early != null ? await early : null) ?? await _actions.FetchAsync(f, ct);
            if (got != null) OnMain(() => { if (!ct.IsCancellationRequested) Facts(got); });
        });
        _log($"thing card: {f.Name} ({string.Join(", ", _list.Select(a => a.Label))})");
    }

    void Facts(ThingFacts f)
    {
        if (_facts == null) return;
        _facts = f;
        _list = _actions.For(f, _tapTouches);
        Pin();
        Changed();
    }

    /// <summary>Put it away (and with it, the thing as "that", unless a request about it is still being spoken).</summary>
    public void Close()
    {
        if (_facts == null) return;
        _cts.Cancel();
        _facts = null;
        _down = null;
        _deskDown = null;
        if (!(_asking && _assistant.Talking)) Unpin();
        _asking = _asking && _assistant.Talking;
        Changed();
    }

    void Pin()
    {
        if (_facts is not { } f || _s.Client is not { } c) return;
        string regionName = c.Network.Simulators.FirstOrDefault(s => s.Handle == f.Region)?.Name ?? "";
        var me = Me();
        _pinned = new Sojourn.Session.Assistant.PointerTarget(PickKind.Object, f.Region, regionName, f.Id, f.Root, f.WornBy,
            _point.In(f.Region), me is { } m ? (float)m.To(_point) : 0, f.Name);
        _assistant.Pointer.Pinned = _pinned;
    }

    Sojourn.Session.Assistant.PointerTarget? _pinned;

    void Unpin()
    {
        if (_pinned != null && _assistant.Pointer.Pinned == _pinned) _assistant.Pointer.Pinned = null;
        _pinned = null;
    }

    // --- every frame -----------------------------------------------------------------------------------------------

    readonly System.Collections.Concurrent.ConcurrentQueue<Action> _main = new();
    void OnMain(Action a) => _main.Enqueue(a);
    long _checkedAt;
    Global? _meBefore;

    /// <summary>Each frame, headset or desktop: what came back from the world, and whether it's time to go. cut: the
    /// view moved to a new spot at once (a teleport).</summary>
    void Tick(bool cut)
    {
        while (_main.TryDequeue(out var a)) a();
        // a request begun from its ask key: once it's sent, the card's done
        if (_asking && !_assistant.Talking)
        {
            _asking = false;
            if (_facts == null) Unpin(); else Close();
            return;
        }
        if (_facts is not { } f) { _meBefore = null; return; }
        long now = Environment.TickCount64;
        if (_closeAt != 0 && now >= _closeAt) { Close(); return; }
        var me = Me();
        if (cut || me is null) { Why(cut ? "a view cut" : "no position"); return; }
        // a jump between frames is a teleport the view didn't call a cut
        if (_meBefore is { } mb && mb.To(me.Value) > 4) { Why("a jump"); return; }
        _meBefore = me;
        double moved = _openedAt.To(me.Value), dist = me.Value.To(_point);
        if (moved > 2 && dist > _distAtOpen - 1) { Why("moved away"); return; }
        if (now - _checkedAt > 500)
        {
            _checkedAt = now;
            if (_actions.Facts(f.Region, f.Id) is null) { Why("the thing's gone"); return; }
        }
    }

    void Why(string why)
    {
        _log($"thing card closed: {why}");
        Close();
    }

    // --- drawing (off the main thread) -----------------------------------------------------------------------------

    sealed record Drawn(OverlayImage Image, List<CardKey> Keys, Vector2 Logical, long Version);
    volatile Drawn? _latest;
    bool _drawing;
    (long Version, int Hover, float Scale, bool Layer, int Phase, object Look)? _drawnKey;
    int _hover = -1;
    long _imageVersion;

    void Changed() => _version++;

    CardView View()
    {
        var f = _facts!;
        string? owner = f.Worn ? (f.OwnerName is { } wn ? Lang.F("worn by {0}", wn) : null) : f.Mine ? Lang.T("yours", "owner") : f.OwnerName;
        return new CardView(f.Name, owner, _page, _list, _about, ThingActions.About(f), _amounts, _contents, _note, _working,
            _asking && _assistant.Talking, _asking && _assistant.Latched, _hover, (int)(Environment.TickCount64 / 60 % 16));
    }

    /// <summary>The card's picture now (redrawn in the background when it changes; the last one meanwhile).</summary>
    Drawn? Picture(float scale, bool layer)
    {
        if (_facts == null) { _latest = null; _drawnKey = null; return null; }
        int phase = _working || _asking ? (int)(Environment.TickCount64 / 60 % 16) : 0;
        var look = _look();
        var key = (_version, _hover, scale, layer, phase, (object)look);
        if (!_drawing && key != _drawnKey)
        {
            _drawing = true;
            _drawnKey = key;
            var view = View();
            long version = _version;
            Task.Run(() =>
            {
                try
                {
                    var (px, w, h, used, keys, _) = ThingCardPainter.Draw(view, look, scale, layer);
                    var img = new OverlayImage(px, w, h, Interlocked.Increment(ref _imageVersion), look.Frost, look.TintOpacity);
                    _latest = new Drawn(img, keys, new Vector2(w / used, h / used), version);
                }
                catch (Exception e) { Console.Error.WriteLine($"thing card: couldn't draw it ({e.GetType().Name}: {e.Message})"); }
                finally { _drawing = false; }
            });
        }
        return _latest;
    }

    // --- pressing it ---------------------------------------------------------------------------------------------------

    CardKey? _down;   // the key a press went down on (a release acts only on that same one)

    void PressDown(CardKey? key)
    {
        _down = key;
        // the ask key talks while held, as the grip does
        if (key is { Kind: CardKeyKind.Action } k && k.Index < _list.Count && _list[k.Index].Do == ThingDo.Ask)
        {
            bool was = _assistant.Talking;
            _asking = true;
            Pin();
            _assistant.TalkPress();
            if (!was) _log($"thing card: asking the assistant about {_facts?.Name}");
        }
    }

    void PressUp(CardKey? key)
    {
        var down = _down;
        _down = null;
        if (down is { Kind: CardKeyKind.Action } dk && dk.Index < _list.Count && _list[dk.Index].Do == ThingDo.Ask)
        {
            _assistant.TalkRelease();
            Changed();
            return;
        }
        if (down is not { } d || key is not { } k || d.Kind != k.Kind || d.Index != k.Index || _facts is not { } f) return;
        switch (k.Kind)
        {
            case CardKeyKind.Close: Close(); return;
            case CardKeyKind.Back: (_page, _note) = (CardPage.Main, null); Changed(); return;
            case CardKeyKind.Amount when _amounts is { } am && k.Index < am.Count:
                Run(ThingDo.Pay, f, amount: am[k.Index]);
                return;
            case CardKeyKind.CopyAll:
                Run(ThingDo.Open, f);
                return;
            case CardKeyKind.Action when k.Index < _list.Count:
                Act(_list[k.Index].Do, f);
                return;
        }
    }

    void Act(ThingDo what, ThingFacts f)
    {
        switch (what)
        {
            case ThingDo.About:
                _about = !_about;
                Changed();
                return;
            case ThingDo.Pay:
            {
                (_page, _amounts, _note) = (CardPage.Pay, null, null);
                Changed();
                var ct = _cts.Token;
                _ = Task.Run(async () =>
                {
                    List<int> amounts;
                    try
                    {
                        var (buttons, suggested, any) = await _actions.PayPricesAsync(f, ct);
                        amounts = buttons.ToList();
                        // no buttons of its own: the viewer's pay window's (1, 5, 10, 20), its suggested amount first
                        if (amounts.Count == 0 && any) amounts = [1, 5, 10, 20];
                        if (suggested is { } sg && any && !amounts.Contains(sg)) amounts.Insert(0, sg);
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { amounts = []; OnMain(() => { _note = Lang.F("Couldn't ask what it takes: {0}", e.Message); Changed(); }); }
                    OnMain(() => { if (!ct.IsCancellationRequested) { _amounts = amounts; Changed(); } });
                }, ct);
                return;
            }
            case ThingDo.Open:
            {
                (_page, _contents, _note) = (CardPage.Open, null, null);
                Changed();
                var ct = _cts.Token;
                _ = Task.Run(async () =>
                {
                    List<ThingActions.Content> items;
                    try { items = await _actions.ContentsAsync(f, ct); }
                    catch (Exception e) when (e is not OperationCanceledException) { items = []; OnMain(() => { _note = e.Message; Changed(); }); }
                    OnMain(() => { if (!ct.IsCancellationRequested) { _contents = items; Changed(); } });
                }, ct);
                return;
            }
            case ThingDo.Ask:
                return;   // (on the press itself)
            default:
                Run(what, f);
                return;
        }
    }

    /// <summary>Run an action: the card shows it's working (out of sight while its confirmation waits), then goes, or
    /// says what happened: a note that it worked stays a moment, one that it didn't until the card goes.</summary>
    void Run(ThingDo what, ThingFacts f, int amount = 0)
    {
        if (_working) return;
        (_working, _note) = (true, null);
        Changed();
        var at = _point.In(f.Region);
        var copy = _contents;
        var ct = _cts.Token;
        _log($"thing card: {what} on {f.Name}{(amount > 0 ? $", L${amount}" : "")}");
        _ = Task.Run(async () =>
        {
            (bool Ok, string? Say) r;
            try { r = await _actions.RunAsync(what, f, at, amount, copy, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { r = (false, Lang.F("Didn't work: {0}", e.Message)); }
            OnMain(() =>
            {
                if (_facts == null || ct.IsCancellationRequested) return;
                _working = false;
                if (r.Say is null) { Close(); return; }
                _note = r.Say;
                if (r.Ok) _closeAt = Environment.TickCount64 + 2500;
                if (_page == CardPage.Pay && r.Ok) _page = CardPage.Main;
                Changed();
            });
        });
    }

    // --- the headset -----------------------------------------------------------------------------------------------

    const float Near = 0.45f, Far = 1.4f;
    bool _armed = true, _onCard;
    Vector3 _pos;
    Quaternion _rot = Quaternion.Identity;
    /// <summary>Where it was put on opening, in the region's frame: its centre and the eyes it faced then. It stays there
    /// as the person moves, as a menu does (the user, 2026-10-31: following the thing wasn't needed, since moving off
    /// closes it anyway).</summary>
    (ulong Region, Vector3 Pos, Vector3 Eyes)? _placed;
    Vector2 _size;
    (Vector3 From, Vector3 To)? _beam;

    /// <summary>The right ray is on the card this frame (the panel, the boards and the world behind wait).</summary>
    public bool RightOn { get; private set; }

    /// <summary>Each headset frame, with the boards' hands (the pointer's right, while it's on): places it, reads the ray
    /// and trigger. pointer: the pointer is on. hidden: a confirmation waits or the interface is hidden. cut: the view
    /// moved to a new spot this frame.</summary>
    public void Headset(XrControllers boards, VrPilot pilot, Vector3 head, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel,
        bool pointer, bool hidden, bool focused, bool cut)
    {
        RightOn = false;
        _beam = null;
        _shown = null;
        Tick(cut);
        if (_facts is not { } f) return;
        // the pointer going off leaves it up (the user: letting go of A closed it); its keys wait for the ray again
        if (hidden || pilot.Region == 0) { _armed = false; _hover = -1; return; }
        if (Picture(UiSize.Raster, Sojourn.Session.Assistant.GlassLight.Layers) is not { } d) return;

        // placed once, on the line from the eyes to where it was pressed, a little in front of it, hanging from just above
        // the point; then it stays there in the world
        if (_placed is not { } pl0 || pl0.Region != pilot.Region || _size == default)
        {
            var anchor = pilot.RegionToRoom(_point.In(pilot.Region));
            var to = anchor - head;
            float dist = to.Length();
            float at = Math.Clamp(dist - 0.15f, Near, Far);
            var mpp = UiSize.MetresPerPx(at);
            _size = d.Logical * mpp;
            var p = head + (dist > 1e-3f ? to / dist : -Vector3.UnitZ) * at;
            var rot = PanelPlacement.Facing(head, p);
            var up = Vector3.Transform(Vector3.UnitY, rot);
            var pos = p + up * (at * MathF.Tan(2 * MathF.PI / 180) - _size.Y / 2);
            _placed = (pilot.Region, pilot.RoomToRegion(pos), pilot.RoomToRegion(head));
        }
        else if (d.Logical.X > 0 && MathF.Abs(_size.Y - _size.X * d.Logical.Y / d.Logical.X) > 1e-4f)
        {
            // grown or shrunk (about opened): its top edge stays where it is
            var grown = new Vector2(_size.X, _size.X * d.Logical.Y / d.Logical.X);
            var upNow = Vector3.Transform(Vector3.UnitY, _rot);
            var moved = pilot.RoomToRegion(pilot.RegionToRoom(pl0.Pos) - upNow * ((grown.Y - _size.Y) / 2));
            _placed = pl0 with { Pos = moved };
            _size = grown;
        }
        var placed = _placed!.Value;
        _pos = pilot.RegionToRoom(placed.Pos);
        _rot = PanelPlacement.Facing(pilot.RegionToRoom(placed.Eyes), _pos);
        _shown = (d.Image, _pos, _rot, _size.X);

        CardKey? key = null;
        _onCard = false;
        if (focused && boards.RightAim is { } q && boards.RightAimPos is { } from)
        {
            var dir = Vector3.Transform(-Vector3.UnitZ, q);
            float panelT = panel is { } pl && PanelPlacement.Hit(from, dir, pl.Pos, pl.Rot, pl.Size, out var pt, out _) ? pt : float.MaxValue;
            if (PanelPlacement.Hit(from, dir, _pos, _rot, _size, out float t, out var local) && t < panelT)
            {
                _onCard = true;
                var px = new Vector2((local.X / _size.X + 0.5f) * d.Logical.X, (0.5f - local.Y / _size.Y) * d.Logical.Y);
                key = KeyAt(d, px);
                _beam = (from, from + dir * t);
            }
        }
        RightOn = _onCard || _down != null;
        int hover = key is { } hk ? d.Keys.IndexOf(hk) : -1;
        if (hover != _hover) { _hover = hover; Changed(); }
        if (!focused) { _armed = false; return; }
        float trigger = boards.RightTrigger;
        if (trigger < 0.35f)
        {
            if (_down != null) PressUp(key);
            _armed = true;
        }
        else if (trigger > 0.6f && _armed)
        {
            _armed = false;
            // a press anywhere off it is the person doing something else: it goes (and the press does that thing)
            if (_onCard) PressDown(key); else Why("a press elsewhere");
        }
    }

    static CardKey? KeyAt(Drawn d, Vector2 px)
    {
        foreach (var k in d.Keys) if (k.Rect.Contains(px.X, px.Y)) return k;
        return null;
    }

    (OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width)? _shown;

    /// <summary>Its layer this frame (Map/MapWiring.HeadsetScene puts it up with the boards', frost and all).</summary>
    public (string Key, OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width)? Layer() =>
        _shown is { } s ? ("thing card", s.Image, s.Pos, s.Rot, s.Width) : null;

    /// <summary>The ray's cue while it's on the card: a beam to where it lands, and a small dot there.</summary>
    public OverlayScene? Decorate(OverlayScene? scene, Func<Vector3, Vector3> roomToRegion)
    {
        if (_beam is not { } b || _shown == null) return scene;
        var a = roomToRegion(b.From);
        var c = roomToRegion(b.To);
        if (Vector3.DistanceSquared(a, c) < 1e-6f) return scene;
        var cue = Sojourn.Session.Assistant.PanelLook.InUse.Light(0.75f);
        var beams = new List<OverlayBeam>(scene?.Beams ?? []) { new(a + Vector3.Normalize(c - a) * 0.04f, c, 0.004f, cue) };
        var dots = new List<OverlayDot>(scene?.Dots ?? []) { new(c, Math.Clamp(0.006f * Vector3.Distance(a, c), 0.006f, 0.02f), cue with { W = 0.9f }) };
        return (scene ?? new OverlayScene()) with { Beams = beams, Dots = dots };
    }

    // --- the desktop ---------------------------------------------------------------------------------------------------

    Vector2 _deskAt;
    SkiaSharp.SKRect _deskRect;
    CardKey? _deskDown;
    float _deskScale = 1;

    /// <summary>A right click in the world: the card at the click (px: the eye image's pixels).</summary>
    public void DesktopShow(PickHit hit, ulong castRegion, bool tapTouches, Vector2 px)
    {
        Show(hit, castRegion, tapTouches);
        _armed = true;
        _deskAt = px;
    }

    /// <summary>Each desktop frame: its picture at the click (kept inside the view), or null.</summary>
    public (OverlayImage Image, Vector2 At)? Desktop(float ui, int width, int height, bool hidden, bool cut)
    {
        Tick(cut);
        _deskRect = SkiaSharp.SKRect.Empty;
        if (_facts == null || hidden) return null;
        _deskScale = ui;
        if (Picture(ui, false) is not { } d) return null;
        float w = d.Image.Width, h = d.Image.Height;
        float x = Math.Clamp(_deskAt.X + 12 * ui, 8, Math.Max(8, width - w - 8)), y = Math.Clamp(_deskAt.Y - 16 * ui, 8, Math.Max(8, height - h - 8));
        _deskRect = new SkiaSharp.SKRect(x, y, x + w, y + h);
        return (d.Image, new Vector2(x, y));
    }

    public bool DesktopOver(Vector2 px) => _facts != null && _deskRect.Contains(px.X, px.Y);

    CardKey? DeskKey(Vector2 px)
    {
        if (!DesktopOver(px) || _latest is not { } d) return null;
        var scale = d.Image.Width / d.Logical.X;
        return KeyAt(d, new Vector2((px.X - _deskRect.Left) / scale, (px.Y - _deskRect.Top) / scale));
    }

    public void DesktopMove(Vector2 px)
    {
        if (_facts == null || _latest is not { } d) return;
        int hover = DeskKey(px) is { } k ? d.Keys.IndexOf(k) : -1;
        if (hover != _hover) { _hover = hover; Changed(); }
    }

    /// <summary>A left click: true if it was the card's. One elsewhere puts it away (and goes on to what it was on).</summary>
    public bool DesktopDown(Vector2 px)
    {
        if (_facts == null) return false;
        if (!DesktopOver(px)) { Why("a click elsewhere"); return false; }
        _deskDown = DeskKey(px);
        PressDown(_deskDown);
        return true;
    }

    public void DesktopUp(Vector2 px)
    {
        if (_down == null) return;
        _deskDown = null;
        PressUp(DeskKey(px));
    }
}
