#version 460
// Property and ban lines as the viewer's UI shader draws them: the vertex colour times the texture (the ban tape),
// written as it is over the gamma-encoded image.
#include "common.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 2) flat in int vTex;
layout(location = 0) out vec4 frag_color;

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    frag_color = vColor * sampleTex(vTex, vUv, vec4(1));
}
