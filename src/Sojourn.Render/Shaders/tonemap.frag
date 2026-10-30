#version 460
// Exposure + tonemap + gamma. Ported from the Second Life viewer's class1/deferred/postDeferredTonemap.glsl and
// tonemapUtilF.glsl (LGPL 2.1, Linden Research, Inc.). Alpha (the glow mask) passes through.
#include "common.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

const mat3 ACESInputMat = mat3
(
    0.59719, 0.07600, 0.02840,
    0.35458, 0.90834, 0.13383,
    0.04823, 0.01566, 0.83777
);

const mat3 ACESOutputMat = mat3
(
    1.60475, -0.10208, -0.00327,
    -0.53108,  1.10813, -0.07276,
    -0.07367, -0.00605,  1.07602
);

vec3 RRTAndODTFit(vec3 color)
{
    vec3 a = color * (color + 0.0245786) - 0.000090537;
    vec3 b = color * (0.983729 * color + 0.4329510) + 0.238081;
    return a / b;
}

vec3 toneMapACES_Hill(vec3 color)
{
    color = ACESInputMat * color;
    color = RRTAndODTFit(color);
    color = ACESOutputMat * color;
    color = clamp(color, 0.0, 1.0);
    return color;
}

vec3 PBRNeutralToneMapping( vec3 color )
{
  const float startCompression = 0.8 - 0.04;
  const float desaturation = 0.15;
  float x = min(color.r, min(color.g, color.b));
  float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
  color -= offset;
  float peak = max(color.r, max(color.g, color.b));
  if (peak < startCompression) return color;
  const float d = 1. - startCompression;
  float newPeak = 1. - d * d / (peak + d - startCompression);
  color *= newPeak / peak;
  float g = 1. - 1. / (desaturation * (peak - newPeak) + 1.);
  return mix(color, newPeak * vec3(1, 1, 1), g);
}

// the viewer's exposureMap: this frame's exposure (exposure.frag, adapted over time, the same for both eyes)
float exposureScale()
{
    int expSlot = int(pc.i2);
    if (expSlot == 0xFFFF) return 1.0;
    return texture(textures[nonuniformEXT(expSlot)], vec2(0.5, 0.5)).r;
}

vec3 toneMap(vec3 color)
{
    vec3 linear_input_color = color;
    float final_exposure = SKY.exposure * exposureScale();
    vec3 exposed_color = color * final_exposure;
    vec3 tonemapped_color = exposed_color;
    switch(SKY.tonemap_type)
    {
    case 0:
        tonemapped_color = PBRNeutralToneMapping(exposed_color);
        break;
    case 1:
        tonemapped_color = toneMapACES_Hill(exposed_color);
        break;
    }
    vec3 exposed_linear_input = linear_input_color * final_exposure;
    color = mix(exposed_linear_input, tonemapped_color, SKY.tonemap_mix);
    color = clamp(color, 0.0, 1.0);
    return color;
}

// postDeferredGammaCorrect / LEGACY_GAMMA: the viewer passes the sky gamma "s/b inverted already CPU-side"
vec3 legacyGamma(vec3 color)
{
    vec3 c = 1. - clamp(color, vec3(0.), vec3(1.));
    c = 1. - pow(c, vec3(SKY.gamma));
    return c;
}

// postDeferredTonemap.glsl main with GAMMA_CORRECT; NO_POST and LEGACY_GAMMA are the variants LLPipeline::tonemap picks
void main()
{
    vec4 diff = texture(textures[nonuniformEXT(int(pc.i1))], vary_fragcoord);
    if (SKY.no_post != 0)
        diff.rgb = clamp(diff.rgb, vec3(0.0), vec3(1.0));
    else
        diff.rgb = toneMap(diff.rgb);
    if (SKY.cas_sharpness <= 0.0)
    {
        // GAMMA_CORRECT (with CAS on, applyCAS does this after sharpening)
        diff.rgb = linear_to_srgb(diff.rgb);
        if (SKY.legacy_gamma_no_post != 0)
            diff.rgb = legacyGamma(diff.rgb);
    }
    diff.rgb = clamp(diff.rgb, vec3(0.0), vec3(1.0));
    frag_color = diff;
}
