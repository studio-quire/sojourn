using System.Numerics;

namespace Sojourn.Render.Environment;

/// <summary>
/// What the viewer's LLEnvironment does over time that a single evaluation doesn't (render thread, once a frame):
/// - Cross-fades (LLEnvironment::DayTransition, LLTrackBlenderLoopingManual::switchTrack): when what's shown changes
///   to another day or fixed setting (entering a parcel with its own sky, the region's changing, an altitude band's
///   track), the sky and water blend from what was last shown to the new one's live values: 5 s (TRANSITION_DEFAULT and
///   TRANSITION_ALTITUDE), 1 s after a teleport (TRANSITION_FAST). The person's own changes are instant, as the
///   viewer's menu and My Environments make them.
/// - Clouds' scroll (LLEnvironment::updateCloudScroll): ours comes from the time of day (exact across sessions) where
///   the viewer's accumulates while it runs; kept continuous here across changes of what's shown, held still while
///   paused (Pause Clouds), carrying on from there after, and zero while the scroll rate is exactly zero, as the
///   viewer's.
/// Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class EnvironmentFader
{
    public const double DefaultSeconds = 5, FastSeconds = 1, AltitudeSeconds = 5;

    bool _started;
    EnvironmentLayer? _skyLayer, _waterLayer;
    int _track;
    SkySettings? _lastSky;
    WaterSettings? _lastWater;
    SkySettings? _fromSky;
    WaterSettings? _fromWater;
    double _fadeStart, _fadeLength;
    Vector2 _offset, _shownScroll;
    bool _paused, _rebase;

    /// <summary>Whether a cross-fade is under way.</summary>
    public bool Fading { get; private set; }

    /// <param name="now">Seconds on a steady clock.</param>
    /// <param name="instant">The person changed their own environment this frame: no fade.</param>
    /// <param name="teleported">The view jumped (a teleport): a fade, if any, is the fast one.</param>
    public EnvironmentSnapshot Next(EnvironmentSnapshot snap, double now, bool instant, bool teleported, bool cloudsPaused)
    {
        bool layerChanged = !ReferenceEquals(snap.SkyLayer, _skyLayer) || !ReferenceEquals(snap.WaterLayer, _waterLayer);
        bool trackChanged = snap.SkyTrack != _track && ReferenceEquals(snap.SkyLayer, _skyLayer);
        if (_started && (layerChanged || trackChanged))
        {
            if (instant || _lastSky == null || _lastWater == null) Fading = false;
            else
            {
                (_fromSky, _fromWater) = (_lastSky, _lastWater);
                _fadeStart = now;
                _fadeLength = layerChanged ? (teleported ? FastSeconds : DefaultSeconds) : AltitudeSeconds;
                Fading = true;
            }
            _rebase = true;   // the clouds carry on from where they are
        }
        (_skyLayer, _waterLayer, _track, _started) = (snap.SkyLayer, snap.WaterLayer, snap.SkyTrack, true);

        var sky = snap.Sky;
        var water = snap.Water;
        if (Fading)
        {
            float t = (float)((now - _fadeStart) / _fadeLength);
            if (t >= 1f) Fading = false;
            else
            {
                sky = SkySettings.Blend(_fromSky!, snap.Sky, t);
                water = WaterSettings.Blend(_fromWater!, snap.Water, t);
            }
        }
        (_lastSky, _lastWater) = (sky, water);

        // clouds
        var raw = snap.CloudScrollDelta;
        Vector2 scroll;
        if (sky.CloudScrollRate == Vector2.Zero) { scroll = Vector2.Zero; _rebase = true; }
        else if (cloudsPaused) { scroll = _shownScroll; _paused = true; }
        else
        {
            if (_paused || _rebase) { _offset = _shownScroll - raw; (_paused, _rebase) = (false, false); }
            scroll = Wrap(raw + _offset);
        }
        _shownScroll = scroll;

        if (ReferenceEquals(sky, snap.Sky) && ReferenceEquals(water, snap.Water) && scroll == raw) return snap;
        return new EnvironmentSnapshot
        {
            Sky = sky, Water = water,
            SkySource = snap.SkySource, WaterSource = snap.WaterSource, SkyLayer = snap.SkyLayer, WaterLayer = snap.WaterLayer,
            RequestedSkyTrack = snap.RequestedSkyTrack, SkyTrack = snap.SkyTrack, SkySample = snap.SkySample, WaterSample = snap.WaterSample,
            CloudScrollDelta = scroll, Utc = snap.Utc, UnixSeconds = snap.UnixSeconds,
        };
    }

    static Vector2 Wrap(Vector2 v) => new(v.X - MathF.Floor(v.X), v.Y - MathF.Floor(v.Y));
}
