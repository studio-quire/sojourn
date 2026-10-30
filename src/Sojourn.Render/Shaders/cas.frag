#version 460
// Contrast adaptive sharpening, sharpen-only path. Ported from the Second Life viewer's class1/deferred/CASF.glsl
// (AMD FidelityFX CAS, MIT; viewer wrapper LGPL 2.1, Linden Research, Inc.) with its defines: CAS_SLOW,
// CAS_BETTER_DIAGONALS, not CAS_GO_SLOWER (the approximate rcp/sqrt). Input: the tonemapped linear image
// (LLPipeline::tonemap with gamma_correct false); output: sRGB, plus legacy gamma for legacy skies (applyCAS).
// pc.i1 = source slot.
#include "common.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

float prxLoRcp(float a) { return uintBitsToFloat(0x7ef07ebbu - floatBitsToUint(a)); }
float prxLoSqrt(float a) { return uintBitsToFloat((floatBitsToUint(a) >> 1u) + 0x1fbc4639u); }
float prxMedRcp(float a) { float b = uintBitsToFloat(0x7ef19fffu - floatBitsToUint(a)); return b * (-b * a + 2.0); }
float casMin3(float x, float y, float z) { return min(x, min(y, z)); }
float casMax3(float x, float y, float z) { return max(x, max(y, z)); }

vec3 legacyGamma(vec3 color)
{
    vec3 c = 1. - clamp(color, vec3(0.), vec3(1.));
    c = 1. - pow(c, vec3(SKY.gamma));
    return c;
}

void main()
{
    int src = int(pc.i1);
    ivec2 size = textureSize(textures[nonuniformEXT(src)], 0);
    ivec2 sp = ivec2(vary_fragcoord * vec2(size));
    #define LOAD(dx, dy) texelFetch(textures[nonuniformEXT(src)], clamp(sp + ivec2(dx, dy), ivec2(0), size - 1), 0).rgb
    vec3 a = LOAD(-1, -1), b = LOAD(0, -1), c = LOAD(1, -1);
    vec3 d = LOAD(-1,  0), e = LOAD(0,  0), f = LOAD(1,  0);
    vec3 g = LOAD(-1,  1), h = LOAD(0,  1), i = LOAD(1,  1);

    float peak = -1.0 / mix(8.0, 5.0, clamp(SKY.cas_sharpness, 0.0, 1.0));
    vec3 pix;
    for (int ch = 0; ch < 3; ++ch)
    {
        // soft min and max (2x, better diagonals)
        float mn = casMin3(casMin3(d[ch], e[ch], f[ch]), b[ch], h[ch]);
        float mn2 = casMin3(casMin3(mn, a[ch], c[ch]), g[ch], i[ch]);
        mn += mn2;
        float mx = casMax3(casMax3(d[ch], e[ch], f[ch]), b[ch], h[ch]);
        float mx2 = casMax3(casMax3(mx, a[ch], c[ch]), g[ch], i[ch]);
        mx += mx2;
        float amp = clamp(min(mn, 2.0 - mx) * prxLoRcp(mx), 0.0, 1.0);
        amp = prxLoSqrt(amp);
        float w = amp * peak;
        float rcpWeight = prxMedRcp(1.0 + 4.0 * w);
        pix[ch] = clamp((b[ch] * w + d[ch] * w + f[ch] * w + h[ch] * w + e[ch]) * rcpWeight, 0.0, 1.0);
    }

    vec4 diff = vec4(pix, texture(textures[nonuniformEXT(src)], vary_fragcoord).a);
    diff.rgb = linear_to_srgb(diff.rgb);
    if (SKY.legacy_gamma_no_post != 0)
        diff.rgb = legacyGamma(diff.rgb);
    frag_color = diff;
}
