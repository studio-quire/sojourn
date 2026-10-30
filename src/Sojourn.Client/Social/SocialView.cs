using System.Numerics;
using LibreMetaverse;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

namespace Sojourn.Client.Social;

/// <summary>
/// Other people's words and names on screen, kept out of Program.cs: the name tags (<see cref="NameTags"/>) and the
/// feed (<see cref="SocialFeed"/>, drawn by <see cref="FeedPainter"/>). Information only: nothing here answers anything;
/// replying, accepting an offer or answering a menu goes through the assistant.
///
/// Where the feed sits. Desktop: the bottom left of the view (where the viewer's chat toasts come up). Headset: its own
/// quad layer, low on the left: its right edge 3 degrees left of the assistant's panel (UiSize.BesideYaw), its bottom 30
/// degrees below the eyes, growing upward, 1.1 m away at the panel's angular size. It shares the panel's lazy follow
/// (keeping its place beside it while the panel shows), and follows on its own the same way when the panel's away; the
/// trigger held on it and moved puts it somewhere, as the panel (<see cref="PanelPlacement"/>).
/// While a confirmation waits it's hidden: nothing other people wrote sits on screen beside it.
///
/// The one thing it takes is the person's hand on it (the pointer in the headset, a click on the desktop; user,
/// 2026-10-26: notifications and menus need a way to be read in full): a press on a block opens it whole and keeps it
/// up, a second closes it, and the close key that shows while the pointer is on the sheet puts everything on it away.
/// Answering still goes through the assistant.
/// </summary>
public sealed class SocialView
{
    public readonly SocialFeed Feed;
    public readonly NameTags Tags;
    public readonly FloatingText Texts;
    public readonly HoverTips Tips;
    /// <summary>The social layer's settings (SocialWiring loads and saves them).</summary>
    public readonly SocialSettings Settings = new();
    readonly AgentSession _s;
    readonly VrSettings _settings;
    readonly Func<PanelLook> _look;

    /// <summary>The look the feed is drawn in (adaptive ink's choice for it), if not the name tags' own.</summary>
    public Func<PanelLook>? FeedLook { get; init; }

    public SocialView(AgentSession session, VrSettings settings, Func<PanelLook> look, VoiceClient? voice)
    {
        _s = session;
        _settings = settings;
        _look = look;
        Feed = new SocialFeed(session);
        Tags = new NameTags(session, Feed) { Options = Settings };
        Texts = new FloatingText(session);
        Tips = new HoverTips(session);
        if (voice is { Available: true })
            Tags.Speaking = id =>
            {
                foreach (var p in voice.Peers)
                    if (p.Id == id) return p.Speaking ? Math.Clamp(p.Power * 4, 0, 1) : null;
                return null;
            };
    }

    /// <summary>Labels of the client's own to stand in the world this frame (the eye's region and position), shown as
    /// floating text whatever the hover text switch says.</summary>
    public Func<ulong, Vector3, IReadOnlyList<FloatingText.Want>>? Marks { get; set; }

    public bool NamesOn => _settings.ShowNames;
    public bool ChatOn => _settings.ShowChat;
    public bool HoverTextOn => _settings.ShowHoverText;

    static UUID Pointed(PointerTarget? t) => t is null ? UUID.Zero : t.Kind == Sojourn.Render.World.PickKind.Avatar ? t.Id : t.WornBy;

    // --- the desktop --------------------------------------------------------------------------------------------------

    /// <summary>Adds tags and the feed to a desktop frame's overlay.</summary>
    public OverlayScene? Desktop(OverlayScene? scene, FrameRenderer renderer, CameraPose pose, float vfov, int ew, int eh, float uiScale,
        PointerTarget? pointer, bool confirming, float dt)
    {
        scene = WithTags(scene, renderer, pose.Region, pose.Position, pose.Forward, pointer, NameTags.Desktop(vfov, eh, uiScale), dt, self: true);
        _inHeadset = false;
        if (confirming || FeedImage(uiScale) is not { } img) { _deskSheet = null; return scene; }
        var at = new Vector2(16 * uiScale, eh - 16 * uiScale - img.Height);
        _deskSheet = _drawn is { } d && d.Width == img.Width && d.Height == img.Height ? (at, d) : null;
        return (scene ?? new OverlayScene()) with { Feed = new OverlaySheet(img, at) };
    }

    (Vector2 At, FeedPainter.Drawn Drawn)? _deskSheet;

