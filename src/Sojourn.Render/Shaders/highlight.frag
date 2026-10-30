#version 460
// Highlighted faces over an eye's finished image (Frame/Renderer.Beacons.cs): the viewer's highlight shader, its
// colour times a texture in the face's own coordinates (Highlight Transparent's smoke image; none for a beacon's flat
// red), alpha-blended. Ported from class1/interface/highlightF.glsl (LGPL 2.1, Linden Research, Inc.).
#include "common.glsl"

layout(location = 3) in vec2 vUv;
layout(location = 0) out vec4 frag_color;

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    frag_color = max(pc.v0 * sampleTex(int(pc.i0), vUv, vec4(1)), vec4(0));
}
