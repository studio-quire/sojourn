using System.Numerics;
using Sojourn.Client.Xr;
using Sojourn.Render.Realtime;

namespace Sojourn.Client.Assistant;

/// <summary>A menu over the headset's first eye: its picture (the bytes its quad layer gets: sRGB-encoded,
/// premultiplied in linear light, GlassLight.Finish), shown at W x H of it (the top left, as the layer crops), and where
/// its quad sits in the play space; Clip: the part of it shown, when the menu goes in parts (Xr/MenuOrder.cs).</summary>
public readonly record struct SeenLayer(string Name, OverlayImage Image, int W, int H, Vector3 Centre, Quaternion Rotation, float WidthM,
    MenuClip Clip = default);

/// <summary>What a frame shows over its first eye image: in the headset the eye's pose and field of view and the menus
/// on quad layers, in the compositor's order (XrSession.Seen); on the desktop nothing (the overlay put the panel and
/// feed in the eye image), only where the panel is, for a close-up.</summary>
public sealed record SeenView(XrEye? Eye, IReadOnlyList<SeenLayer> Layers, IReadOnlyList<string> LeftOut) : ISeenView
{
    public IReadOnlyList<string> LayerNames => Layers.Select(l => l.Name).ToArray();

    public byte[] Compose(byte[] rgba, int w, int h) => Eye != null && Layers.Count > 0 ? SeenShots.Compose(rgba, w, h, this, new(0, 0, w, h), w, h) : rgba;

    public (byte[] Rgba, int W, int H)? CloseUp(byte[] rgba, int w, int h, string name, out string? error) =>
        SeenShots.CloseUp(this, name, w, h, out error) is { } c ? (SeenShots.Compose(rgba, w, h, this, c.R, c.W, c.H), c.W, c.H) : null;

    /// <summary>The desktop panel's rectangle in the eye image's pixels.</summary>
    public (int X, int Y, int W, int H)? Panel { get; init; }

    public static SeenView Desktop(OverlayScene? overlay) =>
        new(null, [], []) { Panel = overlay?.Panel is { } p && overlay.PanelContent ? ((int)overlay.PanelAt.X, (int)overlay.PanelAt.Y, p.Width, p.Height) : null };
}

/// <summary>
/// A picture of what the person sees (LLViewerWindow saveSnapshot showui, dev.py shot): the first eye as it went to the
/// display (FrameRequest.SeenTo: overlay, frost and HUDs in it) with the headset's menus laid over it on the CPU as the
/// compositor lays its quad layers over the projection layer: each pixel's ray from the eye meets each quad's plane, the
/// picture there is sampled bilinearly in linear light (an sRGB texture's filtering), and added over what's below,
/// premultiplied, in linear light (dst = src + (1 - a) dst), farthest layer first; then encoded again. Not modelled: the
/// runtime's timewarp, its lens distortion and the display's own sampling, so the result is the eye image's grid, not
/// the panel's pixels on the lenses.
/// A close-up renders a rectangle of the same view at a higher density (about the menu's own pixels), the eye image
/// under it sampled bilinearly. Runs on a worker, once the frame's readback has arrived. Claude Opus 5.5, 2026-10-28.
/// </summary>
public static class SeenShots
{
    public readonly record struct Rect(float X, float Y, float W, float H);

    /// <summary>The view through rectangle r of the eye image (its pixels) at ow x oh: RGBA8, gamma, opaque.</summary>
    public static byte[] Compose(byte[] eye, int w, int h, SeenView view, Rect r, int ow, int oh)
    {
        var o = new byte[ow * oh * 4];
        float sx = r.W / ow, sy = r.H / oh;
        int rx = (int)r.X, ry = (int)r.Y;
        if (ow == (int)r.W && oh == (int)r.H && rx == r.X && ry == r.Y && rx >= 0 && ry >= 0 && rx + ow <= w && ry + oh <= h)
        {
            for (int y = 0; y < oh; y++) Buffer.BlockCopy(eye, ((ry + y) * w + rx) * 4, o, y * ow * 4, ow * 4);
        }
        else
        {
            for (int y = 0; y < oh; y++)
            {
                float fy = r.Y + (y + 0.5f) * sy - 0.5f;
                for (int x = 0; x < ow; x++)
                {
                    Sample(eye, w, w, h, r.X + (x + 0.5f) * sx - 0.5f, fy, out float cr, out float cg, out float cb, out _);
                    int i = (y * ow + x) * 4;
                    (o[i], o[i + 1], o[i + 2]) = (E(cr), E(cg), E(cb));
                }
            }
        }
        for (int i = 3; i < o.Length; i += 4) o[i] = 255;
        if (view.Eye is { } e)
            foreach (var l in view.Layers) Lay(o, ow, oh, r, w, h, e, l);
        return o;
    }

