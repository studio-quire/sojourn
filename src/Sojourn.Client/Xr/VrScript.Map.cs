using System.Numerics;
using Sojourn.Client.Map;

namespace Sojourn.Client.Xr;

/// <summary>
/// SOJOURN_VR_SCRIPT=map: the location line and the two maps against a simulated headset (Map/). The left hand is
/// raised as if to look at a watch (the line shows over the wrist) and the right taps the line (the mini-map opens over
/// it), then the mini-map's globe (the world map comes up); on the world map: + (zooms in), the trigger held on the map
/// and swept (dragged), a spot tapped (picked), the board taken by its top strip and dragged 10 degrees right, × (it
/// goes). Then the wrist again: the mini-map's pop-out key (it goes out on its own), and its put-back key there (back on
/// the hand); done at 21 s. Shots at the steps with SOJOURN_MAP_SHOTS=1 (shots/map-*.png in the base dir).
/// </summary>
public sealed partial class VrScript
{
    bool _map;
    /// <summary>Where the routine aims (Map/WristLine, Map/MapBoard).</summary>
    public MapWiring? Map { get; set; }

    static readonly (double At, string What)[] MapSteps =
    {
        (0, "left hand raised, right hand on the location line"), (1.5, "right trigger: the mini-map opens on the hand"),
        (2.5, "right hand on the mini-map's globe"), (3, "right trigger: the world map comes up"),
        (4, "right hand on +"), (4.5, "right trigger: zoom in"), (5.5, "right hand on the map"), (6, "right trigger held, sweeping left"),
        (8, "released"), (9, "right hand on a spot of the map"), (9.5, "right trigger: picked"), (10.5, "shot"),
        (11, "right hand on the top strip"), (11.5, "right trigger held: dragging the board 10 deg right"), (13.5, "released"),
        (14.5, "shot"), (15, "right hand on ×"), (16, "right trigger: the world map goes"),
        (16.5, "left hand raised, right hand on the mini-map's pop-out key"), (18, "right trigger: out on its own"),
        (19, "right hand on its put-back key"), (19.5, "right trigger: back on the hand"), (21, "done"),
    };

    Vector3? _mapDragFrom;
    bool _mapShot1, _mapShot2, _mapShot3, _mapReset;

    void MapRoutine(XrFrame f, double t)
    {
        int step = Array.FindLastIndex(MapSteps, s => s.At <= t);
        if (step != _said) { _said = step; _log($"map script {t:F1} s: {MapSteps[step].What}"); }
        if (t > 21 || Map is not { } m) return;
        if (!_mapReset)
        {
            // (where the mini-map was is kept between runs: start with both maps away)
            _mapReset = true;
            m.SetMini(MiniHome.Off);
            m.World.Show(false);
        }
        var c = f.Input;
        var head = (f.Eyes[0].Position + f.Eyes[1].Position) * 0.5f;
        var rightFrom = head + new Vector3(0.2f, -0.35f, -0.25f);
        if (t < 3.5 || t >= 16.5)
        {
            c.LeftHandPos = head + new Vector3(-0.06f, -0.12f, -0.34f);   // (higher than the HUD script's: the line shows within 25 degrees of the gaze, ~19 here)
            c.LeftHand = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -90 * MathF.PI / 180) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 20 * MathF.PI / 180);   // the palm (+X) to the eyes
        }
        var board = m.World;
        var mapRect = MapPainter.MapRect;
        Vector3? rightAt = t switch
        {
            < 2.5 => m.Line.Layer() is { } l ? l.Pos : null,
            < 3.5 => m.Mini.KeyPoint(MapKey.World),
            < 5.5 => board.KeyPoint(MapKey.In),
            < 8 => board.RoomPoint(new Vector2(mapRect.MidX + 80 - (float)Math.Clamp((t - 6) / 1.5, 0, 1) * 160, mapRect.MidY)),
            >= 9 and < 11 => board.RoomPoint(new Vector2(mapRect.MidX + 60, mapRect.MidY - 40)),
            >= 11 and < 15 => _mapDragFrom ??= board.RoomPoint(new Vector2(MapPainter.W * 0.6f, 24)),
            >= 15 and < 16.5 => board.KeyPoint(MapKey.Close),
            >= 16.5 and < 19 => m.Mini.KeyPoint(MapKey.PopOut),
            >= 19 => m.Mini.KeyPoint(MapKey.PutBack),
            _ => null,
        };
        if (t is >= 11.5 and < 13.5 && _mapDragFrom is { } d)
        {
            float k = (float)Math.Clamp((t - 11.5) / 1.5, 0, 1) * 10 * MathF.PI / 180;
            rightAt = head + Vector3.Transform(d - head, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -k));
        }
        if (rightAt is { } ra)
        {
            c.RightAimPos = rightFrom;
            c.RightAim = LookRotation(Vector3.Normalize(ra - rightFrom));
        }
        c.RightTrigger = t is >= 1.5 and < 1.8 or >= 3 and < 3.3 or >= 4.5 and < 4.8 or >= 6 and < 8 or >= 9.5 and < 9.8 or >= 11.5 and < 13.5
            or >= 16 and < 16.3 or >= 18 and < 18.3 or >= 19.5 and < 19.8 ? 1 : 0;
        f.Input = c;
        if (Environment.GetEnvironmentVariable("SOJOURN_MAP_SHOTS") == "1")
        {
            if (t >= 2.8 && !_mapShot1 && (_mapShot1 = true)) Shot?.Invoke("map-mini");
            if (t >= 10.5 && !_mapShot2 && (_mapShot2 = true)) Shot?.Invoke("map-board");
            if (t >= 18.8 && !_mapShot3 && (_mapShot3 = true)) Shot?.Invoke("map-mini-free");
        }
    }
}
