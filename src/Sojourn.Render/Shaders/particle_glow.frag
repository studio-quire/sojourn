#version 460
// Particle glow into the scene's alpha channel (class1/deferred/emissiveF.glsl: texture alpha times the emissive
// vertex alpha). Ported from the Second Life viewer (LGPL 2.1, Linden Research, Inc.).
#include "common.glsl"

layout(location = 0) in vec3 vViewPos;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec4 vColor;
layout(location = 3) in float vGlow;
layout(location = 4) flat in uint vFlags;
layout(location = 5) flat in int vTex;
layout(location = 0) out vec4 frag_color;

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    float a = sampleTex(vTex, vUv, vec4(1)).a * vGlow;
    frag_color = max(vec4(0, 0, 0, a), vec4(0));
}
