#version 460
// Deferred local (point) lights as one fullscreen pass over every light in view.
// Ported from the Second Life viewer's class3/deferred/pointLightF.glsl / multiPointLightF.glsl
// (LGPL 2.1, Linden Research, Inc.). Spotlight projectors come later.
#include "common.glsl"
#include "deferred.glsl"
#include "pbr.glsl"
#include "locallights.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

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
        if (header != TILE_ALL && (header & 0xFFFFu) == 0u) discard;
    }
    vec3 pos = getPositionWithDepth(tc, depth).xyz;

    GBufferInfo gb = getGBuffer(tc);
    if (GET_GBUFFER_FLAG(gb.gbufferFlag, GBUFFER_FLAG_SKIP_ATMOS) || GET_GBUFFER_FLAG(gb.gbufferFlag, GBUFFER_FLAG_HAS_HDRI)) discard;
    frag_color.rgb = localLights(pos, gb, pc.i0);
    frag_color.a = 0.0;
}
