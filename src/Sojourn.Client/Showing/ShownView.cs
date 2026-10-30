using System.Numerics;
using LibreMetaverse;
using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Social;
using Sojourn.Render.Frame;
using Sojourn.Render.Realtime;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;
using Quaternion = System.Numerics.Quaternion;

namespace Sojourn.Client.Showing;

/// <summary>
/// Where the assistant's elements (<see cref="Shown"/>) go on screen, and their close keys under the pointer.
///
/// With the person (view, wrist, room): on the desktop, a stack at the bottom right of the view (the panel is top
/// right, the chat feed bottom left), the first shown lowest, a second column to its left when it's full. In the
/// headset each is its own quad layer (sharp text, as the panel's), its frost drawn into the eyes:
/// - view: a column low on the right, mirroring the chat feed: its left edge 3 degrees right of the panel's card where
///   the panel rests, its bottom 30 degrees below the eyes, growing upward, 1.1 m away at the panel's angular size;
///   it keeps its place beside the panel while the panel shows and follows, and follows lazily on its own otherwise.
/// - wrist: above the left wrist, turned to the eyes, at the panel's angular size, showing while you look at it.
/// - room: placed in front of where you face when it first shows (a second one beside it), 1.3 m away, and left
///   there in the room (which the stick carries, so it stays with you).
/// Out in the world (an object, an avatar, a point): drawn into the eyes like name tags, upright and turned to the
/// eye, shrinking slowly with distance; over an avatar it sits above their name tag; a point gets a thin stem down to
/// a dot where it is.
///
/// While a confirmation waits nothing shows: nothing of the assistant's sits beside the client's own words, and
/// nothing can be pressed but the confirmation's keys. Elements never cover the panel: its layer is drawn over theirs.
/// </summary>
public sealed class ShownView
{
    public readonly Shown Shown;
    readonly Func<PanelLook> _look;

    public ShownView(Shown shown, Func<PanelLook> look)
    {
        Shown = shown;
        _look = look;
    }

    /// <summary>A card on screen this frame, for the pointer: its corners in the frame's region, or its pixels.</summary>
    readonly record struct Hit(string Id, Vector3[]? Corners, SKRect Pixels, float LogicalW, float LogicalH, SKRect? Close);
    readonly List<Hit> _hits = new();
    string? _pressed;

    // --- sizes -----------------------------------------------------------------------------------------------------

    /// <summary>Metres per logical pixel for something out in the world at a distance (as name tags are sized).</summary>
    static float WorldMpp(NameTags.Sizing size, float d)
    {
        float dd = MathF.Max(d, 1.5f);
        float deg = size.DegPerPx * MathF.Pow(dd / 3, -size.Falloff);
        return dd * MathF.Tan(deg * MathF.PI / 180);
    }

