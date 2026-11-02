using SkiaSharp;
using static Sojourn.Session.Assistant.Panel;

namespace Sojourn.Client.Building;

public enum ReadoutKey { Done, Snap, Fine, Local, Undo }

public readonly record struct ReadoutHit(ReadoutKey Key, SKRect Rect);

/// <summary>What building by hand's readout shows, as one snapshot (drawn off the main thread). Pos and Euler are the
/// selected root's place in the region and its turn as the edit floater gives it (null: nothing selected). Snap: its
/// words when on ("grid 0.5 m · turns 15°"). Line: what came of a move, what the handle under the ray does, or what to
/// do here; Strong: the first two (in the text's ink rather than dim).</summary>
public sealed record ReadoutView(string Title, System.Numerics.Vector3? Pos, System.Numerics.Vector3? Euler, string? Snap, bool Fine, bool Local,
    bool CanUndo, string Line, bool Strong, int Hover);

/// <summary>
/// Draws building by hand's readout in the panel's look, as a small sheet of the client's own (Things/ThingCardPainter's
/// manner): the thing's name with × (done) at the top right, where it is and how it's turned in the edit floater's
/// numbers, a line saying what's going on, then four keys in two rows: snap and fine, local axes and undo. Each key a
/// switch lit while on. Nothing in the accent.
/// </summary>
public static class HandBuildPainter
{
    public const float W = 260;
    const float Pad = 14, RowH = 36, Gap = 6;

    public static (byte[] Px, int Width, int Height, float Scale, List<ReadoutHit> Keys) Draw(ReadoutView v,
        Sojourn.Session.Assistant.PanelLook look, float scale, bool layer)
    {
        var k = Ink.For(look);
        var keys = new List<ReadoutHit>();
        var (px, w, h, used) = Rasterise(scale, W, new List<Sojourn.Session.Assistant.PanelKey>(), (c, height) =>
        {
            keys.Clear();
            return Layout(new Painter(c, k, W), v, keys, height);
        }, MaxRasterWidth);
        Sojourn.Session.Assistant.GlassLight.Finish(px, look, layer);
        return (px, w, h, used, keys);
    }

    static float Layout(Painter draw, ReadoutView v, List<ReadoutHit> keys, float height)
    {
        var k = draw.K;
        draw.Body(height);
        bool lit(int i) => v.Hover == i;

        var close = new SKRect(W - 8 - 32, 8, W - 8, 40);
        draw.GlyphKey(close, Painter.Glyph.Close, lit(keys.Count));
        keys.Add(new ReadoutHit(ReadoutKey.Done, close));

        float y = 11;
        var titleF = Font(k.Semi, 16);
        draw.Text(Ellipsize(v.Title, titleF, close.Left - 4 - Pad), Pad, y + 16, titleF, k.Text);
        y = 44;

        var numF = Font(k.Regular, 14);
        var dimF = Font(k.Regular, 13);
        // where it is and how it's turned, in three columns: x, y and z (the region's axes, as every viewer's edit floater
        // labels them; not translated), each angle under its axis, each axis in its gizmo colour
        float col = (W - 2 * Pad) / 3;
        SKColor[] axisInk = [new(0xF2, 0x4D, 0x47), new(0x61, 0xD9, 0x52), new(0x52, 0x8C, 0xFF)];
        if (v.Pos is { } p)
        {
            var axisF = Font(k.Semi, 12.5f);
            float[] at = [p.X, p.Y, p.Z];
            string[] axis = ["x", "y", "z"];
            for (int i = 0; i < 3; i++)
            {
                float cx = Pad + i * col;
                draw.Text(axis[i], cx, y + 14, axisF, axisInk[i]);
                draw.Text(at[i].ToString("0.000", System.Globalization.CultureInfo.InvariantCulture), cx + 12, y + 14, numF, k.Text);
            }
            y += 20;
        }
        if (v.Euler is { } e)
        {
            float[] deg = [e.X, e.Y, e.Z];
            for (int i = 0; i < 3; i++)
                draw.Text(deg[i].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "°", Pad + i * col + 12, y + 14, numF, k.Dim);
            y += 20;
        }
        if (v.Line.Length > 0)
        {
            y += 2;
            foreach (var l in Wrap(v.Line, dimF, W - 2 * Pad, 3)) { draw.Text(l, Pad, y + 14, dimF, v.Strong ? k.Text : k.Dim); y += 18; }
        }
        y += 8;

        float cell = (W - 2 * (Pad - 6) - Gap) / 2;
        int n = 0;
        void Key(ReadoutKey key, string label, bool on, bool enabled = true)
        {
            float x = Pad - 6 + (n % 2) * (cell + Gap);
            var r = new SKRect(x, y, x + cell, y + RowH);
            if (enabled)
            {
                draw.Key(r, label, lit(keys.Count), 15, on ? 1.6f : 1);
                keys.Add(new ReadoutHit(key, r));
            }
            else
            {
                draw.KeyFace(r, Math.Min(r.Height / 2, k.Look.Radius * 0.8f), false, 0.5f);
                var f = Font(k.Regular, 15);
                draw.Text(label, r.MidX - Measure(f, label) / 2, r.MidY + 15 * 0.36f, f, k.Faint);
            }
            if (++n % 2 == 0) y += RowH + Gap;
        }
        Key(ReadoutKey.Snap, Lang.T("snap", "building"), v.Snap != null);
        Key(ReadoutKey.Fine, Lang.T("fine", "building"), v.Fine);
        Key(ReadoutKey.Local, Lang.T("local", "building"), v.Local);
        Key(ReadoutKey.Undo, Lang.T("undo", "building"), false, v.CanUndo);
        y -= Gap;
        if (v.Snap is { } s)
        {
            draw.Text(s, Pad, y + 18, dimF, k.Dim);
            y += 22;
        }
        return y + 10;
    }
}