    /// <summary>Where a close-up of a menu goes: its rectangle in the eye image with a margin, and the size to render it
    /// at (the menu at about its own pixels, at most 4x the eye's and 2560 on the long side); or why there's none.</summary>
    public static (Rect R, int W, int H)? CloseUp(SeenView view, string name, int w, int h, out string? error)
    {
        error = null;
        Rect box;
        float scale = 1;
        if (view.Eye is { } e)
        {
            var l = view.Layers.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (l.Image is null) l = view.Layers.FirstOrDefault(x => x.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (l.Image is null)
            {
                error = $"no {name} showing" + (view.Layers.Count > 0 ? $" (showing: {string.Join(", ", view.Layers.Select(x => x.Name))})" : "");
                return null;
            }
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var c in Corners(l))
            {
                if (Project(e, c, w, h) is not { } p) { error = $"the {l.Name} is beside or behind the eye"; return null; }
                (minX, minY, maxX, maxY) = (Math.Min(minX, p.X), Math.Min(minY, p.Y), Math.Max(maxX, p.X), Math.Max(maxY, p.Y));
            }
            box = new Rect(minX, minY, maxX - minX, maxY - minY);
            scale = Math.Clamp(l.W / Math.Max(1, box.W), 1, 4);
        }
        else if (name.Equals("panel", StringComparison.OrdinalIgnoreCase) && view.Panel is { } p)
            box = new Rect(p.X, p.Y, p.W, p.H);
        else
        {
            error = view.Panel != null ? "on the desktop only the panel has a close-up" : "no panel showing";
            return null;
        }
        float pad = view.Eye != null ? 0.15f * Math.Max(box.W, box.H) : 24;
        float x0 = MathF.Floor(Math.Max(0, box.X - pad)), y0 = MathF.Floor(Math.Max(0, box.Y - pad));
        float x1 = MathF.Ceiling(Math.Min(w, box.X + box.W + pad)), y1 = MathF.Ceiling(Math.Min(h, box.Y + box.H + pad));
        if (x1 - x0 < 2 || y1 - y0 < 2) { error = "it's out of view"; return null; }
        var r = new Rect(x0, y0, x1 - x0, y1 - y0);
        scale = Math.Min(scale, 2560 / Math.Max(r.W, r.H));
        return (r, Math.Max(1, (int)(r.W * scale)), Math.Max(1, (int)(r.H * scale)));
    }

    static Vector3[] Corners(in SeenLayer l)
    {
        var ux = Vector3.Transform(Vector3.UnitX, l.Rotation) * (l.WidthM / 2);
        var uy = Vector3.Transform(Vector3.UnitY, l.Rotation) * (l.WidthM * l.H / l.W / 2);
        return [l.Centre - ux + uy, l.Centre + ux + uy, l.Centre + ux - uy, l.Centre - ux - uy];
    }

    /// <summary>A point of the play space in the eye image's pixels (top left 0,0), or null at or behind the eye's plane.</summary>
    static Vector2? Project(in XrEye e, Vector3 p, int w, int h)
    {
        var l = Vector3.Transform(p - e.Position, Quaternion.Conjugate(e.Orientation));
        if (l.Z > -1e-3f) return null;
        float tx = l.X / -l.Z, ty = l.Y / -l.Z;
        return new Vector2((tx - e.TanLeft) / (e.TanRight - e.TanLeft) * w, (e.TanUp - ty) / (e.TanUp - e.TanDown) * h);
    }

