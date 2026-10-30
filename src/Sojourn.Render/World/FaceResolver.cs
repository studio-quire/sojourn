using System.Collections.Concurrent;
using LibreMetaverse;
using Sojourn.Render.Geometry;

namespace Sojourn.Render.World;

/// <summary>
/// Objects' faces resolved off the frame (docs/performance.md > Streaming off the render thread): an object whose
/// faces need resolving (new, or a texture or material of theirs arrived) is handed here while the frame draws its old
/// faces, or nothing yet; a resolver thread works out the faces' materials and texture slots into a FaceCache, and the
/// next frame to draw the object takes it (RegionScene.DrawObject). Near objects never drawn, and ones whose old faces
/// would outlive the slots they name, are still resolved in the frame. The nearest go first.
/// Texture slots and states change only on the render thread, in the frame's uploads, shrinks and evictions, which hold
/// <see cref="Slots"/> for writing; a resolve holds it for reading, so a cache never records a slot that was being
/// replaced as it was read.
/// </summary>
public sealed class FaceResolver : IDisposable
{
    readonly Thread[] _threads;
    readonly ConcurrentQueue<ResolveJob> _near = new(), _far = new();
    readonly SemaphoreSlim _work = new(0);
    volatile bool _stop;
    /// <summary>Held for writing while texture slots and states change (RenderWorld.BeginFrame, Housekeeping).</summary>
    public readonly ReaderWriterLockSlim Slots = new(LockRecursionPolicy.NoRecursion);
    long _done;

    public int Queued => _near.Count + _far.Count;
    /// <summary>Resolves finished so far.</summary>
    public long Done => Interlocked.Read(ref _done);

    public FaceResolver(int threads)
    {
        _threads = new Thread[threads];
        for (int i = 0; i < threads; i++)
        {
            _threads[i] = new Thread(Loop) { Name = $"faces {i + 1}", IsBackground = true };
            _threads[i].Start();
        }
    }

    internal void Enqueue(ResolveJob job)
    {
        (job.Distance < 32f ? _near : _far).Enqueue(job);
        _work.Release();
    }

    void Loop()
    {
        while (!_stop)
        {
            _work.Wait();
            if (_stop) return;
            if (!_near.TryDequeue(out var job) && !_far.TryDequeue(out job)) continue;
            Slots.EnterReadLock();
            try { job.Result = job.Scene.ResolveOff(job); }
            catch { job.Result = null; }   // resolved in the frame instead
            finally { Slots.ExitReadLock(); }
            job.Done = true;
            Interlocked.Increment(ref _done);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _work.Release(_threads.Length);
        foreach (var t in _threads) t.Join(1000);
    }
}

/// <summary>One object's faces to resolve off the frame, with what they're resolved from (a result for other inputs is
/// dropped: <see cref="Fits"/>).</summary>
internal sealed class ResolveJob(RegionScene scene, SceneObject obj, ShapeGeometry shape, uint owner, int bakeKey, UUID[]? renderMaterials,
    object? overrides, float area, float distance, IAssetSource assets, Simulator sim)
{
    public readonly RegionScene Scene = scene;
    public readonly SceneObject Obj = obj;
    public readonly ShapeGeometry Shape = shape;
    public readonly uint Owner = owner;
    public readonly int BakeKey = bakeKey;
    public readonly UUID[]? RenderMaterials = renderMaterials;
    public readonly object? Overrides = overrides;
    public readonly float Area = area, Distance = distance;
    public readonly IAssetSource Assets = assets;
    public readonly Simulator Sim = sim;
    public FaceCache? Result;
    public volatile bool Done;

    public bool Fits(ShapeGeometry shape, int bakeKey, UUID[]? renderMaterials, object? overrides) =>
        ReferenceEquals(shape, Shape) && bakeKey == BakeKey && ReferenceEquals(renderMaterials, RenderMaterials) && ReferenceEquals(overrides, Overrides);
}
