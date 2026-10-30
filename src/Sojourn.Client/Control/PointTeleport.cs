using Sojourn.Client.Xr;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Control;

/// <summary>
/// The left trigger as teleport (the user's wish, 2026-10-26: the left trigger could also teleport you to where you
/// point). The left hand has nothing else to do since the pointer moved to the right hand (touching and sitting are
/// the pointer's trigger), so: pulled, a beam comes from the left hand; on open ground (the terrain, or the top of
/// something you could stand on that doesn't take touches or seat you and nobody wears) a ring fills on the spot over
/// 0.6 s, and letting go once it's full teleports you there. Let go sooner, or with the beam anywhere else, and nothing
/// happens, so a pull never jumps you by accident; what takes touches or seats you is left to the pointer. Never when
/// a script has taken the left button, nor while a confirmation or the menu is up. It's the viewer's double-click
/// teleport, its landing rules included (a parcel's landing point still wins), so nothing the official viewer can't do
/// (docs/tpv-policy.md, 2.k). The point_teleport setting turns it off.
/// </summary>
public sealed class PointTeleport(VrSettings settings, FrameRenderer renderer, DirectTouch touch)
{
    const double HoldSeconds = 0.6;
    const float Reach = 96;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    double _since = -1;
    bool _armed = true;
    (Vector3 Point, ulong Region)? _target;

    double Held => _since < 0 ? 0 : _clock.Elapsed.TotalSeconds - _since;

    static bool OpenGround(PickHit? hit) => hit is { Kind: PickKind.Ground } ||
        hit is { Kind: PickKind.Object, Surface: { } s } h && h.WornBy == LibreMetaverse.UUID.Zero && s.Normal.Z >= 0.6f;

    /// <summary>Each headset frame, with the left hand as the controllers report it.</summary>
    public OverlayScene? Headset(OverlayScene? scene, XrControllers input, VrPilot pilot, uint selfLocalId, bool blocked, bool focused)
    {
        float trigger = input.LeftTrigger;
        if (!settings.PointTeleport || !focused || blocked || touch.ButtonTaken(false) || input.LeftAim is not { } q
            || input.LeftAimPos is not { } at || pilot.Region == 0)
        {
            _since = -1;
            _target = null;
            if (trigger > 0.35f) _armed = false;   // a pull that couldn't count waits for a release
            else _armed = true;
            return scene;
        }
        var origin = pilot.RoomToRegion(at);
        var dir = pilot.RoomDirToRegion(Vector3.Transform(-Vector3.UnitZ, q));
        if (trigger < 0.35f)
        {
            if (_since >= 0 && Held >= HoldSeconds && _target is { } t) pilot.TeleportToPoint(t.Point, t.Region);
            _since = -1;
            _target = null;
            _armed = true;
            return scene;
        }
        if (trigger > 0.6f && _since < 0 && _armed) { _since = _clock.Elapsed.TotalSeconds; _armed = false; }
        if (_since < 0) return scene;
        var hit = renderer.Pick(pilot.Region, origin, dir, Reach, selfLocalId);
        // things that take touches count too: a script in a linkset's root makes every floor of a house touchable, and
        // only terrain was left to land on (the user, 2026-10-27). Touching is the right trigger's, so they never compete.
        _target = OpenGround(hit) ? (hit!.Value.Point, hit.Value.Region) : null;
        return Cue(scene, origin, dir, hit);
    }

    static Vector4 Ink => Sojourn.Session.Assistant.PanelLook.InUse.Light(0.9f);

    /// <summary>The beam, and on open ground the ring on the spot, filling over the hold (full and brighter once letting
    /// go will teleport).</summary>
    OverlayScene? Cue(OverlayScene? scene, Vector3 origin, Vector3 dir, PickHit? hit)
    {
        var beams = new List<OverlayBeam>(scene?.Beams ?? []);
        float d = hit?.Distance ?? 6;
        beams.Add(new OverlayBeam(origin + dir * 0.04f, origin + dir * (_target != null ? d : MathF.Min(d, 6)), 0.003f, Ink with { W = 0.55f }));
        if (_target is { } t)
        {
            float fill = (float)Math.Clamp(Held / HoldSeconds, 0, 1);
            bool ready = Held >= HoldSeconds;
            const int n = 32;
            float radius = Math.Clamp(0.012f * d, 0.3f, 1.2f);
            var c = t.Point + new Vector3(0, 0, 0.03f);
            int lit = (int)MathF.Ceiling(fill * n);
            for (int i = 0; i < n; i++)
            {
                float a0 = i * MathF.Tau / n, a1 = (i + 1) * MathF.Tau / n;
                var p0 = c + new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0) * radius;
                var p1 = c + new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0) * radius;
                float w = radius * (ready ? 0.06f : 0.04f);
                beams.Add(new OverlayBeam(p0, p1, w, i < lit ? Ink with { W = ready ? 0.95f : 0.7f } : Ink with { W = 0.18f }));
            }
        }
        return (scene ?? new OverlayScene()) with { Beams = beams };
    }
}
