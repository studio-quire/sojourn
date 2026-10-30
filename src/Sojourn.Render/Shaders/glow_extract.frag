#version 460
// Ported from the Second Life viewer's class1/effects/glowExtractF.glsl, HAS_NOISE variant (LGPL 2.1, Linden
// Research, Inc.), with the pass's BT_ADD_WITH_ALPHA blend into a cleared RGBA8 target folded in: the fixed-point
// target clamps the source to [0,1] before blending, then rgb * a, a * a.
// pc.i1 = source (tonemapped, gamma-corrected scene; alpha = glow mask), pc.i2 = glow noise map.
#include "common.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

const float minLuminance = 9999.0;          // the viewer hardcodes this for the extract pass
const float maxExtractAlpha = 0.25;         // RenderGlowMaxExtractAlpha
const vec3 lumWeights = vec3(1.0, 0.0, 0.0);
const vec3 warmthWeights = vec3(1.0, 0.5, 0.7);
const float warmthAmount = 0.0;
const vec2 screen_res = vec2(512.0);        // the glow target

void main()
{
    vec4 col = texture(textures[nonuniformEXT(int(pc.i1))], vary_fragcoord);
    float lum = smoothstep(minLuminance, minLuminance+1.0, dot(col.rgb, lumWeights ) );
    float warmth = smoothstep(minLuminance, minLuminance+1.0, max(col.r * warmthWeights.r, max(col.g * warmthWeights.g, col.b * warmthWeights.b)) );
    vec3 glow_noise = texture(textures[nonuniformEXT(int(pc.i2))], vary_fragcoord * (screen_res / 128.0)).xyz;
    col.rgb += glow_noise / 64.0;
    col.rgb = max(col.rgb, vec3(0));
    float a = clamp(max(col.a, mix(lum, warmth, warmthAmount) * maxExtractAlpha), 0.0, 1.0);
    frag_color = vec4(clamp(col.rgb, 0.0, 1.0) * a, a * a);
}