    /// <summary>One quad layer over the output, where its rays meet it.</summary>
    static void Lay(byte[] o, int ow, int oh, Rect r, int w, int h, XrEye e, SeenLayer l)
    {
        if (l.W <= 0 || l.H <= 0 || l.WidthM <= 0) return;
        float sx = r.W / ow, sy = r.H / oh;
        // the output pixels its corners cover (all of them when one is beside or behind the eye)
        int bx0 = 0, by0 = 0, bx1 = ow, by1 = oh;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        bool bounded = true;
        foreach (var c in Corners(l))
        {
            if (Project(e, c, w, h) is not { } p) { bounded = false; break; }
            float cx = (p.X - r.X) / sx, cy = (p.Y - r.Y) / sy;
            (minX, minY, maxX, maxY) = (Math.Min(minX, cx), Math.Min(minY, cy), Math.Max(maxX, cx), Math.Max(maxY, cy));
        }
        if (bounded)
        {
            bx0 = Math.Max(0, (int)MathF.Floor(minX) - 1);
            by0 = Math.Max(0, (int)MathF.Floor(minY) - 1);
            bx1 = Math.Min(ow, (int)MathF.Ceiling(maxX) + 1);
            by1 = Math.Min(oh, (int)MathF.Ceiling(maxY) + 1);
            if (bx0 >= bx1 || by0 >= by1) return;
        }

        var right = Vector3.Transform(Vector3.UnitX, e.Orientation);
        var up = Vector3.Transform(Vector3.UnitY, e.Orientation);
        var fwd = Vector3.Transform(-Vector3.UnitZ, e.Orientation);
        var ux = Vector3.Transform(Vector3.UnitX, l.Rotation);
        var uy = Vector3.Transform(Vector3.UnitY, l.Rotation);
        var n = Vector3.Transform(Vector3.UnitZ, l.Rotation);
        var toCentre = l.Centre - e.Position;
        float num = Vector3.Dot(toCentre, n);
        float wm = l.WidthM, hm = l.WidthM * l.H / l.W;
        float spanX = e.TanRight - e.TanLeft, spanY = e.TanUp - e.TanDown;
        var px = l.Image.Rgba;
        int stride = l.Image.Width;

        for (int y = by0; y < by1; y++)
        {
            float ty = e.TanUp - (r.Y + (y + 0.5f) * sy) / h * spanY;
            for (int x = bx0; x < bx1; x++)
            {
                float tx = e.TanLeft + (r.X + (x + 0.5f) * sx) / w * spanX;
                var d = right * tx + up * ty + fwd;
                float den = Vector3.Dot(d, n);
                if (MathF.Abs(den) < 1e-9f) continue;
                float t = num / den;
                if (t <= 0) continue;
                var hit = d * t - toCentre;
                float u = Vector3.Dot(hit, ux) / wm + 0.5f, v = 0.5f - Vector3.Dot(hit, uy) / hm;
                if (u < 0 || u >= 1 || v < 0 || v >= 1 || !l.Clip.Keeps(u, v)) continue;
                Sample(px, stride, l.W, l.H, u * l.W - 0.5f, v * l.H - 0.5f, out float sr, out float sg, out float sb, out float sa);
                if (sa <= 0 && sr + sg + sb <= 0) continue;
                int i = (y * ow + x) * 4;
                float k = 1 - sa;
                o[i] = E(sr + k * Dec[o[i]]);
                o[i + 1] = E(sg + k * Dec[o[i + 1]]);
                o[i + 2] = E(sb + k * Dec[o[i + 2]]);
            }
        }
    }

    /// <summary>Bilinear at (fx, fy) in texel space (texel centres at integers), edges clamped, of the top left pw x ph of
    /// an RGBA8 image stride pixels wide: colour decoded to linear light first, alpha as it is.</summary>
    static void Sample(byte[] px, int stride, int pw, int ph, float fx, float fy, out float r, out float g, out float b, out float a)
    {
        float flx = MathF.Floor(fx), fly = MathF.Floor(fy);
        float ax = fx - flx, ay = fy - fly;
        int x0 = Math.Clamp((int)flx, 0, pw - 1), x1 = Math.Clamp((int)flx + 1, 0, pw - 1);
        int y0 = Math.Clamp((int)fly, 0, ph - 1), y1 = Math.Clamp((int)fly + 1, 0, ph - 1);
        int i00 = (y0 * stride + x0) * 4, i10 = (y0 * stride + x1) * 4, i01 = (y1 * stride + x0) * 4, i11 = (y1 * stride + x1) * 4;
        float w00 = (1 - ax) * (1 - ay), w10 = ax * (1 - ay), w01 = (1 - ax) * ay, w11 = ax * ay;
        r = w00 * Dec[px[i00]] + w10 * Dec[px[i10]] + w01 * Dec[px[i01]] + w11 * Dec[px[i11]];
        g = w00 * Dec[px[i00 + 1]] + w10 * Dec[px[i10 + 1]] + w01 * Dec[px[i01 + 1]] + w11 * Dec[px[i11 + 1]];
        b = w00 * Dec[px[i00 + 2]] + w10 * Dec[px[i10 + 2]] + w01 * Dec[px[i01 + 2]] + w11 * Dec[px[i11 + 2]];
        a = (w00 * px[i00 + 3] + w10 * px[i10 + 3] + w01 * px[i01 + 3] + w11 * px[i11 + 3]) / 255f;
    }

    static readonly float[] Dec = Enumerable.Range(0, 256).Select(i => GlassLight.Decode(i / 255f)).ToArray();
    const int EncSteps = 16384;
    static readonly byte[] Enc = Enumerable.Range(0, EncSteps + 1).Select(i => (byte)Math.Round(255 * GlassLight.Encode((float)i / EncSteps))).ToArray();
    static byte E(float l) => Enc[(int)(Math.Clamp(l, 0f, 1f) * EncSteps + 0.5f)];
}
