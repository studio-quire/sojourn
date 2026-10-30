#version 460
// starsF.glsl (deferred, HAS_EMISSIVE): added into the emissive target with BT_ADD_WITH_ALPHA. The viewer's depth
// test hides stars behind the sun quad and the moon's opaque texels, and its cloud pass then blends over them;
// both are applied here directly. Second Life viewer, LGPL 2.1, Linden Research, Inc.
#include "common.glsl"
#include "gbuffer_util.glsl"
#include "sky_color.glsl"
#include "clouds.glsl"
#include "heavenly.glsl"

layout(location = 0) in vec2 vary_texcoord0;
layout(location = 1) in vec3 vertex_color;
layout(location = 2) in vec2 screenpos;
layout(location = 3) in vec3 vary_dir;
layout(location = 0) out vec4 frag_data0;
layout(location = 1) out vec4 frag_data1;
layout(location = 2) out vec4 frag_data2;
layout(location = 3) out vec4 frag_data3;

float twinkle()
{
    float d = fract(screenpos.x + screenpos.y);
    return abs(d);
}

void main()
{
    vec3 d = normalize(vary_dir);
    if (starHidden(d)) discard;
    vec4 col = texture(textures[nonuniformEXT(SKY.star_tex)], vary_texcoord0);   // both of starsF's taps read unit 0
    col.rgb *= vertex_color.rgb;
    float factor = smoothstep(0.0f, 0.9f, SKY.star_brightness);
    col.a = (col.a * factor) * 32.0f;
    col.a *= twinkle();
    vec3 dp = domePos(d);
    col.a *= 1.0 - cloudColor(dp, normalize(dp)).a;   // dst' = mix(dst + star, cloud, a)
    frag_data0 = vec4(0);
    frag_data1 = vec4(0);
    frag_data2 = gbNormOut(vec4(0));
    frag_data3 = col;
}
