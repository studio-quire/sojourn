using LibreMetaverse;
using Sojourn.Client.Assistant;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Control;

/// <summary>
/// Standing up in the headset without the assistant (the user's session of 2026-10-26: seated in a chair, there was
/// no way to stand but asking it). While seated, a small key with an up arrow sits on the left forearm, past the HUD
/// token; the right ray and trigger on it stand you up, as the viewer's Stand button does. (Pushing the left stick
/// stands you up too, when what you sit on hasn't taken the movement keys: VrPilot.Seated.cs.)
/// </summary>
public sealed class StandKey
{
    readonly AgentSession _s;
    Vector3? _token;   // in the room
    bool _lit, _down, _armed = true;

    public StandKey(AgentSession s) => _s = s;

    /// <summary>This frame the right ray is on the key (the panel and the world don't get it).</summary>
    public bool RightOn => _lit;

    public void Frame(XrControllers input, VrPilot pilot, bool blocked, bool focused)
    {
        _lit = false;
        bool seated = _s.Client is { Network.Connected: true } c && c.Self.SittingOn != 0;
        _token = seated && !blocked && focused && pilot.Watch(input, along: 0.095f) is { } t ? pilot.RegionToRoom(t) : null;
        if (input.RightTrigger < 0.35f) { _armed = true; _down = false; }
        if (_token is not { } tk || input.RightAim is not { } q || input.RightAimPos is not { } from) return;
        var dir = Vector3.Transform(-Vector3.UnitZ, q);
        float along = Vector3.Dot(tk - from, dir);
        _lit = along > 0 && Vector3.Distance(from + dir * along, tk) < 0.025f;
        if (_lit && _armed && !_down && input.RightTrigger > 0.6f)
        {
            _down = true;
            _armed = false;
            _s.Client!.Self.Stand();
        }
    }

    /// <summary>The key on the forearm, in the panel's look.</summary>
    public OverlayScene? Decorate(OverlayScene? scene, VrPilot pilot, Func<PanelLook> look)
    {
        if (_token is not { } tk) return scene;
        var l = look();
        var glyphs = new List<OverlayGlyph>(scene?.Glyphs ?? [])
        {
            new(pilot.RoomToRegion(tk), 0.016f, C(l.Tint, Math.Max(l.TintOpacity, 0.88f)), C(l.Ink, 1), OverlayGlyphKind.Stand, _lit ? 0.6f : 0),
        };
        return (scene ?? new OverlayScene()) with { Glyphs = glyphs };
    }

    static Vector4 C(string hex, float a)
    {
        var k = SkiaSharp.SKColor.TryParse(hex, out var v) ? v : SkiaSharp.SKColors.Gray;
        return new Vector4(k.Red / 255f, k.Green / 255f, k.Blue / 255f, a);
    }
}
