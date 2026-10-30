#version 460
// Deferred projector (spot) lights: one fullscreen pass over every projector in view, each evaluated as the
// viewer's spotLightF does. Ported from the Second Life viewer's class3/deferred/spotLightF.glsl and the projector
// helpers of class1/deferred/deferredUtil.glsl (LGPL 2.1, Linden Research, Inc.). The viewer draws a light box or a
// fullscreen triangle per projector; both clip to the light's sphere, so one loop gives the same result.
// Projector data is in region space (the draw list serves several views); the viewer's eye-space dot products and
// projections are the same in any rigid frame. pc.lightRef = projectors, pc.i0 = count.
#include "common.glsl"
#include "deferred.glsl"
#include "pbr.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

#include "projectors.glsl"

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    vec2 tc = vary_fragcoord;
    float depth = getDepth(tc);
    if (isSkyDepth(depth)) discard;
    // nothing of this pass reaches the pixel's tile: done before reading the G-buffer
    {
        TileListRef tiles;
        uint base;
        uint header = tileHeader(tiles, base);
        if (header != TILE_ALL && (header & 0xFFFF0000u) == 0u) discard;
    }
    vec3 posEye = getPositionWithDepth(tc, depth).xyz;

    GBufferInfo gb = getGBuffer(tc);
    if (GET_GBUFFER_FLAG(gb.gbufferFlag, GBUFFER_FLAG_SKIP_ATMOS) || GET_GBUFFER_FLAG(gb.gbufferFlag, GBUFFER_FLAG_HAS_HDRI)) discard;
    frag_color.rgb = projectorLights(tc, posEye, gb, ProjectorRef(uint64_t(pc.lightRef)), pc.i0);
    frag_color.a = 0.0;
}
