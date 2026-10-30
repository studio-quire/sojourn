#version 460
// Dynamic exposure with history, once a frame: the viewer's class1/deferred/exposureF.glsl with USE_LAST_EXPOSURE
// (LGPL 2.1, Linden Research, Inc.). pc.i1 = luminance map slot (mip 8 is the average), pc.i2 = last frame's exposure,
// pc.v0 = (dt seconds, 1 when last frame's exposure is valid, adaptation speed per second, unused).
// Out: r = the exposure used this frame, g = the steady-state exposure for this view (what it adapts toward).
#include "common.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

void main()
{
    float L = textureLod(textures[nonuniformEXT(int(pc.i1))], vec2(0.5, 0.5), 8.0).r;
    float max_L = SKY.dynamic_exposure_coefficient;
    L = clamp(L, 0.0, max_L);
    L /= max_L;
    L = pow(L, 2.0);
    float target = mix(SKY.dynamic_exposure_max, SKY.dynamic_exposure_min, L);
    float s = target;
    if (pc.v0.y > 0.5)
    {
        float prev = texture(textures[nonuniformEXT(int(pc.i2))], vec2(0.5, 0.5)).r;
        s = mix(prev, target, 1.0 - exp(-pc.v0.z * pc.v0.x));
    }
    frag_color = max(vec4(s, target, 0.0, 1.0), vec4(0.0));
}
