#version 460
// Luminance sample for dynamic exposure. Ported from the Second Life viewer's class1/deferred/luminanceF.glsl
// (LGPL 2.1, Linden Research, Inc.). pc.i1 = HDR scene slot, pc.i2 = glow slot, pc.i3 = G-buffer normal slot.
#include "common.glsl"
#include "gbuffer_util.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

float lum(vec3 col)
{
    vec3 l = vec3(0.2126, 0.7152, 0.0722);
    return dot(l, col);
}

void main()
{
    vec2 tc = vary_fragcoord*0.6+0.2;
    tc.y += 0.1; // HACK - nudge exposure sample down a little bit to favor ground over sky (our v runs top-down)
    vec3 c = texture(textures[nonuniformEXT(int(pc.i1))], tc).rgb;

    vec4 norm = gbNormIn(texture(textures[nonuniformEXT(int(pc.i3))], tc));

    if (!GET_GBUFFER_FLAG(norm.w, GBUFFER_FLAG_HAS_HDRI) &&
        !GET_GBUFFER_FLAG(norm.w, GBUFFER_FLAG_SKIP_ATMOS))
    {
        // Apply the diffuse luminance scale to objects but not the sky
        c *= SKY.diffuse_luminance_scale;
    }

    c += texture(textures[nonuniformEXT(int(pc.i2))], tc).rgb;

    float L = lum(c);
    frag_color = vec4(max(L, 0.0));
}
