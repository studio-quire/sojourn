#version 460
// Glow combine into the final image. Ported from the Second Life viewer's class1/interface/glowcombineF.glsl
// (LGPL 2.1, Linden Research, Inc.).
#include "common.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

void main()
{
    vec4 c = texture(textures[nonuniformEXT(int(pc.i1))], vary_fragcoord)
           + texture(textures[nonuniformEXT(int(pc.i2))], vary_fragcoord);
    frag_color = vec4(clamp(c.rgb, 0.0, 1.0), 1.0);
}
