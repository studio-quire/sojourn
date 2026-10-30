// Ported from the Second Life viewer: indra/newview/noise.h, noise.cpp.
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
namespace Sojourn.Render.TerrainGen;

/// <summary>
/// The viewer's Perlin noise (noise2/turbulence2), used for terrain composition and the alpha-ramp row.
/// The gradient tables come from srand(42) + rand(), so they depend on the C runtime: this reproduces
/// the MSVC CRT (the Windows viewer). Linux/macOS builds use glibc/libc rand and get different terrain
/// blending; Windows is what most residents see.
/// </summary>
public static class TerrainNoise
{
    private const int B = 0x100;
    private const float NF32 = 4096f;

    internal static readonly int[] P = new int[B + B + 2];
    internal static readonly float[] G2 = new float[(B + B + 2) * 2];

    static TerrainNoise()
    {
        // noise.h init(), with the MSVC rand() LCG (per-thread _holdrand; srand(42) sets it).
        uint holdrand = 42;
        int Rand()
        {
            holdrand = holdrand * 214013u + 2531011u;
            return (int)((holdrand >> 16) & 0x7fff);
        }

        int i, j, k;
        for (i = 0; i < B; i++)
        {
            P[i] = i;

            Rand(); // g1[i]

            for (j = 0; j < 2; j++)
                G2[i * 2 + j] = (float)((Rand() % (B + B)) - B) / B;
            float s = 1f / MathF.Sqrt(G2[i * 2] * G2[i * 2] + G2[i * 2 + 1] * G2[i * 2 + 1]);
            G2[i * 2] *= s;
            G2[i * 2 + 1] *= s;

            for (j = 0; j < 3; j++)
                Rand(); // g3[i][j]
        }

        while (--i != 0)
        {
            k = P[i];
            j = Rand() % B;
            P[i] = P[j];
            P[j] = k;
        }

        for (i = 0; i < B + 2; i++)
        {
            P[B + i] = P[i];
            G2[(B + i) * 2] = G2[i * 2];
            G2[(B + i) * 2 + 1] = G2[i * 2 + 1];
        }
    }

    // noise.h fast_setup
    private static void FastSetup(float vec, out int b0, out int b1, out float r0, out float r1)
    {
        r1 = vec + NF32;
        int t = (int)r1; // lltrunc
        b0 = t & 0xff;
        b1 = (b0 + 1) & 0xff;
        r0 = r1 - t;
        r1 = r0 - 1f;
    }

    private static float SCurve(float t) => t * t * (3f - 2f * t);

    private static float LerpM(float t, float a, float b) => a + t * (b - a);

    /// <summary>noise.cpp noise2(vec).</summary>
    public static float Noise2(float x, float y)
    {
        FastSetup(x, out int bx0, out int bx1, out float rx0, out float rx1);
        FastSetup(y, out int by0, out int by1, out float ry0, out float ry1);

        int i = P[bx0];
        int j = P[bx1];

        int b00 = P[i + by0];
        int b10 = P[j + by0];
        int b01 = P[i + by1];
        int b11 = P[j + by1];

        float sx = SCurve(rx0);
        float sy = SCurve(ry0);

        float u = rx0 * G2[b00 * 2] + ry0 * G2[b00 * 2 + 1];
        float v = rx1 * G2[b10 * 2] + ry0 * G2[b10 * 2 + 1];
        float a = LerpM(sx, u, v);

        u = rx0 * G2[b01 * 2] + ry1 * G2[b01 * 2 + 1];
        v = rx1 * G2[b11 * 2] + ry1 * G2[b11 * 2 + 1];
        float b = LerpM(sx, u, v);

        return LerpM(sy, a, b);
    }

    /// <summary>noise.h turbulence2(v, freq).</summary>
    public static float Turbulence2(float x, float y, float freq)
    {
        float t;
        for (t = 0f; freq >= 1f; freq *= 0.5f)
            t += Noise2(freq * x, freq * y) / freq;
        return t;
    }
}
