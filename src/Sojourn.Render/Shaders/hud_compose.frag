#version 460
// A HUD image (premultiplied) over an eye's gamma-encoded output. pc.i0: its bindless slot; pc.i1 = 1: the image holds
// linear light (an sRGB image, sampled decoded), so it's put back into sRGB first; pc.i1 = 2: it holds sRGB bytes
// and the target linear light (HUD text into the headset's HUD image).
#include "common.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 frag_color;

void main()
{
    vec4 c = texture(textures[nonuniformEXT(pc.i0)], vUv);
    if (pc.i1 == 1u && c.a > 0.0) c.rgb = linear_to_srgb(c.rgb / c.a) * c.a;
    if (pc.i1 == 2u && c.a > 0.0) c.rgb = srgb_to_linear(c.rgb / c.a) * c.a;
    frag_color = c;
}