    /// <summary>The world cards: upright billboards over their anchors (and a point's stem and dot).</summary>
    void World(Shown.Built built, Shown.Viewer v, NameTags.Sizing size, (ulong Region, NameTag[] Tags) heads, bool namesOn,
        List<OverlayCard> cards, List<OverlayBeam> beams, List<OverlayDot> dots)
    {
        var look = _look();
        var stacked = new Dictionary<UUID, float>();   // several on one thing stack upward, the first lowest
        var ink = SKColor.TryParse(look.Ink, out var ic) ? new Vector4(ic.Red / 255f, ic.Green / 255f, ic.Blue / 255f, 0.7f) : new Vector4(0.2f, 0.3f, 0.3f, 0.7f);
        foreach (var p in built.Items)
        {
            if (!p.Spec.At.InWorld) continue;
            var t = p.Spec.At.Where;
            Vector3 bottom;
            UUID avatar = UUID.Zero;
            float lift;
            if (t.IsThing && Shown.IsAvatar(t.Thing))
            {
                var head = heads.Region == v.Region ? heads.Tags.FirstOrDefault(h => h.Id == t.Thing) : default;
                if (head.Id == t.Thing) { bottom = head.Head; avatar = t.Thing; }
                else if (Shown.Resolve(t, v.Region) is { } pos) bottom = pos + new Vector3(0, 0, 0.9f);
                else continue;
                float mppL = WorldMpp(size, Vector3.Distance(bottom, v.Eye));
                // above the name tag (its lift, its two lines and a little air), or where a tag would start
                lift = 0.24f + (7 + (namesOn ? 41 + 8 : 0)) * mppL;
            }
            else if (t.IsThing)
            {
                if (Shown.Resolve(t, v.Region) is not { } pos) continue;
                bottom = pos + new Vector3(0, 0, Shown.TopOf(p.Id, t.Thing));
                lift = 0.1f;
            }
            else
            {
                if (Shown.Resolve(t, v.Region) is not { } pos) continue;
                if (float.IsNaN(pos.Z)) pos.Z = v.Eye.Z - 1.5f;
                bottom = pos;
                float d0 = Vector3.Distance(pos, v.Eye);
                lift = Math.Clamp(d0 * 0.06f, 0.3f, 3f);
                beams.Add(new OverlayBeam(pos, pos + new Vector3(0, 0, lift), Math.Clamp(d0 * 0.0012f, 0.003f, 0.08f), ink));
                dots.Add(new OverlayDot(pos, Math.Clamp(d0 * 0.004f, 0.012f, 0.5f), ink with { W = 0.85f }));
            }
            float d = Vector3.Distance(bottom, v.Eye);
            if (d > 128) continue;
            float mpp = WorldMpp(size, d) / p.AtlasScale;
            if (t.IsThing)
            {
                float below = stacked.GetValueOrDefault(t.Thing);
                stacked[t.Thing] = below + (p.InAtlas.Height + 6 * p.AtlasScale) * mpp;
                lift += below;
            }
            cards.Add(new OverlayCard(new Vector4(p.InAtlas.Left, p.InAtlas.Top, p.InAtlas.Width, p.InAtlas.Height))
            {
                At = avatar == UUID.Zero ? bottom : default, Avatar = avatar, Lift = lift, MetresPerPx = mpp, Frost = p.Drawn.Frost, Edge = p.Drawn.Edge,
            });
            if (p.Drawn.Close is { } close)
            {
                var b = bottom + new Vector3(0, 0, lift);
                _hits.Add(new Hit(p.Id, Billboard(b, v.Eye, p.InAtlas.Width * mpp, p.InAtlas.Height * mpp), default, p.Drawn.LogicalWidth, p.Drawn.LogicalHeight, close));
            }
        }
    }

    /// <summary>An upright quad turned to the eye, its bottom centre at a point (Overlay.Card's billboard).</summary>
    static Vector3[] Billboard(Vector3 bottom, Vector3 eye, float w, float h)
    {
        var right = Vector3.Cross(bottom - eye, Vector3.UnitZ);
        right = right.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(right);
        var up = new Vector3(0, 0, h);
        return new[] { bottom - right * w / 2 + up, bottom + right * w / 2 + up, bottom + right * w / 2, bottom - right * w / 2 };
    }

    // --- the desktop ---------------------------------------------------------------------------------------------------

    /// <summary>Adds the elements to a desktop frame's overlay.</summary>
    public OverlayScene? Desktop(OverlayScene? scene, CameraPose pose, float vfov, int ew, int eh, float ui, (ulong, NameTag[]) heads, bool namesOn, bool confirming)
    {
        _hits.Clear();
        var v = new Shown.Viewer(pose.Region, pose.Position, pose.Forward);
        var built = Shown.Frame(v, new Shown.Scales(ui, Math.Max(1, ui * 1.5f), ui));
        if (confirming || built is not { Atlas: { } atlas }) { Shown.HoverClose = null; return scene; }
        var cards = new List<OverlayCard>();
        var beams = new List<OverlayBeam>();
        var dots = new List<OverlayDot>();
        World(built, v, NameTags.Desktop(vfov, eh, ui), heads, namesOn, cards, beams, dots);

        // with the person: the bottom right, stacked upward, the first lowest; a column to the left once one is full
        float margin = 16 * ui, gap = 10 * ui, top = eh * 0.38f;
        float right = ew - margin, bottomY = eh - margin, colW = 0;
        foreach (var p in built.Items.Where(p => !p.Spec.At.InWorld))
        {
            float w = p.InAtlas.Width, h = p.InAtlas.Height;
            if (bottomY - h < top && colW > 0) { right -= colW + gap; bottomY = eh - margin; colW = 0; }
            var at = new Vector2(right - w, bottomY - h);
            cards.Add(new OverlayCard(new Vector4(p.InAtlas.Left, p.InAtlas.Top, w, h)) { Screen = at, Frost = p.Drawn.Frost, Edge = p.Drawn.Edge });
            if (p.Drawn.Close != null) _hits.Add(new Hit(p.Id, null, new SKRect(at.X, at.Y, at.X + w, at.Y + h), p.Drawn.LogicalWidth, p.Drawn.LogicalHeight, p.Drawn.Close));
            bottomY -= h + gap;
            colW = Math.Max(colW, w);
        }
        if (cards.Count == 0) return scene;
        var s = scene ?? new OverlayScene();
        return s with { ShownAtlas = atlas, Cards = cards, Beams = s.Beams.Concat(beams).ToArray(), Dots = s.Dots.Concat(dots).ToArray() };
    }

