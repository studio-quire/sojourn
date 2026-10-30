#version 460
// Depth/normal-aware blur of the direct-lighting light map, one direction per pass (pc.v0.xy = delta).
// Ported from the Second Life viewer's class1/deferred/blurLightF.glsl (LGPL 2.1, Linden Research, Inc.).
// The source light map's slot is in pc.i3 >> 16 (SLOT_LIGHTMAP).
#include "common.glsl"
#include "deferred.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

vec4 getPosition(vec2 tc) { return getPositionWithDepth(tc, max(getDepth(tc), 1e-7)); }

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    vec2 screen_res = VIEW.viewport.xy;
    vec3 kern[4] = vec3[4](SKY.blur_kern0.xyz, SKY.blur_kern1.xyz, SKY.blur_kern2.xyz, SKY.blur_kern3.xyz);
    float kern_scale = SKY.blur_kern_scale;
    float dist_factor = SKY.blur_dist_factor;
    vec2 delta = pc.v0.xy;

#ifdef HALF_LIGHT
    // half size (Renderer.HalfLight.cs): this texel was lit at the top left pixel of its 2x2 block; the taps keep their
    // spacing in the eye's pixels, so the blur covers what it does at full size
    vec2 tc = (floor(gl_FragCoord.xy) * 2.0 + 0.5) / screen_res;
#else
    vec2 tc = vary_fragcoord.xy;
#endif
    vec4 norm = getNorm(tc);
    vec3 pos = getPosition(tc).xyz;
    vec4 ccol = texture(textures[nonuniformEXT(SLOT_LIGHTMAP)], tc).rgba;

    vec2 dlt = kern_scale * delta / (1.0+norm.xy*norm.xy);
    dlt /= max(-pos.z*dist_factor, 1.0);

    vec2 defined_weight = kern[0].xy;
    vec4 col = defined_weight.xyxx * ccol;

    float pointplanedist_tolerance_pow2 = pos.z*pos.z*0.00005;

    tc *= screen_res;
    float tc_mod = 0.5*(gl_FragCoord.x + gl_FragCoord.y);   // the target's own pixels (at full size the same as tc's)
    tc_mod -= floor(tc_mod);
    tc_mod *= 2.0;
    tc += ( (tc_mod - 0.5) * kern[1].z * dlt * 0.5 );

    vec3 k[7];
    k[0] = kern[0];
    k[2] = kern[1];
    k[4] = kern[2];
    k[6] = kern[3];
    k[1] = (k[0]+k[2])*0.5f;
    k[3] = (k[2]+k[4])*0.5f;
    k[5] = (k[4]+k[6])*0.5f;

    for (int i = 1; i < 7; i++)
    {
        vec2 samptc = tc + k[i].z*dlt*2.0;
        samptc /= screen_res;
        vec3 samppos = getPosition(samptc).xyz;
        float d = dot(norm.xyz, samppos.xyz-pos.xyz);
        if (d*d <= pointplanedist_tolerance_pow2)
        {
            col += texture(textures[nonuniformEXT(SLOT_LIGHTMAP)], samptc)*k[i].xyxx;
            defined_weight += k[i].xy;
        }
    }
    for (int i = 1; i < 7; i++)
    {
        vec2 samptc = tc - k[i].z*dlt*2.0;
        samptc /= screen_res;
        vec3 samppos = getPosition(samptc).xyz;
        float d = dot(norm.xyz, samppos.xyz-pos.xyz);
        if (d*d <= pointplanedist_tolerance_pow2)
        {
            col += texture(textures[nonuniformEXT(SLOT_LIGHTMAP)], samptc)*k[i].xyxx;
            defined_weight += k[i].xy;
        }
    }

    col /= defined_weight.xyxx;
    frag_color = max(col, vec4(0));
}
