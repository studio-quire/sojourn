using System.Diagnostics;
using System.Numerics;
using Sojourn.Render.Environment;
using Sojourn.Render.World;

namespace Sojourn.Render.Realtime;

/// <summary>The sky the last frame was drawn with, as the environment gave it and as it became uniforms: for telling a
/// place's own look from ours (SojournEnvironment get). Replaced whole each frame, so a reader on another thread sees
/// one frame's values. Claude Opus 5.5, 2026-10-28.</summary>
public sealed record EnvironmentSeen(EnvironmentSnapshot Snapshot, SkyUniforms Sky, Vector3 Position, ulong Region, int Parcel);

public sealed partial class FrameRenderer
{
    public EnvironmentSeen? LastEnvironment { get; private set; }

    /// <summary>The place's own environment at a height now: the region's and parcel's, never the person's own or a
    /// frame's LocalSky (which <see cref="LastEnvironment"/> may hold). Null until the region's has arrived. Render thread.</summary>
    public EnvironmentSnapshot? PlaceEnvironment(ulong regionHandle, double altitude)
    {
        CheckWorld();
        return _world.Environment(regionHandle) is { Loaded: true } env ? env.Evaluate(DateTime.UtcNow, altitude) : null;
    }

    /// <summary>The person's own environment over every place's (SojournEnvironment; the `sky` setting's fixed skies
    /// come through the request's LocalSky and are applied here when it changes).</summary>
    public PersonalEnvironment Personal { get; } = new();

    /// <summary>Property and ban lines' settings and input (World/ParcelLines.cs; thread-safe).</summary>
    public ParcelLines ParcelLines => _world.ParcelLines;

    readonly EnvironmentFader _fader = new();
    RegionEnvironment? _shownEnv;
    bool _presetSeen;
    LibreMetaverse.UUID? _preset;
    long _personalVersion, _presetVersion = -1;
    Vector2? _lastGlobal;   // the view's place last frame on the grid (x, y): a jump is a teleport
    double _jumpedAt = double.NegativeInfinity;
    static readonly double TickSeconds = 1.0 / Stopwatch.Frequency;

    /// <summary>The environment this frame is drawn under: the region's and parcel's under the person's own, faded from
    /// what was shown as the viewer fades (Environment/EnvironmentFader.cs). A region whose environment hasn't arrived
    /// yet shows the last one meanwhile, as the viewer keeps its region environment until the new one is recorded.</summary>
    EnvironmentSnapshot EnvironmentFor(FrameRequest req, RegionEnvironment? env, DateTime time, out long localKey)
    {
        // a still is drawn under the sky the person sees, nothing of the fade stepped (FrameRenderer.Still.cs)
        if (_stillEnv && LastEnvironment is { } seen) { localKey = _lastLocalKey; return seen.Snapshot; }
        bool presetOn = false;
        if (!_presetSeen || req.LocalSky != _preset)
        {
            if (req.LocalSky is { } id && FixedSky(id) is { } sky) { Personal.ApplySky(sky, "preset"); presetOn = true; }
            else if (_presetSeen) Personal.Clear();
            (_presetSeen, _preset) = (true, req.LocalSky);
        }
        if (env is { Loaded: true }) _shownEnv = env;
        var source = env is { Loaded: false } && _shownEnv != null ? _shownEnv : env;

        var (local, version) = Personal.Update(LastEnvironment?.Snapshot, source?.State);
        bool changed = version != _personalVersion;
        _personalVersion = version;
        if (presetOn && Personal.Current is { From: "preset" }) _presetVersion = version;   // (one change applies a frame: a person's own change in the same frame isn't the preset)
        // a preset as put on keys what's captured under it (reflection probes) by itself rather than by each time it's put
        // on: pictures that put the midday sky on and take it off again (Sojourn Access's of people and things) find what
        // it had, rather than capturing the default probe anew each time (0.12 s on the studio PC's processor, where
        // those pictures took 0.06-0.2 s, 2026-11-11)
        localKey = _lastLocalKey = local == null ? 0 : _preset is { } p && version == _presetVersion ? -1L - (uint)p.GetHashCode() : version;

        var snap = source != null ? source.Evaluate(time, req.Position.Z, local) : new EnvironmentState().Evaluate(time, req.Position.Z, local);

        var global = new Vector2((uint)(req.RegionHandle >> 32), (uint)req.RegionHandle) + new Vector2(req.Position.X, req.Position.Y);
        double now = Stopwatch.GetTimestamp() * TickSeconds;
        if (_lastGlobal is { } last && Vector2.Distance(last, global) > 64f) _jumpedAt = now;
        _lastGlobal = global;
        // the new place's environment arrives a moment after a teleport: its fade is the fast one (TRANSITION_FAST)
        return _fader.Next(snap, now, changed, now - _jumpedAt < 10, Personal.CloudsPaused);
    }
}