    /// <summary>The desktop: the notices' keys on screen now, in the eye image's pixels (scripted tests press them).</summary>
    public List<(FeedPainter.FeedKey Key, SkiaSharp.SKRect Pixels)> DesktopKeys()
    {
        var list = new List<(FeedPainter.FeedKey, SkiaSharp.SKRect)>();
        if (_deskSheet is not { } s) return list;
        foreach (var (r, k) in s.Drawn.Keys ?? [])
            list.Add((k, new SkiaSharp.SKRect(s.At.X + r.Left * s.Drawn.Scale, s.At.Y + r.Top * s.Drawn.Scale, s.At.X + r.Right * s.Drawn.Scale, s.At.Y + r.Bottom * s.Drawn.Scale)));
        return list;
    }

    /// <summary>The desktop: the mouse (in the eye image's pixels; null: not over the view) on the feed? It lights what's
    /// under it.</summary>
    public bool DesktopPoint(Vector2? mouse)
    {
        if (mouse is { } m && _deskSheet is { } s)
        {
            var p = (m - s.At) / s.Drawn.Scale;
            if (p.X >= 0 && p.Y >= 0 && p.X < s.Drawn.LogicalWidth && p.Y < s.Drawn.LogicalHeight) { Point(p); return true; }
        }
        Point(null);
        return false;
    }

    OverlayScene? WithTags(OverlayScene? scene, FrameRenderer renderer, ulong region, Vector3 eye, Vector3 forward, PointerTarget? pointer,
        NameTags.Sizing size, float dt, bool self)
    {
        // name tags are ours and go with the rest of the interface (VrSettings.InterfaceHidden); objects' floating
        // text is the world's and stays
        bool names = NamesOn && !_settings.InterfaceHidden;
        // the hover tip is ours too, and goes with the interface
        var tip = !_settings.InterfaceHidden ? Tips.Frame(pointer, region, eye, _look(), Settings, names, dt, Feed.Now) : null;
        // the client's own labels in the world (the tracking beacon's, Beacons/Tracker.cs), drawn as floating text is
        var marks = Marks?.Invoke(region, eye);
        if (!names && !HoverTextOn && tip == null && marks is not { Count: > 0 }) return scene;
        var texts = HoverTextOn ? Texts.Frame(renderer, region, eye, forward, pointer is { Kind: Sojourn.Render.World.PickKind.Object } pt ? pt.RootId : UUID.Zero, self, dt) : null;
        if (marks is { Count: > 0 }) texts = [.. texts ?? [], .. marks];
        var (atlas, tags) = Tags.Frame(region, eye, forward, renderer.Heads, Pointed(pointer), _look(), size, dt, names, texts, tip);
        if (atlas == null || tags.Count == 0) return scene;
        return (scene ?? new OverlayScene()) with { Labels = atlas, Tags = tags };
    }

    // --- the headset --------------------------------------------------------------------------------------------------

    public const float Distance = 1.1f;
    const float Deg = MathF.PI / 180;
    /// <summary>The panel's angular size (UiSize: the person's size setting).</summary>
    public static float DegPerPx => UiSize.DegPerPx;
    public static float MetresPerPx => UiSize.MetresPerPx(Distance);
    /// <summary>Where the feed's layer floats: beside the panel, following, or where the person put it.</summary>
    public readonly PanelPlacement Placement = new("feed", (anchor, yaw, _, size) => Pose(anchor, yaw, size));
    (Vector3 Pos, Quaternion Rot, Vector2 Size)? _layer;   // where the feed's layer floated last frame

    /// <summary>The headset: how far along a ray (the room's space) it meets the feed's layer, and where on it in its
    /// logical pixels; null if it misses.</summary>
    public (float T, Vector2 At)? Ray(Vector3 from, Vector3 dir)
    {
        if (_layer is not { } l || _drawn is not { } d) return null;
        if (!PanelPlacement.Hit(from, dir, l.Pos, l.Rot, l.Size, out float t, out var p)) return null;
        return (t, new Vector2((p.X / l.Size.X + 0.5f) * d.LogicalWidth, (0.5f - p.Y / l.Size.Y) * d.LogicalHeight));
    }

    /// <summary>The headset: the trigger went down with the ray on the feed, t along: a press, or the start of a drag
    /// (once the ray moves 2.5 cm, the press is let go and the feed follows the ray).</summary>
    public void Grab(Vector3 from, Vector3 dir, float t)
    {
        PressDown();
        Placement.Grab(from, dir, t);
    }

