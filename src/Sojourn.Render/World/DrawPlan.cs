using Sojourn.Render.Frame;
using Sojourn.Render.Realtime;
using Sojourn.Render.Shadows;

namespace Sojourn.Render.World;

/// <summary>
/// A view's draw list built in parts on several threads (docs/performance.md > Parallel draw lists). Each scene the
/// view draws adds its parts in the order a single build would draw them (RegionScene.Plan): its lights, its columns of
/// world objects in runs of about <see cref="PartObjects"/>, its wearers' attachments, then its terrain, water and sky.
/// <see cref="Run"/> draws the parts on the scene workers, each into a segment of its own with its own context, the
/// terrain parts after them on the calling thread (terrain keeps state of its own), and appends the segments to the
/// view's list in part order: the same draws in the same order as one thread would make them. Render thread.
/// </summary>
public sealed class DrawPlan
{
    /// <summary>About how many objects a part draws (a column is never split).</summary>
    public static int PartObjects = 384;

    internal enum PartKind { Lights, Columns, Attachments, Terrain }

    internal sealed class Part
    {
        public PartKind Kind;
        public RegionScene Scene = null!;
        public DrawCtx Ctx = null!;
        public readonly List<(SceneCell Cell, int Casters, bool InView)> Cells = new();
        public readonly List<uint> Owners = new();
        public bool Water;
    }

    readonly List<Part> _parts = new();
    readonly List<Part> _parallel = new();
    readonly List<Part> _pool = new();
    readonly List<DrawList> _segments = new();
    int _usedParts, _usedSegments;

    public int Parts => _parts.Count;

    internal Part NewPart(PartKind kind, RegionScene scene)
    {
        if (_usedParts == _pool.Count) _pool.Add(new Part());
        var p = _pool[_usedParts++];
        p.Kind = kind;
        p.Scene = scene;
        p.Cells.Clear();
        p.Owners.Clear();
        p.Water = false;
        _parts.Add(p);
        return p;
    }

    /// <summary>A cleared segment to draw a part into (the view's flags carried over).</summary>
    internal DrawList Segment(DrawList view)
    {
        if (_usedSegments == _segments.Count) _segments.Add(new DrawList());
        var s = _segments[_usedSegments++];
        s.Clear();
        s.MainView = view.MainView;
        return s;
    }

    /// <summary>Draw every part (the workers taking the scenes' objects, this thread the terrain) and append them to
    /// <paramref name="target"/> in order. Returns the scenes' stats summed; the plan is empty again after.</summary>
    public RegionDrawStats Run(SceneWorkers? workers, DrawList target, RenderWorld world)
    {
        try
        {
            _parallel.Clear();
            foreach (var p in _parts) if (p.Kind != PartKind.Terrain) _parallel.Add(p);
            if (workers != null) workers.Run(_parallel.Count, (i, _) => Draw(_parallel[i]));
            else foreach (var p in _parallel) Draw(p);
            foreach (var p in _parts) if (p.Kind == PartKind.Terrain) Draw(p);
            var stats = new RegionDrawStats();
            foreach (var p in _parts)
            {
                target.Append(p.Ctx.Dl);
                p.Ctx.Finish(world);
                stats.Objects += p.Ctx.Stats.Objects;
                stats.Culled += p.Ctx.Stats.Culled;
                stats.Pending += p.Ctx.Stats.Pending;
            }
            return stats;
        }
        finally
        {
            // the pooled parts let go of what they drew: a part kept its scene (and through it the region's objects,
            // geometry and world model) until a later frame used as many parts, so a frame of many parts (a wide draw
            // distance, a big place) kept regions long left alive for good (2026-10-28: 4 of 9 scenes in a gcdump were
            // left regions held only here, 27k objects)
            for (int i = 0; i < _usedParts; i++)
            {
                var p = _pool[i];
                p.Scene = null!;
                p.Ctx = null!;
                p.Cells.Clear();
                p.Owners.Clear();
            }
            _parts.Clear();
            _parallel.Clear();
            _usedParts = _usedSegments = 0;
        }
    }

    static void Draw(Part p) => p.Scene.DrawPart(p);
}
