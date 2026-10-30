using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Xr;
using Sojourn.Render.Realtime;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Social;

/// <summary>
/// Notices waiting for the person, in the headset (the session of 2026-10-27: old ones piled up with no way to see or
/// clear them). A notice shows on the chat feed with its keys for two minutes; after that, while any wait, a small bell
/// sits on the left forearm past the HUD token and the stand key, ringed when some of them aren't on the feed. The right
/// ray and trigger on it bring every waiting notice back onto the feed, where each can be answered or put away, or all
/// cleared at once. Nothing shows when nothing waits.
/// </summary>
public sealed class NoticeBell(SocialView social)
{
    Vector3? _token;   // in the room
    bool _lit, _down, _armed = true;
    int _folded, _waiting;
    long _countedAt;

    /// <summary>This frame the right ray is on the bell (the panel and the world don't get it).</summary>
    public bool RightOn => _lit;

    public void Frame(XrControllers input, VrPilot pilot, bool blocked, bool focused)
    {
        _lit = false;
        // counted a few times a second, not every frame
        if (Environment.TickCount64 - _countedAt > 250 || _down)
        {
            _countedAt = Environment.TickCount64;
            (_waiting, _folded) = social.Waiting;
        }
        _token = _waiting > 0 && !blocked && focused && pilot.Watch(input, along: 0.145f) is { } t ? pilot.RegionToRoom(t) : null;
        if (input.RightTrigger < 0.35f) { _armed = true; _down = false; }
        if (_token is not { } tk || input.RightAim is not { } q || input.RightAimPos is not { } from) return;
        var dir = Vector3.Transform(-Vector3.UnitZ, q);
        float along = Vector3.Dot(tk - from, dir);
        _lit = along > 0 && Vector3.Distance(from + dir * along, tk) < 0.025f;
        if (_lit && _armed && !_down && input.RightTrigger > 0.6f)
        {
            _down = true;
            _armed = false;
            social.Recall();
        }
    }

    /// <summary>The bell on the forearm, in the panel's look; a ring while some waiting notices aren't on the feed.</summary>
    public OverlayScene? Decorate(OverlayScene? scene, VrPilot pilot, Func<PanelLook> look)
    {
        if (_token is not { } tk) return scene;
        var l = look();
        float body = Math.Max(l.TintOpacity, 0.88f);
        var glyphs = new List<OverlayGlyph>(scene?.Glyphs ?? [])
        {
            new(pilot.RoomToRegion(tk), 0.016f, C(_folded > 0 ? l.Accent : l.Tint, _folded > 0 ? 0.96f : body), C(_folded > 0 ? l.OnAccent : l.Ink, 1),
                OverlayGlyphKind.Bell, _lit ? 0.6f : 0),
        };
        return (scene ?? new OverlayScene()) with { Glyphs = glyphs };
    }

    static Vector4 C(string hex, float a)
    {
        var k = SkiaSharp.SKColor.TryParse(hex, out var v) ? v : SkiaSharp.SKColors.Gray;
        return new Vector4(k.Red / 255f, k.Green / 255f, k.Blue / 255f, a);
    }
}
