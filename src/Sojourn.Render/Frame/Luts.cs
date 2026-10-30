using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Frame;

/// <summary>
/// Fixed bindless slots created once at startup: 0 white, 1 black, 2 flat normal, 3 default grey,
/// 4 the GGX BRDF LUT (viewer genbrdflutF), 5 the legacy specular lookup (viewer LLPipeline::createLUTBuffers),
/// 6 the SSAO noise map; then the SMAA area and search textures (slots in <see cref="SmaaArea"/>, <see cref="SmaaSearch"/>).
/// Shaders hardcode these slot numbers (pbr.glsl).
/// </summary>
public sealed class Luts : IDisposable
{
    readonly List<GpuImage> _images = new();

    public Luts(VulkanContext vk, Bindless bindless, Uploader up, string shaderDir)
    {
        Solid(vk, bindless, up, 255, 255, 255, 255, Bindless.White);
        Solid(vk, bindless, up, 0, 0, 0, 255, Bindless.Black);
        Solid(vk, bindless, up, 128, 128, 255, 255, Bindless.FlatNormal);
        Solid(vk, bindless, up, 128, 128, 128, 255, Bindless.Grey);

        // BRDF LUT: R16G16 float, 128x128, 1024 samples like the viewer's generator
        const int bs = 128;
        var brdf = new byte[bs * bs * 4];
        Parallel.For(0, bs, y =>
        {
            for (int x = 0; x < bs; x++)
            {
                float nov = (x + 0.5f) / bs;
                float rough = 1f - (y + 0.5f) / bs;
                var (a, b) = IntegrateBrdf(Math.Max(nov, 1e-4f), rough);
                int o = (y * bs + x) * 4;
                BitConverter.TryWriteBytes(brdf.AsSpan(o), (Half)a);
                BitConverter.TryWriteBytes(brdf.AsSpan(o + 2), (Half)b);
            }
        });
        // shader samples vec2(NoV, roughness) with roughness = 1 - perceptualRough; the viewer writes
        // BRDF(uv.s, 1 - uv.t), so texel row t holds roughness 1 - t
        Upload(vk, bindless, up, bs, bs, Format.R16G16Sfloat, brdf, 4, bindless.ClampLinearNoMip);

        // Legacy normalized Blinn-Phong lookup: x = n.h, y = spec exponent fraction (RenderSpecularResX/Y, exponent)
        const int lx = 1024, ly = 256;
        const float specExp = 368f;
        var ls = new byte[lx * ly * 4];
        for (int y = 0; y < ly; y++)
            for (int x = 0; x < lx; x++)
            {
                float sa = (float)x / (lx - 1);
                float spec = (float)y / (ly - 1);
                float n = spec * spec * specExp;
                spec = MathF.Pow(sa, n);
                spec *= ((n + 2) * (n + 4)) / (8 * MathF.PI * (MathF.Pow(2, -n / 2) + n));
                BitConverter.TryWriteBytes(ls.AsSpan((y * lx + x) * 4), spec);
            }
        Upload(vk, bindless, up, lx, ly, Format.R32Sfloat, ls, 4, bindless.ClampLinearNoMip);

        // slot 6: SSAO noise (LLPipeline mNoiseMap: 128x128 random unit xy, z jitter), point-sampled, repeating
        const int nr = 128;
        var rng = new Random(1);
        var noise = new byte[nr * nr * 8];
        for (int i = 0; i < nr * nr; i++)
        {
            float x = (float)rng.NextDouble() - 0.5f, y = (float)rng.NextDouble() - 0.5f;
            float l = MathF.Max(1e-6f, MathF.Sqrt(x * x + y * y));
            float z = (float)rng.NextDouble() * 1f + 1f - 0.5f;   // scaler 1: ll_frand()*scaler + 1 - scaler/2
            BitConverter.TryWriteBytes(noise.AsSpan(i * 8), (Half)(x / l));
            BitConverter.TryWriteBytes(noise.AsSpan(i * 8 + 2), (Half)(y / l));
            BitConverter.TryWriteBytes(noise.AsSpan(i * 8 + 4), (Half)z);
            BitConverter.TryWriteBytes(noise.AsSpan(i * 8 + 6), (Half)1f);
        }
        Upload(vk, bindless, up, nr, nr, Format.R16G16B16A16Sfloat, noise, 8, bindless.NearestRepeat);

        // the viewer's mTrueNoiseMap: 128x128 RGB16F of ll_frand()*2-1, point-sampled (glow extract dithering)
        var trueNoise = new byte[nr * nr * 8];
        for (int i = 0; i < nr * nr * 4; i++)
            BitConverter.TryWriteBytes(trueNoise.AsSpan(i * 2), (Half)((i & 3) == 3 ? 1f : (float)rng.NextDouble() * 2f - 1f));
        TrueNoise = Upload(vk, bindless, up, nr, nr, Format.R16G16B16A16Sfloat, trueNoise, 8, bindless.NearestRepeat);

        // SMAA area (RG8 160x560) and search (R8 64x16) textures, the viewer's SMAAAreaTex.h / SMAASearchTex.h
        // bytes, bilinear and clamped; top row first, as Vulkan samples them (the viewer flips both rows and v)
        var dir = Path.Combine(shaderDir, "smaa");
        SmaaArea = Upload(vk, bindless, up, 160, 560, Format.R8G8Unorm, Gunzip(Path.Combine(dir, "area.bin.gz")), 2, bindless.ClampLinearNoMip);
        SmaaSearch = Upload(vk, bindless, up, 64, 16, Format.R8Unorm, Gunzip(Path.Combine(dir, "search.bin.gz")), 1, bindless.ClampLinearNoMip);
    }

