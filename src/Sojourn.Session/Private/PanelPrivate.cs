using SkiaSharp;

namespace Sojourn.Session.Assistant;

/// <summary>Dictating words for other people (Private/Dictation.cs): the person's button held (Held), or the words being
/// made out (Hearing).</summary>
public enum PanelDictation { None, Held, Hearing }

public enum DraftTo { Nearby, Im, Group }

/// <summary>Dictated words waiting for the person: where they'd go (a name only for a conversation; this is the
/// person's own screen), and which draft this is (a press on an older one does nothing).</summary>
public sealed record PanelDraft(string Text, DraftTo To, string? Name, long Seq);

/// <summary>Private mode's marks on the panel: a closed eye while it's on (no words), the dictation capsule, and a
/// draft with where it goes, cancel and send.</summary>
public sealed partial class Panel
{
    /// <summary>A draft's keys: <see cref="KeyKind.Draft"/> with Index = seq * 2 + 1 for send, seq * 2 for cancel.</summary>
    public static (long Seq, bool Send) DraftKey(int index) => (index / 2, index % 2 == 1);

    /// <summary>Incognito on: its mark (a hat and glasses) in a small well, centred at (cx, cy).</summary>
    internal static void PrivateMark(Painter draw, float cx, float cy, bool sees, float r = 14)
    {
        if (draw.Canvas is not { } c) return;
        draw.Recess(new SKRect(cx - r, cy - r, cx + r, cy + r), r);
        Incognito(c, cx, cy, r * 0.86f, draw.K.Text, sees);
    }

    /// <summary>Incognito's mark, as the token's glyph in overlay.frag draws it, in a box 2w wide: a hat (crown and brim)
    /// over round glasses, their lenses dark while the assistant is kept blind and open while the person lets it see.</summary>
    static void Incognito(SKCanvas c, float cx, float cy, float w, SKColor ink, bool sees)
    {
        using var fill = new SKPaint { Color = ink, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var line = new SKPaint { Color = ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.2f, w * 0.10f), StrokeCap = SKStrokeCap.Round };
        // y down here, so the shader's y is negated
        c.DrawRoundRect(new SKRect(cx - w * 0.36f, cy - w * 0.52f, cx + w * 0.36f, cy - w * 0.10f), w * 0.09f, w * 0.09f, fill);
        c.DrawRoundRect(new SKRect(cx - w * 0.66f, cy - w * 0.10f, cx + w * 0.66f, cy + w * 0.0f), w * 0.05f, w * 0.05f, fill);
        foreach (var dx in new[] { -0.32f, 0.32f })
        {
            if (sees) c.DrawCircle(cx + dx * w, cy + w * 0.38f, w * 0.22f, line);
            else c.DrawCircle(cx + dx * w, cy + w * 0.38f, w * 0.27f, fill);
        }
        c.DrawLine(cx - w * 0.08f, cy + w * 0.35f, cx + w * 0.08f, cy + w * 0.35f, line);
    }

    /// <summary>Words being written: three lines of text and a caret after the last (blinking while it waits).</summary>
    static void Lines(SKCanvas c, float x, float cy, float w, SKColor ink, bool caret)
    {
        using var p = new SKPaint { Color = ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, StrokeCap = SKStrokeCap.Round };
        float gap = w * 0.3f;
        c.DrawLine(x, cy - gap, x + w, cy - gap, p);
        c.DrawLine(x, cy, x + w * 0.82f, cy, p);
        c.DrawLine(x, cy + gap, x + w * 0.4f, cy + gap, p);
        if (caret) c.DrawLine(x + w * 0.66f, cy + gap - w * 0.24f, x + w * 0.66f, cy + gap + w * 0.12f, p);
    }

    // --- the dictation capsule -----------------------------------------------------------------------------------------

    static float DictationWidth(PanelState s) => 64 + 40 + (s.Private ? 36 : 0);

    static float LayoutDictation(Painter draw, PanelState s, int phase, float height)
    {
        var k = draw.K;
        draw.Body(height);
        var c = draw.Canvas;
        if (s.Dictation == PanelDictation.Hearing) draw.Dots(23, PillH / 2, phase);
        else draw.Orb(32, PillH / 2, phase);
        if (c != null) Lines(c, 62, PillH / 2, 28, k.Text, true);
        if (s.Private) PrivateMark(draw, 64 + 40 + 16, PillH / 2, s.PrivateSees);
        return PillH;
    }

    // --- a draft on the card ---------------------------------------------------------------------------------------------

    /// <summary>The words in full ink; where they'll go (a speech bubble for nearby, an envelope and a name for an IM,
    /// two heads and the group's name); cancel and send.</summary>
    static float LayoutDraft(Painter draw, PanelDraft d, int hover, List<PanelKey> keys, float y, float right)
    {
        var k = draw.K;
        float W = draw.W;
        var f = Font(k.Regular, 18);
        foreach (var l in Wrap(d.Text, f, right - Pad, 10)) { draw.Text(l, Pad, y + 18, f, k.Text); y += 25; }
        y += 8;
        var c = draw.Canvas;
        if (c != null) DestGlyph(c, d.To, Pad + 9, y + 9, k.Dim);
        var wf = Font(k.Regular, 15);
        var where = d.To == DraftTo.Nearby ? Lang.T("nearby") : d.Name ?? "";
        draw.Text(Ellipsize(where, wf, W - 2 * Pad - 28), Pad + 26, y + 14, wf, k.Dim);
        y += 30;
        float half = (W - 2 * Pad - 12) / 2;
        var cancel = new SKRect(Pad, y, Pad + half, y + 50);
        var send = new SKRect(cancel.Right + 12, y, W - Pad, y + 50);
        draw.Key(cancel, Lang.T("cancel"), hover == keys.Count, 16.5f);
        keys.Add(new PanelKey(KeyKind.Draft, (int)(d.Seq * 2), cancel));
        draw.Key(send, Lang.T("send"), hover == keys.Count, 16.5f);
        keys.Add(new PanelKey(KeyKind.Draft, (int)(d.Seq * 2 + 1), send));
        return y + 50 + 10;
    }

    static void DestGlyph(SKCanvas c, DraftTo to, float cx, float cy, SKColor ink)
    {
        using var p = new SKPaint { Color = ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
        switch (to)
        {
            case DraftTo.Im:
            {
                var r = new SKRect(cx - 8, cy - 5.5f, cx + 8, cy + 5.5f);
                c.DrawRoundRect(r, 1.5f, 1.5f, p);
                c.DrawLine(r.Left + 0.5f, r.Top + 0.5f, cx, cy + 1, p);
                c.DrawLine(cx, cy + 1, r.Right - 0.5f, r.Top + 0.5f, p);
                break;
            }
            case DraftTo.Group:
                foreach (var dx in new[] { -4f, 4f })
                {
                    c.DrawCircle(cx + dx, cy - 3, 2.6f, p);
                    using var body = new SKPath();
                    body.AddArc(new SKRect(cx + dx - 5, cy + 1.5f, cx + dx + 5, cy + 10), 180, 180);
                    c.DrawPath(body, p);
                }
                break;
            default:
            {
                // a speech bubble
                using var b = new SKPath();
                b.AddRoundRect(new SKRect(cx - 8, cy - 6.5f, cx + 8, cy + 4), 4, 4);
                c.DrawPath(b, p);
                c.DrawLine(cx - 3, cy + 4, cx - 5, cy + 8, p);
                c.DrawLine(cx - 5, cy + 8, cx + 1, cy + 4, p);
                break;
            }
        }
    }
}