    // --- the headset ---------------------------------------------------------------------------------------------------

    const float Deg = MathF.PI / 180;
    static float LayerMpp => SocialView.MetresPerPx;
    // the view column's lazy follow (as the feed's): each card in it follows its place there, unless the person put it
    // somewhere (its own placement, by element id)
    bool _placed;
    float _yaw;
    Vector3 _anchor;
    readonly Dictionary<string, PanelPlacement> _cardPlaces = new();
    readonly Dictionary<string, int> _slots = new();       // a following view card's place in the column
    readonly List<Vector2> _slotSizes = new();             // the following view cards' logical sizes, in order
    readonly Dictionary<string, Vector2> _logical = new(); // each view card's logical size
    readonly List<(string Id, Vector3 Pos, Quaternion Rot, Vector2 Size)> _viewLayers = new();   // this frame's, for the ray

    /// <summary>The menus' placements (the reset brings the cards back to the column too).</summary>
    public MenuPlaces? Places { get; set; }
    readonly Dictionary<string, (Vector3 Pos, Quaternion Rot, float Width)> _rooms = new();

    public const float RoomDistance = 1.4f;

    /// <summary>Where a room card goes when it first shows (the session of 2026-10-27: one landed low, behind the panel):
    /// 1.4 m out, past the panel's 1.1 m; at about eye level, its bottom edge no lower than 10 degrees below the eyes,
    /// clear of the panel's top (18 below) and the feed and view column (from 30 below upward, at the sides); its top
    /// no higher than 14 above. Straight ahead, or beside the room cards already there (right, then left) if that's
    /// taken.</summary>
    (Vector3 Pos, Quaternion Rot, float Width) RoomPlace(Vector3 head, float headYaw, Vector2 size)
    {
        float hh = 2 * MathF.Atan(size.Y / 2 / RoomDistance), half = MathF.Atan(size.X / 2 / RoomDistance);
        float lowest = -10 * Deg + hh / 2, highest = 14 * Deg - hh / 2;
        float pitch = lowest <= highest ? Math.Clamp(-2 * Deg, lowest, highest) : (lowest + highest) / 2;
        // the headings the room cards already there take up, as seen from here
        var taken = _rooms.Values.Select(r =>
        {
            var d = r.Pos - head;
            return (Yaw: MathF.Atan2(-d.X, -d.Z), Half: MathF.Atan(r.Width / 2 / Math.Max(0.3f, new Vector2(d.X, d.Z).Length())));
        }).ToList();
        bool Free(float yaw) => taken.All(t => MathF.Abs(Wrap(yaw - t.Yaw)) > t.Half + half + 2 * Deg);
        float chosen = headYaw;
        for (int i = 1; !Free(chosen) && i < 12; i++)
            chosen = headYaw + (i % 2 == 1 ? -1 : 1) * ((i + 1) / 2) * (2 * half + 3 * Deg);   // right, left, further right...
        var dir = new Vector3(-MathF.Sin(chosen), 0, -MathF.Cos(chosen)) * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch);
        var pos = head + dir * RoomDistance;
        return (pos, Facing(head, pos), size.X);
    }
    bool _wristShown;

    /// <summary>How far above the left wrist the wrist cards start (metres): above the location line while it shows
    /// (Map/WristLine.cs).</summary>
    public static float WristFrom { get; set; } = 0.05f;

    /// <summary>The room's origin moved (the headset's own recentring): cards left in the room go back in front.</summary>
    public void Recentred() { _rooms.Clear(); _placed = false; }

    /// <summary>Adds the world elements and the cards' frost to a headset frame's overlay and puts the cards on their
    /// layers. head, headRot, wrist: the room's (OpenXR) space; panel: where the panel rests, while it shows and
    /// follows.</summary>
    public OverlayScene? Headset(OverlayScene? scene, VrView view, Vector3 head, Quaternion headRot, PanelPlacement.Leader? panel, Func<Vector3, Vector3> roomToRegion,
        Xr.XrSession hs, (Vector3 Pos, Quaternion Rot)? leftGrip, (ulong, NameTag[]) heads, bool namesOn, bool confirming, bool inEyes, float dt)
    {
        _hits.Clear();
        _viewLayers.Clear();
        var v = new Shown.Viewer(view.Region, view.Position, view.Forward);
        // the cards at the person's size (UiSize; the same raster pixels a degree at any size), world elements as before
        var built = confirming ? null : Shown.Frame(v, new Shown.Scales(UiSize.Raster, 2.4f, inEyes ? 2.4f : 1.2f, UiSize.Wide));
        var layers = new List<(string, OverlayImage, Vector3, Quaternion, float)>();
        if (built is not { Atlas: { } atlas })
        {
            hs.SetCards(layers);
            _placed = false;
            // (hidden, a card the person put somewhere keeps its place; one gone for good forgets it)
            if (!confirming) ForgetCards(new HashSet<string>());
            else foreach (var p in _cardPlaces.Values) { p.CancelGrab(); p.Place(); }
            _grabbed = null;
            if (confirming) Shown.HoverClose = null;
            return scene;
        }
        var cards = new List<OverlayCard>();
        var beams = new List<OverlayBeam>();
        var dots = new List<OverlayDot>();
        World(built, v, NameTags.Headset, heads, namesOn, cards, beams, dots);

        var look = Vector3.Transform(-Vector3.UnitZ, headRot);
        var flat = new Vector2(look.X, look.Z);
        float? headYaw = flat.Length() > 0.25f ? MathF.Atan2(-flat.X, -flat.Y) : null;

        void Layer(Shown.Placed p, Vector3 pos, Quaternion rot, float mpp)
        {
            var size = new Vector2(p.Drawn.LogicalWidth, p.Drawn.LogicalHeight) * mpp;
            layers.Add((p.Id, p.Image, pos, rot, size.X));
            var corners = PanelPlacement.Corners(pos, rot, size).Select(roomToRegion).ToArray();
            cards.Add(new OverlayCard(new Vector4(p.InAtlas.Left, p.InAtlas.Top, p.InAtlas.Width, p.InAtlas.Height))
            {
                Corners = corners, Content = inEyes, Frost = p.Drawn.Frost, Edge = p.Drawn.Edge,
            });
            if (p.Drawn.Close != null) _hits.Add(new Hit(p.Id, corners, default, p.Drawn.LogicalWidth, p.Drawn.LogicalHeight, p.Drawn.Close));
        }

        // view: the column right of the panel, each card following its place in it unless the person put it somewhere
        var inView = built.Items.Where(p => p.Spec.At.Kind == AnchorKind.View).ToList();
        ForgetCards(inView.Select(p => p.Id).ToHashSet());
        if (inView.Count > 0)
        {
            Follow(head, headYaw, panel, dt);
            _slots.Clear();
            _slotSizes.Clear();
            foreach (var p in inView)
            {
                var logical = new Vector2(p.Drawn.LogicalWidth, p.Drawn.LogicalHeight);
                _logical[p.Id] = logical;
                if (CardPlace(p.Id).Pinned) continue;
                _slots[p.Id] = _slotSizes.Count;
                _slotSizes.Add(logical);
            }
            var column = new PanelPlacement.Leader(_yaw, _anchor, Exact: true);
            foreach (var p in inView)
            {
                var place = CardPlace(p.Id);
                var size = _logical[p.Id] * LayerMpp;
                var (pos, rot) = place.Update(head, headRot, size, dt, leader: column);
                Layer(p, pos, rot, LayerMpp);
                _viewLayers.Add((p.Id, pos, rot, size));
                if (place.Dragging) beams.AddRange(place.Outline(roomToRegion));
            }
        }
        else _placed = false;

        // wrist: over the left wrist while it's looked at
        var atWrist = built.Items.Where(p => p.Spec.At.Kind == AnchorKind.Wrist).ToList();
        if (atWrist.Count > 0 && leftGrip is { } g)
        {
            var wrist = g.Pos + Vector3.Transform(new Vector3(0, 0, 0.06f), g.Rot);
            var to = wrist - head;
            float dist = to.Length(), angle = dist > 0.05f ? MathF.Acos(Math.Clamp(Vector3.Dot(to / dist, look), -1, 1)) : MathF.PI;
            _wristShown = dist < 0.85f && angle < (_wristShown ? 40 : 30) * Deg;
            if (_wristShown)
            {
                // at the panel's angular size where it is
                float mpp = dist * MathF.Tan(PanelPlacement.DegPerPx * Deg);
                float y = WristFrom;
                foreach (var p in atWrist)
                {
                    float h = p.Drawn.LogicalHeight * mpp;
                    var centre = wrist + Vector3.UnitY * (y + h / 2) - Vector3.Normalize(to) * 0.02f;
                    Layer(p, centre, Facing(head, centre), mpp);
                    y += h + 0.012f;
                }
            }
        }
        else _wristShown = false;

        // room: placed once where they face, then left there
        foreach (var gone in _rooms.Keys.Where(k => !built.Items.Any(p => p.Id == k && p.Spec.At.Kind == AnchorKind.Room)).ToList()) _rooms.Remove(gone);
        foreach (var p in built.Items.Where(p => p.Spec.At.Kind == AnchorKind.Room))
        {
            if (!_rooms.TryGetValue(p.Id, out var pose))
                _rooms[p.Id] = pose = RoomPlace(head, headYaw ?? 0, new Vector2(p.Drawn.LogicalWidth, p.Drawn.LogicalHeight) * LayerMpp);
            Layer(p, pose.Pos, pose.Rot, LayerMpp);
        }

        hs.SetCards(layers);
        if (cards.Count == 0) return scene;
        var s = scene ?? new OverlayScene();
        return s with { ShownAtlas = atlas, Cards = cards, Beams = s.Beams.Concat(beams).ToArray(), Dots = s.Dots.Concat(dots).ToArray() };
    }

    /// <summary>The view column's poses for cards of these logical sizes, the head where it was placed (anchor) and the
    /// panel's resting heading: right of the panel's card, from 30 degrees below the eyes upward, another column to the
    /// right once one would rise past 2 degrees below them.</summary>
    public static List<(Vector3 Pos, Quaternion Rot)> Column(Vector3 anchor, float panelYaw, IReadOnlyList<Vector2> logical)
    {
        var poses = new List<(Vector3, Quaternion)>();
        // edge to edge with the panel at any size, as the feed on the other side (UiSize.BesideYaw)
        float colLeft = UiSize.PanelHalfYaw + UiSize.BesideGapDeg * Deg, bottom = -30 * Deg, colW = 0;
        foreach (var size in logical)
        {
            float halfW = UiSize.HalfYaw(size.X * LayerMpp);
            float hh = 2 * MathF.Atan(size.Y * LayerMpp / 2 / SocialView.Distance);
            if (bottom + hh > -2 * Deg && colW > 0) { colLeft += colW + 2 * Deg; bottom = -30 * Deg; colW = 0; }
            float yaw = panelYaw - colLeft - halfW, pitch = bottom + hh / 2;
            var fwd = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
            var dir = fwd * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch);
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
            poses.Add((anchor + dir * SocialView.Distance, rot));
            bottom += hh + 1.2f * Deg;
            colW = Math.Max(colW, 2 * halfW);
        }
        return poses;
    }

    public static float MetresPerPx => LayerMpp;

    void Follow(Vector3 head, float? headYaw, PanelPlacement.Leader? panel, float dt)
    {
        if (!_placed)
        {
            _yaw = panel?.Yaw ?? headYaw ?? 0;
            _anchor = panel?.Anchor ?? head;
            _placed = true;
        }
        if (panel is { } pl)
        {
            // with the panel (as the feed): moving with it, a jump glided; the panel's own anchor, as the feed has it
            // (apart, they drifted into each other)
            float d = Wrap(pl.Yaw - _yaw);
            float a = MathF.Abs(d) < 10 * Deg ? 1 - MathF.Exp(-dt / 0.03f) : 1 - MathF.Exp(-dt / 0.18f);
            _yaw = Wrap(_yaw + d * a);
            _anchor += (pl.Anchor - _anchor) * a;
            return;
        }
        PanelPlacement.Leash(ref _yaw, ref _anchor, headYaw, head, 30 * Deg, dt);
    }

    /// <summary>A view card's placement: following its place in the column (or the column's next place, for the outline
    /// while it's dragged back), or where the person put it, for as long as the card is up.</summary>
    PanelPlacement CardPlace(string id)
    {
        if (_cardPlaces.TryGetValue(id, out var p)) return p;
        p = new PanelPlacement("card " + id, (anchor, yaw, _, _) =>
        {
            if (_slots.TryGetValue(id, out int i) && i < _slotSizes.Count) return Column(anchor, yaw, _slotSizes)[i];
            var sizes = new List<Vector2>(_slotSizes) { _logical.GetValueOrDefault(id) };
            return Column(anchor, yaw, sizes)[^1];
        });
        _cardPlaces[id] = p;
        Places?.Add(p, keep: false);
        return p;
    }

    /// <summary>Placements of view cards no longer up go (a card shown again comes back to the column).</summary>
    void ForgetCards(HashSet<string> up)
    {
        foreach (var id in _cardPlaces.Keys.Where(k => !up.Contains(k)).ToList())
        {
            if (_grabbed == _cardPlaces[id]) _grabbed = null;
            Places?.Remove(_cardPlaces[id]);
            _cardPlaces.Remove(id);
            _logical.Remove(id);
        }
    }

    // --- the hand on a view card: a drag puts it somewhere ---------------------------------------------------------------

    PanelPlacement? _grabbed;

    /// <summary>The headset: how far along a ray (the room's space) it meets a card shown with the person in view (in
    /// the column or put somewhere), the nearest; null if none. The pointer's ray stops there.</summary>
    public float? CardRay(Vector3 from, Vector3 dir) => Nearest(from, dir)?.T;

    (string Id, float T)? Nearest(Vector3 from, Vector3 dir)
    {
        (string Id, float T)? best = null;
        foreach (var l in _viewLayers)
            if (PanelPlacement.Hit(from, dir, l.Pos, l.Rot, l.Size, out float t, out _) && (best is not { } b || t < b.T))
                best = (l.Id, t);
        return best;
    }

    /// <summary>The trigger went down on a view card's body (not its close key): it may be dragged.</summary>
    public void Grab(Vector3 from, Vector3 dir)
    {
        if (Nearest(from, dir) is not { } n || !_cardPlaces.TryGetValue(n.Id, out var p)) return;
        _grabbed = p;
        p.Grab(from, dir, n.T);
    }

    public void Drag(Vector3 from, Vector3 dir) => _grabbed?.Drag(from, dir);

    /// <summary>The trigger let go: a dragged card stays where it is, or goes back to the column dropped on its outline.</summary>
    public void Release()
    {
        _grabbed?.Drop();
        _grabbed = null;
    }

    static Quaternion Facing(Vector3 head, Vector3 at)
    {
        var d = at - head;
        float yaw = MathF.Atan2(-d.X, -d.Z);
        float pitch = MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length());
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
    }

    static float Wrap(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    // --- the pointer: close keys ---------------------------------------------------------------------------------------

    /// <summary>The pointer this frame: a ray in the frame's region (and, on the desktop, the pixel under the mouse).
    /// Lights the close key it's on; returns whether it's on one (the press is then the element's, not the world's).</summary>
    public bool Point(Vector3 origin, Vector3 dir, Vector2? pixel)
    {
        string? on = null;
        float best = float.MaxValue;
        foreach (var h in _hits)
        {
            Vector2 logical;
            float t;
            if (h.Corners is { } c)
            {
                var ex = c[1] - c[0];
                var ey = c[3] - c[0];
                var n = Vector3.Cross(ex, ey);
                float denom = Vector3.Dot(dir, n);
                if (MathF.Abs(denom) < 1e-9f) continue;
                t = Vector3.Dot(c[0] - origin, n) / denom;
                if (t <= 0) continue;
                var q = origin + dir * t - c[0];
                float u = Vector3.Dot(q, ex) / ex.LengthSquared(), w = Vector3.Dot(q, ey) / ey.LengthSquared();
                if (u is < 0 or > 1 || w is < 0 or > 1) continue;
                logical = new Vector2(u * h.LogicalW, w * h.LogicalH);
            }
            else if (pixel is { } px && h.Pixels.Contains(px.X, px.Y))
            {
                t = 0;
                logical = new Vector2((px.X - h.Pixels.Left) / h.Pixels.Width * h.LogicalW, (px.Y - h.Pixels.Top) / h.Pixels.Height * h.LogicalH);
            }
            else continue;
            if (h.Close is { } k && k.Contains(logical.X, logical.Y) && t < best) { best = t; on = h.Id; }
        }
        Shown.HoverClose = on;
        return on != null;
    }

    /// <summary>Nothing of ours under the pointer (the panel has it, or there's no pointer).</summary>
    public void Clear() => Shown.HoverClose = null;

    public void PressDown() => _pressed = Shown.HoverClose;

    /// <summary>A press that went down and up on the same close key dismisses its element.</summary>
    public void PressUp()
    {
        var p = _pressed;
        _pressed = null;
        if (p != null && p == Shown.HoverClose) Shown.Dismiss(p);
    }
}