    /// <summary>The trigger still held.</summary>
    public void Drag(Vector3 from, Vector3 dir)
    {
        if (Placement.Drag(from, dir)) { _pressing = false; _downKey = null; }
    }

    /// <summary>The trigger let go: a drag ends where it is (or following again, dropped on its outline), else the press
    /// is the feed's.</summary>
    public void Release()
    {
        if (!Placement.Drop()) PressUp();
    }

    /// <summary>Adds tags to a headset frame's overlay and puts the feed on its layer (its frost into the eyes, and with
    /// inEyes the feed itself, for checking without a headset). panel: where the assistant's panel rests, while it shows
    /// and follows (the feed keeps its place beside it).</summary>
    public OverlayScene? Headset(OverlayScene? scene, FrameRenderer renderer, VrView view, Vector3 head, Quaternion headRot, PanelPlacement.Leader? panel,
        Func<Vector3, Vector3> roomToRegion, Xr.XrSession hs, PointerTarget? pointer, bool confirming, bool inEyes, float dt)
    {
        scene = WithTags(scene, renderer, view.Region, view.Position, view.Forward, pointer, NameTags.Headset, dt, self: false);
        _inHeadset = true;
        var img = !confirming ? FeedImage(UiSize.Raster, UiSize.Fit) : null;
        if (img == null || _drawn is not { } drawn)
        {
            if (_layer != null) { Placement.CancelGrab(); Placement.Place(); }
            _layer = null;
            hs.SetFeed(null, default, Quaternion.Identity, 0);
            return scene;
        }
        var size = new Vector2(drawn.LogicalWidth, drawn.LogicalHeight) * MetresPerPx;
        var (pos, rot) = Placement.Update(head, headRot, size, dt, frozen: _onSheet || Placement.Grabbed, leader: panel);
        _layer = (pos, rot, size);
        hs.SetFeed(img, pos, rot, size.X);
        if (Placement.Dragging) scene = (scene ?? new OverlayScene()) with { Beams = (scene?.Beams ?? []).Concat(Placement.Outline(roomToRegion)).ToArray() };
        if (img.Frost <= 0 && !inEyes) return scene;
        var corners = PanelPlacement.Corners(pos, rot, size).Select(roomToRegion).ToArray();
        return (scene ?? new OverlayScene()) with { Feed = new OverlaySheet(img, Corners: corners, Content: inEyes) };
    }

    /// <summary>The feed's pose for the head where it was placed and the panel's resting heading: left of the panel,
    /// edge to edge with a gap at any size (UiSize.BesideYaw), its bottom 30 degrees down.</summary>
    public static (Vector3, Quaternion) Pose(Vector3 anchor, float panelYaw, Vector2 size)
    {
        float halfH = MathF.Atan(size.Y / 2 / Distance);
        float pitch = MathF.Min(-30 * Deg + halfH, 2 * Deg - halfH);
        return PanelPlacement.Toward(anchor, panelYaw + UiSize.BesideYaw(size.X), pitch, Distance);
    }

    // --- the person's hand on it --------------------------------------------------------------------------------------

    readonly HashSet<long> _open = new();

    /// <summary>The blocks opened in full (their first lines' seqs), for any thread: an IM conversation open here is
    /// where a dictated draft goes (Private/Dictation.cs).</summary>
    public long[] Opened { get; private set; } = [];
    long _hover, _down;
    bool _onSheet, _closeLit, _pressing, _downReal;
    double _keptAt;
    FeedPainter.FeedKey? _hoverKey, _downKey;
    long _frozenSeq = -1;   // while the pointer's in use on the sheet, nothing newer than this joins it (keys stay put under it)
    bool _inHeadset;

    /// <summary>A notecard given, to read: the notification (its payload names the item), from its notice's read key.</summary>
    public Action<System.Text.Json.Nodes.JsonObject>? ReadNotecard { get; set; }

    /// <summary>A SLurl on the feed pressed (its place has just been offered).</summary>
    public event Action<Places.Place>? LinkPressed;

