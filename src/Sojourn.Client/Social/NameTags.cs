using System.Numerics;
using LibreMetaverse;
using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Core.Agents;
using Sojourn.Core.World;
using Sojourn.Render.Frame;
using Sojourn.Render.Realtime;
using static Sojourn.Session.Assistant.Panel;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Social;

/// <summary>
/// Names over people. Which tags show, how big, how clear, and their pictures (a small glass capsule in the panel's
/// look: the display name, and the username under it where it differs and they're within Close metres).
///
/// Shown: everyone within chat range (20 m: whose words reach you, and who hears yours), fading out by 24 m; beyond
/// that, whoever you look at (within 5 degrees of the middle of the view, or the pointer on them), is speaking, typing,
/// or said something in the last 20 s. At most 16; when tags overlap, the farther one fades back. Sizes are angular,
/// shrinking slowly with distance (so far tags still read, but read as far), fixed in metres closer than 1.5 m.
/// A light at the left while they speak in voice; three dots inside while they type.
///
/// Since batch B4 (Claude Opus 5.5, 2026-10-28), as the viewer's LLVOAvatar::idleUpdateNameTagText: their group title
/// small over the name (the "Title" name-value; within Close, as the username), marks after the name for away, Do
/// Not Disturb and editing their appearance (the AWAY, BUSY and CUSTOMIZE animations: a moon, a no-entry sign, a
/// hanger rather than the viewer's words), the name the person calls them (B2's pseudonyms), a ring in their contact
/// set's colour (Firestorm's), or the friends' colour with that switch on; hidden behind walls as the viewer's tags
/// (the eye's depth test; an option to show them through), and the viewer's chat bubbles over them (off by default).
/// The hover tip at the pointer is drawn here too (HoverTips.cs), in the same atlas.
/// </summary>
public sealed class NameTags
{
    const float ChatRange = 20, FadeTo = 24, LookDeg = 5, Farthest = 128;
    const int MaxTags = 16;
    const float NameSize = 15, UserSize = 11.5f, PadX = 11, TypingRoom = 27, TitleRoom = 14, MarkW = 14;

    readonly AgentSession _s;
    readonly SocialFeed _feed;
    readonly Dictionary<UUID, (float Alpha, double LookedAt)> _state = new();
    /// <summary>Group titles and usernames by distance alone, in from Close, out past CloseOut (looking at someone made
    /// it feel inconsistent: the user, 2026-10-29).</summary>
    const float Close = 10, CloseOut = 11.5f;
    readonly HashSet<UUID> _close = new();
    readonly HashSet<UUID> _tagged = new();
    readonly HashSet<UUID> _resolving = new();
    double _resolveAt;

    /// <summary>Their voice's loudness 0-1 while they speak, else null.</summary>
    public Func<UUID, float?>? Speaking { get; set; }

    /// <summary>The social layer's switches (titles, colours, walls, bubbles).</summary>
    public SocialSettings Options { get; set; } = new();

    public NameTags(AgentSession session, SocialFeed feed)
    {
        _s = session;
        _feed = feed;
    }

    /// <summary>Has a tag this frame (voice draws no separate dot over them).</summary>
    public bool Tagged(UUID id) => _tagged.Contains(id);

    /// <summary>How tags are sized here: degrees per logical pixel at 3 m, how fast that shrinks with distance (an
    /// exponent), and the raster's scale (atlas pixels per logical pixel).</summary>
    public readonly record struct Sizing(float DegPerPx, float Falloff, float Scale);

    // falloff 0.7: a far tag grows only a little in the world (x1.8 at 20 m against its size at 3 m; it was 0.2, x4.4,
    // too large from a distance: the user, 2026-10-28); far names needn't read from afar
    public static Sizing Headset => new(0.08f, 0.7f, 2.4f);
    /// <summary>The desktop: about as many pixels as the panel's text uses, by the view's height.</summary>
    public static Sizing Desktop(float vfovRadians, int eyeHeight, float uiScale) =>
        new(vfovRadians * 180 / MathF.PI / Math.Max(1, eyeHeight) * uiScale * 1.3f, 0.1f, Math.Max(1, uiScale * 1.5f));

