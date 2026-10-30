#version 460
// Separable gaussian blur of a probe face render before downsampling (pc.v0.xy = direction, pc.v0.z = resScale).
// Ported from the Second Life viewer's class1/interface/gaussianF.glsl (LGPL 2.1, Linden Research, Inc.).
#include "common.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

void main()
{
    vec3 col = vec3(0,0,0);
    float w[9] = float[9]( 0.0002, 0.0060, 0.0606, 0.2417, 0.3829, 0.2417, 0.0606, 0.0060, 0.0002 );
    for (int i = 0; i < 9; ++i)
    {
        vec2 tc = vary_fragcoord + (i-4)*pc.v0.xy*pc.v0.z;
        col += texture(textures[nonuniformEXT(int(pc.i1))], tc).rgb * w[i];
    }
    frag_color = max(vec4(col, 0.0), vec4(0));
}
