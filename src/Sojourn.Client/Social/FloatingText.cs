using System.Numerics;
using LibreMetaverse;
using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using static Sojourn.Session.Assistant.Panel;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Social;

/// <summary>
/// Objects' floating text (llSetText: vendor prices, signs, titlers, game status), as the viewer's LLHUDText draws it:
/// the script's own words in its colour and opacity, centred, growing upward from 0.6 of the object's height above its
/// middle (LLViewerObject::updateText), the last 10 lines. It fades from 8 m to 12 m away (measured from the side of the
/// object nearer you, as the viewer does), except for what you point at or look straight at, which reads up to 64 m
/// (the viewer's PrimTextMaxDrawDistance); hidden behind walls (the viewer depth-tests it; here a ray to it, a few a
/// frame); at most 20, nearest first, the farther of two that overlap faded back; nothing on HUDs (Hud/HudText.cs draws theirs), nothing from people
/// you've blocked, and in the headset not your own (it would float in your eyes). A thin dark edge keeps light text
/// readable on a bright sky, as the viewer's drop shadow does. Drawn as name tags are (the labels atlas, angular
/// sizes, upright and turned to the eye). The view section's hover text switch.
/// </summary>
public sealed class FloatingText
{
    const float FadeFrom = 8, FadeTo = 12, Pointed = 64, LookDeg = 5;
    const int MaxShown = 20, MaxLines = 10;
    const float TextSize = 14, MaxWidth = 320, Pad = 4;

    /// <summary>A picture's content: its lines and colour (the atlas key).</summary>
    public sealed record Text(string Lines, byte R, byte G, byte B);

    /// <summary>One to draw this frame: where its bottom centre stands (the eye's region frame), how far, how clear; Over:
    /// drawn over everything rather than hidden by what's in front (the client's own labels, as the tracking beacon's).</summary>
    public readonly record struct Want(Text Text, Vector3 At, float Distance, float Alpha, bool Over = false);

    readonly AgentSession _s;
    readonly Dictionary<UUID, (float Alpha, bool Hidden, double CheckedAt)> _state = new();
    List<(ulong Region, Vector3 Offset, Primitive Prim, UUID Root, UUID Wearer)> _cands = new();
    double _scanAt;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public FloatingText(AgentSession session) => _s = session;

    /// <summary>This frame's floating text for an eye in the agent's region. pointed: the object the pointer is on
    /// (its root), read further away. self: draw your own attachments' text (the desktop).</summary>
    public List<Want> Frame(FrameRenderer renderer, ulong region, Vector3 eye, Vector3 forward, UUID pointed, bool self, float dt)
    {
        var result = new List<Want>();
        if (_s.Client is not { Network.CurrentSim: { } sim } c || sim.Handle != region) return result;
        double now = _clock.Elapsed.TotalSeconds;
        if (now >= _scanAt) { _scanAt = now + 0.25; Scan(c, eye, self); }
        var fwd = Vector3.Normalize(forward);
        var shown = new List<(Want W, UUID Id)>();
        var seen = new HashSet<UUID>();
        int checks = 0;
        foreach (var (rh, offset, p, root, wearer) in _cands)
        {
            string text = p.Text ?? "";
            if (string.IsNullOrWhiteSpace(text) || p.TextColor.A < 0.01f) continue;
            var centre = renderer.DrawnPosition(rh, p.LocalID) is { } dp ? dp + offset : (Vector3?)null;
            if (centre is not { } mid) continue;
            var at = mid + new Vector3(0, 0, p.Scale.Z * 0.6f);
            var to = at - eye;
            float d = to.Length();
            if (d < 0.05f) continue;
            // the viewer measures from the side of the object nearer the camera (its radius closer)
            float radius = new Vector3(p.Scale.X, p.Scale.Y, p.Scale.Z).Length() / 2;
            float faded = MathF.Max(0, d - radius);
            bool looked = root == pointed || Vector3.Dot(to / d, fwd) > MathF.Cos(LookDeg * MathF.PI / 180);
            float target = looked && d < Pointed ? 1 : faded <= FadeFrom ? 1 : faded < FadeTo ? (FadeTo - faded) / (FadeTo - FadeFrom) : 0;
            var st = _state.TryGetValue(p.ID, out var have) ? have : (Alpha: 0f, Hidden: false, CheckedAt: -1.0);
            // behind something: a ray to it, a few objects a frame, each again after half a second
            if (target > 0 && checks < 4 && now - st.CheckedAt > 0.5)
            {
                checks++;
                st.CheckedAt = now;
                var hit = renderer.Pick(region, eye, to, d - 0.2f, c.Self.LocalID, ground: true);
                st.Hidden = hit is { } h && h.RootId != root && h.Id != wearer && h.Id != p.ID;
            }
            if (st.Hidden) target = 0;
            st.Alpha = target > st.Alpha ? Math.Min(target, st.Alpha + dt * 5) : Math.Max(target, st.Alpha - dt * 3);
            _state[p.ID] = st;
            seen.Add(p.ID);
            if (st.Alpha < 0.01f || Vector3.Dot(to, fwd) <= 0) continue;
            shown.Add((new Want(KeyOf(p.ID, text, p.TextColor), at, d, st.Alpha * p.TextColor.A), p.ID));
        }
        if (_state.Count > 512)
            foreach (var id in _state.Keys.Where(id => !seen.Contains(id)).ToList()) _state.Remove(id);
        foreach (var (w, _) in shown.OrderBy(s => s.W.Distance).Take(MaxShown)) result.Add(w);
        return result;
    }