    /// <summary>Metres per atlas pixel at a distance.</summary>
    static float Mpp(Sizing size, float d, float scale)
    {
        float dd = MathF.Max(d, 1.5f);
        float deg = size.DegPerPx * MathF.Pow(dd / 3, -size.Falloff);
        return dd * MathF.Tan(deg * MathF.PI / 180) / scale;
    }

    /// <summary>This frame's tags, from where the renderer drew heads last frame (its region must be the eye's), and
    /// the atlas their pictures are in. Objects' floating text (<see cref="FloatingText"/>) shares the atlas: its
    /// pictures go in with the names', and its tags come back with them; so does the hover tip. names: whether names
    /// show at all.</summary>
    public (OverlayImage? Atlas, List<OverlayTag> Tags) Frame(ulong region, Vector3 eye, Vector3 forward, (ulong Region, NameTag[] Tags) heads,
        UUID pointed, PanelLook look, Sizing size, float dt, bool names = true, IReadOnlyList<FloatingText.Want>? texts = null,
        HoverTips.Want? tip = null)
    {
        _tagged.Clear();
        var result = new List<OverlayTag>();
        texts ??= [];
        if (!names || heads.Region != region || region == 0) heads = (region, []);
        if (heads.Tags.Length == 0 && texts.Count == 0 && tip == null) return (_atlas?.Image, result);
        double now = _feed.Now;
        var fwd = Vector3.Normalize(forward);
        var right = Vector3.Cross(fwd, Vector3.UnitZ);
        right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);
        var up = Vector3.Cross(right, fwd);
        var self = _s.AgentId;
        var opt = Options;
        bool tested = !opt.TagsThroughWalls;

        var cands = new List<(UUID Id, Vector3 Head, float D, float Alpha, bool Priority, bool Near, bool Looked, string Fallback)>();
        var seen = new HashSet<UUID>();
        foreach (var t in heads.Tags)
        {
            if (t.Id == UUID.Zero || t.Id == self || !seen.Add(t.Id)) continue;
            var dir = t.Head - eye;
            float d = dir.Length();
            if (d < 0.05f || d > Farthest) continue;
            if (_s.RlvActive && !_s.Rlv.CanShowNameTag(t.Id, d)) continue;   // RLV's @shownametags
            float cos = Vector3.Dot(dir / d, fwd);
            var st = _state.TryGetValue(t.Id, out var have) ? have : (Alpha: 0f, LookedAt: -1e9);
            if (cos > MathF.Cos(LookDeg * MathF.PI / 180) || t.Id == pointed) st.LookedAt = now;
            bool looked = now - st.LookedAt < 1.5;
            bool active = Speaking?.Invoke(t.Id) != null || _feed.TypingNearby(t.Id) || _feed.SinceSaid(t.Id) < 20;
            float target = active || looked ? 1 : d <= ChatRange ? 1 : d < FadeTo ? (FadeTo - d) / (FadeTo - ChatRange) : 0;
            st.Alpha = target > st.Alpha ? Math.Min(target, st.Alpha + dt * 5) : Math.Max(target, st.Alpha - dt * 2);
            _state[t.Id] = st;
            bool close = d < (_close.Contains(t.Id) ? CloseOut : Close);
            if (close) _close.Add(t.Id); else _close.Remove(t.Id);
            if (st.Alpha > 0.01f && cos > 0) cands.Add((t.Id, t.Head, d, st.Alpha, active || looked, close, looked, t.Text));
        }
        if (_state.Count > 256)
            foreach (var id in _state.Keys.Where(id => !seen.Contains(id)).ToList()) _state.Remove(id);
        _close.RemoveWhere(id => !seen.Contains(id));

        var want = new List<(UUID Id, Vector3 Head, float D, float Alpha, Variant V, Bubble? B, float BubbleAlpha)>();
        foreach (var c in cands.OrderBy(c => c.Priority ? 0 : 1).ThenBy(c => c.D).Take(MaxTags))
        {
            var (bubble, ba) = opt.ChatBubbles ? BubbleFor(c.Id, look, now) : (null, 0f);
            want.Add((c.Id, c.Head, c.D, c.Alpha, VariantFor(c.Id, c.Fallback, c.Near, look, now), bubble, ba));
        }
        ResolveNames();