    /// <summary>Where the pointer is on the sheet this frame (logical pixels; null: not on it).</summary>
    public void Point(Vector2? at)
    {
        long hover = 0;
        FeedPainter.FeedKey? key = null;
        bool on = at != null;
        if (at is { } p && _drawn is { } d)
        {
            key = d.KeyAt(p.X, p.Y);
            hover = key != null ? 0 : d.At(p.X, p.Y);
        }
        bool closeLit = hover == -1;
        if (hover == -1) hover = 0;
        // in use: the pointer arrived or moved on the sheet in the last few seconds. A ray just resting across it (a
        // hand at rest, the feed low on the left) isn't reading it: then things run out and new lines join as usual
        // (the session of 2026-10-27: a notice on the left seemed to stay open)
        double now = Feed.Now;
        if (at is { } m && (!_onSheet || Vector2.Distance(m, _lastAt) > 6)) { _lastAt = m; _movedAt = now; }
        bool inUse = on && now - _movedAt < 12;
        // (for a moment after a press, what it brings joins: a script's next page, More >'s answer)
        bool hold = inUse && now >= _joinUntil;
        if (hold && _frozenSeq < 0) _frozenSeq = Feed.Snapshot().Entries.Select(e => e.Seq).DefaultIfEmpty(0).Max();
        if (!hold && _frozenSeq >= 0) { _frozenSeq = -1; Interlocked.Increment(ref _use); }
        if (hover != _hover || on != _onSheet || closeLit != _closeLit || key != _hoverKey)
        {
            (_hover, _onSheet, _closeLit, _hoverKey) = (hover, on, closeLit, key);
            Interlocked.Increment(ref _use);
        }
        // nothing runs out under the pointer while it's in use
        if (inUse && now - _keptAt > 1)
        {
            _keptAt = now;
            Feed.Keep(Feed.Snapshot().Entries.Select(e => e.Seq), 15);
        }
        // scrolled back and left a few seconds: it returns to the newest (a slip off it while scrolling doesn't)
        if (on) _offSince = now;
        else if (_scroll != 0 && now - _offSince > 3) { _scroll = 0; Interlocked.Increment(ref _use); }
    }

    Vector2 _lastAt;
    double _movedAt = -1e9, _joinUntil, _offSince;
    float _scroll;

    /// <summary>The pointer is on the sheet this frame.</summary>
    public bool OnSheet => _onSheet;

    /// <summary>It holds more than it shows (or is scrolled back): the right stick and the wheel scroll it.</summary>
    public bool Scrollable => _drawn is { } d && (d.MaxScroll > 0 || _scroll > 0);

    /// <summary>Scrolls by a fraction of its height (positive: down, toward the newest; Control/SheetStick). Scrolling
    /// is using it: nothing newer joins or runs out meanwhile.</summary>
    public void ScrollViews(float views)
    {
        if (_drawn is not { } d) return;
        float before = _scroll;
        _scroll = Math.Clamp(_scroll - views * d.LogicalHeight, 0, d.MaxScroll);
        if (_scroll == before) return;
        _movedAt = Feed.Now;
        Interlocked.Increment(ref _use);
    }

    /// <summary>The desktop: the wheel over it (a notch: about three lines).</summary>
    public void Wheel(float notches)
    {
        if (_drawn is { } d && d.LogicalHeight > 0) ScrollViews(-notches * 63 / d.LogicalHeight);
    }

    public void PressDown()
    {
        _pressing = _onSheet;
        _down = _closeLit ? -1 : _hover;
        _downKey = _onSheet ? _hoverKey : null;
        // only a hand on the controller or the mouse answers a notice: not clicks a program posts or injects (the
        // assistant has a shell, and its own answers go through the confirmation gate)
        _downReal = _inHeadset || RealInput.LeftButton();
        // a key that has only just come up under the pointer takes no press (it may have arrived under a pull meant
        // for what was there before)
        if (_downKey is { Notice: var id } dk && dk.Kind is FeedPainter.FeedKeyKind.Answer or FeedPainter.FeedKeyKind.Read
            && Feed.Snapshot().Entries.FirstOrDefault(e => e.NoticeId == id) is { } entry && Feed.Now - entry.At < ArmSeconds)
            _downKey = null;
    }

    const double ArmSeconds = 0.6;