    public int SmaaArea { get; }
    public int TrueNoise { get; }
    public int SmaaSearch { get; }

    static byte[] Gunzip(string path)
    {
        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(path), System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    void Solid(VulkanContext vk, Bindless bindless, Uploader up, byte r, byte g, byte b, byte a, int expectSlot)
    {
        var data = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++) { data[i * 4] = r; data[i * 4 + 1] = g; data[i * 4 + 2] = b; data[i * 4 + 3] = a; }
        int slot = Upload(vk, bindless, up, 4, 4, Format.R8G8B8A8Unorm, data, 4, bindless.Repeat);
        if (slot != expectSlot) throw new InvalidOperationException("fixed bindless slots must be allocated first");
    }

    int Upload(VulkanContext vk, Bindless bindless, Uploader up, int w, int h, Format f, byte[] data, int bpp, Sampler sampler)
    {
        var img = new GpuImage(vk, (uint)w, (uint)h, f, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        up.CopyToImage(img, new[] { data });
        int slot = bindless.AllocTexture();
        bindless.Write(slot, img.View, sampler);
        _images.Add(img);
        return slot;
    }

    // Port of genbrdflutF.glsl's BRDF() (Sascha Willems, MIT), including its tiny phi jitter
    static (float, float) IntegrateBrdf(float nov, float roughness)
    {
        const uint N = 1024;
        float vx = MathF.Sqrt(1f - nov * nov), vz = nov;
        float a = 0, b = 0;
        float alpha = roughness * roughness;
        // random(normal.xz) with normal = (0,0,1): dot((0,1),(12.9898,78.233)) = 78.233
        float jitter = Fract(MathF.Sin(78.233f % 3.14f) * 43758.5453f) * 0.1f;
        for (uint i = 0; i < N; i++)
        {
            uint bits = (i << 16) | (i >> 16);
            bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
            bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
            bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
            bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
            float xi0 = (float)i / N, xi1 = bits * 2.3283064365386963e-10f;
            float phi = 2f * MathF.PI * xi0 + jitter;
            float cosT = MathF.Sqrt((1f - xi1) / (1f + (alpha * alpha - 1f) * xi1));
            float sinT = MathF.Sqrt(1f - cosT * cosT);
            // tangent frame for N = (0,0,1): up = (1,0,0) since |N.z| >= 0.999 -> tangentX = (0,1,0)? follow the shader:
            // up = (1,0,0); tangentX = normalize(cross(up, N)) = (0,-1,0); tangentY = cross(N, tangentX) = (1,0,0)
            float hx = sinT * MathF.Cos(phi), hy = sinT * MathF.Sin(phi), hz = cosT;
            float Hx = hy * 1f, Hy = -hx, Hz = hz;   // tangentX*H.x + tangentY*H.y + N*H.z
            float vdh = vx * Hx + vz * Hz;
            float Lx = 2 * vdh * Hx - vx, Lz = 2 * vdh * Hz - vz;
            float nol = MathF.Max(Lz, 0), nov2 = MathF.Max(vz, 0), voh = MathF.Max(vdh, 0), noh = MathF.Max(Hz, 0);
            if (nol > 0)
            {
                float k = roughness * roughness / 2f;
                float g = nol / (nol * (1 - k) + k) * (nov2 / (nov2 * (1 - k) + k));
                float gvis = g * voh / (noh * nov2);
                float fc = MathF.Pow(1 - voh, 5);
                a += (1 - fc) * gvis;
                b += fc * gvis;
            }
            _ = Lx;
        }
        return (a / N, b / N);
    }

    static float Fract(float x) => x - MathF.Floor(x);

    public void Dispose()
    {
        foreach (var i in _images) i.Dispose();
    }
}