    /// <summary>Which objects carry text near enough to matter (the pointed-at reach), every quarter second.</summary>
    void Scan(GridClient c, Vector3 eye, bool self)
    {
        var list = new List<(ulong, Vector3, Primitive, UUID, UUID)>();
        foreach (var w in _s.VisibleRegions())
        {
            var fo = _s.FrameOffset(w);
            var offset = new Vector3(fo.X, fo.Y, fo.Z);
            foreach (var p in w.Prims.Values)
            {
                if (string.IsNullOrEmpty(p.Text)) continue;
                if ((int)p.PrimData.AttachmentPoint is >= 31 and <= 38) continue;   // HUDs aren't drawn
                bool attached = _s.Objects.IsAttachment(w, p, out var wearer, out var root);
                if (attached && wearer == null) continue;
                if (attached && wearer!.ID == c.Self.AgentID && !self) continue;
                if (attached && _s.Mutes.IsMuted(wearer!.ID, wearer.Name)) continue;
                if (_s.Mutes.IsMuted(root.ID, root.Properties?.Name ?? "")) continue;
                // the attachment's wearer stands where the region has them; objects where the world model has them
                var pos = attached ? wearer!.Position : w.RegionPosition(p);
                if (Vector3.Distance(new Vector3(pos.X, pos.Y, pos.Z) + offset, eye) > Pointed + 8) continue;
                list.Add((w.Handle, offset, p, root.ID, wearer?.ID ?? UUID.Zero));
            }
        }
        _cands = list;
    }

    // each object's text as last made into a key (splitting and trimming it every frame was most of this view's garbage)
    readonly Dictionary<UUID, (string Raw, Color4 Color, Text Key)> _keys = new();

    Text KeyOf(UUID id, string text, Color4 c)
    {
        if (_keys.TryGetValue(id, out var k) && ReferenceEquals(k.Raw, text) && k.Color == c) return k.Key;
        if (_keys.Count > 1024) _keys.Clear();
        var key = Key(text, c);
        _keys[id] = (text, c, key);
        return key;
    }

    internal static Text Key(string text, Color4 c)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > MaxLines) lines = lines.Skip(lines.Count - MaxLines).ToList();
        static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
        return new Text(string.Join('\n', lines), B(c.R), B(c.G), B(c.B));
    }

    /// <summary>The tags for this frame's wants, from the atlas: sized as name tags are, the farther of two that
    /// overlap faded back.</summary>
    public static void Emit(List<OverlayTag> into, IReadOnlyList<Want> wants, Dictionary<object, NameTags.Place> places, float scale,
        Vector3 eye, Vector3 forward, NameTags.Sizing size)
    {
        if (wants.Count == 0) return;
        var fwd = Vector3.Normalize(forward);
        var right = Vector3.Cross(fwd, Vector3.UnitZ);
        right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);
        var up = Vector3.Cross(right, fwd);
        var placed = new List<(float X0, float Y0, float X1, float Y1)>();
        foreach (var w in wants.OrderBy(w => w.Distance))
        {
            if (!places.TryGetValue(w.Text, out var pic)) continue;
            float dd = MathF.Max(w.Distance, 1.5f);
            float deg = size.DegPerPx * MathF.Pow(dd / 3, -size.Falloff);
            float mpp = dd * MathF.Tan(deg * MathF.PI / 180) / scale;
            float alpha = w.Alpha;
            var mid = w.At + Vector3.UnitZ * (pic.H * mpp / 2) - eye;
            float z = Vector3.Dot(mid, fwd);
            if (z > 0.1f)
            {
                float x = Vector3.Dot(mid, right) / z, y = Vector3.Dot(mid, up) / z;
                float hw = pic.W * mpp / 2 / z, hh = pic.H * mpp / 2 / z;
                var r = (x - hw, y - hh, x + hw, y + hh);
                if (placed.Any(p => r.Item1 < p.X1 && r.Item3 > p.X0 && r.Item2 < p.Y1 && r.Item4 > p.Y0)) alpha *= 0.35f;
                placed.Add(r);
            }
            into.Add(new OverlayTag(UUID.Zero, new Vector4(pic.X, pic.Y, pic.W, pic.H), mpp, alpha, 0, At: w.At) { DepthTested = w.Over ? false : null });
        }
    }

    /// <summary>One text's picture: its lines centred in its colour, each with a thin dark edge.</summary>
    internal static NameTags.Pic Draw(Text t, float scale)
    {
        var ink = Ink.For(PanelLook.Presets[0]);
        var font = Font(ink.Regular, TextSize);
        var lines = t.Lines.Split('\n').Select(l => l.Length == 0 ? l : Ellipsize(l, font, MaxWidth)).ToList();
        float lineH = TextSize * 1.3f;
        float w = MathF.Ceiling(Pad * 2 + Math.Max(1, lines.Max(l => l.Length == 0 ? 0 : Measure(font, l))));
        float h = Pad * 2 + lineH * lines.Count;
        var colour = new SKColor(t.R, t.G, t.B);
        var (pixels, pw, ph, _) = Rasterise(scale, w, new List<PanelKey>(), (c, height) =>
        {
            if (c == null) return h;
            using var edge = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, StrokeJoin = SKStrokeJoin.Round };
            using var fill = new SKPaint { Color = colour, IsAntialias = true };
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Length == 0) continue;
                var f = Painter.FontFor(font, lines[i]);
                float x = (w - Measure(font, lines[i])) / 2, y = Pad + lineH * i + TextSize;
                c.DrawText(lines[i], x, y, SKTextAlign.Left, f, edge);
                c.DrawText(lines[i], x, y, SKTextAlign.Left, f, fill);
            }
            return h;
        });
        return new NameTags.Pic(pixels, pw, ph, 0, 0);
    }
}
