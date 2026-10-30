// Ported from the Second Life viewer: indra/newview/noise.h, noise.cpp.
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
namespace Sojourn.Render.Foliage;

/// <summary>
/// The viewer's 3D Perlin noise (noise3/turbulence3), which roughens tree trunks and branches. Gradient tables come
/// from srand(42) + rand(), so they depend on the C runtime: this reproduces the MSVC CRT (the Windows viewer), the
/// same tables TerrainGen's noise2 uses.
/// </summary>
internal static class FoliageNoise
{
    private const int B = 0x100;
    private const float NF32 = 4096f;

    private static readonly int[] P = new int[B + B + 2];
    private static readonly float[] G3 = new float[(B + B + 2) * 3];

    static FoliageNoise()
    {
        // noise.h init(), with the MSVC rand() LCG (srand(42) sets the per-thread _holdrand).
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
            Rand(); // g2[i][0]
            Rand(); // g2[i][1]

            for (j = 0; j < 3; j++)
                G3[i * 3 + j] = (float)((Rand() % (B + B)) - B) / B;
            float x = G3[i * 3], y = G3[i * 3 + 1], z = G3[i * 3 + 2];
            float s = 1f / MathF.Sqrt(x * x + y * y + z * z);
            G3[i * 3] = x * s;
            G3[i * 3 + 1] = y * s;
            G3[i * 3 + 2] = z * s;
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
            for (j = 0; j < 3; j++)
                G3[(B + i) * 3 + j] = G3[i * 3 + j];
        }
    }

    // noise.h fast_setup (U8 indices wrap at 256)
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

    private static float At3(float rx, float ry, float rz, int q) => rx * G3[q * 3] + ry * G3[q * 3 + 1] + rz * G3[q * 3 + 2];

    /// <summary>noise.h noise3(vec).</summary>
    public static float Noise3(float x, float y, float z)
    {
        FastSetup(x, out int bx0, out int bx1, out float rx0, out float rx1);
        FastSetup(y, out int by0, out int by1, out float ry0, out float ry1);
        FastSetup(z, out int bz0, out int bz1, out float rz0, out float rz1);

        int i = P[bx0];
        int j = P[bx1];

        int b00 = P[i + by0];
        int b10 = P[j + by0];
        int b01 = P[i + by1];
        int b11 = P[j + by1];

        float t = SCurve(rx0);
        float sy = SCurve(ry0);
        float sz = SCurve(rz0);

        float u = At3(rx0, ry0, rz0, b00 + bz0);
        float v = At3(rx1, ry0, rz0, b10 + bz0);
        float a = LerpM(t, u, v);

        u = At3(rx0, ry1, rz0, b01 + bz0);
        v = At3(rx1, ry1, rz0, b11 + bz0);
        float b = LerpM(t, u, v);

        float c = LerpM(sy, a, b);

        u = At3(rx0, ry0, rz1, b00 + bz1);
        v = At3(rx1, ry0, rz1, b10 + bz1);
        a = LerpM(t, u, v);

        u = At3(rx0, ry1, rz1, b01 + bz1);
        v = At3(rx1, ry1, rz1, b11 + bz1);
        b = LerpM(t, u, v);

        float d = LerpM(sy, a, b);

        return LerpM(sz, c, d);
    }

    /// <summary>noise.h turbulence3(v, freq).</summary>
    public static float Turbulence3(float x, float y, float z, float freq)
    {
        float t;
        for (t = 0f; freq >= 1f; freq *= 0.5f)
            t += Noise3(freq * x, freq * y, freq * z) / freq;
        return t;
    }
}