        var pics = want.Select(w => (object)w.V).Concat(want.Where(w => w.B != null).Select(w => (object)w.B!))
            .Concat(texts.Select(t => (object)t.Text));
        if (tip is { } tw) pics = pics.Append(tw.Tip);
        var atlas = Atlas(pics.ToList(), size.Scale);
        if (atlas == null) return (null, result);
        FloatingText.Emit(result, texts, atlas.Places, atlas.Scale, eye, forward, size);
        var placed = new List<(float X0, float Y0, float X1, float Y1)>();
        float phase = (float)(now * 1.25 % 1);
        var accent = Colour(look.Accent, 0.95f);
        var inkC = Colour(look.Ink, 0.9f);
        foreach (var w in want.OrderBy(w => w.D))
        {
            if (!atlas.Places.TryGetValue(w.V, out var pic)) continue;
            float mpp = Mpp(size, w.D, atlas.Scale);
            float lift = 0.24f + 7 * atlas.Scale * mpp;
            // where it lands in the view (tangent space), to fade the farther of two that overlap
            var mid = w.Head + Vector3.UnitZ * (lift + pic.H * mpp / 2) - eye;
            float z = Vector3.Dot(mid, fwd);
            float alpha = w.Alpha;
            if (z > 0.1f)
            {
                float x = Vector3.Dot(mid, right) / z, y = Vector3.Dot(mid, up) / z;
                float hw = pic.W * mpp / 2 / z, hh = pic.H * mpp / 2 / z;
                var r = (x - hw, y - hh, x + hw, y + hh);
                if (placed.Any(p => r.Item1 < p.X1 && r.Item3 > p.X0 && r.Item2 < p.Y1 && r.Item4 > p.Y0)) alpha *= 0.35f;
                placed.Add(r);
            }
            float? speaking = Speaking?.Invoke(w.Id);
            result.Add(new OverlayTag(w.Id, new Vector4(pic.X, pic.Y, pic.W, pic.H), mpp, alpha, lift, pic.LineY,
                speaking ?? -1, accent, w.V.Typing ? phase : -1, inkC, new Vector3(pic.DotsX, 7 * atlas.Scale, 2.3f * atlas.Scale))
                { DepthTested = tested });
            // the chat bubble just over the tag
            if (w.B != null && atlas.Places.TryGetValue(w.B, out var bp))
                result.Add(new OverlayTag(w.Id, new Vector4(bp.X, bp.Y, bp.W, bp.H), mpp, w.Alpha * w.BubbleAlpha, lift + (pic.H + 3 * atlas.Scale) * mpp)
                    { DepthTested = tested });
            _tagged.Add(w.Id);
        }
        // the hover tip: over the point the pointer is on, never hidden (it's what the pointer touches)
        if (tip is { } t2 && atlas.Places.TryGetValue(t2.Tip, out var tp))
        {
            float mpp = Mpp(size, t2.Distance, atlas.Scale);
            result.Add(new OverlayTag(UUID.Zero, new Vector4(tp.X, tp.Y, tp.W, tp.H), mpp, t2.Alpha, 12 * atlas.Scale * mpp, At: t2.At) { DepthTested = false });
        }
        return (atlas.Image, result);
    }

    static Vector4 Colour(string hex, float a)
    {
        var k = SKColor.TryParse(hex, out var v) ? v : SKColors.Gray;
        return new(k.Red / 255f, k.Green / 255f, k.Blue / 255f, a);
    }

    // --- names ----------------------------------------------------------------------------------------------------

    /// <summary>The marks after a name (the viewer's "(Away)", "(Do Not Disturb)", "(Editing Appearance)").</summary>
    [Flags]
    public enum Marks { None = 0, Away = 1, Busy = 2, Appearance = 4 }

    /// <summary>A tag's picture: what it says, and the look it's drawn in; the group title over the name, the marks
    /// after it, a ring in a contact set's colour (0xRRGGBB, 0 for none).</summary>
    public sealed record Variant(string Display, string? User, bool Typing, PanelLook Look, string? Title = null, Marks Mark = Marks.None, uint Ring = 0);

    static readonly UUID Customize = new("038fcec9-5ebd-8a8e-0e2e-6e71a0a1ac53");   // ANIM_AGENT_CUSTOMIZE
    // the viewer's NameTagFriend is a pale yellow; on glass it reads as a warm ring
    const uint FriendRing = 0xF2D56B;

    sealed record Info(string? Title, Marks Mark, uint Ring, string? Pseudonym, double At);
    readonly Dictionary<UUID, Info> _info = new();

    /// <summary>What the region and the person's own lists say about someone, looked up twice a second at most.</summary>
    Info InfoFor(UUID id, double now)
    {
        if (_info.TryGetValue(id, out var had) && now - had.At < 0.5) return had;
        string? title = null;
        var marks = Marks.None;
        foreach (var rw in _s.VisibleRegions())
        {
            if (rw.AvatarById(id) is not { } av) continue;
            try
            {
                foreach (var nv in av.NameValues ?? [])
                    if (nv.Name == "Title") { title = nv.Value?.ToString(); break; }
            }
            catch { }
            List<Animation>? anims = null;
            lock (av) if (av.Animations is { } a) anims = a.ToList();
            foreach (var a in anims ?? [])
            {
                if (a.AnimationID == Animations.AWAY) marks |= Marks.Away;
                else if (a.AnimationID == Animations.BUSY) marks |= Marks.Busy;
                else if (a.AnimationID == Customize) marks |= Marks.Appearance;
            }
            break;
        }
        uint ring = 0;
        string? pseudonym = null;
        try
        {
            static uint B(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
            if (_s.People.SetColor(id) is { Length: >= 3 } col)
                // (pure black can't be told from "none": nudged)
                ring = Math.Max(0x010101u, B(col[0]) << 16 | B(col[1]) << 8 | B(col[2]));
            else if (Options.FriendColours && _s.Client?.Friends.FriendList.ContainsKey(id) == true) ring = FriendRing;
            pseudonym = _s.People.Pseudonym(id);
        }
        catch { }
        var info = new Info(string.IsNullOrWhiteSpace(title) ? null : title.Trim(), marks, ring, pseudonym, now);
        if (_info.Count > 256) _info.Clear();
        _info[id] = info;
        return info;
    }

    Variant VariantFor(UUID id, string fallback, bool near, PanelLook look, double now)
    {
        var names = _s.Names;
        // the region's own name for them (the object's FirstName/LastName) until the name service answers
        string? legacy = names.Legacy(id) ?? (fallback.Length > 0 ? fallback : null);
        lock (_asked) if (_asked.Add(id)) _resolving.Add(id);
        var user = legacy != null ? NameCache.UserName(legacy) : "";
        var info = InfoFor(id, now);
        // RLV's @shownames: the stand-in RLVa gives them, nothing that tells who they are
        if (_s.RlvActive && !_s.Rlv.CanShowName(id))
            return new Variant(Sojourn.Core.Rlv.RlvEngine.Anonym(legacy ?? fallback), null, _feed.TypingNearby(id),
                look with { TintOpacity = Math.Max(look.TintOpacity, 0.88f) }, null, Marks.None, info.Ring);
        var display = info.Pseudonym ?? names.DisplayName(id) ?? (user.Length > 0 ? user : "…");
        // the username under it only for a display name of their own (the viewer's "Display (user)"), or under the
        // name the person calls them
        bool custom = info.Pseudonym != null || names.Has(id) && names.Complete(id, user) != user;
        string? handle = custom && near ? Handle(legacy ?? user) : null;
        string? title = Options.GroupTitles && near ? info.Title : null;
        // a near-solid body whatever the panel's clarity, as the wrist's bell and the HUD board keep: a tag is small, drawn
        // into the eyes without the panel's glow, and over anything; on a clear look its dark ink was lost (2026-10-28)
        return new Variant(display, handle, _feed.TypingNearby(id), look with { TintOpacity = Math.Max(look.TintOpacity, 0.88f) },
            title, info.Mark, info.Ring);
    }

    /// <summary>SL's username form: "first.last", or "first" for a Resident.</summary>
    static string Handle(string legacy) => NameCache.UserName(legacy).ToLowerInvariant().Replace(' ', '.');

    readonly HashSet<UUID> _asked = new();

    /// <summary>Display names (and legacy names, where the region's are missing), asked once per person a session.</summary>
    void ResolveNames()
    {
        double now = _feed.Now;
        if (now < _resolveAt || _s.Client is not { } c) return;
        List<UUID> ids;
        lock (_asked)
        {
            if (_resolving.Count == 0) return;
            ids = _resolving.ToList();
            _resolving.Clear();
        }
        _resolveAt = now + 1;
        _ = Task.Run(async () =>
        {
            try
            {
                if (c.Avatars.DisplayNamesAvailable())
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var (ok, got, _) = await c.Avatars.GetDisplayNamesAsync(ids, cts.Token);
                    if (ok && got != null) foreach (var n in got) _s.Names.SetDisplay(n);
                }
                await _s.Names.ResolveAsync(c, ids, TimeSpan.FromSeconds(6));
            }
            catch { }
        });
    }

    // --- chat bubbles -----------------------------------------------------------------------------------------------

    /// <summary>A chat bubble's picture: their recent lines (a leading \u0002 marks a whisper, drawn dim).</summary>
    public sealed record Bubble(string Lines, PanelLook Look);

    const double BubbleFor_ = 20, BubbleFade = 4;
    const int BubbleLines = 12;
    const float BubbleSize = 13.5f, BubbleLine = 18, BubbleWidth = 230, BubblePad = 10;

    /// <summary>The viewer's chat bubble (UseChatBubbles): what they said in the last 20 s, newest at the bottom, at most
    /// 12 lines, fading out over its last 4 s.</summary>
    (Bubble?, float) BubbleFor(UUID id, PanelLook look, double now)
    {
        var said = _feed.Said(id, BubbleFor_);
        if (said.Count == 0) return (null, 0);
        double age = now - said[^1].At;
        float alpha = age < BubbleFor_ - BubbleFade ? 1 : (float)Math.Clamp((BubbleFor_ - age) / BubbleFade, 0, 1);
        var text = string.Join('\n', said.Select(l => (l.Style == FeedStyle.Whisper ? "\u0002" : "") + Links.Labelled(l.Text).Replace('\n', ' ')));
        return (new Bubble(text, look with { TintOpacity = Math.Max(look.TintOpacity, 0.82f) }), alpha);
    }

    internal static Pic DrawBubble(Bubble b, float scale)
    {
        var ink = Ink.For(b.Look);
        var f = Font(ink.Regular, BubbleSize);
        var lines = new List<(string Text, bool Dim)>();
        foreach (var said in b.Lines.Split('\n'))
        {
            bool dim = said.StartsWith('\u0002');
            foreach (var l in Wrap(dim ? said[1..] : said, f, BubbleWidth - 2 * BubblePad, BubbleLines)) lines.Add((l, dim));
        }
        if (lines.Count > BubbleLines) lines = lines.Skip(lines.Count - BubbleLines).ToList();
        float w = MathF.Ceiling(2 * BubblePad + Math.Max(24, lines.Max(l => Measure(f, l.Text))));
        float h = 2 * 7 + lines.Count * BubbleLine;
        var (pixels, pw, ph, _) = Rasterise(scale, w, new List<PanelKey>(), (c, height) =>
        {
            var draw = new Painter(c, ink, w);
            draw.Body(h);
            float y = 7 + 13.5f;
            foreach (var (t, dim) in lines)
            {
                draw.Text(t, BubblePad, y, f, dim ? ink.Dim : ink.Text);
                y += BubbleLine;
            }
            return h;
        });
        return new Pic(pixels, pw, ph, 0, 0);
    }

    // --- the atlas ----------------------------------------------------------------------------------------------------

    public sealed record Pic(byte[] Rgba, int W, int H, float LineY, float DotsX);
    public sealed record Place(int X, int Y, int W, int H, float LineY, float DotsX);
    // keyed by what a picture shows: a Variant (a name tag), a Bubble, a FloatingText.Text or a HoverTips.Tip
    sealed record Built(OverlayImage Image, Dictionary<object, Place> Places, float Scale, HashSet<object> Set);

    readonly Dictionary<object, Pic> _pics = new();
    volatile Built? _atlas;
    bool _building;
    long _version;

    /// <summary>Whether the atlas in use holds a picture (a FloatingText.Text, say): one that isn't in it yet isn't drawn
    /// until the atlas is rebuilt, a frame or two.</summary>
    public bool Holds(object picture) => _atlas?.Places.ContainsKey(picture) == true;

    /// <summary>The atlas holding every wanted picture; rebuilt off the main thread when the wanted set grows past it
    /// (the old one serves meanwhile).</summary>
    Built? Atlas(List<object> wanted, float scale)
    {
        var have = _atlas;
        bool fits = have != null && have.Scale == scale && wanted.All(have.Set.Contains);
        if (!fits && !_building)
        {
            _building = true;
            // keep what's there (people come and go from view), up to a point
            var set = new HashSet<object>(wanted);
            if (have != null && have.Scale == scale && have.Set.Count < 48) set.UnionWith(have.Set.Where(v => wanted.All(w => !SameTag(w, v)) ));
            Task.Run(() =>
            {
                try { _atlas = Build(set, scale); }
                catch { }
                finally { _building = false; }
            });
        }
        return have != null && have.Scale == scale ? have : null;
    }

    static bool SameTag(object a, object b) => a switch
    {
        Variant va when b is Variant vb => va.Display == vb.Display && ReferenceEquals(va.Look, vb.Look),
        // a bubble or tip that's moved on isn't worth keeping
        Bubble when b is Bubble => true,
        HoverTips.Tip when b is HoverTips.Tip => true,
        _ => Equals(a, b),
    };

    Built Build(HashSet<object> set, float scale)
    {
        var pics = new List<(object V, Pic P)>();
        lock (_pics)
        {
            if (_pics.Count > 256) _pics.Clear();
            foreach (var v in set)
            {
                if (!_pics.TryGetValue(v, out var p))
                    _pics[v] = p = v switch
                    {
                        FloatingText.Text t => FloatingText.Draw(t, scale),
                        Bubble b => DrawBubble(b, scale),
                        HoverTips.Tip tip => HoverTips.Draw(tip, scale),
                        _ => Draw((Variant)v, scale),
                    };
                pics.Add((v, p));
            }
        }
        // shelves, tallest first
        int width = scale > 2 ? 1024 : 512;
        int x = 0, y = 0, row = 0;
        var places = new Dictionary<object, Place>();
        foreach (var (v, p) in pics.OrderByDescending(p => p.P.H))
        {
            if (x + p.W > width) { x = 0; y += row + 2; row = 0; }
            places[v] = new Place(x, y, p.W, p.H, p.LineY, p.DotsX);
            x += p.W + 2;
            row = Math.Max(row, p.H);
        }
        int height = Math.Max(1, y + row);
        var rgba = new byte[width * height * 4];
        foreach (var (v, p) in pics)
        {
            var at = places[v];
            for (int r = 0; r < p.H; r++)
                Buffer.BlockCopy(p.Rgba, r * p.W * 4, rgba, ((at.Y + r) * width + at.X) * 4, p.W * 4);
        }
        return new Built(new OverlayImage(rgba, width, height, Interlocked.Increment(ref _version)), places, scale, set);
    }

    /// <summary>One tag: the capsule; the group title (small, dim) over the display name (semibold), its marks after it,
    /// the username (small, dim) under it, centred; room at the right for the typing dots; a ring in a set's colour.</summary>
    internal static Pic Draw(Variant v, float scale)
    {
        var ink = Ink.For(v.Look);
        var nameF = Font(ink.Semi, NameSize);
        var userF = Font(ink.Regular, UserSize);
        var display = Ellipsize(v.Display, nameF, 260);
        var user = v.User != null ? Ellipsize(v.User, userF, 260) : null;
        var title = v.Title != null ? Ellipsize(v.Title, userF, 260) : null;
        var marks = new[] { Marks.Away, Marks.Busy, Marks.Appearance }.Where(m => v.Mark.HasFlag(m)).ToList();
        float nameW = Measure(nameF, display);
        float marksW = marks.Count > 0 ? 3 + marks.Count * MarkW : 0;
        float rowW = nameW + marksW;
        float textW = Math.Max(rowW, Math.Max(user != null ? Measure(userF, user) : 0, title != null ? Measure(userF, title) : 0));
        float w = MathF.Ceiling(PadX + textW + (v.Typing ? TypingRoom : 0) + PadX);
        float top = title != null ? TitleRoom : 0;
        float h = (user != null ? 41 : 27) + top;
        float lineY = 13.5f + top;
        var (pixels, pw, ph, used) = Rasterise(scale, w, new List<PanelKey>(), (c, height) =>
        {
            var draw = new Painter(c, ink, w);
            draw.Body(h);
            float mid = PadX + textW / 2;
            if (title != null) draw.Text(title, mid - Measure(userF, title) / 2, 15, userF, ink.Dim);
            float x0 = mid - rowW / 2;
            draw.Text(display, x0, lineY + 5.4f, nameF, ink.Text);
            if (c != null)
                for (int i = 0; i < marks.Count; i++) Mark(c, marks[i], x0 + nameW + 3 + MarkW * i + MarkW / 2, lineY + 0.3f, ink);
            if (user != null) draw.Text(user, mid - Measure(userF, user) / 2, 32.5f + top, userF, ink.Dim);
            if (v.Ring != 0 && c != null)
            {
                var col = new SKColor((byte)(v.Ring >> 16), (byte)(v.Ring >> 8), (byte)v.Ring);
                float rad = Math.Min(v.Look.Radius, h / 2);
                using var ring = new SKPaint { Color = col.WithAlpha(235), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                c.DrawRoundRect(new SKRoundRect(new SKRect(1.6f, 1.6f, w - 1.6f, h - 1.6f), Math.Max(1, rad - 1.1f)), ring);
            }
            return h;
        });
        return new Pic(pixels, pw, ph, lineY * used, (PadX + textW + 9) * used);
    }

    /// <summary>A mark after the name, centred at (cx, cy), about 10 px: away a crescent moon, Do Not Disturb a no-entry
    /// sign (the one solid colour, a soft red), editing their appearance a hanger.</summary>
    static void Mark(SKCanvas c, Marks m, float cx, float cy, Ink ink)
    {
        switch (m)
        {
            case Marks.Away:
            {
                using var moon = new SKPath();
                moon.AddCircle(cx, cy, 4.8f);
                using var bite = new SKPath();
                bite.AddCircle(cx + 2.6f, cy - 2.0f, 4.1f);
                using var cut = moon.Op(bite, SKPathOp.Difference);
                using var p = new SKPaint { Color = ink.Dim, IsAntialias = true };
                if (cut != null) c.DrawPath(cut, p);
                break;
            }
            case Marks.Busy:
            {
                using var disc = new SKPaint { Color = new SKColor(214, 84, 76), IsAntialias = true };
                c.DrawCircle(cx, cy, 5, disc);
                using var bar = new SKPaint { Color = SKColors.White, IsAntialias = true };
                c.DrawRoundRect(new SKRect(cx - 3, cy - 0.9f, cx + 3, cy + 0.9f), 0.8f, 0.8f, bar);
                break;
            }
            case Marks.Appearance:
            {
                using var s = new SKPaint { Color = ink.Dim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
                using var hook = new SKPath();
                hook.MoveTo(cx - 1.4f, cy - 3.2f);
                hook.CubicTo(cx - 1.4f, cy - 5.6f, cx + 1.8f, cy - 5.6f, cx + 1.4f, cy - 3.4f);
                hook.CubicTo(cx + 1.2f, cy - 2.2f, cx, cy - 2.0f, cx, cy - 0.8f);
                c.DrawPath(hook, s);
                using var bar = new SKPath();
                bar.MoveTo(cx, cy - 0.8f);
                bar.LineTo(cx + 5.5f, cy + 3.6f);
                bar.LineTo(cx - 5.5f, cy + 3.6f);
                bar.Close();
                c.DrawPath(bar, s);
                break;
            }
        }
    }
}
