using LibreMetaverse;
using Sojourn.Client.Social;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Vector2 = System.Numerics.Vector2;

namespace Sojourn.Client.Hud;

/// <summary>
/// Floating text on HUD prims (llSetText: titlers, RP meters, status lines, a HUD's own labels), as the viewer draws it
/// (llhudtext.cpp, mOnHUDAttachment): always shown (the hover text switch is for the world's), never faded or hidden
/// by the HUDs, at a fixed size in screen pixels, its bottom 0.6 of the prim's height above its middle, lines growing
/// upward, centred, in the script's colour and opacity with a dark edge. Pictures are the world's floating text's
/// (<see cref="FloatingText.Draw"/>), cached by their words, colour and size; the renderer lays them over the HUDs in
/// every image of them (the desktop's screen, the headset's board, the assistant's pictures). On the board the text is
/// drawn a third larger than on a monitor, for the headset's coarser pixels.
/// </summary>
public sealed class HudText
{
    readonly AgentSession _s;
    readonly Dictionary<(FloatingText.Text Text, byte Alpha, float Scale), (OverlayImage Pic, long Used)> _pics = new();
    long _frame, _version;

    public HudText(AgentSession session) => _s = session;

    /// <summary>The labels for the HUDs the last frame placed, for a screen <paramref name="screenHeight"/> pixels
    /// tall (the picture's pixels are the screen's), text drawn at <paramref name="scale"/>.</summary>
    public IReadOnlyList<HudLabel>? Labels(HudsPlaced placed, int screenHeight, float scale)
    {
        if (placed.Objects.Count == 0 || _s.Region is not { } w || w.Handle != placed.Region) return null;
        _frame++;
        List<HudLabel>? list = null;
        float unit = 1f / Math.Max(screenHeight, 1);
        foreach (var hud in placed.Objects)
            foreach (var part in hud.Parts)
            {
                if (w.PrimById(part.Id) is not { } p || string.IsNullOrWhiteSpace(p.Text) || p.TextColor.A < 0.01f) continue;
                var pic = Picture(FloatingText.Key(p.Text, p.TextColor), p.TextColor.A, scale);
                if (pic == null) continue;
                var mid = (part.Min + part.Max) / 2;
                float bottom = mid.Z + p.Scale.Z * 0.6f;
                var size = new Vector2(pic.Width * unit, pic.Height * unit);
                (list ??= new()).Add(new HudLabel(pic, new Vector2(mid.Y, bottom + size.Y / 2), size));
            }
        if (_pics.Count > 64)
            foreach (var k in _pics.Where(kv => kv.Value.Used < _frame - 300).Select(kv => kv.Key).ToList()) _pics.Remove(k);
        return list;
    }

    OverlayImage? Picture(FloatingText.Text text, float alpha, float scale)
    {
        if (text.Lines.Length == 0) return null;
        byte a = (byte)Math.Clamp((int)MathF.Round(alpha * 255), 0, 255);
        var key = (text, a, scale);
        if (_pics.TryGetValue(key, out var have))
        {
            _pics[key] = (have.Pic, _frame);
            return have.Pic;
        }
        var drawn = FloatingText.Draw(text, scale);
        var rgba = drawn.Rgba;
        if (a < 255)
            for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(rgba[i] * a / 255);   // premultiplied: every channel
        var pic = new OverlayImage(rgba, drawn.W, drawn.H, Interlocked.Increment(ref _version));
        _pics[key] = (pic, _frame);
        return pic;
    }
}
