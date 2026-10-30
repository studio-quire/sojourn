using Sojourn.Render.Gpu;

namespace Sojourn.Render.Frame;

/// <summary>Owns the device and everything shared across views and regions. Render thread only.</summary>
public sealed class GpuContext : IDisposable
{
    public VulkanContext Vk { get; }
    public Bindless Bindless { get; }
    public ShaderCompiler Shaders { get; }
    public Uploader Uploader { get; }
    public GeometryPool GeometryPool { get; }
    /// <summary>Skinning inputs: bind-pose vertices (48-byte MeshVertex) and per-vertex weights (16-byte SkinWeight).</summary>
    public StoragePool SkinVertices { get; }
    public StoragePool SkinWeights { get; }
    public Luts Luts { get; }
    public Renderer Renderer { get; }
    public FrameResources[] Frames { get; }
    readonly Dictionary<(uint, uint, uint), RenderTargets> _targets = new();
    int _frameIndex;

    public GpuContext(string shaderDir, string cacheDir, bool validation = false, Action<string>? log = null, int framesInFlight = 2)
        : this(shaderDir, cacheDir, new VulkanSetup { Validation = validation, Log = log }, framesInFlight) { }

    public GpuContext(string shaderDir, string cacheDir, VulkanSetup setup, int framesInFlight = 2)
    {
        Vk = new VulkanContext(setup);
        Bindless = new Bindless(Vk);
        Shaders = new ShaderCompiler(Vk, shaderDir, Path.Combine(cacheDir, "spirv"));
        Uploader = new Uploader(Vk);
        GeometryPool = new GeometryPool(Vk, Uploader);
        SkinVertices = new StoragePool(Vk, Uploader, PipelineBuilder.VertexStride);
        SkinWeights = new StoragePool(Vk, Uploader, 16);
        Luts = new Luts(Vk, Bindless, Uploader, shaderDir);
        Uploader.FlushNow();
        Renderer = new Renderer(Vk, Bindless, Shaders, GeometryPool) { SmaaAreaSlot = Luts.SmaaArea, SmaaSearchSlot = Luts.SmaaSearch, GlowNoiseSlot = Luts.TrueNoise };
        Frames = new FrameResources[framesInFlight];
        for (int i = 0; i < framesInFlight; i++) Frames[i] = new FrameResources(Vk);
    }

    public FrameResources NextFrame()
    {
        var fr = Frames[_frameIndex];
        _frameIndex = (_frameIndex + 1) % Frames.Length;
        fr.Wait();
        return fr;
    }

    /// <summary>Render targets for a size, created on demand and kept while used; idle sizes are trimmed by TrimTargets.
    /// Layers 2: a stereo set, both eyes drawn at once (its eyes' sets in <see cref="RenderTargets.Eyes"/>).</summary>
    public RenderTargets Targets(uint w, uint h, uint layers = 1)
    {
        if (!_targets.TryGetValue((w, h, layers), out var rt))
        {
            rt = new RenderTargets(Vk, Bindless, w, h, layers);
            _targets[(w, h, layers)] = rt;
        }
        rt.LastUsedTicks = System.Environment.TickCount64;
        return rt;
    }

    public bool HasTargets(uint w, uint h, uint layers = 1) => _targets.ContainsKey((w, h, layers));

    /// <summary>Free one size's targets now (a still's, Realtime/FrameRenderer.Still.cs; waits for the GPU first).</summary>
    public void ReleaseTargets(uint w, uint h, uint layers = 1)
    {
        if (!_targets.Remove((w, h, layers), out var rt)) return;
        foreach (var f in Frames) f.Wait();
        rt.Dispose();
    }

    /// <summary>Free target sets unused for idleMs (waits for the GPU first).</summary>
    public void TrimTargets(long idleMs)
    {
        long now = System.Environment.TickCount64;
        var stale = _targets.Where(kv => now - kv.Value.LastUsedTicks > idleMs).ToList();
        if (stale.Count == 0) return;
        foreach (var f in Frames) f.Wait();
        foreach (var kv in stale)
        {
            kv.Value.Dispose();
            _targets.Remove(kv.Key);
        }
    }

    public ulong TargetBytes => _targets.Values.Aggregate(0UL, (s, t) => s + t.Bytes);

    public void Dispose()
    {
        foreach (var f in Frames) f.Dispose();
        foreach (var t in _targets.Values) t.Dispose();
        Renderer.Dispose();
        Luts.Dispose();
        GeometryPool.Dispose();
        SkinWeights.Dispose();
        SkinVertices.Dispose();
        Uploader.Dispose();
        Shaders.Dispose();
        Bindless.Dispose();
        Vk.Dispose();
    }
}
