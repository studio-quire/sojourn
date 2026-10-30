using System.Runtime.ExceptionServices;

namespace Sojourn.Render.Realtime;

/// <summary>
/// A few threads that build parts of a frame's draw lists together with the render thread (docs/performance.md >
/// Parallel draw lists): <see cref="Run"/> hands out a batch's items one at a time to whichever thread is free, the
/// caller working too, and returns once all are done. Items know which worker runs them (0 is the caller), for scratch
/// kept per worker. Threads above normal priority, as the render thread: a frame waits for them.
/// </summary>
public sealed class SceneWorkers : IDisposable
{
    sealed class Batch(Action<int, int> body, int count)
    {
        public readonly Action<int, int> Body = body;
        public readonly int Count = count;
        public int Next, Left = count;
        public ExceptionDispatchInfo? Failed;
        public readonly ManualResetEventSlim Done = new(false);
    }

    readonly Thread[] _threads;
    readonly object _gate = new();
    Batch? _batch;   // the batch being worked; each has its own counters, so a helper late from the last one finds nothing
    bool _stop;

    /// <summary>Workers besides the caller (0: Run does everything on the caller).</summary>
    public int Helpers => _threads.Length;
    public int Workers => _threads.Length + 1;

    public SceneWorkers(int helpers)
    {
        _threads = new Thread[Math.Max(0, helpers)];
        for (int i = 0; i < _threads.Length; i++)
        {
            int index = i + 1;
            _threads[i] = new Thread(() => Loop(index)) { Name = $"scene {index}", IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _threads[i].Start();
        }
    }

    /// <summary>Run body(item, worker) for items 0..count-1 across the workers; returns when every item is done, and
    /// throws the first failure.</summary>
    public void Run(int count, Action<int, int> body)
    {
        if (count <= 0) return;
        if (_threads.Length == 0 || count == 1)
        {
            for (int i = 0; i < count; i++) body(i, 0);
            return;
        }
        var b = new Batch(body, count);
        lock (_gate)
        {
            _batch = b;
            Monitor.PulseAll(_gate);
        }
        Work(b, 0);
        b.Done.Wait();
        lock (_gate) if (ReferenceEquals(_batch, b)) _batch = null;
        b.Done.Dispose();
        b.Failed?.Throw();
    }

    void Loop(int worker)
    {
        Batch? seen = null;
        while (true)
        {
            Batch b;
            lock (_gate)
            {
                while ((_batch == null || ReferenceEquals(_batch, seen)) && !_stop) Monitor.Wait(_gate);
                if (_stop) return;
                b = seen = _batch!;
            }
            Work(b, worker);
        }
    }

    static void Work(Batch b, int worker)
    {
        while (true)
        {
            int i = Interlocked.Increment(ref b.Next) - 1;
            if (i >= b.Count) return;
            try { b.Body(i, worker); }
            catch (Exception e) { Interlocked.CompareExchange(ref b.Failed, ExceptionDispatchInfo.Capture(e), null); }
            if (Interlocked.Decrement(ref b.Left) == 0) b.Done.Set();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stop = true;
            Monitor.PulseAll(_gate);
        }
        foreach (var t in _threads) t.Join(1000);
    }
}
