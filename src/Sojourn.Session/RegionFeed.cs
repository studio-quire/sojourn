using Sojourn.Core.World;
using Sojourn.Render.Integration;
using Sojourn.Render.Realtime;

namespace Sojourn.Session;

/// <summary>
/// Regions reach a renderer as the session's world model learns them (neighbours too: it draws across borders), each
/// through a <see cref="WorldRegionSource"/>; a region the world lets go of is taken out of the renderer. The VR client
/// and the offscreen renderer (Offscreen/OffscreenRenderer.cs) both feed theirs this way.
///
/// A region the view is still in when the session lets it go stays drawn as it was until the view leaves it
/// (<see cref="ViewRegion"/>, <see cref="LetGoOfKept"/>): after a teleport to another region the old one can be let go
/// before the new one has taken the agent in, while the headset's view waits there for the arrival (VrPilot's Arrival);
/// dropped at once, the eyes would show nothing.
/// </summary>
public sealed class RegionFeed : IDisposable
{
    readonly WorldHub _world;
    readonly Dictionary<ulong, WorldRegionSource> _sources = new();
    readonly HashSet<ulong> _kept = new();
    FrameRenderer _renderer;

    /// <summary>The region the view is in (0: none kept for it).</summary>
    public Func<ulong> ViewRegion { get; set; } = () => 0;

    /// <summary>Starts feeding <paramref name="renderer"/>: the regions the world already knows, then each as it comes and goes.</summary>
    public RegionFeed(WorldHub world, FrameRenderer renderer)
    {
        _world = world;
        _renderer = renderer;
        world.RegionChanged += OnRegion;
        foreach (var r in world.Regions) OnRegion(r, true);
    }

    void OnRegion(RegionWorld world, bool created)
    {
        lock (_sources)
        {
            if (world.Handle == 0) return;
            if (created)
            {
                if (_sources.TryGetValue(world.Handle, out var have) && ReferenceEquals(have.World, world)) return;
                have?.Dispose();
                var src = new WorldRegionSource(_world, world);
                _sources[world.Handle] = src;
                _renderer.AddRegion(src.Source);
            }
            else if (_sources.TryGetValue(world.Handle, out var src) && ReferenceEquals(src.World, world))
            {
                _sources.Remove(world.Handle);
                src.Dispose();
                if (world.Handle == ViewRegion()) _kept.Add(world.Handle);
                else Remove(world.Handle);
            }
        }
    }

    void Remove(ulong handle)
    {
        _renderer.RemoveRegion(handle);
        Sojourn.Render.Particles.ParticleSystemTracker.Shared.ForgetRegion(handle);
    }

    /// <summary>Regions kept for the view that it has left (each frame, before the view's region is used).</summary>
    public void LetGoOfKept()
    {
        lock (_sources)
        {
            if (_kept.Count == 0) return;
            ulong here = ViewRegion();
            _kept.RemoveWhere(h =>
            {
                if (h == here) return false;
                if (!_sources.ContainsKey(h)) Remove(h);   // (unless it has joined again since)
                return true;
            });
        }
    }

    /// <summary>Feed a new renderer instead (the old one's GPU was lost): every region the world knows, from fresh
    /// sources (the old ones' change queues went to the old renderer).</summary>
    public void Switch(FrameRenderer renderer)
    {
        lock (_sources)
        {
            foreach (var s in _sources.Values) s.Dispose();
            _sources.Clear();
            _kept.Clear();
            _renderer = renderer;
            foreach (var r in _world.Regions) OnRegion(r, true);
        }
    }

    public void Dispose()
    {
        _world.RegionChanged -= OnRegion;
        lock (_sources)
        {
            foreach (var s in _sources.Values) s.Dispose();
            _sources.Clear();
        }
    }
}