    /// <summary>A press that went down and came up on the same thing: a notice's key answers it (or reads the notecard,
    /// or puts it away), the clear key puts every waiting notice away, the close key puts the sheet away; a block opens
    /// in full (and stays up two minutes), or closes again.</summary>
    public void PressUp()
    {
        if (!_pressing) return;
        _pressing = false;
        if (_downKey is { } dk)
        {
            _downKey = null;
            if (dk == _hoverKey && _downReal) { Use(dk); _joinUntil = Feed.Now + 4; }
            Interlocked.Increment(ref _use);
            return;
        }
        long now = _closeLit ? -1 : _hover;
        if (now != _down || now == 0) return;
        if (now == -1)
        {
            Feed.Dismiss();
            _open.Clear();
        }
        else if (!_open.Remove(now))
        {
            _open.Add(now);
            Feed.Keep(FeedPainter.Members(Feed.Snapshot().Entries, now), 120);
        }
        Opened = _open.ToArray();
        Interlocked.Increment(ref _use);
    }

    /// <summary>The desktop's Esc: the blocks opened in full close again. False if none still on the feed was open.</summary>
    public bool CloseOpened()
    {
        if (_open.Count == 0) return false;
        bool shown = Feed.Snapshot().Entries.Any(e => _open.Contains(e.Seq));
        _open.Clear();
        Opened = [];
        Interlocked.Increment(ref _use);
        return shown;
    }

    void Use(FeedPainter.FeedKey k)
    {
        switch (k.Kind)
        {
            case FeedPainter.FeedKeyKind.ClearAll:
                Feed.ClearNotices();
                break;
            case FeedPainter.FeedKeyKind.Dismiss:
                Feed.DismissNotice(k.Notice);
                break;
            case FeedPainter.FeedKeyKind.Answer when k.Button is { } button:
                // the person's own answer, as their press on the viewer's toast: no confirmation (that guards the
                // assistant's), and the notice leaves the feed once it's answered
                _ = Answer(k.Notice, button);
                break;
            case FeedPainter.FeedKeyKind.Link when Places.Parse(k.Button).FirstOrDefault() is { } place:
            {
                // a SLurl pressed, as the viewer's click on one: the place offered with its Teleport key (the press
                // that teleports is the person's own, on the offer)
                var e = Feed.Snapshot().Entries.FirstOrDefault(x => x.Seq == k.Entry);
                _s.Places.Offer(place, e?.Kind switch { FeedKind.Nearby => "chat", FeedKind.Notice => "notice", null => "feed", _ => "im" },
                    e?.Name ?? "", e?.From ?? UUID.Zero);
                LinkPressed?.Invoke(place);
                break;
            }
            case FeedPainter.FeedKeyKind.Read:
                if (_s.Notify.Get(k.Notice) is { } n)
                {
                    var copy = (System.Text.Json.Nodes.JsonObject)n.DeepClone();
                    // kept first, so it's in the inventory to read
                    var keep = (n["form"] as System.Text.Json.Nodes.JsonArray ?? new()).OfType<System.Text.Json.Nodes.JsonObject>()
                        .Select(b => (string?)b["name"]).FirstOrDefault(b => b is "Keep" or "Show");
                    if (keep != null) _ = Answer(k.Notice, keep);
                    ReadNotecard?.Invoke(copy);
                }
                break;
        }
    }

    async Task Answer(Guid id, string button)
    {
        var err = await _s.Notify.RespondAsync(id, new System.Text.Json.Nodes.JsonObject { [button] = true });
        Console.WriteLine(err == null ? $"notice answered by the person: {button}" : $"notice not answered ({err})");
    }

    long _use, _drawnUse = -1;

    // --- the bell: waiting notices back on the feed ---------------------------------------------------------------------

    /// <summary>How many notices wait for an answer, and how many of them aren't on the feed now.</summary>
    public (int Waiting, int Folded) Waiting => (Feed.WaitingCount, Feed.Folded);

    /// <summary>The bell (the forearm's token): every waiting notice back on the feed, for two minutes.</summary>
    public void Recall()
    {
        Feed.Recall();
        Interlocked.Increment(ref _use);
    }

    // --- the feed's picture --------------------------------------------------------------------------------------------

    volatile FeedPainter.Drawn? _drawn;
    volatile OverlayImage? _image;
    bool _drawing;
    string _drawnKey = "";
    double _checkAt;
    float _scale, _fit = 1;
    long _imageVersion;

