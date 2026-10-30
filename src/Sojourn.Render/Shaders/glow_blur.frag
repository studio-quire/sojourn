#version 460
// One separable glow blur pass. Ported from the Second Life viewer's class1/effects/glowF.glsl / glowV.glsl
// (LGPL 2.1, Linden Research, Inc.). pc.v0.xy = glowDelta.
#include "common.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

const float glowStrength = 0.325;   // RenderGlowStrength

void main()
{
    int src = int(pc.i1);
    vec2 d = pc.v0.xy;
    vec2 t = vary_fragcoord;
    vec4 col = vec4(0.0);
    col += 0.25 * texture(textures[nonuniformEXT(src)], t + d * -3.5);
    col += 0.5  * texture(textures[nonuniformEXT(src)], t + d * -2.5);
    col += 0.8  * texture(textures[nonuniformEXT(src)], t + d * -1.5);
    col += 1.0  * texture(textures[nonuniformEXT(src)], t + d * -0.5);
    col += 1.0  * texture(textures[nonuniformEXT(src)], t + d * 0.5);
    col += 0.8  * texture(textures[nonuniformEXT(src)], t + d * 1.5);
    col += 0.5  * texture(textures[nonuniformEXT(src)], t + d * 2.5);
    col += 0.25 * texture(textures[nonuniformEXT(src)], t + d * 3.5);
    frag_color = max(vec4(col.rgb * glowStrength, col.a), vec4(0));
}
