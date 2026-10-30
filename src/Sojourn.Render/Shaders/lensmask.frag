#version 460
// Marks a pixel of the lenses' hidden area (Frame/Renderer.Lens.cs), except where the luminance pass samples for
// dynamic exposure (pc.v1: the pixels kept, x0 y0 x1 y1).
#include "common.glsl"

layout(location = 0) out vec4 frag_mask;

void main()
{
    vec2 p = gl_FragCoord.xy;
    if (p.x >= pc.v1.x && p.y >= pc.v1.y && p.x <= pc.v1.z && p.y <= pc.v1.w) discard;
    frag_mask = vec4(1.0);
}