    /// <summary>A waiting notice's keys, from its notification: script menus' buttons (not the text box's, which the
    /// assistant types for them), offers', invitations' and questions'. None for an alert with only OK (its × puts it
    /// away), nor the viewer's Info (a profile this client doesn't have).</summary>
    FeedPainter.NoticeForm? FormOf(FeedEntry e, double now)
    {
        if (e.NoticeId == Guid.Empty || !_s.Notify.Answerable(e.NoticeId) || _s.Notify.Get(e.NoticeId) is not { } n) return null;
        var name = (string?)n["name"] ?? "";
        var buttons = new List<(string, string)>();
        foreach (var b in (n["form"] as System.Text.Json.Nodes.JsonArray ?? new()).OfType<System.Text.Json.Nodes.JsonObject>())
        {
            string bn = (string?)b["name"] ?? "", text = (string?)b["text"] ?? bn;
            int index = b["index"] is System.Text.Json.Nodes.JsonValue iv && iv.TryGetValue<int>(out var i) ? i : 0;
            if (index < 0 || bn.Length == 0 || bn == "!!llTextBox!!" || (name == "JoinGroup" && bn == "Info")) continue;
            buttons.Add((bn, text.Length > 0 ? text : bn));
        }
        var pay = n["payload"] as System.Text.Json.Nodes.JsonObject;
        bool notecard = name is "ObjectGiveItem" or "OwnObjectGiveItem" or "UserGiveItem"
                        && pay?["type"] is System.Text.Json.Nodes.JsonValue tv && tv.TryGetValue<int>(out var t) && t == (int)AssetType.Notecard;
        return new FeedPainter.NoticeForm(buttons, name is "ScriptDialog" or "ScriptDialogGroup", notecard, now - e.At >= ArmSeconds);
    }

    /// <summary>The feed as an overlay picture, redrawn off the main thread when what it shows changes (an entry arrives
    /// or runs out, someone starts or stops typing an IM, a notice is answered or arms, the look or scale changes); null
    /// while there's nothing. With chat off, only waiting notices show. While the pointer's on it, nothing newer joins.</summary>
    OverlayImage? FeedImage(float scale, float fit = 1)
    {
        double now = Feed.Now;
        long version = Feed.Version + Feed.NoticesVersion;
        long use = Interlocked.Read(ref _use);
        if (!_drawing && (now >= _checkAt || scale != _scale || fit != _fit || version != _lastVersion || use != _drawnUse))
        {
            _checkAt = now + 0.25;
            _lastVersion = version;
            _drawnUse = use;
            _scale = scale;
            _fit = fit;
            _drawing = true;
            var look = (FeedLook ?? _look)();
            var opened = _open.ToHashSet();
            var how = new FeedPainter.Use(opened, _hover, _onSheet, _closeLit, _hoverKey, _scroll);
            long frozen = _frozenSeq;
            bool chat = ChatOn;
            Task.Run(() =>
            {
                try
                {
                    var (entries, typing) = Feed.Snapshot();
                    if (frozen >= 0) entries = entries.Where(e => e.Seq <= frozen).ToList();
                    if (!chat) { entries = entries.Where(e => e.NoticeId != Guid.Empty).ToList(); typing.Clear(); }
                    var forms = new Dictionary<Guid, FeedPainter.NoticeForm>();
                    foreach (var e in entries)
                        if (FormOf(e, now) is { } f) forms[e.NoticeId] = f;
                    var names = typing.Select(id => (id, _s.Names.DisplayName(id) ?? "")).ToList();
                    string key = $"{scale}|{fit}|{look.GetHashCode()}|{string.Join(",", entries.Select(e => e.Seq))}|{string.Join(",", typing)}|{how.Key}" +
                                 $"|{string.Join(",", forms.Select(f => $"{f.Key}:{f.Value.Armed}:{f.Value.Buttons.Count}"))}";
                    if (key == _drawnKey) return;
                    _drawnKey = key;
                    var d = FeedPainter.Draw(look, entries, names, scale, how, forms, fit);
                    _drawn = d;
                    _image = d == null ? null : new OverlayImage(d.Rgba, d.Width, d.Height, Interlocked.Increment(ref _imageVersion), d.Frost, d.Edge);
                }
                catch (Exception e)
                {
                    // never left showing an old picture: one entry that can't be drawn takes the sheet down, and it's
                    // tried again when what it holds changes
                    Console.Error.WriteLine($"chat feed: couldn't draw it ({e.GetType().Name}: {e.Message})");
                    _drawn = null;
                    _image = null;
                }
                finally { _drawing = false; }
            });
        }
        return _image;
    }

    long _lastVersion = -1;
}
