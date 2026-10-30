#version 460
// The water exclusion mask (Frame/Renderer.Water.cs, LLDrawPoolWaterExclusion::render): the water planes give it their
// depth (pc.v0.x 0), then the exclusion faces in front of them write 1 (pc.v0.x 1). Inverted from the viewer's (cleared
// to 1, faces black): water_mask.glsl reads it back.
#include "common.glsl"

layout(location = 0) out vec4 frag_mask;

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    frag_mask = vec4(pc.v0.x);
}
